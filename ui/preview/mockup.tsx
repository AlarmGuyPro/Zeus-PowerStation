// SPDX-License-Identifier: GPL-2.0-or-later
// Interactive mockup (not packaged): the real PowerStation panels running
// against an in-memory pretend backend with example devices.
import { useState, type ReactNode } from "react";
import { createRoot } from "react-dom/client";
import { useEffect } from "react";
import {
  BANDS, createClient, type Action, type AutomationState, type ChannelState, type DeviceView, type DiscoveryView,
  type EndAction, type Layout, type ReadingAlert, type ReadingEvent, type ReadingsView, type Rule, type Scene, type SceneTarget, type ZeusPluginApi,
} from "../src/api";
import { MAINS_PRESETS } from "../src/readings";
import { PowerStationPanel } from "../src/PowerStationPanel";
import { IdlePillPanel } from "../src/automations";

type Scenario = "normal" | "empty" | "down";

const sw = (index: number, name: string | null, on: boolean, w: number, extra: Partial<ChannelState> = {}): ChannelState => ({
  key: `switch:${index}`, kind: "Switch", index, name, on,
  powerW: on ? w : 0, voltageV: 121.4, currentA: on ? +(w / 121.4).toFixed(2) : 0,
  powerFactor: 0.98, frequencyHz: 60, energyWh: 18234 + index * 911, temperatureC: 44,
  errors: [], flags: [], metered: true, ...extra,
});

function exampleDevices(): DeviceView[] {
  return [
    {
      deviceId: "shellypro4pm-f008d1d8b8b8", displayName: "Shack Rack", name: "Shack Rack", host: "10.0.20.14",
      generation: 2, model: "SPSW-104PE16EU", app: "Pro4PM", mac: "F008D1D8B8B8", authRequired: true, hasCredential: true,
      status: { health: "Online", channels: [
        sw(0, "Amplifier", true, 312.6, { safetyMinutes: 10 }), sw(1, "Station PSU", true, 58.2, { safetyMinutes: 30 }), sw(2, "Monitors", false, 44),
        sw(3, "Rotator", false, 95, { errors: ["overpower"] }),
      ] },
    },
    {
      deviceId: "shellypro3-c8f09e1a2b3c", displayName: "Antenna switching", name: "Antenna switching", host: "10.0.20.15",
      generation: 2, model: "SPSW-003XE16EU", app: "Pro3", mac: null, authRequired: false, hasCredential: false,
      status: { health: "Online", channels: [
        { key: "switch:0", kind: "Switch", index: 0, name: "Tower preamp", on: true, errors: [], flags: [], metered: false },
        { key: "switch:1", kind: "Switch", index: 1, name: "Remote tuner", on: false, errors: [], flags: [], metered: false },
        { key: "switch:2", kind: "Switch", index: 2, name: "Beverage relay", on: false, errors: [], flags: [], metered: false },
      ] },
    },
    {
      deviceId: "shellydimmerg3-84fce63a1b2c", displayName: "Desk lamp", name: "Desk lamp", host: "10.0.20.31",
      generation: 3, model: "S3DM-0010WW", app: "DimmerG3", mac: null, authRequired: false, hasCredential: false,
      status: { health: "Online", channels: [
        { key: "light:0", kind: "Light", index: 0, name: "Desk lamp", on: true, brightness: 65, powerW: 7.4,
          voltageV: 120.9, currentA: 0.06, errors: [], flags: [], metered: true },
      ] },
    },
    {
      deviceId: "shelly1g4-7c2c6771eea0", displayName: "Antenna Genius Power", name: "Antenna Genius Power", host: "192.168.50.116",
      generation: 4, model: "S1G4", app: "S1G4", mac: null, authRequired: false, hasCredential: false,
      status: { health: "Online", channels: [
        { key: "switch:0", kind: "Switch", index: 0, name: "12 VCD Power", on: true, errors: [], flags: [], metered: false },
      ] },
    },
    {
      deviceId: "shelly1g4-7c2c6771f310", displayName: "Front Gate Control", name: "Front Gate Control", host: "192.168.60.21",
      generation: 4, model: "S1G4", app: "S1G4", mac: null, authRequired: false, hasCredential: false,
      status: { health: "Online", channels: [
        { key: "switch:0", kind: "Switch", index: 0, name: "Gate Open", on: false, errors: [], flags: [], metered: false },
      ] },
    },
    {
      deviceId: "shellywalldimmer-b0a7329e11c4", displayName: "Shack overhead", name: "Shack overhead", host: "10.0.20.33",
      generation: 2, model: "SNDM-0013US", app: "WallDimmer", mac: null, authRequired: false, hasCredential: false,
      status: { health: "Online", channels: [
        { key: "light:0", kind: "Light", index: 0, name: "Overhead", on: true, brightness: 100, powerW: 11.2,
          voltageV: 121.1, currentA: 0.09, errors: [], flags: [], metered: true },
      ] },
    },
    {
      deviceId: "shellyem-c45bbe6a1f22", displayName: "Shack mains (EM)", name: "Shack mains (EM)", host: "10.0.20.60",
      generation: 1, model: "SHEM", app: "ShellyEM", mac: "C45BBE6A1F22", authRequired: false, hasCredential: false,
      status: { health: "Online", channels: [
        { key: "switch:0", kind: "Switch", index: 0, name: "Shack contactor", on: true, errors: [], flags: [], metered: false },
        { key: "emeter:0", kind: "Meter", index: 0, name: "Leg A", on: false, powerW: 1184.2, voltageV: 121.3, currentA: 9.84,
          powerFactor: 0.96, energyWh: 412876, errors: [], flags: [], metered: true },
        { key: "emeter:1", kind: "Meter", index: 1, name: "Leg B", on: false, powerW: 342.8, voltageV: 120.8, currentA: 2.91,
          powerFactor: 0.97, energyWh: 128455, errors: [], flags: [], metered: true },
      ] },
    },
    {
      deviceId: "shelly1-98f4ab12cd34", displayName: "Tower lights (Shelly 1)", name: "Tower lights (Shelly 1)", host: "10.0.30.18",
      generation: 1, model: "SHSW-1", app: "Shelly1", mac: "98F4AB12CD34", authRequired: true, hasCredential: true,
      status: { health: "Online", channels: [
        { key: "switch:0", kind: "Switch", index: 0, name: "Tower beacon", on: false, errors: [], flags: [], metered: false },
      ] },
    },
    {
      deviceId: "shellydimmerg3-84fce63a9e01", displayName: "On Air sign", name: "On Air sign", host: "10.0.20.34",
      generation: 3, model: "S3DM-0010WW", app: "DimmerG3", mac: null, authRequired: false, hasCredential: false,
      status: { health: "Online", channels: [
        { key: "light:0", kind: "Light", index: 0, name: "On Air", on: false, brightness: 100, powerW: 0,
          voltageV: 121.0, currentA: 0, errors: [], flags: [], metered: true },
      ] },
    },
    {
      deviceId: "ogemray25a-a1b2c3d4e5f6", displayName: "Linear PSU (25A)", name: "Linear PSU (25A)", host: "10.0.30.40",
      generation: 2, model: "S25A", app: "Ogemray25A", mac: null, authRequired: false, hasCredential: false,
      status: { health: "Unreachable", message: "10.0.30.40 didn't answer in time. Check the address and that this computer can reach that network.", channels: [sw(0, "Linear PSU", false, 0)] },
    },
    {
      deviceId: "shellyplugus-c049ef8a2b10", displayName: "Soldering station", name: "Soldering station", host: "10.0.20.52",
      generation: 2, model: "SNPL-00116US", app: "PlugUS", mac: null, authRequired: true, hasCredential: false,
      status: { health: "Unauthorized", message: "This device has a password set. Enter it in PowerStation Devices.", channels: [] },
    },
  ];
}

