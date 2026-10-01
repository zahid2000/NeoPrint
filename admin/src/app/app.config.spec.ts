import { ApplicationInitStatus } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TranslateService } from '@ngx-translate/core';
import { describe, expect, it } from 'vitest';

import { appConfig } from './app.config';
import { AuthService } from './core/auth/auth.service';
import { ThemeService } from './core/theme/theme.service';

/**
 * The app initializer as the browser runs it (T-124): it awaits the
 * dictionary chunk before it is done, so an `inject()` after that await threw
 * NG0203 and left the panel a blank page that every component test missed.
 */
describe('app initializer', () => {
  it('loads the dictionary and restores the session without leaving the injection context', async () => {
    let restored = false;

    TestBed.configureTestingModule({
      providers: [
        ...appConfig.providers,
        { provide: AuthService, useValue: { restore: async () => void (restored = true) } },
        { provide: ThemeService, useValue: { initialise: () => undefined } },
      ],
    });

    await TestBed.inject(ApplicationInitStatus).donePromise;

    expect(restored).toBe(true);
    expect(TestBed.inject(TranslateService).instant('common.reason')).toBe('Səbəb');
  });
});
