import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { MAT_FORM_FIELD_DEFAULT_OPTIONS } from '@angular/material/form-field';
import {
  ApplicationConfig,
  inject,
  isDevMode,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import { MAT_ICON_DEFAULT_OPTIONS } from '@angular/material/icon';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { SwUpdate, provideServiceWorker } from '@angular/service-worker';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';

import { routes } from './app.routes';
import { authInterceptor } from './core/auth/auth.interceptor';
import { AuthService } from './core/auth/auth.service';
import { ThemeService } from './core/theme/theme.service';
import { defaultLanguage, supportedLanguages } from './core/i18n/languages';

/**
 * Loads the translations and restores the session before the first route
 * renders, so the panel never flashes the sign-in screen at somebody who is
 * already signed in.
 *
 * Every `inject()` comes before the first `await`: after it there is no
 * injection context, the initializer throws NG0203 and the panel stays a
 * blank page (T-124).
 */
export async function initialiseApp(): Promise<void> {
  const translate = inject(TranslateService);
  const theme = inject(ThemeService);
  const auth = inject(AuthService);
  const updates = inject(SwUpdate);

  // The panel is kept on the device by a service worker (plan section 8B), so
  // what opens first is the copy from the last visit. A newer build is asked
  // for before anything is drawn, and taken at once - otherwise the first
  // visit after a release would still run the old panel against the new API.
  if (await newerBuild(updates)) {
    location.reload();

    return;
  }

  // Before the first render, or the panel would flash light and then switch
  // to the theme the person chose.
  theme.initialise();

  // The dictionary is a chunk of its own: the first download stays small.
  const { translations } = await import('./core/i18n/translations');

  for (const language of supportedLanguages) {
    translate.setTranslation(language, translations[language]);
  }

  const stored = localStorage.getItem('neoprint.language');
  const language = supportedLanguages.includes(stored as (typeof supportedLanguages)[number])
    ? stored!
    : defaultLanguage;

  translate.use(language);

  await auth.restore();
}

/** How long the start-up waits to hear whether there is a newer build; offline, it does not wait at all. */
const updateCheckMs = 3000;

/** Remembers that this tab has just reloaded for a build, so a check that keeps saying "newer" cannot loop. */
const reloadedKey = 'neoprint.reloadedForBuild';

/** Whether a newer build was found and made the active one. Never throws: without it the panel opens as it is. */
export async function newerBuild(updates: SwUpdate): Promise<boolean> {
  // No service worker serves this page - a first visit, or a browser without
  // one: the page came from the server and is the newest build. Asking would
  // wait for a worker that registers only once the panel has started (T-278).
  const controlled = typeof navigator !== 'undefined' && !!navigator.serviceWorker?.controller;

  if (!updates.isEnabled || !controlled || !navigator.onLine || sessionStorage.getItem(reloadedKey)) {
    sessionStorage.removeItem(reloadedKey);

    return false;
  }

  try {
    const found = await Promise.race([
      updates.checkForUpdate(),
      new Promise<boolean>((resolve) => setTimeout(() => resolve(false), updateCheckMs)),
    ]);

    if (!found) {
      return false;
    }

    await updates.activateUpdate();
    sessionStorage.setItem(reloadedKey, '1');

    return true;
  } catch {
    return false;
  }
}

export const appConfig: ApplicationConfig = {
  providers: [
    // One look for every form field, set here rather than per template, so a
    // redesign changes it in one place (docs/DESIGN_SYSTEM.md, "Inputlar").
    { provide: MAT_FORM_FIELD_DEFAULT_OPTIONS, useValue: { appearance: 'outline' } },
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),

    provideRouter(routes, withComponentInputBinding()),

    provideHttpClient(withInterceptors([authInterceptor])),

    provideTranslateService({
      fallbackLang: defaultLanguage,
      lang: defaultLanguage,
    }),

    /**
     * Every `mat-icon` renders with the self-hosted Material Symbols Rounded
     * font (docs/DESIGN_SYSTEM.md section 3). Without this the default font
     * set is `material-icons`, which the panel does not ship, and each icon
     * shows as its own name in plain text.
     */
    {
      provide: MAT_ICON_DEFAULT_OPTIONS,
      useValue: { fontSet: 'material-symbols-rounded' },
    },

    // Off while developing: a cached build would hide every change.
    provideServiceWorker('ngsw-worker.js', {
      enabled: !isDevMode(),
      registrationStrategy: 'registerWhenStable:30000',
    }),

    provideAppInitializer(initialiseApp),
  ],
};
