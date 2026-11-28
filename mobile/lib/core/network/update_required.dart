import 'package:flutter_riverpod/flutter_riverpod.dart';

/// The backend no longer serves this version of the app (`426`,
/// `app.updateRequired` - docs/adr/ADR-0009, T-492): every screen gives way
/// to "Update the app" until the user checks again.
final updateRequiredProvider = StateProvider<bool>((ref) => false);
