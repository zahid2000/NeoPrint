import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, KioskListItem } from '../../core/api/admin-api.service';
import {
  PriceBreakdown,
  PricingKiosk,
  PricingProfile,
  PricingProfileInput,
} from '../../core/api/step2.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { confirmAction } from '../../shared/confirm.dialog';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { DateTimePipe, MoneyPipe } from '../../shared/format.pipes';
import { NotifyService } from '../../shared/notify.service';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { Problem, toProblem } from '../../shared/problem';

/** A volume tier as the form edits it. */
interface TierDraft {
  minSides: number;
  discountPercent: number;
}

/** The profile form: plain values, so empty inputs are allowed while typing. */
interface ProfileDraft {
  name: string;
  bw: number;
  color: number;
  minOrder: number;
  a4: number;
  a3: number;
  tiers: TierDraft[];
}

const emptyDraft = (): ProfileDraft => ({
  name: '',
  bw: 0.1,
  color: 0.3,
  minOrder: 0.2,
  a4: 1,
  a3: 2,
  tiers: [],
});

/**
 * The "Qiymət profilləri" page (plan sections 2.3, 8A.3): profiles, the
 * default, volume tiers, which kiosks use which profile, and a calculator
 * that prices an order against the values on the form before they are saved.
 */
@Component({
  selector: 'app-pricing',
  imports: [
    FormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatSlideToggleModule,
    MatTooltipModule,
    TranslatePipe,
    PageHeaderComponent,
    ErrorBannerComponent,
    EmptyStateComponent,
    MoneyPipe,
    DateTimePipe,
  ],
  templateUrl: './pricing.component.html',
  styleUrl: './pricing.component.scss',
})
export class PricingComponent {
  private readonly api = inject(AdminApiService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly translate = inject(TranslateService);

  readonly profiles = signal<PricingProfile[]>([]);
  readonly loading = signal(true);
  readonly problem = signal<Problem | null>(null);

  /** The profile being edited; null with `creating` set is a new one. */
  readonly selectedId = signal<string | null>(null);
  readonly creating = signal(false);

  readonly draft = signal<ProfileDraft>(emptyDraft());
  private readonly savedDraft = signal<ProfileDraft>(emptyDraft());
  readonly saving = signal(false);

  readonly pricedKiosks = signal<readonly PricingKiosk[]>([]);
  readonly allKiosks = signal<readonly KioskListItem[]>([]);
  readonly kioskToAdd = signal<string[]>([]);

  readonly calculator = signal({
    pageCount: 10,
    copies: 1,
    duplex: false,
    color: false,
    paper: 'A4',
    pagesPerSheet: 1,
  });
  readonly quote = signal<PriceBreakdown | null>(null);
  readonly quoteError = signal<string | null>(null);
  readonly quoting = signal(false);

  readonly canSeeKiosks = computed(() => this.auth.canAny([Permissions.kiosksView]));

  readonly selected = computed(
    () => this.profiles().find((profile) => profile.id === this.selectedId()) ?? null,
  );

  readonly editing = computed(() => this.creating() || this.selected() !== null);

  readonly dirty = computed(
    () => JSON.stringify(this.draft()) !== JSON.stringify(this.savedDraft()),
  );

  /** The rules the backend checks (PricingProfile.Validate), ahead of it. */
  readonly problems = computed<string[]>(() => {
    const draft = this.draft();
    const found: string[] = [];

    if (!draft.name.trim()) {
      found.push('pricing.invalid.name');
    }

    if ([draft.bw, draft.color, draft.minOrder].some((value) => !(value >= 0))) {
      found.push('pricing.invalid.price');
    }

    if (!(draft.a4 > 0) || !(draft.a3 > 0)) {
      found.push('pricing.invalid.multiplier');
    }

    if (draft.tiers.some((tier) => !(tier.minSides >= 1) || !Number.isInteger(tier.minSides))) {
      found.push('pricing.invalid.tierSides');
    }

    if (draft.tiers.some((tier) => !(tier.discountPercent > 0 && tier.discountPercent < 100))) {
      found.push('pricing.invalid.tierPercent');
    }

    if (new Set(draft.tiers.map((tier) => tier.minSides)).size !== draft.tiers.length) {
      found.push('pricing.invalid.tierDuplicate');
    }

    return found;
  });

  readonly canSave = computed(
    () => this.editing() && this.dirty() && this.problems().length === 0 && !this.saving(),
  );

  /** Kiosks that could be moved onto the selected profile. */
  readonly assignable = computed(() => {
    const priced = new Set(this.pricedKiosks().map((kiosk) => kiosk.id));
    const selected = this.selectedId();

    return this.allKiosks().filter(
      (kiosk) => !priced.has(kiosk.id) && (kiosk.pricingProfileId ?? null) !== selected,
    );
  });

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.problem.set(null);

    try {
      const profiles = await firstValueFrom(this.api.listPricingProfiles());
      this.profiles.set(profiles);

      if (this.canSeeKiosks()) {
        this.allKiosks.set(await firstValueFrom(this.api.listKiosks()));
      }

      const keep = this.selectedId();
      const next = profiles.find((profile) => profile.id === keep) ?? profiles.find((p) => p.isDefault) ?? profiles[0];

      if (next && !this.creating()) {
        await this.select(next.id);
      }
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }

  async select(id: string): Promise<void> {
    if (this.dirty() && !(await this.discardChanges())) {
      return;
    }

    const profile = this.profiles().find((candidate) => candidate.id === id);

    if (!profile) {
      return;
    }

    this.creating.set(false);
    this.selectedId.set(id);
    this.setDraft(this.toDraft(profile));
    this.quote.set(null);

    try {
      const detail = await firstValueFrom(this.api.getPricingProfile(id));
      this.pricedKiosks.set(detail.kiosks);
    } catch (error) {
      this.pricedKiosks.set([]);
      this.notify.error(toProblem(error).message);
    }
  }

  async startNew(): Promise<void> {
    if (this.dirty() && !(await this.discardChanges())) {
      return;
    }

    this.creating.set(true);
    this.selectedId.set(null);
    this.pricedKiosks.set([]);
    this.quote.set(null);
    this.setDraft(emptyDraft());
  }

  update<K extends keyof ProfileDraft>(key: K, value: ProfileDraft[K]): void {
    this.draft.update((draft) => ({ ...draft, [key]: value }));
    this.quote.set(null);
  }

  addTier(): void {
    const tiers = this.draft().tiers;
    const next = tiers.length === 0 ? 50 : Math.max(...tiers.map((tier) => tier.minSides)) * 2;

    this.update('tiers', [...tiers, { minSides: next, discountPercent: 5 }]);
  }

  updateTier(index: number, key: keyof TierDraft, value: number): void {
    this.update(
      'tiers',
      this.draft().tiers.map((tier, i) => (i === index ? { ...tier, [key]: value } : tier)),
    );
  }

  removeTier(index: number): void {
    this.update('tiers', this.draft().tiers.filter((_, i) => i !== index));
  }

  revert(): void {
    this.draft.set(structuredClone(this.savedDraft()));
    this.quote.set(null);
  }

  async save(): Promise<void> {
    if (!this.canSave()) {
      return;
    }

    this.saving.set(true);

    try {
      const input = this.toInput(this.draft());
      const saved = this.creating()
        ? await firstValueFrom(this.api.createPricingProfile(input))
        : await firstValueFrom(this.api.updatePricingProfile(this.selectedId()!, input));

      this.creating.set(false);
      this.selectedId.set(saved.id);
      this.setDraft(this.toDraft(saved));
      this.notify.success('pricing.saved');
      await this.load();
    } catch (error) {
      this.notify.error(toProblem(error).message);
    } finally {
      this.saving.set(false);
    }
  }

  async makeDefault(): Promise<void> {
    const profile = this.selected();

    if (!profile) {
      return;
    }

    const confirmed = await confirmAction(this.dialog, {
      title: this.t('pricing.makeDefaultTitle', { name: profile.name }),
      explanation: this.t('pricing.makeDefaultExplanation'),
      confirmLabel: this.t('pricing.makeDefault'),
      cancelLabel: this.t('common.cancel'),
    });

    if (!confirmed) {
      return;
    }

    try {
      await firstValueFrom(this.api.makeDefaultPricingProfile(profile.id));
      this.notify.success('pricing.madeDefault', { name: profile.name });
      await this.load();
    } catch (error) {
      this.notify.error(toProblem(error).message);
    }
  }

  async remove(): Promise<void> {
    const profile = this.selected();

    if (!profile) {
      return;
    }

    const confirmed = await confirmAction(this.dialog, {
      title: this.t('pricing.deleteTitle', { name: profile.name }),
      explanation: this.t('pricing.deleteExplanation'),
      confirmLabel: this.t('common.delete'),
      cancelLabel: this.t('common.cancel'),
      destructive: true,
      reasonLabel: this.t('common.reason'),
    });

    if (!confirmed) {
      return;
    }

    try {
      await firstValueFrom(this.api.deletePricingProfile(profile.id, confirmed.reason));
      this.notify.success('pricing.deleted', { name: profile.name });
      this.selectedId.set(null);
      this.setDraft(emptyDraft());
      await this.load();
    } catch (error) {
      this.notify.error(toProblem(error).message);
    }
  }

  async assignSelected(): Promise<void> {
    const id = this.selectedId();
    const kioskIds = this.kioskToAdd();

    if (!id || kioskIds.length === 0) {
      return;
    }

    try {
      const detail = await firstValueFrom(this.api.assignKiosksToProfile(id, kioskIds));
      this.pricedKiosks.set(detail.kiosks);
      this.kioskToAdd.set([]);
      this.notify.success('pricing.assigned', { count: kioskIds.length });
      await this.refreshKiosksAndProfiles();
    } catch (error) {
      this.notify.error(toProblem(error).message);
    }
  }

  /** Back to the default profile: the kiosk stops having one of its own. */
  async unassign(kiosk: PricingKiosk): Promise<void> {
    try {
      await firstValueFrom(this.api.setKioskPricingProfile(kiosk.id, null));
      this.pricedKiosks.update((kiosks) => kiosks.filter((candidate) => candidate.id !== kiosk.id));
      this.notify.success('pricing.unassigned', { code: kiosk.code });
      await this.refreshKiosksAndProfiles();
    } catch (error) {
      this.notify.error(toProblem(error).message);
    }
  }

  updateCalculator(key: string, value: number | boolean | string): void {
    this.calculator.update((current) => ({ ...current, [key]: value }));
    this.quote.set(null);
  }

  async calculate(): Promise<void> {
    this.quoting.set(true);
    this.quoteError.set(null);

    try {
      this.quote.set(
        await firstValueFrom(
          this.api.calculatePrice({
            profileId: null,
            draft: this.toInput(this.draft()),
            options: this.calculator(),
          }),
        ),
      );
    } catch (error) {
      this.quote.set(null);
      this.quoteError.set(toProblem(error).message);
    } finally {
      this.quoting.set(false);
    }
  }

  private async refreshKiosksAndProfiles(): Promise<void> {
    this.profiles.set(await firstValueFrom(this.api.listPricingProfiles()));

    if (this.canSeeKiosks()) {
      this.allKiosks.set(await firstValueFrom(this.api.listKiosks()));
    }
  }

  private async discardChanges(): Promise<boolean> {
    const confirmed = await confirmAction(this.dialog, {
      title: this.t('common.unsavedChanges'),
      explanation: this.t('pricing.discardExplanation'),
      confirmLabel: this.t('pricing.discard'),
      cancelLabel: this.t('common.cancel'),
    });

    return confirmed !== undefined;
  }

  private setDraft(draft: ProfileDraft): void {
    this.draft.set(structuredClone(draft));
    this.savedDraft.set(structuredClone(draft));
  }

  private toDraft(profile: PricingProfile): ProfileDraft {
    return {
      name: profile.name,
      bw: profile.bwPricePerSide,
      color: profile.colorPricePerSide,
      minOrder: profile.minOrder,
      a4: profile.paperMultipliers['A4'] ?? 1,
      a3: profile.paperMultipliers['A3'] ?? 2,
      tiers: profile.volumeTiers.map((tier) => ({ ...tier })),
    };
  }

  private toInput(draft: ProfileDraft): PricingProfileInput {
    return {
      name: draft.name.trim(),
      bwPricePerSide: draft.bw,
      colorPricePerSide: draft.color,
      minOrder: draft.minOrder,
      paperMultipliers: { A4: draft.a4, A3: draft.a3 },
      volumeTiers: [...draft.tiers].sort((a, b) => a.minSides - b.minSides),
    };
  }

  private t(key: string, params?: Record<string, unknown>): string {
    return this.translate.instant(key, params) as string;
  }
}
