# FluentMediaGallery — HANDOFF

**Last handoff update:** 2026-11-07 — viewer crash/Photos-like layout hotfix. Read the newest section at END; older sections describe historical state. Read this file and AGENTS.md at the start of EVERY chat, and update both when a decision changes. This is a living status/history file. Do not claim unrun tests passed.

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
- A native, **unpackaged WinUI 3 App source project is now committed**: `src/FluentMediaGallery.App`, using Windows App SDK **2.6.5** and .NET 10. It provides source registration, explicit folder scans with cancel, paged SQLite browsing (100/page), source filtering, filename search, favorites filter, read-only classification importing, file-details panel, and explicit heart toggle with a metadata-writing confirmation. **Dominator GUI BUILD PASS for thumbnail GridView at commit c8f077b (2026-11-07), with screenshot evidence showing an open WinUI 3 window, actual photo thumbnails and 530 indexed JPG/PNG files in one selected source.** New image-viewer and automatic rating-import changes are committed but NOT YET built/visually tested. Video playback is not implemented.
- On Dominator, user confirmed at commit **da4906ff5ee1a05e85205e905855485b903c70af**: Git pull/restore PASS, NuGet vulnerability audit PASS (zero vulnerable dependencies), Core build PASS without warning, **ALL CORE SMOKE TESTS PASSED** (2026-11-07). No actual in-file metadata write was tested.
- SQLite dependency updated to **Microsoft.Data.Sqlite 10.0.14**, resolving SQLitePCLRaw to **2.1.13**. Dominator restore/build/audit/smoke all PASS and NU1903 is gone. Do not downgrade or suppress vulnerability warnings. Subsequent Core source-filter changes were tested PASS by user on Dominator; the initial WinUI 3 build FAILED and its corrective commit still needs testing.
- GitHub Actions removed; no automatic CI runs.

## Next steps, ordered

1. On Dominator: close the running preview normally (last observed PID 61220 may be stale), pull current develop and build Release x64 using scripts/Build-WinUIPreview.ps1. Launch and verify **new in-window photo viewer, left/right arrows, Esc, filmstrip, page boundary navigation**; verify copied JPG ratings auto-import after source scan. No full 201k library scan yet.
2. Exercise Windows System.Rating on COPIES: 1-star, 4-star, 5-star JPGs and representative videos. Confirm actual read/write, on-disk persistence and failure behavior; avoid manipulating original media.
3. Viewer image preview and filmstrip now exist in source, but need Dominator build/runtime validation. Once verified, extend to a complete photo viewer with zoom/pan, true fullscreen, video playback and robust cache diagnostics; avoid large network scans until hardened.
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

## 2026-11-07 03:22 Europe/Warsaw — thumbnail build blocked by running app

- User Dominator log at develop c8f077bd: Git branch/status/pull PASS, restore PASS, NuGet audit PASS with no vulnerable packages.
- App build FAILED because old FluentMediaGallery.App process PID 62700 held destination FluentMediaGallery.Core.dll open. MSBuild warnings MSB3026, errors MSB3027 and MSB3021. Core compiled; new thumbnail code is NOT YET verified by compilation. This is a file lock, not evidence of a source error.
- Close the existing gallery **normally** first; do not force-kill during scan/metadata writes. No clean of bin/obj or SDK reinstall is indicated.
- Added scripts/Build-WinUIPreview.ps1: requires clean develop checkout, pulls fast-forward, checks for running app BEFORE build, builds Release x64, optional -Start launches preview. Does not kill processes or delete files.
- NEXT: user closes preview app, runs the fail-fast helper via USER PowerShell, reports outcome. If build succeeds test visible UI and actual thumbnails on a small COPY-only media folder. No full 201k scan, no repeated Core smoke test required.

## 2026-11-07 ~03:25 Europe/Warsaw — first visible thumbnail gallery PASS / viewer implementation

