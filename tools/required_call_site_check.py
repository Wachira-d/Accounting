#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ล็อก "จุดเรียก + วิธีใช้ผล" ของด่านเงิน/ภาษี/สต็อกใน service — ด่านที่มีแต่ไม่ถูกเรียก (หรือถูกเรียกแล้วทิ้งผล) = ไม่มีด่าน

ที่มา (รอบ 193 · ฝ่ายค้าน M2 → หลังฝ่ายค้าน C6)
------------------------------------------------
เทสต์ของรอบ 193 ล็อกแค่ helper (pure) — เรพนี้ไม่มีเทสต์ระดับ service (ไม่มี DbContext ใน Accounting.Tests)
รุ่นแรกของ checker นี้ตรวจแค่ "มีคำนี้ในเมธอดไหม" ⇒ ฝ่ายค้านใส่การถดถอยจริง 7 แบบ จับได้ 1 และฟ้องผิดเมื่อ
ขึ้นบรรทัดใหม่ก่อน `.Decide(` · รุ่นนี้ตรวจ 7 ชนิดด้วย pattern แคบรายเมธอด (ไม่ต้องรู้ชนิด · ไม่ต้องรู้ taint):
  must       — ต้องมีการเรียก/อ้าง (ค้นบนโค้ดที่ตัดคอมเมนต์+สตริงแล้ว · ช่องว่าง/ขึ้นบรรทัดรอบ . ( , ) ไม่มีผล)
  must_re    — regex ที่ต้องพบ (เช่น "ผลต้องถูกใช้" · "throw ต้องยังอยู่หลัง if (!ok)")
  must_lit   — ต้องพบ (ค้นบนโค้ดที่ตัดแค่คอมเมนต์ — ใช้กับค่าคงที่ข้อความ เช่น `e.Status == "Filed"`)
  call_args  — ทุกการเรียก X ต้องส่งอาร์กิวเมนต์ที่มีคำ Y (เช่น ส่ง `lockEvidence` ไม่ใช่ `None`)
  before     — X ต้องมาก่อน Y · หา Y ไม่เจอ = ฟ้อง (ไม่ข้ามเงียบ)
  forbid     — ห้ามมี (ประกอบสูตรเองซ้ำ · ส่งค่าว่างแทนหลักฐาน · เขียน audit นอก chain)

สิ่งที่ checker นี้ **ทำไม่ได้** (เขียนไว้ตรง ๆ — ต้องพึ่งเทสต์/compiler/คนตรวจ):
  • ความถูกต้องของค่า (เช่น ส่ง `lockEvidence` ของรอบอื่น · กรองสถานะผิดตัว) · ลำดับที่ขึ้นกับ control flow
    (เรียกใน branch ที่ไม่เคยวิ่ง) · ผลลัพธ์ที่ถูกใช้แล้วทับทีหลัง · เมธอดที่ถูกเปลี่ยนชื่อ (ฟ้องว่า "ไม่พบเมธอด" แทน)

negative test รันทุกครั้งที่รัน checker: (ก) กลายพันธุ์อัตโนมัติต่อชนิดกติกา (ข) การถดถอยจริงที่ฝ่ายค้านลอง
(M2–M8) ต้องถูกจับ และรูปแบบโค้ดที่ถูกต้อง (FP1 ขึ้นบรรทัดก่อน `.Decide(`) ต้องไม่ถูกฟ้อง
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Accounting"

PAYROLL = "Services/Implementations/PayrollService.cs"
POS = "Services/Implementations/PosService.Orders.cs"
BOT = "Services/Implementations/BotExchangeRateService.cs"
COMMISSION = "Services/Implementations/CommissionService.cs"
PACKAGES = "Services/Implementations/PosService.Packages.cs"

# รอบ 193 ทีม O1 (ฝ่ายค้าน C10): เทสต์ของ O1 ล็อกแค่ helper — ล็อกจุดเรียกใน service ด้วย
DOC = "Services/Implementations/DocumentService.cs"
OCR = "Services/Implementations/OcrService.cs"
ETAX = "Services/Implementations/EtaxInvoiceService.cs"
APPROVAL = "Services/Implementations/ApprovalService.cs"
SIGN = "Services/Implementations/SignatureApprovalService.cs"
MOBILE = "Services/Implementations/MobileApiService.cs"
APIV1 = "Controllers/V1/DocumentsV1Controller.cs"
CLONE = "Controllers/DocumentCloneController.cs"

INTEG = "Services/Implementations/IntegrationService.cs"
IMPORT = "Services/Implementations/ImportExportService.cs"
DOCS_V1 = "Controllers/V1/DocumentsV1Controller.cs"
CONTACTS_V1 = "Controllers/V1/ContactsV1Controller.cs"
CMS_CUST = "Services/Implementations/CmsCustomerService.cs"
CMS_LEAD = "Services/Implementations/CmsLeadService.cs"
PLATFORM = "Services/Implementations/PlatformBillingDocumentIssuer.cs"
XTENANT = "Services/Implementations/CrossTenantWorkflowService.cs"
DUPDET = "Services/Implementations/Import/DuplicateDetector.cs"
LODGING_LIFE = "Services/Implementations/Lodging/LodgingService.Lifecycle.cs"
LODGING_RES = "Services/Implementations/Lodging/LodgingService.Reservations.cs"
LODGING = "Services/Implementations/Lodging/LodgingService.cs"
DOCSVC = "Services/Implementations/DocumentService.cs"
INTEGRATION = "Services/Implementations/IntegrationService.cs"

SSO_INLINE = ["SsoWageBase.GrossWage(", "SsoWageBase.PeriodBase(", "SsoWageBase.Clamp(", "SsoWageBase.SalaryPaidThisPeriod("]

