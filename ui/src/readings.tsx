// SPDX-License-Identifier: GPL-2.0-or-later
// Normal ranges for mains voltage and per-output current, and the log of
// readings that went outside them.
import { useEffect, useState } from "react";
import { ApiError, type MainsProfile, type PowerStationClient, type ReadingEvent } from "./api";
import { NumField } from "./automations";
import { Notice, channelLabel, fmt, type StatusState } from "./shared";
import { c } from "./styles";

const message = (err: unknown) => (err instanceof ApiError ? err.message : String(err));

/**
 * 120 V: ANSI C84.1 Range A (114–126 V) is normal service; Range B
 * (110–127 V) is the outer limit. 230 V: the EN 50160 ±10% band, with ±6%
 * as the normal range.
 */
export const MAINS_PRESETS: Record<"120" | "230", MainsProfile> = {
  "120": { preset: "120", normalLowV: 114, normalHighV: 126, limitLowV: 110, limitHighV: 127 },
  "230": { preset: "230", normalLowV: 216, normalHighV: 244, limitLowV: 207, limitHighV: 253 },
};

export function ReadingsSettings({ client, status }: { client: PowerStationClient; status: StatusState }) {
  const data = status.data;
  const view = data?.readings;
  const [mains, setMains] = useState<MainsProfile | null>(view?.mains ?? null);
  const [hold, setHold] = useState<number | null>(view?.holdSeconds ?? 5);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [limits, setLimits] = useState<Record<string, { warnA?: number | null; maxA?: number | null; minOnA?: number | null }>>({});

  useEffect(() => {
    if (view && !mains) {
      setMains(view.mains);
      setHold(view.holdSeconds);
    }
  }, [view, mains]);

  if (!data || !view || !mains) return null;

  async function act(label: string, fn: () => Promise<unknown>, done?: string) {
    setBusy(label);
    setError(null);
    setSaved(null);
    try {
      await fn();
      status.reload();
      if (done) setSaved(done);
    } catch (err) {
      setError(message(err));
    } finally {
      setBusy(null);
    }
  }

  const dirtyMains =
    JSON.stringify(mains) !== JSON.stringify(view.mains) || (hold ?? 0) !== view.holdSeconds;
  const metered = data.devices.flatMap((d) =>
    d.status.channels.filter((ch) => ch.metered).map((ch) => ({ d, ch, key: `${d.deviceId}|${ch.key}` })),
  );
  const dirtyLimits = Object.keys(limits).length > 0;
  const setField = (m: Partial<MainsProfile>) => setMains((prev) => ({ ...prev!, preset: "custom", ...m }));

  return (
    <section aria-labelledby="ps-readings-title">
      <h3 className={c("section-title")} id="ps-readings-title" style={{ marginTop: 0 }}>
        Normal ranges
      </h3>
      <p className={c("hint")} style={{ margin: "0 0 8px" }}>
        Readings outside the normal range show in amber; outside the limit, in red. Mains voltage is one supply, so it's
        reported once for the station; current is checked per output. Every excursion is logged below. These are warnings only: the Shelly's own overpower and overvoltage protection still does the switching.
      </p>
      {error && <Notice tone="error">{error}</Notice>}
      {saved && <Notice tone="ok">{saved}</Notice>}

      <h4 className={c("section-title")}>Mains voltage</h4>
      <div className={c("row")} style={{ marginBottom: 8 }}>
        <div className={c("seg")} role="radiogroup" aria-label="Mains supply">
          {(["120", "230", "custom"] as const).map((p) => (
            <button
              key={p}
              type="button"
              role="radio"
              aria-checked={mains.preset === p}
              className={c("seg-btn")}
              onClick={() => setMains(p === "custom" ? { ...mains, preset: "custom" } : MAINS_PRESETS[p])}
            >
              {p === "custom" ? "Custom" : `${p} V`}
            </button>
          ))}
        </div>
        <span className={c("hint")}>
          {mains.preset === "120"
            ? "US/Canada service (ANSI C84.1): normal 114–126 V, limit 110–127 V."
            : mains.preset === "230"
              ? "230 V service (EN 50160): normal ±6%, limit ±10%."
              : "Your own values."}
        </span>
      </div>
      <div className={c("range")} aria-hidden="true">
        <RangeBar mains={mains} />
      </div>
      <div className={c("fields")}>
        <NumField label="Limit low (V)" value={mains.limitLowV} step={0.5} onChange={(v) => setField({ limitLowV: v ?? 0 })} />
        <NumField label="Normal from (V)" value={mains.normalLowV} step={0.5} onChange={(v) => setField({ normalLowV: v ?? 0 })} />
        <NumField label="Normal to (V)" value={mains.normalHighV} step={0.5} onChange={(v) => setField({ normalHighV: v ?? 0 })} />
        <NumField label="Limit high (V)" value={mains.limitHighV} step={0.5} onChange={(v) => setField({ limitHighV: v ?? 0 })} />
      </div>
      <div className={c("fields")} style={{ marginTop: 8, maxWidth: 360 }}>
        <NumField
          label="Must last (seconds)"
          value={hold}
          min={0}
          max={120}
          onChange={setHold}
          hint="A reading has to stay out of range this long to count, so switch-on inrush and brief dips are ignored."
        />
      </div>
      <div className={c("row")} style={{ marginTop: 8 }}>
        <button
          type="button"
          className={c("button", "button--primary")}
          disabled={!dirtyMains || busy !== null}
          onClick={() => act("mains", () => client.saveReadings({ mains, holdSeconds: hold ?? 0 }), "Voltage range saved.")}
        >
          {busy === "mains" ? "Saving…" : "Save voltage range"}
        </button>
      </div>

      <h4 className={c("section-title")}>Current per output</h4>
      <p className={c("hint")} style={{ margin: "0 0 6px" }}>
        Defaults come from each device's rating: warn at 80% (the continuous-load rule of thumb), limit at 100%. Set your
        own lower values to match a breaker, cord or the load itself. "Low when on" catches a load that should be drawing
        current but isn't, such as a tripped supply or blown fuse.
      </p>
      <div className={c("limits")} role="table" aria-label="Current limits">
        <div className={c("limits-row", "limits-head")} role="row">
          <span role="columnheader">Output</span>
          <span role="columnheader">Rated</span>
          <span role="columnheader">Warn above (A)</span>
          <span role="columnheader">Limit (A)</span>
          <span role="columnheader">Low when on (A)</span>
        </div>
        {metered.map(({ d, ch, key }) => {
          const l = ch.limits;
          const draft = limits[key] ?? {};
          const val = (f: "warnA" | "maxA" | "minOnA") => (f in draft ? draft[f] ?? "" : l?.[f] ?? "");
          const set = (f: "warnA" | "maxA" | "minOnA", v: string) =>
            setLimits((prev) => ({ ...prev, [key]: { ...prev[key], [f]: v.trim() === "" ? null : Number(v) } }));
          return (
            <div className={c("limits-row")} role="row" key={key}>
              <span role="cell" className={c("limits-name")} title={`${d.displayName} · ${channelLabel(ch)}`}>
                {channelLabel(ch)}
                <small>{d.displayName}</small>
              </span>
              <span role="cell" className={c("limits-rated")}>
                {l?.ratedA ? fmt.amps(l.ratedA).replace(".00", "") : "—"}
                {l?.custom && (
                  <button
                    type="button"
                    className={c("link-button")}
                    title="Go back to the device rating"
                    onClick={() => setLimits((prev) => ({ ...prev, [key]: { warnA: null, maxA: null, minOnA: null } }))}
                  >
                    custom · reset
                  </button>
                )}
              </span>
              {(["warnA", "maxA", "minOnA"] as const).map((f) => (
                <span role="cell" key={f}>
                  <input
                    className={c("inline-num")}
                    type="number"
                    min={0}
                    step={0.1}
                    aria-label={`${channelLabel(ch)} ${f === "warnA" ? "warn above" : f === "maxA" ? "limit" : "low when on"} (amps)`}
                    placeholder={f === "minOnA" ? "Off" : "—"}
                    value={val(f)}
                    onChange={(e) => set(f, e.currentTarget.value)}
                  />
                </span>
              ))}
            </div>
          );
        })}
      </div>
      <div className={c("row")} style={{ marginTop: 8 }}>
        <button
          type="button"
          className={c("button")}
          disabled={!dirtyLimits || busy !== null}
          onClick={() =>
            act(
              "limits",
              async () => {
                // Send each changed row whole; a row left empty goes back to the device rating.
                const byDevice = new Map<string, Record<string, (typeof limits)[string] | null>>();
                for (const [k, draft] of Object.entries(limits)) {
                  const [id, ch] = k.split("|");
                  const current = metered.find((m) => m.key === k)?.ch.limits;
                  const pick = (f: "warnA" | "maxA" | "minOnA") => (f in draft ? draft[f] ?? null : current?.custom ? current[f] ?? null : null);
                  const row = { warnA: pick("warnA"), maxA: pick("maxA"), minOnA: pick("minOnA") };
                  const empty = row.warnA === null && row.maxA === null && row.minOnA === null;
                  byDevice.set(id, { ...byDevice.get(id), [ch]: empty ? null : row });
                }
                for (const [id, map] of byDevice) status.applyDevice(await client.updateDevice(id, { limits: map }));
                setLimits({});
              },
              "Current limits saved.",
            )
          }
        >
          {busy === "limits" ? "Saving…" : "Save current limits"}
        </button>
        {dirtyLimits && (
          <button type="button" className={c("button")} onClick={() => setLimits({})}>
            Undo changes
          </button>
        )}
      </div>

      <div className={c("toolbar")} style={{ marginTop: 14 }}>
        <h4 className={c("section-title")} style={{ margin: 0 }}>
          Events
        </h4>
        {view.events.length > 0 && (
          <button type="button" className={c("button", "button--small")} disabled={busy !== null} onClick={() => act("clear", client.clearReadingEvents)}>
            Clear
          </button>
        )}
      </div>
      {view.events.length === 0 ? (
        <p className={c("hint")}>No readings out of range yet.</p>
      ) : (
        <ul className={c("events")}>
          {view.events.map((ev) => (
            <EventRow key={ev.id} ev={ev} />
          ))}
        </ul>
      )}
    </section>
  );
}

