// SPDX-License-Identifier: GPL-2.0-or-later
// Shared helpers: serve the built mockup (with React 18 UMD and IBM Plex
// from node_modules) and drive it with a headless browser.
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
export const repo = path.resolve(here, "../..");
const dist = path.join(repo, "ui/preview/dist");
const nm = path.join(here, "node_modules");
const routes = [
  ["/r/", path.join(nm, "react/")],
  ["/rd/", path.join(nm, "react-dom/")],
  ["/fonts/sans/", path.join(nm, "@fontsource/ibm-plex-sans/files/")],
  ["/fonts/mono/", path.join(nm, "@fontsource/ibm-plex-mono/files/")],
  ["/promo/", path.join(repo, "docs/promo/")],
  ["/", dist + "/"],
];
const types = { ".js": "text/javascript", ".css": "text/css", ".woff2": "font/woff2", ".png": "image/png", ".html": "text/html" };

export const fontCss = ["sans", "mono"].flatMap((kind) =>
  (kind === "sans" ? [400, 500, 600, 700] : [400, 500]).map((w) =>
    `@font-face{font-family:"IBM Plex ${kind === "sans" ? "Sans" : "Mono"}";font-weight:${w};src:url(/fonts/${kind}/ibm-plex-${kind}-latin-${w}-normal.woff2) format("woff2")}`,
  )).join("\n");

export function serve() {
  const srv = http.createServer((q, s) => {
    const url = decodeURIComponent(new URL(q.url, "http://x").pathname);
    if (url === "/fonts.css") { s.writeHead(200, { "content-type": "text/css" }); return s.end(fontCss); }
    const [prefix, dir] = routes.find(([p]) => url.startsWith(p));
    const file = path.join(dir, url.slice(prefix.length));
    if (!file.startsWith(dir) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { s.writeHead(404); return s.end(); }
    s.writeHead(200, { "content-type": types[path.extname(file)] ?? "application/octet-stream" });
    fs.createReadStream(file).pipe(s);
  }).listen(0);
  return { base: `http://127.0.0.1:${srv.address().port}`, close: () => srv.close() };
}

/** Uses puppeteer's own Chrome, or CHROME_PATH (with @sparticuz/chromium's flags if that's installed). */
export async function launch() {
  if (process.env.CHROME_PATH) {
    const { default: core } = await import("puppeteer-core");
    let args = [];
    try { args = (await import("@sparticuz/chromium")).default.args; } catch { /* plain Chrome */ }
    return core.launch({ executablePath: process.env.CHROME_PATH, args });
  }
  const { default: puppeteer } = await import("puppeteer");
  return puppeteer.launch();
}

export async function openMockup(browser, base, { theme = "dark", width = 1280, height = 1400, scale = 1, frameHeight = 1150 } = {}) {
  const pg = await browser.newPage();
  const errors = [];
  pg.on("pageerror", (e) => errors.push(e.message));
  await pg.emulateMediaFeatures([{ name: "prefers-color-scheme", value: theme }]);
  await pg.setViewport({ width, height, deviceScaleFactor: scale });
  await pg.goto(`${base}/local.html`);
  await pg.addStyleTag({ url: `${base}/fonts.css` });
  await pg.addStyleTag({ content: `.mk-frame:not(.mk-frame--small){height:${frameHeight}px}` });
  await pg.evaluateHandle("document.fonts.ready");
  await wait(1300);
  pg.errors = errors;
  return pg;
}

export const wait = (ms) => new Promise((r) => setTimeout(r, ms));

export const click = (pg, sel, text) =>
  pg.evaluate((sel, text) => {
    const el = [...document.querySelectorAll(sel)].find((x) => x.textContent.trim() === text || x.getAttribute("aria-label") === text);
    if (!el) throw new Error(`missing ${text}`);
    el.click();
  }, sel, text);

export const choose = (pg, selector, value) =>
  pg.evaluate((selector, value) => {
    const s = document.querySelector(selector);
    Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, "value").set.call(s, value);
    s.dispatchEvent(new Event("change", { bubbles: true }));
  }, selector, value);
