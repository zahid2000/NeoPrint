import { Injectable, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslateService } from '@ngx-translate/core';

/**
 * Toasts, as docs/DESIGN_SYSTEM.md section 4 defines them for the panel: top
 * right; a success leaves by itself after three seconds, a failure stays until
 * it is dismissed, so it cannot be missed.
 */
@Injectable({ providedIn: 'root' })
export class NotifyService {
  private readonly snackBar = inject(MatSnackBar);
  private readonly translate = inject(TranslateService);

  /** A translation key, with its parameters. */
  success(key: string, params?: Record<string, unknown>): void {
    this.snackBar.open(this.translate.instant(key, params) as string, undefined, {
      duration: 3000,
      horizontalPosition: 'end',
      verticalPosition: 'top',
      panelClass: 'neo-snackbar--success',
    });
  }

  /** A translation key, or a message the server already worded. */
  error(keyOrMessage: string, params?: Record<string, unknown>): void {
    this.snackBar.open(
      this.translate.instant(keyOrMessage, params) as string,
      this.translate.instant('common.close') as string,
      {
        horizontalPosition: 'end',
        verticalPosition: 'top',
        panelClass: 'neo-snackbar--error',
      },
    );
  }
}
