#!/usr/bin/env bash
# G-09: measures the network traffic of one signed-in Employee Desktop, request by request, against a local
# API with fictional data. Bytes are what curl puts on and takes off the wire (headers + body).
# Usage: API=http://127.0.0.1:5081 ADMIN_EMAIL=... ADMIN_PASSWORD=... PSQL="psql -d tm_e2e" \
#        [MODE=before|after] [VIA=http://127.0.0.1:5090] scripts/audit/desktop-bandwidth.sh
# MODE=before: the desktop as on main (every notification page, no Accept-Encoding).
# MODE=after:  the latest notification page only, with Accept-Encoding, as the G-09 change sends.
# VIA: send the desktop's requests through a reverse proxy (for example Caddy with "encode zstd gzip", as in
#      deploy/production/Caddyfile) instead of straight to API. Setup calls always go to API.
# Needs curl, jq and psql access to the API's database (only to add and remove fictional notifications).
set -euo pipefail
: "${API:?}" "${ADMIN_EMAIL:?}" "${ADMIN_PASSWORD:?}" "${PSQL:?}"
MODE=${MODE:-before}
VIA=${VIA:-$API}
encoding=()
[ "$MODE" = after ] && encoding=(-H 'Accept-Encoding: gzip, deflate, br')
run="$(date +%s)$RANDOM"
json() { curl -sS --fail -H 'Content-Type: application/json' "$@"; }

admin=$(json -X POST "$API/api/auth/web/login" -d "$(jq -n --arg e "$ADMIN_EMAIL" --arg p "$ADMIN_PASSWORD" '{email:$e,password:$p}')" | jq -r .accessToken)
role=$(json -H "Authorization: Bearer $admin" "$API/api/admin-lookups/employees/roles" | jq -r '.[]|select(.name=="Employee").id')
email="bw-$run@example.invalid"
password="Bw-$(head -c 12 /dev/urandom | od -An -tx1 | tr -d ' \n')"
employee=$(json -X POST -H "Authorization: Bearer $admin" "$API/api/employees" -d "$(jq -n --arg c "BW-$run" --arg e "$email" --arg p "$password" --arg r "$role" \
  '{employeeCode:$c,fullName:"Bandwidth Tester",email:$e,password:$p,jobTitle:"Fictional tester",roleIds:[$r]}')" | jq -r .id)
# Monitoring on with one approved application, so activity samples are accepted (needs the G-56 fix).
json -X PUT -H "Authorization: Bearer $admin" "$API/api/monitoring/policy" \
  -d '{"isEnabled":true,"sampleIntervalSeconds":60,"retentionDays":30,"disclosureText":"Approved work applications and business domains are recorded during work hours for workload reporting."}' > /dev/null \
  || echo "note: policy update failed on this build (G-56); the current policy is used" >&2
json -X POST -H "Authorization: Bearer $admin" "$API/api/monitoring/applications" \
  -d '{"processName":"bwtool","displayName":"Bandwidth tool","captureWindowTitle":true,"isActive":true}' > /dev/null 2>&1 || true
token=$(json -X POST "$API/api/auth/login" -d "$(jq -n --arg e "$email" --arg p "$password" '{email:$e,password:$p}')" | jq -r .accessToken)

# One request as the desktop sends it; prints bytes up (request line, headers, body) and down (headers, body).
measure() { # name method path [body]
  local name=$1 method=$2 path=$3 body=${4:-} sizes
  local args=(-sS -o /dev/null -w '%{size_request} %{size_upload} %{size_header} %{size_download} %{http_code}' -X "$method" "${encoding[@]}")
  [ "$path" != /health/live ] && args+=(-H "Authorization: Bearer $token")
  [ -n "$body" ] && args+=(-H 'Content-Type: application/json; charset=utf-8' -d "$body")
  sizes=$(curl "${args[@]}" "$VIA$path")
  set -- $sizes
  printf '%-34s up=%6d down=%8d http=%s\n' "$name" $(( $1 + $2 )) $(( $3 + $4 )) "$5"
}

seed_notifications() { # count
  $PSQL -qAt -c "delete from employee_notifications where \"EmployeeId\"='$employee';
    insert into employee_notifications (\"Id\",\"EmployeeId\",\"Kind\",\"Title\",\"Message\",\"EntityType\",\"EntityId\",\"CreatedAtUtc\",\"ReadAtUtc\")
    select gen_random_uuid(), '$employee', 'TaskAssigned', 'Task assigned',
      'You were assigned to task ''Update the onboarding checklist for March'' in project OPS-ONBOARD.', 'ProjectTask', gen_random_uuid(),
      now() - (g || ' minutes')::interval, case when g > 5 then now() else null end
    from generate_series(1, $1) g;"
}

refresh_notifications() { # count: the pages one background refresh downloads
  local count=$1 pages=1 page up=0 down=0 line
  # main: EmployeeApiClient.GetAllPagesAsync reads 100 per page until it has them all.
  if [ "$MODE" = before ] && [ "$count" -gt 100 ]; then pages=$(( (count + 99) / 100 )); fi
  for page in $(seq 1 "$pages"); do
    line=$(measure "page $page" GET "/api/me/notifications?unreadOnly=false&page=$page&pageSize=100")
    up=$((up + $(sed -E 's/.*up= *([0-9]+).*/\1/' <<<"$line")))
    down=$((down + $(sed -E 's/.*down= *([0-9]+).*/\1/' <<<"$line")))
  done
  printf '%-34s up=%6d down=%8d pages=%d\n' "notifications refresh, $count stored" "$up" "$down" "$pages"
}

echo "== MODE=$MODE via $VIA (fictional employee $employee) =="
measure "presence heartbeat (30 s)" POST /api/me/presence/heartbeat '{"clientKind":"desktop","clientVersion":"1.0.0.0"}'
measure "agent health (30 s)" POST /api/me/agent-health '{"machineName":"BW-PC-01","desktopVersion":"1.0.0.0","serviceVersion":"1.0.0.0","updaterVersion":"1.0.0.0","serviceRunning":true,"installedVersion":"1.0.0.0","updateChannel":"stable","lastSuccessfulUpdateAtUtc":null,"rolledBackAtUtc":null}'
measure "application activity (interval)" POST /api/me/monitoring/application-activity '{"processName":"bwtool","windowTitle":"Quarterly plan.xlsx - Bandwidth tool"}'
measure "monitoring policy (5 min)" GET /api/me/monitoring/policy
measure "service liveness (60 s)" GET /health/live
for count in 0 100 1000; do
  seed_notifications "$count"
  refresh_notifications "$count"
done
$PSQL -qAt -c "delete from employee_notifications where \"EmployeeId\"='$employee';"
