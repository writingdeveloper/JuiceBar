using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace JuiceBar.Ui;

/// <summary>
/// 설정 화면의 전력 채널 한 줄.
///
/// 체크를 껐다 켤 때마다 합계를 다시 계산해야 해서 변경 알림이 필요하다.
/// </summary>
public sealed class ChannelRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string HardwareName { get; init; }
    public required double Watts { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string WattsText => $"{Watts:N1} W";

    public event PropertyChangedEventHandler? PropertyChanged;
}
