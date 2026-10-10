import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_client.dart';
import '../../core/platform/system_share.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import 'referral_repository.dart';

/// "Dostunu dəvət et" (docs/DESIGN_SYSTEM.md, "Mobil: dostunu dəvət et"; plan
/// section 2.6): the code, its link and QR, the share sheet, and what the
/// invitations brought.
class ReferralScreen extends ConsumerWidget {
  const ReferralScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final referral = ref.watch(myReferralProvider);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.referralTitle)),
      body: RefreshIndicator(
        onRefresh: () => ref.refresh(myReferralProvider.future),
        child: switch (referral) {
          AsyncData(:final value) => _Content(referral: value),
          AsyncError(:final error) => ListView(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.all(Spacing.x4),
            children: [
              NeoBanner.error(
                message: l10n.referralLoadFailed(
                  error is ApiException
                      ? apiErrorText(l10n, error)
                      : l10n.errorServer,
                ),
              ),
              const SizedBox(height: Spacing.x3),
              OutlinedButton(
                onPressed: () => ref.invalidate(myReferralProvider),
                child: Text(l10n.commonRetry),
              ),
            ],
          ),
          _ => const Center(child: CircularProgressIndicator()),
        },
      ),
    );
  }
}

class _Content extends ConsumerWidget {
  const _Content({required this.referral});

  final MyReferral referral;

  Future<void> _copy(BuildContext context, String text, String said) async {
    await Clipboard.setData(ClipboardData(text: text));

    if (context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(said)));
    }
  }

  Future<void> _share(BuildContext context, WidgetRef ref) async {
    final l10n = AppLocalizations.of(context);
    final shared = await ref
        .read(systemShareProvider)
        .text(
          l10n.referralShareText(referral.code, referral.link),
          subject: l10n.referralTitle,
        );

    // Nothing on the phone took it: the link is at least on the clipboard.
    if (!shared && context.mounted) {
      await _copy(context, referral.link, l10n.referralShareFailed);
    }
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final canShare = ref.watch(canShareProvider);

    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.all(Spacing.x4),
      children: [
        NeoBanner(
          tone: StatusTone.info,
          icon: Icons.card_giftcard_outlined,
          message: [
            l10n.referralHow(referral.bonusPagesPerInvite),
            if (referral.inviteeBonusPages > 0)
              l10n.referralHowFriend(referral.inviteeBonusPages),
          ].join(' '),
        ),
        const SizedBox(height: Spacing.x2),
        Text(l10n.referralBonusUnit, style: theme.textTheme.bodySmall),
        const SizedBox(height: Spacing.x4),
        Card(
          margin: EdgeInsets.zero,
          child: Padding(
            padding: const EdgeInsets.all(Spacing.x4),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(l10n.referralCodeLabel, style: theme.textTheme.bodyMedium),
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        referral.code,
                        style: theme.textTheme.headlineSmall?.copyWith(
                          fontFeatures: const [FontFeature.tabularFigures()],
                        ),
                      ),
                    ),
                    TextButton.icon(
                      onPressed: () =>
                          _copy(context, referral.code, l10n.profileCopied),
                      icon: const Icon(Icons.copy_outlined),
                      label: Text(l10n.sharesCopy),
                    ),
                  ],
                ),
                const SizedBox(height: Spacing.x2),
                Text(
                  referral.link,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: theme.textTheme.bodyMedium,
                ),
                // On the web the copy is the screen's primary button below.
                if (canShare)
                  Align(
                    alignment: AlignmentDirectional.centerStart,
                    child: TextButton.icon(
                      onPressed: () => _copy(
                        context,
                        referral.link,
                        l10n.referralLinkCopied,
                      ),
                      icon: const Icon(Icons.link),
                      label: Text(l10n.referralCopyLink),
                    ),
                  ),
                const SizedBox(height: Spacing.x3),
                const Center(child: _Qr()),
                const SizedBox(height: Spacing.x2),
                Text(
                  l10n.referralQrHint,
                  textAlign: TextAlign.center,
                  style: theme.textTheme.bodySmall,
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: Spacing.x4),
        if (canShare)
          FilledButton.icon(
            onPressed: () => _share(context, ref),
            icon: const Icon(Icons.share_outlined),
            label: Text(l10n.referralShare),
          )
        else
          FilledButton.icon(
            onPressed: () =>
                _copy(context, referral.link, l10n.referralLinkCopied),
            icon: const Icon(Icons.link),
            label: Text(l10n.referralCopyLink),
          ),
        const SizedBox(height: Spacing.x4),
        _Stats(referral: referral),
      ],
    );
  }
}

