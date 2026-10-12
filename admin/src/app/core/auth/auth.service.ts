import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { tap } from 'rxjs/operators';

import { AdminLoginResponse, AdminSummary } from './auth.models';

/** A token with less than this left is exchanged before it is used for a new connection. */
const tokenMarginMs = 30_000;

/**
 * Sign-in state for the admin panel (plan sections 2.8, 8A.1).
 *
 * The access token is held in a signal and never written to storage: it lives
 * fifteen minutes, and anything in `localStorage` is readable by any script on
 * the page. The refresh token is an HttpOnly cookie the panel cannot see at
 * all, which is what makes a stolen script harmless here.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private readonly accessToken = signal<string | null>(null);

  /** When the access token stops being accepted, in epoch milliseconds. */
  private accessTokenExpiresAt = 0;
  private readonly currentAdmin = signal<AdminSummary | null>(null);
  private readonly restoring = signal(true);

  /** The signed-in administrator, or null. */
  readonly admin = this.currentAdmin.asReadonly();

  /** True while the session is being restored from the refresh cookie. */
  readonly isRestoring = this.restoring.asReadonly();

  readonly isSignedIn = computed(() => this.currentAdmin() !== null);

  /**
   * Whether a password change is outstanding. The router blocks everything
   * else until it clears (plan section 2.8).
   */
  readonly mustChangePassword = computed(() => this.currentAdmin()?.mustChangePassword ?? false);

  /** The current access token, for the interceptor. */
  get token(): string | null {
    return this.accessToken();
  }

  /**
   * Whether the signed-in administrator holds a permission.
   *
   * This drives the menu and the buttons only. Every admin endpoint is
   * enforced on the server; hiding a control the user cannot use is courtesy,
   * not protection (plan section 8A.2).
   */
  can(permission: string): boolean {
    return this.currentAdmin()?.permissions.includes(permission) ?? false;
  }

  /** Whether any of these permissions is held. */
  canAny(permissions: readonly string[]): boolean {
    return permissions.some((permission) => this.can(permission));
  }

  /** Whether a kiosk is inside the administrator's scope. */
  canSeeKiosk(kioskId: string): boolean {
    const admin = this.currentAdmin();
    if (!admin) {
      return false;
    }

    return admin.allKiosks || admin.kioskIds.includes(kioskId);
  }

  signIn(email: string, password: string): Observable<AdminLoginResponse> {
    return this.http
      .post<AdminLoginResponse>('/api/auth/admin/login', { email, password })
      .pipe(tap((response) => this.apply(response)));
  }

  /**
   * Exchanges the refresh cookie for a new access token.
   *
   * Used both at startup and by the interceptor on a 401. The cookie is sent
   * automatically because the panel and the API share an origin behind nginx
   * (plan section 8A.1).
   */
  refresh(): Observable<AdminLoginResponse> {
    return this.http
      .post<AdminLoginResponse>('/api/auth/admin/refresh', {})
      .pipe(tap((response) => this.apply(response)));
  }

  /** Restores a session at startup. Silent: no session is the normal state. */
  async restore(): Promise<void> {
    try {
      await firstValueFrom(this.refresh());
    } catch {
      this.clear();
    } finally {
      this.restoring.set(false);
    }
  }

  /**
   * Re-reads the administrator's own permissions.
   *
   * Called when the server announces a role change over SignalR, so the menu
   * reflects it without signing in again (plan section 8A.2).
   *
   * The token is exchanged first, not only the profile: a permission the
   * account has just been given is not among the claims of the token it is
   * holding, and the API confirms the claim as well as the resolver. Reading
   * /api/admin/me alone would open the menu item while every request behind it
   * was still refused.
   */
  async reloadPermissions(): Promise<void> {
    try {
      await firstValueFrom(this.refresh());
    } catch {
      // No new token: the interceptor will exchange it on the next 401, and
      // the profile below still shows what the account may do.
    }

    try {
      const admin = await firstValueFrom(this.http.get<AdminSummary>('/api/admin/me'));

      this.currentAdmin.set(admin);
    } catch {
      // A failed reload leaves the previous permissions in place. The server
      // is the one that enforces them, so a stale menu is cosmetic.
    }
  }

  async signOut(): Promise<void> {
    try {
      await firstValueFrom(this.http.post('/api/auth/admin/logout', {}));
    } catch {
      // Signing out locally must work even with no connection.
    }

    this.clear();
    await this.router.navigate(['/login']);
  }

  /**
   * Re-reads the administrator's profile - permissions and kiosk scope - with
   * the token the panel holds.
   *
   * An ordinary request on purpose: if the token has run out, the interceptor
   * exchanges it on the way, and it is the interceptor that makes sure only
   * one exchange is in flight. Calling {@link refresh} from here as well
   * could send two at once, and the server answers a replayed refresh token
   * by ending the whole session (plan section 2.8).
   */
  async reloadProfile(): Promise<void> {
    try {
      this.currentAdmin.set(await firstValueFrom(this.http.get<AdminSummary>('/api/admin/me')));
    } catch {
      // The profile on screen stays; the server enforces whatever it says.
    }
  }

  /**
   * The access token for a connection that is about to be opened, exchanged
   * first if it has run out or is about to.
   *
   * The live connection outlives the token it was opened with, and when it
   * has to be opened again - the API restarted, the laptop woke up, the
   * server closed it with the token - nothing else may have asked the API
   * for anything in the meantime. Handing over the old token would be
   * refused, again and again, for as long as the panel sat idle (T-248).
   */
  async freshToken(): Promise<string> {
    if (this.accessToken() && this.accessTokenExpiresAt - Date.now() < tokenMarginMs) {
      await this.reloadProfile();
    }

    return this.accessToken() ?? '';
  }

  /** Called by the interceptor when the session cannot be recovered. */
  onSessionExpired(): void {
    this.clear();
    void this.router.navigate(['/login']);
  }

  private apply(response: AdminLoginResponse): void {
    this.accessToken.set(response.accessToken);

    // Unreadable is "unknown", not "already over": the server is the judge.
    const expiresAt = Date.parse(response.accessTokenExpiresAt);
    this.accessTokenExpiresAt = Number.isNaN(expiresAt) ? Number.MAX_SAFE_INTEGER : expiresAt;
    this.currentAdmin.set(response.admin);
  }

  private clear(): void {
    this.accessToken.set(null);
    this.accessTokenExpiresAt = 0;
    this.currentAdmin.set(null);
  }
}
