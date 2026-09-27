import '../../shared/api_error_text.dart';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/auth_scaffold.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_primary_button.dart';
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
    final state = ref.watch(authControllerProvider);

    return AuthScaffold(
      title: l10n.authSignUp,
      subtitle: l10n.authSignUpSubtitle,
      showBrand: false,
      appBar: AppBar(
        leading: BackButton(
          onPressed: () {
            ref.read(authControllerProvider.notifier).clearError();
            context.go(Routes.signIn);
          },
        ),
      ),
      content: Form(
        key: _formKey,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (state.error != null) ...[
              NeoBanner.error(message: apiErrorText(l10n, state.error!)),
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
              autofillHints: const [AutofillHints.newPassword],
              textInputAction: TextInputAction.next,
              decoration: InputDecoration(
                labelText: l10n.authPassword,
                // The rule is stated before it is broken, not after.
                helperText: l10n.authPasswordHint,
                suffixIcon: IconButton(
                  onPressed: () =>
                      setState(() => _obscurePassword = !_obscurePassword),
                  icon: Icon(
                    _obscurePassword
                        ? Icons.visibility_outlined
                        : Icons.visibility_off_outlined,
                    color: context.tokens.neutral600,
                  ),
                ),
              ),
              validator: (value) => AuthValidators.password(value, l10n),
            ),
            const SizedBox(height: Spacing.x4),

            TextFormField(
              controller: _fullName,
              textCapitalization: TextCapitalization.words,
              autofillHints: const [AutofillHints.name],
              textInputAction: TextInputAction.next,
              decoration: InputDecoration(
                labelText: l10n.authFullName,
                helperText: l10n.authOptional,
              ),
            ),
            const SizedBox(height: Spacing.x4),

            TextFormField(
              controller: _referralCode,
              textCapitalization: TextCapitalization.characters,
              textInputAction: TextInputAction.done,
              decoration: InputDecoration(labelText: l10n.authReferralCode),
            ),
            const SizedBox(height: Spacing.x6),

            // Plan section 2.8: both documents are accepted at registration,
            // so the button cannot be reached without them.
            _ConsentTile(
              value: _acceptedPrivacy,
              onChanged: (value) => setState(() => _acceptedPrivacy = value),
              title: l10n.consentsAcceptPrivacy,
            ),
            const SizedBox(height: Spacing.x2),
            _ConsentTile(
              value: _acceptedTerms,
              onChanged: (value) => setState(() => _acceptedTerms = value),
              title: l10n.consentsAcceptTerms,
              // Plan section 2.14: the wallet is not refundable, and that is
              // said before the account exists, not after.
              subtitle: l10n.consentsWalletWarning,
            ),
            const SizedBox(height: Spacing.x6),

            NeoPrimaryButton(
              label: l10n.authSignUp,
              busy: state.isBusy,
              onPressed: _canSubmit ? _submit : null,
            ),
          ],
        ),
      ),
      footer: _SignInPrompt(busy: state.isBusy),
    );
  }
}

/// One consent: a tick, what is being accepted, and why it matters.
///
/// The whole row is the target, not just the box: 48dp is the minimum and a
/// checkbox on its own is smaller than that
/// (docs/DESIGN_SYSTEM.md section 3).
class _ConsentTile extends StatelessWidget {
  const _ConsentTile({
    required this.value,
    required this.onChanged,
    required this.title,
    this.subtitle,
  });

  final bool value;
  final ValueChanged<bool> onChanged;
  final String title;
  final String? subtitle;

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;
    final text = Theme.of(context).textTheme;

    // One node for the box and its text, so a screen reader announces the
    // checkbox by what it agrees to.
    return MergeSemantics(
      child: InkWell(
        onTap: () => onChanged(!value),
        borderRadius: BorderRadius.circular(Radii.md),
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: Spacing.x2),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              SizedBox.square(
                dimension: 24,
                child: Checkbox(
                  value: value,
                  onChanged: (next) => onChanged(next ?? false),
                ),
              ),
              const SizedBox(width: Spacing.x3),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(title, style: text.bodyLarge),
                    if (subtitle != null) ...[
                      const SizedBox(height: Spacing.x1),
                      Text(
                        subtitle!,
                        style: text.bodySmall?.copyWith(
                          color: tokens.neutral600,
                        ),
                      ),
                    ],
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _SignInPrompt extends ConsumerWidget {
  const _SignInPrompt({required this.busy});

  final bool busy;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);

    return Row(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        Flexible(
          child: Text(
            l10n.authHaveAccount,
            style: Theme.of(context).textTheme.bodyMedium,
          ),
        ),
        TextButton(
          onPressed: busy
              ? null
              : () {
                  ref.read(authControllerProvider.notifier).clearError();
                  context.go(Routes.signIn);
                },
          child: Text(l10n.authSignIn),
        ),
      ],
    );
  }
}
