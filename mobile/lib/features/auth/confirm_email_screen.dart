import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/auth_scaffold.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_primary_button.dart';
import 'auth_validators.dart';

/// Email confirmation (plan sections 2.8, 8.2).
///
/// The six-digit code is the gate: registering does not sign anyone in, and
/// the router keeps the app here until the code is accepted.
class ConfirmEmailScreen extends ConsumerStatefulWidget {
  const ConfirmEmailScreen({super.key});

  @override
  ConsumerState<ConfirmEmailScreen> createState() => _ConfirmEmailScreenState();
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
    final tokens = context.tokens;
    final state = ref.watch(authControllerProvider);

    return AuthScaffold(
      title: l10n.confirmEmailTitle,
      subtitle: l10n.confirmEmailBody(state.pendingEmail ?? ''),
      showBrand: false,
      content: Form(
        key: _formKey,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Center(
              child: Container(
                width: 64,
                height: 64,
                decoration: BoxDecoration(
                  color: tokens.brand50,
                  shape: BoxShape.circle,
                ),
                child: Icon(
                  Icons.mark_email_unread_outlined,
                  size: 30,
                  color: tokens.brand500,
                ),
              ),
            ),
            const SizedBox(height: Spacing.x6),

            if (state.errorMessage != null) ...[
              NeoBanner.error(message: state.errorMessage!),
              const SizedBox(height: Spacing.x4),
            ],

            TextFormField(
              controller: _code,
              keyboardType: TextInputType.number,
              textAlign: TextAlign.center,
              autofocus: true,
              maxLength: AuthValidators.confirmationCodeLength,
              // Digits only: a numeric keyboard still offers other characters
              // on some devices.
              inputFormatters: [FilteringTextInputFormatter.digitsOnly],
              style: TextStyle(
                fontSize: FontSizes.display,
                fontWeight: FontWeights.semibold,
                letterSpacing: 10,
                color: tokens.neutral900,
              ),
              decoration: InputDecoration(
                labelText: l10n.confirmEmailCode,
                counterText: '',
              ),
              onFieldSubmitted: (_) => _submit(),
              validator: (value) =>
                  AuthValidators.confirmationCode(value, l10n),
            ),
            const SizedBox(height: Spacing.x6),

            NeoPrimaryButton(
              label: l10n.confirmEmailSubmit,
              busy: state.isBusy,
              onPressed: _submit,
            ),
            const SizedBox(height: Spacing.x3),

            OutlinedButton(
              onPressed: state.isBusy
                  ? null
                  : () => ref
                        .read(authControllerProvider.notifier)
                        .resendConfirmation(),
              child: Text(l10n.confirmEmailResend),
            ),
          ],
        ),
      ),
      footer: Center(
        child: TextButton(
          onPressed: () => ref.read(authControllerProvider.notifier).signOut(),
          child: Text(l10n.commonCancel),
        ),
      ),
    );
  }
}
