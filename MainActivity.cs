using System.IO;
using Android.Content;
using Android.Database;
using Android.Provider;
using Android.Util;
using Android.Views;
using Android.Widget;
using FigureDrawing.Core;
using FigureDrawing.Data;

// Android.Provider also has a Settings; the alias keeps the domain one unambiguous here.
using Settings = FigureDrawing.Data.Settings;

namespace FigureDrawing
{
    // The three tabbed screens of the Claude Design mock in one Activity: Session (setup), Images
    // (the reference library and the folder picker) and Settings. They share the settings document
    // and the loaded pool, so they are panes rather than separate screens (activity_main.xml).
    //
    // Everything with a rule behind it lives in Core: a draft DrawingSession validates the inputs,
    // gates Start and estimates the session's length; ReferenceLibrary walks the picked tree and owns
    // the pool; Settings persists the preferences. This class finds views, reflects that state, and
    // forwards taps.
    [Activity(Label = "@string/app_name", MainLauncher = true)]
    public class MainActivity : Activity
    {
        const int PickFolderRequestCode = 1000;
        const string LogTag = "FigureDrawing";
        const string DatabaseFileName = "figuredrawing.db";

        // Bound on each decoded reference thumbnail (px). The grid is a preview, not the pose, so it
        // decodes far smaller than the player's 1080px.
        const int ThumbnailDimension = 360;

        // Memory ceiling for a thumbnail's long side (px). 360 is the crop floor — a tile is
        // center-cropped, so decoding below it would upscale — and this is the bound that holds
        // whatever the aspect ratio is. Twice the floor, not the pose's 1080: power-of-two sampling
        // leaves up to 2x slop either way, and at 1080 a 2000x700 scan would still decode at full
        // size (5.6 MB) into a 360 px tile, times 24 tiles. At 720 the same file samples to
        // 1000x350 — a 3% upscale inside a CenterCrop tile, and a quarter of the heap.
        const int MaxThumbnailDimension = 720;

        // How many thumbnails the grid renders. A folder can hold thousands of photos; decoding all
        // of them would exhaust memory long before the drawer scrolled to them. The pool itself is
        // never truncated - every image found is still in the session - only the preview is.
        const int MaxThumbnails = 24;

        // What may cross to the player in the start intent, bounded two ways because a count alone
        // does not bound the size. Extras travel through a ~1 MB per-process Binder buffer; a SAF
        // document id carries the whole relative path and is parcelled as UTF-16, so a deep tree
        // with long filenames runs 200-300 characters — 500+ KB — per thousand ids, while a shallow
        // one runs a fifth of that. Unbounded, a DCIM-sized library throws
        // TransactionTooLargeException, uncaught and reproducing on every launch because the folder
        // is persisted. Past either bound the library hands over a random sample of itself
        // (INV-POOL-6); 1000 images is far more variety than a session of a few hundred poses can
        // consume, and 128k characters is ~256 KB parcelled, a quarter of the buffer.
        const int MaxPoolHandoff = 1000;
        const int MaxPoolHandoffChars = 128_000;

        // --- Panes and tabs ---
        View paneSetup = null!;
        View paneLibrary = null!;
        View paneSettings = null!;
        View tabSession = null!;
        View tabImages = null!;
        View tabSettings = null!;

        // --- Library ---
        GridLayout imageContainer = null!;
        TextView emptyLabel = null!;
        TextView libraryCount = null!;
        TextView libraryMore = null!;

        // The picked folder and every image found beneath it, in enumeration order. Its pool is what
        // is handed to the session when Start is tapped.
        ReferenceLibrary library = ReferenceLibrary.Empty;

        // --- Setup ---
        EditText secondsInput = null!;
        EditText countInput = null!;
        Button startButton = null!;
        TextView poolLabel = null!;
        TextView estimateLabel = null!;
        readonly Dictionary<int, Button> secondsChips = new();
        readonly Dictionary<int, Button> breakChips = new();

        // --- Settings ---
        Button shuffleToggle = null!;
        Button awakeToggle = null!;
        Button chimeToggle = null!;
        Button grayscaleToggle = null!;

        Settings settings = null!;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            SetContentView(Resource.Layout.activity_main);

