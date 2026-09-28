// SPDX-License-Identifier: GPL-2.0-or-later
// Automations: a list of "when … then …" rules driven by Zeus (start, close,
// TX, band, frequency) and by time (idle, time of day). Also the idle
// countdown shown on the main panel and in its own small panel.
import { useEffect, useId, useState, type ReactNode } from "react";
import {
  ApiError,
  BANDS,
  type Action,
  type AutomationState,
  type DeviceView,
  type EndAction,
  type PowerStationClient,
  type Rule,
  type RuleBody,
  type Scene,
  type Trigger,
  type TriggerType,
} from "./api";
import { Notice, PanelRoot, channelLabel, useStatus, type StatusState } from "./shared";
import { c } from "./styles";

const message = (err: unknown) => (err instanceof ApiError ? err.message : String(err));

const TRIGGERS: { type: TriggerType; label: string; hint: string }[] = [
  { type: "zeusStart", label: "Zeus starts", hint: "Runs once each time Zeus starts." },
  { type: "zeusStop", label: "Zeus closes", hint: "Runs when Zeus shuts down normally. Not seen if Zeus crashes or the computer loses power." },
  { type: "tx", label: "Transmitting (on-air light)", hint: "Turns a light on while you transmit." },
  { type: "band", label: "Band", hint: "Runs while the radio is tuned to one of the chosen bands." },
  { type: "frequency", label: "Frequency range", hint: "Runs while the radio is tuned inside the range." },
  { type: "idle", label: "Idle", hint: "Runs when nobody has used the station for a while." },
  { type: "time", label: "Time of day", hint: "At a set time, runs only if the station is idle. If you're still active, it checks again later." },
];

/** Triggers that last (have an end); the others are one-off events. */
const LASTING: TriggerType[] = ["tx", "band", "frequency", "idle"];
const HAS_DEBOUNCE: TriggerType[] = ["tx", "band", "frequency"];
const HAS_DELAY: TriggerType[] = ["zeusStart", "tx", "band", "frequency"];

const defaultTrigger = (type: TriggerType): Trigger => {
  switch (type) {
    case "band": return { type, bands: ["6m"] };
    case "frequency": return { type, fromMHz: 50, toMHz: 54 };
    case "idle": return { type, minutes: 60, warnMinutes: 5, extendMinutes: 30 };
    case "time": return { type, at: "23:00", idleMinutes: 15, extendMinutes: 30 };
    default: return { type } as Trigger;
  }
};

// ---------------------------------------------------------------- wording

function outputName(devices: DeviceView[], a: { deviceId: string; kind: string; index: number }) {
  const d = devices.find((x) => x.deviceId === a.deviceId);
  const ch = d?.status.channels.find((x) => x.kind === a.kind && x.index === a.index);
  if (!d) return "a removed device";
  return ch ? channelLabel(ch) : d.displayName;
}

function describeTrigger(t: Trigger) {
  switch (t.type) {
    case "zeusStart": return "Zeus starts";
    case "zeusStop": return "Zeus closes";
    case "tx": return "transmitting";
    case "band": return `band is ${t.bands.join(", ") || "(none chosen)"}`;
    case "frequency": return `tuned ${t.fromMHz}–${t.toMHz} MHz`;
    case "idle": return `idle ${t.minutes} min`;
    case "time": return `${t.at}, if idle ${t.idleMinutes} min (else +${t.extendMinutes} min)`;
  }
}

function describeAction(a: Action, devices: DeviceView[], scenes: Scene[]) {
  if (a.type === "scene") {
    const s = scenes.find((x) => x.id === a.sceneId);
    const name = s ? `“${s.name}”` : "a deleted scene";
    return a.mode === "off" ? `all off in ${name}` : `apply ${name}`;
  }
  const name = outputName(devices, a);
  if (!a.on) return `turn off ${name}`;
  const level = a.kind === "Light" && a.brightness ? ` at ${a.brightness}%` : "";
  const ramp = a.rampSeconds ? `, ${a.rampSeconds}s ramp` : "";
  return `turn on ${name}${level}${ramp}`;
}

