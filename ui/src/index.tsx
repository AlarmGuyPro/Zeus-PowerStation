// SPDX-License-Identifier: GPL-2.0-or-later
// Zeus loads this module and calls the default export with the public
// plugin API. Panel ids must match ui.panels[].id in plugin.json.
import { createClient, type ZeusPluginApi } from "./api";
import { ControlsPanel } from "./ControlsPanel";
import { DevicesPanel } from "./DevicesPanel";

export default function register(api: ZeusPluginApi) {
  const client = createClient(api);
  api.registerPanel({ id: "powerstation-controls", component: () => <ControlsPanel client={client} /> });
  api.registerPanel({ id: "powerstation-devices", component: () => <DevicesPanel client={client} /> });
}
