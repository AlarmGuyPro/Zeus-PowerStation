# PowerStation for Zeus

A [Zeus SDR](https://github.com/Zeus-SDR/zeus-community-features) community
feature by **KQ4WLR** for controlling and monitoring Shelly relays, plugs and
dimmers on the shack LAN.

Operator documentation lives in [`src/PowerStation/README.md`](src/PowerStation/README.md)
and ships inside the package.

## Status

| Phase | Scope | State |
|---|---|---|
| 1 | Gen2+ client with digest auth, manual add, on/off, dimming, live metering, Controls and Devices panels | **Done** (0.1.0) |
| 1.5 | Scenes and groups (outputs + dimmer levels + fade, All off), lighter typography | **Done** (0.2.0) |
| 2 | Gen1 client, mDNS discovery, subnet sweep and direct query for VLANs, automatic re-find | Next |
| 3 | Automations: Zeus start/stop, MOX, band, mode, frequency, idle with warning pill, restore-on-return, TX deferral, device-side dead-man timers | Planned |
| 4 | Polish, in-Zeus screenshots, catalog submission | Planned |

## Layout

```
sdk/                         Zeus plugin contracts, vendored unchanged (GPL-2.0-or-later)
src/PowerStation/            The feature: C# backend, plugin.json, operator README, build-package.ps1
  Shelly/                    Gen2 RPC client, SHA-256 digest auth, address validation
  Services/                  Device list, scenes, persistence, background polling
  Api/                       HTTP endpoints under /api/plugins/io.github.alarmguypro.powerstation/
ui/                          React panels (TypeScript), bundled to src/PowerStation/ui/powerstation.js
  preview/                   Local preview with a mocked backend (not packaged)
tests/PowerStation.Tests/    Test runner plus an in-process Shelly Gen2 simulator
docs/screenshots/            UI states captured from the preview harness
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

The tests never touch real hardware: they run against `FakeShellyGen2`, an
in-process device simulator with its own independent digest-auth check.

To preview the panels outside Zeus: `cd ui && node build.mjs --preview`, then
serve `ui/preview/` with any static file server and open `index.html`
(`?theme=light`, `?panel=devices`, `?scenario=empty|error`).

## Design notes

- **Capabilities:** `NetworkAccess` and `PersistSettings` only. No radio
  control; `ReadRadioState` will be added with the automations in phase 3.
- **LAN only:** every device address must resolve to a private, link-local or
  loopback address, so the plugin's endpoints can't be used to reach the
  Internet. The HTTP client ignores system proxies and never follows
  redirects.
- **Credentials:** Gen2 passwords are verified against the device's own
  digest challenge and stored only as HA1 = SHA256(admin:realm:password).
- **Change tag:** commands carry `tag: "zeus"` so changes made from Zeus are
  identifiable on the device. Firmware that rejects the parameter is detected
  and the tag is dropped for that device.

## License

GPL-2.0-or-later. See `LICENSE` and `THIRD_PARTY_NOTICES.md`.
