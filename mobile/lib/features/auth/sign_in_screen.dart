import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/auth_scaffold.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_primary_button.dart';
import 'auth_validators.dart';

/// Sign-in (plan section 8.2).
class SignInScreen extends ConsumerStatefulWidget {
  const SignInScreen({super.key});

  @override
  ConsumerState<SignInScreen> createState() => _SignInScreenState();
}

class _SignInScreenState extends ConsumerState<SignInScreen> {
  final _formKey = GlobalKey<FormState>();
  final _email = TextEditingController();
  final _password = TextEditingController();

  bool _obscurePassword = true;

  @override
  void dispose() {
    _email.dispose();
    _password.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) {
      return;
    }

    await ref
        .read(authControllerProvider.notifier)
        .signIn(email: _email.text, password: _password.text);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final state = ref.watch(authControllerProvider);

    return AuthScaffold(
      title: l10n.authSignIn,
      subtitle: l10n.authSignInSubtitle,
      content: Form(
        key: _formKey,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (state.errorMessage != null) ...[
              NeoBanner.error(message: state.errorMessage!),
              const SizedBox(height: Spacing.x4),
            ],

            TextFormField(
              controller: _email,
              keyboardType: TextInputType.emailAddress,
              autocorrect: false,
              autofillHints: const [AutofillHints.email],
              textInputAction: TextInputAction.next,
              decoration: InputDecoration(labelText: l10n.authEmail),
              validator: (value) => AuthValidators.email(value, l10n),
            ),
            const SizedBox(height: Spacing.x4),

            TextFormField(
              controller: _password,
              obscureText: _obscurePassword,
              autofillHints: const [AutofillHints.password],
              textInputAction: TextInputAction.done,
              onFieldSubmitted: (_) => _submit(),
              decoration: InputDecoration(
                labelText: l10n.authPassword,
                suffixIcon: _RevealButton(
                  obscured: _obscurePassword,
                  onPressed: () =>
                      setState(() => _obscurePassword = !_obscurePassword),
                ),
              ),
              // Only presence is checked here: the length rule belongs on
              // registration, and applying it at sign-in would lock out anyone
              // whose password predates it.
              validator: (value) => AuthValidators.passwordPresent(value, l10n),
            ),
            const SizedBox(height: Spacing.x6),

            NeoPrimaryButton(
              label: l10n.authSignIn,
              busy: state.isBusy,
              onPressed: _submit,
            ),
          ],
        ),
      ),
      footer: _SignUpPrompt(busy: state.isBusy),
    );
  }
}

/// Shows or hides the password. Labelled, because an eye on its own says
/// nothing to a screen reader.
class _RevealButton extends StatelessWidget {
  const _RevealButton({required this.obscured, required this.onPressed});

  final bool obscured;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return IconButton(
      onPressed: onPressed,
      tooltip: obscured ? l10n.authPassword : l10n.authPassword,
      icon: Icon(
        obscured ? Icons.visibility_outlined : Icons.visibility_off_outlined,
        color: context.tokens.neutral600,
      ),
    );
  }
}

class _SignUpPrompt extends ConsumerWidget {
  const _SignUpPrompt({required this.busy});

  final bool busy;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);

    return Row(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        Flexible(
          child: Text(
            l10n.authNoAccount,
            style: Theme.of(context).textTheme.bodyMedium,
          ),
        ),
        TextButton(
          onPressed: busy
              ? null
              : () {
                  ref.read(authControllerProvider.notifier).clearError();
                  context.go(Routes.register);
                },
          child: Text(l10n.authSignUp),
        ),
      ],
    );
  }
}
