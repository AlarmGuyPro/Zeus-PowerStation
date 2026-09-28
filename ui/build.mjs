// SPDX-License-Identifier: GPL-2.0-or-later
// Bundles the PowerStation panels as one ESM module. React is provided by the
// Zeus host, so `react` and `react/jsx-runtime` stay external.
import { build } from "esbuild";

const preview = process.argv.includes("--preview");

await build({
  entryPoints: ["src/index.tsx"],
  bundle: true,
  format: "esm",
  platform: "browser",
  target: "es2022",
  jsx: "automatic",
  external: ["react", "react/jsx-runtime"],
  outfile: "../src/PowerStation/ui/powerstation.js",
  legalComments: "inline",
  minify: false,
  sourcemap: false,
  banner: { js: "// SPDX-License-Identifier: GPL-2.0-or-later\n// PowerStation for Zeus (c) KQ4WLR. Source: https://github.com/AlarmGuyPro/zeus-powerstation" },
  logLevel: "info",
});

if (preview) {
  // Local-only harness with a mocked backend, used to check states and take
  // review screenshots. Never packaged.
  await build({
    entryPoints: ["preview/preview.tsx"],
    bundle: true,
    format: "esm",
    platform: "browser",
    target: "es2022",
    jsx: "automatic",
    outfile: "preview/dist/preview.js",
    logLevel: "info",
  });
}
