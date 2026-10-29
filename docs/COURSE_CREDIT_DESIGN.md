# คอร์ส · Class Credits · Hour Pass — ออกแบบ + สถานะ (ทีม L · 2026-10-22)

> สถานะปัจจุบันสั้น ๆ — ประวัติรอบอยู่ `CHANGELOG.md` · flow เงินอยู่ `DOCUMENT_FLOW.md` §6.6

## 1. ของที่มีอยู่ก่อน (สำรวจแล้ว)

| ของเดิม | คืออะไร | ใช้ได้ไหมกับคอร์ส |
| --- | --- | --- |
| `pos-packages.html` + `ServicePackage` (`PosService.Packages.cs`) | แพ็กเกจบริการ "ชุดขั้นตอน" ขายครั้งเดียวจบ (สระ+ทำเล็บ 399฿) + ค่าคอมต่อขั้นตอน | **ไม่ใช่** เครดิต — ไม่มีจำนวนครั้ง/อายุ/ยอดคงเหลือ · บิล POS ลงรายได้ 41000 ทันที |
| `RevenueRecognitionController` / `revenue-recognition.html` | สัญญา + performance obligation + ตารางรับรู้ตามงวด (Dr 217xx / Cr 41100 — รอบ 205 W6 แก้จาก 218 แล้ว · `Helpers/DeferredRevenueAccount`) | ตารางตามเวลา ไม่ใช่ตามการใช้ |
| `deposits.html` / `deposit-center.html` (`DepositPolicyResolver`) | มัดจำ/เงินรับล่วงหน้าที่จะหักกับใบสุดท้าย | คอร์สไม่มี "ใบสุดท้าย" — ใช้เส้นขายเงินสดที่บรรทัดลงผังหนี้สินแทน |
| `CmsBookingService` (ทีม B กำลังทำ slot engine) | บริการ/ช่องเวลา/การจองจากเว็บ | ต่อผ่านสัญญาบาง `ICreditBookingLedger` — **ไม่แตะ slot logic** |
| `ChartOfAccountTemplates` | ผังมาตรฐาน | มี **21713 ค่าบริการรับล่วงหน้า** (หนี้สิน) + **42000 รายได้จากการบริการ** ให้ทุกบริษัทแล้ว — ไม่ seed เพิ่ม |

## 2. ทำแล้ว

**ไฟล์หลัก**: `Helpers/CourseCreditMath.cs` (ตัวคำนวณ pure) · `Models/Entities/CourseCredit.cs` · `Models/DTOs/CourseCredit/CourseCreditDtos.cs` ·
`Services/Interfaces/ICourseCreditService.cs` (`ICourseCreditService` + `ICreditBookingLedger`) · `Services/Implementations/CourseCredit/CourseCreditService.cs` ·
`Services/Background/CourseCreditExpiryJob.cs` · `Controllers/CourseCreditController.cs` (`api/companies/{id}/course-credits`) · `wwwroot/pages/course-credits.html` ·
schema `DatabaseMigrationHelper.CourseCreditStatements` (5 ตารางใหม่) · เทสต์ `Accounting.Tests/CourseCreditMathTests.cs`

1. **แพ็กเกจ** (`CreditPackage`): หน่วย ครั้ง/ชั่วโมง · จำนวน · อายุวัน (0 = ไม่หมดอายุ) · ราคา + รวม VAT ไหม + อัตรา 7/0/-1 · รูปแบบคลาส (ทุกแบบ/1-on-1/กลุ่ม) ·
   โหมดครูฝึก (ลูกค้าเลือก/ระบบกำหนด) + รายชื่อครู · บริการที่ใช้ได้ (`SiteBookingService` · ว่าง = ทุกบริการ) · ยกเลิกฟรีก่อนกี่ ชม. · ผังหนี้สิน/รายได้ (ว่าง = 21713/42000)
