/**
 * The kiosk's own words for its welcome screen and footer (T-422): what a
 * kiosk shows when the panel says nothing. The same as the kiosk app's
 * `home.*` texts (desktop/src/NeoPrint.Kiosk/Localization/Strings.cs) - a
 * change there is made here too. The hotline has none: it shows only when
 * the panel writes one.
 */
export const KIOSK_BUILT_IN_TEXTS: Readonly<
  Record<string, Readonly<Record<'az' | 'en' | 'ru', string>>>
> = {
  heroTitle: {
    az: 'Sənədlərinizi burada çap edin',
    en: 'Print your documents here',
    ru: 'Печатайте документы здесь',
  },
  heroSubtitle: {
    az: 'Sürətli, rahat və təhlükəsiz çap xidməti',
    en: 'A fast, easy and secure printing service',
    ru: 'Быстрая, удобная и безопасная печать',
  },
  usbTitle: { az: 'USB / Fleş kart', en: 'USB / Flash', ru: 'USB / Флешка' },
  usbHint: {
    az: 'Fayllarınızı flash kartdan seçin',
    en: 'Choose your files from a USB stick',
    ru: 'Выберите файлы с флешки',
  },
  phoneTitle: { az: 'Telefon / QR', en: 'Phone / QR', ru: 'Телефон / QR' },
  phoneHint: {
    az: 'Tətbiqdə "Mənim QR kodum"u skanerə tutun',
    en: 'Hold "My QR code" in the app to the scanner',
    ru: 'Поднесите «Мой QR-код» из приложения к сканеру',
  },
  codeTitle: { az: 'Çap kodu', en: 'Print code', ru: 'Код печати' },
  codeHint: {
    az: 'Tətbiqdə hazırladığınız faylları açın',
    en: 'Open the files you prepared in the app',
    ru: 'Откройте файлы, подготовленные в приложении',
  },
  featureSizes: { az: 'A4 və A3', en: 'A4 / A3', ru: 'A4 / A3' },
  featureSizesHint: { az: 'Dəstəklənir', en: 'Supported', ru: 'Поддерживается' },
  featureColor: { az: 'Rəngli və qara-ağ', en: 'Colour and black & white', ru: 'Цветная и ч/б' },
  featureColorHint: { az: 'Çap', en: 'Printing', ru: 'Печать' },
  featureFast: { az: 'Sürətli çap', en: 'Fast printing', ru: 'Быстрая печать' },
  featureFastHint: { az: 'Bir neçə dəqiqə', en: 'A few minutes', ru: 'Несколько минут' },
  featureSafe: { az: 'Təhlükəsiz', en: 'Secure', ru: 'Безопасно' },
  featureSafeHint: {
    az: 'Sənədləriniz qorunur',
    en: 'Your documents are protected',
    ru: 'Документы защищены',
  },
};
