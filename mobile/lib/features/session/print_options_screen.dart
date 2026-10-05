import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/format/formats.dart';
import '../../core/network/api_client.dart';
import '../../core/network/idempotency.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/tokens.dart';
import '../../l10n/app_localizations.dart';
import '../../shared/api_error_text.dart';
import '../../shared/widgets/neo_banner.dart';
import '../../shared/widgets/neo_empty_state.dart';
import '../../shared/widgets/neo_primary_button.dart';
import '../../shared/widgets/option_widgets.dart';
import '../files/file_models.dart';
import '../kiosk_search/kiosk_models.dart';
import '../kiosk_search/kiosk_search_controller.dart';
import 'session_controller.dart';
import 'session_models.dart';

/// The print options of an order in a session (plan section 9.3, step 2;
/// docs/DESIGN_SYSTEM.md, "Mobil: sessiya ilə çap"): one column, the price
/// from the server as the options change, and one button that places the
/// order and opens its payment.
class PrintOptionsScreen extends ConsumerWidget {
  const PrintOptionsScreen({super.key, required this.file});

  final UserFile file;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final session = ref.watch(sessionProvider).valueOrNull;

    if (session == null) {
      // It ended while the options were open.
      return Scaffold(
        appBar: AppBar(title: Text(file.fileName)),
        body: NeoEmptyState(
          icon: Icons.timer_off_outlined,
          message: l10n.noticeSessionExpired,
          action: OutlinedButton(
            onPressed: () => context.go(Routes.kiosks),
            child: Text(l10n.navKiosks),
          ),
        ),
      );
    }

    final kiosk = ref.watch(kioskDetailProvider(session.kioskId));

    return switch (kiosk) {
      AsyncData(:final value) => _Options(
        file: file,
        session: session,
        kiosk: value,
      ),
      AsyncError(:final error) => Scaffold(
        appBar: AppBar(title: Text(file.fileName)),
        body: NeoEmptyState(
          icon: Icons.cloud_off_outlined,
          message: l10n.kioskDetailLoadFailed(
            error is ApiException
                ? apiErrorText(l10n, error)
                : l10n.errorServer,
          ),
          action: OutlinedButton(
            onPressed: () =>
                ref.invalidate(kioskDetailProvider(session.kioskId)),
            child: Text(l10n.commonRetry),
          ),
        ),
      ),
      _ => Scaffold(
        appBar: AppBar(title: Text(file.fileName)),
        body: const Center(child: CircularProgressIndicator()),
      ),
    };
  }
}

class _Options extends ConsumerStatefulWidget {
  const _Options({
    required this.file,
    required this.session,
    required this.kiosk,
  });

  final UserFile file;
  final KioskSessionInfo session;
  final KioskDetail kiosk;

  @override
  ConsumerState<_Options> createState() => _OptionsState();
}

class _OptionsState extends ConsumerState<_Options> {
  static const _maxCopies = 50;
  static const _quoteDelay = Duration(milliseconds: 300);

  // A scan or layout starts, and stays, at its real size on its paper.
  late OrderOptions _options = OrderOptions.forPaper(widget.file.realSizePaper);
  bool _rangeMode = false;
  bool _rangeTouched = false;
  final _range = TextEditingController();
  final _rangeFocus = FocusNode();

  Timer? _quoteTimer;
  OrderQuote? _quote;
  String? _quoteError;

  bool _placing = false;
  String? _orderError;

  /// One per set of options: a retry after a lost answer is the same order.
  String _orderKey = newIdempotencyKey();

  @override
  void initState() {
    super.initState();
    _rangeFocus.addListener(() {
      if (!_rangeFocus.hasFocus) {
        setState(() => _rangeTouched = true);
      }
    });
    _requote();
  }

  @override
  void dispose() {
    _quoteTimer?.cancel();
    _range.dispose();
    _rangeFocus.dispose();
    super.dispose();
  }

  List<int>? get _pages =>
      parsePageRange(_rangeMode ? _range.text : null, widget.file.pageCount);

  void _change(OrderOptions options) {
    setState(() {
      _options = options;
      _orderError = null;
      _orderKey = newIdempotencyKey();
    });
    _requote();
  }

  void _requote() {
    _quoteTimer?.cancel();
    _quoteTimer = Timer(_quoteDelay, () => unawaited(_loadQuote()));
  }

