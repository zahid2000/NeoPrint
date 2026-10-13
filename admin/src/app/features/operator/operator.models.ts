import { PaperRefillMode } from '../../core/api/step3.models';
import { IncidentRow } from '../../core/api/step4.models';

/** A tray's counter as the operator panel is told it. */
export interface OperatorPaper {
  readonly capacity: number;
  readonly current: number;
  readonly lastRefillAt?: string | null;

  /** False until the first refill is recorded: the count is unknown, not nought (T-258). */
  readonly counted?: boolean;
}

/** What the selected printer last said about itself. */
export interface OperatorPrinter {
  readonly name: string;
  readonly online?: boolean | null;
  readonly state?: string | null;

  /** Why it is not printing, in the printer's own words ("media-empty"). */
  readonly reasons: readonly string[];
  readonly lastError?: string | null;
  readonly readAt: string;

  /** The kiosk has not reported its printers for a while. */
  readonly stale: boolean;
}

export interface SupplyLevel {
  readonly resource: string;
  readonly variant?: string | null;
  readonly name: string;
  readonly percent: number;
  readonly readAt: string;
}

export interface SupplyForecast {
  readonly resource: string;
  readonly variant?: string | null;
  readonly unit: string;
  readonly current: number;
  readonly perDay: number;
  readonly runsOutAt?: string | null;
}

export interface SupplyAlert {
  readonly id: string;
  readonly kioskId: string;
  readonly kioskCode?: string | null;
  readonly resource: string;
  readonly variant?: string | null;
  readonly unit: string;
  readonly level: 'Warning' | 'Critical';
  readonly value: number;
  readonly raisedAt: string;
}

/** One kiosk's health card (GET /api/admin/operator/kiosks). */
export interface OperatorKioskCard {
  readonly id: string;
  readonly code: string;
  readonly name: string;
  readonly address: string;
  readonly status: string;
  readonly safeModeSince?: string | null;
  readonly lastHeartbeatAt?: string | null;
  readonly printer?: OperatorPrinter | null;
  readonly paper: Readonly<Record<string, OperatorPaper>>;
  readonly levels: readonly SupplyLevel[];
  readonly forecasts: readonly SupplyForecast[];
  readonly alerts: readonly SupplyAlert[];
  readonly queueLength: number;
  readonly openIncidents: number;
  readonly worstIncident?: string | null;
  readonly openTasks: number;

  /** How much the kiosk needs somebody; the server sorts by it. */
  readonly attention: number;
}

/** What the kiosk's machine last reported, cut down to what a phone shows. */
export interface OperatorTelemetry {
  readonly sample?: {
    readonly timestamp: string;
    readonly app?: { readonly version?: string | null };
    readonly system?: {
      readonly cpuPercent?: number | null;
      readonly memoryUsedBytes?: number | null;
      readonly memoryTotalBytes?: number | null;
      readonly systemDiskFreeBytes?: number | null;
      readonly uptimeSeconds?: number | null;
    };
    readonly network?: {
      readonly connectionType?: string | null;
      readonly backendLatencyMs?: number | null;
    };
  } | null;
  readonly stale: boolean;
}

export type ServiceTaskStatus = 'Open' | 'Assigned' | 'InProgress' | 'Done' | 'Cancelled';

export interface ServiceTask {
  readonly id: string;
  readonly kioskId: string;
  readonly kioskCode?: string | null;
  readonly kioskName?: string | null;
  readonly type: string;
  readonly priority: string;
  readonly status: ServiceTaskStatus;
  readonly source: string;
  readonly resource?: string | null;
  readonly variant?: string | null;
  readonly assignedTo?: string | null;
  readonly assignedToName?: string | null;
  readonly dueAt?: string | null;

  /** What is to be done, already in the reader's language. */
  readonly notes?: string | null;
  readonly createdAt: string;
  readonly startedAt?: string | null;
}

export interface OperatorKioskDetail {
  readonly kiosk: OperatorKioskCard;
  readonly telemetry?: OperatorTelemetry | null;
  readonly incidents: readonly IncidentRow[];
  readonly tasks: readonly ServiceTask[];
}

export interface InventoryItem {
  readonly id: string;
  readonly sku: string;
  readonly name: string;
  readonly type: string;
  readonly compatiblePrinterModels: readonly string[];
  readonly unit: string;
  readonly sheetsPerUnit?: number | null;
  readonly minStock: number;
  readonly stock: number;
  readonly location: string;
  readonly isActive: boolean;
  readonly isLow: boolean;
}

/** What "Kağız doldur" sends. */
export interface RefillRequest {
  readonly paper: string;
  readonly mode: PaperRefillMode;
  readonly sheets: number;
  readonly inventoryItemId?: string;
  readonly quantity?: number;
}

/** What "Toner dəyişdirildi" sends. */
export interface ReplacedRequest {
  readonly resource: string;
  readonly variant?: string;
  readonly inventoryItemId?: string;
  readonly quantity?: number;
  readonly note?: string;
}

/** What "Tamamla" sends. */
export interface CompleteTaskRequest {
  readonly paper?: string;
  readonly refillMode?: PaperRefillMode;
  readonly sheets?: number;
  readonly inventoryItemId?: string;
  readonly quantity?: number;
  readonly note?: string;
}

/** The tasks an operator still has something to do with. */
export const openTaskStatuses: readonly ServiceTaskStatus[] = ['Open', 'Assigned', 'InProgress'];

/** The warehouse item types that hold paper for a tray of this size. */
export const paperItemType: Readonly<Record<string, string>> = { A4: 'PaperA4Ream', A3: 'PaperA3Ream' };
