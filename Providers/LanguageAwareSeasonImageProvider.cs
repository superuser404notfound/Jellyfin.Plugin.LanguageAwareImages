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

    // TMDB only exposes posters at the season level, no backdrops or logos.
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

        var preferred = GetPreferredLanguages(item);
        var apiLanguage = LanguageMatching.ToTmdbLanguage(
            preferred.Count > 0 ? preferred[0] : (GetFallbackLanguages().FirstOrDefault() ?? string.Empty));

        var client = GetClient();

        var originalLanguage = string.Empty;
        if (NeedsOriginalLanguage())
        {
            var show = await client.GetTvShowAsync(seriesTmdbId, TvShowMethods.Undefined, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            originalLanguage = NormaliseLanguage(show?.OriginalLanguage);
        }

        return await FetchRankMapAsync(
            item,
            new[] { ImageType.Primary },
            originalLanguage,
            async (include, ct) =>
            {
                var images = await client.GetTvSeasonImagesAsync(
                    seriesTmdbId, season.IndexNumber.Value,
                    language: apiLanguage, includeImageLanguage: include, cancellationToken: ct)
                    .ConfigureAwait(false);
                return new MultiImages(images?.Posters, null, null);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
