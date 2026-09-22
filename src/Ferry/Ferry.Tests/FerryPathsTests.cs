using System;
using System.IO;
using Ferry;
using Xunit;

namespace Ferry.Tests;

public sealed class FerryPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ferry-path-tests-" + Guid.NewGuid().ToString("N"));

    public FerryPathsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ResolveDataDirectory_ExplicitOverrideWins()
    {
        var explicitPath = Path.Combine(_root, "explicit");
        var result = FerryPaths.ResolveDataDirectory(explicitPath, _root, Path.Combine(_root, "local"), Path.Combine(_root, "app"), Path.Combine(_root, "preferred"));
        Assert.Equal(Path.GetFullPath(explicitPath), result);
    }

    [Fact]
    public void ResolveDataDirectory_PreservesExistingPortableData()
    {
        var app = Path.Combine(_root, "app");
        var portable = Path.Combine(app, "data");
        Directory.CreateDirectory(portable);

        var result = FerryPaths.ResolveDataDirectory(null, Path.Combine(_root, "cwd"), Path.Combine(_root, "local"), app, Path.Combine(_root, "preferred"));
        Assert.Equal(Path.GetFullPath(portable), result);
    }

    [Fact]
    public void ResolveDataDirectory_MigratesLegacyDataToPreferredDrive()
    {
        var local = Path.Combine(_root, "local");
        var legacy = Path.Combine(local, "Ferry", "data");
        Directory.CreateDirectory(Path.Combine(legacy, "files", "kept"));
        File.WriteAllText(Path.Combine(legacy, "flow.db"), "database");
        File.WriteAllText(Path.Combine(legacy, "auth.json"), "token");
        File.WriteAllText(Path.Combine(legacy, "files", "kept", "sample.txt"), "payload");

        var preferredRoot = Path.Combine(_root, "preferred");
        var result = FerryPaths.ResolveDataDirectory(null, Path.Combine(_root, "cwd"), local, Path.Combine(_root, "app"), preferredRoot);

        Assert.Equal(Path.Combine(preferredRoot, "Ferry", "data"), result);
        Assert.False(Directory.Exists(legacy));
        Assert.Equal("database", File.ReadAllText(Path.Combine(result, "flow.db")));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(result, "files", "kept", "sample.txt")));
    }

    [Fact]
    public void ResolveDownloadDirectory_UsesPreferredDriveOrUserDownloadsFallback()
    {
        var preferred = FerryPaths.ResolveDownloadDirectory(Path.Combine(_root, "D"), Path.Combine(_root, "profile"));
        var fallback = FerryPaths.ResolveDownloadDirectory(null, Path.Combine(_root, "profile"));

        Assert.Equal(Path.Combine(_root, "D", "Ferry", "Downloads"), preferred);
        Assert.Equal(Path.Combine(_root, "profile", "Downloads", "Ferry"), fallback);
    }

    [Fact]
    public void TryResolveStoredFile_RejectsTraversalAndReturnsExistingFullPath()
    {
        var files = Path.Combine(_root, "files");
        Directory.CreateDirectory(files);
        var stored = "1234_sample.txt";
        File.WriteAllText(Path.Combine(files, stored), "payload");

        Assert.True(FerryPaths.TryResolveStoredFile(files, stored, out var path));
        Assert.Equal(Path.GetFullPath(Path.Combine(files, stored)), path);
        Assert.False(FerryPaths.TryResolveStoredFile(files, "..\\outside.txt", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
