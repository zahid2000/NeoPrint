import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_status_chip.dart';
import 'share_models.dart';
import 'share_repository.dart';
import 'shares_controller.dart';

/// "Paylaşılanlar" (docs/DESIGN_SYSTEM.md, "Mobil: fayl paylaşımı"): what was
/// sent to the user, with their own id and QR to receive more, and what they
/// sent.
class SharesScreen extends StatelessWidget {
  const SharesScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return DefaultTabController(
      length: 2,
      child: Scaffold(
        appBar: AppBar(
          title: Text(l10n.sharesTitle),
          bottom: TabBar(
            tabs: [
              Tab(text: l10n.sharesReceived),
              Tab(text: l10n.sharesSent),
            ],
          ),
        ),
        body: const TabBarView(children: [_Received(), _Sent()]),
      ),
    );
  }
}

class _Received extends ConsumerWidget {
  const _Received();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final shares = ref.watch(receivedSharesProvider);

    return RefreshIndicator(
      onRefresh: () => ref.refresh(receivedSharesProvider.future),
      child: ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.all(Spacing.x4),
        children: [
          const _MyIdCard(),
          const SizedBox(height: Spacing.x4),
          ..._items(
            context,
            shares,
            empty: NeoEmptyState(
              icon: Icons.move_to_inbox_outlined,
              message: l10n.sharesReceivedEmpty,
              hint: l10n.sharesReceivedEmptyHint,
            ),
            build: (share) => _ShareTile(
              share: share,
              received: true,
              onTap: () =>
                  context.push(Routes.shareDetail(share.id), extra: share),
            ),
          ),
        ],
      ),
    );
  }
}

class _Sent extends ConsumerWidget {
  const _Sent();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final shares = ref.watch(sentSharesProvider);

    return RefreshIndicator(
      onRefresh: () => ref.refresh(sentSharesProvider.future),
      child: ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.all(Spacing.x4),
        children: _items(
          context,
          shares,
          empty: NeoEmptyState(
            icon: Icons.outbox_outlined,
            message: l10n.sharesSentEmpty,
            hint: l10n.sharesSentEmptyHint,
          ),
          build: (share) => _ShareTile(share: share, received: false),
        ),
      ),
    );
  }
}

/// The list's rows, its empty state, a loading indicator or the error.
List<Widget> _items(
  BuildContext context,
  AsyncValue<List<ShareItem>> shares, {
  required Widget empty,
  required Widget Function(ShareItem) build,
}) {
  final l10n = AppLocalizations.of(context);

  return switch (shares) {
    AsyncData(:final value) when value.isEmpty => [empty],
    AsyncData(:final value) => [
      for (final share in value) ...[
        build(share),
        const SizedBox(height: Spacing.x2),
      ],
    ],
    AsyncError(:final error) => [
      NeoBanner.error(
        message: l10n.sharesLoadFailed(
          error is ApiException ? apiErrorText(l10n, error) : l10n.errorServer,
        ),
      ),
    ],
    _ => [
      const Padding(
        padding: EdgeInsets.all(Spacing.x6),
        child: Center(child: CircularProgressIndicator()),
      ),
    ],
  };
}

/// "Sizin ID-niz": what someone needs to send this user a file.
class _MyIdCard extends ConsumerWidget {
  const _MyIdCard();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final publicId = ref.watch(currentUserProvider)?.publicId ?? '';

