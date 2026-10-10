/**
 * Step 4 in the panel (plan section 8A.3): payments, refunds, fiscal
 * receipts, receipts, vouchers, promo codes, customers, suspected jobs and
 * the money figures. The shapes are the API's (docs/API.md).
 */

import type { CustomerFile, CustomerReferrals } from './step6.models';

export interface Page<T> {
  readonly items: readonly T[];
  readonly total: number;
  readonly page: number;
  readonly pageSize: number;
}

// ---------------------------------------------------------------------------
// Payments
// ---------------------------------------------------------------------------

export const paymentStatuses = [
  'Pending',
  'Succeeded',
  'Failed',
  'Expired',
  'Refunded',
  'PartiallyRefunded',
  'RefundFailed',
] as const;

export const paymentMethods = ['BankQr', 'MobileApp', 'Wallet', 'Card', 'Bonus', 'Mixed'] as const;

/** Where the payment was started (plan section 8A.3, "kanal"). */
export const paymentChannels = ['Kiosk', 'Mobile'] as const;

export const refundReasons = [
  'SystemError',
  'UserCancel',
  'ScheduleCancel',
  'AdminManual',
  'PartialPrint',
  'LatePayment',
  'DuplicatePayment',
  'NotCollected',
] as const;

export interface PaymentFilter {
  readonly statuses?: readonly string[];
  readonly methods?: readonly string[];
  readonly channel?: string | null;
  readonly purpose?: string | null;
  readonly kioskId?: string | null;
  readonly from?: string | null;
  readonly to?: string | null;
  readonly hadError?: boolean;
  /** `pending` or `failed`. */
  readonly refund?: string | null;
  readonly search?: string;
  readonly page: number;
  readonly pageSize: number;
}

export interface PaymentRow {
  readonly id: string;
  readonly status: string;
  readonly method: string;
  readonly channel: string;
  readonly purpose: string;
  readonly amount: number;
  readonly refunded: number;
  readonly currency: string;
  readonly kioskId?: string;
  readonly kioskCode?: string;
  readonly jobId?: string;
  readonly jobCode?: string;
  readonly userId?: string;
  readonly failureReason?: string;
  readonly createdAt: string;
  readonly paidAt?: string;
}

export interface PaymentSplit {
  readonly gross: number;
  readonly volumeDiscount: number;
  readonly promoDiscount: number;
  readonly promoCode?: string;
  readonly external: number;
  readonly wallet: number;
  readonly bonusPages: number;
  readonly bonusValue: number;
}

export interface PaymentFiscal {
  readonly id: string;
  readonly type: string;
  readonly status: string;
  readonly amount: number;
  readonly fiscalId?: string;
  readonly terminalId?: string;
  readonly lastError?: string;
  readonly issuedAt?: string;
}

export interface PaymentRefund {
  readonly id: string;
  readonly amount: number;
  readonly bonusPages: number;
  readonly destination: string;
  readonly status: string;
  readonly reason: string;
  readonly initiatedBy: string;
  readonly attempts: number;
  readonly lastError?: string;
  readonly voucherCode?: string;
  readonly note?: string;
  readonly createdAt: string;
  readonly completedAt?: string;
}

export interface PaymentDetail {
  readonly payment: PaymentRow;
  readonly split: PaymentSplit;
  readonly refundable: number;
  readonly provider?: string;
  readonly providerRef?: string;
  readonly expiresAt: string;
  readonly kioskName?: string;
  readonly job?: {
    readonly id: string;
    readonly jobCode: string;
    readonly status: string;
    readonly fileName?: string;
    readonly pages: number;
    readonly copies: number;
    readonly color: boolean;
    readonly paper: string;
  };
  readonly user?: { readonly id: string; readonly publicId: string; readonly email: string };
  readonly receiptNumber?: string;
  readonly fiscal: readonly PaymentFiscal[];
  readonly refunds: readonly PaymentRefund[];
  readonly timeline: readonly {
    readonly at: string;
    readonly event: string;
    readonly details?: string;
  }[];
}

export interface RefundOptions {
  readonly paymentId: string;
  readonly method: string;
  readonly hasAccount: boolean;
  readonly refundable: number;
  readonly externalRefundable: number;
  readonly walletRefundable: number;
  readonly bonusPagesRefundable: number;
  readonly externalDestinations: readonly string[];
  readonly jobCancellable: boolean;
}

export interface AdminRefundInput {
  /** Null for everything still refundable. */
  readonly amount: number | null;
  readonly destination: string | null;
  readonly reason: string;
  readonly cancelJob: boolean;
}

