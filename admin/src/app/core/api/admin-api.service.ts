import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { PermissionGroup } from '../auth/auth.models';
import {
  CreateKioskInput,
  FileFormats,
  KioskDetail,
  KioskDeviceHistoryItem,
  PriceBreakdown,
  PricingProfile,
  PricingProfileDetail,
  PricingProfileInput,
  PrintOptions,
  StorageProvider,
  StorageProviderInput,
  StorageRole,
  StorageSyncFailure,
  StorageSyncJob,
  StorageSyncMode,
  StorageSyncOperation,
  StorageTestResult,
  UpdateKioskInput,
  AdminDashboard,
} from './step2.models';
import {
  CapabilityOverrides,
  KioskCommand,
  KioskPrinters,
  KioskSupplies,
  PaperRefillMode,
  PrintJob,
  PrintJobDetail,
  PrintJobFilter,
  PrintJobPage,
  QueueEntry,
} from './step3.models';

/**
 * A fresh Idempotency-Key for one action (docs/IDEMPOTENCY.md). The endpoints
 * marked idempotent answer a repeated request with the first answer instead
 * of doing the work twice - a double click, or a retry after a timeout.
 */
function idempotent(): { headers: HttpHeaders } {
  return { headers: new HttpHeaders({ 'Idempotency-Key': crypto.randomUUID() }) };
}

/**
 * The reason an administrator gave for a destructive action, for the audit
 * log (plan section 8A.1). URL-encoded: header values are not UTF-8, and a
 * reason is usually written in Azerbaijani.
 */
function withReason(reason: string | null | undefined): { headers: HttpHeaders } {
  const trimmed = reason?.trim();

  return {
    headers: trimmed
      ? new HttpHeaders({ 'X-Audit-Reason': encodeURIComponent(trimmed) })
      : new HttpHeaders(),
  };
}

/** Where a device is in its lifecycle (plan section 7.11). */
export type DeviceStatus = 'Pending' | 'Active' | 'Revoked' | 'Expired';

/** Which operating system the bound device runs. */
export type KioskPlatform = 'Unknown' | 'Windows' | 'Linux';

/** Where a kiosk is in its lifecycle (plan section 7.11). */
export type KioskStatus =
  | 'NotActivated'
  | 'NotConfigured'
  | 'Online'
  | 'Paused'
  | 'Offline'
  | 'Maintenance'
  | 'Disabled';

/** A kiosk row, as the activation wizard offers it (plan section 8A.3). */
export interface KioskListItem {
  readonly id: string;
  readonly code: string;
  readonly name: string;
  readonly status: KioskStatus;
  readonly platform: KioskPlatform;
  readonly address: string;
  readonly defaultLanguage: string;
  readonly activeDeviceId: string | null;
  readonly appVersion: string | null;
  readonly selectedPrinter: string | null;
  readonly lastHeartbeatAt: string | null;
  readonly lat?: number;
  readonly lng?: number;
  readonly pricingProfileId?: string | null;
  /** Since when the kiosk runs in safe mode after a crash loop (plan section 7.12). */
  readonly safeModeSince?: string | null;
  readonly paperA4Current?: number | null;
  readonly paperA4Capacity?: number | null;
  /** False while nobody has recorded what is in the A4 tray (T-258). */
  readonly paperA4Counted?: boolean | null;
}

/** An administrator account as the panel lists it (plan section 8A.3). */
export interface AdminUser {
  readonly id: string;
  readonly email: string;
  readonly fullName: string;
  readonly roleIds: readonly string[];
  readonly roleNames: readonly string[];
  readonly allKiosks: boolean;
  readonly kioskIds: readonly string[];
  readonly isActive: boolean;
  readonly mustChangePassword: boolean;
  readonly isLockedOut: boolean;
  readonly lastLoginAt: string | null;
  readonly createdAt: string;
}