const RACK = "shellypro4pm-f008d1d8b8b8", ANT = "shellypro3-c8f09e1a2b3c", LAMP = "shellydimmerg3-84fce63a1b2c", LIN = "ogemray25a-a1b2c3d4e5f6";
const ONAIR = "shellydimmerg3-84fce63a9e01", OVER = "shellywalldimmer-b0a7329e11c4";
const t = (deviceId: string, kind: "Switch" | "Light", index: number, on: boolean, brightness: number | null = null): SceneTarget =>
  ({ deviceId, kind, index, on, brightness });
function exampleScenes(): Scene[] {
  return [
    { id: "s-operating", name: "Operating", fadeSeconds: 2, targets: [
      t(RACK, "Switch", 0, true), t(RACK, "Switch", 1, true), t(RACK, "Switch", 2, true), t(ANT, "Switch", 0, true), t(LAMP, "Light", 0, true, 70), t(LIN, "Switch", 0, true) ] },
    { id: "s-evening", name: "Evening lights", fadeSeconds: 3, targets: [t(LAMP, "Light", 0, true, 25), t(RACK, "Switch", 2, false)] },
    { id: "s-listen", name: "Listen only", fadeSeconds: null, targets: [t(RACK, "Switch", 0, false), t(RACK, "Switch", 1, true), t(ANT, "Switch", 0, true), t(ANT, "Switch", 2, true)] },
    { id: "s-standby", name: "Standby", fadeSeconds: 5, targets: [t(RACK, "Switch", 0, false), t(RACK, "Switch", 2, false), t(LIN, "Switch", 0, false), t(LAMP, "Light", 0, true, 15), t(OVER, "Light", 0, false)] },
  ];
}
function exampleRules(): Rule[] {
  const ago = (min: number) => new Date(Date.now() - min * 60000).toISOString();
  return [
    { id: "r-start", name: "Operating when Zeus starts", enabled: true, trigger: { type: "zeusStart" },
      action: { type: "scene", sceneId: "s-operating", mode: "apply" }, delaySeconds: 5,
      lastRun: { at: ago(47), ok: false, text: "5 of 6 outputs on. Linear PSU (25A) didn't answer." } },
    { id: "r-stop", name: "Operating off when Zeus closes", enabled: true, trigger: { type: "zeusStop" },
      action: { type: "scene", sceneId: "s-operating", mode: "off" } },
    { id: "r-onair", name: "On Air sign", enabled: true, trigger: { type: "tx" },
      action: { type: "output", deviceId: ONAIR, kind: "Light", index: 0, on: true, brightness: 100, rampSeconds: 0.5 },
      endAction: { type: "off" }, debounceSeconds: 0.3, delaySeconds: 0, endDelaySeconds: 3 },
    { id: "r-6m", name: "6 m preamp", enabled: true, trigger: { type: "band", bands: ["6m"] },
      action: { type: "output", deviceId: ANT, kind: "Switch", index: 0, on: true }, endAction: { type: "restore" },
      debounceSeconds: 2, delaySeconds: 0, endDelaySeconds: 0 },
    { id: "r-bev", name: "Beverage on 160 m", enabled: true, trigger: { type: "frequency", fromMHz: 1.8, toMHz: 2.0 },
      action: { type: "output", deviceId: ANT, kind: "Switch", index: 2, on: true }, endAction: { type: "off" },
      debounceSeconds: 2, delaySeconds: 0, endDelaySeconds: 5 },
    { id: "r-brake", name: "Rotator on when Zeus starts", enabled: false, trigger: { type: "zeusStart" },
      action: { type: "output", deviceId: RACK, kind: "Switch", index: 3, on: true }, delaySeconds: 20 },
    { id: "r-idle", name: "Standby after an hour idle", enabled: true, trigger: { type: "idle", minutes: 60, warnMinutes: 5, extendMinutes: 30 },
      action: { type: "scene", sceneId: "s-standby", mode: "apply" }, endAction: { type: "restore" } },
    { id: "r-night", name: "Lights out at 23:00", enabled: true, trigger: { type: "time", at: "23:00", idleMinutes: 15, extendMinutes: 30 },
      action: { type: "scene", sceneId: "s-evening", mode: "off" } },
  ];
}
const exampleLayout = (): Layout => ({
  columns: 3,
  order: [
    ["shellypro4pm-f008d1d8b8b8", "shellydimmerg3-84fce63a1b2c", "shellypro3-c8f09e1a2b3c",
     "shellyem-c45bbe6a1f22", "shellywalldimmer-b0a7329e11c4", "shellydimmerg3-84fce63a9e01",
     "shelly1g4-7c2c6771eea0", "shelly1-98f4ab12cd34", "shelly1g4-7c2c6771f310",
     "ogemray25a-a1b2c3d4e5f6", "shellyplugus-c049ef8a2b10"],
  ],
});
const state = { scenario: "normal" as Scenario, devices: exampleDevices(), scenes: exampleScenes(), layout: exampleLayout(), rules: exampleRules() };

