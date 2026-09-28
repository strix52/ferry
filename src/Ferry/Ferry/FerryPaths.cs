using System.IO;
using System.Security.Cryptography;

namespace Ferry;

internal static class FerryPaths
{
    private const string ProductFolder = "Ferry";

    public static string ResolveDataDirectory()
    {
        var explicitDirectory = Environment.GetEnvironmentVariable("FERRY_DATA_DIR");
        var currentDirectory = Directory.GetCurrentDirectory();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDirectory = AppContext.BaseDirectory;
        return ResolveDataDirectory(explicitDirectory, currentDirectory, localAppData, baseDirectory, PreferredDriveRoot(), App.Log);
    }

    public static string EnsureDownloadDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = ResolveDownloadDirectory(PreferredDriveRoot(), userProfile);
        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log($"download directory unavailable: {ex.Message}");
            var fallback = ResolveDownloadDirectory(null, userProfile);
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    internal static string ResolveDataDirectory(
        string? explicitDirectory,
        string currentDirectory,
        string localAppData,
        string baseDirectory,
        string? preferredDriveRoot,
        Action<string>? log = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            return Path.GetFullPath(explicitDirectory);
        }

        var currentData = Path.Combine(currentDirectory, "data");
        if (Directory.Exists(currentData)) return Path.GetFullPath(currentData);

        var portableData = Path.Combine(baseDirectory, "data");
        if (Directory.Exists(portableData)) return Path.GetFullPath(portableData);

        var legacyData = Path.Combine(localAppData, ProductFolder, "data");
        if (!string.IsNullOrWhiteSpace(preferredDriveRoot))
        {
            var preferredData = Path.Combine(Path.GetFullPath(preferredDriveRoot), ProductFolder, "data");
            if (Directory.Exists(preferredData)) return preferredData;

            if (Directory.Exists(legacyData))
            {
                if (TryMigrateDirectory(legacyData, preferredData, log)) return preferredData;
                log?.Invoke($"data migration failed; continuing with {legacyData}");
                return legacyData;
            }

            return preferredData;
        }

        return legacyData;
    }

    internal static string ResolveDownloadDirectory(string? preferredDriveRoot, string userProfile) =>
        !string.IsNullOrWhiteSpace(preferredDriveRoot)
            ? Path.Combine(Path.GetFullPath(preferredDriveRoot), ProductFolder, "Downloads")
            : Path.Combine(userProfile, "Downloads", ProductFolder);

    internal static bool TryMigrateDirectory(string source, string destination, Action<string>? log = null)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (!Directory.Exists(source)) return Directory.Exists(destination);
        if (Directory.Exists(destination)) return false;

        var parent = Directory.GetParent(destination)?.FullName
            ?? throw new InvalidOperationException($"No parent directory for {destination}");
        Directory.CreateDirectory(parent);
        var staging = destination + ".migrating-" + Guid.NewGuid().ToString("N");

        try
        {
            CopyDirectory(source, staging);
            var sourceFiles = Snapshot(source);
            var stagedFiles = Snapshot(staging);
            if (sourceFiles.Count != stagedFiles.Count || sourceFiles.Any(pair =>
                    !stagedFiles.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
            {
                throw new IOException("Copied Ferry data did not match the source files.");
            }

            Directory.Move(staging, destination);
            try
            {
                Directory.Delete(source, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The promoted copy is complete and is now authoritative. A
                // locked legacy directory is harmless and can be removed on a
                // later maintenance pass; do not fall back to it and split the
                // thread between two roots.
                log?.Invoke($"legacy data cleanup deferred: {ex.Message}");
            }
            log?.Invoke($"data migrated to {destination}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"data migration error: {ex.Message}");
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch { }
            return false;
        }
    }

    internal static bool TryResolveStoredFile(string filesDirectory, string? storedName, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(storedName) || Path.GetFileName(storedName) != storedName) return false;

        var filesRoot = Path.GetFullPath(filesDirectory);
        var candidate = Path.GetFullPath(Path.Combine(filesRoot, storedName));
        var containedPrefix = filesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(containedPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate)) return false;

        path = candidate;
        return true;
    }

    private static string? PreferredDriveRoot()
    {
        try
        {
            var drive = DriveInfo.GetDrives().FirstOrDefault(candidate =>
                string.Equals(candidate.Name, @"D:\", StringComparison.OrdinalIgnoreCase)
                && candidate.IsReady
                && candidate.DriveType == DriveType.Fixed);
            return drive?.RootDirectory.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static Dictionary<string, string> Snapshot(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => Path.GetRelativePath(directory, file),
                file => HashFile(file),
                StringComparer.OrdinalIgnoreCase);

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
