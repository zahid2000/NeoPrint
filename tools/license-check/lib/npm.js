import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';

import { identifyFromText, normalizeIdentifier } from './spdx.js';

/**
 * Read installed npm packages straight from node_modules. This reflects what is
 * actually going to be shipped, which a lockfile alone does not guarantee.
 */

/**
 * Extract the licence from a package.json document.
 *
 * Handles the current `license` field, the deprecated `licenses` array and the
 * `{ "type": "MIT" }` object form that old packages still use.
 *
 * @param {object} manifest
 * @returns {string | null}
 */
export function licenseFromManifest(manifest) {
    const { license, licenses } = manifest;

    if (typeof license === 'string') {
        return license.trim() === '' ? null : license;
    }
    if (license !== null && typeof license === 'object' && typeof license.type === 'string') {
        return license.type;
    }
    if (Array.isArray(licenses) && licenses.length > 0) {
        const identifiers = licenses
            .map((entry) => (typeof entry === 'string' ? entry : entry?.type))
            .filter((entry) => typeof entry === 'string' && entry.trim() !== '');
        if (identifiers.length > 0) {
            return identifiers.length === 1 ? identifiers[0] : `(${identifiers.join(' OR ')})`;
        }
    }
    return null;
}

/** Licence file names npm packages conventionally use. */
const LICENSE_FILE_NAMES = ['LICENSE', 'LICENSE.md', 'LICENSE.txt', 'LICENCE', 'LICENCE.md', 'license', 'license.md'];

/**
 * Enumerate every installed package under a node_modules directory, including
 * scoped packages and nested node_modules.
 *
 * @param {string} nodeModulesDir
 * @returns {string[]} absolute package directories
 */
export function listPackageDirectories(nodeModulesDir) {
    if (!existsSync(nodeModulesDir)) {
        return [];
    }

    const found = [];

    for (const entry of readdirSync(nodeModulesDir)) {
        if (entry.startsWith('.')) {
            continue;
        }
        const entryPath = path.join(nodeModulesDir, entry);
        if (!statSync(entryPath).isDirectory()) {
            continue;
        }

        if (entry.startsWith('@')) {
            for (const scoped of readdirSync(entryPath)) {
                const scopedPath = path.join(entryPath, scoped);
                if (statSync(scopedPath).isDirectory()) {
                    found.push(scopedPath);
                    found.push(...listPackageDirectories(path.join(scopedPath, 'node_modules')));
                }
            }
            continue;
        }

        found.push(entryPath);
        found.push(...listPackageDirectories(path.join(entryPath, 'node_modules')));
    }

    return found;
}

/**
 * Scan the npm workspaces of the repository.
 *
 * @param {{projects: string[], repoRoot: string, policy: import('./policy.js').Policy}} options
 * @returns {{dependencies: Array, errors: string[], skipped?: string}}
 */
export function scanNpm({ projects, repoRoot, policy }) {
    const errors = [];
    const packages = new Map();
    let scannedAny = false;

    for (const project of projects) {
        const nodeModules = path.join(repoRoot, project, 'node_modules');
        if (!existsSync(nodeModules)) {
            continue;
        }
        scannedAny = true;

        for (const packageDir of listPackageDirectories(nodeModules)) {
            const manifestPath = path.join(packageDir, 'package.json');
            if (!existsSync(manifestPath)) {
                continue;
            }

            let manifest;
            try {
                manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
            } catch (error) {
                errors.push(`could not parse ${manifestPath}: ${error.message}`);
                continue;
            }

            const name = manifest.name;
            const version = manifest.version ?? '0.0.0';
            if (typeof name !== 'string' || policy.isIgnored(name)) {
                continue;
            }

            let license = policy.packageLicenses[name] ?? licenseFromManifest(manifest);
            if (license === null) {
                license = licenseFromFile(packageDir);
            }

            const key = `${name}@${version}`;
            if (!packages.has(key)) {
                packages.set(key, {
                    ecosystem: 'npm',
                    name,
                    version,
                    license: license === null ? null : (normalizeIdentifier(license) ?? license),
                    source: packageDir,
                });
            }
        }
    }

    if (!scannedAny) {
        return { dependencies: [], errors, skipped: 'no node_modules found (run npm ci first)' };
    }

    const dependencies = [...packages.values()].sort((a, b) => a.name.localeCompare(b.name));
    return { dependencies, errors };
}

/**
 * Fall back to reading the package's own licence file.
 *
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