2. **ขาย** (`SellAsync`): ออกเอกสารผ่าน `IDocumentService.CreateDocumentAsync` + `ApproveDocumentAsync(SystemWorkflow)` — จด VAT ⇒ ใบกำกับภาษี/ใบเสร็จ (ขายเงินสด) ·
   ยกเว้น §81 หรือไม่จด VAT ⇒ ใบเสร็จ · บรรทัดเดียวลงผัง 21713 ⇒ AutoPost เดิมลง Dr เงินสด/ธนาคาร · Cr 21713 (สุทธิ) · Cr 21911 ภาษีขาย (§78/1 = วันรับเงิน) ·
   แล้วเปิดบัตร (`CreditPass`) ยอดสัญญา = **ยอดสุทธิที่ใบลง 21713 จริง** (อ่านกลับจากบรรทัดเอกสาร ไม่คิดเอง) · ค่าสัญญาตรึงลงบัตร (แก้แพ็กเกจไม่ย้อนเปลี่ยน)
3. **เปิดบัตรจากเอกสารขายที่ออกแล้ว** (`ActivateFromDocumentAsync`): ใบจากหน้าเอกสาร/ช่องทางอื่นที่บรรทัดลงผังหนี้สินรายได้รับล่วงหน้า · ใบที่ลงรายได้ไปแล้ว = ปฏิเสธ
   (กันรับรู้ซ้ำ) · ใบเดียวเปิดได้บัตรเดียว (unique index) · เป็นทางกู้เมื่อขายแล้วเปิดบัตรล้ม (ข้อความบอกเลขใบ + ห้ามขายซ้ำ)
4. **ยอดคงเหลือ** = ทั้งหมด − ใช้แล้ว − ถูกจอง · สมุดเครดิต `CreditLedgerEntry` (ซื้อ/จอง/คืน/ใช้/หมดอายุ) + JE ที่ผูก = "ประวัติการใช้"
5. **จอง** (`BookAsync`): รอบกลุ่มที่ตั้งไว้ (capacity ต่อรอบ · ล็อกแถวรอบ) หรือ 1-on-1 (สร้างรอบ capacity 1 · ครู: ลูกค้าเลือก ⇒ ตรวจอยู่ในรายชื่อ + ไม่ติดรอบซ้อน ·
   ระบบกำหนด ⇒ `PickTrainer` ตัดคนติดรอบ เลือกภาระน้อยสุด เสมอ = ตามลำดับ) · ด่าน `CannotBookReason` (สถานะ · หมดอายุ · รอบหลังวันหมดอายุ · เครดิตพอ · รูปแบบ · บริการ)
   · จอง = **กันเครดิต ไม่รับรู้รายได้**
6. **ปิดการจอง**: มาเรียน / no-show (ตัดเครดิต + รับรู้รายได้ทั้งคู่ · no-show บันทึกก่อนถึงเวลาไม่ได้) · ยกเลิก: ร้านยกเลิก = คืนเสมอ · ลูกค้ายกเลิก ≥ N ชม. = คืน ·
   น้อยกว่า = ริบ (รับรู้รายได้ สถานะ "ยกเลิกช้า") · ยกเลิกทั้งรอบ = คืนทุกคน
7. **รับรู้รายได้** (`JournalEntryBuilder` ตัวกลาง): Dr 21713 · Cr 42000 = `RecognizeOnUse` (ราคาต่อเครดิตปัด AwayFromZero · ครั้งสุดท้ายรับส่วนที่เหลือทั้งหมด ·
   ไม่เกินส่วนที่เหลือ) · วันที่ = วันให้บริการ · งวดปิด ⇒ ลงวันนี้ + หมายเหตุในสมุด (ไม่เงียบ) · วันนี้ก็ปิด ⇒ ล้มดัง
8. **หมดอายุ**: งานกลางคืน `CourseCreditExpiryJob` (ทุก 6 ชม. · `JobLock`) + ปุ่มบนจอ ⇒ Dr 21713 · Cr 42000 ส่วนคงเหลือทั้งหมด (breakage) · ไม่มี VAT เพิ่ม ·
   บัตรที่มีการจองค้าง **ไม่ปิดเอง** — ป้ายเตือนบนจอให้ปิดการจองก่อน
