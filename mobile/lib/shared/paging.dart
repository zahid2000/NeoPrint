import 'dart:math' as math;

import 'package:flutter_riverpod/flutter_riverpod.dart';

/// How many rows a list asks for at a time (T-413, docs/DESIGN_SYSTEM.md
/// section 5.2): enough to fill a screen and a bit.
const listPageSize = 20;

/// The most the server gives in one page.
const _maxPageSize = 100;

/// A list shown a part at a time (T-413): the rows loaded so far, whether
/// the server has more, and how the next part is doing.
final class Paged<T> {
  const Paged(
    this.items, {
    this.hasMore = false,
    this.loadingMore = false,
    this.moreError,
  });

  final List<T> items;
  final bool hasMore;

  /// The next part is on its way: a spinner under the list.
  final bool loadingMore;

  /// The next part did not come: the reason and "Yenidən cəhd et" under the
  /// list; the rows already shown stay.
  final Object? moreError;

  Paged<T> copyWith({
    List<T>? items,
    bool? hasMore,
    bool? loadingMore,
    Object? moreError,
    bool clearMoreError = false,
  }) => Paged(
    items ?? this.items,
    hasMore: hasMore ?? this.hasMore,
    loadingMore: loadingMore ?? this.loadingMore,
    moreError: clearMoreError ? null : moreError ?? this.moreError,
  );
}

/// Asks the server for a 1-based [page] of [pageSize] rows, newest first.
typedef PageFetch<T> = Future<List<T>> Function(int page, int pageSize);

/// Loads a [Paged] list from the server a page at a time. The pages are by
/// position, so a row added or removed meanwhile may come twice: rows are
/// kept once, by [idOf].
final class PageLoader<T> {
  PageLoader(this.fetch, this.idOf, {this.pageSize = listPageSize});

  final PageFetch<T> fetch;
  final Object Function(T item) idOf;
  final int pageSize;

  /// The first page.
  Future<Paged<T>> first() async {
    final items = await fetch(1, pageSize);
    return Paged(items, hasMore: items.length >= pageSize);
  }

  /// As many rows as [current] shows, read again (pull to refresh, a hub
  /// event): the list keeps its length and the place it was scrolled to.
  Future<Paged<T>> reload(Paged<T>? current) async {
    final shown = current?.items.length ?? 0;
    if (shown <= pageSize) {
      return first();
    }

    // One page while it fits; the largest pages after that.
    final size = math.min(_maxPageSize, shown);
    final pages = (shown / size).ceil();
    final items = <T>[];
    var hasMore = false;
    for (var page = 1; page <= pages; page++) {
      final part = await fetch(page, size);
      items.addAll(part);
      hasMore = part.length >= size;
      if (!hasMore) {
        break;
      }
    }
    return Paged(_unique(items), hasMore: hasMore);
  }

  /// The next page after [current], added under it.
  Future<Paged<T>> more(Paged<T> current) async {
    // A row removed since shifts the pages up: the page the next row is on
    // is asked, and what was already shown is kept once.
    final page = current.items.length ~/ pageSize + 1;
    final part = await fetch(page, pageSize);
    return Paged(
      _unique([...current.items, ...part]),
      hasMore: part.length >= pageSize,
    );
  }

  List<T> _unique(List<T> items) {
    final seen = <Object>{};
    return [
      for (final item in items)
        if (seen.add(idOf(item))) item,
    ];
  }
}

/// Reads [get]'s list again as long as it is (pull to refresh, a hub event);
/// a failure keeps what is on screen, or shows the error when nothing is.
Future<void> refreshPaged<T>(
  PageLoader<T> loader,
  AsyncValue<Paged<T>> Function() get,
  void Function(AsyncValue<Paged<T>>) set,
) async {
  final current = get().valueOrNull;
  try {
    set(AsyncData(await loader.reload(current)));
  } on Object catch (error, stack) {
    if (current == null) {
      set(AsyncError(error, stack));
    }
  }
}

/// Adds the next page under [get]'s list. Asked again while one loads, or
/// after one failed (unless [retry]), it does nothing.
Future<void> loadMorePaged<T>(
  PageLoader<T> loader,
  AsyncValue<Paged<T>> Function() get,
  void Function(AsyncValue<Paged<T>>) set, {
  bool retry = false,
}) async {
  final current = get().valueOrNull;
  if (current == null ||
      !current.hasMore ||
      current.loadingMore ||
      (current.moreError != null && !retry)) {
    return;
  }

  final loading = current.copyWith(loadingMore: true, clearMoreError: true);
  set(AsyncData(loading));
  try {
    final next = await loader.more(current);
    // Read again meanwhile: that list wins.
    if (identical(get().valueOrNull, loading)) {
      set(AsyncData(next));
    }
  } on Object catch (error) {
    if (identical(get().valueOrNull, loading)) {
      set(AsyncData(current.copyWith(moreError: error)));
    }
  }
}
