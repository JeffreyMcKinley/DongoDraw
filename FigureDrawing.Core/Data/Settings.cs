using LiteDB;

namespace FigureDrawing.Data
{
    public sealed class Settings : IDisposable
    {
        const int DocumentId = 1;
        const string CollectionName = "settings";

        LiteDatabase? database;
        ILiteCollection<Settings>? documents;

        // For LiteDB's mapper, which needs a public parameterless constructor and maps public
        // properties only — which is also why database, documents and dirty are private fields.
        public Settings()
        {
        }

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
                Discard(databasePath);
                return Read(databasePath);
            }
        }

        // The log too, not just the datafile: LiteDB folds "<name>-log<ext>" back in on open, so a
        // log outliving its datafile is recovered into the fresh one (INV-SET-P6).
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
                // Unowned handle: without this the file stays locked for the process, and Open's
                // retry — which reopens this same path — fails too.
                database.Dispose();
                throw;
            }
        }

        public void Save()
        {
            ObjectDisposedException.ThrowIf(documents is null, this);

            if (!dirty)
                return;

            Id = DocumentId;
            documents.Upsert(this);
            database?.Checkpoint();
            dirty = false;
        }

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

        public static bool Discarded { get; private set; }

        [BsonId]
        public int Id { get; set; }

        public int PoseDurationSeconds
        {
            get => poseDurationSeconds;
            set => Set(ref poseDurationSeconds, value);
        }

        int poseDurationSeconds = 30;

        public int SessionImageCount
        {
            get => sessionImageCount;
            set => Set(ref sessionImageCount, value);
        }

        int sessionImageCount = 20;

        public int BreakSeconds
        {
            get => breakSeconds;
            set => Set(ref breakSeconds, value);
        }

        int breakSeconds = 0;

        public bool ShuffleImages
        {
            get => shuffleImages;
            set => Set(ref shuffleImages, value);
        }

        bool shuffleImages = true;

        public bool GrayscaleMode
        {
            get => grayscaleMode;
            set => Set(ref grayscaleMode, value);
        }

        bool grayscaleMode = false;

        public bool KeepScreenAwake
        {
            get => keepScreenAwake;
            set => Set(ref keepScreenAwake, value);
        }

        bool keepScreenAwake = true;

        public bool ChimeOnChange
        {
            get => chimeOnChange;
            set => Set(ref chimeOnChange, value);
        }

        bool chimeOnChange = false;

        public string? LastCollection
        {
            get => lastCollection;
            set => Set(ref lastCollection, value);
        }

        string? lastCollection;
    }
}
