using System.Globalization;
using System.Net.Http;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MangaBaka;

/// <summary>Writes a MangaBaka series onto a Jellyfin item.</summary>
public static class MangaBakaWriter
{
    /// <summary>
    /// Copies series metadata onto an item, returning whether anything changed.
    ///
    /// Genres, tags and studios are replaced rather than merged: these are the
    /// fields the provider owns, replacing is what Jellyfin's own providers do, and
    /// it is the only way a lowered tag limit can ever take effect. Lock the field
    /// in Jellyfin to keep hand-written values.
    /// </summary>
    /// <param name="item">Item to fill.</param>
    /// <param name="series">Resolved series.</param>
    /// <param name="cfg">Plugin configuration.</param>
    /// <returns>True when the item was changed.</returns>
    public static bool Apply(BaseItem item, MangaBakaSeries series, PluginConfiguration cfg)
    {
        var changed = false;

        if (!string.IsNullOrWhiteSpace(series.Description)
            && !string.Equals(item.Overview, series.Description, StringComparison.Ordinal))
        {
            item.Overview = series.Description;
            changed = true;
        }

        if (series.Rating is > 0)
        {
            // MangaBaka rates out of 100; Jellyfin shows out of 10.
            var rating = (float)Math.Round(series.Rating.Value / 10d, 1);
            if (item.CommunityRating != rating)
            {
                item.CommunityRating = rating;
                changed = true;
            }
        }

        if (series.Year is int year && MangaBakaSeries.IsPlausibleYear(year))
        {
            if (item.ProductionYear != year)
            {
                item.ProductionYear = year;
                changed = true;
            }

            // EPUB/Calibre often leaves PremiereDate at year 100; the UI shows that
            // instead of ProductionYear, so write a real date or the card stays "100".
            var premiere = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            if (item.PremiereDate is null
                || item.PremiereDate.Value.Year != series.Year
                || !MangaBakaSeries.IsPlausibleYear(item.PremiereDate.Value.Year))
            {
                item.PremiereDate = premiere;
                changed = true;
            }
        }

        var genres = series.Genres.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (genres.Length > 0 && !item.Genres.SequenceEqual(genres, StringComparer.Ordinal))
        {
            item.Genres = genres;
            changed = true;
        }

        var tags = Tags(series, cfg);
        if (tags.Length > 0 && !item.Tags.SequenceEqual(tags, StringComparer.Ordinal))
        {
            item.Tags = tags;
            changed = true;
        }

        var studios = series.Authors.Concat(series.Artists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (studios.Length > 0 && !item.Studios.SequenceEqual(studios, StringComparer.Ordinal))
        {
            item.Studios = studios;
            changed = true;
        }

        if (series.Id > 0)
        {
            var id = series.Id.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(item.GetProviderId(MangaBakaClient.ProviderId), id, StringComparison.Ordinal))
            {
                item.SetProviderId(MangaBakaClient.ProviderId, id);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// The tags to write: the publication status first, then MangaBaka's tags in
    /// general-to-specific order, capped at the configured limit. A series carries
    /// around 180 of them, which is unusable as-is.
    /// </summary>
    /// <param name="series">Resolved series.</param>
    /// <param name="cfg">Plugin configuration.</param>
    /// <returns>The tags to write.</returns>
    public static string[] Tags(MangaBakaSeries series, PluginConfiguration cfg)
    {
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(series.Status))
        {
            tags.Add(MangaBakaText.Prettify(series.Status!));
        }

        if (cfg.MaxTags > 0)
        {
            tags.AddRange(series.Tags.Take(cfg.MaxTags));
        }

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Builds the Book that Jellyfin merges into the library item.</summary>
    /// <param name="series">Resolved series.</param>
    /// <param name="cfg">Plugin configuration.</param>
    /// <returns>A book carrying the series metadata.</returns>
    public static Book ToBook(MangaBakaSeries series, PluginConfiguration cfg)
    {
        var book = new Book();
        var title = series.DisplayTitle(cfg.TitleStyle);
        if (!string.IsNullOrWhiteSpace(title))
        {
            book.SeriesName = title;
        }

        Apply(book, series, cfg);
        return book;
    }

    /// <summary>Turns a resolved series into an Identify result.</summary>
    /// <param name="series">Resolved series.</param>
    /// <param name="cfg">Plugin configuration.</param>
    /// <returns>The search result.</returns>
    public static RemoteSearchResult ToSearchResult(MangaBakaSeries series, PluginConfiguration cfg)
    {
        var result = new RemoteSearchResult
        {
            SearchProviderName = "MangaBaka",
            Name = series.DisplayTitle(cfg.TitleStyle),
            Overview = series.Description,
            ProductionYear = series.Year,
            ImageUrl = series.CoverUrl,
        };

        if (series.Id > 0)
        {
            result.SetProviderId(MangaBakaClient.ProviderId, series.Id.ToString(CultureInfo.InvariantCulture));
        }

        return result;
    }
}

/// <summary>
/// Book metadata from MangaBaka. This is the provider that appears in a book
/// library's metadata-downloader list, next to Google Books and Comic Vine.
/// MangaBaka is a series database, so a volume is filled with its series data.
/// </summary>
public sealed class MangaBakaMetadataProvider : IRemoteMetadataProvider<Book, BookInfo>
{
    private readonly IHttpClientFactory _http;
    private readonly MangaBakaResolver _resolver;

    /// <summary>Initializes a new instance of the <see cref="MangaBakaMetadataProvider"/> class.</summary>
    /// <param name="resolver">Series resolver.</param>
    /// <param name="http">HTTP client factory.</param>
    public MangaBakaMetadataProvider(MangaBakaResolver resolver, IHttpClientFactory http)
    {
        _resolver = resolver;
        _http = http;
    }

    /// <inheritdoc />
    public string Name => "MangaBaka";

    private static PluginConfiguration Config =>
        MangaBakaPlugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(BookInfo searchInfo, CancellationToken cancellationToken)
    {
        var cfg = Config;
        if (int.TryParse(searchInfo.GetProviderId(MangaBakaClient.ProviderId), out var id) && id > 0)
        {
            var byId = await _resolver.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
            return byId is null
                ? Array.Empty<RemoteSearchResult>()
                : new[] { MangaBakaWriter.ToSearchResult(byId, cfg) };
        }

        var results = await _resolver
            .SearchAsync(MangaBakaResolver.QueryTitle(searchInfo), cancellationToken)
            .ConfigureAwait(false);
        return results.Select(s => MangaBakaWriter.ToSearchResult(s, cfg)).ToArray();
    }

    /// <inheritdoc />
    public async Task<MetadataResult<Book>> GetMetadata(BookInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Book>();
        var series = await _resolver
            .ResolveAsync(
                info.GetProviderId(MangaBakaClient.ProviderId),
                MangaBakaResolver.QueryTitle(info),
                cancellationToken)
            .ConfigureAwait(false);

        if (series is null || MangaBakaResolver.ShouldSkip(series))
        {
            return result;
        }

        var cfg = Config;
        result.Item = MangaBakaWriter.ToBook(series, cfg);
        result.HasMetadata = true;
        foreach (var author in series.Authors)
        {
            result.AddPerson(new PersonInfo { Name = author, Type = PersonKind.Author });
        }

        foreach (var artist in series.Artists.Where(a => !series.Authors.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            result.AddPerson(new PersonInfo { Name = artist, Type = PersonKind.Artist });
        }

        return result;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        MangaBakaImageProvider.FetchAsync(_http, url, cancellationToken);
}

/// <summary>
/// Fills series-folder metadata from MangaBaka. Book libraries model a series
/// directory as a plain Folder, which has no row in the metadata-downloader
/// list, so this runs during refresh — but only when MangaBaka is ticked for
/// Books in that library.
/// </summary>
public sealed class MangaBakaFolderMetadataProvider : ICustomMetadataProvider<Folder>, IHasItemChangeMonitor
{
    private readonly MangaBakaResolver _resolver;
    private readonly ILogger<MangaBakaFolderMetadataProvider> _log;

    /// <summary>Initializes a new instance of the <see cref="MangaBakaFolderMetadataProvider"/> class.</summary>
    /// <param name="resolver">Series resolver.</param>
    /// <param name="log">Logger.</param>
    public MangaBakaFolderMetadataProvider(MangaBakaResolver resolver, ILogger<MangaBakaFolderMetadataProvider> log)
    {
        _resolver = resolver;
        _log = log;
    }

    /// <inheritdoc />
    public string Name => "MangaBaka";

    /// <inheritdoc />
    public bool HasChanged(BaseItem item, IDirectoryService directoryService) => false;

    /// <inheritdoc />
    public async Task<ItemUpdateType> FetchAsync(
        Folder item,
        MetadataRefreshOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.Name) || !_resolver.AppliesTo(item) || !_resolver.IsSelected(item, images: false))
        {
            return ItemUpdateType.None;
        }

        var series = await _resolver
            .ResolveAsync(item.GetProviderId(MangaBakaClient.ProviderId), item.Name, cancellationToken)
            .ConfigureAwait(false);
        if (series is null)
        {
            return ItemUpdateType.None;
        }

        if (MangaBakaResolver.ShouldSkip(series))
        {
            _log.LogDebug("MangaBaka: skipping {Name} (content rating {Rating})", item.Name, series.ContentRating);
            return ItemUpdateType.None;
        }

        var cfg = MangaBakaPlugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!MangaBakaWriter.Apply(item, series, cfg))
        {
            return ItemUpdateType.None;
        }

        _log.LogDebug("MangaBaka: filled metadata for {Name} (id {Id})", item.Name, series.Id);
        return ItemUpdateType.MetadataDownload;
    }
}

/// <summary>Serves cover art from MangaBaka for books and series folders.</summary>
public sealed class MangaBakaImageProvider : IRemoteImageProvider
{
    private readonly IHttpClientFactory _http;
    private readonly MangaBakaResolver _resolver;

    /// <summary>Initializes a new instance of the <see cref="MangaBakaImageProvider"/> class.</summary>
    /// <param name="resolver">Series resolver.</param>
    /// <param name="http">HTTP client factory.</param>
    public MangaBakaImageProvider(MangaBakaResolver resolver, IHttpClientFactory http)
    {
        _resolver = resolver;
        _http = http;
    }

    /// <inheritdoc />
    public string Name => "MangaBaka";

    /// <inheritdoc />
    /// <remarks>
    /// Library options probe this with a dummy Book that has no parent library, so
    /// the Books-library check has to live in <see cref="GetImages"/> — otherwise
    /// the provider never appears under Image Fetchers (Books).
    /// </remarks>
    public bool Supports(BaseItem item) =>
        MangaBakaPlugin.Instance?.Configuration.ProvideImages != false
        && (item is Book || item.GetType() == typeof(Folder));

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary };

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        if (!_resolver.AppliesTo(item) || !_resolver.IsSelected(item, images: true))
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var title = item is Book book && !string.IsNullOrWhiteSpace(book.SeriesName) ? book.SeriesName : item.Name;
        var series = await _resolver
            .ResolveAsync(item.GetProviderId(MangaBakaClient.ProviderId), title, cancellationToken)
            .ConfigureAwait(false);

        if (series?.CoverUrl is null || MangaBakaResolver.ShouldSkip(series))
        {
            return Array.Empty<RemoteImageInfo>();
        }

        return new[]
        {
            new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Primary,
                Url = series.CoverUrl,
            },
        };
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        FetchAsync(_http, url, cancellationToken);

