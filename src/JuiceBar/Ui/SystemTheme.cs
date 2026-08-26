using Microsoft.Win32;

namespace JuiceBar.Ui;

public enum ThemeVariant
{
    Light,
    Dark,
}

/// <summary>
/// Windows의 밝게/어둡게 설정을 따라간다.
///
/// 앱 창과 작업표시줄 아이콘은 서로 다른 설정을 따른다 — 앱은 어둡게인데
/// 작업표시줄은 밝게일 수 있어서, 트레이 아이콘 테두리 색은 별도로 물어봐야 한다.
/// </summary>
public static class SystemTheme
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static ThemeVariant App => Read("AppsUseLightTheme");

    public static ThemeVariant Taskbar => Read("SystemUsesLightTheme");

    /// <summary>테마가 바뀌면 발생한다. 창과 트레이 아이콘을 다시 칠하는 신호다.</summary>
    public static event EventHandler? Changed;

    private static RegistryKey? _watchedKey;

    public static void StartWatching()
    {
        // 레지스트리 변경 알림은 P/Invoke 가 필요해서, 시스템이 보내 주는
        // 사용자 환경 설정 변경 이벤트에 얹는 편이 간단하고 충분히 빠르다.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void StopWatching()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _watchedKey?.Dispose();
        _watchedKey = null;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            Changed?.Invoke(null, EventArgs.Empty);
    }

    private static ThemeVariant Read(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);

            // 값이 없는 환경(정책으로 고정된 경우 등)은 어둡게로 본다.
            return key?.GetValue(valueName) is int value && value != 0
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        }
        catch (Exception)
        {
            return ThemeVariant.Dark;
        }
    }
}
