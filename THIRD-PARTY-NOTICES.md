# Third-party notices

JuiceBar itself is MIT licensed. It depends on the following components.

## LibreHardwareMonitorLib

- License: **MPL-2.0** (Mozilla Public License 2.0)
- Source: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor

Provides the hardware sensor readings — CPU package power, GPU board power and
battery charge/discharge rate.

MPL-2.0 is a file-level copyleft licence. Linking to the library from an MIT
project is fine; only modifications made to LibreHardwareMonitor's own source
files would have to stay under MPL-2.0. JuiceBar does not modify it.

## PawnIO

- License: **GPL-2.0-or-later**, with an explicit exception for programs that
  communicate with the driver only through its device IOCTL interface.
- Source: https://github.com/namazso/PawnIO
- Installer: https://github.com/namazso/PawnIO.Setup
- Homepage: https://pawnio.eu/

A signed, open-source kernel driver that gives userspace controlled access to
model-specific registers. LibreHardwareMonitor uses it to read CPU package
power; without it those sensors report zero.

JuiceBar communicates with PawnIO only indirectly, through
LibreHardwareMonitor's IOCTL calls, so the IOCTL exception applies and JuiceBar
can remain MIT licensed.

PawnIO's own installer states that it *"can be redistributed unmodified"*, so
release archives may bundle the unmodified official installer. JuiceBar does not
modify it in any way.

PawnIO replaced the older WinRing0 driver, which Microsoft added to its
vulnerable-driver blocklist (CVE-2020-14979). JuiceBar never ships WinRing0.

## Microsoft.Data.Sqlite / SQLitePCLRaw

- License: **MIT** / **Apache-2.0**
- Source: https://github.com/dotnet/efcore, https://github.com/ericsink/SQLitePCL.raw

Stores the minute-resolution usage history.

`SQLitePCLRaw.bundle_e_sqlite3` is pinned above the version Microsoft.Data.Sqlite
would otherwise pull in, because SQLite releases before 3.50.2 are affected by
CVE-2025-6965.

## .NET / WPF

- License: **MIT**
- Source: https://github.com/dotnet/runtime, https://github.com/dotnet/wpf
