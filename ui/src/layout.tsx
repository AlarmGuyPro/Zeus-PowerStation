// SPDX-License-Identifier: GPL-2.0-or-later
// Status-tab grid: the operator picks a column count and arranges device
// cards into it. The arrangement is saved in PowerStation, so it survives
// Zeus restarts.
import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import type { DeviceView, Layout, PowerStationClient } from "./api";
import { c } from "./styles";

export const COLUMN_CHOICES = [0, 1, 2, 3, 4] as const;
/** Narrowest a column may get before the grid drops a column. */
/** Auto uses roomy cards; a column count the operator picks is kept down to narrow cards. */
const AUTO_COLUMN_PX = 220;
const MIN_COLUMN_PX = 150;
const GAP_PX = 10;

/** How many columns the grid actually shows for a chosen count at a given width. */
export function columnsFor(chosen: number, width: number, devices: number) {
  const fit = (min: number) => (width > 0 ? Math.max(1, Math.floor((width + GAP_PX) / (min + GAP_PX))) : chosen || 3);
  return chosen === 0 ? Math.max(1, Math.min(fit(AUTO_COLUMN_PX), devices)) : Math.min(chosen, fit(MIN_COLUMN_PX));
}

/**
 * Fits saved order to the devices that exist now: unknown IDs are dropped
 * and new devices go to the shortest column.
 */
export function normalize(layout: Layout, devices: DeviceView[]): string[][] {
  const n = Math.max(1, layout.columns || 1);
  const known = new Set(devices.map((d) => d.deviceId));
  const seen = new Set<string>();
  const cols: string[][] = Array.from({ length: n }, () => []);
  layout.order.forEach((col, i) => {
    for (const id of col) {
      if (!known.has(id) || seen.has(id)) continue;
      seen.add(id);
      cols[Math.min(i, n - 1)].push(id);
    }
  });
  for (const d of devices) {
    if (seen.has(d.deviceId)) continue;
    const shortest = cols.reduce((best, col, i) => (col.length < cols[best].length ? i : best), 0);
    cols[shortest].push(d.deviceId);
  }
  return cols;
}

/** Column-major reading order. */
const flatten = (order: string[][]) => order.flat();

export function useLayout(client: PowerStationClient, saved: Layout | null | undefined) {
  const [layout, setLayout] = useState<Layout>(saved ?? { columns: 0, order: [] });
  const [error, setError] = useState<string | null>(null);
  const dirty = useRef(false);

  // Adopt the saved layout until the operator changes something here.
  useEffect(() => {
    if (saved && !dirty.current) setLayout(saved);
  }, [saved]);

  const save = useCallback(
    (next: Layout) => {
      dirty.current = true;
      setLayout(next);
      setError(null);
      client
        .saveLayout(next)
        .then(() => {
          dirty.current = false;
        })
        .catch(() => setError("Couldn't save the layout. It will reset when the panel reloads."));
    },
    [client],
  );
  return { layout, save, error };
}

/** Measures the panel so a narrow panel shows fewer columns than chosen. */
function useWidth() {
  const ref = useRef<HTMLDivElement | null>(null);
  const [width, setWidth] = useState(0);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    setWidth(el.clientWidth);
    const ro = new ResizeObserver((entries) => setWidth(entries[0].contentRect.width));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  return { ref, width };
}

type Move = "up" | "down" | "left" | "right";

/**
 * Cards snap to a grid of equal cells that fill the panel's width. Order is
 * left to right, top to bottom. Every card in a row is the same height.
 * Auto picks as many columns as fit (never more than there are devices);
 * a number fixes the column count, reduced only when the panel is too
 * narrow to hold it.
 */
