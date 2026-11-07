# Architecture and roadmap

## Library model
- SQLite WAL catalogue: Sources, Media. No image/video BLOBs in the DB.
- Paths are indexed with Windows case-insensitive semantics; media and sources survive app restarts.
- A \`favorite_override\` of 0/1 takes precedence over a file's imported \`System.Rating\`. Null means the original file rating is authoritative.
- 4 stars (System.Rating 63–87) => favorite; 1 star (1–12) => not favorite. Values 0, 2, 3, 5 => no favorite import.
- Rating reads use the Windows Property System (PKEY_Rating, read-only). For JPEG/TIFF this may come from EXIF/XMP; video support depends on the installed Shell property handler. The app's favorites override works regardless of media format.

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
3. Read-only rating importer with rate-limiting and progress; favorites UI and app-local overrides for all media.
4. FileSystemWatcher while running; USN journal on supported local NTFS volumes; periodic guarded rescan for SMB. SMB servers do not provide a universal offline change journal. A server-side optional agent can provide stronger guarantees.
5. Multi-source browsing, searching, EXIF dates, sorting and bulk actions.
6. LibVLCSharp video player, subtitles/audio tracks/fullscreen/seeking/volume; HDR/GPU evaluation.
7. Installer, CI performance benchmarks, migration/backups and stable release.

### Scale and verification
- 201,705 items baseline; 1 million item synthetic stress test goal.
- Startup reads only paged SQLite rows; never blocks on a network share.
- No automatic metadata writes to source files.
- Source path normalization and SMB reconnection require dedicated integration tests.
