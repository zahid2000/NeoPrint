/**
 * The shapes of the step 2 admin endpoints: storage, pricing, kiosks and file
 * formats (plan sections 2.2, 2.3, 2.10, 8A.3; docs/API.md).
 *
 * The API leaves null properties out of its JSON, so every nullable field is
 * optional here as well as nullable: read it with `??`, never compare it to
 * `null` alone.
 */

// ---------------------------------------------------------------------------
// Storage
// ---------------------------------------------------------------------------

export type StorageProviderType = 'RustFS' | 'Scality' | 'MinIO';

export const storageProviderTypes: readonly StorageProviderType[] = ['RustFS', 'Scality', 'MinIO'];

export type StorageRole = 'Primary' | 'Secondary' | 'Inactive';

export type StorageHealth = 'Unknown' | 'Healthy' | 'Unhealthy';

export interface StorageBuckets {
  readonly files: string;
  readonly receipts: string;
  readonly temp: string;
}

export interface StorageProvider {
  readonly id: string;
  readonly name: string;
  readonly type: StorageProviderType;
  readonly endpoint: string;
  readonly region: string;
  readonly useSsl: boolean;
  /** The last four characters only; the secret key never reaches the panel. */
  readonly accessKeyHint: string;
  readonly buckets: StorageBuckets;
  readonly role: StorageRole;
  readonly healthStatus: StorageHealth;
  readonly healthMessage?: string | null;
  readonly lastCheckedAt?: string | null;
  readonly isVerified: boolean;
}

/** The add / edit form. An empty secret on edit keeps the stored one. */
export interface StorageProviderInput {
  readonly name: string;
  readonly type: StorageProviderType;
  readonly endpoint: string;
  readonly region: string | null;
  readonly useSsl: boolean;
  readonly accessKey: string;
  readonly secretKey: string | null;
  readonly buckets: StorageBuckets;
}

export interface StorageBucketTest {
  readonly bucket: string;
  readonly succeeded: boolean;
  readonly problem?: string | null;
}

export interface StorageTestResult {
  readonly succeeded: boolean;
  readonly message: string;
  readonly buckets: readonly StorageBucketTest[];
  readonly elapsedMilliseconds: number;
}

export type StorageSyncMode = 'Migration' | 'Mirror';

export type StorageSyncStatus =
  | 'Pending'
  | 'Running'
  | 'Paused'
  | 'Verifying'
  | 'Completed'
  | 'Failed'
  | 'Cancelled';

/** Statuses a job can still move on from. */
export const activeSyncStatuses: readonly StorageSyncStatus[] = [
  'Pending',
  'Running',
  'Paused',
  'Verifying',
];

export interface StorageSyncJob {
  readonly id: string;
  readonly mode: StorageSyncMode;
  readonly sourceId: string;
  readonly sourceName: string;
  readonly targetId: string;
  readonly targetName: string;
  readonly status: StorageSyncStatus;
  readonly totalObjects: number;
  readonly copiedObjects: number;
  readonly failedObjects: number;
  readonly totalBytes: number;
  readonly copiedBytes: number;
  readonly checkpointBucket?: string | null;
  readonly switchPrimaryOnComplete: boolean;
  readonly startedAt?: string | null;
  readonly finishedAt?: string | null;
  readonly failureReason?: string | null;
  readonly sourcePurgeAllowedFrom?: string | null;
  readonly sourcePurgeRequestedAt?: string | null;
  readonly sourcePurgedAt?: string | null;
  readonly sourcePurgedObjects: number;
  readonly sourceKeptObjects: number;
}

/** What `StorageSyncProgress` carries over the admin hub. */
export interface StorageSyncProgress {
  readonly jobId: string;
  readonly mode: StorageSyncMode;
  readonly status: StorageSyncStatus;
  readonly totalObjects: number;
  readonly copiedObjects: number;
  readonly failedObjects: number;
  readonly totalBytes: number;
  readonly copiedBytes: number;
  readonly failureReason?: string | null;
  readonly at: string;
}

/** What `StorageHealthChanged` carries over the admin hub. */
export interface StorageHealthChange {
  readonly providerId: string;
  readonly name: string;
  readonly role: StorageRole;
  readonly status: StorageHealth;
  readonly message?: string | null;
  readonly at: string;
}

export interface StorageSyncFailure {
  readonly bucket: string;
  readonly key: string;
  readonly error: string;
  readonly attempts: number;
  readonly lastAttemptAt: string;
  readonly resolvedAt?: string | null;
}

export type StorageSyncOperation = 'pause' | 'resume' | 'cancel' | 'retry-failed';

// ---------------------------------------------------------------------------
// Pricing
// ---------------------------------------------------------------------------

export interface VolumeTier {
  readonly minSides: number;
  readonly discountPercent: number;
}

export interface PricingProfileInput {
  readonly name: string;
  readonly bwPricePerSide: number;
  readonly colorPricePerSide: number;
  readonly minOrder: number;
  readonly paperMultipliers: Readonly<Record<string, number>>;
  readonly volumeTiers: readonly VolumeTier[];
}

export interface PricingProfile extends PricingProfileInput {
  readonly id: string;
  readonly isDefault: boolean;
  readonly kioskCount: number;
  readonly updatedAt: string;
  readonly revision: number;
}

