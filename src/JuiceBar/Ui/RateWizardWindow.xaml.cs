using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using JuiceBar.Core;
using JuiceBar.Core.Tariff;
using JuiceBar.Core.Localization;

namespace JuiceBar.Ui;

/// <summary>
/// 요금 설정의 기본 경로.
///
/// 전체 요금 편집기(SettingsWindow)는 입력란이 스무 개가 넘는다. 대부분의 사용자는
/// 자기 지역 요금 구조를 정확히 모르기 때문에 그 화면을 보면 그냥 닫아 버린다.
/// 그래서 "복사 → AI에게 붙여넣기 → 답변 붙여넣기" 세 단계로 줄였다.
/// </summary>
public partial class RateWizardWindow : Window
{
    private readonly MeteringService _metering;

    private TariffConfig? _parsed;

    public event EventHandler? AdvancedRequested;

    public RateWizardWindow(MeteringService metering)
    {
        _metering = metering;
        InitializeComponent();

        SourceInitialized += (_, _) => WindowEffects.Apply(this, ThemeManager.Current);
        Loaded += (_, _) => RegionBox.Focus();

        var tariff = metering.Profile.Tariff;
        BudgetBox.Text = tariff.MonthlyBudget > 0
            ? tariff.MonthlyBudget.ToString("0.####", CultureInfo.CurrentCulture)
            : string.Empty;
        BudgetUnit.Text = tariff.Symbol;

        if (metering.Profile.IsTariffConfigured) CurrencyBox.Text = tariff.Currency;

        // 이미 설정된 요금이 있으면 그 내용을 먼저 보여 준다. 무엇이 바뀌는지 알 수 있어야 한다.
        if (metering.Profile.IsTariffConfigured)
            ShowResult(Describe(tariff), isError: false);

        RefreshPrompt();
    }

    // ─────────────────────────── ① 프롬프트 ───────────────────────────

    private void OnRegionChanged(object sender, TextChangedEventArgs e) => RefreshPrompt();

    private void RefreshPrompt() => PromptPreview.Text = CurrentPrompt();

    private string CurrentPrompt() => RatePrompt.Build(RegionBox.Text, CurrencyBox.Text);

