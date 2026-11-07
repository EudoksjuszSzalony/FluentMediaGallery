# FluentMediaGallery — development rules

- Work on a single \`develop\` branch; \`main\` is for tested releases only. Do not create feature branches without explicit instruction.
- Protect user media. Never write metadata or delete originals by default. Favorites are stored in the application's SQLite database.
- Import historical Microsoft Photos favorite conventions from Windows System.Rating: 4 stars (63–87, normally 75) = favorite; 1 star (1–12, normally 1) = not favorite. Ratings 2/3/5 are not automatically favorites.
- Do not treat an offline SMB share, permissions error, watcher overflow, incomplete scan or cancellation as deletion. Mark absent files only following a complete error-free scan.
- Database and thumbnail cache belong in %LOCALAPPDATA%/FluentMediaGallery, NEVER in this public repository.
- The startup path must not synchronously enumerate source folders. Read the persistent index and reconcile in the background.
- Keep per-source scan state and separate metadata work from listing operations.
- Treat network drives and UNC paths as first-class sources.
- Preserve UI responsiveness and avoid loading all 200,000+ media entries into memory.
- Prefer tests for database migrations, missing/offline safety, rating policy, canceled scans and rescans before a main promotion.
- Do not claim GUI/video support unless built and tested; label developmental milestones accurately.
