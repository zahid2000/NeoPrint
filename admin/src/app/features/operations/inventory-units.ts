import { inject, Pipe, PipeTransform } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';

import { formatQuantity } from '../../shared/format.pipes';

/**
 * How a warehouse item is counted (T-323, docs/DESIGN_SYSTEM.md "Anbar"): one
 * of these codes, written out in the reader's language, or the warehouse's
 * own word. The server keeps the same list (`InventoryUnits`) and refuses a
 * unit with a digit - the quantity belongs in the stock, not the unit.
 */
export const inventoryUnits = ['ream', 'piece', 'box', 'pack', 'roll'] as const;

export const isKnownUnit = (unit: string): boolean => (inventoryUnits as readonly string[]).includes(unit);

/** A word, not a quantity: a letter and no digit. */
export const isUnitWord = (unit: string): boolean => /\p{L}/u.test(unit) && !/\d/.test(unit) && unit.trim().length <= 30;

/** The unit as the reader says it: "bağlama", "ream", "пачка" - or the warehouse's own word. */
export const unitText = (translate: TranslateService, unit: string | null | undefined): string =>
  !unit || !isUnitWord(unit) ? '' : isKnownUnit(unit) ? (translate.instant('ops.inventory.units.' + unit) as string) : unit;

@Pipe({ name: 'inventoryUnit', pure: false })
export class InventoryUnitPipe implements PipeTransform {
  private readonly translate = inject(TranslateService);

  transform(unit: string | null | undefined): string {
    return unitText(this.translate, unit);
  }
}

/** "20 bağlama": the quantity in the reader's number format, the unit as a word - never a number format. */
@Pipe({ name: 'inventoryAmount', pure: false })
export class InventoryAmountPipe implements PipeTransform {
  private readonly translate = inject(TranslateService);

  transform(value: number | null | undefined, unit: string | null | undefined): string {
    const text = formatQuantity(value, this.translate.getCurrentLang() ?? 'az');
    const word = unitText(this.translate, unit);

    return word ? `${text}\u00a0${word}` : text;
  }
}
