using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Diagnostics.Tests;

public sealed class StartupAppScannerTests
{
    [Fact]
    public async Task Run_entries_without_an_approval_entry_are_enabled_and_never_written()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();
        registry.SetString(
            RegistryHive.CurrentUser,
            StartupAppScanner.UserRunKey,
            "Notepad",
            @"C:\Windows\System32\notepad.exe");

        StartupAppScanner scanner = CreateScanner(registry, temp);
        int writesBefore = registry.WriteCount;

        IReadOnlyList<StartupAppEntry> entries = await scanner.ScanAsync();

        StartupAppEntry entry = Assert.Single(entries);
        Assert.Equal("Notepad", entry.Name);
        Assert.Equal(@"C:\Windows\System32\notepad.exe", entry.Command);
        Assert.Equal(StartupSource.UserRunKey, entry.Source);
        Assert.True(entry.IsEnabled);
        Assert.StartsWith(StartupAppScanner.ReviewOnlyNote, entry.Note);
        Assert.Equal(writesBefore, registry.WriteCount);
        Assert.Equal(0, registry.DeleteCount);
    }

    [Theory]
    [InlineData(0x02)]
    [InlineData(0x06)]
    public async Task Startup_approved_enabled_bytes_keep_entries_enabled(byte firstByte)
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();
        registry.SetString(RegistryHive.CurrentUser, StartupAppScanner.UserRunKey, "App", @"C:\Apps\app.exe");
        registry.SetBinary(
            RegistryHive.CurrentUser,
            StartupAppScanner.UserApprovedRunKey,
            "App",
            ApprovedBytes(firstByte));

        StartupAppEntry entry = Assert.Single(await CreateScanner(registry, temp).ScanAsync());

        Assert.True(entry.IsEnabled);
    }

    [Theory]
    [InlineData(0x03)]
    [InlineData(0x07)]
    public async Task Startup_approved_disabled_bytes_disable_entries(byte firstByte)
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();
        registry.SetString(RegistryHive.CurrentUser, StartupAppScanner.UserRunKey, "App", @"C:\Apps\app.exe");
        registry.SetBinary(
            RegistryHive.CurrentUser,
            StartupAppScanner.UserApprovedRunKey,
            "App",
            ApprovedBytes(firstByte));

        StartupAppEntry entry = Assert.Single(await CreateScanner(registry, temp).ScanAsync());

        Assert.False(entry.IsEnabled);
    }

    [Fact]
    public async Task Unknown_approval_bytes_default_to_enabled()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();
        registry.SetString(RegistryHive.CurrentUser, StartupAppScanner.UserRunKey, "App", @"C:\Apps\app.exe");
        registry.SetBinary(
            RegistryHive.CurrentUser,
            StartupAppScanner.UserApprovedRunKey,
            "App",
            ApprovedBytes(0x01));

        StartupAppEntry entry = Assert.Single(await CreateScanner(registry, temp).ScanAsync());

        Assert.True(entry.IsEnabled);
    }

    [Fact]
    public void ParseStartupApproved_handles_missing_wrong_kind_and_empty_data()
    {
        Assert.Equal(
            StartupAppScanner.StartupApprovalState.NotSpecified,
            StartupAppScanner.ParseStartupApproved(null));

        var wrongKind = new RegistryValue(
            RegistryHive.CurrentUser, "key", "App", RegistryValueKind.DWord, 2);
        Assert.Equal(
            StartupAppScanner.StartupApprovalState.NotSpecified,
            StartupAppScanner.ParseStartupApproved(wrongKind));

        var empty = new RegistryValue(
            RegistryHive.CurrentUser, "key", "App", RegistryValueKind.Binary, Array.Empty<byte>());
        Assert.Equal(
            StartupAppScanner.StartupApprovalState.NotSpecified,
            StartupAppScanner.ParseStartupApproved(empty));

        var enabled = new RegistryValue(
            RegistryHive.CurrentUser, "key", "App", RegistryValueKind.Binary, new byte[] { 0x02 });
        Assert.Equal(
            StartupAppScanner.StartupApprovalState.Enabled,
            StartupAppScanner.ParseStartupApproved(enabled));
    }

    [Fact]
    public async Task Machine_run32_approval_disables_a_machine_entry()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();
        registry.SetString(RegistryHive.LocalMachine, StartupAppScanner.MachineRunKey, "MachineApp", @"C:\Apps\m.exe");
        registry.SetBinary(
            RegistryHive.LocalMachine,
            StartupAppScanner.MachineApprovedRun32Key,
            "MachineApp",
            ApprovedBytes(0x03));

        StartupAppEntry entry = Assert.Single(await CreateScanner(registry, temp).ScanAsync());

        Assert.Equal(StartupSource.MachineRunKey, entry.Source);
        Assert.False(entry.IsEnabled);
    }

    [Fact]
    public async Task Entries_are_sorted_enabled_first_then_by_name()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();
        registry.SetString(RegistryHive.CurrentUser, StartupAppScanner.UserRunKey, "Zeta", @"C:\zeta.exe");
        registry.SetString(RegistryHive.CurrentUser, StartupAppScanner.UserRunKey, "alpha", @"C:\alpha.exe");
        registry.SetString(RegistryHive.CurrentUser, StartupAppScanner.UserRunKey, "Beta", @"C:\beta.exe");
        registry.SetBinary(
            RegistryHive.CurrentUser,
            StartupAppScanner.UserApprovedRunKey,
            "Beta",
            ApprovedBytes(0x03));

        IReadOnlyList<StartupAppEntry> entries = await CreateScanner(registry, temp).ScanAsync();

        Assert.Equal(["alpha", "Zeta", "Beta"], entries.Select(entry => entry.Name));
        Assert.True(entries[0].IsEnabled);
        Assert.True(entries[1].IsEnabled);
        Assert.False(entries[2].IsEnabled);
    }

    [Fact]
    public async Task Startup_folders_are_enumerated_from_the_overridden_roots()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();
        string userFolder = temp.Combine("user-startup");
        string machineFolder = temp.Combine("machine-startup");
        string userLink = TestFiles.CreateFile(Path.Combine(userFolder, "OneDrive.lnk"));
        string userExe = TestFiles.CreateFile(Path.Combine(userFolder, "App.exe"));
        string machineTool = TestFiles.CreateFile(Path.Combine(machineFolder, "DriverTool.exe"));

        IReadOnlyList<StartupAppEntry> entries = await CreateScanner(
            registry,
            temp,
            userFolder,
            machineFolder).ScanAsync();

        Assert.Equal(3, entries.Count);
        StartupAppEntry link = Assert.Single(entries, entry => entry.Name == "OneDrive");
        Assert.Equal(userLink, link.Command);
        Assert.Equal(StartupSource.UserStartupFolder, link.Source);
        Assert.True(link.IsEnabled);

        StartupAppEntry app = Assert.Single(entries, entry => entry.Name == "App");
        Assert.Equal(userExe, app.Command);

        StartupAppEntry tool = Assert.Single(entries, entry => entry.Name == "DriverTool");
        Assert.Equal(machineTool, tool.Command);
        Assert.Equal(StartupSource.MachineStartupFolder, tool.Source);

        Assert.All(entries, entry => Assert.StartsWith(StartupAppScanner.ReviewOnlyNote, entry.Note));
    }

    [Fact]
    public async Task Startup_folder_approval_disables_the_matching_entry()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();
        string machineFolder = temp.Combine("machine-startup");
        TestFiles.CreateFile(Path.Combine(machineFolder, "MachineTool.exe"));
        registry.SetBinary(
            RegistryHive.LocalMachine,
            StartupAppScanner.MachineApprovedStartupFolderKey,
            "MachineTool.exe",
            ApprovedBytes(0x03));

        StartupAppEntry entry = Assert.Single(await CreateScanner(
            registry,
            temp,
            machineFolder: machineFolder).ScanAsync());

        Assert.False(entry.IsEnabled);
    }

    [Fact]
    public async Task Startup_folder_approval_falls_back_to_the_name_without_extension()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();
        string machineFolder = temp.Combine("machine-startup");
        TestFiles.CreateFile(Path.Combine(machineFolder, "Tool.lnk"));
        registry.SetBinary(
            RegistryHive.LocalMachine,
            StartupAppScanner.MachineApprovedStartupFolderKey,
            "Tool",
            ApprovedBytes(0x07));

        StartupAppEntry entry = Assert.Single(await CreateScanner(
            registry,
            temp,
            machineFolder: machineFolder).ScanAsync());

        Assert.False(entry.IsEnabled);
    }

    [Fact]
    public async Task Known_anti_cheat_gpu_and_audio_apps_are_flagged()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();
        const string userRun = StartupAppScanner.UserRunKey;
        registry.SetString(RegistryHive.CurrentUser, userRun, "Vanguard", @"C:\Program Files\Riot Vanguard\vgc.exe");
        registry.SetString(RegistryHive.CurrentUser, userRun, "NVIDIA App", @"C:\Program Files\NVIDIA Corporation\nvcontainer.exe");
        registry.SetString(RegistryHive.CurrentUser, userRun, "Realtek Audio Console", @"C:\Program Files\Realtek\Audio\RtkAudUService64.exe");
        registry.SetString(RegistryHive.CurrentUser, userRun, "OneDrive", @"C:\Users\Test\OneDrive.exe");
        registry.SetString(RegistryHive.CurrentUser, userRun, "Discord", @"C:\Users\Test\Discord.exe");
        registry.SetString(RegistryHive.CurrentUser, userRun, "Steam", @"C:\Program Files (x86)\Steam\steam.exe");
        registry.SetString(RegistryHive.CurrentUser, userRun, "AMD Software", @"C:\Program Files\AMD\RadeonSoftware.exe");
        registry.SetString(RegistryHive.CurrentUser, userRun, "Intel Graphics", @"C:\Program Files\Intel\igfxTray.exe");

        IReadOnlyList<StartupAppEntry> entries = await CreateScanner(registry, temp).ScanAsync();
        Dictionary<string, StartupAppEntry> byName = entries.ToDictionary(entry => entry.Name);

        Assert.Contains(StartupAppFlags.AntiCheat, byName["Vanguard"].Flags);
        Assert.Contains(StartupAppFlags.RiotGames, byName["Vanguard"].Flags);
        Assert.Contains(StartupAppFlags.Nvidia, byName["NVIDIA App"].Flags);
        Assert.Contains(StartupAppFlags.Audio, byName["Realtek Audio Console"].Flags);
        Assert.Contains(StartupAppFlags.OneDrive, byName["OneDrive"].Flags);
        Assert.Contains(StartupAppFlags.Discord, byName["Discord"].Flags);
        Assert.Contains(StartupAppFlags.Steam, byName["Steam"].Flags);
        Assert.Contains(StartupAppFlags.Amd, byName["AMD Software"].Flags);
        Assert.Contains(StartupAppFlags.Intel, byName["Intel Graphics"].Flags);
        Assert.Contains("do not disable", byName["Vanguard"].Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Publisher_is_resolved_with_the_injected_resolver()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();
        registry.SetString(RegistryHive.CurrentUser, StartupAppScanner.UserRunKey, "Known App", @"C:\Apps\known.exe");

        StartupAppScanner scanner = CreateScanner(
            registry,
            temp,
            publisher: command => command.Contains("known", StringComparison.OrdinalIgnoreCase)
                ? "Acme Corporation"
                : null);

        StartupAppEntry entry = Assert.Single(await scanner.ScanAsync());

        Assert.Equal("Acme Corporation", entry.Publisher);
    }

    [Fact]
    public async Task Ordinary_apps_receive_no_identity_flags()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();
        registry.SetString(
            RegistryHive.CurrentUser,
            StartupAppScanner.UserRunKey,
            "Notepad++",
            @"C:\Program Files\Notepad++\notepad++.exe");

        StartupAppEntry entry = Assert.Single(await CreateScanner(registry, temp).ScanAsync());

        Assert.Empty(entry.Flags);
    }

    [Fact]
    public void Amd64_driver_store_paths_are_not_flagged_as_amd_hardware()
    {
        List<string> flags = StartupAppScanner.DetectFlags(
            "RtkAudUService",
            @"C:\WINDOWS\System32\DriverStore\FileRepository\realtekservice.inf_amd64_a112e46fc636d880\RtkAudUService64.exe",
            "Realtek Semiconductor");

        Assert.DoesNotContain(StartupAppFlags.Amd, flags);
        Assert.Contains(StartupAppFlags.Audio, flags);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --background", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe --silent", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Windows\System32\rundll32.exe shell32.dll,Control_RunDLL", @"C:\Windows\System32\rundll32.exe")]
    [InlineData("notepad", null)]
    public void TryExtractExecutablePath_handles_quoted_and_unquoted_commands(
        string command,
        string? expected)
    {
        Assert.Equal(expected, StartupAppScanner.TryExtractExecutablePath(command));
    }

    [Fact]
    public async Task Missing_startup_folders_are_skipped_without_throwing()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();

        IReadOnlyList<StartupAppEntry> entries = await CreateScanner(
            registry,
            temp,
            userFolder: temp.Combine("does-not-exist-user"),
            machineFolder: temp.Combine("does-not-exist-machine")).ScanAsync();

        Assert.Empty(entries);
    }

    private static StartupAppScanner CreateScanner(
        FakeRegistryStore registry,
        TempDirectory temp,
        string? userFolder = null,
        string? machineFolder = null,
        Func<string, string?>? publisher = null) =>
        new(
            registry,
            userFolder ?? temp.Combine("user-startup"),
            machineFolder ?? temp.Combine("machine-startup"),
            publisher);

    private static byte[] ApprovedBytes(byte first) => [first, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
}
