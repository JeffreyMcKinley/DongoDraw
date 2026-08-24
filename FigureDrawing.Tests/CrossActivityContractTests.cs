namespace FigureDrawing.Tests;

// FD-013 prevention: structural guards on the boundary between the two Activities. The folder memory
// regression (FD-013) could be caused by an indirect lifecycle interaction between SessionActivity's
// async session build and MainActivity's persistence chain — even when neither Activity's code
// directly touches the other's state. These tests pin the isolation properties that prevent the
// class of change that could produce such a regression.
//
// The tier is contract (docs/ARCHITECTURE.md §11): read both Activities as files via SourceContract,
// assert on API names and structural properties, never on spelling or formatting. Comments and
// string literals are stripped (SourceContract.Code) so a comment cannot satisfy an assertion.
public sealed class CrossActivityContractTests
{
    static readonly SourceContract Main = new("MainActivity.cs");
    static readonly SourceContract Session = new("SessionActivity.cs");

    // Settings is exclusively MainActivity's concern (INV-STO-1, INV-SET-P4). SessionActivity
    // receives the preferences it needs as intent extras (SessionActivity §ExtraPool..ExtraChime)
    // and never opens, reads, or writes the database. This is what makes the two Activities
    // lifecycle-independent for persistence: a background thread in SessionActivity cannot corrupt,
    // race, or interfere with a Settings write in MainActivity.
    //
    // Without this test, adding `settings.Save()` inside SessionActivity's async build would compile
    // and pass every contract test — and introduce a concurrent-write race that silently drops the
    // folder preference under memory pressure.
    [Fact]
    public void SessionActivity_NeverTouchesSettings()
    {
        var code = Session.Code;

        // Instance field access (lowercase settings.Anything).
        Assert.DoesNotMatch(@"(?<!\w)settings\.\w", code);

        // Static access through the type name, and fully qualified access that would bypass the
        // using-import check below.
        Assert.DoesNotContain("Settings.Open", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Save", code, StringComparison.Ordinal);
        Assert.DoesNotContain("FigureDrawing.Data.Settings", code, StringComparison.Ordinal);

        // Direct database access bypassing the Settings type.
        Assert.DoesNotContain("new LiteDatabase", code, StringComparison.Ordinal);
    }

    // The using that would pull the Settings type in. A stronger structural guard than the member
    // access check above: even a helper method that accepts Settings as a parameter needs the import.
    [Fact]
    public void SessionActivity_NeverImportsTheSettingsNamespace()
    {
        // Stripped: a comment that mentions the directive (e.g. "do not add using FigureDrawing.Data")
        // must not break a build that still behaves. The stripper preserves real using directives.
        Assert.DoesNotContain("using FigureDrawing.Data", Session.Code, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"using\s+Settings\s*=", Session.Code);
    }

    // The folder persistence chain — pick → save → restore — must be synchronous end to end
    // (INV-SET-P4). An `async` method in MainActivity introduces a point where the continuation may
    // not run before the process dies: the artist swipes the app, the system reclaims it, and the
    // await's continuation (which includes the Save) never fires. This is exactly the class of change FD-010 introduced
    // in SessionActivity (where it is correct — session construction is not persistence-critical) and
    // that FD-013 showed cannot exist on the persistence side.
    //
    // If a future feature genuinely needs async in MainActivity, this test forces the conversation:
    // the developer must move the feature to Core (where it can be tested without the lifecycle) or
    // restructure the persistence chain to save synchronously before the await.
    [Fact]
    public void MainActivity_HasNoAsyncMethods()
    {
        // Stripped source: a mention in a comment is not a method declaration.
        Assert.DoesNotMatch(@"(?<!\w)async\s", Main.Code);
    }
}
