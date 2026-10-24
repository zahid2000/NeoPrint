/**
 * Every event the server sends over `/hubs/admin` (plan section 6), by name.
 *
 * The one place a name is written: a page subscribes with `HubEvents.x`, never
 * with the string, so a renamed or misspelt event is a compile error rather
 * than a page that silently stops following (T-252). `scripts/hub-check.mjs`
 * holds this list against what the backend really sends, in CI:
 *
 * - an event the backend sends and this list lacks fails;
 * - an event in this list the backend never sends fails;
 * - an event no page listens to fails, unless {@link unheardHubEvents} says why;
 * - a name written as a string anywhere else in the panel fails.
 */
export const HubEvents = {
  // The account itself
  accountChanged: 'AccountChanged',
  permissionsChanged: 'PermissionsChanged',

  // Kiosks and what they report
  kioskStatusChanged: 'KioskStatusChanged',
  kioskPrintersUpdated: 'KioskPrintersUpdated',
  kioskTelemetryUpdated: 'KioskTelemetryUpdated',
  printerTelemetryUpdated: 'PrinterTelemetryUpdated',
  commandResult: 'CommandResult',
  queueUpdated: 'QueueUpdated',

  // Devices
  deviceRegistered: 'DeviceRegistered',
  deviceActivated: 'DeviceActivated',
  deviceRevoked: 'DeviceRevoked',

  // Configuration
  featureFlagsChanged: 'FeatureFlagsChanged',

  // Releases and the kiosks' versions (T-317)
  releasesChanged: 'ReleasesChanged',
  kioskUpdateReported: 'KioskUpdateReported',

  // Storage
  storageSyncProgress: 'StorageSyncProgress',
  storageSyncCompleted: 'StorageSyncCompleted',
  storageHealthChanged: 'StorageHealthChanged',

  // Incidents
  incidentRaised: 'IncidentRaised',
  incidentResolved: 'IncidentResolved',

  // Money
  paymentSucceeded: 'PaymentSucceeded',
  refundStatusChanged: 'RefundStatusChanged',
  fiscalStatusChanged: 'FiscalStatusChanged',

  // Supplies and service
  supplyAlertRaised: 'SupplyAlertRaised',
  supplyAlertCleared: 'SupplyAlertCleared',
  serviceTaskAssigned: 'ServiceTaskAssigned',
  serviceTaskUpdated: 'ServiceTaskUpdated',
} as const;

export type HubEvent = (typeof HubEvents)[keyof typeof HubEvents];

/**
 * Events nothing in the panel listens to yet, each with the reason. An entry
 * goes when the page that shows the data arrives - the check then insists on
 * a listener.
 */
export const unheardHubEvents: Partial<Record<HubEvent, string>> = {};

/** What each event carries, for the ones a page reads rather than only reacts to. */
export interface KioskStatusChangedEvent {
  readonly kioskId: string;
  readonly status: string;
}

export interface IncidentEvent {
  readonly incidentId: string;
  readonly type: string;
  readonly severity?: string;
  readonly status?: string;
  readonly kioskId?: string;
}

/** An event about one kiosk; every kiosk-scoped event carries the id under this name. */
export interface AboutKiosk {
  readonly kioskId?: string;
}
