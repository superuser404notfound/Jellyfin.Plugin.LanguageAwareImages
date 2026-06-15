# Regional ISO 639 variants + language priority list

**Issue:** [#2](https://github.com/superuser404notfound/jellyfin-plugin-language-aware-images/issues/2) — "Allow variants of the ISO 639 code and more options"
**Date:** 2026-06-15
**Status:** Approved, ready for planning

## Context

A Portuguese (Brazil) user wants `pt-BR` posters but the plugin serves the
`pt` (Portugal) one. Two problems:

1. **Posters:** The plugin normalises the library language `pt-BR` → `pt`
   (`NormaliseLanguage`) and then matches `iso_639_1 == "pt"` exactly. The
   Brazilian poster, which TMDB only exposes as a regional variant, never wins.
2. **No priority list:** Users can only express one preferred + one fallback
   language. The issue asks for an ordered cascade, e.g.
   `pt-br → pt → pt-pt → en (fallback) → fr (original)`.

### Verified TMDB behaviour (the constraint that shapes the design)

TMDB does **not** return the region in the image `iso_639_1` field — it is
always the bare 2-letter code (`pt`). The region is only exposed through the
`include_image_language` **filter**:

- `include_image_language=pt-BR` → the Brazilian poster set
- `include_image_language=pt-PT` → the Portuguese poster set (disjoint from BR)
- `include_image_language=pt` → the union of both

Confirmed on Game of Thrones (`tv/1399`): `pt-BR` = 5 posters, `pt-PT` = 5
posters, no overlap; `pt` = all 10. The filter is case-insensitive
(`pt-br` == `pt-BR` == `PT-BR`) and one call returns posters + backdrops +
logos together.

**Implication:** regions can only be distinguished by issuing **separate
filter calls per regional code** and attributing each returned image to the
highest-priority call that yielded it (deduped by `file_path`).

## Decisions (from brainstorming)

- **Config model: extended buckets.** Keep the existing `preferred` / `fallback`
  / `original` structure; make `PreferredLanguageOverride` and
  `FallbackLanguage` comma-separated ordered lists. No new config fields.
- **Auto-expansion: on.** When the user sets no override list and the library
  language is a regional variant (`pt-BR`), expand to `[pt-br, pt]` before the
  fallback.
- **Original-language position unchanged:** stays between preferred and
  fallback (backwards compatible), not at the very end.

## Design

### 1. `PluginConfiguration.cs`
- Reinterpret `PreferredLanguageOverride` as a comma-separated ordered list
  (a single code still works → backwards compatible).
- Allow `FallbackLanguage` to be a list too (`en` or `en,fr`).
- Update XML doc comments only; no field additions/renames.

### 2. `LanguageAwareImageProviderBase.cs`
- `NormaliseLanguage`: preserve regional variants — `pt-BR → pt-br`
  (lowercase, keep the hyphen) instead of collapsing to `pt`.
- New helper `GetPreferredLanguages(item)`: returns the ordered preferred list,
  parsed from the override list, or auto-expanded from the library language
  (`pt-BR` → `[pt-br, pt]`).
- Build the ordered bucket list:
  `preferred[0..n] → (original, opt-in) → fallback[0..n] → (textless, per type)`.
  Each bucket's index is its rank; ties broken by `vote_count DESC,
  vote_average DESC` as today.

### 3. Image fetch (Movie + Series providers, shared base method)
- **No regional code in the bucket list** → keep today's path exactly: one
  call, client-side ranking by `iso_639_1`. No regression for the majority.
- **Regional codes present** → one collective call for all simple codes +
  `null`, plus one call per regional code. Merge by `file_path`, lowest rank
  wins. One call returns all three image types.
- Shared method takes a fetch delegate `(includeImageLanguage) => Images` so
  Movie (`GetMovieImagesAsync`) and Series (`GetTvShowImagesAsync`) reuse it.
- Cost example `pt-br,pt,pt-pt,en`: 3 calls instead of 1 (only when regional
  variants are configured/auto-expanded).

### 4. `LanguageAwareEpisodeImageProvider.cs`
- Pass the first preferred code (now a full locale, e.g. `pt-BR`) as the TMDB
  `language` parameter so episode titles come back in the regional variant —
  fixes the title-matching half of the issue. Cache key already includes
  language, so this is mostly automatic once normalisation preserves the region.

### 5. Jellyfin filter workaround
- Keep `DisguiseLanguage`: tag selected images with the top preferred code so
  they survive Jellyfin's `{empty, preferred, fallback}` filter and sort
  correctly. Verify in a real Jellyfin instance that a regional tag (`pt-br`)
  survives the filter against the library's `pt-BR` setting; adjust the tag
  source (use the raw `GetPreferredMetadataLanguage()` string) if needed.

### 6. Supporting changes
- `Configuration/configPage.html`: update help text to "ordered list, regional
  variants allowed".
- `README.md`: update the config table and the "Why" section.
- Version bump in `Jellyfin.Plugin.LanguageAwareImages.csproj` + `meta.json`
  (with changelog).

## Trade-off

Extra TMDB API calls per item (one per regional code), only when regional
variants are in play. Noticeable during a full library scan, but TMDB rate
limits are generous and Jellyfin scans asynchronously.

## Verification

- Unit-level: title/locale normalisation and bucket-ranking are pure functions
  — exercise with `pt-BR/pt/pt-PT` inputs. (No test project exists today; add a
  minimal one or verify via logging.)
- End-to-end: build with `./build.sh`, restart Jellyfin, set a library to
  `pt-BR`, refresh "Totally Spies"/"Game of Thrones" metadata, confirm the
  Brazilian poster ranks first in the image picker with `Debug` logging on.
