import 'dart:async';
import 'dart:convert';

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

/// What the editor page tells the app, and what the app asks of it (T-465):
/// Collabora's PostMessage API, carried over a JavaScript channel.
class OfficeEditorLink {
  /// Whether the document has changes not saved yet; null until the editor
  /// has said - an editor that never does keeps the screen as it was.
  final modified = ValueNotifier<bool?>(null);

  /// Each answer to a save: true when it was saved.
  final saves = StreamController<bool>.broadcast();

  /// Set by the page: asks the editor to save now.
  Future<void> Function()? save;

  void dispose() {
    modified.dispose();
    unawaited(saves.close());
  }
}

/// Draws the editor page: Collabora Online, opened with [session]'s token.
///
/// [onLoading] reports whether the page is still loading, [onFailed] that it
/// could not be loaded at all; [link] carries the document's status both
/// ways. A provider, so a test puts a plain widget in its place - a web view
/// needs the platform.
typedef OfficeEditorView = Widget Function(
  Uri page,
  OfficeSession session, {
  required ValueChanged<bool> onLoading,
  required VoidCallback onFailed,
  required OfficeEditorLink link,
});

final officeEditorViewProvider = Provider<OfficeEditorView>((ref) => _webView);

Widget _webView(
  Uri page,
  OfficeSession session, {
  required ValueChanged<bool> onLoading,
  required VoidCallback onFailed,
  required OfficeEditorLink link,
}) => _OfficeWebView(
  page: page,
  session: session,
  onLoading: onLoading,
  onFailed: onFailed,
  link: link,
);

