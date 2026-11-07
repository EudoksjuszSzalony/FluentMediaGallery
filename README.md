# FluentMediaGallery

Open-source Windows 11 photo and video gallery built with WinUI 3. The goal is a fast, trustworthy alternative to Microsoft Photos for large libraries, including local drives and SMB/UNC shares.

> **Status: early development (develop branch).** This repository is not yet a finished gallery or media player. Do not treat it as a release.

## Goals

- Native Windows 11/Fluent UI with virtualized photo/video browsing.
- Persistent SQLite index, incremental reconciliation and a bounded thumbnail cache.
- Favorites on photos **and videos**, visible directly in the viewer.
- Read legacy Microsoft Photos favorites via the file's Windows \`System.Rating\` property: 4 or 5 stars = favorite; 1 star = not favorite.
- Store favorites in SQLite and write Windows `System.Rating` to supported source files (4 stars for favorite, 1 star for removed; existing 5 stars are preserved). Unwritable video or read-only files keep a pending SQLite override.
- Multiple local, removable and SMB/UNC roots, surviving offline shares.
- Advanced video playback, eventually including subtitles and audio-track selection.
- Scalable performance target: initial real-world library 158,267 photos + 43,438 videos (201,705 total).

## Current implementation

The \`FluentMediaGallery.Core\` project implements a SQLite-backed persistent media catalogue, streaming directory scans, safe offline/error handling, database favorites overrides, and read-only Windows Shell \`System.Rating\` import. Ratings import is a separate background operation so the initial discovery isn't blocked on reading every file's metadata. A Windows-only smoke test covers scan/restart/offline/favorites semantics.

See [architecture](docs/ARCHITECTURE.md) and [development guidance](AGENTS.md).

## Build (Windows, .NET 10 SDK)

\`\`\`powershell
dotnet restore .\src\FluentMediaGallery.Core\FluentMediaGallery.Core.csproj
dotnet build .\src\FluentMediaGallery.Core\FluentMediaGallery.Core.csproj -c Release
dotnet run --project .\tests\FluentMediaGallery.Core.Smoke\FluentMediaGallery.Core.Smoke.csproj -c Release
\`\`\`

The WinUI 3 shell, thumbnail generator, background watchers and video player are future development steps. No signed installer or production release exists yet.

## Actions and local verification

Push/pull CI has been removed to conserve GitHub Actions minutes. Run the smoke test locally, and reserve workflows for future release building and assets.

The metadata writer depends on the Windows property handler for a file type. Some video formats are read-only and a successful Shell write cannot be assumed. An app-local override and pending-write error remain in SQLite until the media can be synchronized. Test against **copies** of representative files before enabling metadata writes on a large library.

## Security and privacy

Media and metadata stay on the user's computer. The database is stored under \`%LOCALAPPDATA%\FluentMediaGallery\`. Scans use local filesystem access or credentials already granted by Windows for SMB. No telemetry or cloud account is required. File-rating persistence requires write access to selected files; nonwritable media stay indexed and can still be marked as favorites in SQLite.

## Development branches

- \`develop\`: ongoing work, direct commits.
- \`main\`: verified release commits.

MIT license applies to this repository's code. Any embedded video decoders/codecs will need their own license notices.
