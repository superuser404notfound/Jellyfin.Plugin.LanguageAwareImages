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
// The provider always returns the title-matched still when it finds one,
// tagged with the preferred language so it wins Jellyfin's downstream sort
// (OrderByLanguageDescending) over the built-in provider's positional still.
// For in-sync libraries the still happens to be the same image the built-in
// would have returned, just with a proper language tag so our entry ranks
// at the top of the image picker.
//
// One TMDB call per (show, language) covers all seasons; cached for the
// process lifetime.
public class LanguageAwareEpisodeImageProvider : LanguageAwareImageProviderBase, IRemoteImageProvider
{
    private record ShowEpisodeData(
        Dictionary<string, string> TitleToStill);

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

        var preferred = GetPreferredLanguages(item);
        var preferredLanguage = preferred.Count > 0 ? preferred[0] : string.Empty;
        var apiLanguageRaw = string.IsNullOrEmpty(preferredLanguage)
            ? (GetFallbackLanguages().FirstOrDefault() ?? string.Empty)
            : preferredLanguage;
        var apiLanguage = LanguageMatching.ToTmdbLanguage(apiLanguageRaw);

        var data = await GetOrBuildShowData(showId, apiLanguage, cancellationToken).ConfigureAwait(false);

        var localNormalised = NormaliseTitle(episode.Name);

        // Look up the still by title. Build a list of candidate titles, the
        // full title first and then each "/"-separated component, to cover
        // combined-episode shows like SpongeBob altdvd where one library
        // entry "Hey, dein Schuh ist offen / Der Stellvertreter" maps to two
        // separate TMDB single-sketch episodes; the first half is canonical
        // enough that its still works for the combined episode.
        var candidates = BuildTitleCandidates(episode.Name, localNormalised);

        string? stillPath = null;
        string? matchedTitle = null;
        foreach (var candidate in candidates)
        {
            if (data.TitleToStill.TryGetValue(candidate, out var found))
            {
                stillPath = found;
                matchedTitle = candidate;
                break;
            }
        }

        if (stillPath is null)
        {
            Logger.LogDebug(
                "LanguageAwareImages Episode: no title match for '{Title}' (tried {Count} candidates) in show {ShowId} ({Lang})",
                episode.Name, candidates.Count, showId, apiLanguage);
            return Array.Empty<RemoteImageInfo>();
        }

        // Tag the still with the library's metadata language so it survives
        // Jellyfin's downstream language filter (which allows {empty,
        // library-language, "en"}) AND ranks above the built-in provider's
        // wrong-position still in OrderByLanguageDescending. Episode stills are
        // language-agnostic images on TMDB; we only set Language here for
        // Jellyfin's pipeline arithmetic. Falls back to the configured fallback
        // language only when the library has no metadata language at all.
        var libraryTag = GetLibraryLanguageTag(item);
        var imageLanguage = !string.IsNullOrEmpty(libraryTag)
            ? libraryTag
            : (GetFallbackLanguages().FirstOrDefault());

        Logger.LogInformation(
            "LanguageAwareImages Episode: title match '{Title}' (matched candidate '{Candidate}') at S{S}E{E} (show {ShowId}, lang {Lang}) -> {Path}",
            episode.Name,
            matchedTitle,
            episode.ParentIndexNumber,
            episode.IndexNumber,
            showId,
            imageLanguage ?? "<null>",
            stillPath);

        return new[]
        {
            new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Primary,
                Url = BuildStillUrl(stillPath),
                Language = imageLanguage
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

        if (show?.Seasons is null)
        {
            var empty = new ShowEpisodeData(titleToStill);
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
                if (string.IsNullOrWhiteSpace(ep.Name) || string.IsNullOrWhiteSpace(ep.StillPath))
                {
                    continue;
                }

                var key = NormaliseTitle(ep.Name);

                // First match wins, duplicate titles within a show are rare
                // (recap/clip episodes mostly), and the first occurrence is
                // usually the canonical one.
                titleToStill.TryAdd(key, ep.StillPath);
            }
        }

        Logger.LogDebug(
            "LanguageAwareImages Episode: built data for show {ShowId} ({Lang}): {TitleCount} titles",
            showId, language, titleToStill.Count);

        var data = new ShowEpisodeData(titleToStill);
        ShowCache[cacheKey] = data;
        return data;
    }

    // Builds an ordered list of normalised titles to try against the TMDB
    // titleToStill dictionary. Order is significant: first match wins.
    //
    // 1. The full normalised title, in case the show legitimately has a "/"
    //    in a single episode's title.
    // 2. Each "/"-separated component, normalised. Covers combined-episode
    //    libraries (TVDB altdvd combined like "A / B", TMDB has A and B as
    //    separate single-sketch episodes), where the still for the first
    //    sketch is a reasonable image for the combined entry.
    //
    // Returns distinct entries only, in insertion order.
    private static IReadOnlyList<string> BuildTitleCandidates(string rawTitle, string normalisedFullTitle)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();

        void Add(string s)
        {
            if (!string.IsNullOrEmpty(s) && seen.Add(s))
            {
                ordered.Add(s);
            }
        }

        Add(normalisedFullTitle);

        if (rawTitle.Contains('/'))
        {
            foreach (var part in rawTitle.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                Add(NormaliseTitle(part));
            }
        }

        return ordered;
    }

    // Normalises titles for fuzzy-ish matching across providers:
    //   "Das Omelette" / "Omelette" / "OMELETTE!" → "omelette"
    // Strips a leading article (DE/EN), lowercases, drops whitespace and
    // punctuation. Survives most legitimate title variations between TVDB
    // and TMDB without resorting to full Levenshtein.
    private static readonly string[] LeadingArticles =
        { "der ", "die ", "das ", "den ", "dem ", "ein ", "eine ", "the ", "a ", "an " };

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