- User supplied actual Dominator screenshot of FluentMediaGallery running from `c8f077bd9c16ce7f0c2afcec659e6d3eff00d268`: **WinUI 3 Release x64 build PASS**, app process started (PID 61220), a visible window shows many correctly rendered real image thumbnails and **530 indexed items** in explicitly chosen source CP77. This supersedes earlier uncertainty about whether GUI is visible. Only this image-heavy folder was visually verified; no evidence yet for video thumbnails, SMB NAS, metadata writes, million-item performance or full media library.
- Screenshot revealed selected JPG shows `System.Rating: nieodczytany / niedostępny` and `Dodaj do ulubionych` despite example 4-star source file. This is because initial implementation only read ratings when a manual import command ran. User had explicitly required 4/5 stars restored from files during first index.
- New Core change (commit `1f86146`) scopes pending-rating queries/import to sourceId; new regression smoke tests. New App change (commit `70ee148`) automatically performs **read-only** System.Rating import for that user-selected source after a **complete manual index scan**, in batches of 64, with progress and cancellation, without blocking UI startup. It refreshes the page after imported ratings are committed. Incomplete/offline scans do NOT auto-import. No metadata write during scans.
- New UI (commits `a83dfbc`, `70ee148`, `4a3cffc`) adds **in-window photo viewer** with background decoded image loading and 2560px max decode width, left/right navigation across pages, Esc to close, horizontal filmstrip backed by current 100-row SQLite page, favorite toggle via shared existing verified-write workflow. Videos can show a Shell extracted still thumbnail and explicitly say playback is not yet implemented. The viewer is NOT OS fullscreen, does not have pan/zoom and is limited to paginated in-memory rows.
- **Status: new viewer and automatic per-source rating importer source commits exist on develop but are NOT YET BUILT OR TESTED on Dominator.** Need focused local build/GUI check. No GitHub Actions triggered.
- Build caution: close previous FluentMediaGallery process before rebuild to avoid MSB3026/MSB3027 file lock; scripts/Build-WinUIPreview.ps1 detects it and aborts without killing it. Do not clean bin/obj unless truly necessary. Next UI test should use copies, including a JPG rated 4/5 stars and a 1-star JPG to verify imported favorites.

## 2026-11-07 — Dominator viewer PASS, new separate-window and folder-tree stage

USER EVIDENCE:
- User demonstrated actual Dominator WinUI 3 app at commit 8c5cd01: Release x64 build PASS and screenshot of an open image in previous in-window viewer, correct picture with filmstrip and visible favorite indicator. This verifies previous viewer display only, not new standalone-window code.
- User requests nested expandable folders for CP77, folder-plus button invoking native folder picker, and safe shortcuts if a subfolder is registered within an existing indexed root (avoid duplicate sources).
- User also requests independent native image-viewer window, retain context (source/folder/all/favorites and later sort), F filmstrip, L favorite, Esc, mouse wheel navigation, Ctrl+numpad +/- zoom, slider, hover-only arrows, right-click menu and motion animations.

IMPLEMENTED ON DEVELOP (SOURCE ONLY; NOT YET BUILT ON DOMINATOR):
- Core: additive SQLite shortcuts table; RegisterLocationAsync records a nested path as a shortcut referencing existing source; adding an ancestor of an existing source is blocked to prevent overlapping indexing. Folder-filtered GetPageAsync/CountFilteredAsync and SQLite-only GetIndexedChildFoldersAsync (offline source retains last indexed subfolders). Smoke tests expanded for shortcut persistence, no duplicate source, folder filtering and immediate subfolder discovery.
- Main UI: expandable, lazily built hierarchy for indexed subfolders; extra shortcut rows; button with folder and plus icons using Windows.Storage.Pickers.FolderPicker and WinRT.Interop.InitializeWithWindow; UNC manual text box retained. Selecting subfolder filters existing SQLite rows. No background disk traversal on startup.
- Standalone ViewerWindow.xaml and ViewerWindow.xaml.cs: launches a new native WinUI Window for any clicked tile, retaining a GalleryViewContext snapshot of source, folder, favorites, filename search and fixed modified-desc ordering. Uses SQLite paging (100/page) across boundaries, never loads all files at once. Gallery stays open.
- Viewer: image rendering on demand, 25–500% ScrollViewer optical zoom with slider and Ctrl+Numpad +/- (Ctrl+Num 0 to fit), F toggles filmstrip, L toggles favorite, wheel and left/right keys navigate, Esc closes, right-click menu supports favorites, filmstrip, zoom, copy path and open Explorer. Nav arrows hidden until mouse hover with 180 ms fade. Video shows still-image placeholder only; no playback yet.
- Existing hidden in-main-window viewer overlay and associated code remain temporarily as safe fallback until standalone viewer compiles and passes smoke test; gallery click now opens the separate ViewerWindow.
- Sorting currently is fixed modified_ticks DESC/id DESC; SortKey is passed for future ordering but selectable sorting is NOT YET IMPLEMENTED. Filmstrip hide is immediate; other animations may be added after verification.

