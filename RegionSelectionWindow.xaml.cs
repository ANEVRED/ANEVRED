using System.Windows;
using System.Windows.Input;
using System.Windows.Shapes;

namespace ZestResourceOptimizer;

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

    private void SurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Surface);
        _isDragging = true;
        Cursor = System.Windows.Input.Cursors.Cross;
        SelectionRectangle.Visibility = Visibility.Visible;
        System.Windows.Controls.Canvas.SetLeft(SelectionRectangle, _start.X);
        System.Windows.Controls.Canvas.SetTop(SelectionRectangle, _start.Y);
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
        Surface.CaptureMouse();
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

        _isDragging = false;
        Surface.ReleaseMouseCapture();
        var current = e.GetPosition(Surface);
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
        DialogResult = result;
        Close();
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
}
