import Flutter
import UIKit
import VisionKit

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

    if let registrar = engineBridge.pluginRegistry.registrar(forPlugin: "NeoPrintBrowser") {
      BrowserOpener.register(with: registrar)
    }

    if let registrar = engineBridge.pluginRegistry.registrar(forPlugin: "NeoPrintSystemShare") {
      SystemShare.register(with: registrar)
    }

    if let registrar = engineBridge.pluginRegistry.registrar(forPlugin: "NeoPrintDocumentScanner") {
      DocumentScanner.register(with: registrar)
    }
  }
}

/// The document scanner (plan section 2.11): VisionKit finds the page edges,
/// straightens the perspective and takes several pages in one go. Each page
/// comes back as a JPEG in the app's temporary directory; the app crops,
/// filters and sizes it afterwards, uploads it and deletes the file.
///
/// Written without a Mac to build it on: compiled and checked on a real
/// iPhone before the pilot (T-187, docs/MANUAL_TESTS.md MT-08).
final class DocumentScanner: NSObject, VNDocumentCameraViewControllerDelegate {
  static let channelName = "az.neoprint/document_scanner"

  /// The scan in progress, kept alive while VisionKit holds only a weak delegate.
  private static var current: DocumentScanner?

  private var result: FlutterResult?
  private var pageLimit = 10

  static func register(with registrar: FlutterPluginRegistrar) {
    let channel = FlutterMethodChannel(name: channelName, binaryMessenger: registrar.messenger())

    channel.setMethodCallHandler { call, result in
      switch call.method {
      case "isAvailable":
        result(VNDocumentCameraViewController.isSupported)

      case "scan":
        guard VNDocumentCameraViewController.isSupported else {
          result(FlutterError(code: "UNAVAILABLE", message: "This device cannot scan documents.", details: nil))
          return
        }

        guard current == nil else {
          result(FlutterError(code: "BUSY", message: "A scan is already open.", details: nil))
          return
        }

        guard let root = topViewController() else {
          result(FlutterError(code: "FAILED", message: "There is no screen to show the scanner on.", details: nil))
          return
        }

        let requested = ((call.arguments as? [String: Any])?["pageLimit"] as? Int) ?? 10
        let scanner = DocumentScanner()
        scanner.result = result
        scanner.pageLimit = max(1, min(requested, 20))
        current = scanner

        let controller = VNDocumentCameraViewController()
        controller.delegate = scanner
        root.present(controller, animated: true)

      default:
        result(FlutterMethodNotImplemented)
      }
    }
  }

  private static func topViewController() -> UIViewController? {
    let window = UIApplication.shared.connectedScenes
      .compactMap { $0 as? UIWindowScene }
      .flatMap { $0.windows }
      .first { $0.isKeyWindow }
    var top = window?.rootViewController

    while let presented = top?.presentedViewController {
      top = presented
    }

    return top
  }

  func documentCameraViewController(
    _ controller: VNDocumentCameraViewController,
    didFinishWith scan: VNDocumentCameraScan
  ) {
    let pages = (0..<min(scan.pageCount, pageLimit)).map { scan.imageOfPage(at: $0) }
    controller.dismiss(animated: true)

    // Encoding a dozen full-size pages takes a moment: off the main thread.
    DispatchQueue.global(qos: .userInitiated).async {
      var paths: [String] = []

      for (index, page) in pages.enumerated() {
        guard let data = page.jpegData(compressionQuality: 0.9) else { continue }

        let output = FileManager.default.temporaryDirectory
          .appendingPathComponent("scan-\(UUID().uuidString)-\(index).jpg")

        if (try? data.write(to: output)) != nil {
          paths.append(output.path)
        }
      }

      DispatchQueue.main.async { self.finish(paths) }
    }
  }

  func documentCameraViewControllerDidCancel(_ controller: VNDocumentCameraViewController) {
    controller.dismiss(animated: true)
    finish([String]())
  }

  func documentCameraViewController(
    _ controller: VNDocumentCameraViewController,
    didFailWithError error: Error
  ) {
    controller.dismiss(animated: true)
    finish(FlutterError(code: "FAILED", message: error.localizedDescription, details: nil))
  }

  private func finish(_ value: Any) {
    result?(value)
    result = nil
    DocumentScanner.current = nil
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

/// A link opened in Safari: the "Məlumatlarımı yüklə" archive, too large to
/// pass through the app (docs/DECISIONS.md T-147). Only http and https.
final class BrowserOpener: NSObject {
  static let channelName = "az.neoprint/browser"

  static func register(with registrar: FlutterPluginRegistrar) {
    let channel = FlutterMethodChannel(name: channelName, binaryMessenger: registrar.messenger())

    channel.setMethodCallHandler { call, result in
      guard call.method == "open",
        let arguments = call.arguments as? [String: Any],
        let text = arguments["url"] as? String,
        let url = URL(string: text),
        url.scheme == "https" || url.scheme == "http"
      else {
        result(FlutterError(code: "BAD_ARGUMENT", message: "an http(s) url is required", details: nil))
        return
      }

      UIApplication.shared.open(url) { opened in result(opened) }
    }
  }
}

/// Text handed to the system share sheet (UIActivityViewController): the
/// referral code and link (plan sections 2.0, 8.2: our own channel, no
/// plugin). Written without a Mac to build it on: compiled and checked with
/// the rest of the iOS build before the pilot (docs/TASKS.md T-187).
final class SystemShare: NSObject {
  static let channelName = "az.neoprint/share"

  static func register(with registrar: FlutterPluginRegistrar) {
    let channel = FlutterMethodChannel(name: channelName, binaryMessenger: registrar.messenger())

    channel.setMethodCallHandler { call, result in
      guard call.method == "text",
        let arguments = call.arguments as? [String: Any],
        let text = arguments["text"] as? String,
        !text.isEmpty
      else {
        result(FlutterError(code: "BAD_ARGUMENT", message: "text is required", details: nil))
        return
      }

      guard let presenter = topViewController() else {
        result(false)
        return
      }

      let sheet = UIActivityViewController(activityItems: [text], applicationActivities: nil)
      if let subject = arguments["subject"] as? String {
        sheet.setValue(subject, forKey: "subject")
      }

      // On an iPad the sheet is a popover and needs somewhere to point.
      if let popover = sheet.popoverPresentationController {
        popover.sourceView = presenter.view
        popover.sourceRect = CGRect(x: presenter.view.bounds.midX, y: presenter.view.bounds.midY, width: 0, height: 0)
        popover.permittedArrowDirections = []
      }

      presenter.present(sheet, animated: true) { result(true) }
    }
  }

  private static func topViewController() -> UIViewController? {
    let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
    var top = scene?.windows.first(where: { $0.isKeyWindow })?.rootViewController

    while let presented = top?.presentedViewController {
      top = presented
    }

    return top
  }
}
