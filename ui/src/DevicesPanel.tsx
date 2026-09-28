// SPDX-License-Identifier: GPL-2.0-or-later
import { useId, useState, type FormEvent } from "react";
import { ApiError, type DeviceView, type PowerStationClient, type ProbeResponse } from "./api";
import { BackendError, HealthLabel, Loading, Notice, channelLabel, type StatusState } from "./shared";
import { FindDevices } from "./discovery";
import { ScenesSettings } from "./scenes";
import { AutomationsSettings } from "./automations";
import { ReadingsSettings } from "./readings";
import { COLUMN_CHOICES, normalize, useLayout } from "./layout";
import { c } from "./styles";

const message = (err: unknown) => (err instanceof ApiError ? err.message : String(err));

export type SetupSection = "devices" | "scenes" | "automations" | "readings" | "layout";

/** Setup, behind the gear: devices, scenes, automations and the card layout. */
export function SetupView({
  client,
  status,
  section,
  onShowStatus,
  onArrange,
}: {
  client: PowerStationClient;
  status: StatusState;
  section: SetupSection;
  onShowStatus: () => void;
  onArrange: () => void;
}) {
  const { data, error, loading } = status;
  if (loading && !data) return <Loading />;
  if (!data && error) return <BackendError error={error} onRetry={status.reload} />;
  return (
    <>
      {section === "devices" && (
        <>
          <FindDevices client={client} onAdded={status.applyDevice} onShowStatus={onShowStatus} />
          <AddDeviceForm client={client} onAdded={status.applyDevice} onShowStatus={onShowStatus} />
          {data && data.devices.length === 0 && <p className={c("hint")}>No devices yet. Scan, or add one by its address above.</p>}
          {data && data.devices.length > 0 && <h3 className={c("section-title")}>Your devices</h3>}
          {data?.devices.map((d) => (
            <DeviceSettings key={d.deviceId} device={d} client={client} onUpdate={status.applyDevice} onRemoved={status.removeDevice} />
          ))}
        </>
      )}
      {section === "scenes" &&
        (data && data.devices.length > 0 ? (
          <ScenesSettings client={client} status={status} />
        ) : (
          <p className={c("hint")}>Add a device first.</p>
        ))}
      {section === "automations" && <AutomationsSettings client={client} status={status} />}
      {section === "readings" && <ReadingsSettings client={client} status={status} />}
      {section === "layout" && data && <LayoutSettings client={client} status={status} onArrange={onArrange} />}
      {data && <p className={c("hint")} style={{ marginTop: 12 }}>PowerStation v{data.version}</p>}
    </>
  );
}

function LayoutSettings({ client, status, onArrange }: { client: PowerStationClient; status: StatusState; onArrange: () => void }) {
  const data = status.data!;
  const { layout, save, error } = useLayout(client, data.layout);
  return (
    <section aria-labelledby="ps-layout-title">
      <h3 className={c("section-title")} id="ps-layout-title" style={{ marginTop: 0 }}>
        Card grid
      </h3>
      <p className={c("hint")} style={{ margin: "0 0 8px" }}>
        Device cards snap to equal cells that fill the panel. Auto fits as many columns as the panel allows. A number keeps
        that many columns unless the panel is too narrow for it.
      </p>
      <div className={c("row")} style={{ marginBottom: 12 }}>
        <div className={c("seg")} role="radiogroup" aria-label="Columns">
          <span className={c("seg-label")} aria-hidden="true">
            Columns
          </span>
          {COLUMN_CHOICES.map((n) => (
            <button
              key={n}
              type="button"
              role="radio"
              aria-checked={layout.columns === n}
              className={c("seg-btn")}
              onClick={() => save({ columns: n, order: normalize({ columns: 1, order: [layout.order.flat()] }, data.devices) })}
              title={n === 0 ? "Fit as many as the panel allows" : `${n} column${n === 1 ? "" : "s"}`}
            >
              {n === 0 ? "Auto" : n}
            </button>
          ))}
        </div>
      </div>
      {error && <Notice tone="warn">{error}</Notice>}
      <h3 className={c("section-title")}>Card order</h3>
      <p className={c("hint")} style={{ margin: "0 0 8px" }}>
        Arrange closes setup and lets you drag the cards into place on the panel itself.
      </p>
      <button type="button" className={c("button")} disabled={data.devices.length < 2} onClick={onArrange}>
        Arrange cards…
      </button>
    </section>
  );
}

