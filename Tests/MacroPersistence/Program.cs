using ANEVRED.Models;
using ANEVRED.Services;

var testRoot = Path.Combine(AppContext.BaseDirectory, "isolated-macros-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
MacroStorage Store(string name) => new(Path.Combine(testRoot, name));
MacroDefinition Macro(string name) => new() { Name = name, Steps = [new() { Key = "A" }] };
try
{
    var fresh = Store("fresh");
    Check(fresh.Load().Count == 0 && fresh.LoadWarning is null && !File.Exists(fresh.StoragePath), "First run must return empty without writing.");
    var legacy = Macro("legacy");
    var migrated = fresh.Load([legacy]);
    Check(migrated.Count == 1 && migrated[0].Name == "legacy" && File.Exists(fresh.StoragePath), "Legacy migration failed.");
    Check(File.Exists(fresh.BackupPath), "First save needs a backup.");
    Check(fresh.Load([Macro("ignored")]).Single().Name == "legacy", "Existing file must take precedence over legacy macros.");
    migrated[0].Name = "edited";
    fresh.Save(migrated);
    Check(fresh.Load().Single().Name == "edited", "Edited macro not retained.");
    fresh.Save([]);
    Check(fresh.Load().Count == 0 && fresh.LoadWarning is null, "Intentional empty list must remain empty.");

    var recovery = Store("recovery");
    recovery.Save([Macro("good-old")]);
    recovery.Save([Macro("good-new")]);
    var goodBackup = File.ReadAllBytes(recovery.BackupPath);
    const string damaged = "{unfinished macros";
    File.WriteAllText(recovery.StoragePath, damaged);
    Check(recovery.Load().Single().Name == "good-old" && recovery.LoadWarning is not null, "Backup recovery must be visible.");
    Check(File.ReadAllText(recovery.StoragePath) == damaged, "Recovery load must leave main untouched.");
    recovery.Save([]);
    var preserved = Directory.GetFiles(Path.GetDirectoryName(recovery.StoragePath)!, "macros.json.corrupt-*.json");
    Check(preserved.Length == 1 && File.ReadAllText(preserved[0]) == damaged, "Later empty save must preserve corrupted original bytes.");
    Check(File.ReadAllBytes(recovery.BackupPath).SequenceEqual(goodBackup), "Empty recovery save must retain good backup.");
    Check(recovery.Load().Count == 0 && recovery.LoadWarning is null, "Post-recovery empty save failed.");
    Check(Directory.GetFiles(Path.GetDirectoryName(recovery.StoragePath)!, "*.tmp").Length == 0, "Temp files remain.");

    var bad = Store("both-bad");
    Directory.CreateDirectory(Path.GetDirectoryName(bad.StoragePath)!);
    File.WriteAllText(bad.StoragePath, "null");
    File.WriteAllText(bad.BackupPath, "broken backup");
    Check(bad.Load([Macro("legacy")]).Count == 0 && bad.LoadWarning is not null, "Broken files must not silently migrate legacy values.");
    Check(File.ReadAllText(bad.StoragePath) == "null" && File.ReadAllText(bad.BackupPath) == "broken backup", "Broken-pair load changed data.");
    foreach (var invalid in new[] { "[null]", "[{\"Steps\":null}]", "[{\"Steps\":[null]}]" })
    {
        File.WriteAllText(bad.StoragePath, invalid);
        Check(bad.Load().Count == 0 && bad.LoadWarning is not null, "Invalid nested macro content needs a warning.");
    }

    recovery.Save([Macro("current")]);
    var prior = File.ReadAllBytes(recovery.StoragePath);
    using (var locked = new FileStream(recovery.StoragePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        Check(recovery.Load().Count == 0 && recovery.LoadWarning is not null, "Locked main must recover backup.");
        var failed = false;
        try { recovery.Save([]); } catch (IOException) { failed = true; }
        Check(failed, "Save against locked main must fail.");
        Check(Directory.GetFiles(Path.GetDirectoryName(recovery.StoragePath)!, "*.tmp").Length == 0, "Failed save left temp files.");
    }
    Check(File.ReadAllBytes(recovery.StoragePath).SequenceEqual(prior), "Failed save altered original.");
    File.Delete(recovery.StoragePath);
    Check(recovery.Load().Count == 0 && recovery.LoadWarning is not null, "Missing main must recover backup without legacy migration.");

    var invalidSave = false;
    try { fresh.Save([null!]); } catch (InvalidDataException) { invalidSave = true; }
    Check(invalidSave && fresh.Load().Count == 0, "Invalid save must not destroy valid settings.");
    var blockedPath = Path.Combine(testRoot, "blocked-directory");
    File.WriteAllText(blockedPath, "retained");
    var blocked = new MacroStorage(blockedPath);
    Check(blocked.Load([Macro("legacy")]).Single().Name == "legacy" && blocked.LoadWarning is not null
        && File.ReadAllText(blockedPath) == "retained", "Failed migration must return legacy with warning and preserve original file.");
    Console.WriteLine($"PASS: {checks} macro persistence checks; isolated directory only.");
}
finally
{
    Directory.Delete(testRoot, recursive: true);
}
