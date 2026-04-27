using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using TMDbLib.Client;
using TMDbLib.Objects.General;

namespace Jellyfin.Plugin.LanguageAwareImages.Providers;

public abstract class LanguageAwareImageProviderBase : IHasOrder
{
    // Public, well-known key that ships in Jellyfin's own source
    // (MediaBrowser.Providers/Plugins/Tmdb/TmdbUtils.cs). Used as a fallback
    // when the user hasn't provided their own.
    protected const string DefaultJellyfinTmdbKey = "4219e299c89411838049ab0dab19ebd5";

    protected const string TmdbImageBaseUrl = "https://image.tmdb.org/t/p/original";

    protected readonly IHttpClientFactory HttpClientFactory;

    protected LanguageAwareImageProviderBase(IHttpClientFactory httpClientFactory)
    {
        HttpClientFactory = httpClientFactory;
    }

    public string Name => "Language-Aware TMDB Images";

    // Lower than Jellyfin's bundled TmdbXxxImageProvider (Order = 0),
    // so this provider's results appear first.
    public int Order => -1;

    protected static Configuration.PluginConfiguration Config =>
        Plugin.Instance!.Configuration;

    protected TMDbClient CreateClient()
    {
        var key = string.IsNullOrWhiteSpace(Config.TmdbApiKey)
            ? DefaultJellyfinTmdbKey
            : Config.TmdbApiKey;
        return new TMDbClient(key);
    }

    // TMDB's `include_image_language` accepts a comma list. The literal token
    // "null" pulls textless images. Order in the list does not affect ranking;
    // we apply our own bucket sort below.
    protected string BuildIncludeLanguageParam()
    {
        var parts = new List<string> { Config.PreferredLanguage, Config.FallbackLanguage };
        if (Config.IncludeNoLanguage)
        {
            parts.Add("null");
        }

        return string.Join(",", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct());
    }

    // Heart of the plugin: filter by language bucket, then ORDER BY vote_count DESC,
    // vote_average DESC — the same ordering TMDB's own /images UI uses.
    protected IEnumerable<RemoteImageInfo> RankAndMap(
        IEnumerable<ImageData>? images,
        ImageType type)
    {
        if (images is null)
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var preferred = Config.PreferredLanguage;
        var fallback = Config.FallbackLanguage;
        var includeNull = Config.IncludeNoLanguage;

        int Rank(string? iso)
        {
            if (string.Equals(iso, preferred, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (string.Equals(iso, fallback, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (string.IsNullOrEmpty(iso) && includeNull)
            {
                return 2;
            }

            return 99;
        }

        return images
            .Where(i => Rank(i.Iso_639_1) < 99)
            .OrderBy(i => Rank(i.Iso_639_1))
            .ThenByDescending(i => i.VoteCount)
            .ThenByDescending(i => i.VoteAverage)
            .Select(i => new RemoteImageInfo
            {
                ProviderName = Name,
                Type = type,
                Url = TmdbImageBaseUrl + i.FilePath,
                Width = i.Width,
                Height = i.Height,
                Language = i.Iso_639_1,
                CommunityRating = i.VoteAverage,
                VoteCount = i.VoteCount,
                RatingType = RatingType.Score
            })
            .ToList();
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return HttpClientFactory.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken);
    }
}
