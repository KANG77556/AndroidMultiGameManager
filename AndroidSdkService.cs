using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AndroidMultiGameManager;

public sealed class AndroidSdkService
{
    public string? SdkRoot { get; }
    public string? AdbPath { get; }
    public string? EmulatorPath { get; }

    public AndroidSdkService()
    {
        SdkRoot = ResolveSdkRoot();
        if (!string.IsNullOrWhiteSpace(SdkRoot))
        {
            AdbPath = Path.Combine(SdkRoot, "platform-tools", "adb.exe");
            EmulatorPath = Path.Combine(SdkRoot, "emulator", "emulator.exe");
        }
    }

    private static string? ResolveSdkRoot()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk")
        };
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p));
    }

    public bool IsReady(out string message)
    {
        if (string.IsNullOrWhiteSpace(SdkRoot))
        {
            message = "Android SDK를 찾지 못했습니다.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(AdbPath) || !File.Exists(AdbPath))
        {
            message = $"adb.exe를 찾지 못했습니다: {AdbPath}";
            return false;
        }
        if (string.IsNullOrWhiteSpace(EmulatorPath) || !File.Exists(EmulatorPath))
        {
            message = $"emulator.exe를 찾지 못했습니다: {EmulatorPath}";
            return false;
        }
        message = "Android SDK 준비 완료";
        return true;
    }

    public static bool IsValidPackageName(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return false;
        var value = packageName.Trim();
        if (value.Equals("com.example.game", StringComparison.OrdinalIgnoreCase)) return false;
        return Regex.IsMatch(value, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$");
    }

    public async Task<IReadOnlyList<string>> GetAvdsAsync()
    {
        EnsureReady();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var result = await RunAsync(EmulatorPath!, "-list-avds", 15000, false);
            foreach (var name in result.Out.Split(
                         new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
        }
        catch { }

        foreach (var root in GetAvdRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var ini in Directory.EnumerateFiles(root, "*.ini", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(ini);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
        }

        try
        {
            var running = await GetRunningAvdsAsync();
            foreach (var name in running.Keys) names.Add(name);
        }
        catch { }

        return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> GetAvdRoots()
    {
        var roots = new List<string>();
        var custom = Environment.GetEnvironmentVariable("ANDROID_AVD_HOME");
        if (!string.IsNullOrWhiteSpace(custom)) roots.Add(custom);

        var androidUserHome = Environment.GetEnvironmentVariable("ANDROID_USER_HOME");
        if (!string.IsNullOrWhiteSpace(androidUserHome))
            roots.Add(Path.Combine(androidUserHome, "avd"));

        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".android",
            "avd"));

        return roots.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<Dictionary<string, string>> GetRunningAvdsAsync()
    {
        EnsureReady();
        var devices = await RunAsync(AdbPath!, "devices", 10000);
        var serials = devices.Out.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase) &&
                        x.EndsWith("\tdevice", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Split('\t')[0])
            .ToList();

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var serial in serials)
        {
            var name = await TryGetAvdNameAsync(serial);
            if (!string.IsNullOrWhiteSpace(name)) map[name] = serial;
        }
        return map;
    }

    private async Task<string?> TryGetAvdNameAsync(string serial)
    {
        try
        {
            var result = await RunDeviceAsync(serial, "emu avd name", 6000, false);
            return result.Out.Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(x => !x.Equals("OK", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    public Process StartAvd(string name, int port, int cores = 4, int memory = 4096)
    {
        EnsureReady();
        cores = Math.Clamp(cores, 1, 16);
        memory = Math.Clamp(memory, 1024, 16384);
        var psi = new ProcessStartInfo
        {
            FileName = EmulatorPath!,
            Arguments = $"-avd {Q(name)} -port {port} -cores {cores} -memory {memory} -no-snapshot-save",
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(EmulatorPath!)!
        };
        return Process.Start(psi) ?? throw new InvalidOperationException($"AVD 실행 실패: {name}");
    }

    public async Task WaitForDeviceAsync(string serial, TimeSpan timeout)
    {
        EnsureReady();
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            try
            {
                var state = await RunDeviceAsync(serial, "get-state", 5000, false, false);
                if (state.Code == 0 &&
                    state.Out.Trim().Equals("device", StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch { }
            await Task.Delay(1200);
        }
        throw new TimeoutException($"에뮬레이터 연결 제한시간 초과: {serial}");
    }

    public async Task LaunchPackageAsync(string serial, string packageName)
    {
        EnsureReady();
        if (!IsValidPackageName(packageName))
            throw new InvalidOperationException("실제 게임 패키지명을 선택하거나 Play 스토어에서 게임을 먼저 설치하세요.");

        await EnsureDeviceReadyAsync(serial);
        var exists = await RunDeviceAsync(
            serial,
            $"shell pm path {Q(packageName.Trim())}",
            12000,
            false);

        if (exists.Code != 0 || !exists.Out.Contains("package:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{serial}에 '{packageName}' 패키지가 설치되어 있지 않습니다.");

        var result = await RunDeviceAsync(
            serial,
            $"shell monkey -p {Q(packageName.Trim())} -c android.intent.category.LAUNCHER 1",
            18000,
            false);

        if (result.Code != 0 ||
            result.Out.Contains("No activities found", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"게임 실행 실패: {packageName}");
    }

    public async Task<IReadOnlyList<string>> GetInstalledUserPackagesAsync(string serial)
    {
        EnsureReady();
        await EnsureDeviceReadyAsync(serial);
        var result = await RunDeviceAsync(serial, "shell pm list packages -3", 20000, false);
        return result.Out.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
            .Select(x => x[8..])
            .OrderBy(x => x)
            .ToList();
    }

    public async Task<bool> HasPlayStoreAsync(string serial)
    {
        EnsureReady();
        if (!await EnsureDeviceReadyAsync(serial, false)) return false;
        var result = await RunDeviceAsync(
            serial,
            "shell pm path com.android.vending",
            12000,
            false);
        return result.Code == 0 &&
               result.Out.Contains("package:", StringComparison.OrdinalIgnoreCase);
    }

    public async Task OpenPlayStoreSearchAsync(string serial, string query)
    {
        EnsureReady();
        await EnsureDeviceReadyAsync(serial);

        if (!await HasPlayStoreAsync(serial))
            throw new InvalidOperationException(
                $"{serial}에는 Google Play 스토어가 없습니다. Google Play 지원 AVD가 필요합니다.");

        var encoded = Uri.EscapeDataString(string.IsNullOrWhiteSpace(query) ? "game" : query.Trim());
        var result = await RunDeviceAsync(
            serial,
            $"shell am start -a android.intent.action.VIEW -d {Q($"market://search?q={encoded}")} com.android.vending",
            18000,
            false);

        if (result.Code != 0)
            throw new InvalidOperationException(
                $"Play 스토어 검색 열기 실패: {serial}\n{result.Err}\n{result.Out}");
    }

    public async Task OpenPlayStoreDetailsAsync(string serial, string packageName)
    {
        EnsureReady();
        if (!IsValidPackageName(packageName))
            throw new InvalidOperationException("실제 게임 패키지명이 필요합니다.");

        await EnsureDeviceReadyAsync(serial);
        if (!await HasPlayStoreAsync(serial))
            throw new InvalidOperationException($"{serial}에는 Google Play 스토어가 없습니다.");

        var result = await RunDeviceAsync(
            serial,
            $"shell am start -a android.intent.action.VIEW -d {Q($"market://details?id={packageName.Trim()}")} com.android.vending",
            18000,
            false);

        if (result.Code != 0)
            throw new InvalidOperationException(
                $"Play 스토어 앱 페이지 열기 실패: {serial}\n{result.Err}\n{result.Out}");
    }

    public async Task OpenGoogleAccountSettingsAsync(string serial)
    {
        EnsureReady();
        await EnsureDeviceReadyAsync(serial);

        var attempts = new[]
        {
            "shell am start -a android.settings.ADD_ACCOUNT_SETTINGS",
            "shell am start -a android.settings.SYNC_SETTINGS",
            "shell am start -a android.settings.SETTINGS"
        };

        var errors = new List<string>();
        foreach (var args in attempts)
        {
            try
            {
                var result = await RunDeviceAsync(serial, args, 12000, false);
                if (result.Code == 0 &&
                    !result.Out.Contains("Error:", StringComparison.OrdinalIgnoreCase) &&
                    !result.Err.Contains("Error:", StringComparison.OrdinalIgnoreCase))
                    return;

                errors.Add($"{args}: {result.Err} {result.Out}".Trim());
            }
            catch (Exception ex)
            {
                errors.Add($"{args}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"Google 계정 설정 화면을 열지 못했습니다: {serial}\n{string.Join(Environment.NewLine, errors)}");
    }

    public async Task<bool> HasGoogleAccountAsync(string serial)
    {
        EnsureReady();
        if (!await EnsureDeviceReadyAsync(serial, false)) return false;

        var result = await RunDeviceAsync(
            serial,
            "shell dumpsys account",
            12000,
            false);

        if (result.Code != 0) return false;

        return Regex.IsMatch(
            result.Out,
            @"Account\s*\{[^}]*type=com\.google",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public async Task<bool> IsPackageInstalledAsync(string serial, string packageName)
    {
        EnsureReady();
        if (!IsValidPackageName(packageName)) return false;
        if (!await EnsureDeviceReadyAsync(serial, false)) return false;

        var result = await RunDeviceAsync(
            serial,
            $"shell pm path {Q(packageName.Trim())}",
            12000,
            false);

        return result.Code == 0 &&
               result.Out.Contains("package:", StringComparison.OrdinalIgnoreCase);
    }

    public async Task InstallApkAsync(string serial, string apkPath)
    {
        EnsureReady();
        if (!File.Exists(apkPath)) throw new FileNotFoundException(apkPath);
        await EnsureDeviceReadyAsync(serial);

        var result = await RunDeviceAsync(
            serial,
            $"install -r {Q(apkPath)}",
            180000,
            false);

        if (result.Code != 0 ||
            !result.Out.Contains("Success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"APK 설치 실패: {serial}\n{result.Out}\n{result.Err}");
    }

    public async Task<bool> IsDeviceHealthyAsync(string serial)
    {
        EnsureReady();
        return await EnsureDeviceReadyAsync(serial, false);
    }

    public async Task<bool> EnsureDeviceReadyAsync(string serial, bool throwOnFailure = true)
    {
        EnsureReady();

        try
        {
            var state = await RunAsync(
                AdbPath!,
                $"-s {Q(serial)} get-state",
                5000,
                false);

            if (state.Code == 0 &&
                state.Out.Trim().Equals("device", StringComparison.OrdinalIgnoreCase))
            {
                var boot = await RunDeviceAsync(
                    serial,
                    "shell getprop sys.boot_completed",
                    7000,
                    false);

                if (boot.Code == 0 && boot.Out.Trim() == "1") return true;
            }
        }
        catch { }

        await TryReconnectDeviceAsync(serial);

        try
        {
            var state = await RunAsync(
                AdbPath!,
                $"-s {Q(serial)} get-state",
                6000,
                false);

            if (state.Code == 0 &&
                state.Out.Trim().Equals("device", StringComparison.OrdinalIgnoreCase))
            {
                var boot = await RunAsync(
                    AdbPath!,
                    $"-s {Q(serial)} shell getprop sys.boot_completed",
                    7000,
                    false);

                if (boot.Code == 0 && boot.Out.Trim() == "1") return true;
            }
        }
        catch { }

        if (throwOnFailure)
            throw new InvalidOperationException($"ADB 연결이 응답하지 않습니다: {serial}");

        return false;
    }

    public async Task SendTapAsync(string serial, int x, int y)
    {
        EnsureReady();
        await RunDeviceAsync(
            serial,
            $"shell input tap {Math.Max(0, x)} {Math.Max(0, y)}",
            12000,
            false);
    }

    public async Task SendSwipeAsync(
        string serial, int x1, int y1, int x2, int y2, int durationMs = 350)
    {
        EnsureReady();
        durationMs = Math.Clamp(durationMs, 50, 5000);
        await RunDeviceAsync(
            serial,
            $"shell input swipe {Math.Max(0, x1)} {Math.Max(0, y1)} {Math.Max(0, x2)} {Math.Max(0, y2)} {durationMs}",
            12000,
            false);
    }

    public async Task SendTextAsync(string serial, string text)
    {
        EnsureReady();
        await RunDeviceAsync(
            serial,
            $"shell input text {Q(text.Replace(" ", "%s"))}",
            12000,
            false);
    }

    public async Task SendKeyEventAsync(string serial, int key)
    {
        EnsureReady();
        await RunDeviceAsync(
            serial,
            $"shell input keyevent {Math.Max(0, key)}",
            12000,
            false);
    }

    public async Task<(int Width, int Height)> GetDisplaySizeAsync(string serial)
    {
        EnsureReady();
        var result = await RunDeviceAsync(serial, "shell wm size", 12000, false);
        var match = Regex.Matches(result.Out, @"(\d+)x(\d+)")
            .Cast<Match>()
            .LastOrDefault();

        return match is null
            ? (1080, 1920)
            : (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
    }

    public string CloneAvd(string source)
    {
        EnsureReady();
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".android",
            "avd");

        var sourceIni = Path.Combine(root, source + ".ini");
        var sourceDir = Path.Combine(root, source + ".avd");
        if (!File.Exists(sourceIni) || !Directory.Exists(sourceDir))
            throw new InvalidOperationException($"AVD 원본을 찾을 수 없습니다: {source}");

        var n = 1;
        string name;
        string ini;
        string dir;
        do
        {
            name = $"{source}_copy{n++}";
            ini = Path.Combine(root, name + ".ini");
            dir = Path.Combine(root, name + ".avd");
        } while (File.Exists(ini) || Directory.Exists(dir));

        CopyDir(sourceDir, dir);
        var text = File.ReadAllText(sourceIni);
        text = SetIni(text, "path", dir);
        text = SetIni(text, "path.rel", $"avd\\{name}.avd");
        File.WriteAllText(ini, text);

        var configPath = Path.Combine(dir, "config.ini");
        if (File.Exists(configPath))
        {
            var config = File.ReadAllText(configPath);
            config = SetIni(config, "AvdId", name);
            config = SetIni(config, "avd.ini.displayname", name);
            File.WriteAllText(configPath, config);
        }

        return name;
    }

    public async Task StopEmulatorAsync(string serial)
    {
        EnsureReady();
        await RunDeviceAsync(serial, "emu kill", 12000, false);
    }

    private async Task<R> RunDeviceAsync(
        string serial,
        string args,
        int timeoutMs,
        bool fail = true,
        bool retryOnTimeout = true)
    {
        try
        {
            return await RunAsync(
                AdbPath!,
                $"-s {Q(serial)} {args}",
                timeoutMs,
                fail);
        }
        catch (TimeoutException) when (retryOnTimeout)
        {
            await TryReconnectDeviceAsync(serial);
            return await RunAsync(
                AdbPath!,
                $"-s {Q(serial)} {args}",
                timeoutMs,
                fail);
        }
    }

    private async Task TryReconnectDeviceAsync(string serial)
    {
        try
        {
            var state = await RunAsync(
                AdbPath!,
                $"-s {Q(serial)} get-state",
                3500,
                false);

            if (state.Code == 0 &&
                state.Out.Trim().Equals("device", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(400);
                return;
            }
        }
        catch { }

        try
        {
            await RunAsync(AdbPath!, "reconnect device", 8000, false);
        }
        catch { }

        await Task.Delay(1200);
    }

    private static void CopyDir(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDir(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private static string SetIni(string content, string key, string value)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        var prefix = key + "=";
        var index = lines.FindIndex(x =>
            x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (index >= 0) lines[index] = prefix + value;
        else lines.Add(prefix + value);

        return string.Join(Environment.NewLine, lines);
    }

    private void EnsureReady()
    {
        if (!IsReady(out var message))
            throw new InvalidOperationException(message);
    }

    private static string Q(string value) =>
        $"\"{value.Replace("\"", "\\\"")}\"";

    private static async Task<R> RunAsync(
        string fileName,
        string arguments,
        int timeoutMs,
        bool fail = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException(
                $"명령 제한시간 초과: {Path.GetFileName(fileName)} {arguments}");
        }

        var result = new R(process.ExitCode, await outTask, await errTask);
        if (fail && result.Code != 0)
            throw new InvalidOperationException(
                $"명령 실패: {result.Err}\n{result.Out}");

        return result;
    }

    private sealed record R(int Code, string Out, string Err);
}
