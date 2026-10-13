import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { KioskCommand } from '../../core/api/step3.models';
import { IncidentRow, Page } from '../../core/api/step4.models';
import {
  CompleteTaskRequest,
  InventoryItem,
  OperatorKioskCard,
  OperatorKioskDetail,
  RefillRequest,
  ReplacedRequest,
  ServiceTask,
  ServiceTaskStatus,
} from './operator.models';

/** A write that must not happen twice when a weak connection makes the phone send it again. */
function idempotent(): { headers: HttpHeaders } {
  return { headers: new HttpHeaders({ 'Idempotency-Key': crypto.randomUUID() }) };
}

/**
 * What the operator panel asks the server (plan section 8B).
 *
 * Two endpoints are the panel's own - the kiosk cards and the short detail,
 * each one request for a whole screen, because the operator stands at a kiosk
 * with a phone and a weak signal. Everything else is what the admin panel
 * calls, under the same permissions.
 */
@Injectable({ providedIn: 'root' })
export class OperatorApiService {
  private readonly http = inject(HttpClient);

  kiosks(): Observable<OperatorKioskCard[]> {
    return this.http.get<OperatorKioskCard[]>('/api/admin/operator/kiosks');
  }

  kiosk(id: string): Observable<OperatorKioskDetail> {
    return this.http.get<OperatorKioskDetail>(`/api/admin/operator/kiosks/${id}`);
  }

  // --- at the kiosk ----------------------------------------------------------

  refill(kioskId: string, request: RefillRequest): Observable<unknown> {
    return this.http.post(`/api/admin/kiosks/${kioskId}/paper/refill`, request, idempotent());
  }

  replaced(kioskId: string, request: ReplacedRequest): Observable<unknown> {
    return this.http.post(`/api/admin/kiosks/${kioskId}/supplies/replaced`, request, idempotent());
  }

  /** `test-print`, `pause`, `resume`, `restart-app` or `printers/refresh`. */
  command(kioskId: string, command: string): Observable<KioskCommand> {
    return this.http.post<KioskCommand>(`/api/admin/kiosks/${kioskId}/${command}`, {});
  }

  // --- tasks -----------------------------------------------------------------

  /** The tasks still to be done in the caller's kiosks: theirs, and the pool's. */
  tasks(statuses: readonly ServiceTaskStatus[]): Observable<Page<ServiceTask>> {
    let params = new HttpParams().set('page', 1).set('pageSize', 100);
    statuses.forEach((status) => (params = params.append('status', status)));

    return this.http.get<Page<ServiceTask>>('/api/admin/service-tasks', { params });
  }

  accept(taskId: string): Observable<ServiceTask> {
    return this.http.post<ServiceTask>(`/api/admin/service-tasks/${taskId}/accept`, {});
  }

  start(taskId: string): Observable<ServiceTask> {
    return this.http.post<ServiceTask>(`/api/admin/service-tasks/${taskId}/start`, {});
  }

  complete(taskId: string, request: CompleteTaskRequest): Observable<ServiceTask> {
    return this.http.post<ServiceTask>(
      `/api/admin/service-tasks/${taskId}/complete`,
      request,
      idempotent(),
    );
  }

  // --- incidents -------------------------------------------------------------

  incidents(): Observable<Page<IncidentRow>> {
    const params = new HttpParams()
      .set('page', 1)
      .set('pageSize', 50)
      .append('status', 'Open')
      .append('status', 'Acknowledged');

    return this.http.get<Page<IncidentRow>>('/api/admin/incidents', { params });
  }

  acknowledge(incidentId: string): Observable<IncidentRow> {
    return this.http.post<IncidentRow>(`/api/admin/incidents/${incidentId}/acknowledge`, {});
  }

  resolve(incidentId: string, note: string): Observable<IncidentRow> {
    return this.http.post<IncidentRow>(`/api/admin/incidents/${incidentId}/resolve`, { note });
  }

  // --- the warehouse ---------------------------------------------------------

  inventory(): Observable<InventoryItem[]> {
    return this.http.get<InventoryItem[]>('/api/admin/inventory/items');
  }

  /** "Özümə götürürəm": written in the caller's own name. */
  take(itemId: string, quantity: number, note: string | null): Observable<InventoryItem> {
    return this.http.post<InventoryItem>(
      `/api/admin/inventory/items/${itemId}/take`,
      { quantity, note },
      idempotent(),
    );
  }
}
