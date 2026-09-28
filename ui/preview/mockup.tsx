// SPDX-License-Identifier: GPL-2.0-or-later
// Interactive mockup (not packaged): the real PowerStation panels running
// against an in-memory pretend backend with example devices.
import { useState, type ReactNode } from "react";
import { createRoot } from "react-dom/client";
import { createClient, type ChannelState, type DeviceView, type DiscoveryView, type Layout, type Scene, type SceneTarget, type ZeusPluginApi } from "../src/api";
import { PowerStationPanel } from "../src/PowerStationPanel";

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
        sw(0, "Amplifier", true, 312.6), sw(1, "Station PSU", true, 58.2), sw(2, "Monitors", false, 44),
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
const t = (deviceId: string, kind: "Switch" | "Light", index: number, on: boolean, brightness: number | null = null): SceneTarget =>
  ({ deviceId, kind, index, on, brightness });
function exampleScenes(): Scene[] {
  return [
    { id: "s-operating", name: "Operating", fadeSeconds: 2, targets: [
      t(RACK, "Switch", 0, true), t(RACK, "Switch", 1, true), t(RACK, "Switch", 2, true), t(ANT, "Switch", 0, true), t(LAMP, "Light", 0, true, 70), t(LIN, "Switch", 0, true) ] },
    { id: "s-evening", name: "Evening lights", fadeSeconds: 3, targets: [t(LAMP, "Light", 0, true, 25), t(RACK, "Switch", 2, false)] },
    { id: "s-listen", name: "Listen only", fadeSeconds: null, targets: [t(RACK, "Switch", 0, false), t(RACK, "Switch", 1, true), t(ANT, "Switch", 0, true), t(ANT, "Switch", 2, true)] },
  ];
}
const exampleLayout = (): Layout => ({
  columns: 3,
  order: [
    ["shellypro4pm-f008d1d8b8b8", "shelly1g4-7c2c6771eea0"],
    ["shellydimmerg3-84fce63a1b2c", "shellywalldimmer-b0a7329e11c4", "shelly1g4-7c2c6771f310"],
    ["shellypro3-c8f09e1a2b3c", "ogemray25a-a1b2c3d4e5f6", "shellyplugus-c049ef8a2b10"],
  ],
});
const state = { scenario: "normal" as Scenario, devices: exampleDevices(), scenes: exampleScenes(), layout: exampleLayout() };

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
  { at: 30, found: { device: { host: "10.0.20.81", deviceId: "shellydimmer2-98cdac1f22e0", generation: 1, app: null, model: "SHDM-2", name: null, authRequired: false, supported: false, foundBy: ["sweep"] }, added: false } },
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

const api: ZeusPluginApi = {
  registerPanel() {},
  async callBackend(method, path, body: any) {
    await new Promise((r) => setTimeout(r, 180));
    if (state.scenario === "down") throw new TypeError("Failed to fetch");
    const devices = state.devices;
    if (method === "GET" && path === "/status") return json({ version: "0.4.2", pollIntervalMs: 2000, devices: clone(devices), scenes: clone(state.scenes), layout: state.scenario === "empty" ? null : clone(state.layout) });
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
        const saved: Scene = { id: scene?.id ?? `s-${Date.now()}`, name, fadeSeconds: body.fadeSeconds || null, targets: body.targets };
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

function Frame({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="mk-frame">
      <header className="mk-frame-bar"><span className="mk-dot" aria-hidden="true" />{title}</header>
      <div className="mk-frame-body">{children}</div>
    </section>
  );
}

function App() {
  const [scenario, setScenario] = useState<Scenario>("normal");
  const [epoch, setEpoch] = useState(0);
  const pick = (s: Scenario) => {
    state.scenario = s;
    state.devices = s === "empty" ? [] : exampleDevices();
    state.scenes = s === "empty" ? [] : exampleScenes();
    state.layout = exampleLayout();
    window.clearInterval(disc.timer);
    disc.scan = { ...disc.scan, running: false, phase: "idle", probed: 0, total: 0, found: [], finishedAt: null, cancelled: false };
    setScenario(s);
    setEpoch((e) => e + 1);
  };
  return (
    <>
      <div className="mk-controls">
        <label className="mk-select">
          <span>Situation</span>
          <select id="mk-scenario" value={scenario} onChange={(e) => pick(e.currentTarget.value as Scenario)}>
            <option value="normal">Example shack (8 devices)</option>
            <option value="empty">First run, no devices yet</option>
            <option value="down">Zeus backend not responding</option>
          </select>
        </label>
        <button className="mk-reset" onClick={() => pick(scenario)}>Reset example</button>
      </div>
      <div className="mk-stage" key={epoch}>
        <Frame title="PowerStation"><PowerStationPanel client={client} initialTab="status" /></Frame>
      </div>
    </>
  );
}

createRoot(document.getElementById("mk-root")!).render(<App />);
