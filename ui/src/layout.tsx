// SPDX-License-Identifier: GPL-2.0-or-later
// Status-tab grid: the operator picks a column count and arranges device
// cards into it. The arrangement is saved in PowerStation, so it survives
// Zeus restarts.
import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import type { DeviceView, Layout, PowerStationClient } from "./api";
import { Notice } from "./shared";
import { c } from "./styles";

export const COLUMN_CHOICES = [0, 1, 2, 3, 4] as const;
/** Narrowest a column may get before the grid drops a column. */
const MIN_COLUMN_PX = 210;

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

/** Re-deals a layout into a different number of columns, keeping reading order. */
function reflow(order: string[][], columns: number): string[][] {
  const flat = flatten(order);
  const n = Math.max(1, columns || 1);
  const perCol = Math.ceil(flat.length / n);
  return Array.from({ length: n }, (_, i) => flat.slice(i * perCol, (i + 1) * perCol));
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

export function LayoutToolbar({
  layout,
  arranging,
  onColumns,
  onArrange,
}: {
  layout: Layout;
  arranging: boolean;
  onColumns: (n: number) => void;
  onArrange: (on: boolean) => void;
}) {
  return (
    <div className={c("toolbar")}>
      <div className={c("seg")} role="radiogroup" aria-label="Columns">
        <span className={c("seg-label")} aria-hidden="true">
          Columns
        </span>
        {COLUMN_CHOICES.map((n) => (
          <button
            key={n}
            type="button"
            role="radio"
            aria-checked={layout.columns === n}
            className={c("seg-btn")}
            onClick={() => onColumns(n)}
            title={n === 0 ? "Fit as many as the panel allows" : `${n} column${n === 1 ? "" : "s"}`}
          >
            {n === 0 ? "Auto" : n}
          </button>
        ))}
      </div>
      <button
        type="button"
        className={c("button", "button--small", arranging && "button--primary")}
        aria-pressed={arranging}
        onClick={() => onArrange(!arranging)}
      >
        {arranging ? "Done arranging" : "Arrange"}
      </button>
    </div>
  );
}

type Move = "up" | "down" | "left" | "right";

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
  const [dropAt, setDropAt] = useState<{ col: number; before: string | null } | null>(null);
  const byId = new Map(devices.map((d) => [d.deviceId, d]));

  const auto = layout.columns === 0;
  const fits = width > 0 ? Math.max(1, Math.floor((width + 10) / MIN_COLUMN_PX)) : 4;
  const order = normalize(auto ? { columns: 1, order: [flatten(layout.order)] } : layout, devices);
  const squeezed = !auto && fits < layout.columns;

  const commit = (next: string[][]) => onSave({ columns: layout.columns, order: next });

  function move(id: string, dir: Move) {
    const next = order.map((col) => [...col]);
    const ci = next.findIndex((col) => col.includes(id));
    const ri = next[ci].indexOf(id);
    if (dir === "up" && ri > 0) [next[ci][ri - 1], next[ci][ri]] = [next[ci][ri], next[ci][ri - 1]];
    else if (dir === "down" && ri < next[ci].length - 1) [next[ci][ri + 1], next[ci][ri]] = [next[ci][ri], next[ci][ri + 1]];
    else if ((dir === "left" && ci > 0) || (dir === "right" && ci < next.length - 1)) {
      const to = ci + (dir === "left" ? -1 : 1);
      next[ci].splice(ri, 1);
      next[to].splice(Math.min(ri, next[to].length), 0, id);
    } else return;
    commit(next);
  }

  function drop(col: number, before: string | null) {
    if (!dragId) return;
    const next = order.map((c2) => c2.filter((x) => x !== dragId));
    const at = before ? next[col].indexOf(before) : -1;
    next[col].splice(at < 0 ? next[col].length : at, 0, dragId);
    setDragId(null);
    setDropAt(null);
    commit(next);
  }

  const controls = (id: string, ci: number, ri: number, colCount: number, colLen: number) => {
    if (!arranging) return null;
    const multi = !auto && !squeezed;
    return (
      <span className={c("move")} role="group" aria-label={`Move ${byId.get(id)?.displayName ?? "device"}`}>
        {multi && (
          <button type="button" className={c("move-btn")} aria-label="Move to previous column" disabled={ci === 0} onClick={() => move(id, "left")}>
            ←
          </button>
        )}
        <button type="button" className={c("move-btn")} aria-label="Move up" disabled={ri === 0} onClick={() => move(id, "up")}>
          ↑
        </button>
        <button type="button" className={c("move-btn")} aria-label="Move down" disabled={ri === colLen - 1} onClick={() => move(id, "down")}>
          ↓
        </button>
        {multi && (
          <button type="button" className={c("move-btn")} aria-label="Move to next column" disabled={ci === colCount - 1} onClick={() => move(id, "right")}>
            →
          </button>
        )}
      </span>
    );
  };

  const cardShell = (id: string, ci: number, ri: number, colCount: number, colLen: number) => {
    const d = byId.get(id);
    if (!d) return null;
    const target = dropAt && dropAt.col === ci && dropAt.before === id;
    return (
      <div
        key={id}
        className={c("cell", arranging && "cell--arranging", dragId === id && "cell--dragging", target && "cell--drop-before")}
        draggable={arranging && !squeezed}
        onDragStart={(e) => {
          setDragId(id);
          e.dataTransfer.effectAllowed = "move";
          e.dataTransfer.setData("text/plain", id);
        }}
        onDragEnd={() => {
          setDragId(null);
          setDropAt(null);
        }}
        onDragOver={(e) => {
          if (!dragId || dragId === id) return;
          e.preventDefault();
          e.stopPropagation();
          setDropAt({ col: ci, before: id });
        }}
        onDrop={(e) => {
          e.preventDefault();
          e.stopPropagation();
          drop(ci, id);
        }}
      >
        {renderCard(d, controls(id, ci, ri, colCount, colLen))}
      </div>
    );
  };

  // Auto, or a panel too narrow for the chosen count: one flowing grid.
  if (auto || squeezed) {
    const flat = order.flat();
    return (
      <div ref={ref}>
        {squeezed && arranging && (
          <Notice>
            The panel is too narrow for {layout.columns} columns, so cards are shown in fewer. Widen the panel to move cards
            between columns.
          </Notice>
        )}
        <div className={c("flow")}>{flat.map((id, i) => cardShell(id, 0, i, 1, flat.length))}</div>
      </div>
    );
  }

  return (
    <div ref={ref} className={c("columns")} style={{ gridTemplateColumns: `repeat(${order.length}, minmax(0, 1fr))` }}>
      {order.map((col, ci) => (
        <div
          key={ci}
          className={c("column", arranging && "column--arranging", dropAt?.col === ci && dropAt.before === null && "column--drop")}
          aria-label={`Column ${ci + 1}`}
          role="group"
          onDragOver={(e) => {
            if (!dragId) return;
            e.preventDefault();
            setDropAt({ col: ci, before: null });
          }}
          onDrop={(e) => {
            e.preventDefault();
            drop(ci, null);
          }}
        >
          {col.map((id, ri) => cardShell(id, ci, ri, order.length, col.length))}
          {arranging && col.length === 0 && <div className={c("column-empty")}>Drop a device here</div>}
        </div>
      ))}
    </div>
  );
}

export { reflow };
