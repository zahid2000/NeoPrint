import '../../shared/api_error_text.dart';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/features/feature_flags.dart';
import '../../core/features/readiness.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_load_more.dart';
import '../../shared/widgets/neo_design.dart';
import 'file_row.dart';
import 'file_models.dart';
import 'files_controller.dart';
import 'file_sources.dart';
import 'heic_converter.dart';
import 'auto_delete_sheet.dart';
import '../layout/layout_launcher.dart';
import '../print_hub/print_next.dart';
import '../print_hub/print_start.dart';
import '../sharing/shares_controller.dart';

/// "Fayllarım" (plan sections 2.2, 8.2): upload from the phone or the camera,
/// the list with where each file came from, preview and delete.
class FilesScreen extends ConsumerWidget {
  const FilesScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final state = ref.watch(filesControllerProvider);
    final controller = ref.read(filesControllerProvider.notifier);

    return Scaffold(
      appBar: neoTabAppBar(
        context,
        title: l10n.navFiles,
        actions: [if (Readiness.fileSharing) const _SharesButton()],
      ),
      // The screen's one primary action (docs/DESIGN_SYSTEM.md section 4).
      floatingActionButton: state.files.isEmpty && state.uploads.isEmpty
          ? null
          : FloatingActionButton(
              tooltip: l10n.filesUpload,
              shape: const CircleBorder(),
              onPressed: () => _chooseSource(context, ref),
              child: const Icon(Icons.add),
            ),
      body: RefreshIndicator(
        onRefresh: controller.load,
        child: _body(context, ref, state),
      ),
    );
  }

  Widget _body(BuildContext context, WidgetRef ref, FilesState state) {
    final l10n = AppLocalizations.of(context);

    if (state.loading) {
      return const _SkeletonList();
    }

    if (state.error != null && state.files.isEmpty) {
      return ListView(
        padding: const EdgeInsets.all(Spacing.x4),
        children: [
          NeoBanner.error(
            message:
                '${l10n.filesLoadFailed} ${apiErrorText(l10n, state.error!)}',
          ),
          const SizedBox(height: Spacing.x4),
          OutlinedButton(
            onPressed: ref.read(filesControllerProvider.notifier).load,
            child: Text(l10n.commonRetry),
          ),
        ],
      );
    }

    if (state.files.isEmpty && state.uploads.isEmpty) {
      // Scrollable, so pull-to-refresh still works on an empty list.
      return ListView(
        children: [
          NeoEmptyState(
            icon: Icons.folder_open_outlined,
            message: l10n.filesEmpty,
            hint: l10n.filesEmptyHint,
            action: FilledButton.icon(
              onPressed: () => _chooseSource(context, ref),
              icon: const Icon(Icons.upload_file),
              label: Text(l10n.filesUpload),
            ),
          ),
        ],
      );
    }

    return _FileList(state: state);
  }

  Future<void> _chooseSource(BuildContext context, WidgetRef ref) async {
    final choice = await chooseFileSource(context);

    if (choice == null || !context.mounted) {
      return;
    }

    await addFileFrom(context, ref, choice);
  }
}

/// "Paylaşılanlar", with how many received files wait to be looked at
/// (docs/DESIGN_SYSTEM.md, "Mobil: fayl paylaşımı").
class _SharesButton extends ConsumerWidget {
  const _SharesButton();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final count = ref.watch(newSharesCountProvider);

    return IconButton(
      tooltip: AppLocalizations.of(context).sharesTitle,
      onPressed: () => context.push(Routes.shares),
      icon: Badge.count(
        count: count,
        isLabelVisible: count > 0,
        child: const Icon(Icons.move_to_inbox_outlined),
      ),
    );
  }
}

/// A file on the list: name, what it is, where it came from, and its state.
class _FileCard extends ConsumerWidget {
  const _FileCard({required this.file});

  final UserFile file;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;
    final flags = ref.watch(featureFlagsProvider);
    // Where "Çapa göndər" leads - the preview's "Çap parametrləri" (T-354).
    final next = ref.watch(printNextRouteProvider);

