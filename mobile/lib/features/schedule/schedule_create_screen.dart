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
import '../../shared/widgets/option_widgets.dart';
import '../files/file_models.dart';
import '../files/files_controller.dart';
import '../kiosk_search/kiosk_models.dart';
import '../kiosk_search/kiosk_picker_sheet.dart';
import '../kiosk_search/kiosk_search_controller.dart';
import '../kiosk_search/kiosk_status_view.dart';
import '../payments/payment_models.dart' show toCents;
import '../session/session_controller.dart';
import '../session/session_models.dart';
import 'schedule_actions.dart';
import 'schedule_repository.dart';

/// "Çapı planla" (plan sections 8.2 and 9.5, step 1; docs/DESIGN_SYSTEM.md
/// "Mobil: planlanmış çap"): a file, a kiosk, the options and a time, priced
/// by that kiosk's current profile and paid now.
class ScheduleCreateScreen extends ConsumerStatefulWidget {
  const ScheduleCreateScreen({super.key, this.file});

  /// Chosen already when it came from the file's menu.
  final UserFile? file;

  @override
  ConsumerState<ScheduleCreateScreen> createState() =>
      _ScheduleCreateScreenState();
}

enum _Pages { all, range }

class _ScheduleCreateScreenState extends ConsumerState<ScheduleCreateScreen> {
  static const _maxCopies = 50;
  static const _quoteDelay = Duration(milliseconds: 300);

  late UserFile? _file = widget.file;
  KioskSummary? _kiosk;
  DateTime? _at;

  int _copies = 1;
  bool _color = false;
  bool _duplex = false;
  late String _paper = widget.file?.realSizePaper ?? 'A4';
  int _perSheet = 1;

  /// A scan or layout: its paper and one page to a sheet are fixed.
  String? get _realSizePaper => _file?.realSizePaper;
  _Pages _pages = _Pages.all;
  final _range = TextEditingController();

  Timer? _quoteTimer;
  OrderQuote? _quote;
  String? _quoteError;

  bool _busy = false;
  String? _error;

  // One per set of choices: a retry after a lost answer is the same schedule.
  String _key = newIdempotencyKey();

  @override
  void dispose() {
    _quoteTimer?.cancel();
    _range.dispose();
    super.dispose();
  }

  int get _pageCount => _file?.pageCount ?? 0;

  OrderOptions get _options => OrderOptions(
    copies: _copies,
    color: _color,
    duplex: _duplex,
    paper: _paper,
    pagesPerSheet: _perSheet,
    pageRange: _pages == _Pages.range ? _range.text.trim() : null,
    actualSize: _realSizePaper != null,
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

    if (_kiosk == null || _file == null) {
      setState(() => _quote = null);
      return;
    }

    _quoteTimer = Timer(_quoteDelay, () => unawaited(_loadQuote()));
  }

