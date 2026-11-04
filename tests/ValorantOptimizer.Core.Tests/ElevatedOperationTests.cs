using System.Text.Json;
using ValorantOptimizer.Core;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Core.Tests;

public sealed class ElevatedOperationTests
{
    [Fact]
    public void Request_round_trips_through_json()
    {
        ChangeSet changeSet = TestChangeSets.Create();
        ElevatedOperationRequest request = ElevatedOperationRequest.Create(
            "fake.setting", ElevatedOperationKind.Apply, changeSet);

        string json = JsonSerializer.Serialize(request, JsonDefaults.Options);
        ElevatedOperationRequest? loaded =
            JsonSerializer.Deserialize<ElevatedOperationRequest>(json, JsonDefaults.Options);

        Assert.NotNull(loaded);
        Assert.Equal(ElevatedOperationRequest.CurrentSchemaVersion, loaded!.SchemaVersion);
        Assert.Equal(request.RequestId, loaded.RequestId);
        Assert.Equal(request.OptimizationId, loaded.OptimizationId);
        Assert.Equal(ElevatedOperationKind.Apply, loaded.Operation);
        Assert.Equal(changeSet.Entries[0].SerializedData, loaded.ChangeSet.Entries[0].SerializedData);
        Assert.Equal(changeSet.Metadata["scheme"], loaded.ChangeSet.Metadata["scheme"]);
    }

    [Fact]
    public void Successful_response_maps_to_success_outcome()
    {
        var response = new ElevatedOperationResponse
        {
            RequestId = "abc",
            Success = true,
            Verified = true,
            Message = "Applied.",
            Changes = ["HKCU\\Test\\Value = 1"],
            Warnings = ["minor note"],
        };

        OperationOutcome outcome = response.ToOutcome();

        Assert.True(outcome.Success);
        Assert.True(outcome.Verified);
        Assert.Equal("Applied.", outcome.Message);
        Assert.Single(outcome.Changes);
        Assert.Single(outcome.Warnings);
    }

    [Fact]
    public void Failed_response_maps_to_failure_outcome()
    {
        var response = new ElevatedOperationResponse
        {
            RequestId = "abc",
            Success = false,
            RolledBack = true,
            Message = "Apply failed; original restored.",
            Error = "stack trace",
            Warnings = ["restored"],
        };

        OperationOutcome outcome = response.ToOutcome();

        Assert.False(outcome.Success);
        Assert.True(outcome.RolledBack);
        Assert.Equal("Apply failed; original restored.", outcome.Message);
        Assert.Single(outcome.Warnings);
    }

    [Fact]
    public void Restore_operation_serializes_as_camel_case_string()
    {
        ChangeSet changeSet = TestChangeSets.Create();
        ElevatedOperationRequest request = ElevatedOperationRequest.Create(
            "fake.setting", ElevatedOperationKind.Restore, changeSet);

        string json = JsonSerializer.Serialize(request, JsonDefaults.Options);

        Assert.Contains("\"operation\": \"restore\"", json, StringComparison.OrdinalIgnoreCase);
    }
}
