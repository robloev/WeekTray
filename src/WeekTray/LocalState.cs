using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WeekTray;

internal sealed class Preferences(string directory)
{
    internal static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeekTray");
    private string FilePath => Path.Combine(directory, "mode.txt");
    public bool Exists => File.Exists(FilePath);

    public SleepMode Load() => File.ReadAllText(FilePath).Trim() switch
    {
        "On" => SleepMode.On,
        "Off" => SleepMode.Off,
        _ => throw new InvalidDataException("Den lagrede hvilemodusen er ugyldig.")
    };

    public void Save(SleepMode mode)
    {
        Directory.CreateDirectory(directory);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, mode.ToString());
        File.Move(temp, FilePath, overwrite: true);
    }
}

internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WeekTray";
    private static string Command => $"\"{Application.ExecutablePath}\"";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, Command);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

internal static class SingleInstance
{
    public static Mutex? Acquire(string appName = "WeekTray")
    {
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        var mutex = new Mutex(false, $@"Global\{appName}.{user.User!.Value}", out bool created);
        if (created)
            return mutex;
        mutex.Dispose();
        return null;
    }
}

internal sealed class SchemeMonitor : IDisposable
{
    private readonly RegistryKey key;
    private readonly AutoResetEvent change = new(false);
    private RegisteredWaitHandle? registration;
    private bool disposed;

    public SchemeMonitor(Action onChange)
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
        key = Registry.LocalMachine.OpenSubKey(path)
            ?? throw new InvalidOperationException("Fant ikke registeret for aktive strømplaner.");
        try
        {
            Rearm();
            registration = ThreadPool.RegisterWaitForSingleObject(change, (_, _) => onChange(),
                null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // A registry notification is one-shot. Rearm before querying the active plan.
    public void Rearm()
    {
        const uint LastSetOnAnyThread = 0x10000004;
        int result = RegNotifyChangeKeyValue(key.Handle, false, LastSetOnAnyThread,
            change.SafeWaitHandle, true);
        if (result != 0)
            throw new Win32Exception(result);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        registration?.Unregister(null);
        key.Dispose();
        change.Dispose();
    }

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool subtree,
        uint filter, SafeWaitHandle changeEvent, bool asynchronous);
}
