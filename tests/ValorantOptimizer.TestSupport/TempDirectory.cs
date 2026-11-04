namespace ValorantOptimizer.TestSupport;

/// <summary>Unique temp directory that is removed on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "test")
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ValorantOptimizerTests",
            prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) =>
        System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Temp cleanup is best effort.
        }
    }
}
