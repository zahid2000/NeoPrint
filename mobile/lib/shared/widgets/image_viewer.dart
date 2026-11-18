import 'package:flutter/foundation.dart';
import 'package:material_ui/material_ui.dart';

import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';

/// A picture looked at full screen (T-446): pinch to zoom, swipe between
/// several, the close button or back to leave. [label] names picture `n`
/// for screen readers.
Future<void> showImageViewer(
  BuildContext context, {
  required List<Uint8List> images,
  int initial = 0,
  required String Function(int n) label,
}) => showDialog<void>(
  context: context,
  useSafeArea: false,
  builder: (_) => ImageViewer(images: images, initial: initial, label: label),
);

class ImageViewer extends StatefulWidget {
  const ImageViewer({
    super.key,
    required this.images,
    this.initial = 0,
    required this.label,
  });

  final List<Uint8List> images;
  final int initial;
  final String Function(int n) label;

  @override
  State<ImageViewer> createState() => _ImageViewerState();
}

class _ImageViewerState extends State<ImageViewer> {
  late final _pages = PageController(initialPage: widget.initial);
  late int _index = widget.initial;

  @override
  void dispose() {
    _pages.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final count = widget.images.length;

    return Dialog.fullscreen(
      backgroundColor: LightPalette.neutral900,
      child: Stack(
        children: [
          PageView.builder(
            controller: _pages,
            itemCount: count,
            onPageChanged: (index) => setState(() => _index = index),
            itemBuilder: (_, index) => InteractiveViewer(
              maxScale: 5,
              child: Center(
                child: Image.memory(
                  widget.images[index],
                  fit: BoxFit.contain,
                  semanticLabel: widget.label(index + 1),
                ),
              ),
            ),
          ),
          SafeArea(
            child: Row(
              children: [
                if (count > 1)
                  Padding(
                    padding: const EdgeInsetsDirectional.only(
                      start: Spacing.x4,
                    ),
                    child: Text(
                      '${_index + 1} / $count',
                      style: Theme.of(context).textTheme.titleMedium
                          ?.copyWith(color: LightPalette.neutral0),
                    ),
                  ),
                const Spacer(),
                IconButton(
                  tooltip: l10n.commonClose,
                  color: LightPalette.neutral0,
                  onPressed: () => Navigator.of(context).pop(),
                  icon: const Icon(Icons.close),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