function describeEnd(e: EndAction | null | undefined, devices: DeviceView[], scenes: Scene[]) {
  if (!e || e.type === "none") return null;
  if (e.type === "restore") return "put back how it was";
  if (e.type === "off") return "turn it off";
  return describeAction(e, devices, scenes);
}

// ---------------------------------------------------------------- settings

export function AutomationsSettings({ client, status }: { client: PowerStationClient; status: StatusState }) {
  const data = status.data;
  const rules = data?.rules ?? [];
  const auto = data?.automation;
  const devices = data?.devices ?? [];
  const scenes = data?.scenes ?? [];
  const [editing, setEditing] = useState<Rule | "new" | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  async function act(label: string, fn: () => Promise<unknown>) {
    setBusy(label);
    setError(null);
    try {
      await fn();
      status.reload();
    } catch (err) {
      setError(message(err));
    } finally {
      setBusy(null);
    }
  }

  const toggle = (r: Rule) => {
    const { id: _id, lastRun: _last, ...body } = r;
    return act(`toggle-${r.id}`, () => client.updateRule(r.id, { ...body, enabled: !r.enabled }));
  };

  if (!data) return null;
  return (
    <section aria-labelledby="ps-auto-title">
      <div className={c("toolbar")}>
        <h3 className={c("section-title")} id="ps-auto-title" style={{ margin: 0 }}>
          Automations
        </h3>
        {auto && (
          <label className={c("check")}>
            <input
              type="checkbox"
              checked={!auto.paused}
              disabled={busy !== null}
              onChange={() => act("pause", () => client.setAutomation({ paused: !auto.paused }))}
            />
            <span>{auto.paused ? "Paused" : "Running"}</span>
          </label>
        )}
      </div>
      {auto && <RadioLine auto={auto} />}
      <p className={c("hint")} style={{ margin: "0 0 8px" }}>
        Each rule says when to act and what to do. Anything a rule would switch while you're transmitting waits until TX ends.
      </p>
      {error && <Notice tone="error">{error}</Notice>}

      {rules.length === 0 && !editing && <p className={c("hint")}>No rules yet.</p>}
      <div className={c("rules")}>
        {rules.map((r) =>
          editing !== "new" && editing?.id === r.id ? (
            <RuleEditor
              key={r.id}
              rule={r}
              client={client}
              devices={devices}
              scenes={scenes}
              onCancel={() => setEditing(null)}
              onSaved={() => {
                setEditing(null);
                status.reload();
              }}
            />
          ) : (
            <div className={c("rule", !r.enabled && "rule--off")} key={r.id}>
              <label className={c("switch")} title={r.enabled ? "Rule on" : "Rule off"}>
                <input
                  type="checkbox"
                  role="switch"
                  checked={r.enabled}
                  aria-label={`${r.name}: ${r.enabled ? "on" : "off"}`}
                  disabled={busy !== null}
                  onChange={() => toggle(r)}
                />
                <span aria-hidden="true" />
              </label>
              <div className={c("rule-text")}>
                <div className={c("rule-name")}>
                  <TriggerChip type={r.trigger.type} />
                  <span title={r.name}>{r.name}</span>
                </div>
                <div className={c("rule-desc")}>
                  <strong>When</strong> {describeTrigger(r.trigger)}
                  {r.debounceSeconds ? ` (steady ${r.debounceSeconds}s)` : ""}
                  {" → "}
                  {r.delaySeconds ? `after ${r.delaySeconds}s, ` : ""}
                  {describeAction(r.action, devices, scenes)}
                  {describeEnd(r.endAction, devices, scenes) && (
                    <>
                      ; <strong>when it ends</strong>
                      {r.endDelaySeconds ? ` (after ${r.endDelaySeconds}s)` : ""} {describeEnd(r.endAction, devices, scenes)}
                    </>
                  )}
                </div>
                {r.trigger.type === "tx" && <div className={c("rule-warn")}>On-air light only. Don't rely on it for safety.</div>}
                {r.lastRun && (
                  <div className={c("rule-last", !r.lastRun.ok && "rule-last--bad")}>
                    Last run {new Date(r.lastRun.at).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}: {r.lastRun.text}
                  </div>
                )}
              </div>
              <div className={c("rule-actions")}>
                {confirmDelete === r.id ? (
                  <>
                    <button type="button" className={c("button", "button--small", "button--danger")} onClick={() => act(`del-${r.id}`, () => client.deleteRule(r.id).then(() => setConfirmDelete(null)))}>
                      Delete
                    </button>
                    <button type="button" className={c("button", "button--small")} onClick={() => setConfirmDelete(null)}>
                      Cancel
                    </button>
                  </>
                ) : (
                  <>
                    <button
                      type="button"
                      className={c("button", "button--small")}
                      title="Run the action now, as if the rule had fired"
                      disabled={busy !== null || r.trigger.type === "zeusStop"}
                      onClick={() => act(`test-${r.id}`, () => client.testRule(r.id))}
                    >
                      {busy === `test-${r.id}` ? "…" : "Test"}
                    </button>
                    <button type="button" className={c("button", "button--small")} onClick={() => setEditing(r)}>
                      Edit
                    </button>
                    <button type="button" className={c("button", "button--small")} aria-label={`Delete ${r.name}`} onClick={() => setConfirmDelete(r.id)}>
                      ✕
                    </button>
                  </>
                )}
              </div>
            </div>
          ),
        )}
      </div>

      {editing === "new" ? (
        <RuleEditor
          client={client}
          devices={devices}
          scenes={scenes}
          onCancel={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            status.reload();
          }}
        />
      ) : (
        <div className={c("row")} style={{ marginTop: 8 }}>
          <button type="button" className={c("button")} disabled={editing !== null || devices.length === 0} onClick={() => setEditing("new")}>
            New rule
          </button>
          {devices.length === 0 && <span className={c("hint")}>Add a device first.</span>}
        </div>
      )}

      {auto && auto.pending.length > 0 && (
        <>
          <h4 className={c("section-title")}>Waiting</h4>
          <ul className={c("log")}>
            {auto.pending.map((p, i) => (
              <li key={i}>
                <span className={c("log-time")}>{p.waitingForTx ? "TX" : p.at ? clock(p.at) : "…"}</span>
                {p.text}
              </li>
            ))}
          </ul>
        </>
      )}
      {auto && auto.log.length > 0 && (
        <>
          <h4 className={c("section-title")}>Recent</h4>
          <ul className={c("log")}>
            {auto.log.slice(0, 8).map((l, i) => (
              <li key={i} className={c(!l.ok && "log--bad")}>
                <span className={c("log-time")}>{clock(l.at)}</span>
                {l.text}
              </li>
            ))}
          </ul>
        </>
      )}
      <p className={c("hint")} style={{ marginTop: 10 }}>
        Idle means no tuning, band or mode change, TX, or use of PowerStation. Zeus doesn't share mouse or keyboard activity,
        so just listening counts as idle: use “I'm here” on the countdown to reset it.
      </p>
    </section>
  );
}

