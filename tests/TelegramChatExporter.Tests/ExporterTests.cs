using System.Text.Json;
using TL;
using NUnit.Framework;

namespace TelegramChatExporter.Tests;

[TestFixture]
public sealed class ExporterTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "tce-tests-" + Guid.NewGuid());

    [Test]
    public async Task WritesOnlyRequestedFieldsAndPreservesUnicodeAndUtc()
    {
        var message = new ExportMessage(125, 120, new DateTimeOffset(2026, 11, 5, 12, 30, 0, TimeSpan.Zero),
            "Иван", "Привет 👋\n\"текст\"");
        var result = await JsonExporter.ExportAsync(directory, "Чат:/вопрос?", Items(message));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.Path));
        var item = json.RootElement[0];
        Assert.That(item.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "id", "replyToMessageId", "time", "sender", "text" }));
        Assert.That(item.GetProperty("id").GetInt32(), Is.EqualTo(125));
        Assert.That(item.GetProperty("replyToMessageId").GetInt32(), Is.EqualTo(120));
        Assert.That(item.GetProperty("time").GetDateTimeOffset(), Is.EqualTo(message.Time));
        Assert.That(item.GetProperty("sender").GetString(), Is.EqualTo(message.Sender));
        Assert.That(item.GetProperty("text").GetString(), Is.EqualTo(message.Text));
        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(Path.GetFileName(result.Path), Does.StartWith("Чат__вопрос__"));
    }

    [Test]
    public async Task EmptyChatProducesEmptyJsonArray()
    {
        var result = await JsonExporter.ExportAsync(directory, "Empty", Items());
        Assert.That((await File.ReadAllTextAsync(result.Path)).Trim(), Is.EqualTo("[]"));
        Assert.That(result.Count, Is.Zero);
    }

    [Test]
    public async Task FailedExportLeavesNoPartialOrFinalFile()
    {
        await Assert.ThatAsync(() => JsonExporter.ExportAsync(directory, "Failed", Failing()), Throws.TypeOf<IOException>());
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [Test]
    public async Task CancellationLeavesNoPartialOrFinalFile()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThatAsync(() => JsonExporter.ExportAsync(directory, "Cancel", Items(), cts.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [Test]
    public void OutgoingPrivateMessageUsesCurrentUserInsteadOfRecipient()
    {
        var me = new User { id = 1, first_name = "Я" };
        var other = new User { id = 2, first_name = "Собеседник" };
        var message = new Message { peer_id = new PeerUser { user_id = 2 }, flags = Message.Flags.out_,
            date = new DateTime(2026, 11, 5), message = "Привет" };
        Assert.That(TelegramHistory.Map(message, me, _ => other).Sender, Is.EqualTo("Я"));
        message.flags = 0;
        Assert.That(TelegramHistory.Map(message, me, _ => other).Sender, Is.EqualTo("Собеседник"));
    }

    [Test]
    public void ChannelSignatureAndUnknownSenderArePreserved()
    {
        var message = new Message { peer_id = new PeerChannel { channel_id = 42 }, post_author = "Автор",
            date = new DateTime(2026, 11, 5), message = "Пост" };
        Assert.That(TelegramHistory.Map(message, new User(), _ => null).Sender, Does.Contain("Автор"));
        message.from_id = new PeerUser { user_id = 77 };
        Assert.That(TelegramHistory.Map(message, new User(), _ => null).Sender, Does.Contain("77"));
    }

    [TestCase("CON", "_CON")]
    [TestCase("...", "Chat")]
    [TestCase("a/b:c?", "a_b_c_")]
    public void SanitizesWindowsFileNames(string name, string expected) => Assert.That(JsonExporter.SafeChatName(name), Is.EqualTo(expected));


    [Test]
    public async Task ExportsReplyChainAndExplicitNullForOriginalMessage()
    {
        var original = new Message { id = 120, peer_id = new PeerUser { user_id = 2 },
            date = new DateTime(2026, 11, 6), message = "Original" };
        var reply = new Message { id = 125, peer_id = original.peer_id, date = original.date,
            message = "Reply", reply_to = new MessageReplyHeader { reply_to_msg_id = 120, reply_to_top_id = 100 } };
        var result = await JsonExporter.ExportAsync(directory, "Replies", Items(
            TelegramHistory.Map(reply, new User(), _ => null),
            TelegramHistory.Map(original, new User(), _ => null)));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.Path));
        Assert.That(json.RootElement[0].GetProperty("id").GetInt32(), Is.EqualTo(125));
        Assert.That(json.RootElement[0].GetProperty("replyToMessageId").GetInt32(),
            Is.EqualTo(json.RootElement[1].GetProperty("id").GetInt32()));
        Assert.That(json.RootElement[1].GetProperty("replyToMessageId").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public async Task PreservesReplyIdWhenTargetIsMissingFromExport()
    {
        var message = new Message { id = 125, peer_id = new PeerUser { user_id = 2 },
            date = new DateTime(2026, 11, 6), reply_to = new MessageReplyHeader { reply_to_msg_id = 120 } };
        var result = await JsonExporter.ExportAsync(directory, "Missing target",
            Items(TelegramHistory.Map(message, new User(), _ => null)));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.Path));
        Assert.That(json.RootElement.GetArrayLength(), Is.EqualTo(1));
        Assert.That(json.RootElement[0].GetProperty("replyToMessageId").GetInt32(), Is.EqualTo(120));
    }

    [Test]
    public void ThreadRootAloneDoesNotBecomeReplyTarget()
    {
        var message = new Message { id = 125, peer_id = new PeerUser { user_id = 2 },
            date = new DateTime(2026, 11, 6), reply_to = new MessageReplyHeader { reply_to_top_id = 100 } };
        Assert.That(TelegramHistory.Map(message, new User(), _ => null).ReplyToMessageId, Is.Null);
    }

    [Test]
    public void StoryReplyDoesNotBecomeMessageReply()
    {
        var message = new Message { id = 125, peer_id = new PeerUser { user_id = 2 },
            date = new DateTime(2026, 11, 6), reply_to = new MessageReplyStoryHeader
            { peer = new PeerUser { user_id = 2 }, story_id = 120 } };
        Assert.That(TelegramHistory.Map(message, new User(), _ => null).ReplyToMessageId, Is.Null);
    }

    private static async IAsyncEnumerable<ExportMessage> Items(params ExportMessage[] messages)
    {
        await Task.CompletedTask;
        foreach (var message in messages) yield return message;
    }

    private static async IAsyncEnumerable<ExportMessage> Failing()
    {
        await Task.CompletedTask;
        yield return new ExportMessage(1, null, DateTimeOffset.UtcNow, "Sender", "Text");
        throw new IOException("Simulated network failure");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