// ---- pretend discovery: saved networks, a timed scan, and one device that moved
const disc = {
  networks: ["10.0.20.0/24", "10.0.30.0/24"],
  autoRefind: true,
  scan: { running: false, phase: "idle", probed: 0, total: 0, networks: [] as string[], usedMdns: false,
    found: [] as DiscoveryView["scan"]["found"], startedAt: null as string | null, finishedAt: null as string | null, cancelled: false, error: null },
  timer: 0 as number,
};
const pool = () => [
  { at: 8, found: { device: { host: "10.0.20.14", deviceId: RACK, generation: 2, app: "Pro4PM", model: "SPSW-104PE16EU", name: "Shack Rack", authRequired: true, supported: true, foundBy: ["mdns", "sweep"] }, added: true } },
  { at: 12, found: { device: { host: "10.0.20.77", deviceId: "shellyplus2pm-d48afc41a0b2", generation: 2, app: "Plus2PM", model: "SNSW-102P16EU", name: "Bench outlets", authRequired: false, supported: true, foundBy: ["mdns", "sweep"] }, added: false } },
  { at: 30, found: { device: { host: "10.0.20.81", deviceId: "shellydimmer2-98cdac1f22e0", generation: 1, app: "Dimmer2", model: "SHDM-2", name: null, authRequired: true, supported: true, foundBy: ["sweep"] }, added: false } },
  { at: 55, found: { device: { host: "10.0.20.60", deviceId: "shellyem-c45bbe6a1f22", generation: 1, app: "ShellyEM", model: "SHEM", name: "Shack mains (EM)", authRequired: false, supported: true, foundBy: ["sweep"] }, added: true } },
  { at: 45, found: { device: { host: "10.0.20.90", deviceId: "shellyplugus-e4b3230a9c11", generation: 2, app: "PlugUS", model: "SNPL-00116US", name: "Heater plug", authRequired: true, supported: true, foundBy: ["sweep"] }, added: false } },
  { at: 78, found: { device: { host: "10.0.30.52", deviceId: LIN, generation: 2, app: "Ogemray25A", model: "S25A", name: "Linear PSU (25A)", authRequired: false, supported: true, foundBy: ["sweep"] }, added: true, addressUpdatedFrom: "10.0.30.40" } },
];
function discoveryView(): DiscoveryView {
  return clone({ settings: { networks: disc.networks, autoRefind: disc.autoRefind }, suggested: ["10.0.20.0/24"], scan: disc.scan }) as DiscoveryView;
}
function startMockScan(mdns: boolean) {
  const nets = disc.networks.length ? disc.networks : ["10.0.20.0/24"];
  const total = nets.length * 254;
  disc.scan = { ...disc.scan, running: true, phase: mdns ? "mdns" : "sweep", probed: 0, total, networks: nets, usedMdns: mdns,
    found: [], startedAt: new Date().toISOString(), finishedAt: null, cancelled: false, error: null };
  const items = pool();
  let tick = 0;
  window.clearInterval(disc.timer);
  disc.timer = window.setInterval(() => {
    tick++;
    if (disc.scan.phase === "mdns" && tick >= 3) disc.scan.phase = "sweep";
    if (disc.scan.phase === "sweep") disc.scan.probed = Math.min(total, disc.scan.probed + Math.ceil(total / 14));
    const pct = (disc.scan.probed / total) * 100;
    for (const it of items)
      if (pct >= it.at && !disc.scan.found.some((f) => f.device.deviceId === it.found.device.deviceId)) {
        disc.scan.found.push(it.found as DiscoveryView["scan"]["found"][number]);
        if (it.found.addressUpdatedFrom) {
          const d = state.devices.find((x) => x.deviceId === LIN);
          if (d) {
            d.previousHost = d.host; d.host = it.found.device.host; d.hostChangedAt = new Date().toISOString();
            d.status = { health: "Online", channels: d.status.channels };
          }
        }
      }
    if (disc.scan.probed >= total) {
      disc.scan.running = false; disc.scan.phase = "done"; disc.scan.finishedAt = new Date().toISOString();
      window.clearInterval(disc.timer);
    }
  }, 300);
}
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
const fail = (error: string, status = 400) => json({ error, kind: "request" }, status);
const clone = <T,>(v: T): T => JSON.parse(JSON.stringify(v));

function applyPower(ch: ChannelState) {
  if (!ch.metered || ch.kind !== "Switch") return;
  const rated: Record<string, number> = { Amplifier: 312.6, "Station PSU": 58.2, Monitors: 44, Rotator: 95 };
  const w = rated[ch.name ?? ""] ?? 60;
  ch.powerW = ch.on ? w : 0;
  ch.currentA = ch.on ? +(w / 121.4).toFixed(2) : 0;
}


