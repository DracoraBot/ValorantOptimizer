using System.Text.Json;
using Microsoft.Win32;
using ValorantOptimizer.Diagnostics;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Diagnostics.Tests;

public sealed class ValorantLocatorTests
{
    [Fact]
    public void Locator_accepts_the_registry_and_process_dependencies()
    {
        var locator = new ValorantLocator(new FakeRegistryStore(), new FakeProcessRunner());

        Assert.NotNull(locator);
    }

    [Fact]
    public async Task Locates_from_the_product_settings_full_path()
    {
        using var temp = new TempDirectory();
        string gameRoot = temp.Combine("Riot Games", "VALORANT");
        GameInstall install = CreateGameInstall(gameRoot);
        WriteProductSettings(temp, install.LiveDirectory, partialPath: null);

        ValorantInstallation result = await CreateLocator(temp).LocateAsync();

        Assert.True(result.IsFound);
        Assert.Equal(install.Executable, result.ExecutablePath);
        Assert.Equal(install.ShippingExecutable, result.ShippingExecutablePath);
        Assert.Equal(install.LiveDirectory, result.InstallDirectory);
        Assert.Equal(ValorantLocator.DetectionSourceProductSettings, result.DetectionSource);
        Assert.NotEmpty(result.Notes);
    }

    [Fact]
    public async Task Locates_from_the_product_settings_partial_path()
    {
        using var temp = new TempDirectory();
        string gameRoot = temp.Combine("Riot Games", "VALORANT");
        GameInstall install = CreateGameInstall(gameRoot);
        WriteProductSettings(temp, fullPath: null, partialPath: "VALORANT/live");

        ValorantInstallation result = await CreateLocator(temp, roots: [gameRoot]).LocateAsync();

        Assert.True(result.IsFound);
        Assert.Equal(install.Executable, result.ExecutablePath);
        Assert.Equal(ValorantLocator.DetectionSourceProductSettings, result.DetectionSource);
    }

    [Fact]
    public async Task Falls_back_to_a_running_process_path()
    {
        using var temp = new TempDirectory();
        GameInstall install = CreateGameInstall(temp.Combine("games", "VALORANT"));

        ValorantInstallation result = await CreateLocator(
            temp,
            roots: [],
            running: () => [install.Executable]).LocateAsync();

        Assert.True(result.IsFound);
        Assert.Equal(install.Executable, result.ExecutablePath);
        Assert.Equal(ValorantLocator.DetectionSourceRunningProcess, result.DetectionSource);
    }

    [Fact]
    public async Task Falls_back_to_the_uninstall_display_icon()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();
        GameInstall install = CreateGameInstall(temp.Combine("games", "VALORANT"));
        SeedUninstallEntry(
            registry,
            ValorantLocator.UninstallKey,
            displayName: "VALORANT",
            displayIcon: $"\"{install.Executable}\",0",
            installLocation: null);

        ValorantInstallation result = await CreateLocator(temp, registry).LocateAsync();

