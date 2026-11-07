# FluentMediaGallery — AGENTS.md

Start each chat by reading the current develop branch, HANDOFF.md, and relevant source code. Update HANDOFF.md after every substantial implementation batch with decisions, tests, remaining work and known issues.

## Development workflow
- Public repo: https://github.com/EudoksjuszSzalony/FluentMediaGallery
- Use one develop branch for all normal commits; main only for validated stable releases. Avoid feature branch sprawl.
- GitHub Actions are reserved for future releases and assets. Do not enable push/PR workflows or consume routine CI minutes.
- Main machine is user's Dominator Windows PC. Remote Desktop Commander/RCU-OPSL is auxiliary only; VM output does not verify Dominator functionality.
- Make real integrated progress; do not claim a UI, video player or hardware verification exists without evidence.

## PowerShell and tests
- Label EVERY user PowerShell script USER or ADMINISTRATOR. Default USER.
- Each script must be one "& { ... }" block with ErrorActionPreference Stop and checked native exit codes. Stop on first failure, never proceed into a destructive later stage.
- Prefer local builds/tests on Dominator with concise feedback rather than long/irrelevant remote verification.
- After dependency updates run restore, dependency vulnerability audit, build, smoke tests. Do not silence NU1903.

## User media and favorites
- Register only user-selected folders. Support local and UNC/SMB; don't import shared folders automatically.
- Do not delete originals, media backups, DB, cache of retained value or settings.
- System.Rating: 4/5 stars (63–99) = favorite; 1 star (1–12) = not favorite; 2/3 not favorite.
- User favorite toggle may write file metadata: 75 for favorite, preserve existing 99, 1 for removal; verify by re-reading. Keep SQLite favorite override and pending/error state if file write fails.
- Do not mutate metadata during indexing. Test ratings on COPIES before enabling for personal media, including videos.
- Incomplete scans, offline shares and watcher overflows MUST NOT wipe indexed items or favorites.
- Implement persistent SQLite catalogue, virtualization, lazy thumbnails, fast startup, and incremental/safe SMB reconciliation for >200k files.

## Cleanup
- Periodically identify and clean provably superseded generated folders; review paths before removing anything.
- Use preview-first scripts/Cleanup-DevelopmentArtifacts.ps1, and explicit -Apply only after review. Keep .git, source, user files, DB, media, backups, release artifacts.
- Don't delete bin/obj routinely if incremental builds would benefit from reusing them.
