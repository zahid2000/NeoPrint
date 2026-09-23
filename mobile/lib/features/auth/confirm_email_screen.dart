import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../l10n/app_localizations.dart';
import 'auth_validators.dart';

/// Email confirmation (plan sections 2.8, 8.2).
///
/// The six-digit code is the gate: registering does not sign anyone in, and
/// the router keeps the app here until the code is accepted.
class ConfirmEmailScreen extends ConsumerStatefulWidget {
  const ConfirmEmailScreen({super.key});

  @override
  ConsumerState<ConfirmEmailScreen> createState() =>
      _ConfirmEmailScreenState();
}

class _ConfirmEmailScreenState extends ConsumerState<ConfirmEmailScreen> {
  final _formKey = GlobalKey<FormState>();
  final _code = TextEditingController();

  @override
  void dispose() {
    _code.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) {
      return;
    }

    await ref.read(authControllerProvider.notifier).confirmEmail(_code.text);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final state = ref.watch(authControllerProvider);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.confirmEmailTitle)),
      body: SafeArea(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: Center(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: Form(
                key: _formKey,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Icon(
                      Icons.mark_email_unread_outlined,
                      size: 64,
                      color: theme.colorScheme.primary,
                    ),
                    const SizedBox(height: 24),

                    Text(
                      l10n.confirmEmailBody(state.pendingEmail ?? ''),
                      textAlign: TextAlign.center,
                      style: theme.textTheme.bodyLarge,
                    ),
                    const SizedBox(height: 32),

                    if (state.errorMessage != null) ...[
                      Container(
                        padding: const EdgeInsets.all(12),
                        decoration: BoxDecoration(
                          color: theme.colorScheme.errorContainer,
                          borderRadius: BorderRadius.circular(12),
                        ),
                        child: Text(
                          state.errorMessage!,
                          style: TextStyle(
                            color: theme.colorScheme.onErrorContainer,
                          ),
                        ),
                      ),
                      const SizedBox(height: 16),
                    ],

                    TextFormField(
                      controller: _code,
                      keyboardType: TextInputType.number,
                      textAlign: TextAlign.center,
                      maxLength: AuthValidators.confirmationCodeLength,
                      // Digits only: a numeric keyboard still offers other
                      // characters on some devices.
                      inputFormatters: [FilteringTextInputFormatter.digitsOnly],
                      style: theme.textTheme.headlineMedium?.copyWith(
                        letterSpacing: 8,
                        fontWeight: FontWeight.bold,
                      ),
                      decoration: InputDecoration(
                        labelText: l10n.confirmEmailCode,
                        counterText: '',
                      ),
                      onFieldSubmitted: (_) => _submit(),
                      validator: (value) =>
                          AuthValidators.confirmationCode(value, l10n),
                    ),
                    const SizedBox(height: 24),

                    FilledButton(
                      onPressed: state.isBusy ? null : _submit,
                      child: state.isBusy
                          ? const SizedBox.square(
                              dimension: 20,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : Text(l10n.confirmEmailSubmit),
                    ),
                    const SizedBox(height: 8),

                    OutlinedButton(
                      onPressed: state.isBusy
                          ? null
                          : () => ref
                                .read(authControllerProvider.notifier)
                                .resendConfirmation(),
                      child: Text(l10n.confirmEmailResend),
                    ),
                    const SizedBox(height: 16),

                    TextButton(
                      onPressed: () =>
                          ref.read(authControllerProvider.notifier).signOut(),
                      child: Text(l10n.commonCancel),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
