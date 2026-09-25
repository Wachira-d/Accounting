# รอบ 194 — ฝ่ายค้าน (เงิน/ภาษี) · อ่านอย่างเดียว

ขอบเขต: `git diff ce29e0f5..HEAD -- Accounting/ Accounting.Tests/` (branch `claude/erp-system-review-team-660mev`, HEAD `ed4426e9`)
Checker ที่รัน (ผ่าน 0 จุด): record_arg · nullable_arg · undeclared_local · arg_type · using · service_interface — **ยังไม่ได้คอมไพล์จริง**

## CONFIRMED

### M1 (P1) — ใบกำกับอัตโนมัติตอนริบล้ม ⇒ หมายเหตุบนการจองที่พักหายเงียบ + คำแนะนำขัดกันจนได้ใบกำกับซ้ำ
- `DocumentService.cs:3847-3856` `FailLoudAsync` เรียก `_db.ChangeTracker.Clear()` (DbContext scoped เดียวกับ LodgingService)
- `LodgingService.Lifecycle.cs:1017-1024` catch ของ `CancelCoreAsync` เขียน `AppendInternal(r, …)` ลง `r` ที่ **ถูก detach แล้ว** ⇒ `SaveChangesAsync` บันทึกแค่ audit ·
  หมายเหตุ "ยกเลิกแล้ว แต่ลงรายได้ส่วนที่ริบไม่สำเร็จ" บนการจอง **ไม่ถูกบันทึก** (ล้มดังเหลือ 2/3 ที่) · เส้นแขก (`:200-208`) กลืน error ต่ออีกชั้น
- ข้อความที่ lodging ต่อท้าย ("ต้องรับรู้ที่หน้า “เงินมัดจำ”") ขัดกับข้อความของ DocumentService (`:3838-3841` "ห้ามกดรับรู้/ริบซ้ำ … จะได้ใบกำกับสองใบ")
- `IssueForfeitTaxInvoiceAsync` ไม่มีด่าน "มีใบกำกับของมัดจำนี้ค้างอยู่แล้วไหม" (มีแค่ `DepositAppliedToDocumentId`) ⇒ อนุมัติไม่ผ่านรอบแรก (ด่านวงเงิน `RequireApprovalForDocuments` :5528 · ApprovalRule :5630 · §86/4 ผู้ซื้อนิติบุคคล :5502) แล้วกดรับรู้ตามคำแนะนำ = ใบร่างใบที่สอง; ถ้ารอบสองอนุมัติผ่าน ใบร่างแรกยังอนุมัติได้ภายหลัง = ใบกำกับออกสองใบต่อเงินก้อนเดียว (ใบหลังไม่มีคนจ่าย มี VAT)

### M2 (P1 · ทิศตรงข้าม) — ใบเดิมที่ VAT 0 โดยชอบ (ยกเว้น §81 / อัตรา 0% §80/1 / ออกตอนยังไม่จด) ริบแล้วได้ใบกำกับ 7%
- `DepositPolicyResolver.cs:639-672`: `hasVat=false` + ลักษณะ NULL ⇒ `IssueTaxInvoiceForForfeit` ที่ `companyVatRate` โดยไม่ดู `DepositOutputVatDeferred`
  (ใบ VAT 0 + deferred=false ไม่ใช่ "มัดจำเต็มยอด" — `ShapeFor` :266 ให้ FullDeposit เป็น deferred=true เสมอ)
- ตัวอย่าง: มัดจำบริการส่งออก 100,000 บรรทัด 0% ไม่ deferred → ริบทั้งก้อน ⇒ ใบกำกับฐาน 93,457.94 VAT 6,542.06 + ธง `[DEPOSIT-LATE-VAT]` สั่งยื่นเพิ่มเติม · ก่อนรอบนี้ = รายได้ไม่มี VAT (ถูก)
- หน้าศูนย์มัดจำ (`DepositKindDocumentRules.ForfeitOptions`) เสนอแค่ "มี VAT (ค่าเริ่มต้น)" กับ "ค่าเสียหาย" — ไม่มีทางเลือกที่ถูก (ราคา แต่อัตราเดิม 0) ⇒ ผู้ใช้ต้องเลือก "ค่าเสียหาย" ซึ่งติดป้ายผิดความจริง
- ใบใหม่ที่ประเภท PartOfPrice×FullDeposit ก็เสียข้อมูลอัตราเดิมเพราะ `DepositDocumentShaping.Apply` ตั้งบรรทัดเป็น 0 ⇒ ใบกำกับตอนริบใช้อัตราบริษัทเสมอ (zero-rated ผิด)

