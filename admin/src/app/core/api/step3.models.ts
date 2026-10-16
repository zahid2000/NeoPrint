/**
 * The shapes of the step 3 endpoints: printers and commands, print jobs and
 * the queue, paper (docs/API.md). Field names follow the API's camelCase JSON.
 */

import type { SupplyAlert, SupplyForecast, SupplyLevel } from '../../features/operator/operator.models';

export interface PrinterCapabilities {
  readonly color: boolean;
  readonly duplex: boolean;
  readonly a3: boolean;
}

export interface CapabilityOverrides {
  readonly color: boolean | null;
  readonly duplex: boolean | null;
  readonly a3: boolean | null;
}

export interface AvailablePrinter {
  readonly name: string;
  readonly driverName: string | null;
  readonly isOnline: boolean;
  readonly detectedCapabilities: PrinterCapabilities;
}

/** The printers tab: what the kiosk found, what is chosen, what may be offered. */
export interface KioskPrinters {
  readonly kioskId: string;
  readonly status: string;
  readonly selectedPrinter: string | null;
  readonly printers: readonly AvailablePrinter[];
  readonly detected: PrinterCapabilities;
  readonly overrides: CapabilityOverrides;
  readonly effective: PrinterCapabilities;
}

export type KioskCommandType = 'RefreshPrinters' | 'TestPrint';

export interface KioskCommand {
  readonly id: string;
  readonly kioskId: string;
  readonly type: string;
  readonly status: string;
  /** A message key the backend has already written in the reader's language, or null. */
  readonly result: string | null;
  readonly createdAt: string;
  readonly completedAt: string | null;
}

export interface QueueEntry {
  readonly jobId: string;
  readonly jobCode: string;
  readonly position: number;
  readonly status: string;
  readonly pages: number;
  readonly copies: number;
  readonly userInitial: string | null;
  readonly fileName: string | null;
  readonly queuedAt: string | null;
}

export interface PrintJobListItem {
  readonly id: string;
  readonly jobCode: string;
  readonly kioskId: string;
  readonly kioskCode: string | null;
  readonly status: string;
  readonly source: string;
  readonly fileName: string | null;
  readonly selectedPages: number;
  readonly copies: number;
  readonly total: number;
  readonly currency: string;
  readonly paymentSkipped: boolean;
  readonly createdAt: string;
  readonly finishedAt: string | null;
}

export interface PrintJobPage {
  readonly items: readonly PrintJobListItem[];
  readonly total: number;
  readonly page: number;
  readonly pageSize: number;
}

export interface PrintJobFilter {
  readonly kioskId?: string | null;
  readonly statuses?: readonly string[];
  readonly from?: string | null;
  readonly to?: string | null;
  readonly code?: string | null;
  readonly page: number;
  readonly pageSize: number;
}

export interface PrintOptionsView {
  readonly copies: number;
  readonly pageRange: string | null;
  readonly color: boolean;
  readonly duplex: boolean;
  readonly paper: string;
  readonly orientation: string;
  readonly pagesPerSheet: number;
  readonly scaling: string;
}

export interface PriceView {
  readonly total: number;
  readonly currency: string;
  readonly basePrice: number;
  readonly volumeDiscount: number;
  readonly discountPercent: number;
  readonly minimumApplied: boolean;
  readonly sides: number;
  readonly sheets: number;
  readonly profileName: string;
  readonly takenAt: string;
}

export interface PrintJob {
  readonly id: string;
  readonly jobCode: string;
  readonly kioskId: string;
  readonly status: string;
  readonly source: string;
  readonly fileName: string | null;
  readonly pageCount: number;
  readonly selectedPages: number;
  readonly options: PrintOptionsView;
  readonly price: PriceView;
  readonly requiredSheets: number;
  readonly queuePosition: number | null;
  readonly printedSides: number;
  readonly printedSheets: number;
  /** Already written in the reader's language. */
  readonly errorReason: string | null;
  readonly cancelReason: string | null;
  readonly paymentSkipped: boolean;
  readonly createdAt: string;
  readonly queuedAt: string | null;
  readonly finishedAt: string | null;
}

export interface PrintJobTransition {
  readonly from: string;
  readonly to: string;
  readonly actorType: string;
  readonly reason: string | null;
  readonly at: string;
}

/** One try at printing: the evidence when nobody knows whether it printed (plan section 2.5). */
export interface PrintAttempt {
  readonly number: number;
  readonly printer: string | null;
  readonly spoolerJobId: string | null;
  readonly startedAt: string | null;
  readonly finishedAt: string | null;
  readonly counterBefore: number | null;
  readonly counterAfter: number | null;
  readonly counterUnit: 'sheets' | 'impressions' | null;
  readonly reportedSides: number;
  readonly reportedSheets: number;
  readonly status: string;
  readonly error: string | null;
  readonly recovered: boolean;
}

export interface PrintJobDetail {
  readonly job: PrintJob;
  readonly kioskCode: string | null;
  readonly history: readonly PrintJobTransition[];
  readonly attempts: readonly PrintAttempt[] | null;
}

export interface PaperStock {
  readonly capacity: number;
  readonly current: number;
  readonly lastRefillAt: string | null;
  /** False until the first refill is recorded: the count is then unknown, not nought (T-258). */
  readonly counted?: boolean;
}

export interface SupplyEvent {
  readonly resource: string;
  readonly type: 'JobPrinted' | 'TestPrint' | 'Refill' | 'Recount';
  readonly delta: number;
  readonly before: number;
  readonly after: number;
  readonly jobId: string | null;
  readonly by: string | null;
  readonly at: string;
}

export interface KioskSupplies {
  readonly paper: Readonly<Record<string, PaperStock>>;
  readonly events: readonly SupplyEvent[];

  /** What the selected printer says of its toner, drum and the rest (step 7A). */
  readonly levels?: readonly SupplyLevel[];

  /** When each supply runs out at the rate of the last fortnight. */
  readonly forecasts?: readonly SupplyForecast[];

  /** The supplies below their rule now. */
  readonly alerts?: readonly SupplyAlert[];
}

/** Below this share of the tray the counter is shown as running low (plan section 2.15). */
export const lowPaperShare = 0.2;

export type PaperRefillMode = 'Add' | 'Full' | 'Exact';

/** The statuses a job can have, in the order the filter offers them. */
export const printJobStatuses = [
  'PendingPayment',
  'Paid',
  'Queued',
  'Printing',
  'Completed',
  'Interrupted',
  'Suspected',
  'Cancelled',
  'Expired',
] as const;

/** A job still waiting for the printer can be taken out of the queue; one at the printer cannot. */
export const cancellableStatuses: readonly string[] = ['Draft', 'PendingPayment', 'Queued', 'Interrupted'];
