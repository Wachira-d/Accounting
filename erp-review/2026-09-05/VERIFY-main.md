# main agent verification log (เปิดไฟล์จริง)
## ทีม C
- C-01 CONFIRMED — DocumentService.cs:12318-12330 reclass กรอง DocumentType==Invoice + SourceDocumentId==invoiceId เท่านั้น · :13366-13380 DN/CN เลือก vatCode 21913 เมื่อใบเดิมยังพัก และ JE ของ DN มี SourceDocumentId = doc.Id (คนละ id) · OutputVatDueAt ถูกเซ็ตแค่ :12297/:12377 บน inv เท่านั้น · TaxService.cs:734-759 DN ถูก IsExcluded ขณะ inv ยัง undue และ query :194-203 เดือนถัดไปใช้ OutputVatDueAt ของ DN เอง (null) ⇒ DN หลุด ภ.พ.30 ถาวร + 21913 ค้าง
- C-04 (ตัวอย่าง) CONFIRMED — FinancialManagementService.Part2.cs:312-350 `if (entity.SharePremium > 0 && premiumAcc != null)` ⇒ premiumAcc null → Dr PaidAmount ≠ Cr capitalAmount, Status=Posted, ไม่มี guard Dr=Cr; TotalDebit/TotalCredit ถูกเก็บต่างกันเงียบ
## ทีม A
- A-02 CONFIRMED — PortalService.cs:200-262 ทั้ง 3 เมธอดกรองแค่ CompanyId+ContactId (grep DocumentStatus = 0) · portal.html:317-331 ตีป้ายจาก balanceDue อย่างเดียว และแสดงปุ่ม "💳 ชำระ" เมื่อ balanceDue>0 ⇒ ใบร่าง/รออนุมัติ/ตีกลับ = "รอชำระ"+จ่ายได้ · Voided (balanceDue 0?) = "ชำระแล้ว"
- A-01 CONFIRMED — DocumentService.cs:11831-11842 `l.Document.Status == DocumentStatus.Approved` ใน priorEntertainment YTD; PV เป็น Paid ตั้งแต่ approve (:5183-5192 ตามที่ทีมอ้าง — ยังไม่เปิดดูบรรทัดนั้นเอง แต่ทิศทางถูกตามหลัก lifecycle)
- A-06 CONFIRMED — DocumentService.cs:12490-12510 switch ไม่มี Receipt/ReceiptVoucher (`_ => 0`) ขณะ ValidConversions :8841-8848 ให้ Quotation→Receipt และ BillingNote→Receipt ⇒ ขายสินค้าด้วยใบเสร็จ standalone ไม่ตัดสต๊อก/COGS
## ทีม B
- B-02 CONFIRMED — MobileApiService.cs:388-396 `document.Status = DocumentStatus.Approved` ตรง ๆ, grep ApproveDocumentAsync ในไฟล์ = 0 · reject :410-416 เช่นกัน · ApprovalRequest ถูก scope ด้วย companyId (:342-348) ⇒ ไม่ใช่ tenant leak แต่เป็น bypass pipeline ทั้งชุด (เลข/JE/สต๊อก/§86/4/งวดปิด)
- ตรวจเพิ่ม (ทีม B ยังไม่ได้อ่าน): IntegrationService.cs:889 initializer Approved แต่เดิน AutoPost เอง (:47-71 คอมเมนต์/โค้ด) ⇒ ไม่ใช่ B-02 class (แต่ SYSTEM_REVIEW เคยบันทึก 7 ทาง return null ไว้แล้ว) · **CrossTenantWorkflowService.cs:403-415 สร้าง PO `DocumentNumber = $"PO-{yyyyMMdd}-{Guid[..6]}"` + Status=Approved ตรง ๆ ไม่ผ่าน DocumentNumberGenerator/ApproveDocumentAsync** ⇒ finding ใหม่ MAIN-01 [P2][S] (PO ไม่มี JE จึงไม่กระทบ GL แต่เลขนอก series + ไม่มี IssuerBranchCode/สิทธิ์อนุมัติ + ขัด "โมดูลห้ามออกเลขเอง")
## ทีม E
- E-01 CONFIRMED — ProductService.cs:617-618,635 / :883-884 / :898-900 ใช้ `In − Out` กับค่าดิบ ขณะ StockLedger.cs:200 เก็บ `Quantity = delta` (OUT ติดลบ) และ InventoryCostingService.cs:148-156 มีคอมเมนต์ยอมรับว่า OUT เก็บคนละเครื่องหมาย จึงใช้ Σ|Quantity| อยู่แล้ว — รายงานไม่ถูกแก้ตาม
- E-04 CONFIRMED — StockTransferController.cs:111,167,192 `_db.Set<StockMovement>().Add` ไม่แตะ WarehouseStock/CurrentStock · checker regex `\bStockMovements\s*\.\s*Add\b` ไม่จับทรง `Set<StockMovement>()` (negative test: สแกน controller เดิมด้วย checker ใหม่ = 3 จุด)
- E-08 CONFIRMED — ProductService.cs:219 `MovementType == "OUT" ? -Abs : Abs` ⇒ ADJUST −5 → +5
- E-05 CONFIRMED (โครงสร้าง) — ValidConversions :8877-8881 PO→{GRN,PI} · GetFulfillmentAxis :8982-8993 GRN=Delivery, PI=Billing แยกแกน · GetReceivedViaGrnAccrualAccountAsync :11329-11343 คืน null เมื่อ RelatedDocumentId ไม่ใช่ GRN ⇒ PI ที่แปลงจาก PO ตรงเดินเส้น expense+stock ปกติ
## ทีม F (รายงานบางส่วน — agent ถูกตัดกลางคัน)
- F-01 CONFIRMED — grep IsClosingEntry ใน DashboardService.cs = 0 ขณะ YearEndClose ติดธง และ AccountingService P&L กรอง `!j.IsClosingEntry` (C-T02)
- F-03 CONFIRMED — grep CnDnPurchaseSideOverride ใน AgingReportService.cs = 0 · :50-55 ตัดสินฝั่งจากชนิดอย่างเดียว
## ทีม G
- G-01 CONFIRMED — grep RequirePermission|HasPermission|CanApprove ใน SignatureApprovalController.cs + SignatureApprovalService.cs = 0 · ExternalApproveQuotationAsync :321-400 ไม่ตรวจขั้นภายใน Pending · :294 `Status = Rejected`
- G-03 CONFIRMED — LineBotService.cs:262 `try { ApproveDocumentAsync } catch { }` แล้วตอบ ✅ · ไม่มีด่านสิทธิ์ (ต่างจาก postback :596-600)
- G-05 CONFIRMED — MobileApiService.QuickApproveAsync ไม่มี permission check (อ่านตอน verify B-02) · ApprovalRequests.Add มี call site จริง (ApprovalService.cs:233) ⇒ ผลกระทบไม่ใช่ 0
