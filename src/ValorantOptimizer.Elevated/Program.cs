using System.Text.Json;
using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;
using ValorantOptimizer.Optimizations;

namespace ValorantOptimizer.Elevated;

/// <summary>
/// One-shot elevated helper. It runs only a known optimization id against a scope-validated
/// change set supplied by the UI, then writes a JSON result. It performs its own rollback when an
/// apply fails so the user is not prompted for a second UAC approval.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? requestPath = null;
        string? responsePath = null;

        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--request", StringComparison.OrdinalIgnoreCase))
            {
                requestPath = args[index + 1];
            }
            else if (string.Equals(args[index], "--response", StringComparison.OrdinalIgnoreCase))
            {
                responsePath = args[index + 1];
            }
        }

        if (string.IsNullOrWhiteSpace(requestPath) || string.IsNullOrWhiteSpace(responsePath))
        {
            Log("Missing --request or --response argument.");
            return 2;
        }

        ElevatedOperationRequest? request = null;
        try
        {
            string requestJson = await File.ReadAllTextAsync(requestPath).ConfigureAwait(false);
            request = JsonSerializer.Deserialize<ElevatedOperationRequest>(requestJson, JsonDefaults.Options)
                ?? throw new InvalidOperationException("The elevation request was empty.");

            if (request.SchemaVersion != ElevatedOperationRequest.CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Unsupported elevation request schema {request.SchemaVersion}.");
            }

            AppPaths paths = AppPaths.Default;
            paths.EnsureCreated();

            var registry = new RegistryStore();
            var processes = new ProcessRunner();
            var locator = new ValorantLocator(registry, processes);
            IReadOnlyList<IOptimization> optimizations = OptimizationCatalog.CreateDefault(locator);

            IOptimization optimization = optimizations.FirstOrDefault(candidate =>
                string.Equals(candidate.Descriptor.Id, request.OptimizationId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Unknown optimization id '{request.OptimizationId}'.");

            if (!RestoreScopeValidator.IsValid(optimization.Descriptor, request.ChangeSet, out string scopeError))
            {
                throw new InvalidOperationException("Refusing out-of-scope elevation data: " + scopeError);
            }

            var context = new OptimizationContext(registry, processes, isElevated: true);
            ElevatedOperationResponse response = await ExecuteAsync(optimization, request, context).ConfigureAwait(false);

            WriteResponse(responsePath, response);
            Log($"{request.Operation} {request.OptimizationId}: success={response.Success} verified={response.Verified} {response.Message}");
            return response.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log("Unhandled: " + ex);
            if (request is not null)
            {
                try
                {
                    WriteResponse(responsePath, new ElevatedOperationResponse
                    {
                        RequestId = request.RequestId,
                        Success = false,
                        Error = ex.ToString(),
                        Message = "The elevated helper failed: " + ex.Message,
                    });
                }
                catch (Exception writeError)
                {
                    Log("Could not write failure response: " + writeError);
                }
            }

            return 1;
        }
    }

    private static async Task<ElevatedOperationResponse> ExecuteAsync(
        IOptimization optimization,
        ElevatedOperationRequest request,
        IOptimizationContext context)
    {
        OperationOutcome outcome;
        if (request.Operation == ElevatedOperationKind.Apply)
        {
            try
            {
                outcome = await optimization.ApplyAsync(context, request.ChangeSet).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                outcome = OperationOutcome.Failed("Apply failed: " + ex.Message);
            }

            if (!outcome.Success)
            {
                try
                {
                    OperationOutcome rollback = await optimization.RestoreAsync(context, request.ChangeSet)
                        .ConfigureAwait(false);
                    bool matched = request.ChangeSet.Entries.Count == 0 ||
                                   ChangeSetInspector.Matches(context.Registry, request.ChangeSet, out _);
                    outcome = rollback.Success && matched
                        ? outcome with
                        {
                            RolledBack = true,
                            Warnings = [.. outcome.Warnings, "The helper restored the original values."],
                        }
                        : outcome with
                        {
                            Warnings = [.. outcome.Warnings, "The helper could not verify its rollback."],
                        };
                }
                catch (Exception rollbackError)
                {
                    outcome = outcome with
                    {
                        Warnings =
                        [
                            .. outcome.Warnings,
                            "The helper rollback failed: " + rollbackError.Message,
                        ],
                    };
                }
            }
            else
            {
                OptimizationAssessment assessment = await optimization.AssessAsync(context).ConfigureAwait(false);
                outcome = outcome with
                {
                    Verified = outcome.Verified || assessment.State == OptimizationState.Applied,
                };
            }
        }
        else
        {
            try
            {
                outcome = await optimization.RestoreAsync(context, request.ChangeSet).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                outcome = OperationOutcome.Failed("Undo failed: " + ex.Message);
            }

            if (outcome.Success)
            {
                bool verified = request.ChangeSet.Entries.Count > 0
                    ? ChangeSetInspector.Matches(context.Registry, request.ChangeSet, out _)
                    : outcome.Verified;
                outcome = outcome with { Verified = verified };
            }
        }

        return new ElevatedOperationResponse
        {
            RequestId = request.RequestId,
            Success = outcome.Success,
            Verified = outcome.Verified,
            RolledBack = outcome.RolledBack,
            Message = outcome.Message,
            Changes = [.. outcome.Changes],
            Warnings = [.. outcome.Warnings],
        };
    }

    private static void WriteResponse(string responsePath, ElevatedOperationResponse response)
    {
        string? directory = Path.GetDirectoryName(responsePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = responsePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(response, JsonDefaults.Options));
        File.Move(tempPath, responsePath, overwrite: true);
    }

    private static void Log(string message)
    {
        try
        {
            AppPaths paths = AppPaths.Default;
            Directory.CreateDirectory(paths.LogsDirectory);
            File.AppendAllText(
                Path.Combine(paths.LogsDirectory, "elevated.log"),
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must not change the outcome of the privileged operation.
        }
    }
}
