// SPDX-License-Identifier: GPL-2.0-or-later
// The PowerStation panel. Everyday controls fill the panel; the gear in the
// top row opens setup (devices, scenes, automations, layout), the same way
// Zeus's own panels put their settings behind a cog.
import { useEffect, useId, useRef, useState, type KeyboardEvent } from "react";
import type { PowerStationClient } from "./api";
import { StatusView } from "./ControlsPanel";
import { SetupView, type SetupSection } from "./DevicesPanel";
import { IdleBanner } from "./automations";
import { PanelRoot, fmt, useStatus } from "./shared";
import { c } from "./styles";

const SECTIONS: { id: SetupSection; label: string }[] = [
  { id: "devices", label: "Devices" },
  { id: "scenes", label: "Scenes" },
  { id: "automations", label: "Automations" },
  { id: "readings", label: "Readings" },
  { id: "layout", label: "Layout" },
];

export function GearIcon() {
  return (
    <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true" focusable="false">
      <path
        fill="currentColor"
        d="M19.14 12.94a7.07 7.07 0 0 0 .06-.94 7.07 7.07 0 0 0-.06-.94l2.03-1.58a.5.5 0 0 0 .12-.64l-1.92-3.32a.5.5 0 0 0-.61-.22l-2.39.96a7.03 7.03 0 0 0-1.63-.94l-.36-2.54a.5.5 0 0 0-.5-.42h-3.84a.5.5 0 0 0-.5.42l-.36 2.54c-.59.24-1.13.55-1.63.94l-2.39-.96a.5.5 0 0 0-.61.22L2.71 8.84a.5.5 0 0 0 .12.64l2.03 1.58a7.4 7.4 0 0 0 0 1.88l-2.03 1.58a.5.5 0 0 0-.12.64l1.92 3.32c.13.22.39.31.61.22l2.39-.96c.5.39 1.04.7 1.63.94l.36 2.54c.04.24.25.42.5.42h3.84c.25 0 .46-.18.5-.42l.36-2.54c.59-.24 1.13-.55 1.63-.94l2.39.96c.22.09.48 0 .61-.22l1.92-3.32a.5.5 0 0 0-.12-.64l-2.03-1.58ZM12 15.5A3.5 3.5 0 1 1 12 8.5a3.5 3.5 0 0 1 0 7Z"
      />
    </svg>
  );
}

export function PowerStationPanel({ client, initialSetup = false }: { client: PowerStationClient; initialSetup?: boolean }) {
  const ids = useId();
  const status = useStatus(client);
  const { data } = status;
  const [setup, setSetup] = useState(initialSetup);
  const [section, setSection] = useState<SetupSection>("devices");
  const [arranging, setArranging] = useState(false);
  const chosen = useRef(false);
  const tabRefs = useRef<Partial<Record<SetupSection, HTMLButtonElement | null>>>({});

  // First run: with no devices yet, open setup unless the operator already chose a view.
  useEffect(() => {
    if (!chosen.current && data && data.devices.length === 0) setSetup(true);
  }, [data]);

  const toggleSetup = (open: boolean) => {
    chosen.current = true;
    setSetup(open);
    if (open) setArranging(false);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== "ArrowRight" && e.key !== "ArrowLeft" && e.key !== "Home" && e.key !== "End") return;
    e.preventDefault();
    const i = SECTIONS.findIndex((t) => t.id === section);
    const n = SECTIONS.length;
    const next = e.key === "Home" ? 0 : e.key === "End" ? n - 1 : (i + (e.key === "ArrowRight" ? 1 : -1) + n) % n;
    setSection(SECTIONS[next].id);
    tabRefs.current[SECTIONS[next].id]?.focus();
  };

  const online = data?.devices.flatMap((d) => (d.status.health === "Online" ? d.status.channels : [])) ?? [];
  const metered = online.some((ch) => ch.metered && ch.kind !== "Meter");
  const totalW = online.reduce((sum, ch) => sum + (ch.kind === "Meter" ? 0 : ch.powerW ?? 0), 0);
  const attention = data?.devices.filter((d) => d.status.health !== "Online" && d.status.health !== "Pending").length ?? 0;
  const auto = data?.automation;
  const outOfRange = online.filter((ch) => ch.alerts?.some((a) => a.kind.startsWith("current"))).length;
  const mainsAlert = data?.readings?.mainsNow?.alert;

  return (
    <PanelRoot label="PowerStation">
      <div className={c("header")}>
        {setup ? (
          <h2 className={c("title")}>Setup</h2>
        ) : (
          <span className={c("summary")}>
            {data && data.devices.length > 0 && (
              <>
                {data.devices.length} device{data.devices.length === 1 ? "" : "s"}
                {metered ? ` · ${fmt.watts(totalW)}` : ""}
                {attention > 0 ? ` · ${attention} need${attention === 1 ? "s" : ""} attention` : ""}
                {mainsAlert && (
                  <span className={c("summary-warn")}>
                    {" · "}mains {mainsAlert.kind === "voltageLow" ? "low" : "high"}
                  </span>
                )}
                {outOfRange > 0 && (
                  <span className={c("summary-warn")}>
                    {" · "}
                    {outOfRange} output{outOfRange === 1 ? "" : "s"} over or under current
                  </span>
                )}
              </>
            )}
            {auto?.paused && <span className={c("badge", "badge--muted")} style={{ marginLeft: 8 }}>Automations paused</span>}
          </span>
        )}
        <span className={c("header-tools")}>
          {setup && (
            <button type="button" className={c("button", "button--small")} onClick={() => toggleSetup(false)}>
              Done
            </button>
          )}
          <button
            type="button"
            className={c("gear")}
            aria-pressed={setup}
            aria-label={setup ? "Close PowerStation setup" : "PowerStation setup"}
            title={setup ? "Close setup" : "Setup"}
            onClick={() => toggleSetup(!setup)}
          >
            <GearIcon />
          </button>
        </span>
      </div>

      {setup ? (
        <>
          <div className={c("tabs", "tabs--setup")} role="tablist" aria-label="Setup sections" onKeyDown={onKeyDown}>
            {SECTIONS.map((t) => (
              <button
                key={t.id}
                ref={(el) => {
                  tabRefs.current[t.id] = el;
                }}
                type="button"
                role="tab"
                id={`${ids}-tab-${t.id}`}
                aria-selected={section === t.id}
                aria-controls={`${ids}-panel`}
                tabIndex={section === t.id ? 0 : -1}
                className={c("tab")}
                onClick={() => setSection(t.id)}
              >
                {t.label}
              </button>
            ))}
          </div>
          <div role="tabpanel" id={`${ids}-panel`} aria-labelledby={`${ids}-tab-${section}`}>
            <SetupView
              client={client}
              status={status}
              section={section}
              onShowStatus={() => toggleSetup(false)}
              onArrange={() => {
                toggleSetup(false);
                setArranging(true);
              }}
            />
          </div>
        </>
      ) : (
        <>
          {auto && <IdleBanner client={client} status={status} />}
          <StatusView
            client={client}
            status={status}
            arranging={arranging}
            onDoneArranging={() => setArranging(false)}
            onGoToSetup={() => toggleSetup(true)}
          />
        </>
      )}
    </PanelRoot>
  );
}