/** How a setting is typed, which decides the input the panel shows. */
export type SettingValueType =
  | 'String'
  | 'Integer'
  | 'Decimal'
  | 'Boolean'
  | 'StringList'
  | 'Cron'
  | 'TimeRange';

/** One platform setting, with its documented default alongside. */
export interface PlatformSetting {
  readonly key: string;
  readonly value: string;
  readonly defaultValue: string;
  readonly valueType: SettingValueType;
  readonly description: string;
  readonly isDefault: boolean;
  readonly updatedAt: string | null;
  readonly updatedBy: string | null;
}

export type LegalDocumentType = 'Privacy' | 'Terms';

/** A published version of a legal document (plan section 2.13). */
export interface LegalDocument {
  readonly id: string;
  readonly docType: LegalDocumentType;
  readonly version: string;
  readonly language: string;
  readonly content: string;
  readonly publishedAt: string;
  readonly publishedBy: string | null;
  readonly isCurrent: boolean;
  readonly acceptances: number;
}

/** A device row, as the Devices page lists it (plan section 8A.3). */
export interface DeviceListItem {
  readonly id: string;
  readonly status: DeviceStatus;
  readonly platform: KioskPlatform;
  readonly appVersion: string | null;
  readonly agentVersion: string | null;
  readonly hostname: string | null;
  readonly os: string | null;
  readonly lastIp: string | null;
  readonly registeredAt: string;
  readonly lastSeenAt: string;
  readonly kioskId: string | null;
  readonly kioskCode: string | null;

  /**
   * Whether the code on the kiosk screen is still valid. The code itself is
   * only ever stored hashed, so the panel cannot show it - the operator reads
   * it off the kiosk (plan section 7.11).
   */
  readonly hasValidPairingCode: boolean;
  readonly pairingCodeExpiresAt: string | null;
  readonly reportedPrinters: readonly string[];
}

/** What the activation wizard shows before binding a device. */
export interface DeviceDetails {
  readonly deviceId: string;
  readonly status: DeviceStatus;
  readonly platform: KioskPlatform;
  readonly appVersion: string | null;
  readonly fingerprint: {
    readonly hostname: string | null;
    readonly os: string | null;
    readonly osVersion: string | null;
    readonly machineId: string | null;
    readonly macAddresses: readonly string[] | null;
    readonly cpu: string | null;
    readonly ramGb: number;
    readonly screenResolution: string | null;
  };
  readonly printers: readonly {
    readonly name: string;
    readonly driverName: string | null;
    readonly isOnline: boolean;
    readonly color: boolean;
    readonly duplex: boolean;
    readonly a3: boolean;
  }[];
  readonly lastIp: string | null;
  readonly registeredAt: string;
  readonly lastSeenAt: string;
  readonly kioskId: string | null;
  readonly kioskCode: string | null;
}

/** Parameters for a kiosk created during activation (plan section 7.11). */
export interface NewKioskParameters {
  readonly name: string;
  readonly code?: string | null;
  readonly lat: number;
  readonly lng: number;
  readonly address: string;
  readonly pricingProfileId?: string | null;
  readonly maxFileSizeMb?: number | null;
  readonly maxPages?: number | null;
  readonly queueDisplayLimit?: number | null;
  readonly defaultLanguage: string;
  readonly fiscalTerminalId?: string | null;
  readonly paperCapacityA4: number;
  readonly paperCapacityA3: number;
  readonly adminPin: string;
}

export interface ActivateDeviceResult {
  readonly deviceId: string;
  readonly kioskId: string;
  readonly kioskCode: string;
  readonly kioskStatus: string;
}

/**
 * A freshly created enrollment token.
 *
 * The only time the raw token is returned; the server stores nothing but its
 * hash (plan section 7.11).
 */
export interface EnrollmentTokenResult {
  readonly id: string;
  readonly token: string;
  readonly expiresAt: string;
  readonly kioskId: string | null;
  readonly kioskCode: string | null;
  readonly windowsCommand: string;
  readonly linuxCommand: string;
}

