import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService } from '../../core/api/admin-api.service';
import {
  StorageProvider,
  StorageProviderInput,
  StorageProviderType,
  StorageTestResult,
  storageProviderTypes,
} from '../../core/api/step2.models';
import { toProblem } from '../../shared/problem';

/** What the dialog closes with: the provider as saved, or nothing. */
export type StorageProviderDialogResult = StorageProvider | undefined;

/**
 * Adding or editing a storage provider (plan section 2.10).
 *
 * The connection test writes, reads back and deletes a small object in every
 * bucket - what the application will actually do - and adding a provider is
 * refused by the server unless it passes. The secret key is never shown: on
 * edit, leaving it empty keeps the stored one.
 */
@Component({
  selector: 'app-storage-provider-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatSlideToggleModule,
    TranslatePipe,
  ],
  template: `
    <h2 mat-dialog-title>
      {{ (existing ? 'storage.provider.editTitle' : 'storage.provider.addTitle') | translate }}
    </h2>

    <mat-dialog-content class="neo-dialog">
      <p class="neo-dialog__explanation">{{ 'storage.provider.explanation' | translate }}</p>

      <div class="form">
        <mat-form-field class="form__wide">
          <mat-label>{{ 'storage.provider.name' | translate }}</mat-label>
          <input matInput required [ngModel]="name()" (ngModelChange)="set(name, $event)" />
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'storage.provider.type' | translate }}</mat-label>
          <mat-select [ngModel]="type()" (ngModelChange)="set(type, $event)">
            @for (option of types; track option) {
              <mat-option [value]="option">{{ option }}</mat-option>
            }
          </mat-select>
          <mat-hint>{{ 'storage.provider.typeHint' | translate }}</mat-hint>
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'storage.provider.region' | translate }}</mat-label>
          <input matInput [ngModel]="region()" (ngModelChange)="set(region, $event)" />
        </mat-form-field>

        <mat-form-field class="form__wide">
          <mat-label>{{ 'storage.provider.endpoint' | translate }}</mat-label>
          <input
            matInput
            required
            placeholder="https://s3.example.az"
            [ngModel]="endpoint()"
            (ngModelChange)="set(endpoint, $event)"
          />
          <mat-hint>{{ 'storage.provider.endpointHint' | translate }}</mat-hint>
        </mat-form-field>

        <mat-slide-toggle
          class="form__wide"
          [ngModel]="useSsl()"
          (ngModelChange)="set(useSsl, $event)"
        >
          {{ 'storage.provider.useSsl' | translate }}
        </mat-slide-toggle>

        <mat-form-field>
          <mat-label>{{ 'storage.provider.accessKey' | translate }}</mat-label>
          <input
            matInput
            autocomplete="off"
            [required]="!existing"
            [ngModel]="accessKey()"
            (ngModelChange)="set(accessKey, $event)"
          />
          @if (existing) {
            <mat-hint>{{
              'storage.provider.keyKeepHint' | translate: { hint: existing.accessKeyHint }
            }}</mat-hint>
          }
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'storage.provider.secretKey' | translate }}</mat-label>
          <input
            matInput
            type="password"
            autocomplete="new-password"
            [required]="!existing"
            [ngModel]="secretKey()"
            (ngModelChange)="set(secretKey, $event)"
          />
          @if (existing) {
            <mat-hint>{{ 'storage.provider.secretKeepHint' | translate }}</mat-hint>
          }
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'storage.provider.bucketFiles' | translate }}</mat-label>
          <input
            matInput
            required
            [ngModel]="bucketFiles()"
            (ngModelChange)="set(bucketFiles, $event)"
          />
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'storage.provider.bucketReceipts' | translate }}</mat-label>
          <input
            matInput
            required
            [ngModel]="bucketReceipts()"
            (ngModelChange)="set(bucketReceipts, $event)"
          />
        </mat-form-field>

        <mat-form-field>
          <mat-label>{{ 'storage.provider.bucketTemp' | translate }}</mat-label>
          <input
            matInput
            required
            [ngModel]="bucketTemp()"
            (ngModelChange)="set(bucketTemp, $event)"
          />
        </mat-form-field>
      </div>

      @if (existing && !secretKey() && !accessKey()) {
        <p class="neo-caption">{{ 'storage.provider.editTestHint' | translate }}</p>
      }

      @if (test(); as result) {
        <div
          class="test"
          [class.test--ok]="result.succeeded"
          [class.test--failed]="!result.succeeded"
          role="status"
        >
          <mat-icon aria-hidden="true">{{ result.succeeded ? 'check_circle' : 'error' }}</mat-icon>
          <div>
            <p>{{ result.message }}</p>
            <ul>
              @for (bucket of result.buckets; track bucket.bucket) {
                <li>
                  <code>{{ bucket.bucket }}</code
                  >:
                  {{
                    bucket.succeeded ? ('storage.provider.bucketOk' | translate) : bucket.problem
                  }}
                </li>
              }
            </ul>
          </div>
        </div>
      }

      @if (error(); as message) {
        <p class="neo-field-error" role="alert">
          <mat-icon aria-hidden="true">error</mat-icon>
          {{ message | translate }}
        </p>
      }
    </mat-dialog-content>

    <mat-dialog-actions class="neo-dialog__actions">
      <button mat-button type="button" (click)="close()">{{ 'common.cancel' | translate }}</button>
      <button
        mat-stroked-button
        type="button"
        [disabled]="!canTest() || busy()"
        (click)="runTest()"
      >
        @if (testing()) {
          <mat-spinner diameter="18" class="neo-button-spinner" />
        }
        {{ 'storage.provider.test' | translate }}
      </button>
      <button mat-flat-button type="button" [disabled]="!canSave() || busy()" (click)="save()">
        @if (saving()) {
          <mat-spinner diameter="18" class="neo-button-spinner" />
        }
        {{ 'common.save' | translate }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .form {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: var(--space-3);
    }

    .form__wide {
      grid-column: 1 / -1;
    }

    @media (max-width: 640px) {
      .form {
        grid-template-columns: 1fr;
      }
    }

    .test {
      display: flex;
      gap: var(--space-3);
      padding: var(--space-3) var(--space-4);
      border-radius: var(--radius-sm);

      ul {
        margin: var(--space-1) 0 0;
        padding-left: var(--space-4);
        font-size: var(--font-size-body-small);
      }
    }

    .test--ok {
      background: var(--success-bg);

      mat-icon {
        color: var(--success-500);
      }
    }

    .test--failed {
      background: var(--danger-bg);

      mat-icon {
        color: var(--danger-500);
      }
    }
  `,
})
export class StorageProviderDialogComponent {
  private readonly api = inject(AdminApiService);
  private readonly dialogRef =
    inject<MatDialogRef<StorageProviderDialogComponent, StorageProviderDialogResult>>(MatDialogRef);

