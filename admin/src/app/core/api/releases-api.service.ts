import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** What a release package carries (backend `ReleaseComponent`). */
export type ReleaseComponent = 'App' | 'Agent' | 'Provisioning';

/** Pilot kiosks first, then everyone (backend `ReleaseChannel`). */
export type ReleaseChannel = 'Pilot' | 'Stable';

/** Who a rollout reaches (backend `RolloutTarget`). */
export type RolloutTarget = 'All' | 'Kiosks' | 'Percent' | 'Channel';

export type RolloutStatus = 'Active' | 'Paused' | 'Completed' | 'RolledBack';

export type RolloutAction = 'pause' | 'resume' | 'complete' | 'rollback';

/** Where an update stands on a kiosk (backend `KioskUpdateStatus`). */
export type KioskUpdateStatus =
  | 'Downloading'
  | 'Ready'
  | 'WaitingForIdle'
  | 'Draining'
  | 'Applying'
  | 'HealthCheck'
  | 'Succeeded'
  | 'Failed'
  | 'RolledBack';

export interface Release {
  readonly id: string;
  readonly component: ReleaseComponent;
  readonly platform: string;
  readonly keySet: string;
  readonly version: string;
  readonly minAgentVersion: string;
  readonly channel: ReleaseChannel;
  readonly fileName: string;
  readonly size: number;
  readonly sha256: string;
  readonly keyId: string;
  readonly commit: string | null;
  readonly builtAt: string;
  readonly uploadedAt: string;
  readonly isWithdrawn: boolean;
  readonly withdrawnAt: string | null;
  readonly withdrawReason: string | null;
  readonly installedOn: number;
}

export interface Rollout {
  readonly id: string;
  readonly releaseId: string;
  readonly component: ReleaseComponent;
  readonly platform: string;
  readonly version: string;
  readonly target: RolloutTarget;
  readonly kioskIds: readonly string[];
  readonly percent: number | null;
  readonly channel: ReleaseChannel | null;
  readonly status: RolloutStatus;
  readonly createdAt: string;
  readonly endedAt: string | null;
  readonly reaches: number;
  /** A security fix: kiosks do not wait for the update window (T-321). */
  readonly urgent: boolean;
  /** Where the kiosks it reaches stand with its version (T-326). */
  readonly progress?: RolloutProgress;
}

export interface RolloutProgress {
  readonly installed: number;
  readonly ready: number;
  /** Downloading, waiting for an idle moment, turning customers away, updating or checking. */
  readonly updating: number;
  readonly rolledBack: number;
  readonly failed: number;
}

export interface ReleasesPage {
  /** The key set this server takes: `staging` on the test server, `production` for the pilot. */
  readonly keySet: string;
  readonly trustedKeys: number;
  readonly releases: readonly Release[];
  readonly rollouts: readonly Rollout[];
}

export interface KioskUpdateState {
  readonly component: ReleaseComponent;
  readonly version: string;
  readonly status: KioskUpdateStatus;
  readonly message: string | null;
  readonly reportedAt: string;
}

export interface KioskVersion {
  readonly kioskId: string;
  readonly code: string;
  readonly name: string;
  readonly platform: string | null;
  readonly channel: ReleaseChannel;
  readonly appVersion: string | null;
  readonly agentVersion: string | null;
  readonly provisioningVersion: string | null;
  readonly updates: readonly KioskUpdateState[];
  readonly offered: readonly { readonly component: ReleaseComponent; readonly version: string }[];
  readonly lastHeartbeatAt: string | null;
}

/** One component on a kiosk (T-326): what runs, what it is offered, where its last update stands. */
export interface KioskComponent {
  readonly component: ReleaseComponent;
  readonly running: string | null;
  readonly target: string | null;
  readonly state: KioskUpdateState | null;
}

