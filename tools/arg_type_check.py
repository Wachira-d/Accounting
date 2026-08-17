#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ตรวจ CS1503 — ส่งอาร์กิวเมนต์ผิดชนิดให้ method ที่ประกาศอยู่ **ไฟล์เดียวกัน**

    CS1503  Argument N: cannot convert from 'System.Guid' to 'Xxx'
            (และทิศกลับกัน: ส่ง entity เข้าพารามิเตอร์ที่เป็น Guid)

ที่มา (บั๊กจริง): `SyncWhtCreditReceivedAsync(Guid companyId, Document doc)`
ถูกเรียกด้วย `SyncWhtCreditReceivedAsync(companyId, doc.Id)` และ
`(companyId, allocDocId)` ⇒ ล้มทั้ง solution (Accounting.Tests พังตามด้วย
CS0006). checker ที่มีอยู่จับไม่ได้เลย — `nullable_arg_check` ดูแค่
nullable→non-nullable ส่วนตัวอื่นดู using/named-arg/accessibility

ขอบเขต (ตั้งใจแคบมากเพื่อไม่ให้ false positive — ทดสอบแล้ว 0 จุดบนเรพจริง):
  • ตรวจเฉพาะ method ที่ประกาศในไฟล์เดียวกันและ **ชื่อไม่ซ้ำ** (ไม่มี overload)
  • ฟ้องเฉพาะเมื่อครบ 2 เงื่อนไขพร้อมกัน
      1. อาร์กิวเมนต์เป็น `xxx.Id` / `xxx.Value.Id` หรือตัวแปรที่ประกาศ `Guid name`
      2. พารามิเตอร์ตำแหน่งนั้นเป็น **ชนิด entity ของเรพนี้** (คลาส/เรคคอร์ดใน
         Models/Entities) — เพราะ ".Id ของอะไรก็ตาม ไม่มีทางเป็น entity"
    เงื่อนไข 2 คือตัวกันเสียงรบกวน: เดิมเช็คแค่ "ไม่ใช่ Guid" แล้วฟ้อง
    `IsValidThaiTaxId(x.Id)` ที่ x เป็น tuple มี `string Id` และ
    `CheckAllApprovedAndProcessAsync(..., userId)` ที่ userId เป็น string
    ในเมธอดนั้น (ชนกับ Guid userId ที่อยู่คนละเมธอดในไฟล์เดียวกัน)
  • ข้ามทุกกรณีที่ไม่ชัด (named arg, params, generic, จำนวนอาร์กิวเมนต์ไม่ตรง)

รันแบบ self-test:  python3 tools/arg_type_check.py --self-test
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# ประกาศ method (รวม private/async/static) — เก็บชื่อ + รายการพารามิเตอร์
METHOD_DECL = re.compile(
    r'^[ \t]*(?:(?:public|private|protected|internal|static|virtual|override|'
    r'sealed|async|extern|unsafe|new|partial)\s+)+'
    r'(?:[A-Za-z_][\w<>,\[\]\.\?\s]*?)\s+'
    r'(?P<name>[A-Za-z_]\w*)\s*\((?P<params>[^()]*)\)\s*(?:where\s[^{;]*)?[{;=]',
    re.M)

# การเรียก: Name(args) — จำกัดวงเล็บชั้นเดียวเพื่อไม่ให้จับ nested ผิด
CALL = re.compile(r'(?<![\w.])(?P<name>[A-Za-z_]\w*)\s*\((?P<args>[^()]*)\)')

# ตัวแปร Guid ที่ประกาศชัด
GUID_LOCAL = re.compile(r'\bGuid\??\s+(?P<name>[A-Za-z_]\w*)\s*[=,)]')

PARAM_MODS = {'ref', 'out', 'in', 'params', 'this', 'scoped', 'readonly'}

# ประกาศ entity: class/record ใต้ Models/Entities
ENTITY_DECL = re.compile(
    r'^\s*(?:(?:public|internal|abstract|sealed|partial)\s+)+'
    r'(?:class|record)\s+(?P<name>[A-Za-z_]\w*)', re.M)

LINE_COMMENT = re.compile(r'//[^\n]*')


