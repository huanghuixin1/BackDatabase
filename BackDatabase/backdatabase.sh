#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PID_FILE="$SCRIPT_DIR/backdatabase.pid"
LOG_FILE="$SCRIPT_DIR/backdatabase.log"
APP_FILE="$SCRIPT_DIR/BackDatabase"
DLL_FILE="$SCRIPT_DIR/BackDatabase.dll"

if [[ -x "$APP_FILE" ]]; then
  COMMAND=("$APP_FILE")
elif [[ -f "$DLL_FILE" ]]; then
  COMMAND=(dotnet "$DLL_FILE")
else
  echo "??? $APP_FILE ? $DLL_FILE???????? BackDatabase ?????" >&2
  exit 1
fi

is_running() {
  [[ -f "$PID_FILE" ]] && kill -0 "$(<"$PID_FILE")" 2>/dev/null
}

start() {
  if is_running; then
    echo "BackDatabase ?????PID: $(<"$PID_FILE")??"
    return
  fi

  rm -f "$PID_FILE"
  nohup "${COMMAND[@]}" >> "$LOG_FILE" 2>&1 &
  local pid=$!
  echo "$pid" > "$PID_FILE"
  sleep 1

  if kill -0 "$pid" 2>/dev/null; then
    echo "BackDatabase ????PID: $pid?????$LOG_FILE"
  else
    rm -f "$PID_FILE"
    echo "BackDatabase ???????????$LOG_FILE" >&2
    exit 1
  fi
}

stop() {
  if ! is_running; then
    rm -f "$PID_FILE"
    echo "BackDatabase ????"
    return
  fi

  local pid
  pid="$(<"$PID_FILE")"
  kill -TERM "$pid"
  for _ in {1..30}; do
    if ! kill -0 "$pid" 2>/dev/null; then
      rm -f "$PID_FILE"
      echo "BackDatabase ????"
      return
    fi
    sleep 1
  done

  echo "BackDatabase ??? 30 ?????PID: $pid??" >&2
  exit 1
}

status() {
  if is_running; then
    echo "BackDatabase ?????PID: $(<"$PID_FILE")??"
  else
    rm -f "$PID_FILE"
    echo "BackDatabase ????"
    return 1
  fi
}

case "${1:-}" in
  start) start ;;
  stop) stop ;;
  restart) stop; start ;;
  status) status ;;
  *)
    echo "???$0 {start|stop|restart|status}" >&2
    exit 2
    ;;
esac
