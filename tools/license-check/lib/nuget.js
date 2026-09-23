import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';

import { identifyFromText, identifyFromUrl, normalizeIdentifier } from './spdx.js';

/**
 * Collect every NuGet package (direct and transitive) referenced by a solution
 * and resolve the licence of each one from its nuspec in the global package
 * folder.
 */

/**
 * Parse the JSON produced by `dotnet list package --include-transitive --format json`.
 *
 * @param {string} json
 * @returns {Map<string, string>} package id -> resolved version
 */
export function parseDotnetListOutput(json) {
    const result = new Map();
    const parsed = JSON.parse(json);

    for (const project of parsed.projects ?? []) {
        for (const framework of project.frameworks ?? []) {
            const groups = [
                ...(framework.topLevelPackages ?? []),
                ...(framework.transitivePackages ?? []),
            ];
            for (const entry of groups) {
                const version = entry.resolvedVersion ?? entry.requestedVersion;
                if (typeof entry.id !== 'string' || typeof version !== 'string') {
                    continue;
                }
                const existing = result.get(entry.id);
                // Keep the highest resolved version when a package appears in
                // several target frameworks.
                if (existing === undefined || compareVersions(version, existing) > 0) {
                    result.set(entry.id, version);
                }
            }
        }
    }

    return result;
}

/**
 * Extract the licence declarations from a .nuspec document.
 *
 * @param {string} xml
 * @returns {{expression: string | null, file: string | null, url: string | null}}
 */
export function parseNuspec(xml) {
    const licenseTag = /<license\b([^>]*)>([\s\S]*?)<\/license>/i.exec(xml);
    let expression = null;
    let file = null;

    if (licenseTag !== null) {
        const attributes = licenseTag[1];
        const value = licenseTag[2].trim();
        const type = /type\s*=\s*"([^"]*)"/i.exec(attributes)?.[1]?.toLowerCase();
        if (type === 'file') {
            file = value;
        } else {
            expression = value;
        }
    }

    const url = /<licenseUrl>([\s\S]*?)<\/licenseUrl>/i.exec(xml)?.[1]?.trim() ?? null;

    return { expression, file, url: url === '' ? null : url };
}

/**
 * Locate the NuGet global packages folder.
 *
 * @param {(command: string, args: string[]) => string} run
 * @returns {string}
 */
export function resolveGlobalPackagesFolder(run) {
    if (process.env.NUGET_PACKAGES) {
        return process.env.NUGET_PACKAGES;
    }
    try {
        const output = run('dotnet', ['nuget', 'locals', 'global-packages', '--list']);
        const match = /global-packages:\s*(.+)$/im.exec(output);
        if (match) {
            return match[1].trim();
        }
    } catch {
        // Fall through to the documented default below.
    }
    return path.join(homedir(), '.nuget', 'packages');
}

/**
 * Resolve the licence of a single package from its extracted nuspec.
 *
 * @returns {string | null} An SPDX expression, or null when it cannot be determined.
 */
export function resolveLicenseFromPackageFolder(packageFolder, readFile = readFileSync) {
    if (!existsSync(packageFolder)) {
        return null;
    }

    const nuspecName = readdirSync(packageFolder).find((entry) => entry.toLowerCase().endsWith('.nuspec'));
    if (nuspecName === undefined) {
        return null;
    }

    const nuspec = parseNuspec(readFile(path.join(packageFolder, nuspecName), 'utf8'));

    if (nuspec.expression !== null) {
        return nuspec.expression;
    }

    if (nuspec.file !== null) {
        const licensePath = path.join(packageFolder, nuspec.file);
        if (existsSync(licensePath)) {
            const identified = identifyFromText(readFile(licensePath, 'utf8'));
            if (identified !== null) {
                return identified;
            }
        }
    }

    return identifyFromUrl(nuspec.url);
}

/**
 * Scan one or more solutions and return every resolved third-party dependency.
 *
 * @param {{solutions: string[], repoRoot: string, run: Function, policy: import('./policy.js').Policy}} options
 * @returns {{dependencies: Array, errors: string[]}}
 */
export function scanNuGet({ solutions, repoRoot, run, policy }) {
    const errors = [];
    const packages = new Map();

    const existing = solutions.filter((solution) => existsSync(path.join(repoRoot, solution)));
    if (existing.length === 0) {
        return { dependencies: [], errors, skipped: 'no .NET solution found yet' };
    }

    for (const solution of existing) {
        let output;
        try {
            output = run('dotnet', [
                'list',
                path.join(repoRoot, solution),
                'package',
                '--include-transitive',
                '--format',
                'json',
            ]);
        } catch (error) {
            errors.push(`dotnet list failed for ${solution}: ${error.message}`);
            continue;
        }

        let listed;
        try {
            listed = parseDotnetListOutput(output);
        } catch (error) {
            errors.push(`could not parse dotnet list output for ${solution}: ${error.message}`);
            continue;
        }

        for (const [name, version] of listed) {
            if (policy.isIgnored(name)) {
                continue;
            }
            const existingVersion = packages.get(name);
            if (existingVersion === undefined || compareVersions(version, existingVersion) > 0) {
                packages.set(name, version);
            }
        }
    }

    const packagesRoot = resolveGlobalPackagesFolder(run);
    const dependencies = [];

    for (const [name, version] of [...packages].sort(([a], [b]) => a.localeCompare(b))) {
        const override = policy.packageLicenses[name];
        const folder = path.join(packagesRoot, name.toLowerCase(), version.toLowerCase());
        const license = override ?? resolveLicenseFromPackageFolder(folder);
        dependencies.push({
            ecosystem: 'nuget',
            name,
            version,
            license: license === null ? null : (normalizeIdentifier(license) ?? license),
            source: folder,
        });
    }

    return { dependencies, errors };
}

/**
 * Compare two dotted version strings numerically, segment by segment.
 *
 * @param {string} left
 * @param {string} right
 * @returns {number}
 */
export function compareVersions(left, right) {
    const toParts = (value) =>
        value
            .split('-')[0]
            .split('.')
            .map((part) => Number.parseInt(part, 10) || 0);
    const a = toParts(left);
    const b = toParts(right);
    for (let i = 0; i < Math.max(a.length, b.length); i += 1) {
        const diff = (a[i] ?? 0) - (b[i] ?? 0);
        if (diff !== 0) {
            return diff < 0 ? -1 : 1;
        }
    }
    return 0;
}
