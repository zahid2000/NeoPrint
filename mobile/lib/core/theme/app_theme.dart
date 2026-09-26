import 'package:flutter/material.dart';

import 'tokens.dart';

/// The application's theme, built from the design tokens.
///
/// Light and dark are both provided and follow the system setting: this is a
/// phone in someone's hand, unlike the kiosk, which is always light so that it
/// stays readable in a public space (docs/DESIGN_SYSTEM.md section 1).
///
/// Nothing here invents a value. Every colour, size and radius comes from
/// `tokens.dart`, and a widget that needs one reads it from the theme rather
/// than writing its own.
abstract final class AppTheme {
  /// The font family. Falls back to the platform's own face where Inter has
  /// not been bundled yet (docs/DECISIONS.md).
  static const fontFamily = 'Inter';

  static const fontFamilyFallback = <String>[
    'system-ui',
    'Segoe UI',
    'Roboto',
    'sans-serif',
  ];

  static ThemeData light() => _build(Brightness.light, NeoPrintTokens.light);

  static ThemeData dark() => _build(Brightness.dark, NeoPrintTokens.dark);

  static ThemeData _build(Brightness brightness, NeoPrintTokens tokens) {
    final scheme = _scheme(brightness, tokens);
    final textTheme = _textTheme(tokens);

    return ThemeData(
      useMaterial3: true,
      brightness: brightness,
      colorScheme: scheme,
      extensions: [tokens],
      scaffoldBackgroundColor: tokens.neutral50,
      canvasColor: tokens.neutral0,
      dividerColor: tokens.neutral200,
      fontFamily: fontFamily,
      fontFamilyFallback: fontFamilyFallback,
      textTheme: textTheme,

      appBarTheme: AppBarTheme(
        centerTitle: false,
        elevation: 0,
        scrolledUnderElevation: 0,
        backgroundColor: tokens.neutral0,
        foregroundColor: tokens.neutral900,
        titleTextStyle: textTheme.titleLarge,
      ),

      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          minimumSize: const Size.fromHeight(Sizes.controlHeight),
          textStyle: const TextStyle(
            fontSize: FontSizes.button,
            fontWeight: FontWeights.medium,
          ),
          shape: const RoundedRectangleBorder(
            borderRadius: BorderRadius.all(Radius.circular(Radii.md)),
          ),
        ),
      ),

      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          minimumSize: const Size.fromHeight(Sizes.controlHeight),
          side: BorderSide(color: tokens.neutral200),
          textStyle: const TextStyle(
            fontSize: FontSizes.button,
            fontWeight: FontWeights.medium,
          ),
          shape: const RoundedRectangleBorder(
            borderRadius: BorderRadius.all(Radius.circular(Radii.md)),
          ),
        ),
      ),

      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(
          // Comfortably above the 48dp minimum touch target, even inline.
          minimumSize: const Size(Sizes.touchTarget, Sizes.touchTarget),
          textStyle: const TextStyle(
            fontSize: FontSizes.body,
            fontWeight: FontWeights.medium,
          ),
        ),
      ),

      /*
       * The label sits above the field rather than inside it: a placeholder
       * that doubles as the label disappears as soon as there is text, and
       * then nobody can check what they were asked for
       * (docs/DESIGN_SYSTEM.md section 4).
       */
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: tokens.neutral0,
        floatingLabelBehavior: FloatingLabelBehavior.always,
        contentPadding: const EdgeInsets.symmetric(
          horizontal: Spacing.x4,
          vertical: Spacing.x4,
        ),
        labelStyle: TextStyle(
          color: tokens.neutral600,
          fontSize: FontSizes.bodySmall,
        ),
        helperStyle: TextStyle(
          color: tokens.neutral600,
          fontSize: FontSizes.caption,
        ),
        errorStyle: TextStyle(
          color: tokens.danger,
          fontSize: FontSizes.caption,
        ),
        border: _fieldBorder(tokens.neutral200),
        enabledBorder: _fieldBorder(tokens.neutral200),
        focusedBorder: _fieldBorder(tokens.brand500, width: 2),
        errorBorder: _fieldBorder(tokens.danger),
        focusedErrorBorder: _fieldBorder(tokens.danger, width: 2),
      ),

      cardTheme: CardThemeData(
        elevation: 0,
        color: tokens.neutral0,
        margin: EdgeInsets.zero,
        shape: RoundedRectangleBorder(
          side: BorderSide(color: tokens.neutral200),
          borderRadius: const BorderRadius.all(Radius.circular(Radii.lg)),
        ),
      ),

      checkboxTheme: CheckboxThemeData(
        shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.all(Radius.circular(Radii.sm / 2)),
        ),
        side: BorderSide(color: tokens.neutral400, width: 1.5),
      ),

      dividerTheme: DividerThemeData(color: tokens.neutral200, space: 1),

      snackBarTheme: SnackBarThemeData(
        behavior: SnackBarBehavior.floating,
        shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.all(Radius.circular(Radii.md)),
        ),
        contentTextStyle: TextStyle(
          color: tokens.neutral0,
          fontSize: FontSizes.bodySmall,
        ),
      ),

      navigationBarTheme: NavigationBarThemeData(
        backgroundColor: tokens.neutral0,
        indicatorColor: tokens.brand100,
        height: 68,
        labelTextStyle: WidgetStatePropertyAll(
          TextStyle(
            fontSize: FontSizes.caption,
            fontWeight: FontWeights.medium,
            color: tokens.neutral600,
          ),
        ),
      ),

      dialogTheme: DialogThemeData(
        backgroundColor: tokens.neutral0,
        shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.all(Radius.circular(Radii.lg)),
        ),
      ),
    );
  }

  static OutlineInputBorder _fieldBorder(Color color, {double width = 1}) {
    return OutlineInputBorder(
      borderRadius: const BorderRadius.all(Radius.circular(Radii.md)),
      borderSide: BorderSide(color: color, width: width),
    );
  }

  static ColorScheme _scheme(Brightness brightness, NeoPrintTokens tokens) {
    return ColorScheme(
      brightness: brightness,
      primary: tokens.brand500,
      onPrimary: brightness == Brightness.light
          ? tokens.neutral0
          : tokens.neutral900,
      primaryContainer: tokens.brand100,
      onPrimaryContainer: tokens.neutral900,
      secondary: tokens.neutral600,
      onSecondary: tokens.neutral0,
      secondaryContainer: tokens.neutral100,
      onSecondaryContainer: tokens.neutral800,
      tertiary: tokens.info,
      onTertiary: tokens.neutral0,
      tertiaryContainer: tokens.infoBackground,
      onTertiaryContainer: tokens.info,
      error: tokens.danger,
      onError: brightness == Brightness.light
          ? tokens.neutral0
          : tokens.neutral900,
      errorContainer: tokens.dangerBackground,
      onErrorContainer: tokens.danger,
      surface: tokens.neutral0,
      onSurface: tokens.neutral800,
      surfaceContainerLowest: tokens.neutral0,
      surfaceContainerLow: tokens.neutral50,
      surfaceContainer: tokens.neutral100,
      surfaceContainerHigh: tokens.neutral100,
      surfaceContainerHighest: tokens.neutral200,
      onSurfaceVariant: tokens.neutral600,
      outline: tokens.neutral400,
      outlineVariant: tokens.neutral200,
      inverseSurface: tokens.neutral800,
      onInverseSurface: tokens.neutral0,
    );
  }

  /// The type scale from the design system's mobile column.
  static TextTheme _textTheme(NeoPrintTokens tokens) {
    return TextTheme(
      displaySmall: TextStyle(
        fontSize: FontSizes.display,
        fontWeight: FontWeights.semibold,
        height: 1.25,
        color: tokens.neutral900,
      ),
      headlineMedium: TextStyle(
        fontSize: FontSizes.h1,
        fontWeight: FontWeights.semibold,
        height: 1.3,
        color: tokens.neutral900,
      ),
      titleLarge: TextStyle(
        fontSize: FontSizes.h2,
        fontWeight: FontWeights.semibold,
        height: 1.3,
        color: tokens.neutral900,
      ),
      titleMedium: TextStyle(
        fontSize: FontSizes.body,
        fontWeight: FontWeights.semibold,
        height: 1.4,
        color: tokens.neutral900,
      ),
      bodyLarge: TextStyle(
        fontSize: FontSizes.body,
        fontWeight: FontWeights.regular,
        height: 1.5,
        color: tokens.neutral800,
      ),
      bodyMedium: TextStyle(
        fontSize: FontSizes.bodySmall,
        fontWeight: FontWeights.regular,
        height: 1.5,
        color: tokens.neutral800,
      ),
      bodySmall: TextStyle(
        fontSize: FontSizes.caption,
        fontWeight: FontWeights.regular,
        height: 1.5,
        color: tokens.neutral600,
      ),
      labelLarge: const TextStyle(
        fontSize: FontSizes.button,
        fontWeight: FontWeights.medium,
      ),
    );
  }
}
