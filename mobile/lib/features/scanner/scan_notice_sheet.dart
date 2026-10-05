import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_models.dart';
import '../../core/l10n/language_controller.dart';
import '../../core/network/api_client.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_primary_button.dart';
import '../legal/legal_documents.dart';
import '../legal/markdown_text.dart';
import 'scanner_repository.dart';

/// Makes sure the current scan notice is accepted before the scanner opens
/// (plan sections 2.11, 2.13; docs/DESIGN_SYSTEM.md "Mobil: sənəd skanı").
/// The acceptance is recorded on the server, which checks it again when the
/// scan is saved.
///
/// Returns false when the user declined or the notice could not be handled;
/// the reason has then been shown.
Future<bool> ensureScanNotice(BuildContext context, WidgetRef ref) async {
  final l10n = AppLocalizations.of(context);
  final messenger = ScaffoldMessenger.of(context);
  final language = ref.read(appLanguageProvider).code;
  final String? version;

  try {
    version = await ref.read(scannerRepositoryProvider).pendingNotice(language);
  } on ApiException catch (error) {
    messenger.showSnackBar(
      SnackBar(
        content: Text(l10n.docScanNoticeLoadFailed(apiErrorText(l10n, error))),
      ),
    );
    return false;
  }

  if (version == null || !context.mounted) {
    return version == null;
  }

  final accepted = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    showDragHandle: true,
    builder: (_) => _ScanNoticeSheet(version: version!),
  );

  return accepted ?? false;
}

class _ScanNoticeSheet extends ConsumerStatefulWidget {
  const _ScanNoticeSheet({required this.version});

  final String version;

  @override
  ConsumerState<_ScanNoticeSheet> createState() => _ScanNoticeSheetState();
}

class _ScanNoticeSheetState extends ConsumerState<_ScanNoticeSheet> {
  bool _sending = false;
  String? _error;

  Future<void> _accept() async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _sending = true;
      _error = null;
    });

    try {
      await ref.read(scannerRepositoryProvider).acceptNotice(widget.version);
      if (mounted) {
        Navigator.pop(context, true);
      }
    } on ApiException catch (error) {
      if (mounted) {
        setState(() {
          _sending = false;
          _error = l10n.docScanNoticeAcceptFailed(apiErrorText(l10n, error));
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final document = ref.watch(
      legalDocumentProvider(LegalDocumentType.scanNotice),
    );

    return SizedBox(
      height: MediaQuery.sizeOf(context).height * 0.85,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(
          Spacing.x4,
          0,
          Spacing.x4,
          Spacing.x4,
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              l10n.docScanNoticeTitle,
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: Spacing.x3),
            Expanded(
              child: switch (document) {
                AsyncData(:final value) => SingleChildScrollView(
                  child: MarkdownText(value.content),
                ),
                AsyncError(:final error) => Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    NeoBanner.error(
                      message: l10n.docScanNoticeLoadFailed(
                        error is ApiException
                            ? apiErrorText(l10n, error)
                            : l10n.errorServer,
                      ),
                    ),
                    const SizedBox(height: Spacing.x3),
                    OutlinedButton(
                      onPressed: () => ref.invalidate(
                        legalDocumentProvider(LegalDocumentType.scanNotice),
                      ),
                      child: Text(l10n.commonRetry),
                    ),
                  ],
                ),
                _ => const Center(child: CircularProgressIndicator()),
              },
            ),
            if (_error case final error?) ...[
              const SizedBox(height: Spacing.x3),
              NeoBanner.error(message: error),
            ],
            const SizedBox(height: Spacing.x3),
            NeoPrimaryButton(
              label: l10n.docScanNoticeAccept,
              busy: _sending,
              onPressed: _sending || document is! AsyncData ? null : _accept,
            ),
            const SizedBox(height: Spacing.x2),
            TextButton(
              onPressed: _sending ? null : () => Navigator.pop(context, false),
              child: Text(l10n.docScanNoticeLater),
            ),
          ],
        ),
      ),
    );
  }
}
