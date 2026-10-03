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
  Object? _error;
  Timer? _next;

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
                  ],
                );
              },
            ),
    );
  }
}
