import { readFileSync } from 'node:fs';

import { evaluate, normalizeIdentifier } from './spdx.js';

/**
 * The licence policy: which licences are allowed, which packages are exempt by
 * name, which major versions are pinned and which packages are banned outright.
 */
export class Policy {
    /**
     * @param {object} raw Parsed allowlist.json.
     */
    constructor(raw) {
        this.allowedLicenses = new Set(raw.allowedLicenses ?? []);
        this.packageExceptions = stripComments(raw.packageExceptions ?? {});
        this.packageLicenses = stripComments(raw.packageLicenses ?? {});
        this.pinnedMajors = stripComments(raw.pinnedMajors ?? {});
        this.bannedPackages = stripComments(raw.bannedPackages ?? {});
        this.ignoredPackagePrefixes = raw.ignoredPackagePrefixes ?? [];
    }

    /**
     * @param {string} path
     * @returns {Policy}
     */
    static load(path) {
        return new Policy(JSON.parse(readFileSync(path, 'utf8')));
    }

    /** Our own projects are not third-party dependencies. */
    isIgnored(packageName) {
        return this.ignoredPackagePrefixes.some((prefix) => packageName.startsWith(prefix));
    }

    /**
     * Evaluate a single resolved dependency against the policy.
     *
     * @param {{ecosystem: string, name: string, version: string, license: string | null, source: string}} dependency
     * @returns {{ok: true} | {ok: false, code: string, message: string}}
     */
    check(dependency) {
        const { name, version, license } = dependency;

        const banned = this.bannedPackages[name];
        if (banned !== undefined) {
            return {
                ok: false,
                code: 'banned-package',
                message: `${name} is explicitly ruled out by plan section 2.0: ${banned}`,
            };
        }

        const pin = this.pinnedMajors[name];
        if (pin !== undefined) {
            const major = majorOf(version);
            if (major === null) {
                return {
                    ok: false,
                    code: 'unparsable-version',
                    message: `${name} ${version}: version cannot be parsed, but the major version is pinned to ${pin.major}.`,
                };
            }
            if (major !== pin.major) {
                return {
                    ok: false,
                    code: 'pinned-major-violated',
                    message:
                        `${name} ${version} leaves the pinned major version ${pin.major}. ${pin.reason} ` +
                        'Lower the version instead of widening the policy.',
                };
            }
        }

        const exception = this.packageExceptions[name];
        if (exception !== undefined) {
            const declared = normalizeIdentifier(license);
            const expected = normalizeIdentifier(exception.license) ?? exception.license;
            // An exception covers exactly the licence it was granted for. If the
            // package changed its licence, the exception no longer applies.
            if (license !== null && declared !== null && declared !== expected) {
                return {
                    ok: false,
                    code: 'exception-mismatch',
                    message:
                        `${name} ${version} is allowlisted as ${exception.license} but now declares ${license}. ` +
                        'Re-check the licence before updating allowlist.json.',
                };
            }
            return { ok: true };
        }

        if (license === null) {
            return {
                ok: false,
                code: 'unknown-license',
                message:
                    `${name} ${version}: no licence could be determined. Add it to packageLicenses ` +
                    'in allowlist.json after verifying the package LICENSE, or remove the dependency.',
            };
        }

        if (evaluate(license, (id) => this.allowedLicenses.has(id))) {
            return { ok: true };
        }

        return {
            ok: false,
            code: 'disallowed-license',
            message: `${name} ${version} is licensed under ${license}, which is not on the allowed list.`,
        };
    }
}

/**
 * Extract the major version number from a NuGet, npm or pub version string.
 *
 * @param {string} version
 * @returns {number | null}
 */
export function majorOf(version) {
    if (typeof version !== 'string') {
        return null;
    }
    const match = /^\s*v?(\d+)\./.exec(version);
    if (match === null) {
        const single = /^\s*v?(\d+)\s*$/.exec(version);
        return single === null ? null : Number.parseInt(single[1], 10);
    }
    return Number.parseInt(match[1], 10);
}

/** Drop the `$comment` keys used to document allowlist.json in place. */
function stripComments(object) {
    const copy = { ...object };
    delete copy.$comment;
    return copy;
}
