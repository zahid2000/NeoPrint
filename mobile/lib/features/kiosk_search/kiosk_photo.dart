import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/theme/tokens.dart';

/// A kiosk's picture, fetched once per version through the API client (it
/// needs the signed-in user's token) and kept while the app runs - the URL
/// carries the version, so a new picture is a new entry (T-340).
final kioskPhotoProvider = FutureProvider.family<Uint8List?, String>((
  ref,
  url,
) async {
  try {
    return await ref
        .watch(apiClientProvider)
        .send<Uint8List>(
          method: 'GET',
          path: url,
          responseType: ResponseType.bytes,
          parse: (data) => Uint8List.fromList(data as List<int>),
        );
  } on Object {
    // No picture to show is the shop icon, not an error on the card.
    return null;
  }
});

/// A kiosk's picture, or - with none, still loading or failing - a shop icon
/// on the brand's light background: never an empty box or a broken image
/// (docs/DESIGN_SYSTEM.md, "Kiosk şəkli", T-340).
class KioskPhoto extends ConsumerWidget {
  const KioskPhoto({this.url, this.size = 72, super.key});

  /// Where the picture is, relative to the API; null when the kiosk has none.
  final String? url;
  final double size;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final tokens = context.tokens;
    final placeholder = ColoredBox(
      color: tokens.brand50,
      child: Center(
        child: Icon(
          Icons.storefront_outlined,
          size: size / 2,
          color: tokens.brand500,
        ),
      ),
    );
    final bytes = url == null
        ? null
        : ref.watch(kioskPhotoProvider(url!)).valueOrNull;

    return ClipRRect(
      borderRadius: BorderRadius.circular(Radii.sm),
      child: SizedBox.square(
        dimension: size,
        child: bytes == null
            ? placeholder
            : Image.memory(
                bytes,
                fit: BoxFit.cover,
                // Decoded at the size it is shown, not the photo's own.
                cacheWidth: (size * MediaQuery.devicePixelRatioOf(context))
                    .round(),
                gaplessPlayback: true,
                errorBuilder: (_, __, ___) => placeholder,
              ),
      ),
    );
  }
}
