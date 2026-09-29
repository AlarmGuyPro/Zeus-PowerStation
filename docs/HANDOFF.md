# PowerStation handoff

Everything someone picking this project up needs: what it is, where things
live, how to build, test and release it, what's been decided, and what's open.
Start here, then read [DECISIONS.md](DECISIONS.md) for the why.

## What it is

A Zeus SDR community feature (plugin) by KQ4WLR that controls and monitors
Shelly devices on the shack LAN: relays, plugs, dimmers, colour LED
controllers and energy meters, Gen1 through Gen4. It adds scenes, rules driven
by Zeus (start/close, band, frequency, TX on-air light, idle, time of day),
device-side safety timers, normal ranges for mains voltage and current, and a
traffic log for troubleshooting.

- Plugin ID `io.github.alarmguypro.powerstation`, GPL-2.0-or-later.
- Zeus SDK ABI 1, minimum 1.5.0. Capabilities: `ReadRadioState` (read only),
  `NetworkAccess`, `PersistSettings`. Never `ControlRadio`, never PureSignal,
  never keys the transmitter.
- Current version: see `src/PowerStation/plugin.json` and
  [CHANGELOG.md](../CHANGELOG.md).

## Status (2026-09-29, 1.0.0)

Working on the operator's hardware: Shelly 1 Gen4 (two), Plus Wall Dimmer,
ShellyEM (Gen1, "Generator Output"), Plus RGBW PM (RGBW mode). Cross-VLAN
scanning works. Everything else is covered by the simulators in the test suite
but hasn't been on real hardware; see [HARDWARE-TESTING.md](HARDWARE-TESTING.md).

1.0.0 is the first catalog release (Debug log off by default). Submission
steps and status:
[CATALOG-SUBMISSION.md](CATALOG-SUBMISSION.md).

## Repository layout

```
src/PowerStation/                 The feature (packaged)
  PowerStationPlugin.cs           Entry point: wires services, starts polling and rules, maps endpoints
  plugin.json                     Manifest (version, capabilities, panels)
  README.md                       Operator documentation (ships in the ZIP)
  build-package.ps1               Zeus template packaging script (unchanged)
  Shelly/
    Gen2Client.cs                 Gen2+ JSON-RPC (/rpc): Switch, Light, RGB, RGBW; SHA-256 digest auth; identify (/shelly)
    Gen1Client.cs                 Gen1 REST (/status, /settings, /relay, /light), HTTP Basic
    LenientJson.cs                Parses device replies; repeated keys: last one wins (RGBW PM quirk)
    ShellyDigest.cs               Digest (RFC 7616, SHA-256) helpers
    HostValidator.cs              Addresses must be LAN (private, link-local, loopback, CGNAT)
  Discovery/
    ShellyMdns.cs                 Minimal Shelly-only mDNS query/parse (no dependencies)
    NetworkScanner.cs             Sweep of saved networks (/20 max, 4,096 addresses)
  Services/
    DeviceManager.cs              Devices, clients, polling, commands, scenes' outputs, safety-timer renewal
    Scenes.cs                     Scenes and validation (colour/white rules live here too)
    Automations.cs                Rules engine: triggers, debounce/delay, TX deferral, idle, time of day, put-back
    ReadingsService.cs            Normal ranges, alerts, mains events, event log, device ratings
    DiscoveryService.cs           Scans and automatic re-find after DHCP changes
    TrafficLog.cs                 Debug log (memory only, bounded)
    DeviceStore.cs                Settings persistence (JSON strings per key)
  Model/Models.cs                 Records shared across the backend
  Api/PowerStationEndpoints.cs    HTTP endpoints (see docs/API.md)
ui/                               React + TypeScript panels, bundled by esbuild into src/PowerStation/ui/powerstation.js
  src/PowerStationPanel.tsx       Panel shell: header, gear, setup sections
  src/ControlsPanel.tsx           Main view: mains banner, Scenes box, Devices box, tiles (switch, wall dimmer, meter)
  src/color.tsx                   Colour tile and picker
  src/layout.tsx                  Column grid and arrange
  src/scenes.tsx, automations.tsx, readings.tsx, discovery.tsx, debug.tsx, DevicesPanel.tsx   Setup sections
  src/styles.ts                   All CSS, scoped under the root class, Zeus tokens only
  preview/mockup.tsx              Clickable mockup: real panels + pretend backend + "Pretend Zeus" bar
  preview/build-mockup.mjs        Builds preview/dist/powerstation-mockup.html
tests/PowerStation.Tests/         Dependency-free runner; Gen1 and Gen2 device simulators, pretend radio, manual clock
tools/screenshots/                Renders docs/screenshots and docs/promo from the mockup
docs/                             This handoff, decisions, API, hardware tests, catalog steps, screenshots, promo image
sdk/                              Zeus plugin contracts, vendored unchanged
```

