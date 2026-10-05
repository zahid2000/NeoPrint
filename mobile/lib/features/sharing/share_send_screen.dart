import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_status_chip.dart';
import '../files/file_models.dart';
import '../files/files_controller.dart';
import '../payments/scan_screen.dart';
import 'share_models.dart';
import 'share_repository.dart';
import 'shares_controller.dart';

/// "Faylı paylaş" (docs/DESIGN_SYSTEM.md, "Mobil: fayl paylaşımı"): a file,
/// the recipient's `NP-` id - typed or read from their QR - and a note.
class ShareSendScreen extends ConsumerStatefulWidget {
  const ShareSendScreen({super.key, this.file, this.to});

  /// From a file's menu; null from a share link, where the file is chosen here.
  final UserFile? file;

  /// From a share link: the recipient, already filled in.
  final String? to;

  @override
  ConsumerState<ShareSendScreen> createState() => _ShareSendScreenState();
}

class _ShareSendScreenState extends ConsumerState<ShareSendScreen> {
  late final _recipient = TextEditingController(text: widget.to ?? '');
  final _message = TextEditingController();
  late UserFile? _file = widget.file;

  bool _busy = false;
  bool _tried = false;
  String? _recipientError;
  String? _error;

  @override
  void dispose() {
    _recipient.dispose();
    _message.dispose();
    super.dispose();
  }

  /// What is still missing, in words, once "Göndər" was pressed.
  String? _missing(AppLocalizations l10n) {
    if (_file == null) {
      return l10n.shareNeedFile;
    }

    final id = _recipient.text;
    if (id.trim().isEmpty) {
      return l10n.shareNeedRecipient;
    }

    return PublicId.isValid(id) ? null : l10n.shareInvalidRecipient;
  }

  Future<void> _chooseFile() async {
    final chosen = await showModalBottomSheet<UserFile>(
      context: context,
      isScrollControlled: true,
      builder: (_) => const _ReadyFileSheet(),
    );

    if (chosen != null) {
      setState(() => _file = chosen);
    }
  }

  Future<void> _scan() async {
    final id = await Navigator.of(context).push<String>(
      MaterialPageRoute(builder: (_) => const ScanScreen(pickShareId: true)),
    );

    if (id != null && mounted) {
      setState(() {
        _recipient.text = id;
        _recipientError = null;
      });
    }
  }

