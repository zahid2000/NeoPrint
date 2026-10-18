import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/core/format/formats.dart';
import 'package:neoprint/core/network/api_client.dart';
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/kiosk_search/kiosk_detail_screen.dart';
import 'package:neoprint/features/kiosk_search/kiosk_models.dart';
import 'package:neoprint/features/kiosk_search/kiosk_repository.dart';
import 'package:neoprint/features/kiosk_search/kiosk_search_controller.dart';
import 'package:neoprint/features/kiosk_search/kiosk_search_screen.dart';
import 'package:neoprint/features/kiosk_search/user_location.dart';
import 'package:neoprint/features/session/session_controller.dart';
import 'package:neoprint/features/session/session_models.dart';
import 'package:neoprint/features/session/session_repository.dart';
import 'package:neoprint/core/platform/browser.dart';
import 'package:neoprint/features/kiosk_search/kiosk_map_view.dart';
import 'package:neoprint/features/kiosk_search/map/kiosk_map_engine.dart';
import 'package:neoprint/features/kiosk_search/map/map_config.dart';
import 'package:neoprint/l10n/app_localizations.dart';

import 'map_fakes.dart';

/// A kiosk search the test scripts: it records what it was asked and
/// answers with [kiosks], or with [error].
class FakeKiosks implements KioskRepository {
  FakeKiosks(this.kiosks, {this.details = const {}});

  final List<KioskSummary> kiosks;
  final Map<String, KioskDetail> details;
  final searches = <({String? text, GeoPoint? near, double? radiusKm})>[];
  ApiException? error;

  @override
  Future<List<KioskSummary>> search({
    String? text,
    GeoPoint? near,
    double? radiusKm,
  }) async {
    searches.add((text: text, near: near, radiusKm: radiusKm));
    if (error case final error?) {
      throw error;
    }

    final query = text?.toLowerCase() ?? '';
    return [
      for (final kiosk in kiosks)
        if (query.isEmpty ||
            '${kiosk.name} ${kiosk.code} ${kiosk.address}'
                .toLowerCase()
                .contains(query))
          kiosk,
    ];
  }

  @override
  Future<KioskDetail> detail(String idOrCode) async =>
      details[idOrCode] ??
      (throw const ApiException(
        statusCode: 404,
        message: 'Kiosk tapılmadı.',
        kind: ApiErrorKind.server,
      ));
}

class FakeLocation implements UserLocation {
  FakeLocation(this.result);

  LocationResult result;
  var asked = 0;

  @override
  Future<LocationResult> current() async {
    asked++;
    return result;
  }
}

const elmler = KioskSummary(
  id: 'k1',
  code: 'KSK-9002',
  name: 'Elmlər',
  address: 'Bakı, Elmlər Akademiyası metro',
  lat: 40.3752,
  lng: 49.8342,
  status: KioskStatus.online,
  distanceKm: 0.53,
);

const mayis = KioskSummary(
  id: 'k2',
  code: 'KSK-9001',
  name: '28 May',
  address: 'Bakı, 28 May metro',
  lat: 40.3793,
  lng: 49.8486,
  status: KioskStatus.offline,
);

/// No session open: the kiosk detail offers to open one.
class _NoSession implements SessionRepository {
  @override
  Future<KioskSessionInfo?> current() async => null;

  @override
  dynamic noSuchMethod(Invocation invocation) => throw UnimplementedError();
}

Widget app(
  Widget home,
  FakeKiosks kiosks, {
  UserLocation? location,
  FakeMapEngine? map,
  FakeMapConfig? mapConfig,
  List<Uri>? opened,
}) => ProviderScope(
  overrides: [
    kioskRepositoryProvider.overrideWithValue(kiosks),
    kioskMapEngineProvider.overrideWithValue(map ?? FakeMapEngine()),
    mapConfigRepositoryProvider.overrideWithValue(mapConfig ?? FakeMapConfig()),
    browserProvider.overrideWithValue((url) async {
      opened?.add(url);
      return true;
    }),
    sessionRepositoryProvider.overrideWithValue(_NoSession()),
    if (location != null) userLocationProvider.overrideWithValue(location),
  ],
  child: MaterialApp(
    theme: AppTheme.light(),
    locale: const Locale('az'),
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: home,
  ),
);