  Future<void> _loadQuote() async {
    final pages = _pages;

    if (pages == null) {
      setState(() => _quote = null);
      return;
    }

    try {
      final quote = await ref
          .read(sessionRepositoryProvider)
          .quote(
            kioskId: widget.kiosk.summary.id,
            options: _options,
            pages: pages.length,
          );

      if (mounted) {
        setState(() {
          _quote = quote;
          _quoteError = null;
        });
      }
    } on ApiException catch (error) {
      if (mounted) {
        setState(
          () => _quoteError = apiErrorText(AppLocalizations.of(context), error),
        );
      }
    }
  }

  Future<void> _order() async {
    if (_pages == null) {
      setState(() => _rangeTouched = true);
      return;
    }

    setState(() {
      _placing = true;
      _orderError = null;
    });

    try {
      final order = await ref
          .read(sessionRepositoryProvider)
          .placeOrder(
            sessionId: widget.session.id,
            fileId: widget.file.id,
            options: _rangeMode
                ? _options.copyWith(pageRange: _range.text.trim())
                : _options.copyWith(allPages: true),
            idempotencyKey: _orderKey,
          );

      // The order moved the session's end on.
      unawaited(ref.read(sessionProvider.notifier).refresh());

      if (mounted) {
        context.pushReplacement(Routes.pay(order.paymentId));
      }
    } on ApiException catch (error) {
      if (mounted) {
        setState(() {
          _placing = false;
          _orderError = apiErrorText(AppLocalizations.of(context), error);
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final language = Localizations.localeOf(context).languageCode;
    final kiosk = widget.kiosk;
    final quote = _quote;
    final rangeInvalid = _rangeMode && _rangeTouched && _pages == null;

    final paper = kiosk.paper
        .where((size) => size.size == _options.paper)
        .firstOrNull;
    final paperShort =
        quote != null && paper?.sheets != null && quote.sheets > paper!.sheets!;

    return Scaffold(
      appBar: AppBar(title: Text(widget.file.fileName)),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(
          Spacing.x4,
          Spacing.x2,
          Spacing.x4,
          Spacing.x8,
        ),
        children: [
          if (widget.file.realSizePaper case final realSize?)
            realSize == 'A3' && !kiosk.a3
                ? RealSizeNote(l10n.optRealSizeNoA3, blocked: true)
                : RealSizeNote(l10n.optRealSize(realSize)),
          Text(
            l10n.printOptionsSubtitle(
              kiosk.summary.code,
              l10n.filesPageCount(widget.file.pageCount),
            ),
            style: theme.textTheme.bodyMedium?.copyWith(
              color: tokens.neutral600,
            ),
          ),
          const SizedBox(height: Spacing.x4),

          // Copies
          _Label(l10n.optCopies),
          Row(
            children: [
              IconButton.outlined(
                tooltip: l10n.optCopiesLess,
                onPressed: _options.copies > 1
                    ? () => _change(
                        _options.copyWith(copies: _options.copies - 1),
                      )
                    : null,
                icon: const Icon(Icons.remove),
              ),
              SizedBox(
                width: Sizes.touchTarget,
                child: Text(
                  '${_options.copies}',
                  textAlign: TextAlign.center,
                  style: theme.textTheme.titleLarge?.copyWith(
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                ),
              ),
              IconButton.outlined(
                tooltip: l10n.optCopiesMore,
                onPressed: _options.copies < _maxCopies
                    ? () => _change(
                        _options.copyWith(copies: _options.copies + 1),
                      )
                    : null,
                icon: const Icon(Icons.add),
              ),
            ],
          ),
          const SizedBox(height: Spacing.x4),

          // Colour
          _Label(l10n.optColor),
          SegmentedButton<bool>(
            segments: [
              ButtonSegment(value: false, label: Text(l10n.optBlackWhite)),
              ButtonSegment(
                value: true,
                label: Text(l10n.optColorful),
                enabled: kiosk.color,
              ),
            ],
            selected: {_options.color},
            onSelectionChanged: (value) =>
                _change(_options.copyWith(color: value.single)),
          ),
          if (!kiosk.color) _Why(l10n.optColorUnavailable),
          const SizedBox(height: Spacing.x4),

          // Both sides
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            title: Text(l10n.optDuplex),
            subtitle: kiosk.duplex ? null : Text(l10n.optDuplexUnavailable),
            value: _options.duplex,
            onChanged: kiosk.duplex
                ? (on) => _change(_options.copyWith(duplex: on))
                : null,
          ),
          const SizedBox(height: Spacing.x2),

          // Paper
          _Label(l10n.optPaper),
          SegmentedButton<String>(
            segments: [
              const ButtonSegment(value: 'A4', label: Text('A4')),
              ButtonSegment(
                value: 'A3',
                label: const Text('A3'),
                enabled: kiosk.a3,
              ),
            ],
            selected: {_options.paper},
            onSelectionChanged: _options.actualSize
                ? null
                : (value) => _change(_options.copyWith(paper: value.single)),
          ),
          if (!kiosk.a3) _Why(l10n.optA3Unavailable),
          const SizedBox(height: Spacing.x4),

          // Pages
          _Label(l10n.optPages),
          SegmentedButton<bool>(
            segments: [
              ButtonSegment(value: false, label: Text(l10n.optPagesAll)),
              ButtonSegment(value: true, label: Text(l10n.optPagesRange)),
            ],
            selected: {_rangeMode},
            onSelectionChanged: (value) {
              setState(() => _rangeMode = value.single);
              _change(_options);
            },
          ),
          if (_rangeMode) ...[
            const SizedBox(height: Spacing.x2),
            TextField(
              controller: _range,
              focusNode: _rangeFocus,
              keyboardType: TextInputType.text,
              decoration: InputDecoration(
                hintText: l10n.optRangeHint,
                errorText: rangeInvalid
                    ? l10n.optRangeInvalid(widget.file.pageCount)
                    : null,
              ),
              onChanged: (_) => _change(_options),
            ),
          ],
          const SizedBox(height: Spacing.x4),

          // Pages per sheet
          _Label(l10n.optPerSheet),
          SegmentedButton<int>(
            segments: const [
              ButtonSegment(value: 1, label: Text('1')),
              ButtonSegment(value: 2, label: Text('2')),
              ButtonSegment(value: 4, label: Text('4')),
            ],
            selected: {_options.pagesPerSheet},
            onSelectionChanged: _options.actualSize
                ? null
                : (value) =>
                      _change(_options.copyWith(pagesPerSheet: value.single)),
          ),
          const SizedBox(height: Spacing.x6),

          // The price
          Card(
            margin: EdgeInsets.zero,
            child: Padding(
              padding: const EdgeInsets.all(Spacing.x4),
              child: _pages == null
                  ? Text(l10n.optRangeInvalid(widget.file.pageCount))
                  : quote == null
                  ? _quoteError == null
                        ? const Center(child: CircularProgressIndicator())
                        : Text(l10n.quoteFailed(_quoteError!))
                  : Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          formatMoney(quote.total, language: language),
                          style: theme.textTheme.headlineMedium?.copyWith(
                            fontSize: FontSizes.display,
                            fontFeatures: const [FontFeature.tabularFigures()],
                          ),
                        ),
                        const SizedBox(height: Spacing.x1),
                        Text(
                          l10n.quoteSidesSheets(quote.sides, quote.sheets),
                          style: theme.textTheme.bodyMedium,
                        ),
                        if (quote.volumeDiscount > 0) ...[
                          const SizedBox(height: Spacing.x1),
                          Row(
                            children: [
                              Icon(
                                Icons.check_circle_outline,
                                size: Spacing.x4,
                                color: tokens
                                    .forTone(StatusTone.success)
                                    .foreground,
                              ),
                              const SizedBox(width: Spacing.x1),
                              Text(
                                l10n.quoteVolumeDiscount(
                                  formatNumber(
                                    quote.discountPercent,
                                    language: language,
                                  ),
                                ),
                              ),
                            ],
                          ),
                        ],
                      ],
                    ),
            ),
          ),
          if (paperShort) ...[
            const SizedBox(height: Spacing.x3),
            NeoBanner(tone: StatusTone.warning, message: l10n.quotePaperShort),
          ],
        ],
      ),
      bottomNavigationBar: SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(
            Spacing.x4,
            Spacing.x2,
            Spacing.x4,
            Spacing.x4,
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (_orderError case final error?) ...[
                NeoBanner.error(message: error),
                const SizedBox(height: Spacing.x2),
              ],
              NeoPrimaryButton(
                // The server prices the order itself: a quote still on its way
                // does not hold the button.
                label: quote == null || _pages == null
                    ? l10n.orderButtonPlain
                    : l10n.orderButton(
                        formatMoney(quote.total, language: language),
                      ),
                busy: _placing,
                onPressed:
                    _placing || (widget.file.realSizePaper == 'A3' && !kiosk.a3)
                    ? null
                    : () => unawaited(_order()),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _Label extends StatelessWidget {
  const _Label(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: Spacing.x2),
    child: Text(text, style: Theme.of(context).textTheme.titleSmall),
  );
}

/// Why a choice is not there: the kiosk's real limit, not an unfinished feature.
class _Why extends StatelessWidget {
  const _Why(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(top: Spacing.x1),
    child: Text(
      text,
      style: Theme.of(context).textTheme.bodySmall
          ?.copyWith(color: context.tokens.neutral600),
    ),
  );
}
