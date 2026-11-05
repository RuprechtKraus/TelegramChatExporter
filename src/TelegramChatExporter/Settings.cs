using Microsoft.Extensions.Configuration;

namespace TelegramChatExporter;

public sealed class TelegramSettings
{
    public int ApiId { get; set; }
    public string ApiHash { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public string SessionPath { get; set; } = "";

    public void Validate()
    {
        if (ApiId <= 0 || ApiHash.Length != 32 || !ApiHash.All(Uri.IsHexDigit))
            throw new InvalidOperationException(
                "Задайте Telegram:ApiId и Telegram:ApiHash (32 hex-символа) через User Secrets или appsettings.json. См. README.");
    }

    public string ResolveSessionPath() => Path.GetFullPath(string.IsNullOrWhiteSpace(SessionPath)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TelegramChatExporter", "telegram.session")
        : SessionPath);
}

public sealed class ExportSettings
{
    public string Directory { get; set; } = "exports";
}

public static class Settings
{
    // User Secrets are loaded in every environment, including Release console runs.
    public static IConfigurationRoot Load() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile("appsettings.Local.json", optional: true)
        .AddUserSecrets<TelegramSettings>(optional: true)
        .AddEnvironmentVariables(prefix: "TCE_")
        .Build();
}
