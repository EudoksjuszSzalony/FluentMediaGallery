# FluentMediaGallery — development rules

- Work on a single \`develop\` branch; \`main\` is for tested releases only. Do not create feature branches without explicit instruction.
- Protect user media. Never delete originals. User-requested favorite toggles attempt to persist System.Rating in the source file (75 favorite; preserve existing 99; 1 unfavorite). A failed read/write must keep the SQLite override pending and report the error. Do not change metadata while scanning or without a user favorite action.
- Import historical Microsoft Photos favorite conventions from Windows System.Rating: 4 or 5 stars (63–99, normally 75/99) = favorite; 1 star (1–12, normally 1) = not favorite. Ratings 2/3 are not automatically favorites.
- Do not treat an offline SMB share, permissions error, watcher overflow, incomplete scan or cancellation as deletion. Mark absent files only following a complete error-free scan.
- Database and thumbnail cache belong in %LOCALAPPDATA%/FluentMediaGallery, NEVER in this public repository.
- The startup path must not synchronously enumerate source folders. Read the persistent index and reconcile in the background.
- Keep per-source scan state and separate metadata work from listing operations.
- Treat network drives and UNC paths as first-class sources.
- Preserve UI responsiveness and avoid loading all 200,000+ media entries into memory.
- No automatic push/PR GitHub Actions. Reserve workflows for release assets; test locally.
- Register only explicitly selected source folders, never every available shared library by default.
- Prefer tests for database migrations, missing/offline safety, rating policy, canceled scans and rescans before a main promotion.
- Do not claim GUI/video support unless built and tested; label developmental milestones accurately.
