# Language-Aware Images

A drop-in TMDB image provider for Jellyfin that gets two long-standing image
issues out of the way:

1. **Posters in your library's language** with a clean fallback to English, no
   more textless no-language posters as the built-in provider's first fallback.
2. **Episode images that match your library's order** for shows like Bluey,
   Star Trek or Doctor Who Classic where TVDB and TMDB disagree on which
   episode lives at which (Season, Episode) position. Matched by title, not by
   index, so the image is bound to the episode itself rather than its slot.

> Also from the same dev: [Sodalite](https://github.com/superuser404notfound/Sodalite), a native Apple TV client for Jellyfin with first-class Jellyseerr integration ([TestFlight beta](https://testflight.apple.com/join/nWeQzmBX)). Built on [AetherEngine](https://github.com/superuser404notfound/AetherEngine), a reusable video player engine for iOS, tvOS and macOS.

## Install

In Jellyfin: *Admin → Plugins → Repositories → +* and add

```
https://raw.githubusercontent.com/superuser404notfound/jellyfin-plugin-language-aware-images/main/manifest.json
```

Then *Catalog → Metadata → Language-Aware Images → Install*. Restart the server.

> After install, go to *Admin → Library → (your library) → Image Fetchers* and
> drag **Language-Aware TMDB Images** to the top, otherwise the built-in
> provider still wins.

## Configuration

*Admin → Plugins → Language-Aware Images*:

| Field                            | Default | Notes                                                                          |
| -------------------------------- | :-----: | ------------------------------------------------------------------------------ |
| `PreferredLanguageOverride`      | empty   | Empty = use each library's metadata language. Set e.g. `de` to force globally. |
| `FallbackLanguage`               |  `en`   | Used when no image in the preferred language exists.                           |
| `IncludeOriginalLanguage`        | `false` | Add the title's original language as a third bucket (e.g. Japanese for anime). |
| `OnlyOriginalLanguageForPosters` | `false` | Strict mode: posters only in original language, drops all other buckets. Backdrops/logos unaffected. |
| `IncludeNoLanguageForPosters`    | `false` | Allow textless posters as last resort.                                         |
| `IncludeNoLanguageForBackdrops`  | `true`  | Backdrops are usually language-agnostic anyway.                                |
| `IncludeNoLanguageForLogos`      | `true`  | Most studio logos are designed without text.                                   |
| `SortByVotes`                    | `true`  | Sort by `vote_count` (TMDB UI behavior). Off = sort by `vote_average`.         |
| `MinimumVoteCount`               |   `0`   | Drops images with fewer votes. `0` = keep everything (recommended).            |
| `MatchEpisodeImagesByTitle`      | `true`  | Episode images by title-lookup, not by (S,E) position. Fixes alternative-order shows. |
| `PosterImageSize`                | `original` | TMDB poster size: w92, w154, w185, w342, w500, w780, original.            |
| `BackdropImageSize`              | `original` | TMDB backdrop size: w300, w780, w1280, original.                          |
| `LogoImageSize`                  | `original` | TMDB logo size: w45, w92, w154, w185, w300, w500, original.               |
| `StillImageSize`                 | `original` | TMDB episode still size: w92, w185, w300, original.                       |
| `TmdbApiKey`                     | empty   | Bring your own TMDB key. Empty = uses Jellyfin's bundled key.                  |

The bucket order (preferred, original (opt-in), fallback, textless (opt-in
per type)) and a `vote_count DESC, vote_average DESC` sort within each bucket
matches TMDB's own `/images` UI.

## Why

### Posters / backdrops / logos

Jellyfin's built-in TMDB provider respects the library language for
language-matched images, but when no match exists it prefers **textless**
(no-language-tag) ones over the English fallback,
[jellyfin/jellyfin#9878](https://github.com/jellyfin/jellyfin/issues/9878).
Textless posters on TMDB are often awkwardly chosen: cropped stills, alternate
art, foreign-market exports without text. The result is a library that looks
visually inconsistent.

This plugin enforces a clean cascade:

1. Images in the library's language
2. English fallback (configurable)
3. Textless, only if you opt in per image type (useful for logos, off by
   default for posters)

Within each bucket, images are sorted by `vote_count DESC, vote_average DESC`,
the same order TMDB's `/images` UI uses, so you get the most popular image
in the matching language rather than a random one.

### Episode images for shows with alternative orderings

If you watch *Bluey* in Disney+ order, *Star Trek* in chronological order, or
*Doctor Who Classic* in DVD order, your library's (Season, Episode) numbering
won't line up with TMDB's. The built-in TMDB image provider asks for "S2E5"
literally and pulls the still that *TMDB* has at that position, which is the
wrong episode entirely. TMDB's own `episode_groups` API exists in theory but
is community-edited and frequently inaccurate.

This plugin instead **looks up the still by episode title**: it fetches all
TMDB episodes of the show once (cached per (show, language)) and matches your
local episode title against TMDB's. Image is bound to title, not to position,
so it works regardless of which order your library is in.

**Smart mode** (always on): the provider checks whether the TMDB title at
your local (S, E) position already matches your local title. If yes, your
library is in sync with TMDB ordering, the provider returns nothing and
Jellyfin's built-in provider delivers its full image set (multiple stills,
alternative crops etc.) for that episode. The title-matched still is only
injected when the library actually differs from TMDB's order.

Title normalisation handles the most common variations (case, leading
articles, punctuation, whitespace). If no title match is found either, the
plugin returns nothing and the built-in provider takes over, no regression.

## License

GPL-3.0 (the plugin links against Jellyfin's GPL assemblies).
