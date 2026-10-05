import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_client.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_primary_button.dart';
import '../files/file_models.dart';
import '../files/files_controller.dart';
import 'layout_editing.dart';
import 'layout_editor_screen.dart';
import 'layout_model.dart';
import 'layout_repository.dart';

/// Formats a layout takes as a picture as they are; anything else is used
/// page by page (plan section 2.11: "PDF səhifələri şəkil kimi").
const layoutImageExtensions = {'jpg', 'jpeg', 'png', 'bmp', 'gif', 'webp'};

/// Whether a file can go into a layout: ready, and not a saved scan
/// (docs/DECISIONS.md 2026-10-05).
bool canUseInLayout(UserFile file) =>
    file.status == FileStatus.ready && !file.editLocked && file.pageCount > 0;

/// One picture for a new layout: an image file, or one page of any file.
final class LayoutSource {
  const LayoutSource(this.file, [this.page]);

  final UserFile file;

  /// One-based; null for an image file used as it is.
  final int? page;

  String get name => page == null ? 'file:${file.id}' : 'file:${file.id}:$page';
}

/// Opens the editor on pictures from the user's files and, when saved,
/// tells the user (plan section 2.11, "Yeni tərtibat" / "Səhifədə yerləşdir").
Future<void> openNewLayout(
  BuildContext context,
  WidgetRef ref,
  List<LayoutSource> sources,
) async {
  final l10n = AppLocalizations.of(context);
  final messenger = ScaffoldMessenger.of(context);
  final navigator = Navigator.of(context, rootNavigator: true);
  final files = ref.read(filesRepositoryProvider);

  final loaded = await _withProgress(context, () async {
    final images = <String, Uint8List>{};
    for (final source in sources) {
      images[source.name] = await files.pageImage(
        source.file.id,
        source.page ?? 1,
        960,
      );
    }
    final pictures = {
      for (final source in sources)
        source.name: EditorPicture(
          name: source.name,
          aspect: await _aspect(images[source.name]!),
        ),
    };
    return (images, pictures);
  });

  if (loaded == null || !context.mounted) {
    return;
  }

  final (images, pictures) = loaded;

  var project = const LayoutProject(marginMm: 10, pages: [LayoutPage([])]);
  for (final source in sources) {
    project = LayoutEditing.add(project, 0, pictures[source.name]!);
  }

  final saved = await navigator.push<UserFile>(
    MaterialPageRoute(
      fullscreenDialog: true,
      builder: (_) => LayoutEditorScreen(
        initial: project,
        pictures: pictures,
        images: images,
        mode: LayoutEditorMode.newFile,
        title: sources.length == 1 ? _stem(sources.single.file.fileName) : null,
      ),
    ),
  );

  if (saved != null) {
    messenger.showSnackBar(SnackBar(content: Text(l10n.layoutSaved)));
  }
}

/// Opens a layout file again ("Tərtibatı redaktə et"); a save is a new version.
Future<void> openLayoutFile(
  BuildContext context,
  WidgetRef ref,
  UserFile file,
) async {
  final l10n = AppLocalizations.of(context);
  final messenger = ScaffoldMessenger.of(context);
  final navigator = Navigator.of(context, rootNavigator: true);
  final repository = ref.read(layoutRepositoryProvider);

  final loaded = await _withProgress(context, () async {
    final opened = await repository.open(file.id);
    final images = <String, Uint8List>{
      for (final name in opened.assets)
        name: await repository.asset(file.id, name),
    };
    final pictures = {
      for (final MapEntry(key: name, value: bytes) in images.entries)
        name: EditorPicture(name: name, aspect: await _aspect(bytes)),
    };
    return (opened.project, images, pictures);
  });

  if (loaded == null || !context.mounted) {
    return;
  }

  final (project, images, pictures) = loaded;

  final saved = await navigator.push<UserFile>(
    MaterialPageRoute(
      fullscreenDialog: true,
      builder: (_) => LayoutEditorScreen(
        initial: project,
        pictures: pictures,
        images: images,
        mode: LayoutEditorMode.editFile,
        fileId: file.id,
        title: _stem(file.fileName),
      ),
    ),
  );

  if (saved != null) {
    messenger.showSnackBar(SnackBar(content: Text(l10n.layoutSaved)));
  }
}