const clock = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });

function RadioLine({ auto }: { auto: AutomationState }) {
  const r = auto.radio;
  if (!r.connected) return <p className={c("radio-line")}>Radio: not reporting. Band, frequency and TX rules wait for it.</p>;
  return (
    <p className={c("radio-line")}>
      Radio now: {r.frequencyHz ? `${(r.frequencyHz / 1e6).toFixed(3)} MHz` : "—"}
      {r.band ? ` · ${r.band}` : ""}
      {r.mode ? ` · ${r.mode}` : ""} ·{" "}
      <span className={c(r.mox ? "on-air" : "rx")}>{r.mox ? "TX" : "RX"}</span>
    </p>
  );
}

function TriggerChip({ type }: { type: TriggerType }) {
  const text: Record<TriggerType, string> = {
    zeusStart: "Start", zeusStop: "Close", tx: "TX", band: "Band", frequency: "Freq", idle: "Idle", time: "Time",
  };
  return <span className={c("trig", `trig--${type}`)}>{text[type]}</span>;
}

// ---------------------------------------------------------------- editor

type Draft = RuleBody;

const blankRule = (): Draft => ({
  name: "",
  enabled: true,
  trigger: defaultTrigger("band"),
  action: { type: "scene", sceneId: "", mode: "apply" },
  endAction: { type: "restore" },
  debounceSeconds: 2,
  delaySeconds: 0,
  endDelaySeconds: 0,
});