VALIDATION STATUS: New revision committed, static checks for WinUI XAML event handlers and relevant SQLite code performed, but NOT compiled or tested on Dominator. Do not call it working until tested. No GitHub Actions triggered.

NEXT STEPS:
1. Close running FluentMediaGallery first, use scripts/Build-WinUIPreview.ps1 -Start in USER PowerShell; run Core.Smoke once for new DB logic. Fix any concrete WinUI compiler errors without blind bin/obj cleanup.
2. Verify gallery remains open and second OS window appears; navigate boundary of 100-row page; test Ctrl+Numpad +/-, L, F, wheel, Esc, zoom slider, right-click menu, fade arrow hotspots. Test metadata writes ONLY on media COPIES.
3. Expand CP77 subdirectories in left sidebar; use folder-plus picker; add an already indexed child as shortcut and confirm no duplicate source/media. Test on an offline source separately.
4. After PASS, remove legacy overlay code and implement selectable/persisted sorting, polished animations, video playback and robust large-library indexing.

## Final check for this implementation batch

- Latest implementation fix commit 0a34b837: guard early ZoomScroll.ViewChanged callbacks before ViewerWindow initialization.
- Static XAML tag-balance validation PASS for MainWindow.xaml and ViewerWindow.xaml. Every declared XAML handler in both windows is found in corresponding C# sources. All named ViewerWindow controls are referenced in code. Core source contains folder/shortcut SQLite schema and tests. These checks do NOT establish that WinUI XAML compiler or GUI will pass on Dominator.
- User-run test remains pending. Test .NET Core.Smoke for new shortcut queries and build the WinUI Release x64. Avoid triggering GitHub Actions, avoid deleting bin/obj. Close previous running FluentMediaGallery before using scripts/Build-WinUIPreview.ps1 -Start.

## 2026-11-07 — Dominator PASS at b7cfbbd and new river/sorting/zoom batch

### Confirmed by user on Dominator before this new batch
- At `b7cfbbd852eda7c7105ff8a55203a819d43ed1d8`: `scripts/Build-WinUIPreview.ps1` WinUI Release x64 build PASS, Core.Smoke **ALL PASS**, application actually launched and screenshots were provided.
- Screenshots confirm new separate native viewer window, CP77 nested sidebar tree, persistent shortcut `wallpapers`, filmstrip, right-click context flyout and zoom controls are visible. Physical file System.Rating write verification is still NOT confirmed. Video playback is not implemented.
- User-reported defects: sidebar folder title top-aligned, chevrons on left (should be right), sidebar width fixed; image viewer opens narrow window then maximization leaves undersized first picture, 100% label misleading, zoom snaps hard; filmstrip flashes per next/previous, excessive padding and selected tile styling; large full-height flashing arrow columns; white system titlebar and too tall top toolbar. User requests gallery Rzeka/justified layout, right date/rating scrubber, no explicit pagination, favorite/rating sorting, per-folder sorting retained independently, adjustable sidebar width and faster thumbnails.

