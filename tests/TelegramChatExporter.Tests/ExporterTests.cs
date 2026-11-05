using System.Text.Json;
using TL;
using Xunit;

namespace TelegramChatExporter.Tests;

public sealed class ExporterTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "tce-tests-" + Guid.NewGuid());

    [Fact]
    public async Task WritesOnlyRequestedFieldsAndPreservesUnicodeAndUtc()
    {
        var message = new ExportMessage(new DateTimeOffset(2026, 11, 5, 12, 30, 0, TimeSpan.Zero),
            "Иван", "Привет 👋\n\"текст\"");
        var result = await JsonExporter.ExportAsync(directory, "Чат:/вопрос?", Items(message));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.Path));
        var item = json.RootElement[0];
        Assert.Equal(new[] { "time", "sender", "text" }, item.EnumerateObject().Select(p => p.Name));
        Assert.Equal(message.Time, item.GetProperty("time").GetDateTimeOffset());
        Assert.Equal(message.Sender, item.GetProperty("sender").GetString());
        Assert.Equal(message.Text, item.GetProperty("text").GetString());
        Assert.Equal(1, result.Count);
        Assert.StartsWith("Чат__вопрос__", Path.GetFileName(result.Path));
    }

    [Fact]
    public async Task EmptyChatProducesEmptyJsonArray()
    {
        var result = await JsonExporter.ExportAsync(directory, "Empty", Items());
        Assert.Equal("[]", (await File.ReadAllTextAsync(result.Path)).Trim());
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public async Task FailedExportLeavesNoPartialOrFinalFile()
    {
        await Assert.ThrowsAsync<IOException>(() => JsonExporter.ExportAsync(directory, "Failed", Failing()));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task CancellationLeavesNoPartialOrFinalFile()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JsonExporter.ExportAsync(directory, "Cancel", Items(), cts.Token));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void OutgoingPrivateMessageUsesCurrentUserInsteadOfRecipient()
    {
        var me = new User { id = 1, first_name = "Я" };
        var other = new User { id = 2, first_name = "Собеседник" };
        var message = new Message { peer_id = new PeerUser { user_id = 2 }, flags = Message.Flags.out_,
            date = new DateTime(2026, 11, 5), message = "Привет" };
        Assert.Equal("Я", TelegramHistory.Map(message, me, _ => other).Sender);
        message.flags = 0;
        Assert.Equal("Собеседник", TelegramHistory.Map(message, me, _ => other).Sender);
    }

    [Fact]
    public void ChannelSignatureAndUnknownSenderArePreserved()
    {
        var message = new Message { peer_id = new PeerChannel { channel_id = 42 }, post_author = "Автор",
            date = new DateTime(2026, 11, 5), message = "Пост" };
        Assert.Contains("Автор", TelegramHistory.Map(message, new User(), _ => null).Sender);
        message.from_id = new PeerUser { user_id = 77 };
        Assert.Contains("77", TelegramHistory.Map(message, new User(), _ => null).Sender);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("...", "Chat")]
    [InlineData("a/b:c?", "a_b_c_")]
    public void SanitizesWindowsFileNames(string name, string expected) => Assert.Equal(expected, JsonExporter.SafeChatName(name));

    private static async IAsyncEnumerable<ExportMessage> Items(params ExportMessage[] messages)
    {
        await Task.CompletedTask;
        foreach (var message in messages) yield return message;
    }

    private static async IAsyncEnumerable<ExportMessage> Failing()
    {
        await Task.CompletedTask;
        yield return new ExportMessage(DateTimeOffset.UtcNow, "Sender", "Text");
        throw new IOException("Simulated network failure");
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
