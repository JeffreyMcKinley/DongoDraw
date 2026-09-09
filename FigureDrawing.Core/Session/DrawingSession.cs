using System.Diagnostics;

namespace FigureDrawing.Core;

public enum SessionPhase
{
    Draft,
    Pose,
    Break,
    Complete
}

public enum PauseReason
{
    Lifecycle,
    User
}

public enum SessionTick
{
    // First, so default(SessionTick) and a missed switch arm both read as "nothing happened".
    None,
    PoseStarted,
    BreakStarted,
    Completed
}

public static class DrawingSession
{
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

public sealed class DrawingSession<TImage> where TImage : class
{
    readonly List<string> _pool = [];
    readonly bool _shuffle;
    readonly Random _random = Random.Shared;
    readonly Func<TimeSpan> _now;
    readonly int _targetCount;

    readonly Queue<string> _upcoming = new();

    TimeSpan _accumulatedDrawingTime;

    TimeSpan _currentImageBanked;

    TimeSpan? _currentImageRunningSince;

    readonly TimeSpan _poseDuration;
    readonly TimeSpan _breakDuration;

    TimeSpan _phaseDuration;

    TimeSpan _countdownBanked;

    TimeSpan? _countdownRunningSince;

    bool _pausedByUser;

    readonly Func<string, TImage?> _load = _ => null;
    readonly Action<string>? _onUnreadable;
    readonly int _maxConsecutiveFailures;

    readonly HashSet<string> _unreadable = [];

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

    public static DrawingSession<TImage> Evaluate(
        string? secondsText,
        string? countText,
        bool folderSelected,
        int breakSeconds = SessionSetup.DefaultBreakSeconds) =>
        new(SessionSetup.ParsePositive(secondsText),
            SessionSetup.ParsePositive(countText),
            folderSelected,
            breakSeconds);

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

        Advance();
        Resolve();
    }

    public SessionPhase Phase { get; private set; }

    public int? SecondsPerImage { get; }

    public int? ImageCount { get; }

    public bool FolderSelected { get; }

    public int BreakSeconds { get; }

    public bool SecondsValid => SecondsPerImage is int s && SessionSetup.IsValidSeconds(s);
    public bool CountValid => ImageCount is int c && SessionSetup.IsValidCount(c);

    public bool CanStart =>
        Phase == SessionPhase.Draft && FolderSelected && SecondsValid && CountValid;

    public SessionConfig? Config =>
        Phase != SessionPhase.Draft || CanStart
            ? new SessionConfig(SecondsPerImage!.Value, ImageCount!.Value, BreakSeconds)
            : null;

    public int EstimateSeconds =>
        SecondsValid && CountValid
            ? SessionSetup.EstimateSeconds(
                new SessionConfig(SecondsPerImage!.Value, ImageCount!.Value, BreakSeconds))
            : 0;

    public TImage? CurrentImage { get; private set; }

    public string? CurrentImageId { get; private set; }

    public string? UpcomingImageId
    {
        get
        {
            if (Phase is SessionPhase.Draft or SessionPhase.Complete
                || _targetCount <= 0 || _pool.Count == 0)
                return null;

            if (_upcoming.Count == 0)
                Refill();

            // Enumerating a Queue walks it head-first without consuming: that is the peek.
            foreach (var id in _upcoming)
            {
                if (!_unreadable.Contains(id))
                    return id;
            }

            return null;
        }
    }

    public bool IsComplete => Phase == SessionPhase.Complete;

    public bool OnBreak => Phase == SessionPhase.Break;

    public bool CouldNotDisplayImage { get; private set; }

    public TimeSpan PhaseDuration => _phaseDuration;

    public bool IsPaused => _countdownRunningSince is null;

    public bool PausedByUser => _pausedByUser;

    public bool IsRunning => !IsComplete && !IsPaused && TimeRemaining > TimeSpan.Zero;

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

    public bool IsExpired => TimeRemaining <= TimeSpan.Zero;

    public int SecondsRemaining => (int)Math.Ceiling(TimeRemaining.TotalSeconds - 1e-6);

