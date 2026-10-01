using System.IO;
using System.Text.Json;

namespace AndroidMultiGameManager;

public static class InstanceAccountService
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AndroidMultiGameManager");
    private static readonly string FilePath = Path.Combine(Folder, "accounts.json");

    public static Dictionary<string,string> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new(StringComparer.OrdinalIgnoreCase);

            var data = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(FilePath))
                       ?? new Dictionary<string,string>();

            return new Dictionary<string,string>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Save(IDictionary<string,string> aliases)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(
            FilePath,
            JsonSerializer.Serialize(aliases, new JsonSerializerOptions { WriteIndented = true }));
    }
}