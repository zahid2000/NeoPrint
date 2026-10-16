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
