import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:neoprint/core/auth/auth_controller.dart';
import 'package:neoprint/core/auth/auth_models.dart';
import 'package:neoprint/core/l10n/app_language.dart';
import 'package:neoprint/core/l10n/language_controller.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/push/push_service.dart';
import 'package:neoprint/core/storage/secure_storage.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/auth/register_screen.dart';
import 'package:neoprint/features/legal/legal_documents.dart';
import 'package:neoprint/features/legal/markdown_text.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// `PUT users/me` as the test scripts it.
class FakeAccountLanguage implements AccountLanguageApi {
  final saved = <String>[];
  ApiException? error;

  @override
  Future<void> save(String language) async {
    if (error case final e?) {
      throw e;
    }
    saved.add(language);
  }
}

class FakeLegal implements LegalRepository {
  final asked = <(LegalDocumentType, String)>[];

  @override
  Future<LegalDocument> load(LegalDocumentType type, String language) async {
    asked.add((type, language));
    return LegalDocument(
      version: '0.1-draft',
      language: 'az',
      publishedAt: DateTime(2026, 9, 20),
      content:
          '# Məxfilik siyasəti\n\n> **QARALAMA**\n\n## Hüquqlarınız\n'
          'Məlumatlarınızı yükləyə\nbilərsiniz. **Balans itir.**\n\n'
          '- Fayllar\n\n| A | B |\n|---|---|\n| bir | iki |\n',
    );
  }
}

