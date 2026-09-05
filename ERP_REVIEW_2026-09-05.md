# ERP REVIEW 2026-09-05 — ผลตรวจ "ทีม ERP" รอบที่ 1 (ทีม A–F) + แผนสู่ ERP

> โจทย์จากเจ้าของโปรเจกต์: **ตั้งทีมที่ครอบทุกเรื่อง ทุกมุม ทุกรายละเอียด — ความต่อเนื่อง ถูกต้อง
> ครบถ้วนของข้อมูล — เพื่อพัฒนาเป็น ERP ครบทุกด้าน · ตรวจว่าประเภทเอกสาร จุดแสดงผล จุดให้เลือก
> ตรงกันไหม · แต่เบื้องต้นระบบเดิมต้องทำงานถูกต้องทุกส่วนก่อน · ตั้งทีมตรวจ เช็ค โต้เถียงวนซ้ำ**
>
> วิธีทำ: 9 ทีม subagent อ่านโค้ดจริงคนละมุม (brief ร่วม `erp-review/2026-09-05/BRIEF.md`) →
> main agent เป็น "ฝ่ายค้าน" เปิดไฟล์หักล้างทุก P0/P1 ก่อนเชื่อ (`VERIFY-main.md`) → แก้เฉพาะข้อที่
> ยืนยันแล้วและขนาด S/M → บันทึกที่เหลือเป็น backlog. **ไม่ได้คอมไพล์/รัน** (env ไม่มี .NET SDK)
> — รบกวน rebuild + รันเทสต์ฝั่งคุณ (`StockMovementSignTests` · `DocumentStatusRulesTests` ใหม่)
>
> commit ฐาน: `444d2cb` · branch `claude/erp-system-review-team-660mev`

## §0 สถานะการรัน (ต้องอ่านก่อน)

| ทีม | ขอบเขต | ผล | รายงานเต็ม |
| --- | --- | --- | --- |
| A | ประเภทเอกสาร × จุดแสดง/จุดเลือก/backend switch | ✅ ครบ 13 ข้อ | `erp-review/2026-09-05/report-A.md` |
| B | วงจรเอกสาร: echo/สืบทอด/สถานะ/void/approve | ✅ ครบ 14 ข้อ | `report-B.md` |
| C | GL · JE · VAT · WHT · สินทรัพย์ (§10 ของ SYSTEM_REVIEW) | ✅ ครบ 12 ข้อ | `report-C.md` |
| D | master data · settings · onboarding · import/export | ✅ ครบ 17 ข้อ | `report-D.md` |
| E | สต๊อก · costing · 3-way match · POS · โอนคลัง | ✅ ครบ 15 ข้อ | `report-E.md` |
| F | รายงาน/แดชบอร์ด ↔ สมุดบัญชี ("สองความจริง") | ⚠️ **บางส่วน** 10 ข้อ (agent ถูกตัดด้วย rate limit) | `report-F.md` |
| G | security/tenant ในส่วน §10 (SignatureApproval ภายนอก · PayslipPublic · Impersonation · CMS anonymous · LINE · /api/v1 · allow-list ของ write_permission_gate_check) | ✅ ครบ 9 ข้อ (รอบ 3) — **ไม่พบ IDOR/รั่วข้ามบริษัทตรง ๆ** ปัญหาหลักคือ "สิทธิ์" ไม่ใช่ "ตัวตน" | `report-G.md` |
| H | payroll/HR ↔ GL · โมดูลรอง (ExpenseClaim/PettyCash/Cheque/Advance) · recurring · bank recon · lodging/CMS ↔ เอกสาร | ✅ ครบ 17 ข้อ (รอบ 3) — P0 1 · P1 9 | `report-H.md` |
| I | label map/ฟอร์ม↔payload/dead reference ทุกหน้า · เมนู↔สิทธิ์ · **แผนที่ช่องว่างสู่ ERP** | ✅ ครบ 10 ข้อ + ตาราง enum×หน้า + ERP gap map 21 โมดูล (รอบ 3) | `report-I.md` |

**ครบทั้ง 9 ทีมแล้ว** (F ได้บางส่วน — ควรรันซ้ำให้ครบในรอบถัดไป: งบการเงิน/ภ.พ.30↔GL/export/scheduled report ยังไม่ได้ไล่) · G เสร็จแล้ว (ปิดความเสี่ยงอันดับ 1-4 ของ §10 SYSTEM_REVIEW: ตรวจแล้ว **ไม่ใช่บั๊ก** 5 พื้นที่ · ที่พบคือช่องอนุมัติทางอ้อมไม่มีด่านสิทธิ์ — แก้แล้ว)

## §1 สรุปผู้บริหาร

**ภาพรวม**: โครงหลักแข็ง (AutoPost 5 branch สมดุลทุกเคสใน simulation · void cascade ครบ 12 ผลข้างเคียง ·
merge ผู้ติดต่อ repoint FK จาก information_schema · unique index/ด่านลบส่วนใหญ่มี · prefix 16/16 ตรง ·
หัวเอกสารเป็นค่าจากเซิร์ฟเวอร์แล้ว) — **แต่ทุกทีมเจอ defect class เดียวกันซ้ำ**: "ด่านที่ครอบแค่ทางเดียว"
และ "สำเนามือที่ drift" คือ ด่าน/กติกาถูกเขียนไว้ที่หนึ่งแล้วไม่ถูกไล่ไปทุกทางเข้า (mobile approve ·
รายงานสต๊อกหลังเฟส 0 · IsClosingEntry หลัง C-T02 · `== Approved` 26 จุด · ชุดชนิดเอกสาร ≥ 25 จุด)

