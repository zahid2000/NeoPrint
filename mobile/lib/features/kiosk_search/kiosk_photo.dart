import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';

/// A kiosk's picture, or - with none, still loading or failing - a shop icon
/// on the brand's light background: never an empty box or a broken image
/// (docs/DESIGN_SYSTEM.md, "Kiosk şəkli", T-340).
class KioskPhoto extends StatelessWidget {
  const KioskPhoto({this.url, this.size = 72, super.key});

  /// Where the picture is; null when the kiosk has none.
  final String? url;
  final double size;

  @override
  Widget build(BuildContext context) {
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

    return ClipRRect(
      borderRadius: BorderRadius.circular(Radii.sm),
      child: SizedBox.square(
        dimension: size,
        child: url == null
            ? placeholder
            : Image.network(
                url!,
                fit: BoxFit.cover,
                // Decoded at the size it is shown, not the photo's own.
                cacheWidth: (size * MediaQuery.devicePixelRatioOf(context))
                    .round(),
                loadingBuilder: (_, child, progress) =>
                    progress == null ? child : placeholder,
                errorBuilder: (_, __, ___) => placeholder,
              ),
      ),
    );
  }
}