### Implemented in source on develop (COMMITTED, NOT BUILT/TESTED on Dominator)
- Viewer: `ViewerWindow.xaml` now uses `ExtendsContentIntoTitleBar` and a compact dark draggable titlebar area (system caption buttons retained). Toolbar tightened, zoom relocated to bottom-right. Small 34x50 hover arrows instead of full-height buttons; stop previous Storyboard on hover change. Zoom default now FIT, exposes 100% decoded image size separately, correctly recalculates on window resize, smooth ScrollViewer ChangeView, slider/keyboard step stops including FIT percentage. Initial layout fallback uses AppWindow.Size to avoid 1-pixel-size bug. For source images larger than the viewer's 3840-pixel decode cap, 100% represents the decoded preview; true full-resolution 1:1 decode/tiling is future work.
- Filmstrip: cache 100-record SQLite page and reuse `MediaRow` / cached thumbnails while navigating within it (fix repetitive blink). Reduced height to 90 and tighter tile spacing, current selection has a narrow blue indicator. Favorite changes update the row without full filmstrip reload unless sorting/filter means the sequence changes.
- Gallery: manually resizable sidebar through 6px drag handle and ColumnDefinition width limits (not persisted yet); folder labels vertically centered, chevrons on right. ComboBoxes `SortBox` and `ViewModeBox` added; per-folder/source/all/favorites preferences persisted in SQLite `view_preferences`. Sort modes: modification asc/desc; creation asc/desc; name asc/desc; raw file rating asc/desc; app-visible favorite-first/last. Captured/EXIF date sort is NOT YET implemented; do not mislabel date-modified as date-taken.
- Core SQLite migration: added `media.created_ticks` with additive ALTER (the index is created after ALTER for old databases), and `view_preferences` table. Indexer saves CreationTimeUtc for future/re-scanned files. Existing media records may have created_ticks=0 until next successful reindex; SQL creation sort falls back to modification date for those rows. Dynamic ORDER BY comes only from a hardcoded allowlist, not user interpolation. Added smoke tests for sort persist/independence/order and date range.
- Gallery: removed visible First/Prev/Next page buttons. `GridView`/`ListView` load another 100 SQLite media records near the scroll bottom; direct viewer offset includes current index window start. **Current append-only model retains visited records in memory**, so it is NOT the final fully bounded 1-million-item virtualization. Later implement a windowed/virtual data source. No auto-scan on startup.
- Rzeka: `RiverLayout` generates justified rows with tile widths based on source image aspect ratios (`System.Drawing.Image.FromFile` on a background task for currently loaded pages only); videos default to 16:9. Rows are ListView ItemsStackPanel, with lazy thumbnail loading and month/year headers. The first implementation rebuilds rows on append/resize; optimize to preserve scroll/avoid blink, and avoid excessive original file header reads on slow NAS. `ShellThumbnailCache` still uses **320px**, there was no 800px tier to remove. Worker limit raised 3→5.
- Gallery timeline: right vertical Slider allows jump to an approximate indexed offset; labels derive from filtered SQLite min/max dates or 0–5 stars for classification, favorites symbols for favorites sort. This is an INITIAL date/rating navigator, not yet the fully grouped time scrollbar of Microsoft Photos; date taken and precise rating bucket seeking remain future work.
- Build evidence: static XAML tag/handler check previously PASS. NO NEW DOMINATOR/VM COMPILE of this batch at the point of the handoff. Do not claim PASS for new code. No routine GitHub Actions used. Remote Desktop Commander VM was online but not used as proof of target functionality.

### Next actions / validation gates
1. User closes all FluentMediaGallery instances. Pull develop and run local Core.Smoke once (DB migration + sorting), then WinUI Release x64 build using the existing fail-fast script. Fix exact errors in develop if any; do NOT clear bin/obj blindly.
2. On a small copied-media folder, verify separate viewer: initial FIT, maximization resize, 100% vs fit, slider/keyboard smooth steps, thin selected indicator, filmstrip not blinking, no hover flash, black custom titlebar. Verify sidebar drag, chevrons/vertical centering.
3. Verify sorting is remembered independently for CP77, CP77/wallpapers, All Media and Favorites; verify favorites/rating ordering, scrollbar date and 0–5★ labels, Rzeka aspect/rows, automatic append near scroll bottom, and no visible pagination.
4. Before mass-indexing 201k media: virtual data window/async source thumbnails at >100k, true full-res photo zoom, actual EXIF creation/capture timestamps, efficient river reflow and date scrubber groups. Add video playback and incremental SMB watcher/reconciliation. Do not use Actions on regular commits.

## 2026-11-07 ~04:52–05:XX Europe/Warsaw — Dominator new UI PASS build, repeatable zoom crash; source fixes committed (unverified)

### User-provided evidence / needs