### แก้แล้วในรอบนี้ (คอมมิตเดียวกับไฟล์นี้ — ทุกข้อ main agent เปิดไฟล์ยืนยันก่อนแก้)
| ID | P | เรื่อง | จุดแก้ |
| --- | --- | --- | --- |
| B-02 | **P0** | อนุมัติผ่านมือถือตั้ง `Status=Approved` ตรง ⇒ เอกสารไม่มีเลข/JE/สต๊อก แต่รับชำระได้ | `MobileApiService.QuickApproveAsync` → `IDocumentService.ApproveDocumentAsync` (resolve ผ่าน IServiceProvider กัน DI cycle) · reject → Draft |
| E-01 | **P0** | รายงานสต๊อก `In − Out` กับ ledger ที่เก็บ OUT ติดลบ ⇒ ขายแล้วสต๊อก "เพิ่ม" COGS ติดลบ | `ProductService` Σ|OUT| 3 จุด + `Helpers/StockMovementSign` |
| E-04 | **P0** | `StockTransferController` เขียน movement ตรง ไม่แตะยอดคลัง (สองความจริงกลับมา) · checker ไม่จับทรง `Set<StockMovement>()` | controller มอบต่อ `IWarehouseService` ทั้ง 4 action · `stock_writer_check` ขยาย regex + negative test (controller เดิม = 3 จุด) |
| E-05 | **P0** | PO→GRN แล้วยังแปลง PO→PI ตรงได้ ⇒ สต๊อก+11500 ลงสองรอบ · 21240 ค้างถาวร | ด่านต้น `ConvertCoreAsync`: PO ที่มี GRN ไม่ Voided/Rejected → บล็อกพร้อมทางไปต่อ |
| C-01 | **P0** | DN/CN ของใบแจ้งหนี้บริการ (VAT พัก 21913) ไม่เคยเข้า ภ.พ.30 และค้าง 21913 ถาวร | `TryReclassifyUndueOutputVatAsync` รวม JE ของ CN/DN ลูก + stamp `OutputVatDueAt` ทั้งชุด · undo ล้างลูกด้วย |
| A-01 | P1 | ค่ารับรอง §65ตรี(4) YTD นับเฉพาะ `== Approved` ⇒ ใบที่จ่ายแล้วหลุด cap ทั้งปี | `Helpers/DocumentStatusRules.NotIssued` + ไม่ Voided |
| A-02 | P1 | portal ลูกค้าเห็น/ดาวน์โหลด/กด "ชำระ" ใบร่าง-รออนุมัติ-ตีกลับ · Voided ขึ้น "ชำระแล้ว" | `PortalService.PortalDocuments` ด่านเดียว 3 เมธอด · portal.html ป้ายจากสถานะ + ซ่อนปุ่มจ่าย |
| E-08 | P1 | ปรับสต๊อก ADJUST −5 ถูก `Math.Abs` เป็น +5 | `StockMovementSign.Normalize` |
| B-01 | P1 | แก้ไขเอกสารแล้ว "ล้างช่องข้อความ" 12 ช่องไม่มีผล (ส่ง null=คงเดิม) และพิมพ์ลงกระดาษ | `documents.html` `_txt()` — แก้ไข: ว่าง=`""` (ล้าง) · สร้าง: ว่าง=null |
| D-01 | P1 | แก้สินค้า: ฟอร์มมี SKU/หน่วย/ติดตามสต็อก แต่ payload ไม่ส่ง (silent no-op) | `products.html` ส่ง 3 ช่อง · ชนิดสินค้าล็อกพร้อมเหตุผล |
| F-01 | P1 | แดชบอร์ดหลักนับใบปิดบัญชี (IsClosingEntry) เข้ารายได้ ⇒ KPI ธ.ค./ปีนี้ = 0 หลังปิดปี | `DashboardService` 5 query ตัด `IsClosingEntry` |
| F-03 | P1 | aging ใส่ CN/DN ทั้งสองฝั่ง (CN ฝั่งขายไปลด AP) | `AgingReportService` กรองด้วย `CnDnPurchaseSideOverride` (null = พฤติกรรมเดิม จนกว่าจะ backfill) |
| A-04 | P2 | `DOC_NO_JE` ฟ้องใบเสนอราคา/PO ทุกใบ และพลาดใบ Paid ที่ไม่มี JE | จำกัดชนิดที่ AutoPost ลง JE + `DocumentStatusRules` |
| G-01 | P1 | เส้นอนุมัติผ่านลายเซ็น (`/approvals/setup·approve·reject`, `/external/quotations/approve`) ไม่มีด่าน `Document.Approve` · external ข้ามขั้นภายในที่ Pending · reject ตั้ง Rejected (ทางตัน B-03) | `SignatureApprovalService.RequireApproveAsync` ทุกทางเข้า · external ต้องมี actingUserId + ตรวจขั้นภายใน · reject → Draft |
| G-03 | P1 | LINE "บันทึก {ร้าน} {จำนวน}" อนุมัติ Expense อัตโนมัติให้ทุกสมาชิก + `catch {}` กลืน error แล้วตอบ ✅ | ด่าน `CanApproveAsync(Expense)` → ไม่มีสิทธิ์ = ร่าง · error = บอกว่าร่างแล้วอนุมัติไม่ผ่านพร้อมเหตุผล |
| G-04 | P2 | LINE postback ใช้สำเนามือ role list แทน `DocumentPermissionHelper` | ใช้ `CanApproveAsync(doc.DocumentType)` |
| G-05 | P1 | อนุมัติผ่านมือถือไม่มีด่านสิทธิ์ (B-02 แก้แค่ Status writer) | `MobileApiService` ตรวจ `CanApproveAsync` ก่อน `ApproveDocumentAsync` (403 `PERM-DOC-APPROVE`) |
| A-06 | P1 | ขายสินค้าคงคลังด้วยใบเสร็จ standalone ไม่ตัดสต๊อก/ไม่ลง COGS — **เจ้าของตัดสิน: ตั้งค่าได้ทุกทาง** | `CashSaleStockPolicy` (Ignore / MoveStockAndCogs=default / Block) + `Helpers/CashSaleStockRules` ใช้ทั้งทิศสต๊อก · COGS · ด่านอนุมัติ · หน้าตั้งค่า |
| F-08 | P1 | ใบวางบิลถูกนับเป็นลูกหนี้ (ไม่มี JE · ครอบใบแจ้งหนี้ = นับซ้ำ) — **เจ้าของตัดสิน: ปรับให้ถูกต้อง** | `Helpers/ArApScope.ReceivableTypes` แหล่งเดียว → Dashboard · Aging · SubLedgerRecon · Forecast · CustomerStatement (ปิด F-02/F-09 บางส่วน) |
| H-07 | P1 | ทิป POS fallback prefix 216 → 21610 "เงินมัดจำรับ" · TipPayout หา 2160 ที่ไม่มี — **เจ้าของตัดสิน: ตั้งค่าได้** | `CompanySettings.PosTipPayableAccountCode` + `Helpers/TipAccountResolver` (default 21814→21819 · ห้าม 216xx) ใช้ทั้ง POS และ TipPayout · หน้าตั้งค่า |
| I-01 | P1 | รายการเกิดซ้ำ "รายปี" สร้าง/แก้ไม่ได้ — select ส่ง `Yearly` แต่ enum ชื่อ `Annual` ⇒ 400 ทุกครั้ง · BiWeekly/SemiAnnual ไม่มีใน UI | recurring.html option+label ตรง enum 7/7 |
| I-02 | P1 | ปุ่มลบตาย 5 จุด/4 หน้า (employees · leave-types ×2 · project-time · roles) — `API.delete` ไม่มี (มีแค่ `del`) ⇒ TypeError ก่อนยิง | api.js alias `delete → del` (แก้ที่เดียวปิด 5 จุด) |
| I-04 | P1 | ลงทะเบียนที่ดินไม่ได้ — server บังคับ `DepreciationMethod.None` แต่ select ไม่มีตัวเลือกนั้น (ข้อความ error ชี้ทางแก้ที่ไม่มี) | เพิ่ม option None ใน fixed-assets.html + document-scan.html + label map |
| I-06 | P1 | สมาชิกคนไหนก็ POST `sample-data/seed`/DELETE ได้ — กินเลขรัน §86/4 จริง + `Status=Approved` ตรง + cleanup ลบผู้ติดต่อด้วยชื่อขึ้นต้น | ด่าน Owner/SystemAdmin (กติกาเดียวกับ BulkCleanupController) · seed ผ่าน IDocumentService ยังเปิด |
| H-01 | **P0** | ยกเลิกรอบเงินเดือนที่นำส่ง สปส. แล้วได้ (Void ไม่ตรวจ `SsoSettledAt` ขณะ Reopen ตรวจ) ⇒ กลับ JE จ่าย แต่ JE นำส่งอยู่ ⇒ 21815 ติดลบถาวร + เสี่ยงนำส่งซ้ำ | `PayrollRunEditPolicy.CanVoid` (กติกา สปส. เดียวกับ CanReopen) เรียกใน `VoidPayrollAsync` + เทสต์ล็อกให้สองด่านเท่ากัน |

