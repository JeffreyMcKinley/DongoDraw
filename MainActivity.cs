using System.IO;
using Android.Content;
using Android.Database;
using Android.Provider;
using Android.Util;
using Android.Views;
using Android.Widget;
using DongoDraw.Core;
using DongoDraw.Data;

using Settings = DongoDraw.Data.Settings;

namespace DongoDraw
{
    // Three contexts in one screen, as three tabbed panes: the reference library, session setup
    // and preferences. The deviation and its closing trigger are DDD-ARCHITECTURE.md §20.
    [Activity(Label = "@string/app_name", MainLauncher = true)]
    public class MainActivity : Activity
    {
        const int PickFolderRequestCode = 1000;
        const string LogTag = "DongoDraw";
        const string DatabaseFileName = "dongodraw.db";

        const int ThumbnailDimension = 360;

        const int MaxThumbnailDimension = 720;

        // Preview only: the pool itself is never truncated, only what the grid shows (§8).
        const int MaxThumbnails = 24;

        // A ceiling on HandoffBound's session-length answer, and a length bound: ids parcel as
        // UTF-16, so 128k characters is ~256 KB of a ~1 MB Binder buffer (INV-POOL-6).
        const int MaxPoolHandoff = 1000;
        const int MaxPoolHandoffChars = 128_000;

        View paneSetup = null!;
        View paneLibrary = null!;
        View paneSettings = null!;
        View tabSession = null!;
        View tabImages = null!;
        View tabSettings = null!;

        GridLayout imageContainer = null!;
        TextView emptyLabel = null!;
        TextView libraryCount = null!;
        TextView libraryMore = null!;

        ReferenceLibrary library = ReferenceLibrary.Empty;

        LibraryLoader loader = null!;

        string? loadedTreeUri;

        // Set when OnStop released the grid. OnStart fires immediately after OnCreate, which has
        // already started a load, so without this every cold start walks the tree twice.
        bool gridReleased;

        bool awaitingPickResult;

        EditText secondsInput = null!;
        EditText countInput = null!;
        Button startButton = null!;
        TextView poolLabel = null!;
        TextView estimateLabel = null!;
        readonly Dictionary<int, Button> secondsChips = new();
        readonly Dictionary<int, Button> breakChips = new();

        Button shuffleToggle = null!;
        Button awakeToggle = null!;
        Button chimeToggle = null!;
        Button grayscaleToggle = null!;

        Settings settings = null!;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            SetContentView(Resource.Layout.activity_main);

            var databasePath = Path.Combine(FilesDir!.AbsolutePath, DatabaseFileName);
            settings = Settings.Open(databasePath);
            Log.Info(LogTag,
                $"Settings loaded from {databasePath}: " +
                $"poseDuration={settings.PoseDurationSeconds}s, break={settings.BreakSeconds}s, " +
                $"shuffle={settings.ShuffleImages}, grayscale={settings.GrayscaleMode}");

            // The application's resolver, never this Activity's: an Activity's would pin the
            // destroyed screen's whole view tree for the length of a walk (§7).
            loader = new LibraryLoader(ApplicationContext!.ContentResolver!);

            BindPanes();
            BindLibrary();
            BindSetup();
            BindSettings();

            ShowPane(paneSetup, tabSession);

            RestoreLastFolder();
        }

        protected override void OnDestroy()
        {
            // OnCreate can throw before these are assigned; ClearThumbnails guards its own fields.
            loader?.Abandon();
            ClearThumbnails();

            settings?.Dispose();
            base.OnDestroy();
        }

        protected override void OnStart()
        {
            base.OnStart();

            if (!gridReleased)
                return;

            gridReleased = false;

            if (awaitingPickResult)
            {
                return;
            }

            RestoreLastFolder();
        }

        protected override void OnStop()
        {
            loader?.Abandon();
            ClearThumbnails();
            gridReleased = true;

            base.OnStop();
        }

        protected override void OnResume()
        {
            base.OnResume();
            UpdateStartState();
        }

        protected override void OnPause()
        {
            base.OnPause();

            // The typed inputs are the only values that live nowhere but the screen; every other
            // preference is written where it is flipped.
            CaptureTypedInputs();
            SaveSettings();
        }

        void CaptureTypedInputs()
        {
            if (Draft().Config is not { } config)
                return;

            settings.PoseDurationSeconds = config.SecondsPerImage;
            settings.SessionImageCount = config.ImageCount;
        }

        void SaveSettings()
        {
            try
            {
                settings?.Save();
            }
            catch (Exception error)
            {
                Log.Error(LogTag, $"Could not save settings: {error}");
            }
        }

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