  Future<OrderQuote?> _loadQuote() async {
    final kiosk = _kiosk;
    final pages = _selected;

    if (kiosk == null || _file == null || pages == null) {
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

  Future<void> _chooseFile() async {
    final chosen = await showModalBottomSheet<UserFile>(
      context: context,
      isScrollControlled: true,
      builder: (_) => const _FileSheet(),
    );

    if (chosen != null) {
      _change(() {
        _file = chosen;
        if (chosen.realSizePaper case final realSize?) {
          _paper = realSize;
          _perSheet = 1;
        }
        _pages = _Pages.all;
        _range.clear();
      });
    }
  }

  Future<void> _chooseKiosk() async {
    final chosen = await showKioskPicker(context);

    if (chosen != null) {
      _change(() => _kiosk = chosen);
    }
  }

  Future<void> _chooseTime() async {
    final at = await pickScheduleTime(context, initial: _at);

    if (at != null) {
      _change(() => _at = at);
    }
  }

  Future<void> _pay({double? amount}) async {
    final l10n = AppLocalizations.of(context);
    final file = _file;
    final kiosk = _kiosk;
    final at = _at;
    final shown = amount ?? _quote?.total;

    final missing = file == null
        ? l10n.schedNeedFile
        : kiosk == null
        ? l10n.schedNeedKiosk
        : at == null
        ? l10n.schedNeedTime
        : _selected == null
        ? l10n.optRangeInvalid(_pageCount)
        : null;

    if (missing != null || shown == null) {
      setState(() => _error = missing);
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      final placed = await ref
          .read(scheduleRepositoryProvider)
          .create(
            kiosk: kiosk!.id,
            fileId: file!.id,
            options: _options,
            scheduledAt: at!,
            expectedAmount: shown,
            idempotencyKey: _key,
          );

      ref.invalidate(schedulesProvider);

      if (mounted) {
        context.pushReplacement(Routes.pay(placed.paymentId));
      }
    } on ApiException catch (error) {
      // A definite answer: the next try is a new schedule.
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

      setState(() => _error = l10n.schedFailed(apiErrorText(l10n, error)));
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
    final file = _file;
    final kiosk = _kiosk;
    final at = _at;
    final detail = kiosk == null
        ? null
        : ref.watch(kioskDetailProvider(kiosk.id)).valueOrNull;
    final quote = _quote;
    final rangeInvalid =
        _pages == _Pages.range && _range.text.isNotEmpty && _selected == null;

    return Scaffold(
      appBar: AppBar(title: Text(l10n.schedCreateTitle)),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(
          Spacing.x4,
          Spacing.x2,
          Spacing.x4,
          Spacing.x8,
        ),
        children: [
          OptionLabel(l10n.schedFileLabel),
          _Section(
            child: file == null
                ? Align(
                    alignment: Alignment.centerLeft,
                    child: OutlinedButton.icon(
                      onPressed: _busy ? null : _chooseFile,
                      icon: const Icon(Icons.description_outlined),
                      label: Text(l10n.schedChooseFile),
                    ),
                  )
                : Row(
                    children: [
                      Icon(Icons.description_outlined, color: tokens.brand500),
                      const SizedBox(width: Spacing.x3),
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              file.fileName,
                              style: theme.textTheme.titleMedium,
                              maxLines: 2,
                              overflow: TextOverflow.ellipsis,
                            ),
                            Text(
                              l10n.filesPageCount(file.pageCount),
                              style: theme.textTheme.bodySmall?.copyWith(
                                color: tokens.neutral600,
                              ),
                            ),
                          ],
                        ),
                      ),
                      TextButton(
                        onPressed: _busy ? null : _chooseFile,
                        child: Text(l10n.schedChange),
                      ),
                    ],
                  ),
          ),
          const SizedBox(height: Spacing.x4),

          OptionLabel(l10n.schedKioskLabel),
          _Section(
            child: kiosk == null
                ? Align(
                    alignment: Alignment.centerLeft,
                    child: OutlinedButton.icon(
                      onPressed: _busy ? null : _chooseKiosk,
                      icon: const Icon(Icons.storefront_outlined),
                      label: Text(l10n.schedChooseKiosk),
                    ),
                  )
                : Row(
                    children: [
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              l10n.sessionBannerKiosk(kiosk.code, kiosk.name),
                              style: theme.textTheme.titleMedium,
                            ),
                            const SizedBox(height: Spacing.x2),
                            KioskStatusChip(status: kiosk.status),
                          ],
                        ),
                      ),
                      TextButton(
                        onPressed: _busy ? null : _chooseKiosk,
                        child: Text(l10n.schedChange),
                      ),
                    ],
                  ),
          ),
          const SizedBox(height: Spacing.x4),

          OptionLabel(l10n.schedTimeLabel),
          _Section(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                if (at == null)
                  OutlinedButton.icon(
                    onPressed: _busy ? null : _chooseTime,
                    icon: const Icon(Icons.edit_calendar_outlined),
                    label: Text(l10n.schedChooseTime),
                  )
                else
                  Row(
                    children: [
                      Icon(Icons.event_outlined, color: tokens.brand500),
                      const SizedBox(width: Spacing.x3),
                      Expanded(
                        child: Text(
                          scheduleTime(context, at),
                          style: theme.textTheme.titleMedium,
                        ),
                      ),
                      TextButton(
                        onPressed: _busy ? null : _chooseTime,
                        child: Text(l10n.schedChange),
                      ),
                    ],
                  ),
                const SizedBox(height: Spacing.x2),
                Text(
                  l10n.schedTimeHint(scheduleMinLead.inMinutes),
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: tokens.neutral600,
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(height: Spacing.x6),

          if (_realSizePaper case final realSize?)
            RealSizeNote(l10n.optRealSize(realSize)),
          OptionLabel(l10n.optCopies),
          Row(
            children: [
              IconButton.outlined(
                tooltip: l10n.optCopiesLess,
                onPressed: _copies > 1 ? () => _change(() => _copies--) : null,
                icon: const Icon(Icons.remove),
              ),
              SizedBox(
                width: Sizes.touchTarget,
                child: Text(
                  '$_copies',
                  textAlign: TextAlign.center,
                  style: theme.textTheme.titleLarge,
                ),
              ),
              IconButton.outlined(
                tooltip: l10n.optCopiesMore,
                onPressed: _copies < _maxCopies
                    ? () => _change(() => _copies++)
                    : null,
                icon: const Icon(Icons.add),
              ),
            ],
          ),
          const SizedBox(height: Spacing.x4),

          OptionLabel(l10n.optColor),
          OptionChoice<bool>(
            atKiosk: false,
            atKioskLabel: '',
            value: _color,
            choices: [
              (false, l10n.optBlackWhite, true),
              (true, l10n.optColorful, detail?.color ?? true),
            ],
            onChanged: (value) => _change(() => _color = value ?? false),
          ),
          if (detail != null && !detail.color)
            OptionWhy(l10n.optColorUnavailable),
          const SizedBox(height: Spacing.x4),

          OptionLabel(l10n.optDuplex),
          OptionChoice<bool>(
            atKiosk: false,
            atKioskLabel: '',
            value: _duplex,
            choices: [
              (false, l10n.prepareOneSide, true),
              (true, l10n.prepareTwoSides, detail?.duplex ?? true),
            ],
            onChanged: (value) => _change(() => _duplex = value ?? false),
          ),
          if (detail != null && !detail.duplex)
            OptionWhy(l10n.optDuplexUnavailable),
          const SizedBox(height: Spacing.x4),

          OptionLabel(l10n.optPaper),
          OptionChoice<String>(
            atKiosk: false,
            atKioskLabel: '',
            value: _paper,
            choices: [('A4', 'A4', true), ('A3', 'A3', detail?.a3 ?? true)],
            onChanged: (value) => _change(() => _paper = value ?? 'A4'),
            locked: _realSizePaper != null,
          ),
          if (detail != null && !detail.a3) OptionWhy(l10n.optA3Unavailable),
          const SizedBox(height: Spacing.x4),

          OptionLabel(l10n.optPerSheet),
          OptionChoice<int>(
            atKiosk: false,
            atKioskLabel: '',
            value: _perSheet,
            choices: const [(1, '1', true), (2, '2', true), (4, '4', true)],
            onChanged: (value) => _change(() => _perSheet = value ?? 1),
            locked: _realSizePaper != null,
          ),
          const SizedBox(height: Spacing.x4),

          OptionLabel(l10n.optPages),
          SegmentedButton<_Pages>(
            segments: [
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

          if (kiosk != null && file != null) ...[
            const SizedBox(height: Spacing.x6),
            _Section(
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
                        Text(l10n.quoteSidesSheets(quote.sides, quote.sheets)),
                      ],
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
              NeoPrimaryButton(
                label: quote == null || _selected == null
                    ? l10n.schedPayPlain
                    : l10n.schedPayAndSchedule(
                        formatMoney(quote.total, language: language),
                      ),
                busy: _busy,
                onPressed: _busy ? null : () => unawaited(_pay()),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// A card around one part of the form.
class _Section extends StatelessWidget {
  const _Section({required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context) => Card(
    margin: EdgeInsets.zero,
    child: Padding(padding: const EdgeInsets.all(Spacing.x4), child: child),
  );
}

/// The user's files ready to print.
class _FileSheet extends ConsumerWidget {
  const _FileSheet();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = AppLocalizations.of(context);
    final state = ref.watch(filesControllerProvider);
    final ready = [
      for (final file in state.files)
        if (file.status == FileStatus.ready && file.pageCount > 0) file,
    ];

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.all(Spacing.x4),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              l10n.schedFileSheetTitle,
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: Spacing.x3),
            Flexible(
              child: state.loading
                  ? const Center(child: CircularProgressIndicator())
                  : ready.isEmpty
                  ? Text(l10n.schedNoReadyFiles)
                  : ListView(
                      shrinkWrap: true,
                      children: [
                        for (final file in ready)
                          ListTile(
                            contentPadding: EdgeInsets.zero,
                            leading: Icon(
                              Icons.description_outlined,
                              color: context.tokens.brand500,
                            ),
                            title: Text(file.fileName),
                            subtitle: Text(l10n.filesPageCount(file.pageCount)),
                            onTap: () => Navigator.pop(context, file),
                          ),
                      ],
                    ),
            ),
          ],
        ),
      ),
    );
  }
}