export interface EnrollmentTokenListItem {
  readonly id: string;
  readonly kioskId: string | null;
  readonly kioskCode: string | null;
  readonly newKioskName: string | null;
  readonly createdAt: string;
  readonly expiresAt: string;
  readonly usedByDeviceId: string | null;
  readonly revokedAt: string | null;
  readonly isUsable: boolean;
}

/** A role with its permissions (plan section 8A.2). */
export interface RoleResponse {
  readonly id: string;
  readonly name: string;
  readonly description: string | null;
  readonly permissions: readonly string[];
  readonly isSystem: boolean;
  readonly userCount: number;
  readonly updatedAt: string;
}

/** A feature flag with its configuration (plan section 2.16). */
export interface FeatureFlagDetails {
  readonly id: string;
  readonly key: string;
  readonly description: string | null;
  readonly defaultEnabled: boolean;
  readonly platforms: readonly string[];
  readonly kioskOverrides: Readonly<Record<string, boolean>>;
  readonly userAllowlist: readonly string[];
  readonly rolloutPercent: number;
  readonly updatedAt: string;
}

/**
 * The admin API (plan section 6).
 *
 * One service for the whole surface, so every call sits behind the same
 * interceptor and nothing reaches the network with a hand-rolled client.
 */
@Injectable({ providedIn: 'root' })
export class AdminApiService {
  private readonly http = inject(HttpClient);

  // ---------------------------------------------------------------------
  // Printers and commands (plan sections 7.7, 7.8; kiosk detail → Printers)
  // ---------------------------------------------------------------------

  kioskPrinters(kioskId: string): Observable<KioskPrinters> {
    return this.http.get<KioskPrinters>(`/api/admin/kiosks/${kioskId}/printers`);
  }

  selectKioskPrinter(kioskId: string, name: string): Observable<KioskPrinters> {
    return this.http.put<KioskPrinters>(`/api/admin/kiosks/${kioskId}/printer`, { name });
  }

  /** Only switches capabilities off: null follows what the printer reports. */
  setKioskCapabilities(kioskId: string, overrides: CapabilityOverrides): Observable<KioskPrinters> {
    return this.http.put<KioskPrinters>(`/api/admin/kiosks/${kioskId}/capabilities`, overrides);
  }

  refreshKioskPrinters(kioskId: string): Observable<KioskCommand> {
    return this.http.post<KioskCommand>(`/api/admin/kiosks/${kioskId}/printers/refresh`, {});
  }

  testPrint(kioskId: string): Observable<KioskCommand> {
    return this.http.post<KioskCommand>(`/api/admin/kiosks/${kioskId}/test-print`, {});
  }

  kioskCommands(kioskId: string): Observable<KioskCommand[]> {
    return this.http.get<KioskCommand[]>(`/api/admin/kiosks/${kioskId}/commands`);
  }

  // ---------------------------------------------------------------------
  // Print jobs and the queue (plan section 8A.3, "Print job-lar")
  // ---------------------------------------------------------------------

  listPrintJobs(filter: PrintJobFilter): Observable<PrintJobPage> {
    let params = new HttpParams().set('page', filter.page).set('pageSize', filter.pageSize);

    if (filter.kioskId) {
      params = params.set('kioskId', filter.kioskId);
    }

    if (filter.statuses?.length) {
      params = params.set('status', filter.statuses.join(','));
    }

    if (filter.from) {
      params = params.set('from', filter.from);
    }

    if (filter.to) {
      params = params.set('to', filter.to);
    }

    if (filter.code?.trim()) {
      params = params.set('code', filter.code.trim());
    }

    return this.http.get<PrintJobPage>('/api/admin/print-jobs', { params });
  }

  getPrintJob(id: string): Observable<PrintJobDetail> {
    return this.http.get<PrintJobDetail>(`/api/admin/print-jobs/${id}`);
  }

  cancelPrintJob(id: string, reason: string): Observable<PrintJob> {
    return this.http.post<PrintJob>(`/api/admin/print-jobs/${id}/cancel`, { reason }, idempotent());
  }

