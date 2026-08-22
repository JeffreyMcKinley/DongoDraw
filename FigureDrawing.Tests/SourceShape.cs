using System.Text;
using System.Text.RegularExpressions;

namespace FigureDrawing.Tests;

// One Android-layer source file, read as text, for the source-shape contract tests
// (docs/ARCHITECTURE.md §11). Shared rather than duplicated: FD-009 split folder loading out of
// MainActivity, so two suites now read two different files with the same machinery.
//
// Comments and string literals are blanked before anything is asserted: a tier whose assertions a
// comment could satisfy would stay green through the exact deletion it exists to catch. What is
// asserted is which API is reached from which method, never how a statement is spelled — a rename
// or a reformat must not fail a build that still behaves.
internal sealed class SourceShape
{
    public SourceShape(params string[] pathParts)
    {
        FileName = pathParts[^1];

        // Line endings are normalised on the way in. core.autocrlf is on for this repo, so a Windows
        // checkout hands these files CRLF while the committed bytes are LF — and .NET's multiline $
        // anchors before \n only, never before \r\n, so every declaration regex below would silently
        // stop matching on one developer's machine and match on another's.
        Source = File.ReadAllText(TestPaths.Path(pathParts)).Replace("\r\n", "\n");
        Code = StripCommentsAndLiterals(Source);
    }

    SourceShape(string fileName, string source)
    {
        FileName = fileName;
        Source = source.Replace("\r\n", "\n");
        Code = StripCommentsAndLiterals(Source);
    }

    // A reader over literal source rather than a file, so SourceShapeTests can exercise the reading
    // machinery against inputs it controls instead of against whatever MainActivity happens to say.
    public static SourceShape Of(string source) => new("<literal>", source);

    // The file's name, for assertion messages.
    public string FileName { get; }

    // The file as written.
    public string Source { get; }

    // The file with every comment, string and char literal blanked out, positions preserved.
    public string Code { get; }

    // Replaces the contents of comments and literals with spaces, keeping the length and the line
    // breaks so offsets still line up with the original file. Blanking rather than deleting also
    // keeps a brace inside a string or an interpolation hole out of the brace matcher below.
    public static string StripCommentsAndLiterals(string source)
    {
        var output = new StringBuilder(source.Length);
        var i = 0;

        void Blank(char c) => output.Append(c == '\n' ? '\n' : ' ');

        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                    Blank(source[i++]);
                continue;
            }

            if (c == '/' && next == '*')
            {
                Blank(source[i++]);
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                    Blank(source[i++]);

                for (var end = 0; end < 2 && i < source.Length; end++)
                    Blank(source[i++]);

                continue;
            }

            if (c is '"' or '\'')
            {
                // A verbatim string ends on a quote that is not doubled; every other literal ends on
                // the first unescaped closing quote.
                var verbatim = c == '"' && output.Length > 0 && Verbatim(output);
                var quote = c;

                Blank(source[i++]);

                while (i < source.Length)
                {
                    if (verbatim)
                    {
                        if (source[i] == quote && i + 1 < source.Length && source[i + 1] == quote)
                        {
                            Blank(source[i++]);
                            Blank(source[i++]);
                            continue;
                        }

                        if (source[i] == quote)
                            break;
                    }
                    else
                    {
                        if (source[i] == '\\' && i + 1 < source.Length)
                        {
                            Blank(source[i++]);
                            Blank(source[i++]);
                            continue;
                        }

                        if (source[i] == quote || source[i] == '\n')
                            break;
                    }

                    Blank(source[i++]);
                }

                if (i < source.Length)
                    Blank(source[i++]);

                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }

    // Whether the quote just consumed was preceded by @ (possibly after $), i.e. opens a verbatim
    // string. The prefix is still in the output because it is ordinary code.
    static bool Verbatim(StringBuilder emitted)
    {
        for (var i = emitted.Length - 1; i >= 0 && emitted.Length - i <= 2; i--)
        {
            if (emitted[i] == '@')
                return true;

            if (emitted[i] != '$')
                return false;
        }

        return false;
    }

