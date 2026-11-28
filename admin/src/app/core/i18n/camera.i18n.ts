import type { PageTranslations } from './page-translations';

/** Kiosk detail → "Kamera" (T-483): live and recorded. */
export const cameraTranslations: PageTranslations = {
  az: {
    'kiosks.tabCamera': 'Kamera',
    'camera.audited': 'Hər baxış audit jurnalına yazılır.',
    'camera.off': 'Bu kioskda kamera söndürülüb',
    'camera.offHint': '"Funksiya açarları" → kiosk_camera ilə bu kiosk üçün açılır.',
    'camera.loadFailedEffect': 'Kamera görüntüsü göstərilmir; kioskun işinə təsir etmir.',
    'camera.liveTitle': 'Canlı görüntü',
    'camera.liveIdle': '"Canlı bax"a basın',
    'camera.liveStart': 'Canlı bax',
    'camera.liveStop': 'Dayandır',
    'camera.liveOn': 'Canlı',
    'camera.liveAlt': 'Kioskun kamerasından canlı kadr',
    'camera.liveNoFrames':
      'Kioskdan kadr gəlmir. Kiosk oflayn ola bilər və ya kamera yazmır — «Vəziyyət» tabında kameranın vəziyyətinə baxın.',
    'camera.recordedTitle': 'Yazılmış görüntü',
    'camera.date': 'Tarix',
    'camera.startTime': 'Başlanğıc saatı',
    'camera.duration': 'Müddət',
    'camera.minutes': '{{count}} dəqiqə',
    'camera.show': 'Göstər',
    'camera.none': 'Bu aralıqda görüntü yoxdur',
    'camera.noneHint':
      'Kamera bu vaxt yazmayıb (Activity rejimində boş vaxt yazılmır) və ya saxlama müddəti bitib.',
    'camera.partial':
      'Bu aralığın {{count}} dəqiqəsi serverdədir. Qalanı yalnız kioskun diskindədir.',
    'camera.allOnKiosk': 'Bu aralığın görüntüsü yalnız kioskun diskindədir.',
    'camera.fetch': 'Kioskdan gətir',
    'camera.fetchTooLong': 'Kioskdan bir dəfəyə ən çox 60 dəqiqə gətirmək olar — müddəti qısaldın.',
    'camera.fetching': 'Kiosk dəqiqələri göndərir…',
    'camera.fetchSlow':
      'Kiosk hələ cavab vermir — oflayn ola bilər; qoşulanda göndərəcək. Bu səhifəni bağlaya bilərsiniz.',
    'camera.fetchedNothing':
      'Kiosk bu aralıqda yazı tapmadı: Activity rejimində boş vaxt yazılmır, ya da saxlama müddəti bitib.',
    'camera.fetchedNote': 'Kioskdan gətirilib — serverdə 24 saat qalır',
    'camera.archivedNote': 'Fayl serverindən',
    'camera.player': 'Kamera görüntüsü',
    'camera.minuteList': 'Dəqiqələr',
    'camera.download': 'Bu dəqiqəni yüklə',
  },
  en: {
    'kiosks.tabCamera': 'Camera',
    'camera.audited': 'Every look is written to the audit log.',
    'camera.off': "This kiosk's camera is turned off",
    'camera.offHint': 'Turn it on for this kiosk in "Feature flags" → kiosk_camera.',
    'camera.loadFailedEffect': "The camera footage is not shown; the kiosk's work is not affected.",
    'camera.liveTitle': 'Live',
    'camera.liveIdle': 'Press "Watch live"',
    'camera.liveStart': 'Watch live',
    'camera.liveStop': 'Stop',
    'camera.liveOn': 'Live',
    'camera.liveAlt': "A live frame from the kiosk's camera",
    'camera.liveNoFrames':
      'No frames come from the kiosk. It may be offline or the camera is not recording - see the camera on the "Status" tab.',
    'camera.recordedTitle': 'Recorded',
    'camera.date': 'Date',
    'camera.startTime': 'Start time',
    'camera.duration': 'Length',
    'camera.minutes': '{{count}} minutes',
    'camera.show': 'Show',
    'camera.none': 'No footage in this span',
    'camera.noneHint':
      'The camera did not record then (in Activity mode idle time is not recorded) or its keeping time is over.',
    'camera.partial':
      "{{count}} minutes of this span are on the server. The rest is only on the kiosk's disk.",
    'camera.allOnKiosk': "This span's footage is only on the kiosk's disk.",
    'camera.fetch': 'Fetch from the kiosk',
    'camera.fetchTooLong':
      'At most 60 minutes can be fetched from the kiosk at once - make the span shorter.',
    'camera.fetching': 'The kiosk is sending the minutes…',
    'camera.fetchSlow':
      'The kiosk does not answer yet - it may be offline; it will send them once it is back. You can close this page.',
    'camera.fetchedNothing':
      'The kiosk found no footage in this span: in Activity mode idle time is not recorded, or its keeping time is over.',
    'camera.fetchedNote': 'Fetched from the kiosk - kept on the server for 24 hours',
    'camera.archivedNote': 'From the file server',
    'camera.player': 'Camera footage',
    'camera.minuteList': 'Minutes',
    'camera.download': 'Download this minute',
  },
  ru: {
    'kiosks.tabCamera': 'Камера',
    'camera.audited': 'Каждый просмотр записывается в журнал аудита.',
    'camera.off': 'Камера этого киоска выключена',
    'camera.offHint': 'Включается для этого киоска в «Флаги функций» → kiosk_camera.',
    'camera.loadFailedEffect': 'Запись камеры не показывается; на работу киоска это не влияет.',
    'camera.liveTitle': 'Прямой эфир',
    'camera.liveIdle': 'Нажмите «Смотреть в эфире»',
    'camera.liveStart': 'Смотреть в эфире',
    'camera.liveStop': 'Остановить',
    'camera.liveOn': 'Эфир',
    'camera.liveAlt': 'Кадр с камеры киоска в эфире',
    'camera.liveNoFrames':
      'С киоска не приходят кадры. Киоск может быть офлайн или камера не пишет — посмотрите камеру на вкладке «Состояние».',
    'camera.recordedTitle': 'Записи',
    'camera.date': 'Дата',
    'camera.startTime': 'Время начала',
    'camera.duration': 'Длительность',
    'camera.minutes': '{{count}} мин',
    'camera.show': 'Показать',
    'camera.none': 'В этом промежутке записи нет',
    'camera.noneHint':
      'Камера тогда не писала (в режиме Activity простой не записывается) или срок хранения истёк.',
    'camera.partial':
      '{{count}} мин этого промежутка есть на сервере. Остальное — только на диске киоска.',
    'camera.allOnKiosk': 'Запись этого промежутка есть только на диске киоска.',
    'camera.fetch': 'Забрать с киоска',
    'camera.fetchTooLong':
      'С киоска за раз можно забрать не больше 60 минут — сократите промежуток.',
    'camera.fetching': 'Киоск отправляет минуты…',
    'camera.fetchSlow':
      'Киоск пока не отвечает — возможно, он офлайн; отправит, когда подключится. Эту страницу можно закрыть.',
    'camera.fetchedNothing':
      'Киоск не нашёл записи в этом промежутке: в режиме Activity простой не записывается, или срок хранения истёк.',
    'camera.fetchedNote': 'Забрано с киоска — хранится на сервере 24 часа',
    'camera.archivedNote': 'С файлового сервера',
    'camera.player': 'Запись камеры',
    'camera.minuteList': 'Минуты',
    'camera.download': 'Скачать эту минуту',
  },
};
