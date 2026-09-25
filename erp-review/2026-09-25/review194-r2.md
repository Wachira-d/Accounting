# รอบ 194 — ฝ่ายค้านรอบสอง (ตรวจการแก้ M1–M6/C2/P1 · C1/C3/C4/P4/P6) · อ่านอย่างเดียว

ขอบเขต: `git diff 5d82ede9..HEAD -- Accounting/ Accounting.Tests/ tools/` (HEAD `2064fdc3`) · ไม่ได้รันโค้ด (ไม่มี SDK) — ทุกข้อเปิดไฟล์ตรงบรรทัด

## CONFIRMED (เรียงความรุนแรง)

✅ `<pending>` **R2-1 (สูง · M4 ทิศใหม่) "ไม่มีแถว TaxReport" ถูกนับว่า "งวดยังไม่ยื่น" ⇒ VAT ของการริบหายจากทุกแบบที่ยื่น**
- `DocumentService.cs:3864-3868` `VatPeriodDeclaredOrFiledAsync` = มีแถว TaxReport สถานะ Filed/Submitted หรือ FilingLockedAt เท่านั้น · `DepositPolicyResolver.ForfeitTaxPointDecision` (:476-479) ⇒ ไม่ยื่น = tax point วันรับเงิน + หมายเหตุ "ไม่ต้องยื่นเพิ่มเติม" · ไม่ดูกำหนดยื่น
- สถานการณ์: บริษัทยื่น ภ.พ.30 ทาง RD โดยไม่บันทึกในระบบ · มัดจำเต็มยอด 10,700 รับ 10/01/2026 · ริบ 20/08/2026 ⇒ ใบกำกับลงวันที่ 20/08 แต่ `PaymentDate`=10/01 (:3967) ⇒ `TaxPointDate`=10/01 (`TaxPointResolver` :6021) ⇒ รายงาน ส.ค. คัดออก (TaxService.cs:171 เลือกด้วย TaxPointDate) · ม.ค. ยื่นไปแล้วนอกระบบ ⇒ VAT 700 ไม่อยู่ในแบบใดเลย และหมายเหตุบอกไม่ต้องยื่นเพิ่ม (ขัด DOCTRINE §1 "เท็จเพราะไม่มีข้อมูล ห้ามตกเป็นผ่าน") · เส้น VAT พักก็เหมือนกัน (:3823 ประทับ RecognizedAt=ม.ค. แต่ JE Cr 21911 ลง ส.ค. ⇒ GL↔ภ.พ.30 คนละเดือน · ใบลงวันที่ ส.ค. ไปอยู่รายงาน ม.ค. = ผิดลำดับ §87)

✅ `<pending>` **R2-2 (สูง · M2 เกินขอบเขต) ใบเต็มยอดเดิม (ก่อน 24/09) ถูกตีเป็น "VAT 0 โดยชอบ" ⇒ ริบแล้วรายได้ไม่มี VAT เงียบ ๆ**
- `ShapeFor(FullDeposit)` ⇒ deferred=true มีตั้งแต่ `5721ebd5` (2026-09-24) เท่านั้น · ใบเก่า: คอลัมน์ default false (Migration :1579) · ฟอร์มเดิมส่ง `fDepositVat=immediate` ค่าเริ่มต้น ⇒ มัดจำเต็มยอดเดิมทุกใบ = VAT 0 + deferred=false + Nature NULL
- `DepositPolicyResolver.cs:441` `depositOutputVatDeferred == false` ⇒ `ZeroVatAtIssue` โดยไม่ดู Nature · เทสต์ล็อกไว้ (`DepositForfeitRound194MTests.cs:29,51`) ⇒ ย้อน spec S3 สำหรับข้อมูลเดิม และหน้าจอบอก "VAT 0 โดยชอบ" ซึ่งเท็จ · แก้: ZeroVatAtIssue เฉพาะ Nature != null (ใบใหม่ตรึงลักษณะเสมอ)

