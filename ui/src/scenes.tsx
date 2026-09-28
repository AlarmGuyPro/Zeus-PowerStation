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
  type Scene,
  type SceneTarget,
} from "./api";
import { Notice, channelLabel, type StatusState } from "./shared";
import { c } from "./styles";

const message = (err: unknown) => (err instanceof ApiError ? err.message : String(err));
const targetKey = (t: { deviceId: string; kind: ChannelKind; index: number }) => `${t.deviceId}/${t.kind}/${t.index}`;

function describe(scene: Scene) {
  const on = scene.targets.filter((t) => t.on).length;
  const off = scene.targets.length - on;
  const parts = [`${scene.targets.length} output${scene.targets.length === 1 ? "" : "s"}`];
  if (on && off) parts.push(`${on} on, ${off} off`);
  if (scene.fadeSeconds) parts.push(`${scene.fadeSeconds}s fade`);
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
    <>
      <div className={c("scenes")} role="group" aria-label="Scenes">
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
    </>
  );
}

// ---------------------------------------------------------------- edit

type Draft = { id: string | null; name: string; fadeSeconds: string; targets: Map<string, SceneTarget> };

const emptyDraft = (): Draft => ({ id: null, name: "", fadeSeconds: "", targets: new Map() });
const draftFrom = (s: Scene): Draft => ({
  id: s.id,
  name: s.name,
  fadeSeconds: s.fadeSeconds ? String(s.fadeSeconds) : "",
  targets: new Map(s.targets.map((t) => [targetKey(t), t])),
});

export function ScenesSettings({ client, status }: { client: PowerStationClient; status: StatusState }) {
  const scenes = status.data?.scenes ?? [];
  const devices = status.data?.devices ?? [];
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
      <h3 className={c("section-title")} id="ps-scenes-title">Scenes</h3>
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
                    <button type="button" className={c("button", "button--small")} onClick={() => setDraft(draftFrom(s))}>
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
          client={client}
          onCancel={() => setDraft(null)}
          onSaved={(scene) => {
            status.upsertScene(scene);
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
  client,
  onCancel,
  onSaved,
}: {
  draft: Draft;
  devices: DeviceView[];
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
        for (const ch of d.status.channels)
          targets.set(targetKey({ deviceId: d.deviceId, kind: ch.kind, index: ch.index }), {
            deviceId: d.deviceId,
            kind: ch.kind,
            index: ch.index,
            on: ch.on,
            brightness: ch.kind === "Light" && ch.on && ch.brightness ? Math.round(ch.brightness) : null,
          });
    });
  }

  async function save() {
    setBusy(true);
    setError(null);
    const fade = draft.fadeSeconds.trim() === "" ? null : Number(draft.fadeSeconds);
    const body = { name: draft.name, fadeSeconds: fade, targets: [...draft.targets.values()] };
    try {
      onSaved(draft.id ? await client.updateScene(draft.id, body) : await client.createScene(body));
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
        </div>
      </div>

      <div className={c("row")}>
        <span className={c("hint")}>Tick the outputs this scene controls and choose what each should do.</span>
        <button type="button" className={c("button", "button--small")} onClick={fillFromCurrent} style={{ marginLeft: "auto" }}>
          Use current states
        </button>
      </div>

      {devices.map((d) =>
        d.status.channels.length === 0 ? null : (
          <div className={c("pick-device")} key={d.deviceId}>
            <p className={c("pick-device-name")}>{d.displayName}</p>
            {d.status.channels.map((ch) => {
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
                            brightness: ch.kind === "Light" ? Math.round(ch.brightness || 100) : null,
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
                            targets.set(key, { ...t, on, brightness: on && t.kind === "Light" ? t.brightness ?? 100 : null }),
                          );
                        }}
                      >
                        <option value="on">On</option>
                        <option value="off">Off</option>
                      </select>
                      {t.kind === "Light" && t.on && (
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
