using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.App.Services;

/// <summary>
/// Launches the separate elevated helper for one privileged operation. The main UI stays
/// unelevated; Windows shows a UAC prompt only when a change actually needs administrator rights.
/// </summary>
public sealed class ElevatedOperationClient : IElevatedOperationClient
{
    private const string HelperFileName = "ValorantOptimizer.Elevated.exe";
    private readonly AppPaths _paths;

    public ElevatedOperationClient(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<ElevatedOperationResponse> ExecuteAsync(
        ElevatedOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string helperPath = ResolveHelperPath();
        Directory.CreateDirectory(_paths.ElevationDirectory);

        string requestPath = Path.Combine(_paths.ElevationDirectory, request.RequestId + ".request.json");
        string responsePath = Path.Combine(_paths.ElevationDirectory, request.RequestId + ".response.json");

        try
        {
            string requestJson = JsonSerializer.Serialize(request, JsonDefaults.Options);
            await File.WriteAllTextAsync(requestPath, requestJson, cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo(helperPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"--request \"{requestPath}\" --response \"{responsePath}\"",
            };

            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new ElevationCancelledException("Administrator approval was cancelled.");
            }
            catch (Win32Exception ex)
            {
                throw new ElevationClientException(
                    "Windows could not start the elevated helper: " + ex.Message, ex);
            }

            if (process is null)
            {
                throw new ElevationClientException("Windows did not start the elevated helper.");
            }

            using (process)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ElevationClientException("The elevated helper did not finish within three minutes.");
                }
            }

            if (!File.Exists(responsePath))
            {
                throw new ElevationClientException(
                    "The elevated helper exited without returning a result. Windows may have blocked it.");
            }

            string responseJson = await File.ReadAllTextAsync(responsePath, cancellationToken).ConfigureAwait(false);
            ElevatedOperationResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<ElevatedOperationResponse>(responseJson, JsonDefaults.Options);
            }
            catch (JsonException ex)
            {
                throw new ElevationClientException("The elevated helper returned an unreadable result.", ex);
            }

            if (response is null)
            {
                throw new ElevationClientException("The elevated helper returned an empty result.");
            }

            if (response.SchemaVersion != ElevatedOperationResponse.CurrentSchemaVersion)
            {
                throw new ElevationClientException(
                    $"The elevated helper uses schema version {response.SchemaVersion}, which this build does not understand.");
            }

            if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
            {
                throw new ElevationClientException("The elevated helper returned a result for a different request.");
            }

            return response;
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(responsePath);
        }
    }

    private static string ResolveHelperPath()
    {
        string baseDirectory = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDirectory, HelperFileName),
            Path.Combine(baseDirectory, "helper", HelperFileName),
        };

        DirectoryInfo? directory = new DirectoryInfo(baseDirectory);
        for (int depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            foreach (string configuration in new[] { "Debug", "Release" })
            {
                candidates.Add(Path.Combine(
                    directory.FullName, "src", "ValorantOptimizer.Elevated", "bin",
                    configuration, "net10.0-windows", HelperFileName));
            }
        }

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new ElevationClientException(
            "The elevated helper executable was not found. Build the solution (or the packaged release) and try again.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Elevation temp files are best effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // The elevated process may still hold an ACL on the file.
        }
    }
}
