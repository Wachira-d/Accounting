#!/usr/bin/env bash
# รอผล CI (GitHub Actions) ของคอมมิตหนึ่ง แล้วพิมพ์ผลสั้น ๆ — แทนการวนเรียก mcp actions_list (รอบละ ~5 KB JSON เข้า context)
#
# ใช้:   bash tools/ci_wait.sh [sha=HEAD]     ← รันด้วย run_in_background แล้วรอแจ้งเตือน (ห้าม sleep วนใน foreground)
# พิมพ์: CI <run_id> <status> <conclusion> <sha7> <url>
#        job <conclusion> <job_id> <name>      (หนึ่งบรรทัดต่อ job — เฉพาะเมื่อ run จบแล้ว)
# exit:  0 เขียว · 1 แดง (failure/timed_out/startup_failure/action_required/stale)
#        2 หมดเวลารอ · 3 ไม่มีรอบ CI ของ sha นี้ (paths-ignore: แตะแต่ .md/erp-review/scripts) — ห้ามรายงานว่าเขียว
#        4 ถูกยกเลิก (concurrency: push ใหม่กว่าแทนที่) — รอ sha ล่าสุดแทน · 5 เรียก GitHub ไม่ได้ติดกันจนหมดรอบ retry
# แดง ⇒ mcp get_job_logs(job_id=<job_id ของบรรทัด job failure>, return_content=true, tail_lines=150) ครั้งเดียว
# ⚠️ job "dotnet test" (เทสต์ไม่ใช่ DB) เป็น skipped บน push ของ claude/** ตาม ci.yml — เขียวที่นี่ ≠ เทสต์หน่วยผ่าน
#
# ที่มา: 2026-10-03 session เดียวเรียก actions_list 135 ครั้ง ≈ 0.72 MB เข้า context ทั้งที่ต้องการแค่ "เขียวหรือแดง"
set -u
sha="$(git rev-parse "${1:-HEAD}" 2>/dev/null)" || { echo "CI - error - ${1:-HEAD}: sha ไม่ถูกต้อง" >&2; exit 2; }
repo="${CI_REPO:-wachira-d/accounting}"
wf_path="${CI_WORKFLOW_PATH:-.github/workflows/ci.yml}"
poll="${CI_POLL_SECONDS:-30}"            # ช่วงห่างเริ่มต้น · คูณ 2 ทุกรอบจนถึง CI_POLL_MAX (CI รอบละ ~2-3 นาที)
poll_max="${CI_POLL_MAX:-120}"
max_wait="${CI_MAX_WAIT_SECONDS:-2400}"
no_run_grace="${CI_NO_RUN_SECONDS:-180}" # GitHub สร้าง run หลัง push ไม่ทันที · เกินนี้ยังไม่มี = ไม่มีรอบ
api_retries="${CI_API_RETRIES:-5}"       # ล้มเพราะเน็ต/5xx/403 rate limit → ถอยหลัง 2·4·8·16·32 วินาที
sha7="${sha:0:7}"
command -v gh >/dev/null 2>&1 || { echo "CI - error - $sha7: ไม่มี gh — ใช้ mcp actions_list (per_page=1) ครั้งเดียวแทน" >&2; exit 5; }
command -v jq >/dev/null 2>&1 || { echo "CI - error - $sha7: ไม่มี jq" >&2; exit 5; }

api() {  # api <endpoint> <jq-filter> — retry + exponential backoff · คืนผล jq ทาง stdout
  local i=0 back=2 out
  while :; do
    if out=$(gh api "$1" 2>/dev/null) && out=$(printf '%s' "$out" | jq -r "$2" 2>/dev/null); then
      printf '%s' "$out"; return 0
    fi
    i=$((i + 1))
    (( i >= api_retries )) && return 1
    sleep "$back"; back=$((back * 2))
  done
}

start=$(date +%s); run_id=""
while :; do
  elapsed=$(( $(date +%s) - start ))
  # run ล่าสุดของ workflow ci.yml สำหรับ sha นี้ (re-run ได้ attempt ใหม่ใน run เดิม · กรอง path กัน Dependency Graph)
  if ! line=$(api "repos/${repo}/actions/runs?head_sha=${sha}&per_page=20" \
      "[.workflow_runs[] | select(.path == \"${wf_path}\")] | sort_by(.created_at) | last
       | select(. != null) | \"\(.id) \(.status) \(.conclusion // \"-\") \(.html_url)\""); then
    echo "CI ${run_id:--} api-error - ${sha7}: เรียก GitHub ไม่ได้ ${api_retries} ครั้งติด (ตรวจ proxy: \$HTTPS_PROXY/__agentproxy/status)"
    exit 5
  fi
  if [[ -z "$line" ]]; then
    if (( elapsed >= no_run_grace )); then
      echo "CI - none - ${sha7}: ไม่มีรอบ CI ภายใน ${no_run_grace}s (คอมมิตแตะแต่ .md/erp-review/scripts?) — ห้ามรายงานว่าเขียว"
      exit 3
    fi
  else
    read -r run_id status conclusion url <<<"$line"
    if [[ "$status" == "completed" ]]; then
      echo "CI ${run_id} ${status} ${conclusion} ${sha7} ${url}"
      api "repos/${repo}/actions/runs/${run_id}/jobs?filter=latest&per_page=100" \
          '.jobs[] | "job \(.conclusion // .status) \(.id) \(.name)"' || echo "job ? - (อ่านรายการ job ไม่ได้ — ใช้ get_job_logs run_id=${run_id} failed_only=true)"
      echo
      case "$conclusion" in
        success|skipped|neutral) exit 0 ;;
        cancelled)               exit 4 ;;
        *)                       exit 1 ;;
      esac
    fi
  fi
  if (( elapsed >= max_wait )); then
    echo "CI ${run_id:--} timeout - ${sha7}: เกิน ${max_wait}s ยังไม่ completed"
    exit 2
  fi
  sleep "$poll"; poll=$(( poll * 2 > poll_max ? poll_max : poll * 2 ))
done