### P0 ที่ **ยืนยันแล้วแต่ยังไม่แก้** (ขนาด M/L หรือต้องตัดสินใจ) — เรียงตามความเสียหาย
1. ~~**A-06**~~ ✅ ปิดแล้วเป็นการตั้งค่า `CashSaleStockPolicy` (เจ้าของตัดสิน 2026-09-05: "ตั้งค่าได้ทุกทาง") — **default = ตัดสต๊อก+COGS = เปลี่ยนพฤติกรรม** ใบเสร็จ standalone ที่มีสินค้า TrackStock จะเริ่มตัดสต๊อก/ลง COGS ตั้งแต่ deploy · บริษัทที่ต้องการแบบเดิมตั้งเป็น Ignore ได้ที่ ตั้งค่า → เอกสาร · เดิม:** ขายสินค้าคงคลังด้วย "ใบเสร็จรับเงิน" standalone (หรือแปลงจาก Quotation/BillingNote → Receipt) ลงรายได้แต่**ไม่ตัดสต๊อก ไม่ลง COGS** (`ApplyStockMovementsAsync` switch ไม่มี Receipt/RV `_ => 0` · DocumentService.cs:12490-12510) — ชนิดเอกสารบนจอเปลี่ยนกำไรขั้นต้น. **ต้องตัดสิน**: (ก) Receipt/RV ที่มีบรรทัดสินค้า TrackStock เดินสาย −1 + COGS เหมือน TIV หรือ (ข) บล็อกพร้อมบอกให้ใช้ใบกำกับ/ใบแจ้งหนี้ — ทีม A แนะ (ก) เพราะ POS ใช้เส้นตัวเองอยู่แล้ว
2. **E-07 [P1][M]** ปรับสต๊อก/ตรวจนับ/รับโอนขาด **ไม่มี JE เลย** (`StockCountService.cs:18` doc-comment "caller posts it" แต่ caller ไม่ทำ) ⇒ GL 11500 กับ subledger ห่างขึ้นทุกรอบนับ — ทางแก้ที่ทีม E เสนอ: "ใบปรับปรุงสต๊อก" เป็นเอกสาร (เลข·อนุมัติ·JE·เหตุผล·สิทธิ์) ปิด E-07/E-08/E-10 พร้อมกัน
3. **E-02 [P1][M]** costing/รายงานรู้จักแค่ IN/OUT/ADJUST แต่ ledger ถูกป้อน OPENING/TRANSFER_* ⇒ สต๊อกยกมาหายจาก FIFO/WAC rebuild/รายงาน — ต้องมี `MovementTypes` static class + นิยาม "เป็น layer/บริโภค" ที่เดียว
4. **D-04 [P1][M]** นำเข้าผังบัญชี CSV ตั้ง `Level = code.Length <= 4 ? 1 : 2` ⇒ 59 จุดที่กรอง `Level >= 4` มองไม่เห็นบัญชีที่นำเข้า (AutoPost ตก fallback/LogWarning · payroll หา 54111 ไม่เจอ) — ระบบใช้ไม่ได้กับลูกค้าที่ย้ายจากโปรแกรมอื่น · ต้องมี `ChartOfAccountLevel.Resolve` ตัวเดียว (Create/Import/Seed) + migration ซ่อม Level เดิม หรือเลิกพึ่ง Level → `IsPostable`
5. **C-04 [P1][M]** JE 50 จุดสร้างตรงไม่ผ่าน `JournalEntryBuilder` — 4 จุดพิสูจน์ได้ว่าไม่สมดุลแล้ว Posted (เพิ่มทุนไม่มีบัญชีส่วนเกิน · ขายเงินลงทุน · IntegrationService CN/DN มี WHT) — main agent ยืนยัน `FinancialManagementService.Part2.cs:312-350` · ทางแก้: SaveChanges interceptor ตรวจ Dr=Cr + checker `journal_builder_check`
6. **B-06/B-09/B-10 [P1][S–M]** convert/clone/recurring ไม่สืบทอด `PaymentTerms`/`CreditDays`/`DimensionId` (+clone ไม่ส่ง `BillDiscount*` · recurring THB เท่านั้น) — **ขัดกับ SYSTEM_REVIEW §9 U6** ("เอกสารลูกสืบทอดครบ") ควรถอด U6 ออก · ทางแก้: header-inheritance contract ตัวเดียว + เทสต์ reflection แบบ `OcrScanSnapshot`
7. **B-08 [P1][M]** JE วงจรมัดจำ (Realize/Refund/Apply) ไม่คูณ `ExchangeRate` ขณะ JE ตอนอนุมัติคูณ ⇒ 217xx ค้างถาวรสำหรับมัดจำต่างสกุล
8. **C-05/C-06 [P1][M]** จำหน่ายสินทรัพย์ไม่มี VAT ขาย/ใบกำกับ · ค่าเสื่อม: แผน≠โพสต์ (M+1 vs M) · adjust/revalue ไม่ prospective · `Math.Round` = 0 จุดทั้งไฟล์
9. **C-07 [P1][S]** 50 ทวิ/ไฟล์ ภ.ง.ด.3/53 ใช้ยอดสกุลเอกสาร (grep ExchangeRate ใน WithholdingTaxCertService = 0) ⇒ ใบ USD ขาดยอดบาท
10. **E-06 [P1][M]** consignment ขายได้จริงสร้าง Invoice ไม่มี ProductCode (COGS ไม่ลง) + `VatRate = 0` hard-code + `_db.Documents.Add` เอง
11. **D-02/D-03 [P1][M]** ลบสินค้าที่มีสต๊อกได้ · ลบผู้ติดต่อตรวจแค่ 2/20 ตาราง (500 หรือ cascade ลบ ConsignmentRecord/PaymentReminder/VendorPortalToken เงียบ)
12. **D-05 [P1][S]** ยอดธนาคารคำนวณจาก `BalanceAfter` แถวล่าสุด `?? 0` ⇒ ยอดเปิดบัญชีหายเมื่อลบแถวเดียว
13. **D-06 [P1][S]** import/export ผู้ติดต่อไม่มี `BranchCode` (§86/4) · dedupe TaxId เดี่ยว → 500/merge ผิด
14. **B-03 [P1][S]** Rejected เป็นทางตัน (แก้ได้แต่อนุมัติ/ส่งใหม่ไม่ได้) — เส้นมือถือแก้แล้วให้เด้ง Draft; `SignatureApprovalService.cs:294` ยังตั้ง Rejected
15. **B-07 [P2][M]** เส้นที่ออกเลขเอง (settlement receipt · CN คืนมัดจำ) ไม่ตรึง `IssuerBranchCode`/`TaxPointDate`/`RetentionUntil` — และ **`RetentionUntil = null` = purge ได้โดยไม่มีด่าน** (`PurgeDocumentAsync :7666`) · backfill เดิม `WHERE IS NOT NULL` ทิ้งแถว null
16. **F-04/F-05/F-08 [P1–P2]** Executive Summary นับ Draft/Voided เป็นเกินกำหนด · รายงานเอกสารกรองแค่ `!= Voided && != Draft` (นับ WaitingApproval/Rejected) · BillingNote ถูกนับเป็นลูกหนี้ทั้งที่ไม่มี JE ⇒ AR เอกสาร > GL 11310 เสมอ
17. **MAIN-01 [P2][S]** `CrossTenantWorkflowService.cs:403-415` สร้าง PO เลข `PO-{yyyyMMdd}-{Guid[..6]}` + `Status=Approved` ตรง — นอก series/นอก pipeline (พบระหว่าง verify B-02)

## §2 ต้นเหตุร่วม (defect class) ของรอบนี้ — แก้ที่รากปิดได้หลายข้อ

