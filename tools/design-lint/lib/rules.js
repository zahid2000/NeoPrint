// The rules per app. Each finds a literal where a token belongs: a colour,
// a font size, or spacing (padding, margin, gap). Sizes of things - widths,
// heights, icon boxes, border widths - are not spacing and are not checked.

/** A CSS length that is not zero: 4px, 0.75rem, 1.5em. */
const cssLength = /(?<![\w-])(?!0(?:\.0+)?(?:px|rem|em)\b)\d*\.?\d+(?:px|rem|em)\b/;

const admin = [
  {
    name: 'css-spacing',
    // padding / margin / gap and their longhands, with a literal length in the value.
    pattern: /\b(?:padding|margin|gap|row-gap|column-gap)(?:-(?:top|right|bottom|left|inline|block)(?:-(?:start|end))?)?\s*:\s*[^;{}]*;/g,
    accept: (match) => !cssLength.test(match[0].replace(/var\([^)]*\)/g, '')),
  },
  {
    name: 'css-font-size',
    pattern: /\bfont-size\s*:\s*[^;{}]*;/g,
    accept: (match) => !cssLength.test(match[0].replace(/var\([^)]*\)/g, '')),
  },
  {
    name: 'css-colour',
    pattern: /(?:#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\()/g,
    // Inside a var() fallback or a comment is still a literal; only token
    // references are clean, so nothing is accepted here.
    accept: (match, line) => /^\s*(\/\/|\*|\/\*)/.test(line),
  },
];

const mobile = [
  {
    name: 'dart-spacing',
    // EdgeInsets with a number literal among its arguments.
    pattern: /EdgeInsets(?:Directional)?\.[a-zA-Z]+\(([^()]*)\)/g,
    accept: (match) => !/(?<![\w.])\d+(?:\.\d+)?(?![\w.])/.test(match[1].replace(/\b0(?:\.0)?\b/g, '')),
  },
  {
    name: 'dart-gap',
    // A SizedBox used as a gap with a literal size.
    pattern: /SizedBox\(\s*(?:height|width)\s*:\s*\d+(?:\.\d+)?\s*[,)]/g,
  },
  {
    name: 'dart-font-size',
    pattern: /fontSize\s*:\s*\d+(?:\.\d+)?/g,
  },
  {
    name: 'dart-colour',
    pattern: /\bColor\(0x[0-9a-fA-F]+\)|\bColors\.(?!transparent\b)[a-zA-Z]+/g,
  },
];

const kiosk = [
  {
    name: 'axaml-spacing',
    pattern: /\b(?:Padding|Margin|Spacing|RowSpacing|ColumnSpacing)="([^"{]*)"/g,
    accept: (match) => /^[0,\s.]*$/.test(match[1]),
  },
  {
    name: 'axaml-font-size',
    pattern: /\bFontSize="[^"{]*"/g,
  },
  {
    name: 'axaml-colour',
    pattern: /="#[0-9a-fA-F]{3,8}"/g,
  },
  {
    // A named colour ("White", "Red") is a literal too.
    name: 'axaml-named-colour',
    pattern: /\b(?:Background|Foreground|BorderBrush|Fill|Stroke)="(?![{#])(?!Transparent")[^"]+"/g,
  },
];

export function rulesFor(app) {
  return { admin, mobile, kiosk }[app] ?? [];
}
