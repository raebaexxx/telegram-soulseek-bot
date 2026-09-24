using Telegram.Bot.Types.ReplyMarkups;
using MusicBot.Soulseek;

namespace MusicBot.Telegram;

/// <summary>Форматирование сообщений и клавиатур для бота.</summary>
public static class MessageViews
{
    /// <summary>Максимум текста кнопки Telegram — 64 символа.</summary>
    private const int ButtonTextLimit = 64;

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L * 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F2} ГБ",
        >= 1L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} МБ",
        >= 1024 => $"{bytes / 1024.0:F0} КБ",
        _ => $"{bytes} Б",
    };

    public static string FormatSpeed(double bytesPerSec) =>
        bytesPerSec <= 0 ? "—" : $"{bytesPerSec / 1024 / 1024:F1} МБ/с";

    /// <summary>Строка результата: название трека, папка (контекст совпадения) и тех. детали.</summary>
    public static string FormatResultLine(int index, MusicResult r)
    {
        var slot = r.HasFreeUploadSlot ? "✅ слот" : $"⏳ очередь {r.QueueLength}";
        var title = Truncate(GuessTrackTitle(r.Filename), 60);
        var parent = string.IsNullOrEmpty(r.ParentDirectory)
            ? string.Empty
            : $"    📁 {Truncate(r.ParentDirectory, 55)}\n";

        return $"{index}. {title}\n{parent}    {r.FormatLabel} · {FormatBytes(r.Size)} · {FormatSpeed(r.UploadSpeed)} · {slot}";
    }

    /// <summary>Строка альбома в списке результатов.</summary>
    public static string FormatAlbumLine(int index, AlbumResult a)
    {
        var artist = string.IsNullOrWhiteSpace(a.Parent) ? "" : $"{a.Parent} — ";
        var year = a.Year is { } y ? $" ({y})" : "";
        var slot = a.HasFreeUploadSlot ? "✅" : $"⏳{a.QueueLength}";

        return $"{index}. 📁 {Truncate(artist + a.Title + year, 55)}\n" +
               $"    {a.TrackCount} треков · {a.FormatSummary} · {FormatBytes(a.TotalSize)} · {FormatSpeed(a.UploadSpeed)} {slot}\n" +
               $"    пир: {a.Username}";
    }

    /// <summary>
    /// Текст сообщения: сначала альбомы (обычно то, что ищут), потом отдельные треки.
    /// </summary>
    public static string BuildSearchMessage(string query, SoulseekService.SearchResults results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"🔎 «{query}» — ответов пиров: {results.ResponseCount}");

        if (results.Albums.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("АЛЬБОМЫ:");
            for (var i = 0; i < results.Albums.Count; i++)
                sb.AppendLine(FormatAlbumLine(i + 1, results.Albums[i]));
        }

        if (results.Tracks.Count > 0)
        {
            if (results.Albums.Count > 0)
            {
                sb.AppendLine();
            }

            sb.AppendLine("ОТДЕЛЬНЫЕ ТРЕКИ:");
            for (var i = 0; i < results.Tracks.Count; i++)
                sb.AppendLine(FormatResultLine(i + 1, results.Tracks[i]));
        }

        sb.AppendLine();
        sb.Append("Нажми номер, чтобы скачать 👇");
        return TruncateMessage(sb.ToString());
    }

    /// <summary>Клавиатура: альбомы (📁) и треки, номера совпадают с текстом сообщения.</summary>
    public static InlineKeyboardMarkup BuildSearchKeyboard(string searchId, SoulseekService.SearchResults results)
    {
        var rows = new List<IEnumerable<InlineKeyboardButton>>();

        for (var i = 0; i < results.Albums.Count; i++)
        {
            var a = results.Albums[i];
            var artist = string.IsNullOrWhiteSpace(a.Parent) ? "" : $"{a.Parent} — ";
            var label = $"📁 {i + 1}. {artist}{a.Title}";
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(Truncate(label, ButtonTextLimit), $"dl|{searchId}|a{i}") });
        }

        foreach (var (result, i) in results.Tracks.Select((r, i) => (r, i + 1)))
        {
            var title = GuessTrackTitle(result.Filename);
            var label = $"{i}. {title}";
            var suffix = $" · {result.FormatLabel} · {FormatBytes(result.Size)}";

            if (label.Length + suffix.Length <= ButtonTextLimit)
                label += suffix;

            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(Truncate(label, ButtonTextLimit), $"dl|{searchId}|t{i - 1}") });
        }

        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>Человекочитаемое «сколько осталось ждать».</summary>
    private static string FormatEta(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Min(seconds, 24 * 3600));

        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours} ч {ts.Minutes} мин"
            : ts.TotalMinutes >= 1
                ? $"{(int)ts.TotalMinutes} мин"
                : $"{(int)ts.TotalSeconds} с";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    /// <summary>Telegram не принимает сообщения длиннее 4096 символов.</summary>
    private static string TruncateMessage(string text) =>
        text.Length <= 4000 ? text : text[..4000] + "\n…(список обрезан)";

    /// <summary>Строка прогресса скачивания.</summary>
    public static string BuildProgressMessage(MusicResult r, TransferProgress p) =>
        $"⬇️ {r.Filename}\n" +
        $"{p.PercentComplete:F1}% из {FormatBytes(p.TotalBytes)} · " +
        $"{FormatSpeed(p.AverageSpeed)} · осталось {p.RemainingTime:hh\\:mm\\:ss}";

    /// <summary>Строка прогресса скачивания альбома.</summary>
    public static string BuildAlbumProgressMessage(AlbumResult a, AlbumProgress p)
    {
        var percent = p.TotalBytes > 0 ? (double)p.BytesTransferred / p.TotalBytes * 100 : 0;
        var parallel = p.TracksStarted > p.TracksDone
            ? $", в работе {p.TracksStarted - p.TracksDone}"
            : "";

        // Оценка времени до конца по текущей скорости.
        var eta = string.Empty;
        if (p.AverageSpeed > 0 && p.TotalBytes > p.BytesTransferred)
        {
            var seconds = (p.TotalBytes - p.BytesTransferred) / p.AverageSpeed;
            eta = seconds > 5 ? $" · осталось ≈ {FormatEta(seconds)}" : string.Empty;
        }

        return $"📦 {a.Title}\n" +
               $"Готово треков: {p.TracksDone} из {p.TotalTracks}{parallel} · {percent:F1}%\n" +
               $"{FormatBytes(p.BytesTransferred)} из {FormatBytes(p.TotalBytes)} · суммарно {FormatSpeed(p.AverageSpeed)}{eta}";
    }

    /// <summary>Пробует вытащить «Artist - Title» из имени файла.</summary>
    public static string GuessTrackTitle(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(MusicResult.GetShortName(filename));

        // Убираем типовые префиксы нумерации: "01 ", "01.", "01_", "0101 - ".
        var cleaned = System.Text.RegularExpressions.Regex.Replace(stem, @"^\s*\d{1,4}\s*[-._)]\s*", "");

        return string.IsNullOrWhiteSpace(cleaned) ? stem : cleaned;
    }
}