            // Open the local settings/config database (created on first launch) from the app's
            // private files directory, then load the persisted settings.
            var databasePath = Path.Combine(FilesDir!.AbsolutePath, DatabaseFileName);
            settings = Settings.Open(databasePath);
            Log.Info(LogTag,
                $"Settings loaded from {databasePath}: " +
                $"poseDuration={settings.PoseDurationSeconds}s, break={settings.BreakSeconds}s, " +
                $"shuffle={settings.ShuffleImages}, grayscale={settings.GrayscaleMode}");

            BindPanes();
            BindLibrary();
            BindSetup();
            BindSettings();

            ShowPane(paneSetup, tabSession);

            // Restore the folder chosen on a previous launch. A revoked permission (folder deleted,
            // permission cleared, the platform trimming the grant) keeps the reference and says so
            // on screen instead — the choice is still known, only the access is gone.
            RestoreLastFolder();
        }

        protected override void OnDestroy()
        {
            // ClearThumbnails guards its own fields — OnCreate can throw before BindLibrary runs.
            ClearThumbnails();

            settings?.Dispose();
            base.OnDestroy();
        }

        // Coming back from a finished session: the pool and inputs are unchanged, but the session may
        // have been started with values the summary screen let the drawer revisit.
        protected override void OnResume()
        {
            base.OnResume();
            UpdateStartState();
        }

        // Leaving the screen is a write moment (INV-SET-P4). Everything here is already saved where
        // it changed, so this is the backstop for the way apps actually end: swiped off the recents
        // list, or reclaimed while backgrounded. Neither runs OnDestroy, so a value that has only
        // reached memory by then is a value the artist loses.
        protected override void OnPause()
        {
            base.OnPause();

            // The typed inputs are the only values that live nowhere but the screen — every other
            // preference is written where it is flipped. Without this the write below would have
            // nothing new to persist and the backstop would be decorative.
            CaptureTypedInputs();
            SaveSettings();
        }

        // Copies the setup inputs into the settings document when they parse. A half-typed number is
        // not a preference, so an unparseable draft leaves the stored value alone (INV-SET-2).
        void CaptureTypedInputs()
        {
            if (Draft().Config is not { } config)
                return;

            settings.PoseDurationSeconds = config.SecondsPerImage;
            settings.SessionImageCount = config.ImageCount;
        }

        // Writes the settings document, and never lets a failed write take the screen down with it:
        // losing a preference is survivable (INV-SET-P6), crashing on the way out is not.
        void SaveSettings()
        {
            try
            {
                // Null-conditional on purpose: OnCreate can throw before Settings.Open returns, and
                // a teardown that then fails on a null field would replace a visible failure with a
                // confusing one.
                settings?.Save();
            }
            catch (Exception error)
            {
                Log.Error(LogTag, $"Could not save settings: {error}");
            }
        }

        // --- Panes -----------------------------------------------------------

        void BindPanes()
        {
            paneSetup = FindViewById<View>(Resource.Id.pane_setup)!;
            paneLibrary = FindViewById<View>(Resource.Id.pane_library)!;
            paneSettings = FindViewById<View>(Resource.Id.pane_settings)!;

            tabSession = FindViewById<View>(Resource.Id.tab_session)!;
            tabImages = FindViewById<View>(Resource.Id.tab_images)!;
            tabSettings = FindViewById<View>(Resource.Id.tab_settings)!;

            tabSession.Click += (_, _) => ShowPane(paneSetup, tabSession);
            tabImages.Click += (_, _) => ShowPane(paneLibrary, tabImages);
            tabSettings.Click += (_, _) => ShowPane(paneSettings, tabSettings);
        }

        // Exactly one pane is visible; the matching tab carries the accent bar and label colour
        // (both inherit the tab's selected state through duplicateParentState).
        void ShowPane(View pane, View tab)
        {
            paneSetup.Visibility = pane == paneSetup ? ViewStates.Visible : ViewStates.Gone;
            paneLibrary.Visibility = pane == paneLibrary ? ViewStates.Visible : ViewStates.Gone;
            paneSettings.Visibility = pane == paneSettings ? ViewStates.Visible : ViewStates.Gone;

            tabSession.Selected = tab == tabSession;
            tabImages.Selected = tab == tabImages;
            tabSettings.Selected = tab == tabSettings;
        }

