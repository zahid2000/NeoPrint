// OpenBao server configuration for the development environment.
//
// Production uses a separate configuration (Raft storage, mandatory TLS,
// auto-unseal). See docs/OPENBAO.md — this file must never be used as-is in
// production, because TLS is disabled on the listener.

ui = true

// The container has no swap and is not allowed to lock memory; mlock is
// therefore disabled here. In production mlock stays on.
disable_mlock = true

storage "file" {
  path = "/openbao/file"
}

listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = true // DEVELOPMENT ONLY
}

api_addr     = "http://openbao:8200"
cluster_addr = "http://openbao:8201"

// Audit device (plan section 2.13). From OpenBao 2.6 an audit device can no
// longer be enabled through the API, only declared here.
audit "file" {
  type = "file"
  path = "file/"

  options = {
    file_path = "/openbao/logs/audit.log"
  }
}

log_level  = "info"
log_format = "standard"