// ---- pretend readings: normal ranges, alerts and the event log
const RATED: Record<string, number | null> = { Pro4PM: 16, PlugUS: 15, Ogemray25A: 25, ShellyEM: 50, DimmerG3: null, WallDimmer: null };
const rd = {
  view: { mains: { ...MAINS_PRESETS["120"] }, holdSeconds: 5, events: [] as ReadingEvent[] } as ReadingsView,
  mainsV: 121.4,
  ampOverload: false,
  psuTripped: false,
  custom: new Map<string, { warnA?: number | null; maxA?: number | null; minOnA?: number | null }>(),
  firstSeen: new Map<string, number>(),
  open: new Map<string, ReadingEvent>(),
};
function exampleEvents(): ReadingEvent[] {
  const at = (h: number) => new Date(Date.now() - h * 3600000).toISOString();
  return [
    { id: "e3", deviceId: RACK, channelKey: "switch:3", label: "Rotator", kind: "device", level: "limit", text: "device reported overpower and switched off", start: at(2.2), end: at(2.2) },
    { id: "e2", deviceId: RACK, channelKey: "switch:0", label: "Amplifier", kind: "currentHigh", level: "warn", text: "high current, peak 13.2 A (warn above 12.8 A)", peak: 13.2, start: at(5.1), end: new Date(Date.now() - 5.1 * 3600000 + 40000).toISOString() },
    { id: "e1", deviceId: "shellyem-c45bbe6a1f22", channelKey: "emeter:0", label: "Leg A", kind: "voltageLow", level: "warn", text: "low voltage, lowest 111.8 V (normal from 114 V)", peak: 111.8, start: at(20), end: new Date(Date.now() - 20 * 3600000 + 4 * 60000).toISOString() },
  ];
}
rd.view.events = exampleEvents();
rd.custom.set(`${RACK}|switch:1`, { warnA: 1.0, maxA: 1.5, minOnA: 0.2 });
function limitsFor(d: DeviceView, ch: ChannelState) {
  const rated = ch.kind === "Meter" || ch.metered ? RATED[d.app ?? ""] ?? null : null;
  const c = rd.custom.get(`${d.deviceId}|${ch.key}`);
  return {
    ratedA: rated,
    warnA: c && "warnA" in c ? c.warnA ?? null : rated ? +(rated * 0.8).toFixed(1) : null,
    maxA: c && "maxA" in c ? c.maxA ?? null : rated,
    minOnA: c?.minOnA ?? null,
    custom: !!c,
  };
}
/** Applies the pretend supply and faults, then works out alerts and events. */
function measure(devices: DeviceView[]) {
  const m = rd.view.mains;
  const now = Date.now();
  const seen = new Set<string>();
  let mainsAlert: ReadingAlert | null = null;
  let mainsCount = 0;
  for (const d of devices) {
    for (const ch of d.status.channels) if (ch.metered) ch.limits = limitsFor(d, ch);
    if (d.status.health !== "Online") continue;
    for (const ch of d.status.channels) {
      if (!ch.metered) continue;
      const jitter = ch.kind === "Meter" ? (ch.index ? -0.5 : 0) : 0;
      ch.voltageV = +(rd.mainsV + jitter).toFixed(1);
      if (ch.name === "Amplifier" && ch.on) { ch.currentA = rd.ampOverload ? 13.9 : 2.57; ch.powerW = +(ch.currentA * ch.voltageV * 0.98).toFixed(1); }
      if (ch.name === "Station PSU" && ch.on) { ch.currentA = rd.psuTripped ? 0.02 : 0.48; ch.powerW = rd.psuTripped ? 1.2 : 58.2; }
      ch.limits = limitsFor(d, ch);
      const checks: [ReadingAlert["kind"], "warn" | "limit", number, number][] = [];
      const v = ch.voltageV;
      if (v < m.limitLowV) checks.push(["voltageLow", "limit", v, m.limitLowV]);
      else if (v < m.normalLowV) checks.push(["voltageLow", "warn", v, m.normalLowV]);
      if (v > m.limitHighV) checks.push(["voltageHigh", "limit", v, m.limitHighV]);
      else if (v > m.normalHighV) checks.push(["voltageHigh", "warn", v, m.normalHighV]);
      const a = ch.currentA ?? 0, l = ch.limits;
      if (l.maxA != null && a > l.maxA) checks.push(["currentHigh", "limit", a, l.maxA]);
      else if (l.warnA != null && a > l.warnA) checks.push(["currentHigh", "warn", a, l.warnA]);
      if (l.minOnA != null && ch.on && a < l.minOnA) checks.push(["currentLow", "warn", a, l.minOnA]);
      ch.alerts = [];
      for (const [kind, level, value, threshold] of checks) {
        const volt = kind.startsWith("voltage");
        // Voltage is the station's supply: one event for all outputs; current is per output.
        const key = volt ? `mains|${kind}` : `${d.deviceId}|${ch.key}|${kind}`;
        seen.add(key);
        if (!rd.firstSeen.has(key)) rd.firstSeen.set(key, now);
        const since = rd.firstSeen.get(key)!;
        if (now - since < rd.view.holdSeconds * 1000) continue;
        const alert = { kind, level, value, threshold, since: new Date(since).toISOString() };
        ch.alerts.push(alert);
        if (volt) {
          mainsCount++;
          const low = kind === "voltageLow";
          if (!mainsAlert || (low ? value < mainsAlert.value : value > mainsAlert.value)) mainsAlert = alert;
        }
        const label = volt ? "Mains" : channelLabelOf(ch);
        const unit = volt ? "V" : "A";
        const low = kind.endsWith("Low");
        let ev = rd.open.get(key);
        if (!ev) {
          ev = { id: key + since, deviceId: volt ? "" : d.deviceId, channelKey: volt ? "" : ch.key, label, kind, level, text: "", peak: value, start: new Date(since).toISOString(), end: null };
          rd.open.set(key, ev);
          rd.view.events.unshift(ev);
        }
        ev.peak = low ? Math.min(ev.peak ?? value, value) : Math.max(ev.peak ?? value, value);
        if (level === "limit") ev.level = "limit";
        const m2 = rd.view.mains;
        const what = { voltageHigh: "high voltage", voltageLow: "low voltage", currentHigh: "high current", currentLow: "low current while on" }[kind];
        const bound = kind === "voltageLow" ? (ev.level === "limit" ? `below the ${m2.limitLowV} V limit` : `normal from ${m2.normalLowV} V`)
          : kind === "voltageHigh" ? (ev.level === "limit" ? `above the ${m2.limitHighV} V limit` : `normal to ${m2.normalHighV} V`)
          : kind === "currentLow" ? `expected at least ${threshold} A`
          : level === "limit" ? `limit ${threshold} A` : `warn above ${threshold} A`;
        ev.text = `${what}, ${low ? "lowest" : "peak"} ${ev.peak} ${unit} (${bound})`;
      }
    }
  }
  rd.view.mainsNow = { voltageV: rd.mainsV, alert: mainsAlert, outputs: mainsCount };
  for (const key of [...rd.firstSeen.keys()]) if (!seen.has(key)) {
    rd.firstSeen.delete(key);
    const ev = rd.open.get(key);
    if (ev) { ev.end = new Date().toISOString(); rd.open.delete(key); }
  }
}
const channelLabelOf = (ch: ChannelState) => ch.name ?? `${ch.kind === "Meter" ? "Meter" : "Output"} ${ch.index + 1}`;

// ---- pretend automation engine: a small stand-in for the backend rules runner
const BAND_EDGES: Record<string, [number, number]> = {
  "160m": [1.8, 2.0], "80m": [3.5, 4.0], "60m": [5.33, 5.41], "40m": [7.0, 7.3], "30m": [10.1, 10.15], "20m": [14.0, 14.35],
  "17m": [18.068, 18.168], "15m": [21.0, 21.45], "12m": [24.89, 24.99], "10m": [28.0, 29.7], "6m": [50.0, 54.0], "4m": [70.0, 70.5], "2m": [144.0, 148.0],
};
const BAND_DIAL: Record<string, number> = { "160m": 1.84, "80m": 3.573, "60m": 5.357, "40m": 7.074, "30m": 10.136, "20m": 14.074, "17m": 18.1, "15m": 21.074, "12m": 24.915, "10m": 28.074, "6m": 50.313, "4m": 70.154, "2m": 144.174 };
const bandOf = (mhz: number) => Object.entries(BAND_EDGES).find(([, [a, b]]) => mhz >= a && mhz <= b)?.[0] ?? null;

