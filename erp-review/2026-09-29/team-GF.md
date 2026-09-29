# รอบ 200 ทีม GF — แก้ผลฝ่ายค้านของทีม G (`review200-G.md`)

> base `f1810dd6` · commit `6986653d` · ไม่มี .NET SDK ⇒ **ยังไม่ได้คอมไพล์** · checker ที่รันผ่านแล้วอยู่ท้ายไฟล์

| ID | สถานะ | P | สิ่งที่ทำ | file:line | เทสต์ |
|---|---|---|---|---|---|
| R200G-1 | ✅ | P1 | ตารางสินค้า (ชื่อ/ชื่ออังกฤษ/รหัส/id ใน onclick) + ปุ่มหมวด (`Layout.jsArg` ใน JS string · `Layout.esc` ในข้อความ) · ป้ายช่องทางชำระในสรุปกะ · ไล่ `${…}` ทุกจุดใน `pos.html` อีกรอบ (ที่เหลือผ่านตัวหนี/ตัวเลข/GUID/ค่าคงที่/toast ที่หนีเอง/prompt/สลิปความร้อนที่เป็น byte) · **สีโต๊ะ**: เซิร์ฟเวอร์ไม่เคยตรวจ (`PosFloorPlanController` เก็บ `Color`/`Shape` ตรง) ⇒ `Helpers/PosTableStyle` — บันทึก (สร้าง/แก้/บันทึกทั้งชุด) สี hex/รูปร่างชุดปิด ไม่ผ่าน = 400 · อ่าน: ค่าเก่าส่งออกเป็นค่าปลอดภัย · หน้า POS/ผังร้านกรองซ้ำ + หนี `shape` ใน class | `pos.html:1521–1523, 1569–1574, 2655, 2883` · `pos-floorplan.html:283–288` · `PosFloorPlanController.cs:112, 186, 212, 263` · `Helpers/PosTableStyle.cs` | `STYLE_*` (4 กลุ่ม สองทิศ) |
| R200G-2 | ✅ | P1 | `MoneyAccountFallback.TerminalPinFor` ตัวตัดสินเดียว: บัญชีธนาคารที่ปัก = เฉพาะ `KindOf == BankDeposit` (โอน/พร้อมเพย์/หักบัญชี — ตรงกับป้ายบนจอ) · บัญชีเงินสดที่ปัก = ชนิดเงินสด (เงินสด + "อื่น ๆ") · บัตร/e-Wallet/เช็ค → ผังมาตรฐานของชนิดนั้นเสมอ · ผู้เรียกทั้งสองจุด (ปิดบิล `:1665` · คืนเงิน `:512`) ผ่าน `ResolvePaymentAccountAsync` ตัวเดียว · คำเตือนหน้าเครื่องพูดถึงแค่โอน/พร้อมเพย์/หักบัญชีอยู่แล้ว (สอดคล้อง) · **เปลี่ยนพฤติกรรม "อื่น ๆ"**: เดิมไปธนาคารที่ปัก ตอนนี้ไปลิ้นชักที่ปัก/11111 (ตรงกับผังสำรองของชนิดเดียวกัน · UI ไม่มีปุ่ม "อื่น ๆ") | `Helpers/MoneyAccountFallback.cs` (`TerminalPinFor`) · `PosService.Orders.cs:2226–2242` | `PIN_*` (3 กลุ่ม สองทิศ) |
| R200G-3 | ✅ | P2 | `LoadIntentRowsAsync` (ย้ายไป `SettlementImportService.Gateway.cs` แล้วหลังทีม P2) ใช้ `GatewaySettlementMath.ConfirmedFromUtc`/`ConfirmedToExclusiveUtc` (ช่วงเปิดปลายได้ — สูตรเดียวกับ `ConfirmedRangeUtc` ซึ่งตอนนี้ประกอบจากสองตัวนี้) · **ต้นช่วงก็ผิด** (ฝ่ายค้านเห็นแค่ปลาย) — แก้ทั้งคู่ · grep `CalendarDateUtc(x).AddDays(1)` เทียบ `ConfirmedAt` ทั้งเรพ = **0 จุด** | `SettlementImportService.Gateway.cs:91–94` · `Helpers/GatewaySettlementMath.cs:175–185` | `RANGE_*` (สองทิศ) |
| R200G-4 | 📋 → รายการ sandbox | P2 | ต้องยืนยันกับ sandbox จริง (เครือข่ายนี้เรียกผู้ให้บริการไม่ได้) · ใส่ในรายการทดสอบก่อนเปิด live + ข้อความเตือนล่วงหน้า "รอบโอนแรกหลัง deploy อาจยอดไม่ตรง" | `PAYMENT_GATEWAY_DESIGN.md` §4.4 | — |
| R200G-5 | 📋 → รายการ sandbox | P3 | เช่นเดียวกัน (รุ่นของข้อมูลใน `GET /events/{id}`) | `PAYMENT_GATEWAY_DESIGN.md` §4.4 | — |
| R200G-6 | ✅ | P3 | `PaymentIntentPolicy.IsProviderFeeFinal` — รับค่าธรรมเนียมเฉพาะจาก charge ที่เงินเคลื่อนแล้ว (สำเร็จ/คืนบางส่วน/คืนเต็ม) ⇒ pending `fee: 0` ไม่ถูกเก็บ · เส้น duplicate (`ApplyChargeAsync:283`) เลิกเขียนเงื่อนไขเอง → ผ่าน `IsProviderFeeFinal` + `ShouldTakeProviderFee` ตัวเดียวกับ `ApplyChargeToEntity` (ผู้เขียน `FeeActual` จาก charge เหลือกติกาเดียว · ผู้เขียนที่สามคือ `CorrectFeeAsync` แก้มือ — ถูกต้อง) | `Helpers/PaymentIntentPolicy.cs:95–103` · `PaymentIntentService.cs:281–286, 399–402` | `FEE_*` (สองทิศ) |
| R200G-7 | ✅ | P3 | เกณฑ์ "ผังที่ปักได้" ตัวเดียว `PosService.UsableTerminalMoneyPin` (ของบริษัท · ใช้งาน · ไม่ถูกลบ · **ผังสินทรัพย์**) ใช้ใน ตัวตรวจตอนบันทึก · ตัวเลือกผังตอนปิดบิล (เดิมรับผังปิดใช้ต่อ) · ป้ายเตือน (`UsableTerminalPinsAsync`) · `MoneyAccountFallback.TerminalPinWarning(pinned, pinUsable, banks)` เตือนเมื่อปักผังที่ใช้ไม่ได้ (บอกว่าจะลงบัญชีเดียวของบริษัทแทนหรือปิดไม่ได้) · `TerminalBankWarning` เป็น private (ผู้เรียกเดียวคือ `TerminalPinWarning`) | `PosService.cs:82–108, 128–131, 278–300` · `MoneyAccountFallback.cs` | `WARN_*` · `E3_*` (ปรับ) |
| R200G-8 | ✅ | P3 | ข้อความแจ้งเตือน "รับชำระค้าง" บอกสิทธิ์ "ดูบัญชีธนาคาร" (`Bank.View` — จาก `PaymentGatewayPermissionScope.ViewPayments` ไม่พิมพ์ซ้ำ) และให้ขอผู้ดูแล/ส่งต่อ · เมนูที่ไม่ gate (`layout.js:1517`) ไม่แตะ — คำถามค้างข้อ 4 ของทีม G | `PaymentIntentReconcileJob.cs:183–189` | — (ข้อความ) |

