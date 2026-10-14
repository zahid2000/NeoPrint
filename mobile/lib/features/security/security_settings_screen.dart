import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/security/app_lock.dart';
import '../../core/security/biometrics.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import 'pin_screen.dart';

/// Whether this phone can check a fingerprint or a face.
final biometricsAvailableProvider = FutureProvider.autoDispose<bool>(
  (ref) => ref.watch(biometricsProvider).available(),
);

/// Profil → "PIN və biometrika" (docs/DESIGN_SYSTEM.md 5.2): changing the PIN,
/// and the fingerprint or face. No way to switch the PIN off - it is
/// mandatory (the owner, 2026-10-14), and the screen says so.
class SecuritySettingsScreen extends ConsumerWidget {
  const SecuritySettingsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final available = ref.watch(biometricsAvailableProvider).value ?? false;
    final on = ref.watch(appLockProvider.select((lock) => lock.biometrics));

    return Scaffold(
      appBar: AppBar(title: Text(l10n.securityTitle)),
      body: ListView(
        children: [
          Padding(
            padding: const EdgeInsets.all(Spacing.x4),
            child: Text(
              l10n.securityPinMandatory,
              style: theme.textTheme.bodyMedium,
            ),
          ),
          ListTile(
            leading: const Icon(Icons.pin_outlined),
            title: Text(l10n.securityChangePin),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => const PinScreen(mode: PinMode.change),
              ),
            ),
          ),
          if (available)
            SwitchListTile(
              secondary: const Icon(Icons.fingerprint),
              title: Text(l10n.securityBiometrics),
              subtitle: Text(l10n.securityBiometricsHint),
              value: on,
              onChanged: (value) async {
                // Switched on only by a finger or a face that passes now.
                if (value &&
                    !await ref
                        .read(biometricsProvider)
                        .authenticate(
                          title: l10n.pinBiometricPrompt,
                          cancel: l10n.commonCancel,
                        )) {
                  return;
                }

                await ref.read(appLockProvider.notifier).setBiometrics(value);
              },
            ),
        ],
      ),
    );
  }
}
