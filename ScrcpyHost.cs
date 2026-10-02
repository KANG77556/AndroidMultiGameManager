using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AndroidMultiGameManager;

public sealed class SyncTapEventArgs : EventArgs
{
    public string Serial { get; }
    public double XRatio { get; }
    public double YRatio { get; }
    public SyncTapEventArgs(string serial,double xRatio,double yRatio){Serial=serial;XRatio=xRatio;YRatio=yRatio;}
}

public sealed class SyncSwipeEventArgs : EventArgs
{
    public string Serial { get; }
    public double X1Ratio { get; }
    public double Y1Ratio { get; }
    public double X2Ratio { get; }
    public double Y2Ratio { get; }
    public int DurationMs { get; }
    public SyncSwipeEventArgs(string serial,double x1,double y1,double x2,double y2,int durationMs){Serial=serial;X1Ratio=x1;Y1Ratio=y1;X2Ratio=x2;Y2Ratio=y2;DurationMs=durationMs;}
}

public sealed class SyncKeyEventArgs : EventArgs
{
    public string Serial { get; }
    public int AndroidKeyCode { get; }
    public SyncKeyEventArgs(string serial,int code){Serial=serial;AndroidKeyCode=code;}
}

public sealed class ScrcpyHost : HwndHost
{
    private const int WS_CHILD=0x40000000,WS_VISIBLE=0x10000000,WS_CLIPCHILDREN=0x02000000,WS_CLIPSIBLINGS=0x04000000,GWL_STYLE=-16;
    private const uint WM_LBUTTONDOWN=0x0201,WM_LBUTTONUP=0x0202,WM_KEYDOWN=0x0100;
    private static readonly ConcurrentDictionary<IntPtr,WeakReference<ScrcpyHost>> Hosts=new();
    private static readonly SUBCLASSPROC SubclassProc=ChildSubclassProc;

    private IntPtr _hostHwnd,_scrcpyHwnd;
    private Process? _process;
    private string? _activeSerial;
    private int _downX,_downY;
    private DateTime _downAt;

    public event EventHandler<SyncTapEventArgs>? TapCaptured;
    public event EventHandler<SyncSwipeEventArgs>? SwipeCaptured;
    public event EventHandler<SyncKeyEventArgs>? KeyCaptured;

    public static readonly DependencyProperty DeviceSerialProperty=DependencyProperty.Register(nameof(DeviceSerial),typeof(string),typeof(ScrcpyHost),new FrameworkPropertyMetadata("-",OnRestartPropertyChanged));
    public static readonly DependencyProperty MaxFpsProperty=DependencyProperty.Register(nameof(MaxFps),typeof(int),typeof(ScrcpyHost),new FrameworkPropertyMetadata(60,OnRestartPropertyChanged));
    public static readonly DependencyProperty MaxSizeProperty=DependencyProperty.Register(nameof(MaxSize),typeof(int),typeof(ScrcpyHost),new FrameworkPropertyMetadata(1080,OnRestartPropertyChanged));
    public static readonly DependencyProperty StatusMessageProperty=DependencyProperty.Register(nameof(StatusMessage),typeof(string),typeof(ScrcpyHost),new FrameworkPropertyMetadata("중지됨"));

    public string DeviceSerial{get=>(string)GetValue(DeviceSerialProperty);set=>SetValue(DeviceSerialProperty,value);}
    public int MaxFps{get=>(int)GetValue(MaxFpsProperty);set=>SetValue(MaxFpsProperty,value);}
    public int MaxSize{get=>(int)GetValue(MaxSizeProperty);set=>SetValue(MaxSizeProperty,value);}
    public string StatusMessage{get=>(string)GetValue(StatusMessageProperty);private set=>SetValue(StatusMessageProperty,value);}
    private static void OnRestartPropertyChanged(DependencyObject d,DependencyPropertyChangedEventArgs e){if(d is ScrcpyHost h)h.RestartForSerialAsync();}

    protected override HandleRef BuildWindowCore(HandleRef parent){_hostHwnd=CreateWindowEx(0,"static","",WS_CHILD|WS_VISIBLE|WS_CLIPCHILDREN|WS_CLIPSIBLINGS,0,0,1,1,parent.Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);RestartForSerialAsync();return new HandleRef(this,_hostHwnd);}
    protected override void DestroyWindowCore(HandleRef hwnd){StopScrcpy();if(_hostHwnd!=IntPtr.Zero){DestroyWindow(_hostHwnd);_hostHwnd=IntPtr.Zero;}}
    protected override IntPtr WndProc(IntPtr hwnd,int msg,IntPtr wp,IntPtr lp,ref bool handled){if(msg==5)ResizeChild();return IntPtr.Zero;}

