import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, RoleResponse } from '../../core/api/admin-api.service';
import { PermissionGroup } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { confirmAction } from '../../shared/confirm.dialog';
import { describeError } from '../login/login.component';

/**
 * Roles and their permissions (plan section 8A.2).
 *
 * The permission catalogue is fixed in the backend code; the roles built out of
 * it are edited here at runtime, and a change takes effect on the next request
 * without anyone signing in again.
 */
@Component({
  selector: 'app-roles',
  imports: [
    ReactiveFormsModule,
    MatCardModule,
    MatListModule,
    MatExpansionModule,
    MatCheckboxModule,
    MatButtonModule,
    MatIconModule,
    MatChipsModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatTooltipModule,
    TranslatePipe,
  ],
  templateUrl: './roles.component.html',
  styleUrl: './roles.component.scss',
})
export class RolesComponent {
  private readonly api = inject(AdminApiService);
  private readonly auth = inject(AuthService);
  private readonly formBuilder = inject(FormBuilder);
  private readonly snackBar = inject(MatSnackBar);
  private readonly translate = inject(TranslateService);
  private readonly dialog = inject(MatDialog);

  readonly roles = signal<RoleResponse[]>([]);
  readonly groups = signal<PermissionGroup[]>([]);
  readonly selectedId = signal<string | null>(null);

  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  /** The permissions ticked in the editor, kept apart from the saved role. */
  readonly draft = signal<ReadonlySet<string>>(new Set<string>());

  readonly form = this.formBuilder.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(80)]],
    description: [''],
  });

  readonly selected = computed(
    () => this.roles().find((role) => role.id === this.selectedId()) ?? null,
  );

  /**
   * A system role is read-only: SuperAdmin must keep every permission, or an
   * installation could lock itself out of its own panel (plan section 8A.2).
   */
  readonly isReadOnly = computed(() => this.selected()?.isSystem ?? false);

  readonly draftCount = computed(() => this.draft().size);

  constructor() {
    void this.reload();
  }

  async reload(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set(null);

    try {
      const [roles, groups] = await Promise.all([
        firstValueFrom(this.api.listRoles()),
        firstValueFrom(this.api.permissionCatalogue()),
      ]);

      this.roles.set(roles);
      this.groups.set(groups);

      const current = this.selectedId();
      const stillThere = roles.some((role) => role.id === current);

      this.select(stillThere ? current! : (roles[0]?.id ?? null));
    } catch (error) {
      this.errorMessage.set(describeError(error));
    } finally {
      this.loading.set(false);
    }
  }

  select(id: string | null): void {
    this.selectedId.set(id);

    const role = this.roles().find((candidate) => candidate.id === id);

    this.form.reset({
      name: role?.name ?? '',
      description: role?.description ?? '',
    });

    this.draft.set(new Set(role?.permissions ?? []));
  }

  has(permission: string): boolean {
    return this.draft().has(permission);
  }

  toggle(permission: string, checked: boolean): void {
    this.draft.update((current) => {
      const next = new Set(current);

      if (checked) {
        next.add(permission);
      } else {
        next.delete(permission);
      }

      return next;
    });
  }

  /** Whether every permission in a group is ticked, for the group checkbox. */
  groupState(group: PermissionGroup): 'all' | 'some' | 'none' {
    const ticked = group.permissions.filter((permission) => this.draft().has(permission)).length;

    if (ticked === 0) {
      return 'none';
    }

    return ticked === group.permissions.length ? 'all' : 'some';
  }

  toggleGroup(group: PermissionGroup, checked: boolean): void {
    this.draft.update((current) => {
      const next = new Set(current);

      for (const permission of group.permissions) {
        if (checked) {
          next.add(permission);
        } else {
          next.delete(permission);
        }
      }

      return next;
    });
  }

  startNew(): void {
    this.selectedId.set(null);
    this.form.reset({ name: '', description: '' });
    this.draft.set(new Set<string>());
  }

  async save(): Promise<void> {
    if (this.form.invalid || this.saving() || this.isReadOnly()) {
      return;
    }

    this.saving.set(true);
    this.errorMessage.set(null);

    const { name, description } = this.form.getRawValue();

    const body = {
      name: name.trim(),
      description: description.trim() || null,
      permissions: [...this.draft()],
    };

    try {
      const id = this.selectedId();

      const saved = id
        ? await firstValueFrom(this.api.updateRole(id, body))
        : await firstValueFrom(this.api.createRole(body));

      this.selectedId.set(saved.id);
      await this.reload();

      // The signed-in account may hold this role, in which case what the menu
      // shows has just changed (plan section 8A.2).
      await this.auth.reloadPermissions();

      this.notify('roles.saved');
    } catch (error) {
      this.errorMessage.set(describeError(error));
    } finally {
      this.saving.set(false);
    }
  }

  async clone(role: RoleResponse): Promise<void> {
    const name = window.prompt(
      this.translate.instant('roles.cloneName') as string,
      `${role.name} (copy)`,
    );

    if (!name) {
      return;
    }

    try {
      const created = await firstValueFrom(this.api.cloneRole(role.id, name));
      this.selectedId.set(created.id);
      await this.reload();
    } catch (error) {
      this.errorMessage.set(describeError(error));
    }
  }

  async remove(role: RoleResponse): Promise<void> {
    // Deleting a role is destructive, so it goes through the panel's own
    // dialog: window.confirm cannot be translated and collects no reason
    // (docs/DESIGN_SYSTEM.md section 4).
    const confirmed = await confirmAction(this.dialog, {
      title: this.translate.instant('roles.deleteConfirm', { name: role.name }) as string,
      explanation: this.translate.instant('roles.deleteExplanation') as string,
      confirmLabel: this.translate.instant('common.delete') as string,
      cancelLabel: this.translate.instant('common.cancel') as string,
      destructive: true,
      reasonLabel: this.translate.instant('common.reason') as string,
    });

    if (!confirmed) {
      return;
    }

    try {
      await firstValueFrom(this.api.deleteRole(role.id));
      this.selectedId.set(null);
      await this.reload();
    } catch (error) {
      // The server refuses to delete a role that accounts still hold, and says
      // how many; that message is worth showing as it is.
      this.errorMessage.set(describeError(error));
    }
  }

  private notify(messageKey: string): void {
    this.snackBar.open(this.translate.instant(messageKey) as string, undefined, {
      duration: 4000,
    });
  }
}
