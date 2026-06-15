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
}
