using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Shapes;

namespace ANEVRED;

public partial class RegionSelectionWindow : Window
{
    private System.Windows.Point _start;
    private bool _isDragging;

    public Rect? SelectedRegion { get; private set; }

    public RegionSelectionWindow()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }


    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Keep the game/previous window active while the selection overlay is shown.
        // Without WS_EX_NOACTIVATE Windows may activate this WPF window; when it closes,
        // the ANEVRED process can be brought back to the foreground.
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, extendedStyle | WsExNoActivate);
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseWithResult(false);
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CleanupCursorAndCapture();
        base.OnClosed(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            CompleteSelection(e.GetPosition(Surface));
            e.Handled = true;
            return;
        }

        base.OnMouseLeftButtonUp(e);
    }

    private void SurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Surface);
        _isDragging = true;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Cross;
        Cursor = System.Windows.Input.Cursors.Cross;
        SelectionRectangle.Visibility = Visibility.Visible;
        System.Windows.Controls.Canvas.SetLeft(SelectionRectangle, _start.X);
        System.Windows.Controls.Canvas.SetTop(SelectionRectangle, _start.Y);
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void SurfaceMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var current = e.GetPosition(Surface);
        var left = Math.Min(_start.X, current.X);
        var top = Math.Min(_start.Y, current.Y);
        SelectionRectangle.Width = Math.Abs(current.X - _start.X);
        SelectionRectangle.Height = Math.Abs(current.Y - _start.Y);
        System.Windows.Controls.Canvas.SetLeft(SelectionRectangle, left);
        System.Windows.Controls.Canvas.SetTop(SelectionRectangle, top);
    }

    private void SurfaceMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        CompleteSelection(e.GetPosition(Surface));
        e.Handled = true;
    }

    private void CompleteSelection(System.Windows.Point current)
    {
        var left = Math.Min(_start.X, current.X) + Left;
        var top = Math.Min(_start.Y, current.Y) + Top;
        var width = Math.Abs(current.X - _start.X);
        var height = Math.Abs(current.Y - _start.Y);
        if (width < 30 || height < 30)
        {
            CloseWithResult(false);
            return;
        }

        SelectedRegion = new Rect(left, top, width, height);
        CloseWithResult(true);
    }

    private void SurfaceMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        CloseWithResult(false);
    }

    private void CloseWithResult(bool result)
    {
        CleanupCursorAndCapture();
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private void CleanupCursorAndCapture()
    {
        _isDragging = false;
        if (Surface.IsMouseCaptured)
        {
            Surface.ReleaseMouseCapture();
        }

        SelectionRectangle.Visibility = Visibility.Collapsed;
        Cursor = System.Windows.Input.Cursors.Arrow;
        Mouse.OverrideCursor = null;
    }

    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
