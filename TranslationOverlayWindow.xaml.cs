using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace ZestResourceOptimizer;

public partial class TranslationOverlayWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExNoactivate = 0x08000000;

    public TranslationOverlayWindow()
    {
        InitializeComponent();
        RegionFrame.Opacity = 0;
        SourceInitialized += (_, _) => MakeClickThrough();
    }

    public void SetRegion(Rect region)
    {
        var selectedScreen = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(
            (int)Math.Round(region.Left),
            (int)Math.Round(region.Top),
            Math.Max(1, (int)Math.Round(region.Width)),
            Math.Max(1, (int)Math.Round(region.Height))));
        var screenLeft = selectedScreen.Bounds.Left;
        var screenTop = selectedScreen.Bounds.Top;
        var screenWidth = selectedScreen.Bounds.Width;
        var screenHeight = selectedScreen.Bounds.Height;
        Left = screenLeft;
        Top = screenTop;
        Width = screenWidth;
        Height = screenHeight;

        var frameLeft = Math.Max(screenLeft, Math.Min(region.Left, screenLeft + screenWidth - 120));
        var frameTop = Math.Max(screenTop, Math.Min(region.Top, screenTop + screenHeight - 80));
        var frameWidth = Math.Min(Math.Max(120, region.Width), screenLeft + screenWidth - frameLeft);
        var frameHeight = Math.Min(Math.Max(80, region.Height), screenTop + screenHeight - frameTop);
        var localLeft = frameLeft - screenLeft;
        var localTop = frameTop - screenTop;
        RegionFrame.Width = frameWidth;
        RegionFrame.Height = frameHeight;
        System.Windows.Controls.Canvas.SetLeft(RegionFrame, localLeft);
        System.Windows.Controls.Canvas.SetTop(RegionFrame, localTop);

        TextPanel.Width = frameWidth;
        TextPanel.Height = frameHeight;
        System.Windows.Controls.Canvas.SetLeft(TextPanel, localLeft);
        System.Windows.Controls.Canvas.SetTop(TextPanel, localTop);
    }

    public void SetText(string translatedText, string status)
    {
        TranslationText.Text = string.IsNullOrWhiteSpace(translatedText) ? string.Empty : translatedText;
    }

    public void SetCaptureMode(bool isCapturing)
    {
        RegionFrame.Opacity = isCapturing ? 1 : 0;
        TextPanel.Opacity = isCapturing ? 0 : 1;
    }

    private void MakeClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExstyle);
        _ = SetWindowLong(handle, GwlExstyle, style | WsExTransparent | WsExToolwindow | WsExNoactivate);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
}
