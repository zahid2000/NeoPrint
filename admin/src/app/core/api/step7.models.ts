/**
 * What the kiosk and printer pages read (step 7A, plan sections 7.9, 7.10,
 * 2.15): a kiosk's telemetry, its printers' readings and history, the supply
 * rules. Every field a kiosk or a printer may leave out is optional: the
 * panel says "not reported", never nought.
 */

import { PaperStock } from './step3.models';

export interface TelemetryApp {
  readonly version?: string | null;
  readonly startedAt?: string | null;
  readonly screen?: string | null;
  readonly session?: string | null;
  readonly lastCrashAt?: string | null;
}

export interface TelemetrySystem {
  readonly os?: string | null;
  readonly osVersion?: string | null;
  readonly hostname?: string | null;
  readonly cpuPercent?: number | null;
  readonly memoryUsedBytes?: number | null;
  readonly memoryTotalBytes?: number | null;
  readonly memoryUsedPercent?: number | null;
  readonly systemDiskFreeBytes?: number | null;
  readonly spoolDiskFreeBytes?: number | null;
  readonly uptimeSeconds?: number | null;
  readonly clockOffsetMs?: number | null;
}

export interface TelemetryNetwork {
  readonly connectionType?: string | null;
  readonly wifiSignalPercent?: number | null;
  readonly localIp?: string | null;
  readonly backendLatencyMs?: number | null;
  readonly hubConnected?: boolean | null;
  readonly hubLastReconnectAt?: string | null;
  readonly hubDisconnectedSeconds?: number | null;
}

export interface TelemetryPeripherals {
  readonly flashInserted?: boolean | null;
  readonly qrScannerLastReadAt?: string | null;
  readonly screenWidth?: number | null;
  readonly screenHeight?: number | null;
}

export interface TelemetryWork {
  readonly queueLength?: number | null;
  readonly currentJob?: string | null;
  readonly jobsToday?: number | null;
  readonly sheetsToday?: number | null;
  readonly spoolFiles?: number | null;
  readonly failedFlashUploads?: number | null;
  readonly pendingCommands?: number | null;
}

export interface TelemetryServices {
  readonly libreOffice?: boolean | null;
  readonly ghostscript?: boolean | null;
  readonly configurationAt?: string | null;
  readonly featureFlags?: string | null;
}

/** The limits a report is over (`disk`, `memory`, `latency`, `hub`, `clock`), each with since when. */
export type TelemetryBreaches = Readonly<Record<string, string>>;

export interface KioskTelemetrySample {
  readonly timestamp: string;
  readonly app?: TelemetryApp | null;
  readonly system?: TelemetrySystem | null;
  readonly network?: TelemetryNetwork | null;
  readonly peripherals?: TelemetryPeripherals | null;
  readonly work?: TelemetryWork | null;
  readonly services?: TelemetryServices | null;
  readonly breaches?: TelemetryBreaches | null;
}

/** GET /api/admin/kiosks/{id}/telemetry, and the body of `KioskTelemetryUpdated`. */
export interface KioskTelemetry {
  readonly kioskId: string;
  readonly sample?: KioskTelemetrySample | null;

  /** The last report is older than three intervals: its figures are not "now". */
  readonly stale: boolean;
  readonly intervalSeconds: number;
  readonly status: {
    readonly status: string;
    readonly lastHeartbeatAt?: string | null;
    readonly openIncidents: number;
    readonly paper: Readonly<Record<string, PaperStock>>;
  };
}

/** One point of the kiosk's graphs: the average over its slice of time. */
export interface KioskTelemetryPoint {
  readonly at: string;
  readonly cpuPercent?: number | null;
  readonly memoryUsedPercent?: number | null;
  readonly systemDiskFreeBytes?: number | null;
  readonly spoolDiskFreeBytes?: number | null;
  readonly backendLatencyMs?: number | null;
  readonly queueLength?: number | null;
  readonly samples: number;
}

export interface PrinterSupplyReading {
  readonly name: string;
  readonly type: string;
  readonly color?: string | null;
  readonly levelPercent?: number | null;
}

export interface PrinterTrayReading {
  readonly name: string;
  readonly media?: string | null;
  readonly levelPercent?: number | null;
}

export interface PrinterSpoolerJob {
  readonly id: string;
  readonly sizeBytes?: number | null;
  readonly state?: string | null;
  readonly submittedAt?: string | null;
}

export interface PrinterFeatures {
  readonly color?: boolean | null;
  readonly duplex?: boolean | null;
  readonly paperSizes: readonly string[];
  readonly trays: readonly string[];
  readonly resolutionDpi?: number | null;
  readonly nativeFormats: readonly string[];
}

