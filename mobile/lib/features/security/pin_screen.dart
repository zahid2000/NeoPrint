import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/security/app_lock.dart';
import '../../core/security/biometrics.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';

/// What the PIN screen is for (T-270).
enum PinMode {
  /// The first PIN after signing in: typed, then typed again.
  setup,

  /// The lock: the PIN, or the fingerprint or face.
  unlock,

  /// From the settings: the current PIN, then a new one twice.
  change,
}

/// The app's PIN screen (docs/DESIGN_SYSTEM.md 5.2, "PIN ekranı"): four dots
/// and a keypad; the fourth digit is the "OK".
class PinScreen extends ConsumerStatefulWidget {
  const PinScreen({super.key, required this.mode});

  final PinMode mode;

  @override
  ConsumerState<PinScreen> createState() => _PinScreenState();
}

enum _Step { current, enter, confirm }

class _PinScreenState extends ConsumerState<PinScreen> {
  late _Step _step = widget.mode == PinMode.setup ? _Step.enter : _Step.current;

  String _typed = '';
  String? _first;
  String? _error;
  bool _busy = false;

  AppLockController get _lock => ref.read(appLockProvider.notifier);

  @override
  void initState() {
    super.initState();

    // The fingerprint or face is offered by itself once, as the lock opens.
    if (widget.mode == PinMode.unlock) {
      WidgetsBinding.instance.addPostFrameCallback((_) => _biometrics());
    }
  }

  Future<void> _biometrics() async {
    if (!mounted || !ref.read(appLockProvider).biometrics) {
      return;
    }

    final l10n = AppLocalizations.of(context);
    await _lock.unlockWithBiometrics(
      title: l10n.pinBiometricPrompt,
      cancel: l10n.pinBiometricCancel,
    );
  }

  void _press(String digit) {
    if (_busy || _typed.length >= AppLockController.pinLength) {
      return;
    }

    setState(() {
      _typed += digit;
      if (_typed.length == 1) {
        _error = null;
      }
    });

    if (_typed.length == AppLockController.pinLength) {
      _complete();
    }
  }

  void _delete() {
    if (_busy || _typed.isEmpty) {
      return;
    }

    setState(() => _typed = _typed.substring(0, _typed.length - 1));
  }

  Future<void> _complete() async {
    final l10n = AppLocalizations.of(context);
    final pin = _typed;

    switch (_step) {
      case _Step.current:
        setState(() => _busy = true);
        final result = await _lock.unlock(pin);
        if (!mounted) {
          return;
        }

        setState(() {
          _busy = false;
          _typed = '';
          if (result.unlocked) {
            _error = null;
            // The lock is open: the router takes the app where it was. A
            // change goes on to the new PIN.
            if (widget.mode == PinMode.change) {
              _step = _Step.enter;
            }
          } else if (!result.signedOut) {
            _error = l10n.pinWrong(result.attemptsLeft);
          }
        });

      case _Step.enter:
        setState(() {
          _first = pin;
          _typed = '';
          _step = _Step.confirm;
        });

      case _Step.confirm:
        if (pin != _first) {
          setState(() {
            _typed = '';
            _first = null;
            _step = _Step.enter;
            _error = l10n.pinMismatch;
          });
          return;
        }

        setState(() => _busy = true);
        await _lock.setPin(pin);
        if (!mounted) {
          return;
        }

        if (widget.mode == PinMode.change) {
          ScaffoldMessenger.of(context)
              .showSnackBar(SnackBar(content: Text(l10n.pinChanged)));
          Navigator.of(context).pop();
          return;
        }

        await _offerBiometrics();
    }
  }

