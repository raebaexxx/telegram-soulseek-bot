using System.IO.Compression;
using MusicBot.Soulseek;

namespace MusicBot.Downloads;

/// <summary>Готовит скачанный альбом к отправке: zip-архив или отдельные файлы.</summary>
public static class AlbumPackager
{
    /// <summary>Расширения картинок (обложки) — их кладём в архив, но не считаем треками.</summary>
    private static readonly HashSet<string> CoverExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp",
    };

    /// <summary>Уже сжатые форматы: zip почти не уменьшает их размер.</summary>
    private static readonly HashSet<string> LosslessExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".wav", ".ape", ".alac", ".aiff",
    };

    /// <summary>Безопасное имя папки архива.</summary>
    public static string BuildArchiveName(AlbumResult album, DateTime now)
    {
        var artist = string.IsNullOrWhiteSpace(album.Parent) ? album.Username : album.Parent;
        var year = album.Year is { } y ? $" ({y})" : string.Empty;
        return Sanitize($"{artist} - {album.Title}{year} [{now:yyyy-MM-dd HH-mm}].zip");
    }

    /// <summary>
    /// Упаковывает файлы в zip. Возвращает null, если архив получится больше лимита —
    /// тогда вызывающий код отправит треки отдельными сообщениями.
    /// </summary>
    public static async Task<string?> TryCreateZipAsync(
        IReadOnlyList<string> files,
        string archivePath,
        long maxZipBytes,
        CancellationToken ct)
    {
        var total = files.Sum(f => SafeSize(f));
        if (total > maxZipBytes)
        {
            await Task.CompletedTask;
            return null;
        }

        if (File.Exists(archivePath))
            File.Delete(archivePath);

        await using var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        // FLAC уже сжат — обычный deflate не сожмёт его и зря потратит CPU.
        var lossless = files.Count(f => LosslessExtensions.Contains(Path.GetExtension(f)));
        var level = lossless * 2 >= files.Count ? CompressionLevel.Fastest : CompressionLevel.Optimal;

        // В режиме Create нельзя спрашивать archive.GetEntry — держим имена сами.
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var entryName = Path.GetFileName(file);
            if (entryName.Length == 0)
                continue;

            // Не плодим одинаковые имена внутри архива.
            var unique = entryName;
            var i = 1;
            while (!usedNames.Add(unique))
            {
                var stem = Path.GetFileNameWithoutExtension(entryName);
                var ext = Path.GetExtension(entryName);
                unique = $"{stem} ({i++}){ext}";
            }

            await using var entryStream = archive.CreateEntry(unique, level).Open();
            await using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            await fileStream.CopyToAsync(entryStream, ct);
        }

        return archivePath;
    }

    /// <summary>Список треков (без обложек) для отправки по одному.</summary>
    public static IReadOnlyList<string> TracksOnly(IEnumerable<string> files) =>
        files.Where(f => !CoverExtensions.Contains(Path.GetExtension(f))).ToList();

    /// <summary>Обложки альбома, если они были в папке пира.</summary>
    public static IReadOnlyList<string> CoversOnly(IEnumerable<string> files) =>
        files.Where(f => CoverExtensions.Contains(Path.GetExtension(f))).ToList();

    private static long SafeSize(string path)
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

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !char.IsControl(c) && !invalid.Contains(c)).ToArray()).Trim();

        const int maxBytes = 200;
        while (System.Text.Encoding.UTF8.GetByteCount(cleaned) > maxBytes && cleaned.Length > 1)
            cleaned = cleaned[..^1];

        return cleaned;
    }
}