✅ `<pending>` **R2-3 (กลาง-สูง · M5) "ส่งมอบแล้ว" (ค่าเริ่มต้นหน้าเว็บ) กับเงินประกันเต็มยอด = รายได้ขาย 41000 ไม่มี VAT**
- `PlainRealizeProblem` :502 ปล่อย RefundableSecurity · หน้า deposit-center/deposits ตั้ง `realizeMode=normal` ⇒ `forfeitAs=null`
- เงินประกัน 5,000 → Dr 21530 5,000 / Cr 41000 5,000 · เดิม (null ⇒ PriceOrFee) = ใบกำกับ VAT 327.10 · เงินประกันที่หักเป็นค่าของ/บริการ = ถึงกำหนดวันหัก (legal-L1 2b)

✅ `<pending>` **R2-4 (กลาง · C2) ตัดชำระหลายใบแล้ว void ใบมัดจำ ⇒ ลูกหนี้ subledger ≠ GL**
- 7b (`:8255-8279`) ลบ `appliedGross` = Cr 113 ของ JV **ทุกใบ** ออกจากใบที่ตัวชี้ชี้ใบเดียว · มัดจำ 10,000: ริบ 3,000→F · ตัดชำระ X 7,000 (ผ่านด่าน :4726 เพราะ prior=F) · void มัดจำ ⇒ X.Paid=max(0,7,000−10,000)=0 · F ยัง Paid 3,000 ขณะ GL เปิดลูกหนี้ F 3,000 · ก่อน C2 เป็นไปไม่ได้ (one-shot)

✅ `<pending>` **R2-5 (กลาง · C2 regression) purge ใบที่เคย void ⇒ ลบ JV ต้นฉบับทิ้ง ทิ้งตัวกลับกำพร้า + หัก Realized ซ้ำ**
- `DepositsAppliedToAsync` (:3841) หาจาก JE ที่ `Reference`=เลขใบ ไม่กรอง `ReversedByEntryId` · purge (:8850) ก็ไม่กรอง · void X แล้ว purge X (override retention): พบ JV เดิม → DELETE เฉพาะต้นฉบับ (ตัวกลับ OriginalEntryId≠null คงอยู่: Dr 113/Cr 217 ลอย) + `DepositRealizedAmount −= 3,000` ครั้งที่สอง · เดิมตัวชี้ถูกล้างตอน void ⇒ ไม่เจอ

✅ `<pending>` **R2-6 (กลาง · C2 ครึ่งทาง) เส้น "หักมัดจำเป็นฐาน" (drives) ยัง one-shot**
- `:15445-15452` และ `:15527-15533` ไม่ใช้ `ApplyToAnotherTargetAllowed` ⇒ ริบบางส่วน (ตัวชี้=F) แล้วออกใบกำกับสุดท้ายหักมัดจำที่เหลือ ⇒ "ถูกนำไปหักกับ F แล้ว" · ทางไปต่อในข้อความ = ยกเลิก F (F2 ข้อ 1/8)

## PLAUSIBLE
- ✅ `<pending>` P-a ปิดไม่ครบ: ล็อก `DepositRealize` มีแค่ปุ่ม · เส้นอนุมัติ (`LoadTaxedDepositsByRefAsync` FOR UPDATE) ไม่ถือคีย์เดียวกัน และ `RealizeDepositAsync` ไม่ล็อกแถว ⇒ อ่านยอดเก่าแล้วเขียนทับ (lost update)
- ✅ `<pending>` P1 ค้าง: รับเงินประกันที่พัก (Lifecycle ~1256) และ `UpdateDocumentAsync` จัดรูปซ้ำ (:2269) ไม่ส่ง `depositChannelVatRate` · หน้าศูนย์มัดจำส่ง channel=null ⇒ ใบที่พัก ChargeVat=false ที่ออก 24–25/09 ริบแบบราคาได้ใบกำกับ 7%
- ✅ `<pending>` `RevertTrackedChangesSinceAsync` คืน collection ได้ แต่ reference navigation ของ entity เดิมที่ชี้ entity ใหม่ไม่ถูกตัด ⇒ `SaveChanges` ใน FailLoud อาจดึงกลับเป็น Added (ไม่พบจุดเกิดจริง)
- ✅ `<pending>` JobLock: ถ้า connection พังระหว่างงาน `pg_advisory_unlock` ใน finally โยนทับ error เดิม