    /// <summary>Fetches an image with the plugin's user-agent, which the CDN expects.</summary>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="url">Image URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response.</returns>
    internal static Task<HttpResponseMessage> FetchAsync(
        IHttpClientFactory http,
        string url,
        CancellationToken cancellationToken)
    {
        var client = http.CreateClient(NamedClient.Default);
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
        request.Headers.TryAddWithoutValidation("User-Agent", MangaBakaClient.UserAgent);
        return client.SendAsync(request, cancellationToken);
    }
}

/// <summary>Lets Identify store a MangaBaka series id on a book.</summary>
public sealed class MangaBakaExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "MangaBaka";

    /// <inheritdoc />
    public string Key => MangaBakaClient.ProviderId;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Book;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Book || item?.GetType() == typeof(Folder);
}

/// <summary>Links an item back to its MangaBaka page.</summary>
public sealed class MangaBakaExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc />
    public string Name => "MangaBaka";

    /// <inheritdoc />
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        // mangabaka.org/<id> redirects to the canonical slug URL, so the id alone is
        // enough and nothing has to be stored beyond the provider id.
        if (item.TryGetProviderId(MangaBakaClient.ProviderId, out var id) && !string.IsNullOrEmpty(id))
        {
            yield return string.Format(CultureInfo.InvariantCulture, "https://mangabaka.org/{0}", id);
        }
    }
}
