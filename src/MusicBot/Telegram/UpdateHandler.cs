using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MusicBot.Configuration;
using MusicBot.Downloads;
using MusicBot.Soulseek;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace MusicBot.Telegram;

/// <summary>
/// Маршрутизация обновлений и флоу: поиск → выбор результата → скачивание → отправка аудио.
/// </summary>
public sealed class UpdateHandler
{
    private readonly BotConfig _config;
    private readonly SoulseekService _soulseek;
    private readonly DownloadQueue _queue;
    private readonly ShareService _shares;
    private readonly ILogger<UpdateHandler> _logger;

    /// <summary>Кэш последних поисков (треки + альбомы) для нажатий на кнопки.</summary>
    private readonly ConcurrentDictionary<string, SoulseekService.SearchResults> _searchResults = new();

    /// <summary>Сколько последних поисков держим в памяти для нажатий на кнопки.</summary>
    private const int MaxCachedSearches = 20;

    /// <summary>Момент последнего редактирования сообщения с прогрессом (чтобы не упереться в лимиты).</summary>
    private readonly ConcurrentDictionary<string, (DateTime Time, double Percent)> _lastProgressEdit = new();

    public UpdateHandler(
        BotConfig config,
        SoulseekService soulseek,
        DownloadQueue queue,
        ShareService shares,
        ILogger<UpdateHandler> logger)
    {
        _config = config;
        _soulseek = soulseek;
        _queue = queue;
        _shares = shares;
        _logger = logger;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            switch (update)
            {
                case { Message: { } message, }:
                    await HandleMessageAsync(bot, message, ct);
                    break;

                case { CallbackQuery: { } callback }:
                    await HandleCallbackAsync(bot, callback, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка обработки апдейта {Type}", update.Type);
        }
    }

    public Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        if (exception is RequestException reqEx)
            _logger.LogWarning("Сетевая ошибка Telegram API: {Message}", reqEx.Message);
        else
            _logger.LogError(exception, "Ошибка Telegram API");

        return Task.CompletedTask;
    }

    // ---------- Команды и текст ----------

    private bool IsAdmin(long userId) =>
        _config.Telegram.AdminUserId == 0 || userId == _config.Telegram.AdminUserId;

    private async Task HandleMessageAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
    {
        if (message.From is null || message.Text is not { } text)
            return;

        if (!IsAdmin(message.From.Id))
        {
            _logger.LogInformation("Чужой пользователь {UserId} ({Username}) пытался писать боту — игнор",
                message.From.Id, message.From.Username);
            return;
        }

        var chatId = message.Chat.Id;

        if (text.StartsWith('/'))
        {
            await HandleCommandAsync(bot, chatId, message.From.Id, text, ct);
            return;
        }

        // Любой текст без слэша — это поисковый запрос.
        await RunSearchFlowAsync(bot, chatId, text.Trim(), ct);
    }

