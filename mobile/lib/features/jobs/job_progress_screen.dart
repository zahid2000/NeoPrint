import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_design.dart';
import '../../shared/widgets/neo_primary_button.dart';
import '../files/file_models.dart';
import '../files/files_controller.dart';
import '../prepared/prepare_screen.dart';
import 'job_card.dart';
import 'jobs_controller.dart';
import 'jobs_repository.dart';

/// Where an order is after it was paid (T-378; the poster's "Çap prosesi" and
/// "Çap tamamlandı"): in the queue, printing page by page, printed - kept
/// current by the user hub. "Pəncərəni bağla" leaves it printing; "Sifarişi
/// ləğv et" while it can be; "Yenidən çap et" once it is printed.
class JobProgressScreen extends ConsumerStatefulWidget {
  const JobProgressScreen({super.key, required this.jobCode});

  final String jobCode;

  @override
  ConsumerState<JobProgressScreen> createState() => _JobProgressScreenState();
}

class _JobProgressScreenState extends ConsumerState<JobProgressScreen> {
  bool _askedAgain = false;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final jobs = ref.watch(myJobsProvider);
    final job = jobs.valueOrNull?.items
        .where((candidate) => candidate.jobCode == widget.jobCode)
        .firstOrNull;

    // Just paid: the list may not have it yet - asked once more.
    if (jobs.hasValue && job == null && !_askedAgain) {
      _askedAgain = true;
      WidgetsBinding.instance.addPostFrameCallback(
        (_) => unawaited(ref.read(myJobsProvider.notifier).refresh()),
      );
    }

    return Scaffold(
      appBar: neoTabAppBar(
        context,
        title: l10n.jobProgressTitle(widget.jobCode),
        showBack: true,
      ),
      body: job == null
          ? Center(
              child: jobs.isLoading || !_askedAgain
                  ? const CircularProgressIndicator()
                  : Padding(
                      padding: const EdgeInsets.all(Spacing.x6),
                      child: Text(
                        l10n.jobProgressNotFound,
                        textAlign: TextAlign.center,
                      ),
                    ),
            )
          : _Progress(job: job),
      bottomNavigationBar: job == null ? null : _Actions(job: job),
    );
  }
}

/// The stage: a big sign, a line or two, and the steps under it.
class _Progress extends StatelessWidget {
  const _Progress({required this.job});

  final MyJob job;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final total = job.pages * (job.copies < 1 ? 1 : job.copies);
    final printed = job.printedSides.clamp(0, total);

    final (Widget sign, String title, String body) = switch (job.status) {
      'Completed' => (
        Icon(Icons.check_circle, size: 96, color: tokens.success),
        l10n.jobDoneTitle,
        l10n.jobDoneBody,
      ),
      'Printing' => (
        SizedBox(
          width: 140,
          height: 140,
          child: Stack(
            alignment: Alignment.center,
            children: [
              SizedBox.expand(
                child: CircularProgressIndicator(
                  value: total == 0 ? null : printed / total,
                  strokeWidth: Spacing.x2,
                  backgroundColor: tokens.neutral100,
                ),
              ),
              // "82%" in the ring, as the design draws it (T-377); the
              // printer until the first page is out.
              if (total > 0 && printed > 0)
                Text(
                  '${(100 * printed / total).floor()}%',
                  style: theme.textTheme.headlineMedium?.copyWith(
                    color: tokens.brand500,
                    fontWeight: FontWeights.semibold,
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                )
              else
                Icon(Icons.print_outlined, size: 48, color: tokens.brand500),
            ],
          ),
        ),
        l10n.jobPrintingTitle,
        l10n.jobPrintedOf(printed, total),
      ),
      'Cancelled' || 'Expired' => (
        Icon(Icons.block, size: 96, color: tokens.neutral400),
        l10n.jobCancelledTitle,
        job.reason ?? l10n.jobCancelledBody,
      ),
      'Interrupted' => (
        Icon(Icons.error_outline, size: 96, color: tokens.danger),
        l10n.jobStoppedTitle,
        job.reason ?? l10n.jobStoppedBody,
      ),
      _ => (
        Icon(Icons.schedule, size: 96, color: tokens.brand500),
        l10n.jobQueuedTitle,
        l10n.jobQueuedBody(job.kioskName ?? job.kioskCode ?? ''),
      ),
    };

    return ListView(
      padding: const EdgeInsets.fromLTRB(
        Spacing.x4,
        Spacing.x6,
        Spacing.x4,
        Spacing.x8,
      ),
      children: [
        Center(child: sign),
        const SizedBox(height: Spacing.x4),
        Text(
          title,
          textAlign: TextAlign.center,
          style: theme.textTheme.headlineSmall?.copyWith(
            fontWeight: FontWeights.semibold,
          ),
        ),
        const SizedBox(height: Spacing.x2),
        Text(
          body,
          textAlign: TextAlign.center,
          style: theme.textTheme.bodyLarge?.copyWith(color: tokens.neutral600),
        ),
        if (job.kioskStopped && job.isActive) ...[
          const SizedBox(height: Spacing.x4),
          NeoBanner(tone: StatusTone.warning, message: l10n.jobKioskStopped),
        ],
        if (job.isActive && job.kioskId != null) ...[
          const SizedBox(height: Spacing.x2),
          Center(
            child: TextButton.icon(
              onPressed: () => context.push(
                Routes.kioskQueue(
                  job.kioskId!,
                  job.kioskName ?? job.kioskCode ?? '',
                ),
              ),
              icon: const Icon(Icons.format_list_numbered),
              label: Text(l10n.queueFollow),
            ),
          ),
        ],
        const SizedBox(height: Spacing.x6),
        JobCard(job: job, showActions: false),
        const SizedBox(height: Spacing.x6),
        _Steps(job: job),
      ],
    );
  }
}

