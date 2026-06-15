# Regional ISO 639 Variants + Language Priority List — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Serve region-correct TMDB images (e.g. `pt-BR` posters, not `pt`) and let users define an ordered language cascade (`pt-br → pt → pt-pt → en`), fixing issue #2.

**Architecture:** TMDB hides the region in the image `iso_639_1` field (always bare `pt`) but exposes it through the `include_image_language` filter. So a regional cascade requires one filter call per regional code, merging results by `file_path` (lowest rank wins). When the resolved cascade contains no regional codes, the existing single-call / client-side-rank path is kept unchanged (no regression for the majority). The non-obvious pure logic (normalisation, bucket ordering, file_path merge) is extracted into a Jellyfin-independent `LanguageMatching` static class.

**Tech Stack:** .NET 8, C#, TMDbLib 3.0.0, Jellyfin.Controller/Model 10.10.7 (reference-only). No test project (per decision) — verification is `dotnet build` + manual end-to-end refresh in Jellyfin with Debug logging.

**Branch:** `feature/regional-language-variants` (already created).

---

## Background facts (verified against the live TMDB API)

- Image `iso_639_1` is always the bare 2-letter code; the region is never in the response body.
- `include_image_language=pt-BR` returns the Brazilian set, `pt-PT` the Portuguese set (disjoint), `pt` the union. Confirmed on `tv/1399` (GoT): 5 / 5 / 10.
- The filter is case-insensitive (`pt-br` == `pt-BR` == `PT-BR`).
- One images call returns posters + backdrops + logos together.
- `pt-PT` can legitimately return zero images even when `pt` has some.

## File structure

- **Create** `Providers/LanguageMatching.cs` — pure, dependency-light helpers (only references `TMDbLib.Objects.General.ImageData`): normalisation, list parsing, library-language expansion, bucket ordering, and the multi-call merge/rank. This is the testable core, kept out of the Jellyfin-coupled providers.
- **Modify** `Providers/LanguageAwareImageProviderBase.cs` — replace `NormaliseLanguage`/`GetEffectivePreferredLanguage`/`BuildIncludeLanguageParam`/`RankAndMap` internals with calls into `LanguageMatching`; add the multi-call fetch driver `FetchRankMapAsync`; adjust language tagging for regional codes.
- **Modify** `Providers/LanguageAwareMovieImageProvider.cs`, `LanguageAwareSeriesImageProvider.cs`, `LanguageAwareSeasonImageProvider.cs` — call `FetchRankMapAsync` with their TMDbLib fetch method.
- **Modify** `Providers/LanguageAwareEpisodeImageProvider.cs` — pass the full regional locale as the TMDB `language` param.
- **Modify** `Configuration/PluginConfiguration.cs` — doc comments only (semantics now "comma-separated ordered list").
- **Modify** `Configuration/configPage.html` — drop `maxlength="2"`, update help text, keep `.toLowerCase()` (preserves hyphens/commas).
- **Modify** `README.md`, `Jellyfin.Plugin.LanguageAwareImages.csproj`, `meta.json` — docs + version bump.

---

## Task 1: Create the pure `LanguageMatching` helper class

**Files:**
- Create: `Providers/LanguageMatching.cs`

- [ ] **Step 1: Write the class**

```csharp
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
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build`
Expected: Build succeeds (new file compiles; nothing references it yet).

- [ ] **Step 3: Commit**

```bash
git add Providers/LanguageMatching.cs
git commit -m "feat: add region-aware LanguageMatching pure helpers

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Rework the base provider to use the cascade + multi-call fetch

**Files:**
- Modify: `Providers/LanguageAwareImageProviderBase.cs`

Context: today the base exposes `NormaliseLanguage`, `GetEffectivePreferredLanguage`, `IsTextlessAllowedFor`, `AnyTextlessAllowed`, `NeedsOriginalLanguage`, `BuildIncludeLanguageParam`, `RankAndMap`, `DisguiseLanguage`, `BuildImageUrl`, `BuildStillUrl`, `GetClient`, `GetImageResponse`. We keep the URL/client/textless/original helpers, replace the language-resolution + ranking ones, and add the fetch driver. `RankAndMap` and `BuildIncludeLanguageParam` are removed (callers move to `FetchRankMapAsync`); `NormaliseLanguage` delegates to `LanguageMatching.Normalise`.

- [ ] **Step 1: Replace `NormaliseLanguage` and `GetEffectivePreferredLanguage`**

Replace the `GetEffectivePreferredLanguage` method (lines ~106-113) and `NormaliseLanguage` (lines ~115-125) with:

```csharp
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

    // The exact string Jellyfin treats as the item's preferred language. Used to
    // tag our images so they survive Jellyfin's downstream {empty, preferred,
    // fallback} filter. When an override is set we fall back to the first override
    // code (best effort; Jellyfin still filters by the library language).
    protected static string GetPreferredTag(BaseItem item)
    {
        var overrideList = LanguageMatching.ParseList(Config.PreferredLanguageOverride);
        if (overrideList.Count > 0)
        {
            return overrideList[0];
        }

        var lib = item.GetPreferredMetadataLanguage();
        return string.IsNullOrWhiteSpace(lib) ? string.Empty : lib;
    }

    protected static string NormaliseLanguage(string? lang) => LanguageMatching.Normalise(lang);
