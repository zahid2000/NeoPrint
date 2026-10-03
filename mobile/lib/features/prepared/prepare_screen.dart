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
import '../../shared/widgets/neo_primary_button.dart';
import '../files/file_models.dart';
import '../../shared/widgets/option_widgets.dart';
import '../kiosk_search/kiosk_models.dart';
import '../kiosk_search/kiosk_picker_sheet.dart';
import '../kiosk_search/kiosk_search_controller.dart';
import '../kiosk_search/kiosk_status_view.dart';
import '../payments/payment_models.dart' show toCents;
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
class PrepareScreen extends ConsumerStatefulWidget {
  const PrepareScreen({super.key, required this.file});

  final UserFile file;

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
  String? _paper;
  int? _perSheet;
  _Pages _pages = _Pages.atKiosk;
  final _range = TextEditingController();

  KioskSummary? _kiosk;

  Timer? _quoteTimer;
  OrderQuote? _quote;
  String? _quoteError;

  bool _busy = false;
  String? _error;

  // One per set of choices: a retry after a lost answer is the same order.
  String _key = newIdempotencyKey();

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

  Future<void> _chooseKiosk() async {
    final chosen = await showKioskPicker(context);

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
    final kiosk = _kiosk;
    final detail = kiosk == null
        ? null
        : ref.watch(kioskDetailProvider(kiosk.id)).valueOrNull;
    final atKiosk = kiosk == null;
    final quote = _quote;
    final rangeInvalid =
        _pages == _Pages.range && _range.text.isNotEmpty && _selected == null;

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
          Text(
            l10n.filesPageCount(_pageCount),
            style: theme.textTheme.bodyMedium?.copyWith(
              color: tokens.neutral600,
            ),
          ),
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
                          l10n.prepareAnyKiosk,
                          style: theme.textTheme.bodyLarge,
                        ),
                        const SizedBox(height: Spacing.x1),
                        Text(
                          l10n.prepareKioskHint,
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
                        KioskStatusChip(status: kiosk.status),
                        const SizedBox(height: Spacing.x3),
                        Wrap(
                          spacing: Spacing.x2,
                          children: [
                            TextButton(
                              onPressed: _busy ? null : _chooseKiosk,
                              child: Text(l10n.prepareChangeKiosk),
                            ),
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
              if (kiosk == null)
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
