import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_empty_state.dart';
import 'account_deletion.dart';

/// "Hesabımı sil" (plan section 2.13; docs/DESIGN_SYSTEM.md, "Mobil:
/// məxfilik"): what is lost, what stands in the way, then the password and
/// the confirmation word.
class AccountDeletionScreen extends ConsumerStatefulWidget {
  const AccountDeletionScreen({super.key});

  @override
  ConsumerState<AccountDeletionScreen> createState() =>
      _AccountDeletionScreenState();
}

class _AccountDeletionScreenState extends ConsumerState<AccountDeletionScreen> {
  final _password = TextEditingController();
  final _confirmation = TextEditingController();
  bool _busy = false;
  bool _obscure = true;
  ApiException? _error;

  @override
  void initState() {
    super.initState();
    _password.addListener(_changed);
    _confirmation.addListener(_changed);
  }

  @override
  void dispose() {
    _password.dispose();
    _confirmation.dispose();
    super.dispose();
  }

  void _changed() => setState(() {});

  bool _ready(AppLocalizations l10n) =>
      _password.text.isNotEmpty &&
      _word(_confirmation.text) == _word(l10n.deleteConfirmWord);

  /// "sil", "Sil" and "SİL" are the same word: Dart upper-cases "i" to "I",
  /// not to the Azerbaijani "İ".
  static String _word(String text) =>
      text.trim().toUpperCase().replaceAll('İ', 'I');

  Future<void> _delete() async {
    final l10n = AppLocalizations.of(context);
    final messenger = ScaffoldMessenger.of(context);
    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      await ref
          .read(accountDeletionRepositoryProvider)
          .delete(
            password: _password.text,
            confirmation: _confirmation.text.trim(),
          );
    } on ApiException catch (error) {
      if (mounted) {
        setState(() {
          _busy = false;
          _error = error;
        });
        // Something may have started meanwhile: show it.
        ref.invalidate(deletionCheckProvider);
      }
      return;
    }

    // Above the navigator, so it is still there on the sign-in screen.
    messenger.showSnackBar(SnackBar(content: Text(l10n.deleteDone)));
    await ref.read(accountSignOutProvider)();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final check = ref.watch(deletionCheckProvider);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.deleteTitle)),
      body: switch (check) {
        AsyncData(:final value) => ListView(
          padding: const EdgeInsets.all(Spacing.x4),
          children: [
            _Warning(check: value),
            const SizedBox(height: Spacing.x4),
            if (value.blocked) _Blockers(check: value) else ..._form(context),
          ],
        ),
        AsyncError(:final error) => NeoEmptyState(
          icon: Icons.cloud_off_outlined,
          message: l10n.deleteLoadFailed(
            error is ApiException
                ? apiErrorText(l10n, error)
                : l10n.errorServer,
          ),
          action: OutlinedButton(
            onPressed: () => ref.invalidate(deletionCheckProvider),
            child: Text(l10n.commonRetry),
          ),
        ),
        _ => const Center(child: CircularProgressIndicator()),
      },
    );
  }

  List<Widget> _form(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;

    return [
      if (_error != null) ...[
        NeoBanner.error(message: apiErrorText(l10n, _error!)),
        const SizedBox(height: Spacing.x3),
      ],
      TextField(
        controller: _password,
        obscureText: _obscure,
        enabled: !_busy,
        autofillHints: const [AutofillHints.password],
        decoration: InputDecoration(
          labelText: l10n.deletePasswordLabel,
          suffixIcon: IconButton(
            onPressed: () => setState(() => _obscure = !_obscure),
            icon: Icon(_obscure ? Icons.visibility : Icons.visibility_off),
          ),
        ),
      ),
      const SizedBox(height: Spacing.x3),
      TextField(
        controller: _confirmation,
        enabled: !_busy,
        textCapitalization: TextCapitalization.characters,
        decoration: InputDecoration(labelText: l10n.deleteConfirmLabel),
      ),
      const SizedBox(height: Spacing.x4),
      FilledButton.icon(
        style: FilledButton.styleFrom(
          backgroundColor: tokens.danger,
          foregroundColor: tokens.neutral0,
        ),
        onPressed: _busy || !_ready(l10n) ? null : _delete,
        icon: _busy
            ? SizedBox.square(
                dimension: Spacing.x4,
                child: CircularProgressIndicator(
                  strokeWidth: 2,
                  color: tokens.neutral0,
                ),
              )
            : const Icon(Icons.person_remove_outlined),
        label: Text(l10n.deleteButton),
      ),
    ];
  }
}

class _Warning extends StatelessWidget {
  const _Warning({required this.check});

  final DeletionCheck check;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final colours = context.tokens.forTone(StatusTone.danger);
    final language = Localizations.localeOf(context).languageCode;
    final amount = formatMoney(check.balance, language: language);

    final balance = check.balance > 0 && check.bonusPages > 0
        ? l10n.deleteBalance(amount, check.bonusPages)
        : check.balance > 0
        ? l10n.deleteBalanceOnly(amount)
        : null;

    return Container(
      padding: const EdgeInsets.all(Spacing.x4),
      decoration: BoxDecoration(
        color: colours.background,
        borderRadius: BorderRadius.circular(Radii.md),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(Icons.warning_amber_rounded, color: colours.foreground),
          const SizedBox(width: Spacing.x3),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  l10n.deleteWarningTitle,
                  style: theme.textTheme.titleSmall?.copyWith(
                    color: colours.foreground,
                  ),
                ),
                const SizedBox(height: Spacing.x1),
                Text(l10n.deleteWarningBody, style: theme.textTheme.bodyMedium),
                if (balance != null) ...[
                  const SizedBox(height: Spacing.x2),
                  Text(
                    balance,
                    style: theme.textTheme.bodyMedium?.copyWith(
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _Blockers extends StatelessWidget {
  const _Blockers({required this.check});

  final DeletionCheck check;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final colours = context.tokens.forTone(StatusTone.warning);

    Widget row(int count, String text, IconData icon, String location) =>
        count == 0
        ? const SizedBox.shrink()
        : ListTile(
            contentPadding: EdgeInsets.zero,
            leading: Icon(icon, color: colours.foreground),
            title: Text(text),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => context.push(location),
          );

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(l10n.deleteBlockedTitle, style: theme.textTheme.titleSmall),
        const SizedBox(height: Spacing.x2),
        row(
          check.activePrints,
          l10n.deleteBlockerPrints(check.activePrints),
          Icons.print_outlined,
          Routes.home,
        ),
        row(
          check.scheduledPrints,
          l10n.deleteBlockerSchedules(check.scheduledPrints),
          Icons.schedule_outlined,
          Routes.scheduled,
        ),
        row(
          check.uncollectedOrders,
          l10n.deleteBlockerUncollected(check.uncollectedOrders),
          Icons.storefront_outlined,
          Routes.prepared,
        ),
        row(
          check.pendingRefunds,
          l10n.deleteBlockerRefunds(check.pendingRefunds),
          Icons.undo,
          Routes.wallet,
        ),
      ],
    );
  }
}
