import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Router } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';

/**
 * Sign-in for the admin panel (plan sections 2.8, 8A.3).
 *
 * The lockout message is shown as the server words it: an administrator locked
 * out by someone else's attempts needs to understand why they cannot get in.
 */
@Component({
  selector: 'app-login',
  imports: [
    ReactiveFormsModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    TranslatePipe,
  ],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss',
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly formBuilder = inject(FormBuilder);

  readonly form = this.formBuilder.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required]],
  });

  readonly busy = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly showPassword = signal(false);

  togglePassword(): void {
    this.showPassword.update((shown) => !shown);
  }

  async submit(): Promise<void> {
    if (this.form.invalid || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.errorMessage.set(null);

    const { email, password } = this.form.getRawValue();

    try {
      const response = await firstValueFrom(this.auth.signIn(email, password));

      // Plan section 2.8: the seeded password came from a configuration value,
      // so it has to be replaced before anything else is reachable.
      await this.router.navigate([response.admin.mustChangePassword ? '/change-password' : '/']);
    } catch (error) {
      this.errorMessage.set(describeError(error));
    } finally {
      this.busy.set(false);
    }
  }
}

/**
 * Turns a failed request into something worth reading.
 *
 * Prefers the server's own wording - it is already in the right language and
 * says the useful thing, such as how many minutes a lockout has left.
 */
export function describeError(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    return 'error.server';
  }

  if (error.status === 0) {
    return 'error.network';
  }

  const body = error.error as
    | { detail?: string; title?: string; errors?: Record<string, string[]> }
    | null
    | undefined;

  const fromErrors = body?.errors ? Object.values(body.errors).flat()[0] : undefined;

  return body?.detail ?? fromErrors ?? body?.title ?? 'error.server';
}
