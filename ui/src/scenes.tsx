// SPDX-License-Identifier: GPL-2.0-or-later
// Scenes: a named set of outputs and dimmer levels applied together.
// ScenesStrip runs them (Status panel); ScenesSettings creates and edits
// them (Setup panel).
import { useId, useState } from "react";
import {
  ApiError,
  type ChannelKind,
  type DeviceView,
  type PowerStationClient,
  type Rule,
  type Scene,
  type SceneTarget,
} from "./api";
import { Notice, channelLabel, type StatusState } from "./shared";
import { ColorPicker, isColor, isDimmable, type Rgb } from "./color";
import { c } from "./styles";

const message = (err: unknown) => (err instanceof ApiError ? err.message : String(err));
const targetKey = (t: { deviceId: string; kind: ChannelKind; index: number }) => `${t.deviceId}/${t.kind}/${t.index}`;

function describe(scene: Scene) {
  const on = scene.targets.filter((t) => t.on).length;
  const off = scene.targets.length - on;
  const parts = [`${scene.targets.length} output${scene.targets.length === 1 ? "" : "s"}`];
  if (on && off) parts.push(`${on} on, ${off} off`);
  if (scene.fadeSeconds) parts.push(`${scene.fadeSeconds}s fade`);
  if (scene.safetyMinutes) parts.push(`${scene.safetyMinutes} min safety timer`);
  return parts.join(" · ");
}

// ---------------------------------------------------------------- run

export function ScenesStrip({ client, status }: { client: PowerStationClient; status: StatusState }) {
  const scenes = status.data?.scenes ?? [];
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<{ tone: "ok" | "warn"; text: string } | null>(null);
  if (scenes.length === 0) return null;

  async function run(scene: Scene, mode: "apply" | "off") {
    setBusy(`${scene.id}/${mode}`);
    setResult(null);
    try {
      const r = await client.runScene(scene.id, mode);
      r.devices.forEach(status.applyDevice);
      const verb = mode === "off" ? "turned off" : "applied";
      if (r.failed === 0) setResult({ tone: "ok", text: `${scene.name} ${verb}.` });
      else {
        const why = [...new Set(r.results.filter((x) => !x.ok).map((x) => x.error))].join(" ");
        setResult({ tone: "warn", text: `${scene.name}: ${r.succeeded} of ${r.succeeded + r.failed} outputs ${verb}. ${why}` });
      }
    } catch (err) {
      setResult({ tone: "warn", text: `${scene.name}: ${message(err)}` });
    } finally {
      setBusy(null);
    }
  }

  return (
    <section className={c("scene-box")} aria-labelledby="ps-scene-box">
      <h3 className={c("group-label", "group-label--box")} id="ps-scene-box">
        <SceneIcon /> Scenes
      </h3>
      <div className={c("scenes")}>
        {scenes.map((s) => (
          <div className={c("scene")} key={s.id}>
            <div>
              <div className={c("scene-name")} title={s.name}>{s.name}</div>
              <div className={c("scene-meta")}>{describe(s)}</div>
            </div>
            <div className={c("scene-actions")}>
              <button
                type="button"
                className={c("button", "button--small", "button--primary")}
                disabled={busy !== null || s.targets.length === 0}
                onClick={() => run(s, "apply")}
              >
                {busy === `${s.id}/apply` ? "Applying…" : "Apply"}
              </button>
              <button
                type="button"
                className={c("button", "button--small")}
                aria-label={`Turn off everything in ${s.name}`}
                disabled={busy !== null || s.targets.length === 0}
                onClick={() => run(s, "off")}
              >
                {busy === `${s.id}/off` ? "…" : "All off"}
              </button>
            </div>
          </div>
        ))}
      </div>
      {result && <Notice tone={result.tone}>{result.text}</Notice>}
    </section>
  );
}

export function SceneIcon() {
  return (
    <svg viewBox="0 0 16 16" width="12" height="12" aria-hidden="true" focusable="false">
      <rect x="1.5" y="1.5" width="5" height="5" rx="1" fill="currentColor" />
      <rect x="9.5" y="1.5" width="5" height="5" rx="1" fill="none" stroke="currentColor" strokeWidth="1.3" />
      <rect x="1.5" y="9.5" width="5" height="5" rx="1" fill="none" stroke="currentColor" strokeWidth="1.3" />
      <rect x="9.5" y="9.5" width="5" height="5" rx="1" fill="currentColor" />
    </svg>
  );
}

// ---------------------------------------------------------------- edit

type Draft = {
  id: string | null;
  name: string;
  fadeSeconds: string;
  safetyMinutes: string;
  onStart: boolean;
  startDelay: string;
  onStop: boolean;
  targets: Map<string, SceneTarget>;
};

