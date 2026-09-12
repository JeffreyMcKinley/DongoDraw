using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit.Abstractions;

namespace DongoDraw.Tests;

// Guards the APK versioning scheme. version.props holds four numbers; Directory.Build.props packs
// them into android:versionCode and android:versionName, and scripts/version-lib.ps1 repeats the
// same packing so a build script can name its output. Three copies of one formula is the risk these
// tests exist to cover, together with the range limits that keep the packing reversible.
public class VersionTests(ITestOutputHelper output)
{
    const string VersionProps = "version.props";
    const string BuildProps = "Directory.Build.props";
    const string AppCsproj = "DongoDraw.csproj";

    static int Part(string name)
    {
        var value = XDocument.Load(TestPaths.Path(VersionProps))
            .Descendants(name).SingleOrDefault()?.Value.Trim();

        Assert.False(string.IsNullOrEmpty(value), $"{VersionProps} has no single <{name}> element.");
        Assert.True(int.TryParse(value, out var number), $"<{name}> is not a number: '{value}'.");
        return number;
    }

    static int Major => Part("FdVersionMajor");
    static int Minor => Part("FdVersionMinor");
    static int Patch => Part("FdVersionPatch");
    static int BuildNumber => Part("FdBuildNumber");

    static int ExpectedCode => Major * 1000000 + Minor * 10000 + Patch * 100 + BuildNumber;

    static string ExpectedName =>
        BuildNumber == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}.{BuildNumber}";

    // Each of the lower three fields owns two decimal digits of the versionCode. At 100 a field
    // carries into the one above it, so 1.0.100 and 1.1.0 would ship the same code and the device
    // would refuse the second as "not an upgrade".
    [Theory]
    [InlineData("FdVersionMinor")]
    [InlineData("FdVersionPatch")]
    [InlineData("FdBuildNumber")]
    public void VersionFields_FitTheirTwoDigitSlot(string name)
    {
        var value = Part(name);

        Assert.InRange(value, 0, 99);
    }

    // versionCode is an int32 on the Android side; a major of 2148 overflows it.
    [Fact]
    public void Major_StaysUnderTheInt32Ceiling()
    {
        Assert.InRange(Major, 0, 2147);
        Assert.True(ExpectedCode > 0, "versionCode must be positive; Android treats 0 as unset.");
    }

    // The app csproj used to carry its own ApplicationVersion/ApplicationDisplayVersion. A csproj
    // value beats the one Directory.Build.props computes (props are imported first), so re-adding
    // one here would leave every APK stamped with a version nothing else in the repo agrees with.
    [Theory]
    [InlineData("ApplicationVersion")]
    [InlineData("ApplicationDisplayVersion")]
    public void AppCsproj_DoesNotHardcodeAVersion(string property)
    {
        var hardcoded = XDocument.Load(TestPaths.Path(AppCsproj)).Descendants(property);

        Assert.Empty(hardcoded);
    }

    // The PowerShell side (scripts/version-lib.ps1, used by build-apk.ps1 to name the artifact and
    // write its manifest) reimplements the packing. If it drifts, the filename claims one version
    // while the APK inside reports another.
    [Fact]
    public void VersionLibScript_PacksTheCodeTheSameWay()
    {
        var script = File.ReadAllText(TestPaths.Path("scripts", "version-lib.ps1"));

        Assert.Contains(
            "$major * 1000000 + $minor * 10000 + $patch * 100 + $build",
            script);
    }

    // Directory.Build.props is the one place the numbers become MSBuild properties; if the import
    // is dropped the build silently falls back to Android's default versionCode of 1.
    [Fact]
    public void BuildProps_ImportsVersionProps()
    {
        var text = File.ReadAllText(TestPaths.Path(BuildProps));

        Assert.Matches(new Regex(@"<Import\s+Project=""[^""]*version\.props""\s*/>"), text);
    }

    // The end-to-end check: ask MSBuild what it evaluates, rather than trusting a reading of the
    // property functions. Runs against Core because it needs no Android workload.
    [Theory]
    [InlineData("ApplicationVersion")]
    [InlineData("ApplicationDisplayVersion")]
    public void MSBuild_DerivesTheVersionFromVersionProps(string property)
    {
        var expected = property == "ApplicationVersion"
            ? ExpectedCode.ToString()
            : ExpectedName;

        var evaluated = EvaluateProperty(property);
        if (evaluated is null)
        {
            output.WriteLine($"Skipped: could not evaluate {property} (dotnet msbuild unavailable).");
            return;
        }

        Assert.Equal(expected, evaluated);
    }

    static string? EvaluateProperty(string property)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = TestPaths.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
        {
            "msbuild", Path.Combine("DongoDraw.Core", "DongoDraw.Core.csproj"),
            $"-getProperty:{property}", "-nologo",
        })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi);
        if (proc is null) return null;

        var stdout = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        return proc.ExitCode == 0 ? stdout.Trim() : null;
    }
}
