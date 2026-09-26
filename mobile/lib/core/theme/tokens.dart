// NeoPrint design tokens for the mobile application.
//
// Every value comes from `docs/DESIGN_SYSTEM.md`. Widgets read them through
// `Theme.of(context).extension<NeoPrintTokens>()` - or the `context.tokens`
// shorthand below - and never write a colour, a size or a spacing of their
// own, so a change to the system is one edit here and the same edit in
// `admin/src/styles/tokens.css` and
// `desktop/src/NeoPrint.Kiosk/Styles/Tokens.axaml`.

import 'package:flutter/material.dart';

/// The brand and neutral scale, light theme.
abstract final class LightPalette {
  static const brand50 = Color(0xFFEEF3FF);
  static const brand100 = Color(0xFFDCE6FF);
  static const brand300 = Color(0xFF9DB8FF);
  static const brand500 = Color(0xFF2B5FD9);
  static const brand600 = Color(0xFF1F4CBF);
  static const brand700 = Color(0xFF173A95);

  static const neutral0 = Color(0xFFFFFFFF);
  static const neutral50 = Color(0xFFF7F8FA);
  static const neutral100 = Color(0xFFEDEFF3);
  static const neutral200 = Color(0xFFDFE3E9);
  static const neutral400 = Color(0xFF9AA3B0);
  static const neutral600 = Color(0xFF5B6473);
  static const neutral800 = Color(0xFF2A303A);
  static const neutral900 = Color(0xFF171B21);

  static const success500 = Color(0xFF16A34A);
  static const successBg = Color(0xFFE8F7EE);
  static const warning500 = Color(0xFFD97706);
  static const warningBg = Color(0xFFFEF3E2);
  static const danger500 = Color(0xFFDC2626);
  static const dangerBg = Color(0xFFFDECEC);
  static const info500 = Color(0xFF0284C7);
  static const infoBg = Color(0xFFE6F4FB);
}

/// The same scale, dark theme.
abstract final class DarkPalette {
  static const brand50 = Color(0xFF111A2E);
  static const brand100 = Color(0xFF16243F);
  static const brand300 = Color(0xFF3D67C7);
  static const brand500 = Color(0xFF7BA4FF);
  static const brand600 = Color(0xFF5E8FF5);
  static const brand700 = Color(0xFF4A7AE0);

  static const neutral0 = Color(0xFF0F1216);
  static const neutral50 = Color(0xFF151A20);
  static const neutral100 = Color(0xFF1C222A);
  static const neutral200 = Color(0xFF252C36);
  static const neutral400 = Color(0xFF6B7684);
  static const neutral600 = Color(0xFF9AA3B0);
  static const neutral800 = Color(0xFFDDE2E8);
  static const neutral900 = Color(0xFFF2F4F7);

  static const success500 = Color(0xFF4ADE80);
  static const successBg = Color(0xFF12291C);
  static const warning500 = Color(0xFFFBBF24);
  static const warningBg = Color(0xFF2C2110);
  static const danger500 = Color(0xFFF87171);
  static const dangerBg = Color(0xFF2E1414);
  static const info500 = Color(0xFF38BDF8);
  static const infoBg = Color(0xFF0E2430);
}

/// The 4-based spacing scale. Constant across both themes.
abstract final class Spacing {
  static const x1 = 4.0;
  static const x2 = 8.0;
  static const x3 = 12.0;
  static const x4 = 16.0;
  static const x6 = 24.0;
  static const x8 = 32.0;
  static const x12 = 48.0;
  static const x16 = 64.0;
}

/// Corner radii.
abstract final class Radii {
  static const sm = 8.0;
  static const md = 12.0;
  static const lg = 16.0;

  /// A pill. Large enough that any control this is applied to is fully round.
  static const full = 999.0;
}

