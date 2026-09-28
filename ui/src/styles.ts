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
  font-size: 13px;
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

${e("header")} { display: flex; flex-wrap: wrap; align-items: baseline; justify-content: space-between; gap: 4px 12px; margin: 0 0 10px; }
${e("title")} { font-size: 14px; font-weight: 600; color: var(--fg-0); margin: 0; }
${e("summary")} { color: var(--fg-2); font-family: var(--font-mono); font-size: 12px; }

${e("device")} { border: 1px solid var(--panel-border); border-radius: var(--r-md); background: var(--bg-2); margin: 0 0 10px; }
${e("device-head")} { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; padding: 8px 10px; border-bottom: 1px solid var(--line); }
${e("device-name")} { font-weight: 600; color: var(--fg-0); margin: 0; font-size: 13px; overflow-wrap: anywhere; }
${e("device-meta")} { color: var(--fg-3); font-size: 11px; font-family: var(--font-mono); overflow-wrap: anywhere; }
${e("device-body")} { padding: 8px; }

${e("health")} { display: inline-flex; align-items: center; gap: 5px; font-size: 11px; color: var(--fg-2); margin-left: auto; }
${e("health")}::before { content: ""; width: 8px; height: 8px; border-radius: 50%; background: var(--fg-3); flex: none; }
${e("health--online")}::before { background: var(--ok); }
${e("health--warn")}::before { background: var(--amber); }
${e("health--bad")}::before { background: var(--tx); }

${e("grid")} { display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); gap: 8px; }
${e("tile")} { border: 1px solid var(--line); border-radius: var(--r-sm); background: var(--bg-1); padding: 8px; display: flex; flex-direction: column; gap: 6px; min-width: 0; }
${e("tile--on")} { border-color: var(--accent); background: var(--bg-3); }
${e("tile-top")} { display: flex; align-items: center; justify-content: space-between; gap: 6px; }
${e("tile-name")} { font-weight: 600; color: var(--fg-0); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
${e("state")} { font-size: 11px; font-weight: 600; letter-spacing: 0.04em; text-transform: uppercase; color: var(--fg-3); }
${e("state--on")} { color: var(--accent-bright); }

${e("button")} {
  min-height: 32px; padding: 4px 12px; border-radius: var(--r-sm);
  border: 1px solid var(--line-strong); background: var(--bg-2); cursor: pointer;
}
${e("button")}:hover:not(:disabled) { background: var(--bg-3); }
${e("button")}:disabled { cursor: default; opacity: 0.55; }
${e("button--primary")} { border-color: var(--accent); color: var(--fg-0); background: var(--bg-3); }
${e("button--danger")} { border-color: var(--tx); color: var(--tx); }
${e("button--small")} { min-height: 28px; padding: 2px 8px; font-size: 12px; }
${e("power")} { width: 100%; min-height: 36px; font-weight: 600; }
${e("power")}[aria-pressed="true"] { border-color: var(--accent); background: var(--bg-inset); color: var(--accent-bright); }

${e("meter")} { font-family: var(--font-mono); color: var(--fg-2); font-size: 11px; display: flex; flex-wrap: wrap; gap: 2px 10px; }
${e("meter-main")} { font-size: 16px; color: var(--fg-0); font-variant-numeric: tabular-nums; }

${e("dimmer")} { display: grid; grid-template-columns: auto 1fr auto; align-items: center; gap: 6px; }
${e("dimmer")} input[type="range"] { width: 100%; min-width: 0; accent-color: var(--accent); }
${e("dim-value")} { font-family: var(--font-mono); font-size: 12px; color: var(--fg-1); text-align: center; }

${e("notice")} { border-radius: var(--r-sm); padding: 8px 10px; background: var(--bg-inset); color: var(--fg-2); margin: 0 0 8px; border-left: 3px solid var(--line-strong); }
${e("notice--warn")} { border-left-color: var(--amber); color: var(--fg-1); }
${e("notice--error")} { border-left-color: var(--tx); color: var(--fg-1); }
${e("notice--ok")} { border-left-color: var(--ok); color: var(--fg-1); }
${e("badge")} { display: inline-block; font-size: 10px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; padding: 1px 6px; border-radius: var(--r-xs); border: 1px solid var(--amber); color: var(--amber); }
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
${e("section-title")} { font-size: 12px; font-weight: 600; color: var(--fg-2); text-transform: uppercase; letter-spacing: 0.05em; margin: 12px 0 6px; }
${e("sr")} { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }

@media (prefers-reduced-motion: no-preference) {
  ${e("tile")}, ${e("button")} { transition: background var(--dur-fast) var(--ease-out), border-color var(--dur-fast) var(--ease-out); }
}
`;
