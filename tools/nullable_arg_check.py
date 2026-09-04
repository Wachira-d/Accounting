# -*- coding: utf-8 -*-
"""ตรวจ "ส่ง int?/decimal? เข้าพารามิเตอร์ที่ไม่รับ null" (CS1503)

รันจาก root ของ repo:  python3 tools/nullable_arg_check.py

ทำไมต้องมี: env พัฒนาไม่มี .NET SDK (proxy บล็อกเซิร์ฟเวอร์ของ Microsoft)
จึง build ไม่ได้ ⇒ type error ชนิดนี้หลุดไปถึงเครื่องผู้ใช้มาแล้ว
(`CreditDaysText(doc.CreditDays)` โดยที่ CreditDays เป็น int?)

ขอบเขต (จงใจแคบเพื่อความแม่น — เตือนน้อยแต่เตือนถูก):
  • ดูเฉพาะเมธอดที่ "นิยามในโปรเจกต์นี้" และพารามิเตอร์เป็น value type ที่ไม่ใช่ nullable
  • argument ต้องอยู่ในรูป `x.Prop` และชื่อ Prop นั้นถูกประกาศเป็น nullable
    **ทุกที่** ในโปรเจกต์ (ถ้ามีที่ไหนประกาศแบบไม่ nullable ด้วย = กำกวม ข้าม)
  • ข้ามเมื่อ argument มี .Value / ?? / cast / GetValueOrDefault อยู่แล้ว
"""
import re, os, sys
from collections import defaultdict

VALUE_TYPES = {'int','long','short','byte','decimal','double','float','bool',
               'Guid','DateTime','DateTimeOffset','TimeSpan','char'}

cs_files = []
for root, dirs, files in os.walk('Accounting'):
    dirs[:] = [d for d in dirs if d not in ('obj', 'bin')]
    cs_files += [os.path.join(root, f) for f in files if f.endswith('.cs')]

# 1) ชนิดของ property/field ตามชื่อ — เก็บทุกการประกาศเพื่อดูความกำกวม
#    ต้องเก็บ **ทั้ง 2 รูปแบบ** ไม่งั้นเกิด false positive: เคย fire ผิดที่
#    AiFeedbackController เพราะเห็นแต่ `public Guid? FeedbackId { get; init; }`
#    ของ AiResponse แล้วสรุปว่า "FeedbackId เป็น nullable ทุกที่" ทั้งที่
#    positional record `RecordChoiceRequest(Guid FeedbackId, ...)` ประกาศเป็น
#    Guid ธรรมดา
prop_types = defaultdict(set)


def _index_param_list(params, sink):
    """แยก `Type Name` จาก parameter list ของ positional record"""
    for part in re.split(r',(?![^<(]*[>)])', params):
        part = re.sub(r'//.*', '', part).split('=')[0].strip()
        if not part:
            continue
        toks = part.split()
        if len(toks) >= 2 and re.fullmatch(r'\w+', toks[-1]):
            sink[toks[-1]].add(toks[-2])


for f in cs_files:
    src = open(f, encoding='utf-8').read()
    # property syntax:  public int? Foo { get; set; }
    for m in re.finditer(r'public\s+([\w<>\.]+\??)\s+(\w+)\s*\{\s*get', src):
        prop_types[m.group(2)].add(m.group(1))
    # positional record:  public record Foo(int? Bar, string Baz);
    # (รวม record struct / sealed record — parameter list อาจกินหลายบรรทัด)
    for m in re.finditer(r'\brecord(?:\s+struct|\s+class)?\s+\w+\s*\(([^;{]*?)\)\s*[;{:]',
                         src, re.S):
        _index_param_list(m.group(1), prop_types)

