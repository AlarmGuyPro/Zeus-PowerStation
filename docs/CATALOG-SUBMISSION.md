# Submitting PowerStation to the Zeus community catalog

Summarised from the catalog's own guide,
[Zeus-SDR/zeus-community-features CONTRIBUTING.md](https://github.com/Zeus-SDR/zeus-community-features/blob/main/CONTRIBUTING.md).
If anything here disagrees with that guide, the guide wins.

## Before submitting

- [ ] Hardware checks in [HARDWARE-TESTING.md](HARDWARE-TESTING.md) done for the devices you'll claim.
- [x] Decide the Debug log default for release: off (1.0.0).
- [ ] Version bumped (plugin.json, csproj, operator README heading) and CHANGELOG updated.
- [ ] CI green on Linux, Windows and macOS for the release commit.
- [ ] Screenshot set current (`tools/screenshots`: dark, light, narrow, 200%, keyboard focus, error and first-run states are in `docs/screenshots`). Retake inside Zeus if the reviewers ask for real-host screenshots.

## Publish the release

1. Tag the exact commit: `git tag v<version> && git push origin v<version>`.
2. Create a GitHub Release for the tag and attach
   `io.github.alarmguypro.powerstation-<version>.zip`. Never replace the bytes later.
3. Get the SHA-256 (lowercase) of that exact ZIP:
   `(Get-FileHash -Algorithm SHA256 <zip>).Hash.ToLowerInvariant()` or the `.sha256` file from the build.

## Registry entry

Fork Zeus-SDR/zeus-community-features, branch
`community/io.github.alarmguypro.powerstation-<version>` from `upstream/main`,
and edit only `registry.json`. Update the top-level `generated` timestamp.

```json
{
  "id": "io.github.alarmguypro.powerstation",
  "channel": "community",
  "name": "PowerStation",
  "description": "Control and monitor Shelly relays, plugs, dimmers, colour lights and energy meters on your LAN, with scenes and rules driven by Zeus.",
  "author": "KQ4WLR",
  "license": "GPL-2.0-or-later",
  "homepage": "https://github.com/AlarmGuyPro/Zeus-PowerStation",
  "categories": ["switches"],
  "verified": false,
  "versions": [{
    "version": "<version>",
    "sdkAbi": 1,
    "sdkMinVersion": "1.5.0",
    "platforms": ["any"],
    "downloadUrl": "https://github.com/Zeus-SDR/zeus-community-features/releases/download/community-io.github.alarmguypro.powerstation-v<version>/io.github.alarmguypro.powerstation-<version>.zip",
    "sha256": "<64 lowercase hex>"
  }]
}
```

The `downloadUrl` is the catalog's custody URL (it 404s until a maintainer
copies the ZIP). Your own release URL goes in the pull request template, not
in `registry.json`. `platforms: ["any"]` is right: the package is fully managed.

## Checks and pull request

Run the catalog's local checks (section 7 of their guide), including
`validate-package.ps1` against your ZIP with `-ExpectedSdkAbi 1
-ExpectedSdkMinVersion 1.5.0 -ManifestSchemaPath schema/plugin.schema.json`.

PR title: `feat(registry): add io.github.alarmguypro.powerstation <version>`
(later versions: `release`). Fill in the template:

- Source: https://github.com/AlarmGuyPro/Zeus-PowerStation (tag `v<version>`)
- Intake ZIP URL and SHA-256
- Platforms: any (managed only, no native code, no NuGet dependencies)
- Capabilities, with reasons:
  - `ReadRadioState`: frequency, mode and MOX drive band/frequency/TX/idle rules. Read only.
  - `NetworkAccess`: talks to Shelly devices on the LAN only; addresses are checked to be private/link-local; no Internet, no proxies, no redirects.
  - `PersistSettings`: devices, scenes, rules, layout and readings settings.
- Safety: never keys the transmitter, no ControlRadio, no PureSignal; the TX rule is limited to one on-air light and warns not to rely on it; every other rule waits for RX.
- Credentials: Gen2 passwords stored only as HA1; Gen1 passwords stored as entered (HTTP Basic), disclosed in the UI and README; never returned by the API or logged.
- Test results: CI, test count, catalog validator output.
- Screenshots from `docs/screenshots`.
