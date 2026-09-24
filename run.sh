#!/usr/bin/env bash
# Единый запуск MusicBot: локальный Telegram Bot API сервер + сам бот.
#
#   ./run.sh          — запустить всё (обычный режим)
#   ./run.sh stop     — остановить всё
#
# Ctrl+C останавливает оба процесса.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOG_DIR="$ROOT/logs"
BOT_DIR="$ROOT/src/MusicBot"
BOT_API_SCRIPT="$ROOT/scripts/run-telegram-bot-api.sh"
PID_FILE="$LOG_DIR/run.pid"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

mkdir -p "$LOG_DIR"

# ---------------------------------------------------------------- остановка

# Ищем ТОЛЬКО процессы, чья командная строка целиком совпадает с нашими бинарниками.
# Якоря ^…$ обязательны: без них pkill -f убивает ещё и любую оболочку,
# в чьей команде встречается эта строка (в том числе сам вызов ./run.sh stop).
bot_processes() {
    pgrep -f "^${BOT_DIR}/bin/Release/net8.0/MusicBot$" 2>/dev/null
}

api_dir() {
    local dir=""
    if [[ -f "$ROOT/scripts/telegram-bot-api.env" ]]; then
        dir=$(grep -E '^TELEGRAM_BOT_API_DIR=' "$ROOT/scripts/telegram-bot-api.env" | head -1 | cut -d= -f2-)
        dir=${dir//\$HOME/$HOME}
    fi
    echo "${dir:-$HOME/bin}"
}

api_processes() {
    pgrep -f "^$(api_dir)/telegram-bot-api " 2>/dev/null
}

stop_all() {
    local found=0 pids

    for getter in bot_processes api_processes; do
        pids=$($getter)
        if [[ -n "${pids// /}" ]]; then
            found=1
            # shellcheck disable=SC2086
            kill $pids 2>/dev/null
        fi
    done

    # Ждём мягкого завершения.
    for _ in $(seq 1 10); do
        pids="$(bot_processes)$(api_processes)"
        [[ -z "${pids// /}" ]] && break
        sleep 0.5
    done

    # Кто выжил — того принудительно.
    pids="$(bot_processes)$(api_processes)"
    if [[ -n "${pids// /}" ]]; then
        # shellcheck disable=SC2086
        kill -9 $pids 2>/dev/null
    fi

    rm -f "$PID_FILE"

    if [[ $found -eq 1 ]]; then
        echo "Остановлено."
    else
        echo "Нечего останавливать — процессы не найдены."
    fi
}

# ---------------------------------------------------------------- проверки

port_busy() {
    (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null && { exec 3>&- 2>/dev/null; return 0; } || return 1
}

check_ports_free() {
    local busy=""
    port_busy 8081 && busy+=" 8081 (локальный Bot API)\n"
    port_busy 50000 && busy+=" 50000 (слушатель Soulseek)\n"

    if [[ -n "$busy" ]]; then
        echo "Занятые порты:"
        printf "$busy"
        echo
        echo "Похоже, MusicBot уже запущен. Остановить:  ./run.sh stop"
        exit 1
    fi
}

# Нужен ли локальный Bot API сервер? Смотрим LocalApiBaseUrl в конфиге.
needs_local_api() {
    local base
    base=$(python3 -c "import json;print(json.load(open('$BOT_DIR/appsettings.json'))['Telegram']['LocalApiBaseUrl'])" 2>/dev/null)
    [[ -n "$base" ]]
}

# ---------------------------------------------------------------- main

case "${1:-start}" in
    stop|st)
        stop_all
        exit 0
        ;;
    start|"")
        ;;
    *)
        echo "Использование: ./run.sh [start|stop]"
        exit 1
        ;;
esac

if ! command -v dotnet > /dev/null; then
    echo "Не найден dotnet. Ожидался $DOTNET_ROOT/dotnet. Переустанови .NET 8 SDK."
    exit 1
fi

check_ports_free

API_PID=""
CLEANED=0

cleanup() {
    [[ $CLEANED -eq 1 ]] && return
    CLEANED=1

    if [[ -n "$API_PID" ]]; then
        echo
        echo "Останавливаю локальный Bot API сервер…"
        kill "$API_PID" 2>/dev/null
        wait "$API_PID" 2>/dev/null
    fi

    rm -f "$PID_FILE"
    echo "Готово. Можно закрыть окно."
}
trap cleanup EXIT INT TERM

# --- 1. Локальный Bot API сервер (если включён в конфиге) ---
if needs_local_api; then
    echo "Запускаю локальный Telegram Bot API сервер…"
    "$BOT_API_SCRIPT" > "$LOG_DIR/telegram-bot-api.log" 2>&1 &
    API_PID=$!

    printf "Жду готовности сервера"
    for _ in $(seq 1 30); do
        if port_busy 8081; then
            echo " — готов."
            break
        fi
        if ! kill -0 "$API_PID" 2>/dev/null; then
            echo " — не удалось запустить."
            echo "Смотри лог: $LOG_DIR/telegram-bot-api.log"
            tail -5 "$LOG_DIR/telegram-bot-api.log" | sed 's/^/    /'
            exit 1
        fi
        printf "."
        sleep 1
    done

    if ! port_busy 8081; then
        echo " таймаут. Лог: $LOG_DIR/telegram-bot-api.log"
        exit 1
    fi
else
    echo "Локальный Bot API не нужен (LocalApiBaseUrl пустой) — работаем через api.telegram.org (лимит 50 МБ)."
fi

# --- 2. Бот ---
echo "Запускаю MusicBot (логи бота — здесь, Ctrl+C для остановки)…"
echo
cd "$BOT_DIR" || exit 1

# Запоминаем PIDs, чтобы ./run.sh stop точно знал, что останавливать.
{
    [[ -n "$API_PID" ]] && echo "api=$API_PID"
} > "$PID_FILE"

dotnet run -c Release
