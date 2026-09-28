// SPDX-License-Identifier: GPL-2.0-or-later
// Zeus loads this module and calls the default export with the public
// plugin API. Panel ids must match ui.panels[].id in plugin.json.
import { createClient, type ZeusPluginApi } from "./api";
import { PowerStationPanel } from "./PowerStationPanel";

export default function register(api: ZeusPluginApi) {
  const client = createClient(api);
  // One panel with Status and Setup tabs. The second id is kept so a panel
  // docked from an earlier version keeps working; it opens on Setup.
  api.registerPanel({
    id: "powerstation-controls",
    component: () => <PowerStationPanel client={client} initialTab="status" />,
  });
  api.registerPanel({
    id: "powerstation-devices",
    component: () => <PowerStationPanel client={client} initialTab="setup" />,
  });
}
