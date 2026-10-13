# Testing

| Layer | Project | What it proves | Run by |
|---|---|---|---|
| Unit | `tests/RadioBridge.Tests` | Protocol encoding and decoding, 8.33 kHz math, formatting, squawk pairs, the altimeter values truncating cockpits show, the controller (on a fake clock), knob and button activity, the sim router and test mode, the debug recording and its watch list, and the X-Plane backend against the emulator | `mise run ci` |
| UI | `tests/RadioBridge.UiTests` | The readme and notices window with real text layout and simulated clicks (no screen needed): Markdown renders, a click on a link follows it, a drag across one doesn't, tabs switch, "Don't show again" appears only at start | `mise run ci` (`mise run test:ui`) |
| Emulator | `tests/RadioBridge.FakeXPlane` | Not a test itself: an emulation of X-Plane's Web API and radios, used by both test layers | |
| Functional | `tests/RadioBridge.FunctionalTests` | The built app, run as its own process and driven end to end, against the emulator or a live simulator | `mise run ci` |
| Hardware | `HardwareTests` in the unit project | On a real panel: other programs can open it while RadioBridge has it and the other way round, and selector positions are read on connect | `mise run test:hardware` (opt-in) |

## UI tests

`RadioBridge.UiTests` runs Avalonia headless: real Skia and HarfBuzz text layout without a display, and
mouse input sent into the window in-process. That is enough to find where a link's text is, click
there, and check that the renderer navigated. (`Avalonia.Headless.XUnit` isn't used: it needs xunit v3,
and the rest of the suite is on v2.) The things only a real display shows, such as how it looks, are
checked by eye: run `RadioBridge` without `--minimized`, and the reader window opens by itself until
you choose "Don't show again".

The notices are tested at the source too: `mise run notices:check` (part of `ci`) fails when
`THIRD-PARTY-NOTICES.md` isn't what the generator would produce, or when a dependency has no license
text, so a dependency update can't ship without its notice.

## Hardware tests and the panel test

`mise run test:hardware` needs the radio panel connected. It may run while other panel software, or
RadioBridge itself, is running, because sharing the panel with them is part of what it checks. It
briefly blanks the displays.

To see the hardware work with your own eyes, run `RadioBridge --panel-test`. It shows test patterns
on the displays and then prints every knob, selector and button event for 20 seconds.

## Functional tests

The functional suite starts the real app the same way a user would. It runs `--headless` by default:
a visible window could be closed by someone at the desk, which quits the app mid-run. Set
`RADIOBRIDGE_FT_UI=window` to run the same suite with the window minimized (Windows only). It adds:

```text
--no-panel --automation <free port> --data-dir tests/RadioBridge.FunctionalTests/bin/<cfg>/net10.0/runs/<timestamp>
```

The tests then use the automation API (below) to work the panel. They send raw input reports, built
from the [panel protocol](radio-panel-protocol.md) independently of the app's own decoder. They
check two things:

- **The panel side.** The display text, and the exact display report bytes a real panel would get.
- **The simulator side.** The values the simulator itself now holds. For X-Plane, the tests can also
  set values directly over X-Plane's REST API, to put the sim in a known state or to play "the pilot
  turned a knob in the cockpit".

Expected values come from the state before each action. That's why the same scenarios run against
three targets:

| Command | Simulator | Notes |
|---|---|---|
| `mise run functional` | X-Plane emulator (in-process, random port) | Default. Runs everywhere and needs nothing installed. |
| `mise run functional:xplane` | Running X-Plane 12, flight loaded, Web API on 8086 | **Changes the loaded aircraft's radios, squawk and altimeter.** |
| `mise run functional:msfs` | Running MSFS, flight loaded (Windows) | Same warning. Scenarios that need direct sim access are skipped. |

The physical panel is never opened (`--no-panel`), so a connected panel and other panel software
are left alone.

By default the suite launches the build output. Set `RADIOBRIDGE_APP` to a packaged single-file
executable (or the folder holding it) to test exactly what users get: `mise run functional:package`
does that for this machine, and [packaging.md](packaging.md) says to do it on the unzipped release
before publishing one. The log of each run records why the app quit (window closed, automation
request, tray menu), which makes an unexpected exit easy to attribute.

Each run keeps the app's settings, log and console output in its `runs/<timestamp>` folder. Look
there first when a scenario fails. The failure message also includes the last displays and sim state.

### Scenarios

- The displays show the simulator's radios, and the display report matches the text.
- The inner knob steps COM1 standby through 8.33 kHz channels, skipping unused channel names.
- The outer knob changes whole MHz and wraps at the band edge.
- ACT/STBY swaps active and standby, also on radios that swap only when the press ends, like REP's
  KX165 in the SF-260 (emulator only).
- A fast spin accelerates, and afterwards the panel and the simulator agree.
- A change made in the cockpit shows on the panel.
- A 25 kHz-only radio that snaps an 8.33 step back is shown truthfully, and a long press fixes the
  step (emulator only).
