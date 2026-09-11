# Jellyfin Plugins

Metadata providers for manga and light novel libraries.

| Plugin | What it does |
|---|---|
| **Suwayomi Metadata** | Fills series-folder metadata and cover art from a [Suwayomi](https://github.com/Suwayomi/Suwayomi-Server) server. Best when Suwayomi is what downloaded the library — matching is exact, by folder path. |
| **MangaBaka** | Fills series-folder metadata and cover art from [MangaBaka](https://mangabaka.org). Works for any manga or light novel library; matches by title, against a local copy of MangaBaka's database or its search API. |

Both target **Jellyfin 12**. Plugin versions start at **12.0.0.0** to match that
major, and live under the **Books** catalogue category. See
[CHANGELOG.md](CHANGELOG.md) for the release history.

## Installing

Dashboard → Plugins → Repositories → **+**, and add this manifest URL:

```
https://raw.githubusercontent.com/lavavex/Jellyfin-Plugins/main/manifest.json
```

That is a file on `main`, so a new release is visible as soon as the mirror
picks up the commit (GitHub's CDN caches it for about five minutes). Do not
use the GitHub `/releases/latest/download/` URL: those assets are served as
`application/octet-stream` after a redirect, and Jellyfin's catalogue will
not parse them — you will only ever see the version already installed. The
manifest carries every published version, so an older one can still be
installed from the plugin's page.

The plugins then appear under Catalog → Books. Restart Jellyfin after installing.

Enable them per library: **Dashboard → Libraries → (book library) → Metadata
downloaders**. They show as **Suwayomi** and **MangaBaka** next to Google Books
and Comic Vine.

## Why these exist

Jellyfin models a book/manga **series folder** as a plain `Folder`, and no
built-in provider populates it — chapters get metadata from the `ComicInfo.xml`
inside each CBZ, but the series itself stays blank. Both plugins bind
`IRemoteMetadataProvider<Book, BookInfo>` so they appear in a book library's
metadata-downloader list (the same interface Google Books and Comic Vine use),
and still fill the series folder during a refresh.

If a series shows no metadata, run *Refresh metadata → Replace all metadata*
on it, and confirm the provider is enabled on that library.

## MangaBaka: the local database

MangaBaka publishes a [nightly snapshot](https://mangabaka.org/data/database) of
its whole catalogue. Matching a library through the search API is one HTTP
request per series — slow, rate-limited, and impolite at scale. With a local
copy there are none at all, which is what makes updating a whole library in one
pass practical.

Two scheduled tasks appear under **Dashboard → Scheduled Tasks**:

| Task | What it does |
|---|---|
| **Download MangaBaka database** | Fetches the snapshot and rebuilds the local copy. Runs weekly by default; run it once by hand to get started. |
| **Refresh MangaBaka metadata** | Queues a metadata refresh for every book and series folder in the book libraries. Run it after a download, or after changing settings. |

What it costs: about **540 MB** downloaded and roughly **525 MB** kept under the
plugin's data folder, covering some **560,000 series** indexed under **1.9
million titles**, with around 50 MB of index held in memory while the server
runs. The published 4 GB of JSONL is streamed and trimmed on the way in — only
the fields the plugin writes into Jellyfin are kept.

It is safe to interrupt. The snapshot's published SHA-1 is verified as it
streams, the new copy is built alongside the old one, and the swap only happens
once it is complete — a failed or cancelled download leaves the previous copy
serving. Delete the `database` folder under the plugin's data directory to
reclaim the space; the plugin falls back to the search API.

## Configuration

**Suwayomi Metadata** — set the server URL. Matching uses the folder path, so
the library must be the one Suwayomi downloads into. Leave the URL blank to
disable.

**MangaBaka** — set the series type (`novel` or `manga`) to suit the library.
Most series exist as *both* a novel and a manga adaptation, so leaving this on
"Any" is the most common cause of wrong matches. Other settings worth knowing:

- **Title to write** — English, romanized, or native script.
- **Minimum match score** (default 80) — how close a title must be to be accepted.
- **Maximum tags** (default 8) — MangaBaka carries around 180 tags per series.
  The most general are written first and spoiler tags never are.
- **Match against** — where lookups go:

  | Choice | Behaviour |
  |---|---|
  | Local database, then the search API | *(default)* the database answers exact title matches, the API covers the rest |
  | Local database only | no outbound requests while matching; matches nothing until the database is downloaded |
  | Search API only | as if the database were never downloaded |

- **Only touch libraries whose content type is Books** — leave on unless your
  book library is filed under another type. Off, the plugin will try to fill any
  plain folder on the server, photo libraries included.

Both plugins only act on books and on plain series folders — never on a TV
series, season or collection, which are also `Folder`s underneath.

## Development

Source of truth is a private Gitea instance, push-mirrored to GitHub. Release
zips are GitHub Release assets; `manifest.json` is committed on `main` so
Jellyfin can fetch it from `raw.githubusercontent.com`.

The build references Jellyfin 12.0.0 assemblies committed in `lib/` rather than
NuGet packages, which pins it to that exact server version. After a Jellyfin
upgrade, re-copy the assemblies from `/usr/lib/jellyfin/bin/` and rebuild. Note
that those are linux-x64 images: they compile as references anywhere, but only
load inside the server.

To build locally without releasing:

```bash
./build.sh [base-url]
```

`base-url` is where `releases/` will be served from; Jellyfin downloads the
zip from `sourceUrl`, so it must be reachable *by the server*. CI passes the
tag's release-asset URL. A local run defaults to the "latest release" alias,
which is fine for checking the zip layout, not for the committed catalogue.

## Releasing

1. Bump `AssemblyVersion`/`FileVersion` in both `.csproj` files and the version
   in `build.sh`, and add the release notes to `CHANGELOG.md` and to
   `changelog_for()` in `build.sh`.
2. Tag and push:

   ```bash
   git tag v12.0.0.1 && git push origin v12.0.0.1
   ```

3. GitHub Actions builds both plugins, packages them, regenerates
   `manifest.json`, verifies every checksum in it against what is actually
   downloadable, and publishes the zips (and a copy of the manifest) as
   release assets.
4. The final workflow step prints a JSON entry per plugin. Paste each into
   `versions.json`, copy the published `manifest.json` into the repo, and
   commit both on Gitea. That is what the catalogue URL reads, and what keeps
   this version offered by the *next* release — the workflow cannot commit it
   itself, because the Gitea mirror would overwrite anything it pushed.

Zips are deliberately **not** committed. Gitea is the source of truth and
push-mirrors into GitHub, so anything a workflow committed to a tracked branch
would be overwritten by the next sync — release assets live outside that.

## Attribution

MangaBaka data is licensed [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/)
and aggregates AniList, Kitsu, MangaUpdates, MyAnimeList and Anime-Planet, each
under its own terms.
