# Catalog review 2 (1.1.0): non-blocking notes

From the catalog maintainer on the listing PR, 2026-09-30. Nothing here blocks
1.1.0; these are for a later release. PowerStation is the first listing
through the new catalog gates: the package security scan is clear, and the
offline rebuild from `00e8a6e` matches the ZIP exactly.

## 1. Safety-timer renewal during TX (PowerStation fix, next version)

The maintainer agrees that renewals should continue during TX. The narrow
edge: a renewal sends "on + timer" based on the poll that just ran, so an
output switched off at the device (Shelly app, wall switch, device's own timer)
between that poll and the renewal would be switched back on.

Suggested fixes (either one closes it):

- Re-read that one channel just before renewing, and renew only if it is
  still on. Applies at all times, not only during TX.
- Skip renewal during TX when the timer still has plenty of margin (for
  example more than half its period left), and renew right after TX settles.

Where: `DeviceManager.RenewSafetyTimersAsync` (called from the poll). Leaning
toward the re-read, since it closes the gap in and out of TX; the extra read is
one request per renewal (a third of the timer period). Add a test with the
simulator switching the output off between the poll and the renewal. Mention
the change in the reply for the next round.

## 2. Hardware CW keying (Zeus host, nothing to change here)

Zeus raises the plugin MOX event for MOX, TUN and two-tone. The maintainers
still need to confirm whether a radio's own hardware CW keyer raises it too.
If it doesn't, that's a Zeus host fix, not a PowerStation change.

For us: when the operator can, test with the radio's hardware keyer (see
HARDWARE-TESTING.md, TX interlock) and share the result with the maintainer.
Until confirmed, the TX interlock may not see hardware-keyed CW.
