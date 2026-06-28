using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ANEVRED.Models;

public sealed class MacroStep : INotifyPropertyChanged
{
    private MacroStepType _type = MacroStepType.KeyPress;
    private string _key = string.Empty;
    private int _delayMs = 100;
    private int _mouseX;
    private int _mouseY;
    private int _wheelDelta = 120;
    private string _mouseButton = "Left";

    public event PropertyChangedEventHandler? PropertyChanged;

    public MacroStepType Type
    {
        get => _type;
        set
        {
            if (!SetField(ref _type, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsKeyPress));
            OnPropertyChanged(nameof(IsDelay));
            OnPropertyChanged(nameof(IsMouseClick));
            OnPropertyChanged(nameof(IsMouseMove));
            OnPropertyChanged(nameof(IsMouseWheel));
            OnPropertyChanged(nameof(IsWheelUp));
            OnPropertyChanged(nameof(IsWheelDown));
        }
    }

    public string Key
    {
        get => _key;
        set
        {
            if (SetField(ref _key, value?.Trim() ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasKey));
            }
        }
    }

    public int DelayMs
    {
        get => _delayMs;
        set => SetField(ref _delayMs, Math.Clamp(value, 10, 60000));
    }

    public int MouseX
    {
        get => _mouseX;
        set => SetField(ref _mouseX, Math.Clamp(value, -10000, 10000));
    }

    public int MouseY
    {
        get => _mouseY;
        set => SetField(ref _mouseY, Math.Clamp(value, -10000, 10000));
    }

    public int WheelDelta
    {
        get => _wheelDelta;
        set
        {
            var normalized = Math.Clamp(value, -12000, 12000);
            if (normalized == 0)
            {
                normalized = 120;
            }

            if (SetField(ref _wheelDelta, normalized))
            {
                OnPropertyChanged(nameof(WheelDirection));
                OnPropertyChanged(nameof(WheelSteps));
                OnPropertyChanged(nameof(IsWheelUp));
                OnPropertyChanged(nameof(IsWheelDown));
            }
        }
    }

    [JsonIgnore]
    public string WheelDirection
    {
        get => WheelDelta < 0 ? "Down" : "Up";
        set
        {
            var direction = value?.Equals("Down", StringComparison.OrdinalIgnoreCase) == true ? -1 : 1;
            WheelDelta = direction * Math.Max(1, WheelSteps) * 120;
        }
    }

    [JsonIgnore]
    public int WheelSteps
    {
        get => Math.Clamp(Math.Abs(WheelDelta) / 120, 1, 100);
        set => WheelDelta = (WheelDelta < 0 ? -1 : 1) * Math.Clamp(value, 1, 100) * 120;
    }

    public string MouseButton
    {
        get => _mouseButton;
        set => SetField(ref _mouseButton, string.IsNullOrWhiteSpace(value) ? "Left" : value.Trim());
    }

    public bool IsKeyPress => Type == MacroStepType.KeyPress;
    public bool IsDelay => Type == MacroStepType.Delay;
    public bool IsMouseClick => Type == MacroStepType.MouseClick;
    public bool IsMouseMove => Type == MacroStepType.MouseMove;
    public bool IsMouseWheel => Type == MacroStepType.MouseWheel;
    public bool IsWheelUp => IsMouseWheel && WheelDelta > 0;
    public bool IsWheelDown => IsMouseWheel && WheelDelta < 0;
    public bool HasKey => !string.IsNullOrWhiteSpace(Key);

    public MacroStep Clone()
    {
        return new MacroStep
        {
            Type = Type,
            Key = Key,
            DelayMs = DelayMs,
            MouseX = MouseX,
            MouseY = MouseY,
            WheelDelta = WheelDelta,
            MouseButton = MouseButton
        };
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