function AddDeviceForm({
  client,
  onAdded,
  onShowStatus,
}: {
  client: PowerStationClient;
  onAdded: (d: DeviceView) => void;
  onShowStatus: () => void;
}) {
  const ids = useId();
  const [host, setHost] = useState("");
  const [name, setName] = useState("");
  const [password, setPassword] = useState("");
  const [probe, setProbe] = useState<ProbeResponse | null>(null);
  const [busy, setBusy] = useState<"check" | "add" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [added, setAdded] = useState<string | null>(null);
  const [needsPassword, setNeedsPassword] = useState(false);

  async function check() {
    setBusy("check");
    setError(null);
    setAdded(null);
    try {
      const found = await client.probe(host);
      setProbe(found);
      setNeedsPassword(found.authRequired);
      if (!name && found.defaultName) setName(found.defaultName);
    } catch (err) {
      setProbe(null);
      setError(message(err));
    } finally {
      setBusy(null);
    }
  }

  async function add(e: FormEvent) {
    e.preventDefault();
    setBusy("add");
    setError(null);
    setAdded(null);
    try {
      const device = await client.addDevice(host, name, password);
      onAdded(device);
      setAdded(device.displayName);
      setHost("");
      setName("");
      setPassword("");
      setProbe(null);
      setNeedsPassword(false);
    } catch (err) {
      if (err instanceof ApiError && err.status === 400 && /password/i.test(err.message)) setNeedsPassword(true);
      setError(message(err));
    } finally {
      setBusy(null);
    }
  }

  return (
    <form className={c("form")} onSubmit={add} aria-labelledby={`${ids}-t`}>
      <h3 className={c("section-title")} id={`${ids}-t`} style={{ margin: 0 }}>
        Add by address
      </h3>
      <div className={c("fields")}>
        <div className={c("field")}>
          <label htmlFor={`${ids}-host`}>IP address or hostname</label>
          <input
            id={`${ids}-host`}
            value={host}
            onChange={(e) => {
              setHost(e.currentTarget.value);
              setProbe(null);
            }}
            placeholder="192.168.1.50"
            autoComplete="off"
            spellCheck={false}
            required
          />
        </div>
        <div className={c("field")}>
          <label htmlFor={`${ids}-name`}>Name (optional)</label>
          <input id={`${ids}-name`} value={name} onChange={(e) => setName(e.currentTarget.value)} placeholder="Amp PSU" maxLength={64} />
        </div>
        {needsPassword && (
          <div className={c("field")}>
            <label htmlFor={`${ids}-pw`}>Device password</label>
            <input
              id={`${ids}-pw`}
              type="password"
              value={password}
              onChange={(e) => setPassword(e.currentTarget.value)}
              autoComplete="off"
              required
            />
            <span className={c("hint")}>
              {probe?.generation === 1
                ? "Gen1 devices can't use a hashed password, so PowerStation stores this one as entered, in Zeus's settings on this computer."
                : "Checked with the device, then stored only as a one-way hash."}
            </span>
          </div>
        )}
      </div>

      {probe && (
        <Notice tone={probe.supported ? "ok" : "warn"}>
          Found {probe.app ?? probe.model ?? "a Shelly"} ({probe.deviceId}), Gen{probe.generation}
          {probe.firmware ? `, firmware ${probe.firmware}` : ""}.
          {probe.authRequired ? " It has a password set." : ""}
          {!probe.supported ? " This model isn't supported yet." : ""}
        </Notice>
      )}
      {error && <Notice tone="error">{error}</Notice>}
      {added && (
        <div className={c("notice", "notice--ok", "row")} role="status">
          <span>Added {added}.</span>
          <button type="button" className={c("button", "button--small", "button--primary")} onClick={onShowStatus}>
            Show on panel
          </button>
        </div>
      )}

      <div className={c("row")}>
        <button type="button" className={c("button")} disabled={!host.trim() || busy !== null} onClick={check}>
          {busy === "check" ? "Checking…" : "Check address"}
        </button>
        <button
          type="submit"
          className={c("button", "button--primary")}
          disabled={!host.trim() || busy !== null || (probe !== null && !probe.supported)}
        >
          {busy === "add" ? "Adding…" : "Add device"}
        </button>
      </div>
      <span className={c("hint")}>
        Devices on another VLAN work too, as long as this computer can reach them. Set the device up in the Shelly app
        first.
      </span>
    </form>
  );
}