function RuleEditor({
  rule,
  client,
  devices,
  scenes,
  onCancel,
  onSaved,
}: {
  rule?: Rule;
  client: PowerStationClient;
  devices: DeviceView[];
  scenes: Scene[];
  onCancel: () => void;
  onSaved: () => void;
}) {
  const ids = useId();
  const [draft, setDraft] = useState<Draft>(() => {
    if (!rule) return blankRule();
    const { id: _id, lastRun: _l, ...body } = rule;
    return body;
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const t = draft.trigger;
  const lasting = LASTING.includes(t.type);
  const isTx = t.type === "tx";

  const outputs = devices.flatMap((d) =>
    d.status.channels.filter((ch) => ch.kind !== "Meter").map((ch) => ({ d, ch, key: `${d.deviceId}|${ch.kind}|${ch.index}` })),
  );

  function setTrigger(type: TriggerType) {
    setDraft((d) => {
      const next: Draft = { ...d, trigger: defaultTrigger(type) };
      if (type === "tx") {
        // On-air light: one output, off again shortly after TX ends.
        const first = outputs.find((o) => o.ch.kind === "Light") ?? outputs[0];
        if (first && (d.action.type !== "output" || d.action.kind !== "Light"))
          next.action = { type: "output", deviceId: first.d.deviceId, kind: first.ch.kind, index: first.ch.index, on: true, brightness: first.ch.kind === "Light" ? 100 : null, rampSeconds: null };
        next.endAction = { type: "off" };
        next.endDelaySeconds = 3;
        next.debounceSeconds = 0;
        next.delaySeconds = 0;
      } else if (type === "idle") next.endAction = { type: "restore" };
      else if (!LASTING.includes(type)) next.endAction = null;
      else if (!d.endAction) next.endAction = { type: "restore" };
      if (!HAS_DEBOUNCE.includes(type)) next.debounceSeconds = null;
      if (!HAS_DELAY.includes(type)) next.delaySeconds = null;
      return next;
    });
  }

  const setT = (patch: Partial<Trigger>) => setDraft((d) => ({ ...d, trigger: { ...d.trigger, ...patch } as Trigger }));

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const body = { ...draft, name: draft.name.trim() || autoName(draft, devices, scenes) };
      if (rule) await client.updateRule(rule.id, body);
      else await client.createRule(body);
      onSaved();
    } catch (err) {
      setError(message(err));
    } finally {
      setBusy(false);
    }
  }

  const a = draft.action;
  const actionValid = a.type === "scene" ? !!a.sceneId : !!a.deviceId;

  return (
    <div className={c("form", "form--rule")} role="group" aria-labelledby={`${ids}-t`}>
      <h4 className={c("section-title")} id={`${ids}-t`} style={{ margin: 0 }}>
        {rule ? `Edit ${rule.name}` : "New rule"}
      </h4>

      {/* WHEN */}
      <div className={c("step-block")}>
        <div className={c("step-head")}>When</div>
        <div className={c("fields")}>
          <div className={c("field")}>
            <label htmlFor={`${ids}-trig`}>Trigger</label>
            <select id={`${ids}-trig`} className={c("select")} value={t.type} onChange={(e) => setTrigger(e.currentTarget.value as TriggerType)}>
              {TRIGGERS.map((x) => (
                <option key={x.type} value={x.type}>
                  {x.label}
                </option>
              ))}
            </select>
            <span className={c("hint")}>{TRIGGERS.find((x) => x.type === t.type)?.hint}</span>
          </div>
        </div>

        {t.type === "band" && (
          <div className={c("band-pick")} role="group" aria-label="Bands">
            {BANDS.map((b) => {
              const on = t.bands.includes(b);
              return (
                <button
                  key={b}
                  type="button"
                  className={c("band", on && "band--on")}
                  aria-pressed={on}
                  onClick={() => setT({ bands: on ? t.bands.filter((x) => x !== b) : [...t.bands, b] } as Partial<Trigger>)}
                >
                  {b}
                </button>
              );
            })}
          </div>
        )}
        {t.type === "frequency" && (
          <div className={c("fields")}>
            <NumField label="From (MHz)" value={t.fromMHz} step={0.001} onChange={(v) => setT({ fromMHz: v ?? 0 } as Partial<Trigger>)} />
            <NumField label="To (MHz)" value={t.toMHz} step={0.001} onChange={(v) => setT({ toMHz: v ?? 0 } as Partial<Trigger>)} />
          </div>
        )}
        {t.type === "idle" && (
          <div className={c("fields")}>
            <NumField label="Idle after (minutes)" value={t.minutes} min={5} max={1440} onChange={(v) => setT({ minutes: v ?? 60 } as Partial<Trigger>)} />
            <NumField label="Warn before (minutes)" value={t.warnMinutes} min={0} max={60} onChange={(v) => setT({ warnMinutes: v ?? 0 } as Partial<Trigger>)} hint="Shows the countdown; 0 = no warning." />
            <NumField label="Extend button adds (minutes)" value={t.extendMinutes} min={5} max={480} onChange={(v) => setT({ extendMinutes: v ?? 30 } as Partial<Trigger>)} />
          </div>
        )}
        {t.type === "time" && (
          <div className={c("fields")}>
            <div className={c("field")}>
              <label htmlFor={`${ids}-at`}>At</label>
              <input id={`${ids}-at`} type="time" value={t.at} onChange={(e) => setT({ at: e.currentTarget.value } as Partial<Trigger>)} />
            </div>
            <NumField label="Only if idle for (minutes)" value={t.idleMinutes} min={0} max={240} onChange={(v) => setT({ idleMinutes: v ?? 0 } as Partial<Trigger>)} hint="0 = always run at this time." />
            <NumField label="If active, check again in (minutes)" value={t.extendMinutes} min={5} max={240} onChange={(v) => setT({ extendMinutes: v ?? 30 } as Partial<Trigger>)} />
          </div>
        )}
        {(HAS_DEBOUNCE.includes(t.type) || HAS_DELAY.includes(t.type)) && (
          <div className={c("fields")}>
            {HAS_DEBOUNCE.includes(t.type) && (
              <NumField
                label="Debounce (seconds)"
                value={draft.debounceSeconds ?? null}
                min={0}
                max={600}
                step={0.5}
                onChange={(v) => setDraft((d) => ({ ...d, debounceSeconds: v }))}
                hint={isTx ? "Ignore TX shorter than this (CW blips, tune)." : "Must stay this way this long, so passing through while tuning doesn't count."}
              />
            )}
            {HAS_DELAY.includes(t.type) && (
              <NumField
                label="Delay (seconds)"
                value={draft.delaySeconds ?? null}
                min={0}
                max={3600}
                step={0.5}
                onChange={(v) => setDraft((d) => ({ ...d, delaySeconds: v }))}
                hint="Wait this long before acting."
              />
            )}
          </div>
        )}
        {isTx && (
          <Notice tone="warn">
            On-air light only. The light follows TX over the network and can lag or miss a change. Never use it to switch
            amplifiers, antennas or anything that protects equipment or people.
          </Notice>
        )}
        {t.type === "zeusStop" && (
          <p className={c("hint")}>Runs right away as Zeus closes (no delay). Use safety timers for crashes or power cuts.</p>
        )}
      </div>

      {/* THEN */}
      <div className={c("step-block")}>
        <div className={c("step-head")}>Then</div>
        <ActionPicker
          ids={`${ids}-a`}
          action={a}
          outputs={outputs}
          scenes={scenes}
          scenesAllowed={!isTx}
          onChange={(action) => setDraft((d) => ({ ...d, action }))}
        />
      </div>

      {/* WHEN IT ENDS */}
      {lasting && (
        <div className={c("step-block")}>
          <div className={c("step-head")}>{t.type === "idle" ? "When you're back" : "When it ends"}</div>
          <div className={c("fields")}>
            <div className={c("field")}>
              <label htmlFor={`${ids}-end`}>Then</label>
              <select
                id={`${ids}-end`}
                className={c("select")}
                value={draft.endAction?.type === "scene" || draft.endAction?.type === "output" ? "other" : draft.endAction?.type ?? "none"}
                onChange={(e) => {
                  const v = e.currentTarget.value;
                  setDraft((d) => ({
                    ...d,
                    endAction: v === "other" ? { type: "scene", sceneId: scenes[0]?.id ?? "", mode: "apply" } : ({ type: v } as EndAction),
                  }));
                }}
              >
                {!isTx && <option value="restore">Put back how it was</option>}
                <option value="off">{a.type === "scene" ? "All off in the scene" : "Turn it off"}</option>
                {!isTx && <option value="other">Do something else…</option>}
                <option value="none">Leave it</option>
              </select>
              {draft.endAction?.type === "restore" && (
                <span className={c("hint")}>
                  Only outputs nobody changed in the meantime are put back, and not until TX ends.
                </span>
              )}
            </div>
            {t.type !== "idle" && (
              <NumField
                label={isTx ? "Hold (seconds)" : "Wait (seconds)"}
                value={draft.endDelaySeconds ?? null}
                min={0}
                max={600}
                step={0.5}
                onChange={(v) => setDraft((d) => ({ ...d, endDelaySeconds: v }))}
                hint={isTx ? "Keeps the light on between overs." : "Wait this long after it ends."}
              />
            )}
          </div>
          {(draft.endAction?.type === "scene" || draft.endAction?.type === "output") && (
            <ActionPicker
              ids={`${ids}-e`}
              action={draft.endAction}
              outputs={outputs}
              scenes={scenes}
              scenesAllowed
              onChange={(endAction) => setDraft((d) => ({ ...d, endAction }))}
            />
          )}
        </div>
      )}

      <div className={c("fields")}>
        <div className={c("field")}>
          <label htmlFor={`${ids}-name`}>Rule name (optional)</label>
          <input
            id={`${ids}-name`}
            value={draft.name}
            maxLength={60}
            placeholder={autoName(draft, devices, scenes)}
            onChange={(e) => {
              const name = e.currentTarget.value;
              setDraft((d) => ({ ...d, name }));
            }}
          />
        </div>
      </div>

      {error && <Notice tone="error">{error}</Notice>}
      <div className={c("row")}>
        <button type="button" className={c("button", "button--primary")} disabled={busy || !actionValid} onClick={save}>
          {busy ? "Saving…" : rule ? "Save rule" : "Create rule"}
        </button>
        <button type="button" className={c("button")} onClick={onCancel} disabled={busy}>
          Cancel
        </button>
      </div>
    </div>
  );
}