### M3 (P2) — ริบเป็นค่าเสียหายบางส่วน แล้วรับรู้/ริบส่วนที่เหลือแบบราคา ⇒ ย้าย VAT พักเต็มใบซ้ำ
- `DocumentService.cs:3669-3690` กลับ 21913 เข้ารายได้ตามสัดส่วน (ไม่ประทับ RecognizedAt) แต่ `:3706` `vatMove = doc.VatAmount` เต็มใบในครั้งถัดไป
- ตัวอย่าง: มัดจำ VatPendingUndue 1,070 (VAT 70) → ค่าเสียหายฐาน 500 (Dr 21913 35 / Cr รายได้ 535) → ส่วนที่เหลือฐาน 500 แบบราคา ⇒ Dr 21913 **70** / Cr 21911 70 ⇒ 21913 ติดลบ 35 · ภ.พ.30 (TaxService.cs:620 `outputVat += doc.VatAmount`) เกิน 35
- เข้าถึงได้เมื่อใบเดิม (NULL) หรือเงินประกันที่ตั้งโหมดรอเรียกเก็บ (มีคำเตือนแต่อนุญาต)

### M4 (P2) — ใบกำกับที่ระบบออกตอนริบลงรายงานเดือนที่ริบ แต่ธงบอกให้ยื่นเพิ่มเติมเดือนรับเงิน
- `IssueForfeitTaxInvoiceAsync` (`:3818-3842`) ไม่ส่ง `PaymentDate` = วันรับมัดจำ ⇒ `TaxPointResolver` (:5855) ได้วันออกใบ ⇒ รายงานภาษีขายนับในเดือนริบ ·
  ธง `LateVatNote` บอก "ต้องยื่น ภ.พ.30 เพิ่มเติมของเดือนนั้น" ⇒ ผู้ใช้ทำตามทั้งสองทาง = VAT ซ้ำ (ไม่มีทางกันในระบบ)

### M5 (P3) — `NonVatNoVat` ถูกใช้กับ "รับรู้ตามปกติ" ด้วย ไม่ใช่แค่ริบ
- `:3641-3656` ประเภทนอกระบบ VAT ที่ตั้ง `ForfeitAccountCode` ⇒ รับรู้ค่าเช่าล่วงหน้าตามงวด (หรือ CMS "ให้บริการเสร็จ" `CmsBookingService` RealizeDepositOnCompleteAsync) ไปลงบัญชีริบ
  **ทับบัญชีที่ผู้ใช้ระบุ** ใน request (silent override) · คำอธิบาย JE เป็น "ริบมัดจำเป็นรายได้" ทุกครั้ง

### M6 (P3) — คำเตือน "21913 ค้างเกิน 90 วัน" ฟ้องใบที่ริบเป็นค่าเสียหายครบแล้วตลอดไป
- `TaxService.cs:1420-1428` กรองแค่ `RecognizedAt == null` + `VatAmount>0` ไม่ดู `DepositRealizedAt` ⇒ M3 ตั้งใจไม่ประทับ RecognizedAt ⇒ ฟ้องใบถูก