    private async Task HandleCommandAsync(ITelegramBotClient bot, long chatId, long userId, string text, CancellationToken ct)
    {
        var command = text.Split(' ')[0].Split('@')[0].ToLowerInvariant();

        switch (command)
        {
            case "/start" or "/help":
                await bot.SendMessage(chatId,
                    "🎵 Я качаю музыку из сети Soulseek.\n\n" +
                    "Просто пришли название трека или исполнителя — я покажу топ результатов.\n" +
                    "Можно и явно: /search Daft Punk Around the World\n\n" +
                    "Команды:\n" +
                    "/cancel — отменить текущее скачивание\n" +
                    "/status — состояние очереди\n" +
                    "/id — показать твой Telegram ID",
                    cancellationToken: ct);
                break;

            case "/id":
                await bot.SendMessage(chatId, $"Твой ID: {userId}\nВпиши его в appsettings.json → Telegram:AdminUserId", cancellationToken: ct);
                break;

            case "/cancel":
            {
                var cancelled = _queue.TryCancel(UserKey(chatId));
                await bot.SendMessage(chatId,
                    cancelled ? "🛑 Отменяю текущее скачивание…" : "Сейчас ничего не качается.",
                    cancellationToken: ct);
                break;
            }

            case "/status":
            {
                var active = _queue.GetActive(UserKey(chatId));
                var status = _queue.ActiveCount == 0
                    ? "Очередь пуста, ничего не качается."
                    : $"Активных: {_queue.ActiveCount}, ждут: {_queue.WaitingCount}." +
                      (active is { } job ? $"\nСейчас: {job.DisplayName}" : "");
                await bot.SendMessage(chatId, status, cancellationToken: ct);
                break;
            }

            default:
            {
                // /search <запрос> и любые «/что-то <текст>» трактуем как поиск:
                // так опечатка в команде не ломает сценарий.
                var argument = text[(text.IndexOf(' ') + 1)..].Trim();

                if (argument.Length > 0)
                {
                    await RunSearchFlowAsync(bot, chatId, argument, ct);
                    return;
                }

                await bot.SendMessage(chatId,
                    "Неизвестная команда. Пришли текст запроса для поиска или набери /help.",
                    cancellationToken: ct);
                break;
            }
        }
    }

    private static string UserKey(long chatId) => $"chat{chatId}";

    // ---------- Поиск ----------

