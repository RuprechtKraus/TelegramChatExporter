using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TelegramChatExporter;

public sealed record ExportMessage(
    [property: JsonPropertyName("time")] DateTimeOffset Time,
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("text")] string Text);

public static class JsonExporter
{
    public static string SafeChatName(string name)
    {
        const string windowsInvalid = "<>:\"/\\|?*";
        var cleaned = new string(name.Select(c => char.IsControl(c) || windowsInvalid.Contains(c)
            || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        // Limit by Unicode scalar values, preserving surrogate pairs such as emoji.
        cleaned = string.Concat(cleaned.EnumerateRunes().Take(80).Select(r => r.ToString())).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Chat";
        var stem = cleaned.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            cleaned = "_" + cleaned;
        return cleaned;
    }

    public static async Task<(string Path, long Count)> ExportAsync(
        string directory, string chatName, IAsyncEnumerable<ExportMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Export:Directory не может быть пустой.", nameof(directory));
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss-fffffff", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"{SafeChatName(chatName)}_{stamp}.json");
        var temporaryPath = path + ".partial";
        long count = 0;
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, useAsync: true))
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                {
                    Indented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                writer.WriteStartArray();
                await foreach (var message in messages.WithCancellation(cancellationToken))
                {
                    JsonSerializer.Serialize(writer, message);
                    count++;
                    // Flush incrementally: memory use does not grow with chat history.
                    if (count % 100 == 0) await writer.FlushAsync(cancellationToken);
                }
                writer.WriteEndArray();
                await writer.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path); // Never overwrite an existing export.
            return (path, count);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }
}
