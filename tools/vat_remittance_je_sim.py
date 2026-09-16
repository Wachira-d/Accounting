# -*- coding: utf-8 -*-
# จำลอง JE นำส่ง ภ.พ.30 — ก่อนแก้ vs หลังแก้
from decimal import Decimal as D

def r2(x): return x.quantize(D('0.01'))

def je_before(output, inp, net, has11610=True):
    amount = net
    lines = [("Dr 21911", output, D(0))]
    if inp > 0:
        if has11610: lines.append(("Cr 11610", D(0), inp))
        else:        amount = output          # ← สาขาเงียบ: จ่ายเท่าภาษีขายเต็ม
    lines.append(("Cr bank", D(0), amount))
    return lines, amount

def je_after(output, inp, net, has11610=True):
    cf = r2(output - inp - net)               # เครดิตยกมา §82/3 — หาย้อนได้เป๊ะ
    if not has11610:
        return None, None, "ไม่พบผังบัญชี 11610 — ต้องสร้างก่อน (ห้ามลงเงียบ)"
    lines = [("Dr 21911", output, D(0))]
    clear = r2(inp + cf)
    if clear > 0: lines.append(("Cr 11610", D(0), clear))
    lines.append(("Cr bank", D(0), net))
    return lines, net, None

CASES = [
  ("ไม่มีเครดิตยกมา (เคสปกติ)",        D('7000'), D('3000'), D('4000')),
  ("มีเครดิตยกมา 1,200",               D('7000'), D('3000'), D('2800')),
  ("เครดิตยกมาใหญ่กว่าส่วนต่าง",        D('7000'), D('3000'), D('500')),
  ("ไม่มีภาษีซื้อเลย",                  D('5000'), D('0'),    D('5000')),
  ("ภาษีซื้อ + CF เศษสตางค์",          D('1234.56'), D('987.65'), D('100.00')),
]
for label, o, i, n in CASES:
    for tag, fn in (("ก่อนแก้", je_before), ("หลังแก้", je_after)):
        res = fn(o, i, n)
        lines, amt = res[0], res[1]
        dr = sum(l[1] for l in lines); cr = sum(l[2] for l in lines)
        ok = "สมดุล" if r2(dr) == r2(cr) else f"**ไม่สมดุล ต่าง {r2(dr-cr)}**"
        print(f"{label:34s} {tag}: Dr={r2(dr):>9} Cr={r2(cr):>9} จ่ายจริง={r2(amt):>9}  {ok}")
    print()

print("=== สาขา 'ไม่พบผัง 11610' ===")
o,i,n = D('7000'), D('3000'), D('2800')
lines, amt = je_before(o,i,n,has11610=False)
dr=sum(l[1] for l in lines); cr=sum(l[2] for l in lines)
print(f"ก่อนแก้: Dr={r2(dr)} Cr={r2(cr)} → จ่ายจริง {r2(amt)} (ควรจ่าย {r2(n)}) = **จ่ายเกิน {r2(amt-n)} เงียบ ๆ**")
lines, amt, err = je_after(o,i,n,has11610=False)
print(f"หลังแก้: ปฏิเสธพร้อมเหตุผล — {err}")
