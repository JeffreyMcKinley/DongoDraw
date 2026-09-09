using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Media;
using Android.Util;
using Android.Views;
using Android.Widget;
using FigureDrawing.Core;

namespace FigureDrawing
{
    // Exported = false is the platform default here, stated rather than inherited: the extras this
    // screen trusts are only safe while nothing outside the app can supply them.
    [Activity(
        Label = "@string/app_name",
        Exported = false,
        Theme = "@style/AppTheme.NoActionBar",
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize
            | ConfigChanges.ScreenLayout | ConfigChanges.Orientation | ConfigChanges.KeyboardHidden)]
    public class SessionActivity : Activity
    {
        public const string ExtraPool = "pool";
        public const string ExtraSeconds = "seconds";
        public const string ExtraCount = "count";
        public const string ExtraBreak = "break";
        public const string ExtraShuffle = "shuffle";
        public const string ExtraGrayscale = "grayscale";
        public const string ExtraKeepAwake = "keepawake";
        public const string ExtraChime = "chime";

        const string LogTag = "FigureDrawing";

        const int MaxImageDimension = 1080;

        static readonly ColorMatrixColorFilter GrayscaleFilter = MakeGrayscaleFilter();

        const float BlurRadius = 24f;

        // Well under a second, so the displayed value flips promptly at each boundary (§7).
        const int TickIntervalMs = 200;

        const int WideScreenWidthDp = 600;

        // Past this the segments would be sub-pixel, so the strip is dropped entirely.
        const int MaxPips = 40;

        LinearLayout body = null!;
        View stage = null!;
        LinearLayout rail = null!;
        ImageView image = null!;
        View grid = null!;
        TextView status = null!;
        TextView timer = null!;
        TextView progressLabel = null!;
        ProgressBar ring = null!;
        View breakOverlay = null!;
        TextView breakTimer = null!;
        View pauseOverlay = null!;
        TextView pausedTimer = null!;
        TextView pausedStats = null!;
        View progressGroup = null!;
        LinearLayout pips = null!;
        TextView stats = null!;

        Button grayscaleChip = null!;
        Button flipChip = null!;
        Button gridChip = null!;
        Button blurChip = null!;
        Button zoomInChip = null!;
        Button zoomOutChip = null!;

        View summary = null!;
        TextView summaryImages = null!;
        TextView summaryTime = null!;
        TextView summaryAverage = null!;
        TextView summarySkipped = null!;

        DrawingSession<PoseImage> session = null!;
        ViewerTools tools = null!;
        Android.OS.Handler ticker = null!;

        bool sessionReady;

        int buildGeneration;

        // Not interchangeable with `ticking`, which is also false during the very first build,
        // before the loop has started.
        bool resumed;

        Java.Lang.IRunnable tickRunnable = null!;
        bool ticking;

        string? lastDisplay;

        PoseImage? displayed;

        // Every field in this family is read and written on the main thread only, which is why none
        // is volatile or locked; the Task is the one thing that crosses, and it is immutable.

        // A null image under a non-null id is an answer too: "this file is unreadable".
        string? prefetchedId;
        PoseImage? prefetched;

        string? prefetchingId;
        Task<PoseImage?>? prefetchTask;

        // The boundary took the result straight off the Task, so the session owns it: the
        // continuation must not release or cache it, which would be a second owner.
        bool prefetchClaimed;

        CancellationTokenSource? prefetchCancel;

        int prefetchGeneration;

        // In GridStyles order: verticals left to right, then horizontals top to bottom. Applied by
        // index, so the order is the wiring.
        GridLinePainter[] gridPainters = Array.Empty<GridLinePainter>();

        GridPalette gridPalette;

        EventHandler<View.LayoutChangeEventArgs>? stageLayoutChanged;

        string[] pool = Array.Empty<string>();
        int secondsPerImage;
        int imageCount;
        int breakSeconds;
        bool shuffle;
        bool startGrayscale;
        bool keepAwake;
        bool chime;

        ToneGenerator? tone;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            SetContentView(Resource.Layout.activity_session);

            BindViews();

            pool = Intent?.GetStringArrayExtra(ExtraPool) ?? Array.Empty<string>();
            secondsPerImage = Intent?.GetIntExtra(ExtraSeconds, SessionSetup.DefaultSecondsPerImage)
                              ?? SessionSetup.DefaultSecondsPerImage;
            imageCount = Intent?.GetIntExtra(ExtraCount, SessionSetup.DefaultImageCount)
                         ?? SessionSetup.DefaultImageCount;
            breakSeconds = Intent?.GetIntExtra(ExtraBreak, SessionSetup.DefaultBreakSeconds)
                           ?? SessionSetup.DefaultBreakSeconds;
            shuffle = Intent?.GetBooleanExtra(ExtraShuffle, true) ?? true;
            startGrayscale = Intent?.GetBooleanExtra(ExtraGrayscale, false) ?? false;
            keepAwake = Intent?.GetBooleanExtra(ExtraKeepAwake, true) ?? true;
            chime = Intent?.GetBooleanExtra(ExtraChime, false) ?? false;

            // Keep the screen awake for the whole session unless the drawer turned that off — no
            // sleeping mid-pose (FD-004 acceptance).
            if (keepAwake)
                Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

