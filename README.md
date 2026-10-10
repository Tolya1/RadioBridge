# RadioBridge

RadioBridge drives the **Logitech / Saitek Pro Flight Radio Panel** for **Microsoft Flight Simulator
2020/2024** and **X-Plane 12**, on Windows, Linux and macOS. It is a small, free alternative to
general-purpose panel software, made for one panel and done properly:

- **8.33 kHz radios that read like a real radio.** COM frequencies show SPAD-style, without the
  leading `1` and with all three decimals (`123.405` → `23.405`), and the inner knob steps through
  real 8.33 kHz channel names, skipping the ones that don't exist (`…515 → …525`).
- **Both simulators, picked automatically.** Start MSFS or X-Plane and RadioBridge follows whichever
  is running. X-Plane needs no plugin, because RadioBridge uses X-Plane's built-in Web API.
- **A panel that never fights you.** Fast spins accelerate, no detent is lost while the sim catches
  up, and the displays always settle on what the simulator really has.

![RadioBridge window](docs/images/radiobridge.png)

## Contents

- [What works where](#what-works-where)
- [Getting started](#getting-started)
- [Using the panel](#using-the-panel)
- [Settings](#settings)
- [Command line](#command-line)
- [Troubleshooting](#troubleshooting)
- [Development](#development)
- [License](#license)

## What works where

|                  | Windows | Linux | macOS |
|------------------|:-------:|:-----:|:-----:|
| X-Plane 12.1.4+  | ✅      | ✅    | ✅    |
| MSFS 2020 / 2024 | ✅      | –     | –     |

MSFS only runs on Windows, and SimConnect is a Windows library. X-Plane works anywhere it runs,
because RadioBridge talks to it over a local WebSocket.

## Getting started

### 1. Get RadioBridge

There are no prebuilt releases yet. Build from source with the [.NET 10 SDK](https://dotnet.microsoft.com/download)
(or let [mise](https://mise.jdx.dev) install it):

```bash
git clone https://github.com/Tolya1/RadioBridge.git
cd RadioBridge
mise run run
```

Without mise, run `dotnet run --project src/RadioBridge.Desktop`. To build self-contained apps for
every platform into `./publish`, run `mise run publish`.

### 2. Free the panel

Only one program can drive the panel at a time. Quit or disable **SPAD.neXt**, **HangarControl**,
**Logitech's Flight Panels plugins** and **xsaitekpanels** before starting RadioBridge. Otherwise the
displays flicker between programs and both react to the knobs.

### 3. Connect your simulator

**X-Plane 12** (12.1.4 or newer). Nothing to install. The Web API is on by default on port 8086.
If RadioBridge doesn't connect, open X-Plane's **Settings › Network** and make sure **Disable
incoming traffic** is off. If you changed X-Plane's port (`--web_server_port`), set the same port
in RadioBridge's settings.

**MSFS 2020 / 2024** (Windows). RadioBridge needs Microsoft's 64-bit `SimConnect.dll`, which it
doesn't ship. It looks in these places, in order:

1. The `SIMCONNECT_DLL` environment variable (a file or a folder).
2. Next to `RadioBridge.exe`.
3. The MSFS SDKs: `%MSFS2024_SDK%` and `%MSFS_SDK%` (set by the SDK installers), then
   `C:\MSFS 2024 SDK` and `C:\MSFS SDK`, each under `SimConnect SDK\lib\`.

If you don't have an SDK installed, copy a 64-bit `SimConnect.dll` next to `RadioBridge.exe`. The
log names the DLL it uses, or lists every place it searched.

### 4. Linux and macOS

**Linux:** allow your user to open the panel, then replug it:

```bash
sudo cp packaging/linux/99-radiobridge.rules /etc/udev/rules.d/
```

```bash
sudo udevadm control --reload-rules && sudo udevadm trigger
```

**macOS:** builds are not signed yet. After copying the app, clear the quarantine flag once, e.g.
`xattr -dr com.apple.quarantine publish/osx-arm64`.

## Using the panel

Each row of the panel works on its own. Turn its selector to choose what that row shows.

| Selector | Left display | Right display | Outer knob | Inner knob | ACT/STBY press | ACT/STBY hold |
|---|---|---|---|---|---|---|
| **COM1 / COM2** | active | standby | standby ±1 MHz | standby ±1 channel (8.33 or 25 kHz) | swap | toggle 8.33 / 25 kHz steps |
| **NAV1 / NAV2** | active | standby | standby ±1 MHz | standby ±50 kHz | swap | – |
| **ADF** | ADF1 (upper row) or ADF2 (lower row) | – | ±10 kHz | ±1 kHz | – | – |
| **DME** | DME1 distance | DME2 distance | – | – | – | – |
| **XPDR** | altimeter | squawk | first two squawk digits | last two squawk digits | edit altimeter instead (outer ±10, inner ±1) and back | standard pressure |

Some details:

- **Inner knobs wrap inside the MHz** and **outer knobs wrap at the band edge** (COM 118–136 MHz, NAV
  108–117 MHz), like real radio knobs.
- In XPDR mode, a **lit decimal point at the end** of a display marks the value the knobs edit, e.g.
  `7000.` (squawk) or `1013.` (altimeter).
- **Fast spins accelerate.** Detents less than 90 ms apart count ×3, and less than 35 ms ×8. Both
  limits can be changed in the settings.
- When you change a frequency, the panel shows your new value right away and keeps it for under a
  second while the simulator catches up. After that the simulator's value always wins. So if a radio
  can't tune 8.33 kHz and snaps back, the panel shows that. **Hold ACT/STBY** to switch the inner knob
  to 25 kHz steps for that aircraft.

## Settings

Settings are in the window and are saved immediately to `settings.json` in the data folder:

| OS | Data folder (settings and logs) |
|---|---|
| Windows | `%APPDATA%\RadioBridge` |
| Linux / macOS | `~/.config/RadioBridge` |

| Setting | Default | Meaning |
|---|---|---|
| `ComSpacing` | `Khz833` | Inner COM knob steps: `Khz833` or `Khz25` (hold ACT/STBY on COM to toggle) |
| `CompactComDisplay` | `true` | `23.405` style; `false` shows `123.40` |
| `BaroUnit` | `Hectopascal` | Altimeter in `Hectopascal` or `InchesOfMercury` |
| `Acceleration` | on, 35 ms ×8, 90 ms ×3 | Knob acceleration thresholds and multipliers |
| `LongPressMs` | `600` | How long ACT/STBY must be held for a long press |
| `MsfsEnabled`, `XPlaneEnabled` | `true` | Which simulators to look for (applied on restart) |
| `XPlanePort` | `8086` | X-Plane Web API port (applied on reconnect) |

## Command line

```text
RadioBridge [options]
  --headless           Run without a window (stop with Ctrl+C), e.g. on a sim PC without a desktop session.
  --minimized          Start with the window minimized.
  --no-panel           Do not open the USB panel.
  --automation PORT    Enable the local automation API (testing only, see docs/testing.md).
  --data-dir DIR       Use another folder for settings.json and logs.
```

## Troubleshooting

| Symptom | What to check |
|---|---|
| *Panel not found* | Is the panel plugged in directly (not through an unpowered hub)? On Linux, install the udev rule. On Windows, the panel shows as a HID device `VID_06A3&PID_0D05`. |
| Displays flicker or the knobs do two things | Another program is driving the panel. See [Free the panel](#2-free-the-panel). |
| *No simulator* with X-Plane running | X-Plane 12.1.4+? **Disable incoming traffic** off? Same port? The log says exactly why it can't connect. |
| *No simulator* with MSFS running | Was SimConnect.dll found? The log names the DLL it uses or lists where it searched. It must be the 64-bit DLL. |
| COM steps snap back to the previous value | That aircraft's radio only supports 25 kHz. Hold ACT/STBY on COM to switch to 25 kHz steps. |
| Add-on aircraft ignores the panel | Some airliners (custom radio management panels) don't use the simulator's standard radios, so no external tool can tune them this way. |

Logs are in the `logs` folder in the data folder (one file per day, the last 7 kept). Please attach
the latest one to bug reports.

## Development

```text
src/RadioBridge.Core        panel protocol, radio logic, simulator backends, automation API
src/RadioBridge.Desktop     Avalonia window, headless mode, command line
tests/RadioBridge.Tests             unit tests, and the X-Plane backend against the emulator
tests/RadioBridge.FakeXPlane        X-Plane Web API emulator (library and stand-alone tool)
tests/RadioBridge.FunctionalTests   the real app, driven end to end through its automation API
```

All checks run locally with [mise](https://mise.jdx.dev). There is no hosted CI yet.

| Task | What it does |
|---|---|
| `mise run ci` | Everything below except the live-sim tasks: lint, build, unit tests, functional tests |
| `mise run lint` / `mise run format` | Check / fix formatting and code style (`dotnet format`) |
| `mise run test` | Unit tests |
| `mise run functional` | The real app against the X-Plane emulator, driven through the automation API |
| `mise run functional:xplane` | The same scenarios against a running X-Plane 12 with a flight loaded |
| `mise run functional:msfs` | The same scenarios against a running MSFS with a flight loaded |
| `mise run emulator` | X-Plane emulator on port 8086, to try the app without X-Plane |
| `mise run publish` | Self-contained builds for win-x64, linux-x64, osx-arm64 and osx-x64 |

More in [docs/architecture.md](docs/architecture.md), [docs/testing.md](docs/testing.md) and
[docs/radio-panel-protocol.md](docs/radio-panel-protocol.md).

## License

[Apache License 2.0](LICENSE). Third-party components are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The panel protocol notes build on the public documentation in
[bjanders/fpanels](https://github.com/bjanders/fpanels). Logitech, Saitek, Microsoft Flight Simulator,
SimConnect and X-Plane are trademarks of their respective owners. RadioBridge is not affiliated with
any of them.