        Assert.True(result.IsFound);
        Assert.Equal(install.Executable, result.ExecutablePath);
        Assert.Equal(ValorantLocator.DetectionSourceUninstallRegistry, result.DetectionSource);
    }

    [Fact]
    public async Task Falls_back_to_the_uninstall_install_location()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();
        string gameRoot = temp.Combine("games", "VALORANT");
        GameInstall install = CreateGameInstall(gameRoot);
        SeedUninstallEntry(
            registry,
            ValorantLocator.UninstallKey,
            displayName: "VALORANT",
            displayIcon: null,
            installLocation: gameRoot);

        ValorantInstallation result = await CreateLocator(temp, registry).LocateAsync();

        Assert.True(result.IsFound);
        Assert.Equal(install.Executable, result.ExecutablePath);
        Assert.Equal(ValorantLocator.DetectionSourceUninstallRegistry, result.DetectionSource);
    }

    [Fact]
    public async Task Falls_back_to_a_wow6432node_uninstall_entry()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();
        GameInstall install = CreateGameInstall(temp.Combine("games", "VALORANT"));
        SeedUninstallEntry(
            registry,
            ValorantLocator.UninstallKey32,
            displayName: "VALORANT",
            displayIcon: install.Executable,
            installLocation: null);

        ValorantInstallation result = await CreateLocator(temp, registry).LocateAsync();

        Assert.True(result.IsFound);
        Assert.Equal(ValorantLocator.DetectionSourceUninstallRegistry, result.DetectionSource);
    }

    [Fact]
    public async Task Falls_back_to_a_common_install_root()
    {
        using var temp = new TempDirectory();
        string gameRoot = temp.Combine("Riot Games", "VALORANT");
        GameInstall install = CreateGameInstall(gameRoot);

        ValorantInstallation result = await CreateLocator(temp, roots: [gameRoot]).LocateAsync();

        Assert.True(result.IsFound);
        Assert.Equal(install.Executable, result.ExecutablePath);
        Assert.Equal(ValorantLocator.DetectionSourceCommonInstallRoot, result.DetectionSource);
    }

    [Fact]
    public async Task Returns_a_not_found_installation_with_useful_notes()
    {
        using var temp = new TempDirectory();
        var runner = new FakeProcessRunner();

        ValorantInstallation result = await new ValorantLocator(
            new FakeRegistryStore(),
            runner,
            programDataPath: temp.Path,
            installRoots: [temp.Combine("empty-root")],
            runningExecutablePaths: () => []).LocateAsync();

        Assert.False(result.IsFound);
        Assert.Null(result.ExecutablePath);
        Assert.Null(result.ShippingExecutablePath);
        Assert.Null(result.DetectionSource);
        Assert.Contains(result.Notes, note => note.Contains("product settings file was not found", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Notes, note => note.Contains("No VALORANT uninstall entry", StringComparison.Ordinal));
        Assert.Contains(result.Notes, note => note.Contains("No VALORANT.exe was found", StringComparison.Ordinal));
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task Malformed_product_settings_are_reported_and_other_sources_still_work()
    {
        using var temp = new TempDirectory();
        WriteRawProductSettings(temp, "{ this is not json");
        var registry = new FakeRegistryStore();
        GameInstall install = CreateGameInstall(temp.Combine("games", "VALORANT"));
        SeedUninstallEntry(
            registry,
            ValorantLocator.UninstallKey,
            displayName: "VALORANT",
            displayIcon: install.Executable,
            installLocation: null);

        ValorantInstallation result = await CreateLocator(temp, registry).LocateAsync();

        Assert.True(result.IsFound);
        Assert.Equal(ValorantLocator.DetectionSourceUninstallRegistry, result.DetectionSource);
        Assert.Contains(result.Notes, note => note.Contains("Could not read Riot product settings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Product_settings_take_precedence_over_uninstall_and_common_roots()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();

        GameInstall settingsInstall = CreateGameInstall(temp.Combine("settings", "VALORANT"));
        WriteProductSettings(temp, settingsInstall.LiveDirectory, partialPath: null);

        GameInstall uninstallInstall = CreateGameInstall(temp.Combine("uninstall", "VALORANT"));
        SeedUninstallEntry(
            registry,
            ValorantLocator.UninstallKey,
            displayName: "VALORANT",
            displayIcon: uninstallInstall.Executable,
            installLocation: null);

        GameInstall rootInstall = CreateGameInstall(temp.Combine("root", "VALORANT"));

        ValorantInstallation result = await CreateLocator(
            temp,
            registry,
            roots: [Path.GetDirectoryName(rootInstall.LiveDirectory)!]).LocateAsync();

        Assert.True(result.IsFound);
        Assert.Equal(settingsInstall.Executable, result.ExecutablePath);
        Assert.Equal(ValorantLocator.DetectionSourceProductSettings, result.DetectionSource);
    }

    [Fact]
    public async Task Stale_product_settings_path_is_returned_with_a_warning_note()
    {
        using var temp = new TempDirectory();
        string missingLive = temp.Combine("Missing", "VALORANT", "live");
        WriteProductSettings(temp, missingLive, partialPath: null);

        ValorantInstallation result = await CreateLocator(temp, roots: []).LocateAsync();

        Assert.True(result.IsFound);
        Assert.Equal(Path.Combine(missingLive, "VALORANT.exe"), result.ExecutablePath);
        Assert.Equal(ValorantLocator.DetectionSourceProductSettings, result.DetectionSource);
        Assert.Contains(result.Notes, note => note.Contains("does not exist on disk", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Riot_client_configuration_is_evidence_only()
    {
        using var temp = new TempDirectory();
        TestFiles.CreateDirectory(temp.Combine("Riot Games", "Riot Client"));

        ValorantInstallation result = await CreateLocator(temp, roots: []).LocateAsync();

        Assert.False(result.IsFound);
        Assert.Contains(
            result.Notes,
            note => note.Contains("Riot Client configuration is present", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Uninstall_display_icon_pointing_at_riot_client_derives_the_sibling_install()
    {
        using var temp = new TempDirectory();
        var registry = new FakeRegistryStore();

        string riotRoot = temp.Combine("Riot Games");
        string riotClient = TestFiles.CreateFile(Path.Combine(riotRoot, "Riot Client", "RiotClientServices.exe"));
        GameInstall install = CreateGameInstall(Path.Combine(riotRoot, "VALORANT"));
        SeedUninstallEntry(
            registry,
            ValorantLocator.UninstallKey,
            displayName: "VALORANT",
            displayIcon: riotClient,
            installLocation: null);

        ValorantInstallation result = await CreateLocator(temp, registry).LocateAsync();

        Assert.True(result.IsFound);
        Assert.Equal(install.Executable, result.ExecutablePath);
        Assert.Equal(ValorantLocator.DetectionSourceUninstallRegistry, result.DetectionSource);
    }

    [Fact]
    public void CleanEvidencePath_strips_quotes_and_icon_indexes()
    {
        string cleaned = ValorantLocator.CleanEvidencePath(
            "\"C:\\Games\\VALORANT\\live\\VALORANT.exe\",0")!;

        Assert.Equal(
            Path.GetFullPath(@"C:\Games\VALORANT\live\VALORANT.exe"),
            cleaned);
        Assert.Null(ValorantLocator.CleanEvidencePath(null));
        Assert.Null(ValorantLocator.CleanEvidencePath("   "));
    }

    [Fact]
    public void ExpandPartialInstallPath_joins_against_the_install_root_parent()
    {
        IReadOnlyList<string> expanded = ValorantLocator.ExpandPartialInstallPath(
            "VALORANT/live",
            [@"C:\Riot Games\VALORANT"]);

        Assert.Contains(Path.Combine(@"C:\Riot Games", "VALORANT", "live"), expanded);
    }

    [Fact]
    public void BuildShippingCandidates_derives_the_live_shipping_binary()
    {
        IReadOnlyList<string> candidates = ValorantLocator.BuildShippingCandidates(@"C:\Riot Games\VALORANT");

        Assert.Contains(
            Path.Combine(
                @"C:\Riot Games\VALORANT",
                "live",
                "ShooterGame",
                "Binaries",
                "Win64",
                "VALORANT-Win64-Shipping.exe"),
            candidates);
    }

    private static ValorantLocator CreateLocator(
        TempDirectory temp,
        FakeRegistryStore? registry = null,
        IEnumerable<string>? roots = null,
        Func<IReadOnlyList<string>>? running = null) =>
        new(
            registry ?? new FakeRegistryStore(),
            new FakeProcessRunner(),
            programDataPath: temp.Path,
            installRoots: roots ?? [],
            runningExecutablePaths: running ?? (() => []));

    private static GameInstall CreateGameInstall(string gameRoot)
    {
        string live = Path.Combine(gameRoot, "live");
        string executable = TestFiles.CreateFile(Path.Combine(live, "VALORANT.exe"));
        string shipping = TestFiles.CreateFile(
            Path.Combine(live, "ShooterGame", "Binaries", "Win64", "VALORANT-Win64-Shipping.exe"));
        return new GameInstall(live, executable, shipping);
    }

    private static void SeedUninstallEntry(
        FakeRegistryStore registry,
        string uninstallKey,
        string displayName,
        string? displayIcon,
        string? installLocation)
    {
        string subKey = uninstallKey + @"\{11111111-1111-1111-1111-111111111111}";
        registry.SetString(RegistryHive.LocalMachine, subKey, "DisplayName", displayName);
        if (displayIcon is not null)
        {
            registry.SetString(RegistryHive.LocalMachine, subKey, "DisplayIcon", displayIcon);
        }

        if (installLocation is not null)
        {
            registry.SetString(RegistryHive.LocalMachine, subKey, "InstallLocation", installLocation);
        }
    }

    private static void WriteProductSettings(TempDirectory temp, string? fullPath, string? partialPath)
    {
        var payload = new Dictionary<string, string>();
        if (fullPath is not null)
        {
            payload["product_install_full_path"] = fullPath.Replace('\\', '/');
        }

        if (partialPath is not null)
        {
            payload["product_install_partial_path"] = partialPath;
        }

        WriteRawProductSettings(temp, JsonSerializer.Serialize(payload));
    }

    private static void WriteRawProductSettings(TempDirectory temp, string json)
    {
        string directory = temp.Combine("Riot Games", "Metadata", "valorant.live");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "valorant.live.product_settings.json"), json);
    }

    private sealed record GameInstall(string LiveDirectory, string Executable, string ShippingExecutable);
}
