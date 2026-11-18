import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideTranslateService } from '@ngx-translate/core';
import { describe, expect, it } from 'vitest';

import { ImageViewerDialog } from './image-viewer.dialog';

/** T-446: a picture opens in the panel, and the arrows go through the others. */
describe('ImageViewerDialog', () => {
  it('starts on the picture tapped and stays within the set', () => {
    let closed = false;
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
        { provide: MAT_DIALOG_DATA, useValue: { urls: ['a', 'b', 'c'], index: 1, altKey: 'x' } },
        { provide: MatDialogRef, useValue: { close: () => (closed = true) } },
      ],
    });
    const fixture = TestBed.createComponent(ImageViewerDialog);
    fixture.detectChanges();
    const viewer = fixture.componentInstance;

    expect(viewer.url()).toBe('b');
    viewer.go(1);
    viewer.go(1);
    expect(viewer.url()).toBe('c');
    viewer.go(-5);
    expect(viewer.url()).toBe('c');
    viewer.go(-1);
    viewer.go(-1);
    expect(viewer.url()).toBe('a');
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('1 / 3');

    viewer.ref.close();
    expect(closed).toBe(true);
  });
});