/// "Redaktə et" for a Word, Excel or PowerPoint file (plan section 2.2,
/// Addım 9; T-459; docs/DESIGN_SYSTEM.md, "Mobil və kiosk: Office
/// redaktoru"): Collabora Online in a web view. A save there - the save
/// icon, its own "Save", or leaving with changes - is a new version in the
/// file's own format, converted again for printing; the original is never
/// changed.
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
  bool _saving = false;
  final _link = OfficeEditorLink();
  StreamSubscription<bool>? _saves;
  Timer? _saveTimeout;

  @override
  void initState() {
    super.initState();
    _link.modified.addListener(_changed);
    _saves = _link.saves.stream.listen(_saved);
    _open();
  }

  @override
  void dispose() {
    _saveTimeout?.cancel();
    unawaited(_saves?.cancel());
    _link.modified.removeListener(_changed);
    _link.dispose();
    super.dispose();
  }

  void _changed() {
    if (mounted) {
      setState(() {});
    }
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

  /// The save icon: the editor saves a new version, converted again.
  Future<void> _save() async {
    final save = _link.save;
    if (save == null || _saving) {
      return;
    }

    setState(() => _saving = true);
    // An editor that never answers does not leave the icon spinning.
    _saveTimeout = Timer(const Duration(seconds: 30), () => _saved(false));
    await save();
  }

  void _saved(bool ok) {
    _saveTimeout?.cancel();
    if (!mounted || !_saving) {
      return;
    }

    final l10n = AppLocalizations.of(context);
    setState(() => _saving = false);
    if (ok) {
      _link.modified.value = false;
      unawaited(ref.read(filesControllerProvider.notifier).load());
    }
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(
          ok ? l10n.officeEditorSaved : l10n.officeEditorSaveFailed,
        ),
      ),
    );
  }

  /// Leaving with changes asks first: the editor saves them on the way out.
  /// With nothing changed - as the editor said - it simply closes.
  Future<void> _leave() async {
    final l10n = AppLocalizations.of(context);

    if (_session == null || _link.modified.value == false) {
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
          // X while nothing has changed, the save icon once something has
          // (T-465). An editor that does not say keeps the X.
          leading: _saving
              ? const Padding(
                  padding: EdgeInsets.all(Spacing.x4),
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : (_link.modified.value ?? false)
              ? IconButton(
                  key: const ValueKey('officeEditorSave'),
                  tooltip: l10n.officeEditorSave,
                  icon: Icon(Icons.save_outlined, color: tokens.brand500),
                  onPressed: _save,
                )
              : IconButton(
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
      link: _link,
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
    required this.link,
  });

  final Uri page;
  final OfficeSession session;
  final ValueChanged<bool> onLoading;
  final VoidCallback onFailed;
  final OfficeEditorLink link;

  @override
  State<_OfficeWebView> createState() => _OfficeWebViewState();
}

class _OfficeWebViewState extends State<_OfficeWebView> {
  late final WebViewController _controller;

  @override
  void initState() {
    super.initState();

    widget.link.save = () => _controller.runJavaScript(officeSaveScript);

    _controller = WebViewController()
      ..setJavaScriptMode(JavaScriptMode.unrestricted)
      ..addJavaScriptChannel(
        officeEditorChannel,
        onMessageReceived: (message) =>
            officeEditorMessage(widget.link, message.message),
      )
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
      // Collabora talks to its host only from inside a frame (Map.WOPI.js:
      // window.parent !== window.self); opened on its own it said nothing,
      // and the app never learnt the document had changed (T-472). So the
      // app's own page holds it, under the server's origin - the one
      // CheckFileInfo names as PostMessageOrigin.
      ..loadHtmlString(
        officeHostPage(widget.page, widget.session),
        baseUrl: widget.page.origin,
      );
  }

  @override
  void dispose() {
    widget.link.save = null;
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => WebViewWidget(controller: _controller);
}

/// The JavaScript channel the editor page talks to the app through.
const officeEditorChannel = 'NeoPrintEditor';

/// One message from the page: `{"modified": bool}` or `{"saved": bool}`.
/// Anything else is ignored.
void officeEditorMessage(OfficeEditorLink link, String message) {
  final Object? value;
  try {
    value = jsonDecode(message);
  } on FormatException {
    return;
  }
  if (value is! Map<String, dynamic>) {
    return;
  }
  if (value['modified'] case final bool modified) {
    link.modified.value = modified;
  }
  if (value['saved'] case final bool saved) {
    link.saves.add(saved);
  }
}

/// The app's page that holds the editor (T-472).
///
/// Opened under the server's origin, it posts the token to Collabora's page
/// into a frame - a form, so the token is in no URL - and passes on what
/// Collabora tells its host: the document's status and a save's answer.
/// Collabora posts to its parent for the origin CheckFileInfo named
/// (`PostMessageOrigin`), and starts once the host has said it listens
/// (`Host_PostmessageReady`). Messages from anything but that frame, or from
/// another origin, are ignored.
String officeHostPage(Uri page, OfficeSession session) {
  final action = _html(page.toString());
  final token = _html(session.accessToken);
  final ttl = _html('${session.accessTokenTtl}');

  return '''
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
<style>
  html, body, iframe { margin: 0; padding: 0; border: 0; width: 100%; height: 100%; overflow: hidden; }
  form { display: none; }
</style>
</head>
<body>
<form id="open" action="$action" method="post" target="editor">
  <input type="hidden" name="access_token" value="$token">
  <input type="hidden" name="access_token_ttl" value="$ttl">
</form>
<iframe id="editor" name="editor" allow="clipboard-read; clipboard-write; fullscreen"></iframe>
<script>
(function () {
  var origin = window.location.origin;
  var frame = document.getElementById('editor');
  function send(message) {
    if (frame.contentWindow) {
      frame.contentWindow.postMessage(JSON.stringify(message), origin);
    }
  }
  window.__neoprintSave = function () {
    send({
      MessageId: 'Action_Save',
      Values: { DontTerminateEdit: true, DontSaveIfUnmodified: true, Notify: true }
    });
  };
  window.addEventListener('message', function (event) {
    if (event.origin !== origin || event.source !== frame.contentWindow) { return; }
    var message;
    try {
      message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
    } catch (e) { return; }
    if (!message || !message.MessageId) { return; }
    var values = message.Values || {};
    if (message.MessageId === 'App_LoadingStatus' && values.Status === 'Frame_Ready') {
      send({ MessageId: 'Host_PostmessageReady' });
    } else if (message.MessageId === 'Doc_ModifiedStatus') {
      $officeEditorChannel.postMessage(JSON.stringify({ modified: values.Modified === true }));
    } else if (message.MessageId === 'Action_Save_Resp') {
      $officeEditorChannel.postMessage(JSON.stringify({ saved: values.success === true }));
    }
  });
  document.getElementById('open').submit();
})();
</script>
</body>
</html>
''';
}

/// "Save" as Collabora's own button does it, through the host page; the
/// answer comes back as `Action_Save_Resp`.
const officeSaveScript = 'window.__neoprintSave && window.__neoprintSave();';

String _html(String value) => value
    .replaceAll('&', '&amp;')
    .replaceAll('"', '&quot;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll("'", '&#39;');