    private void OnCopyPrompt(object sender, RoutedEventArgs e)
    {
        if (!TrySetClipboard(CurrentPrompt()))
        {
            ShowResult(Loc.T("wizard.copyFailed"), isError: true);
            return;
        }

        CopyButton.Content = Loc.T("wizard.copied");

        // 잠깐 뒤에 원래 글자로 돌려놓는다. 눌렸다는 것만 알려주면 된다.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CopyButton.Content = Loc.T("wizard.copy");
        };
        timer.Start();
    }

    /// <summary>
    /// 클립보드는 다른 프로그램이 잡고 있으면 실패한다. 그때 앱이 죽으면 안 되므로
    /// 몇 번 다시 시도하고, 그래도 안 되면 사용자에게 알린다.
    /// </summary>
    private static bool TrySetClipboard(string text)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception)
            {
                Thread.Sleep(60);
            }
        }

        return false;
    }

    private void OnOpenAi(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string url) return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }

    // ─────────────────────────── ② 답변 적용 ───────────────────────────

    private void OnPasteAndApply(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText()) ResponseBox.Text = Clipboard.GetText();
        }
        catch (Exception)
        {
            // 클립보드를 못 읽으면 사용자가 직접 붙여 넣은 내용으로 진행한다.
        }

        // Text 를 바꾸면 아래 debounce 가 걸리지만, 버튼을 눌렀을 때는 바로 보여 주는 편이 낫다.
        Parse();
    }

    /// <summary>
    /// 붙여 넣자마자 알아서 읽는다.
    ///
    /// 예전에는 "적용" 을 따로 눌러야 했는데, 붙여 넣고 바로 저장을 누르는 것이
    /// 훨씬 자연스러운 행동이라 아무 일도 일어나지 않는 것처럼 보였다.
    /// 타이핑 중에 매 글자마다 오류를 띄우지 않도록 잠깐 기다렸다가 읽는다.
    /// </summary>
    private void OnResponseChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressParse) return;

        _parseDelay ??= CreateParseTimer();

        _parseDelay.Stop();
        _parseDelay.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _parseDelay;
    private bool _suppressParse;

    private System.Windows.Threading.DispatcherTimer CreateParseTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Parse();
        };

        return timer;
    }

    /// <summary>입력란의 내용을 읽어 인식 결과나 오류를 보여 준다.</summary>
    private bool Parse()
    {
        _parseDelay?.Stop();

        if (string.IsNullOrWhiteSpace(ResponseBox.Text))
        {
            _parsed = null;
            return false;
        }

        var result = RatePrompt.TryParse(ResponseBox.Text);

        if (!result.Success)
        {
            _parsed = null;
            ShowResult(result.Error ?? Loc.T("rate.error.noJson"), isError: true);
            return false;
        }

        _parsed = result.Tariff;
        BudgetUnit.Text = _parsed!.Symbol;
        ShowResult(Describe(_parsed), isError: false);

        return true;
    }

    /// <summary>
    /// 지금 적용된 요금을 JSON 으로 꺼내 붙여넣기 칸에 넣는다.
    ///
    /// 숫자 하나만 고치고 싶은 사람을 위한 통로다. 이것 때문에 설정 화면에
    /// 구간 편집표를 다시 들여놓을 이유는 없다.
    /// </summary>
    private void OnEditCurrentJson(object sender, RoutedEventArgs e)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        // 여기서 넣은 글은 이미 유효한 값이라 다시 읽어 인식 결과를 덮어쓸 필요가 없다.
        _suppressParse = true;
        ResponseBox.Text = JsonSerializer.Serialize(_parsed ?? _metering.Profile.Tariff, options);
        _suppressParse = false;

        _parsed ??= _metering.Profile.Tariff;
        ShowResult(Loc.T("wizard.exportedHint"), isError: false);
    }

    private static string Describe(TariffConfig tariff)
        => Loc.T("wizard.recognized", TariffSummary.Describe(tariff));

    private void ShowResult(string message, bool isError)
    {
        ResultText.Text = message;
        ResultText.SetResourceReference(
            TextBlock.ForegroundProperty,
            isError ? "WarningTextBrush" : "TextSecondaryBrush");

        ResultCard.Visibility = Visibility.Visible;
    }

    // ─────────────────────────── ③ 저장 ───────────────────────────

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // 붙여 넣고 곧바로 저장을 누르는 것이 자연스럽다.
        // 아직 읽지 않은 내용이 남아 있으면 여기서 읽는다.
        if (_parsed is null && !string.IsNullOrWhiteSpace(ResponseBox.Text) && !Parse())
            return;

        // 요금을 새로 읽지 않았다면 예산만 바꾸러 온 것이다.
        var tariff = _parsed ?? _metering.Profile.Tariff;

        if (_parsed is null && !_metering.Profile.IsTariffConfigured)
        {
            ShowResult(Loc.T("wizard.needsRate"), isError: true);
            return;
        }

        if (!TryParseBudget(out double budget))
        {
            ShowResult(Loc.T("wizard.budgetNotNumber"), isError: true);
            return;
        }

        _metering.UpdateProfile(_metering.Profile with
        {
            Tariff = tariff with { MonthlyBudget = budget },
            IsTariffConfigured = true,

            // 예산을 정했다면 그걸 보고 싶어서 정한 것이다. 게이지를 요금 기준으로 맞춰 준다.
            GaugeMode = budget > 0 ? Core.Storage.GaugeMode.Budget : _metering.Profile.GaugeMode,
        });

        Close();
    }

    private bool TryParseBudget(out double budget)
    {
        string text = BudgetBox.Text.Trim();

        if (text.Length == 0)
        {
            budget = 0;
            return true;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out budget)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out budget);
    }

    private void OnOpenAdvanced(object sender, RoutedEventArgs e)
    {
        Close();
        AdvancedRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
