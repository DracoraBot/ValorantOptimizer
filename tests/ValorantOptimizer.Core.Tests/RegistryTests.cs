using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Core.Tests;

public sealed class RegistryValueCodecTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(4294967295u)]
    public void Dword_round_trips(uint value)
    {
        string serialized = RegistryValueCodec.Serialize(unchecked((int)value), RegistryValueKind.DWord);
        object deserialized = RegistryValueCodec.Deserialize(serialized, RegistryValueKind.DWord);

        Assert.Equal(value.ToString(), serialized);
        Assert.Equal(serialized, RegistryValueCodec.Serialize(deserialized, RegistryValueKind.DWord));
    }

    [Fact]
    public void Qword_round_trips()
    {
        const ulong value = ulong.MaxValue;
        string serialized = RegistryValueCodec.Serialize(unchecked((long)value), RegistryValueKind.QWord);
        object deserialized = RegistryValueCodec.Deserialize(serialized, RegistryValueKind.QWord);

        Assert.Equal("18446744073709551615", serialized);
        Assert.Equal(serialized, RegistryValueCodec.Serialize(deserialized, RegistryValueKind.QWord));
    }

    [Fact]
    public void String_and_expand_string_round_trip()
    {
        const string value = @"C:\Riot Games\VALORANT\live\VALORANT.exe";
        Assert.Equal(value, RegistryValueCodec.Serialize(value, RegistryValueKind.String));
        Assert.Equal(value, RegistryValueCodec.Deserialize(value, RegistryValueKind.ExpandString));
    }

    [Fact]
    public void Binary_round_trips_as_base64()
    {
        byte[] value = [0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        string serialized = RegistryValueCodec.Serialize(value, RegistryValueKind.Binary);
        var deserialized = (byte[])RegistryValueCodec.Deserialize(serialized, RegistryValueKind.Binary);

        Assert.Equal(value, deserialized);
    }

    [Fact]
    public void Multi_string_round_trips()
    {
        string[] value = ["one", "two", "three"];
        string serialized = RegistryValueCodec.Serialize(value, RegistryValueKind.MultiString);
        var deserialized = (string[])RegistryValueCodec.Deserialize(serialized, RegistryValueKind.MultiString);

        Assert.Equal(value, deserialized);
    }
}

public sealed class RegistrySnapshotTests
{
    private const string SubKey = @"Software\ValorantOptimizer\Tests";
    private const string ValueName = "Enabled";

    [Fact]
    public void Capture_missing_value_is_recorded_as_not_present()
    {
        var store = new FakeRegistryStore();

        RegistrySnapshotEntry entry = RegistrySnapshot.Capture(store, RegistryHive.CurrentUser, SubKey, ValueName);

        Assert.False(entry.Existed);
        Assert.Null(entry.SerializedData);
    }

    [Fact]
    public void Restore_deletes_value_that_did_not_exist()
    {
        var store = new FakeRegistryStore();
        store.SetDword(RegistryHive.CurrentUser, SubKey, ValueName, 1);
        RegistrySnapshotEntry entry = new()
        {
            Hive = RegistryHive.CurrentUser,
            SubKey = SubKey,
            ValueName = ValueName,
            Existed = false,
        };

        RegistrySnapshot.Restore(store, entry);

        Assert.Null(store.ReadValue(RegistryHive.CurrentUser, SubKey, ValueName));
    }

    [Fact]
    public void Restore_reinstates_exact_original_dword()
    {
        var store = new FakeRegistryStore();
        store.SetDword(RegistryHive.CurrentUser, SubKey, ValueName, 0);
        RegistrySnapshotEntry entry = RegistrySnapshot.Capture(store, RegistryHive.CurrentUser, SubKey, ValueName);
        store.SetDword(RegistryHive.CurrentUser, SubKey, ValueName, 1);

        RegistrySnapshot.Restore(store, entry);

        RegistryValue restored = Assert.IsType<RegistryValue>(store.ReadValue(RegistryHive.CurrentUser, SubKey, ValueName));
        Assert.Equal(RegistryValueKind.DWord, restored.Kind);
        Assert.Equal("0", restored.DisplayValue);
    }

    [Fact]
    public void CaptureMany_captures_each_target()
    {
        var store = new FakeRegistryStore();
        store.SetString(RegistryHive.CurrentUser, SubKey, "Path", "abc");

        List<RegistrySnapshotEntry> entries = RegistrySnapshot.CaptureMany(
            store,
            new RegistryTarget(RegistryHive.CurrentUser, SubKey, "Path"),
            new RegistryTarget(RegistryHive.CurrentUser, SubKey, "Missing"));

        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].Existed);
        Assert.False(entries[1].Existed);
    }

    [Fact]
    public void Fake_store_is_case_insensitive_like_windows()
    {
        var store = new FakeRegistryStore();
        store.SetDword(RegistryHive.CurrentUser, @"Software\ValorantOptimizer\Tests", "Enabled", 2);

        RegistryValue? value = store.ReadValue(RegistryHive.CurrentUser, @"software\valorantoptimizer\tests", "ENABLED");

        Assert.NotNull(value);
        Assert.Equal("2", value!.DisplayValue);
    }
}
