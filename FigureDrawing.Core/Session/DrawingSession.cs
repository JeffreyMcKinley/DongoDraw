using System.Diagnostics;

namespace FigureDrawing.Core;

// Where one session is in its life. Draft is the setup screen (inputs typed, nothing started); Pose
// and Break are the player screen's two halves; Complete is the summary.
public enum SessionPhase
{
    // Inputs are being evaluated on the setup screen. Nothing is running: no pool, no clock.
    Draft,

    // A reference image is on screen and its countdown is draining.
    Pose,

    // The configured rest between two poses. The next image is already loaded underneath; the screen
    // covers it with the break overlay until the rest is over.
    Break,

    // The session is over — completed, ended early, or the pool proved undisplayable.
    Complete
}

// Why a session's clocks were stopped. The screen needs the distinction: a lifecycle pause (the app
// went to the background) resumes itself when the screen comes back, while a pause the drawer asked
// for must survive backgrounding and only end on an explicit Resume.
public enum PauseReason
{
    // The screen was hidden — backgrounded, or covered by another Activity.
    Lifecycle,

    // The drawer tapped Pause.
    User
}

// What one call to Tick() did. The screen used to reconstruct this from the state left behind
// (`if (!session.OnBreak) Chime();`), which put a rule — what counts as a new pose — in an Activity
// where no unit test could reach it (INV-SES-13). A return value rather than a concept with a
// lifetime, so it is not a tenth domain object (docs/DOMAIN-MODEL.md §9).
public enum SessionTick
{
    // Nothing expired: a draft, a complete or a paused session, or a phase with time still on its
    // clock. First so `default(SessionTick)` is "nothing happened".
    None,

    // A reference image is now on screen with a full clock — either a pose expired straight into the
    // next one, or a rest ended.
    PoseStarted,

    // A pose expired into the configured rest. The next pose's image is already loaded underneath.
    BreakStarted,

    // The tick that ended the session: the configured count was reached, or the failure budget ran
    // out. Never reported alongside a new pose.
    Completed
}

// Non-generic partner of DrawingSession<TImage> (same name, different arity) for the one thing
// callers need without an image type: formatting a duration the way the timer reads it.
public static class DrawingSession
{
    // Seconds -> "m:ss" / "h:mm:ss". Static so the screens and tests can format without a session:
    // the setup screen uses it for the "About 12:30 including breaks" estimate and the summary uses
    // it for the total.
    public static string Format(int totalSeconds)
    {
        if (totalSeconds < 0)
            totalSeconds = 0;

        var time = TimeSpan.FromSeconds(totalSeconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }
}

// The session aggregate: one object owning everything about a run of N poses, from the moment the
// setup inputs are first evaluated to the summary the artist reads at the end (docs/DOMAIN-MODEL.md
// §4.1). It owns:
//
//   * the draft (INV-SET-*)  — parsed inputs, the Start gate, the config and the length estimate;
//   * the sequence (INV-SES-*) — pool, passes, shuffle, counts, drawing-time accounting, the break;
//   * the pose clock (INV-CD-*) — remaining time, pause/resume, how it reads on screen;
//   * image resolution (INV-PLY-*) — turning the current image id into something displayable and
//     skipping past unreadable ones under a bounded failure budget;
//   * the totals (INV-SUM-*) — images displayed, drawing time, average pose.
//
// These were six types (SessionSetupState, DrawingSession, SessionPlayer<TImage>, PoseCountdown,
// PoseSession<TImage>, SessionSummary). They are one because each changed only when the session
// advanced and each served exactly one other; see docs/DOMAIN-MODEL.md §9 for the consolidation and
// what deliberately stayed separate.
//
// Generic over the image type so Bitmap never enters Core (docs/ARCHITECTURE.md §4). The screen is
// left with a repaint loop, lifecycle calls, and rendering.
public sealed class DrawingSession<TImage> where TImage : class
{
    // --- Run state: sequence and counts --------------------------------------

    readonly List<string> _pool = [];
    readonly bool _shuffle;
    readonly Random _random = Random.Shared;
    readonly Func<TimeSpan> _now;
    readonly int _targetCount;

