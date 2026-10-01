import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { PlatformSetting } from './admin-api.service';
import {
  AdminRefundInput,
  Claim,
  CustomerDetail,
  CustomerRow,
  FiscalRow,
  Page,
  PaymentDetail,
  PaymentFilter,
  PaymentRow,
  PaymentsDashboard,
  Promo,
  PromoInput,
  ReceiptRow,
  RefundOptions,
  RefundRow,
  RevenueReport,
  SuspectedDecisionInput,
  SuspectedInvestigation,
  SuspectedJob,
  VoucherRow,
  Wallet,
  WalletEntry,
} from './step4.models';

/** One action, one key: a double click or a retry is answered, not repeated (docs/IDEMPOTENCY.md). */
function idempotent(): { headers: HttpHeaders } {
  return { headers: new HttpHeaders({ 'Idempotency-Key': crypto.randomUUID() }) };
}

/** Repeated query parameters (`status=A&status=B`), the way the API binds arrays. */
function withAll(
  params: HttpParams,
  name: string,
  values: readonly string[] | undefined,
): HttpParams {
  return (values ?? []).reduce((all, value) => all.append(name, value), params);
}

function withOptional(
  params: HttpParams,
  name: string,
  value: string | number | boolean | null | undefined,
): HttpParams {
  return value === null || value === undefined || value === ''
    ? params
    : params.set(name, String(value));
}

/**
 * The money and customer endpoints of step 4 (plan sections 6, 8A.3). Kept
 * apart from {@link AdminApiService}, which holds the earlier steps.
 */
@Injectable({ providedIn: 'root' })
export class MoneyApiService {
  private readonly http = inject(HttpClient);

  // ---------------------------------------------------------------------
  // Payments and refunds
  // ---------------------------------------------------------------------

  listPayments(filter: PaymentFilter): Observable<Page<PaymentRow>> {
    let params = new HttpParams().set('page', filter.page).set('pageSize', filter.pageSize);
    params = withAll(params, 'status', filter.statuses);
    params = withAll(params, 'method', filter.methods);
    params = withOptional(params, 'purpose', filter.purpose);
    params = withOptional(params, 'kioskId', filter.kioskId);
    params = withOptional(params, 'from', filter.from);
    params = withOptional(params, 'to', filter.to);
    params = withOptional(params, 'hadError', filter.hadError || null);
    params = withOptional(params, 'refund', filter.refund);
    params = withOptional(params, 'search', filter.search?.trim());

    return this.http.get<Page<PaymentRow>>('/api/admin/payments', { params });
  }

  getPayment(id: string): Observable<PaymentDetail> {
    return this.http.get<PaymentDetail>(`/api/admin/payments/${id}`);
  }

  refundOptions(paymentId: string): Observable<RefundOptions> {
    return this.http.get<RefundOptions>(`/api/admin/payments/${paymentId}/refund-options`);
  }

  refund(paymentId: string, input: AdminRefundInput): Observable<unknown> {
    return this.http.post(`/api/admin/payments/${paymentId}/refund`, input, idempotent());
  }

  listRefunds(
    statuses: readonly string[],
    page: number,
    pageSize: number,
  ): Observable<Page<RefundRow>> {
    const params = withAll(
      new HttpParams().set('page', page).set('pageSize', pageSize),
      'status',
      statuses,
    );
    return this.http.get<Page<RefundRow>>('/api/admin/refunds', { params });
  }

  retryRefund(id: string, destination: string | null, note: string | null): Observable<RefundRow> {
    return this.http.post<RefundRow>(
      `/api/admin/refunds/${id}/retry`,
      { destination, note },
      idempotent(),
    );
  }

  // ---------------------------------------------------------------------
  // Fiscal receipts, receipts, vouchers
  // ---------------------------------------------------------------------

