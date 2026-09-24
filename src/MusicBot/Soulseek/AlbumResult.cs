using Soulseek;
using SlFile = Soulseek.File;

namespace MusicBot.Soulseek;

/// <summary>Один файл внутри альбома (папки пира).</summary>
/// <param name="RemotePath">Полный путь файла на стороне пира.</param>
/// <param name="Filename">Имя файла с расширением.</param>
/// <param name="Size">Размер в байтах.</param>
/// <param name="BitRate">Битрейт, если указан пиром.</param>
/// <param name="BitDepth">Битовая глубина, если указана.</param>
/// <param name="Extension">Расширение в нижнем регистре, с точкой.</param>
public sealed record AlbumFile(
    string RemotePath,
    string Filename,
    long Size,
    int? BitRate,
    int? BitDepth,
    string Extension)
{
    public static AlbumFile From(SlFile file)
    {
        var ext = string.IsNullOrEmpty(file.Extension)
            ? Path.GetExtension(file.Filename).ToLowerInvariant()
            : file.Extension.ToLowerInvariant();

        return new AlbumFile(
            RemotePath: file.Filename,
            Filename: MusicResult.GetShortName(file.Filename),
            Size: file.Size,
            BitRate: file.BitRate,
            BitDepth: file.BitDepth,
            Extension: ext);
    }

    /// <summary>Номер трека из имени файла («03 - …» → 3), если он угадывается.</summary>
    public int? TrackNumber
    {
        get
        {
            var m = System.Text.RegularExpressions.Regex.Match(Filename, @"^\s*(\d{1,3})\s*[-._)]");
            return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
        }
    }
}

/// <summary>Альбом = папка одного пира с несколькими аудиофайлами.</summary>
public sealed class AlbumResult
{
    public required string Username { get; init; }

    /// <summary>Путь папки на стороне пира (без завершающего разделителя).</summary>
    public required string Directory { get; init; }

    /// <summary>Название папки — обычно это album title.</summary>
    public required string Title { get; init; }

    /// <summary>Родительская папка — обычно artist.</summary>
    public string Parent { get; init; } = string.Empty;

    public required IReadOnlyList<AlbumFile> Files { get; init; }

    // Данные пира (для скоринга и показа):
    public int UploadSpeed { get; init; }
    public bool HasFreeUploadSlot { get; init; }
    public int QueueLength { get; init; }
    public double Score { get; init; }

    public int TrackCount => Files.Count;
    public long TotalSize => Files.Sum(f => f.Size);

    /// <summary>Треки по порядку: сначала по номеру из имени, потом по имени файла.</summary>
    public IReadOnlyList<AlbumFile> OrderedFiles =>
        Files.OrderBy(f => f.TrackNumber ?? int.MaxValue)
             .ThenBy(f => f.Filename, StringComparer.OrdinalIgnoreCase)
             .ToList();

    /// <summary>Краткое описание форматов альбома: «FLAC 24bit» или «MP3 320 + FLAC».</summary>
    public string FormatSummary
    {
        get
        {
            var groups = Files
                .GroupBy(f => f.Extension.TrimStart('.').ToUpperInvariant())
                .Select(g =>
                {
                    var depth = g.Max(f => f.BitDepth ?? 0);
                    var lossless = g.Key is "FLAC" or "WAV" or "APE" or "ALAC" or "AIFF";
                    if (lossless)
                        return depth > 16 ? $"{g.Key} {depth}bit" : g.Key;
                    var br = g.Max(f => f.BitRate ?? 0);
                    return br is >= 32 and <= 500 ? $"{g.Key} {br}" : g.Key;
                })
                .OrderByDescending(s => s.StartsWith("FLAC") ? 3 : s.StartsWith("WAV") ? 2 : 1)
                .ToList();

            return string.Join(" + ", groups);
        }
    }

    /// <summary>Год из названия папки, если нашли («(2019)» / «[2019]» / «2019 -»).</summary>
    public int? Year
    {
        get
        {
            var m = System.Text.RegularExpressions.Regex.Match(Title, @"[\(\[]?((?:19|20)\d{2})[\)\]]?");
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }
    }

    public static AlbumResult? FromDirectory(
        string username,
        string directory,
        IEnumerable<SlFile> files,
        SearchResponse response,
        double score)
    {
        var list = files.Select(AlbumFile.From).ToList();
        if (list.Count == 0)
            return null;

        var normalized = directory.TrimEnd('\\', '/');
        var parts = normalized.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        return new AlbumResult
        {
            Username = username,
            Directory = normalized,
            Title = parts.Length > 0 ? parts[^1] : normalized,
            Parent = parts.Length > 1 ? parts[^2] : string.Empty,
            Files = list,
            UploadSpeed = response.UploadSpeed,
            HasFreeUploadSlot = response.HasFreeUploadSlot,
            QueueLength = response.QueueLength,
            Score = score,
        };
    }
}
