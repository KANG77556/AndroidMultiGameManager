using System.IO;
using System.Text.Json;

namespace AndroidMultiGameManager;

public sealed class GameProfile
{
    public string Name { get; set; } = "기본";
    public string PackageName { get; set; } = "";
    public int CpuCores { get; set; } = 4;
    public int MemoryMb { get; set; } = 4096;
    public int MaxFps { get; set; } = 60;
    public int MaxSize { get; set; } = 1080;
    public int AutoLaunchDelayMs { get; set; } = 1500;
}

public static class GameProfileService
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AndroidMultiGameManager");
    private static readonly string FilePath = Path.Combine(Folder, "profiles.json");

    public static List<GameProfile> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<List<GameProfile>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { return new(); }
    }

    public static void Save(IEnumerable<GameProfile> profiles)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath,
            JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));
    }
}