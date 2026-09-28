// SPDX-License-Identifier: GPL-2.0-or-later
import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { ApiError, type DeviceView, type PowerStationClient, type Scene, type StatusResponse } from "./api";
import { CSS, ROOT, c } from "./styles";

/** Scoped root wrapper. The <style> element lives and dies with the panel. */
export function PanelRoot({ label, children }: { label: string; children: ReactNode }) {
  return (
    <section className={ROOT} aria-label={label}>
      <style>{CSS}</style>
      {children}
    </section>
  );
}

export interface StatusState {
  data: StatusResponse | null;
  error: ApiError | null;
  loading: boolean;
  reload: () => void;
  /** Merge a single device returned by a command so the UI updates immediately. */
  applyDevice: (device: DeviceView) => void;
  removeDevice: (deviceId: string) => void;
  upsertScene: (scene: Scene) => void;
  dropScene: (sceneId: string) => void;
}

/**
 * Polls /status while the panel is mounted and visible. The timer is cleared
 * on unmount, as the Zeus UI contract requires.
 */
export function useStatus(client: PowerStationClient, intervalMs = 2000): StatusState {
  const [data, setData] = useState<StatusResponse | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [loading, setLoading] = useState(true);
  const alive = useRef(true);
  const inFlight = useRef(false);

  const load = useCallback(async () => {
    if (inFlight.current) return;
    inFlight.current = true;
    try {
      const next = await client.status();
      if (!alive.current) return;
      setData(next);
      setError(null);
    } catch (err) {
      if (!alive.current) return;
      setError(err instanceof ApiError ? err : new ApiError(String(err), 0, "unknown"));
    } finally {
      inFlight.current = false;
      if (alive.current) setLoading(false);
    }
  }, [client]);

  useEffect(() => {
    alive.current = true;
    void load();
    const period = Math.max(1000, data?.pollIntervalMs ?? intervalMs);
    const timer = window.setInterval(() => {
      if (document.visibilityState !== "hidden") void load();
    }, period);
    return () => {
      alive.current = false;
      window.clearInterval(timer);
    };
    // Re-arm only when the server-configured interval changes.
  }, [load, data?.pollIntervalMs, intervalMs]);

  const applyDevice = useCallback((device: DeviceView) => {
    setData((prev) => {
      if (!prev) return prev;
      const exists = prev.devices.some((d) => d.deviceId === device.deviceId);
      const devices = exists
        ? prev.devices.map((d) => (d.deviceId === device.deviceId ? device : d))
        : [...prev.devices, device].sort((a, b) => a.displayName.localeCompare(b.displayName));
      return { ...prev, devices };
    });
  }, []);

  const removeDevice = useCallback((deviceId: string) => {
    setData((prev) => (prev ? { ...prev, devices: prev.devices.filter((d) => d.deviceId !== deviceId) } : prev));
  }, []);

  const upsertScene = useCallback((scene: Scene) => {
    setData((prev) => {
      if (!prev) return prev;
      const exists = prev.scenes.some((s) => s.id === scene.id);
      return { ...prev, scenes: exists ? prev.scenes.map((s) => (s.id === scene.id ? scene : s)) : [...prev.scenes, scene] };
    });
  }, []);

  const dropScene = useCallback((sceneId: string) => {
    setData((prev) => (prev ? { ...prev, scenes: prev.scenes.filter((s) => s.id !== sceneId) } : prev));
  }, []);

  return { data, error, loading, reload: () => void load(), applyDevice, removeDevice, upsertScene, dropScene };
}

export function HealthLabel({ device }: { device: DeviceView }) {
  const { health } = device.status;
  const text =
    health === "Online" ? "Online"
      : health === "Pending" ? "Connecting…"
        : health === "Unreachable" ? "Offline"
          : health === "Unauthorized" ? "Password needed"
            : "Error";
  const tone =
    health === "Online" ? "health--online"
      : health === "Pending" ? null
        : health === "Unreachable" ? "health--bad"
          : "health--warn";
  return (
    <span className={c("health", tone)} role="status">
      {text}
    </span>
  );
}

export function Notice({ tone, children }: { tone?: "warn" | "error" | "ok"; children: ReactNode }) {
  return (
    <p className={c("notice", tone && `notice--${tone}`)} role={tone === "error" ? "alert" : undefined}>
      {children}
    </p>
  );
}

export function BackendError({ error, onRetry }: { error: ApiError; onRetry: () => void }) {
  return (
    <div className={c("empty")}>
      <strong>PowerStation isn't responding</strong>
      <p>{error.status === 503 ? "It's still starting up." : error.message}</p>
      <button type="button" className={c("button")} onClick={onRetry}>
        Try again
      </button>
    </div>
  );
}

export function Loading() {
  return (
    <div className={c("empty")} role="status" aria-live="polite">
      Loading PowerStation…
    </div>
  );
}

export const fmt = {
  watts: (w: number) => (Math.abs(w) >= 1000 ? `${(w / 1000).toFixed(2)} kW` : `${w.toFixed(w < 10 ? 1 : 0)} W`),
  volts: (v: number) => `${v.toFixed(1)} V`,
  amps: (a: number) => `${a.toFixed(2)} A`,
  energy: (wh: number) => (wh >= 1000 ? `${(wh / 1000).toFixed(2)} kWh` : `${wh.toFixed(0)} Wh`),
  temp: (t: number) => `${t.toFixed(0)} °C`,
};

export function channelLabel(ch: { name?: string | null; kind: string; index: number }) {
  if (ch.name) return ch.name;
  return `${ch.kind === "Light" ? "Dimmer" : "Output"} ${ch.index + 1}`;
}
