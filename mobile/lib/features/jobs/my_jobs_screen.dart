import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_client.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_design.dart';
import '../../shared/widgets/neo_empty_state.dart';
import 'job_card.dart';
import 'jobs_controller.dart';

enum _JobsTab { active, past }

/// "Sifarişlərim" (T-342): the orders on their way and the ones done, the
/// home screen's "Hamısına bax". The home screen keeps only the active ones.
class MyJobsScreen extends ConsumerStatefulWidget {
  const MyJobsScreen({super.key});

  @override
  ConsumerState<MyJobsScreen> createState() => _MyJobsScreenState();
}

class _MyJobsScreenState extends ConsumerState<MyJobsScreen> {
  _JobsTab _tab = _JobsTab.active;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final jobs = ref.watch(myJobsProvider);

    return Scaffold(
      appBar: neoTabAppBar(context, title: l10n.jobsTitle, showBack: true),
      body: RefreshIndicator(
        onRefresh: () => ref.read(myJobsProvider.notifier).refresh(),
        child: ListView(
          padding: const EdgeInsets.all(Spacing.x4),
          children: [
            NeoPillTabs<_JobsTab>(
              values: _JobsTab.values,
              selected: _tab,
              expand: true,
              label: (tab) => switch (tab) {
                _JobsTab.active => l10n.jobsTabActive,
                _JobsTab.past => l10n.jobsTabPast,
              },
              onSelected: (tab) => setState(() => _tab = tab),
            ),
            const SizedBox(height: Spacing.x4),
            ...switch (jobs) {
              AsyncData(:final value) => () {
                final shown = [
                  for (final job in value)
                    if (job.isActive == (_tab == _JobsTab.active)) job,
                ];
                if (shown.isEmpty) {
                  return [
                    NeoEmptyState(
                      icon: Icons.receipt_long_outlined,
                      message: _tab == _JobsTab.active
                          ? l10n.jobsNoActive
                          : l10n.homeNoJobs,
                      hint: l10n.homeNoJobsHint,
                    ),
                  ];
                }
                return [
                  for (final job in shown) ...[
                    JobCard(job: job),
                    const SizedBox(height: Spacing.x2),
                  ],
                ];
              }(),
              AsyncError(:final error) => [
                NeoBanner.error(
                  message: l10n.homeJobsFailed(
                    error is ApiException
                        ? apiErrorText(l10n, error)
                        : l10n.errorServer,
                  ),
                ),
              ],
              _ => [const Center(child: CircularProgressIndicator())],
            },
          ],
        ),
      ),
    );
  }
}