/** What a printer last said about itself (plan section 7.10). */
export interface PrinterSample {
  readonly timestamp: string;
  readonly printer: { readonly kioskId: string; readonly printerName: string };
  readonly selected: boolean;
  readonly manufacturer?: string | null;
  readonly model?: string | null;
  readonly serialNumber?: string | null;
  readonly firmware?: string | null;
  readonly driverName?: string | null;
  readonly driverVersion?: string | null;
  readonly connection?: string | null;
  readonly address?: string | null;
  readonly macAddress?: string | null;
  readonly uri?: string | null;
  readonly state?: string | null;
  readonly stateReasons: readonly string[];
  readonly online?: boolean | null;
  readonly lastError?: string | null;
  readonly supplies: readonly PrinterSupplyReading[];
  readonly trays: readonly PrinterTrayReading[];
  readonly lifetimePages?: number | null;
  readonly colorPages?: number | null;
  readonly monoPages?: number | null;
  readonly features?: PrinterFeatures | null;
  readonly spoolerJobs: readonly PrinterSpoolerJob[];
  readonly jobsSeenSince?: Readonly<Record<string, string>> | null;
}

/** GET …/printers/telemetry, GET /api/admin/printers, and `PrinterTelemetryUpdated`'s printers. */
export interface PrinterReading {
  readonly kioskId: string;
  readonly sample: PrinterSample;
  readonly stale: boolean;
}

export interface PrinterTelemetryUpdatedEvent {
  readonly kioskId: string;
  readonly printers: readonly PrinterReading[];
}

export interface PrinterHistoryPoint {
  readonly at: string;
  readonly lifetimePages?: number | null;
  readonly supplyLevels: Readonly<Record<string, number | null>>;
}

export interface PrinterHistoryEvent {
  readonly at: string;
  readonly kind: string;
  readonly subject?: string | null;
  readonly from?: string | null;
  readonly to?: string | null;
  readonly fromLevel?: number | null;
  readonly toLevel?: number | null;
  readonly lifetimePages?: number | null;
}

export type SupplyChannel = 'AdminPanel' | 'OperatorPanel' | 'Email' | 'Telegram';

/** A supply alert rule (plan section 2.15): global, or for some kiosks. */
export interface SupplyRule {
  readonly id: string;
  readonly name: string;
  readonly isGlobal: boolean;
  readonly kioskIds: readonly string[];
  readonly resource: string;
  readonly resourceVariant?: string | null;
  readonly unit: 'Sheets' | 'Percent';
  readonly warningThreshold: number;
  readonly criticalThreshold: number;
  readonly channels: readonly SupplyChannel[];
  readonly createServiceTask: boolean;
  readonly taskPriority: string;
  readonly isActive: boolean;
}

export type SupplyRuleInput = Omit<SupplyRule, 'id' | 'isGlobal'>;

// ---------------------------------------------------------------------------
// The operations pages (step 7A, M9): incidents, service tasks, the warehouse,
// supply rules and the audit log.
// ---------------------------------------------------------------------------

/** One order an incident touched, with its payment and refunds (plan section 9.6). */
export interface AffectedOrder {
  readonly jobId: string;
  readonly jobCode: string;
  readonly status: string;
  readonly registered: boolean;
  readonly reason?: string | null;
  readonly paymentId?: string | null;
  readonly paid?: number | null;
  readonly currency?: string | null;
  readonly paymentStatus?: string | null;
  readonly refunds: readonly {
    readonly id: string;
    readonly status: string;
    readonly amount: number;
    readonly destination: string;
    readonly reason: string;
    readonly createdAt: string;
  }[];
}

/** GET /api/admin/incidents/{id}. */
export interface IncidentDetail {
  readonly incident: {
    readonly id: string;
    readonly type: string;
    readonly severity: string;
    readonly status: string;
    readonly details: string;
    readonly kioskId?: string | null;
    readonly kioskCode?: string | null;
    readonly createdAt: string;
    readonly acknowledgedAt?: string | null;
    readonly acknowledgedBy?: string | null;
    readonly resolvedAt?: string | null;
    readonly resolvedBy?: string | null;
    readonly resolution?: string | null;
  };
  readonly affected: readonly AffectedOrder[];
}

/** A service task with all it records (GET /api/admin/service-tasks). */
export interface ServiceTaskDetail {
  readonly id: string;
  readonly kioskId: string;
  readonly kioskCode?: string | null;
  readonly kioskName?: string | null;
  readonly type: string;
  readonly priority: string;
  readonly status: 'Open' | 'Assigned' | 'InProgress' | 'Done' | 'Cancelled';
  readonly source: string;
  readonly resource?: string | null;
  readonly variant?: string | null;
  readonly assignedTo?: string | null;
  readonly assignedToName?: string | null;
  readonly dueAt?: string | null;
  readonly notes?: string | null;
  readonly createdAt: string;
  readonly assignedAt?: string | null;
  readonly startedAt?: string | null;
  readonly completedAt?: string | null;
  readonly escalatedAt?: string | null;
  readonly cancelReason?: string | null;
  readonly completion?: {
    readonly paper?: string | null;
    readonly sheetsAdded?: number | null;
    readonly inventoryItemId?: string | null;
    readonly quantity?: number | null;
    readonly note?: string | null;
  } | null;
}

