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

  setChannel(kioskId: string, channel: ReleaseChannel): Observable<KioskVersion> {
    return this.http.put<KioskVersion>(`/api/admin/kiosks/${kioskId}/update-channel`, { channel }, idempotent());
  }
}
