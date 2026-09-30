## Community feature release

- Feature ID: io.github.alarmguypro.powerstation
- Version: 1.1.0 (replaces 1.0.0, which was never listed; review fixes in the comments below)
- Source repository (public): https://github.com/AlarmGuyPro/Zeus-PowerStation (tag `v1.1.0`, commit `00e8a6e2583c66e2659be0c869893039e7fb95c3`)
- Contributor intake GitHub Releases HTTPS ZIP: https://github.com/AlarmGuyPro/Zeus-PowerStation/releases/download/v1.1.0/io.github.alarmguypro.powerstation-1.1.0.zip
- SHA-256 (lowercase): 34204b4e2445a59d15ac0bbc2d77c8d705a562dbf7e68a0f619631e6b92a2ba5
- Expected Zeus-SDR custody URL: https://github.com/Zeus-SDR/zeus-community-features/releases/download/community-io.github.alarmguypro.powerstation-v1.1.0/io.github.alarmguypro.powerstation-1.1.0.zip
- Platforms: any (fully managed; no native code, no NuGet dependencies, only the .NET and ASP.NET Core shared frameworks)

## Submission checklist

- [x] This pull request adds one feature or one new version of one feature.
- [x] This listing pull request changes only `registry.json`.
- [x] The entry uses `channel: "community"`, `verified: false`, and no
      `subscription` field.
- [x] The embedded `plugin.json` and catalog agree on ID, version, SDK ABI, and
      minimum SDK version.
- [x] The intake ZIP is the exact locally tested artifact, is available over
      HTTPS, and will not be replaced at this versioned URL.
- [x] The public source repository contains the complete corresponding source,
      build files, license, notices, and a tag or commit for this exact version.
- [x] The feature uses only the public SDK/browser API and does not contain,
      reconstruct, or depend on private Zeus logic or undocumented endpoints.
- [x] PureSignal is untouched and the feature never auto-keys a transmitter.
- [x] Capabilities and permissions are complete and no broader than necessary.
- [x] Package license, third-party notices, manifest, and catalog metadata agree.
- [x] UI CSS follows the token, selector-scoping, theme, scaling, keyboard, and
      state guidance in `CONTRIBUTING.md`, or this feature has no UI.
- [x] For a visual feature, I attached the required dark/light, normal/narrow,
      200%-scaling, keyboard-focus, and applicable state screenshots below.
- [x] I installed the ZIP locally and tested the declared platforms and operator
      states. Results are described below.
- [x] All contributor-side commands in "Required local checks" pass. I
      understand the custody-download check becomes green only after maintainer
      intake.

## Capability and safety review

**Capabilities**
- `ReadRadioState`: frequency, mode and MOX drive the band, frequency-range, TX (on-air light) and idle rules, and the TX interlock. Read only.
- `NetworkAccess` (`permissions.network: true`): talks only to Shelly devices on the LAN (Gen1 HTTP REST, Gen2+ RPC over HTTP, Shelly-only mDNS). Every address is checked to be private or link-local before use (loopback is refused); no Internet hosts, no proxies, redirects not followed. mDNS answers pointing off the LAN are dropped. The operator-started network scan is limited to saved private networks, /20 (4,096 addresses) maximum.
- `PersistSettings`: devices, scenes, rules, layout, readings settings and the debug on/off choice.
- No file-system read or write.

