// SPDX-License-Identifier: GPL-2.0-or-later
import { useEffect, useRef, useState, type CSSProperties, type ReactNode, type RefObject } from "react";
import { ApiError, type ChannelState, type DeviceView, type PowerStationClient } from "./api";
import { BackendError, HealthLabel, Loading, Notice, channelLabel, fmt, type StatusState } from "./shared";
import { DeviceGrid, useLayout } from "./layout";
import { ScenesStrip } from "./scenes";
import { mismatch } from "./readings";
import { c } from "./styles";

const ALERT_TEXT: Record<string, string> = {
  voltageHigh: "High voltage",
  voltageLow: "Low voltage",
  currentHigh: "High current",
  currentLow: "Low current",
};

const ERROR_TEXT: Record<string, string> = {
  overpower: "Overpower",
  overtemp: "Overheated",
  overvoltage: "Overvoltage",
  undervoltage: "Undervoltage",
  overcurrent: "Overcurrent",
  unsupported_load: "Unsupported load",
};

/** Everyday view: the Scenes box, then the device cards in the operator's grid. */
export function StatusView({
  client,
  status,
  arranging,
  onDoneArranging,
  onGoToSetup,
}: {
  client: PowerStationClient;
  status: StatusState;
  arranging: boolean;
  onDoneArranging: () => void;
  onGoToSetup: () => void;
}) {
  const { data, error, loading } = status;
  const { layout, save, error: layoutError } = useLayout(client, data?.layout);

  if (loading && !data) return <Loading />;
  if (!data && error) return <BackendError error={error} onRetry={status.reload} />;
  if (!data) return null;
  if (data.devices.length === 0)
    return (
      <div className={c("empty")}>
        <strong>No devices yet</strong>
        <p>Find and add your Shelly relays, plugs and dimmers in setup.</p>
        <button type="button" className={c("button", "button--primary")} onClick={onGoToSetup}>
          Add a device
        </button>
      </div>
    );
  return (
    <>
      {error && <Notice tone="warn">Lost contact with PowerStation. Showing the last known state.</Notice>}
      <MainsBanner status={status} />
      <ScenesStrip client={client} status={status} />
      {arranging && (
        <div className={c("notice", "notice--ok", "row")} role="status">
          <span style={{ flex: 1, minWidth: 180 }}>
            Drag a card onto another to take its place, or use the arrows.
          </span>
          <button type="button" className={c("button", "button--small", "button--primary")} onClick={onDoneArranging}>
            Done arranging
          </button>
        </div>
      )}
      {layoutError && <Notice tone="warn">{layoutError}</Notice>}
      <h3 className={c("group-label")}>Devices</h3>
      <DeviceGrid
        devices={data.devices}
        layout={layout}
        arranging={arranging}
        onSave={save}
        renderCard={(d, controls) => (
          <DeviceCard device={d} client={client} onUpdate={status.applyDevice} controls={controls} arranging={arranging} />
        )}
      />
    </>
  );
}

function MainsBanner({ status }: { status: StatusState }) {
  const readings = status.data?.readings;
  const now = readings?.mainsNow;
  const a = now?.alert;
  if (!now || !a) return null;
  const low = a.kind === "voltageLow";
  return (
    <div className={c("mains-banner", a.level === "limit" && "mains-banner--limit")} role="status">
      <strong>{low ? "Low" : "High"} mains voltage: {fmt.volts(a.value)}</strong>
      <span>
        {a.level === "limit" ? (low ? "below the limit" : "above the limit") : low ? "below normal" : "above normal"} of{" "}
        {fmt.volts(a.threshold)} since {new Date(a.since).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}, seen on{" "}
        {now.outputs} output{now.outputs === 1 ? "" : "s"}.
        {readings?.measured && mismatch(readings.measured, readings.mains) &&
          " That's far from the mains setting: check the voltage choice in setup (Readings)."}
      </span>
    </div>
  );
}

