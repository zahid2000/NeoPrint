import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, KioskBrandingView } from '../../core/api/admin-api.service';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { NotifyService } from '../../shared/notify.service';
import { Problem, toProblem } from '../../shared/problem';
import { shrinkPhoto } from './kiosk-photo.component';

export const BRANDING_LANGUAGES = ['az', 'en', 'ru'] as const;
type Language = (typeof BRANDING_LANGUAGES)[number];

/** The groups the texts are shown in, in the kiosk's order (docs/DESIGN_SYSTEM.md, T-387). */
export const BRANDING_GROUPS: readonly { titleKey: string; keys: readonly string[] }[] = [
  { titleKey: 'kioskBranding.groupHome', keys: ['heroTitle', 'heroSubtitle'] },
  {
    titleKey: 'kioskBranding.groupCards',
    keys: ['usbTitle', 'usbHint', 'phoneTitle', 'phoneHint', 'codeTitle', 'codeHint'],
  },
  {
    titleKey: 'kioskBranding.groupFeatures',
    keys: [
      'featureSizes',
      'featureSizesHint',
      'featureColor',
      'featureColorHint',
      'featureFast',
      'featureFastHint',
      'featureSafe',
      'featureSafeHint',
    ],
  },
  { titleKey: 'kioskBranding.groupHotline', keys: ['hotline'] },
];

type Texts = Record<string, Record<Language, string>>;

function empty(): Texts {
  const texts: Texts = {};
  for (const group of BRANDING_GROUPS) {
    for (const key of group.keys) {
      texts[key] = { az: '', en: '', ru: '' };
    }
  }
  return texts;
}

/**
 * What kiosks say on their welcome screen and footer, and the picture beside
 * the big line (T-387): for every kiosk (no `kioskId`) or for one.
 */
@Component({
  selector: 'app-kiosk-branding',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    TranslatePipe,
    ErrorBannerComponent,
  ],
  template: `
    <section class="branding">
      <header>
        <h2>{{ (kioskId() ? 'kioskBranding.kioskTitle' : 'kioskBranding.title') | translate }}</h2>
        <p class="neo-caption">
          {{ (kioskId() ? 'kioskBranding.kioskHint' : 'kioskBranding.hint') | translate }}
        </p>
      </header>

      @if (problem(); as current) {
        <app-error-banner
          [problem]="current"
          effect="kioskBranding.failedEffect"
          [retryable]="false"
        />
      }

      <div class="branding__image">
        <div class="branding__frame">
          @if (imageUrl(); as src) {
            <img [src]="src" [alt]="'kioskBranding.imageAlt' | translate" />
          } @else {
            <mat-icon aria-hidden="true">print</mat-icon>
          }
        </div>
        <div class="branding__image-body">
          <h3>{{ 'kioskBranding.imageTitle' | translate }}</h3>
          <p class="neo-caption">{{ imageNote() | translate }}</p>
          @if (canEdit()) {
            <div class="branding__actions">
              <input
                #picker
                type="file"
                accept="image/jpeg,image/png,image/webp"
                hidden
                (change)="chosen(picker)"
              />
              <button mat-stroked-button type="button" [disabled]="busy()" (click)="picker.click()">
                <mat-icon>add_photo_alternate</mat-icon>
                {{ (view()?.image ? 'kioskBranding.replace' : 'kioskBranding.upload') | translate }}
              </button>
              @if (view()?.image) {
                <button
                  mat-button
                  type="button"
                  class="branding__remove"
                  [disabled]="busy()"
                  (click)="removeImage()"
                >
                  <mat-icon>delete_outline</mat-icon>
                  {{ 'kioskBranding.remove' | translate }}
                </button>
              }
            </div>
          }
        </div>
      </div>

      @for (group of groups; track group.titleKey) {
        <div class="branding__group">
          <h3>{{ group.titleKey | translate }}</h3>
          @for (key of group.keys; track key) {
            <div class="branding__row">
              <span class="branding__label">{{ 'kioskBranding.key.' + key | translate }}</span>
              <div class="branding__fields">
                @for (language of languages; track language) {
                  <mat-form-field appearance="outline" subscriptSizing="dynamic">
                    <mat-label>{{ language.toUpperCase() }}</mat-label>
                    <input
                      matInput
                      [name]="key + '-' + language"
                      [maxlength]="maxLength"
                      [disabled]="!canEdit() || busy()"
                      [placeholder]="placeholder(key, language) | translate"
                      [ngModel]="texts()[key][language]"
                      (ngModelChange)="edit(key, language, $event)"
                    />
                    <mat-hint align="end"
                      >{{ texts()[key][language].length }} / {{ maxLength }}</mat-hint
                    >
                  </mat-form-field>
                }
              </div>
            </div>
          }
        </div>
      }

      @if (canEdit()) {
        <div class="branding__save">
          <button
            mat-flat-button
            color="primary"
            type="button"
            [disabled]="busy() || !dirty()"
            (click)="save()"
          >
            @if (busy()) {
              <mat-spinner diameter="18" class="neo-button-spinner" />
            }
            {{ 'common.save' | translate }}
          </button>
        </div>
      }
    </section>
  `,
  styles: `
    .branding {
      display: flex;
      flex-direction: column;
      gap: var(--space-4);
    }

    .branding header h2,
    .branding h3 {
      margin: 0;
    }

    .branding__image {
      display: flex;
      gap: var(--space-4);
      flex-wrap: wrap;
      align-items: flex-start;
    }

    .branding__frame {
      width: 240px;
      height: 135px;
      border-radius: var(--radius-md);
      background: var(--brand-50);
      display: flex;
      align-items: center;
      justify-content: center;
      overflow: hidden;
      flex: none;
    }

    .branding__frame img {
      width: 100%;
      height: 100%;
      object-fit: contain;
    }

    .branding__frame mat-icon {
      color: var(--brand-500);
      font-size: var(--space-12);
      width: var(--space-12);
      height: var(--space-12);
    }

    .branding__image-body {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      flex: 1 1 240px;
    }

    .branding__actions {
      display: flex;
      gap: var(--space-2);
      flex-wrap: wrap;
    }

    .branding__remove {
      color: var(--danger-500);
    }

    .branding__group {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      padding: var(--space-4);
      border: 1px solid var(--neutral-200);
      border-radius: var(--radius-md);
      background: var(--neutral-0);
    }

    .branding__row {
      display: grid;
      grid-template-columns: 200px 1fr;
      gap: var(--space-4);
      align-items: start;
    }

    .branding__label {
      font-size: var(--font-size-body-small);
      font-weight: 600;
      padding-top: var(--space-4);
    }

    .branding__fields {
      display: grid;
      grid-template-columns: repeat(3, minmax(0, 1fr));
      gap: var(--space-2);
    }

    .branding__save {
      display: flex;
      justify-content: flex-end;
    }

    @media (max-width: 900px) {
      .branding__row,
      .branding__fields {
        grid-template-columns: 1fr;
      }

      .branding__label {
        padding-top: 0;
      }
    }
  `,
})
export class KioskBrandingComponent implements OnInit, OnDestroy {
  private readonly api = inject(AdminApiService);
  private readonly notify = inject(NotifyService);

