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
- There is NO usable WinUI GUI, video player, thumbnail cache or installation package yet; these are planned.
- On Dominator, previous code at **595b5e44b6d8976bf402022aae05da961f5645d1**: **Build PASS, ALL CORE SMOKE TESTS PASSED**. That build showed **NU1903** for transitive SQLitePCLRaw.lib.e_sqlite3 2.1.11.
- This batch changes Microsoft.Data.Sqlite from 10.0.0 to **10.0.14**, which depends on SQLitePCLRaw >= 2.1.13. **Updated dependency NOT YET BUILT/TESTED on Dominator**. Confirm restore, package vulnerability scan, build and smoke tests; do not suppress warnings.
- GitHub Actions removed; no automatic CI runs.

## Next steps, ordered

1. Pull latest develop on Dominator with --ff-only, restore, build and audit transitive NuGet packages. Verify warning NU1903 is gone and run Core smoke tests; report complete output.
2. Exercise System.Rating on COPIES: 1-star, 4-star, 5-star JPGs and videos. Verify on-disk persistence, changed timestamps and unsupported formats.
3. Implement actual WinUI 3 window with selected folder roots, SQLite-backed paginated grid, offline/error states and responsive loading.
4. Add lazy thumbnail cache, viewer and visible favorite UI, then incremental watching, full video playback, tests and packaging.
5. Update HANDOFF.md with exact commit, new decisions, test evidence, blockers and next actions whenever development changes.

## Known architectural risks

- System.Rating may not be writable on all JPG/MP4/MKV handlers. Read-back after Commit is required; some formats cannot embed ratings.
- Offline SMB change detection requires reconciliation; watch-only is insufficient.
- User source changes must not cause accidental data loss (DB migration, partial scans, overlapping roots).
- Retry pending writes with backoff; guard media mutation and file integrity.
- Don't spend time on redundant remote verification. Give user concise local steps; change real code before repeatedly retesting.
