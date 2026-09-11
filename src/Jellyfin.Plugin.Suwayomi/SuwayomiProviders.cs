using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Suwayomi;

/// <summary>One manga as Suwayomi knows it.</summary>
public sealed class SuwayomiManga
{
    /// <summary>Gets or sets the Suwayomi manga id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the author.</summary>
    public string? Author { get; set; }

    /// <summary>Gets or sets the artist.</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets the synopsis.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the genre list.</summary>
    public List<string>? Genre { get; set; }

    /// <summary>Gets or sets the publishing status.</summary>
    public string? Status { get; set; }

    /// <summary>Gets or sets the source display name, e.g. "MangaDex (EN)".</summary>
    public string? SourceName { get; set; }
}

/// <summary>
/// Talks to Suwayomi's GraphQL API and maps folders on disk back to library entries.
/// </summary>
public sealed class SuwayomiClient
{
    private static readonly Regex StatsLine = new(
        @"^[ \t]*Rating:[ \t]*(?<rating>[0-9]+(?:\.[0-9]+)?)?[^\r\n]*\r?\n?",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex BookmarksOnly = new(
        @"^[ \t]*Bookmarks?:[^\r\n]*\r?\n?",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly char[] Illegal = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    private readonly IHttpClientFactory _http;
    private readonly ILogger<SuwayomiClient> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Dictionary<string, SuwayomiManga>? _byKey;
    private DateTime _fetched = DateTime.MinValue;

    /// <summary>Initializes a new instance of the <see cref="SuwayomiClient"/> class.</summary>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="log">Logger.</param>
    public SuwayomiClient(IHttpClientFactory http, ILogger<SuwayomiClient> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>Gets the configured Suwayomi base URL, trimmed of any trailing slash.</summary>
    public static string BaseUrl =>
        (SuwayomiPlugin.Instance?.Configuration.ServerUrl ?? string.Empty).TrimEnd('/');

    /// <summary>Gets a value indicating whether a server URL has been configured.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>Stored on items so a later refresh can skip search.</summary>
    public const string ProviderId = "Suwayomi";

    /// <summary>
    /// Whether an item is one this plugin should fill: a book, or a series folder in
    /// a book library. Series, Season, BoxSet and CollectionFolder all derive from
    /// Folder, and every folder in a photo library is a plain Folder too, so the
    /// concrete type and the library's content type both have to be checked.
    /// </summary>
    /// <param name="item">The item being refreshed.</param>
    /// <param name="library">Library manager.</param>
    /// <returns>True when the plugin should act on this item.</returns>
    public static bool AppliesTo(BaseItem? item, ILibraryManager library)
    {
        if (item is null || (item is not Book && item.GetType() != typeof(Folder)))
        {
            return false;
        }

        if (SuwayomiPlugin.Instance?.Configuration.RestrictToBookLibraries == false)
        {
            return true;
        }

        try
        {
            return library.GetInheritedContentType(item) == CollectionType.books;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether Suwayomi is ticked in the library's Book metadata or image fetchers.
    /// Series folders are plain Folders, which have no row in that UI, so Jellyfin
    /// never applies the checkbox to the folder provider itself — we have to.
    /// </summary>
    /// <param name="item">Item being refreshed.</param>
    /// <param name="library">Library manager.</param>
    /// <param name="images">True to read Image Fetchers, false for Metadata downloaders.</param>
    /// <returns>True when Suwayomi is selected for this library.</returns>
    public static bool IsSelected(BaseItem item, ILibraryManager library, bool images)
    {
        var options = library.GetLibraryOptions(item);
        var type = options.GetTypeOptions("Book") ?? options.GetTypeOptions(item.GetType().Name);
        if (type is null)
        {
            return false;
        }

        var selected = images ? type.ImageFetchers : type.MetadataFetchers;
        return selected.Any(name => string.Equals(name, "Suwayomi", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reproduces Suwayomi's own folder naming so a path can be mapped back to a title.
    /// Illegal characters become '_', and trailing dots/spaces are stripped - both rules
    /// matter, and getting either wrong silently breaks the match.
    /// </summary>
    /// <param name="value">Raw title.</param>
    /// <returns>The sanitised folder name.</returns>
    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(Illegal, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        return new string(chars).TrimEnd('.', ' ').Trim();
    }

    /// <summary>Builds the lookup key for a source/title pair.</summary>
    /// <param name="source">Source display name.</param>
    /// <param name="title">Manga title.</param>
    /// <returns>A normalised key.</returns>
    public static string Key(string source, string title) =>
        source.Trim().ToLowerInvariant() + "\0" + Sanitize(title).ToLowerInvariant();

    /// <summary>Strips Suwayomi's scraped stats line and returns any rating it carried.</summary>
    /// <param name="description">Raw description.</param>
    /// <param name="rating">Parsed community rating, if present.</param>
    /// <returns>The cleaned synopsis.</returns>
    public static string CleanDescription(string? description, out float? rating)
    {
        rating = null;
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var match = StatsLine.Match(description);
        if (match.Success)
        {
            var g = match.Groups["rating"];
            if (g.Success && float.TryParse(g.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
            {
                rating = r;
            }

            if (SuwayomiPlugin.Instance?.Configuration.StripStatsLine != false)
            {
                description = StatsLine.Replace(description, string.Empty, 1);
            }
        }

        if (SuwayomiPlugin.Instance?.Configuration.StripStatsLine != false)
        {
            description = BookmarksOnly.Replace(description, string.Empty, 1);
        }

        return description.TrimStart('\r', '\n', ' ').TrimEnd();
    }

    /// <summary>
    /// Series-folder path for a library item. Book files live inside the series
    /// folder, so a file path is walked up one level; a directory is used as-is.
    /// </summary>
    /// <param name="path">File or folder path.</param>
    /// <returns>The series folder path, or null.</returns>
    public static string? SeriesPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var p = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.HasExtension(p) ? Path.GetDirectoryName(p) : p;
    }

    /// <summary>Finds the Suwayomi entry matching a folder or book-file path, if any.</summary>
    /// <param name="path">Folder or book file path on disk.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching manga, or null.</returns>
    public async Task<SuwayomiManga?> MatchFolderAsync(string? path, CancellationToken ct)
    {
        var folder = SeriesPath(path);
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        // .../<source display name>/<sanitised title>
        var dir = new DirectoryInfo(folder);
        var source = dir.Parent?.Name;
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        var map = await GetLibraryAsync(ct).ConfigureAwait(false);
        return map is not null && map.TryGetValue(Key(source, dir.Name), out var manga) ? manga : null;
    }

    /// <summary>Finds a library entry by Suwayomi manga id.</summary>
    /// <param name="id">Suwayomi manga id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching manga, or null.</returns>
    public async Task<SuwayomiManga?> GetByIdAsync(int id, CancellationToken ct)
    {
        var map = await GetLibraryAsync(ct).ConfigureAwait(false);
        return map?.Values.FirstOrDefault(m => m.Id == id);
    }

    /// <summary>Searches the cached Suwayomi library by title.</summary>
    /// <param name="title">Series title.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching manga, possibly empty.</returns>
    public async Task<IReadOnlyList<SuwayomiManga>> SearchAsync(string title, CancellationToken ct)
    {
        var map = await GetLibraryAsync(ct).ConfigureAwait(false);
        if (map is null || string.IsNullOrWhiteSpace(title))
        {
            return Array.Empty<SuwayomiManga>();
        }

        var want = Sanitize(title).ToLowerInvariant();
        return map.Values
            .Where(m => !string.IsNullOrEmpty(m.Title)
                && Sanitize(m.Title!).ToLowerInvariant().Contains(want, StringComparison.Ordinal))
            .GroupBy(m => m.Id)
            .Select(g => g.First())
            .ToList();
    }

    private async Task<Dictionary<string, SuwayomiManga>?> GetLibraryAsync(CancellationToken ct)
    {
        // Nothing configured yet: stay idle rather than firing a request per folder
        // at an address the user never gave us. Callers already handle null.
        if (!IsConfigured)
        {
            return null;
        }

        // A refresh walks hundreds of folders; cache so we query Suwayomi once, not once per item.
        if (_byKey is not null && DateTime.UtcNow - _fetched < TimeSpan.FromMinutes(10))
        {
            return _byKey;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_byKey is not null && DateTime.UtcNow - _fetched < TimeSpan.FromMinutes(10))
            {
                return _byKey;
            }

            const string Query = "{\"query\":\"{ mangas(condition:{inLibrary:true}) { nodes { id title author artist description genre status source { displayName } } } }\"}";

            using var client = _http.CreateClient(NamedClient.Default);
            client.Timeout = TimeSpan.FromSeconds(60);
            using var content = new StringContent(Query, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(new Uri(BaseUrl + "/api/graphql"), content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var nodes = doc.RootElement.GetProperty("data").GetProperty("mangas").GetProperty("nodes");
            var map = new Dictionary<string, SuwayomiManga>(StringComparer.Ordinal);

            foreach (var n in nodes.EnumerateArray())
            {
                var manga = new SuwayomiManga
                {
                    Id = n.GetProperty("id").GetInt32(),
                    Title = Str(n, "title"),
                    Author = Str(n, "author"),
                    Artist = Str(n, "artist"),
                    Description = Str(n, "description"),
                    Status = Str(n, "status"),
                };

                if (n.TryGetProperty("genre", out var genre) && genre.ValueKind == JsonValueKind.Array)
                {
                    manga.Genre = genre.EnumerateArray()
                        .Where(g => g.ValueKind == JsonValueKind.String)
                        .Select(g => g.GetString()!)
                        .Where(g => !string.IsNullOrWhiteSpace(g))
                        .ToList();
                }

                if (n.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object)
                {
                    manga.SourceName = Str(src, "displayName");
                }

                if (!string.IsNullOrEmpty(manga.SourceName) && !string.IsNullOrEmpty(manga.Title))
                {
                    map[Key(manga.SourceName!, manga.Title!)] = manga;
                }
            }

            _byKey = map;
            _fetched = DateTime.UtcNow;
            _log.LogInformation("Suwayomi: cached {Count} library entries from {Url}", map.Count, BaseUrl);
            return _byKey;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Suwayomi: could not read library from {Url}", BaseUrl);
            return _byKey; // fall back to a stale cache rather than wiping metadata
        }
        finally
        {
            _lock.Release();
        }

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}

/// <summary>
/// Book metadata from Suwayomi. This is the provider that appears in a book
/// library's metadata-downloader list, matching Google Books / Comic Vine.
/// Matching is by the series folder path Suwayomi writes to disk.
/// </summary>
public sealed class SuwayomiMetadataProvider : IRemoteMetadataProvider<Book, BookInfo>
{
    private readonly IHttpClientFactory _http;
    private readonly SuwayomiClient _client;

    /// <summary>Initializes a new instance of the <see cref="SuwayomiMetadataProvider"/> class.</summary>
    /// <param name="client">Shared Suwayomi client.</param>
    /// <param name="http">HTTP client factory.</param>
    public SuwayomiMetadataProvider(SuwayomiClient client, IHttpClientFactory http)
    {
        _client = client;
        _http = http;
    }

    /// <inheritdoc />
    public string Name => "Suwayomi";

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(BookInfo searchInfo, CancellationToken cancellationToken)
    {
        if (int.TryParse(searchInfo.GetProviderId(SuwayomiClient.ProviderId), out var id) && id > 0)
        {
            var byId = await _client.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
            return byId is null ? Array.Empty<RemoteSearchResult>() : new[] { ToSearchResult(byId) };
        }

        var manga = await _client.MatchFolderAsync(searchInfo.Path, cancellationToken).ConfigureAwait(false);
        if (manga is not null)
        {
            return new[] { ToSearchResult(manga) };
        }

        var title = !string.IsNullOrWhiteSpace(searchInfo.SeriesName) ? searchInfo.SeriesName : searchInfo.Name;
        if (string.IsNullOrWhiteSpace(title))
        {
            return Array.Empty<RemoteSearchResult>();
        }

        var results = await _client.SearchAsync(title, cancellationToken).ConfigureAwait(false);
        return results.Select(ToSearchResult).ToArray();
    }

    /// <inheritdoc />
    public async Task<MetadataResult<Book>> GetMetadata(BookInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Book>();
        var manga = await ResolveAsync(info, cancellationToken).ConfigureAwait(false);
        if (manga is null)
        {
            return result;
        }

        result.Item = ToBook(manga);
        result.HasMetadata = true;
        if (!string.IsNullOrWhiteSpace(manga.Author))
        {
            result.AddPerson(new PersonInfo { Name = manga.Author, Type = PersonKind.Author });
        }

        if (!string.IsNullOrWhiteSpace(manga.Artist)
            && !string.Equals(manga.Artist, manga.Author, StringComparison.OrdinalIgnoreCase))
        {
            result.AddPerson(new PersonInfo { Name = manga.Artist, Type = PersonKind.Artist });
        }

        return result;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var client = _http.CreateClient(NamedClient.Default);
        return client.GetAsync(new Uri(url), cancellationToken);
    }

    internal async Task<SuwayomiManga?> ResolveAsync(BookInfo info, CancellationToken ct)
    {
        if (int.TryParse(info.GetProviderId(SuwayomiClient.ProviderId), out var id) && id > 0)
        {
            var byId = await _client.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (byId is not null)
            {
                return byId;
            }
        }

        return await _client.MatchFolderAsync(info.Path, ct).ConfigureAwait(false);
    }

    internal static Book ToBook(SuwayomiManga manga)
    {
        var book = new Book();
        if (!string.IsNullOrWhiteSpace(manga.Title))
        {
            book.SeriesName = manga.Title;
        }

        var overview = SuwayomiClient.CleanDescription(manga.Description, out var rating);
        if (!string.IsNullOrWhiteSpace(overview))
        {
            book.Overview = overview;
        }

        if (rating.HasValue)
        {
            book.CommunityRating = rating;
        }

        if (manga.Genre is { Count: > 0 })
        {
            foreach (var genre in manga.Genre)
            {
                book.AddGenre(genre);
            }
        }

        if (!string.IsNullOrWhiteSpace(manga.SourceName))
        {
            book.AddStudio(manga.SourceName!);
        }

        if (!string.IsNullOrWhiteSpace(manga.Status))
        {
            book.AddTag(manga.Status!);
        }

        if (manga.Id > 0)
        {
            book.SetProviderId(SuwayomiClient.ProviderId, manga.Id.ToString(CultureInfo.InvariantCulture));
        }

        return book;
    }

    private static RemoteSearchResult ToSearchResult(SuwayomiManga manga)
    {
        var remote = new RemoteSearchResult
        {
            SearchProviderName = "Suwayomi",
            Name = manga.Title,
            Overview = SuwayomiClient.CleanDescription(manga.Description, out _),
        };
        if (manga.Id > 0)
        {
            remote.SetProviderId(SuwayomiClient.ProviderId, manga.Id.ToString(CultureInfo.InvariantCulture));
        }

        return remote;
    }
}

/// <summary>Fills series-folder metadata from Suwayomi.</summary>
public sealed class SuwayomiFolderMetadataProvider : ICustomMetadataProvider<Folder>, IHasItemChangeMonitor
{
    private readonly SuwayomiClient _client;
    private readonly ILibraryManager _library;
    private readonly ILogger<SuwayomiFolderMetadataProvider> _log;

    /// <summary>Initializes a new instance of the <see cref="SuwayomiFolderMetadataProvider"/> class.</summary>
    /// <param name="client">Shared Suwayomi client.</param>
    /// <param name="library">Library manager.</param>
    /// <param name="log">Logger.</param>
    public SuwayomiFolderMetadataProvider(SuwayomiClient client, ILibraryManager library, ILogger<SuwayomiFolderMetadataProvider> log)
    {
        _client = client;
        _library = library;
        _log = log;
    }

    /// <inheritdoc />
    public string Name => "Suwayomi";

    /// <inheritdoc />
    public bool HasChanged(BaseItem item, IDirectoryService directoryService) => false;

    /// <inheritdoc />
    public async Task<ItemUpdateType> FetchAsync(
        Folder item,
        MetadataRefreshOptions options,
        CancellationToken cancellationToken)
    {
        if (!SuwayomiClient.AppliesTo(item, _library) || !SuwayomiClient.IsSelected(item, _library, images: false))
        {
            return ItemUpdateType.None;
        }

        var manga = await _client.MatchFolderAsync(item.Path, cancellationToken).ConfigureAwait(false);
        if (manga is null)
        {
            _log.LogDebug("Suwayomi: no library match for folder {Path}", item.Path);
            return ItemUpdateType.None;
        }

        var updated = ItemUpdateType.None;

        var overview = SuwayomiClient.CleanDescription(manga.Description, out var rating);
        if (!string.IsNullOrWhiteSpace(overview) && !string.Equals(item.Overview, overview, StringComparison.Ordinal))
        {
            item.Overview = overview;
            updated = ItemUpdateType.MetadataDownload;
        }

        if (rating.HasValue && item.CommunityRating != rating)
        {
            item.CommunityRating = rating;
            updated = ItemUpdateType.MetadataDownload;
        }

        if (manga.Genre is { Count: > 0 })
        {
            var genres = manga.Genre.ToArray();
            if (!item.Genres.SequenceEqual(genres, StringComparer.Ordinal))
            {
                item.Genres = genres;
                updated = ItemUpdateType.MetadataDownload;
            }
        }

        if (!string.IsNullOrWhiteSpace(manga.SourceName))
        {
            var studios = new[] { manga.SourceName! };
            if (!item.Studios.SequenceEqual(studios, StringComparer.Ordinal))
            {
                item.Studios = studios;
                updated = ItemUpdateType.MetadataDownload;
            }
        }

        // Author/artist have nowhere sensible to live on a plain Folder, so surface
        // them as tags alongside the publishing status rather than dropping them.
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(manga.Status))
        {
            tags.Add(manga.Status!);
        }

        if (!string.IsNullOrWhiteSpace(manga.Author))
        {
            tags.Add(manga.Author!);
        }

        if (!string.IsNullOrWhiteSpace(manga.Artist)
            && !string.Equals(manga.Artist, manga.Author, StringComparison.OrdinalIgnoreCase))
        {
            tags.Add(manga.Artist!);
        }

        if (tags.Count > 0 && !item.Tags.SequenceEqual(tags, StringComparer.Ordinal))
        {
            item.Tags = tags.ToArray();
            updated = ItemUpdateType.MetadataDownload;
        }

        if (manga.Id > 0)
        {
            var sid = manga.Id.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(item.GetProviderId(SuwayomiClient.ProviderId), sid, StringComparison.Ordinal))
            {
                item.SetProviderId(SuwayomiClient.ProviderId, sid);
                updated = ItemUpdateType.MetadataDownload;
            }
        }

        if (updated != ItemUpdateType.None)
        {
            _log.LogDebug("Suwayomi: filled metadata for {Name}", item.Name);
        }

        return updated;
    }
}

/// <summary>Serves cover art straight from Suwayomi for books and series folders.</summary>
public sealed class SuwayomiImageProvider : IRemoteImageProvider
{
    private readonly IHttpClientFactory _http;
    private readonly SuwayomiClient _client;
    private readonly ILibraryManager _library;

    /// <summary>Initializes a new instance of the <see cref="SuwayomiImageProvider"/> class.</summary>
    /// <param name="client">Shared Suwayomi client.</param>
    /// <param name="library">Library manager.</param>
    /// <param name="http">HTTP client factory.</param>
    public SuwayomiImageProvider(SuwayomiClient client, ILibraryManager library, IHttpClientFactory http)
    {
        _client = client;
        _library = library;
        _http = http;
    }

    /// <inheritdoc />
    public string Name => "Suwayomi";

    /// <inheritdoc />
    /// <remarks>
    /// Library options probe this with a dummy Book that has no parent library, so
    /// the Books-library check has to live in <see cref="GetImages"/> — otherwise
    /// the provider never appears under Image Fetchers (Books).
    /// </remarks>
    public bool Supports(BaseItem item) =>
        SuwayomiPlugin.Instance?.Configuration.ProvideImages != false
        && (item is Book || item.GetType() == typeof(Folder));

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary };

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        if (!SuwayomiClient.AppliesTo(item, _library) || !SuwayomiClient.IsSelected(item, _library, images: true))
        {
            return Array.Empty<RemoteImageInfo>();
        }

        SuwayomiManga? manga = null;
        if (int.TryParse(item.GetProviderId(SuwayomiClient.ProviderId), out var id) && id > 0)
        {
            manga = await _client.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        }

        manga ??= await _client.MatchFolderAsync(item.Path, cancellationToken).ConfigureAwait(false);
        if (manga is null)
        {
            return Array.Empty<RemoteImageInfo>();
        }

        return new[]
        {
            new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Primary,
                Url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/api/v1/manga/{1}/thumbnail",
                    SuwayomiClient.BaseUrl,
                    manga.Id),
            },
        };
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var client = _http.CreateClient(NamedClient.Default);
        return client.GetAsync(new Uri(url), cancellationToken);
    }
}

/// <summary>Lets Identify store a Suwayomi manga id on a book.</summary>
public sealed class SuwayomiExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "Suwayomi";

    /// <inheritdoc />
    public string Key => SuwayomiClient.ProviderId;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Book;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Book || item?.GetType() == typeof(Folder);
}