function autoName(d: Draft, devices: DeviceView[], scenes: Scene[]) {
  const what = describeAction(d.action, devices, scenes);
  const when = describeTrigger(d.trigger);
  const s = `${what[0].toUpperCase()}${what.slice(1)} when ${when}`;
  return s.length > 60 ? `${s.slice(0, 57)}…` : s;
}

function ActionPicker({
  ids,
  action,
  outputs,
  scenes,
  scenesAllowed,
  onChange,
}: {
  ids: string;
  action: Action;
  outputs: { d: DeviceView; ch: DeviceView["status"]["channels"][number]; key: string }[];
  scenes: Scene[];
  scenesAllowed: boolean;
  onChange: (a: Action) => void;
}) {
  const kind = action.type === "scene" ? (action.mode === "off" ? "scene-off" : "scene") : action.on ? "on" : "off";
  const outKey = action.type === "output" ? `${action.deviceId}|${action.kind}|${action.index}` : "";
  const out = outputs.find((o) => o.key === outKey);
  const firstOut = outputs[0];

  return (
    <div className={c("fields")}>
      <div className={c("field")}>
        <label htmlFor={`${ids}-kind`}>Do</label>
        <select
          id={`${ids}-kind`}
          className={c("select")}
          value={kind}
          onChange={(e) => {
            const v = e.currentTarget.value;
            if (v === "scene" || v === "scene-off")
              onChange({ type: "scene", sceneId: action.type === "scene" ? action.sceneId : scenes[0]?.id ?? "", mode: v === "scene" ? "apply" : "off" });
            else {
              const base = action.type === "output" ? action : firstOut ? { type: "output" as const, deviceId: firstOut.d.deviceId, kind: firstOut.ch.kind, index: firstOut.ch.index } : null;
              if (base) onChange({ ...base, type: "output", on: v === "on", brightness: v === "on" && base.kind === "Light" ? (action.type === "output" ? action.brightness : null) ?? 100 : null, rampSeconds: action.type === "output" ? action.rampSeconds ?? null : null });
            }
          }}
        >
          {scenesAllowed && <option value="scene">Apply a scene</option>}
          {scenesAllowed && <option value="scene-off">All off in a scene</option>}
          <option value="on">Turn an output on</option>
          <option value="off">Turn an output off</option>
        </select>
      </div>
      {action.type === "scene" ? (
        <div className={c("field")}>
          <label htmlFor={`${ids}-scene`}>Scene</label>
          <select id={`${ids}-scene`} className={c("select")} value={action.sceneId} onChange={(e) => onChange({ ...action, sceneId: e.currentTarget.value })}>
            {scenes.length === 0 && <option value="">No scenes yet</option>}
            {scenes.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
              </option>
            ))}
          </select>
        </div>
      ) : (
        <>
          <div className={c("field")}>
            <label htmlFor={`${ids}-out`}>Output</label>
            <select
              id={`${ids}-out`}
              className={c("select")}
              value={outKey}
              onChange={(e) => {
                const o = outputs.find((x) => x.key === e.currentTarget.value);
                if (o) onChange({ ...action, deviceId: o.d.deviceId, kind: o.ch.kind, index: o.ch.index, brightness: o.ch.kind === "Light" && action.on ? action.brightness ?? 100 : null });
              }}
            >
              {outputs.map((o) => (
                <option key={o.key} value={o.key}>
                  {o.d.displayName} · {channelLabel(o.ch)}
                </option>
              ))}
            </select>
          </div>
          {out?.ch.kind === "Light" && action.on && (
            <>
              <NumField label="Level (%)" value={action.brightness ?? 100} min={1} max={100} onChange={(v) => onChange({ ...action, brightness: v })} />
              <NumField label="Ramp (seconds)" value={action.rampSeconds ?? null} min={0} max={600} step={0.5} onChange={(v) => onChange({ ...action, rampSeconds: v })} hint="Fade up to the level." />
            </>
          )}
          {out?.ch.kind === "Light" && !action.on && (
            <NumField label="Ramp down (seconds)" value={action.rampSeconds ?? null} min={0} max={600} step={0.5} onChange={(v) => onChange({ ...action, rampSeconds: v })} hint="Fade out before turning off." />
          )}
        </>
      )}
    </div>
  );
}

