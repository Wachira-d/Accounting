#!/usr/bin/env bash
# รันด่านทั้งหมดที่ CLAUDE.md กฎเหล็ก #4 F บังคับ ด้วยคำสั่งเดียว — ออก exit code เดียว
#
# ที่มา (รอบ 169): ลิสต์ใน CLAUDE.md ยาว 40+ บรรทัดและต้องพิมพ์เอง ⇒ "ลืมรัน" เป็นต้นเหตุร่วมของ
# build ที่พังถึงผู้ใช้ 14 ครั้งใน 15 วัน. ตัวนี้ทำให้ข้อ 1-5 ของ checklist ก่อน push เป็นอัตโนมัติ:
#   1. checker ทุกตัวใน tools/*_check.py (ล้มตัวไหน ชื่อไฟล์บอก — แก้ ไม่ใช่เลี่ยง)
#   2. node --check ทุก <script> ใน .html ที่แก้ · brace balance ทุก .cs ที่แก้ · U+FFFD ในไฟล์ที่แก้
#   3. dead helper ใหม่ (dead_helper_check ratchet) · TEST_PLAN §0 ตรงกับเทสต์จริง (test_inventory --check)
#   4. dotnet build/test — เฉพาะเมื่อมี SDK (env นี้ไม่มี — proxy policy ปิด host ของ Microsoft)
#
# ใช้: bash tools/check_all.sh          # ไฟล์ที่แก้ = diff กับ HEAD + staged + untracked
#      bash tools/check_all.sh --all    # node --check / brace / U+FFFD กับทุกไฟล์ใน wwwroot + *.cs
#      bash tools/check_all.sh --all --no-dotnet   # ใน CI job static-checks (job build/test แยกต่างหาก — ไม่ build ซ้ำ)
set -u
cd "$(dirname "$0")/.." || exit 2
fail=0
red()  { printf '\033[31m%s\033[0m\n' "$*"; }
green(){ printf '\033[32m%s\033[0m\n' "$*"; }

# ---------- 1. checker ทุกตัว ----------
for f in tools/*_check.py; do
  out=$(python3 "$f" 2>&1); rc=$?
  if [ $rc -ne 0 ]; then red "❌ $f"; echo "$out" | tail -40; fail=1; fi
done
[ $fail -eq 0 ] && green "✅ checker $(ls tools/*_check.py | wc -l) ตัวผ่าน"

# ---------- 2. ไฟล์ที่แก้ ----------
all=0; nodotnet=0
for a in "$@"; do case "$a" in --all) all=1;; --no-dotnet) nodotnet=1;; esac; done
if [ $all -eq 1 ]; then
  changed=$( { git ls-files 'Accounting/**/*.cs' 'Accounting.Tests/*.cs' 'Accounting/wwwroot/**/*.html' 'Accounting/wwwroot/**/*.js'; } )
else
  changed=$( { git diff --name-only HEAD; git diff --name-only --cached; git ls-files --others --exclude-standard; } | sort -u )
fi

tmpd=$(mktemp -d)
for f in $changed; do
  [ -f "$f" ] || continue
  case "$f" in
    *.cs)
      # brace balance — นับดิบด้วย awk ตามที่ CLAUDE.md F กำหนด (ตัวนับที่พยายาม "ตัดสตริงก่อน" ฟ้องผิดที่
      # `$@"…{(x ? "a" : "b")}…"` เพราะ `"` ในรูอินเทอร์โพเลตปิดสตริงก่อนเวลา — negative test บน PayrollService.cs)
      bal=$(awk '{o+=gsub(/\{/,"");c+=gsub(/\}/,"")} END{print o-c}' "$f")
      if [ "$bal" != "0" ]; then red "❌ brace ไม่สมดุล ($bal): $f"; fail=1; fi
      ;;
    *.html)
      # แตกทุก <script> inline ออกเป็นไฟล์ แล้ว node --check ทีละตัว (ไม่ใช่ new Function — ให้ตรง parser จริง)
      python3 - "$f" "$tmpd" <<'PY'
import re,sys,os
src,out=sys.argv[1],sys.argv[2]
t=open(src,encoding='utf-8',errors='ignore').read()
for i,m in enumerate(re.finditer(r'<script(?![^>]*\bsrc=)(?:[^>]*)>([\s\S]*?)</script>',t,flags=re.I),1):
    attrs=m.group(0)[:m.group(0).index('>')]
    if re.search(r'type\s*=\s*["\'](?!(text/javascript|module|application/javascript)["\'])',attrs,flags=re.I): continue
    ext='.mjs' if 'module' in attrs else '.js'
    open(os.path.join(out,os.path.basename(src)+f'.{i}{ext}'),'w',encoding='utf-8').write(m.group(1))
PY
      for js in "$tmpd"/"$(basename "$f")".*.js "$tmpd"/"$(basename "$f")".*.mjs; do
        [ -f "$js" ] || continue
        if ! node --check "$js" 2>"$tmpd/err"; then red "❌ node --check: $f ($(basename "$js"))"; head -5 "$tmpd/err"; fail=1; fi
        rm -f "$js"
      done
      ;;
    *.js)
      if ! node --check "$f" 2>"$tmpd/err"; then red "❌ node --check: $f"; head -5 "$tmpd/err"; fail=1; fi
      ;;
  esac
  case "$f" in
    *.cs|*.html|*.js|*.md)
      if grep -q $'�' "$f"; then red "❌ ตัวอักษรพัง U+FFFD ใน $f: $(grep -n $'�' "$f" | head -3 | cut -c1-120)"; fail=1; fi ;;
  esac
done
rm -rf "$tmpd"
green "✅ ไฟล์ที่ตรวจ (brace/node/U+FFFD): $(echo "$changed" | grep -c . )"

# ---------- 3. doc ↔ source ----------
if ! python3 tools/test_inventory.py --check; then fail=1; fi

# ---------- 4. compiler ถ้ามี ----------
if [ $nodotnet -eq 1 ]; then
  echo "ℹ️  --no-dotnet: ข้าม build/test (job อื่นทำ)"
elif command -v dotnet >/dev/null 2>&1; then
  dotnet build Accounting.sln -c Release 2>&1 | tail -20 || fail=1
  dotnet test Accounting.sln -c Release --no-build 2>&1 | tail -20 || fail=1
else
  echo "ℹ️  ไม่มี dotnet SDK ในเครื่องนี้ — ยังไม่ได้คอมไพล์ (แจ้งผู้ใช้ rebuild ฝั่งเขา)"
fi

if [ $fail -ne 0 ]; then red "❌ check_all: มีด่านไม่ผ่าน"; exit 1; fi
green "✅ check_all ผ่านทั้งหมด"
