using System.Xml.Linq;

namespace DongoDraw.Tests;

// Inter is the Nocturne design system's typeface and is bundled under Resources/font. The wiring
// that carries it to every view is easy to break silently: a missing weight file, a bare
// android:fontFamily on the theme (which TextView never reads), or a min-API regression below the
// one that introduced font resources. Each of those compiles and ships, and the app just quietly
// renders in the platform sans-serif instead. These tests read the resource XML directly.
public class TypefaceContractTests
{
    static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";

    static XDocument Styles => XDocument.Load(TestPaths.Path("Resources", "values", "styles.xml"));

    static XElement Style(string name) =>
        Styles.Root!.Elements("style").First(e => e.Attribute("name")?.Value == name);

    static string? Item(XElement style, string name) =>
        style.Elements("item").FirstOrDefault(i => i.Attribute("name")?.Value == name)?.Value;

    // The three weights the design system imports (Inter:wght@400;500;600), plus the family that
    // binds them. A font resource name is referenced by name at runtime, so a rename or a missing
    // file is a blank-render on device, not a build error.
    [Theory]
    [InlineData("inter_regular.ttf")]
    [InlineData("inter_medium.ttf")]
    [InlineData("inter_semibold.ttf")]
    [InlineData("inter.xml")]
    public void FontResource_Exists(string fileName)
    {
        var path = TestPaths.Path("Resources", "font", fileName);

        Assert.True(File.Exists(path), $"Missing font resource: {path}");
        Assert.True(new FileInfo(path).Length > 0, $"Font resource is empty: {path}");
    }

    // Every weight in the family must point at a file that is actually shipped.
    [Fact]
    public void FontFamily_MapsTheThreeDesignWeights_ToRealFiles()
    {
        var doc = XDocument.Load(TestPaths.Path("Resources", "font", "inter.xml"));
        var fonts = doc.Root!.Elements("font").ToList();

        var weights = fonts.Select(f => f.Attribute(Android + "fontWeight")?.Value).ToList();
        Assert.Equal(new[] { "400", "500", "600" }, weights);

        foreach (var font in fonts)
        {
            var reference = font.Attribute(Android + "font")?.Value;
            Assert.NotNull(reference);

            var name = reference!.Replace("@font/", "");
            Assert.True(File.Exists(TestPaths.Path("Resources", "font", $"{name}.ttf")),
                $"Font family references @font/{name}, which has no .ttf.");
        }
    }

    // The typeface reaches views through the theme's text appearances. TextView and EditText resolve
    // ?android:attr/textAppearance and Button resolves ?android:attr/textAppearanceButton; a bare
    // android:fontFamily item on the theme is NOT read by TextView, so overriding these is the whole
    // mechanism. Verified on device before this test was written.
    [Theory]
    [InlineData("android:textAppearance")]
    [InlineData("android:textAppearanceButton")]
    public void Theme_RoutesTheTypefaceThroughItsTextAppearances(string item) =>
        Assert.Equal("@style/TextAppearance.Inter", Item(Style("AppTheme"), item));

    [Fact]
    public void DefaultTextAppearance_NamesTheInterFamily() =>
        Assert.Equal("@font/inter", Item(Style("TextAppearance.Inter"), "android:fontFamily"));

    // EditText is the exception: the platform's Widget.Material.EditText pins its own text
    // appearance, so the Input style has to name the family itself. Dropping this line renders every
    // number input in Roboto while the rest of the screen is Inter.
    [Fact]
    public void InputStyle_NamesTheFamilyItself_BecauseTheThemeDoesNotReachEditText() =>
        Assert.Equal("@font/inter", Item(Style("Input"), "android:fontFamily"));

    // Nothing may fall back to the platform sans-serif: that was the placeholder before Inter was
    // bundled, and re-introducing it would leave one control off-system with no build error.
    [Fact]
    public void NoStyleOrLayout_StillAsksForThePlatformSansSerif()
    {
        var files = new[]
        {
            TestPaths.Path("Resources", "values", "styles.xml"),
            TestPaths.Path("Resources", "layout", "activity_main.xml"),
            TestPaths.Path("Resources", "layout", "activity_session.xml"),
        };

        var offenders = files
            .Where(f => File.ReadAllText(f).Contains("sans-serif"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"These still reference the platform sans-serif instead of @font/inter: {string.Join(", ", offenders)}");
    }

    // Bundling the licence is a condition of the SIL Open Font Licence, which Inter ships under.
    [Fact]
    public void TheFontLicence_IsBundled()
    {
        var licence = TestPaths.Path("docs", "third-party-licenses", "Inter-OFL.txt");

        Assert.True(File.Exists(licence), $"Missing font licence at {licence}");
        Assert.Contains("SIL Open Font License", File.ReadAllText(licence));
    }
}
