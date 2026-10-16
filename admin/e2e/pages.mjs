// What the recorder and the test share: how a browser is found, and what
// counts as a raw translation key on a rendered page.
import { existsSync } from 'node:fs';
import { chromium } from 'playwright-core';

/**
 * A Chrome that is already on the machine: playwright-core brings no browser
 * of its own (docs/THIRD_PARTY.md). CI runners have Google Chrome; on a
 * developer machine Chrome or Edge will do, or E2E_CHROME names an executable.
 */
export async function launchBrowser() {
  const tries = [];

  if (process.env.E2E_CHROME && existsSync(process.env.E2E_CHROME)) {
    tries.push({ executablePath: process.env.E2E_CHROME });
  }

  tries.push({ channel: 'chrome' }, { channel: 'msedge' });

  let last;
  for (const options of tries) {
    try {
      return await chromium.launch({ headless: true, ...options });
    } catch (error) {
      last = error;
    }
  }

  throw new Error(`No Chrome or Edge could be started (set E2E_CHROME to an executable). ${last?.message?.split('\n')[0] ?? ''}`);
}

/*
 * Every script opens its browser context with serviceWorkers: 'block'. The built panel
 * registers a service worker (the operator panel is an installable app), and a
 * request the worker makes is not seen by the page - not by page.route, which
 * plays the recorded API back, and not by page.on('response'). The scripts
 * check the panel, not the worker; the worker is looked at by itself (e2e/pwa.mjs).
 */

/** The panel's languages and where the choice is kept. */
export const languages = ['az', 'en', 'ru'];
export const languageKey = 'neoprint.language';

/**
 * Runs in the page: every visible text and every placeholder, title and
 * aria-label that has the shape of a translation key - "legal.type.ScanNotice" -
 * rather than of words.
 */
export function findRawKeys() {
  const key = /^[a-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9_-]*)+$/;
  // An address, a file name, a date pattern in a date field: dots, but not a key.
  const notKey = /^(gg\.aa\.iiii|dd\.mm\.yyyy|дд\.мм\.гггг)$|@|\/|\.(pdf|png|jpe?g|docx?|xlsx?|pptx?|txt|csv|local|test|com|az|net|org|io|json|html)$/i;
  const found = [];

  const visible = (element) => {
    const box = element.getBoundingClientRect();
    const style = getComputedStyle(element);
    return box.width > 0 && box.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
  };

  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const text = node.nodeValue.trim();
    const parent = node.parentElement;
    if (text && parent && visible(parent) && !parent.closest('script,style,code,pre') && key.test(text) && !notKey.test(text)) {
      found.push(text);
    }
  }

  for (const element of document.querySelectorAll('[placeholder],[title],[aria-label]')) {
    for (const name of ['placeholder', 'title', 'aria-label']) {
      const value = (element.getAttribute(name) ?? '').trim();
      if (value && key.test(value) && !notKey.test(value)) found.push(`${name}="${value}"`);
    }
  }

  return [...new Set(found)];
}

/** The pages every signed-in administrator can reach from the menu. */
export const menuRoutes = [
  '', 'kiosks', 'kiosks/new', 'printers', 'print-jobs', 'suspected', 'payments', 'refunds', 'fiscal', 'receipts', 'vouchers', 'promos',
  'referrals', 'customers', 'revenue', 'devices', 'pricing', 'formats', 'features', 'admins', 'roles', 'storage', 'settings', 'legal',
];
