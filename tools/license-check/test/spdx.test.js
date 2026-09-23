import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { evaluate, identifyFromText, identifyFromUrl, normalizeIdentifier } from '../lib/spdx.js';

const ALLOWED = new Set(['MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'MPL-2.0', 'ISC']);
const isAllowed = (id) => ALLOWED.has(id);

describe('normalizeIdentifier', () => {
    it('passes canonical SPDX identifiers through unchanged', () => {
        assert.equal(normalizeIdentifier('MIT'), 'MIT');
        assert.equal(normalizeIdentifier('BSD-3-Clause'), 'BSD-3-Clause');
        assert.equal(normalizeIdentifier('MPL-2.0'), 'MPL-2.0');
    });

    it('maps the spellings tools actually emit onto canonical identifiers', () => {
        assert.equal(normalizeIdentifier('mit'), 'MIT');
        assert.equal(normalizeIdentifier('The MIT License'), 'MIT');
        assert.equal(normalizeIdentifier('Apache License 2.0'), 'Apache-2.0');
        assert.equal(normalizeIdentifier('New BSD'), 'BSD-3-Clause');
        assert.equal(normalizeIdentifier('LGPL-3.0-or-later'), 'LGPL-3.0');
    });

    it('strips surrounding parentheses', () => {
        assert.equal(normalizeIdentifier('(MIT)'), 'MIT');
    });

    it('returns null for input that carries no identifier', () => {
        assert.equal(normalizeIdentifier(''), null);
        assert.equal(normalizeIdentifier('   '), null);
        assert.equal(normalizeIdentifier(null), null);
        assert.equal(normalizeIdentifier(undefined), null);
        assert.equal(normalizeIdentifier(42), null);
        assert.equal(normalizeIdentifier('see LICENSE file for details'), null);
    });
});

describe('evaluate', () => {
    it('accepts a single allowed identifier', () => {
        assert.equal(evaluate('MIT', isAllowed), true);
    });

    it('rejects a single disallowed identifier', () => {
        assert.equal(evaluate('AGPL-3.0', isAllowed), false);
    });

    it('accepts an OR expression when any operand is allowed', () => {
        assert.equal(evaluate('AGPL-3.0 OR MIT', isAllowed), true);
        assert.equal(evaluate('MIT OR AGPL-3.0', isAllowed), true);
    });

    it('rejects an OR expression when no operand is allowed', () => {
        assert.equal(evaluate('AGPL-3.0 OR GPL-3.0', isAllowed), false);
    });

    it('requires every operand of an AND expression', () => {
        assert.equal(evaluate('MIT AND Apache-2.0', isAllowed), true);
        assert.equal(evaluate('MIT AND AGPL-3.0', isAllowed), false);
    });

    it('honours parentheses over operator precedence', () => {
        assert.equal(evaluate('(MIT OR AGPL-3.0) AND Apache-2.0', isAllowed), true);
        assert.equal(evaluate('(AGPL-3.0 OR GPL-3.0) AND MIT', isAllowed), false);
    });

    it('ignores a WITH exception, which only narrows a permissive licence', () => {
        assert.equal(evaluate('Apache-2.0 WITH LLVM-exception', isAllowed), true);
        assert.equal(evaluate('GPL-3.0 WITH Classpath-exception-2.0', isAllowed), false);
    });

    it('treats empty or non-string input as not allowed', () => {
        assert.equal(evaluate('', isAllowed), false);
        assert.equal(evaluate('   ', isAllowed), false);
        assert.equal(evaluate(null, isAllowed), false);
    });
});

describe('identifyFromUrl', () => {
    it('reads the identifier out of a licenses.nuget.org URL', () => {
        assert.equal(identifyFromUrl('https://licenses.nuget.org/MIT'), 'MIT');
        assert.equal(identifyFromUrl('https://licenses.nuget.org/Apache-2.0'), 'Apache-2.0');
    });

    it('recognises well-known canonical licence URLs', () => {
        assert.equal(identifyFromUrl('http://www.apache.org/licenses/LICENSE-2.0'), 'Apache-2.0');
        assert.equal(identifyFromUrl('https://www.mozilla.org/en-US/MPL/2.0/'), 'MPL-2.0');
        assert.equal(identifyFromUrl('https://www.gnu.org/licenses/lgpl-3.0.html'), 'LGPL-3.0');
    });

    it('returns null for a URL that identifies nothing', () => {
        assert.equal(identifyFromUrl('https://github.com/example/repo/blob/main/LICENSE'), null);
        assert.equal(identifyFromUrl(''), null);
        assert.equal(identifyFromUrl(null), null);
    });
});

describe('identifyFromText', () => {
    it('recognises MIT from its grant clause', () => {
        const text = 'Permission is hereby granted, free of charge, to any person obtaining a copy...';
        assert.equal(identifyFromText(text), 'MIT');
    });

    it('recognises Apache-2.0 from its header', () => {
        const text = 'Apache License\nVersion 2.0, January 2004\nhttp://www.apache.org/licenses/';
        assert.equal(identifyFromText(text), 'Apache-2.0');
    });

    it('distinguishes the GNU family', () => {
        assert.equal(identifyFromText('GNU AFFERO GENERAL PUBLIC LICENSE Version 3'), 'AGPL-3.0');
        assert.equal(identifyFromText('GNU LESSER GENERAL PUBLIC LICENSE Version 3'), 'LGPL-3.0');
        assert.equal(identifyFromText('GNU GENERAL PUBLIC LICENSE Version 3'), 'GPL-3.0');
    });

    it('distinguishes the two BSD variants by the name clause', () => {
        const bsd3 =
            'Redistributions in binary form must reproduce the above copyright notice. ' +
            'Neither the name of the copyright holder nor the names of its contributors may be used to endorse.';
        const bsd2 = 'Redistributions in binary form must reproduce the above copyright notice.';
        assert.equal(identifyFromText(bsd3), 'BSD-3-Clause');
        assert.equal(identifyFromText(bsd2), 'BSD-2-Clause');
    });

    it('returns null for text it cannot identify', () => {
        assert.equal(identifyFromText('Copyright 2026 Example Ltd. All rights reserved.'), null);
        assert.equal(identifyFromText(''), null);
        assert.equal(identifyFromText(null), null);
    });
});