## NOT-A-BUG (ตรวจแล้ว)
- M1(ก): baseline หลัง `SaveChanges` (:463) ⇒ การแก้ของผู้เรียก (สถานะการจอง) ถูก**บันทึกก่อน** ไม่ถูกทิ้ง · `r` ยังถูกติดตาม หมายเหตุลงจริง · collection ทุกตัวเป็น `List` (`IList.RemoveAt` ปลอดภัย) · ไม่มี owned/keyless type
- JobLock: `pg_try_advisory_lock` ไม่รอ ⇒ ไม่ deadlock กับล็อก `LodgingRefundPaid` (ซ้อนบน connection เดียว = คนละคีย์) · ปลดใน finally · ไม่มีโค้ดปิด connection กลางทาง
- กุญแจ idempotent: `DepositPolicyNote` เขียนเฉพาะเส้นมัดจำ (:2492/2499 ต้องมีประเภท) · clone ไม่พา · ลบใบร่าง = สร้างใหม่ถูกต้อง
- ตัดชำระเกิน: ด่านฐานคงเหลือ + ยอดค้างใบ (:4703-4712) ยังทำงาน · void F คืนยอดมัดจำถูก (ธงเก่าบนใบมัดจำค้าง แต่ไม่มีผู้อ่าน `LateVatMarker`)
- ผู้เรียก RealizeDeposit: CMS `DecideOnComplete` สอดคล้อง `PlainRealizeProblem` · ที่พักเช็คเอาต์ส่ง FinalInvoiceId · ยกเลิกส่ง PriceOrFee · เงินประกันส่ง Compensation
- C1 `priceChannel`: ผู้เรียก `ResolveKind` มี 3 · ชั้นค่าเริ่มต้นถูกกรองเสมอ · migration แตะแค่ DepositKinds · idempotent
- P6: แต่ละคำสั่ง autocommit แยก · "does not exist" = benign ⇒ DB ใหม่ไม่ล้ม · ชุดหลังรันซ้ำหลังสร้างตารางที่พัก
- M3/M6: สูตร 1,070/70 → 35 ถูก · M6 expression ตัดใบริบครบ

## สถานะการแก้ (ทีม M2 · รอบ 194)
ทุกข้อ CONFIRMED + PLAUSIBLE ข้างบนยืนยันเองแล้ว (เปิดไฟล์ตรงบรรทัด) และแก้ในคอมมิตเดียว — รายละเอียด DOCUMENT_FLOW "รอบ 194 ทีม M2" · เทสต์ `DepositRound194R2Tests`
- ส่วนเพิ่มที่พบระหว่างแก้ (R2-4): ขั้น 2 ของ `VoidDocumentAsync` ส่ง JE ที่ถูกกลับไปแล้วไปกลับซ้ำ ⇒ `ReverseJournalEntryAsync` โยน ⇒ ยกเลิกใบมัดจำที่เคยตัดชำระ
  ใบที่ถูกยกเลิกไปแล้วไม่ได้เลย — กรอง `ReversedByEntryId == null` แล้ว (`DepositApplyJournals.LiveOfSource`)
- P-a: เลือก "advisory key เดียว" (ไม่ใช่ FOR UPDATE ทั้งหมด) เพราะเส้นริบเปิดธุรกรรมของตัวเองหลายขั้น — เส้นในธุรกรรมใช้ `pg_try_advisory_xact_lock` (ไม่รอ)
  ก่อนล็อกแถว · ครอบ `RefundDepositAsync` (แตะ `DepositRefundedAmount`) ด้วย — ทุกเส้นที่อ่าน-แล้ว-เขียนยอดคงค้างของใบมัดจำถือคีย์เดียวกัน
