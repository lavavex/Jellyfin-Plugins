using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MangaBaka;

/// <summary>Plugin configuration.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the MangaBaka API channel: "v1" (stable) or "v2" (beta).
    /// Search and get-by-id exist on both; v2 is still marked beta in the spec.
    /// Only used when the local database is off or has no match.
    /// </summary>
    public string ApiVersion { get; set; } = "v1";

    /// <summary>
    /// Gets or sets which MangaBaka series type to match against:
    /// "novel" for a light novel library, "manga" for manga, or empty for either.
    /// Matching the wrong type is the most common cause of bad results, because
    /// most popular series exist as both a novel and a manga adaptation.
    /// </summary>
    public string SeriesType { get; set; } = "novel";

    /// <summary>
    /// Gets or sets which of a series' titles to write: "english", "romanized" or
    /// "native". Each falls back to the others when MangaBaka has no such title.
    /// </summary>
    public string TitleStyle { get; set; } = TitleStyles.English;

    /// <summary>
    /// Gets or sets the minimum title-similarity score (0-100) required before a
    /// search result is accepted. MangaBaka's search is recall-oriented and will
    /// happily return loosely related series, so a threshold matters.
    /// </summary>
    public int MinMatchScore { get; set; } = 80;

    /// <summary>
    /// Gets or sets how many MangaBaka tags to write, general ones first.
    /// A series carries around 180, which is unusable as a library facet; the
    /// publication status is always written and does not count toward this.
    /// Zero writes no tags at all.
    /// </summary>
    public int MaxTags { get; set; } = 8;

    /// <summary>Gets or sets a value indicating whether cover art is served from MangaBaka.</summary>
    public bool ProvideImages { get; set; } = true;

    /// <summary>
    /// Gets or sets where series are looked up: one of the <see cref="MatchSources"/>
    /// values. Anything unrecognised behaves as the default, so a hand-edited config
    /// cannot leave the plugin with nowhere to resolve from.
    /// </summary>
    public string MatchSource { get; set; } = MatchSources.DatabaseThenApi;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin only touches libraries
    /// whose content type is Books. Turning this off lets it fill plain folders in
    /// any library, which is rarely what you want: a photo library is folders too.
    /// </summary>
    public bool RestrictToBookLibraries { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the "Refresh MangaBaka metadata"
    /// task overwrites metadata that is already present, rather than only filling
    /// gaps. Off by default because it also discards hand-made corrections.
    /// </summary>
    public bool ReplaceOnBulkRefresh { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether series flagged by MangaBaka as
    /// erotica/pornographic are skipped rather than written.
    /// </summary>
    public bool SkipExplicit { get; set; }
}

/// <summary>
/// Metadata for manga and light novel series folders, sourced from MangaBaka.
///
/// Jellyfin models a book series folder as a plain <c>Folder</c>, and no built-in
/// provider populates it — so this binds <c>IRemoteMetadataProvider&lt;Book, BookInfo&gt;</c>
/// (same as Google Books / Comic Vine) plus a folder provider for the series directory.
///
/// Data is from MangaBaka (https://mangabaka.org), CC BY-NC-SA 4.0, which in turn
/// aggregates AniList, Kitsu, MangaUpdates, MyAnimeList and Anime-Planet.
/// </summary>
public class MangaBakaPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="MangaBakaPlugin"/> class.</summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public MangaBakaPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the singleton instance so providers can read configuration.</summary>
    public static MangaBakaPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "MangaBaka";

    /// <inheritdoc />
    public override Guid Id => new("8c3e5a17-42b9-4d6e-b1f0-9a7c5d2e4b83");

    /// <inheritdoc />
    public override string Description =>
        "Series metadata and cover art for manga and light novel libraries, from MangaBaka.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace),
        };
    }
}

/// <summary>
/// Registers the shared services. The local database holds an open file handle and
/// a few tens of megabytes of index, and the resolver memoises lookups across a
/// refresh — both want to be one instance for the whole server, not one per
/// provider, which is what constructing them inline used to give.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MangaBakaDatabase>();
        serviceCollection.AddSingleton<MangaBakaResolver>();
    }
}
