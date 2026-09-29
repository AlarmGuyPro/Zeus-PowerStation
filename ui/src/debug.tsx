// SPDX-License-Identifier: GPL-2.0-or-later
// Setup › Debug: the traffic between PowerStation and your devices, with
// filters, so a device that won't connect shows exactly what it answered.
import { useEffect, useMemo, useRef, useState } from "react";
import { ApiError, type PowerStationClient, type TrafficEntry } from "./api";
import { Notice, type StatusState } from "./shared";
import { c } from "./styles";

const KEEP = 500;
const message = (err: unknown) => (err instanceof ApiError ? err.message : String(err));

function time(iso: string) {
  const d = new Date(iso);
  return `${d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false })}.${String(d.getMilliseconds()).padStart(3, "0")}`;
}

function pretty(text?: string | null) {
  if (!text) return "";
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}

function asText(entries: TrafficEntry[]) {
  return entries
    .map((e) =>
      [
        `${e.at}  ${e.deviceName ?? e.deviceId ?? "-"}  ${e.host ?? ""}  ${e.method}  ${e.ok ? "OK" : "ERROR"}${e.status ? ` HTTP ${e.status}` : ""}${e.ms != null ? ` ${e.ms} ms` : ""}`,
        e.error ? `  error: ${e.error}` : null,
        e.request ? `  request: ${e.request}` : null,
        e.response ? `  response: ${e.response}` : null,
      ]
        .filter(Boolean)
        .join("\n"),
    )
    .join("\n");
}