  /// After the first PIN: the fingerprint or face, where the phone has one.
  Future<void> _offerBiometrics() async {
    if (!await ref.read(biometricsProvider).available() || !mounted) {
      return;
    }

    final l10n = AppLocalizations.of(context);
    final yes = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(l10n.pinBiometricOfferTitle),
        content: Text(l10n.pinBiometricOfferBody),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: Text(l10n.pinBiometricOfferNo),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: Text(l10n.pinBiometricOfferYes),
          ),
        ],
      ),
    );

    if (yes ?? false) {
      await _lock.setBiometrics(true);
    }
  }

  Future<void> _forgot() async {
    final l10n = AppLocalizations.of(context);
    final sure = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(l10n.pinForgotTitle),
        content: Text(l10n.pinForgotBody),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: Text(l10n.commonCancel),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: Text(l10n.pinForgotConfirm),
          ),
        ],
      ),
    );

    if (sure ?? false) {
      await _lock.forgot();
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final email = ref.watch(currentUserProvider)?.email ?? '';
    final biometrics =
        widget.mode == PinMode.unlock &&
        ref.watch(appLockProvider.select((lock) => lock.biometrics));

    final (title, hint) = switch (_step) {
      _Step.current when widget.mode == PinMode.change => (
        l10n.pinChangeCurrentTitle,
        null,
      ),
      _Step.current => (l10n.pinUnlockTitle, l10n.pinUnlockHint(email)),
      _Step.enter when widget.mode == PinMode.change => (
        l10n.pinChangeNewTitle,
        null,
      ),
      _Step.enter => (l10n.pinSetupTitle, l10n.pinSetupHint),
      _Step.confirm => (l10n.pinConfirmTitle, l10n.pinConfirmHint),
    };

    return Scaffold(
      appBar: widget.mode == PinMode.change ? AppBar() : null,
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(Spacing.x6),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: Sizes.formMaxWidth),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(Icons.lock_outline, color: theme.colorScheme.primary),
                  const SizedBox(height: Spacing.x3),
                  Text(
                    title,
                    style: theme.textTheme.headlineSmall,
                    textAlign: TextAlign.center,
                  ),
                  if (hint != null) ...[
                    const SizedBox(height: Spacing.x2),
                    Text(
                      hint,
                      style: theme.textTheme.bodyMedium,
                      textAlign: TextAlign.center,
                    ),
                  ],
                  const SizedBox(height: Spacing.x6),
                  _Dots(filled: _typed.length),
                  const SizedBox(height: Spacing.x3),
                  _ErrorLine(text: _error),
                  const SizedBox(height: Spacing.x4),
                  _Keypad(
                    onDigit: _press,
                    onDelete: _delete,
                    onBiometrics: biometrics ? _biometrics : null,
                    deleteLabel: l10n.pinDelete,
                    biometricsLabel: l10n.pinBiometricButton,
                  ),
                  const SizedBox(height: Spacing.x4),
                  if (widget.mode == PinMode.unlock)
                    TextButton(onPressed: _forgot, child: Text(l10n.pinForgot)),
                  // A mandatory PIN does not trap anyone: they can leave.
                  if (widget.mode == PinMode.setup)
                    TextButton(
                      onPressed: () =>
                          ref.read(authControllerProvider.notifier).signOut(),
                      child: Text(l10n.pinSignOut),
                    ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _Dots extends StatelessWidget {
  const _Dots({required this.filled});

  final int filled;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Semantics(
      label: '$filled / ${AppLockController.pinLength}',
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          for (var i = 0; i < AppLockController.pinLength; i++)
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: Spacing.x2),
              child: Container(
                width: Spacing.x3,
                height: Spacing.x3,
                decoration: BoxDecoration(
                  shape: BoxShape.circle,
                  color: i < filled ? scheme.primary : null,
                  border: Border.all(
                    color: i < filled ? scheme.primary : scheme.outline,
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

/// What went wrong, in its own line under the dots; the same height when empty
/// so the keypad does not jump.
class _ErrorLine extends StatelessWidget {
  const _ErrorLine({required this.text});

  final String? text;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final text = this.text;

    return ConstrainedBox(
      constraints: const BoxConstraints(minHeight: Sizes.touchTarget),
      child: text == null
          ? const SizedBox.shrink()
          : Row(
              mainAxisAlignment: MainAxisAlignment.center,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Icon(Icons.error_outline, color: theme.colorScheme.error),
                const SizedBox(width: Spacing.x2),
                Flexible(
                  child: Text(
                    text,
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: theme.colorScheme.error,
                    ),
                  ),
                ),
              ],
            ),
    );
  }
}

class _Keypad extends StatelessWidget {
  const _Keypad({
    required this.onDigit,
    required this.onDelete,
    required this.onBiometrics,
    required this.deleteLabel,
    required this.biometricsLabel,
  });

  final void Function(String digit) onDigit;
  final VoidCallback onDelete;
  final VoidCallback? onBiometrics;
  final String deleteLabel;
  final String biometricsLabel;

  @override
  Widget build(BuildContext context) {
    Widget digit(String d) => _Key(
      label: d,
      onTap: () => onDigit(d),
      child: Text(d, style: Theme.of(context).textTheme.headlineSmall),
    );

    final biometrics = onBiometrics;

    return Column(
      children: [
        for (final row in const [
          ['1', '2', '3'],
          ['4', '5', '6'],
          ['7', '8', '9'],
        ])
          Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [for (final d in row) digit(d)],
          ),
        Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            if (biometrics != null)
              _Key(
                label: biometricsLabel,
                onTap: biometrics,
                child: const Icon(Icons.fingerprint),
              )
            else
              const SizedBox(
                width: Spacing.x16 + Spacing.x4,
                height: Spacing.x16,
              ),
            digit('0'),
            _Key(
              label: deleteLabel,
              onTap: onDelete,
              child: const Icon(Icons.backspace_outlined),
            ),
          ],
        ),
      ],
    );
  }
}

class _Key extends StatelessWidget {
  const _Key({required this.label, required this.onTap, required this.child});

  final String label;
  final VoidCallback onTap;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(
        horizontal: Spacing.x2,
        vertical: Spacing.x1,
      ),
      child: Semantics(
        button: true,
        label: label,
        excludeSemantics: true,
        child: InkWell(
          key: ValueKey('pin-key-$label'),
          customBorder: const CircleBorder(),
          onTap: onTap,
          child: SizedBox(
            width: Spacing.x16,
            height: Spacing.x16,
            child: Center(child: child),
          ),
        ),
      ),
    );
  }
}