    private async void RestartForSerialAsync()
    {
        await Task.Yield();
        if(_hostHwnd==IntPtr.Zero)return;
        var serial=DeviceSerial?.Trim();
        if(string.IsNullOrWhiteSpace(serial)||serial=="-"||!serial.StartsWith("emulator-",StringComparison.OrdinalIgnoreCase))
        {
            StopScrcpy();
            StatusMessage="중지됨";
            return;
        }
        StopScrcpy();
        StatusMessage="ADB 연결 확인 중...";
        try
        {
            var sdk=new AndroidSdkService();
            if(!sdk.IsReady(out var sdkMessage))
            {
                StatusMessage=sdkMessage;
                return;
            }

            if(!await sdk.IsDeviceHealthyAsync(serial))
            {
                StatusMessage="Android 부팅 대기 또는 ADB 연결 실패";
                return;
            }

            var exe=ScrcpyLocator.Find();
            if(exe is null)
            {
                StatusMessage="scrcpy를 찾을 수 없습니다.";
                return;
            }

            StatusMessage="화면 연결 중...";
            var title=$"AGMM-{serial}-{Guid.NewGuid():N}";
            var fps=Math.Clamp(MaxFps,15,240);
            var size=Math.Clamp(MaxSize,480,2160);
            var psi=new ProcessStartInfo
            {
                FileName=exe,
                Arguments=$"-s {serial} --window-title={title} --window-borderless --no-audio --no-clipboard-autosync --max-fps={fps} --max-size={size}",
                UseShellExecute=false,
                CreateNoWindow=true,
                RedirectStandardOutput=true,
                RedirectStandardError=true,
                WorkingDirectory=Path.GetDirectoryName(exe)!
            };
            if(!string.IsNullOrWhiteSpace(sdk.AdbPath))
            {
                psi.Environment["ADB"]=sdk.AdbPath;
                var adbDir=Path.GetDirectoryName(sdk.AdbPath)!;
                psi.Environment["PATH"]=adbDir+Path.PathSeparator+(psi.Environment.TryGetValue("PATH",out var currentPath)?currentPath:Environment.GetEnvironmentVariable("PATH")??"");
            }

            _process=Process.Start(psi);
            _activeSerial=serial;
            if(_process is null)
            {
                StatusMessage="scrcpy 실행 실패";
                return;
            }

            var stdoutTask=_process.StandardOutput.ReadToEndAsync();
            var stderrTask=_process.StandardError.ReadToEndAsync();
            var end=DateTime.UtcNow.AddSeconds(20);
            while(DateTime.UtcNow<end&&!_process.HasExited)
            {
                _process.Refresh();
                var hwnd=_process.MainWindowHandle;
                if(hwnd==IntPtr.Zero)hwnd=FindWindow(null,title);
                if(hwnd!=IntPtr.Zero)
                {
                    _scrcpyHwnd=hwnd;
                    Attach(hwnd);
                    ResizeChild();
                    StatusMessage="";
                    return;
                }
                await Task.Delay(150);
            }

            if(_process.HasExited)
            {
                var stdout=await stdoutTask;
                var stderr=await stderrTask;
                var detail=string.Join(" ", new[]{stderr,stdout}
                    .Where(x=>!string.IsNullOrWhiteSpace(x)))
                    .Replace("\r"," ")
                    .Replace("\n"," ")
                    .Trim();

                if(detail.Length>180)detail=detail[..180]+"...";
                StatusMessage=string.IsNullOrWhiteSpace(detail)
                    ? "scrcpy 연결 실패"
                    : "scrcpy 연결 실패: "+detail;
            }
            else
            {
                StatusMessage="화면 연결 시간 초과";
            }
        }
        catch(Exception ex)
        {
            StopScrcpy();
            StatusMessage="화면 연결 오류: "+ex.Message;
        }
    }

    private void Attach(IntPtr hwnd)
    {
        SetParent(hwnd,_hostHwnd);
        var style=GetWindowLongPtr(hwnd,GWL_STYLE).ToInt64()|WS_CHILD;
        SetWindowLongPtr(hwnd,GWL_STYLE,new IntPtr(style));
        ShowWindow(hwnd,5);
        Hosts[hwnd]=new WeakReference<ScrcpyHost>(this);
        SetWindowSubclass(hwnd,SubclassProc,1,UIntPtr.Zero);
    }