type Snapshot = Map<string, { on: boolean; brightness?: number | null }>;
const eng = {
  paused: false,
  radio: { connected: true, frequencyHz: 14.074e6, band: "20m" as string | null, mode: "DIGU", mox: false },
  lastActivity: Date.now(),
  idleFiresAt: Date.now() + 42 * 60000,
  idleState: "active" as "active" | "warning" | "idle",
  active: new Map<string, boolean>(),           // lasting rules currently "on"
  timers: new Map<string, number>(),
  snapshots: new Map<string, Snapshot>(),
  pending: [] as AutomationState["pending"],
  txQueue: [] as { ruleId: string; text: string; run: () => void }[],
  log: [] as AutomationState["log"],
};
const idleRule = () => state.rules.find((r) => r.enabled && r.trigger.type === "idle");
const now = () => new Date().toISOString();
function log(text: string, ok = true) { eng.log.unshift({ at: now(), text, ok }); eng.log = eng.log.slice(0, 20); }
function chOf(deviceId: string, kind: string, index: number) {
  return state.devices.find((d) => d.deviceId === deviceId)?.status.channels.find((c) => c.kind === kind && c.index === index);
}
function setCh(deviceId: string, kind: string, index: number, on: boolean, brightness?: number | null) {
  const d = state.devices.find((x) => x.deviceId === deviceId);
  const ch = chOf(deviceId, kind, index);
  if (!d || !ch) return `a removed output`;
  if (d.status.health !== "Online") throw new Error(`${d.displayName} didn't answer`);
  ch.on = on;
  if (ch.kind === "Light" && on && brightness) ch.brightness = brightness;
  if (ch.kind === "Light" && ch.metered) ch.powerW = ch.on ? +((ch.brightness ?? 0) * 0.114).toFixed(1) : 0;
  applyPower(ch);
  return ch.name ?? d.displayName;
}
function targetsOf(a: Action): { deviceId: string; kind: string; index: number }[] {
  if (a.type === "output") return [a];
  return state.scenes.find((s) => s.id === a.sceneId)?.targets ?? [];
}
function snapshot(a: Action): Snapshot {
  const m: Snapshot = new Map();
  for (const tg of targetsOf(a)) { const ch = chOf(tg.deviceId, tg.kind, tg.index); if (ch) m.set(`${tg.deviceId}|${tg.kind}|${tg.index}`, { on: ch.on, brightness: ch.brightness }); }
  return m;
}
function perform(a: Action): string {
  if (a.type === "output") {
    const name = setCh(a.deviceId, a.kind, a.index, a.on, a.brightness);
    return `${name} ${a.on ? "on" : "off"}${a.on && a.brightness && a.kind === "Light" ? ` at ${a.brightness}%` : ""}`;
  }
  const scene = state.scenes.find((s) => s.id === a.sceneId);
  if (!scene) throw new Error("scene was deleted");
  let ok = 0; const bad: string[] = [];
  for (const tg of scene.targets) {
    try { setCh(tg.deviceId, tg.kind, tg.index, a.mode === "off" ? false : tg.on, tg.brightness); ok++; }
    catch (e) { bad.push((e as Error).message); }
  }
  if (bad.length) throw new Error(`${scene.name}: ${ok} of ${scene.targets.length} outputs. ${[...new Set(bad)].join(", ")}.`);
  return a.mode === "off" ? `${scene.name} all off` : `${scene.name} applied`;
}
/** Runs an action for a rule; anything but the on-air light waits while TX is on. */
function runFor(rule: Rule, a: Action | EndAction, label: string, isEnd = false) {
  const go = () => {
    try {
      let text: string;
      if (a.type === "restore") {
        const snap = eng.snapshots.get(rule.id);
        if (!snap) return;
        for (const [k, v] of snap) { const [id, kind, idx] = k.split("|"); try { setCh(id, kind, +idx, v.on, v.brightness); } catch { /* offline */ } }
        eng.snapshots.delete(rule.id);
        text = "put back how it was";
      } else if (a.type === "none") return;
      else if (a.type === "off") text = perform(rule.action.type === "scene" ? { ...rule.action, mode: "off" } : { ...rule.action, on: false });
      else {
        if (!isEnd) eng.snapshots.set(rule.id, snapshot(a));
        text = perform(a);
      }
      rule.lastRun = { at: now(), ok: true, text };
      log(`${rule.name}: ${text}`);
    } catch (e) {
      rule.lastRun = { at: now(), ok: false, text: (e as Error).message };
      log(`${rule.name}: ${(e as Error).message}`, false);
    }
  };
  if (eng.radio.mox && rule.trigger.type !== "tx") {
    eng.txQueue.push({ ruleId: rule.id, text: `${rule.name} (${label})`, run: go });
    log(`${rule.name}: waiting for TX to end`);
  } else go();
}
function later(key: string, seconds: number | null | undefined, fn: () => void, text?: string) {
  window.clearTimeout(eng.timers.get(key));
  eng.pending = eng.pending.filter((p) => p.ruleId !== key);
  if (!seconds) { fn(); return; }
  if (text) eng.pending.push({ ruleId: key, text, at: new Date(Date.now() + seconds * 1000).toISOString() });
  eng.timers.set(key, window.setTimeout(() => { eng.pending = eng.pending.filter((p) => p.ruleId !== key); eng.timers.delete(key); fn(); }, seconds * 1000));
}
function cancel(key: string) { window.clearTimeout(eng.timers.get(key)); eng.timers.delete(key); eng.pending = eng.pending.filter((p) => p.ruleId !== key); }