RULES = [
    dict(file=PAYROLL, method="CalculatePayrollAsync",
         must=["LoadRecalculateLockEvidenceAsync(", "SsoWageBase.ForPeriod("],
         must_re=[r"if\s*\(\s*!\s*canRecalc\s*\)\s*throw\b"],
         call_args=[("PayrollRunEditPolicy.CanRecalculate(", "lockEvidence")],
         before=[("PayrollRunEditPolicy.CanRecalculate(", "RemoveRange(run.Details)")],
         forbid=SSO_INLINE + ["PayrollRunLockEvidence.None"],
         why="#35 คำนวณใหม่ต้องผ่านตัวตัดสินเดียวพร้อมหลักฐานจริงก่อนลบแถวเดิม · ฐาน ปกส. ประกอบที่ SsoWageBase.ForPeriod ตัวเดียว"),
    dict(file=PAYROLL, method="UpdatePayrollDetailAsync",
         must=["LoadRecalculateLockEvidenceAsync("],
         must_re=[r"if\s*\(\s*!\s*canEditAmt\s*\)\s*throw\b"],
         call_args=[("PayrollRunEditPolicy.CanEditAmounts(", "editEvidence")],
         forbid=["PayrollRunLockEvidence.None"],
         why="#35/C3 ✏️ แก้ยอดรายคนต้องถูกล็อกด้วยหลักฐานชุดเดียวกับคำนวณใหม่"),
    dict(file=PAYROLL, method="MapToPayrollRunResponse",
         call_args=[("PayrollRunEditPolicy.CanRecalculate(", "lockEvidence"),
                    ("PayrollRunEditPolicy.CanEditAmounts(", "lockEvidence")],
         forbid=["PayrollRunLockEvidence.None"],
         why="ปุ่มบนจอต้องตัดสินด้วยหลักฐานชุดเดียวกับด่าน"),
    dict(file=PAYROLL, method="LoadRecalculateLockEvidenceAsync",
         must=["TaxCalendarEvents", "ComplianceFilings", "TaxReports", "EFilingExports", "EmployeeProjectTimes",
               "SsoSettledAt.HasValue", "PayrollRunLockEvidence.From(", "PayrollFilingSource.TaxCalendar"],
         must_lit=['e.Status == "Filed"', 'f.Status == "Filed"'],
         forbid=["StatutoryRemittances"],
         why="หลักฐาน 'ยื่นแล้ว' ต้องอ่านปฏิทินภาษี (ทางเดียวบนจอ · C1) · 'นำส่งแล้ว' ผูกกับรอบ ไม่ใช่แถวนำส่งรายเดือน (C2)"),
    dict(file=PAYROLL, method="IssueMonthlyPnd1CertsAsync",
         must=["EmployeeTaxIdentity.Resolve(emp.TaxId, emp.CitizenId)", "payeeTaxId == null"],
         before=[("EmployeeTaxIdentity.Resolve(", "payeeTaxId == null")],
         forbid=["string.IsNullOrWhiteSpace(emp.TaxId)", "string.IsNullOrEmpty(emp.TaxId)", "emp.TaxId == null"],
         why="D-01 ด่าน 50 ทวิรายเดือนต้องใช้ resolver กลาง (TaxId → เลขบัตร) ไม่ใช่ emp.TaxId เดี่ยว ๆ"),
    dict(file=POS, method="CompleteOrderAsync",
         call_args=[("CreateSalesJournalEntryAsync(", "saleCogs")],
         before=[("DeductSaleStockAsync(", "CreateSalesJournalEntryAsync(")],
         why="E-01 ตัดสต็อกก่อน JE — COGS ของ JE มาจากต้นทุนที่ ledger ตัดจริง"),
    dict(file=POS, method="SyncOfflineOrderAsync",
         call_args=[("CreateSalesJournalEntryAsync(", "saleCogs")],
         before=[("DeductSaleStockAsync(", "CreateSalesJournalEntryAsync(")],
         why="E-01 เส้นออฟไลน์ต้องตัดสต็อกก่อน JE เหมือนเส้นออนไลน์"),
    dict(file=POS, method="VoidOrderAsync",
         must=["PosVoidSaleJournal.Decide(", "LoadSaleUnitCostsAsync(", "AddChainedAuditLog("],
         must_re=[r"journalsToReverse\s*\.\s*Add\s*\(\s*\(\s*saleJe\s*\.\s*ReverseEntryId"],
         call_args=[("ApplyRecipeConsumptionAsync(", "saleUnitCosts")],
         before=[("PosVoidSaleJournal.Decide(", "ReverseJournalEntryAsync(")],
         forbid=["journalsToReverse.Add((order.JournalEntryId", "AuditLogs.Add("],
         why="ยกเลิกบิล: กลับ JE ใบที่ตัวตัดสินเลือก (ห้ามกลับซ้ำ) · audit อยู่ใน hash chain · คืนวัตถุดิบด้วยต้นทุนวันขาย"),
    dict(file=POS, method="RefundOrderAsync",
         must=["PosVoidSaleJournal.RefundBlockMessage(", "LoadSaleUnitCostsAsync(", "PosCogsBooking.RefundCogs("],
         call_args=[("ApplyRecipeConsumptionAsync(", "saleUnitCosts")],
         before=[("PosVoidSaleJournal.RefundBlockMessage(", "CreateRefundJournalEntryAsync(")],
         why="คืนเงิน: ห้ามลง JE คืนเมื่อ JE ขายถูกกลับแล้ว · วัตถุดิบกลับด้วยต้นทุน ณ วันขาย"),
    dict(file=POS, method="ApplyRecipeConsumptionAsync",
         must=["UnitCostOverride: restockCost"],
         must_re=[r"return\s*\(\s*true\s*,\s*[\w.]*RecipeCost\s*\(\s*moved\s*\)\s*\)"],
         why="COGS สูตรนับเฉพาะวัตถุดิบ TrackStock (ต้องคืนผลของ RecipeCost ไม่ใช่ผลรวมเอง) · ขาคืนใช้ต้นทุน ณ วันขาย"),
    dict(file=POS, method="LoadJournalChainAsync",
         must_re=[r"!\s*j\s*\.\s*IsDeleted", r"j\s*\.\s*CompanyId\s*==\s*companyId"],
         why="สาย JE ต้องไม่นับ JE ที่ถูกลบ (P4) และกรองบริษัททุกขั้น"),
    dict(file=POS, method="GetCommissionDetailsAsync",
         must=["ServiceCommissionTypeReview.Judge("],
         why="C5 ธงคอมมิชชันกลับด้าน ณ จุดที่เงินไหล"),
    dict(file=POS, method="GetCommissionSummariesAsync",
         must=["ServiceCommissionTypeReview.Judge("],
         why="C5 ธงคอมมิชชันกลับด้าน ณ จุดที่เงินไหล"),
    dict(file=PACKAGES, method="UpdateComponentAsync",
         must=["ServiceCommissionTypeReview.EnsureDefined("],
         must_re=[r"if\s*\(\s*request\s*\.\s*CommissionTypeConfirmed\s*==\s*true\s*\)\s*comp\s*\.\s*CommissionTypeConfirmedAt\s*="],
         why="P6 ยืนยันประเภทคอมมิชชันได้เฉพาะเมื่อฟอร์มใหม่ส่ง commissionTypeConfirmed=true (หน้าเก่าที่แคชห้ามลบป้าย)"),
    dict(file=PACKAGES, method="AddComponentAsync",
         must=["ServiceCommissionTypeReview.EnsureDefined("],
         must_re=[r"CommissionTypeConfirmedAt\s*=\s*request\s*\.\s*CommissionTypeConfirmed\s*\?"],
         why="P6 ยืนยันประเภทคอมมิชชันได้เฉพาะเมื่อผู้เรียกยืนยัน"),
    dict(file=BOT, method="SyncRatesToCompanyAsync",
         must=["CurrencyRateSync.Decide("],
         why="sync ธปท. ต้องเขียนทับแถวอัตราที่ใช้ไม่ได้ (ไม่ข้ามเพราะ 'มีแถวแล้ว')"),
    dict(file=COMMISSION, method="UpdatePlanAsync",
         before=[("CommissionPlanRules.IsDeactivateOnly(", "CommissionPlanRules.Validate(")],
         why="ปิดใช้งานแผนเก่าต้องไม่ถูกบังคับให้แก้อัตรา"),
]

