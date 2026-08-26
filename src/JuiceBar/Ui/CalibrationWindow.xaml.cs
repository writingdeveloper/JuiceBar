using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using JuiceBar.Core;

namespace JuiceBar.Ui;

/// <summary>
/// 전력계 실측값을 받아 베이스라인과 파워서플라이 효율을 역산하는 마법사.
/// 노트북은 배터리 방전 중에 스스로 학습하므로 이 창을 쓸 일이 없다.
/// </summary>
public partial class CalibrationWindow : Window
{
    private readonly MeteringService _metering;
    private readonly ObservableCollection<string> _points = [];

    public CalibrationWindow(MeteringService metering)
    {
        _metering = metering;
        InitializeComponent();

        PointList.ItemsSource = _points;

        SourceInitialized += (_, _) => WindowEffects.Apply(this, ThemeManager.Current);
        Loaded += (_, _) => ActualInput.Focus();

        _metering.Updated += OnMeteringUpdated;
        Closed += (_, _) => _metering.Updated -= OnMeteringUpdated;

        RefreshPoints();
        RefreshResult();
    }

    private void OnMeteringUpdated(object? sender, MeteringSnapshot snapshot)
        => Dispatcher.BeginInvoke(() =>
        {
            MeasuredValue.Text = $"{CurrencyFormatter.FormatWatts(snapshot.Power.MeasuredWatts)} W";
            EstimatedValue.Text = $"{CurrencyFormatter.FormatWatts(snapshot.Power.WallWatts)} W";
        });

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnAddPoint(sender, new RoutedEventArgs());
    }

    private void OnAddPoint(object sender, RoutedEventArgs e)
    {
        string text = ActualInput.Text.Trim();

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double watts)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out watts))
        {
            ShowResult("숫자를 입력해 주세요. 예: 385", isError: true);
            return;
        }

        if (watts is <= 0 or > 5000)
        {
            ShowResult("0에서 5000W 사이의 값이어야 합니다.", isError: true);
            return;
        }

        var result = _metering.AddCalibrationPoint(watts);

        ActualInput.Clear();
        ActualInput.Focus();

        RefreshPoints();

        if (result.Success)
            RefreshResult();
        else
            ShowResult(result.Error ?? "보정에 실패했습니다.", isError: true);
    }

    private void RefreshPoints()
    {
        _points.Clear();

        foreach (var point in _metering.Profile.CalibrationPoints)
        {
            _points.Add(
                $"센서 {CurrencyFormatter.FormatWatts(point.MeasuredWatts)} W "
                + $"→ 실측 {CurrencyFormatter.FormatWatts(point.ActualWallWatts)} W");
        }

        if (_points.Count == 1)
            _points.Add("(지점이 하나 더 필요합니다 — 부하를 준 상태에서 측정해 주세요)");
    }

    private void RefreshResult()
    {
        var calibration = _metering.Profile.Calibration;

        if (!calibration.IsCalibrated)
        {
            ResultCard.Visibility = Visibility.Collapsed;
            return;
        }

        ShowResult(
            $"보정 완료 — 측정 불가 부품 {calibration.BaselineWatts:N1} W, "
            + $"파워서플라이 효율 {calibration.Efficiency:P1}. "
            + "이제부터 이 값으로 콘센트 전력을 계산합니다.",
            isError: false);
    }

    private void ShowResult(string message, bool isError)
    {
        ResultText.Text = message;
        ResultText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            isError ? "WarningTextBrush" : "TextSecondaryBrush");

        ResultCard.Visibility = Visibility.Visible;
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _metering.ResetCalibration();
        RefreshPoints();

        ResultCard.Visibility = Visibility.Collapsed;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