  /** One kiosk's own; without it, the default for every kiosk. */
  readonly kioskId = input<string | null>(null);
  readonly canEdit = input(false);

  readonly groups = BRANDING_GROUPS;
  readonly languages = BRANDING_LANGUAGES;
  readonly maxLength = 160;

  readonly view = signal<KioskBrandingView | null>(null);
  readonly texts = signal<Texts>(empty());
  readonly dirty = signal(false);
  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);
  readonly imageUrl = signal<string | null>(null);

  readonly imageNote = computed(() => {
    const view = this.view();
    if (view?.image) {
      return 'kioskBranding.imageOwn';
    }
    return this.kioskId() && view?.defaultImage
      ? 'kioskBranding.imageDefault'
      : 'kioskBranding.imageNone';
  });

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  ngOnDestroy(): void {
    this.release();
  }

  /** What shows when the field is empty: the default's text, else the kiosk's own words. */
  placeholder(key: string, language: Language): string {
    return this.view()?.defaults?.[key]?.[language] ?? 'kioskBranding.builtIn';
  }

  edit(key: string, language: Language, value: string): void {
    this.texts.update((texts) => ({ ...texts, [key]: { ...texts[key], [language]: value } }));
    this.dirty.set(true);
  }

  async save(): Promise<void> {
    await this.run(async () => {
      const values: Record<string, Record<string, string>> = {};
      for (const [key, byLanguage] of Object.entries(this.texts())) {
        values[key] = { ...byLanguage };
      }
      this.apply(await firstValueFrom(this.api.setKioskBranding(this.kioskId(), values)));
      this.notify.success('kioskBranding.saved');
    });
  }

  async chosen(picker: HTMLInputElement): Promise<void> {
    const file = picker.files?.[0];
    picker.value = '';
    if (!file) {
      return;
    }

    await this.run(async () => {
      let image: Blob;
      try {
        image = await shrinkPhoto(file, 1600);
      } catch {
        this.problem.set({ message: 'kioskBranding.notImage' });
        return;
      }
      this.apply(
        await firstValueFrom(this.api.setKioskBrandingImage(this.kioskId(), image)),
        false,
      );
      this.notify.success('kioskBranding.saved');
    });
  }

  async removeImage(): Promise<void> {
    await this.run(async () => {
      this.apply(await firstValueFrom(this.api.removeKioskBrandingImage(this.kioskId())), false);
      this.notify.success('kioskBranding.imageRemoved');
    });
  }

  private async load(): Promise<void> {
    await this.run(async () =>
      this.apply(await firstValueFrom(this.api.kioskBranding(this.kioskId()))),
    );
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      await work();
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.busy.set(false);
    }
  }

  /** The answer in place; the typed texts stay when only the picture changed. */
  private apply(view: KioskBrandingView, withTexts = true): void {
    this.view.set(view);
    if (withTexts) {
      const texts = empty();
      for (const [key, byLanguage] of Object.entries(view.texts ?? {})) {
        if (texts[key]) {
          texts[key] = { ...texts[key], ...byLanguage };
        }
      }
      this.texts.set(texts);
      this.dirty.set(false);
    }
    this.showImage(view);
  }

  private showImage(view: KioskBrandingView): void {
    this.release();
    // Only the picture set here: the default's needs settings.manage, and its note says it is used.
    if (!view.image) {
      return;
    }
    this.api.kioskBrandingImage(this.kioskId()).subscribe({
      next: (blob) => {
        this.release();
        this.imageUrl.set(URL.createObjectURL(blob));
      },
      error: () => this.imageUrl.set(null),
    });
  }

  private release(): void {
    const current = this.imageUrl();
    if (current) {
      URL.revokeObjectURL(current);
      this.imageUrl.set(null);
    }
  }
}