    return FileRow(
      file: file,
      onTap: () => _open(context),
      trailing: PopupMenuButton<String>(
        tooltip: l10n.commonMore,
        icon: Icon(Icons.more_vert, color: tokens.neutral600),
        onSelected: (choice) => switch (choice) {
          'print' => startPrint(context, ref, file),
          'prepare' => context.push(Routes.prepare, extra: file),
          'schedule' => context.push(Routes.scheduleNew, extra: file),
          'placeOnPage' => openNewLayout(context, ref, [LayoutSource(file)]),
          'editLayout' => openLayoutFile(context, ref, file),
          'share' => context.push(Routes.shareSend, extra: file),
          'autoDelete' => showAutoDeleteSheet(context, file),
          _ => _delete(context, ref),
        },
        itemBuilder: (_) => [
          if (next != null && canPrint(file))
            PopupMenuItem(
              value: 'print',
              child: ListTile(
                leading: Icon(Icons.print_outlined, color: tokens.brand500),
                title: Text(l10n.filesSendToPrint),
                contentPadding: EdgeInsets.zero,
              ),
            ),
          if (file.hasLayout && file.status == FileStatus.ready)
            PopupMenuItem(
              value: 'editLayout',
              child: ListTile(
                leading: Icon(Icons.edit_outlined, color: tokens.brand500),
                title: Text(l10n.filesEditLayout),
                contentPadding: EdgeInsets.zero,
              ),
            ),
          if (canUseInLayout(file) &&
              layoutImageExtensions.contains(file.extension))
            PopupMenuItem(
              value: 'placeOnPage',
              child: ListTile(
                leading: Icon(
                  Icons.dashboard_customize_outlined,
                  color: tokens.brand500,
                ),
                title: Text(l10n.filesPlaceOnPage),
                contentPadding: EdgeInsets.zero,
              ),
            ),
          // Without an open kiosk session "Çapa göndər" is this already.
          if (Readiness.personalQr &&
              flags.isOn(FeatureKey.userQrPrint) &&
              file.status == FileStatus.ready &&
              next != Routes.prepare)
            PopupMenuItem(
              value: 'prepare',
              child: ListTile(
                leading: Icon(
                  Icons.storefront_outlined,
                  color: tokens.brand500,
                ),
                title: Text(l10n.prepareMenu),
                contentPadding: EdgeInsets.zero,
              ),
            ),
          if (Readiness.scheduledPrint &&
              flags.isOn(FeatureKey.scheduledPrint) &&
              file.status == FileStatus.ready)
            PopupMenuItem(
              value: 'schedule',
              child: ListTile(
                leading: Icon(Icons.event_outlined, color: tokens.brand500),
                title: Text(l10n.schedMenu),
                contentPadding: EdgeInsets.zero,
              ),
            ),
          if (Readiness.fileSharing &&
              flags.isOn(FeatureKey.fileSharing) &&
              file.status == FileStatus.ready)
            PopupMenuItem(
              value: 'share',
              child: ListTile(
                leading: Icon(Icons.share_outlined, color: tokens.brand500),
                title: Text(l10n.shareMenu),
                contentPadding: EdgeInsets.zero,
              ),
            ),
          PopupMenuItem(
            value: 'autoDelete',
            child: ListTile(
              leading: Icon(Icons.auto_delete_outlined, color: tokens.brand500),
              title: Text(l10n.filesAutoDeleteMenu),
              contentPadding: EdgeInsets.zero,
            ),
          ),
          PopupMenuItem(
            value: 'delete',
            child: ListTile(
              leading: Icon(Icons.delete_outline, color: tokens.danger),
              title: Text(l10n.commonDelete),
              contentPadding: EdgeInsets.zero,
            ),
          ),
        ],
      ),
    );
  }

  /// The preview, also while the file is being prepared - it shows that
  /// and turns into the pages when ready (T-347). A failed one has its reason
  /// on the row already.
  void _open(BuildContext context) {
    if (file.status != FileStatus.failed) {
      context.push(Routes.filePreview(file.id), extra: file);
    }
  }

  Future<void> _delete(BuildContext context, WidgetRef ref) async {
    final l10n = AppLocalizations.of(context);
    final tokens = context.tokens;

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(l10n.filesDeleteTitle(file.fileName)),
        content: Text(l10n.filesDeleteExplanation),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: Text(l10n.commonCancel),
          ),
          FilledButton(
            style: FilledButton.styleFrom(
              backgroundColor: tokens.danger,
              foregroundColor: tokens.neutral0,
            ),
            onPressed: () => Navigator.pop(context, true),
            child: Text(l10n.commonDelete),
          ),
        ],
      ),
    );

    if (confirmed != true || !context.mounted) {
      return;
    }

    final problem = await ref
        .read(filesControllerProvider.notifier)
        .delete(file);

    if (context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            problem == null ? l10n.filesDeleted : apiErrorText(l10n, problem),
          ),
        ),
      );
    }
  }
}

/// Which files the tabs above the list show (T-342).
enum _FileFilter { all, pdf, image, document, scan }

/// The list with its search field and type tabs.
class _FileList extends ConsumerStatefulWidget {
  const _FileList({required this.state});

  final FilesState state;

  @override
  ConsumerState<_FileList> createState() => _FileListState();
}

class _FileListState extends ConsumerState<_FileList> {
  String _query = '';
  _FileFilter _filter = _FileFilter.all;

