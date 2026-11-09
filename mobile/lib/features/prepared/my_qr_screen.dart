import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_client.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_empty_state.dart';
import 'prepared_models.dart';
import 'prepared_repository.dart';

/// "Mənim QR kodum" (plan sections 2.9, 9.4; docs/DESIGN_SYSTEM.md
/// "Mobil: şəxsi QR"): the code the kiosk's scanner reads, drawn by the
/// backend and fetched again before it stops being good.
class MyQrScreen extends ConsumerStatefulWidget {
  const MyQrScreen({super.key});

  @override
  ConsumerState<MyQrScreen> createState() => _MyQrScreenState();
}

class _MyQrScreenState extends ConsumerState<MyQrScreen> {
  /// A new code this long before the old one runs out.
  static const _ahead = Duration(seconds: 5);

  /// Never sooner than this, whatever the clock says.
  static const _shortest = Duration(seconds: 5);

  MyQr? _qr;
  PrintCode? _code;
  Object? _error;
  Timer? _next;

  Future<void> _loadCode() async {
    try {
      final code = await ref.read(preparedRepositoryProvider).printCode();
      if (mounted) {
        setState(() => _code = code);
      }
    } on Object {
      if (mounted) {
        setState(() => _code = null);
      }
    }
  }

  @override
  void initState() {
    super.initState();
    unawaited(_load());
  }

  @override
  void dispose() {
    _next?.cancel();
    super.dispose();
  }

  Future<void> _load() async {
    _next?.cancel();

    try {
      final qr = await ref.read(preparedRepositoryProvider).qr();

      if (!mounted) {
        return;
      }

      setState(() {
        _qr = qr;
        _error = null;
      });

      // The code under the QR (T-345), fetched with each new QR: the same
      // one while it lasts, a new one once a kiosk used it. Without it the
      // QR still works, so a failure here only hides the code.
      unawaited(_loadCode());

      final wait = qr.expiresAt.difference(DateTime.now()) - _ahead;
      _next = Timer(
        wait < _shortest ? _shortest : wait,
        () => unawaited(_load()),
      );
    } on Object catch (error) {
      if (mounted) {
        // An old code would be refused at the kiosk: it goes.
        setState(() {
          _qr = null;
          _error = error;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final qr = _qr;
    final error = _error;

    return Scaffold(
      appBar: AppBar(title: Text(l10n.myQrTitle)),
      body: error != null
          ? NeoEmptyState(
              icon: Icons.qr_code_2,
              message: l10n.myQrFailed(
                error is ApiException
                    ? apiErrorText(l10n, error)
                    : l10n.errorServer,
              ),
              action: OutlinedButton(
                onPressed: () {
                  setState(() => _error = null);
                  unawaited(_load());
                },
                child: Text(l10n.commonRetry),
              ),
            )
          : qr == null
          ? const Center(child: CircularProgressIndicator())
          : LayoutBuilder(
              builder: (context, constraints) {
                final side = constraints.maxWidth * 0.7;

                return ListView(
                  padding: const EdgeInsets.all(Spacing.x6),
                  children: [
                    Center(
                      child: Container(
                        padding: const EdgeInsets.all(Spacing.x4),
                        decoration: BoxDecoration(
                          color: Functional.qrBackground,
                          borderRadius: BorderRadius.circular(Radii.md),
                        ),
                        child: Image.memory(
                          qr.png,
                          width: side,
                          height: side,
                          gaplessPlayback: true,
                          filterQuality: FilterQuality.none,
                          semanticLabel: l10n.myQrTitle,
                        ),
                      ),
                    ),
                    const SizedBox(height: Spacing.x6),
                    Text(
                      l10n.myQrHint,
                      style: theme.textTheme.titleMedium,
                      textAlign: TextAlign.center,
                    ),
                    const SizedBox(height: Spacing.x2),
                    Text(
                      l10n.myQrRotates(qr.rotationSeconds),
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: context.tokens.neutral600,
                      ),
                      textAlign: TextAlign.center,
                    ),
                    // When the kiosk's scanner cannot read the phone: the
                    // same thing as six digits (T-345).
                    if (_code case final code?) ...[
                      const SizedBox(height: Spacing.x6),
                      Card(
                        margin: EdgeInsets.zero,
                        color: context.tokens.brand50,
                        child: Padding(
                          padding: const EdgeInsets.all(Spacing.x4),
                          child: Column(
                            children: [
                              Text(
                                l10n.myQrCodeTitle,
                                style: theme.textTheme.bodySmall?.copyWith(
                                  color: context.tokens.neutral600,
                                ),
                              ),
                              const SizedBox(height: Spacing.x1),
                              SelectableText(
                                code.grouped,
                                semanticsLabel: code.code.split('').join(' '),
                                style: theme.textTheme.headlineMedium?.copyWith(
                                  fontWeight: FontWeights.semibold,
                                  color: context.tokens.brand500,
                                  letterSpacing: Spacing.x1,
                                  fontFeatures: const [
                                    FontFeature.tabularFigures(),
                                  ],
                                ),
                              ),
                              const SizedBox(height: Spacing.x1),
                              Text(
                                l10n.myQrCodeHint,
                                textAlign: TextAlign.center,
                                style: theme.textTheme.bodySmall?.copyWith(
                                  color: context.tokens.neutral600,
                                ),
                              ),
                            ],
                          ),
                        ),
                      ),
                    ],
                  ],
                );
              },
            ),
    );
  }
}
