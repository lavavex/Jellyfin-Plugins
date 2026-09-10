# Jellyfin Plugins

Metadata providers for manga and light novel libraries.

| Plugin | What it does |
|---|---|
| **Suwayomi Metadata** | Fills series-folder metadata and cover art from a [Suwayomi](https://github.com/Suwayomi/Suwayomi-Server) server. Best when Suwayomi is what downloaded the library — matching is exact, by folder path. |
| **MangaBaka** | Fills series-folder metadata and cover art from [MangaBaka](https://mangabaka.org). Works for any manga or light novel library; matches by title. |

Both target **Jellyfin 12.0.0** (.NET 10).

## Installing

Dashboard → Plugins → Repositories → **+**, and add this manifest URL:

```
https://github.com/lavavex/Jellyfin-Plugins/releases/latest/download/manifest.json
```

That URL always resolves to the newest release, so Jellyfin picks up new
versions without you changing anything.

The plugins then appear under Catalog. Restart Jellyfin after installing.

## Development

Source of truth is a private Gitea instance, push-mirrored to GitHub. Release
zips and `manifest.json` are served from the GitHub mirror so the repository
is usable publicly.

## Why these exist

Jellyfin models a book/manga **series folder** as a plain `Folder`, and no
built-in provider populates it — chapters get metadata from the `ComicInfo.xml`
inside each CBZ, but the series itself stays blank. Both plugins bind
`ICustomMetadataProvider<Folder>` to fill that gap.

Note that such a provider is **not** user-selectable in a library's metadata
downloader list; it runs automatically during a refresh. If a series shows no
metadata, run *Refresh metadata → Replace all metadata* on it.

## Configuration

**Suwayomi Metadata** — set the server URL. Matching uses the folder path, so
the library must be the one Suwayomi downloads into.

**MangaBaka** — set the series type (`novel` or `manga`) to suit the library.
Most series exist as *both* a novel and a manga adaptation, so leaving this on
"Any" is the most common cause of wrong matches. `MinMatchScore` (default 80)
controls how close a title must be before a result is accepted.

## Releasing

Tag and push; GitHub Actions does the rest.

```bash
git tag v1.0.1 && git push origin v1.0.1
```

The workflow builds both plugins, packages them, regenerates `manifest.json`
with checksums, verifies each checksum against the built zip, and publishes
everything as release assets.

Build output is deliberately **not** committed. Gitea is the source of truth and
push-mirrors into GitHub, so anything a workflow committed to a tracked branch
would be overwritten by the next sync — release assets live outside that.

To build locally without releasing:

```bash
./build.sh [base-url]
```

`base-url` is where `releases/` will be served from; Jellyfin downloads from
`sourceUrl`, so it must be reachable *by the server*.

The build references Jellyfin 12.0.0 assemblies committed in `lib/` rather than
NuGet packages, which pins it to that exact server version. After a Jellyfin
upgrade, re-copy the assemblies from `/usr/lib/jellyfin/bin/` and rebuild.