const emptyDraft = (): Draft => ({
  id: null, name: "", fadeSeconds: "", safetyMinutes: "", onStart: false, startDelay: "10", onStop: false, targets: new Map(),
});

/** The start/stop rules a scene owns: plain rules, created and removed from the scene editor. */
export function sceneHooks(rules: Rule[], sceneId: string | null) {
  const start = rules.find((r) => r.trigger.type === "zeusStart" && r.action.type === "scene" && r.action.sceneId === sceneId && r.action.mode === "apply");
  const stop = rules.find((r) => r.trigger.type === "zeusStop" && r.action.type === "scene" && r.action.sceneId === sceneId && r.action.mode === "off");
  return { start, stop };
}

const draftFrom = (s: Scene, rules: Rule[]): Draft => {
  const { start, stop } = sceneHooks(rules, s.id);
  return {
    id: s.id,
    name: s.name,
    fadeSeconds: s.fadeSeconds ? String(s.fadeSeconds) : "",
    safetyMinutes: s.safetyMinutes ? String(s.safetyMinutes) : "",
    onStart: !!start,
    startDelay: String(start?.delaySeconds ?? 10),
    onStop: !!stop,
    targets: new Map(s.targets.map((t) => [targetKey(t), t])),
  };
};

export function ScenesSettings({ client, status }: { client: PowerStationClient; status: StatusState }) {
  const scenes = status.data?.scenes ?? [];
  const devices = status.data?.devices ?? [];
  const rules = status.data?.rules ?? [];
  const [draft, setDraft] = useState<Draft | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const hasOutputs = devices.some((d) => d.status.channels.length > 0);

  async function remove(scene: Scene) {
    setError(null);
    try {
      await client.deleteScene(scene.id);
      status.dropScene(scene.id);
      setConfirmDelete(null);
    } catch (err) {
      setError(message(err));
    }
  }

  return (
    <section aria-labelledby="ps-scenes-title">
      <h3 className={c("section-title")} id="ps-scenes-title" style={{ marginTop: 0 }}>Scenes</h3>
      <p className={c("hint")} style={{ margin: "0 0 8px" }}>
        A scene sets several outputs and dimmer levels in one click. Each scene also has an All off button, so it works as an
        on/off group.
      </p>
      {error && <Notice tone="error">{error}</Notice>}
      {scenes.map((s) =>
        draft?.id === s.id ? null : (
          <div className={c("device")} key={s.id}>
            <div className={c("device-head")}>
              <h4 className={c("device-name")}>{s.name}</h4>
              <span className={c("device-meta")}>{describe(s)}</span>
              {sceneHooks(rules, s.id).start && <span className={c("badge", "badge--muted")}>On start</span>}
              {sceneHooks(rules, s.id).stop && <span className={c("badge", "badge--muted")}>Off at stop</span>}
              <span style={{ marginLeft: "auto" }} className={c("row")}>
                {confirmDelete === s.id ? (
                  <>
                    <button type="button" className={c("button", "button--small", "button--danger")} onClick={() => remove(s)}>
                      Delete {s.name}
                    </button>
                    <button type="button" className={c("button", "button--small")} onClick={() => setConfirmDelete(null)}>
                      Cancel
                    </button>
                  </>
                ) : (
                  <>
                    <button type="button" className={c("button", "button--small")} onClick={() => setDraft(draftFrom(s, rules))}>
                      Edit
                    </button>
                    <button type="button" className={c("button", "button--small")} onClick={() => setConfirmDelete(s.id)}>
                      Delete…
                    </button>
                  </>
                )}
              </span>
            </div>
          </div>
        ),
      )}
      {draft ? (
        <SceneEditor
          draft={draft}
          devices={devices}
          rules={rules}
          client={client}
          onCancel={() => setDraft(null)}
          onSaved={(scene) => {
            status.upsertScene(scene);
            status.reload();
            setDraft(null);
          }}
        />
      ) : (
        <div className={c("row")}>
          <button type="button" className={c("button")} disabled={!hasOutputs} onClick={() => setDraft(emptyDraft())}>
            New scene
          </button>
          {!hasOutputs && <span className={c("hint")}>Add a device first.</span>}
        </div>
      )}
    </section>
  );
}

