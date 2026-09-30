# Hardware test checklist

What's been confirmed on real devices, and what still needs checking. Tick
items as they're verified and note the firmware version.

## Confirmed (2026-09-28)

- [x] Shelly 1 Gen4: add, on/off, names (0.2.x onward)
- [x] Plus Wall Dimmer: level dots, − / +, on/off (0.4.x)
- [x] ShellyEM (Gen1): relay and both clamp meters; renaming meters (0.5.1)
- [x] Plus RGBW PM, RGBW mode: connects after the repeated-key fix (0.6.2)
- [x] Scan across VLANs when the networks are listed (0.3.0)
- [x] 1.1.0 in Zeus: output buttons locked while transmitting, with the "Zeus is transmitting" message (2026-09-30)

## To check

Safety timers
- [ ] Give an output a 1–2 minute safety timer, close Zeus: the output turns off by itself.
- [ ] Turn a protected output off by hand while Zeus runs: it stays off (the device's pending timer is cancelled).
- [ ] Gen1 relay timer behaves the same.

Rules
- [ ] On-air light follows TX with an acceptable lag; hold keeps it on between overs.
- [ ] Band / frequency rules with debounce while tuning across a band.
- [ ] Idle countdown, I'm here, +30 min, and put back how it was on return.
- [ ] Zeus start rule with delay; Zeus close rule on a normal shutdown.
- [ ] Nothing but the on-air light switches while transmitting.

TX interlock (1.1.0)
- [ ] Run a scene while keyed: refused. Unkey and wait 3 s: it works.
- [ ] A band rule that comes due during TX runs about 3 s after the last unkey, not between CW words or VOX gaps.
- [ ] The on-air light still follows TX.
- [ ] Key CW with the radio's own hardware keyer (not MOX/TUN): does the on-air light follow and are outputs locked? Report the result to the catalog maintainer (review 2 note: Zeus may not raise the MOX event for it).
- [ ] (After the renewal fix) Output with a safety timer turned off in the Shelly app right after a poll: it stays off.

New address for a device with a password (1.1.0)
- [ ] Give a password-protected device a new DHCP address (or change its reservation): its card shows "New address found" and it is not moved on its own.
- [ ] Use: the device comes back online at the new address.
- [ ] Ignore: the notice goes away and the device keeps its old address.

Colour lights (Plus RGBW PM)
- [ ] Colour, level and white respond within about half a second; presets and custom colour.
- [ ] RGB mode (no white slider) and Light mode (four dimmers).
- [ ] Scene fade into a colour; rule sets a colour; put-back restores it.

Readings
- [ ] Current matches the Shelly app (the EM's current is derived from power and reactive power).
- [ ] EM clamp size: 50 A default; change it if yours is 120 A.
- [ ] A 240 V load marked "wired across two legs" doesn't warn.
- [ ] Built-in current ratings against the device spec sheets.

Other models (simulator-tested only)
- [ ] Pro 4PM / Pro 3, Plug US, Ogemray 25A
- [ ] Gen1 1PM, 2.5 (relay mode), Plug / Plug S, Dimmer 1/2

Debug log
- [ ] Stays small over a few days (memory only, 500 entries, 24 h).
- [ ] Off switch stops and clears it, and stays off after a Zeus restart.
