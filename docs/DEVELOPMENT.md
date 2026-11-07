# Development on Dominator

- Default terminal: **PowerShell as USER**, not Administrator.
- Git checkout: %USERPROFILE%\Documents\FluentMediaGallery
- Current Windows: .NET 10 SDK and Git for Windows installed.
- Remote Desktop Commander supports work on a different computer; tests on that VM do not verify the main machine.
- GitHub Actions are reserved for releases and installer assets, not normal commits.

## Local verification

After pulling develop, validate restore, compile, vulnerable dependencies, and smoke tests. Label every script **USER** or **ADMINISTRATOR**, wrap all commands in one "& { ... }" block and stop after the first non-zero native exit code.

## Cleanup

Scripts/Cleanup-DevelopmentArtifacts.ps1 lists only disposable per-project bin, obj, TestResults directories under src/ and tests/. Default is **preview only**. Run -Apply explicitly only if you have reviewed the list. Do not run while a build or test process is executing.

Clean artifacts only when genuinely obsolete or space is needed. Repeatedly deleting obj/bin harms incremental build speed. This script never touches media, database, thumbnail cache, backups, source code, Git history or release assets.
