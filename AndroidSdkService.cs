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
    public AndroidSdkService(){ SdkRoot=ResolveSdkRoot(); if(!string.IsNullOrWhiteSpace(SdkRoot)){AdbPath=Path.Combine(SdkRoot,"platform-tools","adb.exe");EmulatorPath=Path.Combine(SdkRoot,"emulator","emulator.exe");}}
    private static string? ResolveSdkRoot(){ var c=new[]{Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),Environment.GetEnvironmentVariable("ANDROID_HOME"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Android","Sdk")}; return c.FirstOrDefault(p=>!string.IsNullOrWhiteSpace(p)&&Directory.Exists(p)); }
    public bool IsReady(out string m){ if(string.IsNullOrWhiteSpace(SdkRoot)){m="Android SDK를 찾지 못했습니다.";return false;} if(string.IsNullOrWhiteSpace(AdbPath)||!File.Exists(AdbPath)){m=$"adb.exe를 찾지 못했습니다: {AdbPath}";return false;} if(string.IsNullOrWhiteSpace(EmulatorPath)||!File.Exists(EmulatorPath)){m=$"emulator.exe를 찾지 못했습니다: {EmulatorPath}";return false;} m="Android SDK 준비 완료";return true; }
    public async Task<IReadOnlyList<string>> GetAvdsAsync(){ EnsureReady(); var r=await RunAsync(EmulatorPath!,"-list-avds",15000); return r.Out.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToList(); }
    public async Task<Dictionary<string,string>> GetRunningAvdsAsync(){ EnsureReady(); var d=await RunAsync(AdbPath!,"devices",10000); var serials=d.Out.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Where(x=>x.StartsWith("emulator-")&&x.EndsWith("\tdevice")).Select(x=>x.Split('\t')[0]); var map=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); foreach(var s in serials){var n=await TryGetAvdNameAsync(s);if(!string.IsNullOrWhiteSpace(n))map[n]=s;} return map; }
    private async Task<string?> TryGetAvdNameAsync(string s){ try{var r=await RunAsync(AdbPath!,$"-s {Q(s)} emu avd name",6000);return r.Out.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).FirstOrDefault(x=>!x.Equals("OK",StringComparison.OrdinalIgnoreCase));}catch{return null;} }
    public Process StartAvd(string name,int port,int cores=4,int memory=4096){ EnsureReady(); cores=Math.Clamp(cores,1,16); memory=Math.Clamp(memory,1024,16384); var p=new ProcessStartInfo{FileName=EmulatorPath!,Arguments=$"-avd {Q(name)} -port {port} -cores {cores} -memory {memory} -no-snapshot-save",UseShellExecute=false,CreateNoWindow=false,WorkingDirectory=Path.GetDirectoryName(EmulatorPath!)!}; return Process.Start(p)??throw new InvalidOperationException($"AVD 실행 실패: {name}"); }
    public async Task WaitForDeviceAsync(string serial,TimeSpan timeout){EnsureReady();var end=DateTime.UtcNow+timeout;while(DateTime.UtcNow<end){var r=await RunAsync(AdbPath!,$"-s {Q(serial)} get-state",5000,false);if(r.Code==0&&r.Out.Trim().Equals("device",StringComparison.OrdinalIgnoreCase))return;await Task.Delay(1200);}throw new TimeoutException($"에뮬레이터 연결 제한시간 초과: {serial}");}
    public async Task LaunchPackageAsync(string serial,string package){EnsureReady();var e=await RunAsync(AdbPath!,$"-s {Q(serial)} shell pm path {Q(package)}",10000,false);if(e.Code!=0||!e.Out.Contains("package:"))throw new InvalidOperationException($"{serial}에 '{package}' 패키지가 없습니다.");var r=await RunAsync(AdbPath!,$"-s {Q(serial)} shell monkey -p {Q(package)} -c android.intent.category.LAUNCHER 1",15000,false);if(r.Code!=0||r.Out.Contains("No activities found",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException($"게임 실행 실패: {package}");}
    public async Task<IReadOnlyList<string>> GetInstalledUserPackagesAsync(string serial){EnsureReady();var r=await RunAsync(AdbPath!,$"-s {Q(serial)} shell pm list packages -3",20000,false);return r.Out.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Where(x=>x.StartsWith("package:")).Select(x=>x[8..]).OrderBy(x=>x).ToList();}

    public async Task<bool> HasPlayStoreAsync(string serial)
    {
        EnsureReady();
        var r=await RunAsync(AdbPath!,$"-s {Q(serial)} shell pm path com.android.vending",10000,false);
        return r.Code==0&&r.Out.Contains("package:",StringComparison.OrdinalIgnoreCase);
    }

    public async Task OpenPlayStoreSearchAsync(string serial,string query)
    {
        EnsureReady();
        if(!await HasPlayStoreAsync(serial))
            throw new InvalidOperationException($"{serial}에는 Google Play 스토어가 없습니다. Google Play 지원 AVD가 필요합니다.");

        var encoded=Uri.EscapeDataString(string.IsNullOrWhiteSpace(query)?"game":query.Trim());
        var r=await RunAsync(
            AdbPath!,
            $"-s {Q(serial)} shell am start -a android.intent.action.VIEW -d {Q($"market://search?q={encoded}")} com.android.vending",
            15000,
            false);
        if(r.Code!=0)throw new InvalidOperationException($"Play 스토어 검색 열기 실패: {serial}\n{r.Err}\n{r.Out}");
    }

    public async Task OpenPlayStoreDetailsAsync(string serial,string packageName)
    {
        EnsureReady();
        if(!await HasPlayStoreAsync(serial))
            throw new InvalidOperationException($"{serial}에는 Google Play 스토어가 없습니다.");

        var r=await RunAsync(
            AdbPath!,
            $"-s {Q(serial)} shell am start -a android.intent.action.VIEW -d {Q($"market://details?id={packageName}")} com.android.vending",
            15000,
            false);
        if(r.Code!=0)throw new InvalidOperationException($"Play 스토어 앱 페이지 열기 실패: {serial}\n{r.Err}\n{r.Out}");
    }

    public async Task OpenGoogleAccountSettingsAsync(string serial)
    {
        EnsureReady();
        var r=await RunAsync(
            AdbPath!,
            $"-s {Q(serial)} shell am start -a android.settings.ADD_ACCOUNT_SETTINGS",
            15000,
            false);

        if(r.Code!=0)
            throw new InvalidOperationException($"Google 계정 설정 열기 실패: {serial}\n{r.Err}\n{r.Out}");
    }

    public async Task<bool> HasGoogleAccountAsync(string serial)
    {
        EnsureReady();
        var r=await RunAsync(
            AdbPath!,
            $"-s {Q(serial)} shell dumpsys account",
            10000,
            false);

        if(r.Code!=0) return false;

        return Regex.IsMatch(
            r.Out,
            @"Account\s*\{[^}]*type=com\.google",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public async Task<bool> IsPackageInstalledAsync(string serial,string packageName)
    {
        EnsureReady();
        if(string.IsNullOrWhiteSpace(packageName))return false;
        var r=await RunAsync(AdbPath!,$"-s {Q(serial)} shell pm path {Q(packageName)}",10000,false);
        return r.Code==0&&r.Out.Contains("package:",StringComparison.OrdinalIgnoreCase);
    }

    public async Task InstallApkAsync(string serial,string apk){EnsureReady();if(!File.Exists(apk))throw new FileNotFoundException(apk);var r=await RunAsync(AdbPath!,$"-s {Q(serial)} install -r {Q(apk)}",180000,false);if(r.Code!=0||!r.Out.Contains("Success",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException($"APK 설치 실패: {serial}\n{r.Out}\n{r.Err}");}
    public async Task<bool> IsDeviceHealthyAsync(string serial)
    {
        EnsureReady();
        var state=await RunAsync(AdbPath!,$"-s {Q(serial)} get-state",5000,false);
        if(state.Code!=0||!state.Out.Trim().Equals("device",StringComparison.OrdinalIgnoreCase))return false;
        var boot=await RunAsync(AdbPath!,$"-s {Q(serial)} shell getprop sys.boot_completed",5000,false);
        return boot.Code==0&&boot.Out.Trim()=="1";
    }

    public async Task SendTapAsync(string serial,int x,int y){EnsureReady();await RunAsync(AdbPath!,$"-s {Q(serial)} shell input tap {Math.Max(0,x)} {Math.Max(0,y)}",10000,false);}
    public async Task SendSwipeAsync(string serial,int x1,int y1,int x2,int y2,int durationMs=350){EnsureReady();durationMs=Math.Clamp(durationMs,50,5000);await RunAsync(AdbPath!,$"-s {Q(serial)} shell input swipe {Math.Max(0,x1)} {Math.Max(0,y1)} {Math.Max(0,x2)} {Math.Max(0,y2)} {durationMs}",10000,false);}
    public async Task SendTextAsync(string serial,string text){EnsureReady();await RunAsync(AdbPath!,$"-s {Q(serial)} shell input text {Q(text.Replace(" ","%s"))}",10000,false);}
    public async Task SendKeyEventAsync(string serial,int key){EnsureReady();await RunAsync(AdbPath!,$"-s {Q(serial)} shell input keyevent {Math.Max(0,key)}",10000,false);}
    public async Task<(int Width,int Height)> GetDisplaySizeAsync(string serial){EnsureReady();var r=await RunAsync(AdbPath!,$"-s {Q(serial)} shell wm size",10000,false);var m=Regex.Matches(r.Out,@"(\d+)x(\d+)").Cast<Match>().LastOrDefault();return m is null?(1080,1920):(int.Parse(m.Groups[1].Value),int.Parse(m.Groups[2].Value));}
    public string CloneAvd(string source){EnsureReady();var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".android","avd");var si=Path.Combine(root,source+".ini");var sd=Path.Combine(root,source+".avd");if(!File.Exists(si)||!Directory.Exists(sd))throw new InvalidOperationException($"AVD 원본을 찾을 수 없습니다: {source}");int n=1;string name,ini,dir;do{name=$"{source}_copy{n++}";ini=Path.Combine(root,name+".ini");dir=Path.Combine(root,name+".avd");}while(File.Exists(ini)||Directory.Exists(dir));CopyDir(sd,dir);var t=File.ReadAllText(si);t=SetIni(t,"path",dir);t=SetIni(t,"path.rel",$"avd\\{name}.avd");File.WriteAllText(ini,t);var cfg=Path.Combine(dir,"config.ini");if(File.Exists(cfg)){var s=File.ReadAllText(cfg);s=SetIni(s,"AvdId",name);s=SetIni(s,"avd.ini.displayname",name);File.WriteAllText(cfg,s);}return name;}
    public async Task StopEmulatorAsync(string serial){EnsureReady();await RunAsync(AdbPath!,$"-s {Q(serial)} emu kill",10000,false);}
    private static void CopyDir(string s,string d){Directory.CreateDirectory(d);foreach(var f in Directory.GetFiles(s))File.Copy(f,Path.Combine(d,Path.GetFileName(f)),true);foreach(var x in Directory.GetDirectories(s))CopyDir(x,Path.Combine(d,Path.GetFileName(x)));}
    private static string SetIni(string c,string k,string v){var l=c.Replace("\r\n","\n").Split('\n').ToList();var p=k+"=";var i=l.FindIndex(x=>x.StartsWith(p,StringComparison.OrdinalIgnoreCase));if(i>=0)l[i]=p+v;else l.Add(p+v);return string.Join(Environment.NewLine,l);}
    private void EnsureReady(){if(!IsReady(out var m))throw new InvalidOperationException(m);}
    private static string Q(string v)=>$"\"{v.Replace("\"","\\\"")}\"";
    private static async Task<R> RunAsync(string f,string a,int ms,bool fail=true){var p=new ProcessStartInfo{FileName=f,Arguments=a,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};using var pr=new Process{StartInfo=p};pr.Start();var ot=pr.StandardOutput.ReadToEndAsync();var et=pr.StandardError.ReadToEndAsync();using var cts=new CancellationTokenSource(ms);try{await pr.WaitForExitAsync(cts.Token);}catch(OperationCanceledException){try{pr.Kill(true);}catch{}throw new TimeoutException($"명령 제한시간 초과: {Path.GetFileName(f)} {a}");}var r=new R(pr.ExitCode,await ot,await et);if(fail&&r.Code!=0)throw new InvalidOperationException($"명령 실패: {r.Err}\n{r.Out}");return r;}
    private sealed record R(int Code,string Out,string Err);
}