# ── กติกาทรง tuple (ทีม C3 · O1 · L2 ฯลฯ) — `(ไฟล์, เมธอด, must[], before[(a, b)], forbid[], เหตุผล)` ──────────────
# คงทรงเดิมไว้ให้ทีมอื่นเพิ่มต่อได้โดยไม่ต้องรู้ทรง dict · แปลงเป็นชนิด must/before/forbid ความหมายเดิมข้างล่าง
# (ต่างจากเดิม 2 จุดที่เข้มขึ้น: ค้นแบบไม่สนช่องว่าง/ขึ้นบรรทัด · `before` ที่หา b ไม่เจอ = ฟ้อง ไม่ข้ามเงียบ)
# กติกาของทีม M2 ที่เคยอยู่ในรูปนี้ ถูกแทนด้วยทรง dict ใน RULES ข้างบน (รอบหลังฝ่ายค้าน C6)
TUPLE_RULES = [
    # ── รอบ 193 ทีม C3 หลังฝ่ายค้าน: คีย์เลขภาษี + สาขา (คำตัดสินเจ้าของข้อ 20) — ฝ่ายค้านถอดการแก้ออกจาก service แล้ว
    #    ContactTaxBranchKeyTests ยังเขียว ⇒ ล็อกจุดเรียกของตัวจับคู่กลาง/SoftScope/ด่านเขียนทับในแต่ละทางเข้า ──
    (INTEG, "ProcessCustomerAsync",
     ["companyId, request.TaxId, request.BranchCode)", "ContactTaxBranchKey.SoftScope(",
      "taxKey.MayOverwriteBranch", "ContactTaxBranchKey.MayWriteTaxId("],
     [("ContactTaxBranchKey.FindAsync(", "ContactTaxBranchKey.SoftScope(")], [],
     "integration ลูกค้า: หาเลข+สาขาของ payload · ถอยไปชื่อบนชุด SoftScope เท่านั้น · ห้ามเขียนสาขา/เลขภาษีทับแถวที่ไม่ตรง (C-6)"),
    (INTEG, "ResolveContactAsync",
     ["ContactTaxBranchKey.FindAsync(", "ContactTaxBranchKey.SoftScope("], [], [],
     "integration ใบขาย: ชื่อตรงห้ามได้นิติบุคคลอื่นที่ถือเลขอื่น (C-6)"),
    (INTEG, "ResolveSupplierAsync",
     ["ContactTaxBranchKey.FindAsync(", "ContactTaxBranchKey.SoftScope("], [], [],
     "integration ผู้ขาย: ชื่อตรงห้ามได้นิติบุคคลอื่นที่ถือเลขอื่น (C-6)"),
    (IMPORT, "ImportContactAsync",
     ["ContactTaxBranchKey.FindAsync(", "ContactTaxBranchKey.SoftScope(", "taxKey.MayOverwriteBranch"], [], [],
     "นำเข้าผู้ติดต่อ: อีเมลบนชุด SoftScope · รหัสสาขาในไฟล์เขียนทับได้เฉพาะแถวที่ตรงสาขาแล้ว"),
    (DOCS_V1, "Create",
     ["TryNormalize(req.ContactBranchCode", "TaxInvoiceCompletenessChecker.MissingBuyerFields("], [], [],
     "API v1: รหัสสาขาผิดรูป = 400 · บอกช่องผู้ซื้อที่ขาดตาม §86/4 (เช่นที่อยู่ของแถวสาขาใหม่ — P-5)"),
    (DOCS_V1, "ResolveContactAsync",
     ["taxId, req.ContactBranchCode, ct)", "branchCode: req.ContactBranchCode", "ContactTaxBranchKey.SoftScope("], [], [],
     "API v1: สาขาของ payload ต้องไปถึงการหา + การสร้าง (เดิมส่ง null ⇒ ใบกำกับออกในนาม สนญ.) · ชื่อบนชุด SoftScope"),
    (CONTACTS_V1, "SyncCoreAsync",
     ["ContactTaxBranchKey.Pick(", "TaxBranchCode.Normalize(item.BranchCode"], [], [],
     "sync: ด่าน CONTACT-TAXID-OWNED ต้องเทียบเลข + สาขา (เดิมบล็อกสาขาของนิติบุคคลเดียวกัน)"),
    (CMS_CUST, "AutoLinkToErpContactAsync",
     ["customer.TaxId, customer.BranchCode)", "ContactTaxBranchKey.SoftScope("], [], [],
     "CMS: ลูกค้าเว็บสาขา 8 ห้ามผูกผู้ติดต่อ สนญ. · อีเมลบนชุด SoftScope"),
    (CMS_LEAD, "EnsureContactLinkedAsync",
     ["ContactTaxBranchKey.FindAsync(", "ContactTaxBranchKey.SoftScope("], [], [],
     "CMS lead: เลขภาษีผ่านตัวจับคู่กลาง · อีเมลบนชุด SoftScope (C-6)"),
    (PLATFORM, "EnsureContactAsync",
     ["ContactTaxBranchKey.HasTaxId(buyer.TaxId)", "taxId, buyerBranch)", "ContactTaxBranchKey.SoftScope("], [], [],
     "ใบค่าบริการแพลตฟอร์ม: \"-\" ของบริษัทที่สมัครใหม่ไม่ใช่เลข (C-8) · เลข + สาขาของลูกค้า"),
    (XTENANT, "FindPartnerContactAsync",
     ["ContactTaxBranchKey.HasTaxId(partner.TaxId)", "ContactTaxBranchKey.FindAsync(", "partner.BranchCode"], [], [],
     "ข้ามบริษัท: \"-\" ไม่ใช่เลข (C-8) · เลข + สาขาของบริษัทคู่ค้า"),
    (DUPDET, "DetectContactAsync",
     ["ContactTaxBranchKey.FindAsync(", "ContactTaxBranchKey.SoftScope("], [], [],
     "ตัวตรวจซ้ำตอนนำเข้า: สาขาอื่นของเลขเดียวกันไม่ใช่ 'ซ้ำ' · ชื่อบนชุด SoftScope"),
    # ── รอบ 193 ทีม O1: ส่วนต่างยอดชำระ (ข้อ 1/3/4) · ผลต่างปัดเศษ (ข้อ 8) · [Σ-GAP] (ข้อ 12) · e-Tax (ข้อ 10/C4) ──
    (DOC, "CreatePaymentAsync",
     ["PaymentSettlementAdjustment.Check(", "SettlementAdjustmentAmount = settleNet", "request.Amount + paymentFee + settleNet"],
     [("PaymentSettlementAdjustment.Check(", "_db.Payments.Add(")], [],
     "ข้อ 1/3: บรรทัดปรับต้องผ่านตัวตรวจกลางก่อนบันทึก · หนี้ที่ปิดด้วยบรรทัดปรับต้องนับเข้า PaidAmount"),
    (DOC, "CreatePaymentJournalAsync",
     ["payment.SettlementAdjustmentAmount", "PaymentSettlementAdjustment.JournalSide("], [], [],
     "ข้อ 1: JE การชำระต้องตัดเจ้าหนี้เต็มยอดที่ปิด + ลงขาบรรทัดปรับ (51120/51150)"),
    (DOC, "ReversePaymentInternalAsync",
     ["payment.SettlementAdjustmentAmount"], [], [],
     "ยกเลิกการชำระต้องคืนหนี้ที่ปิดด้วยบรรทัดปรับด้วย"),
    (DOC, "AutoPostToJournalAsync",
     ["PaymentSettlementAdjustment.ActualPaidNotApplicableReason(", "PaymentSettlementAdjustment.DocumentCashDelta(",
      "PaymentSettlementAdjustment.CheckDocumentLines(", "DocumentRounding.JournalLine("],
     [("PaymentSettlementAdjustment.DocumentCashDelta(", "PaymentSettlementAdjustment.CheckDocumentLines(")], [],
     "ข้อ 1/4/8 + C1: ยอดชำระจริงของใบสำคัญจ่าย (รวมใบที่แปลงจากใบตั้งหนี้) ต้องปรับขาเงินสด · ช่องที่ใช้ไม่ได้ต้องบอก · ผลต่างปัดเศษลง 54960"),
    (DOC, "CreateDocumentAsync",
     ["DocumentRounding.Validate(", "subTotal + doc.RoundingAdjustment", "PaymentSettlementAdjustment.ActualPaidNotApplicableReason("],
     [], [], "ข้อ 8: SubTotal = Σ บรรทัด + ผลต่างปัดเศษ · C1: ยอดชำระจริงที่ไม่มีผลต้องบอกผู้ใช้"),
    (DOC, "UpdateDocumentAsync",
     ["DocumentRounding.Validate(", "subTotal + doc.RoundingAdjustment", "PaymentSettlementAdjustment.ActualPaidNotApplicableReason("],
     [], [], "ข้อ 8 · C1 เส้นแก้ไขต้องเดินด่านเดียวกับเส้นสร้าง"),
    (DOC, "ConvertCoreAsync",
     ["DocumentRounding.Inherit("], [], [],
     "C2: เอกสารลูกที่ยกทุกบรรทัดต้องสืบทอดผลต่างปัดเศษ (ไม่งั้น 5,024.00 → 5,024.01)"),
    (DOC, "CollectApprovalWarningsAsync",
     ["OcrApprovalGapWarning.Build("], [], [],
     "ข้อ 12: [Σ-GAP] ต้องเป็นคำเตือนตอนอนุมัติด้วยมือ"),
    (DOC, "PreviewApprovalWarningsAsync",
     ["CollectApprovalWarningsAsync("], [], ["SuggestApprovalWarningFixesBulkAsync("],
     "C5/C6: พรีวิวคำเตือนใช้ตัวรวบรวมเดียวกับด่านอนุมัติ และห้ามเรียก AI"),
    (CLONE, "Clone",
     ["DocumentRounding.Inherit("], [], [],
     "C2: โคลนต้องสืบทอดผลต่างปัดเศษ"),
    (OCR, "BuildScanLinesAsync",
     ["DocumentRounding.FromPrintedLine(", "DocumentRounding.CapShifts(", "document.RoundingAdjustment = -lineRoundingShift",
      "OcrTotalDecomposer.NoItemsNote("],
     [], [], "ข้อ 5/8: บรรทัด = จำนวน × ราคา · ผลต่างไปหัวเอกสาร · ใบไม่มีรายการต้องเตือนเรื่องสต็อก"),
    (OCR, "PreviewDocumentLinesAsync",
     ["RoundingAdjustment: document.RoundingAdjustment"], [], [],
     "C3: เส้น 'แก้ในฟอร์มก่อน' ต้องได้ผลต่างปัดเศษชุดเดียวกับเส้นสร้างเอกสาร"),
    (OCR, "RepopulateDocumentLinesFromScanAsync",
     ["subTotal + document.RoundingAdjustment"], [], [],
     "ข้อ 8: เส้น repopulate ใช้สัญญา SubTotal เดียวกัน"),
    (OCR, "CreateDocumentFromScanCoreAsync",
     ["ApplyScanSettlementPlanAsync("], [("ApplyScanSettlementPlanAsync(", "_db.Documents.Add(document)")], [],
     "ข้อ 1/C8: ข้อเสนอบรรทัดปรับ/แท็กรอลงที่ขั้นชำระ ต้องลงก่อนบันทึกเอกสาร"),
    (OCR, "ApplyScanSettlementPlanAsync",
     ["OcrSettlementProposal.DeferredNote(", "PaymentSettlementAdjustment.PostsCashAtApproval(", "PaymentSettlementAdjustment.MatchTolerance"],
     [], ["OcrPaperAmounts.ExactTol"],
     "C8: ใบตั้งหนี้ต้องได้แท็ก [PAY-AT-PAYMENT] · P3: ค่าเผื่อตัวเดียวกับ AutoPost"),
    (OCR, "ScanAsync",
     ["OcrBuyerOnPaper.JudgeClaim("], [], [],
     "ข้อ 9: ใบกำกับเต็มรูปที่ไม่มีผู้ซื้อบนกระดาษ ⇒ §82/5(1)"),
    (ETAX, "BuildEtaxXml",
     ["DocumentRounding.EtaxSummation("], [], [],
     "C4: LineTotalAmount = Σ NetLineTotalAmount · ผลต่างปัดเศษเป็นส่วนลด/ค่าบริการระดับเอกสาร"),
    (APPROVAL, "SubmitActionAsync",
     ["PreviewApprovalWarningsAsync("], [("PreviewApprovalWarningsAsync(", "action.Status = actionRequest.Status")], [],
     "C5: ขั้นสุดท้ายของ workflow ต้องหยุดให้คนเห็นคำเตือนก่อนบันทึกผลอนุมัติ"),
    (APPROVAL, "TryFinalizeApprovedEntityAsync",
     ["actionRequest.AcknowledgeWarnings"], [], ["acknowledgeWarnings: true"],
     "C5: ห้าม workflow ประทับ 'รับทราบคำเตือน' แทนคน"),
    (SIGN, "ExternalApproveQuotationAsync",
     ["ApprovalAckSource.SystemWorkflow"], [], ["acknowledgeWarnings: true"],
     "C5: ผู้เซ็นภายนอกไม่เห็นคำเตือน ⇒ ร่องรอยต้องบอกว่าระบบส่งผ่าน"),
    (SIGN, "CheckAllApprovedAndProcessAsync",
     ["ApprovalAckSource.SystemWorkflow"], [], ["acknowledgeWarnings: true"],
     "C5: ลายเซ็นครบ ≠ มีคนรับทราบคำเตือน"),
    (MOBILE, "QuickApproveAsync",
     ["PreviewApprovalWarningsAsync("], [("PreviewApprovalWarningsAsync(", "_db.ApprovalActions.Add(")], ["acknowledgeWarnings: true"],
     "C5: มือถือหยุดให้กดรับทราบคำเตือนทุกชุด (เหมือนเว็บ) ก่อนบันทึกผล"),
    (APIV1, "Approve",
     ["PreviewApprovalWarningsAsync(", "ApprovalAckSource.ApiClient", "withAiHints: false"], [], [],
     "C6: API ห้ามเรียก AI เสริมคำเตือนแล้วโยนทิ้ง · [Σ-GAP] ไม่ขัดจังหวะ API"),
    # ── รอบ 193 ทีม L2 หลังฝ่ายค้าน (review193-L2.md §D: เทสต์เรียกแค่ helper — ถอดการแก้ใน service แล้วยังเขียว) ──
    (LODGING_LIFE, "CheckOutAsync",
     ["LoadDepositSnapshotsAsync(", "LodgingDepositSettlement.PlanCheckout(", "DocumentService.PreviewTotals(",
      "LodgingPricingEngine.ChargeVatRate(", "ResumeCheckOutAsync(", "SettleCheckOutAsync(",
      "BillDiscountAmount: depositPlan.BaseDeducted"],
     [("LodgingDepositSettlement.PlanCheckout(", "_docService.CreateDocumentAsync("),
      ("FindOrCreateContactAsync(", "r.Charges.Add("),
      ("_docService.ApproveDocumentAsync(", "r.Charges.Add(")],
     ["DepositAppliedDrivesJournal", "r.FinalDocumentId != null"],
     "C2/C5 วางแผนใช้มัดจำ (มัดจำเกินยอด = ค้างคืน) ก่อนออกเลขใบ · ด่าน/สร้างผู้ติดต่อก่อนผูกค่าเสียหาย · "
     "ใบเครดิตห้ามใช้ธงขับ JE (P0-1) · ออกใบแล้วกดซ้ำ = ทำต่อ ไม่ throw"),
    (LODGING_LIFE, "SettleCheckOutAsync",
     ["new RealizeDepositRequest(d.Base, DateTime.UtcNow, prop.RoomRevenueAccountCode, finalId)",
      "Math.Min(a.Gross, finalDoc.BalanceDue)", "r.RefundAmount = plan.ExcessGross"], [], [],
     "C4 รับรู้มัดจำต้องผูกใบสุดท้าย (void กลับได้) · ตัดชำระไม่เกินยอดใบ · ส่วนเกินเป็นยอดค้างคืน"),
    (LODGING_LIFE, "ResumeCheckOutAsync",
     ["DepositRealizedForDocumentId == finalId", "LodgingDepositSettlement.PlanCheckout("], [], [],
     "ทำเช็คเอาต์ต่อ: นับที่รับรู้เพื่อใบนี้ไปแล้ว ไม่ใช้มัดจำซ้ำ"),
    (LODGING_LIFE, "CancelCoreAsync",
     ["Terminal.Contains(r.Status)", "LodgingDepositSettlement.PlanCancellation("],
     [("Terminal.Contains(r.Status)", "LodgingDepositSettlement.PlanCancellation("),
      ("_db.SaveChangesAsync(", "RealizeDepositAsync(")],
     ["RefundDepositAsync("],
     "C3 ด่านสถานะอยู่ที่ตัวกลาง (เส้นแขกยกเลิกซ้ำได้) · บันทึกสถานะก่อนลงบัญชีส่วนริบ · ยกเลิกห้ามลงคืนเงิน (F-03)"),
    (LODGING_LIFE, "RecordRefundPaidAsync",
     ["JobLock.RunExclusiveAsync(", "RecordRefundPaidCoreAsync("], [], [],
     "คืนเงินต้องล็อกระดับการจอง (สองคำขอพร้อมกัน = lost update)"),
    (LODGING_LIFE, "RecordRefundPaidCoreAsync",
     ["LodgingDepositSettlement.IsLegacyRefund(", "TaxFilingLockPolicy.DeclaredOrFiledStatuses",
      "LodgingDepositSettlement.AllocateRefund(", "SyncRefundPaidFromDeposits("],
     [("TaxFilingLockPolicy.DeclaredOrFiledStatuses", "RefundDepositAsync(")], [],
     "C10 แถว legacy ห้ามลงคืนซ้ำ · ห้ามลงวันที่ย้อนเข้างวด ภ.พ.30 ที่ยื่นแล้ว · ยอดคืนแล้วตามใบมัดจำ"),
    (LODGING_LIFE, "BuildChargeAsync",
     ["LodgingPricingEngine.ChargeVatRate("], [], ["request.VatRate ??"],
     "C8 อัตรา VAT รายการ folio ต้องผ่านด่าน §90/2"),
    (LODGING_LIFE, "ConfirmAsync",
     ["LodgingDepositSettlement.StatusAfterDeposit(", "request.ConfirmReservation"], [], [],
     "S-06/C9 ปุ่มรับชำระเพิ่มไม่ใช่การยืนยัน — ตามค่าตั้ง AutoConfirmOnDeposit"),
    (LODGING_RES, "FindOrCreateContactAsync",
     ["ContactTaxBranchKey.SoftMatchScope(", "LodgingGuestContact.SoftCandidateAcceptable("], [],
     ["softScope.FirstOrDefaultAsync("],
     "C-7 แขกนิติบุคคลห้ามได้แถวบุคคลธรรมดาที่อีเมล/เบอร์ตรง (§86/4 ผู้ซื้อผิดตัว)"),
    (LODGING, "EffectiveVatRateAsync",
     ["CompanyVatStatus.ProfileAsync(", "LodgingPricingEngine.PropertyVatRate("], [], [],
     "S-10 อัตรา VAT ที่พักผ่านตัวอ่านสถานะ VAT ตัวเดียว"),
    (DOCSVC, "GuardDrivesGrossApplyAsync",
     ["DepositPolicyResolver.DrivesGrossApply(", "TaxFilingLockPolicy.DeclaredOrFiledStatuses"], [], [],
     "C1 เส้นขับ JE บล็อกเฉพาะงวดมัดจำยื่นแล้ว (เดิมบล็อกทุกกรณี ⇒ integration ถอยไปตั้งหนี้เงียบ)"),
    (DOCSVC, "AutoPostToJournalAsync",
     ["GuardDrivesGrossApplyAsync("], [], ["DepositPolicyResolver.GrossApplyBlocked("],
     "C1 เส้นขับ JE ต้องผ่านตัวตัดสินที่ดูงวดที่ยื่นแล้ว ไม่ใช่ GrossApplyBlocked ตรง ๆ"),
    (DOCSVC, "VoidDocumentAsync",
     ["ReverseDepositRealizationsForAsync("], [], [],
     "C4 void ใบสุดท้ายต้องกลับการรับรู้มัดจำที่ทำเพื่อใบนั้น"),
    (DOCSVC, "RealizeDepositAsync",
     ["DepositRealizedForDocumentId = realizedFor"], [], [],
     "C4 FinalInvoiceId ต้องถูกอ่าน (เดิมไม่มีผู้อ่าน)"),
    (INTEGRATION, "ProcessInvoiceAsync",
     ["DepositPolicyResolver.ImmediateVatGrossApplyRuleCode"],
     [("DepositPolicyResolver.ImmediateVatGrossApplyRuleCode", "catch (Exception exCash)")], [],
     "C1 มัดจำออกใบกำกับแล้ว (งวดยื่นแล้ว) ห้ามถอยไปตั้งหนี้เงียบ — ต้องล้มดัง"),
]

