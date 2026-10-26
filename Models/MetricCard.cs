using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;

namespace ANEVRED.Models;

public sealed class MetricCard : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _value = string.Empty;
    private string _detail = string.Empty;
    private string _state = string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Title { get => _title; set => SetField(ref _title, value); }
    public string Value { get => _value; set => SetField(ref _value, value); }
    public string Detail { get => _detail; set => SetField(ref _detail, value); }
    public string State { get => _state; set => SetField(ref _state, value); }
    public MediaBrush Accent { get; init; } = MediaBrushes.DodgerBlue;
    public IReadOnlyList<double> History { get; init; } = [];

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
