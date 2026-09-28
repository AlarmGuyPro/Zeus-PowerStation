// SPDX-License-Identifier: GPL-2.0-or-later
// Regenerates docs/screenshots/*.png from the mockup. Build it first:
//   cd ui && npm run mockup
import path from "node:path";
import { click, choose, launch, openMockup, repo, serve, wait } from "./lib.mjs";

const out = path.join(repo, "docs/screenshots");
const { base, close } = serve();
const browser = await launch();
const errors = [];
const frame = async (pg, name) => { await (await pg.$(".mk-frame")).screenshot({ path: `${out}/${name}.png` }); errors.push(...pg.errors); };

let pg = await openMockup(browser, base);
await frame(pg, "status-dark");
await click(pg, "button", "Sag to 108.6 V"); await click(pg, "button", "Amplifier draws 13.9 A");
await click(pg, "button", "Skip to 1 min before idle");
await wait(6500);
await frame(pg, "status-alerts-idle-dark");
await (await pg.$(".mk-frame--small")).screenshot({ path: `${out}/idle-pill-dark.png` });
await click(pg, "button", "PowerStation setup"); await wait(300);
await click(pg, "[role=tab]", "Readings"); await wait(600);
await frame(pg, "setup-readings-dark");
await click(pg, "[role=tab]", "Automations"); await wait(400);
await frame(pg, "setup-automations-dark");
await click(pg, "button", "New rule"); await wait(300);
await choose(pg, "select[id$='-trig']", "tx"); await wait(300);
await pg.evaluate(() => document.querySelector("select[id$='-trig']").scrollIntoView({ block: "start" }));
await frame(pg, "rule-editor-tx-dark");
await click(pg, "[role=tab]", "Scenes"); await wait(300);
await pg.evaluate(() => {
  const head = [...document.querySelectorAll("h4")].find((h) => h.textContent === "Evening lights");
  [...head.closest("div").parentElement.querySelectorAll("button")].find((x) => x.textContent === "Edit").click();
});
await wait(300);
await pg.evaluate(() => [...document.querySelectorAll("label")].find((l) => l.textContent === "Accent strip")?.scrollIntoView({ block: "center" }));
await frame(pg, "scene-editor-colour-dark");
await click(pg, "[role=tab]", "Devices"); await wait(300);
await click(pg, "button", "Scan now"); await wait(5500);
await frame(pg, "setup-devices-scan-dark");
await click(pg, "[role=tab]", "Layout"); await wait(300);
await frame(pg, "setup-layout-dark");
await click(pg, "[role=tab]", "Debug"); await wait(600);
await frame(pg, "setup-debug-dark");

pg = await openMockup(browser, base, { theme: "light" });
await frame(pg, "status-light");
await pg.evaluate(() => [...document.querySelectorAll(".mk-frame button")].find((x) => x.textContent.trim() === "Apply").focus());
await pg.keyboard.press("Tab");
await frame(pg, "keyboard-focus-light");
pg = await openMockup(browser, base, { width: 430, frameHeight: 1300 });
await frame(pg, "status-narrow-dark");
pg = await openMockup(browser, base, { width: 640, scale: 2, frameHeight: 900 });
await frame(pg, "status-200pct-dark");
pg = await openMockup(browser, base, { theme: "light", width: 900, frameHeight: 500 });
await choose(pg, "#mk-scenario", "down"); await wait(1500);
await frame(pg, "backend-error-light");
pg = await openMockup(browser, base, { width: 900, frameHeight: 800 });
await choose(pg, "#mk-scenario", "empty"); await wait(1500);
await frame(pg, "first-run-dark");

await browser.close();
close();
console.log(errors.length ? errors : `screenshots written to ${path.relative(process.cwd(), out)}`);