RULES += [dict(file=f, method=m, must=list(mu), before=list(b), forbid=list(fo), why=w)
          for (f, m, mu, b, fo, w) in TUPLE_RULES]


# ── ตัดคอมเมนต์/สตริงโดยคงตำแหน่ง ───────────────────────────────────────────────────────
def mask(text: str, keep_strings: bool = False) -> str:
    out = list(text)
    n = len(text)

    def blank(a, b):
        for k in range(a, min(b, n)):
            if out[k] != "\n":
                out[k] = " "

    def skip_string(i):
        j = i
        interp = verb = False
        while j < n and text[j] in "$@":
            interp |= text[j] == "$"
            verb |= text[j] == "@"
            j += 1
        if text.startswith('"""', j):
            end = text.find('"""', j + 3)
            return n if end < 0 else end + 3
        j += 1
        while j < n:
            c = text[j]
            if verb and c == '"' and j + 1 < n and text[j + 1] == '"':
                j += 2; continue
            if not verb and c == "\\":
                j += 2; continue
            if c == '"':
                return j + 1
            if interp and c == "{":
                if j + 1 < n and text[j + 1] == "{":
                    j += 2; continue
                j = skip_code_block(j + 1)
                continue
            if not verb and c == "\n":
                return j
            j += 1
        return n

    def skip_code_block(j):
        depth = 0
        while j < n:
            c = text[j]
            if c == '"' or (c in "$@" and j + 1 < n and text[j + 1] in '"$@'):
                j = skip_string(j); continue
            if c == "'":
                j = skip_char(j); continue
            if c == "{":
                depth += 1
            elif c == "}":
                if depth == 0:
                    return j + 1
                depth -= 1
            j += 1
        return n

    def skip_char(j):
        k = j + 1
        k += 2 if (k < n and text[k] == "\\") else 1
        while k < n and text[k] != "'" and text[k] != "\n" and k - j < 12:
            k += 1
        return k + 1

    i = 0
    while i < n:
        c = text[i]
        if text.startswith("//", i):
            e = text.find("\n", i); e = n if e < 0 else e
            blank(i, e); i = e; continue
        if text.startswith("/*", i):
            e = text.find("*/", i + 2); e = n if e < 0 else e + 2
            blank(i, e); i = e; continue
        if c == '"' or (c in "$@" and i + 1 < n and text[i + 1] in '"$@'):
            e = skip_string(i)
            if not keep_strings:
                blank(i, e)
            i = e; continue
        if c == "'":
            e = skip_char(i)
            if not keep_strings:
                blank(i, e)
            i = e; continue
        i += 1
    return "".join(out)


