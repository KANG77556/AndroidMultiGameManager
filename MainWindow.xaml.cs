using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AndroidMultiGameManager;

public partial class MainWindow : Window
{
    private readonly AndroidSdkService _sdk=new();
    private readonly ObservableCollection<AvdItem> _items=new();
    private readonly DispatcherTimer _refreshTimer=new(){Interval=TimeSpan.FromSeconds(4)};
    private readonly DispatcherTimer _clockTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    private AppSettings _settings=new();
    private List<GameProfile> _profiles=new();
    private bool _busy,_syncBroadcasting;

    public MainWindow()
    {
        InitializeComponent();
        _settings=SettingsService.Load();
        _profiles=GameProfileService.Load();
        PackageTextBox.Text=string.IsNullOrWhiteSpace(_settings.PackageName)?"com.example.game":_settings.PackageName;
        CpuCoresTextBox.Text=_settings.CpuCores.ToString();
        MemoryMbTextBox.Text=_settings.MemoryMb.ToString();
        SyncClickCheckBox.IsChecked=_settings.SyncClick;
        RunAtStartupCheckBox.IsChecked=AppMaintenanceService.IsRunAtStartupEnabled();
        CompactInstanceList.ItemsSource=_items; CardItems.ItemsSource=_items;
        ReloadProfiles(); ApplySavedLayoutSelection();
        Loaded+=async(_,_)=>await InitializeAsync();
        Closed+=(_,_)=>{_refreshTimer.Stop();_clockTimer.Stop();SaveSettings();};
        _refreshTimer.Tick+=async(_,_)=>{if(!_busy){await RefreshRunningStatesAsync();UpdateMetrics();}};
        _clockTimer.Tick+=(_,_)=>ClockText.Text=DateTime.Now.ToString("yyyy.MM.dd  HH:mm:ss");
    }

    private async Task InitializeAsync(){ClockText.Text=DateTime.Now.ToString("yyyy.MM.dd  HH:mm:ss");_clockTimer.Start();SdkPathText.Text=$"SDK: {_sdk.SdkRoot??"찾을 수 없음"}";if(!_sdk.IsReady(out var m)){Log(m);MessageBox.Show(m);return;}Log(m);await RefreshAsync();UpdateMetrics();_refreshTimer.Start();}
    private async Task RefreshAsync(){try{ToggleBusy(true);var avds=await _sdk.GetAvdsAsync();var running=await _sdk.GetRunningAvdsAsync();var selected=_items.Where(x=>x.IsSelected).Select(x=>x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);_items.Clear();foreach(var avd in avds){var i=new AvdItem{Name=avd,IsSelected=selected.Contains(avd)||_settings.SelectedAvds.Contains(avd,StringComparer.OrdinalIgnoreCase),MaxFps=GetProfileFps(),MaxSize=GetProfileSize()};if(running.TryGetValue(avd,out var s)){i.Status="실행 중";i.DeviceSerial=s;}_items.Add(i);}UpdateAdbSummary(running);ApplyLayout();}catch(Exception ex){Log("새로고침 실패: "+ex.Message);}finally{ToggleBusy(false);}}
    private async Task RefreshRunningStatesAsync(){try{var r=await _sdk.GetRunningAvdsAsync();foreach(var i in _items){if(r.TryGetValue(i.Name,out var s)){i.Status="실행 중";i.DeviceSerial=s;}else if(i.Status!="시작 중"){i.Status="중지됨";i.DeviceSerial="-";}}UpdateAdbSummary(r);}catch(Exception ex){Log("상태 확인 실패: "+ex.Message);}}
    private void UpdateAdbSummary(Dictionary<string,string> r)=>AdbSummaryText.Text=r.Count==0?"연결된 에뮬레이터 없음":string.Join(Environment.NewLine,r.OrderBy(x=>x.Value).Select(x=>$"● {x.Value}"));

