// Policy for the NeoPrint API (AppRole "neoprint-api").
//
// Read-only by design (plan section 2.13): the application never writes a
// secret back. Storage provider credentials entered in the admin panel are
// encrypted and kept in MongoDB, not here.

// KV v2 keeps the values under data/ and the versions under metadata/.
path "neoprint/data/backend/core" {
  capabilities = ["read"]
}

path "neoprint/data/backend/crypto" {
  capabilities = ["read"]
}

path "neoprint/data/backend/providers" {
  capabilities = ["read"]
}

// Seed values (first SuperAdmin, first storage provider) are only read by the
// API at startup.
path "neoprint/data/backend/seed" {
  capabilities = ["read"]
}

path "neoprint/metadata/backend/*" {
  capabilities = ["read", "list"]
}

// The configuration provider renews its own token instead of logging in again
// on every refresh.
path "auth/token/renew-self" {
  capabilities = ["update"]
}

path "auth/token/lookup-self" {
  capabilities = ["read"]
}
