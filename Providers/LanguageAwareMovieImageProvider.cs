using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using MovieMethods = TMDbLib.Objects.Movies.MovieMethods;

namespace Jellyfin.Plugin.LanguageAwareImages.Providers;

public class LanguageAwareMovieImageProvider : LanguageAwareImageProviderBase, IRemoteImageProvider
{
    public LanguageAwareMovieImageProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<LanguageAwareMovieImageProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    public bool Supports(BaseItem item) => item is Movie;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[]
    {
        ImageType.Primary,
        ImageType.Backdrop,
        ImageType.Logo
    };

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var tmdbIdRaw = item.GetProviderId(MetadataProvider.Tmdb);
        if (!int.TryParse(tmdbIdRaw, out var tmdbId))
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var preferredLanguage = GetEffectivePreferredLanguage(item);
        var apiLanguage = string.IsNullOrEmpty(preferredLanguage)
            ? Config.FallbackLanguage
            : preferredLanguage;

        var client = GetClient();

        // Fetch original_language only when a feature needs it, saves an API call.
        var originalLanguage = string.Empty;
        if (NeedsOriginalLanguage())
        {
            var movie = await client.GetMovieAsync(tmdbId, MovieMethods.Undefined, cancellationToken)
                .ConfigureAwait(false);
            originalLanguage = NormaliseLanguage(movie?.OriginalLanguage);
        }

        var images = await client.GetMovieImagesAsync(
            tmdbId,
            language: apiLanguage,
            includeImageLanguage: BuildIncludeLanguageParam(preferredLanguage, originalLanguage),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (images is null)
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var result = new List<RemoteImageInfo>();
        result.AddRange(RankAndMap(images.Posters, ImageType.Primary, preferredLanguage, originalLanguage));
        result.AddRange(RankAndMap(images.Backdrops, ImageType.Backdrop, preferredLanguage, originalLanguage));
        result.AddRange(RankAndMap(images.Logos, ImageType.Logo, preferredLanguage, originalLanguage));
        return result;
    }
}
