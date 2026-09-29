# PowerStation: decisions and why

What was decided with the operator (KQ4WLR) and the reasons, newest first.
Kept in step with the project notes. For how to build and release, see
[HANDOFF.md](HANDOFF.md).

## 1.0.0 catalog release (2026-09-29)
- Version 1.0.0 is the first submission to the Zeus community catalog.
- Debug log off by default; operators turn it on in Setup › Debug when asked
  for a log.

## 0.6.3 polish (2026-09-28)
- The Devices section on the panel uses the same labelled box as Scenes
  (accent left edge), so the two read as one design.
- Debug log bounds: memory only (never saved), newest 500 entries, each
  request/reply cut to 2,000 characters, nothing older than 24 hours. "Record
  every poll" still switches itself off after 30 minutes.
- Debug log on/off switch in Setup › Debug, saved in `debug.v1`. Off stops
  recording and clears the log. Off by default from 1.0.0 (decided
  2026-09-29 for the catalog release; `DeviceStore.LoadDebugEnabledAsync`).
- Capabilities: ABI 1 grants the declared capabilities on install; Zeus doesn't
  show an approval prompt for them.

## 0.6.1–0.6.2 debug log and the RGBW PM fix
- Setup › Debug: a traffic log of every command, error and device reply (Gen2
  RPC, Gen1 HTTP, /shelly identification, events). Routine status polls
  (Shelly.GetStatus/GetConfig, Sys.GetStatus, /status, /settings) are kept only
  while "Record every poll" is on. Filters: device, text, errors only, hide
  polls; pause, copy, download. Passwords and auth headers are never recorded.
- An unexpected exception while reading a device sets the card to an error
  with the message instead of leaving it on "Connecting…". Unexpected API
  errors return 500 with the exception type and message.
- Root cause found with the log: the Plus RGBW PM's Shelly.GetConfig repeats
  `button_fade_rate`, and System.Text.Json's JsonObject throws on duplicate
  keys. All device replies now go through LenientJson (last value wins); a
  failure reading output names can't stop the status poll.

## 0.6.0 colour lights
- Plus RGBW PM: RGB mode has `rgb:0`, RGBW mode `rgbw:0`, Light mode four
  `light:N` dimmers. The mode is chosen in the Shelly app (it reboots the
  device); PowerStation doesn't switch modes.
- RGB.Set / RGBW.Set (on, brightness 1–100, rgb 0–255 ×3, white 0–255 on RGBW,
  transition_duration). A colour change alone turns the light on.
- Colour tile: LED-strip preview, on/off, level, ten presets plus custom, white
  slider for RGBW; the status lamp is a square in the actual colour so orange
  isn't read as "off".
- Scenes and rule actions carry colour and white; put-back restores colour; a
  TX rule on a colour light defaults to red.
- The RGBW PM reports its 12/24 V DC supply, so its voltage isn't judged
  against mains. Current limits still apply (no built-in rating).
- No lighting effects (the Gen2+ API has none). Gen1 RGBW2 not supported.

## 0.5.1 fixes from the first 0.5.0 test
- One "Save changes" per form (the operator found several Save buttons
  confusing; renamed meters looked unsaved).
- The EM legs (~121–127 V) were flagged low because the mains setting was on
  230 V. The 120 V preset is labelled "US split phase" and explains that
  Shelly devices measure each leg to neutral; setup shows measured voltage and
  warns when it doesn't match.
- Per-device "Wired across two legs (line to line)": checked against twice the
  range, events labelled "Mains (line to line)".

## 0.5.0 layout and Gen1
- Dimmer level text smaller and centred; + green, − orange; narrow tiles stack.
- Scenes and Devices in their own labelled areas on the panel.
- A gear cog in the panel's top row opens setup (Zeus owns the title bar).
  Sections: Devices, Scenes, Automations, Readings, Layout, Debug.
- One "PowerStation" panel plus an optional "PowerStation Idle" panel.
- Gen1: ShellyEM (relay + two read-only clamp meters; current derived from
  apparent power), Shelly 1; also 1PM, 2.5 relay mode, Plug/Plug S, Dimmer 1/2
  (simulator-tested only). HTTP Basic, optional user name (default admin).
  Gen1 passwords are stored as entered, with a warning where they're entered.

