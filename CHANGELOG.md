# Changelog

All versions by KQ4WLR. Newest first.

## 1.1.0 (2026-09-30)
Changes from the Zeus catalog review (KB2UKA).
- TX interlock: nothing switches during TX or for 3 seconds after unkey,
  except the on-air light rule. Rules that wait for TX are released only
  after that, and each checks again just before acting. Buttons and scenes
  are refused (HTTP 409) with a message; a scene stops partway if the radio
  keys. Zeus-close rules wait out the settle time within the shutdown budget.
- Re-finding a device: every address found by mDNS or a sweep must be on the
  local network. A device with a password is no longer moved automatically;
  its card asks you to confirm the new address, and its password (Gen1 Basic
  auth or Gen2 digest) isn't sent there until you do. Ignore keeps the old
  address and doesn't offer that one again.
- Loopback addresses (127.x, ::1) are refused.
- Panel: a notice while transmitting; channel paths are URL-encoded.
- Build: the browser module is built to ui/dist; zeus-build.json and a pinned
  global.json for the catalog's rebuild-from-source check.

## 1.0.0 (2026-09-29)
- First catalog release.
- Debug log is off by default. Turn it on in Setup › Debug when you need a
  log for a device problem; the choice is remembered.

## 0.6.3 (2026-09-28)
- The Devices section on the panel uses the same labelled box as Scenes.
- Debug log: an on/off switch that's remembered (off also clears it), and
  tighter limits: memory only, 500 entries, 2,000 characters per request or
  reply, 24 hours.
- Repo: mockup build script, screenshot and promo-image tooling, handoff docs,
  changelog, API reference.

## 0.6.2
- Plus RGBW PM connects. Its config repeats `button_fade_rate`, which the
  .NET JSON reader rejected; device replies are now read leniently (last value
  wins). A problem reading output names can no longer stop status polling.

## 0.6.1
- Setup › Debug: traffic log of commands, errors and device replies, with
  filters, pause, copy and download. Passwords and auth headers are never
  recorded.
- A status PowerStation can't read shows as an error on the card instead of
  leaving the device on "Connecting…".

## 0.6.0
- Colour lights: Shelly Plus RGBW PM in RGB or RGBW mode. Colour tile with
  level, presets, custom colour and white; colours in scenes and rules; safety
  timers; DC supply voltage isn't judged against mains.

## 0.5.1
- One Save changes button for device settings (and for the Readings page).
- 120 V preset labelled for US split phase; setup shows measured voltage and
  warns when it doesn't match; "wired across two legs" option for 240 V loads.

## 0.5.0
- Gear-cog setup (Devices, Scenes, Automations, Readings, Layout) instead of
  tabs; one PowerStation panel plus an optional idle-timer panel.
- Gen1 support: ShellyEM (relay and clamp meters), Shelly 1, 1PM, 2.5, Plug,
  Plug S, Dimmer 1/2, with HTTP Basic auth.
- Rules: Zeus start and close, TX (on-air light only), band, frequency range,
  idle with countdown and put-back-on-return, time of day; debounce, delay and
  end wait; everything but the on-air light waits for RX. Needs ReadRadioState.
- Device-side safety timers per output and per scene.
- Normal ranges for mains voltage and output current, with an event log.
- Scenes box, lighter dimmer level with green +/orange −.

## 0.4.0 – 0.4.2
- Column grid (Auto, 1–4) with drag to arrange, saved; cards snap to equal
  cells; chosen column count kept on narrow panels.
- Wall-dimmer drawing for dimmer channels; orange "off" lamp.

## 0.3.0
- Built-in Shelly-only mDNS, network scan across VLANs, automatic re-find
  after DHCP changes, output status lamps.

## 0.2.0 – 0.2.1
- Scenes and groups (outputs, dimmer levels, fade, All off); lighter type.
- One panel with Status and Setup tabs.

## 0.1.0
- Shelly Gen2+ control with SHA-256 digest auth, manual add, on/off, dimming,
  live metering, device simulator tests, CI with the Zeus package validator.
