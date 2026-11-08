import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';
import 'neo_icon_tile.dart';

/// The pieces the screens of the new look share (T-342; docs/DESIGN_SYSTEM.md,
/// "Mobil: yeni vizual dil"): a tab's big title, a section header with its
/// "Hamısına bax", the search field, the pill tabs and the file-type square.

/// A tab's own AppBar: the title large and to the left, as in the design.
PreferredSizeWidget neoTabAppBar(
  BuildContext context, {
  required String title,
  List<Widget> actions = const [],
  bool showBack = false,
}) {
  final theme = Theme.of(context);

  return AppBar(
    automaticallyImplyLeading: showBack,
    centerTitle: false,
    titleSpacing: showBack ? 0 : Spacing.x4,
    title: Text(
      title,
      style: theme.textTheme.headlineSmall?.copyWith(
        fontWeight: FontWeights.semibold,
      ),
    ),
    actions: [
      ...actions,
      const SizedBox(width: Spacing.x2),
    ],
  );
}

/// "Aktiv sifariş ............ Hamısına bax →".
class NeoSectionHeader extends StatelessWidget {
  const NeoSectionHeader({
    required this.title,
    this.actionLabel,
    this.onAction,
    super.key,
  });

  final String title;
  final String? actionLabel;
  final VoidCallback? onAction;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Padding(
      padding: const EdgeInsets.only(bottom: Spacing.x2),
      child: Row(
        children: [
          Expanded(
            child: Text(
              title,
              style: theme.textTheme.titleMedium?.copyWith(
                fontWeight: FontWeights.semibold,
              ),
            ),
          ),
          if (actionLabel != null && onAction != null)
            TextButton(
              onPressed: onAction,
              style: TextButton.styleFrom(
                visualDensity: VisualDensity.compact,
                padding: const EdgeInsets.symmetric(horizontal: Spacing.x2),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(actionLabel!),
                  const SizedBox(width: Spacing.x1),
                  const Icon(Icons.arrow_forward, size: 16),
                ],
              ),
            ),
        ],
      ),
    );
  }
}

/// The rounded search field at the top of a list.
class NeoSearchField extends StatelessWidget {
  const NeoSearchField({
    required this.hint,
    required this.onChanged,
    this.controller,
    super.key,
  });

  final String hint;
  final ValueChanged<String> onChanged;
  final TextEditingController? controller;

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;

    return TextField(
      controller: controller,
      onChanged: onChanged,
      textInputAction: TextInputAction.search,
      decoration: InputDecoration(
        hintText: hint,
        prefixIcon: Icon(Icons.search, color: tokens.neutral400),
        filled: true,
        fillColor: tokens.neutral100,
        isDense: true,
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(Radii.md),
          borderSide: BorderSide.none,
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(Radii.md),
          borderSide: BorderSide.none,
        ),
      ),
    );
  }
}

/// Pill tabs: "Hamısı · PDF · Şəkil", "Yaxınlıqdakı · Hamısı · Xəritə". The
/// chosen one is on the brand's light fill in the brand's colour.
class NeoPillTabs<T> extends StatelessWidget {
  const NeoPillTabs({
    required this.values,
    required this.selected,
    required this.label,
    required this.onSelected,
    this.expand = false,
    super.key,
  });

  final List<T> values;
  final T selected;
  final String Function(T value) label;
  final ValueChanged<T> onSelected;

  /// Each tab takes an equal share of the width, instead of scrolling.
  final bool expand;

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;
    final theme = Theme.of(context);

    Widget pill(T value) {
      final chosen = value == selected;

      return Semantics(
        selected: chosen,
        button: true,
        child: Material(
          color: chosen ? tokens.brand50 : tokens.brand50.withAlpha(0),
          borderRadius: BorderRadius.circular(Radii.full),
          child: InkWell(
            borderRadius: BorderRadius.circular(Radii.full),
            onTap: () => onSelected(value),
            child: Container(
              constraints: const BoxConstraints(
                minHeight: Sizes.touchTarget - Spacing.x2,
              ),
              alignment: Alignment.center,
              padding: const EdgeInsets.symmetric(horizontal: Spacing.x3),
              child: Text(
                label(value),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: theme.textTheme.labelLarge?.copyWith(
                  color: chosen ? tokens.brand500 : tokens.neutral600,
                  fontWeight: chosen
                      ? FontWeights.semibold
                      : FontWeights.medium,
                ),
              ),
            ),
          ),
        ),
      );
    }

    if (expand) {
      return Row(
        children: [for (final value in values) Expanded(child: pill(value))],
      );
    }

    // Every tab is built (a short row): scrolled sideways on a narrow phone.
    return SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      child: Row(
        children: [
          for (var i = 0; i < values.length; i++) ...[
            if (i > 0) const SizedBox(width: Spacing.x1),
            pill(values[i]),
          ],
        ],
      ),
    );
  }
}

/// What kind of document a file is, by its extension - its square's colour
/// and icon on every list (T-342).
enum FileKind { pdf, word, sheet, slides, image, text, other }

FileKind fileKindOf(String extension) => switch (extension.toLowerCase()) {
  'pdf' => FileKind.pdf,
  'doc' || 'docx' || 'odt' || 'rtf' || 'dotx' => FileKind.word,
  'xls' || 'xlsx' || 'xlsm' || 'xltx' || 'ods' || 'csv' => FileKind.sheet,
  'ppt' || 'pptx' || 'pps' || 'ppsx' || 'potx' || 'odp' => FileKind.slides,
  'jpg' ||
  'jpeg' ||
  'png' ||
  'bmp' ||
  'gif' ||
  'webp' ||
  'heic' ||
  'tif' ||
  'tiff' => FileKind.image,
  'txt' => FileKind.text,
  _ => FileKind.other,
};

/// The square at the start of a file's row: red for PDF, blue for Word,
/// green for a sheet, orange for slides, violet for a picture.
class FileTypeTile extends StatelessWidget {
  const FileTypeTile({required this.extension, super.key});

  final String extension;

  @override
  Widget build(BuildContext context) {
    final (icon, tone) = switch (fileKindOf(extension)) {
      FileKind.pdf => (Icons.picture_as_pdf_outlined, IconTileTone.danger),
      FileKind.word => (Icons.description_outlined, IconTileTone.brand),
      FileKind.sheet => (Icons.table_chart_outlined, IconTileTone.success),
      FileKind.slides => (Icons.slideshow_outlined, IconTileTone.warning),
      FileKind.image => (Icons.image_outlined, IconTileTone.accent),
      FileKind.text => (Icons.notes_outlined, IconTileTone.info),
      FileKind.other => (Icons.insert_drive_file_outlined, IconTileTone.info),
    };

    return NeoIconTile(icon: icon, tone: tone);
  }
}

/// A soft brand card: the nearest kiosk and the wallet's balance sit on it.
BoxDecoration neoHeroDecoration(BuildContext context) {
  final tokens = context.tokens;

  return BoxDecoration(
    gradient: LinearGradient(
      begin: Alignment.topLeft,
      end: Alignment.bottomRight,
      colors: [tokens.brand50, tokens.brand100],
    ),
    borderRadius: BorderRadius.circular(Radii.lg),
  );
}
