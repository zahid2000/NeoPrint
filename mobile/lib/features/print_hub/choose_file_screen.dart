import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_design.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../files/file_models.dart';
import '../files/file_row.dart';
import '../files/file_sources.dart';
import '../files/files_controller.dart';

/// "Fayllarımdan seç" in the Print tab (T-347, T-348): the files ready to
/// print, inside the Print tab - back returns to it - and a tap opens the
/// file's preview with its print settings next. A new file is uploaded here
/// (T-353) and, once ready, opens its print settings.
class ChooseFileScreen extends ConsumerStatefulWidget {
  const ChooseFileScreen({super.key});

  @override
  ConsumerState<ChooseFileScreen> createState() => _ChooseFileScreenState();
}

class _ChooseFileScreenState extends ConsumerState<ChooseFileScreen> {
  String _query = '';

  Future<void> _upload() async {
    final source = await chooseFileSource(context);
    if (source != null && mounted) {
      await addFileFrom(context, ref, source, thenPrint: true);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final state = ref.watch(filesControllerProvider);
    final query = _query.trim().toLowerCase();
    final ready = [
      for (final file in state.files)
        if (file.status == FileStatus.ready &&
            (query.isEmpty || file.fileName.toLowerCase().contains(query)))
          file,
    ];

    return Scaffold(
      appBar: neoTabAppBar(
        context,
        title: l10n.printHubFromFiles,
        showBack: true,
      ),
      body: state.loading && state.files.isEmpty
          ? const Center(child: CircularProgressIndicator())
          : ListView(
              padding: const EdgeInsets.all(Spacing.x4),
              children: [
                // T-353: a new file comes in here, not in the Print tab; once
                // it is ready its print settings open.
                if (ready.isNotEmpty || query.isNotEmpty) ...[
                  OutlinedButton.icon(
                    onPressed: _upload,
                    icon: const Icon(Icons.upload_file),
                    label: Text(l10n.filesUpload),
                  ),
                  const SizedBox(height: Spacing.x3),
                ],
                NeoSearchField(
                  hint: l10n.filesSearchHint,
                  onChanged: (value) => setState(() => _query = value),
                ),
                const SizedBox(height: Spacing.x3),
                if (ready.isEmpty)
                  NeoEmptyState(
                    icon: Icons.description_outlined,
                    message: query.isEmpty
                        ? l10n.pickFileEmpty
                        : l10n.filesSearchEmpty,
                    hint: query.isEmpty ? l10n.pickFileEmptyHint : null,
                    action: query.isEmpty
                        ? FilledButton.icon(
                            onPressed: _upload,
                            icon: const Icon(Icons.upload_file),
                            label: Text(l10n.filesUpload),
                          )
                        : null,
                  )
                else
                  for (final file in ready) ...[
                    FileRow(
                      file: file,
                      onTap: () => context.push(
                        Routes.filePreview(file.id),
                        extra: file,
                      ),
                    ),
                    const SizedBox(height: Spacing.x2),
                  ],
              ],
            ),
    );
  }
}
