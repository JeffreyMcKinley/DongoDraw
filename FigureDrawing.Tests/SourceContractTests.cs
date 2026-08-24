namespace FigureDrawing.Tests;

// The tool the contract tier is built on (docs/ARCHITECTURE.md §11). Three consumer files
// (FolderMemoryContractTests, CrossActivityContractTests, SessionScreenContractTests) depend on it
// reading the right span of the right file, and a brace matcher that
// silently returns the wrong method is worse than no assertion at all — it reports green about code
// it never looked at. Exercised against fixture strings rather than against a real Activity, so
// refactoring the app cannot fail the test that validates the tool.
public class SourceContractTests
{
    // --- Blanking comments and literals --------------------------------------

    [Fact]
    public void TheStripper_RemovesCommentsAndLiterals()
    {
        const string fixture =
            "var a = Keep(); // Dropped()\n" +
            "/* Dropped() */ var b = \"Dropped()\";\n" +
            "var c = @\"Dropped() \"\" still dropped\";\n" +
            "var d = $\"{Kept()} Dropped()\";\n" +
            "var e = '}';\n";

        var stripped = SourceContract.StripCommentsAndLiterals(fixture);

        Assert.Equal(fixture.Length, stripped.Length);
        Assert.Equal(fixture.Count(c => c == '\n'), stripped.Count(c => c == '\n'));
        Assert.DoesNotContain("Dropped()", stripped, StringComparison.Ordinal);
        Assert.Contains("Keep()", stripped, StringComparison.Ordinal);

        // Code inside an interpolation hole goes with the literal, deliberately: a method named
        // inside a log message is not wiring, and treating it as code is how an assertion ends up
        // satisfied by a diagnostic string.
        Assert.DoesNotContain("Kept()", stripped, StringComparison.Ordinal);

        // A brace inside a literal must never reach the brace matcher.
        Assert.DoesNotContain("}'", stripped, StringComparison.Ordinal);
    }

    // --- Reading one method's body -------------------------------------------

    static SourceContract Fixture(string body) => SourceContract.ForTesting("Fixture.cs", body);

    [Fact]
    public void MethodBody_ReadsTheDeclarationItWasAskedFor()
    {
        var source = Fixture(
            "class C\n{\n" +
            "    void First()\n    {\n        Alpha();\n    }\n\n" +
            "    void Second()\n    {\n        Beta();\n    }\n}\n");

        Assert.Contains("Alpha();", source.MethodBody("First"), StringComparison.Ordinal);
        Assert.DoesNotContain("Beta();", source.MethodBody("First"), StringComparison.Ordinal);
    }

    // Brace matching, not "up to the next closing brace": a body with nested blocks must come back
    // whole, or an assertion about the tail of a method silently reads nothing.
    [Fact]
    public void MethodBody_MatchesNestedBraces()
    {
        var source = Fixture(
            "class C\n{\n    void Only()\n    {\n        if (x)\n        {\n            Inner();\n" +
            "        }\n\n        Tail();\n    }\n}\n");

        var body = source.MethodBody("Only");

        Assert.Contains("Inner();", body, StringComparison.Ordinal);
        Assert.Contains("Tail();", body, StringComparison.Ordinal);
    }

    // A call is not a declaration. Without this the matcher could anchor on the first mention of the
    // name and read whatever block happened to follow it.
    [Fact]
    public void MethodBody_IsNotRetargetedByACallToTheSameName()
    {
        var source = Fixture(
            "class C\n{\n    void Caller()\n    {\n        Target();\n    }\n\n" +
            "    void Target()\n    {\n        Real();\n    }\n}\n");

        Assert.Contains("Real();", source.MethodBody("Target"), StringComparison.Ordinal);
    }

    // A brace inside a comment or a string is blanked before matching, so it cannot end a body
    // early — the reason the stripper runs first.
    [Fact]
    public void MethodBody_IgnoresBracesInsideCommentsAndLiterals()
    {
        var source = Fixture(
            "class C\n{\n    void Only()\n    {\n        // }\n        var s = \"}\";\n" +
            "        Tail();\n    }\n}\n");

        Assert.Contains("Tail();", source.MethodBody("Only"), StringComparison.Ordinal);
    }

    // The failure has to be loud and name the file: a contract test whose target was renamed must
    // say so rather than assert against an empty string and pass.
    [Fact]
    public void MethodBody_SaysWhichFileIsMissingTheMethod()
    {
        var source = Fixture("class C\n{\n    void Only()\n    {\n    }\n}\n");

        var failure = Assert.ThrowsAny<Exception>(() => source.MethodBody("Absent"));

        Assert.Contains("Fixture.cs", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Absent", failure.Message, StringComparison.Ordinal);
    }

    // An expression-bodied member has no block to read, so the matcher refuses it rather than
    // reading the next method's body by mistake. Anything the contract tier needs to assert on must
    // therefore be written with a block body.
    [Fact]
    public void MethodBody_RefusesAnExpressionBodiedMember()
    {
        var source = Fixture(
            "class C\n{\n    void Arrow() => Something();\n\n" +
            "    void Block()\n    {\n        Other();\n    }\n}\n");

        Assert.ThrowsAny<Exception>(() => source.MethodBody("Arrow"));
    }

    // CRLF is what a Windows checkout produces, and the declaration matcher anchors on end-of-line:
    // without normalisation `\r` sits between the `)` and the `$`, no declaration matches, and every
    // body-sliced assertion in the suite fails against code that is perfectly correct.
    [Fact]
    public void MethodBody_ReadsACrlfFile()
    {
        var source = Fixture("class C\r\n{\r\n    void Only()\r\n    {\r\n        Tail();\r\n    }\r\n}\r\n");

        Assert.Contains("Tail();", source.MethodBody("Only"), StringComparison.Ordinal);
    }
}