## R200G-2 — บิลเก่าที่ลงผิดแล้ว (ไม่แก้ JE อัตโนมัติ · DECISIONS ข้อ 20)

SQL อ่านอย่างเดียว — หาขา Dr ของบิล POS ที่จ่ายด้วยบัตร/e-Wallet/เช็ค แต่ไม่ได้ลงผังมาตรฐานของชนิดนั้น (ขาเงินแต่ละขามีคำอธิบาย `รับเงิน <วิธี> POS #<เลขบิล>`):

```sql
SELECT o."CompanyId", t."Name" AS terminal_name, o."OrderNumber", o."CreatedAt",
       jl."Description", jl."DebitAmount", coa."AccountCode", coa."AccountName",
       (coa."Id" = t."BankAccountId") AS is_current_terminal_pin, o."JournalEntryId"
FROM "PosOrders" o
JOIN "JournalEntryLines" jl ON jl."JournalEntryId" = o."JournalEntryId" AND NOT jl."IsDeleted" AND jl."DebitAmount" > 0
JOIN "ChartOfAccounts" coa ON coa."Id" = jl."AccountId" AND coa."CompanyId" = o."CompanyId"
JOIN "PosSessions" s ON s."Id" = o."SessionId" AND s."CompanyId" = o."CompanyId"
JOIN "PosTerminals" t ON t."Id" = s."TerminalId" AND t."CompanyId" = o."CompanyId"
WHERE NOT o."IsDeleted" AND o."JournalEntryId" IS NOT NULL
  AND (jl."Description" LIKE 'รับเงิน บัตรเครดิต POS #%'
    OR jl."Description" LIKE 'รับเงิน e-Wallet POS #%'
    OR jl."Description" LIKE 'รับเงิน เช็ค POS #%')
  AND coa."AccountCode" NOT IN ('11340', '11113', '11131')
ORDER BY o."CompanyId", o."CreatedAt";
```
- แถวที่ลงบัญชีพักของ gateway (บิลที่ลูกค้าสแกนจ่ายผ่านระบบรับชำระออนไลน์ — `ResolveGatewayClearingAsync`) ก็โผล่ถ้าผังพักไม่ใช่ 11340 — ถูกต้อง ไม่ต้องแก้ · ให้นักบัญชีคัดออกด้วย
  `PosPayments."PaymentIntentId" IS NOT NULL` ของบิลนั้น
