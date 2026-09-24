#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ล็อก "จุดเรียก" ของด่านเงิน/ภาษี/สต็อกใน service — ด่านที่มีแต่ไม่ถูกเรียก = ไม่มีด่าน

ที่มา (รอบ 193 · ฝ่ายค้าน M2)
----------------------------
เทสต์ของรอบ 193 ล็อกแค่ **helper** (pure) ไม่ได้ล็อกว่า service ยังเรียก helper นั้นอยู่ ⇒
ถอดการแก้ออกจาก service แล้วเทสต์ยังเขียวทั้งชุด:
  • `SsoUnpaidLeaveWageTests` ประกอบสูตรเองในไฟล์เทสต์ — `PayrollService` กลับไปใช้
    `GrossWage(proratedBaseSalary, …)` (ไม่หักลา) เทสต์ก็ไม่รู้
  • ด่าน D-01 (50 ทวิรายเดือนใช้ `EmployeeTaxIdentity.Resolve`) · `CalculatePayrollAsync` ใช้
    `PayrollRunEditPolicy.CanRecalculate` · POS "ตัดสต็อกก่อน JE" — ไม่มีอะไรฟ้องถ้าถูกถอด
เรพนี้ไม่มีเทสต์ระดับ service (ไม่มี InMemory/SQLite DbContext ใน Accounting.Tests) จึงล็อกด้วย
**รูปทรงของโค้ด** ในเมธอดที่ระบุ: ต้องมีการเรียก X · X ต้องมาก่อน Y · ห้ามประกอบสูตรเองซ้ำ (Z)

ทำไมเป็น checker ได้ (ไม่ขัด F4 ข้อ 1/3): ไม่ต้องรู้ชนิด ไม่ต้องรู้ taint · scope แคบเป็นรายเมธอด
ที่ระบุชื่อ (OWNER file) · ค้นบนโค้ดที่ตัดคอมเมนต์/สตริงแล้ว ⇒ คอมเมนต์ที่ "พูดถึง" การเรียกไม่นับ

กติกาเพิ่มใหม่: เพิ่มแถวใน RULES · negative test รันทุกครั้งที่รัน checker (ไม่ต้องจำ --self-test):
แต่ละกติกาถูกป้อนเมธอดจริงที่ "ถอดการแก้" (ลบ/สลับ/คอมเมนต์การเรียก) แล้วต้องฟ้อง
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

