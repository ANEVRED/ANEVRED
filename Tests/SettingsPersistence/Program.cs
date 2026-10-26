using ANEVRED.Models;
using ANEVRED.Services;

var testRoot = Path.Combine(AppContext.BaseDirectory, "isolated-settings-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
SettingsService Service(string name) => new(Path.Combine(testRoot, name));
try
{
    var first = Service("roundtrip");
    var settings = first.Load();
    Check(settings.ProtectionRules.Count > 0 && File.Exists(first.SettingsPath), "First run must seed defaults.");
    Check(first.LoadWarning is null, "First run must not warn.");
    var removed = settings.ProtectionRules[0].Pattern;
    settings.ProtectionRules.RemoveAt(0);
    settings.ProtectionRules[0].Pattern = "custom-renamed.exe";
    settings.ProtectionRules[0].Enabled = false;
    first.Save(settings);
    var loaded = new SettingsService(first.AppDataDirectory).Load();
    Check(!loaded.ProtectionRules.Any(x => x.Pattern == removed), "Deleted rule was reseeded.");
    Check(loaded.ProtectionRules.Any(x => x.Pattern == "custom-renamed.exe" && !x.Enabled), "Renamed/disabled rule was lost.");
    loaded.ProtectionRules.Clear();
    first.Save(loaded);
    Check(new SettingsService(first.AppDataDirectory).Load().ProtectionRules.Count == 0, "Empty rules were reseeded.");
    File.WriteAllText(first.SettingsPath, "{\"ProtectionRules\":null}");
    Check(first.Load().ProtectionRules.Count == 0 && first.LoadWarning is null, "Null rules must load as empty.");

    var recovery = Service("recovery");
    var original = recovery.Load();
    original.Language = "ru";
    recovery.Save(original);
    original.Language = "de";
    recovery.Save(original);
    var backupBytes = File.ReadAllBytes(recovery.BackupPath);
    const string damaged = "{broken settings";
    File.WriteAllText(recovery.SettingsPath, damaged);
    var recovered = recovery.Load();
    Check(recovered.Language == "ru" && recovery.LoadWarning is not null, "Must recover prior good backup visibly.");
    Check(File.ReadAllText(recovery.SettingsPath) == damaged, "Load changed damaged main file.");
    recovery.Save(recovered);
    Check(File.ReadAllBytes(recovery.BackupPath).SequenceEqual(backupBytes), "Recovery save destroyed backup.");
    var preserved = Directory.GetFiles(recovery.AppDataDirectory, "settings.json.corrupt-*.json");
    Check(preserved.Length == 1 && File.ReadAllText(preserved[0]) == damaged, "Damaged bytes not preserved.");
    Check(recovery.Load().Language == "ru" && recovery.LoadWarning is null, "Recovered save did not roundtrip.");
    Check(Directory.GetFiles(recovery.AppDataDirectory, "*.tmp").Length == 0, "Temporary save file remains.");

    using (var locked = new FileStream(recovery.SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        Check(recovery.Load().Language == "ru" && recovery.LoadWarning is not null, "Unreadable main must recover backup.");
        var failed = false;
        try { recovery.Save(recovered); } catch (IOException) { failed = true; }
        Check(failed, "Locked main must not be overwritten.");
        Check(File.ReadAllBytes(recovery.BackupPath).SequenceEqual(backupBytes), "Failed save changed backup.");
        Check(Directory.GetFiles(recovery.AppDataDirectory, "*.tmp").Length == 0, "Failed save left temporary file.");
    }
    File.Delete(recovery.SettingsPath);
    Check(recovery.Load().Language == "ru" && recovery.LoadWarning is not null, "Missing main must recover backup.");

    var broken = Service("both-broken");
    Directory.CreateDirectory(broken.AppDataDirectory);
    File.WriteAllText(broken.SettingsPath, "null");
    File.WriteAllText(broken.BackupPath, "{invalid backup");
    broken.Load();
    Check(broken.LoadWarning is not null && File.ReadAllText(broken.SettingsPath) == "null"
        && File.ReadAllText(broken.BackupPath) == "{invalid backup", "Broken pair must warn and remain unchanged on load.");
    var blockedDirectory = Path.Combine(testRoot, "blocked-directory");
    File.WriteAllText(blockedDirectory, "existing file");
    var blocked = new SettingsService(blockedDirectory);
    Check(blocked.Load().ProtectionRules.Count > 0 && blocked.LoadWarning is not null
        && File.ReadAllText(blockedDirectory) == "existing file", "First-run write failure must warn without crashing or replacing existing data.");
    Console.WriteLine($"PASS: {checks} settings persistence checks; isolated directory only.");
}
finally
{
    Directory.Delete(testRoot, recursive: true);
}
