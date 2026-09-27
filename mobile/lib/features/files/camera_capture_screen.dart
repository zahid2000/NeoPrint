import 'package:camera/camera.dart';
import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_empty_state.dart';

/// Takes one photo to upload (plan section 8.2: "kamera").
///
/// Pops with the photo's path, or with nothing when the user goes back. The
/// document scanner with its edge detection is a separate screen (plan
/// section 2.11); this is the plain camera.
class CameraCaptureScreen extends StatefulWidget {
  const CameraCaptureScreen({super.key});

  @override
  State<CameraCaptureScreen> createState() => _CameraCaptureScreenState();
}

enum _CameraProblem { noCamera, permission, failed }

class _CameraCaptureScreenState extends State<CameraCaptureScreen>
    with WidgetsBindingObserver {
  CameraController? _controller;
  _CameraProblem? _problem;
  bool _capturing = false;

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

      final back = cameras.firstWhere(
        (camera) => camera.lensDirection == CameraLensDirection.back,
        orElse: () => cameras.first,
      );

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

      if (mounted) {
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

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final controller = _controller;

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
            Padding(
              padding: const EdgeInsets.all(Spacing.x6),
              child: SizedBox(
                width: double.infinity,
                height: Sizes.controlHeight,
                child: FilledButton.icon(
                  onPressed: _capturing ? null : _capture,
                  icon: _capturing
                      ? const SizedBox.square(
                          dimension: Spacing.x4,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Icons.photo_camera),
                  label: Text(l10n.cameraCapture),
                ),
              ),
            ),
          ],
        ),
      },
    );
  }
}
