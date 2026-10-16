import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { InventoryItem } from '../../features/operator/operator.models';
import { IncidentRow, Page } from './step4.models';
import {
  AuditLogEntry,
  AuditLogFilter,
  IncidentDetail,
  InventoryItemInput,
  InventoryMovement,
  ServiceTaskAssignee,
  ServiceTaskDetail,
  ServiceTaskSummary,
  SupplyAlertRow,
} from './step7.models';

/** A write that must not happen twice if it is sent again. */
function idempotent(): { headers: HttpHeaders } {
  return { headers: new HttpHeaders({ 'Idempotency-Key': crypto.randomUUID() }) };
}

/** Which incidents a list shows: still to be dealt with, closed, or all. */
export type IncidentView = 'open' | 'resolved' | 'all';

/** Which tasks a list shows: still to be done, finished (done or cancelled), or all. */
export type TaskView = 'open' | 'closed' | 'all';

export interface TaskFilter {
  readonly view: TaskView;
  readonly kioskId?: string | null;
  readonly unassigned?: boolean;
  readonly page: number;
  readonly pageSize: number;
}

const taskStatuses: Record<TaskView, readonly string[]> = {
  open: ['Open', 'Assigned', 'InProgress'],
  closed: ['Done', 'Cancelled'],
  all: [],
};

const incidentStatuses: Record<IncidentView, readonly string[]> = {
  open: ['Open', 'Acknowledged'],
  resolved: ['Resolved'],
  all: [],
};

/**
 * What the operations pages ask the server (step 7A, M9; plan section
 * 8A.3): incidents, service tasks, the warehouse, the supply alerts and the
 * audit log.
 */
@Injectable({ providedIn: 'root' })
export class OperationsApiService {
  private readonly http = inject(HttpClient);

  // --- incidents (plan section 9.6) -----------------------------------------

  incidents(view: IncidentView, kioskId: string | null, page: number, pageSize: number): Observable<Page<IncidentRow>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    incidentStatuses[view].forEach((status) => (params = params.append('status', status)));

    if (kioskId) {
      params = params.set('kioskId', kioskId);
    }

    return this.http.get<Page<IncidentRow>>('/api/admin/incidents', { params });
  }

  incident(id: string): Observable<IncidentDetail> {
    return this.http.get<IncidentDetail>(`/api/admin/incidents/${id}`);
  }

  acknowledge(id: string): Observable<IncidentRow> {
    return this.http.post<IncidentRow>(`/api/admin/incidents/${id}/acknowledge`, {});
  }

  resolve(id: string, note: string): Observable<IncidentRow> {
    return this.http.post<IncidentRow>(`/api/admin/incidents/${id}/resolve`, { note });
  }

  // --- service tasks (plan section 2.15) ------------------------------------

  tasks(filter: TaskFilter): Observable<Page<ServiceTaskDetail>> {
    let params = new HttpParams().set('page', filter.page).set('pageSize', filter.pageSize);
    taskStatuses[filter.view].forEach((status) => (params = params.append('status', status)));

    if (filter.kioskId) {
      params = params.set('kioskId', filter.kioskId);
    }

    if (filter.unassigned) {
      params = params.set('unassigned', true);
    }

    return this.http.get<Page<ServiceTaskDetail>>('/api/admin/service-tasks', { params });
  }

  taskSummary(): Observable<ServiceTaskSummary> {
    return this.http.get<ServiceTaskSummary>('/api/admin/service-tasks/summary');
  }

  assignees(kioskId: string): Observable<ServiceTaskAssignee[]> {
    return this.http.get<ServiceTaskAssignee[]>('/api/admin/service-tasks/assignees', { params: { kioskId } });
  }

  createTask(body: {
    kioskId: string;
    type: string;
    priority: string;
    notes: string | null;
    dueAt: string | null;
    assignTo: string | null;
  }): Observable<ServiceTaskDetail> {
    return this.http.post<ServiceTaskDetail>('/api/admin/service-tasks', body, idempotent());
  }

  updateTask(id: string, body: { priority: string; notes: string | null; dueAt: string | null }): Observable<ServiceTaskDetail> {
    return this.http.put<ServiceTaskDetail>(`/api/admin/service-tasks/${id}`, body);
  }

  /** Null hands the task back to the pool. */
  assignTask(id: string, assignTo: string | null): Observable<ServiceTaskDetail> {
    return this.http.post<ServiceTaskDetail>(`/api/admin/service-tasks/${id}/assign`, { assignTo });
  }

  cancelTask(id: string, reason: string): Observable<ServiceTaskDetail> {
    return this.http.post<ServiceTaskDetail>(`/api/admin/service-tasks/${id}/cancel`, { reason });
  }

  // --- the warehouse --------------------------------------------------------

  items(includeInactive: boolean): Observable<InventoryItem[]> {
    return this.http.get<InventoryItem[]>('/api/admin/inventory/items', { params: { includeInactive } });
  }

  createItem(item: InventoryItemInput): Observable<InventoryItem> {
    return this.http.post<InventoryItem>('/api/admin/inventory/items', item, idempotent());
  }

  updateItem(id: string, item: InventoryItemInput): Observable<InventoryItem> {
    return this.http.put<InventoryItem>(`/api/admin/inventory/items/${id}`, item);
  }

  movements(itemId: string | null, kioskId: string | null, page: number, pageSize: number): Observable<Page<InventoryMovement>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);

    if (itemId) {
      params = params.set('itemId', itemId);
    }

    if (kioskId) {
      params = params.set('kioskId', kioskId);
    }

    return this.http.get<Page<InventoryMovement>>('/api/admin/inventory/movements', { params });
  }

  move(body: { itemId: string; type: string; quantity: number; kioskId: string | null; note: string | null }): Observable<InventoryMovement> {
    return this.http.post<InventoryMovement>('/api/admin/inventory/movements', body, idempotent());
  }

  // --- supply alerts --------------------------------------------------------

  activeAlerts(): Observable<Page<SupplyAlertRow>> {
    const params = new HttpParams().set('active', true).set('page', 1).set('pageSize', 100);

    return this.http.get<Page<SupplyAlertRow>>('/api/admin/supply-alerts', { params });
  }

  // --- the audit log --------------------------------------------------------

  auditLogs(filter: AuditLogFilter, page: number, pageSize: number): Observable<Page<AuditLogEntry>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);

    for (const key of ['action', 'entityType', 'entityId', 'adminUserId'] as const) {
      const value = filter[key]?.trim();

      if (value) {
        params = params.set(key, value);
      }
    }

    if (filter.from) {
      params = params.set('from', filter.from.toISOString());
    }

    if (filter.to) {
      params = params.set('to', filter.to.toISOString());
    }

    return this.http.get<Page<AuditLogEntry>>('/api/admin/audit-logs', { params });
  }
}
