import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { describe, beforeEach, afterEach, expect, it } from 'vitest';

import { PlatformSetting } from '../../core/api/admin-api.service';
import { SettingsComponent } from './settings.component';

const settings: PlatformSetting[] = [
  {
    key: 'Queue.DisplayLimit',
    value: '5',
    defaultValue: '5',
    valueType: 'Integer',
    description: 'Kioskda göstərilən queue sayı',
    isDefault: true,
    updatedAt: null,
    updatedBy: null,
  },
  {
    key: 'Queue.MaxLength',
    value: '40',
    defaultValue: '50',
    valueType: 'Integer',
    description: 'Queue uzunluğu',
    isDefault: false,
    updatedAt: new Date().toISOString(),
    updatedBy: '11111111-1111-1111-1111-111111111111',
  },
  {
    key: 'Paper.BlockWhenInsufficient',
    value: 'true',
    defaultValue: 'true',
    valueType: 'Boolean',
    description: 'Kağız çatmayanda sifarişi blokla',
    isDefault: true,
    updatedAt: null,
    updatedBy: null,
  },
];

async function flushMicrotasks(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe('SettingsComponent', () => {
  let fixture: ComponentFixture<SettingsComponent>;
  let component: SettingsComponent;
  let backend: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'login', children: [] }]),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(SettingsComponent);
    component = fixture.componentInstance;

    backend.expectOne('/api/admin/settings').flush(settings);

    await fixture.whenStable();
  });

  afterEach(() => {
    backend.verify();
  });

  it('groups settings by the part of the key before the dot', () => {
    // Forty-odd settings in one list is unusable, and the prefix is already
    // how the plan organises them.
    const groups = component.groups();

    expect(groups.map((group) => group.name)).toEqual(['Paper', 'Queue']);
    expect(groups.find((group) => group.name === 'Queue')!.settings).toHaveLength(2);
  });

  it('starts with nothing to save', () => {
    expect(component.hasChanges()).toBe(false);
    expect(component.changed()).toEqual([]);
  });

  it('notices a changed value', () => {
    component.set(settings[0], '3');

    expect(component.isChanged(settings[0])).toBe(true);
    expect(component.hasChanges()).toBe(true);
    expect(component.changed().map((setting) => setting.key)).toEqual(['Queue.DisplayLimit']);
  });

  it('reads a boolean as a boolean', () => {
    expect(component.boolValue(settings[2])).toBe(true);

    component.setBool(settings[2], false);

    expect(component.value(settings[2])).toBe('false');
    expect(component.boolValue(settings[2])).toBe(false);
  });

  it('puts a setting back to its documented default', () => {
    // Queue.MaxLength is stored as 40 against a default of 50.
    component.resetToDefault(settings[1]);

    expect(component.value(settings[1])).toBe('50');
    expect(component.isChanged(settings[1])).toBe(true);
  });

  it('reverts every edit at once', () => {
    component.set(settings[0], '3');
    component.setBool(settings[2], false);

    component.revertAll();

    expect(component.hasChanges()).toBe(false);
    expect(component.value(settings[0])).toBe('5');
    expect(component.boolValue(settings[2])).toBe(true);
  });

  it('marks the group a changed setting is in', () => {
    const queue = component.groups().find((group) => group.name === 'Queue')!;
    const paper = component.groups().find((group) => group.name === 'Paper')!;

    expect(component.groupHasChanges(queue)).toBe(false);

    component.set(settings[0], '3');

    expect(component.groupHasChanges(queue)).toBe(true);
    expect(component.groupHasChanges(paper)).toBe(false);
  });

  it('sends only what changed', async () => {
    component.set(settings[0], '3');

    const saved = component.save();

    const request = backend.expectOne('/api/admin/settings');

    expect(request.request.method).toBe('PUT');

    // Sending the whole set would write an audit entry for every setting on
    // the page.
    expect(request.request.body).toEqual({ values: { 'Queue.DisplayLimit': '3' } });

    request.flush([{ ...settings[0], value: '3', isDefault: false }, settings[1], settings[2]]);

    await saved;

    expect(component.hasChanges()).toBe(false);
    expect(component.value(component.settings()[0])).toBe('3');
  });

  it('sends nothing when nothing changed', async () => {
    await component.save();

    backend.expectNone('/api/admin/settings');
  });

  it('shows the message the server sent when a value is refused', async () => {
    component.set(settings[0], 'five');

    const saved = component.save();

    backend
      .expectOne('/api/admin/settings')
      .flush(
        { errors: { 'Queue.DisplayLimit': ['Dəyər tam ədəd olmalıdır.'] } },
        { status: 400, statusText: 'Bad Request' },
      );

    await saved;

    expect(component.errorMessage()).toBe('Dəyər tam ədəd olmalıdır.');

    // And the edit is still there, so it can be corrected rather than retyped.
    expect(component.value(settings[0])).toBe('five');
  });

  it('keeps the page usable when the list cannot be loaded', async () => {
    // A second component, this time with a failing load.
    TestBed.resetTestingModule();

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'login', children: [] }]),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      ],
    });

    const failing = TestBed.inject(HttpTestingController);
    const second = TestBed.createComponent(SettingsComponent);

    failing
      .expectOne('/api/admin/settings')
      .flush({ detail: 'Serverdə xəta baş verdi.' }, { status: 500, statusText: 'Server Error' });

    await flushMicrotasks();

    expect(second.componentInstance.errorMessage()).toBe('Serverdə xəta baş verdi.');
    expect(second.componentInstance.loading()).toBe(false);
    expect(second.componentInstance.settings()).toEqual([]);

    failing.verify();
  });
});