    // The body of a method declared in this file, brace-matched from its declaration. The
    // declaration is located by name at the start of a line and must be followed by nothing but
    // whitespace and its opening brace, so a mention of the method elsewhere — including a call —
    // cannot retarget the search. The parameter list may wrap across lines; the line-start anchor,
    // the return-type prefix and the block-body assertion below are what keep a call site out.
    public string MethodBody(string methodName)
    {
        var (start, length) = MethodBodySpan(methodName);
        return Code.Substring(start, length);
    }

    // Where a method's body sits in the file, for assertions that need to say "this call is inside
    // that method" without re-finding the body by substring — two methods with identical bodies, or
    // one body that is a prefix of another, would send IndexOf to the wrong place.
    public (int Start, int Length) MethodBodySpan(string methodName)
    {
        // Every candidate, not just the first: relaxing the parameter list to allow a wrapped
        // signature also lets a statement-leading wrapped *call* match, and taking the first hit
        // would then fail on "not followed by a block body" instead of finding the declaration
        // below it. The block body is part of the search, so a call simply is not a candidate.
        var candidates = Regex.Matches(
            Code,
            $@"(?m)^[ \t]*(?:[\w.<>?\[\],]+[ \t]+)+{Regex.Escape(methodName)}[ \t]*\([^)]*\)[ \t]*$");

        var declaration = candidates.FirstOrDefault(m =>
        {
            var brace = Code.IndexOf('{', m.Index + m.Length);
            return brace >= 0 && Code[(m.Index + m.Length)..brace].Trim().Length == 0;
        });

        Assert.True(
            declaration is not null,
            $"{FileName} no longer declares a block-bodied method named '{methodName}'. " +
            "(An expression-bodied member, or a parameter list containing ')', is unreadable here.)");

        var open = Code.IndexOf('{', declaration!.Index + declaration.Length);

        var depth = 0;
        for (var i = open; i < Code.Length; i++)
        {
            if (Code[i] == '{') depth++;
            else if (Code[i] == '}' && --depth == 0)
                return (open, i + 1 - open);
        }

        throw new InvalidOperationException($"Unbalanced braces after '{methodName}'.");
    }

    // The braced block that follows a position — the body of the `if` a guard opens, say. Lets an
    // assertion say "this happens *inside* that branch" rather than merely "somewhere after it",
    // which is the difference between pinning a conditional and pinning nothing.
    public static string BlockAfter(string body, int from)
    {
        var open = body.IndexOf('{', from);
        Assert.True(open >= 0, "No braced block follows the guard; this test cannot read it.");

        // The block has to be the one this guard opens. Anything but whitespace or the condition's
        // closing paren in between means the brace belongs to something else — a property pattern
        // (`is not { } x`), an object initializer, a collection expression — and the assertion built
        // on it would silently be about unrelated code. A brace-less body lands here too, which is
        // correct: `if (x) Foo();` has no block for a caller to reason about.
        var between = body[from..open];
        Assert.True(
            between.All(c => char.IsWhiteSpace(c) || c == ')'),
            $"The next brace after the guard does not open its block; found \"{between.Trim()}\" in between. " +
            "Pass a position past the whole condition, or assert on the statement directly.");

        var depth = 0;
        for (var i = open; i < body.Length; i++)
        {
            if (body[i] == '{') depth++;
            else if (body[i] == '}' && --depth == 0)
                return body[open..(i + 1)];
        }

        throw new InvalidOperationException("Unbalanced braces after the guard.");
    }

    // The index of the first occurrence of a snippet within a body, asserted to exist. Used to
    // state orderings — "the guard is read before the field is written" — which is what most of
    // FD-009's threading contract comes down to.
    public static int IndexOf(string body, string snippet, string because)
    {
        var at = body.IndexOf(snippet, StringComparison.Ordinal);
        Assert.True(at >= 0, because);
        return at;
    }
}
