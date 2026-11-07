# FluentMediaGallery — HANDOFF

**Last handoff update:** 2026-11-07. Read this file and AGENTS.md at the start of EVERY chat, and update both when a decision changes. This is a living status/history file. Do not claim unrun tests passed.

## Repository and working environment

- Public GitHub: https://github.com/EudoksjuszSzalony/FluentMediaGallery
- Direct commits on **develop**; **main** only after verified stable promotion. No routine feature branches.
- Main working/testing machine is the user's **Dominator Windows PC**. Local clone: %USERPROFILE%\Documents\FluentMediaGallery
- Git for Windows 2.56.0.windows.4 installed, user PATH configured, .NET 10 SDK installed. Remote Desktop Commander/RCU-OPSL is AUXILIARY VM and is NOT proof of main-PC functionality.
- No push/pull-request GitHub Actions. Workflow was removed to save minutes for later **releases/assets**.
- Give PowerShell as one executable "& { ... }" block, label **USER** or **ADMINISTRATOR** (default USER), use ErrorActionPreference Stop, check native exit codes, and stop on error.
- Keep workspace clean: cleanup after confirmed success when output becomes unnecessary. Preview-first; never delete original media, SQLite databases, backups, source, .git, installer assets, or user settings. scripts/Cleanup-DevelopmentArtifacts.ps1 is an opt-in helper for build outputs only.

## Product and constraints

Free public Windows 11 Fluent Photos-like gallery, with native WinUI 3 UI, photo grid / viewer / bottom filmstrip, visible heart for favorites on photos AND videos, playable video incl. seeking, audio/subtitle tracks and fullscreen.
- Dataset baseline: **158,267 photos + 43,438 videos = 201,705 media**. Target 1M files without loading them all into RAM.
- Multiple folders chosen explicitly by the user: local, removable, UNC/SMB/NAS. User has private NAS photos; do NOT automatically import other network shares or Photos libraries.
- Persistent SQLite catalogue and source states; thumbnail disk cache and paged views; fast restarts without rescanning all original media.
- Detect live changes with watchers, local USN where supported, and reconcile SMB after app was off. The watcher cannot guarantee a log of NAS changes while app is down.
- Preserve records/favorites when NAS offline or scan incomplete; never treat a transient permission error as deletion.
- Planned future WinUI 3 + .NET 10, LibVLCSharp.WinUI (not yet implemented), installer and release assets.

## Favorites decisions and current code

- Windows System.Rating: 4 stars (63–87) **OR 5 stars (88–99) = favorite**. 1 star (1–12) = removed/nonfavorite; 2/3 not favorite.
- On favoriting: write 75 (4 stars), preserve existing 99 (5 stars). On removing: write 1.
- In Core, FavoritesService saves override in SQLite first; ShellRatingWriter attempts property SetValue/Commit and verifies by re-reading. Failing read/write leaves pending sync state and user-facing error; critical for videos whose property handler does not support rating writes.
- Rating import must be read-only during indexing. Test physical writes on **copies** of JPG and video files before production library. No bulk silent write.
- Backups/export/import and proper retries/backoff for unsupported files are future tasks.

## Currently implemented on develop

- FluentMediaGallery.Core: LibraryDatabase SQLite WAL and migrations, DirectoryIndexer streaming scan/offline safety, MediaModels, ShellRatingReader/Writer, FavoritesService, RatingImportService.
- Windows-only Core.Smoke tests cover initial scan, persisted DB and favorite overrides, 4/5-star policies, pending file writes, missing files and offline source handling.
- A native, **unpackaged WinUI 3 App source project is now committed**: `src/FluentMediaGallery.App`, using Windows App SDK **2.6.5** and .NET 10. It provides source registration, explicit folder scans with cancel, paged SQLite browsing (100/page), source filtering, filename search, favorites filter, read-only classification importing, file-details panel, and explicit heart toggle with a metadata-writing confirmation. **Dominator GUI BUILD PASS at commit 7832494 (2026-11-07)**. EXE launched and exited with code 0, but user has not explicitly confirmed whether the window was displayed. Newer thumbnail UI is committed but not yet built. Thumbnail grid present in source, not yet verified visually. Full image viewer and video playback are not implemented.
- On Dominator, user confirmed at commit **da4906ff5ee1a05e85205e905855485b903c70af**: Git pull/restore PASS, NuGet vulnerability audit PASS (zero vulnerable dependencies), Core build PASS without warning, **ALL CORE SMOKE TESTS PASSED** (2026-11-07). No actual in-file metadata write was tested.
- SQLite dependency updated to **Microsoft.Data.Sqlite 10.0.14**, resolving SQLitePCLRaw to **2.1.13**. Dominator restore/build/audit/smoke all PASS and NU1903 is gone. Do not downgrade or suppress vulnerability warnings. Subsequent Core source-filter changes were tested PASS by user on Dominator; the initial WinUI 3 build FAILED and its corrective commit still needs testing.
- GitHub Actions removed; no automatic CI runs.

## Next steps, ordered