/// Type sizes, from the mobile column of the design system's table.
abstract final class FontSizes {
  static const display = 28.0;
  static const h1 = 24.0;
  static const h2 = 20.0;
  static const body = 16.0;
  static const bodySmall = 14.0;
  static const caption = 12.0;
  static const button = 16.0;
}

/// The three weights the system uses. Nothing lighter than 400.
abstract final class FontWeights {
  static const regular = FontWeight.w400;
  static const medium = FontWeight.w500;
  static const semibold = FontWeight.w600;
}

/// Transition lengths.
abstract final class Motion {
  static const fast = Duration(milliseconds: 150);
  static const base = Duration(milliseconds: 250);
  static const easing = Curves.easeOut;
}

/// Fixed sizes the design system names.
abstract final class Sizes {
  /// The minimum touch target on a phone.
  static const touchTarget = 48.0;

  /// A full-width primary button.
  static const controlHeight = 52.0;

  static const statusChipHeight = 24.0;

  /// Forms are one column and stop growing on a tablet.
  static const formMaxWidth = 440.0;
}

/// What a status means, independent of the colour it is drawn in.
enum StatusTone { success, warning, danger, neutral, info }

/// The tokens that cannot be expressed as a Material [ColorScheme] entry.
///
/// A [ThemeExtension] rather than a set of globals: the values differ between
/// light and dark, and this way a widget reads them from the theme in scope
/// instead of asking which theme is active.
@immutable
class NeoPrintTokens extends ThemeExtension<NeoPrintTokens> {
  const NeoPrintTokens({
    required this.brand50,
    required this.brand100,
    required this.brand500,
    required this.neutral0,
    required this.neutral50,
    required this.neutral100,
    required this.neutral200,
    required this.neutral400,
    required this.neutral600,
    required this.neutral800,
    required this.neutral900,
    required this.success,
    required this.successBackground,
    required this.warning,
    required this.warningBackground,
    required this.danger,
    required this.dangerBackground,
    required this.info,
    required this.infoBackground,
  });

  final Color brand50;
  final Color brand100;
  final Color brand500;

  final Color neutral0;
  final Color neutral50;
  final Color neutral100;
  final Color neutral200;
  final Color neutral400;
  final Color neutral600;
  final Color neutral800;
  final Color neutral900;

  final Color success;
  final Color successBackground;
  final Color warning;
  final Color warningBackground;
  final Color danger;
  final Color dangerBackground;
  final Color info;
  final Color infoBackground;

  static const light = NeoPrintTokens(
    brand50: LightPalette.brand50,
    brand100: LightPalette.brand100,
    brand500: LightPalette.brand500,
    neutral0: LightPalette.neutral0,
    neutral50: LightPalette.neutral50,
    neutral100: LightPalette.neutral100,
    neutral200: LightPalette.neutral200,
    neutral400: LightPalette.neutral400,
    neutral600: LightPalette.neutral600,
    neutral800: LightPalette.neutral800,
    neutral900: LightPalette.neutral900,
    success: LightPalette.success500,
    successBackground: LightPalette.successBg,
    warning: LightPalette.warning500,
    warningBackground: LightPalette.warningBg,
    danger: LightPalette.danger500,
    dangerBackground: LightPalette.dangerBg,
    info: LightPalette.info500,
    infoBackground: LightPalette.infoBg,
  );

  static const dark = NeoPrintTokens(
    brand50: DarkPalette.brand50,
    brand100: DarkPalette.brand100,
    brand500: DarkPalette.brand500,
    neutral0: DarkPalette.neutral0,
    neutral50: DarkPalette.neutral50,
    neutral100: DarkPalette.neutral100,
    neutral200: DarkPalette.neutral200,
    neutral400: DarkPalette.neutral400,
    neutral600: DarkPalette.neutral600,
    neutral800: DarkPalette.neutral800,
    neutral900: DarkPalette.neutral900,
    success: DarkPalette.success500,
    successBackground: DarkPalette.successBg,
    warning: DarkPalette.warning500,
    warningBackground: DarkPalette.warningBg,
    danger: DarkPalette.danger500,
    dangerBackground: DarkPalette.dangerBg,
    info: DarkPalette.info500,
    infoBackground: DarkPalette.infoBg,
  );

