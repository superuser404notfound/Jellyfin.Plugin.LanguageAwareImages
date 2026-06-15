using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.LanguageAwareImages.Providers;

public class LanguageAwareSeriesImageProvider : LanguageAwareImageProviderBase, IRemoteImageProvider
{
    public LanguageAwareSeriesImageProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<LanguageAwareSeriesImageProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    public bool Supports(BaseItem item) => item is Series;

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
}
