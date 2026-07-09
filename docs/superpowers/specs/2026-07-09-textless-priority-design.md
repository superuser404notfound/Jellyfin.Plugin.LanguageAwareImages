# Configurable textless priority + picker trade-off docs

**Issue:** [#3](https://github.com/superuser404notfound/jellyfin-plugin-language-aware-images/issues/3) — "Incorrect language tags in Edit Images dialog and unexpected backdrop selection"
**Date:** 2026-07-09
**Status:** Approved, ready for planning

## Context

A Polish-library user (`PreferredLanguageOverride=en`, `FallbackLanguage=en`,
textless allowed for backdrops) reports three symptoms:

1. **Backdrops pick titled English art over available textless versions.**
2. Every image in the *Edit Images* dialog is tagged with the library language
   (`pl`) even when it is really English or textless.
3. The dialog's **"All languages"** toggle has no effect.

Only symptom **1** is a defect. Symptoms **2** and **3** are the documented
consequence of the ranking workaround (`CLAUDE.md`): we tag every kept image
with the library metadata language so it survives Jellyfin's downstream
`GetImages` filter and lands in the top language-score tier. Tagging the real
language would make Jellyfin drop anything that is not `{empty, library-lang,
"en"}`. Because all our images already carry the library tag, the "All
languages" toggle (which only changes that same filter) is a no-op. Both are
accepted cosmetic costs of exact ordering, not bugs.

### Root cause of symptom 1

`FetchRankMapAsync` (`LanguageAwareImageProviderBase.cs`) computes:

```csharp
var textlessRank = IsTextlessAllowedFor(type) ? buckets.Count : int.MaxValue;
```

`buckets.Count` is always **one past the last language bucket**, i.e. dead-last.
For this user the only bucket is `["en"]`, so titled English (rank 0) always
beats textless (rank 1). There is no way to prefer textless. The config option
is named *"Allow textless"* but the user reads it as *"prefer textless"*, which
is the common wish for backdrops (Jellyfin overlays its own title anyway).

## Decisions (from brainstorming)

- **Scope: per image type.** Add textless-priority control for posters,
  backdrops and logos independently, mirroring the three existing
  `IncludeNoLanguageFor*` toggles. Posters usually want titled art; backdrops
  and logos usually want textless.
- **Config shape: three new bool fields.** `PreferNoLanguageFor{Posters,
  Backdrops,Logos}`, default `false`. The existing `IncludeNoLanguageFor*`
  bools stay 1:1 (no type change → no XML-deserialisation migration risk).
  Old configs keep today's behaviour.
- **Position when preferred: absolute top.** A preferred textless image ranks
  above *all* language buckets, including the preferred language. "Prefer"
  taken literally: if a textless image exists, use it.
- **Prefer implies Allow.** "Prefer" only has effect when the type's "Allow"
  toggle is on (otherwise textless is never even requested from TMDB). The UI
  disables the "Prefer" checkbox while "Allow" is off; the code needs no
  special-casing because an un-allowed textless image is excluded upstream.
- **Fold the picker trade-off docs into the same change.** Symptoms 2 and 3 are
  not code changes, but the README does not yet warn about them clearly, so the
  same PR adds findable documentation to prevent repeat reports.

## Design

### 1. `Configuration/PluginConfiguration.cs`
Add three bool fields next to the existing `IncludeNoLanguageFor*` block:

```csharp
// When the type's IncludeNoLanguageFor* toggle is on, move textless (no-tag)
// images to the very top of the ranking (above the preferred language) instead
// of dead-last. Off (default) keeps textless as a last resort. No effect while
// the matching Include toggle is off.
public bool PreferNoLanguageForPosters { get; set; } = false;
public bool PreferNoLanguageForBackdrops { get; set; } = false;
public bool PreferNoLanguageForLogos { get; set; } = false;
```

### 2. `Providers/LanguageAwareImageProviderBase.cs`
- New helper mirroring `IsTextlessAllowedFor`:

  ```csharp
  protected static bool IsTextlessPreferredFor(ImageType type) => type switch
  {
      ImageType.Primary  => Config.PreferNoLanguageForPosters,
      ImageType.Backdrop => Config.PreferNoLanguageForBackdrops,
      ImageType.Logo     => Config.PreferNoLanguageForLogos,
      _ => false
  };
  ```

- In `FetchRankMapAsync`, replace the two-state `textlessRank` with three states:

  ```csharp
  int textlessRank;
  if (!IsTextlessAllowedFor(type))       textlessRank = int.MaxValue; // excluded
  else if (IsTextlessPreferredFor(type)) textlessRank = -1;           // top
  else                                   textlessRank = buckets.Count; // last
  ```

- **No change to `BuildRemoteImageInfo`.** Ranks are pure sort keys
  (`MergeAndRank` does `OrderBy(r => r.Rank)`), so `-1` sorts before `0`
  naturally. The synthetic-rating encoding `rating = maxRank + 1 - rank` yields
  `buckets.Count + 2` for rank `-1` — the highest value — so the order also
  survives Jellyfin's `CommunityRating` re-sort. `maxRank` stays `buckets.Count`.

### 3. `Configuration/configPage.html`
Under each existing "Allow textless …" checkbox, add an indented
"Prefer textless over titled" checkbox bound to the new field. Wire the
load/save paths (mirroring the existing `IncludeNoLanguageFor*` handling).
Grey out / disable the Prefer checkbox when its Allow checkbox is unchecked.

### 4. `README.md`
1. **Config table:** add the three `PreferNoLanguageFor*` rows (default `false`).
2. **Bucket-order paragraph** (currently "…then textless (opt-in per type)"):
   note that textless can instead be moved to the top per type via
   `PreferNoLanguageFor*`.
3. **New short "Note on the image picker" subsection** stating plainly, where a
   confused user will find it, that: images are tagged with the library
   language, the picker shows synthetic ratings, and the **"All languages"
   toggle therefore has no effect** — all by design, not a bug.

### 5. `CLAUDE.md`
Update the bucket-ranking diagram so `rank 3: textless` notes the
`PreferNoLanguageFor*` variant (textless → rank `-1`, top).

### 6. Version + release
Feature-level change → bump `0.8.1.0` → `0.9.0.0` in the `.csproj`
(`AssemblyVersion`/`FileVersion`) and `meta.json` (`version` + `changelog`).
`targetAbi` unchanged. `manifest.json` is machine-edited by CI on tag push — do
not hand-edit.

## Out of scope

- Fixing symptoms 2 and 3 "properly" (real language tags / working "All
  languages" toggle). That would break the ordering guarantee and is a
  deliberate architecture trade-off — documented, not fixed.
- Tri-state enum config (Off/Include/Prefer). Rejected to avoid XML-config
  migration risk for existing users.
- Any change to episode still matching.

## Testing

No automated test framework (per `CLAUDE.md`). Manual verification:

1. `./build.sh`, restart Jellyfin, enable Debug logging.
2. On a Polish/`en`-fallback library with a title that has both textless and
   English backdrops: enable "Prefer textless" for backdrops, refresh metadata,
   confirm the textless backdrop is now first in the picker (Debug log shows the
   textless URL at the top of the ranking).
3. Toggle "Prefer" off → titled English returns to the top (regression check).
4. Confirm an existing config (upgraded from 0.8.1 without touching the new
   fields) behaves exactly as before (all three Prefer bools default false).
