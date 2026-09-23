# NeoPrint --- Claude Code Execution Rules

Bu fayl Claude Code üçün icra qaydasıdır.

## 1. Əsas mənbələr

İcra zamanı prioritet:

1.  `NeoPrint-PLAN.md`
2.  `docs/ARCHITECTURE_RULES.md`
3.  `docs/STATE_MACHINES.md`
4.  digər uyğun `docs/*.md`
5.  mövcud repository kodu

Planla ziddiyyət yaranarsa qərarı özbaşına dəyişmə. Uyğunsuzluğu göstər
və təhlükəsiz variantı seç.

## 2. Fazalar

NeoPrint-PLAN.md-dəki Addım 1--10 strategiyası olduğu kimi saxlanılır.

MVP scope-u daraltma.

Gələcək addımları cari addıma səbəbsiz gətirmə.

## 3. Task icrası

Hər task üçün:

1.  Repository-ni analiz et.
2.  Mövcud implementasiyanı yoxla.
3.  Dəyişiklik planını qısa yaz.
4.  Kodla.
5.  Test yaz/yenilə.
6.  Build et.
7.  Testləri işə sal.
8.  Git diff yoxla.
9.  Secret/package/license problemlərini yoxla.
10. Nəticəni qısa report et.

## 4. Test failure

Test/build failure varsa növbəti task-a keçmə.

Root cause tapılmadan failure-i suppress etmə.

## 5. Scope

Claude Code: - task üçün lazım olmayan faylları dəyişməməlidir; - böyük
refactor etməməlidir; - package əlavə etməməlidir, əgər mövcud package
ilə həll mümkündürsə; - planın gələcək hissələrini "hazır olsun" deyə
əvvəlcədən implement etməməlidir.

## 6. State

Status dəyişikliklərini explicit domain/application transition ilə et.

Raw property mutation istifadə etmə, əgər həmin field business
state-dirsə.

## 7. Idempotency

Aşağıdakılarda idempotency yoxlanmalıdır:

-   payment
-   refund
-   wallet
-   print job
-   webhook
-   RabbitMQ consumer
-   fiscalization
-   activation/provisioning
-   update/recovery

## 8. Database

MongoDB source of truth-dur.

Redis və RabbitMQ-dan business truth oxuma.

Cross-module collection mutation etmə.

Index-ləri business uniqueness qaydalarına uyğun yarat.

## 9. Security

Secretləri source control-a yazma.

Log-lara token/secret/file content yazma.

User upload-ları untrusted input qəbul et.

Kiosk private key-ni serverə göndərmə.

## 10. Definition of done

Task "done" yalnız: - kod compile olduqda; - uyğun testlər keçdikdə; -
error handling mövcud olduqda; - idempotency/retry qaydası nəzərə
alındıqda; - security qaydaları pozulmadıqda; - git diff təmiz və scope
daxilində olduqda hesab edilir.