**Radio control and transmit**
- No `ControlRadio`; never changes frequency, mode or any radio setting. Never keys the transmitter. PureSignal is not referenced.
- TX interlock: no output changes while MOX is on or for 3 s after unkey (CW break-in, VOX). Manual output commands and scene runs return 409 with a message. Rules that come due wait and re-check MOX just before switching, and a multi-output action stops if the radio keys partway.
- The only exception is a single on-air light rule, and the editor warns not to rely on it for safety.
- Device-side safety timers (the Shelly's own flip-back timer) switch protected outputs off if Zeus stops. Renewals continue during TX; they only re-assert "on" for an output the same poll just saw on.

**Credentials**
- Gen2+ passwords are stored only as the digest HA1. Gen1 devices use HTTP Basic, so their passwords are stored as entered; this is disclosed where they're entered and in the operator README. Passwords are never returned by the API or written to the debug log.
- A device with a stored password is never moved to a new address automatically. The operator confirms the new address on the device card first, and until then nothing but the unauthenticated `/shelly` identify is sent there.

**Debug log**
- Off by default. When the operator turns it on: memory only (never saved), newest 500 entries, requests and replies cut to 2,000 characters, nothing older than 24 hours; passwords and auth headers are never recorded.

**Provenance**
- `sdk/Zeussdr.Zeus.Plugins.Contracts` is vendored unchanged from this repository for compilation only (not in the ZIP). All other code is original to this feature, GPL-2.0-or-later.

## Validation evidence

- Build and tests: .NET 10 (SDK 10.0.112, pinned in `global.json`) on Linux x64; 88/88 tests pass (Gen1/Gen2 device simulators, fake radio, controlled clock; includes TX interlock, confirmed-move and loopback tests). CI builds and tests on Linux, Windows and macOS.
- Catalog checks run against this ZIP from current `main`:
  - `verify-source-build.ps1` from a fresh clone of `00e8a6e`: clear.
  - `PackageSecurityScan scan`: clear (0 fail, 0 review, 3 info).
  - `validate-package.ps1`, `validate-registry.ps1`, `validate-community-submission.ps1` and the schema checks: pass.
- Real hardware, 1.1.0 installed in Zeus: Shelly 1 Gen4 (×2), Plus Wall Dimmer, ShellyEM Gen1 (relay and both clamp meters), Plus RGBW PM in RGBW mode; devices on a separate VLAN found by the network scan. While transmitting, output buttons are locked and show the "Zeus is transmitting" message.
- Other listed models (Gen1 1PM, 2.5, Plug/Plug S, Dimmer 1/2; Pro 3, Pro 4PM, Ogemray 25A) are covered by the simulators but not yet on real hardware; the source repo's HARDWARE-TESTING.md tracks this.
- Failure states: device offline, wrong password, unreadable status (shown on the card), backend error, empty first run, transmitting (outputs locked), device with a password seen at a new address (confirm or ignore).

### UI screenshots (required for visual features)

<img width="1232" height="1152" alt="scene-editor-colour-dark" src="https://github.com/user-attachments/assets/c739db36-65cf-4462-a63d-3a2a7bff7332" />
<img width="1232" height="1152" alt="rule-editor-tx-dark" src="https://github.com/user-attachments/assets/2c459698-505e-465d-bd1d-ba9068165cd9" />
<img width="1232" height="1152" alt="keyboard-focus-light" src="https://github.com/user-attachments/assets/2ec7e2cd-73d9-4250-94b6-44fd890394c3" />
<img width="522" height="92" alt="idle-pill-dark" src="https://github.com/user-attachments/assets/0fa35086-53ba-41a5-9bbc-629ed4f07832" />
<img width="852" height="802" alt="first-run-dark" src="https://github.com/user-attachments/assets/e7be003c-61f3-49ba-ae18-a7c4f9faf85b" />
<img width="852" height="502" alt="backend-error-light" src="https://github.com/user-attachments/assets/300398f1-c823-4de4-818f-89d1a34d313c" />
<img width="1184" height="1804" alt="status-200pct-dark" src="https://github.com/user-attachments/assets/c34c67f6-d1ee-4f2b-9314-36fb71b4c558" />
<img width="382" height="1302" alt="status-narrow-dark" src="https://github.com/user-attachments/assets/94c359a9-876d-4427-b028-18a0eab22e37" />
<img width="1232" height="1152" alt="status-light" src="https://github.com/user-attachments/assets/b553292f-0156-4988-bd0b-99ca6da7100e" />
<img width="1232" height="1152" alt="status-dark" src="https://github.com/user-attachments/assets/a8796ec9-209c-4aae-b0f7-82e7a40a624c" />
<img width="1232" height="1152" alt="status-alerts-idle-dark" src="https://github.com/user-attachments/assets/0f2e7dd6-0c60-4861-953f-7b25bf3c1964" />
<img width="1232" height="1152" alt="setup-readings-dark" src="https://github.com/user-attachments/assets/44e4919f-ad16-4a58-9502-6d16a2569772" />
<img width="1232" height="1152" alt="setup-layout-dark" src="https://github.com/user-attachments/assets/0f4081fc-3630-46d6-8a07-99d3d783b88c" />
<img width="1232" height="1152" alt="setup-devices-scan-dark" src="https://github.com/user-attachments/assets/3dca5ad3-7926-431a-80f0-9f39ab510e25" />
<img width="1232" height="1152" alt="setup-debug-dark" src="https://github.com/user-attachments/assets/7de377e1-94b3-4af4-893c-5f3352fded23" />
<img width="1232" height="1152" alt="setup-automations-dark" src="https://github.com/user-attachments/assets/86ef5352-2743-4abe-b65e-be9e8fa072f2" />
<img width="1232" height="1152" alt="scene-editor-dark" src="https://github.com/user-attachments/assets/c4018995-5985-4e10-acc4-a20472bba088" />

- Status, dark / light: status-dark.png, status-light.png
- Narrow: status-narrow-dark.png
- 200% scaling: status-200pct-dark.png
- Keyboard focus: keyboard-focus-light.png
- Empty / first run: first-run-dark.png
- Error: backend-error-light.png
- Warning (readings and idle): status-alerts-idle-dark.png, idle-pill-dark.png
- Transmit (on-air rule): rule-editor-tx-dark.png
- Setup: setup-devices-scan-dark.png, setup-automations-dark.png, setup-readings-dark.png, setup-layout-dark.png, setup-debug-dark.png, scene-editor-dark.png, scene-editor-colour-dark.png
- These screenshots are from 1.0.0. 1.1.0 adds two small notices in the same style: "Transmitting…" at the top of the panel, and "New address found" with Use / Ignore on a device card.

## Review and publication

Maintainer custody gate (completed by either maintainer after content review):

- [ ] Source, provenance, permissions, UI, licensing, and operator-safety
      evidence have been reviewed.
- [ ] The protected-main custody workflow mirrored and re-downloaded the exact
      SHA-256-verified bytes without executing feature code.
- [ ] `registry.json` uses the resulting immutable Zeus-SDR custody URL and all
      required checks are green.

I understand that either Douglas J. Cerrato (KB2UKA / `@Kb2uka`) or Christian
Suarez (N9WAR / `@iamexemplar`) may validate and merge this contribution. After
merge to protected `main`, Zeus will include the listing in
**Features → Community** after its catalog cache refreshes; users must still
choose to install it.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01KsNUHLWcZ7nKjQv8RhSPQq