/// "Sifariş qəbul edildi · Ödəniş təsdiqləndi · Çap olunur · Tamamlandı":
/// done ones ticked, the current one turning, the rest waiting.
class _Steps extends StatelessWidget {
  const _Steps({required this.job});

  final MyJob job;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;

    // How many steps are behind it; the next is the current one.
    final done = switch (job.status) {
      'PendingPayment' => 1,
      // Printing is the current step while it prints, not one behind it.
      'Paid' || 'Queued' || 'Suspected' || 'Printing' || 'Interrupted' => 2,
      'Completed' => 4,
      _ => 0,
    };
    final steps = [
      l10n.jobStepAccepted,
      l10n.jobStepPaid,
      l10n.jobStepPrinting,
      l10n.jobStepDone,
    ];

    return Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: Spacing.x2),
        child: Column(
          children: [
            for (var i = 0; i < steps.length; i++)
              ListTile(
                leading: i < done
                    ? Icon(Icons.check_circle, color: tokens.success)
                    : i == done && job.isActive
                    ? SizedBox(
                        width: Spacing.x6,
                        height: Spacing.x6,
                        child: CircularProgressIndicator(
                          strokeWidth: 2,
                          color: tokens.brand500,
                        ),
                      )
                    : Icon(
                        Icons.radio_button_unchecked,
                        color: tokens.neutral400,
                      ),
                title: Text(
                  steps[i],
                  style: theme.textTheme.bodyLarge?.copyWith(
                    color: i <= done ? null : tokens.neutral400,
                    fontWeight: i == done && job.isActive
                        ? FontWeights.semibold
                        : null,
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

/// One main button: "Yenidən çap et" once printed, else "Pəncərəni bağla";
/// "Sifarişi ləğv et" while it can be.
class _Actions extends ConsumerWidget {
  const _Actions({required this.job});

  final MyJob job;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;
    final reprint = job.status == 'Completed' && job.fileId != null;

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(
          Spacing.x4,
          Spacing.x2,
          Spacing.x4,
          Spacing.x4,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (reprint) ...[
              NeoPrimaryButton(
                label: l10n.jobReprint,
                icon: Icons.replay,
                onPressed: () => unawaited(printAgain(context, ref, job)),
              ),
              TextButton(
                onPressed: () => context.go(Routes.home),
                child: Text(l10n.jobClose),
              ),
            ] else
              NeoPrimaryButton(
                label: l10n.jobClose,
                onPressed: () => context.go(Routes.home),
              ),
            if (job.canCancel)
              TextButton(
                style: TextButton.styleFrom(foregroundColor: tokens.danger),
                onPressed: () => unawaited(cancelJob(context, ref, job)),
                child: Text(l10n.jobCancelOrder),
              ),
            // Printing is not the owner's to cancel; when no paper goes, an
            // admin looks at the kiosk and may cancel it (T-385).
            if (job.status == 'Printing') ...[
              const SizedBox(height: Spacing.x2),
              Text(
                l10n.jobPrintingStuckHint,
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.bodySmall
                    ?.copyWith(color: tokens.neutral600),
              ),
              TextButton.icon(
                onPressed: () => unawaited(showReportSheet(context, job)),
                icon: const Icon(Icons.support_agent_outlined),
                label: Text(l10n.jobReport),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

/// "Yenidən çap et" (T-408): the same file's print settings, filled in with
/// what the order was printed with and at its kiosk - one tap to pay, and
/// every choice can still be changed. The file is looked for in the list,
/// and asked from the server when the list has not come to it yet (T-413);
/// a file deleted since is said so.
Future<void> printAgain(BuildContext context, WidgetRef ref, MyJob job) async {
  final l10n = AppLocalizations.of(context);
  final messenger = ScaffoldMessenger.of(context);

  final fileId = job.fileId;
  final UserFile? file;
  try {
    file = fileId == null
        ? null
        : await ref.read(filesControllerProvider.notifier).find(fileId);
  } on ApiException catch (error) {
    messenger.showSnackBar(SnackBar(content: Text(apiErrorText(l10n, error))));
    return;
  }
  if (!context.mounted) {
    return;
  }
  if (file == null) {
    messenger.showSnackBar(SnackBar(content: Text(l10n.jobReprintFileGone)));
    return;
  }

  await context.push(
    Routes.prepare,
    extra: (
      file,
      PrintAgain(
        copies: job.copies,
        color: job.color,
        duplex: job.duplex,
        paper: job.paper,
        pagesPerSheet: job.pagesPerSheet,
        pageRange: job.pageRange,
        kioskId: job.kioskId,
      ),
    ),
  );
}