function EventRow({ ev }: { ev: ReadingEvent }) {
  const start = new Date(ev.start);
  const ongoing = !ev.end;
  const mins = ((ev.end ? new Date(ev.end).getTime() : Date.now()) - start.getTime()) / 60000;
  const dur = mins < 1 ? `${Math.max(1, Math.round(mins * 60))} s` : mins < 90 ? `${Math.round(mins)} min` : `${(mins / 60).toFixed(1)} h`;
  return (
    <li className={c("event", ev.level === "limit" ? "event--limit" : "event--warn")}>
      <span className={c("event-when")}>
        {start.toLocaleDateString([], { month: "short", day: "numeric" })}{" "}
        {start.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}
      </span>
      <span className={c("event-text")}>
        <strong>{ev.label}</strong> {ev.text}
      </span>
      <span className={c("event-dur")}>{ev.kind === "device" ? "" : ongoing ? <span className={c("badge", ev.level === "limit" && "badge--danger")}>Now · {dur}</span> : dur}</span>
    </li>
  );
}

/** A small scale showing limit / normal bands for the chosen profile. */
function RangeBar({ mains }: { mains: MainsProfile }) {
  const lo = mains.limitLowV - (mains.normalHighV - mains.normalLowV) * 0.25;
  const hi = mains.limitHighV + (mains.normalHighV - mains.normalLowV) * 0.25;
  const pct = (v: number) => `${Math.max(0, Math.min(100, ((v - lo) / (hi - lo)) * 100))}%`;
  const w = (a: number, b: number) => `${Math.max(0, ((b - a) / (hi - lo)) * 100)}%`;
  return (
    <div className={c("range-bar")}>
      <span className={c("range-seg", "range-seg--limit")} style={{ left: 0, width: pct(mains.limitLowV) }} />
      <span className={c("range-seg", "range-seg--warn")} style={{ left: pct(mains.limitLowV), width: w(mains.limitLowV, mains.normalLowV) }} />
      <span className={c("range-seg", "range-seg--ok")} style={{ left: pct(mains.normalLowV), width: w(mains.normalLowV, mains.normalHighV) }} />
      <span className={c("range-seg", "range-seg--warn")} style={{ left: pct(mains.normalHighV), width: w(mains.normalHighV, mains.limitHighV) }} />
      <span className={c("range-seg", "range-seg--limit")} style={{ left: pct(mains.limitHighV), right: 0 }} />
      {[mains.limitLowV, mains.normalLowV, mains.normalHighV, mains.limitHighV].map((v, i) => (
        <span key={i} className={c("range-tick")} style={{ left: pct(v) }}>
          {v}
        </span>
      ))}
    </div>
  );
}
