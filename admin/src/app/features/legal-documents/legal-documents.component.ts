import { Component } from '@angular/core';

import { PlaceholderComponent } from '../../core/layout/placeholder.component';

/**
 * Legal documents (plan section 2.9).
 *
 * The published documents are already served to the apps and their acceptances
 * recorded; publishing a new version from the panel waits on the
 * legal-document endpoints.
 */
@Component({
  selector: 'app-legal-documents',
  imports: [PlaceholderComponent],
  template: `
    <app-placeholder titleKey="nav.legal" icon="gavel" />
  `,
})
export class LegalDocumentsComponent {}