```

- [ ] **Step 2: Delete `BuildIncludeLanguageParam` and `RankAndMap`**

Remove the entire `BuildIncludeLanguageParam` method (lines ~148-172) and the entire `RankAndMap` method (lines ~184-285). Keep `IsTextlessAllowedFor`, `AnyTextlessAllowed`, `NeedsOriginalLanguage`.

- [ ] **Step 3: Add the `MultiImages` carrier and `FetchRankMapAsync` driver**

Add inside the class (e.g. just above `GetImageResponse`):

```csharp
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
        var tag = GetPreferredTag(item);
        var minVotes = Math.Max(0, Config.MinimumVoteCount);

        var normalBuckets = LanguageMatching.BuildOrderedBuckets(
            preferred, originalLanguage, Config.IncludeOriginalLanguage, fallback);

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

            var textlessRank = IsTextlessAllowedFor(type) ? buckets.Count : int.MaxValue;
            var typeCalls = calls.Select(c => (c.Code, SelectType(c.Images, type)));
            var ranked = LanguageMatching.MergeAndRank(typeCalls, buckets, textlessRank, minVotes);

            foreach (var r in ranked)
            {
                result.Add(BuildRemoteImageInfo(type, r, buckets, tag, fallback));
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

    // Maps a ranked image to a RemoteImageInfo, tagging it so it survives
    // Jellyfin's downstream language filter. Fallback-language images keep their
    // own iso; textless stays null; everything else (preferred/original/regional,
    // whose iso TMDB reports as the bare code) is tagged with the preferred tag.
    private RemoteImageInfo BuildRemoteImageInfo(
        ImageType type,
        LanguageMatching.RankedImage ranked,
        IReadOnlyList<string> buckets,
        string preferredTag,
        IReadOnlyList<string> fallback)
    {
        var img = ranked.Image;
        var iso = LanguageMatching.Normalise(img.Iso_639_1);

        string? language;
        if (string.IsNullOrEmpty(iso))
        {
            language = null;
        }
        else if (fallback.Contains(iso))
        {
            language = iso;
        }
        else
        {
            language = string.IsNullOrEmpty(preferredTag) ? iso : preferredTag;
        }

        var sortByVotes = Config.SortByVotes;
        return new RemoteImageInfo
        {
            ProviderName = Name,
            Type = type,
            Url = BuildImageUrl(type, img.FilePath),
            Width = img.Width,
            Height = img.Height,
            Language = language,
            CommunityRating = sortByVotes ? null : img.VoteAverage,
            VoteCount = img.VoteCount,
            RatingType = RatingType.Score
        };
    }
```

- [ ] **Step 4: Verify it builds**

Run: `dotnet build`
Expected: FAILS — the Movie/Series/Season providers still call the removed `RankAndMap`/`BuildIncludeLanguageParam`/`GetEffectivePreferredLanguage`. That's expected; Tasks 3-5 fix the callers. (If any error is inside `LanguageAwareImageProviderBase.cs` itself, fix it before moving on.)

- [ ] **Step 5: Commit**

```bash
git add Providers/LanguageAwareImageProviderBase.cs
git commit -m "refactor: region-aware cascade + multi-call fetch in provider base

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Port the Movie provider

**Files:**
- Modify: `Providers/LanguageAwareMovieImageProvider.cs`

- [ ] **Step 1: Replace the `GetImages` body**

Replace the method body (lines ~29-69) with:

```csharp
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var tmdbIdRaw = item.GetProviderId(MetadataProvider.Tmdb);
        if (!int.TryParse(tmdbIdRaw, out var tmdbId))
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var preferred = GetPreferredLanguages(item);
        var apiLanguage = LanguageMatching.ToTmdbLanguage(
            preferred.Count > 0 ? preferred[0] : (GetFallbackLanguages().FirstOrDefault() ?? string.Empty));

        var client = GetClient();

        var originalLanguage = string.Empty;
        if (NeedsOriginalLanguage())
        {
            var movie = await client.GetMovieAsync(tmdbId, MovieMethods.Undefined, cancellationToken)
                .ConfigureAwait(false);
            originalLanguage = NormaliseLanguage(movie?.OriginalLanguage);
        }

        return await FetchRankMapAsync(
            item,
            new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Logo },
            originalLanguage,
            async (include, ct) =>
            {
                var images = await client.GetMovieImagesAsync(
                    tmdbId, language: apiLanguage, includeImageLanguage: include, cancellationToken: ct)
                    .ConfigureAwait(false);
                return new MultiImages(images?.Posters, images?.Backdrops, images?.Logos);
            },
            cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 2: Verify** — `dotnet build` (still fails on Series/Season; Movie file itself must be error-free).
- [ ] **Step 3: Commit** `git add Providers/LanguageAwareMovieImageProvider.cs && git commit -m "refactor: movie provider uses FetchRankMapAsync"`

---

## Task 4: Port the Series provider

**Files:**
- Modify: `Providers/LanguageAwareSeriesImageProvider.cs`

- [ ] **Step 1: Replace the `GetImages` body** (lines ~29-68) with the same shape as Movie, using the TV calls:

```csharp
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var tmdbIdRaw = item.GetProviderId(MetadataProvider.Tmdb);
        if (!int.TryParse(tmdbIdRaw, out var tmdbId))
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var preferred = GetPreferredLanguages(item);
        var apiLanguage = LanguageMatching.ToTmdbLanguage(
            preferred.Count > 0 ? preferred[0] : (GetFallbackLanguages().FirstOrDefault() ?? string.Empty));

        var client = GetClient();

        var originalLanguage = string.Empty;
        if (NeedsOriginalLanguage())
        {
            var show = await client.GetTvShowAsync(tmdbId, TvShowMethods.Undefined, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            originalLanguage = NormaliseLanguage(show?.OriginalLanguage);
        }

        return await FetchRankMapAsync(
            item,
            new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Logo },
            originalLanguage,
            async (include, ct) =>
            {
                var images = await client.GetTvShowImagesAsync(
                    tmdbId, language: apiLanguage, includeImageLanguage: include, cancellationToken: ct)
                    .ConfigureAwait(false);
                return new MultiImages(images?.Posters, images?.Backdrops, images?.Logos);
            },
            cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 2: Verify** — `dotnet build` (still fails on Season only).
- [ ] **Step 3: Commit** `git add Providers/LanguageAwareSeriesImageProvider.cs && git commit -m "refactor: series provider uses FetchRankMapAsync"`

---

## Task 5: Port the Season provider

**Files:**
- Modify: `Providers/LanguageAwareSeasonImageProvider.cs`

- [ ] **Step 1: Replace the `GetImages` body** (lines ~28-67) with (seasons have posters only; backdrops/logos are null):

```csharp
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Season season || season.IndexNumber is null)
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var seriesTmdbIdRaw = season.Series?.GetProviderId(MetadataProvider.Tmdb);
        if (!int.TryParse(seriesTmdbIdRaw, out var seriesTmdbId))
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var preferred = GetPreferredLanguages(item);
        var apiLanguage = LanguageMatching.ToTmdbLanguage(
            preferred.Count > 0 ? preferred[0] : (GetFallbackLanguages().FirstOrDefault() ?? string.Empty));

        var client = GetClient();

        var originalLanguage = string.Empty;
        if (NeedsOriginalLanguage())
        {
            var show = await client.GetTvShowAsync(seriesTmdbId, TvShowMethods.Undefined, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            originalLanguage = NormaliseLanguage(show?.OriginalLanguage);
        }

        return await FetchRankMapAsync(
            item,
            new[] { ImageType.Primary },
            originalLanguage,
            async (include, ct) =>
            {
                var images = await client.GetTvSeasonImagesAsync(
                    seriesTmdbId, season.IndexNumber.Value,
                    language: apiLanguage, includeImageLanguage: include, cancellationToken: ct)
                    .ConfigureAwait(false);
                return new MultiImages(images?.Posters, null, null);
            },
            cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 2: Verify** — `dotnet build`
Expected: Build SUCCEEDS (all callers ported).

- [ ] **Step 3: Commit** `git add Providers/LanguageAwareSeasonImageProvider.cs && git commit -m "refactor: season provider uses FetchRankMapAsync"`

---

## Task 6: Episode provider — pass the regional locale

**Files:**
- Modify: `Providers/LanguageAwareEpisodeImageProvider.cs`

Context: `GetImages` computes `preferredLanguage` then `apiLanguage` and passes it to `GetOrBuildShowData` as the TMDB `language`. With region-preserving normalisation, the first preferred code is now e.g. `pt-br`; canonicalise it to `pt-BR` so TMDB returns Brazilian episode titles.

- [ ] **Step 1: Update language resolution**

Replace these lines (~63-67):

```csharp
        var preferredLanguage = GetEffectivePreferredLanguage(item);
        var apiLanguage = string.IsNullOrEmpty(preferredLanguage)
            ? Config.FallbackLanguage
            : preferredLanguage;
```

with:

```csharp
        var preferred = GetPreferredLanguages(item);
        var preferredLanguage = preferred.Count > 0 ? preferred[0] : string.Empty;
        var apiLanguageRaw = string.IsNullOrEmpty(preferredLanguage)
            ? (GetFallbackLanguages().FirstOrDefault() ?? string.Empty)
            : preferredLanguage;
        var apiLanguage = LanguageMatching.ToTmdbLanguage(apiLanguageRaw);
```

- [ ] **Step 2: Confirm the still tag still works**

The existing `imageLanguage` block (~107-109) uses `preferredLanguage` then `Config.FallbackLanguage`. Leave its logic but make the fallback branch use the first fallback code:

Replace (~107-109):

```csharp
        var imageLanguage = !string.IsNullOrEmpty(preferredLanguage)
            ? preferredLanguage
            : (!string.IsNullOrEmpty(Config.FallbackLanguage) ? Config.FallbackLanguage : null);
```

with:

```csharp
        var imageLanguage = !string.IsNullOrEmpty(preferredLanguage)
            ? GetPreferredTag(item)
            : (GetFallbackLanguages().FirstOrDefault());
```

- [ ] **Step 3: Verify** — `dotnet build` (succeeds).
- [ ] **Step 4: Commit** `git add Providers/LanguageAwareEpisodeImageProvider.cs && git commit -m "feat: episode title lookup uses the regional locale"`

---

## Task 7: Config docs + admin page

**Files:**
- Modify: `Configuration/PluginConfiguration.cs`
- Modify: `Configuration/configPage.html`

- [ ] **Step 1: Update `PreferredLanguageOverride` doc comment** (lines ~7-11) to:

```csharp
    // Comma-separated, ordered priority list of ISO 639-1 codes, regional
    // variants allowed (e.g. "pt-br,pt,pt-pt"). A single code still works.
    // Empty (default) means: auto-derive from the item's library metadata
    // language, expanding a regional value (pt-BR -> [pt-br, pt]) before fallback.
    public string PreferredLanguageOverride { get; set; } = string.Empty;
```

- [ ] **Step 2: Update `FallbackLanguage` doc comment** (line ~13) to note it is also a comma list:

```csharp
    // Comma-separated fallback list, used when no preferred-language image exists
    // (e.g. "en" or "en,fr").
    public string FallbackLanguage { get; set; } = "en";
```

- [ ] **Step 3: Update the admin form fields**

In `Configuration/configPage.html`, for `#PreferredLanguageOverride` (line ~26) remove `maxlength="2"` and update its `fieldDescription` (lines ~27-31):

```html
                        <input is="emby-input" type="text" id="PreferredLanguageOverride" />
                        <div class="fieldDescription">
                            Leave empty (recommended) to use each library's metadata language,
                            auto-expanding a regional value (<code>pt-BR</code> &rarr;
                            <code>pt-br, pt</code>). Or set an ordered, comma-separated priority
                            list like <code>pt-br,pt,pt-pt</code> to force it for every library.
                        </div>
```

For `#FallbackLanguage` (line ~36) remove `maxlength="2"` and update its description (line ~37):

```html
                        <input is="emby-input" type="text" id="FallbackLanguage" required />
                        <div class="fieldDescription">Used when no preferred-language image exists. Comma-separated, e.g. <code>en</code> or <code>en,fr</code>.</div>
```

(The `.toLowerCase()` in the submit handler is correct — it preserves hyphens and commas.)

- [ ] **Step 4: Verify** — `dotnet build` (the html is an embedded resource; build confirms it's still included).
- [ ] **Step 5: Commit** `git add Configuration/PluginConfiguration.cs Configuration/configPage.html && git commit -m "docs: config now takes ordered language lists with regional variants"`

---

## Task 8: README + version bump + end-to-end verification

**Files:**
- Modify: `README.md`, `Jellyfin.Plugin.LanguageAwareImages.csproj`, `meta.json`

- [ ] **Step 1: Update the README config table** rows for `PreferredLanguageOverride` and `FallbackLanguage` (lines ~35-36) to describe ordered comma lists + regional variants, and add a short paragraph under "Posters / backdrops / logos" explaining regional matching (pt-BR vs pt-PT) and the extra-API-call cost.

```markdown
| `PreferredLanguageOverride`      | empty   | Empty = each library's language (a regional value like `pt-BR` auto-expands to `pt-br,pt`). Or an ordered list e.g. `pt-br,pt,pt-pt`. |
| `FallbackLanguage`               |  `en`   | Comma-separated fallback list, e.g. `en` or `en,fr`. Used when no preferred-language image exists.                        |
```

Add after the bucket-order paragraph (~line 53):

```markdown
**Regional variants.** TMDB tags some posters by region (`pt-BR` vs `pt-PT`)
but only exposes the region through its image-language *filter*, not the
response. The plugin therefore issues one TMDB call per regional code in your
cascade and merges by image, so a Brazilian library gets the `pt-br` poster
rather than the `pt` (Portugal) one. Plain 2-letter cascades keep the original
single-call behaviour.
```

- [ ] **Step 2: Bump the version** in `Jellyfin.Plugin.LanguageAwareImages.csproj` (lines 8-9) `0.7.4.0` -> `0.8.0.0` (both `AssemblyVersion` and `FileVersion`).

- [ ] **Step 3: Bump `meta.json`** `version` to `0.8.0.0` and replace `changelog` with:

```
Regional ISO 639 variants and ordered language cascades. Preferred/fallback are now comma-separated ordered lists (e.g. pt-br,pt,pt-pt), regional variants supported. A regional library language (pt-BR) auto-expands to pt-br,pt. Posters/backdrops/logos in a regional variant are matched via per-region TMDB filter calls; episode titles are looked up in the regional locale. Fixes #2.
```

- [ ] **Step 4: Build + install + end-to-end check**

```bash
dotnet build
./build.sh
```

Then, in Jellyfin (enable Debug logging for `Jellyfin.Plugin.LanguageAwareImages`):
1. Set a library's metadata language to `pt-BR`.
2. Refresh metadata (replacing images) on "Totally Spies! The Movie" (TMDB movie 74785) or "Game of Thrones" (TMDB tv 1399).
3. Open the item's image picker → "Language-Aware TMDB Images" entries.

Expected:
- The top poster is the Brazilian (`pt-br`) one, ranking above the `pt`/`en` options.
- Debug log shows multiple include_image_language calls (one collective, one per regional code) and a top entry tagged with the library language.
- **Critical filter check:** confirm the regional-tagged image actually appears (survives Jellyfin's `{empty, preferred, fallback}` filter). If it does not, the tag string in `GetPreferredTag` must match what Jellyfin expects — try the raw `GetPreferredMetadataLanguage()` value vs. a lowercased form, and re-test.
- Regression: set a library to plain `de`, refresh a German title, confirm behaviour is unchanged (single call, German poster first).

- [ ] **Step 5: Commit**

```bash
git add README.md Jellyfin.Plugin.LanguageAwareImages.csproj meta.json
git commit -m "docs: README + bump to 0.8.0 for regional language variants

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-review notes (spec coverage)

- Regional poster matching → Tasks 1, 2 (MergeAndRank per-region calls), 3-5 (providers).
- Ordered priority list (preferred + fallback) → Task 1 (ParseList, BuildOrderedBuckets), Task 2 (GetPreferredLanguages/GetFallbackLanguages).
- Auto-expansion of regional library language → Task 1 (ExpandLibraryLanguage), Task 2.
- Original-language position unchanged → Task 1 (BuildOrderedBuckets inserts original between preferred and fallback).
- Episode titles in regional locale → Task 6.
- Jellyfin filter / tagging → Task 2 (BuildRemoteImageInfo) + Task 8 critical check.
- Config UI + docs + version → Tasks 7, 8.
- No-regional-code path stays single-call → Task 2 (collective call only when no regional codes present).
```
