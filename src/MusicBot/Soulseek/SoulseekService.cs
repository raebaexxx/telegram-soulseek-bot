using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MusicBot.Configuration;
using Soulseek;
using SlFile = Soulseek.File;
using DiagnosticLevel = Soulseek.Diagnostics.DiagnosticLevel;
using SoulseekClientStates = Soulseek.SoulseekClientStates;

namespace MusicBot.Soulseek;

/// <summary>
/// Обёртка над <see cref="SoulseekClient"/>: соединение с автопереподключением,
/// поиск с агрегацией результатов и скачивание файлов.
/// </summary>
public sealed class SoulseekService : IAsyncDisposable
{
    /// <summary>
    /// Уникальный minor version, который бот сообщает серверу Soulseek при входе.
    /// Требование лицензии Soulseek.NET: у каждого приложения свой диапазон.
    /// Диапазон 760-7699999 занят slskd — не пересекаемся с ним.
    /// </summary>
    private const int MinorVersion = 1_700_001;

    /// <summary>Сколько пиров помним на один файл (для переключения при зависании).</summary>
    private const int MaxFileSourcesPerFile = 4;

    /// <summary>Источник файла: сам файл у пира + ключ его альбомной папки.</summary>
    private sealed record FileSource(MusicResult Result, string DirectoryKey);

    /// <summary>Индекс «файл → пиры» по последнему поиску (с папкой, чтобы искать альбомные копии).</summary>
    private readonly ConcurrentDictionary<string, List<FileSource>> FileSources = new();

    private readonly BotConfig _config;
    private readonly ILogger<SoulseekService> _logger;
    private readonly ShareService _shares;
    private readonly SoulseekClient _client;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private volatile bool _disposed;
    private volatile bool _shuttingDown;
    private volatile bool _kicked;
    private Task _reconnectTask = Task.CompletedTask;

    public SoulseekService(BotConfig config, ShareService shares, ILogger<SoulseekService> logger)
    {
        _config = config;
        _shares = shares;
        _logger = logger;
        _client = new SoulseekClient(MinorVersion, new SoulseekClientOptions(
            listenPort: config.Soulseek.ListenPort,
            maximumConcurrentSearches: 4,
            minimumDiagnosticLevel: DiagnosticLevel.Info,
            searchResponseResolver: shares.SearchResponseResolver,
            userInfoResolver: shares.UserInfoResolver,
            browseResponseResolver: shares.BrowseResponseResolver,
            placeInQueueResolver: null,
            raiseEventsAsynchronously: false));

        // Ответы на чужие поиски/просмотры идут из нашей шары.
        shares.Attach(_client);

        _client.LoggedIn += (_, _) =>
        {
            _kicked = false;
            _logger.LogInformation("Soulseek: вход выполнен как {User}", config.Soulseek.Username);
        };

        _client.KickedFromServer += (_, _) =>
        {
            _kicked = true;
            _logger.LogError(
                "Soulseek: сервер выкинул аккаунт {User}. Обычно это слишком частые входы или бан. " +
                "Бот не будет переподключаться сам — подожди и запусти его снова, " +
                "а лучше используй свой аккаунт вместо тестового.",
                config.Soulseek.Username);
        };
        _client.Disconnected += (_, e) => OnDisconnected(e);
        _client.DiagnosticGenerated += (_, e) =>
        {
            if (e.Level >= DiagnosticLevel.Warning)
                _logger.Log(e.Level switch
                {
                    DiagnosticLevel.Warning => LogLevel.Warning,
                    DiagnosticLevel.Debug or DiagnosticLevel.Trace => LogLevel.Debug,
                    _ => LogLevel.Information,
                }, "Soulseek: {Message}", e.Message);
        };
    }

    /// <summary>Соединён ли клиент с сервером.</summary>
    public bool IsConnected => _client.State.HasFlag(SoulseekClientStates.Connected);

    /// <summary>Устанавливает соединение и логинится; при неудаче продолжает попытки в фоне.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await ConnectWithRetryAsync(ct);

