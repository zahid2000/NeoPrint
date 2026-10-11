# NeoPrint --- Security Threat Model

## Trust boundaries

### User

Untrusted.

### Uploaded files

Untrusted.

### Kiosk

Untrusted edge device.

### Backend

Trusted application boundary.

### External providers

Partially trusted; responses must be validated.

### Admin

Privileged but auditable.

## Threats

### Malicious file

Risk: - converter exploit - resource exhaustion - malicious document

Controls: - isolated converter - no network access - timeout -
CPU/memory limits - restricted filesystem - temporary directory -
antivirus/malware scanning where planned

**Vəziyyət (Addım 7A, T-051):** istifadəçi faylını parse edən hər şey —
LibreOffice, Ghostscript ailəsi **və** PDFium, SkiaSharp, PDFsharp (səhifə
sayı, preview, şəkil → PDF, səhifə / şəkil redaktəsi, skan və tərtibatın PDF-ə
çəkilməsi) — yalnız şəbəkəsiz `converter` konteynerində, hər fayl üçün ayrıca
prosesdə işləyir. API prosesi bu kitabxanalara istinad etmir
(`ParserIsolationTests` bunu assembly metadata-sından yoxlayır). Təfərrüat:
`docs/adr/ADR-0005-isolated-converter.md` bənd 6.

**Converter-i yükləmək (T-222):** önizləmə və redaktə sorğuları converter
prosesi başladır, yəni onları çoxaltmaq xidməti yormağın yoludur. Üç sərhəd
var: bir çağıran dəqiqədə 120 sorğu göndərə bilər (`429`); eyni anda ən çox 3
alət prosesi işləyir, arxasında ən çox 48 sorğu ən çox 10 saniyə gözləyir,
qalanı dərhal `503` + `Retry-After` alır; rədd olunan sorğu proses
başlatmır və açılmış faylı spool-a yazmır. Çəkilmiş səhifə şəkilləri yalnız
API-nin yaddaşında keşlənir (64 MB, 10 dəqiqə) — Redis-də və diskdə fayl
məzmununun şifrəsiz nüsxəsi yaranmır; keş icazə yoxlamasından sonra oxunur.

### Stolen kiosk credentials

Controls: - device keypair - private key never leaves device - revoke
support - short-lived auth tokens - activation expiry - rate limiting

### Token leakage

Controls: - HttpOnly/Secure where applicable - secure storage - no token
logging - no secrets in telemetry

### Replay

Controls: - idempotency - unique provider event IDs - request
timestamps/nonces where applicable - device authentication

### Admin abuse

Controls: - RBAC - immutable audit - reason for sensitive actions -
least privilege

### File privacy

Controls: - application-level encryption - no presigned public URLs -
admin cannot casually inspect user files - retention/auto-delete -
account export/delete rules

### Secret leakage

Never commit: - OpenBao credentials - production API keys - payment
secrets - SMTP passwords - private device keys

## Logging

Never log: - access tokens - refresh tokens - passwords - encryption
keys - private keys - complete file content - payment secrets

PII should be minimized and masked where practical.

## Security changes

Security-sensitive changes require tests.

Do not weaken security controls just to make development easier.
