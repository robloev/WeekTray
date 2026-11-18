using System.ComponentModel;

namespace WeekTray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 0)
        {
            // The UAC helper can perform only one of the two predefined changes.
            if (args is [ "--apply-on" ])
                return RunHelper(SleepMode.On);
            if (args is [ "--apply-off" ])
                return RunHelper(SleepMode.Off);
            return 87;
        }

        try
        {
            using Mutex? instance = SingleInstance.Acquire();
            if (instance is null)
                return 0;

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            ApplicationConfiguration.Initialize();
            using var context = new TrayContext();
            Application.Run(context);
            return 0;
        }
        catch (Exception error)
        {
            Log(error);
            return 1;
        }
    }

    private static int RunHelper(SleepMode mode)
    {
        try
        {
            new PowerProfiles(new WindowsPowerSettings()).Apply(mode);
            return 0;
        }
        catch (Win32Exception error) { return error.NativeErrorCode; }
        catch { return 1; }
    }

    internal static void Log(Exception error)
    {
        try
        {
            string directory = Preferences.DefaultDirectory;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "last-error.txt"), $"{DateTimeOffset.Now:O}{Environment.NewLine}{error}");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError(error.ToString());
        }
    }
}
