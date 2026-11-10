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
import '../../shared/widgets/neo_design.dart';
import '../../shared/widgets/neo_primary_button.dart';
import '../files/file_row.dart';
import '../files/file_models.dart';
import '../../shared/widgets/option_widgets.dart';
import '../kiosk_search/kiosk_models.dart';
import '../kiosk_search/kiosk_picker_sheet.dart';
import '../kiosk_search/kiosk_search_controller.dart';
import '../kiosk_search/kiosk_status_view.dart';
import '../payments/payment_models.dart' show toCents;
import '../print_hub/print_start.dart';
import '../session/session_controller.dart';
import '../session/session_models.dart';
import 'prepared_models.dart';
import 'prepared_repository.dart';

/// "Kiosk üçün hazırla" (plan section 9.4, step 1; docs/DESIGN_SYSTEM.md
/// "Mobil: şəxsi QR və Kiosk üçün hazırla"; docs/DECISIONS.md T-144).
///
/// Without a kiosk every option may stay "at the kiosk" and the file is saved
/// unpaid for any kiosk. With a kiosk every option is set, the server prices
/// it by that kiosk's current profile and it can be paid now; a price that
/// changed meanwhile is shown and asked about again.
/// "Yenidən çap et" (T-408): what an order was printed with, and where - the
/// print settings start from it and can still be changed.
final class PrintAgain {
  const PrintAgain({
    required this.copies,
    required this.color,
    required this.duplex,
    required this.paper,
    required this.pagesPerSheet,
    this.pageRange,
    this.kioskId,
  });

  final int copies;
  final bool color;
  final bool duplex;
  final String paper;
  final int pagesPerSheet;
  final String? pageRange;
  final String? kioskId;
}

class PrepareScreen extends ConsumerStatefulWidget {
  const PrepareScreen({super.key, required this.file, this.again});

  final UserFile file;

  /// The order printed again, when that is how the screen was opened.
  final PrintAgain? again;

  @override
  ConsumerState<PrepareScreen> createState() => _PrepareScreenState();
}

/// The pages: chosen at the kiosk, all of them, or a range.
enum _Pages { atKiosk, all, range }

class _PrepareScreenState extends ConsumerState<PrepareScreen> {
  static const _maxCopies = 50;
  static const _quoteDelay = Duration(milliseconds: 300);

  // Null: chosen at the kiosk.
  int? _copies;
  bool? _color;
  bool? _duplex;
  // A scan or layout fixes both: its paper, one page to a sheet.
  late String? _paper = widget.file.realSizePaper;
  late int? _perSheet = widget.file.realSizePaper == null ? null : 1;
  _Pages _pages = _Pages.atKiosk;

  bool get _realSize => widget.file.realSizePaper != null;
  final _range = TextEditingController();

  KioskSummary? _kiosk;

  /// "İndi çap et" or "Hazırla" (T-376); null until the user chooses.
  PrintMode? _mode;

  /// The usual values were filled in for printing now, not chosen: going to
  /// "Hazırla" gives them back to the kiosk.
  bool _filledForNow = false;

  Timer? _quoteTimer;
  OrderQuote? _quote;
  String? _quoteError;

  bool _busy = false;
  String? _error;

  // One per set of choices: a retry after a lost answer is the same order.
  String _key = newIdempotencyKey();

  @override
  void initState() {
    super.initState();
    final again = widget.again;
    if (again == null) {
      return;
    }

    _copies = again.copies;
    _color = again.color;
    _duplex = again.duplex;
    if (!_realSize) {
      _paper = again.paper;
      _perSheet = again.pagesPerSheet;
    }
    if (again.pageRange case final range? when range.trim().isNotEmpty) {
      _pages = _Pages.range;
      _range.text = range;
    } else {
      _pages = _Pages.all;
    }
    if (again.kioskId case final id?) {
      unawaited(_takeKiosk(id));
    }
  }

