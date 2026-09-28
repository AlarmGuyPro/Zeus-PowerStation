// SPDX-License-Identifier: GPL-2.0-or-later
// All PowerStation CSS. Every selector sits under the feature-owned root
// class and uses only the public Zeus design tokens (no raw colors), so the
// panels follow the operator's theme in both dark and light modes.

export const ROOT = "io-github-alarmguypro-powerstation";

/** Prefixed secondary class name: c("tile") -> "io-github-...__tile". */
export const c = (...names: (string | false | null | undefined)[]) =>
  names.filter(Boolean).map((n) => `${ROOT}__${n}`).join(" ");

const r = `.${ROOT}`;
const e = (name: string) => `.${ROOT}__${name}`;

export const CSS = `
${r} {
  color: var(--fg-1);
  background: var(--bg-1);
  font-family: var(--font-sans);
  font-size: 12px;
  font-weight: 400;
  line-height: 1.4;
  height: 100%;
  overflow: auto;
  box-sizing: border-box;
  padding: 10px;
  container-type: inline-size;
}
${r} *, ${r} *::before, ${r} *::after { box-sizing: border-box; }
${r} :focus-visible { outline: 2px solid var(--accent-bright); outline-offset: 2px; }
${r} :where(button, input) { font: inherit; color: inherit; }

${e("header")} { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 4px 12px; margin: 0 0 10px; }
${e("tabs")} { display: inline-flex; gap: 2px; padding: 2px; border: 1px solid var(--line); border-radius: var(--r-sm); background: var(--bg-inset); }
${e("tab")} { min-height: 26px; padding: 2px 12px; border: 0; border-radius: var(--r-xs); background: transparent; color: var(--fg-2); cursor: pointer; font-size: 12px; }
${e("tab")}:hover { color: var(--fg-0); }
${e("tab")}[aria-selected="true"] { background: var(--bg-3); color: var(--fg-0); box-shadow: inset 0 -2px 0 var(--accent); }
${e("title")} { font-size: 13px; font-weight: 500; color: var(--fg-0); margin: 0; }
${e("summary")} { color: var(--fg-2); font-family: var(--font-mono); font-size: 11px; }

${e("device")} { border: 1px solid var(--panel-border); border-radius: var(--r-md); background: var(--bg-2); margin: 0 0 10px; }
${e("device-head")} { display: flex; flex-wrap: nowrap; align-items: center; gap: 6px 8px; padding: 8px 10px; border-bottom: 1px solid var(--line); min-width: 0; }
${e("device-name")} { font-weight: 500; color: var(--fg-0); margin: 0; font-size: 12px; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
${e("device-meta")} { color: var(--fg-3); font-size: 10.5px; font-family: var(--font-mono); min-width: 0; flex: 0 100 auto; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
${e("device-body")} { padding: 8px; }

${e("health")} { display: inline-flex; align-items: center; gap: 5px; font-size: 10.5px; color: var(--fg-2); margin-left: auto; flex: none; white-space: nowrap; }
${e("health")}::before { content: ""; width: 8px; height: 8px; border-radius: 50%; background: var(--fg-3); flex: none; }
${e("health--online")}::before { background: var(--ok); }
${e("health--warn")}::before { background: var(--amber); }
${e("health--bad")}::before { background: var(--tx); }

${e("grid")} { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(160px, 100%), 1fr)); gap: 8px; }
${e("tile")} { border: 1px solid var(--line); border-radius: var(--r-sm); background: var(--bg-1); padding: 8px; display: flex; flex-direction: column; gap: 6px; min-width: 0; }
${e("tile--on")} { border-color: var(--accent); background: var(--bg-3); }
${e("tile-top")} { display: flex; align-items: center; justify-content: space-between; gap: 6px; }
${e("tile-name")} { font-weight: 500; font-size: 12px; color: var(--fg-0); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
${e("state")} { font-size: 10px; font-weight: 500; letter-spacing: 0.06em; text-transform: uppercase; color: var(--fg-3); }
${e("state--on")} { color: var(--accent-bright); }

${e("button")} {
  min-height: 30px; padding: 3px 10px; border-radius: var(--r-sm); font-size: 12px; font-weight: 400;
  border: 1px solid var(--line-strong); background: var(--bg-2); cursor: pointer;
}
${e("button")}:hover:not(:disabled) { background: var(--bg-3); }
${e("button")}:disabled { cursor: default; opacity: 0.55; }
${e("button--primary")} { border-color: var(--accent); color: var(--fg-0); background: var(--bg-3); }
${e("button--danger")} { border-color: var(--tx); color: var(--tx); }
${e("button--small")} { min-height: 26px; padding: 2px 8px; font-size: 11px; }
${e("power")} { width: 100%; min-height: 32px; }
${e("power")}[aria-pressed="true"] { border-color: var(--accent); background: var(--bg-inset); color: var(--accent-bright); }

${e("meter")} { font-family: var(--font-mono); color: var(--fg-2); font-size: 10.5px; display: flex; flex-wrap: wrap; gap: 2px 10px; }
${e("meter-main")} { font-size: 13px; color: var(--fg-0); font-variant-numeric: tabular-nums; }

${e("dimmer")} { display: grid; grid-template-columns: auto 1fr auto; align-items: center; gap: 6px; }
${e("dimmer")} input[type="range"] { width: 100%; min-width: 0; accent-color: var(--accent); }
${e("dim-value")} { font-family: var(--font-mono); font-size: 11px; color: var(--fg-1); text-align: center; }

${e("notice")} { border-radius: var(--r-sm); padding: 8px 10px; background: var(--bg-inset); color: var(--fg-2); margin: 0 0 8px; border-left: 3px solid var(--line-strong); }
${e("notice--warn")} { border-left-color: var(--amber); color: var(--fg-1); }
${e("notice--error")} { border-left-color: var(--tx); color: var(--fg-1); }
${e("notice--ok")} { border-left-color: var(--ok); color: var(--fg-1); }
${e("badge")} { display: inline-block; font-size: 10px; font-weight: 500; text-transform: uppercase; letter-spacing: 0.04em; padding: 1px 6px; border-radius: var(--r-xs); border: 1px solid var(--amber); color: var(--amber); }
${e("badge--danger")} { border-color: var(--tx); color: var(--tx); }

${e("empty")} { text-align: center; color: var(--fg-2); padding: 24px 12px; }
${e("empty")} strong { display: block; color: var(--fg-0); margin-bottom: 4px; }

${e("form")} { display: grid; gap: 8px; margin: 0 0 12px; padding: 10px; border: 1px solid var(--panel-border); border-radius: var(--r-md); background: var(--bg-2); }
${e("field")} { display: grid; gap: 3px; min-width: 0; }
${e("field")} label { font-size: 12px; color: var(--fg-2); }
${e("field")} input { min-height: 32px; padding: 4px 8px; border-radius: var(--r-sm); border: 1px solid var(--line-strong); background: var(--bg-inset); min-width: 0; width: 100%; }
${e("hint")} { font-size: 11px; color: var(--fg-3); }
${e("row")} { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
${e("fields")} { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 8px; }
${e("kv")} { display: grid; grid-template-columns: max-content 1fr; gap: 2px 10px; font-size: 12px; margin: 0 0 8px; }
${e("kv")} dt { color: var(--fg-3); }
${e("kv")} dd { margin: 0; font-family: var(--font-mono); color: var(--fg-1); overflow-wrap: anywhere; }
${e("section-title")} { font-size: 11px; font-weight: 500; color: var(--fg-2); text-transform: uppercase; letter-spacing: 0.05em; margin: 12px 0 6px; }
${e("sr")} { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }

${e("scenes")} { display: grid; grid-template-columns: repeat(auto-fill, minmax(170px, 1fr)); gap: 6px; margin: 0 0 10px; }
${e("scene")} { border: 1px solid var(--line); border-radius: var(--r-sm); background: var(--bg-2); padding: 6px 8px; display: grid; gap: 5px; min-width: 0; }
${e("scene-name")} { font-size: 12px; font-weight: 500; color: var(--fg-0); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
${e("scene-meta")} { font-size: 10.5px; color: var(--fg-3); }
${e("scene-actions")} { display: grid; grid-template-columns: 1fr auto; gap: 5px; }

${e("pick-device")} { margin: 0 0 8px; }
${e("pick-device-name")} { font-size: 11px; color: var(--fg-2); margin: 0 0 4px; }
${e("pick")} { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; align-items: center; gap: 4px 8px; padding: 4px 6px; border-radius: var(--r-xs); }
${e("pick--on")} { background: var(--bg-3); }
${e("pick")} label { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
${e("pick")} select, ${e("pick")} input[type="number"] { font: inherit; color: var(--fg-0); background: var(--bg-inset); border: 1px solid var(--line-strong); border-radius: var(--r-xs); min-height: 26px; padding: 1px 4px; }
${e("pick")} input[type="number"] { width: 4.5em; }
${e("pick-state")} { display: inline-flex; gap: 4px; align-items: center; }
${e("pick-unit")} { color: var(--fg-3); font-size: 11px; }

${e("led")} { display: inline-block; width: 8px; height: 8px; border-radius: 50%; flex: none; background: var(--fg-3); box-shadow: 0 0 0 1px var(--line); }
${e("led--on")} { background: var(--ok); box-shadow: 0 0 5px var(--ok); }
${e("led--off")} { background: color-mix(in srgb, var(--amber) 55%, var(--tx)); box-shadow: 0 0 5px color-mix(in srgb, var(--amber) 55%, var(--tx)); }
${e("led--dim")} { background: var(--accent-bright); box-shadow: 0 0 5px var(--accent); }
${e("tile-label")} { display: inline-flex; align-items: center; gap: 6px; min-width: 0; }

${e("chips")} { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
${e("chip")} { display: inline-flex; align-items: center; gap: 4px; font-family: var(--font-mono); font-size: 11px; padding: 2px 4px 2px 8px; border: 1px solid var(--line-strong); border-radius: var(--r-sm); background: var(--bg-1); }
${e("chip")} button { border: 0; background: transparent; color: var(--fg-2); cursor: pointer; min-width: 22px; min-height: 22px; border-radius: var(--r-xs); }
${e("chip")} button:hover { color: var(--fg-0); background: var(--bg-3); }
${e("chip--suggest")} { border-style: dashed; color: var(--fg-2); padding: 2px 8px; cursor: pointer; background: transparent; }
${e("check")} { display: flex; align-items: flex-start; gap: 6px; font-size: 12px; color: var(--fg-1); }
${e("check")} input { margin-top: 2px; accent-color: var(--accent); }
${e("progress")} { height: 4px; border-radius: 2px; background: var(--bg-inset); overflow: hidden; }
${e("progress")} > span { display: block; height: 100%; background: var(--accent); }
${e("found")} { display: grid; gap: 6px; }
${e("found-row")} { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 6px 10px; align-items: center; padding: 6px 8px; border: 1px solid var(--line); border-radius: var(--r-sm); background: var(--bg-1); }
${e("found-name")} { font-weight: 500; color: var(--fg-0); overflow-wrap: anywhere; }
${e("found-meta")} { font-family: var(--font-mono); font-size: 10.5px; color: var(--fg-3); overflow-wrap: anywhere; }
${e("found-add")} { grid-column: 1 / -1; display: flex; flex-wrap: wrap; gap: 6px; align-items: end; }
${e("found-add")} ${e("field")} { flex: 1 1 140px; }
${e("badge--ok")} { border-color: var(--ok); color: var(--ok); }
${e("badge--muted")} { border-color: var(--line-strong); color: var(--fg-3); }

${e("toolbar")} { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 6px 10px; margin: 0 0 8px; }
${e("seg")} { display: inline-flex; align-items: center; gap: 2px; padding: 2px; border: 1px solid var(--line); border-radius: var(--r-sm); background: var(--bg-inset); }
${e("seg-label")} { font-size: 11px; color: var(--fg-3); padding: 0 6px 0 4px; }
${e("seg-btn")} { min-width: 30px; min-height: 24px; padding: 1px 8px; border: 0; border-radius: var(--r-xs); background: transparent; color: var(--fg-2); cursor: pointer; font-size: 11px; }
${e("seg-btn")}[aria-checked="true"] { background: var(--bg-3); color: var(--fg-0); box-shadow: inset 0 -2px 0 var(--accent); }

${e("snap")} { display: grid; gap: 10px; align-items: stretch; }
${e("snap--arranging")} { padding: 4px; margin: -4px; border-radius: var(--r-md); outline: 1px dashed var(--line-strong); }
${e("cell")} { display: flex; min-width: 0; container-type: inline-size; }
@container (max-width: 210px) { ${e("device-meta")} { display: none; } }
${e("cell")} > ${e("device")} { flex: 1; display: flex; flex-direction: column; min-width: 0; }
${e("cell-end")} { display: grid; place-items: center; min-height: 60px; border: 1px dashed var(--line-strong); border-radius: var(--r-md); color: var(--fg-3); font-size: 11px; }
${e("cell-end--active")} { border-color: var(--accent); color: var(--fg-1); }
${e("columns")} { display: grid; gap: 10px; align-items: start; }
${e("column")} { display: flex; flex-direction: column; gap: 10px; min-width: 0; min-height: 40px; border-radius: var(--r-md); }
${e("column--arranging")} { outline: 1px dashed var(--line-strong); outline-offset: 3px; padding-bottom: 24px; }
${e("column--drop")} { outline-color: var(--accent); }
${e("column-empty")} { border: 1px dashed var(--line-strong); border-radius: var(--r-md); padding: 18px 8px; text-align: center; color: var(--fg-3); font-size: 11px; }
${e("flow")} { display: grid; gap: 10px; grid-template-columns: repeat(auto-fill, minmax(210px, 1fr)); align-items: start; }
${e("cell")} > ${e("device")} { margin: 0; }
${e("cell--arranging")} { cursor: grab; }
${e("cell--arranging")} > ${e("device")} { border-style: dashed; }
${e("cell--dragging")} { opacity: 0.45; }
${e("cell--drop-before")} { box-shadow: -4px 0 0 var(--accent); border-radius: var(--r-md); }
${e("grip")} { color: var(--fg-3); font-size: 12px; line-height: 1; }
${e("move")} { display: inline-flex; gap: 2px; margin-left: auto; flex: none; }
${e("move-btn")} { min-width: 24px; min-height: 24px; border: 1px solid var(--line-strong); border-radius: var(--r-xs); background: var(--bg-1); color: var(--fg-1); cursor: pointer; font-size: 12px; line-height: 1; }
${e("move-btn")}:disabled { opacity: 0.35; cursor: default; }

${e("tile--dimmer")} { gap: 8px; }
${e("wd")} { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 10px; align-items: center; }
${e("wd-plate")} { display: flex; flex-direction: column; align-items: center; justify-content: space-between; gap: 6px; width: 58px; height: 138px; padding: 6px 0; border-radius: 7px; background: var(--wd-face); border: 1px solid var(--wd-edge); box-shadow: 0 1px 2px var(--line); }
${e("wd-screw")} { width: 5px; height: 5px; border-radius: 50%; border: 1px solid var(--wd-edge); flex: none; }
${e("wd-paddle")} { flex: 1; width: 40px; display: flex; flex-direction: column; align-items: center; justify-content: space-between; padding: 7px 0 6px; border-radius: 4px; border: 1px solid var(--wd-edge); background: var(--wd-face); box-shadow: 0 1px 0 var(--wd-edge); }
${e("wd-channel")} { display: flex; flex-direction: column; align-items: center; padding: 3px 0; width: 14px; border-radius: 3px; box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--wd-edge) 55%, transparent); }
${e("wd-dot")} { width: 14px; height: 10px; padding: 0; border: 0; background: transparent; cursor: pointer; display: grid; place-items: center; }
${e("wd-dot")} > span { width: 4px; height: 4px; border-radius: 50%; background: var(--wd-dot); }
${e("wd-dot--lit")} > span { background: var(--accent-bright); box-shadow: 0 0 4px var(--accent-bright); }
${e("wd-dot")}:disabled { cursor: default; }
${e("wd-dot")}:focus-visible { outline-offset: 0; }
${e("wd-square")} { width: 18px; height: 18px; padding: 0; border: 0; background: transparent; cursor: pointer; display: grid; place-items: center; }
${e("wd-square")} > span { width: 13px; height: 13px; border-radius: 3px; border: 2px solid var(--wd-mark); box-sizing: border-box; }
${e("wd-square--on")} > span { box-shadow: 0 0 5px var(--accent-bright); border-color: var(--accent-bright); }
${e("wd-square")}:disabled { cursor: default; opacity: 0.6; }
${e("wd-side")} { display: flex; flex-direction: column; gap: 6px; min-width: 0; }
${e("wd-level")} { font-family: var(--font-mono); font-size: 18px; color: var(--fg-0); font-variant-numeric: tabular-nums; }

@media (prefers-reduced-motion: no-preference) {
  ${e("tile")}, ${e("button")} { transition: background var(--dur-fast) var(--ease-out), border-color var(--dur-fast) var(--ease-out); }
}
`;