9. **ด่านใบขายยังมีผล**: ใบขายถูกยกเลิก/ลบ หรือมีใบลดหนี้อ้าง ⇒ จอง/ตัดเครดิต/ปิดหมดอายุไม่ได้ (รับรู้ต่อ = 21713 ติดลบ) — ข้อความบอกเหตุ
9b. **ฝ่ายค้านรอบ 1 (แก้แล้ว)**: ① ยกเลิกใบขาย/ใบลดหนี้ผ่านด่าน `CreditPassDocumentRule` (hook ในธุรกรรมของ `DocumentService.VoidDocumentAsync` /
    `ApplySourceDocumentAdjustmentsAsync` — บัตรไม่ใช้ = ยกเลิก+ปิดบัตร · ใช้แล้ว = บล็อก · ลดหนี้ได้เฉพาะส่วนที่ยังไม่รับรู้ทั้งก้อนแล้วปิดบัตร) ·
    ② เปิดบัตรยึดยอด JE (บาท) + ห้ามซ้ำในสายเอกสาร · ③ `HoldAsync` advisory lock ต่อ ref + ตรวจซ้ำหลังล็อกบัตร + unique index การจองที่มีผลต่อ ref ·
    ④ ขายกันกดซ้ำ (`ClientRequestId`) + อนุมัติล้ม ⇒ ลบใบร่าง/บอกเลขใบร่าง · ⑤ ล็อกตารางครูต่อวันก่อนตรวจว่าง · ⑥ งานหมดอายุ: ใบเดียวล้มไม่หยุดทั้งรอบ (นับ `Failed` + log Error) ·
    จุดเรียกล็อกใน `tools/required_call_site_check.py`
10. **สัญญาบางกับเครื่องจอง** `ICreditBookingLedger` (ลงทะเบียนตัวเดียวกับบริการ): `HoldAsync(externalRef …)` กันเครดิต (FEFO ถ้าไม่ระบุบัตร · idempotent ต่อ ref) ·
    `ReleaseAsync(ref, byBusiness)` ยกเลิกตามนโยบาย · `SettleAsync(ref, attended)` ตัดเครดิต — อ้างการจองภายนอกด้วยสตริง (เช่น `"SiteBooking:{id}"`) ⇒ ไม่ต้องแก้ schema ของ `SiteBooking`
11. **UI** `/pages/course-credits.html` (เมนู POS หน้าร้าน › คอร์ส & เครดิต): สรุป (บัตรใช้งาน · รายได้รับล่วงหน้าคงเหลือ · ใกล้หมดอายุ · รอปิด) · แท็บบัตรของลูกค้า
    (ค้น/กรอง · รายละเอียด: การจอง + สมุดเครดิต + ใบสำคัญ) · ตารางคลาส (รอบ · ผู้จอง · มาเรียน/ไม่มา/ยกเลิก) · แพ็กเกจ · ขาย (ตัวเลือกผู้ติดต่อกลาง) · เปิดบัตรจากใบ
12. **สิทธิ์**: อ่าน = สมาชิก · ขาย/จอง/ปิด/ยกเลิกการจอง = `POS.Cashier` · แพ็กเกจ/รอบ/ปิดหมดอายุ/เปิดจากใบ = `POS.Manager` · controller อยู่ใน `write_permission_gate_check` WATCHED ·
    ทุกการขยับยอดล็อกแถว (`FOR UPDATE`) ในธุรกรรมเดียวกับ JE + สมุด + audit (`AddChainedAuditLog` พร้อม RuleCode/LegalReference)

## 3. ตัวอย่างตัวเลข (ล็อกในเทสต์)

| เคส | ผล |
| --- | --- |
| 10 ครั้ง 4,500 รวม VAT ⇒ สุทธิ 4,205.61 | ครั้งที่ 1–9 = 420.56 · ครั้งที่ 10 = 420.57 · รวม 4,205.61 |
| 3 ครั้ง 1,000 | 333.33 · 333.33 · 333.34 |
| 0.15 / 10 ครั้ง (ราคาต่อเครดิตปัดขึ้น 0.02) | 7 ครั้งแรก 0.02 · ครั้งที่ 8 = 0.01 · 9–10 = 0 (ไม่เกินสัญญา · ไม่ติดลบ) |
| ใช้ 4 จาก 10 แล้วหมดอายุ | รับรู้แล้ว 1,682.24 · breakage 2,523.37 · รวม = สัญญา |