  /// The order's kiosk, when it can take the order now; otherwise the user
  /// chooses one as usual.
  Future<void> _takeKiosk(String id) async {
    try {
      final detail = await ref.read(kioskDetailProvider(id).future);
      if (!mounted || _kiosk != null) {
        return;
      }
      if (_currentMode == PrintMode.now &&
          detail.summary.status != KioskStatus.online) {
        return;
      }
      _change(() => _kiosk = detail.summary);
    } on Object {
      // Not found or not reachable: chosen by hand.
    }
  }

  @override
  void dispose() {
    _quoteTimer?.cancel();
    _range.dispose();
    super.dispose();
  }

  int get _pageCount => widget.file.pageCount;

  /// What a kiosk order prints: every choice made, the rest the usual.
  OrderOptions get _options => OrderOptions(
    copies: _copies ?? 1,
    color: _color ?? false,
    duplex: _duplex ?? false,
    paper: _paper ?? 'A4',
    pagesPerSheet: _perSheet ?? 1,
    pageRange: _pages == _Pages.range ? _range.text.trim() : null,
    actualSize: _realSize,
  );

  PreparedPreset get _preset => PreparedPreset(
    copies: _copies,
    color: _color,
    duplex: _duplex,
    paper: _paper,
    pagesPerSheet: _perSheet,
    pageRange: _pages == _Pages.range ? _range.text.trim() : null,
  );

  List<int>? get _selected => _pages == _Pages.range
      ? parsePageRange(_range.text, _pageCount)
      : parsePageRange(null, _pageCount);

  void _change(VoidCallback update) {
    setState(() {
      _filledForNow = false;
      update();
      _error = null;
      _key = newIdempotencyKey();
    });
    _requote();
  }

  void _requote() {
    _quoteTimer?.cancel();

    if (_kiosk == null) {
      setState(() => _quote = null);
      return;
    }

    _quoteTimer = Timer(_quoteDelay, () => unawaited(_loadQuote()));
  }

  Future<OrderQuote?> _loadQuote() async {
    final kiosk = _kiosk;
    final pages = _selected;

    if (kiosk == null || pages == null) {
      if (mounted) {
        setState(() => _quote = null);
      }
      return null;
    }

    try {
      final quote = await ref
          .read(sessionRepositoryProvider)
          .quote(kioskId: kiosk.id, options: _options, pages: pages.length);

      if (mounted) {
        setState(() {
          _quote = quote;
          _quoteError = null;
        });
      }

      return quote;
    } on ApiException catch (error) {
      if (mounted) {
        setState(
          () => _quoteError = apiErrorText(AppLocalizations.of(context), error),
        );
      }

      return null;
    }
  }

  /// "İndi çap et" while it can be done (on, and a kiosk works), else
  /// "Hazırla" - until the user chooses.
  PrintMode get _currentMode {
    if (_mode case final chosen?) {
      return chosen;
    }

    final nowOn =
        printNowOn(ref) &&
        ref.read(anyKioskWorkingProvider).valueOrNull != false;
    return nowOn || !prepareOn(ref) ? PrintMode.now : PrintMode.prepare;
  }

  /// Printed now: every choice is made here, none left to the kiosk.
  void _settleForNow() {
    if (_copies == null) {
      _filledForNow = true;
    }
    _copies ??= 1;
    _color ??= false;
    _duplex ??= false;
    _paper ??= 'A4';
    _perSheet ??= 1;
    if (_pages == _Pages.atKiosk) {
      _pages = _Pages.all;
    }
  }

  /// The choice above the kiosk: printing now needs a working one.
  void _setMode(PrintMode mode) {
    final restore =
        mode == PrintMode.prepare && _filledForNow && _kiosk == null;
    _change(() {
      if (restore) {
        _copies = null;
        _color = null;
        _duplex = null;
        _paper = widget.file.realSizePaper;
        _perSheet = widget.file.realSizePaper == null ? null : 1;
        _pages = _Pages.atKiosk;
      }
      _mode = mode;
      if (mode == PrintMode.now && _kiosk?.status != KioskStatus.online) {
        _kiosk = null;
      }
      if (mode == PrintMode.now) {
        _settleForNow();
      }
    });
  }

