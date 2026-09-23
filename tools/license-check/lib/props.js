/**
 * Read the declared package versions out of Directory.Packages.props.
 *
 * The resolved graph from `dotnet list` already catches a package that is pulled
 * in at the wrong version, but that only works once the solutions exist and have
 * been restored. Checking the declarations as well means a raised pin is caught
 * immediately, in every environment.
 */

/**
 * @param {string} xml
 * @returns {Map<string, string>} package id -> declared version
 */
export function parsePackageVersions(xml) {
    const result = new Map();
    const pattern = /<PackageVersion\b[^>]*\/?>/gi;

    let match;
    while ((match = pattern.exec(xml)) !== null) {
        const tag = match[0];
        const include = /\bInclude\s*=\s*"([^"]+)"/i.exec(tag)?.[1];
        const version = /\bVersion\s*=\s*"([^"]+)"/i.exec(tag)?.[1];
        if (include !== undefined && version !== undefined) {
            result.set(include, version);
        }
    }

    return result;
}
