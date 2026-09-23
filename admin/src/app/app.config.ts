import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';

import { routes } from './app.routes';
import { authInterceptor } from './core/auth/auth.interceptor';
import { AuthService } from './core/auth/auth.service';
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
     * Loads the translations and restores the session before the first route
     * renders, so the panel never flashes the sign-in screen at somebody who
     * is already signed in.
     */
    provideAppInitializer(async () => {
      const translate = inject(TranslateService);

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