def load_entity_names(root=ROOT):
    """ชื่อคลาส/เรคคอร์ดใน Models/Entities — พารามิเตอร์ชนิดเหล่านี้เท่านั้น
    ที่เราฟันธงได้ว่า ".Id ใส่แทนไม่ได้แน่นอน"."""
    names = set()
    base = root / 'Accounting' / 'Models' / 'Entities'
    if not base.exists():
        return names
    for f in base.rglob('*.cs'):
        try:
            src = LINE_COMMENT.sub('', f.read_text(encoding='utf-8'))
        except (UnicodeDecodeError, OSError):
            continue
        names.update(m.group('name') for m in ENTITY_DECL.finditer(src))
    return names


def split_top_level(text):
    """แยกด้วย ',' เฉพาะระดับบนสุด (ไม่แยกใน <> [] {} '' "")"""
    parts, depth, buf, quote = [], 0, [], None
    for ch in text:
        if quote:
            buf.append(ch)
            if ch == quote:
                quote = None
            continue
        if ch in '"\'':
            quote = ch
            buf.append(ch)
            continue
        if ch in '<[{':
            depth += 1
        elif ch in '>]}':
            depth -= 1
        if ch == ',' and depth <= 0:
            parts.append(''.join(buf).strip())
            buf = []
            continue
        buf.append(ch)
    if buf:
        parts.append(''.join(buf).strip())
    return [p for p in parts if p]


def parse_params(raw):
    """คืน list ของชนิดพารามิเตอร์ (str) — คืน None เมื่อพบอะไรที่ไม่กล้าตีความ"""
    raw = LINE_COMMENT.sub('', raw)   # คอมเมนต์คั่นกลางรายการพารามิเตอร์
    if raw.strip() == '':
        return []
    out = []
    for p in split_top_level(raw):
        p = p.strip()
        if p.startswith('['):                     # attribute บนพารามิเตอร์
            p = re.sub(r'^\[[^\]]*\]\s*', '', p)
        p = p.split('=')[0].strip()               # ตัดค่า default
        toks = p.split()
        while toks and toks[0] in PARAM_MODS:
            if toks[0] == 'params':
                return None                       # variadic → ไม่ตรวจ
            toks.pop(0)
        if len(toks) < 2:
            return None                           # ตีความไม่ออก → ข้ามทั้งเมธอด
        out.append(' '.join(toks[:-1]))
    return out


# ตัวแปร camelCase ที่ลงท้ายด้วย "Id" — `allocDocId`, `documentId`, `payeeId`
# (ต้องเป็น I ตัวใหญ่ จึงไม่ชน `grid`/`valid`). ใช้ได้เพราะเราฟ้องเฉพาะตอน
# พารามิเตอร์เป็น entity — ค่า id ไม่ว่าจะเป็น Guid หรือ string ก็ใส่แทน
# entity ไม่ได้ทั้งคู่ (CS1503 เหมือนกัน)
ID_NAMED = re.compile(r'[a-z]\w*Id')


def guid_arg_kind(arg, guid_locals):
    """คืน 'guid' เมื่อมั่นใจว่าอาร์กิวเมนต์นี้เป็น "ค่า id" ไม่ใช่ตัว entity"""
    a = arg.strip()
    if not a or '=>' in a or ':' in a:            # lambda / named arg → ข้าม
        return None
    if re.fullmatch(r'[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*\.Id', a):
        return 'guid'
    if re.fullmatch(r'[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*\.Value\.Id', a):
        return 'guid'
    if re.fullmatch(r'[A-Za-z_]\w*', a) and (a in guid_locals or ID_NAMED.fullmatch(a)):
        return 'guid'
    return None


