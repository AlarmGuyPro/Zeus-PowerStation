// SPDX-License-Identifier: GPL-2.0-or-later
// Zeus loads this module and calls the default export with the public
// plugin API. Panel ids must match ui.panels[].id in plugin.json.
import { createClient, type ZeusPluginApi } from "./api";
import { PowerStationPanel } from "./PowerStationPanel";
import { IdlePillPanel } from "./automations";

export default function register(api: ZeusPluginApi) {
  const client = createClient(api);
  // The main panel; setup is behind the gear in its top row.
  api.registerPanel({ id: "powerstation-controls", component: () => <PowerStationPanel client={client} /> });
  // A small panel for the idle countdown, to dock somewhere always visible.
  api.registerPanel({ id: "powerstation-idle", component: () => <IdlePillPanel client={client} /> });
}
