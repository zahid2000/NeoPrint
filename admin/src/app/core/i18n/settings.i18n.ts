import type { PageTranslations } from './page-translations';

/**
 * What each setting on the "Tənzimləmələr" page does (plan section 2.4), in
 * the panel's language. The backend keeps an Azerbaijani description with the
 * seed; the page shows it only for a setting this build has no text for.
 */
export const settingsTranslations: PageTranslations = {
  az: {
    'settings.description.Queue.DisplayLimit':
      'Kiosk ekranında növbədə göstərilən sifarişlərin sayı (3 və ya 5).',
    'settings.description.Payment.TimeoutMinutes':
      'Ödənilməmiş ödəniş sorğusu bu müddətdən sonra ləğv olunur.',
    'settings.description.KioskSession.TtlMinutes':
      'Telefonla kiosk arasındakı sessiya bu qədər fəaliyyətsizlikdən sonra bağlanır.',
    'settings.description.Kiosk.IdleResetSeconds':
      'Ekrana toxunulmasa kiosk bu müddətdən sonra ana ekrana qayıdır.',
    'settings.description.UserQr.RotationSeconds': 'Şəxsi QR kodu bu intervalla yenilənir.',
    'settings.description.PreparedPrint.AutoCancelDays':
      'Tətbiqdə ödənilib kioskda götürülməyən sifariş bu qədər gündən sonra ləğv olunur (pul balansa qayıdır).',
    'settings.description.Schedule.MaxDaysAhead': 'Çapı ən çox neçə gün sonraya planlamaq olar.',
    'settings.description.Schedule.ReminderLeadMinutes':
      'Planlanmış çapdan neçə dəqiqə əvvəl xatırlatma gəlir (0 — vaxtında).',
    'settings.description.Schedule.AutoCancelHours':
      'Vaxtı çatan planlanmış çapa neçə saat ərzində cavab verilməsə, avtomatik ləğv olunur (pul balansa qayıdır).',
    'settings.description.Referral.BonusPages':
      'Dəvət edən istifadəçiyə verilən bonus səhifə sayı.',
    'settings.description.Referral.InviteeBonusPages':
      'Dəvət olunan istifadəçiyə verilən bonus səhifə sayı.',
    'settings.description.Referral.MaxBonusPagesPerJob':
      'Bir çap sifarişində istifadə oluna bilən ən çox bonus səhifə.',
    'settings.description.Referral.MaxRewardsPerMonth':
      'Bir dəvət edənin bir təqvim ayında ala biləcəyi referral mükafatlarının sayı; 0 — limitsiz.',
    'settings.description.Refund.VoucherValidDays':
      'Anonim geri qaytarma vauçerinin etibarlılıq müddəti.',
    'settings.description.Refund.ProviderMaxAttempts':
      'Karta geri qaytarma uğursuz olanda neçə dəfə təkrarlanır.',
    'settings.description.Job.SuspectedAfterMinutes':
      'Kiosk bu qədər susanda çapdakı sifariş "Şübhəli" olur; avtomatik qaytarma yoxdur, admin qərar verir.',
    'settings.description.Job.SuspectedAutoResolveHours':
      'Bu qədər saat ərzində qərar verilməyən şübhəli sifariş üçün adminə təkrar xatırlatma gəlir.',
    'settings.description.Public.SiteUrl':
      'Kiosk ekranındakı refund status QR-ının apardığı ünvan (məs. https://neoprint.az).',
    'settings.description.Kiosk.HeartbeatSeconds':
      'Kiosk serverə bu intervalla "sağam" siqnalı göndərir.',
    'settings.description.Kiosk.OfflineAfterSeconds':
      'Bu müddət ərzində siqnal gəlməsə kiosk əlaqəsiz sayılır.',
    'settings.description.Kiosk.TelemetrySeconds':
      'Kiosk vəziyyət məlumatlarını bu intervalla göndərir.',
    'settings.description.Kiosk.PrinterFaultAfterSeconds':
      'Printer bu qədər saniyə hazır olmasa kiosk dayandırılır: yeni sifariş almır, qonaqların gözləyən ödənişləri qaytarılır.',
    'settings.description.PrintJob.MaxAutoRetries':
      'Printerin dayandırdığı sifariş cəmi neçə dəfə çapa göndərilir (1–10). Bu qədər cəhddən sonra sifariş özü davam etmir: incident açılır, sahibi isə onu ləğv edib məbləği geri ala bilər.',
    'settings.description.Notification.MinSeverity':
      'Incident bu səviyyədən başlayaraq kioska baxan hesablara email ilə bildirilir: Critical, High və ya Medium. Aşağı səviyyəlilər yalnız paneldə görünür.',
    'settings.description.Notification.TelegramEnabled':
      'Incident bildirişlərinin Telegram kanalı. Söndürülüdür: kanal pilotdan sonra açılacaq, bunun üçün botun açarı da lazımdır.',
    'settings.description.Kiosk.TelemetryRetentionDays':
      'Kiosk vəziyyət tarixçəsi bu qədər gün saxlanılır.',
    'settings.description.Kiosk.PrinterEventRetentionDays':
      'Printer hadisələri (toner dəyişdi, kağız dolduruldu, sıxılma) neçə gün saxlanılır.',
    'settings.description.Kiosk.ConfigCacheTtlHours':
      'Kioskun yadda saxladığı konfiqurasiya bu qədər etibarlıdır; köhnələndə kiosk məhdud rejimə keçir.',
    'settings.description.Payment.ReconcileAfterMinutes':
      'Statusu bilinməyən ödənişlər bu müddətdən sonra bankla yoxlanılır.',
    'settings.description.Retention.StateTransitionDays':
      'Vəziyyət dəyişikliyi tarixçəsi bu qədər gün saxlanılır.',
    'settings.description.Retention.ProviderEventDays':
      'Ödəniş provayderindən gələn bildirişlərin qeydləri bu qədər gün saxlanılır.',
    'settings.description.Files.DefaultMaxSizeMb':
      'Öz limiti olmayan kiosk və mobil yükləmə üçün ən böyük fayl ölçüsü (MB).',
    'settings.description.Files.DefaultMaxPages':
      'Öz limiti olmayan kiosk və mobil yükləmə üçün ən çox səhifə sayı.',
    'settings.description.Files.UserQuotaGb':
      'Hər istifadəçinin saxlaya biləcəyi faylların ümumi həcmi (GB).',
    'settings.description.Files.AllowedFormats': 'Qəbul olunan fayl formatları.',
    'settings.description.Files.VersionsToKeep':
      'Hər faylın saxlanılan redaktə versiyalarının sayı.',
    'settings.description.Storage.SyncParallelism':
      'Saxlama sinxronizasiyasında eyni anda köçürülən obyekt sayı.',
    'settings.description.Storage.SourceRetentionDays':
      'Migrasiyadan sonra köhnə provayderdəki fayllar bu qədər gün saxlanılır.',
    'settings.description.Storage.ReconciliationCron':
      'Güzgü rejimində iki provayderin gecəlik tutuşdurulma vaxtı (cron).',
    'settings.description.Layout.DefaultMarginMm':
      'Səhifə tərtibatında standart kənar boşluq (mm).',
    'settings.description.Privacy.SensitiveAutoDeleteDefault':
      'Həssas skanlar üçün standart avtomatik silinmə qaydası.',
    'settings.description.Privacy.AccountDeletionGraceHours':
      'Hesab silindikdən sonra fayllar bu müddətdən sonra birdəfəlik silinir.',
    'settings.description.Privacy.ExportMaxMegabytes':
      '"Məlumatlarımı yüklə" arxivinə faylların məzmunu bu ölçüyə qədər daxil edilir (MB); çox olanda yalnız siyahısı yazılır.',
    'settings.description.Fiscal.Enabled': 'Satışlar üçün fiskal çek vurulur.',
    'settings.description.Fiscal.TopUpReceiptMode':
      'Balans artırılanda (OnTopUp) və ya xidmət göstəriləndə (OnService) fiskal çek.',
    'settings.description.Fiscal.MaxPendingMinutes':
      'Fiskal çeki bu müddətdə vurulmayan satış üçün insident açılır.',
    'settings.description.Paper.BlockWhenInsufficient':
      'Kağız çatmayanda kiosk sifarişi qəbul etmir.',
    'settings.description.Service.ForecastLeadHours':
      'Kağızın bitməsinə bu qədər saat qalanda servis tapşırığı avtomatik yaranır.',
    'settings.description.Service.TaskOverdueEscalationMinutes':
      'Kritik tapşırıq bu müddətdə görülməsə yuxarı ötürülür.',
    'settings.description.Analytics.RetentionDays': 'Analitika hadisələri bu qədər gün saxlanılır.',
    'settings.description.Fiscal.OnlineTerminalId':
      'Mobil (onlayn) satışlar üçün fiskal terminalın identifikatoru.',
    'settings.description.Kiosk.UpdateWindow':
      'Kioskların avtomatik yenilənməsinə icazə verilən saat aralığı.',
    'settings.description.Update.CheckIntervalMinutes':
      'Kiosk yeni versiyanı bu intervalla yoxlayır.',
    'settings.description.Update.HealthTimeoutMinutes':
      'Yeniləmədən sonra kioskun sağlam işləməsi bu qədər gözlənilir; olmasa geri qaytarılır.',
    'settings.description.Update.KeepVersions': 'Kioskda saxlanılan əvvəlki versiyaların sayı.',
    'settings.description.Enrollment.TokenTtlHours':
      'Yeni kioskun qoşulma tokeni bu qədər saat etibarlıdır.',
    'settings.description.Kiosk.CrashLoopThreshold':
      'Kiosk tətbiqi 10 dəqiqədə bu qədər dəfə çökərsə təhlükəsiz rejimə keçir.',
  },
  en: {
    'settings.description.Queue.DisplayLimit':
      'How many orders the kiosk screen shows in its queue (3 or 5).',
    'settings.description.Payment.TimeoutMinutes':
      'An unpaid payment request is cancelled after this long.',
    'settings.description.KioskSession.TtlMinutes':
      'A phone-to-kiosk session closes after this much inactivity.',
    'settings.description.Kiosk.IdleResetSeconds':
      'Left untouched, the kiosk returns to its home screen after this long.',
    'settings.description.UserQr.RotationSeconds': 'How often the personal QR code is renewed.',
    'settings.description.PreparedPrint.AutoCancelDays':
      'An order paid in the app and not collected at the kiosk is cancelled after this many days (the money goes back to the balance).',
    'settings.description.Schedule.MaxDaysAhead':
      'How many days ahead a print can be scheduled at most.',
    'settings.description.Schedule.ReminderLeadMinutes':
      'How many minutes before a scheduled print the reminder comes (0 means on time).',
    'settings.description.Schedule.AutoCancelHours':
      'How many hours a due scheduled print waits for an answer before it is cancelled (the money goes back to the balance).',
    'settings.description.Referral.BonusPages': 'Bonus pages given to the user who invites.',
    'settings.description.Referral.InviteeBonusPages': 'Bonus pages given to the invited user.',
    'settings.description.Referral.MaxBonusPagesPerJob':
      'The most bonus pages one print order can use.',
    'settings.description.Referral.MaxRewardsPerMonth':
      'How many referral rewards one inviter can get in a calendar month; 0 means no limit.',
    'settings.description.Refund.VoucherValidDays':
      'How long an anonymous refund voucher stays valid.',
    'settings.description.Refund.ProviderMaxAttempts':
      'How many times a failed refund to a card is retried.',
    'settings.description.Job.SuspectedAfterMinutes':
      'When the kiosk is silent this long, its printing order becomes "Suspected"; no automatic refund, an admin decides.',
    'settings.description.Job.SuspectedAutoResolveHours':
      'A suspected order with no decision after this many hours reminds the admins again.',
    'settings.description.Public.SiteUrl':
      'The address the refund status QR on the kiosk screen opens (e.g. https://neoprint.az).',
    'settings.description.Kiosk.HeartbeatSeconds':
      'How often the kiosk tells the server it is alive.',
    'settings.description.Kiosk.OfflineAfterSeconds':
      'A kiosk silent for this long counts as offline.',
    'settings.description.Kiosk.TelemetrySeconds': 'How often the kiosk sends its status readings.',
    'settings.description.Kiosk.PrinterFaultAfterSeconds':
      "A printer not ready for this many seconds pauses the kiosk: no new orders, and guests' waiting payments are refunded.",
    'settings.description.PrintJob.MaxAutoRetries':
      'How many times in all an order the printer stopped is sent to the printer (1–10). After that it no longer resumes by itself: an incident is opened, and its owner can cancel it and take the money back.',
    'settings.description.Notification.MinSeverity':
      'From this severity up an incident is e-mailed to the accounts that look after the kiosk: Critical, High or Medium. Anything below is shown in the panel only.',
    'settings.description.Notification.TelegramEnabled':
      'The Telegram channel for incident notifications. Off: the channel opens after the pilot and also needs a bot token.',
    'settings.description.Kiosk.TelemetryRetentionDays':
      'How many days of kiosk status history are kept.',
    'settings.description.Kiosk.PrinterEventRetentionDays':
      'How many days printer events (supply replaced, paper refilled, a jam) are kept.',
    'settings.description.Kiosk.ConfigCacheTtlHours':
      'How long the configuration the kiosk remembers stays valid; once stale, the kiosk runs in a limited mode.',
    'settings.description.Payment.ReconcileAfterMinutes':
      'Payments with an unknown status are checked with the bank after this long.',
    'settings.description.Retention.StateTransitionDays':
      'How many days the history of status changes is kept.',
    'settings.description.Retention.ProviderEventDays':
      'How many days the records of payment provider notifications are kept.',
    'settings.description.Files.DefaultMaxSizeMb':
      'The largest file (MB) for mobile uploads and kiosks without a limit of their own.',
    'settings.description.Files.DefaultMaxPages':
      'The most pages for mobile uploads and kiosks without a limit of their own.',
    'settings.description.Files.UserQuotaGb': 'The total size of files each user can keep (GB).',
    'settings.description.Files.AllowedFormats': 'The file formats that are accepted.',
    'settings.description.Files.VersionsToKeep': 'How many edited versions of each file are kept.',
    'settings.description.Storage.SyncParallelism':
      'How many objects a storage synchronisation copies at once.',
    'settings.description.Storage.SourceRetentionDays':
      'How many days files stay on the old provider after a migration.',
    'settings.description.Storage.ReconciliationCron':
      'When the two providers are compared each night in mirror mode (cron).',
    'settings.description.Layout.DefaultMarginMm': 'The default margin in the page layout (mm).',
    'settings.description.Privacy.SensitiveAutoDeleteDefault':
      'The default automatic deletion rule for sensitive scans.',
    'settings.description.Privacy.AccountDeletionGraceHours':
      'After an account is deleted, its files are removed for good after this long.',
    'settings.description.Privacy.ExportMaxMegabytes':
      'Up to this size (MB) the files go into a "Download my data" archive; above it they are only listed.',
    'settings.description.Fiscal.Enabled': 'Sales get a fiscal receipt.',
    'settings.description.Fiscal.TopUpReceiptMode':
      'Fiscal receipt when the balance is topped up (OnTopUp) or when the service is given (OnService).',
    'settings.description.Fiscal.MaxPendingMinutes':
      'A sale without a fiscal receipt after this long opens an incident.',
    'settings.description.Paper.BlockWhenInsufficient':
      'The kiosk refuses an order it does not have enough paper for.',
    'settings.description.Service.ForecastLeadHours':
      'A service task is created when the paper is forecast to run out within this many hours.',
    'settings.description.Service.TaskOverdueEscalationMinutes':
      'A critical task not done within this long is escalated.',
    'settings.description.Analytics.RetentionDays': 'How many days analytics events are kept.',
    'settings.description.Fiscal.OnlineTerminalId':
      'The fiscal terminal identifier for mobile (online) sales.',
    'settings.description.Kiosk.UpdateWindow': 'The hours in which kiosks may update themselves.',
    'settings.description.Update.CheckIntervalMinutes':
      'How often the kiosk checks for a new version.',
    'settings.description.Update.HealthTimeoutMinutes':
      'How long a kiosk has to run healthily after an update before it is rolled back.',
    'settings.description.Update.KeepVersions': 'How many earlier versions the kiosk keeps.',
    'settings.description.Enrollment.TokenTtlHours':
      'How many hours an enrollment token for a new kiosk stays valid.',
    'settings.description.Kiosk.CrashLoopThreshold':
      'If the kiosk app crashes this many times in 10 minutes it switches to safe mode.',
  },
  ru: {
    'settings.description.Queue.DisplayLimit':
      'Сколько заказов показывает очередь на экране киоска (3 или 5).',
    'settings.description.Payment.TimeoutMinutes':
      'Неоплаченный запрос на оплату отменяется через это время.',
    'settings.description.KioskSession.TtlMinutes':
      'Сессия телефона с киоском закрывается после такого бездействия.',
    'settings.description.Kiosk.IdleResetSeconds':
      'Без касаний киоск возвращается на главный экран через это время.',
    'settings.description.UserQr.RotationSeconds': 'Как часто обновляется личный QR-код.',
    'settings.description.PreparedPrint.AutoCancelDays':
      'Заказ, оплаченный в приложении и не забранный в киоске, отменяется через столько дней (деньги возвращаются на баланс).',
    'settings.description.Schedule.MaxDaysAhead':
      'На сколько дней вперёд максимум можно запланировать печать.',
    'settings.description.Schedule.ReminderLeadMinutes':
      'За сколько минут до печати по расписанию приходит напоминание (0 — в назначенное время).',
    'settings.description.Schedule.AutoCancelHours':
      'Через сколько часов без ответа наступившая печать по расписанию отменяется (деньги возвращаются на баланс).',
    'settings.description.Referral.BonusPages': 'Бонусные страницы пригласившему пользователю.',
    'settings.description.Referral.InviteeBonusPages':
      'Бонусные страницы приглашённому пользователю.',
    'settings.description.Referral.MaxBonusPagesPerJob':
      'Максимум бонусных страниц в одном заказе печати.',
    'settings.description.Referral.MaxRewardsPerMonth':
      'Сколько реферальных наград может получить один пригласивший за календарный месяц; 0 — без ограничения.',
    'settings.description.Refund.VoucherValidDays': 'Срок действия анонимного ваучера на возврат.',
    'settings.description.Refund.ProviderMaxAttempts':
      'Сколько раз повторяется неудачный возврат на карту.',
    'settings.description.Job.SuspectedAfterMinutes':
      'Если киоск молчит столько минут, печатающийся заказ становится «Подозрительным»; автоматического возврата нет, решает администратор.',
    'settings.description.Job.SuspectedAutoResolveHours':
      'Подозрительный заказ без решения через столько часов снова напоминает администраторам.',
    'settings.description.Public.SiteUrl':
      'Адрес, который открывает QR статуса возврата на экране киоска (например, https://neoprint.az).',
    'settings.description.Kiosk.HeartbeatSeconds':
      'Как часто киоск сообщает серверу, что он работает.',
    'settings.description.Kiosk.OfflineAfterSeconds':
      'Киоск без связи дольше этого времени считается недоступным.',
    'settings.description.Kiosk.TelemetrySeconds':
      'Как часто киоск отправляет данные о своём состоянии.',
    'settings.description.Kiosk.PrinterFaultAfterSeconds':
      'Если принтер не готов столько секунд, киоск останавливается: новые заказы не принимаются, ожидающие платежи гостей возвращаются.',
    'settings.description.PrintJob.MaxAutoRetries':
      'Сколько всего раз заказ, остановленный принтером, отправляется на печать (1–10). После этого он сам не продолжается: открывается инцидент, а владелец может отменить заказ и вернуть деньги.',
    'settings.description.Notification.MinSeverity':
      'Начиная с этого уровня инцидент отправляется по email учётным записям, которые следят за киоском: Critical, High или Medium. Менее серьёзные видны только в панели.',
    'settings.description.Notification.TelegramEnabled':
      'Канал Telegram для уведомлений об инцидентах. Выключен: канал откроется после пилота, для него нужен ещё и токен бота.',
    'settings.description.Kiosk.TelemetryRetentionDays':
      'Сколько дней хранится история состояния киосков.',
    'settings.description.Kiosk.PrinterEventRetentionDays':
      'Сколько дней хранятся события принтера (замена картриджа, загрузка бумаги, замятие).',
    'settings.description.Kiosk.ConfigCacheTtlHours':
      'Сколько действует конфигурация, сохранённая в киоске; устаревшая переводит киоск в ограниченный режим.',
    'settings.description.Payment.ReconcileAfterMinutes':
      'Платежи с неизвестным статусом сверяются с банком через это время.',
    'settings.description.Retention.StateTransitionDays':
      'Сколько дней хранится история смены состояний.',
    'settings.description.Retention.ProviderEventDays':
      'Сколько дней хранятся записи уведомлений платёжного провайдера.',
    'settings.description.Files.DefaultMaxSizeMb':
      'Наибольший размер файла (МБ) для мобильной загрузки и киосков без своего лимита.',
    'settings.description.Files.DefaultMaxPages':
      'Наибольшее число страниц для мобильной загрузки и киосков без своего лимита.',
    'settings.description.Files.UserQuotaGb':
      'Общий объём файлов, который может хранить пользователь (ГБ).',
    'settings.description.Files.AllowedFormats': 'Принимаемые форматы файлов.',
    'settings.description.Files.VersionsToKeep':
      'Сколько отредактированных версий каждого файла хранится.',
    'settings.description.Storage.SyncParallelism':
      'Сколько объектов копируется одновременно при синхронизации хранилища.',
    'settings.description.Storage.SourceRetentionDays':
      'Сколько дней файлы остаются у старого поставщика после миграции.',
    'settings.description.Storage.ReconciliationCron':
      'Когда в зеркальном режиме каждую ночь сверяются два поставщика (cron).',
    'settings.description.Layout.DefaultMarginMm': 'Поле по умолчанию в макете страницы (мм).',
    'settings.description.Privacy.SensitiveAutoDeleteDefault':
      'Правило автоматического удаления для конфиденциальных сканов по умолчанию.',
    'settings.description.Privacy.AccountDeletionGraceHours':
      'После удаления учётной записи её файлы окончательно удаляются через это время.',
    'settings.description.Privacy.ExportMaxMegabytes':
      'До этого размера (МБ) файлы входят в архив «Скачать мои данные»; если больше — только их список.',
    'settings.description.Fiscal.Enabled': 'Для продаж выбивается фискальный чек.',
    'settings.description.Fiscal.TopUpReceiptMode':
      'Фискальный чек при пополнении баланса (OnTopUp) или при оказании услуги (OnService).',
    'settings.description.Fiscal.MaxPendingMinutes':
      'Если чек по продаже не выбит за это время, открывается инцидент.',
    'settings.description.Paper.BlockWhenInsufficient':
      'Киоск не принимает заказ, если бумаги не хватает.',
    'settings.description.Service.ForecastLeadHours':
      'Сервисная задача создаётся, когда бумага по прогнозу закончится в течение стольких часов.',
    'settings.description.Service.TaskOverdueEscalationMinutes':
      'Критическая задача, не выполненная за это время, передаётся выше.',
    'settings.description.Analytics.RetentionDays': 'Сколько дней хранятся события аналитики.',
    'settings.description.Fiscal.OnlineTerminalId':
      'Идентификатор фискального терминала для мобильных (онлайн) продаж.',
    'settings.description.Kiosk.UpdateWindow':
      'Часы, в которые киоскам разрешено обновляться автоматически.',
    'settings.description.Update.CheckIntervalMinutes':
      'Как часто киоск проверяет наличие новой версии.',
    'settings.description.Update.HealthTimeoutMinutes':
      'Сколько киоск должен стабильно работать после обновления, иначе оно откатывается.',
    'settings.description.Update.KeepVersions': 'Сколько предыдущих версий хранится в киоске.',
    'settings.description.Enrollment.TokenTtlHours':
      'Сколько часов действует токен подключения нового киоска.',
    'settings.description.Kiosk.CrashLoopThreshold':
      'Если приложение киоска падает столько раз за 10 минут, оно переходит в безопасный режим.',
  },
};
