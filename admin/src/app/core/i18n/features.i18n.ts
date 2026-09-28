import type { PageTranslations } from './page-translations';

/**
 * Human names and one-line explanations of the feature flags (plan section 2.16).
 *
 * The page shows these; the technical key stays underneath in small type. A flag
 * without an entry here falls back to the key and the backend description.
 */
export const featuresTranslations: PageTranslations = {
  az: {
    'features.flag.mobile_payment_at_kiosk.name': 'Kioskda mobil tətbiqlə ödəniş',
    'features.flag.mobile_payment_at_kiosk.description':
      'Kioskda çap sifarişini NeoPrint tətbiqindəki QR skaneri ilə ödəmək imkanı.',
    'features.flag.user_qr_print.name': 'Şəxsi QR ilə çap',
    'features.flag.user_qr_print.description':
      'İstifadəçi şəxsi QR kodunu kioska göstərir və tətbiqdə hazırladığı faylları orada çap edir.',
    'features.flag.mobile_session_print.name': 'Mobil sessiya ilə çap',
    'features.flag.mobile_session_print.description':
      'Kioskdakı QR-ı skan edən istifadəçi faylı telefondan birbaşa həmin kioska göndərir.',
    'features.flag.scheduled_print.name': 'Planlanmış çap',
    'features.flag.scheduled_print.description':
      'Faylı əvvəlcədən seçilmiş vaxtda və kioskda çap üçün hazırlamaq; vaxtı çatanda xatırlatma gəlir.',
    'features.flag.file_sharing.name': 'Fayl paylaşımı',
    'features.flag.file_sharing.description':
      'İstifadəçi faylını başqa NeoPrint istifadəçisinə göndərə bilir.',
    'features.flag.referral.name': 'Dostunu dəvət et',
    'features.flag.referral.description':
      'Dəvət kodu ilə qoşulan dostun ilk ödənişli çapından sonra dəvət edənə bonus yazılır.',
    'features.flag.promo_codes.name': 'Promokodlar',
    'features.flag.promo_codes.description': 'Ödəniş zamanı endirim kodu daxil etmək imkanı.',
    'features.flag.layout_editor.name': 'Səhifə tərtibatı',
    'features.flag.layout_editor.description':
      'Bir neçə şəkli (məsələn, vəsiqənin iki üzünü) bir vərəqdə yerləşdirib çap etmək.',
    'features.flag.document_scan.name': 'Sənəd skanı',
    'features.flag.document_scan.description':
      'Telefon kamerası ilə sənədi skan edib PDF kimi saxlamaq.',
    'features.flag.txt_editor.name': 'Mətn faylı redaktoru',
    'features.flag.txt_editor.description':
      'TXT faylını çapdan əvvəl kioskda və ya tətbiqdə düzəltmək.',
    'features.flag.data_export.name': 'Məlumatlarımı yüklə',
    'features.flag.data_export.description':
      'İstifadəçi hesabındakı bütün məlumatların arxivini tətbiqdən sifariş edə bilir.',
    'features.technicalKey': 'Texniki açar',
    'features.platform.Kiosk': 'Kiosk',
    'features.platform.Mobile': 'Mobil tətbiq',
    'features.platform.Backend': 'Server hissəsi',
    'features.platform.Admin': 'Admin paneli',
  },
  en: {
    'features.flag.mobile_payment_at_kiosk.name': 'Pay at the kiosk with the mobile app',
    'features.flag.mobile_payment_at_kiosk.description':
      'A print order at the kiosk can be paid by scanning its QR code in the NeoPrint app.',
    'features.flag.user_qr_print.name': 'Print with a personal QR code',
    'features.flag.user_qr_print.description':
      'The user shows their personal QR code to the kiosk and prints the files prepared in the app.',
    'features.flag.mobile_session_print.name': 'Print from a mobile session',
    'features.flag.mobile_session_print.description':
      'A user who scans the kiosk QR code sends a file from the phone straight to that kiosk.',
    'features.flag.scheduled_print.name': 'Scheduled printing',
    'features.flag.scheduled_print.description':
      'A file is prepared for printing at a chosen time and kiosk, with a reminder when it is due.',
    'features.flag.file_sharing.name': 'File sharing',
    'features.flag.file_sharing.description':
      'A user can send their file to another NeoPrint user.',
    'features.flag.referral.name': 'Invite a friend',
    'features.flag.referral.description':
      'The inviter gets a bonus after the invited friend makes a first paid print.',
    'features.flag.promo_codes.name': 'Promo codes',
    'features.flag.promo_codes.description': 'A discount code can be entered at payment.',
    'features.flag.layout_editor.name': 'Page layout',
    'features.flag.layout_editor.description':
      'Several images, such as both sides of an ID card, are placed on one sheet and printed.',
    'features.flag.document_scan.name': 'Document scan',
    'features.flag.document_scan.description':
      'A document is scanned with the phone camera and saved as a PDF.',
    'features.flag.txt_editor.name': 'Text file editor',
    'features.flag.txt_editor.description':
      'A TXT file can be corrected at the kiosk or in the app before printing.',
    'features.flag.data_export.name': 'Download my data',
    'features.flag.data_export.description':
      'A user can order an archive of all the data in their account from the app.',
    'features.technicalKey': 'Technical key',
    'features.platform.Kiosk': 'Kiosk',
    'features.platform.Mobile': 'Mobile app',
    'features.platform.Backend': 'Server',
    'features.platform.Admin': 'Admin panel',
  },
  ru: {
    'features.flag.mobile_payment_at_kiosk.name': 'Оплата в киоске через мобильное приложение',
    'features.flag.mobile_payment_at_kiosk.description':
      'Заказ печати в киоске можно оплатить, отсканировав QR-код в приложении NeoPrint.',
    'features.flag.user_qr_print.name': 'Печать по личному QR-коду',
    'features.flag.user_qr_print.description':
      'Пользователь показывает киоску свой QR-код и печатает файлы, подготовленные в приложении.',
    'features.flag.mobile_session_print.name': 'Печать через мобильную сессию',
    'features.flag.mobile_session_print.description':
      'Пользователь сканирует QR-код киоска и отправляет файл с телефона прямо в этот киоск.',
    'features.flag.scheduled_print.name': 'Печать по расписанию',
    'features.flag.scheduled_print.description':
      'Файл готовится к печати на выбранное время и в выбранном киоске, с напоминанием.',
    'features.flag.file_sharing.name': 'Обмен файлами',
    'features.flag.file_sharing.description':
      'Пользователь может отправить свой файл другому пользователю NeoPrint.',
    'features.flag.referral.name': 'Пригласи друга',
    'features.flag.referral.description':
      'Пригласивший получает бонус после первой платной печати приглашённого друга.',
    'features.flag.promo_codes.name': 'Промокоды',
    'features.flag.promo_codes.description': 'При оплате можно ввести код скидки.',
    'features.flag.layout_editor.name': 'Макет страницы',
    'features.flag.layout_editor.description':
      'Несколько изображений, например обе стороны удостоверения, размещаются на одном листе.',
    'features.flag.document_scan.name': 'Сканирование документов',
    'features.flag.document_scan.description':
      'Документ сканируется камерой телефона и сохраняется как PDF.',
    'features.flag.txt_editor.name': 'Редактор текстовых файлов',
    'features.flag.txt_editor.description':
      'TXT-файл можно исправить в киоске или в приложении перед печатью.',
    'features.flag.data_export.name': 'Скачать мои данные',
    'features.flag.data_export.description':
      'Пользователь может заказать в приложении архив всех данных своей учётной записи.',
    'features.technicalKey': 'Технический ключ',
    'features.platform.Kiosk': 'Киоск',
    'features.platform.Mobile': 'Мобильное приложение',
    'features.platform.Backend': 'Сервер',
    'features.platform.Admin': 'Панель администратора',
  },
};
