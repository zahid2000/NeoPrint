import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ThemeService } from './theme.service';

/**
 * A stand-in for the `prefers-color-scheme` query, so a test can say what the
 * operating system is set to and then change it.
 */
function stubMatchMedia(prefersDark: boolean): (matches: boolean) => void {
  let listener: ((event: MediaQueryListEvent) => void) | undefined;

  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('dark') && prefersDark,
    media: query,
    addEventListener: (_: string, handler: (event: MediaQueryListEvent) => void) => {
      listener = handler;
    },
    removeEventListener: () => undefined,
  }));

  return (matches: boolean) => listener?.({ matches } as MediaQueryListEvent);
}

describe('ThemeService', () => {
  let service: ThemeService;

  beforeEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    TestBed.configureTestingModule({});
    service = TestBed.inject(ThemeService);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
  });

  it('follows the operating system when nothing was chosen', () => {
    stubMatchMedia(true);

    service.initialise();

    expect(service.preference()).toBe('system');
    expect(service.resolved()).toBe('dark');

    // No attribute: the media query in tokens.css is what applies the colours,
    // so the panel keeps following the system while it is open.
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
  });

  it('keeps following the system when it changes mid-session', () => {
    const systemChanged = stubMatchMedia(false);

    service.initialise();
    expect(service.resolved()).toBe('light');

    systemChanged(true);

    expect(service.resolved()).toBe('dark');
  });

  it('remembers an explicit choice and writes it onto the document', () => {
    stubMatchMedia(true);
    service.initialise();

    service.set('light');

    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    expect(localStorage.getItem('neoprint.theme')).toBe('light');

    // The point of writing 'light': without it the operating system's dark
    // preference would override the choice.
    expect(service.resolved()).toBe('light');
  });

  it('restores the stored choice on the next visit', () => {
    localStorage.setItem('neoprint.theme', 'dark');
    stubMatchMedia(false);

    service.initialise();

    expect(service.preference()).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('toggles to the opposite of what is on screen', () => {
    stubMatchMedia(true);
    service.initialise();

    // From 'system' resolving to dark, the button means "make it light".
    service.toggle();

    expect(service.preference()).toBe('light');

    service.toggle();

    expect(service.preference()).toBe('dark');
  });

  it('going back to the system setting clears what was stored', () => {
    stubMatchMedia(false);
    service.initialise();
    service.set('dark');

    service.set('system');

    expect(localStorage.getItem('neoprint.theme')).toBeNull();
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
  });

  it('still works when storage is unavailable', () => {
    stubMatchMedia(false);

    const getItem = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('storage disabled');
    });
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage disabled');
    });

    service.initialise();
    service.set('dark');

    // The choice applies for this visit even though it could not be saved.
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');

    getItem.mockRestore();
    setItem.mockRestore();
  });
});
