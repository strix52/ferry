using System.IO;
using System.Text.Json;

namespace Ferry;

// Per-device identity, mirroring the web client's persisted device
// (localStorage ferry_device): a stable id plus a display name.
public sealed record DeviceIdentity(string Id, string Name)
{
    public static DeviceIdentity LoadOrCreate()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ferry");
        var path = Path.Combine(dir, "device.json");
        try
        {
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<DeviceIdentity>(File.ReadAllText(path));
                if (saved is { Id.Length: > 0, Name.Length: > 0 }) return saved;
            }
        }
        catch (Exception) { /* corrupt or unreadable: fall through to create */ }
        var fresh = new DeviceIdentity(Guid.NewGuid().ToString("N"), "Laptop");
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ferry");
        var path = Path.Combine(dir, "device.json");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (IOException) { }
    }
}
