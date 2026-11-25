import 'dart:async';

import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_client.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import 'file_models.dart';
import 'files_controller.dart';

/// "Mətni redaktə et" (plan section 2.2, T-458; docs/DESIGN_SYSTEM.md,
/// "Mobil: mətn redaktoru"): a plain text file - TXT, JSON, CSV, XML, HTML -
/// as text. Saving keeps a new version in the file's own format; the original
/// is never changed, and the file is converted again for printing.
class TextEditorScreen extends ConsumerStatefulWidget {
  const TextEditorScreen({super.key, required this.file});

  final UserFile file;

  /// The longest text the server takes (`FileEdit.MaxTextLength`).
  static const maxLength = 1000000;

  @override
  ConsumerState<TextEditorScreen> createState() => _TextEditorScreenState();
}

class _TextEditorScreenState extends ConsumerState<TextEditorScreen> {
  final _text = TextEditingController();

  /// The text as it was opened, to tell an edit from none.
  String? _loaded;
  ApiException? _loadError;
  String? _error;
  bool _busy = false;

  bool get _changed => _loaded != null && _text.text != _loaded;

  @override
  void initState() {
    super.initState();
    _text.addListener(() => setState(() {}));
    _load();
  }

  @override
  void dispose() {
    _text.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    setState(() => _loadError = null);
    try {
      final text = await ref
          .read(filesRepositoryProvider)
          .currentText(widget.file.id);
      if (!mounted) {
        return;
      }
      setState(() {
        _loaded = text;
        _text.text = text;
      });
    } on ApiException catch (error) {
      if (mounted) {
        setState(() => _loadError = error);
      }
    }
  }

  Future<void> _save() async {
    final l10n = AppLocalizations.of(context);
    if (_text.text.length > TextEditorScreen.maxLength) {
      setState(() => _error = l10n.textEditorTooLong);
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      await ref
          .read(filesRepositoryProvider)
          .editText(widget.file.id, _text.text);
      // Pending again: the list shows it being prepared, and polls.
      unawaited(ref.read(filesControllerProvider.notifier).load());

      if (!mounted) {
        return;
      }
      _loaded = _text.text;
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(l10n.textEditorSaved)));
      Navigator.of(context).pop(true);
    } on ApiException catch (error) {
      if (mounted) {
        setState(() {
          _busy = false;
          _error = apiErrorText(l10n, error);
        });
      }
    }
  }

  /// Leaving with unsaved text asks first; the file is not touched.
  Future<void> _leave() async {
    final l10n = AppLocalizations.of(context);
    final discard = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(l10n.textEditorDiscardTitle),
        content: Text(l10n.textEditorDiscardBody),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, true),
            child: Text(l10n.textEditorDiscard),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, false),
            child: Text(l10n.textEditorKeepEditing),
          ),
        ],
      ),
    );

    if ((discard ?? false) && mounted) {
      _loaded = _text.text;
      Navigator.of(context).pop(false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;

    return PopScope(
      canPop: !_changed || _busy,
      onPopInvokedWithResult: (popped, _) {
        if (!popped) {
          _leave();
        }
      },
      child: Scaffold(
        appBar: AppBar(
          title: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(l10n.textEditorTitle),
              Text(
                widget.file.fileName,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: tokens.neutral600,
                ),
              ),
            ],
          ),
        ),
        body: SafeArea(child: _body(l10n, tokens)),
      ),
    );
  }

  Widget _body(AppLocalizations l10n, NeoPrintTokens tokens) {
    final loadError = _loadError;
    if (loadError != null) {
      return Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            NeoBanner.error(message: apiErrorText(l10n, loadError)),
            const SizedBox(height: Spacing.x4),
            OutlinedButton.icon(
              onPressed: _load,
              icon: const Icon(Icons.refresh),
              label: Text(l10n.commonRetry),
            ),
          ],
        ),
      );
    }

    if (_loaded == null) {
      return const Center(child: CircularProgressIndicator());
    }

    return Padding(
      padding: const EdgeInsets.all(Spacing.x4),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (_error != null) ...[
            NeoBanner.error(
              message: _error!,
              onClose: () => setState(() => _error = null),
            ),
            const SizedBox(height: Spacing.x3),
          ],
          Expanded(
            child: TextField(
              key: const ValueKey('textEditorField'),
              controller: _text,
              enabled: !_busy,
              expands: true,
              maxLines: null,
              keyboardType: TextInputType.multiline,
              textAlignVertical: TextAlignVertical.top,
              decoration: InputDecoration(hintText: l10n.textEditorHint),
            ),
          ),
          const SizedBox(height: Spacing.x3),
          FilledButton.icon(
            onPressed: _changed && !_busy ? _save : null,
            icon: _busy
                ? const SizedBox.square(
                    dimension: Spacing.x4,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.save_outlined),
            label: Text(l10n.textEditorSave),
          ),
        ],
      ),
    );
  }
}
