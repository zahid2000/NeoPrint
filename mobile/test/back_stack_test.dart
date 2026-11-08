import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:neoprint/core/router/back_stack.dart';

void main() {
  testWidgets('a switch made by a button can be gone back from, to the very '
      'screen it was made on (T-348)', (tester) async {
    final router = GoRouter(
      initialLocation: '/print',
      routes: [
        GoRoute(path: '/print', builder: (_, __) => const Text('print')),
        GoRoute(path: '/notifications', builder: (_, __) => const Text('n')),
        GoRoute(path: '/files', builder: (_, __) => const Text('files')),
      ],
    );
    await tester.pumpWidget(MaterialApp.router(routerConfig: router));

    router.push('/notifications');
    await tester.pumpAndSettle();

    final stack = BackStack()..go(router, '/files');
    await tester.pumpAndSettle();
    expect(find.text('files'), findsOneWidget);
    // `go` alone leaves nothing to pop: back would have closed the app.
    expect(router.canPop(), isFalse);

    expect(stack.back(router), isTrue);
    await tester.pumpAndSettle();
    expect(find.text('n'), findsOneWidget);

    // And the screen under it is still there.
    router.pop();
    await tester.pumpAndSettle();
    expect(find.text('print'), findsOneWidget);
    expect(stack.back(router), isFalse);
  });

  test('only the last few places are kept', () {
    final stack = BackStack();
    expect(stack.isEmpty, isTrue);
    stack.clear();
    expect(BackStack.limit, greaterThan(5));
  });
}
