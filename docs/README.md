# NeoPrint — sənədlər və prioritet

## Mənbələrin prioriteti (ziddiyyət olarsa yuxarıdakı üstündür)

1. `docs/NeoPrint-PLAN.md` — layihənin əsas spesifikasiyası (nə qurulur, hansı ardıcıllıqla).
2. `docs/ARCHITECTURE_RULES.md` — arxitektura qaydaları.
3. `docs/STATE_MACHINES.md` — biznes state-ləri və keçidləri.
4. `docs/IDEMPOTENCY.md`, `docs/PAYMENT_FLOWS.md`, `docs/FAILURE_RECOVERY.md`, `docs/OFFLINE_BEHAVIOR.md`, `docs/SECURITY_THREAT_MODEL.md`, `docs/DOMAIN_RULES.md`, `docs/DATA_RETENTION.md`, `docs/OBSERVABILITY.md` — mövzu üzrə qaydalar.
5. `docs/CLAUDE_CODE_EXECUTION_RULES.md` — icra qaydası.
6. Mövcud repo kodu.

Ziddiyyət görünürsə: dayan, uyğunsuzluğu göstər, təhlükəsiz variantı seç və `docs/DECISIONS.md`-ə yaz. Qərar arxitektura səviyyəsindədirsə `docs/adr/TEMPLATE.md` formatında `docs/adr/ADR-XXXX-*.md` yarat.

## Fayllar

| Fayl | Nə üçün |
|---|---|
| `NeoPrint-PLAN.md` | Tam spesifikasiya, 10 addımlıq icra planı |
| `ARCHITECTURE_RULES.md` | Layer-lər, source of truth, modul sərhədləri |
| `STATE_MACHINES.md` | PrintJob, Payment, Refund, Device, Update, Schedule state-ləri |
| `IDEMPOTENCY.md` | Idempotency açarları, provider event-ləri, consumer qaydaları |
| `PAYMENT_FLOWS.md` | Ödəniş, snapshot, timeout, webhook, refund, wallet ledger |
| `FAILURE_RECOVERY.md` | Failure ssenariləri, power loss, print attempt evidence, recovery testləri |
| `OFFLINE_BEHAVIOR.md` | Kioskun offline/degraded davranışı |
| `SECURITY_THREAT_MODEL.md` | Trust boundary-lər, təhdidlər, nəzarətlər |
| `DOMAIN_RULES.md` | Money, pricing snapshot, wallet ledger, audit, kiosk/device ayrımı |
| `DATA_RETENTION.md` | Fayl, hesab, razılıq və maliyyə məlumatlarının saxlanması |
| `OBSERVABILITY.md` | CorrelationId, structured log, metrik və alert-lər |
| `KIOSK_SETUP.md` | Kioskun sıfırdan quraşdırılması (texnik üçün): paket, provisioning, aktivləşdirmə, texniki giriş, golden image |
| `KIOSK_PROVISIONING.md` | Provisioning skriptlərinin OS-də etdiyi hər dəyişiklik və səbəbi |
| `MANUAL_TESTS.md` | Real printer / cihaz tələb edən əl ilə yoxlamalar (məs. çapın ortasında elektrik kəsilməsi): hazırlıq, addımlar, gözlənilən nəticə |
| `UI_BUGS.md` | Layihə sahibinin bir sətirlə yazdığı xırda UI problemləri; hər M-dən sonra oxunur və bağlanır |
| `PILOT.md` | Pilot: pilot-bloklayıcılar siyahısı (yoxlama siyahıları və uğur meyarları Addım 7B-də) |
| `CLAUDE_CODE_EXECUTION_RULES.md` | Task icrası, scope, definition of done |
| `adr/TEMPLATE.md` | Arxitektura qərar şablonu |
