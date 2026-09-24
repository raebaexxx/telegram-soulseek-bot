namespace MusicBot.Configuration;

/// <summary>Привязка секций appsettings.json.</summary>
public sealed class BotConfig
{
    public const string SectionName = "MusicBot";

    public TelegramOptions Telegram { get; init; } = new();
    public SoulseekOptions Soulseek { get; init; } = new();
    public SearchOptions Search { get; init; } = new();
    public DownloadOptions Downloads { get; init; } = new();
    public AlbumOptions Albums { get; init; } = new();

    public sealed class TelegramOptions
    {
        /// <summary>Токен бота от @BotFather.</summary>
        public string Token { get; init; } = string.Empty;

        /// <summary>Telegram user id владельца бота; остальные пользователи игнорируются.</summary>
        public long AdminUserId { get; init; }

        /// <summary>
        /// База локального Bot API сервера (например http://localhost:8081/bot).
        /// Пустая строка = официальный сервер (лимит 50 МБ на файл).
        /// </summary>
        public string LocalApiBaseUrl { get; init; } = string.Empty;

        public bool UseLocalApiServer => !string.IsNullOrWhiteSpace(LocalApiBaseUrl);
    }

    public sealed class SoulseekOptions
    {
        public string Username { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;

        /// <summary>Порт для входящих соединений пиров (желательно пробросить на роутере).</summary>
        public int ListenPort { get; init; } = 50000;

        /// <summary>Папка, куда складываются файлы; она же объявляется как share.</summary>
        public string DownloadDirectory { get; init; } = "downloads";

        /// <summary>Отвечать на поиски других пиров содержимым папки загрузок.</summary>
        public bool ShareDownloads { get; init; } = true;

        public string ShareDescription { get; init; } = string.Empty;
    }

    public sealed class SearchOptions
    {
        public int TimeoutSeconds { get; init; } = 10;
        public int ResponseLimit { get; init; } = 100;
        public int MaxResultsShown { get; init; } = 8;
    }

    public sealed class DownloadOptions
    {
        /// <summary>Параллельных скачиваний одновременно.</summary>
        public int MaxConcurrent { get; init; } = 2;

        /// <summary>Upload-слоты, отдаваемые сети (обязательно &gt; 0, иначе нас считают леечером).</summary>
        public int MaxUploadSlots { get; init; } = 2;

        /// <summary>Оставлять скачанные файлы на диске (нужны для шаринга).</summary>
        public bool KeepDownloadedFiles { get; init; } = true;

        /// <summary>
        /// Сколько секунд не идёт передача, прежде чем бот бросит этого пира и
        /// переключится на другого. Помогает, когда пир за NAT не может до нас достучаться.
        /// </summary>
        public int StallTimeoutSeconds { get; init; } = 20;

        /// <summary>
        /// Предел объёма папки загрузок в ГБ. Когда превышен — бот удаляет самые старые файлы.
        /// 0 = неограниченно (тогда диск рано или поздно кончится).
        /// </summary>
        public int MaxDiskUsageGb { get; init; } = 10;

        /// <summary>
        /// Сколько свежих файлов не трогать при уборке. Нужно, чтобы папка не опустела:
        /// с пустой шарой сеть Soulseek считает бота «леечером» и режет скорость.
        /// </summary>
        public int MinFilesToKeep { get; init; } = 10;

        /// <summary>Как часто проверять объём папки загрузок (минут).</summary>
        public int CleanupIntervalMinutes { get; init; } = 10;

        /// <summary>Файлы моложе этого возраста не удаляем (минут) — вдруг они ещё отправляются.</summary>
        public int MinFileAgeMinutes { get; init; } = 15;
    }

    public sealed class AlbumOptions
    {
        /// <summary>Минимум файлов в папке пира, чтобы она считалась альбомом.</summary>
        public int MinFilesForAlbum { get; init; } = 3;

        /// <summary>Сколько альбомов показывать в результатах поиска.</summary>
        public int MaxAlbumsShown { get; init; } = 5;

        /// <summary>Один альбом с каждого пира (иначе топ занимает один и тот же альбом у 5 пиров).</summary>
        public int MaxAlbumsPerPeer { get; init; } = 2;

        /// <summary>Максимум треков в одном альбоме (защита от «альбомов» на 500 файлов).</summary>
        public int MaxTracksPerAlbum { get; init; } = 60;

        /// <summary>
        /// Сколько треков альбома качать одновременно. По умолчанию 1: треки альбома
        /// обычно отдаёт один пир, и параллельные запросы к нему только упираются
        /// в его upload-слоты (остальные запросы виснут в таймаут).
        /// Поставь 2-3, если пир явно имеет несколько свободных слотов.
        /// </summary>
        public int ParallelTracks { get; init; } = 1;

        /// <summary>Как отдавать скачанный альбом: individual (треками), zip или both.</summary>
        public string PackageAs { get; init; } = "individual";

        /// <summary>
        /// Если альбом не влезает в zip этого размера, отправляем треки отдельными сообщениями.
        /// Локальный Bot API сервер принимает до 2 ГБ.
        /// </summary>
        public int MaxZipSizeMb { get; init; } = 1800;
    }
}
