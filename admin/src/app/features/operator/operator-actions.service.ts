import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { NotifyService } from '../../shared/notify.service';
import { toProblem } from '../../shared/problem';
import { OperatorApiService } from './operator-api.service';
import { InventoryItem, OperatorKioskCard, RefillRequest, ServiceTask } from './operator.models';
import { RefillDialogComponent, RefillDialogData } from './refill.dialog';
import { ReplacedDialogComponent, ReplacedDialogData, ReplacedDialogResult } from './replaced.dialog';

/** A dialog on a phone: as wide as the screen lets it be (docs/DESIGN_SYSTEM.md 5.4). */
export const operatorDialog = { width: 'min(480px, 95vw)', autoFocus: 'first-tabbable' } as const;

/**
 * What an operator does at a kiosk, from whichever page they start it (plan
 * section 8B): a refill, a changed cartridge, a finished task. Each asks its
 * question in a dialog, sends the answer once, and says how it went.
 *
 * Every method answers whether something was recorded, so the page knows to
 * fetch again.
 */
@Injectable({ providedIn: 'root' })
export class OperatorActionsService {
  private readonly api = inject(OperatorApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly translate = inject(TranslateService);

  /** "Kağız doldur". */
  async refill(kiosk: OperatorKioskCard, preferred?: string): Promise<boolean> {
    const request = await this.askRefill(kiosk, preferred);

    return request ? this.send(() => firstValueFrom(this.api.refill(kiosk.id, request))) : false;
  }

  /** "Toner dəyişdirildi", outside any task. */
  async replaced(kiosk: OperatorKioskCard): Promise<boolean> {
    const answer = await this.askReplaced({
      title: this.text('operator.replaced.title'),
      code: kiosk.code,
      askWhat: true,
      levels: kiosk.levels,
      items: await this.items(),
    });

    return answer ? this.send(() => firstValueFrom(this.api.replaced(kiosk.id, answer))) : false;
  }

  /**
   * "Tamamla". A refill task asks what a refill asks; any other asks what was
   * taken from the warehouse for it, and for a note.
   *
   * @returns The task as it is now, or null when nothing was sent.
   */
  async complete(task: ServiceTask): Promise<ServiceTask | null> {
    let done: ServiceTask | null = null;

    if (task.type === 'RefillPaper') {
      // The trays are the kiosk's; the task only says which one it is about.
      const kiosk = await this.kioskOf(task);

      if (!kiosk) {
        return null;
      }

      const request = await this.askRefill(kiosk, task.resource === 'PaperA3' ? 'A3' : 'A4');

      if (!request) {
        return null;
      }

      await this.send(async () => {
        done = await firstValueFrom(
          this.api.complete(task.id, {
            paper: request.paper,
            refillMode: request.mode,
            sheets: request.sheets,
            inventoryItemId: request.inventoryItemId,
            quantity: request.quantity,
          }),
        );
      });

      return done;
    }

    const answer = await this.askReplaced({
      title: this.text('operator.tasks.completeTitle'),
      code: task.kioskCode ?? undefined,
      askWhat: false,
      resource: task.resource ?? (task.type === 'ReplaceDrum' ? 'Drum' : task.type === 'ReplaceToner' ? 'Toner' : 'Other'),
      levels: [],
      items: task.type === 'ReplaceToner' || task.type === 'ReplaceDrum' ? await this.items() : [],
    });

    if (!answer) {
      return null;
    }

    await this.send(async () => {
      done = await firstValueFrom(
        this.api.complete(task.id, {
          inventoryItemId: answer.inventoryItemId,
          quantity: answer.quantity,
          note: answer.note,
        }),
      );
    });

    return done;
  }

  /** Sends, and says how it went: a short "recorded", or the server's own sentence. */
  async send(action: () => Promise<unknown>): Promise<boolean> {
    try {
      await action();
      this.notify.success('operator.done');

      return true;
    } catch (error) {
      this.notify.error(toProblem(error).message);

      return false;
    }
  }

  /** What the warehouse holds - nothing for an account that may not look at it. */
  private async items(): Promise<InventoryItem[]> {
    if (!this.auth.can(Permissions.inventoryView)) {
      return [];
    }

    try {
      return await firstValueFrom(this.api.inventory());
    } catch {
      // The refill itself does not need the warehouse.
      return [];
    }
  }

  private async kioskOf(task: ServiceTask): Promise<OperatorKioskCard | null> {
    try {
      return (await firstValueFrom(this.api.kiosk(task.kioskId))).kiosk;
    } catch (error) {
      this.notify.error(toProblem(error).message);

      return null;
    }
  }

  private async askRefill(kiosk: OperatorKioskCard, preferred?: string): Promise<RefillRequest | undefined> {
    const data: RefillDialogData = { code: kiosk.code, paper: kiosk.paper, items: await this.items(), preferred };

    return firstValueFrom(
      this.dialog
        .open<RefillDialogComponent, RefillDialogData, RefillRequest>(RefillDialogComponent, { ...operatorDialog, data })
        .afterClosed(),
    );
  }

  private askReplaced(data: ReplacedDialogData): Promise<ReplacedDialogResult | undefined> {
    return firstValueFrom(
      this.dialog
        .open<ReplacedDialogComponent, ReplacedDialogData, ReplacedDialogResult>(ReplacedDialogComponent, {
          ...operatorDialog,
          data,
        })
        .afterClosed(),
    );
  }

  private text(key: string, params?: Record<string, unknown>): string {
    return this.translate.instant(key, params) as string;
  }
}
