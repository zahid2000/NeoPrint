using System.Text.RegularExpressions;

using NeoPrint.Kiosk.Styles;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The design system, checked against the source.
/// </summary>
/// <remarks>
/// A colour written straight into a view is invisible in review and shows up
/// only on the finished screen, next to the token-driven element it no longer
/// matches. These tests are what keeps <c>Styles/Tokens.axaml</c> the one
/// source (docs/DESIGN_SYSTEM.md section 8).
///
/// No Avalonia and no Docker: they read files.
/// </remarks>
public sealed class DesignSystemTests
{
    [Fact]
    public void No_view_writes_a_colour_of_its_own()
    {
        var offenders = new List<string>();

        // Hex literals in XAML: Background="#111C33", Color="#F8FAFC".
        var hex = new Regex("\"#[0-9A-Fa-f]{3,8}\"", RegexOptions.Compiled);

        foreach (var file in Views())
        {
            foreach (Match match in hex.Matches(File.ReadAllText(file)))
            {
                offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
            }
        }

        offenders.Should().BeEmpty(
            "a view has to take its colours from Tokens.axaml, or the screen "
            + "stops matching the rest of the application when the palette changes");
    }

    [Fact]
    public void No_view_writes_a_font_size_of_its_own()
    {
        var offenders = new List<string>();

        // FontSize="19" - a literal. FontSize="{DynamicResource ...}" is fine.
        var literal = new Regex("FontSize=\"[0-9]", RegexOptions.Compiled);

        foreach (var file in Views())
        {
            var name = Path.GetFileName(file);

            foreach (var line in File.ReadAllLines(file))
            {
                if (literal.IsMatch(line))
                {
                    offenders.Add($"{name}: {line.Trim()}");
                }
            }
        }

        // The pairing code is the one exception, and it is stated here rather
        // than left for the reader to infer: it is the largest thing on the
        // screen by design, above every size in the scale.
        offenders.Should().OnlyContain(
            offender => offender.StartsWith("ActivationView.axaml", StringComparison.Ordinal),
            "only the pairing code is allowed to set its own size");
    }

    [Fact]
    public void The_kiosk_is_light_only()
    {
        var app = File.ReadAllText(Path.Combine(KioskRoot(), "App.axaml"));

        // Plan section 1 of the design system: these screens stand in daylight.
        app.Should().Contain("RequestedThemeVariant=\"Light\"");
    }

    [Fact]
    public void The_token_dictionary_is_loaded_before_the_styles_that_use_it()
    {
        var app = File.ReadAllText(Path.Combine(KioskRoot(), "App.axaml"));

        app.Should().Contain("Styles/Tokens.axaml");
        app.Should().Contain("Styles/Icons.axaml");
        app.Should().Contain("Styles/Controls.axaml");
    }

    [Fact]
    public void The_brushes_the_converters_use_match_the_dictionary()
    {
        var tokens = File.ReadAllText(
            Path.Combine(KioskRoot(), "Styles", "Tokens.axaml"));

        foreach (var (key, expected) in KioskTokens.Mirrored)
        {
            // Either <Color x:Key="XColor">#…</Color> or a brush declared with
            // the colour inline; both spellings carry the value next to the key.
            var pattern = new Regex(
                $"x:Key=\"{Regex.Escape(key)}(Color)?\"[^>]*(Color=\"(?<inline>#[0-9A-Fa-f]{{3,8}})\"|>(?<body>#[0-9A-Fa-f]{{3,8}})<)",
                RegexOptions.Compiled);

            var match = pattern.Match(tokens);

            match.Success.Should().BeTrue($"Tokens.axaml has to declare {key}");

            var actual = match.Groups["inline"].Success
                ? match.Groups["inline"].Value
                : match.Groups["body"].Value;

            actual.Should().BeEquivalentTo(
                expected,
                $"KioskTokens.{key} mirrors Tokens.axaml and the two have drifted");
        }
    }

    private static IEnumerable<string> Views()
        => Directory.EnumerateFiles(
            Path.Combine(KioskRoot(), "Views"),
            "*.axaml",
            SearchOption.AllDirectories);

    private static string KioskRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        // Directory.Build.props, not a documentation file: it sits at the
        // repository root by definition, and its name is the same on a
        // case-sensitive file system, which CI runs on.
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root has to be findable from the test output directory");

        return Path.Combine(directory!.FullName, "desktop", "src", "NeoPrint.Kiosk");
    }
}