    private async void RefreshButton_Click(object s,RoutedEventArgs e)=>await RefreshAsync();
    private async void StartSelectedButton_Click(object s,RoutedEventArgs e)=>await StartItemsAsync(_items.Where(x=>x.IsSelected).ToList());
    private async void CardStart_Click(object s,RoutedEventArgs e){if(s is FrameworkElement{Tag:AvdItem i})await StartItemsAsync(new(){i});}
    private async Task StartItemsAsync(List<AvdItem> targets)
    {
        if(targets.Count==0){MessageBox.Show("실행할 인스턴스를 선택하세요.");return;}
        try{ToggleBusy(true);var running=await _sdk.GetRunningAvdsAsync();var used=running.Values.Where(x=>x.StartsWith("emulator-")).Select(x=>int.TryParse(x[9..],out var p)?p:-1).Where(x=>x>0).ToHashSet();foreach(var i in targets){if(running.TryGetValue(i.Name,out var es)){i.Status="실행 중";i.DeviceSerial=es;continue;}var port=FindPort(used);used.Add(port);i.Status="시작 중";i.DeviceSerial=$"emulator-{port}";_sdk.StartAvd(i.Name,port,GetCpu(),GetMemory());Log($"시작: {i.Name} → {i.DeviceSerial}");}await Task.WhenAll(targets.Where(x=>x.Status=="시작 중").Select(async i=>{try{await _sdk.WaitForDeviceAsync(i.DeviceSerial,TimeSpan.FromSeconds(120));await Dispatcher.InvokeAsync(()=>i.Status="실행 중");}catch(Exception ex){await Dispatcher.InvokeAsync(()=>i.Status="오류");Log(ex.Message);}}));await RefreshRunningStatesAsync();}finally{ToggleBusy(false);}
    }
    private async void LaunchGameButton_Click(object s,RoutedEventArgs e)=>await LaunchGameAsync(PackageTextBox.Text.Trim());
    private async Task LaunchGameAsync(string package){var t=SelectedRunning();if(t.Count==0){MessageBox.Show("실행 중인 선택 인스턴스가 없습니다.");return;}if(string.IsNullOrWhiteSpace(package)){MessageBox.Show("패키지명을 입력하세요.");return;}await Task.WhenAll(t.Select(async i=>{try{await _sdk.LaunchPackageAsync(i.DeviceSerial,package);Log($"게임 실행: {package} → {i.Name}");}catch(Exception ex){Log(ex.Message);}}));}
    private async void StopSelectedButton_Click(object s,RoutedEventArgs e)=>await StopItemsAsync(SelectedRunning());
    private async void CardStop_Click(object s,RoutedEventArgs e){if(s is FrameworkElement{Tag:AvdItem i}&&i.DeviceSerial.StartsWith("emulator-"))await StopItemsAsync(new(){i});}
    private async void StopAllButton_Click(object s,RoutedEventArgs e)=>await StopItemsAsync(_items.Where(x=>x.DeviceSerial.StartsWith("emulator-")).ToList());
    private async Task StopItemsAsync(List<AvdItem> t){if(t.Count==0)return;await Task.WhenAll(t.Select(async i=>{try{var serial=i.DeviceSerial;await _sdk.StopEmulatorAsync(serial);await Dispatcher.InvokeAsync(()=>{i.Status="중지됨";i.DeviceSerial="-";});}catch(Exception ex){Log(ex.Message);}}));}

    private async void RefreshAppsButton_Click(object s,RoutedEventArgs e){var t=SelectedRunning().FirstOrDefault()??_items.FirstOrDefault(x=>x.Status=="실행 중");if(t is null){MessageBox.Show("실행 중인 인스턴스가 없습니다.");return;}var p=await _sdk.GetInstalledUserPackagesAsync(t.DeviceSerial);InstalledAppsComboBox.ItemsSource=p;if(p.Count>0)InstalledAppsComboBox.SelectedIndex=0;}
    private void InstalledAppsComboBox_SelectionChanged(object s,SelectionChangedEventArgs e){if(InstalledAppsComboBox.SelectedItem is string p)PackageTextBox.Text=p;}
    private async void SelectApkButton_Click(object s,RoutedEventArgs e){var d=new OpenFileDialog{Filter="Android APK (*.apk)|*.apk"};if(d.ShowDialog()==true)await InstallApkAsync(d.FileName);}
    private async void Window_Drop(object s,DragEventArgs e){if(e.Data.GetDataPresent(DataFormats.FileDrop)&&e.Data.GetData(DataFormats.FileDrop) is string[] f){var apk=f.FirstOrDefault(x=>x.EndsWith(".apk",StringComparison.OrdinalIgnoreCase));if(apk is not null)await InstallApkAsync(apk);}}
    private async Task InstallApkAsync(string path){var t=SelectedRunning();if(t.Count==0){MessageBox.Show("설치 대상 인스턴스를 선택하세요.");return;}await Task.WhenAll(t.Select(async i=>{try{await _sdk.InstallApkAsync(i.DeviceSerial,path);Log($"APK 설치 완료: {i.Name}");}catch(Exception ex){Log(ex.Message);}}));}