  /// The pair a status is drawn with: the foreground and its background.
  ({Color foreground, Color background}) forTone(
    StatusTone tone,
  ) => switch (tone) {
    StatusTone.success => (foreground: success, background: successBackground),
    StatusTone.warning => (foreground: warning, background: warningBackground),
    StatusTone.danger => (foreground: danger, background: dangerBackground),
    StatusTone.info => (foreground: info, background: infoBackground),
    StatusTone.neutral => (foreground: neutral600, background: neutral100),
  };

  @override
  NeoPrintTokens copyWith({
    Color? brand50,
    Color? brand100,
    Color? brand500,
    Color? neutral0,
    Color? neutral50,
    Color? neutral100,
    Color? neutral200,
    Color? neutral400,
    Color? neutral600,
    Color? neutral800,
    Color? neutral900,
    Color? success,
    Color? successBackground,
    Color? warning,
    Color? warningBackground,
    Color? danger,
    Color? dangerBackground,
    Color? info,
    Color? infoBackground,
  }) {
    return NeoPrintTokens(
      brand50: brand50 ?? this.brand50,
      brand100: brand100 ?? this.brand100,
      brand500: brand500 ?? this.brand500,
      neutral0: neutral0 ?? this.neutral0,
      neutral50: neutral50 ?? this.neutral50,
      neutral100: neutral100 ?? this.neutral100,
      neutral200: neutral200 ?? this.neutral200,
      neutral400: neutral400 ?? this.neutral400,
      neutral600: neutral600 ?? this.neutral600,
      neutral800: neutral800 ?? this.neutral800,
      neutral900: neutral900 ?? this.neutral900,
      success: success ?? this.success,
      successBackground: successBackground ?? this.successBackground,
      warning: warning ?? this.warning,
      warningBackground: warningBackground ?? this.warningBackground,
      danger: danger ?? this.danger,
      dangerBackground: dangerBackground ?? this.dangerBackground,
      info: info ?? this.info,
      infoBackground: infoBackground ?? this.infoBackground,
    );
  }

  @override
  NeoPrintTokens lerp(ThemeExtension<NeoPrintTokens>? other, double t) {
    if (other is! NeoPrintTokens) {
      return this;
    }

    Color mix(Color a, Color b) => Color.lerp(a, b, t)!;

    return NeoPrintTokens(
      brand50: mix(brand50, other.brand50),
      brand100: mix(brand100, other.brand100),
      brand500: mix(brand500, other.brand500),
      neutral0: mix(neutral0, other.neutral0),
      neutral50: mix(neutral50, other.neutral50),
      neutral100: mix(neutral100, other.neutral100),
      neutral200: mix(neutral200, other.neutral200),
      neutral400: mix(neutral400, other.neutral400),
      neutral600: mix(neutral600, other.neutral600),
      neutral800: mix(neutral800, other.neutral800),
      neutral900: mix(neutral900, other.neutral900),
      success: mix(success, other.success),
      successBackground: mix(successBackground, other.successBackground),
      warning: mix(warning, other.warning),
      warningBackground: mix(warningBackground, other.warningBackground),
      danger: mix(danger, other.danger),
      dangerBackground: mix(dangerBackground, other.dangerBackground),
      info: mix(info, other.info),
      infoBackground: mix(infoBackground, other.infoBackground),
    );
  }
}

/// `context.tokens` instead of the full `Theme.of(context).extension<...>()!`.
extension NeoPrintTokensExtension on BuildContext {
  NeoPrintTokens get tokens =>
      Theme.of(this).extension<NeoPrintTokens>() ?? NeoPrintTokens.light;
}
