// Pictures of the app's screens, for comparing them with the design
// (docs/dizayn/mobile/, T-377). Not a check: it draws each screen with the
// real fonts and writes a PNG to build/screens/, nothing more.
//
// Run on purpose only:
//   cd mobile && flutter test test/screens --dart-define=SCREENS=true
@Tags(['screens'])
library;

import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/features/home/home_screen.dart';
import 'package:neoprint/features/kiosk_search/kiosk_queue_screen.dart';
import 'package:neoprint/features/wallet/wallet_controller.dart';
import 'package:neoprint/features/wallet/wallet_repository.dart';
import 'package:neoprint/features/wallet/wallet_screen.dart';

import '../files_screen_test.dart' as files;
import '../job_progress_test.dart' as progress;
import '../kiosk_queue_test.dart' as queue;
import '../payments_kit.dart';
import '../print_hub_test.dart' as print_hub;

const _enabled = bool.fromEnvironment('SCREENS');

/// A phone the size of the design's frames, in logical pixels.
const _phone = Size(390, 844);

Future<void> _loadFonts() async {
  final inter = FontLoader('Inter')
    ..addFont(rootBundle.load('assets/fonts/Inter-Variable.ttf'));
  await inter.load();

  final root =
      Platform.environment['FLUTTER_ROOT'] ??
      File(Platform.resolvedExecutable).parent.parent.parent.parent.parent.path;
  final icons = File(
    '$root/bin/cache/artifacts/material_fonts/materialicons-regular.otf',
  );

  if (icons.existsSync()) {
    for (final (family, file) in [
      ('Roboto', 'roboto-regular.ttf'),
      ('Roboto', 'roboto-medium.ttf'),
      ('Roboto', 'roboto-bold.ttf'),
    ]) {
      final font = File('$root/bin/cache/artifacts/material_fonts/$file');
      if (font.existsSync()) {
        final loader = FontLoader(family)
          ..addFont(Future.value(ByteData.sublistView(font.readAsBytesSync())));
        await loader.load();
      }
    }

    final loader = FontLoader('MaterialIcons')
      ..addFont(Future.value(ByteData.sublistView(icons.readAsBytesSync())));
    await loader.load();
  }
}

Future<void> _shoot(WidgetTester tester, String name) async {
  await tester.pump(const Duration(milliseconds: 300));
  final view = tester.binding.renderViews.first;
  final layer = view.debugLayer! as OffsetLayer;

  await tester.runAsync(() async {
    final image = await layer.toImage(Offset.zero & view.size, pixelRatio: 2);
    final bytes = await image.toByteData(format: ui.ImageByteFormat.png);
    final file = File('build/screens/$name.png')..createSync(recursive: true);
    file.writeAsBytesSync(bytes!.buffer.asUint8List());
  });
}

Future<void> _phoneSize(WidgetTester tester) async {
  tester.view.physicalSize = _phone;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
}

void main() {
  setUpAll(() async {
    if (_enabled) {
      await _loadFonts();
    }
  });

  testWidgets('01 home', skip: !_enabled, (tester) async {
    await _phoneSize(tester);
    await tester.pumpWidget(
      RepaintBoundary(
        child: step4App(
          const HomeScreen(),
          overrides: step4Overrides(
            wallet: walletOf(12.4, bonusPages: 25),
            jobs: [jobOf('Printing', fileName: 'Report.pdf')],
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await _shoot(tester, '01-home');
  });

  testWidgets('02 files', skip: !_enabled, (tester) async {
    await _phoneSize(tester);
    await tester.pumpWidget(
      RepaintBoundary(
        child: files.app([
          files.file(name: 'Report.pdf'),
          files.file(name: 'CV.pdf'),
          files.file(name: 'Presentation.pptx'),
          files.file(name: 'Table.xlsx'),
          files.file(name: 'Photo.jpg'),
        ]),
      ),
    );
    await tester.pumpAndSettle();
    await _shoot(tester, '02-files');
  });

  testWidgets('03 new print', skip: !_enabled, (tester) async {
    await _phoneSize(tester);
    await tester.pumpWidget(RepaintBoundary(child: print_hub.hub()));
    await tester.pumpAndSettle();
    await _shoot(tester, '03-new-print');
  });

  testWidgets('09 printing', skip: !_enabled, (tester) async {
    await progress.show(tester, progress.order('Printing', printedSides: 10));
    await progress.settle(tester);
    await _shoot(tester, '09-printing');
  });

  testWidgets('10 completed', skip: !_enabled, (tester) async {
    await progress.show(tester, progress.order('Completed', printedSides: 12));
    await progress.settle(tester);
    await _shoot(tester, '10-completed');
  });

  testWidgets('11 queue', skip: !_enabled, (tester) async {
    await _phoneSize(tester);
    await tester.pumpWidget(
      RepaintBoundary(
        child: queue.app(
          queue.QueueKiosks(const [
            QueueRow(
              position: 1,
              sides: 12,
              printedSides: 10,
              printing: true,
              isMine: false,
              readyInMinutes: 1,
            ),
            QueueRow(
              position: 2,
              sides: 24,
              printedSides: 0,
              printing: false,
              isMine: true,
              jobCode: '482913',
              readyInMinutes: 2,
            ),
            QueueRow(
              position: 3,
              sides: 8,
              printedSides: 0,
              printing: false,
              isMine: false,
              readyInMinutes: 4,
            ),
          ]),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await _shoot(tester, '11-queue');
  });

  testWidgets('12 wallet', skip: !_enabled, (tester) async {
    await _phoneSize(tester);
    await tester.pumpWidget(
      RepaintBoundary(
        child: step4App(
          const WalletScreen(),
          overrides: step4Overrides(
            wallet: WalletState(
              wallet: const Wallet(balance: 12.4, bonusPages: 25),
              entries: [
                WalletEntry(
                  id: 'e3',
                  type: 'TopUp',
                  amount: 20,
                  bonusPages: 0,
                  balanceAfter: 12.4,
                  createdAt: DateTime(2026, 10, 8, 14, 30),
                ),
                WalletEntry(
                  id: 'e2',
                  type: 'Payment',
                  amount: -2.4,
                  bonusPages: 0,
                  balanceAfter: -7.6,
                  createdAt: DateTime(2026, 10, 8, 12, 45),
                ),
                WalletEntry(
                  id: 'e1',
                  type: 'Payment',
                  amount: -1.2,
                  bonusPages: 0,
                  balanceAfter: 10,
                  createdAt: DateTime(2026, 10, 6, 17, 20),
                ),
              ],
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await _shoot(tester, '12-wallet');
  });
}
