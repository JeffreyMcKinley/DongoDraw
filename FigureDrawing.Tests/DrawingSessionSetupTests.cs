using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// The session before it starts: the draft the setup screen is showing (INV-SET-*, INV-CFG-*). The
// Android EditText/Button wiring is exercised e2e; this is the testable core.
public class DrawingSessionSetupTests
{
    static DrawingSession<string> Evaluate(
        string? secondsText, string? countText, bool folderSelected, int breakSeconds = 0) =>
        DrawingSession<string>.Evaluate(secondsText, countText, folderSelected, breakSeconds);

    [Fact]
    public void Evaluate_AllValidAndFolderSelected_CanStartWithConfig()
    {
        var draft = Evaluate("45", "12", folderSelected: true);

        Assert.True(draft.SecondsValid);
        Assert.True(draft.CountValid);
        Assert.True(draft.CanStart);
        Assert.Equal(new SessionConfig(45, 12), draft.Config);
    }

    [Fact]
    public void Evaluate_NoFolder_CannotStartEvenWhenInputsValid()
    {
        var draft = Evaluate("30", "20", folderSelected: false);

        Assert.True(draft.SecondsValid);
        Assert.True(draft.CountValid);
        Assert.False(draft.CanStart);
        Assert.Null(draft.Config);
    }

    [Theory]
    [InlineData("0", "20")]    // seconds invalid
    [InlineData("30", "0")]    // count invalid
    [InlineData("", "20")]     // seconds blank
    [InlineData("30", "")]     // count blank
    [InlineData("x", "y")]     // both non-numeric
    public void Evaluate_InvalidInput_CannotStart(string secondsText, string countText)
    {
        var draft = Evaluate(secondsText, countText, folderSelected: true);

        Assert.False(draft.CanStart);
        Assert.Null(draft.Config);
    }

    [Fact]
    public void Evaluate_ReportsPerFieldValidityIndependently()
    {
        var draft = Evaluate("30", "0", folderSelected: true);

        Assert.True(draft.SecondsValid);
        Assert.False(draft.CountValid);
    }

    [Theory]
    [InlineData("  45  ", 45)]   // parsing is domain logic, not UI logic (INV-SET-1)
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void Evaluate_ParsesTheRawInput(string raw, int? expected)
    {
        var draft = Evaluate(raw, raw, folderSelected: true);

        Assert.Equal(expected, draft.SecondsPerImage);
        Assert.Equal(expected, draft.ImageCount);
    }

    // --- Break between poses -------------------------------------------------

    [Fact]
    public void Evaluate_CarriesTheBreakIntoTheConfig()
    {
        var draft = Evaluate("45", "12", folderSelected: true, breakSeconds: 15);

        Assert.Equal(15, draft.BreakSeconds);
        Assert.Equal(new SessionConfig(45, 12, 15), draft.Config);
    }

    // The break is a pace setting, not a validated input — "no break" is a legitimate choice, so it
    // must never be able to close the Start gate (INV-SET-2).
    [Fact]
    public void Evaluate_NoBreak_StillStarts()
    {
        var draft = Evaluate("45", "12", folderSelected: true, breakSeconds: 0);

        Assert.True(draft.CanStart);
        Assert.Equal(0, draft.Config!.Value.BreakSeconds);
    }

    [Fact]
    public void Evaluate_NegativeBreak_IsFlooredAtZero()
    {
        var draft = Evaluate("45", "12", folderSelected: true, breakSeconds: -30);

        Assert.Equal(0, draft.BreakSeconds);
    }

    [Fact]
    public void Evaluate_OmittedBreak_DefaultsToNone()
    {
        var draft = DrawingSession<string>.Evaluate("45", "12", folderSelected: true);

        Assert.Equal(SessionSetup.DefaultBreakSeconds, draft.BreakSeconds);
    }

    // --- Session length estimate ---------------------------------------------

    // The estimate is shown under the Start button, which is visible before a folder is picked.
    [Fact]
    public void Estimate_IsAvailableBeforeAFolderIsPicked()
    {
        var draft = Evaluate("60", "10", folderSelected: false, breakSeconds: 15);

        Assert.False(draft.CanStart);
        Assert.Equal(735, draft.EstimateSeconds);
    }

    [Fact]
    public void Estimate_IsZeroWhileAnInputIsInvalid()
    {
        Assert.Equal(0, Evaluate("", "10", folderSelected: true).EstimateSeconds);
        Assert.Equal(0, Evaluate("60", "x", folderSelected: true).EstimateSeconds);
    }

    // --- A draft is not a run -------------------------------------------------

    [Fact]
    public void Draft_IsInTheDraftPhase_WithNothingRunning()
    {
        var draft = Evaluate("30", "12", folderSelected: true);

        Assert.Equal(SessionPhase.Draft, draft.Phase);
        Assert.False(draft.IsComplete);
        Assert.False(draft.OnBreak);
        Assert.Null(draft.CurrentImage);
        Assert.Null(draft.CurrentImageId);
        Assert.Equal(0, draft.CompletedCount);
    }

    // Evaluate runs on every keystroke (INV-SET-4), so a draft must never start a session by
    // accident: the pose commands do nothing until one is constructed with a pool.
    [Fact]
    public void Draft_IgnoresEveryPoseCommand()
    {
        var draft = Evaluate("30", "12", folderSelected: true);

        draft.Next();
        draft.Skip();
        draft.End();
        draft.Pause();
        draft.Resume();

        Assert.Equal(SessionTick.None, draft.Tick());
        Assert.Equal(SessionPhase.Draft, draft.Phase);
        Assert.Equal(0, draft.CompletedCount);
        Assert.Equal(0, draft.SkippedCount);
        Assert.False(draft.IsComplete);

        // A draft has no loader, so a command that leaked through to image resolution would show up
        // here as an error state rather than as silence.
        Assert.Null(draft.CurrentImage);
        Assert.False(draft.CouldNotDisplayImage);
    }

    // Evaluation is pure and total (INV-SET-4): the same inputs always evaluate the same way, and no
    // input string throws.
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("!!", "\t")]
    [InlineData("999999999999999999999", "0")]
    public void Evaluate_NeverThrows(string? secondsText, string? countText)
    {
        var first = Evaluate(secondsText, countText, folderSelected: true);
        var second = Evaluate(secondsText, countText, folderSelected: true);

        Assert.Equal(first.CanStart, second.CanStart);
        Assert.Equal(first.SecondsPerImage, second.SecondsPerImage);
        Assert.Equal(first.EstimateSeconds, second.EstimateSeconds);
    }

    // A running session reports the config it is running under, so the screen never has to keep a
    // second copy of the inputs (INV-CFG-1: a config never changes mid-session).
    [Fact]
    public void RunningSession_ReportsItsOwnConfig()
    {
        var session = new DrawingSession<string>(
            ["a", "b"], new SessionConfig(45, 12, 15), id => id,
            shuffle: false, random: new Random(1), clock: () => TimeSpan.Zero);

        Assert.Equal(SessionPhase.Pose, session.Phase);
        Assert.Equal(45, session.SecondsPerImage);
        Assert.Equal(12, session.ImageCount);
        Assert.Equal(15, session.BreakSeconds);
        Assert.Equal(new SessionConfig(45, 12, 15), session.Config);

        // ...but it cannot start again. Changing a setting means a new session (INV-CFG-1), so the
        // Start gate is a draft's question, and a running or finished session answers it false.
        Assert.False(session.CanStart);

        session.End();
        Assert.False(session.CanStart);
    }
}