/** A kiosk's "Yeniləmə" tab (GET admin/kiosks/{id}/update): within the account's kiosk scope. */
export interface KioskUpdateDetail {
  readonly kioskId: string;
  readonly platform: string | null;
  readonly channel: ReleaseChannel;
  /** Without a bound device no command can be sent. */
  readonly hasDevice: boolean;
  readonly lastHeartbeatAt: string | null;
  readonly maxIdleWaitMinutes: number;
  readonly components: readonly KioskComponent[];
  /** Newest first, the last fifty reports. */
  readonly history: readonly KioskUpdateState[];
  /** Versions this kiosk is not offered again (rolled back or refused) until "İndi yenilə". */
  readonly skipped: readonly { readonly component: ReleaseComponent; readonly version: string; readonly at: string }[];
}

/** A log file a kiosk uploaded (T-321). */
export interface KioskLog {
  readonly id: string;
  readonly commandId: string | null;
  readonly size: number;
  readonly lines: number;
  /** Values the backend took out on top of what the kiosk did. */
  readonly redacted: number;
  readonly createdAt: string;
  readonly expiresAt: string;
}

export interface NewRollout {
  readonly releaseId: string;
  readonly target: RolloutTarget;
  readonly kioskIds?: readonly string[];
  readonly percent?: number;
  readonly channel?: ReleaseChannel;
  readonly urgent?: boolean;
}

function idempotent(): { headers: HttpHeaders } {
  return { headers: new HttpHeaders({ 'Idempotency-Key': crypto.randomUUID() }) };
}

/** Releases, rollouts and the kiosks' versions (T-317, plan section 6 "Admin Releases"). */
@Injectable({ providedIn: 'root' })
export class ReleasesApiService {
  private readonly http = inject(HttpClient);

  page(): Observable<ReleasesPage> {
    return this.http.get<ReleasesPage>('/api/admin/releases');
  }

  /** The package, its manifest and the manifest's signature, checked by the server as a kiosk checks them. */
  upload(files: { package: File; manifest: File; signature: File }, channel: ReleaseChannel): Observable<Release> {
    const form = new FormData();
    form.append('package', files.package, files.package.name);
    form.append('manifest', files.manifest, files.manifest.name);
    form.append('signature', files.signature, files.signature.name);
    form.append('channel', channel);

    return this.http.post<Release>('/api/admin/releases', form, idempotent());
  }

  promote(id: string): Observable<Release> {
    return this.http.post<Release>(`/api/admin/releases/${id}/promote`, null, idempotent());
  }

  withdraw(id: string, reason: string): Observable<Release> {
    return this.http.post<Release>(`/api/admin/releases/${id}/withdraw`, { reason }, idempotent());
  }

  createRollout(rollout: NewRollout): Observable<Rollout> {
    return this.http.post<Rollout>('/api/admin/rollouts', rollout, idempotent());
  }

  changeRollout(id: string, action: RolloutAction): Observable<Rollout> {
    return this.http.post<Rollout>(`/api/admin/rollouts/${id}/${action}`, null, idempotent());
  }

  versions(): Observable<KioskVersion[]> {
    return this.http.get<KioskVersion[]>('/api/admin/kiosks/versions');
  }

  kioskUpdate(kioskId: string): Observable<KioskUpdateDetail> {
    return this.http.get<KioskUpdateDetail>(`/api/admin/kiosks/${kioskId}/update`);
  }

  kioskLogs(kioskId: string): Observable<KioskLog[]> {
    return this.http.get<KioskLog[]>(`/api/admin/kiosks/${kioskId}/logs`);
  }

  /** The gzip file itself; the download is on the record. */
  kioskLogFile(kioskId: string, logId: string): Observable<Blob> {
    return this.http.get(`/api/admin/kiosks/${kioskId}/logs/${logId}`, { responseType: 'blob' });
  }

  setChannel(kioskId: string, channel: ReleaseChannel): Observable<KioskVersion> {
    return this.http.put<KioskVersion>(`/api/admin/kiosks/${kioskId}/update-channel`, { channel }, idempotent());
  }
}
