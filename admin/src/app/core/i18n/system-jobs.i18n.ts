import type { PageTranslations } from './page-translations';

/** "Arxa plan işləri" (step 7A, M11; plan section 6). */
export const systemJobsTranslations: PageTranslations = {
  az: {
    'nav.systemJobs': 'Arxa plan işləri',

    'bgjobs.title': 'Arxa plan işləri',
    'bgjobs.description':
      'Sistemin özü gördüyü işlər və tapdığı uyğunsuzluqlar. Pul heç vaxt avtomatik düzəldilmir — tapılan hər şey incident olur.',
    'bgjobs.hangfire': 'Hangfire paneli',
    'bgjobs.hangfireFailed': 'Hangfire paneli açılmadı. Yenidən cəhd edin; təkrarlansa, hesabınızın SuperAdmin olduğunu yoxlayın.',
    'bgjobs.loadFailedEffect': 'İşlərin vəziyyəti göstərilə bilmir. İşlərin özünə və incident-lərə təsir etmir.',
    'bgjobs.lastRun': 'Son yoxlama: {{time}}',
    'bgjobs.neverRan': 'Hələ işləməyib',
    'bgjobs.seeIncidents': 'Incident-lərə bax',

    'bgjobs.name.dead-letters': 'Uğursuz mesajlar (DLQ)',
    'bgjobs.name.file-cleanup': 'Fayl təmizləmə',
    'bgjobs.name.wallet-ledger': 'Balans uzlaşdırması',
    'bgjobs.name.payment-consistency': 'Ödəniş uzlaşdırması',

    'bgjobs.state.never': 'Hələ işləməyib',
    'bgjobs.state.failed': 'Yoxlama alınmadı',
    'bgjobs.state.messages': '{{count}} mesaj',
    'bgjobs.state.mismatches': '{{count}} uyğunsuzluq',
    'bgjobs.state.dryRun': 'Sınaq rejimi',
    'bgjobs.state.ok': 'Qaydasındadır',

    'bgjobs.age.minutes': '{{count}} dəqiqə əvvəl',
    'bgjobs.age.hours': '{{count}} saat əvvəl',
    'bgjobs.age.days': '{{count}} gün əvvəl',

    'bgjobs.deadLetters.unreachable':
      'Növbələrə baxmaq alınmadı: mesaj brokeri cavab vermir. Uğursuz mesajların sayı bilinmir. Brokerin işlədiyini yoxlayın; növbəti yoxlama 5 dəqiqədən sonradır.',
    'bgjobs.deadLetters.allEmpty': 'Bütün növbələr boşdur',
    'bgjobs.deadLetters.queue': 'Növbə',
    'bgjobs.deadLetters.messages': 'Mesaj',
    'bgjobs.deadLetters.oldest': 'Ən köhnəsi',
    'bgjobs.deadLetters.ageUnknown': 'Vaxt bilinmir',

    'bgjobs.files.dryRunNotice':
      'Sınaq rejimi: heç nə silinmir; aşağıdakılar silinərdi. Rejim "Tənzimləmələr" → Files.CleanupDryRun-dadır.',
    'bgjobs.files.examined': 'Baxılan obyekt',
    'bgjobs.files.wouldDelete': 'Silinəcək',
    'bgjobs.files.deleted': 'Silinən',
    'bgjobs.files.freed': 'Boşalan yer',
    'bgjobs.files.protected': 'Qorunan (açıq sifariş, refund, planlı çap, hazırlanmış sifariş)',
    'bgjobs.files.recent': 'Təzə olduğu üçün saxlanan',
    'bgjobs.files.wouldDeleteList': 'Silinəcək obyektlər',

    'bgjobs.wallet.checked': 'Yoxlanan hesab',
    'bgjobs.wallet.mismatches': 'Uyğunsuzluq',
    'bgjobs.wallet.account': 'Hesab',
    'bgjobs.wallet.balance': 'Balans',
    'bgjobs.wallet.ledger': 'Ledger cəmi',
    'bgjobs.wallet.difference': 'Fərq',

    'bgjobs.money.paidNotPrinted': 'Ödənilib, çap və refund yoxdur',
    'bgjobs.money.refundWithoutLedger': 'Refund var, ledger-də yoxdur',
    'bgjobs.money.walletPaymentWithoutLedger': 'Balansdan ödəniş ledger-də yoxdur',
    'bgjobs.money.paidWithoutFiscal': 'Fiskal çeki yoxdur',
    'bgjobs.money.latest': 'Son tapıntılar',

    'bgjobs.analytics.title': 'Kiosk analitikası',
    'bgjobs.analytics.notice':
      'Kiosklardan tanınmayan analitika hadisələri gəlir — pilot hesabatı natamam ola bilər. Kiosk versiyasını yoxlayın.',
    'bgjobs.analytics.today': 'Bu gün atılan hadisə',
    'bgjobs.analytics.week': 'Son 7 gündə atılan hadisə',
    'bgjobs.analytics.todayShort': 'Bu gün',
    'bgjobs.analytics.weekShort': '7 gün',
    'bgjobs.analytics.lastUnknown': 'Son tanınmayan ad',
  },
  en: {
    'nav.systemJobs': 'Background jobs',

    'bgjobs.title': 'Background jobs',
    'bgjobs.description':
      'The work the system does on its own and the mismatches it finds. Money is never corrected automatically — whatever is found becomes an incident.',
    'bgjobs.hangfire': 'Hangfire panel',
    'bgjobs.hangfireFailed': 'The Hangfire panel did not open. Try again; if it happens again, check that your account is a SuperAdmin.',
    'bgjobs.loadFailedEffect': 'The jobs cannot be shown. The jobs themselves and the incidents are not affected.',
    'bgjobs.lastRun': 'Last check: {{time}}',
    'bgjobs.neverRan': 'Has not run yet',
    'bgjobs.seeIncidents': 'See the incidents',

    'bgjobs.name.dead-letters': 'Failed messages (DLQ)',
    'bgjobs.name.file-cleanup': 'File cleanup',
    'bgjobs.name.wallet-ledger': 'Balance check',
    'bgjobs.name.payment-consistency': 'Payment check',

    'bgjobs.state.never': 'Not run yet',
    'bgjobs.state.failed': 'Check failed',
    'bgjobs.state.messages': '{{count}} messages',
    'bgjobs.state.mismatches': '{{count}} mismatches',
    'bgjobs.state.dryRun': 'Trial mode',
    'bgjobs.state.ok': 'All in order',

    'bgjobs.age.minutes': '{{count}} minutes ago',
    'bgjobs.age.hours': '{{count}} hours ago',
    'bgjobs.age.days': '{{count}} days ago',

    'bgjobs.deadLetters.unreachable':
      'The queues could not be read: the message broker does not answer. The number of failed messages is unknown. Check that the broker runs; the next check is in 5 minutes.',
    'bgjobs.deadLetters.allEmpty': 'Every queue is empty',
    'bgjobs.deadLetters.queue': 'Queue',
    'bgjobs.deadLetters.messages': 'Messages',
    'bgjobs.deadLetters.oldest': 'Oldest',
    'bgjobs.deadLetters.ageUnknown': 'Time unknown',

    'bgjobs.files.dryRunNotice':
      'Trial mode: nothing is deleted; the objects below would be. The mode is in Settings → Files.CleanupDryRun.',
    'bgjobs.files.examined': 'Objects examined',
    'bgjobs.files.wouldDelete': 'Would be deleted',
    'bgjobs.files.deleted': 'Deleted',
    'bgjobs.files.freed': 'Space freed',
    'bgjobs.files.protected': 'Protected (open order, refund, planned print, prepared order)',
    'bgjobs.files.recent': 'Kept as too recent',
    'bgjobs.files.wouldDeleteList': 'Objects that would be deleted',

    'bgjobs.wallet.checked': 'Accounts checked',
    'bgjobs.wallet.mismatches': 'Mismatches',
    'bgjobs.wallet.account': 'Account',
    'bgjobs.wallet.balance': 'Balance',
    'bgjobs.wallet.ledger': 'Ledger sum',
    'bgjobs.wallet.difference': 'Difference',

    'bgjobs.money.paidNotPrinted': 'Paid, neither printed nor refunded',
    'bgjobs.money.refundWithoutLedger': 'Refund missing from the ledger',
    'bgjobs.money.walletPaymentWithoutLedger': 'Balance payment missing from the ledger',
    'bgjobs.money.paidWithoutFiscal': 'No fiscal receipt',
    'bgjobs.money.latest': 'Latest findings',

    'bgjobs.analytics.title': 'Kiosk analytics',
    'bgjobs.analytics.notice':
      'Kiosks are sending analytics events that are not recognised — the pilot report may be incomplete. Check the kiosk version.',
    'bgjobs.analytics.today': 'Events dropped today',
    'bgjobs.analytics.week': 'Events dropped in 7 days',
    'bgjobs.analytics.todayShort': 'Today',
    'bgjobs.analytics.weekShort': '7 days',
    'bgjobs.analytics.lastUnknown': 'Last unknown name',
  },
  ru: {
    'nav.systemJobs': 'Фоновые задачи',

    'bgjobs.title': 'Фоновые задачи',
    'bgjobs.description':
      'Работа, которую система делает сама, и найденные расхождения. Деньги никогда не исправляются автоматически — всё найденное становится инцидентом.',
    'bgjobs.hangfire': 'Панель Hangfire',
    'bgjobs.hangfireFailed': 'Панель Hangfire не открылась. Повторите; если снова — проверьте, что ваша учётная запись SuperAdmin.',
    'bgjobs.loadFailedEffect': 'Состояние задач не удаётся показать. На сами задачи и инциденты это не влияет.',
    'bgjobs.lastRun': 'Последняя проверка: {{time}}',
    'bgjobs.neverRan': 'Ещё не запускалась',
    'bgjobs.seeIncidents': 'Смотреть инциденты',

    'bgjobs.name.dead-letters': 'Неудачные сообщения (DLQ)',
    'bgjobs.name.file-cleanup': 'Очистка файлов',
    'bgjobs.name.wallet-ledger': 'Сверка балансов',
    'bgjobs.name.payment-consistency': 'Сверка платежей',

    'bgjobs.state.never': 'Не запускалась',
    'bgjobs.state.failed': 'Проверка не удалась',
    'bgjobs.state.messages': 'Сообщений: {{count}}',
    'bgjobs.state.mismatches': 'Расхождений: {{count}}',
    'bgjobs.state.dryRun': 'Пробный режим',
    'bgjobs.state.ok': 'Всё в порядке',

    'bgjobs.age.minutes': '{{count}} мин назад',
    'bgjobs.age.hours': '{{count}} ч назад',
    'bgjobs.age.days': '{{count}} дн. назад',

    'bgjobs.deadLetters.unreachable':
      'Не удалось прочитать очереди: брокер сообщений не отвечает. Число неудачных сообщений неизвестно. Проверьте, что брокер работает; следующая проверка через 5 минут.',
    'bgjobs.deadLetters.allEmpty': 'Все очереди пусты',
    'bgjobs.deadLetters.queue': 'Очередь',
    'bgjobs.deadLetters.messages': 'Сообщения',
    'bgjobs.deadLetters.oldest': 'Самое старое',
    'bgjobs.deadLetters.ageUnknown': 'Время неизвестно',

    'bgjobs.files.dryRunNotice':
      'Пробный режим: ничего не удаляется; удалились бы объекты ниже. Режим — в «Настройки» → Files.CleanupDryRun.',
    'bgjobs.files.examined': 'Проверено объектов',
    'bgjobs.files.wouldDelete': 'Будет удалено',
    'bgjobs.files.deleted': 'Удалено',
    'bgjobs.files.freed': 'Освобождено',
    'bgjobs.files.protected': 'Защищено (открытый заказ, возврат, плановая печать, подготовленный заказ)',
    'bgjobs.files.recent': 'Оставлено как слишком новые',
    'bgjobs.files.wouldDeleteList': 'Объекты к удалению',

    'bgjobs.wallet.checked': 'Проверено счетов',
    'bgjobs.wallet.mismatches': 'Расхождения',
    'bgjobs.wallet.account': 'Счёт',
    'bgjobs.wallet.balance': 'Баланс',
    'bgjobs.wallet.ledger': 'Сумма журнала',
    'bgjobs.wallet.difference': 'Разница',

    'bgjobs.money.paidNotPrinted': 'Оплачено, но нет ни печати, ни возврата',
    'bgjobs.money.refundWithoutLedger': 'Возврат не отражён в журнале',
    'bgjobs.money.walletPaymentWithoutLedger': 'Оплата с баланса не отражена в журнале',
    'bgjobs.money.paidWithoutFiscal': 'Нет фискального чека',
    'bgjobs.money.latest': 'Последние находки',

    'bgjobs.analytics.title': 'Аналитика киосков',
    'bgjobs.analytics.notice':
      'Киоски присылают нераспознанные события аналитики — отчёт пилота может быть неполным. Проверьте версию киоска.',
    'bgjobs.analytics.today': 'Отброшено событий сегодня',
    'bgjobs.analytics.week': 'Отброшено за 7 дней',
    'bgjobs.analytics.todayShort': 'Сегодня',
    'bgjobs.analytics.weekShort': '7 дней',
    'bgjobs.analytics.lastUnknown': 'Последнее неизвестное имя',
  },
};