            if (chime)
                tone = new ToneGenerator(Android.Media.Stream.Notification, 70);

            Log.Info(LogTag,
                $"Session player start: {pool.Length} images in pool, {imageCount} to draw, " +
                $"{secondsPerImage}s/image, {breakSeconds}s break, shuffle={shuffle}, " +
                $"grayscale={startGrayscale}.");

            ticker = new Android.OS.Handler(Android.OS.Looper.MainLooper!);
            tickRunnable = new Java.Lang.Runnable(Tick);

            ApplyRailLayout();
            StartSession();
        }

        // A fold opening mid-session moves the rail beside the pose without losing the pose.
        public override void OnConfigurationChanged(Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            ApplyRailLayout();
        }

        // Backgrounded: freeze the pose clock and stop repainting, so no time is burned and the
        // timer cannot fire while hidden (FD-005 acceptance).
        protected override void OnPause()
        {
            resumed = false;

            // A session still being built has no clock to freeze; the build itself carries the pause
            // forward when it publishes (see PublishSession).
            if (sessionReady)
                session.Pause();

            StopTicking();

            // The screen may never come back, and a decode nobody will consume must not outlive it
            // holding a full-size bitmap (docs/ARCHITECTURE.md §7).
            CancelPrefetch();
            base.OnPause();
        }

        // Foregrounded again: pick the pose up exactly where it was left. A session that was already
        // paused by the drawer, or already over, stays that way.
        protected override void OnResume()
        {
            base.OnResume();
            resumed = true;

            // Nothing to resume until the build publishes, and it will start the clocks itself.
            if (!sessionReady)
                return;

            // A pause the drawer asked for is remembered by the session itself, so returning from
            // the background cannot restart a pose that was deliberately stopped.
            if (session.IsComplete || session.PausedByUser)
                return;

            session.Resume();
            RenderClock();
            StartTicking();

            // The clocks are running again, so a boundary is coming: pick the decode back up. This
            // is the one running path that does not go through Render, deliberately — a full repaint
            // here would rebuild the pips and reset the clock cache for no reason.
            PrefetchUpcoming();
        }

        protected override void OnDestroy()
        {
            StopTicking();
            CancelPrefetch();
            // Drop the keep-awake flag so it can't leak past this screen.
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            tone?.Release();
            tone?.Dispose();
            tone = null;

            // Every listener on a long-lived object is detached (docs/ARCHITECTURE.md §8).
            if (stage is not null && stageLayoutChanged is not null)
                stage.LayoutChange -= stageLayoutChanged;
            stageLayoutChanged = null;

            // Null-safe: OnCreate can throw before BindViews runs, and a teardown that NREs would
            // mask the original failure and leak the bitmap it came here to free.
            image?.SetImageDrawable(null);
            ReleaseDisplayed();

            base.OnDestroy();
        }

        // --- Wiring ----------------------------------------------------------

        void BindViews()
        {
            body = FindViewById<LinearLayout>(Resource.Id.session_body)!;
            stage = FindViewById<View>(Resource.Id.session_stage)!;
            rail = FindViewById<LinearLayout>(Resource.Id.session_rail)!;
            image = FindViewById<ImageView>(Resource.Id.session_image)!;
            grid = FindViewById<View>(Resource.Id.session_grid)!;
            BindGuides();
            status = FindViewById<TextView>(Resource.Id.session_status)!;
            timer = FindViewById<TextView>(Resource.Id.session_timer)!;
            progressLabel = FindViewById<TextView>(Resource.Id.session_progress)!;
            ring = FindViewById<ProgressBar>(Resource.Id.session_ring)!;
            breakOverlay = FindViewById<View>(Resource.Id.session_break_overlay)!;
            breakTimer = FindViewById<TextView>(Resource.Id.break_timer)!;
            pauseOverlay = FindViewById<View>(Resource.Id.session_pause_overlay)!;
            pausedTimer = FindViewById<TextView>(Resource.Id.paused_timer)!;
            pausedStats = FindViewById<TextView>(Resource.Id.paused_stats)!;
            progressGroup = FindViewById<View>(Resource.Id.session_progress_group)!;
            pips = FindViewById<LinearLayout>(Resource.Id.session_pips)!;
            stats = FindViewById<TextView>(Resource.Id.session_stats)!;

            grayscaleChip = FindViewById<Button>(Resource.Id.chip_grayscale)!;
            flipChip = FindViewById<Button>(Resource.Id.chip_flip)!;
            gridChip = FindViewById<Button>(Resource.Id.chip_grid)!;
            blurChip = FindViewById<Button>(Resource.Id.chip_blur)!;
            zoomInChip = FindViewById<Button>(Resource.Id.chip_zoom_in)!;
            zoomOutChip = FindViewById<Button>(Resource.Id.chip_zoom_out)!;

            summary = FindViewById<View>(Resource.Id.session_summary)!;
            summaryImages = FindViewById<TextView>(Resource.Id.summary_images)!;
            summaryTime = FindViewById<TextView>(Resource.Id.summary_time)!;
            summaryAverage = FindViewById<TextView>(Resource.Id.summary_average)!;
            summarySkipped = FindViewById<TextView>(Resource.Id.summary_skipped)!;

            // Manual "done" gesture: finish the pose early instead of waiting out the countdown.
            image.Click += (_, _) => Command(() => session.Next());

            FindViewById<Button>(Resource.Id.session_next)!.Click += (_, _) => Command(() => session.Next());
            FindViewById<Button>(Resource.Id.session_skip)!.Click += (_, _) => Command(() => session.Skip());
            FindViewById<Button>(Resource.Id.session_end)!.Click += (_, _) => Command(() => session.End());
            FindViewById<Button>(Resource.Id.session_pause)!.Click += (_, _) => PauseSession();

            FindViewById<Button>(Resource.Id.paused_resume)!.Click += (_, _) => ResumeSession();
            FindViewById<Button>(Resource.Id.paused_skip)!.Click += (_, _) =>
            {
                ResumeSession();
                Command(() => session.Skip());
            };
            FindViewById<Button>(Resource.Id.paused_end)!.Click += (_, _) => Command(() => session.End());

            grayscaleChip.Click += (_, _) => ApplyTool(() => tools.ToggleGrayscale());
            flipChip.Click += (_, _) => ApplyTool(() => tools.ToggleFlip());
            gridChip.Click += (_, _) => ApplyTool(() => tools.ToggleGrid());
            blurChip.Click += (_, _) => ApplyTool(() => tools.ToggleBlur());
            zoomInChip.Click += (_, _) => ApplyTool(() => tools.ZoomIn());
            zoomOutChip.Click += (_, _) => ApplyTool(() => tools.ZoomOut());

            // Blur is a RenderEffect, which only exists from API 31. Below that the chip would be a
            // control that does nothing, so it is not offered at all.
            if (!OperatingSystem.IsAndroidVersionAtLeast(31))
                blurChip.Visibility = ViewStates.Gone;

            FindViewById<Button>(Resource.Id.summary_again)!.Click += (_, _) => StartSession();
            FindViewById<Button>(Resource.Id.summary_settings)!.Click += (_, _) => Finish();
        }

