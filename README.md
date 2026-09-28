# PowerStation for Zeus

A [Zeus SDR](https://github.com/Zeus-SDR/zeus-community-features) community
feature by **KQ4WLR** for controlling and monitoring Shelly relays, plugs,
dimmers and energy meters on the shack LAN, with rules driven by Zeus.

Operator documentation lives in [`src/PowerStation/README.md`](src/PowerStation/README.md)
and ships inside the package.

## Status

| Phase | Scope | State |
|---|---|---|
| 1 | Gen2+ client with digest auth, manual add, on/off, dimming, live metering, Controls and Devices panels | **Done** (0.1.0) |
| 1.5 | Scenes and groups (outputs + dimmer levels + fade, All off), lighter typography | **Done** (0.2.0) |
| 1.6 | One panel with Status and Setup tabs (first hardware test: Shelly 1 Gen4) | **Done** (0.2.1) |
| 2a | Built-in mDNS, network scan for VLANs, automatic re-find after DHCP changes, output status lamps | **Done** (0.3.0) |
| 2a+ | Column grid with drag-to-arrange (saved), wall-dimmer drawing, orange off lamp | **Done** (0.4.0) |
| 2a+ | Cards snap to equal grid cells that fill the panel (from operator test) | **Done** (0.4.1) |
| 2a+ | Chosen column count honoured down to narrow cards; visible note when the panel is too narrow | **Done** (0.4.2) |
| 3 | Gear-cog setup; Gen1 (ShellyEM, Shelly 1, 1PM, 2.5, Plug/Plug S, Dimmer 1/2); rules on Zeus start/close, TX (on-air light), band, frequency, idle and time of day, with TX deferral and put-back-on-return; device-side safety timers; normal ranges for mains voltage and output current with an event log | **Done** (0.5.0) |
| 3+ | One Save for device settings; 120 V split-phase wording, measured voltage shown, line-to-line option (from operator test) | **Done** (0.5.1) |
| 3+ | Colour lights: Plus RGBW PM in RGB/RGBW mode with colour, level and white on the panel, in scenes and in rules | **Done** (0.6.0) |
| 4 | Rules on readings (low battery, AC out of range), polish, in-Zeus screenshots, catalog submission | Planned |

## Layout

```
sdk/                         Zeus plugin contracts, vendored unchanged (GPL-2.0-or-later)
src/PowerStation/            The feature: C# backend, plugin.json, operator README, build-package.ps1
  Shelly/                    Gen1 REST and Gen2 RPC clients (Switch, Light, RGB, RGBW), digest and Basic auth, address validation
  Discovery/                 Shelly-only mDNS query and network sweep
  Services/                  Devices, scenes, rules, readings, discovery and re-find, persistence, polling
  Api/                       HTTP endpoints under /api/plugins/io.github.alarmguypro.powerstation/
ui/                          React panels (TypeScript), bundled to src/PowerStation/ui/powerstation.js
  preview/                   Local preview with a mocked backend (not packaged)
tests/PowerStation.Tests/    Test runner plus in-process Shelly Gen1 and Gen2 simulators
docs/screenshots/            UI states captured from the mockup harness
```

## Build

Requires the .NET 10 SDK, Node.js 22 and PowerShell 7.

```sh
cd ui && npm ci && npm run build && cd ..
dotnet build Zeus.PowerStation.slnx -c Release
dotnet run --project tests/PowerStation.Tests -c Release --no-build
pwsh src/PowerStation/build-package.ps1
```

The package is written to
`artifacts/io.github.alarmguypro.powerstation/io.github.alarmguypro.powerstation-<version>.zip`.
Install it in Zeus with **Features → Community → Install local feature**.

The tests never touch real hardware: they run against `FakeShellyGen1` and
`FakeShellyGen2`, in-process device simulators (the Gen2 one has its own
independent digest-auth check), a pretend radio and a hand-moved clock.

To preview the panels outside Zeus: `cd ui && node build.mjs --preview`, then
serve `ui/preview/` with any static file server and open `index.html`
(`?theme=light`, `?panel=devices`, `?scenario=empty|error`).

## Design notes

- **Capabilities:** `ReadRadioState`, `NetworkAccess` and `PersistSettings`.
  Radio state (frequency, mode, TX) is only read, to drive rules. There's no
  `ControlRadio`: PowerStation never tunes, keys or touches PureSignal.
- **TX:** only an on-air light may follow TX, and the UI warns not to rely on
  it for safety. Every other rule waits until TX ends.
- **Safety timers:** the Shelly's own flip-back timer (`toggle_after` on
  Gen2+, `timer` on Gen1), renewed only right after a poll sees the output on,
  so the device switches off by itself if Zeus stops.
- **LAN only:** every device address must resolve to a private, link-local or
  loopback address, so the plugin's endpoints can't be used to reach the
  Internet. The HTTP client ignores system proxies and never follows
  redirects.
- **Credentials:** Gen2 passwords are verified against the device's own
  digest challenge and stored only as HA1 = SHA256(admin:realm:password).
  Gen1 uses HTTP Basic, which has no hashed form, so a Gen1 password is
  stored as entered in the Zeus plugin settings; the UI says so.
- **Change tag:** commands carry `tag: "zeus"` so changes made from Zeus are
  identifiable on the device. Firmware that rejects the parameter is detected
  and the tag is dropped for that device.

## License

GPL-2.0-or-later. See `LICENSE` and `THIRD_PARTY_NOTICES.md`.
