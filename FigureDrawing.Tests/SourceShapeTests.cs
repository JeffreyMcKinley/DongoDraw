namespace FigureDrawing.Tests;

// The reader behind the source-shape contract tests (docs/ARCHITECTURE.md §11).
//
// It is tested directly because two suites now depend on it: a regression in the stripper would
// quietly change what every assertion in FolderMemoryContractTests and LibraryLoadContractTests
// means, and both would stay green while pinning nothing. The consumers' own smoke check can only
// say "MainActivity still looks stripped"; these say what the rules are.
public class SourceShapeTests
{
    static string Strip(string source) => SourceShape.StripCommentsAndLiterals(source);

    static SourceShape Written(string source) => SourceShape.Of(source);

    // Offsets have to line up with the original file, because the brace matcher indexes into the
    // stripped text and reports positions the reader compares against the real source.
    [Theory]
    [InlineData("var a = \"text\";")]
    [InlineData("// a comment\nvar a = 1;")]
    [InlineData("/* block */ var a = 1;")]
    [InlineData("var a = @\"ver\"\"batim\";")]
    [InlineData("var c = '{';")]
    public void Stripping_PreservesLengthAndLines(string source)
    {
        var stripped = Strip(source);

        Assert.Equal(source.Length, stripped.Length);
        Assert.Equal(source.Count(c => c == '\n'), stripped.Count(c => c == '\n'));
    }

    // The whole point: a brace inside a literal or a comment must not reach the brace matcher, or a
    // method body is measured to the wrong closing brace.
    [Theory]
    [InlineData("var a = \"{\";")]
    [InlineData("var a = '}';")]
    [InlineData("// {\n")]
    [InlineData("/* } */")]
    [InlineData("var a = @\"{\";")]
    [InlineData("var a = \"\\\"{\";")]
    public void Stripping_LeavesNoBraceFromInsideALiteralOrComment(string source)
    {
        Assert.DoesNotContain('{', Strip(source));
        Assert.DoesNotContain('}', Strip(source));
    }

    // Code outside literals is untouched — an assertion that an API is reached has to still see it.
    [Fact]
    public void Stripping_KeepsCode()
    {
        var stripped = Strip("Log.Warn(LogTag, \"secret\"); // note\n");

        Assert.Contains("Log.Warn(LogTag,", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("note", stripped, StringComparison.Ordinal);
    }

    // An interpolation hole is code to the compiler but is blanked here, and that is deliberate: the
    // brace matcher cannot tell $"{x}" apart from a block. Assertions must not rely on reading it.
    [Fact]
    public void Stripping_BlanksInterpolationHolesAndTheirBraces()
    {
        var stripped = Strip("var s = $\"{count} images\";");

        Assert.DoesNotContain('{', stripped);
        Assert.DoesNotContain("count", stripped, StringComparison.Ordinal);
    }

    // --- Reading a method body -------------------------------------------------

    // The reader is only useful if it finds the *declaration*. A wrapped signature has to match
    // (LoadAsync's does), and a call site must not — the relaxed parameter regex admits a
    // statement-leading wrapped call, so the block-body requirement is what excludes it.
    [Fact]
    public void MethodBody_FindsAWrappedDeclarationAndNotACallSite()
    {
        var source = Written("""
            void Caller()
            {
                Target(
                    1);
            }

            void Target(
                int value)
            {
                Body();
            }
            """);

        Assert.Contains("Body();", source.MethodBody("Target"), StringComparison.Ordinal);
        Assert.DoesNotContain("Target(", source.MethodBody("Target"), StringComparison.Ordinal);
    }

    // Two methods can share a body, so a caller asking "is this call inside that method?" has to be
    // given offsets rather than left to search for the body's text.
    [Fact]
    public void MethodBodySpan_DistinguishesIdenticalBodies()
    {
        var source = Written("""
            void First()
            {
                Shared();
            }

            void Second()
            {
                Shared();
            }
            """);

        var first = source.MethodBodySpan("First");
        var second = source.MethodBodySpan("Second");

        Assert.NotEqual(first.Start, second.Start);
        Assert.True(first.Start < second.Start);
    }

    // --- Reading the block a guard opens ---------------------------------------

    [Fact]
    public void BlockAfter_ReadsTheGuardsOwnBlock()
    {
        const string body = "if (ready) { Inside(); } Outside();";

        var block = SourceShape.BlockAfter(body, body.IndexOf(')') + 1);

        Assert.Contains("Inside();", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Outside();", block, StringComparison.Ordinal);
    }

    // The trap this helper was written around: a property pattern's braces are not a block, and
    // silently returning them would make every "inside the branch" assertion read the wrong code.
    [Fact]
    public void BlockAfter_RefusesAPatternsBracesAndABracelessBody()
    {
        const string pattern = "if (x is not { } y) { Inside(); }";
        Assert.ThrowsAny<Exception>(() => SourceShape.BlockAfter(pattern, pattern.IndexOf("is", StringComparison.Ordinal)));

        const string braceless = "if (ready) Inside(); void Other() { Elsewhere(); }";
        Assert.ThrowsAny<Exception>(() => SourceShape.BlockAfter(braceless, braceless.IndexOf(')') + 1));
    }

    // Raw string literals are not parsed as such — the stripper reads """a { b""" as an empty
    // string, then a plain one, then another empty one. The braces still come out blanked, which is
    // what the brace matcher needs, so this is safe by accident rather than by design. Pinned so a
    // future change to the stripper cannot quietly turn "safe by accident" into a wrong body.
    [Fact]
    public void Stripping_BlanksBracesInsideARawStringLiteral()
    {
        var stripped = Strip("var s = \"\"\"a { b\"\"\";");

        Assert.DoesNotContain('{', stripped);
        Assert.Equal("var s = \"\"\"a { b\"\"\";".Length, stripped.Length);
    }
}
