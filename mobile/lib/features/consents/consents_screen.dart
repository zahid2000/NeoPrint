import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../l10n/app_localizations.dart';

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
    final theme = Theme.of(context);
    final state = ref.watch(authControllerProvider);
    final pending = state.user?.pendingConsents ?? const <PendingConsent>[];

    return Scaffold(
      appBar: AppBar(title: Text(l10n.consentsTitle)),
      body: SafeArea(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: Center(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 480),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Text(l10n.consentsBody, style: theme.textTheme.bodyLarge),
                  const SizedBox(height: 24),

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

                  for (final consent in pending)
                    Card(
                      margin: const EdgeInsets.only(bottom: 12),
                      child: Padding(
                        padding: const EdgeInsets.all(8),
                        child: Column(
                          children: [
                            CheckboxListTile(
                              value: _accepted.contains(consent.docType),
                              onChanged: (value) => setState(() {
                                if (value ?? false) {
                                  _accepted.add(consent.docType);
                                } else {
                                  _accepted.remove(consent.docType);
                                }
                              }),
                              title: Text(_titleFor(consent.docType, l10n)),
                              // The version is shown, so it is clear this is a
                              // new one rather than a repeat prompt.
                              subtitle: Text('v${consent.version}'),
                              controlAffinity: ListTileControlAffinity.leading,
                            ),
                            if (consent.docType == LegalDocumentType.terms)
                              Padding(
                                padding: const EdgeInsets.fromLTRB(
                                  16,
                                  0,
                                  16,
                                  12,
                                ),
                                child: Row(
                                  children: [
                                    Icon(
                                      Icons.info_outline,
                                      size: 18,
                                      color: theme.colorScheme.outline,
                                    ),
                                    const SizedBox(width: 8),
                                    Expanded(
                                      child: Text(
                                        // Plan section 2.14.
                                        l10n.consentsWalletWarning,
                                        style: theme.textTheme.bodySmall,
                                      ),
                                    ),
                                  ],
                                ),
                              ),
                          ],
                        ),
                      ),
                    ),

                  const SizedBox(height: 8),

                  FilledButton(
                    onPressed: (state.isBusy || !_allAccepted) ? null : _submit,
                    child: state.isBusy
                        ? const SizedBox.square(
                            dimension: 20,
                            child: CircularProgressIndicator(strokeWidth: 2),
                          )
                        : Text(l10n.consentsContinue),
                  ),
                  const SizedBox(height: 8),

                  TextButton(
                    onPressed: () =>
                        ref.read(authControllerProvider.notifier).signOut(),
                    child: Text(l10n.profileSignOut),
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
