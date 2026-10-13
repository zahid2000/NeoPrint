import { Component, DestroyRef, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService } from '../../core/api/admin-api.service';
import { CapabilityOverrides, KioskCommand, KioskPrinters, PrinterCapabilities } from '../../core/api/step3.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe } from '../../shared/format.pipes';
import { commandResults } from '../../core/realtime/command-results';
import { NotifyService } from '../../shared/notify.service';
import { Problem, toProblem } from '../../shared/problem';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { HubEvents } from '../../core/realtime/hub-events';

type Capability = keyof PrinterCapabilities;

/** What an override select offers: follow the printer, or switch the capability off. */
type OverrideChoice = 'follow' | 'off';

/**
 * Kiosk detail → Printers (plan sections 7.7, 8A.3): what the kiosk found,
 * which one prints, what the kiosk may offer, and the commands that ask the
 * kiosk to look again or to print its test page. Results arrive live.
 */
@Component({
  selector: 'app-kiosk-printers',
  imports: [
    FormsModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatTableModule,
    TranslatePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
    StatusChipComponent,
    DateTimePipe,
  ],
  template: `
    <p class="neo-muted neo-body-small">{{ 'printers.tabDescription' | translate }}</p>

    @if (problem(); as problem) {
      <app-error-banner [problem]="problem" effect="printers.loadFailedEffect" (retry)="load()" />
    }

    @if (loading()) {
      <div class="neo-skeleton neo-skeleton--block"></div>
    } @else if (view(); as current) {
      <div class="printers__actions">
        @if (canRefresh()) {
          <button mat-stroked-button type="button" [disabled]="busy() !== null" (click)="send('RefreshPrinters')">
            @if (busy() === 'RefreshPrinters') {
              <mat-spinner diameter="18" class="neo-button-spinner" />
            } @else {
              <mat-icon>refresh</mat-icon>
            }
            {{ 'printers.refresh' | translate }}
          </button>
        }
        @if (canTestPrint() && current.selectedPrinter) {
          <button mat-flat-button type="button" [disabled]="busy() !== null" (click)="send('TestPrint')">
            @if (busy() === 'TestPrint') {
              <mat-spinner diameter="18" class="neo-button-spinner" />
            } @else {
              <mat-icon>print</mat-icon>
            }
            {{ 'printers.testPrint' | translate }}
          </button>
        }
      </div>

      @if (!current.selectedPrinter) {
        <p class="neo-field-error" role="status">
          <mat-icon aria-hidden="true">warning</mat-icon>
          {{ 'printers.notSelected' | translate }}
        </p>
      }

      @if (current.printers.length === 0) {
        <app-empty-state
          icon="print_disabled"
          [message]="'printers.empty' | translate"
          [hint]="'printers.emptyHint' | translate"
        />
      } @else {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="current.printers">
            <ng-container matColumnDef="name">
              <th mat-header-cell *matHeaderCellDef>{{ 'printers.colName' | translate }}</th>
              <td mat-cell *matCellDef="let printer">
                <strong>{{ printer.name }}</strong>
              </td>
            </ng-container>
            <ng-container matColumnDef="driver">
              <th mat-header-cell *matHeaderCellDef>{{ 'printers.colDriver' | translate }}</th>
              <td mat-cell *matCellDef="let printer">{{ printer.driverName }}</td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'printers.colStatus' | translate }}</th>
              <td mat-cell *matCellDef="let printer">
                <app-status-chip
                  [status]="printer.isOnline ? 'Online' : 'Offline'"
                  [text]="(printer.isOnline ? 'printers.online' : 'printers.offline') | translate"
                />
              </td>
            </ng-container>
            <ng-container matColumnDef="capabilities">
              <th mat-header-cell *matHeaderCellDef>{{ 'printers.colCapabilities' | translate }}</th>
              <td mat-cell *matCellDef="let printer">{{ capabilityText(printer.detectedCapabilities) }}</td>
            </ng-container>
            <ng-container matColumnDef="choose">
              <th mat-header-cell *matHeaderCellDef></th>
              <td mat-cell *matCellDef="let printer">
                @if (printer.name === current.selectedPrinter) {
                  <app-status-chip status="Completed" [text]="'printers.selected' | translate" />
                } @else if (canSelect()) {
                  <button mat-stroked-button type="button" [disabled]="busy() !== null" (click)="select(printer.name)">
                    {{ 'printers.select' | translate }}
                  </button>
                }
              </td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="columns"></tr>
            <tr mat-row *matRowDef="let row; columns: columns"></tr>
          </table>
        </div>
      }

      @if (current.selectedPrinter) {
        <h3>{{ 'printers.capabilitiesTitle' | translate }}</h3>
        <p class="neo-muted neo-body-small">{{ 'printers.capabilitiesHint' | translate }}</p>
        <div class="printers__capabilities">
          @for (capability of capabilities; track capability.key) {
            <mat-form-field>
              <mat-label>{{ capability.label | translate }}</mat-label>
              @if (current.detected[capability.key]) {
                <mat-select
                  [disabled]="!canCapabilities()"
                  [ngModel]="choices()[capability.key]"
                  (ngModelChange)="choose(capability.key, $event)"
                >
                  <mat-option value="follow">{{ 'printers.follow' | translate }}</mat-option>
                  <mat-option value="off">{{ 'printers.switchedOff' | translate }}</mat-option>
                </mat-select>
              } @else {
                <mat-select [disabled]="true" value="none">
                  <mat-option value="none">{{ 'printers.notSupported' | translate }}</mat-option>
                </mat-select>
              }
            </mat-form-field>
          }
        </div>
        @if (canCapabilities()) {
          <div class="printers__save">
            <button mat-stroked-button type="button" [disabled]="!changed() || busy() !== null" (click)="saveCapabilities()">
              @if (busy() === 'capabilities') {
                <mat-spinner diameter="18" class="neo-button-spinner" />
              }
              {{ 'common.save' | translate }}
            </button>
          </div>
        }
      }

      <h3>{{ 'printers.commandsTitle' | translate }}</h3>
      @if (commands().length === 0) {
        <app-empty-state icon="terminal" [message]="'printers.commandsEmpty' | translate" />
      } @else {
        <div class="neo-table-wrapper">
          <table mat-table class="neo-table" [dataSource]="commands()">
            <ng-container matColumnDef="type">
              <th mat-header-cell *matHeaderCellDef>{{ 'printers.colCommand' | translate }}</th>
              <td mat-cell *matCellDef="let command">{{ 'printers.command.' + command.type | translate }}</td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'printers.colStatus' | translate }}</th>
              <td mat-cell *matCellDef="let command">
                <app-status-chip
                  [status]="command.status"
                  [text]="'printers.commandStatus.' + command.status | translate"
                />
              </td>
            </ng-container>
            <ng-container matColumnDef="result">
              <th mat-header-cell *matHeaderCellDef>{{ 'printers.colResult' | translate }}</th>
              <td mat-cell *matCellDef="let command">{{ command.result }}</td>
            </ng-container>
            <ng-container matColumnDef="sent">
              <th mat-header-cell *matHeaderCellDef>{{ 'printers.colSent' | translate }}</th>
              <td mat-cell *matCellDef="let command">{{ command.createdAt | dateTime }}</td>
            </ng-container>
            <ng-container matColumnDef="completed">
              <th mat-header-cell *matHeaderCellDef>{{ 'printers.colCompleted' | translate }}</th>
              <td mat-cell *matCellDef="let command">{{ command.completedAt | dateTime }}</td>
            </ng-container>

            <tr mat-header-row *matHeaderRowDef="commandColumns"></tr>
            <tr mat-row *matRowDef="let row; columns: commandColumns"></tr>
          </table>
        </div>
      }
    }
  `,
  styles: `
    h3 {
      margin: var(--space-6) 0 var(--space-2);
    }

    .printers__capabilities {
      margin-top: var(--space-4);
    }

    .printers__actions,
    .printers__save {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3);
      margin-block: var(--space-4);
    }

    .printers__save {
      justify-content: flex-end;
    }

    .printers__capabilities {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
      gap: var(--space-4);
    }
  `,
})
export class KioskPrintersComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly auth = inject(AuthService);
  private readonly hub = inject(AdminHubService);
  private readonly notify = inject(NotifyService);

  /** How each command sent from this tab ended, said aloud (T-271). */
  private readonly results = commandResults();
  private readonly translate = inject(TranslateService);

  readonly kioskId = input.required<string>();

  /** The kiosk's status as the printers answer gave it: choosing a printer or a passed test print changes it. */
  readonly statusChange = output<string>();

  readonly columns = ['name', 'driver', 'status', 'capabilities', 'choose'];
  readonly commandColumns = ['type', 'status', 'result', 'sent', 'completed'];

  readonly capabilities: readonly { key: Capability; label: string }[] = [
    { key: 'color', label: 'printers.capColor' },
    { key: 'duplex', label: 'printers.capDuplex' },
    { key: 'a3', label: 'printers.capA3' },
  ];

  readonly view = signal<KioskPrinters | null>(null);
  readonly commands = signal<KioskCommand[]>([]);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);
  readonly busy = signal<string | null>(null);
  readonly choices = signal<Record<Capability, OverrideChoice>>({ color: 'follow', duplex: 'follow', a3: 'follow' });

  readonly canSelect = computed(() => this.auth.can(Permissions.printersSelect));
  readonly canCapabilities = computed(() => this.auth.can(Permissions.printerCapabilitiesManage));
  readonly canRefresh = computed(() => this.auth.can(Permissions.printersRefresh));
  readonly canTestPrint = computed(() => this.auth.can(Permissions.printersTestPrint));

  /** Whether the override selects differ from what is saved. */
  readonly changed = computed(() => {
    const saved = this.view()?.overrides;

    return (
      !!saved &&
      this.capabilities.some(({ key }) => (saved[key] === false ? 'off' : 'follow') !== this.choices()[key])
    );
  });

  constructor() {
    // The kiosk's answers: a new printer list, a command's result.
    const off = [
      this.hub.on<{ kioskId: string }>(HubEvents.kioskPrintersUpdated, (event) => {
        if (event.kioskId === this.kioskId()) {
          void this.load();
        }
      }),
      this.hub.on<KioskCommand>(HubEvents.commandResult, (command) => {
        if (command.kioskId === this.kioskId()) {
          this.commands.update((list) => [command, ...list.filter((item) => item.id !== command.id)]);

          // A passed test print is what takes the kiosk into service.
          if (command.type === 'TestPrint' && command.status === 'Succeeded') {
            void this.load();
          }
        }
      }),
    ];
    inject(DestroyRef).onDestroy(() => off.forEach((unsubscribe) => unsubscribe()));

    // After a gap in the connection the list and the commands are asked for
    // again - unless a choice on this page is waiting to be saved.
    liveRefresh({ events: [], reload: () => (this.changed() ? null : this.load()) });
  }

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.problem.set(null);

    try {
      const [view, commands] = await Promise.all([
        firstValueFrom(this.api.kioskPrinters(this.kioskId())),
        firstValueFrom(this.api.kioskCommands(this.kioskId())),
      ]);

      this.apply(view);
      this.commands.set(commands);
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }

  capabilityText(capabilities: PrinterCapabilities): string {
    return this.capabilities
      .filter(({ key }) => capabilities[key])
      .map(({ label }) => this.translate.instant(label) as string)
      .join(', ');
  }

  choose(key: Capability, choice: OverrideChoice): void {
    this.choices.update((current) => ({ ...current, [key]: choice }));
  }

  async select(name: string): Promise<void> {
    await this.run('select', async () => {
      this.apply(await firstValueFrom(this.api.selectKioskPrinter(this.kioskId(), name)));
      this.notify.success('printers.saved');
    });
  }

  async saveCapabilities(): Promise<void> {
    const choices = this.choices();
    const overrides: CapabilityOverrides = {
      color: choices.color === 'off' ? false : null,
      duplex: choices.duplex === 'off' ? false : null,
      a3: choices.a3 === 'off' ? false : null,
    };

    await this.run('capabilities', async () => {
      this.apply(await firstValueFrom(this.api.setKioskCapabilities(this.kioskId(), overrides)));
      this.notify.success('printers.saved');
    });
  }

  async send(type: 'RefreshPrinters' | 'TestPrint'): Promise<void> {
    await this.run(type, async () => {
      const command =
        type === 'TestPrint'
          ? await firstValueFrom(this.api.testPrint(this.kioskId()))
          : await firstValueFrom(this.api.refreshKioskPrinters(this.kioskId()));

      // The kiosk may have answered already: its row is not put back to "sent".
      this.commands.update((list) => (list.some((item) => item.id === command.id) ? list : [command, ...list]));
      this.notify.success('printers.sent');
      this.results.sent(command);
    });
  }

  private async run(part: string, work: () => Promise<void>): Promise<void> {
    this.busy.set(part);

    try {
      await work();
    } catch (error) {
      this.notify.error(toProblem(error).message);
    } finally {
      this.busy.set(null);
    }
  }

  private apply(view: KioskPrinters): void {
    this.view.set(view);
    this.statusChange.emit(view.status);
    this.choices.set({
      color: view.overrides.color === false ? 'off' : 'follow',
      duplex: view.overrides.duplex === false ? 'off' : 'follow',
      a3: view.overrides.a3 === false ? 'off' : 'follow',
    });
  }
}
