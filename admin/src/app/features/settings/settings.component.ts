import { Component } from '@angular/core';

import { PlaceholderComponent } from '../../core/layout/placeholder.component';

/**
 * Platform settings (plan section 2.4).
 *
 * The 41 seeded settings are already read through ISettingsReader on every
 * request; this screen waits on the settings endpoints.
 */
@Component({
  selector: 'app-settings',
  imports: [PlaceholderComponent],
  template: `
    <app-placeholder titleKey="nav.settings" icon="settings" />
  `,
})
export class SettingsComponent {}