        void ShowPane(View pane, View tab)
        {
            paneSetup.Visibility = pane == paneSetup ? ViewStates.Visible : ViewStates.Gone;
            paneLibrary.Visibility = pane == paneLibrary ? ViewStates.Visible : ViewStates.Gone;
            paneSettings.Visibility = pane == paneSettings ? ViewStates.Visible : ViewStates.Gone;

            tabSession.Selected = tab == tabSession;
            tabImages.Selected = tab == tabImages;
            tabSettings.Selected = tab == tabSettings;
        }

        void BindSetup()
        {
            secondsInput = FindViewById<EditText>(Resource.Id.seconds_input)!;
            countInput = FindViewById<EditText>(Resource.Id.count_input)!;
            startButton = FindViewById<Button>(Resource.Id.start_button)!;
            poolLabel = FindViewById<TextView>(Resource.Id.pool_label)!;
            estimateLabel = FindViewById<TextView>(Resource.Id.estimate_label)!;

            // Positional: chip i renders presets[i], so the two arrays must stay in the same order.
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

            FindViewById<Button>(Resource.Id.change_button)!.Click +=
                (_, _) => ShowPane(paneLibrary, tabImages);

            UpdateStartState();
        }

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

        DrawingSession<Android.Graphics.Bitmap> Draft() =>
            DrawingSession<Android.Graphics.Bitmap>.Evaluate(
                secondsInput.Text, countInput.Text, !library.IsEmpty, settings.BreakSeconds);

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

        void BindLibrary()
        {
            imageContainer = FindViewById<GridLayout>(Resource.Id.image_container)!;
            emptyLabel = FindViewById<TextView>(Resource.Id.empty_label)!;
            libraryCount = FindViewById<TextView>(Resource.Id.library_count)!;
            libraryMore = FindViewById<TextView>(Resource.Id.library_more)!;

            imageContainer.ColumnCount = Resources!.GetInteger(Resource.Integer.library_columns);

            FindViewById<Button>(Resource.Id.pick_button)!.Click += (_, _) => PickFolder();
        }