- User supplied screenshots of the develop version at `d46e037` on **Dominator**: main WinUI gallery shows "Rzeka" rows and "Kwadrat"; per-folder sort appears persisted; photos opened in separate ViewerWindow. Previous user logs show build/smoke PASS for earlier version, but no fresh build log was supplied for this exact revision. Treat screenshots as **visible UI evidence** rather than claiming exact compilation results.
- **Repeatable runtime exception on image open / window maximization:** "The MinZoomFactor property cannot be set to a value smaller than 0.1." This was directly caused by `ViewerWindow.xaml.cs` assigning `ZoomScroll.MinZoomFactor = 0.05f` in `RecalculateFit()`. Users saw partial black screen, then window/application termination. **Root cause verified from source and error message.**
- User wants UI closer to Windows Photos: remove details pane from Gallery, show Details only when clicking a button in photo Viewer; titlebar icon/actions (favorites, filmstrip) next to system minimize/maximize/close in BOTH windows; sort menu with field group (modified/created/name/rating/favorites) followed by "Ascending/Descending"; square layout filling available gallery width; filmstrip selection outlined around full thumbnail, no left blue bar; improve date-grouped "Rzeka" without giant stretched stripes.

### Actual develop source commits implemented — NOT YET rebuilt/tested on Dominator

- `9e961ef` and `0734a9d`: fix crashing MinZoomFactor. Viewer ScrollViewer now has valid Min 0.1 / Max 10 in XAML, no code path sets invalid 0.05. Internal layout image uses 0.5x decoded-pixel dimensions, so UI percentages 5–500 correspond to supported ScrollViewer factors 0.1–10. Maintains separate fit-to-window percentage with pixel scale. This is a source-level fix, runtime validation PENDING.
- `7750037`: compact ViewerWindow chrome. Favoriting (L), Filmstrip (F), Details toggle controls are in top custom bar before reserved native caption buttons. Separate on-demand viewer DetailsPane overlays photo surface (file name, type, size, Windows rating, path); default Collapsed; main gallery metadata writes unchanged. Filmstrip selected row gets a full blue Border instead of a thin left strip. Initial zoom percent uses placeholder until image decoding completes.
- `06a774f`: MainWindow dark custom titlebar with Favorites toggle and add-folder button before native caption; old right-hand Details panel made Collapsed and column Width=0, so no right-side space. Sort UI changed from one flat ComboBox to MenuFlyout first listing Modyfikacja/Utworzenie/Nazwa/Klasyfikacja/Ulubione, then separator + Rosnąco/Malejąco. Existing per-view saved `_sortKey` preserved; title shows selection and arrow. Square gallery tile panel dynamically calculates ItemWidth on resize; fixed-width border and image removed so tiles fill row. Legacy hidden detail controls and old in-main viewer remain in XAML/code to avoid breaking existing references and can be cleaned later.
- `9bdae49`: Rzeka rows target ~202 DIP, height clamp 145–240, source photo aspect ratio clamp 0.65–2.8 to prevent ultra-wide logos collapsing row height, date groups cannot mix within a row. Empty repeated date headers collapsed; reduced double spacing; `SameRiverRow` diffing preserves unchanged row controls and thumbnails on append/resize instead of clearing whole ListView. This is closer to Photos but not pixel-identical and NOT verified on Dominator.
- `a04352a`: after image decode, schedule a low-priority fit recalculation once layout is ready, avoiding first-image appearing tiny due to ChangeView before measurement. Added `FavoriteRatingPolicy.ToStars` and smoke assertions: Windows raw 75 displays **4★** (previous viewer formula incorrectly showed 3★), 99=5★, 1=1★.
- `2595149`: bottom filmstrip has ListView selection disabled, `IsItemClickEnabled` with click handler, compact item-container padding and a single custom selection outline via `MediaRow.SelectionOpacity`. Avoid default selected background/highlight interfering with user's request.
- No dependency changes or GitHub Actions added. No changes to original photos, video files, caches, SQLite library or backups during code commits.

### Validation and limitations

