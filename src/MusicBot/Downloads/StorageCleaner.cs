using Microsoft.Extensions.Logging;
using MusicBot.Configuration;

namespace MusicBot.Downloads;

/// <summary>Результат уборки папки загрузок.</summary>
/// <param name="TotalBytes">Сколько занимала папка до уборки.</param>
/// <param name="FreedBytes">Сколько освободили.</param>
/// <param name="DeletedFiles">Сколько файлов удалили.</param>
/// <param name="RemainingFiles">Сколько файлов осталось.</param>
/// <param name="WasRun">Уборка вообще выполнялась (или объём был в норме).</param>
public sealed record CleanupResult(
    long TotalBytes,
    long FreedBytes,
    int DeletedFiles,
    int RemainingFiles,
    bool WasRun);

/// <summary>
/// Следит за папкой загрузок и удаляет самые старые файлы, когда превышен лимит.
///
/// Важный нюанс: совсем пустая папка — это плохо. Сеть Soulseek считает бота без шары
/// «леечером»: режет скорость и игнорирует в очередях. Поэтому оставляем
/// <see cref="BotConfig.DownloadOptions.MinFilesToKeep"/> свежих файлов.
/// </summary>
public sealed class StorageCleaner
{
    private readonly BotConfig _config;
    private readonly ILogger<StorageCleaner> _logger;
    private DateTime _lastRunUtc = DateTime.MinValue;

    public StorageCleaner(BotConfig config, ILogger<StorageCleaner> logger)
    {
        _config = config;
        _logger = logger;
    }

    public string DownloadDirectory => _config.Soulseek.DownloadDirectory;

    /// <summary>Текущий объём папки загрузок в байтах.</summary>
    public long GetUsedBytes()
    {
        try
        {
            if (!System.IO.Directory.Exists(DownloadDirectory))
                return 0;

            return new DirectoryInfo(DownloadDirectory)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f =>
                {
                    try
                    {
                        return f.Length;
                    }
                    catch
                    {
                        return 0L;
                    }
                });
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Убирает старые файлы, если объём превышает лимит.</summary>
    /// <param name="force">Проверить даже если недавно запускали (команда /cleanup).</param>
    public CleanupResult Cleanup(bool force = false)
    {
        var dir = DownloadDirectory;
        var limitBytes = (long)_config.Downloads.MaxDiskUsageGb * 1024 * 1024 * 1024;

        if (!System.IO.Directory.Exists(dir))
            return new CleanupResult(0, 0, 0, 0, false);

        if (limitBytes <= 0)
            return new CleanupResult(GetUsedBytes(), 0, 0, 0, false);

        // Не мешаем часто: после альбома проверяем каждый раз, но не чаще раза в минуту.
        if (!force && (DateTime.UtcNow - _lastRunUtc).TotalMinutes < 1)
            return new CleanupResult(GetUsedBytes(), 0, 0, 0, false);

        _lastRunUtc = DateTime.UtcNow;

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать папку загрузок при уборке");
            return new CleanupResult(0, 0, 0, 0, false);
        }

        var total = files.Sum(f => f.Length);
        var minKeep = Math.Max(0, _config.Downloads.MinFilesToKeep);
        var minAge = TimeSpan.FromMinutes(Math.Max(0, _config.Downloads.MinFileAgeMinutes));

        if (total <= limitBytes)
            return new CleanupResult(total, 0, 0, files.Count, false);

        _logger.LogInformation(
            "Уборка: папка загрузок {Size}, лимит {Limit} — удаляю самые старые файлы",
            FormatBytes(total), FormatBytes(limitBytes));

        long freed = 0;
        var deleted = 0;

        for (var i = 0; i < files.Count; i++)
        {
            // Стоп, если уже уложились в лимит.
            if (total - freed <= limitBytes)
                break;

            // Последние minKeep файлов не трогаем — шара должна остаться непустой.
            if (files.Count - i <= minKeep)
                break;

            var file = files[i];

            // Свежие файлы могут ещё отправляться в Telegram.
            if (DateTime.UtcNow - file.LastWriteTimeUtc < minAge)
                continue;

            try
            {
                var size = file.Length;
                file.Delete();
                freed += size;
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Не удалось удалить {File}", file.FullName);
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Уборка: удалено {Count} файлов, освобождено {Freed} (осталось {Files})",
                deleted, FormatBytes(freed), files.Count - deleted);
        }

        return new CleanupResult(total, freed, deleted, files.Count - deleted, true);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F1} ГБ",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:F0} МБ",
        _ => $"{bytes / 1024:F0} КБ",
    };
}