/// Runs [load] behind a progress dialog; null, with the reason shown, on failure.
Future<T?> _withProgress<T>(
  BuildContext context,
  Future<T> Function() load,
) async {
  final l10n = AppLocalizations.of(context);
  final messenger = ScaffoldMessenger.of(context);
  final navigator = Navigator.of(context, rootNavigator: true);

  showDialog<void>(
    context: context,
    barrierDismissible: false,
    builder: (_) => const Center(child: CircularProgressIndicator()),
  );

  try {
    return await load();
  } on ApiException catch (error) {
    messenger.showSnackBar(
      SnackBar(content: Text(l10n.layoutOpenFailed(apiErrorText(l10n, error)))),
    );
    return null;
  } on Exception {
    // A picture the phone could not decode: say so rather than nothing.
    messenger.showSnackBar(
      SnackBar(content: Text(l10n.layoutOpenFailed(l10n.errorServer))),
    );
    return null;
  } finally {
    navigator.pop();
  }
}

/// Width over height of an encoded picture. Through a codec, which the web
/// build has too (ui.ImageDescriptor is not implemented there).
Future<double> _aspect(Uint8List bytes) async {
  final codec = await ui.instantiateImageCodec(bytes);
  final image = (await codec.getNextFrame()).image;
  final aspect = image.width / image.height;
  image.dispose();
  codec.dispose();
  return aspect;
}

String _stem(String fileName) {
  final dot = fileName.lastIndexOf('.');
  return dot <= 0 ? fileName : fileName.substring(0, dot);
}

/// "Yeni tərtibat": choose the files - and for a document, the page - that
/// go into a new layout.
class LayoutSourcesScreen extends ConsumerStatefulWidget {
  const LayoutSourcesScreen({super.key});

  @override
  ConsumerState<LayoutSourcesScreen> createState() =>
      _LayoutSourcesScreenState();
}

class _LayoutSourcesScreenState extends ConsumerState<LayoutSourcesScreen> {
  /// Chosen files, with the page for a document.
  final Map<String, int?> _chosen = {};

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;
    final files = ref
        .watch(filesControllerProvider)
        .files
        .where(canUseInLayout)
        .toList();

    return Scaffold(
      appBar: AppBar(title: Text(l10n.layoutPickTitle)),
      body: files.isEmpty
          ? NeoEmptyState(
              icon: Icons.dashboard_customize_outlined,
              message: l10n.layoutPickEmpty,
              hint: l10n.layoutPickEmptyHint,
            )
          : ListView(
              padding: const EdgeInsets.all(Spacing.x4),
              children: [
                for (final file in files)
                  Card(
                    margin: const EdgeInsets.only(bottom: Spacing.x2),
                    child: Column(
                      children: [
                        CheckboxListTile(
                          value: _chosen.containsKey(file.id),
                          title: Text(file.fileName),
                          subtitle: Text(l10n.filesPageCount(file.pageCount)),
                          onChanged: (on) => setState(() {
                            if (on ?? false) {
                              _chosen[file.id] =
                                  layoutImageExtensions.contains(file.extension)
                                  ? null
                                  : 1;
                            } else {
                              _chosen.remove(file.id);
                            }
                          }),
                        ),
                        if (_chosen[file.id] case final page?
                            when file.pageCount > 1)
                          Padding(
                            padding: const EdgeInsets.fromLTRB(
                              Spacing.x4,
                              0,
                              Spacing.x4,
                              Spacing.x2,
                            ),
                            child: Row(
                              children: [
                                Text(
                                  l10n.layoutPickPage,
                                  style: TextStyle(color: tokens.neutral600),
                                ),
                                const Spacer(),
                                IconButton(
                                  onPressed: page > 1
                                      ? () => setState(
                                          () => _chosen[file.id] = page - 1,
                                        )
                                      : null,
                                  icon: const Icon(Icons.remove),
                                ),
                                Text('$page / ${file.pageCount}'),
                                IconButton(
                                  onPressed: page < file.pageCount
                                      ? () => setState(
                                          () => _chosen[file.id] = page + 1,
                                        )
                                      : null,
                                  icon: const Icon(Icons.add),
                                ),
                              ],
                            ),
                          ),
                      ],
                    ),
                  ),
              ],
            ),
      bottomNavigationBar: files.isEmpty
          ? null
          : SafeArea(
              child: Padding(
                padding: const EdgeInsets.all(Spacing.x4),
                child: NeoPrimaryButton(
                  label: l10n.layoutPickContinue,
                  onPressed: _chosen.isEmpty
                      ? null
                      : () => Navigator.pop(context, [
                          for (final file in files)
                            if (_chosen.containsKey(file.id))
                              LayoutSource(file, _chosen[file.id]),
                        ]),
                ),
              ),
            ),
    );
  }
}