UserProfile account(String language) => UserProfile(
  id: 'u1',
  publicId: 'NP-ABC123',
  email: 'dil@neoprint.local',
  language: language,
  emailConfirmed: true,
);

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('markdown of a legal document', () {
    test('headings, notes, joined lines, bullets and table rows', () {
      final blocks = parseMarkdown(
        '# Başlıq\n\n> **QARALAMA** —\n> təsdiq lazımdır.\n\n'
        'Birinci sətir\nikinci sətir.\n\n- bənd\n\n---\n'
        '| Nə | Niyə |\n|---|---|\n| Email | Giriş |\n',
      );

      expect(blocks[0], isA<MarkdownHeading>());
      expect((blocks[0] as MarkdownHeading).level, 1);
      expect(
        (blocks[1] as MarkdownQuote).text,
        '**QARALAMA** — təsdiq lazımdır.',
      );
      expect(
        (blocks[2] as MarkdownParagraph).text,
        'Birinci sətir ikinci sətir.',
      );
      expect((blocks[3] as MarkdownBullet).text, 'bənd');
      expect(blocks[4], isA<MarkdownRule>());
      expect((blocks[5] as MarkdownParagraph).text, 'Nə · Niyə');
      expect((blocks[6] as MarkdownParagraph).text, 'Email · Giriş');
      expect(blocks, hasLength(7));
    });

    test('bold inside a line', () {
      final spans = markdownSpans('Balans **itir**, geri qaytarılmır.');

      expect(spans.map((s) => s.text), [
        'Balans ',
        'itir',
        ', geri qaytarılmır.',
      ]);
      expect(spans[1].style?.fontWeight, FontWeight.w600);
    });
  });

  group('the account language', () {
    late InMemorySecureStorage storage;
    late FakeAccountLanguage api;
    late ProviderContainer container;
    late StateController<UserProfile?> user;

    final userState = StateProvider<UserProfile?>((_) => null);

    setUp(() {
      TestWidgetsFlutterBinding.instance.platformDispatcher.localeTestValue =
          const Locale('az');
      storage = InMemorySecureStorage();
      api = FakeAccountLanguage();
      container = ProviderContainer(
        overrides: [
          secureStorageProvider.overrideWithValue(storage),
          accountLanguageApiProvider.overrideWithValue(api),
          currentUserProvider.overrideWith((ref) => ref.watch(userState)),
          pushPlatformProvider.overrideWithValue(const NoPushPlatform()),
        ],
      );
      user = container.read(userState.notifier);
    });

    tearDown(() {
      container.dispose();
      chosenLanguageCode = null;
      TestWidgetsFlutterBinding.instance.platformDispatcher
          .clearLocaleTestValue();
    });

    Future<void> settle() async {
      container.read(appLanguageProvider);
      await Future<void>.delayed(Duration.zero);
      await Future<void>.delayed(Duration.zero);
    }

    test('an account in the phone\'s language follows the phone', () async {
      user.state = account('az');
      await settle();

      final language = container.read(appLanguageProvider);
      expect(language.followsPhone, isTrue);
      expect(language.code, 'az');
      expect(storage.values[SecureStorageKeys.language], 'u1|system');
      expect(api.saved, isEmpty);
    });

    test('an account chosen in English is English on this phone too', () async {
      user.state = account('en');
      await settle();

      final language = container.read(appLanguageProvider);
      expect(language.choice, 'en');
      expect(currentLanguageCode(), 'en', reason: 'Accept-Language follows');
      expect(api.saved, isEmpty);
    });

    test('a choice is saved on the account and remembered here', () async {
      user.state = account('az');
      await settle();

      await container.read(appLanguageProvider.notifier).choose('ru');

      expect(container.read(appLanguageProvider).code, 'ru');
      expect(api.saved, ['ru']);
      expect(storage.values[SecureStorageKeys.language], 'u1|ru');
    });

    test('a choice the account refused goes back', () async {
      user.state = account('az');
      await settle();
      api.error = const ApiException.unreachable();

      await expectLater(
        container.read(appLanguageProvider.notifier).choose('en'),
        throwsA(isA<ApiException>()),
      );

      expect(container.read(appLanguageProvider).followsPhone, isTrue);
      expect(currentLanguageCode(), 'az');
    });

    test(
      '"the phone\'s language" chosen here tells the account the phone\'s',
      () async {
        await storage.write(SecureStorageKeys.language, 'u1|system');
        container.invalidate(appLanguageProvider);
        container.read(appLanguageProvider);
        user.state = account('en');
        await settle();
        await settle();

        expect(container.read(appLanguageProvider).followsPhone, isTrue);
        expect(api.saved, ['az']);
      },
    );
  });

  group('legal documents', () {
    Widget app(Widget home, FakeLegal legal, {String locale = 'en'}) =>
        ProviderScope(
          overrides: [
            legalRepositoryProvider.overrideWithValue(legal),
            appLanguageProvider.overrideWith(() => _Fixed(locale)),
          ],
          child: MaterialApp(
            theme: AppTheme.light(),
            locale: Locale(locale),
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: home,
          ),
        );

    testWidgets('a document is read in the app\'s language, and says when '
        'it has no translation yet', (tester) async {
      final legal = FakeLegal();
      await tester.pumpWidget(
        app(const LegalDocumentScreen(type: LegalDocumentType.privacy), legal),
      );
      await tester.pumpAndSettle();

      expect(legal.asked, [(LegalDocumentType.privacy, 'en')]);
      expect(find.text('Privacy policy'), findsOneWidget);
      expect(find.text('Version 0.1-draft · 20.09.2026'), findsOneWidget);
      expect(
        find.text('This document is available in Azerbaijani only for now.'),
        findsOneWidget,
      );
      expect(find.text('Hüquqlarınız'), findsOneWidget);
      expect(find.text('QARALAMA'), findsOneWidget);
    });

    testWidgets('in Azerbaijani there is no translation note', (tester) async {
      await tester.pumpWidget(
        app(
          const LegalDocumentScreen(type: LegalDocumentType.terms),
          FakeLegal(),
          locale: 'az',
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('İstifadə şərtləri'), findsOneWidget);
      expect(
        find.text('Bu sənəd hələ yalnız Azərbaycan dilindədir.'),
        findsNothing,
      );
    });

    testWidgets('registration lets each document be read before it is '
        'accepted', (tester) async {
      final opened = <String>[];
      final router = GoRouter(
        routes: [
          GoRoute(path: '/', builder: (_, __) => const RegisterScreen()),
          GoRoute(
            path: '/legal/:type',
            builder: (_, state) {
              opened.add(state.pathParameters['type']!);
              return const SizedBox();
            },
          ),
        ],
      );

      await tester.pumpWidget(
        ProviderScope(
          child: MaterialApp.router(
            theme: AppTheme.light(),
            locale: const Locale('az'),
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            routerConfig: router,
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Oxu'), findsNWidgets(2));
      await tester.ensureVisible(find.text('Oxu').first);
      await tester.tap(find.text('Oxu').first);
      await tester.pumpAndSettle();
      expect(opened, ['privacy']);
    });
  });
}

class _Fixed extends AppLanguageController {
  _Fixed(this.code);

  final String code;

  @override
  AppLanguage build() => AppLanguage(choice: code, phone: code);
}