void main() {
  group('kiosk list', () {
    testWidgets('shows each kiosk with its status in words and its distance', (
      tester,
    ) async {
      await tester.pumpWidget(
        app(const KioskSearchScreen(), FakeKiosks([elmler, mayis])),
      );
      await tester.pumpAndSettle();

      expect(find.text('Elmlər'), findsOneWidget);
      expect(
        find.text('KSK-9002 · Bakı, Elmlər Akademiyası metro'),
        findsOneWidget,
      );
      expect(find.text('İşləyir'), findsOneWidget);
      expect(find.text('Əlaqə yoxdur'), findsOneWidget);
      expect(find.text('530 m'), findsOneWidget);
      expect(find.text('Xəritə'), findsOneWidget);
    });

    testWidgets('searches when typing pauses, and says when nothing matches', (
      tester,
    ) async {
      final kiosks = FakeKiosks([elmler, mayis]);
      await tester.pumpWidget(app(const KioskSearchScreen(), kiosks));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextField), 'May');
      await tester.pump(const Duration(milliseconds: 100));
      expect(kiosks.searches.last.text, '', reason: 'not on every letter');

      await tester.pumpAndSettle(const Duration(milliseconds: 500));
      expect(kiosks.searches.last.text, 'May');
      expect(find.text('28 May'), findsOneWidget);
      expect(find.text('Elmlər'), findsNothing);

      await tester.enterText(find.byType(TextField), 'yoxdur');
      await tester.pumpAndSettle(const Duration(milliseconds: 500));
      expect(find.text('Axtarışa uyğun kiosk tapılmadı'), findsOneWidget);

      await tester.tap(find.widgetWithText(OutlinedButton, 'Axtarışı təmizlə'));
      await tester.pumpAndSettle();
      expect(find.text('Elmlər'), findsOneWidget);
    });

    testWidgets('near me searches around the user within the radius chosen', (
      tester,
    ) async {
      final kiosks = FakeKiosks([elmler]);
      final location = FakeLocation(
        const LocationFound((lat: 40.377, lng: 49.84)),
      );
      await tester.pumpWidget(
        app(const KioskSearchScreen(), kiosks, location: location),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Yaxınımda'));
      await tester.pumpAndSettle();

      expect(kiosks.searches.last.near, (lat: 40.377, lng: 49.84));
      expect(kiosks.searches.last.radiusKm, 3);

      await tester.tap(find.text('10 km'));
      await tester.pumpAndSettle();
      expect(kiosks.searches.last.radiusKm, 10);

      // Off again: no place, no radius.
      await tester.tap(find.text('Yaxınımda'));
      await tester.pumpAndSettle();
      expect(kiosks.searches.last.near, isNull);
      expect(kiosks.searches.last.radiusKm, isNull);
    });

    testWidgets('a refused location says why and how to fix it', (
      tester,
    ) async {
      final kiosks = FakeKiosks([elmler]);
      final location = FakeLocation(
        const LocationMissing(LocationProblem.deniedForever),
      );
      await tester.pumpWidget(
        app(const KioskSearchScreen(), kiosks, location: location),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Yaxınımda'));
      await tester.pumpAndSettle();

      expect(
        find.textContaining('Telefonun tənzimləmələrində NeoPrint üçün'),
        findsOneWidget,
      );
      expect(kiosks.searches.last.near, isNull, reason: 'the list stays');
      expect(find.text('Elmlər'), findsOneWidget);
    });

    testWidgets('nothing near offers a wider radius', (tester) async {
      final location = FakeLocation(
        const LocationFound((lat: 40.0, lng: 49.0)),
      );
      await tester.pumpWidget(
        app(const KioskSearchScreen(), FakeKiosks([]), location: location),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Yaxınımda'));
      await tester.pumpAndSettle();

      expect(find.text('3 km radiusda kiosk yoxdur'), findsOneWidget);
      await tester.tap(find.widgetWithText(OutlinedButton, 'Radiusu böyüt'));
      await tester.pumpAndSettle();
      expect(find.text('10 km radiusda kiosk yoxdur'), findsOneWidget);
      expect(find.text('Radiusu böyüt'), findsNothing, reason: 'the widest');
    });

    testWidgets('the map shows the same kiosks, a legend and a card per tap', (
      tester,
    ) async {
      tester.view.physicalSize = const Size(430, 932);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);

      final map = FakeMapEngine();
      final opened = <Uri>[];
      await tester.pumpWidget(
        app(
          const KioskSearchScreen(),
          FakeKiosks([elmler, mayis]),
          map: map,
          opened: opened,
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Xəritə'));
      await tester.pumpAndSettle();

      // The style the server names, a pin per kiosk in the result.
      expect(
        find.text('style: https://tiles.openfreemap.org/styles/liberty'),
        findsOneWidget,
      );
      expect(map.requests.last.pins.map((pin) => pin.id), ['k1', 'k2']);
      expect(find.byType(CircularProgressIndicator), findsNothing);

      // The attribution: all three, each a link that opens its owner's page.
      for (final label in [
        'OpenFreeMap',
        '© OpenMapTiles',
        '© OpenStreetMap contributors',
      ]) {
        expect(find.text(label), findsOneWidget, reason: label);
      }
      await tester.tap(find.text('© OpenStreetMap contributors'));
      expect(opened, [Uri.parse('https://www.openstreetmap.org/copyright')]);
      for (final status in [
        'İşləyir',
        'Fasilədə',
        'Servisdə',
        'Əlaqə yoxdur',
      ]) {
        expect(find.text(status), findsOneWidget, reason: 'legend: $status');
      }

      await tester.tap(find.text('pin: Elmlər, İşləyir'));
      await tester.pumpAndSettle();

      // The card reads as words, not one letter a line.
      final name = tester.getSize(find.text('Elmlər'));
      expect(name.width, greaterThan(name.height));
      expect(find.text('530 m'), findsOneWidget);
      expect(find.widgetWithText(OutlinedButton, 'Ətraflı'), findsOneWidget);
    });

    testWidgets(
      'a map that does not load in time gives way to the list, with why and a '
      'retry, and each opening tries again',
      (tester) async {
        tester.view.physicalSize = const Size(430, 932);
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);

        final map = FakeMapEngine(loads: false);
        final config = FakeMapConfig();
        await tester.pumpWidget(
          app(
            const KioskSearchScreen(),
            FakeKiosks([elmler, mayis]),
            map: map,
            mapConfig: config,
          ),
        );
        await tester.pumpAndSettle();
        await tester.tap(find.text('Xəritə'));
        await tester.pump();
        await tester.pump();
        expect(find.byType(CircularProgressIndicator), findsOneWidget);

        // The style never comes.
        await tester.pump(mapLoadTimeout);
        await tester.pump();
        expect(find.text('Xəritə yüklənmədi'), findsOneWidget);
        expect(
          find.textContaining('Kiosklar aşağıdakı siyahıdadır'),
          findsOneWidget,
        );
        expect(find.byKey(const Key('fake-map')), findsNothing);
        // The same kiosks, as the list.
        expect(find.text('Elmlər'), findsOneWidget);
        expect(find.text('28 May'), findsOneWidget);

        // The map server is back: "Yenidən cəhd et" draws it.
        map.loads = true;
        await tester.tap(
          find.widgetWithText(OutlinedButton, 'Yenidən cəhd et'),
        );
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('fake-map')), findsOneWidget);
        expect(find.text('Xəritə yüklənmədi'), findsNothing);
        expect(config.asked, 2, reason: 'the settings are asked again');
      },
    );

    testWidgets('settings that cannot be read give the same fallback', (
      tester,
    ) async {
      final config = FakeMapConfig()..fails = true;
      await tester.pumpWidget(
        app(const KioskSearchScreen(), FakeKiosks([elmler]), mapConfig: config),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Xəritə'));
      await tester.pumpAndSettle();

      expect(find.text('Xəritə yüklənmədi'), findsOneWidget);
      expect(find.text('Elmlər'), findsOneWidget);
    });

    testWidgets('where no map can be drawn (the web build), the list and why', (
      tester,
    ) async {
      final config = FakeMapConfig();
      await tester.pumpWidget(
        app(
          const KioskSearchScreen(),
          FakeKiosks([elmler]),
          map: FakeMapEngine(available: false),
          mapConfig: config,
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Xəritə'));
      await tester.pumpAndSettle();

      expect(find.text('Xəritə bu versiyada yoxdur'), findsOneWidget);
      expect(find.text('Elmlər'), findsOneWidget);
      expect(
        find.widgetWithText(OutlinedButton, 'Yenidən cəhd et'),
        findsNothing,
      );
      expect(config.asked, 0, reason: 'no map, nothing asked');
    });

    testWidgets('a failed load says so and retries', (tester) async {
      final kiosks = FakeKiosks([elmler])
        ..error = const ApiException(
          statusCode: 0,
          message: '',
          kind: ApiErrorKind.unreachable,
        );
      await tester.pumpWidget(app(const KioskSearchScreen(), kiosks));
      await tester.pumpAndSettle();

      expect(
        find.textContaining('Kioskları yükləmək alınmadı'),
        findsOneWidget,
      );

      kiosks.error = null;
      await tester.tap(find.widgetWithText(OutlinedButton, 'Yenidən cəhd et'));
      await tester.pumpAndSettle();
      expect(find.text('Elmlər'), findsOneWidget);
    });
  });

  group('kiosk detail', () {
    const detail = KioskDetail(
      summary: elmler,
      color: true,
      duplex: false,
      a3: true,
      paper: [
        KioskPaper(size: 'A4', state: PaperState.low, sheets: 120),
        KioskPaper(size: 'A3', state: PaperState.out, sheets: 0),
      ],
      queueLength: 2,
      prices: KioskPrices(
        bwPerSide: 0.1,
        colorPerSide: 0.3,
        paperMultipliers: {'A4': 1, 'A3': 2},
        minOrder: 0.2,
        tiers: [PriceTier(minSides: 50, discountPercent: 10)],
      ),
    );

    testWidgets('shows what it can do, prices, queue and paper in words', (
      tester,
    ) async {
      await tester.pumpWidget(
        app(
          const KioskDetailScreen(idOrCode: 'KSK-9002'),
          FakeKiosks([], details: {'KSK-9002': detail}),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Elmlər'), findsOneWidget);
      expect(find.text('İşləyir'), findsOneWidget);
      expect(find.text('Rəngli çap'), findsOneWidget);
      expect(find.text('İkitərəfli çap — yoxdur'), findsOneWidget);
      expect(find.text('0,10 ₼'), findsOneWidget);
      expect(find.text('0,30 ₼'), findsOneWidget);
      expect(find.text('A3 — A4 qiymətinin 2 misli'), findsOneWidget);
      expect(find.text('50 üzdən: −10%'), findsOneWidget);
      await tester.scrollUntilVisible(find.text('Növbədə 2 sifariş'), 200);

      await tester.scrollUntilVisible(find.text('A3 kağızı qurtarıb'), 200);
      expect(find.text('A4 kağızı azdır'), findsOneWidget);
      expect(
        find.text('Bu kioskda hazırda A3 çap etmək olmur.'),
        findsOneWidget,
      );
    });

    testWidgets('a kiosk that is not there reads as not found', (tester) async {
      await tester.pumpWidget(
        app(const KioskDetailScreen(idOrCode: 'KSK-0000'), FakeKiosks([])),
      );
      await tester.pumpAndSettle();

      expect(find.text('Kiosk tapılmadı'), findsOneWidget);
      expect(find.textContaining('Başqa kiosk seçin'), findsOneWidget);
      expect(
        find.widgetWithText(OutlinedButton, 'Bütün kiosklar'),
        findsOneWidget,
      );
      expect(find.byType(BackButton), findsOneWidget, reason: 'a way back');
    });
  });

  group('formats', () {
    test('a distance in metres under a kilometre, then km', () {
      expect(formatDistance(0.534), (value: '530', kilometres: false));
      expect(formatDistance(1.25), (value: '1,3', kilometres: true));
      expect(formatDistance(1.25, language: 'en'), (
        value: '1.3',
        kilometres: true,
      ));
      expect(formatDistance(12.6), (value: '13', kilometres: true));
    });

    test('a number keeps only the digits it needs', () {
      expect(formatNumber(10), '10');
      expect(formatNumber(12.5), '12,5');
      expect(formatNumber(12.5, language: 'en'), '12.5');
      expect(formatNumber(1.5), '1,5');
    });
  });
}