/// The link as the server's QR image, on white so a camera reads it.
class _Qr extends ConsumerStatefulWidget {
  const _Qr();

  @override
  ConsumerState<_Qr> createState() => _QrState();
}

class _QrState extends ConsumerState<_Qr> {
  late Future<Uint8List> _qr = ref.read(referralRepositoryProvider).qr();

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final side = MediaQuery.sizeOf(context).width * 0.55;

    return FutureBuilder<Uint8List>(
      future: _qr,
      builder: (context, snapshot) {
        if (snapshot.hasError) {
          final error = snapshot.error;

          return Column(
            children: [
              NeoBanner.error(
                message: l10n.sharesQrFailed(
                  error is ApiException
                      ? apiErrorText(l10n, error)
                      : l10n.errorServer,
                ),
              ),
              const SizedBox(height: Spacing.x2),
              OutlinedButton(
                onPressed: () => setState(
                  () => _qr = ref.read(referralRepositoryProvider).qr(),
                ),
                child: Text(l10n.commonRetry),
              ),
            ],
          );
        }

        return Container(
          padding: const EdgeInsets.all(Spacing.x3),
          decoration: BoxDecoration(
            color: Functional.qrBackground,
            borderRadius: BorderRadius.circular(Radii.md),
          ),
          child: SizedBox.square(
            dimension: side,
            child: snapshot.hasData
                ? Image.memory(
                    snapshot.data!,
                    filterQuality: FilterQuality.none,
                    semanticLabel: l10n.referralQrHint,
                  )
                : const Center(child: CircularProgressIndicator()),
          ),
        );
      },
    );
  }
}

class _Stats extends StatelessWidget {
  const _Stats({required this.referral});

  final MyReferral referral;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;

    return Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(l10n.referralStatsTitle, style: theme.textTheme.titleMedium),
            const SizedBox(height: Spacing.x3),
            if (referral.invited == 0)
              Text(l10n.referralNobody, style: theme.textTheme.bodyMedium)
            else ...[
              _StatRow(
                icon: Icons.person_add_alt,
                colour: tokens.brand500,
                label: l10n.referralStatRegistered,
                value: referral.invited,
              ),
              _StatRow(
                icon: Icons.check_circle_outline,
                colour: tokens.success,
                label: l10n.referralStatRewarded,
                value: referral.rewarded,
              ),
              _StatRow(
                icon: Icons.schedule,
                colour: theme.colorScheme.outline,
                label: l10n.referralStatWaiting,
                value: referral.waiting,
              ),
              const Divider(height: Spacing.x6),
              Text(
                l10n.referralEarned(referral.bonusPagesEarned),
                style: theme.textTheme.titleMedium,
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _StatRow extends StatelessWidget {
  const _StatRow({
    required this.icon,
    required this.colour,
    required this.label,
    required this.value,
  });

  final IconData icon;
  final Color colour;
  final String label;
  final int value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: Spacing.x1),
      child: Row(
        children: [
          Icon(icon, color: colour),
          const SizedBox(width: Spacing.x3),
          Expanded(child: Text(label, style: theme.textTheme.bodyMedium)),
          Text(
            '$value',
            style: theme.textTheme.titleMedium?.copyWith(
              fontFeatures: const [FontFeature.tabularFigures()],
            ),
          ),
        ],
      ),
    );
  }
}
