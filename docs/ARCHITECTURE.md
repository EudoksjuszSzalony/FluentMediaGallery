# Architecture and roadmap

## Library model
- SQLite WAL catalogue: Sources, Media. No image/video BLOBs in the DB.
- Paths are indexed with Windows case-insensitive semantics; media and sources survive app restarts.
- A \`favorite_override\` of 0/1 takes precedence over a file's imported \`System.Rating\`. Null means the original file rating is authoritative.
- 4 or 5 stars (System.Rating 63–99) => favorite; 1 star (1–12) => not favorite. Values 0, 2 and 3 => no favorite import.
- Rating reads and writes use the Windows Property System (PKEY_Rating). The writer uses GPS_READWRITE / SetValue / Commit followed by a fresh read-back. Existing five-star files stay five-star on re-favorite; unfavorite writes one star. The app's favorites override works regardless of format. Some video handlers are read-only; failed writes remain pending in SQLite and return an explicit error.

## Safety guarantees
- Enumeration is streaming, iterative, avoids reparse-point recursion.
- Each complete scan stamps media with a scan generation.
- Only a complete error-free scan can mark absent rows as missing.
- Missing media are never physically deleted from the catalogue and retain favorites.
- Inaccessible sources are marked offline and retain their entire index.
- A canceled or partially failed scan never removes media.
- Existing file records keep ratings unless size/last-write changes.

## Planned
1. Unpackaged WinUI 3 application, persisted source selection, paged thumbnail grid and full-size image viewer.
2. Bounded on-disk image thumbnails (on-demand generation, versioned cache key, LRU eviction) and video frames.
3. Background rating importer with rate-limiting and progress; favorite UI uses FavoritesService (including error states and pending writes).
4. FileSystemWatcher while running; USN journal on supported local NTFS volumes; periodic guarded rescan for SMB. SMB servers do not provide a universal offline change journal. A server-side optional agent can provide stronger guarantees.
5. Multi-source browsing, searching, EXIF dates, sorting and bulk actions.
6. LibVLCSharp video player, subtitles/audio tracks/fullscreen/seeking/volume; HDR/GPU evaluation.
7. Installer, CI performance benchmarks, migration/backups and stable release.

### Scale and verification
- 201,705 items baseline; 1 million item synthetic stress test goal.
- Startup reads only paged SQLite rows; never blocks on a network share.
- User-triggered favorite changes attempt a source-file System.Rating write; read-only/unsupported files retain a pending SQLite override. Avoid implicit writes during scanning.
- Source path normalization and SMB reconnection require dedicated integration tests.

## Source selection
Never auto-register every Windows picture library, network share or visible folder. Only user-selected roots belong to the catalogue. Shared libraries must not be included unless explicitly selected.

## GitHub Actions
No push or pull_request workflows during development. Use local tests; reserve Actions for releases/assets later.
