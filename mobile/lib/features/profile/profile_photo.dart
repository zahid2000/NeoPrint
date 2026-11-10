import 'dart:async';

import 'package:dio/dio.dart';
import 'package:file_picker/file_picker.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/network/api_client.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../files/file_models.dart';
import '../files/files_controller.dart';

/// The profile picture (T-396; docs/DECISIONS.md 2026-11-11): only its owner
/// sees it; the photo goes up as the phone took it (a HEIC one as JPEG).
class ProfilePhotoRepository {
  ProfilePhotoRepository(this._api);

  final ApiClient _api;

  /// The picture; null when there is none.
  Future<Uint8List?> get() async {
    try {
      return await _api.send<Uint8List>(
        method: 'GET',
        path: 'api/users/me/photo',
        responseType: ResponseType.bytes,
        parse: (data) => Uint8List.fromList(data as List<int>),
      );
    } on ApiException catch (error) {
      if (error.statusCode == 404) {
        return null;
      }
      rethrow;
    }
  }

  Future<void> upload({
    String? path,
    Uint8List? bytes,
    required String name,
  }) async {
    final form = FormData.fromMap({
      'photo': path != null
          ? await MultipartFile.fromFile(path, filename: name)
          : MultipartFile.fromBytes(bytes!, filename: name),
    });
    await _api.send<void>(
      method: 'PUT',
      path: 'api/users/me/photo',
      body: form,
      parse: (_) {},
    );
  }

  Future<void> remove() => _api.send<void>(
    method: 'DELETE',
    path: 'api/users/me/photo',
    parse: (_) {},
  );
}

final profilePhotoRepositoryProvider = Provider<ProfilePhotoRepository>(
  (ref) => ProfilePhotoRepository(ref.watch(apiClientProvider)),
);

/// The signed-in user's picture; read again after a change or a new account.
final myPhotoProvider = FutureProvider.autoDispose<Uint8List?>((ref) {
  ref.watch(currentUserProvider.select((user) => user?.id));
  return ref.watch(profilePhotoRepositoryProvider).get();
});

enum _PhotoChoice { camera, gallery, remove }

/// "Şəkil əlavə et" / "Şəkli dəyiş": the camera, the gallery, or taking it away.
Future<void> changeProfilePhoto(BuildContext context, WidgetRef ref) async {
  final l10n = AppLocalizations.of(context);
  final messenger = ScaffoldMessenger.of(context);
  final has = ref.read(myPhotoProvider).valueOrNull != null;

  final choice = await showModalBottomSheet<_PhotoChoice>(
    context: context,
    showDragHandle: true,
    builder: (context) => SafeArea(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (!kIsWeb)
            ListTile(
              leading: const Icon(Icons.photo_camera_outlined),
              title: Text(l10n.profilePhotoCamera),
              onTap: () => Navigator.pop(context, _PhotoChoice.camera),
            ),
          ListTile(
            leading: const Icon(Icons.photo_library_outlined),
            title: Text(l10n.profilePhotoGallery),
            onTap: () => Navigator.pop(context, _PhotoChoice.gallery),
          ),
          if (has)
            ListTile(
              leading: Icon(Icons.delete_outline, color: context.tokens.danger),
              title: Text(
                l10n.profilePhotoRemove,
                style: TextStyle(color: context.tokens.danger),
              ),
              onTap: () => Navigator.pop(context, _PhotoChoice.remove),
            ),
        ],
      ),
    ),
  );
  if (choice == null || !context.mounted) {
    return;
  }

  final repository = ref.read(profilePhotoRepositoryProvider);
  try {
    switch (choice) {
      case _PhotoChoice.remove:
        await repository.remove();
      case _PhotoChoice.camera:
        final path = await context.push<String>(Routes.filesCamera);
        if (path == null) {
          return;
        }
        await repository.upload(path: path, name: 'photo.jpg');
      case _PhotoChoice.gallery:
        final picked = await FilePicker.pickFile(type: FileType.image);
        if (picked == null) {
          return;
        }
        var path = kIsWeb ? null : picked.path;
        var name = picked.name;
        // The server takes JPEG, PNG and WebP: a HEIC photo goes as JPEG.
        if (path != null &&
            phoneConvertedExtensions.contains(extensionOf(name))) {
          path = await ref.read(heicConverterProvider).toJpeg(path);
          name = 'photo.jpg';
        }
        await repository.upload(
          path: path,
          bytes: path == null ? await picked.xFile.readAsBytes() : null,
          name: name,
        );
    }
    ref.invalidate(myPhotoProvider);
  } on ApiException catch (error) {
    messenger.showSnackBar(
      SnackBar(
        content: Text(l10n.profilePhotoFailed(apiErrorText(l10n, error))),
      ),
    );
  } on Exception {
    messenger.showSnackBar(
      SnackBar(content: Text(l10n.profilePhotoFailed(l10n.errorServer))),
    );
  }
}

/// The round picture at the top of the profile: the photo, else the
/// initials; a small camera badge says it can be changed (T-396).
class ProfileAvatar extends ConsumerWidget {
  const ProfileAvatar({super.key, required this.initials, this.radius = 32});

  final String initials;
  final double radius;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final photo = ref.watch(myPhotoProvider).valueOrNull;

    return Semantics(
      button: true,
      label: photo == null ? l10n.profilePhotoAdd : l10n.profilePhotoChange,
      child: InkWell(
        customBorder: const CircleBorder(),
        onTap: () => unawaited(changeProfilePhoto(context, ref)),
        child: Stack(
          clipBehavior: Clip.none,
          children: [
            CircleAvatar(
              radius: radius,
              backgroundColor: tokens.brand100,
              backgroundImage: photo == null ? null : MemoryImage(photo),
              child: photo == null
                  ? Text(
                      initials,
                      style: theme.textTheme.titleLarge?.copyWith(
                        color: tokens.brand500,
                        fontWeight: FontWeights.semibold,
                      ),
                    )
                  : null,
            ),
            Positioned(
              right: -Spacing.x1,
              bottom: -Spacing.x1,
              child: Container(
                padding: const EdgeInsets.all(Spacing.x1),
                decoration: BoxDecoration(
                  color: tokens.brand500,
                  shape: BoxShape.circle,
                  border: Border.all(color: tokens.neutral0, width: 2),
                ),
                child: Icon(
                  Icons.photo_camera,
                  size: Spacing.x4,
                  color: tokens.neutral0,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
