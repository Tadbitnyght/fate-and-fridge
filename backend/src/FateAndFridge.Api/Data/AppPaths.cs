namespace FateAndFridge.Api.Data;

/// <summary>Persistent storage locations (docs/ARCHITECTURE.md section 4).</summary>
internal static class AppPaths
{
    private static readonly string[] Subdirectories = ["config", "images", "backups"];

    public static string DataRoot(IConfiguration configuration) =>
        configuration.GetValue("DataDir", "./data") ?? "./data";

    /// <summary>
    /// Creates the required data directories. Missing or unwritable paths are
    /// logged but do not stop the application (development hosts may not have
    /// a mounted /data).
    /// </summary>
    public static void EnsureDataDirectories(IConfiguration configuration, ILogger logger)
    {
        var root = DataRoot(configuration);
        try
        {
            foreach (var subdirectory in Subdirectories)
            {
                Directory.CreateDirectory(Path.Combine(root, subdirectory));
            }

            logger.LogInformation("Persistent data directory ready at {DataRoot}", root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not prepare data directories under {DataRoot}", root);
        }
    }
}
