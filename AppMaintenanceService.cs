using Microsoft.Win32;
using System.IO;
using System.Text.Json;

namespace AndroidMultiGameManager;

public static class AppMaintenanceService
{
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AndroidMultiGameManager");
    private static readonly string LogFolder = Path.Combine(DataFolder, "logs");
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AndroidMultiGameManager";

    public static string LogFilePath
    {
        get
        {
            Directory.CreateDirectory(LogFolder);
            return Path.Combine(LogFolder, $"app-{DateTime.Now:yyyyMMdd}.log");
        }
    }

    public static void AppendLog(string message)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            File.AppendAllText(
                LogFilePath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public static string CreateBackup()
    {
        Directory.CreateDirectory(DataFolder);
        var backupDir = Path.Combine(DataFolder, "backups");
        Directory.CreateDirectory(backupDir);
        var backupFile = Path.Combine(backupDir, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");

        string ReadOrEmpty(string name)
        {
            var path = Path.Combine(DataFolder, name);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        var payload = new BackupPayload
        {
            SettingsJson = ReadOrEmpty("settings.json"),
            ProfilesJson = ReadOrEmpty("profiles.json")
        };

        File.WriteAllText(
            backupFile,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

        return backupFile;
    }

    public static void RestoreBackup(string backupFile)
    {
        if (!File.Exists(backupFile))
            throw new FileNotFoundException("백업 파일을 찾을 수 없습니다.", backupFile);

        var payload = JsonSerializer.Deserialize<BackupPayload>(File.ReadAllText(backupFile))
                      ?? throw new InvalidOperationException("백업 파일 형식이 올바르지 않습니다.");

        Directory.CreateDirectory(DataFolder);

        if (!string.IsNullOrWhiteSpace(payload.SettingsJson))
            File.WriteAllText(Path.Combine(DataFolder, "settings.json"), payload.SettingsJson);

        if (!string.IsNullOrWhiteSpace(payload.ProfilesJson))
            File.WriteAllText(Path.Combine(DataFolder, "profiles.json"), payload.ProfilesJson);
    }

    public static void SetRunAtStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true);

        if (enabled)
        {
            var exe = Environment.ProcessPath
                      ?? throw new InvalidOperationException("현재 실행 파일 경로를 확인할 수 없습니다.");
            key.SetValue(RunValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(RunValueName, false);
        }
    }

    public static bool IsRunAtStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    private sealed class BackupPayload
    {
        public string SettingsJson { get; set; } = "";
        public string ProfilesJson { get; set; } = "";
    }
}