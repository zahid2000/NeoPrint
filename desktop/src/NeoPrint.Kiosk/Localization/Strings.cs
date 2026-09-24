using System.Collections.Frozen;

namespace NeoPrint.Kiosk.Localization;

/// <summary>
/// The kiosk's interface text in Azerbaijani, English and Russian
/// (plan section 2.1: az is the default).
/// </summary>
/// <remarks>
/// Held as dictionaries rather than resx files because the kiosk switches
/// language from the header at runtime, on a screen that is already built. Only
/// UI text lives here; identifiers and log messages are English
/// (CLAUDE.md).
/// </remarks>
public static class Strings
{
    public static readonly string[] SupportedLanguages = ["az", "en", "ru"];

    public const string DefaultLanguage = "az";

    /// <summary>Language names as shown in the language picker.</summary>
    public static readonly FrozenDictionary<string, string> LanguageNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["az"] = "Azərbaycan",
            ["en"] = "English",
            ["ru"] = "Русский",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, FrozenDictionary<string, string>> Catalogue =
        new Dictionary<string, FrozenDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["az"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app.starting"] = "Kiosk başladılır…",
                ["app.hotline"] = "Kömək: 0${0}",

                ["activation.title"] = "Bu kiosk aktivləşdirilməyib",
                ["activation.instruction"] =
                    "Aktivləşdirmək üçün bu kodu admin panelinə daxil edin və ya QR kodu operator tətbiqi ilə oxudun.",
                ["activation.codeExpires"] = "Kod {0} sonra yenilənir",
                ["activation.deviceInfo"] = "Cihaz məlumatı",
                ["activation.hostname"] = "Ad",
                ["activation.platform"] = "Platforma",
                ["activation.version"] = "Versiya",
                ["activation.printers"] = "Aşkarlanan printerlər",
                ["activation.noPrinters"] = "Printer aşkarlanmadı",
                ["activation.connected"] = "Serverə qoşuludur",
                ["activation.disconnected"] = "Server əlçatmazdır — yenidən cəhd edilir",
                ["activation.revoked"] = "Cihaz ləğv edildi. Yeni kimlik yaradılır…",

                ["home.title"] = "Xoş gəlmisiniz",
                ["home.printFromUsb"] = "Flash kartdan çap et",
                ["home.scanMyQr"] = "QR kodumu oxut",
                ["home.cancelByCode"] = "Job kodu ilə ləğv et",

                ["notConfigured.title"] = "Kiosk quraşdırılır",
                ["notConfigured.body"] = "Printer hələ seçilməyib. Bir az sonra yenidən cəhd edin.",

                ["degraded.title"] = "Onlayn ödəniş müvəqqəti əlçatmazdır",
                ["degraded.body"] =
                    "Növbədəki ödənilmiş işlər çap olunmağa davam edir. Yeni sifariş bir azdan qəbul edilə biləcək.",
                ["offline.title"] = "İnternet bağlantısı yoxdur",
                ["offline.body"] = "Yeni sifariş qəbul edilmir. Bağlantı bərpa olunanda kiosk özü işə qayıdır.",

                ["queue.title"] = "Növbə",
                ["queue.empty"] = "Növbə boşdur",
                ["queue.guest"] = "Qonaq",
                ["queue.statusQueued"] = "Növbədə",
                ["queue.statusPrinting"] = "Çap olunur",
            }.ToFrozenDictionary(StringComparer.Ordinal),

