# รอบ 194 — ฝ่ายค้านรอบสาม (ตรวจ `f6a09e23` ทีม M2 · R2-1–R2-6 + P-a) · อ่านอย่างเดียว

ไม่ได้รันโค้ด (ไม่มี SDK) — ทุกข้อเปิดไฟล์ตรงบรรทัด (HEAD `15c5dc68`)

## CONFIRMED

**R3-1 (สูง-กลาง) R2-1 ปิดครึ่งเดียว — เส้น "ออกใบกำกับของยอดที่ริบ" ยังให้ GL 21911 กับ ภ.พ.30 คนละเดือน**
- แก้แยก JE เฉพาะเส้นย้าย VAT พัก (`DocumentService.cs:3876-3903`) · เส้น `IssueForfeitTaxInvoiceAsync`: `DocumentDate: when` (:4036) + `PaymentDate` = วันรับเงิน (:4049) ⇒ `TaxPointDate` = วันรับเงิน (:6119 · `TaxPointResolver.cs:75`) ⇒ ภ.พ.30 เลือกตาม TaxPointDate (`TaxService.cs:176`) แต่ JE ของใบ `EntryDate = doc.DocumentDate` (:16350)
- ตัวเลข: มัดจำเต็มยอด 10,700 รับ 20/01/2026 · ริบ 05/02 (ก่อนกำหนดกระดาษ 16/02) ⇒ Dr 113 10,700 / Cr 41000 10,000 / Cr 21911 700 ลง **ก.พ.** · ภ.พ.30 **ม.ค.** +700 ⇒ กระทบยอดผิด 700 ทั้งสองเดือน · ใบลงวันที่ 05/02 เลขชุด ก.พ. อยู่ในรายงานภาษีขาย ม.ค. (§87 — R2-1 เดิมยกไว้ ยังไม่แก้)

**R3-2 (กลาง) P-a "ทุกเส้นถือคีย์เดียวกัน" (ข้อ 8 ในคอมมิต) ไม่จริง — เส้นที่อ่าน-เขียนสถานะใบมัดจำโดยไม่ถือ `DepositRealizeKey`**
- (ก) `RecognizeDepositOutputVatAsync` (:3353-3420) มี tx แต่ไม่มีคีย์/FOR UPDATE · ชนกับริบแบบ ReclassifyUndueToDue (ปุ่มไม่ล็อกแถว): ทั้งคู่อ่าน `RecognizedAt=null` + พักค้าง 700 ⇒ Dr 21913 700 สองครั้ง ⇒ 21913 −700 · 21911 1,400 · ภ.พ.30 รายงาน 700 (ถูกเพดาน `ReportedRecognizedDepositVat`)
- (ข) เส้น drives หลายใบ/ใบเดียว (:15546-15611 · :15630) มีแค่ FOR UPDATE — R2-6 เพิ่งเปิดให้เส้นนี้ทำงานกับใบที่ริบไปบางส่วนแล้ว ⇒ lost update กับปุ่มริบ
- (ค) void ใบปลายทาง ขั้น 2b (:8093-8135) เขียน `dep.DepositRealizedAmount` โดยไม่ล็อกใบมัดจำ
- (ง) `PostCashSaleJournalAsync` (:16571) อยู่นอก tx ⇒ `LoadTaxedDepositsByRefAsync` ข้ามล็อก (:16421-16426) — โค้ดเขียนไว้แล้ว แต่ในคอมมิตเขียนว่า "ทุกเส้น"

