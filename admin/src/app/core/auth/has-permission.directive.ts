import {
  Directive,
  effect,
  inject,
  input,
  TemplateRef,
  ViewContainerRef,
} from '@angular/core';

import { AuthService } from './auth.service';

/**
 * Shows content only when the administrator holds a permission
 * (plan section 8A.2).
 *
 * ```html
 * <button *appHasPermission="'devices.revoke'">Cihazı ayır</button>
 * <div *appHasPermission="['roles.manage', 'admins.manage']">…</div>
 * ```
 *
 * Presentation only. Every endpoint is guarded on the server, so a control
 * this hides is one the user could not have used anyway - hiding it just
 * avoids offering an action that would be refused.
 */
@Directive({
  selector: '[appHasPermission]',
})
export class HasPermissionDirective {
  private readonly auth = inject(AuthService);
  private readonly template = inject(TemplateRef<unknown>);
  private readonly container = inject(ViewContainerRef);

  /** One permission, or several of which any will do. */
  readonly appHasPermission = input.required<string | readonly string[]>();

  private rendered = false;

  constructor() {
    // An effect rather than a one-off check: a role change reloads the
    // permissions, and the view has to follow (plan section 8A.2).
    effect(() => {
      const required = this.appHasPermission();
      const allowed = this.auth.canAny(
        typeof required === 'string' ? [required] : required,
      );

      if (allowed === this.rendered) {
        return;
      }

      this.container.clear();

      if (allowed) {
        this.container.createEmbeddedView(this.template);
      }

      this.rendered = allowed;
    });
  }
}