        // --- Session setup ---------------------------------------------------

        void BindSetup()
        {
            secondsInput = FindViewById<EditText>(Resource.Id.seconds_input)!;
            countInput = FindViewById<EditText>(Resource.Id.count_input)!;
            startButton = FindViewById<Button>(Resource.Id.start_button)!;
            poolLabel = FindViewById<TextView>(Resource.Id.pool_label)!;
            estimateLabel = FindViewById<TextView>(Resource.Id.estimate_label)!;

            // The chip rows are the presets from Core, in the order Core lists them, so the two can
            // never drift apart.
            BindPresetChips(secondsChips, SessionSetup.SecondsPresets, new[]
            {
                Resource.Id.chip_sec_30, Resource.Id.chip_sec_60,
                Resource.Id.chip_sec_120, Resource.Id.chip_sec_300
            }, OnSecondsPreset);

            BindPresetChips(breakChips, SessionSetup.BreakPresets, new[]
            {
                Resource.Id.chip_break_0, Resource.Id.chip_break_5,
                Resource.Id.chip_break_15, Resource.Id.chip_break_60
            }, OnBreakPreset);

            secondsInput.Text = settings.PoseDurationSeconds.ToString();
            countInput.Text = settings.SessionImageCount.ToString();

            secondsInput.TextChanged += (_, _) => UpdateStartState();
            countInput.TextChanged += (_, _) => UpdateStartState();
            startButton.Click += (_, _) => StartSession();

            // "Change" on the pool card is a shortcut to the folder picker's own screen.
            FindViewById<Button>(Resource.Id.change_button)!.Click +=
                (_, _) => ShowPane(paneLibrary, tabImages);

            UpdateStartState();
        }

        // Wires one chip row to one list of Core presets. The chips carry no value of their own —
        // they render presets[i] and hand it back — so a change to the preset list moves both ends.
        void BindPresetChips(
            Dictionary<int, Button> chips, IReadOnlyList<int> presets, int[] viewIds, Action<int> onPick)
        {
            for (var i = 0; i < presets.Count && i < viewIds.Length; i++)
            {
                var value = presets[i];
                var chip = FindViewById<Button>(viewIds[i])!;
                chip.Click += (_, _) => onPick(value);
                chips[value] = chip;
            }
        }

        void OnSecondsPreset(int seconds)
        {
            secondsInput.Text = seconds.ToString();
            secondsInput.SetSelection(secondsInput.Text!.Length);
        }

        void OnBreakPreset(int breakSeconds)
        {
            settings.BreakSeconds = breakSeconds;
            settings.Save();
            UpdateStartState();
        }

        // Recomputes whether the session may start and reflects it across the setup pane: the Start
        // gate, which preset chips read as chosen, the pool card and the length estimate. Pure logic
        // (parsing, validation, the estimate) lives in the draft session this evaluates.
        void UpdateStartState()
        {
            var draft = Draft();

            startButton.Enabled = draft.CanStart;

            foreach (var (value, chip) in secondsChips)
                chip.Selected = draft.SecondsPerImage == value;

            foreach (var (value, chip) in breakChips)
                chip.Selected = draft.BreakSeconds == value;

            poolLabel.Text = library.Count > 0
                ? string.Format(GetString(Resource.String.pool_ready_format), library.Count)
                : GetString(Resource.String.pool_empty_text);

            estimateLabel.Text = string.Format(
                GetString(Resource.String.estimate_format),
                DrawingSession.Format(draft.EstimateSeconds));
        }

        // The setup pane's state: a session that has not started yet. Evaluated on every keystroke,
        // so it copies no pool and starts no clock.
        DrawingSession<Android.Graphics.Bitmap> Draft() =>
            DrawingSession<Android.Graphics.Bitmap>.Evaluate(
                secondsInput.Text, countInput.Text, !library.IsEmpty, settings.BreakSeconds);

