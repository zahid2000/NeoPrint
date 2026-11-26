import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:webview_flutter/webview_flutter.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/features/feature_flags.dart';
import '../../core/features/readiness.dart';
import '../../core/network/api_client.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import 'file_models.dart';
import 'files_controller.dart';

/// Whether "Redaktə et" opens [file] in the Office editor here: an Office
/// document, the `office_editor` flag on, and not the web build - the
/// editor is a web view, which a browser tab cannot hold.
bool canOpenOfficeEditor(FeatureFlags flags, UserFile file) =>
    !kIsWeb &&
    Readiness.officeEditor &&
    flags.isOn(FeatureKey.officeEditor) &&
    canEditOffice(file);

/// Draws the editor page: Collabora Online, opened with [session]'s token.
///
/// [onLoading] reports whether the page is still loading, [onFailed] that it
/// could not be loaded at all. A provider, so a test puts a plain widget in
/// its place - a web view needs the platform.
typedef OfficeEditorView = Widget Function(
  Uri page,
  OfficeSession session, {
  required ValueChanged<bool> onLoading,
  required VoidCallback onFailed,
});

final officeEditorViewProvider = Provider<OfficeEditorView>((ref) => _webView);

Widget _webView(
  Uri page,
  OfficeSession session, {
  required ValueChanged<bool> onLoading,
  required VoidCallback onFailed,
}) => _OfficeWebView(
  page: page,
  session: session,
  onLoading: onLoading,
  onFailed: onFailed,
);

/// "Redaktə et" for a Word, Excel or PowerPoint file (plan section 2.2,
/// Addım 9; T-459; docs/DESIGN_SYSTEM.md, "Mobil və kiosk: Office
/// redaktoru"): Collabora Online in a web view. A save there - its own
/// "Save", or leaving with changes - is a new version in the file's own
/// format, converted again for printing; the original is never changed.
class OfficeEditorScreen extends ConsumerStatefulWidget {
  const OfficeEditorScreen({super.key, required this.file});

  final UserFile file;

  @override
  ConsumerState<OfficeEditorScreen> createState() => _OfficeEditorScreenState();
}

class _OfficeEditorScreenState extends ConsumerState<OfficeEditorScreen> {
  OfficeSession? _session;
  String? _error;
  bool _pageLoading = true;

  @override
  void initState() {
    super.initState();
    _open();
  }

  Future<void> _open() async {
    setState(() {
      _error = null;
      _session = null;
      _pageLoading = true;
    });

    try {
      final session = await ref
          .read(filesRepositoryProvider)
          .officeSession(widget.file.id);
      if (mounted) {
        setState(() => _session = session);
      }
    } on ApiException catch (error) {
      if (mounted) {
        setState(
          () => _error = apiErrorText(AppLocalizations.of(context), error),
        );
      }
    }
  }

  /// Leaving asks first: the editor saves what changed on the way out.
  Future<void> _leave() async {
    final l10n = AppLocalizations.of(context);

    if (_session == null) {
      Navigator.of(context).pop(false);
      return;
    }

    final leave = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(l10n.officeEditorLeaveTitle),
        content: Text(l10n.officeEditorLeaveBody),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: Text(l10n.officeEditorKeepEditing),
          ),
          FilledButton(
            key: const ValueKey('officeEditorLeave'),
            onPressed: () => Navigator.pop(context, true),
            child: Text(l10n.officeEditorLeave),
          ),
        ],
      ),
    );

    if ((leave ?? false) && mounted) {
      // A save on the way out makes the file Pending again: the list polls.
      unawaited(ref.read(filesControllerProvider.notifier).load());
      Navigator.of(context).pop(true);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;

    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (popped, _) {
        if (!popped) {
          _leave();
        }
      },
      child: Scaffold(
        appBar: AppBar(
          leading: IconButton(
            key: const ValueKey('officeEditorClose'),
            tooltip: l10n.commonClose,
            icon: const Icon(Icons.close),
            onPressed: _leave,
          ),
          title: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(l10n.officeEditorTitle),
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
          bottom: _session != null && _pageLoading
              ? const PreferredSize(
                  preferredSize: Size.fromHeight(Spacing.x1),
                  child: LinearProgressIndicator(),
                )
              : null,
        ),
        body: SafeArea(child: _body(l10n)),
      ),
    );
  }

  Widget _body(AppLocalizations l10n) {
    final error = _error;
    if (error != null) {
      return Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            NeoBanner.error(message: error),
            const SizedBox(height: Spacing.x4),
            OutlinedButton.icon(
              key: const ValueKey('officeEditorRetry'),
              onPressed: _open,
              icon: const Icon(Icons.refresh),
              label: Text(l10n.commonRetry),
            ),
          ],
        ),
      );
    }

    final session = _session;
    if (session == null) {
      return const Center(child: CircularProgressIndicator());
    }

    final view = ref.watch(officeEditorViewProvider);
    return view(
      Uri.parse(apiBaseUrl).resolve(session.editorPath),
      session,
      onLoading: (loading) {
        if (mounted && loading != _pageLoading) {
          setState(() => _pageLoading = loading);
        }
      },
      onFailed: () {
        if (mounted) {
          setState(() => _error = l10n.officeEditorPageFailed);
        }
      },
    );
  }
}

/// The editor page in a web view. Collabora takes its token as a form post,
/// so it is never in the page's address or the server's request log.
class _OfficeWebView extends StatefulWidget {
  const _OfficeWebView({
    required this.page,
    required this.session,
    required this.onLoading,
    required this.onFailed,
  });

  final Uri page;
  final OfficeSession session;
  final ValueChanged<bool> onLoading;
  final VoidCallback onFailed;

  @override
  State<_OfficeWebView> createState() => _OfficeWebViewState();
}

class _OfficeWebViewState extends State<_OfficeWebView> {
  late final WebViewController _controller;

  @override
  void initState() {
    super.initState();

    final form =
        'access_token=${Uri.encodeQueryComponent(widget.session.accessToken)}'
        '&access_token_ttl=${widget.session.accessTokenTtl}';

    _controller = WebViewController()
      ..setJavaScriptMode(JavaScriptMode.unrestricted)
      ..setNavigationDelegate(
        NavigationDelegate(
          onPageStarted: (_) => widget.onLoading(true),
          onPageFinished: (_) => widget.onLoading(false),
          onWebResourceError: (error) {
            // Only the page itself: a picture or a font that did not load
            // leaves a working editor.
            if (error.isForMainFrame ?? false) {
              widget.onFailed();
            }
          },
          // The editor stays on its own page; a link in a document is not
          // followed inside the app.
          onNavigationRequest: (request) =>
              Uri.tryParse(request.url)?.origin == widget.page.origin
              ? NavigationDecision.navigate
              : NavigationDecision.prevent,
        ),
      )
      ..loadRequest(
        widget.page,
        method: LoadRequestMethod.post,
        headers: const {'Content-Type': 'application/x-www-form-urlencoded'},
        body: Uint8List.fromList(utf8.encode(form)),
      );
  }

  @override
  Widget build(BuildContext context) => WebViewWidget(controller: _controller);
}
