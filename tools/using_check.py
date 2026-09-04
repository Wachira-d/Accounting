#!/usr/bin/env python3
"""
ตรวจ CS0246 "The type or namespace name 'X' could not be found" — เคสที่พบบ่อย
ที่สุดคือ **ใช้ type ในโปรเจกต์เดียวกัน แต่ลืมใส่ using ของ namespace นั้น**
(เช่น Helpers/AuditHashChain.cs ใช้ `AuditAction` ซึ่งอยู่ Accounting.Models.Enums
แต่ import มาแค่ Accounting.Models.Entities ⇒ build ล้ม + โปรเจกต์ Tests พังตาม
ด้วย CS0006 "Metadata file Accounting.dll could not be found")

env นี้ไม่มี .NET SDK จึงคอมไพล์ไม่ได้ — checker นี้ทดแทนเฉพาะ defect class นี้

หลักการ (เลือกความแม่นเหนือความครอบคลุม — false positive ต้องเป็นศูนย์):
  • index เฉพาะ type ที่ **นิยามในเรพนี้** (class/enum/interface/record/struct)
    → type ของ BCL/NuGet ไม่ถูกแตะเลย (เดาผิดไม่ได้)
  • ไฟล์หนึ่งเห็น namespace: ของตัวเอง + บรรพบุรุษทุกชั้น + ที่ using ไว้
    (C# ค้น enclosing namespace ให้ แต่ **ไม่** ค้น namespace ลูก)
  • flag เมื่อ: token เป็นชื่อ type ในเรพ ∧ ทุก namespace ที่นิยาม type นั้น
    ไม่มีตัวไหนมองเห็นได้จากไฟล์นี้
  • ตัด comment/string ออกก่อน + ข้าม token ที่นำหน้าด้วย '.' (member access)
    + ข้ามชื่อหลัง 'namespace'/'class'/... เอง

ใช้: python3 tools/using_check.py [path ...]   (ไม่ระบุ = ทั้งเรพ)
exit 1 เมื่อพบปัญหา
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# ── ตัด comment + string literal (กัน token ในข้อความไทย/URL ถูกนับเป็นการใช้ type)
_STRIP = re.compile(
    r'"""(?:.|\n)*?"""'          # (ไม่ใช่ C# แต่กันไว้)
    r'|@"(?:[^"]|"")*"'          # verbatim string
    r'|\$?"(?:\\.|[^"\\\n])*"'   # normal / interpolated string
    r"|'(?:\\.|[^'\\\n])*'"      # char
    r'|//[^\n]*'                 # line comment
    r'|/\*(?:.|\n)*?\*/',        # block comment
    re.M)

# ⚠️ `record struct X` / `record class X` มีคำนำหน้า **สองคำ** ก่อนชื่อจริง —
# ถ้าไม่ยอมให้ข้าม จะจับได้แค่ "struct" แล้วชื่อจริงหลุด ⇒ ชนิดที่ประกาศในไฟล์
# เองถูกฟ้องว่า "ไม่ได้ using" (เจอกับ `OutboundUrlGuard.Result` ซึ่งชื่อชนกับ
# `Result` ในอีก 4 namespace — ชนิดที่ชื่อไม่ชนใครไม่เคยเปิดเผยบั๊กนี้)
TYPE_DECL = re.compile(
    r'\b(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|ref|file)\s+'
    r'(?:[\w\s]*?)\b(class|struct|interface|enum|record)\s+(?:(?:struct|class)\s+)?([A-Za-z_]\w*)')
NAMESPACE_DECL = re.compile(r'^\s*namespace\s+([\w.]+)\s*[;{]', re.M)
USING_DECL = re.compile(r'^\s*(?:global\s+)?using\s+(?:static\s+)?(?:[\w]+\s*=\s*)?([\w.]+)\s*;', re.M)
DECL_AFTER = re.compile(
    r'\b(?:namespace|class|struct|interface|enum|record)\s+(?:(?:struct|class)\s+)?([A-Za-z_]\w*)')

# ── จับเฉพาะ "ตำแหน่งไวยากรณ์ที่ต้องเป็น type เท่านั้น" ────────────────────
# รอบแรกจับ PascalCase ทุกตัวที่ไม่มี '.' นำหน้า → false positive 210 จุด
# เพราะชื่อ property/พารามิเตอร์ในเรพซ้ำกับชื่อ type (Score, Row, Position,
# VatRate, Plan...). checker ที่ต้องมานั่งกรอง = ไม่มีใครเชื่อ → จับให้แคบแทน
_KW = (r'(?:return|new|case|is|as|out|ref|in|typeof|nameof|await|throw|when|and|or|not|'
       r'if|else|for|foreach|while|do|switch|using|public|private|protected|internal|'
       r'static|readonly|const|var|void|this|base|default|null|true|false)')
TYPE_POSITIONS = [
    # ประกาศตัวแปร/พารามิเตอร์/field: `AuditAction action`  (ตามด้วยชื่อ camelCase)
    re.compile(rf'(?<![\w.])(?!{_KW}\b)([A-Z]\w*)(?:\?)?\s+[a-z_]\w*\s*[,)=;]'),
    # cast: `(AuditAction)x`
    re.compile(rf'\(\s*(?!{_KW}\b)([A-Z]\w*)\s*\)\s*[\w("]'),
    # new: `new AuditAction(` / `new AuditAction {`
    re.compile(r'\bnew\s+([A-Z]\w*)\s*[({<]'),
    # static/enum member: `AuditAction.Create` (ตัวแปรตั้งชื่อ camelCase ตามแนวเรพ)
    re.compile(r'(?<![\w.])([A-Z]\w*)\.[A-Za-z_]'),
]

# generic argument list — จับทั้งวงเล็บ `<...>` แล้วค่อยดึงชื่อข้างใน (แยกจาก
# rule ข้างบนเพราะ regex เดี่ยวที่ยอมให้ ',' นำหน้า ไปจับรายการสมาชิก enum
# (`CompanyName,\n BankAccount,\n Url`) ว่าเป็น generic arg = false positive)
# ⚠️ ห้ามใส่ '<' '>' ในคลาสอักขระข้างใน — จะกินข้ามวงเล็บปิดไปคร่อมชื่อ
# parameter ตัวถัดไป (`List<TrendPoint> RevenueTrend, List<TrendPoint>` ถูกอ่าน
# เป็น generic เดียวกัน ⇒ RevenueTrend กลายเป็น "type ที่ไม่ได้ using")
# generic ซ้อน (`Dictionary<string, List<X>>`) จะจับได้เฉพาะวงในสุด — พอแล้ว
GENERIC_SPAN = re.compile(r'<([\w\s,?\[\]]{1,120})>')
INNER_TYPE = re.compile(r'(?<![\w.])([A-Z]\w*)')

# สมาชิกที่ **สืบทอดมาจาก base class ของเฟรมเวิร์ก** — ชื่อชนกับ type ในเรพ
# (`ControllerBase.User` vs entity `User`, `Hub.Context` vs `Context` ของ Tax)
# ⇒ `User.FindFirst(...)` ไม่ใช่การอ้าง type. ไม่ใส่ = 16 false positive
FRAMEWORK_MEMBERS = {
    'User', 'Context', 'Request', 'Response', 'Url', 'Clients', 'Groups',
    'HttpContext', 'ModelState', 'Items', 'Configuration', 'Environment',
}


def strip_noise(src: str) -> str:
    """ลบ comment/string แต่ **คงจำนวนบรรทัดเดิม** เพื่อให้เลขบรรทัดที่รายงาน
    ตรงกับที่ compiler ชี้ (comment /* */ หลายบรรทัดถ้ายุบเป็นช่องว่างเดียว
    บรรทัดจะเลื่อนทั้งไฟล์)"""
    return _STRIP.sub(lambda m: '\n' * m.group(0).count('\n') or ' ', src)


def file_namespace(src: str) -> str:
    m = NAMESPACE_DECL.search(src)
    return m.group(1) if m else ''


def visible_namespaces(ns: str, usings: set) -> set:
    """namespace ที่ไฟล์นี้อ้าง type ได้โดยไม่ต้อง qualify"""
    vis = set(usings)
    parts = ns.split('.') if ns else []
    for i in range(len(parts), 0, -1):
        vis.add('.'.join(parts[:i]))
    return vis


def build_index(files):
    """ชื่อ type → set(namespace ที่นิยาม)  — partial/ชื่อซ้ำได้หลาย namespace"""
    index = {}
    for f in files:
        src = strip_noise(f.read_text(encoding='utf-8', errors='replace'))
        ns = file_namespace(src)
        for _, name in TYPE_DECL.findall(src):
            index.setdefault(name, set()).add(ns)
    return index


def check_file(f: Path, index: dict):
    raw = f.read_text(encoding='utf-8', errors='replace')
    src = strip_noise(raw)
    ns = file_namespace(src)
    usings = set(USING_DECL.findall(src))
    vis = visible_namespaces(ns, usings)
    # ชื่อที่ประกาศ "ในไฟล์นี้เอง" — ไม่ต้อง using
    declared_here = {n for _, n in TYPE_DECL.findall(src)} | set(DECL_AFTER.findall(src))

    used_as_type = set()
    for rx in TYPE_POSITIONS:
        used_as_type.update(rx.findall(src))
    for span in GENERIC_SPAN.findall(src):
        used_as_type.update(INNER_TYPE.findall(span))
    used_as_type -= FRAMEWORK_MEMBERS

    problems = []
    seen = set()
    for tok in sorted(used_as_type):
        if tok in seen or tok in declared_here:
            continue
        owners = index.get(tok)
        if not owners:
            continue                      # ไม่ใช่ type ในเรพ → ไม่เดา
        if owners & vis:
            continue                      # เห็นได้อย่างน้อยหนึ่ง namespace
        seen.add(tok)
        # หาเลขบรรทัดแรกที่ใช้จริง (จากไฟล์ที่ strip แล้ว map กลับด้วยการค้นดิบ)
        line = next((i for i, l in enumerate(raw.splitlines(), 1)
                     if re.search(rf'(?<![\w.]){re.escape(tok)}\b', l)), 0)
        problems.append((line, tok, sorted(owners)))
    return sorted(problems)


def main():
    targets = [Path(a) for a in sys.argv[1:]] or [ROOT]
    all_cs = sorted(p for p in ROOT.rglob('*.cs')
                    if 'obj' not in p.parts and 'bin' not in p.parts)
    index = build_index(all_cs)

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
        for line, tok, owners in check_file(f, index):
            bad += 1
            rel = f.relative_to(ROOT)
            print(f'{rel}:{line}: CS0246 เสี่ยง — ใช้ `{tok}` แต่ไม่ได้ using '
                  f'(นิยามที่: {", ".join(o or "<global>" for o in owners)})')

    print(f'\nตรวจ {len(scan)} ไฟล์ · type ในเรพ {len(index)} ชนิด · พบปัญหา {bad} จุด')
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
