import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';

/// The frame every sign-in and registration screen sits in.
///
/// One column, one question per field, a single card that stops growing on a
/// tablet, and the brand at the top so it is clear which application is
/// asking for a password (docs/DESIGN_SYSTEM.md section 5.2).
///
/// Having the frame here is what keeps the four auth screens looking like one
/// flow rather than four pages that happen to follow each other.
class AuthScaffold extends StatelessWidget {
  const AuthScaffold({
    super.key,
    required this.title,
    required this.content,
    this.subtitle,
    this.showBrand = true,
    this.appBar,
    this.footer,
  });

  /// What this screen asks for, as a sentence-length heading.
  final String title;

  /// One line under the heading: why, or what happens next.
  final String? subtitle;

  /// The form itself.
  final Widget content;

  /// The wordmark. Off on screens reached from inside the flow, where it
  /// would just push the content down.
  final bool showBrand;

  final PreferredSizeWidget? appBar;

  /// The "already have an account?" row and the like.
  final Widget? footer;

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;
    final text = Theme.of(context).textTheme;

    return Scaffold(
      appBar: appBar,
      backgroundColor: tokens.neutral50,
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.symmetric(
              horizontal: Spacing.x6,
              vertical: Spacing.x8,
            ),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: Sizes.formMaxWidth),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  if (showBrand) ...[
                    const _Wordmark(),
                    const SizedBox(height: Spacing.x8),
                  ],

                  Text(title, style: text.headlineMedium),

                  if (subtitle != null) ...[
                    const SizedBox(height: Spacing.x2),
                    Text(subtitle!, style: text.bodyMedium),
                  ],

                  const SizedBox(height: Spacing.x6),

                  Container(
                    padding: const EdgeInsets.all(Spacing.x4),
                    decoration: BoxDecoration(
                      color: tokens.neutral0,
                      border: Border.all(color: tokens.neutral200),
                      borderRadius: BorderRadius.circular(Radii.lg),
                    ),
                    child: content,
                  ),

                  if (footer != null) ...[
                    const SizedBox(height: Spacing.x4),
                    footer!,
                  ],
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// The wordmark: the mark, then the name.
class _Wordmark extends StatelessWidget {
  const _Wordmark();

  @override
  Widget build(BuildContext context) {
    final tokens = context.tokens;

    return Row(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        Container(
          width: 36,
          height: 36,
          decoration: BoxDecoration(
            color: tokens.brand500,
            borderRadius: BorderRadius.circular(Radii.sm),
          ),
          child: Icon(Icons.print_outlined, color: tokens.neutral0, size: 22),
        ),
        const SizedBox(width: Spacing.x3),
        Text(
          'NeoPrint',
          style: TextStyle(
            fontSize: FontSizes.h1,
            fontWeight: FontWeights.semibold,
            color: tokens.neutral900,
          ),
        ),
      ],
    );
  }
}
