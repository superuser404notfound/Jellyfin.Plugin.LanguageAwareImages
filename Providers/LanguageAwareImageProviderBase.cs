using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using TMDbLib.Client;
using TMDbLib.Objects.General;

namespace Jellyfin.Plugin.LanguageAwareImages.Providers;

public abstract class LanguageAwareImageProviderBase : IHasOrder
{
    // Public, well-known key that ships in Jellyfin's own source
    // (MediaBrowser.Providers/Plugins/Tmdb/TmdbUtils.cs). Used as a fallback
    // when the user hasn't provided their own.
    protected const string DefaultJellyfinTmdbKey = "4219e299c89411838049ab0dab19ebd5";

    private const string TmdbImageHost = "https://image.tmdb.org/t/p/";

    // Builds the TMDB image URL for a given image type using the size
    // configured by the user. Falls back to "original" if the type doesn't
    // map (shouldn't happen for the four types we support).
    protected static string BuildImageUrl(ImageType type, string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return string.Empty;
        }

        var size = type switch
        {
            ImageType.Primary => Config.PosterImageSize,
            ImageType.Backdrop => Config.BackdropImageSize,
            ImageType.Logo => Config.LogoImageSize,
            _ => "original"
        };

        if (string.IsNullOrWhiteSpace(size))
        {
            size = "original";
        }

