import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/router/app_router.dart';
import '../../l10n/app_localizations.dart';
import 'auth_validators.dart';

/// Registration (plan section 8.2).
///
/// The privacy policy and the terms are accepted here, as plan section 2.8
/// requires: the button stays disabled until both are ticked, so an account
/// cannot exist without a recorded consent.
class RegisterScreen extends ConsumerStatefulWidget {
  const RegisterScreen({super.key});

  @override
  ConsumerState<RegisterScreen> createState() => _RegisterScreenState();
}

class _RegisterScreenState extends ConsumerState<RegisterScreen> {
  /// The version of the seeded drafts. Replaced by the version the server
  /// reports once the legal documents are fetched in step 5.
  static const _documentVersion = '0.1-draft';

  final _formKey = GlobalKey<FormState>();
  final _email = TextEditingController();
  final _password = TextEditingController();
  final _fullName = TextEditingController();
  final _referralCode = TextEditingController();

  bool _obscurePassword = true;
  bool _acceptedPrivacy = false;
  bool _acceptedTerms = false;

  bool get _canSubmit => _acceptedPrivacy && _acceptedTerms;

  @override
  void dispose() {
    _email.dispose();
    _password.dispose();
    _fullName.dispose();
    _referralCode.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!_canSubmit || !(_formKey.currentState?.validate() ?? false)) {
      return;
    }

    await ref
        .read(authControllerProvider.notifier)
        .register(
          email: _email.text,
          password: _password.text,
          fullName: _fullName.text.trim().isEmpty ? null : _fullName.text,
          referralCode: _referralCode.text.trim().isEmpty
              ? null
              : _referralCode.text,
          consents: const [
            PendingConsent(
              docType: LegalDocumentType.privacy,
              version: _documentVersion,
            ),
            PendingConsent(
              docType: LegalDocumentType.terms,
              version: _documentVersion,
            ),
          ],
        );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final state = ref.watch(authControllerProvider);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.authSignUp)),
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
                      controller: _email,
                      keyboardType: TextInputType.emailAddress,
                      autocorrect: false,
                      textInputAction: TextInputAction.next,
                      decoration: InputDecoration(
                        labelText: l10n.authEmail,
                        prefixIcon: const Icon(Icons.mail_outline),
                      ),
                      validator: (value) => AuthValidators.email(value, l10n),
                    ),
                    const SizedBox(height: 16),

                    TextFormField(
                      controller: _password,
                      obscureText: _obscurePassword,
                      textInputAction: TextInputAction.next,
                      decoration: InputDecoration(
                        labelText: l10n.authPassword,
                        prefixIcon: const Icon(Icons.lock_outline),
                        suffixIcon: IconButton(
                          icon: Icon(
                            _obscurePassword
                                ? Icons.visibility_outlined
                                : Icons.visibility_off_outlined,
                          ),
                          onPressed: () => setState(
                            () => _obscurePassword = !_obscurePassword,
                          ),
                        ),
                      ),
                      validator: (value) =>
                          AuthValidators.password(value, l10n),
                    ),
                    const SizedBox(height: 16),

                    TextFormField(
                      controller: _fullName,
                      textCapitalization: TextCapitalization.words,
                      textInputAction: TextInputAction.next,
                      decoration: InputDecoration(
                        labelText: l10n.authFullName,
                        prefixIcon: const Icon(Icons.person_outline),
                      ),
                    ),
                    const SizedBox(height: 16),

                    TextFormField(
                      controller: _referralCode,
                      textCapitalization: TextCapitalization.characters,
                      textInputAction: TextInputAction.done,
                      decoration: InputDecoration(
                        labelText: l10n.authReferralCode,
                        prefixIcon: const Icon(Icons.card_giftcard_outlined),
                      ),
                    ),
                    const SizedBox(height: 24),

                    // Plan section 2.8: both documents are accepted at
                    // registration, so the button cannot be reached without
                    // them.
                    CheckboxListTile(
                      value: _acceptedPrivacy,
                      onChanged: (value) =>
                          setState(() => _acceptedPrivacy = value ?? false),
                      title: Text(l10n.consentsAcceptPrivacy),
                      controlAffinity: ListTileControlAffinity.leading,
                      contentPadding: EdgeInsets.zero,
                    ),
                    CheckboxListTile(
                      value: _acceptedTerms,
                      onChanged: (value) =>
                          setState(() => _acceptedTerms = value ?? false),
                      title: Text(l10n.consentsAcceptTerms),
                      subtitle: Text(
                        // Plan section 2.14: the wallet is not refundable, and
                        // that is said before the account exists, not after.
                        l10n.consentsWalletWarning,
                        style: theme.textTheme.bodySmall,
                      ),
                      controlAffinity: ListTileControlAffinity.leading,
                      contentPadding: EdgeInsets.zero,
                    ),
                    const SizedBox(height: 16),

                    FilledButton(
                      onPressed: (state.isBusy || !_canSubmit) ? null : _submit,
                      child: state.isBusy
                          ? const SizedBox.square(
                              dimension: 20,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : Text(l10n.authSignUp),
                    ),
                    const SizedBox(height: 8),

                    Row(
                      mainAxisAlignment: MainAxisAlignment.center,
                      children: [
                        Text(l10n.authHaveAccount),
                        TextButton(
                          onPressed: state.isBusy
                              ? null
                              : () {
                                  ref
                                      .read(authControllerProvider.notifier)
                                      .clearError();
                                  context.go(Routes.signIn);
                                },
                          child: Text(l10n.authSignIn),
                        ),
                      ],
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
