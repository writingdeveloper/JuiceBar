using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace JuiceBar.Ui;

/// <summary>
/// Windows 11 의 창 효과 — 둥근 모서리와 Mica 배경.
///
/// 둘 다 DWM 속성으로만 켤 수 있고 WPF 에는 대응하는 API가 없다.
/// Windows 10 에서는 조용히 무시되므로 별도 분기 없이 그냥 호출하면 된다.
/// </summary>
public static class WindowEffects
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_MAINWINDOW = 2;   // Mica

    /// <summary>
    /// 창에 둥근 모서리와 Mica 를 입힌다.
    /// Mica 가 실제로 적용되면 창 배경이 비쳐야 하므로 배경을 투명으로 바꾼다.
    /// 적용에 실패한 환경에서는 불투명 배경을 유지해 글자가 뭉개지지 않게 한다.
    /// </summary>
    public static void Apply(Window window, ThemeVariant variant)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;

        int darkMode = variant == ThemeVariant.Dark ? 1 : 0;
        DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        int backdrop = DWMSBT_MAINWINDOW;
        bool micaApplied = DwmSetWindowAttribute(
            handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;

        if (micaApplied && GetHwndSource(window) is { } source)
        {
            // Mica 는 창 뒤편을 흐려 보여 주는 효과라, WPF 쪽 배경이 불투명하면 가려진다.
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            window.Background = Brushes.Transparent;
        }
        else
        {
            window.SetResourceReference(Window.BackgroundProperty, "SurfaceFallbackBrush");
        }
    }

    private static HwndSource? GetHwndSource(Window window)
        => PresentationSource.FromVisual(window) as HwndSource;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
