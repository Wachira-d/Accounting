#!/usr/bin/env python3
"""ฟ้องหน้าเว็บที่ตัดสิน enum ของเซิร์ฟเวอร์ด้วย **ตัวเลข**

ที่มา (บั๊กจริง รอบ 159): `Program.cs` ลงทะเบียน `JsonStringEnumConverter` ⇒ ทุก enum
ถูก serialize เป็น **ชื่อ** ("Pending") ไม่ใช่ตัวเลข. แต่หลายหน้าเขียนตัวแมป
`{ 0:'รอยืนยัน', 1:'ยืนยันแล้ว', ... }` แล้ว index ด้วย `x.status` หรือเทียบ
`b.status === 0` ⇒ **ไม่ match อะไรเลยและเงียบ**:

  • cms-edit.html แท็บออเดอร์/การจอง/ฟอร์ม โชว์ชื่ออังกฤษดิบแทนป้ายไทย
  • ปุ่ม "ยืนยัน/เสร็จสิ้น/ยกเลิก" ของการจอง **ไม่เคย render เลย** (`b.status === 0` เท็จเสมอ)
    และถ้า render ก็ส่งเลขที่ความหมายเลื่อนไปแล้ว (BookingStatus 2 = กำลังให้บริการ ไม่ใช่ "เสร็จสิ้น")
  • การ์ดใน cms-sites โชว์ชนิดเว็บเป็น "-" มาตลอด (`TYPE_LABEL[s.siteType]` คีย์ตัวเลข)

กติกา: ค่าที่มาจาก API ให้เทียบ/แมปด้วย **ชื่อ enum** เสมอ และป้ายไทยอยู่ที่
`Layout.CMS_LABELS` ที่เดียว (`Layout.cmsLabel(kind, value)`).

ข้อยกเว้นที่ตรวจแล้วว่าเป็น **ตัวเลขจริง** (ไม่ใช่ enum ของ API) มี 4 ทาง และทุกทางต้อง
เป็นคุณสมบัติของโค้ดเอง ไม่ใช่รายชื่อไฟล์ที่ทิ้งไว้เฉย ๆ:
  1. HTTP status (`res.status === 404` · รับ optional chaining ด้วย)
  2. พร็อพเพอร์ตี้ของ DOM (`nodeType`)
  3. บรรทัดที่รับทั้งสองรูปอยู่แล้ว (`x !== 5 && x !== 'Project'`) หรือมี `typeof … === 'number'`
  4. ค่าที่หน้าเว็บ normalize ชื่อ→เลขเองก่อน (`this._x = this._normX(api.y)`)
"""
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'Accounting', 'wwwroot')

# ชื่อพร็อพเพอร์ตี้ที่ "เกือบแน่ว่าเป็น enum ของเซิร์ฟเวอร์"
ENUM_PROP = r'(?:status|Status|type|Type|mode|Mode|kind|Kind)'

# 1) เทียบตรง ๆ กับตัวเลข:  x.status === 0 / x.siteType == 2 / b.status < 2
CMP = re.compile(r'\.(\w*' + ENUM_PROP + r')\s*(===|==|!==|!=|<|>|<=|>=)\s*(\d+)\b')
# 2) index ตัวแมปด้วยค่า enum:  STATUS[o.status] / ARR[p.status]
IDX = re.compile(r'\[\s*\w+\.(\w*' + ENUM_PROP + r')\s*\]')

# regex ของบรรทัดที่เป็น HTTP status (ตัดออกก่อน เพราะพบเยอะและไม่ใช่ enum โดเมน)
# ต้องรับ optional chaining ด้วย (`ae?.status === 403`) — ไม่งั้นฟ้องผิดที่โค้ดจัดการ 403
HTTP_CTX = re.compile(r'\b(res|r|resp|response|e|err|ae|xhr)\??\.(status)\b')
# เลข HTTP status ที่พบจริงในเรพ — เทียบกับเลขพวกนี้ไม่มีทางเป็น enum ของโดเมน
HTTP_CODES = {'200', '201', '204', '400', '401', '402', '403', '404', '409', '422', '429', '500', '502', '503'}

# พร็อพเพอร์ตี้ของ DOM/เบราว์เซอร์ที่ "เป็นตัวเลขจริง" ไม่ใช่ enum ของเซิร์ฟเวอร์
DOM_PROPS = {'nodeType', 'readyState', 'buttonType', 'keyType'}

