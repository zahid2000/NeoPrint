// Policy for the NeoPrint background workers (AppRole "neoprint-worker").
//
// Same read-only shape as neoprint-api, minus the seed path: seeding is the
// API's job at startup, a worker has no reason to read the initial SuperAdmin
// credentials. The backup path is added in phase 2 (plan section 2.13).

path "neoprint/data/backend/core" {
  capabilities = ["read"]
}

path "neoprint/data/backend/crypto" {
  capabilities = ["read"]
}

path "neoprint/data/backend/providers" {
  capabilities = ["read"]
}

path "neoprint/metadata/backend/*" {
  capabilities = ["read", "list"]
}

path "auth/token/renew-self" {
  capabilities = ["update"]
}

path "auth/token/lookup-self" {
  capabilities = ["read"]
}