  listFiscal(
    statuses: readonly string[],
    types: readonly string[],
    page: number,
    pageSize: number,
  ): Observable<Page<FiscalRow>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    params = withAll(params, 'status', statuses);
    params = withAll(params, 'type', types);
    return this.http.get<Page<FiscalRow>>('/api/admin/fiscal-receipts', { params });
  }

  retryFiscal(id: string): Observable<FiscalRow> {
    return this.http.post<FiscalRow>(`/api/admin/fiscal-receipts/${id}/retry`, {}, idempotent());
  }

  fiscalSettings(): Observable<PlatformSetting[]> {
    return this.http.get<PlatformSetting[]>('/api/admin/fiscal/settings');
  }

  updateFiscalSettings(values: Record<string, string>): Observable<PlatformSetting[]> {
    return this.http.put<PlatformSetting[]>('/api/admin/fiscal/settings', { values });
  }

  listReceipts(number: string, page: number, pageSize: number): Observable<Page<ReceiptRow>> {
    const params = withOptional(
      new HttpParams().set('page', page).set('pageSize', pageSize),
      'number',
      number.trim(),
    );
    return this.http.get<Page<ReceiptRow>>('/api/admin/receipts', { params });
  }

  receiptPdf(id: string): Observable<Blob> {
    return this.http.get(`/api/admin/receipts/${id}/pdf`, { responseType: 'blob' });
  }

  resendReceipt(id: string): Observable<ReceiptRow> {
    return this.http.post<ReceiptRow>(`/api/admin/receipts/${id}/resend`, {}, idempotent());
  }

  listVouchers(page: number, pageSize: number): Observable<Page<VoucherRow>> {
    return this.http.get<Page<VoucherRow>>('/api/admin/vouchers', {
      params: new HttpParams().set('page', page).set('pageSize', pageSize),
    });
  }

  // ---------------------------------------------------------------------
  // Promo codes
  // ---------------------------------------------------------------------

  listPromos(code: string, page: number, pageSize: number): Observable<Page<Promo>> {
    const params = withOptional(
      new HttpParams().set('page', page).set('pageSize', pageSize),
      'code',
      code.trim(),
    );
    return this.http.get<Page<Promo>>('/api/admin/promos', { params });
  }

  createPromo(input: PromoInput): Observable<Promo> {
    return this.http.post<Promo>('/api/admin/promos', input, idempotent());
  }

  updatePromo(id: string, input: PromoInput): Observable<Promo> {
    return this.http.put<Promo>(`/api/admin/promos/${id}`, input);
  }

  setPromoActive(id: string, active: boolean): Observable<Promo> {
    return this.http.post<Promo>(
      `/api/admin/promos/${id}/${active ? 'activate' : 'deactivate'}`,
      {},
      idempotent(),
    );
  }

  // ---------------------------------------------------------------------
  // Customers and their wallets
  // ---------------------------------------------------------------------

  listCustomers(
    search: string,
    status: string | null,
    page: number,
    pageSize: number,
  ): Observable<Page<CustomerRow>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    params = withOptional(params, 'search', search.trim());
    params = withOptional(params, 'status', status);
    return this.http.get<Page<CustomerRow>>('/api/admin/users', { params });
  }

  getCustomer(id: string): Observable<CustomerDetail> {
    return this.http.get<CustomerDetail>(`/api/admin/users/${id}`);
  }

  setCustomerBlocked(id: string, blocked: boolean, reason: string): Observable<CustomerRow> {
    return this.http.post<CustomerRow>(
      `/api/admin/users/${id}/${blocked ? 'block' : 'unblock'}`,
      { reason },
      idempotent(),
    );
  }

  walletEntries(userId: string, page: number, pageSize: number): Observable<Page<WalletEntry>> {
    return this.http.get<Page<WalletEntry>>(`/api/admin/wallets/${userId}/transactions`, {
      params: new HttpParams().set('page', page).set('pageSize', pageSize),
    });
  }

  adjustWallet(
    userId: string,
    amount: number,
    bonusPages: number,
    reason: string,
  ): Observable<Wallet> {
    return this.http.post<Wallet>(
      `/api/admin/wallets/${userId}/adjust`,
      { amount, bonusPages, reason },
      idempotent(),
    );
  }

  // ---------------------------------------------------------------------
  // Suspected jobs and claims
  // ---------------------------------------------------------------------

  listSuspected(): Observable<SuspectedJob[]> {
    return this.http.get<SuspectedJob[]>('/api/admin/print-jobs/suspected');
  }

  investigation(jobId: string): Observable<SuspectedInvestigation> {
    return this.http.get<SuspectedInvestigation>(`/api/admin/print-jobs/${jobId}/investigation`);
  }

  decide(jobId: string, input: SuspectedDecisionInput): Observable<unknown> {
    return this.http.post(`/api/admin/print-jobs/${jobId}/resolve`, input, idempotent());
  }

  listClaims(status: string | null, page: number, pageSize: number): Observable<Page<Claim>> {
    const params = withOptional(
      new HttpParams().set('page', page).set('pageSize', pageSize),
      'status',
      status,
    );
    return this.http.get<Page<Claim>>('/api/admin/support/claims', { params });
  }

  answerClaim(id: string, reply: string): Observable<Claim> {
    return this.http.post<Claim>(`/api/admin/support/claims/${id}/answer`, { reply }, idempotent());
  }

  closeClaim(id: string): Observable<Claim> {
    return this.http.post<Claim>(`/api/admin/support/claims/${id}/close`, {}, idempotent());
  }

  // ---------------------------------------------------------------------
  // Money figures
  // ---------------------------------------------------------------------

  revenue(from: string, to: string, kioskId: string | null): Observable<RevenueReport> {
    const params = withOptional(
      new HttpParams().set('from', from).set('to', to),
      'kioskId',
      kioskId,
    );
    return this.http.get<RevenueReport>('/api/admin/reports/revenue', { params });
  }

  paymentsDashboard(): Observable<PaymentsDashboard> {
    return this.http.get<PaymentsDashboard>('/api/admin/dashboard/payments');
  }
}
