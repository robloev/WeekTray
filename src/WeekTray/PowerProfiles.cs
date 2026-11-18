using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WeekTray;

internal enum SleepMode { On = 1, Off = 0 }

internal interface IPowerSettings
{
    Guid ActiveScheme();
    void CheckPolicy(int setting);
    uint Read(Guid scheme, int setting);
    void Write(Guid scheme, int setting, uint value);
    void Activate(Guid scheme);
}

internal sealed class PowerProfiles(IPowerSettings settings)
{
    internal static uint[] Values(SleepMode mode) =>
        [mode == SleepMode.On ? 3600u : 0u, mode == SleepMode.On ? 2700u : 0u, (uint)mode, (uint)mode];

    public Guid ActiveScheme() => settings.ActiveScheme();

    public Guid Verify(SleepMode mode)
    {
        Guid scheme = ActiveScheme();
        Verify(scheme, Values(mode));
        return scheme;
    }

    public Guid Apply(SleepMode mode)
    {
        Guid scheme = ActiveScheme();
        uint[] wanted = Values(mode);
        uint[] original = new uint[wanted.Length];
        List<int> written = [];
        for (int index = 0; index < wanted.Length; index++)
        {
            settings.CheckPolicy(index);
            original[index] = settings.Read(scheme, index);
        }

        try
        {
            EnsureActive(scheme);
            for (int index = 0; index < wanted.Length; index++)
            {
                if (original[index] == wanted[index])
                    continue;
                settings.Write(scheme, index, wanted[index]);
                written.Add(index);
            }

            EnsureActive(scheme);
            // Reactivate even if the stored values already match, in case they were
            // edited externally without being activated.
            settings.Activate(scheme);
            Verify(scheme, wanted);
            return scheme;
        }
        catch (Exception applyError)
        {
            if (written.Count == 0)
                throw;

            List<Exception> rollbackErrors = [];
            foreach (int index in written)
            {
                try { settings.Write(scheme, index, original[index]); }
                catch (Exception error) { rollbackErrors.Add(error); }
            }

            try
            {
                if (ActiveScheme() == scheme)
                    settings.Activate(scheme);
                foreach (int index in written)
                    if (settings.Read(scheme, index) != original[index])
                        throw new InvalidOperationException("Gjenoppretting kunne ikke bekreftes.");
            }
            catch (Exception error) { rollbackErrors.Add(error); }

            if (rollbackErrors.Count != 0)
            {
                rollbackErrors.Insert(0, applyError);
                throw new InvalidOperationException(
                    "Kontroller strømplanen. Tidligere innstillinger kunne ikke gjenopprettes.",
                    new AggregateException(rollbackErrors));
            }
            throw;
        }
    }

    private void Verify(Guid scheme, uint[] wanted)
    {
        for (int index = 0; index < wanted.Length; index++)
        {
            settings.CheckPolicy(index);
            if (settings.Read(scheme, index) != wanted[index])
                throw new InvalidOperationException("Windows rapporterte andre verdier enn forventet.");
        }
        EnsureActive(scheme);
    }

    private void EnsureActive(Guid scheme)
    {
        if (ActiveScheme() != scheme)
            throw new InvalidOperationException("Den aktive strømplanen ble endret under operasjonen.");
    }

    public Guid ApplyWithElevation(SleepMode mode)
    {
        try { return Apply(mode); }
        catch (Win32Exception error) when (error.NativeErrorCode is 5 or 740 or 1314)
        {
            using Process helper = Process.Start(new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = mode == SleepMode.On ? "--apply-on" : "--apply-off",
                UseShellExecute = true,
                Verb = "runas"
            }) ?? throw new InvalidOperationException("Kunne ikke starte strømkonfigurering.");
            helper.WaitForExit();
            if (helper.ExitCode != 0)
                throw new Win32Exception(helper.ExitCode, helper.ExitCode == 1
                    ? "Strømkonfigurering mislyktes. Kontroller strømplanen."
                    : $"Windows avviste strømkonfigureringen (kode {helper.ExitCode}).");
            return Verify(mode);
        }
    }
}

internal sealed class WindowsPowerSettings : IPowerSettings
{
    private static readonly Guid Sleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid Idle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    private static readonly Guid Buttons = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static readonly Guid Lid = new("5ca83367-6e45-459f-a27b-476b1d01c936");

    public Guid ActiveScheme()
    {
        Check(PowerGetActiveScheme(0, out nint pointer));
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    public void CheckPolicy(int setting)
    {
        Guid id = setting < 2 ? Idle : Lid;
        const uint KeyQueryValue = 0x0001;
        Check(PowerSettingAccessCheckEx((uint)(setting % 2), in id, KeyQueryValue));
    }

    public uint Read(Guid scheme, int setting)
    {
        var (group, id) = setting < 2 ? (Sleep, Idle) : (Buttons, Lid);
        uint value;
        int result = setting % 2 == 0
            ? PowerReadACValueIndex(0, in scheme, in group, in id, out value)
            : PowerReadDCValueIndex(0, in scheme, in group, in id, out value);
        Check(result);
        return value;
    }

    public void Write(Guid scheme, int setting, uint value)
    {
        var (group, id) = setting < 2 ? (Sleep, Idle) : (Buttons, Lid);
        Check(setting % 2 == 0
            ? PowerWriteACValueIndex(0, in scheme, in group, in id, value)
            : PowerWriteDCValueIndex(0, in scheme, in group, in id, value));
    }

    public void Activate(Guid scheme) => Check(PowerSetActiveScheme(0, in scheme));

    private static void Check(int result)
    {
        if (result != 0)
            throw new Win32Exception(result);
    }

    internal static string? HardwareWarning(SleepMode mode)
    {
        if (!GetPwrCapabilities(out PowerCapabilities caps))
            return "Windows kunne ikke bekrefte støtte for lokk og hvilemodus.";
        if (caps.LidPresent == 0)
            return "Strømplanen er lagret. Windows rapporterer ingen lokkbryter.";
        if (mode == SleepMode.On && caps.S1 == 0 && caps.S2 == 0 && caps.S3 == 0 && caps.AoAc == 0)
            return "Strømplanen er lagret. Windows rapporterer ingen støttet hviletilstand.";
        return null;
    }

    // Offsets from SYSTEM_POWER_CAPABILITIES in the Windows SDK.
    [StructLayout(LayoutKind.Explicit, Size = 76)]
    private struct PowerCapabilities
    {
        [FieldOffset(2)] public byte LidPresent;
        [FieldOffset(3)] public byte S1;
        [FieldOffset(4)] public byte S2;
        [FieldOffset(5)] public byte S3;
        [FieldOffset(20)] public byte AoAc;
    }

    [DllImport("powrprof.dll")] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities(out PowerCapabilities caps);
    [DllImport("powrprof.dll")] private static extern int PowerGetActiveScheme(nint key, out nint scheme);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("powrprof.dll")] private static extern int PowerSettingAccessCheckEx(uint access, in Guid setting, uint accessType);
    [DllImport("powrprof.dll")] private static extern int PowerSetActiveScheme(nint key, in Guid scheme);
    [DllImport("powrprof.dll")] private static extern int PowerReadACValueIndex(nint key, in Guid scheme, in Guid group, in Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern int PowerReadDCValueIndex(nint key, in Guid scheme, in Guid group, in Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern int PowerWriteACValueIndex(nint key, in Guid scheme, in Guid group, in Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern int PowerWriteDCValueIndex(nint key, in Guid scheme, in Guid group, in Guid setting, uint value);
}



