# -*- coding: utf-8 -*-
ALL = [
 ("นาย", ["Mr.","Mr"]), ("นาง", ["Mrs.","Mrs"]),
 ("นางสาว", ["น.ส.","น.ส","นส.","Miss","Ms.","Ms"]),
 ("เด็กชาย", ["ด.ช.","ด.ช","ดช.","Master"]), ("เด็กหญิง", ["ด.ญ.","ด.ญ","ดญ."]),
 ("ดร.", ["ดร","Dr.","Dr"]),
 ("บริษัท", ["บจก.","บมจ.","บริษัทมหาชนจำกัด"]),
 ("ห้างหุ้นส่วนจำกัด", ["หจก.","หจก"]), ("ห้างหุ้นส่วนสามัญ", ["หสน.","หสน"]),
 ("คณะบุคคล", []), ("มูลนิธิ", []), ("สมาคม", []),
]
def cands():
    out=[]
    for thai,al in ALL:
        for f in al+[thai]: out.append((f,thai,f==thai))
    return sorted(out,key=lambda x:-len(x[0]))
def comb(c):
    o=ord(c); return o==0x0E31 or 0x0E33<=o<=0x0E3A or 0x0E47<=o<=0x0E4E
def split(name, new):
    name=(name or "").strip()
    if not name: return ("","")
    for form,canon,iscanon in cands():
        if not name.lower().startswith(form.lower()): continue
        rest=name[len(form):].lstrip()
        if not rest: continue
        fbs=name[len(form)].isspace()
        if not fbs:
            if ' ' not in rest: continue
            if new:
                if not iscanon and not form.endswith('.'): continue
                if comb(rest[0]): continue
        return (canon,rest)
    return ("",name)

CASES=[
 ("ดรุณี ใจดี",        ("","ดรุณี ใจดี"),      "ชื่อจริงขึ้นต้น ดร — ห้ามตัด"),
 ("Drake Co Ltd",      ("","Drake Co Ltd"),   "ชื่ออังกฤษขึ้นต้น Dr — ห้ามตัด"),
 ("นางสาวสมหญิง ใจดี", ("นางสาว","สมหญิง ใจดี"),"รูปเต็มติดชื่อ — ต้องตัด"),
 ("น.ส.สมหญิง ใจดี",   ("นางสาว","สมหญิง ใจดี"),"ตัวย่อมีจุดติดชื่อ — ต้องตัด"),
 ("นาย สมชาย ใจดี",    ("นาย","สมชาย ใจดี"),   "มีช่องว่าง — ต้องตัด"),
 ("ดร. สมชาย ใจดี",    ("ดร.","สมชาย ใจดี"),   "ดร. มีช่องว่าง — ต้องตัด"),
 ("เด็กชาย สมชาย ใจดี",("เด็กชาย","สมชาย ใจดี"),"เด็กชาย — ต้องตัด"),
 ("นายช่างการไฟฟ้า",   ("","นายช่างการไฟฟ้า"), "ชื่อร้าน — ห้ามตัด"),
 ("นางเลิ้งพาณิชย์",    ("","นางเลิ้งพาณิชย์"),  "ชื่อร้าน — ห้ามตัด"),
 ("หจก. แอม แฮปปี้เนส",("ห้างหุ้นส่วนจำกัด","แอม แฮปปี้เนส"),"หจก. — ต้องตัด"),
 ("บริษัท ก จำกัด",     ("บริษัท","ก จำกัด"),   "บริษัท — ต้องตัด"),
]
for label,new in (("ก่อนแก้",False),("หลังแก้",True)):
    print(f"=== {label} ===")
    bad=0
    for name,exp,why in CASES:
        got=split(name,new)
        ok = got==exp
        if not ok: bad+=1
        print(("  OK " if ok else "  ** ")+f"{name!r} -> {got!r}  (คาด {exp!r}) {why}")
    print(f"  ผิด {bad}/{len(CASES)}")