def pat(p: str) -> re.Pattern:
    """ข้อความ → regex ที่ไม่สนช่องว่าง/ขึ้นบรรทัดรอบเครื่องหมาย (แก้ฟ้องผิด FP1: `X\\n    .Decide(`)"""
    out = []
    for tok in re.findall(r"[A-Za-z0-9_]+|\s+|.", p):
        if tok.isspace():
            out.append(r"\s+")
        elif re.fullmatch(r"[A-Za-z0-9_]+", tok):
            out.append(re.escape(tok))
        else:
            out.append(r"\s*" + re.escape(tok) + r"\s*")
    return re.compile("".join(out))


RE_DECL = r"^[ \t]*(?:public|private|internal|protected)\b[^;\n=]*?\b{name}\s*(?:<[^>\n]*>)?\s*\("


def method_body(masked: str, name: str):
    m = re.search(RE_DECL.format(name=re.escape(name)), masked, flags=re.M)
    if not m:
        return None
    j, depth = m.end() - 1, 0
    while j < len(masked):
        if masked[j] == "(":
            depth += 1
        elif masked[j] == ")":
            depth -= 1
            if depth == 0:
                break
        j += 1
    k = masked.find("{", j)
    arrow = masked.find("=>", j)
    if k < 0 or (0 <= arrow < k):
        return None
    depth = 0
    for e in range(k, len(masked)):
        if masked[e] == "{":
            depth += 1
        elif masked[e] == "}":
            depth -= 1
            if depth == 0:
                return (k, e + 1)
    return None