    private async void CloneSelectedAvd_Click(object s,RoutedEventArgs e){var t=_items.Where(x=>x.IsSelected).ToList();if(t.Count!=1){MessageBox.Show("복제할 AVD 하나만 선택하세요.");return;}if(t[0].Status=="실행 중"){MessageBox.Show("원본 AVD를 종료한 뒤 복제하세요.");return;}try{var n=_sdk.CloneAvd(t[0].Name);Log($"복제 완료: {n}");await RefreshAsync();}catch(Exception ex){MessageBox.Show(ex.Message);}}

    private async void SyncTapButton_Click(object s,RoutedEventArgs e){if(int.TryParse(SyncXTextBox.Text,out var x)&&int.TryParse(SyncYTextBox.Text,out var y))await Task.WhenAll(SelectedRunning().Select(i=>_sdk.SendTapAsync(i.DeviceSerial,x,y)));}
    private async void SyncTextButton_Click(object s,RoutedEventArgs e)=>await Task.WhenAll(SelectedRunning().Select(i=>_sdk.SendTextAsync(i.DeviceSerial,SyncTextBox.Text)));
    private async void SyncKeyButton_Click(object s,RoutedEventArgs e){if(int.TryParse(KeyCodeTextBox.Text,out var k))await Task.WhenAll(SelectedRunning().Select(i=>_sdk.SendKeyEventAsync(i.DeviceSerial,k)));}
    private async void ScrcpyHost_TapCaptured(object? sender,SyncTapEventArgs e)
    {
        if(SyncClickCheckBox.IsChecked!=true||_syncBroadcasting)return;
        var targets=SelectedRunning().Where(x=>!x.DeviceSerial.Equals(e.Serial,StringComparison.OrdinalIgnoreCase)).ToList();
        if(targets.Count==0)return;
        _syncBroadcasting=true;
        try{await Task.WhenAll(targets.Select(async i=>{var size=await _sdk.GetDisplaySizeAsync(i.DeviceSerial);var x=(int)Math.Round(e.XRatio*(size.Width-1));var y=(int)Math.Round(e.YRatio*(size.Height-1));await _sdk.SendTapAsync(i.DeviceSerial,x,y);}));Log($"실시간 동기 클릭 → {targets.Count}개");}catch(Exception ex){Log("동기 클릭 실패: "+ex.Message);}finally{_syncBroadcasting=false;}
    }

    private async void ScrcpyHost_SwipeCaptured(object? sender,SyncSwipeEventArgs e)
    {
        if(SyncClickCheckBox.IsChecked!=true||_syncBroadcasting)return;
        var targets=SelectedRunning().Where(x=>!x.DeviceSerial.Equals(e.Serial,StringComparison.OrdinalIgnoreCase)).ToList();
        if(targets.Count==0)return;
        _syncBroadcasting=true;
        try
        {
            await Task.WhenAll(targets.Select(async i=>{
                var size=await _sdk.GetDisplaySizeAsync(i.DeviceSerial);
                var x1=(int)Math.Round(e.X1Ratio*(size.Width-1));
                var y1=(int)Math.Round(e.Y1Ratio*(size.Height-1));
                var x2=(int)Math.Round(e.X2Ratio*(size.Width-1));
                var y2=(int)Math.Round(e.Y2Ratio*(size.Height-1));
                await _sdk.SendSwipeAsync(i.DeviceSerial,x1,y1,x2,y2,e.DurationMs);
            }));
            Log($"실시간 동기 스와이프 → {targets.Count}개");
        }
        catch(Exception ex){Log("동기 스와이프 실패: "+ex.Message);}
        finally{_syncBroadcasting=false;}
    }

    private async void ScrcpyHost_KeyCaptured(object? sender,SyncKeyEventArgs e)
    {
        if(SyncClickCheckBox.IsChecked!=true||_syncBroadcasting)return;
        var targets=SelectedRunning().Where(x=>!x.DeviceSerial.Equals(e.Serial,StringComparison.OrdinalIgnoreCase)).ToList();
        if(targets.Count==0)return;
        _syncBroadcasting=true;
        try{await Task.WhenAll(targets.Select(i=>_sdk.SendKeyEventAsync(i.DeviceSerial,e.AndroidKeyCode)));Log($"실시간 동기 키 {e.AndroidKeyCode} → {targets.Count}개");}
        catch(Exception ex){Log("동기 키 실패: "+ex.Message);}
        finally{_syncBroadcasting=false;}
    }

