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

    // Ordered preferred-language cascade for an item:
    // 1. If PreferredLanguageOverride is set, parse it as a comma list.
    // 2. Otherwise auto-expand the item's library language (pt-BR -> [pt-br, pt]).
    protected static List<string> GetPreferredLanguages(BaseItem item)
    {
        var overrideList = LanguageMatching.ParseList(Config.PreferredLanguageOverride);
        return overrideList.Count > 0
            ? overrideList
            : LanguageMatching.ExpandLibraryLanguage(item.GetPreferredMetadataLanguage());
    }

    protected static List<string> GetFallbackLanguages()
        => LanguageMatching.ParseList(Config.FallbackLanguage);

    // The library's metadata language, the ONLY tag Jellyfin's downstream filter
    // accepts besides {empty, "en"}: GetImages keeps images whose Language is
    // empty, == GetPreferredMetadataLanguage(), or "en", then sorts by that same
    // language. We tag every image we want to keep with this so it survives the
    // filter and lands in the top language-score tier, regardless of any
    // PreferredLanguageOverride (the override only drives which TMDB images we
    // *request*, never what Jellyfin filters by). Empty when the library has no
    // metadata language, in which case Jellyfin skips the filter entirely.
    protected static string GetLibraryLanguageTag(BaseItem item)
        => NormaliseLanguage(item.GetPreferredMetadataLanguage());

    protected static string NormaliseLanguage(string? lang) => LanguageMatching.Normalise(lang);

    protected static bool IsTextlessAllowedFor(ImageType type) => type switch
    {
        ImageType.Primary => Config.IncludeNoLanguageForPosters,
        ImageType.Backdrop => Config.IncludeNoLanguageForBackdrops,
        ImageType.Logo => Config.IncludeNoLanguageForLogos,
        _ => false
    };

    protected static bool IsTextlessPreferredFor(ImageType type) => type switch
    {
        ImageType.Primary => Config.PreferNoLanguageForPosters,
        ImageType.Backdrop => Config.PreferNoLanguageForBackdrops,
        ImageType.Logo => Config.PreferNoLanguageForLogos,
        _ => false
    };

    protected static bool AnyTextlessAllowed() =>
        Config.IncludeNoLanguageForPosters
        || Config.IncludeNoLanguageForBackdrops
        || Config.IncludeNoLanguageForLogos;

    // True if any feature requires us to fetch the title's original_language
    // from TMDB. OnlyOriginalLanguageForPosters implies it.
    protected static bool NeedsOriginalLanguage() =>
        Config.IncludeOriginalLanguage || Config.OnlyOriginalLanguageForPosters;

    // Posters/backdrops/logos from one images call. Season calls leave
    // Backdrops/Logos null.
    protected readonly record struct MultiImages(
        IReadOnlyList<ImageData>? Posters,
        IReadOnlyList<ImageData>? Backdrops,
        IReadOnlyList<ImageData>? Logos);

    private static IReadOnlyList<ImageData>? SelectType(MultiImages images, ImageType type) => type switch
    {
        ImageType.Primary => images.Posters,
        ImageType.Backdrop => images.Backdrops,
        ImageType.Logo => images.Logos,
        _ => null
    };

    // Drives the include_image_language calls (one collective call for simple
    // codes + null, plus one per regional code) and returns ranked, tagged
    // RemoteImageInfos for each requested image type.
    protected async Task<List<RemoteImageInfo>> FetchRankMapAsync(
        BaseItem item,
        ImageType[] types,
        string originalLanguage,
        Func<string, CancellationToken, Task<MultiImages>> fetch,
        CancellationToken cancellationToken)
    {
        var preferred = GetPreferredLanguages(item);
        var fallback = GetFallbackLanguages();
        var tag = GetLibraryLanguageTag(item);
        var minVotes = Math.Max(0, Config.MinimumVoteCount);

        var normalBuckets = LanguageMatching.BuildOrderedBuckets(
            preferred, originalLanguage, Config.IncludeOriginalLanguage,
            Config.OriginalLanguageLast, fallback);

        // Per-type bucket lists. Posters honour the strict original-only mode.
        List<string> BucketsForType(ImageType type)
        {
            if (type == ImageType.Primary && Config.OnlyOriginalLanguageForPosters)
            {
                var orig = LanguageMatching.Normalise(originalLanguage);
                return string.IsNullOrEmpty(orig) ? new List<string>() : new List<string> { orig };
            }

            return normalBuckets;
        }

        // Union of every code any type needs -> drives which calls we make.
        var allCodes = new List<string>();
        foreach (var type in types)
        {
            foreach (var c in BucketsForType(type))
            {
                if (!allCodes.Contains(c))
                {
                    allCodes.Add(c);
                }
            }
        }

        if (allCodes.Count == 0)
        {
            return new List<RemoteImageInfo>();
        }

        var simpleCodes = allCodes.Where(c => !LanguageMatching.IsRegional(c)).ToList();
        var regionalCodes = allCodes.Where(LanguageMatching.IsRegional).ToList();
        var anyTextless = AnyTextlessAllowed();

        var calls = new List<(string Code, MultiImages Images)>();

        // Collective call: simple codes + textless. Skip if nothing simple and no
        // textless wanted.
        var collectiveParts = new List<string>(simpleCodes);
        if (anyTextless)
        {
            collectiveParts.Add("null");
        }

        if (collectiveParts.Count > 0)
        {
            var images = await fetch(string.Join(",", collectiveParts), cancellationToken).ConfigureAwait(false);
            calls.Add((string.Empty, images));
        }

        // One call per regional code (sent in canonical pt-BR form).
        foreach (var rc in regionalCodes)
        {
            var images = await fetch(LanguageMatching.ToTmdbLanguage(rc), cancellationToken).ConfigureAwait(false);
            calls.Add((rc, images));
        }

        var result = new List<RemoteImageInfo>();
        foreach (var type in types)
        {
            var buckets = BucketsForType(type);
            if (buckets.Count == 0)
            {
                continue;
            }

            int textlessRank;
            if (!IsTextlessAllowedFor(type))
            {
                textlessRank = int.MaxValue; // excluded for this type
            }
            else if (IsTextlessPreferredFor(type))
            {
                textlessRank = -1; // above every language bucket
            }
            else
            {
                textlessRank = buckets.Count; // dead-last (current behaviour)
            }
            var typeCalls = calls.Select(c => (c.Code, SelectType(c.Images, type)));
            var ranked = LanguageMatching.MergeAndRank(typeCalls, buckets, textlessRank, minVotes);

            foreach (var r in ranked)
            {
                result.Add(BuildRemoteImageInfo(type, r, tag, buckets.Count));
            }
        }

        if (Logger.IsEnabled(LogLevel.Debug) && result.Count > 0)
        {
            Logger.LogDebug(
                "LanguageAwareImages: {Count} images, top lang={Lang} url={Url}",
                result.Count, result[0].Language ?? "null", result[0].Url);
        }

        return result;
    }

    // Maps a ranked image to a RemoteImageInfo, encoding our bucket order so it
    // survives Jellyfin's downstream re-sort. Jellyfin's OrderByLanguageDescending
    // sorts by language-score, THEN CommunityRating, THEN VoteCount, and its
    // GetImages filter drops anything not tagged {empty, library-language, "en"}.
    //
    // So we tag every image with the library language (top tier, passes the
    // filter) and bake the bucket rank into CommunityRating: a lower rank maps to
    // a higher value, so rank 0 sorts above rank 1 above ... regardless of vote
    // counts. Within a rank the value is identical, leaving Jellyfin's VoteCount
    // tiebreak to order by popularity (SortByVotes on). With SortByVotes off we
    // add a small vote_average term so the within-bucket order is by rating
    // instead; that term is below the 0.1 granularity some Jellyfin builds round
    // CommunityRating to, so it degrades cleanly back to the VoteCount tiebreak.
    //
    // Cost: the picker shows these synthetic ratings instead of TMDB's, and every
    // image is labelled with the library language even when it is really a
    // regional variant or the original language. Both are accepted trade-offs for
    // exact ordering (see CLAUDE.md).
    private RemoteImageInfo BuildRemoteImageInfo(
        ImageType type,
        LanguageMatching.RankedImage ranked,
        string libraryTag,
        int maxRank)
    {
        var img = ranked.Image;

        double rating = maxRank + 1 - ranked.Rank;
        if (!Config.SortByVotes)
        {
            rating += Math.Clamp(img.VoteAverage, 0, 9.99) / 100.0;
        }

        return new RemoteImageInfo
        {
            ProviderName = Name,
            Type = type,
            Url = BuildImageUrl(type, img.FilePath),
            Width = img.Width,
            Height = img.Height,
            Language = string.IsNullOrEmpty(libraryTag) ? null : libraryTag,
            CommunityRating = rating,
            VoteCount = img.VoteCount,
            RatingType = RatingType.Score
        };
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return HttpClientFactory.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken);
    }
}
