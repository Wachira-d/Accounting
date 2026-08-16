#!/usr/bin/env python3
"""
ตรวจ CS1739 "The best overload for 'X' does not have a parameter named 'Y'"
— เคสที่เจอจริง: เพิ่ม field ลง entity + Create/UpdateRequest + mapper แล้ว
**ลืมเพิ่มใน Response record** ⇒ `new DocumentResponse(..., IssuedAsCashReceipt: ...)`
คอมไพล์ไม่ผ่าน (และลาก Accounting.Tests ล้มตามด้วย CS0006)

นี่คือ defect class เดียวกับ "เก็บแล้วต้อง echo กลับ" (CLAUDE.md กฎเหล็ก #4 A) —
ต่างกันตรงที่ถ้าลืม **ทั้ง** mapper และ record จะไม่มี error ให้เห็นเลย (ค่าหาย
เงียบ ๆ ตอน runtime) แต่ถ้าลืมเฉพาะ record จะได้ CS1739. checker นี้จับตัวหลัง

env นี้ไม่มี .NET SDK จึงคอมไพล์ไม่ได้ — checker นี้ทดแทนเฉพาะ defect class นี้

หลักการ (แม่นก่อนครอบคลุม — false positive ต้องเป็นศูนย์):
  • index เฉพาะ **positional record ที่นิยามในเรพ** และมีชื่อไม่ซ้ำกัน
    (ชื่อซ้ำ = overload/partial → ข้าม ตัดสินไม่ได้ด้วย regex)
  • ข้าม record ที่มี ctor เพิ่มเอง / เป็น `record class X { }` แบบ property
  • ดูเฉพาะ `new X(...)` ที่มี named argument (`Name:`) — positional ไม่ตรวจ
  • flag เมื่อชื่อ named arg ไม่อยู่ในรายการพารามิเตอร์ของ record นั้น

ใช้: python3 tools/record_arg_check.py [path ...]   (ไม่ระบุ = ทั้งเรพ)
exit 1 เมื่อพบปัญหา
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

_STRIP = re.compile(
    r'@"(?:[^"]|"")*"'
    r'|\$?"(?:\\.|[^"\\\n])*"'
    r"|'(?:\\.|[^'\\\n])*'"
    r'|//[^\n]*'
    r'|/\*(?:.|\n)*?\*/',
    re.M)

RECORD_DECL = re.compile(r'\brecord\s+(?:class\s+|struct\s+)?([A-Z]\w*)\s*\(')
NEW_EXPR = re.compile(r'\bnew\s+([A-Z]\w*)\s*\(')
# target-typed new — **เฉพาะรูป `return new(...)`** ที่ชนิดมาจาก return type ของ
# เมธอดที่ครอบอยู่. จำเป็นต้องรองรับ เพราะ mapper ใหญ่ของเรพเขียนแบบนี้
# (DocumentService.MapDocumentToResponse:12759) — บั๊ก CS1739 จริงอยู่ในนั้น
#
# ⚠️ ห้ามขยายไปจับ `new(` ทุกที่: ใน collection initializer / ประกาศตัวแปร
# ชนิดมาจาก **element type หรือชนิดตัวแปร** ไม่ใช่ return type ของเมธอด
# (เจอจริง: `Lines: new List<DocumentLineRequest> { new(Description: ...) }`
#  ถูกตีความเป็น PlatformDocResult → 34 false positive)
TARGET_TYPED_NEW = re.compile(r'\breturn\s+new\s*\(')
METHOD_DECL = re.compile(
    r'^[ \t]*(?:\[[^\]\n]*\][ \t]*)*'
    r'(?:(?:public|private|protected|internal|static|async|virtual|override|sealed|partial|new)[ \t]+)+'
    r'([A-Za-z_][\w.]*(?:<[^<>\n]*>)?\??)[ \t]+'
    r'([A-Za-z_]\w*)[ \t]*\(', re.M)
TASK_WRAP = re.compile(r'^(?:Task|ValueTask)<(.+)>$')
# ชื่อพารามิเตอร์ = identifier ตัวสุดท้ายก่อน ',' / '=' / ')' ในแต่ละช่อง
PARAM_NAME = re.compile(r'([A-Za-z_]\w*)\s*(?:=[^,]*)?$')
NAMED_ARG = re.compile(r'(?:^|[(,])\s*([A-Za-z_]\w*)\s*:(?!:)')


def strip_noise(src):
    """ลบ comment/string แต่ **คงจำนวนบรรทัดเดิม** — comment แบบ /* */ หลาย
    บรรทัดถ้าถูกยุบเป็นช่องว่างเดียว เลขบรรทัดที่รายงานจะเลื่อนไม่ตรงกับที่
    compiler ชี้ (เจอจริง: รายงาน 12843 ขณะ compiler บอก 12865)"""
    return _STRIP.sub(lambda m: '\n' * m.group(0).count('\n') or ' ', src)


def match_parens(src, open_idx):
    """คืน index ของ ')' ที่คู่กับ '(' ที่ open_idx (นับ <> ไม่เกี่ยว)"""
    depth = 0
    for i in range(open_idx, len(src)):
        c = src[i]
        if c == '(':
            depth += 1
        elif c == ')':
            depth -= 1
            if depth == 0:
                return i
    return -1


def split_top_level(s):
    """แยกด้วย ',' เฉพาะระดับบนสุด — นับเฉพาะ (), [], {}

    ⚠️ **ห้ามนับ `<` `>`**: lambda `l => new X(...)` มี '>' ที่ทำให้ depth ติดลบ
    → argument ของ call ที่ซ้อนอยู่ข้างในถูกมองเป็นระดับบนสุดของ call นอก
    (เจอจริง: `new CreateJournalEntryRequest(Lines: ....Select(l => new
    JournalLineRequest(DebitAmount: ...)))` ถูกรายงานว่า CreateJournalEntryRequest
    ไม่มีพารามิเตอร์ DebitAmount = false positive)
    ผลข้างเคียงที่ยอมรับได้: generic `Dictionary<string, X>` จะถูกตัดผิดตรง ','
    แต่ชิ้นที่ได้ไม่ได้ขึ้นต้นด้วย `Name:` จึงไม่กลายเป็น false positive
    (แค่ตรวจไม่เจอในเคสนั้น — เลือกพลาดฝั่งเงียบดีกว่าฝั่งหลอน)
    """
    parts, depth, cur = [], 0, []
    for c in s:
        if c in '([{':
            depth += 1
        elif c in ')]}':
            depth -= 1
        if c == ',' and depth == 0:
            parts.append(''.join(cur)); cur = []
        else:
            cur.append(c)
    if cur:
        parts.append(''.join(cur))
    return parts


def index_records(files):
    """ชื่อ record → set(ชื่อพารามิเตอร์)  · ชื่อซ้ำ → None (ข้าม)"""
    idx = {}
    for f in files:
        src = strip_noise(f.read_text(encoding='utf-8', errors='replace'))
        for m in RECORD_DECL.finditer(src):
            name = m.group(1)
            open_idx = src.index('(', m.end() - 1)
            close = match_parens(src, open_idx)
            if close < 0:
                idx[name] = None
                continue
            params = set()
            ok = True
            for chunk in split_top_level(src[open_idx + 1:close]):
                chunk = chunk.strip()
                if not chunk:
                    continue
                pm = PARAM_NAME.search(chunk.split('=')[0].strip())
                if not pm:
                    ok = False
                    break
                params.add(pm.group(1))
            if name in idx and idx[name] != params:
                idx[name] = None        # นิยามซ้ำ/ไม่ตรง → ไม่ตัดสิน
            else:
                idx[name] = params if ok else None
    return idx


def _return_types(src):
    """[(ตำแหน่ง, ชนิดที่คืน)] ของทุกเมธอดในไฟล์ — เรียงตามตำแหน่ง"""
    out = []
    for m in METHOD_DECL.finditer(src):
        rt = m.group(1).rstrip('?')
        tw = TASK_WRAP.match(rt)
        if tw:
            rt = tw.group(1).strip().rstrip('?')
        out.append((m.start(), rt))
    return out


def _enclosing_type(rets, pos):
    """ชนิดที่คืนของเมธอดล่าสุดก่อนตำแหน่ง pos (ใช้ตีความ `new(...)`)"""
    found = None
    for start, rt in rets:
        if start > pos:
            break
        found = rt
    return found


def check_file(f, idx):
    raw = f.read_text(encoding='utf-8', errors='replace')
    src = strip_noise(raw)
    rets = _return_types(src)
    problems = []

    # (ชื่อ record, ตำแหน่ง '(' ที่เปิดวงเล็บ argument)
    sites = []
    for m in NEW_EXPR.finditer(src):
        sites.append((m.group(1), src.index('(', m.end() - 1)))
    for m in TARGET_TYPED_NEW.finditer(src):
        t = _enclosing_type(rets, m.start())
        if t:
            sites.append((t, src.index('(', m.end() - 1)))

    for name, open_idx in sites:
        params = idx.get(name)
        if not params:                  # ไม่ใช่ record ในเรพ / ตัดสินไม่ได้
            continue
        close = match_parens(src, open_idx)
        if close < 0:
            continue
        body = src[open_idx + 1:close]
        off = open_idx + 1              # ตำแหน่งจริงใน src ของต้น chunk
        for chunk in split_top_level(body):
            start, off = off, off + len(chunk) + 1   # +1 = ',' ที่ถูกตัดทิ้ง
            stripped = chunk.strip()
            am = NAMED_ARG.match('(' + stripped) if stripped else None
            if not am:
                continue
            arg = am.group(1)
            if arg in params:
                continue
            # รายงานบรรทัดของ argument ตัวที่ผิด (ไม่ใช่บรรทัดที่เปิดวงเล็บ) —
            # ตรงกับที่ compiler ชี้ ทำให้กระโดดไปแก้ได้ทันที
            line = src[:start + (len(chunk) - len(chunk.lstrip()))].count('\n') + 1
            problems.append((line, name, arg))
    return problems


def main():
    targets = [Path(a) for a in sys.argv[1:]] or [ROOT]
    all_cs = sorted(p for p in ROOT.rglob('*.cs')
                    if 'obj' not in p.parts and 'bin' not in p.parts)
    idx = index_records(all_cs)

    scan = []
    for t in targets:
        t = t if t.is_absolute() else (ROOT / t)
        if t.is_file() and t.suffix == '.cs':
            scan.append(t)
        elif t.is_dir():
            scan += [p for p in t.rglob('*.cs')
                     if 'obj' not in p.parts and 'bin' not in p.parts]
    scan = sorted(set(scan))

    bad = 0
    for f in scan:
        for line, rec, arg in check_file(f, idx):
            bad += 1
            print(f'{f.relative_to(ROOT)}:{line}: CS1739 — `new {rec}(...)` '
                  f'ส่ง named argument `{arg}:` ที่ไม่มีใน record นี้')

    usable = sum(1 for v in idx.values() if v)
    print(f'\nตรวจ {len(scan)} ไฟล์ · positional record ที่ตรวจได้ {usable} ชนิด '
          f'· พบปัญหา {bad} จุด')
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