        // The four rule-of-thirds guides. Their views are only needed to hang a painter on, so they
        // are locals rather than four fields that nothing would read again.
        void BindGuides()
        {
            var casingPx = Resources!.GetDimensionPixelSize(Resource.Dimension.grid_line_casing);

            gridPainters = new[]
            {
                new GridLinePainter(FindViewById<View>(Resource.Id.session_grid_v1)!, casingPx, vertical: true),
                new GridLinePainter(FindViewById<View>(Resource.Id.session_grid_v2)!, casingPx, vertical: true),
                new GridLinePainter(FindViewById<View>(Resource.Id.session_grid_h1)!, casingPx, vertical: false),
                new GridLinePainter(FindViewById<View>(Resource.Id.session_grid_h2)!, casingPx, vertical: false),
            };

            gridPalette = new GridPalette(
                LightLine: GetColor(Resource.Color.grid_line_light),
                LightCasing: GetColor(Resource.Color.grid_casing_light),
                DarkLine: GetColor(Resource.Color.grid_line_dark),
                DarkCasing: GetColor(Resource.Color.grid_casing_dark));

            // Where each guide falls on the pose depends on the stage's size, which is not known
            // until layout has run and changes again when a fold opens. Detached in OnDestroy.
            stageLayoutChanged = (_, _) => ApplyGridColors();
            stage.LayoutChange += stageLayoutChanged;
        }

        // Builds (or rebuilds, for "Run it again") a session from the extras this screen was started
        // with. The build happens off the UI thread: the running constructor positions itself on the
        // first displayable image before it returns (INV-PLY-6), and doing that means decoding — a
        // two-pass open plus a multi-megabyte decode, and more than one of those if the first files
        // it reaches are unreadable. On this thread that is a stalled launch and, on a slow provider,
        // an ANR before the screen has drawn anything.
        void StartSession()
        {
            // Whatever the previous run decoded belongs to the previous run. Dropped before the new
            // one starts, or the peak is two full-size poses at once.
            CancelPrefetch();
            sessionReady = false;

            tools = new ViewerTools(startGrayscale);
            lastDisplay = null;

            RenderBuildingState();
            BuildSession(++buildGeneration);
        }

        // The build itself. Nothing here touches a view: the session is constructed on a pool thread
        // and published on the main one, and only the publish half may look at the screen.
        async void BuildSession(int generation)
        {
            try
            {
                var config = new SessionConfig(secondsPerImage, imageCount, breakSeconds);
                var built = await Task.Run(() => new DrawingSession<PoseImage>(
                    pool,
                    config,
                    LoadPose,
                    shuffle,
                    onUnreadable: id => Log.Warn(LogTag, $"Skipping unreadable image {id}"),

                    // Explicit rather than the aggregate's default (INV-PLY-3). Twice the pool means
                    // a run this long cannot happen unless every id in it is unreadable, even when
                    // the run spans a pass boundary, so the error screen never appears while a
                    // drawable image remains. Repeats after the first pass cost no decode
                    // (INV-PLY-8), and the pool is itself bounded by the session's length
                    // (INV-POOL-6), so this scales with the run rather than with the folder.
                    maxConsecutiveFailures: pool.Length * 2));

                PublishSession(built, generation);
            }
            catch (Exception ex)
            {
                // Nothing above may throw out of an async void method: the exception would land on
                // the main looper with no catch above it and take the process down (INV-X-11).
                Log.Error(LogTag, $"Building the session failed: {ex}");
            }
        }

