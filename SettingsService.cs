using System.IO;
using System.Text.Json;
namespace AndroidMultiGameManager;
public sealed class AppSettings
{
    public string PackageName { get; set; } = "";
    public string LayoutMode { get; set; } = "자동";
    public List<string> SelectedAvds { get; set; } = new();
    public int CpuCores { get; set; } = 4;
    public int MemoryMb { get; set; } = 4096;
    public bool SyncClick { get; set; }
    public bool RunAtStartup { get; set; }
    public int StartRetryCount { get; set; } = 2;
}
public static class SettingsService
{
    private static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"AndroidMultiGameManager");
    private static readonly string FilePath=Path.Combine(Folder,"settings.json");
    public static AppSettings Load(){ try { return File.Exists(FilePath)?JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))??new():new(); } catch { return new(); } }
    public static void Save(AppSettings s){ Directory.CreateDirectory(Folder); File.WriteAllText(FilePath,JsonSerializer.Serialize(s,new JsonSerializerOptions{WriteIndented=true})); }
}