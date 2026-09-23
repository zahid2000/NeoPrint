import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { licenseFromManifest } from '../lib/npm.js';
import { compareVersions, parseDotnetListOutput, parseNuspec } from '../lib/nuget.js';
import { parsePackageVersions } from '../lib/props.js';
import { parsePubspecLock } from '../lib/pub.js';

describe('parseDotnetListOutput', () => {
    const sample = JSON.stringify({
        version: 1,
        parameters: '--include-transitive',
        projects: [
            {
                path: 'NeoPrint.Api.csproj',
                frameworks: [
                    {
                        framework: 'net8.0',
                        topLevelPackages: [
                            { id: 'MediatR', requestedVersion: '12.5.0', resolvedVersion: '12.5.0' },
                            { id: 'MongoDB.Driver', requestedVersion: '3.12.0', resolvedVersion: '3.12.0' },
                        ],
                        transitivePackages: [
                            { id: 'MediatR.Contracts', resolvedVersion: '2.0.1' },
                            { id: 'System.Text.Json', resolvedVersion: '8.0.5' },
                        ],
                    },
                ],
            },
        ],
    });

    it('collects both top-level and transitive packages', () => {
        const packages = parseDotnetListOutput(sample);
        assert.equal(packages.get('MediatR'), '12.5.0');
        assert.equal(packages.get('MediatR.Contracts'), '2.0.1');
        assert.equal(packages.get('System.Text.Json'), '8.0.5');
        assert.equal(packages.size, 4);
    });

    it('keeps the highest version when a package appears under several frameworks', () => {
        const multi = JSON.stringify({
            projects: [
                {
                    frameworks: [
                        { framework: 'net8.0', topLevelPackages: [{ id: 'SkiaSharp', resolvedVersion: '2.88.9' }] },
                        { framework: 'net8.0-windows', topLevelPackages: [{ id: 'SkiaSharp', resolvedVersion: '2.88.10' }] },
                    ],
                },
            ],
        });
        assert.equal(parseDotnetListOutput(multi).get('SkiaSharp'), '2.88.10');
    });

    it('falls back to the requested version when nothing was resolved', () => {
        const unresolved = JSON.stringify({
            projects: [{ frameworks: [{ topLevelPackages: [{ id: 'QRCoder', requestedVersion: '1.8.0' }] }] }],
        });
        assert.equal(parseDotnetListOutput(unresolved).get('QRCoder'), '1.8.0');
    });

    it('tolerates a project with no packages at all', () => {
        assert.equal(parseDotnetListOutput(JSON.stringify({ projects: [{ frameworks: [] }] })).size, 0);
    });
});

describe('parseNuspec', () => {
    it('reads a modern SPDX expression', () => {
        const xml = '<package><metadata><license type="expression">Apache-2.0</license></metadata></package>';
        assert.deepEqual(parseNuspec(xml), { expression: 'Apache-2.0', file: null, url: null });
    });

    it('reads a licence file reference', () => {
        const xml = '<package><metadata><license type="file">LICENSE.txt</license></metadata></package>';
        const parsed = parseNuspec(xml);
        assert.equal(parsed.file, 'LICENSE.txt');
        assert.equal(parsed.expression, null);
    });

    it('reads the deprecated licenseUrl element', () => {
        const xml = '<package><metadata><licenseUrl>https://licenses.nuget.org/MIT</licenseUrl></metadata></package>';
        assert.equal(parseNuspec(xml).url, 'https://licenses.nuget.org/MIT');
    });

    it('returns nulls when the nuspec declares no licence', () => {
        assert.deepEqual(parseNuspec('<package><metadata><id>X</id></metadata></package>'), {
            expression: null,
            file: null,
            url: null,
        });
    });
});

describe('compareVersions', () => {
    it('orders versions numerically rather than lexically', () => {
        assert.equal(compareVersions('2.88.10', '2.88.9') > 0, true);
        assert.equal(compareVersions('8.0.31', '8.0.5') > 0, true);
        assert.equal(compareVersions('12.5.0', '12.5.0'), 0);
        assert.equal(compareVersions('1.0', '1.0.0'), 0);
    });

    it('ignores the prerelease suffix', () => {
        assert.equal(compareVersions('1.8.25-beta', '1.8.25'), 0);
    });
});

describe('licenseFromManifest', () => {
    it('reads the current license field', () => {
        assert.equal(licenseFromManifest({ license: 'MIT' }), 'MIT');
    });

    it('reads the legacy object form', () => {
        assert.equal(licenseFromManifest({ license: { type: 'BSD-2-Clause' } }), 'BSD-2-Clause');
    });

    it('turns a legacy licenses array into an OR expression', () => {
        assert.equal(licenseFromManifest({ licenses: [{ type: 'MIT' }, { type: 'Apache-2.0' }] }), '(MIT OR Apache-2.0)');
        assert.equal(licenseFromManifest({ licenses: ['ISC'] }), 'ISC');
    });

    it('returns null when no licence is declared', () => {
        assert.equal(licenseFromManifest({}), null);
        assert.equal(licenseFromManifest({ license: '' }), null);
        assert.equal(licenseFromManifest({ licenses: [] }), null);
    });
});

describe('parsePubspecLock', () => {
    const lock = [
        '# Generated by pub',
        'packages:',
        '  dio:',
        '    dependency: "direct main"',
        '    description:',
        '      name: dio',
        '      sha256: "abc"',
        '      url: "https://pub.dev"',
        '    source: hosted',
        '    version: "5.7.0"',
        '  flutter:',
        '    dependency: "direct main"',
        '    description: flutter',
        '    source: sdk',
        '    version: "0.0.0"',
        '  leaflet_stub:',
        '    dependency: transitive',
        '    source: hosted',
        '    version: "1.2.3"',
        'sdks:',
        '  dart: ">=3.3.0 <4.0.0"',
    ].join('\n');

    it('reads every package with its version and source', () => {
        const packages = parsePubspecLock(lock);
        assert.equal(packages.length, 3);
        assert.deepEqual(packages[0], { name: 'dio', version: '5.7.0', source: 'hosted' });
        assert.deepEqual(packages[1], { name: 'flutter', version: '0.0.0', source: 'sdk' });
        assert.deepEqual(packages[2], { name: 'leaflet_stub', version: '1.2.3', source: 'hosted' });
    });

    it('stops at the next top-level key rather than reading the sdks section', () => {
        assert.equal(parsePubspecLock(lock).some((entry) => entry.name === 'dart'), false);
    });

    it('returns an empty list when there is no packages section', () => {
        assert.deepEqual(parsePubspecLock('sdks:\n  dart: ">=3.3.0"'), []);
    });
});

describe('parsePackageVersions', () => {
    it('reads every PackageVersion declaration', () => {
        const xml = [
            '<Project>',
            '  <ItemGroup>',
            '    <PackageVersion Include="MediatR" Version="12.5.0" />',
            '    <PackageVersion Include="MassTransit" Version="8.5.10" />',
            '    <PackageVersion Version="7.2.2" Include="FluentAssertions" />',
            '  </ItemGroup>',
            '</Project>',
        ].join('\n');

        const versions = parsePackageVersions(xml);
        assert.equal(versions.get('MediatR'), '12.5.0');
        assert.equal(versions.get('MassTransit'), '8.5.10');
        assert.equal(versions.get('FluentAssertions'), '7.2.2');
        assert.equal(versions.size, 3);
    });

    it('ignores entries without both attributes', () => {
        assert.equal(parsePackageVersions('<PackageVersion Include="X" />').size, 0);
    });
});
