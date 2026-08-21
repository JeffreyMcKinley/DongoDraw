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
    // The player screen from the Claude Design mock: the pose on a lightbox stage, a rail with the
    // countdown ring and the viewing tools, a break overlay between poses, a pause sheet, and the
    // end-of-session summary.
    //
    // All of the session's behaviour is in Core. DrawingSession owns which image is up, how long it has
    // left, and the pose/break/complete state machine; ViewerTools owns the grayscale/flip/grid/blur
    // flags and the zoom range. This class does what an Activity is allowed to do: find views, run a
    // repaint loop, render Core's state, forward taps, and manage the lifecycle.
    //
    // NoActionBar theme: the stage is full-bleed, and the default theme's opaque title bar would eat
    // the top of every pose. session_root uses fitsSystemWindows so the content still clears the
    // system bars.
    //
    // ConfigurationChanges: a fold opening or closing is handled in place rather than by recreating
    // the Activity. Recreation would restart the current pose (session state is not persisted -
    // docs/ARCHITECTURE.md §5), which is exactly what a drawer must not lose mid-session.
    // Exported = false is the platform default for an Activity with no intent filter; it is stated
    // rather than inherited because the extras this screen trusts (the pool, the config) are only
    // safe while nothing outside the app can supply them.
    [Activity(
        Label = "@string/app_name",
        Exported = false,
        Theme = "@style/AppTheme.NoActionBar",
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize
            | ConfigChanges.ScreenLayout | ConfigChanges.Orientation | ConfigChanges.KeyboardHidden)]
    public class SessionActivity : Activity
    {
        // Intent extras handed over by MainActivity.StartSession.
        public const string ExtraPool = "pool";            // string[] of content:// uri strings
        public const string ExtraSeconds = "seconds";      // int, seconds per image
        public const string ExtraCount = "count";          // int, images this session shows
        public const string ExtraBreak = "break";          // int, seconds of rest between poses
        public const string ExtraShuffle = "shuffle";      // bool, Settings.ShuffleImages
        public const string ExtraGrayscale = "grayscale";  // bool, Settings.GrayscaleMode
        public const string ExtraKeepAwake = "keepawake";  // bool, Settings.KeepScreenAwake
        public const string ExtraChime = "chime";          // bool, Settings.ChimeOnChange

        const string LogTag = "FigureDrawing";

        // Decode bound for the pose itself: real photos are far larger, and decoding one at full
        // resolution would exhaust memory within a few images.
        const int MaxImageDimension = 1080;

        // Saturation-0 filter applied to the ImageView for grayscale value studies.
        static readonly ColorMatrixColorFilter GrayscaleFilter = MakeGrayscaleFilter();

        // Blur radius for the block-in tool, in px. Only reachable on API 31+ (RenderEffect).
        const float BlurRadius = 24f;

        // Repaint cadence for the countdown. Well under a second so the displayed value flips within
        // a fraction of a second of each boundary; the value itself comes from the clock, so this
        // interval never affects accuracy (a missed tick cannot slow the countdown down).
        const int TickIntervalMs = 200;

        // Width at which the rail moves from under the pose to beside it (fold open / tablet).
        const int WideScreenWidthDp = 600;

        // Above this many poses the progress strip is dropped rather than drawn as slivers.
        const int MaxPips = 40;

        // --- Player views ---
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

        // --- Tool chips ---
        Button grayscaleChip = null!;
        Button flipChip = null!;
        Button gridChip = null!;
        Button blurChip = null!;
        Button zoomInChip = null!;
        Button zoomOutChip = null!;

        // --- Summary views ---
        View summary = null!;
        TextView summaryImages = null!;
        TextView summaryTime = null!;
        TextView summaryAverage = null!;
        TextView summarySkipped = null!;

        DrawingSession<Bitmap> session = null!;
        ViewerTools tools = null!;
        Android.OS.Handler ticker = null!;

        // The repaint callback, held as ONE Runnable instance so it can actually be removed from the
        // Handler queue. Handler.PostDelayed(Action) wraps the delegate in a fresh Java Runnable each
        // call, so RemoveCallbacks(Action) would never match — posting and removing the same stored
        // IRunnable is what guarantees no Tick survives teardown.
        Java.Lang.IRunnable tickRunnable = null!;
        bool ticking;

        // The countdown string currently in the timer views. Display only changes once a second, so
        // caching it keeps four of every five ticks from calling setText and forcing a layout pass.
        string? lastDisplay;

        // The bitmap currently attached to `image`. This screen owns it: nothing else holds a
        // reference, so it must free the pixels when it repoints the view or goes away.
        Bitmap? displayed;

        // --- Rule-of-thirds guides ---
        // One painter per guide, in GridStyles order: the two verticals left to right, then the two
        // horizontals top to bottom.
        GridLinePainter[] gridPainters = Array.Empty<GridLinePainter>();

        // The four colors.xml tokens GridContrast picks between, read once.
        GridPalette gridPalette;

        // The current pose reduced to a SampleGrid x SampleGrid block of pixels, plus the size of
        // the bitmap it came from. Sampled once when the pose changes and then reused, so zooming
        // or flipping re-resolves the guides without touching the bitmap again. Null means "not
        // sampled" — GridContrast treats an empty span as a fallback, so nothing special-cases it.
        int[]? gridSamples;
        int gridSampleWidth;
        int gridSampleHeight;

        // Held so it can be detached in OnDestroy (docs/ARCHITECTURE.md §8).
        EventHandler<View.LayoutChangeEventArgs>? stageLayoutChanged;

        // Session inputs, kept so "Run it again" can rebuild an identical session.
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
            session.Pause();
            StopTicking();
            base.OnPause();
        }

        // Foregrounded again: pick the pose up exactly where it was left. A session that was already
        // paused by the drawer, or already over, stays that way.
        protected override void OnResume()
        {
            base.OnResume();

            // A pause the drawer asked for is remembered by the session itself, so returning from
            // the background cannot restart a pose that was deliberately stopped.
            if (session.IsComplete || session.PausedByUser)
                return;

            session.Resume();
            RenderClock();
            StartTicking();
        }

        protected override void OnDestroy()
        {
            StopTicking();
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
        // with, and paints the first pose.
        void StartSession()
        {
            session = new DrawingSession<Bitmap>(
                pool,
                new SessionConfig(secondsPerImage, imageCount, breakSeconds),
                LoadBitmap,
                shuffle,
                onUnreadable: id => Log.Warn(LogTag, $"Skipping unreadable image {id}"));

            tools = new ViewerTools(startGrayscale);
            lastDisplay = null;

            BuildPips();
            ApplyTools();
            Render();
        }

        // --- Commands --------------------------------------------------------

        // Every pose command goes through here: run it on the Core aggregate, then repaint. The
        // aggregate is what decides whether the command counted, started a break, or ended the run.
        void Command(Action command)
        {
            command();
            Render();
        }

        void PauseSession()
        {
            if (session.IsComplete)
                return;

            session.Pause(PauseReason.User);
            StopTicking();
            Render();
        }

        void ResumeSession()
        {
            if (session.IsComplete)
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
            if (!ticking || IsFinishing || IsDestroyed)
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

        // --- Rendering -------------------------------------------------------

        // Full repaint: which of the three states the screen is in (player, error, summary) and
        // everything inside the current one.
        void Render()
        {
            if (session.IsComplete)
            {
                StopTicking();
                RenderTerminalState();
                return;
            }

            body.Visibility = ViewStates.Visible;
            summary.Visibility = ViewStates.Gone;
            status.Visibility = ViewStates.Gone;

            // Repoint the view FIRST, then free the pixels the view no longer draws. The reference
            // check is load-bearing: Render re-runs on every command, pause and pip repaint with the
            // same bitmap, and recycling the one on screen would blank the pose.
            if (session.CurrentImage is { } bitmap && !ReferenceEquals(bitmap, displayed))
            {
                image.SetImageBitmap(bitmap);
                ReleaseDisplayed();
                displayed = bitmap;

                // Once per pose, never per tick: the guides take their tone from this image, and
                // this branch is the only place that knows it changed.
                gridSamples = SampleForGuides(bitmap);
                gridSampleWidth = bitmap.Width;
                gridSampleHeight = bitmap.Height;
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

            if (!paused)
                StartTicking();
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

        // Decode a content-uri string to a bitmap, or null if it is unreadable/broken — the
        // session treats null as "skip this image". Never throws out to the session.
        Bitmap? LoadBitmap(string id)
        {
            try
            {
                var uri = Android.Net.Uri.Parse(id);
                if (uri is null)
                    return null;

                // Same value twice: on the pose the quality floor and the memory ceiling coincide.
                return ImageDecoding.DecodeSampledBitmap(
                    ContentResolver!, uri, MaxImageDimension, MaxImageDimension);
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, $"Failed to decode {id}: {ex.Message}");
                return null;
            }
        }

        // The screen owns the decoded pose (docs/ARCHITECTURE.md §8: a screen disposes what it owns).
        // Nulling the field is what keeps the OnDestroy path and the "Run it again" rebuild from
        // recycling the same bitmap twice.
        void ReleaseDisplayed()
        {
            // The samples describe the bitmap being let go, so they go with it. A stale block would
            // colour the next pose's guides from the previous pose's image.
            gridSamples = null;
            gridSampleWidth = 0;
            gridSampleHeight = 0;

            if (displayed is null)
                return;

            if (!displayed.IsRecycled)
                displayed.Recycle();

            displayed.Dispose();
            displayed = null;
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
        int[]? SampleForGuides(Bitmap bitmap)
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
                Log.Warn(LogTag, $"Could not sample the pose for the grid: {ex.Message}");
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
            // and falls back — so an unsampled pose needs no branch here.
            ReadOnlySpan<int> samples = gridSamples;

            var styles = GridContrast.LineStyles(
                samples,
                GridContrast.SampleGrid,
                stage.Width, stage.Height,
                gridSampleWidth, gridSampleHeight,
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