# (ไฟล์, เมธอด, must[], before[(a, b)], forbid[], เหตุผล)
RULES = [
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
    (PAYROLL, "CalculatePayrollAsync",
     ["LoadRecalculateLockEvidenceAsync(", "PayrollRunEditPolicy.CanRecalculate(", "SsoWageBase.ForPeriod("],
     [("PayrollRunEditPolicy.CanRecalculate(", "RemoveRange(run.Details)")],
     ["SsoWageBase.GrossWage(", "SsoWageBase.PeriodBase(", "SsoWageBase.Clamp(", "SsoWageBase.SalaryPaidThisPeriod("],
     "#35 คำนวณใหม่ต้องผ่านตัวตัดสินเดียว (พร้อมหลักฐานยื่น/นำส่ง/ปันต้นทุน) ก่อนลบแถวเดิม · "
     "ฐาน ปกส. ต้องประกอบที่ SsoWageBase.ForPeriod ตัวเดียว (ตัวที่เทสต์ D-02 เรียก)"),
    (PAYROLL, "MapToPayrollRunResponse",
     ["PayrollRunEditPolicy.CanRecalculate("], [], [],
     "ปุ่มคำนวณใหม่บนจอต้องตัดสินด้วยตัวเดียวกับด่าน"),
    (PAYROLL, "LoadRecalculateLockEvidenceAsync",
     ["ComplianceFilings", "TaxReports", "EFilingExports", "StatutoryRemittances", "EmployeeProjectTimes",
      "SsoSettledAt", "PayrollRunLockEvidence.From("], [], [],
     "หลักฐาน 'ยื่น/นำส่ง/ปันต้นทุนแล้ว' ต้องครบทุกแหล่ง"),
    (PAYROLL, "IssueMonthlyPnd1CertsAsync",
     ["EmployeeTaxIdentity.Resolve(emp.TaxId, emp.CitizenId)", "payeeTaxId == null"],
     [("EmployeeTaxIdentity.Resolve(", "payeeTaxId == null")],
     ["string.IsNullOrWhiteSpace(emp.TaxId)", "string.IsNullOrEmpty(emp.TaxId)", "emp.TaxId == null"],
     "D-01 ด่าน 50 ทวิรายเดือนต้องใช้ resolver กลาง (TaxId → เลขบัตร) ไม่ใช่ emp.TaxId เดี่ยว ๆ"),
    (POS, "CompleteOrderAsync",
     ["DeductSaleStockAsync(", "CreateSalesJournalEntryAsync("],
     [("DeductSaleStockAsync(", "CreateSalesJournalEntryAsync(")], [],
     "E-01 ตัดสต็อกก่อน JE — COGS ของ JE มาจากต้นทุนที่ ledger ตัดจริง"),
    (POS, "SyncOfflineOrderAsync",
     ["DeductSaleStockAsync(", "CreateSalesJournalEntryAsync("],
     [("DeductSaleStockAsync(", "CreateSalesJournalEntryAsync(")], [],
     "E-01 เส้นออฟไลน์ต้องตัดสต็อกก่อน JE เหมือนเส้นออนไลน์"),
    (POS, "VoidOrderAsync",
     ["PosVoidSaleJournal.Decide(", "LoadSaleUnitCostsAsync("],
     [("PosVoidSaleJournal.Decide(", "ReverseJournalEntryAsync(")], [],
     "ยกเลิกบิลต้องตัดสิน JE ขายที่อาจถูกกลับด้วยมือแล้ว (ห้ามกลับซ้ำ) · คืนวัตถุดิบด้วยต้นทุน ณ วันขาย"),
    (POS, "RefundOrderAsync",
     ["LoadSaleUnitCostsAsync(", "PosCogsBooking.RefundCogs("], [], [],
     "คืนเงินเมนูสูตร: วัตถุดิบกลับด้วยต้นทุน ณ วันขาย ให้ตรงกับ COGS ที่ JE กลับ"),
    (POS, "ApplyRecipeConsumptionAsync",
     ["PosCogsBooking.RecipeCost(", "UnitCostOverride: restockCost"], [], [],
     "COGS สูตรนับเฉพาะวัตถุดิบ TrackStock · ขาคืนใช้ต้นทุน ณ วันขาย"),
    (BOT, "SyncRatesToCompanyAsync",
     ["CurrencyRateSync.Decide("], [], [],
     "sync ธปท. ต้องเขียนทับแถวอัตรา 0 ที่ค้าง (ไม่ข้ามเพราะ 'มีแถวแล้ว')"),
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
    (COMMISSION, "UpdatePlanAsync",
     ["CommissionPlanRules.IsDeactivateOnly(", "CommissionPlanRules.Validate("],
     [("CommissionPlanRules.IsDeactivateOnly(", "CommissionPlanRules.Validate(")], [],
     "ปิดใช้งานแผนเก่าต้องไม่ถูกบังคับให้แก้อัตรา"),
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


# ── ตัดคอมเมนต์/สตริงโดยคงตำแหน่ง (เพื่อ index ของ before[] และเลขบรรทัด) ────────────────
def mask(text: str) -> str:
    out = list(text)
    n = len(text)

    def blank(a, b):
        for k in range(a, min(b, n)):
            if out[k] != "\n":
                out[k] = " "

    def skip_string(i):
        """i ชี้ที่ prefix ($/@) หรือ " — คืน index หลังปิดสตริง (รองรับรูอินเทอร์โพเลตที่มีสตริงซ้อน)"""
        j = i
        interp = verb = False
        while j < n and text[j] in "$@":
            interp |= text[j] == "$"
            verb |= text[j] == "@"
            j += 1
        if text.startswith('"""', j):  # raw string — มองเป็นก้อนทึบ
            end = text.find('"""', j + 3)
            return n if end < 0 else end + 3
        j += 1  # ข้าม "
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
                j = skip_code_block(j + 1)  # คืน index หลัง } ที่ปิดรู
                continue
            if not verb and c == "\n":
                return j
            j += 1
        return n

    def skip_code_block(j):
        depth = 0
        while j < n:
            c = text[j]
            if c in "$@\"" and (c == '"' or (j + 1 < n and text[j + 1] in '"$@')):
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
        if k < n and text[k] == "\\":
            k += 2
        else:
            k += 1
        while k < n and text[k] != "'" and text[k] != "\n" and k - j < 12:
            k += 1
        return k + 1

    i = 0
    while i < n:
        c = text[i]
        if text.startswith("//", i):
            e = text.find("\n", i)
            e = n if e < 0 else e
            blank(i, e); i = e; continue
        if text.startswith("/*", i):
            e = text.find("*/", i + 2)
            e = n if e < 0 else e + 2
            blank(i, e); i = e; continue
        if c == '"' or (c in "$@" and i + 1 < n and text[i + 1] in '"$@'):
            e = skip_string(i)
            blank(i, e); i = e; continue
        if c == "'":
            e = skip_char(i)
            blank(i, e); i = e; continue
        i += 1
    return "".join(out)


RE_DECL = r"^[ \t]*(?:public|private|internal|protected)\b[^;\n=]*?\b{name}\s*(?:<[^>\n]*>)?\s*\("


def method_body(masked: str, name: str):
    """คืน (start, end) ของบอดี้ { … } ของเมธอดชื่อนี้ (ตัวแรก) หรือ None"""
    m = re.search(RE_DECL.format(name=re.escape(name)), masked, flags=re.M)
    if not m:
        return None
    # หาวงเล็บปิดของพารามิเตอร์ แล้วปีกกาแรก
    j = m.end() - 1
    depth = 0
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


def check_rule(text: str, rule):
    rel, meth, must, before, forbid, why = rule
    masked = mask(text)
    span = method_body(masked, meth)
    if span is None:
        return [f"{rel}: ไม่พบเมธอด {meth} (ย้าย/เปลี่ยนชื่อ? ต้องแก้ RULES ให้ตรง — ห้ามปล่อยกติกาที่ไม่ตรวจอะไร)"]
    body = masked[span[0]:span[1]]
    line0 = masked.count("\n", 0, span[0]) + 1
    errs = []
    for p in must:
        if p not in body:
            errs.append(f"{rel}:{line0} {meth} ไม่เรียก `{p}` — {why}")
    for a, b in before:
        ia, ib = body.find(a), body.find(b)
        if ia >= 0 and ib >= 0 and ia > ib:
            errs.append(f"{rel}:{line0 + body.count(chr(10), 0, ib)} {meth}: `{a}` ต้องมาก่อน `{b}` — {why}")
    for p in forbid:
        idx = body.find(p)
        if idx >= 0:
            errs.append(f"{rel}:{line0 + body.count(chr(10), 0, idx)} {meth} ประกอบเองด้วย `{p}` — {why}")
    return errs


def _code_positions(raw: str, pat: str):
    """ตำแหน่งของ pat ที่อยู่ในโค้ดจริง (ไม่ใช่คอมเมนต์/สตริง) ของ raw"""
    m = mask(raw)
    out, k = [], m.find(pat)
    while k >= 0:
        out.append(k)
        k = m.find(pat, k + 1)
    return out


def _replace_code(raw: str, pat: str, repl: str, first_only: bool = False) -> str:
    pos = _code_positions(raw, pat)
    if first_only:
        pos = pos[:1]
    for k in reversed(pos):
        raw = raw[:k] + repl + raw[k + len(pat):]
    return raw


def self_test() -> list:
    """negative test ด้วยโค้ดจริง: ถอดการแก้ทีละแบบแล้วต้องฟ้อง · ของจริงต้องไม่ฟ้อง"""
    fails = []
    cache = {}
    for rule in RULES:
        rel, meth, must, before, forbid, _ = rule
        text = cache.setdefault(rel, (SRC / rel).read_text(encoding="utf-8"))
        span = method_body(mask(text), meth)
        if span is None:
            continue  # รายงานในรอบจริงอยู่แล้ว
        head, body_raw, tail = text[:span[0]], text[span[0]:span[1]], text[span[1]:]
        for p in must:
            # (1) ลบการเรียกที่บังคับ (ทุกจุดที่เป็นโค้ด) → ต้องฟ้อง
            # ตัวแทนต้องไม่มี p เป็น substring (ไม่งั้น "Removed_X" ยังมี "X" อยู่ = ด่านผ่านเอง)
            removed = _replace_code(body_raw, p, "REMOVED_CALL_SITE")
            if not any(f"`{p}`" in e for e in check_rule(head + removed + tail, rule)):
                fails.append(f"self-test: ลบ `{p}` จาก {meth} แล้ว checker ไม่ฟ้อง")
            # (2) เหลือไว้แค่ในคอมเมนต์ → ต้องฟ้อง (คอมเมนต์ที่พูดถึงการเรียกไม่ใช่การเรียก)
            commented = removed.replace("{", "{ // " + p + "\n", 1)
            if not any(f"`{p}`" in e for e in check_rule(head + commented + tail, rule)):
                fails.append(f"self-test: `{p}` ที่อยู่ในคอมเมนต์ถูกนับเป็นการเรียกใน {meth}")
        # (3) สลับลำดับ → ต้องฟ้อง
        for a, b in before:
            tok = "SWAP_TOKEN_(" 
            swapped = _replace_code(body_raw, a, tok, first_only=True)
            swapped = _replace_code(swapped, b, a, first_only=True)
            swapped = swapped.replace(tok, b, 1)
            if not any("ต้องมาก่อน" in e for e in check_rule(head + swapped + tail, rule)):
                fails.append(f"self-test: สลับ `{a}` ↔ `{b}` ใน {meth} แล้ว checker ไม่ฟ้อง")
        # (4) ประกอบสูตรเองซ้ำ → ต้องฟ้อง
        for p in forbid:
            injected = body_raw.replace("{", "{ var __x = " + p + "1m);\n", 1)
            if not any("ประกอบเอง" in e for e in check_rule(head + injected + tail, rule)):
                fails.append(f"self-test: ใส่ `{p}` ใน {meth} แล้ว checker ไม่ฟ้อง")
    # (5) ตัวตัดสตริง: การเรียกที่อยู่ในสตริง/สตริงอินเทอร์โพเลตซ้อน ต้องไม่นับ · ปีกกาในสตริงต้องไม่ทำให้บอดี้ขาด
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
    rule = ("x.cs", "Foo", ["Baz.Real(", "Bar.Call("], [], [], "t")
    errs = check_rule(sample, rule)
    if not (len(errs) == 1 and "Bar.Call(" in errs[0]):
        fails.append(f"self-test: ตัวตัดสตริงผิด — คาด 1 ข้อ (Bar.Call ในสตริง/เมธอดอื่นไม่นับ) ได้ {errs}")
    return fails


def main() -> int:
    errs = []
    for rule in RULES:
        path = SRC / rule[0]
        if not path.exists():
            errs.append(f"{rule[0]}: ไม่พบไฟล์")
            continue
        errs += check_rule(path.read_text(encoding="utf-8"), rule)
    st = self_test()
    for e in errs:
        print("❌ " + e)
    for e in st:
        print("❌ " + e)
    if errs or st:
        return 1
    if "--self-test" in sys.argv:
        print("self-test: ผ่าน")
    print(f"required_call_site_check: {len(RULES)} กติกา ผ่าน (+ negative test ในตัว)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
