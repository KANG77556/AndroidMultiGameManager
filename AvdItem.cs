using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AndroidMultiGameManager;

public sealed class AvdItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _status = "중지";
    private string _deviceSerial = "-";
    private int _maxFps = 60;
    private int _maxSize = 1080;

    public string Name { get; init; } = string.Empty;

    public bool IsSelected { get => _isSelected; set { _isSelected=value; OnPropertyChanged(); } }
    public string Status { get => _status; set { _status=value; OnPropertyChanged(); } }
    public string DeviceSerial { get => _deviceSerial; set { _deviceSerial=value; OnPropertyChanged(); } }
    public int MaxFps { get => _maxFps; set { _maxFps=Math.Clamp(value,15,240); OnPropertyChanged(); } }
    public int MaxSize { get => _maxSize; set { _maxSize=Math.Clamp(value,480,2160); OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name=null)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
}