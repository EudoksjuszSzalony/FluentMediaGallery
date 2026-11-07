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
- A native, **unpackaged WinUI 3 App source project is now committed**: `src/FluentMediaGallery.App`, using Windows App SDK **2.6.5** and .NET 10. It provides source registration, explicit folder scans with cancel, paged SQLite browsing (100/page), source filtering, filename search, favorites filter, read-only classification importing, file-details panel, and explicit heart toggle with a metadata-writing confirmation. **Not yet built/launched/tested on Dominator**. It currently displays file rows, NOT actual photo thumbnails/preview or video playback. The UI and WinUI dependency are not verified until a local build.
- On Dominator, user confirmed at commit **da4906ff5ee1a05e85205e905855485b903c70af**: Git pull/restore PASS, NuGet vulnerability audit PASS (zero vulnerable dependencies), Core build PASS without warning, **ALL CORE SMOKE TESTS PASSED** (2026-11-07). No actual in-file metadata write was tested.
- SQLite dependency updated to **Microsoft.Data.Sqlite 10.0.14**, resolving SQLitePCLRaw to **2.1.13**. Dominator restore/build/audit/smoke all PASS and NU1903 is gone. Do not downgrade or suppress vulnerability warnings. Subsequent Core source-filter changes and the WinUI 3 application are **NOT YET compiled/tested**.
- GitHub Actions removed; no automatic CI runs.

## Next steps, ordered

1. On Dominator: pull latest develop, restore and build `src/FluentMediaGallery.App/FluentMediaGallery.App.csproj`, run Core smoke tests (new source filtering checks). Check WinUI 3 runtime/dependency issues. Launch the GUI manually and verify window, add a **tiny COPY-only test folder**, index, pagination, source selection/search, favorites filter. **Do not index the 201k library yet.**
2. Exercise Windows System.Rating on COPIES: 1-star, 4-star, 5-star JPGs and representative videos. Confirm actual read/write, on-disk persistence and failure behavior; avoid manipulating original media.
3. Once GUI build/runtime works, implement thumbnail cache, full photo preview, viewer filmstrip and an actually virtualized grid. Current UI is an indexed LIST, not a visual tile gallery.
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
