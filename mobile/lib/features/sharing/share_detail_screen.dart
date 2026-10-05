import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../files/file_models.dart';
import '../files/file_preview_screen.dart';
import '../files/files_controller.dart';
import 'share_models.dart';
import 'share_repository.dart';
import 'shares_controller.dart';
import 'shares_screen.dart';

/// A received file (docs/DESIGN_SYSTEM.md, "Mobil: fayl paylaşımı"): who sent
/// it, its pages, and "Fayllarıma saxla" - nothing is the user's before that.
class ShareDetailScreen extends ConsumerStatefulWidget {
  const ShareDetailScreen({super.key, required this.share});

  final ShareItem share;

  @override
  ConsumerState<ShareDetailScreen> createState() => _ShareDetailScreenState();
}

class _ShareDetailScreenState extends ConsumerState<ShareDetailScreen> {
  late ShareAvailability _availability = widget.share.availability;
  UserFile? _saved;
  bool _busy = false;
  String? _error;

  Future<void> _save() async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      final file = await ref
          .read(shareRepositoryProvider)
          .save(widget.share.id);

      ref.invalidate(receivedSharesProvider);
      unawaited(ref.read(filesControllerProvider.notifier).load());

      if (!mounted) {
        return;
      }

      setState(() {
        _busy = false;
        _saved = file;
        _availability = ShareAvailability.saved;
      });

      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(l10n.shareSavedSnack),
          action: SnackBarAction(label: l10n.shareOpen, onPressed: _openSaved),
        ),
      );
    } on ApiException catch (error) {
      if (!mounted) {
        return;
      }

      setState(() {
        _busy = false;
        // The sender deleted or changed it meanwhile: the screen says so.
        _availability = switch (error.code) {
          'shares.fileGone' => ShareAvailability.gone,
          'shares.fileChanged' => ShareAvailability.changed,
          _ => _availability,
        };
        _error = _availability == ShareAvailability.available
            ? apiErrorText(l10n, error)
            : null;
      });
    }
  }

  /// The copy in "Fayllarım": its preview, or the list when it is not ready yet.
  void _openSaved() {
    final saved = _saved;
    final id = saved?.id ?? widget.share.savedFileId;
    final file =
        saved ??
        ref
            .read(filesControllerProvider)
            .files
            .where((candidate) => candidate.id == id)
            .firstOrNull;

    if (file != null && file.canPreview) {
      context.push(Routes.filePreview(file.id), extra: file);
    } else {
      context.go(Routes.files);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final share = widget.share;
    final unavailable =
        _availability == ShareAvailability.gone ||
        _availability == ShareAvailability.changed;

    return Scaffold(
      appBar: AppBar(
        title: Text(
          share.fileName,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
      ),
      body: SafeArea(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(
                Spacing.x4,
                Spacing.x4,
                Spacing.x4,
                0,
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    [
                      l10n.shareFrom(share.otherPublicId),
                      formatDateTime(
                        share.createdAt,
                        today: l10n.commonToday,
                        yesterday: l10n.commonYesterday,
                      ),
                    ].join(' · '),
                    style: theme.textTheme.bodyMedium,
                  ),
                  if (share.message case final message?) ...[
                    const SizedBox(height: Spacing.x1),
                    Text(
                      '“$message”',
                      style: theme.textTheme.bodyMedium?.copyWith(
                        fontStyle: FontStyle.italic,
                      ),
                    ),
                  ],
                  const SizedBox(height: Spacing.x2),
                  ShareStatusChip(availability: _availability, received: true),
                  if (share.isSensitive && !unavailable) ...[
                    const SizedBox(height: Spacing.x3),
                    NeoBanner(
                      message: l10n.shareSensitiveInfo,
                      tone: StatusTone.info,
                      icon: Icons.lock_outline,
                    ),
                  ],
                ],
              ),
            ),
            Expanded(
              child: unavailable
                  ? Padding(
                      padding: const EdgeInsets.all(Spacing.x4),
                      child: Align(
                        alignment: Alignment.topCenter,
                        child: NeoBanner(
                          message: _availability == ShareAvailability.gone
                              ? l10n.shareGoneBody
                              : l10n.shareChangedBody,
                          tone: _availability == ShareAvailability.gone
                              ? StatusTone.danger
                              : StatusTone.warning,
                        ),
                      ),
                    )
                  : PagedPreview(
                      pageCount: share.pageCount,
                      load: (page, width) =>
                          _availability == ShareAvailability.saved
                          ? ref
                                .read(filesRepositoryProvider)
                                .pageImage(
                                  _saved?.id ?? share.savedFileId!,
                                  page,
                                  width,
                                )
                          : ref
                                .read(shareRepositoryProvider)
                                .pageImage(share.id, page, width),
                    ),
            ),
            if (_error != null)
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: Spacing.x4),
                child: NeoBanner.error(
                  message: _error!,
                  onClose: () => setState(() => _error = null),
                ),
              ),
            if (!unavailable)
              Padding(
                padding: const EdgeInsets.all(Spacing.x4),
                child: _availability == ShareAvailability.saved
                    ? FilledButton.icon(
                        onPressed: _openSaved,
                        icon: const Icon(Icons.folder_open_outlined),
                        label: Text(l10n.shareOpenInFiles),
                      )
                    : FilledButton.icon(
                        onPressed: _busy ? null : _save,
                        icon: _busy
                            ? const SizedBox.square(
                                dimension: Spacing.x4,
                                child: CircularProgressIndicator(
                                  strokeWidth: 2,
                                ),
                              )
                            : const Icon(Icons.save_alt_outlined),
                        label: Text(_busy ? l10n.shareSaving : l10n.shareSave),
                      ),
              ),
          ],
        ),
      ),
    );
  }
}