## 4. ต่อกับเครื่องจองของทีม B (merge)

- เครื่องจองเป็นเจ้าของช่องเวลา/ความจุ/การยืนยัน · โมดูลนี้เป็นเจ้าของเครดิต + รายได้ — ห้ามคำนวณซ้ำฝั่งเครื่องจอง
- จุดต่อที่ทีม B ใส่ (ยังไม่ต่อสาย — งานของทีม B หลัง merge): ตอนยืนยันการจองที่ลูกค้าเลือก "ใช้เครดิต" ⇒ `HoldAsync(new CreditHoldRequest(contactId, passId?, serviceId,
  "SiteBooking:"+id, startUtc, durationMin, format))` · ตอนยกเลิก ⇒ `ReleaseAsync(ref, byBusiness)` · ตอน Completed/NoShow ⇒ `SettleAsync(ref, attended)` ·
  การจองที่ใช้เครดิต**ห้าม**ออกเอกสาร ERP ค่าบริการซ้ำ (`SyncBookingToErpAsync`) — รายได้มาจาก JE รับรู้ของโมดูลนี้แล้ว
- `CreditClassBooking.ExternalRef` มี index (CompanyId, ExternalRef) — unique บางส่วน (แถวที่ยังไม่ยกเลิก) + รอบ 208 `IX_CreditClassBookings_Company_ExternalRef_Any` (ค้นทุกสถานะ)
- **รอบ 208 (ฝ่ายค้าน W6 ข้อ 1)**: บิล POS ที่เปิดจากคิวการจองที่จ่ายด้วยบัตร ⇒ ปิดบิล = `SettleAsync(ref, attended)` ในธุรกรรมปิดบิล (`CreditBookingLink.ForPosClose`) ·
  ยกเลิกบิลที่ตัดเครดิตแล้วไม่ถอยสถานะการจอง (`PosVoidRevertsCompleted`) · คืนเครดิตไปแล้ว ⇒ ปิดบิลไม่ได้ (ข้อความทางไปต่อ)
- **รอบ 208 (ข้อ 2–4)**: ยกเลิกใบลดหนี้คืนบางส่วน ⇒ คืนเครดิต/ถอนยอดคืน (`OnVoidPartialCreditNote` · แถว `AdjustReversal` · `CreditLedgerEntries.DocumentId`) ·
  คืนบางส่วนต้องเท่า k × ราคาต่อเครดิตคงเหลือ (ส่วนที่ยังไม่รับรู้ ÷ เครดิตที่ยังไม่ใช้) · คืนตามจำนวนเครดิตส่งเครดิตตรง · ไม่จด VAT/ยกเว้น ⇒ ปุ่มไม่สร้างใบลดหนี้

## 5. Backlog (ยังไม่ทำ + เหตุผล)

