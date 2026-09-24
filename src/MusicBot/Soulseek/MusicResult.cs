using Soulseek;
using SlFile = Soulseek.File;

namespace MusicBot.Soulseek;

/// <summary>Один кандидат на скачивание: файл конкретного пира.</summary>
/// <param name="Username">Ник пира.</param>
/// <param name="RemoteFilename">Полный путь на стороне пира.</param>
/// <param name="Filename">Короткое имя файла.</param>
/// <param name="Extension">Расширение в нижнем регистре, с точкой.</param>
/// <param name="Size">Размер в байтах.</param>
/// <param name="BitRate">Битрейт, если известен.</param>
/// <param name="BitDepth">Битовая глубина (для lossless), если известна.</param>
/// <param name="UploadSpeed">Скорость пира, байт/с.</param>
/// <param name="HasFreeUploadSlot">Есть ли у пира свободный слот.</param>
/// <param name="QueueLength">Длина очереди пира.</param>
/// <param name="Score">Итоговый балл сортировки.</param>
public sealed record MusicResult(
    string Username,
    string RemoteFilename,
    string Filename,
    string Extension,
    long Size,
    int? BitRate,
    int? BitDepth,
    int UploadSpeed,
    bool HasFreeUploadSlot,
    int QueueLength,
    double Score)
{
    /// <summary>
    /// Тот же трек у других пиров — резервные источники, если основной пир завис.
    /// Заполняется в <see cref="ResultScorer.Aggregate"/>.
    /// </summary>
    public IReadOnlyList<MusicResult> Alternatives { get; set; } = [];

    public static MusicResult From(SearchResponse response, SlFile file, double score)
    {
        var ext = string.IsNullOrEmpty(file.Extension)
            ? Path.GetExtension(file.Filename).ToLowerInvariant()
            : file.Extension.ToLowerInvariant();

        return new MusicResult(
            Username: response.Username,
            RemoteFilename: file.Filename,
            Filename: GetShortName(file.Filename),
            Extension: ext,
            Size: file.Size,
            BitRate: file.BitRate,
            BitDepth: file.BitDepth,
            UploadSpeed: response.UploadSpeed,
            HasFreeUploadSlot: response.HasFreeUploadSlot,
            QueueLength: response.QueueLength,
            Score: score);
    }

    /// <summary>
    /// Имя файла без пути пира. Пути Soulseek используют обратный слэш,
    /// а на Linux это обычный символ, поэтому режем вручную по обоим разделителям.
    /// </summary>
    public static string GetShortName(string remotePath) =>
        remotePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? remotePath;

    /// <summary>
    /// Последние папки пути файла — нужны пользователю, чтобы понять контекст:
    /// совпадение поиска часто найдено именно в имени папки, а не в имени трека.
    /// </summary>
    public string ParentDirectory
    {
        get
        {
            var parts = RemoteFilename
                .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            if (parts.Count <= 1)
                return string.Empty;

            parts.RemoveAt(parts.Count - 1);
            return string.Join(" / ", parts.TakeLast(2));
        }
    }

    public bool IsLossless => Extension is ".flac" or ".wav" or ".aiff" or ".ape" or ".alac";

    /// <summary>
    /// Короткое человекочитаемое описание формата:
    /// для lossless — битовая глубина («FLAC 24bit»), для lossy — битрейт («MP3 320»).
    /// </newString>
    public string FormatLabel
    {
        get
        {
            var baseLabel = Extension.TrimStart('.').ToUpperInvariant();

            if (IsLossless)
                return BitDepth is > 16 ? $"{baseLabel} {BitDepth}bit" : baseLabel;

            // В сети встречаются мусорные значения битрейта (903, 2852 и т.п.) —
            // показываем только правдоподобные.
            return BitRate is >= 32 and <= 500 ? $"{baseLabel} {BitRate}" : baseLabel;
        }
    }
}
