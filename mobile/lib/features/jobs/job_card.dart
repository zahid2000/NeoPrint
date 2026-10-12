import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/format/formats.dart';
import '../../core/network/idempotency.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_primary_button.dart';
import '../../shared/widgets/neo_status_chip.dart';
import 'jobs_controller.dart';
import 'jobs_repository.dart';

/// How a job's status is shown: colour, icon and words together
/// (docs/DESIGN_SYSTEM.md section 4, "Sifariş kartı").
({String label, StatusTone tone, IconData icon}) jobStatusLook(
  AppLocalizations l10n,
  String status,
) => switch (status) {
  'PendingPayment' => (
    label: l10n.jobStatusPendingPayment,
    tone: StatusTone.warning,
    icon: Icons.payments_outlined,
  ),
  'Paid' => (
    label: l10n.jobStatusPaid,
    tone: StatusTone.info,
    icon: Icons.check,
  ),
  'Queued' => (
    label: l10n.jobStatusQueued,
    tone: StatusTone.info,
    icon: Icons.schedule,
  ),
  'Printing' => (
    label: l10n.jobStatusPrinting,
    tone: StatusTone.info,
    icon: Icons.print_outlined,
  ),
  'Completed' => (
    label: l10n.jobStatusCompleted,
    tone: StatusTone.success,
    icon: Icons.check_circle_outline,
  ),
  'Interrupted' => (
    label: l10n.jobStatusInterrupted,
    tone: StatusTone.danger,
    icon: Icons.error_outline,
  ),
  'Suspected' => (
    label: l10n.jobStatusSuspected,
    tone: StatusTone.warning,
    icon: Icons.hourglass_empty,
  ),
  'Cancelled' => (
    label: l10n.jobStatusCancelled,
    tone: StatusTone.neutral,
    icon: Icons.block,
  ),
  'Expired' => (
    label: l10n.jobStatusExpired,
    tone: StatusTone.neutral,
    icon: Icons.timer_off_outlined,
  ),
  _ => (
    label: l10n.jobStatusDraft,
    tone: StatusTone.neutral,
    icon: Icons.radio_button_unchecked,
  ),
};

/// One of the user's jobs on the home tab.
class JobCard extends ConsumerWidget {
  const JobCard({super.key, required this.job});

  final MyJob job;

