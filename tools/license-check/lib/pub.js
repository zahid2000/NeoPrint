import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';

import { identifyFromText, normalizeIdentifier } from './spdx.js';

/**
 * Dart / Flutter dependencies.
 *
 * pubspec.yaml carries no licence field, so the resolved package list comes from
 * pubspec.lock and the licence itself is read from the LICENSE file each package
 * is required to ship in the pub cache.
 */

/**
 * Parse the `packages:` section of a pubspec.lock file.
 *
 * pubspec.lock is a small, strictly indented subset of YAML, so it is parsed
 * directly rather than by adding a YAML dependency.
 *
 * @param {string} content
 * @returns {Array<{name: string, version: string, source: string}>}
 */
export function parsePubspecLock(content) {
    const lines = content.split(/\r?\n/);
    const packages = [];

    let inPackages = false;
    let current = null;

    for (const line of lines) {
        if (/^packages:\s*$/.test(line)) {
            inPackages = true;
            continue;
        }
        if (!inPackages) {
            continue;
        }
        // A new top-level key ends the packages section.
        if (/^\S/.test(line) && line.trim() !== '') {
            break;
        }

        const packageHeader = /^ {2}([A-Za-z0-9_]+):\s*$/.exec(line);
        if (packageHeader) {
            current = { name: packageHeader[1], version: '', source: 'hosted' };
            packages.push(current);
            continue;
        }

        if (current === null) {
            continue;
        }

        const version = /^ {4}version:\s*"?([^"\s]+)"?\s*$/.exec(line);
        if (version) {
            current.version = version[1];
            continue;
        }

        const source = /^ {4}source:\s*"?([^"\s]+)"?\s*$/.exec(line);
        if (source) {
            current.source = source[1];
        }
    }

    return packages.filter((entry) => entry.version !== '');
}

/**
 * Locate the pub cache directory.
 *
 * @returns {string}
 */
export function resolvePubCache() {
    if (process.env.PUB_CACHE) {
        return process.env.PUB_CACHE;
    }
    if (process.platform === 'win32') {
        const localAppData = process.env.LOCALAPPDATA;
        if (localAppData) {
            return path.join(localAppData, 'Pub', 'Cache');
        }
    }
    return path.join(homedir(), '.pub-cache');
}

/** Licence file names pub packages conventionally use. */
const LICENSE_FILE_NAMES = ['LICENSE', 'LICENSE.md', 'LICENSE.txt', 'LICENCE', 'LICENCE.md'];

/**
 * Find a cached package directory. Hosted packages live under
 * `hosted/<host>/<name>-<version>`.
 *
 * @param {string} pubCache
 * @param {{name: string, version: string}} entry
 * @returns {string | null}
 */
export function findCachedPackage(pubCache, entry) {
    const hostedRoot = path.join(pubCache, 'hosted');
    if (!existsSync(hostedRoot)) {
        return null;
    }
    const wanted = `${entry.name}-${entry.version}`;
    for (const host of readdirSync(hostedRoot)) {
        const candidate = path.join(hostedRoot, host, wanted);
        if (existsSync(candidate)) {
            return candidate;
        }
    }
    return null;
}

/**
 * Scan the Flutter project's resolved dependencies.
 *
 * @param {{projects: string[], repoRoot: string, policy: import('./policy.js').Policy}} options
 * @returns {{dependencies: Array, errors: string[], skipped?: string}}
 */
export function scanPub({ projects, repoRoot, policy }) {
    const errors = [];
    const dependencies = [];
    let scannedAny = false;

    const pubCache = resolvePubCache();

    for (const project of projects) {
        const lockPath = path.join(repoRoot, project, 'pubspec.lock');
        if (!existsSync(lockPath)) {
            continue;
        }
        scannedAny = true;

        let entries;
        try {
            entries = parsePubspecLock(readFileSync(lockPath, 'utf8'));
        } catch (error) {
            errors.push(`could not parse ${lockPath}: ${error.message}`);
            continue;
        }

        for (const entry of entries) {
            if (policy.isIgnored(entry.name)) {
                continue;
            }
            // Packages shipped inside the Flutter SDK follow the SDK's own BSD-3
            // licence and are not separately resolvable in the pub cache.
            if (entry.source === 'sdk') {
                dependencies.push({
                    ecosystem: 'pub',
                    name: entry.name,
                    version: entry.version,
                    license: 'BSD-3-Clause',
                    source: 'flutter-sdk',
                });
                continue;
            }

            const override = policy.packageLicenses[entry.name];
            const cached = findCachedPackage(pubCache, entry);
            const license = override ?? (cached === null ? null : licenseFromFile(cached));

            dependencies.push({
                ecosystem: 'pub',
                name: entry.name,
                version: entry.version,
                license: license === null ? null : (normalizeIdentifier(license) ?? license),
                source: cached ?? `${pubCache} (not downloaded)`,
            });
        }
    }

    if (!scannedAny) {
        return { dependencies: [], errors, skipped: 'no pubspec.lock found (run flutter pub get first)' };
    }

    dependencies.sort((a, b) => a.name.localeCompare(b.name));
    return { dependencies, errors };
}

/**
 * @param {string} packageDir
 * @returns {string | null}
 */
function licenseFromFile(packageDir) {
    for (const fileName of LICENSE_FILE_NAMES) {
        const candidate = path.join(packageDir, fileName);
        if (existsSync(candidate)) {
            const identified = identifyFromText(readFileSync(candidate, 'utf8'));
            if (identified !== null) {
                return identified;
            }
        }
    }
    return null;
}
