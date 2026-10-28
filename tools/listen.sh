#!/usr/bin/env bash
# For an agent (a Claude chat) that listens to what the user says into mictap.
#
# Waits until the user presses «Отправить агенту» (Ctrl+Alt+Enter) or stops the recording in the project, prints what
# they said as one JSON line (see `mictap wait --help`) and exits. Run it in the background, answer what it printed,
# run it again.
#
#   bash tools/listen.sh [project] [agent]        defaults: project mictap, agent claude-code
#
# Each agent name keeps its own read position (agents\<agent>.json in the session): two chats listening to the same
# project need two names, or they take the phrases from each other.

project=${1:-mictap}
agent=${2:-claude-code}

# Sessions that are not recording now count as read: a wait on a session this agent has never read would hand back all
# of it at once.
recording=$(mictap status 2>/dev/null | python -I -c "import json,sys; r=json.load(sys.stdin)['recording']; print(r['session'] if r else '')")
mictap sessions --project "$project" 2>/dev/null \
    | python -I -c "import json,sys; [print(s['name']) for s in json.load(sys.stdin)]" \
    | while IFS= read -r session; do
        if [ "$session" != "$recording" ]; then
            mictap wait --project "$project" --session "$session" --agent "$agent" --max-wait 1 > /dev/null 2>&1
        fi
    done

while true; do
    # --min-words 100000: never back just because enough words came; only on «send» or a stop.
    out=$(mictap wait --project "$project" --agent "$agent" --min-words 100000 2>&1)
    code=$?

    # 2 = nothing is recording in the project now, 3 = mictap is not running (being reinstalled): look again soon.
    if [ "$code" -eq 2 ] || [ "$code" -eq 3 ]; then
        sleep 3
        continue
    fi

    # 5 = another wait of this agent took over, 4 = bad arguments, …: report and stop.
    if [ "$code" -ne 0 ]; then
        echo "WAIT EXIT $code: $out"
        exit "$code"
    fi

    # A stop with nothing new said: keep listening.
    if echo "$out" | grep -q '"segments":\[\]'; then
        sleep 3
        continue
    fi

    echo "$out"
    exit 0
done
