import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService } from '../../core/api/admin-api.service';
import { FileFormatItem, FileFormats, FormatGroup } from '../../core/api/step2.models';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';

/** A row of the plan's format table, with its formats. */
interface FormatSection {
  readonly group: FormatGroup;
  readonly formats: readonly FileFormatItem[];
}

const groupOrder: readonly FormatGroup[] = [
  'Pdf',
  'TextDocument',
  'Spreadsheet',
  'Presentation',
  'Image',
  'PageDescription',
];

/**
 * The "Fayl formatları" page (plan sections 2.2, 8A.3): which formats are
 * accepted, and the default size, page and quota limits.
 *
 * The catalogue itself is fixed in the backend - a format needs a converter -
 * so this page switches formats on and off; it cannot add one.
 */
@Component({
  selector: 'app-file-formats',
  imports: [
    FormsModule,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    TranslatePipe,
    PageHeaderComponent,
    ErrorBannerComponent,
  ],
  templateUrl: './file-formats.component.html',
  styleUrl: './file-formats.component.scss',
})
export class FileFormatsComponent {
  private readonly api = inject(AdminApiService);
  private readonly notify = inject(NotifyService);

  readonly saved = signal<FileFormats | null>(null);
  readonly enabled = signal<ReadonlySet<string>>(new Set());
  readonly maxSizeMb = signal(50);
  readonly maxPages = signal(200);
  readonly quotaGb = signal(1);

  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly problem = signal<Problem | null>(null);

  readonly sections = computed<FormatSection[]>(() => {
    const formats = this.saved()?.formats ?? [];

    return groupOrder
      .map((group) => ({ group, formats: formats.filter((format) => format.group === group) }))
      .filter((section) => section.formats.length > 0);
  });

  readonly enabledCount = computed(() => this.enabled().size);

  readonly totalCount = computed(() => this.saved()?.formats.length ?? 0);

  /** Limits the backend would accept (UpdateFileFormatsCommandValidator). */
  readonly limitsValid = computed(
    () =>
      this.inRange(this.maxSizeMb(), 1, 2048) &&
      this.inRange(this.maxPages(), 1, 10_000) &&
      this.inRange(this.quotaGb(), 1, 1000),
  );

  readonly dirty = computed(() => {
    const saved = this.saved();

    if (!saved) {
      return false;
    }

    const savedEnabled = saved.formats
      .filter((format) => format.enabled)
      .map((format) => format.extension);

    return (
      savedEnabled.length !== this.enabled().size ||
      savedEnabled.some((extension) => !this.enabled().has(extension)) ||
      saved.defaultMaxSizeMb !== this.maxSizeMb() ||
      saved.defaultMaxPages !== this.maxPages() ||
      saved.userQuotaGb !== this.quotaGb()
    );
  });

  readonly canSave = computed(
    () => this.dirty() && this.limitsValid() && this.enabled().size > 0 && !this.saving(),
  );

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.problem.set(null);

    try {
      this.apply(await firstValueFrom(this.api.getFileFormats()));
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }

  isEnabled(extension: string): boolean {
    return this.enabled().has(extension);
  }

  toggle(extension: string, on: boolean): void {
    this.enabled.update((current) => {
      const next = new Set(current);

      if (on) {
        next.add(extension);
      } else {
        next.delete(extension);
      }

      return next;
    });
  }

  sectionState(section: FormatSection): 'all' | 'some' | 'none' {
    const on = section.formats.filter((format) => this.enabled().has(format.extension)).length;

    return on === 0 ? 'none' : on === section.formats.length ? 'all' : 'some';
  }

  toggleSection(section: FormatSection, on: boolean): void {
    for (const format of section.formats) {
      this.toggle(format.extension, on);
    }
  }

  revert(): void {
    const saved = this.saved();

    if (saved) {
      this.apply(saved);
    }
  }

  async save(): Promise<void> {
    if (!this.canSave()) {
      return;
    }

    this.saving.set(true);

    try {
      const result = await firstValueFrom(
        this.api.updateFileFormats({
          enabled: [...this.enabled()],
          defaultMaxSizeMb: this.maxSizeMb(),
          defaultMaxPages: this.maxPages(),
          userQuotaGb: this.quotaGb(),
        }),
      );

      this.apply(result);
      this.notify.success('formats.saved');
    } catch (error) {
      this.notify.error(toProblem(error).message);
    } finally {
      this.saving.set(false);
    }
  }

  private apply(formats: FileFormats): void {
    this.saved.set(formats);
    this.enabled.set(
      new Set(formats.formats.filter((format) => format.enabled).map((format) => format.extension)),
    );
    this.maxSizeMb.set(formats.defaultMaxSizeMb);
    this.maxPages.set(formats.defaultMaxPages);
    this.quotaGb.set(formats.userQuotaGb);
  }

  private inRange(value: number, min: number, max: number): boolean {
    return Number.isInteger(value) && value >= min && value <= max;
  }
}