function evaluate() {
  if (eng.paused) return;
  const mhz = (eng.radio.frequencyHz ?? 0) / 1e6;
  for (const r of state.rules) {
    if (!r.enabled) continue;
    const t = r.trigger;
    let match: boolean;
    if (t.type === "band") match = !!eng.radio.band && t.bands.includes(eng.radio.band);
    else if (t.type === "frequency") match = mhz >= t.fromMHz && mhz <= t.toMHz;
    else if (t.type === "tx") match = eng.radio.mox;
    else continue;
    const was = eng.active.get(r.id) ?? false;
    const want = eng.timers.has(`${r.id}:on`) ? true : eng.timers.has(`${r.id}:off`) ? false : was;
    if (match === want) {
      // Condition flipped back before the debounce ran out: cancel it.
      if (match === was) { cancel(`${r.id}:on`); cancel(`${r.id}:off`); }
      continue;
    }
    if (match) {
      cancel(`${r.id}:off`);
      if (was) continue;
      const wait = (r.debounceSeconds ?? 0) + (r.delaySeconds ?? 0);
      later(`${r.id}:on`, wait, () => { eng.active.set(r.id, true); runFor(r, r.action, "start"); }, `${r.name}: starts in ${wait}s if it holds`);
    } else {
      cancel(`${r.id}:on`);
      if (!was) continue;
      const wait = r.endDelaySeconds ?? 0;
      later(`${r.id}:off`, wait, () => { eng.active.set(r.id, false); if (r.endAction) runFor(r, r.endAction, "end", true); }, `${r.name}: ends in ${wait}s`);
    }
  }
}
function activity(why: string) {
  eng.lastActivity = Date.now();
  const rule = idleRule();
  if (eng.idleState === "idle" && rule) {
    log(`Activity (${why}): welcome back`);
    if (rule.endAction) runFor(rule, rule.endAction, "return", true);
  }
  eng.idleState = "active";
  if (rule && rule.trigger.type === "idle") eng.idleFiresAt = Date.now() + rule.trigger.minutes * 60000;
}
function idleTick() {
  const rule = idleRule();
  if (eng.paused || !rule || rule.trigger.type !== "idle") return;
  const left = eng.idleFiresAt - Date.now();
  if (eng.idleState === "active" && left <= rule.trigger.warnMinutes * 60000) { eng.idleState = "warning"; log(`${rule.name}: warning shown`); }
  if (eng.idleState === "warning" && left <= 0) { eng.idleState = "idle"; runFor(rule, rule.action, "idle"); }
}
window.setInterval(idleTick, 500);
function flushTx() {
  const q = eng.txQueue; eng.txQueue = [];
  for (const item of q) item.run();
}
function automationView(): AutomationState {
  const rule = idleRule();
  return clone({
    paused: eng.paused,
    radio: eng.radio,
    idle: {
      state: rule ? eng.idleState : "off",
      ruleId: rule?.id ?? null,
      lastActivity: new Date(eng.lastActivity).toISOString(),
      firesAt: rule ? new Date(eng.idleFiresAt).toISOString() : null,
      extendMinutes: rule && rule.trigger.type === "idle" ? rule.trigger.extendMinutes : null,
    },
    pending: [...eng.pending, ...eng.txQueue.map((q) => ({ ruleId: q.ruleId, text: q.text, waitingForTx: true }))],
    log: eng.log,
  });
}
const zeus = {
  tune(mhz: number) { eng.radio.frequencyHz = Math.round(mhz * 1e6); eng.radio.band = bandOf(mhz); activity("tuning"); evaluate(); },
  mode(m: string) { eng.radio.mode = m; activity("mode"); },
  mox(on: boolean) { eng.radio.mox = on; activity("TX"); evaluate(); if (!on) flushTx(); },
  start() {
    log("Zeus started");
    for (const r of state.rules) if (r.enabled && r.trigger.type === "zeusStart") later(`${r.id}:start`, r.delaySeconds, () => runFor(r, r.action, "start"), `${r.name}: in ${r.delaySeconds}s`);
  },
  stop() {
    log("Zeus closing");
    for (const r of state.rules) if (r.enabled && r.trigger.type === "zeusStop") runFor(r, r.action, "stop");
  },
  nearIdle() { const rule = idleRule(); if (rule?.trigger.type === "idle") { eng.idleState = "active"; eng.idleFiresAt = Date.now() + 65000; eng.lastActivity = Date.now() - (rule.trigger.minutes * 60000 - 65000); } },
  elevenPm() {
    for (const r of state.rules) {
      if (!r.enabled || r.trigger.type !== "time") continue;
      const idleFor = (Date.now() - eng.lastActivity) / 60000;
      if (eng.idleState === "idle" || idleFor >= r.trigger.idleMinutes) runFor(r, r.action, "time");
      else {
        const next = new Date(); next.setHours(23, r.trigger.extendMinutes, 0, 0);
        r.lastRun = { at: now(), ok: true, text: `Station active at ${r.trigger.at}; checking again at ${next.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}` };
        log(`${r.name}: you're active, checking again in ${r.trigger.extendMinutes} min`);
        eng.pending = eng.pending.filter((p) => p.ruleId !== r.id);
        eng.pending.push({ ruleId: r.id, text: `${r.name}: next check`, at: next.toISOString() });
      }
    }
  },
};

