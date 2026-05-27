using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ANEVRED;

public partial class UiDimmingOverlayWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExNoactivate = 0x08000000;

    public UiDimmingOverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => MakeClickThrough();
    }

    public void Apply(int red, int green, int blue, double opacityPercent, System.Drawing.Rectangle targetBounds)
    {
        red = Math.Clamp(red, 0, 255);
        green = Math.Clamp(green, 0, 255);
        blue = Math.Clamp(blue, 0, 255);
        opacityPercent = Math.Clamp(opacityPercent, 0, 80);
        var colorScale = 1 - opacityPercent / 100d;

        PositionOverBounds(targetBounds);
        OverlayFill.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(
            (byte)Math.Round(opacityPercent / 100d * 255),
            ScaleColor(red, colorScale),
            ScaleColor(green, colorScale),
            ScaleColor(blue, colorScale)));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        MakeClickThrough();
    }

    private void PositionOverBounds(System.Drawing.Rectangle bounds)
    {
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private void MakeClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExstyle);
        _ = SetWindowLong(handle, GwlExstyle, style | WsExTransparent | WsExToolwindow | WsExNoactivate);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private static byte ScaleColor(int value, double scale)
    {
        return (byte)Math.Clamp(Math.Round(value * scale), 0, 255);
    }
}
