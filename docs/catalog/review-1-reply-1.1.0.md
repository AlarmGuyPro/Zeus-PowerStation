Thanks for the careful review, Doug. All three points are fixed in **1.1.0** (tag `v1.1.0`, commit `00e8a6e`), and I've updated this PR to 1.1.0 with a new ZIP, hash, custody URL and `source` block. 1.0.0 was never listed, so the entry now carries only 1.1.0.

**1. Auto-refind and credentials**
- Every relocation target must be a local IP. mDNS answers whose A record isn't private/link-local are dropped in `ShellyMdns.ParseResponse`, and both the scan and refind paths check `HostValidator.IsLocal` again before probing. `RelocateIfMovedAsync` also checks the address itself (`HostValidator.IsLocalHost`), so nothing reaches it that isn't a LAN IP.
- A device with a stored credential (Gen1 Basic password or Gen2 HA1) is **never moved automatically**. PowerStation records the new address and shows it on the device card ("New address found"), and nothing but the unauthenticated `/shelly` identify has gone to that address. **Use** (`POST devices/{id}/move`) re-validates the address, confirms the device ID and then switches over. **Ignore** (`DELETE devices/{id}/move`) keeps the old address and doesn't offer that one again. Devices without a password still move on their own.
- Tests start a password-protected Gen2 and Gen1 device at a new address and assert that no RPC, digest or Basic auth header reaches the new address before confirmation, and that Ignore sticks. I also checked that both tests fail against the old behaviour.

**2. TX interlock**
- New `TxInterlock`: outputs are locked while MOX is on **and for 3 s after unkey** (CW break-in / VOX). It reads `IRadioStateReader.Mox` at check time.
- Rules: deferred rules are no longer flushed on MOX-false. The clock releases them only once the settle time has passed, and `ExecuteAsync` re-checks after acquiring the run lock (a rule that was waiting its turn goes back in the queue if the radio keyed meanwhile). `ApplyTargetsAsync` also checks before **each** output, so a multi-output action stops partway if the radio keys.
- Manual channel commands and scene runs return **409** during TX or the settle time, like the PW2 Bridge, and the panel shows a Transmitting notice.
- The on-air light (TX-trigger rule, single output) is the only exception.
- Two things I kept deliberately, tell me if you'd rather they change:
  - Safety-timer renewals continue during TX. A renewal only re-asserts "on" plus the timer for an output the same poll just saw on, so it never changes state. Stopping renewals during a long over could let the Shelly's own timer switch an amplifier off mid-transmission.
  - Zeus-close rules wait out the settle time within the shutdown budget. If Zeus closes while still keyed, they switch nothing and log it; the device-side safety timers cover that case.
- Tests cover re-keying during the settle time, the re-check before switching, the 409s, and a scene stopping partway.

**3. Smaller items**
- `HostValidator` now refuses loopback (`127.0.0.0/8`, `::1`, mapped forms). Only the test suite enables it (`HostValidator.AllowLoopback`, internal); the plugin never sets it.
- `api.ts` encodes the channel kind and index with `encodeURIComponent`.

**New catalog requirements**
- `zeus-build.json` and `global.json` (SDK 10.0.112, `rollForward: disable`) are at the repo root, and both of our projects have NuGet lock files. The browser bundle now builds to `ui/dist`, outside the .NET project folder. The vendored SDK is unchanged from this repo.
- I ran your tools from current `main` against the 1.1.0 ZIP:
  - `verify-source-build.ps1` from a fresh clone of `00e8a6e`: **clear** (0 fail, 0 review).
  - `PackageSecurityScan scan`: **clear** (0 fail, 0 review, 3 info for the declared network use, capabilities and the licence banner link).
  - `validate-package.ps1`, `validate-registry.ps1`, `validate-community-submission.ps1` and the schema checks: pass.
- 88 tests pass (80 before). I tested 1.1.0 installed in Zeus on my station.

73, KQ4WLR