export function DebugLog({ client, status }: { client: PowerStationClient; status: StatusState }) {
  const devices = status.data?.devices ?? [];
  const [entries, setEntries] = useState<TrafficEntry[]>([]);
  const [enabled, setEnabled] = useState(false);
  const [recordAll, setRecordAll] = useState(false);
  const [until, setUntil] = useState<string | null>(null);
  const [device, setDevice] = useState<string>("*");
  const [text, setText] = useState("");
  const [errorsOnly, setErrorsOnly] = useState(false);
  const [hideRoutine, setHideRoutine] = useState(false);
  const [paused, setPaused] = useState(false);
  const [open, setOpen] = useState<Set<number>>(new Set());
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const since = useRef(0);

  useEffect(() => {
    if (paused) return;
    let alive = true;
    const load = async () => {
      try {
        const r = await client.debugLog(since.current);
        if (!alive) return;
        setEnabled(r.enabled);
        setRecordAll(r.recordAll);
        setUntil(r.recordAllUntil ?? null);
        if (r.latest < since.current) {
          // The log was cleared (or PowerStation restarted): start over.
          since.current = 0;
          setEntries(r.entries);
        } else if (r.entries.length) setEntries((prev) => [...prev, ...r.entries].slice(-KEEP));
        since.current = Math.max(since.current, r.latest);
        setError(null);
      } catch (err) {
        if (alive) setError(message(err));
      }
    };
    void load();
    const t = window.setInterval(() => {
      if (document.visibilityState !== "hidden") void load();
    }, 2000);
    return () => {
      alive = false;
      window.clearInterval(t);
    };
  }, [client, paused]);

  const shown = useMemo(() => {
    const needle = text.trim().toLowerCase();
    return entries
      .filter((e) => device === "*" || (device === "" ? !e.deviceId : e.deviceId === device))
      .filter((e) => !errorsOnly || !e.ok)
      .filter((e) => !hideRoutine || !e.routine || !e.ok)
      .filter(
        (e) =>
          !needle ||
          [e.method, e.host, e.deviceName, e.request, e.response, e.error].some((f) => f?.toLowerCase().includes(needle)),
      )
      .reverse();
  }, [entries, device, errorsOnly, hideRoutine, text]);

  const errorCount = entries.filter((e) => !e.ok).length;

  async function toggleEnabled(on: boolean) {
    try {
      const r = await client.setDebug({ enabled: on });
      setEnabled(r.enabled);
      setRecordAll(r.recordAll);
      if (!on) {
        setEntries([]);
        since.current = 0;
      }
    } catch (err) {
      setError(message(err));
    }
  }

  async function toggleRecordAll(on: boolean) {
    try {
      const r = await client.setDebug({ recordAll: on });
      setRecordAll(r.recordAll);
      setUntil(r.recordAllUntil ?? null);
    } catch (err) {
      setError(message(err));
    }
  }

  async function clear() {
    try {
      await client.clearDebugLog();
      setEntries([]);
      since.current = 0;
    } catch (err) {
      setError(message(err));
    }
  }

  async function copy() {
    try {
      await navigator.clipboard.writeText(asText([...shown].reverse()));
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      setError("Couldn't copy here. Use Download instead.");
    }
  }

  function download() {
    const blob = new Blob([asText([...shown].reverse())], { type: "text/plain" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `powerstation-log-${new Date().toISOString().replace(/[:.]/g, "-")}.txt`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  const toggle = (seq: number) =>
    setOpen((prev) => {
      const next = new Set(prev);
      if (next.has(seq)) next.delete(seq);
      else next.add(seq);
      return next;
    });

  return (
    <section aria-labelledby="ps-debug-title">
      <h3 className={c("section-title")} id="ps-debug-title" style={{ marginTop: 0 }}>
        Device traffic
      </h3>
      <p className={c("hint")} style={{ margin: "0 0 8px" }}>
        Every command and every error between PowerStation and your devices, newest first. Turn on "Record every poll" to
        also see the routine status reads (it switches itself off after 30 minutes). Passwords are never logged. The log is
        kept in memory only: at most 500 entries, 24 hours, and nothing is written to disk.
      </p>

      <label className={c("check")} style={{ marginBottom: 6 }}>
        <input type="checkbox" checked={enabled} onChange={(e) => toggleEnabled(e.currentTarget.checked)} />
        <span>Debug log on</span>
      </label>
      {!enabled ? (
        <p className={c("hint")}>The debug log is off. Nothing is being recorded. Turn it on when you need a log for a device problem.</p>
      ) : (
      <>
      <label className={c("check")} style={{ marginBottom: 8 }}>
        <input type="checkbox" checked={recordAll} onChange={(e) => toggleRecordAll(e.currentTarget.checked)} />
        <span>
          Record every poll
          {recordAll && until && (
            <span className={c("hint")}> until {new Date(until).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span>
          )}
        </span>
      </label>

      <div className={c("log-filters")}>
        <select className={c("select")} aria-label="Device" value={device} onChange={(e) => setDevice(e.currentTarget.value)}>
          <option value="*">All devices</option>
          {devices.map((d) => (
            <option key={d.deviceId} value={d.deviceId}>
              {d.displayName}
            </option>
          ))}
          <option value="">Adding, finding and other</option>
        </select>
        <input
          className={c("log-search")}
          type="search"
          placeholder="Filter: method, address, text in replies…"
          aria-label="Filter text"
          value={text}
          onChange={(e) => setText(e.currentTarget.value)}
        />
        <label className={c("check")}>
          <input type="checkbox" checked={errorsOnly} onChange={(e) => setErrorsOnly(e.currentTarget.checked)} />
          <span>Errors only{errorCount ? ` (${errorCount})` : ""}</span>
        </label>
        <label className={c("check")}>
          <input type="checkbox" checked={hideRoutine} onChange={(e) => setHideRoutine(e.currentTarget.checked)} />
          <span>Hide status polls</span>
        </label>
      </div>

      <div className={c("row")} style={{ margin: "8px 0" }}>
        <button type="button" className={c("button", "button--small")} aria-pressed={paused} onClick={() => setPaused(!paused)}>
          {paused ? "Resume" : "Pause"}
        </button>
        <button type="button" className={c("button", "button--small")} onClick={copy} disabled={shown.length === 0}>
          {copied ? "Copied" : "Copy"}
        </button>
        <button type="button" className={c("button", "button--small")} onClick={download} disabled={shown.length === 0}>
          Download
        </button>
        <button type="button" className={c("button", "button--small")} onClick={clear}>
          Clear
        </button>
        <span className={c("hint")}>
          {shown.length} of {entries.length} shown
        </span>
      </div>
      {error && <Notice tone="error">{error}</Notice>}

      {shown.length === 0 ? (
        <p className={c("hint")}>
          {entries.length === 0
            ? "Nothing recorded yet. Try Check now on a device, or turn on Record every poll."
            : "Nothing matches the filters."}
        </p>
      ) : (
        <ul className={c("traffic")}>
          {shown.map((e) => {
            const expanded = open.has(e.seq);
            return (
              <li key={e.seq} className={c("traffic-row", !e.ok && "traffic-row--bad")}>
                <button type="button" className={c("traffic-head")} aria-expanded={expanded} onClick={() => toggle(e.seq)}>
                  <span className={c("traffic-time")}>{time(e.at)}</span>
                  <span className={c("traffic-device")} title={e.host ?? ""}>
                    {e.deviceName ?? e.host ?? "PowerStation"}
                  </span>
                  <span className={c("traffic-method")}>{e.method}</span>
                  <span className={c("traffic-status", e.ok ? "traffic-status--ok" : "traffic-status--bad")}>
                    {e.ok ? (e.status ? e.status : "OK") : e.status ? `${e.status} ✕` : "✕"}
                  </span>
                  <span className={c("traffic-ms")}>{e.ms != null ? `${Math.round(e.ms)} ms` : ""}</span>
                </button>
                {!e.ok && e.error && <div className={c("traffic-error")}>{e.error}</div>}
                {expanded && (
                  <div className={c("traffic-body")}>
                    <div className={c("hint")}>
                      {e.kind === "rpc" ? "RPC" : e.kind === "http" ? "HTTP" : "Event"} · {e.host ?? "—"}
                      {e.deviceId ? ` · ${e.deviceId}` : ""}
                    </div>
                    {e.request && (
                      <>
                        <div className={c("traffic-label")}>Sent</div>
                        <pre>{pretty(e.request)}</pre>
                      </>
                    )}
                    <div className={c("traffic-label")}>Reply</div>
                    <pre>{e.response ? pretty(e.response) : "(none)"}</pre>
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}
      </>
      )}
    </section>
  );
}
