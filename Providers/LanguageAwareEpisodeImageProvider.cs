using System.Collections.Concurrent;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using TvShowMethods = TMDbLib.Objects.TvShows.TvShowMethods;

namespace Jellyfin.Plugin.LanguageAwareImages.Providers;

// Episode-level image provider that looks up TMDB stills by episode TITLE
// rather than by (Season, Episode) index. This sidesteps the mess of
// alternative orderings (Disney+, DVD, Production, regional) where TVDB and
// TMDB disagree on which episode lives at which position.
//
// One API call per (show, language) on first encounter, cached per show.
public class LanguageAwareEpisodeImageProvider : LanguageAwareImageProviderBase, IRemoteImageProvider
{
    // (showId, language) → normalized-title → still_path
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> ShowCache = new();

    public LanguageAwareEpisodeImageProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<LanguageAwareEpisodeImageProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    public bool Supports(BaseItem item) => item is Episode;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary };

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        if (!Config.MatchEpisodeImagesByTitle)
        {
            return Array.Empty<RemoteImageInfo>();
        }

        if (item is not Episode episode || string.IsNullOrWhiteSpace(episode.Name))
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var seriesTmdbIdRaw = episode.Series?.GetProviderId(MetadataProvider.Tmdb);
        if (!int.TryParse(seriesTmdbIdRaw, out var showId))
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var preferredLanguage = GetEffectivePreferredLanguage(item);
        var apiLanguage = string.IsNullOrEmpty(preferredLanguage)
            ? Config.FallbackLanguage
            : preferredLanguage;

        var map = await GetOrBuildTitleMap(showId, apiLanguage, cancellationToken).ConfigureAwait(false);

        var key = NormaliseTitle(episode.Name);
        if (!map.TryGetValue(key, out var stillPath))
        {
            Logger.LogDebug(
                "LanguageAwareImages Episode: no title match for '{Title}' in show {ShowId} ({Lang})",
                episode.Name, showId, apiLanguage);
            return Array.Empty<RemoteImageInfo>();
        }

        Logger.LogDebug(
            "LanguageAwareImages Episode: matched '{Title}' -> {Path} (show {ShowId})",
            episode.Name, stillPath, showId);

        return new[]
        {
            new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Primary,
                Url = TmdbImageBaseUrl + stillPath
            }
        };
    }

    private async Task<IReadOnlyDictionary<string, string>> GetOrBuildTitleMap(
        int showId, string language, CancellationToken cancellationToken)
    {
        var cacheKey = $"{showId}-{language}";
        if (ShowCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var client = GetClient();
        var show = await client.GetTvShowAsync(
            showId, TvShowMethods.Undefined, language: language, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (show?.Seasons is null)
        {
            ShowCache[cacheKey] = map;
            return map;
        }

        // One API call per season (S0/specials excluded — those rarely have
        // alt-order issues and would just inflate the call count).
        foreach (var seasonInfo in show.Seasons.Where(s => s.SeasonNumber > 0))
        {
            var season = await client.GetTvSeasonAsync(
                showId, seasonInfo.SeasonNumber, language: language, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (season?.Episodes is null)
            {
                continue;
            }

            foreach (var ep in season.Episodes)
            {
                if (string.IsNullOrWhiteSpace(ep.Name) || string.IsNullOrWhiteSpace(ep.StillPath))
                {
                    continue;
                }

                var key = NormaliseTitle(ep.Name);
                // First match wins — duplicate titles within a show are rare
                // (recap/clip episodes mostly), and the first occurrence is
                // usually the canonical one.
                map.TryAdd(key, ep.StillPath);
            }
        }

        Logger.LogDebug(
            "LanguageAwareImages Episode: built title map for show {ShowId} ({Lang}) with {Count} entries",
            showId, language, map.Count);

        ShowCache[cacheKey] = map;
        return map;
    }

    // Normalises titles for fuzzy-ish matching across providers:
    //   "Das Omelette" / "Omelette" / "OMELETTE!" → "omelette"
    // Strips a leading article (DE/EN), lowercases, drops whitespace and
    // punctuation. Survives most legitimate title variations between TVDB
    // and TMDB without resorting to full Levenshtein.
    private static readonly string[] LeadingArticles =
        { "der ", "die ", "das ", "den ", "dem ", "the ", "a ", "an " };

    private static string NormaliseTitle(string title)
    {
        var t = title.Trim().ToLowerInvariant();

        foreach (var prefix in LeadingArticles)
        {
            if (t.StartsWith(prefix, StringComparison.Ordinal))
            {
                t = t[prefix.Length..];
                break;
            }
        }

        var sb = new StringBuilder(t.Length);
        foreach (var c in t)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