# บรรทัดที่เทียบกับ **ชื่อ enum** ด้วย = ผู้เขียนตั้งใจรับทั้งสองรูป
# (เช่น `x === 'Equity' || x === 3` และทิศลบ `x !== 5 && x !== 'Project'`)
DUAL_FORM = re.compile(r"[=!]==?\s*'[A-Za-z]")
# บรรทัดที่ยืนยันชนิดก่อนใช้ (`typeof c.mode === 'number' ? map[c.mode] : c.mode`)
TYPEOF_GUARD = re.compile(r"typeof\s+\w+\.\w+\s*===\s*'number'")

SKIP_DIRS = {'lib', 'vendor', 'node_modules'}

# 3) (รอบ 193 · A06) <select> ที่ option เป็นตัวเลขล้วน แต่ถูก hydrate จากพร็อพเพอร์ตี้ enum ของ API
#    `document.getElementById('provider').value = d.provider` — API ส่ง "Smtp" ⇒ ตั้งไม่ติด
#    (selectedIndex -1, value "") ⇒ การ์ดที่ผูกกับค่าหายทั้งหมด และตอนบันทึก parseInt("") = NaN → null
#    ⇒ enum non-nullable โยน body ทิ้งทั้งก้อน. กติกา CMP/IDX ข้างบนมองไม่เห็นเพราะไม่มีการเทียบตัวเลขในบรรทัดเดียว
HYDRATE_PROP = r'\w*(?:status|Status|type|Type|mode|Mode|kind|Kind|provider|Provider)'
SELECT_BLOCK = re.compile(r'<select\b[^>]*\bid="(?P<id>[\w-]+)"[^>]*>(?P<body>.*?)</select>', re.S)
OPTION_VALUE = re.compile(r'<option\b[^>]*\bvalue="([^"]*)"')


def scan(path, rel, problems):
    try:
        text = open(path, encoding='utf-8', errors='replace').read()
    except OSError:
        return
    # ตัดคอมเมนต์บรรทัดเดียวออก เพื่อไม่ให้หมายเหตุที่อธิบายบั๊กนี้ถูกนับเป็นโค้ดจริง
    # (บทเรียนซ้ำของ localstorage_key_check / css_var_check — checker ต้องไม่ฟ้องเอกสารของตัวเอง)
    lines = text.split('\n')
    for i, raw in enumerate(lines, 1):
        line = re.sub(r'//.*$', '', raw)
        if not line.strip() or line.strip().startswith(('*', '<!--')):
            continue
        if DUAL_FORM.search(line) or TYPEOF_GUARD.search(line):
            continue          # รับทั้งชื่อและตัวเลขอยู่แล้ว = ตั้งใจ ไม่ใช่บั๊ก
        for m in CMP.finditer(line):
            seg = line[max(0, m.start() - 14):m.end()]
            if HTTP_CTX.search(seg) or m.group(1) in DOM_PROPS:
                continue
            if m.group(1) == 'status' and m.group(3) in HTTP_CODES:
                continue
            # `this._xxx === 1` โดยที่ `_xxx` ถูกป้อนผ่าน `this._normXxx(...)` ซึ่งแปลง
            # ชื่อ enum → เลขให้แล้ว = เลขของหน้าเว็บเอง ไม่ใช่ค่าดิบจาก API
            prop = m.group(1)
            if re.search(r"this\." + re.escape(prop) + r"\b", line) and \
               re.search(r"this\." + re.escape(prop) + r"\s*=\s*this\._norm", text):
                continue
            problems.append((rel, i, f'.{m.group(1)} {m.group(2)} {m.group(3)}', raw.strip()[:110]))
        for m in IDX.finditer(line):
            # ตัวแมปที่ถูก index ต้องมีคีย์เป็นตัวเลขถึงจะผิด — หาคำประกาศในไฟล์เดียวกัน
            name = re.search(r'(\w+)\s*\[\s*\w+\.' + re.escape(m.group(1)), line)
            if not name:
                continue
            decl = re.search(r'\b(?:const|let|var)\s+' + re.escape(name.group(1)) + r'\s*=\s*[{\[]([^\n]{0,200})', text)
            if not decl:
                continue
            head = decl.group(1)
            # คีย์ตัวเลข ({0:'…'} หรือ array literal ['','Held',…]) = ตัดสินด้วยตัวเลข
            if re.match(r"\s*\d+\s*:", head) or re.match(r"\s*['\"]", head):
                problems.append((rel, i, f'{name.group(1)}[.{m.group(1)}] (คีย์ตัวเลข)', raw.strip()[:110]))


