using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicBot.Configuration;
using MusicBot.Downloads;
using MusicBot.Soulseek;
using MusicBot.Telegram;
using Serilog;
using Serilog.Events;
using Telegram.Bot;

// Режим `logout`: снимает бота с официального сервера Telegram,
// чтобы его можно было забрать на локальный Bot API сервер.
// Использование: dotnet run -- logout
if (args.Length > 0 && args[0].Equals("logout", StringComparison.OrdinalIgnoreCase))
{
    await RunLogoutAsync(args);
    return;
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    await RunBotAsync(args);
}
finally
{
    await Log.CloseAndFlushAsync();
}

static async Task RunBotAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Logging.ClearProviders();
    builder.Services.AddSerilog((services, configuration) => configuration
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"));

// ---- Конфигурация ----
var telegramSection = builder.Configuration.GetSection("Telegram");
var config = new BotConfig
{
    Telegram = new BotConfig.TelegramOptions
    {
        Token = telegramSection["Token"] ?? string.Empty,
        AdminUserId = long.TryParse(telegramSection["AdminUserId"], out var adminId) ? adminId : 0,
        LocalApiBaseUrl = telegramSection["LocalApiBaseUrl"] ?? string.Empty,
    },
    Soulseek = new BotConfig.SoulseekOptions
    {
        Username = builder.Configuration["Soulseek:Username"] ?? string.Empty,
        Password = builder.Configuration["Soulseek:Password"] ?? string.Empty,
        ListenPort = builder.Configuration.GetValue("Soulseek:ListenPort", 50000),
        DownloadDirectory = builder.Configuration["Soulseek:DownloadDirectory"] ?? "downloads",
        ShareDownloads = builder.Configuration.GetValue("Soulseek:ShareDownloads", true),
        ShareDescription = builder.Configuration["Soulseek:ShareDescription"] ?? "Music sharing via Telegram bot",
    },
    Search = new BotConfig.SearchOptions
    {
        TimeoutSeconds = builder.Configuration.GetValue("Search:TimeoutSeconds", 10),
        ResponseLimit = builder.Configuration.GetValue("Search:ResponseLimit", 100),
        MaxResultsShown = builder.Configuration.GetValue("Search:MaxResultsShown", 8),
    },
    Downloads = new BotConfig.DownloadOptions
    {
        MaxConcurrent = builder.Configuration.GetValue("Downloads:MaxConcurrent", 2),
        MaxUploadSlots = builder.Configuration.GetValue("Downloads:MaxUploadSlots", 2),
        KeepDownloadedFiles = builder.Configuration.GetValue("Downloads:KeepDownloadedFiles", true),
        StallTimeoutSeconds = builder.Configuration.GetValue("Downloads:StallTimeoutSeconds", 20),
        MaxDiskUsageGb = builder.Configuration.GetValue("Downloads:MaxDiskUsageGb", 10),
        MinFilesToKeep = builder.Configuration.GetValue("Downloads:MinFilesToKeep", 10),
        CleanupIntervalMinutes = builder.Configuration.GetValue("Downloads:CleanupIntervalMinutes", 10),
        MinFileAgeMinutes = builder.Configuration.GetValue("Downloads:MinFileAgeMinutes", 15),
    },
    Albums = new BotConfig.AlbumOptions
    {
        MinFilesForAlbum = builder.Configuration.GetValue("Albums:MinFilesForAlbum", 3),
        MaxAlbumsShown = builder.Configuration.GetValue("Albums:MaxAlbumsShown", 5),
        MaxAlbumsPerPeer = builder.Configuration.GetValue("Albums:MaxAlbumsPerPeer", 2),
        MaxTracksPerAlbum = builder.Configuration.GetValue("Albums:MaxTracksPerAlbum", 60),
        ParallelTracks = builder.Configuration.GetValue("Albums:ParallelTracks", 1),
        PackageAs = builder.Configuration["Albums:PackageAs"] ?? "individual",
        MaxZipSizeMb = builder.Configuration.GetValue("Albums:MaxZipSizeMb", 1800),
    },
};

Validate(config);
Directory.CreateDirectory(config.Soulseek.DownloadDirectory);
EnsureSoulseekPortAvailable(config.Soulseek.ListenPort);

// ---- DI ----
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<ShareService>();
builder.Services.AddSingleton<SoulseekService>();
builder.Services.AddSingleton<DownloadQueue>();
builder.Services.AddSingleton<StorageCleaner>();
builder.Services.AddSingleton<UpdateHandler>();
builder.Services.AddHostedService<BotService>();
builder.Services.AddHostedService<StorageCleanupService>();

var host = builder.Build();
    await host.RunAsync();
}

// ---- Функции ----

/// <summary>
/// Проверяет, что порт слушателя Soulseek свободен: иначе пользователь получит
/// невнятный ListenException вместо нормального подсказки «бот уже запущен».
/// </summary>
static void EnsureSoulseekPortAvailable(int port)
{
    try
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
        listener.Start();
        listener.Stop();
    }
    catch (System.Net.Sockets.SocketException)
    {
        throw new InvalidOperationException(
            $"Порт {port} занят — скорее всего, бот уже запущен в другом окне. " +
            "Закрой второй экземпляр (Ctrl+C) или смени Soulseek:ListenPort в appsettings.json.");
    }
}

static void Validate(BotConfig config)
{
    var errors = new List<string>();

    if (string.IsNullOrWhiteSpace(config.Telegram.Token))
        errors.Add("Telegram:Token пуст — укажи токен от @BotFather");

    if (string.IsNullOrWhiteSpace(config.Soulseek.Username))
        errors.Add("Soulseek:Username пуст — зарегистрируйся на slsknet.org");

    if (string.IsNullOrWhiteSpace(config.Soulseek.Password))
        errors.Add("Soulseek:Password пуст");

    if (config.Soulseek.ListenPort is < 1024 or > 65535)
        errors.Add("Soulseek:ListenPort должен быть 1024..65535");

    if (errors.Count > 0)
        throw new InvalidOperationException(
            "Ошибки конфигурации:\n  - " + string.Join("\n  - ", errors));
}

static async Task RunLogoutAsync(string[] args)
{
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    var token = configuration["Telegram:Token"] ?? args.ElementAtOrDefault(1);
    if (string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("Токен не найден. Задай Telegram:Token в appsettings.json или вторым аргументом: dotnet run -- logout <token>");
        Environment.Exit(1);
    }

    Console.WriteLine("Снимаем бота с официального сервера Telegram (нужно перед переходом на локальный Bot API)…");
    var client = new TelegramBotClient(token);

    try
    {
        await client.LogOut();
        Console.WriteLine("Готово. Теперь бот свободен для локального сервера (Telegram:LocalApiBaseUrl).");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Не удалось выполнить logOut: {ex.Message}");
        Environment.Exit(1);
    }
}
