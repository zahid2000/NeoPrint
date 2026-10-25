using System.Reflection;
using System.Text.RegularExpressions;

using NeoPrint.Application.Devices;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Tests.Kiosks;

/// <summary>
/// Every platform setting the kiosk configuration reads is one whose change
/// tells the kiosks to read it again (T-326): a key added to the configuration
/// and forgotten in the list would reach the kiosks only at their next poll.
/// </summary>
public sealed partial class KioskConfigSettingsTests
{
    [GeneratedRegex(@"SettingKeys\.(\w+)")]
    private static partial Regex SettingKey();

    [Fact]
    public void Every_setting_the_configuration_reads_tells_the_kiosks_when_it_changes()
    {
        var source = File.ReadAllText(Path.Combine(Repository(), "backend", "src", "NeoPrint.Application", "Devices", "KioskConfigHandlers.cs"));
        var handler = source[source.IndexOf("public sealed class GetKioskConfigHandler", StringComparison.Ordinal)..];
        handler = handler[..handler.IndexOf("public sealed class KioskHeartbeatHandler", StringComparison.Ordinal)];

        var read = SettingKey().Matches(handler)
            .Select(match => (string)typeof(SettingKeys).GetField(match.Groups[1].Value, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!)
            .ToHashSet();

        read.Should().NotBeEmpty();
        KioskConfigSettings.Keys.Should().BeEquivalentTo(read);
    }

    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