    private void SaveProfileButton_Click(object s,RoutedEventArgs e){var name=ProfileNameTextBox.Text.Trim();if(string.IsNullOrWhiteSpace(name))return;var p=_profiles.FirstOrDefault(x=>x.Name.Equals(name,StringComparison.OrdinalIgnoreCase));if(p is null){p=new GameProfile{Name=name};_profiles.Add(p);}p.PackageName=PackageTextBox.Text.Trim();p.CpuCores=GetCpu();p.MemoryMb=GetMemory();p.MaxFps=GetProfileFps();p.MaxSize=GetProfileSize();p.AutoLaunchDelayMs=GetAutoLaunchDelay();GameProfileService.Save(_profiles);ReloadProfiles(name);Log($"프로필 저장: {name}");}
    private void ProfileComboBox_SelectionChanged(object s,SelectionChangedEventArgs e){if(ProfileComboBox.SelectedItem is GameProfile p){ProfileNameTextBox.Text=p.Name;PackageTextBox.Text=p.PackageName;CpuCoresTextBox.Text=p.CpuCores.ToString();MemoryMbTextBox.Text=p.MemoryMb.ToString();ProfileFpsTextBox.Text=p.MaxFps.ToString();ProfileSizeTextBox.Text=p.MaxSize.ToString();AutoLaunchDelayTextBox.Text=p.AutoLaunchDelayMs.ToString();foreach(var i in _items.Where(x=>x.IsSelected)){i.MaxFps=p.MaxFps;i.MaxSize=p.MaxSize;}}}
    private async void RunProfileButton_Click(object s,RoutedEventArgs e){var selected=_items.Where(x=>x.IsSelected).ToList();if(selected.Count==0){MessageBox.Show("실행할 AVD를 선택하세요.");return;}foreach(var i in selected){i.MaxFps=GetProfileFps();i.MaxSize=GetProfileSize();}await StartItemsAsync(selected);await Task.Delay(GetAutoLaunchDelay());var package=PackageTextBox.Text.Trim();if(!string.IsNullOrWhiteSpace(package))await LaunchGameAsync(package);}
    private void ReloadProfiles(string? select=null){ProfileComboBox.ItemsSource=null;ProfileComboBox.ItemsSource=_profiles;ProfileComboBox.DisplayMemberPath=nameof(GameProfile.Name);var p=select is null?_profiles.FirstOrDefault():_profiles.FirstOrDefault(x=>x.Name.Equals(select,StringComparison.OrdinalIgnoreCase));if(p is not null)ProfileComboBox.SelectedItem=p;}