    // Upcoming images for the current pass through the pool; refilled (reshuffled) when drained.
    readonly Queue<string> _upcoming = new();

    TimeSpan _accumulatedDrawingTime;

    // Time already banked on the image currently displayed, from run segments that have ended.
    TimeSpan _currentImageBanked;

    // When the current run segment started, or null while the session clock is paused. Paused time
    // is never drawing time: a break between poses, a backgrounded app and an explicit pause all
    // stop this clock (see Pause).
    TimeSpan? _currentImageRunningSince;

    // --- Run state: the pose clock -------------------------------------------

    readonly TimeSpan _poseDuration;
    readonly TimeSpan _breakDuration;

    // How long the phase currently on screen lasts: a pose, or a break.
    TimeSpan _phaseDuration;

    // Countdown time banked from completed run segments (everything before the latest resume).
    TimeSpan _countdownBanked;

    // When the countdown's current run segment started, or null while paused.
    TimeSpan? _countdownRunningSince;

    // Whether the current pause was asked for by the drawer rather than forced by the lifecycle.
    bool _pausedByUser;

    // --- Run state: resolving an id to an image -------------------------------

    readonly Func<string, TImage?> _load = _ => null;
    readonly Action<string>? _onUnreadable;
    readonly int _maxConsecutiveFailures;

    // The draft constructor: parsed inputs and nothing else. INV-SET-4 — this runs on every
    // keystroke, so it copies no pool, starts no clock and walks no tree.
    DrawingSession(int? secondsPerImage, int? imageCount, bool folderSelected, int breakSeconds)
    {
        _now = () => TimeSpan.Zero;
        _maxConsecutiveFailures = 1;

        Phase = SessionPhase.Draft;
        SecondsPerImage = secondsPerImage;
        ImageCount = imageCount;
        FolderSelected = folderSelected;
        BreakSeconds = Math.Max(0, breakSeconds);
    }

    // Evaluates the setup inputs into a draft session. The Android layer calls this on every
    // keystroke to drive the Start button's enabled state, and again on Start to read Config.
    // Parsing is domain logic, not UI logic (INV-SET-1): blank, non-numeric and non-positive input
    // all evaluate to "absent".
    public static DrawingSession<TImage> Evaluate(
        string? secondsText,
        string? countText,
        bool folderSelected,
        int breakSeconds = SessionSetup.DefaultBreakSeconds) =>
        new(SessionSetup.ParsePositive(secondsText),
            SessionSetup.ParsePositive(countText),
            folderSelected,
            breakSeconds);

    // The running constructor. The session is positioned on its first displayable image before it
    // returns, so the screen's first repaint has something to draw.
    //
    // pool    : image ids to draw from (the reference library's pool).
    // config  : validated seconds-per-image + image count + break (the setup screen's output).
    // load    : resolve an image id to a displayable image, or null when it cannot be decoded.
    // shuffle : Settings.ShuffleImages — random order when true, pool order when false.
    // random  : injectable for deterministic tests; defaults to a shared Random.
    // clock   : injectable monotonic time source for deterministic tests; defaults to a Stopwatch.
    // onUnreadable : optional hook to log an image skipped because it would not decode.
    // maxConsecutiveFailures : upper bound on consecutive unreadable images before giving up, so a
    //           folder of all-broken images cannot loop forever (the pool repeats when the
    //           configured count exceeds the pool size).
    public DrawingSession(
        IReadOnlyList<string> pool,
        SessionConfig config,
        Func<string, TImage?> load,
        bool shuffle = true,
        Random? random = null,
        Func<TimeSpan>? clock = null,
        Action<string>? onUnreadable = null,
        int maxConsecutiveFailures = 100)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(load);

        _pool = new List<string>(pool);
        _shuffle = shuffle;
        _random = random ?? Random.Shared;
        _targetCount = Math.Max(0, config.ImageCount);
        _load = load;
        _onUnreadable = onUnreadable;
        _maxConsecutiveFailures = Math.Max(1, maxConsecutiveFailures);

        var stopwatch = Stopwatch.StartNew();
        _now = clock ?? (() => stopwatch.Elapsed);

