using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Suwayomi;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the base URL of the Suwayomi server, scheme included.
    /// Empty until configured, and while it is empty the providers stay idle.
    /// </summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the synopsis should have the
    /// scraped "Rating: x - Bookmarks: y" stats line stripped out.
    /// The rating itself is lifted onto CommunityRating.
    /// </summary>
    public bool StripStatsLine { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether folder covers are served from Suwayomi.
    /// </summary>
    public bool ProvideImages { get; set; } = true;
}

/// <summary>
/// Pulls manga metadata for series folders straight from Suwayomi.
///
/// Jellyfin already reads per-chapter metadata out of the ComicInfo.xml that
/// Suwayomi embeds in each CBZ, but nothing populates the *series folder*.
/// This plugin fills that gap with <c>IRemoteMetadataProvider&lt;Book, BookInfo&gt;</c>
/// (same as Google Books / Comic Vine) plus a folder provider for the series directory.
/// </summary>
public class SuwayomiPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SuwayomiPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public SuwayomiPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the singleton instance so providers can read configuration.
    /// </summary>
    public static SuwayomiPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Suwayomi Metadata";

    /// <inheritdoc />
    public override Guid Id => new("6f1c9d24-3b7a-4f18-9d55-2e7a1c4b8e90");

    /// <inheritdoc />
    public override string Description =>
        "Fills in series-folder metadata (summary, genres, rating, author, artist) and cover art from a Suwayomi server.";

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
