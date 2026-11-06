using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using TL;

namespace TelegramChatExporter;

public static class TelegramHistory
{
    public static async Task<T> RequestAsync<T>(Func<Task<T>> request, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await request().WaitAsync(cancellationToken); }
            catch (RpcException ex) when (ex.Code == 420)
            {
                var match = Regex.Match(ex.Message, @"FLOOD_WAIT_(\d+)");
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out var seconds)) throw;
                Console.WriteLine($"Telegram ограничил частоту запросов. Ожидание {seconds} сек. (Ctrl+C — отмена)");
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
            }
        }
    }

    public static string DisplayName(IPeerInfo? peer) => peer switch
    {
        User user => string.IsNullOrWhiteSpace($"{user.first_name} {user.last_name}".Trim())
            ? user.MainUsername ?? $"User {user.id}"
            : $"{user.first_name} {user.last_name}".Trim(),
        ChatBase chat => chat.Title ?? $"Chat {chat.ID}",
        _ => "Неизвестный отправитель"
    };

    public static ExportMessage Map(Message message, User currentUser, Func<Peer, IPeerInfo?> resolve)
    {
        // Private outgoing messages can omit from_id; peer_id then points to the recipient.
        var senderPeer = message.from_id ??
            (message.peer_id is PeerUser && message.flags.HasFlag(Message.Flags.out_)
                ? new PeerUser { user_id = currentUser.id }
                : message.peer_id);
        var sender = senderPeer is PeerUser p && p.user_id == currentUser.id
            ? DisplayName(currentUser)
            : resolve(senderPeer) is { } info ? DisplayName(info) : senderPeer.ToString();
        if (message.from_id is null && message.peer_id is PeerChannel && !string.IsNullOrWhiteSpace(message.post_author))
            sender = $"{sender} ({message.post_author})";
        // Use the direct reply target, not reply_to_top_id (the thread root).
        // Missing targets remain references even when the original message is not exported.
        int? replyToMessageId = message.reply_to is MessageReplyHeader { reply_to_msg_id: > 0 } reply
            ? reply.reply_to_msg_id
            : null;
        return new ExportMessage(message.id, replyToMessageId, new DateTimeOffset(DateTime.SpecifyKind(message.date, DateTimeKind.Utc)),
            sender ?? "Неизвестный отправитель", message.message ?? "");
    }

    public static async IAsyncEnumerable<ExportMessage> ReadAsync(WTelegram.Client client,
        InputPeer peer, Messages_Dialogs dialogs, User currentUser,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var offsetId = 0;
        long count = 0;
        while (true)
        {
            var page = await RequestAsync(() => client.Messages_GetHistory(peer, offset_id: offsetId, limit: 100), cancellationToken);
            if (page.Messages.Length == 0) yield break;
            var nextOffset = page.Messages.Min(m => m.ID);
            if (nextOffset <= 0 || (offsetId != 0 && nextOffset >= offsetId))
                throw new InvalidOperationException("Telegram вернул страницу без продвижения по истории.");
            foreach (var message in page.Messages.OfType<Message>().OrderByDescending(m => m.id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (offsetId != 0 && message.id >= offsetId) continue;
                yield return Map(message, currentUser, p => page.UserOrChat(p) ?? dialogs.UserOrChat(p));
                count++;
            }
            Console.WriteLine($"Экспортировано сообщений: {count}");
            offsetId = nextOffset;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }
}
