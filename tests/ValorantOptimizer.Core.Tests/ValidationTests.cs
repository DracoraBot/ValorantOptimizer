using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Core.Tests;

public sealed class ChangeSetInspectorTests
{
    private const string SubKey = @"Software\ValorantOptimizer\Tests";

    private static ChangeSet CreateSet(RegistrySnapshotEntry entry) => new()
    {
        OptimizationId = "fake.setting",
        Entries = [entry],
    };

    private static RegistrySnapshotEntry Entry(bool existed, string serialized = "0", RegistryValueKind kind = RegistryValueKind.DWord) => new()
    {
        Hive = RegistryHive.CurrentUser,
        SubKey = SubKey,
        ValueName = "Enabled",
        Existed = existed,
        Kind = kind,
        SerializedData = serialized,
    };

    [Fact]
    public void Matches_when_value_and_kind_are_identical()
    {
        var registry = new FakeRegistryStore();
        registry.SetDword(RegistryHive.CurrentUser, SubKey, "Enabled", 0);

        Assert.True(ChangeSetInspector.Matches(registry, CreateSet(Entry(true)), out List<string> mismatches));
        Assert.Empty(mismatches);
    }

    [Fact]
    public void Mismatch_when_value_differs()
    {
        var registry = new FakeRegistryStore();
        registry.SetDword(RegistryHive.CurrentUser, SubKey, "Enabled", 1);

        Assert.False(ChangeSetInspector.Matches(registry, CreateSet(Entry(true)), out List<string> mismatches));
        Assert.Contains("expected '0'", Assert.Single(mismatches));
    }

    [Fact]
    public void Mismatch_when_value_should_be_absent_but_is_present()
    {
        var registry = new FakeRegistryStore();
        registry.SetDword(RegistryHive.CurrentUser, SubKey, "Enabled", 1);

        Assert.False(ChangeSetInspector.Matches(registry, CreateSet(Entry(false)), out List<string> mismatches));
        Assert.Contains("still present", Assert.Single(mismatches));
    }

    [Fact]
    public void Mismatch_when_value_is_missing()
    {
        var registry = new FakeRegistryStore();

        Assert.False(ChangeSetInspector.Matches(registry, CreateSet(Entry(true)), out List<string> mismatches));
        Assert.Contains("missing", Assert.Single(mismatches));
    }

    [Fact]
    public void Mismatch_when_kind_differs()
    {
        var registry = new FakeRegistryStore();
        registry.SetString(RegistryHive.CurrentUser, SubKey, "Enabled", "0");

        Assert.False(ChangeSetInspector.Matches(registry, CreateSet(Entry(true)), out List<string> mismatches));
        Assert.Contains("has kind", Assert.Single(mismatches));
    }

    [Fact]
    public void Describe_formats_values_and_absences()
    {
        List<string> descriptions = ChangeSetInspector.Describe([Entry(true), Entry(false)]);

        Assert.Contains("= 0", descriptions[0]);
        Assert.Contains("(not present)", descriptions[1]);
    }
}

public sealed class RestoreScopeValidatorTests
{
    private static OptimizationDescriptor Descriptor() => new()
    {
        Id = "fake.setting",
        Name = "Fake",
        Category = OptimizationCategory.Windows,
        Risk = RiskLevel.Safe,
        Summary = "Test.",
        WhatItChanges = "Test.",
        Why = "Test.",
        ManagedScopes = [new RegistryScope(RegistryHive.CurrentUser, @"Software\ValorantOptimizer")],
    };

    private static ChangeSet Backup(RegistryHive hive = RegistryHive.CurrentUser, string subKey = @"Software\ValorantOptimizer\Tests", RegistryValueKind kind = RegistryValueKind.DWord) => new()
    {
        OptimizationId = "fake.setting",
        Entries =
        [
            new RegistrySnapshotEntry
            {
                Hive = hive,
                SubKey = subKey,
                ValueName = "Enabled",
                Existed = true,
                Kind = kind,
                SerializedData = "0",
            },
        ],
    };

    [Fact]
    public void Accepts_entries_inside_declared_scopes()
    {
        Assert.True(RestoreScopeValidator.IsValid(Descriptor(), Backup(), out string error));
        Assert.Empty(error);
    }

    [Fact]
    public void Rejects_entries_outside_declared_scopes()
    {
        Assert.False(RestoreScopeValidator.IsValid(Descriptor(), Backup(hive: RegistryHive.LocalMachine), out string error));
        Assert.Contains("outside the declared scopes", error);
    }

    [Fact]
    public void Rejects_backup_for_another_optimization()
    {
        ChangeSet backup = Backup() with { OptimizationId = "other.setting" };

        Assert.False(RestoreScopeValidator.IsValid(Descriptor(), backup, out string error));
        Assert.Contains("belongs to", error);
    }

    [Fact]
    public void Rejects_unknown_value_kind()
    {
        Assert.False(RestoreScopeValidator.IsValid(Descriptor(), Backup(kind: RegistryValueKind.Unknown), out string error));
        Assert.Contains("unknown value kind", error);
    }

    [Fact]
    public void Rejects_future_schema_version()
    {
        Assert.False(RestoreScopeValidator.IsValid(Descriptor(), Backup() with { SchemaVersion = 2 }, out string error));
        Assert.Contains("schema version", error);
    }
}

public sealed class OptimizationCatalogValidatorTests
{
    [Fact]
    public void Valid_catalog_has_no_errors()
    {
        Assert.Empty(OptimizationCatalogValidator.Validate([new FakeOptimization()]));
    }

    [Fact]
    public void Duplicate_ids_are_reported()
    {
        IReadOnlyList<string> errors = OptimizationCatalogValidator.Validate(
            [new FakeOptimization("same.id"), new FakeOptimization("same.id")]);

        Assert.Contains(errors, error => error.Contains("Duplicate"));
    }

    [Fact]
    public void Invalid_id_is_reported()
    {
        OptimizationDescriptor descriptor = new FakeOptimization().Descriptor with { Id = "Bad ID!" };
        var optimization = new FakeOptimization(descriptor: descriptor);

        IReadOnlyList<string> errors = OptimizationCatalogValidator.Validate([optimization]);

        Assert.Contains(errors, error => error.Contains("not a valid optimization id"));
    }

    [Fact]
    public void Missing_required_text_is_reported()
    {
        OptimizationDescriptor descriptor = new FakeOptimization().Descriptor with { Summary = string.Empty };

        IReadOnlyList<string> errors = OptimizationCatalogValidator.Validate([new FakeOptimization(descriptor: descriptor)]);

        Assert.Contains(errors, error => error.Contains("no summary"));
    }
}