function SceneEditor({
  draft: initial,
  devices,
  rules,
  client,
  onCancel,
  onSaved,
}: {
  draft: Draft;
  devices: DeviceView[];
  rules: Rule[];
  client: PowerStationClient;
  onCancel: () => void;
  onSaved: (s: Scene) => void;
}) {
  const ids = useId();
  const [draft, setDraft] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const update = (fn: (targets: Map<string, SceneTarget>) => void) =>
    setDraft((d) => {
      const targets = new Map(d.targets);
      fn(targets);
      return { ...d, targets };
    });

  /** Include every output with its current state and level. */
  function fillFromCurrent() {
    update((targets) => {
      targets.clear();
      for (const d of devices)
        for (const ch of d.status.channels.filter((x) => x.kind !== "Meter"))
          targets.set(targetKey({ deviceId: d.deviceId, kind: ch.kind, index: ch.index }), {
            deviceId: d.deviceId,
            kind: ch.kind,
            index: ch.index,
            on: ch.on,
            brightness: isDimmable(ch.kind) && ch.on && ch.brightness ? Math.round(ch.brightness) : null,
            ...(isColor(ch.kind) && ch.on ? { rgb: ch.rgb ?? [255, 255, 255], white: ch.kind === "Rgbw" ? ch.white ?? 0 : null } : {}),
          });
    });
  }

  async function save() {
    setBusy(true);
    setError(null);
    const num = (v: string) => (v.trim() === "" ? null : Number(v));
    const body = {
      name: draft.name,
      fadeSeconds: num(draft.fadeSeconds),
      safetyMinutes: num(draft.safetyMinutes),
      targets: [...draft.targets.values()],
    };
    try {
      const scene = draft.id ? await client.updateScene(draft.id, body) : await client.createScene(body);
      // Start/stop hooks are ordinary rules; add or remove them to match the ticks.
      const { start, stop } = sceneHooks(rules, scene.id);
      const delay = Math.max(0, Number(draft.startDelay) || 0);
      if (draft.onStart) {
        const rule = {
          name: `${scene.name} when Zeus starts`, enabled: true, trigger: { type: "zeusStart" as const },
          action: { type: "scene" as const, sceneId: scene.id, mode: "apply" as const }, delaySeconds: delay,
        };
        if (start) await client.updateRule(start.id, { ...rule, enabled: start.enabled });
        else await client.createRule(rule);
      } else if (start) await client.deleteRule(start.id);
      if (draft.onStop && !stop)
        await client.createRule({
          name: `${scene.name} off when Zeus stops`, enabled: true, trigger: { type: "zeusStop" },
          action: { type: "scene", sceneId: scene.id, mode: "off" },
        });
      else if (!draft.onStop && stop) await client.deleteRule(stop.id);
      onSaved(scene);
    } catch (err) {
      setError(message(err));
    } finally {
      setBusy(false);
    }
  }

  const count = draft.targets.size;

  return (
    <div className={c("form")} role="group" aria-labelledby={`${ids}-t`}>
      <h4 className={c("section-title")} id={`${ids}-t`} style={{ margin: 0 }}>
        {draft.id ? `Edit ${initial.name}` : "New scene"}
      </h4>
      <div className={c("fields")}>
        <div className={c("field")}>
          <label htmlFor={`${ids}-name`}>Scene name</label>
          <input
            id={`${ids}-name`}
            value={draft.name}
            maxLength={40}
            placeholder="Operating"
            onChange={(e) => {
              const name = e.currentTarget.value;
              setDraft((d) => ({ ...d, name }));
            }}
          />
        </div>
        <div className={c("field")}>
          <label htmlFor={`${ids}-fade`}>Dimmer fade (seconds, optional)</label>
          <input
            id={`${ids}-fade`}
            type="number"
            min={0}
            max={600}
            step={0.5}
            value={draft.fadeSeconds}
            placeholder="0"
            onChange={(e) => {
              const fadeSeconds = e.currentTarget.value;
              setDraft((d) => ({ ...d, fadeSeconds }));
            }}
          />
          <span className={c("hint")}>Dimmers ramp to their level over this time.</span>
        </div>
        <div className={c("field")}>
          <label htmlFor={`${ids}-safety`}>Safety timer (minutes, optional)</label>
          <input
            id={`${ids}-safety`}
            type="number"
            min={1}
            max={1440}
            value={draft.safetyMinutes}
            placeholder="None"
            onChange={(e) => {
              const safetyMinutes = e.currentTarget.value;
              setDraft((d) => ({ ...d, safetyMinutes }));
            }}
          />
          <span className={c("hint")}>
            Outputs this scene turns on switch themselves off this long after Zeus stops or crashes.
          </span>
        </div>
      </div>

      <fieldset className={c("fieldset")}>
        <legend>Run automatically</legend>
        <div className={c("row")}>
          <label className={c("check")}>
            <input
              type="checkbox"
              checked={draft.onStart}
              onChange={(e) => {
                const onStart = e.currentTarget.checked;
                setDraft((d) => ({ ...d, onStart }));
              }}
            />
            <span>Apply when Zeus starts, after</span>
          </label>
          <input
            className={c("inline-num")}
            type="number"
            min={0}
            max={600}
            aria-label="Seconds after Zeus starts"
            value={draft.startDelay}
            disabled={!draft.onStart}
            onChange={(e) => {
              const startDelay = e.currentTarget.value;
              setDraft((d) => ({ ...d, startDelay }));
            }}
          />
          <span className={c("pick-unit")}>seconds</span>
        </div>
        <label className={c("check")}>
          <input
            type="checkbox"
            checked={draft.onStop}
            onChange={(e) => {
              const onStop = e.currentTarget.checked;
              setDraft((d) => ({ ...d, onStop }));
            }}
          />
          <span>All off when Zeus closes normally</span>
        </label>
        <span className={c("hint")}>
          Closing is only seen when Zeus shuts down cleanly. For a crash or power cut, use the safety timer above.
          These appear in Automations as ordinary rules.
        </span>
      </fieldset>

      <div className={c("row")}>
        <span className={c("hint")}>Tick the outputs this scene controls and choose what each should do.</span>
        <button type="button" className={c("button", "button--small")} onClick={fillFromCurrent} style={{ marginLeft: "auto" }}>
          Use current states
        </button>
      </div>

      {devices.map((d) =>
        d.status.channels.every((x) => x.kind === "Meter") ? null : (
          <div className={c("pick-device")} key={d.deviceId}>
            <p className={c("pick-device-name")}>{d.displayName}</p>
            {d.status.channels.filter((x) => x.kind !== "Meter").map((ch) => {
              const key = targetKey({ deviceId: d.deviceId, kind: ch.kind, index: ch.index });
              const t = draft.targets.get(key);
              const label = channelLabel(ch);
              const box = `${ids}-${key}`;
              return (
                <div className={c("pick", t && "pick--on")} key={key}>
                  <input
                    type="checkbox"
                    id={box}
                    checked={!!t}
                    onChange={(e) => {
                      const checked = e.currentTarget.checked;
                      update((targets) => {
                        if (!checked) targets.delete(key);
                        else
                          targets.set(key, {
                            deviceId: d.deviceId,
                            kind: ch.kind,
                            index: ch.index,
                            on: true,
                            brightness: isDimmable(ch.kind) ? Math.round(ch.brightness || 100) : null,
                            ...(isColor(ch.kind)
                              ? { rgb: ch.rgb ?? [255, 255, 255], white: ch.kind === "Rgbw" ? ch.white ?? 0 : null }
                              : {}),
                          });
                      });
                    }}
                  />
                  <label htmlFor={box} title={label}>{label}</label>
                  {t ? (
                    <span className={c("pick-state")}>
                      <select
                        aria-label={`${label} state`}
                        value={t.on ? "on" : "off"}
                        onChange={(e) => {
                          const on = e.currentTarget.value === "on";
                          update((targets) =>
                            targets.set(key, {
                              ...t,
                              on,
                              brightness: on && isDimmable(t.kind) ? t.brightness ?? 100 : null,
                              rgb: on && isColor(t.kind) ? t.rgb ?? [255, 255, 255] : null,
                              white: on && t.kind === "Rgbw" ? t.white ?? 0 : null,
                            }),
                          );
                        }}
                      >
                        <option value="on">On</option>
                        <option value="off">Off</option>
                      </select>
                      {isDimmable(t.kind) && t.on && (
                        <>
                          <input
                            type="number"
                            min={1}
                            max={100}
                            aria-label={`${label} level`}
                            value={t.brightness ?? ""}
                            onChange={(e) => {
                              const v = e.currentTarget.value;
                              update((targets) =>
                                targets.set(key, { ...t, brightness: v === "" ? null : Math.max(1, Math.min(100, Number(v))) }),
                              );
                            }}
                          />
                          <span className={c("pick-unit")}>%</span>
                        </>
                      )}
                    </span>
                  ) : (
                    <span className={c("pick-unit")}>Not in scene</span>
                  )}
                  {t && isColor(t.kind) && t.on && (
                    <div className={c("pick-color")}>
                      <ColorPicker
                        compact
                        label={label}
                        withWhite={t.kind === "Rgbw"}
                        value={{ rgb: (t.rgb ?? [255, 255, 255]) as Rgb, white: t.white }}
                        onChange={(v) => update((targets) => targets.set(key, { ...t, rgb: v.rgb, white: v.white ?? null }))}
                      />
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        ),
      )}

      {error && <Notice tone="error">{error}</Notice>}
      <div className={c("row")}>
        <button
          type="button"
          className={c("button", "button--primary")}
          disabled={busy || count === 0 || !draft.name.trim()}
          onClick={save}
        >
          {busy ? "Saving…" : draft.id ? "Save scene" : `Create scene (${count} output${count === 1 ? "" : "s"})`}
        </button>
        <button type="button" className={c("button")} onClick={onCancel} disabled={busy}>
          Cancel
        </button>
      </div>
    </div>
  );
}
