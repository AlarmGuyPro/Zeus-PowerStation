# PowerStation HTTP API

Zeus mounts these under `/api/plugins/io.github.alarmguypro.powerstation/`.
The panels reach them only through the Zeus `callBackend(method, path, body)`
call. Bodies and replies are JSON (camelCase). Errors come back as
`{ "error": "<plain-language message>", "kind": "<kind>" }`:

| HTTP | kind | Meaning |
|---|---|---|
| 400 / 404 / 409 / 413 | `request` | Bad input, unknown device/scene/rule, name clash, body too large |
| 403 | `unauthorized` | The device rejected the password |
| 422 | `unsupported` | The device can't do that |
| 429 | `throttled` | The device is rate-limiting |
| 502 | `protocol` / `deviceerror` | The device answered with something unexpected |
| 503 | `starting` | PowerStation is still starting |
| 504 | `unreachable` | The device didn't answer |
| 500 | `internal` | Unexpected; the message names the exception and it's in Setup › Debug |

## Status

| Method | Path | Notes |
|---|---|---|
| GET | `status` | Everything the panel needs: `version`, `pollIntervalMs`, `devices`, `scenes`, `layout`, `rules`, `automation`, `readings` |

## Devices

| Method | Path | Body |
|---|---|---|
| POST | `devices/probe` | `{ host }` → identity, `supported` |
| POST | `devices` | `{ host, name?, password?, username? }` (username: Gen1 only) |
| PATCH | `devices/{id}` | Any of `name`, `host`, `password`, `username`, `clearPassword`, `channelNames {key: name\|null}`, `safetyMinutes {key: minutes\|null}`, `limits {key: {warnA,maxA,minOnA}\|null}`, `lineToLine` |
| DELETE | `devices/{id}` | Also removes it from scenes |
| POST | `devices/{id}/refresh` | Poll now |
| POST | `devices/{id}/channels/{kind}/{index}` | `{ action: on\|off\|toggle\|brightness\|dim\|color, brightness?, transitionSeconds?, direction?, rgb?, white? }`; kind is `switch`, `light`, `rgb` or `rgbw` |

Channel keys: `switch:N`, `light:N`, `rgb:N`, `rgbw:N`, `emeter:N` (meters are read-only).

## Scenes and layout

| Method | Path | Body |
|---|---|---|
| POST | `scenes` | `{ name, fadeSeconds?, safetyMinutes?, targets: [{ deviceId, kind, index, on, brightness?, rgb?, white? }] }` |
| PUT | `scenes/{id}` | Same as POST |
| DELETE | `scenes/{id}` | |
| POST | `scenes/{id}/run` | `{ mode: apply\|off }` (empty body = apply) |
| PUT | `layout` | `{ columns: 0-4, order: [[deviceId…]] }` |

## Discovery

| Method | Path | Body |
|---|---|---|
| GET | `discovery` | Settings, suggestions, scan state and results |
| PUT | `discovery` | `{ networks?, autoRefind? }` |
| POST | `discovery/scan` | `{ mdns?, networks? }` (409 if one is running) |
| POST | `discovery/cancel` | |

## Automations

| Method | Path | Body |
|---|---|---|
| POST | `rules` | `{ name, enabled, trigger, action, endAction?, debounceSeconds?, delaySeconds?, endDelaySeconds? }` |
| PUT | `rules/{id}` | Same as POST |
| DELETE | `rules/{id}` | |
| POST | `rules/{id}/test` | Runs the action now (still waits for RX) |
| PUT | `automation` | `{ paused }` |
| POST | `automation/activity` | "I'm here" |
| POST | `automation/extend` | Push the idle deadline out by the extend step |

Triggers: `{type: zeusStart}`, `{type: zeusStop}`, `{type: tx}`,
`{type: band, bands: ["20m", …]}`, `{type: frequency, fromMHz, toMHz}`,
`{type: idle, minutes, warnMinutes, extendMinutes}`,
`{type: time, at: "HH:mm", idleMinutes, extendMinutes}`.
Actions: `{type: scene, sceneId, mode: apply|off}` or
`{type: output, deviceId, kind, index, on, brightness?, rampSeconds?, rgb?, white?}`.
End actions add `{type: restore|off|none}`. A TX rule may only switch one
output and end with `off` or `none`.

## Readings

| Method | Path | Body |
|---|---|---|
| PUT | `readings` | `{ mains?: { preset: "120"\|"230"\|"custom", normalLowV, normalHighV, limitLowV, limitHighV }, holdSeconds? }` |
| DELETE | `readings/events` | Clears finished events |

## Debug

| Method | Path | Body |
|---|---|---|
| GET | `debug/log?since=N&max=500` | `{ enabled, recordAll, recordAllUntil, latest, total, entries[] }` |
| PUT | `debug` | `{ enabled?, recordAll? }` (enabled is saved; off clears the log) |
| DELETE | `debug/log` | Clear |

## Saved settings (plugin settings store)

`devices.v1`, `options.v1`, `scenes.v1`, `layout.v1`, `discovery.v1`,
`rules.v1`, `automation.v1`, `readings.v1`, `reading-events.v1`, `debug.v1`.
Each is a JSON string. Gen2 passwords are stored only as HA1; Gen1 passwords
as entered (HTTP Basic has no hashed form).
