using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }

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

    /// <summary>Gets or sets the final volume number, when known.</summary>
    public int? FinalVolume { get; set; }

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
}

/// <summary>Talks to the MangaBaka REST API and resolves folder names to series.</summary>
public sealed class MangaBakaClient
{
    private static readonly Regex Noise = new(
        @"\s*[\(\[][^\)\]]*[\)\]]|\s*[-–]\s*(Complete|Vol(ume)?\.?\s*\d+).*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Sent on every request. MangaBaka does filter some user-agents (Mozilla/3.0
    /// is refused, for instance), so we send a known-good one — and since this can
    /// make a few hundred searches during a library refresh, it carries the plugin
    /// name and repo so the operators can tell who is calling.
    ///
    /// It is also, regrettably, Netscape Navigator 4.08.
    /// </summary>
    public const string UserAgent =
        "Mozilla/4.08 [en] (Win95; I ;Nav) Jellyfin-Plugin-MangaBaka/1.0 "
        + "(+https://git.azuresucks.net/roberth/Jellyfin-Plugins)";

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

    /// <summary>Finds the best MangaBaka series for a folder name.</summary>
    /// <param name="folderName">Series folder name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The best match, or null.</returns>
    public async Task<MangaBakaSeries?> FindAsync(string folderName, CancellationToken ct)
    {
        var cfg = Config;
        var query = Noise.Replace(folderName, " ").Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/v1/series/search?q={1}",
            cfg.ApiUrl.TrimEnd('/'),
            Uri.EscapeDataString(query));
        if (!string.IsNullOrWhiteSpace(cfg.SeriesType))
        {
            url += "&type=" + Uri.EscapeDataString(cfg.SeriesType);
        }

        JsonDocument doc;
        try
        {
            using var client = _http.CreateClient(NamedClient.Default);
            client.Timeout = TimeSpan.FromSeconds(30);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
            // MangaBaka rejects unrecognised user-agents with 403, so identify explicitly.
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("MangaBaka: search returned {Code} for {Query}", (int)resp.StatusCode, query);
                return null;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MangaBaka: search failed for {Query}", query);
            return null;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var want = Normalize(folderName);
            JsonElement best = default;
            var bestScore = -1;

            foreach (var item in data.EnumerateArray())
            {
                var candidate = Str(item, "title");
                var score = Similarity(want, Normalize(candidate));

                // A romanised or native title may match where the English one doesn't.
                foreach (var alt in new[] { Str(item, "romanized_title"), Str(item, "native_title") })
                {
                    if (!string.IsNullOrEmpty(alt))
                    {
                        score = Math.Max(score, Similarity(want, Normalize(alt)));
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = item.Clone();
                }
            }

            if (bestScore < Config.MinMatchScore)
            {
                _log.LogDebug(
                    "MangaBaka: best match for {Folder} scored {Score}, below threshold {Min}",
                    folderName, bestScore, Config.MinMatchScore);
                return null;
            }

            return Parse(best);
        }
    }

    private static MangaBakaSeries Parse(JsonElement e)
    {
        var s = new MangaBakaSeries
        {
            Id = e.TryGetProperty("id", out var id) && id.TryGetInt32(out var i) ? i : 0,
            Title = Str(e, "title"),
            Description = Str(e, "description"),
            Type = Str(e, "type"),
            Status = Str(e, "status"),
            ContentRating = Str(e, "content_rating"),
            Url = Str(e, "canonical_url"),
        };

        if (e.TryGetProperty("year", out var y) && y.TryGetInt32(out var yy))
        {
            s.Year = yy;
        }

        if (e.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Number)
        {
            s.Rating = r.GetDouble();
        }

        if (e.TryGetProperty("final_volume", out var fv) && fv.TryGetInt32(out var fvv))
        {
            s.FinalVolume = fvv;
        }

        AddAll(e, "genres", s.Genres, prettify: true);
        AddAll(e, "tags", s.Tags, prettify: false);
        AddAll(e, "authors", s.Authors, prettify: false);
        AddAll(e, "artists", s.Artists, prettify: false);

        // cover.raw.url is the full-size original; the x150/x250/x350 variants are thumbnails.
        if (e.TryGetProperty("cover", out var cover) && cover.ValueKind == JsonValueKind.Object
            && cover.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.Object)
        {
            s.CoverUrl = Str(raw, "url");
        }

        return s;
    }

    private static void AddAll(JsonElement e, string prop, List<string> into, bool prettify)
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

            // MangaBaka genres arrive as slugs: slice_of_life -> Slice Of Life
            into.Add(prettify ? Prettify(s!) : s!);
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

/// <summary>Fills series-folder metadata from MangaBaka.</summary>
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
        var series = await _client.FindAsync(item.Name, cancellationToken).ConfigureAwait(false);
        if (series is null)
        {
            return ItemUpdateType.None;
        }

        if (cfg.SkipExplicit
            && (string.Equals(series.ContentRating, "erotica", StringComparison.OrdinalIgnoreCase)
                || string.Equals(series.ContentRating, "pornographic", StringComparison.OrdinalIgnoreCase)))
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
            // MangaBaka scores 0-100; Jellyfin's community rating is 0-10.
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

        if (updated != ItemUpdateType.None)
        {
            _log.LogDebug("MangaBaka: filled metadata for {Name} (id {Id})", item.Name, series.Id);
        }

        return updated;
    }
}

/// <summary>Serves series-folder cover art from MangaBaka.</summary>
public sealed class MangaBakaFolderImageProvider : IRemoteImageProvider
{
    private readonly IHttpClientFactory _http;
    private readonly MangaBakaClient _client;

    /// <summary>Initializes a new instance of the <see cref="MangaBakaFolderImageProvider"/> class.</summary>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="log">Logger.</param>
    public MangaBakaFolderImageProvider(IHttpClientFactory http, ILogger<MangaBakaFolderImageProvider> log)
    {
        _http = http;
        _client = new MangaBakaClient(http, log);
    }

    /// <inheritdoc />
    public string Name => "MangaBaka";

    /// <inheritdoc />
    public bool Supports(BaseItem item) =>
        item?.GetType() == typeof(Folder)
        && MangaBakaPlugin.Instance?.Configuration.ProvideImages != false;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary };

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.Name))
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var series = await _client.FindAsync(item.Name, cancellationToken).ConfigureAwait(false);
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
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
        request.Headers.TryAddWithoutValidation("User-Agent", MangaBakaClient.UserAgent);
        return client.SendAsync(request, cancellationToken);
    }
}
