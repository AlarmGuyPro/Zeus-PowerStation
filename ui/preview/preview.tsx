// SPDX-License-Identifier: GPL-2.0-or-later
// Local preview only (never packaged): renders the real panels against a
// mocked callBackend so every UI state can be checked and screenshotted.
import { createRoot } from "react-dom/client";
import { createClient, type DeviceView, type StatusResponse, type ZeusPluginApi } from "../src/api";
import { ControlsPanel } from "../src/ControlsPanel";
import { DevicesPanel } from "../src/DevicesPanel";

const params = new URLSearchParams(location.search);
const scenario = params.get("scenario") ?? "normal";
const panel = params.get("panel") ?? "controls";
document.documentElement.dataset.theme = params.get("theme") ?? "dark";

const now = new Date().toISOString();
const sw = (index: number, name: string | null, on: boolean, w: number, extra: object = {}) => ({
  key: `switch:${index}`, kind: "Switch" as const, index, name, on,
  powerW: on ? w : 0, voltageV: 121.4, currentA: on ? +(w / 121.4).toFixed(2) : 0,
  powerFactor: 0.98, frequencyHz: 60, energyWh: 18234 + index * 911, temperatureC: 44,
  errors: [], flags: [], metered: true, ...extra,
});

let devices: DeviceView[] = [
  {
    deviceId: "shellypro4pm-f008d1d8b8b8", displayName: "Shack Rack", name: "Shack Rack", host: "10.0.20.14",
    generation: 2, model: "SPSW-104PE16EU", app: "Pro4PM", mac: "F008D1D8B8B8", authRequired: true, hasCredential: true,
    status: { health: "Online", lastSeen: now, channels: [
      sw(0, "Amplifier", true, 312.6), sw(1, "Station PSU", true, 58.2), sw(2, "Monitors", false, 0),
      sw(3, "Rotator", false, 0, { errors: ["overpower"] }),
    ] },
  },
  {
    deviceId: "shellydimmerg3-84fce63a1b2c", displayName: "Desk lamp", name: "Desk lamp", host: "10.0.20.31",
    generation: 3, model: "S3DM-0010WW", app: "DimmerG3", mac: null, authRequired: false, hasCredential: false,
    status: { health: "Online", lastSeen: now, channels: [
      { key: "light:0", kind: "Light", index: 0, name: "Desk lamp", on: true, brightness: 65, powerW: 7.4,
        voltageV: 120.9, currentA: 0.06, errors: [], flags: [], metered: true },
    ] },
  },
  {
    deviceId: "ogemray25a-a1b2c3d4e5f6", displayName: "Linear PSU (25A)", name: "Linear PSU (25A)", host: "10.0.20.40",
    generation: 2, model: "S25A", app: "Ogemray25A", mac: null, authRequired: false, hasCredential: false,
    status: { health: "Unreachable", message: "10.0.20.40 didn't answer in time. Check the address and that this computer can reach that network.", lastSeen: now, channels: [sw(0, "Linear PSU", false, 0)] },
  },
  {
    deviceId: "shellyplugus-c049ef8a2b10", displayName: "Soldering station", name: "Soldering station", host: "10.0.20.52",
    generation: 2, model: "SNPL-00116US", app: "PlugUS", mac: null, authRequired: true, hasCredential: false,
    status: { health: "Unauthorized", message: "This device has a password set. Enter it in PowerStation Devices.", channels: [] },
  },
];
if (scenario === "empty") devices = [];

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

const api: ZeusPluginApi = {
  registerPanel() {},
  async callBackend(method, path, body: any) {
    await new Promise((r) => setTimeout(r, 150));
    if (scenario === "error") throw new TypeError("Failed to fetch");
    if (method === "GET" && path === "/status") {
      const status: StatusResponse = { version: "0.2.0", pollIntervalMs: 2000, devices, scenes: [] };
      return json(status);
    }
    const m = path.match(/^\/devices\/([^/]+)\/channels\/(switch|light)\/(\d+)$/);
    if (m) {
      const d = devices.find((x) => x.deviceId === decodeURIComponent(m[1]))!;
      const ch = d.status.channels.find((x) => x.kind.toLowerCase() === m[2] && x.index === +m[3])!;
      if (body.action === "on") ch.on = true;
      if (body.action === "off") ch.on = false;
      if (body.action === "brightness") { ch.brightness = body.brightness; ch.on = body.brightness > 0; }
      return json(d);
    }
    if (path === "/devices/probe")
      return json({ deviceId: "shellyplus1pm-441793ce3f08", generation: 2, app: "Plus1PM", firmware: "1.5.0", authRequired: true, defaultName: "Bench", supported: true });
    return json({ error: "Not in preview", kind: "request" }, 400);
  },
};

const client = createClient(api);
createRoot(document.getElementById("root")!).render(
  panel === "devices" ? <DevicesPanel client={client} /> : <ControlsPanel client={client} />,
);