def call_arg_spans(body: str, callee: str):
    """ช่วงข้อความอาร์กิวเมนต์ของทุกการเรียก callee (callee ลงท้ายด้วย '(')"""
    spans = []
    for m in pat(callee).finditer(body):
        start = m.end()
        depth, e = 1, start
        while e < len(body) and depth:
            if body[e] == "(":
                depth += 1
            elif body[e] == ")":
                depth -= 1
            e += 1
        spans.append((start, e - 1))
    return spans


def check_rule(text: str, rule):
    rel, meth, why = rule["file"], rule["method"], rule["why"]
    code = mask(text)
    span = method_body(code, meth)
    if span is None:
        return [f"{rel}: ไม่พบเมธอด {meth} (ย้าย/เปลี่ยนชื่อ? ต้องแก้ RULES ให้ตรง — ห้ามปล่อยกติกาที่ไม่ตรวจอะไร)"]
    body = code[span[0]:span[1]]
    lit_body = mask(text, keep_strings=True)[span[0]:span[1]]
    line0 = code.count("\n", 0, span[0]) + 1
    ln = lambda idx: line0 + body.count("\n", 0, idx)
    errs = []
    for p in rule.get("must", []):
        if not pat(p).search(body):
            errs.append(f"{rel}:{line0} {meth} ไม่เรียก `{p}` — {why}")
    for rx in rule.get("must_re", []):
        if not re.search(rx, body):
            errs.append(f"{rel}:{line0} {meth} ไม่พบรูป `{rx}` (ผลของด่านต้องถูกใช้/throw ต้องยังอยู่) — {why}")
    for p in rule.get("must_lit", []):
        if not pat(p).search(lit_body):
            errs.append(f"{rel}:{line0} {meth} ไม่พบ `{p}` — {why}")
    for callee, arg in rule.get("call_args", []):
        spans = call_arg_spans(body, callee)
        if not spans:
            errs.append(f"{rel}:{line0} {meth} ไม่เรียก `{callee}` — {why}")
        for a, b in spans:
            if not re.search(r"\b" + re.escape(arg) + r"\b", body[a:b]):
                errs.append(f"{rel}:{ln(a)} {meth}: `{callee}` ไม่ได้ส่ง `{arg}` — {why}")
    for a, b in rule.get("before", []):
        ma, mb = pat(a).search(body), pat(b).search(body)
        if ma is None:
            errs.append(f"{rel}:{line0} {meth} ไม่เรียก `{a}` — {why}")
        if mb is None:
            errs.append(f"{rel}:{line0} {meth} หา `{b}` ไม่เจอ (ลำดับ `{a}` ก่อน `{b}` ตรวจไม่ได้ — ห้ามข้ามเงียบ) — {why}")
        if ma and mb and ma.start() > mb.start():
            errs.append(f"{rel}:{ln(mb.start())} {meth}: `{a}` ต้องมาก่อน `{b}` — {why}")
    for p in rule.get("forbid", []):
        m = pat(p).search(body)
        if m:
            errs.append(f"{rel}:{ln(m.start())} {meth} มี `{p}` ซึ่งห้ามใช้ที่นี่ (ประกอบเอง/ค่าว่างแทนหลักฐาน/นอก chain) — {why}")
    return errs


