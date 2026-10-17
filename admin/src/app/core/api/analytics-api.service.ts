import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { PilotReport } from './step7.models';

/** The pilot report (step 7A, M10; plan section 2.17). */
@Injectable({ providedIn: 'root' })
export class AnalyticsApiService {
  private readonly http = inject(HttpClient);

  /** Baku days `yyyy-MM-dd`, both included; today is worked out on the spot. */
  pilot(from: string, to: string, kioskId: string | null): Observable<PilotReport> {
    let params = new HttpParams().set('from', from).set('to', to);

    if (kioskId) {
      params = params.set('kioskId', kioskId);
    }

    return this.http.get<PilotReport>('/api/admin/analytics/pilot', { params });
  }
}