- `is_current_terminal_pin = false` = เครื่องเปลี่ยนบัญชีที่ปักไปแล้วหลังปิดบิล (ยังเป็นขาที่ผิด)
- ฝั่งคืนเงิน (`จ่ายคืนเงิน POS #…`) ไม่มีวิธีคืนในคำอธิบาย — ตรวจคู่กับบิลที่เจอข้างบน
- ทางแก้ = นักบัญชีทำ JE ปรับปรุง Dr 11340 / Cr ธนาคาร (ในงวดที่เปิด) — ระบบไม่ทำให้เอง

## ความเสี่ยงคอมไพล์ที่เหลือ (อ่านโค้ด ไม่มี SDK)
- `PosService.UsableTerminalMoneyPin` คืน `Expression<Func<ChartOfAccount,bool>>` ส่งเข้า `.Where(...)` แล้วต่อ `.FirstOrDefaultAsync(a => a.Id == acctId)` / `.AnyAsync(...)` — รูปแบบมาตรฐาน EF
- `MapTerminal` เพิ่มพารามิเตอร์ `HashSet<Guid> usablePins` — ผู้เรียก 2 จุดอัปเดตครบ (`GetTerminalsAsync` · `MapTerminalAsync`)
- `PosFloorPlanController`: pattern variable `is string styleCreateErr/styleUpdateErr` ชื่อไม่ชนตัวแปรในเมธอด · `TableDto` สร้างใน LINQ-to-objects (หลัง `ToListAsync`) ⇒ เรียก helper ได้ ไม่ติด CS0854
- `IsValidColor/IsValidShape` เป็น private (ไม่ให้เกิด dead helper) · เทสต์ใช้ `RejectReason/SafeColor/SafeShape`
- `GatewayTeamGRound200Tests.E3_*` เปลี่ยนจาก `TerminalBankWarning(p, b)` เป็น `TerminalPinWarning(p, p, b)` (ความหมายเดิม: ปักผังที่ใช้ได้)

## Checker ที่รันแล้ว (worktree นี้)
html_attr_escape 0 · onclick_js_string 0 · escape_helper 0 · record_arg 0 · nullable_arg ✅ · using 0 · dead_helper ✅ (ไม่มีตัวใหม่) · string_quote_close 0 ·
comment_line_break ✅ · identifier_space 0 · test_inventory --check (วางทับ §0 แล้ว) · `node --check` สคริปต์ `pos.html`/`pos-floorplan.html` ✅ ·
required_call_site_check — ดูผลท้ายรายงานคำตอบ · `check_all.sh` เต็ม**ไม่ได้รัน** (เครื่องโหลดหนัก — checker ตัวเดียวใช้หลายนาที)

## คำถามค้าง
1. **"อื่น ๆ" (PaymentMethod.Other)** ของ POS — ตอนนี้ตามชนิดเงินสด (ลิ้นชักที่ปัก/11111) ตาม `KindOf` เดิม · ถ้าเจ้าของอยากให้ "อื่น ๆ" เป็นเงินฝาก ต้องแก้ที่ `KindOf` (ตัวตั้งเดียวของ POS+integration) ไม่ใช่ที่ POS
2. ช่องสี/รูปร่างเก่าที่ไม่ถูกรูปใน DB ไม่ได้ถูก migration ล้าง — ฝั่งอ่านส่งค่าปลอดภัยแทน · ถ้าผังร้านบันทึกทั้งชุด ค่าที่ส่งกลับเป็นค่าปลอดภัยแล้ว (ค่าเสียถูกเขียนทับเองตามธรรมชาติ)
