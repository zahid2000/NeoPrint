using System.Text.RegularExpressions;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The kiosk may restart its machine and nothing more (plan sections 7.8,
/// 7.12): no power-off, no halt, no wildcard. The reboot is carried out by
/// the kiosk application itself (docs/DECISIONS.md, 2026-10-12), so what the
/// provisioning script allows it and what the code asks for are both pinned
/// here - widening either fails a test before it reaches a kiosk.
/// </summary>
public sealed partial class PowerPermissionTests
{
    private static readonly string Desktop = FindDesktop();

    private static string FindDesktop()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (Directory.Exists(Path.Combine(folder.FullName, "provisioning")) && Directory.Exists(Path.Combine(folder.FullName, "src")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("The desktop folder (provisioning, src) was not found above the test output.");
    }

    [GeneratedRegex(@"poweroff|halt|shutdown|suspend|hibernate|kexec|init\s+[06]", RegexOptions.IgnoreCase)]
    private static partial Regex OtherPowerCommands();

    [GeneratedRegex(@"if \[ ""\$DRY_RUN"" -eq 1 \]; then\s+for file in ([^;]+); do")]
    private static partial Regex DryRunFiles();

    /// <summary>The rules the provisioning script writes to <c>/etc/sudoers.d/neoprint</c>, without comments.</summary>
    private static List<string> SudoersRules()
    {
        var script = File.ReadAllText(Path.Combine(Desktop, "provisioning", "linux", "provision.sh"));
        // Written beside the file, checked with visudo, then installed (T-327).
        var start = script.IndexOf("cat >\"$sudoers_new\" <<CONF", StringComparison.Ordinal);
        var end = script.IndexOf("\nCONF", start, StringComparison.Ordinal);

        start.Should().BeGreaterThan(0, "the sudoers rules are written by provision.sh");

        return
        [
            .. script[start..end].Split('\n').Skip(1)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#')),
        ];
    }

    [Fact]
    public void On_linux_the_kiosk_account_may_reboot_open_maintenance_and_run_the_converter_and_nothing_else()
    {
        var kiosk = SudoersRules().Where(rule => rule.StartsWith("$KIOSK_USER ", StringComparison.Ordinal)).ToList();

        kiosk.Should().BeEquivalentTo(
        [
            "$KIOSK_USER ALL=(root) NOPASSWD: /usr/bin/systemctl start neoprint-maintenance.service",
            "$KIOSK_USER ALL=(root) NOPASSWD: /usr/bin/systemctl reboot",
            "$KIOSK_USER ALL=($CONVERT_USER) NOPASSWD: $LIB_ROOT/neoprint-convert",
        ]);
    }

    [Fact]
    public void No_sudoers_rule_allows_powering_off_or_anything_open_ended()
    {
        var rules = SudoersRules();

        rules.Should().NotBeEmpty();

        foreach (var rule in rules)
        {
            OtherPowerCommands().IsMatch(rule).Should().BeFalse($"\"{rule}\" would let the machine be switched off, not just restarted");
            rule.Should().NotContain("*", "a wildcard allows more than was meant");
            rule.Should().NotEndWith("NOPASSWD: ALL");
            // One program by its full path (the converter's is under the install folder).
            Regex.IsMatch(rule, @"NOPASSWD: (/|\$LIB_ROOT/)\S+").Should().BeTrue($"\"{rule}\" must name one program by its full path");
        }

        // Of systemctl, only these verbs - each with its one unit where it takes one.
        rules.Where(rule => rule.Contains("systemctl", StringComparison.Ordinal))
            .Select(rule => rule[(rule.IndexOf("systemctl", StringComparison.Ordinal) + "systemctl ".Length)..])
            .Should().BeSubsetOf([
                "reboot", "restart neoprint-kiosk", "restart neoprint-agent", "start neoprint-maintenance.service",
                // Around a version switch (T-321).
                "stop neoprint-kiosk.service", "start neoprint-kiosk.service",
            ]);
    }

    [Fact]
    public void The_agent_may_restart_the_ui_and_itself_and_reach_the_provisioning_scripts_only_through_the_root_owned_helper()
    {
        var agent = SudoersRules().Where(rule => rule.StartsWith("$SERVICE_USER ", StringComparison.Ordinal)).ToList();

        agent.Should().BeEquivalentTo(
        [
            "$SERVICE_USER ALL=(root) NOPASSWD: /usr/bin/systemctl reboot",
            "$SERVICE_USER ALL=(root) NOPASSWD: /usr/bin/systemctl restart neoprint-kiosk",
            "$SERVICE_USER ALL=(root) NOPASSWD: /usr/bin/systemctl restart neoprint-agent",
            "$SERVICE_USER ALL=(root) NOPASSWD: /usr/sbin/lpadmin",
            "$SERVICE_USER ALL=(root) NOPASSWD: /usr/sbin/cupsenable",
            "$SERVICE_USER ALL=(root) NOPASSWD: /usr/bin/systemctl stop neoprint-kiosk.service",
            "$SERVICE_USER ALL=(root) NOPASSWD: /usr/bin/systemctl start neoprint-kiosk.service",
            "$SERVICE_USER ALL=(root) NOPASSWD: $LIB_ROOT/neoprint-reprovision",
        ]);

        // Nothing the agent can write is run as root: not the scripts under
        // /opt/neoprint, and the helper passes the scripts nothing but --repair.
        agent.Should().NotContain(rule => rule.Contains("/opt/neoprint", StringComparison.Ordinal) || rule.Contains("$INSTALL_ROOT", StringComparison.Ordinal));
        var helper = File.ReadAllText(Path.Combine(Desktop, "provisioning", "linux", "neoprint-reprovision"));
        helper.Should().Contain("exec /usr/local/lib/neoprint/agent/NeoPrint.Kiosk.Agent provisioning \"$@\" --repair");
        helper.Should().Contain("install) [ $# -eq 4 ]").And.Contain("reapply) [ $# -eq 1 ]");

        var script = File.ReadAllText(Path.Combine(Desktop, "provisioning", "linux", "provision.sh"));
        script.Should().Contain("install -d -o root -g root -m 755 \"$INSTALL_ROOT/provisioning\"", "the provisioning scripts stay root's");
        script.Should().Contain("if [ \"$REPAIR\" -eq 0 ] && [ -x \"$INSTALL_ROOT/agent/current/NeoPrint.Kiosk.Agent\" ]", "a repair never refreshes root's copy of the agent");
    }

    [Fact]
    public void Every_file_the_dry_run_looks_for_is_a_file_of_the_provisioning_package()
    {
        // The container kiosk's ReapplyProvisioning is the dry run; a wrong name
        // in its list made it fail on every kiosk that could run it (T-321).
        var script = File.ReadAllText(Path.Combine(Desktop, "provisioning", "linux", "provision.sh"));
        var list = DryRunFiles().Match(script);
        list.Success.Should().BeTrue("provision.sh --dry-run checks its files");

        var files = list.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        files.Should().Contain(["neoprint-agent-guard", "neoprint-reprovision", "neoprint-convert"]);
        files.Should().OnlyContain(file => File.Exists(Path.Combine(Desktop, "provisioning", "linux", file)));
    }

    [Fact]
    public void The_kiosk_code_asks_the_operating_system_for_a_restart_and_never_for_a_power_off()
    {
        var sources = Directory
            .EnumerateFiles(Path.Combine(Desktop, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(file => (File: Path.GetFileName(file), Text: File.ReadAllText(file)))
            .ToList();

        // Windows: shutdown.exe is started in one place, to restart at once.
        var windows = sources.Where(source => source.Text.Contains("FileName = \"shutdown\"", StringComparison.Ordinal)).ToList();
        windows.Should().ContainSingle().Which.Text.Should().Contain("Arguments = \"/r /t 0\"");

        foreach (var (file, text) in sources)
        {
            // /s powers off, /p powers off at once, /h hibernates, /l signs out.
            Regex.IsMatch(text, "Arguments = \"/(s|p|h|l)\\b").Should().BeFalse($"{file} would do something other than restart");
            Regex.IsMatch(text, "\"(poweroff|halt|Stop-Computer)\"").Should().BeFalse($"{file} names a power-off command");
        }

        // Linux: through sudo, systemctl is asked for exactly the two things the sudoers file allows the UI.
        var linux = sources.Single(source => source.File == "LinuxPlatform.cs").Text;
        Regex.Matches(linux, "RunAsync\\(\"systemctl\", \"([^\"]+)\"").Select(match => match.Groups[1].Value)
            .Should().BeEquivalentTo(["reboot", "start neoprint-maintenance.service"]);
    }
}
