using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Formats.Tar;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.MangaBaka;

/// <summary>What the plugin knows about the local copy of MangaBaka's database.</summary>
public sealed class SnapshotInfo
{
    /// <summary>Gets or sets the on-disk format version, so a future change can rebuild.</summary>
    public int FormatVersion { get; set; }

    /// <summary>Gets or sets the dump that was downloaded.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the SHA-1 of the archive, as published alongside it.</summary>
    public string? Sha1 { get; set; }

    /// <summary>Gets or sets the archive's Last-Modified, used for conditional requests.</summary>
    public string? LastModified { get; set; }

    /// <summary>Gets or sets when the local copy was built.</summary>
    public DateTime BuiltUtc { get; set; }

    /// <summary>Gets or sets how many series were kept.</summary>
    public int SeriesCount { get; set; }

    /// <summary>Gets or sets how many title aliases are indexed.</summary>
    public int AliasCount { get; set; }

    /// <summary>Gets or sets the size of the trimmed record file, in bytes.</summary>
    public long Bytes { get; set; }
}

/// <summary>
/// A local copy of MangaBaka's nightly database dump.
///
/// The point is mass updates: matching a whole library through the search API is
/// one HTTP request per series, which is slow, rate-limited and rude. With the
/// dump on disk a full refresh does no network I/O at all.
///
/// The published dump is a 540 MB gzipped tar holding 4 GB of JSONL — roughly
/// 18 KB a series, nearly all of it cover variants, relationships and raw
/// upstream payloads. It is streamed and trimmed on the way in, so what lands on
/// disk is the handful of fields this plugin writes into Jellyfin, plus a compact
/// hash index of every title each series is known by.
/// </summary>
public sealed class MangaBakaDatabase
{
    /// <summary>Bump when the trimmed record layout changes, to force a rebuild.</summary>
    public const int FormatVersion = 1;

    /// <summary>The dump this plugin downloads.</summary>
    /// <remarks>
    /// The <c>.zst</c> variants are ~30% smaller but .NET has no built-in zstd, and a
    /// native dependency in a Jellyfin plugin is not worth 160 MB. <c>series.full.*</c>
    /// additionally carries the raw upstream API responses, which this plugin has no
    /// use for and which nearly doubles the download.
    /// </remarks>
    public const string DumpUrl = "https://api.mangabaka.org/v1/database/series.jsonl.tar.gz";

    /// <summary>Checksum published next to the dump.</summary>
    public const string ChecksumUrl = DumpUrl + ".sha1";

    private const string RecordsFile = "series.jsonl";
    private const string TitleIndexFile = "titles.idx";
    private const string IdIndexFile = "ids.idx";
    private const string SnapshotFile = "snapshot.json";

    private const int TitleIndexMagic = 0x49544244;   // "DBTI"
    private const int IdIndexMagic = 0x49444244;      // "DBID"

    // Trimming limits. Alt titles exist only to confirm a match, tags are capped
    // well above any sane display limit, and a synopsis longer than this is a
    // wiki article that Jellyfin would truncate on screen anyway.
    private const int MaxAltTitles = 8;
    private const int MaxStoredTags = 20;
    private const int MaxDescription = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<MangaBakaDatabase> _log;
    private readonly IHttpClientFactory _http;
    private readonly SemaphoreSlim _updateLock = new(1, 1);

    private Snapshot? _snapshot;
    private bool _loadAttempted;

    /// <summary>Initializes a new instance of the <see cref="MangaBakaDatabase"/> class.</summary>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="log">Logger.</param>
    public MangaBakaDatabase(IHttpClientFactory http, ILogger<MangaBakaDatabase> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>Gets a value indicating whether a usable local copy is loaded.</summary>
    public bool IsReady => Volatile.Read(ref _snapshot) is not null;

    /// <summary>Gets what is known about the loaded copy, or null when there is none.</summary>
    public SnapshotInfo? Info => Volatile.Read(ref _snapshot)?.Info;

    /// <summary>Gets the directory holding the local copy.</summary>
    public static string Directory =>
        Path.Combine(MangaBakaPlugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "database");

    /// <summary>
    /// Loads the local copy if one is on disk. Safe to call repeatedly; the work
    /// happens once and later calls just see the loaded snapshot.
    /// </summary>
    /// <returns>The loaded snapshot info, or null when there is nothing to load.</returns>
    public SnapshotInfo? Load()
    {
        if (Volatile.Read(ref _snapshot) is { } loaded)
        {
            return loaded.Info;
        }

        _updateLock.Wait();
        try
        {
            if (Volatile.Read(ref _snapshot) is { } already)
            {
                return already.Info;
            }

            if (_loadAttempted)
            {
                return null;
            }

            _loadAttempted = true;
            try
            {
                var snapshot = Snapshot.Open(Directory);
                if (snapshot is null)
                {
                    return null;
                }

                Volatile.Write(ref _snapshot, snapshot);
                _log.LogInformation(
                    "MangaBaka: local database ready — {Series} series, {Aliases} titles, built {Built:u}",
                    snapshot.Info.SeriesCount,
                    snapshot.Info.AliasCount,
                    snapshot.Info.BuiltUtc);
                return snapshot.Info;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "MangaBaka: local database could not be opened; falling back to the API");
                return null;
            }
        }
        finally
        {
            _updateLock.Release();
        }
    }