  Future<void> _send() async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _tried = true;
      _error = null;
      _recipientError = null;
    });

    final file = _file;
    if (_missing(l10n) != null || file == null) {
      return;
    }

    final to = PublicId.normalise(_recipient.text);

    if (file.isSensitive && !await _confirmSensitive(file, to)) {
      return;
    }

    setState(() => _busy = true);

    try {
      await ref
          .read(shareRepositoryProvider)
          .send(
            fileId: file.id,
            toPublicId: to,
            message: _message.text,
            confirmSensitive: file.isSensitive,
          );

      ref.invalidate(sentSharesProvider);

      if (!mounted) {
        return;
      }

      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(l10n.shareSent(to))));
      context.pop();
    } on ApiException catch (error) {
      if (!mounted) {
        return;
      }

      // The id's own problems go under the field; anything else above.
      final aboutId =
          error.errorFor('toPublicId') ??
          (error.code == 'notFound.shareRecipient' ? error.message : null);

      setState(() {
        _busy = false;
        if (aboutId != null && aboutId.isNotEmpty) {
          _recipientError = aboutId;
        } else {
          _error = apiErrorText(l10n, error);
        }
      });
    }
  }

  Future<bool> _confirmSensitive(UserFile file, String to) async {
    final l10n = AppLocalizations.of(context);

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(l10n.shareConfirmTitle),
        content: Text(l10n.shareConfirmBody(file.fileName, to)),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: Text(l10n.commonCancel),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: Text(l10n.shareSend),
          ),
        ],
      ),
    );

    return confirmed ?? false;
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final file = _file;
    final missing = _tried ? _missing(l10n) : null;
    final canScan = ref.watch(canScanProvider);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.shareSendTitle)),
      body: SafeArea(
        child: ListView(
          padding: const EdgeInsets.all(Spacing.x4),
          children: [
            if (_error != null) ...[
              NeoBanner.error(
                message: _error!,
                onClose: () => setState(() => _error = null),
              ),
              const SizedBox(height: Spacing.x4),
            ],
            Card(
              margin: EdgeInsets.zero,
              child: file == null
                  ? ListTile(
                      leading: Icon(
                        Icons.description_outlined,
                        color: context.tokens.brand500,
                      ),
                      title: Text(l10n.shareChooseFile),
                      trailing: const Icon(Icons.chevron_right),
                      onTap: _busy ? null : _chooseFile,
                    )
                  : ListTile(
                      leading: Icon(
                        file.isSensitive
                            ? Icons.lock_outline
                            : Icons.description_outlined,
                        color: context.tokens.brand500,
                      ),
                      title: Text(file.fileName),
                      subtitle: Padding(
                        padding: const EdgeInsets.only(top: Spacing.x1),
                        child: Wrap(
                          spacing: Spacing.x2,
                          runSpacing: Spacing.x1,
                          crossAxisAlignment: WrapCrossAlignment.center,
                          children: [
                            Text(l10n.filesPageCount(file.pageCount)),
                            if (file.isSensitive)
                              NeoStatusChip(
                                label: l10n.filesSensitive,
                                tone: StatusTone.warning,
                                icon: Icons.lock_outline,
                              ),
                          ],
                        ),
                      ),
                      // From a link the file is chosen here, so it can be changed.
                      trailing: widget.file == null
                          ? TextButton(
                              onPressed: _busy ? null : _chooseFile,
                              child: Text(l10n.shareChangeFile),
                            )
                          : null,
                    ),
            ),
            const SizedBox(height: Spacing.x4),
            TextField(
              controller: _recipient,
              enabled: !_busy,
              textCapitalization: TextCapitalization.characters,
              autocorrect: false,
              inputFormatters: [_UpperCase()],
              decoration: InputDecoration(
                labelText: l10n.shareRecipientLabel,
                hintText: l10n.shareRecipientHint,
                helperText: l10n.shareRecipientHelp,
                helperMaxLines: 2,
                errorText: _recipientError,
                errorMaxLines: 3,
                suffixIcon: canScan
                    ? IconButton(
                        tooltip: l10n.shareScanRecipient,
                        icon: const Icon(Icons.qr_code_scanner),
                        onPressed: _busy ? null : _scan,
                      )
                    : null,
              ),
              // What is missing is said again as the id is typed, so a
              // fixed problem does not stay on screen.
              onChanged: (_) => setState(() => _recipientError = null),
            ),
            const SizedBox(height: Spacing.x4),
            TextField(
              controller: _message,
              enabled: !_busy,
              maxLength: PublicId.maxMessage,
              decoration: InputDecoration(labelText: l10n.shareMessageLabel),
            ),
            if (file?.isSensitive ?? false) ...[
              const SizedBox(height: Spacing.x2),
              NeoBanner(
                message: l10n.shareSensitiveWarning,
                tone: StatusTone.warning,
              ),
            ],
            const SizedBox(height: Spacing.x4),
            if (missing != null) ...[
              Text(
                missing,
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: context.tokens.danger,
                ),
              ),
              const SizedBox(height: Spacing.x2),
            ],
            FilledButton.icon(
              onPressed: _busy ? null : _send,
              icon: _busy
                  ? const SizedBox.square(
                      dimension: Spacing.x4,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.send_outlined),
              label: Text(l10n.shareSend),
            ),
          ],
        ),
      ),
    );
  }
}

/// Ids are written in capitals: a typed "np-7kq2mx" shows as it will be sent.
class _UpperCase extends TextInputFormatter {
  @override
  TextEditingValue formatEditUpdate(
    TextEditingValue oldValue,
    TextEditingValue newValue,
  ) => newValue.copyWith(text: newValue.text.toUpperCase());
}

/// The files ready to send, one tap picks one.
class _ReadyFileSheet extends ConsumerWidget {
  const _ReadyFileSheet();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final state = ref.watch(filesControllerProvider);
    final ready = [
      for (final file in state.files)
        if (file.status == FileStatus.ready && file.pageCount > 0) file,
    ];

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              l10n.shareChooseFile,
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: Spacing.x3),
            Flexible(
              child: state.loading
                  ? const Center(child: CircularProgressIndicator())
                  : ready.isEmpty
                  ? Text(l10n.shareNoReadyFiles)
                  : ListView(
                      shrinkWrap: true,
                      children: [
                        for (final file in ready)
                          ListTile(
                            contentPadding: EdgeInsets.zero,
                            leading: Icon(
                              file.isSensitive
                                  ? Icons.lock_outline
                                  : Icons.description_outlined,
                              color: context.tokens.brand500,
                            ),
                            title: Text(file.fileName),
                            subtitle: Text(l10n.filesPageCount(file.pageCount)),
                            onTap: () => Navigator.pop(context, file),
                          ),
                      ],
                    ),
            ),
            if (ready.isEmpty && !state.loading) ...[
              const SizedBox(height: Spacing.x3),
              OutlinedButton(
                onPressed: () => context.go(Routes.files),
                child: Text(l10n.pickFileUpload),
              ),
            ],
          ],
        ),
      ),
    );
  }
}
