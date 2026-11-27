import 'dart:async';
import 'dart:io';

import 'package:camera/camera.dart';
import 'package:material_ui/material_ui.dart';

import '../../core/platform/camera_choice.dart';

import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_empty_state.dart';

/// Takes a photo to upload (plan section 8.2: "kamera") - or several, when
/// [multiple] (T-466).
///
/// Pops with the photo's path - with the list of paths when [multiple] - or
/// with nothing when the user goes back. The document scanner with its edge
/// detection is a separate screen (plan section 2.11); this is the plain
/// camera.
class CameraCaptureScreen extends StatefulWidget {
  const CameraCaptureScreen({super.key, this.multiple = false});

  final bool multiple;

  /// The most photos one go takes: as many as one layout takes from files.
  static const maxPhotos = 24;

  @override
  State<CameraCaptureScreen> createState() => _CameraCaptureScreenState();
}

enum _CameraProblem { noCamera, permission, failed }

class _CameraCaptureScreenState extends State<CameraCaptureScreen>
    with WidgetsBindingObserver {
  CameraController? _controller;
  _CameraProblem? _problem;
  bool _capturing = false;

  /// The photos taken so far, when several are taken.
  final _photos = <String>[];

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _open();
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _controller?.dispose();
    super.dispose();
  }

  /// The camera is released while the app is in the background, as the
  /// platforms expect, and opened again on return.
  @override
  void didChangeAppLifecycleState(AppLifecycleState lifecycle) {
    final controller = _controller;

    if (controller == null || !controller.value.isInitialized) {
      return;
    }

    if (lifecycle == AppLifecycleState.inactive) {
      controller.dispose();
      _controller = null;
    } else if (lifecycle == AppLifecycleState.resumed) {
      _open();
    }
  }

  Future<void> _open() async {
    try {
      final cameras = await availableCameras();

      if (cameras.isEmpty) {
        setState(() => _problem = _CameraProblem.noCamera);
        return;
      }

      // The main back camera, not an ultra-wide or macro one listed first (T-346).
      final backs = backCamerasMainFirst(cameras);
      final back = backs.isEmpty ? cameras.first : backs.first;

      final controller = CameraController(
        back,
        ResolutionPreset.high,
        enableAudio: false,
        imageFormatGroup: ImageFormatGroup.jpeg,
      );

      await controller.initialize();

      if (!mounted) {
        await controller.dispose();
        return;
      }

      setState(() {
        _controller = controller;
        _problem = null;
      });
    } on CameraException catch (error) {
      setState(
        () => _problem = error.code.contains('Access')
            ? _CameraProblem.permission
            : _CameraProblem.failed,
      );
    }
  }

  Future<void> _capture() async {
    final controller = _controller;

    if (controller == null || _capturing) {
      return;
    }

    setState(() => _capturing = true);

    try {
      final photo = await controller.takePicture();

      if (!mounted) {
        return;
      }
      if (widget.multiple) {
        setState(() {
          _photos.add(photo.path);
          _capturing = false;
        });
      } else {
        Navigator.of(context).pop(photo.path);
      }
    } on CameraException {
      if (mounted) {
        setState(() {
          _capturing = false;
          _problem = _CameraProblem.failed;
        });
      }
    }
  }

  /// Going back with photos taken asks first: they would be lost.
  Future<void> _leave() async {
    final l10n = AppLocalizations.of(context);
    final discard = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(l10n.cameraDiscardTitle(_photos.length)),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, true),
            child: Text(l10n.cameraDiscard),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, false),
            child: Text(l10n.cameraKeepShooting),
          ),
        ],
      ),
    );
    if ((discard ?? false) && mounted) {
      _photos.clear();
      Navigator.of(context).pop();
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final controller = _controller;

    return PopScope(
      canPop: _photos.isEmpty,
      onPopInvokedWithResult: (popped, _) {
        if (!popped) {
          unawaited(_leave());
        }
      },
      child: _scaffold(l10n, controller),
    );
  }

  Widget _scaffold(AppLocalizations l10n, CameraController? controller) {
    final full = _photos.length >= CameraCaptureScreen.maxPhotos;

    return Scaffold(
      appBar: AppBar(title: Text(l10n.cameraTitle)),
      body: switch (_problem) {
        _CameraProblem.noCamera => NeoEmptyState(
          icon: Icons.no_photography_outlined,
          message: l10n.cameraNoCamera,
        ),
        _CameraProblem.permission => NeoEmptyState(
          icon: Icons.no_photography_outlined,
          message: l10n.cameraPermission,
          action: OutlinedButton(
            onPressed: _open,
            child: Text(l10n.commonRetry),
          ),
        ),
        _CameraProblem.failed => NeoEmptyState(
          icon: Icons.error_outline,
          message: l10n.cameraFailed,
          action: OutlinedButton(
            onPressed: _open,
            child: Text(l10n.commonRetry),
          ),
        ),
        null when controller == null || !controller.value.isInitialized =>
          const Center(child: CircularProgressIndicator()),
        null => Column(
          children: [
            Expanded(child: Center(child: CameraPreview(controller!))),
            if (widget.multiple && _photos.isNotEmpty) _strip(l10n),
            if (widget.multiple && full)
              Padding(
                padding: const EdgeInsets.fromLTRB(
                  Spacing.x6,
                  Spacing.x3,
                  Spacing.x6,
                  0,
                ),
                child: Text(
                  l10n.cameraFull(CameraCaptureScreen.maxPhotos),
                  textAlign: TextAlign.center,
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ),
            CameraShutterRow(
              multiple: widget.multiple,
              capturing: _capturing,
              full: full,
              onCapture: _capture,
              onDone: _photos.isEmpty
                  ? null
                  : () => Navigator.of(context).pop(List<String>.of(_photos)),
            ),
          ],
        ),
      },
    );
  }

  /// The photos taken, newest last; each can be dropped.
  Widget _strip(AppLocalizations l10n) {
    final tokens = context.tokens;
    const size = 64.0;

    return Padding(
      padding: const EdgeInsets.fromLTRB(Spacing.x4, Spacing.x3, Spacing.x4, 0),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            l10n.cameraCount(_photos.length),
            style: Theme.of(context).textTheme.labelMedium,
          ),
          const SizedBox(height: Spacing.x2),
          SizedBox(
            height: size,
            child: ListView.separated(
              scrollDirection: Axis.horizontal,
              itemCount: _photos.length,
              separatorBuilder: (_, __) => const SizedBox(width: Spacing.x2),
              itemBuilder: (_, index) => Stack(
                children: [
                  ClipRRect(
                    borderRadius: BorderRadius.circular(Radii.sm),
                    child: Image.file(
                      File(_photos[index]),
                      width: size,
                      height: size,
                      fit: BoxFit.cover,
                      cacheWidth: 192,
                      errorBuilder: (_, __, ___) => Container(
                        width: size,
                        height: size,
                        color: tokens.neutral200,
                      ),
                    ),
                  ),
                  PositionedDirectional(
                    top: 0,
                    end: 0,
                    child: Material(
                      color: tokens.neutral900.withValues(alpha: 0.6),
                      shape: const CircleBorder(),
                      child: InkWell(
                        key: ValueKey('cameraDrop-$index'),
                        customBorder: const CircleBorder(),
                        onTap: () => setState(() => _photos.removeAt(index)),
                        child: Padding(
                          padding: const EdgeInsets.all(Spacing.x1),
                          child: Icon(
                            Icons.close,
                            size: Spacing.x4,
                            color: tokens.neutral0,
                            semanticLabel: l10n.cameraDrop,
                          ),
                        ),
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// The shutter and, taking several, "Hazırdır" beside it (T-466).
///
/// Both buttons are Expanded: the theme's buttons are full width, and an
/// unbounded one in a row laid nothing out - the shutter went with it
/// (T-470).
class CameraShutterRow extends StatelessWidget {
  const CameraShutterRow({
    required this.multiple,
    required this.capturing,
    required this.full,
    required this.onCapture,
    required this.onDone,
    super.key,
  });

  final bool multiple;
  final bool capturing;
  final bool full;
  final VoidCallback onCapture;

  /// Null while there is nothing to hand back.
  final VoidCallback? onDone;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return Padding(
      padding: const EdgeInsets.all(Spacing.x6),
      child: Row(
        children: [
          Expanded(
            child: SizedBox(
              height: Sizes.controlHeight,
              child: FilledButton.icon(
                key: const ValueKey('cameraCapture'),
                onPressed: capturing || full ? null : onCapture,
                icon: capturing
                    ? const SizedBox.square(
                        dimension: Spacing.x4,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Icon(Icons.photo_camera),
                label: Text(l10n.cameraCapture),
              ),
            ),
          ),
          if (multiple) ...[
            const SizedBox(width: Spacing.x3),
            Expanded(
              child: SizedBox(
                height: Sizes.controlHeight,
                child: OutlinedButton.icon(
                  key: const ValueKey('cameraDone'),
                  onPressed: capturing ? null : onDone,
                  icon: const Icon(Icons.check),
                  label: Text(l10n.cameraDone),
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }
}
