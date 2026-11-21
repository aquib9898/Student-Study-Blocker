using System.Text.Json;
using StudyBlocker.Common;
using StudyBlocker.Service.Packaging;
using StudyBlocker.Service.Policies;

namespace StudyBlocker.Service.Tests;

// The Microsoft Store (MSIX) version's pieces that don't need a package to test.
public class PackagingTests
{
    private const string Chrome = @"SOFTWARE\Policies\Google\Chrome";

    private static ManagedPolicy Managed(string key, string name, object? original, bool isList = false) => new()
    {
        KeyPath = key,
        Name = name,
        IsList = isList,
        Original = original is null ? null : JsonSerializer.SerializeToElement(original),
    };

    [Fact]
    public void The_restore_plan_puts_each_setting_back_as_it_was()
    {
        var reg = RestorePlan.Build(
        [
            Managed(Chrome, "IncognitoModeAvailability", null),
            Managed(Chrome, "DeveloperToolsAvailability", 1),
            Managed(Chrome, "DnsOverHttpsMode", "automatic"),
            Managed(Chrome, "URLBlocklist", null, isList: true),
            Managed(Chrome, "ExtensionInstallForcelist", new[] { "abc;https://example.com/u\"pdate" }, isList: true),
        ], [@"SOFTWARE\Google\Chrome\NativeMessagingHosts\com.smartstudyblocker.host"]);

        Assert.StartsWith("Windows Registry Editor Version 5.00\r\n", reg);
        // Absent before: deleted. A number: a DWORD. Text: a string.
        Assert.Contains("[HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Google\\Chrome]\r\n\"IncognitoModeAvailability\"=-\r\n", reg);
        Assert.Contains("\"DeveloperToolsAvailability\"=dword:00000001\r\n", reg);
        Assert.Contains("\"DnsOverHttpsMode\"=\"automatic\"\r\n", reg);
        // A list the service created goes; one that was there is rebuilt as it was.
        Assert.Contains("[-HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Google\\Chrome\\URLBlocklist]\r\n", reg);
        Assert.DoesNotContain("[HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Google\\Chrome\\URLBlocklist]", reg);
        Assert.Contains(
            "[-HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Google\\Chrome\\ExtensionInstallForcelist]\r\n" +
            "[HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Google\\Chrome\\ExtensionInstallForcelist]\r\n" +
            "\"1\"=\"abc;https://example.com/u\\\"pdate\"\r\n", reg);
        // The service's own keys are deleted.
        Assert.Contains("[-HKEY_LOCAL_MACHINE\\SOFTWARE\\Google\\Chrome\\NativeMessagingHosts\\com.smartstudyblocker.host]\r\n", reg);
    }

    [Fact]
    public void The_restore_plan_escapes_backslashes()
    {
        var reg = RestorePlan.Build([Managed(Chrome, "DownloadDirectory", @"C:\Users\me")], []);
        Assert.Contains("\"DownloadDirectory\"=\"C:\\\\Users\\\\me\"\r\n", reg);
    }

    [Fact]
    public void With_nothing_taken_over_the_plan_only_deletes_the_services_keys()
    {
        var reg = RestorePlan.Build([], PackagedInstall.KeysToDeleteOnUninstall);
        Assert.Equal(PackagedInstall.KeysToDeleteOnUninstall.Length, reg.Split("[-HKEY_LOCAL_MACHINE\\").Length - 1);
        Assert.Contains(@"[-HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\Application\SmartStudyBlockerService]", reg);
    }

    [Fact]
    public void Signed_in_users_may_start_the_service_and_nothing_more()
    {
        const string ace = "(A;;CCLCSWRPLOCRRC;;;AU)";
        // Added when absent, ahead of the SACL.
        Assert.Equal("D:(A;;CCLCRPWPDTLOCRRC;;;SY)" + ace + "S:(AU;FA;CCDCLCSWRPWPDTLOSDRCWDWO;;;WD)",
            PackagedInstall.WithStartForSignedInUsers("D:(A;;CCLCRPWPDTLOCRRC;;;SY)S:(AU;FA;CCDCLCSWRPWPDTLOSDRCWDWO;;;WD)"));
        // An existing Authenticated Users entry is replaced, not doubled.
        Assert.Equal("D:(A;;CCLCRPWPDTLOCRRC;;;SY)" + ace,
            PackagedInstall.WithStartForSignedInUsers("D:(A;;CCLCRPWPDTLOCRRC;;;SY)(A;;CCLCSWLOCRRC;;;AU)"));
        // Already right: unchanged.
        Assert.Equal("D:" + ace, PackagedInstall.WithStartForSignedInUsers("D:" + ace));
        Assert.Throws<InvalidOperationException>(() => PackagedInstall.WithStartForSignedInUsers("garbage"));
    }

    [Fact]
    public void The_policy_writer_reports_its_ledger_each_time_it_saves_it()
    {
        var reports = new List<IReadOnlyCollection<ManagedPolicy>>();
        var writer = new PolicyWriter(new FakePolicyRegistry(), new InMemoryStateStore(), ledgerSaved: reports.Add);

        writer.Apply(new LockState { IsLocked = true, DurationMs = 60_000, Rules = new RuleSet() });
        Assert.NotEmpty(reports);
        Assert.Contains(reports[^1], m => m.KeyPath == Chrome && m.Name == "DeveloperToolsAvailability");

        writer.Apply(new LockState { Rules = new RuleSet() });
        Assert.DoesNotContain(reports[^1], m => m.KeyPath == Chrome && m.Name == "DeveloperToolsAvailability");
    }
}
