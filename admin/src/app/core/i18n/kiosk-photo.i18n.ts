import type { PageTranslations } from './page-translations';

/** "Kiosk şəkli" on the kiosk's General tab (T-340). */
export const kioskPhotoTranslations: PageTranslations = {
  az: {
    'kioskPhoto.title': 'Kiosk şəkli',
    'kioskPhoto.hint':
      'Mobil tətbiqdə kiosk kartlarında görünür. Binanın və ya kioskun yaxşı işıqlı şəkli seçin; şəkil göndərilməzdən əvvəl kiçildilir.',
    'kioskPhoto.alt': 'Kioskun şəkli',
    'kioskPhoto.upload': 'Şəkil yüklə',
    'kioskPhoto.replace': 'Şəkli dəyiş',
    'kioskPhoto.remove': 'Şəkli sil',
    'kioskPhoto.saved': 'Şəkil saxlandı. Tətbiqdə kiosk kartlarında görünəcək.',
    'kioskPhoto.removed': 'Şəkil silindi. Tətbiqdə yerinə ikon görünəcək.',
    'kioskPhoto.notImage': 'Bu fayl şəkil kimi açılmadı. JPEG, PNG və ya WebP şəkil seçin.',
    'kioskPhoto.failedEffect': 'Kioskun işinə təsir etmir — yalnız tətbiqdəki şəkil dəyişmədi.',
  },
  en: {
    'kioskPhoto.title': 'Kiosk photo',
    'kioskPhoto.hint':
      "Shown on the kiosk's cards in the mobile app. Choose a well-lit photo of the building or the kiosk; it is shrunk before it is sent.",
    'kioskPhoto.alt': 'The kiosk',
    'kioskPhoto.upload': 'Upload a photo',
    'kioskPhoto.replace': 'Change the photo',
    'kioskPhoto.remove': 'Remove the photo',
    'kioskPhoto.saved': "Photo saved. It will show on the kiosk's cards in the app.",
    'kioskPhoto.removed': 'Photo removed. The app shows an icon instead.',
    'kioskPhoto.notImage': 'This file did not open as a picture. Choose a JPEG, PNG or WebP image.',
    'kioskPhoto.failedEffect': "The kiosk keeps working — only the app's picture did not change.",
  },
  ru: {
    'kioskPhoto.title': 'Фото киоска',
    'kioskPhoto.hint':
      'Показывается на карточках киоска в мобильном приложении. Выберите хорошо освещённое фото здания или киоска; перед отправкой оно уменьшается.',
    'kioskPhoto.alt': 'Фото киоска',
    'kioskPhoto.upload': 'Загрузить фото',
    'kioskPhoto.replace': 'Заменить фото',
    'kioskPhoto.remove': 'Удалить фото',
    'kioskPhoto.saved': 'Фото сохранено. Оно появится на карточках киоска в приложении.',
    'kioskPhoto.removed': 'Фото удалено. В приложении вместо него будет значок.',
    'kioskPhoto.notImage': 'Этот файл не открылся как изображение. Выберите JPEG, PNG или WebP.',
    'kioskPhoto.failedEffect': 'На работу киоска это не влияет — не изменилось только фото в приложении.',
  },
};
