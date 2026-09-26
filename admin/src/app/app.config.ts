import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import { MAT_ICON_DEFAULT_OPTIONS } from '@angular/material/icon';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';

import { routes } from './app.routes';
import { authInterceptor } from './core/auth/auth.interceptor';
import { AuthService } from './core/auth/auth.service';
import { ThemeService } from './core/theme/theme.service';
import {
  defaultLanguage,
  supportedLanguages,
  translations,
} from './core/i18n/translations';

export const appConfig: ApplicationConfig = {
  providers: [
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

    /**
     * Loads the translations and restores the session before the first route
     * renders, so the panel never flashes the sign-in screen at somebody who
     * is already signed in.
     */
    provideAppInitializer(async () => {
      const translate = inject(TranslateService);

      // Before the first render, or the panel would flash light and then
      // switch to the theme the person chose.
      inject(ThemeService).initialise();

      for (const language of supportedLanguages) {
        translate.setTranslation(language, translations[language]);
      }

      const stored = localStorage.getItem('neoprint.language');
      const language = supportedLanguages.includes(
        stored as (typeof supportedLanguages)[number],
      )
        ? stored!
        : defaultLanguage;

      translate.use(language);

      await inject(AuthService).restore();
    }),
  ],
};