export interface ServiceTaskSummary {
  readonly open: number;
  readonly overdue: number;
  readonly unassigned: number;
  readonly completedLastWeek: number;
  readonly averageMinutes?: number | null;
}

export interface ServiceTaskAssignee {
  readonly id: string;
  readonly fullName: string;
  readonly operator: boolean;
  readonly activeTasks: number;
}

export interface InventoryItemInput {
  readonly sku: string;
  readonly name: string;
  readonly type: string;
  readonly compatiblePrinterModels: readonly string[];
  readonly unit: string;
  readonly sheetsPerUnit?: number | null;
  readonly minStock: number;
  readonly location?: string | null;
  readonly isActive: boolean;
}

export interface InventoryMovement {
  readonly id: string;
  readonly itemId: string;
  readonly itemName?: string | null;
  readonly type: string;
  readonly quantity: number;
  readonly stockAfter: number;
  readonly kioskId?: string | null;
  readonly taskId?: string | null;
  readonly by?: string | null;
  readonly at: string;
  readonly note?: string | null;
}

export interface SupplyAlertRow {
  readonly id: string;
  readonly kioskId: string;
  readonly kioskCode?: string | null;
  readonly resource: string;
  readonly variant?: string | null;
  readonly unit: string;
  readonly level: 'Warning' | 'Critical';
  readonly value: number;
  readonly raisedAt: string;
  readonly levelSince?: string | null;
}

export interface AuditLogEntry {
  readonly id: string;
  readonly at: string;
  readonly adminUserId?: string | null;
  readonly adminName?: string | null;
  readonly actorType: string;
  readonly action: string;
  readonly entityType: string;
  readonly entityId?: string | null;
  readonly before?: string | null;
  readonly after?: string | null;
  readonly ip?: string | null;
  readonly reason?: string | null;
  readonly correlationId?: string | null;
}

export interface AuditLogFilter {
  readonly action?: string;
  readonly entityType?: string;
  readonly entityId?: string;
  readonly adminUserId?: string;
  readonly from?: Date | null;
  readonly to?: Date | null;
}

// --- Pilot report (step 7A, M10; plan section 2.17) -----------------------

/** A reason and how often; `text` is the server's short text, missing when it has none. */
export interface PilotReason {
  readonly key: string;
  readonly text?: string;
  readonly count: number;
}

export interface PilotCount {
  readonly key: string;
  readonly count: number;
}

export interface PilotFormat {
  readonly format: string;
  readonly selected: number;
  readonly failed: number;
  /** Percent of the files chosen. */
  readonly share: number;
}

export interface PilotMethod {
  readonly method: string;
  readonly started: number;
  readonly succeeded: number;
}

/** The funnel's steps, in order (`PilotReportHandlers.Steps`). */
export const pilotSteps = ['selected', 'previewed', 'options', 'paymentScreen', 'paid', 'printing', 'printed'] as const;
export type PilotStep = (typeof pilotSteps)[number];

export interface PilotStepRow {
  readonly step: PilotStep;
  readonly count: number;
  /** Percent of the step before that did not get here; missing for the first step. */
  readonly loss?: number;
}

export interface PilotTotals {
  readonly visits: number;
  readonly orders: number;
  readonly printsSucceeded: number;
  readonly printsFailed: number;
  readonly cancelled: number;
  readonly refunds: number;
  readonly refunded: number;
  readonly sales: number;
  readonly net: number;
  /** Missing when no print was measured. */
  readonly averagePrintSeconds?: number;
  readonly paperSheets: number;
  readonly testSheets: number;
  readonly tonerReplaced: number;
  readonly paymentsStarted: number;
  readonly paymentsSucceeded: number;
  readonly incidents: number;
}

export interface PilotDayRow {
  /** `yyyy-MM-dd`, a Baku day. */
  readonly day: string;
  /** Today, worked out on the spot. */
  readonly live: boolean;
  readonly visits: number;
  readonly orders: number;
  readonly printsSucceeded: number;
  readonly printsFailed: number;
  readonly net: number;
  readonly paperSheets: number;
  readonly incidents: number;
}

export interface PilotReport {
  readonly from: string;
  readonly to: string;
  readonly kioskId?: string;
  readonly currency: string;
  /** When the oldest past day shown was worked out; missing when the span is only today. */
  readonly computedAt?: string;
  readonly totals: PilotTotals;
  readonly funnel: readonly PilotStepRow[];
  readonly formats: readonly PilotFormat[];
  /** The app's uploads: only for an account that sees every kiosk, with no kiosk chosen. */
  readonly appFormats?: readonly PilotFormat[];
  readonly paymentFailures: readonly PilotReason[];
  readonly methods: readonly PilotMethod[];
  readonly abandoned: readonly PilotCount[];
  readonly conversionErrors: readonly PilotReason[];
  readonly printErrors: readonly PilotReason[];
  readonly refundReasons: readonly PilotCount[];
  readonly paper: readonly PilotCount[];
  readonly toner: readonly PilotCount[];
  readonly incidents: readonly PilotCount[];
  readonly days: readonly PilotDayRow[];
}
