import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';

import { Permissions } from '../auth/auth.models';
import { AuthService } from '../auth/auth.service';
import { ThemeService } from '../theme/theme.service';
import {
  SupportedLanguage,
  languageNames,
  supportedLanguages,
} from '../i18n/translations';

/** One entry in the sidebar. */
interface NavItem {
  readonly path: string;
  readonly labelKey: string;
  readonly icon: string;

  /** Permissions that put this entry on the menu; empty means always. */
  readonly permissions: readonly string[];
}

/**
 * The panel shell: sidebar, toolbar and the routed content
 * (plan section 8A.1).
 *
 * The menu is built from the administrator's own permissions, so an operator
 * is not offered pages whose every request would be refused. The server
 * enforces the same permissions, so this is presentation
 * (plan section 8A.2).
 */
@Component({
  selector: 'app-shell',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    MatSidenavModule,
    MatToolbarModule,
    MatListModule,
    MatIconModule,
    MatButtonModule,
    MatMenuModule,
    MatDividerModule,
    MatTooltipModule,
    TranslatePipe,
  ],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
})
export class ShellComponent {
  private readonly auth = inject(AuthService);
  private readonly theme_ = inject(ThemeService);
  private readonly translate = inject(TranslateService);

  readonly admin = this.auth.admin;

  /** Which theme is on screen, for the toggle's icon. */
  readonly theme = this.theme_.resolved;

  /**
   * What the toggle would do, not what is on screen now: a button says where
   * it takes you.
   */
  readonly themeTooltip = computed(() =>
    this.theme() === 'dark' ? 'theme.light' : 'theme.dark',
  );
  readonly sidenavOpen = signal(true);

  readonly languages = supportedLanguages.map((code) => ({
    code,
    name: languageNames[code],
  }));

  readonly currentLanguage = signal<string>(this.translate.getCurrentLang());

  private readonly allItems: readonly NavItem[] = [
    {
      path: '/',
      labelKey: 'nav.dashboard',
      icon: 'dashboard',
      permissions: [],
    },
    {
      path: '/devices',
      labelKey: 'nav.devices',
      icon: 'devices',
      permissions: [Permissions.devicesView],
    },
    {
      path: '/roles',
      labelKey: 'nav.roles',
      icon: 'admin_panel_settings',
      permissions: [Permissions.rolesManage],
    },
    {
      path: '/admins',
      labelKey: 'nav.admins',
      icon: 'group',
      permissions: [Permissions.adminsManage],
    },
    {
      path: '/features',
      labelKey: 'nav.features',
      icon: 'toggle_on',
      permissions: [Permissions.featuresManage],
    },
    {
      path: '/settings',
      labelKey: 'nav.settings',
      icon: 'settings',
      permissions: [Permissions.settingsManage],
    },
    {
      path: '/legal',
      labelKey: 'nav.legal',
      icon: 'description',
      permissions: [Permissions.legalManage],
    },
  ];

  /**
   * The entries this administrator can reach.
   *
   * Computed from the permission signal, so a role change reflows the menu
   * without a reload (plan section 8A.2).
   */
  readonly navItems = computed(() =>
    this.allItems.filter(
      (item) => item.permissions.length === 0 || this.auth.canAny(item.permissions),
    ),
  );

  readonly initial = computed(() => {
    const name = this.admin()?.fullName ?? this.admin()?.email ?? '';
    return name.length > 0 ? name[0].toUpperCase() : '?';
  });

  toggleSidenav(): void {
    this.sidenavOpen.update((open) => !open);
  }

  toggleTheme(): void {
    this.theme_.toggle();
  }

  selectLanguage(language: SupportedLanguage): void {
    this.translate.use(language);
    this.currentLanguage.set(language);

    // A per-browser convenience, not state the server needs to know about.
    localStorage.setItem('neoprint.language', language);
  }

  signOut(): void {
    void this.auth.signOut();
  }
}
