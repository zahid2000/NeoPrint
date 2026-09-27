/**
 * Minimal SPDX licence expression handling.
 *
 * Supports the subset that actually appears in NuGet, npm and pub metadata:
 *   MIT
 *   MIT OR Apache-2.0
 *   (MIT AND BSD-3-Clause)
 *   Apache-2.0 WITH LLVM-exception
 *
 * Written by hand rather than pulled from npm: plan section 2.0 requires that a
 * dependency is only added when writing it ourselves is disproportionately hard.
 */

/** Canonical spellings for identifiers that tools spell inconsistently. */
const ALIASES = new Map(
    Object.entries({
        'mit': 'MIT',
        'mit license': 'MIT',
        'the mit license': 'MIT',
        'apache 2.0': 'Apache-2.0',
        'apache-2': 'Apache-2.0',
        'apache2.0': 'Apache-2.0',
        'apache license 2.0': 'Apache-2.0',
        'bsd': 'BSD-3-Clause',
        'bsd-2': 'BSD-2-Clause',
        'bsd-3': 'BSD-3-Clause',
        'bsd 2-clause': 'BSD-2-Clause',
        'bsd 3-clause': 'BSD-3-Clause',
        'new bsd': 'BSD-3-Clause',
        'simplified bsd': 'BSD-2-Clause',
        'mpl-2': 'MPL-2.0',
        'mpl 2.0': 'MPL-2.0',
        'mozilla public license 2.0': 'MPL-2.0',
        'lgpl-3': 'LGPL-3.0',
        'lgpl-3.0-only': 'LGPL-3.0',
        'lgpl-3.0-or-later': 'LGPL-3.0',
        'gpl-3.0-only': 'GPL-3.0',
        'agpl-3.0-only': 'AGPL-3.0',
    }),
);

/**
 * Normalize a raw licence string to a canonical SPDX identifier.
 * Returns null when the input carries no usable identifier.
 *
 * @param {string | null | undefined} raw
 * @returns {string | null}
 */
export function normalizeIdentifier(raw) {
    if (typeof raw !== 'string') {
        return null;
    }
    const trimmed = raw.trim().replace(/^\((.*)\)$/s, '$1').trim();
    if (trimmed === '') {
        return null;
    }
    const alias = ALIASES.get(trimmed.toLowerCase());
    if (alias) {
        return alias;
    }
    // Already SPDX-shaped (e.g. BSD-3-Clause, MPL-2.0, CC0-1.0).
    if (/^[A-Za-z0-9.+-]+$/.test(trimmed)) {
        return trimmed;
    }
    return null;
}

/**
 * Tokenize an SPDX expression into identifiers, operators and parentheses.
 *
 * @param {string} expression
 * @returns {string[]}
 */
function tokenize(expression) {
    return expression
        .replace(/\(/g, ' ( ')
        .replace(/\)/g, ' ) ')
        .split(/\s+/)
        .filter((token) => token !== '');
}

/**
 * Evaluate an SPDX expression against a predicate that decides whether a single
 * identifier is acceptable.
 *
 * OR passes when any operand passes; AND requires every operand to pass.
 * `WITH <exception>` is ignored - the exception only narrows an already
 * permissive licence.
 *
 * Grammar:
 *   expression := term (OR term)*
 *   term       := factor (AND factor)*
 *   factor     := '(' expression ')' | identifier ['WITH' identifier]
 *
 * @param {string} expression
 * @param {(identifier: string) => boolean} isAllowed
 * @returns {boolean}
 */
