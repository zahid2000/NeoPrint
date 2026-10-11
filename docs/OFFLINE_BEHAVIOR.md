# NeoPrint --- Offline Behavior

Kiosk online və offline vəziyyətlərini explicit şəkildə idarə etməlidir.

## Backend unavailable

### Yeni payment

Yeni online payment başlatmaq olmaz.

UI: `Online payment unavailable`

### Artıq ödənilmiş job

Backend reconciliation və local state təhlükəsizdirsə job davam etdirilə
bilər.

### Queue

Local queue yalnız backend source of truth ilə konflikt yaratmayacaq
şəkildə işləməlidir.

### Configuration

Kiosk son məlum configuration/pricing məlumatını cache edə bilər, lakin
həmin cache-in TTL və istifadə qaydası explicit olmalıdır.

## Uzaqdan əmrlər kiosk offline olanda (plan 7.8, T-208)

- Əmr `kiosk_commands`-da gözləyir; kiosk qoşulanda `GET kiosks/me/commands` ilə alır. 24 saatdan köhnə əmr
  `Expired` olur və icra edilmir.
- **`Pause` kioskun cavabını gözləmir:** server statusu dərhal `Maintenance` edir və yeni sifarişi rədd edir.
  Kiosk qayıdanda konfiqurasiyanı oxuyur və "Texniki xidmət" ekranını göstərir; heartbeat fasiləni götürmür.
- **`RestartApp` söndürülmüş kioska göndərilibsə** kiosk açılanda bir daha yenidən başlamır: açılışın özü əmrin
  icrasıdır (əmr prosesdən köhnədir) və `Succeeded` yazılır. `Reboot` üçün eyni qayda kompüterin uptime-ı ilə.
- Əmr kioska çatıb, amma cavab itibsə: kiosk əmri bir dəfə icra edir (eyni `id` ikinci dəfə işlənmir), cavabı
  növbəti qoşulmada yenidən göndərir; server təkrar cavabı və təkrar təsdiqi dəyişiklik saymır.

## Printer offline

UI-də: - printer status - səbəb - gözlənilən action

göstərilməlidir.

Print başlanmazdan əvvəl printer capability/status mümkün qədər
yoxlanmalıdır.

## Redis offline

Redis source of truth olmadığı üçün əsas biznes state itirilməməlidir.

Queue/projection recovery mexanizmi MongoDB-dən rebuild etməlidir.

## RabbitMQ offline

Synchronous business transaction DB-də təhlükəsiz tamamlanmalıdır.

Async event delivery sonradan retry edilə bilər.

## Payment network offline

Kiosk "ödəniş qəbul edildi" göstərməməlidir, əgər backend/provider bunu
təsdiqləməyibsə.

## UX state-ləri

Minimum: - Online - Degraded - Offline - Payment unavailable - Printer
unavailable - Backend unavailable - Recovering

## Important

Offline davranışı "hər şey işləsin" prinsipi deyil.

Əsas məqsəd: - duplicate payment yaratmamaq; - duplicate print
yaratmamaq; - state itirməmək; - user-a yanlış uğur göstərməmək.