def scan_text(text, path='<memory>', entities=None):
    entities = entities if entities is not None else load_entity_names()
    problems = []

    decls = {}
    dupes = set()
    for m in METHOD_DECL.finditer(text):
        name = m.group('name')
        if name in ('if', 'while', 'for', 'foreach', 'switch', 'catch', 'lock', 'using'):
            continue
        types = parse_params(m.group('params'))
        if name in decls or name in dupes:        # overload → ไม่ตรวจ
            dupes.add(name)
            decls.pop(name, None)
            continue
        if types is None:
            continue
        decls[name] = types
    if not decls:
        return problems

    guid_locals = {m.group('name') for m in GUID_LOCAL.finditer(text)}

    for m in CALL.finditer(text):
        name = m.group('name')
        if name not in decls:
            continue
        # ข้ามบรรทัดที่เป็นตัวประกาศเมธอดเอง
        line_start = text.rfind('\n', 0, m.start()) + 1
        line = text[line_start:text.find('\n', m.end()) if text.find('\n', m.end()) > 0 else len(text)]
        if re.search(r'\b(?:public|private|protected|internal)\b.*\b' + re.escape(name) + r'\s*\(', line):
            continue

        params = decls[name]
        args = split_top_level(m.group('args'))
        if len(args) != len(params):               # optional param / ไม่ตรง → ข้าม
            continue
        for i, (arg, ptype) in enumerate(zip(args, params)):
            if guid_arg_kind(arg, guid_locals) != 'guid':
                continue
            pt = ptype.strip().rstrip('?')
            # ฟันธงเฉพาะพารามิเตอร์ที่เป็น entity ของเรพนี้ — ".Id ของอะไรก็ตาม
            # ไม่มีทางเป็น entity" ส่วนชนิดอื่น (string/int/interface/generic)
            # เดาไม่ได้ว่า arg เป็น Guid จริงไหม จึงไม่ฟ้อง
            if pt not in entities:
                continue
            lineno = text.count('\n', 0, m.start()) + 1
            problems.append(
                f"{path}:{lineno}  {name}(...) อาร์กิวเมนต์ที่ {i + 1} = `{arg}` "
                f"เป็น Guid แต่พารามิเตอร์ประกาศเป็น `{pt}` → CS1503")
    return problems


SELF_TEST_BAD = '''
public class Sample
{
    private async Task SyncWhtCreditReceivedAsync(Guid companyId, Document doc) { }

    public async Task Run(Guid companyId, Document doc, Dictionary<Guid, Document> docMap)
    {
        await SyncWhtCreditReceivedAsync(companyId, doc.Id);
        foreach (var allocDocId in docMap.Keys)
            await SyncWhtCreditReceivedAsync(companyId, allocDocId);
    }
}
'''

SELF_TEST_GOOD = '''
public class Sample
{
    private async Task SyncWhtCreditReceivedAsync(Guid companyId, Document doc) { }
    private async Task TouchAsync(Guid companyId, Guid documentId) { }

    public async Task Run(Guid companyId, Document doc, Dictionary<Guid, Document> docMap)
    {
        await SyncWhtCreditReceivedAsync(companyId, doc);
        await TouchAsync(companyId, doc.Id);
        foreach (var allocDocId in docMap.Keys)
            await SyncWhtCreditReceivedAsync(companyId, docMap[allocDocId]);
    }
}
'''


def self_test():
    ents = load_entity_names()
    if 'Document' not in ents:
        print("❌ หา entity Document ไม่เจอ — ตัวกรองหลักใช้ไม่ได้")
        return 1
    bad = scan_text(SELF_TEST_BAD, 'bad.cs', ents)
    good = scan_text(SELF_TEST_GOOD, 'good.cs', ents)
    ok = len(bad) == 2 and len(good) == 0
    print("negative test (ใส่บั๊ก 2 แบบกลับเข้าไป):", "จับได้ครบ ✅" if len(bad) == 2 else f"ไม่ครบ ❌ ({bad})")
    print("positive test (โค้ดถูก):", "เงียบ ✅" if not good else f"ฟ้องเท็จ ❌ ({good})")
    return 0 if ok else 1


def main():
    if '--self-test' in sys.argv:
        return self_test()

    files = sorted(ROOT.rglob('*.cs'))
    files = [f for f in files if '/obj/' not in f.as_posix() and '/bin/' not in f.as_posix()]
    entities = load_entity_names()
    problems = []
    for f in files:
        try:
            problems += scan_text(f.read_text(encoding='utf-8'), str(f.relative_to(ROOT)), entities)
        except (UnicodeDecodeError, OSError):
            continue

    print()
    for p in problems:
        print("  " + p)
    print(f"ตรวจ {len(files)} ไฟล์ · พบปัญหา {len(problems)} จุด")
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
