import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideTranslateService } from '@ngx-translate/core';
import { describe, expect, it, vi } from 'vitest';

import { InventoryItem } from './operator.models';
import { RefillDialogComponent, RefillDialogData } from './refill.dialog';

function item(overrides: Partial<InventoryItem> = {}): InventoryItem {
  return {
    id: 'i-a4',
    sku: 'PAPER-A4',
    name: 'A4, 500',
    type: 'PaperA4Ream',
    compatiblePrinterModels: [],
    unit: 'ream',
    sheetsPerUnit: 500,
    minStock: 1,
    stock: 4,
    location: 'Shelf 1',
    isActive: true,
    isLow: false,
    ...overrides,
  };
}

function open(data: RefillDialogData): { dialog: RefillDialogComponent; close: ReturnType<typeof vi.fn> } {
  const close = vi.fn();

  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService(),
      { provide: MAT_DIALOG_DATA, useValue: data },
      { provide: MatDialogRef, useValue: { close } },
    ],
  });

  return { dialog: TestBed.createComponent(RefillDialogComponent).componentInstance, close };
}

const trays = {
  A4: { capacity: 500, current: 120, counted: true },
  A3: { capacity: 250, current: 0, counted: false },
  Letter: { capacity: 0, current: 0 },
};

/** "Kağız doldur" (plan section 8B): what the dialog offers and what it hands back. */
describe('RefillDialogComponent', () => {
  it('offers only trays that have a capacity, and a ream of the size by default', () => {
    const { dialog } = open({ code: 'KSK-0001', paper: trays, items: [item(), item({ id: 'i-a3', type: 'PaperA3Ream' })] });

    expect(dialog.sizes, 'A4 first').toEqual(['A4', 'A3']);
    expect(dialog.paper(), 'the tray nobody has counted needs it most').toBe('A3');
    expect(dialog.itemId(), 'the ream of that size').toBe('i-a3');
  });

  // Found on the development stack: the dialog opened on A3 because "A3" sorts
  // before "A4", and "Tam doldur" filled a tray that was already full.
  it('opens on the tray that needs paper most, and on A4 when both are the same', () => {
    const fuller = open({
      code: 'KSK-0001',
      paper: { A4: { capacity: 500, current: 499, counted: true }, A3: { capacity: 250, current: 250, counted: true } },
      items: [item()],
    });

    expect(fuller.dialog.paper()).toBe('A4');
    expect(fuller.dialog.itemId(), 'and takes an A4 ream for it').toBe('i-a4');

    const emptier = open({
      code: 'KSK-0001',
      paper: { A4: { capacity: 500, current: 400, counted: true }, A3: { capacity: 250, current: 10, counted: true } },
      items: [],
    });

    expect(emptier.dialog.paper()).toBe('A3');

    const same = open({
      code: 'KSK-0001',
      paper: { A4: { capacity: 500, current: 500, counted: true }, A3: { capacity: 250, current: 250, counted: true } },
      items: [],
    });

    expect(same.dialog.paper()).toBe('A4');
  });

  it('opens on the tray the task or the alert is about', () => {
    const { dialog } = open({ code: 'KSK-0001', paper: trays, items: [item()], preferred: 'A4' });

    expect(dialog.paper()).toBe('A4');
    expect(dialog.mode(), 'a counted tray is filled').toBe('Full');
    expect(dialog.itemId()).toBe('i-a4');
  });

  it('counts a tray nobody has counted before anything is added to it (T-258)', () => {
    const { dialog } = open({ code: 'KSK-0001', paper: trays, items: [], preferred: 'A3' });

    expect(dialog.mode()).toBe('Exact');
    expect(dialog.valid(), 'a count has to be given').toBe(false);

    dialog.sheets.set(250);
    expect(dialog.valid()).toBe(true);
  });

  it('checks the count the way the server does', () => {
    const { dialog } = open({ code: 'KSK-0001', paper: trays, items: [], preferred: 'A4' });

    dialog.mode.set('Add');
    dialog.sheets.set(380);
    expect(dialog.sheetsValid(), 'fills the tray exactly').toBe(true);
    dialog.sheets.set(381);
    expect(dialog.sheetsValid(), 'one more than fits').toBe(false);
    dialog.sheets.set(0);
    expect(dialog.sheetsValid(), 'adding nothing is not a refill').toBe(false);

    dialog.mode.set('Exact');
    expect(dialog.sheetsValid(), 'the tray was counted and found empty').toBe(true);
  });

  it('does not take more from the warehouse than it holds', () => {
    const { dialog } = open({ code: 'KSK-0001', paper: trays, items: [item({ stock: 2 })], preferred: 'A4' });

    dialog.quantity.set(3);
    expect(dialog.valid()).toBe(false);

    dialog.quantity.set(2);
    expect(dialog.valid()).toBe(true);
  });

  it('hands back the refill and what was taken for it', () => {
    const { dialog, close } = open({ code: 'KSK-0001', paper: trays, items: [item()], preferred: 'A4' });

    dialog.quantity.set(2);
    dialog.save();

    expect(close).toHaveBeenCalledWith({ paper: 'A4', mode: 'Full', sheets: 0, inventoryItemId: 'i-a4', quantity: 2 });
  });

  it('hands back a refill alone when nothing came from the warehouse', () => {
    const { dialog, close } = open({ code: 'KSK-0001', paper: trays, items: [], preferred: 'A4' });

    dialog.mode.set('Add');
    dialog.sheets.set(100);
    dialog.save();

    expect(close).toHaveBeenCalledWith({ paper: 'A4', mode: 'Add', sheets: 100 });
  });

  it('offers no ream that is switched off or of another size', () => {
    const { dialog } = open({
      code: 'KSK-0001',
      paper: trays,
      items: [item({ isActive: false }), item({ id: 'i-toner', type: 'Toner' })],
      preferred: 'A4',
    });

    expect(dialog.choices()).toEqual([]);
    expect(dialog.itemId()).toBeNull();
  });
});
