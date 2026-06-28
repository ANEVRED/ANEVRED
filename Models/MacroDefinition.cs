using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ANEVRED.Models;

public sealed class MacroDefinition : INotifyPropertyChanged
{
    private string _name = "New macro";
    private string _description = string.Empty;
    private bool _enabled = true;
    private string _hotkey = string.Empty;
    private int _repeatCount = 1;
    private MacroExecutionMode _executionMode = MacroExecutionMode.Once;
    private bool _allowParallelExecution;
    private MacroRunState _runState;
    private string _lastError = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set => SetField(ref _name, string.IsNullOrWhiteSpace(value) ? "Macro" : value.Trim());
    }

    public string Description
    {
        get => _description;
        set => SetField(ref _description, value?.Trim() ?? string.Empty);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public string Hotkey
    {
        get => _hotkey;
        set
        {
            if (SetField(ref _hotkey, value?.Trim() ?? string.Empty))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasHotkey)));
            }
        }
    }

    public int RepeatCount
    {
        get => _repeatCount;
        set => SetField(ref _repeatCount, Math.Clamp(value, 1, 100));
    }

    public MacroExecutionMode ExecutionMode
    {
        get => _executionMode;
        set
        {
            if (SetField(ref _executionMode, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsesRepeatCount)));
            }
        }
    }

    public bool AllowParallelExecution
    {
        get => _allowParallelExecution;
        set => SetField(ref _allowParallelExecution, value);
    }

    public ObservableCollection<MacroStep> Steps { get; set; } = [];
    public bool HasHotkey => !string.IsNullOrWhiteSpace(Hotkey);
    public bool UsesRepeatCount => ExecutionMode == MacroExecutionMode.Repeat;

    [JsonIgnore]
    public MacroRunState RunState
    {
        get => _runState;
        set
        {
            if (SetField(ref _runState, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
            }
        }
    }

    [JsonIgnore]
    public string LastError
    {
        get => _lastError;
        set
        {
            if (SetField(ref _lastError, value ?? string.Empty))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
            }
        }
    }

    [JsonIgnore]
    public bool IsRunning => RunState is MacroRunState.Running or MacroRunState.Stopping;

    [JsonIgnore]
    public bool HasError => RunState == MacroRunState.Error && !string.IsNullOrWhiteSpace(LastError);

    public MacroDefinition Clone()
    {
        var clone = new MacroDefinition
        {
            Name = Name,
            Description = Description,
            Enabled = Enabled,
            Hotkey = string.Empty,
            RepeatCount = RepeatCount,
            ExecutionMode = ExecutionMode,
            AllowParallelExecution = AllowParallelExecution
        };
        foreach (var step in Steps)
        {
            clone.Steps.Add(step.Clone());
        }

        return clone;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
