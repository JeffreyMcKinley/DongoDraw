using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FigureDrawing.Tests;

// UI-contract tests: MainActivity resolves views/strings/layouts by name at runtime
// (FindViewById(Resource.Id.x), GetString(Resource.String.y), SetContentView(Resource.Layout.z)).
// A rename or deletion in the Android resource XML would compile fine but crash at runtime with a
// null view / missing resource. These tests read the resource XML directly (no device) and assert
// every name MainActivity references still exists.
public class UiResourceContractTests
{
    static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";

    static string MainActivitySource => File.ReadAllText(TestPaths.Path("MainActivity.cs"));

    // Names referenced from code as Resource.<kind>.<name>.
    static IReadOnlySet<string> ReferencedResources(string kind)
    {
        var matches = Regex.Matches(MainActivitySource, $@"Resource\.{kind}\.(\w+)");
        return matches.Select(m => m.Groups[1].Value).ToHashSet();
    }

    // android:id="@+id/foo" / "@id/foo" declared anywhere in a layout file.
    static IReadOnlySet<string> LayoutIds(string layoutName)
    {
        var doc = XDocument.Load(TestPaths.Path("Resources", "layout", $"{layoutName}.xml"));
        return doc.Descendants()
            .Select(e => e.Attribute(Android + "id")?.Value)
            .Where(v => v is not null)
            .Select(v => v!.Replace("@+id/", "").Replace("@id/", ""))
            .ToHashSet();
    }

