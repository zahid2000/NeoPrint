import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_status_chip.dart';
import 'jobs_controller.dart';
import 'jobs_repository.dart';

/// The user's "Problem barədə bildir" messages and the answers to them.
class ClaimsScreen extends ConsumerWidget {
  const ClaimsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final claims = ref.watch(myClaimsProvider);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.claimsTitle)),
      body: switch (claims) {
        AsyncData(:final value) when value.isEmpty => NeoEmptyState(
          icon: Icons.support_agent_outlined,
          message: l10n.claimsEmpty,
          hint: l10n.claimsEmptyHint,
        ),
        AsyncData(:final value) => RefreshIndicator(
          onRefresh: () => ref.refresh(myClaimsProvider.future),
          child: ListView.separated(
            padding: const EdgeInsets.all(Spacing.x4),
            itemCount: value.length,
            separatorBuilder: (_, __) => const SizedBox(height: Spacing.x2),
            itemBuilder: (_, index) => _ClaimCard(claim: value[index]),
          ),
        ),
        AsyncError(:final error) => NeoEmptyState(
          icon: Icons.cloud_off_outlined,
          message: l10n.claimsLoadFailed(
            error is ApiException
                ? apiErrorText(l10n, error)
                : l10n.errorServer,
          ),
          action: OutlinedButton(
            onPressed: () => ref.invalidate(myClaimsProvider),
            child: Text(l10n.commonRetry),
          ),
        ),
        _ => const Center(child: CircularProgressIndicator()),
      },
    );
  }
}

class _ClaimCard extends StatelessWidget {
  const _ClaimCard({required this.claim});

  final MyClaim claim;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;

    final (label, tone) = switch (claim.status) {
      'Answered' => (l10n.claimStatusAnswered, StatusTone.success),
      'Closed' => (l10n.claimStatusClosed, StatusTone.neutral),
      _ => (l10n.claimStatusOpen, StatusTone.info),
    };

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    [
                      if (claim.jobCode != null) claim.jobCode!,
                      formatDateTime(
                        claim.createdAt,
                        today: l10n.commonToday,
                        yesterday: l10n.commonYesterday,
                      ),
                    ].join(' · '),
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: tokens.neutral600,
                    ),
                  ),
                ),
                NeoStatusChip(label: label, tone: tone),
              ],
            ),
            const SizedBox(height: Spacing.x2),
            Text(claim.text, style: theme.textTheme.bodyMedium),
            if (claim.reply?.isNotEmpty ?? false) ...[
              const SizedBox(height: Spacing.x3),
              Text(l10n.claimReplyLabel, style: theme.textTheme.titleSmall),
              const SizedBox(height: Spacing.x1),
              Text(claim.reply!, style: theme.textTheme.bodyMedium),
            ],
          ],
        ),
      ),
    );
  }
}