# ── negative tests ─────────────────────────────────────────────────────────────────────────
def _code_spans(raw: str, rx: re.Pattern):
    return [(m.start(), m.end()) for m in rx.finditer(mask(raw))]


def _replace_spans(raw: str, spans, repl: str) -> str:
    for a, b in sorted(spans, reverse=True):
        raw = raw[:a] + repl + raw[b:]
    return raw


# การถดถอยจริงที่ฝ่ายค้านลอง (review193-M2 §C6) + รูปแบบถูกต้องที่เคยฟ้องผิด — (เมธอด, หา, แทน, ต้องฟ้อง?)
REVIEWER_CASES = [
    ("M2", "RefundOrderAsync", '"คืนวัตถุดิบจากการคืนเงิน POS", userId, saleUnitCosts);', '"คืนวัตถุดิบจากการคืนเงิน POS", userId);', True),
    ("M3", "VoidOrderAsync", "journalsToReverse.Add((saleJe.ReverseEntryId!.Value,", "journalsToReverse.Add((order.JournalEntryId!.Value,", True),
    ("M4", "CalculatePayrollAsync", "run.Status, run.ExternalSystem, run.ReopenedAt, lockEvidence);", "run.Status, run.ExternalSystem, run.ReopenedAt, PayrollRunLockEvidence.None);", True),
    ("M5", "CalculatePayrollAsync", "throw new Accounting.Helpers.BusinessRuleException(recalcReason!);", "_ = recalcReason;", True),
    ("M6a", "LoadRecalculateLockEvidenceAsync", '&& e.Status == "Filed")', '&& e.Status != null)', True),
    ("M6b", "LoadRecalculateLockEvidenceAsync", "runSsoSettled: run.SsoSettledAt.HasValue,", "runSsoSettled: false,", True),
    ("M7", "ApplyRecipeConsumptionAsync", "return (true, Accounting.Helpers.PosCogsBooking.RecipeCost(moved));", "var _rc = Accounting.Helpers.PosCogsBooking.RecipeCost(moved); return (true, moved.Sum(m => m.MoveCost));", True),
    ("M8", "CalculatePayrollAsync", "_db.Set<PayrollDetail>().RemoveRange(run.Details);", "_db.Set<PayrollDetail>().RemoveRange(existingRun.Details);", True),
    ("C4", "VoidOrderAsync", "_db.AddChainedAuditLog(new AuditLog", "_db.AuditLogs.Add(new AuditLog", True),
    ("P6", "UpdateComponentAsync", "if (request.CommissionTypeConfirmed == true) comp.CommissionTypeConfirmedAt", "comp.CommissionTypeConfirmedAt", True),
    ("FP1", "VoidOrderAsync", "Accounting.Helpers.PosVoidSaleJournal.Decide(chain", "Accounting.Helpers.PosVoidSaleJournal\n                    .Decide(chain", False),
    ("FP2", "CalculatePayrollAsync", "if (!canRecalc)\n", "if ( !canRecalc )\n", False),
]