export function evaluate(expression, isAllowed) {
    if (typeof expression !== 'string' || expression.trim() === '') {
        return false;
    }

    const tokens = tokenize(expression);
    let position = 0;

    const peek = () => tokens[position];
    const next = () => tokens[position++];

    function parseFactor() {
        const token = next();
        if (token === undefined) {
            return false;
        }
        if (token === '(') {
            const value = parseExpression();
            if (peek() === ')') {
                next();
            }
            return value;
        }
        if (peek() !== undefined && peek().toUpperCase() === 'WITH') {
            next(); // WITH
            next(); // the exception identifier
        }
        const identifier = normalizeIdentifier(token);
        return identifier !== null && isAllowed(identifier);
    }

    function parseTerm() {
        let value = parseFactor();
        while (peek() !== undefined && peek().toUpperCase() === 'AND') {
            next();
            const right = parseFactor();
            value = value && right;
        }
        return value;
    }

    function parseExpression() {
        let value = parseTerm();
        while (peek() !== undefined && peek().toUpperCase() === 'OR') {
            next();
            const right = parseTerm();
            value = value || right;
        }
        return value;
    }

    return parseExpression();
}

/**
 * Distinctive phrases used to recognise a licence from its full text.
 * Order matters: the most specific signature has to be tried first.
 */
const TEXT_SIGNATURES = [
    { id: 'Apache-2.0', phrases: ['apache license', 'version 2.0, january 2004'] },
    // Before the GNU family: the MPL's own text names the GPL, LGPL and AGPL
    // as "Secondary Licenses", so an AGPL phrase check would claim every
    // MPL-2.0 package. No GNU licence text carries the MPL's title.
    { id: 'MPL-2.0', phrases: ['mozilla public license version 2.0'] },
    { id: 'AGPL-3.0', phrases: ['gnu affero general public license'] },
    { id: 'LGPL-3.0', phrases: ['gnu lesser general public license'] },
    { id: 'GPL-3.0', phrases: ['gnu general public license'] },
    { id: 'ISC', phrases: ['permission to use, copy, modify, and/or distribute this software'] },
    { id: 'MIT', phrases: ['permission is hereby granted, free of charge'] },
    { id: 'BSD-3-Clause', phrases: ['redistributions in binary form', 'neither the name of'] },
    { id: 'BSD-2-Clause', phrases: ['redistributions in binary form'] },
];

/**
 * Best-effort identification of a licence from its full text. Used only when a
 * package ships a licence file instead of declaring an SPDX identifier.
 *
 * @param {string} text
 * @returns {string | null}
 */
export function identifyFromText(text) {
    if (typeof text !== 'string' || text.trim() === '') {
        return null;
    }
    const haystack = text.toLowerCase().replace(/\s+/g, ' ');
    for (const signature of TEXT_SIGNATURES) {
        if (signature.phrases.every((phrase) => haystack.includes(phrase))) {
            return signature.id;
        }
    }
    return null;
}

/** Licence URLs that unambiguously identify a licence. */
const URL_SIGNATURES = [
    { pattern: /licenses\.nuget\.org\/(.+?)$/i, capture: true },
    { pattern: /spdx\.org\/licenses\/([A-Za-z0-9.+-]+)/i, capture: true },
    { pattern: /opensource\.org\/licenses\/([A-Za-z0-9.+-]+)/i, capture: true },
    { pattern: /apache\.org\/licenses\/LICENSE-2\.0/i, id: 'Apache-2.0' },
    { pattern: /mozilla\.org\/(en-US\/)?MPL\/2\.0/i, id: 'MPL-2.0' },
    { pattern: /gnu\.org\/licenses\/lgpl-3\.0/i, id: 'LGPL-3.0' },
    { pattern: /gnu\.org\/licenses\/agpl-3\.0/i, id: 'AGPL-3.0' },
    { pattern: /gnu\.org\/licenses\/gpl-3\.0/i, id: 'GPL-3.0' },
];

/**
 * Identify a licence from a licence URL.
 *
 * @param {string | null | undefined} url
 * @returns {string | null}
 */
export function identifyFromUrl(url) {
    if (typeof url !== 'string' || url.trim() === '') {
        return null;
    }
    for (const signature of URL_SIGNATURES) {
        const match = signature.pattern.exec(url);
        if (!match) {
            continue;
        }
        if (signature.capture) {
            const identifier = normalizeIdentifier(
                decodeURIComponent(match[1]).replace(/\.html?$/i, ''),
            );
            if (identifier !== null) {
                return identifier;
            }
            continue;
        }
        return signature.id;
    }
    return null;
}