def scan_numeric_select_hydrate(text, rel, problems):
    """กติกาที่ 3 — select ตัวเลขล้วนที่ถูกตั้งค่าจาก `<obj>.<enumish>` ของ API"""
    lines = text.split('\n')
    for m in SELECT_BLOCK.finditer(text):
        values = [v for v in OPTION_VALUE.findall(m.group('body')) if v != '']
        if len(values) < 2 or not all(v.isdigit() for v in values):
            continue
        sid = re.escape(m.group('id'))
        hyd = re.compile(r"getElementById\(\s*['\"]" + sid + r"['\"]\s*\)\.value\s*=\s*\w+\??\.(" + HYDRATE_PROP + r")\b(?!\s*[!=]==?)")   # `x.t === 'ภ.ง.ด.3' ? '3' : '53'` = แปลงเองแล้ว
        for h in hyd.finditer(text):
            line_no = text.count('\n', 0, h.start()) + 1
            line = lines[line_no - 1]
            code = re.sub(r'//.*$', '', line)
            if h.group(0) not in code or TYPEOF_GUARD.search(line):
                continue      # อยู่ในคอมเมนต์ / ตรวจชนิดก่อนแล้ว
            problems.append((rel, line_no,
                             f'<select id="{m.group("id")}"> option ตัวเลข ← .{h.group(1)}',
                             line.strip()[:110]))


SELF_TEST_BAD = """<select id="provider" onchange="sw()">
  <option value="0">SMTP</option><option value="1">Graph</option><option value="2">Gmail</option>
</select>
<script>
  document.getElementById('provider').value = d.provider;
</script>"""

SELF_TEST_OK = """<select id="provider"><option value="Smtp">SMTP</option><option value="GmailApi">Gmail</option></select>
<select id="calcMonth"><option value="1">ม.ค.</option><option value="2">ก.พ.</option></select>
<select id="mForm"><option value="3">3</option><option value="53">53</option></select>
<script>
  document.getElementById('provider').value = d.provider;
  document.getElementById('calcMonth').value = r.month;
  document.getElementById('mForm').value = r.payerFormType === 'X' ? '3' : '53';
</script>"""


def self_test():
    """negative test ของกติกาที่ 3 — ต้องจับหน้าก่อนแก้ และไม่ฟ้อง option ชื่อ/ตัวเลขที่ไม่ใช่ enum (F2 ข้อ 6)"""
    bad, ok = [], []
    scan_numeric_select_hydrate(SELF_TEST_BAD, 'bad.html', bad)
    scan_numeric_select_hydrate(SELF_TEST_OK, 'ok.html', ok)
    passed = len(bad) == 1 and not ok
    print('✅ self-test ผ่าน 2 ทิศ (จับ select ตัวเลข←enum · ไม่ฟ้อง option ชื่อ/เดือน/ค่าที่แปลงเองด้วย ===)' if passed
          else f'❌ self-test ไม่ผ่าน — bad={bad} ok={ok}')
    return 0 if passed else 1


def main():
    if '--self-test' in sys.argv:
        return self_test()
    problems = []
    for base, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for fn in files:
            if not fn.endswith(('.html', '.js')):
                continue
            path = os.path.join(base, fn)
            scan(path, os.path.relpath(path, ROOT), problems)
            if fn.endswith('.html'):
                try:
                    txt = open(path, encoding='utf-8', errors='replace').read()
                except OSError:
                    continue
                scan_numeric_select_hydrate(txt, os.path.relpath(path, ROOT), problems)

    for rel, line, what, src in problems:
        print(f'❌ {rel}:{line}: {what} — enum ของ API มาเป็น "ชื่อ" ไม่ใช่ตัวเลข')
        print(f'    => {src}')
    print(f'\nตรวจ wwwroot · การตัดสิน enum ด้วยตัวเลข {len(problems)} จุด')
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
