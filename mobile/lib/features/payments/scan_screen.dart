import 'dart:async';

import 'package:camera/camera.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:google_mlkit_barcode_scanning/google_mlkit_barcode_scanning.dart';

import '../../core/features/feature_flags.dart';
import '../../core/features/readiness.dart';
import '../../core/platform/camera_choice.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_empty_state.dart';
import 'payment_models.dart';

/// Whether this build can read a QR code from the camera. ML Kit runs on
/// Android and iOS only; the web build opens a payment from its
/// `neoprint://pay` link instead (docs/DESIGN_SYSTEM.md: no scan button there).
final canScanProvider = Provider<bool>(
  (_) =>
      !kIsWeb &&
      (defaultTargetPlatform == TargetPlatform.android ||
          defaultTargetPlatform == TargetPlatform.iOS),
);

enum _ScanProblem { noCamera, permission }

/// Reads the kiosk's "pay with the NeoPrint app" QR (plan section 9.2) and
/// opens its payment. Everything happens on the phone: the frames go to ML
/// Kit on the device and nowhere else.
class ScanScreen extends ConsumerStatefulWidget {
  const ScanScreen({super.key, this.pickShareId = false});

  /// Reads a recipient's share QR for "Faylı paylaş" and closes with their
  /// `NP-` id (docs/DESIGN_SYSTEM.md, "Mobil: fayl paylaşımı"); any other
  /// code only says it is not one.
  final bool pickShareId;

  @override
  ConsumerState<ScanScreen> createState() => _ScanScreenState();
}

class _ScanScreenState extends ConsumerState<ScanScreen> {
  final _scanner = BarcodeScanner(formats: [BarcodeFormat.qrCode]);

  /// Which camera, as an index into [_cameras]: the main back one unless
  /// the user stepped to another (T-406). Kept while the app runs, so the next scan
  /// opens the one that worked.
  static int _lens = 0;

  CameraController? _controller;
  CameraDescription? _camera;
  List<CameraDescription> _cameras = const [];
  _ScanProblem? _problem;

  /// A frame is with ML Kit; the next ones are dropped until it answers.
  bool _reading = false;

  /// A payment code was found and its screen is opening.
  bool _done = false;

  /// The last code read was not ours (a bank QR, a web link).
  bool _foreign = false;

  /// A kiosk's "pay with the app" QR read while that is switched off.
  bool _payOff = false;

  @override
  void initState() {
    super.initState();
    unawaited(_open());
  }

  @override
  void dispose() {
    final controller = _controller;
    _controller = null;
    if (controller != null) {
      unawaited(_close(controller));
    }
    unawaited(_scanner.close());
    super.dispose();
  }

  static Future<void> _close(CameraController controller) async {
    if (controller.value.isStreamingImages) {
      await controller.stopImageStream();
    }
    await controller.dispose();
  }

  Future<void> _open() async {
    try {
      final cameras = await availableCameras();

      if (cameras.isEmpty) {
        setState(() => _problem = _ScanProblem.noCamera);
        return;
      }

      // The main back camera, not the ultra-wide or macro one that some
      // phones list first (T-346); "another camera" goes to the front one,
      // then the other back lenses (T-406).
      _cameras = scanCamerasInOrder(cameras);
      // A lens that worked is kept for the next scan; the front camera is
      // not - a kiosk is in front of the back one.
      if (_controller == null &&
          _camera == null &&
          _cameras[_lens % _cameras.length].lensDirection ==
              CameraLensDirection.front) {
        _lens = 0;
      }
      final back = _cameras[_lens % _cameras.length];

      // 720p: sharp enough for a QR on a kiosk screen an arm's length away,
      // light enough for ML Kit on every frame. The formats are the ones ML
      // Kit reads straight from memory on each platform.
      final controller = CameraController(
        back,
        ResolutionPreset.high,
        enableAudio: false,
        imageFormatGroup: defaultTargetPlatform == TargetPlatform.iOS
            ? ImageFormatGroup.bgra8888
            : ImageFormatGroup.nv21,
      );

      await controller.initialize();

      if (!mounted) {
        await controller.dispose();
        return;
      }

      // Continuous focus and exposure; a phone that cannot is fine as it is.
      try {
        await controller.setFocusMode(FocusMode.auto);
        await controller.setExposureMode(ExposureMode.auto);
      } on CameraException {
        // Fixed-focus camera.
      }

      _camera = back;
      await controller.startImageStream(_onFrame);

      setState(() {
        _controller = controller;
        _problem = null;
      });
    } on CameraException catch (error) {
      if (mounted) {
        setState(
          () => _problem = error.code.contains('Access')
              ? _ScanProblem.permission
              : _ScanProblem.noCamera,
        );
      }
    }
  }

  /// The next camera: the front one, then the other back lenses for a phone
  /// whose main camera does not focus on a screen (T-346, T-406).
  Future<void> _nextLens() async {
    final controller = _controller;
    setState(() => _controller = null);
    if (controller != null) {
      await _close(controller);
    }
    _lens = (_lens + 1) % _cameras.length;
    await _open();
  }

