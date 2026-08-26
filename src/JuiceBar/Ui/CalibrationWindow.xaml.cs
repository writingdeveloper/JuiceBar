using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using JuiceBar.Core;
using JuiceBar.Core.Localization;

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
            ShowResult(Loc.T("calib.notNumber"), isError: true);
            return;
        }

        if (watts is <= 0 or > 5000)
        {
            ShowResult(Loc.T("calib.outOfRange"), isError: true);
            return;
        }

        var result = _metering.AddCalibrationPoint(watts);

        ActualInput.Clear();
        ActualInput.Focus();

        RefreshPoints();

        if (result.Success)
            RefreshResult();
        else
            ShowResult(result.Error ?? Loc.T("calib.error.needTwo"), isError: true);
    }

    private void RefreshPoints()
    {
        _points.Clear();

        foreach (var point in _metering.Profile.CalibrationPoints)
        {
            _points.Add(Loc.T("calib.point",
                CurrencyFormatter.FormatWatts(point.MeasuredWatts),
                CurrencyFormatter.FormatWatts(point.ActualWallWatts)));
        }

        if (_points.Count == 1)
            _points.Add(Loc.T("calib.needSecond"));
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
            Loc.T("calib.done",
                calibration.BaselineWatts.ToString("N1"),
                calibration.Efficiency.ToString("P1")),
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