  /// "Ləğv et və məbləği qaytar": asked once more, then done.
  Future<void> _cancel(BuildContext context, WidgetRef ref) async {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;
    final language = Localizations.localeOf(context).languageCode;

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(l10n.jobCancelTitle),
        content: Text(
          l10n.jobCancelBody(formatMoney(job.amount, language: language)),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: Text(l10n.jobCancelKeep),
          ),
          FilledButton(
            style: FilledButton.styleFrom(
              backgroundColor: tokens.danger,
              foregroundColor: tokens.neutral0,
            ),
            onPressed: () => Navigator.pop(context, true),
            child: Text(l10n.jobCancelConfirm),
          ),
        ],
      ),
    );

    if (confirmed != true || !context.mounted) {
      return;
    }

    final messenger = ScaffoldMessenger.of(context);
    final problem = await ref.read(myJobsProvider.notifier).cancel(job.id);

    messenger.showSnackBar(
      SnackBar(
        content: Text(
          problem == null ? l10n.jobCancelDone : apiErrorText(l10n, problem),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    // A scheduled print waiting for its time is not a paid order waiting at
    // the kiosk (T-158).
    final look = job.scheduledAt != null && job.status == 'Paid'
        ? (
            label: l10n.jobStatusScheduled(
              formatDateTime(
                job.scheduledAt!,
                today: l10n.commonToday,
                yesterday: l10n.commonYesterday,
              ),
            ),
            tone: StatusTone.info,
            icon: Icons.schedule,
          )
        : jobStatusLook(l10n, job.status);
    final language = Localizations.localeOf(context).languageCode;
    final kiosk = [
      if (job.kioskName?.isNotEmpty ?? false) job.kioskName!,
      if (job.kioskCode?.isNotEmpty ?? false) job.kioskCode!,
    ].join(' · ');

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // The status takes what it needs, up to a little over half the
            // card; the name always keeps the rest. Without the limit a long
            // status ("waiting for its time", in Russian) pushed past the
            // card's edge on a narrow phone.
            LayoutBuilder(
              builder: (context, box) => Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: Text(
                      job.fileName ?? job.jobCode,
                      style: theme.textTheme.titleMedium,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  const SizedBox(width: Spacing.x2),
                  ConstrainedBox(
                    constraints: BoxConstraints(maxWidth: box.maxWidth * 0.6),
                    child: NeoStatusChip(
                      label: look.label,
                      tone: look.tone,
                      icon: look.icon,
                    ),
                  ),
                ],
              ),
            ),
            if (kiosk.isNotEmpty) ...[
              const SizedBox(height: Spacing.x1),
              Text(
                kiosk,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: tokens.neutral600,
                ),
              ),
            ],
            const SizedBox(height: Spacing.x2),
            Row(
              children: [
                Expanded(
                  child: Text(
                    l10n.jobPagesCopies(job.pages, job.copies),
                    style: theme.textTheme.bodyMedium,
                  ),
                ),
                Text(
                  formatMoney(job.amount, language: language),
                  style: theme.textTheme.bodyMedium?.copyWith(
                    fontWeight: FontWeights.medium,
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                ),
              ],
            ),
            const SizedBox(height: Spacing.x1),
            Text(
              formatDateTime(
                job.createdAt,
                today: l10n.commonToday,
                yesterday: l10n.commonYesterday,
              ),
              style: theme.textTheme.bodySmall?.copyWith(
                color: tokens.neutral600,
              ),
            ),
            if (job.status == 'Suspected') ...[
              const SizedBox(height: Spacing.x3),
              NeoBanner(message: l10n.jobSuspectedNote, tone: StatusTone.info),
            ] else if (job.reason?.isNotEmpty ?? false) ...[
              const SizedBox(height: Spacing.x2),
              Text(job.reason!, style: theme.textTheme.bodySmall),
            ],
            if (job.offersRefund) ...[
              // Plan section 9.6: wait, or cancel and take the money back.
              const SizedBox(height: Spacing.x3),
              NeoBanner(
                message: job.status == 'Interrupted'
                    ? l10n.jobInterruptedNote
                    : l10n.jobKioskStoppedNote,
                tone: StatusTone.warning,
              ),
              const SizedBox(height: Spacing.x2),
              SizedBox(
                width: double.infinity,
                child: OutlinedButton(
                  onPressed: () => _cancel(context, ref),
                  child: Text(l10n.jobCancelRefund),
                ),
              ),
            ] else if (job.canCancel) ...[
              const SizedBox(height: Spacing.x2),
              Align(
                alignment: AlignmentDirectional.centerStart,
                child: TextButton.icon(
                  onPressed: () => _cancel(context, ref),
                  icon: const Icon(Icons.close),
                  label: Text(l10n.jobCancel),
                ),
              ),
            ],
            if (job.canReport) ...[
              const SizedBox(height: Spacing.x2),
              Align(
                alignment: AlignmentDirectional.centerStart,
                child: TextButton.icon(
                  onPressed: () => showReportSheet(context, job),
                  icon: const Icon(Icons.report_outlined),
                  label: Text(l10n.jobReport),
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

/// "Problem barədə bildir": a message about one job, to support.
Future<void> showReportSheet(BuildContext context, MyJob job) =>
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (_) => _ReportSheet(job: job),
    );

class _ReportSheet extends ConsumerStatefulWidget {
  const _ReportSheet({required this.job});

  final MyJob job;

  @override
  ConsumerState<_ReportSheet> createState() => _ReportSheetState();
}

class _ReportSheetState extends ConsumerState<_ReportSheet> {
  static const maxLength = 1000;

  final _text = TextEditingController();

  /// One key for this message, kept across a retry after a lost answer.
  final _key = newIdempotencyKey();

  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _text.dispose();
    super.dispose();
  }

  Future<void> _send() async {
    final l10n = AppLocalizations.of(context);
    final text = _text.text.trim();

    if (text.isEmpty) {
      setState(() => _error = l10n.reportEmpty);
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
    });

    final error = await ref
        .read(myJobsProvider.notifier)
        .report(widget.job.id, text, idempotencyKey: _key);

    if (!mounted) {
      return;
    }

    if (error == null) {
      final messenger = ScaffoldMessenger.of(context);
      Navigator.of(context).pop();
      messenger.showSnackBar(SnackBar(content: Text(l10n.reportSent)));
    } else {
      setState(() {
        _busy = false;
        _error = apiErrorText(l10n, error);
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);

    return Padding(
      padding: EdgeInsets.only(
        left: Spacing.x4,
        right: Spacing.x4,
        bottom: MediaQuery.viewInsetsOf(context).bottom + Spacing.x4,
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(l10n.jobReport, style: theme.textTheme.titleLarge),
          const SizedBox(height: Spacing.x1),
          Text(
            widget.job.fileName ?? widget.job.jobCode,
            style: theme.textTheme.bodySmall,
          ),
          const SizedBox(height: Spacing.x2),
          Text(l10n.reportHint, style: theme.textTheme.bodyMedium),
          const SizedBox(height: Spacing.x4),
          if (_error != null) ...[
            NeoBanner.error(message: _error!),
            const SizedBox(height: Spacing.x3),
          ],
          TextField(
            controller: _text,
            minLines: 3,
            maxLines: 6,
            maxLength: maxLength,
            enabled: !_busy,
            decoration: InputDecoration(labelText: l10n.reportField),
          ),
          const SizedBox(height: Spacing.x3),
          NeoPrimaryButton(
            label: l10n.reportSend,
            onPressed: _send,
            busy: _busy,
          ),
        ],
      ),
    );
  }
}