| ราก | ข้อที่เกิดจากราก | กลไกที่กันตัวที่สาม |
| --- | --- | --- |
| **เขียน `Document.Status` นอก `IDocumentService`** | B-02 ✅ · MAIN-01 · IntegrationService 7 จุด (ถูก — เดิน AutoPost เอง) · PosService:459 (ยังไม่ตรวจ) | checker `document_status_writer_check.py` (allow-list: DocumentService · ApprovalService bounce) |
| **`== DocumentStatus.Approved` เป๊ะ 26 จุด** | A-01 ✅ · A-03 · A-04 ✅ · A-05 · F-04/F-05 | `Helpers/DocumentStatusRules` ✅ (เพิ่ม) · รอบถัดไปไล่ 22 จุดที่เหลือ + checker ฟ้อง literal `== DocumentStatus.Approved` นอก allow-list |
| **ฝั่งอ่านไม่ถูกแก้ตามฝั่งเขียน** (เฟส 0 stock · C-T02 IsClosingEntry) | E-01 ✅ · E-02 · F-01 ✅ · F-06 | `StockMovementSign` ✅ · `MovementTypes` (รอ) · predicate "GL ที่นับเป็นผลการดำเนินงาน" ตัวเดียวแบบ `AiCallBilling.BillableRow` |
| **ชุดชนิดเอกสาร/ฝั่งซื้อ-ขาย เขียนมือ ≥ 25 จุด** (C# 8 map + JS 3 map + 12 select + 9 side-set) | A-07 · A-08 · A-09 · A-10 · A-11 · A-12 · A-13 · F-02 · F-09 | **`DocumentTypeRegistry`** ตัวเดียว (enum → prefix · ชื่อ th/en · side · ต้องมี JE · ทิศสต๊อก · e-Tax code · แปลงไปได้) + `<select>` สร้างจาก `Layout.docTypeLabel` runtime (กลไก `MENU_SECTIONS`) |
| **JE นอก Builder / ไม่มีด่าน Dr=Cr** | C-04 · B-08 · C-06 (rounding) · E-07 (ไม่มี JE) | SaveChanges interceptor + `journal_builder_check` |
| **ด่านครอบทางเดียว** | E-03 (AllowNegativeStock เอกสารข้าม/POS บล็อก) · D-08/D-09 (create vs update) · D-03 (ลบ Contact vs Product) · C-02/C-10 (งวดปิด 3 ไฟล์) | หลังใส่ด่าน `grep` ทุกทางเข้าที่แตะข้อมูลชุดเดียวกัน — บันทึกใน CLAUDE.md แล้ว ต้องทำจริง |
| **สืบทอดเอกสารลูกด้วยรายการมือ** | B-06 · B-09 · B-10 · B-13 | header-inheritance contract + เทสต์ reflection (กลไก `OcrScanSnapshot` deny-list) |
| **PATCH semantics null vs ""** | B-01 ✅ · D-11 · D-10 · SYSTEM_REVIEW D9 | DTO แยก "ไม่ส่ง" จาก "ส่ง null" หรือ helper ฝั่ง JS ตัวเดียว (`_txt`/`_gsel`) ทุกหน้า |

## §3 ผลตรวจรายทีม (สรุป — รายละเอียด/หลักฐาน file:line อยู่ในรายงานเต็ม)

สถานะ: ✅ แก้แล้ว · ✔ verify แล้วยังเปิด · ○ ทีมรายงาน ยังไม่ได้ verify ซ้ำ · ✗ หักล้างแล้ว

### ทีม A — ประเภทเอกสาร (13 ข้อ)
| ID | P | ขนาด | เรื่อง | สถานะ |
| --- | --- | --- | --- | --- |
| A-01 | P1 | S | §65ตรี(4) YTD `== Approved` | ✅ |
| A-02 | P1 | S | portal เห็นทุกสถานะ/ทุกชนิด + PDF | ✅ (สถานะ) · ชนิดฝั่งซื้อรั่วถึง vendor ยังเปิด — ต้องดูใครสร้าง PortalAccess ให้ vendor |
| A-03 | P2 | S | BankAiAugmenter/AdvancedAiAugmenter aging นับเฉพาะ Approved‖PartiallyPaid — Sent/Overdue หลุด | ○ |
| A-04 | P2 | S | DOC_NO_JE false positive | ✅ |
| A-05 | P2 | S | conversion rate ใบเสนอราคา = "ที่อนุมัติ" ไม่ใช่ "ที่ถูกแปลง" (ExecutiveReportService.Sales.cs:42) | ○ |
| A-06 | P1 | M | Receipt standalone ไม่ตัดสต๊อก/COGS | ✅ ตั้งค่าได้ (CashSaleStockPolicy · default ตัดสต๊อก+COGS) |
| A-07 | P2 | S | โมดัลแปลงซ่อน option ที่มี ⇒ PO→GRN แปลงจากจอไม่ได้ (documents.html:1432-1456 static) | ○ — ควรสร้าง option จาก `allowed` ที่ server คืน |
| A-08 | P2 | S | กฎอนุมัติ sme-config เลือกชนิดได้ 5/16 | ○ |
| A-09 | P3 | S | risk.html ส่ง `JournalEntry` เข้า enum DocumentType → 400 | ○ |
| A-10 | P3 | M | ชื่อไทยของชนิด drift 8 map C# + 3 JS (CIL "ใบรับรองแทนใบเสร็จ" vs "…รับเงิน" · Receipt "ใบเสร็จ" ใน CustomerStatementController:191) | ○ |
| A-11 | P3 | S | DocumentStatus filter/ป้ายไม่ครบ 9 (documents.html:213 ไม่มี WaitingApproval/Rejected · :7616 ไม่มี Overdue) | ○ |
| A-12 | P3 | S | portal `#docFilter` 6 ชนิด กรอง DN/BN/RV ไม่ได้ | ○ |
| A-13 | P3 | S | ชุดฝั่งซื้อ/ขายเขียนมือ ≥9 ที่นอก `DocumentSide` (documents.html ไฟล์เดียว 4 ชุดไม่เท่ากัน) | ○ |
| — | — | — | ตรวจแล้วไม่ใช่บั๊ก: prefix 16/16 ตรง · หัวเอกสาร 16 th/en · e-Tax gate · pseudo-type Deposit/TaxInvoiceCombined/TaxInvoicePaid map ก่อนส่ง · DN ไม่ตัดสต๊อก (ตั้งใจ) | |

### ทีม B — วงจรเอกสาร (14 ข้อ)
| ID | P | ขนาด | เรื่อง | สถานะ |
| --- | --- | --- | --- | --- |
| B-02 | P0 | S | mobile approve ข้าม pipeline | ✅ |
| B-01 | P1 | S | ล้างช่องข้อความไม่มีผล | ✅ |
| B-03 | P1 | S | Rejected ทางตัน | ✔ (มือถือแก้แล้ว · SignatureApprovalService:294 ยังตั้ง) |
| B-06 | P1 | S | convert ไม่สืบทอด PaymentTerms/CreditDays/DimensionId/IsForeignService | ○ |
| B-08 | P1 | M | JE มัดจำ realize/refund/apply ไม่คูณ fx | ○ |
| B-09 | P1 | S | clone ไม่คัดลอก BillDiscount/PaymentTerms/CreditDays/DimensionId | ○ |
| B-05 | P2 | S | void ใบซื้อที่มีสินทรัพย์ค่าเสื่อม posted → LogWarning+continue | ○ |
| B-07 | P2 | M | เส้นออกเลขเองไม่ตรึงสาขา/tax point/retention · null retention purge ได้ | ○ |
| B-10 | P2 | M | recurring THB เท่านั้น ไม่มี PaymentTerms/Brand/Dimension | ○ |
| B-11 | P2 | S | Overdue ติดถาวร (ไม่มี path กลับ) · ternary void payment ทับ Overdue เป็น Approved | ○ |
| B-04 | P3 | S | `DocumentStatus.Sent` ไม่มีใครเขียนทั้งเรพ — ต่อสายหรือลบ | ○ |
| B-12 | P3 | S | DOCUMENT_FLOW drift (approve 3 ทาง → 4 ✅ แก้ · line refs เก่า · §3.5 ขั้น asset) | ✅ บางส่วน |
| B-13 | P3 | S | openEdit ไม่ hydrate มัดจำที่ผูก | ○ |
| B-14 | P3 | S | เกณฑ์ "จ่ายครบ" 3 ค่า (0.005/0.01/0) ใน 8 จุด | ○ |
| — | — | — | ตรวจแล้วไม่ใช่บั๊ก: PaidAmount↔BalanceDue recompute ครบ · CN cap ครอบทุกทางเข้า · void cascade 12 ผลข้างเคียง · approve transaction ครอบเลข→JE ไม่เกิด gap | |

### ทีม C — GL/ภาษี/สินทรัพย์ (12 ข้อ)
| ID | P | ขนาด | เรื่อง | สถานะ |
| --- | --- | --- | --- | --- |
| C-01 | P0 | M | DN/CN ของใบบริการหลุด ภ.พ.30 · 21913 ค้าง | ✅ |
| C-02 | P1 | S | reclass ใช้ `ResolveFiscalPeriodAsync` ไม่ใช่ `RequireOpen…` (C-T04 เหลือ) · catch-all · reclass เต็มก้อนตอนรับบางส่วน | ○ (นโยบาย full-on-first-settlement ตั้งใจ — ต้องยืนยันกับนักบัญชี) |
| C-03 | P2 | S | CN/DN ฝั่งซื้อตัดเจ้าหนี้ "212" เสมอ ไม่ใช้ ResolvePayableAccountAsync | ○ |
| C-04 | P1 | M | JE 50 จุดนอก Builder · 4 จุดไม่สมดุลได้จริง | ✔ (ยืนยันตัวอย่าง FMS.Part2:312-350) |
| C-05 | P1 | M | จำหน่ายสินทรัพย์ไม่มี VAT ขาย/ใบกำกับ · Disposed โดยไม่มี JE เมื่อไม่มีบัญชี | ○ |
| C-06 | P1 | M | ค่าเสื่อม: plan≠post · adjust/revalue ไม่ prospective · ไม่ปัดเศษ | ○ |
| C-07 | P1 | S | 50 ทวิ/ภ.ง.ด. เป็นสกุลเอกสาร | ○ |
| C-08 | P2 | S | เลข 50 ทวิ `.Max()+1` ไม่ล็อก (checker ไม่จับทรงนี้) · auto-gen ล้มแล้ว LogWarning | ○ |
| C-09 | P2 | S | job ค่าเสื่อมไม่ไล่เดือนที่หลุด (ไม่มี watermark) | ○ |
| C-10 | P2 | S | FMS 13 JE คืน FiscalPeriodId=null เมื่องวดปิด · RdCompliance เลข `VR-{Guid}` | ○ |
| C-11 | P3 | S | CIL settlement ไม่ cross-rate · `lineProjects.Add` นอก if | ○ |
| C-12 | P2 | ? | สงสัยใบเสร็จหลายใบต่อ INV นับ VAT ซ้ำ | ○ ต้องเปิด Convert |
| — | — | — | ตรวจแล้วดี: AutoPost 5 branch สมดุล (`sim_je_branches.py` 20,000×7) · AwayFromZero ครบ · PV/Receipt Cash-Accrual + FX · JournalPostingGuard ถูกเรียกจริง · 50 ทวิ idempotent/void · dedup ภ.พ.30 · JobLock ค่าเสื่อม | |

### ทีม D — master data (17 ข้อ · ไม่พบ P0)
| ID | P | ขนาด | เรื่อง | สถานะ |
| --- | --- | --- | --- | --- |
| D-01 | P1 | S | products.html update ไม่ส่ง SKU/Unit/TrackStock | ✅ |
| D-02 | P1 | M | ลบสินค้าที่มีสต๊อกได้ | ○ |
| D-03 | P1 | M | ลบผู้ติดต่อ: ด่าน 2/20 ตาราง · 3 entity ไม่ตั้ง OnDelete (cascade) | ○ |
| D-04 | P1 | M | import CoA Level ตายตัว ⇒ 59 จุด `Level >= 4` มองไม่เห็น | ○ (§1 ข้อ 4) |
| D-05 | P1 | S | ยอดธนาคาร = BalanceAfter แถวล่าสุด ?? 0 | ○ |
| D-06 | P1 | S | import/export ผู้ติดต่อไม่มี BranchCode · dedupe TaxId เดี่ยว | ○ |
| D-07 | P2 | S | รหัสสินค้าที่ลบแล้วสร้างใหม่ = 500 (unique index ไม่กรอง IsDeleted) | ○ |
| D-08 | P2 | S | สร้างงวดไม่ตรวจทับซ้อน (update ตรวจ) · Resolve FirstOrDefault | ○ |
| D-09 | P2 | S | update ผู้ติดต่อไม่ตรวจ TaxId ซ้ำ · ไม่มี unique index | ○ |
| D-10 | P2 | S | accounts.html fType/fParent เปิดแก้แต่ไม่ส่ง | ○ |
| D-11 | P2 | S | ล้างวันเครดิตผู้ติดต่อไม่ได้ (sentinel null vs -1) | ○ |
| D-12 | P2 | M | เครดิตเทอมไม่มี resolver กลาง · `DefaultPaymentDueDays` ไม่มี server อ่าน · Integration `AddDays(30)` 4 จุด | ○ |
| D-13 | P2 | S | export CSV ไม่มี BOM | ○ |
| D-14 | P3 | S | ของที่มีแต่ไม่มีใครเรียก: Product.SalesAccountId/PurchaseAccountId (UI ไม่ส่ง · SalesAccountId ไม่มีใครอ่าน) · Warehouse.BranchId · PreventPostToClosedPeriod · Contact.PaymentTerms ไม่มีช่อง | ○ ต้องเลือก ต่อสาย/ลบ |
| D-15 | P3 | S | ภาษาเอกสาร 3 บริการคำนวณเองข้าม template.Language | ○ |
| D-16 | P3 | S | สร้างผู้ติดต่อซ้ำ → คืนรายเดิมแต่ toast "เพิ่มสำเร็จ" | ○ |
| D-17 | P3 | S | payroll ผังไม่มี 541xx → ล้ม "ไม่สมดุล" แทน "ไม่พบผัง" | ○ |
| — | — | — | ตรวจแล้วไม่ใช่บั๊ก: MergeContacts repoint FK ทุกคอลัมน์ · unique index Products/CoA/Branches/Warehouses/FiscalPeriods · Branch delete guard · onboarding seed ผัง + 00000 ที่ server (SYSTEM_REVIEW row 177 ระบุผิดบางส่วน) | |

### ทีม E — สต๊อก/ซื้อ/ขาย/POS (15 ข้อ)
| ID | P | ขนาด | เรื่อง | สถานะ |
| --- | --- | --- | --- | --- |
| E-01 | P0 | S | รายงานสต๊อก In − Out เครื่องหมายผิด | ✅ |
| E-04 | P0 | S | StockTransferController เขียน movement ตรง | ✅ |
| E-05 | P0 | M | PO→GRN และ PO→PI ตรงเปิดคู่กัน | ✅ (ด่าน) |
| E-02 | P1 | M | OPENING/TRANSFER_* ไม่อยู่ใน costing/รายงาน | ○ (§1 ข้อ 3) |
| E-03 | P1 | S | AllowNegativeStock สองมาตรฐาน (เอกสารข้าม · POS บล็อกทั้งบิล · เช็คระดับบริษัทไม่ใช่คลัง) | ○ |
| E-06 | P1 | M | consignment Invoice ไม่มี ProductCode + VAT 0 | ○ |
| E-07 | P1 | M | ปรับสต๊อก/ตรวจนับ/รับโอนขาด ไม่มี JE | ○ (§1 ข้อ 2) |
| E-08 | P1 | S | ADJUST ทิ้งเครื่องหมาย | ✅ |
| E-09 | P1 | M | นำเข้าสต๊อกจาก OCR + สร้าง PI จากสแกนเดียวกัน = เข้า 2 รอบ | ○ ความมั่นใจกลาง |
| E-10 | P2 | S | รับโอนขาด 10 หายเงียบ · รับเกินได้ | ○ |
| E-11 | P2 | S | reconcile สต๊อก↔GL หา prefix `113` (=ลูกหนี้) ⇒ 0 บัญชี ไม่มีวันฟ้อง | ○ |
| E-12 | P2 | S | Product.SalesAccountId/PurchaseAccountId ไม่มีใครอ่าน | ○ (= D-14) |
| E-13 | P2 | S | ลบสินค้าที่มีสต๊อก | ○ (= D-02) |
| E-14 | P3 | S | ขาเข้าต้นทุน 0 (ของแถม) ตกไปใช้ต้นทุนเฉลี่ย | ○ |
| E-15 | P3 | S | RebuildAverageCost SaveChanges กลาง void txn + LogWarning | ○ |
| — | — | — | ตรวจแล้วไม่ใช่บั๊ก: StockLedger lock/WAC/SetAbsolute · ApplyStockMovements void/CN/DN/GRN · POS ผ่าน ledger + BOM 4 เส้น · หัวสลิปผ่าน PosSlipHeader อ่าน ภ.พ.06 · SYSTEM_REVIEW H-A8/H-A9 ปิดแล้วจริงโดยเฟส 0 (ยังไม่ติ๊ก) · H-A7 ยังเปิด | |

### ทีม G — security / tenant / สิทธิ์ (9 ข้อ)
| ID | P | ขนาด | เรื่อง | สถานะ |
| --- | --- | --- | --- | --- |
| G-01 | P1 | S | ลายเซ็น/external approve ไม่มีด่าน Document.Approve · ข้ามขั้นภายใน | ✅ |
| G-02 | P1 | S | รหัสผูก LINE 6 หลัก (ผู้ใช้ 10 นาที · สลิป 24 ชม.) ค้นข้ามทุกบริษัท ไม่มี attempt limit ⇒ oracle ✅/❌ ชนแล้วได้สลิป/ตัวตนคนอื่น (`LineBotService.cs:100-107` · `PayslipLineDeliveryService.cs:84-92`) | ○ **ควรแก้ก่อน** (ตัวนับผิดต่อ lineUserId ใน DB แบบ ChatRateLimiter · scope CompanyId · ลดอายุ 24 ชม.) |
| G-03 | P1 | S | LINE text auto-approve ทุกสมาชิก + catch {} | ✅ |
| G-04 | P2 | S | LINE postback สำเนา role list | ✅ |
| G-05 | P1 | S | มือถืออนุมัติไม่มีด่านสิทธิ์/ไม่ตรวจผู้อนุมัติของขั้น | ✅ (ด่านสิทธิ์) · "ผู้กด = ผู้อนุมัติของขั้นนั้น" ยังไม่ตรวจ (ApprovalStep.Approver) |
| G-06 | P2 | M | ลูกค้าหน้าร้าน CMS ได้ JWT ชนิดเดียวกับผู้ใช้ระบบ (`CmsCustomerService.cs:166`) ⇒ ผ่าน `[Authorize]` ของ 11 controller ที่ไม่มี companyId · endpoint นี้ไม่มีหน้าเว็บไหนเรียก | ○ ต้องเลือก ต่อสาย (scheme/purpose แยก) หรือลบ |
| G-07 | P1 | M | `write_permission_gate_check` เฝ้า 3 ไฟล์ — controller ที่ลง JE/ภาษี/สต๊อกอีก ≥20 ไฟล์ permRefs=0 (50 ทวิ · เช็ค · สินทรัพย์ · เงินทดรอง · recurring · import · warehouse · Bank/Pos เดิม) | ○ เพิ่มเข้า WATCHED ทีละไฟล์ + ใส่ด่าน |
| G-08 | P3 | S | impersonation: `imp_by` ไม่มีใครอ่าน ⇒ PII log เป็นชื่อ Owner · read-only ดูแค่ verb (GET ที่เขียนหลุด) | ○ |
| G-09 | P3 | S | token สาธารณะเก็บ plaintext (PayslipDelivery · QuotationAccept · DeliverySign · lodging) — VendorPortalTokens hash ถูกแล้ว | ○ |
| — | — | — | ตรวจแล้วไม่ใช่บั๊ก: ExternalApprove เป็น POST ใต้ TenantGuard (ไม่ใช่ "GET เปลี่ยนสถานะ") · PublicQuotation/e-sign token 256-bit 30 วัน · PayslipPublic token 256-bit 7 วัน revoke + mask (**SYSTEM_REVIEW §7 เรื่อง tier rate limit ควรถอด**) · LINE webhook HMAC FixedTimeEquals · /api/v1 scope ทุก endpoint · CMS anonymous scope companyId+siteId + sanitizer · file access GUID ไม่มี traversal · LodgingPublic token scope | |

### ทีม H — payroll/HR ↔ GL · โมดูลรอง (17 ข้อ)
| ID | P | ขนาด | เรื่อง | สถานะ |
| --- | --- | --- | --- | --- |
| H-01 | P0 | S | Void รอบที่นำส่ง สปส. แล้ว ⇒ 21815 ติดลบ | ✅ |
| H-02 | P1 | M | นำส่ง ภ.ง.ด.1 ไม่ stamp run · Reopen ไม่รู้ · ไม่มี Reverse · dup-check บล็อกนำส่งเพิ่ม ⇒ 21914 ค้าง W′−W | ○ |
| H-03 | P1 | M | Import: `OtherDeductions += SalaryAdvance` → Cr ค่าใช้จ่ายเงินเดือนแทน Cr 115x ⇒ ลูกหนี้ค้าง + หักซ้ำรอบหน้า · Loan ไม่มีผัง → ลดค่าใช้จ่ายเงียบ | ○ |
| H-04 | P1 | S | `AdvanceRecovered` ไม่ขึ้นสลิป/LINE (พิมพ์ NetPay แต่โอนจริง NetPay − Advance) | ○ |
| H-05 | P1 | M | เงินสดย่อย: Replenish ไม่มี JE · Disburse `new JournalEntry` ตรง (เลข Guid · ไม่ผ่าน Builder/งวดปิด) · ไม่มี void | ○ (C-04 class + E-07 class) |
| H-06 | P1 | M | เช็คเด้ง/ยกเลิก = เปลี่ยน status อย่างเดียว ไม่กลับ Payment/JE/ยอดธนาคาร · MarkCleared ขยับ CurrentBalance ไม่มี JE · เช็คจากฟอร์มเอกสารไม่สร้าง Cheque | ○ |
| H-07 | P1 | S | ทิป POS fallback prefix "216" → **21610 เงินมัดจำรับ** ทุกบริษัทผังมาตรฐาน · TipPayoutService หา "2160"/"1011" ที่ไม่มี (throw เสมอ) และไม่มีใครเรียก · WHT 3% ม.40(2) กับพนักงาน — **ขัด SYSTEM_REVIEW §8 "tip ถูก"** | ✅ บัญชีตั้งค่าได้ + resolver เดียว (POS/TipPayout) · WHT ประเภทเงินได้ + ปุ่มเรียก TipPayout ยังเปิด |
| H-08 | P1 | M | คอมมิชชัน Approve = เปลี่ยน status จบ — ไม่มี PayrollItem/JE/เอกสาร | ○ |
| H-09 | P1 | M | ลาไม่รับค่าจ้างหักเฉพาะ `LeaveType == "UnpaidLeave"` literal — `IsPaid` ที่ผู้ใช้ตั้งไม่ถูกอ่าน | ○ |
| H-10 | P1 | S | SmeOperationsController/ChequeController มีแค่ [Authorize] + หลุด allow-list (= G-07) | ○ |
| H-11 | P2 | M | TimeBilling `new Document` ตรง ข้าม CreateDocumentAsync · Billed ถาวรแม้ลบ Draft | ○ |
| H-12 | P2 | S | 21816 (กท.) / 21818 (PVD) ไม่มี type นำส่ง ⇒ ไม่มีทางล้าง (D-R5 ปิดครึ่ง) | ○ |
| H-13 | P2 | S | แก้ยอดหลัง Approved ไม่ถอยสถานะ ⇒ จ่ายได้โดยไม่ re-approve | ○ อาจเป็น design |
| H-14 | P2 | S | Employee: 14 ฟิลด์ Create-only (ชื่อ/เลขบัตร/DOB…) · Response ขาด 13 ฟิลด์ · employees.html hardcode `socialSecurityNumber: null` — **D-U1 ยังเปิด** | ○ |
| H-15 | P2 | S | (สงสัย) Recurring JE `EntryDate = UtcNow` ตกงวดก่อนถ้า startDate serialize เป็น 17:00 UTC | ○ ต้องเช็ค recurring.html |
| H-16 | P3 | S | ExpenseClaim PV ไม่ส่ง BankAccountId ⇒ GL ลดเงินสดแม้จ่ายโอน · Paid claim void ไม่ได้ | ○ |
| H-17 | P3 | S | เงินทดรอง Disbursed ไม่มีทางออก · ลาออกก่อนหักครบ = 115x ค้าง | ○ |
| — | — | — | ตรวจแล้วไม่ใช่บั๊ก: JE เงินเดือนสมดุลตามพีชคณิต + วินิจฉัยรายคน · Pay/Void/Reopen ล็อกแถว · SalaryAdvance/ExpenseClaim/Lodging/CMS ผ่าน IDocumentService · Recurring ล็อกข้ามเครื่องจริง · Terminate → UserLoginPolicy ครอบ · D-T2 ปิดแล้วจริง | |

### ทีม I — หน้าจอ/เมนู/สถานะข้ามหน้า + ERP gap map (10 ข้อ)
| ID | P | ขนาด | เรื่อง | สถานะ |
| --- | --- | --- | --- | --- |
| I-01 | P1 | S | recurring `Yearly` ≠ enum `Annual` | ✅ |
| I-02 | P1 | S | `API.delete` ไม่มี — ปุ่มลบตาย 5 จุด | ✅ |
| I-03 | P1 | M | POS select ส่งเลข 0–4 แต่ `PosOrderType` เริ่ม WalkIn=1 ⇒ ประเภทออเดอร์เก็บผิดสมาชิก**ทุกแถว** · KDS `Takeaway`≠`TakeAway` · pos-reports map รองรับสองระบบเลข (เคยแก้ที่ป้าย) | ○ ต้อง migration ข้อมูล + `Enum.IsDefined` guard |
| I-04 | P1 | S | select ค่าเสื่อมไม่มี `None` | ✅ |
| I-05 | P2 | S | employees.html: ธนาคาร 3 ช่องไม่ hydrate · `bankAccountName`/`isSubjectToSocialSecurity` ไม่ส่งตอนแก้ · ธง ปกส. รีเซ็ตติ๊กเสมอ (= H-14 คนละมุม) | ○ |
| I-06 | P1 | S | SampleData seed/cleanup ไม่มีด่าน | ✅ (ด่าน Owner) · seed `Status=Approved` ตรงยังเปิด (ราก §2 จุดที่ 4) |
| I-07 | P1 | L | "สิทธิ์ตามเมนู" บังคับที่จอเท่านั้น — `IPermissionService` ใช้ใน 10/~140 controller · middleware ไม่ตรวจ permission (= G-07 ภาพใหญ่) | ○ **ราก ERP ข้อ 7** |
| I-08 | P2 | M | ตาราง enum × หน้า ≥60 map/30+ ไฟล์: ตัวกรองสถานะ 6 หน้ากรองสถานะที่ backend เซ็ตจริงไม่ได้ (`Suggested/Printed/Created/Voided/Submitted`) · ค่าผี `Generated` · PayrollRunStatus/LeaveType/ApprovalStatus/JournalEntryStatus ไม่มี label map เลย | ○ → `GET /api/meta/enums` + `Layout.enumLabel/enumOptions` + checker |
| I-09 | P3 | S | `Layout.statusLabel` ไม่มีจริง (documents.html:8988 dead branch) | ○ |
| I-10 | P3 | S | `typeof this.<เมธอดตัวเอง> === 'function'` 7 จุด — มีจริงทุกตัว (กลิ่น ไม่ใช่บั๊ก) | ○ checker |
| — | — | — | ตรวจแล้วไม่ใช่บั๊ก: ฟอร์ม↔payload 20 หน้าที่ฟ้องเป็นฟอร์มสร้างอย่างเดียว · dead-ref ที่เหลืออยู่ในคอมเมนต์/DOM API/`*-logic.js` · RolePermission/BulkCleanup มีด่าน Owner · delivery-sign/quotation-accept/line-bind ไม่ orphan | |

### ทีม F — รายงาน ↔ สมุดบัญชี (10 ข้อ · **บางส่วน**)
| ID | P | ขนาด | เรื่อง | สถานะ |
| --- | --- | --- | --- | --- |
| F-01 | P1 | S | แดชบอร์ดนับ IsClosingEntry | ✅ |
| F-03 | P1 | S | aging CN/DN ทั้งสองฝั่ง | ✅ |
| F-04 | P1 | S | Executive Summary นับ Draft/Voided เป็นเกินกำหนด · void ไม่ล้าง BalanceDue | ○ |
| F-08 | P1 | M | BillingNote นับเป็นลูกหนี้ใน Dashboard/Aging/recon ทั้งที่ไม่มี JE | ✅ ArApScope (ใบวางบิลออกจากลูกหนี้ทุกจุด) |
| F-02 | P2 | S | AR/AP แดชบอร์ด ≠ aging (ชนิด/สถานะ/CN คนละชุด) | ○ |
| F-05 | P2 | S | รายงานเอกสารกรองแค่ `!= Voided && != Draft` | ○ (ใช้ `DocumentStatusRules`) |
| F-06 | P2 | S | Report Builder แหล่ง JE กรอง Posted เท่านั้น — Reversed หายแต่ใบกลับยังอยู่ | ○ |
| F-09 | P2 | M | นิยาม "ลูกหนี้/เจ้าหนี้ค้าง" ≥5 สำเนา | ○ → `Helpers/ArApScope` |
| F-07 | P3 | S | simple.html ป้าย "เงินเข้า/ออก" แต่แหล่งเป็น P&L คงค้าง | ○ |
| F-10 | P3 | S | การ์ด VAT อ่าน TaxReports ค้างไม่ refresh | ○ |

## §4 สิ่งที่ main agent เปิดไฟล์ยืนยันเอง (ฝ่ายค้าน)
ดู `erp-review/2026-09-05/VERIFY-main.md` — CONFIRMED: C-01 · C-04 (ตัวอย่าง) · A-01 · A-02 · A-06 · B-02 ·
E-01 · E-04 · E-05 · E-08 · F-01 · F-03 · G-01 · G-03 · G-05 · H-01 · I-01 · I-02 · I-04 · I-06 · พบเพิ่ม MAIN-01. **ยังไม่มีข้อไหนถูกหักล้าง** — แต่ P1/P2 ที่เหลือ
(○) ยังไม่ผ่านการ verify ซ้ำ ให้เปิดไฟล์ก่อนลงมือทุกข้อตามกติกา CLAUDE.md

## §5 ข้อแก้ไข SYSTEM_REVIEW_2026-09.md ที่ค้นพบ
- §9 **U6** ("เอกสารลูกสืบทอดครบ") **ไม่จริง** — B-06/B-09/B-10 มีหลักฐาน file:line · ควรถอดออกจาก "ไม่ใช่บั๊ก"
- row 177 (Onboarding) "seed ผังตอนกด ตั้งค่าเสร็จสิ้น · settings ไม่ default 00000" **ไม่ตรงโค้ด** — `CompanyService.CreateAsync:48,90` ทำที่ server ทุกทางเข้า · ส่วน "ไม่มี fiscal period ปีปัจจุบัน" ยังจริง
- **H-A8/H-A9** ปิดแล้วโดยเฟส 0 (ทีม E ยืนยัน) แต่ยังไม่ติ๊ก · **H-A7** (ปิดกะไม่ลง JE เงินขาด/เกิน) ยังเปิด `PosService.cs:169-178`
- **C-T04 ✅** แต่ด่านงวดปิดยังไม่ครอบ 3 ไฟล์ (C-02/C-10) · **T-11 ✅** แต่ backfill `IS NOT NULL` ทิ้งแถว null (B-07)
- **C-R3** `FiscalYear.RangeFor` ยังคำนวณเอง ≥8 จุด (ทีม D ระบุไฟล์)
- §8 "POS tip ลงบัญชีถูก" **ขัดกับ H-07** (fallback prefix 216 → 21610 เงินมัดจำรับ) · **D-U1 ยังเปิด** (H-14) · **D-T2 ปิดแล้วจริง** · **D-R5 ปิดครึ่ง** (21914 ✔ · 21816/21818 ✘ → H-12)

## §6 แผนที่ช่องว่างสู่ ERP (จากข้อเสนอของทีม A–F · ทีม I ที่จะทำตารางเต็มยังไม่ได้รัน)

**รากที่ต้องซ่อมก่อนขยาย** (ถ้าขยายทับ จะได้ ERP ที่ตัวเลขไม่ตรงกันเองโดยโครงสร้าง):
1. **ชั้น posting เดียว** — ทุก JE ผ่าน Builder + interceptor Dr=Cr + ด่านงวดปิด + rounding (ปิด C-04/C-06/B-08/E-07)
2. **DocumentTypeRegistry + DocumentStatusRules** เป็นแหล่งเดียวของ "ชนิด/สถานะแปลว่าอะไร" (ปิดทีม A เกือบทั้งทีม + F-02/F-05/F-09)
3. **Master data governance** — lifecycle เดียว (IsActive ≠ IsDeleted) · ลบได้เฉพาะไม่มีใครอ้าง (helper อ่าน FK จาก information_schema ที่ MergeContacts มีแล้ว) · partial unique index `WHERE IsDeleted=false` · CoA เป็นโครงสร้าง (`IsPostable`) ไม่ใช่ Level + account-role mapping ต่อ tenant (ปิด D-02/03/04/07/09)
4. **Header-inheritance contract + PATCH DTO** (ปิด B-06/09/10/13 · B-01 ✅ · D-10/11)
5. **Inventory เป็น subledger จริง** — MovementTypes enum · ใบปรับปรุงสต๊อกเป็นเอกสาร · costing ต่อคลัง · reconcile สต๊อก↔GL ที่หาบัญชีถูก (E-02/07/10/11) · สถานะ PO/SO Open/PartiallyReceived/Closed · Reservation/ATP (`ReservedQuantity` มีคอลัมน์ไม่มีใครเขียน)
6. **Sub-ledger ↔ GL reconciliation รายวัน** เป็นรายงานมาตรฐาน (AR/AP/Inventory/VAT/มัดจำ) — วันนี้ "สองความจริง" ถูกพบโดยการอ่านโค้ด ไม่ใช่โดยระบบ
7. **สิทธิ์ที่ server ชั้นเดียว** (ทีม I I-07 + ทีม G G-07) — วันนี้ "สิทธิ์ตามเมนู" คือการซ่อนปุ่ม: `IPermissionService` ถูกเรียกใน 10/~140 controller และ middleware ตรวจแค่สมาชิกภาพ ⇒ segregation of duties ไม่มีจริงนอกเอกสาร/เงินเดือน/ค่าใช้จ่าย/ลา/PDPA/metering · ทางแก้: permission ต่อ route (attribute + action filter) + checker เป็น deny-list `[PublicSurface]`
8. **enum → UI จากแหล่งเดียว** (I-08 · A-10/A-11) — `Helpers/EnumLabels` → `GET /api/meta/enums` → `Layout.enumLabel/enumOptions` สร้าง option runtime (กลไก roles.html↔navItems) + checker `enum_option_value_check`
9. **Sales Order** เป็นเอกสารกลาง — ไม่มี SO = `ReservedQuantity`/ATP/backorder/PriceList ไม่มีที่อยู่ (ทีม E+I ชี้ตรงกัน)

**ERP gap map 21 โมดูล** (ทีม I · ตารางเต็ม+ไฟล์อ้างอิงใน `report-I.md` §ERP): ✅ GL · AR · AP · Audit (hash chain) · e-Tax (XAdES จริง) — แต่ทุกตัวมีรากค้าง (C-04 · F-08 · C-07 · I-07) · 🔨 Inventory/WMS (FIFO จริง · ไม่มี bin/lot · ReservedQuantity เขียนที่เดียว=0) · Purchasing (ไม่มีสถานะ PO) · Sales/CRM (**ไม่มี SalesOrder entity** · Lead แค่ CMS) · Manufacturing (ไม่มี WIP · UnitConversion ไม่ถึงบรรทัด) · Fixed Assets (I-04 · C-05/06 · ค่าเสื่อม 2 ชุดบัญชี 📋) · HR/Payroll (ไฟล์โอนเงินเดือน 📋 · ปฏิทินวันหยุด 📋) · Projects · Budgeting · Multi-company (elimination ยังไม่ตรวจ) · Multi-branch (JE ไม่ติด BranchId) · Multi-currency (`JournalLine` ไม่มี ClosingRate/ForeignCurrency/OriginalRate ทั้งที่ CLAUDE.md G บทที่ 19 บังคับ · B-08) · Approval workflow (3 engine คนละกติกา) · Reporting/BI (ReportBuilder 10 endpoint ไม่มีหน้าเรียก) · API/Integration (E-commerce ไม่มี UI · Open Banking "simulate") · POS (I-03 · ภ.พ.06 · สาขา) · Lodging/CMS (ปลายทางตาม LODGING_BOOKING_AUDIT) · SoD (I-07) · 📋 SO · WMS bin/lot · ค่าเผื่อหนี้ · ไฟล์โอนเงินเดือน

**โมดูล ERP ที่ยังไม่มี/มีครึ่งเดียว** (จากที่ทีมพบระหว่างทาง — ทีม I จะทำตารางเต็ม): Sales Order · Return แยกจาก CN · Stock Adjustment/Transfer มีเลขและอนุมัติ · UoM หลายระดับในบรรทัดเอกสาร (UnitConversion มี CRUD แต่ไม่ถูกใช้) · PriceList ต่อสาขา/ลูกค้า · WIP/ค่าแรง/โสหุ้ยในการผลิต · Backorder/Drop-ship/RMA · Depreciation สองชุดบัญชี (TFRS/พ.ร.ฎ.145) · Period-close checklist · Consignment off-balance subledger · หน้า "ยอดค้างรับต่อ PO"

## §7 ตรวจแล้ว **ไม่ใช่บั๊ก** (รอบนี้) — ห้ามรายงานซ้ำ
รวมจาก §"ตรวจแล้วไม่ใช่บั๊ก" ของทุกทีม (ดูรายงานเต็ม) — ที่สำคัญ: prefix 16/16 ตรง · AutoPost 5 branch สมดุล ·
Math.Round AwayFromZero ครบใน DocumentService/TaxService · void cascade ครบ · approve ไม่เกิด gap เลข ·
MergeContacts ครบ · pseudo-type ใน select ไม่ใช่บั๊ก · missing ที่ตั้งใจ (batchConvert ไม่มี Quotation/PR · CN/DN ไม่อยู่ใน convert) ·
IntegrationService `Status = Approved` ใน initializer เดิน AutoPost เอง (ไม่ใช่ B-02 class)

## §8 checker ใหม่ที่ควรมี (ทุกตัวต้องผ่าน negative test)
1. `document_status_writer_check.py` — เขียน `.Status = DocumentStatus.` นอก allow-list (DocumentService · ApprovalService bounce) — จับ B-02/MAIN-01
2. `approved_literal_check.py` — `== DocumentStatus.Approved` นอก allow-list → บังคับ `DocumentStatusRules`
3. `journal_builder_check.py` — `new JournalEntry {` นอก Builder/allow-list — จับ C-04 50 จุด
4. `movement_type_vocab_check.py` — string literal ชนิด movement นอก `MovementTypes`
5. `doc_type_select_check.py` — `<option value="<DocumentType>">` static ในหน้าเว็บ → ต้องสร้างจาก `Layout.docTypeLabel`
6. ขยาย `sequence_lock_check` ให้จับ `.Max() + 1` (C-08) · ✅ ขยาย `stock_writer_check` จับ `Set<StockMovement>()` แล้ว

## §9 รอบถัดไป (ลำดับ)
1. **รันทีม F ซ้ำให้ครบ** (งบการเงิน/ภ.พ.30↔GL/export/scheduled report) · **G-02** (รหัสผูก LINE) + **G-07/I-07** (สิทธิ์ที่ server) เป็นงาน security ชิ้นแรก · **I-03** (POS OrderType migration) ก่อนมีตรรกะเงินบน OrderType
2. ~~ตัดสินใจ A-06 / F-08 / H-07~~ ✅ เจ้าของตัดสินแล้ว (ตั้งค่าได้ · ใบวางบิลออกจากลูกหนี้ · บัญชีทิปตั้งค่าได้) — ทำแล้วในคอมมิตรอบ 136
3. Sprint "ราก": DocumentStatusRules ไล่ 22 จุดที่เหลือ + checker · MovementTypes + E-02 · journal interceptor + C-04
4. D-04 (CoA Level) ก่อนรับลูกค้าที่ import ผังเอง
5. sync SYSTEM_REVIEW ตาม §5

---
_ผลิตโดย 9 subagent (ทีม A–I · F บางส่วน) + main agent verify/แก้ · 2026-09-05 · ไม่ได้คอมไพล์ — env ไม่มี .NET SDK_
