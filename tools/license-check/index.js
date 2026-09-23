#!/usr/bin/env node
/**
 * NeoPrint licence check.
 *
 * Verifies that every third-party dependency across the three ecosystems
 * (NuGet, npm, pub) carries an allowed licence, and that the major versions of
 * the packages whose newer releases are commercially licensed have not been
 * raised.
 *
 * Exit code 0 means the policy holds; 1 means the build must stop.
 *
 * Usage:
 *   node tools/license-check            # human readable report
 *   node tools/license-check --json     # machine readable report
 *   node tools/license-check --verbose  # also list every compliant dependency
 *   node tools/license-check --strict   # a skipped ecosystem is a failure
 *
 * CI runs it with --strict, after every project has been restored: an ecosystem
 * that silently scanned nothing would otherwise report a green licence check.
 *
 * Plan section 2.0 requires this to run in CI; it has no npm dependencies of its
 * own, only the Node standard library.
 */

import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { scanNpm } from './lib/npm.js';
import { scanNuGet } from './lib/nuget.js';
import { majorOf, Policy } from './lib/policy.js';
import { parsePackageVersions } from './lib/props.js';
import { scanPub } from './lib/pub.js';

const toolDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(toolDir, '..', '..');

const CONFIG = {
    solutions: [
        path.join('backend', 'NeoPrint.sln'),
        path.join('desktop', 'NeoPrint.Kiosk.sln'),
    ],
    npmProjects: ['admin'],
    pubProjects: ['mobile'],
    centralPackagesFile: 'Directory.Packages.props',
};

function run(command, args) {
    return execFileSync(command, args, {
        cwd: repoRoot,
        encoding: 'utf8',
        maxBuffer: 64 * 1024 * 1024,
        stdio: ['ignore', 'pipe', 'pipe'],
    });
}

/**
 * Check the declared central package versions against the pinned majors.
 *
 * @param {Policy} policy
 * @returns {{violations: Array, checked: number, skipped?: string}}
 */
function checkCentralPackagePins(policy) {
    const propsPath = path.join(repoRoot, CONFIG.centralPackagesFile);
    if (!existsSync(propsPath)) {
        return { violations: [], checked: 0, skipped: `${CONFIG.centralPackagesFile} not found` };
    }

    const declared = parsePackageVersions(readFileSync(propsPath, 'utf8'));
    const violations = [];

    for (const [name, pin] of Object.entries(policy.pinnedMajors)) {
        const version = declared.get(name);
        if (version === undefined) {
            continue;
        }
        const major = majorOf(version);
        if (major !== pin.major) {
            violations.push({
                ecosystem: 'nuget',
                name,
                version,
                license: null,
                code: 'pinned-major-violated',
                message:
                    `${CONFIG.centralPackagesFile} declares ${name} ${version}, but the major version ` +
                    `is pinned to ${pin.major}. ${pin.reason}`,
            });
        }
    }

    return { violations, checked: declared.size };
}

function main() {
    const argv = process.argv.slice(2);
    const asJson = argv.includes('--json');
    const verbose = argv.includes('--verbose');
    const strict = argv.includes('--strict');

    const policy = Policy.load(path.join(toolDir, 'allowlist.json'));

    const pins = checkCentralPackagePins(policy);
    const scans = [
        { name: 'NuGet', result: scanNuGet({ ...CONFIG, repoRoot, run, policy }) },
        { name: 'npm', result: scanNpm({ projects: CONFIG.npmProjects, repoRoot, policy }) },
        { name: 'pub', result: scanPub({ projects: CONFIG.pubProjects, repoRoot, policy }) },
    ];

    const violations = [...pins.violations];
    const compliant = [];
    const errors = [];
    const skipped = [];

    for (const scan of scans) {
        errors.push(...scan.result.errors.map((message) => `[${scan.name}] ${message}`));
        if (scan.result.skipped) {
            skipped.push(`${scan.name}: ${scan.result.skipped}`);
        }
        for (const dependency of scan.result.dependencies) {
            const verdict = policy.check(dependency);
            if (verdict.ok) {
                compliant.push(dependency);
            } else {
                violations.push({ ...dependency, code: verdict.code, message: verdict.message });
            }
        }
    }

    if (strict && skipped.length > 0) {
        for (const note of skipped) {
            errors.push(`[strict] ${note} — restore the project before running the licence check`);
        }
    }

    const report = {
        ok: violations.length === 0 && errors.length === 0,
        strict,
        checked: compliant.length + violations.length,
        compliant: compliant.length,
        violations,
        errors,
        skipped,
    };

    if (asJson) {
        process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);
    } else {
        printReport(report, compliant, verbose);
    }

    process.exit(report.ok ? 0 : 1);
}

function printReport(report, compliant, verbose) {
    const out = (line = '') => process.stdout.write(`${line}\n`);

    out('NeoPrint licence check');
    out('='.repeat(60));

    for (const note of report.skipped) {
        out(`  skipped  ${note}`);
    }

    if (verbose) {
        out();
        for (const dependency of compliant) {
            out(`  ok       ${dependency.ecosystem.padEnd(6)} ${dependency.name} ${dependency.version} — ${dependency.license}`);
        }
    }

    if (report.errors.length > 0) {
        out();
        out(`Errors (${report.errors.length}):`);
        for (const error of report.errors) {
            out(`  ERROR    ${error}`);
        }
    }

    if (report.violations.length > 0) {
        out();
        out(`Violations (${report.violations.length}):`);
        for (const violation of report.violations) {
            out(`  FAIL     [${violation.code}] ${violation.message}`);
        }
    }

    out();
    out('-'.repeat(60));
    out(`Checked ${report.checked} dependencies, ${report.compliant} compliant.`);
    out(report.ok ? 'PASS — licence policy holds.' : 'FAIL — licence policy violated, build must stop.');
}

main();
