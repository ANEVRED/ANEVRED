using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ZestResourceOptimizer.Models;

public sealed class ProtectionRule : INotifyPropertyChanged
{
    private string _pattern = string.Empty;
    private bool _enabled = true;
    private string _notes = string.Empty;

    public ProtectionRule()
    {
    }

    public ProtectionRule(string pattern, bool enabled, string notes)
    {
        _pattern = pattern;
        _enabled = enabled;
        _notes = notes;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Pattern
    {
        get => _pattern;
        set => SetField(ref _pattern, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public string Notes
    {
        get => _notes;
        set => SetField(ref _notes, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