- Static source check on latest `2595149`: MainWindow.xaml + ViewerWindow.xaml balanced tags PASS, no duplicate x:Name, all **43** MainWindow events and **26** ViewerWindow events have C# handlers, no dangling `SortBox` usage, original invalid zoom bound absent. This does NOT mean WinUI XAML compiler/build/runtime passes.
- **Latest source has NOT been built or tested on Dominator.** User must run `scripts/Build-WinUIPreview.ps1` from clean USER PowerShell with all app windows closed, then Core.Smoke (new star mapping tests), launch and test.
- Primary gate: open photo, verify displayed zoom %, maximize and restore 2–3 times without crash or black frame, 100% vs fit, scroll/zoom slider. Test photos with very different resolutions, incl. portrait and landscape.
- UI gates: gallery has no right-hand Details pane, Viewer Details button toggles on demand, both top bars dark with buttons next to native controls, sort menu fields/directions and persistence, square layout width resizes, Rzeka grouping/row widths, selected filmstrip picture outlined ONLY. Also navigate while filmstrip open; no flicker expected within SQLite page.
- Known unfinished work: actual Windows Photos visual fidelity/animations still incomplete; true EXIF DateTaken, advanced Rzeka masonry/timeline scrubber, finite-memory virtualized scrolling for >201k, true full-resolution 1:1 tiled decoding beyond viewer 3840px cap, video playback, offline SMB incremental reconciliation. Legacy hidden viewer and details code should be removed after successful Dominator validation.
- All changes on sole `develop`; `main` unchanged. No deletion/cleanup performed. Test favorites metadata writes **only on COPY media**; file write success still unverified by user.

## 2026-11-07 — Dominator viewer now resizes successfully; edge-fit, dimensions and Rzeka improvements

### Confirmed feedback from user
- Screenshots of latest previously supplied version show the standalone ViewerWindow opening, image fit scale correctly changing after resize, working custom filmstrip selection frame, and functioning on-demand Details panel. User explicitly: "obramowanie na dolnym pasku git", "szczegóły git", "zmiana rozmiaru okna podglądu git". The MinZoomFactor crash is not reported for this latest test; previous crash is likely resolved but do not claim exhaustive stability.
- User wants the image to **occupy the maximum available viewport without distortion**, not have unnecessary side gutters; bottom-left viewer bar should display **original image dimensions in pixels + extension (JPG/PNG)** instead of long file path. Current Rzeka still looks far from Microsoft Photos: screenshots show extreme underfilled rows due to forced new rows whenever modified month changes. User requests continued actual improvements, not just plan.

### Implemented on develop in this batch — source only, NOT YET compiled/verified on Dominator
- Commit `23c6898`: ViewerWindow photo surface now assigns full available image width to ScrollViewer (removed always-reserved 46 DIP left/right columns; navigation arrows now overlay thin edge areas). Viewer Details panel moved INSIDE PhotoSurface as a separate 290 DIP grid column instead of covering the image. Toggling details changes the column width 0↔290. New ZoomScroll.SizeChanged listener recalculates FIT when that column changes. Subtracted only 2 pixels padding from measured viewport instead of 12. The primary gallery window and separate photo viewer remain distinct.
- Viewer bottom-left `Status`: on image load, read original pixel dimensions using `Windows.Graphics.Imaging.BitmapDecoder` from image stream, Seek(0) then show preview via BitmapImage. Show `WIDTH × HEIGHT px · JPG/PNG/...` (JPEG normalized to JPG). On an unsupported decoder fall back to the bitmap's decoded dimensions; video queries `GetVideoPropertiesAsync` when available. Details also displays dimensions. Errors/confirmation from favorite-write, copy-path and Explorer now use ImageMessage instead of overwriting pixel dimensions. Source media is read-only during preview.
- Commit `82ab235`: Rzeka builder now treats the indexed result stream as CONTINUOUS rather than splitting on every different month. Uses a single date-range header above the first row (minimum/maximum from filtered SQLite date range); fills rows with a target ~194 DIP and chooses row size by logarithmic closeness to preferred height, penalizing singular banners and excessive small/large rows; clamps photo ratios 0.55–3.6 and final row can remain left-aligned rather than grotesquely stretched. Header hidden on following rows. This is closer to continuous justified Microsoft Photos but NOT yet identical.
- Static validation: both ViewerWindow/MainWindow XAML tag stacks PASS. All 43 MainWindow handlers present across MainWindow.xaml.cs+MainWindow.Viewer.cs, all ViewerWindow handlers found in ViewerWindow.xaml.cs; no duplicate x:Name. No WinUI build/runtime test performed for these newest commits on Dominator. GitHub Actions not run.

