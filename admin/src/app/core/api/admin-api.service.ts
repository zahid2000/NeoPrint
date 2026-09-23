import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { PermissionGroup } from '../auth/auth.models';

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
    return this.http.post<ActivateDeviceResult>(
      `/api/admin/devices/${deviceId}/activate`,
      body,
    );
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
    return this.http.get<EnrollmentTokenListItem[]>(
      '/api/admin/enrollment-tokens',
    );
  }

  createEnrollmentToken(body: {
    kioskId?: string | null;
    newKiosk?: NewKioskParameters | null;
    ttlHours?: number | null;
  }): Observable<EnrollmentTokenResult> {
    return this.http.post<EnrollmentTokenResult>(
      '/api/admin/enrollment-tokens',
      body,
    );
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

  deleteRole(id: string): Observable<void> {
    return this.http.delete<void>(`/api/admin/roles/${id}`);
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
    return this.http.put<FeatureFlagDetails>(
      `/api/admin/features/${key}`,
      body,
    );
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
}
