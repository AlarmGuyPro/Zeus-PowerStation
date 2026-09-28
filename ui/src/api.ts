// SPDX-License-Identifier: GPL-2.0-or-later
// Typed wrapper over the Zeus `callBackend` API. Every path is relative to
// /api/plugins/io.github.alarmguypro.powerstation/.
import type { ComponentType } from "react";

export interface ZeusPluginApi {
  registerPanel(spec: { id: string; component: ComponentType }): void;
  callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

/** Meter = a read-only energy-meter channel (ShellyEM / EM clamps); it can't be switched. */
export type ChannelKind = "Switch" | "Light" | "Meter";
export type DeviceHealth = "Pending" | "Online" | "Unreachable" | "Unauthorized" | "Error";

export interface ChannelState {
  key: string;
  kind: ChannelKind;
  index: number;
  name?: string | null;
  on: boolean;
  brightness?: number | null;
  powerW?: number | null;
  voltageV?: number | null;
  currentA?: number | null;
  powerFactor?: number | null;
  frequencyHz?: number | null;
  energyWh?: number | null;
  temperatureC?: number | null;
  errors: string[];
  flags: string[];
  metered: boolean;
  /** Device-side safety timer on this output, in minutes (null = none). */
  safetyMinutes?: number | null;
  /** When the safety timer would turn the output off if Zeus stopped renewing it. */
  safetyEndsAt?: string | null;
}

export interface DeviceView {
  deviceId: string;
  displayName: string;
  name?: string | null;
  host: string;
  generation: number;
  model?: string | null;
  app?: string | null;
  mac?: string | null;
  authRequired: boolean;
  hasCredential: boolean;
  previousHost?: string | null;
  hostChangedAt?: string | null;
  status: {
    health: DeviceHealth;
    message?: string | null;
    lastSeen?: string | null;
    lastPolled?: string | null;
    channels: ChannelState[];
  };
}

export interface SceneTarget {
  deviceId: string;
  kind: ChannelKind;
  index: number;
  on: boolean;
  brightness?: number | null;
}

export interface Scene {
  id: string;
  name: string;
  fadeSeconds?: number | null;
  /** Safety timer (minutes) put on every output this scene turns on. */
  safetyMinutes?: number | null;
  targets: SceneTarget[];
}

export interface SceneRunResult {
  sceneId: string;
  mode: "apply" | "off";
  succeeded: number;
  failed: number;
  results: { deviceId: string; channelKey: string; ok: boolean; error?: string | null }[];
  devices: DeviceView[];
}

/** Status-tab arrangement. columns 0 = automatic; order[i] = device IDs in column i, top to bottom. */
export interface Layout {
  columns: number;
  order: string[][];
}

export interface StatusResponse {
  version: string;
  pollIntervalMs: number;
  devices: DeviceView[];
  scenes: Scene[];
  layout?: Layout | null;
  rules?: Rule[];
  automation?: AutomationState | null;
}

// ---------------------------------------------------------------- automations

export const BANDS = ["160m", "80m", "60m", "40m", "30m", "20m", "17m", "15m", "12m", "10m", "6m", "4m", "2m"] as const;

export type Trigger =
  | { type: "zeusStart" }
  | { type: "zeusStop" }
  /** On-air light only: never a safety interlock. */
  | { type: "tx" }
  | { type: "band"; bands: string[] }
  | { type: "frequency"; fromMHz: number; toMHz: number }
  | { type: "idle"; minutes: number; warnMinutes: number; extendMinutes: number }
  /** At a time of day, act only if the station has been idle; otherwise check again later. */
  | { type: "time"; at: string; idleMinutes: number; extendMinutes: number };

export type TriggerType = Trigger["type"];

export type Action =
  | { type: "scene"; sceneId: string; mode: "apply" | "off" }
  | { type: "output"; deviceId: string; kind: ChannelKind; index: number; on: boolean; brightness?: number | null; rampSeconds?: number | null };

/** What happens when a lasting condition (TX, band, range, idle) ends. */
export type EndAction = { type: "restore" } | { type: "none" } | { type: "off" } | Action;

export interface Rule {
  id: string;
  name: string;
  enabled: boolean;
  trigger: Trigger;
  action: Action;
  endAction?: EndAction | null;
  /** Condition must hold this long before the rule counts it (ignores blips). */
  debounceSeconds?: number | null;
  /** Wait this long after the trigger before acting. */
  delaySeconds?: number | null;
  /** Wait this long after the condition ends before running the end action. */
  endDelaySeconds?: number | null;
  lastRun?: { at: string; ok: boolean; text: string } | null;
}

export type RuleBody = Omit<Rule, "id" | "lastRun">;

export interface AutomationState {
  paused: boolean;
  radio: { connected: boolean; frequencyHz?: number | null; band?: string | null; mode?: string | null; mox: boolean };
  idle: {
    /** active: someone is operating; warning: countdown shown; idle: the idle rule has run. */
    state: "active" | "warning" | "idle" | "off";
    ruleId?: string | null;
    lastActivity?: string | null;
    /** When the idle rule will run (active or warning state). */
    firesAt?: string | null;
    extendMinutes?: number | null;
  };
  /** Pending delayed actions, including actions deferred until TX ends. */
  pending: { ruleId: string; text: string; at?: string | null; waitingForTx?: boolean }[];
  log: { at: string; text: string; ok: boolean }[];
}

export interface FoundDevice {
  host: string;
  deviceId: string;
  generation: number;
  model?: string | null;
  app?: string | null;
  name?: string | null;
  authRequired: boolean;
  supported: boolean;
  foundBy: string[];
}

export interface DiscoveryView {
  settings: { networks: string[]; autoRefind: boolean };
  suggested: string[];
  scan: {
    running: boolean;
    phase: "idle" | "mdns" | "sweep" | "done";
    probed: number;
    total: number;
    networks: string[];
    usedMdns: boolean;
    found: { device: FoundDevice; added: boolean; addressUpdatedFrom?: string | null }[];
    startedAt?: string | null;
    finishedAt?: string | null;
    cancelled: boolean;
    error?: string | null;
  };
}

export interface ProbeResponse {
  deviceId: string;
  generation: number;
  model?: string | null;
  app?: string | null;
  firmware?: string | null;
  authRequired: boolean;
  defaultName?: string | null;
  supported: boolean;
}

export interface ChannelCommand {
  action: "on" | "off" | "toggle" | "brightness" | "dim";
  brightness?: number;
  transitionSeconds?: number;
  direction?: "up" | "down" | "stop";
}

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly kind: string,
  ) {
    super(message);
  }
}

