# WeekTray

WeekTray is a Windows 11 notification-area app. It shows the current local ISO week as two white digits and applies one of two lid-close configurations to the active Windows power plan. It has no normal window and uses no network services.

## Build and publish

Use a Windows x64 machine with the .NET 10 SDK. Open `WeekTray.slnx` in a Visual Studio version that supports .NET 10, or run these commands from the repository root.

```powershell
dotnet build WeekTray.slnx -c Release
dotnet publish src/WeekTray/WeekTray.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o dist
```

Distribute `dist\WeekTray.exe`. The application includes its runtime. The .NET host may extract bundled native libraries into its local temporary cache. The build machine needs the Windows x64 runtime packs available at restore time.

## Install and use

1. Copy `WeekTray.exe` to a permanent location, such as `%LOCALAPPDATA%\Programs\WeekTray\WeekTray.exe`.
2. Double-click it. Find the week number near the clock. If Windows puts the icon under the notification-area overflow arrow, drag it into the visible area.
3. Right-click the icon. Select **Mode: Hvilemodus PÅ** or **Mode: Hvilemodus AV** to switch to the opposite mode. **Avslutt** closes WeekTray and leaves the last applied Windows settings in place.

| Mode | Icon | Sleep after, battery / plugged in | Close lid, battery / plugged in |
| --- | --- | --- | --- |
| Hvilemodus PÅ (first launch) | Black | 45 minutes / 60 minutes | Sleep |
| Hvilemodus AV | Red | Never / Never | Do nothing |

The tooltip shows the ISO week and the verified mode. Gray and **Mode: Hvilemodus ubekreftet** indicate that the app cannot currently verify a mode. Selecting that item retries the saved selection. Mode changes may trigger the Windows UAC prompt when normal permissions are insufficient. A declined prompt leaves the selection uncommitted.

## Start with Windows

On first launch, WeekTray enables **Start med Windows**, displays a notification about the setting, and creates `%LOCALAPPDATA%\WeekTray\mode.txt`. The context menu checkbox controls the app's per-user registration under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Windows launches the registered executable on sign-in. Windows Startup Apps settings or organizational policy may independently block the entry.

At startup, WeekTray loads the saved mode and applies and verifies it on the active plan. It also applies that mode when Windows switches to another plan. A named mutex scoped to the Windows user prevents duplicate tray instances. After moving the executable, run it from the new location and enable **Start med Windows** to update its registered path. To uninstall, disable **Start med Windows**, choose **Avslutt**, and delete the executable. Delete `%LOCALAPPDATA%\WeekTray` if you also want to remove the saved mode and diagnostic log.

## Verify Windows configuration

Switch modes and check the current AC and DC indexes for the two named settings using PowerShell or Command Prompt.

```powershell
powercfg /getactivescheme
powercfg /qh SCHEME_CURRENT SUB_SLEEP
powercfg /qh SCHEME_CURRENT SUB_BUTTONS
powercfg /a
```

In `SUB_SLEEP`, find **Sleep after** (`29f6c1db-86da-48c5-9fdb-f2b67b1f44da`). For **PÅ**, the AC index should be `0x00000E10` (60 minutes) and DC should be `0x00000A8C` (45 minutes). For **AV**, both indexes should be `0x00000000` (Never). In `SUB_BUTTONS`, find **Lid close action** (`5ca83367-6e45-459f-a27b-476b1d01c936`). Both indexes should be `0x00000001` for **PÅ** or `0x00000000` for **AV**. The setting names in the output are localized. `powercfg /a` reports whether Windows supports Modern Standby (S0 Low Power Idle) or traditional sleep states.

Policy may block a write, and hardware, firmware, or drivers can affect what happens when the lid closes even when the reported plan values are correct. Devices without a lid switch cannot demonstrate the lid action. WeekTray reports relevant capabilities when Windows exposes them. It applies Windows' Sleep action and never substitutes Hibernate. Existing hibernate settings, display power behavior, and battery protection can still take effect independently.

## Source and tests

- `src/WeekTray/Program.cs` starts the tray process and provides the one-operation UAC helper.
- `src/WeekTray/TrayContext.cs` owns the notification icon, menu, and time and power events.
- `src/WeekTray/PowerProfiles.cs` defines the profiles, Windows power API calls, read-back, rollback, and capability reporting.
- `src/WeekTray/LocalState.cs` manages local preferences, startup registration, single-instance enforcement, and active-plan notifications.
- `src/WeekTray/WeekIcon.cs` calculates ISO weeks and draws the digits at the tray's current icon size.
- `src/WeekTray/app.manifest` starts the tray process with the caller's privileges.
- `tests/WeekTray.Tests` covers ISO year boundaries, icon colors and sizes, both sets of native power setting values, switching and rollback with a test double, persistence, and cross-process single-instance enforcement.

```powershell
dotnet run --project tests/WeekTray.Tests -c Release
```

The automated tests do not write to Windows power plans. For an end-to-end check on a test laptop, verify both modes with the commands above, close the lid on AC and battery, then switch to a different power plan and verify that the selected mode follows. Check sign-in startup and exit behavior, and test accepting and denying UAC on a system whose power settings require elevation. Diagnostic errors are recorded in `%LOCALAPPDATA%\WeekTray\last-error.txt`.
