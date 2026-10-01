using System.IO;
using System.Text.Json;

namespace AndroidMultiGameManager;

public sealed class InstanceGroup
{
    public string Name { get; set; } = "기본 그룹";
    public List<string> AvdNames { get; set; } = new();
}

public static class InstanceGroupService
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AndroidMultiGameManager");
    private static readonly string FilePath = Path.Combine(Folder, "groups.json");

    public static List<InstanceGroup> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<List<InstanceGroup>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { return new(); }
    }

    public static void Save(IEnumerable<InstanceGroup> groups)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath,
            JsonSerializer.Serialize(groups, new JsonSerializerOptions { WriteIndented = true }));
    }
}