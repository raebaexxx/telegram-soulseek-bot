using TagLib;

namespace MusicBot.Telegram;

/// <summary>Метаданные трека: из тегов файла, с запасным вариантом по имени.</summary>
/// <param name="Title">Название трека.</param>
/// <param name="Performer">Исполнитель.</param>
/// <param name="Album">Альбом, если тег есть.</param>
/// <param name="DurationSeconds">Длительность в секундах, если удалось определить.</param>
/// <param name="CoverBytes">Встроенная обложка, если есть.</param>
/// <param name="CoverMimeType">MIME-тип обложки.</param>
public sealed record TrackMetadata(
    string Title,
    string Performer,
    string? Album,
    int? DurationSeconds,
    byte[]? CoverBytes,
    string? CoverMimeType);

/// <summary>Читает теги аудиофайла (ID3/Vorbis/FLAC) для корректных тегов в Telegram.</summary>
public static class TrackMetadataReader
{
    /// <summary>Максимальный размер обложки, который мы готовы встроить в сообщение (Telegram — до 200 КБ для превью).</summary>
    private const int MaxCoverBytes = 200 * 1024;

    public static TrackMetadata Read(string localPath, string fallbackTitle)
    {
        string? title = null;
        string? performer = null;
        string? album = null;
        int? duration = null;
        byte[]? cover = null;
        string? coverMime = null;

        try
        {
            using var file = TagLib.File.Create(localPath);
            var tag = file.Tag;

            if (!string.IsNullOrWhiteSpace(tag.Title))
                title = tag.Title.Trim();

            if (tag.Performers is { Length: > 0 } performers && !string.IsNullOrWhiteSpace(performers[0]))
                performer = performers[0].Trim();

            if (!string.IsNullOrWhiteSpace(tag.Album))
                album = tag.Album.Trim();

            if (file.Properties.Duration > TimeSpan.Zero)
                duration = (int)Math.Round(file.Properties.Duration.TotalSeconds);

            if (tag.Pictures is { Length: > 0 } pictures)
            {
                var picture = pictures[0];
                var data = picture.Data?.Data;

                if (data is { Length: > 0 } && data.Length <= MaxCoverBytes)
                {
                    cover = data;
                    coverMime = string.IsNullOrWhiteSpace(picture.MimeType) ? "image/jpeg" : picture.MimeType;
                }
            }
        }
        catch
        {
            // Битые теги не должны ломать отправку — отправим как есть.
        }

        // Если тегов нет (часто бывает на Soulseek) — угадываем по имени файла.
        title ??= fallbackTitle;

        if (string.IsNullOrWhiteSpace(performer))
        {
            var guessed = GuessArtistFromTitle(title);
            performer = guessed ?? "Unknown Artist";
        }

        return new TrackMetadata(title!, performer!, album, duration, cover, coverMime);
    }

    /// <summary>Из «Artist - Song» делает исполнителя и название.</summary>
    private static string? GuessArtistFromTitle(string title)
    {
        var parts = title.Split(" - ", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return parts[0].Trim();

        return null;
    }
}
