/// A notification from `GET api/notifications` (plan section 8.2), already in
/// the app's language.
class AppNotification {
  const AppNotification({
    required this.id,
    required this.type,
    required this.title,
    required this.body,
    required this.link,
    required this.createdAt,
    required this.readAt,
  });

  factory AppNotification.fromJson(Map<String, dynamic> json) =>
      AppNotification(
        id: json['id'] as String,
        type: json['type'] as String,
        title: json['title'] as String? ?? '',
        body: json['body'] as String? ?? '',
        link: json['link'] as String?,
        createdAt: DateTime.parse(json['createdAt'] as String),
        readAt: json['readAt'] == null
            ? null
            : DateTime.parse(json['readAt'] as String),
      );

  final String id;

  /// `JobCompleted`, `ScheduleDue`, `ScheduleAutoCancelled`, `RefundCompleted`,
  /// … (plan section 8.4).
  final String type;
  final String title;
  final String body;

  /// The screen it opens: a `neoprint://` link, or null.
  final String? link;
  final DateTime createdAt;
  final DateTime? readAt;

  bool get isRead => readAt != null;

  AppNotification read(DateTime at) => AppNotification(
    id: id,
    type: type,
    title: title,
    body: body,
    link: link,
    createdAt: createdAt,
    readAt: readAt ?? at,
  );
}

/// One page of the list; [unread] is all of the user's, for the badge.
class NotificationPage {
  const NotificationPage({
    required this.items,
    required this.unread,
    required this.next,
  });

  factory NotificationPage.fromJson(Map<String, dynamic> json) =>
      NotificationPage(
        items: [
          for (final item in json['items'] as List<dynamic>)
            AppNotification.fromJson(item as Map<String, dynamic>),
        ],
        unread: (json['unread'] as num?)?.toInt() ?? 0,
        // Left out on the last page.
        next: json['next'] == null
            ? null
            : DateTime.parse(json['next'] as String),
      );

  final List<AppNotification> items;
  final int unread;
  final DateTime? next;
}
