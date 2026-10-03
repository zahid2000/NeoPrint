import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

/// Sends the user to [location] after this frame.
///
/// For a route that needs what the screen before handed over (`extra`): the
/// browser's back button rebuilds such a page from history without it, and
/// the route's redirect does not run then. Instead of failing, the page goes
/// to where that thing can be chosen again.
class RedirectTo extends StatefulWidget {
  const RedirectTo(this.location, {super.key});

  final String location;

  @override
  State<RedirectTo> createState() => _RedirectToState();
}

class _RedirectToState extends State<RedirectTo> {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) {
        context.go(widget.location);
      }
    });
  }

  @override
  Widget build(BuildContext context) => const Scaffold(body: SizedBox.shrink());
}
