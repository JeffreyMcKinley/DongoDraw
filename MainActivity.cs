using System.IO;
using Android.Content;
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
        // is handed to the session when Start is tapped. Only ever written on the main thread: the
        // loader builds a fresh ReferenceLibrary on its worker and this swaps it in whole, so a
        // reader can never see a half-built pool (INV-X-13).
        ReferenceLibrary library = ReferenceLibrary.Empty;

        // Reads a folder off the UI thread, and owns abandoning a load that has been superseded.
        LibraryLoader loader = null!;

        // The tree uri whose pool `library` holds, so a re-walk of the same folder can keep it.
        // Null until a folder has been loaded; cleared whenever the pool is.
        string? loadedTreeUri;

        // Whether OnStop released the grid and it needs rebuilding. OnStart fires immediately after
        // OnCreate, which has already started a load, so without this every cold start walks twice.
        bool gridReleased;

        // Whether the folder picker is open. The picker is a full-screen Activity in another
        // process, so it stops this screen — and its result arrives *after* OnStart, while
        // Settings.LastCollection still names the old folder. Without this, every pick starts a full
        // walk of the folder the artist is in the middle of replacing, which OnActivityResult then
        // supersedes: the exact work this ticket exists to stop doing.
        bool awaitingPickResult;

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

            // The application's resolver, not this Activity's: an Activity's ContentResolver reaches
            // back to the Activity through its ContextImpl, so a walk still running after OnDestroy
            // would pin the destroyed screen and its whole view tree.
            loader = new LibraryLoader(ApplicationContext!.ContentResolver!);

            BindPanes();
            BindLibrary();
            BindSetup();
            BindSettings();

            ShowPane(paneSetup, tabSession);

            // Restore the folder chosen on a previous launch, if the persisted URI permission
            // is still granted. A revoked permission (folder deleted, permission cleared) is
            // expected and simply leaves the empty state showing.
            RestoreLastFolder();
        }

        protected override void OnDestroy()
        {
            // Abandon before anything else is torn down: a load that lands afterwards would render
            // into a destroyed screen's views and keep this Activity's whole view tree reachable
            // from the worker until it finished. Null-guarded because OnCreate can throw before the
            // loader is assigned — Settings.Open runs first.
            loader?.Abandon();

            // ClearThumbnails guards its own fields — OnCreate can throw before BindLibrary runs.
            ClearThumbnails();

            settings?.Dispose();
            base.OnDestroy();
        }

        // Coming back into view. MainActivity is stopped rather than destroyed while a session runs,
        // so this is the return path from the player as well as from the launcher — and re-walking
        // here is what picks up images added to the folder in another app (INV-GRP-1).
        protected override void OnStart()
        {
            base.OnStart();

            // Skipped on the first OnStart after OnCreate: RestoreLastFolder has just run and a cold
            // start must not walk the tree twice. Only a grid OnStop released needs rebuilding.
            if (!gridReleased)
                return;

            gridReleased = false;

            // The pick's result lands next and starts the load itself — including the cancelled
            // case, which restores the folder that was already there.
            if (awaitingPickResult)
            {
                return;
            }

            // Handed the uri this already resolved, when it resolved one: RestoreLastFolder would
            // otherwise repeat the grant enumeration, a binder round trip, on a path that now runs
            // on every return. It owns the "nothing left to restore" case for both callers.
            RestoreLastFolder(RememberedTree());
        }

        // Released here, not in OnDestroy: MainActivity is merely *stopped* while SessionActivity
        // runs, so previews held to OnDestroy would sit under every session and the app's real peak
        // would be the grid plus the pose plus the pose being decoded. Affordable only because the
        // rebuild in OnStart is off the UI thread.
        //
        // This depends on SessionActivity being opaque and full-screen. Give it a translucent or
        // dialog theme and MainActivity never reaches Stopped, and this silently stops running.
        protected override void OnStop()
        {
            // Abandon before releasing: a load already past its guard would otherwise repopulate the
            // grid this just cleared, putting back the previews the release exists to remove.
            // Null-guarded for the same reason as OnDestroy — OnCreate can throw before the loader
            // is assigned, and the platform still runs the teardown.
            loader?.Abandon();
            ClearThumbnails();
            gridReleased = true;

            base.OnStop();
        }

        // Coming back from a finished session: the pool and inputs are unchanged, but the session may
        // have been started with values the summary screen let the drawer revisit.
        protected override void OnResume()
        {
            base.OnResume();
            UpdateStartState();
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
            var handoff = library.Sample(MaxPoolHandoff, MaxPoolHandoffChars);

            if (handoff.Count < library.Count)
                Log.Info(LogTag,
                    $"Pool of {library.Count} exceeds the {MaxPoolHandoff} handoff bound; " +
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

            // Set before the picker can stop this screen: OnStart must not re-walk the folder that
            // is about to be replaced. Cleared again if the picker never opens, or the flag would
            // stay raised and every later return to this screen would skip its rebuild.
            awaitingPickResult = true;

            try
            {
                StartActivityForResult(intent, PickFolderRequestCode);
            }
            catch (Exception error)
            {
                // A device with no document picker cannot be given one; crossing to another app is a
                // system boundary and a missing handler is not a defined outcome to throw on (§9).
                awaitingPickResult = false;
                Log.Error(LogTag, $"Opening the folder picker failed: {error.GetType().Name}: {error.Message}");

                // Reported where the button is. poolLabel lives on the setup pane, and the artist
                // who just tapped Pick folder is looking at the library pane — and the library
                // itself is still fine, so this captions without resetting it.
                emptyLabel.Text = GetString(Resource.String.picker_error_text);
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

        // The remembered library, as a uri, when it is still worth acting on: something is stored,
        // it is a SAF tree reference (LibraryReference), and the read grant that made it usable is
        // still held. Whether it is usable is Core's rule; enumerating the platform's grants and
        // parsing the uri is this layer's job.
        Android.Net.Uri? RememberedTree()
        {
            if (!LibraryReference.TryParse(settings.LastCollection, out var reference))
                return null;

            // Enumerating the platform's grants is a binder call into the system server, and parsing
            // is a platform call too — both can fault. This is no longer only OnCreate work: OnStart
            // runs it on every return to the screen, so an escape here would be a crash on every
            // foreground, off a reference that is persisted (INV-X-11, §9).
            try
            {
                if (!LibraryReference.HasReadGrant(reference, PersistedGrants()))
                {
                    Log.Info(LogTag, "Remembered folder is no longer granted; nothing to restore.");
                    return null;
                }

                return Android.Net.Uri.Parse(reference);
            }
            catch (Exception error)
            {
                // Unreadable grant state is "nothing to restore", never a crash (INV-GRP-5).
                Log.Warn(LogTag, $"Could not check the remembered grant: {error.Message}");
                return null;
            }
        }

        // The platform's persisted permissions, reduced to what the rule needs.
        IEnumerable<PersistedGrant> PersistedGrants()
        {
            // Three peers, all disposed: the bound list itself is a wrapper over the platform's
            // java.util.List, each element is a peer, and each element carries a Uri of its own.
            // Since FD-009 this runs on every return to the screen rather than once per launch, so
            // leaving them to a finalizer pass is the thing ARCHITECTURE.md §8 says not to do.
            var permissions = ContentResolver!.PersistedUriPermissions;
            using var listPeer = permissions as IDisposable;

            foreach (var permission in permissions)
            {
                using (permission)
                using (var granted = permission.Uri)
                    yield return new PersistedGrant(granted?.ToString(), permission.IsReadPermission);
            }
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            if (requestCode != PickFolderRequestCode)
                return;

            awaitingPickResult = false;

            // A cancelled pick, or one that came back without a folder, still has to rebuild: the
            // picker stopped this screen, OnStart deferred to this method, and the artist expects
            // the library they already had.
            var treeUri = resultCode == Result.Ok ? data?.Data : null;
            if (treeUri is null)
            {
                RestoreLastFolder();
                return;
            }

            // Handling a folder result must never crash the app. Persisting the grant, writing
            // settings, or enumerating the tree can each throw (SecurityException, provider quirks,
            // out-of-memory on large images); a failure here shows a message instead of dying.
            try
            {
                // Persist the read grant so the folder can be reused on the next launch. Pass the
                // read flag as a constant — deriving it from data.Flags is fragile: on some devices
                // the result intent reports no flags, yielding 0 and a SecurityException.
                ContentResolver!.TakePersistableUriPermission(treeUri, ActivityFlags.GrantReadUriPermission);

                settings.LastCollection = treeUri.ToString();
                settings.Save();

                Log.Info(LogTag, $"Folder selected: {treeUri}");
                LoadFolder(treeUri);
            }
            catch (Exception ex)
            {
                Log.Error(LogTag, $"Failed to open folder {treeUri}: {ex.GetType().Name}: {ex.Message}");

                // Covers taking the grant and writing settings, plus LoadFolder's synchronous
                // prologue. The walk itself now fails inside LoadFolderAsync, which reports through
                // this same method.
                ShowFolderError();
            }
        }

        // Comes up with the library the artist last used already loaded, so a relaunch is not a
        // second trip to the picker. A revoked grant, a deleted folder or a value this app can no
        // longer read is the ordinary case, not an error: the empty state shows and Start stays shut
        // (INV-GRP-5).
        // remembered: the tree a caller has already resolved, so the grant enumeration is not
        // repeated. Null means "work it out here", which is what the OnCreate path passes.
        void RestoreLastFolder(Android.Net.Uri? remembered = null)
        {
            if ((remembered ?? RememberedTree()) is not { } treeUri)
            {
                // Nothing left to restore — the grant was revoked, the volume unmounted or the
                // provider uninstalled. The empty state, not the folder-error caption: an expired
                // grant is an *expected* outcome (INV-GRP-5), and captioning it as a failure would
                // make it indistinguishable from a provider that is genuinely broken.
                //
                // Keyed on the folder, not on the pool: a load abandoned before it landed leaves the
                // pool empty while the pane still reads "Reading that folder…", so testing
                // `library.IsEmpty` would skip the reset and leave that caption up forever. A screen
                // that never loaded a folder has no loadedTreeUri and keeps its first-run prompt.
                //
                // Here rather than in OnStart so every caller is covered — including a cancelled
                // pick, which returns to a screen whose grid OnStop already released.
                if (loadedTreeUri is not null)
                {
                    ResetLibrary();
                }

                return;
            }

            // A grant that is still listed can still be unusable — an unmounted volume or an
            // uninstalled provider. This runs during OnCreate, and the stale uri is persisted, so an
            // escape here would be a crash on every launch from now on.
            try
            {
                LoadFolder(treeUri);
            }
            catch (Exception error)
            {
                Log.Warn(LogTag, $"Restoring the remembered folder failed: {error.Message}");
                ResetLibrary();
            }
        }

        // Starts a load of the picked tree. Synchronous prologue only: it releases the old grid,
        // decides whether the pool survives, paints the loading state, and hands off. The walk, the
        // classification rules and the decodes are LibraryLoader's, off the UI thread; what lands
        // afterwards is LoadFolderAsync's.
        void LoadFolder(Android.Net.Uri treeUri)
        {
            ClearThumbnails();

            // A re-walk of the folder already loaded keeps its pool, so returning from a session
            // does not grey Start out for the length of the walk. The pool it keeps is the previous
            // *complete* one and it is replaced whole, so Start is never armed over a half-built
            // pool. A first pick, or a different folder, still goes back to empty.
            //
            // Whether the pool survives is Core's comparison (LibraryLoadState), not the screen's:
            // it is the "never a mixture" clause of INV-X-13 and a source-shape test cannot tell it
            // from its own inverse.
            if (!LibraryLoadState.KeepsPool(loadedTreeUri, treeUri.ToString()))
            {
                library = ReferenceLibrary.Empty;
                loadedTreeUri = treeUri.ToString();
            }

            // After the reset, not before: RenderLibrary captions an empty library as a folder with
            // no images in it, so the loading caption has to land last or it is clobbered.
            RenderLibrary();
            ShowLoading();
            UpdateStartState();

            _ = LoadFolderAsync(treeUri);
        }

        // The load's tail, resumed on the main thread by the looper's SynchronizationContext once
        // the walk and the decodes are done.
        //
        // It catches everything, because by this point there is no caller left to do it: the
        // try/catch in OnActivityResult and RestoreLastFolder only ever covered the synchronous
        // prologue above. An escape here lands on the looper unhandled — and on the restore path,
        // which runs off a persisted uri, that would be a crash on every launch from then on
        // (INV-X-11).
        async Task LoadFolderAsync(Android.Net.Uri treeUri)
        {
            try
            {
                var loaded = await loader.LoadAsync(
                    treeUri, MaxThumbnails, ThumbnailDimension, MaxThumbnailDimension);

                // Superseded, stopped or destroyed while the walk was running. The loader has
                // already recycled whatever it decoded; this load writes nothing (INV-X-13).
                if (loaded is null)
                    return;

                // Re-checked here rather than trusting the loader's guard to have run in this same
                // looper turn. It does today — the awaiter inlines when the captured context is the
                // current one — but resting on that would mean a posted continuation could attach 24
                // previews to a grid OnStop had just released, and they would sit there until the
                // next stop. Structural beats a scheduling detail.
                if (gridReleased)
                {
                    LibraryLoader.DiscardThumbnails(loaded.Thumbnails);
                    return;
                }

                library = loaded.Library;
                AttachThumbnails(loaded.Thumbnails);

                RenderLibrary();

                // A session needs images to run, so the Start gate opens only when the folder
                // yielded at least one image (FD-002).
                UpdateStartState();
            }
            catch (Exception error)
            {
                // Type and message, not the whole exception: a SAF failure's message and stack can
                // carry absolute paths and the ids of files *inside* the folder, which is more than
                // the content uri §9 permits logging.
                Log.Error(LogTag, $"Loading folder {treeUri} failed: {error.GetType().Name}: {error.Message}");

                // The recovery is itself guarded. This task is discarded by its caller, so a throw
                // from in here would be an unobserved fault: nothing crashes, nothing is logged, and
                // the pane keeps the loading caption forever. A silent hang is as undefined an
                // outcome as a throw (INV-X-11).
                try
                {
                    ShowFolderError();
                }
                catch (Exception recovery)
                {
                    Log.Error(LogTag, $"Reporting the failure also failed: {recovery.GetType().Name}");
                }
            }
        }

        // Hands the decoded previews to the grid, which owns them from here. Each tile is attached
        // independently — AddView can run out of memory — and anything left undelivered is freed
        // rather than left to a finalizer pass.
        // The element type is nullable because null *is* the ownership state: a slot is cleared the
        // moment its view takes the bitmap, so the discard below can never free pixels the grid is
        // showing. Saying that in the type beats saying it in a comment over a `null!`.
        void AttachThumbnails(List<Android.Graphics.Bitmap?> thumbnails)
        {
            try
            {
                for (var i = 0; i < thumbnails.Count; i++)
                {
                    if (thumbnails[i] is not { } bitmap)
                        continue;

                    try
                    {
                        AddThumbnail(bitmap);

                        // Attached: the ImageView owns it now, so it must not be discarded below.
                        thumbnails[i] = null;
                    }
                    catch (Exception error)
                    {
                        // One tile that will not attach — an out-of-memory on the view, not on the
                        // decode — must cost that tile and nothing else.
                        Log.Warn(LogTag, $"Skipping a preview tile: {error.Message}");
                    }
                }
            }
            finally
            {
                // Whatever no view took: the tiles that failed to attach, and the tail after a
                // failure that stopped the loop. DiscardThumbnails skips the cleared slots.
                LibraryLoader.DiscardThumbnails(thumbnails);
            }
        }

        void RenderLibrary()
        {
            if (library.Count > 0)
            {
                emptyLabel.Visibility = ViewStates.Gone;
                libraryCount.Text =
                    string.Format(GetString(Resource.String.pool_ready_format), library.Count);
            }
            else
            {
                // Two different empty states, and telling them apart matters: a folder that holds no
                // images is not the same answer as no folder at all, which is what the artist is
                // back to after a grant expires. Keyed on the folder, since the pool is empty in
                // both cases.
                emptyLabel.Text = GetString(loadedTreeUri is null
                    ? Resource.String.empty_label_text
                    : Resource.String.empty_folder_text);

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

                // The view's own peers go too. Until FD-009 the grid was built once per launch and
                // leaving these to a finalizer cost nothing; it is now rebuilt on every return to
                // the screen, and an undisposed ImageView keeps its Java View — and through it this
                // Activity — reachable until a managed GC plus finalizer pass (§8).
                view.LayoutParameters?.Dispose();
                view.Dispose();
            }

            imageContainer.RemoveAllViews();
            libraryMore.Visibility = ViewStates.Gone;
        }

        // The library pane while a folder is being read. Deliberately not a branch inside
        // RenderLibrary: a folder still being walked is not a folder that turned out to be empty,
        // and captioning it "No images found" is a different and wrong answer.
        void ShowLoading()
        {
            emptyLabel.Text = GetString(Resource.String.library_loading_text);
            emptyLabel.Visibility = ViewStates.Visible;

            // "+N more not shown" counts the pool against what the grid is showing, and during a
            // re-walk the grid is empty while the previous pool is still there — which would read as
            // every image being hidden. It comes back when the previews land.
            libraryMore.Visibility = ViewStates.Gone;
        }

        // The empty state, captioned with the folder error. Both failure paths come here so the
        // ordering rule lives in one place: after ResetLibrary, never before, or RenderLibrary's
        // empty_folder_text clobbers the specific message.
        void ShowFolderError()
        {
            ResetLibrary();

            emptyLabel.Text = GetString(Resource.String.folder_error_text);
            emptyLabel.Visibility = ViewStates.Visible;
        }

        // Back to the no-folder state, rendered whole: grid, count, empty label, pool card and the
        // Start gate all describe the same (empty) pool. Without this a failed open leaves a blank
        // grid under a header still reporting the previous folder's count, with Start still open on
        // a pool the drawer was just told is gone.
        void ResetLibrary()
        {
            // A load still in flight would otherwise repopulate the folder the artist was just told
            // could not be opened — and its RenderLibrary would clobber the error message.
            loader?.Abandon();

            ClearThumbnails();
            library = ReferenceLibrary.Empty;

            // The pool is gone, so the folder it belonged to must not still match the next load —
            // otherwise a re-pick of that same folder would "keep" a pool that no longer exists.
            loadedTreeUri = null;

            RenderLibrary();
            UpdateStartState();
        }

        void AddThumbnail(Android.Graphics.Bitmap bitmap)
        {
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

            // A view that never makes it into the grid is freed here: ClearThumbnails only reaches
            // attached children, so an AddView that throws would otherwise strand the peer — and the
            // bitmap it is holding is freed by the caller's finally.
            try
            {
                imageContainer.AddView(imageView);
            }
            catch
            {
                imageView.SetImageDrawable(null);
                imageView.Dispose();
                throw;
            }
        }
    }
}
