import type { PageTranslations } from './page-translations';

/** Kiosk detail → Status → "Açıq sessiyalar" (T-362). */
export const kioskSessionsTranslations: PageTranslations = {
  az: {
    'kioskSessions.title': 'Açıq sessiyalar',
    'kioskSessions.hint':
      'Bu kioska telefondan qoşulmuş müştərilər. Açıq sessiya kioskun yenilənməsini saxlayır; lazım olsa bağlaya bilərsiniz.',
    'kioskSessions.colCustomer': 'Müştəri',
    'kioskSessions.colOpened': 'Açılıb',
    'kioskSessions.colEnds': 'Bitir',
    'kioskSessions.minutesLeft': '{{minutes}} dəq qalıb',
    'kioskSessions.close': 'Bağla',
    'kioskSessions.empty': 'Açıq sessiya yoxdur',
    'kioskSessions.emptyHint': 'Müştəri tətbiqdə kioskun QR kodunu oxuyanda burada görünür.',
    'kioskSessions.loadFailedEffect': 'Sessiyalar göstərilə bilmir. Kioskun işinə təsir etmir.',
    'kioskSessions.confirmTitle': 'Sessiya bağlansın?',
    'kioskSessions.confirmExplanation':
      'Müştərinin telefonu bu kiosku buraxır və tətbiqdə sessiyanın bağlandığı yazılır. Ödənilmiş sifarişlər qalır və çap olunur.',
    'kioskSessions.reason': 'Səbəb',
    'kioskSessions.reasonHint': 'Audit jurnalına yazılır, məs. "kiosk yenilənməlidir".',
    'kioskSessions.closed': 'Sessiya bağlandı. Telefona bildiriş getdi.',
  },
  en: {
    'kioskSessions.title': 'Open sessions',
    'kioskSessions.hint':
      'Customers joined to this kiosk from their phone. An open session holds the kiosk\'s update back; you can close it if needed.',
    'kioskSessions.colCustomer': 'Customer',
    'kioskSessions.colOpened': 'Opened',
    'kioskSessions.colEnds': 'Ends',
    'kioskSessions.minutesLeft': '{{minutes}} min left',
    'kioskSessions.close': 'Close',
    'kioskSessions.empty': 'No open sessions',
    'kioskSessions.emptyHint': "A customer shows up here when they scan the kiosk's QR code in the app.",
    'kioskSessions.loadFailedEffect': 'The sessions cannot be shown. The kiosk keeps working.',
    'kioskSessions.confirmTitle': 'Close the session?',
    'kioskSessions.confirmExplanation':
      "The customer's phone leaves this kiosk and the app says the session was closed. Paid orders stay and are printed.",
    'kioskSessions.reason': 'Reason',
    'kioskSessions.reasonHint': 'Goes into the audit log, e.g. "the kiosk needs to update".',
    'kioskSessions.closed': 'Session closed. The phone was told.',
  },
  ru: {
    'kioskSessions.title': 'Открытые сессии',
    'kioskSessions.hint':
      'Клиенты, подключившиеся к киоску с телефона. Открытая сессия задерживает обновление киоска; при необходимости её можно закрыть.',
    'kioskSessions.colCustomer': 'Клиент',
    'kioskSessions.colOpened': 'Открыта',
    'kioskSessions.colEnds': 'Закончится',
    'kioskSessions.minutesLeft': 'осталось {{minutes}} мин',
    'kioskSessions.close': 'Закрыть',
    'kioskSessions.empty': 'Открытых сессий нет',
    'kioskSessions.emptyHint': 'Клиент появится здесь, когда отсканирует QR-код киоска в приложении.',
    'kioskSessions.loadFailedEffect': 'Сессии не удаётся показать. На работу киоска это не влияет.',
    'kioskSessions.confirmTitle': 'Закрыть сессию?',
    'kioskSessions.confirmExplanation':
      'Телефон клиента отключится от киоска, а в приложении появится сообщение о закрытии сессии. Оплаченные заказы останутся и будут напечатаны.',
    'kioskSessions.reason': 'Причина',
    'kioskSessions.reasonHint': 'Записывается в журнал аудита, напр. «киоску нужно обновиться».',
    'kioskSessions.closed': 'Сессия закрыта. Телефон уведомлён.',
  },
};
