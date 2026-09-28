// SPDX-License-Identifier: GPL-2.0-or-later
// Typed wrapper over the Zeus `callBackend` API. Every path is relative to
// /api/plugins/io.github.alarmguypro.powerstation/.
import type { ComponentType } from "react";

export interface ZeusPluginApi {
  registerPanel(spec: { id: string; component: ComponentType }): void;
  callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

export type ChannelKind = "Switch" | "Light";
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

export interface StatusResponse {
  version: string;
  pollIntervalMs: number;
  devices: DeviceView[];
  scenes: Scene[];
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

  const sceneBody = (s: Omit<Scene, "id">) => ({ name: s.name, fadeSeconds: s.fadeSeconds ?? null, targets: s.targets });

  return {
    createScene: (s: Omit<Scene, "id">) => call<Scene>("POST", "/scenes", sceneBody(s)),
    updateScene: (id: string, s: Omit<Scene, "id">) => call<Scene>("PUT", `/scenes/${encodeURIComponent(id)}`, sceneBody(s)),
    deleteScene: (id: string) => call<{ removed: string }>("DELETE", `/scenes/${encodeURIComponent(id)}`),
    runScene: (id: string, mode: "apply" | "off") =>
      call<SceneRunResult>("POST", `/scenes/${encodeURIComponent(id)}/run`, { mode }),
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
