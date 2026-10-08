---
description: ปิดรอบงาน — check_all → commit → push → อ่านผล CI จนเขียว แล้วค่อยรายงาน
argument-hint: [หัวข้อคอมมิตสั้น ๆ (ไม่ใส่ก็ได้)]
---

ปิดรอบงานที่ค้างอยู่ใน working tree ให้จบตาม **กฎเหล็ก #4 F3** (checklist ก่อน push 12 ข้อ)
หัวข้อคอมมิตที่ผู้ใช้ให้มา (ถ้ามี): $ARGUMENTS

ทำตามลำดับนี้ **ห้ามข้ามข้อ** และห้ามรายงานผู้ใช้ก่อนถึงข้อสุดท้าย

## 1. ตรวจที่ยืน
- `git branch --show-current` ต้องไม่ใช่ `main`/`master` — ถ้าใช่ **หยุด** แล้วถามผู้ใช้ก่อน
- `git status --short` + `git diff --stat` ดูว่ากำลังจะ commit อะไรจริง ๆ (ไฟล์หลุดมาไหม)

## 2. เครื่องตรวจ (F3 ก.)
```
bash tools/check_all.sh
```
- ล้มตัวไหน = **แก้ ไม่ใช่เลี่ยง** · checker ที่ฟ้องผิด = checker ที่พัง ให้แก้ checker พร้อม negative test
- ถ้าเครื่องมี `dotnet` มันจะ build/test ให้เอง · ถ้าไม่มี CI จะเป็นตัวคอมไพล์ในข้อ 6

## 3. เอกสารต้องขยับพร้อมโค้ด (คอมมิตเดียวกัน)
ถามทีละข้อ แตะข้อไหน = แก้ไฟล์นั้นด้วย:
- เพิ่ม/ลด `throw` · เปลี่ยนสูตรของค่าที่ persist · เปลี่ยน guard/สถานะ → `DOCUMENT_FLOW.md`
- แตะ Company/Branch/Subscription/ApiClient/billing/quota → `ACCOUNT_STRUCTURE.md`
- เพิ่ม/ลบเทสต์ → `TEST_PLAN.md` (§0 ใช้ `python3 tools/test_inventory.py --row` วางทับ)
- ประวัติรอบ → `CHANGELOG.md` (ไม่ใช่ append ลง DOCUMENT_FLOW)
- defect class ใหม่ → `docs/lessons/<หมวด>.md` (ไม่ใช่ CLAUDE.md — แตะ CLAUDE.md เฉพาะตอนเปลี่ยน **หลักการ** F2)

## 4. ตอบ 6 คำถามในข้อความคอมมิต (F3 ข. — ตอบไม่ได้ = ยังไม่ push)
1. แก้ที่นี่ แล้ว `python3 tools/callers.py <Symbol>` / grep รูปแบบเดิมทั้งเรพเหลือกี่จุด → **เขียนเป็นตัวเลข** (0 ก็เขียนว่า 0)
2. ทางเข้าอื่นที่แตะข้อมูลชุดเดียวกัน (คีย์มือ · CSV · API · LINE · มือถือ · background job) เดินด่านเดียวกันไหม
3. ถ้าเข้มขึ้น/เพิ่ม throw — ผู้ใช้ที่ถูกกันทำอะไรได้แทน + เทสต์ทิศตรงข้ามชื่ออะไร
4. ค่าที่ persist ไว้ก่อนแก้ ยังผิดอยู่ไหม → มี migration ไหม
5. แตะเงิน/ภาษี/สิทธิ์ → ส่ง diff ให้ subagent ฝ่ายค้าน 1 รอบแล้วหรือยัง
6. คู่สมมาตรอ่านครบไหม (`if (isSeller) … else …` · renderer HTML/QuestPDF/พรีวิว · ฝั่งอ่าน/ฝั่งเขียน)

## 5. คอมมิต
- ข้อความเป็นไทย อธิบาย **ทำไม** มากกว่า *ทำอะไร* · ท้ายข้อความใส่ footer การระบุที่มาตามที่ harness กำหนดใน session นั้น
- **ห้าม `--amend` หลังเติม sha ลง doc** — sha ที่เพิ่งเขียนจะตายทันที ให้เติมใน **คอมมิตตามหลังอีกใบ**
- ก่อน push: `git merge-base --is-ancestor <sha ที่จดใน doc> HEAD` ต้องผ่าน (`doc_commit_sha_check` ก็ฟ้อง)

## 6. push แล้ว **อ่านผล CI เอง**
```
git push -u origin $(git branch --show-current)
```
- ล้มเพราะเน็ต → retry 4 ครั้ง (2s · 4s · 8s · 16s)
- **ถ้าคอมมิตแตะแต่ `**.md` / `erp-review/**` / `scripts/**`** → CI ไม่รันตาม `paths-ignore`
  บอกผู้ใช้ตรง ๆ ว่า "ไม่มีรอบ CI ให้รอ" **ห้ามรอเก้อแล้วรายงานว่าเขียว**
- ถ้ารัน: `bash tools/ci_wait.sh` ด้วย `run_in_background` แล้ว**รอแจ้งเตือน** — มันถาม GitHub เอง (30s→120s · retry เน็ตล้ม)
  แล้วพิมพ์ `CI <run_id> completed <conclusion> <sha7> <url>` + บรรทัด `job <conclusion> <job_id> <name>` ต่อ job
  exit 0 เขียว · 1 แดง · 2 หมดเวลา · 3 ไม่มีรอบ (ห้ามรายงานว่าเขียว) · 4 ถูกยกเลิกเพราะมี push ใหม่ (รอ sha ล่าสุด) · 5 เรียก GitHub ไม่ได้
  **ห้าม** วนเรียก `mcp__github__actions_list` เอง (บทเรียน 2026-10-03: 135 ครั้ง ≈ 0.72 MB เข้า context ทั้งที่ต้องการแค่เขียว/แดง)
  ไม่มี `gh`/exit 5 → `actions_list` ครั้งเดียวต่อ push ใส่ `per_page: 1`
  ⚠️ เขียวบน `claude/**` = build + static + เทสต์ DB เท่านั้น — job `dotnet test` (เทสต์หน่วย) ถูก skip ตาม ci.yml (ต้องให้เจ้าของตัดสิน)

## 7. แดง = แก้ก่อน ห้ามรายงาน
- `mcp__github__get_job_logs` **ครั้งเดียว** (`job_id` จากบรรทัด `job failure …` · `return_content: true` · `tail_lines: 150`)
  — error ของ dotnet อยู่ท้าย log เสมอ ไม่ต้องดึงเต็ม
- แก้ในคอมมิตใหม่ (ห้าม amend) → กลับข้อ 2 → วนจนเขียว
- ถ้า CI จับ build error ที่ checker มองไม่เห็น (ต้องรู้ชนิดจริง เช่น CS1061/CS1503/CS8126/CS1056):
  **อย่าเขียน checker ที่ต้อง type resolution** (F4 ข้อ 1) — ถ้าเป็นรูปทรงของโค้ดล้วนค่อยขยาย checker เดิม
  พร้อม negative test แล้วจดที่ `docs/lessons/build-errors.md`

## 8. รายงานผู้ใช้
สรุปสั้น: แก้อะไร · ทำไม · เลข run CI + ผล · อะไรที่เจ้าของต้องทำต่อ
และปิดท้ายเสมอว่า **"ยังไม่ได้คอมไพล์ในเครื่องนี้ — รบกวน rebuild ฝั่งคุณ"** (จนกว่า proxy จะเปิด host .NET)
