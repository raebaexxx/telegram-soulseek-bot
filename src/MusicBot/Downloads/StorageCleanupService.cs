using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicBot.Configuration;
using MusicBot.Downloads;
using MusicBot.Soulseek;

namespace MusicBot.Downloads;

/// <summary>
/// Периодически проверяет папку загрузок и подрезает её, если превышен лимит.
/// Отдельный фоновый цикл нужен, чтобы уборка шла даже без активных загрузок
/// (например, после перезапуска или если бот долго простаивал).
/// </summary>
public sealed class StorageCleanupService(
    BotConfig config,
    StorageCleaner cleaner,
    SoulseekService soulseek,
    ILogger<StorageCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, config.Downloads.CleanupIntervalMinutes));

        // Первую проверку делаем не сразу, чтобы дать боту подняться.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = cleaner.Cleanup();

                if (result.DeletedFiles > 0)
                {
                    // Сообщаем сети об изменившемся шаре: иначе она продолжит
                    // считать нас файлом с прежним количеством.
                    await soulseek.AnnounceShareAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка фоновой уборки папки загрузок");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
