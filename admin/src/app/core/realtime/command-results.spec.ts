import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideTranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { NotifyService } from '../../shared/notify.service';
import { KioskCommand } from '../api/step3.models';
import { AdminHubService } from './admin-hub.service';
import { commandResults } from './command-results';
import { FakeHub } from './fake-hub.testing';

@Component({ template: '' })
class PageComponent {
  readonly results = commandResults();
}

function command(overrides: Partial<KioskCommand> = {}): KioskCommand {
  return {
    id: 'c-1',
    kioskId: 'k-1',
    type: 'TestPrint',
    status: 'Sent',
    result: null,
    createdAt: '2026-10-14T08:00:00Z',
    completedAt: null,
    ...overrides,
  };
}

/**
 * T-271 (the owner, 2026-10-14): after "Test çap" the kiosk's answer came over
 * the hub and nothing told the person who had pressed the button.
 */
describe('commandResults', () => {
  let hub: FakeHub;
  let notify: { success: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> };
  let page: PageComponent;
  let destroy: () => void;

  beforeEach(() => {
    vi.useFakeTimers();
    hub = new FakeHub();
    notify = { success: vi.fn(), error: vi.fn() };

    TestBed.configureTestingModule({
      providers: [
        provideTranslateService(),
        { provide: AdminHubService, useValue: hub },
        { provide: NotifyService, useValue: notify },
      ],
    });

    const fixture = TestBed.createComponent(PageComponent);

    page = fixture.componentInstance;
    destroy = () => fixture.destroy();
  });

  afterEach(() => vi.useRealTimers());

  it('says that the test page printed when the kiosk answers', () => {
    page.results.sent(command());
    expect(notify.success, 'nothing yet: the kiosk has not answered').not.toHaveBeenCalled();

    hub.emit('CommandResult', command({ status: 'Acknowledged' }));
    expect(notify.success, 'taken, not done').not.toHaveBeenCalled();

    hub.emit('CommandResult', command({ status: 'Succeeded', result: 'Test səhifəsi çap olundu.' }));

    expect(notify.success).toHaveBeenCalledTimes(1);
    expect(notify.success).toHaveBeenCalledWith('printers.resultSucceeded', {
      command: 'printers.command.TestPrint',
      result: 'Test səhifəsi çap olundu.',
    });

    // The same answer again - a reconnect replays nothing, but a second listener might - is not said twice.
    hub.emit('CommandResult', command({ status: 'Succeeded', result: 'Test səhifəsi çap olundu.' }));
    expect(notify.success).toHaveBeenCalledTimes(1);
  });

  it('says why it failed, in a notice that stays', () => {
    page.results.sent(command());
    hub.emit('CommandResult', command({ status: 'Failed', result: 'Printer oflayndır.' }));

    expect(notify.error).toHaveBeenCalledWith('printers.resultFailed', {
      command: 'printers.command.TestPrint',
      result: 'Printer oflayndır.',
    });
    expect(notify.success).not.toHaveBeenCalled();
  });

  it('says what to do when the kiosk never answered, or failed without a word', () => {
    page.results.sent(command());
    hub.emit('CommandResult', command({ status: 'Expired' }));
    expect(notify.error).toHaveBeenLastCalledWith('printers.resultExpired', { command: 'printers.command.TestPrint' });

    page.results.sent(command({ id: 'c-2' }));
    hub.emit('CommandResult', command({ id: 'c-2', status: 'Failed' }));
    expect(notify.error).toHaveBeenLastCalledWith('printers.resultFailedPlain', {
      command: 'printers.command.TestPrint',
      result: undefined,
    });
  });

  it("says nothing about a command somebody else sent to the same kiosk", () => {
    hub.emit('CommandResult', command({ id: 'theirs', status: 'Succeeded', result: 'Test səhifəsi çap olundu.' }));

    expect(notify.success).not.toHaveBeenCalled();
    expect(notify.error).not.toHaveBeenCalled();
  });

  it('is not caught out by a kiosk that answers before the request returns', () => {
    // The hub is quicker than the HTTP response.
    hub.emit('CommandResult', command({ status: 'Succeeded', result: 'Test səhifəsi çap olundu.' }));
    expect(notify.success).not.toHaveBeenCalled();

    page.results.sent(command());
    expect(notify.success).toHaveBeenCalledTimes(1);

    // An answer nobody claims is forgotten.
    hub.emit('CommandResult', command({ id: 'c-9', status: 'Succeeded' }));
    vi.advanceTimersByTime(31_000);
    page.results.sent(command({ id: 'c-9' }));
    expect(notify.success, 'by now it is waited for again, not said').toHaveBeenCalledTimes(1);
  });

  it('says it at once when the request itself comes back finished', () => {
    page.results.sent(command({ status: 'Succeeded' }));

    expect(notify.success).toHaveBeenCalledWith('printers.resultSucceededPlain', {
      command: 'printers.command.TestPrint',
      result: undefined,
    });
  });

  it('stops listening when the page goes', () => {
    page.results.sent(command());
    destroy();

    hub.emit('CommandResult', command({ status: 'Succeeded' }));
    expect(notify.success).not.toHaveBeenCalled();
  });
});