  kioskQueue(kioskId: string): Observable<QueueEntry[]> {
    return this.http.get<QueueEntry[]>(`/api/admin/kiosks/${kioskId}/queue`);
  }

  // ---------------------------------------------------------------------
  // Paper (plan section 2.15; kiosk detail → Supplies)
  // ---------------------------------------------------------------------

  kioskSupplies(kioskId: string): Observable<KioskSupplies> {
    return this.http.get<KioskSupplies>(`/api/admin/kiosks/${kioskId}/supplies`);
  }

  refillPaper(kioskId: string, paper: string, mode: PaperRefillMode, sheets: number): Observable<KioskSupplies> {
    return this.http.post<KioskSupplies>(
      `/api/admin/kiosks/${kioskId}/paper/refill`,
      { paper, mode, sheets },
      idempotent(),
    );
  }

  // ---------------------------------------------------------------------
  // Kiosks
  // ---------------------------------------------------------------------

  /** The kiosks the account's scope covers (plan section 8A.2). */
  listKiosks(): Observable<KioskListItem[]> {
    return this.http.get<KioskListItem[]>('/api/admin/kiosks');
  }

  // ---------------------------------------------------------------------
  // Devices
  // ---------------------------------------------------------------------

  listDevices(status?: DeviceStatus): Observable<DeviceListItem[]> {
    return this.http.get<DeviceListItem[]>('/api/admin/devices', {
      params: status ? { status } : {},
    });
  }

  /** Finds a device from a pairing code, or from a scanned activation QR. */
  lookupDevice(codeOrQr: string): Observable<DeviceDetails> {
    return this.http.post<DeviceDetails>('/api/admin/devices/lookup', {
      codeOrQr,
    });
  }

  activateDevice(
    deviceId: string,
    body: {
      kioskId?: string | null;
      newKiosk?: NewKioskParameters | null;
      selectedPrinterName?: string | null;
    },
  ): Observable<ActivateDeviceResult> {
    return this.http.post<ActivateDeviceResult>(`/api/admin/devices/${deviceId}/activate`, body);
  }

  revokeDevice(deviceId: string, reason: string | null): Observable<void> {
    return this.http.post<void>(`/api/admin/devices/${deviceId}/revoke`, {
      reason,
    });
  }

  // ---------------------------------------------------------------------
  // Enrollment tokens
  // ---------------------------------------------------------------------

  listEnrollmentTokens(): Observable<EnrollmentTokenListItem[]> {
    return this.http.get<EnrollmentTokenListItem[]>('/api/admin/enrollment-tokens');
  }

  createEnrollmentToken(body: {
    kioskId?: string | null;
    newKiosk?: NewKioskParameters | null;
    ttlHours?: number | null;
  }): Observable<EnrollmentTokenResult> {
    return this.http.post<EnrollmentTokenResult>('/api/admin/enrollment-tokens', body);
  }

  revokeEnrollmentToken(id: string): Observable<void> {
    return this.http.delete<void>(`/api/admin/enrollment-tokens/${id}`);
  }

  // ---------------------------------------------------------------------
  // Roles
  // ---------------------------------------------------------------------

  listRoles(): Observable<RoleResponse[]> {
    return this.http.get<RoleResponse[]>('/api/admin/roles');
  }

  permissionCatalogue(): Observable<PermissionGroup[]> {
    return this.http.get<PermissionGroup[]>('/api/admin/permissions');
  }

  createRole(body: {
    name: string;
    description?: string | null;
    permissions: readonly string[];
  }): Observable<RoleResponse> {
    return this.http.post<RoleResponse>('/api/admin/roles', body);
  }

  updateRole(
    id: string,
    body: {
      name: string;
      description?: string | null;
      permissions: readonly string[];
    },
  ): Observable<RoleResponse> {
    return this.http.put<RoleResponse>(`/api/admin/roles/${id}`, body);
  }

