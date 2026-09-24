using Soulseek;
using SlFile = Soulseek.File;

namespace MusicBot.Soulseek;

/// <summary>
/// Оценивает результаты поиска и сортирует их:
/// сначала качество (lossless &gt; 320kbps &gt; остальное), затем скорость и доступность пира.
/// </summary>
public static class ResultScorer
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".m4a", ".aac", ".wav", ".ape", ".wma", ".opus", ".aiff", ".alac", ".wv",
    };

    /// <summary>Минимальный размер аудиофайла, ниже которого кандидаты отфильтровываются (2 МБ).</summary>
    private const long MinFileSizeBytes = 2 * 1024 * 1024;

    /// <summary>Не больше этого числа файлов с одного пира — иначе топ занимает один альбом одного пользователя.</summary>
    private const int MaxResultsPerPeer = 3;

    /// <summary>Сколько запасных пиров держим для каждого трека (переключение при зависании).</summary>
    private const int MaxAlternatives = 3;

    public static bool IsAudio(SlFile file) =>
        AudioExtensions.Contains(Path.GetExtension(file.Filename));

    public static double Score(SearchResponse response, SlFile file)
    {
        var ext = Path.GetExtension(file.Filename).ToLowerInvariant();

        // Базовый вес формата: lossless сильно выше, mp3 зависит от битрейта.
        double format = ext switch
        {
            ".flac" => 120,
            ".wav" => 105,
            ".ape" or ".alac" or ".wv" => 100,
            ".aiff" => 95,
            ".ogg" or ".opus" => 80,
            ".m4a" or ".aac" => 70,
            ".mp3" => 30,
            _ => 10,
        };

        // Бонус за битрейт (актуально для mp3/m4a/ogg): до +30 за 320kbps.
        if (file.BitRate is > 0)
            format += Math.Min(30, file.BitRate.Value / 320.0 * 30);

        // Бонус за hi-res lossless.
        if (file.BitDepth is >= 24)
            format += 10;

        // Скорость пира: лог-шкала, до +30 за 100 МБ/с и выше.
        double speed = response.UploadSpeed <= 0
            ? 0
            : 30 * Math.Clamp(Math.Log10(response.UploadSpeed + 1) / Math.Log10(100_000_000), 0, 1);

        // Доступность: свободный слот — большой плюс, очередь — минус.
        double availability = (response.HasFreeUploadSlot ? 25 : 0) - Math.Min(20, response.QueueLength * 2);

        return format + speed + availability;
    }

    /// <summary>Собирает, дедуплицирует и сортирует ответы поиска.</summary>
    public static List<MusicResult> Aggregate(IEnumerable<SearchResponse> responses, int maxResults)
    {
        var candidates = new List<(MusicResult Result, double Score)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var response in responses)
        {
            foreach (var file in response.Files)
            {
                if (file.Size < MinFileSizeBytes || !IsAudio(file))
                    continue;

                if (!seen.Add($"{response.Username}|{file.Filename}"))
                    continue;

                var score = Score(response, file);
                candidates.Add((MusicResult.From(response, file, score), score));
            }
        }

        var top = candidates
            .OrderByDescending(c => c.Score)
            .GroupBy(c => c.Result.Username, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Take(MaxResultsPerPeer))
            .OrderByDescending(c => c.Score)
            .Take(maxResults)
            .Select(c => c.Result)
            .ToList();

        // К каждому результату приклеиваем тот же трек у других пиров:
        // если выбранный пир не отвечает, бот переключится на запасной.
        var byTrack = candidates
            .GroupBy(c => TrackKey(c.Result))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Score).Select(c => c.Result).ToList());

        foreach (var result in top)
        {
            if (!byTrack.TryGetValue(TrackKey(result), out var same))
                continue;

            var peers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { result.Username };
            var alternatives = same
                .Where(r => peers.Add(r.Username))
                .Take(MaxAlternatives)
                .ToList();

            if (alternatives.Count > 0)
            {
                result.Alternatives = alternatives;
            }
        }

        return top;
    }

    /// <summary>Ключ «это тот же трек»: путь без номера трека и без расширения, в нижнем регистре.</summary>
    private static string TrackKey(MusicResult r)
    {
        var name = MusicResult.GetShortName(r.RemoteFilename);
        var stem = System.Text.RegularExpressions.Regex.Replace(
            Path.GetFileNameWithoutExtension(name), @"^\s*\d{1,4}\s*[-._)]\s*", "");

        return stem.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Группирует ответы поиска по папкам пиров — это и есть альбомы.
    /// Папка считается альбомом, если в ней минимум <paramref name="minFiles"/> аудиофайлов
    /// и её имя содержит все значимые термины запроса (иначе в топ всплывают чужие
    /// альбомы, у которых случайно совпали отдельные треки).
    /// </summary>
    public static List<AlbumResult> AggregateAlbums(
        IEnumerable<SearchResponse> responses,
        string query,
        int minFiles,
        int maxAlbums,
        int maxPerPeer,
        int maxTracksPerAlbum)
    {
        // Значимые термины запроса: без служебных слов и повторов.
        var terms = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();

        // ключ: пир + папка
        var groups = new Dictionary<(string User, string Dir), (SearchResponse Response, List<SlFile> Files)>();

        foreach (var response in responses)
        {
            foreach (var file in response.Files)
            {
                if (!IsAudio(file) || file.Size < MinFileSizeBytes)
                    continue;

                var dir = GetDirectory(file.Filename);
                if (dir.Length == 0)
                    continue; // файл в корне шары — альбомом не считаем

                var key = (response.Username.ToLowerInvariant(), dir.ToLowerInvariant());
                if (!groups.TryGetValue(key, out var g))
                {
                    g = (response, new List<SlFile>());
                    groups[key] = g;
                }

                g.Files.Add(file);
            }
        }

        var albums = new List<AlbumResult>();

        foreach (var ((_, _), (response, files)) in groups)
        {
            if (files.Count < minFiles)
                continue;

            var dir = GetDirectory(files[0].Filename);

            // Имя папки должно объяснять, почему альбом нашёлся.
            if (terms.Count > 0 && !terms.All(term => dir.Contains(term, StringComparison.OrdinalIgnoreCase)))
                continue;

            var album = AlbumResult.FromDirectory(response.Username, dir, files.Take(maxTracksPerAlbum), response, 0);
            if (album is null)
                continue;

            // Скор: качество + полнота + доступность пира.
            var quality = files.Average(f => FormatWeight(f));
            var completeness = Math.Min(25, files.Count * 2.5);
            var speed = response.UploadSpeed <= 0
                ? 0
                : 25 * Math.Clamp(Math.Log10(response.UploadSpeed + 1) / Math.Log10(100_000_000), 0, 1);
            var availability = (response.HasFreeUploadSlot ? 20 : 0) - Math.Min(15, response.QueueLength * 2);

            var scored = new AlbumResult
            {
                Username = album.Username,
                Directory = album.Directory,
                Title = album.Title,
                Parent = album.Parent,
                Files = album.Files,
                UploadSpeed = album.UploadSpeed,
                HasFreeUploadSlot = album.HasFreeUploadSlot,
                QueueLength = album.QueueLength,
                Score = quality + completeness + speed + availability,
            };

            albums.Add(scored);
        }

        // Одинаковый альбом у одного пира (например «Album» и «Album (2019)») — оставляем лучший.
        var deduped = albums
            .GroupBy(a => (a.Username.ToLowerInvariant(), a.Title.ToLowerInvariant()))
            .Select(g => g.OrderByDescending(a => a.Score).First())
            .ToList();

        return deduped
            .OrderByDescending(a => a.Score)
            .GroupBy(a => a.Username, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Take(Math.Max(1, maxPerPeer)))
            .OrderByDescending(a => a.Score)
            .Take(maxAlbums)
            .ToList();
    }

    /// <summary>Вес формата файла (тот же, что в <see cref="Score"/>, но без скорости и слотов).</summary>
    private static double FormatWeight(SlFile file)
    {
        var ext = Path.GetExtension(file.Filename).ToLowerInvariant();

        double weight = ext switch
        {
            ".flac" => 120,
            ".wav" => 105,
            ".ape" or ".alac" or ".wv" => 100,
            ".aiff" => 95,
            ".ogg" or ".opus" => 80,
            ".m4a" or ".aac" => 70,
            ".mp3" => 30,
            _ => 10,
        };

        if (file.BitRate is > 0)
            weight += Math.Min(30, file.BitRate.Value / 320.0 * 30);

        if (file.BitDepth is >= 24)
            weight += 10;

        return weight;
    }

    /// <summary>Папка файла в пути пира (пути Soulseek используют обратный слэш).</summary>
    public static string GetDirectory(string remotePath)
    {
        var normalized = remotePath.Replace('/', '\\');
        var idx = normalized.LastIndexOf('\\');
        return idx <= 0 ? string.Empty : normalized[..idx];
    }
}
