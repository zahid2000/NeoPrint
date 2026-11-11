import { EffectRef, Injector, WritableSignal, effect } from '@angular/core';

const storageKey = (key: string): string => `neo.filterValues.${key}`;

/** An ISO date written by JSON.stringify, read back as a date. */
const isoDate = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$/;

/**
 * A page's filters kept in this browser (T-424): back on the page, the list
 * opens as it was left. Only what the person chose is kept - no rows, no
 * names - and a value of the wrong shape (an older build's) is ignored.
 *
 * Called in a constructor or a field's initialiser, or with the component's
 * injector after it; the keeping stops with the component. A field holding a
 * date is named in `dates`: one that starts empty cannot say so itself.
 */
export function rememberFilters(
  key: string,
  fields: Readonly<Record<string, WritableSignal<unknown>>>,
  options: { readonly injector?: Injector; readonly dates?: readonly string[] } = {},
): EffectRef {
  const { injector, dates = [] } = options;
  const saved = read(key);
  for (const [name, field] of Object.entries(fields)) {
    if (!saved || !(name in saved)) {
      continue;
    }
    const value = saved[name];
    if (dates.includes(name)) {
      if (value === null || (typeof value === 'string' && isoDate.test(value))) {
        field.set(value === null ? null : new Date(value));
      }
    } else if (sameShape(field(), value)) {
      field.set(value);
    }
  }

  return effect(
    () => {
      const values: Record<string, unknown> = {};
      for (const [name, field] of Object.entries(fields)) {
        values[name] = field();
      }
      try {
        localStorage.setItem(storageKey(key), JSON.stringify(values));
      } catch {
        // Private mode or blocked storage: the filters are only not kept.
      }
    },
    injector ? { injector } : undefined,
  );
}

function read(key: string): Record<string, unknown> | null {
  try {
    const raw = localStorage.getItem(storageKey(key));
    const value: unknown = raw ? JSON.parse(raw) : null;
    return value && typeof value === 'object' && !Array.isArray(value)
      ? (value as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

/** A kept value fits the field: a list of words for a list, a flag for a flag, a word or nothing for a choice. */
function sameShape(current: unknown, saved: unknown): boolean {
  if (Array.isArray(current)) {
    return Array.isArray(saved) && saved.every((item) => typeof item === 'string');
  }
  if (current === null) {
    return saved === null || typeof saved === 'string';
  }
  return typeof saved === typeof current;
}
