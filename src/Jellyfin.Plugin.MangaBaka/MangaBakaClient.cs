using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MangaBaka;

/// <summary>Talks to the MangaBaka REST API.</summary>
public sealed class MangaBakaClient
{
    /// <summary>MangaBaka's public API. Not configurable — there is only one.</summary>
    public const string ApiUrl = "https://api.mangabaka.org";

    /// <summary>Stored on items so a later refresh can skip search.</summary>
    public const string ProviderId = "MangaBaka";

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

    /// <summary>
    /// A library refresh resolves items in parallel, and every miss is a request at
    /// somebody else's free API. Two at a time is plenty when the local database is
    /// carrying the common case anyway.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(2, 2);

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

    /// <summary>Looks up a series by MangaBaka id, following a merge if needed.</summary>
    /// <param name="id">MangaBaka series id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The series, or null.</returns>
    public Task<MangaBakaSeries?> GetByIdAsync(int id, CancellationToken ct) =>
        GetByIdAsync(id, hops: 0, ct);

    /// <summary>Searches MangaBaka and returns scored results, best first.</summary>
    /// <param name="query">Series title.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching series, possibly empty.</returns>
    public async Task<IReadOnlyList<MangaBakaSeries>> SearchAsync(string query, CancellationToken ct)
    {
        query = MangaBakaText.StripNoise(query);
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

        var type = Config.SeriesType;
        if (!string.IsNullOrWhiteSpace(type))
        {
            url += "&type=" + Uri.EscapeDataString(type);
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

            var results = new List<MangaBakaSeries>();
            foreach (var item in data.EnumerateArray())
            {
                var series = MangaBakaSeries.Parse(item);
                if (!series.IsDeleted)
                {
                    results.Add(series);
                }
            }

            return MangaBakaMatcher.Rank(results, query);
        }
    }

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

            var series = MangaBakaSeries.Parse(data);
            if (series.IsMerged && hops < 3 && series.MergedWith != id)
            {
                return await GetByIdAsync(series.MergedWith!.Value, hops + 1, ct).ConfigureAwait(false);
            }

            return series.IsDeleted ? null : series;
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = _http.CreateClient(NamedClient.Default);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            // Bound each call rather than mutating the shared client's timeout, so a
            // stalled request cannot hold up a refresh behind the gate above.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("MangaBaka: {Url} returned {Code}", url, (int)resp.StatusCode);
                return null;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MangaBaka: request failed for {Url}", url);
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }
}

/// <summary>Scores and orders candidate series against the title that was searched for.</summary>
public static class MangaBakaMatcher
{
    /// <summary>Scores every candidate against a query and returns them best first.</summary>
    /// <param name="candidates">Candidate series.</param>
    /// <param name="query">The title that was searched for.</param>
    /// <returns>The candidates, scored and ordered.</returns>
    public static IReadOnlyList<MangaBakaSeries> Rank(IEnumerable<MangaBakaSeries> candidates, string query)
    {
        var want = MangaBakaText.Normalize(query);
        var wantedType = MangaBakaPlugin.Instance?.Configuration.SeriesType;
        var scored = new List<MangaBakaSeries>();
        foreach (var series in candidates)
        {
            // The search API filters on type server-side; the local database returns
            // everything, so apply the same rule here and keep the two paths honest.
            if (!TypeMatches(series, wantedType))
            {
                continue;
            }

            var score = 0;
            foreach (var title in series.AllTitles)
            {
                score = Math.Max(score, MangaBakaText.Similarity(want, MangaBakaText.Normalize(title)));
                if (score == 100)
                {
                    break;
                }
            }

            series.MatchScore = score;
            scored.Add(series);
        }

        // Exact-title ties are common: the same story is catalogued several times
        // over, and the busiest entry is reliably the right one.
        return scored
            .OrderByDescending(s => s.MatchScore)
            .ThenByDescending(s => s.Popularity ?? 0)
            .ThenByDescending(s => s.Rating ?? 0)
            .ToList();
    }

    /// <summary>
    /// Whether a series is the type the library is configured for. A series with no
    /// type recorded is allowed through rather than discarded — the type is there to
    /// separate a novel from its manga adaptation, not to reject unlabelled records.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="wantedType">Configured type, or empty for any.</param>
    /// <returns>True when the type matches, is unknown, or no type is configured.</returns>
    public static bool TypeMatches(MangaBakaSeries series, string? wantedType) =>
        string.IsNullOrWhiteSpace(wantedType)
        || string.IsNullOrWhiteSpace(series.Type)
        || string.Equals(series.Type, wantedType, StringComparison.OrdinalIgnoreCase);
}
