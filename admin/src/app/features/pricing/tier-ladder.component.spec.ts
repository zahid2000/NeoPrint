import { TestBed } from '@angular/core/testing';
import { provideTranslateService } from '@ngx-translate/core';
import { describe, expect, it } from 'vitest';

import {
  baseRowEnd,
  convertLadder,
  ladderExample,
  ladderKind,
  ladderRows,
  TierDraft,
  TierLadderComponent,
} from './tier-ladder.component';

const price = (minSides: number, bw: number, color = 0.3): TierDraft => ({
  minSides,
  kind: 'Price',
  discountPercent: 0,
  bw,
  color,
});

const percent = (minSides: number, discountPercent: number): TierDraft => ({
  minSides,
  kind: 'Percent',
  discountPercent,
  bw: 0,
  color: 0,
});

/** T-442: the owner's ladder - up to 50 sides 0.15, from 51 0.13, from 101 0.11. */
const owners = [price(51, 0.13), price(101, 0.11)];

describe('tier ladder (T-442)', () => {
  it('prices the whole order at the step it reaches, or step by step', () => {
    expect(ladderExample(owners, 'WholeOrder', 0.15, 120)).toBe(13.2);
    expect(ladderExample(owners, 'Graduated', 0.15, 120)).toBe(16.2);
    expect(ladderExample(owners, 'Graduated', 0.15, 40)).toBe(6);
    // A percentage step is worked unrounded: 0.15 less 20% is 0.12 a side.
    expect(ladderExample([percent(51, 20)], 'WholeOrder', 0.15, 120)).toBe(14.4);
  });

  it('spells out each range, the last one open', () => {
    expect(baseRowEnd(owners)).toBe(50);
    expect(ladderRows(owners).map((row) => [row.from, row.to])).toEqual([
      [51, 100],
      [101, null],
    ]);
  });

  it('is a price ladder unless every step is a percentage', () => {
    expect(ladderKind([])).toBe('Price');
    expect(ladderKind([percent(51, 10)])).toBe('Percent');
    expect(ladderKind([percent(51, 10), price(101, 0.1)])).toBe('Price');
  });

  it('keeps what a step charges when its kind changes', () => {
    const asPrices = convertLadder([percent(51, 20)], 'Price', 0.15, 0.4);
    expect(asPrices).toEqual([
      { minSides: 51, kind: 'Price', discountPercent: 0, bw: 0.12, color: 0.32 },
    ]);

    const back = convertLadder(asPrices, 'Percent', 0.15, 0.4);
    expect(back[0]).toMatchObject({ kind: 'Percent', discountPercent: 20 });
  });

  it('adds a step 50 sides on at the prices of the one before it', () => {
    TestBed.configureTestingModule({
      providers: [provideTranslateService({ fallbackLang: 'az', lang: 'az' })],
    });
    const fixture = TestBed.createComponent(TierLadderComponent);
    fixture.componentRef.setInput('tiers', owners);
    fixture.componentRef.setInput('mode', 'WholeOrder');
    fixture.componentRef.setInput('baseBw', 0.15);
    fixture.componentRef.setInput('baseColor', 0.4);
    let emitted: TierDraft[] = [];
    fixture.componentInstance.tiersChange.subscribe((tiers) => (emitted = tiers));
    fixture.detectChanges();

    fixture.componentInstance.add();

    expect(emitted.at(-1)).toEqual(price(151, 0.11));
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelectorAll('[data-tier]').length).toBe(2);
    expect(host.querySelector('[data-base-row]')).not.toBeNull();
    expect(host.querySelector('[data-example]')).not.toBeNull();
  });
});