        return TmdbImageHost + size + filePath;
    }

    // For episode stills (Still type isn't in Jellyfin's ImageType enum at the
    // poster/backdrop level, episode images use ImageType.Primary too, so we
    // expose this separately).
    protected static string BuildStillUrl(string filePath)
    {
        var size = string.IsNullOrWhiteSpace(Config.StillImageSize) ? "original" : Config.StillImageSize;
        return TmdbImageHost + size + filePath;
    }

    protected readonly IHttpClientFactory HttpClientFactory;
    protected readonly ILogger Logger;

    // One TMDbClient is cheap, but keeping it static enables the underlying
    // HttpClient to pool connections across calls. Rebuild only if the user
    // changes the API key in plugin config.
    private static readonly object ClientLock = new();
    private static TMDbClient? _sharedClient;
    private static string? _sharedKey;

    protected LanguageAwareImageProviderBase(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        HttpClientFactory = httpClientFactory;
        Logger = logger;
    }

    public string Name => "Language-Aware TMDB Images";

    // Lower than Jellyfin's bundled TmdbXxxImageProvider (Order = 0),
    // so this provider's results appear first.
    public int Order => -1;

    protected static Configuration.PluginConfiguration Config =>
        Plugin.Instance!.Configuration;

    protected static TMDbClient GetClient()
    {
        var desiredKey = string.IsNullOrWhiteSpace(Config.TmdbApiKey)
            ? DefaultJellyfinTmdbKey
            : Config.TmdbApiKey;

        lock (ClientLock)
        {
            if (_sharedClient is null || _sharedKey != desiredKey)
            {
                _sharedClient = new TMDbClient(desiredKey);
                _sharedKey = desiredKey;
            }

            return _sharedClient;
        }
    }

    // Resolves the effective preferred language for a given item:
    // 1. If the user set a global PreferredLanguageOverride, that wins.
    // 2. Otherwise, ask the item what its library/parent language is.
    // 3. Normalise to a 2-letter ISO 639-1 code (Jellyfin sometimes returns
    //    "en-US" style; TMDB filters expect plain "en").
    // Returns empty string if nothing is configured anywhere.
    protected static string GetEffectivePreferredLanguage(BaseItem item)
    {
        var lang = !string.IsNullOrWhiteSpace(Config.PreferredLanguageOverride)
            ? Config.PreferredLanguageOverride
            : item.GetPreferredMetadataLanguage();

        return NormaliseLanguage(lang);
    }

    protected static string NormaliseLanguage(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
        {
            return string.Empty;
        }

        // "en-US" -> "en", "de-DE" -> "de"
        var dash = lang.IndexOf('-');
        return (dash > 0 ? lang[..dash] : lang).ToLowerInvariant();
    }

    protected static bool IsTextlessAllowedFor(ImageType type) => type switch
    {
        ImageType.Primary => Config.IncludeNoLanguageForPosters,
        ImageType.Backdrop => Config.IncludeNoLanguageForBackdrops,
        ImageType.Logo => Config.IncludeNoLanguageForLogos,
        _ => false
    };

    protected static bool AnyTextlessAllowed() =>
        Config.IncludeNoLanguageForPosters
        || Config.IncludeNoLanguageForBackdrops
        || Config.IncludeNoLanguageForLogos;

    // TMDB's `include_image_language` accepts a comma list. The literal token
    // "null" pulls textless images. Order in the list does not affect ranking;
    // we apply our own bucket sort below.
    protected static string BuildIncludeLanguageParam(string preferredLanguage, string originalLanguage)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredLanguage))
        {
            parts.Add(preferredLanguage);
        }

        if (Config.IncludeOriginalLanguage && !string.IsNullOrWhiteSpace(originalLanguage))
        {
            parts.Add(originalLanguage);
        }

        if (!string.IsNullOrWhiteSpace(Config.FallbackLanguage))
        {
            parts.Add(Config.FallbackLanguage);
        }

        if (AnyTextlessAllowed())
        {
            parts.Add("null");
        }

        return string.Join(",", parts.Distinct());
    }

    // Heart of the plugin: filter by language bucket + min vote count, then
    // ORDER BY vote_count DESC, vote_average DESC, the same ordering TMDB's
    // own /images UI uses.
    //
    // Bucket ranks:
    //   0 - preferred language
    //   1 - original language (only if IncludeOriginalLanguage and != preferred/fallback)
    //   2 - fallback language
    //   3 - textless (null), only if allowed for this image type
    //  99 - excluded
    protected IEnumerable<RemoteImageInfo> RankAndMap(
        IEnumerable<ImageData>? images,
        ImageType type,
        string preferredLanguage,
        string originalLanguage)
    {
        if (images is null)
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var fallback = Config.FallbackLanguage;
        var includeTextless = IsTextlessAllowedFor(type);
        var includeOriginal = Config.IncludeOriginalLanguage
            && !string.IsNullOrEmpty(originalLanguage)
            && !string.Equals(originalLanguage, preferredLanguage, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(originalLanguage, fallback, StringComparison.OrdinalIgnoreCase);
        var minVotes = Math.Max(0, Config.MinimumVoteCount);

        int Rank(string? iso)
        {
            if (!string.IsNullOrEmpty(preferredLanguage)
                && string.Equals(iso, preferredLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (includeOriginal
                && string.Equals(iso, originalLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (!string.IsNullOrEmpty(fallback)
                && string.Equals(iso, fallback, StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            if (string.IsNullOrEmpty(iso) && includeTextless)
            {
                return 3;
            }

            return 99;
        }

        // CommunityRating is set null when SortByVotes is on, because Jellyfin's
        // downstream OrderByLanguageDescending sorts by CommunityRating before
        // VoteCount, so leaving rating in place would override our vote-count
        // primary sort with a rating primary sort. With CommunityRating null,
        // all entries tie at 0 and Jellyfin falls through to VoteCount.
        var sortByVotes = Config.SortByVotes;

        var ranked = images
            .Where(i => i.VoteCount >= minVotes)
            .Where(i => Rank(i.Iso_639_1) < 99)
            .OrderBy(i => Rank(i.Iso_639_1))
            .ThenByDescending(i => i.VoteCount)
            .ThenByDescending(i => i.VoteAverage)
            .Select(i => new RemoteImageInfo
            {
                ProviderName = Name,
                Type = type,
                Url = BuildImageUrl(type, i.FilePath),
                Width = i.Width,
                Height = i.Height,
                Language = DisguiseLanguage(i.Iso_639_1, preferredLanguage, fallback),
                CommunityRating = sortByVotes ? null : i.VoteAverage,
                VoteCount = i.VoteCount,
                RatingType = RatingType.Score
            })
            .ToList();

        if (Logger.IsEnabled(LogLevel.Debug) && ranked.Count > 0)
        {
            var top = ranked[0];
            Logger.LogDebug(
                "LanguageAwareImages: {Type} -> {Count} candidates, top lang={Lang} votes={Votes} url={Url}",
                type, ranked.Count, top.Language ?? "null", top.VoteCount, top.Url);
        }

        return ranked;
    }

    // Jellyfin's ProviderManager filters the returned RemoteImageInfo list down
    // to {empty | preferred | "en"} unless the UI's "All languages" toggle is
    // set, then sorts what's left with OrderByLanguageDescending(preferred),
    // which puts preferred first and English second. Both behaviors hide the
    // original-language bucket we worked to fetch (e.g. Japanese posters for
    // an anime in a German library).
    //
    // Workaround: for any image whose iso is neither preferred nor fallback
    // nor empty (i.e. it's an original-language entry), tag it with the
    // preferred code. The image survives the filter and lands in Jellyfin's
    // "preferred" sort bucket alongside real preferred matches; LINQ's stable
    // sort preserves our internal preferred → original → fallback order.
    //
    // Tradeoff: the UI labels a Japanese poster as "DE". Functionally right,
    // cosmetically white-lied.
    private static string? DisguiseLanguage(string? iso, string preferred, string fallback)
    {
        if (string.IsNullOrEmpty(iso))
        {
            return null;
        }

        if (string.Equals(iso, preferred, StringComparison.OrdinalIgnoreCase)
            || string.Equals(iso, fallback, StringComparison.OrdinalIgnoreCase))
        {
            return iso;
        }

        return string.IsNullOrEmpty(preferred) ? iso : preferred;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return HttpClientFactory.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken);
    }
}