## PLAUSIBLE
- **P-1 ล้มเพราะ "รอสักครู่" ตอนอนุมัติ**: ฝั่ง DB rollback สะอาด (tx :5855/:6294-6298 · เลขเอกสารใช้ max+xact lock ⇒ ไม่มีเลขขาด) แต่ entity ใน context ยังถูกแก้ค้าง (Status/เลข) ⇒ ผู้เรียกที่ catch แล้ว `SaveChanges` ต่อ (`CmsBookingService.cs:614-623`) จะได้ใบ "Approved ไม่มี JE" (defect class นี้มีอยู่ก่อน แต่ครั้งนี้เพิ่มสาเหตุชั่วคราวใหม่) · เช็คเอาต์ที่พักล้มก่อนประทับ `FinalDocumentId` (`Lifecycle.cs:737`) ⇒ กดซ้ำแล้วใบร่างเดิมค้างอยู่
- **P-2 บอกว่าไม่ว่างทั้งที่ว่าง**: ล็อกทุก id ที่เลขอ้างอิงตรง**ก่อน** `ResolveDeductedDeposits` ⇒ เลขอ้างอิงเดียวกันของมัดจำค่าห้องกับเงินประกัน ถ้ากำลังคืนเงินประกันอยู่ การอนุมัติเช็คเอาต์จะล้ม (ผู้ใช้เห็น และกดใหม่ได้)
- **P-3 `TrackedChangeRevert`**: reference ฝั่ง dependent (FK อยู่บน entity เดิมแต่ชี้ entity ใหม่) ไม่ถูกตัด — พึ่ง reload + snapshot ส่วน reload ไม่มีเทสต์ · ไม่พบจุดเกิดจริงในเส้นริบ (ผูกกันด้วย FK scalar) · `AutoDetectChangesEnabled` คืนค่าใน finally ✓ · baseline ถ่ายหลัง `SaveChanges` ⇒ การแก้ของผู้เรียกไม่หาย ✓
- **P-4**: `UnrealizeDrivesDepositAsync` เทียบ `DocumentNumber == DepositAppliedRef` (:8685-8690) ⇒ เลขอ้างอิงหลายใบไม่เคยตรง ⇒ void แล้วยอดคงเหลือในบัญชีย่อยมัดจำไม่คืน (บั๊กมีอยู่ก่อน แต่ R2-6 เปิดเส้นนี้ให้ใบที่ริบบางส่วน)
- **P-5 (กฎหมาย)**: หมายเหตุ tax point ไม่ได้บอกว่าใบกำกับออกช้ากว่าจุดความรับผิดตาม §86 · ใบรับเงิน ธ.ค. ที่ริบใน ม.ค. ก่อนวันที่ 15 ถูกย้ายเข้างวดปัจจุบันพร้อมธง §89/1 ทั้งที่ยังยื่นงวด ธ.ค. ทัน (`crossYear`) — เป็นทิศปลอดภัย แต่เกินจำเป็น

## NOT-A-BUG (ตรวจแล้ว)
- `TryXactLockAsync` โยนเมื่อไม่มีธุรกรรม (`JobLock.cs:113-114`) · ผู้เรียกทุกตัวเปิด tx ก่อนเรียก (คืน :4554→4556 · ตัดชำระ :4713→4716 · อนุมัติ :16425) · สูตรคีย์ = `For(companyId, DepositRealize, id.ToString())` ตัวเดียวกับ `RunExclusiveAsync` (FNV คงที่) · `advisory_lock_key_check` = 0
- session lock กับ xact lock ถือซ้ำได้ใน session เดียวกัน (RunExclusive เปิด connection เอง และ EF ใช้ connection นั้นต่อ) · แบบ try ไม่รอ ⇒ ไม่เกิด deadlock
- `SqlQueryRaw<bool>(... AS "Value").SingleAsync` ใช้ได้กับ EF Core 8.0.11 · เทสต์สร้าง context จาก options อย่างเดียว ไม่แตะ DB
- `OriginallyNoVat`: รับเป็นชื่อ enum (`Program.cs:805`) · หน้าเว็บส่ง `nameof` · ถ้าถูกเพิกเฉย ⇒ บันทึกหมายเหตุ `RequestIgnored` ก่อนออกใบ
- `PlainRealizeOffered` = `!IsDeposit || nature != Security` · หน้าเว็บเช็ค `!== false`
- R2-4 `GrossByTarget`: ใบปลายทางที่ void ไปก่อนแล้ว ⇒ JV ถูกกลับไปแล้วจึงไม่ถูกนับ · ใบที่สถานะ Voided ข้าม · `DocumentNumber` มี unique index (`AccountingDbContext.cs:843`) · R2-5 ปิดจริง
- เลข JE ของ JE ที่แยกออกมา: JE หลักถูก Add ก่อน และ `NextJournalNumberAsync` นับรายการที่ค้างใน context ด้วย (`JournalEntryBuilder.cs:157`)
- `JobLock` finally ไม่ทับ error เดิมของงานแล้ว
- **ข้อค้นพบเพิ่มของ M2 "ขั้น 2 กลับ JE ซ้ำ" ไม่จริง**: ตัวกรองเดิม `Status == Posted` ตัดใบที่ถูกกลับไปแล้วอยู่แล้ว (`AccountingService.cs:1079` ตั้งสถานะเป็น Reversed) — ตัวกรองใหม่ไม่เสียหายแต่ซ้ำซ้อน