  Future<void> _onFrame(CameraImage image) async {
    final camera = _camera;
    if (_reading || _done || camera == null) {
      return;
    }

    final input = _toInputImage(image, camera);
    if (input == null) {
      return;
    }

    _reading = true;

    try {
      for (final barcode in await _scanner.processImage(input)) {
        final raw = barcode.rawValue;
        if (raw == null || !mounted) {
          continue;
        }

        final code = ScannedCode.parse(raw);

        if (widget.pickShareId) {
          if (code case ShareCode(:final publicId)) {
            _done = true;
            context.pop(publicId);
            return;
          }

          if (!_foreign) {
            setState(() => _foreign = true);
          }
          continue;
        }

        switch (code) {
          case PayCode()
              when !ref.read(
                featureOnProvider(FeatureKey.mobilePaymentAtKiosk),
              ):
            if (!_payOff) {
              setState(() => _payOff = true);
            }
          case PayCode(:final paymentId):
            _done = true;
            context.pushReplacement(Routes.pay(paymentId));
            return;
          case KioskCode(:final code) when Readiness.kioskSearch:
            _done = true;
            context.pushReplacement(Routes.kiosk(code));
            return;
          case KioskCode() || ShareCode() || ForeignCode():
            if (!_foreign) {
              setState(() => _foreign = true);
            }
        }
      }
    } on Object {
      // One unreadable frame; the next one is tried.
    } finally {
      _reading = false;
    }
  }

  /// A camera frame as ML Kit takes it: one plane, NV21 or BGRA.
  static InputImage? _toInputImage(
    CameraImage image,
    CameraDescription camera,
  ) {
    final rotation = InputImageRotationValue.fromRawValue(
      camera.sensorOrientation,
    );
    final raw = image.format.raw;
    final format = raw is int ? InputImageFormatValue.fromRawValue(raw) : null;

    if (rotation == null || format == null || image.planes.length != 1) {
      return null;
    }

    final plane = image.planes.single;

    return InputImage.fromBytes(
      bytes: plane.bytes,
      metadata: InputImageMetadata(
        size: Size(image.width.toDouble(), image.height.toDouble()),
        rotation: rotation,
        format: format,
        bytesPerRow: plane.bytesPerRow,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final controller = _controller;

    return Scaffold(
      appBar: AppBar(
        title: Text(l10n.scanTitle),
        actions: [
          if (_cameras.length > 1 && controller != null)
            IconButton(
              tooltip: l10n.scanOtherCamera,
              onPressed: _nextLens,
              icon: const Icon(Icons.cameraswitch_outlined),
            ),
        ],
      ),
      body: switch (_problem) {
        _ScanProblem.noCamera => NeoEmptyState(
          icon: Icons.no_photography_outlined,
          message: l10n.scanNoCamera,
        ),
        _ScanProblem.permission => NeoEmptyState(
          icon: Icons.no_photography_outlined,
          message: l10n.scanNoPermission,
        ),
        null when controller == null => const Center(
          child: CircularProgressIndicator(),
        ),
        null => _Viewfinder(
          controller: controller!,
          instruction: widget.pickShareId
              ? l10n.shareScanRecipient
              : l10n.scanInstruction,
          warning: _payOff
              ? l10n.featureOffKioskPayment
              : _foreign
              ? (widget.pickShareId ? l10n.scanNotShareCode : l10n.scanForeign)
              : null,
        ),
      },
    );
  }
}

class _Viewfinder extends StatelessWidget {
  const _Viewfinder({
    required this.controller,
    required this.instruction,
    this.warning,
  });

  final CameraController controller;
  final String instruction;
  final String? warning;

  static Size _upright(Size sensor, Orientation orientation) {
    final long = sensor.longestSide;
    final short = sensor.shortestSide;
    return orientation == Orientation.portrait
        ? Size(short, long)
        : Size(long, short);
  }

  Future<void> _focusAt(Offset point) async {
    try {
      await controller.setFocusPoint(point);
      await controller.setExposurePoint(point);
    } on CameraException {
      // A camera without focus points.
    }
  }

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Expanded(
          child: Stack(
            fit: StackFit.expand,
            children: [
              // The sensor reports its size lying down; the phone is held
              // upright, so the box is turned to match - otherwise the
              // picture was stretched (T-346). A tap focuses there.
              LayoutBuilder(
                builder: (context, constraints) => GestureDetector(
                  onTapUp: (details) => _focusAt(
                    Offset(
                      details.localPosition.dx / constraints.maxWidth,
                      details.localPosition.dy / constraints.maxHeight,
                    ),
                  ),
                  child: FittedBox(
                    fit: BoxFit.cover,
                    clipBehavior: Clip.hardEdge,
                    child: SizedBox.fromSize(
                      size: _upright(
                        controller.value.previewSize ?? const Size.square(1),
                        MediaQuery.orientationOf(context),
                      ),
                      child: CameraPreview(controller),
                    ),
                  ),
                ),
              ),
              // The square the code goes into.
              Center(
                child: FractionallySizedBox(
                  widthFactor: 0.65,
                  child: AspectRatio(
                    aspectRatio: 1,
                    child: DecoratedBox(
                      decoration: BoxDecoration(
                        border: Border.all(color: tokens.neutral0, width: 3),
                        borderRadius: BorderRadius.circular(Radii.lg),
                      ),
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
        Padding(
          padding: const EdgeInsets.all(Spacing.x4),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                instruction,
                textAlign: TextAlign.center,
                style: theme.textTheme.bodyLarge,
              ),
              const SizedBox(height: Spacing.x1),
              Text(
                AppLocalizations.of(context).scanTapToFocus,
                textAlign: TextAlign.center,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: tokens.neutral600,
                ),
              ),
              if (warning != null) ...[
                const SizedBox(height: Spacing.x3),
                NeoBanner(message: warning!, tone: StatusTone.warning),
              ],
            ],
          ),
        ),
      ],
    );
  }
}
