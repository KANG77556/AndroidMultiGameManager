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
    private List<InstanceGroup> _groups=new();
    private readonly Dictionary<string,string> _accountAliases=InstanceAccountService.Load();
    private readonly Dictionary<string,HashSet<string>> _playStoreSnapshots=new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _playStoreWatchCts;
    private bool _busy,_syncBroadcasting;

    public MainWindow()
    {
        InitializeComponent();
        _settings=SettingsService.Load();
        _profiles=GameProfileService.Load();
        _groups=InstanceGroupService.Load();
        PackageTextBox.Text=AndroidSdkService.IsValidPackageName(_settings.PackageName)?_settings.PackageName:"";
        CpuCoresTextBox.Text=_settings.CpuCores.ToString();
        MemoryMbTextBox.Text=_settings.MemoryMb.ToString();
        RetryCountTextBox.Text=_settings.StartRetryCount.ToString();
        SyncClickCheckBox.IsChecked=_settings.SyncClick;
        RunAtStartupCheckBox.IsChecked=AppMaintenanceService.IsRunAtStartupEnabled();
        CompactInstanceList.ItemsSource=_items; CardItems.ItemsSource=_items;
        ReloadGroups(); ReloadProfiles(); ApplySavedLayoutSelection();
        Loaded+=async(_,_)=>await InitializeAsync();
        Closed+=(_,_)=>{_playStoreWatchCts?.Cancel();_refreshTimer.Stop();_clockTimer.Stop();SaveSettings();};
        _refreshTimer.Tick+=async(_,_)=>{if(!_busy){await RefreshRunningStatesAsync();await CheckHealthAsync();UpdateMetrics();}};
        _clockTimer.Tick+=(_,_)=>ClockText.Text=DateTime.Now.ToString("yyyy.MM.dd  HH:mm:ss");
    }

    private async Task InitializeAsync(){ClockText.Text=DateTime.Now.ToString("yyyy.MM.dd  HH:mm:ss");_clockTimer.Start();SdkPathText.Text=$"SDK: {_sdk.SdkRoot??"찾을 수 없음"}";if(!_sdk.IsReady(out var m)){Log(m);MessageBox.Show(m);return;}Log(m);await RefreshAsync();UpdateMetrics();_refreshTimer.Start();}
    private async Task RefreshAsync(){try{ToggleBusy(true);var avds=await _sdk.GetAvdsAsync();var running=await _sdk.GetRunningAvdsAsync();var selected=_items.Where(x=>x.IsSelected).Select(x=>x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);_items.Clear();foreach(var avd in avds){var i=new AvdItem{Name=avd,IsSelected=selected.Contains(avd)||_settings.SelectedAvds.Contains(avd,StringComparer.OrdinalIgnoreCase),MaxFps=GetProfileFps(),MaxSize=GetProfileSize(),AccountAlias=_accountAliases.TryGetValue(avd,out var alias)?alias:"미지정"};if(running.TryGetValue(avd,out var s)){i.Status="실행 중";i.DeviceSerial=s;}_items.Add(i);}UpdateAdbSummary(running);ApplyLayout();}catch(Exception ex){Log("새로고침 실패: "+ex.Message);}finally{ToggleBusy(false);}}
    private async Task RefreshRunningStatesAsync(){try{var r=await _sdk.GetRunningAvdsAsync();foreach(var i in _items){if(r.TryGetValue(i.Name,out var s)){i.Status="실행 중";i.DeviceSerial=s;}else if(i.Status!="시작 중"){i.Status="중지됨";i.DeviceSerial="-";}}UpdateAdbSummary(r);}catch(Exception ex){Log("상태 확인 실패: "+ex.Message);}}
    private void UpdateAdbSummary(Dictionary<string,string> r)=>AdbSummaryText.Text=r.Count==0?"연결된 에뮬레이터 없음":string.Join(Environment.NewLine,r.OrderBy(x=>x.Value).Select(x=>$"● {x.Value}"));

    private async void RefreshButton_Click(object s,RoutedEventArgs e)=>await RefreshAsync();
    private async void StartSelectedButton_Click(object s,RoutedEventArgs e)=>await StartItemsAsync(_items.Where(x=>x.IsSelected).ToList());
    private async void CardStart_Click(object s,RoutedEventArgs e){if(s is FrameworkElement{Tag:AvdItem i})await StartItemsAsync(new(){i});}
    private async Task StartItemsAsync(List<AvdItem> targets)
    {
        if(targets.Count==0){MessageBox.Show("실행할 인스턴스를 선택하세요.");return;}
        try
        {
            ToggleBusy(true);
            var running=await _sdk.GetRunningAvdsAsync();
            var used=running.Values.Where(x=>x.StartsWith("emulator-"))
                .Select(x=>int.TryParse(x[9..],out var p)?p:-1).Where(x=>x>0).ToHashSet();

            foreach(var item in targets)
            {
                if(running.TryGetValue(item.Name,out var existingSerial))
                {
                    item.Status="실행 중";
                    item.DeviceSerial=existingSerial;
                    continue;
                }

                var port=FindPort(used);
                used.Add(port);
                item.Status="시작 중";
                item.DeviceSerial=$"emulator-{port}";
                _sdk.StartAvd(item.Name,port,GetCpu(),GetMemory());
                Log($"시작: {item.Name} → {item.DeviceSerial}");
            }

            var retryCount=GetRetryCount();
            await Task.WhenAll(targets.Where(x=>x.Status=="시작 중").Select(async item=>
            {
                Exception? lastError=null;
                for(var attempt=0;attempt<=retryCount;attempt++)
                {
                    try
                    {
                        if(attempt>0)
                        {
                            Log($"재시도 {attempt}/{retryCount}: {item.Name}");
                            await Task.Delay(2000);
                        }

                        await _sdk.WaitForDeviceAsync(item.DeviceSerial,TimeSpan.FromSeconds(45));
                        var healthy=await _sdk.IsDeviceHealthyAsync(item.DeviceSerial);
                        if(!healthy) throw new InvalidOperationException("Android 부팅 완료 상태가 아닙니다.");

                        await Dispatcher.InvokeAsync(()=>
                        {
                            item.Status="실행 중";
                            item.Health="정상";
                        });
                        return;
                    }
                    catch(Exception ex)
                    {
                        lastError=ex;
                    }
                }

                await Dispatcher.InvokeAsync(()=>
                {
                    item.Status="오류";
                    item.Health="실패";
                });
                Log($"시작 실패: {item.Name} - {lastError?.Message}");
            }));

            await RefreshRunningStatesAsync();
            await CheckHealthAsync();
        }
        finally
        {
            ToggleBusy(false);
        }
    }
    private async void LaunchGameButton_Click(object s,RoutedEventArgs e)=>await LaunchGameAsync(PackageTextBox.Text.Trim());
    private async Task LaunchGameAsync(string package)
    {
        var targets=SelectedRunning();
        if(targets.Count==0)
        {
            MessageBox.Show("실행 중인 선택 인스턴스가 없습니다.");
            return;
        }

        if(!AndroidSdkService.IsValidPackageName(package))
        {
            MessageBox.Show(
                "실제 게임 패키지명을 선택하세요.\n\n" +
                "Play 스토어에서 게임을 설치한 뒤 '설치 완료 감지' 또는 '앱 조회'를 사용하면 패키지명이 자동으로 입력됩니다.",
                "게임 패키지 필요",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await Task.WhenAll(targets.Select(async item=>
        {
            try
            {
                if(!await _sdk.IsDeviceHealthyAsync(item.DeviceSerial))
                {
                    item.Health="주의";
                    Log($"게임 실행 건너뜀: {item.Name} - ADB 연결 응답 없음");
                    return;
                }

                await _sdk.LaunchPackageAsync(item.DeviceSerial,package);
                Log($"게임 실행: {package} → {item.Name}");
            }
            catch(Exception ex)
            {
                Log($"게임 실행 실패: {item.Name} - {ex.Message}");
            }
        }));
    }
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

    private void ReloadGroups(string? select=null)
    {
        GroupComboBox.ItemsSource=null;
        GroupComboBox.ItemsSource=_groups;
        GroupComboBox.DisplayMemberPath=nameof(InstanceGroup.Name);
        var group=select is null?_groups.FirstOrDefault():_groups.FirstOrDefault(x=>x.Name.Equals(select,StringComparison.OrdinalIgnoreCase));
        if(group is not null)GroupComboBox.SelectedItem=group;
    }

    private void SaveGroupButton_Click(object s,RoutedEventArgs e)
    {
        var name=GroupNameTextBox.Text.Trim();
        var selected=_items.Where(x=>x.IsSelected).Select(x=>x.Name).ToList();
        if(string.IsNullOrWhiteSpace(name)){MessageBox.Show("그룹 이름을 입력하세요.");return;}
        if(selected.Count==0){MessageBox.Show("그룹에 포함할 인스턴스를 선택하세요.");return;}

        var group=_groups.FirstOrDefault(x=>x.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
        if(group is null){group=new InstanceGroup{Name=name};_groups.Add(group);}
        group.AvdNames=selected;
        InstanceGroupService.Save(_groups);
        ReloadGroups(name);
        Log($"인스턴스 그룹 저장: {name} / {selected.Count}개");
    }

    private void GroupComboBox_SelectionChanged(object s,SelectionChangedEventArgs e)
    {
        if(GroupComboBox.SelectedItem is not InstanceGroup group)return;
        GroupNameTextBox.Text=group.Name;
        var names=group.AvdNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var item in _items)item.IsSelected=names.Contains(item.Name);
        Log($"그룹 선택: {group.Name} / {group.AvdNames.Count}개");
    }

    private async void RunGroupScenarioButton_Click(object s,RoutedEventArgs e)
    {
        if(GroupComboBox.SelectedItem is not InstanceGroup group)
        {
            MessageBox.Show("실행할 인스턴스 그룹을 선택하세요.");
            return;
        }

        var names=group.AvdNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets=_items.Where(x=>names.Contains(x.Name)).ToList();
        if(targets.Count==0){MessageBox.Show("그룹에 실행 가능한 AVD가 없습니다.");return;}

        foreach(var item in _items)item.IsSelected=names.Contains(item.Name);
        foreach(var item in targets){item.MaxFps=GetProfileFps();item.MaxSize=GetProfileSize();}

        Log($"그룹 자동 시작: {group.Name}");
        await StartItemsAsync(targets);

        var healthy=targets.Where(x=>x.Status=="실행 중"&&x.Health=="정상").ToList();
        if(healthy.Count==0)
        {
            Log("그룹 자동 시작 중단: 정상 부팅된 인스턴스가 없습니다.");
            return;
        }

        await Task.Delay(GetAutoLaunchDelay());
        var package=PackageTextBox.Text.Trim();
        if(!string.IsNullOrWhiteSpace(package)&&package!="com.example.game")
            await LaunchGameAsync(package);
    }

    private async Task CheckHealthAsync()
    {
        var running=_items.Where(x=>x.Status=="실행 중"&&x.DeviceSerial.StartsWith("emulator-")).ToList();
        if(running.Count==0)
        {
            HealthSummaryText.Text="실행 인스턴스 없음";
            return;
        }

        var checks=await Task.WhenAll(running.Select(async item=>
        {
            try{return (Item:item,Healthy:await _sdk.IsDeviceHealthyAsync(item.DeviceSerial));}
            catch{return (Item:item,Healthy:false);}
        }));

        foreach(var check in checks)check.Item.Health=check.Healthy?"정상":"주의";
        var ok=checks.Count(x=>x.Healthy);
        HealthSummaryText.Text=$"정상 {ok}/{checks.Length}";
    }

    private int GetRetryCount()=>int.TryParse(RetryCountTextBox.Text,out var value)?Math.Clamp(value,0,5):2;

    private async Task<List<AvdItem>> EnsureSelectedRunningAsync()
    {
        var selected=_items.Where(x=>x.IsSelected).ToList();
        if(selected.Count==0)
        {
            MessageBox.Show("대상 인스턴스를 하나 이상 선택하세요.");
            return new();
        }

        if(selected.Any(x=>x.Status!="실행 중"))
            await StartItemsAsync(selected);

        return selected.Where(x=>x.Status=="실행 중"&&x.DeviceSerial.StartsWith("emulator-")).ToList();
    }

    private async Task<List<AvdItem>> UpdateGoogleAccountStatusesAsync(List<AvdItem> targets)
    {
        if(targets.Count==0)return new();

        var results=await Task.WhenAll(targets.Select(async item=>
        {
            try
            {
                var healthy=await _sdk.IsDeviceHealthyAsync(item.DeviceSerial);
                if(!healthy)
                {
                    item.Health="주의";
                    item.GoogleAccountStatus="ADB 오류";
                    return (Item:item,Ready:false);
                }

                item.Health="정상";
                var ready=await _sdk.HasGoogleAccountAsync(item.DeviceSerial);
                return (Item:item,Ready:ready);
            }
            catch(Exception ex)
            {
                item.Health="주의";
                item.GoogleAccountStatus="확인 실패";
                Log($"Google 계정 상태 확인 실패: {item.Name} - {ex.Message}");
                return (Item:item,Ready:false);
            }
        }));

        foreach(var result in results)
        {
            if(result.Item.GoogleAccountStatus is "ADB 오류" or "확인 실패") continue;
            result.Item.GoogleAccountStatus=result.Ready?"로그인됨":"미로그인";
        }

        var readyItems=results.Where(x=>x.Ready).Select(x=>x.Item).ToList();
        PlayStoreStatusText.Text=$"Google 계정 로그인: {readyItems.Count}/{targets.Count}";
        return readyItems;
    }

    private async void CheckGoogleAccountsButton_Click(object s,RoutedEventArgs e)
    {
        var targets=await EnsureSelectedRunningAsync();
        if(targets.Count==0)return;

        var ready=await UpdateGoogleAccountStatusesAsync(targets);
        Log($"Google 계정 상태 점검: 로그인 {ready.Count}/{targets.Count}");
    }

    private async void OpenMissingGoogleAccountsButton_Click(object s,RoutedEventArgs e)
    {
        var targets=await EnsureSelectedRunningAsync();
        if(targets.Count==0)return;

        var ready=await UpdateGoogleAccountStatusesAsync(targets);
        var readyNames=ready.Select(x=>x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing=targets.Where(x=>!readyNames.Contains(x.Name)).ToList();

        if(missing.Count==0)
        {
            PlayStoreStatusText.Text="선택된 모든 인스턴스에 Google 계정이 로그인되어 있습니다.";
            return;
        }

        foreach(var item in missing)
        {
            try
            {
                await _sdk.OpenGoogleAccountSettingsAsync(item.DeviceSerial);
                Log($"미로그인 Google 계정 설정 열기: {item.Name} ({item.AccountAlias})");
            }
            catch(Exception ex){Log(ex.Message);}
        }

        PlayStoreStatusText.Text=$"미로그인 {missing.Count}개 인스턴스의 계정 추가 화면을 열었습니다.";
    }

    private void SaveAccountAliasButton_Click(object s,RoutedEventArgs e)
    {
        var selected=_items.Where(x=>x.IsSelected).ToList();
        if(selected.Count!=1)
        {
            MessageBox.Show("계정 별칭을 저장할 인스턴스 하나만 선택하세요.");
            return;
        }

        var alias=AccountAliasTextBox.Text.Trim();
        if(string.IsNullOrWhiteSpace(alias)){MessageBox.Show("계정 별칭을 입력하세요.");return;}

        _accountAliases[selected[0].Name]=alias;
        selected[0].AccountAlias=alias;
        InstanceAccountService.Save(_accountAliases);
        Log($"계정 별칭 저장: {selected[0].Name} → {alias}");
    }

    private async void OpenGoogleAccountButton_Click(object s,RoutedEventArgs e)
    {
        var targets=await EnsureSelectedRunningAsync();
        if(targets.Count==0)return;

        foreach(var item in targets)
        {
            try
            {
                await _sdk.OpenGoogleAccountSettingsAsync(item.DeviceSerial);
                Log($"Google 계정 설정 열기: {item.Name} ({item.AccountAlias})");
            }
            catch(Exception ex){Log(ex.Message);}
        }

        PlayStoreStatusText.Text="각 인스턴스 화면에서 서로 다른 Google 계정으로 로그인하세요.";
    }

    private async void SearchPlayStoreButton_Click(object s,RoutedEventArgs e)
    {
        var targets=await EnsureSelectedRunningAsync();
        if(targets.Count==0)return;

        var query=PlayStoreSearchTextBox.Text.Trim();
        if(string.IsNullOrWhiteSpace(query)||query=="게임 이름")
        {
            MessageBox.Show("검색할 게임 이름을 입력하세요.");
            return;
        }

        var ready=await UpdateGoogleAccountStatusesAsync(targets);
        if(ready.Count==0)
        {
            PlayStoreStatusText.Text="Google 계정 로그인이 완료된 인스턴스가 없습니다.";
            return;
        }

        _playStoreSnapshots.Clear();
        foreach(var item in ready)
        {
            try
            {
                var before=await _sdk.GetInstalledUserPackagesAsync(item.DeviceSerial);
                _playStoreSnapshots[item.DeviceSerial]=before.ToHashSet(StringComparer.OrdinalIgnoreCase);
                await _sdk.OpenPlayStoreSearchAsync(item.DeviceSerial,query);
                Log($"Play 스토어 검색: {item.Name} / {item.AccountAlias} / {query}");
            }
            catch(Exception ex){Log(ex.Message);}
        }

        var missing=targets.Count-ready.Count;
        PlayStoreStatusText.Text=missing==0
            ? $"{ready.Count}개 인스턴스에서 '{query}' 검색 화면을 열었습니다. 각 계정에서 설치를 누르세요."
            : $"로그인된 {ready.Count}개에서 검색 완료. 미로그인 {missing}개는 로그인 후 다시 검색하세요.";
    }

    private async void OpenPlayStoreDetailsButton_Click(object s,RoutedEventArgs e)
    {
        var package=PackageTextBox.Text.Trim();
        if(!IsKnownPackage(package))
        {
            MessageBox.Show("먼저 실제 게임 패키지명을 입력하거나 설치 완료 감지로 패키지를 확인하세요.");
            return;
        }

        var targets=await EnsureSelectedRunningAsync();
        foreach(var item in targets)
        {
            try{await _sdk.OpenPlayStoreDetailsAsync(item.DeviceSerial,package);}
            catch(Exception ex){Log(ex.Message);}
        }

        PlayStoreStatusText.Text=$"{package} Play 스토어 페이지를 선택 인스턴스에 열었습니다.";
    }

    private async void WatchPlayStoreInstallButton_Click(object s,RoutedEventArgs e)
    {
        var targets=await EnsureSelectedRunningAsync();
        if(targets.Count==0)return;

        _playStoreWatchCts?.Cancel();
        _playStoreWatchCts=new CancellationTokenSource();
        var token=_playStoreWatchCts.Token;

        foreach(var item in targets)
        {
            if(!_playStoreSnapshots.ContainsKey(item.DeviceSerial))
            {
                var baseline=await _sdk.GetInstalledUserPackagesAsync(item.DeviceSerial);
                _playStoreSnapshots[item.DeviceSerial]=baseline.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }

        PlayStoreStatusText.Text="Play 스토어 설치 완료를 감지하는 중...";
        Log("Play 스토어 설치 감지 시작");

        try
        {
            var package=IsKnownPackage(PackageTextBox.Text.Trim())?PackageTextBox.Text.Trim():"";

            for(var attempt=0;attempt<150&&!token.IsCancellationRequested;attempt++)
            {
                if(string.IsNullOrWhiteSpace(package))
                {
                    var first=targets[0];
                    var current=await _sdk.GetInstalledUserPackagesAsync(first.DeviceSerial);
                    var baseline=_playStoreSnapshots[first.DeviceSerial];
                    package=current.FirstOrDefault(x=>!baseline.Contains(x))??"";
                    if(!string.IsNullOrWhiteSpace(package))
                    {
                        PackageTextBox.Text=package;
                        Log($"새 게임 패키지 감지: {package}");
                    }
                }

                if(!string.IsNullOrWhiteSpace(package))
                {
                    var installed=await Task.WhenAll(targets.Select(x=>_sdk.IsPackageInstalledAsync(x.DeviceSerial,package)));
                    var count=installed.Count(x=>x);
                    PlayStoreStatusText.Text=$"설치 상태: {count}/{targets.Count} ({package})";

                    if(count==targets.Count)
                    {
                        PlayStoreStatusText.Text=$"설치 완료: {package} / {targets.Count}개 인스턴스";
                        Log($"동일 게임 설치 완료: {package} / {targets.Count}개");
                        if(AutoPlayAfterInstallCheckBox.IsChecked==true)
                            await LaunchGameAsync(package);
                        return;
                    }
                }

                await Task.Delay(2000,token);
            }

            if(!token.IsCancellationRequested)
                PlayStoreStatusText.Text="설치 감지 제한시간(5분)을 초과했습니다.";
        }
        catch(OperationCanceledException)
        {
            PlayStoreStatusText.Text="설치 감지가 취소되었습니다.";
        }
        catch(Exception ex)
        {
            PlayStoreStatusText.Text="설치 감지 오류";
            Log("Play 스토어 설치 감지 실패: "+ex.Message);
        }
    }

    private async void PlayInstalledGameButton_Click(object s,RoutedEventArgs e)
    {
        var package=PackageTextBox.Text.Trim();
        if(!IsKnownPackage(package))
        {
            MessageBox.Show("플레이할 게임 패키지명이 확인되지 않았습니다.");
            return;
        }

        await LaunchGameAsync(package);
    }

    private static bool IsKnownPackage(string package)=>
        !string.IsNullOrWhiteSpace(package)&&
        package!="com.example.game"&&
        package.Contains('.')&&
        !package.Contains(' ');

    private void LayoutComboBox_SelectionChanged(object s,SelectionChangedEventArgs e){ApplyLayout();SaveSettings();}
    private void Window_SizeChanged(object s,SizeChangedEventArgs e){if(IsLoaded)ApplyLayout();}
    private void ApplyLayout()
    {
        if(CardItems is null||LayoutComboBox is null)return;

        var mode=(LayoutComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()??"자동";
        var available=Math.Max(620,ActualWidth-360);
        var columns=mode switch
        {
            "2×2"=>2,
            "3×2"=>3,
            _=>available>=980?3:2
        };

        var cardWidth=columns>=3?292d:304d;
        CardItems.Tag=cardWidth;
        CardItems.Width=(cardWidth+14)*columns;
        CardItems.HorizontalAlignment=HorizontalAlignment.Left;
    }
    private void ApplySavedLayoutSelection(){var wanted=_settings.LayoutMode;foreach(var i in LayoutComboBox.Items.OfType<ComboBoxItem>())if((i.Content?.ToString()??"")==wanted){LayoutComboBox.SelectedItem=i;break;}}
    private void SaveSettings(){try{SettingsService.Save(new AppSettings{PackageName=PackageTextBox.Text.Trim(),LayoutMode=(LayoutComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()??"자동",SelectedAvds=_items.Where(x=>x.IsSelected).Select(x=>x.Name).ToList(),CpuCores=GetCpu(),MemoryMb=GetMemory(),SyncClick=SyncClickCheckBox.IsChecked==true,RunAtStartup=RunAtStartupCheckBox.IsChecked==true,StartRetryCount=GetRetryCount()});}catch(Exception ex){AppMaintenanceService.AppendLog("설정 저장 실패: "+ex.Message);}}


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