export function NumField({
  label,
  value,
  onChange,
  min,
  max,
  step,
  hint,
}: {
  label: string;
  value: number | null;
  onChange: (v: number | null) => void;
  min?: number;
  max?: number;
  step?: number;
  hint?: ReactNode;
}) {
  const id = useId();
  return (
    <div className={c("field")}>
      <label htmlFor={id}>{label}</label>
      <input
        id={id}
        type="number"
        min={min}
        max={max}
        step={step}
        value={value ?? ""}
        onChange={(e) => {
          const v = e.currentTarget.value;
          onChange(v.trim() === "" ? null : Number(v));
        }}
      />
      {hint && <span className={c("hint")}>{hint}</span>}
    </div>
  );
}

// ---------------------------------------------------------------- idle countdown

function useNow(ms = 1000) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = window.setInterval(() => setNow(Date.now()), ms);
    return () => window.clearInterval(t);
  }, [ms]);
  return now;
}

const mmss = (ms: number) => {
  const s = Math.max(0, Math.round(ms / 1000));
  const m = Math.floor(s / 60);
  return m >= 60 ? `${Math.floor(m / 60)} h ${m % 60} min` : m >= 10 ? `${m} min` : `${m}:${String(s % 60).padStart(2, "0")}`;
};

function IdleButtons({ client, status, auto, small }: { client: PowerStationClient; status: StatusState; auto: AutomationState; small?: boolean }) {
  const [busy, setBusy] = useState(false);
  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true);
    try {
      await fn();
      status.reload();
    } finally {
      setBusy(false);
    }
  };
  return (
    <span className={c("row")}>
      <button type="button" className={c("button", "button--primary", small && "button--small")} disabled={busy} onClick={() => run(client.imHere)}>
        I'm here
      </button>
      {auto.idle.extendMinutes ? (
        <button type="button" className={c("button", small && "button--small")} disabled={busy} onClick={() => run(client.extendIdle)}>
          +{auto.idle.extendMinutes} min
        </button>
      ) : null}
    </span>
  );
}