# 2) เมธอดที่นิยามในโปรเจกต์ + ชนิดพารามิเตอร์
#
# เก็บ **สองชั้น**: ทั้งเรพ (methods) และต่อไฟล์ (methods_in_file) — เวลาไล่จุดเรียก
# ให้ **ชั้นใกล้ชนะ**: ถ้าไฟล์ที่เรียกประกาศเมธอดชื่อนั้นเอง ต้องใช้ตัวนั้นเท่านั้น
# ห้ามไปหยิบเมธอดชื่อซ้ำจากไฟล์อื่น (กติกาเดียวกับ namespace_shadow_check)
#
# ที่มาของกฎนี้: `PosService.Normalize(Guid?)` (private static ในไฟล์นั้นเอง) ถูก
# จับคู่กับ `Normalize(decimal)` ของอีกไฟล์ ⇒ ฟ้องผิด 4 จุดบนโค้ดที่ถูกต้อง
# — "checker ที่ฟ้องผิด = checker ที่พังแล้ว" ต้องแก้ checker ไม่ใช่เลี่ยงโค้ด
methods = defaultdict(list)   # name -> [ [(type, pname), ...], ... ]
methods_in_file = defaultdict(lambda: defaultdict(list))   # file -> name -> [plist, ...]
for f in cs_files:
    src = open(f, encoding='utf-8').read()
    for m in re.finditer(
            r'(?:public|private|internal|protected)\s+(?:static\s+|async\s+|override\s+|sealed\s+|virtual\s+)*'
            r'[\w<>\[\]\.\?]+\s+(\w+)\s*\(([^)]*)\)\s*(?:=>|\{)', src):
        name, params = m.group(1), m.group(2)
        if name in ('if','while','for','foreach','switch','catch','lock','using','return'): continue
        plist = []
        for part in re.split(r',(?![^<]*>)', params):
            part = re.sub(r'//.*', '', part).strip()
            if not part: continue
            toks = part.split('=')[0].strip().split()
            if len(toks) >= 2:
                plist.append((toks[-2], toks[-1]))
        methods[name].append(plist)
        methods_in_file[f][name].append(plist)

bad = []
# จับทั้ง `Method(arg)` และ `receiver.Method(arg)` — เวอร์ชันแรกใช้ negative
# lookbehind กัน `.` ไว้ ทำให้พลาดเคสที่พบบ่อยที่สุด (`L.CreditDaysText(...)`)
# ซึ่งคือบั๊กจริงที่ต้องจับ. ตัวกรอง keyword + "ต้องเป็นเมธอดที่นิยามในโปรเจกต์"
# ทำหน้าที่กันเสียงรบกวนแทนอยู่แล้ว
call_re = re.compile(r'(\w+)\s*\(\s*([A-Za-z_]\w*(?:\.\w+)+)\s*[,)]')
for f in cs_files:
    src = open(f, encoding='utf-8').read()
    for i, line in enumerate(src.split('\n'), 1):
        code = re.sub(r'//.*', '', line)
        for m in call_re.finditer(code):
            mname, arg = m.group(1), m.group(2)
            if mname not in methods: continue
            # ชั้นใกล้ชนะ: ไฟล์นี้ประกาศเองไหม
            overloads = methods_in_file[f].get(mname) or methods[mname]
            # argument ที่ป้องกันไว้แล้ว
            if any(t in code[m.start():m.end()] for t in ('.Value', '??', 'GetValueOrDefault')): continue
            prop = arg.split('.')[-1]
            decls = prop_types.get(prop)
            if not decls: continue
            # ต้องเป็น nullable "ทุกที่" ที่ประกาศ ไม่งั้นกำกวม
            if not all(d.endswith('?') for d in decls): continue
            base = next(iter(decls)).rstrip('?')
            if base not in VALUE_TYPES: continue
            # พารามิเตอร์ตัวแรกของ overload ใด ๆ รับ non-nullable value type ไหม
            for plist in overloads:
                if not plist: continue
                ptype = plist[0][0]
                if ptype in VALUE_TYPES:      # ไม่มี ? ต่อท้าย = ไม่รับ null
                    bad.append((f, i, mname, arg, base, ptype))
                    break

if bad:
    print(f'❌ พบ {len(bad)} จุดที่ส่ง nullable เข้าพารามิเตอร์ที่ไม่รับ null:\n')
    for f, i, mn, arg, at, pt in bad:
        print(f'  {f}:{i}')
        print(f'    {mn}({arg})  —  {arg} เป็น {at}?  แต่พารามิเตอร์รับ {pt}')
        print(f'    แก้: ใส่ .Value (ถ้ามี guard แล้ว) หรือ ?? ค่าเริ่มต้น\n')
    sys.exit(1)
print(f'✅ ไม่พบการส่ง nullable เข้าพารามิเตอร์ที่ไม่รับ null '
      f'(ตรวจ {len(cs_files)} ไฟล์ · เมธอด {len(methods)} ชื่อ)')
