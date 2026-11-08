import 'package:flutter/material.dart';

import '../../core/format/formats.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_design.dart';
import '../../shared/widgets/neo_status_chip.dart';
import 'auto_delete_sheet.dart';
import 'file_models.dart';

/// One file on a list (T-342; the design's "Fayllarım"): its type's coloured
/// square, the name, "12 səhifə · 2,4 MB", and - only when there is
/// something to say - its state. [trailing] is the menu on the files tab.
class FileRow extends StatelessWidget {
  const FileRow({required this.file, this.onTap, this.trailing, super.key});

  final UserFile file;
  final VoidCallback? onTap;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final language = Localizations.localeOf(context).languageCode;

    final details = [
      if (file.pageCount > 0) l10n.filesPageCount(file.pageCount),
      formatBytes(file.sizeBytes, language: language),
    ].join(' · ');

    final chips = [
      if (file.status != FileStatus.ready)
        NeoStatusChip(
          label: switch (file.status) {
            FileStatus.pending => l10n.filesStatusPending,
            FileStatus.converting => l10n.filesStatusConverting,
            _ => l10n.filesStatusFailed,
          },
          tone: file.status == FileStatus.failed
              ? StatusTone.danger
              : StatusTone.info,
          icon: file.status == FileStatus.failed
              ? Icons.error_outline
              : Icons.hourglass_empty,
        ),
      if (file.isSensitive)
        NeoStatusChip(
          label: l10n.filesSensitive,
          tone: StatusTone.warning,
          icon: Icons.lock_outline,
        ),
      // When it deletes itself is worth knowing at a glance (T-147).
      if (autoDeleteChipLabel(l10n, file.autoDelete) case final label?)
        NeoStatusChip(
          label: label,
          tone: StatusTone.neutral,
          icon: Icons.auto_delete_outlined,
        ),
    ];

    return Card(
      margin: EdgeInsets.zero,
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsetsDirectional.only(
            start: Spacing.x3,
            top: Spacing.x3,
            bottom: Spacing.x3,
            end: Spacing.x1,
          ),
          child: Row(
            children: [
              FileTypeTile(extension: file.extension),
              const SizedBox(width: Spacing.x3),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      file.fileName,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: theme.textTheme.titleSmall,
                    ),
                    const SizedBox(height: Spacing.x1),
                    Text(
                      details,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: tokens.neutral600,
                      ),
                    ),
                    if (chips.isNotEmpty) ...[
                      const SizedBox(height: Spacing.x2),
                      Wrap(
                        spacing: Spacing.x2,
                        runSpacing: Spacing.x1,
                        children: chips,
                      ),
                    ],
                    if (file.status == FileStatus.failed &&
                        file.failureReason != null) ...[
                      const SizedBox(height: Spacing.x1),
                      Text(
                        file.failureReason!,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: tokens.danger,
                        ),
                      ),
                    ],
                  ],
                ),
              ),
              if (trailing != null)
                trailing!
              else
                Padding(
                  padding: const EdgeInsetsDirectional.only(end: Spacing.x2),
                  child: Icon(Icons.chevron_right, color: tokens.neutral400),
                ),
            ],
          ),
        ),
      ),
    );
  }
}
