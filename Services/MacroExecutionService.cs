using System.Runtime.InteropServices;
using ANEVRED.Models;

namespace ANEVRED.Services;

public sealed record MacroExecutionChangedEventArgs(
    Guid MacroId,
    MacroRunState State,
    string? Error = null);

public sealed class MacroExecutionService : IDisposable
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventXDown = 0x0080;
    private const uint MouseEventXUp = 0x0100;
    private const uint MouseEventWheel = 0x0800;
    private const int XButton1 = 0x0001;
    private const int XButton2 = 0x0002;

    private readonly LocalizationService _localizer;
    private readonly Action<string, string> _log;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, List<CancellationTokenSource>> _runs = [];
    private bool _disposed;

    public MacroExecutionService(LocalizationService localizer, Action<string, string> log)
    {
        _localizer = localizer;
        _log = log;
    }

    public event EventHandler<MacroExecutionChangedEventArgs>? ExecutionChanged;
    public event Action<Guid, MacroStep>? StepExecuted;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _runs.Values.Any(items => items.Count > 0);
            }
        }
    }

    public bool IsRunningMacro(Guid macroId)
    {
        lock (_sync)
        {
            return _runs.TryGetValue(macroId, out var runs) && runs.Count > 0;
        }
    }

    public Task<bool> StartAsync(MacroDefinition macro)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!macro.Enabled || macro.Steps.Count == 0)
        {
            return Task.FromResult(false);
        }

        var cancellation = new CancellationTokenSource();
        lock (_sync)
        {
            if (!macro.AllowParallelExecution && _runs.TryGetValue(macro.Id, out var existing) && existing.Count > 0)
            {
                cancellation.Dispose();
                return Task.FromResult(false);
            }

            if (!_runs.TryGetValue(macro.Id, out var runs))
            {
                runs = [];
                _runs[macro.Id] = runs;
            }

            runs.Add(cancellation);
        }

        _ = ExecuteRunAsync(macro, cancellation);
        return Task.FromResult(true);
    }

    public void Stop(Guid macroId)
    {
        List<CancellationTokenSource> runs;
        lock (_sync)
        {
            if (!_runs.TryGetValue(macroId, out var existing))
            {
                return;
            }

            runs = [.. existing];
        }

        ExecutionChanged?.Invoke(this, new(macroId, MacroRunState.Stopping));
        foreach (var run in runs)
        {
            run.Cancel();
        }
    }

    public void StopAll()
    {
        List<CancellationTokenSource> runs;
        lock (_sync)
        {
            runs = _runs.Values.SelectMany(items => items).ToList();
        }

        foreach (var run in runs)
        {
            run.Cancel();
        }
    }

    private async Task ExecuteRunAsync(MacroDefinition macro, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            ExecutionChanged?.Invoke(this, new(macro.Id, MacroRunState.Running));
            var cycles = macro.ExecutionMode switch
            {
                MacroExecutionMode.Repeat => Math.Clamp(macro.RepeatCount, 1, 10000),
                MacroExecutionMode.Toggle or MacroExecutionMode.Hold => int.MaxValue,
                _ => 1
            };

            for (var cycle = 0; cycle < cycles; cycle++)
            {
                foreach (var step in macro.Steps.Take(500))
                {
                    token.ThrowIfCancellationRequested();
                    await ExecuteStepAsync(step, token);
                    StepExecuted?.Invoke(macro.Id, step);
                }
            }

            ExecutionChanged?.Invoke(this, new(macro.Id, MacroRunState.Stopped));
        }
        catch (OperationCanceledException)
        {
            ExecutionChanged?.Invoke(this, new(macro.Id, MacroRunState.Stopped));
        }
        catch (Exception ex)
        {
            _log("Warn", _localizer.Format("LogMacroFailed", macro.Name, ex.Message));
            ExecutionChanged?.Invoke(this, new(macro.Id, MacroRunState.Error, ex.Message));
        }
        finally
        {
            lock (_sync)
            {
                if (_runs.TryGetValue(macro.Id, out var runs))
                {
                    runs.Remove(cancellation);
                    if (runs.Count == 0)
                    {
                        _runs.Remove(macro.Id);
                    }
                }
            }

            cancellation.Dispose();
        }
    }

    private static async Task ExecuteStepAsync(MacroStep step, CancellationToken token)
    {
        switch (step.Type)
        {
            case MacroStepType.Delay:
                await Task.Delay(Math.Clamp(step.DelayMs, 10, 60000), token);
                return;
            case MacroStepType.MouseClick:
                ClickMouse(step.MouseButton);
                break;
            case MacroStepType.MouseMove:
                SendMouse(step.MouseX, step.MouseY, 0, MouseEventMove);
                break;
            case MacroStepType.MouseWheel:
                SendMouse(0, 0, step.WheelDelta, MouseEventWheel);
                break;
            default:
                PressCombination(step.Key);
                break;
        }

        await Task.Delay(15, token);
    }

    private static void PressCombination(string combination)
    {
        if (!HotkeyParser.TryParse(combination, out var parsed) || parsed.IsMouse)
        {
            throw new InvalidOperationException($"Invalid key combination: {combination}");
        }

        var keys = new List<ushort>();
        if ((parsed.Modifiers & HotkeyParser.ModControl) != 0) keys.Add(0x11);
        if ((parsed.Modifiers & HotkeyParser.ModAlt) != 0) keys.Add(0x12);
        if ((parsed.Modifiers & HotkeyParser.ModShift) != 0) keys.Add(0x10);
        if ((parsed.Modifiers & HotkeyParser.ModWin) != 0) keys.Add(0x5B);
        keys.Add((ushort)parsed.VirtualKey);

        foreach (var key in keys) SendKey(key, false);
        for (var index = keys.Count - 1; index >= 0; index--) SendKey(keys[index], true);
    }

    private static void ClickMouse(string button)
    {
        var (down, up, data) = button.ToUpperInvariant() switch
        {
            "RIGHT" => (MouseEventRightDown, MouseEventRightUp, 0),
            "MIDDLE" => (MouseEventMiddleDown, MouseEventMiddleUp, 0),
            "X1" => (MouseEventXDown, MouseEventXUp, XButton1),
            "X2" => (MouseEventXDown, MouseEventXUp, XButton2),
            _ => (MouseEventLeftDown, MouseEventLeftUp, 0)
        };
        SendMouse(0, 0, data, down);
        SendMouse(0, 0, data, up);
    }

    private static void SendKey(ushort virtualKey, bool keyUp)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = keyUp ? KeyEventKeyUp : 0 }
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            throw new InvalidOperationException($"SendInput failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    private static void SendMouse(int x, int y, int data, uint flags)
    {
        var input = new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput { X = x, Y = y, MouseData = data, Flags = flags }
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            throw new InvalidOperationException($"SendInput failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAll();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public int MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);
}
