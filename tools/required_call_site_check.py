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
  forbid_lit — ห้ามมี (ค้นบนโค้ดที่ตัดแค่คอมเมนต์ — รูในสตริง interpolate เช่น `{t.AccentColor}` ที่ต่อเข้า CSS/HTML ดิบ · รอบ 200 ทีม RF)

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

# ── รอบ 193 ทีม S2 (ฝ่ายค้านรอบสอง R2-C2 · P0): ใบเบิกค่าใช้จ่าย — ด่านสิทธิ์ + SoD อยู่ใน service เอง ⇒ ทุกทางเข้า (เว็บ · มือถือ)
#    ได้ด่านเดียวกัน · เทสต์ล็อกแค่ตัวตัดสิน pure (ExpenseClaimActionPolicyTests) — ที่นี่ล็อกว่าเมธอดเขียนทุกตัวเรียกด่านก่อนบันทึก ──
EXPENSE = "Services/Implementations/ExpenseClaimService.cs"
_EXPENSE_WHY = "R2-C2 เมธอดเขียนของใบเบิกต้องเรียกด่านสิทธิ์ (ExpenseClaimActionPolicy) ก่อนบันทึก — มือถือ/ทางเข้าอื่นพึ่งด่านนี้"
RULES += [
    dict(file=EXPENSE, method=m, must=["EnsureClaimActionAsync("],
         before=[("EnsureClaimActionAsync(", "_db.SaveChangesAsync(")], why=_EXPENSE_WHY)
    for m in ("UpdateAsync", "SubmitAsync", "ApproveAsync", "RejectAsync", "MarkAsPaidAsync", "VoidAsync")
]
RULES += [
    dict(file=EXPENSE, method="MarkAsPaidAsync", call_args=[("ApproveDocumentAsync(", "payerUserId")],
         why="R2-C2 ใบสำคัญจ่ายที่เกิดจากการจ่ายใบเบิกต้องอนุมัติในนามผู้กด (ไม่ใช่ \"system\") — ข้าม CanApproveAsync ไม่ได้"),
    dict(file=EXPENSE, method="DenyClaimActionAsync",
         must=["ExpenseClaimActionPolicy.Decide(", "ExpenseClaimActionPolicy.SelfDecisionAllowed(",
               "DocumentPermissionHelper.CanApproveAsync(", "ExpenseClaimActionPolicy.ReviewerKeys("],
         why="R2-C2 ด่านใบเบิกต้องรวบรวมหลักฐานครบ (คีย์ · SoD เจ้าของ/สวิตช์ · สิทธิ์อนุมัติ PV) แล้วให้ตัวตัดสินเดียวตัดสิน"),
    # ฝ่ายค้านรอบสาม (B7 · กฎ #4 D watermark): งานกวาดไฟล์สแกนต้องตัดแถวที่ข้ามแน่ที่ query + เรียงแน่นอน + เลื่อนแถวที่ลบไม่ได้
    dict(file="Services/Implementations/Ocr/OcrSelfCorrectionService.cs", method="RunMaintenanceAsync",
         must=["ThenBy(s => s.Id)", "ThenBy(f => f.Id)", "f.UpdatedAt = sweepNow",
               "o.FileAttachmentId == f.Id", "o.CreatedJournalEntryId != null"],
         why="B7 แถวที่กวาดไม่ได้ต้องไม่ค้างหัวคิว Take(500) ทุกคืน (head-of-line) — ตัดที่ query · เรียงด้วย id · ประทับเวลาแถวที่ลบไม่ได้"),
    dict(file=MOBILE, method="HandleExpenseClaimApprovalAsync", must=["ApproveAsync(", "RejectAsync("],
         forbid=["ExpenseClaimStatus.Approved;", "ExpenseClaimStatus.Rejected;"],
         why="R2-C2/Q7 มือถืออนุมัติใบเบิกต้องเดินเมธอดเดียวกับเว็บ (ด่านสิทธิ์ · SoD · §65 ทวิ · CertificateInLieu) — ห้ามตั้งสถานะเอง"),
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
      "taxKey.MayOverwriteBranch", "ContactTaxBranchKey.AdoptTaxId(", "ContactAdoptOutcome.Reject",
      "ContactTaxBranchKey.StampTaxIdWarning(", "ContactTaxBranchKey.TaxIdChecksumWarning("],
     [("ContactTaxBranchKey.FindAsync(", "ContactTaxBranchKey.SoftScope(")], [],
     "integration ลูกค้า: หาเลข+สาขาของ payload · ถอยไปชื่อบนชุด SoftScope เท่านั้น · ห้ามเขียนสาขา/เลขภาษีทับแถวที่ไม่ตรง (C-6)"),
    (INTEG, "ResolveContactAsync",
     ["ContactTaxBranchKey.FindAsync(", "ContactTaxBranchKey.SoftScope(", "ContactTaxBranchKey.AdoptTaxId(", "ContactAdoptOutcome.Reject"], [], [],
     "integration ใบขาย: ชื่อตรงห้ามได้นิติบุคคลอื่นที่ถือเลขอื่น (C-6)"),
    (INTEG, "ResolveSupplierAsync",
     ["ContactTaxBranchKey.FindAsync(", "ContactTaxBranchKey.SoftScope(", "ContactTaxBranchKey.AdoptTaxId(", "ContactAdoptOutcome.Reject"], [], [],
     "integration ผู้ขาย: ชื่อตรงห้ามได้นิติบุคคลอื่นที่ถือเลขอื่น (C-6)"),
    (IMPORT, "ImportContactAsync",
     ["ContactTaxBranchKey.FindAsync(", "ContactTaxBranchKey.SoftScope(", "taxKey.MayOverwriteBranch",
      "ContactTaxBranchKey.AdoptTaxId(", "ContactAdoptOutcome.Reject"], [], [],
     "นำเข้าผู้ติดต่อ: อีเมลบนชุด SoftScope · รหัสสาขาในไฟล์เขียนทับได้เฉพาะแถวที่ตรงสาขาแล้ว"),
    (DOCS_V1, "Create",
     ["TryNormalize(req.ContactBranchCode", "TaxInvoiceCompletenessChecker.MissingBuyerFields(",
      "ContactTaxBranchKey.TaxIdChecksumWarning("], [], [],
     "API v1: รหัสสาขาผิดรูป = 400 · บอกช่องผู้ซื้อที่ขาดตาม §86/4 (เช่นที่อยู่ของแถวสาขาใหม่ — P-5)"),
    (DOCS_V1, "ResolveContactAsync",
     ["taxId, req.ContactBranchCode, ct)", "branchCode: req.ContactBranchCode", "ContactTaxBranchKey.SoftScope(",
      "ContactTaxBranchKey.NameMatchKind(", "ContactTaxBranchKey.StampTaxIdWarning(",
      "ContactTaxBranchKey.AdoptTaxId(", "ContactAdoptOutcome.Reject"], [], [],
     "API v1: สาขาของ payload ต้องไปถึงการหา + การสร้าง (เดิมส่ง null ⇒ ใบกำกับออกในนาม สนญ.) · ชื่อบนชุด SoftScope"),
    (CONTACTS_V1, "SyncCoreAsync",
     ["ContactTaxBranchKey.Pick(", "TaxBranchCode.Normalize(item.BranchCode"], [], [],
     "sync: ด่าน CONTACT-TAXID-OWNED ต้องเทียบเลข + สาขา (เดิมบล็อกสาขาของนิติบุคคลเดียวกัน)"),
    (CMS_CUST, "AutoLinkToErpContactAsync",
     ["customer.TaxId, customer.BranchCode)", "ContactTaxBranchKey.SoftScope(", "ContactTaxBranchKey.AdoptTaxId(", "ContactAdoptOutcome.Reject"], [], [],
     "CMS: ลูกค้าเว็บสาขา 8 ห้ามผูกผู้ติดต่อ สนญ. · อีเมลบนชุด SoftScope"),
    (CMS_LEAD, "EnsureContactLinkedAsync",
     ["ContactTaxBranchKey.FindAsync(", "ContactTaxBranchKey.SoftScope(", "ContactTaxBranchKey.AdoptTaxId(", "ContactAdoptOutcome.Reject"], [], [],
     "CMS lead: เลขภาษีผ่านตัวจับคู่กลาง · อีเมลบนชุด SoftScope (C-6)"),
    (PLATFORM, "EnsureContactAsync",
     ["ContactTaxBranchKey.HasTaxId(buyer.TaxId)", "c.ExternalId == buyerKey", "taxId, buyerBranch)",
      "ContactTaxBranchKey.SoftScope(", "ContactTaxBranchKey.AdoptTaxId(", "ContactAdoptOutcome.Reject"],
     [("c.ExternalId == buyerKey", "ContactTaxBranchKey.FindAsync(")], [],
     "ใบค่าบริการแพลตฟอร์ม: \"-\" ของบริษัทที่สมัครใหม่ไม่ใช่เลข (C-8) · เลข + สาขาของลูกค้า"),
    (XTENANT, "FindPartnerContactAsync",
     ["ContactTaxBranchKey.HasTaxId(partner.TaxId)", "c.ExternalId == partnerKey", "ContactTaxBranchKey.FindAsync(",
      "partner.BranchCode", "ContactTaxBranchKey.SoftScope(", "ContactTaxBranchKey.AdoptTaxId(", "ContactAdoptOutcome.Reject"],
     [("c.ExternalId == partnerKey", "ContactTaxBranchKey.FindAsync(")], [],
     "ข้ามบริษัท: \"-\" ไม่ใช่เลข (C-8) · เลข + สาขาของบริษัทคู่ค้า"),
    (CONTACTS_V1, "Resolve",
     ["ContactTaxBranchKey.FindAsync(", "ContactTaxBranchKey.SoftScope("], [], [],
     "resolve: ผู้สมัครเทียบชื่อจากชุด SoftScope (เลขใหม่ห้ามตอบ matched กับนิติบุคคลอื่น)"),
    (IMPORT, "ImportOpeningSubledgerAsync",
     ["ContactTaxBranchKey.FindAsync(", "ContactTaxBranchKey.SoftScope(", "ContactTaxBranchKey.AdoptTaxId(", "ContactAdoptOutcome.Reject"], [], [],
     "ยอดยกมา: ชื่อบน SoftScope · แถวที่จับได้รับเลขจากไฟล์ (R2-C5)"),
    (IMPORT, "ImportDocumentAsync",
     ["ContactTaxBranchKey.FindAsync(", "ContactTaxBranchKey.SoftScope(", "ContactTaxBranchKey.NameMatchKind(",
      "OrderBy(c => c.Name.Length)", "ContactTaxBranchKey.AdoptTaxId(", "ContactAdoptOutcome.Reject"], [], [],
     "นำเข้าเอกสาร: ชื่อบน SoftScope · substring เรียงแน่นอน · เติมเลขเฉพาะชื่อตรงตัว (R3-2) · แถวที่จับได้รับเลขจากไฟล์ (R2-C5)"),
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
    # ── รอบ 193 ทีม L2 หลังฝ่ายค้าน (review193-L2.md §D: เทสต์เรียกแค่ helper — ถอดการแก้ใน service แล้วยังเขียว) ──
    (LODGING_LIFE, "CheckOutAsync",
     ["LoadDepositSnapshotsAsync(", "LodgingDepositSettlement.PlanCheckout(", "DocumentService.PreviewTotals(",
      "LodgingPricingEngine.ChargeVatRate(", "ResumeCheckOutAsync(", "SettleCheckOutAsync(",
      "DepositBaseDeducted: depositPlan.BaseDeducted"],
     [("LodgingDepositSettlement.PlanCheckout(", "_docService.CreateDocumentAsync("),
      ("FindOrCreateContactAsync(", "r.Charges.Add("),
      ("_docService.ApproveDocumentAsync(", "r.Charges.Add(")],
     ["DepositAppliedDrivesJournal", "r.FinalDocumentId != null", "BillDiscountAmount: depositPlan"],
     "R3-1 ฐานมัดจำลงช่องของตัวเอง (ห้ามใส่ช่องส่วนลดการค้า) · C2/C5 วางแผนใช้มัดจำ (มัดจำเกินยอด = ค้างคืน) ก่อนออกเลขใบ · ด่าน/สร้างผู้ติดต่อก่อนผูกค่าเสียหาย · "
     "ใบเครดิตห้ามใช้ธงขับ JE (P0-1) · ออกใบแล้วกดซ้ำ = ทำต่อ ไม่ throw"),
    (LODGING_LIFE, "SettleCheckOutAsync",
     ["RealizedForFinalByDepositAsync(", "new RealizeDepositRequest(left, DateTime.UtcNow, prop.RoomRevenueAccountCode, finalId)",
      "Math.Min(a.Gross, finalDoc.BalanceDue)", "r.RefundAmount = plan.ExcessGross", "r.RefundBaselineGross ="],
     [("RealizedForFinalByDepositAsync(", "RealizeDepositAsync(")], [],
     "C4/N1 รับรู้เฉพาะส่วนที่อนุมัติยังไม่ได้รับรู้ (ห้ามซ้ำ) · ผูกใบสุดท้าย · ตัดชำระไม่เกินยอดใบ · ส่วนเกิน = ค้างคืน + จุดตั้งยอด (N3)"),
    (LODGING_LIFE, "ResumeCheckOutAsync",
     ["DepositRealizedForDocumentId == finalId", "LodgingDepositSettlement.PlanCheckout(",
      "finalDoc.DepositBaseDeducted - realizedForFinal"], [], ["finalDoc.BillDiscountAmount"],
     "ทำเช็คเอาต์ต่อ: นับที่รับรู้เพื่อใบนี้ไปแล้ว ไม่ใช้มัดจำซ้ำ · R3-1 ความจุ = ช่องฐานมัดจำ ไม่ใช่ส่วนลดการค้า"),
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
     ["LodgingDepositSettlement.IsLegacyRefund(", "VatPeriodFiledAsync(",
      "LodgingDepositSettlement.AllocateRefund(", "SyncRefundPaidFromDeposits("],
     [("VatPeriodFiledAsync(", "RefundDepositAsync("),
      ("SyncRefundPaidFromDeposits(", "LodgingDepositSettlement.ValidateRefundPayment("),
      ("_db.SaveChangesAsync(", "LodgingDepositSettlement.ValidateRefundPayment(")], [],
     "C10 แถว legacy ห้ามลงคืนซ้ำ · ห้ามลงวันที่ย้อนเข้างวด ภ.พ.30 ที่ยื่นแล้ว · ยอดคืนแล้วตามใบมัดจำ"),
    (LODGING_LIFE, "BuildChargeAsync",
     ["LodgingPricingEngine.ChargeVatRate("], [], ["request.VatRate ??"],
     "C8 อัตรา VAT รายการ folio ต้องผ่านด่าน §90/2"),
    (LODGING_LIFE, "ConfirmAsync",
     ["LodgingDepositSettlement.StatusAfterDeposit(", "request.ConfirmReservation"], [], [],
     "S-06/C9 ปุ่มรับชำระเพิ่มไม่ใช่การยืนยัน — ตามค่าตั้ง AutoConfirmOnDeposit"),
    (LODGING_RES, "FindOrCreateContactAsync",
     ["ContactTaxBranchKey.SoftMatchScope(", "LodgingGuestContact.SoftCandidateAcceptable(", "ContactTaxBranchKey.AdoptTaxId(",
      "ContactMatchKind.Email", "ContactMatchKind.Phone", "ContactAdoptOutcome.Reject", "!x.IsWalkInCustomer",
      "ContactTaxBranchKey.StampTaxIdWarning(c)"],
     [("LodgingGuestContact.SoftCandidateAcceptable(", "ContactTaxBranchKey.AdoptTaxId("),
      ("ContactTaxBranchKey.StampTaxIdWarning(c)", "_db.Contacts.Add(c)")],
     ["softScope.FirstOrDefaultAsync(", "c.TaxId = taxId", "BranchCode ??="],
     "C-7 แขกนิติบุคคลห้ามได้แถวบุคคลธรรมดาที่อีเมล/เบอร์ตรง (§86/4 ผู้ซื้อผิดตัว) · R3-2 ส่งชนิดการจับจริง · Reject = สร้างแถวใหม่ · "
     "แถว walk-in ไม่ใช่ตัวแขก"),
    (LODGING, "EffectiveVatRateAsync",
     ["CompanyVatStatus.ProfileAsync(", "LodgingPricingEngine.PropertyVatRate("], [], [],
     "S-10 อัตรา VAT ที่พักผ่านตัวอ่านสถานะ VAT ตัวเดียว"),
    (DOCSVC, "GuardDrivesGrossApplyAsync",
     ["DepositPolicyResolver.GrossApplyBlocked(", "throw new", "DepositPolicyResolver.DrivesGuardMessage("], [],
     ["TaxReports", "AllowedVatMoved", "GrossApplyBlockedMessage("],
     "N1 เส้นขับ JE เข้มเท่าปุ่มทุกงวด (ผ่อนตามงวด ⇒ ผู้ซื้อได้ใบกำกับสองใบ VAT 618.22) · R3-5 ข้อความทางเดียว (ข้อความปุ่มสั่งรับรู้ที่หน้าเงินมัดจำ ⇒ ซ้ำ)"),
    (DOCSVC, "AutoPostToJournalAsync",
     ["GuardDrivesGrossApplyAsync(", "RealizeTaxedDepositDeductionsAsync("], [], [],
     "N1/C4 เส้นขับ JE ผ่านด่าน · ใบที่หักมูลค่ามัดจำก่อน VAT รับรู้มัดจำในธุรกรรมเดียวกับการลงบัญชี (กู้ใบ void แล้วอนุมัติใหม่ก็รับรู้)"),
    (DOCSVC, "RealizeTaxedDepositDeductionsAsync",
     ["DepositPolicyResolver.TaxedDepositDeducted(doc.DepositBaseDeducted", "DepositPolicyResolver.AllocateBaseDeduction(",
      "doc.DepositBaseDeducted - already", "realizedFor.Contains(", "lockRows: true", "RealizeDepositCoreAsync(",
      "DepositPolicyResolver.TaxedDepositDeductionAllowed("],
     [("realizedFor.Contains(", "RealizeDepositCoreAsync("), ("lockRows: true", "DepositPolicyResolver.AllocateBaseDeduction(")],
     ["doc.BillDiscountAmount"],
     "N1 รับรู้เฉพาะที่ยังไม่ได้รับรู้เพื่อใบนี้ (รวมใบแม่ของใบที่แปลงมา) · มัดจำไม่พอ = ล้มดัง · R3-1 อ่านช่องฐานมัดจำ ห้ามอ่านส่วนลดการค้า · "
     "B2 ล็อกแถวใบมัดจำก่อนอ่านฐานคงเหลือ"),
    (DOCSVC, "LoadTaxedDepositsByRefAsync",
     ["LockDepositBalancesAsync(", "d.CompanyId == companyId"], [], [],
     "B2 ล็อกแถวใบมัดจำ (เรียงตาม Id) แล้วอ่านค่าล่าสุดของแถวที่ context ถือ — อนุมัติพร้อมกันต้องต่อคิว "
     "(รอบ 194 R3-2: ย้ายไปตัวล็อกตัวเดียว LockDepositBalancesAsync)"),
    (DOCSVC, "ConvertTaxedDrivesAsync",
     ["DepositReversalMath.ParseDepositRefs(", "Distinct(", "DepositPolicyResolver.PercentWithTaxedDepositMessage",
      "DepositPolicyResolver.TaxedDepositBase("], [], ["billDiscountAmount", "Length != taxed.Count"],
     "R3-1 คืนฐานมัดจำอย่างเดียว (ไม่บวกส่วนลดการค้า) · B3 'ปนกัน' ตัดสินจากเลขที่ชี้มัดจำ VAT พักจริง ไม่ใช่นับจำนวน"),
    (DOCSVC, "AllocateBillDeductions",
     ["AllocateBillDiscount(", "DepositPolicyResolver.SplitBillDeduction(", "throw new"], [], [],
     "R3-1 ส่วนหักท้ายบิล = ส่วนลดการค้า + ฐานมัดจำ เฉลี่ยครั้งเดียวแล้วแยกกลับ · เกินยอดขาย = ล้มดัง"),
    (DOCSVC, "PreviewTotals",
     ["AllocateBillDeductions("], [], ["AllocateBillDiscount("],
     "R3-1 พรีวิวใช้ตัวแยกช่องตัวเดียวกับสร้าง/แก้"),
    (DOCSVC, "ConvertCoreAsync",
     ["DepositBaseDeducted: source.DepositBaseDeducted", "DepositAppliedRef: source.DepositBaseDeducted"], [], [],
     "R3-1 เอกสารลูกสืบทอดฐานมัดจำที่หัก + เลขใบมัดจำ (ยอดตรงใบแม่ · ไม่รับรู้ซ้ำ)"),
    (DOCSVC, "CreateDocumentAsync",
     ["ConvertTaxedDrivesAsync(", "DepositBaseDeducted = convCreate.DepositBase", "DepositPolicyResolver.TaxedDepositDeductionProblem(",
      "AllocateBillDeductions(", "doc.DepositBaseDeducted = createDepositBase"],
     [("ConvertTaxedDrivesAsync(", "DepositPolicyResolver.DrivesJournalSupported("),
      ("DepositPolicyResolver.TaxedDepositDeductionProblem(", "AllocateBillDeductions(")],
     ["BillDiscountAmount = convCreate", "AllocateBillDiscount("],
     "N1 ฟอร์มติ๊กขายเงินสดใบเดียว + มัดจำออกใบกำกับแล้ว ⇒ แปลงเป็นหักฐานตอนบันทึก (เว็บเลี่ยงปุ่มไม่ได้) · R3-1 ฐานมัดจำช่องแยก"),
    (DOCSVC, "UpdateDocumentAsync",
     ["ConvertTaxedDrivesAsync(", "DepositBaseDeducted = convUpdate.DepositBase", "DepositPolicyResolver.TaxedDepositDeductionProblem(",
      "AllocateBillDeductions(", "doc.DepositBaseDeducted = updDepositBase"],
     [("DocumentStatus.Rejected) && !isDocRevision", "ConvertTaxedDrivesAsync(")],
     ["BillDiscountAmount = convUpdate", "AllocateBillDiscount("],
     "N1 ทางแก้ร่างเดินด่านเดียวกับตอนสร้าง · B4 ด่านสถานะก่อนตัวแปลง · R3-1 ฐานมัดจำช่องแยก"),
    (DOCSVC, "RefundDepositAsync",
     ["DepositReversalMath.RefundSplit("], [], ["Math.Round(request.Amount * vatPortion"],
     "N4 แยกยอดคืนด้วยตัวเดียวกับตัววางแผนที่พัก (VAT จากยอดสะสม ⇒ คืนหลายงวดไม่ค้าง 0.01)"),
    (DOCSVC, "PurgeDocumentAsync#2",
     ["ReverseDepositRealizationsForAsync("], [], [],
     "C4 ลบใบสุดท้ายต้องลบ JE รับรู้มัดจำที่ทำเพื่อใบนั้นด้วย (ฝ่ายค้านรอบสอง: ถอดแล้วเขียว)"),
    (DOCSVC, "VoidDocumentAsync",
     ["ReverseDepositRealizationsForAsync("], [], [],
     "C4 void ใบสุดท้ายต้องกลับการรับรู้มัดจำที่ทำเพื่อใบนั้น"),
    (DOCSVC, "RealizeDepositAsync",
     ["RealizeDepositCoreAsync("], [], [],
     "ปุ่มรับรู้มัดจำกับการรับรู้ตอนอนุมัติใช้ตัวรับรู้ตัวเดียว"),
    (DOCSVC, "RealizeDepositCoreAsync",
     ["DepositRealizedForDocumentId = realizedFor"], [], ["SaveChangesAsync("],
     "C4 FinalInvoiceId ต้องถูกอ่าน · ตัวรับรู้ห้าม SaveChanges เอง (เรียกในธุรกรรมอนุมัติ)"),
    (INTEGRATION, "ProcessInvoiceAsync",
     ["DepositPolicyResolver.ImmediateVatGrossApplyRuleCode", "TaxedDrivesRejectionAsync(", "VoidDocumentAsync(", "RejectTaxedDrivesAsync(",
      "TaxedDrivesVoidFailedMarker", "_db.ChangeTracker.Clear("],
     [("TaxedDrivesRejectionAsync(", "_settingsService.GetNextNumberAsync("),
      ("TaxedDrivesRejectionAsync(", "ResyncUpdateInvoiceAsync("),
      ("VoidDocumentAsync(", "catch (Exception exCash)"),
      ("DepositPolicyResolver.ImmediateVatGrossApplyRuleCode", "catch (Exception exCash)")], [],
     "N1/N2 ปฏิเสธก่อนออกเลขและก่อน resync · ตาข่ายยกเลิกใบ (ห้ามใบ Approved ไม่มี JE · ห้ามถอยไปตั้งหนี้)"),
]

RULES += [dict(file=f, method=m, must=list(mu), before=list(b), forbid=list(fo), why=w)
          for (f, m, mu, b, fo, w) in TUPLE_RULES]

# ── รอบ 199 (คำตัดสินเจ้าของรอบ 198 ข้อ 6 · ฝ่ายค้าน B-1): API v1 ปฏิเสธใบสแกนที่ VAT ไม่ได้พิมพ์บนกระดาษ "ก่อน" เรียกอนุมัติ ──────
# ถอดด่าน / ทิ้งผล (ไม่ return) / ย้ายไปหลังอนุมัติ / กลับไปใช้ IsGapWarning (ที่รวมชุด VAT) เป็นตัวแยกชุด "ผ่าน" = ฟ้อง
RULES += [
    dict(file=APIV1, method="Approve",
         must=["PreviewApprovalWarningsAsync(", "ApprovalAcknowledgement.ApiRefusal(", "ApprovalAckSource.ApiClient",
               "withAiHints: false", "OcrApprovalGapWarning.IsAmountGapWarning", "ApprovalAcknowledgement.ApiRefusalOf("],
         must_re=[r"if\s*\(\s*refusal\s+is\s+not\s+null\s*\)\s*return\b"],
         before=[("ApprovalAcknowledgement.ApiRefusal(", "_documents.ApproveDocumentAsync(")],
         forbid=["OcrApprovalGapWarning.IsGapWarning", "acknowledgeWarnings: true"],
         why="C6 + คำตัดสินข้อ 6: API ห้ามเรียก AI เสริมคำเตือน · VAT ไม่ได้พิมพ์บนกระดาษ = 422 ก่อนอนุมัติ · [Σ-GAP] ยอดอย่างเดียวไม่ขัดจังหวะ (ข้อ 12)"),
    dict(file=APIV1, method="RefuseApprovalAsync",
         must=["ApprovalAcknowledgement.ApiRefusalNote(", "UnprocessableEntity(", "refusal.Code"],
         forbid=["ApproveDocumentAsync(", "MeterAsync("],
         why="คำตัดสินข้อ 6: ปฏิเสธ = 422 พร้อมรหัส + หมายเหตุบนเอกสารครั้งเดียว · ห้ามอนุมัติ/คิดเงินในเส้นปฏิเสธ"),
    # รอบ 199 ทีม W: อนุมัติหลายใบห้ามส่งธงรับทราบของผู้เรียกเข้า service (เดิม req.AcknowledgeWarnings ⇒ ประทับ "ผู้ใช้รับทราบ"
    # ให้คำเตือนที่ไม่มีใครเห็น) · ปฏิเสธธงเหมารวมก่อนแตะเอกสาร · ใบที่มีคำเตือนคืนเป็นรายการ ไม่ใช่ "ล้มเหลว"
    dict(file="Controllers/DocumentController.cs", method="BulkApprove",
         must=["ApprovalAcknowledgement.BulkBlanketAckRefusal(", "ApprovalAckSource.None", "withAiHints: false",
               "catch (DocumentApprovalWarningsException", "needsAck.Add(", "ApprovalAcknowledgement.BulkSummary("],
         must_re=[r"if\s*\(\s*blanketAckRefusal\s+is\s+not\s+null\s*\)\s*return\b"],
         before=[("ApprovalAcknowledgement.BulkBlanketAckRefusal(", "_documentService.ApproveDocumentAsync("),
                 ("catch (DocumentApprovalWarningsException", "catch (Exception")],
         call_args=[("_documentService.ApproveDocumentAsync(", "ApprovalAckSource.None")],
         forbid=["userId, req.AcknowledgeWarnings", "ApprovalAckSource.User", "ApprovalAckSource.SystemWorkflow", "acknowledgeWarnings: true"],
         why="คำตัดสิน #12 · รอบ 198 ข้อ 6: คำเตือนก่อนอนุมัติต้องมีคนเห็นทีละใบ — bulk ห้ามรับทราบแทนคน"),
]

# ── รอบ 193 ทีม O1 หลังฝ่ายค้านรอบสอง (C10 บางส่วน): ล็อก "ผลต้องถูกใช้" ไม่ใช่แค่ "มีการเรียก" ─────────────────
RULES += [
    dict(file=OCR, method="ApplyScanSettlementPlanAsync",
         must=["OcrSettlementProposal.FitsDocument("],
         must_re=[r"if\s*\(\s*fittingPlan\s+is\s+null\s*\)"],
         call_args=[("OcrSettlementProposal.DeferredNote(", "fittingPlan")],
         why="N5/C8: [PAY-AT-PAYMENT] ปลดการหยุดได้เฉพาะข้อเสนอที่ตรงยอดเอกสาร — ไม่มี/ขัดกัน/มี WHT ต้องคงการหยุด"),
    dict(file=DOC, method="CreatePaymentJournalAsync",
         must_re=[r"if\s*\(\s*thbCash\s*!=\s*0m\s*\)\s*pendingLines\s*\.\s*Add\s*\(\s*\(\s*cashAccount\s*\.\s*Id\s*,\s*0\s*,\s*thbCash"],
         why="C9: ข้ามขาเงินสดเฉพาะเมื่อเงินออก 0 (ปิดยอดด้วยบรรทัดปรับ) — ห้ามข้ามทั้งที่มีเงินออก"),
    dict(file=DOC, method="CreatePaymentAsync",
         must_re=[r"closesByAdjustmentOnly\s*=\s*request\s*\.\s*Amount\s*==\s*0m\s*&&\s*request\s*\.\s*SettlementAdjustments\s+is\s*\{\s*Count\s*:\s*>\s*0\s*\}",
                  r"request\s*\.\s*Amount\s*==\s*0\s*&&\s*!\s*closesByAdjustmentOnly",
                  r"doc\s*\.\s*WithholdingTaxAmount\s*>\s*0m\s*&&\s*!\s*closesByAdjustmentOnly"],
         why="C9/P-c: เงิน 0 ได้เฉพาะเมื่อมีบรรทัดปรับ · ไม่หัก ณ ที่จ่าย/ไม่ออก 50 ทวิ ในงวดที่ไม่มีเงินออก"),
    dict(file=SIGN, method="ApproveAsync",
         before=[("PreviewApprovalWarningsAsync(", "approval.Status = ApprovalStatus.Approved")],
         why="N6: ถามคำเตือนก่อนบันทึกลายเซ็นขั้นสุดท้าย (ไม่งั้นลายเซ็นครบแต่เอกสารค้างเงียบ)"),
    dict(file=SIGN, method="ExternalApproveQuotationAsync",
         must=["DocumentSignedContent.Hash(", "DocumentSignedContent.CanReuseSignature(",
               "SignedContentHash = contentHash", "DocumentSignedContent.Supersede("],
         must_lit=["FOR UPDATE"],
         call_args=[("DocumentSignedContent.CanReuseSignature(", "request.SignatureData")],
         before=[("PreviewApprovalWarningsAsync(", "_db.Set<DocumentSignature>().Add("),
                 ("DocumentSignedContent.CanReuseSignature(", "_db.Set<DocumentSignature>().Add("),
                 ("DocumentSignedContent.Supersede(", "_db.Set<DocumentSignature>().Add(")],
         why="N6 + R3-3: ถามคำเตือนก่อนเก็บลายเซ็น · ลายเซ็นเดิมใช้ซ้ำได้เฉพาะเนื้อหาเดิม (hash) + ลายเซ็นเดิม · "
             "ไม่งั้นแทนที่แล้วบันทึกใหม่ · ล็อกแถวเอกสารกันเรียกพร้อมกัน (B9)"),
    # ── รอบ 193 O1 หลังฝ่ายค้านรอบสี่ (R4-1): ทุกเส้นที่อ่านลายเซ็นลูกค้าตัดสินผ่าน DocumentSignedContent.IsSignatureCurrent ตัวเดียว ──
    dict(file=DOCSVC, method="ApproveDocumentAsync#2",
         must=["DocumentSignedContent.IsSignatureCurrent(", "DocumentSignedContent.Supersede(",
               "!staleCustomerSignatureIds.Contains(s.Id)"],
         before=[("DocumentSignedContent.IsSignatureCurrent(", "DocumentLineVatConvention.LinesStoredGross(")],
         why="R4-1: ด่านเซ็นครบไม่นับลายเซ็นลูกค้าที่เซ็นกับเนื้อหาก่อนแก้ (ตัดสินก่อนขั้นซ่อมบรรทัด) · อนุมัติแล้วลายเซ็นนั้นถูกแทนที่ในธุรกรรมเดียวกัน"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="ResolveSignersAsync",
         must=["DocumentSignedContent.IsSignatureCurrent("],
         why="R4-1: ช่องลายเซ็นลูกค้าบน PDF (ใช้ร่วม HTML + QuestPDF) พิมพ์เฉพาะลายเซ็นที่ยังมีผลกับเนื้อหา"),
    dict(file=SIGN, method="CheckAllApprovedAndProcessAsync",
         must=["DocumentSignedContent.IsSignatureCurrent("],
         before=[("DocumentSignedContent.IsSignatureCurrent(", "_docService.ApproveDocumentAsync(")],
         why="R4-1: ลายเซ็นครบทุกขั้นไม่นับขั้นลูกค้าที่เซ็นกับเนื้อหาก่อนแก้"),
    dict(file=SIGN, method="StaleSignatureApprovalIdsAsync",
         must=["DocumentSignedContent.IsSignatureCurrent("],
         why="R4-1: API สถานะเซ็นใช้ตัวตัดสินตัวเดียว"),
    dict(file=SIGN, method="GetDocumentWithApprovalsAsync",
         must=["StaleSignatureApprovalIdsAsync(", "SignatureStaleReason"],
         why="R4-1: หน้า/API สถานะเซ็นบอกเหตุผลเมื่อลายเซ็นลูกค้าไม่นับ และไม่ส่งภาพลายเซ็นนั้นเป็นลายเซ็นบนเอกสาร"),
    dict(file=SIGN, method="GetDocumentSignaturesAsync",
         must=["StaleSignatureApprovalIdsAsync("],
         why="R4-1: รายการลายเซ็นบนเอกสารไม่รวมลายเซ็นลูกค้าที่ไม่นับ"),
    dict(file=XTENANT, method="ApproveIncomingAsync",
         must=["DocumentSignedContent.Hash("],
         why="R4-1 ทางเข้าอื่น: ลายเซ็นคู่ค้าข้ามบริษัท (บทบาท Customer) ต้องบันทึก hash เนื้อหาที่เซ็น"),
]


# ── รอบ 193 ทีม L2 หลังฝ่ายค้านรอบสาม (review193-r3.md R3-1/R3-6/B1): ยอดที่พิมพ์ · ยิงซ้ำ · ตาข่าย void ──
PDF_HTML = "Services/Implementations/PdfGenerationService.cs"
PDF_QUEST = "Services/Implementations/PdfGenerationService.DocumentRenderer.cs"
IDEMPOTENT_VOIDED_FORBID = ["existing.Status != DocumentStatus.Voided"]
RULES += [
    dict(file=PDF_HTML, method="BuildDocumentHtml",
         must=["DepositPolicyResolver.BillDeductionTotal(", "DepositPolicyResolver.TaxedDepositDeducted(", "doc.DepositBaseDeducted"],
         forbid=["BillDeductionIsTaxedDeposit(", "doc.SubTotal + doc.BillDiscountAmount"],
         why="R3-1 HTML: สองแถวแยก (ส่วนลดการค้า / หักมูลค่ามัดจำ) · ยอดก่อนหักท้ายบิลรวมฐานมัดจำ — คู่กับ QuestPDF"),
    dict(file=PDF_QUEST, method="ComposeSummary",
         must=["DepositPolicyResolver.BillDeductionTotal(", "DepositPolicyResolver.TaxedDepositDeducted(", "doc.DepositBaseDeducted"],
         forbid=["BillDeductionIsTaxedDeposit(", "doc.SubTotal + doc.BillDiscountAmount"],
         why="R3-1 QuestPDF: สองแถวแยก — คู่กับ HTML (สอง renderer ห้าม drift)"),
    dict(file=PDF_QUEST, method="ComposeItemsTable",
         must=["DepositPolicyResolver.BillDeductionTotal("], forbid=["doc.SubTotal + doc.BillDiscountAmount"],
         why="R3-1 ยอดบรรทัดที่ scale กลับต้องรวมฐานมัดจำ (ไม่งั้นบรรทัดรวมไม่ได้ยอดก่อนหัก)"),
    dict(file=DOCSVC, method="LockDepositBalancesAsync", must_lit=["FOR UPDATE"],
         why="B2 คำสั่งล็อกแถวต้องเป็น FOR UPDATE จริง (ไม่ใช่ SELECT เปล่า) — รอบ 194 R3-2 อยู่ในตัวล็อกตัวเดียว"),
    dict(file="Data/DatabaseMigrationHelper.cs", method="GetFullTextSearchStatements",
         must=["DepositBaseSplitMigrationSql("],
         why="R4-3 สร้างคอลัมน์ DepositBaseDeducted + ย้ายค่าเดิมผ่านคำสั่งครั้งเดียว (ห้าม UPDATE ย้ายค่าที่รันทุกบูต — เทสต์ DepositBaseSplitMigrationTests ล็อกเนื้อ SQL)"),
    dict(file="Data/DatabaseMigrationHelper.cs", method="DepositBaseSplitMigrationSql",
         must_lit=["information_schema.columns", "pg_advisory_xact_lock(", "DepositBaseSplitLockKey"],
         why="R4-3 ย้ายเฉพาะตอนสร้างคอลัมน์ · ล็อกคีย์คงที่ (สองเครื่องบูตพร้อมกันย้ายครั้งเดียว)"),
    dict(file=CLONE, method="Clone",
         must=["BillDiscountAmount:"], forbid=["DepositBaseDeducted", "DepositAppliedRef"],
         why="R3-1 ใบโคลน = การขายใหม่: ส่วนลดการค้าตาม · ฐานมัดจำ/เลขใบมัดจำห้ามตาม (มัดจำใช้แล้ว ⇒ หักซ้ำ)"),
    dict(file=INTEGRATION, method="ProcessInvoiceAsync",
         must_re=[r"DocumentType\s*\.\s*TaxInvoice\s*&&\s*d\s*\.\s*Status\s*!=\s*DocumentStatus\s*\.\s*Voided",
                  r"VoidDocumentAsync\s*\(\s*companyId\s*,\s*voidedId\s*\)[\s\S]*?catch\s*\(\s*Exception\s+exVoid\s*\)[\s\S]*?document\s*\.\s*InternalNotes\s*="],
         forbid=IDEMPOTENT_VOIDED_FORBID,
         why="R3-6 ยิงซ้ำหาใบที่ยังไม่ยกเลิกในคิวรีเอง · B1 ยกเลิกก่อน แล้วค่อยประทับหมายเหตุ 'ยกเลิกอัตโนมัติ' (ยกเลิกล้ม = ล้มดังด้วยข้อความจริง)"),
] + [
    dict(file=INTEGRATION, method=m,
         must_re=[r"DocumentType\s*\.\s*" + t + r"\s*&&\s*d\s*\.\s*Status\s*!=\s*DocumentStatus\s*\.\s*Voided"],
         forbid=IDEMPOTENT_VOIDED_FORBID,
         why="R3-6 (คลาสเดียวกัน) ใบ Voided ของ ExternalRef เดียวกันต้องไม่ทำให้สร้างซ้ำ")
    for (m, t) in [("ProcessCreditNoteAsync", "CreditNote"), ("ProcessDebitNoteAsync", "DebitNote"),
                   ("ProcessExpenseAsync", "Expense"), ("ProcessPaymentVoucherAsync", "PaymentVoucher"),
                   ("ProcessCertificateInLieuAsync", "CertificateInLieu")]
]

# ── รอบ 200 ทีม R (กวาดค้างรอบ 189): ด่านที่เทสต์ล็อกแค่ helper — ล็อกจุดเรียกใน service/controller ──
RULES += [
    dict(file=PDF_HTML, method="BuildDocumentHtml",
         must=["HtmlImageSource.Attribute("],
         must_lit=["HtmlEncode(doc.DisplayReference)", "HtmlEncode(doc.Contact.TaxId)", "HtmlEncode(company.TaxId)",
                   "HtmlEncode(bankTextForLang)", "HtmlEncode(watermark ?? template.WatermarkText",
                   "HtmlEncode(contactSectionLabel)"],
         why="G2-02/G2-11: ช่องที่ผู้ใช้/คู่ค้า/OCR คุมได้ (อ้างอิง · เลขภาษี · โลโก้/ตรา/ลายเซ็น · ลายน้ำ · ข้อมูลชำระเงิน) ต้องหนีก่อนต่อเข้า HTML"),
    dict(file=PDF_HTML, method="BuildReceiptHtml",
         must_lit=["HtmlEncode(payment.Reference)", "HtmlEncode(payment.PaymentMethod"],
         why="G2-02: อ้างอิง/วิธีชำระของการรับชำระมาจากผู้ใช้/คู่ค้า"),
    dict(file=DOC, method="PurgeDocumentAsync#2",
         must=["DocumentRetention.EffectiveUntil(", "DocumentRetention.InRetention("],
         before=[("DocumentRetention.InRetention(", "ExecuteDeleteAsync(")],
         forbid=["doc.RetentionUntil.HasValue"],
         why="B-06: ใบที่ออกจาก POS/API/นำเข้า (RetentionUntil = null) ต้องถูกกันลบถาวรด้วยวันที่คำนวณ ไม่ใช่ข้ามเงียบ"),
    dict(file=DOC, method="ApproveDocumentAsync#2",
         must=["DocumentRetention.ComputeUntil("], forbid=["basis.AddYears(5)"],
         why="B-06: สูตรระยะเก็บตัวเดียวกับด่านลบถาวร"),
    dict(file=DOC, method="ApproveDocumentAsync#2",
         must=["AdjustmentNoteAccount.SourceIsPurchaseSide("],
         forbid=["or DocumentType.Expense or DocumentType.GoodsReceiptNote"],
         why="B-07: ด่าน §90/2 ใช้ชุดใบต้นทางฝั่งซื้อตัวเดียว (PV/CIL ด้วย) — ห้ามพิมพ์มือซ้ำ"),
    dict(file=DOC, method="ApplySection65TerAsync",
         must=["FiscalYear.RangeFor("], forbid=["new DateTime(doc.DocumentDate.Year, 1, 1)"],
         why="B-10: เพดานค่ารับรอง §65 ตรี(4) + (10) รายจ่ายรอบก่อน คิดต่อรอบบัญชี ไม่ใช่ปีปฏิทิน"),
    dict(file=LODGING_RES, method="GetPublicInfoAsync",
         must=["LodgingPublicProjection.RoomTypes("],
         why="F-05: endpoint สาธารณะห้ามส่งรายการห้อง (เลขห้อง/หมายเหตุภายใน/สถานะแม่บ้าน)"),
    dict(file="Services/Implementations/ThaiGovIntegrationService.cs", method="GetCurrentSsoRateAsync",
         must=["SsoRateSchedule.GetDefault(", "SsoWageBase.MinBase"], forbid=["15000m", "1650m"],
         why="D-03: เพดาน ปกส. ของ API อ่านจากตารางกฎหมายตัวเดียวกับเครื่องคำนวณเงินเดือน"),
    dict(file="Services/Implementations/Payroll/TipPayoutService.cs", method="DistributeAndPayoutAsync",
         must=["TipShareAllocation.Split(", "TipShareAllocation.Withholding(", "TipShareAllocation.WhtRatePercent"],
         forbid=["WhtThreshold", "totalTip * pct / 100m"],
         why="D-05: ส่วนแบ่งรวม = กองทิป (JE สมดุล) · อัตรา/เกณฑ์ WHT จาก ThaiWhtRateTable"),
    dict(file=DUPDET, method="DetectEmployeeAsync",
         must=["EncryptedIdMatch.Same("], forbid=["e.CitizenId == idCard"],
         why="G2-06: คอลัมน์เข้ารหัส nonce สุ่ม เทียบใน SQL ไม่มีวันเจอ — ต้องถอดรหัสแล้วเทียบ"),
    dict(file=PAYROLL, method="GeneratePnd1Async", must=["PiiMask.CitizenId("],
         why="G2-05: รายงาน ภ.ง.ด.1 ปิดบังเลขบัตรสำหรับผู้ไม่มี Pii.View"),
    dict(file=PAYROLL, method="GenerateSsoReportAsync", must=["SsoInsuredNumber.ForDisplay("],
         call_args=[("SsoInsuredNumber.ForDisplay(", "includePii")],
         forbid=["PiiMask.CitizenId(d.Employee.SocialSecurityNumber)"],
         why="G2-05 + R200-X6: รายงาน ปกส. ปิดบังเลขสำหรับผู้ไม่มี Pii.View · เลข ปกส. จากตัวตัดสินเดียวกับไฟล์ สปส.1-10"),
    dict(file="Controllers/PayrollController.cs", method="GetPnd1", must=["CanViewPiiAsync("],
         call_args=[("_service.GeneratePnd1Async(", "includePii")],
         why="G2-05: ด่าน PII (Pii.View + PiiAccessLog) ไม่ใช่แค่ด่านเงินเดือน"),
    dict(file="Controllers/PayrollController.cs", method="GetSso", must=["CanViewPiiAsync("],
         call_args=[("_service.GenerateSsoReportAsync(", "includePii")],
         why="G2-05: ด่าน PII ของรายงาน ปกส."),
    dict(file="Controllers/PayrollController.cs", method="SsoLateFeePreview",
         must=["CheckPayrollAccessAsync(", "SsoLateFee.Compute("],
         why="D-04: หน้าเว็บ preview เงินเพิ่ม สปส. ด้วยสูตรเดียวกับตอนลงบัญชี"),
    dict(file=PAYROLL, method="ComputeSsoLateFee", must=["SsoLateFee.Compute("], forbid=["0.02m"],
         why="D-04: สูตรเงินเพิ่ม §49 อยู่ที่ Helpers/SsoLateFee ตัวเดียว (ห้ามสำเนา)"),
    dict(file=PAYROLL, method="CalculatePayrollAsync",
         must=["PayrollIncomeBase.PriorTaxBase(", "PayrollIncomeBase.PvdContribution("],
         must_re=[r"var\s+ytdIncome\s*=\s*cumulativeTaxable\b"],
         forbid=["emp.BaseSalary * emp.ProvidentFundEmployeePercent"],
         why="D-09 ฐานภาษีสะสมใช้ฐานภาษีของงวดก่อน · D-10 PVD จากเงินเดือนที่จ่ายจริง + ปัดเศษ"),
    dict(file=DOC, method="CollectApprovalWarningsAsync",
         must=["VatPeriodDeclaredOrFiledAsync(", "AdjustmentNoteAccount.ResolveSide("],
         why="B-09: อนุมัติใบขายเข้าเดือนที่ยื่น ภ.พ.30 แล้วต้องเตือน (สมมาตรกับด่านยกเลิก/กู้คืน)"),
    dict(file=DOC, method="IssueForfeitTaxInvoiceAsync",
         must=["DepositKindDocumentRules.ShouldClearStalePaymentDate("],
         forbid=["draft.Status == DocumentStatus.Draft"],
         must_re=[r"draft\s*\.\s*PaymentDate\s*=\s*null\s*;"],
         before=[("draft.PaymentDate = null", "ApproveDocumentAsync(")],
         why="review194-r4 P4-1: ใบร่างของการริบที่ค้างจากรุ่นก่อนต้องล้างวันรับเงินก่อนอนุมัติ (tax point = วันที่ใบ)"),
    dict(file="Controllers/DocumentTemplateController.cs", method="GeneratePdf", must=["DenySensitiveAsync("],
         before=[("DenySensitiveAsync(", "_pdfService.GenerateDocumentPdfAsync(")],
         why="review193-r4 P4-7: PDF ของเอกสารลับต้องผ่านด่านชั้นความลับก่อนสร้างไฟล์"),
    dict(file="Controllers/DocumentTemplateController.cs", method="GenerateHtml", must=["DenySensitiveAsync("],
         before=[("DenySensitiveAsync(", "_pdfService.GenerateDocumentHtmlAsync(")],
         why="review193-r4 P4-7: HTML พิมพ์ของเอกสารลับ ด่านเดียวกัน"),
    dict(file="Services/Implementations/RolePermissionService.cs", method="SeedDefaultRolesAsync",
         must=["EnsureOwnerAccessAsync(", "existingNames.Contains("],
         before=[("EnsureOwnerAccessAsync(", "_db.CompanyRoles.Add(")],
         why="G2-08: สร้าง Role เริ่มต้นต้องเป็นเจ้าของ + กดซ้ำไม่สร้าง Role ซ้ำ"),
    dict(file="Services/Implementations/ProductService.cs", method="AdjustStockAsync",
         must=["_stock.MoveAsync("], forbid=["onHand + qty < 0"],
         why="E-08: ด่านสต็อกติดลบตัวเดียวที่ ledger (เคารพค่าตั้ง AllowNegativeStock) — ห้ามด่านที่สองที่ไม่อ่านค่าตั้ง"),
    dict(file=PAYROLL, method="GenerateAnnualEmployeeWhtCertsAsync",
         must=["AddChainedAuditLog(", "PiiMask.CitizenId("], forbid=["_db.AuditLogs.Add("],
         why="D-11: audit ห้ามเก็บเลขบัตรเต็ม + ต้องอยู่ใน hash chain"),
    dict(file="Controllers/V1/OcrV1Controller.cs", method="Confirm",
         must=["OcrAiLabelScope.AcceptedAi("], forbid=["acceptedAi: false"],
         why="C-04: ยืนยันผ่าน API ต้องบอกว่ารับคำตอบครูไหมตามจริง (เดิม false ทุกครั้ง)"),
    dict(file="Controllers/AiSuggestionController.cs", method="ExplainAnomaly",
         # รอบ 200 ทีม Z (RF-6): Coerce + ReadStructured ย้ายเข้า AnomalyExplainVerdict.View ตัวเดียวของสองทางเข้า
         must=["AnomalyExplainVerdict.ShouldPersist(", "AnomalyExplainVerdict.View(", "anomaly.AiReasoning = view.Reasoning"],
         forbid=["resp.UsedAi && !string.IsNullOrEmpty(resp.PrimaryAnswer)", "anomaly.AiReasoning = resp.Reasoning;",
                 "AnomalyExplainStudent.ReadStructured("],
         why="H-3 + R200-X2/X8: นักเรียนตอบต้องบันทึกได้ (kill-switch) พร้อมคำอธิบายจริงของนักเรียน · คำตอบนอกชุดแปลงเข้าชุดแล้วเก็บ (ไม่ทิ้ง)"),
    dict(file="Services/Implementations/FixedAssetService.cs", method="ImportAsync",
         must=["FixedAssetImportMethod.Resolve("],
         why="E-03: นำเข้าเดินด่านที่ดิน/งานระหว่างก่อสร้างเดียวกับเส้นสร้างด้วยมือ"),
    dict(file="Services/Implementations/FixedAssetService.cs", method="UpdateAsync",
         must=["FixedAssetValuationEdit.Problem("],
         before=[("FixedAssetValuationEdit.Problem(", "RemoveRange(planned)")],
         why="A12/E-04: อายุ/ซาก/วิธีคิดที่ส่งมาต้องมีผล (หรือถูกปฏิเสธดัง) — ห้าม 'แก้ไขสำเร็จ' เงียบ"),
    dict(file="Services/Implementations/TaxFilingExportService.cs", method="ExportSso110Async",
         must=["SsoInsuredNumber.Resolve(", "SsoInsuredNumber.MissingNotice("],
         why="D-06: ช่องเลขประกันสังคมว่าง ⇒ เลขบัตรที่ถูกต้อง · ไม่มีทั้งคู่ = เตือนในสรุป"),
    dict(file="Services/Implementations/TaxFilingExportService.cs", method="ExportSps609Async",
         must=["SsoInsuredNumber.Resolve(", "SsoInsuredNumber.MissingNotice("],
         why="D-06: คลาสเดียวกันใน สปส.6-09"),
    dict(file=APPROVAL, method="CreateRuleAsync", must=["ValidateRuleAsync("],
         before=[("ValidateRuleAsync(", "_db.ApprovalRules.Add(")],
         why="A07/G2-09: ผู้อนุมัติต้องเป็นสมาชิกบริษัท · ตรวจก่อนบันทึก"),
    dict(file=APPROVAL, method="UpdateRuleAsync", must=["ValidateRuleAsync("],
         before=[("ValidateRuleAsync(", "RemoveRange(rule.Steps)")],
         why="A07/G2-09: แก้กฎใช้ด่านเดียวกับสร้าง"),
    dict(file=APPROVAL, method="ValidateRuleAsync", must=["ApprovalRuleValidation.Problem(", "cu.CompanyId == companyId"],
         why="G2-09: รายชื่อสมาชิกกรองบริษัทนี้"),
]

# ── รอบ 200 ทีม RF (แก้ผลฝ่ายค้านทีม R · review200-R.md): X1 หัวเอกสาร/CSS ของเทมเพลต + ด่านบันทึก · X2/X8 นักเรียนอธิบายรายการผิดปกติ ·
#    X3 แก้กฎอนุมัติไม่ล้างช่องที่ไม่ได้ส่ง · X5 ไม่ใช่เจ้าของ = 403 · (X4 · X6 แก้แถวเดิมด้านบน) ──
TPL_CTRL = "Controllers/DocumentTemplateController.cs"
PDF_QUEST = "Services/Implementations/PdfGenerationService.DocumentRenderer.cs"
RAW_TEMPLATE_CSS = ["{t.FontFamily}", "{t.BodyFontSize}", "{t.TitleFontSize}", "{t.PrimaryColor}", "{t.AccentColor}",
                    "{t.HeaderBackgroundColor}", "{t.TableHeaderColor", "{t.TableHeaderTextColor", "{t.TableStripedColor}",
                    "{t.PaperSize}", "{t.Orientation"]
RULES += [
    dict(file=PDF_HTML, method="BuildDocumentHtml",
         must=["WebUtility.HtmlEncode(ComputeDocumentTitle("],
         why="R200-X1: หัวเอกสาร (CustomTitle/CustomTitleEn/DocumentTitleOverridesJson) มาจากผู้แก้เทมเพลต/ค่าตั้ง — หนีก่อนต่อเข้า HTML"),
    dict(file=PDF_HTML, method="BuildCss",
         must=["DocumentTemplateStyle.Font(", "DocumentTemplateStyle.Color(", "DocumentTemplateStyle.Hex(",
               "DocumentTemplateStyle.BodyFontSize(", "DocumentTemplateStyle.TitleFontSize(",
               "DocumentTemplateStyle.PaperSize(", "DocumentTemplateStyle.Orientation("],
         forbid_lit=RAW_TEMPLATE_CSS,
         why="R200-X1: ค่าเทมเพลตทุกช่องที่เข้า <style> ผ่านตัวตรวจตัวเดียว — ค่าดิบปิด </style> แล้วยิงสคริปต์ได้"),
    dict(file=PDF_HTML, method="BuildLayoutCss",
         must=["DocumentTemplateStyle.Color(", "DocumentTemplateStyle.TitleFontSize("],
         forbid=["var accent = t.AccentColor;"], forbid_lit=["{t.TitleFontSize}", "{t.AccentColor}"],
         why="R200-X1: CSS ของโครงหน้าใช้สี/ขนาดที่ผ่านตัวตรวจแล้ว"),
    dict(file=PDF_HTML, method="NormalizeFont", must=["DocumentTemplateStyle.Font("],
         why="R200-X1: QuestPDF ใช้รายการฟอนต์อนุญาตชุดเดียวกับ HTML (สอง renderer ห้าม drift)"),
    dict(file=PDF_QUEST, method="ResolvePageSize",
         must=["DocumentTemplateStyle.PaperSize(", "DocumentTemplateStyle.Orientation("],
         why="R200-X1: ขนาด/แนวกระดาษ QuestPDF = ตัวตรวจเดียวกับ @page ของ HTML"),
    dict(file=PDF_QUEST, method="RenderDocumentPdfNative",
         must=["DocumentTemplateStyle.BodyFontSize("], forbid=["float.TryParse(template.BodyFontSize"],
         why="R200-X1: ขนาดเนื้อความ QuestPDF = ตัวตรวจเดียวกับ HTML"),
    dict(file=TPL_CTRL, method="Create",
         must=["StyleRejection("], before=[("StyleRejection(", "_templateService.CreateAsync(")],
         why="R200-X1: ตรวจค่าหน้าตาเทมเพลตตอนบันทึก (400 + ข้อความไทย) ก่อนเขียน"),
    dict(file=TPL_CTRL, method="Update",
         must=["StyleRejection("], before=[("StyleRejection(", "_templateService.UpdateAsync(")],
         why="R200-X1: ตรวจค่าหน้าตาเทมเพลตตอนแก้ ด่านเดียวกับสร้าง"),
    dict(file=TPL_CTRL, method="StyleRejection", must=["DocumentTemplateStyle.RejectReasons("],
         why="R200-X1: ตัวตรวจตอนบันทึก = ตัวเดียวกับตอน render"),
    dict(file="Services/Ai/Distillation/AnomalyExplanationDistillationModel.cs", method="PredictAsync",
         must=["AnomalyExplainStudent.ReadPrompt(", "AnomalyExplainStudent.Decide(", "AmountAnomalyDetector.CheckModifiedZScore(", "StructuredJson ="],
         forbid=["ExtractInput("],
         why="R200-X2: นักเรียนอ่าน payload ของ prompt จริง · ตอบค่าในชุด · คำอธิบายไปทาง StructuredJson (kill-switch ได้คำอธิบายครบ)"),
    dict(file=APPROVAL, method="UpdateRuleAsync",
         must=["ApprovalRuleValidation.PatchDescription(", "ApprovalRuleValidation.PatchProjectId("],
         forbid=["rule.Description = request.Description;", "rule.ProjectId = request.ProjectId;"],
         why="R200-X3: แก้กฎแล้วช่องที่ไม่ได้ส่ง (คำอธิบาย/โครงการ) ต้องคงเดิม — ห้ามเขียนทับด้วย null เงียบ ๆ"),
    dict(file=APPROVAL, method="ValidateRuleAsync", must=["_db.Projects", "p.CompanyId == companyId"],
         why="R200-X3: ขอบเขตโครงการของกฎต้องเป็นโครงการของบริษัทนี้"),
    dict(file="Services/Implementations/RolePermissionService.cs", method="EnsureOwnerAccessAsync",
         must=["OwnerActionGuard.NotOwner("], forbid=["new UnauthorizedAccessException("],
         why="R200-X5: ไม่ใช่เจ้าของ = 403 + ข้อความ (401 ทำให้หน้าเว็บลบ token เด้งออกจากระบบ)"),
    dict(file="Services/Implementations/CompanyService.cs", method="EnsureOwnerAccessAsync",
         must=["OwnerActionGuard.NotOwner("], forbid=["new UnauthorizedAccessException("],
         why="R200-X5: ด่านเจ้าของของ CompanyService (17 ผู้เรียก) — 403 ตัวสร้างเดียวกับ RolePermissionService"),
]

# ── รอบ 193 ทีม W หลังฝ่ายค้านรอบสอง (W2-C2): audit hash chain — เรพไม่มี PostgreSQL ในเทสต์ ⇒ เทสต์เรียก Seal/Analyze ตรง
# และ "ย้อนเส้นเขียนกลับไปสูตรเก่า/ถอด ResolveTip" เทสต์ยังเขียว ⇒ ล็อกการต่อสายของเส้นเขียน/ตรวจที่นี่ (สูตร v1 เป็น private แล้ว) ──
AUDIT_CTX = "Data/AccountingDbContext.cs"
AUDIT_SVC = "Services/Implementations/AuditTrailService.cs"
AUDIT_CTRL = "Controllers/AuditTrailController.cs"
AUDIT_JOB = "Services/Background/AuditChainVerifyJob.cs"
AUDIT_HASH_FORBID = ["SHA256", "ComputeRowHash(", "ToHexString("]
RULES += [
    dict(file=AUDIT_CTX, method="ApplyAuditHashChain",
         must=["AuditHashChain.Seal(", "AuditHashChain.ResolveTip("],
         before=[("AuditHashChain.ResolveTip(", "AuditHashChain.Seal(")],
         forbid=AUDIT_HASH_FORBID + ["e.RowHash ="],
         why="W2-C2 เส้นเขียน audit มีทางเดียว: ปลาย chain จาก ResolveTip (รวมแถวที่รอบันทึก) แล้ว Seal สูตร v2 — ห้ามประกอบ hash เอง"),
    dict(file=AUDIT_CTX, method="AddChainedAuditLog",
         must=["ApplyAuditHashChain("], before=[("ApplyAuditHashChain(", "AuditLogs.Add(")],
         why="W2-C2 แถว audit ที่เขียนตรงต้องถูกประทับก่อน Add (ไม่งั้น RowHash=null อยู่นอก chain)"),
    dict(file=AUDIT_CTX, method="SaveChangesAsync",
         must=["ApplyAuditHashChain("], before=[("ApplyAuditHashChain(", "AuditLogs.AddRange(")],
         why="W2-C2 แถว audit จาก ChangeTracker ต้องถูกประทับก่อนบันทึก"),
    dict(file=AUDIT_CTX, method="SaveChanges",
         must=["ApplyAuditHashChain("], before=[("ApplyAuditHashChain(", "AuditLogs.AddRange(")],
         why="W2-C2 เส้น sync ต้องประทับเหมือนเส้น async"),
    dict(file=AUDIT_SVC, method="VerifyHashChainAsync",
         must=["AuditHashChain.Analyze(", "AuditHashChain.AlertMessage("],
         forbid=AUDIT_HASH_FORBID + ["PrevHash !="],
         why="W2-C1/C2 ฝั่งตรวจตัวเดียว (แยก ถูกแก้/ขาดตอน/แตกกิ่ง · รายงานทุกแถว) — ห้ามเดิน chain/ประกอบ hash เอง"),
    dict(file=AUDIT_CTRL, method="VerifyHashChain",
         must=["AuditHashChain.Analyze("],
         forbid=AUDIT_HASH_FORBID + ["PrevHash !="],
         why="W2-C2 endpoint ตรวจใช้ตัวตรวจกลาง (เดิมมีสำเนา canonical ของตัวเอง)"),
    dict(file=AUDIT_JOB, method="RunCycleAsync",
         must=["result.AlertMessage", "result.ForkCount"],
         why="W2-C1 ข้อความถึงลูกค้าตามสาเหตุที่ตรวจพบจริง · fork แยกรายงาน ไม่แจ้งว่า \"ถูกแก้\""),
    dict(file="Controllers/PaymentSettingsController.cs", method="SetMode",
         must=["AddChainedAuditLog("], forbid=["AuditLogs.Add("],
         why="W2-C3 ร่องรอยการเปิดรับเงินจริงต้องอยู่ใน hash chain"),
]

# ── รอบ 194 ทีม A (แกนกลาง · spec S8): ประเภทเงินมัดจำเริ่มต้น — tenant ใหม่ห้ามว่าง · จุดสร้างบริษัททุกจุดต้อง seed หลัง
#    `_db.Companies.Add(company)` (FindAsync หาบริษัทที่ยังไม่บันทึกจาก change tracker) · ทีม B/C/D เติมกติกาของตัวเองต่อท้ายบล็อกนี้
#    (DocumentService ต้องเรียก DepositDocumentShaping.Apply/ResolveKind · ด่าน SecurityDeductionProblem · ริบผ่าน ForfeitVatDecision) ──
_DEPOSIT_SEED_WHY = "รอบ 194 S8 บริษัทใหม่ต้องได้ประเภทเงินมัดจำเริ่มต้นจากตารางเดียว (DepositKindSeed) — ขาด = ฟอร์มมัดจำไม่มีประเภทให้เลือก"
RULES += [
    dict(file=f, method=m, must=["DepositKindSeed.EnsureSeededAsync("],
         before=[("_db.Companies.Add(company)", "DepositKindSeed.EnsureSeededAsync(")], why=_DEPOSIT_SEED_WHY)
    for f, m in (("Services/Implementations/CompanyService.cs", "CreateAsync"),
                 ("Services/Implementations/AuthService.cs", "RegisterAsync"),
                 ("Services/Implementations/AuthService.cs", "SsoLoginAsync"))
]

# ── รอบ 194 ทีม B (เส้นเอกสาร · spec S2/S3/S4): ใบมัดจำที่ระบุประเภท ⇒ ตัวตัดสินตัวเดียวจัดรูปใบก่อนเฉลี่ยส่วนหักท้ายบิล ·
#    เงินประกันห้ามหักเป็นฐาน/ราคาทุกเส้น (DEP-SEC-DEDUCT) · ริบโดยไม่มีใบสุดท้ายผ่าน ForfeitVatDecision ·
#    มัดจำเต็มยอดที่เป็นราคา ⇒ ออกใบกำกับของยอดที่ริบแล้วตัดชำระด้วยมัดจำ (ห้ามลงรายได้ไม่มี VAT เงียบ ๆ) ──
_DEP_KIND_WHY = "รอบ 194 S4 ใบมัดจำที่ระบุประเภทต้องผ่าน ResolveKind + DepositDocumentShaping.Apply ก่อนคิดยอด — ไม่งั้น VAT/ธง/บัญชีตาม payload"
_DEP_SEC_WHY = "รอบ 194 S2 DEP-SEC-DEDUCT เงินประกันที่ต้องคืนไม่ใช่ราคา — หักเป็นฐานภาษี/ราคาไม่ได้ทุกเส้น (ตัดชำระหนี้หลังอนุมัติยังได้)"
RULES += [
    dict(file=DOC, method=m,
         must=["ResolveDocumentDepositKindAsync(", "DepositDocumentShaping.Apply(", "GuardSecurityDepositDeductionAsync("],
         before=[("DepositDocumentShaping.Apply(", "AllocateBillDeductions(")], why=_DEP_KIND_WHY)
    for m in ("CreateDocumentAsync", "UpdateDocumentAsync")
] + [
    dict(file=DOC, method="ResolveDocumentDepositKindAsync",
         must=["DepositPolicyResolver.ResolveKind("],
         must_re=[r"k\.CompanyId\s*==\s*companyId", r"k\.IsActive"],
         why="รอบ 194 S4 ประเภทต้องเป็นของบริษัทนี้และเปิดใช้ — ตัดสินด้วย ResolveKind ตัวเดียว"),
    dict(file=DOC, method="GuardSecurityDepositDeductionAsync",
         must=["DepositPolicyResolver.SecurityDeductionProblemForRefs("], why=_DEP_SEC_WHY),
    dict(file=DOC, method="LoadTaxedDepositsByRefAsync",
         must=["DepositPolicyResolver.SecurityDeductionProblemForRefs("], why=_DEP_SEC_WHY),
    dict(file=DOC, method="GuardDrivesGrossApplyAsync",
         must=["DepositPolicyResolver.SecurityDeductionProblem("], why=_DEP_SEC_WHY),
    dict(file=DOC, method="RealizeDepositCoreAsync",
         must=["DepositPolicyResolver.ForfeitVatDecision(", "IssueForfeitTaxInvoiceAsync("],
         before=[("DepositPolicyResolver.ForfeitVatDecision(", "_db.JournalEntries.Add(je)")],
         why="รอบ 194 S3 VAT ของการริบตามลักษณะเงิน (ตัวตัดสินตัวเดียว) ก่อนลง JE รับรู้ — มัดจำเต็มยอดที่เป็นราคาต้องออกใบกำกับ"),
    dict(file=DOC, method="IssueForfeitTaxInvoiceAsync",
         must=["CreateDocumentAsync(", "ApproveDocumentAsync(", "ApplyDepositToInvoiceAsync("],
         before=[("CreateDocumentAsync(", "ApproveDocumentAsync("), ("ApproveDocumentAsync(", "ApplyDepositToInvoiceAsync(")],
         why="รอบ 194 S3 ยอดที่ริบต้องมีใบกำกับจริง (เส้นสร้าง/อนุมัติเดิม) แล้วตัดชำระด้วยมัดจำผ่านเส้นเดิม — ไม่ลง JE เองแยก"),
    dict(file=CLONE, method="Clone",
         must=["IsDeposit = true", "DepositKindId ="],
         why="รอบ 194 โคลนใบมัดจำต้องเป็นใบมัดจำ (เดิมหาย ⇒ รายได้แทนหนี้สินมัดจำ) พร้อมประเภท — ใบใหม่ถูกตัดสินใหม่ผ่าน CreateDocumentAsync"),
]

# ── รอบ 194 ทีม C (ตั้งค่า + ช่องทาง · spec S2/S5/S6 C-1): ด่านบันทึกประเภท · ยกเลิกการจองห้าม void ใบมัดจำ ·
#    integration คำนวณ mismatch ผ่าน ResolveKind (ตัวตัดสินเดียว) + ปฏิเสธรหัสที่ไม่รู้จัก/หักเงินประกันแบบขับ JE ──
DKSVC = "Services/Implementations/DepositKindService.cs"
CMS_BOOK = "Services/Implementations/CmsBookingService.cs"
_DK_SAVE_WHY = "รอบ 194 S2 บันทึกประเภทเงินมัดจำต้องผ่าน KindProblem (ราคา+เลื่อน VAT ต้องมีเหตุผล · ค่าขยะ enum) — ขาด = ประเภทผิดกฎหมายถูกบันทึกเงียบ"
_CMS_C1_WHY = ("รอบ 194 S6 C-1 ยกเลิกการจองห้าม void ใบมัดจำที่ออกแล้ว (ลบภาษีขายเดือนที่รับเงินย้อนหลัง · §86/4) — "
               "ต้องตัดสินด้วย CmsBookingCancelPolicy ก่อนแตะ VoidDocumentAsync")
RULES += [
    dict(file=DKSVC, method="ApplyAsync", must=["DepositPolicyResolver.KindProblem("], why=_DK_SAVE_WHY),
    dict(file=DKSVC, method="CreateAsync", must=["ApplyAsync(", "DepositKindCatalog.CodeProblem("], why=_DK_SAVE_WHY),
    dict(file=DKSVC, method="UpdateAsync", must=["ApplyAsync("], why=_DK_SAVE_WHY),
    # ยกเลิก: ตัดสินก่อน void · ตัวเมธอดสถานะห้ามเรียก void/realize ตรง (ต้องผ่านเมธอดที่ตัดสินแล้ว)
    dict(file=CMS_BOOK, method="SettleErpDocumentOnCancelAsync", must=["CmsBookingCancelPolicy.DecideOnCancel("],
         before=[("CmsBookingCancelPolicy.DecideOnCancel(", "VoidDocumentAsync(")], why=_CMS_C1_WHY),
    dict(file=CMS_BOOK, method="UpdateBookingStatusAsync", must=["SettleErpDocumentOnCancelAsync(", "RealizeDepositOnCompleteAsync("],
         forbid=["VoidDocumentAsync(", "RealizeDepositAsync("], why=_CMS_C1_WHY),
    dict(file=CMS_BOOK, method="RealizeDepositOnCompleteAsync", must=["CmsBookingCancelPolicy.DecideOnComplete("],
         before=[("CmsBookingCancelPolicy.DecideOnComplete(", "RealizeDepositAsync(")],
         why="รอบ 194 มัดจำเต็มยอดของบริษัทที่จด VAT ห้ามรับรู้ตรงเข้ารายได้ (= รายได้ไม่มี VAT ไม่มีใบกำกับ) — ตัดสินก่อนรับรู้"),
    # CMS ส่งประเภทเฉพาะบริษัทที่ตั้งค่าแล้ว (ไม่งั้นพฤติกรรมเดิมทุกตัวอักษร)
    dict(file=CMS_BOOK, method="SyncBookingToErpAsync", must=["DepositKindCatalog.LoadContextAsync(", "DepositKindCatalog.PrePaymentKindId("],
         forbid=["kindCtx.DefaultKind"],
         why="รอบ 194 S4 CMS ส่ง DepositKindId เฉพาะบริษัทที่ตั้งค่ามัดจำเองแล้ว (ส่งเสมอ = บริษัทที่ไม่เคยตั้งได้ใบรูปใหม่เงียบ ๆ) · "
             "ฝ่ายค้าน C1: ค่าเริ่มต้นที่เป็นเงินประกันห้ามส่ง (ใบจองได้ลักษณะเงินประกัน ⇒ ใช้บริการแล้วไม่รับรู้รายได้) — ตัดสินที่ PrePaymentKindId ตัวเดียว"),
    # integration: mismatch ผ่านตัวตัดสินประเภทตัวเดียว (ห้ามกลับไปใช้ Resolve เดิมที่ไม่รู้จักประเภท)
    dict(file=INTEGRATION, method="DepositTreatmentMismatchNoteAsync", must=["DepositKindCatalog.Decide("],
         forbid=["DepositPolicyResolver.Resolve("], why="รอบ 194 S5 หมายเหตุ mismatch คำนวณผ่าน ResolveKind (ประเภทที่คู่ค้าระบุ/ประเภทเริ่มต้น)"),
    dict(file=INTEGRATION, method="DepositKindPayloadRejectionAsync",
         must=["DepositKindCatalog.UnusableCodeMessage(", "DepositPolicyResolver.SecurityDeductionProblem(",
               "DepositPolicyResolver.SecurityDeductionProblemForRefs("],
         why="รอบ 194 S5/S2 depositKindCode ที่ไม่รู้จัก ⇒ 400 (ไม่ตกเงียบ) · หักเงินประกันแบบขับ JE ⇒ DEP-SEC-DEDUCT"),
    dict(file=INTEGRATION, method="ProcessInvoiceAsync", must=["DepositKindPayloadRejectionAsync("],
         before=[("DepositKindPayloadRejectionAsync(", "TaxedDrivesRejectionAsync(")],
         why="รอบ 194 S5 ตรวจรหัสประเภทก่อนทุกเส้น (idempotent/resync/สร้างใหม่) — ไม่มีการออกเลข"),
]

# ── รอบ 194 ทีม D (ที่พัก · spec S2/S3/S4): เงินประกันความเสียหาย ≠ มัดจำค่าห้อง · ประเภทเงินมัดจำของที่พัก ──
#    ตัวโหลดใบมัดจำคืน "ทุกใบของเลขจอง" (รวมเงินประกัน) ⇒ ทุกเส้นมัดจำค่าห้องต้องกรองด้วย RoomDeposits เอง — ถอดจุดไหน
#    เงินประกันกลายเป็น "ราคา" ในใบเช็คเอาต์ (DEP-SEC-DEDUCT) หรือถูกริบเป็นค่าปรับยกเลิก
_LODGING_ROOM_ONLY_WHY = ("รอบ 194 เงินประกันความเสียหายไม่ใช่ส่วนหนึ่งของราคา — ห้ามเข้าแผนหัก/ตัดชำระ/ริบ/คืนของมัดจำค่าห้อง "
                          "(LodgingDepositSettlement.RoomDeposits · ปิดแยกที่ SettleSecurityDepositAsync)")
RULES += [
    dict(file=LODGING_LIFE, method=m, must=["LodgingDepositSettlement.RoomDeposits("], why=_LODGING_ROOM_ONLY_WHY)
    for m in ("CheckOutAsync", "ResumeCheckOutAsync", "SettleCheckOutAsync", "RecordRefundPaidCoreAsync")
]
RULES += [
    dict(file=LODGING_LIFE, method="CancelCoreAsync",
         must=["LodgingDepositSettlement.RoomDeposits(", "ForfeitAs: DepositForfeitAs.PriceOrFee"],
         before=[("LodgingDepositSettlement.RoomDeposits(", "LodgingDepositSettlement.PlanCancellation(")],
         forbid=["DepositForfeitAs.Compensation"],
         why="รอบ 194 S3: มัดจำค่าห้อง = ส่วนหนึ่งของราคา ⇒ ริบเป็นราคา/ค่าธรรมเนียมยกเลิก (มัดจำเต็มยอดต้องออกใบกำกับของยอดที่ริบ · "
             "ห้ามรายได้ไม่มี VAT เงียบ ๆ) · เงินประกันถูกกรองก่อนวางแผนยกเลิก"),
    dict(file=LODGING_LIFE, method="SettleCheckOutAsync", forbid=["ForfeitAs:"],
         why="รอบ 194: รับรู้มัดจำตอนเช็คเอาต์ = รายได้ตามปกติ (ใบสุดท้ายออกใบกำกับแล้ว) ไม่ใช่การริบ — ห้ามส่ง ForfeitAs"),
    dict(file=LODGING_LIFE, method="LoadDepositSnapshotsAsync", must=["d.DepositNature", "d.CompanyId == companyId"],
         why="รอบ 194: ตัวกรอง RoomDeposits/SecurityDeposit ต้องได้ลักษณะเงินที่ตรึงบนใบ (ไม่มี = กรองได้แค่ด้วย id บนการจอง)"),
    dict(file=LODGING_LIFE, method="CreateDepositReceiptAsync",
         must=["DepositKindForAsync(", "DepositDocumentShaping.Apply(", "DepositKindId: kind.KindId"],
         why="รอบ 194 S4: ใบมัดจำค่าห้องตรึงประเภทของที่พัก (ตัวตัดสินตัวเดียว) และจัดรูปด้วยตัวจัดรูปตัวเดียว"),
    dict(file=LODGING, method="DepositKindForAsync",
         must=["DepositPolicyResolver.ResolveKind(", "prop.RoomDepositKindId", "k.CompanyId == companyId"],
         forbid=["DepositPolicyResolver.Resolve("],
         why="รอบ 194 S4: ลำดับประเภทของที่พัก → ค่าเดิมของที่พัก → ประเภทเริ่มต้นบริษัท → ค่าบริษัท ผ่าน ResolveKind ตัวเดียว (tenant)"),
    dict(file=LODGING, method="ApplyDepositKindsAsync",
         must=["p.DepositVatTreatment = null", "p.DepositOutputVatDeferred = false", "x.CompanyId == companyId",
               "DepositNature.RefundableSecurity", "DepositNature.PartOfPrice"],
         before=[("x.CompanyId == companyId", "p.RoomDepositKindId = d.RoomDepositKindId")],
         why="รอบ 194: บันทึกตั้งค่าที่พักต้องล้างค่าเดิม (migration ผูก lp:{id} กลับทุกบูต) · ประเภทต้องเป็นของบริษัทนี้และลักษณะถูกช่อง"),
    dict(file=LODGING_LIFE, method="ReceiveSecurityDepositAsync",
         must=["DepositPolicyResolver.ResolveKind(", "DepositDocumentShaping.Apply(", "DepositKindId: kindEntity.Id",
               "DepositNature.RefundableSecurity", "k.CompanyId == companyId"],
         before=[("_docService.ApproveDocumentAsync(", "r.SecurityDepositDocumentId = created.Id")],
         why="รอบ 194: ใบรับเงินประกันตรึงประเภทเงินประกัน (ด่าน DEP-SEC-DEDUCT/ริบตามลักษณะอ่านจากใบ) · ผูกการจองหลังอนุมัติสำเร็จ"),
    dict(file=LODGING_LIFE, method="SettleSecurityDepositAsync", must=["JobLock.RunExclusiveAsync(", "SettleSecurityDepositCoreAsync("],
         why="รอบ 194: ปิดเงินประกันล็อกระดับการจองเดียวกับคืนเงินค่าห้อง (ทั้งสองเส้นแตะ PaidAmount)"),
    dict(file=LODGING_LIFE, method="SettleSecurityDepositCoreAsync",
         must=["LodgingDepositSettlement.SecurityDeposit(", "LodgingDepositSettlement.PlanSecuritySettlement(",
               "ForfeitAs: DepositForfeitAs.Compensation", "VatPeriodFiledAsync(", "LodgingDepositSettlement.HeldGross("],
         before=[("LodgingDepositSettlement.PlanSecuritySettlement(", "ApplyDepositToInvoiceAsync("),
                 ("LodgingDepositSettlement.PlanSecuritySettlement(", "RealizeDepositAsync("),
                 ("ApplyDepositToInvoiceAsync(", "RefundDepositAsync(")],
         forbid=["DepositBaseDeducted", "DepositForfeitAs.PriceOrFee"],
         why="รอบ 194 S3: เงินประกันปิด 3 ทาง — ตัดชำระใบที่คิด VAT (ห้ามหักฐาน DEP-SEC-DEDUCT) · ริบเป็นค่าเสียหายไม่มี VAT · "
             "คืนจากยอดจริงหลังขั้นก่อน · ด่านทั้งหมดก่อนแตะบัญชี"),
    dict(file=LODGING_LIFE, method="VatPeriodFiledAsync",
         must=["TaxFilingLockPolicy.DeclaredOrFiledStatuses", "t.CompanyId == companyId"],
         why="ด่านวันที่คืนเงิน (มัดจำค่าห้อง + เงินประกัน) ห้ามย้อนเข้างวด ภ.พ.30 ที่ยื่นแล้ว — ตัวเดียว"),
]

# ── รอบ 194 ทีม M (หลังฝ่ายค้านเงิน/ภาษี review194-money M1–M6 + PLAUSIBLE · regsec C2/P1) — เทสต์ของรอบนี้เรียกแค่ helper pure
#    (เรพไม่มี DbContext ในเทสต์) ⇒ ล็อกการต่อสาย/ลำดับ/สูตรต้องห้ามใน service ที่นี่ ──
_M1_WHY = ("รอบ 194 M1: ริบแล้วออกใบกำกับ — (ก) ห้าม ChangeTracker.Clear (ปลด entity ของผู้เรียก ⇒ หมายเหตุที่พักหายเงียบ) ถอยเฉพาะของขั้นที่ล้ม · "
           "(ข) หาใบกำกับที่ค้างของมัดจำใบนี้ก่อนสร้าง (idempotent) · (ค) ทางไปต่อข้อความเดียว · (ง) ธงลงใบมัดจำหลังสำเร็จเท่านั้น")
RULES += [
    dict(file=DOC, method="IssueForfeitTaxInvoiceAsync",
         must=["DepositKindDocumentRules.ResumeForfeitInvoice(", "DepositKindDocumentRules.ForfeitInvoiceMarker(",
               "RevertTrackedChangesSinceAsync(", "DepositKindDocumentRules.ForfeitRetryHint(", "PaymentDate: null"],
         before=[("DepositKindDocumentRules.ResumeForfeitInvoice(", "CreateDocumentAsync("),
                 ("ApplyDepositToInvoiceAsync(", "AppendInternalNote(")],
         # R3-1: ห้ามส่งวันรับเงินย้อนเป็น PaymentDate ของใบกำกับของยอดที่ริบ (GL 21911 กับ ภ.พ.30 คนละเดือน · ลำดับ §87 ผิด)
         forbid=["ChangeTracker.Clear(", "decision.LateVatNote", "DepositAppliedToDocumentId is Guid priorId",
                 "tp.TaxPointDate < when", "PaymentDate: taxPoint"],
         why=_M1_WHY + " · R3-1: tax point ของใบกำกับของการริบ = วันที่ของใบเสมอ"),
    dict(file=DOC, method="RevertTrackedChangesSinceAsync",
         must=["TrackedChangeRevert.DetachSince(", "ReloadAsync(", "TrackedChangeRevert.DetachStrays("],
         before=[("TrackedChangeRevert.DetachSince(", "ReloadAsync("), ("ReloadAsync(", "TrackedChangeRevert.DetachStrays(")],
         forbid=["ChangeTracker.Clear("],
         why="รอบ 194 M1(ก) + R2: ถอยเฉพาะ entity ที่เกิดหลังจุดตั้งต้น (ตรวจ graph ก่อน · ตัด reference/collection) + อ่านค่าจริงของ entity เดิม "
             "+ ตรวจซ้ำหลัง reload — ห้ามล้างทั้ง context"),
    dict(file="Helpers/TrackedChangeRevert.cs", method="DetachSince",
         must=["ChangeTracker.DetectChanges(", "AutoDetectChangesEnabled = false", "EntityState.Detached", "r.CurrentValue = null"],
         before=[("ChangeTracker.DetectChanges(", "EntityState.Detached")],
         why="รอบ 194 R2: ต้องตรวจ graph ก่อนปลด (entity ที่ผูกผ่าน navigation แต่ยังไม่ถูกตรวจจะถูกดึงกลับตอน SaveChanges)"),
    dict(file=DOC, method="RealizeDepositAsync",
         must=["JobLock.RunExclusiveAsync(", "AdvisoryLockKey.DepositRealize", "ReloadAsync("],
         before=[("JobLock.RunExclusiveAsync(", "RealizeDepositCoreAsync("), ("ReloadAsync(", "RealizeDepositCoreAsync(")],
         why="รอบ 194 M1(จ)/P-a: ล็อกระดับใบมัดจำแล้วอ่านค่าล่าสุดก่อนตัดสิน — สองคำขอพร้อมกันห้ามได้ใบกำกับสองใบ"),
    # รอบ 194 R2: ถอดกฎเดิม must "ForfeitZeroVatDeferred(" + "VatPeriodDeclaredOrFiledAsync(" ในเมธอดนี้ — มันล็อกพฤติกรรมผิด 2 ข้อ:
    #   R2-2 ส่ง ForfeitZeroVatDeferred(ธงบนใบ, ช่องทาง) เข้าตัวตัดสิน ⇒ ใบเดิม (NULL · VAT 0 · ธง false) ถูกตีเป็น "VAT 0 โดยชอบ" · ตอนนี้ตัวตัดสินรับ
    #   ธงดิบ + อัตราช่องทางแล้วแปลงสามสถานะเอง (ตัวเดียว) · R2-1 ใช้ "มีแถวยื่นในระบบ" อย่างเดียวเป็นหลักฐานว่ายังไม่ยื่น ⇒ ตอนนี้ต้องดูกำหนดยื่น
    #   (วันนี้) + ล็อก/ปิดงวด ผ่าน DepositReceiptPeriodLockedAsync ตัวเดียว
    dict(file=DOC, method="RealizeDepositCoreAsync",
         must=["request.ForfeitAs == null", "DepositPolicyResolver.PlainRealizeProblem(", "DepositPolicyResolver.RevenueAccountPlan(",
               "DepositPolicyResolver.UndueVatToRecognize(", "DepositPolicyResolver.ForfeitTaxPointDecision(",
               "DepositReceiptPeriodLockedAsync(", "ThaiDate.CalendarDateUtc(", "DepositChannelVatRateAsync(",
               "DepositPolicyResolver.SameVatPeriod(", "doc.DepositOutputVatRecognizedAt = vatJeDate",
               "DepositForfeitVatRoute.ForfeitTaxInvoice", "DepositForfeitVatRoute.UndueReclassification"],
         call_args=[("DepositPolicyResolver.ForfeitVatDecision(", "channelVatRate"),
                    ("DepositPolicyResolver.PlainRealizeProblem(", "channelVatRate")],
         before=[("DepositChannelVatRateAsync(", "DepositPolicyResolver.PlainRealizeProblem("),
                 ("DepositPolicyResolver.ForfeitTaxPointDecision(", "IssueForfeitTaxInvoiceAsync("),
                 ("DepositPolicyResolver.UndueVatToRecognize(", "_db.JournalEntries.Add(je)")],
         forbid=["kindForfeitAccount.Trim() : request.RevenueAccountCode", "var vatMove = recognizeDeferredVat ? doc.VatAmount",
                 "forfeit.LateVatNote", "ForfeitZeroVatDeferred(doc.DepositOutputVatDeferred",
                 "doc.DepositOutputVatRecognizedAt = forfeitTaxPoint?.TaxPointDate ?? when",
                 "await VatPeriodDeclaredOrFiledAsync(companyId, doc.DocumentDate))"],
         why="รอบ 194 M2–M5 + R2-1/R2-2/R2-3: ริบ = ForfeitAs ระบุ · เต็มยอดที่ยังไม่เสีย VAT/เงินประกัน ปฏิเสธ \"ส่งมอบแล้ว\" พร้อมทางไปต่อ · "
             "VAT 0 รู้แน่ได้จากลักษณะเงิน/ช่องทาง (หาจากใบ) เท่านั้น · VAT พักย้ายเท่าที่เหลือจริง · tax point ตามกำหนดยื่น+ล็อก · "
             "RecognizedAt ตรงเดือนของ JE ที่ Cr 21911 จริง"),
    dict(file=DOC, method="DepositReceiptPeriodLockedAsync",
         must=["VatPeriodDeclaredOrFiledAsync(", "ResolveFiscalPeriodAsync(", "FiscalPeriodStatus.Open"],
         why="รอบ 194 R2-1: งวดเดือนรับเงินที่ยื่น/ล็อก/ปิดงวดบัญชีแล้ว ⇒ VAT ของการริบเข้างวดปัจจุบัน + ธง (ลงขาภาษีย้อนเข้างวดที่ปิดไม่ได้)"),
    dict(file=DOC, method="DepositChannelVatRatesAsync",
         must=["LodgingPricingEngine.PropertyVatRate(", "r.CompanyId == companyId", "p.CompanyId == companyId"],
         why="รอบ 194 R2-2/P1: อัตราช่องทางหาจากใบ (ที่พัก ChargeVat) ตัวเดียวกับที่พัก · tenant ทั้งสองตาราง — ห้ามรับอัตราจาก client"),
    dict(file=DOC, method="GetDepositsAsync",
         must=["DepositChannelVatRatesAsync(", "DepositPolicyResolver.PlainRealizeOffered("],
         call_args=[("DepositKindDocumentRules.ForfeitOptions(", "channelVatRate"),
                    ("DepositKindDocumentRules.DefaultForfeitExplanation(", "channelVatRate"),
                    ("DepositPolicyResolver.PlainRealizeProblem(", "plainCh")],
         why="รอบ 194 R2-2/R2-3: ตัวเลือก/คำเตือนบนหน้าศูนย์มัดจำต้องตรงกับที่เซิร์ฟเวอร์ตัดสินตอนกด (อัตราช่องทางเดียวกัน · เงินประกันซ่อน \"ส่งมอบแล้ว\")"),
    dict(file=DOC, method="UpdateDocumentAsync", must=["DepositPolicyResolver.ShapingVatRate(", "DepositChannelVatRateAsync("],
         why="รอบ 194 R2 (P1 ค้าง): จัดรูปใบมัดจำซ้ำตอนแก้ด้วยอัตราช่องทางของใบเอง — อัตราบริษัท = ใบที่พักไม่คิด VAT กลายเป็นมัดจำเต็มยอด"),
    dict(file=LODGING_LIFE, method="ReceiveSecurityDepositAsync", must=["depositChannelVatRate: vatRate"],
         why="รอบ 194 R2 (P1 ค้าง): ใบรับเงินประกันของที่พักจัดรูปฝั่งเซิร์ฟเวอร์ด้วยอัตราของที่พัก (เส้นเดียวกับมัดจำค่าห้อง)"),
    dict(file=DOC, method="LoadTaxedDepositsByRefAsync",
         must=["LockDepositBalancesAsync("],
         before=[("DepositPolicyResolver.ResolveDeductedDeposits(", "LockDepositBalancesAsync(")],
         forbid=["_db.Database.CurrentTransaction != null"],
         why="รอบ 194 R2 P-a + R3: เส้นอนุมัติใบที่หักฐานมัดจำถือคีย์ล็อกยอดใบมัดจำตัวเดียวกับปุ่มรับรู้/ริบ (LockDepositBalancesAsync) · "
             "R3-2(ง) ห้ามข้ามล็อกเมื่อไม่มีธุรกรรม · R3 P-2 ล็อกเฉพาะใบที่ถูกหักจริง (ตัดสินก่อนล็อก)"),
    # ── รอบ 194 R3-2 — ทุกเส้นที่อ่าน-แล้ว-เขียนยอด/สถานะใบมัดจำถือคีย์ DepositRealizeKey ผ่านตัวล็อกตัวเดียว ──
    dict(file=DOC, method="LockDepositBalancesAsync",
         must=["JobLock.TryXactLockAsync(", "AdvisoryLockKey.DepositRealizeKey(", "ExecuteSqlRawAsync(", "ReloadAsync(",
               "DepositKindDocumentRules.DepositBusyMessage"],
         must_lit=['""CompanyId"" = {1}'],
         before=[("JobLock.TryXactLockAsync(", "ExecuteSqlRawAsync("), ("ExecuteSqlRawAsync(", "ReloadAsync(")],
         forbid=["CurrentTransaction"],
         why="รอบ 194 R3-2: ตัวล็อกยอดใบมัดจำตัวเดียว — คีย์ (ไม่รอ) ก่อนแถว · tenant ใน raw SQL · อ่านค่าล่าสุดใต้ล็อก · ห้ามข้ามเมื่อไม่มีธุรกรรม"),
    dict(file=DOC, method="RecognizeDepositOutputVatAsync",
         must=["LockDepositBalancesAsync("],
         before=[("LockDepositBalancesAsync(", "DepositPolicyResolver.UndueVatToRecognize(")],
         why="รอบ 194 R3-2(ก): ปุ่ม “🧾 VAT” อ่าน RecognizedAt/VAT พักค้างใต้คีย์เดียวกับริบ (เดิมชนกัน ⇒ Dr 21913 สองครั้ง)"),
    dict(file=DOC, method="AutoPostToJournalAsync",
         # สองเส้นขับ JE (หลายใบ · ใบเดียว) ต้องถือล็อกทั้งคู่
         must=["ResolveDrivesDepositIdAsync(", "LockDepositBalancesAsync(companyId, new[] { mDepId.Value })",
               "LockDepositBalancesAsync(companyId, new[] { depIdForLock.Value })"],
         why="รอบ 194 R3-2(ข): เส้นหักมัดจำแบบขับ JE (หลายใบ/ใบเดียว) ถือคีย์ล็อกยอดใบมัดจำ — เดิม FOR UPDATE อย่างเดียว ⇒ lost update กับปุ่มริบ"),
    dict(file=DOC, method="VoidDocumentAsync",
         must=["DepositIdsTouchedByAsync(", "LockDepositBalancesAsync("],
         before=[("LockDepositBalancesAsync(", "DepositsAppliedToAsync("), ("LockDepositBalancesAsync(", "ReverseDepositRealizationsForAsync("),
                 ("LockDepositBalancesAsync(", "UnrealizeDrivesDepositAsync(")],
         why="รอบ 194 R3-2(ค): ยกเลิกเอกสารถือคีย์ของทุกใบมัดจำที่จะคืนยอด ก่อนขั้น 2b/2b-R/7c"),
    dict(file=DOC, method="PurgeDocumentAsync#2",
         must=["DepositIdsTouchedByAsync(", "LockDepositBalancesAsync(", "skipAlreadyReleased: doc.Status == DocumentStatus.Voided"],
         before=[("LockDepositBalancesAsync(", "UnrealizeDrivesDepositAsync(")],
         why="รอบ 194 R3-2 + P-4: ลบเอกสารถือคีย์เดียวกับ void · ใบที่ void แล้วห้ามคืนยอดหักมัดจำแบบขับ JE ซ้ำ"),
    dict(file=DOC, method="DepositIdsTouchedByAsync",
         must=["DepositsAppliedToQuery(", "DepositRealizedForDocumentId == doc.Id", "ResolveDrivesDepositIdsAsync(", "j.CompanyId == companyId"],
         why="รอบ 194 R3-2: ชุดใบมัดจำที่ void/purge แตะ ต้องครบทุกเส้นคืนยอด (ตัดชำระ JV · รับรู้เพื่อใบนี้ · ขับ JE)"),
    dict(file=DOC, method="UnrealizeDrivesDepositAsync",
         must=["ResolveDrivesDepositIdsAsync(", "DepositReversalMath.SplitDrivesUnrealize("],
         forbid=["d.DocumentNumber == doc.DepositAppliedRef"],
         why="รอบ 194 R3 P-4: เส้นคืนใช้ตัวแยกเลขอ้างอิงตัวเดียวกับเส้นหัก (เดิมเทียบตรงตัว ⇒ หลายใบไม่เคยคืน)"),
    dict(file=DOC, method="PostCashSaleJournalAsync",
         must=["BeginTransactionAsync(", "CommitAsync(", "RollbackAsync("],
         before=[("BeginTransactionAsync(", "AutoPostToJournalAsync("), ("RollbackAsync(", "ReloadAsync(")],
         forbid=["catch { e.State = EntityState.Unchanged; }"],
         why="รอบ 194 R3-2(ง): ขายเงินสดผ่าน integration เดินเส้นหักมัดจำ — ต้องอยู่ในธุรกรรมให้ล็อกยอดใบมัดจำมีผล · ถอยก่อนอ่านค่าจริงคืน"),
    dict(file="Services/Implementations/CmsBookingService.cs", method="SyncBookingToErpAsync",
         must=["TrackedChangeRevert.DetachSince(", "TrackedChangeRevert.DetachStrays(", "ReloadAsync("],
         before=[("TrackedChangeRevert.DetachSince(", "ReloadAsync(")],
         why="รอบ 194 R3 P-1: อนุมัติล้มแล้วห้ามบันทึก entity ที่ถูกแก้ค้าง (ใบ Approved ไม่มี JE)"),
    dict(file=LODGING_LIFE, method="CheckOutAsync",
         must=["LodgingCheckoutDraft.LeftoverOf(", "LodgingCheckoutDraft.Plan(", "UpdateDocumentAsync("],
         before=[("LodgingCheckoutDraft.LeftoverOf(", "CreateDocumentAsync("), ("LodgingCheckoutDraft.Plan(", "ApproveDocumentAsync(")],
         why="รอบ 194 R3 P-1: เช็คเอาต์ที่อนุมัติล้มแล้วกดซ้ำ = ใช้ใบร่างเดิม (idempotent) ไม่สร้างใบร่างใหม่ทุกครั้ง"),
    dict(file=DOC, method="RefundDepositAsync",
         must=["JobLock.TryXactLockAsync(", "AdvisoryLockKey.DepositRealizeKey("],
         before=[("JobLock.TryXactLockAsync(", "ExecuteSqlRawAsync(")],
         why="รอบ 194 R2 P-a: คืนมัดจำอ่าน-แล้ว-เขียนยอดเดียวกัน — ถือคีย์เดียวกับรับรู้/ริบ/ตัดชำระ ก่อนล็อกแถว"),
    dict(file=DOC, method="ApplyDepositToInvoiceAsync",
         must=["JobLock.TryXactLockAsync(", "AdvisoryLockKey.DepositRealizeKey("],
         before=[("JobLock.TryXactLockAsync(", "ExecuteSqlRawAsync(")],
         why="รอบ 194 R2 P-a: ตัดชำระด้วยมัดจำถือคีย์เดียวกับรับรู้/ริบ ก่อนล็อกแถว (ลำดับเดียวกันทุกเส้น กัน deadlock)"),
    dict(file="Helpers/JobLock.cs", method="RunExclusiveAsync",
         must=["workError = ex", "when (workError != null)"],
         why="รอบ 194 R2: connection พังระหว่างงาน ⇒ ปลดล็อกล้ม ห้ามทับ error เดิมของงาน (log แล้วปล่อยตัวเดิม)"),
    dict(file=DOC, method="VoidDocumentAsync",
         must=["DepositApplyJournals.GrossByTarget(", "DepositApplyJournals.AfterRestore(", "DepositApplyJournals.LiveOfSource(",
               "DepositApplyJournals.AppliedFromDepositTo("],
         before=[("DepositApplyJournals.GrossByTarget(", "_accountingService.ReverseJournalEntryAsync(")],
         forbid=["d.Id == doc.DepositAppliedToDocumentId.Value && d.CompanyId == companyId);",
                 "inv.PaidAmount = Math.Max(0m, inv.PaidAmount - appliedGross)"],
         why="รอบ 194 R2-4: ยกเลิกใบมัดจำที่ตัดชำระหลายใบ ⇒ คืนยอดจ่ายรายใบตาม JV ที่จับภาพก่อนกลับรายการ (เดิมหักรวมจากใบที่ตัวชี้ชี้ใบเดียว) · "
             "ขั้น 2 ไม่กลับ JE ที่ถูกกลับแล้ว"),
    dict(file=DOC, method="PurgeDocumentAsync#2",
         must=["DepositApplyJournals.AppliedFromDepositTo("],
         why="รอบ 194 R2-5: purge ใบที่เคย void ห้ามลบ JV ต้นฉบับ (ตัวกลับกำพร้า) / หักยอดรับรู้ซ้ำ — ตัวกรองเดียวกับ void"),
    dict(file=DOC, method="DepositsAppliedToQuery",
         must=["DepositApplyJournals.AppliedTo("],
         why="รอบ 194 R2-5: หาใบมัดจำจาก JV ที่ยังมีผลเท่านั้น (ไม่นับคู่ที่ถูกกลับ)"),
    dict(file=DOC, method="AutoPostToJournalAsync",
         must=["DepositKindDocumentRules.ApplyToAnotherTargetAllowed(", "DepositKindDocumentRules.AppliedElsewhereMessage("],
         why="รอบ 194 R2-6: เส้นหักมัดจำแบบขับ JE/หลายใบผ่อน one-shot แบบเดียวกับตัดชำระ · ข้อความปฏิเสธมีทางไปต่อ"),
    dict(file=DOC, method="RecognizeDepositOutputVatAsync",
         must=["DepositPolicyResolver.UndueVatToRecognize("], forbid=["TotalDebit = doc.VatAmount"],
         why="รอบ 194 M3 (คลาสเดียวกัน): ปุ่ม “🧾 VAT” ย้ายเฉพาะ VAT ที่ยังพัก 21913 จริง"),
    dict(file=DOC, method="ApplyDepositToInvoiceCoreAsync",
         must=["DepositKindDocumentRules.ApplyToAnotherTargetAllowed(", "DepositKindDocumentRules.IsForfeitInvoiceOf("],
         why="รอบ 194 C2 (regsec): ริบบางส่วนหลายครั้ง/ตัดชำระบางส่วนแล้วริบส่วนที่เหลือ — ผ่อน one-shot เฉพาะใบกำกับของการริบของมัดจำใบนี้"),
    dict(file=DOC, method="VoidDocumentAsync",
         must=["DepositsAppliedToAsync("], forbid=["&& d.DepositAppliedToDocumentId == documentId)"],
         why="รอบ 194 C2: ยกเลิกใบปลายทางต้องหามัดจำจาก JE ตัดชำระด้วย (ตัวชี้ชี้ได้ใบเดียวเมื่อริบหลายครั้ง)"),
    dict(file=DOC, method="PurgeDocumentAsync#2",
         must=["DepositsAppliedToAsync("], forbid=["&& d.DepositAppliedToDocumentId == documentId)"],
         why="รอบ 194 C2: ลบใบปลายทางหามัดจำจาก JE ตัดชำระด้วย (mirror void)"),
    # รอบ 194 R2-5: ตัวกรอง JV ย้ายไป DepositApplyJournals.AppliedTo (มี companyId + เลขใบ + ไม่นับคู่ที่ถูกกลับ · เทสต์ล็อกด้วย entity จริง)
    dict(file=DOC, method="DepositsAppliedToQuery",
         must=["d.CompanyId == companyId", "DepositApplyJournals.AppliedTo(companyId, documentNumber)"],
         why="รอบ 194 C2: tenant-safe · หาได้ทั้งจากตัวชี้และจาก JE ตัดชำระ"),
    dict(file=DOC, method="CreateDocumentAsync", must=["DepositPolicyResolver.ShapingVatRate("],
         why="รอบ 194 P1 (regsec): ช่องทางที่ไม่คิด VAT ส่งอัตราของตัวเอง — ตัวจัดรูปห้ามจัดซ้ำด้วยอัตราบริษัท"),
    dict(file=DOC, method="ResolveDocumentDepositKindAsync", must=["DepositKindCatalog.ChartHasSecurityAccountAsync("],
         why="รอบ 194 P-f: บัญชี 21530 ตัวกรองเดียว (เปิดใช้ + ไม่ลบ)"),
    dict(file="Helpers/DepositKindCatalog.cs", method="LoadContextAsync", must=["ChartHasSecurityAccountAsync("],
         why="รอบ 194 P-f: บัญชี 21530 ตัวกรองเดียว"),
    dict(file=DOC, method="GuardSecurityDepositDeductionAsync", must=["DocumentStatusRules.NotIssued"],
         forbid=["DepositPolicyResolver.SecurityDeductionProblem(d."],
         why="รอบ 194 P-e: ใบร่าง/void ไม่ใช่สิ่งที่ถูกหัก · ตัดสินจากใบที่ถูกหักจริง (เลขจองร่วมของที่พักห้ามบล็อกผิด)"),
    dict(file=DOC, method="LoadTaxedDepositsByRefAsync", must=["DepositPolicyResolver.ResolveDeductedDeposits("],
         why="รอบ 194 P-e: ใบที่ถูกหักจริงเท่านั้นที่เข้ารับรู้/ตรวจเงินประกัน"),
    dict(file=INTEGRATION, method="DepositKindPayloadRejectionAsync", must=["DocumentStatusRules.NotIssued"],
         why="รอบ 194 P-e: คู่ค้าอ้างใบร่าง/void ไม่นับ"),
    dict(file="Services/Implementations/TaxService.cs", method="GenerateVatReport",
         must=["DepositPolicyResolver.UndueVatStillOpen", "DepositPolicyResolver.ReportedRecognizedDepositVat(", "outputVat += rowVat"],
         why="รอบ 194 M3/M6: แถวมัดจำ VAT พักรายงานเท่าที่ย้ายเข้า 21911 จริง · คำเตือน 21913 ค้างไม่ฟ้องใบที่ปิดแล้ว"),
    dict(file=LODGING_LIFE, method="CancelCoreAsync",
         must=["DepositKindDocumentRules.ForfeitRetryHint(", "channelVatRate: channelVatRate"],
         forbid=["ต้องรับรู้ที่หน้า"],
         why="รอบ 194 M1(ค)/P1: ทางไปต่อข้อความเดียวกับ DocumentService · ริบตามอัตรา VAT ของที่พัก"),
    dict(file=LODGING_LIFE, method="CreateDepositReceiptAsync", must=["depositChannelVatRate: vatRate"],
         why="รอบ 194 P1 (regsec): ใบมัดจำของที่พักที่ไม่คิด VAT คงรูป (ไม่ถูกจัดซ้ำเป็นมัดจำเต็มยอดด้วยอัตราบริษัท)"),
    dict(file=LODGING_LIFE, method="SecurityForfeitAccountAsync",
         forbid=["RoomRevenueAccountCode", "CancellationFeeAccountCode"],
         why="รอบ 194 P-c: ค่าเสียหายห้ามตกไปรายได้ค่าห้อง/ค่าธรรมเนียมยกเลิก — ไม่ตั้งบัญชีริบ ⇒ DocumentService เลือกรายได้อื่น (43080/43070)"),
]

# ── ตัดคอมเมนต์/สตริงโดยคงตำแหน่ง ───────────────────────────────────────────────────────
# ── รอบ 194 ฝ่ายค้านถดถอย/ความปลอดภัย (review194-regsec.md) — C1 เงินประกันกลายเป็นมัดจำค่าห้อง/ค่าเริ่มต้นบริษัท · C3 ใบเงินประกัน
#    ถูกยกเลิกแล้วการจองค้างตลอดไป — ล็อกว่าตัวตัดสินกลาง "ถูกเรียกจริง" ที่ทุกจุด (เทสต์ของ helper เขียวแม้ถอดการเรียกใน service) ──
LODGING_OPS = "Services/Implementations/Lodging/LodgingService.Operations.cs"
_C1_WHY = ("รอบ 194 C1: มัดจำค่าห้อง/ค่าเริ่มต้นบริษัทต้องเป็นมัดจำที่เป็นราคา — เงินประกันที่หลุดเข้ามา = ใบค่าห้องไม่เกิดภาษีตอนรับเงิน (§78/1) "
           "· เช็คเอาต์ไม่หักมัดจำ · CMS ไม่รับรู้รายได้")
RULES += [
    dict(file=LODGING, method="DepositKindForAsync", must=["priceChannel: true"], why=_C1_WHY),
    dict(file=DKSVC, method="SetDefaultAsync", must=["DepositKindCatalog.DefaultKindProblem("],
         before=[("DepositKindCatalog.DefaultKindProblem(", "kind.IsDefault = true")], why=_C1_WHY),
    dict(file=DKSVC, method="UpdateAsync", must=["GuardNatureChangeAsync("],
         before=[("GuardNatureChangeAsync(", "ApplyAsync(")], why=_C1_WHY + " · เปลี่ยนลักษณะต้องตรวจก่อนเขียนทับ"),
    dict(file=DKSVC, method="GuardNatureChangeAsync",
         must=["DepositKindCatalog.NatureChangeProblem(", "d.CompanyId == companyId", "p.CompanyId == companyId"],
         must_re=[r"is\s+string\s+problem\s*\)\s*throw\b"], why=_C1_WHY + " (tenant ทุก query · ผลต้องถูกใช้)"),
    dict(file=LODGING_LIFE, method="ReceiveSecurityDepositAsync", must=["LodgingDepositSettlement.SecurityLinkState("],
         must_re=[r"LodgingSecurityLinkState\.Open\s*\)\s*throw\b"],
         forbid=["r.SecurityDepositDocumentId != null && r.SecurityDepositSettledAt == null"],
         why="รอบ 194 C3: มีเงินประกันค้างไหมตัดสินที่ SecurityLinkState ตัวเดียว (ใบที่ผูกถูกยกเลิก = ไม่ค้าง) — ตรวจลิงก์+วันปิดเอง = ค้างตลอดไป"),
    dict(file=LODGING_OPS, method="MapAsync", must=["LodgingDepositSettlement.SecurityLinkState(", "SecurityDepositOpen ="],
         why="รอบ 194 C3: หน้าจอการจองตัดสินปุ่มรับ/สถานะเงินประกันจากเซิร์ฟเวอร์ (ตัวตัดสินเดียวกับด่านรับ)"),
]

# ── รอบ 195 (ใบ Scommerce — ถดถอยจาก 5e3a323b): ตัวเดาจากชื่อสินค้าต้องอยู่ "ใต้" หลักฐานตัวเลขทั้งใบ ─────────────────
#    BuildScanLinesAsync = ตัวสร้างบรรทัดตัวเดียวของทุกทางเข้า (สร้าง · line-preview · ดึงรายการซ้ำ) · ถอดการเรียก planner หรือ
#    ย้ายไปหลัง ThaiVatTypeRule.Suggest = นมผงกลับเป็นยกเว้นเงียบ ๆ (เทสต์ของ helper ยังเขียว) · ผลต้องถูกใช้ (Decided)
RULES += [
    dict(file=OCR, method="BuildScanLinesAsync",
         must=["OcrLineVatPlanner.PlanWholeInvoice(", "OcrLineVatPlanner.PaperExemptAmount(",
               "OcrLineReconciler.LineDiscountPercent("],
         must_re=[r"if\s*\(\s*vatPlan\s*\.\s*Decided\s*\)", r"vatPlan\s*\.\s*Rates\s*\[\s*vpi\s*\]"],
         before=[("OcrLineVatPlanner.PlanWholeInvoice(", "ThaiVatTypeRule.Suggest(")],
         forbid=["DiscountPercent = docDiscountPercent,"],
         why="รอบ 195: อัตรา VAT บรรทัด = engine/ผู้ใช้ > สัญลักษณ์บนกระดาษ > ตัวเลขหัวใบพิสูจน์ทั้งใบ > เดาจากชื่อ · บรรทัดยอด 0 ไม่ได้ % ส่วนลด"),
    dict(file=DOCSVC, method="CollectApprovalWarningsAsync",
         must=["OcrLineVatPlanner.RateAdvice(", "OcrApprovalGapWarning.Build("],
         before=[("OcrLineVatPlanner.RateAdvice(", "OcrApprovalGapWarning.Build(")],
         call_args=[("OcrApprovalGapWarning.Build(", "vatRateAdvice")],
         why="รอบ 195 P4: คำเตือนตอนอนุมัติบอกทางแก้เป็นตัวเลขเมื่อ VAT บนกระดาษ = 7% ของฐานทั้งใบ (ตัวตัดสินเดียวกับตอนสร้างบรรทัด)"),
]

# ── รอบ 195 ฝ่ายค้าน C1/P1/P3: ด่านที่ตรวจด้วยสูตรเดียวกับที่ผลิตค่า = ผ่านตลอดกาล ────────────────────────────────────────
#    ตัวพิสูจน์ทั้งใบ/คำแนะนำต้องรู้ "ที่มาของ VAT หัวใบ" (Helpers/OcrHeaderVatEvidence) · ตัวแยก VAT ชุดที่สองต้องถามด่านเดียวกับชุดแรก ·
#    AmountTriple ห้ามแต่ง 7/107 เอง · ดึงรายการซ้ำต้องล้าง [Σ-GAP] เก่าก่อนสร้างใหม่ · คำเตือนรวมข้อต้องรู้ VAT ของบรรทัด
SMART = "Services/Implementations/Ocr/SmartFieldExtractor.cs"
TRIPLE = "Services/Implementations/Ocr/AmountTripleExtractor.cs"
RULES += [
    dict(file=OCR, method="BuildScanLinesAsync",
         must=["OcrHeaderVatEvidence.Classify(", "OcrHeaderVatEvidence.DerivedNote(", "OcrHeaderVatEvidence.DerivedTag"],
         call_args=[("OcrLineVatPlanner.PlanWholeInvoice(", "headerVatSource == Accounting.Helpers.OcrHeaderVatSource.Labelled")],
         before=[("OcrHeaderVatEvidence.Classify(", "OcrLineVatPlanner.PlanWholeInvoice(")],
         why="รอบ 195 C1: ชั้นพิสูจน์ทั้งใบใช้ได้เฉพาะ VAT ที่พิมพ์บนกระดาษในฐานะ VAT · VAT ที่ไม่มีบนกระดาษ ⇒ [VAT-DERIVED] หยุดอนุมัติเอง"),
    dict(file=OCR, method="BuildScanLinesAsync",
         must=["OcrLineBuildNotes.StripRecomputed(result.ProcessingNotes)"],
         before=[("OcrLineBuildNotes.StripRecomputed(", "OcrHeaderVatEvidence.Classify(")],
         call_args=[("OcrHeaderVatEvidence.Classify(", "result.ProcessingNotes")],
         why="รอบ 195 P3 → รอบสอง R2-5/R2-4: [Σ-GAP]/[VAT-DERIVED] ของรอบก่อนถูกล้างที่ต้นตัวสร้าง (ครอบทุกเส้น: สร้างใหม่หลังลบ · "
             "สำเนาอัปซ้ำ · ดึงรายการซ้ำ · พรีวิว) · ตัวตัดสินที่มา VAT ต้องเห็นร่องรอย [VAT back-calc]"),
    dict(file=DOCSVC, method="CollectApprovalWarningsAsync",
         call_args=[("OcrLineVatPlanner.RateAdvice(", "headerVatSource"),
                    ("OcrApprovalGapWarning.Build(", "linesVat"),
                    ("OcrApprovalGapWarning.Build(", "headerVatSource"),
                    ("OcrHeaderVatEvidence.Classify(", "gapScan.ProcessingNotes")],
         before=[("OcrHeaderVatEvidence.Classify(", "OcrApprovalGapWarning.Build(")],
         why="รอบ 195 C1/P1 → รอบสอง R2-3/R2-4: ไม่แนะนำ 'ตั้ง 7%' จาก VAT ที่ระบบคำนวณเอง · รวมข้อยอดรวมเฉพาะเมื่อรากเดียวกันจริง · "
             "VAT ที่ไม่ได้พิมพ์บนกระดาษต้องเป็นคำเตือนตอนอนุมัติทุกทางเข้า (ตัวตัดสินเดียวกับตอนสร้าง + ร่องรอย [VAT back-calc])"),
    dict(file=SMART, method="ApplyAmountMath",
         must=["OcrVatBackCalc.Plan("],
         call_args=[("OcrVatBackCalc.Plan(", "data.ReasoningTrace")],
         forbid=["LooksLikeVatDoc(", "/ (1m + ThaiVatRate)"],
         why="รอบ 195 C1: ตัวแยก VAT ชุดที่สองต้องถาม VatBackCalcGuard และเคารพคำปฏิเสธเดิม (ไม่เติมทับ)"),
    dict(file=SMART, method="ValidateOrInferVatRate",
         must=["OcrVatBackCalc.WasBackCalculated("],
         before=[("OcrVatBackCalc.WasBackCalculated(", "0.95")],
         why="รอบ 195 C1: ห้ามดันความมั่นใจของ VAT ที่ถอดจากยอดรวมด้วยสูตร 7% เดียวกัน"),
    dict(file=TRIPLE, method="Extract",
         forbid=["LooksLikeVatDoc(", "(1m + ThaiVat)"],
         why="รอบ 195 C1: AmountTriple ห้ามแต่ง VAT 7/107 ใน fallback (เคยถูกรับเป็นสามค่าที่ลงตัว ความมั่นใจ 0.95 ไม่ผ่านด่าน)"),
]

# ── รอบ 195 ฝ่ายค้านรอบสอง R2-2: ตัวถอด 7/107 ชุดที่สาม (CrossValidator.FillMissingAmounts → ZoneFallback) ไม่ถามด่าน · ไม่ติดแท็ก · banker's
#    ⇒ ถอดได้ที่ Helpers/OcrVatBackCalc ที่เดียว (SplitInclusive · Plan → VatBackCalcGuard.Decide) · ผู้เรียกทุกตัวล็อกไว้ที่นี่
#    + FOLDER_FORBID ข้างล่างกวาดทั้งโฟลเดอร์ OCR ห้ามสูตร ÷1.07 · ÷107 · ÷(1 + อัตรา) เขียนเอง
CROSSV = "Services/Implementations/Ocr/CrossValidator.cs"
RULES += [
    dict(file=CROSSV, method="FillMissingAmounts",
         forbid=["/ (1 + vatRate)", "/ 1.07m", "OcrVatBackCalc.SplitInclusive("],
         why="รอบ 195 R2-2: ตัวเติมยอดของ ZoneFallback ห้ามถอด VAT จากยอดรวมเอง (ถามด่านที่ผู้เรียกผ่าน OcrVatBackCalc.Plan)"),
    dict(file=OCR, method="ApplyZoneAnalysisFallbackAsync",
         must=["OcrVatBackCalc.Plan("],
         call_args=[("OcrVatBackCalc.Plan(", "data.ReasoningTrace")],
         forbid=["OcrVatBackCalc.SplitInclusive("],
         why="รอบ 195 R2-2: โซนอ่านได้แต่ยอดรวม ⇒ ถอด VAT ผ่านด่านตัวเดียวกับเส้นหลัก (เคารพ [VAT skip] + ติด [VAT back-calc])"),
    dict(file=OCR, method="ParseThaiDocument",
         must=["VatBackCalcGuard.Decide(", "OcrVatBackCalc.SplitInclusive("],
         before=[("VatBackCalcGuard.Decide(", "OcrVatBackCalc.SplitInclusive(")],
         why="รอบ 195 R2-2: เส้น Tesseract ถอด VAT ด้วยสูตรตัวเดียว หลังด่านยอมเท่านั้น"),
]

# โฟลเดอร์ OCR ทั้งโฟลเดอร์ (ไม่ใช่รายเมธอด): ห้ามเขียนสูตรถอด VAT 7% จากยอดรวมเอง — ตัวตั้งคือ Helpers/OcrVatBackCalc.SplitInclusive
# (ค้นบนโค้ดที่ตัดคอมเมนต์+สตริงแล้ว · ตัวตรวจ "VAT บนกระดาษ = 7/107 ของยอด" อยู่ใน Helpers/ ซึ่งไม่ถูกกวาด — เป็นการตรวจ ไม่ใช่การผลิตค่า)
FOLDER_FORBID = dict(
    globs=["Services/Implementations/Ocr/*.cs", "Services/Implementations/*Ocr*.cs",
           "Services/Implementations/DocumentZoneAnalyzer.cs"],
    patterns=[r"/\s*1\.07m?\b", r"/\s*107m?\b",
              r"/\s*\(\s*1(?:\.0+)?m?\s*\+\s*(?:0?\.07m?|[A-Za-z_]*[Vv][Aa][Tt][A-Za-z_]*)\s*\)"],
    why="รอบ 195 R2-2: สูตรถอด VAT 7/107 อยู่ที่ Helpers/OcrVatBackCalc.SplitInclusive ที่เดียว (ผ่าน VatBackCalcGuard + แท็ก [VAT back-calc])")


def folder_forbid_errors(files) -> list:
    errs = []
    for rel, text in files:
        code = mask(text)
        for rx in FOLDER_FORBID["patterns"]:
            for m in re.finditer(rx, code):
                line = code.count("\n", 0, m.start()) + 1
                errs.append(f"{rel}:{line} มีสูตรถอด VAT `{text[m.start():m.end()]}` นอก OcrVatBackCalc — {FOLDER_FORBID['why']}")
    return errs


def folder_forbid_files():
    seen, out = set(), []
    for g in FOLDER_FORBID["globs"]:
        for path in sorted(SRC.glob(g)):
            rel = str(path.relative_to(SRC))
            if rel in seen:
                continue
            seen.add(rel)
            out.append((rel, path.read_text(encoding="utf-8")))
    return out


def folder_forbid_self_test(files) -> list:
    fails = []
    if not files:
        return ["self-test FOLDER_FORBID: ไม่พบไฟล์ในโฟลเดอร์ OCR (glob ผิด?)"]
    rel, text = files[0]
    for sample in ["var s = t / 1.07m;", "var v = t * 7m / 107m;", "var s = Math.Round(t / (1 + vatRate), 2);",
                   "var s = t / (1m + ThaiVatRate);", "var s = t / ( 1 + 0.07m );"]:
        if not folder_forbid_errors([(rel, text + "\nclass __X { void F() { " + sample + " } }\n")]):
            fails.append(f"self-test FOLDER_FORBID: ใส่ `{sample}` แล้วไม่ฟ้อง")
    for ok in ["// เดิม t / 1.07m", "var s = \"÷ 1.07\";", "var x = 1m - (1m / (1m + 0.5m * n));", "var r = v / s - 0.07m;"]:
        if folder_forbid_errors([(rel, "class __Y { void F() { " + ok + " } }\n")]):
            fails.append(f"self-test FOLDER_FORBID: `{ok}` ถูกฟ้องผิด")
    return fails


# ── รอบ 196 (ทีม Q · "ใบไหนออกใบแจ้งหนี้แล้ว ดูจากหน้ารวมไม่ได้"): list · detail · ตัวกรอง ต้องเรียกตัวคำนวณการออกเอกสารต่อ
#    ตัวเดียว — เดิม list ไม่เรียกเลย (ป้าย "⏳ รอดำเนินการต่อ" ทุกใบ) และ detail มีสูตรของตัวเอง (ComputeConversionStatusAsync
#    · ไม่กรอง CompanyId) · ถอดการเรียกออกจาก list แล้วเทสต์ของ helper ยังเขียว ⇒ ล็อกจุดเรียกที่นี่
RULES += [
    dict(file=DOCSVC, method="GetDocumentsAsync",
         must=["LoadConversionSummariesAsync(", "ResolveConversionStateIdsAsync("],
         must_re=[r"conversion\s*:\s*conversionByDoc\s*\.\s*GetValueOrDefault\s*\("],
         before=[("ResolveConversionStateIdsAsync(", "CountAsync(")],
         forbid=["ComputeConversionStatusAsync("],
         why="รอบ 196: หน้ารวมต้องส่งผลการออกเอกสารต่อเข้า MapDocumentToResponse (ป้ายโกหกเดิม) · ตัวกรองตัดสินก่อนนับ/แบ่งหน้า"),
    dict(file=DOCSVC, method="GetDocumentAsync",
         must=["LoadConversionSummariesAsync("],
         forbid=["ComputeConversionStatusAsync("],
         why="รอบ 196: หน้ารายละเอียดใช้ตัวคำนวณเดียวกับหน้ารวม (ห้ามสองสูตร)"),
    dict(file=DOCSVC, method="ResolveConversionStateIdsAsync",
         must=["LoadConversionSummariesAsync("],
         why="รอบ 196: ตัวกรองบนหน้ารวมตัดสินด้วยตัวเดียวกับป้ายบนแถว"),
    dict(file=DOCSVC, method="LoadConversionSummariesAsync",
         must=["DocumentConversionProgress.Evaluate(", "DocumentConversionProgress.InactiveChildStatuses"],
         must_re=[r"sd\s*\.\s*CompanyId\s*==\s*companyId", r"cd\s*\.\s*CompanyId\s*==\s*companyId",
                  r"c\s*\.\s*CompanyId\s*==\s*companyId"],
         why="รอบ 196: สูตรเดียว (DocumentConversionProgress) · ใบลูกยกเลิก/ปฏิเสธไม่นับ · ทุก query กรองบริษัท (กฎ M)"),
    dict(file=DOCSVC, method="ComputeLifecycle",
         must=["DocumentConversionProgress.Lifecycle("],
         why="รอบ 196: ข้อความป้ายของชนิดต้นทางมาจาก helper ตัวเดียว (บอกชนิดใบลูก) — ห้ามกลับไปพิมพ์ป้ายเอง"),
]

# ── รอบ 197 (ทีม K · ใบ Makro สาขาชลบุรี 00005): ผู้ติดต่อจากสแกน = คีย์เลขภาษี + สาขา ทั้งเส้นสแกนและเส้นสร้างเอกสาร ──
#    เทสต์ของ OcrVendorBranchContact/ContactTaxBranchKey เป็นแค่ helper — ถอดการส่ง branchReliable · ถอดด่าน "ห้ามจับชื่อเมื่อ
#    ต้องสร้างแถวสาขา" · หรือให้เส้นสร้างเอกสารกลับไปหาแถวไหนก็ได้ของเลขนั้น แล้วเทสต์ยังเขียวทั้งหมด ⇒ ล็อกจุดเรียกที่นี่
RULES += [
    dict(file=OCR, method="ScanAsync",
         must=["OcrVendorBranchContact.Decide(", "NewVendorBranchContactAsync(", "ApplyPrintedVendorLegalName(",
               "OcrVendorLegalName.PickKnownLegalName(", "OcrSellerContactChannel.BuyerSideEmails("],
         call_args=[("OcrVendorBranchContact.Decide(", "branchReliable")],
         must_re=[r"!\s*scanResult\s*\.\s*MatchedContactId\s*\.\s*HasValue\s*&&\s*!\s*mustCreateVendorBranchRow\s*&&\s*ocrVendorNameKey",
                  r"!\s*scanResult\s*\.\s*MatchedContactId\s*\.\s*HasValue\s*&&\s*!\s*mustCreateVendorBranchRow\s*&&\s*_aiAugmenter"],
         before=[("OcrVendorBranchContact.Decide(", "NewVendorBranchContactAsync("),
                 ("ApplyPrintedVendorLegalName(", "EnrichFromDbdAsync(")],
         why="รอบ 197: สาขาบนกระดาษ (มีหลักฐาน) ไม่ตรงผู้ติดต่อเดิม ⇒ สร้างแถวสาขา ห้ามถอยไปจับชื่อ/AI (ได้สำนักงานใหญ่คืน) · "
             "ชื่อโลโก้ → ชื่อนิติบุคคลที่พิมพ์ก่อนทะเบียน/ตัวเรียนรู้ · เตือนอีเมลผู้ซื้อที่ปนในผู้ติดต่อผู้ขาย"),
    dict(file=OCR, method="CreateDocumentFromScanCoreAsync",
         # รอบ 200 ทีม K2 (C-01/C-02): คีย์กลาง + SoftScope ฝั่งผู้ซื้อย้ายไป ResolveSalesCounterpartyAsync ตัวเดียว (ใช้ร่วมกับพรีวิว
         # "แก้ในฟอร์มก่อน") — แถวของเมธอดนั้นล็อก FindAsync/SoftScope/PickBuyerByName ต่อ (บล็อก K2 ข้างล่าง)
         must=["DecideScanVendorBranchContactAsync(", "ResolveSalesCounterpartyAsync("],
         forbid=["NormalizeTaxDigits(c.TaxId) == vTaxDigits", "OcrVendorBranchContact.Decide("],
         why="รอบ 197: เส้นสร้างเอกสารตัดสินผู้ติดต่อด้วยตัวเดียวกับเส้นสแกน (รวม MatchedContactId เก่าที่ผูกสำนักงานใหญ่) · "
             "ผู้ใช้เลือกเองชนะ · ฝั่งผู้ซื้อใช้คีย์กลาง + SoftScope · รอบ 200 K-4: ตัวตัดสินสาขาย้ายไป DecideScanVendorBranchContactAsync "
             "ตัวเดียว (ใช้ร่วมกับ SubmitCorrectionAsync) — ห้ามมีสำเนาในเมธอดนี้"),
    dict(file=OCR, method="DecideScanVendorBranchContactAsync",
         must=["ContactTaxBranchKey.AllBranchIdsAsync(", "OcrVendorBranchContact.Decide(", "NewVendorBranchContactAsync(",
               "OcrSelfPartyGuard.IsOurContact(", "OcrIssuerBranch.StoredAddressIsIssuerBranch("],
         call_args=[("OcrVendorBranchContact.Decide(", "branchReliable"),
                    ("OcrIssuerBranch.ContactAddress(", "paperAddressProven")],
         must_re=[r"userPickedContact\s*=\s*correctedFields\s*\.\s*Contains\s*\(\s*MatchedContactCorrectionField"],
         before=[("OcrIssuerBranch.StoredAddressIsIssuerBranch(", "NewVendorBranchContactAsync(")],
         forbid=["paperIsIssuerBranchAddress: false"],
         why="รอบ 200 K-4/K-9: ตัวตัดสินผู้ติดต่อตามสาขาตัวเดียวของเส้นสร้างเอกสาร + เส้นแก้ผลสแกน · ผู้ใช้เลือกเองชนะ · แถวของเราไม่ใช่ผู้สมัคร · "
             "ที่อยู่แถวสาขาใหม่ต้องพิสูจน์ได้ว่าเป็นของสาขานั้น (ไม่รู้ = ว่าง ไม่เอาที่อยู่ สนญ. จากหัวกระดาษ)"),
    dict(file=OCR, method="MatchContactAsync",
         must=["MatchedContactCorrectionField"],
         why="รอบ 197: ผู้ใช้เลือกผู้ติดต่อเอง ต้องถูกจดไว้ ไม่งั้นเส้นสร้างเอกสารตัดสินสาขาทับคำตอบของคน"),
    dict(file=OCR, method="EnrichFromRawText",
         must=["OcrSellerContactChannel.SellerEmail(", "OcrSellerContactChannel.IsBuyerSide("],
         forbid=["VendorEmailRegex"],
         why="รอบ 197: อีเมล/เบอร์ในบล็อกผู้ซื้อ/ที่อยู่จัดส่งห้ามเป็นของผู้ขาย (อีเมลผู้รับสินค้าบนใบ Makro ปนเข้าผู้ติดต่อผู้ขาย)"),
    dict(file=OCR, method="ParseThaiDocument",
         must=["OcrSellerContactChannel.SellerEmail(", "OcrSellerContactChannel.IsBuyerSide("],
         forbid=["VendorEmailRegex"],
         why="รอบ 197: เส้น Tesseract ใช้ตัวตัดสินอีเมล/เบอร์ตัวเดียวกัน"),
    # ── รอบ 197 ทีม K2 (ฝ่ายค้าน review197.md) ──
    dict(file=OCR, method="SubmitCorrectionAsync",
         call_args=[("OcrCorrectedFieldList.From(", "correctionBaseline"),
                    ("new Accounting.Helpers.OcrCorrectionBaseline(", "OcrWhtBaseline")],
         before=[("new Accounting.Helpers.OcrCorrectionBaseline(", "result.VendorBranchCode = correction.VendorBranchCode")],
         why="K-1: หน้าเว็บส่งรหัสสาขามาทุกครั้ง — นับว่าผู้ใช้แก้สาขาเฉพาะเมื่อค่าเปลี่ยนจากที่เก็บไว้ก่อนรับคำแก้/พิมพ์ยืนยัน "
             "(ไม่งั้นด่านหลักฐานอ่อน ⇒ ไม่สร้างผู้ติดต่อ ไม่เคยกันบนเว็บ)"),
    dict(file=OCR, method="EnrichFromRawText",
         must=["br.SellerConfidence"],
         forbid=["OcrFieldKeys.SellerBranchCode] = 0.85"],
         why="K-2: คะแนนรหัสสาขาผู้ขายตามที่มา (ถอยอ่านทั้งหน้า < 0.85) — ห้ามกลับไปใช้ 0.85 คงที่ที่เท่าเกณฑ์สร้างผู้ติดต่อพอดี"),
    dict(file=OCR, method="ParseThaiDocument",
         must=["branches.SellerConfidence"],
         why="K-2/K-6: เส้น Tesseract/python ต้องติดคะแนนรหัสสาขาด้วย ไม่งั้น 'ไม่มีคะแนน' = ไม่รู้ ⇒ ไม่สร้างแถวแม้มีป้าย"),
    dict(file=OCR, method="ScanAsync",
         must=["OcrSelfPartyGuard.IsOurContact(", "ContactTaxBranchKey.SameEntityIdsAsync("],
         forbid=["d.ContactId == scanResult.MatchedContactId.Value"],
         why="K-7: ตัวตัดสิน 'ผู้ขายคือเราเอง' ตัวเดียว · K-3: แบนเนอร์ PO ค้างมองทุกสาขาของนิติบุคคลเดียวกัน"),
    dict(file=OCR, method="CreateDocumentFromScanCoreAsync",
         must=["OcrSelfPartyGuard.IsOurContact(", "OcrSelfPartyGuard.DecideVendorContactFallback(",
               "OcrSelfPartyGuard.VendorContactBlockMessage(vendorFallback)"],
         must_re=[r"if\s*\(\s*!\s*vendorIsUs\s*&&",
                  r"is\s+string\s+vendorBlockMessage\s*\)\s*throw\b",
                  r"if\s*\(\s*vendorFallback\s*==\s*Accounting\s*\.\s*Helpers\s*\.\s*OcrVendorContactFallback\s*\.\s*CreateNew\s*\)",
                  r"if\s*\(\s*!\s*vendorIsUs\s*&&\s*contactId\s*\.\s*HasValue"],
         call_args=[("OcrSelfPartyGuard.DecideVendorContactFallback(", "vendorSameTaxIdRows")],
         before=[("OcrSelfPartyGuard.DecideVendorContactFallback(", "Name = result.ExtractedVendorName")],
         forbid=["if (!contactId.HasValue && !string.IsNullOrWhiteSpace(result.ExtractedVendorName))"],
         why="K-7 → รอบ 199 ฝ่ายค้าน A-1: ผู้ขายเป็นเรา ⇒ ห้ามสร้างผู้ติดต่อ (ชื่อเรา+เลขเรา) · เลขมีแถวอยู่แล้ว ⇒ ห้ามสร้างแถวซ้ำ · "
             "ตัวตัดสิน fallback ตัวเดียว (DecideVendorContactFallback) · ห้ามเติมเลขเราลงผู้ติดต่อที่ผู้ใช้เลือก"),
    dict(file=OCR, method="GetOpenPosForScanAsync",
         must=["ContactTaxBranchKey.SameEntityIdsAsync("],
         forbid=["d.ContactId == scan.MatchedContactId.Value"],
         why="K-3: PO ที่ออกให้แถว สนญ. ต้องถูกเสนอให้สแกนของสาขา (นิติบุคคลเดียวกัน)"),
    dict(file=OCR, method="LinkPurchaseOrderAsync",
         must=["ContactTaxBranchKey.SameEntityIdsAsync("],
         forbid=["po.ContactId != scan.MatchedContactId.Value"],
         why="K-3: ด่านผูก PO = นิติบุคคลเดียวกัน (ยังกัน PO ของนิติบุคคลอื่น)"),
    dict(file=OCR, method="ComputePredecessorDecisionAsync",
         must=["ContactTaxBranchKey.SameEntityIdsAsync(", "OcrPredecessorMatcher.AcceptsSiblingBranchSource("],
         forbid=["d.ContactId == contactId.Value"],
         why="K-3: ใบต้นทางของทุกสาขาของนิติบุคคลเดียวกัน ยกเว้นใบลด/เพิ่มหนี้ (§86/9-10)"),
    dict(file=OCR, method="LinkPredecessorAsync",
         must=["ContactTaxBranchKey.SameEntityIdsAsync(", "OcrPredecessorMatcher.AcceptsSiblingBranchSource("],
         forbid=["doc.ContactId != expected.Value"],
         why="K-3: ด่านผูกใบต้นทางใช้ขอบเขตเดียวกับตัวหาใบต้นทาง"),
]
# ── รอบ 198 ทีม E (settlement เฟส 0): ด่านเงินของ gateway / ทางเข้าเก่า — เทสต์ล็อกแค่ helper pure (GatewayRefundMath ·
#    GatewaySettlementMath · MoneyAccountFallback · TradeReceivableAccount) · ที่นี่ล็อกว่า service/controller เรียกจริงและเรียงถูก ──
GW_SETTLE = "Services/Payments/GatewaySettlementService.cs"
GW_REFUND = "Services/Payments/GatewayRefundService.cs"
GW_CTL = "Controllers/PaymentGatewayController.cs"
SUBLEDGER = "Services/Implementations/SubLedgerReconciliationService.cs"
RULES += [
    dict(file=GW_REFUND, method="RefundCoreAsync",
         must=["GatewayRefundMath.Check(", "_accounts.ResolveMoneyInAccountAsync(", "TradeReceivableAccount.ResolveAsync(",
               "JournalEntryBuilder.ClosedPeriodReasonAsync(", "BookRefundAsync(", "check.NewRefundedTotal", "MarkOutcomeUnknown("],
         must_re=[r"if\s*\(\s*!\s*check\s*\.\s*Ok\s*\)\s*return\b"],
         call_args=[("GatewayRefundMath.Check(", "RefundOutcomeUnknownSince")],
         before=[("TradeReceivableAccount.ResolveAsync(", "provider.RefundAsync("),
                 ("JournalEntryBuilder.ClosedPeriodReasonAsync(", "provider.RefundAsync("),
                 ("provider.RefundAsync(", "MarkOutcomeUnknown("),
                 ("provider.RefundAsync(", "BookRefundAsync(")],
         forbid=["new JournalEntry", "catch {"],
         why="G-1 คืนเงินต้องมี JE (Dr ลูกหนี้ / Cr บัญชีพัก) · ตรวจยอดสะสม+ผัง+งวดก่อนเงินออก · ห้ามกลืน error หลังเงินออก · "
             "E-2 ผู้ให้บริการโยน = ผลไม่แน่ชัด ⇒ ประทับธง + ด่านยอดต้องอ่านธง (ไม่งั้นกดคืนซ้ำได้)"),
    dict(file=GW_REFUND, method="BookRefundAsync",
         must=["JournalEntryBuilder.For(", "intent.RefundedAmount = newRefundedTotal", "RefundAmount = amount",
               "At = refundAtUtc", "intent.LastRefundedAt = refundAtUtc"],
         forbid=["new JournalEntry", "DateTime.UtcNow, Source"],
         why="R-E2 ยอดคืนรายครั้ง + เวลาเงินออกจริงบนเหตุการณ์ — แผนรอบโอนแยกยอดคืนก่อน/หลังวันเงินเข้าจากตรงนี้ · ตัวลงบัญชีคืนเงินตัวเดียว"),
    dict(file=GW_REFUND, method="VerifyCoreAsync",
         must=["GatewayRefundMath.Verify(", "charge.RefundedTotal", "BookRefundAsync(", "provider.GetChargeAsync(",
               "JournalEntryBuilder.ClosedPeriodReasonAsync("],
         must_re=[r"i\s*\.\s*CompanyId\s*==\s*companyId"],
         before=[("GatewayRefundMath.Verify(", "BookRefundAsync("),
                 ("JournalEntryBuilder.ClosedPeriodReasonAsync(", "BookRefundAsync(")],
         forbid=["provider.RefundAsync(", "catch {"],
         why="E-2 ปลดล็อก/ลงบัญชีตามยอดคืนสะสมที่ผู้ให้บริการรายงานเท่านั้น (ไม่ประทับผลเอง · ตรวจห้ามคืนเงินซ้ำ)"),
    dict(file=GW_REFUND, method="VerifyUnknownRefundAsync", must=["VerifyCoreAsync(", "_intents.ApplyChargeAsync("],
         why="E-2 สถานะคืนเงินหลังตรวจผลเดินผ่านเครื่องสถานะตัวเดียว"),
    dict(file=GW_CTL, method="VerifyRefund", must=["refunds.VerifyUnknownRefundAsync("],
         forbid=["provider.GetChargeAsync(", "RefundOutcomeUnknownSince = null"],
         why="E-2 endpoint ตรวจผลห้ามตัดสิน/ปลดล็อกเอง"),
    dict(file=GW_REFUND, method="RefundAsync", must=["RefundCoreAsync(", "_intents.ApplyChargeAsync("],
         why="G-1 สถานะคืนเงินเดินผ่านเครื่องสถานะตัวเดียว (PaymentIntentService) ไม่ประทับเอง"),
    dict(file=GW_CTL, method="Refund", must=["refunds.RefundAsync("], forbid=["provider.RefundAsync(", ".RefundAsync(intent"],
         why="G-1 endpoint คืนเงินห้ามเรียกผู้ให้บริการตรง (เส้นเดิมที่ไม่ลงบัญชี)"),
    dict(file=GW_SETTLE, method="BuildPlanAsync",
         must=["SelectCandidatesAsync(", "GatewaySettlementMath.Plan(", "CompanyVatStatus.IsRegisteredAsync(",
               "JournalEntryBuilder.ClosedPeriodReasonAsync(", "GatewaySettlementMath.RefundCutoffUtc(req.SettledAt)"],
         why="G-2/G-3/G-5 แผนรอบโอน: ชุดรายการเดียวกับหน้าค้างโอน · VAT ค่าธรรมเนียมตามสถานะจด VAT · ด่านงวดปิดในพรีวิว · "
             "R-E2 ยอดคืนนับ ณ วันเงินเข้า"),
    dict(file=GW_SETTLE, method="SelectCandidatesAsync",
         must=["i.RefundedAmount > 0m", "i.RefundedAmount > i.RefundSettledAmount", "GatewaySettlementMath.RefundedAsOf(",
               "RefundTimingUnknown: !asOf.Known", "e.RefundAmount != null",
               "RefundOutcomeUnknown: i.RefundOutcomeUnknownSince != null"],
         must_re=[r"i\s*\.\s*CompanyId\s*==\s*companyId", r"e\s*\.\s*CompanyId\s*==\s*companyId"],
         forbid=["LastRefundedAt < t2"],
         why="G-2 คืนบางส่วน/คืนหลังรอบโอนต้องเข้าในรอบโอน (เดิมกรองแค่ Succeeded ⇒ บล็อกถาวร) · tenant ทุก query"),
    dict(file=GW_SETTLE, method="ListPendingAsync",
         must=["SelectCandidatesAsync(", "GatewaySettlementMath.Contribution(", "GatewaySettlementMath.FeeInput(",
               "GatewaySettlementMath.FeeInputLabel(", "GatewaySettlementMath.FeeVatModeWarning("],
         why="G-2 หน้ารายการค้างโอนใช้เกณฑ์+สูตรเดียวกับแผน JE (ห้ามสูตรที่สอง) · R-E4 ค่าตั้งต้น/ป้ายของปุ่มแก้ค่าธรรมเนียมจากเซิร์ฟเวอร์ · "
             "R-E6 คำเตือนโหมด VAT"),
    dict(file=GW_SETTLE, method="RecordAsync",
         must=["JournalEntryBuilder.For(", "intent.RefundSettledAmount = cand.Input.RefundedAmount",
               "intent.RefundDeductedAfterSettlement += deducted"],
         forbid=["new JournalEntry", "intent.RefundSettledAmount = intent.RefundedAmount"],
         why="G-5 JE รอบโอนผ่าน JournalEntryBuilder (Dr=Cr + ด่านงวดปิด) · R-E2 มาร์กเฉพาะยอดคืน ณ วันเงินเข้า (ยอดสะสมวันนี้ = คืนหลังวันเงินเข้าหาย) · "
             "R-E5 สะสมยอดคืนที่หักในรอบหลังให้รายงานกระทบยอด"),
    dict(file=GW_SETTLE, method="LoadFeeVatAgingAsync",
         must=["GatewayFeeVatClaim.ClaimTag(", "GatewayFeeVatClaim.Aging(", "settlementJeIds.Contains(l.JournalEntryId)",
               "l.JournalEntry.ReversedByEntryId == null"],
         must_re=[r"l\s*\.\s*JournalEntry\s*\.\s*CompanyId\s*==\s*companyId", r"i\s*\.\s*CompanyId\s*==\s*companyId"],
         why="R-E3 ยอด VAT ค่าธรรมเนียมรอใบกำกับ = บรรทัด 11630 ในใบสำคัญรอบโอนของผู้ให้บริการนี้ − ที่เคลมแล้ว (ไม่ใช่ยอด 11630 ทั้งบัญชี)"),
    dict(file=GW_SETTLE, method="ClaimFeeVatAsync",
         must=["GatewayFeeVatClaim.Check(", "LoadFeeVatAgingAsync(", "JournalEntryBuilder.ClosedPeriodReasonAsync(",
               "JournalEntryBuilder.For(", "GatewayFeeVatClaim.ClaimTag(", "_db.AddChainedAuditLog(", "CompanyVatStatus.IsRegisteredAsync("],
         must_re=[r"if\s*\(\s*!\s*check\s*\.\s*Ok\s*\)"],
         before=[("LoadFeeVatAgingAsync(", "GatewayFeeVatClaim.Check("), ("GatewayFeeVatClaim.Check(", "JournalEntryBuilder.For(")],
         forbid=["new JournalEntry", "AuditLogs.Add(", "FeeExpense"],
         why="R-E3 รับใบกำกับค่าธรรมเนียม = Dr 11610 / Cr 11630 เท่านั้น (ห้ามลงค่าใช้จ่ายซ้ำ) · ยอดห้ามเกิน VAT ที่พัก · §82/3 · hash chain"),
    dict(file=GW_SETTLE, method="ResolveAccountsAsync", must=["_accounts.ResolveClearingAccountAsync("],
         why="G-1/G-5 บัญชีพักตัวเดียวกับขาเงินเข้าและ JE คืนเงิน"),
    dict(file=GW_SETTLE, method="CorrectFeeAsync", must=["_db.AddChainedAuditLog("], forbid=["AuditLogs.Add("],
         why="G-6 แก้ค่าธรรมเนียมที่จะลงบัญชีต้องอยู่ใน hash chain"),
    dict(file=GW_CTL, method="PendingSettlement", must=["settlements.ListPendingAsync(", "i.FeeInput", "i.FeeInputLabel"],
         forbid=["i.Status == PaymentIntentStatus.Succeeded"],
         why="G-2 ห้ามเขียนเกณฑ์เลือกรายการค้างโอนซ้ำใน controller · R-E4 ส่งค่าตั้งต้น/ป้ายของปุ่มแก้ค่าธรรมเนียม"),
    dict(file=GW_CTL, method="Reconciliation",
         must=["GatewayReconciliation.Compute(", "i.RefundDeductedAfterSettlement", "ModeOf(i.ProviderCode)"],
         why="R-E5 รายงานกระทบยอดใช้สูตรรอบโอน (โหมด VAT ค่าธรรมเนียม · ยอดคืนที่หักรอบหลัง)"),
    dict(file=GW_CTL, method="ClaimFeeVat", must=["settlements.ClaimFeeVatAsync("], forbid=["JournalEntryBuilder"],
         why="R-E3 endpoint ห้ามลงบัญชีเอง"),
    # ── ทีม E3 (review198-E2): ผลไม่แน่ชัดจาก HTTP · ช่วงรอก่อนปลดล็อก · ยอดที่พยายามคืน · บันทึกผลด้วยมือ · เคลมซ้ำ · เดือนภาษีที่ยื่นแล้ว ──
    dict(file=GW_REFUND, method="RefundCoreAsync",
         must=["result.OutcomeUnknown", "Guid.NewGuid().ToString("],
         call_args=[("provider.RefundAsync(", "CancellationToken.None"), ("provider.RefundAsync(", "attemptMarker"),
                    ("MarkOutcomeUnknown(", "attemptMarker")],
         before=[("result.OutcomeUnknown", "if (!result.Succeeded)")],
         why="E2-1 ผู้ให้บริการตอบ 5xx/408 (OutcomeUnknown) ต้องล็อกเหมือนหมดเวลา ก่อนแปลเป็น \"ปฏิเสธ\" · "
             "E2-2 คำขอเงินออกไม่ผูกกับการยกเลิกของผู้ใช้ + แนบเครื่องหมายของครั้งนี้"),
    dict(file=GW_REFUND, method="MarkOutcomeUnknown",
         must=["intent.RefundOutcomeUnknownAmount = amount", "intent.RefundOutcomeUnknownAttempt = attemptMarker"],
         why="E2-7/E2-2 เก็บยอด+เครื่องหมายของครั้งที่ผลไม่แน่ชัด ให้การตรวจผลเทียบส่วนต่างได้"),
    dict(file=GW_REFUND, method="VerifyCoreAsync",
         must=["ClearOutcomeUnknown(", "charge.Refunds"],
         call_args=[("GatewayRefundMath.Verify(", "intent.RefundOutcomeUnknownAmount"), ("GatewayRefundMath.Verify(", "attemptAt"),
                    ("GatewayRefundMath.Verify(", "DateTime.UtcNow")],
         forbid=["RefundOutcomeUnknownSince = null"],
         why="E2-2 ปลดล็อก \"ไม่มีเงินออก\" ได้เฉพาะเมื่อพ้นช่วงรอ (ตัวตัดสินต้องได้เวลาพยายามคืน+เวลาปัจจุบัน) · "
             "E2-7 ลงบัญชีเฉพาะส่วนต่างที่เท่ายอดที่พยายามคืน · ปลดล็อกผ่าน ClearOutcomeUnknown ตัวเดียว (ล้างยอด/เครื่องหมายพร้อมธง)"),
    dict(file=GW_REFUND, method="ResolveManuallyCoreAsync",
         must=["GatewayRefundMath.CheckManualResolution(", "_db.AddChainedAuditLog(", "BookRefundAsync(", "ClearOutcomeUnknown(",
               "JournalEntryBuilder.ClosedPeriodReasonAsync("],
         must_lit=["pg_advisory_xact_lock("],
         must_re=[r"if\s*\(\s*!\s*check\s*\.\s*Ok\s*\)\s*return\b", r"i\s*\.\s*CompanyId\s*==\s*companyId"],
         before=[("GatewayRefundMath.CheckManualResolution(", "BookRefundAsync("),
                 ("GatewayRefundMath.CheckManualResolution(", "ClearOutcomeUnknown(")],
         forbid=["provider.RefundAsync(", "new JournalEntry", "AuditLogs.Add(", "RefundOutcomeUnknownSince = null", "catch {"],
         why="E2-3 บันทึกผลด้วยมือ = คนตัดสินจากหลักฐาน (ตัวตรวจเดียว) → ลงบัญชีเส้นเดียวกับคืนเงิน → hash chain · ห้ามสั่งคืนเงินซ้ำจากเส้นนี้"),
    dict(file=GW_REFUND, method="ResolveUnknownRefundManuallyAsync", must=["ResolveManuallyCoreAsync(", "_intents.ApplyChargeAsync("],
         why="E2-3 สถานะหลังบันทึกผลด้วยมือเดินผ่านเครื่องสถานะตัวเดียว"),
    dict(file=GW_CTL, method="ResolveRefundManually", must=["refunds.ResolveUnknownRefundManuallyAsync("],
         forbid=["RefundOutcomeUnknownSince = null", "JournalEntryBuilder", "provider.GetChargeAsync("],
         why="E2-3 endpoint ห้ามตัดสิน/ปลดล็อก/ลงบัญชีเอง"),
    dict(file="Services/Payments/Providers/OmisePaymentProvider.cs", method="RefundAsync",
         must=["GatewayRefundMath.ClassifyRefundHttpStatus(", "OutcomeUnknown: true", "GatewayRefundHttpOutcome.Refused"],
         must_lit=["\"metadata[attempt]\""],
         forbid=["if (!resp.IsSuccessStatusCode)"],
         why="E2-1 5xx/408 ของคำขอคืนเงิน = ผลไม่แน่ชัด (ห้ามตีเป็นปฏิเสธ) · E2-2 แนบเครื่องหมายของครั้งนี้"),
    dict(file="Services/Payments/Providers/OmisePaymentProvider.cs", method="ParseCharge",
         must=["ParseRefundList(", "refunds?.Sum("],
         why="E2-3 ยอดคืนสะสมจากรายการคืนเมื่อไม่มีช่อง refunded_amount · E2-2 เครื่องหมายของครั้งที่ผลไม่แน่ชัด"),
    dict(file=GW_SETTLE, method="ClaimFeeVatAsync",
         must=["GatewayFeeVatClaim.FindDuplicate(", "GatewayFeeVatClaim.PriorClaim(", "TaxFilingLockPolicy.DeclaredOrFiledStatuses",
               "j.ReversedByEntryId == null", "je.TaxInvoiceNo = invoiceNo", "je.TaxInvoiceDate = invoiceDate",
               "je.TaxInvoiceSupplierTaxId = taxId", "je.TaxInvoiceSupplierBranch = check.BranchCode"],
         must_re=[r"j\s*\.\s*CompanyId\s*==\s*companyId", r"t\s*\.\s*CompanyId\s*==\s*companyId"],
         before=[("GatewayFeeVatClaim.FindDuplicate(", "JournalEntryBuilder.For("),
                 ("TaxFilingLockPolicy.DeclaredOrFiledStatuses", "JournalEntryBuilder.For(")],
         why="E2-4 ใบกำกับฉบับเดียวเคลมได้ครั้งเดียว · E2-6 ห้ามลงภาษีซื้อย้อนเข้าเดือนภาษีที่ยื่นแล้ว · E2-5 ใบกำกับเป็นข้อมูลโครงสร้างบนใบสำคัญ"),
    dict(file=GW_SETTLE, method="BuildPlanAsync",
         must=["CountSettledOutcomeUnknownAsync("],
         call_args=[("GatewaySettlementMath.Plan(", "settledOutcomeUnknown")],
         why="E2-3 รายการที่บันทึกรอบโอนแล้วแต่คืนเงินผลไม่แน่ชัด = คำเตือนเฉพาะรอบที่เกี่ยว (ไม่บล็อกทุกรอบในอนาคต)"),
    dict(file=GW_SETTLE, method="CountSettledOutcomeUnknownAsync",
         must=["i.RefundOutcomeUnknownSince < refundCutoffUtc", "i.SettlementJournalEntryId != null"],
         must_re=[r"i\s*\.\s*CompanyId\s*==\s*companyId"],
         why="E2-3 นับเฉพาะที่พยายามคืนก่อนจุดตัดของรอบนั้น · tenant"),
    dict(file=GW_SETTLE, method="SelectCandidatesAsync",
         forbid=["|| i.RefundOutcomeUnknownSince != null", "|| c.Input.RefundOutcomeUnknown"],
         why="E2-3 ธงผลไม่แน่ชัดของรายการที่บันทึกรอบโอนแล้ว (ไม่มีตัวกรองวัน) ห้ามดึงเข้าทุกรอบ ⇒ บล็อกถาวร"),
    dict(file="Services/Implementations/TaxService.cs", method="GenerateVatReport",
         must=["JournalInputTaxInvoice.Resolve(", "TaxPayerBranchCode = inv.SupplierBranch", "TransactionDate = inv.TransactionDate"],
         why="E2-5 บรรทัดภาษีซื้อของ JE ที่มีใบกำกับโครงสร้าง: วันที่/เลขที่/สาขาจากใบกำกับ ไม่ regex คำอธิบาย"),
    dict(file="Controllers/PaymentSettingsController.cs", method="List",
         must=["CompanyVatStatus.IsRegisteredAsync(", "Map(c, vatRegistered)"],
         why="R-E6 หน้าตั้งค่าได้คำเตือนโหมด VAT ค่าธรรมเนียมจากสถานะจด VAT จริง"),
    dict(file=INTEG, method="ProcessPaymentAsync", must=["ResolvePaymentJournalAccountsAsync("],
         before=[("ResolvePaymentJournalAccountsAsync(", "_db.Set<Payment>().Add(")],
         why="I-1 หาผังของ JE รับชำระให้ได้ก่อนบันทึกการชำระ (เดิม return null เงียบหลังตัดยอดแล้ว)"),
    dict(file=INTEG, method="ResolveMoneyAccountAsync",
         must=["MoneyAccountFallback.KindOf(", "MoneyAccountFallback.StandardCode(", "MoneyAccountFallback.PickBank("],
         forbid=["StartsWith(cashAccountCode)"],
         why="I-1 ขาเงินจาก integration ห้ามค้นด้วย prefix 112 (= เงินลงทุนชั่วคราว)"),
    dict(file=INTEG, method="ResolveReceivableAccountAsync", must=["TradeReceivableAccount.ResolveAsync("],
         why="I-1 ผังลูกหนี้ตัวเดียวของใบแจ้งหนี้/ใบลด-เพิ่มหนี้/รับชำระ"),
    dict(file=POS, method="ResolvePaymentAccountAsync",
         must=["MoneyAccountFallback.KindOf(", "MoneyAccountFallback.StandardCode(", "MoneyAccountFallback.PickBank("],
         forbid=["StartsWith(prefix)"],
         why="P-1 ผังสำรองของ POS = รหัสเต็มผังมาตรฐาน (เดิม 1011/1012/1131 → prefix 112/113)"),
    dict(file=SUBLEDGER, method="ReconcileAsync", must=["TradeReceivableAccount.IsTradeReceivableControl("],
         why="S-1 บัญชีพัก gateway (11340) ไม่ใช่ลูกหนี้การค้าในรายงานกระทบบัญชีย่อย"),
]

# ── รอบ 199 (คำตัดสินเจ้าของ 2026-09-28): ภ.พ.06 คุมเฉพาะสลิปจากเครื่องบันทึกการเก็บเงิน ──
#    เทสต์ AbbreviatedTaxInvoiceRuleTests ล็อกตัวตัดสิน · ที่นี่ล็อกว่าสลิป POS ส่งช่องทางสลิป (ถ้าเผลอส่ง Document
#    สลิปจะพิมพ์ "ใบกำกับภาษีอย่างย่อ" โดยไม่มี ภ.พ.06 ⇒ ผู้ซื้อเคลมภาษีซื้อไม่ได้) และหัวเอกสารส่งช่องทางเอกสาร
RULES += [
    dict(file="Helpers/PosSlipHeader.cs", method="Resolve",
         must=["AbbreviatedTaxInvoiceRule.Judge(", "AbbreviatedInvoiceChannel.CashRegisterSlip"],
         forbid=["AbbreviatedInvoiceChannel.Document"],
         why="รอบ 199: สลิปจากเครื่อง POS ต้องผ่านด่าน ภ.พ.06 (ช่องทาง CashRegisterSlip)"),
    dict(file="Services/Implementations/CompanyService.cs", method="UpdateAsync",
         must=["company.IsRetailApproved = request.IsRetailApproved.Value"],
         forbid=["PhoR06ApprovedDate == null"],
         why="รอบ 199 ฝ่ายค้าน C-1: ธงขายปลีก (§86/6) บันทึกได้โดยไม่มีวันที่ ภ.พ.06 — บังคับวันที่ = ร้านไม่มีเครื่องต้องกรอก"
             "วันที่ปลอม แล้ววันที่ปลอมไปเปิดสิทธิ์สลิป POS"),
]

# ── รอบ 199 ฝ่ายค้าน (review-r199-ocr): หัวพิมพ์ซ้ำตามบทบาทที่ตรึง (C-2) · คะแนนรายช่องตามค่าที่ถูกสลับ (A-3) ·
#    คำเตือน VAT ตัดสินจาก VAT ที่จะลงบัญชีตอนนี้ (B-2) — เทสต์ OcrReview199Tests ล็อก helper · ที่นี่ล็อกจุดเรียก ──
RULES += [
    dict(file=PDF_HTML, method="BuildDocumentHtml",
         must=["AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated("],
         call_args=[("AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated(", "doc.IsTaxInvoiceByLaw")],
         why="C-2: renderer HTML — ใบที่ออกเลขแล้วพิมพ์หัวตามบทบาทที่ตรึง ไม่ใช่สิทธิ์บริษัท ณ วันพิมพ์"),
    dict(file=PDF_QUEST, method="RenderDocumentPdfNative",
         must=["AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated("],
         call_args=[("AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated(", "doc.IsTaxInvoiceByLaw")],
         why="C-2: renderer QuestPDF — ตัวตัดสินเดียวกับ HTML (สอง renderer ห้าม drift)"),
    dict(file=PDF_HTML, method="ResolveDocumentTitlesAsync",
         must=["AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated("],
         call_args=[("AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated(", "doc.IsTaxInvoiceByLaw")],
         why="C-2: หัวบนหน้าเว็บ/ตอนตรึงบทบาทต้องตรงกระดาษ"),
    dict(file=PDF_HTML, method="ResolveDocumentHeadingAsync",
         must=["AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated("],
         call_args=[("AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated(", "document.IsTaxInvoiceByLaw")],
         why="C-2: หัวในอีเมล/LINE ต้องตรงกับ PDF แนบ"),
    dict(file=DOCSVC, method="GetDocumentAsync",
         must=["AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated("],
         before=[("AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated(", "AbbreviatedDowngradeNotice(")],
         why="C-2: ใบที่ตรึงเป็นใบกำกับแล้วห้ามขึ้นข้อความ 'หัวถูกลดเป็นใบเสร็จ' (หัวพิมพ์อย่างย่อตามค่าที่ตรึง)"),
    dict(file=OCR, method="ScanAsync",
         must=["OcrPartyResolver.FollowFieldConfidence(extractedData.FieldConfidence, vendorBlock0, buyerBlock0, v, b)",
               "vendorBeforeAi, buyerBeforeAi, v2, b2)"],
         before=[("OcrPartyResolver.FollowFieldConfidence(", "extractedData.VendorBranchCode = v.BranchCode")],
         why="A-3: คะแนนสาขา 'ตามที่มา' (K-2) ต้องย้ายตามค่าเมื่อสลับฝั่ง — ไม่งั้นสาขาที่ย้ายมาถือคะแนนของช่องเดิม"),
    dict(file=DOCSVC, method="CollectApprovalWarningsAsync",
         must=["OcrHeaderVatEvidence.ClassifyPosted("],
         call_args=[("OcrHeaderVatEvidence.ClassifyPosted(", "linesVatNow"),
                    ("OcrApprovalGapWarning.Build(", "postedVatSource")],
         before=[("OcrHeaderVatEvidence.ClassifyPosted(", "OcrApprovalGapWarning.Build(")],
         why="B-2: คำเตือน 'VAT ไม่ได้พิมพ์บนกระดาษ' ตัดสินจาก VAT ที่จะลงบัญชีตอนนี้ — แก้เป็นเลขบนกระดาษแล้วต้องหาย"),
]

# ── รอบ 200 ทีม G: gateway ส่วนที่ยังไม่เคยถูกตรวจ (G-2 ขอบช่วงวัน · G-8 ทุก endpoint · E2-12 · E-3 · ข้อ 18 รุ่น API) ──
OMISE = "Services/Payments/Providers/OmisePaymentProvider.cs"
RULES += [
    dict(file=OMISE, method="Client",
         must=["c.DefaultRequestHeaders.Add(ApiVersionHeader, ApiVersion)"],
         why="ข้อ 18: ปักรุ่น API ทุกคำขอ — ชื่อช่องที่อ่าน (refunded_amount) ต้องไม่ขึ้นกับรุ่นตั้งต้นของบัญชีผู้ขาย"),
    dict(file=OMISE, method="VerifyWebhookAsync",
         must=["IsWellFormedEventId(eventId)"],
         before=[("IsWellFormedEventId(eventId)", "Client(config, ApiBase)")],
         why="ทีม G: เลขจาก body สาธารณะต้องเป็นรูปเลข event ก่อนต่อเข้า path ที่แนบ secret key"),
    dict(file="Services/Payments/PaymentIntentService.cs", method="ApplyChargeToEntity",
         must=["PaymentIntentPolicy.ShouldTakeProviderFee(", "intent.FeeActual = charge.Fee"],
         before=[("PaymentIntentPolicy.ShouldTakeProviderFee(", "intent.Status = charge.Status")],
         forbid=["if (charge.Fee.HasValue) intent.FeeActual = charge.Fee"],
         why="ทีม G: ค่าธรรมเนียมที่แก้มือ (hash chain) / อยู่ในรอบโอนแล้ว ห้ามถูกค่าจากผู้ให้บริการทับ — ตัดสินด้วยสถานะก่อนเปลี่ยน"),
    dict(file=GW_SETTLE, method="BuildPlanAsync",
         must=["GatewaySettlementMath.ConfirmedRangeUtc("],
         forbid=["ThaiDate.CalendarDateUtc(req.ToDate).AddDays(1)"],
         why="G-2: ขอบช่วงวันที่รับเงิน = เที่ยงคืนเวลาไทย (เดิมป้ายวันที่ 00:00 UTC ⇒ เลื่อน 7 ชม.)"),
    dict(file=GW_SETTLE, method="RecordAsync",
         must=["intent.SettledFeeDeducted = c.FeeDeducted"],
         why="E2-12: เก็บค่าธรรมเนียมที่ถูกหัก ณ วันบันทึกรอบ ให้กระทบยอดไม่คิดใหม่ด้วยโหมด VAT วันหน้า"),
    dict(file=GW_SETTLE, method="ClaimFeeVatAsync",
         must=["GatewayFeeVatClaim.FutureClaimDateMessage("],
         before=[("GatewayFeeVatClaim.FutureClaimDateMessage(", "JournalEntryBuilder.For(")],
         why="E2-12: ห้ามเคลมภาษีซื้อลงวันที่ล่วงหน้า (ด่านที่ต้องรู้วันนี้ แยกจาก Check)"),
    dict(file=GW_REFUND, method="VerifyCoreAsync",
         must=["PastRefundBookingAsync(companyId, attemptAt, ct)", "booking.Note"],
         forbid=["var entryDate = ThaiDate.CalendarDateUtc(DateTime.UtcNow)"],
         why="E2-12: เงินคืนที่ยืนยันทีหลังลงวันที่เงินออกจริง (งวดปิด = วันนี้พร้อมหมายเหตุ)"),
    dict(file=GW_REFUND, method="ResolveManuallyCoreAsync",
         must=["PastRefundBookingAsync(companyId, attemptAt, ct)", "booking.Note"],
         forbid=["var entryDate = ThaiDate.CalendarDateUtc(DateTime.UtcNow)"],
         why="E2-12 + คำถามเจ้าของข้อ 4: บันทึกผลด้วยมือ 'เงินออก' ลงวันที่เงินออกจริง ตัวตัดสินเดียวกับการตรวจผล"),
    dict(file=GW_REFUND, method="PastRefundBookingAsync",
         must=["GatewayRefundMath.PastRefundBooking(", "JournalEntryBuilder.ClosedPeriodReasonAsync("],
         why="E2-12: ถามงวดของวันเงินออกแล้วให้ตัวตัดสิน pure ตัดสิน"),
    dict(file=GW_CTL, method="Status",
         must=["PaymentGatewayPermissionScope.StatusKeysFor(", "_permissions.HasPermissionAsync("],
         before=[("PaymentGatewayPermissionScope.StatusKeysFor(", "_intents.RefreshAsync(")],
         why="G-8: ดู/ถามสถานะสด (เปลี่ยนสถานะ + ส่งต่อต้นทางได้) ต้องมีสิทธิ์ต้นทางหรือดูธนาคาร ก่อนถามผู้ให้บริการ"),
    dict(file=GW_CTL, method="ConfirmManually",
         must=["PaymentIntentPolicy.ManualConfirmBlockReason(", "SettlesDirectlyToBank"],
         before=[("PaymentIntentPolicy.ManualConfirmBlockReason(", "_intents.ApplyChargeAsync(")],
         why="ทีม G: ยืนยันมือบนรายการที่ไม่มี charge ที่ผู้ให้บริการ = Dr บัญชีพักด้วยเงินที่ไม่มีวันถูกโอนมา"),
    dict(file=GW_CTL, method="Reconciliation",
         must=["GatewaySettlementMath.ConfirmedRangeUtc(", "i.SettledFeeDeducted"],
         forbid=[".Date.AddDays(1)"],
         why="G-2/E2-12: ขอบช่วงเวลาไทยตัวเดียวกับแผนรอบโอน · ค่าธรรมเนียม ณ วันบันทึกรอบ"),
    dict(file="Services/Implementations/PosService.cs", method="MapTerminal",
         must=["MoneyAccountFallback.TerminalPinWarning(", "usablePins.Contains("],
         forbid=["TerminalBankWarning(t.BankAccountId != null"],
         why="E-3 + R200G-7: เตือนล่วงหน้าว่าบิลโอน/พร้อมเพย์ของเครื่องจะปิดไม่ได้ (กติกาเดียวกับตอนปิดบิล) · ปักผังที่ใช้ไม่ได้ต้องไม่เงียบ"),
    dict(file="Services/Implementations/PosService.cs", method="CompanyBankPickAsync",
         must=["MoneyAccountFallback.PickBank("],
         must_re=[r"b\s*\.\s*CompanyId\s*==\s*companyId"],
         why="E-3: ผลการเลือกบัญชีธนาคารมาจากตัวตัดสินเดียวกับ ResolvePaymentAccountAsync · tenant"),
    dict(file="Services/Implementations/PosService.cs", method="UpdateTerminalAsync",
         must=["ValidateTerminalMoneyAccountsAsync("],
         why="E-3: บัญชีรับเงินที่ปักต้องเป็นผังของบริษัทที่ยังใช้งาน"),
    dict(file="Services/Implementations/PosService.cs", method="CreateTerminalAsync",
         must=["ValidateTerminalMoneyAccountsAsync("],
         why="E-3: บัญชีรับเงินที่ปักต้องเป็นผังของบริษัทที่ยังใช้งาน"),
]

# ── รอบ 200 ฝ่ายค้านทีม G (ทีม GF): R200G-2 บัญชีธนาคารที่ปักใช้เฉพาะโอน/พร้อมเพย์/หักบัญชี · R200G-7 เกณฑ์ "ผังที่ปักได้" ตัวเดียว ·
#    R200G-3 ขอบช่วงเวลาไทยในตัวประกอบรอบโอนจาก PaymentIntent · R200G-1 สี/รูปร่างโต๊ะต่อเข้า CSS ──
RULES += [
    dict(file=POS, method="ResolvePaymentAccountAsync",
         must=["MoneyAccountFallback.TerminalPinFor(", "UsableTerminalMoneyPin(companyId)"],
         forbid=["method == PaymentMethod.Cash ? terminal?.CashAccountId : terminal?.BankAccountId"],
         why="R200G-2: ปักบัญชีธนาคารแล้ว บัตร/e-Wallet/เช็คลง Dr ธนาคารข้าม 11340/11113/11131 · R200G-7: ผังที่ปักต้องผ่านเกณฑ์เดียวกับตัวตรวจตอนบันทึก"),
    dict(file="Services/Implementations/PosService.cs", method="ValidateTerminalMoneyAccountsAsync",
         must=["UsableTerminalMoneyPin(companyId)"],
         why="R200G-7: เกณฑ์ผังที่ปักได้ (สินทรัพย์ · ใช้งาน · ของบริษัท) ตัวเดียวกับตอนปิดบิล"),
    dict(file="Services/Implementations/PosService.cs", method="UsableTerminalPinsAsync",
         must=["UsableTerminalMoneyPin(companyId)"],
         why="R200G-7: ป้ายเตือนหน้าเครื่องตัดสินจากเกณฑ์เดียวกับตอนปิดบิล"),
    dict(file="Services/Settlement/SettlementImportService.Gateway.cs", method="LoadIntentRowsAsync",
         must=["GatewaySettlementMath.ConfirmedFromUtc(", "GatewaySettlementMath.ConfirmedToExclusiveUtc("],
         forbid=["ThaiDate.CalendarDateUtc(pt)", "ThaiDate.CalendarDateUtc(pf)"],
         why="R200G-3: ขอบช่วงวันที่รับเงิน = เที่ยงคืนเวลาไทย ตัวเดียวกับแผนรอบโอนเส้นเดิม (เดิม 00:00 UTC ⇒ เลื่อน 7 ชม.)"),
    dict(file="Services/Payments/PaymentIntentService.cs", method="ApplyChargeToEntity",
         must=["PaymentIntentPolicy.IsProviderFeeFinal(charge.Status)", "PaymentIntentPolicy.ShouldTakeProviderFee("],
         why="R200G-6: ค่าธรรมเนียมจาก charge ที่ยังรอจ่าย (มักเป็น 0) ห้ามถูกเก็บเป็นค่าจริง"),
    dict(file="Services/Payments/PaymentIntentService.cs", method="ApplyChargeAsync",
         must=["PaymentIntentPolicy.IsProviderFeeFinal(charge.Status)", "PaymentIntentPolicy.ShouldTakeProviderFee("],
         forbid=["intent.FeeActual == null) intent.FeeActual = charge.Fee"],
         why="R200G-6: เส้น duplicate ห้ามเป็นผู้เขียน FeeActual คนที่สองที่มีเงื่อนไขของตัวเอง"),
    dict(file="Controllers/PosFloorPlanController.cs", method="CreateTable",
         must=["PosTableStyle.RejectReason("],
         why="R200G-1: สี/รูปร่างโต๊ะต่อเข้า style/class ของหน้าเว็บ — ข้อความอิสระแตก attribute ได้"),
    dict(file="Controllers/PosFloorPlanController.cs", method="UpdateTable",
         must=["PosTableStyle.RejectReason("],
         why="R200G-1: สี/รูปร่างโต๊ะต่อเข้า style/class ของหน้าเว็บ — ข้อความอิสระแตก attribute ได้"),
    dict(file="Controllers/PosFloorPlanController.cs", method="BulkSave",
         must=["PosTableStyle.RejectReason("],
         before=[("PosTableStyle.RejectReason(", "_db.SaveChangesAsync(")],
         why="R200G-1: ตรวจทั้งชุดก่อนบันทึก"),
    dict(file="Controllers/PosFloorPlanController.cs", method="List",
         must=["PosTableStyle.SafeColor(", "PosTableStyle.SafeShape("],
         why="R200G-1: ค่าที่เก็บไว้ก่อนมีด่านเขียนต้องส่งออกเป็นค่าปลอดภัย"),
]

# ── รอบ 198 ฝ่ายค้าน R-E1 (P0): webhook ต้องตรวจความเป็นเจ้าของรายการก่อนเปลี่ยนสถานะ ──
RULES += [
    dict(file="Controllers/PaymentGatewayController.cs", method="Receive",
         must=["PaymentWebhookOwnership.RejectReason(", "i.CompanyId == cfg.CompanyId", "_intents.ApplyChargeAsync("],
         before=[("PaymentWebhookOwnership.RejectReason(", "_intents.ApplyChargeAsync(")],
         must_re=[r"if\s*\(\s*reject\s*!=\s*null\s*\)"],
         why="R-E1: ลายเซ็นผ่านด้วยคีย์บริษัทหนึ่ง แต่ metadata อ้างรายการของอีกบริษัท ⇒ ปิดหนี้ร้านอื่นได้"),
]

# ── รอบ 198 เฟส 1 ทีม B: settlement นำเข้า/จัดประเภท/จับคู่/ตั้งค่าช่องทาง (+ ฝ่ายค้าน R-A1/R-A2/R-A9) ──
SETTLE_IMPORT = "Services/Settlement/SettlementImportService.cs"
SETTLE_LINES = "Services/Settlement/SettlementImportService.Lines.cs"
SETTLE_CHANNEL = "Services/Settlement/SettlementChannelService.cs"
SETTLE_GATEWAY = "Services/Settlement/SettlementImportService.Gateway.cs"
SETTLE_CLEARING = "Helpers/SettlementChannelAccounts.cs"
TRADE_AR = "Helpers/TradeReceivableAccount.cs"
RULES += [
    dict(file=SETTLE_IMPORT, method="PersistAsync",
         must=["LockChannelAsync(", "SettlementTxnKey.Assign(", "ExistingKeysAsync(", "SettlementPiiScrubber.Scrub(",
               "ClassifyAsync(", "MatchLinesAsync(", "SyncIntentStampsAsync(", "_db.AddChainedAuditLog(",
               "SettlementSaleMatch.DeriveImportStatus(", "PostingArtifactsAsync(", "SettlementSaleMatch.IsEditable("],
         before=[("ClassifyAsync(", "LockChannelAsync("),
                 ("LockChannelAsync(", "_db.SettlementBatches"),
                 ("LockChannelAsync(", "_db.SettlementLines.Add("),
                 ("PostingArtifactsAsync(", "_db.SettlementLines.Add("),
                 ("SettlementPiiScrubber.Scrub(", "_db.SettlementLines.Add(")],
         forbid=["AuditLogs.Add("],
         why="นำเข้า: ตัด PII ก่อนเก็บ · จัดประเภทนอกล็อก (ห้ามถือล็อกรอ AI) · ล็อกต่อช่องทางตัวเดียวกับผู้ลงบัญชีก่อนอ่านรอบโอน (C-1 · R-B3) · "
             "รอบที่ลงค้างครึ่งทางเติมบรรทัดไม่ได้ · audit ใน hash chain"),
    dict(file=SETTLE_IMPORT, method="ClassifyAsync",
         must=["SettlementLineClassification.ResolveLocal(", "SettlementLabelSeed.Lookup(", "LearnedLabelsAsync(", "_ai.AskAsync(",
               "SettlementLineClassification.AcceptModelAnswer(", "p.FeedbackId = resp.FeedbackId", "p.UsedAi = resp.UsedAi"],
         must_re=[r"if\s*\(\s*t\s+is\s+SettlementLineType\s+ok\s*\)"],
         before=[("SettlementLineClassification.ResolveLocal(", "_ai.AskAsync("),
                 ("_ai.AskAsync(", "SettlementLineClassification.AcceptModelAnswer(")],
         forbid=["Enum.Parse<SettlementLineType>(", "Enum.TryParse<SettlementLineType>(", "p.Type = Enum"],
         why="กฎเหล็ก #1: local ก่อน (adapter → คลัง → seed) · คำตอบครู/นักเรียนผ่านด่านเดียว (ชุด enum · ≥0.70 · ไม่ใช่ majority · เครื่องหมาย) · เก็บ FeedbackId/UsedAi"),
    dict(file=SETTLE_IMPORT, method="LearnedLabelsAsync",
         must=["SettlementClassifiedBy.User", "SettlementLineClassification.LearnedVote("],
         must_re=[r"l\s*\.\s*CompanyId\s*==\s*companyId", r"l\s*\.\s*ChannelId\s*==\s*channelId"],
         why="คลังที่เรียนต่อช่องทาง = เฉพาะที่ผู้ใช้เลือกเอง (ไม่สอนตัวเอง) · tenant + ช่องทาง (IgnoreQueryFilters ต้องกรองเอง)"),
    dict(file=SETTLE_GATEWAY, method="ImportFromPaymentIntentsAsync",
         must=["GatewayClearingMatchesAsync(", "LoadIntentRowsAsync("],
         before=[("GatewayClearingMatchesAsync(", "LoadIntentRowsAsync(")],
         why="R-A1: บรรทัดที่พก PaymentIntentId นับว่าอยู่ในผังพักแล้ว — ต้องยืนยันว่าผังพักช่องทาง = ผังขาเงินเข้าของ intent ก่อน"),
    # ── รอบ 200 ทีม P2 (settlement เฟส 2 · DECISIONS ข้อ 12): intent ของ gateway เข้ารอบโอน — หนึ่งรายการหนึ่งเจ้าของ · สูตรเดียว · ข้อเท็จจริงเดียว ──
    dict(file=SETTLE_GATEWAY, method="ImportFromPaymentIntentsAsync",
         must=["GatewayBatchIntentRules.ChannelRefusal(", "GatewayBatchIntentRules.ModeMismatch(", "LoadIntentRowsAsync(", "PersistAsync("],
         must_re=[r"GatewayBatchIntentRules\s*\.\s*ModeMismatch\s*\([^;]*\)\s*is\s+string\s+(\w+)\s*\)\s*throw\s+new\s+BusinessRuleException\s*\(\s*\1\b",
                  r"GatewayBatchIntentRules\s*\.\s*ChannelRefusal\s*\([^;]*\)\s*is\s+string\s+(\w+)\s*\)\s*throw\s+new\s+BusinessRuleException\s*\(\s*\1\b"],
         call_args=[("LoadIntentRowsAsync(", "FeeVatMode"), ("LoadIntentRowsAsync(", "payoutDate"), ("LoadIntentRowsAsync(", "PeriodFrom")],
         before=[("GatewayBatchIntentRules.ChannelRefusal(", "GatewayBatchIntentRules.ModeMismatch("),
                 ("GatewayBatchIntentRules.ModeMismatch(", "LoadIntentRowsAsync("),
                 ("GatewayClearingMatchesAsync(", "LoadIntentRowsAsync(")],
         why="P2: config gateway กับช่องทางต้องตอบเรื่อง VAT/WHT ค่าธรรมเนียมตรงกัน (ไม่งั้นแต่งภาษีซื้อ/ยอดไม่ลงตัว) · โหมด VAT ของ config "
             "ส่งเข้าตัวประกอบบรรทัด · วันเงินเข้าเป็นจุดตัดยอดคืน · ต้นช่วงที่ผู้ใช้เลือกมีผล (เดิมถูกเพิกเฉยเงียบ)"),
    dict(file=SETTLE_GATEWAY, method="LoadIntentRowsAsync",
         must=["GatewayBatchIntentRules.UnclaimedForBatch(", "GatewayBatchIntentRules.LateRefundInBatch(",
               "GatewaySettlementMath.RefundCutoffUtc(", "GatewaySettlementMath.RefundedAsOf(", "GatewayBatchIntentRules.RefundTimingRefusal(",
               "PaymentIntentAdapter.BuildRows(", "RefundInLinesAsync("],
         must_re=[r"e\s*\.\s*CompanyId\s*==\s*companyId", r"if\s*\(\s*timingUnknown\s*>\s*0\s*\)\s*throw\b"],
         call_args=[("PaymentIntentAdapter.BuildRows(", "feeVatMode")],
         forbid=["SettlementBatchId == null", "SettlementJournalEntryId == null",
                 "SettlementIntentSnapshot(x.Row.Id, x.Row.ProviderRef, x.Row.Amount, x.Row.RefundedAmount",
                 "SettlementIntentSnapshot(i.Id, i.ProviderRef, i.Amount, i.RefundedAmount"],
         why="P2 หนึ่งรายการหนึ่งเจ้าของ: ตัวเลือก intent = expression ตัวเดียวใน GatewayBatchIntentRules (ห้ามเขียนเงื่อนไขซ้ำในเมธอด) · "
             "ยอดคืน ณ วันเงินเข้าด้วยสูตรเส้นเดิม (ห้ามส่งยอดสะสมวันนี้) · แยกไม่ได้ = บล็อก · tenant"),
    dict(file=SETTLE_GATEWAY, method="EnsureIntentRefundCapacityAsync",
         must=["GatewayBatchIntentRules.RefundLinesOverRefunded(", "RefundInLinesAsync("],
         must_re=[r"if\s*\(\s*over\s*\.\s*Count\s*>\s*0\s*\)\s*throw\b", r"i\s*\.\s*CompanyId\s*==\s*companyId"],
         why="P2: ยอดคืนก้อนเดียวอยู่ในรอบโอนเดียว — ตรวจซ้ำใต้ล็อก (ผลต้องถูกใช้)"),
    dict(file=SETTLE_GATEWAY, method="RefundInLinesAsync",
         must=["SettlementLineType.Refund"], must_re=[r"l\s*\.\s*CompanyId\s*==\s*companyId"],
         why="P2/R-B17: ยอดคืนที่อยู่ในบรรทัดแล้วนับทุกช่องทางของบริษัท · tenant"),
    dict(file=SETTLE_IMPORT, method="PersistAsync",
         must=["EnsureIntentRefundCapacityAsync("],
         before=[("AdvisoryLockKey.For(", "EnsureIntentRefundCapacityAsync("), ("LockChannelAsync(", "EnsureIntentRefundCapacityAsync("),
                 ("EnsureIntentRefundCapacityAsync(", "_db.SettlementLines.Add(")],
         why="P2: ตาข่ายยอดคืนต้องอยู่ใต้ล็อกช่องทาง + ล็อก gateway และก่อนเพิ่มบรรทัด"),
    dict(file="Services/Settlement/Adapters/PaymentIntentAdapter.cs", method="BuildRows",
         must=["GatewaySettlementMath.Contribution(", "SettlementLineType.PaymentFee", "part.FeeVat"],
         forbid=["i.FeeActual ?? i.FeeEstimated", "7m / 107m", "100m / 107m", "* 0.07m", "SettlementFeeTax."],
         why="P2 สูตรเดียว: ค่าธรรมเนียมที่ถูกหัก + VAT ต่อรายการจาก GatewaySettlementMath.Contribution ตัวเดียวกับเส้นเดิม (ห้ามสูตรที่สอง)"),
    dict(file=SETTLE_CHANNEL, method="SaveAsync",
         must=["GatewayBatchIntentRules.ModeMismatch("],
         must_re=[r"GatewayBatchIntentRules\s*\.\s*ModeMismatch\s*\([^;]*\)\s*is\s+string\s+(\w+)\s*\)\s*throw\s+new\s+BusinessRuleException\s*\(\s*\1\b"],
         why="P2 ทางเข้าอื่นของข้อเท็จจริงเดียวกัน: ผูกช่องทางกับ gateway ที่โหมดภาษีค่าธรรมเนียมขัดกันไม่ได้"),
    dict(file=GW_SETTLE, method="SelectCandidatesAsync",
         must=["i.SettlementBatchId == null", "i.SettlementJournalEntryId == null", "i.SettlementJournalEntryId != null"],
         why="P2 ทิศกลับ: เส้นเดิมห้ามหยิบ intent ที่รอบโอน settlement เป็นเจ้าของ (ธนาคารเกินสองเท่า) · คืนภายหลังของเส้นเดิม = เฉพาะที่เส้นเดิมเป็นเจ้าของ"),
    dict(file=SETTLE_IMPORT, method="GatewayClearingMatchesAsync", must=["_gatewayAccounts.ResolveClearingAccountAsync("],
         why="R-A1: ผังพัก gateway มาจากตัวตัดสินตัวเดียว (IGatewayAccountResolver) ไม่อ่าน config เอง"),
    dict(file=SETTLE_LINES, method="MatchLinesAsync",
         must=["SettlementSaleMatch.Decide(", "GatewayClearingMatchesAsync(", "SettlementSaleMatch.IntentCandidate(",
               "SettlementSaleMatch.ApplyIntentRefundCapacity(", "RefundOutcomeUnknownSince"],
         must_re=[r"if\s*\(\s*apply\s*&&\s*l\s*\.\s*MatchDecidedByUser\s*\)\s*continue\b"],
         before=[("SettlementSaleMatch.Decide(", "SettlementSaleMatch.ApplyIntentRefundCapacity(")],
         why="R-B1/R-B2/R-B4/E2-10: ไม่ทับคำตัดสินของคน · intent ค้นทั้งบริษัท (พบแต่ใช้ไม่ได้ = ให้คนตัดสิน ห้ามตกใบสรุป) · "
             "ยอดคืนผ่านระบบจัดสรรทีละบรรทัด · คืนเงินผลไม่แน่ชัด = ใช้ไม่ได้"),
    dict(file=SETTLE_LINES, method="MatchLinesAsync",
         must=["SettlementSaleMatch.Decide(", "GatewayClearingMatchesAsync("],
         must_re=[r"d\s*\.\s*CompanyId\s*==\s*companyId", r"r\s*\.\s*CompanyId\s*==\s*companyId",
                  r"i\s*\.\s*CompanyId\s*==\s*companyId"],
         why="จับคู่ด้วยตัวตัดสินตัวเดียว (ไม่เดา · หลายผู้สมัคร = คนเลือก) · tenant ทุก query · intent เฉพาะผังพักที่ตรง (R-A1)"),
    dict(file=SETTLE_LINES, method="ReclassifyLineAsync",
         must=["SettlementLineClassification.Fits(", "LockChannelAsync(", "LoadRedecidableBatchAsync(", "halfPosted?.Check(batchLines)",
               "_recorder.RecordUserChoiceAsync(", "UserChoiceSource.Explicit", "o.LineType != type"],
         call_args=[("RematchChangedAsync(", "oldTypes")],
         before=[("LockChannelAsync(", "_db.SettlementLines.FirstOrDefaultAsync("),
                 ("LockChannelAsync(", "LoadRedecidableBatchAsync("),
                 ("LoadRedecidableBatchAsync(", "Apply(line"),
                 ("RematchChangedAsync(", "halfPosted?.Check("),
                 ("halfPosted?.Check(", "_db.SaveChangesAsync("),
                 ("SettlementLineClassification.Fits(", "Apply(line"),
                 ("_recorder.RecordUserChoiceAsync(", "tx.CommitAsync(")],
         why="ผู้ใช้เลือกประเภท: ด่านเครื่องหมายเดียวกับตัวคิดแผน · ปิดลูปการเรียนรู้แบบ Explicit ในธุรกรรมเดียวกับการกระทำ (DOCTRINE §3) · "
             "review198-S3 S3-1: รอบค้างครึ่งทางแก้ได้เฉพาะเมื่อไม่เปลี่ยนชิ้นที่ออกแล้ว — แผนก่อนแก้คิดก่อน Apply · ตรวจหลังจับคู่ใหม่ก่อนบันทึก"),
    dict(file=SETTLE_LINES, method="VoidBatchAsync",
         must=["LoadEditableBatchAsync(", "LockChannelAsync(", "l.IsDeleted = true", "batch.IsDeleted = true",
               "SyncIntentStampsAsync(", "_db.AddChainedAuditLog("],
         before=[("LockChannelAsync(", "LoadEditableBatchAsync(")],
         forbid=["AuditLogs.Add("],
         why="R-A9: ยกเลิก = soft-delete รอบ+บรรทัด (unique กรอง IsDeleted ⇒ นำเข้าใหม่ได้) · ปลด intent · เฉพาะรอบที่ยังไม่ลงบัญชี · audit"),
    # ── ทีม S3 (review198-B R-B1/R-B3 · review198-C C-1): ล็อกก่อนโหลด · ล็อกตัวเดียวกับผู้ลงบัญชี · รอบค้างครึ่งทางแก้ไม่ได้ ──
    dict(file=SETTLE_LINES, method="LockChannelAsync",
         must=["SettlementChannelLock.Key(", "JobLock.TryXactLockAsync(", "SettlementChannelLock.BusyMessage"],
         must_re=[r"if\s*\(\s*!\s*await\s+JobLock\s*\.\s*TryXactLockAsync\s*\([^;]*\)\s*\)\s*throw\b"],
         forbid=["AdvisoryLockKey.For(", "pg_advisory_xact_lock"],
         why="C-1: ล็อกของผู้นำเข้า = คีย์ตัวเดียวกับที่ผู้ลงบัญชีถือผ่าน JobLock (SettlementChannelLock) — คนละคีย์ = ยกเลิกแทรกการลงบัญชีได้ · "
             "review198-S3 S3-9: ลองล็อกไม่รอ (รอไม่จำกัด = คำขอค้างจน timeout 500) · ถูกถือ = ข้อความเดียวกับฝั่งลงบัญชี"),
    dict(file=SETTLE_LINES, method="AssignLineMatchAsync",
         must=["LockChannelAsync(", "LoadRedecidableBatchAsync(", "SetMatch(", "halfPosted?.Check(batchLines)"],
         before=[("LockChannelAsync(", "_db.SettlementLines.FirstOrDefaultAsync("), ("LockChannelAsync(", "LoadRedecidableBatchAsync("),
                 ("LoadRedecidableBatchAsync(", "SetMatch("), ("SetMatch(", "halfPosted?.Check("), ("halfPosted?.Check(", "_db.SaveChangesAsync(")],
         why="R-B3: ล็อกก่อนโหลดบรรทัด/รอบโอน · review198-S3 S3-1: รอบค้างครึ่งทางตัดสินการจับคู่ได้เฉพาะเมื่อไม่เปลี่ยนชิ้นที่ออกแล้ว "
             "(แผนก่อนแก้คิดก่อน SetMatch · ตรวจก่อนบันทึก)"),
    dict(file=SETTLE_LINES, method="LoadRedecidableBatchAsync",
         must=["SettlementSaleMatch.IsEditable(batch.Status)", "FrozenPartsAsync(", "SettlementBatchMath.Plan(", "CompanyVatStatus.IsRegisteredAsync("],
         must_re=[r"if\s*\(\s*!\s*SettlementSaleMatch\s*\.\s*IsEditable\s*\(\s*batch\s*\.\s*Status\s*\)\s*\)\s*throw\b"],
         why="S3-1: ลงบัญชีแล้วแก้ไม่ได้ · ค้างครึ่งทาง = แผนก่อนแก้จากตัวคิดแผนตัวเดียว + ชิ้นที่ออกแล้วจากป้ายชุดเดียวกับผู้ลงบัญชี"),
    dict(file=SETTLE_LINES, method="Check", must=["SettlementPartialEdit.Refusal(", "SettlementBatchMath.Plan("],
         must_re=[r"is\s+string\s+why\s*\)\s*throw\s+new\s+BusinessRuleException\s*\(\s*why\b"],
         why="S3-1: ผลของตัวตัดสินต้องถูกใช้ (ปฏิเสธทั้งธุรกรรม) — เรียกแล้วทิ้งผล = แก้ชิ้นที่ออกแล้วได้เงียบ ๆ"),
    dict(file=SETTLE_LINES, method="FrozenPartsAsync",
         must=["SettlementPostingKeys.CreatorPrefix(", "SettlementPostingKeys.PaymentMarker(", "DocumentStatus.Voided"],
         must_re=[r"d\s*\.\s*CompanyId\s*==\s*companyId", r"p\s*\.\s*CompanyId\s*==\s*companyId"],
         why="S3-1: ชิ้นที่ออกแล้ว = ป้ายชุดเดียวกับผู้ลงบัญชี (ไม่นับที่ยกเลิกแล้ว) · tenant"),
    dict(file=SETTLE_LINES, method="RematchBatchAsync",
         must=["LockChannelAsync(", "LoadEditableBatchAsync(", "!l.MatchDecidedByUser"],
         before=[("LockChannelAsync(", "LoadEditableBatchAsync(")],
         why="R-B3 ล็อกก่อนตรวจ · R-B1 ไม่จับใหม่ทับใบสรุปที่คนยืนยัน"),
    dict(file=SETTLE_LINES, method="RematchChangedAsync", must=["SettlementSaleMatch.KeepUserMatch(", "l.MatchDecidedByUser = false"],
         before=[("SettlementSaleMatch.KeepUserMatch(", "MatchLinesAsync(")],
         why="R-B1: จัดประเภทใหม่ไม่ทับการจับคู่ที่คนตัดสิน (ยกเว้นเปลี่ยนข้ามกลุ่ม)"),
    dict(file=SETTLE_LINES, method="SetMatch", must=["l.MatchDecidedByUser = true"],
         why="R-B1: คำตัดสินการจับคู่ของคนต้องถูกบันทึกว่าเป็นของคน"),
    dict(file=SETTLE_LINES, method="LoadEditableBatchAsync", must=["PostingArtifactsAsync(", "SettlementSaleMatch.IsEditable("],
         must_re=[r"if\s*\(\s*!\s*SettlementSaleMatch\s*\.\s*IsEditable\s*\(\s*batch\s*\.\s*Status\s*,\s*artifacts\s*\.\s*Count\s*\)\s*\)\s*throw\b"],
         why="C-1(b): รอบที่ลงบัญชีค้างครึ่งทาง (มีเอกสาร/การรับชำระของการลงบัญชี) แก้/ยกเลิกไม่ได้ — ของที่ออกแล้วจะกลายเป็นกำพร้า"),
    dict(file=SETTLE_GATEWAY, method="ImportFromPaymentIntentsAsync", must=["loaded.RefundUnknown > 0"],
         why="E2-10: การคืนเงินผลไม่แน่ชัดต้องบอกตอนประกอบรอบโอน (ด่านผู้ลงบัญชีบล็อก)"),
    dict(file=GW_SETTLE, method="CorrectFeeAsync", must=["SettlementSaleMatch.FeeEditBlockedByBatch("],
         why="R-B16: แก้ค่าธรรมเนียมของ intent ที่อยู่ในรอบโอนแล้วไม่มีผล — ต้องบอก ไม่เงียบ"),
    dict(file=SETTLE_CHANNEL, method="SaveAsync",
         must=["SettlementLineTypeRules.ParseFeeAccountMap(", "_gatewayAccounts.ResolveClearingAccountAsync(",
               "SettlementChannelAccounts.EnsureClearingAccountAsync(", "ResolveCounterpartyAsync(", "_db.AddChainedAuditLog("],
         must_re=[r"if\s*\(\s*rejected\s*\.\s*Count\s*>\s*0\s*\)\s*throw\b"],
         call_args=[("SettlementChannelAccounts.EnsureClearingAccountAsync(", "gatewayClearing")],
         before=[("_gatewayAccounts.ResolveClearingAccountAsync(", "SettlementChannelAccounts.EnsureClearingAccountAsync(")],
         why="ตั้งค่าช่องทาง: แผนผังค่าธรรมเนียมผ่านตัวอ่านกลาง (ปฏิเสธ = ล้มดัง) · ผังพัก gateway จาก resolver (R-A1) · audit"),
    dict(file=SETTLE_CHANNEL, method="ResolveCounterpartyAsync", must=["ContactTaxBranchKey.FindAsync("],
         must_re=[r"c\s*\.\s*CompanyId\s*==\s*companyId"],
         why="ผู้ติดต่อของแพลตฟอร์มด้วยคีย์เลขภาษี+สาขา (รอบ 193) · tenant"),
    dict(file=SETTLE_CLEARING, method="EnsureClearingAccountAsync",
         must=["DecideGatewayClearing("],
         before=[("DecideGatewayClearing(", "CreateUnderLockAsync(")],
         why="R-A1: gateway ที่ผูก config ห้ามสร้าง 1134x — ตัดสินก่อนถึงตัวสร้างผัง"),
    dict(file=GW_SETTLE, method="SelectCandidatesAsync", must=["i.SettlementBatchId == null"],
         why="รอบ 198 ทีม B: intent ที่อยู่ในรอบโอน settlement ใหม่แล้ว ห้ามเส้นเดิมหยิบซ้ำ (ธนาคารเกินสองเท่า)"),
    dict(file=SUBLEDGER, method="ReconcileAsync", must=["_db.SettlementChannels"],
         why="R-A2: ผังพัก wallet ของช่องทาง settlement ไม่ใช่ลูกหนี้การค้า"),
    dict(file=TRADE_AR, method="IsTradeReceivableControl", must=["SettlementChannelAccounts.IsClearingCode("],
         why="R-A2: 11341–11349 ไม่ใช่ลูกหนี้การค้า (ไม่มีเอกสารลูกหนี้รองรับ)"),
]

# ── รอบ 200 ทีม I: ตัวอ่านไฟล์ settlement + คีย์กันซ้ำ (review198-B R-B7–R-B11 · review198-S4 S4-3/S4-4) — ด่านอยู่ใน pure helper
#    (SettlementValueParser · SettlementFileDecisions · SettlementTxnKey · SettlementContentOverlap) · ที่นี่ล็อกว่าตัวอ่าน/ผู้นำเข้า/ผู้ลงบัญชีเรียกจริง ──
SETTLE_ADAPTER = "Services/Settlement/Adapters/GenericColumnMapAdapter.cs"
RULES += [
    dict(file=SETTLE_ADAPTER, method="Parse",
         must=["EnsureIdIntact(txn", "EnsureIdIntact(orderId", "EnsureIdIntact(payout", "SettlementFileDecisions.IsSummaryRow(",
               "SettlementFileDecisions.DecimalCommaAmbiguity(", "SettlementFileDecisions.DecideDates(", "opened.CsvDelimiter",
               "SettlementFileDecisions.WideIdColumnsEmpty(", "SettlementFileDecisions.TotalsIdRows(", "SettlementFileDecisions.LegacyReadOrders(",
               "LiteralDates(", "SummaryNotice(", "warnings"],
         call_args=[("ParseDate(", "zone"), ("SettlementFileDecisions.DecideDates(", "context"),
                    ("SettlementFileDecisions.DecimalCommaAmbiguity(", "map.CommaIsThousands")],
         before=[("SettlementFileDecisions.IsSummaryRow(", "SettlementFileDecisions.DecideDates("),
                 ("SettlementFileDecisions.WideIdColumnsEmpty(", "SettlementFileDecisions.IsSummaryRow("),
                 ("SettlementFileDecisions.DecideDates(", "ParseDate(")],
         forbid=["SettlementValueParser.DetectDateOrder(", "SettlementFileDecisions.DecideDateOrder(", "SettlementFileDecisions.DecideTimeZone("],
         why="R-B7 เลขอ้างอิงที่ Excel ปัดหลัก ⇒ ล้มดัง · R-B11/ข้อ 39 ข้ามเฉพาะแถวสรุปที่พิสูจน์ได้ (ยอดเท่าผลรวม · ไม่มีวันที่ · คำสรุป) ก่อนตัดสินวันที่ + "
             "ยอดที่ข้ามเป็นคำเตือน · I-6 คอลัมน์เลขว่างทั้งไฟล์ ⇒ ล้มดังด้วยเหตุจริง · R-B10/I-5 CSV ; + 1,500 ⇒ ล้มดัง เว้นผู้ใช้ยืนยันจุลภาค · "
             "R-B8/R-B9/I-2 ลำดับวัน/เดือน + เขตเวลาผ่านตัวตัดสินรวมตัวเดียว (ถามครั้งเดียว · ข้อความรู้ว่าจำได้ไหม) · I-1 วันที่ตามตัวอักษรให้คีย์รุ่นก่อน"),
    dict(file=SETTLE_ADAPTER, method="SummaryNotice", must=["LongAmount(", "WideAmounts("],
         why="I-4: ยอดของแถวสรุปที่ข้ามคิดด้วยตัวเดียวกับแถวจริง (กลับเครื่องหมาย · VAT · ยอดออก) — ห้ามรวมค่าดิบเอง"),
    dict(file=SETTLE_ADAPTER, method="EnsureIdIntact", must=["SettlementValueParser.IdLostPrecision("],
         must_re=[r"if\s*\(\s*!\s*SettlementValueParser\s*\.\s*IdLostPrecision\s*\(\s*raw\s*\)\s*\)\s*return\s*;\s*throw\b"],
         why="R-B7: ผลของด่านต้องถูกใช้ (ล้มทั้งไฟล์) — เรียกแล้วทิ้งผล = เลขที่เสียหลักเข้าคีย์กันซ้ำเงียบ ๆ"),
    dict(file=SETTLE_IMPORT, method="ImportFileAsync",
         must=["new SettlementParseContext(", "parsed.LearnedDateOrder", "parsed.LearnedTimeZone", ".Learn(", "request.RememberColumnMap",
               "memoryBlockedReason", "parsed.Warnings", "learnNotes"],
         forbid=["warnings.AddRange(toSave.Learn("],
         before=[("adapter.Parse(", ".Learn(")],
         why="R-B9: ช่วงวันที่ของรอบโอนเป็นหลักฐานให้ตัวอ่าน · สิ่งที่ไฟล์พิสูจน์ได้ถูกจำให้ช่องทาง (เฉพาะเมื่อด่านสิทธิ์จำเปิด) · I-2 ตัวอ่านรู้ว่าจำได้ไหม · "
             "I-3 ข้อความ \"จำแล้ว\" ส่งต่อให้ PersistAsync เติมเฉพาะเส้นที่บันทึกค่าตั้งจริง (ห้ามเติมก่อนรู้ว่าจะ rollback) · ข้อ 39 คำเตือนของตัวอ่านถึงผู้ใช้"),
    dict(file=SETTLE_IMPORT, method="PersistAsync",
         must=["SettlementTxnKey.ImportScopeOf(", "ImportScope = lineScope", "pool.RevisedScope", "SettlementTxnKey.SplitRevisedFilePool(", "pool.SameFile",
               "pool.OtherFiles", "LiteralDateSets(rows)", "SettlementTxnKey.SharesRawIdWith(", "PostedBatchNewRowsMessage(", "input.LearnNotes"],
         call_args=[("SettlementTxnKey.LegacyKeys(", "LiteralDateSets")],
         before=[("await tx.RollbackAsync(", "input.LearnNotes")],
         why="S4-3/I-8: เทียบเนื้อหาเฉพาะไฟล์รุ่นก่อนของไฟล์นี้ + บรรทัดที่เติมจากไฟล์ฉบับแก้สืบลายนิ้วมือของไฟล์รุ่นก่อน · I-1 คีย์รุ่นก่อนคิดจากวันที่ตามตัวอักษรด้วย "
             "(ไฟล์ก่อน deploy ต้องถูกจับว่าซ้ำ) + เส้นรอบลงบัญชีแล้วบอกเหตุจริง · I-3 ข้อความจำเฉพาะหลังเส้น rollback"),
    dict(file=SETTLE_IMPORT, method="ContentOverlapElsewhereAsync",
         must=["SettlementContentOverlap.LoadOtherBatchesAsync(", "SettlementContentOverlap.Find("],
         call_args=[("SettlementContentOverlap.Find(", "claimed")],
         why="S4-4: ผู้นำเข้ากับด่านลงบัญชีใช้ข้อเท็จจริง \"เนื้อหาตรงรอบอื่น\" ตัวเดียวกัน (ห้ามสูตรเทียบสองชุด)"),
    dict(file="Services/Settlement/SettlementPostingService.cs", method="BuildGateAsync",
         must=["SettlementContentOverlap.ForBatchAsync(", "SettlementContentOverlap.Annotate("],
         before=[("SettlementPostingGate.Evaluate(", "SettlementContentOverlap.Annotate(")],
         why="S4-4: เนื้อหาตรงรอบโอนอื่นต้องเตือนที่พรีวิว/ลงบัญชีทุกครั้ง (เดิมเตือนครั้งเดียวตอนนำเข้า ⇒ ค่าธรรมเนียมลงซ้ำได้)"),
]

# ── รอบ 198 เฟส 1 ทีม C: ผู้ลงบัญชีรอบโอน settlement — เทสต์ล็อกแค่ helper pure (SettlementBatchMath · SettlementPostingGate ·
#    SettlementAccountResolver · SettlementDocumentBuilder · SettlementBankMatch) เพราะเรพไม่มีเทสต์ที่มี DbContext ·
#    ที่นี่ล็อกว่า service เรียกด่าน/ล็อก/builder จริง เรียงถูก และใช้ผลของด่าน ──
SETTLE_POST = "Services/Settlement/SettlementPostingService.cs"
RULES += [
    dict(file=SETTLE_POST, method="PostAsync", must=["JobLock.RunExclusiveAsync(", "SettlementChannelLock.Scope", "PostCoreAsync("],
         forbid=["SettlementPostingKeys.LockScope"],
         before=[("JobLock.RunExclusiveAsync(", "PostCoreAsync(")],
         why="ลงบัญชีรอบโอนต้องถือล็อก (deterministic · ต่อช่องทาง) ครอบทุกขั้น — สองคนกดพร้อมกัน = ใบค่าธรรมเนียม/JE ซ้ำ"),
    dict(file=SETTLE_POST, method="PostCoreAsync",
         must=["BuildGateAsync(", "EnsureFeeDocumentAsync(", "EnsureSummaryDocumentAsync(", "EnsureReceiptAsync(", "CommitPostedAsync("],
         must_re=[r"if\s*\(\s*!\s*gate\s*\.\s*Plan\s*\.\s*CanPost\s*\)\s*return\b",
                  r"is\s+SettlementBatchStatus\s*\.\s*Posted\s+or\s+SettlementBatchStatus\s*\.\s*BankMatched\s*\)\s*return\b"],
         before=[("BuildGateAsync(", "EnsureFeeDocumentAsync("), ("EnsureReceiptAsync(", "CommitPostedAsync(")],
         forbid=["new JournalEntry", "catch {"],
         why="แผนต้อง CanPost ก่อนแตะอะไร · ลงแล้วกดซ้ำ = คืนผลเดิม (idempotent) · Posted ประทับหลังทุกชิ้นครบ · ห้ามกลืน error"),
    dict(file=SETTLE_POST, method="BuildGateAsync",
         must=["SettlementBatchMath.Plan(", "CompanyVatStatus.IsRegisteredAsync(", "JournalEntryBuilder.ClosedPeriodReasonAsync(",
               "TaxFilingLockPolicy.DeclaredOrFiledStatuses", "SettlementAccountResolver.Resolve(", "SettlementPostingGate.Evaluate(",
               "DocumentPermissionHelper.CanApproveAsync(", "ClearingSourcesAsync(", "DuplicateSalesAsync(",
               "OrphanArtifactsAsync(", "SettlementReceiptReconcile.Stale(", "AbbreviatedTaxInvoiceRule.Judge(", "AbbreviatedInvoiceChannel.Document"],
         must_re=[r"a\s*\.\s*CompanyId\s*==\s*companyId"],
         before=[("SettlementBatchMath.Plan(", "SettlementPostingGate.Evaluate("),
                 ("SettlementAccountResolver.Resolve(", "SettlementPostingGate.Evaluate(")],
         why="ด่านเดียวของพรีวิวและลงจริง: แผนของทีม A → ผังของบริษัทนี้เท่านั้น (tenant) → งวด/ภาษีที่ยื่นแล้ว/สิทธิ์/รายได้ซ้ำ/ผังพักต้นทาง → Evaluate"),
    dict(file=SETTLE_POST, method="ClearingSourcesAsync",
         must=["_gateway.ResolveMoneyInAccountAsync(", "SettlementSaleMatch.IntentSettledElsewhere(", "RefundOutcomeUnknownSince"],
         why="R-A1: บรรทัดที่อ้าง PaymentIntent ต้องตรวจกับผังที่ขาเงินเข้าลงไว้จริง (ตัวตัดสินเดียวของ gateway)"),
    dict(file=SETTLE_POST, method="EnsureFeeDocumentAsync",
         must=["SettlementDocumentBuilder.FeeDocument(", "SettlementDocumentBuilder.WhtCertificate(", "ApproveIfDraftAsync(",
               "SettlementWhtCertResume.Decide(", "SettlementWhtCertStep.IssueDraft"],
         forbid=["new CreatePaymentRequest(", "new DocumentLineRequest(", "WithholdingTaxRate", "WithholdingTaxAmount"],
         why="ใบค่าธรรมเนียมประกอบจาก builder ตัวเดียว — WHT บนใบ/ตอนจ่าย = 0 (ขา WHT อยู่ใน JE รอบโอน · 50 ทวิ จากชุดบรรทัดเดียวกัน)"),
    dict(file=SETTLE_POST, method="EnsureSummaryDocumentAsync",
         must=["SettlementDocumentBuilder.SummaryDocument(", "SettlementDocumentBuilder.SummaryReviewNote(", "WalkInCustomerContact.GetOrCreateAsync("],
         forbid=["BuyerDeclinedTaxInvoice", "new DocumentLineRequest("],
         why="DECISIONS ข้อ 2–3: ใบสรุปจาก builder ตัวเดียว + ติดป้ายตรวจ · ผู้ซื้อ = ลูกค้าเงินสดกลาง · ห้ามประทับ 'ผู้ซื้อไม่ประสงค์รับใบกำกับ' แทนคน"),
    dict(file=SETTLE_POST, method="EnsureReceiptAsync", must=["SettlementDocumentBuilder.ReceiptPayment("],
         forbid=["new CreatePaymentRequest("],
         why="รับชำระเข้าผังพักพร้อมป้ายของรอบโอน (ทำต่อจากที่ค้างได้ ไม่รับชำระซ้ำ)"),
    dict(file=SETTLE_POST, method="ApproveIfDraftAsync",
         call_args=[("_documents.ApproveDocumentAsync(", "ApprovalAckSource.SystemWorkflow")],
         why="อนุมัติในนามผู้กด — คำเตือนผ่านแบบ 'ระบบส่งผ่าน' ห้ามประทับว่าคนรับทราบ (ApprovalAcknowledgement)"),
    dict(file=SETTLE_POST, method="CommitPostedAsync",
         must=["JournalEntryBuilder.For(", "_db.AddChainedAuditLog(", "SettlementBatchMath.Plan(", "SettlementPlanFingerprint.Of(",
               "SettlementPostingCompleteness.Missing(", "intent.SettlementBatchId ??= batch.Id"],
         must_lit=["FOR UPDATE"],
         must_re=[r"if\s*\(\s*missing\s*\.\s*Count\s*>\s*0\s*\)"],
         before=[("SettlementPlanFingerprint.Of(", "JournalEntryBuilder.For("),
                 ("SettlementPostingCompleteness.Missing(", "JournalEntryBuilder.For("),
                 ("JournalEntryBuilder.For(", "batch.Status = SettlementBatchStatus.Posted")],
         forbid=["new JournalEntry", "AuditLogs.Add("],
         why="JE รอบโอนผ่าน JournalEntryBuilder (Dr=Cr · ด่านงวดปิด) ในธุรกรรมเดียวกับสถานะ Posted · ล็อกแถวรอบโอน · audit ใน hash chain"),
    dict(file=SETTLE_POST, method="MatchBankTransactionAsync", must=["JobLock.RunExclusiveAsync(", "SettlementChannelLock.Scope"],
         why="C-1: ล็อกต่อช่องทางตัวเดียวกับการนำเข้า/ลงบัญชี"),
    dict(file=SETTLE_POST, method="UnpostAsync", must=["JobLock.RunExclusiveAsync(", "SettlementChannelLock.Scope"],
         why="C-1: ล็อกต่อช่องทางตัวเดียวกับการนำเข้า/ลงบัญชี"),
    dict(file=SETTLE_POST, method="ResolveChargebackAsync",
         must=["PermissionKeys.JournalManage", "JobLock.RunExclusiveAsync(", "SettlementChannelLock.Scope", "ResolveChargebackCoreAsync("],
         before=[("PermissionKeys.JournalManage", "JobLock.RunExclusiveAsync(")],
         why="C-8 สิทธิ์ลง JE ตรวจใน service · C-14 ล็อกต่อช่องทาง (แทรกระหว่างยกเลิกการลงบัญชีไม่ได้)"),
    dict(file=SETTLE_POST, method="MatchCoreAsync",
         must=["SettlementBankMatch.Check(", "_bank.ReconcileAsync(", "_db.AddChainedAuditLog(", "PermissionKeys.BankReconcile"],
         must_re=[r"if\s*\(\s*!\s*decision\s*\.\s*Ok\b"],
         before=[("SettlementBankMatch.Check(", "_bank.ReconcileAsync("),
                 ("SettlementBankMatch.Check(", "batch.Status = SettlementBatchStatus.BankMatched"),
                 ("PermissionKeys.BankReconcile", "_bank.ReconcileAsync(")],
         forbid=["ReconciliationStatus.Matched;"],
         why="R1: BankMatched เฉพาะเมื่อมีรายการเดินบัญชีจริงของบริษัทนี้ยอดเท่ากัน · ฝั่งรายการเดินบัญชีประทับโดยเจ้าของ (IBankService)"),
    dict(file=SETTLE_POST, method="ResolveChargebackCoreAsync",
         must=["SettlementBatchMath.PlanChargebackResolution(", "SettlementAccountResolver.ResolveJournal(", "JournalEntryBuilder.For(",
               "_db.AddChainedAuditLog("],
         forbid=["new JournalEntry"],
         why="ปิด chargeback ตามแผนของทีม A ผ่าน builder · ผังของบริษัทนี้เท่านั้น"),
    dict(file=SETTLE_POST, method="UnpostCoreAsync",
         must=["_accounting.ReverseJournalEntryAsync(", "_documents.VoidDocumentAsync(", "_documents.VoidPaymentAsync(",
               "_db.AddChainedAuditLog(", "JournalEntryBuilder.ClosedPeriodReasonAsync(", "DocumentPermissionHelper.CanVoidAsync(",
               "SettlementUnpostGate.Evaluate(", "SettlementUnpostGate.VoidOrder(", "SettlementUnpostScope.Enter(",
               "PermissionKeys.BankReconcile", "LoadUnpostFactsAsync("],
         must_re=[r"if\s*\(\s*refusals\s*\.\s*Count\s*>\s*0\s*\)\s*return\b"],
         before=[("JournalEntryBuilder.ClosedPeriodReasonAsync(", "_documents.VoidDocumentAsync("),
                 ("SettlementUnpostGate.Evaluate(", "_documents.VoidDocumentAsync("),
                 ("SettlementUnpostGate.Evaluate(", "_documents.VoidPaymentAsync("),
                 ("SettlementUnpostGate.Evaluate(", "_bank.UnmatchTransactionAsync("),
                 ("SettlementUnpostScope.Enter(", "_documents.VoidDocumentAsync("),
                 ("_documents.VoidDocumentAsync(", "_documents.VoidPaymentAsync("),
                 ("_documents.VoidPaymentAsync(", "_bank.UnmatchTransactionAsync(")],
         forbid=["new JournalEntry", "ExecuteDeleteAsync(", ".Remove("],
         why="C-2: ด่านภาษี/e-Tax/50 ทวิ ของทุกชิ้นก่อนแตะชิ้นแรก (ถูกปฏิเสธกลางทาง = สมุดครึ่งกลับ) · ลำดับคงที่: เอกสาร (ฝั่งขายก่อน) → "
             "การรับชำระ → ถอนจับคู่ธนาคาร → กลับ JE · C-8 สิทธิ์ใน service · ยกเลิกผ่านเส้นปกติ (ไม่ลบแถว)"),
    dict(file=SETTLE_POST, method="CreateOrAdoptAsync",
         call_args=[("_documents.CreateDocumentAsync(", "autoApproveBy: userId")],
         why="คำตัดสินเจ้าของข้อ 7: ใบที่อนุมัติทันทีตอนสร้างต้องบันทึกคนกดลงบัญชีเป็นผู้อนุมัติ (ไม่ใช่ป้ายของระบบ)"),
    dict(file=SETTLE_POST, method="BuildGateAsync", must=["SettlementPostingGate.SodSelfApproval(", "SodBlockSelfApproval", "batch.CreatedBy"],
         why="คำตัดสินเจ้าของข้อ 7: แยกหน้าที่ ผู้นำเข้า ≠ ผู้กดลงบัญชี เมื่อบริษัทเปิด SoD — ห้ามข้ามเงียบ"),
    dict(file=DOCSVC, method="CreateDocumentAsync", must=["autoApproveBy ?? createdBy"],
         why="คำตัดสินเจ้าของข้อ 7: ผู้อนุมัติอัตโนมัติของใบสำคัญจ่ายเงินสด = ผู้ที่ผู้เรียกระบุ"),
    dict(file=SETTLE_POST, method="LoadUnpostFactsAsync",
         must=["TaxFilingLockPolicy.DeclaredOrFiledStatuses", "EtaxStatus.Accepted", "FilingLockedAt", "WithholdingTaxCertStatus.Voided",
               "DocumentVoidPreconditions.ChildBlocksAsync(", "d.InputVatPostedAsUndue", "d.InputVatBecameClaimableAt", "d.OutputVatDueAt",
               "d.PaidAmount", "d.TaxPointDate", "p.ReceiptDocumentId", "acceptedReceipts.Contains(rid)"],
         why="C-2: ข้อเท็จจริงของด่านยกเลิกการลงบัญชี — ตัวโหลดเดียวของ UnpostAsync และปุ่มบนหน้าจอ (UnpostBlockersAsync) · review198-S3: "
             "ภาษีซื้อพัก/ถึงกำหนด (S3-2) · เหตุที่ VoidDocumentAsync ปฏิเสธเองจากตัวตัดสินเดียว (S3-3) · §78/1 ที่เกิดจากการรับชำระ (S3-7) · "
             "review198-S4 S4-8: จุดความรับผิด (เดือนเดียวกับ ภ.พ.30) + e-Tax ของใบเสร็จอัตโนมัติคู่การรับชำระ"),
    dict(file=SETTLE_POST, method="UnpostCoreAsync", call_args=[("SettlementUnpostGate.Evaluate(", "unpostPays")],
         why="S3-7: การรับชำระที่จะถูกยกเลิกต้องเข้าด่านด้วย (ไม่ส่ง = ด่านไม่เห็น §78/1 ของเดือนที่ยื่นแล้ว)"),
    dict(file=SETTLE_POST, method="OrphanArtifactsAsync",
         must=["LoadUnpostFactsAsync(", "SettlementUnpostGate.Evaluate(", "SettlementOrphanTriage.Split(", "OrphanChildrenAsync(",
               "SettlementPostingKeys.PaymentMarkerHead", "SettlementArtifactGuard.BatchIdFromPaymentNotes(", "d.SettlementOrphanAckAt",
               "p.Payment.SettlementOrphanAckAt"],
         call_args=[("SettlementUnpostGate.Evaluate(", "unpostPays"), ("SettlementOrphanTriage.Split(", "refusals"),
                    ("SettlementOrphanTriage.Split(", "children")],
         before=[("SettlementUnpostGate.Evaluate(", "SettlementOrphanTriage.Split("), ("OrphanChildrenAsync(", "SettlementOrphanTriage.Split(")],
         forbid=["r.ArtifactId", ".Take("],
         why="S3-6: ของกำพร้าตัดสินด้วยด่านตัวเดียวกับยกเลิกการลงบัญชี · review198-S4 S4-1: แยกกองด้วย Kind ที่ SettlementOrphanTriage ตัวเดียว "
             "(ห้ามกลับไปนับ 'ด่านปฏิเสธ = ยกเลิกไม่ได้' เองจาก ArtifactId)"),
    # ── รอบ 200 ทีม V2 (DECISIONS ข้อ 10): รับรู้ของกำพร้า — ใบที่อ้างซึ่งยกเลิกไม่ได้ ⇒ กองยกเลิกไม่ได้จริง · รับรู้ได้เฉพาะกองนั้น (สิทธิ์ + เหตุผล +
    #    ล็อกช่องทาง + ตัวแยกตัวเดียว + audit chain) · รับรู้แล้วไม่บล็อก / ยังไม่รับรู้บล็อก ──
    dict(file=SETTLE_POST, method="OrphanChildrenAsync",
         must=["DocumentVoidPreconditions.ChildFactsAsync(", "EtaxStatus.Accepted", "FilingLockedAt", "WhtCertVoidGuard.CheckDocumentAsync(",
               "DocumentDeliveryEvidence.DeliveredAsync(", "DocumentVoidPreconditions.EffectiveEtaxAsync(", "EtaxSubmitted:"],
         must_re=[r"r\s*\.\s*CompanyId\s*==\s*companyId"],
         forbid=["DocumentStatus.Sent", "e.Status == EtaxStatus.Accepted"],
         why="ข้อ 10: ใบที่อ้างของกำพร้าชุดเดียวกับที่ VoidDocumentAsync ปฏิเสธ (ตัวโหลดเดียว) + ข้อเท็จจริงว่าใบนั้นเองยกเลิกได้ไหม · tenant"),
    dict(file=SETTLE_POST, method="AcknowledgeOrphanAsync",
         must=["SettlementOrphanTriage.AckReasonProblem(", "PermissionKeys.SettlementPost", "JobLock.RunExclusiveAsync(",
               "SettlementChannelLock.Scope", "AcknowledgeOrphanCoreAsync(", "SettlementArtifactGuard.BatchIdFromCreator(",
               "SettlementArtifactGuard.BatchIdFromPaymentNotes("],
         must_re=[r"if\s*\(\s*!\s*await\s+_perms\s*\.\s*HasPermissionAsync\s*\([^;]*PermissionKeys\s*\.\s*SettlementPost\s*\)\s*\)\s*return\b"],
         before=[("SettlementOrphanTriage.AckReasonProblem(", "JobLock.RunExclusiveAsync("),
                 ("PermissionKeys.SettlementPost", "JobLock.RunExclusiveAsync(")],
         why="ข้อ 10: รับรู้ของกำพร้าต้องมีเหตุผล + สิทธิ์ Settlement.Post ตรวจใน service (ไม่ใช่แค่ controller) ก่อนแตะอะไร · ล็อกช่องทางตัวเดียวกับลงบัญชี"),
    dict(file=SETTLE_POST, method="AcknowledgeOrphanCoreAsync",
         must=["OrphanArtifactsAsync(", "SettlementOrphanTriage.AckRefusal(", "_db.AddChainedAuditLog(", "SettlementOrphanAckAt = now",
               "SettlementOrphanAckBy = userId", "SettlementOrphanAckReason = reason"],
         must_re=[r"AckRefusal\s*\([^;]*\)\s*is\s+string\s+refusal\s*\)\s*return\b"],
         before=[("SettlementOrphanTriage.AckRefusal(", "BeginTransactionAsync("), ("OrphanArtifactsAsync(", "SettlementOrphanTriage.AckRefusal(")],
         forbid=["AuditLogs.Add(", "ExecuteUpdateAsync("],
         why="ข้อ 10: รับรู้ได้เฉพาะกองยกเลิกไม่ได้จริง — ตัดสินด้วยตัวแยกตัวเดียวกับด่านลงบัญชี (ข้อเท็จจริงสดใต้ล็อก) ก่อนประทับ · ผู้/เวลา/เหตุผล + audit ใน hash chain"),
    dict(file=SETTLE_POST, method="BuildGateAsync", must=["orphans.Acknowledged", "orphans.Items", "lines.Select(l => l.CreatedBy)"],
         why="ข้อ 10: ของกำพร้าที่รับรู้แล้วต้องถึงด่าน (แสดง ไม่บล็อก) และถึงหน้าจอ · S3-11: ผู้เติมไฟล์เข้ารอบเดิมนับเป็นผู้ทำใน SoD"),
    dict(file=SETTLE_POST, method="PreviewAsync", must=["gate.OrphanItems"],
         why="ข้อ 10: หน้าจอเห็นของกำพร้ารายชิ้น (กอง · ผู้รับรู้ · ปุ่มรับรู้) จากตัวแยกเดียวกับด่าน"),
    dict(file=SETTLE_POST, method="UnpostBlockersAsync", forbid=["await LoadAsync("],
         why="S3-11: หน้ารอบโอน (GET) อ่านแค่หัวรอบ — ไม่โหลดบรรทัดทั้งรอบ และไม่พังเมื่อช่องทางถูกลบ"),
    dict(file="Controllers/SettlementController.cs", method="AcknowledgeOrphan", must=["_posting.AcknowledgeOrphanAsync(", "Conflict("],
         why="ข้อ 10: ปุ่มรับรู้เรียก service ตัวเดียว (ด่านสิทธิ์/กอง/เหตุผลอยู่ใน service) · ปฏิเสธ = 409 พร้อมเหตุ ไม่ใช่ 200"),
    dict(file=SETTLE_POST, method="BuildGateAsync", must=["orphans.Unvoidable", "orphans.NeedsUserAction", "orphans.Voidable"],
         why="S3-6/S4-1: ของกำพร้าทั้งสามกองต้องถึงด่าน — ยกเลิกไม่ได้จริง = เตือน · ต้องให้คนทำก่อน/ยกเลิกได้ = บล็อก (ไม่ส่ง = ลงซ้ำเงียบ)"),
    dict(file=SETTLE_POST, method="UnpostBlockersAsync", must=["LoadUnpostFactsAsync(", "SettlementUnpostGate.Evaluate(", "SettlementPaymentsAsync("],
         call_args=[("SettlementUnpostGate.Evaluate(", "unpostPays")],
         why="C-2: หน้าจอบอกเหตุที่ยกเลิกการลงบัญชีไม่ได้จากด่านตัวเดียวกับการกดจริง (ไม่ drift)"),
    dict(file=SETTLE_LINES, method="GetBatchAsync", must=["PostingArtifactsAsync("],
         why="C-1: มุมมองรอบโอนบอก 'ลงค้างครึ่งทาง' จากป้ายชุดเดียวกับด่าน LoadEditableBatchAsync"),
    dict(file="Controllers/SettlementController.cs", method="BatchDetailAsync",
         must=["SettlementBatchActions.For(", "batch.PostingArtifacts", "_posting.UnpostBlockersAsync("],
         why="ทีม S3: ปุ่มของหน้าจอใช้ตัวตัดสินเดียวกับด่าน service (ลงค้างครึ่งทาง · ด่านยกเลิกการลงบัญชี) — ไม่มีสำเนาเกณฑ์"),
    dict(file="Helpers/SettlementBatchActions.cs", method="For",
         must=["SettlementSaleMatch.IsEditable(status, n)", "SettlementSaleMatch.IsEditable(status)", "closed.Contains(l.LineId)",
               "p.Import", "p.Post", "p.JournalManage", "CanRedecideLines: stRedecide && p.Import"],
         why="ทีม S3: ปุ่มแก้/ยกเลิก/ลงบัญชีใช้ IsEditable ตัวเดียวกับ LoadEditableBatchAsync · review198-D D-04 chargeback ที่ปิดแล้วไม่มีปุ่ม · "
             "D-06 ปุ่มตัดด้วยสิทธิ์ของผู้ใช้"),
    # ── review198-D (ทีม D2): ผู้สมัครที่เลือกได้ · ผังที่ลงจริงในพรีวิว · บัญชีธนาคาร · สิทธิ์จำการจับคู่คอลัมน์ · chargeback ที่ปิดแล้ว ──
    dict(file=SETTLE_LINES, method="AssignLineMatchAsync",
         must=["SettlementSaleMatch.AssignRefusal(", "MatchLinesAsync(", "request.PaymentIntentId"],
         before=[("LockChannelAsync(", "MatchLinesAsync("),
                 ("SettlementSaleMatch.AssignRefusal(", "SetMatch(l, SettlementMatchStatus.Matched, null, intentId)")],
         why="D-01: เลือกรายการรับชำระได้ด้วยด่านเดียวกับการจับคู่อัตโนมัติ (ผู้สมัครคำนวณสดใต้ล็อก · AssignRefusal ตัวเดียวกับที่หน้าจอติดธง)"),
    dict(file=SETTLE_LINES, method="ToLineView", must=["SettlementSaleMatch.AssignRefusal(", "IsIntentSourced(l)"],
         why="D-01: หน้าจอติดธง 'เลือกได้/เหตุผล' ด้วยตัวตัดสินเดียวกับด่านของ AssignLineMatchAsync · บรรทัดจาก intent ไม่มีปุ่มตัดสิน"),
    dict(file=SETTLE_LINES, method="SetBankAccountAsync",
         must=["LockChannelAsync(", "LoadEditableBatchAsync(", "_db.AddChainedAuditLog("],
         must_re=[r"b\s*\.\s*CompanyId\s*==\s*companyId"],
         before=[("LockChannelAsync(", "LoadEditableBatchAsync("), ("LoadEditableBatchAsync(", "batch.BankAccountId = bank.Id")],
         forbid=["AuditLogs.Add("],
         why="D-03: เปลี่ยนบัญชีธนาคารของรอบ = ด่านเดียวกับแก้บรรทัด (ล็อกก่อนโหลด · ลงแล้ว/ค้างครึ่งทางแก้ไม่ได้) · tenant · audit"),
    dict(file=SETTLE_IMPORT, method="PersistAsync",
         must=["SettlementTxnKey.LegacyKeys(", "SettlementTxnKey.MatchByContent(", "StoredRowContentAsync(", "ContentOverlapElsewhereAsync(",
               "r.TxnDate, r.PayoutRef)"],
         forbid=["r.TxnDate, payoutRef)"],
         before=[("SettlementTxnKey.MatchByContent(", "_db.SettlementLines.Add(")],
         why="review198-S3 S3-4: คีย์ใช้เลขรอบโอนจากคอลัมน์ในไฟล์เท่านั้น (ห้ามเลขที่ผู้ใช้พิมพ์ — ไฟล์เดิมพิมพ์ต่าง = ซ้ำทั้งก้อน) · "
             "เทียบคีย์รุ่นก่อน (v1 · v2 ที่ใช้เลขพิมพ์) · ไฟล์ฉบับแก้ของรอบเดิมเทียบเนื้อหาแบบนับจำนวน · เนื้อหาตรงรอบอื่น = เตือน"),
    dict(file=SETTLE_IMPORT, method="StoredRowContentAsync", must=["SettlementTxnKey.IsRowKey(", "!claimed.Contains("],
         must_re=[r"l\s*\.\s*CompanyId\s*==\s*companyId"],
         why="S3-4: บรรทัดที่แถวในไฟล์อ้างด้วยคีย์แล้วห้ามถูกนับซ้ำด้วยเนื้อหา (แถวใหม่จริงหายเงียบ) · tenant"),
    # ทีม I รอบ 200 (S4-4): ตรรกะเทียบย้ายเข้า Helpers/SettlementContentOverlap (ตัวเดียวของผู้นำเข้า + ผู้ลงบัญชี) — ล็อกที่ตัว helper
    dict(file="Helpers/SettlementContentOverlap.cs", method="Find", must=["SettlementTxnKey.IsRowKey(", "!claimed.Contains("],
         why="S3-4: เตือนเนื้อหาตรงรอบโอนอื่น — เฉพาะบรรทัดไม่มี id · ไม่นับบรรทัดที่ถูกอ้างด้วยคีย์แล้ว"),
    dict(file="Helpers/SettlementContentOverlap.cs", method="LoadOtherBatchesAsync",
         must_re=[r"l\s*\.\s*CompanyId\s*==\s*companyId", r"l\s*\.\s*ChannelId\s*==\s*channelId"],
         must=["StartsWith(RowKeyPrefix)", "StartsWith(LegacyRowKeyPrefix)", "l.Batch.Status"],
         why="S3-4/S4-4: tenant + ช่องทางเดียวกัน · I-11 กรองคำนำหน้าคีย์แถวไม่มี id ใน SQL (ไม่ดึงบรรทัดที่มี id ทั้งวัน) + สถานะรอบให้คำเตือน"),
    dict(file="Helpers/SettlementContentOverlap.cs", method="Annotate", must=["RefsWithStatus(", "WhatToDo("],
         why="I-11: คำเตือนบอกรอบที่ลงบัญชีแล้ว และห้ามชักชวนให้ยกเลิกรอบที่ลงบัญชีแล้ว (ทางไปต่อตัวเดียวกับตอนนำเข้า)"),
    dict(file=SETTLE_IMPORT, method="ContentOverlapElsewhereAsync",
         must=["SettlementContentOverlap.RefsWithStatus(", "SettlementContentOverlap.WhatToDo("],
         why="I-11: ข้อความตอนนำเข้าใช้ทางไปต่อตัวเดียวกับพรีวิว/ลงบัญชี"),
    dict(file=SETTLE_IMPORT, method="PersistAsync", must=["SettlementBankAccountRule.MissingForImport("],
         why="D-03: ระบบไม่เลือกบัญชีธนาคารให้ — รอบใหม่ที่มีเงินโอนต้องระบุบัญชี"),
    dict(file=SETTLE_POST, method="PreviewAsync", must=["gate.Accounts.Described", "BuildGateAsync("],
         why="D-02: พรีวิวแสดงผังที่จะลงจริง (ตัวหาผังเดียวกับการลง) ไม่ใช่ผังมาตรฐานของบทบาท"),
    dict(file="Helpers/SettlementPosting.cs", method="Describe", must=["ResolveOne("],
         why="D-02: ผังในพรีวิวมาจาก ResolveOne ตัวเดียวกับ JE/ใบค่าธรรมเนียม — ห้ามคำนวณผังของพรีวิวเอง"),
    dict(file=SETTLE_POST, method="ResolveChargebackCoreAsync", must=["OpenChargebackEntries("],
         why="D-04: นิยาม 'ปิดไว้แล้ว' ตัวเดียวกับปุ่มบนหน้าจอ (ClosedChargebacksAsync)"),
    dict(file=SETTLE_POST, method="ClosedChargebacksAsync", must=["OpenChargebackEntries(", "SettlementPostingKeys.ChargebackReference"],
         why="D-04: หน้าจอตัดปุ่มแพ้/ชนะของ chargeback ที่ปิดแล้วจากข้อเท็จจริงชุดเดียวกับด่านของ service"),
    dict(file="Controllers/SettlementController.cs", method="BatchDetailAsync",
         must=["_posting.ClosedChargebacksAsync(", "SettlementPermissionScope.Import", "SettlementPermissionScope.Post",
               "PermissionKeys.JournalManage"],
         call_args=[("SettlementBatchActions.For(", "permissions"), ("SettlementBatchActions.For(", "closedChargebacks")],
         why="D-04/D-06: ปุ่มตัดด้วย chargeback ที่ปิดแล้วและสิทธิ์ของผู้ใช้ (คีย์เดียวกับ [RequirePermission] ของ endpoint)"),
    dict(file="Controllers/SettlementController.cs", method="ImportFile",
         must=["SettlementPermissionScope.ColumnMapMemory(", "OwnerActionGuard.IsApiKeyRequest(", "SettlementPermissionScope.Channels",
               "HeaderPresent(", "SettlementPermissionScope.ColumnMapMemoryBlocker("],
         call_args=[("_import.ImportFileAsync(", "effective"), ("_import.ImportFileAsync(", "memoryBlocked")],
         before=[("SettlementPermissionScope.ColumnMapMemory(", "_import.ImportFileAsync("), ("HeaderPresent(", "_import.ImportFileAsync(")],
         why="D-P2: จำการจับคู่คอลัมน์ = ค่าตั้งของช่องทาง ⇒ สิทธิ์ Channels + ห้ามคีย์ API (ไม่ผ่าน = ไม่จำ + บอก) · D-07 หัวรอบโอน null ⇒ 400"),
    dict(file="Controllers/SettlementController.cs", method="ImportFromIntents", must=["HeaderPresent("],
         before=[("HeaderPresent(", "_import.ImportFromPaymentIntentsAsync(")],
         why="D-07: \"header\": null ⇒ 400 ภาษาไทย ไม่ใช่ NullReferenceException 500"),
    dict(file="Controllers/SettlementController.cs", method="ResolveChargeback",
         must_re=[r"request\s*\?\s*\.\s*Won\s+is\s+not\s+bool\s+won\s*\)\s*return\s+BadRequest\b"],
         call_args=[("_posting.ResolveChargebackAsync(", "won")],
         why="D-08: ไม่ส่ง won ต้องไม่กลายเป็น 'แพ้' (ลง JE ขาดทุนเงียบ ๆ)"),
    dict(file="Controllers/SettlementController.cs", method="ListBatches",
         must=["SettlementReferenceCatalog.IsListable("],
         why="D-05: กรองสถานะที่ไม่มีทางอยู่ในรายการ (ยกเลิกแล้ว = soft-delete) ต้องบอกเหตุผล ไม่ใช่ 200 กับของว่าง"),
    # ── ทีม S3: ด่านนอกโฟลเดอร์ settlement ที่ C-2/C-5 ต้องการ ──
    dict(file=DOCSVC, method="VoidDocumentAsync",
         must=["SettlementArtifactGuard.CheckAsync(", "SettlementArtifactGuard.BatchIdFromCreator(",
               "SettlementArtifactGuard.CheckDocumentPaymentsAsync(", "WhtCertVoidGuard.CheckDocumentAsync("],
         before=[("SettlementArtifactGuard.CheckAsync(", "BeginTransactionAsync("),
                 ("SettlementArtifactGuard.CheckDocumentPaymentsAsync(", "BeginTransactionAsync("),
                 ("WhtCertVoidGuard.CheckDocumentAsync(", "BeginTransactionAsync(")],
         why="C-5: ชิ้นของรอบโอนที่ลงบัญชีแล้วยกเลิกทีละใบไม่ได้ (ต้องผ่าน Unpost) · C-2: เอกสารที่ 50 ทวิ ยื่นแล้วห้ามหายโดยใบรับรองค้าง"),
    dict(file=DOCSVC, method="VoidDocumentAsync",
         must=["DocumentVoidPreconditions.ChildBlocksAsync(", "SettlementArtifactGuard.CheckLockedAsync(", "lockBatchRows: true"],
         before=[("DocumentVoidPreconditions.ChildBlocksAsync(", "BeginTransactionAsync("),
                 ("BeginTransactionAsync(", "SettlementArtifactGuard.CheckLockedAsync("),
                 ("SettlementArtifactGuard.CheckLockedAsync(", "LockDepositBalancesAsync(")],
         forbid=["d.Reference == docNumber"],
         why="review198-S3 S3-3: เหตุ 'มีเอกสารอ้าง' มาจากตัวตัดสินเดียวกับด่านยกเลิกการลงบัญชี (ห้ามสำเนา inline) · S3-8: ด่าน C-5 ซ้ำใต้ธุรกรรม "
             "(แถวรอบโอน FOR SHARE) ก่อนแตะอะไร"),
    dict(file=DOCSVC, method="VoidPaymentAsync", must=["SettlementArtifactGuard.CheckLockedAsync("],
         before=[("BeginTransactionAsync(", "SettlementArtifactGuard.CheckLockedAsync("),
                 ("SettlementArtifactGuard.CheckLockedAsync(", "ReversePaymentInternalAsync(")],
         why="S3-8: ด่าน C-5 ซ้ำใต้ธุรกรรมก่อนกลับรายการการรับชำระ"),
    dict(file="Services/Implementations/PayrollService.cs", method="IssueMonthlyPnd1CertsAsync", must=["WhtCertVoidGuard.CheckAsync("],
         must_lit=['catch (BusinessRuleException filed) when (filed.RuleCode == "RD-50TWI-FILED")'],
         before=[("WhtCertVoidGuard.CheckAsync(", "ex.Status = WithholdingTaxCertStatus.Voided"),
                 ("catch (BusinessRuleException filed)", "catch (Exception ex)")],
         why="review198-S3 S3-10: 50 ทวิ ภ.ง.ด.1 ที่ยื่นแล้วห้ามถูกยกเลิกเงียบตอน re-post — ตัวตัดสินเดียว WhtCertVoidGuard · review198-S4 S4-6: "
             "เหตุนี้แจ้งทางไปต่อของตัวเอง (ยื่นเพิ่มเติม) ก่อนตกไป catch ทั่วไปที่บอกให้กด 'สร้างเอกสารใหม่' ซึ่งล้มซ้ำตลอด"),
    dict(file="Helpers/SettlementPostingGuards.cs", method="CheckLockedAsync", must=["LockBatchRowAsync(", "CheckAsync("],
         before=[("LockBatchRowAsync(", "CheckAsync(")],
         why="S3-8: อ่านแถวรอบโอนแบบล็อกก่อนตัดสิน (รอการประทับ Posted ที่กำลังทำ) · รอบ 200 ทีม V1: ตัวล็อกย้ายเป็น LockBatchRowAsync ตัวเดียว"),
    dict(file="Helpers/SettlementPostingGuards.cs", method="LockBatchRowAsync", must=["ExecuteSqlRawAsync("], must_lit=["FOR SHARE"],
         why="S3-8: ล็อกแถวรอบโอน FOR SHARE (ตัวเดียวของ CheckLockedAsync และ CheckDocumentPaymentsAsync)"),
    dict(file="Helpers/SettlementPostingGuards.cs", method="CheckDocumentPaymentsAsync",
         must=["BatchIdFromPaymentNotes(", "BatchStateAsync(", "PaidDocumentVoidReason(", "SettlementUnpostScope.IsUnposting("],
         before=[("LockBatchRowAsync(", "BatchStateAsync("), ("BatchStateAsync(", "PaidDocumentVoidReason(")],
         call_args=[("LockBatchRowAsync(", "companyId")],
         why="รอบ 200 ทีม V1 (คำตัดสินข้อ 9 · S3-5): ใบขายที่รอบโอน Posted รับชำระ — ข้อความ 409 ต้องพาไปทางที่ถูก (ยกเลิกและออกใบแทน · "
             "ใบลดหนี้ · ยกเลิกการลงบัญชี) ทุกทางเข้า · ล็อกแถวรอบโอนก่อนอ่านเมื่อเรียกใต้ธุรกรรม (S3-8)"),
    dict(file=DOCSVC, method="VoidPaymentAsync",
         must=["SettlementArtifactGuard.CheckAsync(", "SettlementArtifactGuard.BatchIdFromPaymentNotes("],
         before=[("SettlementArtifactGuard.CheckAsync(", "BeginTransactionAsync(")],
         why="C-5: การรับชำระของรอบโอนที่ลงบัญชีแล้วยกเลิกทีละรายการไม่ได้ (ต้องผ่าน Unpost)"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="VoidAsync",
         must=["WhtCertVoidGuard.CheckAsync("],
         before=[("WhtCertVoidGuard.CheckAsync(", "cert.Status = WithholdingTaxCertStatus.Voided")],
         why="C-2: 50 ทวิ ที่อยู่ในแบบ ภ.ง.ด. ที่ยื่นแล้วยกเลิกไม่ได้ (ตัวตัดสินเดียวกับด่าน Unpost)"),
]

# ── รอบ 200 ทีม T (เวลา/ภาษีของรอบโอน): เทสต์ล็อกตัวตัดสิน pure (SettlementWalletContinuity · SettlementCrossBatchReceipts ·
#    SettlementSummarySupplement · SettlementStock · LegacyMoneyLegAudit) — ที่นี่ล็อกว่า service หาข้อเท็จจริงแล้วส่งถึงด่านจริง ──
RULES += [
    dict(file=SETTLE_POST, method="BuildGateAsync",
         must=["WalletContinuityAsync(", "PendingReceiptsElsewhereAsync(", "SettlementStock.StanceOf(", "ResolveWhtFormType(",
               "TaxType.VatPp36", "Wallet: wallet", "FiledPp36Periods: filedPp36", "WhtFormType: whtForm", "Supplementary: supplementary",
               "Stock: stock", "pend?.Amount", "supplementary.Where(s => s.FirstIssued)"],
         must_re=[r"t\s*\.\s*TaxType\s*==\s*whtForm"],
         forbid=["t.TaxType == TaxType.WithholdingTax53"],
         before=[("WalletContinuityAsync(", "SettlementPostingGate.Evaluate("),
                 ("PendingReceiptsElsewhereAsync(", "SettlementPostingGate.Evaluate("),
                 ("ResolveWhtFormType(", "SettlementPostingGate.Evaluate(")],
         why="รอบ 200 ทีม T: R-A12 ความต่อเนื่องของ wallet · R-B13 รับชำระเกินข้ามรอบ · C-11 เดือนที่ยื่นแล้วของแบบ ภ.ง.ด. ที่ 50 ทวิ จะเป็นจริง "
             "(ตัวเลือกเดียวกับผู้ออกใบรับรอง) + ภ.พ.36 · C-15 สต็อก · ข้อ 15 ใบสรุปเพิ่มเติม — ข้อเท็จจริงไม่ถึงด่าน = ด่านไม่มี"),
    dict(file=SETTLE_POST, method="WalletContinuityAsync",
         must=["SettlementWalletContinuity.Judge(", "SettlementBatchStatus.Voided", "b.ChannelId == batch.ChannelId", "!b.IsDeleted"],
         must_re=[r"b\s*\.\s*CompanyId\s*==\s*companyId"],
         why="R-A12: รอบก่อนหน้า = ช่องทางเดียวกัน · ไม่นับรอบที่ยกเลิก/ลบ · tenant"),
    dict(file=SETTLE_POST, method="PendingReceiptsElsewhereAsync",
         must=["SettlementCrossBatchReceipts.PendingElsewhere(", "SettlementPostingKeys.PaymentMarker(", "l.BatchId != batchId",
               "SettlementBatchStatus.Matched"],
         must_re=[r"l\s*\.\s*CompanyId\s*==\s*companyId", r"p\s*\.\s*CompanyId\s*==\s*companyId"],
         forbid=["SettlementBatchStatus.Posted"],
         why="R-B13: นับเฉพาะรอบโอนอื่นที่ยังไม่ลงบัญชี (ที่ลงแล้วลดยอดค้างของใบไปแล้ว) · รอบที่รับชำระใบนั้นไปแล้วไม่นับซ้ำ · tenant"),
    dict(file=SETTLE_POST, method="DuplicateSalesAsync",
         must=["SettlementSummarySupplement.Judge(", "DocumentStatusRules.IsIssued(", "other.Dead"],
         why="คำตัดสินรอบ 200 ข้อ 15: ใบสรุปวันเดียวกันของรอบที่ยังมีผล = ใบสรุปเพิ่มเติม (ไม่บล็อก) · ของรอบที่ยกเลิกแล้วยังเป็นรายได้ซ้ำ"),
    dict(file=SETTLE_POST, method="EnsureSummaryDocumentAsync",
         call_args=[("SettlementDocumentBuilder.SummaryDocument(", "SupplementOf")],
         why="ข้อ 15: ใบสรุปเพิ่มเติมต้องอ้างเลขใบแรกของวันบนเอกสาร (ไม่ส่ง = ใบที่สองของวันไม่มีร่องรอยว่าเป็นใบเพิ่มเติม)"),
    dict(file=SETTLE_POST, method="CommitPostedAsync", must=["_db.ChangeTracker.Clear()"],
         before=[("_db.ChangeTracker.Clear()", "BeginTransactionAsync(")],
         why="review198-C C-20: lambda ของ execution strategy เริ่มด้วยตัวติดตามว่าง — retry ต้องไม่บันทึก JE รอบแรกซ้ำ"),
    dict(file=SETTLE_POST, method="MatchCoreAsync", must=["_db.ChangeTracker.Clear()"],
         before=[("_db.ChangeTracker.Clear()", "BeginTransactionAsync(")],
         why="review198-C C-20"),
    dict(file=SETTLE_POST, method="ResolveChargebackCoreAsync", must=["_db.ChangeTracker.Clear()"],
         before=[("_db.ChangeTracker.Clear()", "BeginTransactionAsync(")],
         why="review198-C C-20"),
    dict(file="Helpers/WalkInCustomerContact.cs", method="GetOrCreateAsync",
         must=["AdvisoryLockKey.WalkInContact", "FindAsync("], must_lit=["pg_advisory_xact_lock"],
         before=[("AdvisoryLockKey.WalkInContact", "db.Set<Contact>().Add(")],
         why="review198-C C-13: ตรวจ-แล้ว-สร้างผู้ติดต่อ \"ลูกค้าเงินสด\" ใต้ล็อกคีย์คงที่ต่อบริษัท — สองเส้นพร้อมกันต้องได้แถวเดียว"),
    dict(file="Services/Implementations/JournalAnomalyService.cs", method="ScanAsync",
         must=["LegacyMoneyLegAudit.Classify(", "j.ReversedByEntryId == null"],
         why="คำตัดสินรอบ 200 ข้อ 20: JE เก่าที่ลงขาเงิน/ลูกหนี้ผิดหมวด (I-1/P-1) ต้องถึงหน้านักบัญชี — อ่านอย่างเดียว ไม่แก้อัตโนมัติ"),
]
# ── รอบ 200 ทีม V1: ยกเลิก-ออกใบแทน (คำตัดสินข้อ 9) · ใบเสร็จอัตโนมัติ vs e-Tax (ข้อ 11) · §78/1 ของการรับชำระ (ข้อ 17) ──
# เทสต์ VoidReissueR200Tests ล็อกแค่ตัวตัดสิน (pure) — ถอดการเรียกใน service แล้วเทสต์ยังเขียว ⇒ ล็อกจุดเรียก/ลำดับ/การใช้ผลที่นี่
DOC_REISSUE = "Services/Implementations/DocumentService.Reissue.cs"
CHEQUE = "Services/Implementations/Cheque/ChequeService.cs"
RULES += [
    dict(file=DOC_REISSUE, method="EvaluateSettlementPaidReissueAsync",
         must=["SettlementPaidReissue.QuickRelevance(", "SettlementArtifactGuard.CheckDocumentPaymentsAsync(", "SettlementPaidReissue.Decide(",
               "DocumentVoidPreconditions.ChildBlocksAsync(", "WhtCertVoidGuard.CheckDocumentAsync(", "IsDocumentFilingLockedAsync(",
               "DocumentVoidPreconditions.EffectiveEtaxAsync(", "DepositAppliedToDocumentId",
               "IsDocumentFilingLockedAsync(companyId, r.Id)", "ClosedPeriodNameAsync(companyId, r.DocumentDate)",
               "ClosedPeriodNameAsync(companyId, doc.DocumentDate)", "DocumentEtax = etaxById.GetValueOrDefault(doc.Id)"],
         call_args=[("SettlementArtifactGuard.CheckDocumentPaymentsAsync(", "lockBatchRows: lockRows"),
                    ("DocumentVoidPreconditions.ChildBlocksAsync(", "ignoreChildIds: liveReceiptIds")],
         before=[("SettlementPaidReissue.QuickRelevance(", "SettlementArtifactGuard.CheckDocumentPaymentsAsync(")],
         forbid=["e.Status == EtaxStatus.Accepted"],
         why="ข้อ 9: ด่านเดียวของปุ่มและการกดจริง — ด่านเดิมของยกเลิกเอกสารครบ (e-Tax ถึงกรมสรรพากร · รายงานล็อก · 50 ทวิ ยื่นแล้ว · เอกสารลูก) + "
             "ใบเสร็จอัตโนมัติที่ถึงกรมสรรพากรแล้ว · ใต้ธุรกรรมล็อกแถวรอบโอน · V1F: รายงานล็อก/งวดปิดของใบเสร็จ (R1) · ใบขาย Submitted = บล็อก (R4 — "
             "ห้ามกลับไปตรวจแค่ Accepted) · e-Tax by Email (P1) ผ่านตัวโหลดเดียว"),
    dict(file=DOC_REISSUE, method="ClosedPeriodNameAsync",
         must=["FiscalPeriodStatus.Open", "f.CompanyId == companyId"],
         why="V1F R1: งวดบัญชีที่ไม่เปิดของวันที่ใบขาย/ใบเสร็จ — ตัวเดียว (tenant)"),
    dict(file=DOC_REISSUE, method="ReissueSettlementPaidDocumentAsync",
         must=["EvaluateSettlementPaidReissueAsync(", "SettlementPaidReissue.CopyDocumentForReissue(", "SettlementPaidReissue.CopyLineForReissue(",
               "SettlementPaidReissue.ForbiddenChanges(", "SettlementPaidReissue.RequestLineIssues(", "ApprovalControlPolicy.ForReissue(",
               "RepointDocumentLinksAsync(", "ResolveNumberSeriesTypeAsync(", "DocumentNumberGenerator.NextAsync(",
               "TaxInvoiceCompletenessChecker.BuyerBlockingFields(", "TaxInvoiceCompletenessChecker.MustEnforceBuyerFields(",
               "CreateSettlementReceiptAsync(", "_db.AddChainedAuditLog(", "_issuedHooks.RunAsync(", "ReplacementCarriesPostings = true",
               "DocumentStatus.Voided", "ReplacedByDocumentId = neo.Id", "DocumentVoidPreconditions.EtaxReachedRdStatuses.Contains(e.Status)",
               "SettlementPaidReissueRequestCodec.Parse(", "old.ReissueRequestedBy = null"],
         must_re=[r"if\s*\(\s*!\s*eval\s*\.\s*Verdict\s*\.\s*Allowed\s*\)\s*throw\b",
                  r"if\s*\(\s*forbidden\s*\.\s*Count\s*>\s*0\s*\)\s*throw\b",
                  r"if\s*\(\s*control\s*\.\s*Action\s*==\s*ReissueControlAction\s*\.\s*Blocked\s*\)\s*throw\b",
                  r"if\s*\(\s*lineIssues\s*\.\s*Count\s*>\s*0\s*\)\s*throw\b",
                  r"if\s*\(\s*blockingBuyerFields\s*!=\s*null\s*\)\s*throw\b",
                  r"buyerDeclined\s*=\s*!\s*buyerChanged\s*&&"],
         call_args=[("EvaluateSettlementPaidReissueAsync(", "lockRows: true")],
         before=[("BeginTransactionAsync(", "EvaluateSettlementPaidReissueAsync("),
                 ("ApprovalControlPolicy.ForReissue(", "EvaluateSettlementPaidReissueAsync("),
                 ("EvaluateSettlementPaidReissueAsync(", "_db.Documents.Add("),
                 ("SettlementPaidReissue.ForbiddenChanges(", "ReissueControlAction.RecordRequest"),
                 ("ReissueControlAction.RecordRequest", "_db.Documents.Add("),
                 ("SettlementPaidReissue.ForbiddenChanges(", "_db.Documents.Add("),
                 ("SettlementPaidReissue.ForbiddenChanges(", "RepointDocumentLinksAsync("),
                 ("CommitAsync(", "_issuedHooks.RunAsync("),
                 ("outcome.Neo == null", "_issuedHooks.RunAsync(")],
         forbid=["ReverseJournalEntryAsync(", "ReversePaymentInternalAsync(", "ApplyStockMovementsAsync(", "AuditLogs.Add(",
                 "SettlementPaidReissue.CopyScalars(", "MissingBuyerFields(", "e.Status != EtaxStatus.Accepted"],
         why="ข้อ 9: ใบใหม่ยอด/บรรทัด/อัตรา VAT/tax point เท่าเดิม (ตาข่ายก่อนเขียน) · ย้ายผลทางบัญชี ไม่กลับรายการเงิน/สต็อก · เลขใหม่ด้วยกติกาเดียวกับตอนอนุมัติ · "
             "audit ใน hash chain · e-Tax อัตโนมัติหลัง commit นอก execution strategy (V1F P4) · V1F: SoD/วงเงินผ่าน ApprovalControlPolicy ตัวเดียวกับอนุมัติ "
             "(R6 — ต้องมีคนที่สอง = บันทึกคำขอแล้วจบก่อนแตะเงิน) · คัดลอกแบบ allowlist (R7) · ด่านของคำขอผู้ใช้ (R8) · ผู้ซื้อ §86/4 ตัวตัดสินเดียวกับเส้นอนุมัติ "
             "(R10 — ห้ามสำเนา MissingBuyerFields) · e-Tax ใบเดิมที่ถึงกรมสรรพากรไม่ถูกพลิก (R4)"),
    dict(file=DOC_REISSUE, method="RepointDocumentLinksAsync",
         must=["PaymentAllocations", "JournalEntries", "SettlementLines", "StockMovements", "WhtCreditsReceived", "ReconciliationGroupItems",
               "PosOrders", "LodgingReservations", "SiteOrders", "SiteBookings", "PaymentSourceKind.Document", "i.ReceiptDocumentId == oldId",
               "p.DocumentLineId == oldLineId", "SettlementPaidReissue.JournalDescriptionAfterMove("],
         must_re=[r"g\.CompanyId\s*==\s*companyId"],
         forbid=["ExecuteDeleteAsync(", ".Remove(", "j.Reference ="],
         why="ข้อ 9: การรับชำระ + JE ที่อ้างใบ + คู่จับของบรรทัดรอบโอน + สต็อก ชี้ใบใหม่ (tenant ทุกตาราง) · ย้ายเท่านั้น ห้ามลบ · V1F R9: PaymentIntent "
             "(SourceId/ReceiptDocumentId) · บรรทัดโครงการ · คำบรรยาย JE (Reference ห้ามแตะ — ตัวเลือก JE อ่านช่องนั้น)"),
    dict(file=DOC_REISSUE, method="CancelReissueRequestAsync",
         must=["_db.AddChainedAuditLog(", "doc.ReissueRequestedBy = null", "BeginTransactionAsync(", "ExecuteSqlRawAsync(", "CommitAsync("],
         must_re=[r"if\s*\(\s*doc\s*\.\s*ReissueRequestedBy\s*==\s*null\s*\)\s*throw\b",
                  r"if\s*\(\s*doc\s*\.\s*Status\s*==\s*DocumentStatus\s*\.\s*Voided\s*\|\|\s*doc\s*\.\s*ReplacedByDocumentId\s*!=\s*null\s*\)"],
         before=[("ExecuteSqlRawAsync(", "_db.Documents.FirstOrDefaultAsync("), ("BeginTransactionAsync(", "ExecuteSqlRawAsync(")],
         why="V1F R6: ยกเลิกคำขอที่ค้าง — ไม่มีคำขอ = บอกตรง ๆ (ไม่ใช่สำเร็จเงียบ) · audit ใน chain"),
    dict(file=DOC_REISSUE, method="ResolveEtaxCancellationAsync",
         must=["DocumentVoidPreconditions.EtaxCancellationResolution(", "DocumentVoidPreconditions.EffectiveEtaxAsync(",
               "IsDocumentFilingLockedAsync(", "OutputVatPeriodLockReasonAsync(", "ApplyAutoReceiptOnPaymentVoidAsync(", "_db.AddChainedAuditLog(",
               "DocumentVoidPreconditions.EtaxCancellationFollowUp(", "LiveVatReceiptExistsAsync(", "ReclassLockReasonAsync(",
               "rcpt.EtaxCancelRequiredAt = null", "DocumentVoidPreconditions.EvidenceLabel(", "rcpt.EtaxCancelledByCreditNoteId = creditNote",
               "UndoUndueOutputVatReclassAsync(", "CreateSettlementReceiptAsync(", "_issuedHooks.RunAsync(", "a.EntityId == rcpt.Id",
               "DocumentPermissionHelper.CanApproveAsync("],
         must_lit=["EtaxReissueReview.ResolvedMarker"],
         must_re=[r"if\s*\(\s*!\s*verdict\s*\.\s*Allowed\s*\)\s*throw\b",
                  r"if\s*\(\s*!\s*plan\s*\.\s*Allowed\s*\)\s*throw\b"],
         call_args=[("ApplyAutoReceiptOnPaymentVoidAsync(", "keepVisible: true"),
                    ("TryReclassifyUndueOutputVatAsync(", "throwOnFailure: true"),
                    ("CreateSettlementReceiptAsync(", "carryVatFromSource: true")],
         before=[("lockSrcId, companyId", "var rcpt = await"),
                 ("DocumentVoidPreconditions.EtaxCancellationResolution(", "ApplyAutoReceiptOnPaymentVoidAsync("),
                 ("IsDocumentFilingLockedAsync(", "ApplyAutoReceiptOnPaymentVoidAsync("),
                 ("OutputVatPeriodLockReasonAsync(", "ApplyAutoReceiptOnPaymentVoidAsync("),
                 ("DocumentVoidPreconditions.EtaxCancellationFollowUp(", "ApplyAutoReceiptOnPaymentVoidAsync("),
                 ("DocumentVoidPreconditions.EtaxCancellationFollowUp(", "UndoUndueOutputVatReclassAsync("),
                 ("CommitAsync(", "_issuedHooks.RunAsync(")],
         forbid=["etax.Status = EtaxStatus.", "EtaxStatus.Accepted;", "AuditLogs.Add(", "ClosedPeriodNameAsync(", "IsDeleted = true",
                 "TryUndoUndueOutputVatReclassAsync("],
         why="V1F R3 (ข้อ 11) + V1G (ข้อ 46–48): ปิดธงต้องมีหลักฐาน — ระบบห้ามประทับสถานะของกรมสรรพากรเอง · แยกทางยกเลิก/ใบลดหนี้ (RV1F-1) · "
             "ไฟล์หลักฐานเป็นของใบนี้จริง (RV1F-4) · ใบเสร็จคงแสดงเป็น Voided (RV1F-10) · ด่านงวดของทุกวันที่ที่จะเขียน + ถอยภาษีลงเดือนที่ตั้งรายการ "
             "(RV1F-2) · มีเงินรับที่มีผล ⇒ ออกใบกำกับ ณ วันรับเงินในธุรกรรมเดียว (RV1F-3) · ล็อกใบต้นทางก่อนใบเสร็จ (RV1F-8) · ป้ายหลักฐานตามความจริง (RV1F-9)"),
    dict(file=DOCSVC, method="GetDocumentAsync", call_args=[("EvaluateSettlementPaidReissueAsync(", "lockRows: false")],
         why="ข้อ 9: ปุ่ม 'ยกเลิกและออกใบแทน' ตัดสินด้วยตัวเดียวกับ endpoint (ไม่มีเกณฑ์สำเนาฝั่ง JS)"),
    dict(file=DOCSVC, method="VoidPaymentAsync",
         call_args=[("ReversePaymentInternalAsync(", "cause: cause"), ("ReverseMultiDocPaymentInternalAsync(", "cause: cause")],
         why="ข้อ 11: ทางเข้าของการยกเลิกการชำระ (ผู้ใช้/ยกเลิกการลงบัญชี/เช็คเด้ง) ต้องถึงตัวตัดสินใบเสร็จ e-Tax"),
    dict(file=DOCSVC, method="ReversePaymentInternalAsync",
         must=["DecideAutoReceiptOnPaymentVoidAsync(", "ApplyAutoReceiptOnPaymentVoidAsync(", "UndoOutputVatOnPaymentVoidAsync("],
         call_args=[("UndoOutputVatOnPaymentVoidAsync(", "cause")],
         before=[("DecideAutoReceiptOnPaymentVoidAsync(", "ReverseJournalEntryAsync("),
                 ("ApplyAutoReceiptOnPaymentVoidAsync(", "UndoOutputVatOnPaymentVoidAsync(")],
         forbid=["rcpt.Status = DocumentStatus.Voided", "UndoUndueOutputVatReclassAsync(", "DocumentVoidPreconditions.OutputVatUndoOnPaymentVoid("],
         why="ข้อ 11: ตัดสินใบเสร็จอัตโนมัติก่อนแตะ JE (ปฏิเสธ = ไม่มีอะไรถูกเขียน) · ห้ามกลับไปประทับ Voided เองโดยไม่ดู e-Tax · "
             "ข้อ 50 (V1H): ถอยภาษีผ่านตัวเดียวกับเส้นหลายใบ — ห้ามมีสำเนาตรรกะถอยภาษีในเมธอดนี้"),
    dict(file=DOCSVC, method="UndoOutputVatOnPaymentVoidAsync",
         must=["DocumentVoidPreconditions.ReceiptHoldsTaxPointFor(", "LiveVatReceiptExistsAsync(", "DocumentVoidPreconditions.ShouldUndoOutputVatReclass(",
               "DocumentVoidPreconditions.OutputVatUndoOnPaymentVoid(", "ReclassLockReasonAsync(", "OutputVatUndoAction.KeepAndFlag",
               "UndoUndueOutputVatReclassAsync("],
         must_re=[r"OutputVatUndoAction\s*\.\s*Refuse\s*\)\s*throw\b"],
         call_args=[("DocumentVoidPreconditions.OutputVatUndoOnPaymentVoid(", "cause"),
                    ("LiveVatReceiptExistsAsync(", "exceptReceiptId: receiptDoc?.Id")],
         before=[("DocumentVoidPreconditions.ShouldUndoOutputVatReclass(", "UndoUndueOutputVatReclassAsync("),
                 ("DocumentVoidPreconditions.OutputVatUndoOnPaymentVoid(", "UndoUndueOutputVatReclassAsync(")],
         forbid=["catch", "DocumentVoidPreconditions.FlaggedReceiptKeepsTaxPoint("],
         why="ข้อ 48 + 50 (V1H): ตัวถอยภาษีขายถึงกำหนดตัวเดียวของเส้นใบเดียวและหลายใบ — ใบเสร็จติดธงถือจุดความรับผิดของใบที่มันอ้างเท่านั้น · "
             "ด่านงวดก่อนถอย · ผู้ใช้ = 409 · ห้ามกลืน error"),
    dict(file=DOCSVC, method="ReverseMultiDocPaymentInternalAsync",
         must=["DecideAutoReceiptOnPaymentVoidAsync(", "ApplyAutoReceiptOnPaymentVoidAsync(", "UndoOutputVatOnPaymentVoidAsync(", "vatNotices"],
         call_args=[("UndoOutputVatOnPaymentVoidAsync(", "cause")],
         before=[("DecideAutoReceiptOnPaymentVoidAsync(", "ReverseJournalEntryAsync("),
                 ("ad.PaidAmount = Math.Max(", "UndoOutputVatOnPaymentVoidAsync("),
                 ("ApplyAutoReceiptOnPaymentVoidAsync(", "UndoOutputVatOnPaymentVoidAsync(")],
         forbid=["rcpt.Status = DocumentStatus.Voided", "UndoUndueOutputVatReclassAsync("],
         why="ข้อ 11: เส้นหลายใบเดินด่านเดียวกับเส้นใบเดียว (R5 ทางเข้าอื่น) · ข้อ 50 (V1H): ถอยภาษีขายรายใบหลังคืนยอดของใบนั้น ผ่านตัวเดียวกับเส้นใบเดียว"),
    dict(file=DOCSVC, method="DecideAutoReceiptOnPaymentVoidAsync",
         must=["DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(", "DocumentVoidPreconditions.EffectiveEtaxAsync(", "rcpt.DocumentNumber"],
         must_re=[r"AutoReceiptEtaxAction\.Refuse\s*\)\s*throw\b"],
         why="ข้อ 11: ตัวตัดสินตัวเดียว (pure) · ปฏิเสธต้อง throw (ทิ้งผล = ใบที่กรมสรรพากรรับแล้วถูกประทับ Voided เงียบ)"),
    dict(file=DOCSVC, method="ApplyAutoReceiptOnPaymentVoidAsync",
         must=["AutoReceiptEtaxAction.FlagEtaxCancellation", "EtaxCancelRequiredAt", "EtaxCancelRequiredReason",
               "DocumentVoidPreconditions.EtaxReachedRdStatuses"],
         why="ข้อ 11: เช็คเด้ง = ธงที่มองเห็นแทนการประทับ Voided · ใบที่ยกเลิกได้ ยกเลิก e-Tax ที่ยังไม่ถึงกรมสรรพากรตาม"),
    dict(file=DOCSVC, method="RestoreVoidedDocumentAsync", must=["ReplacementCarriesPostings", "doc.ReplacedByDocumentId"],
         must_lit=["DOC-RESTORE-REPLACED"],
         why="ข้อ 9: ใบเดิมที่ยกเลิกและออกใบแทนแล้ว กู้คืนไม่ได้ (ผลทางบัญชีอยู่ที่ใบแทน — กู้คืน = รายได้/ภาษีขายซ้ำ)"),
    dict(file=DOCSVC, method="ApproveDocumentAsync#2",
         must_re=[r"isFullTaxInvoiceReplacement\s*=\s*doc\.ReplacesDocumentId\.HasValue\s*&&\s*!\s*doc\.ReplacementCarriesPostings"],
         must=["ResolveNumberSeriesTypeAsync("],
         why="ข้อ 9: ใบแทนที่ถือผลทางบัญชีเอง (ยกเลิกและออกใบแทน) อนุมัติใหม่ต้องลง JE/สต็อก · ชุดเลขกติกาเดียวกับใบแทน"),
    dict(file=DOCSVC, method="VoidDocumentAsync",
         must_re=[r"isReplacementDoc\s*=\s*doc\.ReplacesDocumentId\.HasValue\s*&&\s*!\s*doc\.ReplacementCarriesPostings"],
         why="ข้อ 9: ยกเลิกใบแทนที่ถือผลทางบัญชีเอง = คืนสต็อก/ยอดโครงการตามปกติ (ไม่ข้ามแบบใบแทนกระดาษ)"),
    dict(file=CHEQUE, method="MarkBouncedAsync",
         call_args=[("VoidPaymentAsync(", "PaymentVoidCause.ChequeBounce")],
         must=["voided.OutputVatNotice"],
         why="ข้อ 11: เช็คเด้งห้ามบล็อก — ต้องบอกตัวตัดสินว่าเป็นเช็คเด้ง (ติดธง ไม่ปฏิเสธ)"),
    dict(file=SETTLE_POST, method="UnpostCoreAsync",
         call_args=[("_documents.VoidPaymentAsync(", "PaymentVoidCause.SettlementUnpost")],
         must=["etaxFlags.Add(voidResult.EtaxCancellationFlag)", "etaxFlags.Add(voidResult.OutputVatNotice)"],
         why="ข้อ 11: ยกเลิกการลงบัญชีรอบโอนเป็นทางเข้าที่ด่านปฏิเสธใบเสร็จ e-Tax ไว้ก่อนแล้ว · V1F P2: ใบที่ถูกส่งระหว่างทางติดธง (ไม่ throw กลางลูป) "
             "และบอกในผลลัพธ์ (ห้ามทิ้งผล)"),
    dict(file=SETTLE_POST, method="LoadUnpostFactsAsync",
         must=["DocumentVoidPreconditions.EtaxReachedRdStatuses", "submittedReceipts.Contains(sid)", "ReceiptEtaxSubmitted:",
               "DocumentVoidPreconditions.EffectiveEtaxAsync("],
         why="ข้อ 11: ด่านยกเลิกการลงบัญชีต้องเห็นใบเสร็จที่ส่ง e-Tax แล้ว (ชุดสถานะเดียวกับ VoidPaymentAsync) ก่อนแตะชิ้นแรก"),
    # ── รอบ 200 ทีม V1F (แก้ผลฝ่ายค้าน review200-V1) ──
    dict(file=DOCSVC, method="ApproveDocumentAsync#2",
         must=["ApprovalControlPolicy.NeedsSignatureFlow(", "ApprovalControlPolicy.SelfApprovalBlocked(",
               "TaxInvoiceCompletenessChecker.MustEnforceBuyerFields(", "TaxInvoiceCompletenessChecker.BuyerBlockingFields("],
         must_re=[r"if\s*\(\s*blockingBuyerFields\s*!=\s*null\s*\)"],
         forbid=["rd864Types", "SodBlockSelfApproval: true"],
         why="V1F R6/R10: เกณฑ์วงเงินเซ็นหลายขั้น + SoD + ผู้ซื้อ §86/4 อยู่ที่ตัวตัดสินเดียว (เส้นยกเลิกและออกใบแทนถามตัวเดียวกัน) — ห้ามกลับไปเขียน inline"),
    dict(file=DOCSVC, method="CreatePaymentAsync",
         call_args=[("CarriesTaxInvoiceRole(", "liveVatReceiptExists")],
         why="V1F R2: ใบกำกับของการขายนี้ออกไปแล้ว (ใบเสร็จถือ VAT ที่ติดธงยังมีผล) ⇒ รับชำระใหม่ต้องไม่ออกใบกำกับใบที่สอง"),
    dict(file=DOCSVC, method="IssueReceiptForPaymentAsync",
         call_args=[("CarriesTaxInvoiceRole(", "liveVatReceiptExists")],
         why="V1F R2: เส้นออกใบเสร็จย้อนหลังใช้กติกาเดียวกับเส้นรับชำระ (ห้ามออกใบกำกับใบที่สองของการขายเดียว)"),
    dict(file=INTEGRATION, method="VoidDocumentByExternalRefAsync",
         must=["SettlementPaidReissue.IntegrationVoid(", "IntegrationVoidKind.Replaced", "IntegrationVoidKind.AlreadyVoided"],
         must_re=[r"OrderBy\(\s*d\s*=>\s*d\.Status\s*==\s*DocumentStatus\.Voided",
                  r"if\s*\(\s*voidOutcome\s*\.\s*Kind\s*==\s*Accounting\s*\.\s*Helpers\s*\.\s*IntegrationVoidKind\s*\.\s*Replaced\s*\)\s*throw\b"],
         before=[("SettlementPaidReissue.IntegrationVoid(", "_documentService.VoidDocumentAsync(")],
         forbid=["doc.Status == DocumentStatus.Voided"],
         why="V1F R5: ยกเลิกผ่าน integration ด้วยอ้างอิงของใบที่ถูกแทน ⇒ บอกเลขใบแทน (ห้ามตอบ already voided สำเร็จ = HTTP 200 โกหก) · "
             "ค้นด้วย ExternalRef เลือกใบที่ยังมีผลก่อน + เรียงชัด"),
    dict(file="Controllers/DocumentController.cs", method="ResolveEtaxCancellation",
         must=["DocumentPermissionHelper.CanVoidAsync(", "ResolveEtaxCancellationAsync(", "gate.DenyAttachmentAsync(", "request.EvidenceAttachmentId"],
         before=[("DocumentPermissionHelper.CanVoidAsync(", "ResolveEtaxCancellationAsync("),
                 ("gate.DenyAttachmentAsync(", "ResolveEtaxCancellationAsync(")],
         why="V1F R3: ปิดธงต้องใช้สิทธิ์ยกเลิกเอกสาร (ยกเลิกใบเสร็จ)"),
    dict(file="Controllers/DocumentController.cs", method="CancelReissueRequest",
         must=["DocumentPermissionHelper.CanVoidAsync(", "DocumentPermissionHelper.CanApproveAsync(", "CancelReissueRequestAsync("],
         before=[("DocumentPermissionHelper.CanApproveAsync(", "CancelReissueRequestAsync(")],
         why="V1F R6: ยกเลิกคำขอใช้สิทธิ์เดียวกับการออกใบแทน"),
    dict(file="Helpers/SettlementPostingGuards.cs", method="Evaluate",
         must=["p.ReceiptEtaxSubmitted", "p.OutputVatDueAt", "p.DocumentPaidAmount - p.BatchPaidOnDocument"],
         why="ข้อ 17 (S3-7 ✅ c3116a4d) + ข้อ 11: ด่านยกเลิกการลงบัญชีดูเดือนภาษีของการรับชำระใบบริการ §78/1 และใบเสร็จที่ส่ง e-Tax แล้ว"),
]

# ── รอบ 200 ทีม SF (แก้ผลฝ่ายค้าน review200-P2/T/V2 · DECISIONS ข้อ 25–27): เทสต์ล็อกตัวตัดสิน pure — ที่นี่ล็อกว่าทุกทางเข้าเรียกจริงและใช้ผล ──
RULES += [
    # X-1/X-3 (ข้อ 26): โหมดภาษีค่าธรรมเนียม config ↔ ช่องทาง ต้องให้ผลเท่ากัน — ตรวจที่นำเข้าไฟล์ · บันทึกช่องทาง · บันทึกค่าตั้ง gateway · ลงบัญชี
    dict(file=SETTLE_POST, method="BuildGateAsync",
         must=["GatewayBatchIntentRules.ModeMismatch(", "GatewayBatchIntentRules.PostingIssue(", "SettlementSummarySupplement.SplitDuplicates(",
               "new SettlementOrphanCurrentBatch(", "d.WithholdingTaxAmount"],
         must_re=[r"PostingIssue\s*\([^;]*\)\s*is\s+SettlementPlanIssue\s+(\w+)\s*\)\s*gated\s*=\s*gated\s+with\s*\{\s*CanPost\s*=\s*false"],
         call_args=[("GatewayBatchIntentRules.ModeMismatch(", "vatRegistered"), ("SettlementContentOverlap.Annotate(", "contentHits")],
         before=[("SettlementSummarySupplement.SplitDuplicates(", "SettlementPostingGate.Evaluate(")],
         why="ข้อ 26 X-1: รอบโอนที่นำเข้าก่อนด่าน/ก่อนเปลี่ยนโหมดต้องถูกถามตอนลงบัญชี (ผลต้องบล็อก) · T-2: ใบเพิ่มเติมที่เนื้อหาซ้ำรอบที่ออกใบแรก = รายได้ซ้ำ · "
             "V2-P1: การรับรู้ของกำพร้าเทียบรอบที่กำลังลง · T-1: WHT ลูกค้าของใบถึงด่าน"),
    dict(file=SETTLE_IMPORT, method="ImportFileAsync",
         must=["GatewayBatchIntentRules.ModeMismatch(", "CompanyVatStatus.IsRegisteredAsync("],
         must_re=[r"GatewayBatchIntentRules\s*\.\s*ModeMismatch\s*\([^;]*\)\s*is\s+string\s+(\w+)\s*\)\s*throw\s+new\s+BusinessRuleException\s*\(\s*\1\b",
                  r"c\s*\.\s*CompanyId\s*==\s*companyId"],
         before=[("GatewayBatchIntentRules.ModeMismatch(", "PersistAsync(")],
         why="ข้อ 26 X-1: ไฟล์ของช่องทางที่ผูก config gateway ถูกแยก VAT ตามโหมดช่องทาง — โหมดขัด ⇒ ไม่รับไฟล์"),
    dict(file=SETTLE_CHANNEL, method="SaveAsync",
         must=["GatewayBatchIntentRules.ChannelModeTouched(", "CompanyVatStatus.IsRegisteredAsync("],
         call_args=[("GatewayBatchIntentRules.ModeMismatch(", "channelVatRegistered")],
         why="X-10: ตรวจโหมดเมื่อช่องทางใหม่/การผูกหรือโหมดเปลี่ยน (แก้ชื่อ/ปิดใช้งานช่องทางเดิมได้) · บริษัทไม่จด VAT ⇒ คู่ VAT ไทยให้ผลเท่ากัน"),
    dict(file=SETTLE_GATEWAY, method="ImportFromPaymentIntentsAsync",
         call_args=[("GatewayBatchIntentRules.ModeMismatch(", "vatRegistered")],
         why="ข้อ 26: ตัวตัดสินโหมดต้องรู้สถานะ VAT ของบริษัท (ไม่จด VAT ⇒ คู่ VAT ไทยเท่ากัน)"),
    dict(file="Controllers/PaymentSettingsController.cs", method="Save",
         must=["GatewayBatchIntentRules.ConfigChangeRefusal(", "c.PaymentProviderConfigId == cfg.Id"],
         must_re=[r"ConfigChangeRefusal\s*\([^;]*\)\s*is\s+string\s+(\w+)\s*\)\s*return\s+BadRequest\s*\([^;]*\1\b",
                  r"c\s*\.\s*CompanyId\s*==\s*companyId"],
         before=[("GatewayBatchIntentRules.ConfigChangeRefusal(", "SaveChangesAsync(")],
         why="ข้อ 26 X-1/B-3: เปลี่ยนโหมดภาษีค่าธรรมเนียมของ gateway ต้องยังตรงกับทุกช่องทางที่ผูก (ก่อนบันทึก)"),
    dict(file=SETTLE_IMPORT, method="PersistAsync",
         must_re=[r"input\s*\.\s*GatewayProviderCode\s*!=\s*null\s*&&\s*ThaiDate\s*\.\s*CalendarDateUtc\s*\(\s*payoutDateRaw\s*\)\s*!=\s*batch\s*\.\s*PayoutDate\s*\)\s*throw\b"],
         why="X-5: เติมรอบโอน gateway เดิมด้วยวันเงินเข้าอื่น = ยอดคืนตัดผิดวัน ⇒ ปฏิเสธ"),
    dict(file="Helpers/SettlementBatchMath.cs", method="BuildFeeLines", must=["SettlementFeeTax.WhtOnBase("],
         why="X-4: WHT ต่อบรรทัดใบคิดจากฐานรวมครั้งเดียว (บรรทัดบน 50 ทวิ) ไม่ใช่รวมยอดที่ปัดทีละรายการ"),
    dict(file="Helpers/SettlementFeeTax.cs", method="Compute", must=["WhtOnBase("], forbid=["(100m - rate)"],
         why="X-4: สูตร WHT (ออกภาษีแทน/ปกติ) ตัวเดียว — ห้ามสำเนาใน Compute"),
    # T-1 (ข้อ 27): รับชำระใบที่ลูกค้าหัก WHT ผ่านรอบโอน
    dict(file=SETTLE_POST, method="EnsureReceiptAsync",
         must=["SettlementReceiptWht.Decide(", "SettlementDocumentBuilder.ReceiptPayment("],
         call_args=[("SettlementDocumentBuilder.ReceiptPayment(", "wht")],
         must_re=[r"d\s*\.\s*CompanyId\s*==\s*companyId"],
         forbid=["WithholdingTaxAmount: 0m"],
         why="ข้อ 27: WHT ของใบตัดสินจากข้อเท็จจริงสดด้วยตัวตัดสินเดียวกับด่าน — ห้ามส่ง 0 เงียบ (ลูกหนี้ใน GL ค้าง · 11910 หาย)"),
    # T-3/T-4
    dict(file=SETTLE_POST, method="WalletContinuityAsync",
         must=["SettlementWalletContinuity.PickPrevious(", "b.PayoutDate == batch.PayoutDate"],
         why="T-3: รอบก่อนหน้าของวันเดียวกันตัดสินจากยอดต่อกัน ไม่ใช่ลำดับนำเข้า"),
    # T-7
    dict(file="Services/Implementations/JournalAnomalyService.cs", method="ScanAsync",
         must=["LegacyMoneyLegAudit.AdjustedBy(", "e.CompanyId == companyId", "JournalEntryStatus.Posted"],
         why="T-7: JE เก่าที่มี JE ปรับปรุงอ้างเลขในช่องอ้างอิงแล้ว ไม่ฟ้องซ้ำ (หลักฐาน ไม่ใช่ fuzzy)"),
    # V2
    dict(file=SETTLE_POST, method="OrphanArtifactsAsync",
         must=["parts.Contains(p.Notes.Substring("],
         call_args=[("SettlementOrphanTriage.Split(", "current")],
         why="V2-C4: กรองการรับชำระของรอบตายใน SQL · V2-P1: ตัวแยกรู้รอบที่กำลังลง/ตรวจเทียบ"),
    dict(file=SETTLE_POST, method="AcknowledgeOrphanCoreAsync",
         must=["_db.ChangeTracker.Clear()", "checkedBatchId = checkedBatch?.BatchId"],
         call_args=[("OrphanArtifactsAsync(", "checkedBatch")],
         before=[("_db.ChangeTracker.Clear()", "BeginTransactionAsync(")],
         why="V2-C3: ธุรกรรมของปุ่มรับรู้เริ่มด้วยตัวติดตามว่าง (แบบแผน C-20) · V2-P1: audit บันทึกรอบที่ตรวจเทียบ"),
    dict(file=SETTLE_POST, method="AcknowledgeOrphanAsync",
         must_re=[r"cb\s*\.\s*ChannelId\s*!=\s*owner\s*\.\s*ChannelId", r"b\s*\.\s*CompanyId\s*==\s*companyId"],
         why="V2-P1: รอบที่ตรวจเทียบต้องเป็นของบริษัทนี้และช่องทางเดียวกับของกำพร้า"),
    dict(file=SETTLE_POST, method="UnpostCoreAsync", must=["_db.ChangeTracker.Clear()"],
         before=[("_db.ChangeTracker.Clear()", "BeginTransactionAsync(")],
         why="V2-C3: ธุรกรรมท้ายของยกเลิกการลงบัญชีเริ่มด้วยตัวติดตามว่าง (แบบแผน C-20)"),
    dict(file="Controllers/SettlementController.cs", method="AcknowledgeOrphan", call_args=[("_posting.AcknowledgeOrphanAsync(", "request.BatchId")],
         why="V2-P1: ส่งรอบที่ผู้ใช้ตรวจเทียบถึง service"),
]

# ── รอบ 198 ข้อ 5 (คำตัดสินเจ้าของ): gate แพ็กเกจ/ระงับบริษัทบนหน้าเว็บ — รายงานก่อน แล้วค่อยเปิดบังคับ ──
# เทสต์ของ Helpers/SubscriptionGatePolicy + TenantCompanyId ล็อกแค่ตัวตัดสิน · ถอดการเรียกใน middleware แล้วเทสต์ยังเขียว ⇒ ล็อกจุดเรียก
SUB_MW = "Middleware/SubscriptionMiddleware.cs"
TENANT_MW = "Middleware/TenantAccessMiddleware.cs"
RULES += [
    dict(file=SUB_MW, method="InvokeAsync",
         must=["TenantCompanyId.FromHttp(context)", "shadowLog.ReadAdminSwitchAsync(", "SubscriptionEnforcementResolver.Resolve(",
               "SubscriptionGatePolicy.ActionFor(", "SubscriptionGatePolicy.WriteGateModeFor(",
               "SubscriptionGatePolicy.Decide(", "subscriptionService.GetGateStateAsync(", "SubscriptionGatePolicy.RequiredFeatureFor(",
               "SubscriptionGatePolicy.FeaturePlanFor(", "SubscriptionGatePolicy.WithPublicApiCompany(", "SubscriptionGatePolicy.IsPartnerCaller("],
         must_re=[r"if\s*\(\s*action\s*==\s*SubscriptionGateAction\s*\.\s*Shadow\s*\)\s*\{\s*await\s+RecordShadowAsync\s*\([^;]*enforced\s*:\s*false\s*,\s*partner\s*\)\s*;"
                  r"\s*await\s+_next\s*\(\s*context\s*\)\s*;\s*return\s*;\s*\}",
                  r"ActionFor\s*\(\s*target\s*,\s*enforcement\s*\.\s*EffectiveMode\s*\)",
                  # รอบ 200 S2 (S200-3): partner ที่ถูกบล็อกจริงก็นับ (แยกคอลัมน์) — เดิม `&& !target.HeaderCarried` ⇒ ไม่เคยถูกนับ
                  r"if\s*\(\s*verdict\s*\.\s*Blocks\s*\)\s*await\s+RecordShadowAsync\s*\([^;]*enforced\s*:\s*true\s*,\s*partner\s*\)",
                  # ข้อ 21/22: ตัดสินเส้นทางด้วย method (ข้อยกเว้นราย method) · ฟีเจอร์ที่ส่งเข้าตัวตัดสินมาจากแผน (คีย์ใหม่ = เงาสำหรับ partner)
                  r"RequiredFeatureFor\s*\(\s*path\s*,\s*context\s*\.\s*Request\s*\.\s*Method\s*\)",
                  r"FeaturePlanFor\s*\(\s*target\s*,\s*enforcement\s*\.\s*EffectiveMode\s*,\s*required\s*\)",
                  r"eff\s*\?\s*\.\s*IsActive\s*,\s*featurePlan\s*\.\s*Enforce\s*\)",
                  # คีย์ใหม่ของ partner ระหว่างยังไม่บังคับ: บันทึกเป็นเงา (enforced: false) ห้ามบล็อก
                  r"featurePlan\s*\.\s*ShadowOnly\s+is\s+FeatureFlags\s+\w+[^;]*RecordShadowAsync\s*\([^;]*enforced\s*:\s*false"],
         before=[("TenantCompanyId.FromHttp(context)", "SubscriptionGatePolicy.ActionFor("),
                 ("SubscriptionEnforcementResolver.Resolve(", "SubscriptionGatePolicy.ActionFor("),
                 ("SubscriptionGatePolicy.WithPublicApiCompany(", "SubscriptionGatePolicy.ActionFor("),
                 ("SubscriptionGatePolicy.FeaturePlanFor(", "SubscriptionGatePolicy.Decide("),
                 ("SubscriptionGatePolicy.Decide(", "RecordShadowAsync("),
                 ("RecordShadowAsync(", "Write403(")],
         forbid=["Headers.TryGetValue(", "RouteValues.TryGetValue(", "RouteFeatureMap", "NewlyGatedRouteKeys", "HasFlag(", "_config[",
                 "Subscription:Enforcement", "required?.Feature"],
         why="ข้อ 5+14+21–23: บริษัทมาจากตัวหาเดียวกับ TenantAccessMiddleware (route ชนะ header) + คีย์ API ของ /api/v1 · คำขอที่ส่ง header บังคับคีย์เดิมเสมอ · "
             "คีย์ใหม่รอบ 200 เป็นเงาสำหรับ partner จนกว่ากดบังคับ (FeaturePlanFor ตัวเดียว) · "
             "โหมดที่มีผลจริงมาจาก SubscriptionEnforcementResolver ตัวเดียว (ห้ามอ่าน config เอง = สองสวิตช์ขัดกัน) · ด่านเขียนจาก WriteGateModeFor · "
             "โหมดเงาบันทึกแล้วปล่อยผ่านทันที (ห้ามถึงเส้น 403) · ถูกบล็อกจริงถูกนับในรายงาน (partner แยกคอลัมน์) · ตัดสินด้วย SubscriptionGatePolicy ตัวเดียว"),
    dict(file=SUB_MW, method="RecordShadowAsync",
         must=["shadowLog.RecordAsync(", "SubscriptionGatePolicy.EndpointKey(", "RoutePattern.RawText", "partner"],
         must_re=[r"WouldBlock\s*:\s*true", r"if\s*\(\s*!\s*verdict\s*\.\s*Blocks\s*\)\s*return\s*;"],
         forbid=["Request.QueryString", "Request.Path.ToString()"],
         why="ข้อ 14: รายงานบอก บริษัท · endpoint (route template ไม่มี id/PII) · เหตุ · สถานะ subscription · แยกจะบล็อก/บล็อกจริง"),
    dict(file="Controllers/AdminSubscriptionEnforcementController.cs", method="EffectiveAsync",
         must=["SubscriptionEnforcementResolver.Resolve(", "_shadow.ReadAdminSwitchFreshAsync("],
         why="ข้อ 14: หน้าแอดมินแสดงโหมดที่มีผลจริงจากตัวตัดสินเดียวกับ middleware · อ่านสวิตช์สด (ไม่ผ่านแคช S200-8)"),
    dict(file="Controllers/AdminSubscriptionEnforcementController.cs", method="Get",
         must=["EffectiveAsync(", "_shadow.PruneAsync(", "SubscriptionGatePolicy.ShadowRetentionDays", "SubscriptionTrialReadiness.SummarizeTrialBlocks("],
         forbid=["_config["],
         why="ข้อ 14: โหมดที่มีผลจริง + ตัดแถวเก่า (ตารางไม่โตไม่จำกัด) + สรุปลูกค้าทดลองจากเซิร์ฟเวอร์"),
    dict(file="Controllers/AdminSubscriptionEnforcementController.cs", method="SetMode",
         must=["EffectiveAsync(", "AdminSwitchHasEffect", "_shadow.InvalidateAdminSwitchCache("],
         before=[("_db.SaveChangesAsync(", "_shadow.InvalidateAdminSwitchCache(")],
         why="ข้อ 14 ห้าม silent no-op: override ฉุกเฉินทับอยู่ ⇒ ตอบว่า \u0027บันทึกแล้วแต่ยังไม่มีผล\u0027"),
    dict(file="Controllers/AdminSubscriptionEnforcementController.cs", method="Precheck",
         must=["TrialReadinessAsync(", "SubscriptionWriteGateMode.Enforce"],
         forbid=["_config[", "HasFlag("],
         why="ข้อ 14: ตรวจล่วงหน้า = ผลของการกดบังคับ + ความพร้อมของแพ็กเกจทดลองตามข้อมูลแพ็กเกจ"),
    dict(file="Controllers/AdminSubscriptionEnforcementController.cs", method="TrialReadinessAsync",
         must=["SubscriptionTrialReadiness.CheckTemplate("],
         forbid=["HasFlag("],
         why="ข้อ 14: ความพร้อมแพ็กเกจทดลองใช้ตัวตัดสินเดียว (ห้ามเขียนเงื่อนไขฟีเจอร์ซ้ำ)"),
    dict(file="Services/Implementations/SubscriptionService.cs", method="ResolveGateOverlayAsync",
         must=["ResolvePlanFeaturesAsync("],
         before=[("ResolvePlanFeaturesAsync(", "OwnerDisabledFeatures")],
         forbid=["SubscriptionTrialReadiness.ResolveFeatures(", "SubscriptionTrialReadiness.IsTrialLike("],
         why="ข้อ 14 + S200-4: ลูกค้าทดลองที่สำเนาฟีเจอร์ว่าง ใช้ฟีเจอร์ตามข้อมูลแพ็กเกจ ก่อน mask ของเจ้าของบริษัท — ผ่านเมธอดเดียวกับ GetEffectivePlanAsync"),
    dict(file="Services/Implementations/SubscriptionService.cs", method="ResolvePlanFeaturesAsync",
         must=["SubscriptionTrialReadiness.MayFillFromTemplate(", "SubscriptionTrialReadiness.ResolveFeatures(", "_db.TrialConfigs"],
         before=[("SubscriptionTrialReadiness.MayFillFromTemplate(", "_db.PlanTemplates")],
         why="S200-4/S200-6: ตัวตัดสินฟีเจอร์ของบริษัทตัวเดียว — เฉพาะระหว่างทดลอง/ฟรีถาวร · เคารพ trial config รายบริษัท · query เฉพาะกรณีต้องเติม"),
    dict(file="Services/Implementations/SubscriptionService.cs", method="GetEffectivePlanAsync",
         must=["WithPlanFeaturesAsync(", "ResolvePlanFeaturesAsync("],
         forbid=["EnabledFeatures: acct.EnabledFeatures,"],
         why="S200-4: ด่านสร้างเอกสาร/EntitlementService (CheckFeatureAccessAsync) เห็นฟีเจอร์ชุดเดียวกับหน้าเว็บ/gate — เดิมหน้าเว็บบอกมี แต่สร้างเอกสารไม่ได้"),
    dict(file="Services/Implementations/SubscriptionService.cs", method="CheckFeatureAccessAsync",
         must=["GetEffectivePlanAsync("],
         why="S200-4: ด่านฟีเจอร์ฝั่ง service อ่านจากตัวตัดสินเดียว (EffectivePlan ที่ผ่าน ResolvePlanFeaturesAsync)"),
    dict(file="Services/Implementations/SubscriptionGateShadowLog.cs", method="ReadAdminSwitchAsync",
         must=["_cache.TryGet(", "_cache.Set(", "ReadAdminSwitchFreshAsync("],
         why="S200-8: middleware อ่านสวิตช์ผ่านแคชสั้นต่อเครื่อง (DB ยังเป็นความจริง · กัน query/log spam ทุกคำขอ)"),
    dict(file="Controllers/SubscriptionController.cs", method="GetSubscription",
         must=["SubscriptionEnforcementResolver.Resolve(", "_gateSwitch.ReadAdminSwitchAsync(", "FeatureGateMode"],
         forbid=["_config["],
         why="S200-2: เมนูล็อกตามโหมดที่มีผลจริงจากตัวตัดสินเดียวกับ middleware (หน้าเว็บห้าม hardcode)"),
    dict(file="Controllers/SettlementController.cs", method="PostingPreview",
         must=["SettlementPermissionScope.CandidatesHiddenReason(", "SettlementPermissionScope.HideReceivableDetails(",
               "SettlementPermissionScope.Import", "SettlementPermissionScope.Post"],
         why="S200-5: พรีวิวลงบัญชี (สิทธิ์ View) ไม่รั่วเลขที่/ยอดค้างของใบขาย — ตัวตัดสินสิทธิ์เดียวกับ D-P5"),
    dict(file="Controllers/SettlementController.cs", method="BatchDetailAsync",
         must=["SettlementPermissionScope.CandidatesHiddenReason(", "SettlementPermissionScope.HideCandidates(", "candidatesHiddenReason"],
         must_re=[r"CandidatesHiddenReason\s*\(\s*permissions\s*\.\s*Import\s*,\s*permissions\s*\.\s*Post\s*\)"],
         before=[("new SettlementActionPermissions(", "SettlementPermissionScope.CandidatesHiddenReason("),
                 ("SettlementPermissionScope.HideCandidates(", "return new")],
         why="D-P5 (ข้อ 14): ผู้มีแค่ Settlement.View ไม่เห็นผู้สมัครเอกสารขาย/ยอดค้าง — สิทธิ์ชุดเดียวกับปุ่ม · ซ่อนแล้วบอกเหตุผล"),
    dict(file=TENANT_MW, method="ExtractCompanyId",
         must=["TenantCompanyId.FromHttp(context)"],
         forbid=["Headers.TryGetValue(", "RouteValues.TryGetValue("],
         why="ข้อ 5: บริษัทที่ตรวจสมาชิกกับบริษัทที่ตัดสินแพ็กเกจต้องมาจากตัวหาเดียวกัน — สำเนาที่สอง = header ปลอมยืมแพ็กเกจได้"),
]

# ── รอบ 200 ทีม W (คำตัดสินข้อ 13): หัก ณ ที่จ่ายจ่ายต่างประเทศ (ม.70 ภ.ง.ด.54) ผ่านตัวตัดสินเดียว ForeignWhtRateResolver ──
#    เทสต์ (ForeignWhtRateResolverTests · SettlementForeignWhtTests) ล็อกตัวตัดสิน pure · ที่นี่ล็อกว่าทุกทางเข้า (แผนรอบโอน · ด่านผู้ลงบัญชี ·
#    คำเตือนตอนอนุมัติเอกสาร · ไฟล์ ภ.ง.ด.54) เรียกจริง และห้ามกลับไปบล็อกเหมา/เตือนเหมา/พิมพ์ ภ.ง.ด.53 ตายตัว
W_FEETAX = "Helpers/SettlementFeeTax.cs"
W_BATCH = "Helpers/SettlementBatchMath.cs"
W_POSTING = "Helpers/SettlementPosting.cs"
W_POSTSVC = "Services/Settlement/SettlementPostingService.cs"
W_EXPORT = "Services/Implementations/TaxFilingExportService.cs"
RULES += [
    dict(file=W_FEETAX, method="Compute",
         must=["SettlementForeignWht.IsForeignChannel(vatMode)", "SettlementForeignWht.Decide(whtIncomeCode"],
         why="ทีม W: อัตรา WHT ค่าธรรมเนียมผู้ให้บริการต่างประเทศมาจากตัวตัดสิน ม.70/อนุสัญญาตัวเดียว (ห้ามอัตราในประเทศ · R-A5)"),
    dict(file=W_BATCH, method="Plan",
         must=["SettlementForeignWht.PlanIssues(", "SettlementForeignWht.WhtForm(channel.FeeVatMode)",
               "SettlementForeignWht.IsForeignChannel(channel.FeeVatMode)"],
         forbid=["SettlementPlanIssueCode.ForeignWhtNotSupported"],
         why="ทีม W: บล็อกเฉพาะที่คิดให้ไม่ได้ (นอก ม.70 · ตัวแทนหักแทน) ผ่าน PlanIssues ตัวเดียว — ห้ามกลับไปบล็อกเหมาทุกช่องทางต่างประเทศ · "
             "ใบค่าธรรมเนียมพกแบบ ภ.ง.ด. · ขา WHT ต่างประเทศลง 21918"),
    dict(file=W_POSTING, method="WhtCertificate",
         must=["fee.WhtForm"], forbid=["TaxType.WithholdingTax53"],
         why="ทีม W: 50 ทวิ ของรอบโอนใช้แบบ ภ.ง.ด. ของแผน (ต่างประเทศ = ภ.ง.ด.54) — ห้ามพิมพ์ ภ.ง.ด.53 ตายตัว"),
    dict(file=W_POSTSVC, method="BuildGateAsync",
         must=["SettlementForeignWht.GateWhtForm(plan, domesticWhtForm)",
               "t.TaxType == whtForm", "f.TaxType == whtForm",
               "SettlementForeignWht.CounterpartyCountryIssue(", "counterparty?.CountryCode"],
         forbid=["t.TaxType == TaxType.WithholdingTax53", "f.TaxType == TaxType.WithholdingTax53",
                 "SettlementForeignWht.WhtForm(channel.FeeVatMode)"],
         why="ทีม W/WF (W-1): เดือนที่ยื่นแล้วของแบบ WHT จากตัวตั้งเดียว GateWhtForm (ขา ภ.ง.ด.54 ของแผนชนะ · ในประเทศ 3/53 ตามผู้รับ) — "
             "ห้ามตัดสินแบบซ้ำจากช่องทาง · ผู้ติดต่อต่างประเทศบนช่องทางไทยที่มีขา WHT = บล็อก (R-A5 อีกรูป)"),
    dict(file=W_POSTING, method="Evaluate",
         must=["SettlementForeignWht.GateWhtForm(plan, f.WhtFormType)"],
         forbid=["FormLabel(f.WhtFormType)"],
         why="ทีม WF (W-1): ข้อความด่านเดือนที่ยื่นแล้วพูดแบบเดียวกับที่ตัวโหลดเดือนใช้ — ผู้เรียกที่ลืมส่งแบบต้องไม่ได้ ภ.ง.ด.53 กับรอบโอนต่างประเทศ"),
    dict(file=DOCSVC, method="CollectApprovalWarningsAsync",
         must=["WhtPayeeKind.IsForeignPayee(doc.IsForeignService", "ForeignWhtRateResolver.ResolveForIncomeCode(",
               "ForeignWhtPayeeCheck.ScopeOf(", "ForeignWhtPayeeCheck.Warning(foreignScope",
               "l.WithholdingTaxRate > 0m || !string.IsNullOrWhiteSpace(l.IncomeTypeCode)"],
         forbid=["maxRate >= 15m", "ForeignWhtRateResolver.RateWarning("],
         why="ทีม W/WF (W-7): คำเตือนจ่ายต่างประเทศเทียบอัตรารายบรรทัดกับตัวตัดสิน ม.70 ผ่านขอบเขตผู้รับตัวเดียว (บุคคลธรรมดาเงียบ · มีเลขนิติบุคคลไทย/"
             "ไม่รู้ประเภท = เตือนว่าไม่รู้) · บรรทัดไม่หักเลยที่จำแนกแล้วต้องถูกตรวจ · ห้ามกลับไปเตือนเหมาทุกใบที่หัก 15%"),
    # ── ทีม WF: W-3 (ข้อ 40) ฐาน ภ.พ.36 รวมภาษีออกแทน · W-4/W-9 (ข้อ 41) ประเภทเงินได้ต่อช่องทาง · W-5 คำเตือนตอนออก 50 ทวิ · W-6 ทางไปต่อ ──
    # รอบ 200 ทีม SG (R2M-4): สูตร ภ.พ.36 ย้ายเข้า SettlementFeeTax.Pp36Legs ตัวเดียว (รายก้อน · รายบรรทัดใบ) — Compute ต้องเรียกหลังขั้น WHT
    dict(file=W_FEETAX, method="Compute",
         must=["Pp36Legs(treatment, preVat, borne)"],
         before=[("WhtOnBase(preVat, rate, whtMode)", "Pp36Legs(treatment, preVat, borne)")],
         forbid=["R(deducted * VatRate / 100m)"],
         why="ทีม WF (คำตัดสินข้อ 40): ฐาน ภ.พ.36 = มูลค่าบริการ + ภาษีที่ออกแทน — คิดหลังขั้น WHT ด้วยสูตรตัวเดียว (ห้ามกลับไปคิดบนยอดที่ถูกหัก)"),
    dict(file=W_FEETAX, method="Pp36Legs",
         must=["ForeignServiceVat.SelfAssessedVatOn(ForeignServiceVat.Pp36Base(serviceValue, payerBorneTax))"],
         why="ทีม WF/SG (ข้อ 40 · R2M-4): สูตร ภ.พ.36 ตัวเดียวของรายก้อนและรายบรรทัดใบ = ฐานรวมภาษีออกแทน"),
    dict(file=W_BATCH, method="ComputeTax",
         must=["SettlementWhtIncomeType.For(rule.Type, channel)"],
         forbid=["rule.WhtIncomeCode"],
         why="ทีม WF (คำตัดสินข้อ 41): รหัสประเภทเงินได้ของค่าธรรมเนียมผ่านตัวตัดสินเดียว (ค่าตั้งช่องทาง → ต่างประเทศ 40(2) → ตาราง)"),
    dict(file="Helpers/SettlementForeignWht.cs", method="PlanIssues",
         must=["SettlementWhtIncomeType.For(l.LineType, channel)", "SettlementWhtIncomeType.MapIssue(channel)", "GatewayConfigHint(channel)"],
         forbid=["x.Rule.WhtIncomeCode"],
         why="ทีม WF (ข้อ 41 · W-6): บล็อก/ทางไปต่อใช้รหัสเดียวกับผู้คิดภาษี · ค่าตั้งเสีย = บล็อก · ช่องทางผูก gateway ต้องบอกให้แก้ config ด้วย"),
    dict(file="Services/Implementations/TaxService.cs", method="GeneratePp36Report",
         must=["ForeignServiceVat.Pp36Base(", "WithholdingTaxCertType.PayAlways", "w.CompanyId == companyId"],
         why="ทีม WF (คำตัดสินข้อ 40): ฐานในรายงาน ภ.พ.36 รวมภาษีที่ออกแทนตาม 50 ทวิ แบบออกให้ตลอดไปของเอกสารนั้น"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="CreateAsync",
         must=["IssueWarningsAsync(companyId, cert, payee)"],
         why="ทีม WF (W-5): ใบ 50 ทวิ ที่ออกเองได้คำเตือนอัตรา ภ.ง.ด.54/ฐาน ภ.พ.36 ตอนออก ไม่ใช่แค่หมายเหตุท้ายไฟล์ส่งออก"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="UpdateAsync",
         must=["IssueWarningsAsync(companyId, cert, updPayee)"],
         why="ทีม WF (W-5): แก้ใบร่างก็ได้คำเตือนชุดเดียวกับตอนสร้าง"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="IssueWarningsAsync",
         must=["ForeignWhtPayeeCheck.CertificateWarnings(", "ForeignWhtPayeeCheck.ScopeOf(", "ForeignServiceVat.Pp36Shortfall(",
               "d.CompanyId == companyId"],
         why="ทีม WF (W-5 · ข้อ 40): ขอบเขตผู้รับตัวเดียวกับคำเตือนตอนอนุมัติ + สูตรฐาน ภ.พ.36 ตัวเดียวกับรอบโอน"),
    dict(file=SETTLE_CHANNEL, method="SaveAsync",
         must=["SettlementWhtIncomeType.ParseMap(r.WhtIncomeTypeMapJson)", "SettlementWhtIncomeType.Serialize(incomeMap)",
               "AddChainedAuditLog("],
         must_re=[r"if\s*\(\s*incomeRejected\s*\.\s*Count\s*>\s*0\s*\)\s*throw\b"],
         why="ทีม WF (ข้อ 41): ค่าตั้งประเภทเงินได้ของค่าธรรมเนียมอ่านด้วยตัวอ่านเดียวกับผู้คิดแผน · ค่าเสียล้มดัง · บันทึกมี audit (สิทธิ์ Settlement.Channels ที่ controller)"),
    dict(file=SETTLE_CHANNEL, method="ToViewAsync",
         must=["c.WhtIncomeTypeMapJson", "SettlementWhtIncomeType.For(type, c)", "SettlementWhtIncomeType.Describe("],
         why="ทีม WF (ข้อ 41): เก็บแล้วต้อง echo + แสดงค่าที่รอบโอนจะใช้จริงจากตัวตัดสินเดียว"),
    dict(file=W_EXPORT, method="ExportPnd54Async", must=["Pnd54RateNote(certs)"],
         why="ทีม W: ไฟล์ ภ.ง.ด.54 บอกบรรทัดที่อัตราไม่ตรงตัวตัดสิน ม.70/อนุสัญญา"),
    dict(file=W_EXPORT, method="Pnd54RateNote",
         must=["ForeignWhtRateResolver.ResolveForIncomeCode(", "ForeignWhtRateResolver.RateWarning("],
         forbid=["ThaiWhtRateTable"],
         why="ทีม W: ภ.ง.ด.54 ห้ามตัดสินด้วยตารางอัตราในประเทศ (ท.ป.4/2528) — ใช้ตัวตัดสิน ม.70 ตัวเดียว"),
]
# ── รอบ 200 ทีม SG (แก้ผลฝ่ายค้านรอบสอง review200-round2-money.md R2M-2..13 · DECISIONS ข้อ 26/27/40/41): เทสต์ SettlementReview200SgTests
#    ล็อกตัวตัดสิน pure — ที่นี่ล็อกว่าทุกทางเข้าเรียกจริง · ส่งข้อเท็จจริงที่ถูกตัว · ใช้ผล ──
RULES += [
    # R2M-2/R2M-5: ตัวตัดสินโหมดรู้ค่าตั้งประเภทเงินได้ของช่องทาง (ข้อ 41) ทุกทางเข้า
    dict(file=SETTLE_GATEWAY, method="ImportFromPaymentIntentsAsync",
         call_args=[("GatewayBatchIntentRules.ModeMismatch(", "channel.WhtIncomeTypeMapJson")],
         why="R2M-5: ประกอบรอบโอนจากรายการรับชำระ — ประเภทเงินได้ต่อช่องทางต้องให้อัตราเท่าเส้นเดิม"),
    dict(file=SETTLE_IMPORT, method="ImportFileAsync",
         call_args=[("GatewayBatchIntentRules.ModeMismatch(", "channel.WhtIncomeTypeMapJson")],
         why="R2M-5: นำเข้าไฟล์ของช่องทางที่ผูก config — ตัวตัดสินเดียวกัน"),
    dict(file=SETTLE_CHANNEL, method="SaveAsync",
         must=["prior?.WhtIncomeTypeMapJson"],
         call_args=[("GatewayBatchIntentRules.ModeMismatch(", "newIncomeMapJson")],
         why="R2M-5: บันทึกช่องทางที่เปลี่ยนค่าตั้งประเภทเงินได้ = แตะโหมด ⇒ ตรวจด้วยค่าตั้งใหม่ (ไม่ใช่ค่าเดิมในฐาน)"),
    dict(file="Controllers/PaymentSettingsController.cs", method="Save",
         must=["b.WhtIncomeTypeMapJson"],
         why="R2M-5: เปลี่ยนโหมดหักที่หน้า gateway ตรวจกับค่าตั้งประเภทเงินได้ของทุกช่องทางที่ผูก"),
    # R2M-2/5/7/13 + R2M-12 ที่ด่านผู้ลงบัญชี
    dict(file=SETTLE_POST, method="BuildGateAsync",
         must=["RemainingWhtAsync(", "remainingWht[d.Id]", "l.DistinctConfirmedAt != null"],
         call_args=[("GatewayBatchIntentRules.ModeMismatch(", "channel.WhtIncomeTypeMapJson"),
                    ("GatewayBatchIntentRules.PostingIssue(", "channel.FeeVatMode"),
                    ("SettlementSummarySupplement.SplitDuplicates(", "confirmedDistinct")],
         forbid=["pend?.PayoutRefs, d.WithholdingTaxAmount)"],
         why="R2M-5 ค่าตั้งประเภทเงินได้ · R2M-7 ทางไปต่อของช่องทาง ภ.พ.36 · R2M-12 บรรทัดที่ยืนยันแล้วไม่นับเป็นหลักฐานซ้ำ · "
             "R2M-13 ด่านเห็น WHT ที่ยังไม่ถูกบันทึก (ไม่ใช่ WHT ทั้งใบ)"),
    dict(file=SETTLE_POST, method="EnsureReceiptAsync",
         must=["RemainingWhtAsync(", "SettlementReceiptWht.Decide(remainingWht"],
         forbid=["Decide(target.WithholdingTaxAmount"],
         why="R2M-13: เส้นรับชำระตัดสินจาก WHT ที่ยังไม่ถูกบันทึก ตัวเดียวกับด่าน"),
    dict(file=SETTLE_POST, method="RemainingWhtAsync",
         must=["SettlementReceiptWht.Remaining(", "p.CompanyId == companyId", "d.CompanyId == companyId", "DocumentStatus.Draft"],
         why="R2M-13: ยอดที่บันทึกแล้ว = ชุดเดียวกับเพดานของ CreatePaymentAsync (tenant ทุก query)"),
    dict(file=SETTLE_POST, method="ConfirmDistinctLinesAsync",
         must=["_perms.HasPermissionAsync(", "JobLock.RunExclusiveAsync(", "BuildGateAsync(", "SettlementSummarySupplement.ConfirmRefusal(",
               "AddChainedAuditLog(", "l.CompanyId == companyId"],
         must_re=[r"ConfirmRefusal\s*\([^;]*gate\s*\.\s*Plan\s*\.\s*Issues\s*\)\s*is\s+string\s+(\w+)\s*\)\s*\{\s*result\s*=\s*Fail\s*\(\s*\1"],
         before=[("SettlementSummarySupplement.ConfirmRefusal(requested", "SaveChangesAsync(")],
         why="R2M-12: ยืนยันรายบรรทัดต้องผ่านสิทธิ์ใน service + ด่านสดใต้ล็อก (บรรทัดต้องอยู่ในปัญหา SummarySupplementDuplicate) + audit chain รายบรรทัด"),
    dict(file="Controllers/SettlementController.cs", method="ConfirmDistinctLines",
         must=["_posting.ConfirmDistinctLinesAsync("],
         why="R2M-12: endpoint ยืนยันรายบรรทัดเรียก service ตัวเดียว (สิทธิ์/ด่านอยู่ใน service)"),
    # R2M-3/R2M-10: ฐาน ภ.พ.36 ห้ามบวกภาษีออกแทนซ้ำ · นับเฉพาะ 50 ทวิ ที่ออกแล้ว
    dict(file="Services/Implementations/TaxService.cs", method="GeneratePp36Report",
         must=["ForeignServiceVat.BorneTaxOutsideLines(", "WhtCertFilingScope.Filed", "w.TotalIncomeAmount"],
         forbid=["w.Status != WithholdingTaxCertStatus.Voided"],
         why="R2M-3: ใบที่คีย์ gross-up แล้วห้ามบวกภาษีออกแทนซ้ำ (ตัดสินจากเงินได้บน 50 ทวิ vs ยอดบรรทัด) · R2M-10: ร่างไม่นับ"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="IssueWarningsAsync",
         must=["ForeignServiceVat.BorneTaxOutsideLines(", "cert.TotalIncomeAmount"],
         forbid=["Pp36Shortfall(serviceValue, cert.TotalTaxAmount"],
         why="R2M-3: คำเตือน 'VAT ขาด' ใช้ตัวตัดสินเดียวกับรายงาน — ห้ามพาผู้ใช้ยื่น ภ.พ.36 เกิน"),
    # R2M-4: ภ.พ.36 ของบรรทัดใบคิดจากฐานรวม + ภาษีออกแทนตัวที่ลง 50 ทวิ
    dict(file=W_BATCH, method="BuildFeeLines",
         must=["SettlementFeeTax.Pp36Legs(treatment, lineDeducted, whtBorne)"],
         before=[("SettlementFeeTax.WhtOnBase(", "SettlementFeeTax.Pp36Legs(")],
         why="R2M-4: ลำดับข้อ 40 (เงินได้รวม → WHT → ภ.พ.36) ใช้ WHT ตัวที่ลง 50 ทวิ ไม่ใช่รวม ภ.พ.36 ที่ปัดรายส่วน"),
    # R2M-6: ไม่กรอกปลายช่วง ⇒ ขอบบน = เที่ยงคืนต้นวันเงินเข้า
    dict(file=SETTLE_GATEWAY, method="LoadIntentRowsAsync",
         must=["GatewaySettlementMath.ConfirmedToExclusiveUtc(periodTo ?? payoutDate.AddDays(-1))"],
         forbid=["periodTo is DateTime pt ?"],
         why="R2M-6 (X-8): ห้ามไม่มีขอบบน — รายการที่รับเงินหลังวันเงินเข้าไม่ใช่ของรอบนี้"),
    # R2M-7/R2M-8: ทางไปต่อตัวเดียวของช่องทางต่างประเทศที่ผูก gateway + เตือนบนหน้ารอบโอนเส้นเดิม
    dict(file="Helpers/SettlementForeignWht.cs", method="GatewayConfigHint",
         must=["GatewayBatchIntentRules.ForeignPp36BoundNextStep"],
         forbid=["ระบบตรวจว่าสองที่ตอบตรงกัน"],
         why="R2M-7: ทางไปต่อไม่ขัดกับด่านโหมด (ช่องทาง ภ.พ.36 ที่ผูก config ไม่มีโหมดที่ตรงได้)"),
    dict(file="Services/Payments/GatewaySettlementService.cs", method="ListPendingAsync",
         must=["GatewayBatchIntentRules.LegacyForeignChannelWarning(", "ForeignBoundChannelNamesAsync("],
         why="R2M-8: หน้ารอบโอนเส้นเดิมเตือนว่าไม่ตั้ง ภ.พ.36 เมื่อมีช่องทางต่างประเทศผูก config นี้"),
    dict(file="Services/Payments/GatewaySettlementService.cs", method="ForeignBoundChannelNamesAsync",
         must=["s.CompanyId == companyId", "SettlementFeeVatMode.ForeignPp36"],
         why="R2M-8/SG-1: ชุดช่องทาง ภ.พ.36 ที่ผูก config — tenant + โหมดต่างประเทศ (ตัวเดียวของคำเตือนและด่าน)"),
    dict(file="Services/Payments/GatewaySettlementService.cs", method="BuildPlanAsync",
         must=["ForeignBoundChannelNamesAsync(", "GatewayBatchIntentRules.LegacyForeignChannelWarning(", "SettlementBlockReason.ForeignPp36Bound"],
         before=[("SettlementBlockReason.ForeignPp36Bound", "JournalEntryBuilder.ClosedPeriodReasonAsync(")],
         why="ฝ่ายค้านรอบสาม SG-1: พรีวิว/บันทึกรอบโอนเส้นเดิมบล็อกเมื่อมีช่องทาง ภ.พ.36 ผูก config (เดิมเตือนแค่หน้ารายการค้างโอน — บันทึกผ่านเงียบ §83/6 ขาด)"),
    # R2M-11: POS คืนเงินบัตร/e-Wallet/เช็ค ลงผังเดียวกับขาขายเดิม
    dict(file="Services/Implementations/PosService.Orders.cs", method="CreateRefundJournalEntryAsync",
         must=["MoneyAccountFallback.RefundAccountFromSale(", "SaleMoneyLegDescription(refundMethod, order.OrderNumber)",
               "l.JournalEntry.CompanyId == companyId"],
         before=[("MoneyAccountFallback.RefundAccountFromSale(", "ResolvePaymentAccountAsync(")],
         why="R2M-11: บิลที่ปิดก่อน R200G-2 คืนเงินหลัง deploy ต้องกลับขาที่ลงไว้จริง (ไม่ใช่กติกาวันนี้)"),
    dict(file="Services/Implementations/PosService.Orders.cs", method="CreateSalesJournalEntryAsync",
         must=["SaleMoneyLegDescription(pay.PaymentMethod, order.OrderNumber)"],
         forbid_lit=["รับเงิน {methodLabel} POS #"],
         why="R2M-11: คำอธิบายขาเงินผ่านตัวสร้างเดียว — เส้นคืนเงินอ่านขานี้กลับ (ข้อความสองที่ต้องไม่ drift)"),
]

# ── รอบ 200 ทีม K (OCR ผู้ติดต่อสาขา/ใบ Makro · คำตัดสินเจ้าของข้อ 19): เทสต์ล็อกแค่ helper pure ⇒ ล็อกจุดเรียกใน service ──
PRODUCT_MATCHER = "Services/Implementations/Ocr/ProductMatcher.cs"
_K5_WHY = ("รอบ 200 K-5 (คำตัดสินข้อ 19): สร้างผู้ติดต่อจากสแกนต้องอยู่ใต้ advisory lock ต่อ (CompanyId, เลขผู้เสียภาษี) ในธุรกรรม แล้วถาม"
           "คีย์กลางซ้ำ — อัปโหลดพร้อมกันไม่งั้นได้แถวสาขาซ้ำ (ไม่มี unique index โดยตั้งใจ)")
RULES += [
    dict(file=OCR, method="SubmitCorrectionAsync",
         must=["OcrVendorBranchContact.ShouldRedecideOnCorrection(", "DecideScanVendorBranchContactAsync(",
               "LockAndFindConcurrentOcrContactAsync("],
         call_args=[("OcrVendorBranchContact.ShouldRedecideOnCorrection(", "contactPickedByUser")],
         before=[("OcrCorrectedFieldList.From(", "DecideScanVendorBranchContactAsync("),
                 ("LockAndFindConcurrentOcrContactAsync(", "redecideTx.CommitAsync(")],
         why="รอบ 200 K-4: ผู้ใช้เปลี่ยนรหัสสาขา/เลขผู้ขายในหน้ารีวิว ⇒ \"แก้ในฟอร์มก่อน\" ต้องได้ผู้ติดต่อของสาขาใหม่ (ตัวตัดสินเดียวกับเส้นสแกน) · "
             "ตัดสินหลังนับช่องที่ผู้ใช้แก้ (ให้ userCorrected ของสาขาถูกต้อง) · แถวใหม่เกิดใต้ล็อก K-5"),
    dict(file=OCR, method="LockAndFindConcurrentOcrContactAsync",
         must=["OcrContactCreateLock.LockPart(", "AdvisoryLockKey.For(", "AdvisoryLockKey.OcrContactCreate",
               "ContactTaxBranchKey.FindAsync(", "OcrContactCreateLock.ReuseAfterLock("],
         must_lit=["pg_advisory_xact_lock"],
         must_re=[r"if\s*\(\s*_db\s*\.\s*Database\s*\.\s*CurrentTransaction\s*==\s*null\s*\)\s*throw\b"],
         before=[("ExecuteSqlRawAsync(", "ContactTaxBranchKey.FindAsync(")],
         forbid=["HashCode.Combine(", "GetHashCode("],
         why=_K5_WHY + " · ถามคีย์ซ้ำหลังได้ล็อกเท่านั้น · นอกธุรกรรม = ล็อกหลุดทันที ⇒ throw"),
    dict(file=OCR, method="ScanAsync",
         must=["LockAndFindConcurrentOcrContactAsync(", "contactCreateTx.CommitAsync(", "OcrOpenPurchaseOrders.Plan("],
         before=[("LockAndFindConcurrentOcrContactAsync(", "NewVendorBranchContactAsync("),
                 ("LockAndFindConcurrentOcrContactAsync(", "_db.Contacts.Add(newContact)"),
                 ("_db.Contacts.Add(newContact)", "contactCreateTx.CommitAsync(")],
         why=_K5_WHY + " · r199 A-5: ผูก PO อัตโนมัติจากเลขบนกระดาษเทียบ PO ค้างทั้งหมด (ตัดเพดานเฉพาะรายการที่แสดง)"),
    dict(file=OCR, method="CreateDocumentFromScanCoreAsync",
         must=["LockAndFindConcurrentOcrContactAsync("],
         before=[("BeginTransactionAsync(", "LockAndFindConcurrentOcrContactAsync(")],
         why=_K5_WHY + " · ผู้ติดต่อที่เส้นสร้างเอกสารเพิ่ง Add ต้องถูกถามซ้ำใต้ล็อกในธุรกรรมเดียวกับเอกสาร"),
    dict(file=PRODUCT_MATCHER, method="MatchAsync",
         must=["VendorEntityIdsAsync(", "OcrVendorAliasScope.IsVendorAlias("],
         forbid=["a.ContactId == vendorContactId", "n.ContactId == vendorContactId"],
         why="รอบ 200 K-3b: alias/คำปฏิเสธ/ประวัติซื้อที่เรียนบนแถว สนญ. ใช้กับแถวสาขาของนิติบุคคลเดียวกัน (ขอบเขตกลาง SameEntityIdsAsync)"),
    dict(file=PRODUCT_MATCHER, method="VendorEntityIdsAsync",
         must=["ContactTaxBranchKey.SameEntityIdsAsync(", "OcrVendorAliasScope.VendorIds("],
         why="รอบ 200 K-3b: ขอบเขต \"ผู้ขายรายนี้\" ตัวเดียว (ห้ามเขียน c.TaxId == x เอง)"),
    dict(file=PRODUCT_MATCHER, method="GetVendorAdaptiveThresholdAsync",
         must=["VendorEntityIdsAsync("], forbid=["a.ContactId == vendorContactId"],
         why="รอบ 200 K-3b: เกณฑ์ยอมรับอัตโนมัติของผู้ขายนับ alias ทุกแถวของนิติบุคคลเดียวกัน"),
    dict(file="Services/BranchCodeExtractor.cs", method="Extract",
         must=["OcrIssuerBranch.MaskStatements(", "OcrPartyLabels.MaskCopyNoise("],
         why="รอบ 200 K-11: สาขาผู้ซื้ออ่านบนข้อความที่กลบป้ายฉบับ (ต้นฉบับลูกค้า) + ประโยคประกาศสาขาผู้ออกใบ (ของผู้ขายเสมอ) — ใบ Makro ได้ 00005 แทน 00000"),
    dict(file="Helpers/OcrPartyLabels.cs", method="FindRecipientAll",
         must=["OcrSignatureSlot.IsSignatureSlot("],
         why="รอบ 200 K-8: ช่องลายเซ็นท้ายบิล (\"ลงชื่อ....ผู้รับสินค้า\") ไม่ใช่บล็อกผู้รับ — อีเมล/เบอร์ผู้ขายใต้ช่องลายเซ็นต้องไม่หายเป็นของผู้ซื้อ"),
    dict(file="Controllers/ContactHygieneController.cs", method="Get",
         must=["ContactDataHygiene.DuplicateKeyGroups("],
         forbid=["MergeContactsAsync("],
         why="รอบ 200 K-5 (คำตัดสินข้อ 19): แถวผู้ติดต่อซ้ำที่มีอยู่แล้ว <รายงาน> ไม่รวมอัตโนมัติ (การรวมย้อนไม่ได้)"),
    dict(file=DOCSVC, method="GetDuplicateContactGroupsAsync",
         must=["ContactDataHygiene.DuplicateKey("],
         why="รอบ 200 K-5: คีย์ผู้ติดต่อซ้ำตัวเดียวกับรายงาน contact-hygiene (สองหน้านับกลุ่มตรงกัน)"),
]
# ── รอบ 200 ทีม K2 (ฝ่ายค้าน K R1–R8 · คำตัดสินข้อ 28/29 · ผลตรวจรอบ 189 C-01..C-10) — เทสต์ล็อก helper pure ⇒ ล็อกจุดเรียกใน service ──
RULES += [
    dict(file=OCR, method="SubmitCorrectionAsync",
         must=["OcrVendorBranchContact.VendorKeyTouched(", "OcrVendorBranchContact.ScanAlreadyPosted(",
               "OcrCorrectedFieldList.VendorAddressTyped("],
         call_args=[("OcrVendorBranchContact.VendorKeyTouched(", "VendorBranchConfirmed"),
                    ("OcrVendorBranchContact.ScanAlreadyPosted(", "CreatedJournalEntryId")],
         before=[("OcrCorrectedFieldList.VendorAddressTyped(", "DecideScanVendorBranchContactAsync(")],
         why="K2 R1: พิมพ์ยืนยันรหัสสาขาเดิม = ตัดสินผู้ติดต่อใหม่ (สองเส้นต้องได้แถวเดียวกัน) · R5: สแกนที่ลง JE อย่างเดียวห้ามเปลี่ยนผู้ติดต่อ · "
             "ข้อ 29: ธง \"ผู้ใช้พิมพ์ที่อยู่\" เขียนจากกติกา baseline ก่อนตัดสินแถวสาขา"),
    dict(file=OCR, method="DecideScanVendorBranchContactAsync",
         must=["result.VendorAddressUserTyped"],
         why="คำตัดสินข้อ 29: ที่อยู่ของแถวสาขาใหม่นับ \"ผู้ใช้พิมพ์\" จากธงกติกาใหม่เท่านั้น (\"VendorAddress\" ในรายการช่องที่แก้ของแถวก่อนรอบ 200 แยกไม่ได้)"),
    dict(file=DOC, method="SyncScanToPostedDocumentAsync",
         must=["OcrPostedTruth.WhtTouched(", "touched.AddRange(whtTouched)"],
         why="คำตัดสินข้อ 28: WHT ที่คนแก้ในฟอร์มเอกสาร (ต่างจาก baseline ของสแกน) ต้องถูกนับเป็นคำแก้ก่อนเรียนประวัติผู้ขาย"),
    dict(file=DOC, method="ApproveDocumentAsync#2",
         before=[("SyncScanToPostedDocumentAsync(", "_vendorIntel.TryTrainAsync(")],
         why="คำตัดสินข้อ 28: sync สแกน (merge ช่อง WHT ที่คนแก้) ต้องมาก่อนการเรียนประวัติผู้ขาย — train ครั้งเดียวต่อเอกสาร"),
    dict(file=OCR, method="ScanAsync",
         must=["UndoOcrContactCreateAfterRollback(", "AdoptTaxIdUnderOcrContactLockAsync(", "OcrCurrencyEvidence.Read("],
         forbid=["existing.TaxId = extractedData.VendorTaxId", "InferCurrency("],
         why="K2 R8: ธุรกรรมสร้างผู้ติดต่อ rollback ⇒ สแกนห้ามชี้ผู้ติดต่อที่ไม่มีจริง · R6: เติมเลขภาษีเข้าแถวเดิมใต้ล็อก K-5 · "
             "C-09: สกุลเงินจากตัวอ่านเดียว (ดูบริเวณยอดรวม)"),
    dict(file=OCR, method="AdoptTaxIdUnderOcrContactLockAsync",
         must=["LockAndFindConcurrentOcrContactAsync(", "OcrContactCreateLock.MayAdoptAfterLock(", "adoptTx.CommitAsync("],
         why="K2 R6: ถามคีย์ซ้ำหลังได้ล็อกก่อนเติมเลขเข้าแถวเดิม"),
    dict(file=OCR, method="CreateDocumentFromScanCoreAsync",
         must=["ResolveSalesCounterpartyAsync(", "OcrContactCreateLock.MayAdoptAfterLock(", "OcrCounterpartyMatch.NoCounterpartyMessage(",
               "OcrAiLabelScope.ImplicitMayRecord(", "result.OurRoleAiFeedbackId"],
         before=[("BeginTransactionAsync(", "OcrContactCreateLock.MayAdoptAfterLock(")],
         forbid=["c.Name.Contains(buyerNm)", "contactId ??= result.MatchedContactId",
                 "Cannot create document: no contact"],
         why="C-01: ลูกค้าจากชื่อผ่านตัวจับคู่เดียว (ห้าม substring ดิบ · ห้ามถอยไป MatchedContactId เงียบ) · C-03: ไม่รู้คู่ค้า = ข้อความไทยบอกทางไปต่อ · "
             "C-06: ปิดลูปบทบาทเราบนเส้น 1-click · R6: เติมเลขเข้าแถวเดิมถูกถามซ้ำใต้ล็อก"),
    dict(file=OCR, method="ResolveSalesCounterpartyAsync",
         must=["OcrCounterpartyMatch.PickBuyerByName(", "ContactTaxBranchKey.SoftScope(", "ContactTaxBranchKey.AdoptTaxId(",
               "ContactTaxBranchKey.FindAsync("],
         why="C-01: ตัวหาลูกค้าตัวเดียวของเส้นสร้างเอกสาร + พรีวิว (คีย์ → ชื่อในชุดที่อนุญาต → ตัวจับคู่ชื่อ)"),
    dict(file=OCR, method="PreviewDocumentLinesAsync",
         must=["ResolveSalesCounterpartyAsync(", "Counterparty: counterparty"],
         why="C-02: \"แก้ในฟอร์มก่อน\" ได้คู่ค้าตามฝั่งเอกสารจากเซิร์ฟเวอร์ (ฝั่งขาย = ผู้ซื้อ ไม่ใช่ผู้ขาย = เรา)"),
]

# ── รอบ 200 ทีม V1G (ฝ่ายค้านรอบสอง review200-round2-V1F · DECISIONS ข้อ 43–49): เทสต์ล็อกตัวตัดสิน pure ⇒ ล็อกจุดเรียก/ลำดับ/การใช้ผลที่นี่ ──
RULES += [
    dict(file=DOCSVC, method="UndoUndueOutputVatReclassAsync",
         must=["OutputVatReclassJournalsAsync(", "je.EntryDate.Date", "inv.OutputVatDueAt = null"],
         forbid=["catch", "inv.DocumentDate", "LogError("],
         why="ข้อ 48 (RV1F-2): ตัวกลับภาษีขายถึงกำหนดลงวันที่ของ JE ย้ายภาษีเอง (เดือนรับเงิน) — ห้ามกลับไปลงวันที่ใบแจ้งหนี้ · ล้ม = throw (ห้ามกลืนในเส้นภาษี)"),
    dict(file=DOCSVC, method="OutputVatReclassJournalsAsync",
         must_re=[r"j\s*\.\s*CompanyId\s*==\s*companyId", r"a\s*\.\s*CompanyId\s*==\s*companyId", r"l\s*\.\s*DebitAmount\s*>\s*0"],
         must_lit=['"21913"'],
         why="ข้อ 48: JE ย้ายภาษีเลือกจาก GL จริง (ขา Dr 21913 · ยังไม่ถูกกลับ) · tenant"),
    dict(file=DOCSVC, method="OutputVatPeriodLockReasonAsync",
         must=["ResolveFiscalPeriodAsync(", "FiscalPeriodStatus.Open", "VatPeriodDeclaredOrFiledAsync("],
         why="ข้อ 48: ด่านงวดของวันที่รายการภาษีขายที่จะเขียนจริง — งวดบัญชีปิด หรือ ภ.พ.30 ยื่น/ประกาศว่ายื่น/ล็อก (ตัวเดียวของถอย/ย้ายภาษี)"),
    dict(file=DOCSVC, method="ReclassLockReasonAsync",
         must=["OutputVatReclassJournalsAsync(", "OutputVatPeriodLockReasonAsync(companyId, je.EntryDate)"],
         why="ข้อ 48: ด่านถอยภาษีดูวันที่ของตัวกลับจริง (วันที่ JE ย้ายภาษี) — ไม่ใช่วันที่ใบเสร็จ/ใบแจ้งหนี้"),
    dict(file=DOCSVC, method="TryReclassifyUndueOutputVatAsync",
         must=["throwOnFailure"],
         must_re=[r"catch\s*\(\s*Exception\s+ex\s*\)\s*when\s*\(\s*!\s*throwOnFailure\s*\)",
                  r"if\s*\(\s*throwOnFailure\s*\)\s*throw\b"],
         why="ข้อ 48: เส้นปิดธงต้อง 'ล้มดัง' (งวดปิด = throw ให้ธุรกรรม rollback) · เส้นรับชำระคงพฤติกรรมเดิม (ห้ามล้มการรับเงินจริง)"),
    dict(file=DOCSVC, method="VoidPaymentAsync",
         must=["outputVatNotice", "new PaymentVoidResult(etaxCancellationFlag, outputVatNotice)",
               "(etaxCancellationFlag, outputVatNotice) = await ReverseMultiDocPaymentInternalAsync("],
         why="ข้อ 48: ข้อความ 'ถอยภาษีขายไม่ได้' ของเช็คเด้ง/ยกเลิกการลงบัญชีต้องถึงผู้เรียก (ห้ามทิ้งผล)"),
    dict(file=DOCSVC, method="VoidDocumentAsync",
         must=["DocumentVoidPreconditions.EffectiveEtaxAsync(", "DocumentVoidPreconditions.DocumentVoidEtaxBlock(",
               "DocumentVoidPreconditions.EtaxReachedRdStatuses.Contains(e.Status)"],
         call_args=[("DocumentVoidPreconditions.DocumentVoidEtaxBlock(", "restore: false")],
         before=[("DocumentVoidPreconditions.DocumentVoidEtaxBlock(", "BeginTransactionAsync(")],
         forbid=["e.Status != EtaxStatus.Accepted", "e.Status == EtaxStatus.Accepted"],
         why="ข้อ 43 (RV1F-6): ยกเลิกเอกสารที่ e-Tax ส่งแล้ว/ตอบรับ/อีเมลประทับเวลา = บล็อกพร้อมทางไปต่อ · ห้ามพลิก Submitted เป็น Voided · ตัวโหลด/ชุดสถานะเดียว"),
    dict(file=DOCSVC, method="RestoreVoidedDocumentAsync",
         must=["DocumentVoidPreconditions.EffectiveEtaxAsync(", "DocumentVoidPreconditions.DocumentVoidEtaxBlock("],
         call_args=[("DocumentVoidPreconditions.DocumentVoidEtaxBlock(", "restore: true")],
         forbid=["e.Status == EtaxStatus.Accepted"],
         why="ข้อ 43: กู้คืนดูชุดสถานะ 'ถึงกรมสรรพากร' เดียวกับยกเลิก"),
    dict(file=SETTLE_POST, method="LoadUnpostFactsAsync",
         must=["DocumentVoidPreconditions.EffectiveEtaxAsync(_db, companyId, docIds", "EtaxSubmitted: submittedDocs.Contains(d.Id)"],
         forbid=["docIds.Contains(e.DocumentId) && e.Status == EtaxStatus.Accepted"],
         why="ข้อ 43: ด่านยกเลิกการลงบัญชีเห็นเอกสารที่ VoidDocumentAsync จะปฏิเสธ (ส่งแล้ว/ตอบรับ/อีเมล) ก่อนแตะชิ้นแรก"),
    dict(file=DOC_REISSUE, method="ReissueSettlementPaidDocumentAsync",
         must=["BuildReissueRequestViewAsync(", "SettlementPaidReissueRequestView.ConfirmMismatch(", "ConfirmRequestHash = null"],
         call_args=[("SettlementPaidReissueRequestView.ConfirmMismatch(", "request.ConfirmRequestHash")],
         before=[("SettlementPaidReissueRequestView.ConfirmMismatch(", "EvaluateSettlementPaidReissueAsync("),
                 ("SettlementPaidReissueRequestView.ConfirmMismatch(", "_db.Documents.Add(")],
         must_re=[r"ConfirmMismatch\s*\([^;]*\)\s*is\s+string\s+\w+\s*\)\s*throw\b"],
         why="ข้อ 49 (RV1F-5): การยืนยันผูกกับคำขอฉบับที่ผู้ยืนยันเห็น (ประกอบใหม่ใต้ล็อกด้วยตัวเดียวกับหน้าจอ) — คำขอ/ผู้ซื้อเปลี่ยน ⇒ 409"),
    dict(file=DOC_REISSUE, method="BuildReissueRequestViewAsync",
         must=["SettlementPaidReissueRequestView.Build(", "SettlementPaidReissueRequestCodec.Parse(", "SettlementPaidReissue.StripReplacementNote("],
         must_re=[r"c\s*\.\s*CompanyId\s*==\s*companyId"],
         why="ข้อ 49: ตัวประกอบภาพคำขอตัวเดียวของหน้าจอและการยืนยัน · ผู้ซื้อของบริษัทนี้เท่านั้น"),
    dict(file=DOCSVC, method="GetDocumentAsync", must=["BuildReissueRequestViewAsync("],
         why="ข้อ 49: หน้าเอกสารแสดงคำขอเต็มจากตัวประกอบเดียวกับการยืนยัน"),
    dict(file=DOC_REISSUE, method="ListEtaxCancellationCreditNotesAsync",
         must=["DocumentStatusRules.NotIssued", "EtaxCancelledByCreditNoteId"],
         must_re=[r"d\s*\.\s*CompanyId\s*==\s*companyId"],
         why="ข้อ 47: ตัวเลือกใบลดหนี้ = ออกแล้ว · ยังไม่เคยใช้ · tenant (ตัวตัดสินจริงอยู่ที่ endpoint ปิดธง)"),
    dict(file=DOC_REISSUE, method="GetEtaxReissueReviewAsync",
         must=["EtaxReissueReview.FlaggedReceiptVatUndone(", "EtaxReissueReview.CarriedExcess(", "EtaxReissueReview.ResolvedByV1FMarker"],
         must_re=[r"r\s*\.\s*CompanyId\s*==\s*companyId", r"d\s*\.\s*CompanyId\s*==\s*companyId"],
         forbid=["SaveChangesAsync(", "AddChainedAuditLog(", "ExecuteUpdateAsync(", "ExecuteDeleteAsync("],
         why="ข้อ 44: รายงานอ่านอย่างเดียวให้นักบัญชี — ห้ามแก้อะไรอัตโนมัติ (งวดที่อาจยื่นแล้วห้ามแก้เงียบ) · tenant"),
    dict(file="Controllers/DocumentController.cs", method="EtaxCancellationCreditNotes",
         must=["ListEtaxCancellationCreditNotesAsync("],
         why="ข้อ 47: หน้าจอเลือกใบลดหนี้จากเซิร์ฟเวอร์ (ไม่รับเลขที่ข้อความ)"),
]

# ── รอบ 200 ทีม Z: แก้ผลฝ่ายค้านรอบสอง ด้านสิทธิ์/แพ็กเกจ/security/OCR (review200-round2-sec.md) ──
RULES += [
    dict(file=OCR, method="ResolveSalesCounterpartyAsync",
         must=["OcrCounterpartyMatch.PrefilterToken(", "OcrCounterpartyMatch.NewBranchNote("],
         call_args=[("OcrCounterpartyMatch.PickBuyerByName(", "BuyerBranchCode"),
                    ("OcrCounterpartyCandidate(", "c.TaxId")],
         why="K2-1: ชื่อตรงหลายสาขาของนิติบุคคลเดียว ⇒ แถวตามสาขาผู้ซื้อบนกระดาษ (ต้องส่งเลข/สาขาของผู้สมัคร + สาขาบนกระดาษ) · "
             "คำค้นเสริมให้ชื่อที่สะกดรูปนิติบุคคลต่างกันถึงตัวตัดสิน · เลขมีอยู่คนละสาขา ⇒ โน้ต [BUYER] (สร้างใหม่ต้องบอกเสมอ)"),
    dict(file=OCR, method="ScanAsync",
         must_re=[r"if\s*\(\s*lockContactCreate\s*&&\s*_db\s*\.\s*Database\s*\.\s*CurrentTransaction\s*==\s*null\s*&&\s*"
                  r"_db\s*\.\s*ChangeTracker\s*\.\s*HasChanges\s*\(\s*\)\s*\)\s*await\s+_db\s*\.\s*SaveChangesAsync\s*\("],
         before=[("_db.ChangeTracker.HasChanges()", "UndoOcrContactCreateAfterRollback(")],
         why="K2-2: ของค้างก่อนบล็อกสร้างผู้ติดต่อบันทึกก่อนเปิดธุรกรรมสั้น — rollback ต้องไม่พาของชนิดอื่นหายเงียบ"),
    dict(file=OCR, method="UndoOcrContactCreateAfterRollback",
         must=["_db.ChangeTracker.Entries()", "EntityState.Detached"],
         forbid=["Entries<Contact>()"],
         why="K2-2: ถอด entity ทุกชนิดที่เกิดในบล็อก (ไม่ใช่เฉพาะ Contact)"),
    dict(file=DOC, method="SyncScanToPostedDocumentAsync",
         must=["DiscardUnsavedEntry(scan)"],
         why="K2-4: sync ล้ม ⇒ ถอยการแก้แถวสแกนออกจาก change tracker ก่อน TryTrain/e-Tax hook (ห้าม SaveChanges ถัดไปล้มตาม)"),
    dict(file="Controllers/AiSuggestionController.cs", method="ExplainAnomaly#1",
         must=["AnomalyExplainVerdict.View(", "answer = view.Primary", "reasoning = view.Reasoning"],
         forbid=["reasoning = resp.Reasoning", "answer = resp.PrimaryAnswer"],
         why="RF-6 (กฎเหล็ก #1 kill-switch): ทางเข้าเฉพาะกิจใช้ตัวประกอบเดียวกับทางเข้าที่บันทึก — ปิด provider แล้วเห็นคำอธิบายนักเรียน · คำตอบผ่าน Coerce"),
    dict(file=SUB_MW, method="InvokeAsync",
         must=["SubscriptionGatePolicy.SkipsPublicApiRead(", "SubscriptionGatePolicy.MayCreateSubscriptionRow("],
         before=[("SubscriptionGatePolicy.SkipsPublicApiRead(", "shadowLog.ReadAdminSwitchAsync("),
                 ("SubscriptionGatePolicy.MayCreateSubscriptionRow(", "subscriptionService.GetSubscriptionAsync(")],
         why="S2-6 (ข้อ 23): /api/v1 กันการเขียน · คำขออ่านไม่ผ่านด่าน (ไม่มี query เพิ่ม) · ไม่สร้างแถว FreeTrial จากคำขอ Connected"),
    dict(file="Controllers/SubscriptionController.cs", method="GetSubscription",
         must=["SubscriptionGatePolicy.PageMainFeatures("],
         why="S2-3: หน้าเว็บรู้ฟีเจอร์ของ route ข้อมูลหลักจากตารางเส้นทางตัวเดียวกับ middleware (ไม่เก็บสำเนาใน JS)"),
    dict(file="Controllers/DocumentTemplateController.cs", method="GetAccess",
         must=["HasPermissionAsync(", "PermissionKeys.CompanySettingsEdit", "PermissionKeys.DeniedMessage("],
         why="RF-3: หน้าเทมเพลตปิดปุ่มบันทึกตามคำตอบของตัวตัดสินเดียวกับด่าน [RequirePermission(CompanySettingsEdit)] + ข้อความวิธีขอสิทธิ์"),
    dict(file="Services/Implementations/CmsRenderingService.cs", method="GenerateThemeCssFromEntity",
         # ผู้เรียกอยู่ในรูของ $"…" ⇒ must_lit (ค้นบนโค้ดที่ตัดแค่คอมเมนต์ — must ตัดสตริงทิ้งจึงมองไม่เห็น)
         must_lit=["CssThemeValue.Color(", "CssThemeValue.FontName(", "CssThemeValue.Length(", "CssThemeValue.Radius("],
         forbid_lit=["{theme.PrimaryColor}", "{theme.HeadingFont}", "{theme.BodyFont}", "{theme.MonoFont}", "{theme.BaseFontSize}",
                     "{theme.MaxContentWidth}", "{theme.DangerColor}", "{theme.TextColor}"],
         why="RF-2: ค่าธีม CMS ต่อเข้า CSS ผ่านตัวตรวจตัวเดียว (สี = DocumentTemplateStyle.Hex) — ห้ามต่อดิบ"),
    dict(file="Services/Implementations/CmsSiteService.cs", method="CreateThemeAsync",
         must=["RejectUnsafeTheme(request)"],
         why="RF-2: ค่าธีมที่ไม่ถูกรูปถูกปฏิเสธตอนบันทึก (ไม่แก้ค่าเงียบ)"),
    dict(file="Services/Implementations/CmsSiteService.cs", method="UpdateThemeAsync",
         must=["RejectUnsafeTheme(request)"],
         before=[("RejectUnsafeTheme(request)", "theme.PrimaryColor = request.PrimaryColor")],
         why="RF-2: ด่านเดียวกับตอนสร้าง — ต้องมาก่อนเขียนค่าลง entity"),
    dict(file="Controllers/DocumentBrandController.cs", method="Apply",
         must=["DocumentTemplateStyle.Hex(r.PrimaryColor)", "DocumentTemplateStyle.Hex(r.SecondaryColor)"],
         forbid=["Blank(r.PrimaryColor)", "Blank(r.SecondaryColor)"],
         why="RF-2: สีแบรนด์เก็บเป็นค่ามาตรฐานจากตัวตรวจสีตัวเดียว (หน้าเว็บต่อเข้า style attribute)"),
    dict(file="Controllers/DocumentBrandController.cs", method="Create",
         must=["BrandColorRejection(req)"],
         why="RF-2: สีแบรนด์ไม่ถูกรูป = 400 ข้อความไทย"),
    dict(file="Controllers/DocumentBrandController.cs", method="Update",
         must=["BrandColorRejection(req)"],
         before=[("BrandColorRejection(req)", "Apply(b, req)")],
         why="RF-2: สีแบรนด์ไม่ถูกรูป = 400 ข้อความไทย ก่อนเขียนค่าลง entity"),
]

# ── รอบ 200 ทีม V1H (DECISIONS ข้อ 50–54): ล็อกจุดเรียกของตัวตัดสินใหม่ (pure ทดสอบใน VoidReissueR200HTests) ──
RULES += [
    dict(file=ETAX, method="VoidAsync",
         must=["EtaxVoidPolicy.Decide(", "_db.AddChainedAuditLog(", "a.EntityId == etax.DocumentId", "verdict.EvidenceLabel"],
         must_re=[r"if\s*\(\s*!\s*verdict\s*\.\s*Allowed\s*\)\s*throw\b", r"a\s*\.\s*CompanyId\s*==\s*companyId"],
         before=[("EtaxVoidPolicy.Decide(", "etax.Status = EtaxStatus.Voided")],
         forbid=["etax.Status == EtaxStatus.Accepted", "AuditLogs.Add(", "InvalidOperationException("],
         why="ข้อ 51 (V1H): ยกเลิกแถว e-Tax ในระบบนี้ผ่านตัวตัดสินเดียว — Submitted ต้องมีไฟล์หลักฐานที่เป็นของเอกสารของแถวนี้ + เหตุผล · "
             "audit ใน hash chain · ห้ามประทับสถานะของกรมสรรพากรเอง"),
    dict(file="Controllers/EtaxController.cs", method="Void",
         must=["_etaxService.VoidAsync(companyId, etaxId, request,", "RequireEtaxAsync(companyId, PermissionKeys.EtaxVoid"],
         before=[("RequireEtaxAsync(", "_etaxService.VoidAsync("), (".DenyAttachmentAsync(", "_etaxService.VoidAsync(")],
         why="ข้อ 51: ไฟล์หลักฐานเดินด่านไฟล์แนบตัวเดียวก่อนถึง service · สิทธิ์ยกเลิก e-Tax ก่อนทุกอย่าง"),
    dict(file=DOC_REISSUE, method="ResolveEtaxCancellationAsync",
         must=["EtaxCancellationPath.OriginalStillValid", "LivePaymentCoverageAsync(", "EtaxReissueReview.FlaggedReceiptVatUndone(",
               "ReceiptTotalAmount: rcpt.TotalAmount", "keepUid, rcpt.DocumentType)"],
         before=[("DocumentPermissionHelper.CanApproveAsync(_permissionService, companyId, keepUid", "DocumentVoidPreconditions.EtaxCancellationResolution("),
                 ("LivePaymentCoverageAsync(", "DocumentVoidPreconditions.EtaxCancellationResolution(")],
         must_re=[r"_permissionService\s*==\s*null\s*\|\|\s*!\s*Guid\s*\.\s*TryParse\s*\(\s*actor\s*,\s*out\s+var\s+keepUid\s*\)"],
         why="ข้อ 54 (V1H): ทาง (ค) ใบกำกับเดิมยังใช้ได้ — สิทธิ์ตรวจใน service (ไม่รู้ผู้กด = ปฏิเสธ) · ข้อเท็จจริงจากรายการรับชำระจริงก่อนตัวตัดสิน"),
    dict(file=DOC_REISSUE, method="LivePaymentCoverageAsync",
         must=["p.Amount + p.FeeAmount + p.SettlementAdjustmentAmount", "a.AllocatedAmount"],
         must_re=[r"a\s*\.\s*CompanyId\s*==\s*companyId", r"p\s*\.\s*CompanyId\s*==\s*companyId", r"!\s*p\s*\.\s*IsDeleted"],
         why="ข้อ 54: ยอดครอบนับจากการรับชำระจริงที่ยังมีผล (ไม่ใช่ PaidAmount ที่รวมใบลดหนี้) · tenant"),
    dict(file=DOC_REISSUE, method="GetEtaxReissueReviewAsync",
         must=["EtaxReissueReview.ReclassReversalMisdated(", "new EtaxReissueReviewReport(undone, resolvedRows, excessRows, misdated)"],
         must_lit=['"21913"'],
         must_re=[r"rev\s*\.\s*CompanyId\s*==\s*companyId", r"orig\s*\.\s*CompanyId\s*==\s*companyId", r"a\s*\.\s*CompanyId\s*==\s*companyId"],
         forbid=["SaveChangesAsync(", "ReverseJournalEntryAsync("],
         why="ข้อ 53 (V1H): ตัวกลับภาษีขายที่ลงคนละเดือนอยู่ในรายงานข้อ 44 — อ่านอย่างเดียว · tenant ทุกตาราง"),
]

# ── รอบ 200 ทีม V1I (ฝ่ายค้าน V1H-O1/O2/O3/O5/O6): ล็อกจุดเรียก (pure ทดสอบใน VoidReissueR200ITests) ──
RULES += [
    dict(file=DOCSVC, method="ReflagKeptOriginalReceiptsAsync",
         must=["DocumentVoidPreconditions.KeptOriginalCoverageLost(", "EtaxReissueReview.LastResolutionKeptOriginal(",
               "EtaxReissueReview.KeptOriginalMarker", "r.EtaxCancelRequiredAt = DateTime.UtcNow", "r.EtaxCancelRequiredReason = flag"],
         must_re=[r"r\s*\.\s*CompanyId\s*==\s*companyId", r"r\s*\.\s*EtaxCancelledByCreditNoteId\s*==\s*null"],
         call_args=[("LivePaymentCoverageAsync(", "excludePaymentId: voidedPaymentId")],
         before=[("LivePaymentCoverageAsync(", "DocumentVoidPreconditions.KeptOriginalCoverageLost(")],
         forbid=["catch", "UndoUndueOutputVatReclassAsync(", "Status = DocumentStatus.Voided"],
         why="V1H-O1 (V1I): ใบกำกับที่ยืนยันทาง (ค) แล้วการรับชำระที่ครอบยอดถูกยกเลิก ⇒ ติดธงกลับ (เดิมจบเงียบ) · ยอดครอบไม่นับรายการที่กำลังยกเลิก · "
             "ไม่ถอยภาษี/ไม่ยกเลิกเอง · tenant"),
    dict(file=DOCSVC, method="ReversePaymentInternalAsync",
         must=["ReflagKeptOriginalReceiptsAsync("],
         call_args=[("ReflagKeptOriginalReceiptsAsync(", "payment.Id"), ("ReflagKeptOriginalReceiptsAsync(", "receiptDoc?.Id")],
         why="V1H-O1 (V1I): เส้นใบเดียวต้องถึงตัวติดธงกลับของทาง (ค) และส่งข้อความถึงผู้เรียก"),
    dict(file=DOCSVC, method="ReverseMultiDocPaymentInternalAsync",
         must=["ReflagKeptOriginalReceiptsAsync("],
         call_args=[("ReflagKeptOriginalReceiptsAsync(", "payment.Id"), ("ReflagKeptOriginalReceiptsAsync(", "undoDoc")],
         why="V1H-O1 (V1I): เส้นจัดสรรหลายใบเดินตัวเดียวกับเส้นใบเดียว (R5) — รายใบ"),
    dict(file=DOCSVC, method="VoidPaymentAsync",
         must=["LockDocumentsForPaymentVoidAsync(", "lockDocIds.Add(payment.DocumentId)", "_db.Entry(locked).ReloadAsync("],
         call_args=[("LockDocumentsForPaymentVoidAsync(", "lockDocIds")],
         before=[("LockDocumentsForPaymentVoidAsync(", ".FromSqlRaw("), ("LockDocumentsForPaymentVoidAsync(", "ReversePaymentInternalAsync("),
                 ("LockDocumentsForPaymentVoidAsync(", "ReverseMultiDocPaymentInternalAsync(")],
         why="V1H-O3 (V1I): ล็อกเอกสารทุกใบที่การชำระแตะ (ORDER BY Id — ลำดับเดียวกับเส้นรับชำระ) ก่อนแถว Payment แล้วอ่านใหม่ใต้ล็อก — "
             "ไม่งั้นถอยภาษี/ล้าง OutputVatDueAt จาก PaidAmount เก่าที่มีการรับชำระใหม่ commit พร้อมกัน"),
    dict(file=DOCSVC, method="LockDocumentsForPaymentVoidAsync",
         must=["ExecuteSqlRawAsync(", "e.ReloadAsync("],
         must_lit=['ORDER BY ""Id"" FOR UPDATE', '""CompanyId"" = {1}'],
         call_args=[("ExecuteSqlRawAsync(", "companyId")],
         why="V1H-O3 (V1I): ล็อกเรียงตาม Id คำสั่งเดียว (กัน deadlock) · tenant · อ่านใหม่แถวที่ context ถือไว้"),
    dict(file="Controllers/DocumentController.cs", method="VoidPayment",
         must=["voided.EtaxCancellationFlag", "voided.OutputVatNotice"],
         why="V1H-O1 (V1I): ธงที่ติดระหว่างยกเลิกการชำระต้องถึงผู้กด (เดิมทิ้งผล ตอบแค่สำเร็จ = ล้มเงียบ F2 ข้อ 7)"),
    dict(file=DOC_REISSUE, method="ResolveEtaxCancellationAsync",
         must=["EtaxReissueReview.KeptOriginalMarker"],
         why="V1H-O1 (V1I): ป้ายทาง (ค) ฝั่งเขียนใช้ค่าคงที่ตัวเดียวกับฝั่งอ่าน (LastResolutionKeptOriginal)"),
    dict(file=ETAX, method="VoidAsync",
         must=["EtaxVoidPolicy.StatusForVoid(", "DocumentVoidPreconditions.EtaxEmailedWithRdTimestampAsync(", "EtaxVoidPolicy.EvidenceNotBefore(",
               "a.CreatedAt > evidenceNotBefore"],
         call_args=[("EtaxVoidPolicy.Decide(", "decidedStatus"), ("EtaxVoidPolicy.EvidenceNotBefore(", "etax.SubmittedAt")],
         before=[("EtaxVoidPolicy.StatusForVoid(", "EtaxVoidPolicy.Decide(")],
         forbid=["EtaxVoidPolicy.Decide(etax.Status"],
         why="V1H-O2/O5 (V1I): ตัดสินด้วยสถานะที่รวม e-Tax by Email ประทับเวลาแล้ว (เกณฑ์เดียวกับ EffectiveEtaxAsync) · หลักฐานต้องแนบหลังส่ง e-Tax"),
    dict(file="Helpers/DocumentVoidPreconditions.cs", method="EffectiveEtaxAsync",
         must=["EtaxEmailedWithRdTimestampAsync("],
         forbid=["DocumentEmailLogs"],
         why="V1H-O2 (V1I): เกณฑ์ e-Tax by Email ประทับเวลามีตัวเดียว (EtaxEmailedWithRdTimestampAsync) — ห้ามสำเนาคิวรี"),
]

# ── รอบ 200 ฝ่ายค้านรอบสาม V1I-X1: ลำดับล็อกกลาง "ใบตัวเอง → ใบต้นทาง → เลข JE" (VoidPaymentAsync ล็อกใบต้นทางก่อนเลข JE · อนุมัติ/ยกเลิกต้องเรียงเดียวกัน ไม่งั้น deadlock 40P01) ──
RULES += [
    dict(file=DOC, method="ApproveDocumentAsync#2",
         must=["LockRelatedSourceDocumentAsync(companyId, documentId)"],
         before=[("LockRelatedSourceDocumentAsync(companyId, documentId)", "AutoPostToJournalAsync(")],
         why="V1I-X1: ล็อกใบต้นทางก่อนออกเลข JE (AutoPostToJournalAsync ถือ advisory lock ของเลข RV/JV)"),
    dict(file=DOC, method="VoidDocumentAsync",
         must=["LockRelatedSourceDocumentAsync(companyId, documentId)"],
         before=[("LockRelatedSourceDocumentAsync(companyId, documentId)", "ReverseJournalEntryAsync(")],
         why="V1I-X1: ล็อกใบต้นทางก่อนกลับ JE (ReverseJournalEntryAsync ถือ advisory lock ของเลข JE)"),
    dict(file=DOC, method="LockRelatedSourceDocumentAsync",
         must_lit=["FOR UPDATE", "\\\"CompanyId\\\" = {1} AND \\\"Id\\\" = ", "WHERE \\\"Id\\\" = {0} AND \\\"CompanyId\\\" = {1})"],
         why="V1I-X1: ล็อกแถวใบต้นทางของบริษัทนี้เท่านั้น (tenant ทั้งแถวนอกและ subquery)"),
]

# ── รอบ 200 ทีม PR1: ➕/🗑 พนักงานในรอบเงินเดือน — ตัวตั้งเดียวของ "อยู่ในงวด" + ตัวเติมยอดเดียว + ด่านเดียวกับ ✏️ แก้ยอด ──
_PR1_WHY_ELIG = "PR1: พนักงานที่อยู่ในงวดตัดสินที่ PayrollEmployeeEligibility ตัวเดียว (คำนวณ · เพิ่ม · รายชื่อที่เพิ่มได้) — ห้ามสำเนาเงื่อนไข inline"
_PR1_WHY_EDIT = ("PR1: เพิ่ม/เอาพนักงานออกจากรอบต้องล็อกแถวรอบ (FOR UPDATE) · ผ่านด่าน CanEditAmounts ด้วยหลักฐานยื่น/นำส่งจริงก่อนบันทึก · "
                 "ยอดรวมผ่าน RecomputeRunTotals ตัวเดียว · audit ใน hash chain")
RULES += [
    dict(file=PAYROLL, method="CalculatePayrollAsync",
         must=["PayrollEmployeeEligibility.InPeriod(run.PeriodStart, run.PeriodEnd)", "e.CompanyId == companyId && !e.IsDeleted"],
         forbid=["e.StartDate <= run.PeriodEnd", "e.EndDate >= run.PeriodStart"],
         why=_PR1_WHY_ELIG),
    dict(file=PAYROLL, method="GetAddableEmployeesAsync",
         must=["PayrollEmployeeEligibility.InPeriod(", "e.CompanyId == companyId && !e.IsDeleted", "x.CompanyId == companyId"],
         forbid=["e.StartDate <=", "e.EndDate >="],
         why=_PR1_WHY_ELIG),
    dict(file=PAYROLL, method="UpdatePayrollDetailAsync",
         must=["PayrollDetailAmounts.Apply(d, req, ssoParams)", "PayrollDetailAmounts.RecomputeRunTotals(run)"],
         forbid=["SsoWageBase.Clamp(", "d.NetPay = d.GrossIncome", "run.Details.Sum("],
         why="PR1: ✏️ แก้ยอดกับ ➕ เพิ่มพนักงานใช้ตัวเติมยอด/ยอดรวมตัวเดียว (Helpers/PayrollDetailAmounts) — ห้ามสูตรสองชุด"),
    dict(file=PAYROLL, method="AddPayrollDetailAsync",
         must=["LoadRecalculateLockEvidenceAsync(", "PayrollEmployeeEligibility.Reason(", "PayrollDetailAmounts.Apply(",
               "PayrollDetailAmounts.RecomputeRunTotals(run)", "AddChainedAuditLog(", "tx.CommitAsync(", "tx.RollbackAsync("],
         must_re=[r"if\s*\(\s*!\s*canEditAmt\s*\)\s*throw\b", r"if\s*\(\s*!\s*req\s*\.\s*WithholdingTax\s*\.\s*HasValue\s*\)\s*throw\b",
                  r"if\s*\(\s*!\s*req\s*\.\s*SocialSecurityBase\s*\.\s*HasValue\s*\)\s*throw\b"],
         must_lit=["FOR UPDATE", "\\\"CompanyId\\\" = {1}"],
         call_args=[("PayrollRunEditPolicy.CanEditAmounts(", "editEvidence")],
         before=[("ExecuteSqlRawAsync(", "PayrollRunEditPolicy.CanEditAmounts("),
                 ("PayrollRunEditPolicy.CanEditAmounts(", "_db.SaveChangesAsync("),
                 ("PayrollEmployeeEligibility.Reason(", "_db.SaveChangesAsync("),
                 ("AddChainedAuditLog(", "_db.SaveChangesAsync(")],
         forbid=["PayrollRunLockEvidence.None", "AuditLogs.Add(", "SsoWageBase.Clamp(", "SsoWageBase.Contribution(",
                 "d.NetPay = d.GrossIncome"],
         why=_PR1_WHY_EDIT),
    dict(file=PAYROLL, method="RemovePayrollDetailAsync",
         must=["LoadRecalculateLockEvidenceAsync(", "PayrollDetailAmounts.RecomputeRunTotals(run)", "AddChainedAuditLog(",
               "d.IsDeleted = true", "EmployeeProjectTimes", "tx.CommitAsync(", "tx.RollbackAsync("],
         must_re=[r"if\s*\(\s*!\s*canEditAmt\s*\)\s*throw\b", r"Count\s*\(\s*x\s*=>\s*!\s*x\s*\.\s*IsDeleted\s*\)\s*<=\s*1\s*\)\s*throw\b"],
         must_lit=["FOR UPDATE", "\\\"CompanyId\\\" = {1}"],
         call_args=[("PayrollRunEditPolicy.CanEditAmounts(", "editEvidence")],
         before=[("ExecuteSqlRawAsync(", "PayrollRunEditPolicy.CanEditAmounts("),
                 ("PayrollRunEditPolicy.CanEditAmounts(", "_db.SaveChangesAsync("),
                 ("AddChainedAuditLog(", "_db.SaveChangesAsync(")],
         forbid=["PayrollRunLockEvidence.None", "AuditLogs.Add(", "RemoveRange(", ".Remove(d)"],
         why=_PR1_WHY_EDIT + " · soft-delete (ห้ามลบจริง — ประวัติรอบต้องตามรอยได้) · ห้ามเหลือ 0 คน"),
    dict(file="Controllers/PayrollController.cs", method="AddDetail",
         before=[("RequirePayrollWriteAsync(", "_service.AddPayrollDetailAsync(")],
         must_re=[r"RequirePayrollWriteAsync\s*\([^;]*PermissionKeys\s*\.\s*PayrollRun\s*\)\s*;\s*if\s*\(\s*block\s*!=\s*null\s*\)\s*return\s+block"],
         why="PR1: ➕ เพิ่มพนักงานเข้ารอบ = เขียนข้อมูลเงินเดือน ต้องผ่านด่านสิทธิ์ PayrollRun ก่อนเรียก service"),
    dict(file="Controllers/PayrollController.cs", method="RemoveDetail",
         before=[("RequirePayrollWriteAsync(", "_service.RemovePayrollDetailAsync(")],
         must_re=[r"RequirePayrollWriteAsync\s*\([^;]*PermissionKeys\s*\.\s*PayrollRun\s*\)\s*;\s*if\s*\(\s*block\s*!=\s*null\s*\)\s*return\s+block"],
         why="PR1: 🗑 เอาพนักงานออกจากรอบ ต้องผ่านด่านสิทธิ์ PayrollRun ก่อนเรียก service"),
    dict(file="Controllers/PayrollController.cs", method="GetAddableEmployees",
         before=[("CheckPayrollAccessAsync(", "_service.GetAddableEmployeesAsync(")],
         call_args=[("_service.GetAddableEmployeesAsync(", "CanViewPayrollAsync")],
         why="PR1: รายชื่อ + เงินเดือนพนักงานต้องผ่านด่านดูข้อมูลเงินเดือน · เงินเดือนคืนตามสิทธิ์ดูเงินเดือน"),
]

# ── รอบ 201 ทีม OC (OCR · BACKLOG §1.7 A-OC1/A-OC2/A-OC5 + หมวด C-18..C-24 · คำตัดสินข้อ 91–97): เทสต์ล็อกตัวตัดสิน pure (OcrReview201OcTests)
#    ⇒ ล็อกจุดเรียก/ลำดับ/การใช้ผลใน service ที่นี่ ──
OCR_VI = "Services/Implementations/Ocr/VendorIntelligenceService.cs"
HYGIENE = "Controllers/ContactHygieneController.cs"
BRANCH_X = "Services/BranchCodeExtractor.cs"
RULES += [
    dict(file=OCR, method="SubmitCorrectionAsync",
         must=["OcrCorrectedFieldList.ShouldRememberKnownGood(", "StaleVendorContactNoteAsync("],
         must_re=[r"if\s*\(\s*rememberVendorName\s*\)\s*await\s+_knownGoodCorrector\s*\.\s*RememberUserCorrectionAsync",
                  r"if\s*\(\s*rememberVendorAddress\s*\)\s*await\s+_knownGoodCorrector\s*\.\s*RememberUserCorrectionAsync",
                  r"staleCorrectionNote\s*!=\s*null\s*\?\s*null\s*:\s*result\s*\.\s*MatchedContactId"],
         before=[("StaleVendorContactNoteAsync(", "DecideScanVendorBranchContactAsync(")],
         why="A-OC1: known-good ชื่อ/ที่อยู่ผู้ขายจำเฉพาะเมื่อค่าเปลี่ยนจากที่สแกน (หน้าเว็บส่งค่าเดิมทุกครั้ง) · "
             "C-23: แก้เลขผู้เสียภาษีเป็นนิติบุคคลอื่น ⇒ ถอดการผูกเดิมก่อนตัดสินผู้ติดต่อใหม่"),
    dict(file=OCR, method="StaleVendorContactNoteAsync",
         must=["OcrVendorBranchContact.MatchedContactIsOtherEntity(", "OcrVendorBranchContact.StaleMatchNote(", "MatchedContactCorrectionField"],
         must_re=[r"c\s*\.\s*CompanyId\s*==\s*companyId"],
         must_lit=['"VendorTaxId"'],
         call_args=[("OcrVendorBranchContact.MatchedContactIsOtherEntity(", "userTouchedTaxId")],
         why="C-23: ตัวตัดสินเดียวของสามเส้น (แก้ผลสแกน · สร้างเอกสาร · พรีวิว) · ผู้ใช้เลือกผู้ติดต่อเอง = ไม่ถอด · ถอดเฉพาะเมื่อผู้ใช้แตะเลข "
             "(เลขที่ OCR อ่านเพี้ยนแต่ผูกถูกราย ห้ามถูกถอด) · tenant"),
    dict(file=OCR, method="CreateDocumentFromScanCoreAsync",
         must=["StaleVendorContactNoteAsync(", "OcrSettlementCounterparty.Decide(", "OcrWalkInBuyer.BlockReason("],
         must_re=[r"!\s*vendorIsUs\s*&&\s*!\s*settlementInherited\s*&&"],
         before=[("StaleVendorContactNoteAsync(", "DecideScanVendorBranchContactAsync("),
                 ("OcrSettlementCounterparty.Decide(", "DecideScanVendorBranchContactAsync(")],
         why="C-23 ถอดผู้ติดต่อของเลขเดิมก่อนตัดสินสาขา · C-20 ใบรับ/จ่ายเงินสืบทอดผู้ติดต่อใบต้นทาง (สืบทอดแล้ว = ข้ามตัวตัดสินสาขา) · "
             "C-24 ใบกำกับเต็มรูปห้ามผูกลูกค้าเงินสด (ตรวจซ้ำตอนสร้าง)"),
    dict(file=OCR, method="PreviewDocumentLinesAsync",
         must=["StaleVendorContactNoteAsync(", "OcrSettlementCounterparty.Decide("],
         why="C-23/C-20: \"แก้ในฟอร์มก่อน\" ได้ผู้ติดต่อผู้ขายจากตัวตัดสินเดียวกับเส้นสร้างเอกสาร (CLAUDE.md §H สามเส้นตัวสร้างเดียว)"),
    dict(file=OCR, method="MatchWalkInBuyerAsync",
         must=["OcrWalkInBuyer.BlockReason(", "DocumentSide.IsSales(", "WalkInCustomerContact.GetOrCreateAsync(", "MatchContactAsync(",
               "OcrVendorBranchContact.ScanAlreadyPosted("],
         before=[("OcrWalkInBuyer.BlockReason(", "WalkInCustomerContact.GetOrCreateAsync(")],
         why="C-24: ตัดสินก่อนสร้าง/ผูกผู้ติดต่อเงินสด (ใบกำกับเต็มรูป = บล็อกพร้อมทางไปต่อ) · ผูกผ่านเส้นเลือกผู้ติดต่อเอง (นับเป็นผู้ใช้เลือก)"),
    dict(file=OCR, method="ScanAsync",
         call_args=[("OcrIssuerBranch.ContactAddress(", "registryAddressIsBranch")],
         why="C-22: ที่อยู่แถวสาขาจากทะเบียนได้เฉพาะเมื่อทะเบียน VAT ยืนยันสาขานั้น — ไม่ส่งธง = ถอยไปใช้ที่อยู่ สนญ. (ค่าแต่ง)"),
    dict(file=OCR, method="EnrichContactAddress",
         call_args=[("OcrIssuerBranch.ContactAddress(", "registryAddressIsBranch")],
         why="C-22: ตัวเติมที่อยู่ผู้ติดต่อเดิมใช้ตัวตัดสินเดียวกับเส้นสร้างแถว"),
    dict(file=OCR, method="DeleteScanAsync",
         must=["AddChainedAuditLog("],
         forbid=["AuditLogs.Add("],
         why="A-OC5 (A-PL4): audit ของการลบสแกนต้องเข้า hash chain ของบริษัท (Add ตรง = RowHash null นอก chain)"),
    dict(file=OCR_VI, method="TrainFromDocumentAsync#1",
         call_args=[("OcrWhtLearningScope.Decide(", "WhtCorrectionsPredateBaseline")],
         must=["WhtCorrectionsPredateBaseline"],
         why="C-18 (ข้อ 91): คำแก้ WHT ก่อนกติกา baseline = ไม่รู้ ⇒ ไม่นับเป็นหลักฐาน (เส้นเรียนทีละใบ)"),
    dict(file=OCR_VI, method="BackfillCoreAsync",
         call_args=[("OcrWhtLearningScope.Decide(", "PredateBaseline")],
         must=["WhtCorrectionsPredateBaseline"],
         why="C-18 (ข้อ 91): backfill ข้ามแถวที่คำแก้เกิดก่อนกติกา baseline K-10 (ไม่ลบข้อมูล)"),
    dict(file=BRANCH_X, method="Extract",
         must=["SingleBranchCodeOnPage("],
         why="C-19 (ข้อ 92): สลิปไม่มีบล็อกผู้ซื้อได้ 0.85 เฉพาะเมื่อตัวตรวจ \"รหัสเดียวทั้งหน้า\" ผ่าน"),
    dict(file=BRANCH_X, method="SingleBranchCodeOnPage",
         must=["OcrIssuerBranch.HasAnyStatement(", "HeadOfficeRegex.IsMatch("],
         must_re=[r"codes\s*\.\s*Count\s*!=\s*1"],
         why="C-19: ประโยคประกาศสาขาขัดกัน/มีสำนักงานใหญ่ปน/สองรหัส = ไม่ใช่รหัสเดียว (คงคะแนนเดิม K-2)"),
    dict(file=HYGIENE, method="RetireOcrBranchOrphan",
         must=["HasPermissionAsync(", "PermissionKeys.ContactEdit", "ContactDataHygiene.OrphanRetireBlock(", "ReferencedContactIdsAsync("],
         before=[("HasPermissionAsync(", "SaveChangesAsync("), ("ContactDataHygiene.OrphanRetireBlock(", "SaveChangesAsync(")],
         why="A-OC2: ลบแถวสาขาที่ OCR สร้างได้เฉพาะผู้มีสิทธิ์ Contact.Edit + ตรวจซ้ำที่เซิร์ฟเวอร์ว่ายังไม่มีอะไรอ้างถึง (ก่อนบันทึก)"),
    dict(file=HYGIENE, method="ReferencedContactIdsAsync",
         must_re=[r"d\s*\.\s*CompanyId\s*==\s*companyId", r"s\s*\.\s*CompanyId\s*==\s*companyId", r"w\s*\.\s*CompanyId\s*==\s*companyId",
                  r"a\s*\.\s*CompanyId\s*==\s*companyId", r"r\s*\.\s*CompanyId\s*==\s*companyId"],
         must=["WithholdingTaxCerts", "OcrScanResults", "Documents"],
         why="A-OC2: ตัวนับการอ้างถึงต้องครอบเอกสาร/สแกน/50 ทวิ และกรอง tenant ทุกตาราง (raw query ไม่มี global filter ของบริษัท)"),
]

# ── รอบ 198 ทีม C: ทั้งโฟลเดอร์ Services/Settlement/** ห้ามประกอบ JE เอง (ทีม B เขียนไฟล์ในโฟลเดอร์เดียวกัน) ──
SETTLEMENT_FOLDER_FORBID = dict(
    globs=["Services/Settlement/**/*.cs"],
    patterns=[r"\bnew\s+JournalEntry\b", r"\bnew\s+JournalEntryLine\b", r"\bJournalEntries\s*\.\s*Add(?:Range)?\s*\("],
    why="รอบ 198: JE ใน Services/Settlement/** ผ่าน JournalEntryBuilder (Dr=Cr · ด่านงวดปิด · เลขใบสำคัญ) "
        "หรือเส้นกลับรายการ IAccountingService.ReverseJournalEntryAsync เท่านั้น",
)


def settlement_folder_files():
    out = []
    for g in SETTLEMENT_FOLDER_FORBID["globs"]:
        for path in sorted(SRC.glob(g)):
            out.append((str(path.relative_to(SRC)), path.read_text(encoding="utf-8")))
    return out


def settlement_folder_errors(files) -> list:
    errs = []
    for rel, text in files:
        code = mask(text)
        for rx in SETTLEMENT_FOLDER_FORBID["patterns"]:
            for m in re.finditer(rx, code):
                line = code.count("\n", 0, m.start()) + 1
                errs.append(f"{rel}:{line} ประกอบ JE เอง `{text[m.start():m.end()]}` — {SETTLEMENT_FOLDER_FORBID['why']}")
    return errs


def settlement_folder_self_test(files) -> list:
    fails = []
    if not files:
        return ["self-test SETTLEMENT_FOLDER_FORBID: ไม่พบไฟล์ใน Services/Settlement (glob ผิด?)"]
    rel, text = files[0]
    for sample in ["var je = new JournalEntry { CompanyId = c };", "var je = new JournalEntry();",
                   "var l = new JournalEntryLine { AccountId = a };", "_db.JournalEntries.Add(je);", "db.JournalEntries.AddRange(x);"]:
        if not settlement_folder_errors([(rel, text + "\nclass __X { void F() { " + sample + " } }\n")]):
            fails.append(f"self-test SETTLEMENT_FOLDER_FORBID: ใส่ `{sample}` แล้วไม่ฟ้อง")
    for ok in ["// เดิม new JournalEntry { }", "var b = JournalEntryBuilder.For(db, c, d);", "var s = \"new JournalEntry\";",
               "var x = _db.JournalEntries.AsNoTracking();"]:
        if settlement_folder_errors([(rel, "class __Y { void F() { " + ok + " } }\n")]):
            fails.append(f"self-test SETTLEMENT_FOLDER_FORBID: `{ok}` ถูกฟ้องผิด")
    return fails

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
        # raw string literal เฉพาะเมื่อไม่ใช่ verbatim — `@"""IsActive"" = true"` คือ verbatim ที่ขึ้นต้นด้วย "" (escape)
        # (ทีม W รอบสอง: เดิมตีเป็น raw string ⇒ ตัดโค้ดครึ่งหลังของ AccountingDbContext ทิ้งทั้งไฟล์ ⇒ "ไม่พบเมธอด")
        if not verb and text.startswith('"""', j):
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
    # "ชื่อ#n" = การประกาศลำดับที่ n (นับจาก 0) ของเมธอด overload ชื่อเดียวกัน (รอบ 193 L2: PurgeDocumentAsync มี 3 ตัว
    # ตัวที่ทำงานจริงคือตัวสุดท้าย) · ไม่มี # = ตัวแรก (พฤติกรรมเดิม)
    nth = 0
    if "#" in name:
        name, nth_s = name.split("#", 1)
        nth = int(nth_s)
    found = list(re.finditer(RE_DECL.format(name=re.escape(name)), masked, flags=re.M))
    if len(found) <= nth:
        return None
    m = found[nth]
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
    for p in rule.get("forbid_lit", []):
        m = pat(p).search(lit_body)
        if m:
            errs.append(f"{rel}:{ln(m.start())} {meth} มี `{p}` ซึ่งห้ามใช้ที่นี่ (ค่าดิบต่อเข้าสตริง) — {why}")
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
    # รอบ 199 B-1: ทิ้งผลของด่าน (คำนวณแล้วไม่ return) = อนุมัติใบ VAT ไม่อยู่บนกระดาษผ่าน API ต่อได้ ⇒ ต้องฟ้อง
    ("B1a", "Approve", "if (refusal is not null)\n                return await RefuseApprovalAsync(", "if (refusal is not null)\n                _ = await RefuseApprovalAsync(", True),
    # รอบ 199 B-1: กลับไปแยกชุด "ผ่าน" ด้วย IsGapWarning (รวมชุด VAT) = ขยายคำตัดสินข้อ 12 เองอีกครั้ง ⇒ ต้องฟ้อง
    ("B1b", "Approve", "preview.Where(Helpers.OcrApprovalGapWarning.IsAmountGapWarning)", "preview.Where(Helpers.OcrApprovalGapWarning.IsGapWarning)", True),
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
        for p in rule.get("forbid_lit", []):
            if not any("ห้ามใช้" in e for e in run(body.replace("{", '{ var __x = $@"' + p + '";\n', 1))):
                fails.append(f"self-test: ใส่ `{p}` ในสตริงของ {meth} แล้วไม่ฟ้อง")
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
    verbatim = ('class B {\n    void Cfg() { var f = @"""IsActive"" = true"; }\n'
                '    private void Later()\n    {\n        Real.Call();\n    }\n}\n')
    if check_rule(verbatim, dict(file="y.cs", method="Later", must=["Real.Call("], why="t")):
        fails.append("self-test: verbatim string ที่ขึ้นต้นด้วย \"\" ถูกตีเป็น raw string (โค้ดหลังจากนั้นหายทั้งไฟล์)")
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
    ocr_files = folder_forbid_files()
    errs += folder_forbid_errors(ocr_files)
    settle_files = settlement_folder_files()
    errs += settlement_folder_errors(settle_files)
    st = self_test() + folder_forbid_self_test(ocr_files) + settlement_folder_self_test(settle_files)
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