1. On Dominator: pull latest develop; build `src/FluentMediaGallery.App/FluentMediaGallery.App.csproj` (Release win-x64, Platform=x64) after new thumbnail grid and System.Drawing.Common package. Audit NuGet packages. Launch GUI and confirm **visible window, actual thumbnails**, user-selected COPY-only small folder, manual index/search/favorites filter. **Do not index 201k library yet.**
2. Exercise Windows System.Rating on COPIES: 1-star, 4-star, 5-star JPGs and representative videos. Confirm actual read/write, on-disk persistence and failure behavior; avoid manipulating original media.
3. After thumbnail grid is verified, build the full photo preview, viewer filmstrip and robust cache support/telemetry; new GridView + Shell cache is in source, not yet validated on Dominator.
4. Harden source overlap protection, cancellation/restart, background rating importing, watcher/USN/SMB reconciliation and error retry. Do not mass-index network shares until safe.
5. Implement full video playback, tests and packaging; maintain HANDOFF after each substantive commit.

## Known architectural risks

- System.Rating may not be writable on all JPG/MP4/MKV handlers. Read-back after Commit is required; some formats cannot embed ratings.
- Offline SMB change detection requires reconciliation; watch-only is insufficient.
- User source changes must not cause accidental data loss (DB migration, partial scans, overlapping roots).
- Retry pending writes with backoff; guard media mutation and file integrity.
- Don't spend time on redundant remote verification. Give user concise local steps; change real code before repeatedly retesting.

## Session update — 2026-11-07, UI stage (commits 0078e3f, 8a7d680, cec54c9)

- Added Core source-id filtering and filtered count SQL, and regression smoke checks for per-source paging/count.
- Added first actual WinUI 3 source code: app shell, local/UNC source text input, manual scan/cancel, SQLite page of 100 rows, name search, favorites filter, click-to-view metadata, explicit favorite action, manual classification importer.
- App is **unpackaged**, WinAppSDKSelfContained=true, Microsoft.WindowsAppSDK=2.6.5 and Windows.SDK.BuildTools=10.0.28000.2705. First GUI compilation on Dominator is a REQUIRED next gate; no user GUI validation has happened.
- Static check verified all 15 XAML event handlers are present and all named controls are referenced; this is NOT a compile or UX test.
- No automatic scanning or metadata modification on launch. Registering a source doesn't scan it. The first favorite change warns before attempting file metadata write. User must test only on media COPIES.
- Not implemented: thumbnail tiles/cache, image viewer, filmstrip, video playback, source removal, background watchers, automatic startup reconciliation, installers.
- Cleanup: keep bin/obj for incremental builds until clearly unnecessary. Existing cleanup script is preview-first.

## 2026-11-07 02:17 Europe/Warsaw — Dominator GUI build failure and corrective change

- User ran full script at develop `1b085a92a279a924fbefe8a9228c8bae7ab5c166`. Git update PASS. All **35 Core.Smoke checks PASS**, including source filtered count and page tests. The WinUI 3 app build failed before launch (Core itself compiled successfully).
- Primary compiler diagnostic: `MainWindow.xaml.cs(21,22): CS0104 'DispatcherQueue' ambiguous between Microsoft.UI.Dispatching.DispatcherQueue and Windows.System.DispatcherQueue`.
- Secondary diagnostics: XamlCompiler `WMC1509` (missing LocalAssembly during MarkupCompilePass2) and `WMC9999` (NullReferenceException / object reference not set). Their connection to CS0104 is **plausible but not confirmed**; reevaluate if they remain after correction.
- Corrective source change: replace global `using Windows.System;` with explicit alias `using VirtualKey = Windows.System.VirtualKey;`, preserving Microsoft.UI.Dispatching.DispatcherQueue unambiguously. No dependency changes, no Actions.
- **Validation status:** committed source fix, NOT YET rebuilt on Dominator. Run a targeted local GUI build first. If WMC9999 persists, collect `dotnet build` diagnostics and inspect WinUI XAML compiler config, do not blindly repeat clean/restore or delete user files.

## 2026-11-07 ~02:30 Europe/Warsaw — first WinUI build PASS, thumbnail stage

- User ran `develop` at **783249406b94776aa1a2addd7efd63f7aa304fc0** on Dominator: **WinUI 3 build PASS (Release x64, zero reported warnings)** and EXE found in `src/FluentMediaGallery.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/`. User invoked EXE; script reported **PASS Application exit (exit code 0)**. User did not explicitly confirm visible window, contents or user interactions. Do not overstate GUI smoke-test status.
- Earlier Core tests with source filtering already PASS on Dominator. No need to repeat endlessly.
- Next commit **67ba83b** implemented source for virtualized `GridView` thumbnail tiles, queued processing only for realized containers, source-independent 320px thumbnail extraction through Windows Shell `IShellItemImageFactory` (read-only thumbnail API), local `%LOCALAPPDATA%\FluentMediaGallery\Thumbnails\v1` PNG cache with SHA256 file signature and a best-effort 2 GiB trimming policy. Bounded workers (3). Windows Shell decoding may support video thumbnails depending on installed handlers, with placeholders if unavailable.
- Thumbnail cache adds dependency `System.Drawing.Common 10.0.12` **for Windows-only GDI+ HBITMAP-to-PNG encoding**. This dependency and the new UI/XAML/code are **NOT COMPILED OR TESTED on Dominator**. Run restore, vulnerability audit, build and GUI rendering; fix errors in develop if any.
- Cache only writes generated PNGs under user LocalAppData, NEVER original media. Source registration still requires manual user action; no automatic full scan.
- There is still no photo viewer, bottom filmstrip, video player or incremental watchers.
