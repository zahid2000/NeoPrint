import assert from 'node:assert/strict';
import { test } from 'node:test';

import { lintRepository, lintText } from '../lib/lint.js';

const rules = (app, text) => lintText(app, text).map((finding) => finding.rule);

test('the repository has no hardcoded colour, font size or spacing', () => {
  assert.deepEqual(lintRepository(), []);
});

test('admin: spacing and font sizes come from tokens', () => {
  assert.deepEqual(rules('admin', 'padding: 0.75rem 1rem;'), ['css-spacing']);
  assert.deepEqual(rules('admin', 'margin-bottom: 8px;'), ['css-spacing']);
  assert.deepEqual(rules('admin', 'gap: 0.25rem var(--space-2);'), ['css-spacing']);
  assert.deepEqual(rules('admin', 'font-size: 14px;'), ['css-font-size']);

  assert.deepEqual(rules('admin', 'padding: var(--space-3) var(--space-4);'), []);
  assert.deepEqual(rules('admin', 'margin: 0;'), []);
  assert.deepEqual(rules('admin', 'font-size: var(--font-size-caption);'), []);
  // Sizes of things are not spacing.
  assert.deepEqual(rules('admin', 'min-width: 16rem;'), []);
  assert.deepEqual(rules('admin', 'border: 1px solid var(--neutral-200);'), []);
});

test('admin: colours come from tokens', () => {
  assert.deepEqual(rules('admin', 'color: #2b5fd9;'), ['css-colour']);
  assert.deepEqual(rules('admin', 'background: rgba(0, 0, 0, 0.4);'), ['css-colour']);
  assert.deepEqual(rules('admin', 'color: var(--brand-600);'), []);
});

test('mobile: spacing, font sizes and colours come from tokens', () => {
  assert.deepEqual(rules('mobile', 'padding: const EdgeInsets.all(16),'), ['dart-spacing']);
  assert.deepEqual(rules('mobile', 'const SizedBox(height: 24),'), ['dart-gap']);
  assert.deepEqual(rules('mobile', 'style: TextStyle(fontSize: 18),'), ['dart-font-size']);
  assert.deepEqual(rules('mobile', 'color: Color(0xFF2B5FD9),'), ['dart-colour']);
  assert.deepEqual(rules('mobile', 'color: Colors.red,'), ['dart-colour']);

  assert.deepEqual(rules('mobile', 'padding: const EdgeInsets.all(Spacing.x4),'), []);
  assert.deepEqual(rules('mobile', 'EdgeInsets.symmetric(horizontal: Spacing.x2, vertical: 0),'), []);
  assert.deepEqual(rules('mobile', 'const SizedBox(height: Spacing.x6),'), []);
  assert.deepEqual(rules('mobile', 'color: Colors.transparent,'), []);
});

test('kiosk: spacing, font sizes and colours come from tokens', () => {
  assert.deepEqual(rules('kiosk', '<StackPanel Spacing="24">'), ['axaml-spacing']);
  assert.deepEqual(rules('kiosk', '<Border Padding="40,28">'), ['axaml-spacing']);
  assert.deepEqual(rules('kiosk', '<TextBlock FontSize="72" />'), ['axaml-font-size']);
  assert.deepEqual(rules('kiosk', '<Border Background="#FFFFFF">'), ['axaml-colour']);
  assert.deepEqual(rules('kiosk', '<Border Background="White">'), ['axaml-named-colour']);

  assert.deepEqual(rules('kiosk', '<StackPanel Spacing="{DynamicResource Space6}">'), []);
  assert.deepEqual(rules('kiosk', '<Border Margin="0">'), []);
  assert.deepEqual(rules('kiosk', '<Border Background="Transparent">'), []);
  assert.deepEqual(rules('kiosk', '<Border Background="{DynamicResource QrBackground}">'), []);
});

test('a justified exception is marked on its line', () => {
  assert.deepEqual(rules('admin', 'padding: 3px; // design-lint: allow hairline focus ring'), []);
});
