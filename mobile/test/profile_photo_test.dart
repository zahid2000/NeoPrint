import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:image/image.dart' as img;
import 'package:neoprint/core/theme/app_theme.dart';
import 'package:neoprint/features/profile/profile_photo.dart';
import 'package:neoprint/l10n/app_localizations.dart';

/// T-396: the profile picture, else the initials; a tap offers the camera,
/// the gallery and - when there is one - removing it.
void main() {
  Widget app(Uint8List? photo) => ProviderScope(
    overrides: [myPhotoProvider.overrideWith((ref) async => photo)],
    child: MaterialApp(
      theme: AppTheme.light(),
      locale: const Locale('az'),
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: const Scaffold(
        body: Center(child: ProfileAvatar(initials: 'ZM')),
      ),
    ),
  );

  testWidgets('no picture: the initials, and no "remove"', (tester) async {
    await tester.pumpWidget(app(null));
    await tester.pumpAndSettle();
    expect(find.text('ZM'), findsOneWidget);

    await tester.tap(find.byType(ProfileAvatar));
    await tester.pumpAndSettle();
    expect(find.text('Şəkil çək'), findsOneWidget);
    expect(find.text('Qalereyadan seç'), findsOneWidget);
    expect(find.text('Şəkli sil'), findsNothing);
  });

  testWidgets('a picture: shown instead of the initials, and can be removed', (
    tester,
  ) async {
    final png = Uint8List.fromList(
      img.encodePng(img.Image(width: 4, height: 4)),
    );
    await tester.pumpWidget(app(png));
    await tester.pumpAndSettle();
    expect(find.text('ZM'), findsNothing);

    await tester.tap(find.byType(ProfileAvatar));
    await tester.pumpAndSettle();
    expect(find.text('Şəkli sil'), findsOneWidget);
  });
}
