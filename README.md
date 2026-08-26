# JuiceBar

A fuel gauge for your PC's electricity bill, in the Windows tray.

JuiceBar measures how much power your computer is actually drawing, converts it
into money using your local electricity rate, and keeps a gauge in the system
tray that fills up like a fuel gauge as the billing cycle goes on.

Click it and you get the details: current watts, energy and cost for today and
for the cycle, a projection for the end of the month, and a trend of the last
hour.

---

## Why bother

A desktop PC with a high-end GPU can pull anywhere between 60 W idle and 600 W
under load. That difference is real money, but nothing on Windows shows it to
you. JuiceBar makes it visible without you having to think about it.

## Accuracy

This is the part that matters, so it's worth being precise about what JuiceBar
does and does not know.

Windows does not report your computer's power consumption. There is no API for
it. What hardware sensors *do* report is the power of individual components:

| Component | Source | Available |
|---|---|---|
| CPU package | Intel RAPL / AMD SMU, via PawnIO | needs the driver + admin |
| Discrete GPU | NVIDIA NVML / AMD ADL | always |
| Battery charge & discharge | ACPI | laptops only |
| Motherboard, RAM, SSDs, fans | — | never measurable |
| Power supply losses | — | never measurable |

So JuiceBar models the rest:

```
P_wall = (P_measured + B) / η

  P_measured : sum of the sensors above
  B          : constant draw of the parts nothing can measure
  η          : power supply efficiency
```

`B` and `η` differ from machine to machine, so JuiceBar works them out for your
hardware rather than guessing:

**On a laptop** — no input needed. While running on battery, the discharge rate
*is* the true whole-system power. JuiceBar compares it against the sensor sum
and learns `B` on its own, then applies what it learned once you plug back in.

**On a desktop** — open the calibration window and enter what a plug-in power
meter or an energy-monitoring smart plug reads, once while idle and once under
load. Two points, two unknowns, solved exactly. This takes the error from around
±15% down to roughly ±5%.

Without calibration JuiceBar uses conservative defaults (`B` = 35 W,
`η` = 0.88) and labels the reading "미보정" so you know not to trust it too far.

### The PawnIO driver