    // Every declared string by name, for tests that care about the text and not just the name.
    static IReadOnlyDictionary<string, string> StringResources()
    {
        var doc = XDocument.Load(TestPaths.Path("Resources", "values", "strings.xml"));
        return doc.Root!.Elements("string")
            .Where(e => e.Attribute("name") is not null)
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Value);
    }

    static IReadOnlySet<string> StringResourceNames()
    {
        var doc = XDocument.Load(TestPaths.Path("Resources", "values", "strings.xml"));
        return doc.Root!.Elements("string")
            .Select(e => e.Attribute("name")?.Value)
            .Where(v => v is not null)
            .Select(v => v!)
            .ToHashSet();
    }

    [Fact]
    public void EveryReferencedViewId_ExistsInLayout()
    {
        var ids = LayoutIds("activity_main");
        var missing = ReferencedResources("Id").Where(id => !ids.Contains(id)).ToList();

        Assert.True(missing.Count == 0,
            $"MainActivity references view id(s) not declared in activity_main.xml: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryReferencedString_ExistsInStringsXml()
    {
        var names = StringResourceNames();
        var missing = ReferencedResources("String").Where(n => !names.Contains(n)).ToList();

        Assert.True(missing.Count == 0,
            $"MainActivity references string(s) not declared in strings.xml: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryReferencedLayout_HasAFile()
    {
        var missing = ReferencedResources("Layout")
            .Where(name => !File.Exists(TestPaths.Path("Resources", "layout", $"{name}.xml")))
            .ToList();

        Assert.True(missing.Count == 0,
            $"MainActivity references layout(s) with no file: {string.Join(", ", missing)}");
    }

    // FD-001 explicit view contract: the folder-picker screen must keep these three views.
    [Theory]
    [InlineData("pick_button")]
    [InlineData("image_container")]
    [InlineData("empty_label")]
    public void ActivityMain_DeclaresFdViews(string id) =>
        Assert.Contains(id, LayoutIds("activity_main"));

    // FD-002 session-setup view contract: the two inputs and the Start button.
    [Theory]
    [InlineData("seconds_input")]
    [InlineData("count_input")]
    [InlineData("start_button")]
    public void ActivityMain_DeclaresSessionSetupViews(string id) =>
        Assert.Contains(id, LayoutIds("activity_main"));

    // FD-002 session-setup string contract: input labels + Start label.
    [Theory]
    [InlineData("seconds_label_text")]
    [InlineData("count_label_text")]
    [InlineData("start_button_text")]
    public void Strings_DeclaresSessionSetupStrings(string name) =>
        Assert.Contains(name, StringResourceNames());

    // FD-001 explicit string contract: picker label + every empty-state message. Plus FD-009's
    // loading caption, which empty_label carries while the walk and the preview decodes run off the
    // UI thread — the reason a folder being read is never captioned as one with nothing in it.
    [Theory]
    [InlineData("app_name")]
    [InlineData("pick_button_text")]
    [InlineData("empty_label_text")]
    [InlineData("empty_folder_text")]
    [InlineData("folder_error_text")]
    [InlineData("folder_unavailable_text")]
    [InlineData("library_loading_text")]
    [InlineData("picker_error_text")]
    public void Strings_DeclaresFdStrings(string name) =>
        Assert.Contains(name, StringResourceNames());

    // The four empty states are four different things to tell the artist — nothing picked yet, the
    // remembered folder cannot be reopened, the folder holds no images, that folder would not open —
    // and the whole point of distinguishing them is lost the moment two of them read the same. A
    // copy-paste between these would defeat the feature with every other test still green.
    [Fact]
    public void Strings_TheEmptyStateMessagesAreAllDifferent()
    {
        var texts = StringResources();

        var messages = new[]
            {
                "empty_label_text", "empty_folder_text", "folder_error_text", "folder_unavailable_text",
                // FD-009 put two more captions on the same label: one while the folder is being
                // read, one when no app on the device can pick a folder at all.
                "library_loading_text", "picker_error_text",
            }
            .Select(name => texts[name])
            .ToList();

        Assert.Equal(messages.Count, messages.Distinct().Count());
        Assert.DoesNotContain(messages, string.IsNullOrWhiteSpace);
    }

    // --- Claude Design import: the three tabbed panes -------------------------

    // Session / Images / Settings are panes of one screen, so the tab bar and the panes it switches
    // between must both exist. Losing either leaves a screen with no way back to the others.
    [Theory]
    [InlineData("pane_setup")]
    [InlineData("pane_library")]
    [InlineData("pane_settings")]
    [InlineData("tab_session")]
    [InlineData("tab_images")]
    [InlineData("tab_settings")]
    public void ActivityMain_DeclaresTheTabbedPanes(string id) =>
        Assert.Contains(id, LayoutIds("activity_main"));

    // Exactly one pane starts visible; the other two are gone. Two visible panes would stack their
    // content on top of each other, which no amount of code in OnCreate would undo before it drew.
    [Fact]
    public void ActivityMain_StartsOnExactlyOnePane()
    {
        var doc = XDocument.Load(TestPaths.Path("Resources", "layout", "activity_main.xml"));
        var panes = new[] { "pane_setup", "pane_library", "pane_settings" };

        var visible = doc.Descendants()
            .Where(e => panes.Contains(e.Attribute(Android + "id")?.Value?.Replace("@+id/", "")))
            .Count(e => e.Attribute(Android + "visibility")?.Value != "gone");

        Assert.Equal(1, visible);
    }

    // The setup pane's preset chips. One chip per SessionSetup preset, bound by position in
    // MainActivity.BindPresetChips — a missing id would leave a preset unreachable.
    [Theory]
    [InlineData("chip_sec_30")]
    [InlineData("chip_sec_60")]
    [InlineData("chip_sec_120")]
    [InlineData("chip_sec_300")]
    [InlineData("chip_break_0")]
    [InlineData("chip_break_5")]
    [InlineData("chip_break_15")]
    [InlineData("chip_break_60")]
    public void ActivityMain_DeclaresThePresetChips(string id) =>
        Assert.Contains(id, LayoutIds("activity_main"));

    // The rest of the imported setup pane: the pool card, the length estimate, and the four
    // Settings toggles.
    [Theory]
    [InlineData("pool_label")]
    [InlineData("change_button")]
    [InlineData("estimate_label")]
    [InlineData("library_count")]
    [InlineData("library_more")]
    [InlineData("setting_shuffle")]
    [InlineData("setting_awake")]
    [InlineData("setting_chime")]
    [InlineData("setting_grayscale")]
    public void ActivityMain_DeclaresTheImportedControls(string id) =>
        Assert.Contains(id, LayoutIds("activity_main"));

    [Theory]
    [InlineData("tab_session_text")]
    [InlineData("tab_images_text")]
    [InlineData("tab_settings_text")]
    [InlineData("break_label_text")]
    [InlineData("pool_ready_format")]
    [InlineData("pool_empty_text")]
    [InlineData("estimate_format")]
    [InlineData("library_more_format")]
    [InlineData("change_button_text")]
    [InlineData("toggle_on_text")]
    [InlineData("toggle_off_text")]
    [InlineData("thumbnail_desc")]
    public void Strings_DeclaresTheImportedStrings(string name) =>
        Assert.Contains(name, StringResourceNames());

    // The reference grid is a GridLayout whose column count MainActivity reads from a resource, so
    // the fold-open variant is a resource swap rather than a code branch.
    [Fact]
    public void LibraryColumns_AreDeclaredForBothPhoneAndFoldOpen()
    {
        Assert.Equal(2, IntegerResource(Path.Combine("values", "integers.xml"), "library_columns"));
        Assert.Equal(4, IntegerResource(Path.Combine("values-sw600dp", "integers.xml"), "library_columns"));
    }

    static int IntegerResource(string relativePath, string name)
    {
        var doc = XDocument.Load(TestPaths.Path("Resources", relativePath));
        var value = doc.Root!.Elements("integer")
            .First(e => e.Attribute("name")?.Value == name).Value;

        return int.Parse(value);
    }
}