function DeviceSettings({
  device,
  client,
  onUpdate,
  onRemoved,
}: {
  device: DeviceView;
  client: PowerStationClient;
  onUpdate: (d: DeviceView) => void;
  onRemoved: (id: string) => void;
}) {
  const ids = useId();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState(device.name ?? "");
  const [host, setHost] = useState(device.host);
  const [password, setPassword] = useState("");
  const [channelNames, setChannelNames] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [confirmRemove, setConfirmRemove] = useState(false);

  async function run(label: string, action: () => Promise<DeviceView | void>, done?: string) {
    setBusy(label);
    setError(null);
    setSaved(null);
    try {
      const result = await action();
      if (result) onUpdate(result);
      if (done) setSaved(done);
    } catch (err) {
      setError(message(err));
    } finally {
      setBusy(null);
    }
  }

  const channels = device.status.channels;
  const dirtyNames = Object.keys(channelNames).length > 0;
  const [safety, setSafety] = useState<Record<string, string>>({});
  const dirtySafety = Object.keys(safety).length > 0;
  const switchable = channels.filter((ch) => ch.kind !== "Meter");

  return (
    <article className={c("device")} aria-labelledby={`${ids}-n`}>
      <div className={c("device-head")}>
        <h3 className={c("device-name")} id={`${ids}-n`}>
          {device.displayName}
        </h3>
        <span className={c("device-meta")}>{device.host}</span>
        <HealthLabel device={device} />
        <button
          type="button"
          className={c("button", "button--small")}
          aria-expanded={open}
          aria-controls={`${ids}-body`}
          onClick={() => setOpen(!open)}
        >
          {open ? "Close" : "Edit"}
        </button>
      </div>
      {open && (
        <div className={c("device-body")} id={`${ids}-body`}>
          <dl className={c("kv")}>
            <dt>Model</dt>
            <dd>{device.app ?? device.model ?? "Unknown"}</dd>
            <dt>Device ID</dt>
            <dd>{device.deviceId}</dd>
            <dt>Generation</dt>
            <dd>Gen{device.generation}</dd>
            {device.previousHost && (
              <>
                <dt>Moved</dt>
                <dd>
                  from {device.previousHost}
                  {device.hostChangedAt ? ` on ${new Date(device.hostChangedAt).toLocaleString()}` : ""}
                </dd>
              </>
            )}
            <dt>Password</dt>
            <dd>
              {device.authRequired
                ? device.hasCredential
                  ? device.generation === 1
                    ? "Saved (Gen1: stored as entered)"
                    : "Saved (hashed)"
                  : "Required, not saved"
                : "None set on device"}
            </dd>
          </dl>
          {device.status.health !== "Online" && device.status.message && (
            <Notice tone="warn">{device.status.message}</Notice>
          )}
          {error && <Notice tone="error">{error}</Notice>}
          {saved && <Notice tone="ok">{saved}</Notice>}

          <div className={c("fields")}>
            <div className={c("field")}>
              <label htmlFor={`${ids}-name`}>Name</label>
              <input id={`${ids}-name`} value={name} maxLength={64} onChange={(e) => setName(e.currentTarget.value)} />
            </div>
            <div className={c("field")}>
              <label htmlFor={`${ids}-host`}>Address</label>
              <input id={`${ids}-host`} value={host} spellCheck={false} onChange={(e) => setHost(e.currentTarget.value)} />
            </div>
          </div>
          <div className={c("row")} style={{ marginTop: 8 }}>
            <button
              type="button"
              className={c("button", "button--primary")}
              disabled={busy !== null || (name === (device.name ?? "") && host === device.host)}
              onClick={() =>
                run(
                  "save",
                  () =>
                    client.updateDevice(device.deviceId, {
                      name: name === (device.name ?? "") ? undefined : name,
                      host: host === device.host ? undefined : host,
                    }),
                  "Saved.",
                )
              }
            >
              {busy === "save" ? "Saving…" : "Save"}
            </button>
            <button
              type="button"
              className={c("button")}
              disabled={busy !== null}
              onClick={() => run("refresh", () => client.refresh(device.deviceId))}
            >
              {busy === "refresh" ? "Checking…" : "Check now"}
            </button>
          </div>

          {channels.length > 0 && (
            <>
              <h4 className={c("section-title")}>Output names</h4>
              <div className={c("fields")}>
                {channels.map((ch) => (
                  <div className={c("field")} key={ch.key}>
                    <label htmlFor={`${ids}-${ch.key}`}>
                      {ch.kind === "Light" ? "Dimmer" : ch.kind === "Meter" ? "Meter" : "Output"} {ch.index + 1}
                    </label>
                    <input
                      id={`${ids}-${ch.key}`}
                      maxLength={64}
                      placeholder={channelLabel({ ...ch, name: null })}
                      value={channelNames[ch.key] ?? ch.name ?? ""}
                      onChange={(e) => {
                        const value = e.currentTarget.value;
                        setChannelNames((prev) => ({ ...prev, [ch.key]: value }));
                      }}
                    />
                  </div>
                ))}
              </div>
              <div className={c("row")} style={{ marginTop: 8 }}>
                <button
                  type="button"
                  className={c("button")}
                  disabled={!dirtyNames || busy !== null}
                  onClick={() =>
                    run(
                      "names",
                      async () => {
                        const result = await client.updateDevice(device.deviceId, {
                          channelNames: Object.fromEntries(
                            Object.entries(channelNames).map(([k, v]) => [k, v.trim() || null]),
                          ),
                        });
                        setChannelNames({});
                        return result;
                      },
                      "Output names saved.",
                    )
                  }
                >
                  {busy === "names" ? "Saving…" : "Save output names"}
                </button>
              </div>
            </>
          )}

          {switchable.length > 0 && (
            <>
              <h4 className={c("section-title")}>Safety timers</h4>
              <p className={c("hint")} style={{ margin: "0 0 6px" }}>
                The device itself turns the output off this many minutes after Zeus stops renewing the timer, so an amplifier
                or supply doesn't stay on if Zeus crashes or the computer loses power. Leave blank for none.
              </p>
              <div className={c("fields")}>
                {switchable.map((ch) => (
                  <div className={c("field")} key={ch.key}>
                    <label htmlFor={`${ids}-safe-${ch.key}`}>{channelLabel(ch)} (minutes)</label>
                    <input
                      id={`${ids}-safe-${ch.key}`}
                      type="number"
                      min={1}
                      max={1440}
                      placeholder="None"
                      value={safety[ch.key] ?? (ch.safetyMinutes ? String(ch.safetyMinutes) : "")}
                      onChange={(e) => {
                        const value = e.currentTarget.value;
                        setSafety((prev) => ({ ...prev, [ch.key]: value }));
                      }}
                    />
                  </div>
                ))}
              </div>
              <div className={c("row")} style={{ marginTop: 8 }}>
                <button
                  type="button"
                  className={c("button")}
                  disabled={!dirtySafety || busy !== null}
                  onClick={() =>
                    run(
                      "safety",
                      async () => {
                        const result = await client.updateDevice(device.deviceId, {
                          safetyMinutes: Object.fromEntries(
                            Object.entries(safety).map(([k, v]) => [k, v.trim() === "" ? null : Number(v)]),
                          ),
                        });
                        setSafety({});
                        return result;
                      },
                      "Safety timers saved.",
                    )
                  }
                >
                  {busy === "safety" ? "Saving…" : "Save safety timers"}
                </button>
              </div>
            </>
          )}

          <h4 className={c("section-title")}>Password</h4>
          <div className={c("fields")}>
            <div className={c("field")}>
              <label htmlFor={`${ids}-pw`}>{device.hasCredential ? "Change password" : "Device password"}</label>
              <input
                id={`${ids}-pw`}
                type="password"
                autoComplete="off"
                value={password}
                onChange={(e) => setPassword(e.currentTarget.value)}
              />
              <span className={c("hint")}>
                Use the password you set in the Shelly app.{" "}
                {device.generation === 1
                  ? "Gen1 devices can't use a hashed password, so it's stored as entered in Zeus's settings on this computer."
                  : "Stored only as a one-way hash."}
              </span>
            </div>
          </div>
          <div className={c("row")} style={{ marginTop: 8 }}>
            <button
              type="button"
              className={c("button")}
              disabled={!password || busy !== null}
              onClick={() =>
                run(
                  "password",
                  async () => {
                    const result = await client.updateDevice(device.deviceId, { password });
                    setPassword("");
                    return result;
                  },
                  "Password checked and saved.",
                )
              }
            >
              {busy === "password" ? "Checking…" : "Save password"}
            </button>
            {device.hasCredential && (
              <button
                type="button"
                className={c("button")}
                disabled={busy !== null}
                onClick={() => run("clear", () => client.updateDevice(device.deviceId, { clearPassword: true }), "Password forgotten.")}
              >
                Forget password
              </button>
            )}
          </div>

          <h4 className={c("section-title")}>Remove</h4>
          <div className={c("row")}>
            {!confirmRemove ? (
              <button type="button" className={c("button", "button--danger")} disabled={busy !== null} onClick={() => setConfirmRemove(true)}>
                Remove device…
              </button>
            ) : (
              <>
                <span>Remove {device.displayName} from PowerStation? The device itself isn't changed.</span>
                <button
                  type="button"
                  className={c("button", "button--danger")}
                  disabled={busy !== null}
                  onClick={() =>
                    run("remove", async () => {
                      await client.removeDevice(device.deviceId);
                      onRemoved(device.deviceId);
                    })
                  }
                >
                  {busy === "remove" ? "Removing…" : "Yes, remove"}
                </button>
                <button type="button" className={c("button")} onClick={() => setConfirmRemove(false)}>
                  Cancel
                </button>
              </>
            )}
          </div>
        </div>
      )}
    </article>
  );
}
