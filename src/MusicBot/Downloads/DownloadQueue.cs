using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MusicBot.Configuration;
using MusicBot.Soulseek;

namespace MusicBot.Downloads;

/// <summary>Задача скачивания: один файл или целый альбом.</summary>
public sealed record DownloadJob
{
    /// <summary>Ключ очереди: по одному активному скачиванию на пользователя.</summary>
    public required string UserKey { get; init; }
    public required long ChatId { get; init; }
    public required int StatusMessageId { get; init; }
    public required CancellationTokenSource Cts { get; init; }
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Трек для скачивания (null, если качаем альбом).</summary>
    public MusicResult? Track { get; init; }

    /// <summary>Альбом для скачивания (null, если качаем трек).</summary>
    public AlbumResult? Album { get; init; }

    public bool IsAlbum => Album is not null;

    /// <summary>Человекочитаемое имя задачи для логов и /status.</summary>
    public string DisplayName => IsAlbum ? $"альбом «{Album!.Title}»" : Track!.Filename;
}

/// <summary>Реестр активных задач: ограничение параллелизма, отмена, статус.</summary>
public sealed class DownloadQueue
{
    private readonly SoulseekService _soulseek;
    private readonly ILogger<DownloadQueue> _logger;
    private readonly SemaphoreSlim _slots;

    private readonly ConcurrentDictionary<string, DownloadJob> _active = new();

    public DownloadQueue(SoulseekService soulseek, BotConfig config, ILogger<DownloadQueue> logger)
    {
        _soulseek = soulseek;
        _logger = logger;
        _slots = new SemaphoreSlim(Math.Max(1, config.Downloads.MaxConcurrent));
    }

    private int _waiting;

    public int ActiveCount => _active.Count;
    public int WaitingCount => _waiting;

    /// <summary>Выполняет задачу с треком: ждёт свободный слот и качает файл.</summary>
    /// <returns>Локальный путь скачанного файла.</returns>
    public async Task<string> RunAsync(
        DownloadJob job,
        Action<TransferProgress> progress,
        CancellationToken externalCt)
    {
        var track = job.Track ?? throw new InvalidOperationException("Задача без трека: используй RunAlbumAsync.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCt, job.Cts.Token);

        await AcquireSlotAsync(linked.Token);

        try
        {
            _active[job.UserKey] = job;
            linked.Token.ThrowIfCancellationRequested();

            return await _soulseek.DownloadAsync(track, progress, linked.Token);
        }
        finally
        {
            _active.TryRemove(job.UserKey, out _);
            _slots.Release();
        }
    }

    /// <summary>Выполняет задачу с альбомом: все треки подряд, слот занят до конца.</summary>
    public async Task<SoulseekService.AlbumDownload> RunAlbumAsync(
        DownloadJob job,
        Action<AlbumProgress> progress,
        Action<int, string>? onTrackReady,
        CancellationToken externalCt)
    {
        var album = job.Album ?? throw new InvalidOperationException("Задача без альбома: используй RunAsync.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCt, job.Cts.Token);

        await AcquireSlotAsync(linked.Token);

        try
        {
            _active[job.UserKey] = job;
            linked.Token.ThrowIfCancellationRequested();

            return await _soulseek.DownloadAlbumAsync(album, progress, onTrackReady, linked.Token);
        }
        finally
        {
            _active.TryRemove(job.UserKey, out _);
            _slots.Release();
        }
    }

    /// <summary>Ждёт свободный слот, учитывая количество ожидающих задач для /status.</summary>
    private async Task AcquireSlotAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            await _slots.WaitAsync(ct);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <summary>Отменяет активную задачу пользователя, если она есть.</summary>
    public bool TryCancel(string userKey)
    {
        if (_active.TryGetValue(userKey, out var job))
        {
            _logger.LogInformation("Отмена скачивания для {Key}", userKey);
            job.Cts.Cancel();
            return true;
        }

        return false;
    }

    /// <summary>Есть ли у пользователя активное скачивание.</summary>
    public bool IsActive(string userKey) => _active.ContainsKey(userKey);

    public DownloadJob? GetActive(string userKey) => _active.GetValueOrDefault(userKey);
}
