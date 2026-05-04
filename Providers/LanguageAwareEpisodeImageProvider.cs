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
// rather than by (Season, Episode) index. Sidesteps the mess of alternative
// orderings (Disney+, DVD, Production, regional) where TVDB and TMDB
// disagree on which episode lives at which position.
//
// Smart mode (always on, no toggle): we first check whether the TMDB title
// at the local (S, E) position matches the local title. If they match, the
// library is in sync with TMDB ordering, we return empty so the built-in
// provider can deliver its full image set unimpeded. We only inject our
// title-matched still when ordering actually differs.
//
// One TMDB call per (show, language) covers all seasons; cached for the
// process lifetime.
public class LanguageAwareEpisodeImageProvider : LanguageAwareImageProviderBase, IRemoteImageProvider
{
    private record ShowEpisodeData(
        Dictionary<string, string> TitleToStill,
        Dictionary<(int Season, int Episode), string> PositionToNormalisedTitle);

    private static readonly ConcurrentDictionary<string, ShowEpisodeData> ShowCache = new();

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

        var data = await GetOrBuildShowData(showId, apiLanguage, cancellationToken).ConfigureAwait(false);

        var localNormalised = NormaliseTitle(episode.Name);

        // Smart mode: if the TMDB title at the local (S, E) position already
        // matches the local title, library is in sync with TMDB ordering.
        // Return empty so the built-in TMDB image provider delivers its full
        // image set (multiple stills, alt crops etc.) for this episode.
        if (episode.ParentIndexNumber is int season
            && episode.IndexNumber is int epNum
            && data.PositionToNormalisedTitle.TryGetValue((season, epNum), out var tmdbTitleAtPos)
            && tmdbTitleAtPos == localNormalised)
        {
            Logger.LogDebug(
                "LanguageAwareImages Episode: '{Title}' is in sync at S{S}E{E} (show {ShowId}), deferring to built-in provider",
                episode.Name, season, epNum, showId);
            return Array.Empty<RemoteImageInfo>();
        }

        // Mismatch (or position unknown), library uses an alternative order.
        // Look up the still by title.
        if (!data.TitleToStill.TryGetValue(localNormalised, out var stillPath))
        {
            Logger.LogDebug(
                "LanguageAwareImages Episode: no title match for '{Title}' in show {ShowId} ({Lang})",
                episode.Name, showId, apiLanguage);
            return Array.Empty<RemoteImageInfo>();
        }

        Logger.LogDebug(
            "LanguageAwareImages Episode: alt-order match '{Title}' -> {Path} (show {ShowId})",
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

    private async Task<ShowEpisodeData> GetOrBuildShowData(
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

        var titleToStill = new Dictionary<string, string>(StringComparer.Ordinal);
        var positionToTitle = new Dictionary<(int, int), string>();

        if (show?.Seasons is null)
        {
            var empty = new ShowEpisodeData(titleToStill, positionToTitle);
            ShowCache[cacheKey] = empty;
            return empty;
        }

        // One API call per season (S0/specials excluded, they rarely have
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
                if (string.IsNullOrWhiteSpace(ep.Name))
                {
                    continue;
                }

                var key = NormaliseTitle(ep.Name);

                positionToTitle[(ep.SeasonNumber, (int)ep.EpisodeNumber)] = key;

                if (!string.IsNullOrWhiteSpace(ep.StillPath))
                {
                    // First match wins, duplicate titles within a show are rare
                    // (recap/clip episodes mostly), and the first occurrence is
                    // usually the canonical one.
                    titleToStill.TryAdd(key, ep.StillPath);
                }
            }
        }

        Logger.LogDebug(
            "LanguageAwareImages Episode: built data for show {ShowId} ({Lang}): {TitleCount} titles, {PosCount} positions",
            showId, language, titleToStill.Count, positionToTitle.Count);

        var data = new ShowEpisodeData(titleToStill, positionToTitle);
        ShowCache[cacheKey] = data;
        return data;
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