        // FD-002 Start: persist the chosen values so they seed the next session, then hand the config
        // to the session engine (FD-003). Guarded on the same validation the button state uses.
        void StartSession()
        {
            if (Draft().Config is not { } config)
                return;

            settings.PoseDurationSeconds = config.SecondsPerImage;
            settings.SessionImageCount = config.ImageCount;
            settings.Save();

            Log.Info(LogTag,
                $"Session start: {config.SecondsPerImage}s/image, {config.ImageCount} images, " +
                $"{config.BreakSeconds}s break, {library.Count} in pool.");

            // FD-004: hand the pool + config to the session player screen. The preferences the player
            // needs travel as extras too — a screen never reads Settings on the far side (§16).
            // How wide the handoff needs to be is a function of the session's length, not of the
            // Binder buffer alone: the player cannot re-sample, so this is also what "Run it again"
            // will redraw from (INV-POOL-6). MaxPoolHandoff stays the ceiling.
            var bound = SessionSetup.HandoffBound(config.ImageCount, MaxPoolHandoff);
            var handoff = library.Sample(bound, MaxPoolHandoffChars);

            if (handoff.Count < library.Count)
                Log.Info(LogTag,
                    $"Pool of {library.Count} exceeds the {bound} handoff bound; " +
                    $"sampling {handoff.Count} for this session.");

            var intent = new Intent(this, typeof(SessionActivity));
            intent.PutExtra(SessionActivity.ExtraPool, handoff.ToArray());
            intent.PutExtra(SessionActivity.ExtraSeconds, config.SecondsPerImage);
            intent.PutExtra(SessionActivity.ExtraCount, config.ImageCount);
            intent.PutExtra(SessionActivity.ExtraBreak, config.BreakSeconds);
            intent.PutExtra(SessionActivity.ExtraShuffle, settings.ShuffleImages);
            intent.PutExtra(SessionActivity.ExtraGrayscale, settings.GrayscaleMode);
            intent.PutExtra(SessionActivity.ExtraKeepAwake, settings.KeepScreenAwake);
            intent.PutExtra(SessionActivity.ExtraChime, settings.ChimeOnChange);

            // Crossing to another screen is a system boundary: throwing is not a defined outcome
            // (§9, INV-X-11). The handoff bound should keep the extras well inside the Binder
            // buffer, but a device with a smaller one must show a message rather than take the
            // process down on every Start.
            try
            {
                StartActivity(intent);
            }
            catch (Exception ex)
            {
                Log.Error(LogTag, $"Starting the session failed: {ex}");
                poolLabel.Text = GetString(Resource.String.session_start_failed_text);
            }
        }

        // --- Settings --------------------------------------------------------

        void BindSettings()
        {
            shuffleToggle = FindViewById<Button>(Resource.Id.setting_shuffle)!;
            awakeToggle = FindViewById<Button>(Resource.Id.setting_awake)!;
            chimeToggle = FindViewById<Button>(Resource.Id.setting_chime)!;
            grayscaleToggle = FindViewById<Button>(Resource.Id.setting_grayscale)!;

            shuffleToggle.Click += (_, _) => Toggle(() => settings.ShuffleImages = !settings.ShuffleImages);
            awakeToggle.Click += (_, _) => Toggle(() => settings.KeepScreenAwake = !settings.KeepScreenAwake);
            chimeToggle.Click += (_, _) => Toggle(() => settings.ChimeOnChange = !settings.ChimeOnChange);
            grayscaleToggle.Click += (_, _) => Toggle(() => settings.GrayscaleMode = !settings.GrayscaleMode);

            RenderSettings();
        }

        // A preference change is saved the moment it is made — the Settings screen has no Save
        // button, so an unsaved toggle would silently be lost on back.
        void Toggle(Action change)
        {
            change();
            settings.Save();
            RenderSettings();
        }

        void RenderSettings()
        {
            RenderToggle(shuffleToggle, settings.ShuffleImages);
            RenderToggle(awakeToggle, settings.KeepScreenAwake);
            RenderToggle(chimeToggle, settings.ChimeOnChange);
            RenderToggle(grayscaleToggle, settings.GrayscaleMode);
        }

        void RenderToggle(Button toggle, bool on)
        {
            toggle.Selected = on;
            toggle.Text = GetString(on ? Resource.String.toggle_on_text : Resource.String.toggle_off_text);
        }

        // --- Reference library -----------------------------------------------

