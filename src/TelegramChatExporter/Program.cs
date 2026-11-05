using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using TelegramChatExporter;
using TL;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var configuration = Settings.Load();
    var telegram = configuration.GetSection("Telegram").Get<TelegramSettings>() ?? new();
    var export = configuration.GetSection("Export").Get<ExportSettings>() ?? new();
    telegram.Validate();
    if (string.IsNullOrWhiteSpace(export.Directory))
        throw new InvalidOperationException("Задайте Export:Directory.");
    var sessionPath = telegram.ResolveSessionPath();
    Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
    // Do not log MTProto internals or authentication data to the console.
    WTelegram.Helpers.Log = (_, _) => { };
    string? Config(string key) => key switch
    {
        "api_id" => telegram.ApiId.ToString(CultureInfo.InvariantCulture),
        "api_hash" => telegram.ApiHash,
        "phone_number" => string.IsNullOrWhiteSpace(telegram.PhoneNumber)
            ? Prompt("Номер телефона (с кодом страны): ") : telegram.PhoneNumber,
        "verification_code" => Prompt("Код входа из Telegram/SMS: ", hidden: true),
        "password" => Prompt("Пароль двухфакторной авторизации: ", hidden: true),
        "email" => Prompt("Email для авторизации: "),
        "email_verification_code" => Prompt("Код из email: ", hidden: true),
        "session_pathname" => sessionPath,
        _ => null
    };
    await using var client = new WTelegram.Client(Config) { FloodRetryThreshold = 0 };
    Console.WriteLine("Вход в Telegram...");
    var me = await TelegramHistory.RequestAsync(() => client.LoginUserIfNeeded(), cancellation.Token);
    Console.WriteLine($"Выполнен вход: {TelegramHistory.DisplayName(me)}");
    var dialogs = await TelegramHistory.RequestAsync(() => client.Messages_GetAllDialogs(), cancellation.Token);
    var chats = dialogs.dialogs.Select(d => dialogs.UserOrChat(d))
        .OfType<IPeerInfo>().Where(p => p.IsActive)
        .OrderBy(TelegramHistory.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    if (chats.Length == 0)
    {
        Console.WriteLine("Доступных чатов нет.");
        return 0;
    }
    Console.WriteLine("\nВыберите чат:");
    for (var i = 0; i < chats.Length; i++)
    {
        var kind = chats[i] is User ? "личный" : chats[i] is Channel ? "канал/супергруппа" : "группа";
        Console.WriteLine($"{i + 1,4}. {TelegramHistory.DisplayName(chats[i])} [{kind}, {chats[i].ID}]");
    }
    int selected;
    while (true)
    {
        var input = Prompt("Номер чата (0 — выход): ");
        if (input == "0") return 0;
        if (int.TryParse(input, out selected) && selected >= 1 && selected <= chats.Length) break;
        Console.WriteLine("Введите номер из списка.");
    }
    var chat = chats[selected - 1];
    Console.WriteLine($"Экспорт: {TelegramHistory.DisplayName(chat)}. Ctrl+C — отмена.");
    var result = await JsonExporter.ExportAsync(export.Directory, TelegramHistory.DisplayName(chat),
        TelegramHistory.ReadAsync(client, chat.ToInputPeer(), dialogs, me, cancellation.Token), cancellation.Token);
    Console.WriteLine($"Готово: {result.Count} сообщений\n{result.Path}");
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Операция отменена. Незавершённый экспорт удалён.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Ошибка: {ex.Message}");
    return 1;
}

string Prompt(string label, bool hidden = false)
{
    cancellation.Token.ThrowIfCancellationRequested();
    Console.Write(label);
    if (Console.IsInputRedirected)
        return Console.ReadLine()?.Trim() ?? throw new OperationCanceledException();
    var value = new StringBuilder();
    while (true)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        if (!Console.KeyAvailable)
        {
            Thread.Sleep(50);
            continue;
        }
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
        if (key.Key == ConsoleKey.Backspace && value.Length > 0)
        {
            value.Length--;
            if (!hidden) Console.Write("\b \b");
        }
        else if (!char.IsControl(key.KeyChar))
        {
            value.Append(key.KeyChar);
            if (!hidden) Console.Write(key.KeyChar);
        }
    }
}
