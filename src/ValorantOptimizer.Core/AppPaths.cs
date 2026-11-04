namespace ValorantOptimizer.Core;

/// <summary>
/// Filesystem locations owned by the application. Everything is per-user so the UI can run
/// without administrator rights. Tests construct instances with a temporary root.
/// </summary>
public sealed record AppPaths(string DataRoot)
{
    public static AppPaths Default { get; } = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ValorantOptimizer"));

    public string BackupsDirectory => Path.Combine(DataRoot, "backups");
    public string LogsDirectory => Path.Combine(DataRoot, "logs");
    public string ElevationDirectory => Path.Combine(DataRoot, "elevation");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ElevationDirectory);
    }
}
