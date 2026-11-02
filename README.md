# strminfo

An Emby server plugin that reads **media info only** (resolution, video codec, audio tracks and languages, subtitles, runtime, bitrate) for `.strm` items, without refreshing their metadata.

Emby normally fills in media info for a `.strm` item the first time it's played, or through a full item refresh, which also re-runs every metadata provider (TMDB, TheTVDB, Fanart.tv…) for the item. strminfo runs Emby's own probe directly and saves only the result, so:

- no metadata downloads, image fetches or `.nfo` writes,
- one stream opened per item, for a few seconds,
- a probe that hangs is stopped after a timeout and its connection closed, so it can't tie up one of your provider's connections.

## Features

- **Library scope:** pick which Emby libraries are probed, and count how many items in each still need media info.
- **Item types:** movies, TV episodes and other videos, each on or off. By default only `.strm` items are probed; turn that off to also cover regular files that are missing media info.
- **Pace:** probes at the same time (1 by default), timeout, a pause after each probe, and the user agent sent to the provider.
- **Limits:** stop a run after a number of items or minutes (a nightly window carries on where it stopped next time), stop when too many probes fail in a row (provider down or at its connection limit), and wait while an Emby library scan is running.
- **Failed items** are listed with the reason and skipped until a retry period has passed (7 days by default). Clear the list to try them all again.
- **New items:** optionally probe items soon after a scan adds them.
- **Live page** in the dashboard: progress, time left, what's being probed right now, recent results, failures and a log.
- **Scheduled task** "Extract media info (strminfo)". It has no schedule by default; add a trigger under Scheduled Tasks, or use **Run now** on the page.
- Items that already have media info are always skipped, newest items first by default.
- Stream URLs are never shown in full on the page or in the log (only the scheme and host), since they usually contain the provider login.

## Install

1. Download `StrmInfo.Emby.dll` from the [Releases](../../releases) page, or build it (below).
2. Copy it into Emby's `plugins` folder (`/config/plugins` in Docker) and restart Emby.
3. Open **Dashboard → strminfo**.
4. **Libraries** tab: tick the libraries to probe, Save, and optionally select **Count items**.
5. **Settings** tab: set **Probes at the same time** within your provider's connection limit, leaving room for watching. 1 is safest.
6. **Status** tab: **Run now**, or add a schedule to the task in Scheduled Tasks.

If you use another media-info plugin, turn its task off, so the two don't probe the same items.

Tested on Emby 4.11.

## What it saves

For each probed item: its media streams (video, audio and subtitle tracks with codecs, languages and channels), chapters (optional), runtime, total bitrate, and the video's width and height. Nothing else about the item is changed.

## Build

The plugin targets `netstandard2.1` and needs no NuGet packages. It's compiled against the `MediaBrowser.*` assemblies from an Emby server.

1. Install the .NET SDK (6 or newer).
2. Copy `MediaBrowser.Common.dll`, `MediaBrowser.Controller.dll` and `MediaBrowser.Model.dll` from an Emby server's `system` folder into `emby-sdk/`.
3. `dotnet build -c Release -o out` (or pass `-p:EmbySdkDir=/path/to/dlls`).

Raising `<Version>` in `StrmInfo.Emby.csproj` and pushing to `main` makes the GitHub workflow build the DLL against the Emby release named in `.github/workflows/release.yml` and publish a release.

## Files

The plugin keeps two small files in Emby's data folder, under `data/strminfo/`: `failures.json` (failed items and why) and `last.json` (the last run's summary).
