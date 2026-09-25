# รอบ 194 — ฝ่ายค้านรอบสี่ (ตรวจ `6ec10976` ทีม M3 · R3-1 · R3-2 · P-1/P-2/P-4) · อ่านอย่างเดียว

ไม่ได้รันโค้ด (ไม่มี SDK) — ทุกข้อเปิดไฟล์ตรงบรรทัด (HEAD `b70fd0c6`)

## CONFIRMED

**R4-1 (ต่ำ-กลาง) เช็คเอาต์ใช้ใบร่างเดิม — `DueDate` ค้างจากรอบก่อน ทั้งที่มี sentinel ล้างค่าอยู่แล้ว**
- `LodgingService.Lifecycle.cs:750` ส่ง `DueDate: create.DueDate` · รอบนี้ `CollectBalanceNow=true` ⇒ `null` = "คงค่าเดิม" (`DocumentService.cs:2539`)
  ⇒ ใบที่เก็บเงินแล้วค้างวันครบกำหนด +30 วันของรอบที่ล้ม · ตัวล้างมีอยู่แล้ว: `DateTime.MinValue` ⇒ null (:2540-2542) — แก้ 1 คำ `create.DueDate ?? DateTime.MinValue`
- ช่องมัดจำ: `""`/`0m` ล้าง `DepositAppliedRef`/`DepositBaseDeducted` ครบ (:2417-2418 · :2620) ✓ · `DepositAppliedAmount`/`DrivesJournal` เส้นที่พักไม่เคยตั้ง ✓
- ช่องที่ยังไม่ล้าง: `BillDiscountAmount` — ใบร่างจากรุ่นก่อนรอบ 193 R3-1 (ตอนฐานมัดจำยังเก็บในส่วนลดท้ายบิล) ถ้าถูกหยิบมาใช้ ⇒ หักซ้ำ (ส่วนลด + ฐานมัดจำ) · `Plan` ระบุเองว่ารองรับ "ค้างจากรุ่นก่อนแก้" จึงควรส่ง `BillDiscountAmount: 0m, BillDiscountPercent: 0m` ด้วย
- ใบร่างของการจองอื่น/บริษัทอื่นไม่ถูกหยิบ ✓ (`LodgingCheckoutDraft.cs:203-206` · เลขจอง unique ต่อบริษัท `AccountingDbContext.cs:3360` · ใบริบอ้างเลขใบมัดจำ ไม่ใช่เลขจอง ✓)

**R4-2 (กลาง) integration ขายเงินสด: "รอสักครู่" ชั่วคราว ⇒ ลงบัญชีถาวรเป็นตั้งหนี้**
- R3-2(ง) ทำให้ `PostCashSaleJournalAsync` ถือคีย์จริง ⇒ ชนปุ่มริบ/ตัดชำระได้ `DEPOSIT-REALIZE-BUSY` ⇒ `IntegrationService.cs:1138-1146` catch ทั่วไป ⇒ `IssuedAsCashReceipt=false` + JE ตั้งหนี้เต็มยอด · มัดจำไม่ถูกหัก · ตอบ `success=true`
- ก่อนแก้เส้นนี้ข้ามล็อก (ผิดคนละแบบ) — ตอนนี้ความผิดพลาดชั่วคราวกลายเป็นผลบัญชีคนละเส้นที่ต้องจับคู่มือ (AR ค้างเท่ามัดจำ + 217xx ค้าง) · ควรแยก `DepositBusyRuleCode` ออกไปตอบ `success=false`/retry (ใบถูก Approved แล้ว — ต้องถอยแบบเดียวกับเส้น ImmediateVat หรือเก็บไว้ให้ยิงซ้ำ)

## PLAUSIBLE

