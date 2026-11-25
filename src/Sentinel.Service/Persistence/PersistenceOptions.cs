namespace Sentinel.Service.Persistence;

/// <summary>Configuration section <c>Sentinel:Persistence</c>.</summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "Sentinel:Persistence";
    public const string DatabaseFileName = "sentinel.db";

    /// <summary>Overrides the default data directory.</summary>
    public string? DataDirectory { get; set; }

    public int RetentionDays { get; set; } = HistoryWriter.DefaultRetentionDays;

    /// <summary>
    /// Service: <c>%ProgramData%\PCSentinel</c> (the installer restricts its ACL to SYSTEM and Administrators, M9).
    /// Console/development: <c>%LOCALAPPDATA%\PCSentinel-dev</c>, so development never touches the production location.
    /// </summary>
    public string ResolveDatabasePath(bool runningAsService)
    {
        var directory = DataDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = runningAsService
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCSentinel")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCSentinel-dev");
        }

        return Path.Combine(directory, DatabaseFileName);
    }
}