/** On the main panel: the countdown while the idle warning is up, and what's waiting for TX to end. */
export function IdleBanner({ client, status }: { client: PowerStationClient; status: StatusState }) {
  const auto = status.data?.automation;
  const now = useNow();
  if (!auto || auto.paused) return null;
  const { idle } = auto;
  const tx = auto.pending.filter((p) => p.waitingForTx);
  return (
    <>
      {idle.state === "warning" && idle.firesAt && (
        <div className={c("idle-banner")} role="status">
          <span className={c("idle-dot")} aria-hidden="true" />
          <span className={c("idle-text")}>
            No activity. Idle rule runs in <strong>{mmss(new Date(idle.firesAt).getTime() - now)}</strong>
          </span>
          <IdleButtons client={client} status={status} auto={auto} small />
        </div>
      )}
      {idle.state === "idle" && (
        <div className={c("idle-banner", "idle-banner--idle")} role="status">
          <span className={c("idle-text")}>Station is idle. Things go back to how they were when you return.</span>
          <IdleButtons client={client} status={status} auto={{ ...auto, idle: { ...idle, extendMinutes: null } }} small />
        </div>
      )}
      {tx.length > 0 && (
        <Notice tone="warn">
          Waiting for TX to end: {tx.map((p) => p.text).join("; ")}
        </Notice>
      )}
    </>
  );
}

