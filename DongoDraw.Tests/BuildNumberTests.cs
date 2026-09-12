using System.Diagnostics;
using Xunit.Abstractions;

namespace DongoDraw.Tests;

// Guards the build-number lifecycle: every APK build consumes the next build number and writes it
// back to version.props, and a semantic bump resets it to 0. Two scripts share that rule
// (scripts/build-apk.ps1 consumes, scripts/bump-version.ps1 resets), so the write itself lives in
// scripts/version-lib.ps1 and is exercised here against a throwaway copy of the repo rather than
// against the real version.props.
public class BuildNumberTests(ITestOutputHelper output) : IDisposable
{
    readonly string _sandbox = NewSandbox();

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // A fake repo root: the real scripts, a version.props we control. bump-version.ps1 and
    // version-lib.ps1 both derive the repo root from their own location, so this is enough.
    static string NewSandbox()
    {
        var root = Path.Combine(Path.GetTempPath(), "fd-buildnum-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        foreach (var script in new[] { "version-lib.ps1", "bump-version.ps1" })
        {
            File.Copy(TestPaths.Path("scripts", script), Path.Combine(root, "scripts", script));
        }

        return root;
    }

    void WriteVersionProps(int major, int minor, int patch, int build) =>
        File.WriteAllText(Path.Combine(_sandbox, "version.props"), $"""
            <Project>
              <PropertyGroup>
                <FdVersionMajor>{major}</FdVersionMajor>
                <FdVersionMinor>{minor}</FdVersionMinor>
                <FdVersionPatch>{patch}</FdVersionPatch>
                <FdBuildNumber>{build}</FdBuildNumber>
              </PropertyGroup>
            </Project>
            """);

    int ReadBuildNumber()
    {
        var text = File.ReadAllText(Path.Combine(_sandbox, "version.props"));
        return int.Parse(System.Text.RegularExpressions.Regex
            .Match(text, @"<FdBuildNumber>\s*(\d+)\s*</FdBuildNumber>").Groups[1].Value);
    }

    // Returns null when Windows PowerShell is unavailable (non-Windows agent), so the test skips
    // rather than fails; the same pattern VersionTests uses for MSBuild.
    (int ExitCode, string Output)? RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo("powershell")
        {
            WorkingDirectory = _sandbox,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command })
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var text = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, text);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return null;
        }
    }

    (int ExitCode, string Output)? RunLibCommand(string call) =>
        RunPowerShell($". '{Path.Combine(_sandbox, "scripts", "version-lib.ps1")}'; {call}");

    [Fact]
    public void SetBuildNumber_WritesTheNumberBackToVersionProps()
    {
        WriteVersionProps(1, 2, 3, 4);

        var run = RunLibCommand($"Set-FdBuildNumber -Build 7 -RepoRoot '{_sandbox}'");
        if (run is null) { output.WriteLine("Skipped: powershell unavailable."); return; }

        Assert.Equal(7, ReadBuildNumber());
    }

    // The whole point of the auto-bump: two builds of the same commit never claim the same
    // versionCode, so a device accepts the second as an upgrade.
    [Fact]
    public void NextBuildNumber_IsOneMoreThanTheCurrentOne()
    {
        WriteVersionProps(1, 2, 3, 4);

        var run = RunLibCommand($"(Get-FdNextBuildNumber -RepoRoot '{_sandbox}')");
        if (run is null) { output.WriteLine("Skipped: powershell unavailable."); return; }

        Assert.Equal("5", run.Value.Output.Trim());
    }

    // 100 would carry into the patch field of the versionCode. The build must stop rather than ship
    // an APK whose code collides with the next patch release.
    [Fact]
    public void NextBuildNumber_FailsInsteadOfOverflowingThePatchField()
    {
        WriteVersionProps(1, 2, 3, 99);

        var run = RunLibCommand($"Get-FdNextBuildNumber -RepoRoot '{_sandbox}'");
        if (run is null) { output.WriteLine("Skipped: powershell unavailable."); return; }

        Assert.NotEqual(0, run.Value.ExitCode);
    }

    [Fact]
    public void SetBuildNumber_RejectsANumberOutsideItsTwoDigitSlot()
    {
        WriteVersionProps(1, 2, 3, 4);

        var run = RunLibCommand($"Set-FdBuildNumber -Build 100 -RepoRoot '{_sandbox}'");
        if (run is null) { output.WriteLine("Skipped: powershell unavailable."); return; }

        Assert.NotEqual(0, run.Value.ExitCode);
        Assert.Equal(4, ReadBuildNumber());
    }

    // version.props is committed BOM-less; a BOM written back would show as a whole-file diff for
    // whoever next opens it.
    [Fact]
    public void SetBuildNumber_WritesUtf8WithoutABom()
    {
        WriteVersionProps(1, 2, 3, 4);

        var run = RunLibCommand($"Set-FdBuildNumber -Build 5 -RepoRoot '{_sandbox}'");
        if (run is null) { output.WriteLine("Skipped: powershell unavailable."); return; }

        var bytes = File.ReadAllBytes(Path.Combine(_sandbox, "version.props"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    // The reset half of the rule: a semantic bump starts the build count over, which is what keeps
    // the auto-bump from walking into the 99 ceiling.
    [Theory]
    [InlineData("-Patch")]
    [InlineData("-Minor")]
    [InlineData("-Major")]
    public void SemanticBump_ResetsTheBuildNumber(string part)
    {
        WriteVersionProps(1, 2, 3, 7);

        var run = RunPowerShell($"& '{Path.Combine(_sandbox, "scripts", "bump-version.ps1")}' {part}");
        if (run is null) { output.WriteLine("Skipped: powershell unavailable."); return; }

        Assert.Equal(0, ReadBuildNumber());
    }

    // build-apk.ps1 must go through the shared writer, not reimplement the regex: version-lib.ps1
    // is where the range check and the BOM-less write live.
    [Fact]
    public void BuildApkScript_PersistsTheConsumedBuildNumber()
    {
        var script = File.ReadAllText(TestPaths.Path("scripts", "build-apk.ps1"));

        Assert.Contains("Set-FdBuildNumber", script);
        Assert.Contains("Get-FdNextBuildNumber", script);
    }
}
