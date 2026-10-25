import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { KioskCommand } from './step3.models';
import { IncidentRow, Page } from './step4.models';
import {
  KioskTelemetry,
  KioskTelemetryPoint,
  PrinterHistoryEvent,
  PrinterHistoryPoint,
  PrinterReading,
  SupplyRule,
  SupplyRuleInput,
} from './step7.models';
import { ServiceTask, SupplyAlert } from '../../features/operator/operator.models';

/** The commands a kiosk takes from its "Əmrlər" tab, by their endpoint. */
export type KioskCommandPath = 'pause' | 'resume' | 'restart-app' | 'reboot' | AgentCommandPath;

/** The agent's commands (T-326): each is sent with a reason. */
export type AgentCommandPath = 'update-now' | 'rollback-version' | 'reapply-provisioning' | 'restart-agent' | 'upload-logs';

/**
 * What the kiosk and printer pages ask the server (step 7A, M8): a kiosk's
 * telemetry and graphs, its printers' readings and history, the printers of
 * every kiosk, the remote commands, and the kiosk's tasks, incidents and
 * supply rules.
 */
@Injectable({ providedIn: 'root' })
export class MonitoringApiService {
  private readonly http = inject(HttpClient);

  // --- a kiosk's own report (plan section 7.9) ------------------------------

  kioskTelemetry(kioskId: string): Observable<KioskTelemetry> {
    return this.http.get<KioskTelemetry>(`/api/admin/kiosks/${kioskId}/telemetry`);
  }

  kioskTelemetryHistory(kioskId: string, from: Date, to: Date, points = 120): Observable<KioskTelemetryPoint[]> {
    const params = new HttpParams().set('from', from.toISOString()).set('to', to.toISOString()).set('points', points);

    return this.http.get<KioskTelemetryPoint[]>(`/api/admin/kiosks/${kioskId}/telemetry/history`, { params });
  }

  // --- printers (plan section 7.10) ----------------------------------------

  kioskPrinterReadings(kioskId: string): Observable<PrinterReading[]> {
    return this.http.get<PrinterReading[]>(`/api/admin/kiosks/${kioskId}/printers/telemetry`);
  }

  /** Every printer of every kiosk the account sees. */
  allPrinterReadings(): Observable<PrinterReading[]> {
    return this.http.get<PrinterReading[]>('/api/admin/printers');
  }

  printerHistory(kioskId: string, printer: string, from: Date, to: Date, points = 120): Observable<PrinterHistoryPoint[]> {
    const params = new HttpParams()
      .set('printer', printer)
      .set('from', from.toISOString())
      .set('to', to.toISOString())
      .set('points', points);

    return this.http.get<PrinterHistoryPoint[]>(`/api/admin/kiosks/${kioskId}/printers/telemetry/history`, { params });
  }

  printerEvents(kioskId: string, printer: string): Observable<PrinterHistoryEvent[]> {
    const params = new HttpParams().set('printer', printer);

    return this.http.get<PrinterHistoryEvent[]>(`/api/admin/kiosks/${kioskId}/printers/telemetry/events`, { params });
  }

  // --- commands (plan section 7.8) -----------------------------------------

  /** @param reason Required for the agent's commands (3-500 characters), kept in the audit log. */
  command(kioskId: string, command: KioskCommandPath, reason?: string): Observable<KioskCommand> {
    return this.http.post<KioskCommand>(`/api/admin/kiosks/${kioskId}/${command}`, reason === undefined ? {} : { reason });
  }

  // --- what is open for the kiosk ------------------------------------------

  kioskTasks(kioskId: string, openOnly: boolean): Observable<Page<ServiceTask>> {
    let params = new HttpParams().set('kioskId', kioskId).set('page', 1).set('pageSize', 50);

    if (openOnly) {
      ['Open', 'Assigned', 'InProgress'].forEach((status) => (params = params.append('status', status)));
    }

    return this.http.get<Page<ServiceTask>>('/api/admin/service-tasks', { params });
  }

  kioskIncidents(kioskId: string, openOnly: boolean): Observable<Page<IncidentRow>> {
    let params = new HttpParams().set('kioskId', kioskId).set('page', 1).set('pageSize', 50);

    if (openOnly) {
      ['Open', 'Acknowledged'].forEach((status) => (params = params.append('status', status)));
    }

    return this.http.get<Page<IncidentRow>>('/api/admin/incidents', { params });
  }

  // --- supply rules (plan section 2.15) -------------------------------------

  /** The supplies below their rule now, in every kiosk the account sees. */
  activeSupplyAlerts(): Observable<Page<SupplyAlert>> {
    const params = new HttpParams().set('active', true).set('page', 1).set('pageSize', 500);

    return this.http.get<Page<SupplyAlert>>('/api/admin/supply-alerts', { params });
  }

  supplyRules(): Observable<SupplyRule[]> {
    return this.http.get<SupplyRule[]>('/api/admin/supply-rules');
  }

  createSupplyRule(rule: SupplyRuleInput): Observable<SupplyRule> {
    return this.http.post<SupplyRule>('/api/admin/supply-rules', rule, {
      headers: new HttpHeaders({ 'Idempotency-Key': crypto.randomUUID() }),
    });
  }

  updateSupplyRule(id: string, rule: SupplyRuleInput): Observable<SupplyRule> {
    return this.http.put<SupplyRule>(`/api/admin/supply-rules/${id}`, rule);
  }

  deleteSupplyRule(id: string): Observable<void> {
    return this.http.delete<void>(`/api/admin/supply-rules/${id}`);
  }
}
