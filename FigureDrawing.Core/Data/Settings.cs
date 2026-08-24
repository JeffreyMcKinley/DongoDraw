using LiteDB;

namespace FigureDrawing.Data
{
    // The persisted preferences, and the single-document store that reads and writes them
    // (docs/DOMAIN-MODEL.md §5.1). One object: there is exactly one document, in one collection,
    // with one store — splitting the two bought a second name and no second implementation.
    //
    // Settings seed, they do not control (INV-SET-P3): values are copied into the setup screen on
    // launch and into intent extras on Start. Neither a session nor the setup logic reads them at
    // runtime. Losing the file costs preferences and nothing else (INV-SET-P6).
    //
    // Storage vocabulary stops here (INV-STO-4): nothing above this type speaks BSON, collections,
    // or LiteDB. The database handle is a private field, so LiteDB's mapper — which maps public
    // properties — never sees it.
    public sealed class Settings : IDisposable
    {
        const int DocumentId = 1;
        const string CollectionName = "settings";

        LiteDatabase? database;
        ILiteCollection<Settings>? documents;

        // Public and parameterless for LiteDB's mapper. Application code calls Open.
        public Settings()
        {
        }

        // Opens the database (creating it on first launch) and returns the persisted settings,
        // inserting a default document the first time. The caller owns the returned instance and
        // disposes it with the screen that opened it (INV-STO-3).
        public static Settings Open(string databasePath)
        {
            ArgumentNullException.ThrowIfNull(databasePath);

            try
            {
                return Read(databasePath);
            }
            catch (Exception) when (File.Exists(databasePath))
            {
                Discarded = true;
                // Losing the database costs preferences and nothing else (INV-SET-P6). A file left
                // corrupt by a kill mid-write would otherwise fail every launch from here on, so it
                // is discarded and reopened from defaults rather than thrown at the screen.
                Discard(databasePath);
                return Read(databasePath);
            }
        }

        // Delete the datafile AND the write-ahead log beside it. LiteDB writes through
        // "<name>-log<ext>" and only folds it back on checkpoint, so a log left by a process kill
        // outlives the datafile it belongs to. Deleting one without the other lets a stale — or, on
        // a restored device, a foreign — log be recovered into the fresh database, which is how a
        // document nobody in this app wrote ends up being deserialized (INV-STO-1: this type is the
        // only one that may touch these files).
        static void Discard(string databasePath)
        {
            File.Delete(databasePath);

            var log = Path.Combine(
                Path.GetDirectoryName(databasePath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(databasePath) + "-log" +
                Path.GetExtension(databasePath));

            if (File.Exists(log))
                File.Delete(log);
        }

        static Settings Read(string databasePath)
        {
            // Sole owner of the database (INV-STO-1) — no other type opens a LiteDatabase.
            var database = new LiteDatabase(databasePath);

            try
            {
                var documents = database.GetCollection<Settings>(CollectionName);

                var current = documents.FindById(DocumentId);
                if (current is null)
                {
                    current = new Settings { Id = DocumentId };
                    documents.Insert(current);
                }

                current.database = database;
                current.documents = documents;
                return current;
            }
            catch
            {
                // Nothing owns the handle yet, so a failure here would leave the file locked for the
                // life of the process — including for the retry above.
                database.Dispose();
                throw;
            }
        }

        // Persists the current values (insert or update). Called at named moments only — a folder
        // was picked and loaded successfully, Start was pressed, a settings toggle was flipped, a
        // break preset was tapped, the screen was left — never on every keystroke (INV-SET-P4).
        //
        // The write is forced all the way into the datafile before returning (INV-STO-5). LiteDB is
        // write-ahead logged: an upsert alone leaves the new value in "<name>-log<ext>" until a
        // checkpoint folds it in, and a process killed before that — the artist swiping the app off
        // the recents list, or the system reclaiming it — can leave that log truncated mid-page. A
        // truncated log does not fail to open; it reads back as the values from BEFORE the save, so
        // the folder the artist picked simply reverts with nothing logged and nothing thrown. The
        // checkpoint is what makes a save that returned a save that happened.
        public void Save()
        {
            // A silently dropped save would cost the user their preferences, so a save against a
            // disposed (or mapper-materialized) instance is an error rather than a no-op.
            ObjectDisposedException.ThrowIf(documents is null, this);

            // Nothing changed since the last write, so there is nothing to make durable. This is
            // what keeps the write-on-leaving-the-screen backstop free on an ordinary pause.
            if (!dirty)
                return;

            // The store owns the document identity; callers never set Id (INV-STO-2).
            Id = DocumentId;
            documents.Upsert(this);
            database?.Checkpoint();
            dirty = false;
        }

        // Whether anything has been assigned since the last successful write. Private, so LiteDB's
        // mapper — which maps public properties — never sees it, exactly like the database handle.
        bool dirty;

        T Set<T>(ref T field, T value)
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                dirty = true;
            }

            return field;
        }

        public void Dispose()
        {
            database?.Dispose();
            database = null;
            documents = null;
        }

        // Whether the last Open threw away an unreadable database and started from defaults
        // (INV-SET-P6). Static because it describes the file, not the document, and the document it
        // would otherwise live on is the one that was discarded. The screen logs it: a preference
        // set that resets itself is otherwise invisible from a bug report.
        public static bool Discarded { get; private set; }

        // Exactly one document, forever (INV-SET-P1).
        [BsonId]
        public int Id { get; set; }

        // Every property below has a default, so a first run and a missing field behave identically
        // and a new preference is a new defaulted property rather than a migration (INV-SET-P2).

        // How long each pose/reference image is shown, in seconds.
        public int PoseDurationSeconds
        {
            get => poseDurationSeconds;
            set => Set(ref poseDurationSeconds, value);
        }

        int poseDurationSeconds = 30;

        // How many images a session shows in total (FD-002 session setup). Persisted so the count
        // chosen last time seeds the setup screen on the next launch.
        public int SessionImageCount
        {
            get => sessionImageCount;
            set => Set(ref sessionImageCount, value);
        }

        int sessionImageCount = 20;

        // Rest between poses, in seconds. 0 runs one pose straight into the next.
        public int BreakSeconds
        {
            get => breakSeconds;
            set => Set(ref breakSeconds, value);
        }

        int breakSeconds = 0;

        // Whether to shuffle the selected images rather than show them in order.
        public bool ShuffleImages
        {
            get => shuffleImages;
            set => Set(ref shuffleImages, value);
        }

        bool shuffleImages = true;

        // Render reference images in grayscale (useful for value studies).
        public bool GrayscaleMode
        {
            get => grayscaleMode;
            set => Set(ref grayscaleMode, value);
        }

        bool grayscaleMode = false;

        // Hold the screen on for the whole session so a long pose can't put the device to sleep.
        public bool KeepScreenAwake
        {
            get => keepScreenAwake;
            set => Set(ref keepScreenAwake, value);
        }

        bool keepScreenAwake = true;

        // Play a short tone when the pose changes, for drawing away from the screen.
        public bool ChimeOnChange
        {
            get => chimeOnChange;
            set => Set(ref chimeOnChange, value);
        }

        bool chimeOnChange = false;

        // The reference library last opened, held by reference and never by its contents
        // (INV-SET-P5, INV-GRP-1). A stale one is expected: the permission may since have been
        // revoked (INV-GRP-5).
        public string? LastCollection
        {
            get => lastCollection;
            set => Set(ref lastCollection, value);
        }

        string? lastCollection;
    }
}