## How it works (short)

- **Polling.** Each device is polled every 2 s (backoff when it's offline).
  A poll reads status, applies names, renews safety timers for outputs it just
  saw on, then hands the channels to ReadingsService.
- **Devices are tracked by Shelly device ID**, not address. When one stops
  answering, DiscoveryService looks for it (mDNS, then its old /24 and the
  saved networks) and moves it; a device still answering is never moved.
- **Rules.** AutomationService ticks every 250 ms and on every radio event.
  Lasting conditions (TX, band, frequency) use debounce + delay to start and an
  end wait to finish. Anything except the TX light queues while MOX is on and
  runs when it drops. Idle uses the last activity time (radio events,
  PowerStation actions, "I'm here"). Band comes from frequency using
  PowerStation's own band table.
- **Put back how it was** remembers each output's state before a rule acted,
  and restores only outputs still the way the rule left them.
- **Safety timers** use the device's own flip-back timer (`toggle_after` on
  Gen2+, `timer` on Gen1), renewed at a third of the period, only right after
  a poll saw the output on. If Zeus stops, the device switches off by itself.
- **Readings** compare every metered channel with the mains range and its
  current limits; voltage excursions become one station-wide "Mains" event,
  current excursions are per output. DC devices (RGBW PM) skip the mains check.
- **UI** polls `GET status` every 2 s and applies command results immediately.

## Build, test, package

Needs .NET 10 SDK, Node.js 22, PowerShell 7. No NuGet packages are used (only
the .NET and ASP.NET Core shared frameworks), which keeps the plugin small and
the build reproducible.

```sh
cd ui && npm ci && npm run typecheck && npm run build && cd ..
dotnet build Zeus.PowerStation.slnx -c Release
dotnet run --project tests/PowerStation.Tests -c Release --no-build     # exit code = failures
pwsh src/PowerStation/build-package.ps1
# → artifacts/io.github.alarmguypro.powerstation/io.github.alarmguypro.powerstation-<version>.zip
```

Validate with the Zeus catalog tools (from a clone of Zeus-SDR/zeus-community-features):

```sh
pwsh tools/validate-package.ps1 -PackagePath <zip> -ExpectedId io.github.alarmguypro.powerstation \
  -ExpectedVersion <version> -ExpectedSdkAbi 1 -ExpectedSdkMinVersion 1.5.0 \
  -ManifestSchemaPath schema/plugin.schema.json
```

CI (`.github/workflows/ci.yml`) does all of this on Linux, Windows and macOS
for every push to main and every pull request.

Tests run a single test by name filter: `dotnet run --project tests/PowerStation.Tests -c Release --no-build -- Automation`.

## Releasing a version

1. Bump the version in `src/PowerStation/plugin.json`,
   `src/PowerStation/KQ4WLR.PowerStation.csproj` and the heading of
   `src/PowerStation/README.md`; add a CHANGELOG entry.
2. Build, test, package, validate (above).
3. Commit, tag `v<version>` on that exact commit, push the tag.
4. Create a GitHub Release for the tag and attach the ZIP (and its `.sha256`).
   Never replace a published ZIP; release a new version instead.
5. Install in Zeus: Features › Community › Install local feature.

UI changes go through the mockup first (`cd ui && npm run mockup`), reviewed by
the operator, on a feature branch; fixes from testing go straight to main.

## Where to look when something's wrong

- **Setup › Debug** in the panel: every command, error and device reply.
  "Record every poll" adds the routine status reads. Ask the operator for a
  Download of the filtered log.
- The Zeus log: PowerStation writes warnings (first failure per device) and
  errors (anything unexpected) through the plugin logger.
- A device card that says "PowerStation couldn't read this device's status"
  names the exception; the reply is in the Debug log.

## Known limits and open items

- Zeus doesn't expose mouse/keyboard activity, radio-connected state, S-meter
  or TX power to plugins; idle is based on radio and PowerStation activity.
- Zeus "closes" is only seen on a clean shutdown; safety timers cover crashes.
- TX following has network lag; the TX rule is limited to an on-air light by
  design.
- Built-in current ratings exist only for some models (see ReadingsService.RatedAmps);
  worth checking against Shelly spec sheets.
- Gen1 RGBW2 isn't supported; Gen2+ has no lighting effects.
- Planned: rules on readings (low-battery cutoff, AC too low/high), optional
  light flash/dim as an idle warning.
- Debug log is off by default (1.0.0). Ask operators to turn it on in
  Setup › Debug before sending a log.
