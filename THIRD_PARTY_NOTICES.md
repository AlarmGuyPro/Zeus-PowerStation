# Third-party notices

PowerStation is licensed under GPL-2.0-or-later (see `LICENSE`).

## Bundled in the package

Nothing. The package contains only PowerStation's own assembly, its
browser module, `plugin.json`, this file, the README and the license.

## Used at build or run time, not bundled

- **Zeus plugin contracts** (`sdk/Zeussdr.Zeus.Plugins.Contracts`),
  GPL-2.0-or-later, © Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR)
  and contributors. Vendored unchanged from
  https://github.com/Zeus-SDR/zeus-community-features for compilation; the
  Zeus host supplies the assembly at run time.
- **React** (MIT), provided by the Zeus host. The UI module imports it and
  does not bundle it.
- **esbuild** (MIT) and **TypeScript** (Apache-2.0), build tools only.

## API documentation

Device communication follows Shelly's public API documentation
(https://shelly-api-docs.shelly.cloud/). No Shelly source code is included.
