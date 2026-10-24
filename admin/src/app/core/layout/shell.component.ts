import { BreakpointObserver } from '@angular/cdk/layout';
import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { filter, map } from 'rxjs';

import { Permissions } from '../auth/auth.models';
import { AuthService } from '../auth/auth.service';
import { ThemeService } from '../theme/theme.service';
import { LiveIndicatorComponent } from '../../shared/live-indicator.component';
import { SupportedLanguage, languageNames, supportedLanguages } from '../i18n/languages';
import { panelConnection } from '../realtime/panel-connection';
import {
  NavItem,
  NavLayout,
  NavSection,
  isActive,
  navGroupLabels,
  isGroupOpen,
  navSections,
  phoneScreen,
  readCollapsed,
  tabletScreen,
  writeCollapsed,
} from './nav-groups';

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
export class ShellComponent {
  private readonly auth = inject(AuthService);
  private readonly theme_ = inject(ThemeService);
  private readonly translate = inject(TranslateService);
  private readonly breakpoints = inject(BreakpointObserver);

  readonly admin = this.auth.admin;

  /** Which theme is on screen, for the toggle's icon. */
  readonly theme = this.theme_.resolved;

  /**
   * What the toggle would do, not what is on screen now: a button says where
   * it takes you.
   */
  readonly themeTooltip = computed(() => (this.theme() === 'dark' ? 'theme.light' : 'theme.dark'));
  /**
   * What the menu is at this width (docs/DESIGN_SYSTEM.md 5.3, T-304): beside
   * the page in full, a strip of icons on a tablet, a drawer on a phone.
   */
  readonly layout = toSignal(
    this.breakpoints
      .observe([phoneScreen, tabletScreen])
      .pipe(map((state): NavLayout => (state.breakpoints[phoneScreen] ? 'drawer' : state.breakpoints[tabletScreen] ? 'rail' : 'full'))),
    { initialValue: this.layoutNow() },
  );

  /** A phone: the menu goes over the page and stays shut until asked for. */
  readonly narrow = computed(() => this.layout() === 'drawer');

  /** On a tablet: whether ☰ has opened the full menu over the page. */
  readonly railExpanded = signal(false);

  /** Icons only: a tablet whose menu has not been opened in full. */
  readonly iconsOnly = computed(() => this.layout() === 'rail' && !this.railExpanded());

  readonly sidenavMode = computed(() =>
    this.layout() === 'drawer' || (this.layout() === 'rail' && this.railExpanded()) ? 'over' : 'side',
  );

  readonly sidenavOpen = signal(this.layoutNow() === 'full');

  /** The page on screen, for which group stays open. */
  private readonly url = signal(inject(Router).url);

