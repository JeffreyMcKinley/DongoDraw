namespace DongoDraw.Tests;

// #8 prevention: structural guards on the boundary between the two Activities. The folder memory
// regression (#8) could be caused by an indirect lifecycle interaction between SessionActivity's
// async session build and MainActivity's persistence chain — even when neither Activity's code
// directly touches the other's state. These tests pin the isolation properties that prevent the
// class of change that could produce such a regression.
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
        Assert.DoesNotContain("DongoDraw.Data.Settings", code, StringComparison.Ordinal);

        // Direct database access bypassing the Settings type.
        Assert.DoesNotContain("new LiteDatabase", code, StringComparison.Ordinal);
    }

    // The using that would pull the Settings type in. A stronger structural guard than the member
    // access check above: even a helper method that accepts Settings as a parameter needs the import.
    [Fact]
    public void SessionActivity_NeverImportsTheSettingsNamespace()
    {
        // Stripped: a comment that mentions the directive (e.g. "do not add using DongoDraw.Data")
        // must not break a build that still behaves. The stripper preserves real using directives.
        Assert.DoesNotContain("using DongoDraw.Data", Session.Code, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"using\s+Settings\s*=", Session.Code);
    }

    // The folder persistence chain — validate → assign → save — must be synchronous end to end
    // (INV-SET-P4). An `await` inside it introduces a point where the continuation may not run
    // before the process dies: the artist swipes the app, the system reclaims it, and the Save never
    // fires. That is what #8 showed cannot exist on the persistence side.
    //
    // Originally this banned `async` anywhere in MainActivity, and named the two ways out if a
    // feature ever needed it: move it to Core, or restructure the chain to save synchronously before
    // the await. #4 needed it — the SAF walk and the preview decodes cannot leave the Android
    // layer, and they were freezing launch — and took the second way out. The screen is async now;
    // the persistence chain still is not, and that is the property worth pinning. The chain's own
    // ordering is asserted in FolderMemoryContractTests.PickingAFolder_PersistsItAsLastCollection.
    [Theory]
    [InlineData("OnActivityResult")]
    [InlineData("OnPause")]
    [InlineData("RestoreLastFolder")]
    public void ThePersistenceChain_IsSynchronous(string method)
    {
        // Stripped source: a mention in a comment is not an await.
        Assert.DoesNotMatch(@"(?<!\w)await\s", Main.MethodBody(method));
    }

    // Async is confined to the library load. Anything else in this screen that starts awaiting is
    // either persistence-adjacent or a rule that belongs in Core, and both want the conversation
    // #8 asked for rather than a quiet second continuation.
    [Fact]
    public void OnlyTheLibraryLoad_IsAsynchronous()
    {
        var declarations = System.Text.RegularExpressions.Regex.Matches(Main.Code, @"(?<!\w)async\s+\w[\w<>?]*\s+(\w+)\s*\(")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(["LoadFolderAsync"], declarations);
    }
}
