using System.ComponentModel;
using Microsoft.Win32;

namespace WeekTray;

internal sealed class TrayContext : ApplicationContext
{
    private readonly Preferences preferences = new(Preferences.DefaultDirectory);
    private readonly PowerProfiles power = new(new WindowsPowerSettings());
    private readonly Control dispatcher = new();
    private readonly NotifyIcon tray = new();
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem modeItem = new();
    private readonly ToolStripMenuItem startupItem = new("Start med Windows");
    private readonly ToolStripMenuItem exitItem = new("Avslutt");
    private readonly System.Windows.Forms.Timer weekTimer = new();
    private readonly System.Windows.Forms.Timer fallbackTimer = new() { Interval = 60000 };
    private SchemeMonitor? monitor;
    private Icon? icon;
    private (int Week, SleepMode? Mode, int Size)? iconKey;
    private SleepMode selected = SleepMode.On;
    private SleepMode? verified;
    private Guid? attemptedScheme;
    private bool applying;
    private volatile bool disposed;

    public TrayContext()
    {
        try
        {
            _ = dispatcher.Handle;
            menu.Items.AddRange([modeItem, new ToolStripSeparator(), startupItem, new ToolStripSeparator(), exitItem]);
            modeItem.Click += (_, _) => Apply(verified is null ? selected :
                verified == SleepMode.On ? SleepMode.Off : SleepMode.On, saveSelection: true);
            startupItem.Click += (_, _) => ToggleStartup();
            exitItem.Click += (_, _) => ExitThread();
            menu.Opening += (_, _) =>
            {
                RefreshStartupItem();
                RefreshIcon();
                CheckPlan();
            };
            tray.ContextMenuStrip = menu;
            weekTimer.Tick += (_, _) => ScheduleWeekUpdate();
            fallbackTimer.Tick += (_, _) => CheckPlan();

            SystemEvents.TimeChanged += OnClockOrDisplayChanged;
            SystemEvents.DisplaySettingsChanged += OnClockOrDisplayChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            Application.ThreadException += OnThreadException;

            ScheduleWeekUpdate();
            tray.Visible = true;
            Dispatch(Initialize);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        try
        {
            if (preferences.Exists)
            {
                selected = preferences.Load();
            }
            else
            {
                Startup.SetEnabled(true);
                preferences.Save(selected);
                Notify("Start med Windows er slått på. Du kan slå det av i menyen.");
            }
        }
        catch (Exception error) { Report("Kunne ikke klargjøre lokale innstillinger.", error); }

        RefreshStartupItem();
        try { monitor = new SchemeMonitor(() => Dispatch(OnSchemeNotification)); }
        catch (Exception error) { StartFallback(error); }
        Apply(selected, saveSelection: false);
    }

    private void OnSchemeNotification()
    {
        if (monitor is null)
            return;
        try { monitor.Rearm(); }
        catch (Exception error) { StartFallback(error); }
        CheckPlan();
    }

    private void StartFallback(Exception error)
    {
        monitor?.Dispose();
        monitor = null;
        fallbackTimer.Start();
        Program.Log(error);
    }

    private void CheckPlan()
    {
        if (applying)
            return;

        Guid scheme;
        try { scheme = power.ActiveScheme(); }
        catch (Exception error)
        {
            if (verified is not null)
            {
                verified = null;
                RefreshIcon();
                Report("Kunne ikke lese den aktive strømplanen.", error);
            }
            attemptedScheme = null;
            return;
        }

        if (attemptedScheme == scheme)
            return;
        verified = null;
        RefreshIcon();
        Apply(selected, saveSelection: false);
    }

    private async void Apply(SleepMode target, bool saveSelection)
    {
        if (applying)
            return;
        applying = true;
        modeItem.Enabled = exitItem.Enabled = false;
        bool checkForPlanChange = false;
        try
        {
            attemptedScheme = power.ActiveScheme();
            checkForPlanChange = true;
            attemptedScheme = await Task.Run(() => power.ApplyWithElevation(target));
            selected = target;
            verified = target;
            if (saveSelection)
            {
                try { preferences.Save(target); }
                catch (Exception error) { Report("Modusen er aktiv, men kunne ikke lagres til neste oppstart.", error); }
            }

            string? warning = WindowsPowerSettings.HardwareWarning(target);
            if (warning is not null)
                Notify(warning, ToolTipIcon.Warning);
        }
        catch (Exception error)
        {
            // A failed toggle can leave the previous mode intact after rollback.
            if (verified is SleepMode previous)
            {
                try { power.Verify(previous); }
                catch { verified = null; }
            }
            if (error is Win32Exception { NativeErrorCode: 1223 })
                Notify("Endringen ble avbrutt da tillatelse ikke ble gitt.", ToolTipIcon.Warning);
            else
                Report("Kunne ikke bekrefte hvilemodusen.", error);
        }
        finally
        {
            applying = false;
            modeItem.Enabled = exitItem.Enabled = true;
            RefreshIcon();
            if (checkForPlanChange)
                CheckPlan();
        }
    }

    private void ToggleStartup()
    {
        try { Startup.SetEnabled(!Startup.IsEnabled()); }
        catch (Exception error) { Report("Kunne ikke endre automatisk oppstart.", error); }
        RefreshStartupItem();
    }

    private void RefreshStartupItem()
    {
        try { startupItem.Checked = Startup.IsEnabled(); }
        catch (Exception error) { Report("Kunne ikke lese automatisk oppstart.", error); }
    }

    private void ScheduleWeekUpdate()
    {
        weekTimer.Stop();
        DateTime now = DateTime.Now;
        RefreshIcon();
        int daysToMonday = (8 - (int)now.DayOfWeek) % 7;
        DateTime nextMonday = now.Date.AddDays(daysToMonday == 0 ? 7 : daysToMonday);
        double milliseconds = (nextMonday.ToUniversalTime() - DateTime.UtcNow).TotalMilliseconds;
        weekTimer.Interval = (int)Math.Clamp(Math.Ceiling(milliseconds), 1, int.MaxValue);
        weekTimer.Start();
    }

    private void RefreshIcon()
    {
        string modeLabel = verified switch
        {
            SleepMode.On => "Hvilemodus PÅ",
            SleepMode.Off => "Hvilemodus AV",
            _ => "Hvilemodus ubekreftet"
        };
        modeItem.Text = $"Mode: {modeLabel}";
        var key = (Week: WeekIcon.Number(DateTime.Now), Mode: verified, Size: WeekIcon.TraySize());
        if (iconKey == key)
            return;
        Icon replacement = WeekIcon.Create(key.Week, key.Mode, key.Size);
        tray.Icon = replacement;
        icon?.Dispose();
        icon = replacement;
        iconKey = key;
        tray.Text = $"ISO-uke {WeekIcon.Digits(key.Week)} – {modeLabel}";
    }

    private void OnClockOrDisplayChanged(object? sender, EventArgs args) => Dispatch(ScheduleWeekUpdate);

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume)
            Dispatch(() => { ScheduleWeekUpdate(); CheckPlan(); });
        else if (args.Mode == PowerModes.StatusChange)
            Dispatch(CheckPlan);
    }

    private void Dispatch(Action action)
    {
        if (disposed)
            return;
        try { dispatcher.BeginInvoke(action); }
        catch (InvalidOperationException) when (disposed) { }
    }

    private void OnThreadException(object sender, ThreadExceptionEventArgs args) =>
        Report("Programmet oppdaget en feil.", args.Exception);

    private void Notify(string message, ToolTipIcon type = ToolTipIcon.Info) =>
        tray.ShowBalloonTip(6000, "Uke og hvilemodus", message, type);

    private void Report(string message, Exception error)
    {
        Program.Log(error);
        Notify($"{message} {error.Message}", ToolTipIcon.Error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            Application.ThreadException -= OnThreadException;
            SystemEvents.TimeChanged -= OnClockOrDisplayChanged;
            SystemEvents.DisplaySettingsChanged -= OnClockOrDisplayChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            monitor?.Dispose();
            weekTimer.Dispose();
            fallbackTimer.Dispose();
            tray.Visible = false;
            tray.Dispose();
            icon?.Dispose();
            menu.Dispose();
            dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}

