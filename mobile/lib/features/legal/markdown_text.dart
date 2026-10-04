import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';
import '../../shared/widgets/neo_banner.dart';

/// One block of a legal document.
sealed class MarkdownBlock {
  const MarkdownBlock();
}

final class MarkdownHeading extends MarkdownBlock {
  const MarkdownHeading(this.level, this.text);

  final int level;
  final String text;
}

final class MarkdownParagraph extends MarkdownBlock {
  const MarkdownParagraph(this.text);

  final String text;
}

final class MarkdownQuote extends MarkdownBlock {
  const MarkdownQuote(this.text);

  final String text;
}

final class MarkdownBullet extends MarkdownBlock {
  const MarkdownBullet(this.text);

  final String text;
}

final class MarkdownRule extends MarkdownBlock {
  const MarkdownRule();
}

/// Reads the markdown the admin panel publishes (`legal_documents.Content`)
/// into blocks: headings, paragraphs (hard-wrapped lines joined), `>` notes,
/// `-` / `*` bullets, `---` rules, and table rows as text with their cells
/// joined by " · " (docs/DESIGN_SYSTEM.md "Mobil: dil və hüquqi sənədlər").
///
/// Our own: plan section 2.0 keeps a markdown package out for this much.
List<MarkdownBlock> parseMarkdown(String source) {
  final blocks = <MarkdownBlock>[];
  final paragraph = <String>[];
  final quote = <String>[];

  void flush() {
    if (paragraph.isNotEmpty) {
      blocks.add(MarkdownParagraph(paragraph.join(' ')));
      paragraph.clear();
    }
    if (quote.isNotEmpty) {
      blocks.add(MarkdownQuote(quote.join(' ')));
      quote.clear();
    }
  }

  for (final raw in source.split('\n')) {
    final line = raw.trim();

    if (line.isEmpty) {
      flush();
    } else if (RegExp(r'^#{1,6}\s').hasMatch(line)) {
      flush();
      final level = line.indexOf(' ');
      blocks.add(MarkdownHeading(level, line.substring(level).trim()));
    } else if (line.startsWith('>')) {
      if (paragraph.isNotEmpty) {
        flush();
      }
      quote.add(line.substring(1).trim());
    } else if (RegExp(r'^[-*]\s').hasMatch(line)) {
      flush();
      blocks.add(MarkdownBullet(line.substring(2).trim()));
    } else if (RegExp(r'^(-{3,}|\*{3,})$').hasMatch(line)) {
      flush();
      blocks.add(const MarkdownRule());
    } else if (line.startsWith('|')) {
      flush();
      final cells = line
          .split('|')
          .map((cell) => cell.trim())
          .where((cell) => cell.isNotEmpty)
          .toList();
      // The |---|---| line under a table's header carries nothing to read.
      if (cells.isNotEmpty &&
          !cells.every((cell) => RegExp(r'^:?-+:?$').hasMatch(cell))) {
        blocks.add(MarkdownParagraph(cells.join(' · ')));
      }
    } else {
      if (quote.isNotEmpty) {
        flush();
      }
      paragraph.add(line);
    }
  }

  flush();
  return blocks;
}

/// `**bold**` within a line; everything else as it is.
List<TextSpan> markdownSpans(String text) {
  final spans = <TextSpan>[];
  final bold = RegExp(r'\*\*(.+?)\*\*');
  var at = 0;

  for (final match in bold.allMatches(text)) {
    if (match.start > at) {
      spans.add(TextSpan(text: text.substring(at, match.start)));
    }
    spans.add(
      TextSpan(
        text: match.group(1),
        style: const TextStyle(fontWeight: FontWeight.w600),
      ),
    );
    at = match.end;
  }

  if (at < text.length) {
    spans.add(TextSpan(text: text.substring(at)));
  }

  return spans;
}

/// A legal document on screen.
class MarkdownText extends StatelessWidget {
  const MarkdownText(this.source, {super.key});

  final String source;

  @override
  Widget build(BuildContext context) {
    final text = Theme.of(context).textTheme;

    Widget rich(String value, TextStyle? style) =>
        Text.rich(TextSpan(style: style, children: markdownSpans(value)));

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        for (final block in parseMarkdown(source))
          Padding(
            padding: const EdgeInsets.only(bottom: Spacing.x3),
            child: switch (block) {
              MarkdownHeading(:final level, text: final value) => rich(
                value,
                level == 1 ? text.titleLarge : text.titleMedium,
              ),
              MarkdownParagraph(text: final value) => rich(
                value,
                text.bodyMedium,
              ),
              MarkdownQuote(text: final value) => NeoBanner(
                message: value.replaceAll('**', ''),
                tone: StatusTone.warning,
              ),
              MarkdownBullet(text: final value) => Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('•  ', style: text.bodyMedium),
                  Expanded(child: rich(value, text.bodyMedium)),
                ],
              ),
              MarkdownRule() => const Divider(),
            },
          ),
      ],
    );
  }
}
