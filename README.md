# FluentMediaGallery

Open-source Windows 11 photo/video gallery, based on WinUI 3 and SQLite. The goal is a fast alternative to Microsoft Photos for a library of 201,705 media files and beyond.

> **Early preview source code.** There is now an initial WinUI 3 app project, but its compilation and GUI behavior are **not yet verified on Dominator**. The thumbnail grid, in-app photo viewer, video player and installer are still pending.

## Implemented in source

- Persistent SQLite WAL media index, rescan with missing-file safeguards and offline NAS protection.
- Local and UNC/SMB path registration, only for user-selected source folders.
- Core rating read/write support using Windows `System.Rating`, file write verification and SQLite fallback.
- Favorites: **4 and 5 stars count as favorites**, removal writes **1 star**. Existing 5-star classification is preserved when favoriting.
- New unpackaged WinUI 3 preview: three-panel UI, manual source registration and manual background indexing with cancellation; SQLite pages of 100, file-name search, source and favorites filtering, details, explicit favorites toggle with warning and manual rating import.
- Virtualized WinUI GridView, Windows Shell thumbnail extraction with bounded background workers (3), local PNG disk cache under LocalAppData, best-effort 2 GiB trimming and file-change cache keys. **Working image thumbnails confirmed on Dominator for a 530-file local source**.
- **New source code, awaiting build/validation:** in-window full-size photo viewer with arrow/Esc navigation and horizontal current-page filmstrip; automatic **read-only** import of Windows System.Rating for a selected source after a complete manual scan.

### Not yet implemented

- Photo zoom/pan, true OS fullscreen, viewer polish and cache diagnostics. A basic image viewer and current-page filmstrip are now in source but not yet tested on Dominator.
- Integrated image viewer, filmstrip, video player and full playback controls.
- Live FileSystemWatcher, local USN recovery, SMB re-sync after shutdown, thumbnail cache invalidation.
- Installer, release assets and validated GUI performance on the real media library.

## Development and safety

All routine work takes place on `develop`; `main` is for verified stable releases. GitHub Actions **do not run** for push/pull-request; reserve them for releases. User tests on the **Dominator Windows PC**, not on the auxiliary Remote Desktop Commander VM.

Read [HANDOFF.md](HANDOFF.md) before every session. Also see [AGENTS.md](AGENTS.md), [development instructions](docs/DEVELOPMENT.md), and [architecture](docs/ARCHITECTURE.md).

The user has verified **Core** on Dominator with `Microsoft.Data.Sqlite 10.0.14` and SQLitePCLRaw 2.1.13: successful restore, vulnerability audit, build and Core smoke tests without warnings. **Core source-filter tests, WinUI build and thumbnail rendering PASS on Dominator at c8f077b.** New image-viewer and automatic rating-import changes still require local compilation/behavior testing.

To test the new app, open `src/FluentMediaGallery.App/FluentMediaGallery.App.csproj` with the Windows .NET 10 SDK/Windows App SDK toolchain and build Release. The UI is unpackaged and self-contained for Windows App SDK runtime. First use **copies of media** in a small local test folder, never the entire personal library. An explicit favorite change may attempt to write file metadata.

No original media or database belongs in Git. User data/index is under `%LOCALAPPDATA%\FluentMediaGallery`. MIT license governs the project code; video codecs will carry separate notices.

## Separate viewer and indexed folders (new development source, unverified)

After a tile click, the gallery opens a separate WinUI ViewerWindow with navigation tied to the originating gallery context (folder, selected root, favorites or all media, plus filename search). The viewer has a hideable filmstrip (F), favorites (L), mouse wheel/arrow navigation, Esc, 25–500% zoom slider and Ctrl+numpad +/-, right-click menu and hover-fading navigation arrows.

The gallery sidebar now shows expandable directories represented by indexed media paths. An icon button opens the native folder picker. Registering a nested folder creates a persistent shortcut to the existing indexed source, not a second index, while adding an overlapping ancestor is blocked. Empty folders aren't listed unless they contain indexed media.

**This new revision is not yet built or visually verified on Dominator.** Full video playback, user-selectable sorting and polished filmstrip animations are future steps.
