// SPDX-License-Identifier: GPL-2.0-or-later
// Colour LED controllers (Shelly Plus RGBW PM in its RGB or RGBW profile):
// the tile on the panel and the colour picker shared by scenes and rules.
//
// The swatch colours below are LED output values sent to the device (data),
// not UI colours; everything around them still uses Zeus theme tokens.
import { useEffect, useId, useRef, useState, type CSSProperties, type ReactNode } from "react";
import type { ChannelKind, ChannelState } from "./api";
import { c } from "./styles";

export type Rgb = [number, number, number];

export const isColor = (kind: ChannelKind) => kind === "Rgb" || kind === "Rgbw";
export const isDimmable = (kind: ChannelKind) => kind === "Light" || isColor(kind);

export interface ColorValue {
  rgb: Rgb;
  /** RGBW only: white channel 0-255. */
  white?: number | null;
}

/** LED-friendly presets. "White" on an RGBW strip uses its own white LEDs. */
export const PRESETS: { name: string; rgb: Rgb; white?: number }[] = [
  { name: "Red", rgb: [255, 0, 0] },
  { name: "Orange", rgb: [255, 70, 0] },
  { name: "Amber", rgb: [255, 140, 0] },
  { name: "Warm", rgb: [255, 150, 60] },
  { name: "Green", rgb: [0, 255, 0] },
  { name: "Cyan", rgb: [0, 220, 255] },
  { name: "Blue", rgb: [0, 0, 255] },
  { name: "Purple", rgb: [140, 0, 255] },
  { name: "Pink", rgb: [255, 0, 110] },
  { name: "White", rgb: [255, 255, 255] },
];

