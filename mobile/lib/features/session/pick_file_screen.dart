import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../files/file_models.dart';
import '../files/files_controller.dart';

/// "Faylı seçin" (docs/DESIGN_SYSTEM.md, "Mobil: sessiya ilə çap"): the
/// files ready to print, one tap picks one for the session's kiosk. A file
/// still being converted is not listed rather than listed and unusable.
class PickFileScreen extends ConsumerWidget {
  const PickFileScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final state = ref.watch(filesControllerProvider);
    final ready = [
      for (final file in state.files)
        if (file.status == FileStatus.ready) file,
    ];

    return Scaffold(
      appBar: AppBar(title: Text(l10n.pickFileTitle)),
      body: state.loading && state.files.isEmpty
          ? const Center(child: CircularProgressIndicator())
          : ready.isEmpty
          ? NeoEmptyState(
              icon: Icons.description_outlined,
              message: l10n.pickFileEmpty,
              hint: l10n.pickFileEmptyHint,
              action: OutlinedButton(
                onPressed: () => context.go(Routes.files),
                child: Text(l10n.pickFileUpload),
              ),
            )
          : ListView.separated(
              padding: const EdgeInsets.all(Spacing.x4),
              itemCount: ready.length,
              separatorBuilder: (_, __) => const SizedBox(height: Spacing.x2),
              itemBuilder: (_, index) {
                final file = ready[index];

                return Card(
                  margin: EdgeInsets.zero,
                  clipBehavior: Clip.antiAlias,
                  child: ListTile(
                    leading: const Icon(Icons.description_outlined),
                    title: Text(
                      file.fileName,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                    ),
                    subtitle: Text(
                      '${file.extension.toUpperCase()} · ${l10n.filesPageCount(file.pageCount)}',
                    ),
                    trailing: const Icon(Icons.chevron_right),
                    onTap: () => context.pushReplacement(
                      Routes.sessionPrint,
                      extra: file,
                    ),
                  ),
                );
              },
            ),
    );
  }
}
