import { Injectable, computed, signal } from '@angular/core';

/** What the person chose, which is not the same as what is on screen. */
export type ThemePreference = 'system' | 'light' | 'dark';

/** What is actually rendered. */
export type ResolvedTheme = 'light' | 'dark';

const storageKey = 'neoprint.theme';

/**
 * Light and dark for the panel (plan section 8A.1, docs/DESIGN_SYSTEM.md).
 *
 * The default is the operating system's setting, because that is what the
 * person already decided for every other application. An explicit choice is
 * remembered on the device and wins over it.
 *
 * The service only writes `data-theme` on `<html>`; the colours themselves
 * live in `styles/tokens.css`, which is where the design system is.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly _preference = signal<ThemePreference>('system');

  /** The operating system's current setting, kept up to date. */
  private readonly _systemPrefersDark = signal(false);

  readonly preference = this._preference.asReadonly();

  /** What is on screen now. */
  readonly resolved = computed<ResolvedTheme>(() => {
    const preference = this._preference();

    if (preference === 'system') {
      return this._systemPrefersDark() ? 'dark' : 'light';
    }

    return preference;
  });

  /**
   * Reads the stored choice and starts following the system setting.
   *
   * Called from the application initialiser rather than from the constructor,
   * so a unit test can drive the service without a document.
   */
  initialise(): void {
    this._preference.set(readStoredPreference());

    const query = matchMedia('(prefers-color-scheme: dark)');
    this._systemPrefersDark.set(query.matches);

    // The person can change the system theme while the panel is open, and on
    // the 'system' preference the panel is supposed to follow. The colours
    // follow on their own - the media query in tokens.css does that - so only
    // the signal behind the toggle's icon has to be updated here.
    query.addEventListener('change', (event) => this._systemPrefersDark.set(event.matches));

    this.apply();
  }

  /** Records a choice and applies it. */
  set(preference: ThemePreference): void {
    this._preference.set(preference);

    try {
      if (preference === 'system') {
        localStorage.removeItem(storageKey);
      } else {
        localStorage.setItem(storageKey, preference);
      }
    } catch {
      // A browser with storage disabled still gets the theme for this visit.
    }

    this.apply();
  }

  /**
   * Flips between light and dark.
   *
   * From 'system' it goes to the opposite of what is currently rendered, which
   * is what somebody pressing the button is asking for.
   */
  toggle(): void {
    this.set(this.resolved() === 'dark' ? 'light' : 'dark');
  }

  /**
   * Writes the choice onto the document, or removes it.
   *
   * On 'system' the attribute is removed rather than set to the resolved
   * value: without it the media query in tokens.css is what decides, so the
   * panel keeps following the operating system even while it is open. An
   * explicit choice is written - including 'light', which is what stops the
   * operating system's dark preference from overriding it.
   */
  private apply(): void {
    const preference = this._preference();

    if (preference === 'system') {
      document.documentElement.removeAttribute('data-theme');
      return;
    }

    document.documentElement.setAttribute('data-theme', preference);
  }
}

function readStoredPreference(): ThemePreference {
  try {
    const stored = localStorage.getItem(storageKey);

    return stored === 'light' || stored === 'dark' ? stored : 'system';
  } catch {
    return 'system';
  }
}
