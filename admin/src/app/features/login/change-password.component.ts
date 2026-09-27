import { Component, inject, signal } from '@angular/core';
import {
  AbstractControl,
  FormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService } from '../../core/api/admin-api.service';
import { AuthService } from '../../core/auth/auth.service';
import { describeError } from './login.component';

/**
 * Minimum length for an admin password.
 *
 * Longer than the mobile minimum: an admin account can refund money and
 * reconfigure kiosks. Mirrors the server's rule (plan section 2.8).
 */
export const minimumAdminPasswordLength = 12;

/** The two new-password fields must agree. */
export function passwordsMatch(group: AbstractControl): ValidationErrors | null {
  const next = group.get('newPassword')?.value as string | undefined;
  const confirm = group.get('confirmPassword')?.value as string | undefined;

  return next && confirm && next !== confirm ? { mismatch: true } : null;
}

/**
 * Changing the administrator's own password (plan sections 2.8, 8A.3).
 *
 * Mandatory at first sign-in for the seeded SuperAdmin, and the router keeps
 * the panel here until it is done. Changing it signs every session out,
 * including this one, so the panel returns to sign-in afterwards.
 */
@Component({
  selector: 'app-change-password',
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
  templateUrl: './change-password.component.html',
  styleUrl: './login.component.scss',
})
export class ChangePasswordComponent {
  private readonly api = inject(AdminApiService);
  private readonly auth = inject(AuthService);
  private readonly formBuilder = inject(FormBuilder);
  private readonly snackBar = inject(MatSnackBar);
  private readonly translate = inject(TranslateService);

  readonly minimumLength = minimumAdminPasswordLength;
  readonly isFirstLogin = this.auth.mustChangePassword;

  readonly form = this.formBuilder.nonNullable.group(
    {
      currentPassword: ['', [Validators.required]],
      newPassword: ['', [Validators.required, Validators.minLength(minimumAdminPasswordLength)]],
      confirmPassword: ['', [Validators.required]],
    },
    { validators: passwordsMatch },
  );

  readonly busy = signal(false);
  readonly errorMessage = signal<string | null>(null);

  async submit(): Promise<void> {
    if (this.form.invalid || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.errorMessage.set(null);

    const { currentPassword, newPassword } = this.form.getRawValue();

    try {
      await firstValueFrom(this.api.changePassword(currentPassword, newPassword));

      this.snackBar.open(this.translate.instant('password.changed') as string, undefined, {
        duration: 5000,
      });

      // Every session was signed out on the server, so signing out locally
      // keeps the two in step rather than leaving a dead token in memory.
      await this.auth.signOut();
    } catch (error) {
      this.errorMessage.set(describeError(error));
    } finally {
      this.busy.set(false);
    }
  }
}
