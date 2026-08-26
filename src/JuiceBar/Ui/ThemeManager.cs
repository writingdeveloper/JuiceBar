using System.Windows;

namespace JuiceBar.Ui;

/// <summary>
/// 팔레트 사전을 통째로 갈아 끼워 밝게/어둡게를 전환한다.
/// 브러시를 DynamicResource 로 참조하고 있어 창을 다시 열 필요 없이 즉시 반영된다.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri DarkUri = new("Ui/Themes/Dark.xaml", UriKind.Relative);
    private static readonly Uri LightUri = new("Ui/Themes/Light.xaml", UriKind.Relative);

    private static ResourceDictionary? _palette;

    public static ThemeVariant Current { get; private set; } = ThemeVariant.Dark;

    public static void Apply(ThemeVariant variant)
    {
        var merged = Application.Current.Resources.MergedDictionaries;

        var replacement = new ResourceDictionary
        {
            Source = variant == ThemeVariant.Dark ? DarkUri : LightUri,
        };

        if (_palette is not null)
            merged.Remove(_palette);

        // 팔레트가 스타일보다 먼저 와야 스타일 쪽 DynamicResource 가 이 값을 집는다.
        merged.Insert(0, replacement);

        _palette = replacement;
        Current = variant;
    }

    public static void FollowSystem() => Apply(SystemTheme.App);
}