- **P4-1 ใบกำกับของการริบที่ค้างร่างจากรุ่นก่อนแก้**: `ResumeForfeitInvoice` อนุมัติใบร่างเดิม (:4148-4150 · :4206) โดยไม่ล้าง `PaymentDate` ที่รุ่นเก่าตั้ง = วันรับเงิน ⇒ R3-1 เกิดซ้ำกับใบที่ล้มกลางทางก่อน deploy (F2 #9) · แก้: ขั้น resume ตั้ง `PaymentDate=null` ก่อนอนุมัติ
- **P4-2 CMS หมายเหตุอาจไม่ตรงความจริง**: `ApproveDocumentAsync` มีขั้นหลัง `CommitAsync` (:6417) ที่โยนได้ (`contactName` query :6449 · `GetDocumentAsync` · webhook) ⇒ catch ใน `CmsBookingService.cs:237-247` reload ได้ Approved จาก DB (ข้อมูลถูก) แต่เขียน "ยังเป็นร่าง ยังไม่ลงบัญชี" (F2 #7 — ข้อความต้องตรวจสาเหตุจริง) · แก้: อ่าน `Status` หลัง reload ก่อนเลือกข้อความ
- **P4-3 `LockDepositBalancesAsync` reload เฉพาะ Unchanged**: ไม่ทิ้งการแก้ของผู้เรียก ✓ แต่แถวมัดจำที่ Modified ก่อนล็อกคงค่าเก่า (ถ้าผู้เรียกอ่านยอดจากมันต่อ = lost update) · ไม่พบจุดเกิดจริงใน 9 เส้น (void/purge ไม่แตะใบก่อนล็อก · drives/LoadTaxed โหลดหลังล็อก) — ควรโยนเมื่อพบ Modified แทนการข้ามเงียบ
- **P4-4 P-4 เคสขอบ**: เลขอ้างอิงสองใบแต่ resolve ได้ใบเดียว (อีกใบถูกลบ) ⇒ `numbers.Count==1` ⇒ ขา Dr ของอีกใบถูกนับให้ใบที่เหลือ (คืนเกิน · ถูกหนีบที่ 0) · ขา Dr 21913 ที่ผูกไม่ได้ถูกข้ามเงียบ (ไม่นับใน `Unattributed`) ⇒ `RecognizedAt` ค้าง · ใบที่ void→กู้→void (JE ต้นฉบับสองชุด `OriginalEntryId==null`) นับฐานซ้ำ — มีมาก่อน
- **P4-5 ขอบเดือน UTC**: `when = RealizeDate ?? UtcNow` (:3758) ใช้ตัดสิน `sameMonth` แต่วันที่ใบถูกทำเป็นวันไทย ⇒ ริบ 31/01 หลัง 17:00 UTC ธงอาจผิดเดือน (มีมาก่อน R3-1)

## NOT-A-BUG (ตรวจแล้ว)
- **R3-1 ปิดจริง**: route `ForfeitTaxInvoice` คืน `forfeitDate` เสมอ + ธงเมื่อคนละเดือน (`DepositPolicyResolver.cs:815-823`) · `PaymentDate: null` (:4166) ⇒ `TaxPointResolver.Resolve` = `DocumentDate` (ไม่ส่ง ServiceUsed/Delivery) = `EntryDate` ของ JE (:16487) ⇒ GL/ภ.พ.30/§87 เดือนเดียวกัน · ผู้เรียก 2 จุดระบุเส้นครบ ไม่มีผู้เรียกค้าง
- **ทุกเส้นอยู่ในธุรกรรม**: ปุ่ม VAT :3470 · void :8098 · purge :9079 · drives (Approve tx ≤:6417 · :7303 · :7424 · PostCashSale ownTx) · LoadTaxed ผ่าน AutoPost · integration ไม่มีธุรกรรมซ้อน (ไม่มี BeginTransaction ใน `IntegrationService`) · ไม่มี retrying strategy ⇒ BeginTransaction เปล่าใช้ได้
- **deadlock**: คีย์เป็น try ไม่รอทุกเส้น ⇒ แม้ void ล็อกแถวตัวเองก่อนคีย์ (:8105) และ multi-drives ล็อกทีละใบตามลำดับเลขอ้างอิง ก็ไม่วนรอ (ฝั่งหนึ่งได้ busy แล้วปล่อย) · การเรียง Guid ของ C# ≠ uuid ของ PG ไม่มีผลเพราะไม่มีใครรอคีย์
- **PostCashSale**: rollback ก่อน reload ✓ · rollback ล้ม log แล้วโยนตัวเดิม ✓ · dispose ใน finally ก่อนผู้เรียก `VoidDocumentAsync` ✓ · `catch {}` ถูกแทนด้วย log + Unchanged ✓
- **CMS**: `SaveChanges` ก่อน baseline ⇒ `ErpDocumentId` ไม่หาย · `ReferenceEqualityComparer`/`IReadOnlySet` แบบเดียวกับ DocumentService · ใบกลับเป็นร่าง ไม่มี Approved ไร้ JE
- **P-4 regex**: `มัดจำ\s+{เลข}(?=$|[\s),])` REC-1≠REC-10 ✓ · คำอธิบายมาจาก `mDeposit.DocumentNumber` (:15750-15753) ตรงกับ `numbers` · `AddLine` ไม่รวมบรรทัด ⇒ ขาแยกรายใบ · JE ผูกเอกสารแก้ไม่ได้ (`AccountingService.cs:680-691`) · ผูกไม่ได้ ⇒ หมายเหตุบนใบมัดจำ + log · `skipAlreadyReleased` ตรงกับ 7c ที่ล้างตัวชี้
- **คอมไพล์**: `List<(Guid Id, DocumentType DocumentType)>` → `IReadOnlyList<(Guid Id, DocumentType Type)>` = identity conversion (ชื่อ tuple ไม่ใช่ส่วนของชนิด) · `deducted.Contains(d.Id)` (List<Guid>) แปลเป็น SQL ได้ · `return new();` target-typed · `ids` (Guid[]) → uuid[] แบบเดิม
