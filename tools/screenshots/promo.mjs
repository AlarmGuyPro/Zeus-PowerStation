// SPDX-License-Identifier: GPL-2.0-or-later
// Renders docs/promo/powerstation-promo.png (1600×900, for posts and chats):
// captures fresh crops from the mockup, then renders docs/promo/promo.html.
// Build the mockup first: cd ui && npm run mockup
import path from "node:path";
import { click, launch, openMockup, repo, serve, wait } from "./lib.mjs";

const promo = path.join(repo, "docs/promo");
const { base, close } = serve();
const browser = await launch();

const pg = await openMockup(browser, base, { width: 1060, height: 1500, scale: 2, frameHeight: 1300 });
await click(pg, "button", "Key TX");
await click(pg, "button", "Skip to 1 min before idle");
await wait(2000);
const box = await (await pg.$(".mk-frame")).boundingBox();
await pg.screenshot({ path: `${promo}/status.png`, clip: { x: box.x, y: box.y + 30, width: box.width, height: 880 } });
const strip = await pg.evaluateHandle(() => [...document.querySelectorAll(".mk-frame article")].find((a) => a.textContent.includes("Shack accent strip")));
await strip.screenshot({ path: `${promo}/colour.png` });
await click(pg, "button", "TX (click to unkey)");
await click(pg, "button", "PowerStation setup"); await wait(300);
await click(pg, "[role=tab]", "Automations"); await wait(600);
await pg.evaluate(() => document.querySelector(".mk-frame [class$='__rules']").scrollIntoView({ block: "start" }));
await wait(200);
await (await pg.$(".mk-frame [class$='__rules']")).screenshot({ path: `${promo}/rules.png` });

const card = await browser.newPage();
await card.setViewport({ width: 1600, height: 900, deviceScaleFactor: 1.5 });
await card.goto(`${base}/promo/promo.html`);
await card.evaluateHandle("document.fonts.ready");
await wait(800);
await card.screenshot({ path: `${promo}/powerstation-promo.png` });

await browser.close();
close();
console.log(pg.errors.length ? pg.errors : "docs/promo/powerstation-promo.png written");
