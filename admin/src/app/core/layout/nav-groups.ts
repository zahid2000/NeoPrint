/**
 * The admin menu in groups (T-304, docs/DESIGN_SYSTEM.md 5.3): which page goes
 * where, which groups are shut, and what a screen of a given width shows.
 */

/** The groups, in the order the menu shows them. */
export const navGroups = [
  'monitoring',
  'operations',
  'warehouse',
  'finance',
  'customers',
  'reports',
  'configuration',
  'administration',
] as const;

export type NavGroupId = (typeof navGroups)[number];

/** What each group is called on the menu. */
export const navGroupLabels: Readonly<Record<NavGroupId, string>> = {
  monitoring: 'nav.group.monitoring',
  operations: 'nav.group.operations',
  warehouse: 'nav.group.warehouse',
  finance: 'nav.group.finance',
  customers: 'nav.group.customers',
  reports: 'nav.group.reports',
  configuration: 'nav.group.configuration',
  administration: 'nav.group.administration',
};

/** One entry in the sidebar. */
export interface NavItem {
  readonly path: string;
  readonly labelKey: string;
  readonly icon: string;
  /** Permissions that put this entry on the menu; empty means always. */
  readonly permissions: readonly string[];
  /** The group it belongs to; none for the two at the top. */
  readonly group?: NavGroupId;
}

/** A part of the menu: the ungrouped top, or one group with what this account may open in it. */
export interface NavSection {
  readonly group: NavGroupId | null;
  readonly items: readonly NavItem[];
}

/**
 * The menu as this account sees it: the ungrouped entries first, then each
 * group in its order. A group with nothing this account may open is left out.
 */
export function navSections(items: readonly NavItem[]): NavSection[] {
  const sections: NavSection[] = [];
  const top = items.filter((item) => !item.group);

  if (top.length > 0) {
    sections.push({ group: null, items: top });
  }

  for (const group of navGroups) {
    const inGroup = items.filter((item) => item.group === group);

    if (inGroup.length > 0) {
      sections.push({ group, items: inGroup });
    }
  }

  return sections;
}

/** Whether the page at `url` is this entry's (the dashboard only at "/" itself). */
export function isActive(item: NavItem, url: string): boolean {
  const path = url.split(/[?#]/)[0];

  return item.path === '/' ? path === '/' : path === item.path || path.startsWith(`${item.path}/`);
}

/** A group opened or shut by hand, on the page it was done on. */
export interface GroupChoice {
  /** The one group left open; null when every group was shut. */
  readonly group: NavGroupId | null;
  readonly path: string;
}

function pathOf(url: string): string {
  return url.split(/[?#]/)[0];
}

/**
 * The one group that shows its entries (T-319): the group chosen by hand on
 * this page, otherwise the group of the page on screen; none on a page
 * without a group. Opening another page forgets the choice.
 */
export function openGroup(sections: readonly NavSection[], url: string, choice: GroupChoice | null): NavGroupId | null {
  if (choice && choice.path === pathOf(url)) {
    return choice.group;
  }

  return sections.find((section) => section.group && section.items.some((item) => isActive(item, url)))?.group ?? null;
}

/** What a click on a group heading leaves open: that group, or nothing when it was the open one. */
export function toggleChoice(open: NavGroupId | null, clicked: NavGroupId, url: string): GroupChoice {
  return { group: open === clicked ? null : clicked, path: pathOf(url) };
}

/** What the sidebar is at a width (docs/DESIGN_SYSTEM.md 5.3). */
export type NavLayout = 'full' | 'rail' | 'drawer';

/** Up to this width a phone: the menu is a drawer over the page. */
export const phoneScreen = '(max-width: 600px)';

/** Up to this width a tablet: icons beside the page. */
export const tabletScreen = '(max-width: 1024px)';
