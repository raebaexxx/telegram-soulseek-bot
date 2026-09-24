using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using MusicBot.Configuration;
using Soulseek;
using SlDirectory = Soulseek.Directory;
using SlFile = Soulseek.File;

namespace MusicBot.Soulseek;

/// <summary>
/// Шаринг содержимого папки загрузок другим пирам сети.
/// Soulseek — взаимная сеть: пиры без шары считаются леечерами,
/// их ставят в конец очереди или вообще игнорируют.
/// </summary>
public sealed class ShareService
{
    private readonly BotConfig _config;
    private readonly ILogger<ShareService> _logger;
    private readonly ConcurrentDictionary<string, List<SlFile>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Список запрещённых фраз от сервера (анти-копирайт); по ним мы не отдаём результаты.</summary>
    private volatile string[] _excludedPhrases = [];

    public ShareService(BotConfig config, ILogger<ShareService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Подключает обработку списка запрещённых фраз от сервера.</summary>
    public void Attach(SoulseekClient client) =>
        client.ExcludedSearchPhrasesReceived += (_, e) =>
        {
            _excludedPhrases = [.. e];
            _logger.LogInformation("Получен список запрещённых фраз от сервера: {Count} шт.", e.Count);
        };

    /// <summary>Сбрасывает кэш шары (например, после завершения скачивания).</summary>
    public void InvalidateCache()
    {
        lock (_lock)
            _cache.Clear();
    }

    /// <summary>Статистика шары для объявления серверу (папки, файлы).</summary>
    public (int Directories, int Files) GetShareStats()
    {
        var dirs = ScanSharedDirectories();
        return (dirs.Count, dirs.Sum(d => d.Value.Count));
    }

    /// <summary>
    /// Ответ на входящий поисковый запрос от другого пира.
    /// </summary>
    public Task<SearchResponse?> SearchResponseResolver(string username, int token, SearchQuery query)
    {
        if (!_config.Soulseek.ShareDownloads)
            return Task.FromResult<SearchResponse?>(null);

        try
        {
            var directories = ScanSharedDirectories();

            var files = directories
                .SelectMany(d => d.Value)
                .Where(f => MatchesQuery(f.Filename, query))
                .Take(200)
                .ToList();

            if (files.Count == 0)
                return Task.FromResult<SearchResponse?>(null);

            var response = new SearchResponse(
                username: _config.Soulseek.Username,
                token: token,
                hasFreeUploadSlot: true,
                uploadSpeed: 10_000_000,
                queueLength: 0,
                fileList: files,
                lockedFileList: []);

            return Task.FromResult<SearchResponse?>(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при формировании ответа на поиск для {User}", username);
            return Task.FromResult<SearchResponse?>(null);
        }
    }

    /// <summary>Ответ на запрос информации о пользователе.</summary>
    public Task<UserInfo> UserInfoResolver(string username, IPEndPoint endPoint)
    {
        var description = string.IsNullOrWhiteSpace(_config.Soulseek.ShareDescription)
            ? "Music sharing via Telegram bot"
            : _config.Soulseek.ShareDescription;

        return Task.FromResult(new UserInfo(
            description: description,
            uploadSlots: Math.Max(1, _config.Downloads.MaxUploadSlots),
            queueLength: 0,
            hasFreeUploadSlot: true));
    }

    /// <summary>Ответ на просмотр нашей файловой шары.</summary>
    public Task<BrowseResponse> BrowseResponseResolver(string username, IPEndPoint endPoint)
    {
        var directories = ScanSharedDirectories()
            .Select(d => new SlDirectory(d.Key, d.Value))
            .ToList();

        return Task.FromResult(new BrowseResponse(directories));
    }

    /// <summary>Сканирует папку загрузок и группирует аудиофайлы по подпапкам.</summary>
    private ConcurrentDictionary<string, List<SlFile>> ScanSharedDirectories()
    {
        lock (_lock)
        {
            if (!_cache.IsEmpty)
                return _cache;

            var root = Path.GetFullPath(_config.Soulseek.DownloadDirectory);
            var result = new ConcurrentDictionary<string, List<SlFile>>(StringComparer.OrdinalIgnoreCase);

            if (!System.IO.Directory.Exists(root))
                return result;

            foreach (var filePath in System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(filePath);
                if (!ResultScorer.IsAudio(
                        new SlFile(code: 1, filename: filePath, size: new FileInfo(filePath).Length, extension: ext)))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(filePath);
                    var relativeDir = Path.GetRelativePath(root, info.DirectoryName ?? root);

                    // Файл в корне шары: путь внутри шары без префикса "./".
                    var isRoot = relativeDir is "." or "";
                    var sharePath = isRoot ? info.Name : relativeDir.Replace('\\', '/') + "/" + info.Name;

                    var file = new SlFile(
                        code: 1,
                        filename: sharePath,
                        size: info.Length,
                        extension: ext,
                        attributeList: null);

                    var list = result.GetOrAdd(isRoot ? string.Empty : relativeDir, _ => new List<SlFile>());
                    lock (list)
                        list.Add(file);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Не удалось проиндексировать {Path}", filePath);
                }
            }

            _logger.LogInformation("Шара просканирована: {Dirs} папок, {Files} файлов", result.Count, result.Sum(d => d.Value.Count));
            _cache.Clear();
            foreach (var kv in result)
                _cache[kv.Key] = kv.Value;

            return _cache;
        }
    }

    /// <summary>Проверяет, что файл не попадает под запрещённые фразы и совпадает с запросом.</summary>
    private bool MatchesQuery(string filename, SearchQuery query)
    {
        if (_excludedPhrases.Any(p => filename.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Согласие по всем терминам запроса (как это делает slskd).
        return query.Terms.All(term => filename.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
