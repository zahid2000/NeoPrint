import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/platform/browser.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_primary_button.dart';
import 'data_export.dart';

/// "Məlumatlarımı yüklə" (plan section 2.13; docs/DESIGN_SYSTEM.md, "Mobil:
/// məxfilik"): one main button for the state the archive is in.
class DataExportScreen extends ConsumerStatefulWidget {
  const DataExportScreen({super.key});

  @override
  ConsumerState<DataExportScreen> createState() => _DataExportScreenState();
}

class _DataExportScreenState extends ConsumerState<DataExportScreen> {
  bool _busy = false;

  Future<void> _prepare() async {
    setState(() => _busy = true);
    await ref.read(dataExportProvider.notifier).request();
    if (mounted) {
      setState(() => _busy = false);
    }
  }

  Future<void> _download(DataExport export) async {
    final l10n = AppLocalizations.of(context);
    final messenger = ScaffoldMessenger.of(context);
    if (!await ref.read(browserProvider)(downloadUri(export))) {
      messenger.showSnackBar(SnackBar(content: Text(l10n.exportOpenFailed)));
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final export = ref.watch(dataExportProvider);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.exportTitle)),
      body: switch (export) {
        AsyncData(:final value) => RefreshIndicator(
          onRefresh: () => ref.read(dataExportProvider.notifier).refresh(),
          child: ListView(
            padding: const EdgeInsets.all(Spacing.x4),
            children: [
              _Intro(),
              const SizedBox(height: Spacing.x4),
              ..._state(context, value),
            ],
          ),
        ),
        AsyncError(:final error) => NeoEmptyState(
          icon: Icons.cloud_off_outlined,
          message: l10n.exportLoadFailed(
            error is ApiException
                ? apiErrorText(l10n, error)
                : l10n.errorServer,
          ),
          action: OutlinedButton(
            onPressed: () => ref.invalidate(dataExportProvider),
            child: Text(l10n.commonRetry),
          ),
        ),
        _ => const Center(child: CircularProgressIndicator()),
      },
    );
  }

  List<Widget> _state(BuildContext context, DataExport? export) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final language = Localizations.localeOf(context).languageCode;

    Widget prepare(String label) => NeoPrimaryButton(
      label: label,
      icon: Icons.inventory_2_outlined,
      busy: _busy,
      onPressed: _busy ? null : _prepare,
    );

    switch (export?.status) {
      case DataExportStatus.preparing:
        return [
          Row(
            children: [
              const SizedBox.square(
                dimension: Spacing.x6,
                child: CircularProgressIndicator(strokeWidth: 2),
              ),
              const SizedBox(width: Spacing.x3),
              Expanded(
                child: Text(
                  l10n.exportPreparing,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: tokens.info,
                  ),
                ),
              ),
            ],
          ),
        ];

      case DataExportStatus.ready when export!.canDownload:
        return [
          if (!export.includesFiles) ...[
            NeoBanner(
              message: l10n.exportFilesListed,
              tone: StatusTone.warning,
            ),
            const SizedBox(height: Spacing.x3),
          ],
          NeoPrimaryButton(
            label: l10n.exportDownload,
            icon: Icons.download_outlined,
            onPressed: () => _download(export),
          ),
          const SizedBox(height: Spacing.x2),
          Text(
            l10n.exportValidUntil(
              formatDateTime(
                export.expiresAt!,
                today: l10n.commonToday,
                yesterday: l10n.commonYesterday,
              ),
            ),
            style: theme.textTheme.bodySmall?.copyWith(
              color: tokens.neutral600,
            ),
          ),
          Text(
            l10n.exportSize(formatBytes(export.sizeBytes, language: language)),
            style: theme.textTheme.bodySmall?.copyWith(
              color: tokens.neutral600,
            ),
          ),
        ];

      case DataExportStatus.failed:
        return [
          NeoBanner(message: export!.failureReason ?? l10n.errorServer),
          const SizedBox(height: Spacing.x3),
          prepare(l10n.exportRetry),
        ];

      default:
        // None yet, or the last one is past its 24 hours.
        return [prepare(l10n.exportPrepare)];
    }
  }
}

class _Intro extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(Icons.inventory_2_outlined, color: context.tokens.brand500),
            const SizedBox(width: Spacing.x3),
            Expanded(
              child: Text(l10n.exportIntro, style: theme.textTheme.bodyMedium),
            ),
          ],
        ),
      ),
    );
  }
}