    /// <summary>Finds every series indexed under a title.</summary>
    /// <param name="title">Raw title or folder name.</param>
    /// <returns>Candidate series, unscored, possibly empty.</returns>
    public IReadOnlyList<MangaBakaSeries> Lookup(string? title)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var want = MangaBakaText.Normalize(title);
        if (snapshot is null || want.Length == 0)
        {
            return Array.Empty<MangaBakaSeries>();
        }

        try
        {
            return snapshot.Lookup(want);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MangaBaka: local lookup failed for {Title}", title);
            return Array.Empty<MangaBakaSeries>();
        }
    }

    /// <summary>Reads a series by id, following merges.</summary>
    /// <param name="id">MangaBaka series id.</param>
    /// <returns>The series, or null when it is not in the local copy.</returns>
    public MangaBakaSeries? GetById(int id)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null)
        {
            return null;
        }

        try
        {
            for (var hop = 0; hop < 4; hop++)
            {
                var series = snapshot.GetById(id);
                if (series is null || series.IsDeleted)
                {
                    return null;
                }

                if (!series.IsMerged || series.MergedWith == id)
                {
                    return series;
                }

                id = series.MergedWith!.Value;
            }

            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MangaBaka: local lookup failed for id {Id}", id);
            return null;
        }
    }

    /// <summary>
    /// Downloads the published dump and rebuilds the local copy. The new copy is
    /// built beside the old one and only swapped in once it is complete and its
    /// checksum matches, so an interrupted update leaves the previous copy serving.
    /// </summary>
    /// <param name="progress">Progress, 0-100.</param>
    /// <param name="force">Rebuild even when the published dump has not changed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The snapshot now in use.</returns>
    public async Task<SnapshotInfo?> UpdateAsync(IProgress<double>? progress, bool force, CancellationToken ct)
    {
        await _updateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref _snapshot)?.Info ?? ReadSnapshotFile(Directory);
            var expected = await GetPublishedChecksumAsync(ct).ConfigureAwait(false);

            if (!force
                && current is { FormatVersion: FormatVersion }
                && expected is not null
                && string.Equals(current.Sha1, expected, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("MangaBaka: published database is unchanged ({Sha1}); keeping the local copy", expected);
                progress?.Report(100);
                return current;
            }

            var build = Path.Combine(Directory, ".build");
            TryDeleteDirectory(build);
            System.IO.Directory.CreateDirectory(build);

            SnapshotInfo info;
            try
            {
                info = await DownloadAsync(build, expected, current, force, progress, ct).ConfigureAwait(false);
            }
            catch
            {
                TryDeleteDirectory(build);
                throw;
            }

            if (info.SeriesCount == 0)
            {
                TryDeleteDirectory(build);
                _log.LogInformation("MangaBaka: published database is unchanged; keeping the local copy");
                progress?.Report(100);
                return current;
            }

            // Swap. Close the old record file first: unlinking an open file is fine
            // on Linux but not on Windows, and a reader caught mid-request by this
            // degrades to a miss rather than an error — Lookup and GetById catch.
            var previous = Volatile.Read(ref _snapshot);
            Volatile.Write(ref _snapshot, null);
            previous?.Dispose();
            foreach (var name in new[] { RecordsFile, TitleIndexFile, IdIndexFile, SnapshotFile })
            {
                File.Move(Path.Combine(build, name), Path.Combine(Directory, name), overwrite: true);
            }

            TryDeleteDirectory(build);
            Volatile.Write(ref _snapshot, Snapshot.Open(Directory));
            _loadAttempted = true;

            _log.LogInformation(
                "MangaBaka: local database rebuilt — {Series} series, {Aliases} titles, {Size} on disk",
                info.SeriesCount,
                info.AliasCount,
                FormatSize(info.Bytes));
            progress?.Report(100);
            return info;
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private async Task<SnapshotInfo> DownloadAsync(
        string build,
        string? expectedSha1,
        SnapshotInfo? current,
        bool force,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var client = _http.CreateClient(NamedClient.Default);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(DumpUrl));
        request.Headers.TryAddWithoutValidation("User-Agent", MangaBakaClient.UserAgent);
        if (!force && current?.LastModified is { Length: > 0 } since && current.FormatVersion == FormatVersion)
        {
            request.Headers.TryAddWithoutValidation("If-Modified-Since", since);
        }

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new SnapshotInfo { SeriesCount = 0 };
        }

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        var lastModified = response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture);
        _log.LogInformation(
            "MangaBaka: downloading {Url} ({Size})",
            DumpUrl,
            total is null ? "unknown size" : FormatSize(total.Value));

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var counted = new HashingProgressStream(stream, total, progress);
            await using (counted.ConfigureAwait(false))
            {
                var info = await BuildAsync(build, counted, ct).ConfigureAwait(false);

                // The tar reader stops at the end of the entry; drain the rest so the
                // checksum covers the whole archive, trailer and padding included.
                await counted.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);

                var sha1 = counted.HashHex();
                if (expectedSha1 is not null && !string.Equals(sha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"MangaBaka database checksum mismatch: expected {expectedSha1}, got {sha1}");
                }

                info.Sha1 = sha1;
                info.LastModified = lastModified;
                info.Source = DumpUrl;
                info.FormatVersion = FormatVersion;
                info.BuiltUtc = DateTime.UtcNow;

                await using var meta = File.Create(Path.Combine(build, SnapshotFile));
                await JsonSerializer.SerializeAsync(meta, info, JsonOptions, ct).ConfigureAwait(false);
                return info;
            }
        }
    }

    /// <summary>Streams the archive, trims every record, and writes the records and indexes.</summary>
    internal async Task<SnapshotInfo> BuildAsync(string build, Stream archive, CancellationToken ct)
    {
        var titles = new List<TitleEntry>(capacity: 1 << 21);
        var ids = new List<IdEntry>(capacity: 1 << 19);
        var info = new SnapshotInfo();

        var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
        await using (gzip.ConfigureAwait(false))
        {
            var tar = new TarReader(gzip, leaveOpen: true);
            await using (tar.ConfigureAwait(false))
            {
                var entry = await NextJsonLinesEntryAsync(tar, ct).ConfigureAwait(false)
                    ?? throw new InvalidDataException("MangaBaka database archive contained no .jsonl entry");

                var records = File.Create(Path.Combine(build, RecordsFile), 1 << 20);
                await using (records.ConfigureAwait(false))
                {
                    using var reader = new StreamReader(entry.DataStream!, Encoding.UTF8, false, 1 << 20);
                    var buffer = new ArrayBufferWriter<byte>(1 << 16);
                    await using var writer = new Utf8JsonWriter(buffer);
                    var newline = new byte[] { (byte)'\n' };
                    long offset = 0;
                    var skipped = 0;

                    while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (line.Length == 0)
                        {
                            continue;
                        }

                        MangaBakaSeries series;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            series = MangaBakaSeries.Parse(doc.RootElement);
                        }
                        catch (JsonException)
                        {
                            skipped++;
                            continue;
                        }

                        if (series.Id <= 0 || series.IsDeleted)
                        {
                            continue;
                        }

                        series.PrepareForStorage(MaxAltTitles);
                        buffer.Clear();
                        writer.Reset(buffer);
                        series.WriteSlim(writer, MaxStoredTags, MaxDescription);
                        await writer.FlushAsync(ct).ConfigureAwait(false);

                        var length = buffer.WrittenCount;
                        await records.WriteAsync(buffer.WrittenMemory, ct).ConfigureAwait(false);
                        await records.WriteAsync(newline, ct).ConfigureAwait(false);

                        ids.Add(new IdEntry(series.Id, offset, length));
                        foreach (var hash in AliasHashes(series))
                        {
                            titles.Add(new TitleEntry(hash, offset, length));
                        }

                        offset += length + 1;
                        info.SeriesCount++;
                    }

                    info.Bytes = offset;
                    if (skipped > 0)
                    {
                        _log.LogWarning("MangaBaka: skipped {Count} unparseable records in the dump", skipped);
                    }
                }
            }
        }

        info.AliasCount = titles.Count;
        titles.Sort(static (a, b) => a.Hash.CompareTo(b.Hash));
        ids.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        WriteTitleIndex(Path.Combine(build, TitleIndexFile), titles);
        WriteIdIndex(Path.Combine(build, IdIndexFile), ids);
        return info;
    }

    /// <summary>Every distinct normalised title a series can be found by.</summary>
    private static IEnumerable<ulong> AliasHashes(MangaBakaSeries series)
    {
        var seen = new HashSet<ulong>();
        foreach (var title in series.AllTitles)
        {
            var normalized = MangaBakaText.Normalize(title);
            if (normalized.Length == 0)
            {
                continue;
            }

            var hash = MangaBakaText.Hash(normalized);
            if (seen.Add(hash))
            {
                yield return hash;
            }
        }
    }

    private static async Task<TarEntry?> NextJsonLinesEntryAsync(TarReader tar, CancellationToken ct)
    {
        while (await tar.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
        {
            if (entry.DataStream is not null
                && entry.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static void WriteTitleIndex(string path, List<TitleEntry> entries)
    {
        using var file = File.Create(path, 1 << 20);
        using var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);
        writer.Write(TitleIndexMagic);
        writer.Write(FormatVersion);
        writer.Write(entries.Count);
        foreach (var e in entries)
        {
            writer.Write(e.Hash);
            writer.Write(e.Offset);
            writer.Write(e.Length);
        }
    }

    private static void WriteIdIndex(string path, List<IdEntry> entries)
    {
        using var file = File.Create(path, 1 << 20);
        using var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);
        writer.Write(IdIndexMagic);
        writer.Write(FormatVersion);
        writer.Write(entries.Count);
        foreach (var e in entries)
        {
            writer.Write(e.Id);
            writer.Write(e.Offset);
            writer.Write(e.Length);
        }
    }

    private async Task<string?> GetPublishedChecksumAsync(CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient(NamedClient.Default);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ChecksumUrl));
            request.Headers.TryAddWithoutValidation("User-Agent", MangaBakaClient.UserAgent);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            // "<sha1>  series.jsonl.tar.gz"
            var hex = body.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return hex is { Length: 40 } ? hex : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MangaBaka: could not read the published checksum; downloading anyway");
            return null;
        }
    }

    private static SnapshotInfo? ReadSnapshotFile(string directory)
    {
        var path = Path.Combine(directory, SnapshotFile);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SnapshotInfo>(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
            {
                System.IO.Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Formats a byte count for a log line.</summary>
    /// <param name="bytes">Byte count.</param>
    /// <returns>A human-readable size.</returns>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", bytes / (double)(1L << 30)),
        >= 1L << 20 => string.Format(CultureInfo.InvariantCulture, "{0:0} MB", bytes / (double)(1L << 20)),
        _ => string.Format(CultureInfo.InvariantCulture, "{0:0} KB", bytes / 1024d),
    };

    private readonly record struct TitleEntry(ulong Hash, long Offset, int Length);

    private readonly record struct IdEntry(int Id, long Offset, int Length);

    /// <summary>
    /// One immutable, loaded copy: the index arrays in memory and an open handle on
    /// the record file. Updating publishes a new instance rather than mutating this
    /// one, so a refresh already reading records is never pulled out from under.
    /// </summary>
    private sealed class Snapshot : IDisposable
    {
        private readonly SafeFileHandle _records;
        private readonly ulong[] _titleHashes;
        private readonly long[] _titleOffsets;
        private readonly int[] _titleLengths;
        private readonly int[] _ids;
        private readonly long[] _idOffsets;
        private readonly int[] _idLengths;

        private Snapshot(
            SnapshotInfo info,
            SafeFileHandle records,
            ulong[] titleHashes,
            long[] titleOffsets,
            int[] titleLengths,
            int[] ids,
            long[] idOffsets,
            int[] idLengths)
        {
            Info = info;
            _records = records;
            _titleHashes = titleHashes;
            _titleOffsets = titleOffsets;
            _titleLengths = titleLengths;
            _ids = ids;
            _idOffsets = idOffsets;
            _idLengths = idLengths;
        }

        public SnapshotInfo Info { get; }

        public void Dispose() => _records.Dispose();

        public static Snapshot? Open(string directory)
        {
            var info = ReadSnapshotFile(directory);
            if (info is null || info.FormatVersion != FormatVersion)
            {
                return null;
            }

            var recordsPath = Path.Combine(directory, RecordsFile);
            var titlePath = Path.Combine(directory, TitleIndexFile);
            var idPath = Path.Combine(directory, IdIndexFile);
            if (!File.Exists(recordsPath) || !File.Exists(titlePath) || !File.Exists(idPath))
            {
                return null;
            }

            var (titleHashes, titleOffsets, titleLengths) = ReadTitleIndex(titlePath);
            var (ids, idOffsets, idLengths) = ReadIdIndex(idPath);
            var handle = File.OpenHandle(recordsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new Snapshot(info, handle, titleHashes, titleOffsets, titleLengths, ids, idOffsets, idLengths);
        }

        public IReadOnlyList<MangaBakaSeries> Lookup(string normalizedTitle)
        {
            var hash = MangaBakaText.Hash(normalizedTitle);
            var i = Array.BinarySearch(_titleHashes, hash);
            if (i < 0)
            {
                return Array.Empty<MangaBakaSeries>();
            }

            while (i > 0 && _titleHashes[i - 1] == hash)
            {
                i--;
            }

            var results = new List<MangaBakaSeries>();
            var seen = new HashSet<int>();
            for (; i < _titleHashes.Length && _titleHashes[i] == hash; i++)
            {
                var series = Read(_titleOffsets[i], _titleLengths[i]);

                // A 64-bit hash collision is vanishingly unlikely but costs nothing
                // to rule out, and the check also catches a stale index.
                if (series is null || series.IsDeleted || !seen.Add(series.Id))
                {
                    continue;
                }

                if (series.AllTitles.Any(t =>
                        string.Equals(MangaBakaText.Normalize(t), normalizedTitle, StringComparison.Ordinal)))
                {
                    results.Add(series);
                }
            }

            return results;
        }

        public MangaBakaSeries? GetById(int id)
        {
            var i = Array.BinarySearch(_ids, id);
            return i < 0 ? null : Read(_idOffsets[i], _idLengths[i]);
        }

        private static (ulong[] Hashes, long[] Offsets, int[] Lengths) ReadTitleIndex(string path)
        {
            using var file = File.OpenRead(path);
            using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadInt32() != TitleIndexMagic || reader.ReadInt32() != FormatVersion)
            {
                throw new InvalidDataException("MangaBaka title index has an unexpected header");
            }

            var count = reader.ReadInt32();
            var hashes = new ulong[count];
            var offsets = new long[count];
            var lengths = new int[count];
            for (var i = 0; i < count; i++)
            {
                hashes[i] = reader.ReadUInt64();
                offsets[i] = reader.ReadInt64();
                lengths[i] = reader.ReadInt32();
            }

            return (hashes, offsets, lengths);
        }

        private static (int[] Ids, long[] Offsets, int[] Lengths) ReadIdIndex(string path)
        {
            using var file = File.OpenRead(path);
            using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadInt32() != IdIndexMagic || reader.ReadInt32() != FormatVersion)
            {
                throw new InvalidDataException("MangaBaka id index has an unexpected header");
            }

            var count = reader.ReadInt32();
            var ids = new int[count];
            var offsets = new long[count];
            var lengths = new int[count];
            for (var i = 0; i < count; i++)
            {
                ids[i] = reader.ReadInt32();
                offsets[i] = reader.ReadInt64();
                lengths[i] = reader.ReadInt32();
            }

            return (ids, offsets, lengths);
        }

        private MangaBakaSeries? Read(long offset, int length)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                var span = buffer.AsSpan(0, length);
                var read = 0;
                while (read < length)
                {
                    var n = RandomAccess.Read(_records, span[read..], offset + read);
                    if (n == 0)
                    {
                        return null;
                    }

                    read += n;
                }

                using var doc = JsonDocument.Parse(buffer.AsMemory(0, length));
                return MangaBakaSeries.Parse(doc.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Wraps the download so the bytes are hashed and counted exactly once as they
    /// are pulled through gzip and tar, rather than buffering 540 MB to hash it.
    /// </summary>
    private sealed class HashingProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly long? _total;
        private readonly IProgress<double>? _progress;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        private long _read;
        private int _lastReported = -1;

        public HashingProgressStream(Stream inner, long? total, IProgress<double>? progress)
        {
            _inner = inner;
            _total = total > 0 ? total : null;
            _progress = progress;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public string HashHex() => Convert.ToHexString(_hash.GetCurrentHash()).ToLowerInvariant();

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = _inner.Read(buffer);
            Account(buffer[..n]);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            Account(buffer.Span[..n]);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Account(ReadOnlySpan<byte> chunk)
        {
            if (chunk.Length == 0)
            {
                return;
            }

            _hash.AppendData(chunk);
            _read += chunk.Length;
            if (_progress is null || _total is null)
            {
                return;
            }

            // Reserve the last few percent for sorting and writing the indexes.
            var percent = (int)(_read * 95 / _total.Value);
            if (percent != _lastReported)
            {
                _lastReported = percent;
                _progress.Report(percent);
            }
        }
    }
}
