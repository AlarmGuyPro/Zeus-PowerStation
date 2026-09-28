// SPDX-License-Identifier: GPL-2.0-or-later
// The PowerStation panel: one panel with Status and Setup tabs sharing a
// single status poll. Both manifest panels render this; they differ only in
// which tab opens first.
import { useEffect, useId, useRef, useState, type KeyboardEvent } from "react";
import type { PowerStationClient } from "./api";
import { StatusView } from "./ControlsPanel";
import { SetupView } from "./DevicesPanel";
import { PanelRoot, fmt, useStatus } from "./shared";
import { c } from "./styles";

type Tab = "status" | "setup";
const TABS: { id: Tab; label: string }[] = [
  { id: "status", label: "Status" },
  { id: "setup", label: "Setup" },
];

export function PowerStationPanel({ client, initialTab }: { client: PowerStationClient; initialTab: Tab }) {
  const ids = useId();
  const status = useStatus(client);
  const { data } = status;
  const [tab, setTab] = useState<Tab>(initialTab);
  const chosen = useRef(false);
  const tabRefs = useRef<Record<Tab, HTMLButtonElement | null>>({ status: null, setup: null });

  // First run: with no devices yet, open on Setup unless the operator already picked a tab.
  useEffect(() => {
    if (!chosen.current && data && data.devices.length === 0) setTab("setup");
  }, [data]);

  const select = (next: Tab, focus = false) => {
    chosen.current = true;
    setTab(next);
    if (focus) tabRefs.current[next]?.focus();
  };

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== "ArrowRight" && e.key !== "ArrowLeft" && e.key !== "Home" && e.key !== "End") return;
    e.preventDefault();
    const i = TABS.findIndex((t) => t.id === tab);
    const next =
      e.key === "Home" ? 0 : e.key === "End" ? TABS.length - 1 : (i + (e.key === "ArrowRight" ? 1 : -1) + TABS.length) % TABS.length;
    select(TABS[next].id, true);
  };

  const online = data?.devices.flatMap((d) => (d.status.health === "Online" ? d.status.channels : [])) ?? [];
  const metered = online.some((ch) => ch.metered);
  const totalW = online.reduce((sum, ch) => sum + (ch.powerW ?? 0), 0);
  const attention = data?.devices.filter((d) => d.status.health !== "Online" && d.status.health !== "Pending").length ?? 0;

  return (
    <PanelRoot label="PowerStation">
      <div className={c("header")}>
        <div className={c("tabs")} role="tablist" aria-label="PowerStation views" onKeyDown={onKeyDown}>
          {TABS.map((t) => (
            <button
              key={t.id}
              ref={(el) => {
                tabRefs.current[t.id] = el;
              }}
              type="button"
              role="tab"
              id={`${ids}-tab-${t.id}`}
              aria-selected={tab === t.id}
              aria-controls={`${ids}-panel`}
              tabIndex={tab === t.id ? 0 : -1}
              className={c("tab")}
              onClick={() => select(t.id)}
            >
              {t.label}
            </button>
          ))}
        </div>
        {data && data.devices.length > 0 && (
          <span className={c("summary")}>
            {data.devices.length} device{data.devices.length === 1 ? "" : "s"}
            {metered ? ` · ${fmt.watts(totalW)}` : ""}
            {attention > 0 ? ` · ${attention} need${attention === 1 ? "s" : ""} attention` : ""}
          </span>
        )}
      </div>
      <div role="tabpanel" id={`${ids}-panel`} aria-labelledby={`${ids}-tab-${tab}`}>
        {tab === "status" ? (
          <StatusView client={client} status={status} onGoToSetup={() => select("setup")} />
        ) : (
          <SetupView client={client} status={status} onShowStatus={() => select("status")} />
        )}
      </div>
    </PanelRoot>
  );
}
