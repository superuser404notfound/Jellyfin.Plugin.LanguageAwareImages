using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LanguageAwareImages.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    // Comma-separated, ordered priority list of ISO 639-1 codes, regional
    // variants allowed (e.g. "pt-br,pt,pt-pt"). A single code still works.
    // Empty (default) means: auto-derive from the item's library metadata
    // language, expanding a regional value (pt-BR -> [pt-br, pt]) before fallback.
    public string PreferredLanguageOverride { get; set; } = string.Empty;

    // Comma-separated fallback list, used when no preferred-language image exists
    // (e.g. "en" or "en,fr").
    public string FallbackLanguage { get; set; } = "en";

    // Per-image-type textless toggles. Logos are almost always designed without
    // text (studio logos), backdrops are usually pure cinematography and don't
    // need language matching, posters generally should be language-matched.
    public bool IncludeNoLanguageForPosters { get; set; } = false;

    public bool IncludeNoLanguageForBackdrops { get; set; } = true;

    public bool IncludeNoLanguageForLogos { get; set; } = true;

    // When on, the movie/show's original_language is treated as a separate
    // bucket. Useful for foreign-cinema / anime libraries: a German user can
    // still see the Japanese poster for Princess Mononoke instead of being
    // forced to the US-marketing version. Its position is controlled by
    // OriginalLanguageLast.
    public bool IncludeOriginalLanguage { get; set; } = false;

    // Where the original-language bucket sits when IncludeOriginalLanguage is on.
    // true (default): after the fallback languages (dead-last), so a title is
    // shown under its international name first and the original (e.g. Japanese
    // or Korean) artwork only when nothing else matches. false: between
    // preferred and fallback.
    public bool OriginalLanguageLast { get; set; } = true;

    // Strict mode for posters (Primary image type): keep ONLY images in the
    // title's original_language, drop preferred / fallback / textless entries.
    // For purists who want the original artwork no matter what their library
    // language is. Implies IncludeOriginalLanguage. Returns empty (so the
    // built-in provider takes over) when original_language is unknown.
    // Backdrops and logos are unaffected, they keep the normal cascade.
    public bool OnlyOriginalLanguageForPosters { get; set; } = false;

    // Drops images with fewer than this many TMDB votes. Default 0 = keep
    // everything; a vote_count of 0 often just means "not voted on yet" rather
    // than "junk", and language-matched images with no votes are still
    // preferred over a fallback-language image. Bump to 1+ if you want to
    // aggressively trim unvalidated uploads.
    public int MinimumVoteCount { get; set; } = 0;

    // Tiebreak within each language bucket by vote_count (true) or vote_average
    // (false). Defaults to true, that's the order TMDB's own /images UI uses.
    // The bucket order itself is always enforced first (see BuildRemoteImageInfo);
    // this only decides how images that share a bucket are ordered.
    //
    // Implementation note: Jellyfin's downstream OrderByLanguageDescending sorts
    // by CommunityRating BEFORE VoteCount. We encode the bucket rank into the
    // integer part of CommunityRating either way; when this is true the rank is
    // the only signal we put there, so Jellyfin's VoteCount tiebreak orders
    // within a bucket. When false we add vote_average/100 on top of the rank so
    // the within-bucket order is by rating.
    public bool SortByVotes { get; set; } = true;

    public string TmdbApiKey { get; set; } = string.Empty;

    // Image scaling per type. TMDB serves images at preset widths via
    // /t/p/{size}/{file_path}. Smaller sizes save bandwidth and disk space
    // (Jellyfin re-downloads each pick) at the cost of resolution.
    // Valid values per type (from TMDB's /configuration endpoint):
    //   poster:   w92, w154, w185, w342, w500, w780, original
    //   backdrop: w300, w780, w1280, original
    //   logo:     w45, w92, w154, w185, w300, w500, original
    //   still:    w92, w185, w300, original
    // Defaults match the built-in TMDB plugin (all original).
    public string PosterImageSize { get; set; } = "original";

    public string BackdropImageSize { get; set; } = "original";

    public string LogoImageSize { get; set; } = "original";

    public string StillImageSize { get; set; } = "original";

    // Episode images: match by title rather than by (S,E) index.
    //
    // Background: shows with alternative episode orderings (Bluey, Star Trek,
    // Doctor Who Classic etc.) often have an order in TVDB that doesn't match
    // TMDB's own ordering. The built-in TMDB image provider uses (S,E) directly
    // and pulls the wrong still. TMDB's "episode_groups" API exists but is
    // community-edited and frequently mismatched with reality.
    //
    // Smart mode (always on inside the provider): we only inject a title-
    // matched still when the local episode title at (S,E) differs from
    // TMDB's title at the same (S,E). For in-sync shows we return nothing
    // and the built-in provider's full image set is preserved.
    public bool MatchEpisodeImagesByTitle { get; set; } = true;
}
