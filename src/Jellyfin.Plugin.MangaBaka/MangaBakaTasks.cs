using System.Globalization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MangaBaka;

/// <summary>
/// Downloads MangaBaka's published database dump and rebuilds the local copy.
///
/// This is what makes a whole-library update practical: with the dump on disk,
/// matching every series costs no network requests at all.
/// </summary>
public sealed class MangaBakaDatabaseTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly MangaBakaDatabase _database;
    private readonly MangaBakaResolver _resolver;
    private readonly ILogger<MangaBakaDatabaseTask> _log;

    /// <summary>Initializes a new instance of the <see cref="MangaBakaDatabaseTask"/> class.</summary>
    /// <param name="database">Local database.</param>
    /// <param name="resolver">Series resolver, whose memo is dropped after an update.</param>
    /// <param name="log">Logger.</param>
    public MangaBakaDatabaseTask(
        MangaBakaDatabase database,
        MangaBakaResolver resolver,
        ILogger<MangaBakaDatabaseTask> log)
    {
        _database = database;
        _resolver = resolver;
        _log = log;
    }

    /// <inheritdoc />
    public string Name => "Download MangaBaka database";

    /// <inheritdoc />
    public string Key => "MangaBakaDatabaseDownload";

    /// <inheritdoc />
    public string Description =>
        "Downloads MangaBaka's nightly database snapshot and rebuilds the local copy, so library "
        + "refreshes match series offline instead of one search request at a time.";

    /// <inheritdoc />
    public string Category => "MangaBaka";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        // The dump is rebuilt nightly, but it is a 540 MB download and the catalogue
        // moves slowly; weekly keeps a local copy current without being a nuisance.
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = System.DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
        },
    };

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var info = await _database.UpdateAsync(progress, force: false, cancellationToken).ConfigureAwait(false);
        _resolver.ClearCache();
        if (info is not null)
        {
            _log.LogInformation(
                "MangaBaka: database ready — {Series} series indexed under {Aliases} titles",
                info.SeriesCount,
                info.AliasCount);
        }
    }
}

/// <summary>
/// Re-runs metadata for every book and series folder in the book libraries.
///
/// Jellyfin's own refresh does the same thing per library; this exists so a mass
/// update is one click after the database has been downloaded or the settings
/// (title style, tag limit, series type) have changed.
/// </summary>
public sealed class MangaBakaRefreshTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILibraryManager _library;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly MangaBakaResolver _resolver;
    private readonly ILogger<MangaBakaRefreshTask> _log;

    /// <summary>Initializes a new instance of the <see cref="MangaBakaRefreshTask"/> class.</summary>
    /// <param name="library">Library manager.</param>
    /// <param name="providers">Provider manager.</param>
    /// <param name="fileSystem">File system.</param>
    /// <param name="resolver">Series resolver.</param>
    /// <param name="log">Logger.</param>
    public MangaBakaRefreshTask(
        ILibraryManager library,
        IProviderManager providers,
        IFileSystem fileSystem,
        MangaBakaResolver resolver,
        ILogger<MangaBakaRefreshTask> log)
    {
        _library = library;
        _providers = providers;
        _fileSystem = fileSystem;
        _resolver = resolver;
        _log = log;
    }

    /// <inheritdoc />
    public string Name => "Refresh MangaBaka metadata";

    /// <inheritdoc />
    public string Key => "MangaBakaRefreshAll";

    /// <inheritdoc />
    public string Description =>
        "Queues a metadata refresh for every book and series folder in the book libraries. "
        + "Run it after downloading the database or changing the plugin settings.";

    /// <inheritdoc />
    public string Category => "MangaBaka";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _resolver.ClearCache();

        var items = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Book, BaseItemKind.Folder },
            Recursive = true,
        });

        var cfg = MangaBakaPlugin.Instance?.Configuration ?? new PluginConfiguration();
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = cfg.ProvideImages ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.None,
            ReplaceAllMetadata = cfg.ReplaceOnBulkRefresh,
        };

        var queued = 0;
        var total = items.Count;
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_resolver.AppliesTo(items[i]))
            {
                _providers.QueueRefresh(items[i].Id, options, RefreshPriority.Low);
                queued++;
            }

            progress.Report(total == 0 ? 100 : (i + 1) * 100d / total);
        }

        _log.LogInformation(
            "MangaBaka: queued {Queued} of {Total} items for refresh (local database {State})",
            queued,
            total,
            _resolver.Database.IsReady
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "ready, {0} series",
                    _resolver.Database.Info?.SeriesCount ?? 0)
                : "not downloaded");

        progress.Report(100);
        return Task.CompletedTask;
    }
}
