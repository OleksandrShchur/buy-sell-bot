using System.Globalization;
using System.Net;
using BuySellBot.Domain;

namespace BuySellBot.Texts;

public static class Texts
{
    public const string Buy = "🛒 Купити", Sell = "💰 Продати";
    public const string CreateListing = "🛒 Купити або продати";
    public static string Welcome(string? chatUrl)
    {
        const string community = "у спільноті";
        var communityLink = string.IsNullOrEmpty(chatUrl) ? community : $"<a href=\"{WebUtility.HtmlEncode(chatUrl)}\">{community}</a>";
        return $"Вітаємо! Тут можна купити чи продати речі {communityLink}. Натисніть «{Buy}» або «{Sell}», щоб створити оголошення 👇";
    }
    public const string Help = "Натисніть «🛒 Купити» або «💰 Продати» й заповніть оголошення. Фото та опис залишаються у картці, а запитання й кнопки — під нею. На останньому кроці оберіть контакт, який можна показати в оголошенні. Перед публікацією оголошення перевірить модератор.\n/start — головне меню та ваше оголошення\n/cancel — скасувати активне оголошення\n/help — допомога\nАдміністратори: /topicid у темі групи показує її ідентифікатор.\nДля перемикання між власним оголошенням і редагуванням модератора натисніть кнопку під карткою.";
    public const string StartCommand = "Головне меню", CancelCommand = "Скасувати оголошення", HelpCommand = "Допомога";
    public const string Back = "⬅️ Назад", Cancel = "❌ Скасувати", Skip = "⏭ Пропустити", Keep = "↩️ Залишити як є";
    public const string Yes = "✅ Так", No = "Ні", Restart = "Почати спочатку?", CancelQuestion = "Скасувати це оголошення?";
    public const string ReviewQuestion = "Все готово до надсилання?", ChangesQuestion = "Що ви хочете змінити?";
    public const string Submit = "✅ Так, надіслати", Change = "✏️ Ні, змінити", ChangeMore = "✏️ Змінити...";
    public const string Approve = "✅ Схвалити", Decline = "❌ Відхилити", Edit = "✏️ Редагувати", Publish = "✅ Опублікувати";
    public const string StopEdit = "↩️ Скасувати редагування", Resubmit = "✏️ Виправити та надіслати повторно";
    public const string Submitted = "✅ Надіслано на модерацію", Cancelled = "Оголошення скасовано.", EditCancelled = "Редагування скасовано. Картку модерації відновлено.";
    public const string Stale = "Ця кнопка вже неактуальна", NotAdmin = "Ця дія доступна лише адміністраторам.";
    public const string Error = "⚠️ Не вдалося виконати дію. Спробуйте ще раз.", InvalidInput = "Скористайтеся кнопками або надішліть значення для цього кроку.";
    public const string NoSession = "Активного оголошення немає. Натисніть «🛒 Купити» або «💰 Продати».";
    public const string ChooseIntent = "Оберіть «🛒 Купити» або «💰 Продати», щоб створити оголошення.";
    public const string PendingLimit = "Ви вже маєте максимальну кількість оголошень на модерації. Дочекайтеся рішення.";
    public const string PhotoLimit = "Можна додати до 10 фото. Зайві фото не збережено.";
    public const string PhotoPrompt = "Надішліть до 10 фото одним повідомленням (альбомом). Якщо фото менше 10, після завантаження натисніть «Готово». З 10 фото перейдемо далі автоматично. Нове повідомлення з фото замінить попередні фото.";
    public const string PhotoDone = "✅ Готово", PhotoExpected = "Надішліть до 10 фото одним повідомленням або натисніть «Пропустити».";
    public const string CategoryPrompt = "Оберіть категорію:", TitlePrompt = "Напишіть назву (3–60 символів):";
    public const string ConditionSellPrompt = "У якому стані річ?", ConditionBuyPrompt = "Який стан вас влаштує?";
    public const string PricePrompt = "Вкажіть ціну числом або оберіть варіант. Валюта перемикається кнопками.";
    public const string SharedPricePrompt = "Вкажіть ціну або бюджет числом чи діапазоном, наприклад 1000–2000, або оберіть варіант. Оберіть валюту.";
    public const string SharedConditionPrompt = "Оберіть поточний або бажаний стан речі:";
    public const string BudgetPrompt = "Вкажіть бюджет числом або діапазоном, наприклад 1000–2000. Оберіть валюту.";
    public const string LocationPrompt = "Вкажіть локацію (2–60 символів):", DescriptionPrompt = "Додайте опис (до 500 символів) або пропустіть:";
    public const string ContactPrompt = "Натисніть «Поділитися контактом», щоб додати в оголошення доступні контакти: @username та збережений номер телефону. Або введіть український номер телефону, наприклад 050 123 45 67 чи +380501234567.";
    public const string ManualContactPrompt = "Введіть український номер телефону для оголошення, наприклад 050 123 45 67 чи +380501234567.";
    public const string ShareContact = "📱 Поділитися контактом";
    public const string ContactConsentRequired = "Автор має обрати контакт, який можна показати в оголошенні.";
    public const string ContactError = "Введіть коректний український номер: 050 123 45 67 або +380501234567. Пробіли, дужки та дефіси дозволені. Через меню вкладень можна надіслати лише власний контакт.";
    public const string TitleError = "Назва має містити від 3 до 60 символів.", LocationError = "Локація має містити від 2 до 60 символів.";
    public const string DescriptionError = "Опис має містити не більше 500 символів.", PriceError = "Вкажіть додатне число до 999 999 999 з максимум двома десятковими знаками або діапазон від меншої до більшої суми.";
    public const string MissingFields = "Заповніть усі обов’язкові поля.", TooLong = "Картка задовга. Скоротіть опис.";
    public const string Busy = "Інший модератор уже працює з цим оголошенням.", ModeratorSessionBusy = "Спершу завершіть поточне редагування модератора.";
    public const string ChooseReason = "Оберіть причину відхилення або напишіть її повідомленням (1–500 символів):", Moderating = "✏️ Оголошення редагує модератор.";
    public const string DeclineReasonError = "Надішліть причину відхилення текстом від 1 до 500 символів або оберіть готовий варіант.";
    public const string DeclineReplyRequired = "Ви обираєте причину для кількох оголошень. Надішліть причину у відповідь на потрібну картку оголошення.";
    public const string Declining = "⏳ Модератор обирає причину відхилення.", Publishing = "⏳ Оголошення публікується.";
    public const string PublishedStatus = "✅ Опубліковано", DeclinedStatus = "❌ Відхилено", UncertainStatus = "⚠️ Потрібна перевірка публікації адміністратором. Перевірте тему та журнали сервера.";
    public const string OwnSession = "📝 Моє оголошення", ModeratorSession = "🛠 Редагування модератора", OpenPost = "Переглянути оголошення";
    public const string Negotiable = "🤝 Договірна", Free = "🎁 Безкоштовно", Exchange = "🔄 Обмін";
    public const string CategoryLabel = "📂 Категорія", TitleLabel = "📝 Назва", ConditionLabel = "🧩 Стан", PriceLabel = "💰 Ціна", BudgetLabel = "💰 Бюджет";
    public const string PhotosLabel = "🖼 Фото", LocationLabel = "📍 Локація", DescriptionLabel = "📄 Опис", ContactLabel = "📱 Контакт";
    public const string PriceField = "Ціна", BudgetField = "Бюджет", ConditionField = "Стан", DesiredConditionField = "Бажаний стан";
    public const string LocationField = "Локація", DescriptionField = "Опис", ContactField = "Контакт", AuthorField = "Автор", IntentField = "Тип";
    public const string BuyTag = "#купівля", SellTag = "#продаж", Empty = "—";
    public const string Selected = "✓ ";
    public const string ListingTag = "#оголошення", BuilderHeader = "📝 Створюємо оголошення";
    public const string BuilderStarted = "Заповнімо оголошення 👇";
    public const string PriceOrBudgetLabel = "💰 Ціна / бюджет", PriceOrBudgetField = "Ціна / бюджет";
    public const string PublicationChatMissing = "Не вдалося опублікувати: не налаштовано Bot:ChatId. Вкажіть ID супергрупи та перезапустіть бота. Оголошення залишається на модерації.";
    public static string PublicationTopicMissing(string category, int index) => $"Не вдалося опублікувати: для категорії «{category}» не налаштовано Categories:{index}:TopicId. Вкажіть ID теми та перезапустіть бота.";
    public static string PublicationValidationFailed(string reason) => $"Не вдалося опублікувати: {reason}";
    public static string PublicationTelegramRejected(int code) => code == 403
        ? "Telegram заборонив публікацію. Перевірте, що бот є адміністратором супергрупи та може надсилати повідомлення й фото в цю тему, і спробуйте ще раз."
        : "Telegram відхилив публікацію. Перевірте ID супергрупи й теми та налаштування оголошення, і спробуйте ще раз.";
    public static readonly string[] Conditions = ["🆕 Нове", "✨ Як нове", "👍 Добрий стан", "🔧 Потребує ремонту", "➖ Не важливо"];
    public static readonly string[] DeclineReasons = ["Порушує правила", "Дублікат", "Недостатньо інформації", "Без причини"];
    public static string Progress(int step, int total) => $"Крок {step} з {total}";
    public static string PhotoCount(int count) => $"Фото: {count}/10";
    public static string Warning(string warning) => $"⚠️ {warning}";
    public static string AuthorPublished(bool edited) => edited ? "✅ Оголошення відредагував модератор і опублікував." : "✅ Ваше оголошення опубліковано!";
    public static string AuthorDeclined(string reason) => $"❌ Оголошення відхилено. Причина: {reason}";
    public static string Topic(long chat, int? thread) => $"ID чату: <code>{chat}</code>\nID теми: <code>{thread?.ToString(CultureInfo.InvariantCulture) ?? Empty}</code>";
    public static string CurrencySymbol(Currency currency) => currency switch { Currency.USD => "$", Currency.EUR => "€", _ => "₴" };
    public static string Money(decimal amount) => amount.ToString("0.##", CultureInfo.InvariantCulture);
}
