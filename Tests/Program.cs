using ANEVRED.Models;
using ANEVRED.Services;
using System.IO;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Hotkey parser normalizes keyboard and mouse triggers", TestHotkeyParser),
    ("Validator detects duplicate and invalid macros", TestValidator),
    ("Macro storage persists and reloads definitions", TestStorage),
    ("Runner executes exact once/repeat counts and blocks duplicate runs", TestRunner),
    ("Runner keeps routine macro activation out of the app log", TestRunnerDoesNotLogRoutineActivation)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

if (failures.Count > 0)
{
    Environment.ExitCode = 1;
    Console.WriteLine(string.Join(Environment.NewLine, failures));
}

static Task TestHotkeyParser()
{
    Equal("Ctrl+Shift+K", HotkeyParser.Normalize("shift + strg + k"));
    True(HotkeyParser.TryParse("Alt+MouseX1", out var mouse) && mouse.IsMouse && mouse.MouseButton == "MouseX1");
    False(HotkeyParser.TryParse("Ctrl+MouseWheelDown", out _));
    True(HotkeyParser.Conflicts("Ctrl+Alt+M", "alt + control + m"));
    False(HotkeyParser.TryParse("Ctrl+Alt", out _));
    return Task.CompletedTask;
}

static Task TestValidator()
{
    var validator = new MacroValidator();
    var first = ValidMacro("First", "Ctrl+K");
    var second = ValidMacro("Second", "Ctrl+K");
    var duplicate = validator.Validate(second, [first, second]);
    False(duplicate.IsValid);
    True(duplicate.Errors.Any(error => error.Contains("bereits", StringComparison.OrdinalIgnoreCase)));

    second.Hotkey = "MouseLeft";
    True(validator.Validate(second, [first, second]).IsValid);

    second.Steps[0].Key = "Not+A+Key";
    False(validator.Validate(second, [first, second]).IsValid);
    return Task.CompletedTask;
}

static Task TestStorage()
{
    var directory = Path.Combine(Path.GetTempPath(), "ANEVRED-tests-" + Guid.NewGuid());
    try
    {
        var storage = new MacroStorage(directory);
        var source = new[] { ValidMacro("Persisted", "MouseX2") };
        storage.Save(source);
        var loaded = storage.Load();
        Equal(1, loaded.Count);
        Equal("Persisted", loaded[0].Name);
        Equal("MouseX2", loaded[0].Hotkey);
        Equal(MacroExecutionMode.Once, loaded[0].ExecutionMode);
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    return Task.CompletedTask;
}

static async Task TestRunner()
{
    using var runner = new MacroExecutionService(new LocalizationService("en"), (_, _) => { });
    var macro = ValidMacro("Runner", "Ctrl+R");
    macro.Steps.Clear();
    macro.Steps.Add(new MacroStep { Type = MacroStepType.Delay, DelayMs = 10 });
    var count = 0;
    runner.StepExecuted += (id, _) =>
    {
        if (id == macro.Id) count++;
    };

    macro.ExecutionMode = MacroExecutionMode.Once;
    await RunToCompletion(runner, macro);
    Equal(1, count);

    count = 0;
    macro.ExecutionMode = MacroExecutionMode.Repeat;
    macro.RepeatCount = 3;
    await RunToCompletion(runner, macro);
    Equal(3, count);

    macro.ExecutionMode = MacroExecutionMode.Toggle;
    True(await runner.StartAsync(macro));
    False(await runner.StartAsync(macro));
    await Task.Delay(25);
    runner.Stop(macro.Id);
    await WaitUntil(() => !runner.IsRunningMacro(macro.Id), 1000);
}

static async Task TestRunnerDoesNotLogRoutineActivation()
{
    var logs = new List<(string Level, string Message)>();
    using var runner = new MacroExecutionService(new LocalizationService("en"), (level, message) => logs.Add((level, message)));
    var macro = ValidMacro("Quiet", "Ctrl+Q");
    macro.Steps.Clear();
    macro.Steps.Add(new MacroStep { Type = MacroStepType.Delay, DelayMs = 10 });

    await RunToCompletion(runner, macro);
    Equal(0, logs.Count);
}

static async Task RunToCompletion(MacroExecutionService runner, MacroDefinition macro)
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    EventHandler<MacroExecutionChangedEventArgs>? handler = null;
    handler = (_, args) =>
    {
        if (args.MacroId == macro.Id && args.State is MacroRunState.Stopped or MacroRunState.Error)
        {
            completion.TrySetResult();
        }
    };
    runner.ExecutionChanged += handler;
    try
    {
        True(await runner.StartAsync(macro));
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
    finally
    {
        runner.ExecutionChanged -= handler;
    }
}

static async Task WaitUntil(Func<bool> condition, int timeoutMs)
{
    var started = Environment.TickCount64;
    while (!condition())
    {
        if (Environment.TickCount64 - started > timeoutMs)
        {
            throw new TimeoutException("Condition was not reached.");
        }
        await Task.Delay(10);
    }
}

static MacroDefinition ValidMacro(string name, string hotkey)
{
    var macro = new MacroDefinition { Name = name, Hotkey = hotkey };
    macro.Steps.Add(new MacroStep { Type = MacroStepType.KeyPress, Key = "A" });
    return macro;
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void False(bool value)
{
    if (value) throw new InvalidOperationException("Expected false.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
