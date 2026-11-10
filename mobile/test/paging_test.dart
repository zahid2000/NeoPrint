import 'package:flutter_test/flutter_test.dart';
import 'package:neoprint/shared/paging.dart';

/// T-413: lists come a page at a time; a row added or removed meanwhile
/// neither repeats nor goes missing.
void main() {
  // The server's list, newest first, paged by position.
  late List<int> server;
  final asked = <(int, int)>[];
  late PageLoader<int> loader;

  setUp(() {
    server = [for (var i = 50; i >= 1; i--) i];
    asked.clear();
    loader = PageLoader((page, size) async {
      asked.add((page, size));
      return server.skip((page - 1) * size).take(size).toList();
    }, (item) => item);
  });

  test('twenty at a time until the end', () async {
    var paged = await loader.first();
    expect(paged.items, hasLength(20));
    expect(paged.hasMore, isTrue);

    paged = await loader.more(paged);
    paged = await loader.more(paged);

    expect(paged.items, server, reason: 'all fifty, in order');
    expect(paged.hasMore, isFalse);
  });

  test('a row deleted in the app: the next page loses nothing', () async {
    final first = await loader.first();
    server.remove(45);
    // The app takes it off its list too (FilesController.delete).
    final shown = Paged([...first.items]..remove(45), hasMore: true);

    var paged = await loader.more(shown);
    paged = await loader.more(paged);

    expect(paged.items, server.take(paged.items.length));
    expect(paged.items, contains(30), reason: 'the row that moved up a page');
  });

  test('a row added on top meanwhile: nothing comes twice', () async {
    final first = await loader.first();
    server.insert(0, 51);
    final paged = await loader.more(first);

    expect(paged.items.toSet().length, paged.items.length, reason: 'once');
    expect(paged.items, [for (var i = 50; i >= 12; i--) i]);
  });

  test('reading again keeps as many rows as were shown', () async {
    var paged = await loader.first();
    paged = await loader.more(paged);
    paged = await loader.more(paged);
    asked.clear();

    final again = await loader.reload(paged);

    expect(again.items, server);
    expect(asked, [(1, 50)], reason: 'one page as long as the list');
  });
}
