# Submitting PowerStation to the Zeus community catalog

Summarised from the catalog's own guide,
[Zeus-SDR/zeus-community-features CONTRIBUTING.md](https://github.com/Zeus-SDR/zeus-community-features/blob/main/CONTRIBUTING.md).
If anything here disagrees with that guide, the guide wins.

## Status and history

| Date | What happened |
|---|---|
| 2026-09-29 | 1.0.0 released (tag `v1.0.0`) and listing PR opened on Zeus-SDR/zeus-community-features from branch `community/io.github.alarmguypro.powerstation-1.0.0` of the AlarmGuyPro fork. |
| 2026-09-30 | KB2UKA requested changes: refind could send stored passwords to an unconfirmed address; TX interlock gaps (queued rules, manual commands, scenes); loopback accepted; `encodeURIComponent` on the channel kind. The catalog also added `source` blocks, `zeus-build.json`, a security scan and a rebuild-from-source check. |
| 2026-09-30 | 1.1.0 released (tag `v1.1.0`, commit `00e8a6e`, SHA-256 `34204b4e…2ba5`) with all fixes; rebuild check and security scan clear locally. PR branch rebased and updated to 1.1.0 (the entry lists only 1.1.0 because 1.0.0 was never listed); title, description ([catalog/pr-description-1.1.0.md](catalog/pr-description-1.1.0.md)) and reply ([catalog/review-1-reply-1.1.0.md](catalog/review-1-reply-1.1.0.md)) posted. |
| 2026-09-30 | Second review: security scan clear and the offline rebuild from `00e8a6e` matches the ZIP (first listing through the new gates). Two non-blocking notes for a later release, recorded in [catalog/review-2-notes-1.1.0.md](catalog/review-2-notes-1.1.0.md): safety-timer renewal can switch back on an output turned off between poll and renewal; hardware CW keyer may not raise the MOX event (Zeus host side). |

**Next:** the two review-2 notes go into the next version (1.1.1 or 1.2.0):
fix the safety-timer renewal edge, and follow up on hardware CW keying (a Zeus
host question; test with the operator's keyer). See
[catalog/review-2-notes-1.1.0.md](catalog/review-2-notes-1.1.0.md). The new
version follows the "Next round" steps below; the reply should say how each
note was handled.

## Next round (updating the open PR)

1. Fix, bump the version, update CHANGELOG, commit and push to main.
2. Build the ZIP from a clean clone of that commit, then run the rebuild check
   and security scan (below).
3. The operator tests in Zeus, then publishes the GitHub Release `v<version>`
   on that commit with the ZIP and `.sha256`.
4. Verify the release download's SHA-256.
5. On the fork, rebase the PR branch onto `upstream/main` and replace the
   PowerStation version in `registry.json` with the new one (sha256, custody
   `downloadUrl`, `source` block). While the feature isn't listed yet, keep
   only the newest version; once it's listed, add new versions at the top and
   keep the old ones. Update `generated`. Run the registry checks, then
   force-push the branch (with lease).
6. The operator updates the PR title's version, replaces the description
   (start from the newest file in [catalog/](catalog/)), posts a reply
   listing each point and how it was fixed, and re-requests review
   (circular-arrows icon next to the reviewer).

The PR branch name still ends in `-1.0.0`; that's fine, it doesn't need to
match the version.

## Before submitting

- [ ] Hardware checks in [HARDWARE-TESTING.md](HARDWARE-TESTING.md) done for the devices you'll claim.
- [x] Decide the Debug log default for release: off (1.0.0).
- [ ] Version bumped (plugin.json, csproj, operator README heading) and CHANGELOG updated.
- [ ] CI green on Linux, Windows and macOS for the release commit. (1.1.0's
      macOS job failed only because CI didn't install the `global.json` SDK;
      fixed in CI after the release.)
- [ ] Screenshot set current (`tools/screenshots`: dark, light, narrow, 200%, keyboard focus, error and first-run states are in `docs/screenshots`). Retake inside Zeus if the reviewers ask for real-host screenshots.

## Build the ZIP from the release commit

The catalog rebuilds the feature from `source.commit` (zeus-build.json,
global.json SDK pin) and compares it with the ZIP, and the commit hash is
stamped into the DLL. So commit first, then build from a clean clone of that
commit, and don't commit anything else before tagging it:

```sh
git clone --branch <branch> https://github.com/AlarmGuyPro/Zeus-PowerStation build && cd build
(cd ui && npm ci --ignore-scripts && npm run build)
pwsh src/PowerStation/build-package.ps1
```

Then, from a clone of the catalog repo, run their rebuild check against a
second fresh clone (Linux; needs bubblewrap):

```sh
pwsh tools/verify-source-build.ps1 -PackagePath <zip> -SourceDirectory <fresh clone> -OutputDirectory <empty dir>
dotnet tools/PackageSecurityScan/bin/Release/net10.0/PackageSecurityScan.dll scan --package <zip>
```

Both reported CLEAR for 1.1.0. On Linux the rebuild check needs bubblewrap
(`apt-get install bubblewrap`); it works in the Claude workspace.

## Publish the release

The Claude workspace can't push tags or create releases, so the operator does
this on GitHub:

1. Open https://github.com/AlarmGuyPro/Zeus-PowerStation/releases/new
2. Choose a tag: type `v<version>` (check it matches plugin.json) and pick
   "Create new tag on publish". Target: main, which must still be the commit
   the ZIP was built from.
3. Title `PowerStation <version>`; attach
   `io.github.alarmguypro.powerstation-<version>.zip` and its `.sha256`.
   Publish. Never replace the bytes later.
4. Check that
   `https://github.com/AlarmGuyPro/Zeus-PowerStation/releases/download/v<version>/io.github.alarmguypro.powerstation-<version>.zip`
   downloads and its SHA-256 matches the `.sha256` file.

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
    "sha256": "<64 lowercase hex>",
    "source": {
      "repository": "https://github.com/AlarmGuyPro/Zeus-PowerStation",
      "commit": "<40-character commit the ZIP was built from>",
      "package": "https://github.com/AlarmGuyPro/Zeus-PowerStation/releases/download/v<version>/io.github.alarmguypro.powerstation-<version>.zip"
    }
  }]
}
```

The `downloadUrl` is the catalog's custody URL (it 404s until a maintainer
copies the ZIP). Your own release URL goes in `source.package` and the pull
request template, never in `downloadUrl`. `platforms: ["any"]` is right: the package is fully managed.

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