  cloneRole(id: string, name: string): Observable<RoleResponse> {
    return this.http.post<RoleResponse>(`/api/admin/roles/${id}/clone`, {
      name,
    });
  }

  deleteRole(id: string, reason: string): Observable<void> {
    return this.http.delete<void>(`/api/admin/roles/${id}`, withReason(reason));
  }

  // ---------------------------------------------------------------------
  // Feature flags
  // ---------------------------------------------------------------------

  listFeatureFlags(): Observable<FeatureFlagDetails[]> {
    return this.http.get<FeatureFlagDetails[]>('/api/admin/features');
  }

  updateFeatureFlag(
    key: string,
    body: {
      defaultEnabled: boolean;
      rolloutPercent: number;
      kioskOverrides?: Record<string, boolean> | null;
      userAllowlist?: readonly string[] | null;
    },
  ): Observable<FeatureFlagDetails> {
    return this.http.put<FeatureFlagDetails>(`/api/admin/features/${key}`, body);
  }

  // ---------------------------------------------------------------------
  // Administrator accounts
  // ---------------------------------------------------------------------

  listAdmins(): Observable<AdminUser[]> {
    return this.http.get<AdminUser[]>('/api/admin/admins');
  }

  createAdmin(body: {
    email: string;
    fullName: string;
    password: string;
    roleIds: readonly string[];
    allKiosks: boolean;
    kioskIds: readonly string[];
  }): Observable<AdminUser> {
    return this.http.post<AdminUser>('/api/admin/admins', body);
  }

  updateAdmin(
    id: string,
    body: {
      fullName: string;
      roleIds: readonly string[];
      allKiosks: boolean;
      kioskIds: readonly string[];
    },
  ): Observable<AdminUser> {
    return this.http.put<AdminUser>(`/api/admin/admins/${id}`, body);
  }

  /** Deactivating rather than deleting keeps the audit trail meaningful. */
  setAdminActive(id: string, isActive: boolean, reason: string | null): Observable<void> {
    return this.http.post<void>(`/api/admin/admins/${id}/active`, {
      isActive,
      reason,
    });
  }

  resetAdminPassword(id: string, newPassword: string): Observable<void> {
    return this.http.post<void>(`/api/admin/admins/${id}/reset-password`, {
      newPassword,
    });
  }

  // ---------------------------------------------------------------------
  // Settings
  // ---------------------------------------------------------------------

  listSettings(): Observable<PlatformSetting[]> {
    return this.http.get<PlatformSetting[]>('/api/admin/settings');
  }

  /** Saves a page of settings at once; a half-applied page is worse than none. */
  updateSettings(values: Record<string, string>): Observable<PlatformSetting[]> {
    return this.http.put<PlatformSetting[]>('/api/admin/settings', { values });
  }

  // ---------------------------------------------------------------------
  // Legal documents
  // ---------------------------------------------------------------------

  listLegalDocuments(): Observable<LegalDocument[]> {
    return this.http.get<LegalDocument[]>('/api/admin/legal-documents');
  }

  /** Publishes a new version; existing versions are never edited. */
  publishLegalDocument(body: {
    docType: LegalDocumentType;
    version: string;
    language: string;
    content: string;
  }): Observable<LegalDocument> {
    return this.http.post<LegalDocument>('/api/admin/legal-documents', body);
  }

  // ---------------------------------------------------------------------
  // Own account
  // ---------------------------------------------------------------------

  changePassword(currentPassword: string, newPassword: string): Observable<void> {
    return this.http.post<void>('/api/auth/admin/change-password', {
      currentPassword,
      newPassword,
    });
  }

  // ---------------------------------------------------------------------
  // Kiosks (plan section 8A.3: "Kiosklar", "Kiosk yarat/redaktə")
  // ---------------------------------------------------------------------

  getKiosk(id: string): Observable<KioskDetail> {
    return this.http.get<KioskDetail>(`/api/admin/kiosks/${id}`);
  }

