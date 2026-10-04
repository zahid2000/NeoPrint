import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/l10n/language_controller.dart';
import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_empty_state.dart';
import 'markdown_text.dart';

/// A published legal document (`GET legal/{docType}?lang=`).
@immutable
final class LegalDocument {
  const LegalDocument({
    required this.version,
    required this.language,
    required this.content,
    required this.publishedAt,
  });

  factory LegalDocument.fromJson(Map<String, dynamic> json) => LegalDocument(
    version: json['version'] as String,
    language: json['language'] as String,
    content: json['content'] as String,
    publishedAt: DateTime.parse(json['publishedAt'] as String),
  );

  final String version;

  /// The language it is written in: Azerbaijani when there is no translation.
  final String language;

  /// Markdown, as published in the admin panel.
  final String content;
  final DateTime publishedAt;
}

class LegalRepository {
  LegalRepository(this._api);

  final ApiClient _api;

  Future<LegalDocument> load(LegalDocumentType type, String language) =>
      _api.send(
        method: 'GET',
        path: 'api/legal/${type.wireName}',
        authenticated: false,
        query: {'lang': language},
        parse: (json) => LegalDocument.fromJson(json as Map<String, dynamic>),
      );
}

final legalRepositoryProvider = Provider<LegalRepository>(
  (ref) => LegalRepository(ref.watch(apiClientProvider)),
);

/// The current version in the app's language; read again when it changes.
final legalDocumentProvider =
    FutureProvider.family<LegalDocument, LegalDocumentType>(
      (ref, type) => ref
          .watch(legalRepositoryProvider)
          .load(type, ref.watch(appLanguageProvider.select((l) => l.code))),
    );

String legalTitle(AppLocalizations l10n, LegalDocumentType type) =>
    switch (type) {
      LegalDocumentType.terms => l10n.consentsTerms,
      _ => l10n.consentsPrivacy,
    };

/// Profile → "Hüquqi sənədlər" (plan section 8.2; docs/DESIGN_SYSTEM.md
/// "Mobil: dil və hüquqi sənədlər").
class LegalListScreen extends StatelessWidget {
  const LegalListScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.profileLegal)),
      body: ListView(
        children: [
          for (final (type, icon) in [
            (LegalDocumentType.privacy, Icons.privacy_tip_outlined),
            (LegalDocumentType.terms, Icons.gavel_outlined),
          ])
            ListTile(
              leading: Icon(icon),
              title: Text(legalTitle(l10n, type)),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push(Routes.legal(type)),
            ),
        ],
      ),
    );
  }
}

/// One document, readable before signing in too: the registration and
/// consents screens open it with "Oxu" (docs/DECISIONS.md T-152).
class LegalDocumentScreen extends ConsumerWidget {
  const LegalDocumentScreen({required this.type, super.key});

  final LegalDocumentType type;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final document = ref.watch(legalDocumentProvider(type));
    final language = Localizations.localeOf(context).languageCode;

    return Scaffold(
      appBar: AppBar(title: Text(legalTitle(l10n, type))),
      body: switch (document) {
        AsyncData(:final value) => ListView(
          padding: const EdgeInsets.all(Spacing.x4),
          children: [
            Text(
              l10n.legalVersion(value.version, formatDate(value.publishedAt)),
              style: Theme.of(context).textTheme.bodySmall
                  ?.copyWith(color: context.tokens.neutral600),
            ),
            if (value.language != language) ...[
              const SizedBox(height: Spacing.x3),
              NeoBanner(
                message: l10n.legalOnlyAz,
                tone: StatusTone.info,
                icon: Icons.translate,
              ),
            ],
            const SizedBox(height: Spacing.x3),
            MarkdownText(value.content),
          ],
        ),
        AsyncError(:final error) => NeoEmptyState(
          icon: Icons.cloud_off_outlined,
          message: l10n.legalLoadFailed(
            error is ApiException
                ? apiErrorText(l10n, error)
                : l10n.errorServer,
          ),
          action: OutlinedButton(
            onPressed: () => ref.invalidate(legalDocumentProvider(type)),
            child: Text(l10n.commonRetry),
          ),
        ),
        _ => const Center(child: CircularProgressIndicator()),
      },
    );
  }

  static String formatDate(DateTime value) {
    final local = value.toLocal();
    String two(int n) => n.toString().padLeft(2, '0');
    return '${two(local.day)}.${two(local.month)}.${local.year}';
  }
}

/// "Oxu" under a consent box: the document before it is accepted
/// (docs/DECISIONS.md T-152). Outside the box's own semantics node, and
/// named by its document, so a screen reader does not hear a bare "Oxu".
class LegalReadButton extends StatelessWidget {
  const LegalReadButton({required this.type, super.key});

  final LegalDocumentType type;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return Align(
      alignment: AlignmentDirectional.centerStart,
      child: TextButton(
        onPressed: () => context.push(Routes.legal(type)),
        child: Text(
          l10n.consentsRead,
          semanticsLabel: '${l10n.consentsRead}: ${legalTitle(l10n, type)}',
        ),
      ),
    );
  }
}
