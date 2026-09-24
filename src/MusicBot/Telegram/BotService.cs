using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicBot.Configuration;
using MusicBot.Soulseek;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
using ApiRequestException = Telegram.Bot.Exceptions.ApiRequestException;
using User = Telegram.Bot.Types.User;

namespace MusicBot.Telegram;

/// <summary>
/// Фоновый сервис: поднимает соединение с Soulseek и запускает long polling Telegram-бота.
/// </summary>
public sealed class BotService : BackgroundService
{
    private readonly BotConfig _config;
    private readonly SoulseekService _soulseek;
    private readonly UpdateHandler _handler;
    private readonly ILogger<BotService> _logger;

    public BotService(
        BotConfig config,
        SoulseekService soulseek,
        UpdateHandler handler,
        ILogger<BotService> logger)
    {
        _config = config;
        _soulseek = soulseek;
        _handler = handler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Сначала сеть Soulseek, затем Telegram.
        await _soulseek.StartAsync(stoppingToken);

        var bot = CreateClient();

        User me;
        try
        {
            me = await bot.GetMe(stoppingToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 401)
        {
            var hint = _config.Telegram.UseLocalApiServer
                ? "Если бот раньше работал через api.telegram.org, выполни: dotnet run -- logout"
                : "Проверь Telegram:Token в appsettings.json (токен от @BotFather)";
            throw new InvalidOperationException($"Telegram отклонил токен (401 Unauthorized). {hint}", ex);
        }
        _logger.LogInformation(
            "Telegram: бот @{Username} (id {Id}) запущен. API: {Api}",
            me.Username, me.Id,
            _config.Telegram.UseLocalApiServer ? "локальный сервер" : "api.telegram.org");

        // Показываем применённые настройки альбомов: по логам видно, какой режим реально работает.
        _logger.LogInformation(
            "Альбомы: отправка {Mode}, треков подряд {Parallel}, ждать пира до {Stall}с",
            _config.Albums.PackageAs, _config.Albums.ParallelTracks, _config.Downloads.StallTimeoutSeconds);

        if (_config.Telegram.AdminUserId == 0)
            _logger.LogWarning("AdminUserId = 0: бот отвечает ЛЮБОМУ пользователю. Выставь свой id командой /id.");

        bot.StartReceiving(
            updateHandler: _handler.HandleUpdateAsync,
            errorHandler: _handler.HandleErrorAsync,
            receiverOptions: new ReceiverOptions
            {
                AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery],
                DropPendingUpdates = true,
            },
            cancellationToken: stoppingToken);

        // Держим сервис живым до остановки хоста.
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    /// <summary>Создаёт клиента Telegram (официальный или локальный Bot API сервер).</summary>
    public ITelegramBotClient CreateClient()
    {
        var options = _config.Telegram.UseLocalApiServer
            ? new TelegramBotClientOptions(_config.Telegram.Token, _config.Telegram.LocalApiBaseUrl)
            : new TelegramBotClientOptions(_config.Telegram.Token);

        return new TelegramBotClient(options);
    }
}