function DeviceCard({
  device,
  client,
  onUpdate,
  controls,
  arranging,
}: {
  device: DeviceView;
  client: PowerStationClient;
  onUpdate: (d: DeviceView) => void;
  controls: ReactNode;
  arranging: boolean;
}) {
  const { health, message, channels } = device.status;
  const online = health === "Online";
  const [actionError, setActionError] = useState<string | null>(null);

  return (
    <article className={c("device")} aria-labelledby={`ps-${device.deviceId}`}>
      <div className={c("device-head")}>
        {arranging && <span className={c("grip")} aria-hidden="true">⠿</span>}
        <h3 className={c("device-name")} id={`ps-${device.deviceId}`} title={device.displayName}>
          {device.displayName}
        </h3>
        <span className={c("device-meta")}>{device.app ?? device.model ?? ""}</span>
        {controls ?? <HealthLabel device={device} />}
      </div>
      <div className={c("device-body")} inert={arranging ? true : undefined}>
        {!online && health !== "Pending" && (
          <Notice tone={health === "Unreachable" ? "error" : "warn"}>
            {health === "Unauthorized"
              ? "This device needs its password. Enter it in setup (the gear, top right)."
              : message ?? "The device isn't answering."}
          </Notice>
        )}
        {device.previousHost && device.hostChangedAt &&
          Date.now() - new Date(device.hostChangedAt).getTime() < 24 * 3600 * 1000 && (
            <p className={c("hint")} style={{ margin: "0 0 6px" }}>
              Found at a new address, {device.host} (was {device.previousHost}).
            </p>
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

  const alerts = channel.alerts ?? [];
  const tone = (kinds: string[]) => {
    const a = alerts.filter((x) => kinds.includes(x.kind));
    return a.some((x) => x.level === "limit") ? "reading--limit" : a.length ? "reading--warn" : null;
  };
  const readings = channel.metered && (
    <div className={c("meter")} aria-label={`${label} readings`}>
      {channel.powerW != null && <span className={c("meter-main")}>{fmt.watts(channel.powerW)}</span>}
      {channel.voltageV != null && <span className={c(tone(["voltageHigh", "voltageLow"]))}>{fmt.volts(channel.voltageV)}</span>}
      {channel.currentA != null && <span className={c(tone(["currentHigh", "currentLow"]))}>{fmt.amps(channel.currentA)}</span>}
      {channel.energyWh != null && <span>{fmt.energy(channel.energyWh)}</span>}
      {channel.temperatureC != null && <span>{fmt.temp(channel.temperatureC)}</span>}
    </div>
  );
  // Voltage is one supply for the whole station: it's colored here and reported once in the mains banner.
  const currentAlerts = alerts.filter((a) => a.kind.startsWith("current"));
  const badges = (errors.length > 0 || uncalibrated || currentAlerts.length > 0) && (
    <div className={c("row")}>
      {currentAlerts.map((a) => (
        <span
          key={a.kind}
          className={c("badge", a.level === "limit" && "badge--danger")}
          title={`${ALERT_TEXT[a.kind]}: ${a.kind.startsWith("voltage") ? fmt.volts(a.value) : fmt.amps(a.value)}, ${
            a.kind.endsWith("High") ? "above" : "below"
          } ${a.kind.startsWith("voltage") ? fmt.volts(a.threshold) : fmt.amps(a.threshold)} since ${new Date(a.since).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`}
        >
          {ALERT_TEXT[a.kind]}
        </span>
      ))}
      {errors.map((e) => (
        <span key={e} className={c("badge", "badge--danger")}>
          {e}
        </span>
      ))}
      {uncalibrated && <span className={c("badge")}>Needs calibration</span>}
    </div>
  );
  const header = (
    <div className={c("tile-top")}>
      <span className={c("tile-label")}>
        <Led channel={channel} stale={disabled} />
        <span className={c("tile-name")} title={label}>
          {label}
        </span>
      </span>
      <span className={c("state", channel.on && "state--on")}>{channel.on ? "On" : "Off"}</span>
    </div>
  );

  const safety = channel.safetyMinutes ? (
    <span className={c("safety")} title={`Safety timer: the device turns this off ${channel.safetyMinutes} min after Zeus stops renewing it`}>
      <TimerIcon /> {channel.safetyMinutes} min
    </span>
  ) : null;

  if (channel.kind === "Meter")
    return (
      <div className={c("tile", "tile--meter")}>
        <div className={c("tile-top")}>
          <span className={c("tile-label")}>
            <span className={c("meter-icon")} aria-hidden="true">≈</span>
            <span className={c("tile-name")} title={label}>
              {label}
            </span>
          </span>
          <span className={c("state")}>Meter</span>
        </div>
        {readings}
        {badges}
      </div>
    );

  if (channel.kind === "Light")
    return (
      <div className={c("tile", "tile--dimmer", channel.on && "tile--on")}>
        {header}
        <WallDimmer
          label={label}
          on={channel.on}
          value={channel.brightness ?? 0}
          disabled={disabled || busy}
          levelDisabled={uncalibrated}
          onToggle={() => send({ action: channel.on ? "off" : "on" })}
          onLevel={(v) => send({ action: "brightness", brightness: v })}
        >
          {readings}
          {safety}
        </WallDimmer>
        {badges}
      </div>
    );

  return (
    <div className={c("tile", channel.on && "tile--on")}>
      {header}
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
      {readings}
      {safety}
      {badges}
    </div>
  );
}

function TimerIcon() {
  return (
    <svg viewBox="0 0 16 16" width="11" height="11" aria-hidden="true" focusable="false">
      <circle cx="8" cy="9" r="5.5" fill="none" stroke="currentColor" strokeWidth="1.5" />
      <path d="M8 6v3l2 1.5M6 1.5h4" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
    </svg>
  );
}

/**
 * State lamp: green on, orange off, blue for a dimmer that's on below 100%.
 * Grey when the device isn't answering, since the last state may be stale.
 * Decorative: the ON/OFF text next to it carries the same information.
 */
function Led({ channel, stale }: { channel: ChannelState; stale: boolean }) {
  const dimmed = channel.kind === "Light" && channel.on && (channel.brightness ?? 100) < 100;
  const tone = stale ? null : dimmed ? "led--dim" : channel.on ? "led--on" : "led--off";
  const title = stale
    ? "State unknown: device not answering"
    : dimmed
      ? `Dimmed to ${Math.round(channel.brightness ?? 0)}%`
      : channel.on
        ? "On"
        : "Off";
  return <span className={c("led", tone)} title={title} aria-hidden="true" />;
}

/** Level of each of the 7 dots on the wall dimmer, bottom to top. */
const DOT_LEVELS = [14, 29, 43, 57, 71, 86, 100];

/**
 * True when the panel's background is dark. The dimmer drawing is always a
 * light-colored plate like the real device, so its details need contrast
 * picked from the other end of the theme.
 */
function useDarkBackground(ref: RefObject<HTMLElement | null>) {
  const [dark, setDark] = useState(true);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    let node: HTMLElement | null = el;
    let bg = "";
    while (node && (!bg || bg === "rgba(0, 0, 0, 0)" || bg === "transparent")) {
      bg = getComputedStyle(node).backgroundColor;
      node = node.parentElement;
    }
    const m = bg.match(/\d+(\.\d+)?/g);
    if (m && m.length >= 3) {
      const [r, g, b] = m.slice(0, 3).map(Number);
      setDark(0.2126 * r + 0.7152 * g + 0.0722 * b < 128);
    }
  });
  return dark;
}

/**
 * A drawing of the wall dimmer: a plate with a column of 7 level dots and a
 * square on/off button, like the real device. Dots light from the bottom up
 * with the brightness; click a dot to jump to that level, or the square to
 * switch on and off. − and + step by 10% for finer control and keyboards.
 */
function WallDimmer({
  label,
  on,
  value,
  disabled,
  levelDisabled,
  onToggle,
  onLevel,
  children,
}: {
  label: string;
  on: boolean;
  value: number;
  disabled: boolean;
  levelDisabled: boolean;
  onToggle: () => void;
  onLevel: (v: number) => void;
  children?: ReactNode;
}) {
  const ref = useRef<HTMLDivElement | null>(null);
  const dark = useDarkBackground(ref);
  const [draft, setDraft] = useState<number | null>(null);
  const settle = useRef<number | undefined>(undefined);
  useEffect(() => () => window.clearTimeout(settle.current), []);
  // Show the requested level until the device reports it (or 3 s pass).
  useEffect(() => setDraft(null), [value, on]);

  const level = Math.round(draft ?? value);
  const lit = on ? Math.max(1, Math.ceil((level / 100) * DOT_LEVELS.length)) : 0;
  const set = (v: number) => {
    const clamped = Math.max(1, Math.min(100, Math.round(v)));
    if (on && clamped === Math.round(value)) return;
    setDraft(clamped);
    window.clearTimeout(settle.current);
    settle.current = window.setTimeout(() => setDraft(null), 3000);
    onLevel(clamped);
  };

  const plateStyle = {
    "--wd-face": dark ? "var(--fg-0)" : "var(--bg-inset)",
    "--wd-edge": dark ? "var(--fg-2)" : "var(--line-strong)",
    "--wd-mark": dark ? "var(--bg-1)" : "var(--fg-1)",
    "--wd-dot": dark ? "var(--fg-3)" : "var(--fg-3)",
  } as CSSProperties;

  return (
    <div className={c("wd")} ref={ref}>
      <div className={c("wd-plate")} style={plateStyle}>
        <span className={c("wd-screw")} aria-hidden="true" />
        <div className={c("wd-paddle")}>
          <div className={c("wd-channel")} role="group" aria-label={`${label} level`}>
            {[...DOT_LEVELS].reverse().map((lvl) => {
              const index = DOT_LEVELS.indexOf(lvl);
              return (
                <button
                  key={lvl}
                  type="button"
                  className={c("wd-dot", index < lit && "wd-dot--lit")}
                  aria-label={`${label}: set to ${lvl}%`}
                  disabled={disabled || levelDisabled}
                  onClick={() => set(lvl)}
                >
                  <span />
                </button>
              );
            })}
          </div>
          <button
            type="button"
            className={c("wd-square", on && "wd-square--on")}
            aria-pressed={on}
            aria-label={`${label}: turn ${on ? "off" : "on"}`}
            disabled={disabled}
            onClick={onToggle}
          >
            <span />
          </button>
        </div>
        <span className={c("wd-screw")} aria-hidden="true" />
      </div>
      <div className={c("wd-side")}>
        <div className={c("wd-steps")}>
          <button
            type="button"
            className={c("step", "step--down")}
            aria-label={`${label}: dimmer down 10%`}
            disabled={disabled || levelDisabled || !on || level <= 1}
            onClick={() => set(level - 10)}
          >
            −
          </button>
          <span className={c("wd-level")} aria-live="polite">
            {on ? `${level}%` : "Off"}
          </span>
          <button
            type="button"
            className={c("step", "step--up")}
            aria-label={`${label}: dimmer up 10%`}
            disabled={disabled || levelDisabled || level >= 100}
            onClick={() => set(on ? level + 10 : Math.max(level, 10))}
          >
            +
          </button>
        </div>
        {children}
      </div>
    </div>
  );
}
