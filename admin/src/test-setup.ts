import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach } from 'vitest';

/**
 * Shared setup for the panel's unit tests.
 *
 * Every spec file runs in one worker (see vitest.config.ts), and Angular's
 * TestBed is global, so a module configured by one file would still be
 * instantiated when the next file tried to configure its own. Resetting around
 * each test also destroys the fixtures, which is what releases the component
 * effects between tests.
 */
beforeEach(() => {
  TestBed.resetTestingModule();
});

afterEach(() => {
  TestBed.resetTestingModule();
});
