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
    // Exported = false is the platform default for an Activity with no intent filter, stated
    // rather than inherited: the extras this screen trusts assume nothing outside the app can send.
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

        const int TickIntervalMs = 200;

        const int WideScreenWidthDp = 600;

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

        // Not interchangeable with `ticking`, which is also false during the very first build.
        bool resumed;

        Java.Lang.IRunnable tickRunnable = null!;
        bool ticking;

        string? lastDisplay;

        PoseImage? displayed;

        // Unsynchronised on purpose: the prefetch fields below are main-thread-only once a session
        // is running, and the build path that is not (LoadPose, §7) runs with the slot empty.

        // A null image under a non-null id is an answer too: "this file is unreadable".
        string? prefetchedId;
        PoseImage? prefetched;

        string? prefetchingId;
        Task<PoseImage?>? prefetchTask;

        // Set when the boundary took the result straight off the Task: the session owns it, so the
        // continuation must not release or cache it.
        bool prefetchClaimed;

        CancellationTokenSource? prefetchCancel;

        int prefetchGeneration;

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

        public override void OnConfigurationChanged(Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            ApplyRailLayout();
        }

        protected override void OnPause()
        {
            resumed = false;

            // A build in flight has no clock to freeze; it carries the pause forward on publish.
            if (sessionReady)
                session.Pause();

            StopTicking();

            // Earlier than the library load's OnStop, deliberately (§7): a decode nobody will
            // consume must not outlive a screen that may never come back.
            CancelPrefetch();
            base.OnPause();
        }

        protected override void OnResume()
        {
            base.OnResume();
            resumed = true;

            if (!sessionReady)
                return;

            if (session.IsComplete || session.PausedByUser)
                return;

            session.Resume();
            RenderClock();
            StartTicking();

            // Prefetch without a repaint: Render here would rebuild the pips for nothing.
            PrefetchUpcoming();
        }

        protected override void OnDestroy()
        {
            StopTicking();
            CancelPrefetch();
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            tone?.Release();
            tone?.Dispose();
            tone = null;

            if (stage is not null && stageLayoutChanged is not null)
                stage.LayoutChange -= stageLayoutChanged;
            stageLayoutChanged = null;

            image?.SetImageDrawable(null);
            ReleaseDisplayed();

            base.OnDestroy();
        }

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

            // RenderEffect is API 31+; below it the chip would be a control that does nothing.
            if (!OperatingSystem.IsAndroidVersionAtLeast(31))
                blurChip.Visibility = ViewStates.Gone;

            FindViewById<Button>(Resource.Id.summary_again)!.Click += (_, _) => StartSession();
            FindViewById<Button>(Resource.Id.summary_settings)!.Click += (_, _) => Finish();
        }

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

            // Not a one-shot: the stage's size is unknown until layout runs, and a fold changes it.
            stageLayoutChanged = (_, _) => ApplyGridColors();
            stage.LayoutChange += stageLayoutChanged;
        }

        void StartSession()
        {
            // Dropped before the new run starts, or the rebuild peaks at two full-size poses.
            CancelPrefetch();
            sessionReady = false;

            tools = new ViewerTools(startGrayscale);
            lastDisplay = null;

            RenderBuildingState();
            BuildSession(++buildGeneration);
        }

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

                    maxConsecutiveFailures: pool.Length * 2));

                PublishSession(built, generation);
            }
            catch (Exception ex)
            {
                Log.Error(LogTag, $"Building the session failed: {ex}");
            }
        }

        void PublishSession(DrawingSession<PoseImage> built, int generation)
        {
            if (generation != buildGeneration || IsFinishing || IsDestroyed)
            {
                Release(built.CurrentImage);
                return;
            }

            session = built;
            sessionReady = true;

            // The constructor already started the clock, and a screen that is not in the foreground
            // would burn the first pose on it.
            if (!resumed)
                session.Pause();

            BuildPips();
            ApplyTools();
            Render();
        }

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

        void Command(Action command)
        {
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

        void Tick()
        {
            if (!ticking || !sessionReady || IsFinishing || IsDestroyed)
                return;

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

        void StopTicking()
        {
            ticking = false;
            ticker.RemoveCallbacks(tickRunnable);
        }

        // Only the automatic change chimes: whoever tapped Next or Skip is already watching.
        void Chime() => tone?.StartTone(Tone.PropBeep, 120);

        // Called from the tail of a running repaint and from the prefetch's own continuation, so it
        // must be free when the upcoming id is already cached or in flight.
        void PrefetchUpcoming()
        {
            if (!sessionReady || IsFinishing || IsDestroyed || session.IsPaused)
                return;

            if (prefetchTask is not null)
                return;

            var id = session.UpcomingImageId;
            if (id is null)
            {
                ReleasePrefetched();
                return;
            }

            if (id == prefetchedId)
                return;

            // The cached answer landed too late: a boundary consumed that id mid-decode.
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

        async void DecodeAhead(string id, int generation, Task<PoseImage?> work)
        {
            try
            {
                PoseImage? pose = null;

                try
                {
                    pose = await work;
                }
                catch (OperationCanceledException)
                {
                    // Abandoned before it started, so nothing was decoded and nothing needs freeing.
                }
                catch (Exception ex)
                {
                    Log.Warn(LogTag, $"Could not decode ahead: {ex.GetType().Name}");
                }

                prefetchTask = null;
                prefetchingId = null;
                prefetchCancel?.Dispose();
                prefetchCancel = null;

                if (prefetchClaimed)
                {
                    prefetchClaimed = false;
                }
                else if (generation != prefetchGeneration || IsFinishing || IsDestroyed)
                {
                    Release(pose);
                }
                else
                {
                    prefetchedId = id;
                    prefetched = pose;
                }

                PrefetchUpcoming();
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, $"Settling the prefetch failed: {ex.GetType().Name}");
            }
        }

        void CancelPrefetch()
        {
            prefetchGeneration++;

            // Not disposed here: the continuation owns that, and it still has to run.
            prefetchCancel?.Cancel();

            ReleasePrefetched();
        }

        void Render()
        {
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

            // Repoint first, then free (§8). The reference check is load-bearing too: Render re-runs
            // with the same bitmap on every command, and recycling it would blank the pose.
            if (session.CurrentImage is { } pose && !ReferenceEquals(pose, displayed))
            {
                image.SetImageBitmap(pose.Bitmap);
                ReleaseDisplayed();
                displayed = pose;

                ApplyGridColors();
            }

            breakOverlay.Visibility = session.OnBreak ? ViewStates.Visible : ViewStates.Gone;

            var paused = session.IsPaused;

            var pausedByUser = session.PausedByUser;
            pauseOverlay.Visibility = pausedByUser ? ViewStates.Visible : ViewStates.Gone;

            if (pausedByUser)
            {
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

            // Keyed on the string alone, so a phase change carrying the one the pose ended on — a
            // done-tap at 0:15 into a 15s break — would leave the break timer on its placeholder.
            lastDisplay = null;
            RenderClock();

            if (paused)
            {
                OnClocksStopped();
            }
            else
            {
                StartTicking();

                PrefetchUpcoming();
            }
        }

        // Its own method, with a block body, so the contract tier can pin it: inlined it is
        // indistinguishable from the completion branch's release, and the brace matcher reads bodies.
        void OnClocksStopped()
        {
            CancelPrefetch();
        }

        // setText on the wrap_content clock views requests a layout pass, so the string is written
        // only when it changes; ProgressBar.Progress already no-ops on an unchanged value.
        void RenderClock()
        {
            ring.Progress = session.RemainingPercent;

            var display = session.Display;
            if (display == lastDisplay)
                return;

            lastDisplay = display;
            timer.Text = display;

            if (session.OnBreak)
                breakTimer.Text = display;
        }

        void RenderTerminalState()
        {
            body.Visibility = ViewStates.Gone;

            image.SetImageDrawable(null);
            ReleaseDisplayed();

            if (session.CouldNotDisplayImage)
            {
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

            ApplyGridColors();

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

        void ApplyRailLayout()
        {
            var wide = Resources!.Configuration!.ScreenWidthDp >= WideScreenWidthDp;

            // Fully qualified: Android.Media and Android.Content.Res also define an Orientation.
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

        static string FormatDuration(TimeSpan value) =>
            DrawingSession.Format((int)Math.Round(value.TotalSeconds));

        // Main thread for a tick or a skip, but NOT for the running constructor, which resolves its
        // first pose on the build's pool thread (§7) — the one path that reaches these fields off it.
        PoseImage? LoadPose(string id)
        {
            if (prefetchedId == id)
            {
                var ready = prefetched;
                prefetchedId = null;
                prefetched = null;
                return ready;
            }

            if (prefetchingId == id && prefetchTask is { } inFlight)
            {
                prefetchClaimed = true;
                return inFlight.GetAwaiter().GetResult();
            }

            return DecodePose(ContentResolver!, id, CancellationToken.None);
        }

        // Static and handed its resolver: this runs on a pool thread, where an instance property
        // would be a JNI call on a peer teardown may be disposing underneath it.
        static PoseImage? DecodePose(ContentResolver resolver, string id, CancellationToken cancelled)
        {
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

                return new PoseImage(bitmap, SampleForGuides(bitmap), bitmap.Width, bitmap.Height);
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, $"Failed to decode an image: {ex.GetType().Name}");
                return null;
            }
            finally
            {
                Android.OS.Process.SetThreadPriority(priority);
            }
        }

        static void Release(PoseImage? pose)
        {
            if (pose?.Bitmap is not { } bitmap)
                return;

            if (!bitmap.IsRecycled)
                bitmap.Recycle();

            bitmap.Dispose();
        }

        // Nulling the field is what stops OnDestroy and the rebuild recycling the same bitmap
        // twice; the guide samples travel inside the pose and are dropped with it.
        void ReleaseDisplayed()
        {
            Release(displayed);
            displayed = null;
        }

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

        // Filtered scaling on purpose: each cell must be a box average of the region it stands for,
        // so GridContrast can mean the cells a guide's band covers. Null falls back to the light style.
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

        void ApplyGridColors()
        {
            if (gridPainters.Length == 0 || tools is null)
                return;

            // A null array becomes an empty span, which GridContrast reads as "no samples".
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

        // Two layers rather than one stroked drawable: both tones are translucent, so a casing
        // drawn over the core would tint it instead of staying distinct.
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