def self_test() -> list:
    fails = []
    cache = {}
    by_method = {}
    for r in RULES:                      # กติกาของ M2 มาก่อน ⇒ ชื่อเมธอดซ้ำข้ามไฟล์ไม่ทับเคสของฝ่ายค้าน
        by_method.setdefault(r["method"], r)
    for rule in RULES:
        rel, meth = rule["file"], rule["method"]
        text = cache.setdefault(rel, (SRC / rel).read_text(encoding="utf-8"))
        span = method_body(mask(text), meth)
        if span is None:
            continue
        head, body, tail = text[:span[0]], text[span[0]:span[1]], text[span[1]:]
        run = lambda b: check_rule(head + b + tail, rule)
        for p in rule.get("must", []):
            removed = _replace_spans(body, _code_spans(body, pat(p)), "REMOVED_CALL_SITE")
            if not any(f"`{p}`" in e for e in run(removed)):
                fails.append(f"self-test: ลบ `{p}` จาก {meth} แล้วไม่ฟ้อง")
            if not any(f"`{p}`" in e for e in run(removed.replace("{", "{ // " + p + "\n", 1))):
                fails.append(f"self-test: `{p}` ในคอมเมนต์ถูกนับเป็นการเรียกใน {meth}")
        for rx in rule.get("must_re", []):
            if not run(_replace_spans(body, _code_spans(body, re.compile(rx)), "REMOVED_CALL_SITE")):
                fails.append(f"self-test: ลบรูป `{rx}` จาก {meth} แล้วไม่ฟ้อง")
        for p in rule.get("must_lit", []):
            lit_spans = [(m.start(), m.end()) for m in pat(p).finditer(mask(body, keep_strings=True))]
            if not run(_replace_spans(body, lit_spans, "REMOVED_LITERAL")):
                fails.append(f"self-test: ลบ `{p}` จาก {meth} แล้วไม่ฟ้อง")
        for callee, arg in rule.get("call_args", []):
            code = mask(body)
            spans = []
            for a, b in call_arg_spans(code, callee):
                spans += [(a + m.start(), a + m.end()) for m in re.finditer(r"\b" + re.escape(arg) + r"\b", code[a:b])]
            if not any("ไม่ได้ส่ง" in e for e in run(_replace_spans(body, spans, "null"))):
                fails.append(f"self-test: `{callee}` ไม่ส่ง `{arg}` ใน {meth} แล้วไม่ฟ้อง")
        for a, b in rule.get("before", []):
            sa, sb = _code_spans(body, pat(a))[:1], _code_spans(body, pat(b))[:1]
            if sa and sb:
                (a0, a1), (b0, b1) = sa[0], sb[0]
                ta, tb = body[a0:a1], body[b0:b1]
                first, second = sorted([(a0, a1, tb), (b0, b1, ta)])
                swapped = body[:first[0]] + first[2] + body[first[1]:second[0]] + second[2] + body[second[1]:]
                if not any("ต้องมาก่อน" in e for e in run(swapped)):
                    fails.append(f"self-test: สลับ `{a}` ↔ `{b}` ใน {meth} แล้วไม่ฟ้อง")
            if not any("หา" in e and "ไม่เจอ" in e for e in run(_replace_spans(body, _code_spans(body, pat(b)), "REMOVED_CALL_SITE"))):
                fails.append(f"self-test: ลบ `{b}` จาก {meth} แล้วกติกาลำดับข้ามเงียบ")
        for p in rule.get("forbid", []):
            if not any("ห้ามใช้" in e for e in run(body.replace("{", "{ var __x = " + p + "1m);\n", 1))):
                fails.append(f"self-test: ใส่ `{p}` ใน {meth} แล้วไม่ฟ้อง")
    for tag, meth, old, new, expect in REVIEWER_CASES:
        rule = by_method[meth]
        text = cache.setdefault(rule["file"], (SRC / rule["file"]).read_text(encoding="utf-8"))
        if old not in text:
            fails.append(f"self-test {tag}: หา `{old[:50]}` ใน {rule['file']} ไม่เจอ (โค้ดขยับ — ปรับเคสให้ตรง)")
            continue
        fired = bool(check_rule(text.replace(old, new, 1), rule))
        if fired != expect:
            fails.append(f"self-test {tag}: {'ต้องฟ้องแต่ไม่ฟ้อง' if expect else 'ฟ้องผิด (โค้ดถูกต้อง)'} ใน {meth}")
    sample = (
        'class A {\n'
        '    private async Task Foo(int x)\n'
        '    {\n'
        '        var s = $"{(x > 0 ? "}" : "{")} Bar.Call(";\n'
        '        var t = @"}}"" Bar.Call(";\n'
        '        Baz.Real();\n'
        '    }\n'
        '    private void After() { Bar.Call(1); }\n'
        '}\n')
    errs = check_rule(sample, dict(file="x.cs", method="Foo", must=["Baz.Real(", "Bar.Call("], why="t"))
    if not (len(errs) == 1 and "Bar.Call(" in errs[0]):
        fails.append(f"self-test: ตัวตัดสตริงผิด — คาด 1 ข้อ ได้ {errs}")
    return fails


def main() -> int:
    errs = []
    for rule in RULES:
        path = SRC / rule["file"]
        if not path.exists():
            errs.append(f"{rule['file']}: ไม่พบไฟล์")
            continue
        errs += check_rule(path.read_text(encoding="utf-8"), rule)
    st = self_test()
    for e in errs + st:
        print("❌ " + e)
    if errs or st:
        return 1
    if "--self-test" in sys.argv:
        print("self-test: ผ่าน")
    print(f"required_call_site_check: {len(RULES)} กติกา ผ่าน (+ negative test ในตัว {len(REVIEWER_CASES)} เคสจากฝ่ายค้าน)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
