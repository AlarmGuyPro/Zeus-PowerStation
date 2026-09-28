// SPDX-License-Identifier: GPL-2.0-or-later
import { useEffect, useRef, useState } from "react";
import { ApiError, type ChannelState, type DeviceView, type PowerStationClient } from "./api";
import { BackendError, HealthLabel, Loading, Notice, channelLabel, fmt, type StatusState } from "./shared";
import { ScenesStrip } from "./scenes";
import { c } from "./styles";

const ERROR_TEXT: Record<string, string> = {
  overpower: "Overpower",
  overtemp: "Overheated",
  overvoltage: "Overvoltage",
  undervoltage: "Undervoltage",
  overcurrent: "Overcurrent",
  unsupported_load: "Unsupported load",
};

/** Status tab: scene buttons and one card per device with its outputs. */
export function StatusView({
  client,
  status,
  onGoToSetup,
}: {
  client: PowerStationClient;
  status: StatusState;
  onGoToSetup: () => void;
}) {
  const { data, error, loading } = status;
  if (loading && !data) return <Loading />;
  if (!data && error) return <BackendError error={error} onRetry={status.reload} />;
  if (!data) return null;
  if (data.devices.length === 0)
    return (
      <div className={c("empty")}>
        <strong>No devices yet</strong>
        <p>Add your Shelly relays, plugs and dimmers on the Setup tab.</p>
        <button type="button" className={c("button", "button--primary")} onClick={onGoToSetup}>
          Add a device
        </button>
      </div>
    );
  return (
    <>
      {error && <Notice tone="warn">Lost contact with PowerStation. Showing the last known state.</Notice>}
      <ScenesStrip client={client} status={status} />
      {data.devices.map((d) => (
        <DeviceCard key={d.deviceId} device={d} client={client} onUpdate={status.applyDevice} />
      ))}
    </>
  );
}

function DeviceCard({
  device,
  client,
  onUpdate,
}: {
  device: DeviceView;
  client: PowerStationClient;
  onUpdate: (d: DeviceView) => void;
}) {
  const { health, message, channels } = device.status;
  const online = health === "Online";
  const [actionError, setActionError] = useState<string | null>(null);

  return (
    <article className={c("device")} aria-labelledby={`ps-${device.deviceId}`}>
      <div className={c("device-head")}>
        <h3 className={c("device-name")} id={`ps-${device.deviceId}`}>
          {device.displayName}
        </h3>
        <span className={c("device-meta")}>{device.app ?? device.model ?? ""}</span>
        <HealthLabel device={device} />
      </div>
      <div className={c("device-body")}>
        {!online && health !== "Pending" && (
          <Notice tone={health === "Unreachable" ? "error" : "warn"}>
            {health === "Unauthorized"
              ? "This device needs its password. Enter it on the Setup tab."
              : message ?? "The device isn't answering."}
          </Notice>
        )}
        {actionError && <Notice tone="error">{actionError}</Notice>}
        {channels.length === 0 && online && (
          <p className={c("hint")}>This device has no relay or dimmer outputs PowerStation can control.</p>
        )}
        {channels.length > 0 && (
          <div className={c("grid")}>
            {channels.map((ch) => (
              <ChannelTile
                key={ch.key}
                device={device}
                channel={ch}
                disabled={!online}
                client={client}
                onUpdate={onUpdate}
                onError={setActionError}
              />
            ))}
          </div>
        )}
      </div>
    </article>
  );
}

