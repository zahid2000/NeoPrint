import Flutter
import UIKit

@main
@objc class AppDelegate: FlutterAppDelegate, FlutterImplicitEngineDelegate {
  override func application(
    _ application: UIApplication,
    didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?
  ) -> Bool {
    return super.application(application, didFinishLaunchingWithOptions: launchOptions)
  }

  func didInitializeImplicitFlutterEngine(_ engineBridge: FlutterImplicitEngineBridge) {
    GeneratedPluginRegistrant.register(with: engineBridge.pluginRegistry)

    if let registrar = engineBridge.pluginRegistry.registrar(forPlugin: "NeoPrintHeicConverter") {
      HeicConverter.register(with: registrar)
    }
  }
}

/// HEIC/HEIF photos turned into JPEG before upload (plan section 2.2: the
/// backend does not take HEIC; the phone converts it with its own API).
///
/// UIImage reads HEIC natively and keeps the photo's orientation, which
/// jpegData writes back out.
final class HeicConverter: NSObject {
  static let channelName = "az.neoprint/heic"

  static func register(with registrar: FlutterPluginRegistrar) {
    let channel = FlutterMethodChannel(name: channelName, binaryMessenger: registrar.messenger())

    channel.setMethodCallHandler { call, result in
      guard call.method == "toJpeg" else {
        result(FlutterMethodNotImplemented)
        return
      }

      guard let arguments = call.arguments as? [String: Any],
        let path = arguments["path"] as? String,
        !path.isEmpty
      else {
        result(FlutterError(code: "BAD_ARGUMENT", message: "path is required", details: nil))
        return
      }

      // Decoding a large photo takes a moment: off the main thread.
      DispatchQueue.global(qos: .userInitiated).async {
        guard let image = UIImage(contentsOfFile: path),
          let data = image.jpegData(compressionQuality: 0.9)
        else {
          DispatchQueue.main.async {
            result(FlutterError(code: "FAILED", message: "The photo could not be read.", details: nil))
          }
          return
        }

        // The app's temporary directory: private, and cleared by the system.
        let output = FileManager.default.temporaryDirectory
          .appendingPathComponent("heic-\(UUID().uuidString).jpg")

        do {
          try data.write(to: output)
          DispatchQueue.main.async { result(output.path) }
        } catch {
          DispatchQueue.main.async {
            result(FlutterError(code: "FAILED", message: error.localizedDescription, details: nil))
          }
        }
      }
    }
  }
}
