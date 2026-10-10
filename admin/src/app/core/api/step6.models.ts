/**
 * Shapes of the step 6 admin API (docs/API.md, "Referral"; plan section 8A.3):
 * the referral programme and a customer's files as metadata.
 */

/** A user as the referral pages name them: the public id, never the email. */
export interface ReferralParty {
  readonly id: string;
  readonly publicId: string;
}

/** `Registered` until the invitee's first paid print, then `Rewarded` - or still `Registered`, with a `hold`. */
export type ReferralStatus = 'Registered' | 'Rewarded';

/** Why a decided invitation brought no bonus. */
export type ReferralHold = 'None' | 'SameDevice' | 'MonthlyLimit' | 'InviterInactive';

/** The history's tabs. */
export type ReferralFilter = 'Waiting' | 'Rewarded' | 'Held';

export interface ReferralRow {
  readonly id: string;
  readonly inviter: ReferralParty;
  readonly invitee: ReferralParty;
  readonly status: ReferralStatus;
  readonly hold: ReferralHold;
  readonly createdAt: string;
  /** When the invitee's first paid print decided it; absent while it waits. */
  readonly qualifiedAt?: string;
  readonly inviterBonusPages: number;
  readonly inviteeBonusPages: number;
}

export interface TopInviter {
  readonly inviter: ReferralParty;
  readonly invited: number;
  readonly rewarded: number;
  readonly bonusPages: number;
}

export interface ReferralProgrammeTotals {
  readonly registered: number;
  readonly rewarded: number;
  readonly waiting: number;
  /** Decided without a bonus. */
  readonly held: number;
  /** To inviters and invitees together. */
  readonly bonusPagesGiven: number;
}

export interface ReferralPage {
  readonly totals: ReferralProgrammeTotals;
  readonly top: readonly TopInviter[];
  readonly items: readonly ReferralRow[];
  readonly total: number;
  readonly page: number;
  readonly pageSize: number;
}

/** What a referral row is to the reader: one of three things, whatever the two fields say. */
export type ReferralOutcome = 'Rewarded' | 'Held' | 'Waiting';

export const referralOutcome = (row: Pick<ReferralRow, 'status' | 'hold'>): ReferralOutcome =>
  row.status === 'Rewarded' ? 'Rewarded' : row.hold !== 'None' ? 'Held' : 'Waiting';

/** A customer's file as the panel may see it: metadata, the name masked by the server. */
export interface CustomerFile {
  readonly id: string;
  /** `He•••.pdf`; a personal document's name is hidden whole. */
  readonly maskedName: string;
  readonly extension: string;
  readonly sizeBytes: number;
  readonly source: 'Uploaded' | 'Scan' | 'Layout' | 'Usb' | 'Shared';
  readonly isSensitive: boolean;
  readonly status: string;
  readonly pageCount: number;
  readonly createdAt: string;
}

export interface CustomerReferrals {
  /** Whose code the customer registered with; absent when none. */
  readonly invitedBy?: ReferralParty;
  readonly totals: {
    readonly registered: number;
    readonly rewarded: number;
    readonly waiting: number;
    readonly bonusPagesEarned: number;
  };
  readonly items: readonly ReferralRow[];
}
