// SPDX-License-Identifier: GPL-2.0-or-later
// "Find devices" in setup: saved networks, a scan with progress, and
// results that can be added in place.
import { useCallback, useEffect, useId, useRef, useState } from "react";
import { ApiError, type DeviceView, type DiscoveryView, type FoundDevice, type PowerStationClient } from "./api";
import { Notice } from "./shared";
import { c } from "./styles";

const message = (err: unknown) => (err instanceof ApiError ? err.message : String(err));

export function FindDevices({
  client,
  onAdded,
  onShowStatus,
}: {
  client: PowerStationClient;
  onAdded: (d: DeviceView) => void;
  onShowStatus: () => void;
}) {
  const ids = useId();
  const [view, setView] = useState<DiscoveryView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [networkText, setNetworkText] = useState("");
  const [useMdns, setUseMdns] = useState(true);
  const [busy, setBusy] = useState(false);
  const alive = useRef(true);

  const load = useCallback(async () => {
    try {
      const next = await client.discovery();
      if (alive.current) setView(next);
    } catch (err) {
      if (alive.current) setError(message(err));
    }
  }, [client]);

  useEffect(() => {
    alive.current = true;
    void load();
    return () => {
      alive.current = false;
    };
  }, [load]);

  // Follow a running scan; stop polling when it's done or the panel closes.
  const running = view?.scan.running ?? false;
  useEffect(() => {
    if (!running) return;
    const timer = window.setInterval(() => void load(), 700);
    return () => window.clearInterval(timer);
  }, [running, load]);

  async function act(fn: () => Promise<DiscoveryView>) {
    setBusy(true);
    setError(null);
    try {
      const next = await fn();
      setView(next);
      return true;
    } catch (err) {
      setError(message(err));
      return false;
    } finally {
      setBusy(false);
    }
  }

  const saved = view?.settings.networks ?? [];
  const suggestions = (view?.suggested ?? []).filter((n) => !saved.includes(n));
  const scanTargets = saved.length > 0 ? saved : view?.suggested ?? [];
  const scan = view?.scan;
  const percent = scan && scan.total > 0 ? Math.min(100, Math.round((scan.probed / scan.total) * 100)) : 0;

  const saveNetworks = (networks: string[]) => act(() => client.saveDiscovery({ networks }));

  return (
    <div className={c("form")} role="group" aria-labelledby={`${ids}-t`}>
      <h3 className={c("section-title")} id={`${ids}-t`} style={{ margin: 0 }}>
        Find devices
      </h3>
      <p className={c("hint")} style={{ margin: 0 }}>
        Scans your networks for Shelly devices. Add each VLAN your Shelly devices live on; this computer must be able to
        reach them.
      </p>

      <div className={c("field")}>
        <span id={`${ids}-nets`} style={{ fontSize: 12, color: "var(--fg-2)" }}>
          Networks to scan
        </span>
        <div className={c("chips")} aria-labelledby={`${ids}-nets`}>
          {saved.map((n) => (
            <span className={c("chip")} key={n}>
              {n}
              <button
                type="button"
                aria-label={`Remove ${n}`}
                disabled={busy || running}
                onClick={() => saveNetworks(saved.filter((x) => x !== n))}
              >
                ×
              </button>
            </span>
          ))}
          {saved.length === 0 && (
            <span className={c("hint")}>
              {view?.suggested.length
                ? `None saved. A scan covers this computer's network${view.suggested.length === 1 ? "" : "s"}.`
                : "None saved yet."}
            </span>
          )}
        </div>
      </div>

      <form
        className={c("row")}
        onSubmit={async (e) => {
          e.preventDefault();
          const value = networkText.trim();
          if (!value) return;
          if (await saveNetworks([...saved, value])) setNetworkText("");
        }}
      >
        <div className={c("field")} style={{ flex: "1 1 180px" }}>
          <label htmlFor={`${ids}-net`} className={c("sr")}>
            Network to add
          </label>
          <input
            id={`${ids}-net`}
            value={networkText}
            placeholder="192.168.50.0/24"
            spellCheck={false}
            autoComplete="off"
            onChange={(e) => setNetworkText(e.currentTarget.value)}
          />
        </div>
        <button type="submit" className={c("button")} disabled={!networkText.trim() || busy || running}>
          Add network
        </button>
      </form>

      {suggestions.length > 0 && (
        <div className={c("chips")}>
          <span className={c("hint")}>This computer is on:</span>
          {suggestions.map((n) => (
            <button
              key={n}
              type="button"
              className={c("chip", "chip--suggest")}
              disabled={busy || running}
              onClick={() => saveNetworks([...saved, n])}
              aria-label={`Add ${n} to networks to scan`}
            >
              + {n}
            </button>
          ))}
        </div>
      )}

      <label className={c("check")}>
        <input type="checkbox" id={`${ids}-mdns`} checked={useMdns} onChange={(e) => setUseMdns(e.currentTarget.checked)} />
        <span>Also listen for devices announcing themselves on this computer's network (mDNS)</span>
      </label>
      <label className={c("check")}>
        <input
          type="checkbox"
          id={`${ids}-auto`}
          checked={view?.settings.autoRefind ?? true}
          disabled={!view || busy}
          onChange={(e) => {
            const autoRefind = e.currentTarget.checked;
            void act(() => client.saveDiscovery({ autoRefind }));
          }}
        />
        <span>
          Find devices automatically when their address changes
          <span className={c("hint")} style={{ display: "block" }}>
            If a device stops answering, PowerStation looks for it on its old network and the networks above, and moves it
            to its new address.
          </span>
        </span>
      </label>

      <div className={c("row")}>
        {running ? (
          <button type="button" className={c("button")} onClick={() => act(() => client.cancelScan())}>
            Stop scan
          </button>
        ) : (
          <button
            type="button"
            className={c("button", "button--primary")}
            disabled={busy || !view || (scanTargets.length === 0 && !useMdns)}
            onClick={() => act(() => client.startScan(useMdns))}
          >
            Scan now
          </button>
        )}
        {scan && (running || scan.finishedAt) && (
          <span className={c("hint")} role="status" aria-live="polite">
            {running
              ? scan.phase === "mdns"
                ? "Listening for devices announcing themselves…"
                : `Checking ${scan.probed.toLocaleString()} of ${scan.total.toLocaleString()} addresses…`
              : scan.cancelled
                ? `Stopped after ${scan.probed.toLocaleString()} addresses.`
                : `Checked ${scan.total.toLocaleString()} addresses on ${scan.networks.join(", ") || "this network"}.`}
          </span>
        )}
      </div>
      {running && (
        <div className={c("progress")} role="progressbar" aria-label="Scan progress" aria-valuenow={percent} aria-valuemin={0} aria-valuemax={100}>
          <span style={{ width: `${scan?.phase === "mdns" ? 4 : percent}%` }} />
        </div>
      )}
      {error && <Notice tone="error">{error}</Notice>}
      {scan?.error && <Notice tone="error">{scan.error}</Notice>}

      {scan && scan.found.length > 0 && (
        <div className={c("found")} aria-label="Devices found">
          {scan.found.map((f) => (
            <FoundRow
              key={f.device.deviceId}
              found={f}
              client={client}
              onAdded={(d) => {
                onAdded(d);
                void load();
              }}
              onShowStatus={onShowStatus}
            />
          ))}
        </div>
      )}
      {scan && !running && scan.finishedAt && scan.found.length === 0 && !scan.error && (
        <Notice tone="warn">
          No Shelly devices answered. Check the network ranges, and that a firewall between VLANs allows this computer to reach
          port 80 on the devices.
        </Notice>
      )}
    </div>
  );
}

