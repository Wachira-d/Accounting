#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ตรวจ CS0051 / CS0050 — "ชนิดที่เข้าถึงได้น้อยกว่า" ในลายเซ็น method สาธารณะ

    CS0051  พารามิเตอร์เป็นชนิด private/protected แต่ method เป็น public
    CS0050  ชนิดที่คืนค่าเป็น private/protected แต่ method เป็น public

ที่มา: เทสต์ [Theory] ต้องเป็น public (xUnit บังคับ) แต่เขียน enum ช่วยเป็น
`private enum Nature` แล้วรับเป็นพารามิเตอร์ → คอมไพล์ไม่ผ่านทั้ง solution
(env นี้ไม่มี .NET SDK จึงไม่เจอจนกว่าจะไป build ฝั่งผู้ใช้)

ขอบเขต (ตั้งใจแคบเพื่อไม่ให้ false positive): ตรวจเฉพาะ **nested type ที่ประกาศ
private/protected ในคลาสเดียวกัน** แล้วถูกใช้ในลายเซ็นของ method ที่เป็น public
ในคลาสนั้น — ซึ่งเป็นเคสที่เกิดจริง. ไม่ตรวจข้ามไฟล์/ข้าม assembly

รันแบบ self-test:  python3 tools/accessibility_check.py --self-test
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# ประกาศ nested type: <modifiers> (class|struct|record|enum|interface) <Name>
TYPE_DECL = re.compile(
    r'^\s*(?P<mods>(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|file|new)\s+)+)'
    r'(?P<kind>class|struct|record\s+struct|record|enum|interface)\s+'
    r'(?P<name>[A-Za-z_]\w*)',
    re.M)

# ลายเซ็น method: <modifiers> <returnType> <Name>(<params>)
METHOD_DECL = re.compile(
    r'^[ \t]*(?P<mods>(?:(?:public|private|protected|internal|static|virtual|override|sealed|async|extern|unsafe|new|partial)\s+)+)'
    r'(?P<ret>[A-Za-z_][\w<>,\[\]\.\? ]*?)\s+'
    r'(?P<name>[A-Za-z_]\w*)\s*(?:<[^>()]*>)?\s*\((?P<params>[^)]*)\)',
    re.M)

IDENT = re.compile(r'[A-Za-z_]\w*')

# คำที่ไม่ใช่ชนิด — กันจับ modifier/keyword ในรายการพารามิเตอร์
PARAM_KEYWORDS = {
    'ref', 'out', 'in', 'params', 'this', 'readonly', 'scoped',
    'string', 'int', 'long', 'short', 'byte', 'bool', 'char', 'decimal',
    'double', 'float', 'object', 'void', 'var', 'dynamic', 'uint', 'ulong',
    'ushort', 'sbyte', 'nint', 'nuint', 'default', 'null', 'true', 'false',
    'new', 'typeof', 'nameof',
}


def strip_comments_and_strings(src: str) -> str:
    """ตัดคอมเมนต์/สตริงออกแต่ **คงจำนวนบรรทัด** (เลขบรรทัดต้องไม่เลื่อน)"""
    out = []
    i, n = 0, len(src)
    while i < n:
        c = src[i]
        if c == '/' and i + 1 < n and src[i + 1] == '/':
            j = src.find('\n', i)
            if j < 0:
                break
            out.append(' ' * (j - i))
            i = j
        elif c == '/' and i + 1 < n and src[i + 1] == '*':
            j = src.find('*/', i + 2)
            j = n if j < 0 else j + 2
            out.append(''.join(ch if ch == '\n' else ' ' for ch in src[i:j]))
            i = j
        elif c == '"':
            # verbatim / interpolated / ปกติ — ข้ามไปจนจบสตริง
            j = i + 1
            while j < n:
                if src[j] == '\\' and src[i - 1:i] != '@':
                    j += 2
                    continue
                if src[j] == '"':
                    j += 1
                    break
                j += 1
            out.append(''.join(ch if ch == '\n' else ' ' for ch in src[i:j]))
            i = j
        else:
            out.append(c)
            i += 1
    return ''.join(out)


