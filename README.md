# Disk Temp Monitor

A Windows tray monitor for **disk, CPU and GPU temperatures**, with full S.M.A.R.T. data
and self-tests for every drive attached to the machine — NVMe, SATA SSDs and HDDs, flash
drives and USB enclosures.

Nothing else has to be installed or kept running: the application talks to the storage
driver itself, and carries the kernel driver it needs to read CPU sensors.

> Full documentation is in Italian: **[LEGGIMI.md](LEGGIMI.md)** — it covers every screen,
> setting and protocol detail, including why several of the design decisions are what
> they are.

## What it does

Three tabs:

- **Disks** — serial number, temperature and S.M.A.R.T. attributes for every drive, with
  **numeric tray icons** in the style of Core Temp. Per-disk cards with a temperature
  dial, health pill, remaining life, volume usage, a session temperature chart, and a
  full attribute table with per-row status.
- **System** — per-core CPU temperature and load, GPU, CPU identity (model,
  microarchitecture, cache, instruction sets), memory bank by bank, motherboard and BIOS.
  CPU-Z style.
- **Self-test** — device self-diagnosis per drive: start short or extended, abort a
  running one, watch progress, and keep a history of past runs.

Plus:

- **Tray icons** — one icon per disk, or a single icon for the hottest one; six extra
  icons for CPU/GPU temperature, CPU/GPU load, VRAM and RAM usage. Each icon carries a
  short tag so two icons reading `43` are never ambiguous.
- **Compact widget** (`F8`) — an always-on-top strip with just the readings, a sparkline
  per disk and an optional per-core grid. Drag it into a corner; it remembers where.
- **Game overlay** — FPS plus temperatures and loads on top of a running game, toggled by
  a global hotkey (`Ctrl+Alt+F9` by default). FPS are counted via ETW, with no injection
  into the game. The position is chosen by dragging a scale preview of your screen.
- **Native Windows notifications** for critical temperature, low disk space, health
  degradation and finished self-tests, with a configurable minimum interval between
  repeats.
- **Light and dark theme**, following Windows or forced; **text export** of a short
  per-disk summary or a full report with the complete attribute tables.

## How it reads the data

No external tooling: the app issues IOCTLs to the storage driver directly, the same route
CrystalDiskInfo takes. Sources are tried in cascade and the one that worked is remembered
per drive, so periodic refreshes don't pay a timeout for every channel that fails.

| Drive | Source |
|---|---|
| NVMe | `IOCTL_STORAGE_QUERY_PROPERTY` → Identify Controller, and the **SMART/Health log page 02h** (composite temperature, 8 sensors, wear, hours, cycles, errors) |
| SATA / ATA | `IOCTL_ATA_PASS_THROUGH`, legacy `SMART_RCV_DRIVE_DATA`, then SCSI encapsulation → **IDENTIFY DEVICE** and **READ ATTRIBUTES / THRESHOLDS** |
| USB bridge over SATA | `IOCTL_SCSI_PASS_THROUGH_DIRECT` → **ATA PASS-THROUGH**, 16-byte, then the same with `ck_cond`, then 12-byte (SAT) |
| USB bridge over NVMe | The bridge's own protocol — currently **Realtek RTL9210/9220**, read-only commands only |
| Any SCSI/USB | **LOG SENSE** pages `0Dh` (Temperature) and `2Fh` (Informational Exceptions) |
| Fallback | `StorageDeviceTemperatureProperty`, the sensors the driver exposes on its own |

CPU core temperatures live in MSRs that only the kernel can read. The app ships the driver
for that itself (embedded as a resource in LibreHardwareMonitor 0.9.4, registered at
startup and removed on exit), so there is nothing to install. If Windows **Core isolation**
or an antivirus blocks that driver, the app says so rather than showing blanks, and falls
back to Core Temp's shared memory when Core Temp happens to be installed.

## Requirements

- Windows 10 (build 19041) or later
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- **Administrator rights.** The manifest requests elevation (`requireAdministrator`):
  without it, SATA drives and drives behind USB bridges report neither temperature nor
  attributes, because S.M.A.R.T. commands to the storage driver are privileged.

Because Windows will not auto-start elevated programs from the registry `Run` key, "start
with Windows" is set up as a scheduled task with highest privileges instead.

## Running

```
DiskTempMonitor.exe          # normal window
DiskTempMonitor.exe --tray   # start minimised to the notification area
DiskTempMonitor.exe --show   # force the window open even if settings say otherwise
```

Settings and error logs live in `%AppData%\DiskTempMonitor\`.

### Shortcuts

| Key | Action |
|---|---|
| `F5` | Re-read temperature and attributes of known drives |
| `F6` | Re-enumerate the physical drives |
| `F9` | Show or collapse the attribute sidebar |
| `F8` | Compact widget |
| `F4` | Settings |
| `F3` | Export menu |
| `F2` | Cycle light / dark / automatic theme |
| `Esc` | Hide to the notification area |

## Building

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

The build stamps the compile time into the informational version (`1.7.1+2026-09-18T16:24`),
which the status bar shows — handy for telling at a glance whether the running copy is the
one you just built.

## Project layout

```
Program.cs            startup, single instance, error handling
Native/               P/Invoke, IOCTL codes, ATA commands
Models/               DiskInfo, SmartAttribute, health states
Services/             storage protocols, sensors, settings, notifications, self-tests
UI/                   WinForms views, custom-drawn controls, theme, tray, overlay
```

Most WinForms controls are drawn by hand (`UI/Controls.cs`): `DropDownList` combo boxes,
`NumericUpDown` and `CheckBox` either ignore `BackColor` or become unreadable on a dark
background. Scrollbars are drawn too, for the same reason.

## Dependencies

[LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
**0.9.4** — deliberately not the latest. From 0.9.5 the library no longer embeds the ring-0
driver and relies on PawnIO, which the user would have to install separately. 0.9.4 carries
it as a resource and loads it itself, which keeps this application self-contained.