// ---------------------------------------------------------------------------
// Refunds, fiscal receipts, receipts, vouchers
// ---------------------------------------------------------------------------

export const refundStatuses = ['Pending', 'Processing', 'Succeeded', 'Failed'] as const;

export interface RefundRow {
  readonly id: string;
  readonly paymentId: string;
  readonly jobId?: string;
  readonly userId?: string;
  readonly amount: number;
  readonly bonusPages: number;
  readonly destination: string;
  readonly reason: string;
  readonly initiatedBy: string;
  readonly status: string;
  readonly attempts: number;
  readonly nextAttemptAt?: string;
  readonly lastError?: string;
  readonly voucherCode?: string;
  readonly createdAt: string;
  readonly completedAt?: string;
}

export interface FiscalRow {
  readonly id: string;
  readonly type: string;
  readonly status: string;
  readonly paymentId?: string;
  readonly refundId?: string;
  readonly terminalId?: string;
  readonly amount: number;
  readonly currency: string;
  readonly fiscalId?: string;
  readonly attempts: number;
  readonly nextAttemptAt?: string;
  readonly lastError?: string;
  readonly createdAt: string;
  readonly issuedAt?: string;
}

/** An incident as the panel lists it (GET /api/admin/incidents, T-129). */
export interface IncidentRow {
  readonly id: string;
  readonly type: string;
  readonly severity: string;
  readonly status: string;
  /** What happened and what to do, already in the reader's language. */
  readonly details: string;
  readonly entityType?: string;
  readonly entityId?: string;
  readonly kioskId?: string;
  readonly createdAt: string;
  readonly resolvedAt?: string;
  readonly resolution?: string;
}

export interface ReceiptRow {
  readonly id: string;
  readonly number: string;
  readonly paymentId: string;
  readonly jobId?: string;
  readonly kioskId?: string;
  readonly amount: number;
  readonly currency: string;
  readonly purpose: string;
  readonly fiscalStatus: string;
  readonly fiscalShown: boolean;
  readonly emailedCount: number;
  readonly createdAt: string;
}

export interface VoucherRow {
  readonly id: string;
  readonly code: string;
  readonly amount: number;
  readonly currency: string;
  readonly status: string;
  readonly paymentId: string;
  readonly refundId: string;
  readonly createdAt: string;
  readonly expiresAt: string;
  readonly redeemedBy?: string;
  readonly redeemedAt?: string;
}

// ---------------------------------------------------------------------------
// Promo codes
// ---------------------------------------------------------------------------

export interface Promo {
  readonly id: string;
  readonly code: string;
  readonly kind: 'Percent' | 'Fixed';
  readonly value: number;
  readonly startsAt?: string;
  readonly endsAt?: string;
  readonly totalLimit?: number;
  readonly perUserLimit?: number;
  readonly minAmount: number;
  readonly kioskIds: readonly string[];
  readonly profileIds: readonly string[];
  readonly isActive: boolean;
  readonly usedCount: number;
  readonly confirmedCount: number;
  readonly totalDiscount: number;
  readonly description?: string;
  readonly createdAt: string;
}

export interface PromoInput {
  readonly code: string;
  readonly kind: 'Percent' | 'Fixed';
  readonly value: number;
  readonly startsAt: string | null;
  readonly endsAt: string | null;
  readonly totalLimit: number | null;
  readonly perUserLimit: number | null;
  readonly minAmount: number;
  readonly kioskIds: readonly string[];
  readonly profileIds: readonly string[];
  readonly description: string | null;
}

// ---------------------------------------------------------------------------
// Customers and wallets
// ---------------------------------------------------------------------------

export interface CustomerRow {
  readonly id: string;
  readonly publicId: string;
  readonly email: string;
  readonly fullName?: string;
  readonly status: string;
  readonly emailConfirmed: boolean;
  readonly createdAt: string;
  readonly blockedAt?: string;
}

export interface CustomerJob {
  readonly id: string;
  readonly jobCode: string;
  readonly status: string;
  readonly kioskCode?: string;
  readonly fileName?: string;
  readonly pages: number;
  readonly copies: number;
  readonly amount: number;
  readonly createdAt: string;
}

export interface CustomerDetail {
  readonly profile: CustomerRow;
  readonly language: string;
  readonly wallet: Wallet;
  readonly recentJobs: readonly CustomerJob[];
  readonly fileCount: number;
  readonly fileBytes: number;
  readonly referred: number;
  readonly consents: readonly { readonly docType: string; readonly version: string }[];
  /** The newest files as metadata, names masked by the server (step 6). */
  readonly files: readonly CustomerFile[];
  readonly referrals: CustomerReferrals;
}

