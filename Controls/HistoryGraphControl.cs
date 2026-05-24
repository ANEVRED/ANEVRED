using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;

namespace ZestResourceOptimizer.Controls;

public sealed class HistoryGraphControl : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IEnumerable),
        typeof(HistoryGraphControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty MaxPointsProperty = DependencyProperty.Register(
        nameof(MaxPoints),
        typeof(int),
        typeof(HistoryGraphControl),
        new FrameworkPropertyMetadata(300, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(MediaBrush),
        typeof(HistoryGraphControl),
        new FrameworkPropertyMetadata(MediaBrushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(MediaBrush),
        typeof(HistoryGraphControl),
        new FrameworkPropertyMetadata(MediaBrushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    private INotifyCollectionChanged? _observedValues;

    public HistoryGraphControl()
    {
        MinHeight = 0;
        ClipToBounds = true;
    }

    public IEnumerable? Values
    {
        get => (IEnumerable?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public int MaxPoints
    {
        get => (int)GetValue(MaxPointsProperty);
        set => SetValue(MaxPointsProperty, value);
    }

    public MediaBrush Stroke
    {
        get => (MediaBrush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public MediaBrush Fill
    {
        get => (MediaBrush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRoundedRectangle(Fill, null, rect, 6, 6);
        drawingContext.PushClip(new RectangleGeometry(rect));

        var gridPen = new MediaPen(new SolidColorBrush(MediaColor.FromArgb(35, 160, 170, 190)), 1);
        for (var i = 1; i < 4; i++)
        {
            var y = ActualHeight * i / 4;
            drawingContext.DrawLine(gridPen, new WpfPoint(0, y), new WpfPoint(ActualWidth, y));
        }

        var values = Values?.Cast<object>()
            .Select(Convert.ToDouble)
            .TakeLast(Math.Max(2, MaxPoints))
            .ToList() ?? [];

        if (values.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            drawingContext.Pop();
            return;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var x = values.Count == 1 ? 0 : i * ActualWidth / (values.Count - 1);
                var y = ActualHeight - Math.Clamp(values[i], 0, 100) / 100d * ActualHeight;
                var point = new WpfPoint(x, y);
                if (i == 0)
                {
                    context.BeginFigure(point, false, false);
                }
                else
                {
                    context.LineTo(point, true, false);
                }
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, new MediaPen(Stroke, 2), geometry);
        drawingContext.Pop();
    }

    private static void OnValuesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is HistoryGraphControl graph)
        {
            graph.UpdateObservedValues(args.OldValue, args.NewValue);
        }
    }

    private void UpdateObservedValues(object? oldValue, object? newValue)
    {
        if (_observedValues is not null)
        {
            _observedValues.CollectionChanged -= ValuesCollectionChanged;
            _observedValues = null;
        }

        if (newValue is INotifyCollectionChanged observable)
        {
            _observedValues = observable;
            _observedValues.CollectionChanged += ValuesCollectionChanged;
        }

        InvalidateVisual();
    }

    private void ValuesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }
}
