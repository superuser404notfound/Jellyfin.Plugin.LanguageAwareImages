using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.LanguageAwareImages.Providers;

public class LanguageAwareSeasonImageProvider : LanguageAwareImageProviderBase, IRemoteImageProvider
{
    public LanguageAwareSeasonImageProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<LanguageAwareSeasonImageProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    public bool Supports(BaseItem item) => item is Season;

    // TMDB only exposes posters at the season level — no backdrops or logos.
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[]
    {
        ImageType.Primary
    };

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Season season || season.IndexNumber is null)
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var seriesTmdbIdRaw = season.Series?.GetProviderId(MetadataProvider.Tmdb);
        if (!int.TryParse(seriesTmdbIdRaw, out var seriesTmdbId))
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var preferredLanguage = GetEffectivePreferredLanguage(item);
        var apiLanguage = string.IsNullOrEmpty(preferredLanguage)
            ? Config.FallbackLanguage
            : preferredLanguage;

        var client = GetClient();

        // Seasons inherit original_language from the parent show.
        var originalLanguage = string.Empty;
        if (Config.IncludeOriginalLanguage)
        {
            var show = await client.GetTvShowAsync(seriesTmdbId, TvShowMethods.Undefined, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            originalLanguage = NormaliseLanguage(show?.OriginalLanguage);
        }

        var images = await client.GetTvSeasonImagesAsync(
            seriesTmdbId,
            season.IndexNumber.Value,
            language: apiLanguage,
            includeImageLanguage: BuildIncludeLanguageParam(preferredLanguage, originalLanguage),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return images is null
            ? Array.Empty<RemoteImageInfo>()
            : RankAndMap(images.Posters, ImageType.Primary, preferredLanguage, originalLanguage);
    }
}