    return Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(l10n.sharesMyId, style: theme.textTheme.bodyMedium),
            const SizedBox(height: Spacing.x1),
            Text(
              publicId,
              style: theme.textTheme.titleMedium?.copyWith(
                fontFeatures: const [FontFeature.tabularFigures()],
              ),
            ),
            const SizedBox(height: Spacing.x2),
            Wrap(
              spacing: Spacing.x2,
              children: [
                TextButton.icon(
                  onPressed: () async {
                    await Clipboard.setData(ClipboardData(text: publicId));

                    if (context.mounted) {
                      ScaffoldMessenger.of(context).showSnackBar(
                        SnackBar(content: Text(l10n.profileCopied)),
                      );
                    }
                  },
                  icon: const Icon(Icons.copy_outlined),
                  label: Text(l10n.sharesCopy),
                ),
                TextButton.icon(
                  onPressed: () => showModalBottomSheet<void>(
                    context: context,
                    showDragHandle: true,
                    builder: (_) => _QrSheet(publicId: publicId),
                  ),
                  icon: const Icon(Icons.qr_code_2),
                  label: Text(l10n.sharesMyQr),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _QrSheet extends ConsumerStatefulWidget {
  const _QrSheet({required this.publicId});

  final String publicId;

  @override
  ConsumerState<_QrSheet> createState() => _QrSheetState();
}

class _QrSheetState extends ConsumerState<_QrSheet> {
  late Future<Uint8List> _qr = ref.read(shareRepositoryProvider).myQr();

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(
          Spacing.x4,
          0,
          Spacing.x4,
          Spacing.x6,
        ),
        child: FutureBuilder<Uint8List>(
          future: _qr,
          builder: (context, snapshot) => Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(widget.publicId, style: theme.textTheme.titleLarge),
              const SizedBox(height: Spacing.x4),
              if (snapshot.hasError)
                Column(
                  children: [
                    NeoBanner.error(
                      message: l10n.sharesQrFailed(
                        snapshot.error is ApiException
                            ? apiErrorText(
                                l10n,
                                snapshot.error! as ApiException,
                              )
                            : l10n.errorServer,
                      ),
                    ),
                    const SizedBox(height: Spacing.x3),
                    OutlinedButton(
                      onPressed: () => setState(
                        () => _qr = ref.read(shareRepositoryProvider).myQr(),
                      ),
                      child: Text(l10n.commonRetry),
                    ),
                  ],
                )
              else if (!snapshot.hasData)
                const Padding(
                  padding: EdgeInsets.all(Spacing.x8),
                  child: CircularProgressIndicator(),
                )
              else
                // On white in both themes: a QR needs its light background to scan.
                Container(
                  padding: const EdgeInsets.all(Spacing.x4),
                  decoration: BoxDecoration(
                    color: Functional.qrBackground,
                    borderRadius: BorderRadius.circular(Radii.md),
                  ),
                  child: Image.memory(
                    snapshot.data!,
                    width: MediaQuery.sizeOf(context).width * 0.6,
                    height: MediaQuery.sizeOf(context).width * 0.6,
                    filterQuality: FilterQuality.none,
                    semanticLabel: l10n.sharesMyQr,
                  ),
                ),
              const SizedBox(height: Spacing.x4),
              Text(
                l10n.sharesQrHint,
                textAlign: TextAlign.center,
                style: theme.textTheme.bodyMedium,
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _ShareTile extends StatelessWidget {
  const _ShareTile({required this.share, required this.received, this.onTap});

  final ShareItem share;
  final bool received;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final when = formatDateTime(
      share.createdAt,
      today: l10n.commonToday,
      yesterday: l10n.commonYesterday,
    );

    return Card(
      margin: EdgeInsets.zero,
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.all(Spacing.x4),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Icon(
                share.isSensitive
                    ? Icons.lock_outline
                    : Icons.description_outlined,
                color: context.tokens.brand500,
              ),
              const SizedBox(width: Spacing.x3),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      share.fileName,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: theme.textTheme.titleSmall,
                    ),
                    const SizedBox(height: Spacing.x1),
                    Text(
                      [
                        share.otherPublicId,
                        when,
                        l10n.filesPageCount(share.pageCount),
                      ].join(' · '),
                      style: theme.textTheme.bodySmall,
                    ),
                    if (share.message case final message?) ...[
                      const SizedBox(height: Spacing.x1),
                      Text(
                        '“$message”',
                        style: theme.textTheme.bodySmall?.copyWith(
                          fontStyle: FontStyle.italic,
                        ),
                      ),
                    ],
                    const SizedBox(height: Spacing.x2),
                    ShareStatusChip(
                      availability: share.availability,
                      received: received,
                    ),
                  ],
                ),
              ),
              if (onTap != null) const Icon(Icons.chevron_right),
            ],
          ),
        ),
      ),
    );
  }
}

/// A share's state as colour, icon and text - worded for whichever side reads it.
class ShareStatusChip extends StatelessWidget {
  const ShareStatusChip({
    super.key,
    required this.availability,
    required this.received,
  });

  final ShareAvailability availability;
  final bool received;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    final (label, tone, icon) = switch (availability) {
      ShareAvailability.available =>
        received
            ? (l10n.sharesStatusNew, StatusTone.info, Icons.fiber_new_outlined)
            : (l10n.sharesStatusWaiting, StatusTone.neutral, Icons.schedule),
      ShareAvailability.saved => (
        received ? l10n.sharesStatusSaved : l10n.sharesStatusRecipientSaved,
        StatusTone.success,
        Icons.check_circle_outline,
      ),
      ShareAvailability.gone => (
        received ? l10n.sharesStatusGone : l10n.sharesStatusYouDeleted,
        StatusTone.neutral,
        Icons.block,
      ),
      ShareAvailability.changed => (
        received ? l10n.sharesStatusChanged : l10n.sharesStatusYouChanged,
        StatusTone.warning,
        Icons.history,
      ),
    };

    return NeoStatusChip(label: label, tone: tone, icon: icon);
  }
}
