import 'package:material_ui/material_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';
import '../../core/platform/browser.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_empty_state.dart';

/// How a customer reaches the company (T-438), as the backend's ContactKind.
enum ContactKind { phone, whatsApp, telegram, email }

/// One active channel from `GET contact`: what the panel wrote and the link
/// the server made of it (tel:, wa.me, t.me or mailto: - nothing else).
@immutable
final class ContactChannel {
  const ContactChannel({
    required this.kind,
    required this.value,
    required this.link,
  });

  final ContactKind kind;
  final String value;
  final Uri link;

  static ContactChannel? fromJson(Map<String, dynamic> json) {
    final kind = switch (json['kind']) {
      'Phone' => ContactKind.phone,
      'WhatsApp' => ContactKind.whatsApp,
      'Telegram' => ContactKind.telegram,
      'Email' => ContactKind.email,
      _ => null,
    };
    final link = Uri.tryParse(json['link'] as String? ?? '');

    // A kind this app does not know, or a link to anywhere else, is not shown.
    if (kind == null ||
        link == null ||
        !const {'tel', 'mailto', 'https'}.contains(link.scheme)) {
      return null;
    }

    return ContactChannel(
      kind: kind,
      value: json['value'] as String? ?? '',
      link: link,
    );
  }
}

class ContactRepository {
  ContactRepository(this._api);

  final ApiClient _api;

  Future<List<ContactChannel>> load() => _api.send(
    method: 'GET',
    path: 'api/contact',
    authenticated: false,
    parse: (json) => [
      for (final item in json as List<dynamic>)
        ?ContactChannel.fromJson(item as Map<String, dynamic>),
    ],
  );
}

final contactRepositoryProvider = Provider<ContactRepository>(
  (ref) => ContactRepository(ref.watch(apiClientProvider)),
);

final contactChannelsProvider = FutureProvider<List<ContactChannel>>(
  (ref) => ref.watch(contactRepositoryProvider).load(),
);

/// Opens a contact link; a test puts its own in.
final contactOpenerProvider = Provider<Future<bool> Function(Uri)>(
  (ref) => openInBrowser,
);

/// "Əlaqə" (T-438): the hotline and the chats the panel switched on.
class ContactScreen extends ConsumerWidget {
  const ContactScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final channels = ref.watch(contactChannelsProvider);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.contactTitle)),
      body: switch (channels) {
        AsyncData(:final value) when value.isEmpty => NeoEmptyState(
          icon: Icons.support_agent_outlined,
          message: l10n.contactEmpty,
          hint: l10n.contactEmptyHint,
          action: OutlinedButton(
            onPressed: () => context.push(Routes.claims),
            child: Text(l10n.profileClaims),
          ),
        ),
        AsyncData(:final value) => RefreshIndicator(
          onRefresh: () => ref.refresh(contactChannelsProvider.future),
          child: ListView(
            padding: const EdgeInsets.symmetric(vertical: Spacing.x3),
            children: [
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: Spacing.x4),
                child: Text(
                  l10n.contactHint,
                  style: Theme.of(context).textTheme.bodyMedium
                      ?.copyWith(color: context.tokens.neutral600),
                ),
              ),
              const SizedBox(height: Spacing.x2),
              for (final channel in value) _ChannelTile(channel: channel),
            ],
          ),
        ),
        AsyncError(:final error) => NeoEmptyState(
          icon: Icons.cloud_off_outlined,
          message: l10n.contactLoadFailed(
            error is ApiException
                ? apiErrorText(l10n, error)
                : l10n.errorServer,
          ),
          action: OutlinedButton(
            onPressed: () => ref.invalidate(contactChannelsProvider),
            child: Text(l10n.commonRetry),
          ),
        ),
        _ => const Center(child: CircularProgressIndicator()),
      },
    );
  }
}

class _ChannelTile extends ConsumerWidget {
  const _ChannelTile({required this.channel});

  final ContactChannel channel;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;
    final (icon, title) = switch (channel.kind) {
      ContactKind.phone => (Icons.call_outlined, l10n.contactPhone),
      ContactKind.whatsApp => (Icons.chat_outlined, l10n.contactWhatsApp),
      ContactKind.telegram => (Icons.send_outlined, l10n.contactTelegram),
      ContactKind.email => (Icons.mail_outline, l10n.contactEmail),
    };

    return ListTile(
      leading: Container(
        width: Spacing.x12,
        height: Spacing.x12,
        decoration: BoxDecoration(
          color: tokens.brand50,
          borderRadius: BorderRadius.circular(Radii.md),
        ),
        child: Icon(icon, color: tokens.brand500),
      ),
      title: Text(title),
      subtitle: Text(channel.value),
      trailing: Icon(
        channel.kind == ContactKind.phone ? Icons.call : Icons.open_in_new,
        color: tokens.brand500,
      ),
      onTap: () async {
        final opened = await ref.read(contactOpenerProvider)(channel.link);
        if (!opened && context.mounted) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.contactOpenFailed(channel.value))),
          );
        }
      },
    );
  }
}