export function DeviceGrid({
  devices,
  layout,
  arranging,
  onSave,
  renderCard,
}: {
  devices: DeviceView[];
  layout: Layout;
  arranging: boolean;
  onSave: (l: Layout) => void;
  renderCard: (device: DeviceView, controls: ReactNode) => ReactNode;
}) {
  const { ref, width } = useWidth();
  const [dragId, setDragId] = useState<string | null>(null);
  const [dropBefore, setDropBefore] = useState<string | null | undefined>(undefined);
  const byId = new Map(devices.map((d) => [d.deviceId, d]));

  const flat = normalize({ columns: 1, order: [flatten(layout.order)] }, devices)[0];
  const cols = columnsFor(layout.columns, width, flat.length);
  const squeezed = layout.columns > 0 && cols < layout.columns;

  const commit = (next: string[]) => onSave({ columns: layout.columns, order: [next] });

  function move(id: string, dir: Move) {
    const i = flat.indexOf(id);
    const j = dir === "left" ? i - 1 : dir === "right" ? i + 1 : dir === "up" ? i - cols : i + cols;
    if (j < 0 || j >= flat.length) return;
    const next = [...flat];
    [next[i], next[j]] = [next[j], next[i]];
    commit(next);
  }

  function drop(before: string | null) {
    if (!dragId) return;
    const next = flat.filter((x) => x !== dragId);
    const at = before ? next.indexOf(before) : -1;
    next.splice(at < 0 ? next.length : at, 0, dragId);
    setDragId(null);
    setDropBefore(undefined);
    commit(next);
  }

  const controls = (id: string, i: number) => {
    if (!arranging) return null;
    const name = byId.get(id)?.displayName ?? "device";
    return (
      <span className={c("move")} role="group" aria-label={`Move ${name}`}>
        <button type="button" className={c("move-btn")} aria-label="Move left" disabled={i === 0} onClick={() => move(id, "left")}>
          ←
        </button>
        <button type="button" className={c("move-btn")} aria-label="Move up a row" disabled={i - cols < 0} onClick={() => move(id, "up")}>
          ↑
        </button>
        <button type="button" className={c("move-btn")} aria-label="Move down a row" disabled={i + cols >= flat.length} onClick={() => move(id, "down")}>
          ↓
        </button>
        <button type="button" className={c("move-btn")} aria-label="Move right" disabled={i === flat.length - 1} onClick={() => move(id, "right")}>
          →
        </button>
      </span>
    );
  };

  return (
    <div ref={ref}>
      {squeezed && (
        <p className={c("hint")} role="status" style={{ margin: "0 0 8px" }}>
          The panel is only wide enough for {cols} column{cols === 1 ? "" : "s"}. Widen it to see all {layout.columns}.
        </p>
      )}
      <div
        className={c("snap", arranging && "snap--arranging")}
        style={{ gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))` }}
        onDragOver={(e) => {
          if (!dragId) return;
          e.preventDefault();
          setDropBefore(null);
        }}
        onDrop={(e) => {
          e.preventDefault();
          drop(null);
        }}
      >
        {flat.map((id, i) => {
          const d = byId.get(id);
          if (!d) return null;
          return (
            <div
              key={id}
              className={c("cell", arranging && "cell--arranging", dragId === id && "cell--dragging", dropBefore === id && "cell--drop-before")}
              draggable={arranging}
              onDragStart={(e) => {
                setDragId(id);
                e.dataTransfer.effectAllowed = "move";
                e.dataTransfer.setData("text/plain", id);
              }}
              onDragEnd={() => {
                setDragId(null);
                setDropBefore(undefined);
              }}
              onDragOver={(e) => {
                if (!dragId || dragId === id) return;
                e.preventDefault();
                e.stopPropagation();
                setDropBefore(id);
              }}
              onDrop={(e) => {
                e.preventDefault();
                e.stopPropagation();
                drop(id);
              }}
            >
              {renderCard(d, controls(id, i))}
            </div>
          );
        })}
        {arranging && dragId && (
          <div className={c("cell-end", dropBefore === null && "cell-end--active")} aria-hidden="true">
            Drop here to move to the end
          </div>
        )}
      </div>
    </div>
  );
}
