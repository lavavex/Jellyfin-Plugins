using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MangaBaka;

/// <summary>
/// Where series are looked up. The local database and the search API are the two
/// sources, and these are the combinations of them worth having — there is
/// deliberately no way to select neither.
/// </summary>
public static class MatchSources
{
    /// <summary>Local database for exact title matches, search API for the rest.</summary>
    public const string DatabaseThenApi = "database-then-api";

    /// <summary>Local database only: no outbound requests while matching at all.</summary>
    public const string DatabaseOnly = "database-only";

    /// <summary>Search API only, as if the local database were never downloaded.</summary>
    public const string ApiOnly = "api-only";

    /// <summary>Whether the local database should be consulted.</summary>
    /// <param name="source">Configured source.</param>
    /// <returns>True unless the API is the only source.</returns>
    public static bool UsesDatabase(string? source) =>
        !string.Equals(source, ApiOnly, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the search API should be consulted.</summary>
    /// <param name="source">Configured source.</param>
    /// <returns>True unless the database is the only source.</returns>
    public static bool UsesApi(string? source) =>
        !string.Equals(source, DatabaseOnly, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The one place that turns a title into a MangaBaka series: local database first,
/// search API second, with a short-lived memo so the metadata provider, the folder
/// provider and the image provider do not each look the same series up again.
/// </summary>
public sealed class MangaBakaResolver
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
    private const int CacheLimit = 5000;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly MangaBakaDatabase _database;
    private readonly ILibraryManager _library;
    private readonly MangaBakaClient _api;
    private readonly ILogger<MangaBakaResolver> _log;
    private int _warnedNoDatabase;

    /// <summary>Initializes a new instance of the <see cref="MangaBakaResolver"/> class.</summary>
    /// <param name="database">Local database.</param>
    /// <param name="library">Library manager, used to tell a book library from the rest.</param>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="log">Logger.</param>
    public MangaBakaResolver(
        MangaBakaDatabase database,
        ILibraryManager library,
        System.Net.Http.IHttpClientFactory http,
        ILogger<MangaBakaResolver> log)
    {
        _database = database;
        _library = library;
        _log = log;
        _api = new MangaBakaClient(http, log);
    }

    /// <summary>Gets the local database.</summary>
    public MangaBakaDatabase Database => _database;

    private static PluginConfiguration Config =>
        MangaBakaPlugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Whether this item belongs to a library the plugin should touch.
    ///
    /// Series, Season, BoxSet and CollectionFolder all derive from Folder, and a
    /// photo library is made of plain Folders too — without this the plugin would
    /// search MangaBaka for every folder on the server and could hang a manga cover
    /// on a holiday album.
    /// </summary>
    /// <param name="item">The item being refreshed.</param>
    /// <returns>True when the item is a book, or a series folder in a book library.</returns>
    public bool AppliesTo(BaseItem? item)
    {
        if (item is null)
        {
            return false;
        }

        if (item is not Book && item.GetType() != typeof(Folder))
        {
            return false;
        }

        if (!Config.RestrictToBookLibraries)
        {
            return true;
        }

        try
        {
            return _library.GetInheritedContentType(item) == CollectionType.books;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "MangaBaka: could not read the library type for {Path}", item.Path);
            return false;
        }
    }

    /// <summary>
    /// Whether this plugin is ticked in the library's Book metadata or image
    /// fetchers. Series folders are plain <see cref="Folder"/>s, which have no row
    /// in that UI, so Jellyfin never applies the checkbox to the folder provider
    /// itself — we have to.
    /// </summary>
    /// <param name="item">Item being refreshed.</param>
    /// <param name="images">True to read Image Fetchers, false for Metadata downloaders.</param>
    /// <returns>True when MangaBaka is selected for this library.</returns>
    public bool IsSelected(BaseItem item, bool images)
    {
        var options = _library.GetLibraryOptions(item);
        var type = options.GetTypeOptions("Book") ?? options.GetTypeOptions(item.GetType().Name);
        if (type is null)
        {
            return false;
        }

        var selected = images ? type.ImageFetchers : type.MetadataFetchers;
        return selected.Any(name => string.Equals(name, "MangaBaka", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves the series for a stored provider id, falling back to a title.</summary>
    /// <param name="providerId">MangaBaka id already stored on the item, if any.</param>
    /// <param name="title">Series title or folder name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The best match, or null.</returns>
    public async Task<MangaBakaSeries?> ResolveAsync(string? providerId, string? title, CancellationToken ct)
    {
        if (int.TryParse(providerId, out var id) && id > 0)
        {
            var known = await GetByIdAsync(id, ct).ConfigureAwait(false);
            if (known is not null)
            {
                return known;
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var key = MangaBakaText.Normalize(title);
        if (key.Length == 0)
        {
            return null;
        }

        if (_cache.TryGetValue(key, out var cached) && cached.Expires > DateTime.UtcNow)
        {
            return cached.Series;
        }

        var best = (await SearchAsync(title, ct).ConfigureAwait(false)).FirstOrDefault();
        if (best is not null && best.MatchScore < Config.MinMatchScore)
        {
            _log.LogDebug(
                "MangaBaka: best match for {Title} scored {Score}, below threshold {Min}",
                title,
                best.MatchScore,
                Config.MinMatchScore);
            best = null;
        }

        if (_cache.Count > CacheLimit)
        {
            _cache.Clear();
        }

        _cache[key] = new CacheEntry(best, DateTime.UtcNow + CacheLifetime);
        return best;
    }

    /// <summary>Looks a series up by MangaBaka id.</summary>
    /// <param name="id">MangaBaka series id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The series, or null.</returns>
    public async Task<MangaBakaSeries?> GetByIdAsync(int id, CancellationToken ct)
    {
        var source = Config.MatchSource;
        if (MatchSources.UsesDatabase(source) && LoadDatabase() is not null)
        {
            var local = _database.GetById(id);
            if (local is not null)
            {
                return local;
            }
        }

        return MatchSources.UsesApi(source) ? await _api.GetByIdAsync(id, ct).ConfigureAwait(false) : null;
    }

    /// <summary>
    /// Candidate series for a title, best first. The local database answers exact
    /// title matches — which is nearly everything, because folder names come from
    /// the same catalogues MangaBaka aggregates — and the API covers the rest.
    /// </summary>
    /// <param name="title">Series title or folder name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Scored candidates, possibly empty.</returns>
    public async Task<IReadOnlyList<MangaBakaSeries>> SearchAsync(string? title, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Array.Empty<MangaBakaSeries>();
        }

        var source = Config.MatchSource;
        if (MatchSources.UsesDatabase(source) && LoadDatabase() is not null)
        {
            var local = _database.Lookup(title);
            if (local.Count > 0)
            {
                return MangaBakaMatcher.Rank(local, title);
            }
        }

        return MatchSources.UsesApi(source)
            ? await _api.SearchAsync(title, ct).ConfigureAwait(false)
            : Array.Empty<MangaBakaSeries>();
    }

    /// <summary>
    /// Title used to look a book up. Series name wins when Jellyfin has one,
    /// otherwise the item name (a series folder, or a volume filename).
    /// </summary>
    /// <param name="info">Book lookup info.</param>
    /// <returns>The query string, or empty.</returns>
    public static string QueryTitle(BookInfo info) =>
        (!string.IsNullOrWhiteSpace(info.SeriesName) ? info.SeriesName : info.Name ?? string.Empty).Trim();

    /// <summary>Drops anything the user asked to be left out of the library.</summary>
    /// <param name="series">The candidate series.</param>
    /// <returns>True when the series should not be written.</returns>
    public static bool ShouldSkip(MangaBakaSeries series) =>
        Config.SkipExplicit
        && (string.Equals(series.ContentRating, "erotica", StringComparison.OrdinalIgnoreCase)
            || string.Equals(series.ContentRating, "pornographic", StringComparison.OrdinalIgnoreCase));

    /// <summary>Forgets memoised lookups, so the next refresh re-reads everything.</summary>
    public void ClearCache()
    {
        _cache.Clear();
        _warnedNoDatabase = 0;
    }

    /// <summary>
    /// Opens the local database, complaining once if the only configured source is
    /// one that has not been downloaded yet — otherwise nothing resolves and the
    /// log says nothing about why.
    /// </summary>
    private SnapshotInfo? LoadDatabase()
    {
        var info = _database.Load();
        if (info is null
            && !MatchSources.UsesApi(Config.MatchSource)
            && Interlocked.Exchange(ref _warnedNoDatabase, 1) == 0)
        {
            _log.LogWarning(
                "MangaBaka: set to match against the local database only, but none has been "
                + "downloaded — run the \"Download MangaBaka database\" scheduled task, or allow "
                + "the search API. Nothing will match until then.");
        }

        return info;
    }

    private readonly record struct CacheEntry(MangaBakaSeries? Series, DateTime Expires);
}