/** The small "pill" panel: dock it where it's always visible. */
export function IdlePillPanel({ client }: { client: PowerStationClient }) {
  const status = useStatus(client);
  const auto = status.data?.automation;
  const now = useNow();
  let body: ReactNode;
  if (!auto) body = <span className={c("hint")}>PowerStation…</span>;
  else if (auto.paused) body = <span className={c("pill-text")}>Automations paused</span>;
  else if (auto.idle.state === "off") body = <span className={c("pill-text")}>No idle rule</span>;
  else if (auto.idle.state === "warning" && auto.idle.firesAt)
    body = (
      <>
        <span className={c("pill-text", "pill-text--warn")}>
          <span className={c("idle-dot")} aria-hidden="true" /> Idle in <strong>{mmss(new Date(auto.idle.firesAt).getTime() - now)}</strong>
        </span>
        <IdleButtons client={client} status={status} auto={auto} small />
      </>
    );
  else if (auto.idle.state === "idle")
    body = (
      <>
        <span className={c("pill-text")}>Station idle</span>
        <IdleButtons client={client} status={status} auto={{ ...auto, idle: { ...auto.idle, extendMinutes: null } }} small />
      </>
    );
  else
    body = (
      <span className={c("pill-text")}>
        <span className={c("led", "led--on")} aria-hidden="true" /> Active
        {auto.idle.firesAt ? ` · idle in ${mmss(new Date(auto.idle.firesAt).getTime() - now)}` : ""}
      </span>
    );
  return (
    <PanelRoot label="PowerStation idle timer">
      <div className={c("pill", auto?.idle.state === "warning" && !auto.paused && "pill--warn")} role="status" aria-live="polite">
        {body}
      </div>
    </PanelRoot>
  );
}
