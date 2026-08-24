using System.Text;
using System.Text.RegularExpressions;

namespace FigureDrawing.Tests;

// Reads one Activity as a file for the contract tier (docs/ARCHITECTURE.md §11), with the two tools
// those tests depend on: source with comments and literals blanked out, and the body of a named
// method. Shared because more than one screen is pinned this way — FolderMemoryContractTests reads
// MainActivity, SessionScreenContractTests reads SessionActivity — and a second copy of a brace
// matcher is a second place for it to be subtly wrong.
//
// Line endings are normalised to \n on the way in. The declaration matcher anchors on end-of-line,
// and in .NET `$` matches before the \n with the \r still ahead of it, so a CRLF checkout would
// silently find no methods at all — which is a green suite reporting on a file it never read.
internal sealed class SourceContract
{
    readonly string _fileName;

    public SourceContract(params string[] pathParts)
    {
        ArgumentOutOfRangeException.ThrowIfZero(pathParts.Length);

        _fileName = pathParts[^1];
        Text = Normalise(File.ReadAllText(TestPaths.Path(pathParts)));
        Code = StripCommentsAndLiterals(Text);
    }

    SourceContract(string fileName, string source)
    {
        _fileName = fileName;
        Text = Normalise(source);
        Code = StripCommentsAndLiterals(Text);
    }

    // Over a fixture string rather than a file on disk, so the matcher's own tests do not depend on
    // any real Activity staying shaped the way they need it.
    public static SourceContract ForTesting(string fileName, string source) => new(fileName, source);

    static string Normalise(string source) => source.Replace("\r\n", "\n");

    // The file as written, minus the line-ending difference between checkouts.
    public string Text { get; }

    // The same source with every comment, string and char literal blanked out, positions preserved.
    public string Code { get; }

    // The body of a method declared in this file, brace-matched from its declaration. The
    // declaration is located by name at the start of a line and must be followed by nothing but
    // whitespace and its opening brace, so a mention of the method elsewhere — including a call —
    // cannot retarget the search.
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
        // Every candidate, not just the first. The parameter list may wrap across lines, which a
        // signature like LibraryLoader.LoadAsync's does — but allowing that also lets a
        // statement-leading wrapped *call* match, and taking the first hit would then fail on "not
        // followed by a block body" instead of finding the declaration below it. Requiring the block
        // as part of the search is what keeps a call out.
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
            $"{_fileName} no longer declares a block-bodied method named '{methodName}'. " +
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

    // Replaces the contents of comments and literals with spaces, keeping the length and the line
    // breaks so offsets still line up with the original file. Blanking rather than deleting also
    // keeps a brace inside a string or an interpolation hole out of the brace matcher above.
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
}