## Readings: normal ranges
- 120 V preset (per leg): ANSI C84.1 Range A 114–126 V normal, Range B
  110–127 V limit. 230 V preset: ±6% normal, ±10% limit (EN 50160). Or custom.
- Voltage is one supply, so it's reported once for the station (banner plus
  header note); each card's V value is coloured.
- Current per output: defaults from the device rating, warn at 80%, limit at
  100%; the operator can set warn, limit and an optional "low when on" minimum
  (tripped supply, blown fuse). An empty row goes back to the rating.
- Built-in ratings: Pro 4PM/1PM/2PM 16 A, Plug US 15 A, Ogemray 25 A, Gen1 1PM
  16 A, Gen1 2.5 10 A, ShellyEM clamp 50 A. To check against spec sheets.
- Plugs without a voltage reading: current estimated from power at nominal
  voltage (doubled line to line; not for DC devices).
- Amber outside normal, red outside limit; 5 s "must last" hold.
- Event log (200 max, saved) with start, duration and worst value; device
  errors logged too. Warnings only; the Shelly's own protection switches.

## Automations
- Triggers: Zeus starts, Zeus closes (clean shutdown only), TX (on-air light
  only, with a "don't rely on this for safety" warning), band, frequency range,
  idle time, time of day. Mode isn't a trigger for now.
- The SDK doesn't expose radio connected, S-meter or TX power/SWR. Never
  ControlRadio, never PureSignal, never keys the transmitter.
- Rule list: "When [trigger] → Then [action]" (scene apply, scene all off, one
  output with level, colour and ramp), and "When it ends" (put back how it
  was / off / something else / leave it).
- Timing: Zeus start has a delay; TX, band and range have debounce + delay +
  end wait; Zeus close runs at once (3 s budget of Zeus's 5 s shutdown).
- Scenes can carry "apply when Zeus starts" and "all off when Zeus closes";
  these create ordinary rules.
- Uses in mind: preamps, power supplies, rotator brakes, on-air sign.
- Idle: 60 min default with a 5 min warning; "I'm here" and "+N min" (default
  30). Return puts back how it was, only for untouched outputs, after TX ends.
  Activity = tuning, band/mode change, TX, or using PowerStation (Zeus doesn't
  expose mouse or keyboard).
- Time of day: at HH:MM act only if idle for N min; otherwise check again
  every M min until the next day.
- Safety timers (dead-man) per output and per scene, using the device's own
  flip-back timer, renewed at a third of the period, only right after a poll
  saw the output on. If Zeus stops, the device switches off by itself.
- Every rule except the on-air light waits until TX ends; the panel shows what's
  waiting. Global Running/Paused switch, Test per rule, activity log.
- Band comes from frequency using PowerStation's own band table.

## Later (operator)
- Rules on readings: low-battery cutoff, AC too low or too high.
- Optional light flash/dim as the idle warning.
- Catalog submission.

## Earlier decisions
- Devices: Shelly Plug (Gen1 + Gen2+), Gen1 and Gen2+ relays, dimmers, Pro 3,
  Pro 4PM, Plus RGBW PM, Ogemray 25A (Gen2+ RPC). Gen4 confirmed.
- Addresses come from DHCP and may change; devices are tracked by Shelly device
  ID, so reservations aren't required.
- Discovery: built-in Shelly-only mDNS; operator-triggered scan of saved private
  networks (/20 max, 4,096 addresses). The operator lists each VLAN.
- Re-find: mDNS, then the old /24 and saved networks; cooldown 1 min doubling to
  30 min; never moves a device that's still answering.
- Lamps: green on, orange off, blue for a dimmer below 100%, colour square for
  colour lights, grey unknown.
- Grid: equal snapping cells, Auto/1–4 columns, chosen count kept down to
  150 px cards with a note when squeezed.
- Gen2 passwords stored only as HA1.
- Workflow: a clickable mockup before each UI build; unapproved UI work on a
  feature branch; fixes from testing straight to main. For device problems, ask
  for a Setup › Debug download.
- No NuGet dependencies: only the .NET and ASP.NET Core shared frameworks.