## PLAUSIBLE
- P-a กดริบพร้อมกันสองคำขอ: `RealizeDepositAsync` ไม่ล็อกแถวก่อนตัดสิน ⇒ ใบกำกับ 2 ใบอนุมัติแล้ว ใบที่สองตัดชำระไม่ได้ (ApplyDeposit one-shot) ⇒ ใบกำกับค้างลูกหนี้มี VAT
- P-b ริบบางส่วนผ่านใบกำกับ ⇒ มัดจำผูก `DepositAppliedToDocumentId` กับใบนั้น ⇒ นำส่วนที่เหลือไปตัดชำระใบสุดท้ายไม่ได้ (one-shot `:4557`) — ทางไปต่อคือยกเลิกใบกำกับที่เพิ่งออก
- P-c ริบเงินประกันเป็นค่าเสียหายที่ที่พัก → `CancellationFeeAccountCode ?? RoomRevenueAccountCode` (Lifecycle.cs:1399-1408 · seed ไม่ตั้ง ForfeitAccountCode) · เส้นทั่วไป → 41000 — รายได้ขายที่ไม่มีใน ภ.พ.30 ทำให้กระทบยอดรายได้ GL↔ภ.พ.30 ไม่ลง (spec S7 สั่งให้ใช้บัญชีเดิม — เจ้าของควรตัดสิน)
- P-d `[DEPOSIT-LATE-VAT]`/หมายเหตุถูก SaveChanges บนใบมัดจำ (`:3824`) ก่อน `CreateDocumentAsync` — สร้างใบล้ม (เช่น ContactId ว่าง) = ธงค้างทั้งที่ไม่มีการริบ
- P-e `GuardSecurityDepositDeductionAsync`/`DepositKindPayloadRejectionAsync` ไม่กรองสถานะ + จับด้วย `Reference` ⇒ เงินประกันที่ยกเลิกแล้ว/ที่ใช้เลขอ้างอิงร่วม (ที่พัก: ReservationNumber ทั้งสองใบ) อาจบล็อกผิด
- P-f `DepositKindCatalog.LoadContextAsync` ตรวจ 21530 ด้วย `IsActive` แต่ `DocumentService.ResolveDocumentDepositKindAsync` ใช้ `!IsDeleted` — บัญชีปิดใช้ถูกเลือกเป็นบัญชีเงินประกัน
- P-g มัดจำ NonVatSupply ถูก deferred=true ⇒ ไม่ขึ้นบรรทัด "ยอดขายที่ได้รับยกเว้น" ของ ภ.พ.30 เลย (กระทบ §82/6 เฉลี่ยภาษีซื้อ)

## NOT-A-BUG (ตรวจแล้ว)
- ใบเดิม/payload เดิมตอนสร้าง-แก้: `kindRequested=false` / `UpdateTarget(null,null)` ⇒ ไม่แตะบรรทัด/ธง/บัญชี · คำเตือนอนุมัติ `nature==null` ⇒ ไม่เตือน
- VAT ใบกำกับริบ: `ComputeLineAmounts` (:601-607) net = round(gross×100/(100+r), AwayFromZero), VAT = gross−net ⇒ 1,000 → 934.58/65.42 รวมพอดี ตัดชำระเต็มได้
- e-Tax hook: ผ่าน `ApproveDocumentAsync` → `_issuedHooks.RunAsync` หลัง commit · ack = `SystemWorkflow` (ไม่ประทับว่าคนรับทราบ)
- ผู้ซื้อ §86/4: `ContactId` จากใบมัดจำ ด่านผู้ซื้อนิติบุคคลยังทำงาน (ล้มดังผ่าน FailLoud)
- ค่าเสียหายกลับ 21913 เข้ารายได้: ทิศ Dr 21913 / Cr รายได้ ถูก · ไม่ประทับ RecognizedAt ⇒ ไม่เข้า ภ.พ.30 ถูก (ยกเว้น M3/M6)
- CMS C-1: ใบมัดจำอนุมัติแล้วไม่ void · ยังอยู่ศูนย์มัดจำ (คืน=CN / ริบ=KeepExistingVat สำหรับ VAT ทันที) · ล้มประทับบนการจอง + `ErpNotices`
- `RealizeTaxedDepositDeductionsAsync`/เช็คเอาต์ส่ง `FinalInvoiceId` ⇒ ไม่เข้าเส้นออกใบกำกับ (ไม่ติดด่าน CurrentTransaction)
- tenant: ทุก query `DepositKinds` ใหม่มี `CompanyId == companyId` · `pendingUndue` JE query มี `j.CompanyId`
- integration ไม่สร้างใบมัดจำเอง — `DepositKindCode` ใช้แค่ตรวจ 400 + หมายเหตุ mismatch (ตรง spec S5)
