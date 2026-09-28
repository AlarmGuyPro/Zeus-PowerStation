// SPDX-License-Identifier: GPL-2.0-or-later
// Builds the clickable mockup (ui/preview/mockup.tsx + the real panels against
// a pretend backend) into one standalone HTML page:
//   ui/preview/dist/powerstation-mockup.html   (React from cdnjs)
//   ui/preview/dist/local.html                 (React from ./r and ./rd, for offline screenshots)
// Usage: cd ui && node preview/build-mockup.mjs
import { build } from "esbuild";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const shims = {
  react: `const R=window.React;export default R;export const {useState,useEffect,useLayoutEffect,useRef,useCallback,useId,useMemo,Fragment,createElement}=R;`,
  "react/jsx-runtime": `const R=window.React;export const Fragment=R.Fragment;export function jsx(t,p,k){return R.createElement(t,k===undefined?p:{...p,key:k});}export const jsxs=jsx;`,
  "react-dom/client": `export const createRoot=(...a)=>window.ReactDOM.createRoot(...a);`,
};
const result = await build({
  entryPoints: [path.join(here, "mockup.tsx")],
  bundle: true, format: "iife", target: "es2020", jsx: "automatic", write: false, minify: true, legalComments: "none",
  plugins: [{
    name: "react-globals",
    setup(b) {
      b.onResolve({ filter: /^(react|react\/jsx-runtime|react-dom\/client)$/ }, (a) => ({ path: a.path, namespace: "g" }));
      b.onLoad({ filter: /.*/, namespace: "g" }, (a) => ({ contents: shims[a.path], loader: "js" }));
    },
  }],
});
const js = result.outputFiles[0].text;
const head = `<meta charset="utf-8">\n` + fs.readFileSync(path.join(here, "mockup-head.html"), "utf8");
const dist = path.join(here, "dist");
fs.mkdirSync(dist, { recursive: true });
fs.writeFileSync(path.join(dist, "powerstation-mockup.html"), `${head}\n<script>${js}</script>\n`);
const local = head
  .replace("https://cdnjs.cloudflare.com/ajax/libs/react/18.3.1/umd/react.production.min.js", "r/umd/react.production.min.js")
  .replace("https://cdnjs.cloudflare.com/ajax/libs/react-dom/18.3.1/umd/react-dom.production.min.js", "rd/umd/react-dom.production.min.js");
fs.writeFileSync(path.join(dist, "local.html"), `${local}\n<script>${js}</script>\n`);
console.log(`mockup: ${path.relative(process.cwd(), dist)}/powerstation-mockup.html (${(js.length / 1024).toFixed(0)} KB)`);
