import type { PageTranslations } from './page-translations';

/** "Monitorinq" (plan step 8, T-477): the way to the Grafana dashboards. */
export const systemMonitoringTranslations: PageTranslations = {
  az: {
    'nav.systemMonitoring': 'Monitorinq',

    'sysmon.title': 'Monitorinq',
    'sysmon.description':
      'Serverin vəziyyətini göstərən qrafiklər Grafana-dadır: API gecikməsi və xətaları, növbələr, refund, fiskal, storage, kiosklar və incident-lər. Gözlənilməz xətaların təfərrüatı GlitchTip-dədir.',
    'sysmon.open': 'Grafana-nı aç',
    'sysmon.loginNote': 'Grafana-nın öz girişi var. İstifadəçi və parolu serverin administratoru verir.',
    'sysmon.loadFailedEffect': 'Monitorinqin vəziyyəti göstərilə bilmir. Sistemin işinə və metrikaların yığılmasına təsir etmir.',
    'sysmon.metricsOn': 'Metrikalar göndərilir',
    'sysmon.metricsOff': 'Metrikalar göndərilmir',
    'sysmon.metricsOffHint':
      'API metrika göndərmir: Monitoring__OtlpEndpoint qurulmayıb. Serverdə monitorinq profilini yandırmaq üçün docs/MONITORING.md-ə baxın.',
    'sysmon.countedAt': 'Göstəricilər son dəfə {{time}} sayılıb',
    'sysmon.notCounted': 'Göstəricilər hələ sayılmayıb (API başlayandan sonra bir dəqiqə ərzində)',
    'sysmon.noGrafana': 'Grafana-nın ünvanı qurulmayıb (Monitoring__GrafanaUrl). Qrafiklər açıla bilmir.',
    'sysmon.dashboards': 'Hazır qrafik panelləri',
    'sysmon.dash.overview': 'Ümumi baxış — hər şey bir ekranda, qırmızı = baxmaq lazımdır',
    'sysmon.dash.api': 'API — ünvanlar üzrə gecikmə və xətalar, resurslar',
    'sysmon.dash.queues': 'Növbələr — RabbitMQ və dead-letter növbələri',
    'sysmon.dash.operations': 'Əməliyyatlar — refund, fiskal, storage, kiosk və incident sayları',
    'sysmon.errors.title': 'Xəta izləmə (GlitchTip)',
    'sysmon.errors.on': 'Xətalar GlitchTip-ə göndərilir',
    'sysmon.errors.off': 'Xətalar göndərilmir',
    'sysmon.errors.offHint':
      'API-nin GlitchTip ünvanı (ErrorTracking__ApiDsn) qurulmayıb. Gözlənilməz xətalar yalnız log-da qalır. Qurulma qaydası docs/MONITORING.md-dədir.',
    'sysmon.errors.open': 'GlitchTip-i aç',
    'sysmon.errors.test': 'Sınaq xətası göndər',
    'sysmon.errors.testSent': 'Sınaq xətası göndərildi ({{id}}…). Bir dəqiqə ərzində GlitchTip-də "NeoPrint API" layihəsində görünməlidir.',
    'sysmon.errors.loginNote':
      'API, kiosklar, mobil tətbiq və bu panel gözlənilməz xətalarını GlitchTip-ə göndərir. İstifadəçi adı, e-poçt, IP və sorğunun məzmunu göndərilmir. GlitchTip-in öz girişi var.',
  },
  en: {
    'nav.systemMonitoring': 'Monitoring',

    'sysmon.title': 'Monitoring',
    'sysmon.description':
      'The charts of how the server is doing are in Grafana: API latency and errors, queues, refunds, fiscal receipts, storage, kiosks and incidents.',
    'sysmon.open': 'Open Grafana',
    'sysmon.loginNote': 'Grafana has its own sign-in. The server administrator gives out the user and password.',
    'sysmon.loadFailedEffect': 'The monitoring status cannot be shown. The system and the collection of metrics are not affected.',
    'sysmon.metricsOn': 'Metrics are being sent',
    'sysmon.metricsOff': 'Metrics are not being sent',
    'sysmon.metricsOffHint':
      'The API sends no metrics: Monitoring__OtlpEndpoint is not set. See docs/MONITORING.md to turn the monitoring profile on.',
    'sysmon.countedAt': 'Figures last counted {{time}}',
    'sysmon.notCounted': 'Figures not counted yet (within a minute of the API starting)',
    'sysmon.noGrafana': 'Grafana’s address is not configured (Monitoring__GrafanaUrl). The charts cannot be opened.',
    'sysmon.dashboards': 'Ready-made dashboards',
    'sysmon.dash.overview': 'Overview — everything on one screen, red = needs a look',
    'sysmon.dash.api': 'API — latency and errors by endpoint, resources',
    'sysmon.dash.queues': 'Queues — RabbitMQ and the dead-letter queues',
    'sysmon.dash.operations': 'Operations — refund, fiscal, storage, kiosk and incident counts',
    'sysmon.errors.title': 'Error tracking (GlitchTip)',
    'sysmon.errors.on': 'Errors are sent to GlitchTip',
    'sysmon.errors.off': 'Errors are not sent',
    'sysmon.errors.offHint':
      'The API has no GlitchTip address (ErrorTracking__ApiDsn). Unexpected errors stay in the log only. docs/MONITORING.md says how to set it up.',
    'sysmon.errors.open': 'Open GlitchTip',
    'sysmon.errors.test': 'Send a test error',
    'sysmon.errors.testSent': 'Test error sent ({{id}}…). It should show in GlitchTip under "NeoPrint API" within a minute.',
    'sysmon.errors.loginNote':
      'The API, the kiosks, the app and this panel send their unexpected errors to GlitchTip. No user name, e-mail, IP or request content is sent. GlitchTip has its own sign-in.',
  },
  ru: {
    'nav.systemMonitoring': 'Мониторинг',

    'sysmon.title': 'Мониторинг',
    'sysmon.description':
      'Графики состояния сервера — в Grafana: задержки и ошибки API, очереди, возвраты, фискальные чеки, хранилище, киоски и инциденты.',
    'sysmon.open': 'Открыть Grafana',
    'sysmon.loginNote': 'У Grafana свой вход. Пользователя и пароль выдаёт администратор сервера.',
    'sysmon.loadFailedEffect': 'Состояние мониторинга не удаётся показать. На работу системы и сбор метрик это не влияет.',
    'sysmon.metricsOn': 'Метрики отправляются',
    'sysmon.metricsOff': 'Метрики не отправляются',
    'sysmon.metricsOffHint':
      'API не отправляет метрики: Monitoring__OtlpEndpoint не задан. Как включить профиль мониторинга — в docs/MONITORING.md.',
    'sysmon.countedAt': 'Показатели посчитаны {{time}}',
    'sysmon.notCounted': 'Показатели ещё не посчитаны (в течение минуты после запуска API)',
    'sysmon.noGrafana': 'Адрес Grafana не настроен (Monitoring__GrafanaUrl). Графики открыть нельзя.',
    'sysmon.dashboards': 'Готовые дашборды',
    'sysmon.dash.overview': 'Обзор — всё на одном экране, красное = нужно посмотреть',
    'sysmon.dash.api': 'API — задержки и ошибки по эндпоинтам, ресурсы',
    'sysmon.dash.queues': 'Очереди — RabbitMQ и очереди недоставленных сообщений',
    'sysmon.dash.operations': 'Операции — возвраты, фискальные чеки, хранилище, киоски и инциденты',
    'sysmon.errors.title': 'Отслеживание ошибок (GlitchTip)',
    'sysmon.errors.on': 'Ошибки отправляются в GlitchTip',
    'sysmon.errors.off': 'Ошибки не отправляются',
    'sysmon.errors.offHint':
      'У API нет адреса GlitchTip (ErrorTracking__ApiDsn). Непредвиденные ошибки остаются только в логе. Как настроить — в docs/MONITORING.md.',
    'sysmon.errors.open': 'Открыть GlitchTip',
    'sysmon.errors.test': 'Отправить тестовую ошибку',
    'sysmon.errors.testSent': 'Тестовая ошибка отправлена ({{id}}…). В течение минуты она появится в GlitchTip в проекте «NeoPrint API».',
    'sysmon.errors.loginNote':
      'API, киоски, мобильное приложение и эта панель отправляют непредвиденные ошибки в GlitchTip. Имя пользователя, e-mail, IP и содержимое запросов не отправляются. У GlitchTip свой вход.',
  },
};
