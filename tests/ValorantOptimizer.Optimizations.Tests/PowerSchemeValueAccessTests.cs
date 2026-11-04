using ValorantOptimizer.Core;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests;

public sealed class PowerSchemeValueAccessTests
{
    [Fact]
    public void TryParseQueryOutput_English_ParsesSchemeNameAndValues()
    {
        string output = PowerSchemeTestData.EnglishQuery(
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid,
            acValue: 100,
            dcValue: 50);

        Assert.True(PowerSchemeValueAccess.TryParseQueryOutput(output, out PowerSettingQuery? setting));
        Assert.NotNull(setting);
        Assert.Equal(
            Guid.Parse(PowerSchemeTestData.BalancedSchemeGuid),
            setting!.ActiveSchemeGuid);
        Assert.Equal("Balanced", setting.ActiveSchemeName);
        Assert.Equal(
            Guid.Parse(PowerSchemeTestData.ProcessorSubgroupGuid),
            setting.SubgroupGuid);
        Assert.Equal(
            Guid.Parse(PowerSchemeTestData.CoreParkingSettingGuid),
            setting.SettingGuid);
        Assert.Equal(100u, setting.AcValue);
        Assert.Equal(50u, setting.DcValue);
    }

    [Fact]
    public void TryParseQueryOutput_German_ParsesWithoutLocalizedKeywords()
    {
        string output = PowerSchemeTestData.GermanQuery(
            PowerSchemeTestData.UsbSubgroupGuid,
            PowerSchemeTestData.SelectiveSuspendSettingGuid,
            acValue: 0,
            dcValue: 1);

        Assert.True(PowerSchemeValueAccess.TryParseQueryOutput(output, out PowerSettingQuery? setting));
        Assert.NotNull(setting);
        Assert.Equal("Ausbalanciert", setting!.ActiveSchemeName);
        Assert.Equal(0u, setting.AcValue);
        Assert.Equal(1u, setting.DcValue);
    }

    [Fact]
    public void TryParseQueryOutput_MinimalOutput_UsesLastTwoHexTokens()
    {
        string output = PowerSchemeTestData.MinimalQuery(
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid,
            acValue: 42,
            dcValue: 7);

        Assert.True(PowerSchemeValueAccess.TryParseQueryOutput(output, out PowerSettingQuery? setting));
        Assert.NotNull(setting);
        Assert.Equal(42u, setting!.AcValue);
        Assert.Equal(7u, setting.DcValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("No power data here.")]
    [InlineData("Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e")]
    public void TryParseQueryOutput_WithoutEnoughData_ReturnsFalse(string? output)
    {
        Assert.False(PowerSchemeValueAccess.TryParseQueryOutput(output, out _));
    }

    [Fact]
    public void TryParseQueryOutput_WithSingleHexToken_ReturnsFalse()
    {
        string output =
            $"Power Scheme GUID: {PowerSchemeTestData.BalancedSchemeGuid}  (Balanced)\r\n" +
            $"  Subgroup GUID: {PowerSchemeTestData.ProcessorSubgroupGuid}\r\n" +
            $"    Power Setting GUID: {PowerSchemeTestData.CoreParkingSettingGuid}\r\n" +
            "    Current AC Power Setting Index: 0x00000064\r\n";

        Assert.False(PowerSchemeValueAccess.TryParseQueryOutput(output, out _));
    }

    [Fact]
    public async Task ReadAsync_InvokesExactQueryArgumentsAndReturnsParsedValue()
    {
        var fixture = new OptimizationFixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.EnglishQuery(
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid,
            acValue: 64));

        PowerSettingReadResult result = await PowerSchemeValueAccess.ReadAsync(
            fixture.Processes,
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid);

        Assert.True(result.Success);
        Assert.Equal(64u, result.Setting!.AcValue);
        (string FileName, IReadOnlyList<string> Arguments) invocation = Assert.Single(fixture.Processes.Invocations);
        Assert.Equal("powercfg.exe", invocation.FileName);
        Assert.Equal(
            ["/query", "SCHEME_CURRENT", PowerSchemeTestData.ProcessorSubgroupGuid, PowerSchemeTestData.CoreParkingSettingGuid],
            invocation.Arguments);
    }

