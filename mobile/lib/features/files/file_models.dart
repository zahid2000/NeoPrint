/// A user's file, as the API describes it (plan section 6, `GET files`).
///
/// The API leaves null fields out of its JSON, so every optional field is read
/// with a default rather than assumed present.
library;

/// Where a file is on its way to being printable.
enum FileStatus {
  pending,
  converting,
  ready,
  failed;

  static FileStatus parse(String? value) => switch (value) {
    'Converting' => FileStatus.converting,
    'Ready' => FileStatus.ready,
    'Failed' => FileStatus.failed,
    _ => FileStatus.pending,
  };

  /// Whether nothing more will happen to it without the user.
  bool get isSettled => this == FileStatus.ready || this == FileStatus.failed;
}

/// Where a file came from - the badge on the list (plan section 8.2).
enum FileSourceKind {
  uploaded,
  scan,
  layout,
  usb,
  shared;

  static FileSourceKind parse(String? value) => switch (value) {
    'Scan' => FileSourceKind.scan,
    'Layout' => FileSourceKind.layout,
    'Usb' => FileSourceKind.usb,
    'Shared' => FileSourceKind.shared,
    _ => FileSourceKind.uploaded,
  };
}

final class UserFile {
  const UserFile({
    required this.id,
    required this.fileName,
    required this.extension,
    required this.source,
    required this.status,
    required this.sizeBytes,
    required this.pageCount,
    required this.isSensitive,
    required this.versionCount,
    required this.createdAt,
    this.failureReason,
  });

  factory UserFile.fromJson(Map<String, dynamic> json) => UserFile(
    id: json['id'] as String,
    fileName: json['fileName'] as String? ?? '',
    extension: json['extension'] as String? ?? '',
    source: FileSourceKind.parse(json['source'] as String?),
    status: FileStatus.parse(json['status'] as String?),
    failureReason: json['failureReason'] as String?,
    sizeBytes: (json['sizeBytes'] as num?)?.toInt() ?? 0,
    pageCount: (json['pageCount'] as num?)?.toInt() ?? 0,
    isSensitive: json['isSensitive'] as bool? ?? false,
    versionCount: (json['versionCount'] as num?)?.toInt() ?? 0,
    createdAt:
        DateTime.tryParse(json['createdAt'] as String? ?? '')?.toLocal() ??
        DateTime.fromMillisecondsSinceEpoch(0),
  );

  final String id;
  final String fileName;
  final String extension;
  final FileSourceKind source;
  final FileStatus status;

  /// Why conversion failed, already worded for the user by the server.
  final String? failureReason;

  final int sizeBytes;

  /// Zero until conversion finishes.
  final int pageCount;

  /// A personal document, as the scanner marks it (plan section 2.11).
  final bool isSensitive;

  final int versionCount;
  final DateTime createdAt;

  /// Whether the file has pages to preview.
  bool get canPreview => status == FileStatus.ready && pageCount > 0;
}

final class FilePage {
  const FilePage({required this.items, required this.total});

  factory FilePage.fromJson(Map<String, dynamic> json) => FilePage(
    items: (json['items'] as List<dynamic>? ?? const [])
        .map((item) => UserFile.fromJson(item as Map<String, dynamic>))
        .toList(growable: false),
    total: (json['total'] as num?)?.toInt() ?? 0,
  );

  final List<UserFile> items;
  final int total;
}

/// Formats converted to JPEG on the phone before upload (plan section 2.2:
/// HEIC/HEIF is a mobile-only format, handled with the platform's own API).
const phoneConvertedExtensions = {'heic', 'heif'};

/// The extension of a file name, lower case and without the dot.
String extensionOf(String fileName) {
  final dot = fileName.lastIndexOf('.');
  return dot < 0 || dot == fileName.length - 1
      ? ''
      : fileName.substring(dot + 1).toLowerCase();
}
