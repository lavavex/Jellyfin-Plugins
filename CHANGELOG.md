# Changelog

Both plugins are versioned together and share a release tag. The version's first
component tracks the Jellyfin major they target.

The same notes are published in `manifest.json`, which carries every release —
Jellyfin's plugin page lets you install an older version from it.

## 12.0.0.2 — 2026-09-11

### MangaBaka and Suwayomi Metadata

**Fixed: neither plugin appeared under Image Fetchers (Books).** Jellyfin
probes image providers with a dummy `Book` that has no parent library.
`Supports()` was requiring a Books library, so both providers declined the
dummy and never showed up. The library-type check now happens when fetching
images, not when listing fetchers.

## 12.0.0.1 — 2026-09-11

### MangaBaka

**Local database.** MangaBaka publishes a nightly snapshot of its whole
catalogue. A new scheduled task, **Download MangaBaka database**, fetches it and
keeps a trimmed local copy, after which matching a library costs no network
requests at all — which is what makes updating everything at once practical.
A second task, **Refresh MangaBaka metadata**, re-runs every book library in one
go. The download is about 540 MB and roughly 525 MB is kept under the plugin's
data folder, covering 560,000 series indexed under 1.9 million titles. It
repeats weekly, verifies the published SHA-1, and builds the new copy beside the
old one so an interrupted download leaves the previous copy serving.

**Fixed: series were titled in Chinese or Japanese.** Titles were read from the
API's `titles[]` array, preferring the entry flagged `is_primary` — but that flag
is per *language*, and a series typically has eight or more entries carrying it.
The last one won, which was reliably a CJK title, so *Frieren: Beyond Journey's
End* came through as 葬送的芙莉蓮. Titles are now chosen by language, and which
kind you get — English, romanized or native — is a setting.

**Fixed: the plugin searched MangaBaka for folders in every library.** Series,
Season and BoxSet all derive from `Folder`, and so does every folder in a photo
library. Only the concrete type was checked, so a photo library refresh issued a
MangaBaka search per folder and the image provider could hang a manga cover on a
holiday album. Both providers now check the library's content type, with a
setting to turn that off for a book library filed under another type.

**Fixed: about 180 tags were written per series**, spoilers included. Tags are
now capped — 8 by default, configurable — ordered general to specific, and
anything MangaBaka flags as a spoiler is dropped.

Also in this release:

- Titles in non-Latin scripts are matched and indexed. Normalisation reduced
  titles to `[a-z0-9]`, which collapses a Japanese, Korean or Cyrillic title to
  an empty string, making it silently unmatchable.
- API requests are throttled to two at a time, each bounded by its own 30-second
  timeout, and lookups are memoised for 30 minutes — the metadata, folder and
  image providers used to resolve the same series separately.
- The series type is applied consistently: it was a hard filter on the search API
  and no filter at all elsewhere.
- Items link back to their MangaBaka page.
- A JSON `null` in an integer field no longer throws while parsing.
- Merged series are followed by id in the local copy as well as through the API.
- Settings page gains the title, tag-limit, match-source and library-restriction
  options, and no longer writes `NaN` when a number field is cleared. Where
  lookups go is a single three-way choice — local database then API, database
  only, or API only — so there is no way to select neither.

### Suwayomi Metadata

- The metadata, folder and image providers now share one client, so a library
  refresh makes a single GraphQL query to Suwayomi instead of three — each
  provider previously built its own client with its own ten-minute cache.
- New setting limits the plugin to libraries whose content type is Books, so it
  no longer claims plain folders in photo or mixed libraries.

### Repository

- `manifest.json` now carries every published release rather than only the
  newest, so an older version stays installable. The history lives in
  `versions.json`; the release workflow verifies every checksum in the manifest
  against what is actually downloadable.
- `build.sh` defaults to the `releases/latest/download` base, which resolves
  correctly, instead of a raw-git path that never held the zips.

## 12.0.0.0 — 2026-09-11

First release for Jellyfin 12.

### MangaBaka

- Listed under Books and selectable as a book metadata provider.
- Settings page no longer overlays other dashboard pages.
- Reads titles, publication dates and tags per the MangaBaka spec.
- Optional beta (v2) API.

### Suwayomi Metadata

- Listed under Books and selectable as a book metadata provider.
- Settings page no longer overlays other dashboard pages.
- Server URL starts blank, and the providers stay idle until it is set.
