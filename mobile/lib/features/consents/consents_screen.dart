import '../../shared/api_error_text.dart';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/auth_scaffold.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_primary_button.dart';

/// Re-accepting a document after a new version is published
/// (plan sections 2.8, 2.13).
///
/// The router keeps the app here until every outstanding version is accepted,
/// so a new version of the terms genuinely reaches the user rather than being
/// a banner they can dismiss.
class ConsentsScreen extends ConsumerStatefulWidget {
  const ConsentsScreen({super.key});

  @override
  ConsumerState<ConsentsScreen> createState() => _ConsentsScreenState();
}

class _ConsentsScreenState extends ConsumerState<ConsentsScreen> {
  final Set<LegalDocumentType> _accepted = {};

  bool get _allAccepted {
    final pending =
        ref.read(authControllerProvider).user?.pendingConsents ?? [];

    return pending.every((consent) => _accepted.contains(consent.docType));
  }

  Future<void> _submit() async {
    final pending =
        ref.read(authControllerProvider).user?.pendingConsents ?? [];

    // Sends the versions the user was actually shown, not the latest the
    // server holds: the consent record has to match what they read.
    await ref.read(authControllerProvider.notifier).acceptConsents(pending);
  }

  String _titleFor(LegalDocumentType docType, AppLocalizations l10n) =>
      switch (docType) {
        LegalDocumentType.privacy => l10n.consentsAcceptPrivacy,
        LegalDocumentType.terms => l10n.consentsAcceptTerms,
        LegalDocumentType.scanNotice => l10n.consentsTitle,
      };

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final state = ref.watch(authControllerProvider);
    final pending = state.user?.pendingConsents ?? const <PendingConsent>[];

    return AuthScaffold(
      title: l10n.consentsTitle,
      subtitle: l10n.consentsBody,
      showBrand: false,
      content: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (state.error != null) ...[
            NeoBanner.error(message: apiErrorText(l10n, state.error!)),
            const SizedBox(height: Spacing.x4),
          ],

          for (final consent in pending) ...[
            _ConsentRow(
              title: _titleFor(consent.docType, l10n),
              version: consent.version,
              // Plan section 2.14: the wallet is not refundable.
              note: consent.docType == LegalDocumentType.terms
                  ? l10n.consentsWalletWarning
                  : null,
              value: _accepted.contains(consent.docType),
              onChanged: (value) => setState(() {
                if (value) {
                  _accepted.add(consent.docType);
                } else {
                  _accepted.remove(consent.docType);
                }
              }),
            ),
            const SizedBox(height: Spacing.x3),
          ],

          const SizedBox(height: Spacing.x3),

          NeoPrimaryButton(
            label: l10n.consentsContinue,
            busy: state.isBusy,
            onPressed: _allAccepted ? _submit : null,
          ),
        ],
      ),
      footer: Center(
        child: TextButton(
          onPressed: () => ref.read(authControllerProvider.notifier).signOut(),
          child: Text(l10n.profileSignOut),
        ),
      ),
    );
  }
}

/// One document waiting to be accepted.
///
/// The version is shown so it is clear this is a new one rather than a repeat
/// prompt, and the whole row is the touch target.
class _ConsentRow extends StatelessWidget {
  const _ConsentRow({
    required this.title,
    required this.version,
    required this.value,
    required this.onChanged,
    this.note,
  });

  final String title;
  final String version;
  final String? note;
  final bool value;
  final ValueChanged<bool> onChanged;

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
        child: Container(
          padding: const EdgeInsets.all(Spacing.x3),
          decoration: BoxDecoration(
            color: value ? tokens.brand50 : tokens.neutral50,
            border: Border.all(
              color: value ? tokens.brand500 : tokens.neutral200,
            ),
            borderRadius: BorderRadius.circular(Radii.md),
          ),
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
                    const SizedBox(height: Spacing.x1),
                    Text(
                      'v$version',
                      style: text.bodySmall?.copyWith(color: tokens.neutral600),
                    ),
                    if (note != null) ...[
                      const SizedBox(height: Spacing.x2),
                      Row(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Icon(
                            Icons.info_outline,
                            size: 16,
                            color: tokens.neutral600,
                          ),
                          const SizedBox(width: Spacing.x2),
                          Expanded(
                            child: Text(
                              note!,
                              style: text.bodySmall?.copyWith(
                                color: tokens.neutral600,
                              ),
                            ),
                          ),
                        ],
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