function FoundRow({
  found,
  client,
  onAdded,
  onShowStatus,
}: {
  found: DiscoveryView["scan"]["found"][number];
  client: PowerStationClient;
  onAdded: (d: DeviceView) => void;
  onShowStatus: () => void;
}) {
  const ids = useId();
  const d: FoundDevice = found.device;
  const [open, setOpen] = useState(false);
  const [name, setName] = useState(d.name ?? "");
  const [password, setPassword] = useState("");
  const [username, setUsername] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [justAdded, setJustAdded] = useState(false);
  const title = d.name || d.app || d.model || d.deviceId;

  async function add() {
    setBusy(true);
    setError(null);
    try {
      onAdded(await client.addDevice(d.host, name, password, username));
      setJustAdded(true);
      setOpen(false);
    } catch (err) {
      setError(message(err));
    } finally {
      setBusy(false);
    }
  }

  const added = found.added || justAdded;
  return (
    <div className={c("found-row")}>
      <div>
        <div className={c("found-name")}>{title}</div>
        <div className={c("found-meta")}>
          {d.host} · {d.app ?? d.model ?? "Shelly"} · Gen{d.generation}
          {d.authRequired ? " · password set" : ""}
        </div>
        {found.addressUpdatedFrom && (
          <div className={c("hint")}>Address updated from {found.addressUpdatedFrom}.</div>
        )}
        {found.needsConfirmation && (
          <div className={c("hint")}>
            Already added at another address. It has a password, so confirm the new address on its card first.
          </div>
        )}
      </div>
      <div className={c("row")}>
        {added ? (
          <>
            <span className={c("badge", "badge--ok")}>Added</span>
            {justAdded && (
              <button type="button" className={c("button", "button--small")} onClick={onShowStatus}>
                Show on panel
              </button>
            )}
          </>
        ) : !d.supported ? (
          <span className={c("badge", "badge--muted")} title="PowerStation can't control this model yet">
            Not supported
          </span>
        ) : (
          <button
            type="button"
            className={c("button", "button--small", "button--primary")}
            aria-expanded={open}
            aria-controls={`${ids}-add`}
            onClick={() => setOpen(!open)}
          >
            {open ? "Cancel" : "Add"}
          </button>
        )}
      </div>
      {open && (
        <div className={c("found-add")} id={`${ids}-add`}>
          <div className={c("field")}>
            <label htmlFor={`${ids}-name`}>Name</label>
            <input id={`${ids}-name`} value={name} maxLength={64} placeholder={title} onChange={(e) => setName(e.currentTarget.value)} />
          </div>
          {d.authRequired && d.generation === 1 && (
            <div className={c("field")}>
              <label htmlFor={`${ids}-user`}>User name</label>
              <input id={`${ids}-user`} value={username} placeholder="admin" autoComplete="off" onChange={(e) => setUsername(e.currentTarget.value)} />
            </div>
          )}
          {d.authRequired && (
            <div className={c("field")}>
              <label htmlFor={`${ids}-pw`}>Device password</label>
              <input id={`${ids}-pw`} type="password" autoComplete="off" value={password} onChange={(e) => setPassword(e.currentTarget.value)} />
              {d.generation === 1 && (
                <span className={c("hint")}>Gen1 can't use a hashed password, so it's stored as entered on this computer.</span>
              )}
            </div>
          )}
          <button
            type="button"
            className={c("button", "button--primary")}
            disabled={busy || (d.authRequired && !password)}
            onClick={add}
          >
            {busy ? "Adding…" : "Add device"}
          </button>
          {error && <div style={{ flexBasis: "100%" }}><Notice tone="error">{error}</Notice></div>}
        </div>
      )}
    </div>
  );
}