        // Публикуем количество расшаренных файлов на сервере.
        var (dirs, files) = _shares.GetShareStats();
        await _client.SetSharedCountsAsync(dirs, files);
        _logger.LogInformation("Soulseek: объявлено {Dirs} папок / {Files} файлов в шаре", dirs, files);
    }

    private void OnDisconnected(SoulseekClientDisconnectedEventArgs e)
    {
        _logger.LogWarning("Soulseek: разъединено ({Message})", e.Message ?? "unknown");

        if (_shuttingDown || _disposed || _kicked)
            return;

        // Автопереподключение с задержкой.
        _reconnectTask = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ConnectWithRetryAsync(cts.Token);
        });
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            if (IsConnected && _client.State.HasFlag(SoulseekClientStates.LoggedIn))
                return;

            var attempt = 0;
            while (!ct.IsCancellationRequested)
            {
                attempt++;
                try
                {
                    _logger.LogInformation("Soulseek: подключение к серверу (попытка {Attempt})…", attempt);
                    await _client.ConnectAsync(_config.Soulseek.Username, _config.Soulseek.Password, ct);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Порт слушателя занят — обычно это второй экземпляр бота.
                    // Повтор не поможет, поэтому падаем сразу с понятным сообщением.
                    if (ex is ListenException)
                    {
                        throw new InvalidOperationException(
                            $"Не удалось занять порт {_config.Soulseek.ListenPort} для входящих соединений Soulseek. " +
                            "Почти всегда это значит, что бот уже запущен в другом окне. " +
                            "Закрой второй экземпляр или смени Soulseek:ListenPort в appsettings.json.", ex);
                    }

                    // Неверный логин/пароль — повтор тоже не поможет.
                    if (ex is LoginRejectedException)
                    {
                        throw new InvalidOperationException(
                            "Сервер Soulseek отклонил логин или пароль. Проверь Soulseek:Username и Soulseek:Password " +
                            "(регистрация: https://www.slsknet.org/register).", ex);
                    }

                    _logger.LogError(ex, "Soulseek: неудачная попытка подключения #{Attempt}", attempt);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * attempt)), ct);
                }
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>Результат поиска: отдельные треки и альбомы (папки пиров).</summary>
    public sealed record SearchResults(List<MusicResult> Tracks, List<AlbumResult> Albums, int ResponseCount)
    {
        public bool IsEmpty => Tracks.Count == 0 && Albums.Count == 0;
    }

    /// <summary>
    /// Ищет по сети и возвращает отсортированный топ: и альбомы, и отдельные треки.
    /// Метод ждёт <see cref="BotConfig.SearchOptions.TimeoutSeconds"/> секунд.
    /// </summary>
    public async Task<SearchResults> SearchAsync(string query, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);

        var responses = new ConcurrentBag<SearchResponse>();

        var options = new SearchOptions(
            searchTimeout: _config.Search.TimeoutSeconds * 1000,
            responseLimit: _config.Search.ResponseLimit,
            fileLimit: 5000,
            filterResponses: true,
            minimumResponseFileCount: 1,
            removeSingleCharacterSearchTerms: true,
            responseReceived: response => responses.Add(response.Response));

        var (search, _) = await _client.SearchAsync(
            SearchQuery.FromText(query), options: options, cancellationToken: ct);

        var all = responses.ToList();

        var tracks = ResultScorer.Aggregate(all, _config.Search.MaxResultsShown);
        var albums = ResultScorer.AggregateAlbums(
            all,
            query,
            _config.Albums.MinFilesForAlbum,
            _config.Albums.MaxAlbumsShown,
            _config.Albums.MaxAlbumsPerPeer,
            _config.Albums.MaxTracksPerAlbum);

        // Карта «файл → пиры, у которых он есть»: пригодится, если внутри альбома
        // конкретный трек не отдадут — найдём другого пира с тем же файлом.
        IndexFileSources(all);

        // Если альбомы нашлись, отдельных треков нужно меньше — чтобы не раздувать список.
        if (albums.Count > 0)
            tracks = tracks.Take(Math.Max(3, _config.Search.MaxResultsShown / 2)).ToList();

        _logger.LogInformation(
            "Поиск «{Query}»: {Responses} ответов, альбомов {Albums}, треков {Tracks}",
            query, search.ResponseCount, albums.Count, tracks.Count);

        return new SearchResults(tracks, albums, search.ResponseCount);
    }

    /// <summary>Пересчитывает шару и сообщает серверу Soulseek актуальные цифры.</summary>
    public async Task AnnounceShareAsync(CancellationToken ct)
    {
        if (!IsConnected)
            return;

        _shares.InvalidateCache();
        var (dirs, files) = _shares.GetShareStats();
        await _client.SetSharedCountsAsync(dirs, files, cancellationToken: ct);
        _logger.LogInformation("Шара обновлена: {Dirs} папок / {Files} файлов", dirs, files);
    }

    /// <summary>
    /// Скачивает файл. Если выбранный пир не отвечает или отваливается —
    /// автоматически пробует запасных пиров с тем же треком.
    /// </summary>
    public Task<string> DownloadAsync(
        MusicResult result,
        Action<TransferProgress> progress,
        CancellationToken ct) =>
        DownloadWithFailoverAsync(result, progress, result.Alternatives, ct);

    /// <summary>Скачивает файл с переключением на запасные источники при зависании.</summary>
    public Task<string> DownloadWithFailoverAsync(
        MusicResult result,
        Action<TransferProgress> progress,
        IReadOnlyList<MusicResult> fallbacks,
        CancellationToken ct) =>
        DownloadWithFailoverAsync(result, progress, fallbacks, ct, null);

    /// <param name="stallOverride">Свой таймаут простоя (например, короткий для обложек).</param>
    public async Task<string> DownloadWithFailoverAsync(
        MusicResult result,
        Action<TransferProgress> progress,
        IReadOnlyList<MusicResult> fallbacks,
        CancellationToken ct,
        int? stallOverride)
    {
        await EnsureConnectedAsync(ct);

        var candidates = new List<MusicResult> { result };
        candidates.AddRange(fallbacks);

        var failures = new List<string>();

        // Если запасных пиров нет, есть смысл подождать подольше: торопиться некуда.
        var stallSeconds = stallOverride ?? Math.Max(5, _config.Downloads.StallTimeoutSeconds);
        if (stallOverride is null && fallbacks.Count == 0)
            stallSeconds = Math.Min(90, (int)(stallSeconds * 1.5));

        for (var attempt = 0; attempt < candidates.Count; attempt++)
        {
            var candidate = candidates[attempt];

            if (attempt > 0)
            {
                _logger.LogInformation(
                    "Переключаюсь на запасного пира {User} ({Attempt}/{Total}) для {File}",
                    candidate.Username, attempt + 1, candidates.Count, candidate.Filename);
                progress(new TransferProgress(0, 0, 0, candidate.Size, null));
            }

            try
            {
                return await DownloadFromPeerAsync(candidate, progress, stallSeconds, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{candidate.Username}: {ex.Message}");
                _logger.LogWarning(ex, "Не удалось скачать {File} от {User}", candidate.RemoteFilename, candidate.Username);
            }
        }

        var reason = failures.Count == 1
            ? failures[0]
            : $"не помог ни один из {failures.Count} пиров";

        throw new DownloadException($"Не удалось скачать «{result.Filename}». {reason}");
    }

    /// <summary>Одна попытка скачивания у конкретного пира со сторожем зависания.</summary>
    private async Task<string> DownloadFromPeerAsync(
        MusicResult result,
        Action<TransferProgress> progress,
        int stallSeconds,
        CancellationToken ct)
    {
        var localPath = BuildLocalPath(result);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        var lastBytes = -1L;
        var lastActivityUtc = DateTime.UtcNow;

        var options = new TransferOptions(
            progressUpdated: update =>
            {
                var transfer = update.Transfer;

                if (transfer.BytesTransferred != lastBytes)
                {
                    lastBytes = transfer.BytesTransferred;
                    lastActivityUtc = DateTime.UtcNow;
                }

                progress(new TransferProgress(
                    transfer.PercentComplete,
                    transfer.AverageSpeed,
                    transfer.BytesTransferred,
                    transfer.Size,
                    transfer.RemainingTime));
            },
            maximumLingerTime: 3000);

        // Сторож: если за stallSeconds не пришло ни байта — считаем пира мёртвым.
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watchdog = new CancellationTokenSource();

        var watch = Task.Run(async () =>
        {
            while (!watchdog.IsCancellationRequested && !attemptCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), watchdog.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var idle = DateTime.UtcNow - lastActivityUtc;
                if (idle >= TimeSpan.FromSeconds(stallSeconds))
                {
                    _logger.LogWarning(
                        "Пир {User} не передаёт данные {Idle:F0}с ({File}) — переключаюсь",
                        result.Username, idle.TotalSeconds, result.Filename);
                    attemptCts.Cancel();
                    return;
                }
            }
        }, CancellationToken.None);

        Transfer transfer;
        var started = DateTime.UtcNow;

        try
        {
            transfer = await _client.DownloadAsync(
                result.Username,
                result.RemoteFilename,
                localPath,
                size: result.Size,
                options: options,
                cancellationToken: attemptCts.Token);
        }
        catch (Exception ex) when (ex is SoulseekClientException or OperationCanceledException)
        {
            CleanupPartialFile(localPath);

            // Сторож сработал, а пользователь не отменял: пир просто не пошёл.
            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
                throw new DownloadException($"Пир {result.Username} не начал отдачу за {stallSeconds} с.");

            if (ex is SoulseekClientException soulseekEx)
                throw new DownloadException(DescribeTransferError(soulseekEx, result), soulseekEx);

            throw;
        }
        finally
        {
            watchdog.Cancel();
            await IgnoreAsync(watch);
        }

        if (!transfer.State.HasFlag(TransferStates.Completed) ||
            !transfer.State.HasFlag(TransferStates.Succeeded))
        {
            CleanupPartialFile(localPath);
            throw new DownloadException(
                $"Пир {result.Username} не отдал файл: {transfer.State}. {transfer.Exception?.Message}");
        }

        // Контроль целостности: размер на диске должен совпадать с заявленным.
        var actualSize = new FileInfo(localPath).Length;
        if (result.Size > 0 && actualSize != result.Size)
        {
            CleanupPartialFile(localPath);
            throw new DownloadException(
                $"Файл повреждён при передаче: ожидалось {result.Size} байт, получено {actualSize}.");
        }

        var elapsed = (DateTime.UtcNow - started).TotalSeconds;
        var speed = result.Size / 1024.0 / 1024 / Math.Max(0.1, elapsed);
        _logger.LogInformation(
            "Скачано: {File} ({Size:N0} байт) от {User} за {Seconds:F0}с — {Speed:F2} МБ/с",
            localPath, result.Size, result.Username, elapsed, speed);

        return localPath;
    }

    private static async Task IgnoreAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // Сторож отменён — это нормально.
        }
    }

    /// <summary>
    /// Ищет уже скачанный файл: то же имя и тот же размер в папке загрузок.
    /// Чтобы не качать заново то, что уже лежит на диске (и не плодить «track (1).flac»).
    /// </summary>
    private string? FindExistingLocalFile(AlbumFile file)
    {
        try
        {
            var dir = Path.GetFullPath(_config.Soulseek.DownloadDirectory);
            if (!System.IO.Directory.Exists(dir))
                return null;

            var name = SanitizeFileName(MusicResult.GetShortName(file.RemotePath), file.Extension);
            var direct = Path.Combine(dir, name);

            if (System.IO.File.Exists(direct) && new FileInfo(direct).Length == file.Size)
                return direct;

            // Могли остаться копии вида «name (1).flac» — ищем по размеру.
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);

            foreach (var candidate in System.IO.Directory.EnumerateFiles(dir, $"{stem}*{ext}"))
            {
                if (new FileInfo(candidate).Length == file.Size)
                    return candidate;
            }
        }
        catch
        {
            // Не нашли — просто скачаем заново.
        }

        return null;
    }

    /// <summary>
    /// Готовит безопасный локальный путь: только имя файла (без пути пира),
    /// без недопустимых символов и в пределах лимита длины имени в ФС.
    /// </summary>
    private string BuildLocalPath(MusicResult result)
    {
        // Пути Soulseek используют обратный слэш, а на Linux это обычный символ,
        // поэтому режем вручную по обоим разделителям.
        var remoteName = result.RemoteFilename
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? result.Filename;

        var safeName = SanitizeFileName(remoteName, result.Extension);

        var path = Path.Combine(_config.Soulseek.DownloadDirectory, safeName);

        // Если файл с таким именем уже есть — добавляем суффикс.
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var i = 1;
        while (System.IO.File.Exists(path))
            path = Path.Combine(_config.Soulseek.DownloadDirectory, $"{stem} ({i++}){ext}");

        return path;
    }

    /// <summary>Запоминает, у каких пиров встречался каждый файл из последнего поиска.</summary>
    private void IndexFileSources(IReadOnlyCollection<SearchResponse> responses)
    {
        FileSources.Clear();

        foreach (var response in responses)
        {
            foreach (var file in response.Files)
            {
                if (!ResultScorer.IsAudio(file) || file.Size < 2L * 1024 * 1024)
                    continue;

                var shortName = MusicResult.GetShortName(file.Filename);
                var key = FileSourceKey(shortName);
                var dirKey = DirectoryKey(file.Filename);
                var list = FileSources.GetOrAdd(key, _ => new List<FileSource>());

                lock (list)
                {
                    if (list.Count >= MaxFileSourcesPerFile ||
                        list.Any(s => string.Equals(s.Result.Username, response.Username, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var ext = string.IsNullOrEmpty(file.Extension)
                        ? Path.GetExtension(file.Filename).ToLowerInvariant()
                        : file.Extension.ToLowerInvariant();

                    list.Add(new FileSource(
                        Result: new MusicResult(
                            Username: response.Username,
                            RemoteFilename: file.Filename,
                            Filename: shortName,
                            Extension: ext,
                            Size: file.Size,
                            BitRate: file.BitRate,
                            BitDepth: file.BitDepth,
                            UploadSpeed: response.UploadSpeed,
                            HasFreeUploadSlot: response.HasFreeUploadSlot,
                            QueueLength: response.QueueLength,
                            Score: 0),
                        DirectoryKey: dirKey));
                }
            }
        }
    }

    /// <summary>
    /// Ищет, у кого ещё есть этот трек. Сначала точное совпадение имени, потом —
    /// файлы из одноимённой альбомной папки (у пиров имена внутри альбома отличаются:
    /// «02 ХТТ.flac» против «02. Платина - ХТТ.flac»).
    /// </summary>
    private IReadOnlyList<MusicResult> FindFileSources(AlbumResult album, AlbumFile file)
    {
        var wanted = FileSourceKey(file.Filename);
        var albumDir = DirectoryKey(album.Directory);
        var trackNo = file.TrackNumber;

        var exact = new List<MusicResult>();
        var sameFolder = new List<MusicResult>();

        foreach (var pair in FileSources)
        {
            lock (pair.Value)
            {
                foreach (var source in pair.Value)
                {
                    if (string.Equals(source.Result.Username, album.Username, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (string.Equals(pair.Key, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        exact.Add(source.Result);
                        continue;
                    }

                    // Та же альбомная папка и тот же номер трека — хороший кандидат.
                    if (albumDir.Length > 0 &&
                        string.Equals(source.DirectoryKey, albumDir, StringComparison.OrdinalIgnoreCase) &&
                        trackNo is { } no &&
                        ExtractTrackNumber(source.Result.Filename) == no)
                    {
                        sameFolder.Add(source.Result);
                    }
                }
            }
        }

        static int Rank(MusicResult r) =>
            (r.HasFreeUploadSlot ? 1_000_000_000 : 0) +
            (r.QueueLength == 0 ? 100_000_000 : 0) -
            r.QueueLength * 1_000_000 +
            Math.Min(999_999_999, r.UploadSpeed);

        return exact
            .OrderByDescending(Rank)
            .Concat(sameFolder.OrderByDescending(Rank))
            .Take(MaxFileSourcesPerFile)
            .ToList();
    }

    /// <summary>Ключ индекса источников: имя файла без номера трека, в нижнем регистре.</summary>
    private static string FileSourceKey(string filename) =>
        System.Text.RegularExpressions.Regex
            .Replace(Path.GetFileNameWithoutExtension(filename), @"^\s*\d{1,4}\s*[-._)]\s*", "")
            .Trim()
            .ToLowerInvariant();

    /// <summary>Ключ альбомной папки: последний элемент пути, в нижнем регистре.</summary>
    private static string DirectoryKey(string remotePath)
    {
        var parts = remotePath
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length > 0 ? parts[^1].Trim().ToLowerInvariant() : string.Empty;
    }

    private static int? ExtractTrackNumber(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        var m = System.Text.RegularExpressions.Regex.Match(stem, @"^\s*(\d{1,4})\s*[-._)]");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    /// <summary>Скачанный альбом: список локальных файлов и суммарный размер.</summary>
    public sealed record AlbumDownload(IReadOnlyList<string> Files, long TotalBytes, int FailedCount);

    /// <summary>
    /// Скачивает альбом целиком. Сначала пытается узнать у пира полный состав папки
    /// (в поиске могли прийти не все треки), затем качает файлы по очереди.
    /// </summary>
    /// <param name="onTrackReady">
    /// Вызывается сразу после скачивания каждого трека: индекс в альбоме и локальный путь.
    /// Нужен, чтобы отправлять треки в чат, не дожидаясь конца альбома. Может быть null.
    /// </param>
    public async Task<AlbumDownload> DownloadAlbumAsync(
        AlbumResult album,
        Action<AlbumProgress> progress,
        Action<int, string>? onTrackReady,
        CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);

        var files = await ResolveAlbumFilesAsync(album, ct);
        if (files.Count == 0)
            throw new DownloadException($"У пира {album.Username} не нашлось аудиофайлов в папке «{album.Title}».");

        // Обложки в конец: главное — треки по порядку, картинку пусть докачивает в конце.
        files = files
            .OrderBy(f => IsCover(f.Filename) ? 1 : 0)
            .ThenBy(f => f.TrackNumber ?? int.MaxValue)
            .ThenBy(f => f.Filename, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalBytes = files.Sum(f => f.Size);
        var failed = 0;

        // Счётчики прогресса, обновляемые из параллельных загрузок.
        long accountedBytes = 0;
        int doneCount = 0;
        int startedCount = 0;

        void Report(double speed = 0)
        {
            progress(new AlbumProgress(
                TracksDone: Volatile.Read(ref doneCount),
                TracksStarted: Volatile.Read(ref startedCount),
                TotalTracks: files.Count,
                BytesTransferred: Math.Min(totalBytes, Interlocked.Read(ref accountedBytes)),
                TotalBytes: totalBytes,
                AverageSpeed: speed));
        }

        Report();

        // Треки качаем несколькими потоками: суммарная скорость заметно выше,
        // а один зависший пир не тормозит весь альбом.
        var parallel = Math.Max(1, _config.Albums.ParallelTracks);
        using var gate = new SemaphoreSlim(parallel);
        var orderedResults = new string?[files.Count];
        var tasks = new List<Task>(files.Count);

        for (var i = 0; i < files.Count; i++)
        {
            var index = i;
            var file = files[i];

            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct);
                Interlocked.Increment(ref startedCount);

                // Сколько байт этого трека уже учтено в общем счётчике.
                var accountedForThisTrack = 0L;

                void Account(long total)
                {
                    var delta = total - Interlocked.Exchange(ref accountedForThisTrack, total);
                    if (delta != 0)
                        Interlocked.Add(ref accountedBytes, delta);
                }

                try
                {
                    ct.ThrowIfCancellationRequested();

                    // Если такой файл уже скачан (совпадает имя и размер) — не качаем заново,
                    // а сразу отдаём то, что лежит на диске.
                    var existing = FindExistingLocalFile(file);
                    if (existing is not null)
                    {
                        orderedResults[index] = existing;
                        Account(SafeFileSize(existing));
                        Interlocked.Increment(ref doneCount);
                        _logger.LogInformation("Уже на диске: {File}", existing);
                        onTrackReady?.Invoke(index, existing);
                        return;
                    }

                    var trackResult = new MusicResult(
                        Username: album.Username,
                        RemoteFilename: file.RemotePath,
                        Filename: file.Filename,
                        Extension: file.Extension,
                        Size: file.Size,
                        BitRate: file.BitRate,
                        BitDepth: file.BitDepth,
                        UploadSpeed: album.UploadSpeed,
                        HasFreeUploadSlot: album.HasFreeUploadSlot,
                        QueueLength: album.QueueLength,
                        Score: 0);

                    // Запасные пиры с тем же треком: сначала точное совпадение имени,
                    // потом одноимённая альбомная папка у других пиров из поиска.
                    var fallbacks = FindFileSources(album, file);
                    var isCover = IsCover(file.Filename);

                    var path = await DownloadWithFailoverAsync(
                        trackResult,
                        p =>
                        {
                            Account(p.BytesTransferred);
                            Report(p.AverageSpeed);
                        },
                        fallbacks,
                        ct,
                        // Обложка — необязательная деталь, ждать её 20 секунд смысла нет.
                        isCover ? Math.Max(4, _config.Downloads.StallTimeoutSeconds / 3) : null);

                    orderedResults[index] = path;
                    Account(SafeFileSize(path));
                    Interlocked.Increment(ref doneCount);

                    // Отдаём трек наружу сразу, не дожидаясь остальных.
                    try
                    {
                        onTrackReady?.Invoke(index, path);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Не удалось отдать готовый трек {File}", path);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Один битый трек не должен губить весь альбом.
                    Interlocked.Increment(ref failed);
                    Account(file.Size); // чтобы общий прогресс не «застревал»
                    _logger.LogWarning(ex, "Альбом «{Album}»: не удалось скачать {File}", album.Title, file.Filename);
                }
                finally
                {
                    gate.Release();
                    Report();
                }
            }, CancellationToken.None));
        }

        await Task.WhenAll(tasks);

        var localFiles = orderedResults.Where(p => p is not null).Select(p => p!).ToList();
        return new AlbumDownload(localFiles, localFiles.Sum(SafeFileSize), failed);

        // Размер уже скачанного файла на диске.
        static long SafeFileSize(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Уточняет состав альбома: просит у пира листинг папки и оставляет аудио + обложки.
    /// Если пир не ответил — берём файлы, которые пришли в поиске.
    /// </summary>
    private async Task<List<AlbumFile>> ResolveAlbumFilesAsync(AlbumResult album, CancellationToken ct)
    {
        try
        {
            // GetDirectoryContentsAsync возвращает набор папок (может быть вложенность).
            var contents = await _client.GetDirectoryContentsAsync(album.Username, album.Directory, cancellationToken: ct);

            var audio = contents
                .SelectMany(dir => dir.Files)
                .Where(f => f.Size >= 1024 && (ResultScorer.IsAudio(f) || IsCover(f.Filename)))
                .Take(_config.Albums.MaxTracksPerAlbum)
                // В листинге имена относительные — для скачивания нужен полный путь.
                .Select(f => AlbumFile.From(WithAlbumDirectory(f, album.Directory)))
                .ToList();

            if (audio.Count > 0)
            {
                _logger.LogInformation("Альбом «{Album}»: пир {User} отдал листинг, {Count} файлов",
                    album.Title, album.Username, audio.Count);
                return audio;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Альбом «{Album}»: не удалось получить листинг папки у {User}, беру файлы из поиска",
                album.Title, album.Username);
        }

        return album.Files.ToList();
    }

    private static bool IsCover(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp";
    }

    /// <summary>
    /// Листинг папки отдаёт имена файлов относительно папки, а Soulseek при скачивании
    /// ждёт полный путь. Без этого пир отвечает «File not shared».
    /// </summary>
    private static SlFile WithAlbumDirectory(SlFile file, string directory)
    {
        if (directory.Length == 0)
            return file;

        var name = file.Filename;
        if (!name.Contains('\\') && !name.Contains('/'))
            name = directory.TrimEnd('\\', '/') + "\\" + name;

        if (string.Equals(name, file.Filename, StringComparison.Ordinal))
            return file;

        return new SlFile(
            code: file.Code,
            filename: name,
            size: file.Size,
            extension: file.Extension,
            attributeList: file.Attributes);
    }

    /// <summary>Переводит исключения сетевого уровня в понятный пользователю текст.</summary>
    private static string DescribeTransferError(Exception ex, MusicResult result) => ex switch    {
        UserOfflineException => $"Пир {result.Username} сейчас офлайн — попробуй другой вариант из результатов.",
        UserNotFoundException => $"Пир {result.Username} больше не в сети — попробуй другой вариант.",
        TransferRejectedException => $"Пир {result.Username} больше не шэрит этот файл — попробуй другой вариант.",
        TransferSizeMismatchException => "Файл пришёл повреждённым (размер не совпал) — попробуй другой вариант.",
        TransferReportedFailedException => $"Пир {result.Username} сообщил об ошибке передачи — попробуй другой вариант.",
        TransferNotFoundException => $"Файл не найден у пира {result.Username} — попробуй другой вариант.",
        ConnectionException or ConnectionReadException or ConnectionWriteException =>
            "Нет соединения с пиром — проверь интернет или попробуй другой вариант.",
        _ => $"Не удалось скачать файл: {ex.Message}",
    };

    /// <summary>Удаляет недокачанный файл, чтобы он не попал в шару.</summary>
    private static void CleanupPartialFile(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        catch
        {
            // Не критично: файл уйдёт при следующей чистке папки.
        }
    }

    /// <summary>Убирает недопустимые символы и обрезает имя до 200 байт (лимит ФС — 255).</summary>
    private static string SanitizeFileName(string name, string fallbackExtension)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name
            .Where(c => !char.IsControl(c) && !invalid.Contains(c))
            .ToArray())
            .Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = $"track{DateTime.UtcNow.Ticks}{fallbackExtension}";

        // Обрезка по границе UTF-8, чтобы не разорвать многобайтовый символ.
        const int maxBytes = 200;
        while (System.Text.Encoding.UTF8.GetByteCount(cleaned) > maxBytes && cleaned.Length > 1)
            cleaned = cleaned[..^1];

        return cleaned;
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (!IsConnected || !_client.State.HasFlag(SoulseekClientStates.LoggedIn))
        {
            await _reconnectTask.WaitAsync(ct);
            if (!IsConnected)
                await ConnectWithRetryAsync(ct);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        _shuttingDown = true;
        _client.Disconnect("shutting down");
        _client.Dispose();
        _connectLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Снимок прогресса скачивания.</summary>
public sealed record TransferProgress(
    double PercentComplete,
    double AverageSpeed,
    long BytesTransferred,
    long TotalBytes,
    TimeSpan? RemainingTime);

/// <summary>Прогресс скачивания альбома: сколько треков готово и общий объём.</summary>
/// <param name="TracksDone">Треков скачано полностью.</param>
/// <param name="TracksStarted">Треков уже взято в работу (может быть больше, чем TracksDone).</param>
/// <param name="TotalTracks">Всего треков в альбоме.</param>
/// <param name="BytesTransferred">Суммарно скачано байт (включая незавершённые треки).</param>
/// <param name="TotalBytes">Суммарный объём альбома.</param>
/// <param name="AverageSpeed">Текущая суммарная скорость, байт/с.</param>
public sealed record AlbumProgress(
    int TracksDone,
    int TracksStarted,
    int TotalTracks,
    long BytesTransferred,
    long TotalBytes,
    double AverageSpeed);

/// <summary>Ошибка скачивания, показываемая пользователю как есть.</summary>
public sealed class DownloadException : Exception
{
    public DownloadException(string message) : base(message) { }
    public DownloadException(string message, Exception inner) : base(message, inner) { }
}