        void PickFolder()
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);

            // A hint only: the picker is free to ignore it, and the drawer can still browse anywhere.
            if (LastPickedDocumentUri() is { } initial)
                intent.PutExtra(DocumentsContract.ExtraInitialUri, initial);

            // Set before the picker can stop this screen, and cleared again if it never opens.
            awaitingPickResult = true;

            try
            {
                StartActivityForResult(intent, PickFolderRequestCode);
            }
            catch (Exception error)
            {
                awaitingPickResult = false;
                Log.Error(LogTag, $"Could not open the folder picker: {error.GetType().Name}: {error.Message}");

                // Not folder_error_text: "try again" is wrong advice when nothing can pick.
                emptyLabel.Text = GetString(Resource.String.picker_error_text);
                emptyLabel.Visibility = ViewStates.Visible;
            }
        }

        // A *document* uri, which is what the picker navigates to: handed a bare tree uri it lands
        // at the root of the provider instead.
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
                Log.Warn(LogTag, $"Could not build a picker hint: {error.Message}");
                return null;
            }
        }

        Android.Net.Uri? RememberedTree()
        {
            if (!LibraryReference.TryParse(settings.LastCollection, out var reference))
                return null;

            return Android.Net.Uri.Parse(reference);
        }

        IReadOnlyList<PersistedGrant> PersistedGrants()
        {
            var grants = new List<PersistedGrant>();

            foreach (var permission in ContentResolver!.PersistedUriPermissions)
            {
                using (permission)
                using (var granted = permission.Uri)
                    grants.Add(new PersistedGrant(granted?.ToString(), permission.IsReadPermission));
            }

            return grants;
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            if (requestCode != PickFolderRequestCode)
                return;

            awaitingPickResult = false;

            // A cancelled pick still has to rebuild: the picker stopped this screen and OnStart
            // deferred to this method.
            var treeUri = resultCode == Result.Ok ? data?.Data : null;
            if (treeUri is null)
            {
                RestoreLastFolder();
                return;
            }

            var previousCollection = settings.LastCollection;
            try
            {
                // The read flag as a constant, never derived from data.Flags: some devices report
                // no flags on the result intent, yielding 0 and a SecurityException.
                ContentResolver!.TakePersistableUriPermission(treeUri, ActivityFlags.GrantReadUriPermission);

                ReleaseSupersededGrants(treeUri.ToString());

                // Where "openable" is established, and synchronously: the walk below is async, and
                // the persistence chain must contain no await (INV-SET-P4).
                if (DocumentsContract.GetTreeDocumentId(treeUri) is null)
                    throw new InvalidOperationException("The picked tree has no document id.");

                settings.LastCollection = treeUri.ToString();

                settings.Save();

                Log.Info(LogTag, $"Folder selected: {treeUri}");

                LoadFolder(treeUri);
            }
            catch (Exception ex)
            {
                Log.Error(LogTag, $"Failed to open folder {treeUri}: {ex.GetType().Name}: {ex.Message}");
                settings.LastCollection = previousCollection;
                ResetLibrary();

                // After ResetLibrary, whose re-caption from state would overwrite folder_error_text.
                emptyLabel.Text = GetString(Resource.String.folder_error_text);
                emptyLabel.Visibility = ViewStates.Visible;
            }
        }

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
                if (!LibraryReference.HasReadGrant(reference, PersistedGrants()))
                {
                    Log.Warn(LogTag, "The remembered folder is still stored but its read grant is gone.");
                    ShowRememberedFolderUnavailable();
                    return;
                }

                // Its own try, before the walk: a failed refresh must not cost a library that then
                // loads, and a failed walk must not skip the refresh (INV-REF-4).
                RefreshGrant(treeUri);

                LoadFolder(treeUri);
            }
            catch (Exception error)
            {
                Log.Warn(LogTag, $"Restoring the remembered folder failed: {error.Message}");
                ShowRememberedFolderUnavailable();
            }
        }

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

        bool walkFailed;

        void LoadFolder(Android.Net.Uri treeUri)
        {
            ClearThumbnails();
            walkFailed = false;

            if (!LibraryLoadState.KeepsPool(loadedTreeUri, treeUri.ToString()))
            {
                library = ReferenceLibrary.Empty;
                loadedTreeUri = treeUri.ToString();
            }

            // Before ShowLoading, whose caption RenderLibrary would otherwise overwrite.
            RenderLibrary();
            ShowLoading();
            UpdateStartState();

            _ = LoadFolderAsync(treeUri);
        }

        async Task LoadFolderAsync(Android.Net.Uri treeUri)
        {
            try
            {
                var loaded = await loader.LoadAsync(
                    treeUri, MaxThumbnails, ThumbnailDimension, MaxThumbnailDimension);

                if (loaded is null)
                    return;

                // Structural, not a reliance on the loader's guard inlining into this same looper
                // turn: a posted continuation would attach previews to a grid OnStop just released.
                if (gridReleased)
                {
                    LibraryLoader.DiscardThumbnails(loaded.Thumbnails);
                    return;
                }

                library = loaded.Library;
                AttachThumbnails(loaded.Thumbnails);

                RenderLibrary();

                UpdateStartState();

                Log.Info(LogTag, $"Loaded the folder: {library.Count} images.");
            }
            catch (Exception error)
            {
                Log.Error(LogTag, $"Loading folder {treeUri} failed: {error.GetType().Name}: {error.Message}");

                try
                {
                    ShowRememberedFolderUnavailable();
                }
                catch (Exception recovery)
                {
                    Log.Error(LogTag, $"Reporting the failure also failed: {recovery.GetType().Name}");
                }
            }
        }

        // The element type is nullable because null *is* the ownership state: a slot is cleared the
        // moment its view takes the bitmap, so the discard below cannot free attached pixels (§8).
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

                        thumbnails[i] = null;
                    }
                    catch (Exception error)
                    {
                        Log.Warn(LogTag, $"Skipping a preview tile: {error.Message}");
                    }
                }
            }
            finally
            {
                LibraryLoader.DiscardThumbnails(thumbnails);
            }
        }

        // Not a branch in RenderLibrary: a folder still being walked is not one that turned out
        // empty, and Classify has no state for it.
        void ShowLoading()
        {
            emptyLabel.Text = GetString(Resource.String.library_loading_text);
            emptyLabel.Visibility = ViewStates.Visible;

            libraryMore.Visibility = ViewStates.Gone;
        }

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

        void ClearThumbnails()
        {
            // Both fields are `null!` until BindLibrary runs, which OnCreate can throw before.
            if (imageContainer is null || libraryMore is null)
                return;

            for (var i = imageContainer.ChildCount - 1; i >= 0; i--)
            {
                if (imageContainer.GetChildAt(i) is not ImageView view)
                    continue;

                // Captured before the detach below: a second lookup can return a different peer (§8).
                using var drawable = view.Drawable as Android.Graphics.Drawables.BitmapDrawable;
                var bitmap = drawable?.Bitmap;
                view.SetImageDrawable(null);

                if (bitmap is not null && !bitmap.IsRecycled)
                    bitmap.Recycle();

                bitmap?.Dispose();

                view.LayoutParameters?.Dispose();
                view.Dispose();
            }

            imageContainer.RemoveAllViews();
            libraryMore.Visibility = ViewStates.Gone;
        }

        void ResetLibrary()
        {
            loader?.Abandon();

            ClearThumbnails();
            library = ReferenceLibrary.Empty;

            // Cleared with the pool, or a re-pick of the same folder would "keep" one that is gone.
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

            // ClearThumbnails only reaches attached children, so a view that never makes it into
            // the grid is unreachable by every other cleanup path here.
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