  bool _matches(UserFile file) {
    final query = _query.trim().toLowerCase();
    if (query.isNotEmpty && !file.fileName.toLowerCase().contains(query)) {
      return false;
    }

    final kind = fileKindOf(file.extension);
    return switch (_filter) {
      _FileFilter.all => true,
      _FileFilter.pdf => kind == FileKind.pdf,
      _FileFilter.image => kind == FileKind.image,
      _FileFilter.document => kind != FileKind.pdf && kind != FileKind.image,
      _FileFilter.scan => file.source == FileSourceKind.scan,
    };
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final files = widget.state.files.where(_matches).toList();
    final paged = widget.state.paged;
    final controller = ref.read(filesControllerProvider.notifier);
    // The filter's rows may be further down: a spinner, not "nothing
    // found", until the whole list is in (T-413).
    final more = files.isEmpty && paged.hasMore && paged.moreError == null;

    return NeoTabSwipe<_FileFilter>(
      values: _FileFilter.values,
      selected: _filter,
      onSelected: (filter) => setState(() => _filter = filter),
      child: NeoLoadMore(
        paged: paged,
        onMore: controller.loadMore,
        child: ListView(
          padding: const EdgeInsets.fromLTRB(
            Spacing.x4,
            Spacing.x2,
            Spacing.x4,
            // Room for the floating button over the last row.
            Spacing.x16 + Spacing.x8,
          ),
          children: [
            NeoSearchField(
              hint: l10n.filesSearchHint,
              onChanged: (value) => setState(() => _query = value),
            ),
            const SizedBox(height: Spacing.x3),
            NeoPillTabs<_FileFilter>(
              values: _FileFilter.values,
              selected: _filter,
              label: (filter) => switch (filter) {
                _FileFilter.all => l10n.filesFilterAll,
                _FileFilter.pdf => l10n.filesFilterPdf,
                _FileFilter.image => l10n.filesFilterImage,
                _FileFilter.document => l10n.filesFilterDocument,
                _FileFilter.scan => l10n.filesFilterScan,
              },
              onSelected: (filter) => setState(() => _filter = filter),
            ),
            const SizedBox(height: Spacing.x3),
            for (final upload in widget.state.uploads) ...[
              _UploadCard(upload: upload),
              const SizedBox(height: Spacing.x2),
            ],
            if (files.isEmpty && !paged.hasMore)
              NeoEmptyState(
                icon: Icons.search_off_outlined,
                message: l10n.filesSearchEmpty,
              )
            else
              for (final file in files) ...[
                _FileCard(file: file),
                const SizedBox(height: Spacing.x2),
              ],
            NeoLoadMoreFooter(
              paged: more ? paged.copyWith(loadingMore: true) : paged,
              onRetry: () => controller.loadMore(retry: true),
            ),
          ],
        ),
      ),
    );
  }
}

/// An upload on its way, or one that failed with the reason and a way out.
class _UploadCard extends ConsumerWidget {
  const _UploadCard({required this.upload});

  final UploadItem upload;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final text = Theme.of(context).textTheme;
    final controller = ref.read(filesControllerProvider.notifier);
    final error = upload.error;

    if (error != null) {
      final message = switch (error) {
        ServerUploadError(:final error) => apiErrorText(l10n, error),
        HeicUploadError(:final reason) =>
          reason == HeicFailure.unsupported
              ? l10n.filesHeicUnsupported
              : l10n.filesHeicFailed,
        NetworkUploadError() => l10n.filesUploadNetwork,
      };

      return NeoBanner.error(
        message: '${l10n.filesUploadFailed(upload.fileName)}. $message',
        onClose: () => controller.dismissUpload(upload.id),
      );
    }

    final percent = (upload.progress * 100).round();

    return Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    upload.fileName,
                    style: text.titleMedium,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                IconButton(
                  tooltip: l10n.filesCancelUpload,
                  onPressed: () => controller.cancelUpload(upload.id),
                  icon: const Icon(Icons.close),
                ),
              ],
            ),
            const SizedBox(height: Spacing.x2),
            LinearProgressIndicator(
              value: upload.progress == 0 ? null : upload.progress,
            ),
            const SizedBox(height: Spacing.x2),
            Text(
              upload.progress == 0
                  ? l10n.filesPreparingPhoto
                  : l10n.filesUploading(percent),
              style: text.bodySmall,
            ),
          ],
        ),
      ),
    );
  }
}

/// The list while it loads: the shape of the rows, not a spinner
/// (docs/DESIGN_SYSTEM.md section 4, "Yüklənmə").
class _SkeletonList extends StatelessWidget {
  const _SkeletonList();

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;

    return ListView.separated(
      padding: const EdgeInsets.all(Spacing.x4),
      itemCount: 4,
      separatorBuilder: (_, _) => const SizedBox(height: Spacing.x3),
      itemBuilder: (_, _) => Container(
        height: Spacing.x16 + Spacing.x8,
        decoration: BoxDecoration(
          color: tokens.neutral100,
          borderRadius: BorderRadius.circular(Radii.md),
        ),
      ),
    );
  }
}
