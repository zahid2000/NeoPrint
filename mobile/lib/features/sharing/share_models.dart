import 'package:flutter/foundation.dart';

/// Whether a share can still be opened (docs/DECISIONS.md 2026-10-05, sharing).
enum ShareAvailability {
  /// The file is as it was sent.
  available,

  /// The recipient has their own copy.
  saved,

  /// The sender deleted it: "Fayl artıq yoxdur".
  gone,

  /// The sender changed it after sending.
  changed;

  static ShareAvailability parse(String? value) => switch (value) {
    'Saved' => ShareAvailability.saved,
    'Gone' => ShareAvailability.gone,
    'Changed' => ShareAvailability.changed,
    _ => ShareAvailability.available,
  };
}

/// A share in "Alınanlar" or "Göndərilənlər" (`GET api/shares/received|sent`).
@immutable
class ShareItem {
  const ShareItem({
    required this.id,
    required this.fileName,
    required this.pageCount,
    required this.isSensitive,
    required this.otherPublicId,
    required this.createdAt,
    required this.availability,
    this.message,
    this.savedFileId,
  });

  factory ShareItem.fromJson(Map<String, dynamic> json) => ShareItem(
    id: json['id'] as String,
    fileName: json['fileName'] as String? ?? '',
    pageCount: json['pageCount'] as int? ?? 0,
    isSensitive: json['isSensitive'] as bool? ?? false,
    message: json['message'] as String?,
    otherPublicId: json['otherPublicId'] as String? ?? '',
    createdAt: DateTime.parse(json['createdAt'] as String),
    availability: ShareAvailability.parse(json['availability'] as String?),
    savedFileId: json['savedFileId'] as String?,
  );

  final String id;
  final String fileName;
  final int pageCount;
  final bool isSensitive;

  /// The sender's note.
  final String? message;

  /// The recipient's id in "sent", the sender's in "received".
  final String otherPublicId;

  final DateTime createdAt;
  final ShareAvailability availability;

  /// The recipient's own copy, while it exists.
  final String? savedFileId;
}

/// A user's public id: `NP-` and six letters or digits (backend `User.NewPublicId`).
abstract final class PublicId {
  static final _format = RegExp(r'^NP-[A-Z0-9]{6}$');

  /// The longest note a sender can add (backend `Share.MaxMessageLength`).
  static const maxMessage = 200;

  /// As typed, in capitals and without spaces: "np-7kq2mx " is NP-7KQ2MX.
  static String normalise(String value) =>
      value.trim().replaceAll(' ', '').toUpperCase();

  static bool isValid(String value) => _format.hasMatch(normalise(value));
}
