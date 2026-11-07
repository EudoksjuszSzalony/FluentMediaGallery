# FluentMediaGallery

Open-source Windows 11 photo and video gallery planned in WinUI 3. Optimized for large libraries, multiple local directories and user-selected SMB/UNC shares.

> **Status: early development.** A functional Core library exists; the Windows GUI, media player, thumbnail cache and installer are not implemented yet. Do not treat this as a production release.

## Product goals

- Fluent Windows 11-style grid, photo/video viewer, bottom filmstrip and visible favorite-heart control.
- Favorites for photos **and videos**, read from Windows `System.Rating`: 4 or 5 stars = favorite; 1 star = not favorite.
- User favorite changes attempt to write back 4 stars (preserving existing 5) or 1 star, with read-back verification. SQLite retains pending writes/errors for unsupported formats.
- SQLite-backed persistent catalogue, rapid startup, virtualized paging, incremental/offline-safe reconciliation and bounded thumbnail cache.
- User-selected local/removable/UNC-SMB roots only; never auto-import every share or Windows Photos library.
- Video playback with seeking, subtitles, audio tracks, fullscreen (future LibVLCSharp.WinUI).
- Scale baseline: **158,267 photos + 43,438 videos = 201,705 files**; design target ≥1 million.

## Source and project guidance

- [HANDOFF.md](HANDOFF.md) — **living record** of decisions, completed/untested work, bugs, test evidence and next steps; mandatory at the start of a new chat.
- [AGENTS.md](AGENTS.md) — repository and safety rules.
- [docs/PROJECT_CUSTOM_INSTRUCTIONS.md](docs/PROJECT_CUSTOM_INSTRUCTIONS.md) — copyable project-level ChatGPT instructions.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — roadmap and design constraints.
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) — Dominator workflow and cleanup policy.
- [scripts/Cleanup-DevelopmentArtifacts.ps1](scripts/Cleanup-DevelopmentArtifacts.ps1) — **preview-first** optional cleanup of obsolete bin/obj/TestResults folders only.

## Current implementation

`FluentMediaGallery.Core` targets .NET 10 Windows and contains SQLite WAL with migration, streaming folder indexing, offline safety, read/write Windows Property System `System.Rating`, favorites state with pending retries, and a Windows Core smoke test. A user-run earlier test at commit `595b5e44` passed the build and all smoke checks. The former `NU1903` dependency warning prompted an update to `Microsoft.Data.Sqlite 10.0.14`, which **still needs local retesting** on the Dominator.

## Local build and verification

On **Dominator** with .NET 10 SDK, run in **PowerShell as USER** (not administrator), from repository root:

```powershell
& {
    $ErrorActionPreference = 'Stop'
    dotnet build .\src\FluentMediaGallery.Core\FluentMediaGallery.Core.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core build failed' }
    dotnet run --project .\tests\FluentMediaGallery.Core.Smoke\FluentMediaGallery.Core.Smoke.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core smoke tests failed' }
}
```

GitHub Actions **do not run on routine pushes or PRs**. Reserve Actions for explicitly approved releases and assets. Any metadata-write experiments should use **copies** of real media files, never the live collection.

## Privacy and license

Media, SQLite index and thumbnail cache belong outside the Git repository, under `%LOCALAPPDATA%\FluentMediaGallery`. No cloud account or telemetry is required. Original file metadata is only modified by an explicit favorite toggle, if the Windows file handler permits it. MIT license applies to source; future codecs need their own license notices.