export interface Wallet {
  readonly balance: number;
  readonly bonusPages: number;
  readonly currency: string;
}

export interface WalletEntry {
  readonly id: string;
  readonly type: string;
  readonly amount: number;
  readonly bonusPages: number;
  readonly balanceAfter: number;
  readonly bonusPagesAfter: number;
  readonly createdAt: string;
  readonly note?: string;
}

// ---------------------------------------------------------------------------
// Suspected jobs and claims
// ---------------------------------------------------------------------------

export interface SuspectedJob {
  readonly jobId: string;
  readonly jobCode: string;
  readonly kioskId: string;
  readonly kioskCode?: string;
  readonly suspectedAt?: string;
  readonly kioskLastHeartbeatAt?: string;
  readonly amount: number;
  readonly currency: string;
  readonly paymentMethod?: string;
  readonly hasAccount: boolean;
  readonly openClaims: number;
  readonly overdue: boolean;
}

export interface Claim {
  readonly id: string;
  readonly channel: string;
  readonly jobId?: string;
  readonly jobCode?: string;
  readonly kioskId?: string;
  readonly userId?: string;
  readonly text: string;
  readonly contact?: string;
  readonly status: string;
  readonly reply?: string;
  readonly repliedAt?: string;
  readonly createdAt: string;
}

export interface SuspectedInvestigation {
  readonly detail: {
    readonly job: {
      readonly id: string;
      readonly jobCode: string;
      readonly status: string;
      readonly fileName?: string;
      readonly selectedPages: number;
      readonly requiredSheets: number;
      readonly printedSides: number;
      readonly printedSheets: number;
      readonly options: {
        readonly copies: number;
        readonly color: boolean;
        readonly duplex: boolean;
        readonly paper: string;
      };
      readonly createdAt: string;
    };
    readonly kioskCode?: string;
    readonly attempts?: readonly {
      readonly number: number;
      readonly printer?: string;
      readonly spoolerJobId?: string;
      readonly counterBefore?: number;
      readonly counterAfter?: number;
      readonly startedAt?: string;
      readonly finishedAt?: string;
      readonly recovered?: boolean;
    }[];
  };
  readonly suspectedAt?: string;
  readonly overdue: boolean;
  readonly kioskStatus?: string;
  readonly kioskLastHeartbeatAt?: string;
  readonly payment?: {
    readonly id: string;
    readonly method: string;
    readonly status: string;
    readonly amount: number;
    readonly providerRef?: string;
    readonly refundable: number;
    readonly externalDestinations: readonly string[];
  };
  readonly claims: readonly Claim[];
  readonly kioskIncidents: readonly {
    readonly id: string;
    readonly type: string;
    readonly severity: string;
    readonly status: string;
    readonly details: string;
    readonly createdAt: string;
  }[];
  readonly kioskJobs: readonly {
    readonly id: string;
    readonly jobCode: string;
    readonly status: string;
    readonly createdAt: string;
    readonly finishedAt?: string;
  }[];
}

export const suspectedDecisions = ['FullRefund', 'PartialRefund', 'NoRefund', 'Reprint'] as const;

export interface SuspectedDecisionInput {
  readonly decision: (typeof suspectedDecisions)[number];
  readonly amount: number | null;
  readonly destination: string | null;
  readonly reason: string;
  readonly note: string;
}

// ---------------------------------------------------------------------------
// Money figures
// ---------------------------------------------------------------------------

export interface RevenueTotals {
  readonly sales: number;
  readonly payments: number;
  readonly refunds: number;
  readonly net: number;
  readonly discounts: number;
  readonly topUps: number;
}

export interface RevenueDay {
  readonly day: string;
  readonly sales: number;
  readonly refunds: number;
  readonly net: number;
  readonly payments: number;
}

export interface RevenueReport {
  readonly from: string;
  readonly to: string;
  readonly currency: string;
  readonly totals: RevenueTotals;
  readonly days: readonly RevenueDay[];
  readonly methods: readonly {
    readonly method: string;
    readonly sales: number;
    readonly payments: number;
  }[];
  readonly kiosks: readonly {
    readonly kioskId: string;
    readonly kioskCode?: string;
    readonly sales: number;
    readonly payments: number;
  }[];
}

export interface PaymentsDashboard {
  readonly today: RevenueTotals;
  readonly lastSevenDays: readonly RevenueDay[];
  readonly failedRefunds: number;
  readonly failedFiscal: number;
  readonly openMoneyIncidents: number;
}