        void BindLibrary()
        {
            imageContainer = FindViewById<GridLayout>(Resource.Id.image_container)!;
            emptyLabel = FindViewById<TextView>(Resource.Id.empty_label)!;
            libraryCount = FindViewById<TextView>(Resource.Id.library_count)!;
            libraryMore = FindViewById<TextView>(Resource.Id.library_more)!;

            // Two columns folded, four with the fold open (values[-sw600dp]/integers.xml).
            imageContainer.ColumnCount = Resources!.GetInteger(Resource.Integer.library_columns);

            FindViewById<Button>(Resource.Id.pick_button)!.Click += (_, _) => PickFolder();
        }

        // Opens the system folder picker (Storage Access Framework). ACTION_OPEN_DOCUMENT_TREE
        // returns a tree content:// Uri granting access to the folder and everything under it.
        void PickFolder()
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);

            // Open the picker on the folder chosen last time, so reusing the same library is one tap
            // and picking its sibling starts next door rather than at the provider root. A hint
            // only: the picker is free to ignore it, and the drawer can still browse anywhere.
            if (LastPickedDocumentUri() is { } initial)
                intent.PutExtra(DocumentsContract.ExtraInitialUri, initial);

            // Launching the picker crosses the system boundary, so throwing is not a defined outcome
            // (§9, INV-X-11): an image with no documents provider, or one where the user disabled it,
            // must show a message rather than take the process down on a tap.
            try
            {
                StartActivityForResult(intent, PickFolderRequestCode);
            }
            catch (Exception error)
            {
                Log.Error(LogTag, $"Could not open the folder picker: {error}");
                emptyLabel.Text = GetString(Resource.String.folder_error_text);
                emptyLabel.Visibility = ViewStates.Visible;
            }
        }

        // The remembered library as a *document* uri, which is what the picker navigates to —
        // handed a bare tree uri it lands at the root of the provider instead. Null when there is
        // nothing usable to start from: a hint that cannot be built must leave the picker opening at
        // its default rather than failing to open at all.
        Android.Net.Uri? LastPickedDocumentUri()
        {
            if (RememberedTree() is not { } treeUri)
                return null;

            try
            {
                var documentId = DocumentsContract.GetTreeDocumentId(treeUri);
                return documentId is null
                    ? null
                    : DocumentsContract.BuildDocumentUriUsingTree(treeUri, documentId);
            }
            catch (Exception error)
            {
                // The reference itself is not logged: it carries the artist's own folder path, and
                // it is already recorded once where the folder was picked (§9).
                Log.Warn(LogTag, $"Could not build a picker hint: {error.Message}");
                return null;
            }
        }

        // The remembered library as a uri, when something usable is stored at all — a SAF tree
        // reference and not whatever else a settings file can end up holding (LibraryReference).
        //
        // Deliberately says nothing about permission: pointing the picker at a folder needs no grant,
        // and requiring one would turn a revoked permission into a second problem, sending the artist
        // back to the provider root to find a folder the app still knows the name of. Restoring the
        // library is the caller that has to check (RestoreLastFolder).
        Android.Net.Uri? RememberedTree()
        {
            if (!LibraryReference.TryParse(settings.LastCollection, out var reference))
                return null;

            return Android.Net.Uri.Parse(reference);
        }

        // The platform's persisted permissions, reduced to what the rules need. Materialised rather
        // than yielded: a lazy sequence would run the Binder call wherever it happened to be
        // enumerated, which is how a system-server failure escapes the try that was meant to contain
        // it. The UriPermission peers are freed here — the screen owns what it created (§8).
        IReadOnlyList<PersistedGrant> PersistedGrants()
        {
            var grants = new List<PersistedGrant>();

            foreach (var permission in ContentResolver!.PersistedUriPermissions)
            {
                using (permission)
                    grants.Add(new PersistedGrant(permission.Uri?.ToString(), permission.IsReadPermission));
            }

            return grants;
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            if (requestCode != PickFolderRequestCode || resultCode != Result.Ok)
                return;

            var treeUri = data?.Data;
            if (treeUri is null)
                return;

            // Handling a folder result must never crash the app. Persisting the grant, writing
            // settings, or enumerating the tree can each throw (SecurityException, provider quirks,
            // out-of-memory on large images); a failure here shows a message instead of dying.
            try
            {
                // Persist the read grant so the folder can be reused on the next launch. Pass the
                // read flag as a constant — deriving it from data.Flags is fragile: on some devices
                // the result intent reports no flags, yielding 0 and a SecurityException.
                ContentResolver!.TakePersistableUriPermission(treeUri, ActivityFlags.GrantReadUriPermission);

                // Grants accumulate against a per-package cap and the platform drops the OLDEST
                // past it, so a grant kept for a folder the artist has moved on from is a grant that
                // can cost them the one they still use. Re-picking the same folder releases nothing.
                ReleaseSupersededGrants(treeUri.ToString());

                settings.LastCollection = treeUri.ToString();
                settings.Save();

                Log.Info(LogTag, $"Folder selected: {treeUri}");
                LoadFolder(treeUri);
            }
            catch (Exception ex)
            {
                Log.Error(LogTag, $"Failed to open folder {treeUri}: {ex}");
                ResetLibrary();

                // After ResetLibrary, not before: RenderLibrary writes empty_folder_text for an
                // empty library, so the specific message has to land last or it is clobbered.
                emptyLabel.Text = GetString(Resource.String.folder_error_text);
                emptyLabel.Visibility = ViewStates.Visible;
            }
        }

        // Comes up with the library the artist last used already loaded, so a relaunch is not a
        // second trip to the picker. Three outcomes, and they are not the same thing to say: nothing
        // was ever picked (the first-run prompt stands), a folder is remembered but cannot be opened
        // (its own message, INV-GRP-5), or it loads.
        // Hands back every read grant this app holds for a folder that is no longer the remembered
        // one. Which those are is Core's rule; releasing them is the platform's API.
        void ReleaseSupersededGrants(string? keep)
        {
            foreach (var stale in LibraryReference.GrantsToRelease(keep, PersistedGrants()))
            {
                try
                {
                    if (Android.Net.Uri.Parse(stale) is { } uri)
                        ContentResolver!.ReleasePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
                }
                catch (Exception error)
                {
                    // A grant that cannot be released is not worth a visible failure: the cap is a
                    // ceiling, not a wall, and the folder just picked is already granted.
                    Log.Info(LogTag, $"Could not release a superseded folder grant: {error.Message}");
                }
            }
        }

        void RestoreLastFolder()
        {
            if (Settings.Discarded)
                Log.Warn(LogTag, "The settings database was unreadable and was reset; preferences start from defaults.");

            if (!LibraryReference.TryParse(settings.LastCollection, out var reference))
            {
                Log.Info(LogTag, "No folder remembered yet.");
                return;
            }

            var treeUri = Android.Net.Uri.Parse(reference);
            if (treeUri is null)
            {
                ShowRememberedFolderUnavailable();
                return;
            }

            try
            {
                // The reference outlives the permission: a grant can be cleared, dropped when its
                // volume unmounts, or trimmed by the platform, and none of that erases what the
                // artist picked. Say which of the two is missing rather than showing the first-run
                // state, which reads as the app having forgotten.
                if (!LibraryReference.HasReadGrant(reference, PersistedGrants()))
                {
                    Log.Warn(LogTag, "The remembered folder is still stored but its read grant is gone.");
                    ShowRememberedFolderUnavailable();
                    return;
                }

                // Before the walk, and in its own try: taking a grant already held is a no-op that
                // refreshes it, which keeps the folder in daily use off the platform's trim list —
                // but a refresh that fails must not throw away a library that then loads perfectly
                // well, and a walk that fails must not skip the refresh.
                RefreshGrant(treeUri);

                LoadFolder(treeUri);
                Log.Info(LogTag, $"Restored the remembered folder: {library.Count} images.");
            }
            catch (Exception error)
            {
                Log.Warn(LogTag, $"Restoring the remembered folder failed: {error.Message}");
                ShowRememberedFolderUnavailable();
            }
        }

        // Re-takes a grant the app already holds. Best effort by design: the grant can be trimmed
        // between the check above and this call, and losing the refresh costs nothing this launch.
        void RefreshGrant(Android.Net.Uri treeUri)
        {
            try
            {
                ContentResolver!.TakePersistableUriPermission(treeUri, ActivityFlags.GrantReadUriPermission);
            }
            catch (Exception error)
            {
                Log.Info(LogTag, $"Could not refresh the folder grant: {error.Message}");
            }
        }

        // The remembered-but-unreachable state. Distinct from a first run on purpose: the choice is
        // still known, the picker will still open there, and only the permission has to be given
        // again.
        void ShowRememberedFolderUnavailable()
        {
            walkFailed = true;

            try
            {
                ResetLibrary();
            }
            finally
            {
                walkFailed = false;
            }
        }

        // Set while the screen is rendering the aftermath of a folder that would not open, so the
        // classification below can tell "remembered and unreachable" from "picked and empty".
        bool walkFailed;

        // Builds the reference library for the picked tree (the recursive walk and the pool live in
        // Core) and shows what it found. Any entry whose MIME type starts with "image/" is accepted
        // (jpg/png/webp/gif/heic/...); the library maps each document id to the content uri a
        // session draws from, so the pool needs no second pass here.
        void LoadFolder(Android.Net.Uri treeUri)
        {
            ClearThumbnails();
            library = ReferenceLibrary.Empty;

            var rootDocumentId = DocumentsContract.GetTreeDocumentId(treeUri);
            if (rootDocumentId is not null)
            {
                var tree = new ContentResolverDocumentTree(ContentResolver!, treeUri);
                library = new ReferenceLibrary(
                    tree,
                    rootDocumentId,
                    treeUri.LastPathSegment,
                    documentId =>
                        DocumentsContract.BuildDocumentUriUsingTree(treeUri, documentId)?.ToString());

                // Every image found is in the pool regardless of whether its preview decodes; the
                // session handles any that turn out unreadable. The cap bounds decode ATTEMPTS, not
                // successes — a folder of undecodable files must not cost one provider round trip
                // per entry on the main thread.
                foreach (var id in library.Pool.Take(MaxThumbnails))
                {
                    if (Android.Net.Uri.Parse(id) is { } fileUri)
                        AddThumbnail(fileUri);
                }
            }

            RenderLibrary();

            // A session needs images to run, so the Start gate opens only when the folder yielded at
            // least one image (FD-002).
            UpdateStartState();
        }

        // What the pane says is decided in Core (LibraryStatus) and only mapped to a resource here:
        // "never picked", "remembered but unreachable" and "picked and empty" are three different
        // things to tell the artist, and choosing between them from `library.Count` alone is how the
        // last two ended up indistinguishable.
        void RenderLibrary()
        {
            var status = LibraryReference.Classify(
                settings.LastCollection, PersistedGrants(), library.Count, walkFailed);

            if (status == LibraryStatus.Ready)
            {
                emptyLabel.Visibility = ViewStates.Gone;
                libraryCount.Text =
                    string.Format(GetString(Resource.String.pool_ready_format), library.Count);
            }
            else
            {
                emptyLabel.Text = GetString(status switch
                {
                    LibraryStatus.Unavailable => Resource.String.folder_unavailable_text,
                    LibraryStatus.Empty => Resource.String.empty_folder_text,
                    _ => Resource.String.empty_label_text,
                });

                emptyLabel.Visibility = ViewStates.Visible;
                libraryCount.Text = GetString(Resource.String.pool_empty_text);
            }

            // Be explicit that the grid is a sample of a bigger pool rather than the whole of it.
            var hidden = library.Count - imageContainer.ChildCount;
            if (hidden > 0)
            {
                libraryMore.Text = string.Format(GetString(Resource.String.library_more_format), hidden);
                libraryMore.Visibility = ViewStates.Visible;
            }
            else
            {
                libraryMore.Visibility = ViewStates.Gone;
            }
        }

        // The grid owns its decoded previews: nothing else holds them (a session re-decodes from the
        // uri), so detach the drawable, then free the pixels and the peer before dropping the views.
        // Capture the bitmap before detaching — a second lookup afterwards can return a different
        // peer.
        void ClearThumbnails()
        {
            // Both fields are `null!` until BindLibrary runs, and OnCreate can throw before it does.
            if (imageContainer is null || libraryMore is null)
                return;

            for (var i = imageContainer.ChildCount - 1; i >= 0; i--)
            {
                if (imageContainer.GetChildAt(i) is not ImageView view)
                    continue;

                // The drawable is a managed peer of its own; disposing it with the bitmap keeps the
                // JNI global ref from outliving the pixels it wrapped.
                using var drawable = view.Drawable as Android.Graphics.Drawables.BitmapDrawable;
                var bitmap = drawable?.Bitmap;
                view.SetImageDrawable(null);

                if (bitmap is not null && !bitmap.IsRecycled)
                    bitmap.Recycle();

                bitmap?.Dispose();
            }

            imageContainer.RemoveAllViews();
            libraryMore.Visibility = ViewStates.Gone;
        }

        // Back to the no-folder state, rendered whole: grid, count, empty label, pool card and the
        // Start gate all describe the same (empty) pool. Without this a failed open leaves a blank
        // grid under a header still reporting the previous folder's count, with Start still open on
        // a pool the drawer was just told is gone.
        void ResetLibrary()
        {
            ClearThumbnails();
            library = ReferenceLibrary.Empty;
            RenderLibrary();
            UpdateStartState();
        }

        void AddThumbnail(Android.Net.Uri uri)
        {
            Android.Graphics.Bitmap? bitmap;
            try
            {
                bitmap = ImageDecoding.DecodeSampledBitmap(
                    ContentResolver!, uri, ThumbnailDimension, MaxThumbnailDimension);
            }
            catch (Exception ex)
            {
                // A single unreadable/oversized image must not sink the whole folder.
                Log.Warn(LogTag, $"Skipping image {uri}: {ex.Message}");
                return;
            }

            if (bitmap is null)
                return;

            var margin = Resources!.GetDimensionPixelSize(Resource.Dimension.space_2);
            var layoutParams = new GridLayout.LayoutParams
            {
                Width = 0,
                Height = Resources.GetDimensionPixelSize(Resource.Dimension.thumb_height),
                ColumnSpec = GridLayout.InvokeSpec(GridLayout.Undefined, 1f)
            };
            layoutParams.SetMargins(margin, margin, margin, margin);

            var imageView = new ImageView(this)
            {
                LayoutParameters = layoutParams,
                ContentDescription = GetString(Resource.String.thumbnail_desc)
            };
            imageView.SetScaleType(ImageView.ScaleType.CenterCrop);
            imageView.SetBackgroundResource(Resource.Drawable.bg_thumb);
            imageView.ClipToOutline = true;
            imageView.SetImageBitmap(bitmap);

            imageContainer.AddView(imageView);
        }

        // Adapts a Storage Access Framework tree (DocumentsContract + ContentResolver) to the
        // IDocumentTree abstraction the pure enumerator walks.
        sealed class ContentResolverDocumentTree(ContentResolver resolver, Android.Net.Uri treeUri)
            : IDocumentTree
        {
            // A failed query yields nothing rather than throwing: the provider may be gone, the
            // volume unmounted, or the grant revoked between the permission check and the walk, and
            // the domain treats "no children" as an ordinary answer (INV-TREE-4, INV-GRP-5).
            public IEnumerable<DocumentEntry> GetChildren(string parentDocumentId)
            {
                var entries = new List<DocumentEntry>();

                try
                {
                    var childrenUri =
                        DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, parentDocumentId);

                    ICursor? cursor = resolver.Query(
                        childrenUri!,
                        new[]
                        {
                            DocumentsContract.Document.ColumnDocumentId,
                            DocumentsContract.Document.ColumnMimeType,
                        },
                        null, null, null);

                    if (cursor is null)
                        return entries;

                    try
                    {
                        while (cursor.MoveToNext())
                        {
                            var documentId = cursor.GetString(0);
                            if (documentId is null)
                                continue;

                            entries.Add(new DocumentEntry(documentId, cursor.GetString(1)));
                        }
                    }
                    finally
                    {
                        cursor.Close();
                    }
                }
                catch (Exception error)
                {
                    Log.Warn(LogTag, $"Listing {parentDocumentId} failed: {error.Message}");
                }

                return entries;
            }
        }
    }
}
