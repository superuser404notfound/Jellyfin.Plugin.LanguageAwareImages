using TMDbLib.Objects.General;

namespace Jellyfin.Plugin.LanguageAwareImages.Providers;

// Pure, Jellyfin-independent language matching + ranking helpers.
// Region-aware: "pt-BR" is preserved as "pt-br" rather than collapsed to "pt",
// because TMDB only distinguishes regions through the include_image_language
// filter (see the plan's background facts).
public static class LanguageMatching
{
    // An image plus the bucket rank it was matched at (lower = higher priority).
    public readonly record struct RankedImage(int Rank, ImageData Image);

    // Lowercase + trim, region preserved. "pt-BR" -> "pt-br", "EN" -> "en".
    public static string Normalise(string? lang)
        => string.IsNullOrWhiteSpace(lang) ? string.Empty : lang.Trim().ToLowerInvariant();

    public static bool IsRegional(string code) => code.IndexOf('-') > 0;

    // "pt-br" -> "pt". Non-regional codes are returned unchanged.
    public static string BaseCode(string code)
    {
        var dash = code.IndexOf('-');
        return dash > 0 ? code[..dash] : code;
    }

    // Canonical TMDB locale for the `language` query param: "pt-br" -> "pt-BR".
    public static string ToTmdbLanguage(string code)
    {
        var dash = code.IndexOf('-');
        return dash > 0 ? code[..dash] + "-" + code[(dash + 1)..].ToUpperInvariant() : code;
    }

    // "pt-br, pt , PT-PT" -> [pt-br, pt, pt-pt]. Normalised, de-duplicated,
    // order preserved.
    public static List<string> ParseList(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var n = Normalise(part);
            if (n.Length > 0 && !result.Contains(n))
            {
                result.Add(n);
            }
        }

        return result;
    }

    // Auto-expand a library language: "pt-BR" -> [pt-br, pt]; "de" -> [de]; "" -> [].
    public static List<string> ExpandLibraryLanguage(string? libLang)
    {
        var result = new List<string>();
        var n = Normalise(libLang);
        if (n.Length == 0)
        {
            return result;
        }

        result.Add(n);
        if (IsRegional(n))
        {
            var b = BaseCode(n);
            if (b.Length > 0 && !result.Contains(b))
            {
                result.Add(b);
            }
        }

        return result;
    }

    // Ordered language buckets; the index of a code is its rank.
    // preferred... , (original if opted in and not already present), fallback...
    public static List<string> BuildOrderedBuckets(
        IReadOnlyList<string> preferred,
        string originalLanguage,
        bool includeOriginal,
        IReadOnlyList<string> fallback)
    {
        var buckets = new List<string>();

        void Add(string c)
        {
            if (!string.IsNullOrEmpty(c) && !buckets.Contains(c))
            {
                buckets.Add(c);
            }
        }

        foreach (var p in preferred)
        {
            Add(p);
        }

        if (includeOriginal)
        {
            Add(Normalise(originalLanguage));
        }

        foreach (var f in fallback)
        {
            Add(f);
        }

        return buckets;
    }

    // Merge images from one or more include_image_language calls and rank them.
    //
    // Each call is (code, images): an empty code is the "collective" call whose
    // images carry a real iso_639_1 and are ranked by it; a non-empty code is a
    // regional call whose images all take that code's rank (TMDB doesn't echo the
    // region, so the call itself is the only evidence). Lowest rank per file_path
    // wins, so a Brazilian poster returned by both the pt-br call (rank 0) and the
    // pt collective call (rank 1) keeps rank 0.
    //
    // `buckets` defines the rank space; `textlessRank` is where empty-iso images
    // land (pass int.MaxValue to exclude them for this image type).
    public static List<RankedImage> MergeAndRank(
        IEnumerable<(string Code, IReadOnlyList<ImageData>? Images)> calls,
        IReadOnlyList<string> buckets,
        int textlessRank,
        int minVotes)
    {
        var rankOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < buckets.Count; i++)
        {
            rankOf[buckets[i]] = i;
        }

        int RankFromIso(string? iso)
        {
            var n = Normalise(iso);
            if (n.Length == 0)
            {
                return textlessRank;
            }

            return rankOf.TryGetValue(n, out var r) ? r : int.MaxValue;
        }

        var best = new Dictionary<string, RankedImage>(StringComparer.Ordinal);

        foreach (var (code, images) in calls)
        {
            if (images is null)
            {
                continue;
            }

            var isRegional = !string.IsNullOrEmpty(code);
            var regionalRank = isRegional && rankOf.TryGetValue(code, out var rr) ? rr : int.MaxValue;

            foreach (var img in images)
            {
                if (string.IsNullOrEmpty(img.FilePath) || img.VoteCount < minVotes)
                {
                    continue;
                }

                var rank = isRegional ? regionalRank : RankFromIso(img.Iso_639_1);
                if (rank == int.MaxValue)
                {
                    continue;
                }

                if (!best.TryGetValue(img.FilePath, out var cur) || rank < cur.Rank)
                {
                    best[img.FilePath] = new RankedImage(rank, img);
                }
            }
        }

        return best.Values
            .OrderBy(r => r.Rank)
            .ThenByDescending(r => r.Image.VoteCount)
            .ThenByDescending(r => r.Image.VoteAverage)
            .ToList();
    }
}
