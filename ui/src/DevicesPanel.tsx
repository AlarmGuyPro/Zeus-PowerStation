// SPDX-License-Identifier: GPL-2.0-or-later
import { useId, useState, type FormEvent } from "react";
import { ApiError, type DeviceView, type PowerStationClient, type ProbeResponse } from "./api";
import { BackendError, HealthLabel, Loading, Notice, PanelRoot, channelLabel, useStatus } from "./shared";
import { ScenesSettings } from "./scenes";
import { c } from "./styles";

const message = (err: unknown) => (err instanceof ApiError ? err.message : String(err));

export function DevicesPanel({ client }: { client: PowerStationClient }) {
  const status = useStatus(client, 5000);
  const { data, error, loading } = status;

  return (
    <PanelRoot label="PowerStation devices">
      <div className={c("header")}>
        <h2 className={c("title")}>PowerStation Devices</h2>
        {data && <span className={c("summary")}>v{data.version}</span>}
      </div>
      <AddDeviceForm client={client} onAdded={status.applyDevice} />
      {loading && !data && <Loading />}
      {!data && error && <BackendError error={error} onRetry={status.reload} />}
      {data && data.devices.length === 0 && (
        <p className={c("hint")}>No devices yet. Add one by its IP address above.</p>
      )}
      {data?.devices.map((d) => (
        <DeviceSettings
          key={d.deviceId}
          device={d}
          client={client}
          onUpdate={status.applyDevice}
          onRemoved={status.removeDevice}
        />
      ))}
      {data && data.devices.length > 0 && <ScenesSettings client={client} status={status} />}
    </PanelRoot>
  );
}

function AddDeviceForm({ client, onAdded }: { client: PowerStationClient; onAdded: (d: DeviceView) => void }) {
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
        Add a device
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
            <span className={c("hint")}>Checked with the device, then stored only as a one-way hash.</span>
          </div>
        )}
      </div>

      {probe && (
        <Notice tone={probe.supported ? "ok" : "warn"}>
          Found {probe.app ?? probe.model ?? "a Shelly"} ({probe.deviceId}), Gen{probe.generation}
          {probe.firmware ? `, firmware ${probe.firmware}` : ""}.
          {probe.authRequired ? " It has a password set." : ""}
          {!probe.supported ? " Gen1 devices are supported in the next PowerStation update." : ""}
        </Notice>
      )}
      {error && <Notice tone="error">{error}</Notice>}
      {added && <Notice tone="ok">Added {added}. It now appears in the PowerStation panel.</Notice>}

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
            <dt>Password</dt>
            <dd>{device.authRequired ? (device.hasCredential ? "Saved (hashed)" : "Required, not saved") : "None set on device"}</dd>
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
                      {ch.kind === "Light" ? "Dimmer" : "Output"} {ch.index + 1}
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
              <span className={c("hint")}>Use the password you set in the Shelly app. Stored only as a one-way hash.</span>
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