            ["en"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app.starting"] = "Starting the kiosk…",
                ["app.hotline"] = "Help: 0${0}",

                ["activation.title"] = "This kiosk is not activated",
                ["activation.instruction"] =
                    "Enter this code in the admin panel, or scan the QR code with the operator app.",
                ["activation.codeExpires"] = "The code refreshes in {0}",
                ["activation.deviceInfo"] = "Device details",
                ["activation.hostname"] = "Name",
                ["activation.platform"] = "Platform",
                ["activation.version"] = "Version",
                ["activation.printers"] = "Detected printers",
                ["activation.noPrinters"] = "No printer detected",
                ["activation.connected"] = "Connected to the server",
                ["activation.disconnected"] = "The server is unreachable — retrying",
                ["activation.revoked"] = "The device was detached. Creating a new identity…",

                ["home.title"] = "Welcome",
                ["home.printFromUsb"] = "Print from a USB stick",
                ["home.scanMyQr"] = "Scan my QR code",
                ["home.cancelByCode"] = "Cancel with a job code",

                ["notConfigured.title"] = "The kiosk is being set up",
                ["notConfigured.body"] = "No printer has been selected yet. Please try again shortly.",

                ["degraded.title"] = "Online payment is temporarily unavailable",
                ["degraded.body"] =
                    "Paid jobs already in the queue are still printing. New orders can be placed again shortly.",
                ["offline.title"] = "No internet connection",
                ["offline.body"] = "New orders cannot be taken. The kiosk resumes on its own once the connection is back.",

                ["queue.title"] = "Queue",
                ["queue.empty"] = "The queue is empty",
                ["queue.guest"] = "Guest",
                ["queue.statusQueued"] = "Queued",
                ["queue.statusPrinting"] = "Printing",
            }.ToFrozenDictionary(StringComparer.Ordinal),

            ["ru"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app.starting"] = "Запуск киоска…",
                ["app.hotline"] = "Помощь: 0${0}",

                ["activation.title"] = "Этот киоск не активирован",
                ["activation.instruction"] =
                    "Введите этот код в панели администратора или отсканируйте QR-код приложением оператора.",
                ["activation.codeExpires"] = "Код обновится через {0}",
                ["activation.deviceInfo"] = "Сведения об устройстве",
                ["activation.hostname"] = "Имя",
                ["activation.platform"] = "Платформа",
                ["activation.version"] = "Версия",
                ["activation.printers"] = "Обнаруженные принтеры",
                ["activation.noPrinters"] = "Принтер не обнаружен",
                ["activation.connected"] = "Подключено к серверу",
                ["activation.disconnected"] = "Сервер недоступен — повторная попытка",
                ["activation.revoked"] = "Устройство отключено. Создаётся новая личность…",

                ["home.title"] = "Добро пожаловать",
                ["home.printFromUsb"] = "Печать с флешки",
                ["home.scanMyQr"] = "Сканировать мой QR-код",
                ["home.cancelByCode"] = "Отменить по коду задания",

                ["notConfigured.title"] = "Киоск настраивается",
                ["notConfigured.body"] = "Принтер ещё не выбран. Попробуйте чуть позже.",

                ["degraded.title"] = "Онлайн-оплата временно недоступна",
                ["degraded.body"] =
                    "Оплаченные задания в очереди продолжают печататься. Новые заказы можно будет оформить чуть позже.",
                ["offline.title"] = "Нет подключения к интернету",
                ["offline.body"] = "Новые заказы не принимаются. Киоск возобновит работу сам.",

                ["queue.title"] = "Очередь",
                ["queue.empty"] = "Очередь пуста",
                ["queue.guest"] = "Гость",
                ["queue.statusQueued"] = "В очереди",
                ["queue.statusPrinting"] = "Печатается",
            }.ToFrozenDictionary(StringComparer.Ordinal),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Looks a key up in one language.
    /// </summary>
    /// <remarks>
    /// Falls back to Azerbaijani, and then to the key itself. A missing
    /// translation shows the default language rather than an empty screen, and
    /// a missing key is visible in the UI rather than silently blank - which is
    /// how it gets noticed.
    /// </remarks>
    public static string Get(string language, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (Catalogue.TryGetValue(language ?? DefaultLanguage, out var table) &&
            table.TryGetValue(key, out var value))
        {
            return value;
        }

        if (Catalogue[DefaultLanguage].TryGetValue(key, out var fallback))
        {
            return fallback;
        }

        return key;
    }

    /// <summary>Looks a key up and formats it.</summary>
    public static string Get(string language, string key, params object?[] arguments)
        => string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(language, key), arguments);

    /// <summary>Every key in the default language, for the completeness test.</summary>
    public static IReadOnlyCollection<string> AllKeys => Catalogue[DefaultLanguage].Keys;

    /// <summary>Whether a language has a table at all.</summary>
    public static bool IsSupported(string? language)
        => language is not null && Catalogue.ContainsKey(language);
}