### Next validation and outstanding gaps
1. Close ALL FluentMediaGallery windows before rebuilding (locked DLL). Pull develop, build WinUI Release x64; check Core smoke only if Core has changed (not in THIS batch). Test read-only photos including 16MP PNG and 1920×1080 JPG.
2. Test ViewerWindow initial full fit, maximize/restore, toggle Details repeatedly: image must shrink to remaining width when details opens and expand back when closed, without covering image or crash. Bottom-left must say original image pixel size and type, not path. A landscape photo in a portrait window will necessarily have black bars above/below unless cropped: we preserve full aspect ratio instead of destructive cropping.
3. Check Rzeka at 530-item CP77 source and nested folder: avoid a separate large row for each month, smaller empty gaps, row filling at multiple window widths, preserving scroll and thumbnails. Assess real visuals before declaring parity with Microsoft Photos.
4. Before 201k+ media, improve Rzeka dimension cache via SQLite and virtualized windowing, add EXIF DateTaken, date/rating segmented scrubber, video playback and offline SMB reconciliation. The full image viewer still previews up to 3840 pixels in decoded width; dimensions in the bottom left are now read from original file headers. This is not yet full-resolution zoom.
5. Keep testing System.Rating writes only on media COPIES; physical writes still not confirmed. Never delete originals, user SQLite or backups; no automatic external-share discovery or routine GitHub Actions.

### Final follow-up for this viewer and Rzeka batch

- Commit `1ead862`: removed default ListViewItem padding, margin and minimum height for Rzeka; StackPanel spacing now provides exactly one 3-DIP inter-tile gap instead of per-item trailing Margin. Added a graceful NotSupportedException fallback for uncommon image codecs when reading aspect ratios.
- Static verification on head `1ead862b9db4d1f18a0921aef1d8b026b491b452`: both Window XAML tag structures PASS and all MainWindow + ViewerWindow XAML event names resolve to methods across the correct code-behind files. Verified that ViewerWindow shows metadata via BitmapDecoder and file extension, DetailsPane consumes its own column instead of overlapping photo, full image viewport margins are ~2 DIP, and month-change breaks are absent from RiverLayout. This is source inspection, NOT a compiled or user-tested build.
- Build/test next: user closes all app windows, runs the existing fail-fast build script then visually tests one landscape 1920×1080 JPG, one portrait PNG and one >3840px image if available; click Details toggling and viewer maximize must keep image inside remaining viewport. Confirm bottom-left dimension label reads original pixels and type, frame selection unchanged. Compare continuous Rzeka on CP77 versus original Microsoft Photos screenshot. The **next app/GUI build result has not yet been provided**.

## 2026-11-07 06:56+ Europe/Warsaw — Photos-style mosaic, unified viewer chrome and panel animations

### Dominator feedback for previous code (CONFIRMED FROM USER SCREENSHOTS)
- User supplied newer screenshots of FluentMediaGallery CP77 and Microsoft Photos: selected photo's original pixel size/format display is visible, details pane functions, filmstrip selection outline works and viewer resize was confirmed in previous feedback. Current Rzeka is still too monotonous: all items in a given row have the same height; Windows Photos uses variable-height, multi-row feature tiles.
- User requests a single viewer top toolbar with all actions, filmstrip ABOVE the lower zoom/properties bar, larger dimensions text + media file size, subtle horizontal photo switch animation, smooth opening/hiding of Details and filmstrip panels and expansion/collapse in the gallery folder tree. Gallery sidebar should be dimensionally/visually closer to Photos, but still user-resizable.

