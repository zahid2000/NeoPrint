import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, ContactChannel } from '../../core/api/admin-api.service';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { NotifyService } from '../../shared/notify.service';
import { Problem, toProblem } from '../../shared/problem';

const ICONS: Record<ContactChannel['kind'], string> = {
  Phone: 'call',
  WhatsApp: 'chat',
  Telegram: 'send',
  Email: 'mail',
};

/**
 * "Əlaqə" (T-438): the hotline, WhatsApp, Telegram and email the app shows,
 * each written here and switched on or off. The link each one opens is the
 * server's, shown under it once saved.
 */
@Component({
  selector: 'app-contact-channels',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ErrorBannerComponent,
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSlideToggleModule,
    TranslatePipe,
  ],
  template: `
    <div class="neo-page contact">
      <header>
        <h1>{{ 'contact.title' | translate }}</h1>
        <p class="neo-muted neo-body-small">{{ 'contact.description' | translate }}</p>
      </header>

      @if (problem(); as current) {
        <app-error-banner [problem]="current" effect="contact.failedEffect" [retryable]="false" />
      }

      <section class="neo-card contact__list">
        @for (channel of channels(); track channel.kind; let i = $index) {
          <div class="contact__row" [attr.data-kind]="channel.kind">
            <span class="contact__icon"
              ><mat-icon aria-hidden="true">{{ icon(channel.kind) }}</mat-icon></span
            >
            <mat-form-field class="contact__field" subscriptSizing="dynamic">
              <mat-label>{{ 'contact.kind.' + channel.kind | translate }}</mat-label>
              <input
                matInput
                maxlength="120"
                [disabled]="busy()"
                [ngModel]="channel.value"
                (ngModelChange)="edit(i, { value: $event })"
              />
              <mat-hint>{{ 'contact.hint.' + channel.kind | translate }}</mat-hint>
            </mat-form-field>
            <div class="contact__state">
              <mat-slide-toggle
                [disabled]="busy()"
                [checked]="channel.isActive"
                (change)="edit(i, { isActive: $event.checked })"
                >{{ 'contact.active' | translate }}</mat-slide-toggle
              >
              <span class="neo-caption">
                @if (channel.isActive && channel.link) {
                  {{ 'contact.opens' | translate: { link: channel.link } }}
                } @else if (!channel.isActive) {
                  {{ 'contact.notShown' | translate }}
                }
              </span>
            </div>
          </div>
        }
      </section>

      <div class="neo-actions">
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
    </div>
  `,
  styles: `
    .contact {
      display: flex;
      flex-direction: column;
      gap: var(--space-4);
      max-width: 960px;
    }

    .contact header h1,
    .contact header p {
      margin: 0;
    }

    .contact__list {
      display: flex;
      flex-direction: column;
      gap: var(--space-4);
    }

    .contact__row {
      display: grid;
      grid-template-columns: auto minmax(0, 1fr) minmax(200px, 280px);
      gap: var(--space-4);
      align-items: start;
    }

    .contact__icon {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: var(--control-height-lg);
      height: var(--control-height-lg);
      border-radius: var(--radius-md);
      background: var(--brand-50);
      color: var(--brand-600);
    }

    .contact__state {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      padding-top: var(--space-3);
      overflow-wrap: anywhere;
    }

    @media (max-width: 720px) {
      .contact__row {
        grid-template-columns: auto minmax(0, 1fr);
      }

      .contact__state {
        grid-column: 2;
        padding-top: 0;
      }
    }
  `,
})
export class ContactChannelsComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly notify = inject(NotifyService);

  readonly channels = signal<ContactChannel[]>([]);
  readonly saved = signal<ContactChannel[]>([]);
  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);

  readonly dirty = computed(
    () =>
      JSON.stringify(
        this.channels().map(({ kind, value, isActive }) => [kind, value, isActive]),
      ) !==
      JSON.stringify(this.saved().map(({ kind, value, isActive }) => [kind, value, isActive])),
  );

  async ngOnInit(): Promise<void> {
    await this.run(async () => this.apply(await firstValueFrom(this.api.contactChannels())));
  }

  icon(kind: ContactChannel['kind']): string {
    return ICONS[kind] ?? 'help';
  }

  edit(index: number, change: Partial<ContactChannel>): void {
    this.channels.update((all) =>
      all.map((channel, i) => (i === index ? { ...channel, ...change, link: null } : channel)),
    );
  }

  async save(): Promise<void> {
    await this.run(async () => {
      this.apply(await firstValueFrom(this.api.setContactChannels(this.channels())));
      this.notify.success('contact.saved');
    });
  }

  private apply(channels: ContactChannel[]): void {
    this.channels.set(channels);
    this.saved.set(channels);
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
}
