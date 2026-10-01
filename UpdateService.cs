using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AndroidMultiGameManager;

public sealed record UpdateResult(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string Message,
    string? InstallerPath);

public static class UpdateService
{
    public const string CurrentVersion = "1.7.0";

    public static async Task<UpdateResult> CheckAndDownloadAsync(string manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
            return new(false, CurrentVersion, CurrentVersion,
                "update-config.json에 manifestUrl을 설정하세요.", null);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var manifestJson = await client.GetStringAsync(manifestUrl);
        using var doc = JsonDocument.Parse(manifestJson);

        var latest = doc.RootElement.TryGetProperty("version", out var versionElement)
            ? versionElement.GetString() ?? CurrentVersion
            : CurrentVersion;

        var downloadUrl = doc.RootElement.TryGetProperty("installerUrl", out var urlElement)
            ? urlElement.GetString()
            : null;

        var expectedSha256 = doc.RootElement.TryGetProperty("sha256", out var shaElement)
            ? shaElement.GetString()
            : null;

        if (!Version.TryParse(latest, out var latestV) ||
            !Version.TryParse(CurrentVersion, out var currentV) ||
            latestV <= currentV)
        {
            return new(false, CurrentVersion, latest, "현재 최신 버전입니다.", null);
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
            return new(true, CurrentVersion, latest,
                "새 버전이 있지만 installerUrl이 없습니다.", null);

        if (string.IsNullOrWhiteSpace(expectedSha256))
            return new(true, CurrentVersion, latest,
                "새 버전이 있지만 sha256 값이 없어 안전하게 설치할 수 없습니다.", null);

        var target = Path.Combine(
            Path.GetTempPath(),
            $"AndroidMultiGameManager-{latest}-Setup.exe");

        var bytes = await client.GetByteArrayAsync(downloadUrl);
        await File.WriteAllBytesAsync(target, bytes);

        var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes));
        if (!actualSha256.Equals(
                expectedSha256.Replace(" ", "", StringComparison.Ordinal).Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(target); } catch { }
            throw new InvalidOperationException(
                $"업데이트 설치 파일 SHA-256 검증 실패.\n예상: {expectedSha256}\n실제: {actualSha256}");
        }

        return new(true, CurrentVersion, latest,
            $"v{latest} 설치 파일 다운로드 및 SHA-256 검증 완료", target);
    }

    public static string LoadManifestUrl()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "update-config.json");
            if (!File.Exists(file)) return "";

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.TryGetProperty("manifestUrl", out var url)
                ? url.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    public static void LaunchInstaller(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("업데이트 설치 파일을 찾을 수 없습니다.", path);

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}