    private async Task RunSearchFlowAsync(ITelegramBotClient bot, long chatId, string query, CancellationToken ct)
    {
        if (query.Length is < 2 or > 200)
        {
            await bot.SendMessage(chatId, "Запрос должен быть от 2 до 200 символов.", cancellationToken: ct);
            return;
        }

        var statusMessage = await bot.SendMessage(chatId, $"🔍 Ищу «{query}» на Soulseek…", cancellationToken: ct);

        SoulseekService.SearchResults results;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_config.Search.TimeoutSeconds * 1000 + 20_000));
            results = await _soulseek.SearchAsync(query, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await SafeEdit(bot, chatId, statusMessage.Id, "⏱ Поиск превысил таймаут. Попробуй ещё раз.", replyMarkup: null, ct);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка поиска «{Query}»", query);
            await SafeEdit(bot, chatId, statusMessage.Id,
                $"⚠️ Ошибка поиска: {ex.Message}\nПроверь, что бот подключён к сети Soulseek (см. лог).",
                replyMarkup: null, ct);
            return;
        }

        if (results.IsEmpty)
        {
            await SafeEdit(bot, chatId, statusMessage.Id,
                "😔 Ничего не нашлось. Попробуй короче запрос или другой вариант названия.", replyMarkup: null, ct);
            return;
        }

        var searchId = Guid.NewGuid().ToString("N")[..8];
        _searchResults[searchId] = results;

        // Подрезаем кэш: оставляем только последние поиски.
        while (_searchResults.Count > MaxCachedSearches)
        {
            var oldest = _searchResults.Keys.FirstOrDefault();
            if (oldest is null || !_searchResults.TryRemove(oldest, out _))
                break;
        }

        await SafeEdit(bot, chatId, statusMessage.Id,
            MessageViews.BuildSearchMessage(query, results),
            replyMarkup: MessageViews.BuildSearchKeyboard(searchId, results),
            ct);
    }

    // ---------- Выбор результата и скачивание ----------

    private async Task HandleCallbackAsync(ITelegramBotClient bot, CallbackQuery callback, CancellationToken ct)
    {
        if (callback.From is null || !IsAdmin(callback.From.Id) || callback.Data is not { } data)
            return;

        var parts = data.Split('|');
        if (parts.Length != 3 || parts[0] != "dl")
            return;

        var searchId = parts[1];
        var target = parts[2];
        var isAlbum = target.StartsWith('a');
        if (!isAlbum && !target.StartsWith('t'))
            return;

        if (!int.TryParse(target.AsSpan(1), out var index))
            return;

        var chatId = callback.Message?.Chat.Id ?? callback.From.Id;
        var statusMessageId = callback.Message?.MessageId;

        if (statusMessageId is null ||
            !_searchResults.TryGetValue(searchId, out var results) ||
            index < 0 ||
            (isAlbum ? index >= results.Albums.Count : index >= results.Tracks.Count))
        {
            await bot.AnswerCallbackQuery(callback.Id, "Результат устарел, поищи заново", cancellationToken: ct);
            return;
        }

        if (_queue.IsActive(UserKey(chatId)))
        {
            await bot.AnswerCallbackQuery(callback.Id, "Уже качается — дождись или /cancel", showAlert: true, cancellationToken: ct);
            return;
        }

        var job = new DownloadJob
        {
            UserKey = UserKey(chatId),
            ChatId = chatId,
            StatusMessageId = statusMessageId.Value,
            Album = isAlbum ? results.Albums[index] : null,
            Track = isAlbum ? null : results.Tracks[index],
            Cts = new CancellationTokenSource(),
        };

        await bot.AnswerCallbackQuery(callback.Id,
            isAlbum ? $"Качаю альбом: {job.Album!.Title}" : "Начинаю скачивание…", cancellationToken: ct);

        // Фоновое выполнение: UI уже отвечает, скачивание может занять минуты.
        _ = Task.Run(() => RunDownloadAsync(bot, job), CancellationToken.None);
    }

    private async Task RunDownloadAsync(ITelegramBotClient bot, DownloadJob job)
    {
        if (job.IsAlbum)
        {
            await RunAlbumDownloadAsync(bot, job, job.Album!);
            return;
        }

        await RunTrackDownloadAsync(bot, job, job.Track!);
    }

    // ---------- Трек ----------

    private async Task RunTrackDownloadAsync(ITelegramBotClient bot, DownloadJob job, MusicResult result)
    {
        var (chatId, messageId) = (job.ChatId, job.StatusMessageId);

        try
        {
            await SafeEdit(bot, chatId, messageId,
                $"⬇️ Скачиваю {result.Filename}\nот пира {result.Username}…", replyMarkup: null, CancellationToken.None);

            var localPath = await _queue.RunAsync(
                job,
                progress => _ = ReportProgressAsync(bot, job, progress),
                CancellationToken.None);

            await SafeEdit(bot, chatId, messageId,
                $"📤 Отправляю {result.Filename} ({MessageViews.FormatBytes(result.Size)})…", replyMarkup: null, CancellationToken.None);

            await SendAudioAsync(bot, job, localPath, CancellationToken.None);
            await SafeEdit(bot, chatId, messageId, $"✅ Готово: {result.Filename}", replyMarkup: null, CancellationToken.None);

            if (!_config.Downloads.KeepDownloadedFiles)
            {
                File.Delete(localPath);
            }

            // Новые файлы — новая шара.
            _shares.InvalidateCache();
        }
        catch (OperationCanceledException)
        {
            await SafeEdit(bot, chatId, messageId, "🛑 Скачивание отменено.", replyMarkup: null, CancellationToken.None);
        }
        catch (DownloadException ex)
        {
            _logger.LogWarning("Не удалось скачать {File} от {User}: {Message}", result.RemoteFilename, result.Username, ex.Message);
            await SafeEdit(bot, chatId, messageId,
                $"⚠️ Не удалось скачать: {ex.Message}\nПопробуй другой вариант из результатов (поиск заново).",
                replyMarkup: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Неожиданная ошибка при скачивании {File}", result.RemoteFilename);
            await SafeEdit(bot, chatId, messageId, $"⚠️ Ошибка скачивания: {ex.Message}", replyMarkup: null, CancellationToken.None);
        }
        finally
        {
            job.Cts.Dispose();
            _lastProgressEdit.TryRemove($"{chatId}:{messageId}", out _);
        }
    }

    // ---------- Альбом ----------

    private async Task RunAlbumDownloadAsync(ITelegramBotClient bot, DownloadJob job, AlbumResult album)
    {
        var (chatId, messageId) = (job.ChatId, job.StatusMessageId);
        string? archivePath = null;
        var archiveWasSent = false;

        var packageAs = _config.Albums.PackageAs.ToLowerInvariant();
        var sendTracks = packageAs is "individual" or "both";
        var sendZip = packageAs is "zip" or "both";

        // Очередь отправки: скачивание не ждёт Telegram и наоборот.
        var sendQueue = System.Threading.Channels.Channel.CreateUnbounded<(int Index, string Path)>();
        var sendResult = new AlbumSendResult(0, 0);
        var senderTask = sendTracks
            ? SendAlbumTracksAsync(bot, job, album, sendQueue.Reader, album.OrderedFiles.Count)
            : Task.FromResult(new AlbumSendResult(0, 0));

        try
        {
            await SafeEdit(bot, chatId, messageId,
                $"📦 Качаю альбом: {album.Title}\n{album.TrackCount} треков · {MessageViews.FormatBytes(album.TotalSize)} · пир {album.Username}",
                replyMarkup: null, CancellationToken.None);

            var download = await _queue.RunAlbumAsync(
                job,
                progress => _ = ReportAlbumProgressAsync(bot, job, album, progress),
                (index, localPath) => sendQueue.Writer.TryWrite((index, localPath)),
                CancellationToken.None);

            // Доходим очередь отправки, даже если часть треков не скачалась.
            sendQueue.Writer.TryComplete();
            sendResult = await senderTask;

            if (download.Files.Count == 0)
            {
                await SafeEdit(bot, chatId, messageId,
                    "⚠️ Не удалось скачать ни одного трека — пир мог уйти офлайн. Попробуй другой вариант.",
                    replyMarkup: null, CancellationToken.None);
                return;
            }

            var tracks = AlbumPackager.TracksOnly(download.Files);
            var covers = AlbumPackager.CoversOnly(download.Files);

            // Обложка — превью для треков и отдельное сообщение.
            if (covers.Count > 0)
            {
                await ThrottledSendAsync(() => SendDocumentAsync(bot, job, covers[0], "🖼 Обложка", CancellationToken.None));
            }

            if (sendZip)
            {
                var maxZipBytes = (long)_config.Albums.MaxZipSizeMb * 1024 * 1024;
                var zipPath = Path.Combine(_config.Soulseek.DownloadDirectory, AlbumPackager.BuildArchiveName(album, DateTime.Now));

                await SafeEdit(bot, chatId, messageId,
                    $"📦 Упаковываю {tracks.Count} треков в архив…", replyMarkup: null, CancellationToken.None);
                archivePath = await AlbumPackager.TryCreateZipAsync(download.Files, zipPath, maxZipBytes, CancellationToken.None);

                if (archivePath is not null)
                {
                    archiveWasSent = true;
                    var zipSize = new FileInfo(archivePath).Length;
                    await SafeEdit(bot, chatId, messageId,
                        $"📤 Отправляю архив ({MessageViews.FormatBytes(zipSize)})…", replyMarkup: null, CancellationToken.None);

                    var caption = $"📦 {album.Title}\n{album.FormatSummary} · {tracks.Count} треков · " +
                                  $"{MessageViews.FormatBytes(download.TotalBytes)} · пир: {album.Username}";
                    await ThrottledSendAsync(() => SendDocumentAsync(bot, job, archivePath!, caption, CancellationToken.None));

                    TryDelete(archivePath);
                    archivePath = null;
                }
            }

            var issues = new List<string>();
            if (download.FailedCount > 0)
                issues.Add($"не скачано треков: {download.FailedCount}");
            if (sendResult.Errors > 0)
                issues.Add($"не отправлено: {sendResult.Errors}");

            var tail = issues.Count > 0 ? $"\n⚠️ {string.Join("; ", issues)}" : string.Empty;

            // Итог зависит от режима: треки, архив или и то и другое.
            var summary = (sendTracks, archiveWasSent) switch
            {
                (true, true) => $"✅ Альбом «{album.Title}»: отправлено треков {sendResult.Sent} + архив",
                (true, false) => $"✅ Альбом «{album.Title}»: отправлено треков {sendResult.Sent}",
                (false, true) => $"✅ Альбом «{album.Title}»: отправлен архив",
                _ => $"✅ Альбом «{album.Title}»: файлы скачаны, но не отправлены (смотри лог)",
            };

            await SafeEdit(bot, chatId, messageId, summary + tail, replyMarkup: null, CancellationToken.None);

            if (!_config.Downloads.KeepDownloadedFiles)
            {
                foreach (var file in download.Files)
                    TryDelete(file);
            }

            _shares.InvalidateCache();
        }
        catch (OperationCanceledException)
        {
            sendQueue.Writer.TryComplete();
            await SafeIgnoreAsync(senderTask);
            await SafeEdit(bot, chatId, messageId, "🛑 Скачивание альбома отменено.", replyMarkup: null, CancellationToken.None);
        }
        catch (DownloadException ex)
        {
            sendQueue.Writer.TryComplete();
            await SafeIgnoreAsync(senderTask);
            _logger.LogWarning("Не удалось скачать альбом {Album} от {User}: {Message}", album.Title, album.Username, ex.Message);
            await SafeEdit(bot, chatId, messageId,
                $"⚠️ Не удалось скачать альбом: {ex.Message}\nПопробуй другого пира из результатов.",
                replyMarkup: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            sendQueue.Writer.TryComplete();
            await SafeIgnoreAsync(senderTask);
            _logger.LogError(ex, "Неожиданная ошибка при скачивании альбома {Album}", album.Title);
            await SafeEdit(bot, chatId, messageId, $"⚠️ Ошибка скачивания альбома: {ex.Message}", replyMarkup: null, CancellationToken.None);
        }
        finally
        {
            sendQueue.Writer.TryComplete();
            await SafeIgnoreAsync(senderTask);

            if (archivePath is not null)
                TryDelete(archivePath);

            job.Cts.Dispose();
            _lastProgressEdit.TryRemove($"{chatId}:{messageId}", out _);
        }
    }

    /// <summary>Итог потоковой отправки треков альбома.</summary>
    private sealed record AlbumSendResult(int Sent, int Errors);

    /// <summary>
    /// Отправляет треки альбома в чат по мере готовности, но строго по порядку номера:
    /// трек №3 не уйдёт раньше №2, даже если скачался раньше. С тегами из файла,
    /// обложкой-превью и ограничением частоты (Telegram не любит пачки сообщений).
    /// </summary>
    private async Task<AlbumSendResult> SendAlbumTracksAsync(
        ITelegramBotClient bot,
        DownloadJob job,
        AlbumResult album,
        System.Threading.Channels.ChannelReader<(int Index, string Path)> queue,
        int expectedTotal)
    {
        var sent = 0;
        var errors = 0;
        var coverAttached = false;
        var nextIndex = 0;

        // Буфер: ждём нужный номер трека, но не бесконечно.
        var pending = new SortedDictionary<int, string>();
        var waitingSince = DateTime.UtcNow;

        // Битый трек не должен заблокировать весь альбом: ждём его недолго и идём дальше.
        const int SkipGraceSeconds = 10;

        void TryFlush(bool forceSkipStale = false)
        {
            if (forceSkipStale && pending.Count > 0 && pending.Keys.Min() > nextIndex &&
                (DateTime.UtcNow - waitingSince).TotalSeconds >= SkipGraceSeconds)
            {
                nextIndex = pending.Keys.Min();
            }

            while (pending.TryGetValue(nextIndex, out var path))
            {
                pending.Remove(nextIndex);
                SendOne(path);
                nextIndex++;
                waitingSince = DateTime.UtcNow;
            }
        }

        void SendOne(string path)
        {
            try
            {
                var fallbackTitle = MessageViews.GuessTrackTitle(Path.GetFileName(path));
                var meta = TrackMetadataReader.Read(path, fallbackTitle);

                SendTrackWithMetadataAsync(bot, job, path, meta, album, coverAttached)
                    .GetAwaiter().GetResult();

                if (meta.CoverBytes is { Length: > 0 })
                    coverAttached = true;

                sent++;

                if (sent % 3 == 0)
                {
                    SafeEdit(bot, job.ChatId, job.StatusMessageId,
                        $"📤 Отправлено треков: {sent}", replyMarkup: null, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                errors++;
                _logger.LogWarning(ex, "Не удалось отправить трек {File}", path);
            }
        }

        await foreach (var (index, path) in queue.ReadAllAsync())
        {
            lock (pending)
            {
                pending[index] = path;
                TryFlush();
            }

            // Ждём пропущенный номер трека, но не дольше отведённого времени.
            var waitUntil = DateTime.UtcNow.AddSeconds(SkipGraceSeconds);
            while (pending.Count > 0 && DateTime.UtcNow < waitUntil)
            {
                lock (pending)
                {
                    TryFlush(forceSkipStale: true);
                }

                if (pending.Count == 0)
                    break;

                await Task.Delay(500, CancellationToken.None);
            }

            // Telegram не любит пачки сообщений: держим темп ~20 в минуту.
            await ThrottleAsync();
        }

        // На случай пропусков (битые треки) — досылаем всё, что осталось.
        if (pending.Count > 0)
        {
            foreach (var path in pending.Values)
                SendOne(path);
        }

        _logger.LogDebug("Отправлено {Sent} из {Total} треков альбома «{Album}»", sent, expectedTotal, album.Title);
        return new AlbumSendResult(sent, errors);
    }

    private async Task<bool> SendTrackWithMetadataAsync(
        ITelegramBotClient bot,
        DownloadJob job,
        string localPath,
        TrackMetadata meta,
        AlbumResult album,
        bool coverAttached)
    {
        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var file = InputFile.FromStream(stream, Path.GetFileName(localPath));

        // Обложку прикрепляем только к первому треку: иначе повторяем картинку 20 раз.
        InputFile? thumbnail = null;
        MemoryStream? thumbStream = null;
        var coverUsed = false;

        if (!coverAttached && meta.CoverBytes is { Length: > 0 } bytes)
        {
            thumbStream = new MemoryStream(bytes);
            thumbnail = InputFile.FromStream(thumbStream, "cover.jpg");
            coverUsed = true;
        }

        try
        {
            var caption = string.IsNullOrWhiteSpace(meta.Album)
                ? $"{meta.Performer} — {meta.Title}"
                : $"{meta.Performer} — {meta.Title}\n{album.Title} · пир: {album.Username}";

            await bot.SendAudio(
                job.ChatId,
                file,
                caption: TruncateCaption(caption),
                performer: meta.Performer,
                title: meta.Title,
                thumbnail: thumbnail,
                duration: meta.DurationSeconds,
                cancellationToken: CancellationToken.None);

            return coverUsed;
        }
        finally
        {
            thumbStream?.Dispose();
        }
    }

    /// <summary>Ограничитель частоты отправки: не чаще ~20 сообщений в минуту.</summary>
    private static readonly SemaphoreSlim _rateGate = new(1, 1);
    private static DateTime _lastSendUtc = DateTime.MinValue;

    private static async Task ThrottledSendAsync(Func<Task> send)
    {
        await _rateGate.WaitAsync();
        try
        {
            var minGap = TimeSpan.FromSeconds(3);
            var sinceLast = DateTime.UtcNow - _lastSendUtc;
            if (sinceLast < minGap)
                await Task.Delay(minGap - sinceLast);
        }
        finally
        {
            _rateGate.Release();
        }

        await send();
        _lastSendUtc = DateTime.UtcNow;
    }

    private static Task ThrottleAsync() => ThrottledSendAsync(() => Task.CompletedTask);

    private static async Task SafeIgnoreAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // Фоновая отправка не должна ронять основной флоу.
        }
    }

    private static string TruncateCaption(string caption) =>
        caption.Length <= 1024 ? caption : caption[..1021] + "…";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Не критично: файл уйдёт при чистке папки.
        }
    }

    /// <summary>Редактирование сообщения с прогрессом не чаще, чем раз в 2 секунды и не чаще, чем на 1%.</summary>
    private async Task ReportProgressAsync(ITelegramBotClient bot, DownloadJob job, TransferProgress progress)
    {
        var key = $"{job.ChatId}:{job.StatusMessageId}";
        var now = DateTime.UtcNow;

        if (_lastProgressEdit.TryGetValue(key, out var last) &&
            (now - last.Time).TotalSeconds < 2 && progress.PercentComplete - last.Percent < 5)
        {
            return;
        }

        _lastProgressEdit[key] = (now, progress.PercentComplete);
        await SafeEdit(bot, job.ChatId, job.StatusMessageId,
            MessageViews.BuildProgressMessage(job.Track!, progress), replyMarkup: null, CancellationToken.None);
    }

    /// <summary>Прогресс альбома: обновляем не чаще раза в 3 секунды и при заметном сдвиге.</summary>
    private async Task ReportAlbumProgressAsync(ITelegramBotClient bot, DownloadJob job, AlbumResult album, AlbumProgress progress)
    {
        var key = $"{job.ChatId}:{job.StatusMessageId}";
        var now = DateTime.UtcNow;
        var percent = progress.TotalBytes > 0 ? (double)progress.BytesTransferred / progress.TotalBytes * 100 : 0;

        if (_lastProgressEdit.TryGetValue(key, out var last) &&
            (now - last.Time).TotalSeconds < 3 && percent - last.Percent < 2)
        {
            return;
        }

        _lastProgressEdit[key] = (now, percent);
        await SafeEdit(bot, job.ChatId, job.StatusMessageId,
            MessageViews.BuildAlbumProgressMessage(album, progress), replyMarkup: null, CancellationToken.None);
    }

    private async Task SendAudioAsync(ITelegramBotClient bot, DownloadJob job, string localPath, CancellationToken ct, string? caption = null)
    {
        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var file = InputFile.FromStream(stream, Path.GetFileName(localPath));

        var result = job.Track;
        caption ??= result is null
            ? $"🎵 {MessageViews.GuessTrackTitle(Path.GetFileName(localPath))}"
            : $"🎵 {MessageViews.GuessTrackTitle(result.Filename)}\n" +
              $"{result.FormatLabel} · {MessageViews.FormatBytes(result.Size)} · пир: {result.Username}";

        await bot.SendAudio(job.ChatId, file, caption: caption, cancellationToken: ct);
    }

    /// <summary>Отправка файла как документа (архив альбома, обложка).</summary>
    private async Task SendDocumentAsync(ITelegramBotClient bot, DownloadJob job, string localPath, string caption, CancellationToken ct)
    {
        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var file = InputFile.FromStream(stream, Path.GetFileName(localPath));

        await bot.SendDocument(job.ChatId, file, caption: caption, cancellationToken: ct);
    }

    /// <summary>Редактирование сообщения без падений (игнор «not modified», «message not found» и т.п.).</summary>
    private async Task SafeEdit(
        ITelegramBotClient bot, long chatId, int messageId, string text, InlineKeyboardMarkup? replyMarkup, CancellationToken ct)
    {
        try
        {
            await bot.EditMessageText(chatId, messageId, text, replyMarkup: replyMarkup, cancellationToken: ct);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("not modified") || ex.Message.Contains("not found"))
        {
            // Нормальные ситуации: текст не изменился / сообщение удалено.
        }
        catch (RequestException ex)
        {
            _logger.LogDebug(ex, "Не удалось отредактировать сообщение {MessageId}", messageId);
        }
    }
}
