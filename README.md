# Jellyfin.Plugin.LanguageAwareImages

A drop-in TMDB image provider for Jellyfin that gives you posters in your
library's language with a clean fallback to English — and skips the textless
no-language posters the built-in provider often picks instead.

## Install

In Jellyfin: *Admin → Plugins → Repositories → +* and add

```
https://raw.githubusercontent.com/superuser404notfound/Jellyfin.Plugin.LanguageAwareImages/main/manifest.json
```

Then *Catalog → Metadata → Language-Aware Images → Install*. Restart the server.

> After install, go to *Admin → Library → (your library) → Image Fetchers* and
> drag **Language-Aware TMDB Images** to the top — otherwise the built-in
> provider still wins.

## Configuration

*Admin → Plugins → Language-Aware Images*:

| Field                       | Default | Notes                                                                          |
| --------------------------- | :-----: | ------------------------------------------------------------------------------ |
| `PreferredLanguageOverride` | empty   | Empty = use each library's metadata language. Set e.g. `de` to force globally. |
| `FallbackLanguage`          |  `en`   | Used when no image in the preferred language exists.                           |
| `IncludeNoLanguage`         | `false` | Allow textless images as last resort. Useful for logos, not for posters.       |
| `TmdbApiKey`                | empty   | Bring your own TMDB key. Empty = uses Jellyfin's bundled key.                  |

## Why

Jellyfin's built-in TMDB provider respects the library language for
language-matched posters, but when no match exists it prefers **textless**
(no-language-tag) posters over the English ones —
[jellyfin/jellyfin#9878](https://github.com/jellyfin/jellyfin/issues/9878).
Textless posters on TMDB are often awkwardly chosen: cropped stills, alternate
art, foreign-market exports without text. The result is a library that looks
visually inconsistent.

This plugin enforces a clean cascade:

1. Posters in the library's language
2. English fallback (configurable)
3. Textless — only if you opt in (useful for logos, off by default)

Within each bucket, images are sorted by `vote_count DESC, vote_average DESC` —
the same order TMDB's `/images` UI uses, so you get the most popular poster
in the matching language rather than a random one.

## License

GPL-3.0 (the plugin links against Jellyfin's GPL assemblies).