  /** The groups shut in this browser. */
  private readonly collapsed = signal(readCollapsed(this.storage()));

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
      // The same account's phone-sized panel (plan section 8B).
      path: '/operator',
      labelKey: 'nav.operator',
      icon: 'engineering',
      permissions: [Permissions.operatorPanel],
    },
    {
      path: '/kiosks',
      labelKey: 'nav.kiosks',
      icon: 'storefront',
      permissions: [Permissions.kiosksView],
      group: 'monitoring',
    },
    {
      path: '/printers',
      labelKey: 'nav.printers',
      icon: 'print',
      permissions: [Permissions.printersView],
      group: 'monitoring',
    },
    {
      path: '/incidents',
      labelKey: 'nav.incidents',
      icon: 'crisis_alert',
      permissions: [Permissions.incidentsView],
      group: 'monitoring',
    },
    {
      path: '/service-tasks',
      labelKey: 'nav.serviceTasks',
      icon: 'assignment',
      permissions: [Permissions.serviceTasksView],
      group: 'operations',
    },
    {
      path: '/inventory',
      labelKey: 'nav.inventory',
      icon: 'warehouse',
      permissions: [Permissions.inventoryView],
      group: 'warehouse',
    },
    {
      path: '/supply-rules',
      labelKey: 'nav.supplyRules',
      icon: 'notifications_active',
      permissions: [Permissions.suppliesView],
      group: 'warehouse',
    },
    {
      path: '/audit',
      labelKey: 'nav.audit',
      icon: 'history',
      permissions: [Permissions.auditView],
      group: 'administration',
    },
    {
      path: '/system-jobs',
      labelKey: 'nav.systemJobs',
      icon: 'settings_suggest',
      permissions: [Permissions.systemJobsView],
      group: 'administration',
    },
    {
      path: '/releases',
      labelKey: 'nav.releases',
      icon: 'deployed_code',
      permissions: [Permissions.releasesView],
      group: 'administration',
    },
    {
      path: '/print-jobs',
      labelKey: 'nav.printJobs',
      icon: 'receipt_long',
      permissions: [Permissions.jobsView],
      group: 'operations',
    },
    {
      path: '/suspected',
      labelKey: 'nav.suspected',
      icon: 'manage_search',
      permissions: [Permissions.jobsView],
      group: 'operations',
    },
    {
      path: '/payments',
      labelKey: 'nav.payments',
      icon: 'payments',
      permissions: [Permissions.paymentsView],
      group: 'finance',
    },
    {
      path: '/refunds',
      labelKey: 'nav.refunds',
      icon: 'undo',
      permissions: [Permissions.refundsView],
      group: 'finance',
    },
    {
      path: '/fiscal',
      labelKey: 'nav.fiscal',
      icon: 'receipt',
      permissions: [Permissions.fiscalView],
      group: 'finance',
    },
    {
      path: '/receipts',
      labelKey: 'nav.receipts',
      icon: 'description',
      permissions: [Permissions.receiptsView],
      group: 'finance',
    },
    {
      path: '/vouchers',
      labelKey: 'nav.vouchers',
      icon: 'confirmation_number',
      permissions: [Permissions.vouchersView],
      group: 'customers',
    },
    {
      path: '/promos',
      labelKey: 'nav.promos',
      icon: 'local_offer',
      permissions: [Permissions.promosManage],
      group: 'customers',
    },
    {
      path: '/referrals',
      labelKey: 'nav.referrals',
      icon: 'card_giftcard',
      permissions: [Permissions.referralsView],
      group: 'customers',
    },
    {
      path: '/customers',
      labelKey: 'nav.customers',
      icon: 'people',
      permissions: [Permissions.usersView],
      group: 'customers',
    },
    {
      path: '/revenue',
      labelKey: 'nav.revenue',
      icon: 'query_stats',
      permissions: [Permissions.reportsView],
      group: 'reports',
    },
    {
      path: '/pilot-report',
      labelKey: 'nav.pilotReport',
      icon: 'insights',
      permissions: [Permissions.analyticsView],
      group: 'reports',
    },
    {
      path: '/devices',
      labelKey: 'nav.devices',
      icon: 'devices',
      permissions: [Permissions.devicesView],
      group: 'monitoring',
    },
    {
      path: '/pricing',
      labelKey: 'nav.pricing',
      icon: 'sell',
      permissions: [Permissions.pricingManage],
      group: 'configuration',
    },
    {
      path: '/formats',
      labelKey: 'nav.formats',
      icon: 'rule',
      permissions: [Permissions.formatsManage],
      group: 'configuration',
    },
    {
      path: '/storage',
      labelKey: 'nav.storage',
      icon: 'cloud',
      permissions: [Permissions.storageManage],
      group: 'administration',
    },
    {
      path: '/roles',
      labelKey: 'nav.roles',
      icon: 'admin_panel_settings',
      permissions: [Permissions.rolesManage],
      group: 'administration',
    },
    {
      path: '/admins',
      labelKey: 'nav.admins',
      icon: 'group',
      permissions: [Permissions.adminsManage],
      group: 'administration',
    },
    {
      path: '/features',
      labelKey: 'nav.features',
      icon: 'toggle_on',
      permissions: [Permissions.featuresManage],
      group: 'configuration',
    },
    {
      path: '/settings',
      labelKey: 'nav.settings',
      icon: 'settings',
      permissions: [Permissions.settingsManage],
      group: 'configuration',
    },
    {
      path: '/legal',
      labelKey: 'nav.legal',
      icon: 'description',
      permissions: [Permissions.legalManage],
      group: 'configuration',
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

  readonly groupLabels = navGroupLabels;

  /** The menu in its groups; a group this account has nothing in is not shown. */
  readonly sections = computed(() => navSections(this.navItems()));

  isOpen(section: NavSection): boolean {
    return isGroupOpen(section, this.collapsed(), this.url());
  }

  isCurrent(item: NavItem): boolean {
    return isActive(item, this.url());
  }

  toggleGroup(section: NavSection): void {
    if (!section.group) {
      return;
    }

    const next = new Set(this.collapsed());

    if (this.isOpen(section)) {
      next.add(section.group);
    } else {
      next.delete(section.group);
    }

    this.collapsed.set(next);
    writeCollapsed(this.storage(), next);
  }

  private layoutNow(): NavLayout {
    return this.breakpoints.isMatched(phoneScreen) ? 'drawer' : this.breakpoints.isMatched(tabletScreen) ? 'rail' : 'full';
  }

  private storage(): Storage | undefined {
    try {
      return globalThis.localStorage;
    } catch {
      return undefined;
    }
  }

  readonly initial = computed(() => {
    const name = this.admin()?.fullName ?? this.admin()?.email ?? '';
    return name.length > 0 ? name[0].toUpperCase() : '?';
  });

  toggleSidenav(): void {
    if (this.layout() === 'rail') {
      this.railExpanded.update((expanded) => !expanded);
      return;
    }

    this.sidenavOpen.update((open) => !open);
  }

  toggleTheme(): void {
    this.theme_.toggle();
  }

  constructor() {
    // The live connection, for as long as the shell is on screen.
    panelConnection();

    // Turning a tablet or resizing the window: the menu follows the width.
    effect(() => {
      const layout = this.layout();
      untracked(() => {
        this.sidenavOpen.set(layout !== 'drawer');
        this.railExpanded.set(false);
      });
    });

    // On a narrow screen a chosen page closes the menu that covers it.
    inject(Router)
      .events.pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe((event) => {
        this.url.set(event.urlAfterRedirects);

        if (this.narrow()) {
          this.sidenavOpen.set(false);
        }

        this.railExpanded.set(false);
      });
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
