import 'dart:math';

/// The header that makes a retried request the same request
/// (docs/IDEMPOTENCY.md): payments, top-ups, vouchers, claims, emails.
const idempotencyKeyHeader = 'Idempotency-Key';

final Random _random = Random.secure();

/// A fresh key: 32 hexadecimal characters.
///
/// One per user action, kept for that action's retries - a new key for a
/// retry would be a second payment.
String newIdempotencyKey() =>
    List.generate(32, (_) => _random.nextInt(16).toRadixString(16)).join();