    [Fact]
    public async Task ReadAsync_WhenPowerCfgFails_ReturnsFailureWithDetail()
    {
        var fixture = new OptimizationFixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(string.Empty, querySucceeds: false);

        PowerSettingReadResult result = await PowerSchemeValueAccess.ReadAsync(
            fixture.Processes,
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid);

        Assert.False(result.Success);
        Assert.Contains("powercfg /query", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("query failed", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_WhenOutputDescribesAnotherSetting_ReturnsFailure()
    {
        var fixture = new OptimizationFixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.EnglishQuery(
            PowerSchemeTestData.UsbSubgroupGuid,
            PowerSchemeTestData.SelectiveSuspendSettingGuid,
            acValue: 0));

        PowerSettingReadResult result = await PowerSchemeValueAccess.ReadAsync(
            fixture.Processes,
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid);

        Assert.False(result.Success);
        Assert.Contains("different power setting", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_WhenOutputIsUnreadable_ReturnsFailure()
    {
        var fixture = new OptimizationFixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler("unexpected output without hex values");

        PowerSettingReadResult result = await PowerSchemeValueAccess.ReadAsync(
            fixture.Processes,
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid);

        Assert.False(result.Success);
        Assert.Contains("unreadable", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_RejectsMalformedGuidArguments()
    {
        var fixture = new OptimizationFixture();

        await Assert.ThrowsAsync<ArgumentException>(() => PowerSchemeValueAccess.ReadAsync(
            fixture.Processes,
            "not-a-guid",
            PowerSchemeTestData.CoreParkingSettingGuid));

        await Assert.ThrowsAsync<ArgumentException>(() => PowerSchemeValueAccess.ReadAsync(
            fixture.Processes,
            PowerSchemeTestData.ProcessorSubgroupGuid,
            "also-not-a-guid"));
    }

    [Fact]
    public async Task SetAcValueAndActivateAsync_InvokesBothCommandsInOrder()
    {
        var fixture = new OptimizationFixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(string.Empty);

        PowerSettingWriteResult result = await PowerSchemeValueAccess.SetAcValueAndActivateAsync(
            fixture.Processes,
            PowerSchemeValueAccess.CurrentScheme,
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid,
            100);

        Assert.True(result.Success);
        Assert.Equal(2, fixture.Processes.Invocations.Count);
        Assert.Equal(
            [
                "/setacvalueindex",
                "SCHEME_CURRENT",
                PowerSchemeTestData.ProcessorSubgroupGuid,
                PowerSchemeTestData.CoreParkingSettingGuid,
                "100",
            ],
            fixture.Processes.Invocations[0].Arguments);
        Assert.Equal(["/setactive", "SCHEME_CURRENT"], fixture.Processes.Invocations[1].Arguments);
    }

    [Fact]
    public async Task SetAcValueAndActivateAsync_WhenSetFails_DoesNotActivate()
    {
        var fixture = new OptimizationFixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(string.Empty, setAcValueSucceeds: false);

        PowerSettingWriteResult result = await PowerSchemeValueAccess.SetAcValueAndActivateAsync(
            fixture.Processes,
            PowerSchemeValueAccess.CurrentScheme,
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid,
            100);

        Assert.False(result.Success);
        Assert.Single(fixture.Processes.Invocations);
        Assert.Contains("setacvalueindex", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetAcValueAndActivateAsync_WhenActivateFails_ReturnsFailure()
    {
        var fixture = new OptimizationFixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(string.Empty, setActiveSucceeds: false);

        PowerSettingWriteResult result = await PowerSchemeValueAccess.SetAcValueAndActivateAsync(
            fixture.Processes,
            PowerSchemeValueAccess.CurrentScheme,
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid,
            100);

        Assert.False(result.Success);
        Assert.Equal(2, fixture.Processes.Invocations.Count);
        Assert.Contains("setactive", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateMetadata_StoresSchemeGuidAndDecimalAcValue()
    {
        PowerSettingQuery setting = new()
        {
            ActiveSchemeGuid = Guid.Parse(PowerSchemeTestData.BalancedSchemeGuid),
            ActiveSchemeName = "Balanced",
            SubgroupGuid = Guid.Parse(PowerSchemeTestData.ProcessorSubgroupGuid),
            SettingGuid = Guid.Parse(PowerSchemeTestData.CoreParkingSettingGuid),
            AcValue = 100,
        };

        Dictionary<string, string> metadata = PowerSchemeValueAccess.CreateMetadata(setting);

        Assert.Equal(
            PowerSchemeTestData.BalancedSchemeGuid,
            metadata[PowerSchemeValueAccess.ActiveSchemeGuidMetadataKey]);
        Assert.Equal("100", metadata[PowerSchemeValueAccess.OriginalAcValueMetadataKey]);
    }

    [Fact]
    public void TryReadMetadata_ValidMetadata_ParsesSchemeAndValue()
    {
        ChangeSet changeSet = PowerSchemeTestData.PowerSettingChangeSet(
            "cpu-core-parking",
            PowerSchemeTestData.BalancedSchemeGuid,
            originalAcValue: 25);

        Assert.True(PowerSchemeValueAccess.TryReadMetadata(
            changeSet,
            out Guid scheme,
            out uint value,
            out string? error));

        Assert.Equal(Guid.Parse(PowerSchemeTestData.BalancedSchemeGuid), scheme);
        Assert.Equal(25u, value);
        Assert.Null(error);
    }

    [Fact]
    public void TryReadMetadata_AcceptsHexForHandEditedBackups()
    {
        var changeSet = new ChangeSet
        {
            OptimizationId = "cpu-core-parking",
            Entries = [],
            Metadata = new Dictionary<string, string>
            {
                ["activeSchemeGuid"] = PowerSchemeTestData.BalancedSchemeGuid,
                ["originalAcValue"] = "0x00000064",
            },
        };

        Assert.True(PowerSchemeValueAccess.TryReadMetadata(
            changeSet,
            out _,
            out uint value,
            out _));
        Assert.Equal(100u, value);
    }

    [Fact]
    public void TryReadMetadata_MissingOrInvalidValues_ReturnsFalseWithError()
    {
        var cases = new List<Dictionary<string, string>>
        {
            new(),
            new() { ["activeSchemeGuid"] = PowerSchemeTestData.BalancedSchemeGuid },
            new() { ["activeSchemeGuid"] = "not-a-guid", ["originalAcValue"] = "100" },
            new() { ["activeSchemeGuid"] = Guid.Empty.ToString(), ["originalAcValue"] = "100" },
            new() { ["activeSchemeGuid"] = PowerSchemeTestData.BalancedSchemeGuid, ["originalAcValue"] = "not-a-number" },
            new() { ["activeSchemeGuid"] = PowerSchemeTestData.BalancedSchemeGuid, ["originalAcValue"] = "-1" },
        };

        foreach (Dictionary<string, string> metadata in cases)
        {
            var changeSet = new ChangeSet
            {
                OptimizationId = "cpu-core-parking",
                Entries = [],
                Metadata = metadata,
            };

            Assert.False(PowerSchemeValueAccess.TryReadMetadata(changeSet, out _, out _, out string? error));
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Fact]
    public void Summarize_IncludesExitCodeAndCollapsedOutput()
    {
        string summary = PowerSchemeValueAccess.Summarize(new ProcessResult(
            5,
            "line one\r\nline two",
            "some error"));

        Assert.Contains("exit code 5", summary, StringComparison.Ordinal);
        Assert.Contains("line one", summary, StringComparison.Ordinal);
        Assert.Contains("some error", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", summary, StringComparison.Ordinal);
    }
}
