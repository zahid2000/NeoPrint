import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import {
  AdminApiService,
  LegalDocument,
  LegalDocumentType,
} from '../../core/api/admin-api.service';
import { supportedLanguages } from '../../core/i18n/translations';
import { describeError } from '../login/login.component';
import { DateTimePipe } from '../../shared/format.pipes';

/**
 * Legal documents (plan section 2.13).
 *
 * Publishing adds a version rather than editing one: users who accepted the
 * previous text are asked to accept the new one at their next sign-in, and
 * their consent records point at the version they actually saw. That only
 * works if every published version stays.
 */
@Component({
  selector: 'app-legal-documents',
  imports: [
    ReactiveFormsModule,
    DateTimePipe,
    MatCardModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatChipsModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatProgressBarModule,
    MatTooltipModule,
    TranslatePipe,
  ],
  templateUrl: './legal-documents.component.html',
  styleUrl: './legal-documents.component.scss',
})
export class LegalDocumentsComponent {
  private readonly api = inject(AdminApiService);
  private readonly formBuilder = inject(FormBuilder);
  private readonly snackBar = inject(MatSnackBar);
  private readonly translate = inject(TranslateService);

  readonly documentTypes: readonly LegalDocumentType[] = ['Privacy', 'Terms'];
  readonly languages = supportedLanguages;

  readonly documents = signal<LegalDocument[]>([]);
  readonly selected = signal<LegalDocument | null>(null);

  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly showForm = signal(false);

  readonly columns = [
    'docType',
    'version',
    'language',
    'publishedAt',
    'acceptances',
    'actions',
  ] as const;

  readonly form = this.formBuilder.nonNullable.group({
    docType: ['Privacy' as LegalDocumentType, [Validators.required]],
    version: ['', [Validators.required, Validators.maxLength(40)]],
    language: ['az', [Validators.required]],
    content: ['', [Validators.required]],
  });

  /** The version in force for each type and language. */
  readonly current = computed(() => this.documents().filter((document) => document.isCurrent));

  constructor() {
    void this.reload();
  }

  async reload(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set(null);

    try {
      this.documents.set(await firstValueFrom(this.api.listLegalDocuments()));
    } catch (error) {
      this.errorMessage.set(describeError(error));
    } finally {
      this.loading.set(false);
    }
  }

  view(document: LegalDocument): void {
    this.selected.set(document);
    this.showForm.set(false);
  }

  /**
   * Starts a new version, prefilled from the one in force.
   *
   * Most edits are small, so starting from the current text is less error-prone
   * than starting from nothing - and the version number is left blank, because
   * it has to be new.
   */
  startNewVersion(from?: LegalDocument): void {
    const source = from ?? this.selected() ?? this.current()[0];

    this.showForm.set(true);
    this.selected.set(null);

    this.form.reset({
      docType: source?.docType ?? 'Privacy',
      version: '',
      language: source?.language ?? 'az',
      content: source?.content ?? '',
    });
  }

  cancel(): void {
    this.showForm.set(false);
  }

  async publish(): Promise<void> {
    if (this.form.invalid || this.saving()) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    this.errorMessage.set(null);

    const form = this.form.getRawValue();

    try {
      await firstValueFrom(
        this.api.publishLegalDocument({
          docType: form.docType,
          version: form.version.trim(),
          language: form.language,
          content: form.content,
        }),
      );

      this.showForm.set(false);
      await this.reload();

      this.notify('legal.published');
    } catch (error) {
      // The server refuses a version number that is already published: the
      // same version with different text would change what people accepted.
      this.errorMessage.set(describeError(error));
    } finally {
      this.saving.set(false);
    }
  }

  private notify(messageKey: string): void {
    this.snackBar.open(this.translate.instant(messageKey) as string, undefined, {
      duration: 4000,
    });
  }
}
