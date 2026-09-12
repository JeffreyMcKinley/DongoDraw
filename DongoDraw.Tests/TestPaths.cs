namespace DongoDraw.Tests;

// Locates repo files from within the test run. Tests execute with an arbitrary working directory
// (nx sets cwd to the test project, dotnet test to the output dir), so resolve everything relative
// to the repo root, found by walking up until the solution file appears.
internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string Path(params string[] parts) =>
        System.IO.Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray());

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "DongoDraw.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate DongoDraw.sln above {AppContext.BaseDirectory}");
    }
}