export function createClient(api: ZeusPluginApi) {
  async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
    let response: Response;
    try {
      response = await api.callBackend(method, path, body);
    } catch {
      throw new ApiError("Can't reach Zeus. Check that Zeus is still running.", 0, "network");
    }
    let payload: unknown = null;
    const text = await response.text();
    if (text) {
      try {
        payload = JSON.parse(text);
      } catch {
        payload = null;
      }
    }
    if (!response.ok) {
      const p = payload as { error?: string; kind?: string } | null;
      throw new ApiError(
        p?.error ?? `PowerStation returned HTTP ${response.status}.`,
        response.status,
        p?.kind ?? "http",
      );
    }
    return payload as T;
  }

  const id = (deviceId: string) => encodeURIComponent(deviceId);

  const sceneBody = (s: Omit<Scene, "id">) => ({
    name: s.name,
    fadeSeconds: s.fadeSeconds ?? null,
    safetyMinutes: s.safetyMinutes ?? null,
    targets: s.targets,
  });

  return {
    saveLayout: (layout: Layout) => call<Layout>("PUT", "/layout", layout),
    discovery: () => call<DiscoveryView>("GET", "/discovery"),
    saveDiscovery: (patch: { networks?: string[]; autoRefind?: boolean }) => call<DiscoveryView>("PUT", "/discovery", patch),
    startScan: (mdns: boolean, networks?: string[]) =>
      call<DiscoveryView>("POST", "/discovery/scan", { mdns, networks: networks ?? null }),
    cancelScan: () => call<DiscoveryView>("POST", "/discovery/cancel"),
    createScene: (s: Omit<Scene, "id">) => call<Scene>("POST", "/scenes", sceneBody(s)),
    updateScene: (id: string, s: Omit<Scene, "id">) => call<Scene>("PUT", `/scenes/${encodeURIComponent(id)}`, sceneBody(s)),
    deleteScene: (id: string) => call<{ removed: string }>("DELETE", `/scenes/${encodeURIComponent(id)}`),
    runScene: (id: string, mode: "apply" | "off") =>
      call<SceneRunResult>("POST", `/scenes/${encodeURIComponent(id)}/run`, { mode }),
    createRule: (r: RuleBody) => call<Rule>("POST", "/rules", r),
    updateRule: (id: string, r: RuleBody) => call<Rule>("PUT", `/rules/${encodeURIComponent(id)}`, r),
    deleteRule: (id: string) => call<{ removed: string }>("DELETE", `/rules/${encodeURIComponent(id)}`),
    testRule: (id: string) => call<AutomationState>("POST", `/rules/${encodeURIComponent(id)}/test`),
    setAutomation: (patch: { paused?: boolean }) => call<AutomationState>("PUT", "/automation", patch),
    /** "I'm here": counts as activity and resets the idle countdown. */
    imHere: () => call<AutomationState>("POST", "/automation/activity"),
    /** Push the idle timeout out by the idle rule's extend step. */
    extendIdle: () => call<AutomationState>("POST", "/automation/extend"),
    status: () => call<StatusResponse>("GET", "/status"),
    probe: (host: string) => call<ProbeResponse>("POST", "/devices/probe", { host }),
    addDevice: (host: string, name?: string, password?: string) =>
      call<DeviceView>("POST", "/devices", { host, name: name || null, password: password || null }),
    updateDevice: (
      deviceId: string,
      patch: {
        name?: string | null;
        host?: string;
        password?: string;
        clearPassword?: boolean;
        channelNames?: Record<string, string | null>;
        safetyMinutes?: Record<string, number | null>;
      },
    ) => call<DeviceView>("PATCH", `/devices/${id(deviceId)}`, patch),
    removeDevice: (deviceId: string) => call<{ removed: string }>("DELETE", `/devices/${id(deviceId)}`),
    refresh: (deviceId: string) => call<DeviceView>("POST", `/devices/${id(deviceId)}/refresh`),
    command: (deviceId: string, channel: ChannelState, command: ChannelCommand) =>
      call<DeviceView>(
        "POST",
        `/devices/${id(deviceId)}/channels/${channel.kind.toLowerCase()}/${channel.index}`,
        command,
      ),
  };
}

export type PowerStationClient = ReturnType<typeof createClient>;