    private static IntPtr ChildSubclassProc(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp,UIntPtr id,UIntPtr data)
    {
        if(Hosts.TryGetValue(hwnd,out var wr)&&wr.TryGetTarget(out var host))
        {
            if(msg==WM_LBUTTONDOWN)
            {
                host._downX=(short)(lp.ToInt32()&0xffff);
                host._downY=(short)((lp.ToInt32()>>16)&0xffff);
                host._downAt=DateTime.UtcNow;
            }
            else if(msg==WM_LBUTTONUP&&GetClientRect(hwnd,out var r))
            {
                int raw=lp.ToInt32();
                int x=(short)(raw&0xffff),y=(short)((raw>>16)&0xffff);
                var w=Math.Max(1,r.Right-r.Left);var h=Math.Max(1,r.Bottom-r.Top);
                var x1=Math.Clamp(host._downX/(double)w,0,1);var y1=Math.Clamp(host._downY/(double)h,0,1);
                var x2=Math.Clamp(x/(double)w,0,1);var y2=Math.Clamp(y/(double)h,0,1);
                var distance=Math.Sqrt(Math.Pow(x-host._downX,2)+Math.Pow(y-host._downY,2));
                var duration=(int)Math.Clamp((DateTime.UtcNow-host._downAt).TotalMilliseconds,50,5000);
                host.Dispatcher.BeginInvoke(()=>{
                    if(distance>=18)host.SwipeCaptured?.Invoke(host,new SyncSwipeEventArgs(host.DeviceSerial,x1,y1,x2,y2,duration));
                    else host.TapCaptured?.Invoke(host,new SyncTapEventArgs(host.DeviceSerial,x2,y2));
                });
            }
            else if(msg==WM_KEYDOWN)
            {
                var code=TranslateVirtualKey(wp.ToInt32());
                if(code>0)host.Dispatcher.BeginInvoke(()=>host.KeyCaptured?.Invoke(host,new SyncKeyEventArgs(host.DeviceSerial,code)));
            }
        }
        return DefSubclassProc(hwnd,msg,wp,lp);
    }

    private static int TranslateVirtualKey(int vk)=>vk switch
    {
        0x08=>67,0x09=>61,0x0D=>66,0x1B=>4,0x20=>62,
        0x25=>21,0x26=>19,0x27=>22,0x28=>20,
        0x24=>3,0x23=>123,0x2E=>112,
        >=0x30 and <=0x39=>vk-0x30+7,
        _=>0
    };

    private void ResizeChild(){if(_scrcpyHwnd!=IntPtr.Zero&&_hostHwnd!=IntPtr.Zero&&GetClientRect(_hostHwnd,out var r))MoveWindow(_scrcpyHwnd,0,0,Math.Max(1,r.Right-r.Left),Math.Max(1,r.Bottom-r.Top),true);}
    private void StopScrcpy(){if(_scrcpyHwnd!=IntPtr.Zero){try{RemoveWindowSubclass(_scrcpyHwnd,SubclassProc,1);}catch{}Hosts.TryRemove(_scrcpyHwnd,out _);}try{if(_process is {HasExited:false})_process.Kill(true);}catch{} _process?.Dispose();_process=null;_scrcpyHwnd=IntPtr.Zero;_activeSerial=null;}

    private delegate IntPtr SUBCLASSPROC(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp,UIntPtr id,UIntPtr data);
    [DllImport("comctl32.dll",SetLastError=true)] static extern bool SetWindowSubclass(IntPtr hWnd,SUBCLASSPROC pfnSubclass,UIntPtr uIdSubclass,UIntPtr dwRefData);
    [DllImport("comctl32.dll",SetLastError=true)] static extern bool RemoveWindowSubclass(IntPtr hWnd,SUBCLASSPROC pfnSubclass,UIntPtr uIdSubclass);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hWnd,uint uMsg,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex,string cls,string name,int style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr inst,IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child,IntPtr parent);
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string? cls,string name);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd,out RECT rect);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hWnd,int x,int y,int w,int h,bool repaint);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd,int cmd);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtr")] static extern IntPtr GetWindowLongPtr64(IntPtr hWnd,int index);
    [DllImport("user32.dll",EntryPoint="GetWindowLong")] static extern IntPtr GetWindowLong32(IntPtr hWnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtr")] static extern IntPtr SetWindowLongPtr64(IntPtr hWnd,int index,IntPtr value);
    [DllImport("user32.dll",EntryPoint="SetWindowLong")] static extern IntPtr SetWindowLong32(IntPtr hWnd,int index,IntPtr value);
    static IntPtr GetWindowLongPtr(IntPtr h,int i)=>IntPtr.Size==8?GetWindowLongPtr64(h,i):GetWindowLong32(h,i);
    static IntPtr SetWindowLongPtr(IntPtr h,int i,IntPtr v)=>IntPtr.Size==8?SetWindowLongPtr64(h,i,v):SetWindowLong32(h,i,v);
    [StructLayout(LayoutKind.Sequential)] struct RECT{public int Left,Top,Right,Bottom;}
}

public static class ScrcpyLocator
{
    public static string? Find()
    {
        var env=Environment.GetEnvironmentVariable("SCRCPY_PATH");
        if(!string.IsNullOrWhiteSpace(env)&&File.Exists(env))return env;
        var list=new List<string>{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"scrcpy","scrcpy.exe"),@"C:\Tools\scrcpy\scrcpy.exe"};
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WinGet","Packages");
        if(Directory.Exists(root)){var f=Directory.EnumerateFiles(root,"scrcpy.exe",SearchOption.AllDirectories).FirstOrDefault(p=>p.Contains("Genymobile.scrcpy",StringComparison.OrdinalIgnoreCase));if(f is not null)list.Insert(0,f);}
        return list.FirstOrDefault(File.Exists);
    }
}