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

    // Drops images with very few votes. TMDB lets anyone upload art; entries
    // with 0 votes are usually unvalidated junk. Default 1 trims them without
    // losing legitimate uploads.
    public int MinimumVoteCount { get; set; } = 1;

    public string TmdbApiKey { get; set; } = string.Empty;
}