export const toHex = (rgb: Rgb) => `#${rgb.map((v) => Math.round(v).toString(16).padStart(2, "0")).join("")}`;
export const fromHex = (hex: string): Rgb => {
  const m = hex.replace("#", "").match(/^([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i);
  return m ? [parseInt(m[1], 16), parseInt(m[2], 16), parseInt(m[3], 16)] : [255, 255, 255];
};
const css = (rgb: Rgb) => `rgb(${rgb[0]} ${rgb[1]} ${rgb[2]})`;
const same = (a: Rgb, b: Rgb) => a.every((v, i) => Math.abs(v - b[i]) <= 3);

/** What the LEDs look like: the colour mixed with the white channel. */
export function shown(value: ColorValue): Rgb {
  const w = (value.white ?? 0) / 255;
  return value.rgb.map((v) => Math.min(255, v + (255 - v) * w * 0.85)) as Rgb;
}

export function describeColor(value: ColorValue) {
  const p = PRESETS.find((x) => same(x.rgb, value.rgb));
  const name = p ? p.name.toLowerCase() : toHex(value.rgb);
  return value.white ? `${name} + white ${Math.round((value.white / 255) * 100)}%` : name;
}

/** Swatches plus a custom colour, and a white level for RGBW. */
export function ColorPicker({
  value,
  withWhite,
  onChange,
  disabled,
  label,
  compact,
}: {
  value: ColorValue;
  withWhite: boolean;
  onChange: (v: ColorValue) => void;
  disabled?: boolean;
  label: string;
  compact?: boolean;
}) {
  const ids = useId();
  return (
    <div className={c("color-pick", compact && "color-pick--compact")}>
      <div className={c("swatches")} role="group" aria-label={`${label} colour`}>
        {PRESETS.map((p) => {
          const selected = same(p.rgb, value.rgb);
          return (
            <button
              key={p.name}
              type="button"
              className={c("swatch", selected && "swatch--on")}
              style={{ "--swatch": css(p.rgb) } as CSSProperties}
              aria-label={`${label}: ${p.name}`}
              aria-pressed={selected}
              title={p.name}
              disabled={disabled}
              onClick={() => onChange({ rgb: p.rgb, white: value.white })}
            />
          );
        })}
        <label className={c("swatch", "swatch--custom")} title="Pick any colour">
          <span className={c("sr")}>{label}: custom colour</span>
          <input
            type="color"
            value={toHex(value.rgb)}
            disabled={disabled}
            onChange={(e) => onChange({ rgb: fromHex(e.currentTarget.value), white: value.white })}
          />
        </label>
      </div>
      {withWhite && (
        <div className={c("slider-row")}>
          <label htmlFor={`${ids}-w`}>White</label>
          <input
            id={`${ids}-w`}
            type="range"
            min={0}
            max={100}
            value={Math.round(((value.white ?? 0) / 255) * 100)}
            disabled={disabled}
            onChange={(e) => onChange({ rgb: value.rgb, white: Math.round((Number(e.currentTarget.value) / 100) * 255) })}
          />
          <span className={c("slider-value")}>{Math.round(((value.white ?? 0) / 255) * 100)}%</span>
        </div>
      )}
    </div>
  );
}

/**
 * Sends slider changes after the hand stops moving, and shows the pending
 * value until the device reports it.
 */
function useSettled<T>(actual: T, send: (v: T) => void, ms = 350) {
  const [draft, setDraft] = useState<T | null>(null);
  const timer = useRef<number | undefined>(undefined);
  const settle = useRef<number | undefined>(undefined);
  useEffect(() => () => { window.clearTimeout(timer.current); window.clearTimeout(settle.current); }, []);
  useEffect(() => setDraft(null), [JSON.stringify(actual)]);
  const set = (v: T) => {
    setDraft(v);
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => {
      send(v);
      window.clearTimeout(settle.current);
      settle.current = window.setTimeout(() => setDraft(null), 3000);
    }, ms);
  };
  return [draft ?? actual, set] as const;
}

export function ColorTile({
  channel,
  label,
  disabled,
  header,
  children,
  onPower,
  onColor,
  onBrightness,
}: {
  channel: ChannelState;
  label: string;
  disabled: boolean;
  header: ReactNode;
  children?: ReactNode;
  onPower: (on: boolean) => void;
  onColor: (v: ColorValue) => void;
  onBrightness: (level: number) => void;
}) {
  const ids = useId();
  const withWhite = channel.kind === "Rgbw";
  const actual: ColorValue = { rgb: (channel.rgb ?? [255, 255, 255]) as Rgb, white: withWhite ? channel.white ?? 0 : null };
  const [value, setValue] = useSettled(actual, onColor);
  const [level, setLevel] = useSettled(Math.round(channel.brightness ?? 100), onBrightness);
  const glow = shown(value);
  const strip = {
    "--led": css(glow),
    "--led-level": channel.on ? 0.25 + (level / 100) * 0.75 : 0,
  } as CSSProperties;

  return (
    <div className={c("tile", "tile--color", channel.on && "tile--on")}>
      {header}
      <div className={c("strip", channel.on && "strip--on")} style={strip} aria-hidden="true">
        <span />
      </div>
      <div className={c("color-main")}>
        <button
          type="button"
          className={c("button", "power")}
          aria-pressed={channel.on}
          aria-label={`${label}: turn ${channel.on ? "off" : "on"}`}
          disabled={disabled}
          onClick={() => onPower(!channel.on)}
        >
          {channel.on ? "Turn off" : "Turn on"}
        </button>
        <div className={c("slider-row")}>
          <label htmlFor={`${ids}-b`}>Level</label>
          <input
            id={`${ids}-b`}
            type="range"
            min={1}
            max={100}
            value={level}
            disabled={disabled}
            onChange={(e) => setLevel(Number(e.currentTarget.value))}
          />
          <span className={c("slider-value")}>{level}%</span>
        </div>
      </div>
      <ColorPicker value={value} withWhite={withWhite} onChange={setValue} disabled={disabled} label={label} />
      {children}
    </div>
  );
}
