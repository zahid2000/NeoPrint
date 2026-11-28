import { HttpClient, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** One recorded minute on the server (T-483). */
export interface CameraMinute {
  readonly id: string;
  readonly start: string;
  readonly end: string;
  readonly bytes: number;
  /** Asked of the kiosk: kept a day. Otherwise from the file server. */
  readonly fetched: boolean;
  readonly expiresAt: string;
}

/** A span the panel asked the kiosk for. */
export interface CameraRequest {
  readonly id: string;
  readonly from: string;
  readonly to: string;
  readonly status: 'Pending' | 'Delivered';
  readonly delivered: number;
  readonly createdAt: string;
  readonly deliveredAt: string | null;
}

export interface CameraFootage {
  readonly cameraOn: boolean;
  readonly minutes: readonly CameraMinute[];
  readonly request: CameraRequest | null;
}

/** The kiosk camera in the panel (T-483): `admin/kiosks/{id}/camera`. */
@Injectable({ providedIn: 'root' })
export class CameraApiService {
  private readonly http = inject(HttpClient);

  footage(kioskId: string, from: Date, to: Date): Observable<CameraFootage> {
    return this.http.get<CameraFootage>(`${this.path(kioskId)}/footage`, {
      params: { from: from.toISOString(), to: to.toISOString() },
    });
  }

  requestFootage(kioskId: string, from: Date, to: Date): Observable<CameraRequest> {
    return this.http.post<CameraRequest>(`${this.path(kioskId)}/footage/requests`, {
      from: from.toISOString(),
      to: to.toISOString(),
    });
  }

  /** One minute as MP4, opened for this look (on the audit record). */
  minute(kioskId: string, minuteId: string): Observable<Blob> {
    return this.http.get(`${this.path(kioskId)}/footage/${minuteId}`, { responseType: 'blob' });
  }

  startLive(kioskId: string): Observable<unknown> {
    return this.http.post(`${this.path(kioskId)}/live`, {});
  }

  /** The latest live frame; 204 (no body) while none has come. */
  liveFrame(kioskId: string): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.path(kioskId)}/live/frame`, {
      responseType: 'blob',
      observe: 'response',
    });
  }

  stopLive(kioskId: string): Observable<unknown> {
    return this.http.delete(`${this.path(kioskId)}/live`);
  }

  private path(kioskId: string): string {
    return `/api/admin/kiosks/${kioskId}/camera`;
  }
}