  /// A choice the chosen kiosk cannot print moves to one it can (T-407):
  /// colour to black and white, two sides to one, A3 to A4 - the row then
  /// says why. Done after the frame, when the kiosk's detail has come.
  void _fitToKiosk(KioskDetail? detail) {
    if (detail == null) {
      return;
    }
    final color = _color == true && !detail.color;
    final duplex = _duplex == true && !detail.duplex;
    final a3 = _paper == 'A3' && !detail.a3 && !_realSize;
    if (!color && !duplex && !a3) {
      return;
    }
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) {
        return;
      }
      _change(() {
        if (color) {
          _color = false;
        }
        if (duplex) {
          _duplex = false;
        }
        if (a3) {
          _paper = 'A4';
        }
      });
    });
  }

  Future<void> _chooseKiosk() async {
    final chosen = await showKioskPicker(
      context,
      onlyWorking: _currentMode == PrintMode.now,
    );

    if (chosen == null) {
      return;
    }

    _change(() {
      _kiosk = chosen;
      // A price needs every choice: what was left to the kiosk takes the usual.
      _copies ??= 1;
      _color ??= false;
      _duplex ??= false;
      _paper ??= 'A4';
      _perSheet ??= 1;
      if (_pages == _Pages.atKiosk) {
        _pages = _Pages.all;
      }
    });
  }

  Future<void> _save() async {
    final l10n = AppLocalizations.of(context);

    if (_pages == _Pages.range && _selected == null) {
      setState(() => _error = l10n.optRangeInvalid(_pageCount));
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      await ref
          .read(preparedRepositoryProvider)
          .prepare(
            fileId: widget.file.id,
            preset: _preset,
            idempotencyKey: _key,
          );

      ref.invalidate(preparedPrintsProvider);

      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(l10n.prepareSaved)));
        context.pushReplacement(Routes.prepared);
      }
    } on ApiException catch (error) {
      if (mounted) {
        setState(() {
          _busy = false;
          _error = l10n.prepareFailed(apiErrorText(l10n, error));
        });
      }
    }
  }

  Future<void> _pay({double? amount}) async {
    final l10n = AppLocalizations.of(context);
    final kiosk = _kiosk;
    final shown = amount ?? _quote?.total;

    if (kiosk == null || shown == null || _selected == null) {
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      final order = await ref
          .read(preparedRepositoryProvider)
          .preparePaid(
            kiosk: kiosk.id,
            fileId: widget.file.id,
            options: _options,
            expectedAmount: shown,
            idempotencyKey: _key,
          );

      ref.invalidate(preparedPrintsProvider);

      if (mounted) {
        context.pushReplacement(Routes.pay(order.paymentId));
      }
    } on ApiException catch (error) {
      // A definite answer: the next try is a new order.
      _key = newIdempotencyKey();

      final now = error.statusCode == 409 ? await _loadQuote() : null;

      if (!mounted) {
        return;
      }

      setState(() => _busy = false);

      if (now != null && toCents(now.total) != toCents(shown)) {
        await _askNewPrice(shown, now.total);
        return;
      }

      setState(() => _error = l10n.prepareFailed(apiErrorText(l10n, error)));
    }
  }

  /// "İndi çap et" (T-376, plan 9.3): a session at the kiosk, the order in
  /// it, then the payment; the job goes into the kiosk's queue once paid.
  Future<void> _printNow() async {
    final l10n = AppLocalizations.of(context);
    final kiosk = _kiosk;

    if (kiosk == null) {
      return;
    }

    if (_pages == _Pages.range && _selected == null) {
      setState(() => _error = l10n.optRangeInvalid(_pageCount));
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      final session = await ref.read(sessionProvider.notifier).open(kiosk.code);
      final order = await ref
          .read(sessionRepositoryProvider)
          .placeOrder(
            sessionId: session.id,
            fileId: widget.file.id,
            options: _pages == _Pages.range
                ? _options
                : _options.copyWith(allPages: true),
            idempotencyKey: _key,
          );

      unawaited(ref.read(sessionProvider.notifier).refresh());

      if (mounted) {
        context.pushReplacement(Routes.pay(order.paymentId));
      }
    } on ApiException catch (error) {
      _key = newIdempotencyKey();
      if (mounted) {
        setState(() {
          _busy = false;
          _error = l10n.prepareFailed(apiErrorText(l10n, error));
        });
      }
    }
  }

  /// The kiosk's prices changed since they were shown: the new one, asked.
  Future<void> _askNewPrice(double before, double now) async {
    final l10n = AppLocalizations.of(context);
    final language = Localizations.localeOf(context).languageCode;

    final agreed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(l10n.preparePriceChangedTitle),
        content: Text(
          l10n.preparePriceChangedBody(
            formatMoney(before, language: language),
            formatMoney(now, language: language),
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: Text(l10n.commonCancel),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: Text(l10n.preparePriceChangedConfirm),
          ),
        ],
      ),
    );

    if (agreed == true && mounted) {
      await _pay(amount: now);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final tokens = context.tokens;
    final language = Localizations.localeOf(context).languageCode;
    final canNow = printNowOn(ref);
    final canPrepare = prepareOn(ref);
    // Unknown (still asking) leaves "İndi çap et" on: the list offers only working kiosks.
    final nowWorks = ref.watch(anyKioskWorkingProvider).valueOrNull != false;
    final nowOn = canNow && nowWorks;
    final mode = _currentMode;
    final printingNow = mode == PrintMode.now;
    if (printingNow) {
      // The first look, before any choice: the usual values, shown as chosen.
      _settleForNow();
    }
    final kiosk = _kiosk;
    final detail = kiosk == null
        ? null
        : ref.watch(kioskDetailProvider(kiosk.id)).valueOrNull;
    // The detail is the fresher of the two; the list's until it comes.
    final kioskStatus = detail?.summary.status ?? kiosk?.status;
    _fitToKiosk(detail);
    final atKiosk = kiosk == null && !printingNow;
    final quote = _quote;
    final rangeInvalid =
        _pages == _Pages.range && _range.text.isNotEmpty && _selected == null;

    return Scaffold(
      // The design's "Çap parametrləri" (T-342): the screen says what it is,
      // the file is the card on top.
      appBar: neoTabAppBar(
        context,
        title: l10n.filePreviewPrintSettings,
        showBack: true,
      ),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(
          Spacing.x4,
          Spacing.x2,
          Spacing.x4,
          Spacing.x8,
        ),
        children: [
          if (widget.file.realSizePaper case final realSize?)
            RealSizeNote(l10n.optRealSize(realSize)),
          FileRow(file: widget.file),
          const SizedBox(height: Spacing.x4),

          OptionLabel(l10n.optCopies),
          Row(
            children: [
              if (atKiosk) ...[
                ChoiceChip(
                  label: Text(l10n.prepareAtKiosk),
                  selected: _copies == null,
                  onSelected: (_) => _change(() => _copies = null),
                ),
                const SizedBox(width: Spacing.x3),
              ],
              IconButton.outlined(
                tooltip: l10n.optCopiesLess,
                onPressed: (_copies ?? 1) > 1
                    ? () => _change(() => _copies = (_copies ?? 1) - 1)
                    : null,
                icon: const Icon(Icons.remove),
              ),
              SizedBox(
                width: Sizes.touchTarget,
                child: Text(
                  _copies == null ? '–' : '$_copies',
                  textAlign: TextAlign.center,
                  style: theme.textTheme.titleLarge,
                ),
              ),
              IconButton.outlined(
                tooltip: l10n.optCopiesMore,
                onPressed: (_copies ?? 0) < _maxCopies
                    ? () => _change(() => _copies = (_copies ?? 0) + 1)
                    : null,
                icon: const Icon(Icons.add),
              ),
            ],
          ),
          const SizedBox(height: Spacing.x4),

          OptionLabel(l10n.optColor),
          OptionChoice<bool>(
            atKiosk: atKiosk,
            atKioskLabel: l10n.prepareAtKiosk,
            value: _color,
            choices: [
              (false, l10n.optBlackWhite, true),
              (true, l10n.optColorful, detail?.color ?? true),
            ],
            onChanged: (value) => _change(() => _color = value),
          ),
          if (detail != null && !detail.color)
            OptionWhy(l10n.optColorUnavailable),
          const SizedBox(height: Spacing.x4),

          OptionLabel(l10n.optDuplex),
          OptionChoice<bool>(
            atKiosk: atKiosk,
            atKioskLabel: l10n.prepareAtKiosk,
            value: _duplex,
            choices: [
              (false, l10n.prepareOneSide, true),
              (true, l10n.prepareTwoSides, detail?.duplex ?? true),
            ],
            onChanged: (value) => _change(() => _duplex = value),
          ),
          if (detail != null && !detail.duplex)
            OptionWhy(l10n.optDuplexUnavailable),
          const SizedBox(height: Spacing.x4),

          OptionLabel(l10n.optPaper),
          OptionChoice<String>(
            atKiosk: atKiosk,
            atKioskLabel: l10n.prepareAtKiosk,
            value: _paper,
            choices: [('A4', 'A4', true), ('A3', 'A3', detail?.a3 ?? true)],
            onChanged: (value) => _change(() => _paper = value),
            locked: _realSize,
          ),
          if (detail != null && !detail.a3) OptionWhy(l10n.optA3Unavailable),
          const SizedBox(height: Spacing.x4),

          OptionLabel(l10n.optPerSheet),
          OptionChoice<int>(
            atKiosk: atKiosk,
            atKioskLabel: l10n.prepareAtKiosk,
            value: _perSheet,
            choices: const [(1, '1', true), (2, '2', true), (4, '4', true)],
            onChanged: (value) => _change(() => _perSheet = value),
            locked: _realSize,
          ),
          const SizedBox(height: Spacing.x4),

          OptionLabel(l10n.optPages),
          SegmentedButton<_Pages>(
            segments: [
              if (atKiosk)
                ButtonSegment(
                  value: _Pages.atKiosk,
                  label: Text(l10n.prepareAtKiosk),
                ),
              ButtonSegment(value: _Pages.all, label: Text(l10n.optPagesAll)),
              ButtonSegment(
                value: _Pages.range,
                label: Text(l10n.optPagesRange),
              ),
            ],
            selected: {_pages},
            onSelectionChanged: (value) => _change(() => _pages = value.single),
          ),
          if (_pages == _Pages.range) ...[
            const SizedBox(height: Spacing.x2),
            TextField(
              controller: _range,
              decoration: InputDecoration(
                hintText: l10n.optRangeHint,
                errorText: rangeInvalid
                    ? l10n.optRangeInvalid(_pageCount)
                    : null,
              ),
              onChanged: (_) => _change(() {}),
            ),
          ],
          const SizedBox(height: Spacing.x6),

          // "İndi çap et" or "Hazırla", above the kiosk (T-376).
          if (canNow && canPrepare) ...[
            OptionLabel(l10n.printModeTitle),
            SegmentedButton<PrintMode>(
              segments: [
                ButtonSegment(
                  value: PrintMode.now,
                  icon: const Icon(Icons.print_outlined),
                  label: Text(l10n.printModeNow),
                  enabled: nowOn,
                ),
                ButtonSegment(
                  value: PrintMode.prepare,
                  icon: const Icon(Icons.storefront_outlined),
                  label: Text(l10n.prepareMenu),
                ),
              ],
              selected: {mode},
              onSelectionChanged: (value) => _setMode(value.single),
            ),
            const SizedBox(height: Spacing.x1),
            Text(
              printingNow
                  ? l10n.printModeNowHint
                  : nowOn
                  ? l10n.printModePrepareHint
                  : l10n.printModeNowNone,
              style: theme.textTheme.bodySmall?.copyWith(
                color: tokens.neutral600,
              ),
            ),
            const SizedBox(height: Spacing.x4),
          ],

          OptionLabel(l10n.prepareKioskLabel),
          Card(
            margin: EdgeInsets.zero,
            child: Padding(
              padding: const EdgeInsets.all(Spacing.x4),
              child: kiosk == null
                  ? Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          printingNow
                              ? l10n.printNowChooseKiosk
                              : l10n.prepareAnyKiosk,
                          style: theme.textTheme.bodyLarge,
                        ),
                        const SizedBox(height: Spacing.x1),
                        Text(
                          printingNow
                              ? l10n.printNowKioskHint
                              : l10n.prepareKioskHint,
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: tokens.neutral600,
                          ),
                        ),
                        const SizedBox(height: Spacing.x3),
                        OutlinedButton.icon(
                          onPressed: _busy ? null : _chooseKiosk,
                          icon: const Icon(Icons.storefront_outlined),
                          label: Text(l10n.prepareChooseKiosk),
                        ),
                      ],
                    )
                  : Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          l10n.sessionBannerKiosk(kiosk.code, kiosk.name),
                          style: theme.textTheme.titleMedium,
                        ),
                        const SizedBox(height: Spacing.x2),
                        KioskStatusChip(status: kioskStatus!),
                        // T-351: a paid order may wait for a kiosk that is
                        // not working now; the customer is told so first.
                        if (kioskStatus != KioskStatus.online) ...[
                          const SizedBox(height: Spacing.x3),
                          NeoBanner(
                            tone: StatusTone.warning,
                            message: l10n.prepareKioskInactive,
                          ),
                        ],
                        const SizedBox(height: Spacing.x3),
                        Wrap(
                          spacing: Spacing.x2,
                          children: [
                            TextButton(
                              onPressed: _busy ? null : _chooseKiosk,
                              child: Text(l10n.prepareChangeKiosk),
                            ),
                            if (!printingNow)
                              TextButton(
                                onPressed: _busy
                                    ? null
                                    : () => _change(() => _kiosk = null),
                                child: Text(l10n.prepareClearKiosk),
                              ),
                          ],
                        ),
                      ],
                    ),
            ),
          ),

          if (kiosk != null) ...[
            const SizedBox(height: Spacing.x4),
            Card(
              margin: EdgeInsets.zero,
              child: Padding(
                padding: const EdgeInsets.all(Spacing.x4),
                child: _selected == null
                    ? Text(l10n.optRangeInvalid(_pageCount))
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
                            ),
                          ),
                          const SizedBox(height: Spacing.x1),
                          Text(
                            l10n.quoteSidesSheets(quote.sides, quote.sheets),
                          ),
                        ],
                      ),
              ),
            ),
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
              if (_error case final error?) ...[
                NeoBanner.error(message: error),
                const SizedBox(height: Spacing.x2),
              ],
              if (printingNow)
                NeoPrimaryButton(
                  label: quote == null || _selected == null
                      ? l10n.printNowPlain
                      : l10n.printNowPay(
                          formatMoney(quote.total, language: language),
                        ),
                  busy: _busy,
                  onPressed:
                      _busy ||
                          kiosk == null ||
                          quote == null ||
                          _selected == null
                      ? null
                      : () => unawaited(_printNow()),
                )
              else if (kiosk == null)
                NeoPrimaryButton(
                  label: l10n.prepareSave,
                  busy: _busy,
                  onPressed: _busy ? null : () => unawaited(_save()),
                )
              else ...[
                NeoPrimaryButton(
                  label: quote == null || _selected == null
                      ? l10n.preparePayPlain
                      : l10n.preparePay(
                          formatMoney(quote.total, language: language),
                        ),
                  busy: _busy,
                  onPressed: _busy || quote == null || _selected == null
                      ? null
                      : () => unawaited(_pay()),
                ),
                TextButton(
                  onPressed: _busy ? null : () => unawaited(_save()),
                  child: Text(l10n.prepareSaveUnpaid),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