    public string Display => DrawingSession.Format(SecondsRemaining);

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

    public int TargetCount => _targetCount;

    public int CompletedCount { get; private set; }

    public int SkippedCount { get; private set; }

    public int Remaining => Math.Max(0, _targetCount - CompletedCount);

    public int CurrentPoseNumber => Math.Min(CompletedCount + 1, Math.Max(1, TargetCount));

    public int ImagesDisplayed => CompletedCount;

    public TimeSpan TotalDrawingTime => _accumulatedDrawingTime;

    public TimeSpan AveragePoseTime =>
        CompletedCount > 0
            ? TimeSpan.FromTicks(_accumulatedDrawingTime.Ticks / CompletedCount)
            : TimeSpan.Zero;

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

        return Phase switch
        {
            SessionPhase.Complete => SessionTick.Completed,
            SessionPhase.Break => SessionTick.BreakStarted,
            _ => SessionTick.PoseStarted,
        };
    }

    public void Next()
    {
        if (Phase is SessionPhase.Draft or SessionPhase.Complete)
            return;

        // The image under the break overlay is the NEXT pose's, so counting it here would count a
        // pose nobody has drawn yet (INV-SES-10).
        if (Phase == SessionPhase.Break)
        {
            StartPose();
            return;
        }

        CompletePose();
    }

    public void Skip()
    {
        if (Phase is SessionPhase.Draft or SessionPhase.Complete)
            return;

        SkipCurrent();
        Resolve();

        if (Phase != SessionPhase.Complete)
            StartPose();
    }

    public void End()
    {
        if (Phase is SessionPhase.Draft or SessionPhase.Complete)
            return;

        if (CurrentImageId is not null)
            _accumulatedDrawingTime += CurrentElapsed();

        Finish();
        CurrentImage = null;
    }

    public void Pause(PauseReason reason = PauseReason.Lifecycle)
    {
        if (Phase is SessionPhase.Draft or SessionPhase.Complete)
            return;

        if (reason == PauseReason.User)
            _pausedByUser = true;

        PauseCountdown();
        PauseSessionClock();
    }

    public void Resume()
    {
        if (Phase is SessionPhase.Draft or SessionPhase.Complete)
            return;

        _pausedByUser = false;
        ResumeCountdown();

        if (Phase != SessionPhase.Break)
            ResumeSessionClock();
    }

    void CompletePose()
    {
        CountCurrent();
        Resolve();

        if (Phase == SessionPhase.Complete)
            return;

        if (_breakDuration > TimeSpan.Zero)
        {
            Phase = SessionPhase.Break;

            // The sequence has already advanced, so this clock is running against a pose nobody is
            // drawing yet, and rest is not drawing time (INV-SES-12).
            PauseSessionClock();
            RestartCountdown(_breakDuration);
            return;
        }

        StartPose();
    }

    void StartPose()
    {
        Phase = SessionPhase.Pose;

        _pausedByUser = false;

        ResumeSessionClock();
        RestartCountdown(_poseDuration);
    }

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

    void SkipCurrent()
    {
        if (Phase == SessionPhase.Complete || CurrentImageId is null)
            return;

        SkippedCount++;
        Advance();
    }

    TimeSpan CurrentElapsed() =>
        _currentImageBanked +
        (_currentImageRunningSince is { } since ? _now() - since : TimeSpan.Zero);

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

    void Refill()
    {
        var pass = new List<string>(_pool);

        if (_shuffle)
        {
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

        // The countdown too, or it keeps draining behind the summary (§4.1).
        PauseCountdown();
    }

    void Resolve()
    {
        var failures = 0;
        while (Phase != SessionPhase.Complete && CurrentImageId is { } id)
        {
            // Known unreadable: skip the load, not the consequences — it still travels the skip
            // path and still counts against the budget (INV-PLY-8).
            if (!_unreadable.Contains(id))
            {
                var image = _load(id);
                if (image is not null)
                {
                    CurrentImage = image;
                    return;
                }

                _unreadable.Add(id);
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
