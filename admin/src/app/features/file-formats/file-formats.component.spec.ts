import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { FileFormats } from '../../core/api/step2.models';
import { FileFormatsComponent } from './file-formats.component';

const formats: FileFormats = {
  formats: [
    {
      extension: 'pdf',
      mediaType: 'application/pdf',
      group: 'Pdf',
      route: 'None',
      printerNative: true,
      enabled: true,
    },
    {
      extension: 'docx',
      mediaType: 'application/x',
      group: 'TextDocument',
      route: 'LibreOffice',
      printerNative: false,
      enabled: true,
    },
    {
      extension: 'txt',
      mediaType: 'text/plain',
      group: 'TextDocument',
      route: 'LibreOffice',
      printerNative: false,
      enabled: false,
    },
  ],
  defaultMaxSizeMb: 50,
  defaultMaxPages: 200,
  userQuotaGb: 1,
};

describe('FileFormatsComponent', () => {
  let fixture: ComponentFixture<FileFormatsComponent>;
  let component: FileFormatsComponent;
  let backend: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(FileFormatsComponent);
    component = fixture.componentInstance;

    backend.expectOne('/api/admin/files/formats').flush(formats);
    await fixture.whenStable();
  });

  afterEach(() => backend.verify());

  it('groups the formats as the plan table does', () => {
    expect(component.sections().map((section) => section.group)).toEqual(['Pdf', 'TextDocument']);
    expect(component.sectionState(component.sections()[1])).toBe('some');
  });

  it('has nothing to save until something changes', () => {
    expect(component.dirty()).toBe(false);

    component.toggle('txt', true);

    expect(component.dirty()).toBe(true);
    expect(component.canSave()).toBe(true);
  });

  it('refuses to save with every format switched off', () => {
    component.toggle('pdf', false);
    component.toggle('docx', false);

    expect(component.enabledCount()).toBe(0);
    expect(component.canSave()).toBe(false);
  });

  it('refuses a limit outside the allowed range', () => {
    component.maxSizeMb.set(5000);

    expect(component.limitsValid()).toBe(false);
    expect(component.canSave()).toBe(false);
  });

  it('saves the switched-on formats and the limits', async () => {
    component.toggle('docx', false);
    component.maxPages.set(300);

    const saving = component.save();
    const request = backend.expectOne('/api/admin/files/formats');

    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      enabled: ['pdf'],
      defaultMaxSizeMb: 50,
      defaultMaxPages: 300,
      userQuotaGb: 1,
    });

    request.flush({ ...formats, defaultMaxPages: 300 });
    await saving;

    expect(component.dirty()).toBe(false);
  });
});