CPU package power lives behind model-specific registers, which need a kernel
driver. LibreHardwareMonitor — which JuiceBar uses for sensors — switched to
[PawnIO](https://pawnio.eu/) for this, a signed open-source driver. Install it
and run JuiceBar elevated to get real CPU numbers.

If you skip it, JuiceBar still runs. GPU power is still measured properly, and
the CPU share is estimated from utilisation instead. The badge in the popup will
say so.

> The driver PawnIO replaced, WinRing0, is on Microsoft's vulnerable-driver
> blocklist. JuiceBar never ships it.

### Double counting

Integrated graphics sit inside the CPU package, so their power shows up twice —
once in `CPU Package` and again as `GPU Core` / `GPU SoC`. On a Ryzen 9 7950X
that's an extra 46 W of phantom draw if you add them all up. JuiceBar excludes
integrated GPUs by default, and the settings window lets you inspect and change
every channel that goes into the sum, because sensor naming varies by hardware
and the heuristic will not be right everywhere.

---

## Electricity rates

JuiceBar has no built-in rates for any country, and it does not ask you to fill
in a form of twenty fields either. It gives you a prompt to paste into an AI
assistant, and reads the answer back.

1. Type where you live and press **프롬프트 복사**.
2. Paste it into Gemini, ChatGPT, Claude — whichever you use.
3. Paste the reply straight back into JuiceBar and press apply.

The prompt pins down a fixed JSON schema, so the reply comes back in a shape
JuiceBar can read without you editing anything. Currency, symbol, billing day,
standing charge, taxes and the whole rate structure all arrive in that one
paste. Explanations around the JSON are fine — only the JSON is extracted.

The parsed values are validated before they are accepted: a tax rate written as
`10` instead of `0.1`, a final tier with an upper bound, tiers out of order, a
billing day of 45 — all of these are rejected with a message saying what is
wrong, rather than silently producing a bill ten times too large.

You are then asked for one thing the AI cannot know: your monthly budget. That's
the whole setup. Everything else in Settings — budget, gauge basis, start with
Windows — is the same regardless of where you live.

> Prefer to type it yourself? **현재 설정 꺼내기** puts the active tariff into
> the same box as JSON, so you can edit a number and apply it.

Three shapes cover most of the world:

- **Flat** — one price per kWh. The default, and enough for most people.
- **Tiered** — the price rises as cycle usage crosses thresholds. Korea, Japan,
  parts of the US.
- **Time-of-use** — the price depends on the time of day. Common in Europe,
  North America and Australia.

On top of that: an optional monthly standing charge and any number of
percentage taxes.

Ready-made starting points live in [`tariffs/`](tariffs/) — paste one into the
same box if you'd rather start from a known tariff than ask an AI. Contributions
for more countries are welcome: one file per tariff, with a comment saying where
the numbers came from and when.

Set a monthly budget and the gauge fills against it. Leave it at zero and the
gauge tracks instantaneous power instead. You can switch between the two at any
time from the popup.

---

## Updates

JuiceBar checks GitHub Releases once a day and offers the new version in the
popup. Accepting it downloads the new executable and restarts into it — there is
no installer to run, because there was none to begin with.

Replacing a running executable is not allowed on Windows, but renaming one is.
So the current file is moved aside to `JuiceBar.exe.old`, the new one takes its
place, and the leftover is deleted on the next start. If anything fails midway
the old file is moved back, so a failed update leaves a working app rather than
a broken one.

Two checks before anything is written: the download URL must be on a GitHub
host, and the downloaded file must match the published size and actually be a
Windows executable. Otherwise the update is refused and you are pointed at the
release page instead.

Turn it off in Settings if you would rather update by hand.

## Footprint

A power meter that itself wastes power would be a poor joke, so this is measured
rather than assumed.

JuiceBar polls sensors once a second and writes one row per minute to SQLite.
Two things were fixed after measuring:

- **LibreHardwareMonitor keeps a rolling history of every sensor value in
  memory.** For a tray app that stays open for days that grows without bound,
  and JuiceBar keeps the history it actually needs in SQLite anyway. Setting
  `ValuesTimeWindow` to zero cut private memory from 213 MB to about 126 MB.
- **The cycle and daily totals were re-queried from SQLite every second.** Over
  a month of minute rows that is a full scan per second for numbers that only
  change once a minute. They are now cached per minute, with the unwritten
  current minute added on top so the display still moves smoothly.

## Multiple machines

Each machine runs its own copy and keeps its own everything — calibration,
channel selection, history, tariff. There is no server, no account, nothing to
sign into. Copy the executable to a laptop and it starts a fresh profile keyed
to that machine.

Data lives in `%APPDATA%\JuiceBar\devices\<machine-id>\`.

---

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build -c Release
dotnet test

dotnet publish src/JuiceBar -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o publish
```

That produces a single ~75 MB `JuiceBar.exe` that needs no .NET runtime
installed. Without compression it is 172 MB.

The app requests administrator rights in its manifest, so a debugger has to be
started elevated too.

### Layout

```
src/JuiceBar.Core     sensors, calibration, energy integration, tariffs, storage
src/JuiceBar          WPF tray application
tests/JuiceBar.Tests  unit tests for everything that doesn't need hardware
tools/SensorProbe     console utility that dumps what your machine reports
```

`tools/SensorProbe` is the first thing to run when sensors misbehave — it prints
every power sensor LibreHardwareMonitor can see, so you can tell a missing
driver apart from a bad channel selection.

Setting `JUICEBAR_PIN_POPUP=1` opens the popup at startup and stops it closing
when it loses focus, which makes it possible to screenshot and iterate on the
UI. It changes nothing otherwise.

To start over, delete `%APPDATA%\JuiceBar` — profiles, calibration and history
all live there.

---

## Licence

MIT. See [LICENSE](LICENSE) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