    private void LayoutComboBox_SelectionChanged(object s,SelectionChangedEventArgs e){ApplyLayout();SaveSettings();}
    private void Window_SizeChanged(object s,SizeChangedEventArgs e){if(IsLoaded)ApplyLayout();}
    private void ApplyLayout(){if(CardItems is null||LayoutComboBox is null)return;var mode=(LayoutComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()??"자동";var avail=Math.Max(600,ActualWidth-350);var col=mode switch{"2×2"=>2,"3×2"=>3,_=>avail>=1280?3:2};CardItems.Tag=Math.Clamp((avail-col*16)/col,300,560);}
    private void ApplySavedLayoutSelection(){var wanted=_settings.LayoutMode;foreach(var i in LayoutComboBox.Items.OfType<ComboBoxItem>())if((i.Content?.ToString()??"")==wanted){LayoutComboBox.SelectedItem=i;break;}}
    private void SaveSettings(){try{SettingsService.Save(new AppSettings{PackageName=PackageTextBox.Text.Trim(),LayoutMode=(LayoutComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()??"자동",SelectedAvds=_items.Where(x=>x.IsSelected).Select(x=>x.Name).ToList(),CpuCores=GetCpu(),MemoryMb=GetMemory(),SyncClick=SyncClickCheckBox.IsChecked==true,RunAtStartup=RunAtStartupCheckBox.IsChecked==true});}catch(Exception ex){AppMaintenanceService.AppendLog("설정 저장 실패: "+ex.Message);}}


    private void RunAtStartupCheckBox_Changed(object s,RoutedEventArgs e)
    {
        try
        {
            var enabled=RunAtStartupCheckBox.IsChecked==true;
            AppMaintenanceService.SetRunAtStartup(enabled);
            _settings.RunAtStartup=enabled;
            SaveSettings();
            Log(enabled?"시작프로그램 등록 완료":"시작프로그램 등록 해제");
        }
        catch(Exception ex)
        {
            Log("시작프로그램 설정 실패: "+ex.Message);
            MessageBox.Show(ex.Message,"시작프로그램 오류",MessageBoxButton.OK,MessageBoxImage.Error);
        }
    }

    private void BackupSettingsButton_Click(object s,RoutedEventArgs e)
    {
        try
        {
            SaveSettings();
            GameProfileService.Save(_profiles);
            var path=AppMaintenanceService.CreateBackup();
            Log("설정 백업 완료: "+path);
            MessageBox.Show(path,"백업 완료",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        catch(Exception ex)
        {
            Log("설정 백업 실패: "+ex.Message);
            MessageBox.Show(ex.Message,"백업 오류",MessageBoxButton.OK,MessageBoxImage.Error);
        }
    }

    private void RestoreSettingsButton_Click(object s,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog
        {
            Title="Android Multi Game Manager 백업 파일 선택",
            Filter="JSON 백업 (*.json)|*.json"
        };
        if(dialog.ShowDialog()!=true)return;

        try
        {
            AppMaintenanceService.RestoreBackup(dialog.FileName);
            Log("설정 복원 완료. 프로그램을 다시 시작하면 복원된 설정이 적용됩니다.");
            MessageBox.Show("설정 복원이 완료되었습니다. 프로그램을 다시 시작하면 적용됩니다.","복원 완료",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        catch(Exception ex)
        {
            Log("설정 복원 실패: "+ex.Message);
            MessageBox.Show(ex.Message,"복원 오류",MessageBoxButton.OK,MessageBoxImage.Error);
        }
    }

    private void OpenLogFolderButton_Click(object s,RoutedEventArgs e)
    {
        try
        {
            var logFile=AppMaintenanceService.LogFilePath;
            var folder=Path.GetDirectoryName(logFile)!;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo{FileName="explorer.exe",Arguments=$"\"{folder}\"",UseShellExecute=true});
        }
        catch(Exception ex)
        {
            Log("로그 폴더 열기 실패: "+ex.Message);
        }
    }

    private int GetCpu()=>int.TryParse(CpuCoresTextBox.Text,out var v)?Math.Clamp(v,1,16):4;
    private int GetProfileFps()=>int.TryParse(ProfileFpsTextBox.Text,out var v)?Math.Clamp(v,15,240):60;
    private int GetProfileSize()=>int.TryParse(ProfileSizeTextBox.Text,out var v)?Math.Clamp(v,480,2160):1080;
    private int GetAutoLaunchDelay()=>int.TryParse(AutoLaunchDelayTextBox.Text,out var v)?Math.Clamp(v,0,30000):1500;
    private int GetMemory()=>int.TryParse(MemoryMbTextBox.Text,out var v)?Math.Clamp(v,1024,16384):4096;
    private List<AvdItem> SelectedRunning()=>_items.Where(x=>x.IsSelected&&x.Status=="실행 중"&&x.DeviceSerial.StartsWith("emulator-")).ToList();
    private static int FindPort(HashSet<int> used){for(int p=5554;p<=5680;p+=2)if(!used.Contains(p))return p;throw new InvalidOperationException("사용 가능한 포트가 없습니다.");}
    private void ToggleBusy(bool b){_busy=b;RefreshButton.IsEnabled=!b;StartSelectedButton.IsEnabled=!b;StopAllButton.IsEnabled=!b;}
    private async void CheckUpdateButton_Click(object s,RoutedEventArgs e)
    {
        try
        {
            var result=await UpdateService.CheckAndDownloadAsync(UpdateService.LoadManifestUrl());
            Log(result.Message);
            if(result.HasUpdate&&result.InstallerPath is not null)
            {
                var answer=MessageBox.Show($"{result.Message}\n지금 설치 프로그램을 실행할까요?","업데이트",MessageBoxButton.YesNo,MessageBoxImage.Information);
                if(answer==MessageBoxResult.Yes)UpdateService.LaunchInstaller(result.InstallerPath);
            }
            else MessageBox.Show(result.Message,"업데이트",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        catch(Exception ex){Log("업데이트 확인 실패: "+ex.Message);MessageBox.Show(ex.Message,"업데이트 오류");}
    }

    private void ClearLog_Click(object s,RoutedEventArgs e)=>LogTextBox.Clear();
    private void Log(string m)
    {
        AppMaintenanceService.AppendLog(m);
        Dispatcher.Invoke(()=>{
            LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {m}{Environment.NewLine}");
            LogTextBox.ScrollToEnd();
        });
    }
    private void UpdateMetrics(){try{var m=SystemMetricsService.Read();CpuText.Text=$"CPU {m.CpuPercent:0}%";RamText.Text=$"RAM {m.MemoryPercent:0}%";RamDetailText.Text=$"{m.UsedMemoryGb:0.0} / {m.TotalMemoryGb:0.0} GB";}catch{}}
}