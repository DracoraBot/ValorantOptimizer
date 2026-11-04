using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.TestSupport;

namespace ValorantOptimizer.Optimizations.Tests;

/// <summary>Shared fake data and helpers for the power-scheme modules' tests.</summary>
internal static class PowerSchemeTestData
{
    internal const string ProcessorSubgroupGuid = "54533251-82be-4824-96c1-47b60b740d00";
    internal const string CoreParkingSettingGuid = "0cc5b647-c1df-4637-891a-dec35c318583";
    internal const string UsbSubgroupGuid = "2a737441-1930-4402-8d77-b2bebba308a3";
    internal const string SelectiveSuspendSettingGuid = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    internal const string BalancedSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    internal const string HighPerformanceSchemeGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    internal static string EnglishQuery(
        string subgroupGuid,
        string settingGuid,
        uint acValue,
        uint dcValue = 0,
        string schemeGuid = BalancedSchemeGuid,
        string schemeName = "Balanced")
    {
        return
            $"Power Scheme GUID: {schemeGuid}  ({schemeName})\r\n" +
            $"  Subgroup GUID: {subgroupGuid}  (Processor power management)\r\n" +
            $"    Power Setting GUID: {settingGuid}  (Test setting)\r\n" +
            "      Minimum Possible Setting: 0x00000000\r\n" +
            "      Maximum Possible Setting: 0x00000064\r\n" +
            "      Possible Settings increment: 0x00000001\r\n" +
            "      Possible Settings units: %\r\n" +
            $"    Current AC Power Setting Index: 0x{acValue:X8}\r\n" +
            $"    Current DC Power Setting Index: 0x{dcValue:X8}\r\n";
    }

    internal static string GermanQuery(
        string subgroupGuid,
        string settingGuid,
        uint acValue,
        uint dcValue = 0,
        string schemeGuid = BalancedSchemeGuid,
        string schemeName = "Ausbalanciert")
    {
        return
            $"Energieschema-GUID: {schemeGuid}  ({schemeName})\r\n" +
            $"  Untergruppe GUID: {subgroupGuid}  (Prozessorenergieverwaltung)\r\n" +
            $"    Energieeinstellungs-GUID: {settingGuid}  (Testeinstellung)\r\n" +
            "      Minimal mögliche Einstellung: 0x00000000\r\n" +
            "      Maximal mögliche Einstellung: 0x00000064\r\n" +
            "      Inkrement für mögliche Einstellungen: 0x00000001\r\n" +
            "      Einheit für mögliche Einstellungen: %\r\n" +
            $"    Aktueller Index der Netzeinstellung: 0x{acValue:X8}\r\n" +
            $"    Aktueller Index der Batterieeinstellung: 0x{dcValue:X8}\r\n";
    }

    internal static string MinimalQuery(
        string subgroupGuid,
        string settingGuid,
        uint acValue,
        uint dcValue,
        string schemeGuid = BalancedSchemeGuid,
        string schemeName = "Balanced")
    {
        return
            $"Power Scheme GUID: {schemeGuid}  ({schemeName})\r\n" +
            $"  Subgroup GUID: {subgroupGuid}\r\n" +
            $"    Power Setting GUID: {settingGuid}\r\n" +
            $"    Current AC Power Setting Index: 0x{acValue:X8}\r\n" +
            $"    Current DC Power Setting Index: 0x{dcValue:X8}\r\n";
    }

    internal static Func<string, IReadOnlyList<string>, ProcessResult> Handler(
        string queryOutput,
        bool querySucceeds = true,
        bool setAcValueSucceeds = true,
        bool setActiveSucceeds = true)
    {
        return (_, arguments) =>
        {
            string command = arguments.Count > 0 ? arguments[0] : string.Empty;
            return command switch
            {
                "/query" => querySucceeds
                    ? new ProcessResult(0, queryOutput, string.Empty)
                    : new ProcessResult(1, string.Empty, "query failed"),
                "/setacvalueindex" => setAcValueSucceeds
                    ? new ProcessResult(0, string.Empty, string.Empty)
                    : new ProcessResult(1, string.Empty, "setacvalueindex failed"),
                "/setactive" => setActiveSucceeds
                    ? new ProcessResult(0, string.Empty, string.Empty)
                    : new ProcessResult(1, string.Empty, "setactive failed"),
                _ => new ProcessResult(1, string.Empty, $"unexpected command '{command}'"),
            };
        };
    }

    internal static ChangeSet ChangeSetWithoutMetadata(string optimizationId) => new()
    {
        OptimizationId = optimizationId,
        Entries = [],
    };

    internal static ChangeSet PowerSettingChangeSet(
        string optimizationId,
        string activeSchemeGuid,
        uint originalAcValue) => new()
    {
        OptimizationId = optimizationId,
        Entries = [],
        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["activeSchemeGuid"] = activeSchemeGuid,
            ["originalAcValue"] = originalAcValue.ToString(CultureInfo.InvariantCulture),
        },
    };
}

/// <summary>One fake process/registry context for a power-setting or network module test.</summary>
internal sealed class OptimizationFixture
{
    public OptimizationFixture(bool isElevated = false)
    {
        Context = new OptimizationContext(Registry, Processes, isElevated);
    }

    public FakeProcessRunner Processes { get; } = new();

    public FakeRegistryStore Registry { get; } = new();

    public OptimizationContext Context { get; }
}

/// <summary>Shared constants and seeding for the network adapter module tests.</summary>
internal static class NetworkAdapterTestData
{
    internal const string ClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    internal static string AdapterKey(string instanceName) => ClassKey + "\\" + instanceName;

    internal static void SeedAdapter(
        FakeRegistryStore registry,
        string instanceName,
        string driverDesc,
        int? pnpCapabilities = null,
        RegistryValueKind pnpKind = Microsoft.Win32.RegistryValueKind.DWord,
        int? characteristics = null,
        int? ifType = null)
    {
        string subKey = AdapterKey(instanceName);
        registry.SetString(
            Microsoft.Win32.RegistryHive.LocalMachine,
            subKey,
            "DriverDesc",
            driverDesc);

        if (pnpCapabilities is int pnpValue)
        {
            registry.WriteValue(
                Microsoft.Win32.RegistryHive.LocalMachine,
                subKey,
                "PnPCapabilities",
                pnpKind,
                pnpKind == Microsoft.Win32.RegistryValueKind.DWord
                    ? RegistryValueCodec.ToRegistryDword(unchecked((uint)pnpValue))
                    : pnpValue.ToString(CultureInfo.InvariantCulture));
        }

        if (characteristics is int characteristicsValue)
        {
            registry.SetDword(
                Microsoft.Win32.RegistryHive.LocalMachine,
                subKey,
                "Characteristics",
                unchecked((uint)characteristicsValue));
        }

        if (ifType is int ifTypeValue)
        {
            registry.SetDword(
                Microsoft.Win32.RegistryHive.LocalMachine,
                subKey,
                "*IfType",
                unchecked((uint)ifTypeValue));
        }
    }

    internal static RegistryValue? ReadPnpCapabilities(
        FakeRegistryStore registry,
        string instanceName) =>
        registry.ReadValue(
            Microsoft.Win32.RegistryHive.LocalMachine,
            AdapterKey(instanceName),
            "PnPCapabilities");
}