### External research
- Flickr open-source justified-layout approach (https://github.com/flickr/justified-layout) uses source aspect ratios to fit row widths. Plain justified rows are insufficient for the requested Windows Photos-like layout because every photo still shares the same row height. Microsoft WinUI animation documentation recommends native AddDeleteThemeTransition/RepositionThemeTransition for lists and efficient render-property animations where possible.
- IMPLEMENTED ORIGINAL C# MOSAIC DESIGN based on the general technique, NOT a vendor-copy of the Microsoft Photos layout/algorithm. No third-party package, image asset, or license import.

### Changes committed to sole develop in this batch (NOT YET BUILT OR RUNTIME-TESTED ON DOMINATOR)
- `70bce36` / `58d94fa`: Rewrite `RiverLayout.cs` to output virtualized mosaic bands containing absolute tile coordinates and heights. Band candidates include normal aspect-justified rows plus double-tier featured compositions: one photo spans two different-height rows; the remaining media occupy smaller tiles next to it. Chooses geometry from source ratios and viewport; cycles ordinary and featured bands, maintains media logical order; blocks from 3 to 7 media items; no recursive file scanning. Rzeka renders each virtualized band with a sized Canvas and positioned buttons. Ratios read for currently loaded SQLite pages only. The final lone portrait tile has a safe fallback so no item is silently omitted.
- `d513e2a`: Correct WinUI Canvas binding issue: WinRT does NOT evaluate `{Binding}` inside Style.Setter.Value; use per-item Button.RenderTransform TranslateTransform X/Y instead of invalid Canvas.Left/Top bindings. Layout position and click hit tests expected to move with the visual, require local validation.
- `4035f66`: ViewerWindow now has ONE compact top header/action bar: gallery back button, file name/source label and favorites/filmstrip/details buttons next to the OS window controls. Height is 44 DIP, prior separate 31+38 DIP bands removed. Filmstrip moved ABOVE the zoom bar; bottom properties/zoom row enlarged to 54 DIP; dimensions label 13 DIP semibold and adjacent size label 12 DIP. XAML now uses rows [title, photo, filmstrip, properties].
- `242d0f7` / `b1e21d3`: New FileSizeLabel shows media size (MB/KB) next to existing original pixel dimensions and extension. Filmstrip toggle and viewer Details toggle now have 160–190ms fade/TranslateTransform effects; previous animations are stopped on rapid clicks. Layout reflows after closing the panel; size may snap at end of disappearance (not yet continuous grid-width animation). Image switching now uses only a restrained 22 DIP horizontal move, 130ms. Initial image fit and image navigation no longer animate zoom (manual zoom still smoothly interpolates).
- `02ccd70`: Main sidebar default width 270→220 DIP, min 175 max 500 DIP, padding reduced, no duplicated big app title, row height/indent reduced, folder icon switched to small FontIcon, empty Status rows collapsed. Folder expansion uses prefix/suffix observable-collection diffs rather than Clear+re-add-all; native AddDeleteThemeTransition/RepositionThemeTransition should animate inserted/removed tree items.
- `3a1c165`: Gallery and Favorites navigation entries grouped in the compact left pane, with independent quick access. All selected local/UNC paths and existing shortcut behavior remain; no automatic source discovery.
- No changes to SQLite schema, favorites write semantics, source scan safety or dependency versions. No GitHub Actions invoked. No user media/cache/backup files removed. Existing scripts/Build-WinUIPreview.ps1 checks branch cleanliness, fast-forward pulls, checks running GUI and builds WinUI Release x64; has -Start option.

### Verification status / next gates
- Static structure PASS at `3a1c165`: Viewer/MainWindow XAML tags balanced, no duplicate XAML control names, 27 Viewer events + 44 MainWindow events resolve to code-behind, renderer creates Canvas with per-tile TranslateTransform bindings, animation toggle methods and tree transitions present. A local JavaScript geometry simulation using 21 representative ratios at 680-DIP width selected multiple double-height mosaics and normal rows, confirming candidate selection in principle; **this is not a compiled C# test**.
- **NOT YET COMPILED OR TESTED ON DOMINATOR.** User should close ALL app windows and run `scripts/Build-WinUIPreview.ps1 -Start`. Test Rzeka geometry: featured double-height images, no overlapping image/click regions, no skipped portrait tail image, several widths, scroll and thumbnail cache. If WinUI XAML complains about item binding or transitions, fix exact error. No machine-runtime test was made in this batch; local container lacks dotnet.
- Viewer regression tests: one top bar, filmstrip ABOVE zoom section, dimensions+file size on lower left, short photo slide but no zoom flicker, F toggles filmstrip, Details toggles with fade/slide, resize fit remains stable, keyboard/wheel nav unchanged.
- Sidebar test: default width looks similar to Microsoft Photos at normal Windows scale; users can still drag; expand/collapse deep folders to check no reset/scroll jumps. Visual dimensions can differ with DPI and scaling; NOT an exact pixel-perfect claim.
- Future technical debt: true physically bounded virtualization for 201k+, preindexed dimensions instead of GDI file header reads from NAS, smarter global mosaic optimization across the collection, reflow without scroll jump on width changes, EXIF date taken, video playback and SMB change reconciliation. Do not conflate photo preview with video playback or direct file-rating write verification; write only to COPIES.