export interface PricingKiosk {
  readonly id: string;
  readonly code: string;
  readonly name: string;
  readonly pricingProfileId?: string | null;
}

export interface PricingProfileDetail {
  readonly profile: PricingProfile;
  readonly kiosks: readonly PricingKiosk[];
}

export interface PrintOptions {
  readonly pageCount: number;
  readonly copies: number;
  readonly duplex: boolean;
  readonly color: boolean;
  readonly paper: string;
  readonly pagesPerSheet: number;
}

export interface PriceBreakdown {
  readonly pages: number;
  readonly sides: number;
  readonly sheets: number;
  readonly copies: number;
  readonly color: boolean;
  readonly duplex: boolean;
  readonly paper: string;
  readonly pricePerSide: number;
  readonly paperMultiplier: number;
  readonly basePrice: number;
  readonly discountPercent: number;
  readonly volumeDiscount: number;
  readonly promoDiscount: number;
  readonly bonusPages: number;
  readonly minimumApplied: boolean;
  readonly finalPrice: number;
  readonly currency: string;
}

// ---------------------------------------------------------------------------
// Kiosks
// ---------------------------------------------------------------------------

export interface KioskLimit {
  readonly override?: number | null;
  readonly default: number;
  readonly effective: number;
}

export interface PaperStock {
  readonly capacity: number;
  readonly current: number;
  readonly lastRefillAt?: string | null;
}

export interface KioskDetail {
  readonly id: string;
  readonly code: string;
  readonly name: string;
  readonly status: string;
  readonly lat: number;
  readonly lng: number;
  readonly address: string;
  readonly defaultLanguage: string;
  readonly queueDisplayLimit?: number | null;
  readonly maxFileSizeMb: KioskLimit;
  readonly maxPages: KioskLimit;
  readonly pricingProfileId?: string | null;
  readonly pricingProfileName?: string | null;
  readonly fiscalTerminalId?: string | null;
  readonly paper: Readonly<Record<string, PaperStock>>;
  readonly hasAdminPin: boolean;
  readonly activeDeviceId?: string | null;
  readonly platform: string;
  readonly appVersion?: string | null;
  readonly selectedPrinter?: string | null;
  readonly lastHeartbeatAt?: string | null;
  readonly configurationChangedAt: string;
  readonly createdAt: string;
  readonly revision: number;
}

export interface CreateKioskInput {
  readonly name: string;
  readonly code: string | null;
  readonly lat: number;
  readonly lng: number;
  readonly address: string;
  readonly defaultLanguage: string;
  readonly queueDisplayLimit: number | null;
  readonly maxFileSizeMb: number | null;
  readonly maxPages: number | null;
  readonly pricingProfileId: string | null;
  readonly fiscalTerminalId: string | null;
  readonly paperCapacityA4: number;
  readonly paperCapacityA3: number;
  readonly adminPin: string;
}

export interface UpdateKioskInput {
  readonly name: string;
  readonly lat: number;
  readonly lng: number;
  readonly address: string;
  readonly defaultLanguage: string;
  readonly queueDisplayLimit: number | null;
}

export interface KioskDeviceHistoryItem {
  readonly id: string;
  readonly status: string;
  readonly platform: string;
  readonly appVersion?: string | null;
  readonly hostname?: string | null;
  readonly activatedAt?: string | null;
  readonly revokedAt?: string | null;
  readonly revokeReason?: string | null;
  readonly lastSeenAt: string;
  readonly isActive: boolean;
}

// ---------------------------------------------------------------------------
// File formats
// ---------------------------------------------------------------------------

export type FormatGroup =
  | 'Pdf'
  | 'TextDocument'
  | 'Spreadsheet'
  | 'Presentation'
  | 'Image'
  | 'PageDescription';

export interface FileFormatItem {
  readonly extension: string;
  readonly mediaType: string;
  readonly group: FormatGroup;
  readonly route: string;
  readonly printerNative: boolean;
  readonly enabled: boolean;
}

export interface FileFormats {
  readonly formats: readonly FileFormatItem[];
  readonly defaultMaxSizeMb: number;
  readonly defaultMaxPages: number;
  readonly userQuotaGb: number;
}

/** The dashboard's file figures (GET /api/admin/dashboard). */
export interface DashboardFiles {
  readonly count: number;
  readonly totalBytes: number;
  readonly inProgress: number;
  readonly failedLast24Hours: number;
  readonly failedLast7Days: number;
}

/** A storage provider as the dashboard shows it: no endpoint, no key. */
export interface DashboardStorageProvider {
  readonly id: string;
  readonly name: string;
  readonly type: StorageProviderType;
  readonly role: string;
  readonly healthStatus: string;
  readonly healthMessage?: string;
  readonly lastCheckedAt?: string;
}

export interface DashboardStorage {
  readonly providers: readonly DashboardStorageProvider[];
  readonly activeSync?: StorageSyncJob;
  readonly lastSync?: StorageSyncJob;
}

/** Storage is present only for accounts that may manage it. */
export interface AdminDashboard {
  readonly files: DashboardFiles;
  readonly storage?: DashboardStorage;
}