| # | งาน | เหตุ |
| --- | --- | --- |
| B1 | **ขายผ่าน POS** | **ตั้งใจไม่ทำ — คำตัดสินเจ้าของข้อ 161 (2026-10-26)**: ขายที่หน้า "คอร์ส & เครดิต"/ร้านออนไลน์เท่านั้น · ไม่แตะ JE ขาย/คืนของ POS |
| B2 | ~~ขายผ่านร้านออนไลน์~~ **ทำแล้ว (รอบ 205 W6)** — `SiteProduct.CreditPackageId` (ช่อง "ขายเป็นคอร์ส" ใน cms-edit · สั่งได้ครั้งละ 1) ⇒ `CmsCommerceService.SyncOrderToErpCoreAsync` ส่งผังจาก `CourseCreditOnlineSale.UnearnedAccountsAsync` เข้า `BuildOrderErpLines` (บรรทัดคอร์สลง 21713) ⇒ ใบอนุมัติ ⇒ `IssuedDocumentHooks.TryActivateCoursePassAsync` เปิดบัตร (`ActivateFromDocumentAsync` · ช่องทาง Online) | จุดเชื่อม: ไม่แตะ `ConfirmPaymentAsync` (ทีม U) · ครอบทั้ง gateway และสลิป เพราะ hook รันหลังอนุมัติทุกใบ · ล้ม/หลายหน่วย ⇒ `[COURSE-PASS-FAILED]` บนใบ + log (ทางไปต่อ: ปุ่ม "เปิดบัตรจากเอกสารขาย") |
| B3 | ~~คืนเงินบางส่วน~~ **ทำแล้ว (รอบ 205 W6 · R-38 · ยังไม่ผ่านฝ่ายค้าน)** — `CreditPassDocumentRule.RefundPreview` ตัวเดียว: ยอด = ส่วนที่เหลือ ⇒ ปิดบัตร (เดิม) · ยอดน้อยกว่า ⇒ ลดเครดิต `floor(ยอด ÷ ราคาต่อเครดิต)` (ชั่วโมงขั้น 0.25) โดยคงเหลือต้อง > ใช้แล้ว+จองไว้ · บัตรยัง Active · แถว `Adjust` ในสมุด · หลังคืน ราคาต่อเครดิต/รับรู้/breakage ใช้ `EffectiveContract = สัญญา − คืนแล้ว` · ปุ่ม "ออกใบลดหนี้คืนเงิน (ร่าง)" บนหน้าบัตร (`DraftRefundCreditNoteAsync` · PosManager · ไม่อนุมัติเอง) | เทสต์ `CreditPassDocumentRuleTests` (ทั้งสองทิศ) |
| B4 | ~~ต่อสายเครื่องจอง~~ **ทำแล้ว (รอบ 205 W6 · R-39)** — `CreateBookingRequest.PayWithCredit/CreditPassId` ⇒ `CmsBookingService.Credits.cs` Hold ในธุรกรรมเดียวกับ Decide (ล้ม = ไม่มีการจอง) · ยอดเงินการจอง 0 · ไม่ออกเอกสาร ERP · เสร็จ/ไม่มา/ยกเลิก ⇒ Settle/Release (`Helpers/CreditBookingLink`) · ความจริง "จ่ายด้วยบัตร" อยู่ที่ `CreditClassBooking.ExternalRef` (ไม่แก้ schema) | ยังไม่มี UI ให้แขกเลือก (หน้าเว็บจองเป็นแขกไม่ล็อกอิน) — เปิดใช้ผ่าน API/หน้าหลังบ้านรุ่นถัดไป · ไม่แตะ `BookingSlotEngine` |
| B5 | ขยายอายุบัตร / โอนเครดิต / แพ็กเกจหลายบริการแยกจำนวน (เช่น 5 กลุ่ม + 2 ตัวต่อตัว) | ยังไม่มีผู้ขอ · โครงสร้างสมุดรองรับ (แถว Adjust ภายหลัง) |
| B6 | ~~ผัง 218~~ **ทำแล้ว (รอบ 205 W6 · R-40)** — `Helpers/DeferredRevenueAccount.Pick` (21713 → ผังเดียวในกลุ่ม 217 · ไม่นับ 21714) · ไม่พบ ⇒ ล้มดัง (ไม่สร้าง JE ขาข้างเดียว) · รายงานตรวจ JE เดิมที่ลง 218 อ่านอย่างเดียว `GET revenue-recognition/deferred-account-audit` (กล่องเหลืองในแท็บรายได้รอรับรู้) | ระบบไม่แก้ JE ที่ลงแล้ว — นักบัญชีออก JE ปรับปรุง |
| B7 | Integration test (DB จริง) ของเส้นขาย→จอง→ตัด→หมดอายุ | env ไม่มี Testcontainers · ตัวเลขทั้งหมดล็อกใน pure helper แล้ว |
| B8 | หน้าแขก/สมาชิกดูยอดคงเหลือเอง (portal) | ต้องผ่าน PDPA/สิทธิ์ของ portal |