function ChannelTile({
  device,
  channel,
  disabled,
  client,
  onUpdate,
  onError,
}: {
  device: DeviceView;
  channel: ChannelState;
  disabled: boolean;
  client: PowerStationClient;
  onUpdate: (d: DeviceView) => void;
  onError: (message: string | null) => void;
}) {
  const [busy, setBusy] = useState(false);
  const label = channelLabel(channel);

  async function send(command: Parameters<PowerStationClient["command"]>[2]) {
    setBusy(true);
    onError(null);
    try {
      onUpdate(await client.command(device.deviceId, channel, command));
    } catch (err) {
      onError(`${label}: ${err instanceof ApiError ? err.message : String(err)}`);
    } finally {
      setBusy(false);
    }
  }

  const errors = channel.errors.map((e) => ERROR_TEXT[e] ?? e);
  const uncalibrated = channel.flags.includes("uncalibrated");

  return (
    <div className={c("tile", channel.on && "tile--on")}>
      <div className={c("tile-top")}>
        <span className={c("tile-name")} title={label}>
          {label}
        </span>
        <span className={c("state", channel.on && "state--on")}>{channel.on ? "On" : "Off"}</span>
      </div>

      <button
        type="button"
        className={c("button", "power")}
        aria-pressed={channel.on}
        aria-label={`${label}: turn ${channel.on ? "off" : "on"}`}
        disabled={disabled || busy}
        onClick={() => send({ action: channel.on ? "off" : "on" })}
      >
        {busy ? "…" : channel.on ? "Turn off" : "Turn on"}
      </button>

      {channel.kind === "Light" && (
        <Dimmer
          label={label}
          value={channel.brightness ?? 0}
          disabled={disabled || busy || uncalibrated}
          onCommit={(v) => send({ action: "brightness", brightness: v })}
        />
      )}

      {channel.metered && (
        <div className={c("meter")} aria-label={`${label} readings`}>
          {channel.powerW != null && <span className={c("meter-main")}>{fmt.watts(channel.powerW)}</span>}
          {channel.voltageV != null && <span>{fmt.volts(channel.voltageV)}</span>}
          {channel.currentA != null && <span>{fmt.amps(channel.currentA)}</span>}
          {channel.energyWh != null && <span>{fmt.energy(channel.energyWh)}</span>}
          {channel.temperatureC != null && <span>{fmt.temp(channel.temperatureC)}</span>}
        </div>
      )}

      {(errors.length > 0 || uncalibrated) && (
        <div className={c("row")}>
          {errors.map((e) => (
            <span key={e} className={c("badge", "badge--danger")}>
              {e}
            </span>
          ))}
          {uncalibrated && <span className={c("badge")}>Needs calibration</span>}
        </div>
      )}
    </div>
  );
}

/**
 * Brightness slider plus -/+ buttons (keyboard and touch friendly). The
 * slider shows the value while dragging and sends one command on release.
 */
function Dimmer({
  label,
  value,
  disabled,
  onCommit,
}: {
  label: string;
  value: number;
  disabled: boolean;
  onCommit: (v: number) => void;
}) {
  const [draft, setDraft] = useState<number | null>(null);
  const timer = useRef<number | undefined>(undefined);
  const settle = useRef<number | undefined>(undefined);
  useEffect(
    () => () => {
      window.clearTimeout(timer.current);
      window.clearTimeout(settle.current);
    },
    [],
  );
  // Hold the operator's value until the device reports the new level, so the
  // slider doesn't jump back while the command is in flight.
  useEffect(() => setDraft(null), [value]);

  const shown = Math.round(draft ?? value);
  const commit = (v: number) => {
    window.clearTimeout(timer.current);
    const clamped = Math.max(0, Math.min(100, Math.round(v)));
    if (clamped === Math.round(value)) {
      setDraft(null);
      return;
    }
    setDraft(clamped);
    window.clearTimeout(settle.current);
    settle.current = window.setTimeout(() => setDraft(null), 3000);
    onCommit(clamped);
  };

  return (
    <div className={c("dimmer")}>
      <button
        type="button"
        className={c("button", "button--small")}
        aria-label={`${label}: dimmer down 10%`}
        disabled={disabled || shown <= 0}
        onClick={() => commit(shown - 10)}
      >
        −
      </button>
      <input
        type="range"
        min={0}
        max={100}
        step={1}
        value={shown}
        disabled={disabled}
        aria-label={`${label} brightness`}
        aria-valuetext={`${shown} percent`}
        onChange={(e) => {
          const v = Number(e.currentTarget.value);
          setDraft(v);
          // Keyboard arrows fire change without a release event: commit after a pause.
          window.clearTimeout(timer.current);
          timer.current = window.setTimeout(() => commit(v), 400);
        }}
        onPointerUp={(e) => commit(Number(e.currentTarget.value))}
      />
      <button
        type="button"
        className={c("button", "button--small")}
        aria-label={`${label}: dimmer up 10%`}
        disabled={disabled || shown >= 100}
        onClick={() => commit(shown + 10)}
      >
        +
      </button>
      <span className={c("dim-value")} style={{ gridColumn: "1 / -1" }} aria-hidden="true">
        {shown}%
      </span>
    </div>
  );
}