        // Back on the main thread with a built session. Everything that decides whether it is still
        // wanted lives here, on one thread, so there is nothing to synchronise.
        void PublishSession(DrawingSession<PoseImage> built, int generation)
        {
            // Superseded by a later build, or the screen went away while this one ran: the pose it
            // decoded has no owner, so it is freed here rather than left to a finalizer.
            if (generation != buildGeneration || IsFinishing || IsDestroyed)
            {
                Release(built.CurrentImage);
                return;
            }

            session = built;
            sessionReady = true;

            // The clock started when the constructor ran. If the screen was backgrounded meanwhile,
            // stop it now — otherwise the first pose quietly burns however long the app stays away.
            if (!resumed)
                session.Pause();

            BuildPips();
            ApplyTools();
            Render();
        }

        // What the stage shows while a session is being built. The status line already exists for
        // the "could not display" case; this is the same one view saying something briefer.
        void RenderBuildingState()
        {
            body.Visibility = ViewStates.Visible;
            summary.Visibility = ViewStates.Gone;
            breakOverlay.Visibility = ViewStates.Gone;
            pauseOverlay.Visibility = ViewStates.Gone;

            status.Visibility = ViewStates.Visible;
            status.Text = GetString(Resource.String.session_loading_text);

            image.SetImageDrawable(null);
            ReleaseDisplayed();
        }

        // --- Commands --------------------------------------------------------

        // Every pose command goes through here: run it on the Core aggregate, then repaint. The
        // aggregate is what decides whether the command counted, started a break, or ended the run.
        void Command(Action command)
        {
            // A tap that lands while the session is still being built has nothing to command; the
            // loading state is on screen and the rail's buttons are not yet meaningful.
            if (!sessionReady)
                return;

            command();
            Render();
        }

        void PauseSession()
        {
            if (!sessionReady || session.IsComplete)
                return;

            session.Pause(PauseReason.User);
            StopTicking();
            Render();
        }

        void ResumeSession()
        {
            if (!sessionReady || session.IsComplete)
                return;

            session.Resume();
            Render();
        }

        void ApplyTool(Action change)
        {
            change();
            ApplyTools();
        }

        // --- Repaint loop ----------------------------------------------------

        // Refresh the displayed time and let the aggregate expire the current phase. Bails the
        // instant the activity is tearing down so a queued Tick can never touch a dead view.
        void Tick()
        {
            if (!ticking || !sessionReady || IsFinishing || IsDestroyed)
                return;

            // What changed is the session's answer, not this screen's to work out: a rest starting is
            // not a new pose, and the tick that ends the run is not one either.
            switch (session.Tick())
            {
                case SessionTick.PoseStarted:
                    Chime();
                    Render();
                    break;

                case SessionTick.BreakStarted:
                case SessionTick.Completed:
                    Render();
                    break;

                default:
                    RenderClock();
                    break;
            }

            if (ticking)
                ticker.PostDelayed(tickRunnable, TickIntervalMs);
        }

        void StartTicking()
        {
            if (ticking)
                return;

            ticking = true;
            ticker.PostDelayed(tickRunnable, TickIntervalMs);
        }

        // Stop AND drop any already-queued repaint, so nothing fires after we've torn down.
        void StopTicking()
        {
            ticking = false;
            ticker.RemoveCallbacks(tickRunnable);
        }

        // A short tone when the pose changes on its own. Only the automatic change chimes: a drawer
        // who tapped Next or Skip is already looking at the screen. Reached only from the
        // PoseStarted arm, so completion no longer needs guarding here.
        void Chime() => tone?.StartTone(Tone.PropBeep, 120);

        // --- Decoding the next pose ahead of the boundary --------------------

        // Start decoding the image after the one on screen, if that is worth doing. Idempotent by
        // design: this is called from the tail of every repaint, so it must cost nothing when the
        // upcoming id is already cached or already being decoded.
        void PrefetchUpcoming()
        {
            if (!sessionReady || IsFinishing || IsDestroyed || session.IsPaused)
                return;

            // One slot. A decode is already running, so this call does nothing but note that the
            // answer may be for the wrong id — DecodeAhead re-enters here once it settles, and picks
            // up whatever is next by then. Without this, every command that changes the upcoming id
            // would start another decode and a handful of quick taps would run several at once.
            if (prefetchTask is not null)
                return;

            var id = session.UpcomingImageId;
            if (id is null)
            {
                // Nothing drawable follows this pose, so anything cached can never be consumed.
                ReleasePrefetched();
                return;
            }

            if (id == prefetchedId)
                return;

            // The cache answers for an image that is no longer next: a boundary consumed that id
            // while the decode was still running, so the answer landed too late to use.
            ReleasePrefetched();

            var generation = ++prefetchGeneration;
            var cancel = new CancellationTokenSource();
            var token = cancel.Token;
            var resolver = ContentResolver!;

            prefetchCancel = cancel;
            prefetchingId = id;
            prefetchClaimed = false;
            prefetchTask = Task.Run(() => DecodePose(resolver, id, token), token);

            DecodeAhead(id, generation, prefetchTask);
        }