        SecondsPerImage = config.SecondsPerImage;
        ImageCount = config.ImageCount;
        BreakSeconds = Math.Max(0, config.BreakSeconds);
        FolderSelected = true;

        _poseDuration = TimeSpan.FromSeconds(Math.Max(0, config.SecondsPerImage));
        _breakDuration = TimeSpan.FromSeconds(Math.Max(0, config.BreakSeconds));

        Phase = SessionPhase.Pose;
        RestartCountdown(_poseDuration);

        // Position on the first image (or complete immediately for an empty pool / zero count),
        // then resolve it to something displayable.
        Advance();
        Resolve();
    }

    // --- Draft queries -------------------------------------------------------

    // Which phase the session is in. Draft until it is started; Complete once it is over.
    public SessionPhase Phase { get; private set; }

    // Seconds each pose lasts. Null on a draft whose seconds input is missing or invalid.
    public int? SecondsPerImage { get; }

    // How many poses the session runs. Null on a draft whose count input is missing or invalid.
    public int? ImageCount { get; }

    // Whether a reference library with at least one image is loaded (the third half of the Start
    // gate). Always true on a running session — it could not have started otherwise.
    public bool FolderSelected { get; }

    // The configured rest between poses. Zero means "no break" and is a legal value: unlike the two
    // inputs it never gates Start (INV-SET-2).
    public int BreakSeconds { get; }

    public bool SecondsValid => SecondsPerImage is int s && SessionSetup.IsValidSeconds(s);
    public bool CountValid => ImageCount is int c && SessionSetup.IsValidCount(c);

    // Start is enabled only once a folder is selected AND both inputs are valid (INV-SET-3). The
    // screen binds the button's enabled state to this and decides nothing itself. A session that has
    // already started cannot start again — "run it again" builds a new one (INV-CFG-1).
    public bool CanStart =>
        Phase == SessionPhase.Draft && FolderSelected && SecondsValid && CountValid;

    // The config to hand to a session, or null while the setup is not startable (INV-SET-5) — no
    // partially valid config can be obtained. A session that is already running reports the config
    // it runs under, which was validated before it was built, so the screen never holds a second
    // copy of it.
    public SessionConfig? Config =>
        Phase != SessionPhase.Draft || CanStart
            ? new SessionConfig(SecondsPerImage!.Value, ImageCount!.Value, BreakSeconds)
            : null;

    // Estimated length of the session described by these inputs, in seconds. Unlike Config this is
    // available before the inputs are startable (a missing folder still lets the pace be
    // estimated); it reads 0 while either number is invalid.
    public int EstimateSeconds =>
        SecondsValid && CountValid
            ? SessionSetup.EstimateSeconds(
                new SessionConfig(SecondsPerImage!.Value, ImageCount!.Value, BreakSeconds))
            : 0;

    // --- Run queries: the pose -----------------------------------------------

    // The image to draw right now, or null once the session is over. During a break this is already
    // the *next* pose's image — the screen covers it with the break overlay.
    public TImage? CurrentImage { get; private set; }

    // The id behind CurrentImage. Opaque to the domain (INV-IMG-1): never parsed, split or sorted.
    public string? CurrentImageId { get; private set; }

    public bool IsComplete => Phase == SessionPhase.Complete;

    // True on a break, so the screen knows to show the rest overlay instead of the pose.
    public bool OnBreak => Phase == SessionPhase.Break;

    // The session ended because nothing in the pool could be decoded — an error state, not a normal
    // completion (INV-PLY-4).
    public bool CouldNotDisplayImage { get; private set; }

    // --- Run queries: the clock ----------------------------------------------

    // The full length of the phase on screen: the pose duration, or the break's while resting.
    public TimeSpan PhaseDuration => _phaseDuration;

    // True while paused by the lifecycle (or an explicit pause).
    public bool IsPaused => _countdownRunningSince is null;

    // True while the *drawer* asked for the pause, as opposed to the screen being hidden. Only an
    // explicit Resume clears it, so backgrounding and returning cannot silently restart a pose the
    // drawer stopped (INV-CD-8). The pause sheet is bound to this, not to IsPaused.
    public bool PausedByUser => _pausedByUser;

