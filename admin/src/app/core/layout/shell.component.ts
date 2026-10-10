import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
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
import { AdminHubService } from '../realtime/admin-hub.service';
import { ThemeService } from '../theme/theme.service';
import { LiveIndicatorComponent } from '../../shared/live-indicator.component';
import { SupportedLanguage, languageNames, supportedLanguages } from '../i18n/languages';

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
    LiveIndicatorComponent,
  ],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
})
export class ShellComponent implements OnInit, OnDestroy {
  private readonly auth = inject(AuthService);
  private readonly theme_ = inject(ThemeService);
  private readonly hub = inject(AdminHubService);

  /** Removes the SignalR handlers when the shell goes away. */
  private readonly subscriptions: (() => void)[] = [];
  private readonly translate = inject(TranslateService);

  readonly admin = this.auth.admin;

  /** Which theme is on screen, for the toggle's icon. */
  readonly theme = this.theme_.resolved;

  /**
   * What the toggle would do, not what is on screen now: a button says where
   * it takes you.
   */
  readonly themeTooltip = computed(() => (this.theme() === 'dark' ? 'theme.light' : 'theme.dark'));
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
      path: '/kiosks',
      labelKey: 'nav.kiosks',
      icon: 'storefront',
      permissions: [Permissions.kiosksView],
    },
    {
      path: '/print-jobs',
      labelKey: 'nav.printJobs',
      icon: 'receipt_long',
      permissions: [Permissions.jobsView],
    },
    {
      path: '/suspected',
      labelKey: 'nav.suspected',
      icon: 'manage_search',
      permissions: [Permissions.jobsView],
    },
    {
      path: '/payments',
      labelKey: 'nav.payments',
      icon: 'payments',
      permissions: [Permissions.paymentsView],
    },
    {
      path: '/refunds',
      labelKey: 'nav.refunds',
      icon: 'undo',
      permissions: [Permissions.refundsView],
    },
    {
      path: '/fiscal',
      labelKey: 'nav.fiscal',
      icon: 'receipt',
      permissions: [Permissions.fiscalView],
    },
    {
      path: '/receipts',
      labelKey: 'nav.receipts',
      icon: 'description',
      permissions: [Permissions.receiptsView],
    },
    {
      path: '/vouchers',
      labelKey: 'nav.vouchers',
      icon: 'confirmation_number',
      permissions: [Permissions.vouchersView],
    },
    {
      path: '/promos',
      labelKey: 'nav.promos',
      icon: 'local_offer',
      permissions: [Permissions.promosManage],
    },
    {
      path: '/referrals',
      labelKey: 'nav.referrals',
      icon: 'card_giftcard',
      permissions: [Permissions.referralsView],
    },
    {
      path: '/customers',
      labelKey: 'nav.customers',
      icon: 'people',
      permissions: [Permissions.usersView],
    },
    {
      path: '/revenue',
      labelKey: 'nav.revenue',
      icon: 'query_stats',
      permissions: [Permissions.reportsView],
    },
    {
      path: '/devices',
      labelKey: 'nav.devices',
      icon: 'devices',
      permissions: [Permissions.devicesView],
    },
    {
      path: '/pricing',
      labelKey: 'nav.pricing',
      icon: 'sell',
      permissions: [Permissions.pricingManage],
    },
    {
      path: '/formats',
      labelKey: 'nav.formats',
      icon: 'rule',
      permissions: [Permissions.formatsManage],
    },
    {
      path: '/storage',
      labelKey: 'nav.storage',
      icon: 'cloud',
      permissions: [Permissions.storageManage],
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

  /**
   * Opens the real-time connection once the panel is inside the shell, which
   * is the first point at which there is a session for the hub to authorise.
   */
  ngOnInit(): void {
    void this.hub.start();

    // A role change used to reach the panel only on the next token exchange,
    // up to fifteen minutes later; a permission taken away was immediate but
    // one granted was not (docs/DECISIONS.md). The server announces it, so
    // the menu now follows within a second.
    this.subscriptions.push(
      this.hub.on('PermissionsChanged', () => void this.auth.reloadPermissions()),
    );
  }

  ngOnDestroy(): void {
    for (const unsubscribe of this.subscriptions) {
      unsubscribe();
    }

    void this.hub.stop();
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