const api: ZeusPluginApi = {
  registerPanel() {},
  async callBackend(method, path, body: any) {
    await new Promise((r) => setTimeout(r, 180));
    if (state.scenario === "down") throw new TypeError("Failed to fetch");
    const devices = state.devices;
    if (method === "GET" && path === "/status") {
      measure(devices);
      return json({ version: "0.5.0", pollIntervalMs: 1000, devices: clone(devices), scenes: clone(state.scenes), rules: clone(state.rules),
        automation: automationView(), readings: clone(rd.view), layout: state.scenario === "empty" ? null : clone(state.layout) });
    }
    if (path === "/readings" && method === "PUT") {
      const mm = body?.mains;
      if (mm && !(mm.limitLowV <= mm.normalLowV && mm.normalLowV < mm.normalHighV && mm.normalHighV <= mm.limitHighV))
        return fail("Voltages must go limit low ≤ normal from < normal to ≤ limit high.");
      if (mm) rd.view.mains = mm;
      if (typeof body?.holdSeconds === "number") rd.view.holdSeconds = body.holdSeconds;
      return json(rd.view);
    }
    if (path === "/readings/events" && method === "DELETE") { rd.view.events = rd.view.events.filter((e) => !e.end); return json(rd.view); }
    if (method !== "GET" && !path.startsWith("/discovery") && path !== "/layout") activity("PowerStation");

    if (path === "/automation" && method === "PUT") { if (typeof body?.paused === "boolean") { eng.paused = body.paused; log(body.paused ? "Automations paused" : "Automations running"); } return json(automationView()); }
    if (path === "/automation/activity") { log("I'm here"); return json(automationView()); }
    if (path === "/automation/extend") {
      const r = idleRule(); if (r?.trigger.type === "idle") { eng.idleFiresAt += r.trigger.extendMinutes * 60000; eng.idleState = "active"; log(`Idle pushed back ${r.trigger.extendMinutes} min`); }
      return json(automationView());
    }
    const rm = path.match(/^\/rules(?:\/([^/]+))?(\/test)?$/);
    if (rm) {
      const rule = rm[1] ? state.rules.find((x) => x.id === decodeURIComponent(rm[1])) : undefined;
      if (rm[1] && !rule) return fail("That rule doesn't exist.", 404);
      if (method === "DELETE") { state.rules = state.rules.filter((x) => x !== rule); eng.active.delete(rule!.id); return json({ removed: rule!.id }); }
      if (rm[2]) { runFor(rule!, rule!.action, "test"); return json(automationView()); }
      if (body?.trigger?.type === "tx" && body?.action?.type !== "output") return fail("A TX rule can only switch one light.");
      if (body?.trigger?.type === "frequency" && !(body.trigger.fromMHz < body.trigger.toMHz)) return fail("The range's From must be below To.");
      if (body?.trigger?.type === "band" && !body.trigger.bands.length) return fail("Pick at least one band.");
      const saved: Rule = { ...body, id: rule?.id ?? `r-${Date.now()}`, lastRun: rule?.lastRun ?? null };
      state.rules = rule ? state.rules.map((x) => (x === rule ? saved : x)) : [...state.rules, saved];
      if (rule) eng.active.delete(rule.id);
      evaluate();
      return json(saved);
    }
    if (path === "/layout" && method === "PUT") {
      state.layout = { columns: body.columns, order: body.order };
      return json(state.layout);
    }

    if (path === "/discovery" && method === "GET") return json(discoveryView());
    if (path === "/discovery" && method === "PUT") {
      if (body.networks) {
        for (const n of body.networks)
          if (!/^(10|192\.168|172\.(1[6-9]|2\d|3[01]))\.\d+\.\d+\.\d+\/(2[0-9]|3[0-2])$/.test(n))
            return fail(`"${n}" isn't a valid local network. Use the form 192.168.1.0/24, /20 or smaller.`);
        disc.networks = body.networks;
      }
      if (typeof body.autoRefind === "boolean") disc.autoRefind = body.autoRefind;
      return json(discoveryView());
    }
    if (path === "/discovery/scan") { startMockScan(body?.mdns !== false); return json(discoveryView()); }
    if (path === "/discovery/cancel") {
      window.clearInterval(disc.timer);
      disc.scan = { ...disc.scan, running: false, phase: "done", cancelled: true, finishedAt: new Date().toISOString() };
      return json(discoveryView());
    }

    const sm = path.match(/^\/scenes(?:\/([^/]+))?(\/run)?$/);
    if (sm) {
      const scene = sm[1] ? state.scenes.find((x) => x.id === decodeURIComponent(sm[1])) : undefined;
      if (sm[1] && !scene) return fail("That scene doesn't exist.", 404);
      if (method === "DELETE") { state.scenes = state.scenes.filter((x) => x !== scene); return json({ removed: scene!.id }); }
      if (!sm[2]) {
        const name = String(body?.name ?? "").trim();
        if (!name) return fail("Give the scene a name.");
        if (!body?.targets?.length) return fail("Pick at least one output for the scene.");
        if (state.scenes.some((x) => x.id !== scene?.id && x.name.toLowerCase() === name.toLowerCase()))
          return fail(`There's already a scene called "${name}".`, 409);
        const saved: Scene = { id: scene?.id ?? `s-${Date.now()}`, name, fadeSeconds: body.fadeSeconds || null, safetyMinutes: body.safetyMinutes || null, targets: body.targets };
        state.scenes = scene ? state.scenes.map((x) => (x === scene ? saved : x)) : [...state.scenes, saved];
        return json(saved);
      }
      const mode = body?.mode === "off" ? "off" : "apply";
      const results = scene!.targets.map((tg) => {
        const d = devices.find((x) => x.deviceId === tg.deviceId);
        if (!d) return { deviceId: tg.deviceId, channelKey: "", ok: false, error: "Device was removed from PowerStation." };
        const key = `${tg.kind === "Light" ? "light" : "switch"}:${tg.index}`;
        if (d.status.health !== "Online") return { deviceId: d.deviceId, channelKey: key, ok: false, error: `${d.displayName}: ${d.host} didn't answer in time.` };
        const ch = d.status.channels.find((c) => c.key === key);
        if (ch) {
          ch.on = mode === "off" ? false : tg.on;
          if (ch.kind === "Light") { if (mode === "apply" && tg.on && tg.brightness) ch.brightness = tg.brightness; ch.powerW = ch.on ? +((ch.brightness ?? 0) * 0.114).toFixed(1) : 0; }
          applyPower(ch);
        }
        return { deviceId: d.deviceId, channelKey: key, ok: true, error: null };
      });
      const touched = [...new Set(scene!.targets.map((x) => x.deviceId))].map((id) => devices.find((x) => x.deviceId === id)).filter(Boolean);
      return json({ sceneId: scene!.id, mode, succeeded: results.filter((r) => r.ok).length, failed: results.filter((r) => !r.ok).length, results, devices: clone(touched) });
    }

    if (path === "/devices/probe") {
      const host = String(body?.host ?? "").trim();
      if (!/^(10|192\.168|172\.(1[6-9]|2\d|3[01]))\./.test(host)) return fail(`${host || "That"} isn't a local-network address. PowerStation only controls devices on your LAN.`);
      return json({ deviceId: `shellyplus1pm-${host.split(".").pop()}a17c3e`, generation: 2, app: "Plus1PM", firmware: "1.5.0", authRequired: host.endsWith("7"), defaultName: "Bench supply", supported: true });
    }
    if (method === "POST" && path === "/devices") {
      const host = String(body?.host ?? "").trim();
      if (!/^(10|192\.168|172\.(1[6-9]|2\d|3[01]))\./.test(host)) return fail(`${host || "That"} isn't a local-network address. PowerStation only controls devices on your LAN.`);
      if (host.endsWith("7") && !body?.password) return fail("This device has a password set. Enter it to add the device.");
      const d: DeviceView = {
        deviceId: `shellyplus1pm-${host.split(".").pop()}a17c3e`, displayName: body?.name || "Bench supply", name: body?.name || "Bench supply",
        host, generation: 2, app: "Plus1PM", model: "SNSW-001P16EU", authRequired: !!body?.password, hasCredential: !!body?.password,
        status: { health: "Online", channels: [sw(0, null, false, 60)] },
      };
      state.devices = [...devices.filter((x) => x.deviceId !== d.deviceId), d];
      return json(d);
    }
    const dm = path.match(/^\/devices\/([^/]+)(\/.*)?$/);
    const d = dm && devices.find((x) => x.deviceId === decodeURIComponent(dm[1]));
    if (!d) return fail("That device isn't in PowerStation.", 404);
    const rest = dm![2] ?? "";

    if (method === "DELETE") {
      state.devices = devices.filter((x) => x !== d);
      state.scenes = state.scenes.map((sc) => ({ ...sc, targets: sc.targets.filter((x) => x.deviceId !== d.deviceId) }));
      return json({ removed: d.deviceId });
    }
    if (rest === "/refresh") return json(d);
    if (method === "PATCH") {
      if (body.name !== undefined) { d.name = body.name || null; d.displayName = body.name || d.deviceId; }
      if (body.host) d.host = body.host;
      if (body.clearPassword) d.hasCredential = false;
      if (body.password) {
        d.hasCredential = true; d.authRequired = true;
        if (d.status.health === "Unauthorized") d.status = { health: "Online", channels: [sw(0, "Soldering station", false, 48)] };
      }
      if (body.limits) for (const [k, v] of Object.entries(body.limits)) {
        const key = `${d.deviceId}|${k}`;
        if (v === null) { rd.custom.delete(key); continue; }
        rd.custom.set(key, { ...rd.custom.get(key), ...(v as object) });
      }
      if (body.limits) measure(state.devices);
      if (body.safetyMinutes) for (const [k, v] of Object.entries(body.safetyMinutes)) {
        const ch = d.status.channels.find((c) => c.key === k); if (ch) ch.safetyMinutes = (v as number) || null;
      }
      if (body.channelNames) for (const [k, v] of Object.entries(body.channelNames)) {
        const ch = d.status.channels.find((c) => c.key === k); if (ch) ch.name = (v as string) || null;
      }
      return json(d);
    }
    const cm = rest.match(/^\/channels\/(switch|light)\/(\d+)$/);
    if (cm) {
      if (d.status.health !== "Online") return json({ error: `${d.host} didn't answer in time.`, kind: "unreachable" }, 504);
      const ch = d.status.channels.find((x) => x.kind.toLowerCase() === cm[1] && x.index === +cm[2])!;
      if (body.action === "on") ch.on = true;
      if (body.action === "off") ch.on = false;
      if (body.action === "toggle") ch.on = !ch.on;
      if (body.action === "brightness") { ch.brightness = body.brightness; ch.on = body.brightness > 0; }
      if (ch.kind === "Light" && ch.metered) ch.powerW = ch.on ? +((ch.brightness ?? 0) * 0.114).toFixed(1) : 0;
      applyPower(ch);
      return json(d);
    }
    return fail("Not available in the mockup.");
  },
};

