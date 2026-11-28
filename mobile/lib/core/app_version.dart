/// The version of this build: `version` in pubspec.yaml, without the build
/// number (a test keeps the two equal).
///
/// Sent on every request as [appVersionHeader], so the backend can tell an
/// app older than it supports to update (docs/adr/ADR-0009, T-492).
const appVersion = '0.1.0';

const appVersionHeader = 'X-App-Version';