    // True while time is actually draining (not paused, not expired, not over).
    public bool IsRunning => !IsComplete && !IsPaused && TimeRemaining > TimeSpan.Zero;

    // Time left in the current phase, never negative (INV-CD-4). Computed from the clock rather
    // than decremented by ticks, so a slow or dropped repaint cannot make a pose longer or shorter.
    public TimeSpan TimeRemaining
    {
        get
        {
            var elapsed = _countdownBanked +
                          (_countdownRunningSince is { } since ? _now() - since : TimeSpan.Zero);
            var left = _phaseDuration - elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    // True once the current phase's time is used up — Tick turns this into the next pose.
    public bool IsExpired => TimeRemaining <= TimeSpan.Zero;

    // Whole seconds to display, rounded UP (INV-CD-5): a fresh 30s pose reads "30" immediately and
    // only reads "0" once it has actually expired.
    public int SecondsRemaining => (int)Math.Ceiling(TimeRemaining.TotalSeconds - 1e-6);

    // What the timer view shows: m:ss (or h:mm:ss for the rare hour-long pose).
    public string Display => DrawingSession.Format(SecondsRemaining);

    // How much of the current phase is still to run, 0-100. The progress ring is drawn from this, so
    // it starts full and empties as the pose runs out.
    public int RemainingPercent
    {
        get
        {
            if (_phaseDuration <= TimeSpan.Zero)
                return 0;

            var fraction = TimeRemaining.TotalSeconds / _phaseDuration.TotalSeconds;
            return (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);
        }
    }

    // --- Run queries: counts and totals ---------------------------------------

    // Total number of images this session will count toward completion.
    public int TargetCount => _targetCount;

    // Images completed (counted toward the total). Skips do not increment this.
    public int CompletedCount { get; private set; }

    // Images left without counting: an explicit Skip, or one the loader found unreadable. Reported
    // on the summary screen; never affects CompletedCount or drawing time.
    public int SkippedCount { get; private set; }

    // Images still to complete before the session ends. Never negative (INV-SES-2).
    public int Remaining => Math.Max(0, _targetCount - CompletedCount);

    // Which pose the drawer is on, 1-based, for "Image 3 of 12". Stays at the target once the last
    // pose is running rather than reading one past it.
    public int CurrentPoseNumber => Math.Min(CompletedCount + 1, Math.Max(1, TargetCount));

    // The totals the summary screen reads. ImagesDisplayed is the completed count, so skipped
    // images are absent from it by definition (INV-SUM-2); TotalDrawingTime is banked time only —
    // completed poses plus the final partial pose when the session was ended early — never skipped,
    // break, background or paused time (INV-SUM-3, INV-SES-12).
    public int ImagesDisplayed => CompletedCount;

    public TimeSpan TotalDrawingTime => _accumulatedDrawingTime;

    // Mean time over the poses that counted. Zero for a session that completed nothing, so the
    // summary screen never has to special-case an empty run.
    public TimeSpan AveragePoseTime =>
        CompletedCount > 0
            ? TimeSpan.FromTicks(_accumulatedDrawingTime.Ticks / CompletedCount)
            : TimeSpan.Zero;

    // --- Commands -------------------------------------------------------------

    // Repaint-loop hook: hand the clock a chance to expire the current phase. Safe to call at any
    // cadence — time comes from a monotonic clock, so a slow or dropped tick cannot change how much
    // time a pose gets. Reports which transition it made, so the screen never has to infer one from
    // the state left behind (INV-SES-13).
    public SessionTick Tick()
    {
        if (Phase is SessionPhase.Draft or SessionPhase.Complete || IsPaused || !IsExpired)
            return SessionTick.None;

        if (Phase == SessionPhase.Break)
        {
            StartPose();
            return SessionTick.PoseStarted;
        }

        CompletePose();

        // CompletePose lands in exactly one of three places: the target was reached (or the failure
        // budget ran out inside Resolve), a rest is configured, or the next pose is already up. The
        // phase it left behind is which, so this reads the branch rather than repeating it.
        return Phase switch
        {
            SessionPhase.Complete => SessionTick.Completed,
            SessionPhase.Break => SessionTick.BreakStarted,
            _ => SessionTick.PoseStarted,
        };
    }

    // Timer expiry or a manual "done" tap: count this pose, bank its drawing time, and move on
    // (through the break, if one is configured). No-op once complete (INV-SES-6).
    public void Next()
    {
        if (Phase is SessionPhase.Draft or SessionPhase.Complete)
            return;

        // A done-tap during a rest ends the rest. The image under the break overlay is the *next*
        // pose's, so counting it here would count a pose nobody has drawn yet (INV-SES-10).
        if (Phase == SessionPhase.Break)
        {
            StartPose();
            return;
        }

        CompletePose();
    }

    // Leave this image without counting it and without banking its partial time (INV-SES-3,
    // INV-SES-5). A skip always lands straight on the next pose (INV-SES-11) — resting after an
    // image the drawer did not want is not the point of the break.
    public void Skip()
    {
        if (Phase is SessionPhase.Draft or SessionPhase.Complete)
            return;

        SkipCurrent();
        Resolve();

        if (Phase != SessionPhase.Complete)
            StartPose();
    }

    // End early: bank the current pose's partial time and stop. The current image is NOT counted
    // toward the total (it was not completed). The totals stay readable afterwards.
    public void End()
    {
        if (Phase is SessionPhase.Draft or SessionPhase.Complete)
            return;

        if (CurrentImageId is not null)
            _accumulatedDrawingTime += CurrentElapsed();

        Finish();
        CurrentImage = null;
    }

    // Lifecycle: freeze both clocks while the screen is hidden or the pause overlay is up, so a
    // backgrounded app burns no pose time, cannot fire a timer while it is not on screen, and does
    // not bank the time it spent away as drawing time. Idempotent (INV-CD-3).
    //
    // reason: why the clocks stopped. A User pause is remembered until Resume, so a user pause that
    // is then backgrounded does not come back as a running session (INV-CD-8); a Lifecycle pause
    // never clears a user pause already in effect.
    public void Pause(PauseReason reason = PauseReason.Lifecycle)
    {
        if (Phase is SessionPhase.Draft or SessionPhase.Complete)
            return;

        if (reason == PauseReason.User)
            _pausedByUser = true;

        PauseCountdown();
        PauseSessionClock();
    }

    // Unfreeze both clocks. Idempotent. Resuming an expired phase cannot revive it (elapsed only
    // grows and TimeRemaining clamps at zero) but it does clear IsPaused, so the next Tick retires
    // the pose instead of leaving the screen frozen at 0:00 (INV-CD-3).
    public void Resume()
    {
        if (Phase is SessionPhase.Draft or SessionPhase.Complete)
            return;

        _pausedByUser = false;
        ResumeCountdown();

        // A break runs its own clock; the session's stays stopped until the next pose starts.
        if (Phase != SessionPhase.Break)
            ResumeSessionClock();
    }

    // --- The pose/break state machine ------------------------------------------

    // Count the pose on screen and move to the next image, resting first when a break is configured.
    void CompletePose()
    {
        CountCurrent();
        Resolve();

        if (Phase == SessionPhase.Complete)
            return;

        if (_breakDuration > TimeSpan.Zero)
        {
            Phase = SessionPhase.Break;

            // The sequence has already moved to the next image, so the session clock is running
            // against a pose nobody is drawing yet. Rest is not drawing time — stop it until the
            // break is over (INV-SES-12).
            PauseSessionClock();
            RestartCountdown(_breakDuration);
            return;
        }

        StartPose();
    }

    // Put the next image on screen with a full, running clock (INV-POSE-2, INV-POSE-3, INV-CD-6).
    void StartPose()
    {
        Phase = SessionPhase.Pose;

        // A pose that has just started is not paused, whoever asked for it. Commands reachable from
        // outside the pause sheet (the rail's Next and Skip) land here, and leaving the flag set
        // would strand the sheet over a pose whose clock is already draining (INV-CD-8).
        _pausedByUser = false;

        ResumeSessionClock();
        RestartCountdown(_poseDuration);
    }

    // --- The sequence -----------------------------------------------------------

    // Count the current image toward the total, bank its drawing time, and move on — finishing the
    // session once the target is reached.
    void CountCurrent()
    {
        if (Phase == SessionPhase.Complete || CurrentImageId is null)
            return;

        _accumulatedDrawingTime += CurrentElapsed();
        CompletedCount++;

        if (CompletedCount >= _targetCount)
        {
            Finish();
            return;
        }

        Advance();
    }

    // Move past the current image WITHOUT counting it and WITHOUT banking its partial drawing time.
    void SkipCurrent()
    {
        if (Phase == SessionPhase.Complete || CurrentImageId is null)
            return;

        SkippedCount++;
        Advance();
    }

    // Time spent drawing the image currently displayed: banked segments plus the one still running.
    TimeSpan CurrentElapsed() =>
        _currentImageBanked +
        (_currentImageRunningSince is { } since ? _now() - since : TimeSpan.Zero);

    // Move to the next image and (re)start its timer, or finish if the pool is empty (INV-SES-7).
    void Advance()
    {
        if (_targetCount <= 0 || _pool.Count == 0)
        {
            Finish();
            return;
        }

        if (_upcoming.Count == 0)
            Refill();

        CurrentImageId = _upcoming.Dequeue();
        _currentImageBanked = TimeSpan.Zero;
        _currentImageRunningSince = _now();
    }

    // Rebuild the upcoming queue for a fresh pass through the pool, shuffled when configured. Every
    // image is shown once before any repeat (INV-SES-4).
    void Refill()
    {
        var pass = new List<string>(_pool);

        if (_shuffle)
        {
            // Fisher-Yates with the injected Random for deterministic, unbiased shuffling.
            for (var i = pass.Count - 1; i > 0; i--)
            {
                var j = _random.Next(i + 1);
                (pass[i], pass[j]) = (pass[j], pass[i]);
            }
        }

        foreach (var image in pass)
            _upcoming.Enqueue(image);
    }

    void Finish()
    {
        Phase = SessionPhase.Complete;
        CurrentImageId = null;
        CurrentImage = null;
        _currentImageRunningSince = null;

        // Stop the pose clock as well, so a finished session is unambiguously not running rather
        // than reporting a countdown that keeps draining behind the summary.
        PauseCountdown();
    }

    // --- Resolving an id to a displayable image -----------------------------------

    // Resolve the current image id to something displayable, skipping past unreadable ones until one
    // loads, the session completes on its own, or the failure budget is exhausted (INV-PLY-2,
    // INV-PLY-3). Runs to a decision before returning (INV-PLY-6).
    void Resolve()
    {
        var failures = 0;
        while (Phase != SessionPhase.Complete && CurrentImageId is { } id)
        {
            var image = _load(id);
            if (image is not null)
            {
                CurrentImage = image;
                return;
            }

            _onUnreadable?.Invoke(id);

            if (++failures >= _maxConsecutiveFailures)
            {
                CouldNotDisplayImage = true;
                Finish();
                break;
            }

            SkipCurrent();
        }

        CurrentImage = null;
    }

    // --- The two clocks -------------------------------------------------------------

    void RestartCountdown(TimeSpan duration)
    {
        _phaseDuration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
        _countdownBanked = TimeSpan.Zero;
        _countdownRunningSince = _now();
    }

    void PauseCountdown()
    {
        if (_countdownRunningSince is not { } since)
            return;

        _countdownBanked += _now() - since;
        _countdownRunningSince = null;
    }

    void ResumeCountdown()
    {
        if (_countdownRunningSince is not null)
            return;

        _countdownRunningSince = _now();
    }

    // Stop banking drawing time without leaving the current image. Called for a break between
    // poses, a backgrounded app, and an explicit pause — none of them is time spent drawing.
    void PauseSessionClock()
    {
        if (_currentImageRunningSince is not { } since)
            return;

        _currentImageBanked += _now() - since;
        _currentImageRunningSince = null;
    }

    void ResumeSessionClock()
    {
        if (Phase == SessionPhase.Complete || _currentImageRunningSince is not null)
            return;

        _currentImageRunningSince = _now();
    }
}
