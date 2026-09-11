using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MangaBaka;

/// <summary>A MangaBaka series.</summary>
public sealed class MangaBakaSeries
{
    /// <summary>Gets or sets the MangaBaka id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the primary title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets every known title used for matching.</summary>
    public List<string> Titles { get; } = new();

    /// <summary>Gets or sets the synopsis.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the series type (novel / manga / manhwa ...).</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the publication status.</summary>
    public string? Status { get; set; }

    /// <summary>Gets or sets the content rating (safe / suggestive / erotica ...).</summary>
    public string? ContentRating { get; set; }

    /// <summary>Gets or sets the first publication year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the community rating, 0-100.</summary>
    public double? Rating { get; set; }

    /// <summary>Gets or sets the final volume, when known.</summary>
    public string? FinalVolume { get; set; }

    /// <summary>Gets or sets the genres.</summary>
    public List<string> Genres { get; } = new();

    /// <summary>Gets or sets the tags.</summary>
    public List<string> Tags { get; } = new();

    /// <summary>Gets the authors.</summary>
    public List<string> Authors { get; } = new();

    /// <summary>Gets the artists.</summary>
    public List<string> Artists { get; } = new();

    /// <summary>Gets or sets the cover image URL.</summary>
    public string? CoverUrl { get; set; }

    /// <summary>Gets or sets the canonical MangaBaka page.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets how closely this result matched the query, 0-100.</summary>
    public int MatchScore { get; set; }
}

/// <summary>Talks to the MangaBaka REST API and resolves titles to series.</summary>
public sealed class MangaBakaClient
{
    /// <summary>MangaBaka's public API. Not configurable — there is only one.</summary>
    public const string ApiUrl = "https://api.mangabaka.org";

    /// <summary>Stored on items so a later refresh can skip search.</summary>
    public const string ProviderId = "MangaBaka";

    private static readonly Regex Noise = new(
        @"\s*[\(\[][^\)\]]*[\)\]]|\s*[-–]\s*(Complete|Vol(ume)?\.?\s*\d+).*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Sent on every request. MangaBaka does filter some user-agents (Mozilla/3.0
    /// is refused, for instance), so we send a known-good one — and since this can
    /// make a few hundred searches during a library refresh, it names the plugin so
    /// the operators can tell who is calling.
    ///
    /// It is also, regrettably, Netscape Navigator 9 on Windows NT 3.5.
    /// </summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows; U; Windows NT 3.5; en-US; rv:1.8.1.11pre) "
        + "Gecko/20071206 Firefox/2.0.0.11 Navigator/9.0.0.5 "
        + "Jellyfin-Plugin-MangaBaka/12.0";

    private readonly IHttpClientFactory _http;
    private readonly ILogger _log;

    /// <summary>Initializes a new instance of the <see cref="MangaBakaClient"/> class.</summary>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="log">Logger.</param>
    public MangaBakaClient(IHttpClientFactory http, ILogger log)
    {
        _http = http;
        _log = log;
    }

    private static PluginConfiguration Config =>
        MangaBakaPlugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>v1 is stable; v2 is the spec's beta channel.</summary>
    private static bool UseV2 =>
        string.Equals(Config.ApiVersion, "v2", StringComparison.OrdinalIgnoreCase);

    private static string ApiPrefix => UseV2 ? "v2" : "v1";

    /// <summary>Normalises a title for comparison.</summary>
    /// <param name="value">Raw title.</param>
    /// <returns>Lowercase alphanumeric words.</returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var s = Noise.Replace(value, " ").ToLowerInvariant();
        s = Regex.Replace(s, "[^a-z0-9]+", " ");
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Title used to look a book up. Series name wins when Jellyfin has one,
    /// otherwise the item name (a series folder, or a volume filename).
    /// </summary>
    /// <param name="info">Book lookup info.</param>
    /// <returns>The query string, or empty.</returns>
    public static string QueryTitle(BookInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.SeriesName))
        {
            return info.SeriesName.Trim();
        }