const client = createClient(api);

function Frame({ title, children, small }: { title: string; children: ReactNode; small?: boolean }) {
  return (
    <section className={`mk-frame${small ? " mk-frame--small" : ""}`}>
      <header className="mk-frame-bar"><span className="mk-dot" aria-hidden="true" />{title}</header>
      <div className="mk-frame-body">{children}</div>
    </section>
  );
}

/** Stand-in for Zeus itself: tune, key TX and so on, to watch the rules react. */
function PretendZeus() {
  const [, force] = useState(0);
  useEffect(() => { const t = window.setInterval(() => force((n) => n + 1), 500); return () => window.clearInterval(t); }, []);
  const r = eng.radio;
  const [freq, setFreq] = useState("");
  return (
    <div className="mk-zeus" aria-label="Pretend Zeus radio controls">
      <div className="mk-zeus-title">Pretend Zeus <span>(mockup only: drives the rules)</span></div>
      <div className="mk-zeus-row">
        <span className="mk-zeus-freq">{((r.frequencyHz ?? 0) / 1e6).toFixed(3)} MHz · {r.band ?? "out of band"} · {r.mode}</span>
        <button className={`mk-tx${r.mox ? " mk-tx--on" : ""}`} aria-pressed={r.mox} onClick={() => zeus.mox(!r.mox)}>{r.mox ? "TX (click to unkey)" : "Key TX"}</button>
        <select className="mk-small" value={r.mode} onChange={(e) => zeus.mode(e.currentTarget.value)} aria-label="Mode">
          {["LSB", "USB", "CW", "DIGU", "AM", "FM"].map((m) => <option key={m}>{m}</option>)}
        </select>
        <form onSubmit={(e) => { e.preventDefault(); const v = parseFloat(freq); if (v > 0) zeus.tune(v); }} className="mk-zeus-tune">
          <input className="mk-small" placeholder="Tune MHz" value={freq} onChange={(e) => setFreq(e.currentTarget.value)} aria-label="Frequency in MHz" inputMode="decimal" />
          <button className="mk-small">Tune</button>
        </form>
      </div>
      <div className="mk-zeus-row mk-bands">
        {BANDS.map((b) => <button key={b} aria-pressed={r.band === b} onClick={() => zeus.tune(BAND_DIAL[b])}>{b}</button>)}
      </div>
      <div className="mk-zeus-row">
        <button className="mk-small" onClick={() => zeus.start()}>Zeus starts</button>
        <button className="mk-small" onClick={() => zeus.stop()}>Zeus closes</button>
        <button className="mk-small" onClick={() => zeus.nearIdle()}>Skip to 1 min before idle</button>
        <button className="mk-small" onClick={() => zeus.elevenPm()}>Pretend it's 23:00</button>
      </div>
      <div className="mk-zeus-row">
        <span className="mk-zeus-label">Mains {rd.mainsV} V</span>
        <button className="mk-small" aria-pressed={rd.mainsV === 108.6} onClick={() => { rd.mainsV = 108.6; }}>Sag to 108.6 V</button>
        <button className="mk-small" aria-pressed={rd.mainsV === 126.6} onClick={() => { rd.mainsV = 126.6; }}>Rise to 126.6 V</button>
        <button className="mk-small" aria-pressed={rd.mainsV === 121.4} onClick={() => { rd.mainsV = 121.4; }}>Normal 121.4 V</button>
        <button className="mk-small" aria-pressed={rd.ampOverload} onClick={() => { rd.ampOverload = !rd.ampOverload; }}>Amplifier draws 13.9 A</button>
        <button className="mk-small" aria-pressed={rd.psuTripped} onClick={() => { rd.psuTripped = !rd.psuTripped; }}>Station PSU fuse blown</button>
      </div>
    </div>
  );
}

function App() {
  const [scenario, setScenario] = useState<Scenario>("normal");
  const [epoch, setEpoch] = useState(0);
  const pick = (s: Scenario) => {
    state.scenario = s;
    state.devices = s === "empty" ? [] : exampleDevices();
    state.scenes = s === "empty" ? [] : exampleScenes();
    state.rules = s === "empty" ? [] : exampleRules();
    state.layout = exampleLayout();
    window.clearInterval(disc.timer);
    disc.scan = { ...disc.scan, running: false, phase: "idle", probed: 0, total: 0, found: [], finishedAt: null, cancelled: false };
    for (const t of eng.timers.values()) window.clearTimeout(t);
    Object.assign(eng, { paused: false, lastActivity: Date.now(), idleFiresAt: Date.now() + 42 * 60000, idleState: "active", pending: [], txQueue: [], log: [] });
    eng.radio = { connected: true, frequencyHz: 14.074e6, band: "20m", mode: "DIGU", mox: false };
    eng.active.clear(); eng.timers.clear(); eng.snapshots.clear();
    Object.assign(rd, { mainsV: 121.4, ampOverload: false, psuTripped: false });
    rd.custom.clear(); rd.firstSeen.clear(); rd.open.clear();
    rd.view = { mains: { ...MAINS_PRESETS["120"] }, holdSeconds: 5, events: exampleEvents() };
    rd.custom.set(`${RACK}|switch:1`, { warnA: 1.0, maxA: 1.5, minOnA: 0.2 });
    setScenario(s);
    setEpoch((e) => e + 1);
  };
  return (
    <>
      <div className="mk-controls">
        <label className="mk-select">
          <span>Situation</span>
          <select id="mk-scenario" value={scenario} onChange={(e) => pick(e.currentTarget.value as Scenario)}>
            <option value="normal">Example shack (11 devices)</option>
            <option value="empty">First run, no devices yet</option>
            <option value="down">Zeus backend not responding</option>
          </select>
        </label>
        <button className="mk-reset" onClick={() => pick(scenario)}>Reset example</button>
      </div>
      <PretendZeus />
      <div className="mk-stage" key={epoch}>
        <Frame title="PowerStation"><PowerStationPanel client={client} /></Frame>
        <Frame title="PowerStation Idle" small><IdlePillPanel client={client} /></Frame>
      </div>
    </>
  );
}

createRoot(document.getElementById("mk-root")!).render(<App />);