  /** The provider being edited, or null when adding one. */
  readonly existing = inject<StorageProvider | null>(MAT_DIALOG_DATA);

  readonly types = storageProviderTypes;

  readonly name = signal(this.existing?.name ?? '');
  readonly type = signal<StorageProviderType>(this.existing?.type ?? 'RustFS');
  readonly endpoint = signal(this.existing?.endpoint ?? '');
  readonly region = signal(this.existing?.region ?? 'us-east-1');
  readonly useSsl = signal(this.existing?.useSsl ?? true);
  readonly accessKey = signal('');
  readonly secretKey = signal('');
  readonly bucketFiles = signal(this.existing?.buckets.files ?? 'neoprint-files');
  readonly bucketReceipts = signal(this.existing?.buckets.receipts ?? 'neoprint-receipts');
  readonly bucketTemp = signal(this.existing?.buckets.temp ?? 'neoprint-temp');

  readonly test = signal<StorageTestResult | null>(null);
  readonly error = signal<string | null>(null);
  readonly testing = signal(false);
  readonly saving = signal(false);
  readonly busy = computed(() => this.testing() || this.saving());

  private readonly complete = computed(
    () =>
      this.name().trim() !== '' &&
      this.endpoint().trim() !== '' &&
      (this.existing !== null || this.accessKey().trim() !== '') &&
      this.bucketFiles().trim() !== '' &&
      this.bucketReceipts().trim() !== '' &&
      this.bucketTemp().trim() !== '',
  );

  /** Credentials not saved yet can only be tested with both keys typed in. */
  readonly canTest = computed(
    () => this.complete() && this.accessKey().trim() !== '' && this.secretKey().trim() !== '',
  );

  /**
   * A new provider has to pass the test first; an edit that changes the keys
   * too. An edit that keeps them is tested by the server when it is saved.
   */
  readonly canSave = computed(() => {
    if (!this.complete()) {
      return false;
    }

    const keepsKeys = this.accessKey().trim() === '' && this.secretKey().trim() === '';

    return this.existing && keepsKeys ? true : this.test()?.succeeded === true;
  });

  set<T>(target: { set(value: T): void }, value: T): void {
    target.set(value);

    // Any change makes an earlier test result stale.
    this.test.set(null);
    this.error.set(null);
  }

  async runTest(): Promise<void> {
    this.testing.set(true);
    this.error.set(null);

    try {
      this.test.set(await firstValueFrom(this.api.testStorageCredentials(this.input())));
    } catch (error) {
      this.test.set(null);
      this.error.set(toProblem(error).message);
    } finally {
      this.testing.set(false);
    }
  }

  async save(): Promise<void> {
    this.saving.set(true);
    this.error.set(null);

    try {
      const saved = this.existing
        ? await firstValueFrom(this.api.updateStorageProvider(this.existing.id, this.input()))
        : await firstValueFrom(this.api.addStorageProvider(this.input()));

      this.dialogRef.close(saved);
    } catch (error) {
      this.error.set(toProblem(error).message);
    } finally {
      this.saving.set(false);
    }
  }

  close(): void {
    this.dialogRef.close(undefined);
  }

  private input(): StorageProviderInput {
    return {
      name: this.name().trim(),
      type: this.type(),
      endpoint: this.endpoint().trim(),
      region: this.region().trim() || null,
      useSsl: this.useSsl(),
      accessKey: this.accessKey().trim(),
      secretKey: this.secretKey().trim() || null,
      buckets: {
        files: this.bucketFiles().trim(),
        receipts: this.bucketReceipts().trim(),
        temp: this.bucketTemp().trim(),
      },
    };
  }
}
