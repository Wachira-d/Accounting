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
    (APIV1, "Approve",
     ["PreviewApprovalWarningsAsync(", "ApprovalAckSource.ApiClient", "withAiHints: false"], [], [],
     "C6: API ห้ามเรียก AI เสริมคำเตือนแล้วโยนทิ้ง · [Σ-GAP] ไม่ขัดจังหวะ API"),
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
     ["ExecuteSqlRawAsync(", "ids.ToArray()", "ReloadAsync(", "d.CompanyId == companyId"], [("ExecuteSqlRawAsync(", "ReloadAsync(")], [],
     "B2 ล็อกแถวใบมัดจำ (เรียงตาม Id) แล้วอ่านค่าล่าสุดของแถวที่ context ถือ — อนุมัติพร้อมกันต้องต่อคิว"),
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
    dict(file=DOCSVC, method="LoadTaxedDepositsByRefAsync", must_lit=["FOR UPDATE"],
         why="B2 คำสั่งล็อกแถวต้องเป็น FOR UPDATE จริง (ไม่ใช่ SELECT เปล่า)"),
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
               "RevertTrackedChangesSinceAsync(", "DepositKindDocumentRules.ForfeitRetryHint(", "PaymentDate:"],
         before=[("DepositKindDocumentRules.ResumeForfeitInvoice(", "CreateDocumentAsync("),
                 ("ApplyDepositToInvoiceAsync(", "AppendInternalNote(")],
         forbid=["ChangeTracker.Clear(", "decision.LateVatNote", "DepositAppliedToDocumentId is Guid priorId"],
         why=_M1_WHY),
    dict(file=DOC, method="RevertTrackedChangesSinceAsync",
         must=["EntityState.Detached", "ReloadAsync("], forbid=["ChangeTracker.Clear("],
         why="รอบ 194 M1(ก): ถอยเฉพาะ entity ที่เกิดหลังจุดตั้งต้น + อ่านค่าจริงของ entity เดิม — ห้ามล้างทั้ง context"),
    dict(file=DOC, method="RealizeDepositAsync",
         must=["JobLock.RunExclusiveAsync(", "AdvisoryLockKey.DepositRealize", "ReloadAsync("],
         before=[("JobLock.RunExclusiveAsync(", "RealizeDepositCoreAsync("), ("ReloadAsync(", "RealizeDepositCoreAsync(")],
         why="รอบ 194 M1(จ)/P-a: ล็อกระดับใบมัดจำแล้วอ่านค่าล่าสุดก่อนตัดสิน — สองคำขอพร้อมกันห้ามได้ใบกำกับสองใบ"),
    dict(file=DOC, method="RealizeDepositCoreAsync",
         must=["request.ForfeitAs == null", "DepositPolicyResolver.PlainRealizeProblem(", "DepositPolicyResolver.RevenueAccountPlan(",
               "DepositPolicyResolver.UndueVatToRecognize(", "DepositPolicyResolver.ForfeitTaxPointDecision(",
               "DepositPolicyResolver.ForfeitZeroVatDeferred(", "VatPeriodDeclaredOrFiledAsync("],
         call_args=[("DepositPolicyResolver.ForfeitVatDecision(", "depositOutputVatDeferred")],
         before=[("DepositPolicyResolver.ForfeitTaxPointDecision(", "IssueForfeitTaxInvoiceAsync("),
                 ("DepositPolicyResolver.UndueVatToRecognize(", "_db.JournalEntries.Add(je)")],
         forbid=["kindForfeitAccount.Trim() : request.RevenueAccountCode", "var vatMove = recognizeDeferredVat ? doc.VatAmount",
                 "forfeit.LateVatNote"],
         why="รอบ 194 M2–M5: ริบ = ForfeitAs ระบุ (รับรู้ตามปกติไม่ใช้บัญชีริบ · เต็มยอดที่ยังไม่เสีย VAT ปฏิเสธพร้อมทางไปต่อ) · VAT 0 โดยชอบไม่ออกใบกำกับ 7% · "
             "VAT พักย้ายเท่าที่เหลือจริง · tax point ตามสถานะงวดเดือนรับเงิน"),
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
    dict(file=DOC, method="DepositsAppliedToAsync",
         must=["j.CompanyId == companyId", "d.CompanyId == companyId", "j.Reference == documentNumber"],
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