        // Settles one prefetch (docs/ARCHITECTURE.md §7). Returns void rather than a Task because
        // nothing awaits it: it is started from a repaint and its only result is a field.
        //
        // No ConfigureAwait(false) anywhere here: the continuation MUST come back to the main
        // thread. Android's synchronization context posts it to the main looper, which is also the
        // barrier that publishes the decoded pixels to the thread that will attach them.
        async void DecodeAhead(string id, int generation, Task<PoseImage?> work)
        {
            // The whole body is guarded, not just the await: everything after it is a JNI call on a
            // peer that teardown may already have disposed, and an exception escaping an async void
            // lands on the main looper with nothing above it to catch it (INV-X-11).
            try
            {
                PoseImage? pose = null;

                try
                {
                    pose = await work;
                }
                catch (OperationCanceledException)
                {
                    // Abandoned before it started. Nothing was decoded, so there is nothing to free.
                }
                catch (Exception ex)
                {
                    Log.Warn(LogTag, $"Could not decode ahead: {ex.GetType().Name}");
                }

                // The slot is free from here, whatever happens to the result.
                prefetchTask = null;
                prefetchingId = null;
                prefetchCancel?.Dispose();
                prefetchCancel = null;

                if (prefetchClaimed)
                {
                    // The boundary arrived first and took this straight off the Task, so the session
                    // owns it now. Releasing or caching it here would make that two owners.
                    prefetchClaimed = false;
                }
                else if (generation != prefetchGeneration || IsFinishing || IsDestroyed)
                {
                    // A decode cannot be stopped once it is running, so this is where an abandoned
                    // prefetch is settled: the screen took a different turn while this ran, and the
                    // pixels it produced belong to nobody.
                    Release(pose);
                }
                else
                {
                    prefetchedId = id;
                    prefetched = pose;
                }

                // Whatever happened, the one slot is free again — re-aim it at whatever is actually
                // next now, which is how a boundary that overtook this decode gets covered.
                PrefetchUpcoming();
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, $"Settling the prefetch failed: {ex.GetType().Name}");
            }
        }

        // Give up on whatever the prefetch was doing and free whatever it produced. "Cancel" is the
        // intent, not the mechanism: a decode already running cannot be stopped, so what this does
        // is make sure its result is thrown away rather than cached. Idempotent, and called on every
        // way out of this screen — a prefetch that outlived the screen is the leak this trades the
        // boundary stall for if it is ever missed.
        void CancelPrefetch()
        {
            prefetchGeneration++;

            // Stops a decode that has not started, and lets a running one give up before it
            // allocates. Not disposed here — the continuation owns that, and it still has to run.
            prefetchCancel?.Cancel();

            // `prefetchTask` and `prefetchingId` are deliberately left alone: the work is still
            // running, and clearing the slot here would let the next Render start a second decode
            // alongside it. The continuation clears them when it settles.
            ReleasePrefetched();
        }

        // --- Rendering -------------------------------------------------------

        // Full repaint: which of the three states the screen is in (player, error, summary) and
        // everything inside the current one.
        void Render()
        {
            // Every caller checks this already; the guard is here because a repaint of a session
            // that does not exist yet is the failure mode of getting the build wiring wrong, and it
            // should be a no-op rather than a crash on the first frame.
            if (!sessionReady)
                return;

            if (session.IsComplete)
            {
                StopTicking();
                CancelPrefetch();
                RenderTerminalState();
                return;
            }

            body.Visibility = ViewStates.Visible;
            summary.Visibility = ViewStates.Gone;
            status.Visibility = ViewStates.Gone;

            // Repoint the view FIRST, then free the pixels the view no longer draws. The reference
            // check is load-bearing: Render re-runs on every command, pause and pip repaint with the
            // same bitmap, and recycling the one on screen would blank the pose.
            if (session.CurrentImage is { } pose && !ReferenceEquals(pose, displayed))
            {
                image.SetImageBitmap(pose.Bitmap);
                ReleaseDisplayed();
                displayed = pose;

                // Once per pose, never per tick. The samples the guides read were computed on the
                // thread that decoded the image, so this is arithmetic only.
                ApplyGridColors();
            }

            breakOverlay.Visibility = session.OnBreak ? ViewStates.Visible : ViewStates.Gone;

            var paused = session.IsPaused;

            // The sheet follows the *reason*: a lifecycle pause stops the clocks without covering
            // the pose, only the drawer's own pause raises it.
            var pausedByUser = session.PausedByUser;
            pauseOverlay.Visibility = pausedByUser ? ViewStates.Visible : ViewStates.Gone;

            if (pausedByUser)
            {
                // Only path that can make the sheet visible, so its text is written here rather than
                // five times a second in RenderClock.
                pausedTimer.Text = session.Display;
                pausedStats.Text = string.Format(
                    GetString(Resource.String.paused_stats_format),
                    string.Format(GetString(Resource.String.session_progress_format),
                        session.CurrentPoseNumber, session.TargetCount),
                    FormatDuration(session.TotalDrawingTime));
            }

            progressLabel.Text = string.Format(
                GetString(Resource.String.session_progress_format),
                session.CurrentPoseNumber, session.TargetCount);

            stats.Text = string.Format(
                GetString(Resource.String.session_stats_format),
                FormatDuration(session.TotalDrawingTime), session.SkippedCount);

            RenderPips();

            // A full repaint rewrites the clock views unconditionally: the cache is keyed on the
            // string alone, and a phase change can arrive carrying the same one the pose ended on
            // (a done-tap at 0:15 into a 15 s break), which would otherwise leave the break timer
            // showing the layout placeholder. Costs one setText per command, not per tick.
            lastDisplay = null;
            RenderClock();

            if (paused)
            {
                OnClocksStopped();
            }
            else
            {
                StartTicking();

                // Decode the next pose while this one is on screen, so the boundary tick attaches a
                // bitmap instead of decoding one. Costs nothing when the upcoming id is already
                // cached or already in flight, which is most of the times this line runs.
                PrefetchUpcoming();
            }
        }

        // Both clocks have stopped, so no boundary is coming: nothing for a decode to beat, and no
        // reason to hold a full-size bitmap for the length of a pause. The drawer's pause and the
        // lifecycle's reach here alike — one rule, one path.
        //
        // Its own method so the contract tier can pin it: asserted against Render's whole body, this
        // release is indistinguishable from the one on the completion branch. A block body, not an
        // expression one — the contract tier's brace matcher reads bodies, not arrows.
        void OnClocksStopped()
        {
            CancelPrefetch();
        }

        // The cheap per-tick repaint: only the things that change every 200ms. The countdown string
        // is second-resolution, so it is written only when it actually changes — setText on these
        // wrap_content clock views requests a layout pass, and four ticks in five carry no news.
        void RenderClock()
        {
            // ProgressBar.setProgress already no-ops on an unchanged value and never lays out.
            ring.Progress = session.RemainingPercent;

            var display = session.Display;
            if (display == lastDisplay)
                return;

            lastDisplay = display;
            timer.Text = display;

            // The break's own timer gets the tick that entered the break too: Render calls through
            // here on the phase change, when OnBreak is already true.
            if (session.OnBreak)
                breakTimer.Text = display;
        }

        // The session is over: either nothing in the pool could be decoded (an error), or it ran to
        // its end / was ended early (the summary).
        void RenderTerminalState()
        {
            body.Visibility = ViewStates.Gone;

            // Nothing draws the pose from here on. Freeing it now keeps a summary screen from
            // sitting on a full-size bitmap, and keeps "Run it again" from peaking at two — the new
            // session decodes its first image inside its constructor.
            image.SetImageDrawable(null);
            ReleaseDisplayed();

            if (session.CouldNotDisplayImage)
            {
                // Every reachable image failed to decode — show the error rather than a blank screen.
                summary.Visibility = ViewStates.Gone;
                status.Text = GetString(Resource.String.session_error_text);
                status.Visibility = ViewStates.Visible;
                return;
            }

            status.Visibility = ViewStates.Gone;
            summary.Visibility = ViewStates.Visible;

            summaryImages.Text = session.ImagesDisplayed.ToString();
            summaryTime.Text = FormatDuration(session.TotalDrawingTime);
            summaryAverage.Text = FormatDuration(session.AveragePoseTime);
            summarySkipped.Text = session.SkippedCount.ToString();
        }

        // One segment per pose in the session-progress strip, filled as poses are completed. Weighted
        // rather than fixed-width so a long session still fits the rail; past MaxPips the segments
        // would be sub-pixel, so the strip is dropped and the stats line below carries the progress.
        void BuildPips()
        {
            pips.RemoveAllViews();

            if (session.TargetCount > MaxPips)
                return;

            var gap = Resources!.GetDimensionPixelSize(Resource.Dimension.pip_gap);
            var height = Resources.GetDimensionPixelSize(Resource.Dimension.pip_size);

            for (var i = 0; i < session.TargetCount; i++)
            {
                var segment = new View(this);
                var layoutParams = new LinearLayout.LayoutParams(0, height, 1f);
                layoutParams.SetMargins(i == 0 ? 0 : gap, 0, 0, 0);
                segment.LayoutParameters = layoutParams;
                segment.SetBackgroundResource(Resource.Drawable.bg_pip);
                pips.AddView(segment);
            }
        }

        void RenderPips()
        {
            for (var i = 0; i < pips.ChildCount; i++)
                pips.GetChildAt(i)!.Selected = i < session.CompletedCount;
        }

        // Reflect ViewerTools onto the stage: the chips' selected faces and the effects themselves.
        void ApplyTools()
        {
            grayscaleChip.Selected = tools.Grayscale;
            flipChip.Selected = tools.Flip;
            gridChip.Selected = tools.Grid;
            blurChip.Selected = tools.Blur;
            zoomInChip.Enabled = tools.CanZoomIn;
            zoomOutChip.Enabled = tools.CanZoomOut;

            if (tools.Grayscale)
                image.SetColorFilter(GrayscaleFilter);
            else
                image.ClearColorFilter();

            grid.Visibility = tools.Grid ? ViewStates.Visible : ViewStates.Gone;

            // Zoom and flip both move where a guide lands on the pose, so its tone is re-resolved
            // here. This reads the cached samples only — no bitmap work.
            ApplyGridColors();

            // Flip is a negative horizontal scale, so it composes with zoom in one transform.
            var zoom = (float)tools.Zoom;
            image.ScaleX = tools.Flip ? -zoom : zoom;
            image.ScaleY = zoom;

            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                image.SetRenderEffect(tools.Blur
                    ? RenderEffect.CreateBlurEffect(BlurRadius, BlurRadius, Shader.TileMode.Clamp!)
                    : null);
            }
        }

        // Phone: the rail sits under the pose. Fold open / tablet: it becomes a column beside it,
        // which is also where the session-progress strip earns its space.
        void ApplyRailLayout()
        {
            var wide = Resources!.Configuration!.ScreenWidthDp >= WideScreenWidthDp;

            // Fully qualified: Android.Media and Android.Content.Res both also define an Orientation.
            body.Orientation = wide
                ? Android.Widget.Orientation.Horizontal
                : Android.Widget.Orientation.Vertical;

            var stageParams = (LinearLayout.LayoutParams)stage.LayoutParameters!;
            stageParams.Width = wide ? 0 : ViewGroup.LayoutParams.MatchParent;
            stageParams.Height = wide ? ViewGroup.LayoutParams.MatchParent : 0;
            stageParams.Weight = 1f;
            stage.LayoutParameters = stageParams;

            var railParams = (LinearLayout.LayoutParams)rail.LayoutParameters!;
            railParams.Width = wide
                ? Resources.GetDimensionPixelSize(Resource.Dimension.rail_width)
                : ViewGroup.LayoutParams.MatchParent;
            railParams.Height = wide
                ? ViewGroup.LayoutParams.MatchParent
                : ViewGroup.LayoutParams.WrapContent;
            railParams.Weight = 0f;
            rail.LayoutParameters = railParams;

            progressGroup.Visibility = wide ? ViewStates.Visible : ViewStates.Gone;
        }

        // --- Helpers ---------------------------------------------------------

        // Durations read the same everywhere on this screen (m:ss), using the session's own
        // formatter so the summary and the timer can never disagree about how time is written.
        static string FormatDuration(TimeSpan value) =>
            DrawingSession.Format((int)Math.Round(value.TotalSeconds));

        // Resolve an image id for the session. Called on the main thread only — from the running
        // session's constructor, and from a tick or a skip by way of the aggregate's own resolve —
        // which is what lets the one-entry cache be a plain field.
        PoseImage? LoadPose(string id)
        {
            // The prefetch already answered for this id during the pose that has just ended: hand
            // the answer over and empty the cache, so the boundary tick decodes nothing. A null
            // entry is an answer too — the file is unreadable, and the session is about to learn
            // that and remember it for the rest of the run.
            if (prefetchedId == id)
            {
                var ready = prefetched;
                prefetchedId = null;
                prefetched = null;
                return ready;
            }

            // The decode for this id is still running. Waiting for it costs whatever is left of a
            // decode already in progress; starting a second one costs a whole decode on this thread
            // AND leaves the first to be thrown away. The Task was made by Task.Run and captured no
            // synchronization context, so blocking on it here cannot deadlock.
            if (prefetchingId == id && prefetchTask is { } inFlight)
            {
                prefetchClaimed = true;
                return inFlight.GetAwaiter().GetResult();
            }

            // Not prefetched at all — the first pose of a run, or the image after one that was
            // skipped. Nothing to wait for, so decode it here.
            return DecodePose(ContentResolver!, id, CancellationToken.None);
        }

        // Decode a content-uri string into a pose, or null if it is unreadable/broken — the session
        // treats null as "skip this image". Never throws out to the session.
        //
        // Static, and given its resolver rather than reaching for the Activity's: this runs on the
        // prefetch thread, and an instance property would be a JNI call on a peer that teardown may
        // be disposing underneath it.
        static PoseImage? DecodePose(ContentResolver resolver, string id, CancellationToken cancelled)
        {
            // The decode competes with the UI thread for CPU, and a ThreadPool worker starts at
            // normal priority. Restored in the finally: the thread goes back to the pool and will
            // serve work that has nothing to do with this screen.
            var priority = Android.OS.Process.GetThreadPriority(Android.OS.Process.MyTid());

            try
            {
                Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.Background);

                var uri = Android.Net.Uri.Parse(id);
                if (uri is null)
                    return null;

                // Same value twice: on the pose the quality floor and the memory ceiling coincide.
                var bitmap = ImageDecoding.DecodeSampledBitmap(
                    resolver, uri, MaxImageDimension, MaxImageDimension, cancelled);

                if (bitmap is null)
                    return null;

                // Sampled here rather than in Render: this is the thread that already paid for the
                // pixels, and doing it on the repaint callback put a scaled copy plus a GetPixels
                // back onto the UI thread at every pose change.
                return new PoseImage(bitmap, SampleForGuides(bitmap), bitmap.Width, bitmap.Height);
            }
            catch (Exception ex)
            {
                // Boundary with the platform (docs/ARCHITECTURE.md §9). The type alone: a provider's
                // message routinely carries the resolved on-disk path, which must not be logged.
                Log.Warn(LogTag, $"Failed to decode an image: {ex.GetType().Name}");
                return null;
            }
            finally
            {
                Android.OS.Process.SetThreadPriority(priority);
            }
        }

        // Free a bitmap this screen decoded and will not show. Recycle then Dispose: a JNI global
        // ref keeps the pixels alive until a managed GC plus a finalizer pass, far too late at a
        // session's decode rate (docs/ARCHITECTURE.md §8).
        static void Release(PoseImage? pose)
        {
            if (pose?.Bitmap is not { } bitmap)
                return;

            if (!bitmap.IsRecycled)
                bitmap.Recycle();

            bitmap.Dispose();
        }

        // The screen owns the decoded pose (docs/ARCHITECTURE.md §8: a screen disposes what it owns).
        // Nulling the field is what keeps the OnDestroy path and the "Run it again" rebuild from
        // recycling the same bitmap twice.
        void ReleaseDisplayed()
        {
            // The guide samples travel inside the pose, so letting it go drops them with it — a
            // stale block would colour the next pose's guides from the previous pose's image.
            Release(displayed);
            displayed = null;
        }

        // The screen owns what it decoded ahead just as much as what it is showing: an entry the
        // session never asks for is freed here rather than left for a finalizer.
        void ReleasePrefetched()
        {
            Release(prefetched);
            prefetched = null;
            prefetchedId = null;
        }

        static ColorMatrixColorFilter MakeGrayscaleFilter()
        {
            var matrix = new ColorMatrix();
            matrix.SetSaturation(0f);
            return new ColorMatrixColorFilter(matrix);
        }

        // --- Rule-of-thirds guides -------------------------------------------

        // Reduce the pose to a small block of pixels for GridContrast. Filtered scaling makes each
        // cell a box average of the region it stands for, which is what "the strip of image this
        // guide crosses" needs. One rescale per pose, on a path that already paid for a full
        // decode; never on the tick.
        //
        // Null means "could not sample" — the guides then fall back to the light style rather than
        // the screen failing, which is the same outcome as a guide landing on the letterbox bar.
        static int[]? SampleForGuides(Bitmap bitmap)
        {
            Bitmap? scaled = null;

            try
            {
                if (bitmap.IsRecycled)
                    return null;

                const int grid = GridContrast.SampleGrid;

                scaled = Bitmap.CreateScaledBitmap(bitmap, grid, grid, filter: true);
                if (scaled is null)
                    return null;

                var pixels = new int[grid * grid];
                scaled.GetPixels(pixels, 0, grid, 0, 0, grid, grid);
                return pixels;
            }
            catch (Exception ex)
            {
                // Boundary with the platform: log it and carry on with the default guides.
                Log.Warn(LogTag, $"Could not sample the pose for the grid: {ex.GetType().Name}");
                return null;
            }
            finally
            {
                // CreateScaledBitmap hands back the source itself when no scaling was needed, and
                // recycling that would blank the pose.
                if (scaled is not null && !ReferenceEquals(scaled, bitmap))
                {
                    if (!scaled.IsRecycled)
                        scaled.Recycle();

                    scaled.Dispose();
                }
            }
        }

        // Re-resolve every guide's tone against the pose as it is currently presented. Cheap enough
        // to call from a layout change: it is arithmetic over the cached sample block.
        void ApplyGridColors()
        {
            if (gridPainters.Length == 0 || tools is null)
                return;

            // A null array converts to an empty span, and GridContrast reads that as "no samples"
            // and falls back — so an unsampled pose, or none at all, needs no branch here.
            ReadOnlySpan<int> samples = displayed?.GuideSamples;

            var styles = GridContrast.LineStyles(
                samples,
                GridContrast.SampleGrid,
                stage.Width, stage.Height,
                displayed?.Width ?? 0, displayed?.Height ?? 0,
                tools.Zoom, tools.Flip,
                gridPalette);

            gridPainters[0].Apply(styles.VerticalLeft);
            gridPainters[1].Apply(styles.VerticalRight);
            gridPainters[2].Apply(styles.HorizontalTop);
            gridPainters[3].Apply(styles.HorizontalBottom);
        }

        // One guide's background: a casing rectangle with the core laid inside it, inset by the
        // casing width down the guide's two long edges. Two separate layers rather than one stroked
        // drawable, so the tones stay distinct instead of blending where they meet — both are
        // translucent, and a casing drawn over the core would just tint it.
        //
        // The drawables are built once and recoloured in place, so re-resolving on every zoom step
        // allocates nothing.
        sealed class GridLinePainter
        {
            readonly GradientDrawable casing = new();
            readonly GradientDrawable core = new();
            readonly LayerDrawable layers;

            public GridLinePainter(View line, int casingPx, bool vertical)
            {
                layers = new LayerDrawable(new Drawable[] { casing, core });
                layers.SetLayerInset(
                    1,
                    vertical ? casingPx : 0,
                    vertical ? 0 : casingPx,
                    vertical ? casingPx : 0,
                    vertical ? 0 : casingPx);

                line.Background = layers;
            }

            public void Apply(GridLineStyle style)
            {
                casing.SetColor(style.CasingArgb);
                core.SetColor(style.LineArgb);
                layers.InvalidateSelf();
            }
        }
    }
}