        return (info.Name ?? string.Empty).Trim();
    }

    /// <summary>
    /// Similarity of two titles as a 0-100 score, using token overlap weighted
    /// toward the query. Cheap, and good enough to separate a real match from
    /// MangaBaka's looser search results.
    /// </summary>
    /// <param name="a">First title.</param>
    /// <param name="b">Second title.</param>
    /// <returns>A score from 0 to 100.</returns>
    public static int Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 100;
        }

        var at = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bt = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        if (at.Length == 0 || bt.Count == 0)
        {
            return 0;
        }

        var hit = at.Count(bt.Contains);
        var recall = (double)hit / at.Length;          // how much of the query was found
        var precision = (double)hit / bt.Count;        // how much of the candidate was used
        if (hit == 0)
        {
            return 0;
        }

        // Favour recall: a candidate carrying extra subtitle words is still fine.
        var score = ((recall * 0.75) + (precision * 0.25)) * 100;
        return (int)Math.Round(score);
    }

    /// <summary>Looks up a series by MangaBaka id, following a merge if needed.</summary>
    /// <param name="id">MangaBaka series id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The series, or null.</returns>
    public Task<MangaBakaSeries?> GetByIdAsync(int id, CancellationToken ct) =>
        GetByIdAsync(id, hops: 0, ct);

    private async Task<MangaBakaSeries?> GetByIdAsync(int id, int hops, CancellationToken ct)
    {
        var url = string.Format(CultureInfo.InvariantCulture, "{0}/{1}/series/{2}", ApiUrl, ApiPrefix, id);
        if (UseV2)
        {
            url += "?schema=full";
        }

        var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var series = Parse(data.Clone());
            if (hops < 3
                && string.Equals(Str(data, "state"), "merged", StringComparison.OrdinalIgnoreCase)
                && data.TryGetProperty("merged_with", out var merged)
                && merged.TryGetInt32(out var next)
                && next > 0
                && next != id)
            {
                return await GetByIdAsync(next, hops + 1, ct).ConfigureAwait(false);
            }

            return series;
        }
    }

    /// <summary>Searches MangaBaka and returns scored results, best first.</summary>
    /// <param name="query">Series title.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching series, possibly empty.</returns>
    public async Task<IReadOnlyList<MangaBakaSeries>> SearchAsync(string query, CancellationToken ct)
    {
        query = Noise.Replace(query, " ").Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<MangaBakaSeries>();
        }

        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/{1}/series/search?q={2}",
            ApiUrl,
            ApiPrefix,
            Uri.EscapeDataString(query));
        if (UseV2)
        {
            url += "&schema=full";
        }

        var cfg = Config;
        if (!string.IsNullOrWhiteSpace(cfg.SeriesType))
        {
            url += "&type=" + Uri.EscapeDataString(cfg.SeriesType);
        }

        var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (doc is null)
        {
            return Array.Empty<MangaBakaSeries>();
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<MangaBakaSeries>();
            }

            var want = Normalize(query);
            var results = new List<MangaBakaSeries>();
            foreach (var item in data.EnumerateArray())
            {
                if (string.Equals(Str(item, "state"), "deleted", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var series = Parse(item);
                var score = 0;
                foreach (var title in series.Titles.Prepend(series.Title).Where(t => !string.IsNullOrEmpty(t)))
                {
                    score = Math.Max(score, Similarity(want, Normalize(title)));
                }

                series.MatchScore = score;
                results.Add(series);
            }

            return results.OrderByDescending(s => s.MatchScore).ToList();
        }
    }

    /// <summary>Finds the best MangaBaka series for a title.</summary>
    /// <param name="title">Series title or folder name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The best match, or null.</returns>
    public async Task<MangaBakaSeries?> FindAsync(string title, CancellationToken ct)
    {
        var results = await SearchAsync(title, ct).ConfigureAwait(false);
        var best = results.FirstOrDefault();
        if (best is null)
        {
            return null;
        }

        if (best.MatchScore < Config.MinMatchScore)
        {
            _log.LogDebug(
                "MangaBaka: best match for {Title} scored {Score}, below threshold {Min}",
                title, best.MatchScore, Config.MinMatchScore);
            return null;
        }

        return best;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var client = _http.CreateClient(NamedClient.Default);
            client.Timeout = TimeSpan.FromSeconds(30);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("MangaBaka: {Url} returned {Code}", url, (int)resp.StatusCode);
                return null;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MangaBaka: request failed for {Url}", url);
            return null;
        }
    }

    private static MangaBakaSeries Parse(JsonElement e)
    {
        var s = new MangaBakaSeries
        {
            Title = PrimaryTitle(e),
            Description = Str(e, "description"),
            Type = Str(e, "type"),
            Status = Str(e, "status"),
            ContentRating = Str(e, "content_rating"),
            Url = Str(e, "canonical_url"),
            CoverUrl = CoverUrl(e),
            FinalVolume = FinalVolume(e),
            Year = Year(e),
        };

        if (e.TryGetProperty("id", out var id) && id.TryGetInt32(out var i))
        {
            s.Id = i;
        }

        if (e.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Number)
        {
            s.Rating = r.GetDouble();
        }

        foreach (var title in TitleCandidates(e))
        {
            if (!s.Titles.Contains(title, StringComparer.OrdinalIgnoreCase))
            {
                s.Titles.Add(title);
            }
        }

        AddStaff(e, "authors", s.Authors);
        AddStaff(e, "artists", s.Artists);
        AddTags(e, s);

        return s;
    }

    /// <summary>
    /// Spec: use <c>titles[]</c> (language, traits, title, is_primary).
    /// v1 still emits the deprecated title / native_title / romanized_title fields.
    /// </summary>
    private static string? PrimaryTitle(JsonElement e)
    {
        if (e.TryGetProperty("titles", out var titles) && titles.ValueKind == JsonValueKind.Array)
        {
            string? primary = null;
            string? official = null;
            string? first = null;
            foreach (var t in titles.EnumerateArray())
            {
                var name = Str(t, "title");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                first ??= name;
                if (t.TryGetProperty("is_primary", out var p) && p.ValueKind == JsonValueKind.True)
                {
                    primary = name;
                }

                if (t.TryGetProperty("traits", out var traits) && traits.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tr in traits.EnumerateArray())
                    {
                        if (tr.ValueKind == JsonValueKind.String
                            && string.Equals(tr.GetString(), "official", StringComparison.OrdinalIgnoreCase))
                        {
                            official ??= name;
                        }
                    }
                }
            }

            if (primary is not null || official is not null || first is not null)
            {
                return primary ?? official ?? first;
            }
        }

        return Str(e, "title");
    }

    private static IEnumerable<string> TitleCandidates(JsonElement e)
    {
        if (e.TryGetProperty("titles", out var titles) && titles.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in titles.EnumerateArray())
            {
                var name = Str(t, "title");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    yield return name!;
                }
            }
        }

        foreach (var key in new[] { "title", "native_title", "romanized_title" })
        {
            var name = Str(e, key);
            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return name!;
            }
        }
    }

    /// <summary>Spec: use <c>published.start_date</c>; v1 still emits deprecated <c>year</c>.</summary>
    private static int? Year(JsonElement e)
    {
        if (e.TryGetProperty("published", out var pub) && pub.ValueKind == JsonValueKind.Object)
        {
            var start = Str(pub, "start_date");
            if (!string.IsNullOrEmpty(start) && start.Length >= 4
                && int.TryParse(start.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y)
                && y > 0)
            {
                return y;
            }
        }

        if (e.TryGetProperty("year", out var year) && year.TryGetInt32(out var yy) && yy > 0)
        {
            return yy;
        }

        return null;
    }

    /// <summary>
    /// v1: <c>cover.raw.url</c>. v2: <c>cover.raw</c> is the URL string itself.
    /// </summary>
    private static string? CoverUrl(JsonElement e)
    {
        if (!e.TryGetProperty("cover", out var cover) || cover.ValueKind != JsonValueKind.Object
            || !cover.TryGetProperty("raw", out var raw))
        {
            return null;
        }

        if (raw.ValueKind == JsonValueKind.String)
        {
            return raw.GetString();
        }

        return raw.ValueKind == JsonValueKind.Object ? Str(raw, "url") : null;
    }

    /// <summary>v1 sends a string; v2 sends a number.</summary>
    private static string? FinalVolume(JsonElement e)
    {
        if (!e.TryGetProperty("final_volume", out var fv)
            || fv.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (fv.ValueKind == JsonValueKind.String)
        {
            return string.IsNullOrWhiteSpace(fv.GetString()) ? null : fv.GetString();
        }

        if (fv.ValueKind == JsonValueKind.Number)
        {
            return fv.TryGetInt32(out var n) ? n.ToString(CultureInfo.InvariantCulture) : fv.GetRawText();
        }

        return null;
    }

    /// <summary>
    /// Spec: filter <c>tags_v2</c> (v1) / <c>tags</c> objects (v2) on <c>is_genre</c>.
    /// Falls back to the deprecated string <c>genres</c> / <c>tags</c> arrays.
    /// </summary>
    private static void AddTags(JsonElement e, MangaBakaSeries s)
    {
        foreach (var key in new[] { "tags_v2", "tags" })
        {
            if (!e.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var anyObject = false;
            foreach (var v in arr.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                anyObject = true;
                var name = Str(v, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (name.Contains('_', StringComparison.Ordinal))
                {
                    name = Prettify(name);
                }

                var isGenre = v.TryGetProperty("is_genre", out var g) && g.ValueKind == JsonValueKind.True;
                (isGenre ? s.Genres : s.Tags).Add(name);
            }

            if (anyObject)
            {
                return;
            }
        }

        AddStrings(e, "genres", s.Genres, prettify: true);
        AddStrings(e, "tags", s.Tags, prettify: false);
    }

    private static void AddStaff(JsonElement e, string prop, List<string> into) =>
        AddStrings(e, prop, into, prettify: false);

    private static void AddStrings(JsonElement e, string prop, List<string> into, bool prettify)
    {
        if (!e.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var v in arr.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var s = v.GetString();
            if (string.IsNullOrWhiteSpace(s))
            {
                continue;
            }

            into.Add(prettify ? Prettify(s) : s);
        }
    }

    private static string Prettify(string slug)
    {
        var parts = slug.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Book metadata from MangaBaka. This is the provider that appears in a book
/// library's metadata-downloader list, matching Google Books / Comic Vine.
/// MangaBaka is a series database, so a volume is filled with its series data.
/// </summary>
public sealed class MangaBakaMetadataProvider : IRemoteMetadataProvider<Book, BookInfo>
{
    private readonly IHttpClientFactory _http;
    private readonly MangaBakaClient _client;
    private readonly ILogger<MangaBakaMetadataProvider> _log;

    /// <summary>Initializes a new instance of the <see cref="MangaBakaMetadataProvider"/> class.</summary>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="log">Logger.</param>
    public MangaBakaMetadataProvider(IHttpClientFactory http, ILogger<MangaBakaMetadataProvider> log)
    {
        _http = http;
        _log = log;
        _client = new MangaBakaClient(http, log);
    }

    /// <inheritdoc />
    public string Name => "MangaBaka";

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(BookInfo searchInfo, CancellationToken cancellationToken)
    {
        if (int.TryParse(searchInfo.GetProviderId(MangaBakaClient.ProviderId), out var id) && id > 0)
        {
            var byId = await _client.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
            return byId is null ? Array.Empty<RemoteSearchResult>() : new[] { ToSearchResult(byId) };
        }

        var query = MangaBakaClient.QueryTitle(searchInfo);
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<RemoteSearchResult>();
        }

        var results = await _client.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        return results.Select(ToSearchResult).ToArray();
    }

    /// <inheritdoc />
    public async Task<MetadataResult<Book>> GetMetadata(BookInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Book>();
        var series = await ResolveAsync(info, cancellationToken).ConfigureAwait(false);
        if (series is null)
        {
            return result;
        }

        var cfg = MangaBakaPlugin.Instance?.Configuration ?? new PluginConfiguration();
        if (ShouldSkip(series, cfg))
        {
            _log.LogDebug("MangaBaka: skipping {Name} (content rating {Rating})", info.Name, series.ContentRating);
            return result;
        }

        result.Item = ToBook(series, cfg);
        result.HasMetadata = true;
        foreach (var author in series.Authors)
        {
            result.AddPerson(new PersonInfo { Name = author, Type = PersonKind.Author });
        }

        foreach (var artist in series.Artists)
        {
            result.AddPerson(new PersonInfo { Name = artist, Type = PersonKind.Artist });
        }

        return result;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var client = _http.CreateClient(NamedClient.Default);
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
        request.Headers.TryAddWithoutValidation("User-Agent", MangaBakaClient.UserAgent);
        return client.SendAsync(request, cancellationToken);
    }

    internal async Task<MangaBakaSeries?> ResolveAsync(BookInfo info, CancellationToken ct)
    {
        if (int.TryParse(info.GetProviderId(MangaBakaClient.ProviderId), out var id) && id > 0)
        {
            var byId = await _client.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (byId is not null)
            {
                return byId;
            }
        }

        var query = MangaBakaClient.QueryTitle(info);
        return string.IsNullOrWhiteSpace(query) ? null : await _client.FindAsync(query, ct).ConfigureAwait(false);
    }

    internal static bool ShouldSkip(MangaBakaSeries series, PluginConfiguration cfg) =>
        cfg.SkipExplicit
        && (string.Equals(series.ContentRating, "erotica", StringComparison.OrdinalIgnoreCase)
            || string.Equals(series.ContentRating, "pornographic", StringComparison.OrdinalIgnoreCase));

    internal static Book ToBook(MangaBakaSeries series, PluginConfiguration cfg)
    {
        var book = new Book();
        if (!string.IsNullOrWhiteSpace(series.Title))
        {
            book.SeriesName = series.Title;
        }

        if (!string.IsNullOrWhiteSpace(series.Description))
        {
            book.Overview = series.Description;
        }

        if (series.Rating is > 0)
        {
            book.CommunityRating = (float)Math.Round(series.Rating.Value / 10d, 1);
        }

        if (series.Year is > 0)
        {
            book.ProductionYear = series.Year;
        }

        foreach (var genre in series.Genres)
        {
            book.AddGenre(genre);
        }

        if (!string.IsNullOrWhiteSpace(series.Status))
        {
            book.AddTag(char.ToUpperInvariant(series.Status![0]) + series.Status[1..]);
        }

        if (cfg.ImportTags)
        {
            foreach (var tag in series.Tags)
            {
                book.AddTag(tag);
            }
        }

        var studios = series.Authors.Concat(series.Artists).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var studio in studios)
        {
            book.AddStudio(studio);
        }

        if (series.Id > 0)
        {
            book.SetProviderId(MangaBakaClient.ProviderId, series.Id.ToString(CultureInfo.InvariantCulture));
        }

        return book;
    }

    private static RemoteSearchResult ToSearchResult(MangaBakaSeries series)
    {
        var remote = new RemoteSearchResult
        {
            SearchProviderName = "MangaBaka",
            Name = series.Title,
            Overview = series.Description,
            ProductionYear = series.Year,
            ImageUrl = series.CoverUrl,
        };
        if (series.Id > 0)
        {
            remote.SetProviderId(MangaBakaClient.ProviderId, series.Id.ToString(CultureInfo.InvariantCulture));
        }

        return remote;
    }
}

/// <summary>
/// Fills series-folder metadata from MangaBaka. Book libraries still model a
/// series directory as a plain Folder, which never appears in the metadata
/// downloader list — this runs during refresh so the folder is not left blank.
/// </summary>
public sealed class MangaBakaFolderMetadataProvider : ICustomMetadataProvider<Folder>, IHasItemChangeMonitor
{
    private readonly MangaBakaClient _client;
    private readonly ILogger<MangaBakaFolderMetadataProvider> _log;

    /// <summary>Initializes a new instance of the <see cref="MangaBakaFolderMetadataProvider"/> class.</summary>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="log">Logger.</param>
    public MangaBakaFolderMetadataProvider(IHttpClientFactory http, ILogger<MangaBakaFolderMetadataProvider> log)
    {
        _log = log;
        _client = new MangaBakaClient(http, log);
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
        // Series, Season, BoxSet and CollectionFolder all derive from Folder;
        // only a plain Folder is a book/manga series directory.
        if (item.GetType() != typeof(Folder) || string.IsNullOrEmpty(item.Name))
        {
            return ItemUpdateType.None;
        }

        var cfg = MangaBakaPlugin.Instance?.Configuration ?? new PluginConfiguration();
        MangaBakaSeries? series = null;
        if (int.TryParse(item.GetProviderId(MangaBakaClient.ProviderId), out var id) && id > 0)
        {
            series = await _client.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        }

        series ??= await _client.FindAsync(item.Name, cancellationToken).ConfigureAwait(false);
        if (series is null)
        {
            return ItemUpdateType.None;
        }

        if (MangaBakaMetadataProvider.ShouldSkip(series, cfg))
        {
            _log.LogDebug("MangaBaka: skipping {Name} (content rating {Rating})", item.Name, series.ContentRating);
            return ItemUpdateType.None;
        }

        var updated = ItemUpdateType.None;

        if (!string.IsNullOrWhiteSpace(series.Description)
            && !string.Equals(item.Overview, series.Description, StringComparison.Ordinal))
        {
            item.Overview = series.Description;
            updated = ItemUpdateType.MetadataDownload;
        }

        if (series.Rating is > 0)
        {
            var rating = (float)Math.Round(series.Rating.Value / 10d, 1);
            if (item.CommunityRating != rating)
            {
                item.CommunityRating = rating;
                updated = ItemUpdateType.MetadataDownload;
            }
        }

        if (series.Year is > 0 && item.ProductionYear != series.Year)
        {
            item.ProductionYear = series.Year;
            updated = ItemUpdateType.MetadataDownload;
        }

        if (series.Genres.Count > 0 && !item.Genres.SequenceEqual(series.Genres, StringComparer.Ordinal))
        {
            item.Genres = series.Genres.ToArray();
            updated = ItemUpdateType.MetadataDownload;
        }

        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(series.Status))
        {
            tags.Add(char.ToUpperInvariant(series.Status![0]) + series.Status[1..]);
        }

        if (cfg.ImportTags)
        {
            tags.AddRange(series.Tags);
        }

        if (tags.Count > 0 && !item.Tags.SequenceEqual(tags, StringComparer.Ordinal))
        {
            item.Tags = tags.ToArray();
            updated = ItemUpdateType.MetadataDownload;
        }

        var studios = series.Authors.Concat(series.Artists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (studios.Length > 0 && !item.Studios.SequenceEqual(studios, StringComparer.Ordinal))
        {
            item.Studios = studios;
            updated = ItemUpdateType.MetadataDownload;
        }

        if (series.Id > 0)
        {
            var sid = series.Id.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(item.GetProviderId(MangaBakaClient.ProviderId), sid, StringComparison.Ordinal))
            {
                item.SetProviderId(MangaBakaClient.ProviderId, sid);
                updated = ItemUpdateType.MetadataDownload;
            }
        }

        if (updated != ItemUpdateType.None)
        {
            _log.LogDebug("MangaBaka: filled metadata for {Name} (id {Id})", item.Name, series.Id);
        }

        return updated;
    }
}

/// <summary>Serves cover art from MangaBaka for books and series folders.</summary>
public sealed class MangaBakaImageProvider : IRemoteImageProvider
{
    private readonly IHttpClientFactory _http;
    private readonly MangaBakaClient _client;

    /// <summary>Initializes a new instance of the <see cref="MangaBakaImageProvider"/> class.</summary>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="log">Logger.</param>
    public MangaBakaImageProvider(IHttpClientFactory http, ILogger<MangaBakaImageProvider> log)
    {
        _http = http;
        _client = new MangaBakaClient(http, log);
    }

    /// <inheritdoc />
    public string Name => "MangaBaka";

    /// <inheritdoc />
    public bool Supports(BaseItem item)
    {
        if (MangaBakaPlugin.Instance?.Configuration.ProvideImages == false)
        {
            return false;
        }

        return item is Book || item?.GetType() == typeof(Folder);
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary };

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        MangaBakaSeries? series = null;
        if (int.TryParse(item.GetProviderId(MangaBakaClient.ProviderId), out var id) && id > 0)
        {
            series = await _client.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        }

        if (series is null)
        {
            var query = item is Book book && !string.IsNullOrWhiteSpace(book.SeriesName)
                ? book.SeriesName
                : item.Name;
            if (string.IsNullOrEmpty(query))
            {
                return Array.Empty<RemoteImageInfo>();
            }

            series = await _client.FindAsync(query, cancellationToken).ConfigureAwait(false);
        }

        if (series?.CoverUrl is null)
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
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var client = _http.CreateClient(NamedClient.Default);
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