  createKiosk(body: CreateKioskInput): Observable<KioskDetail> {
    return this.http.post<KioskDetail>('/api/admin/kiosks', body, idempotent());
  }

  updateKiosk(id: string, body: UpdateKioskInput): Observable<KioskDetail> {
    return this.http.put<KioskDetail>(`/api/admin/kiosks/${id}`, body);
  }

  deleteKiosk(id: string, reason: string): Observable<void> {
    return this.http.delete<void>(`/api/admin/kiosks/${id}`, withReason(reason));
  }

  /** Null for either limit means "Default istifadə et". */
  setKioskLimits(
    id: string,
    maxFileSizeMb: number | null,
    maxPages: number | null,
  ): Observable<KioskDetail> {
    return this.http.put<KioskDetail>(`/api/admin/kiosks/${id}/limits`, {
      maxFileSizeMb,
      maxPages,
    });
  }

  setKioskPricingProfile(id: string, pricingProfileId: string | null): Observable<KioskDetail> {
    return this.http.put<KioskDetail>(`/api/admin/kiosks/${id}/pricing-profile`, {
      pricingProfileId,
    });
  }

  setKioskFiscalTerminal(id: string, fiscalTerminalId: string | null): Observable<KioskDetail> {
    return this.http.put<KioskDetail>(`/api/admin/kiosks/${id}/fiscal-terminal`, {
      fiscalTerminalId,
    });
  }

  setKioskPaperCapacity(id: string, a4: number, a3: number): Observable<KioskDetail> {
    return this.http.put<KioskDetail>(`/api/admin/kiosks/${id}/paper/capacity`, { a4, a3 });
  }

  setKioskAdminPin(id: string, pin: string): Observable<KioskDetail> {
    return this.http.post<KioskDetail>(`/api/admin/kiosks/${id}/admin-pin`, { pin });
  }

  kioskDevices(id: string): Observable<KioskDeviceHistoryItem[]> {
    return this.http.get<KioskDeviceHistoryItem[]>(`/api/admin/kiosks/${id}/devices`);
  }

  // ---------------------------------------------------------------------
  // Pricing profiles (plan sections 2.3, 8A.3)
  // ---------------------------------------------------------------------

  listPricingProfiles(): Observable<PricingProfile[]> {
    return this.http.get<PricingProfile[]>('/api/admin/pricing-profiles');
  }

  getPricingProfile(id: string): Observable<PricingProfileDetail> {
    return this.http.get<PricingProfileDetail>(`/api/admin/pricing-profiles/${id}`);
  }

  createPricingProfile(body: PricingProfileInput): Observable<PricingProfile> {
    return this.http.post<PricingProfile>('/api/admin/pricing-profiles', body, idempotent());
  }

  updatePricingProfile(id: string, body: PricingProfileInput): Observable<PricingProfile> {
    return this.http.put<PricingProfile>(`/api/admin/pricing-profiles/${id}`, body);
  }

  deletePricingProfile(id: string, reason: string): Observable<void> {
    return this.http.delete<void>(`/api/admin/pricing-profiles/${id}`, withReason(reason));
  }

  makeDefaultPricingProfile(id: string): Observable<PricingProfile> {
    return this.http.post<PricingProfile>(`/api/admin/pricing-profiles/${id}/default`, {});
  }

  assignKiosksToProfile(id: string, kioskIds: readonly string[]): Observable<PricingProfileDetail> {
    return this.http.post<PricingProfileDetail>(`/api/admin/pricing-profiles/${id}/assign`, {
      kioskIds,
    });
  }

  /** The calculator: a saved profile, or the values still on the form. */
  calculatePrice(body: {
    profileId: string | null;
    draft: PricingProfileInput | null;
    options: PrintOptions;
  }): Observable<PriceBreakdown> {
    return this.http.post<PriceBreakdown>('/api/admin/pricing-profiles/calculate', body);
  }

  // ---------------------------------------------------------------------
  // File formats (plan sections 2.2, 8A.3)
  // ---------------------------------------------------------------------

