import { ChangeDetectionStrategy, Component } from '@angular/core';

import { TranslatePipe } from '@ngx-translate/core';
import { KioskBrandingComponent } from '../kiosks/kiosk-branding.component';

/** "Kiosk ekranı" (T-387): the welcome screen's words and picture for every kiosk. */
@Component({
  selector: 'app-kiosk-screen',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [KioskBrandingComponent, TranslatePipe],
  template: `
    <div class="kiosk-screen">
      <h1>{{ 'nav.kioskScreen' | translate }}</h1>
      <app-kiosk-branding [canEdit]="true" />
    </div>
  `,
  styles: `
    .kiosk-screen {
      max-width: 1200px;
    }
  `,
})
export class KioskScreenComponent {}
