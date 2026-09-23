import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';

import { majorOf, Policy } from '../lib/policy.js';

const toolDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

/** The real policy the repository ships with. */
function realPolicy() {
    return Policy.load(path.join(toolDir, 'allowlist.json'));
}

function dependency(overrides) {
    return {
        ecosystem: 'nuget',
        name: 'Example.Package',
        version: '1.0.0',
        license: 'MIT',
        source: 'test',
        ...overrides,
    };
}

describe('majorOf', () => {
    it('reads the major version of the version formats the three ecosystems use', () => {
        assert.equal(majorOf('12.5.0'), 12);
        assert.equal(majorOf('8.5.10'), 8);
        assert.equal(majorOf('4.0.103.3'), 4);
        assert.equal(majorOf('1.8.25-beta1'), 1);
        assert.equal(majorOf('v2.3.4'), 2);
        assert.equal(majorOf('7'), 7);
    });

    it('returns null when the version cannot be parsed', () => {
        assert.equal(majorOf(''), null);
        assert.equal(majorOf('latest'), null);
        assert.equal(majorOf(null), null);
    });
});

describe('Policy.check — licences', () => {
    const policy = realPolicy();

    it('accepts a permissive licence from the allowed list', () => {
        assert.deepEqual(policy.check(dependency({ license: 'MIT' })), { ok: true });
        assert.deepEqual(policy.check(dependency({ license: 'Apache-2.0' })), { ok: true });
        assert.deepEqual(policy.check(dependency({ license: 'MPL-2.0' })), { ok: true });
    });

    it('accepts a dual licence when one side is permitted', () => {
        assert.deepEqual(policy.check(dependency({ license: 'AGPL-3.0 OR MIT' })), { ok: true });
    });

    it('rejects a copyleft licence that is not allowlisted', () => {
        const verdict = policy.check(dependency({ name: 'Some.Agpl.Package', license: 'AGPL-3.0' }));
        assert.equal(verdict.ok, false);
        assert.equal(verdict.code, 'disallowed-license');
    });

    it('rejects a dependency whose licence could not be determined', () => {
        const verdict = policy.check(dependency({ name: 'Mystery.Package', license: null }));
        assert.equal(verdict.ok, false);
        assert.equal(verdict.code, 'unknown-license');
    });
});

describe('Policy.check — named exceptions', () => {
    const policy = realPolicy();

    it('allows Hangfire.Core under LGPL-3.0, which is otherwise not permitted', () => {
        assert.deepEqual(
            policy.check(dependency({ name: 'Hangfire.Core', version: '1.8.25', license: 'LGPL-3.0' })),
            { ok: true },
        );
        // The same licence on any other package is still a violation.
        const other = policy.check(dependency({ name: 'Other.Package', license: 'LGPL-3.0' }));
        assert.equal(other.ok, false);
    });

    it('allows QuestPDF under its Community licence', () => {
        assert.deepEqual(
            policy.check(dependency({ name: 'QuestPDF', version: '2026.9.0', license: null })),
            { ok: true },
        );
    });

    it('fails when an allowlisted package changes to a different licence', () => {
        const verdict = policy.check(
            dependency({ name: 'Hangfire.Core', version: '1.8.25', license: 'AGPL-3.0' }),
        );
        assert.equal(verdict.ok, false);
        assert.equal(verdict.code, 'exception-mismatch');
    });
});

describe('Policy.check — pinned majors', () => {
    const policy = realPolicy();

    it('accepts the last freely licensed major version', () => {
        assert.deepEqual(policy.check(dependency({ name: 'MediatR', version: '12.5.0', license: 'Apache-2.0' })), { ok: true });
        assert.deepEqual(policy.check(dependency({ name: 'MassTransit', version: '8.5.10', license: 'Apache-2.0' })), { ok: true });
        assert.deepEqual(policy.check(dependency({ name: 'FluentAssertions', version: '7.2.2', license: 'Apache-2.0' })), { ok: true });
    });

    it('blocks MediatR 13, which is commercially licensed', () => {
        const verdict = policy.check(dependency({ name: 'MediatR', version: '13.0.0', license: 'Apache-2.0' }));
        assert.equal(verdict.ok, false);
        assert.equal(verdict.code, 'pinned-major-violated');
    });

    it('blocks MassTransit 9 and FluentAssertions 8', () => {
        assert.equal(policy.check(dependency({ name: 'MassTransit', version: '9.0.0', license: 'Apache-2.0' })).code, 'pinned-major-violated');
        assert.equal(policy.check(dependency({ name: 'MassTransit.RabbitMQ', version: '9.0.0', license: 'Apache-2.0' })).code, 'pinned-major-violated');
        assert.equal(policy.check(dependency({ name: 'FluentAssertions', version: '8.0.0', license: 'Apache-2.0' })).code, 'pinned-major-violated');
    });

    it('blocks a downgrade below the pinned major as well, since that is not the approved version', () => {
        assert.equal(policy.check(dependency({ name: 'MediatR', version: '11.0.0', license: 'Apache-2.0' })).code, 'pinned-major-violated');
    });

    it('takes precedence over the licence check, because the licence metadata lags the change', () => {
        // MediatR 13 still declares Apache-2.0 in some package metadata.
        const verdict = policy.check(dependency({ name: 'MediatR', version: '13.1.0', license: 'MIT' }));
        assert.equal(verdict.ok, false);
        assert.equal(verdict.code, 'pinned-major-violated');
    });
});

describe('Policy.check — banned packages', () => {
    const policy = realPolicy();

    it('reports a package that plan section 2.0 rules out, even under a permissive licence', () => {
        const verdict = policy.check(dependency({ name: 'Moq', version: '4.20.0', license: 'BSD-3-Clause' }));
        assert.equal(verdict.ok, false);
        assert.equal(verdict.code, 'banned-package');
    });

    it('reports the replaced packages named in the plan', () => {
        for (const name of ['Serilog', 'FluentValidation', 'AutoMapper', 'Refit', 'FirebaseAdmin']) {
            assert.equal(policy.check(dependency({ name, license: 'MIT' })).code, 'banned-package', name);
        }
    });
});

describe('Policy — our own projects', () => {
    const policy = realPolicy();

    it('does not treat NeoPrint projects as third-party dependencies', () => {
        assert.equal(policy.isIgnored('NeoPrint.Domain'), true);
        assert.equal(policy.isIgnored('NeoPrint.Crypto'), true);
        assert.equal(policy.isIgnored('MediatR'), false);
    });
});

describe('allowlist.json', () => {
    it('is valid JSON and grants no exception without a stated reason', () => {
        const raw = JSON.parse(readFileSync(path.join(toolDir, 'allowlist.json'), 'utf8'));
        for (const [name, entry] of Object.entries(raw.packageExceptions)) {
            assert.ok(entry.license, `${name} must state the licence it is allowlisted for`);
            assert.ok(entry.reason && entry.reason.length > 20, `${name} must state why`);
        }
        for (const [name, entry] of Object.entries(raw.pinnedMajors)) {
            if (name === '$comment') {
                continue;
            }
            assert.equal(typeof entry.major, 'number', `${name} must pin a numeric major version`);
            assert.ok(entry.reason, `${name} must state why the major version is pinned`);
        }
    });

    it('does not allow any copyleft licence by default', () => {
        const raw = JSON.parse(readFileSync(path.join(toolDir, 'allowlist.json'), 'utf8'));
        for (const id of ['GPL-3.0', 'AGPL-3.0', 'LGPL-3.0', 'SSPL-1.0']) {
            assert.equal(raw.allowedLicenses.includes(id), false, `${id} must not be allowed globally`);
        }
    });
});