def body_span(src: str, open_brace: int):
    """คืนช่วง (start, end) ของบล็อกที่เปิดด้วย { ที่ตำแหน่ง open_brace"""
    depth = 0
    for i in range(open_brace, len(src)):
        if src[i] == '{':
            depth += 1
        elif src[i] == '}':
            depth -= 1
            if depth == 0:
                return open_brace, i
    return open_brace, len(src)


def analyze(src: str, path_label: str):
    """คืน list ของ (line, method, type, kind) ที่เป็นปัญหา"""
    clean = strip_comments_and_strings(src)
    problems = []

    for m in TYPE_DECL.finditer(clean):
        # หา container: type ที่ประกาศแล้วมี body ครอบ decl อื่น ๆ
        brace = clean.find('{', m.end())
        if brace < 0:
            continue
        start, end = body_span(clean, brace)
        body = clean[start:end]

        # ชนิดลูกที่ประกาศ private/protected ในคลาสนี้
        restricted = {}
        for d in TYPE_DECL.finditer(body):
            mods = d.group('mods')
            if 'private' in mods or ('protected' in mods and 'internal' not in mods):
                restricted[d.group('name')] = d.group('kind').strip()
        if not restricted:
            continue

        # method ที่เป็น public ในคลาสนี้ (ไม่รวมของคลาสลูกที่ nested ลึกกว่า —
        # เคสนั้นชนิดยังมองเห็นกันได้อยู่ดี จึงไม่ใช่ปัญหา)
        for f in METHOD_DECL.finditer(body):
            if 'public' not in f.group('mods'):
                continue
            if f.group('name') in ('if', 'for', 'foreach', 'while', 'switch', 'catch', 'lock', 'using', 'return'):
                continue
            line = clean.count('\n', 0, start + f.start()) + 1

            used = []
            for ident in IDENT.findall(f.group('params')):
                if ident in PARAM_KEYWORDS:
                    continue
                if ident in restricted:
                    used.append((ident, 'พารามิเตอร์', 'CS0051'))
            for ident in IDENT.findall(f.group('ret')):
                if ident in restricted:
                    used.append((ident, 'ชนิดที่คืนค่า', 'CS0050'))

            for name, where, code in dict.fromkeys(used):
                problems.append((line, f.group('name'), name,
                                 restricted[name], where, code))
    return problems


SELF_TEST_BAD = """
namespace X;
public class T
{
    private enum Nature { A, B }
    public void Falls_back(Nature n, string s) { }
}
"""

SELF_TEST_GOOD = """
namespace X;
public class T
{
    public enum Nature { A, B }
    private sealed record Doc(string Type);
    private static string Block(Doc d) => d.Type;
    public void Ok(Nature n, string s) { var d = new Doc("x"); }
    public void AlsoOk(string s) { }
}
"""


def main():
    if '--self-test' in sys.argv:
        bad = analyze(SELF_TEST_BAD, '<bad>')
        good = analyze(SELF_TEST_GOOD, '<good>')
        ok = len(bad) == 1 and bad[0][5] == 'CS0051' and len(good) == 0
        print('negative test (ต้องจับได้ 1 จุด):', len(bad), bad)
        print('positive test (ต้องไม่จับอะไร):', len(good), good)
        print('✅ checker ใช้ได้' if ok else '❌ checker ยังไม่ถูก')
        return 0 if ok else 1

    files = [p for p in ROOT.rglob('*.cs')
             if 'obj' not in p.parts and 'bin' not in p.parts]
    total = 0
    for p in files:
        try:
            src = p.read_text(encoding='utf-8')
        except (UnicodeDecodeError, OSError):
            continue
        for line, method, tname, kind, where, code in analyze(src, str(p)):
            total += 1
            rel = p.relative_to(ROOT)
            print(f'{rel}:{line}  {code}  method `{method}` เป็น public '
                  f'แต่ {where} ใช้ {kind} `{tname}` ที่เป็น private/protected')
            print(f'    แก้: เปลี่ยน `{tname}` เป็น public '
                  f'(เทสต์ [Theory]/[Fact] ต้อง public) หรือลด method เป็น private')

    print(f'\nตรวจ {len(files)} ไฟล์ · พบปัญหา {total} จุด')
    return 1 if total else 0


if __name__ == '__main__':
    sys.exit(main())
