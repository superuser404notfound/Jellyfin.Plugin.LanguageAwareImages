using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LanguageAwareImages.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    // Empty (the default) means: pick up the language from the item's
    // library / parent metadata settings via BaseItem.GetPreferredMetadataLanguage().
    // Set to a 2-letter ISO 639-1 code to force a specific language regardless
    // of library settings.
    public string PreferredLanguageOverride { get; set; } = string.Empty;

    public string FallbackLanguage { get; set; } = "en";

    // Per-image-type textless toggles. Logos are almost always designed without
    // text (studio logos), backdrops are usually pure cinematography and don't
    // need language matching, posters generally should be language-matched.
    public bool IncludeNoLanguageForPosters { get; set; } = false;

    public bool IncludeNoLanguageForBackdrops { get; set; } = true;

    public bool IncludeNoLanguageForLogos { get; set; } = true;

    // When on, the movie/show's original_language is treated as a separate
    // bucket between preferred and fallback. Useful for foreign-cinema /
    // anime libraries: a German user can still see the Japanese poster for
    // Princess Mononoke instead of being forced to the US-marketing version.
    public bool IncludeOriginalLanguage { get; set; } = false;

    // Drops images with fewer than this many TMDB votes. Default 0 = keep
    // everything; a vote_count of 0 often just means "not voted on yet" rather
    // than "junk", and language-matched images with no votes are still
    // preferred over a fallback-language image. Bump to 1+ if you want to
    // aggressively trim unvalidated uploads.
    public int MinimumVoteCount { get; set; } = 0;

    public string TmdbApiKey { get; set; } = string.Empty;

    // Episode images — match by title rather than by (S,E) index.
    //
    // Background: shows with alternative episode orderings (Bluey, Star Trek,
    // Doctor Who Classic etc.) often have an order in TVDB that doesn't match
    // TMDB's own ordering. The built-in TMDB image provider uses (S,E) directly
    // and pulls the wrong still. TMDB's "episode_groups" API exists but is
    // community-edited and frequently mismatched with reality.
    //
    // When this is on, the plugin fetches all TMDB episodes for the show once,
    // builds a normalized-title → still_path map, and looks up your local
    // episode title in it. Image is bound to title, not to position, so it
    // works regardless of which order your library is in.
    public bool MatchEpisodeImagesByTitle { get; set; } = true;
}
