using System.Diagnostics;
using WeekTray;

if (args is ["--mutex", var name])
{
    using Mutex? instance = SingleInstance.Acquire(name);
    return instance is null ? 2 : 0;
}

(Action Run, string Name)[] tests =
[
    (IsoWeeks, "ISO year boundaries"),
    (Icons, "Both icon colors and DPI sizes"),
    (ApplyModes, "Native API profile values and mode switching"),
    (Rollback, "Read-back failure and rollback"),
    (PlanChange, "Switching active plans during application"),
    (Persistence, "Mode persistence and invalid configuration"),
    (MutexAcrossProcesses, "Single instance across processes")
];

int failures = 0;
foreach (var (run, testName) in tests)
{
    try { run(); Console.WriteLine($"PASS {testName}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {testName}: {error}"); }
}
return failures == 0 ? 0 : 1;

static void IsoWeeks()
{
    Equal(53, WeekIcon.Number(new DateTime(2021, 1, 1)));
    Equal(53, WeekIcon.Number(new DateTime(2021, 1, 3, 23, 59, 59)));
    Equal(1, WeekIcon.Number(new DateTime(2021, 1, 4)));
    Equal(1, WeekIcon.Number(new DateTime(2018, 12, 31)));
    Equal(52, WeekIcon.Number(new DateTime(2022, 1, 1)));
    Equal("07", WeekIcon.Digits(7));
}

static void Icons()
{
    foreach (int size in new[] { 16, 20, 24, 32 })
    foreach (SleepMode mode in Enum.GetValues<SleepMode>())
    {
        using Bitmap bitmap = WeekIcon.Render(47, mode, size);
        Equal(new Size(size, size), bitmap.Size);
        Equal((mode == SleepMode.On ? Color.Black : Color.FromArgb(194, 0, 0)).ToArgb(),
            bitmap.GetPixel(0, 0).ToArgb());
        int whitePixels = 0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            if (bitmap.GetPixel(x, y).ToArgb() == Color.White.ToArgb())
                whitePixels++;
        True(whitePixels >= 10);
        using Icon icon = WeekIcon.Create(47, mode, size);
        Equal(bitmap.Size, icon.Size);
    }
}

static void ApplyModes()
{
    var settings = new FakePower();
    var power = new PowerProfiles(settings);
    Equal(settings.Scheme, power.Apply(SleepMode.Off));
    True(settings.Values.SequenceEqual(new uint[] { 0, 0, 0, 0 }));
    Equal(settings.Scheme, power.Apply(SleepMode.On));
    True(settings.Values.SequenceEqual(new uint[] { 3600, 2700, 1, 1 }));
    Equal(settings.Scheme, power.Verify(SleepMode.On));
    Equal(2, settings.Activations);
}

static void Rollback()
{
    var settings = new FakePower { IgnoreFirstWriteTo = 2 };
    var power = new PowerProfiles(settings);
    Throws<InvalidOperationException>(() => power.Apply(SleepMode.On));
    True(settings.Values.SequenceEqual(new uint[] { 180, 240, 2, 2 }));
    Equal(2, settings.Activations);
}

static void PlanChange()
{
    var settings = new FakePower { ChangePlanOnFirstWrite = true };
    Guid original = settings.Scheme;
    var power = new PowerProfiles(settings);
    Throws<InvalidOperationException>(() => power.Apply(SleepMode.On));
    True(original != settings.Scheme);
    True(settings.Values.SequenceEqual(new uint[] { 180, 240, 2, 2 }));
    Equal(0, settings.Activations);
}

static void Persistence()
{
    string directory = Path.Combine(Path.GetTempPath(), $"WeekTray-test-{Guid.NewGuid():N}");
    try
    {
        var preferences = new Preferences(directory);
        True(!preferences.Exists);
        preferences.Save(SleepMode.On);
        Equal(SleepMode.On, new Preferences(directory).Load());
        preferences.Save(SleepMode.Off);
        Equal(SleepMode.Off, new Preferences(directory).Load());
        File.WriteAllText(Path.Combine(directory, "mode.txt"), "unknown");
        Throws<InvalidDataException>(() => preferences.Load());
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            File.Delete(Path.Combine(directory, "mode.txt"));
            File.Delete(Path.Combine(directory, "mode.txt.tmp"));
            Directory.Delete(directory);
        }
    }
}

static void MutexAcrossProcesses()
{
    string name = $"WeekTray.Test.{Guid.NewGuid():N}";
    using (Mutex instance = SingleInstance.Acquire(name) ?? throw new Exception("Mutex was already held."))
    {
        Equal(2, ChildExitCode(name));
    }
    Equal(0, ChildExitCode(name));
}

static int ChildExitCode(string name)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    start.ArgumentList.Add("--mutex");
    start.ArgumentList.Add(name);
    using Process child = Process.Start(start) ?? throw new Exception("Could not start test child.");
    if (!child.WaitForExit(10000))
    {
        child.Kill();
        throw new TimeoutException("Test child did not exit.");
    }
    return child.ExitCode;
}

static void True(bool condition)
{
    if (!condition)
        throw new Exception("Assertion failed.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}.");
}

static void Throws<T>(Action operation) where T : Exception
{
    try { operation(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

internal sealed class FakePower : IPowerSettings
{
    public Guid Scheme { get; private set; } = Guid.NewGuid();
    public int Activations { get; private set; }
    public uint[] Values { get; } = [180, 240, 2, 2];
    public int? IgnoreFirstWriteTo { get; set; }
    public bool ChangePlanOnFirstWrite { get; set; }

    public Guid ActiveScheme() => Scheme;
    public void CheckPolicy(int setting) { }
    public uint Read(Guid scheme, int setting) => Values[setting];

    public void Write(Guid scheme, int setting, uint value)
    {
        if (IgnoreFirstWriteTo == setting)
        {
            IgnoreFirstWriteTo = null;
            return;
        }

        if (ChangePlanOnFirstWrite)
        {
            ChangePlanOnFirstWrite = false;
            Scheme = Guid.NewGuid();
        }
        Values[setting] = value;
    }

    public void Activate(Guid scheme)
    {
        if (scheme != Scheme)
            throw new Exception("Attempted to reactivate an obsolete plan.");
        Activations++;
    }
}
