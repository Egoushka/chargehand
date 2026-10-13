import { useMemo } from 'react';
import { usePreferences } from '../shell/preferences';

// Chart colours come from the design system's tokens and from nothing else. CSS can use `var(--chart-1)`; a canvas cannot, so
// uPlot gets the token's computed value, read again whenever the theme changes.

/** The design system's categorical hues, in its fixed order. A ninth series is never a new hue: the tail folds into "Other". */
export const SERIES_SLOTS = 8;

/** A series' place in the order. Slot 0 is "Other", the de-emphasis grey. */
export type Slot = number;

export interface ChartColors {
  /** Index 0 is "Other"; 1 to 8 are `--chart-1` to `--chart-8`. */
  slots: string[];
  text: string;
  textMuted: string;
  textSubtle: string;
  /** Hairline grid. */
  grid: string;
  /** Axis rules and the baseline. */
  axis: string;
  /** What a mark's 2 px gap and ring are drawn in. */
  surface: string;
  font: string;
}

const read = (style: CSSStyleDeclaration, name: string) => style.getPropertyValue(name).trim();

/** The token values for the current theme. Empty strings where there is no stylesheet (a test). */
export function readChartColors(root: HTMLElement = document.documentElement): ChartColors {
  const style = getComputedStyle(root);
  return {
    slots: [
      read(style, '--neutral-9'),
      ...Array.from({ length: SERIES_SLOTS }, (_, i) => read(style, `--chart-${i + 1}`)),
    ],
    text: read(style, '--text'),
    textMuted: read(style, '--text-muted'),
    textSubtle: read(style, '--text-subtle'),
    grid: read(style, '--border'),
    axis: read(style, '--border-hover'),
    surface: read(style, '--surface'),
    font: getComputedStyle(document.body).fontFamily || 'sans-serif',
  };
}

/** The colours of the current theme; a new object when the theme changes, so a chart redraws in the new colours. */
export function useChartColors(): ChartColors {
  const { theme } = usePreferences();
  // biome-ignore lint/correctness/useExhaustiveDependencies: the theme is what the computed colours depend on
  return useMemo(() => readChartColors(), [theme]);
}

/** The CSS custom property that paints a slot, for HTML and SVG marks (they use the token directly, with no JavaScript colour). */
export const slotVar = (slot: Slot): string => (slot <= 0 ? '--neutral-9' : `--chart-${slot}`);

/**
 * Keeps a series on its hue for as long as the screen is open: a group takes the lowest free slot the first time it is shown and
 * keeps it when a filter drops it and brings it back (the colour follows the entity, never its rank). A slot is only taken from a
 * group that is not shown, and only when no unclaimed slot is left. At most `SERIES_SLOTS` groups are shown at once; the model
 * folds the tail into "Other" (slot 0).
 */
export class SlotMemory {
  private readonly slots = new Map<string, Slot>();

  /** The slot of each name in `shown`, in the order given. */
  assign(shown: readonly string[]): Map<string, Slot> {
    const present = new Set(shown);
    const taken = (name: string) => this.slots.get(name);
    const inUse = () => new Set([...present].map(taken).filter((s): s is Slot => s !== undefined));
    for (const name of shown) {
      if (this.slots.has(name)) continue;
      const shownSlots = inUse();
      const remembered = new Set(this.slots.values());
      const free = (from: Set<Slot>) =>
        Array.from({ length: SERIES_SLOTS }, (_, i) => i + 1).find((slot) => !from.has(slot));
      const slot = free(remembered) ?? free(shownSlots);
      if (slot === undefined) {
        this.slots.set(name, 0);
        continue;
      }
      for (const [other, held] of this.slots) if (held === slot) this.slots.delete(other);
      this.slots.set(name, slot);
    }
    return new Map(shown.map((name) => [name, this.slots.get(name) ?? 0]));
  }
}