  getFileFormats(): Observable<FileFormats> {
    return this.http.get<FileFormats>('/api/admin/files/formats');
  }

  updateFileFormats(body: {
    enabled: readonly string[];
    defaultMaxSizeMb: number;
    defaultMaxPages: number;
    userQuotaGb: number;
  }): Observable<FileFormats> {
    return this.http.put<FileFormats>('/api/admin/files/formats', body);
  }

  // ---------------------------------------------------------------------
  // Storage (plan sections 2.10, 8A.3)
  // ---------------------------------------------------------------------

  /** The dashboard's figures; storage only for accounts that may manage it. */
  getDashboard(): Observable<AdminDashboard> {
    return this.http.get<AdminDashboard>('/api/admin/dashboard');
  }

  listStorageProviders(): Observable<StorageProvider[]> {
    return this.http.get<StorageProvider[]>('/api/admin/storage/providers');
  }

  testStorageCredentials(body: StorageProviderInput): Observable<StorageTestResult> {
    return this.http.post<StorageTestResult>('/api/admin/storage/providers/test', body);
  }

  addStorageProvider(body: StorageProviderInput): Observable<StorageProvider> {
    return this.http.post<StorageProvider>('/api/admin/storage/providers', body, idempotent());
  }

  updateStorageProvider(id: string, body: StorageProviderInput): Observable<StorageProvider> {
    return this.http.put<StorageProvider>(`/api/admin/storage/providers/${id}`, body);
  }

  testStorageProvider(id: string): Observable<StorageTestResult> {
    return this.http.post<StorageTestResult>(`/api/admin/storage/providers/${id}/test`, {});
  }

  setStorageRole(id: string, role: StorageRole, reason?: string): Observable<StorageProvider> {
    switch (role) {
      case 'Primary':
        return this.http.post<StorageProvider>(
          `/api/admin/storage/providers/${id}/set-primary`,
          {},
        );
      case 'Secondary':
        return this.http.post<StorageProvider>(
          `/api/admin/storage/providers/${id}/set-secondary`,
          {},
        );
      default:
        return this.http.put<StorageProvider>(
          `/api/admin/storage/providers/${id}/role`,
          { role },
          withReason(reason),
        );
    }
  }

  deleteStorageProvider(id: string, reason: string): Observable<void> {
    return this.http.delete<void>(`/api/admin/storage/providers/${id}`, withReason(reason));
  }

  listStorageSyncJobs(): Observable<StorageSyncJob[]> {
    return this.http.get<StorageSyncJob[]>('/api/admin/storage/sync-jobs');
  }

  startStorageSync(body: {
    mode: StorageSyncMode;
    targetId: string;
    switchPrimaryOnComplete: boolean;
  }): Observable<StorageSyncJob> {
    return this.http.post<StorageSyncJob>('/api/admin/storage/sync-jobs', body, idempotent());
  }

  /** A reason is sent with a cancel, which throws away the work done so far. */
  controlStorageSync(
    id: string,
    operation: StorageSyncOperation,
    reason?: string,
  ): Observable<StorageSyncJob> {
    return this.http.post<StorageSyncJob>(
      `/api/admin/storage/sync-jobs/${id}/${operation}`,
      {},
      withReason(reason),
    );
  }

  storageSyncFailures(id: string): Observable<StorageSyncFailure[]> {
    return this.http.get<StorageSyncFailure[]>(`/api/admin/storage/sync-jobs/${id}/failures`);
  }

  purgeStorageSource(id: string, reason: string): Observable<StorageSyncJob> {
    const headers = idempotent().headers.set('X-Audit-Reason', encodeURIComponent(reason.trim()));

    return this.http.post<StorageSyncJob>(
      `/api/admin/storage/sync-jobs/${id}/purge-source`,
      {},
      { headers },
    );
  }

  startKeyRewrap(): Observable<void> {
    return this.http.post<void>('/api/admin/storage/rewrap', {}, idempotent());
  }
}
