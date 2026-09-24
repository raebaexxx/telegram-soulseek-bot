#!/usr/bin/env bash
# Запуск локального Telegram Bot API сервера.
# Нужен, чтобы снимать лимит 50 МБ на файл: локальный сервер принимает до 2 ГБ.
#
# Требования:
#   1. api_id и api_hash с https://my.telegram.org (Tools → API development tools)
#   2. telegram-bot-api собран из исходников (см. инструкцию в README)
#
# Где вводить api_id/api_hash (любой из способов, по приоритету):
#   1. Файл scripts/telegram-bot-api.env:
#        TELEGRAM_API_ID=12345678
#        TELEGRAM_API_HASH=0123456789abcdef0123456789abcdef
#   2. Переменные окружения при запуске:
#        TELEGRAM_API_ID=12345678 TELEGRAM_API_HASH=... ./scripts/run-telegram-bot-api.sh
#
# Запуск:  ./scripts/run-telegram-bot-api.sh
# Остановить: Ctrl+C

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Подхватываем файл с ключами, если он есть (создавать нужно только один раз).
if [[ -f "$SCRIPT_DIR/telegram-bot-api.env" ]]; then
    set -a
    # shellcheck disable=SC1091
    source "$SCRIPT_DIR/telegram-bot-api.env"
    set +a
fi

API_ID="${TELEGRAM_API_ID:-}"
API_HASH="${TELEGRAM_API_HASH:-}"
LOCAL_DIR="${TELEGRAM_BOT_API_DIR:-$HOME/bin}"
DATA_DIR="${TELEGRAM_BOT_API_DATA_DIR:-$HOME/.local/share/telegram-bot-api}"
PORT="${TELEGRAM_BOT_API_PORT:-8081}"

if [[ -z "$API_ID" || -z "$API_HASH" ]]; then
    cat <<'EOF'
Не заданы TELEGRAM_API_ID / TELEGRAM_API_HASH.

Как получить:
  1. Открой https://my.telegram.org → Sign in (телефон)
  2. Tools → API development tools → создай приложение
  3. Скопируй api_id и api_hash

Куда вписать — двумя способами:

  А) Создай файл scripts/telegram-bot-api.env с двумя строками:
       TELEGRAM_API_ID=12345678
       TELEGRAM_API_HASH=0123456789abcdef0123456789abcdef

  Б) Или передай при запуске:
       TELEGRAM_API_ID=12345678 TELEGRAM_API_HASH=... ./scripts/run-telegram-bot-api.sh
EOF
    exit 1
fi

if [[ ! -x "$LOCAL_DIR/telegram-bot-api" ]]; then
    echo "Не найден $LOCAL_DIR/telegram-bot-api. Собери его по инструкции в README (раздел «Локальный Bot API сервер»)."
    exit 1
fi

# Не даём поднять второй экземпляр: он упал бы с крэшем и дампом стека.
if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then
    exec 3>&- 2>/dev/null || true
    echo "Порт $PORT уже занят — похоже, сервер уже запущен."
    echo "Посмотреть: ss -ltnp | grep $PORT    Остановить: pkill -f telegram-bot-api"
    exit 1
fi

mkdir -p "$DATA_DIR"

echo "Запускаю локальный Bot API сервер на порту $PORT (данные в $DATA_DIR)…"
exec "$LOCAL_DIR/telegram-bot-api" \
    --local \
    --api-id "$API_ID" \
    --api-hash "$API_HASH" \
    --dir "$DATA_DIR" \
    --http-port "$PORT" \
    --http-ip-address 127.0.0.1
