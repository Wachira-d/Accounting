# รอบ 194 — แผนโค้ด "ประเภทเงินมัดจำ" (DepositKind)

> ที่มา: ทีมแผนโค้ด (อ่านอย่างเดียว) · main agent ยังต้อง verify file:line ก่อนลงมือทุกชุด · คำตัดสินกฎหมายรอ `legal-L1-forfeit.md` + `legal-L2-classification.md`

## 1. จุดที่แตะมัดจำฝั่งขายตอนนี้ (callers: DepositPolicyResolver 61 · DepositVatTreatment 57)

**ทางเข้า**
- ฟอร์มเอง `wwwroot/pages/documents.html` — ช่องติ๊ก 917-919 · radio `fDepositVat` 936-946 · `_resetDepositVatDefault` 4719-4733 · hydrate 8436-8446 · payload 8920-8928 · `_depositVatInfo` 1617
- POST `/api/documents` รับ API key (`DocumentController.cs:353`) → `CreateDocumentRequest` (`DocumentDtos.cs:125-127`)
- ที่พัก `LodgingService.Lifecycle.cs:379-431` `CreateDepositReceiptAsync` · ตัวตัดสิน `LodgingService.cs:80-87` · บันทึก property 251-261 · `LodgingReservationPaymentHandler.cs`
- CMS booking `CmsBookingService.cs:482-523` (ไม่อ่านค่าตั้งบริษัท — VatImmediate เสมอ)
- OCR `OcrService.cs:6279` สร้าง entity ตรง
- integration `IntegrationService.cs:1060-1066, 2032-2046, 2959-2961, 3257-3264` · DTO `IntegrationDtos.cs:153`
- POS `PosOrder.IsDeposit` ไม่มีผู้อ่าน · API v1/LINE/มือถือ/recurring ไม่รับ IsDeposit · clone ไม่พา IsDeposit (ช่องโหว่เดิม)

**ทางออก** — JE รับเงิน `DocumentService.cs:15267-15305` · `RealizeDepositCoreAsync` 3445 · `RefundDepositAsync` 3944 ·
`ApplyDepositToInvoiceCoreAsync` 4143-4172 · drives 14870-15094 · `GuardDrivesGrossApply` 15770 · หักฐาน 15781/15820/15869 ·
void/purge 7738, 8066-8190, 8329 · คำเตือนอนุมัติ 17211/17247 · ศูนย์มัดจำ 3568/3928 · ที่พัก เช็คเอาต์ 636-829 · ยกเลิก/ริบ 941-1013 ·
คืน 1018+ · `LoadDepositSnapshotsAsync` 443-455 (ดึงทุก IsDeposit ของการจอง) · TaxService 212-240, 592-610, 1424 · e-Tax · PDF สอง renderer

## 2. ที่พัก — ไม่มีเงินประกันความเสียหายเลย (ค่าเสียหายเป็นแค่ folio charge คิด VAT) · ผังโรงแรมมี 21530 · `DepositTransaction "Guarantee"` ไม่มีใครเขียน (ไม่รวม)

## 3. ผังบัญชี — 21610 เงินมัดจำรับ · 21620 เงินค้ำประกัน · ผังโรงแรม 21510/21530 ⇒ ไม่เพิ่มเลขใหม่: เงินประกันใช้ 21530 ถ้ามี ไม่งั้น 21620

## 4–8. entity/migration · resolver `ResolveKind` · `DepositDocumentShaping.Apply` · DTO/ฟอร์ม · 4 ชุดงาน (A แกน · B เส้นเอกสาร · C ตั้งค่า+ช่องทาง · D ที่พัก) · กฎ required_call_site · ความเสี่ยง 8 ข้อ
ดูข้อความเต็มในสรุปของ main agent (บันทึกลง CHANGELOG ตอนปิดรอบ) — ข้อสำคัญ:
- ใบเดิม `Documents.DepositKindId` NULL = PartOfPrice + โหมดอ่านย้อนด้วย `OfDocument` · **ห้าม UPDATE Documents** · ห้ามใส่ใน `DocumentSignedContent`
- ไม่มีประเภทบน payload ⇒ `Shaping.Apply` ไม่แตะอะไร (คู่ค้า/OCR เดิมเหมือนเดิม) · ห้ามบังคับ 7% ให้บรรทัด 0 (§81)
- บันทึกหน้าตั้งค่าที่พักต้องล้าง `DepositVatTreatment` legacy ไม่งั้น migration ตอนสตาร์ทผูกทับ
- `LoadDepositSnapshotsAsync` ต้องกรองเงินประกันออกก่อน `PlanCheckout`/`PlanCancellation`
- CMS ต่อสายแล้วพฤติกรรมเปลี่ยน ⇒ ต้องคงพฤติกรรมเดิมถ้าบริษัทไม่ได้ตั้ง