- A long press on COM toggles 8.33/25 kHz and flashes the new spacing.
- NAV2 on the lower row tunes, wraps and swaps.
- ADF tuning, the DME display (emulator only), transponder digit pairs.
- Altimeter editing, standard pressure on a hold, and the inHg display.
- A hold on the squawk sets the configured VFR code (7000 and 1200).
- The classic COM display setting applies immediately.
- Knob turns, a held ACT/STBY and a long press are reported for the window's knob and switch view.
- Test mode drives the panel without a simulator and steps aside when one connects (emulator only).
- The app survives the simulator quitting and restarting (emulator only).
- An X-Plane that refuses connections is explained in a notice, which goes away once it is fixed
  (emulator only).
- An X-Plane that answers late (3 s here; 30 s in GitHub issue #1) doesn't make the panel spring back:
  it keeps what was set, says X-Plane is slow, and X-Plane ends up with the right value (emulator only).
- With GPS control on, ADF and DME type a Direct-to into the GNS (D→, letters, ENT, ENT), choose and
  press FPL, and hold for CRSR and CLR; the lower row drives GPS 2 by default. The emulator checks the
  exact sequence of GNS clicks and that none started while the same command was still held (emulator only).
- A debug recording of a knob turn and two swaps holds the whole story: the panel input, the
  controller's decisions, the displays, what went to the simulator and its replies, the values that
  came back by name, and the log.

The MSFS side is covered by unit tests (the library search order on a pretend machine, and the
notices) and by `mise run functional:msfs` against a live sim. The latter **changes the aircraft's
radios**. A read-only check that RadioBridge finds the sim's library and reads its radios is to run
the packaged exe from an empty folder with `--headless --automation <port>` while MSFS is running,
then read `GET /api/state`.

## Automation API

The API is off unless the app is started with `--automation PORT`. It listens on `localhost` only,
is meant for testing and tooling, and has no authentication.

| Request | Body | Returns |
|---|---|---|
| `GET /api/state` | | Panel connection, selectors and per-row knob/button activity, display text, display report (hex), the active simulator and its raw `RadioState`, settings |
| `POST /api/panel/report` | `{"bits": 1}` | Feeds one 24-bit input report into the virtual panel and returns the decoded events |
| `GET /api/settings` | | Current settings |
| `PUT /api/settings` | settings object | Replaces the settings |
| `POST /api/recording` | `{"on": true}` | Starts (or with `false` stops) the debug recording; returns whether it runs, its file and size. `GET /api/state` includes the same. |
| `POST /api/shutdown` | | Exits the app |

To try it by hand, start the emulator and the app in two terminals, then select COM1 on the upper
row and turn the inner knob one detent:

```bash
mise run emulator
```

```bash
dotnet run --project src/RadioBridge.Desktop -- --no-panel --automation 47800
```

```bash
curl -s -X POST localhost:47800/api/panel/report -d '{"bits": 1}'
```

```bash
curl -s -X POST localhost:47800/api/panel/report -d '{"bits": 65537}'
```

```bash
curl -s -X POST localhost:47800/api/panel/report -d '{"bits": 1}'
```

```bash
curl -s localhost:47800/api/state
```

`65537` is `1 << 16 | 1`: COM1 selected and the upper inner knob's clockwise bit. The report after
it clears the knob bit again, so the next detent is a new rising edge.

## X-Plane emulator

`XPlaneEmulator` implements the subset of the X-Plane 12 Web API (v2) that radio tools use:

- `/api/capabilities`
- dataref and command lookup with `filter[name]`, and the full dataref list
- dataref `GET` and `PATCH`, and command activation
- the WebSocket with subscribe, unsubscribe, set and command messages (held for a `duration`, or begun
  and ended separately), and pushes of changed values every 100 ms

Behind the API is a small radio model. COM radios snap to 8.33 or 25 kHz channels
(`Com833Capable`), the `_833` and 10 kHz COM datarefs stay in sync, the flip commands swap, and
`barometer_2992` sets 29.92125 like the real thing. With `FlipOnRelease` the radios behave like REP's
KX165: they swap when a held flip command is released, and ignore a press shorter than a frame. With
`RequestDelay` every WebSocket request is handled that much later, in order, like an X-Plane that has
fallen behind. The GNS 430/530 commands (`sim/GPS/g430n1_*`, `g430n2_*`) aren't modelled, only counted:
`GnsPresses` lists each key press and knob click in order with how long it was held, and a press that
starts while the same command is still held is left out (`IgnoredGnsPresses`), so tests catch lost clicks.
`AddDataref` adds datarefs such as an add-on's, for the recording's watch list. It defines its own
dataref names from X-Plane's `DataRefs.txt` instead of sharing RadioBridge's, so a wrong name on
either side makes a test fail.

Run it on its own with `mise run emulator`, which listens on port 8086 like X-Plane. Add `--com25` for
25 kHz-only radios, `--flip-on-release` for REP-style radios, `--lag 30` to answer 30 s late, `--refuse` to act like X-Plane with
"Disable incoming traffic" on (to see the window's notice banner), or `--api1` to act like an X-Plane
too old for Web API v2.
