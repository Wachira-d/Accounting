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
import functools
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
         # รอบ 201 ฝ่ายค้าน PR2 (P1-a): การนำส่ง ภ.ง.ด.1 รายงวด (StatutoryRemittance WhtPnd1) = หลักฐานยื่นแล้ว · "นำส่ง สปส." ยังต้องผูกกับรอบ
         forbid_lit=['"SsoSps110"'],
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
# คำตัดสินข้อ 138 (2026-10-05): แปลงบางส่วน "ห้ามล็อกจำนวน — ยอดเงินรวมสำคัญที่สุด" · ด่านเดียวที่หยุดถามคือยอดสะสมเกินใบต้นทาง
# (ยอดจริงของใบลูก ไม่ใช่จำนวน × ราคาต้นทาง) และต้องยืนยันแล้วไปต่อได้ · ห้ามข้อความล็อกจำนวนเดิมกลับมา
RULES += [
    dict(file=DOC, method="ConvertDocumentPartialAsync",
         must=["PartialConvertPolicy.IsOverAmount(", "PartialConvertPolicy.QuantityWarnings(", "ComputeConsumedBaseAsync("],
         must_re=[r"if\s*\(\s*!\s*request\.ConfirmOverSourceAmount\s*\)\s*throw\b",
                  r"ValidateConversionAsync\([^;]*partialBillingSplit:\s*true\s*\)"],
         before=[("PartialConvertPolicy.IsOverAmount(", "ConvertCoreAsync(")],
         forbid_lit=["แต่คงเหลือให้"],
         why="#138 แปลงบางส่วนห้ามล็อกจำนวน — ถามยืนยันเฉพาะยอดเงินสะสมเกินใบต้นทาง (ยอดจริงของใบลูก) แล้วไปต่อได้ · ใบแจ้งหนี้ใบที่ 2+ ต้องผ่านด่านรายได้ซ้ำแบบแบ่งบิล"),
    # คำตัดสินข้อ 138ข: คู่ค้าไม่ตรงฝั่งเอกสาร ⇒ 409 + รหัสกฎ ให้หน้าเว็บเสนอ "เปิดสถานะให้เลย" (_createDocumentFixingContactRole)
    dict(file=DOC, method="CreateDocumentAsync",
         must=["ContactRoleRule.CustomerRequired", "ContactRoleRule.SupplierRequired"],
         forbid_lit=["ไม่ได้ตั้งค่าเป็นลูกค้า — กรุณาเปิดสถานะ 'ลูกค้า' ก่อนออกเอกสารขาย\");"],
         why="#138ข ถ้ากลับไปโยน InvalidOperationException ไม่มีรหัสกฎ หน้าเว็บเสนอเปิดสถานะไม่ได้ ผู้ใช้กลับไปเจอทางตัน"),
    # คำตัดสินข้อ 139: ผูกใบที่สร้างแยกเข้าใบเสนอราคา — ด่านชุดเดียวกับแปลงบางส่วน + ไม่แตะช่องที่พิมพ์ลงกระดาษ (§86/4)
    dict(file="Services/Implementations/DocumentService.Link.cs", method="LinkToSourceAsync",
         must=["DocumentLinkPolicy.ChildBlockReason(", "DocumentLinkPolicy.SourceBlockReason(",
               "PartialConvertPolicy.IsOverAmount(", "ComputeConsumedBaseAsync(", "_db.AddChainedAuditLog("],
         must_re=[r"ValidateConversionAsync\([^;]*partialBillingSplit:\s*true\s*\)",
                  r"if\s*\(\s*!\s*request\.ConfirmOverSourceAmount\s*\)\s*throw\b"],
         must_lit=["FOR UPDATE"],
         before=[("ValidateConversionAsync(", "child.RelatedDocumentId = source.Id"),
                 ("PartialConvertPolicy.IsOverAmount(", "_db.SaveChangesAsync(")],
         forbid=["child.Reference", "ApproveDocumentAsync(", "AutoPostToJournalAsync(", "ApplyStockMovementsAsync("],
         why="#139 ผูกภายหลังต้องผ่านด่านเดียวกับการแปลง · ไม่เปลี่ยนเนื้อกระดาษ/JE/สต็อก"),
    dict(file="Services/Implementations/DocumentService.Link.cs", method="UnlinkSourceAsync",
         must=["_db.AddChainedAuditLog("],
         must_re=[r"child\.SourceLinkedAt\s*==\s*null"],
         why="#139 ยกเลิกได้เฉพาะการผูกภายหลัง — ใบที่แปลงมาจริงยกเลิกผูกแล้วแปลงซ้ำ = สองใบต่อบิลเดียว"),
    # แปลงทั้งฉบับไม่มีด่านยอดเงิน ⇒ ต้องคงด่านรายได้ซ้ำเต็ม (ห้ามส่งธงแบ่งบิล)
    dict(file=DOC, method="ConvertDocumentAsync",
         must=["ValidateConversionAsync("],
         forbid=["partialBillingSplit"],
         why="#138 แปลงทั้งฉบับไม่มีด่านยอดสะสม — เปิดแบ่งบิลที่นี่ = รายได้ซ้ำโดยไม่มีด่านไหนเห็น"),
    # ข้ามด่านรายได้ซ้ำได้เฉพาะลูกที่ด่านยอดเงินมองเห็น (มีบรรทัดอ้าง SourceLineId ของต้นทาง)
    dict(file=DOC, method="ValidateConversionAsync",
         must_re=[r"sourceLineIds\.Contains\(\s*l\.SourceLineId\.Value\s*\)",
                  r"source\.DocumentType\s+is\s+not\s*\(\s*DocumentType\.Invoice\s+or\s+DocumentType\.TaxInvoice\s*\)"],
         why="#138 ลูก Receipt/RV หรือใบที่ไม่มี SourceLineId ไม่ถูกนับในยอดสะสม — ต้องยังกันรายได้ซ้ำ · อัปเกรด INV→TIV ยังกัน"),
    # ทีมตรวจเส้นแปลง 2026-10-08 (ข้อ 2/3/6/10) — ด่านที่ถอดแล้วเทสต์ pure-logic ยังเขียว
    dict(file=DOC, method="ValidateConversionAsync", must_lit=['"CONVERT-BILLED-VIA-PARENT"'],
         must_re=[r"source\.DocumentType\s*==\s*DocumentType\.DeliveryNote", r"!d\.IsDeposit"],
         why="ข้อ 2 ทิศกลับ: DN → INV ทั้งที่ QT แม่เก็บเงินตรงแล้ว = เรียกเก็บซ้ำ · ใบมัดจำไม่นับ (หักมัดจำปกติ)"),
    dict(file=DOC, method="ComputeConsumptionAsync", must=["LoadBilledViaDeliveryAsync("],
         why="ข้อ 2: ใบแจ้งหนี้ที่ออกจากใบส่งของต้องนับเข้าแกนวางบิลของใบเสนอราคา (QT→DN→INV แล้ว QT→INV ซ้ำ)"),
    dict(file=DOC, method="ComputeConsumedBaseAsync", must=["LoadBilledViaDeliveryAsync("],
         why="ข้อ 2: ยอดสะสม (ด่าน 422) ต้องเห็นใบหลานผ่านใบส่งของเหมือนด่านจำนวน"),
    dict(file=DOC, method="LoadBilledViaDeliveryAsync",
         must_re=[r"md\.CompanyId\s*==\s*companyId", r"gd\.CompanyId\s*==\s*companyId", r"consuming\.Contains\(\s*gd\.Id\s*\)"],
         must_lit=["DocumentType.DeliveryNote", "DocumentType.GoodsReceiptNote"],
         forbid_lit=["DocumentType.BillingNote"],
         why="ข้อ 2: ยกผ่านใบส่งของ/ใบรับสินค้าเท่านั้น (ผ่านใบวางบิล = นับซ้ำ) · กรองบริษัททั้งสองชั้น (กฎ M)"),
    dict(file=DOC, method="ConvertCoreAsync",
         must_re=[r"source\.DocumentType\s*==\s*DocumentType\.PurchaseOrder\s*&&\s*targetType\s*==\s*DocumentType\.GoodsReceiptNote"],
         why="ข้อ 3: PO → PI ตรงแล้ว PO → GRN = สต็อกเข้าสองรอบ (กระจกของ E-05)"),
    dict(file=DOC, method="BatchConvertDocumentsAsync", must_lit=["errors.Add("],
         must_re=[r"catch\s*\(\s*Exception\s+\w+\s*\)\s*when\s*\(\s*\w+\s+is\s+InvalidOperationException"],
         why="ข้อ 10: ใบที่แปลงไม่ได้ต้องคืนให้หน้าเว็บ · ห้ามกลืน error ทุกชนิด (ฐานข้อมูล/บั๊กต้องดัง)"),
    # ฝ่ายค้าน 2026-10-08 P1: แถวใหม่ใต้ parent ที่ติดตามอยู่ผ่าน EfNewChild ตัวเดียว (ไม่ Modified · ไม่เบิ้ลในคอลเลกชัน)
    # ทีมตรวจงานค้าง 2026-10-08 (C-02): ใบวางบิลรวมใบแจ้งหนี้ห้ามแปลง/ห้ามรับชำระตรง (รายได้ซ้ำ · ลูกหนี้ใบเดิมไม่ถูกตัด)
    dict(file=DOC, method="ValidateConversionAsync", must=["BillingNoteKind.IsRollup("],
         why="C-02: ใบเสร็จจากใบวางบิลรวมลงแบบขายสด ⇒ รายได้ซ้ำ"),
    dict(file=DOC, method="CreatePaymentAsync", must=["BillingNoteKind.PaymentBlockedMessage("],
         why="C-02: ข้อความเดิมพาผู้ใช้ไปแปลงใบวางบิลเป็นใบเสร็จ (= เส้นรายได้ซ้ำ)"),
    # ทีมตรวจงานค้าง C-01: รายได้รวมของใบเสนอราคานับทุกทาง — ตัวนับเดียว LoadRootRevenueAsync ทุกจุด
    dict(file=DOC, method="LoadRootRevenueAsync",
         must=["ConsumingChildDocIds(", "RootRevenueLedger.CountsAsRevenue(", "DocumentType.BillingNote"],
         must_re=[r"d\.CompanyId\s*==\s*companyId"],
         why="C-01: ใบวางบิลเป็นใบกลาง (หลานผ่านใบวางบิลต้องนับ) · กรองบริษัท"),
    dict(file=DOC, method="CollectApprovalWarningsAsync", must=["LoadRootRevenueAsync(", "RootRevenueLedger.OverMessage("],
         why="C-01: ชั้นสุดท้ายครอบทางสร้างมือ/API/แก้ราคาหลังแปลง"),
    dict(file=DOC, method="ConvertDocumentPartialAsync", must=["LoadRootRevenueAsync("],
         why="C-01: แปลงบางส่วนต้องเห็นรายได้ผ่านใบวางบิล/ใบเสร็จขายสด"),
    dict(file=DOC, method="ConvertDocumentAsync", must=["LoadRootRevenueAsync("],
         why="C-01: ใบเสร็จขายสดทั้งใบหลังเรียกเก็บทางอื่น = รายได้ซ้ำ"),
    dict(file="Services/Implementations/DocumentService.Link.cs", method="LinkToSourceAsync",
         must=["LoadRootRevenueAsync(", "ResolveRootQuotationAsync(companyId, source.Id)"],
         before=[("ResolveRootQuotationAsync(", "LoadRootRevenueAsync(")],
         why="C-01: ผูกต้องเห็นรายได้รวมของใบเสนอราคา · รุ่นสอง: ใบส่งของยกขึ้นไปนับที่ใบเสนอราคาต้นทาง (ห้ามส่ง id ใบส่งของเป็น root ตรง)"),
    # คำตัดสินเจ้าของ 2026-10-08 "Add DN + GRN": ใบแจ้งหนี้ซื้อ → ใบรับสินค้าเปลี่ยนการลงบัญชีตอนอนุมัติ ⇒ ด่านร่าง/ใบสั่งซื้อเดียวกัน/ห้ามถอดหลังลง
    dict(file="Services/Implementations/DocumentService.Link.cs", method="LinkToSourceAsync",
         must=["ParentTypeAsync(companyId, child.RelatedDocumentId)", "SourceBlockReason(child.DocumentType,"],
         must_re=[r"childPoMismatch:\s*childFacts\.ParentIsPurchaseOrder\s*&&\s*source\.RelatedDocumentId\s*!=\s*child\.RelatedDocumentId",
                  r"if\s*\(\s*childFacts\.ParentIsPurchaseOrder\s*\)\s*foreach"],
         before=[("DocumentLinkPolicy.ChildBlockReason(", "child.RelatedDocumentId = source.Id")],
         why="ทางซ่อม PO → GRN เฉพาะใบสั่งซื้อเดียวกัน + ล้างบรรทัดที่ชี้ใบสั่งซื้อ · ใบแจ้งหนี้ซื้อที่ไม่ใช่ร่างห้ามผูก (ตัวตัดสินใน DocumentLinkPolicy)"),
    dict(file="Services/Implementations/DocumentService.Link.cs", method="LinkToSourceAsync",
         must=["DocumentLinkPolicy.LineMapBlockReason(", "GrnHasPostedJournalAsync(companyId, source.Id)"],
         before=[("DocumentLinkPolicy.LineMapBlockReason(", "child.RelatedDocumentId = source.Id")],
         why="ฝ่ายค้าน 2026-10-09: ใบแจ้งหนี้ซื้อ → ใบรับสินค้าต้องจับคู่ครบทุกบรรทัด (ล้าง 21240 ทั้งใบ) + ใบรับสินค้าต้องมี JE จริง (ไม่งั้นอนุมัติแล้วรับสต็อกซ้ำ)"),
    dict(file="Services/Implementations/DocumentService.Link.cs", method="UnlinkSourceAsync",
         must_lit=["FOR UPDATE"],
         before=[("_db.Database.ExecuteSqlRawAsync(", "DocumentLinkPolicy.UnlinkBlockReason(")],
         why="ฝ่ายค้าน 2026-10-09: ล็อกใบลูกก่อนตรวจสถานะ — กันอนุมัติพร้อมกัน"),
    dict(file="Services/Implementations/DocumentService.Link.cs", method="UnlinkSourceAsync",
         must=["DocumentLinkPolicy.UnlinkBlockReason("],
         why="ใบแจ้งหนี้ซื้อที่ลงบัญชีโดยล้าง 21240 แล้ว ห้ามถอดการผูก (ความคืบหน้า/3-way match จะโกหก)"),
    dict(file="Services/Implementations/DocumentService.Link.cs", method="GetLinkCandidatesAsync",
         must=["DocumentLinkPolicy.SourceTypesFor(child.DocumentType)", "d.CompanyId == companyId",
               "d.RelatedDocumentId == poParent", "d.DocumentType != DocumentType.GoodsReceiptNote"],
         why="รายการเสนอใช้ตัวกรองชุดเดียวกับด่านผูก (ชนิดตามใบลูก · ใบรับสินค้าอนุมัติแล้ว · ใบสั่งซื้อเดียวกัน)"),
    # ฝ่ายค้านชุดสอง (C-02/C-05): ทางเลี่ยงใบวางบิลรวม (แก้รายการ · โคลน) + ชั้นความลับของเอกสารลูก
    dict(file=DOC, method="UpdateDocumentAsync", must=["BillingNoteKind.IsRollup("],
         why="แก้รายการใบวางบิลรวมสร้างบรรทัดใหม่ ⇒ SourceDocumentId หาย ⇒ แปลงเป็นใบเสร็จได้ (รายได้ซ้ำ)"),
    dict(file="Controllers/DocumentCloneController.cs", method="Clone",
         must=["BillingNoteKind.IsRollup(", "CanViewAsync("], must_re=[r"Sensitivity:\s*src\.Sensitivity"],
         why="โคลนใบวางบิลรวมเป็นใบเสร็จ = รายได้ซ้ำ · ใบลับโคลนแล้วทุกคนเห็น"),
    dict(file=DOC, method="CreateSettlementReceiptAsync", must_re=[r"Sensitivity\s*=\s*invoice\.Sensitivity"],
         why="ใบเสร็จของใบลับต้องลับตาม (กฎ #4 A เอกสารลูกสืบทอด)"),
    # คำตัดสินข้อ 114 Q1: รายการประจำอนุมัติอัตโนมัติต้องเก็บข้อสังเกตที่ผ่าน แล้วแจ้งคน
    dict(file="Services/Implementations/RecurringTransactionService.cs", method="CreateDocumentFromTemplateAsync",
         must=["Section65TerApprovalWarnings.PassedNotice(", "RecurringApprovedWithNotes"],
         why="ข้อสังเกต §65 ตรีจากใบประจำต้องถึงคน (ไม่บล็อก ข้อ 110)"),
    # คำตัดสินข้อ 115 Q1: รอบเงินเดือนอยู่ในการนำส่งตามเวลาจ่ายจริง (ไม่ใช่ CreatedAt ตรง ๆ)
    dict(file=PAYROLL, method="LoadRecalculateLockEvidenceAsync", must=["RemittanceInclusion.RunCountedAt("],
         forbid=["(DateTime?)remittedAt : null, run.CreatedAt"],
         why="รอบที่สร้างก่อนแต่จ่ายหลังนำส่งถูกล็อกผิดเมื่อใช้ CreatedAt"),
    # ทีมตรวจงานค้าง D5: ส่วนบังคับ §86/4 · §86/9-10 ของ PDF ห้ามห่อ Safe (หายเงียบ = ใบกำกับขาดรายการ)
    dict(file="Services/Implementations/PdfGenerationService.DocumentRenderer.cs", method="RenderDocumentPdfNative",
         forbid=["Safe(() => ComposeHeaderAndTitle(", "Safe(() => ComposeContact(", "Safe(() => ComposeItemsTable(",
                 "Safe(() => ComposeSummary(", "Safe(() => ComposeAdjustmentRef(",
                 "SafeH(() => ComposeHeaderAndTitle(", "SafeH(() => ComposeContact("],
         why="D5: section บังคับตามกฎหมายล้มแล้วต้องไม่ได้ PDF ที่ขาดผู้ซื้อ/ตาราง/ยอด"),
    # ทีมตรวจงานค้าง C-03/C-09 (E-05): บิลบรรทัด PO ที่รับผ่าน GRN แล้ว — ตัวตัดสินเดียวทั้งตอนแปลงและตอนอนุมัติ (ทางสแกน/API)
    dict(file=DOC, method="ConvertCoreAsync", must=["PoBillBlockingGrnAsync("],
         why="C-09: PO ผสมต้องบิลบรรทัดที่ GRN ไม่ได้รับได้ · ตัวตัดสินเดียวกับตอนอนุมัติ"),
    dict(file=DOC, method="ApproveDocumentAsync#2", must=["PoBillBlockingGrnAsync("],
         why="C-03: ใบแจ้งหนี้ซื้อจากสแกน/API ที่ผูก PO ตรงข้ามด่านแปลง ⇒ สต็อกเข้าสองรอบ"),
    dict(file=DOC, method="PoBillBlockingGrnAsync", must=["PurchaseReceiptRoute.PoBillBlockedByGrn("],
         must_re=[r"d\.CompanyId\s*==\s*companyId"],
         why="C-03: ตัวโหลดเดียวของกติกา E-05"),
    # คำตัดสินข้อ 113 (ทีมตรวจงานค้าง 2026-10-08): ยกเลิกการนำส่ง + กลับ สปส. รายรอบห้ามแตะ JE ที่ใช้ร่วม
    dict(file="Services/Implementations/StatutoryRemittanceService.cs", method="VoidRemittanceAsync",
         must=["RemittanceVoidPolicy.BlockReason(", "ReverseJournalEntryAsync(", "AddChainedAuditLog("],
         must_lit=["FOR UPDATE"],
         must_re=[r"r\.SsoSettlementJournalEntryId\s*==\s*jeId", r"r\.CompanyId\s*==\s*companyId"],
         before=[("RemittanceVoidPolicy.BlockReason(", "ReverseJournalEntryAsync(")],
         why="กลับ JE ครั้งเดียว + ปลดทุกรอบที่ใช้ JE นี้ (ไม่ใช่รอบเดียว) · ภ.พ.36 ที่รับรู้ภาษีซื้อแล้วต้องถูกกันก่อนกลับ"),
    dict(file=PAYROLL, method="ReverseSsoSettlementAsync",
         must=["RemittanceVoidPolicy.SsoPerRunReverseBlock("],
         before=[("RemittanceVoidPolicy.SsoPerRunReverseBlock(", "ReverseJournalEntryAsync(")],
         why="JE นำส่งทั้งเดือนใช้ร่วมหลายรอบ — กลับรายรอบ = รอบอื่นค้างธงนำส่ง + กลับ JE ซ้ำ"),
    # คำตัดสินข้อ 114 Q2 (ทีมตรวจงานค้าง 2026-10-08): ยกเลิกเงินทดรอง — สิทธิ์ + ใบสำคัญจ่ายที่ผูก ผ่านตัวตัดสินเดียว
    dict(file="Services/Implementations/SalaryAdvanceService.cs", method="VoidAsync",
         must=["SalaryAdvanceVoidPolicy.CanVoid(", "SalaryAdvanceVoidPolicy.Decide(", "AddChainedAuditLog("],
         before=[("SalaryAdvanceVoidPolicy.CanVoid(", "SalaryAdvanceVoidPolicy.Decide("),
                 ("SalaryAdvanceVoidPolicy.Decide(", "DeleteDocumentAsync(")],
         why="เดิมยกเลิกได้ทั้งที่ใบสำคัญจ่ายลงบัญชีแล้ว (ลูกหนี้ค้าง) และสมาชิกคนใดก็ยกเลิกได้"),
    dict(file=PAYROLL, method="AddPayrollDetailAsync", must=["AddNewChild("], forbid=["run.Details.Add("],
         why="เพิ่มคนเข้ารอบ: Add สองทาง ⇒ ยอดรอบเบิ้ล · คอลเลกชันอย่างเดียว ⇒ 500 concurrency"),
    dict(file="Services/Implementations/TaxService.cs", method="FileTaxReportAsync",
         must=["SyncNextPeriodVatCreditAsync("], forbid=["Lines.Add("],
         before=[("SyncNextPeriodVatCreditAsync(", "_db.SaveChangesAsync(")],
         why="เครดิตภาษีซื้อยกไปบันทึกพร้อมการยื่น (เดิม catch {} กลืน 500 ทุกครั้ง ⇒ เครดิตไม่เคยยก)"),
    dict(file="Services/Implementations/TaxService.cs", method="SyncNextPeriodVatCreditAsync",
         must=["AddNewChild(", "DeclaredOrFiled(", "RecalcVatTotals("], forbid=["Lines.Add("],
         must_re=[r"r\.CompanyId\s*==\s*companyId", r"r\.Status\s*==\s*TaxReportStatus\.Draft"],
         why="ฝ่ายค้านรอบสาม: เครดิตยกไปต้องตามยอด/สถานะงวดนี้เสมอ (แก้ยอด · ถอนเมื่อปลดล็อก) · แตะเฉพาะงวดถัดไปที่ยังร่าง"),
    dict(file="Services/Implementations/TaxService.RdCompliance.cs", method="UnlockTaxFilingAsync",
         must=["SyncNextPeriodVatCreditAsync("], before=[("SyncNextPeriodVatCreditAsync(", "_db.SaveChangesAsync(")],
         why="ปลดล็อกงวด ⇒ ถอนเครดิตที่ยกไปงวดถัดไป (มิฉะนั้นยื่นใหม่ด้วยยอดใหม่แล้วงวดถัดไปค้างเครดิตเก่า)"),
    dict(file="Services/Implementations/TaxService.RdCompliance.cs", method="RejectAndReverseTaxReportAsync",
         must=["SyncNextPeriodVatCreditAsync("], before=[("SyncNextPeriodVatCreditAsync(", "_db.SaveChangesAsync(")],
         why="ถูกปฏิเสธ ⇒ ถอนเครดิตที่ยกไปงวดถัดไป"),
]

# ── 2026-10-14 ทีม F2 (PAYMENT_CHANNEL_AUDIT PC#3/#4/#5/#9/#14/#21 · ID#4/#6/#7): แหล่งเงินของเอกสาร/การชำระ → ผังบัญชี
#    ตัวตัดสินเดียว Helpers/DocumentMoneyChannel — เดิมลำดับเดียวกันถูกเขียน 4 ที่ และทุกที่ตกไปเงินสดเงียบ ๆ เมื่อธนาคารยังไม่ผูกผัง ──
_MONEY_WHY = ("PC#4 แหล่งเงินที่เลือกแล้วใช้ไม่ได้ต้องล้มดังพร้อมทางไปต่อผ่าน DocumentMoneyChannel — "
              "ห้ามกลับไปหา BankAccounts.LinkedAccountId เอง/ตกเงินสด 111 เงียบ ๆ/คืนก่อนลง JE")
BANKSVC = "Services/Implementations/BankService.cs"
TPLSVC = "Services/Implementations/DocumentTemplateService.cs"
PDFSVC = "Services/Implementations/PdfGenerationService.cs"
RULES += [
    dict(file=DOC, method="AutoPostToJournalAsync",
         must=["DocumentMoneyChannel.EvaluateAsync(", "UseMoneyAccount()", "DocumentMoneyChannel.ExpenseCategoryProblemAsync("],
         forbid=["moneyAccount != null", "bankAcc?.LinkedAccount"],
         why=_MONEY_WHY + " · ขาเงินทุกขาเรียก UseMoneyAccount() (ใบตั้งหนี้ที่ไม่ขยับเงินยังอนุมัติได้)"),
    dict(file=DOC, method="ResolvePaymentCashAccountAsync",
         must=["DocumentMoneyChannel.ResolvePostingAccountAsync(", "DocumentMoneyChannel.NoDefaultCashMessage("],
         forbid=["LinkedAccountId"],
         why=_MONEY_WHY),
    dict(file=DOC, method="CreatePaymentJournalAsync",
         must=["ResolvePaymentCashAccountAsync("],
         forbid=["if (cashAccount == null) return", "LinkedAccountId"],
         why=_MONEY_WHY + " · เดิม if (cashAccount == null) return; บันทึกการชำระโดยไม่มี JE"),
    dict(file=DOC, method="CreatePaymentAsync",
         must=["DocumentMoneyChannel.ForPayment(", "DocumentMoneyChannel.HasBothOverrides(", "ResolvePaymentCashAccountAsync("],
         must_re=[r"BankAccountId\s*=\s*payChannel\.BankAccountId", r"OverridePaymentAccountId\s*=\s*payChannel\.PaymentAccountId"],
         before=[("ResolvePaymentCashAccountAsync(", "_db.Payments.Add(")],
         forbid=["request.OverrideBankAccountId ?? doc.BankAccountId"],
         why="PC#5/#21 การชำระสืบทอดแหล่งเงินของเอกสารทั้งสองแบบ · เลือกผังตรง = ไม่ขยับยอดธนาคาร · ตรวจผังก่อนบันทึก"),
    dict(file=DOC, method="CreateMultiDocPaymentAsync",
         must=["DocumentMoneyChannel.ForPayment(", "DocumentMoneyChannel.HasBothOverrides(", "ResolvePaymentCashAccountAsync("],
         must_re=[r"OverridePaymentAccountId\s*=\s*payChannel\.PaymentAccountId",
                  r"OverridePaymentAccountId\s*=\s*payment\.OverridePaymentAccountId"],
         forbid=["request.OverrideBankAccountId ?? firstDoc.BankAccountId"],
         why="PC#5 เส้นจ่ายหลายใบเคยทิ้ง OverridePaymentAccountId ทั้งของคำขอและของเอกสาร (รวมการชำระย่อยต่อใบ)"),
    dict(file=DOC, method="CreateDocumentAsync",
         must=["ValidateDocumentMoneyHeaderAsync("],
         why="PC#4/ID#7 หัวเอกสาร BankAccountId/PaymentAccountId/ExpenseCategoryId ต้องตรวจตาราง+บริษัท+สถานะตอนบันทึก"),
    dict(file=DOC, method="UpdateDocumentAsync",
         must=["ValidateDocumentMoneyHeaderAsync("],
         why="PC#4/ID#7 แก้แหล่งเงิน/หมวดค่าใช้จ่ายต้องผ่านด่านเดียวกับตอนสร้าง"),
    dict(file=DOC, method="ValidateDocumentMoneyHeaderAsync",
         must=["DocumentMoneyChannel.EvaluateAsync(", "DocumentMoneyChannel.ExpenseCategoryProblemAsync("],
         must_re=[r"if\s*\(\s*!\s*d\.Ok\s*\)\s*throw\b"],
         why="PC#4/ID#7 ด่านหัวเอกสารต้องโยนเมื่อตัวตัดสินบอกว่าใช้ไม่ได้"),
    dict(file=DOC, method="ReclassifyPaymentSourceAsync",
         must=["DocumentMoneyChannel.EvaluateAsync(", "JournalEntryLines", "IsCashInflowDocAsync("],
         forbid=["Select(b => b.LinkedAccountId)"],
         why="ID#6 ผังแหล่งเงินเดิมต้องอ่านจาก JE ที่ลงไว้จริง ไม่ใช่ LinkedAccountId ปัจจุบันของธนาคาร"),
    dict(file=BANKSVC, method="CreateBankAccountAsync", must=["ValidateLinkedAccountAsync("],
         before=[("ValidateLinkedAccountAsync(", "_db.Set<BankAccount>().Add(")],
         why="PC#9/ID#16 เส้นสร้างบัญชีธนาคารต้องตรวจผังที่ผูกด้วยตัวเดียวกับเส้นแก้ไข"),
    dict(file=BANKSVC, method="UpdateBankAccountAsync", must=["ValidateLinkedAccountAsync("],
         why="PC#9/ID#16 เส้นแก้ไขบัญชีธนาคารใช้ตัวตรวจผังที่ผูกตัวเดียว"),
    dict(file=TPLSVC, method="CreateAsync", must=["ValidateDefaultPaymentAccountAsync("],
         why="PC#14/ID#4 บัญชีรับ/จ่ายเงินตั้งต้นของเทมเพลตต้องตรวจตาราง+บริษัท+สถานะ"),
    dict(file=TPLSVC, method="UpdateAsync", must=["ValidateDefaultPaymentAccountAsync("],
         why="PC#14/ID#4 บัญชีรับ/จ่ายเงินตั้งต้นของเทมเพลตต้องตรวจตาราง+บริษัท+สถานะ"),
    dict(file=TPLSVC, method="GetDefaultTemplateAsync", must=["DocumentMoneyChannel.ChannelValueForGlAsync("],
         why="PC#14 ฟอร์มเอกสารเติมแหล่งเงินจากค่าที่เซิร์ฟเวอร์แปลง (เดิมเทียบรหัสผังดิบกับ bank:/account: ⇒ ไม่เคยเติม)"),
    dict(file=PDFSVC, method="BuildProjectedGlAsync", must=["DocumentMoneyChannel.EvaluateAsync("],
         forbid=["BankGlAsync("],
         why="PC#15 พรีวิวการลงบัญชีต้องใช้ลำดับเดียวกับ JE จริง (เดิมเลือกธนาคารก่อน + เดาเงินสดเมื่อยังไม่ผูกผัง)"),
]

# ── ฝ่ายค้านรอบ 1–2 (2026-10-14 · PAYMENT_CHANNEL_AUDIT ส่วนที่ 3) — ด่านที่เพิ่ม/ผ่อน ต้องอยู่ที่จุดเรียกจริง ──
_ADV1 = "ฝ่ายค้านรอบ 1/2 (PAYMENT_CHANNEL_AUDIT ส่วนที่ 3)"
RECUR = "Services/Implementations/RecurringTransactionService.cs"
BANK_DEL = "Services/Implementations/BankService.Delete.cs"
BANK_IMP = "Services/Implementations/BankService.StatementImport.cs"
PICKED = "Helpers/PickedAccountGuard.cs"
RULES += [
    dict(file=DOC, method="CreateDocumentAsync", call_args=[("ValidateDocumentMoneyHeaderAsync(", "carriedMoneyHeader")],
         why=_ADV1 + " P1: ค่าที่ยกมาจากต้นทางต้องถึงตัวตรวจหัวเอกสาร (ไม่งั้นแปลง/คัดลอกใบที่ธนาคารปิดใช้ทีหลังไม่ได้)"),
    dict(file=DOC, method="ValidateDocumentMoneyHeaderAsync",
         must=["DocumentMoneyChannel.IsCarried(", "ThrowIfCarriedProblemAsync("],
         why=_ADV1 + " P1: ค่าที่ยกมา ⇒ ตรวจแค่ tenant · ค่าที่เลือกใหม่ ⇒ ตรวจเข้ม"),
    dict(file=DOC, method="ThrowIfCarriedProblemAsync", must=["DocumentMoneyChannel.CarriedProblemAsync("],
         must_re=[r"is\s*\{\s*\}\s*problem\s*\)\s*throw\b"],
         why=_ADV1 + " P1: ค่าที่ยกมาของบริษัทอื่นยังบล็อก (tenant isolation)"),
    dict(file=DOC, method="ConvertCoreAsync", must=["carriedMoneyHeader:", "CarriedMoneyHeader(source.BankAccountId"],
         why=_ADV1 + " P1: แปลงเอกสารส่งแหล่งเงินของใบต้นทางเป็นค่าที่ยกมา"),
    dict(file=CLONE, method="Clone", must=["carriedMoneyHeader:"],
         why=_ADV1 + " P1: คัดลอกเอกสารส่งแหล่งเงินของต้นแบบเป็นค่าที่ยกมา"),
    dict(file=LODGING_LIFE, method="ReissueFinalDocumentCoreAsync", must=["CarriedMoneyHeader(old.BankAccountId"],
         why=_ADV1 + " P1: ออกใบเช็คเอาต์แทนยกบัญชีรับเงินของใบเดิมเป็นค่าที่ยกมา"),
    dict(file=DOC, method="ReclassifyPaymentSourceAsync", must=["DocumentMoneyChannel.PickReclassifySource("],
         why=_ADV1 + " P2 (4d): ยอดที่ย้ายมาจากขาเงินใน JE ไม่ใช่ PaidAmount × อัตรา"),
    dict(file=DOC, method="TryRecognizeSalesWhtOnSettlementAsync", must=["OriginalArApAccountAsync("], must_lit=["[WHT-SETTLE]"],
         forbid=["_logger.LogWarning(ex"],
         why=_ADV1 + " รอบ 2 P2(b): ตัดลูกหนี้ผังที่ใบตั้งไว้ · ล้มต้องดังบนเอกสาร (ไม่ใช่ LogWarning อย่างเดียว)"),
    dict(file=RECUR, method="CreateDocumentFromTemplateAsync",
         must=["carriedMoneyHeader:", "RecurringRunFailure.AfterCreateNotice("],
         before=[("MarkRunAsync(", "RecurringRunFailure.AfterCreateNotice(")],
         why=_ADV1 + " P2 (3b): ขั้นหลังสร้างเอกสาร/นับงวดแล้วล้ม = เรื่องที่ต้องทำต่อ ไม่ใช่รอบล้ม (กันใบซ้ำ)"),
    dict(file=RECUR, method="ProcessDueRecurringTransactionsAsync",
         before=[("JobLock.RunExclusiveAsync(", "RecordRunFailureAsync(")],
         why=_ADV1 + " P2 (3a): บันทึกเหตุผลล้มภายใต้ล็อกต่อรายการ (ไม่ทับผลสำเร็จของปุ่ม \"สร้างทันที\")"),
    dict(file=RECUR, method="ValidateTemplateAsync",
         must=["DocumentMoneyChannel.EvaluateAsync(", "DocumentMoneyChannel.ExpenseCategoryProblemAsync(", "PostingAccountRule.Check("],
         why=_ADV1 + " P2 (3c) + รอบ 2 P2(d): แหล่งเงิน/บัญชีคุมของเทมเพลตตรวจด้วยตัวตัดสินเดียวกับหน้าเอกสาร/สมุดรายวัน"),
    dict(file=BANK_DEL, method="RefreshAccountBalance",
         must=["BankBalanceAfterDelete.Resolve(", "!t.BalanceAfterUnknown", "t.CompanyId == companyId"],
         forbid=["?? 0m"],
         why=_ADV1 + " P1: ลบบรรทัด statement ที่ไม่มียอดคงเหลือแล้วการ์ดบัญชีกลายเป็น 0"),
    dict(file=BANK_IMP, method="ImportParsedStatementRowsAsync",
         must=["UndoTracked(", "BalanceAfterUnknown = knownBalance is null"],
         why=_ADV1 + " P1/P2: ยอดที่ไม่รู้ = ไม่รู้ · ล้มแล้วถอดของออกจาก tracker · ไฟล์ต้นทางเขียนหลัง commit"),
    dict(file=INTEG, method="ProcessInvoiceAsync",
         must=["DocumentMoneyChannel.EvaluateAsync(", "MoneyChannelCheck.Post"], must_lit=['log.Status = "PartialSuccess"'],
         why=_ADV1 + " P2 (4a): ขายเงินสดตรวจบัญชีรับเงินด้วยตัวตัดสินตอนลงบัญชีก่อนออกเลข · ถอยไปตั้งหนี้ต้องล้มดัง"),
    dict(file=OCR, method="CreateDocumentFromScanCoreAsync", must=["DocumentMoneyChannel.IsPostableGl("],
         why=_ADV1 + " P2 (4b): OCR เติมเฉพาะผังที่ลงรายการได้ (กฎเหล็ก #3 ห้ามล้มตอนอนุมัติ)"),
    dict(file=TPLSVC, method="UpdateAsync", must=["previousDefaultPaymentAccountId"],
         why=_ADV1 + " P2 (4c): ตรวจบัญชีตั้งต้นของเทมเพลตเฉพาะเมื่อเปลี่ยน"),
    dict(file=PICKED, method="AccountProblem", must=["PostingAccountRule.Check("], forbid=["Level < 4"],
         why=_ADV1 + " รอบ 2 P1-1: กติกา \"ลงรายการได้\" มีเจ้าของเดียว (PostingAccountRule — มีบัญชีย่อย ไม่ใช่ระดับ)"),
    dict(file=PICKED, method="ResolveBankGlAsync", must=["TryResolveBankGlAsync("],
         why=_ADV1 + " รอบ 2 P1-1: บัญชีธนาคาร → ผังที่ผูก ผ่าน MoneyAccountDirectory ตัวเดียว"),
    dict(file=PICKED, method="ResolveMoneyChannelAsync", must=["SourceProblem("],
         why=_ADV1 + " รอบ 2 P1-1: ผังเงินตรงตัดสินด้วยตัวเดียวกับรายการช่องทางเงิน"),
    dict(file="Controllers/ScheduledReportController.cs", method="SendNow", must=["AdvisoryLockKey.ScheduledReportSend"],
         forbid=["AdvisoryLockKey.BackgroundJob"],
         why=_ADV1 + " รอบ 2 P1-2: ปุ่มส่งทันทีล็อกต่อรายงาน (เดิมถือล็อกรวม ⇒ รอบรายชั่วโมงข้ามทุกบริษัท)"),
    dict(file="Services/Implementations/ScheduledReportDispatcher.cs", method="RunOnceAsync", must=["AdvisoryLockKey.ScheduledReportSend", "ReloadAsync("],
         why=_ADV1 + " รอบ 2 P1-2: รอบตามเวลาล็อกต่อรายงาน + อ่านใหม่ใต้ล็อก (ไม่ส่งซ้ำกับปุ่ม)"),
    dict(file="Services/Implementations/PayrollService.cs", method="ResolvePayrollPaymentSourcesAsync", must=["requireExplicitChoice: true"],
         why=_ADV1 + " รอบ 2 P2(a): เงินเดือนไม่ตกไปเงินสด/เงินสดย่อยเงียบ ๆ"),
    dict(file=LODGING_LIFE, method="ConfirmCoreAsync", must=["LodgingPaymentBank.Problem("],
         why=_ADV1 + " รอบ 2 P2(g): โอน/พร้อมเพย์ต้องมีบัญชีธนาคาร — เซิร์ฟเวอร์บังคับเอง"),
    dict(file=LODGING_LIFE, method="CheckOutCoreAsync", must=["LodgingPaymentBank.Problem("],
         why=_ADV1 + " รอบ 2 P2(g): โอน/พร้อมเพย์ต้องมีบัญชีธนาคาร — เซิร์ฟเวอร์บังคับเอง"),
    dict(file="Services/Implementations/ApprovalService.cs", method="ReassignApproverAsync", must=["MayApproveRequestDocumentAsync(", "a.Id != action.Id"],
         why=_ADV1 + " รอบ 2 P2(f): โอนผู้อนุมัติขั้นสุดท้ายที่ไม่มีสิทธิ์ได้ · คนใหม่ห้ามเป็นผู้อนุมัติขั้นอื่น"),
    dict(file="Services/Implementations/AccountingService.cs", method="ValidateJournalReferencesAsync", must=["IgnoreQueryFilters()"],
         why=_ADV1 + " รอบ 2 P2(d): ทางอัตโนมัติตรวจแค่ tenant (ของที่ลบทีหลังไม่ทำให้รายการประจำ/แก้ JE ล้ม)"),
]

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
    # ฝ่ายค้านด่านไฟล์ซ้ำ D1 (2026-10-09): คืนโควตาผ่านตัวตัดสินเดียว — ห้ามคืนตามธง IsDuplicate (ไฟล์ซ้ำที่อ่านใหม่ด้วย Azure ต้องเสียโควตา)
    dict(file="Controllers/OcrController.cs", method="UploadAndScan",
         must=["OcrQuotaRefundRule.ShouldRefund(result.ScanStatus, result.OcrEngine)"],
         forbid=["result.IsDuplicate ||"],
         why="คืนโควตาเฉพาะเมื่อไม่ได้ใช้ engine จริง (Helpers/OcrQuotaRefundRule) — สองทางเข้าใช้กติกาเดียว"),
    dict(file="Services/Implementations/LineBotService.cs", method="HandleImageAsync",
         must=["OcrQuotaRefundRule.ShouldRefund(result.ScanStatus, result.OcrEngine)"],
         forbid=["result.IsDuplicate ||"],
         why="คืนโควตาเฉพาะเมื่อไม่ได้ใช้ engine จริง (Helpers/OcrQuotaRefundRule) — สองทางเข้าใช้กติกาเดียว"),
    dict(file="Services/Implementations/OcrService.cs", method="FindDuplicateDocumentWarningAsync",
         must=["prior.DuplicateOfScanId is Guid rootId", "r.Id == rootId"],
         why="ฝ่ายค้าน D2: ต้นฉบับอาจเป็นแถวซ้ำที่อ่านใหม่ (ไม่สร้างเอกสาร) — ต้องตามไปอีกชั้นถึงต้นฉบับที่สร้างเอกสารไว้"),
    # ทีมตรวจงานค้าง 2026-10-08 (คำตัดสินเจ้าของ "รายงานอ่านอย่างเดียวก่อน"): รายงานทั้งบริษัทต้องกรองบริษัททุก query + ห้ามเขียนอะไร
    dict(file="Services/Implementations/DataIntegrityReportService.cs", method="GetAsync",
         must=["o.CompanyId == companyId", "l.Document.CompanyId == companyId", "d.CompanyId == companyId",
               "pi.CompanyId == companyId", "po.CompanyId == companyId", "g.CompanyId == companyId",
               "r.CompanyId == companyId", "AsNoTracking()"],
         forbid=["SaveChanges", ".Remove(", ".Update(", "ExecuteUpdate", "ExecuteDelete"],
         why="กฎ M tenant isolation ทุก query ย่อย · รายงานนี้อ่านอย่างเดียว ซ่อมรายตัวหลังผู้ทำบัญชีอนุมัติเท่านั้น"),
    # ── 2026-10-14 ทีม F3 (PAYMENT_CHANNEL_AUDIT PC#6/#11/#12/#17/#22 · ID#13/#15/#19/#23/#24): แหล่งเงิน + ผังที่ลงรายการได้ ──
    dict(file=PAYROLL, method="ProcessPaymentAsync",
         must=["ResolvePayrollPaymentSourcesAsync(", "paySources.RowProblem(", "paySources.DefaultProblem"],
         forbid_lit=['"11122"'],
         why="PC#12/ID#15 แหล่งจ่ายเงินเดือนผ่านตัวตัดสินเดียวกับหน้ารอบ · รหัสที่ใช้ไม่ได้ล้มดัง ไม่สลับเงียบ · ห้ามกลับไปใช้บัญชีคุม 11122"),
    dict(file=PAYROLL, method="GetPayrollRunAsync",
         must=["ResolvePayrollPaymentSourcesAsync(", "paySources.RowProblem("],
         why="PC#22 หน้ารอบเห็นคำเตือนแหล่งจ่ายชุดเดียวกับตอนจ่าย"),
    dict(file=PAYROLL, method="SetEmployeePaymentAccountAsync",
         must=["CheckNetPaymentAccountAsync("],
         must_re=[r"if\s*\(\s*problem\s*!=\s*null\s*\)\s*throw\b"],
         why="PC#11 แหล่งจ่ายรายคน: บล็อกเฉพาะลงรายการไม่ได้ · ไม่ใช่ผังเงิน = เตือน (กติกาเดียว MoneyAccountRule)"),
    dict(file=PAYROLL, method="SettleSocialSecurityAsync",
         must=["ResolveSourceGlAsync("],
         forbid=["b.LinkedAccountId"],
         why="PC#17/ID#19 นำส่ง สปส. ใช้ตัวหาแหล่งเงินตัวเดียว (แยก ไม่พบ/ปิดใช้/ยังไม่ผูก) — ห้ามสำเนาที่สอง"),
    dict(file="Services/Implementations/StatutoryRemittanceService.cs", method="ResolveBankGlAsync",
         must=["ResolveSourceGlAsync("], forbid=["b.LinkedAccountId", "DefaultPaymentAccountId"],
         why="PC#6/#17 นำส่งภาษีใช้ตัวหาแหล่งเงินตัวเดียว · ค่าตั้งบริษัทต้องถูกตรวจ ไม่อ่านดิบ"),
    dict(file="Services/Implementations/SettingsService.cs", method="UpdateSettingsAsync",
         must=["SourceProblem(", "MoneyAccountRule.NormalizeSetting("],
         why="PC#6/ID#22 แหล่งเงินเริ่มต้นตรวจก่อนเก็บ · รหัสผังเงินของบริษัทผ่านตัวปรับรูปเดียว"),
    dict(file="Services/Implementations/AccountingService.cs", method="GetPaymentChannelAccountsAsync",
         must=["GetPaymentChannelsAsync("], forbid_lit=['"1112"', '"2123"'],
         why="PC#11 รายการช่องทางเงินมาจากกติกาเดียว MoneyAccountRule — ห้ามกลับไปเขียน prefix เอง"),
    dict(file="Controllers/AccountingController.cs", method="CreateJournalEntry",
         before=[("ValidateManualJournalAsync(", "CreateJournalEntryAsync(")],
         why="ID#24 ใบสำคัญที่บันทึกด้วยมือผ่านด่านเข้ม (ผังเปิดใช้/ไม่ใช่บัญชีคุม + สาขา/มิติ/โครงการของบริษัท) ก่อนสร้าง"),
    dict(file="Services/Implementations/AccountingService.cs", method="UpdateJournalEntryAsync",
         must=["strictAccounts: true"],
         why="ID#24 แก้ใบสำคัญ (ทางมือเท่านั้น) ใช้ด่านเข้ม"),
    dict(file="Services/Implementations/FixedAssetService.cs", method="CreateAsync",
         before=[("EnsurePostingAccountsAsync(", "_db.FixedAssets.Add(")],
         why="ID#13 ผังของสินทรัพย์ (งานค่าเสื่อมรายเดือนลงสมุดด้วย id เหล่านี้) ต้องลงรายการได้ก่อนเก็บ"),
    dict(file=INTEG, method="ProcessDailySummaryAsync",
         must=["unmapped.Count > 0", "LoadPostingFactsAsync("],
         why="ID#14 สรุปรายวัน: หมวดที่ไม่จับคู่ = ปฏิเสธทั้งชุด (ไม่ลงบางส่วนแล้วตอบสำเร็จ) · ผังที่จับคู่ต้องยังลงรายการได้"),
    dict(file=INTEG, method="CreateMappingAsync",
         must=["EnsureIntegrationOwnedAsync(", "EnsurePostingAccountsAsync("],
         why="ID#13 การจับคู่ผัง: integration + ผังต้องเป็นของบริษัทนี้"),
    dict(file="Controllers/PaymentSettingsController.cs", method="Save",
         before=[("EnsurePostingAccountsAsync(", "cfg.ClearingAccountId =")],
         why="ID#13 ผังพักเงิน/ค่าธรรมเนียม gateway ต้องลงรายการได้ก่อนเก็บ"),
    dict(file=LODGING_LIFE, method="ConfirmCoreAsync",
         must=["ResolveBankGlAsync("],
         why="PC#8 บัญชีธนาคารที่รับมัดจำต้องของบริษัทนี้/เปิดใช้/ผูกผัง ก่อนออกใบ"),
    dict(file=LODGING_LIFE, method="ResolveRefundMoneyAccountAsync",
         must=["TryResolveBankGlAsync("], forbid=["x.LinkedAccountId"],
         why="ID#19 คืนเงินแขก: แยก ไม่พบ/ปิดใช้/ยังไม่ผูก ด้วยตัวหาตัวเดียว"),
    dict(file=DOC, method="FindAccountAsync",
         must=["PinnedContactAccountAsync("],
         why="ID#23 ผังที่ผู้ติดต่อปักไว้ใช้ไม่ได้ = ล้มดังพร้อมทางไปต่อ ไม่ตกไปผังมาตรฐานเงียบ ๆ"),
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
    # รอบ 205 ทีม B: เอกสาร/รับ-จ่ายชำระ/สินทรัพย์จากทางเดิมส่งต่อปลายทางของทีม D (กติกาของ DocumentImportTarget/PaymentImportTarget/
    # FixedAssetImportTarget อยู่ในชุด _ETL_D) — ImportExportService ไม่มีตัวเขียนของสามชนิดนี้แล้ว
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
    # รอบ 2026-10-14 ทีม F4 (ID#9): มือถือเดินเมธอดเดียวกับเว็บ (SubmitActionAsync หยุดให้กดรับทราบคำเตือนก่อนบันทึกผล — ล็อกที่ APPROVAL ข้างบน) ·
    # ห้ามกลับไปเขียนกติกาเองชุดที่สอง (เพิ่มแถวผลอนุมัติเอง · อนุมัติเอกสารเอง · ตั้งสถานะคำขอเอง)
    (MOBILE, "QuickApproveAsync",
     ["SubmitActionAsync(", "DocumentApprovalWarningsException"], [],
     ["acknowledgeWarnings: true", "_db.ApprovalActions.Add(", "ApproveDocumentAsync(", "approvalRequest.OverallStatus", "CurrentStep++"],
     "C5 + ID#9: มือถือใช้เจ้าของกติกาอนุมัติตัวเดียวกับเว็บ (ผู้อนุมัติของขั้น · สิทธิ์ · คำเตือน · แก้แถวที่รออยู่)"),
    # ── รอบ 193 ทีม L2 หลังฝ่ายค้าน (review193-L2.md §D: เทสต์เรียกแค่ helper — ถอดการแก้ใน service แล้วยังเขียว) ──
    # รอบ 201 ทีม IN (A-IN5): ตัวสร้างรายการ/ใบร่าง/การใช้มัดจำ แยกเป็นเมธอดที่เส้นเช็คเอาต์และเส้นออกใบใหม่ใช้ร่วม ⇒ ย้ายจุดล็อก
    # ChargeVatRate → BuildFinalInvoiceLinesAsync · CreateDocumentAsync → UpsertFinalDraftAsync (ลำดับ "วางแผนก่อนออกเลข" ล็อกที่จุดเรียก)
    (LODGING_LIFE, "CheckOutCoreAsync",
     ["LoadDepositSnapshotsAsync(", "LodgingDepositSettlement.PlanCheckout(", "DocumentService.PreviewTotals(",
      "BuildFinalInvoiceLinesAsync(", "ResumeCheckOutAsync(", "SettleCheckOutAsync(",
      "DepositBaseDeducted: depositPlan.BaseDeducted"],
     [("LodgingDepositSettlement.PlanCheckout(", "UpsertFinalDraftAsync("),
      ("FindOrCreateContactAsync(", "AddNewChild(r.Charges"),
      ("_docService.ApproveDocumentAsync(", "AddNewChild(r.Charges")],
     ["DepositAppliedDrivesJournal", "r.FinalDocumentId != null", "BillDiscountAmount: depositPlan"],
     "R3-1 ฐานมัดจำลงช่องของตัวเอง (ห้ามใส่ช่องส่วนลดการค้า) · C2/C5 วางแผนใช้มัดจำ (มัดจำเกินยอด = ค้างคืน) ก่อนออกเลขใบ · ด่าน/สร้างผู้ติดต่อก่อนผูกค่าเสียหาย · "
     "ใบเครดิตห้ามใช้ธงขับ JE (P0-1) · ออกใบแล้วกดซ้ำ = ทำต่อ ไม่ throw"),
    (LODGING_LIFE, "SettleCheckOutAsync",
     ["ApplyFinalDepositPlanAsync(", "r.RefundAmount = plan.ExcessGross", "r.RefundBaselineGross ="],
     [("ApplyFinalDepositPlanAsync(", "r.RefundAmount = plan.ExcessGross")], [],
     "C4/N1 รับรู้เฉพาะส่วนที่อนุมัติยังไม่ได้รับรู้ (ห้ามซ้ำ) · ผูกใบสุดท้าย · ตัดชำระไม่เกินยอดใบ · ส่วนเกิน = ค้างคืน + จุดตั้งยอด (N3)"),
    # รอบ 201 ทีม IN (A-IN5): ตัวรับรู้/ตัดชำระมัดจำย้ายจาก SettleCheckOutAsync คำต่อคำ (ใช้ร่วมกับ ReissueFinalDocumentAsync)
    (LODGING_LIFE, "ApplyFinalDepositPlanAsync",
     ["RealizedForFinalByDepositAsync(", "new RealizeDepositRequest(left, DateTime.UtcNow, prop.RoomRevenueAccountCode, finalId)",
      "Math.Min(a.Gross, finalDoc.BalanceDue)"],
     [("RealizedForFinalByDepositAsync(", "RealizeDepositAsync(")], ["ForfeitAs:"],
     "C4/N1 รับรู้เฉพาะส่วนที่อนุมัติยังไม่ได้รับรู้ (ห้ามซ้ำ) · ผูกใบสุดท้าย · ตัดชำระไม่เกินยอดใบ (ตัวเดียวของเช็คเอาต์และออกใบใหม่ · รอบ 201)"),
    # รอบ 201 ทีม IN (A-IN5): บรรทัด folio ผ่านด่าน §90/2 ในตัวสร้างรายการตัวเดียว
    (LODGING_LIFE, "BuildFinalInvoiceLinesAsync",
     ["LodgingPricingEngine.ChargeVatRate(", "prop.RoomRevenueAccountCode", "prop.ServiceChargeAccountCode"],
     [], [],
     "C8: folio ของบริษัทไม่จด VAT = อัตรา 0 · ค่าห้อง/service charge ลงผังของที่พัก — ตัวสร้างรายการใบสุดท้ายตัวเดียว (รอบ 201 A-IN5)"),
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
    (LODGING_LIFE, "ConfirmCoreAsync",   # รอบ 202 ฝ่ายค้าน P2-1: ตัวเส้นย้ายเข้า ConfirmCoreAsync (ConfirmAsync = ล็อกต่อการจอง)
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
                   "HtmlEncode(bankTextForLang)", "HtmlEncode(copyLabel.Watermark)",
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
    # รอบ 201 ทีม TX (A-TX1): การคำนวณย้ายจาก ApplySection65TerAsync ไป EvaluateSection65TerAsync (ตัวประเมินเดียวของธุรกรรม + คำเตือน)
    dict(file=DOC, method="EvaluateSection65TerAsync",
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
    # ทีม W1d (ฝ่ายค้าน W1c P2-4/P2-5): ด่านไฟล์นำเข้าของผู้หัก (เลขผู้หัก · สาขา 4 หลัก) ตัวเดียว · พรีวิวไม่ถูกกั้น · ไฟล์แนบรอบเงินเดือนที่ถูกกั้นต้องดัง
    dict(file="Services/Implementations/TaxFilingExportService.cs", method="Pnd1Result",
         must=["Pnd1FileFormat.ImportFileBlocker("],
         must_re=[r"!\s*previewOnly\s*&&\s*importBlocker\s*!=\s*null\s*\)\s*throw\s+importBlocker\b"],
         why="W1d P2-4: ไฟล์นำเข้าถูกกั้นด้วยด่านเดียว · พรีวิว/รายงานตรวจทานแสดงเป็นคำเตือน (ไม่ใช่ล้มทั้งหน้า)"),
    dict(file="Controllers/TaxFilingExportController.cs", method="Preview",
         call_args=[("_exportService.ExportPnd1Async(", "previewOnly: true"), ("_exportService.ExportPnd1kAsync(", "previewOnly: true")],
         why="W1d P2-4: action พรีวิวต้องขอแบบ previewOnly — เดิมไม่ระบุ ⇒ txt ⇒ ล้มด้วย PND1-WITHHOLDER-TAXID"),
    dict(file=PAYROLL, method="AutoGenerateFilingsAsync",
         must=["PayrollFilingAttachmentNotice.Pnd1Missing(", "PayrollFilingAttachmentNotice.Sso110Missing(",
               "PayrollFilingAttachmentNotice.Combine(", "FilingAttachmentWarning = warning"],
         why="W1d P2-5: ไฟล์แนบที่สร้างไม่ได้ต้องประทับบนรอบ (แบนเนอร์) + แจ้งเตือน — เดิม LogWarning อย่างเดียว"),
    dict(file=PAYROLL, method="ProcessPaymentAsync",
         must=["PayrollFilingAttachmentNotice.PredictAtPayment("],
         must_re=[r"resp\s*=\s*resp\s+with\s*\{\s*Notice\s*=\s*notice"],
         why="W1d P2-5: คำตอบปุ่มจ่ายบอกทันทีว่าไฟล์แนบ ภ.ง.ด.1 จะถูกกั้น (งานสร้างไฟล์รันหลังตอบ)"),
    dict(file=PAYROLL, method="MapToPayrollRunResponse", must=["FilingAttachmentWarning: r.FilingAttachmentWarning"],
         why="W1d P2-5: เก็บแล้วต้อง echo — หน้ารอบเงินเดือนอ่านแบนเนอร์จากคำตอบนี้"),
    dict(file="Controllers/PayrollController.cs", method="GetSso", must=["CanViewPiiAsync("],
         call_args=[("_service.GenerateSsoReportAsync(", "includePii")],
         why="G2-05: ด่าน PII ของรายงาน ปกส."),
    dict(file="Controllers/PayrollController.cs", method="SsoLateFeePreview",
         must=["CheckPayrollAccessAsync(", "SsoLateFee.Compute("],
         why="D-04: หน้าเว็บ preview เงินเพิ่ม สปส. ด้วยสูตรเดียวกับตอนลงบัญชี"),
    dict(file=PAYROLL, method="ComputeSsoLateFee", must=["SsoLateFee.Compute("], forbid=["0.02m"],
         why="D-04: สูตรเงินเพิ่ม §49 อยู่ที่ Helpers/SsoLateFee ตัวเดียว (ห้ามสำเนา)"),
    dict(file=PAYROLL, method="CalculatePayrollAsync",
         must=["PayrollWithholdingTax.PriorYtd(", "PayrollIncomeBase.PvdContribution("],
         must_re=[r"var\s+ytdIncome\s*=\s*cumulativeTaxable\b"],
         forbid=["emp.BaseSalary * emp.ProvidentFundEmployeePercent"],
         why="D-09 ฐานภาษีสะสมใช้ฐานภาษีของงวดก่อน (ผ่าน PayrollWithholdingTax.PriorYtd ตัวเดียว · รอบ 201) · D-10 PVD จากเงินเดือนที่จ่ายจริง + ปัดเศษ"),
    dict(file="Helpers/PayrollWithholdingTax.cs", method="PriorYtd",
         must=["PayrollIncomeBase.PriorTaxBase("],
         why="D-09 (ย้ายมารอบ 201 PR2): ฐานภาษีสะสม = ฐานภาษีของงวดก่อน ไม่ใช่ gross ที่รวมสวัสดิการยกเว้น"),
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
    dict(file=APPROVAL, method="ValidateRuleAsync", must=["ApprovalRuleValidation.Problem(", "CompanyMembership.ActiveMemberIdsAsync(_db, companyId"],
         why="G2-09 + ฝ่ายค้าน F4: รายชื่อสมาชิกกรองบริษัทนี้ ผ่านนิยามสมาชิกตัวเดียวกับตอนส่งคำขอ (ตัดบัญชีที่ปิด/ระงับ)"),
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
    # รอบ 201 ทีม PL (A-PL1 · คำตัดสินข้อ 32): ประทับย้ายจาก AddChainedAuditLog ไปที่ SaveChanges ภายใต้ล็อกของบริษัท —
    # การอ่านปลาย chain ตอน Add อยู่นอกล็อก (คำขอพร้อมกัน = แตกกิ่ง) ⇒ ห้ามประทับที่ Add อีก
    dict(file=AUDIT_CTX, method="AddChainedAuditLog",
         must=["AuditLogs.Add("], forbid=["ApplyAuditHashChain(", "AuditHashChain.Seal("],
         why="A-PL1 Add ไม่ประทับ — ประทับใน SaveChanges หลังได้ล็อก (ประทับนอกล็อก = แตกกิ่งเมื่อคำขอพร้อมกัน)"),
    # รอบ 201 ทีม PL หลังฝ่ายค้าน (PL-X1..X5 · คำตัดสินข้อ 104): ประทับ "ตอน commit" — SaveChanges ในธุรกรรมของผู้เรียกห้ามล็อก/ประทับ (เก็บไว้) ·
    # ธุรกรรมที่เปิดเองบันทึกข้อมูลหลักแบบยังไม่ Accept แล้ว commit (interceptor ประทับ) · ห้าม AddRange แถว audit ผ่าน ChangeTracker บนเส้น Npgsql
    dict(file=AUDIT_CTX, method="SaveChangesAsync",
         must=["DetachPendingAuditRows(", "_deferredAudit.Add(", "BeginTransactionAsync(", "acceptAllChangesOnSuccess: false",
               "ChangeTracker.AcceptAllChanges(", "DropDeferredAudit(", "RestorePendingAuditRows("],
         before=[("DetachPendingAuditRows(", "base.SaveChangesAsync("), ("ownTx.CommitAsync(", "ChangeTracker.AcceptAllChanges(")],
         forbid=["LockAuditChainAsync(", "LockAuditChain("],
         why="PL-X2/X3/X4 ห้ามล็อก audit ระหว่างธุรกรรม (วนรอกับเลข JE/ล็อกสินค้า · ถือข้าม HTTP) — ประทับตอน commit · PL-X5 ล้มแล้ว ChangeTracker/แถว audit คืนสภาพ"),
    dict(file=AUDIT_CTX, method="SaveChanges",
         must=["DetachPendingAuditRows(", "_deferredAudit.Add(", "BeginTransaction(", "acceptAllChangesOnSuccess: false",
               "ChangeTracker.AcceptAllChanges(", "DropDeferredAudit(", "RestorePendingAuditRows("],
         before=[("DetachPendingAuditRows(", "base.SaveChanges("), ("ownTx.Commit(", "ChangeTracker.AcceptAllChanges(")],
         forbid=["LockAuditChainAsync(", "LockAuditChain("],
         why="เส้น sync ต้องประทับตอน commit เหมือนเส้น async"),
    dict(file=AUDIT_CTX, method="SealDeferredAuditAtCommitAsync",
         must=["TakeDeferredAudit(", "AuditChainScope.IsolationBlockReason(isolation)", "AuditChainScope.Normalize(", "AuditLockTimeoutSql",
               "LockAuditChainAsync(", "ApplyAuditHashChain(", "AuditInsertBatches(rows)"],
         before=[("AuditChainScope.IsolationBlockReason(", "LockAuditChainAsync("),
                 ("AuditChainScope.Normalize(", "LockAuditChainAsync("), ("AuditLockTimeoutSql", "LockAuditChainAsync("),
                 ("LockAuditChainAsync(", "ApplyAuditHashChain(")],
         forbid=["AuditLogs.AddRange(", "SaveChanges", "foreach (var row in rows)"],
         why="PL-X1 แถวลูกได้บริษัทของ batch ก่อนล็อก · lock_timeout · ล็อกก่อนอ่านปลาย chain · INSERT ตรง (ห้าม flush ChangeTracker ของผู้เรียกตอน commit)"),
    dict(file=AUDIT_CTX, method="SealDeferredAuditAtCommit",
         must=["TakeDeferredAudit(", "AuditChainScope.IsolationBlockReason(isolation)", "AuditChainScope.Normalize(", "AuditLockTimeoutSql",
               "LockAuditChain(", "ApplyAuditHashChain(", "AuditInsertBatches(rows)"],
         before=[("AuditChainScope.IsolationBlockReason(", "LockAuditChain("), ("LockAuditChain(", "ApplyAuditHashChain(")],
         forbid=["AuditLogs.AddRange(", "SaveChanges", "foreach (var row in rows)"],
         why="เส้น sync ของตัวประทับตอน commit"),
    dict(file=AUDIT_CTX, method="OnConfiguring",
         must=["AuditChainCommitInterceptor.Instance"],
         why="ข้อ 104 ตัวประทับตอน commit ต้องลงทะเบียนทุกทางที่สร้าง context — ไม่มี = แถว audit ในธุรกรรมของผู้เรียกหายเงียบ"),
    dict(file=AUDIT_CTX, method="AuditInsertBatches",
         must=["AuditChainScope.InsertSql(", "AuditChainScope.InsertBatchRows"],
         why="ฝ่ายค้านรอบสาม P2-2→P2-1: INSERT หลายแถวต่อคำสั่ง (ลด round-trip ขณะถือล็อก audit) · รูป SQL จากตัวสร้างเดียว"),
    dict(file="Data/AuditChainCommitInterceptor.cs", method="TransactionCommittingAsync",
         must=["SealDeferredAuditAtCommitAsync("],
         call_args=[("SealDeferredAuditAtCommitAsync(", "transaction.IsolationLevel")],
         why="ข้อ 104 ประทับก่อน commit จริง"),
    dict(file="Data/AuditChainCommitInterceptor.cs", method="TransactionCommitting",
         must=["SealDeferredAuditAtCommit("],
         call_args=[("SealDeferredAuditAtCommit(", "transaction.IsolationLevel")],
         why="ข้อ 104 เส้น sync"),
    dict(file="Data/AuditChainCommitInterceptor.cs", method="TransactionRolledBackAsync",
         must=["DropDeferredAudit("],
         why="rollback ⇒ ทิ้งแถวที่รอ (ห้ามประทับร่องรอยของสิ่งที่ไม่เกิด)"),
    dict(file=AUDIT_CTX, method="AuditChainLockKeys",
         must=["AdvisoryLockKey.For(", "AdvisoryLockKey.AuditChain", "OrderBy("],
         why="A-PL1 คีย์ล็อกคงที่ข้ามเครื่อง (ไม่ใช่ HashCode) · เรียงบริษัทก่อนล็อก (กัน deadlock ระหว่างคำขอที่เขียนหลายบริษัท)"),
    dict(file=AUDIT_CTX, method="LockAuditChainAsync",
         must=["AuditChainLockKeys("], must_lit=["pg_advisory_xact_lock"],
         why="A-PL1 ล็อกระดับธุรกรรม (ถือจน commit — เรียกเฉพาะตอน commit · ข้อ 104) — session lock/try-lock ไม่ serialize การประทับ"),
    dict(file=AUDIT_CTX, method="LockAuditChain",
         must=["AuditChainLockKeys("], must_lit=["pg_advisory_xact_lock"],
         why="A-PL1 เส้น sync ล็อกเหมือนเส้น async"),
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
    # รอบ 201 ฝ่ายค้าน X7: ตัวเช็คเอาต์จริงย้ายเข้า CheckOutCoreAsync (CheckOutAsync = ห่อล็อกต่อการจอง) · ออกใบใหม่ใช้ RoomDeposits เช่นกัน
    for m in ("CheckOutCoreAsync", "ResumeCheckOutAsync", "SettleCheckOutAsync", "RecordRefundPaidCoreAsync",
              "ReissueFinalDocumentCoreAsync")
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
         must=["DepositKindForAsync(", "DepositDocumentShaping.Apply(", "DepositKindId: covered ? null : kind.KindId"],
         why="รอบ 194 S4: ใบมัดจำค่าห้องตรึงประเภทของที่พัก (ตัวตัดสินตัวเดียว) และจัดรูปด้วยตัวจัดรูปตัวเดียว · ข้อยกเว้นเดียว (ข้อ 157): ภาษีอยู่ในใบกำกับล่วงหน้าแล้ว"),
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
    # รอบ 201 ทีม IN (A-IN5): ใบร่างย้ายเข้า UpsertFinalDraftAsync (ใช้ร่วมกับออกใบใหม่) · "ใบร่างก่อนอนุมัติ" ล็อกที่จุดเรียก (บล็อกทีม IN)
    dict(file=LODGING_LIFE, method="UpsertFinalDraftAsync",
         must=["LodgingCheckoutDraft.LeftoverOf(", "LodgingCheckoutDraft.Plan(", "UpdateDocumentAsync("],
         before=[("LodgingCheckoutDraft.LeftoverOf(", "CreateDocumentAsync("), ("LodgingCheckoutDraft.Plan(", "UpdateDocumentAsync(")],
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
               "OcrLineReconciler.LineDiscount("],
         must_re=[r"if\s*\(\s*vatPlan\s*\.\s*Decided\s*\)", r"vatPlan\s*\.\s*Rates\s*\[\s*vpi\s*\]"],
         before=[("OcrLineVatPlanner.PlanWholeInvoice(", "ThaiVatTypeRule.Suggest(")],
         # ทีม D 2026-10-13 B5: % ส่วนลดของบรรทัดมาจาก LineDiscount ตัวเดียว (ไม่มีราคาต่อหน่วย ⇒ 0% · ไม่งั้น ComputeLineAmounts หักซ้ำ)
         forbid=["DiscountPercent = docDiscountPercent,",
                 "DiscountPercent = Accounting.Helpers.OcrLineReconciler.LineDiscountPercent("],
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
# รอบ 203 — รูปแบบกระดาษหลักฐานรับเงิน (Helpers/ReceiptFormRule): ด่านอนุมัติต้องตัดสิน "ก่อน" หัวกระดาษ · ใช้ผล · บล็อกต้องยัง throw ·
# ข้อเท็จจริงทุกตัวมาจากเจ้าของกติกาเดิม (สิทธิ์ §86/6 · นโยบายมัดจำ · ความครบ §86/4) ห้ามประกอบเองในชั้น service
DOC_RECEIPT_FORM = "Services/Implementations/DocumentService.ReceiptForm.cs"
RULES += [
    dict(file=DOC, method="ApproveDocumentAsync#2",
         must=["DecideReceiptFormAsync(", "doc.ReceiptForm = receiptForm.Chosen", "AddChainedAuditLog("],
         must_lit=['"ReceiptFormChoiceOverridden"'],   # ตัวเลือกรายใบที่ถูกแทนต้องลง audit (ฝ่ายค้านรอบ 203 ข้อ 5)
         before=[("DecideReceiptFormAsync(", "ResolveDocumentTitleAsync(")],
         # คำตัดสินข้อ 140: ห้ามบล็อกด้วยกติกานี้ — คำเตือนไปทาง CollectApprovalWarningsAsync
         forbid=["receiptForm.Blocked", "receiptForm.BlockMessage"],
         why="รอบ 203: รูปแบบใบเสร็จต้องตรึงลงใบก่อน resolver หัวกระดาษอ่าน · ไม่มี throw ในด่านนี้ (ข้อ 140: เตือน+รับทราบ)"),
    dict(file=DOC, method="CollectApprovalWarningsAsync",
         must=["DecideReceiptFormAsync(", "receiptFormDecision.Warnings"],
         why="รอบ 203 ข้อ 140: คำเตือนรูปแบบใบเสร็จต้องเข้าชุดคำเตือนก่อนอนุมัติ (เว็บรับทราบ · ทางเข้าอัตโนมัติผ่านพร้อมร่องรอย)"),
    dict(file=DOC_RECEIPT_FORM, method="DecideReceiptFormAsync",
         must=["ReceiptFormRule.AppliesTo(", "ReceiptFormRule.Decide(", "ReceiptFormRule.CaseOf(", "ReceiptFormPolicy.Parse(",
               "DepositPolicyResolver.OfDocument(", "AbbreviatedTaxInvoiceRule.CanIssue(",
               "TaxInvoiceCompletenessChecker.IsJuristicBuyer(", "TaxInvoiceCompletenessChecker.MissingBuyerFields(",
               # ฝ่ายค้านรอบ 203: ใบกำกับเข้าตัวตัดสินเฉพาะที่เป็นหลักฐานรับเงิน · ผู้ซื้อแจ้งเลขภาษี = ต้องการเต็มรูป
               "doc.IssuedAsCashReceipt", "TaxInvoiceCompletenessChecker.IsValidThaiTaxId("],
         forbid=["IsRetailApproved &&", "IsRetailApproved ||", "DepositOutputVatDeferred ?", "ContactType ==", "ContactType.JuristicPerson"],
         why="รอบ 203: ตัวตัดสินรับข้อเท็จจริงจากเจ้าของกติกาเดิมเท่านั้น — ไม่มีสำเนาเกณฑ์ขายปลีก/มัดจำ/นิติบุคคลชุดที่สอง"),
]

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

# ── 2026-10-09 (ตรวจความครอบคลุมเส้นกระดาษ): แถวสรุป/แถวชำระ/เงินทอนที่ engine คืนปนในตารางรายการ ต้องผ่านตัวตัดสินตัวเดียว ─────
#    Helpers/OcrNonItemRow ทุกทางเข้าที่รับ "ตารางรายการ" จาก engine (Azure prebuilt Items · Azure layout-table fallback · python ocr-service)
#    — ถอดการเรียกที่ทางเข้าใดทางเข้าหนึ่ง = สลิป POS จากทางนั้นได้บรรทัด "เงินสด/เงินทอน" กลับมาเงียบ ๆ (เทสต์ของ helper ยังเขียว)
AZURE_DI = "Services/Implementations/Ocr/AzureDocumentIntelligenceService.cs"
RULES += [
    dict(file=OCR, method="MapAzureDiToExtractedDataAsync",
         must=["OcrNonItemRow.IsSummaryOrTenderRow(item.Description)"],
         before=[("OcrNonItemRow.IsSummaryOrTenderRow(", "data.Items.Add(")],
         why="2026-10-09: แถวสรุป/ชำระจาก Azure Items ไม่เข้าเป็นบรรทัดสินค้า"),
    dict(file=OCR, method="ExtractWithLocalServiceAsync",
         must=["OcrNonItemRow.IsSummaryOrTenderRow(rowDesc)"],
         before=[("OcrNonItemRow.IsSummaryOrTenderRow(", "data.Items.Add(")],
         why="2026-10-09: แถวสรุป/ชำระจาก python ocr-service ไม่เข้าเป็นบรรทัดสินค้า"),
    dict(file=AZURE_DI, method="TryExtractItemsFromTables",
         must=["OcrNonItemRow.IsSummaryOrTenderRow(desc)", "OcrNonItemRow.DroppedNote("],
         before=[("OcrNonItemRow.IsSummaryOrTenderRow(", "result.Items.Add(")],
         why="2026-10-09: ตาราง layout ของ Azure — แถว 'เงินสด/เงินทอน/รวม/VAT' ที่มีตัวอักษรเคยผ่านด่าน 'ไม่มีตัวอักษร' เป็นบรรทัดสินค้า"),
    # ฝ่ายค้าน 2026-10-09 ข้อ 3: ทางเข้าที่ 4 = บรรทัดที่โมเดล/นักเรียน/RawTextLineSplitter แตกมา (ผ่าน OcrLineSplitGuard) ต้องเดินด่านเดียวกัน ·
    # และ RawTextLineSplitter ห้ามมีสำเนาคำสรุปชุดที่สอง — ต้องถาม OcrNonItemRow.MentionsSummaryLabel
    dict(file=OCR, method="TrySplitLineItemsAsync",
         must=["OcrNonItemRow.IsSummaryOrTenderRow(line.Description)"],
         before=[("OcrLineSplitGuard.Evaluate(", "OcrNonItemRow.IsSummaryOrTenderRow("),
                 ("OcrNonItemRow.IsSummaryOrTenderRow(", "data.Items.Add(")],
         why="2026-10-09: บรรทัดจาก line-split (AI/นักเรียน/กติกา) ไม่ให้แถวเงินสด/เงินทอน/รวม เป็นสินค้า"),
    dict(file="Helpers/RawTextLineSplitter.cs", method="TrailingCandidate",
         must=["OcrNonItemRow.MentionsSummaryLabel(line)"],
         forbid=["SummaryMarkers"],
         why="2026-10-09 F2 ข้อ 4: คำสรุป/ชำระ/เงินทอนของเส้น OCR อยู่ที่ OcrNonItemRow ที่เดียว — ตัวแตกบรรทัดห้ามมีลิสต์ของตัวเอง"),
]

# ── ฝ่ายค้าน 2026-10-09 ข้อ 4: replay harness ระดับบรรทัด (Accounting.Tests/OcrReplayHarness.BuildLines) ต้องเรียก helper ชุดเดียวกับ ───
#    BuildScanLinesAsync — ลิสต์เดียวใช้กับทั้งสองเมธอด ⇒ เพิ่ม/ถอด helper ฝั่งใดฝั่งหนึ่งแล้วอีกฝั่งไม่ตาม = แดง (ป้องกัน harness ล็อกคำตอบที่ service ไม่ได้ให้)
LINE_PIPELINE_HELPERS = [
    "OcrLineVatMarks.Read(", "OcrHeaderVatEvidence.Classify(", "OcrLineVatMarks.Assign(",
    # 2026-10-16: ฐานกระทบยอด (NetSubTotal) + ตัวจำแนกเคส (OcrLineReconciler.Classify) อยู่ใน OcrVatCodeLines.Decide ตัวเดียว (รายการ/แถวส่วนลด/ตารางชนะ)
    # ฝ่ายค้าน f7c29c2c P1-1: ตารางรหัส ภ.พ. ที่ตัดสินแล้วมาจาก OcrVatCodeMeaning.ResolvedTable ตัวเดียว (ไม่ประกอบ ReadGroups + Resolve เอง)
    "OcrEtaxLineNormalizer.ProvenLineDiscount(", "OcrTotalDecomposer.LineGross(", "OcrVatCodeLines.Decide(", "OcrVatCodeMeaning.ResolvedTable(",
    "OcrEtaxLineNormalizer.AllLinesPriceIncludeVat(",
    "OcrLineVatPlanner.PlanWholeInvoice(", "OcrLineVatPlanner.PaperExemptAmount(",
    "ThaiVatTypeRule.ToVatRate(", "ThaiVatTypeRule.Suggest(", "OcrEtaxLineNormalizer.InclusiveLineVats(", "ThaiVatTypeRule.SpreadHeaderVat(",
    "DocumentRounding.CapShifts(", "DocumentRounding.FromPrintedLine(",
    "OcrLineReconciler.LineDiscount(", "OcrTotalDecomposer.SummaryGroupLines(", "OcrTotalDecomposer.Decompose(",
]
HARNESS = "../Accounting.Tests/OcrReplayHarness.cs"
RULES += [
    # ด่าน Σ: service เรียกผ่าน AppendAmountIntegrityGaps (ห่อ OcrAmountIntegrity.Check) · harness เรียก Check ตรง
    dict(file=OCR, method="BuildScanLinesAsync", must=list(LINE_PIPELINE_HELPERS) + ["AppendAmountIntegrityGaps("],
         why="2026-10-09 ข้อ 4: ชุด helper ของตัวสร้างบรรทัด = ชุดที่ harness เล่นซ้ำ (ลิสต์เดียวกัน)"),
    dict(file=HARNESS, method="BuildLines", must=list(LINE_PIPELINE_HELPERS) + ["OcrAmountIntegrity.Check("],
         why="2026-10-09 ข้อ 4: replay harness ต้องเรียก helper ชุดเดียวกับ BuildScanLinesAsync — ไม่งั้น golden ล็อกคำตอบที่ service ไม่ได้ให้"),
    # ทีม E 2026-10-13: จำนวน < 1 ไม่มีราคาต่อหน่วย ⇒ ราคาที่เก็บมาจาก LineDiscount (ไม่งั้นส่วนลดติดลบ ⇒ บันทึกซ้ำได้ จำนวน × ยอด) — ทั้ง service และ harness
    dict(file=OCR, method="BuildScanLinesAsync", must=["UnitPrice = lineDisc.UnitPrice ?? item.UnitPrice ?? item.Amount ?? 0"],
         forbid=["UnitPrice = item.UnitPrice ?? item.Amount ?? 0,"],
         why="ทีม E 2026-10-13: ราคาต่อหน่วยของบรรทัดไม่มีราคา + จำนวน < 1 มาจาก OcrLineReconciler.LineDiscount ตัวเดียว"),
    dict(file=HARNESS, method="BuildLines", must=["decidedUnitPrice ?? lines[i].UnitPrice ?? amount"],
         why="ทีม E 2026-10-13: harness ใช้ราคาที่ตัวตัดสินคืนเหมือน service"),
]

# ── 2026-10-09 (ผู้ใช้รายงาน ใบ BS2026100001 บุญทรัพย์ ถาวร — PDF ต้นฉบับ+สำเนา ⇒ engine คืน 5 บรรทัดสองรอบ ⇒ ขั้นยุบรวมจำนวนเป็น ×2):
#    "แถวเดียวกันที่ถูกอ่านสองรอบ" ตัดสินที่ Helpers/OcrDuplicateLineGuard ตัวเดียว · ต้องถูกเรียก **ก่อน** ขั้นยุบชื่อ+ราคา (seen.TryGetValue) ด้วยยอดหัวใบจริง
#    (data.SubTotal) · ทางเข้าที่สอง = บรรทัดจากข้อความ (OcrLineSplitGuard.Evaluate ก่อนเทียบ Σ) · harness เล่นซ้ำด่านเดียวกันก่อนกระทบยอด
#    — ถอดการเรียกที่จุดใด = ใบต้นฉบับ+สำเนากลับมาจำนวน ×2 เงียบ ๆ (เทสต์ของ helper ยังเขียว)
RULES += [
    dict(file=OCR, method="SanitizeVatSplitArtifacts",
         must=["OcrDuplicateLineGuard.Decide(", "OcrDuplicateLineGuard.EffectiveAmount("],
         # ฝ่ายค้านข้อ 1: ส่วนลดท้ายบิลของสแกนต้องถึงตัวตัดสิน (ใบมีส่วนลด Σ บรรทัด = หัวใบ + ส่วนลด) · ข้อ 4: EffAmt ใช้สูตรเดียวกับ helper
         call_args=[("OcrDuplicateLineGuard.Decide(", "data.SubTotal"), ("OcrDuplicateLineGuard.Decide(", "data.DiscountAmount")],
         before=[("OcrDuplicateLineGuard.Decide(", "seen.TryGetValue(key, out var existing)")],
         why="2026-10-09 BS2026100001: ตัดแถวที่ถูกอ่านซ้ำด้วยยอดหัวใบ+ส่วนลดจริง ก่อนขั้นยุบชื่อ+ราคาที่บวกจำนวน · ยอดบรรทัดสูตรเดียว"),
    # ฝ่ายค้านข้อ 2: [DUP-ROWS] จาก trace ต้องถึง ProcessingNotes (ผู้อ่าน = OcrPostingReadiness) หลัง Sanitize — ไม่งั้นเอกสารที่ระบบตัดแถวเองอนุมัติเองได้
    dict(file=OCR, method="ScanAsync",
         must=["OcrDuplicateLineGuard.Tag"],
         before=[("SanitizeVatSplitArtifacts(extractedData", "OcrDuplicateLineGuard.Tag")],
         must_re=[r"scanResult\.ProcessingNotes\s*=\s*\(\s*scanResult\.ProcessingNotes\s*\?\?[^;]*\+\s*dupRowsNote\s*;"],
         why="2026-10-09 ฝ่ายค้านข้อ 2: หมายเหตุ [DUP-ROWS] ต้องเขียนลง ProcessingNotes ให้ OcrPostingReadiness หยุดอนุมัติเอง"),
    dict(file="Helpers/OcrLineSplitGuard.cs", method="Evaluate",
         must=["OcrDuplicateLineGuard.Decide("],
         before=[("OcrDuplicateLineGuard.Decide(", "parsed.Sum(")],
         why="2026-10-09 BS2026100001: บรรทัดจากข้อความต้นฉบับ+สำเนา (Σ = 2 × หัวใบ) ตัดซ้ำก่อนเทียบ Σ — ไม่งั้นทิ้งทั้งชุดเป็นบรรทัดสรุปใบเดียว"),
    dict(file=OCR, method="TrySplitLineItemsAsync",
         must=["guard.DedupeNote"],
         why="2026-10-09 BS2026100001: หมายเหตุการตัดแถวซ้ำต้องถึง trace ของสแกน (ล้มดัง ไม่ตัดเงียบ)"),
    dict(file=HARNESS, method="BuildLines",
         must=["OcrDuplicateLineGuard.Decide("],
         call_args=[("OcrDuplicateLineGuard.Decide(", "discountToSpread")],
         before=[("OcrDuplicateLineGuard.Decide(", "OcrVatCodeLines.Decide(")],
         why="2026-10-09 BS2026100001: harness เล่นซ้ำด่านแถวซ้ำก่อนกระทบยอด — golden ของใบต้นฉบับ+สำเนาถึงล็อกคำตอบที่ service ให้จริง"),
]

# ── 2026-10-16 คำตัดสินเจ้าของ "ถอดช่องหมวดค่าใช้จ่ายออกเลย": หมวดที่ระบบ/LLM/ผู้ใช้เลือก ห้ามกลับมาเป็นหลักฐาน/คีย์ ─────
#    เทสต์ OcrExpenseCategoryRemovalTests ล็อก helper + ตัวจัดหมวด (pure) — ถอด headerDescription: null กลับเป็นชื่อหมวด ⇒ เทสต์ยังเขียว
#    แต่ใบเสร็จซื้อของได้ข้อเสนอหัก ณ ที่จ่าย 3% 40(8) กลับมา (ค่าตั้งต้น "ค่าบริการ") ⇒ ล็อกจุดเรียกที่นี่
RULES += [
    dict(file=OCR, method="ScanAsync",
         must=["OcrLearnerDescriptionKey.For("],
         must_re=[r"headerDescription\s*:\s*null\s*,"],
         forbid=["headerDescription: extractedData.ExpenseCategory", "?? extractedData.ExpenseCategory"],
         forbid_lit=['extractedData.FieldConfidence["ExpenseCategory"]'],
         why="2026-10-16: ตัวจัดหมวดเห็นเฉพาะหลักฐานบนกระดาษ · ตัวเรียน/NaiveBayes ทำนายด้วยคีย์คำอธิบายตัวเดียว ไม่ fallback เป็นชื่อหมวด"),
    dict(file=OCR, method="SubmitCorrectionAsync",
         # ทีม OCR 9: บรรทัดผ่านตัวตัดท้าย "(4,272 หน่วย)" ตัวเดียว (OcrLearnerDescriptionKey.Descriptions → OcrLineQuantityGuard.DescriptionForMatching) ก่อนสร้างคีย์
         must=["OcrLearnerDescriptionKey.For(", "OcrLearnerDescriptionKey.Descriptions(ScanLineDescriptionsForLearner(result)), scannedTypeBeforeCorrection)"],
         before=[("var scannedTypeBeforeCorrection = result.DocumentType;", "result.DocumentType = correction.DocumentType;")],
         forbid=["result.ExpenseCategory"],
         why="2026-10-16: ตัวเรียนผังบันทึกด้วยคีย์เดียวกับฝั่งทำนาย (คำอธิบายบรรทัด) · คำแก้ไม่เขียนหมวดลงสแกน (ช่องถูกถอดจาก DTO)"),
    dict(file=OCR, method="ScanAsync",
         must=["OcrLearnerDescriptionKey.PredictAsync(", "OcrDocTypeDefaultAccounts.DebitFallbackSeeds(extractedData.DocumentType,"],
         call_args=[("OcrLearnerDescriptionKey.PredictAsync(", "extractedData.DocumentType"),
                    # ฝ่ายค้าน EC2 P2-1: ใบซื้อสินค้าได้ต้นทุนสินค้าเฉพาะบริษัทที่ถือสต็อก
                    ("OcrDocTypeDefaultAccounts.DebitFallbackSeeds(", "InventoryIndustry.KeepsInventory("),
                    # ฝ่ายค้าน EC3 P2-7 (ทีม EC4): บริษัทถือสต็อกไม่พอ — ของบนใบต้องเป็นสินค้า master ทุกบรรทัด
                    ("OcrDocTypeDefaultAccounts.DebitFallbackSeeds(", "OcrDocTypeDefaultAccounts.PurchaseIsStock")],
         before=[("OcrDocTypeDefaultAccounts.DebitFallbackSeeds(", "SuggestedAccountsJson = System.Text.Json.JsonSerializer.Serialize(")],
         why="ฝ่ายค้าน f5b10674 (2026-10-16): ใบไม่มีบรรทัดใช้คีย์ตามชนิดกระดาษ (ไม่ตกชั้นผู้ขายอย่างเดียว 0.433) · ด่านวางฝั่งตัดเดบิตแล้ว "
             "หมวดไม่มีผัง ⇒ ผังตั้งต้นของชนิดกระดาษ ห้ามปล่อยเดบิตว่าง (กฎเหล็ก #3)"),
    dict(file=OCR, method="ParseThaiDocument",
         must=["OcrDocTypeDefaultAccounts.For(data.DocumentType)"],
         forbid=["ExpenseCategory ="],
         why="2026-10-16: ค่าตั้งต้นตามชนิดกระดาษ (Receipt ⇒ ค่าบริการ) ไม่ใช่ข้อความบนกระดาษ — ห้ามตั้งเป็นหมวด"),
    dict(file=OCR, method="ExtractWithLocalServiceAsync",
         forbid=["ExpenseCategory ="],
         forbid_lit=['"expense_category"'],
         why="2026-10-16: หมวดที่ LLM ของ ocr-service เลือก (ไม่มีด่าน) ไม่ถูกอ่านเป็นหลักฐาน"),
    # ── ฝ่ายค้าน EC2 (7fd7a0a5) · ทีม EC3 2026-10-16 ──
    dict(file="Services/Implementations/Ocr/ExpenseCategoryResolver.cs", method="ApplyTo",
         must=["ThaiVatTypeRule.ForeignPayeeReason(null, data.VendorTaxId, data.VendorName)", "SuggestionBase(data)",
               "OcrWhtThresholdBase.InstalmentWording(", "OcrWhtThresholdBase.InstalmentNote("],
         must_re=[r"whtBasis\s*\.\s*Verdict\s+is\s+Accounting\.Helpers\.OcrWhtThresholdVerdict\.Below\s+or\s+Accounting\.Helpers\.OcrWhtThresholdVerdict\.Unsure"],
         before=[("ThaiVatTypeRule.ForeignPayeeReason(", "data.SuggestedWhtRate = effectiveWhtRate;"),
                 ("SuggestionBase(data)", "data.SuggestedWhtRate = effectiveWhtRate;")],
         # ทีม EC4 (ฝ่ายค้าน EC3 P1-3/P2-5): ห้ามนับหลักเลขที่ OCR ยังไม่ validate (LooksForeignVendor 2 อาร์กิวเมนต์) · ห้ามเทียบเกณฑ์เองนอกเจ้าของฐาน
         forbid=["1000m", "1_000m", "ThaiWhtRateTable.ShouldWithhold(", "LooksForeignVendor(null, data.VendorTaxId)"],
         why="ฝ่ายค้าน EC2 P1-2/P1-5 · EC3 P1-3/P2-5: ข้อเสนออัตราต้องผ่านเกณฑ์ 1,000 บาทบนฐานตัวเดียว (Helpers/OcrWhtThresholdBase) และผู้ขายต่างประเทศ "
             "(ตัวตัดสินเดียวกับโน้ต ภ.พ.36/ม.70 ตอนสร้างเอกสาร · เลขที่อ่านเพี้ยน = ไม่รู้) ไม่ได้อัตราในประเทศ · งวดสัญญาต่ำกว่าเกณฑ์ต้องบอกกฎหักทุกงวด"),
    dict(file="Services/Implementations/Ocr/ExpenseCategoryResolver.cs", method="SuggestionBase",
         must=["OcrWhtThresholdBase.FromScan(data.SubTotal, data.VatAmount, data.TotalAmount,", "data.DocumentType)"],
         forbid=["TotalAmount.Value -", "ShouldWithhold("],
         why="ฝ่ายค้าน EC3 P2-5: ฐานเกณฑ์ของตัวจัดหมวด = ตัวตั้งตัวเดียวกับ ParseThaiDocument/ตอนสร้างเอกสาร"),
    dict(file=OCR, method="ParseThaiDocument",
         must=["OcrWhtThresholdBase.FromScan(data.SubTotal, data.VatAmount, data.TotalAmount,"],
         must_re=[r"whtBasis\s*\.\s*Verdict\s*==\s*Accounting\.Helpers\.OcrWhtThresholdVerdict\.Below"],
         # ฝ่ายค้านรอบ 5 P2-e (ทีม EC5): อัตราที่พิมพ์ชนะการคำนวณเกณฑ์ — ห้ามกลับไปล้าง HasWht เมื่อฐานต่ำกว่าเกณฑ์ (อัตราที่พิมพ์หาย)
         must_lit=["PrintedRateBelowThresholdConfidenceCap"],
         forbid=["data.SubTotal ?? data.TotalAmount", "ThaiWhtRateTable.ShouldWithhold(whtBase", "data.HasWht = false;"],
         why="ฝ่ายค้าน EC3 P2-5: กระดาษพิมพ์อัตรา WHT แต่ต่ำกว่าเกณฑ์ — ฐานจากตัวตั้งตัวเดียว (เดิม SubTotal ?? Total คนละสูตรกับอีกสองจุด) · "
             "รอบ 5 P2-e: อัตราที่พิมพ์ชนะ + เตือน + ช่องเหลือง"),
    dict(file=OCR, method="CreateDocumentFromScanCoreAsync",
         must=["OcrWhtThresholdBase.FromScan(result.ExtractedSubTotal, result.ExtractedVatAmount,",
               "ThaiVatTypeRule.ForeignPayeeReason(null, result.ExtractedVendorTaxId, result.ExtractedVendorName)",
               # ฝ่ายค้านรอบ 5 P2-e (ทีม EC5): ฐาน "ตัดสินไม่ได้" ต้องมีโน้ต ไม่เงียบ
               "OcrWhtThresholdBase.UnsureNote(", "ExpenseCategoryResolver.StatutoryWhtRateOfCategory(result.ExpenseCategory)"],
         must_re=[r"whtBasis\s*\.\s*Verdict\s*==\s*Accounting\.Helpers\.OcrWhtThresholdVerdict\.Meets",
                  r"whtBasis\s*\.\s*Verdict\s*==\s*Accounting\.Helpers\.OcrWhtThresholdVerdict\.Unsure",
                  r"var\s+whtBase\s*=\s*whtBasis\s*\.\s*Amount"],
         forbid=["ThaiWhtRateTable.ShouldWithhold(whtBase", "(result.ExtractedTotalAmount ?? 0) - (result.ExtractedVatAmount ?? 0)",
                 "LooksForeignVendor(null, result.ExtractedVendorTaxId)"],
         why="ฝ่ายค้าน EC3 P2-5/P1-3: เกณฑ์ + ยอดหักตอนสร้างเอกสารจากฐานตัวเดียว · โน้ต ภ.พ.36 ใช้ตัวตัดสินผู้ขายต่างประเทศตัวเดียวกับตัวจัดหมวด"),
    dict(file=OCR, method="PreviewDocumentLinesAsync",
         must=["OcrWhtThresholdBase.FromScan(result.ExtractedSubTotal, result.ExtractedVatAmount,"],
         forbid=["(result.ExtractedTotalAmount ?? 0) - (result.ExtractedVatAmount ?? 0)"],
         why="ฝ่ายค้าน EC3 P2-5 (T1-03): พรีวิวต้องได้ยอดหักเดียวกับเส้นสร้างเอกสาร"),
    dict(file=OCR, method="RepopulateDocumentLinesFromScanAsync",
         must=["OcrWhtThresholdBase.FromScan(result.ExtractedSubTotal, result.ExtractedVatAmount,"],
         forbid=["(result.ExtractedTotalAmount ?? 0) - (result.ExtractedVatAmount ?? 0)"],
         why="ฝ่ายค้าน EC3 P2-5 (T1-03): ดึงรายการซ้ำต้องได้ยอดหักเดียวกับเส้นสร้างเอกสาร"),
    dict(file=OCR, method="CreateJournalEntryFromScanAsync",
         must=["OcrWhtThresholdBase.FromScan(result.ExtractedSubTotal, result.ExtractedVatAmount,"],
         forbid=["total - (result.ExtractedVatAmount ?? 0m)"],
         why="ฝ่ายค้าน EC3 P2-5 (T1-03): ลงสมุดรายวันตรงต้องได้ยอดหักเดียวกับเส้นสร้างเอกสาร"),
    dict(file=OCR, method="ApplyExternalAmountOverrides",
         must=["OcrWhtThresholdBase.FromScan(data.SubTotal, data.VatAmount, data.TotalAmount,"],
         forbid=["(data.TotalAmount ?? 0) - (data.VatAmount ?? 0)"],
         why="ฝ่ายค้าน EC3 P2-5: อัตราที่ถอดจากยอดหักของระบบภายนอกใช้ฐานตัวเดียวกัน"),
    dict(file="Services/Implementations/Ocr/ExpenseCategoryResolver.cs", method="Resolve",
         must=["AlreadyCounted(k)", "LooksGoodsOnlyLine"],
         why="ฝ่ายค้าน EC2 P1-1/P1-4: คำที่เป็นส่วนหนึ่งของคำที่นับแล้วไม่บวกซ้ำ · ชื่อผู้ขายไม่ใช่หลักฐานค่าบริการเมื่อทุกบรรทัดเป็นสินค้า"),
    # ── ฝ่ายค้านรอบ 5 (ทีม EC5) ──
    dict(file=OCR, method="ScanAsync",
         must=["GlAccountNameMatch.Pick(coaRows, nameSpec, seeded)", "categoryResult.AccountNameMatch",
               "OcrDocTypeDefaultAccounts.FreightIsInbound(", "categoryResult?.AccountNameMatch"],
         forbid=["ExtractDistinctiveKeyword(", "a.AccountName.Contains(keyword)"],
         why="ฝ่ายค้านรอบ 5 P1-3/P2-f: ผังที่ผู้ใช้ตั้งเองจับด้วยคำของกฎ (ไม่ใช่คำจากชื่อแม่แบบ · กันเงินเดือน/ค่านายหน้า) · ค่าขนส่งบนใบซื้อของบริษัทที่ถือสต็อก = 51120"),
    dict(file=OCR, method="PickCategorySeededDebitAsync",
         must=["GlAccountNameMatch.Pick(rows, nameSpec, code)"],
         forbid=["ExtractDistinctiveKeyword(", "a.AccountName.Contains(keyword)"],
         why="ฝ่ายค้านรอบ 5 P1-3: เดบิตแทนจับชื่อผ่านตัวเดียว (กันเงินเดือน/ค่านายหน้า)"),
    dict(file=OCR, method="BuildScanLinesAsync",
         must=["ThaiVatTypeRule.Suggest(", "it.Description, null, result.ExtractedVendorTaxId, result.ExtractedVendorName)"],
         why="ฝ่ายค้านรอบ 5 P1-2: อัตรา VAT รายบรรทัดต้องได้ชื่อผู้ขาย (เดิมไม่ส่ง ⇒ Canva/Google Asia Pacific ได้ 7%)"),
    dict(file="Controllers/AiSuggestionController.cs", method="InferVatType",
         must=["ThaiVatTypeRule.LooksForeignVendor(", "req.VendorCountryCode, req.VendorTaxId, req.VendorName)",
               "req.LineDescription, req.VendorCountryCode, req.VendorTaxId, req.VendorName)"],
         why="ฝ่ายค้านรอบ 5 P1-2: endpoint กรอกมือส่งชื่อผู้ขายให้ตัวตัดสินเดียวกับเส้น OCR"),
    dict(file=OCR, method="PickCategorySeededDebitAsync",
         must=[".WhereNotControlAccount(_db)", "GlDebitAccountPicker.Pick("],
         before=[(".WhereNotControlAccount(_db)", "GlDebitAccountPicker.Pick(")],
         why="ฝ่ายค้าน EC2 P2-1: เดบิตแทนห้ามเป็นบัญชีคุม (เดิมได้ 54/51 แล้ว IsPostableGl ตอนสร้างเอกสารตัดทิ้ง ⇒ เดบิตว่าง)"),
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
         must=["DocumentConversionProgress.EvaluateByValue(", "DocumentConversionProgress.InactiveChildStatuses"],
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
    dict(file=OCR, method="MatchContactCoreAsync",   # รอบ 201 OCX-4: ตัวจริงย้ายไปเมธอดภายใน (MatchContactAsync = ตัวส่งต่อ)
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
         must=["OcrDuplicateReusePolicy.Decide(", "OcrDuplicateReusePolicy.Note(dupVerdict)", "OcrExtractionVersion.Current"],
         must_re=[r"IsDuplicate\s*=\s*dupVerdict\s*\.\s*IsDuplicate\b",
                  r"DuplicateOfScanId\s*=\s*dupVerdict\s*\.\s*DuplicateOfScanId\b",
                  r"if\s*\(\s*dupVerdict\s*\.\s*ReuseExtraction\s*&&",
                  r"duplicateOf\s*\.\s*ExtractionVersion\s*[,)]",
                  r"!\s*r\s*\.\s*IsDeleted\b",
                  r"!\s*r\s*\.\s*IsDuplicate\s*\|\|\s*\(\s*r\s*\.\s*OcrEngine\s*!=\s*null\s*&&\s*r\s*\.\s*OcrEngine\s*!="],
         before=[("OcrDuplicateReusePolicy.Decide(", "OcrScanSnapshot.CopyExtractionFrom(")],
         forbid=["IsDuplicate = duplicateOf != null", "if (duplicateOf != null)"],
         why="2026-10-09 ผู้ใช้รายงาน 'ลบทั้งคู่แล้วอัปไฟล์เดิม ได้ผลผิดชุดเดิม': ด่านไฟล์ซ้ำคัดลอกผลอ่านเดิมได้เฉพาะเมื่อ "
             "OcrDuplicateReusePolicy ตัดสิน (รุ่นตัวแกะตรง · ไม่ใช่ e-Tax XML · ไม่ใช่สำเนา) · ธงไฟล์ซ้ำมาจากตัวตัดสินเดียวกัน "
             "(อ่านใหม่ก็ยังติดธง) · ทุกแถวประทับ OcrExtractionVersion.Current"),
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
         must=["CompanyVatStatus.IsRegisteredAsync(", "Map(c, vatRegistered, showToken)"],
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
    dict(file=PDF_HTML, method="ResolveDocumentHeadingAsync#1",   # รอบ 201 PL: #0 เป็น overload ส่งต่อแบบ expression-bodied (A-PL9)
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
    # รอบ 201 ทีม GW (A-GW1): action เดียวรับทั้ง URL เดิมและ URL รหัสลับ (สอง [HttpPost])
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
         before=[("LockGatewayAsync(", "EnsureIntentRefundCapacityAsync("), ("LockChannelAsync(", "EnsureIntentRefundCapacityAsync("),
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
         must=["SettlementPostingKeys.CreatorPrefix(", "p.SettlementBatchId == batchId", "DocumentStatus.Voided"],
         must_re=[r"d\s*\.\s*CompanyId\s*==\s*companyId", r"p\s*\.\s*CompanyId\s*==\s*companyId"],
         why="S3-1: ชิ้นที่ออกแล้ว = กุญแจชุดเดียวกับผู้ลงบัญชี (ไม่นับที่ยกเลิกแล้ว) · tenant · รอบ 201 A-ST1: การรับชำระ = คอลัมน์ SettlementBatchId"),
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
         call_args=[("SettlementTxnKey.LegacyKeySets(", "LiteralDateSets")],
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
               "p.SettlementBatchId != null && deadIds.Contains(p.SettlementBatchId.Value)", "d.SettlementOrphanAckAt",
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
               "Select(p => p.SettlementBatchId)"],
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
    dict(file=SETTLE_POST, method="BuildGateAsync", must=["orphans.Acknowledged", "orphans.Items", "(l.CreatedBy, l.DecidedBy)"],
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
                 ("SettlementSaleMatch.AssignRefusal(", "SetMatch(l, SettlementMatchStatus.Matched, null, userId, intentId)")],
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
         must=["SettlementTxnKey.LegacyKeySets(", "SettlementTxnKey.MatchByContent(", "StoredRowContentAsync(", "ContentOverlapElsewhereAsync(",
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
         must=["p.SettlementBatchId != null", "BatchStateAsync(", "PaidDocumentVoidReason(", "SettlementUnpostScope.IsUnposting("],
         before=[("LockBatchRowAsync(", "BatchStateAsync("), ("BatchStateAsync(", "PaidDocumentVoidReason(")],
         call_args=[("LockBatchRowAsync(", "companyId")],
         why="รอบ 200 ทีม V1 (คำตัดสินข้อ 9 · S3-5): ใบขายที่รอบโอน Posted รับชำระ — ข้อความ 409 ต้องพาไปทางที่ถูก (ยกเลิกและออกใบแทน · "
             "ใบลดหนี้ · ยกเลิกการลงบัญชี) ทุกทางเข้า · ล็อกแถวรอบโอนก่อนอ่านเมื่อเรียกใต้ธุรกรรม (S3-8)"),
    dict(file=DOCSVC, method="VoidPaymentAsync",
         must=["SettlementArtifactGuard.CheckAsync(_db, companyId, payment.SettlementBatchId)",
               "SettlementArtifactGuard.CheckLockedAsync(_db, companyId, locked.SettlementBatchId)"],
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
         must=["SettlementCrossBatchReceipts.PendingElsewhere(", "otherBatches.Contains(p.SettlementBatchId.Value)", "l.BatchId != batchId",
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
         must=["deadIds.Contains(p.SettlementBatchId.Value)"],
         call_args=[("SettlementOrphanTriage.Split(", "current")],
         why="V2-C4: กรองการรับชำระของรอบตายใน SQL (รอบ 201 A-ST1: ด้วยคอลัมน์เจ้าของ) · V2-P1: ตัวแยกรู้รอบที่กำลังลง/ตรวจเทียบ"),
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
         must=["EtaxReissueReview.ReclassReversalMisdated(", "new EtaxReissueReviewReport(undone, resolvedRows, excessRows, misdated,"],
         must_lit=['"21913"'],
         must_re=[r"rev\s*\.\s*CompanyId\s*==\s*companyId", r"orig\s*\.\s*CompanyId\s*==\s*companyId", r"a\s*\.\s*CompanyId\s*==\s*companyId"],
         forbid=["SaveChangesAsync(", "ReverseJournalEntryAsync("],
         why="ข้อ 53 (V1H): ตัวกลับภาษีขายที่ลงคนละเดือนอยู่ในรายงานข้อ 44 — อ่านอย่างเดียว · tenant ทุกตาราง"),
]

# ── รอบ 200 ทีม V1I (ฝ่ายค้าน V1H-O1/O2/O3/O5/O6): ล็อกจุดเรียก (pure ทดสอบใน VoidReissueR200ITests) ──
RULES += [
    dict(file=DOCSVC, method="ReflagKeptOriginalReceiptsAsync",
         must=["DocumentVoidPreconditions.KeptOriginalCoverageLost(", "EtaxReissueReview.KeptOriginal(",
               "EtaxReissueReview.KeptOriginalMarker", "r.EtaxCancelRequiredAt = DateTime.UtcNow", "r.EtaxCancelRequiredReason = flag",
               "r.EtaxKeptOriginalAt != null"],
         must_re=[r"r\s*\.\s*CompanyId\s*==\s*companyId", r"r\s*\.\s*EtaxCancelledByCreditNoteId\s*==\s*null"],
         # รอบ 201 ทีม DV (A-DV4 · ข้อ 68): ไม่นับทุกรายการที่ธุรกรรมนี้กำลังยกเลิก (เดิมรายการเดียว — cascade หลายรายการนับรายการก่อนหน้าว่ายังมีผล)
         call_args=[("LivePaymentCoverageAsync(", "PaymentsVoidingInThisContext"), ("PaymentsVoidingInThisContext(", "voidedPaymentId")],
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
         # รอบ 201 ฝ่ายค้าน DV-O3: เวลาอ้างอิงหลักฐานมาจากตัวโหลดเดียวกับทาง (ก) (CancellationEvidenceNotBeforeAsync → EtaxVoidPolicy.EvidenceNotBefore ภายใน)
         must=["EtaxVoidPolicy.StatusForVoid(", "DocumentVoidPreconditions.EtaxEmailedWithRdTimestampAsync(",
               "DocumentVoidPreconditions.CancellationEvidenceNotBeforeAsync(", "a.CreatedAt > evidenceNotBefore.Value"],
         call_args=[("EtaxVoidPolicy.Decide(", "decidedStatus"), ("DocumentVoidPreconditions.CancellationEvidenceNotBeforeAsync(", "etax.DocumentId")],
         before=[("EtaxVoidPolicy.StatusForVoid(", "EtaxVoidPolicy.Decide(")],
         forbid=["EtaxVoidPolicy.Decide(etax.Status", "EtaxVoidPolicy.EvidenceNotBefore("],
         why="V1H-O2/O5 (V1I): ตัดสินด้วยสถานะที่รวม e-Tax by Email ประทับเวลาแล้ว (เกณฑ์เดียวกับ EffectiveEtaxAsync) · หลักฐานต้องแนบหลังส่ง e-Tax"),
    dict(file="Helpers/DocumentVoidPreconditions.cs", method="CancellationEvidenceNotBefore",
         must=["EtaxVoidPolicy.EvidenceNotBefore(", "EtaxReachedRdStatuses.Contains("],
         why="รอบ 201 ฝ่ายค้าน DV-O3: กติกา 'แนบหลังส่ง' ชุดเดียวของการยกเลิกแถว e-Tax และทาง (ก) — แถวเก่าไม่มีเวลาส่ง = เวลาสร้างแถว"),
    dict(file="Helpers/DocumentVoidPreconditions.cs", method="CancellationEvidenceNotBeforeAsync",
         must=["CancellationEvidenceNotBefore(", "EtaxRdTimestampEmailTimesAsync("],
         must_re=[r"e\s*\.\s*CompanyId\s*==\s*companyId"],
         forbid=["DocumentEmailLogs"],
         why="รอบ 201 ฝ่ายค้าน DV-O3: ตัวโหลดเดียวของเวลาอ้างอิงหลักฐาน · เกณฑ์อีเมลตัวเดียว · tenant"),
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
         why="PR1: รายชื่อ + เงินเดือนพนักงานต้องผ่านด่านดูข้อมูลเงินเดือนก่อน (รอบ 201 X6: เงินเดือนคืนเสมอหลังด่านนี้ — ถอด includeSalary ที่จริงเสมอ)"),
]

# ── รอบ 201 ทีม PR2: คำตัดสิน 69–73 + ผลฝ่ายค้าน PR1 (X1–X6) ──
_PR2_WHY_TAX = ("PR2 ข้อ 73: ภาษีหัก ณ ที่จ่ายรายคนคิดที่ PayrollWithholdingTax.Compute ตัวเดียว (คำนวณรอบ + พรีวิว 'คำนวณภาษีให้') · "
                "ยอดสะสมจาก query เดียว (LoadPriorYtdDetailsAsync) — ห้ามสูตรภาษีชุดที่สอง")
_PR2_WHY_ROSTER = "PR2 ข้อ 69/X4: เปลี่ยนรายชื่อคนรับเงิน ⇒ รอบ Approved กลับเป็น Calculated (ต้องอนุมัติใหม่) + ประทับเวลาแก้รายชื่อ ผ่าน PayrollRosterChange ตัวเดียว"
RULES += [
    dict(file=PAYROLL, method="CalculatePayrollAsync#0",
         must=["PayrollWithholdingTax.Compute(", "LoadPriorYtdDetailsAsync(", "PitBracketsOf(taxCfg)",
               "PayrollWithholdingTax.ItemAmount(", "PayrollWithholdingTax.ItemBuckets(", "run.ManualRosterChangedAt = null"],
         forbid=["ThaiPitCalculator.Compute(", "PitAllowances(", "item.CalculationType", "pitAllowancesEffective"],
         why=_PR2_WHY_TAX + " · X4: คำนวณใหม่ล้างร่องรอยแก้รายชื่อ"),
    dict(file=PAYROLL, method="PreviewWithholdingTaxAsync",
         must=["PayrollWithholdingTax.Compute(", "PayrollDetailAmounts.ApplyFields(", "LoadPriorYtdDetailsAsync(",
               "PayrollWithholdingTax.PriorYtd(", "GetTaxRuleAsync(", "PitBracketsOf(taxCfg)", "PayrollWithholdingTax.ItemBuckets(",
               "r.CompanyId == companyId", "e.CompanyId == companyId", "x.CompanyId == companyId", "i.CompanyId == companyId"],
         forbid=["SaveChangesAsync(", "ThaiPitCalculator.Compute(", "PayrollDetailAmounts.Apply(", "AddChainedAuditLog("],
         why=_PR2_WHY_TAX + " · พรีวิวไม่บันทึกอะไร (ApplyFields บนสำเนาไม่ติดตาม) · tenant ทุก query"),
    dict(file=PAYROLL, method="LoadPriorYtdDetailsAsync",
         must=["d.PayrollRun.CompanyId == companyId", "PayrollRun.Status != "],
         must_lit=['"Voided"'],
         why=_PR2_WHY_TAX + " · ยอดสะสมของบริษัทนี้เท่านั้น ไม่นับรอบที่ยกเลิก"),
    dict(file=PAYROLL, method="UpdatePayrollDetailAsync#0",
         must=["LoadRecalculateLockEvidenceAsync(", "tx.CommitAsync(", "tx.RollbackAsync(", "PayrollSsoFlagGuard.Check(",
               "PayrollDetailAmounts.ApplyWorkersCompensation(", "LoadPriorYtdDetailsAsync(", "PayrollDetailAmounts.SetYtd("],
         must_re=[r"if\s*\(\s*flagProblem\s*!=\s*null\s*\)\s*throw\b"],
         must_lit=["FOR UPDATE", "\\\"CompanyId\\\" = {1}"],
         call_args=[("PayrollDetailAmounts.ApplyWorkersCompensation(", "run.IsExternalImport")],
         before=[("ExecuteSqlRawAsync(", "PayrollRunEditPolicy.CanEditAmounts("),
                 ("ExecuteSqlRawAsync(", "Include(r => r.Details)"),
                 ("PayrollDetailAmounts.Apply(d, req, ssoParams)", "PayrollSsoFlagGuard.Check("),
                 ("PayrollDetailAmounts.RecomputeRunTotals(run)", "_db.SaveChangesAsync(")],
         forbid=["PayrollRosterChange.Apply(", "WorkersCompensationBase.Contribution("],
         why="PR2 X2: ✏️ แก้ยอดล็อกแถวรอบ + อ่านใหม่ใต้ล็อกแบบเดียวกับ ➕/🗑 · X1: ฐาน ปกส. ที่เปลี่ยนต้องผ่านด่านธงพนักงาน · "
             "ข้อ 71: เงินทดแทนคิดใหม่ผ่านตัวเติมเดียว (รอบนำเข้าไม่แตะ) · X5: YTD · ข้อ 69: แก้ยอดไม่ดีดสถานะ (คงพฤติกรรมเดิม)"),
    dict(file=PAYROLL, method="AddPayrollDetailAsync#0",
         must=["PayrollSsoFlagGuard.Check(", "PayrollDetailAmounts.ApplyWorkersCompensation(", "PayrollRosterChange.Apply(",
               "LoadPriorYtdDetailsAsync(", "PayrollDetailAmounts.SetYtd("],
         must_re=[r"if\s*\(\s*flagProblem\s*!=\s*null\s*\)\s*throw\b"],
         call_args=[("PayrollDetailAmounts.ApplyWorkersCompensation(", "run.IsExternalImport")],
         before=[("PayrollSsoFlagGuard.Check(", "_db.SaveChangesAsync("),
                 ("PayrollDetailAmounts.RecomputeRunTotals(run)", "PayrollRosterChange.Apply("),
                 ("PayrollRosterChange.Apply(", "_db.SaveChangesAsync(")],
         forbid=["WorkersCompensationBase.Contribution(", "run.Status = "],
         why=_PR2_WHY_ROSTER + " · X1 ด่านธง ปกส. · X3 รอบนำเข้าไม่คิดเงินทดแทน (ตัวเติมเดียวกับ ✏️) · X5 YTD"),
    dict(file=PAYROLL, method="RemovePayrollDetailAsync#0",
         must=["PayrollRosterChange.Apply("],
         before=[("PayrollRosterChange.Apply(", "_db.SaveChangesAsync(")],
         forbid=["run.Status = "],
         forbid_lit=["ผ่าน API ยกเลิกรอบ", "/void"],
         why=_PR2_WHY_ROSTER + " · ข้อ 70: ข้อความคนสุดท้ายชี้ปุ่มยกเลิกรอบบนหน้า ไม่ใช่ API"),
    dict(file=PAYROLL, method="VoidPayrollAsync",
         must=["PayrollRunEditPolicy.CanVoid(", "AddChainedAuditLog(", "tx.CommitAsync("],
         must_re=[r"if\s*\(\s*why\s*\.\s*Length\s*<\s*5\s*\)\s*throw\b", r"if\s*\(\s*!\s*canVoid\s*\)\s*throw\b"],
         before=[("PayrollRunEditPolicy.CanVoid(", "_db.SaveChangesAsync("),
                 ("AddChainedAuditLog(", "_db.SaveChangesAsync(")],
         forbid=["AuditLogs.Add("],
         why="PR2 ข้อ 70: ยกเลิกรอบผ่านด่าน CanVoid ตัวเดียวกับปุ่ม · เหตุผลบังคับ + audit chain"),
    dict(file=PAYROLL, method="ProcessPaymentAsync",
         must=["PayrollDetailAmounts.RecomputeRunTotals(run)"],
         before=[("ExecuteSqlRawAsync(", "Include(r => r.Details)"),
                 ("NormalizeRunSsoAsync(", "PayrollDetailAmounts.RecomputeRunTotals(run)"),
                 ("PayrollDetailAmounts.RecomputeRunTotals(run)", "CreateJournalEntryAsync(")],
         why="PR2 X2: จ่ายด้วยรายชื่อ/ยอดที่อ่านใต้ล็อก (EF ไม่ refresh instance ที่ติดตามไว้ก่อนล็อก) · ยอดรวมของ JE คิดใหม่จากแถวด้วยตัวรวมเดียว"),
    dict(file=PAYROLL, method="MapToPayrollRunResponse",
         must=["PayrollRunEditPolicy.CanVoid("],
         call_args=[("PayrollRunEditPolicy.RecalculateWarning(", "ManualRosterChangedAt")],
         why="PR2 ข้อ 70: ปุ่มยกเลิกรอบตัดสินด้วยด่านเดียวกับ endpoint · X4: คำเตือนคำนวณใหม่อ่านร่องรอยแก้รายชื่อจากรอบ"),
    dict(file="Controllers/PayrollController.cs", method="PreviewWithholdingTax",
         before=[("RequirePayrollWriteAsync(", "_service.PreviewWithholdingTaxAsync(")],
         must_re=[r"RequirePayrollWriteAsync\s*\([^;]*PermissionKeys\s*\.\s*PayrollRun\s*\)\s*;\s*if\s*\(\s*block\s*!=\s*null\s*\)\s*return\s+block"],
         why="PR2 ข้อ 73: พรีวิวภาษีรายคนเปิดข้อมูลเงินเดือน/ลดหย่อน — ด่านสิทธิ์เดียวกับการแก้รายคน"),
    dict(file="Controllers/PayrollController.cs", method="VoidRun",
         before=[("RequirePayrollWriteAsync(", "_service.VoidPayrollAsync(")],
         call_args=[("_service.VoidPayrollAsync(", "request")],
         why="PR2 ข้อ 70: ยกเลิกรอบส่งเหตุผลจากคำขอถึง service (service บังคับ)"),
    dict(file="Controllers/PayrollController.cs", method="GetAddableEmployees#0",
         forbid=["CanViewPayrollAsync("],
         why="PR2 X6: ด่าน CheckPayrollAccessAsync คือสิทธิ์ดูเงินเดือนตัวเดียวกัน — ส่งซ้ำ = โค้ดตายที่ดูเหมือนด่าน"),
]

# ── รอบ 201 ทีม PR2 ชุดสอง (BACKLOG 2026-10-01 §1.9): A-PR1 เลขประกันสังคม · A-PR2 ค่าเสนอธง ม.5 · A-PR3 audit chain ──
_PR2_WHY_AUDIT = "PR2 A-PR3 (A-PL4): แถว audit ของเงินเดือนต้องเข้า hash chain (AddChainedAuditLog) — AuditLogs.Add ตรง = นอก chain ตรวจย้อนไม่ได้"
RULES += [
    dict(file=PAYROLL, method="NormalizeRunSsoAsync", must=["AddChainedAuditLog("], forbid=["AuditLogs.Add("], why=_PR2_WHY_AUDIT),
    dict(file=PAYROLL, method="ReopenPaidRunAsync", must=["AddChainedAuditLog("], forbid=["AuditLogs.Add("], why=_PR2_WHY_AUDIT),
    dict(file=PAYROLL, method="ReverseSsoSettlementAsync", must=["AddChainedAuditLog("], forbid=["AuditLogs.Add("], why=_PR2_WHY_AUDIT),
    dict(file=PAYROLL, method="CreateEmployeeAsync",
         must=["EmployeeRecordEdit.SsoInsuredNumber(request.SocialSecurityNumber, null)", "SocialSecurityNumber = ssoNoEdit.Value"],
         forbid=["SocialSecurityNumber = request.SocialSecurityNumber"],
         why="PR2 A-PR1: เลขประกันสังคมผ่านตัวตัดสินเดียว (13 หลัก · เก็บตัวเลขล้วน) ทั้งสร้างและแก้"),
    dict(file=PAYROLL, method="UpdateEmployeeAsync",
         must=["EmployeeRecordEdit.SsoInsuredNumber(request.SocialSecurityNumber, employee.SocialSecurityNumber)"],
         must_re=[r"if\s*\(\s*ssoNoEdit\s*\.\s*Changes\s*\)\s*employee\s*\.\s*SocialSecurityNumber\s*=\s*ssoNoEdit\s*\.\s*Value"],
         before=[("EmployeeRecordEdit.SsoInsuredNumber(", "_db.SaveChangesAsync(")],
         forbid=["employee.SocialSecurityNumber = request.SocialSecurityNumber"],
         why="PR2 A-PR1: แก้เลขประกันสังคมผ่านตัวตัดสินเดียว · ค่าปิดบังของเดิมไม่เขียนทับของจริง"),
    dict(file=PAYROLL, method="MapToEmployeeResponse",
         must=["PiiMask.CitizenId(e.SocialSecurityNumber)"],
         why="PR2 A-PR1: เลขประกันสังคมเป็น PII ม.26 — echo ปิดบังเว้นแต่มีสิทธิ์ pii:view"),
    dict(file="Controllers/PayrollController.cs", method="GetItemSsoSuggestion",
         must=["PayrollIncomeNatureRules.SuggestedCountsForSsoBase("],
         before=[("CheckPayrollAccessAsync(", "PayrollIncomeNatureRules.SuggestedCountsForSsoBase(")],
         why="PR2 A-PR2: ค่าเสนอธง ม.5 มาจากกติกาตัวเดียวที่เซิร์ฟเวอร์ (หน้าเว็บห้ามมีตารางเอง)"),
]

# ── รอบ 201 ทีม PR2 แก้ผลฝ่ายค้าน (V-1 · V-2 · V-3 · SSO-1 · Q3 · Q4) ──
_PR2_WHY_VOID = ("PR2 ฝ่ายค้าน V-1/V-2/V-3: ยกเลิกรอบตัดสินด้วย CanVoid + หลักฐานยื่น/นำส่ง/ปันต้นทุนชุดเดียวกับแก้ยอด — ทั้งด่านเร็วและ"
                 "ใต้ล็อก (แถวที่ล็อกแล้ว) · 50 ทวิ ของรอบผ่าน WhtCertVoidGuard ก่อนกลับ JE (ใบที่ยื่นแล้ว ⇒ ปฏิเสธทั้งการยกเลิก)")
RULES += [
    dict(file=PAYROLL, method="VoidPayrollAsync#0",
         must=["PayrollRunEditPolicy.CanVoid(run.Status, run.SsoSettledAt, preEvidence)",
               "PayrollRunEditPolicy.CanVoid(lockedRun.Status, lockedRun.SsoSettledAt, lockedEvidence)",
               "LoadRecalculateLockEvidenceAsync(companyId, new[] { lockedRun })",
               "WhtCertVoidGuard.CheckAsync(", "c.SourcePayrollRunId == lockedRun.Id", "c.CompanyId == companyId"],
         must_re=[r"if\s*\(\s*!\s*canVoidLocked\s*\)\s*throw\b", r"is\s+string\s+filedCert\s*\)\s*throw\b"],
         before=[("FromSqlRaw(", "PayrollRunEditPolicy.CanVoid(lockedRun"),
                 ("PayrollRunEditPolicy.CanVoid(lockedRun", "ReverseJournalEntryAsync("),
                 ("WhtCertVoidGuard.CheckAsync(", "ReverseJournalEntryAsync("),
                 ("WhtCertVoidGuard.CheckAsync(", "RestoreSalaryAdvancesAsync(")],
         forbid=["PayrollRunLockEvidence.None"],
         why=_PR2_WHY_VOID),
    dict(file=PAYROLL, method="MapToPayrollRunResponse#0",
         call_args=[("PayrollRunEditPolicy.CanVoid(", "lockEvidence")],
         why=_PR2_WHY_VOID + " · ปุ่มบนจอใช้หลักฐานชุดเดียวกับด่าน"),
    dict(file=PAYROLL, method="SyncEmployeesAsync",
         must=["EmployeeRecordEdit.SsoInsuredNumber(r.SocialSecurityNumber, null)"],
         forbid=["SocialSecurityNumber = r.SocialSecurityNumber"],
         why="PR2 ฝ่ายค้าน Q4: เลขประกันสังคมจาก HRIS ผ่านตัวตัดสินเดียวกับหน้าพนักงาน"),
    dict(file="Services/Implementations/ImportExportService.cs", method="ImportEmployeeAsync",
         must=["EmployeeRecordEdit.SsoInsuredNumber("],
         must_re=[r"if\s*\(\s*ssoNoEdit\s*\.\s*Error\s*!=\s*null\s*\)\s*throw\b"],
         forbid=['SocialSecurityNumber = row.GetValueOrDefault'],
         why="PR2 ฝ่ายค้าน Q4: เลขประกันสังคมจากไฟล์ CSV ผ่านตัวตัดสินเดียวกับหน้าพนักงาน"),
    dict(file=PAYROLL, method="GetPayrollRunAsync",
         must=["PayrollSsoFlagGuard.Check("],
         why="PR2 ฝ่ายค้าน Q3: แถวที่ธง ปกส. ขัดกับฐานต้องเห็นคำเตือนบนหน้ารอบ (ตัวตัดสินเดียวกับ ➕/✏️ · ไม่บล็อก)"),
]

# ── รอบ 201 ทีม PR2 ฝ่ายค้านรอบสอง (P1-a · P1-b · P1-c · P2-d) ──
RULES += [
    dict(file=PAYROLL, method="LoadRecalculateLockEvidenceAsync#0",
         must=["PayrollFilingSource.StatutoryRemittance", "r.CompanyId == companyId && !r.IsDeleted"],
         must_lit=['r.RemittanceType == "WhtPnd1"'],
         why="PR2 P1-a: นำส่ง ภ.ง.ด.1 ของงวดแล้ว = ยื่นแล้ว — หลักฐานตัวเดียวของแก้ยอด/คำนวณใหม่/ยกเลิกรอบ"),
    dict(file="Helpers/WhtCertVoidGuard.cs", method="CheckAsync",
         must=["StatutoryRemittances", "RemittanceForm(", "r.CompanyId == companyId && !r.IsDeleted"],
         why="PR2 P1-a: 50 ทวิ ในงวดที่นำส่งแล้วยกเลิกไม่ได้ — หลักฐานเดียวกับด่านรอบเงินเดือน"),
    dict(file=PAYROLL, method="GeneratePostPaymentArtifactsAsync",
         must=["Include(r => r.Details)"],
         before=[("Include(r => r.Details)", "IssueMonthlyPnd1CertsAsync(")],
         why="PR2 P1-b: เส้น background scope ต้องโหลดแถวรายคน — ไม่งั้น 50 ทวิ อัตโนมัติไม่เคยออก"),
    dict(file=PAYROLL, method="IssueMonthlyPnd1CertsAsync#0",
         must=["PayrollPnd1Certs.NextNumber(", "PayrollPnd1Certs.DetailsNotLoaded(", "IgnoreQueryFilters()", "certTx.CommitAsync(",
               "c.CompanyId == companyId && c.CertificateNumber.StartsWith(pnd1Prefix)"],
         must_lit=["FOR UPDATE", "\\\"CompanyId\\\" = {1}"],
         before=[("ExecuteSqlRawAsync(", "PayrollPnd1Certs.DetailsNotLoaded("),
                 ("PayrollPnd1Certs.DetailsNotLoaded(", "WhtCertVoidGuard.CheckAsync(")],
         forbid_lit=["PND1-{run.Year}"],
         why="PR2 P1-b/P1-c: ห้ามคืนเงียบเมื่อไม่ได้โหลดแถวรายคน · เลขใบจากตัวตั้งเดียว (ต่อท้าย -2/-3 เมื่อชนใบเดิมรวม Voided) ใต้ล็อกแถวรอบ"),
    dict(file="Services/Implementations/HrAllocationService.cs", method="AllocatePayrollRunAsync",
         must=["tx.CommitAsync("],
         must_lit=["FOR UPDATE", "\\\"CompanyId\\\" = {1}"],
         before=[("ExecuteSqlRawAsync(", "Include(r => r.Details)"),
                 ("ExecuteSqlRawAsync(", "_db.SaveChangesAsync(")],
         why="PR2 P2-d: ปันต้นทุนล็อกแถวรอบก่อนอ่าน (ลำดับเดียวกับยกเลิก/แก้ยอด) — ปันซ้อนกับยกเลิกรอบไม่ได้"),
]

# ── รอบ 201 ทีม PR2 ฝ่ายค้านรอบสาม (P1-1 นำส่งนับเฉพาะของที่เกิดก่อนนำส่ง · P2-1 ล้าง context หลังย้อนธุรกรรมใบ · P2-5 ภ.ง.ด.54) ──
_PR2_WHY_CUTOFF = ("PR2 P1-1: บันทึกนำส่งเป็นหลักฐาน 'ยื่นแล้ว' เฉพาะใบ/รอบที่เกิดก่อนเวลาบันทึกนำส่ง (Helpers/RemittanceInclusion ตัวเดียว) — "
                   "นับทั้งงวด = ใบที่ออกหลังนำส่ง (ยังค้างนำส่ง) ยกเลิกไม่ได้ · รอบที่ยกเลิกแล้วสร้างใหม่ถูกล็อกทันที")
RULES += [
    dict(file="Helpers/WhtCertVoidGuard.cs", method="CheckAsync",
         must=["RemittanceInclusion.LatestByPeriod(", "RemittanceInclusion.CertCountedAt(c.IssuedDate, c.CreatedAt)", "r.CreatedAt"],
         must_lit=['r.RemittanceType == "WhtPnd54"'],
         forbid=["filed.Add((form"],
         why=_PR2_WHY_CUTOFF + " · P2-5 ภ.ง.ด.54 มี 50 ทวิ และบันทึกนำส่ง"),
    dict(file="Helpers/WhtCertVoidGuard.cs", method="Reason#1",
         must=["RemittanceInclusion.Includes(", "WhtCertFilingScope.Filed.Contains(status)"],
         must_lit=["RemittanceInclusion.ThaiStamp(remittedAt)"],   # อยู่ในรูของสตริง interpolate (ถูก mask)
         why=_PR2_WHY_CUTOFF + " · ข้อความแยก 'อยู่ในการนำส่ง ณ วันที่'"),
    dict(file=PAYROLL, method="LoadRecalculateLockEvidenceAsync#0",
         must=["RemittanceInclusion.LatestByPeriod(", "RemittanceInclusion.Includes(", "run.CreatedAt", "r.CreatedAt"],
         forbid=["pnd1Remitted.Any("],
         why=_PR2_WHY_CUTOFF),
    dict(file=PAYROLL, method="IssueMonthlyPnd1CertsAsync#0",
         must_re=[r"if\s*\(\s*certTx\s*!=\s*null\s*\)\s*\{\s*await\s+certTx\s*\.\s*RollbackAsync\(\)\s*;\s*_db\s*\.\s*ChangeTracker\s*\.\s*Clear\(\)\s*;"
                  r"\s*\}\s*_logger\?\s*\.\s*LogError\([^;]*;\s*await\s+NotifyRunAsync\(",
                  r"CurrentTransaction\s*!=\s*null\s*\)\s*await\s+certTx\s*\.\s*RollbackAsync\(\)\s*;\s*_db\s*\.\s*ChangeTracker\s*\.\s*Clear\(\)\s*;"
                  r"\s*\}\s*_logger\?\s*\.\s*LogError\([^;]*;\s*await\s+NotifyRunAsync\("],
         why="PR2 P2-1: ย้อนธุรกรรมออกใบแล้วต้องล้าง ChangeTracker ก่อนแจ้งเตือน (NotificationEngine SaveChanges บน context เดียวกัน "
             "⇒ ใบ/บรรทัดที่ค้างถูกเขียนนอกธุรกรรม = ใบครึ่งชุด) · ทั้งสอง catch"),
]

# ── รอบ 201 ทีม PL (Platform/Audit/Security/Tools) — A-PL3 watermark งานตรวจ chain · (บล็อกนี้ทีม PL ต่อท้ายเอง) ──
RULES += [
    dict(file=AUDIT_JOB, method="RunCycleAsync",
         must=["AuditChainCheckpointPolicy.Decide(", "AuditChainCheckpointPolicy.Advance(", "audit.VerifyHashChainAsync("],
         call_args=[("audit.VerifyHashChainAsync(", "AfterId")],
         before=[("AuditChainCheckpointPolicy.Decide(", "audit.VerifyHashChainAsync("),
                 ("audit.VerifyHashChainAsync(", "AuditChainCheckpointPolicy.Advance(")],
         forbid=["LastVerifiedId = result.LastRowId", "LastVerifiedId = rows"],
         why="A-PL3 ช่วงที่ตรวจ/การขยับ watermark มีตัวตัดสินเดียว — พบถูกแก้/ขาดตอนต้องไม่ขยับ (ห้ามตั้ง watermark จากผลตรวจตรง ๆ)"),
    dict(file=AUDIT_SVC, method="VerifyHashChainAsync",
         must=["AuditHashChain.ExternalParents(", "a.Id <= afterId", "AuditHashChain.UnchainedNote("],
         call_args=[("AuditHashChain.Analyze(", "anchors")],
         forbid=["Analyze(rows, outside)", "Analyze(rows, Accounting.Helpers.AuditHashChain.ExternalParents("],
         why="A-PL3 ตรวจเป็นช่วง: parent ก่อนช่วงต้องค้นว่ามีจริงในฐานของบริษัท — ส่งชุดที่แถวอ้างเป็น anchors ตรง ๆ = ปิดการตรวจขาดตอน"),
    # A-PL9 (P-3 N+1): หัวเอกสารของอีเมลตั้งเวลาผ่านแคชต่อรอบ + ตัวตัดสินหัวตัวเดียว (ห้ามคำนวณภาษา/หัวเอง · ห้ามโหลดบริษัทต่อใบ)
    dict(file="Services/Implementations/EmailScheduleService.cs", method="EnqueueDocumentAsync",
         must=["HeadingForAsync("], forbid=["ResolveDocumentHeadingAsync(", "_db.Companies"],
         why="A-PL9 หัว/ชื่อบริษัทของเนื้ออีเมลมาจากแคชต่อรอบที่เรียกตัวตัดสินหัวตัวเดียว — เดิมโหลดบริษัท + หัวใหม่ทุก rule ทุกใบ"),
    dict(file="Services/Implementations/EmailScheduleService.cs", method="HeadingForAsync",
         must=["PdfGenerationService.LoadHeadingCompanyContextAsync(", "PdfGenerationService.ResolveDocumentHeadingAsync("],
         call_args=[("PdfGenerationService.ResolveDocumentHeadingAsync(", "shared")],
         why="A-PL9 ค่าระดับบริษัทโหลดครั้งเดียวแล้วส่งเข้าตัวตัดสินหัวตัวเดียว (ไม่ใช่สำเนากติกาหัว)"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="ResolveDocumentHeadingAsync#1",
         must=["shared.CompanyId == companyId", "ComputeDocumentHeading(", "AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated("],
         why="A-PL9 ค่าระดับบริษัทที่ส่งมาใช้ได้เฉพาะบริษัทเดียวกัน (tenant) · ขั้นตัดสินหัวยังเป็นของเดิม"),
    # A-PL10 (team-V1 Q5): ยกเลิกเอกสาร ERP ล้ม ⇒ ล้าง context ก่อนบันทึกสถานะออเดอร์ (ห้ามบันทึก "ยกเลิกครึ่งเดียว")
    dict(file="Services/Implementations/CmsCommerceService.cs", method="UpdateOrderStatusAsync",
         must=["_docService.VoidDocumentAsync(", "_db.ChangeTracker.Clear(", "ApplyStatus(", "VoidResultNotice.Lines("],
         before=[("_docService.VoidDocumentAsync(", "VoidResultNotice.Lines("), ("_docService.VoidDocumentAsync(", "_db.ChangeTracker.Clear(")],
         why="A-PL10 VoidDocumentAsync ใช้ context เดียวกัน — ล้มกลางทางแล้ว entity ที่แตะค้างจะถูก SaveChanges ของออเดอร์บันทึกตาม"),
    # A-PL6 (team-RF Q1): สีแบรนด์ผ่านตัวตัดสินเดียวทั้งสอง renderer
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildCss",
         must=["DocumentBrandColor.Primary(", "DocumentBrandColor.Accent("],
         why="A-PL6 สอง renderer ห้าม drift — HTML ใช้ตัวตัดสินสีแบรนด์ตัวเดียวกับ QuestPDF"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildBranding",
         must=["DocumentBrandColor.Primary(", "DocumentBrandColor.Accent("],
         forbid=["SanitizeHex(brand?.PrimaryColor) ??"],
         why="A-PL6 QuestPDF ใช้ตัวตัดสินสีแบรนด์ตัวเดียวกับ HTML (ห้ามสูตร ?? ของตัวเอง)"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildDocumentHtml",
         must_lit=["BuildCss(template, doc.Brand?.PrimaryColor)", "BuildLayoutCss(layout, template, doc.Brand?.PrimaryColor)"],
         why="A-PL6 เส้น HTML ต้องส่งแบรนด์ของเอกสารเข้า CSS (เดิมไม่รู้จักแบรนด์ ⇒ หัวคนละสีกับ PDF สำรอง/e-Tax)"),
    # รอบ 202: ป้าย ต้นฉบับ/สำเนา (ต่อท้ายชื่อ · ลายน้ำ · ป้ายมุม) ตัดสินที่ Helpers/CopyLabelPlacement ตัวเดียวทั้งสอง renderer
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildDocumentHtml",
         must=["CopyLabelPlacement.Decide(", "copyLabel.TitleSuffix", "copyLabel.Watermark", "copyLabel.Corner"],
         forbid=["copyCornerMode", "isCopyPrintWm"],
         forbid_lit=['title += $" ({L.CopyOriginal})"', '"TopLeft" ? "left"'],
         why="รอบ 202 สอง renderer ห้าม drift — HTML ห้ามตัดสินตำแหน่งป้ายเอง (เดิมเขียนเงื่อนไขซ้ำกับ QuestPDF)"),
    dict(file="Services/Implementations/PdfGenerationService.DocumentRenderer.cs", method="RenderDocumentPdfNative",
         must=["CopyLabelPlacement.Decide(", "copyLabel.TitleSuffix", "copyLabel.Watermark", "copyLabel.CornerLeft"],
         forbid=["cornerMode", "labelPos"],
         forbid_lit=['titleText += $"  ({L.CopyOriginal})"'],
         why="รอบ 202 สอง renderer ห้าม drift — QuestPDF ห้ามตัดสินตำแหน่งป้ายเอง"),
    dict(file="Services/Implementations/DocumentTemplateService.cs", method="ApplyRequestToTemplate",
         must=["CopyLabelPositionOrThrow("],
         why="รอบ 202 ค่าตำแหน่งป้ายที่ renderer ไม่รู้จักต้องถูกปฏิเสธ (เดิมรับอะไรก็ได้แล้วตกเป็นค่าเดิมเงียบ ๆ)"),
    dict(file="Services/Implementations/DocumentTemplateService.cs", method="ApplyUpdateToTemplate",
         must=["CopyLabelPositionOrThrow("],
         why="รอบ 202 เส้นแก้ไขเทมเพลตเดินด่านเดียวกับเส้นสร้าง"),
    # รอบ 202: ฐาน ปกส. มากกว่ารายได้งวด ⇒ บอกในข้อความผลลัพธ์ทั้ง ➕ เพิ่ม และ ✏️ แก้ยอด (ตัวตัดสินเดียว)
    dict(file="Services/Implementations/PayrollService.cs", method="AddPayrollDetailAsync",
         must=["PayrollSsoFlagGuard.BaseAboveWagesNotice(d.SocialSecurityBase, d.GrossIncome)"],
         why="รอบ 202 เพิ่มพนักงานกลางเดือนแล้วฐาน ปกส. ค้างเงินเดือนเต็ม ⇒ หักเกิน — ต้องบอกผู้ใช้"),
    dict(file="Services/Implementations/PayrollService.cs", method="UpdatePayrollDetailAsync",
         must=["PayrollSsoFlagGuard.BaseAboveWagesNotice(d.SocialSecurityBase, d.GrossIncome)"],
         why="รอบ 202 ทางแก้ยอดเดินด่านเดียวกับทางเพิ่ม"),
    # A-PL7 (team-Z Z-3): CSS กำหนดเองของธีมผ่านตัวกรองฝั่ง render ทุกทางออก
    dict(file="Services/Implementations/CmsRenderingService.cs", method="GenerateThemeCssFromEntity",
         must=["CssThemeValue.SafeCustomCss("], forbid=["sb.AppendLine(theme.CustomCss)"],
         why="A-PL7 แถวธีมก่อนรอบ 200 อาจมี </style — กรองตอน render (ไม่ต้อง migration)"),
    # A-PL8 (ข้อ 58): GET เทมเพลตเริ่มต้นไม่เขียน · สร้างผ่าน POST ที่มีด่าน
    dict(file="Services/Implementations/DocumentTemplateService.cs", method="GetDefaultTemplateAsync",
         must=["AsNoTracking("], forbid=["SaveChangesAsync(", "DocumentTemplates.Add("],
         why="A-PL8 GET อ่านอย่างเดียว — การสร้างเทมเพลตอยู่ใน EnsureDefaultTemplateAsync (POST + CompanySettings.Edit)"),
    dict(file="Controllers/DocumentTemplateController.cs", method="EnsureDefault",
         must=["_templateService.EnsureDefaultTemplateAsync("],
         why="A-PL8 ทางสร้างเทมเพลตเริ่มต้นมีที่เดียว (ด่านสิทธิ์ตรวจโดย write_permission_gate_check)"),
    # C-3 (ข้อ 76): ด่านเจ้าของปิดฟีเจอร์ระดับ service — ตัวตัดสินเดียว + บันทึกโหมดเงา + สวิตช์บังคับแยก
    dict(file="Services/Implementations/SubscriptionService.cs", method="CheckFeatureAccessAsync",
         must=["OwnerFeatureMask.IsOwnerDisabled(", "OwnerFeatureMask.Decide(", "_shadowLog.RecordAsync(", "OwnerFeatureMaskEnforced",
               "SubscriptionGateReason.OwnerDisabledFeature"],
         before=[("OwnerFeatureMask.Decide(", "_shadowLog.RecordAsync(")],
         forbid=["features & ~ownerDisabled"],
         why="C-3 เจ้าของปิดฟีเจอร์แล้วด่าน service ต้องเห็น — โหมดเงาก่อน (ผ่าน+บันทึก) แล้วบังคับเมื่อแอดมินกด (ห้ามเข้มขึ้นทันทีด้วย mask ตรง)"),
    dict(file="Controllers/SubscriptionController.cs", method="CheckFeature",
         call_args=[("CheckFeatureAccessAsync(", "recordShadow: false")],
         why="PL-C2 เส้น GET ของหน้าเว็บห้ามบันทึกโหมดเงา (เปิดหน้า ≠ ใช้ฟีเจอร์ — ยอด \"จะถูกปิด\" เฟ้อ)"),
    dict(file="Controllers/AdminSubscriptionEnforcementController.cs", method="Get",
         must=["IsPlanGate(", "nameof(SubscriptionGateReason.OwnerDisabledFeature)"],
         why="PL-C1 ยอดสรุปของสวิตช์แพ็กเกจห้ามรวมแถวด่านเจ้าของปิดฟีเจอร์ (คนละสวิตช์)"),
    # C-4 (ข้อ 77): ผู้ดูแลแพลตฟอร์มไม่ใช่ Owner · โอนความเป็นเจ้าของผ่านตัวตัดสินเดียว + audit chain
    dict(file="Services/Implementations/CompanyService.cs", method="CreateAsync",
         must=["CompanyCreatorMembership.NewRow(", "request.OpenForCustomer"], forbid=["new CompanyUser"],
         why="รอบ 205 ทีม I: ผู้สร้างบริษัทได้ Owner ทันที (support เฉพาะแอดมินที่ติ๊กเปิดให้ลูกค้า · C-4) — ตัวสร้างแถวตัวเดียว"),
    dict(file="Services/Implementations/AuthService.cs", method="RegisterAsync",
         must=["CompanyCreatorMembership.NewRow("], forbid=["Role = Models.Enums.UserRole.Owner"],
         why="รอบ 205 ทีม I: เส้นสมัครสร้างบริษัท ⇒ ผู้สมัครเป็น Owner ผ่านตัวสร้างแถวตัวเดียว"),
    dict(file="Services/Implementations/AuthService.cs", method="SsoLoginAsync",
         must=["CompanyCreatorMembership.NewRow("], forbid=["Role = Models.Enums.UserRole.Owner"],
         why="รอบ 205 ทีม I: เส้น SSO สร้างบริษัท ⇒ ผู้สมัครเป็น Owner ผ่านตัวสร้างแถวตัวเดียว"),
    dict(file="Services/Implementations/CompanyService.cs", method="CheckOwnershipTransferAsync",
         must=["OwnershipTransferPolicy.Decide(", "OwnerActionGuard.IsApiKeyRequest(", "cu.CompanyId == companyId"],
         why="C-4 ด่านโอนความเป็นเจ้าของ: ปฏิเสธคีย์ API · บทบาทของผู้เรียกในบริษัทนี้ (tenant)"),
    dict(file="Services/Implementations/CompanyService.cs", method="TransferOwnershipAsync",
         must=["CheckOwnershipTransferAsync(", "_db.AddChainedAuditLog(", "OwnershipTransferPolicy.Outcome.Allow"],
         before=[("CheckOwnershipTransferAsync(", "_db.AddChainedAuditLog(")],
         why="C-4 service ตรวจซ้ำก่อนเขียน (ทางเข้าอื่นที่เรียก service ตรง) · การโอนต้องอยู่ใน audit hash chain"),
    dict(file="Services/Implementations/CompanyService.cs", method="UpdateUserRoleAsync",
         must=["OwnershipTransferPolicy.MayAssign(", "IsPlatformAdminAsync("],
         why="C-4 บทบาท support ตั้งผ่านหน้าทีมไม่ได้ · ข้อ 107 SystemAdmin(99) ตั้งได้เฉพาะแอดมินแพลตฟอร์ม"),
    dict(file="Services/Implementations/CompanyService.cs", method="AddUserAsync",
         must=["OwnershipTransferPolicy.MayAssign(", "IsPlatformAdminAsync("],
         why="C-4 บทบาท support เชิญผ่านหน้าทีมไม่ได้ · ข้อ 107"),
    dict(file="Services/Implementations/CompanyService.cs", method="CheckOwnershipTransferAsync",
         call_args=[("OwnershipTransferPolicy.Decide(", "targetIsSelf")],
         why="PL-S1 ห้าม support โอนให้ตัวเอง — ต้องส่ง targetIsSelf เข้าตัวตัดสิน"),
    dict(file="Controllers/AdminController.cs", method="ChangeCompanyUserRole",
         must=["OwnershipTransferPolicy.AdminRoleChangeBlock(cu.Role, request.Role)", "_db.AddChainedAuditLog("],
         before=[("OwnershipTransferPolicy.AdminRoleChangeBlock(", "cu.Role = request.Role"), ("cu.Role = request.Role", "_db.AddChainedAuditLog(")],
         why="Q2 + ฝ่ายค้านรอบสาม P1-2: ตั้ง PlatformSupport/ตั้ง-ลด Owner จากหน้าแอดมินไม่ได้ (ต้องผ่านเส้นโอนเจ้าของ) · บทบาทเดิม/ใหม่เข้า audit chain"),
    # ฝ่ายค้านรอบสาม P1-1: Serializable ⇒ ReadCommitted + FOR UPDATE (ตัวประทับ audit ตอน commit อ่านปลาย chain จาก snapshot เก่า) · ratchet ทั้งเรพอยู่ใน audit_direct_add_check
    dict(file="Services/Implementations/DocumentService.cs", method="CreateInvoiceFromObligationAsync",
         must_lit=['"PerformanceObligations\\" WHERE \\"Id\\" = {0} AND \\"CompanyId\\" = {1} FOR UPDATE',
                   '"RevenueContracts\\" WHERE \\"Id\\" = {0} AND \\"CompanyId\\" = {1} FOR UPDATE'],
         must=["IsolationLevel.ReadCommitted", "CreateDocumentAsync("],
         before=[("ExecuteSqlRawAsync(", "CreateDocumentAsync(")],
         why="P1-1 ยังกันออกใบซ้ำของภาระงานเดียวกัน (ล็อกแถวก่อนอ่าน IsSatisfied) โดยไม่ใช้ snapshot ของ Serializable"),
    dict(file="Controllers/AdminAccountSubscriptionController.cs", method="AdminAttach",
         must=["IsolationLevel.ReadCommitted", "CountAsync("],
         must_lit=['"AccountSubscriptions\\" WHERE \\"Id\\" = {0} FOR UPDATE', '"Subscriptions\\" WHERE \\"CompanyId\\" = {0} FOR UPDATE'],
         before=[("ExecuteSqlRawAsync(", "CountAsync(")],
         why="P1-1 ล็อก License ก่อนนับช่อง (สองคำขอแย่งช่องสุดท้ายไม่ผ่านทั้งคู่) แทน Serializable"),
    # ฝ่ายค้านรอบสาม P2-2: รับคำเชิญตรวจบทบาทซ้ำ (คำเชิญเก่าที่ค้าง)
    dict(file="Controllers/InvitationController.cs", method="Accept",
         must=["OwnershipTransferPolicy.InvitationRoleBlock(inv.Role, inviterIsPlatformAdmin)"],
         before=[("OwnershipTransferPolicy.InvitationRoleBlock(", "_db.CompanyUsers.Add(")],
         why="P2-2 คำเชิญ SystemAdmin/PlatformSupport ที่ค้างก่อนแก้ห้ามรับได้"),
    dict(file="Services/Implementations/AuthService.cs", method="ConsumeInvitationAsync",
         must=["OwnershipTransferPolicy.InvitationRoleBlock(inv.Role, inviterIsPlatformAdmin)"],
         before=[("OwnershipTransferPolicy.InvitationRoleBlock(", "_db.CompanyUsers.Add(")],
         why="P2-2 เส้นสมัคร/SSO ที่รับคำเชิญเดินด่านเดียวกับปุ่มรับคำเชิญ"),
    dict(file="Services/Implementations/PermissionService.cs", method="HasPermissionAsync",
         must=["BuiltInRolePermissions.Grants(userRole, permissionKey)"],
         forbid=["userRole == UserRole.PlatformSupport) return true", "AccountantDefaultKeys"],
         why="C-4 support ได้เฉพาะคีย์งานตั้งค่า — ไม่ bypass ทั้งหมดแบบ Owner · รอบ 205: ตารางบทบาทมาตรฐานตัวเดียว (หน้าดูสิทธิ์อ่านตัวเดียวกัน)"),
    dict(file="Controllers/CompanyController.cs", method="TransferOwnership",
         must=["_companyService.CheckOwnershipTransferAsync(", "_companyService.TransferOwnershipAsync("],
         before=[("_companyService.CheckOwnershipTransferAsync(", "_companyService.TransferOwnershipAsync(")],
         why="C-4 ด่านก่อนเขียน + ข้อความปฏิเสธไทย"),
    # B-9: วันหยุดราชการระดับแพลตฟอร์ม — ผู้อ่านกำหนดยื่น (ปฏิทินภาษี · หน้านำส่ง) ส่งชุดวันหยุดเข้าตัวตัดสินเดียว (ว่าง = เดิม)
    # PL-B1 (ฝ่ายค้านรอบ 201 · ข้อ 106): ผู้อ่านที่เหลือ — ปฏิทิน compliance · ตัวตรวจก่อนยื่น · tax point ของมัดจำที่ริบ · ด่านปิดการกรอกเมื่อยังค้าง
    dict(file="Services/Implementations/ComplianceService.cs", method="InitializeFilingCalendarAsync",
         must=["PlatformHolidayStore.LoadSetAsync("],
         call_args=[("TaxFilingDeadline.For(", "holidays")],
         why="PL-B1 ปฏิทิน compliance ต้องได้กำหนดเดียวกับปฏิทินภาษี (เดิมเลื่อนแค่เสาร์/อาทิตย์)"),
    dict(file="Services/Implementations/Tax/TaxComplianceChecker.cs", method="CheckAsync",
         must=["PlatformHolidayStore.LoadSetAsync("],
         call_args=[("DeadlineFor(", "holidays"), ("TaxFilingDeadline.EFilingFor(", "holidays")],
         why="PL-B1 ตัวตรวจก่อนยื่นเตือน \"พ้นกำหนด\" ตามวันหยุดชุดเดียวกัน"),
    dict(file="Services/Implementations/DocumentService.cs", method="RealizeDepositCoreAsync",
         must=["holidays: await PlatformHolidayStore.LoadSetAsync("],
         why="PL-B1 tax point ของมัดจำที่ริบ (ย้อนเข้างวดได้ถ้ายังไม่เลยกำหนด) ใช้กำหนดยื่นที่เลื่อนพ้นวันหยุด"),
    dict(file="Controllers/AdminPlatformHolidayController.cs", method="Create",
         must=["PlatformHolidayReadiness.EntryBlockedReason("],
         before=[("PlatformHolidayReadiness.EntryBlockedReason(", "_db.PlatformHolidays.Add(")],
         why="PL-B1 ผู้อ่านกำหนดส่งยังต่อสายไม่ครบ (สปส. · §87) ⇒ ปิดการเพิ่มวันหยุด (กำหนดคนละวันในแต่ละหน้า)"),
    dict(file="Services/Implementations/TaxCalendarService.cs", method="InitializeYearAsync",
         must=["PlatformHolidayStore.LoadSetAsync("],
         call_args=[("TaxFilingDeadline.For(", "holidays"), ("TaxFilingDeadline.RollToBusinessDay(", "holidays")],
         why="B-9 กำหนดยื่นในปฏิทินภาษีเลื่อนพ้นวันหยุดราชการของแพลตฟอร์มด้วย (ป.พ.พ. §193/8)"),
    dict(file="Services/Implementations/StatutoryRemittanceService.cs", method="BuildItem",
         call_args=[("DueDates(", "_holidays")],
         why="B-9 ธงเลยกำหนดของหน้านำส่งใช้วันหยุดราชการชุดเดียวกับปฏิทินภาษี"),
    dict(file="Services/Implementations/StatutoryRemittanceService.cs", method="BuildCell",
         call_args=[("DueDates(", "_holidays")],
         why="B-9 ปฏิทินยื่นของหน้านำส่งใช้วันหยุดราชการชุดเดียวกัน"),
    dict(file="Services/Implementations/StatutoryRemittanceService.cs", method="GetDashboardAsync",
         must=["EnsureHolidaysAsync("], before=[("EnsureHolidaysAsync(", "BuildItem(")],
         why="B-9 โหลดวันหยุดก่อนคิดธงเลยกำหนด (ไม่งั้นเงียบกลับไปเสาร์/อาทิตย์)"),
    dict(file="Services/Implementations/StatutoryRemittanceService.cs", method="GetFilingCalendarAsync",
         must=["EnsureHolidaysAsync("], before=[("EnsureHolidaysAsync(", "BuildCell(")],
         why="B-9 โหลดวันหยุดก่อนสร้างปฏิทินยื่น"),
    dict(file="Helpers/TaxFilingDeadline.cs", method="RollToBusinessDay#1",
         must=["BusinessDayCalendar.RollForward("],
         why="B-9 การเลื่อนวันทำการมีตัวตัดสินเดียว (เสาร์/อาทิตย์ + วันหยุดราชการ)"),
    # คำสั่ง main agent หลังทีม DV: ผลยกเลิกเอกสาร (ธง e-Tax/ภาษีขาย) ห้ามทิ้งเงียบในทางเข้า CMS
    dict(file="Services/Implementations/CmsBookingService.cs", method="SettleErpDocumentOnCancelAsync",
         must=["VoidResultNotice.Lines("], before=[("_docService!.VoidDocumentAsync(", "VoidResultNotice.Lines(")],
         why="ผลยกเลิกเอกสาร ERP ถึงผู้กด (notices) และประทับบนการจอง — เดิมทิ้งเงียบ"),
]

# ── รอบ 201 ทีม DV (BACKLOG A-DV1..A-DV6 · C-1 · คำตัดสินข้อ 62/65/66/67/68/74): เอกสาร ยกเลิก/ออกใบแทน/e-Tax (pure ทดสอบใน VoidReissueR201DvTests) ──
_DV_WHY_LOCK = ("A-DV4 (ข้อ 68) + ฝ่ายค้าน DV-O1: การชำระที่ชำระร่วมกับเอกสารอื่นถูกปฏิเสธทันทีหลังล็อกใบนี้ ก่อนล็อกอื่น/กลับรายการใด (ตัวตัดสินเดียว "
                "SharedPaymentVoidBlock) ⇒ cascade แตะแค่ใบนี้ · ห้ามล็อก 'ใบอื่นของการชำระ' (ว่างเสมอในเส้นที่สำเร็จ แต่สร้างวงรอกับ VoidPaymentAsync/"
                "CreateMultiDocPaymentAsync ที่ล็อก ORDER BY Id) · ข้อความธงของทุกรายการในลูปถึงผู้กด (ห้ามทิ้งผล)")
RULES += [
    dict(file=DOC, method="VoidDocumentAsync",
         must=["DocumentVoidPreconditions.SharedPaymentVoidBlock(",
               "CombineNotices(cascadeEtaxFlag, cascadeVoid.EtaxFlag)", "CombineNotices(cascadeVatNotice, cascadeVoid.VatNotice)"],
         must_re=[r"new\s+PaymentVoidResult\s*\(\s*cascadeEtaxFlag\s*,\s*cascadeVatNotice\s*\)",
                  r"is\s+string\s+sharedPaymentBlock\s*\)\s*throw\b",
                  r"a\s*\.\s*CompanyId\s*==\s*companyId\s*&&\s*p\s*\.\s*CompanyId\s*==\s*companyId"],
         call_args=[("DocumentVoidPreconditions.SharedPaymentVoidBlock(", "documentId")],
         before=[("ExecuteSqlRawAsync(", "DocumentVoidPreconditions.SharedPaymentVoidBlock("),
                 ("DocumentVoidPreconditions.SharedPaymentVoidBlock(", "LockRelatedSourceDocumentAsync(companyId, documentId)"),
                 ("DocumentVoidPreconditions.SharedPaymentVoidBlock(", "LockDepositBalancesAsync("),
                 ("DocumentVoidPreconditions.SharedPaymentVoidBlock(", "ReversePaymentInternalAsync("),
                 ("DocumentVoidPreconditions.SharedPaymentVoidBlock(", "ReverseJournalEntryAsync(")],
         forbid=["LockDocumentsForPaymentVoidAsync(", "otherDocs"],
         why=_DV_WHY_LOCK),
    dict(file="Controllers/DocumentController.cs", method="VoidDocument",
         must=["voided.EtaxCancellationFlag", "voided.OutputVatNotice"],
         why="A-DV4 (ข้อ 68): ธงที่ติดระหว่างยกเลิกการชำระใน cascade ต้องถึงผู้กด (เดิมตอบแค่สำเร็จ = ล้มเงียบ F2 ข้อ 7)"),
    dict(file=DOC_REISSUE, method="LivePaymentCoverageAsync",
         must=["DocumentVoidPreconditions.LivePaymentCoverage("],
         call_args=[("DocumentVoidPreconditions.LivePaymentCoverage(", "excludePaymentIds")],
         why="A-DV4 (ข้อ 68): ยอดครอบรวมผ่านตัวตัดสิน pure ตัวเดียวและไม่นับชุดรายการที่กำลังยกเลิก"),
    dict(file=DOC_REISSUE, method="PaymentsVoidingInThisContext",
         must=["e.Entity.IsDeleted", "OriginalValue", "ids.Add(voidingPaymentId)", "EntityState.Modified"],
         must_re=[r"e\s*\.\s*Entity\s*\.\s*CompanyId\s*==\s*companyId"],
         why="A-DV4 (ข้อ 68): รายการที่ถูกตั้งยกเลิกในธุรกรรมนี้ (ยังไม่ save) ต้องไม่ถูกนับว่ายังมีผล · tenant"),
    dict(file=DOC_REISSUE, method="ResolveEtaxCancellationAsync",
         must=["DocumentVoidPreconditions.CancellationEvidenceNotBeforeAsync(", "a.CreatedAt > evidenceNotBefore.Value",
               "rcpt.EtaxKeptOriginalAt = now", "rcpt.EtaxKeptOriginalAt = null"],
         must_lit=["EtaxReissueReview.OneLine(reason)", "EtaxReissueReview.OneLine(reference)"],
         call_args=[("DocumentVoidPreconditions.CancellationEvidenceNotBeforeAsync(", "rcpt.Id")],
         before=[("DocumentVoidPreconditions.CancellationEvidenceNotBeforeAsync(", "DocumentVoidPreconditions.EtaxCancellationResolution("),
                 ("rcpt.EtaxKeptOriginalAt = now", "_db.AddChainedAuditLog(")],
         forbid=["DocumentEmailLogs"], forbid_lit=["{reason}", "{reference}"],
         why="A-DV3 (ข้อ 67) + DV-O3/O6: หลักฐานทาง (ก) ต้องแนบหลังเวลาที่ใบถึงกรมสรรพากร (แถว e-Tax ที่ส่ง · อีเมลประทับเวลา — เกณฑ์อีเมลตัวเดียว) · "
             "A-DV2 (ข้อ 65): ทาง (ค) ตั้งคอลัมน์ · ทาง ก/ข ล้าง (ผู้เขียนตัวเดียว)"),
    dict(file=DOC_REISSUE, method="EvaluateSettlementPaidReissueAsync",
         must=["DocumentVoidPreconditions.ReissueDeclaredVatMonthBlock(", "VatPeriodFilingStatusAsync(",
               "r.IsTaxInvoiceByLaw == true && r.VatAmount > 0.005m"],
         must_re=[r"verdict\s*=\s*SettlementPaidReissueVerdict\s*\.\s*Blocked\s*\(\s*declaredBlock", r"return\s*\(\s*verdict\s*,"],
         before=[("SettlementPaidReissue.Decide(", "DocumentVoidPreconditions.ReissueDeclaredVatMonthBlock(")],
         why="C-1 (ข้อ 74): ออกใบแทนในเดือนภาษีที่ประกาศว่ายื่น/ยื่นแล้ว = บล็อกพร้อมทางไปต่อ (ปุ่ม + การกดจริงผ่านตัวประเมินตัวเดียว)"),
    dict(file=DOC, method="VatPeriodFilingStatusAsync",
         must=["TaxFilingLockPolicy.DeclaredOrFiledStatuses.Contains(t.Status)", "t.TaxType == TaxType.VAT", "t.FilingLockedAt != null"],
         must_re=[r"t\s*\.\s*CompanyId\s*==\s*companyId"],
         forbid=["TaxReportStatus.Draft"],
         why="C-1 (ข้อ 74) + ฝ่ายค้าน DV-O5: คิวรีเดียวของ 'งวด ภ.พ.30 ประกาศว่ายื่น/ยื่นแล้ว/ล็อก' (ด่านมัดจำ/ใบปรับปรุง/ใบแทนใช้ร่วม) · tenant"),
    dict(file=DOC, method="VatPeriodDeclaredOrFiledAsync",
         must=["VatPeriodFilingStatusAsync("], forbid=["_db.TaxReports"],
         why="ฝ่ายค้าน DV-O5: ห้ามมีคิวรีงวด ภ.พ.30 ชุดที่สอง"),
    dict(file=DOC_REISSUE, method="GetEtaxReissueReviewAsync",
         must=["EtaxReissueReview.StuckOutputVatAfterPaymentVoid(", "EtaxReissueReview.SubmittedEtaxVoidedWithoutEvidence(",
               "EtaxReissueReview.VoidAuditHasEvidence(",
               "EtaxReissueReview.EmailedEtaxVoidedInSystem(", "DocumentVoidPreconditions.KeptOriginalCoverageLost(", "EtaxReissueReview.KeptOriginal(",
               "DocumentVoidPreconditions.EtaxEmailedWithRdTimestampAsync(", "LivePaymentCoverageAsync(", "IgnoreQueryFilters()"],
         must_re=[r"e\s*\.\s*CompanyId\s*==\s*companyId", r"d\s*\.\s*CompanyId\s*==\s*companyId"],
         call_args=[("new EtaxReissueReviewReport(", "stuckRows"), ("new EtaxReissueReviewReport(", "etaxRowsSubmitted"),
                    ("new EtaxReissueReviewReport(", "keptLostRows"), ("new EtaxReissueReviewReport(", "etaxRowsEmailed")],
         forbid=["SaveChangesAsync(", "DocumentEmailLogs", "ReverseJournalEntryAsync("],
         why="A-DV1 (ข้อ 62/66): 4 กลุ่มใหม่ของรายงานข้อ 44 — อ่านอย่างเดียว · ตัวตัดสิน pure · ใบทาง ค ใช้ตัวตัดสินเดียวกับเส้นยกเลิกการชำระ · tenant"),
    dict(file="Controllers/DocumentController.cs", method="GetEtaxReissueReview",
         must=["r.Total"],
         why="A-DV1: ตัวนับเดียวของทุกกลุ่ม (เดิมนับเอง 4 กลุ่ม — กลุ่มใหม่จะหลุดข้อความ)"),
    dict(file="Data/DatabaseMigrationHelper.cs", method="EtaxKeptOriginalBackfillSql",
         must=["EtaxReissueReview.KeptOriginalMarker", "EtaxReissueReview.LastResolutionLinePattern", "EtaxKeptOriginalLockKey"],
         must_lit=["RD-ETAX-ORIGINAL-STILL-VALID", "\"EtaxKeptOriginalAt\" IS NULL", "a.\"CompanyId\" = d.\"CompanyId\"", "information_schema.columns",
                   "pg_advisory_xact_lock("],
         forbid_lit=["ADD COLUMN IF NOT EXISTS"],
         why="A-DV2 (ข้อ 65) + ฝ่ายค้าน DV-O6: เติมครั้งเดียวในขั้นที่สร้างคอลัมน์ (ไม่สแกนทั้งตารางทุกบูต) · ป้ายตัวสุดท้ายที่ต้นบรรทัด (กติกาเดียวกับตัวอ่าน) · "
             "audit ของบริษัทเดียวกัน · ข้อความป้ายจากค่าคงที่ตัวเดียว"),
]

# ── รอบ 201 ทีม IN (สต็อก/สินทรัพย์/ค่าตั้ง/ที่พัก · BACKLOG §1.8 + C-5/C-6): ตัวตัดสิน pure ทดสอบใน InventoryCostingMethodTests ·
#    StockTotalsReconciliationTests · DepreciationEstimateChangeTests · CompanySettingsInRound201Tests · DocumentNumberBookTests ·
#    LodgingCheckoutReissueTests ⇒ ล็อกว่า service เรียกจริง/ลำดับถูก/ไม่ประกอบเอง ──
RULES += [
    dict(file="Services/Implementations/ProductService.cs", method="CreateAsync",
         must=["CostingMethodPolicy.RejectReason("],
         before=[("CostingMethodPolicy.RejectReason(", "_db.Products.Add(")],
         why="A-IN1: วิธีคิดต้นทุนของสินค้าใหม่ผ่านตัวตัดสินเดียว (ห้าม LIFO/ต้นทุนมาตรฐาน · TFRS for NPAEs บทที่ 8)"),
    dict(file="Services/Implementations/ProductService.cs", method="UpdateAsync",
         must=["CostingMethodPolicy.ChangeRejectReason(", "HasStockMovementsAsync(", "AddChainedAuditLog("],
         before=[("CostingMethodPolicy.ChangeRejectReason(", "product.CostingMethod = requestedCosting")],
         forbid=["_db.AuditLogs.Add("],
         why="A-IN1: เปลี่ยนวิธีคิดต้นทุนของสินค้าที่มีความเคลื่อนไหวแล้ว = ปฏิเสธก่อนแตะ entity · ทุกการเปลี่ยนเข้า audit chain"),
    dict(file="Services/Implementations/Inventory/InventoryCostingService.cs", method="ResolveOutboundCostAsync",
         must=["InventoryCostFlow.FifoQueue(", "LoadCostMovementsAsync(", "FifoLayerCost.Resolve("],
         why="A-IN2: คิว FIFO เห็นยอดยกมา/ตรวจนับ/ไม่นับโอนคลัง ผ่านตัวจำแนกเดียว (ห้ามกลับไปอ่านเฉพาะ IN/OUT)"),
    dict(file="Services/Implementations/Inventory/InventoryCostingService.cs", method="RebuildAverageCostAsync",
         must=["InventoryCostFlow.RebuildWeightedAverage(", "LoadCostMovementsAsync("],
         why="A-IN2: rebuild ถัวเฉลี่ยใช้ตัวจำแนก+สูตรเดียวกับ ledger (WeightedAverageCost.Next)"),
    dict(file="Services/Implementations/Inventory/StockLedger.cs", method="RepairProductTotalsAsync",
         must=["StockTotalsReconciliation.SkipReason(", "AddChainedAuditLog(", "LoadTotalsEvidenceAsync("],
         before=[("StockTotalsReconciliation.SkipReason(", "ExecuteUpdateAsync("),
                 ("ExecuteUpdateAsync(", "AddChainedAuditLog(")],
         forbid=["AuditLogs.Add("],
         why="C-5: ซ่อมยอดสต็อกรวมเฉพาะแถวที่ผู้ใช้เห็นและยังเท่าค่าที่เห็น · ประวัติไม่ตรงต้องยืนยันตรวจนับ · ทุกแถวเข้า audit chain"),
    dict(file="Controllers/ProductController.cs", method="StockTotalsRepair",
         before=[("RequireInventoryAsync(", "_stock.RepairProductTotalsAsync(")],
         call_args=[("RequireInventoryAsync(", "PermissionKeys.InventoryAdjust")],
         must_re=[r"RequireInventoryAsync\s*\([^;]*\)\s*is\s*\{\s*\}\s*deny\s*\)\s*return\s+deny"],
         why="C-5: ซ่อมยอดสต็อก (เปลี่ยนตัวเลขสต็อก) ต้องผ่านด่านสิทธิ์ปรับสต็อกก่อน และใช้ผลของด่าน"),
    dict(file="Controllers/ProductController.cs", method="StockTotalsCheck",
         before=[("RequireInventoryAsync(", "_stock.FindProductTotalMismatchesAsync(")],
         why="C-5: รายงานยอดสต็อกต้องผ่านด่านสิทธิ์ดูคลัง"),
    dict(file="Services/Implementations/PreCloseChecklistService.cs", method="RunAsync",
         must=["UsefulLifeReview.IsFiscalYearEndMonth(", "UsefulLifeReview.NeedsReview("],
         why="C-6: รายการตรวจปิดปีเตือนสินทรัพย์ที่ยังไม่ทบทวนอายุใช้งาน (ผู้อ่านของ UsefulLifeReviewedAt)"),
    dict(file="Services/Implementations/FixedAssetService.cs", method="AdjustUsefulLifeAsync",
         must=["DepreciationEstimateChange.Problem(", "DepreciationEstimateChange.IsChange(", "AddChainedAuditLog("],
         before=[("DepreciationEstimateChange.Problem(", "RemoveRange(stalePlan)")],
         forbid=["_db.AuditLogs.Add("],
         why="A-IN3: เปลี่ยนประมาณการค่าเสื่อม (อายุ/ซาก/วิธี) ไปข้างหน้าผ่านตัวตัดสินเดียว · ยืนยันค่าเดิม = บันทึกว่าทบทวน · audit chain"),
    dict(file="Services/Implementations/SettingsService.cs", method="UpdateSettingsAsync",
         must=["DocumentTemplateStyle.ColorRejectReason("],
         before=[("DocumentTemplateStyle.ColorRejectReason(", "settings.PrimaryColor =")],
         forbid=["settings.PrimaryColor = request.PrimaryColor", "settings.SecondaryColor = request.SecondaryColor"],
         why="A-IN6: สีบริษัทผ่านตัวตรวจสีเดียวกับเทมเพลตก่อนเก็บ (เดิมรับข้อความอะไรก็ได้แล้วไหลเข้า CSS หัวเอกสาร)"),
    dict(file="Services/Implementations/CompanyService.cs", method="UpdateAsync",
         must=["DepositKindSeed.AddedByIndustryChange(", "DepositKindSeed.EnsureSeededAsync("],
         why="A-IN7: เปลี่ยนประเภทธุรกิจภายหลังต้องเติมประเภทเงินมัดจำเริ่มต้นของธุรกิจใหม่ (ตารางเดียวกับตอนสร้างบริษัท)"),
    dict(file="Helpers/DocumentNumberGenerator.cs", method="NextAsync#1",
         must=["DocumentNumberBook.BookPrefix("],
         before=[("DocumentNumberBook.BookPrefix(", "AdvisoryLockKey.For(")],
         why="A-IN4: ตัวนำหน้าเล่ม (บริษัท/สาขา) ตัดสินที่เดียวก่อนล็อก — ล็อกต่อเล่ม ไม่ใช่ต่อชนิดเอกสาร"),
    dict(file="Services/Implementations/Lodging/LodgingService.Lifecycle.cs", method="ReissueFinalDocumentCoreAsync",
         must=["LodgingCheckoutReissue.Problem(", "BuildFinalInvoiceLinesAsync(", "UpsertFinalDraftAsync(",
               "ApplyFinalDepositPlanAsync(", "AddChainedAuditLog(", "FindLiveReplacementSaleAsync("],
         before=[("LodgingCheckoutReissue.Problem(", "UpsertFinalDraftAsync("),
                 ("BuildFinalInvoiceLinesAsync(", "LodgingCheckoutReissue.Problem("),
                 ("FindLiveReplacementSaleAsync(", "LodgingCheckoutReissue.Problem(")],
         call_args=[("LodgingCheckoutReissue.Problem(", "request.ConfirmNoManualReissue")],
         forbid=["MeterStayAsync(", "CreatePaymentAsync(", "AuditLogs.Add("],
         why="A-IN5: ใบเช็คเอาต์ใหม่ผ่านตัวสร้างของที่พักตัวเดียว · ยอด/ชนิดต้องเท่าใบเดิมก่อนออกเลข · ไม่นับมิเตอร์/ไม่รับเงินซ้ำ"),
    # ฝ่ายค้าน X7: เช็คเอาต์/ออกใบใหม่ เดินผ่านล็อกต่อการจองตัวเดียว (คีย์คงที่)
    dict(file="Services/Implementations/Lodging/LodgingService.Lifecycle.cs", method="CheckOutAsync",
         must=["ExclusiveCheckoutAsync(", "CheckOutCoreAsync("],
         why="X7: เช็คเอาต์ถือล็อกต่อการจอง (สองแท็บกดพร้อมกัน = ใบกำกับสองใบ)"),
    dict(file="Services/Implementations/Lodging/LodgingService.Lifecycle.cs", method="ReissueFinalDocumentAsync",
         must=["ExclusiveCheckoutAsync(", "ReissueFinalDocumentCoreAsync("],
         why="X7: ออกใบเช็คเอาต์ใหม่ถือล็อกต่อการจองตัวเดียวกับเช็คเอาต์"),
    dict(file="Services/Implementations/Lodging/LodgingService.Lifecycle.cs", method="ExclusiveCheckoutAsync",
         must=["JobLock.RunExclusiveAsync(", "AdvisoryLockKey.LodgingCheckout"],
         why="X7: session lock คีย์คงที่ AdvisoryLockKey.For(บริษัท, LodgingCheckout, การจอง) — เส้นออกเอกสารเปิดธุรกรรมหลายขั้น"),
    dict(file="Services/Implementations/Lodging/LodgingService.Lifecycle.cs", method="FindLiveReplacementSaleAsync",
         must=["d.CompanyId == companyId", "!d.IsDeposit", "d.BookingNumber == reservationNumber", "d.Reference == reservationNumber",
               "DocumentStatus.Voided", "ReceiptDocumentId"],
         why="X2: ใบแทนที่ผู้ใช้ออกเอง = ใบขายที่ยังมีผลอ้างเลขจอง (ไม่นับใบเดิม/ใบมัดจำ/ใบเสร็จคู่การรับชำระ) · tenant"),
    dict(file="Services/Implementations/Lodging/LodgingService.Lifecycle.cs", method="CheckOutCoreAsync",
         must=["BuildFinalInvoiceLinesAsync(", "UpsertFinalDraftAsync("],
         before=[("UpsertFinalDraftAsync(", "_docService.ApproveDocumentAsync(")],
         why="A-IN5: เช็คเอาต์กับออกใบใหม่ใช้ตัวสร้างรายการ/ใบร่างตัวเดียวกัน (ห้ามสำเนาที่สอง) · ใบร่าง (ใช้ซ้ำได้) ก่อนอนุมัติ"),
]

# ── รอบ 201 ทีม GW: Gateway/Integration (BACKLOG §1.1 A-GW1..A-GW12 · C-10 · C-11 · B-1) ──
ROLE_PERM = "Services/Implementations/RolePermissionService.cs"
PAY_INTENT = "Services/Payments/PaymentIntentService.cs"
RULES += [
    dict(file=GW_CTL, method="Receive",
         must=["GatewayWebhookRoute.IsWellFormedToken(", "GatewayWebhookRoute.ConfigsToTry(", "configs = candidates.Where(c => toTry.Contains(c.Id))",
               "cfg.LastTokenWebhookMode = cfg.Mode", "cfg.LastLegacyWebhookAt = cfg.LastWebhookAt"],
         must_re=[r"c\s*\.\s*WebhookToken\s*==\s*token"],
         before=[("GatewayWebhookRoute.IsWellFormedToken(", "ReadToEndAsync("),
                 ("GatewayWebhookRoute.ConfigsToTry(", "provider.VerifyWebhookAsync(")],
         forbid=["foreach (var cfg in candidates)"],
         why="A-GW1: webhook นิรนาม — ลองยืนยัน (= ยิงคำขอออกด้วยคีย์ของร้าน) เฉพาะ config ที่ตัวตัดสินเลือก · รหัสลับผิด/รูปผิด = ไม่ยิงอะไรออก"),
    dict(file="Controllers/PaymentSettingsController.cs", method="Save",
         must=["cfg.WebhookToken ??= GatewayWebhookRoute.NewToken()"],
         why="A-GW1: config ทุกแถวมีรหัสลับ — ออกครั้งเดียว (บันทึกครั้งถัดไปห้ามเปลี่ยน URL ที่ตั้งในแดชบอร์ดแล้ว)"),
    dict(file=GW_CTL, method="Reconciliation",
         must=["GatewayReconciliation.FromIntent(", "LoadBatchSettlementAsync(", "GatewayReconciliation.LateRefundSplitUnknown"],
         forbid=["IsSettled = i.SettlementJournalEntryId != null"],
         why="A-GW4: รอบโอน batch ที่ลงบัญชีแล้ว = โอนแล้ว (ตัวตัดสินเดียว) · A-GW9 นับแถวเก่าที่แยกยอดคืนหักรอบหลังไม่ได้"),
    dict(file=GW_CTL, method="LoadBatchSettlementAsync",
         must=["GatewayReconciliation.IsBatchPosted(", "GatewayReconciliation.BatchSettled("],
         must_re=[r"b\s*\.\s*CompanyId\s*==\s*companyId", r"l\s*\.\s*CompanyId\s*==\s*companyId"],
         why="A-GW4: สถานะรอบโอน + ยอดจากบรรทัดของรอบที่ลงบัญชีแล้ว · tenant"),
    dict(file=GW_CTL, method="List",
         must=["PaymentIntentPolicy.IsStuck(", "PaymentIntentPolicy.StatusLabel(", "PaymentIntentPolicy.SourceKindLabel(",
               "GatewayRefundMath.Check(", "PaymentIntentPolicy.CanEditFee(", "PaymentIntentPolicy.StatusOptions(",
               "LoadBatchSettlementAsync("],
         forbid=["isSettled = i.SettlementJournalEntryId != null"],
         why="A-GW2: ป้าย/ปุ่ม/ค้างนาน ตัดสินที่เซิร์ฟเวอร์ (JS ไม่มีสำเนาเกณฑ์) · ปุ่มคืนเงิน = ด่านเดียวกับ service"),
    dict(file=GW_CTL, method="RecordLegacyRefund", must=["refunds.RecordLegacyRefundAsync("],
         why="A-GW7: endpoint ส่งต่อ service (ด่านทั้งหมดอยู่ที่ service · เจ้าของ + ห้ามคีย์ API ที่ attribute)"),
    dict(file=GW_CTL, method="WriteOffFeeVatResidue", must=["settlements.WriteOffFeeVatResidueAsync("],
         why="A-GW8: endpoint ส่งต่อ service (เกณฑ์/ผัง/งวด/ล็อก/audit อยู่ที่ service)"),
    dict(file=GW_SETTLE, method="ListPendingAsync",
         must=["i.SettlementBatchId == null", "GatewayBatchIntentRules.LegacyAcceptsNewIntents(",
               "GatewayBatchIntentRules.LegacyNewIntentsMovedMessage(", "ActiveBatchChannelNamesByProviderAsync("],
         why="A-GW6: ตัวนับคืนก่อนระบบเก็บยอดไม่นับ intent ที่ batch ถือ · C-10: config ผูกช่องทาง batch ⇒ รายการใหม่ไม่อยู่ในหน้านี้ + ข้อความ/ลิงก์"),
    dict(file=GW_SETTLE, method="BuildPlanAsync",
         must=["GatewayBatchIntentRules.LegacyAcceptsNewIntents(", "candidates = candidates.Where(c => c.Input.AlreadySettled)",
               "GatewayBatchIntentRules.LegacyNewIntentsMovedMessage("],
         before=[("GatewayBatchIntentRules.LegacyAcceptsNewIntents(", "GatewaySettlementMath.Plan(")],
         why="C-10 (คำตัดสินข้อ 83): ตัดรายการใหม่ก่อนคิดแผน ⇒ แผนกับการมาร์ก (RecordAsync) ใช้ชุดเดียวกัน · คงเส้นคืนเงินภายหลัง"),
    dict(file=GW_SETTLE, method="ActiveBatchChannelNamesByProviderAsync",
         must=["SettlementChannelKind.Gateway", "s.IsActive"],
         must_re=[r"s\s*\.\s*CompanyId\s*==\s*companyId"],
         why="C-10: ช่องทางที่ปิดเส้นเดิม = ชนิด Gateway ที่เปิดใช้และผูก config ของบริษัทนี้"),
    dict(file=GW_SETTLE, method="ResolveAccountsAsync", must=["_accounts.ResolveFeeExpenseAccountAsync("],
         forbid=["config?.FeeExpenseAccountId"],
         why="C-11: ผังค่าธรรมเนียมของเส้นเดิมมาจากตัวตัดสินเดียวกับค่าตั้งต้นของช่องทาง batch (ผังเดียวต่อผู้ให้บริการ)"),
    dict(file=SETTLE_CHANNEL, method="SaveAsync",
         must=["GatewayBatchIntentRules.SeedFeeAccountMapOnFirstBinding(", "_gatewayAccounts.ResolveFeeExpenseAccountAsync("],
         why="C-11 (คำตัดสินข้อ 84): ผูก config ครั้งแรก ⇒ payment_fee = ผังของเส้นเดิม (แก้ได้ · ไม่ย้ายย้อนหลัง)"),
    dict(file=GW_SETTLE, method="FeeVatStatusAsync",
         must=["LoadBatchFeeVatUndueAsync(", "GatewayFeeVatClaim.BatchPortionNote(", "ResidueCheckAsync("],
         why="A-GW5: VAT ค่าธรรมเนียมของรอบโอน batch ที่รอใบกำกับ (11640 ในใบสำคัญจ่าย) แสดงคู่กัน · A-GW8 สถานะปรับปรุงเศษจากตัวตรวจเดียว"),
    dict(file=GW_SETTLE, method="ClaimFeeVatAsync",
         must=["GatewayFeeVatClaim.BatchPortionNote(", "LoadBatchFeeVatUndueAsync("],
         why="A-GW5: ยอดเกินที่พัก 11630 ต้องบอกว่าส่วนของรอบโอน batch เคลมที่ใบสำคัญจ่าย (ห้ามเคลมรวมที่นี่)"),
    dict(file=GW_SETTLE, method="LoadBatchFeeVatUndueAsync",
         must=["d.InputVatPostedAsUndue", "d.InputVatBecameClaimableAt == null", "SettlementBatchStatus.Posted"],
         must_re=[r"c\s*\.\s*CompanyId\s*==\s*companyId", r"b\s*\.\s*CompanyId\s*==\s*companyId", r"d\s*\.\s*CompanyId\s*==\s*companyId"],
         why="A-GW5: อ่านอย่างเดียว · tenant ทุก query"),
    dict(file=GW_SETTLE, method="LoadFeeVatAgingAsync", must=["GatewayFeeVatClaim.ResidueTag("],
         why="A-GW8: ใบปรับปรุงเศษล้าง 11630 เหมือนใบเคลม (ฝั่งอ่านกับฝั่งเขียนใช้ tag ตัวเดียว)"),
    dict(file=GW_SETTLE, method="ResidueCheckAsync", must=["GatewayFeeVatClaim.ResidueCheck("],
         must_re=[r"j\s*\.\s*CompanyId\s*==\s*companyId"],
         why="A-GW8: สถานะบนหน้ากับด่านตอนบันทึกมาจากตัวตรวจเดียว · tenant"),
    dict(file=GW_SETTLE, method="WriteOffFeeVatResidueAsync",
         must=["AdvisoryLockKey.For(", "ResidueCheckAsync(", "JournalEntryBuilder.ClosedPeriodReasonAsync(", "JournalEntryBuilder.For(",
               "GatewayFeeVatClaim.ResidueTag(", "_db.AddChainedAuditLog(", "_accounts.ResolveFeeExpenseAccountAsync("],
         must_re=[r"if\s*\(\s*!\s*check\s*\.\s*Ok\s*\)"],
         before=[("AdvisoryLockKey.For(", "ResidueCheckAsync("), ("ResidueCheckAsync(", "JournalEntryBuilder.For(")],
         forbid=["AuditLogs.Add(", "new JournalEntry"],
         why="A-GW8: ปรับปรุงเศษ 11630 ภายใต้เกณฑ์ ใต้ล็อกเดียวกับรอบโอน/เคลม · JE ผ่าน builder · hash chain"),
    dict(file=GW_REFUND, method="RecordLegacyRefundAsync",
         must=["GatewayRefundMath.CheckLegacyRefundEntry(", "_db.AddChainedAuditLog(", "BookRefundAsync(", "PastRefundBookingAsync(",
               "AdvisoryLockKey.For("],
         must_re=[r"if\s*\(\s*!\s*check\s*\.\s*Ok\s*\)", r"i\s*\.\s*CompanyId\s*==\s*companyId"],
         before=[("GatewayRefundMath.CheckLegacyRefundEntry(", "BookRefundAsync(")],
         forbid=["AuditLogs.Add(", "new JournalEntry"],
         why="A-GW7: บันทึกยอดคืนย้อนหลังผ่านด่านเดียว · ลงบัญชีด้วยตัวลงบัญชีคืนเงินตัวเดียว · hash chain"),
    dict(file=PAY_INTENT, method="StartAsync",
         must=["SafeReturnUrlAsync(", "ReturnUrl = returnUrl"],
         forbid=["ReturnUrl = request.ReturnUrl", "request.CardToken, request.ReturnUrl"],
         why="A-GW3: URL กลับหลังจ่ายจำกัดโดเมนของบริษัท/ระบบ (open redirect หลัง 3-D Secure) — ทุกทางเข้าเดินผ่าน StartAsync"),
    dict(file=PAY_INTENT, method="SafeReturnUrlAsync",
         must=["PaymentIntentPolicy.SafeReturnUrl("],
         must_re=[r"d\s*\.\s*CompanyId\s*==\s*companyId", r"x\s*\.\s*CompanyId\s*==\s*companyId"],
         why="A-GW3: โดเมนที่อนุญาต = เว็บไซต์ของบริษัทนี้ (tenant) + โดเมนระบบ · ตัวตัดสิน pure ตัวเดียว"),
    dict(file=INTEG, method="ApplyResyncJournalAsync",
         must=["DryRunMappingJournalAsync(", "IntegrationResyncJournal.Decide(", "IntegrationResyncJournal.KeepOriginalNote("],
         forbid=["AdvisoryLockKey.", "BeginTransactionAsync(", "CreateExecutionStrategy("],
         before=[("DryRunMappingJournalAsync(", "ResyncReverseOriginalsAsync("),
                 ("IntegrationResyncJournal.Decide(", "ResyncReverseOriginalsAsync(")],
         why="A-GW12: สร้างบรรทัด JE ใหม่ได้ก่อน (dry-run) ค่อยกลับ JE เดิม · สร้างไม่ได้ = คง JE เดิม + ดังสามที่"),
    dict(file=INTEG, method="DryRunMappingJournalAsync",
         must=["BuildIntegrationJournalLinesAsync(", "ValidateAndAutofixJournalAsync("],
         forbid=["_db.JournalEntries.Add(", "SaveChangesAsync("],
         why="A-GW12: dry-run ใช้ตัวสร้าง + ด่านโครงสร้างตัวเดียวกับตอนลงจริง และไม่บันทึกอะไร"),
    dict(file=INTEG, method="ResyncUpdateInvoiceCoreAsync", must=["ApplyResyncJournalAsync("], forbid=["ResyncReverseOriginalsAsync("],
         why="A-GW12: resync ใบแจ้งหนี้เดินเส้นปรับ JE ตัวเดียว (ห้ามกลับ JE เดิมเอง)"),
    dict(file=INTEG, method="ResyncUpdateExpenseCoreAsync", must=["ApplyResyncJournalAsync("], forbid=["ResyncReverseOriginalsAsync("],
         why="A-GW12: resync ค่าใช้จ่ายเดินเส้นปรับ JE ตัวเดียว (ห้ามกลับ JE เดิมเอง)"),
    dict(file=INTEG, method="PostMappingJournalAsync", must=["IntegrationResyncJournal.ResendNextStep"],
         why="A-GW12: ทางไปต่อที่มีจริง (ส่งซ้ำแบบแก้ไข) — เดิมชี้ปุ่ม 'ลงบัญชีใหม่จากหน้าเอกสาร' ที่ไม่มี"),
    dict(file=INTEG, method="GetDashboardAsync",
         must=["MoneyAccountFallback.IntegrationBankWarning(", "MoneyAccountFallback.PickBank("],
         must_re=[r"b\s*\.\s*CompanyId\s*==\s*companyId"],
         why="A-GW11: คำเตือนล่วงหน้าบัญชีธนาคารรับเงินจากกติกาเดียวกับตอนรับรายการชำระ · tenant"),
    dict(file=INTEG, method="VoidDocumentByExternalRefAsync",
         must=["var voidResult = await _documentService.VoidDocumentAsync(", "VoidNotices(voidResult)", "Warnings: voidNotices"],
         why="ฝ่ายค้าน DV-O4 (A-GW · รอบ 201): ธงจาก cascade ยกเลิกการชำระ (e-Tax/ภาษีขาย) ต้องถึงคู่ค้า — ห้ามทิ้งผล VoidDocumentAsync"),
    dict(file=INTEG, method="ProcessInvoiceAsync",
         must=["voidResult = await _documentService!.VoidDocumentAsync(", "WithVoidNotices(rejected, voidResult)"],
         why="ฝ่ายค้าน DV-O4 (A-GW · รอบ 201): ยกเลิกอัตโนมัติของขายเงินสดที่หักมัดจำ — ธงของการยกเลิกต่อท้ายคำตอบ"),
    dict(file=ROLE_PERM, method="GetMyPermissionsAsync", must=["PermissionDeniedMenuIdsAsync("],
         why="A-GW10: เมนูรับชำระออนไลน์ซ่อนเมื่อไม่มีสิทธิ์ (เดิมเห็นแล้วกด 403)"),
    dict(file=ROLE_PERM, method="PermissionDeniedMenuIdsAsync",
         must=["PaymentGatewayPermissionScope.MenuPermissionKeys", "_permissions.HasPermissionAsync("],
         why="A-GW10: ตารางเมนู→สิทธิ์ตัวเดียว · ตรวจด้วยด่านเดียวกับ endpoint"),
    # ── ฝ่ายค้านรอบ 201 ทีม GW (GWO-1..7) ──
    dict(file=GW_CTL, method="Receive",
         must=["c.LegacyWebhookEligible", "GatewayWebhookRoute.ConfigsToTry(candidates.Select(Facts), token, now)",
               "GatewayWebhookRoute.ShouldStampLegacySkip(", "owner.LastLegacySkippedAt = now", "skippedCompanies.Contains(i.CompanyId)"],
         before=[("GatewayWebhookRoute.ShouldStampLegacySkip(", "provider.VerifyWebhookAsync(")],
         why="GWO-1: URL เดิมลองเฉพาะร้านที่มีหลักฐานใช้ URL เดิม (ธงจาก migration) + ก่อนวันปิด · GWO-7: คำขอที่ถูกข้ามประทับเวลาให้คำเตือนขึ้นจริง (ขอบเขตบริษัท)"),
    dict(file="Controllers/PaymentSettingsController.cs", method="List",
         must=["RequireOwnerAttribute.DenyAsync(", "Map(c, vatRegistered, showToken)"],
         why="GWO-6: URL ที่มีรหัสลับเต็มเห็นเฉพาะเจ้าของ (ด่านเดียวกับ [RequireOwner])"),
    dict(file="Controllers/PaymentSettingsController.cs", method="Map",
         must=["MaskedWebhookUrl(", "GatewayWebhookRoute.AcceptsLegacy(", "c.LastLegacySkippedAt"],
         why="GWO-6 ผู้อื่นเห็น URL ปิดบัง · GWO-1 แสดง URL เดิมเฉพาะร้านที่ URL เดิมยังรับ · GWO-7 คำเตือนใช้เวลาที่ถูกข้าม"),
    dict(file="Controllers/PaymentSettingsController.cs", method="RotateWebhookToken",
         must=["GatewayWebhookRoute.NewToken()", "cfg.LastTokenWebhookAt = null", "_db.AddChainedAuditLog(",
               "cfg.LegacyWebhookEligible = GatewayWebhookRoute.LegacyEligibleAfterRotate("],
         before=[("GatewayWebhookRoute.LegacyEligibleAfterRotate(", "cfg.LastTokenWebhookAt = null")],
         must_re=[r"c\s*\.\s*CompanyId\s*==\s*companyId"],
         forbid=["AuditLogs.Add(", "cfg.WebhookToken,"],
         why="GWO-6: ออกรหัสใหม่ = URL เก่าตายทันที ⇒ เจ้าของเท่านั้น + hash chain (ไม่เก็บตัวรหัสใน audit)"),
    dict(file="Middleware/RequestLoggingMiddleware.cs", method="InvokeAsync",
         must=["GatewayWebhookRoute.RedactPath(path)"],
         why="GWO-6: path ของ webhook มีรหัสลับของร้าน — ห้ามเขียนลง log ตรง ๆ"),
    dict(file="Middleware/ApiErrorLoggingMiddleware.cs", method="InvokeAsync",
         must=["GatewayWebhookRoute.RedactPath(path)"],
         why="GWO-6: log ข้อผิดพลาด (เช่น 429 ของ webhook) ปิดบังรหัสลับ"),
    dict(file="Middleware/AuditMiddleware.cs", method="InvokeAsync",
         must=["GatewayWebhookRoute.RedactPath(context.Request.Path.Value)"],
         forbid=["var path = context.Request.Path.Value;"],
         why="RV2-2 (รอบ 201 PL): audit (append-only) + log ข้อผิดพลาดของ AuditMiddleware ต้องไม่เก็บรหัสลับใน path webhook"),
    dict(file="Services/Implementations/AuditTrailService.cs", method="CaptureAuditEntries",
         must=["AuditRedaction.Value(prop.Metadata.Name, prop.OriginalValue)", "AuditRedaction.Value(prop.Metadata.Name, prop.CurrentValue)"],
         forbid=["= prop.CurrentValue;", "= prop.OriginalValue;"],
         why="RV2-1 (รอบ 201 PL): ช่องลับ (WebhookToken · *Protected · PasswordHash · token/secret) ห้ามเข้า audit เป็นข้อความเปล่า — ตัวตัดสินเดียว Helpers/AuditRedaction"),
    dict(file="Services/Implementations/AuditTrailService.cs", method="MapToResponse",
         must=["AuditRedaction.RedactJson(a.OldValues)", "AuditRedaction.RedactJson(a.NewValues)"],
         why="RV2-1: แถวเก่าที่เก็บค่าลับไปแล้ว (แก้ไม่ได้ — append-only) ห้ามแสดงซ้ำ"),
    dict(file="Controllers/AdminController.cs", method="GetAuditLogs",
         must=["AuditRedaction.RedactJson(a.OldValues)", "AuditRedaction.RedactJson(a.NewValues)", "items = shown"],
         why="RV2-1: หน้า audit ของแอดมินแพลตฟอร์มปิดค่าลับเหมือนหน้าบริษัท"),
    dict(file=GW_SETTLE, method="ResidueCheckAsync",
         must=["LatestFeeVatDeferralDateAsync("],
         forbid=["b.MonthStartUtc"],
         why="GWO-2: ตัดสินเศษระดับวัน (วันที่บรรทัดพัก 11630 ล่าสุด) ไม่ใช่ต้นเดือน"),
    dict(file=GW_SETTLE, method="LatestFeeVatDeferralDateAsync",
         must=["l.DebitAmount > l.CreditAmount", "l.JournalEntry.ReversedByEntryId == null"],
         must_re=[r"i\s*\.\s*CompanyId\s*==\s*companyId", r"l\s*\.\s*JournalEntry\s*\.\s*CompanyId\s*==\s*companyId"],
         why="GWO-2: บรรทัดพักจากรอบโอนเส้นเดิมชุดเดียวกับ aging · tenant"),
    dict(file=GW_REFUND, method="RecordLegacyRefundAsync",
         must=["GatewayRefundMath.LegacyRefundRoundOutcome(", "RecordedRoundAsync(", "roundOutcome"],
         forbid=["RefundSettledAmount", "DeductedInRecordedRound"],
         why="RV2-3 (คำตัดสินข้อ 109): ยอดคืนย้อนหลังเข้ารอบโอนถัดไปเสมอ — ห้ามตั้งยอดที่หักแล้วเอง (รอบที่บันทึกผ่านด่านยอดตรง = ไม่ได้หักในรอบนั้น)"),
    dict(file=GW_REFUND, method="RecordedRoundAsync",
         must=["GatewayReconciliation.IsBatchPosted(", "intent.SettlementJournalEntryId != null"],
         must_re=[r"b\s*\.\s*CompanyId\s*==\s*companyId"],
         why="RV2-3: รายการอยู่ในรอบโอนที่บันทึกแล้วไหม (ถ้อยคำเท่านั้น) — สถานะ batch ชุดเดียวกับรายงานกระทบยอด · tenant"),
    dict(file=GW_CTL, method="RecordLegacyRefund", must=["journal, actor"], forbid=["RoundTiming", "roundTiming"],
         why="RV2-3: ไม่มีช่อง 'หักในรอบไหน' ให้ผู้ใช้เลือกอีก (ทางเลือกเดิมทำให้รอบถัดไปไม่หัก + กระทบยอดค้างผลต่าง)"),
    dict(file=INTEG, method="RunResyncLockedAsync",
         must=["AdvisoryLockKey.IntegrationResync", "ExecuteSqlRawAsync(", "BeginTransactionAsync(", "CreateExecutionStrategy(",
               "body(doc)", "tx.CommitAsync(", "_db.ChangeTracker.Clear()", "EntityState.Detached"],
         must_lit=["pg_advisory_xact_lock("],
         must_re=[r"d\s*\.\s*CompanyId\s*==\s*companyId", r"catch\s*\{[^}]*ChangeTracker\s*\.\s*Clear\s*\(\s*\)\s*;\s*throw\s*;"],
         before=[("ExecuteSqlRawAsync(", "_db.Documents"), ("_db.Documents", "body(doc)"), ("body(doc)", "tx.CommitAsync(")],
         forbid=["ResyncReverseOriginalsAsync(", "DocumentLines"],
         why="GWO-4 → RV2-7: ล็อก + ธุรกรรมครอบทั้ง resync (ถือล็อกก่อนโหลดหัวเอกสาร/ลบบรรทัด) · RV2-8: ล้มกลางทาง = Clear แล้วโยนต่อ (SaveSyncLog นอกธุรกรรมห้ามบันทึกของที่ rollback)"),
    dict(file=INTEG, method="ResyncUpdateInvoiceAsync", must=["RunResyncLockedAsync(", "ResyncUpdateInvoiceCoreAsync("],
         forbid=["DocumentLines", "ApplyResyncJournalAsync("],
         why="RV2-7: resync ใบแจ้งหนี้ทั้งเส้นอยู่ใต้ล็อก/ธุรกรรมเดียว (ทางเข้าเดียวของ core)"),
    dict(file=INTEG, method="ResyncUpdateExpenseAsync", must=["RunResyncLockedAsync(", "ResyncUpdateExpenseCoreAsync("],
         forbid=["DocumentLines", "ApplyResyncJournalAsync("],
         why="RV2-7: resync ค่าใช้จ่ายทั้งเส้นอยู่ใต้ล็อก/ธุรกรรมเดียว (ทางเข้าเดียวของ core)"),
    dict(file=INTEG, method="ResyncUpdateInvoiceCoreAsync", forbid=["AdvisoryLockKey.", "BeginTransactionAsync(", "CreateExecutionStrategy("],
         why="RV2-6: ภายในธุรกรรม resync ไม่ขอล็อก advisory อื่นเอง/ไม่เปิดธุรกรรมซ้อน (ลำดับล็อก int-resync → เลข JE เท่านั้น)"),
    dict(file=INTEG, method="ResyncUpdateExpenseCoreAsync", forbid=["AdvisoryLockKey.", "BeginTransactionAsync(", "CreateExecutionStrategy("],
         why="RV2-6: ภายในธุรกรรม resync ไม่ขอล็อก advisory อื่นเอง/ไม่เปิดธุรกรรมซ้อน (ลำดับล็อก int-resync → เลข JE เท่านั้น)"),
    dict(file=INTEG, method="ResyncUpdateInvoiceCoreAsync", must=["IntegrationResyncJournal.Warnings(jeAction, jeSkipReason)"],
         why="GWO-5: คง JE เดิม/สร้าง JE ไม่ได้ ⇒ อยู่ในช่องคำเตือนของคำตอบด้วย"),
    dict(file=INTEG, method="ResyncUpdateExpenseCoreAsync", must=["IntegrationResyncJournal.Warnings(jeAction, jeSkipReason)"],
         why="GWO-5: คง JE เดิม/สร้าง JE ไม่ได้ ⇒ อยู่ในช่องคำเตือนของคำตอบด้วย"),
]

# ── รอบ 201 ทีม ST (Settlement · BACKLOG §1.2 A-ST1..9 · คำตัดสินข้อ 82 C-9 · คำถามค้าง DV Q1): เทสต์ SettlementRound201StTests ล็อกตัวตัดสิน pure —
#    ที่นี่ล็อกว่า service เรียกจริง/ใช้ผล/ลำดับถูก (ป้ายใน Payment.Notes ห้ามมีผล = NOTES_MARKER_FORBID ทั้งเรพ) ──
_ST_WHY_OWNER = ("รอบ 201 ทีม ST (A-ST1): เจ้าของการรับชำระของรอบโอน = คอลัมน์ Payment.SettlementBatchId ประทับใน SaveChanges เดียวกับ INSERT "
                 "(SettlementPaymentOwner) แล้วตรวจว่าประทับจริง — ป้ายใน Notes (ผู้ใช้พิมพ์ได้) ไม่มีผลกับด่านใด")
RULES += [
    dict(file=SETTLE_POST, method="EnsureReceiptAsync",
         must=["SettlementPaymentOwner.StampOnSave(_db, batch.Id, r.DocumentId)", "p.SettlementBatchId == batch.Id",
               "p.CompanyId == companyId"],
         must_re=[r"AnyAsync\s*\([^;]*SettlementBatchId\s*==\s*batch\s*\.\s*Id[^;]*\)\s*\)\s*throw\s+new\s+BusinessRuleException\b"],
         before=[("SettlementPaymentOwner.StampOnSave(", "_documents.CreatePaymentAsync(")],
         why=_ST_WHY_OWNER),
    dict(file=SETTLE_POST, method="OrphanArtifactsAsync", forbid=["p.Notes"], why=_ST_WHY_OWNER),
    dict(file=SETTLE_POST, method="PendingReceiptsElsewhereAsync", forbid=["p.Notes"], why=_ST_WHY_OWNER),
    dict(file=SETTLE_POST, method="AcknowledgeOrphanAsync", forbid=["p.Notes"], why=_ST_WHY_OWNER),
    dict(file=SETTLE_LINES, method="FrozenPartsAsync", forbid=["p.Notes"],
         must=["SettlementPieceFingerprint", "new SettlementFrozenParts(components, received, issued)"],
         why=_ST_WHY_OWNER + " · A-ST8: ชิ้นที่ออกแล้วพกลายนิ้วมือตอนออกไปถึงตัวเทียบ (ไม่ส่ง = ตัวเทียบไม่เคยรู้ว่าเนื้อหาที่ออกคืออะไร)"),
    dict(file=SETTLE_LINES, method="PostingArtifactsAsync", must=["p.SettlementBatchId == batchId"], forbid=["p.Notes"], why=_ST_WHY_OWNER),
    dict(file="Helpers/SettlementPostingGuards.cs", method="CheckDocumentPaymentsAsync", forbid=["p.Notes"], why=_ST_WHY_OWNER),
    # A-ST2: ล็อก gateway ตัวเดียวทุกเส้นที่ประทับ intent (ไฟล์ · จับคู่มือ · จัดประเภท · จับคู่ใหม่ · ยกเลิกรอบ) + ตรวจซ้ำใต้ล็อก
    dict(file=SETTLE_LINES, method="SyncIntentStampsAsync",
         must=["LockGatewaysAsync(companyId, batchId, referenced, ct)", "i.SettlementJournalEntryId != null", "takenByLegacy++"],
         must_re=[r"if\s*\(\s*takenByLegacy\s*>\s*0\s*\)\s*throw\s+new\s+BusinessRuleException\b"],
         before=[("LockGatewaysAsync(", "_db.PaymentIntents")],
         why="รอบ 201 ทีม ST (A-ST2 · X-6/X-7): ล็อก gateway ก่อนอ่าน intent แบบติดตาม · intent ที่เส้นเดิมบันทึกรอบโอนไประหว่างนี้ = ล้มดัง (ไม่ประทับซ้ำสองเจ้าของ)"),
    dict(file=SETTLE_LINES, method="LockGatewaysAsync",
         must=["LockGatewayAsync(companyId, pc, ct)", "OrderBy(p => p, StringComparer.Ordinal)"],
         must_re=[r"i\s*\.\s*CompanyId\s*==\s*companyId"],
         why="A-ST2: ล็อกทุกผู้ให้บริการของ intent ที่รอบถือ/จะถือ เรียงชื่อก่อน (ลำดับคงที่ กัน deadlock) · tenant"),
    dict(file=SETTLE_LINES, method="LockGatewayAsync",
         must=["AdvisoryLockKey.For(companyId, AdvisoryLockKey.GatewaySettlement, providerCode)"], must_lit=["pg_advisory_xact_lock"],
         why="A-ST2: คีย์ล็อกเดียวกับ GatewaySettlementService/เส้นประกอบ (คงที่ข้ามเครื่อง)"),
    dict(file=SETTLE_LINES, method="VoidBatchAsync", before=[("LockGatewaysAsync(", "ownedIntents")],
         why="A-ST2: ล็อก gateway ก่อนตรวจ 'รอบถัดไปมีบรรทัดคืนเงินของ intent ในรอบนี้' (อีกช่องทางที่ผูก config เดียวกันเพิ่มบรรทัดพร้อมกันได้)"),
    dict(file=SETTLE_IMPORT, method="PersistAsync",
         must=["LockGatewayAsync(companyId, pc, ct)", "SettlementTxnKey.LegacyKeySets(", "KeyVersion = SettlementTxnKey.StoredKeyVersion", "literalOnly"],
         forbid=["AdvisoryLockKey.GatewaySettlement"],
         why="A-ST2: ตัวล็อก gateway ตัวเดียว · A-ST9: บรรทัดใหม่ประทับรุ่นตัวอ่าน + คีย์วันที่ตามตัวอักษรเทียบเฉพาะบรรทัดรุ่นก่อน"),
    dict(file=SETTLE_IMPORT, method="ExistingKeysAsync", must=["SettlementTxnKey.CountsAsExisting(", "l.KeyVersion"],
         must_re=[r"l\s*\.\s*CompanyId\s*==\s*companyId"],
         why="A-ST9 (R2M-9): คีย์วันที่ตามตัวอักษรนับเป็น 'มีแล้ว' เฉพาะบรรทัดที่นำเข้าด้วยตัวอ่านรุ่นก่อน (ตัวตัดสินเดียว CountsAsExisting)"),
    # A-ST5 / A-ST6: การรับรู้ผูกกับเหตุ · ไล่ใบที่อ้างทุกชั้น
    dict(file=SETTLE_POST, method="AcknowledgeOrphanCoreAsync",
         must=["p.SettlementOrphanAckReasonHash = item.ReasonHash", "d.SettlementOrphanAckReasonHash = item.ReasonHash", "reasonHash = item.ReasonHash"],
         why="รอบ 201 ทีม ST (A-ST5): การรับรู้ประทับลายนิ้วมือเหตุที่ผู้รับรู้เห็น (ไม่ประทับ = การรับรู้ใหม่ไม่มีผลเลย) + audit"),
    dict(file=SETTLE_POST, method="OrphanChildrenAsync", must=["SettlementOrphanTriage.MaxChildDepth", "seen.Add("],
         why="รอบ 201 ทีม ST (A-ST6): ใบที่อ้างทุกชั้น (ลูก → หลาน) ด้วยตัวโหลดเดียวกับ VoidDocumentAsync · กันวน"),
    # A-ST4: รายงานของกำพร้าระดับช่องทาง
    dict(file=SETTLE_POST, method="ChannelOrphanReportAsync",
         must=["OrphanArtifactsAsync(", "SettlementOrphanReport.Build("],
         must_re=[r"c\s*\.\s*CompanyId\s*==\s*companyId", r"d\s*\.\s*CompanyId\s*==\s*companyId", r"p\s*\.\s*CompanyId\s*==\s*companyId"],
         why="รอบ 201 ทีม ST (A-ST4): ตัวแยกเดียวกับด่านลงบัญชี · ทิศต่อผังพักจากตัวตัดสินเดียว · tenant ทุก query"),
    dict(file="Controllers/SettlementController.cs", method="ChannelOrphans",
         before=[("SettlementPermissionScope.OrphanAmountsHiddenReason(", "_posting.ChannelOrphanReportAsync(")],
         call_args=[("_posting.ChannelOrphanReportAsync(", "hidden")],
         forbid=["SettlementPermissionScope.CandidatesHiddenReason("],
         why="A-ST4 (D-P5): ผู้มีแค่สิทธิ์ดูเห็นรายการแต่ไม่เห็นยอด — เกณฑ์ตัวเดียวกับหน้ารอบโอน · ST-X7: ข้อความเฉพาะของรายงานกำพร้า (ไม่ยืมข้อความผู้สมัคร)"),
    # A-ST7: ผู้ตัดสินการจับคู่/จัดประเภทนับเป็นผู้ทำใน SoD
    dict(file=SETTLE_POST, method="BuildGateAsync",
         must=["SettlementLineMakers.Of(", "l.DecidedBy", "SettlementPlanFingerprint.IssuedDrift(", "SettlementContentOverlap.AgainstBatchesAsync(",
               "orphanFirstBatches"],
         call_args=[("SettlementSummarySupplement.SplitDuplicates(", "supContentHits"), ("DuplicateSalesAsync(", "orphans.Items")],
         before=[("OrphanArtifactsAsync(", "DuplicateSalesAsync("), ("SettlementLineMakers.Of(", "SettlementPostingGate.Evaluate(")],
         why="รอบ 201 ทีม ST: A-ST7 ผู้ตัดสินบรรทัดเข้าชุดผู้ทำของ SoD (พารามิเตอร์เดิมของ SodSelfApproval) · A-ST8 เตือนเอกสารที่ออกแล้วซึ่งเนื้อหาไม่ตรงแผน · "
             "C-9 (ข้อ 82) ตัวแยกของกำพร้ามาก่อน ⇒ ใบกำพร้าที่รับรู้แล้วเป็นใบแรกของวัน + ด่านเนื้อหาซ้ำเทียบบรรทัดของรอบเจ้าของ (รวมที่ถูกลบ)"),
    dict(file=SETTLE_LINES, method="AssignLineMatchAsync", call_args=[("SetMatch(", "userId")],
         why="A-ST7: ทุกการจับคู่โดยคนประทับผู้ตัดสิน"),
    dict(file=SETTLE_LINES, method="ReclassifyLineAsync", must=["MarkDecided(line, userId)", "MarkDecided(other, userId)"],
         why="A-ST7: การจัดประเภทโดยคน (รวม 'ใช้กับป้ายเดียวกัน') ประทับผู้ตัดสิน"),
    # A-ST8: ลายนิ้วมือตอนออกเอกสาร
    dict(file=SETTLE_POST, method="CreateOrAdoptAsync",
         must=["SettlementPlanFingerprint.PieceHash(gate.Plan, component)", "d.CompanyId == companyId"],
         before=[("_documents.CreateDocumentAsync(", "SettlementPieceFingerprint =")],
         why="รอบ 201 ทีม ST (A-ST8): ประทับลายนิ้วมือชิ้นแผนบนเอกสารที่เพิ่งสร้าง (ตัว canonical เดียว PieceHash)"),
    # C-9 (คำตัดสินข้อ 82)
    dict(file=SETTLE_POST, method="DuplicateSalesAsync",
         must=["SettlementSummarySupplement.AckedOrphanCountsAsFirst(d.Id, orphanItems)", "SettlementSummarySupplement.OrphanFirstDuplicates(",
               "IgnoreQueryFilters()"],
         must_re=[r"l\s*\.\s*CompanyId\s*==\s*companyId\s*&&\s*l\s*\.\s*ChannelId\s*==\s*channel\s*\.\s*Id"],
         why="C-9 (ข้อ 82): ใบสรุปกำพร้าที่รับรู้แล้วมีผล = ใบแรกของวัน · รายการเดียวกับรอบเจ้าของ (เลขรายการ/ออเดอร์) = รายได้ซ้ำ · tenant+ช่องทาง"),
    # คำถามค้าง DV Q1: ผลของการยกเลิกเอกสาร/การรับชำระในเส้นถอยรอบโอนต้องถึงผู้กด
    dict(file=SETTLE_POST, method="UnpostCoreAsync",
         must=["docVoid.EtaxCancellationFlag", "docVoid.OutputVatNotice", "voidResult.OutputVatNotice", "notices = etaxFlags"],
         must_re=[r"var\s+docVoid\s*=\s*await\s+_documents\s*\.\s*VoidDocumentAsync\s*\("],
         why="รอบ 201 ทีม ST (DV Q1): VoidDocumentAsync คืน PaymentVoidResult แล้ว — ทิ้งผล = ธง e-Tax/ภาษีขายที่ถอยไม่ได้หายเงียบจากผู้กดยกเลิกการลงบัญชี"),
    # ── รอบ 201 ทีม ST ฝ่ายค้าน (ST-X1..X7) ──
    dict(file=SETTLE_POST, method="UnpostCoreAsync",
         must=["SettlementUnpostNotice.Partial(batch.PayoutRef, ex.Message, voidedDocs.Count, voidedPayments.Count, etaxFlags)",
               "SettlementUnpostNotice.FlagsTail(etaxFlags)", "_db.Database.CurrentTransaction != null", "_db.AddChainedAuditLog("],
         must_lit=['action = "settlement-unpost-partial"', 'throw new BusinessRuleException(message, ex, "SETTLEMENT-UNPOST-PARTIAL", 409)'],
         before=[("SettlementUnpostNotice.Partial(", "throw new BusinessRuleException(message, ex,")],
         why="ST-X1: เส้นล้มกลางทางต้องพาธง e-Tax/ภาษีขายของชิ้นที่ยกเลิกไปแล้วถึงผู้กด + audit แถว partial (กดใหม่ชิ้นนั้นไม่ถูกยกเลิกซ้ำ ⇒ ธงไม่เกิดอีก) · "
             "audit ห้ามอยู่ในธุรกรรมที่ค้าง (rollback = หายเงียบ)"),
    dict(file="Helpers/SettlementPaymentOwner.cs", method="StampOnSave",
         must=["db.ChangeTracker.Tracked += tracked", "db.ChangeTracker.Tracked -= tracked", "e.Entry.State == EntityState.Added",
               "db.SavingChanges += saving", "db.SavingChanges -= saving"],
         why="ST-X2: ประทับตอนแถวเริ่มถูกติดตาม — ก่อน CaptureAuditEntries ⇒ audit \"Create Payment\" มีเจ้าของจริง · SavingChanges คงเป็นตาข่าย · ถอดทั้งคู่ตอน Dispose"),
    dict(file=SETTLE_POST, method="BuildGateAsync",
         must=["SettlementLineMakers.RolesOf(batch.CreatedBy, lines.Select(l => (l.CreatedBy, l.DecidedBy)), userId)", "SodRoles: sodRoles"],
         why="ST-X3: ข้อความแยกหน้าที่บอกบทบาทที่ชนจริง (ผู้สร้าง · ผู้เติมไฟล์ · ผู้ตัดสินจับคู่/จัดประเภท) — ข้อมูลชุดเดียวกับตัวตัดสิน SodSelfApproval"),
    dict(file=SETTLE_POST, method="DocumentOrphanAckStatusAsync",
         must=["SettlementOrphanReport.AckStatusOf(documentId, triage.Items)", "OrphanArtifactsAsync(companyId, null, ch,",
               "SettlementArtifactGuard.BatchIdFromCreator(doc.CreatedBy)"],
         must_re=[r"d\s*\.\s*CompanyId\s*==\s*companyId", r"b\s*\.\s*CompanyId\s*==\s*companyId"],
         why="ST-X4: แถบ \"รับรู้แล้ว\" บนหน้าเอกสารบอกว่าการรับรู้ยังมีผลไหม — ตัวแยกเดียวกับพรีวิว/ด่านลงบัญชี (ห้ามตัดสินเองจากช่องบนแถว) · tenant ทุก query"),
    dict(file=SETTLE_LINES, method="SyncIntentStampsAsync",
         must=["SettlementSaleMatch.SaleReferenceTakenByOtherBatch(", "takenByOtherBatch++", "NewlyReferencesIntent(l)",
               "!= SettlementPostingKind.Refund"],
         must_re=[r"if\s*\(\s*takenByOtherBatch\s*>\s*0\s*\)\s*throw\s+new\s+BusinessRuleException\b"],
         before=[("LockGatewaysAsync(", "NewlyReferencesIntent(l)")],
         why="ST-X6: ใต้ล็อก gateway — บรรทัดฝั่งขายที่เพิ่งอ้าง intent ซึ่งรอบอื่นเป็นเจ้าของแล้ว ⇒ 409 (เดิมข้ามเงียบไปติดด่านลงบัญชี) · คืนเงินยกเว้น · อ้างค้างไม่ทำให้ตัน"),
    dict(file="Data/DatabaseMigrationHelper.cs", method="ApplyMissingColumns",
         must=["PaymentOwnerColumnExists(db, logger)", "if (!paymentOwnerColumnExisted) ReportPaymentOwnerBackfill(db, logger)"],
         before=[("PaymentOwnerColumnExists(db, logger)", "GetAlterStatements()")],
         why="ST-X5: รู้ก่อนรันคำสั่งว่าบูตนี้สร้างคอลัมน์ (backfill ครั้งเดียว) ⇒ log จำนวนแถวที่มีป้ายแต่ไม่ผ่านหลักฐานเฉพาะบูตนั้น"),
    dict(file="Data/DatabaseMigrationHelper.cs", method="ReportPaymentOwnerBackfill",
         must=["SettlementPostingKeys.PaymentOwnerUnbackfilledCountSql()", "LogWarning("],
         forbid=["ExecuteSqlRaw(", "PaymentOwnerBackfillSql("],
         why="ST-X5: นับแล้ว log อย่างเดียว — ไม่ backfill รอบสอง (ป้ายใน Notes ไม่มีผลกับด่านใด)"),
]

# ── รอบ 201 ทีม OC (OCR · BACKLOG §1.7 A-OC1/A-OC2/A-OC5 + หมวด C-18..C-24 · คำตัดสินข้อ 91–97): เทสต์ล็อกตัวตัดสิน pure (OcrReview201OcTests)
#    ⇒ ล็อกจุดเรียก/ลำดับ/การใช้ผลใน service ที่นี่ ──
OCR_VI = "Services/Implementations/Ocr/VendorIntelligenceService.cs"
HYGIENE = "Controllers/ContactHygieneController.cs"
BRANCH_X = "Services/BranchCodeExtractor.cs"
RULES += [
    dict(file=OCR, method="SubmitCorrectionAsync",
         must=["OcrCorrectedFieldList.ShouldRememberKnownGood(", "StaleVendorContactNoteAsync(", "OcrCorrectedFieldList.VendorTaxIdTyped(",
               "result.VendorTaxIdUserChanged = true"],
         call_args=[("OcrCorrectedFieldList.VendorTaxIdTyped(", "prevVendorTaxId"),
                    ("OcrWhtLearningScope.Decide(", "WhtCorrectionsPredateBaseline")],
         must_re=[r"if\s*\(\s*rememberVendorName\s*\)\s*await\s+_knownGoodCorrector\s*\.\s*RememberUserCorrectionAsync",
                  r"if\s*\(\s*rememberVendorAddress\s*\)\s*await\s+_knownGoodCorrector\s*\.\s*RememberUserCorrectionAsync",
                  r"staleCorrectionNote\s*!=\s*null\s*\?\s*null\s*:\s*result\s*\.\s*MatchedContactId"],
         before=[("StaleVendorContactNoteAsync(", "DecideScanVendorBranchContactAsync("),
                 ("OcrCorrectedFieldList.VendorTaxIdTyped(", "StaleVendorContactNoteAsync(")],
         why="ฝ่ายค้าน OCX-1: ธง \"ผู้ใช้เปลี่ยนเลขผู้เสียภาษี\" เขียนจาก baseline (prevVendorTaxId) ก่อนด่าน C-23 · OCX-3: ผู้เรียกตัวที่สามของ OcrWhtLearningScope ส่งธง C-18 · "
             "A-OC1: known-good ชื่อ/ที่อยู่ผู้ขายจำเฉพาะเมื่อค่าเปลี่ยนจากที่สแกน (หน้าเว็บส่งค่าเดิมทุกครั้ง) · "
             "C-23: แก้เลขผู้เสียภาษีเป็นนิติบุคคลอื่น ⇒ ถอดการผูกเดิมก่อนตัดสินผู้ติดต่อใหม่"),
    dict(file=OCR, method="StaleVendorContactNoteAsync",
         must=["OcrVendorBranchContact.MatchedContactIsOtherEntity(", "OcrVendorBranchContact.StaleMatchNote(", "MatchedContactCorrectionField",
               "result.VendorTaxIdUserChanged"],
         must_re=[r"c\s*\.\s*CompanyId\s*==\s*companyId"],
         forbid_lit=['"VendorTaxId"'],
         call_args=[("OcrVendorBranchContact.MatchedContactIsOtherEntity(", "userTouchedTaxId")],
         why="C-23: ตัวตัดสินเดียวของสามเส้น (แก้ผลสแกน · สร้างเอกสาร · พรีวิว) · ผู้ใช้เลือกผู้ติดต่อเอง = ไม่ถอด · ถอดเฉพาะเมื่อผู้ใช้แตะเลข "
             "(เลขที่ OCR อ่านเพี้ยนแต่ผูกถูกราย ห้ามถูกถอด) · tenant"),
    dict(file=OCR, method="CreateDocumentFromScanCoreAsync",
         must=["StaleVendorContactNoteAsync(", "OcrSettlementCounterparty.Decide(", "WalkInSalesBlockAsync("],
         must_re=[r"!\s*vendorIsUs\s*&&\s*!\s*settlementInherited\s*&&",
                  r"WalkInSalesBlockAsync\s*\(\s*companyId\s*,\s*contactId\s*,\s*docType",
                  r"WalkInSalesBlockAsync\s*\(\s*companyId\s*,\s*contactId\s*,\s*document\s*\.\s*DocumentType\s*,\s*Math\s*\.\s*Max\s*\(\s*document\s*\.\s*VatAmount"],
         before=[("StaleVendorContactNoteAsync(", "DecideScanVendorBranchContactAsync("),
                 ("OcrSettlementCounterparty.Decide(", "DecideScanVendorBranchContactAsync(")],
         why="ฝ่ายค้าน OCX-2/5: ด่าน walk-in ตรวจคู่ค้าฝั่งขายจากทุกแหล่ง + ซ้ำด้วย VAT จริงหลังสร้างบรรทัด · C-23 ถอดผู้ติดต่อของเลขเดิมก่อนตัดสินสาขา · C-20 ใบรับ/จ่ายเงินสืบทอดผู้ติดต่อใบต้นทาง (สืบทอดแล้ว = ข้ามตัวตัดสินสาขา) · "
             "C-24 ใบกำกับเต็มรูปห้ามผูกลูกค้าเงินสด (ตรวจซ้ำตอนสร้าง)"),
    dict(file=OCR, method="PreviewDocumentLinesAsync",
         must=["StaleVendorContactNoteAsync(", "OcrSettlementCounterparty.Decide("],
         why="C-23/C-20: \"แก้ในฟอร์มก่อน\" ได้ผู้ติดต่อผู้ขายจากตัวตัดสินเดียวกับเส้นสร้างเอกสาร (CLAUDE.md §H สามเส้นตัวสร้างเดียว)"),
    dict(file=OCR, method="MatchWalkInBuyerAsync",
         must=["OcrWalkInBuyer.BlockReason(", "DocumentSide.IsSales(", "WalkInCustomerContact.GetOrCreateAsync(", "MatchContactCoreAsync(",
               "OcrVendorBranchContact.ScanAlreadyPosted("],
         must_re=[r"MatchContactCoreAsync\s*\([^;]*recordVendorCanonFeedback\s*:\s*false"],
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
         must=["HasPermissionAsync(", "PermissionKeys.ContactEdit", "ContactDataHygiene.OrphanRetireBlock(", "ReferencedContactIdsAsync(",
               "uid.ToString("],
         before=[("HasPermissionAsync(", "SaveChangesAsync("), ("ContactDataHygiene.OrphanRetireBlock(", "SaveChangesAsync(")],
         why="A-OC2: ลบแถวสาขาที่ OCR สร้างได้เฉพาะผู้มีสิทธิ์ Contact.Edit + ตรวจซ้ำที่เซิร์ฟเวอร์ว่ายังไม่มีอะไรอ้างถึง (ก่อนบันทึก)"),
    dict(file=HYGIENE, method="ReferencedContactIdsAsync",
         must=["WithholdingTaxCerts", "OcrScanResults", "Documents", "Cheques", "PostDatedChecks", "DepositTransactions", "PaymentIntents",
               "SettlementChannels", "Loans", "ContactCreditSettings", "BankReconciliationPatterns", "WhtCreditsReceived"],
         why="A-OC2 + ฝ่ายค้าน OCX-6: ตัวนับการอ้างถึงครอบทุกตารางที่มีช่องผู้ติดต่อ (เงิน/ธนาคาร/เช็ค/gateway/รอบโอน/สินเชื่อ/วงเงิน)"),
    dict(file=HYGIENE, method="AddContactRefsAsync",
         must_re=[r"x\s*\.\s*CompanyId\s*==\s*companyId"],
         must=["IgnoreQueryFilters("],
         why="OCX-6: tenant ที่ตัวนับต่อตารางที่เดียว · นับแถวที่ลบแล้วด้วย (ทิศปลอดภัย)"),
    dict(file=OCR, method="MatchContactCoreAsync",
         must=["DocumentSide.IsSales(", "MatchedContactCorrectionField"],
         must_re=[r"if\s*\(\s*vendorCanonLabel\s*&&\s*_feedbackRecorder"],
         why="ฝ่ายค้าน OCX-4: VendorCanon บันทึกเฉพาะการเลือกผู้ขาย (ฝั่งซื้อ · ไม่ใช่เส้น walk-in) — ฝั่งขายผู้ใช้เลือกผู้ซื้อ = คนละคำถาม"),
    dict(file=OCR, method="WalkInSalesBlockAsync",
         must=["OcrWalkInBuyer.BlockReason(", "IsWalkInCustomer"],
         must_re=[r"c\s*\.\s*CompanyId\s*==\s*companyId"],
         why="OCX-2: ตัวตรวจ walk-in ของเส้นสร้างเอกสาร (ทุกแหล่งคู่ค้า) · tenant"),
    dict(file=OCR, method="LinkPredecessorAsync",
         must=["OcrPredecessorPartyCheck.Judge("],
         why="ฝ่ายค้าน OCX-10: ผูกใบต้นทางเมื่อยังไม่รู้คู่ค้า ⇒ เทียบเลข/ชื่อบนกระดาษ (เลขคนละนิติบุคคล = บล็อก · ชื่อไม่ตรง = โน้ต)"),
]

# ── รอบ 201 ทีม TX (ภาษี/ด่านอนุมัติ/มัดจำ · BACKLOG §1.5): ด่านที่เทสต์ล็อกแค่ helper — ล็อกจุดเรียกใน service ──
_TX_WHY_S65 = "A-TX1: §65 ตรี ต้องเห็นก่อนกดอนุมัติ — ตัวประเมินเดียว (EvaluateSection65TerAsync) ของคำเตือนและธุรกรรม · ชุดชนิด/ข้อที่ยกขึ้น = Section65TerApprovalWarnings"
RULES += [
    dict(file=DOC, method="CollectApprovalWarningsAsync",
         must=["Section65TerApprovalWarnings.AppliesTo(", "Section65TerApprovalWarnings.For(", "EvaluateSection65TerAsync("],
         why=_TX_WHY_S65),
    dict(file=DOC, method="ApplySection65TerAsync",
         must=["EvaluateSection65TerAsync("],
         must_re=[r"if\s*\(\s*result\s*\.\s*HasHardBlock\s*\)\s*throw\b"],
         forbid=["Section65TerValidator.Evaluate("],
         why=_TX_WHY_S65 + " · ข้อที่บล็อกยังโยนในธุรกรรม"),
    dict(file=DOC, method="EvaluateSection65TerAsync",
         must=["Section65TerValidator.Evaluate(", "a.CompanyId == companyId && accIds.Contains(a.Id)"],
         forbid=["doc.NonDeductibleAmount ="],
         why=_TX_WHY_S65 + " · ตัวประเมินอ่านอย่างเดียว (ไม่แตะเอกสาร) + tenant"),
    dict(file=DOCSVC, method="ApproveDocumentAsync#2",
         must=["Section65TerApprovalWarnings.AppliesTo(", "ApprovalControlPolicy.SelfApprovalBlocked(", "ApprovalControlPolicy.RecordsShadow(",
               "ApprovalControlPolicy.SelfApproval(", "ApprovalControlPolicy.DocumentUnknownMakerMode", "TaxPointResolver.KindForApproval(doc)"],
         before=[("ApprovalControlPolicy.SelfApprovalBlocked(", "BeginTransactionAsync(")],
         forbid=["string.Equals(doc.CreatedBy", "doc.CreatedBy == approvedBy"],
         why="A-TX1 ชุดชนิด §65 ตรีตัวเดียว · A-TX3 SoD ตัวตัดสินเดียว + ร่องรอยโหมดเงา · A-TX2 tax point §83/6 = วันจ่าย"),
    dict(file="Helpers/SettlementPosting.cs", method="SodSelfApproval#0",
         must=["ApprovalControlPolicy.SelfApproval(", "ApprovalControlPolicy.BlocksSettlementPosting("],
         forbid=["string.Equals(", "IsNullOrWhiteSpace(batchCreatedBy)"],
         why="A-TX3 (คำตัดสินข้อ 45): ห้ามมีสูตร SoD ชุดที่สองนอก ApprovalControlPolicy"),
    dict(file="Helpers/SettlementPosting.cs", method="SodSelfApproval#1",
         must=["ApprovalControlPolicy.SelfApproval(", "ApprovalControlPolicy.BlocksSettlementPosting("],
         forbid=["string.Equals(", "lineCreators.Any("],
         why="A-TX3 (คำตัดสินข้อ 45): ห้ามมีสูตร SoD ชุดที่สองนอก ApprovalControlPolicy"),
    dict(file=DOC, method="LockDepositBalancesAsync",
         must=["DepositKindDocumentRules.LockReloadPlan(", "_depositLockedIds.UnionWith("],
         call_args=[("LockReloadPlan(", "_depositLockedIds")],
         must_re=[r"if\s*\(\s*plan\s*\.\s*ModifiedBeforeLock\s*\.\s*Count\s*>\s*0\s*\)\s*throw\b"],
         before=[("TryXactLockAsync(", "LockReloadPlan(")],
         why="A-TX5: แถวมัดจำที่ถูกแก้ก่อนล็อกต้องล้มดัง (เดิมข้ามเงียบ = lost update)"),
    dict(file=DOC, method="UnrealizeDrivesDepositAsync",
         must=["ParseDepositRefs(doc.DepositAppliedRef).Length", "split.UnattributedUndue"],
         call_args=[("SplitDrivesUnrealize(", "referencedCount")],
         why="A-TX6: นับจากจำนวนเลขบนใบ ไม่ใช่จำนวนที่ resolve ได้ · ขา 21913 ที่ผูกไม่ได้ต้องบอกให้เห็น"),
    dict(file=DOC, method="RealizeDepositCoreAsync",
         must=["DepositKindDocumentRules.RealizeDateOrToday("], forbid=["RealizeDate ?? DateTime.UtcNow"],
         why="A-TX7: วันที่รับรู้/ริบตามปฏิทินไทย (เดิม UtcNow ⇒ เช้ามืดวันที่ 1 ได้เดือนก่อน)"),
    dict(file=DOC, method="IssueForfeitTaxInvoiceAsync",
         must=["DepositKindDocumentRules.RealizeDateOrToday("], forbid=["RealizeDate ?? DateTime.UtcNow"],
         why="A-TX7: วันที่ใบกำกับของยอดที่ริบตามปฏิทินไทย"),
    dict(file="Services/Implementations/JournalAnomalyService.cs", method="ScanAsync",
         must=["WhtCertFilingScope.Filed", "b.CompanyId == companyId", "w.CompanyId == companyId"],
         call_args=[("JournalPostingGuard.Validate(", "externalWhtBase")],
         why="A-TX9: JE รอบโอนออกภาษีแทนใช้เงินได้ตาม 50 ทวิ ของใบค่าธรรมเนียมเป็นฐาน JE-WHT-RATIO · tenant"),
    dict(file="Services/Implementations/StatutoryRemittanceService.cs", method="BuildItem",
         must=["TaxFilingDeadline.WarnBy(", "TaxFilingDeadline.EFilingCaveat("], forbid=["today > efiling.Date"],
         why="B-7: ภ.พ.36/ภ.ง.ด.54 เตือนตามวันที่เร็วกว่าจนกว่าจะยืนยันมาตรการ e-Filing — ตัวตัดสินเดียว TaxFilingDeadline.WarnBy"),
    dict(file="Services/Implementations/StatutoryRemittanceService.cs", method="BuildCell",
         must=["TaxFilingDeadline.WarnBy(", "TaxFilingDeadline.EFilingCaveat("], forbid=["today > efiling.Date", "(efiling.Date - today)"],
         why="B-7: ช่องปฏิทินนำส่งนับวัน/เลยกำหนดจาก TaxFilingDeadline.WarnBy ตัวเดียว"),
    # ── ฝ่ายค้านรอบ 201 ทีม TX (RTX-1/5/6 · คำตัดสินข้อ 110) ──
    dict(file=EXPENSE, method="MarkAsPaidAsync",
         must=["LinkedPayVoucher.StepFor(", "claim.PaymentVoucherDocumentId = pvId", "LinkedPayVoucher.WarningsMessage(",
               "Section65TerApprovalWarnings.PassedNotes(passedWarnings)"],
         call_args=[("_documentService.ApproveDocumentAsync(", "passedWarnings: passedWarnings")],
         must_re=[r"catch\s*\(\s*DocumentApprovalWarningsException\s+ex\s*\)\s*\{\s*throw\b"],
         before=[("claim.PaymentVoucherDocumentId = pvId", "_documentService.ApproveDocumentAsync(")],
         why="RTX-5: กดจ่ายใบเบิกซ้ำใช้ใบร่างเดิม (ผูกก่อนอนุมัติ) · คำเตือนที่ต้องมีคนรับทราบ = ข้อความไทยพร้อมทางไปต่อ ไม่ใช่ 500"),
    dict(file=APIV1, method="Approve",
         must=["Section65TerApprovalWarnings.IsWarning", "nonDeductibleExpense ="],
         why="RTX-1 (ข้อ 110): API v1 ไม่ถูกบล็อกด้วยข้อสังเกต §65 ตรี — อนุมัติแล้วคืนธงในผลตอบ"),
    dict(file="Services/Implementations/Tax/TaxComplianceChecker.cs", method="CheckAsync",
         must=["PlatformHolidayStore.LoadSetAsync("], call_args=[("DeadlineFor(", "holidays")],
         why="RTX-6: ตัวตรวจรายงานใช้วันหยุดราชการชุดเดียวกับหน้านำส่ง/ปฏิทิน"),
    # ── ฝ่ายค้านรอบ 201 ทีม TX รอบสาม (P1-2 · P2-2/3/4/6) ──
    dict(file=DOC, method="EvaluateSection65TerAsync",
         must=["a.AccountType", "accountTypes: accTypes"],
         why="P1-2: ตัวประเมิน §65 ตรีตัดสิน 'รายจ่ายไหม' ด้วยชนิดผัง (ผัง 6100 ค่าปรับ ชนิด Expense ต้องถูกตรวจ) — ไม่ส่งชนิด = ตกไปใช้เลขนำหน้า 5 เงียบ"),
    dict(file="Services/Implementations/Tax/Section65TerValidator.cs", method="Evaluate",
         must=["IsProfitAndLossAccount(code, accountType)", "IsExpenseLedger(code, accountType)"],
         why="P1-2: ข้อ (6)(6 ทวิ)(1)(2)(3)(5) ตัดสินผ่านชนิดผังก่อน เลขนำหน้าเป็นทางสำรอง"),
    dict(file=DOCSVC, method="ApproveDocumentAsync#2",
         must=["ApprovalAcknowledgement.LegalReference(warnings)", "passedWarnings.Add(w)"],
         must_lit=["legalReference = Accounting.Helpers.ApprovalAcknowledgement.LegalReference(warnings)"],
         why="P2-4 (กฎ M): ร่องรอยอนุมัติทั้งที่มีคำเตือนอ้างมาตราของชุดที่ผ่านจริง · P2-2: ส่งชุดที่ผ่านคืนผู้เรียกที่มีคนรอ"),
    dict(file="Controllers/OcrController.cs", method="CreateDocument",
         must=["Section65TerApprovalWarnings.PassedNotice(passed)"], must_lit=["[APPROVE-S65-NOTE]"],
         call_args=[("_documentService.ApproveDocumentAsync(", "passedWarnings: passed")],
         why="P2-2: สร้าง+อนุมัติจากหน้าสแกน — ข้อสังเกต §65 ตรีที่ผ่าน (ไม่บล็อก) กลับไปถึงคนที่กดใน ProcessingNotes"),
    dict(file="Services/Implementations/LineBotService.cs", method="HandleMessageAsync",
         must=["Section65TerApprovalWarnings.PassedNotice(passed)"],
         call_args=[("_docService.ApproveDocumentAsync(", "passedWarnings: passed")],
         why="P2-2: คำสั่ง 'จ่าย…' ในแชท — ข้อสังเกต §65 ตรีที่ผ่านตอบกลับในข้อความ LINE"),
    dict(file="Services/Implementations/LineBotService.cs", method="HandlePostbackAsync",
         must=["Section65TerApprovalWarnings.PassedNotice(passed)"],
         call_args=[("_docService.ApproveDocumentAsync(", "passedWarnings: passed")],
         why="P2-2: ปุ่มอนุมัติในแชท — ข้อสังเกต §65 ตรีที่ผ่านตอบกลับในข้อความ LINE"),
    dict(file="Services/Implementations/SalaryAdvanceService.cs", method="DisburseAsync",
         must=["LinkedPayVoucher.StepFor(", "advance.DisbursementDocumentId = pvId", "LinkedPayVoucher.WarningsMessage("],
         must_re=[r"catch\s*\(\s*DocumentApprovalWarningsException\s+ex\s*\)\s*\{\s*throw\b"],
         before=[("advance.DisbursementDocumentId = pvId", "_documentService.ApproveDocumentAsync(")],
         why="P2-3 (คลาสเดียวกับ RTX-5): กดจ่ายเงินทดรองซ้ำใช้ใบร่างเดิม · คำเตือน = ข้อความไทยพร้อมทางไปต่อ · ตัวตัดสินตัวเดียวกับใบเบิก"),
    dict(file=LODGING_LIFE, method="CreateDepositReceiptAsync",
         must=["ApprovalAckSource.SystemWorkflow"], forbid=["acknowledgeWarnings: true"],
         why="P2-6: เส้นอัตโนมัติไม่มีคนเห็นคำเตือน ⇒ ห้ามประทับว่าคนรับทราบ"),
    dict(file=LODGING_LIFE, method="CheckOutCoreAsync",
         must=["ApprovalAckSource.SystemWorkflow"], forbid=["acknowledgeWarnings: true"],
         why="P2-6: เส้นอัตโนมัติไม่มีคนเห็นคำเตือน ⇒ ห้ามประทับว่าคนรับทราบ"),
    dict(file=LODGING_LIFE, method="ReissueFinalDocumentCoreAsync",
         must=["ApprovalAckSource.SystemWorkflow"], forbid=["acknowledgeWarnings: true"],
         why="P2-6: เส้นอัตโนมัติไม่มีคนเห็นคำเตือน ⇒ ห้ามประทับว่าคนรับทราบ"),
    dict(file=LODGING_LIFE, method="ReceiveSecurityDepositAsync",
         must=["ApprovalAckSource.SystemWorkflow"], forbid=["acknowledgeWarnings: true"],
         why="P2-6: เส้นอัตโนมัติไม่มีคนเห็นคำเตือน ⇒ ห้ามประทับว่าคนรับทราบ"),
    dict(file=PLATFORM, method="IssuePaidReceiptAsync",
         must=["ApprovalAckSource.SystemWorkflow"], forbid=["Actor, true)"],
         why="P2-6: ใบเสร็จค่าบริการแพลตฟอร์มออกอัตโนมัติ ⇒ ห้ามประทับว่าคนรับทราบ"),
    dict(file=PLATFORM, method="IssueRenewalInvoiceAsync",
         must=["ApprovalAckSource.SystemWorkflow"], forbid=["Actor, true)"],
         why="P2-6: ใบแจ้งหนี้ต่ออายุออกอัตโนมัติ ⇒ ห้ามประทับว่าคนรับทราบ"),
    dict(file=PLATFORM, method="IssueUsageInvoiceAsync",
         must=["ApprovalAckSource.SystemWorkflow"], forbid=["Actor, true)"],
         why="P2-6: ใบแจ้งหนี้ค่าใช้งานออกอัตโนมัติ ⇒ ห้ามประทับว่าคนรับทราบ"),
    dict(file=CMS_BOOK, method="SyncBookingToErpAsync",
         must=["ApprovalAckSource.SystemWorkflow"], forbid=["acknowledgeWarnings: true"],
         why="P2-6: การจองหน้าร้านออกเอกสารอัตโนมัติ ⇒ ห้ามประทับว่าคนรับทราบ"),
    dict(file="Services/Implementations/CmsCommerceService.cs", method="ApproveOrderDocumentIfPendingAsync",
         must=["ApprovalAckSource.SystemWorkflow"], forbid=["acknowledgeWarnings: true"],
         why="P2-6: ยืนยันชำระคำสั่งซื้อออนไลน์ออกเอกสารอัตโนมัติ ⇒ ห้ามประทับว่าคนรับทราบ"),
]

# ── รอบ 201 ทีม IN (ฝ่ายค้าน X1–X7): แถวกลับรายการไม่เข้าคิว · ถัวเฉลี่ยติดตามมูลค่า · ยอดยกมาซ้ำไม่ลบแถว · มูลค่า/เบิกใช้ผ่านตัวคิดต้นทุนเดียว ·
#    ซ่อมยอดถือล็อกคลัง · ล็อกต่อการจอง (เทสต์ pure ใน InventoryCostingMethodTests · LodgingCheckoutReissueTests) ──
RULES += [
    dict(file="Helpers/InventoryCostFlow.cs", method="FifoQueue",
         must=["CancelReversals("],
         why="X1: แถวกลับรายการ (ยกเลิกเอกสาร/บิล POS/ล้างยอดยกมา) ต้องถูกจับคู่ตัดก่อนสร้างคิว FIFO"),
    dict(file="Helpers/InventoryCostFlow.cs", method="LastLayerCost",
         must=["CancelReversals("],
         why="X1: ต้นทุนสำรองต้องไม่มาจากล็อตที่ถูกยกเลิก"),
    dict(file="Services/Implementations/Inventory/InventoryCostingService.cs", method="LoadCostMovementsAsync",
         must=["m.DocumentId, m.Reference"],
         why="X1: คีย์จับคู่แถวกลับรายการต้องถูกส่งเข้าแถวต้นทุน (ไม่ส่ง = CancelReversals ไม่เห็นคู่)"),
    dict(file="Services/Implementations/Inventory/InventoryCostingService.cs", method="ResolveValuationUnitCostAsync",
         must=["InventoryCostFlow.FifoQueue(", "InventoryCostFlow.RemainingFifoUnitCost(", "p.CompanyId == companyId"],
         why="X5: มูลค่าคงเหลือตามวิธีของสินค้า ตัวเดียว (FIFO = ล็อตที่เหลือ) · tenant"),
    dict(file="Services/Implementations/ProductService.cs", method="GetInventoryValuationAsync",
         must=["_costing.ResolveValuationUnitCostAsync("],
         forbid=["movements.Sum(m => m.Quantity * m.UnitCost)"],
         why="X5: รายงานมูลค่าคงเหลือห้ามเฉลี่ยแถว IN เอง — ผ่านตัวคิดต้นทุนตามวิธีของสินค้า"),
    dict(file="Services/Implementations/ProductService.cs", method="UseSuppliesAsync",
         must=["move.UnitCostUsed"],
         forbid=["UnitCostOverride: avgCost", "inMovements.Sum("],
         why="X5: เบิกวัสดุใช้ต้นทุนที่ ledger ถามตัวคิดต้นทุน (ถัวเฉลี่ย/FIFO) — ห้ามคิดค่าเฉลี่ยเองข้ามบริษัท"),
    dict(file="Services/Implementations/ImportExportService.cs", method="ImportStockOpeningAsync",
         must=["_stock.MoveAsync("],
         forbid=["RemoveRange(existingOpening)", "StockMovements.Remove("],
         why="X4: นำเข้ายอดยกมาซ้ำห้ามลบแถวเดิม — เขียนแถว OPENING ติดลบหักล้างผ่าน IStockLedger"),
    dict(file="Services/Implementations/Inventory/StockLedger.cs", method="RepairProductTotalsAsync#0",
         must=["pairs.OrderBy("],
         before=[("pairs.OrderBy(", "LoadTotalsEvidenceAsync(")],
         why="X6: ถือล็อก (คลัง, สินค้า) ทุกคลังของสินค้าก่อนอ่านหลักฐาน — คีย์เดียวกับ MoveAsync"),
]

# ── รอบ 198 ทีม C: ทั้งโฟลเดอร์ Services/Settlement/** ห้ามประกอบ JE เอง (ทีม B เขียนไฟล์ในโฟลเดอร์เดียวกัน) ──
# ── รอบ 201 ทีม AI (A-AI1..A-AI8 · AI/ธนาคาร): เทสต์ล็อกตัวตัดสิน pure (BankAiCandidateGuardTests · BankPatternEvidenceTests ·
#    BankMatchSingleStandardTests · AiKillSwitchOrchestratorTests · BulkPvStudentTests · AiFeedbackRecorderDiscardTests) ⇒ ล็อกจุดเรียกที่นี่ ──
_AI_BULKBANK = "Services/Implementations/Bank/BulkBankAiMatchService.cs"
_AI_BANK = "Services/Implementations/BankService.cs"
_AI_LEARN = "Services/Implementations/BankService.Learning.cs"
_AI_DOCAI = "Services/Ai/DocumentAiAugmenter.cs"
_AI_ORCH = "Services/Ai/AiOrchestrator.cs"
_AI_REC = "Services/Ai/AiFeedbackRecorder.cs"
_AI_WHY7 = ("A-AI7 (H-9): candidateId/bankTxnId ที่ AI แต่งต้องถูกตัดก่อนแย่งคู่จริง (ก่อน AiValidated/dedup) และห้ามพกยอดของ AI ถึงจอ — "
            "ตัวตัดสิน Helpers/BankAiCandidateGuard ตัวเดียว · ข้อเสนอที่ถูกตัดต้องถูกนับในคำเตือน (ไม่หายเงียบ)")
_AI_WHY1 = ("A-AI1 (H-1): คลังจับคู่ธนาคารนับความมั่นใจเฉพาะคำยืนยันที่ผู้ใช้เลือกคู่เอง (Explicit) — แหล่งของคำยืนยันต้องเดินจาก"
            "คำขอถึง UpsertPatternAsync ทุกทางเข้า (1:1 · batch · กลุ่ม M:N) · ไม่ส่ง = Implicit")
_AI_WHY_REC = "A-AI8 (คำตัดสินข้อ 58): ตัวบันทึก feedback ใช้ context ร่วม — catch ต้องถอยการแก้ของตัวเอง ไม่งั้น SaveChanges ถัดไปของผู้เรียกล้มตาม"
RULES += [
    dict(file=_AI_BULKBANK, method="ProposeAsync",
         must=["ScreenAiMatches(aiParsed", "BankAiCandidateGuard.Screen(", "BankAiCandidateGuard.PlanWarning(",
               "realAmountById[key]", "recordIndexOfMatch.Add("],
         before=[("ScreenAiMatches(aiParsed", "aiBankCandPairs.Add("),
                 ("ScreenAiMatches(aiParsed", "DeduplicateMatches(parsed)")],
         forbid=["? ra2 : c.Amount", "recordIndexOfMatch[mi]"],
         why=_AI_WHY7),
    dict(file=_AI_BULKBANK, method="ScreenAiMatches",
         must=["BankAiCandidateGuard.Screen(", "BankAiCandidateGuard.CapConfidence(", "BankAiCandidateGuard.PlanWarning(",
               "BankAiCandidateGuard.RealType("],
         why=_AI_WHY7),
    dict(file=_AI_BANK, method="ValidateMatchAmountAsync",
         must=["BankAiCandidateGuard.MissingIds(ids, foundIds)", "BankAiCandidateGuard.MissingIds(declaredPaymentIds, payIds)",
               "BankAiCandidateGuard.MissingIds(declaredJournalEntryIds"],
         must_re=[r"if\s*\(\s*missingIds\s*\.\s*Count\s*>\s*0\s*\)\s*throw\b"],
         before=[("BankAiCandidateGuard.MissingIds(", "BankMatchAmountReconciler.Reconcile(")],
         why="A-AI7 ฝั่งเขียน: เส้น batch/api ยืนยันคู่ต้องตรวจว่าทุก id มีอยู่จริงในบริษัทนี้ก่อนกระทบยอด (เส้น 1:1 ตรวจอยู่แล้ว)"),
    dict(file=_AI_LEARN, method="UpsertPatternAsync",
         must=["BankPatternEvidence.ExplicitIncrement(source)", "existing.ExplicitConfirmCount += explicitInc",
               "ExplicitConfirmCount = explicitInc"],
         why=_AI_WHY1),
    dict(file=_AI_LEARN, method="CaptureConfirmedMatchAsync",
         call_args=[("UpsertPatternAsync(", "source")],
         why=_AI_WHY1),
    dict(file=_AI_LEARN, method="RecordReconciliationPatternsAsync",
         call_args=[("UpsertPatternAsync(", "itemSource")],
         why=_AI_WHY1),
    dict(file=_AI_BANK, method="ReconcileAsync",
         call_args=[("CaptureConfirmedMatchAsync(", "ParseSource")],
         must=["BankPatternEvidence.ParseSource(request.Source)"],
         why=_AI_WHY1),
    dict(file="Services/Implementations/BankService.AiReconciliation.cs", method="BatchReconcileAsync",
         call_args=[("CaptureConfirmedMatchAsync(", "ParseSource"), ("ValidateMatchAmountAsync(", "declaredPaymentIds")],
         must=["BankPatternEvidence.ParseSource(item.Source)"],
         why=_AI_WHY1),
    dict(file="Services/Implementations/BankService.Reconciliation.cs", method="CreateReconciliationGroupAsync",
         must=["BankPatternEvidence.ParseSource(mi.Source)"],
         call_args=[("RecordReconciliationPatternsAsync(", "sourceByItem")],
         why=_AI_WHY1),
    dict(file="Services/Ai/Distillation/BankMatchDistillationModel.cs", method="LoadFromFeedbackAsync",
         must=["BankPatternEvidence.StudentConfidence(", "x.ExplicitConfirmCount"],
         forbid=["Wilson(x.TimesConfirmed"],
         why=_AI_WHY1 + " · นักเรียนห้ามนับ TimesConfirmed เป็นหลักฐาน (กดผ่านรัว ๆ ดัน Wilson ทะลุ 0.85)"),
    dict(file=_AI_LEARN, method="GetLearnedSuggestionsAsync",
         must=["BankPatternEvidence.Relevance(", "BankMatchScorer.Score(", "BankMatchArbiter.Decide(options)"],
         must_re=[r"autoSelect\s*=\s*isChosen\s*&&\s*decision\s*\.\s*Verdict\s*==\s*Accounting\s*\.\s*Helpers\s*\.\s*BankMatchVerdict\s*\.\s*Apply"],
         forbid=["p.TimesConfirmed / 10.0", "0.2 * Math.Min("],
         why="A-AI3 (H-5): ข้อเสนอจากประวัติให้คะแนนด้วย BankMatchScorer + ตัดสินติ๊กด้วย BankMatchArbiter เหมือนทุกเส้น (ห้ามสูตรที่ 3)"),
    dict(file="Services/Implementations/BankService.MatchCandidates.cs", method="RankCandidates",
         must=["BankMatchScorer.DepositPreference("],
         before=[("OrderByDescending(c => c.Score)", "BankMatchScorer.DepositPreference(")],
         why="A-AI3 · ฝ่ายค้าน X-6: ลำดับรายการ (จอ + SuggestMatchAsync) = คะแนนก่อน แล้วเงินลงที่ไหนเป็นลำดับรอง"),
    dict(file="Services/Implementations/BankService.MatchCandidates.cs", method="GetMatchCandidatesAsync",
         must=["RankCandidates(rankedPaymentsSeq", "RankCandidates(rankedJournalsSeq"],
         forbid=["score = Math.Min(100, score +", "score = Math.Max(0, score -"],
         why="A-AI3 (H-5): ตัวเลขบนจอ = ตัวเลขที่ arbiter ใช้ประทับ — หลักฐานเงินลงที่ไหนเป็นลำดับรองเท่านั้น ห้ามบวกเข้าคะแนนเฉพาะจอ"),
    dict(file=_AI_ORCH, method="AskAsync",
         must=["progress.Request", "fromLocalModel: progress.HasLocal"],
         why="A-AI6: ตาข่ายชั้นนอกของ orchestrator ต้องคืนคำตอบนักเรียนที่ทำนายไว้แล้ว (ฐานข้อมูล/provider ล่ม = ยังมีคำตอบ)"),
    dict(file=_AI_ORCH, method="AskInternalAsync",
         must=["LoadSiteSettingsAsync(ct)", "LoadActiveProviderAsync(ct)", "progress.HasLocal = true"],
         before=[("TryPredictLocalAsync(", "LoadSiteSettingsAsync(")],
         forbid=["_db.AiProviderConfigs", "_db.SiteSettings"],
         why="A-AI6: เทสต์ kill-switch ป้อนค่าผ่าน seam ตัวเดียวกับเส้นจริง — อ่านฐานข้อมูลตรงในเมธอดนี้ = เทสต์ไม่ได้ตรวจเส้นจริงอีก"),
    dict(file=_AI_DOCAI, method="ParseBulkPvResponse",
         must=["AiAnswerSource.Of(resp.UsedAi, resp.FromLocalModel)", "FromStudent: resp.FromLocalModel",
               "AiAnswerSource.UnreadableMessage(who)"],
         must_re=[r"resp\s*\.\s*RawResponseJson\s*\?\?\s*\(\s*resp\s*\.\s*UsedAi\s*\?"],
         forbid=["resp.RawResponseJson ?? resp.PrimaryAnswer"],
         why="A-AI4 (H-6): คำตอบนักเรียนแบบมีโครงต้องใช้ได้ · คำเดี่ยวห้าม parse เป็น JSON · ข้อความบอกผู้ตอบจริง (ห้ามโทษ AI ที่ไม่ได้ถูกถาม)"),
    dict(file=_AI_DOCAI, method="ParseBulkApprovalResponse",
         must=["AiAnswerSource.Of(resp.UsedAi, resp.FromLocalModel)", "FromStudent: resp.FromLocalModel",
               "AiAnswerSource.UnreadableMessage(who)"],
         forbid=["resp.RawResponseJson ?? resp.PrimaryAnswer"],
         why="A-AI4 คลาสเดียวกัน (แก้ที่หนึ่ง grep ทั้งเรพ): bulk คำเตือนอนุมัติ"),
    dict(file=_AI_DOCAI, method="SuggestAllPaymentVoucherAccountingAsync",
         must=["PaymentVoucherAccountingDistillationModel.BuildPerLineInputJson(", "ParseBulkPvResponse(resp, lines, _logger)"],
         call_args=[("SynthesiseChildFeedbackAsync(", "answerFromAi")],
         why="A-AI4: กุญแจแถว feedback ลูก = กุญแจที่นักเรียนแตก (ตัวสร้างเดียว) · คำตอบนักเรียนห้ามบันทึกเป็นคำตอบครู (คลังสอนตัวเอง)"),
    dict(file="Services/Ai/BankAiAugmenter.cs", method="SuggestStatementMatchAsync",
         must=["FromStudent: resp.FromLocalModel"],
         why="A-AI2 (H-4): ธงนักเรียนต้องเดินทางถึงผู้บริโภค (BankFeedService)"),
    dict(file="Services/Implementations/BankFeedService.cs", method="TryAutoMatchAsync",
         must=["aiResult.HasModelAnswer", "BankMatchArbiter.ScreenAiProposals("],
         forbid=["!aiResult.UsedAi"],
         why="A-AI2 (H-4): ด่าน 'มีคำตอบให้ใช้ไหม' = HasModelAnswer + เกณฑ์ตัวเลข/candidate set (ScreenAiProposals) — UsedAi ทิ้งคำตอบนักเรียนตอนปิด provider"),
    dict(file="Services/Implementations/DocumentService.cs", method="SuggestPaymentVoucherAccountingAsync",
         must=["GlSuggestionApplyPolicy.MayAutoFill(", "MayAutoFill: mayAutoFill", "AiAnswerSource.LabelWithRules("],
         why="A-AI5 (H-7): เกณฑ์เติมผังให้เอง (≥0.70 + ผังของบริษัท) ตัดสินที่เซิร์ฟเวอร์ — ทางเข้าอื่น (มือถือ/สคริปต์) ได้ด่านเดียวกัน"),
    dict(file=_AI_REC, method="RecordUserChoiceAsync", must=["DiscardUnsaved(_db, row)"], why=_AI_WHY_REC),
    dict(file=_AI_REC, method="BumpTenantReviewAsync", must=["DiscardUnsaved(_db, target)"], why=_AI_WHY_REC),
    dict(file=_AI_REC, method="UpsertTenantRollupAsync", must=["DiscardUnsaved(_db, row)"], why=_AI_WHY_REC),
    dict(file=_AI_REC, method="UpsertDailyRollupAsync", must=["DiscardUnsaved(_db, row)"], why=_AI_WHY_REC),
]
# ── จบบล็อกรอบ 201 ทีม AI ──

# ── รอบ 202 ทีม LS: หน้าตั้งค่าที่พัก — ด่านที่เดิม "แก้ค่าให้เงียบ" ต้องปฏิเสธ · id อ้างอิงต้องเป็นของบริษัท/ที่พักนี้ · สถานะเปิดจองออนไลน์
#    จากตัวตัดสินเดียว · ค่าตั้งต้นที่พักใหม่จาก entity (เทสต์ pure: LodgingSettingsRound202Tests) ──
_LS_WHY = "รอบ 202 ทีม LS: "
RULES += [
    dict(file=LODGING, method="Apply",
         must=["LodgingTimeOfDay.Parse(", "LodgingSettingsRules.EnsureNightsRange("],
         before=[("LodgingTimeOfDay.Parse(", "p.CheckInTime = checkIn"),
                 ("LodgingSettingsRules.EnsureNightsRange(", "p.MaxNights = d.MaxNights")],
         forbid=["Math.Max(p.MinNights", "TryParseExact("],
         why=_LS_WHY + "S-P1-2/S-P2-7 เวลาอ่านไม่ได้/พักสูงสุด < ขั้นต่ำ ⇒ ปฏิเสธพร้อมป้ายบนจอ ก่อนแตะ entity (ห้ามแทน 14:00 / ยกค่าเงียบ ๆ)"),
    dict(file=LODGING, method="CreatePropertyAsync",
         must=["EnsurePropertyRefsBelongAsync("],
         before=[("EnsurePropertyRefsBelongAsync(", "_db.SaveChangesAsync(")],
         why=_LS_WHY + "S-P2-8 เว็บ/นโยบายยกเลิกที่อ้างต้องเป็นของบริษัท/ที่พักนี้ก่อนบันทึก"),
    dict(file=LODGING, method="UpdatePropertyAsync",
         must=["EnsurePropertyRefsBelongAsync("],
         call_args=[("EnsurePropertyRefsBelongAsync(", "prevSiteId")],
         before=[("EnsurePropertyRefsBelongAsync(", "_db.SaveChangesAsync(")],
         why=_LS_WHY + "S-P2-8 เว็บ/นโยบายยกเลิกที่อ้างต้องเป็นของบริษัท/ที่พักนี้ (ตรวจเมื่อค่าเปลี่ยน — ค่าเดิมส่งจากของเดิม)"),
    dict(file=LODGING, method="EnsurePropertyRefsBelongAsync",
         must=["s.CompanyId == companyId", "x.CompanyId == companyId", "x.PropertyId == p.Id"],
         why=_LS_WHY + "S-P2-8 tenant + ที่พักเดียวกัน (กฎ M)"),
    dict(file=LODGING, method="SaveRatePlanAsync",
         must=["LodgingSettingsRules.EnsureNightsRange(", "LodgingSettingsRules.EnsureDateRange(",
               "r.CompanyId == companyId && r.PropertyId == dto.PropertyId", "c.CompanyId == companyId && c.PropertyId == dto.PropertyId"],
         before=[("LodgingSettingsRules.EnsureDateRange(", "_db.SaveChangesAsync(")],
         why=_LS_WHY + "S-P2-7/S-P2-8 แผนราคาช่วงกลับหัว = แผนตายเงียบ · ประเภทห้อง/นโยบายต้องเป็นของที่พักนี้"),
    dict(file=LODGING, method="SaveRoomTypeAsync",
         must=["LodgingSettingsRules.NormalizeExtraBed(", "r.ExtraBedPrice = extraBed.PricePerPersonNight",
               "r.MaxExtraBeds = extraBed.MaxExtraBeds"],
         forbid=["r.ExtraBedPrice = dto.ExtraBedPrice"],
         why=_LS_WHY + "คำตัดสินข้อ 123 ติ๊กเตียงเสริมแล้วจำนวน 0/ราคาว่าง ⇒ ปฏิเสธ (ห้ามบันทึกตรงจาก dto)"),
    dict(file=LODGING, method="ToDtoAsync",
         must=["LodgingPublicReadiness.Evaluate(", "PublicBookingStatus = readiness.Status", "PublicBookingFix = readiness.FixHint"],
         why=_LS_WHY + "W-01 สถานะเปิดจองออนไลน์ + เหตุผล + ทางแก้ จากตัวตัดสินเดียว (หน้าเว็บแสดงอย่างเดียว)"),
    dict(file=LODGING, method="GetPropertyDefaultsAsync",
         must=["new LodgingProperty", "ToDtoAsync(", "dto.Id = null"],
         forbid=["_db.SaveChangesAsync(", "_db.LodgingProperties.Add("],
         why=_LS_WHY + "S-P1-3 ค่าตั้งต้นที่พักใหม่มาจาก entity ผ่าน mapper ตัวเดียว · อ่านอย่างเดียว ไม่สร้างแถว"),
    dict(file="Services/Implementations/Lodging/LodgingService.Operations.cs", method="MapAsync",
         must=["LodgingStayConditions.Lines("],
         why=_LS_WHY + "คำตัดสินข้อ 121 early/late hours = เงื่อนไขบนหลักฐานการจอง (ผู้อ่านจริงของค่าตั้ง)"),
    dict(file=LODGING, method="BindSiteAsync",
         must=["BeginTransactionAsync(", "LodgingPublicReadiness.QuickBindRefusal(", "EnsurePropertyRefsBelongAsync(",
               "EnsureSiteNotBoundElsewhereAsync(", "_db.AddChainedAuditLog(", "tx.CommitAsync(", "ToDtoAsync(",
               "s.IndustryType == IndustryType.Hotel"],
         must_re=[r"if\s*\(\s*refusal\s*!=\s*null\s*\)\s*throw\b"],
         before=[("EnsureSiteNotBoundElsewhereAsync(", "_db.SaveChangesAsync("),
                 ("LodgingPublicReadiness.QuickBindRefusal(", "_db.SaveChangesAsync("),
                 ("_db.SaveChangesAsync(", "tx.CommitAsync(")],
         forbid=["_db.AuditLogs.Add("],
         why=_LS_WHY + "ผูกที่พักกับเว็บจากป้ายสถานะ: ด่านเดิม (tenant · ผูกซ้ำ) + เว็บที่พักเท่านั้น ใต้ธุรกรรมเดียว + audit chain"),
    dict(file=LODGING, method="BindableSitesAsync",
         must=["LodgingPublicReadiness.BindCandidates(", "s.CompanyId == companyId", "x.CompanyId == companyId"],
         why=_LS_WHY + "ผู้สมัครผูกด่วนจากตัวคัดตัวเดียว (ไม่รวมเว็บที่ผูกกับที่พักอื่น · tenant)"),
]
# ── จบบล็อกรอบ 202 ทีม LS ──

# ── รอบ 202 ทีม LW (เว็บที่พัก: ข้อมูลห้องสด · ห้าม GET สาธารณะเขียนฐาน · ด่านที่พักหลายแห่งทุกทางเข้า · migration ตรงทุกไบต์) ──
_LW_WHY_GET = "W-03: GET สาธารณะ (storefront/booking-services) ต้องอ่านอย่างเดียว — เดิมสร้างบริการ ฿0 ลงฐาน + catch {} กลืน error"
_LW_WHY_QUOTA = "W-05/ข้อ 118: ที่พักแห่งที่ 2 ผ่านด่าน add-on ตัวเดียว (LodgingPropertyQuota) ทั้งสร้างมือและ seed จากเว็บ"
_LW_WHY_MIG = "ข้อ 117: แทนเฉพาะบล็อกที่ตรง snapshot ของ seed ทุกไบต์ (ฟังก์ชัน Legacy* ตัวเดียวกับที่เคย seed) · ล็อกคีย์คงที่ · ไม่เทียบหลวม"
RULES += [
    dict(file="Services/Implementations/CmsBookingService.cs", method="GetServicesAsync",
         must=["AsNoTracking("],
         forbid=["SaveChangesAsync(", "SiteBookingServices.Add(", "EnsureDefaultBookingServiceAsync(", "catch"],
         why=_LW_WHY_GET),
    dict(file="Services/Implementations/Cms/LodgingSeeder.cs", method="SeedForSiteAsync",
         must=["LodgingPropertyQuota.BlockReasonAsync(", "LodgingSeedDecision.Decide("],
         must_re=[r"if\s*\(\s*outcome\s*!=\s*LodgingSeedOutcome\s*\.\s*Created\s*\)\s*return\b"],
         before=[("LodgingSeedDecision.Decide(", "db.LodgingProperties.Add(")],
         why=_LW_WHY_QUOTA + " · มีที่พักไม่ผูกเว็บ ⇒ ไม่สร้างแห่งที่สอง"),
    dict(file="Services/Implementations/Lodging/LodgingService.cs", method="CreatePropertyAsync",
         must=["LodgingPropertyQuota.BlockReasonAsync("],
         must_re=[r"if\s*\(\s*quotaBlock\s*!=\s*null\s*\)\s*throw\b"],
         before=[("LodgingPropertyQuota.BlockReasonAsync(", "_db.LodgingProperties.Add(")],
         forbid=["AddOnCodes.LodgingMultiProperty"],
         why=_LW_WHY_QUOTA + " (ห้ามเขียนด่านซ้ำ inline)"),
    dict(file="Services/Implementations/CmsSiteService.cs", method="CreateSiteAsync",
         must=["warnings.Add(seed.Message)", "created.Warnings = warnings"],
         call_args=[("Cms.LodgingSeeder.SeedForSiteAsync(", "_entitlement")],
         why="W-08: ขั้น seed ที่ล้ม/ถูกข้ามต้องถึงเจ้าของในผลตอบ (ไม่ใช่ LogWarning อย่างเดียว) · " + _LW_WHY_QUOTA),
    dict(file="Services/Implementations/CmsSiteService.cs", method="ApplyTemplateCoreAsync",
         must=["result.LodgingMessage = seed.Message"],
         call_args=[("Cms.LodgingSeeder.SeedForSiteAsync(", "_entitlement")],
         why="ข้อ 118: เติมเทมเพลตที่พักต้องบอกเหตุที่ไม่สร้างที่พัก · " + _LW_WHY_QUOTA),
    dict(file="Data/DatabaseMigrationHelper.cs", method="GetAlterStatements",
         must=["Round202LodgingWebStatements("], why=_LW_WHY_MIG + " (ต้องอยู่เส้นหลักที่รันตอนบูต)"),
    dict(file="Data/DatabaseMigrationHelper.cs", method="Round202LodgingWebStatements",
         must=["LodgingSiteSeedMigration.RoomBlocksSql(", "LodgingSiteSeedMigration.AutoSeedServiceCleanupSql("], why=_LW_WHY_MIG),
    dict(file="Helpers/LodgingSiteSeedMigration.cs", method="RoomListRules",
         must=["CmsSiteTemplateSeeder.LegacyHotelRoomsRichTextConfig(", "CmsSiteTemplateSeeder.LegacyHotelPricingTableConfig(",
               "CmsSiteTemplateSeeder.LegacyHotelRoomsRichTextV1Config(", "CmsSiteTemplateSeeder.LegacyHotelPricingTableV1Config(",
               "CmsSiteTemplateSeeder.LegacyHotelRoomsPageRichTextV1Config(", "CmsSiteTemplateSeeder.LegacyHotelRoomsGalleryV1Config(",
               "CmsSiteTemplateSeeder.HotelLiveRoomsConfig("], why=_LW_WHY_MIG + " · ครอบรุ่นก่อนรอบ 158 (ฝ่ายค้าน P2-3)"),
    dict(file="Helpers/LodgingSiteSeedMigration.cs", method="RoomsHeroRules",
         must=["CmsSiteTemplateSeeder.LegacyHotelRoomsHeroConfig(", "CmsSiteTemplateSeeder.LegacyHotelRoomsHeroV1Config("], why=_LW_WHY_MIG),
    dict(file="Helpers/LodgingSiteSeedMigration.cs", method="RoomBlocksSql",
         must=["RoomListRules()", "RoomsHeroRules()", "CmsSiteTemplateSeeder.HotelRoomsHeroConfig()", "LockKey"],
         must_lit=["pg_advisory_xact_lock(", 'b.\\"IsVisible\\" = true', '\\"PageBlockTranslations\\"'],
         forbid_lit=["LIKE", "trim(", "jsonb"],
         why=_LW_WHY_MIG),
    dict(file="Helpers/LodgingSiteSeedMigration.cs", method="AutoSeedServiceCleanupSql",
         must=["LockKey"],
         must_lit=["pg_advisory_xact_lock(", 'v.\\"UpdatedBy\\" IS NULL', '\\"SiteBookings\\"', '\\"SiteBookingSlots\\"', '\\"SiteBookingServiceTranslations\\"'],
         why="W-03: ล้างเฉพาะบริการ auto-seed บนเว็บที่พักที่ไม่มีการจอง/ไม่เคยแก้ · ล็อกคีย์คงที่"),
]
# ฝ่ายค้านรอบ 202 LW: ค้นหาหลายห้องเทียบเพดานต่อห้อง (P1-3) · ป้ายการ์ดห้องจาก engine (P2-1/2) · เว็บที่พักนับที่พักที่ผูก (P2-4)
RULES += [
    dict(file="Services/Implementations/Lodging/LodgingService.Reservations.cs", method="SearchAsync",
         must=["LodgingSearchGuests.PerRoom(request.Adults, request.Rooms, 1)", "LodgingSearchGuests.PerRoom(request.Children, request.Rooms, 0)"],
         call_args=[("LodgingPricingEngine.QuoteRoom(", "adultsPerRoom"), ("LodgingPricingEngine.QuoteRoom(", "childrenPerRoom")],
         forbid=["Math.Max(1, request.Adults)"],
         why="P1-3: ช่องค้นหาส่งยอดรวม ⇒ ด่านความจุ/ราคาต่อห้องใช้ผู้ใหญ่/เด็กต่อห้อง (ปัดขึ้น) — เดิม 4 คน 2 ห้องถูกบอกว่าเกิน"),
    dict(file="Controllers/LodgingPublicController.cs", method="Info",
         must=["ApplyPublicRoomLabelsAsync("],
         why="P2-1/P2-2: การ์ดห้องหน้าเว็บแสดงราคาเริ่มต้น/ความจุที่เซิร์ฟเวอร์คำนวณ (ไม่ใช่ baseRate/maxOccupancy ดิบ)"),
    dict(file="Services/Implementations/Lodging/LodgingService.PublicRooms.cs", method="ApplyPublicRoomLabelsAsync",
         must=["PickRatePlan(", "ctx.InputFor(rt, plan)", "LodgingPublicRoomLabels.FromRate(", "LodgingPublicRoomLabels.FromRateLabel(",
               "LodgingPublicRoomLabels.CapacityLabel("],
         why="P2-1: ราคาเริ่มต้นผ่าน engine ตัวเดียวกับใบเสนอราคา (แผนตั้งต้น · ฤดูกาล · สุดสัปดาห์ · override)"),
    dict(file="Services/Implementations/CmsRenderingService.cs", method="GetStorefrontDataAsync",
         must=["CmsModuleResolver.IsLodgingSite(site.IndustryType, hasBoundLodging)", "p.CompanyId == companyId && p.SiteId == siteId && !p.IsDeleted"],
         why="P2-4: เว็บที่พัก = ประเภทที่พัก หรือมีที่พัก (ไม่ลบ) ผูกเว็บนี้ · กรองบริษัท"),
]
# ── จบบล็อกรอบ 202 ทีม LW ──


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

# ── รอบ 201 ทีม ST (A-ST1): ป้าย [SETTLEMENT:] ใน Payment.Notes (ผู้ใช้พิมพ์ได้) ห้ามมีผลกับด่านใดอีก — เจ้าของการรับชำระ = คอลัมน์
#    Payment.SettlementBatchId ตัวเดียว · ทั้งเรพห้ามอ้างตัวอ่านป้าย/ป้ายเอง ยกเว้นไฟล์ที่ "เขียน" ป้ายเป็นข้อความให้คนอ่าน (SettlementPosting.cs:
#    ตัวสร้างข้อความ + SQL backfill ครั้งเดียวตอนสร้างคอลัมน์) ──
NOTES_MARKER_FORBID = dict(
    allow={"Helpers/SettlementPosting.cs"},
    code_patterns=[r"\bBatchIdFromPaymentNotes\b", r"\bPaymentMarker(?:Head)?\b", r"\bPaymentOwnerBackfillSql\b", r"\bPaymentOwnerUnbackfilledCountSql\b"],
    literal_patterns=[r"\[SETTLEMENT:"],
    why="รอบ 201 ทีม ST (A-ST1): ป้ายใน Payment.Notes เป็นข้อความที่ผู้ใช้พิมพ์ได้ — อ่านเจ้าของจากคอลัมน์ Payment.SettlementBatchId "
        "(ผู้ลงบัญชีประทับผ่าน SettlementPaymentOwner) · backfill จากป้ายมีที่เดียวใน SettlementPostingKeys.PaymentOwnerBackfillSql "
        "ซึ่ง DatabaseMigrationHelper.PaymentSettlementOwnerMigrationSql เรียกครั้งเดียวตอนสร้างคอลัมน์",
)
NOTES_MARKER_MIGRATION_CALLER = "Data/DatabaseMigrationHelper.cs"


def notes_marker_files():
    out = []
    for path in sorted(SRC.rglob("*.cs")):
        rel = str(path.relative_to(SRC))
        if "/bin/" in "/" + rel or "/obj/" in "/" + rel:
            continue
        out.append((rel, path.read_text(encoding="utf-8")))
    return out


def notes_marker_errors(files) -> list:
    errs = []
    for rel, text in files:
        if rel in NOTES_MARKER_FORBID["allow"]:
            continue
        checks = [(mask(text), NOTES_MARKER_FORBID["code_patterns"]), (mask(text, keep_strings=True), NOTES_MARKER_FORBID["literal_patterns"])]
        for code, pats in checks:
            for rx in pats:
                for m in re.finditer(rx, code):
                    # ผู้เรียก backfill ที่อนุญาตตัวเดียว (เรียกใน DO block ตอนสร้างคอลัมน์)
                    # + ตัวนับแถวที่ไม่ถูก backfill (ST-X5 · log อย่างเดียว)
                    if rel == NOTES_MARKER_MIGRATION_CALLER and m.group(0) in ("PaymentOwnerBackfillSql", "PaymentOwnerUnbackfilledCountSql"):
                        continue
                    line = code.count("\n", 0, m.start()) + 1
                    errs.append(f"{rel}:{line} อ้างป้ายใน Payment.Notes `{m.group(0)}` — {NOTES_MARKER_FORBID['why']}")
    return errs


def notes_marker_self_test(files) -> list:
    fails = []
    if not files:
        return ["self-test NOTES_MARKER_FORBID: ไม่พบไฟล์ .cs (glob ผิด?)"]
    target = "Services/Settlement/SettlementPostingService.cs"
    base = dict(files).get(target)
    if base is None:
        return [f"self-test NOTES_MARKER_FORBID: ไม่พบ {target}"]
    if notes_marker_errors([(target, base)]):
        fails.append(f"self-test NOTES_MARKER_FORBID: {target} ปัจจุบันถูกฟ้อง (ต้องแก้โค้ดก่อน)")
    for sample in ["var b = SettlementArtifactGuard.BatchIdFromPaymentNotes(p.Notes);",
                   "var ok = p.Notes.Contains(SettlementPostingKeys.PaymentMarker(batchId));",
                   "var h = SettlementPostingKeys.PaymentMarkerHead;",
                   "var ok = p.Notes != null && p.Notes.StartsWith(\"[SETTLEMENT:\");",
                   "var sql = SettlementPostingKeys.PaymentOwnerBackfillSql();"]:
        if not notes_marker_errors([(target, base + "\nclass __X { void F() { " + sample + " } }\n")]):
            fails.append(f"self-test NOTES_MARKER_FORBID: ใส่ `{sample}` แล้วไม่ฟ้อง")
    for ok in ["// เดิมอ่าน BatchIdFromPaymentNotes(p.Notes) และ [SETTLEMENT:]",
               "var owner = p.SettlementBatchId;"]:
        if notes_marker_errors([(target, "class __Y { void F() { " + ok + " } }\n")]):
            fails.append(f"self-test NOTES_MARKER_FORBID: `{ok}` ถูกฟ้องผิด")
    # ผู้เรียก backfill ที่อนุญาตต้องผ่าน · แต่ป้ายดิบในไฟล์ migration ต้องถูกฟ้อง
    if notes_marker_errors([(NOTES_MARKER_MIGRATION_CALLER, "class __Z { string F() => Accounting.Helpers.SettlementPostingKeys.PaymentOwnerBackfillSql(); }\n")]):
        fails.append("self-test NOTES_MARKER_FORBID: ผู้เรียก backfill ใน DatabaseMigrationHelper ถูกฟ้องผิด")
    if not notes_marker_errors([(NOTES_MARKER_MIGRATION_CALLER, "class __Z { string F() => \"UPDATE x WHERE n LIKE '[SETTLEMENT:%'\"; }\n")]):
        fails.append("self-test NOTES_MARKER_FORBID: ป้ายดิบใน DatabaseMigrationHelper ไม่ถูกฟ้อง")
    return fails


@functools.lru_cache(maxsize=64)
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
    return _check_body(body, lit_body, line0, rule)


def _check_body(body: str, lit_body: str, line0: int, rule):
    # แยกจาก check_rule (2026-10-08) ให้ self_test mask เฉพาะ "ตัวเมธอดที่กลายพันธุ์" ไม่ใช่ทั้งไฟล์ — เดิม mask() (สแกนทีละตัวอักษร)
    # ทั้ง DocumentService.cs ซ้ำ 2 ครั้งต่อ variant × ~1,000 กติกา ⇒ check_all รอบละ 20+ นาที · ผลเท่าเดิม: ขอบเมธอดอยู่ในโค้ดเสมอ
    rel, meth, why = rule["file"], rule["method"], rule["why"]
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
    # รอบ 202 ทีม LW: ใส่ auto-seed บน GET สาธารณะกลับ ⇒ ต้องฟ้อง · ทิ้งผลตัวตัดสิน seed (สร้างแห่งที่สองต่อ) ⇒ ต้องฟ้อง
    ("LW1", "GetServicesAsync", "GetServicesAsync(Guid companyId, Guid siteId, bool includeInactive = false)\n    {\n", "GetServicesAsync(Guid companyId, Guid siteId, bool includeInactive = false)\n    {\n        await EnsureDefaultBookingServiceAsync(companyId, siteId);\n", True),
    ("LW2", "SeedForSiteAsync", "if (outcome != LodgingSeedOutcome.Created) return new Result(null, outcome, message);", "_ = message;", True),
    ("LW4", "SearchAsync", "var adultsPerRoom = LodgingSearchGuests.PerRoom(request.Adults, request.Rooms, 1);", "var adultsPerRoom = Math.Max(1, request.Adults);", True),
    # ฝ่ายค้านรอบส่งออก รอบ 4: ถอดด่านยกยอดรุ่นเก่าออกจากปิดปี (P2-a) · รายงานผังใต้ผังแม่แบบไม่กรองหลักฐาน (P2-c) · ใบปิดปีกลับไป join ผังแบบกรอง (P2-b) ⇒ ต้องฟ้อง
    ("EXA4a", "YearEndCloseAsync", "var legacyAnchorIds = await EnsureNoUnrepairedLegacyRollAsync(companyId, startMonth, fiscalYear);",
     "var legacyAnchorIds = new List<Guid>();", True),
    ("EXA4c", "PlanLeafRescueAsync", "var scope = underLeaf.Where(evidenced.Contains).ToList();", "var scope = underLeaf;", True),
    ("EXA4b", "YearEndCloseAsync", "join account in _db.ChartOfAccounts.IgnoreQueryFilters() on line.AccountId equals account.Id",
     "join account in _db.ChartOfAccounts on line.AccountId equals account.Id", True),
    # ฝ่ายค้านรอบส่งออก รอบ 5: ถอดด่านใบปิดปีจากกลับรายการ (P1) · เลิกเติมผังที่ลบตอนเปิดดู (P1) · ถอดด่านล็อกเดือนเริ่มรอบ (P2-2) ·
    # ตัวตรวจยกยอดรุ่นเก่าไม่สนหลักฐาน (P2-2) · กู้คืนไม่ตรวจผังแม่ (P2-3) ⇒ ต้องฟ้อง
    ("EXA5a", "ReverseJournalEntryAsync",
     'await Accounting.Helpers.ClosingEntryLock.EnsureNotClosingEntriesAsync(_db, companyId, new[] { original.Id }, "กลับรายการ");', "", True),
    ("EXA5b", "GetJournalEntryAsync", "await Accounting.Helpers.JournalLineAccounts.HydrateAsync(_db, companyId, entry);\n\n        var srcMap", "var srcMap", True),
    ("EXA5c", "ApplyFiscalYearStartMonthAsync", "Accounting.Helpers.OpeningRoll.FiscalYearStartMonthLockedMessage(before, requested, closedYears)", '"x"', True),
    ("EXA5d", "FindLegacyCalendarRollPeriodsAsync", "if (!OpeningRoll.IsLegacyCalendarRoll(rolledAt, startMonth, changes)) continue;", "", True),
    ("EXA5e", "RestoreAccountAsync", "var plan = await PlanRestoreUnderAsync(companyId, account, parentId);",
     "var plan = new AccountRestorePlan(account.Id, account.AccountCode, null, null, null);", True),
    # ยกเลิกการปิดปี (2026-10-16): ทิ้งผลตัวตัดสิน · ถอดล็อก · ถอดด่านเจ้าของ · ยกเลิกแถวนำเข้าด้วย · กลับใบผ่านทางสมุดรายวัน · ถอดการเปิดงวด ธ.ค. ตอนปิดปี ⇒ ต้องฟ้อง
    ("REO1", "ReopenYearEndAsync", "if (!decision.Allowed) throw new YearEndReopenException(decision);", "_ = decision;", True),
    ("REO2", "ReopenYearEndAsync", 'await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", OpeningRoll.LockKey(companyId));\n\n        // ── ข้อเท็จจริง',
     "\n        // ── ข้อเท็จจริง", True),
    ("REO3", "ReopenYearEnd", "await _companyService.EnsureOwnerAccessAsync(companyId, userId);\n        try\n        {\n            var result = await _accountingService.ReopenYearEndAsync(",
     "try\n        {\n            var result = await _accountingService.ReopenYearEndAsync(", True),
    ("REO4", "ReopenYearEndAsync", "foreach (var row in nextRows.Where(o => OpeningRoll.IsRolled(o.Notes)))", "foreach (var row in nextRows)", True),
    ("REO5", "ReopenYearEndAsync", "var reversalId = await CreateJournalEntryCoreAsync(companyId, reversalRequest, userId, deletedClosingAccounts);",
     "var reversalId = (await ReverseJournalEntryAsync(companyId, original.Id, original.EntryDate, null, true)).Id;", True),
    ("REO6", "YearEndCloseAsync", "            closingDatePeriod.Status = FiscalPeriodStatus.Open;\n", "", True),
    # ทิศตรงข้าม: จัดรูปแบบใหม่ (ขึ้นบรรทัด) ต้องไม่ฟ้อง
    ("REO7", "ReopenYearEndAsync", "if (!decision.Allowed) throw new YearEndReopenException(decision);",
     "if (!decision.Allowed)\n            throw new YearEndReopenException(decision);", False),
    # (LW3 เดิม — ทิ้งผลด่าน quota ใน CreatePropertyAsync — ย้ายไปพึ่งกลายพันธุ์อัตโนมัติของ must_re ในกติกาของ LW เพราะ by_method ผูกชื่อเมธอดนี้กับกติกาของทีม LS ที่มาก่อน)
]


def self_test() -> list:
    fails = []
    cache = {}
    mcache = {}
    for rule in RULES:
        rel, meth = rule["file"], rule["method"]
        text = cache.setdefault(rel, (SRC / rel).read_text(encoding="utf-8"))
        masked = mcache.setdefault(rel, mask(text))
        span = method_body(masked, meth)
        if span is None:
            continue
        body = text[span[0]:span[1]]
        line0 = masked.count("\n", 0, span[0]) + 1
        run = lambda b: _check_body(mask(b), mask(b, keep_strings=True), line0, rule)
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
    # รอบ 202 (รวม LO/LS/LW): เมธอดชื่อเดียวกันมีหลายกติกาจากหลายทีม — เดิมเลือก "กติกาแรก" ตามลำดับการ += ⇒ ลำดับ merge เปลี่ยนแล้วเคสฝ่ายค้าน
    # ไปเจอกติกาของทีมอื่นที่ไม่ได้ตรวจสิ่งที่เคสนั้นทดสอบ (LW3 ไปเจอกติกา LS ของ CreatePropertyAsync) ⇒ ตรวจทุกกติกาของเมธอดนั้นในไฟล์ที่มีโค้ดเดิม
    # (ต้องฟ้อง = มีกติกาใดกติกาหนึ่งฟ้อง · ต้องไม่ฟ้อง = ไม่มีกติกาไหนฟ้องเลย — เข้มกว่าเดิมทั้งสองทิศ)
    rules_by_method = {}
    for r in RULES:
        rules_by_method.setdefault(r["method"], []).append(r)
    for tag, meth, old, new, expect in REVIEWER_CASES:
        group = [r for r in rules_by_method[meth]
                 if old in cache.setdefault(r["file"], (SRC / r["file"]).read_text(encoding="utf-8"))]
        if not group:
            fails.append(f"self-test {tag}: หา `{old[:50]}` ในไฟล์ของกติกา {meth} ไม่เจอ (โค้ดขยับ — ปรับเคสให้ตรง)")
            continue
        fired = any(bool(check_rule(cache[r["file"]].replace(old, new, 1), r)) for r in group)
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


# ── รอบ 202 ทีม LO (เครื่องจองที่พัก: ล็อก · lifecycle · เงิน) — ด่านใน service ต้องถูกเรียกจริง (เทสต์ pure ล็อกแค่ helper) ──
# O-P0-1 ตรวจห้องว่าง/ราคาใต้ล็อกเดียวกับเลขจอง · O-P0-2/ข้อ 119 night audit ไม่ประทับสถานะปลายทาง · O-P1-3 แผนราคาของแขก ·
# O-P1-4/5 กติกากันห้องตัวเดียว + ธงเงินเข้าแต่ยืนยันไม่ได้ · ข้อ 123–127 คนเสริม/ความจุ/เช็คอินก่อน 1 วัน/ค่าปรับไม่เกินมัดจำ
LODGING_AUDIT_JOB = "Services/Background/LodgingNightAuditJob.cs"
LODGING_PAY_HANDLER = "Services/Payments/Handlers/LodgingReservationPaymentHandler.cs"
PUBLIC_PAY = "Controllers/PublicPaymentController.cs"
_LO_WHY = "รอบ 202 ทีม LO: "
RULES += [
    dict(file=LODGING_RES, method="WithPropertyLockAsync",
         must=["CurrentTransaction", "PropertyLockKey(", "BeginTransactionAsync(", "CommitAsync("],
         must_lit=['"SELECT pg_advisory_xact_lock({0})"'],
         why=_LO_WHY + "O-P0-1 ล็อกต่อที่พัก = xact lock คีย์คงที่ · มีธุรกรรมแล้วห้ามเปิดซ้อน"),
    dict(file=LODGING_RES, method="CreateReservationAsync",
         must=["WithPropertyLockAsync(", "LoadContextAsync(", "BuildQuote(", "LodgingBookingGuards.PendingCapProblem(",
               "LodgingBookingGuards.PromoCodeProblem(", "SequenceNumber.NextSequence("],
         before=[("WithPropertyLockAsync(", "LoadContextAsync("), ("LoadContextAsync(", "BuildQuote("),
                 ("BuildQuote(", "SequenceNumber.NextSequence(")],
         forbid=["BeginTransactionAsync(", "pg_advisory_xact_lock"],
         why=_LO_WHY + "O-P0-1 ห้องว่าง + ราคา + เพดานรอชำระ + เลขจอง อยู่ใต้ล็อกเดียว (เดิมล็อกแค่เลขจอง ⇒ ห้องสุดท้ายจองซ้อน)"),
    dict(file=LODGING_RES, method="ClaimInventoryForConfirmAsync",
         must=["WithPropertyLockAsync(", "ReloadAsync(", "LoadContextAsync(", "EnsureRoomsAvailable(", "LodgingHoldRule.ExtendHold("],
         before=[("ReloadAsync(", "LoadContextAsync("), ("LoadContextAsync(", "EnsureRoomsAvailable(")],
         why=_LO_WHY + "O-P0-1 ยืนยัน: ตรวจห้องว่าง + จองห้องไว้ใต้ล็อก (อ่านสถานะใหม่หลังได้ล็อก)"),
    dict(file=LODGING_LIFE, method="ConfirmCoreAsync",
         must=["ClaimInventoryForConfirmAsync(", "ReloadAsync(", "ProblemIntentMoneyInAsync("],
         before=[("ReloadAsync(", "ClaimInventoryForConfirmAsync("), ("ClaimInventoryForConfirmAsync(", "CreateDepositReceiptAsync(")],
         forbid=["LoadContextAsync("],
         why=_LO_WHY + "O-P0-1 ห้ามตรวจห้องว่างนอกล็อก · จองห้องไว้ก่อนออกใบมัดจำ · ฝ่ายค้าน P2-1 อ่านใหม่หลังได้ล็อก · P1-3ค บัญชีจากรายการชำระ"),
    dict(file=LODGING_LIFE, method="ConfirmAsync",
         must=["WithReservationLockAsync(", "AdvisoryLockKey.LodgingConfirm", "ConfirmCoreAsync("],
         why=_LO_WHY + "ฝ่ายค้าน P2-1 ยืนยันซ้อน ⇒ ใบมัดจำสองใบ + DepositPaid lost update — ล็อกต่อการจองครอบทั้งเส้น"),
    dict(file=LODGING_LIFE, method="WithReservationLockAsync",
         must=["AdvisoryLockKey.For(", "ExecuteScalarAsync("],
         must_lit=["SELECT pg_try_advisory_lock(", "SELECT pg_advisory_unlock("],
         why=_LO_WHY + "ฝ่ายค้าน P2-1 ล็อกต่อการจองคีย์คงที่ (FNV-1a) · ปลดเสมอใน finally"),
    dict(file=LODGING_LIFE, method="ProblemIntentMoneyInAsync",
         must=["_gatewayAccounts.ResolveMoneyInAccountAsync(", "PaymentIntentStatus.Succeeded", "i.CompanyId == companyId", "i.SourceId == r.Id",
               "BankAccountId = null"],
         why=_LO_WHY + "ฝ่ายค้าน P1-3ค ใบมัดจำของเงินออนไลน์ที่ยืนยันภายหลังลงบัญชีพักของช่องทางชำระ (ไม่ใช่บัญชีจากหน้าจอ)"),
    dict(file=LODGING_LIFE, method="ResolvePaymentProblemAsync",
         must=["WithReservationLockAsync(", "ReloadAsync(", "LodgingHoldRule.IsAutoExpiredHold(", "WithPropertyLockAsync(", "EnsureRoomsAvailable(",
               "ClosePaymentProblem(", "reason.Length == 0"],
         before=[("WithPropertyLockAsync(", "EnsureRoomsAvailable(")],
         forbid=["r.PaymentProblemAt = null"],
         why=_LO_WHY + "ฝ่ายค้าน P1-3ข ปิดเรื่องเงินเข้า 3 ทาง (เปิดกลับเมื่อห้องว่างจริงใต้ล็อก · คืนเงินแล้ว · เหตุผล) — ห้ามล้างธงเงียบ"),
    dict(file=LODGING_LIFE, method="ClosePaymentProblem",
         must=["AddChainedAuditLog(", "AppendInternal("],
         why=_LO_WHY + "ฝ่ายค้าน P1-3ข ปลดธงพร้อมประวัติ + audit"),
    dict(file=LODGING_LIFE, method="EnsureSlipAcceptable",
         must=["LodgingHoldRule.IsAutoExpiredHold("],
         why=_LO_WHY + "ฝ่ายค้าน P1-3ก สลิปหลังระบบยกเลิกเพราะหมด hold ต้องถูกรับ (ใบที่คนยกเลิกเองยังปฏิเสธ)"),
    dict(file=LODGING_LIFE, method="UnitTakenByOtherAsync",
         must=["LodgingHoldRule.OccupiesUnit(", "x.CompanyId == companyId", "CheckedOutAt"],
         why=_LO_WHY + "ห้องชนใช้กติกาเดียว (ยังกันห้อง + ทับช่วง · แถวรุ่นเก่าที่เปิดกลับไม่ยืดวันออก)"),
    dict(file=LODGING_LIFE, method="EnsureAssignedUnitsFreeAsync",
         must=["UnitTakenByOtherAsync(", "IsOutOfService"],
         why=_LO_WHY + "ฝ่ายค้าน P1-1 ห้องที่จัดไว้ต้องว่างคืนที่ขยายเพิ่มด้วย"),
    dict(file=LODGING_LIFE, method="RescheduleAsync",
         must=["WithPropertyLockAsync(", "LodgingBookingGuards.PairByKey("],
         before=[("WithPropertyLockAsync(", "LoadContextAsync("), ("LoadContextAsync(", "BuildQuote(")],
         forbid=["ElementAt(i)", "roomsById[i]"],
         why=_LO_WHY + "O-P0-1 เลื่อนวันใต้ล็อก · P2 จับคู่บรรทัดด้วยกุญแจ ไม่ใช่ index"),
    dict(file=LODGING_LIFE, method="AssignUnitAsync",
         must=["WithPropertyLockAsync(", "AssignCoreAsync("],
         before=[("WithPropertyLockAsync(", "AssignCoreAsync(")],
         why=_LO_WHY + "O-P0-1 จัดห้องใต้ล็อก (สองแท็บจัดห้องเดียวกันให้สองใบ)"),
    dict(file=LODGING_LIFE, method="CheckInAsync",
         must=["WithPropertyLockAsync(", "ExtendStayOneNightEarlierAsync(", "EnsureAssignedUnitsFreeAsync("],
         before=[("WithPropertyLockAsync(", "ExtendStayOneNightEarlierAsync("), ("WithPropertyLockAsync(", "AssignCoreAsync("),
                 ("AssignCoreAsync(", "EnsureAssignedUnitsFreeAsync(")],
         why=_LO_WHY + "O-P0-1 เช็คอิน/จัดห้องใต้ล็อก · ข้อ 125 เช็คอินก่อน 1 วัน = เพิ่มคืน (ตรวจห้องว่างใต้ล็อก)"),
    dict(file=LODGING_LIFE, method="ExtendStayOneNightEarlierAsync",
         must=["LodgingAvailability.AvailableRooms(", "LodgingPricingEngine.QuoteRoom(", "LodgingPricingEngine.Totals(", "AddChainedAuditLog("],
         before=[("LodgingAvailability.AvailableRooms(", "LodgingPricingEngine.QuoteRoom(")],
         why=_LO_WHY + "ข้อ 125 ต้องมีห้องว่างคืนนั้น · ราคาคืนนั้นจาก engine · บันทึกประวัติ"),
    dict(file=LODGING_LIFE, method="AssignCoreAsync",
         must=["UnitTakenByOtherAsync("],
         forbid=["x.Reservation.HoldExpiresAt > DateTime.UtcNow"],
         why=_LO_WHY + "O-P1-5/ข้อ 119 ตัวตรวจห้องชนใช้กติกากันห้องตัวเดียวกับตัวนับห้องว่าง"),
    dict(file=LODGING_RES, method="ExpireHoldsAsync",
         must=["LodgingHoldRule.HoldLapsed(", "WithPropertyLockAsync(", "AddChainedAuditLog(", "LodgingGuestConfirmPolicy.AutoExpireReasonFor("],   # รอบ 202 LC P2-5: เหตุผลตามโหมด (ตัวอ่านรู้จักทั้งสองข้อความ)
         forbid=["AuditLogs.Add("],
         why=_LO_WHY + "O-P1-5 ยกเลิกอัตโนมัติด้วยกติกาเดียวกับตัวนับห้องว่าง (ใบส่งสลิป/เงินค้างไม่ถูกยกเลิก) ใต้ล็อก"),
    dict(file=LODGING_RES, method="LoadContextAsync",
         must=["LodgingHoldRule.EffectiveCheckOut(", "b.SlipUploadedAt != null", "b.PaymentProblemAt != null", "b.CheckedOutAt"],
         why=_LO_WHY + "O-P1-4/5 + ข้อ 119 ข้อเท็จจริงที่ทำให้ยังกันห้องต้องถูกส่งเข้าตัวนับ"),
    dict(file=LODGING_RES, method="HoldForOnlinePaymentAsync",
         must=["WithPropertyLockAsync(", "LodgingHoldRule.HoldLapsed(", "EnsureRoomsAvailable(", "LodgingHoldRule.PaymentHoldUntil("],
         before=[("LodgingHoldRule.HoldLapsed(", "LodgingHoldRule.PaymentHoldUntil(")],
         why=_LO_WHY + "O-P1-4 ถือห้องถึงวันหมดอายุของรายการชำระ · hold หมดแล้วต้องตรวจห้องก่อนแขกจ่าย"),
    dict(file=PUBLIC_PAY, method="StartAsync",
         must=["holdForPayment(target, null)", "holdForPayment(target, intent.QrExpiresAt)"],
         before=[("holdForPayment(target, null)", "_intents.StartAsync(")],
         why=_LO_WHY + "O-P1-4 หน้าจ่ายเงินสาธารณะต้องถือห้องก่อน/หลังสร้างรายการชำระ"),
    dict(file=LODGING_PAY_HANDLER, method="HandleSucceededAsync",
         must=["FlagPaymentProblemAsync("],
         must_re=[r"\bthrow\s*;"],
         why=_LO_WHY + "O-P1-4/ข้อ 127 เงินเข้าแต่ยืนยันไม่ได้ ⇒ ธงบนการจอง แล้วโยนต่อให้ประวัติรายการชำระ (ดัง 3 ที่)"),
    dict(file=LODGING_LIFE, method="UploadSlipByTokenAsync",
         must=["WithPropertyLockAsync(", "LodgingHoldRule.HoldLapsed(", "FlagPaymentProblemAsync(", "ReloadAsync(", "EnsureSlipAcceptable(",
               "LodgingHoldRule.IsAutoExpiredHold("],
         before=[("WithPropertyLockAsync(", "ReloadAsync("), ("ReloadAsync(", "LodgingHoldRule.IsAutoExpiredHold(")],
         why=_LO_WHY + "ข้อ 127 ส่งสลิปหลัง hold หมดแล้วห้องเต็ม ⇒ รับสลิป + ธง (ไม่คืนเงินอัตโนมัติ)"),
    dict(file=LODGING_LIFE, method="FlagPaymentProblemAsync",
         must=["PaymentProblemAt", "AddChainedAuditLog(", "TryNotifyAsync(", "NotificationEvents.LodgingPaymentUnconfirmed"],
         why=_LO_WHY + "ข้อ 127 ธง + audit + แจ้งที่พัก (อีเมล + เครื่องแจ้งเตือนกลาง)"),
    dict(file=LODGING_RES, method="PickRatePlan",
         must=["LodgingBookingGuards.ExplicitRatePlanProblem(", "PlanApplies(chosen"],
         why=_LO_WHY + "O-P1-3 แผนราคาที่แขกส่ง id มาเองต้องผ่านเงื่อนไขของแผน"),
    dict(file=LODGING_RES, method="BuildQuote",
         must=["LodgingOccupancy.ExtraBedProblem(", "LodgingOccupancy.AdultProblem(", "LodgingBookingGuards.PromoCodeProblem(", "PickRatePlan(ctx, ratePlanId, rt, checkIn, nights, isStaff)",
               "LodgingOccupancy.ChargeableGuests(", "LodgingOccupancy.Totals("],
         forbid=["rt.MaxOccupancy", "rt.MaxChildren", "Math.Clamp(room.ExtraBeds", "rt.ExtraBedPrice ?? 0m"],
         why=_LO_WHY + "ข้อ 123/124 คนเสริมเกิน = ปฏิเสธ (ไม่ตัดเงียบ) · เด็ก/ทารกไม่นับความจุ · จำนวนผู้เข้าพักจากตัวนับเดียว"),
    dict(file=LODGING_RES, method="QuoteAsync",
         call_args=[("BuildQuote(", "isStaff")],
         forbid=["isStaff: false"],
         why=_LO_WHY + "P2 quote ของพนักงานใช้ด่านเดียวกับเส้นสร้างจองของพนักงาน"),
    dict(file=LODGING_LIFE, method="CancelCoreAsync",
         must=["LodgingBookingGuards.CappedCancellationFee(", "LodgingOverdueRule.IsLegacyAutoNoShow("],
         before=[("LodgingBookingGuards.CappedCancellationFee(", "LodgingDepositSettlement.PlanCancellation(")],
         forbid=["UncollectedFee"],
         why=_LO_WHY + "ข้อ 126 ค่าปรับไม่เกินมัดจำ · no-show รุ่นเก่าคิดได้ครั้งเดียว (ตรวจซ้ำที่ตัวกลาง)"),
    dict(file=LODGING_LIFE, method="CheckOutCoreAsync",
         must=["LodgingOverdueRule.IsLegacyAutoCheckout(", "LodgingOverdueRule.BackfillVatAckProblem(", "VatPeriodFiledAsync(companyId, r.CheckOutDate)",
               "RestoreLegacyAutoCheckoutAsync("],
         before=[("LodgingOverdueRule.BackfillVatAckProblem(", "r.Status = LodgingReservationStatus.CheckedIn")],
         why=_LO_WHY + "O-P0-2 แถวที่ night audit รุ่นก่อนปิดเอง ออกใบเช็คเอาต์ย้อนหลังผ่านเส้นนี้ · ฝ่ายค้าน P1-4 เดือนยื่น ภ.พ.30 แล้วต้องมีคนรับทราบ · "
             "P2-3 ล้มก่อนออกใบคืนสถานะเดิม"),
    dict(file=LODGING_LIFE, method="SettleCheckOutAsync",
         must=["LodgingOverdueRule.IsReopenedLegacy("],
         why=_LO_WHY + "O-P0-2 ออกใบย้อนหลังห้ามแตะสถานะห้อง/งานแม่บ้านปัจจุบัน"),
    dict(file=LODGING_AUDIT_JOB, method="RunOnce",
         must=["LodgingOverdueRule.ShouldFlag(", "LodgingOverdueRule.FlagNote(", "OverdueFlaggedAt", "AddChainedAuditLog(",
               "LodgingOverdueRule.ShouldMeterOnFlag("],
         forbid=["r.Status = LodgingReservationStatus", "LodgingReservationStatus.CheckedOut;", "LodgingReservationStatus.NoShow;",
                 "HousekeepingStatus =", "UnitId = null", "AuditLogs.Add(", "LodgingHousekeepingTasks.Add("],
         why=_LO_WHY + "O-P0-2/ข้อ 119 (R1) night audit ติดธงเท่านั้น — ห้ามประทับ CheckedOut/NoShow · ห้ามแตะห้อง/งานแม่บ้าน"),
]
# ── จบบล็อกรอบ 202 ทีม LO ──


# ── 2026-10-02 ผู้ใช้รายงาน "ส่งสลิปแล้ว 500" (DbUpdateException) — ไฟล์จากคนนอกระบบต้องบันทึกผู้อัปโหลดเป็น null ──
# เดิมใส่ Guid.Empty ⇒ ชน FK FileAttachments → Users ทุกครั้ง (เทสต์ Db: GuestUploadFileAttachmentDbTests) · ห้ามกลับไปใส่ค่าแต่ง
_GUEST_FILE_WHY = "ไฟล์จากคนนอกระบบ (ไม่มีผู้ใช้) ⇒ UploadedByUserId = null — Guid.Empty ชน FK → Users = แขก/ลูกค้าส่งสลิปไม่ได้ (500)"
RULES += [
    dict(file="Services/Implementations/Lodging/LodgingService.Operations.cs", method="SaveSlipFileAsync",
         must=["UploadedByUserId = null"], forbid=["UploadedByUserId = Guid.Empty"], why=_GUEST_FILE_WHY + " · สลิปแขกที่พัก"),
    dict(file="Services/Implementations/CmsCommerceService.cs", method="RecordPaymentSlipAsync",
         must=["UploadedByUserId = null"], forbid=["UploadedByUserId = Guid.Empty"], why=_GUEST_FILE_WHY + " · สลิปหน้าร้านออนไลน์"),
    dict(file="Services/Implementations/PortalService.cs", method="UploadDocumentSlipAsync",
         must=["UploadedByUserId = null"], forbid=["UploadedByUserId = Guid.Empty"], why=_GUEST_FILE_WHY + " · สลิปพอร์ทัลลูกค้า"),
]


# ── รอบ 202 คำตัดสินข้อ 128 (ทีม LC): "การจองจากเว็บสำเร็จเมื่อไร" — ตัวตัดสินเดียว Helpers/LodgingGuestConfirmPolicy ──
# เดิมสูตร `prop.ConfirmWithoutDeposit || มัดจำ 0 || staff ConfirmImmediately` อยู่ในเส้นสร้างจอง + ตัวคิดมัดจำแยกกัน ⇒ ทุกจุดที่ตัดสินสถานะเริ่มต้น /
# มัดจำที่ต้องชำระ / ผลของสลิป / ป้ายฝั่งแขก ต้องเรียก helper และห้ามประกอบสูตรเดิมเอง · ส่งสลิป ≠ รับเงิน (ห้ามแตะ DepositPaid/PaidAmount ในเส้นสลิป)
_LC_WHY = "รอบ 202 ข้อ 128 (ทีม LC): "
RULES += [
    dict(file=LODGING_RES, method="CreateReservationAsync",
         must=["LodgingGuestConfirmPolicy.Resolve(", "LodgingGuestConfirmPolicy.ForChannel(", "LodgingGuestConfirmPolicy.Initial(",
               "GuestConfirmMode = channelMode", "LodgingGuestConfirmPolicy.CreatedEvent("],
         before=[("LodgingGuestConfirmPolicy.Initial(", "SequenceNumber.NextSequence(")],
         call_args=[("TryNotifyAsync(", "LodgingGuestConfirmPolicy.CreatedEvent")],
         forbid=["prop.ConfirmWithoutDeposit ||", "ConfirmWithoutDeposit", "AddMinutes(prop.PaymentHoldMinutes)"],
         why=_LC_WHY + "สถานะเริ่มต้น + เวลาถือห้อง + โหมดที่ตรึงบนใบ + เหตุการณ์แจ้งเตือน จากตัวตัดสินเดียว (เส้นพนักงานไม่ถูกบังคับสลิป)"),
    dict(file=LODGING_RES, method="BuildQuote",
         must=["LodgingGuestConfirmPolicy.QuotedDeposit(", "LodgingGuestConfirmPolicy.ForChannel(", "LodgingGuestConfirmPolicy.QuoteTerms(", "reservationMode ??"],
         forbid=["ConfirmWithoutDeposit", "DepositRequired = 0m"],
         why=_LC_WHY + "มัดจำที่ต้องชำระตามโหมด (Instant 0 · RequireSlip มัดจำ 0 = ยอดเต็ม) · เลื่อนวันใช้โหมดที่ตรึงบนใบ"),
    dict(file=LODGING_LIFE, method="RescheduleAsync",
         call_args=[("BuildQuote(", "r.GuestConfirmMode")],
         why=_LC_WHY + "เลื่อนวันคิดมัดจำตามโหมดที่ตรึงบนใบ (ไม่ใช่โหมดของเส้นพนักงาน — ใบส่งสลิปมัดจำ 0 ต้องไม่ตกเป็น ฿0)"),
    dict(file=LODGING_LIFE, method="UploadSlipByTokenAsync",
         must=["LodgingGuestConfirmPolicy.OnSlipUploaded(", "problemFound: problem != null", "LodgingSlipOutcome.ConfirmNow",
               "LodgingGuestConfirmPolicy.SlipConfirmActor", "LodgingGuestConfirmPolicy.SlipUploadedMessage("],
         before=[("WithPropertyLockAsync(", "LodgingGuestConfirmPolicy.OnSlipUploaded("),
                 ("LodgingHoldRule.HoldLapsed(", "LodgingGuestConfirmPolicy.OnSlipUploaded("),
                 ("LodgingGuestConfirmPolicy.OnSlipUploaded(", "_db.SaveChangesAsync(")],
         forbid=["DepositPaid +=", "PaidAmount +=", "DepositPaid =", "PaidAmount ="],
         why=_LC_WHY + "ยืนยันจากสลิปตัดสินใต้ล็อกหลังตรวจห้องว่าง (hold หมดแล้วห้องเต็ม ⇒ ธงข้อ 127) · ส่งสลิป ≠ รับเงิน"),
    dict(file=LODGING_LIFE, method="RejectSlipAsync",
         must=["WithReservationLockAsync<", "AdvisoryLockKey.LodgingConfirm", "ReloadAsync(", "LodgingGuestConfirmPolicy.IsSlipConfirmed(",
               "LodgingGuestConfirmPolicy.HoldAfterSlipRejected("],
         before=[("ReloadAsync(", "LodgingGuestConfirmPolicy.IsSlipConfirmed(")],
         forbid=["AddHours(24)"],
         why=_LC_WHY + "ปฏิเสธสลิปของใบที่ยืนยันเพราะสลิป ⇒ กลับรอชำระ + hold (ตัวตัดสินเดียว) ใต้ล็อกต่อการจองเดียวกับการรับเงิน"),
    dict(file=LODGING_LIFE, method="GetReservationByTokenAsync",
         must=["ExpireHoldsAsync(", "ReloadAsync("],
         before=[("ExpireHoldsAsync(", "MapAsync(")],
         why=_LC_WHY + "หน้าแขกหลังหมดเวลาส่งสลิปต้องเห็น \"ถูกยกเลิก\" (ตัวยกเลิกกติกาเดียว) ไม่ใช่ \"รอสลิป\" ที่ห้องถูกปล่อยแล้ว"),
    dict(file=LODGING_LIFE, method="ListReservationsAsync",
         must=["LodgingGuestConfirmPolicy.AwaitingSlipReview", "LodgingGuestConfirmPolicy.StaffSlipLabel("],
         forbid=["r.SlipUploadedAt != null && r.DepositPaid == 0"],
         why=_LC_WHY + "คิว \"มีสลิปรอตรวจ\" รวมใบที่ยืนยันอัตโนมัติจากสลิปที่ยังไม่บันทึกรับเงิน · ป้ายรอสลิป/สลิปรอตรวจจากตัวตัดสินเดียว"),
    dict(file=LODGING_OPS, method="MapAsync",
         must=["LodgingGuestConfirmPolicy.GuestView(", "LodgingGuestConfirmPolicy.StaffSlipLabel(", "LodgingGuestConfirmPolicy.AwaitingSlipMoneyCheck("],
         why=_LC_WHY + "ป้าย/ข้อความ/ยอดที่ต้องโอน/ช่องส่งสลิปของแขก มาจากเซิร์ฟเวอร์ (หน้าเว็บห้ามตัดสินเอง)"),
    dict(file=LODGING, method="Apply",
         must=["LodgingGuestConfirmPolicy.ModeOnSave(", "LodgingGuestConfirmPolicy.SlipDeadlineProblem(", "LodgingGuestConfirmPolicy.LegacyConfirmWithoutDeposit("],
         before=[("LodgingGuestConfirmPolicy.SlipDeadlineProblem(", "p.SlipDeadlineMinutes = newSlipDeadline")],
         forbid=["p.ConfirmWithoutDeposit = d.ConfirmWithoutDeposit", "p.SlipDeadlineMinutes = d.SlipDeadlineMinutes", "p.AutoConfirmOnSlip = d.AutoConfirmOnSlip"],
         why=_LC_WHY + "ธงเดิมเป็นสำเนาที่ระบบเขียนตามโหมด (ทางเดียว) · ส่งสลิปภายในนอกช่วง ⇒ ปฏิเสธก่อนแตะ entity"),
    # ── ผลฝ่ายค้านรอบ 202 บนงานข้อ 128 ──
    dict(file=LODGING_LIFE, method="CheckInAsync",
         must=["LodgingGuestConfirmPolicy.CheckInProblem(", "LodgingGuestConfirmPolicy.CheckInRuleCode", "ReloadAsync("],
         before=[("ReloadAsync(", "ExtendStayOneNightEarlierAsync(")],
         why=_LC_WHY + "ฝ่ายค้าน P1-1 ใบยืนยันเพราะสลิปที่ยังไม่บันทึกรับเงินเช็คอินไม่ได้ (เดิมเช็คอินได้แล้วหลุดทุกคิว) · ตรวจซ้ำใต้ล็อกหลังอ่านแถวใหม่"),
    dict(file=LODGING_LIFE, method="CancelCoreAsync",
         must=["LodgingGuestConfirmPolicy.CancelLeavesUnverifiedSlip(", "FlagPaymentProblemAsync(", "LodgingGuestConfirmPolicy.UnverifiedSlipOnCancelNote("],
         before=[("_db.SaveChangesAsync(", "LodgingGuestConfirmPolicy.CancelLeavesUnverifiedSlip(")],
         forbid=["RefundAmount +="],
         why=_LC_WHY + "ฝ่ายค้าน P1-1 ยกเลิก/no-show ใบที่มีสลิปค้างตรวจ ⇒ ธงข้อ 127 (เงินตามสลิปอาจต้องคืน) · ไม่ประทับยอดคืนเอง"),
    dict(file=LODGING_LIFE, method="RejectSlipAsync",
         must=["WithPropertyLockAsync<", "LodgingGuestConfirmPolicy.RejectSlipStaleProblem(", "LodgingGuestConfirmPolicy.RejectSlipStaleRuleCode"],
         before=[("WithReservationLockAsync<", "WithPropertyLockAsync<"), ("ReloadAsync(", "LodgingGuestConfirmPolicy.RejectSlipStaleProblem(")],
         why=_LC_WHY + "ฝ่ายค้าน P2-2 ปฏิเสธสลิปถือล็อกการจอง→ที่พัก (กันเช็คอิน/สลิปใบที่สองแทรก) + ข้อมูลที่พนักงานเห็นต้องเป็นปัจจุบัน"),
    dict(file=LODGING_OPS, method="MapAsync",
         must=["LodgingGuestConfirmPolicy.OnlinePaymentBlockedNote("],
         why=_LC_WHY + "ฝ่ายค้าน P2-1 สลิปรอตรวจ ⇒ ไม่ชวนแขกจ่ายออนไลน์ซ้ำ (ตัวตัดสินเดียวกับตัวคิดยอด gateway)"),
    dict(file="Services/Payments/PublicPaymentResolver.cs", method="ResolveLodgingAsync",
         must=["LodgingGuestConfirmPolicy.OnlinePaymentBlockedNote(", "LodgingAmounts.OnlinePayableAmount("],
         why=_LC_WHY + "ฝ่ายค้าน P2-1 ยอดที่ gateway เก็บต้องเดินด่านเดียวกับหน้าแขก (ข้ามหน้าเว็บก็จ่ายซ้ำไม่ได้)"),
    dict(file=LODGING_OPS, method="GetDashboardAsync",
         must=["LodgingGuestConfirmPolicy.AwaitingSlipReview"],
         forbid=["r.SlipUploadedAt != null && r.DepositPaid == 0"],
         why=_LC_WHY + "ฝ่ายค้าน P2-3 ตัวนับสลิปรอตรวจเงื่อนไขเดียวกับตัวกรองรายการ"),
    dict(file=LODGING_OPS, method="ToListItem",
         must=["LodgingGuestConfirmPolicy.StaffSlipLabel("],
         why=_LC_WHY + "ฝ่ายค้าน P2-3 ป้ายสลิปบนแดชบอร์ด (เข้าพัก/ออกวันนี้)"),
    dict(file=LODGING_RES, method="ExpireHoldsAsync",
         must=["LodgingGuestConfirmPolicy.AutoExpireReasonFor("],
         forbid=["= LodgingHoldRule.AutoExpireReason"],
         why=_LC_WHY + "ฝ่ายค้าน P2-5 เหตุผลยกเลิกอัตโนมัติตามโหมดที่ตรึงบนใบ (ตัวอ่าน IsAutoExpiredHold รู้จักทั้งสองข้อความ)"),
    dict(file="Helpers/LodgingVoucherBuilder.cs", method="BuildHtml",
         must=["LodgingGuestConfirmPolicy.VoucherTitle(", "r.GuestStatusLabel ??", "r.SlipRequired"],
         why=_LC_WHY + "ฝ่ายค้าน P2-4 หลักฐานการจองของใบที่ยังไม่สำเร็จห้ามพิมพ์หัว \"ยืนยันการจอง\""),
    dict(file="Controllers/LodgingPublicController.cs", method="Create",
         must=["LodgingGuestConfirmPolicy.CreatedMessage("],
         forbid_lit=["จองสำเร็จ เลขที่"],
         why=_LC_WHY + "โหมดส่งสลิปก่อน ⇒ ห้ามตอบ \"จองสำเร็จ\" — ข้อความจากตัวตัดสินเดียวกับหน้าแขก"),
]


# ── PP36_REVIEW 2026-10-02 (ทีม F1): P0-1 ด่าน JE รู้จัก §83/6 ผ่านตัวตัดสินเดียว · P0-2 อนุมัติล้มต้องถอยค่าค้าง + ผู้เรียกล้มดัง ·
# P0-3 integration PV ห้ามใบ Approved ไม่มี JE · ซ่อมข้อมูลเดิม (ลงบัญชีให้ใบอนุมัติแล้วแต่ไม่มี JE) — เทสต์ล็อกแค่ helper (pure + DbContext) ⇒ ล็อกจุดเรียกที่นี่
_PP36_WHY = "PP36_REVIEW (ทีม F1) — "
JOURNAL_ANOMALY = "Services/Implementations/JournalAnomalyService.cs"
DOC_CTRL = "Controllers/DocumentController.cs"
OCR_CTRL = "Controllers/OcrController.cs"
RECURRING = "Services/Implementations/RecurringTransactionService.cs"
LINEBOT = "Services/Implementations/LineBotService.cs"
SETTLE_POST = "Services/Settlement/SettlementPostingService.cs"
RULES += [
    dict(file=DOC, method="AutoPostToJournalAsync",
         call_args=[("JournalPostingGuard.DocFacts(", "IsForeignService")],
         why=_PP36_WHY + "P0-1 ด่านก่อนบันทึกต้องได้ธง §83/6 จริง — ไม่ส่ง ⇒ JE ภ.พ.36 ที่ถูกต้องถูกตีตกทุกใบ (JE-NO-COUNTERPART)"),
    dict(file=JOURNAL_ANOMALY, method="ScanAsync",
         call_args=[("JournalPostingGuard.DocFacts(", "IsForeignService")],
         must=["MissingJournalRepair.ScannerFix", "DocumentJournalExpectation.ExpectsLiveJournal(", "DocumentJournalExpectation.PostingTypes"],
         must_re=[r"OriginalEntryId\s*==\s*null\s*&&\s*j\.ReversedByEntryId\s*==\s*null"],
         forbid=["JePostingTypes"],
         forbid_lit=["ยกเลิกเอกสารแล้วอนุมัติใหม่"],
         why=_PP36_WHY + "P0-1 ตัวสแกนใช้กฎชุดเดียวกับด่าน (ธง §83/6) · DOC-NO-JE ชี้เครื่องมือลงบัญชีย้อนหลัง ไม่ใช่ 'ยกเลิกแล้วอนุมัติใหม่'"),
    dict(file=INTEG, method="ValidateAndAutofixJournalAsync",
         call_args=[("JournalPostingGuard.DocFacts(", "IsForeignService")],
         why=_PP36_WHY + "P0-1 เส้น integration ใช้กฎ §83/6 ชุดเดียวกัน"),
    dict(file=DOC, method="ApproveDocumentAsync#2",
         must=["TrackedChangeRevert.Capture(", "RevertTrackedChangesSinceAsync(approveBaseline)", "AutoApproveFailure.MarkResolved("],
         must_re=[r"finally\s*\{\s*await\s+RevertTrackedChangesSinceAsync\s*\(\s*approveBaseline\s*\)\s*;\s*\}\s*throw\s*;"],
         before=[("TrackedChangeRevert.Capture(", "BeginTransactionAsync(")],
         why=_PP36_WHY + "P0-2 ธุรกรรมอนุมัติล้ม ⇒ ถอยค่าค้างใน context ก่อนโยน (ปิดผู้เรียกทุกตัวที่จับ error แล้ว SaveChanges ต่อ)"),
    dict(file=DOC, method="RevertTrackedChangesSinceAsync#1",
         must=["TrackedChangeRevert.RevertAsync(", "ChangeTracker.Clear(", "LogError("],
         why=_PP36_WHY + "P0-2 ตัวถอยแบบจำงานค้างของผู้เรียก · ถอยล้ม ⇒ Clear ทั้ง context (ห้ามปล่อยครึ่งทาง) + log Error"),
    dict(file=DOC, method="CreateDocumentAsync",
         must=["RecordAutoApproveFailureAsync(", "AutoApproveFailedReason"],
         forbid=["LogInformation(ex"],
         why=_PP36_WHY + "P0-2 อนุมัติอัตโนมัติ PV เงินสดล้ม ⇒ หมายเหตุบนเอกสาร + คำตอบ + log Warning (เดิม LogInformation 'staying Draft' อย่างเดียว)"),
    dict(file=DOC, method="RecordAutoApproveFailureAsync",
         must=["AutoApproveFailure.Classify(", "DocumentJournalExpectation.ExpectsLiveJournal(", "AutoApproveFailure.Message(",
               "DepositPolicyResolver.AppendNoteOnce(", "SaveChangesAsync(", "LogWarning(", "LogError("],
         must_re=[r"kind\s*!=\s*AutoApproveFailureKind\.Closed", r"catch\s*\(\s*Exception\s+recordEx\s*\)"],
         before=[("AsNoTracking(", "AutoApproveFailure.Classify(")],
         why=_PP36_WHY + "P0-2 ล้มดัง 3 ที่ด้วยสถานะจริงจากฐานข้อมูล (ขั้นหลัง commit ล้มได้ทั้งที่อนุมัติแล้ว)"),
    dict(file=APPROVAL, method="TryFinalizeApprovedEntityAsync",
         must=["RecordAutoApproveFailureAsync("],
         why=_PP36_WHY + "P0-2 ลำดับอนุมัติครบแต่อนุมัติเอกสารล้ม ⇒ หมายเหตุบนเอกสาร + คืนข้อความ (เดิม Console.Error อย่างเดียว)"),
    dict(file=APPROVAL, method="SubmitActionAsync",
         must=["FinalizeError"],
         why=_PP36_WHY + "P0-2 คำตอบของ workflow ต้องบอกว่าอนุมัติเอกสารไม่สำเร็จ"),
    dict(file=DOC_CTRL, method="BulkApprove",
         must=["RecordAutoApproveFailureAsync(", "outcome.Committed"],
         must_re=[r"before\.Status\s+is\s+not"],
         before=[("before.Status", "ApproveDocumentAsync(")],
         why=_PP36_WHY + "P0-2 ผลรายใบบอกเลข+เหตุผล+สถานะจริง"),
    dict(file=OCR_CTRL, method="CreateDocument",
         must=["RecordAutoApproveFailureAsync("],
         why=_PP36_WHY + "P0-2 สแกนสร้าง+อนุมัติล้ม ⇒ หมายเหตุบนเอกสาร (ไม่ใช่แค่แถวสแกน)"),
    dict(file=RECURRING, method="CreateDocumentFromTemplateAsync",
         must=["RecordAutoApproveFailureAsync("],
         why=_PP36_WHY + "P0-2 รายการประจำอนุมัติอัตโนมัติล้ม ⇒ หมายเหตุบนเอกสาร (เดิม log อย่างเดียว)"),
    dict(file=LINEBOT, method="HandleMessageAsync",
         must=["RecordAutoApproveFailureAsync("],
         why=_PP36_WHY + "P0-2 LINE บันทึกค่าใช้จ่าย — สถานะจริง + หมายเหตุบนเอกสาร"),
    dict(file=LINEBOT, method="HandlePostbackAsync",
         must=["RecordAutoApproveFailureAsync("],
         why=_PP36_WHY + "P0-2 LINE ปุ่มอนุมัติ — สถานะจริง + หมายเหตุบนเอกสาร"),
    dict(file=SETTLE_POST, method="ApproveIfDraftAsync",
         must=["GetMissingJournalStatusAsync("],
         before=[("ApproveDocumentAsync(", "GetMissingJournalStatusAsync(")],
         why=_PP36_WHY + "P0-2 รอบโอนห้ามถือว่า Paid = มี JE"),
    dict(file=DOC, method="RepairMissingJournalAsync",
         must=["TrackedChangeRevert.Capture(", "LockRelatedSourceDocumentAsync(", "MissingJournalRepair.Decide(",
               "AutoPostToJournalAsync(", "AddChainedAuditLog(", "RevertTrackedChangesSinceAsync(baseline)"],
         must_lit=["FOR UPDATE"],
         before=[("LockRelatedSourceDocumentAsync(", "MissingJournalRepair.Decide("), ("MissingJournalRepair.Decide(", "AutoPostToJournalAsync(")],
         why=_PP36_WHY + "ซ่อมข้อมูล: ตัดสินซ้ำภายใต้ล็อก · AutoPost ตัวเดียวกับการอนุมัติ · audit · ล้มแล้วถอย"),
    dict(file=DOC_CTRL, method="RepairMissingJournal",
         must=["DocumentPermissionHelper.CanApproveAsync("],
         before=[("DocumentPermissionHelper.CanApproveAsync(", "RepairMissingJournalAsync(")],
         why=_PP36_WHY + "ซ่อมข้อมูล: สิทธิ์เดียวกับอนุมัติ (มีผลต่อ GL เท่ากัน)"),
    dict(file=INTEG, method="ProcessPaymentVoucherAsync",
         must=["RunAtomicCreateAsync("],
         before=[("CreatePaymentVoucherJournalAsync(", "RunAtomicCreateAsync("), ("RunAtomicCreateAsync(", "TryAutoGenerateWhtAsync(")],
         why=_PP36_WHY + "P0-3 ใบ Approved + JE ในธุรกรรมเดียว · ล้มแล้วทิ้งของค้างก่อน SaveSyncLog"),
    dict(file=INTEG, method="RunAtomicCreateAsync",
         must=["BeginTransactionAsync(", "CommitAsync(", "ChangeTracker.Clear("],
         before=[("body()", "CommitAsync(")],
         why=_PP36_WHY + "P0-3/P2-4 สร้างใบอนุมัติแล้ว + JE เป็นหน่วยเดียว · ล้มแล้วทิ้งของค้างก่อน SaveSyncLog"),
    dict(file=INTEG, method="ProcessExpenseAsync",
         must=["RunAtomicCreateAsync(", "PostMappingJournalOrThrowAsync("],
         forbid=["PostMappingJournalAsync("],
         before=[("RunAtomicCreateAsync(", "TryAutoGenerateWhtAsync(")],
         why=_PP36_WHY + "P2-4 integration expense ห้ามใบ Approved ไม่มี JE (เดิมเก็บใบ + [ยังไม่ลงบัญชี])"),
    dict(file=INTEG, method="ProcessCertificateInLieuAsync",
         must=["RunAtomicCreateAsync(", "PostMappingJournalOrThrowAsync("],
         forbid=["PostMappingJournalAsync("],
         why=_PP36_WHY + "P2-4 integration ใบรับรองแทนใบเสร็จ ห้ามใบ Approved ไม่มี JE"),
    dict(file=INTEG, method="PostMappingJournalOrThrowAsync",
         must=["CreateJournalFromMappingsAsync("],
         must_re=[r"throw\s+new\s+Accounting\.Helpers\.BusinessRuleException"],
         why=_PP36_WHY + "P2-4 ลง JE ไม่ได้ต้องโยน"),
    dict(file="Services/Implementations/CmsCommerceService.cs", method="ApproveOrderDocumentIfPendingAsync",
         must=["RecordAutoApproveFailureAsync("],
         why=_PP36_WHY + "P2-4 CMS อนุมัติเอกสาร ERP ล้ม ⇒ หมายเหตุบนเอกสาร"),
    dict(file=PLATFORM, method="NoteDraftFailureAsync",
         must=["RecordAutoApproveFailureAsync("],
         why=_PP36_WHY + "P2-4 แพลตฟอร์ม: ใบร่างที่อนุมัติล้มต้องมีหมายเหตุ"),
    dict(file=PLATFORM, method="IssuePaidReceiptAsync",
         must=["NoteDraftFailureAsync("], why=_PP36_WHY + "P2-4"),
    dict(file=PLATFORM, method="IssueRenewalInvoiceAsync",
         must=["NoteDraftFailureAsync("], why=_PP36_WHY + "P2-4"),
    dict(file=PLATFORM, method="IssueUsageInvoiceAsync",
         must=["NoteDraftFailureAsync("], why=_PP36_WHY + "P2-4"),
    dict(file=DOC, method="LoadMissingJournalFactsAsync",
         must=["MissingJournalRepair.SideEffectsOf(", "AccountCode.StartsWith("],
         why=_PP36_WHY + "P1-A ใบที่การอนุมัติมีผลนอก JE ต้องถูกปฏิเสธ (ใบต้นทาง · WHT · สต็อก · PPE · ใบแทน · โครงการ · มัดจำ)"),
    dict(file=DOC, method="ReclassifyForeignServiceAsync",
         must_lit=["FOR UPDATE"],
         must_re=[r"lockedFlag\s*==\s*toForeignService"],
         before=[("ExecuteSqlRawAsync(", "ReverseJournalEntryAsync(")],
         why=_PP36_WHY + "P2-8 ล็อกแถว + ตัดสินซ้ำใต้ล็อกก่อนกลับ/ลง JE (กัน JE คู่)"),
    dict(file=INTEG, method="CreatePaymentVoucherJournalAsync",
         forbid=["return null"],
         why=_PP36_WHY + "P0-3 ลง JE ไม่ได้ต้องโยน (ห้ามคืน null ขณะใบเป็น Approved)"),
]

# ── รอบ 203 ทีม F3 (PP36_REVIEW E-2..E-10 · T-2 · คำตัดสินข้อ 129, 132–136): วงจรนำส่ง/รับรู้ ภ.พ.36 ──
# ทุกเส้นคัดใบด้วยตัวโหลด/ตัวตัดสินเดียว (Pp36Ledger + Pp36Lifecycle + ForeignServiceVat.OwnsPp36) — ใบต้องมี Cr 21912 ใน GL จริง ·
# รับรู้เฉพาะใบที่อยู่ในรายการนำส่ง · วันเคลม = วันใบเสร็จ RD ผ่าน TaxService.ClaimBasisDate · ด่านแก้ใบหลังนำส่งทุกทางเข้า (pure ทดสอบใน Pp36LifecycleTests ·
# เส้นจริงใน Db/Pp36LifecycleGoldenDbTests)
_PP36_WHY = "รอบ 203 ทีม F3 (ภ.พ.36): "
STATREM = "Services/Implementations/StatutoryRemittanceService.cs"
RULES += [
    dict(file=STATREM, method="RecognizePp36InputVatAsync",
         must=["Pp36Ledger.LoadDocsAsync(", "Pp36DocState.RemittedAwaitingRecognition", "Ledger.UndueInputVat",
               "TaxService.ClaimBasisDate(", "Pp36Lifecycle.ResolveClaimDate(", "Pp36Lifecycle.RuleNoReceipt",
               "TaxFilingLockPolicy.DeclaredOrFiledStatuses", "RecognizedJournalEntryId = je.Id", "ChangeTracker.Clear("],
         must_re=[r"if\s*\(\s*claimError\s*!=\s*null\s*\)\s*throw\b", r"if\s*\(\s*claimPeriodClosed\s*\)\s*throw\b"],
         call_args=[("CreateJournalEntryRequest(", "claimAt")],
         before=[("Pp36Lifecycle.ResolveClaimDate(", "CreateJournalEntryAsync("),
                 ("DeclaredOrFiledStatuses", "CreateJournalEntryAsync("),
                 ("ExecuteSqlRawAsync(", "CreateJournalEntryAsync(")],
         forbid=["SupplierTaxInvoiceDate", "d.VatAmount)", "Sum(d => d.VatAmount"],
         why=_PP36_WHY + "E-2/E-3/E-5 คำตัดสินข้อ 129/133/136 — รับรู้เฉพาะใบในรายการนำส่ง · ยอด = 11640 ใน GL · เลข/วันที่ใบเสร็จบังคับ · วันเคลม = วันใบเสร็จ (ห้ามก่อน) · ห้ามงวด ภ.พ.30 ที่ยื่นแล้ว · JE ลงวันเคลม"),
    dict(file=STATREM, method="RemitAsync",
         must=["Pp36Ledger.LoadDocsAsync(", "Pp36DocState.AwaitingRemittance", "Pp36RemittanceDocuments.Add(", "Ledger.Pp36Payable",
               "Pp36SurchargeFor(", "ChangeTracker.Clear("],
         before=[("ExecuteSqlRawAsync(", "Pp36RemittanceDocuments.Add(")],
         why=_PP36_WHY + "E-5 คำตัดสินข้อ 133/135 — นำส่งเพิ่มเติมงวดเดิมได้ (ห้ามนับใบเดิมซ้ำ: ล็อกแถวเอกสาร + ตรวจซ้ำ + ผูกใบ) · ยอด = Cr 21912 ใน GL · เงินเพิ่ม §89/1 ค่าแนะนำ"),
    dict(file=STATREM, method="GetDashboardAsync",
         must=["Pp36Ledger.LoadDocsAsync(", "BuildPp36Issues(", "Pp36DocState.AwaitingRemittance", "Ledger.UndueInputVat"],
         forbid=["d.IsForeignService && d.VatAmount > 0", "g.Sum(x => x.VatAmount) - Remitted("],
         why=_PP36_WHY + "E-3/E-6/E-8 ยอดค้าง/รอรับรู้จากตัวโหลดเดียว (ใบมี JE · ยังไม่นำส่ง · บาท) · ใบไม่มี JE/นำส่งเกินขึ้นเป็นรายการต้องตรวจ (ไม่ continue เงียบ)"),
    dict(file=STATREM, method="GetFilingCalendarAsync",
         must=["Pp36Ledger.LoadDocsAsync(", "CountedVat"],
         forbid=["d.IsForeignService && d.VatAmount > 0"],
         why=_PP36_WHY + "E-8 ปฏิทินนับ ภ.พ.36 ชุดเดียวกับหน้านำส่ง"),
    dict(file=STATREM, method="BuildItem",
         must=["Pp36Lifecycle.SuggestedSurcharge("],
         why=_PP36_WHY + "E-9 คำตัดสินข้อ 135 เงินเพิ่ม §89/1 ที่เสนอบนแถวรอนำส่ง"),
    dict(file="Services/Implementations/TaxService.cs", method="GeneratePp36Report",
         must=["Pp36Ledger.LoadDocsAsync(", "CountedVat", "DocumentFx.ToBaht("],
         forbid=["d.TaxPointDate ?? d.DocumentDate", "TaxAmount = doc.VatAmount"],
         why=_PP36_WHY + "E-3/E-4/E-8 รายงาน ภ.พ.36 นับเฉพาะใบที่มี JE (เจ้าของหนี้ · ชุดชนิดเดียว · งวด = วันจ่าย) · ยอดเป็นบาท"),
    dict(file="Services/Implementations/TaxGlReconciliationService.cs", method="ReconcileAsync",
         must=["Pp36Ledger.LoadDocsAsync(", "!pp36RemitJeIds.Contains(", "Pp36DocState.NoJournal", "Pp36Ledger.IsUndueCode("],
         why=_PP36_WHY + "E-10 เดือนนำส่งไม่ฟ้องผลต่างปลอม (ตัด JE นำส่ง) · สาเหตุ \"ใบอนุมัติแล้วไม่มี JE\" · บรรทัด 11640"),
    dict(file="Services/Implementations/Tax/TaxPointResolver.cs", method="IsReverseCharge",
         must=["ForeignServiceVat.IsSelfAssessingType("],
         why=_PP36_WHY + "E-8 tax point ใช้ชุดชนิดเดียวกับ AutoPost/รายงาน/ด่านติ๊กธง"),
    dict(file=DOC, method="Pp36SettledReasonAsync",
         must=["Pp36Ledger.RemittedStatusAsync(", "Pp36Lifecycle.SettledReason("],
         forbid=["Set<StatutoryRemittance>("],
         forbid_lit=["RemittanceType == \"VatPp36\""],
         why=_PP36_WHY + "ด่านใบลด/เพิ่มหนี้ (F2 E-7) ตัดสินต่อใบด้วยตัวตรวจเดียวกับด่านยกเลิก/ปลดธง (คำตัดสินข้อ 133/134) — ไม่ใช่ระดับงวด"),
    # ── ฝ่ายค้านบน merge F2+F3 (รอบ 203 · P1-2 · P1-3 · P2-2 · P2-3 · P2-6 · คำตัดสินข้อ 137) ──
    dict(file="Helpers/Pp36Ledger.cs", method="LoadAsync",
         must=["AdjustmentNotesOfAsync(", "ownerOf.TryGetValue("],
         why=_PP36_WHY + "P1-2 ยอด 21912/11640 ของใบเจ้าของรวม JE ใบลด/เพิ่มหนี้ที่อ้างใบนั้น (ยอดค้าง/นำส่ง/รายงาน/รับรู้/ป้าย/กระทบยอด ใช้ตัวนี้ตัวเดียว)"),
    dict(file=STATREM, method="RecognizePp36InputVatAsync",
         must=["Pp36Lifecycle.RemittanceGap(", "Pp36Lifecycle.RuleUnderRemitted", "TaxService.EvaluateClaimPeriod(", "Pp36Lifecycle.RuleClaimWindow"],
         must_re=[r"if\s*\(\s*rdReceiptDate\s+is\s+not\s+DateTime\b"],
         before=[("TaxService.EvaluateClaimPeriod(", "CreateJournalEntryAsync("), ("Pp36Lifecycle.RemittanceGap(", "CreateJournalEntryAsync(")],
         forbid=["rdReceiptDate ?? remittance.PayDate"],
         why=_PP36_WHY + "P2-1/P2-2 วันที่ใบเสร็จบังคับ · เพดาน §82/3 ตัวตัดสินเดียวกับ ภ.พ.30 · รายการนำส่งที่จ่ายขาดรับรู้ไม่ได้"),
    dict(file=DOC, method="LoadMissingJournalFactsAsync",
         must=["Pp36Ledger.RemittedStatusAsync(", "ForeignServiceVat.OwnsPp36(doc)"],
         forbid=["Set<StatutoryRemittance>("],
         forbid_lit=["RemittanceType == \"VatPp36\""],
         why=_PP36_WHY + "P1-3 เครื่องมือซ่อมตัดสินต่อใบ (คำตัดสินข้อ 133) — ใบที่ไม่เคยถูกนับในงวดที่นำส่งแล้วต้องซ่อมได้"),
    dict(file="Services/Implementations/TaxService.cs", method="GenerateVatReport",
         must=["DocumentFx.ToBaht(", "Thb(doc, doc.VatAmount)", "Thb(doc, VatableBase(doc))"],
         forbid=["outputVat += doc.VatAmount", "inputVat += doc.VatAmount", "inputVat -= doc.VatAmount", "outputVat -= doc.VatAmount",
                 "TaxAmount = doc.VatAmount", "TaxAmount = -doc.VatAmount", "IncomeAmount = VatableBase(doc)"],
         why=_PP36_WHY + "P2-3 ทุกบรรทัดภาษีซื้อ/ขายของ ภ.พ.30 เป็นบาท (ตัวแปลงเดียวกับ JE)"),
    dict(file=DOC, method="CreatePaymentAsync",
         must=["Pp36Ledger.StampFirstPaymentAsync("],
         why=_PP36_WHY + "P2-6 คำตัดสินข้อ 137 งวด ภ.พ.36 = เดือนที่จ่ายครั้งแรก (รอบโอน/gateway/ที่พัก/CMS เข้าทางนี้)"),
    dict(file=DOC, method="CreateMultiDocPaymentAsync",
         must=["Pp36Ledger.StampFirstPaymentAsync("],
         why=_PP36_WHY + "P2-6 คำตัดสินข้อ 137 จับคู่จ่ายหลายใบ"),
    dict(file=DOC, method="ApplySourceDocumentAdjustmentsAsync",
         must=["Pp36Ledger.StampFirstPaymentAsync("],
         why=_PP36_WHY + "P2-6 คำตัดสินข้อ 137 ใบสำคัญจ่ายปิดหนี้ใบต้นทางตอนอนุมัติ"),
    dict(file="Services/Implementations/IntegrationService.cs", method="ProcessPaymentAsync",
         must=["Pp36Ledger.StampFirstPaymentAsync("],
         why=_PP36_WHY + "P2-6 คำตัดสินข้อ 137 จ่ายผ่าน integration"),
    # รอบ 205 ทีม B: เอกสาร/รับ-จ่ายชำระ/สินทรัพย์จากทางเดิม (นำเข้าไฟล์ · Smart Import) ส่งต่อปลายทางของทีม D ตัวเดียวกับศูนย์นำเข้า ·
    # ห้ามกลับไปเขียนเอง (PaidAmount ตรง = AR ลดแต่ GL ไม่ขยับ · ที่ดินถูกคิดค่าเสื่อม · เอกสารไม่ผ่าน DocumentService)
    dict(file="Services/Implementations/ImportExportService.cs", method="WriteUnitAsync",
         must=["WriteViaAccountingTargetAsync("],
         forbid=["PaidAmount +=", "_db.Payments.Add(", "_db.FixedAssets.Add(", "_db.Documents.Add("],
         why=_PP36_WHY + "P2-6 คำตัดสินข้อ 137 · รอบ 205 ทีม B/D: ทางเดิมของเอกสาร/การชำระ/สินทรัพย์ = ปลายทางเดียวกับศูนย์นำเข้า"),
    dict(file="Services/Implementations/ImportExportService.cs", method="WriteViaAccountingTargetAsync",
         must=["target.ValidateAsync(", "target.CommitGroupAsync(", "ImportCellNormalizer.Normalize("],
         before=[("target.ValidateAsync(", "target.CommitGroupAsync(")],
         why="รอบ 205 ทีม B: ตรวจด้วยกติกาของปลายทางก่อนลงเสมอ (ข้อผิดของบรรทัดใด = ทั้งหน่วยไม่ลง)"),
    dict(file="Services/Implementations/ImportExportService.cs", method="AccountingTarget",
         must=["new DocumentImportTarget(", "new PaymentImportTarget(", "new FixedAssetImportTarget("],
         why="รอบ 205 ทีม B/D: สามชนิดนี้มีปลายทางตัวเดียว (ทีม D)"),
    # ปิดลูป AI ของการนำเข้า (กฎเหล็ก #1 · ฝ่ายค้านรอบ 205: RecordConfirmed/RecordPick ไม่มีผู้เรียก)
    dict(file="Services/Import/ImportPipelineService.cs", method="RemapAsync", must=["RecordColumnChoiceAsync("],
         why="รอบ 205 ทีม B: ผู้ใช้แก้การจับคู่คอลัมน์ = คำตอบสุดท้ายของ ImportColumnMatch"),
    dict(file="Services/Import/ImportPipelineService.Engine.cs", method="FinishCommitAsync",
         must=["RecordColumnChoiceAsync(", "RecordMatchChoicesAsync("],
         why="รอบ 205 ทีม B: commit = ผู้ใช้ยืนยันการจับคู่คอลัมน์ + คู่ข้อมูลเดิมของทุกแถวที่ลง"),
    dict(file="Services/Import/ImportPipelineService.Engine.cs", method="RecordMatchChoicesAsync",
         must=["_ai.RecordPickChoiceAsync("],
         why="รอบ 205 ทีม B: ปิดลูป ImportEntityMatch (59) ผ่าน augmenter (ตรวจว่า feedback เป็นของบริษัทนี้)"),
    dict(file="Services/Implementations/ImportExportService.cs", method="RecordSmartColumnChoiceAsync",
         must=["_columnMatch.RecordConfirmedAsync("],
         why="รอบ 205 ทีม B: ปิดลูป ImportColumnMatch ของ Smart Import"),
    dict(file="Controllers/AiSuggestionController.cs", method="ConfirmImportMapping",
         must=["permissions.HasPermissionAsync(", "ImportExportPermissionScope.ForImport(", "matcher.RecordConfirmedAsync("],
         before=[("permissions.HasPermissionAsync(", "matcher.RecordConfirmedAsync(")],
         why="รอบ 205 ทีม B: suggest-mapping คืน feedbackId — ต้องมีทางบันทึกการยืนยัน (เดิมข้อความอ้างว่าเรียนแต่ไม่มีผู้เรียก)"),
    dict(file="Services/Implementations/ImportExportService.cs", method="RecordAiReviewChoiceAsync",
         must=["_ai.RecordReviewChoiceAsync(", "s.CompanyId == companyId"],
         why="รอบ 205 ทีม B: ปิดลูป ImportDataReview — รับ/ปฏิเสธคำแนะนำของผลตรวจ"),
    dict(file="Controllers/ImportExportController.cs", method="AiReviewChoice",
         must=["await DenyAsync(", "RecordAiReviewChoiceAsync("],
         before=[("await DenyAsync(", "RecordAiReviewChoiceAsync(")],
         why="รอบ 205 ทีม B: สิทธิ์นำเข้าชนิดนั้นก่อนบันทึกการรับ/ปฏิเสธ"),
    dict(file="Services/Implementations/ImportExportService.cs", method="SubmitManualMappingAsync",
         must=["RecordSmartColumnChoiceAsync("], why="รอบ 205 ทีม B: ผู้ใช้แก้การจับคู่ = คำตอบสุดท้าย"),
    dict(file="Services/Implementations/ImportExportService.cs", method="ConfirmAndImportAsync",
         must=["RecordSmartColumnChoiceAsync("], why="รอบ 205 ทีม B: ยืนยันนำเข้า = ยืนยันการจับคู่คอลัมน์"),
    dict(file="Services/Import/ImportPipelineService.Engine.cs", method="MapColumnsAsync",
         must=["_columnMatch.MapAsync(", "ImportColumnMapper.Resolve("], forbid=["SuggestColumnMappingsAsync("],
         why="รอบ 205 ทีม B: จับคู่คอลัมน์ผ่านทางเข้าเดียวของทีม A (FeedbackId/UsedAi เก็บลง session · กฎเหล็ก #1) · ไม่มีบริการ = ชั้น local ของตัวจับคู่ตัวเดียวกัน"),
    dict(file="Services/Import/ImportPipelineService.Engine.cs", method="RecordColumnChoiceAsync",
         must=["_columnMatch.RecordConfirmedAsync("],
         why="รอบ 205 ทีม B: ปิดลูป ImportColumnMatch ตอน remap/commit — ไม่ปิด = จ่าย token แล้วไม่ได้ข้อมูลสอนนักเรียน"),
    dict(file="Services/Implementations/ImportExportService.cs", method="UploadAndAnalyzeAsync",
         must=["_columnMatch.MapAsync(", "ColumnMatchAiFeedbackId ="], forbid=["SuggestColumnMappingsAsync(", "AnalyzeColumnMatch("],
         why="รอบ 205 ทีม B: Smart Import ใช้ตัวจับคู่ตัวเดียวกับศูนย์นำเข้า + เก็บ FeedbackId (เดิมทิ้ง — AI ไม่เคยเรียนจากผู้ใช้)"),
    dict(file="Services/Implementations/ImportExportService.cs", method="ImportBankTransactionAsync",
         must=["_bank.ImportParsedStatementRowsAsync("], forbid=["CurrentBalance +=", "_db.BankTransactions.Add("],
         why="รอบ 205 ทีม B/C: รายการธนาคารจากไฟล์ผ่านตัวเขียน statement ตัวเดียว (กันซ้ำ · ยอดคงเหลือ = ยอดตามธนาคาร) — เดิมเดินยอดการ์ดเอง"),
    # ฝ่ายค้านรอบ ETL (P1-6): ตัว chunk แยกเป็น CommitChunkAsync (ใช้ทั้งธุรกรรมเดียวของ AllOrNothing และธุรกรรมต่อ chunk/หน่วยของ GoodRowsOnly)
    dict(file="Services/Import/ImportPipelineService.Engine.cs", method="CommitChunkAsync",
         must=["CreateSavepointAsync(", "RollbackToSavepointAsync(", "RollbackDeferredAuditTo(", "ReplayUnitsAsync(", "PersistResultsAsync("],
         before=[("CreateSavepointAsync(", "target.CommitGroupAsync("), ("RollbackToSavepointAsync(", "ReplayUnitsAsync(")],
         why="รอบ 205 ทีม B: แถวที่ล้มต้องไม่ทิ้งของครึ่ง ๆ (savepoint ต่อ chunk + ลงซ้ำทีละกลุ่ม) · audit ของสิ่งที่ถูก rollback ต้องไม่ถูกประทับ · ผลแถวประทับในธุรกรรมเดียวกับข้อมูล"),
    dict(file="Services/Import/ImportPipelineService.Engine.cs", method="CommitSharedAsync",
         must=["CommitChunkAsync(", "IImportSequenceNumberingTarget", "ImportCommitMode.AllOrNothing", "tx.CommitAsync("],
         forbid=["AddAudit("],
         why="ฝ่ายค้านรอบ ETL P1-6/P0-1: ลงเฉพาะแถวที่ผ่าน = ธุรกรรมต่อ chunk (สมุดรายวัน = ต่อใบ — ปล่อยล็อกเลข JV) · audit IMPORT-COMMIT ไม่ใช่ที่นี่"),
    dict(file="Services/Import/ImportPipelineService.Engine.cs", method="CommitOwnAsync", forbid=["AddAudit("],
         why="ฝ่ายค้านรอบ ETL P0-1: audit IMPORT-COMMIT เขียนครั้งเดียวที่ FinishCommitAsync (เดิมทุกรอบ worker)"),
    dict(file="Services/Import/ImportPipelineService.Engine.cs", method="FinishCommitAsync",
         must=["AddAudit(companyId, batchId, actor,"],
         why="ฝ่ายค้านรอบ ETL P0-1: audit การลงข้อมูลครั้งเดียวต่อการสั่งลง"),
    dict(file="Services/Import/ImportPipelineService.Engine.cs", method="CommitPendingAsync",
         must=["r.Status != ImportRowStatus.Failed", "IncompleteGroupMessage(", "results.Count > 0", "IImportGroupIntegrityTarget"],
         why="ฝ่ายค้านรอบ ETL P0-1/P0-2: แถวที่ล้มในรอบลงนี้ไม่ถูกหยิบซ้ำ (worker ไม่วนไม่สิ้นสุด) · รอบที่ไม่มีแถวเปลี่ยนสถานะห้ามคืนคิว · "
             "หน่วยที่สมาชิกกลุ่มยังไม่พร้อมไม่ถูกลง (ใบสำคัญไม่ลงตัวห้ามเกิด — ข้อ 150 (2))"),
    dict(file="Services/Import/ImportPipelineService.Engine.cs", method="RevalidateAsync",
         must=["ExpandToGroupsAsync(", "ValidateByGroupsAsync("],
         why="ฝ่ายค้านรอบ ETL P0-2: แก้แถว ⇒ ตรวจใหม่ทั้งกลุ่ม (กติการะดับกลุ่มห้ามเห็นครึ่งกลุ่ม)"),
    dict(file="Services/Import/ImportPipelineService.Engine.cs", method="FinishStagingAsync",
         must=["RevalidateSplitGroupsAsync("],
         before=[("RevalidateSplitGroupsAsync(", "ImportSessionStatus.Staged")],
         why="ฝ่ายค้านรอบ ETL P0-2: กลุ่มที่คร่อมขอบ chunk 500 แถวถูกตรวจใหม่ทั้งกลุ่มก่อนเปิดให้ผู้ใช้ตรวจ"),
    dict(file="Services/Import/ImportPipelineService.Engine.cs", method="TransformValidateAsync",
         must=["IsExcluded(", "MetaMatchSpace", "keptMatch"],
         why="ฝ่ายค้านรอบ ETL: แถวที่ตัดออกไม่ถูกนับในกติการะดับกลุ่ม · คู่ที่เป็นเนื้อของแถวนับคะแนนเข้าความมั่นใจ · คู่ที่ target คงไว้ไม่ถูกล้าง"),
    dict(file="Services/Import/ImportPipelineService.Engine.cs", method="ToRowDto",
         must=["ImportPiiFields.HasPii(", "ImportPiiFields.Mask(", "MaskIssue("],
         why="ฝ่ายค้านรอบ ETL P1-5 (PDPA ม.26): แถวพักของทะเบียนพนักงานปิดบังเลขบัตร/บัญชี/เงินเดือน เว้นแต่ผู้ใช้มี Pii.View"),
    dict(file="Services/Import/ImportPipelineService.cs", method="ExportErrorRowsCsvAsync",
         must=["ImportPiiFields.HasPii(", "ImportPiiFields.Mask(", "MaskIssue("],
         why="ฝ่ายค้านรอบ ETL P1-5: ไฟล์แถวผิดของทะเบียนพนักงานปิดบังช่องอ่อนไหวเว้นแต่มี Pii.View"),
    dict(file="Services/Import/ImportPipelineService.cs", method="CancelAsync", must=["JobLock.RunExclusiveAsync("],
         why="ฝ่ายค้านรอบ ETL: ยกเลิกถือล็อกเดียวกับ worker/ลงข้อมูล (ไม่แข่งกัน)"),
    dict(file="Services/Import/ImportPipelineService.cs", method="CommitAsync",
         must=["JobLock.RunExclusiveAsync(", "Eligible("],
         why="รอบ 205 ทีม B: ลงข้อมูล batch เดียวกันซ้อนกันไม่ได้ (สองแท็บ/สองเครื่อง = แถวลงซ้ำ) · แถวที่มีคำเตือนต้องถูกรับทราบก่อน"),
    dict(file="Services/Import/ImportBatchWorker.cs", method="RunOnceAsync",
         must=["JobLock.RunExclusiveAsync("], must_lit=["FOR UPDATE SKIP LOCKED"],
         why="รอบ 205 ทีม B: claim batch ข้ามเครื่อง + ล็อกต่อ batch (CLAUDE.md F4 ข้อ 6)"),
    dict(file=STATREM, method="GetDashboardAsync",
         must=["Pp36Lifecycle.SplitPaymentWarning("],
         why=_PP36_WHY + "P2-6 คำตัดสินข้อ 137 จ่ายบางส่วนข้ามงวด ⇒ เตือน (คำนวณสด ไม่เขียนธงลงใบ)"),
    dict(file="Helpers/Pp36Ledger.cs", method="LoadDocsCoreAsync",
         must=["ForeignServiceVat.OwnsPp36Query", "ForeignServiceVat.OwnsPp36("],
         forbid=["d.IsForeignService && d.VatAmount > 0"],
         why=_PP36_WHY + "ตัวโหลดใบ ภ.พ.36 ใช้ predicate เจ้าของหนี้ตัวเดียว (F2) — PV ที่ปิดหนี้ใบต้นทางสืบทอดธงแต่ไม่นับซ้ำ"),
    dict(file=DOC, method="VoidDocumentAsync",
         must=["Pp36Ledger.ChangeBlocksAsync(", "Pp36Lifecycle.RuleChangeAfterRemit"],
         before=[("Pp36Ledger.ChangeBlocksAsync(", "ReverseJournalEntryAsync(")],
         why=_PP36_WHY + "E-6 คำตัดสินข้อ 134 ยกเลิกใบหลังนำส่ง/รับรู้ ภ.พ.36 ถูกบล็อกก่อนกลับ JE"),
    dict(file=DOC, method="ReclassifyForeignServiceAsync",
         must=["Pp36Ledger.ChangeBlocksAsync("],
         before=[("Pp36Ledger.ChangeBlocksAsync(", "ReverseJournalEntryAsync(")],
         why=_PP36_WHY + "E-6 คำตัดสินข้อ 134 ปลดธงบริการต่างประเทศหลังนำส่ง/รับรู้ถูกบล็อก"),
    dict(file=DOC, method="AdjustDocumentJournalEntryAsync",
         must=["Pp36Ledger.ChangeBlocksAsync(", "Pp36Ledger.IsUndueCode("],
         why=_PP36_WHY + "E-6 คำตัดสินข้อ 134 ปรับยอด 21912/11640 ของใบที่นำส่ง/รับรู้แล้วถูกบล็อก"),
    dict(file=DOC, method="CreateDocumentAsync",
         must=["Pp36Lifecycle.FlagTypeError("],
         before=[("Pp36Lifecycle.FlagTypeError(", "doc.IsForeignService = request.IsForeignService")],
         why=_PP36_WHY + "E-8 ธงบริการต่างประเทศบนใบขายถูกปฏิเสธตั้งแต่สร้าง"),
    dict(file=DOC, method="UpdateDocumentAsync",
         must=["Pp36Lifecycle.FlagTypeError("],
         why=_PP36_WHY + "E-8 ธงบริการต่างประเทศบนใบขายถูกปฏิเสธตอนแก้"),
    dict(file=DOC, method="GetDocumentsAsync",
         must=["Pp36Ledger.StatesAsync("],
         why=_PP36_WHY + "T-3d/C-P2 ป้าย ภ.พ.36 บนรายการคำนวณที่เซิร์ฟเวอร์"),
    dict(file=DOC, method="GetDocumentAsync",
         must=["Pp36Ledger.StatesAsync("],
         why=_PP36_WHY + "T-3d/C-P2 ป้าย ภ.พ.36 บนรายละเอียดคำนวณที่เซิร์ฟเวอร์"),
    dict(file="Services/Settlement/SettlementPostingService.cs", method="UnpostCoreAsync",
         must=["Pp36UnpostRefusalsAsync("],
         before=[("Pp36UnpostRefusalsAsync(", "refusals.Count > 0")],
         why=_PP36_WHY + "E-6 ยกเลิกการลงบัญชีรอบโอนเดินด่าน ภ.พ.36 ก่อนแตะชิ้นแรก"),
    dict(file="Services/Settlement/SettlementPostingService.cs", method="UnpostBlockersAsync",
         must=["Pp36UnpostRefusalsAsync("],
         why=_PP36_WHY + "E-6 พรีวิวด่านยกเลิกรอบโอนเห็นด่าน ภ.พ.36 ชุดเดียวกับตอนกดจริง"),
]

# ── รอบ PP36 ทีม F2 (คำตัดสินข้อ 131 · PP36_REVIEW E-1/E-1b/E-7/T-4b/E-12/C-P2): ยอดจ่ายผู้รับเงินของใบซื้อบริการต่างประเทศ ──
# ทุกเส้น "เงินออก/ยอดค้าง" ต้องอ่าน ForeignServiceVat.PayeeAmount (TotalAmount คงเป็นมูลค่ารวม VAT ประเมินเองให้รายงานภาษี) ·
# สอง renderer ต้องเรียกตัวตัดสินแถวยอดตัวเดียว (ResolvePrintTotals) · ห้ามสูตรเดิมกลับมา
_F2_WHY = "รอบ PP36 ทีม F2 (คำตัดสินข้อ 131): "
RULES += [
    dict(file=DOC, method="CreateDocumentAsync",
         must=["ForeignServiceVat.PayeeAmount(doc, createLinkedSrc", "LinkedSourcePp36Async(", "ForeignServiceVat.LinkedVoucherFlag(",
               "throw new BusinessRuleException(linkedErr"],
         before=[("ForeignServiceVat.LinkedVoucherFlag(", "ForeignServiceVat.PayeeAmount(doc, createLinkedSrc"),
                 ("ForeignServiceVat.PayeeAmount(doc, createLinkedSrc", "doc.PaidAmount = createPayee")],
         forbid=["doc.PaidAmount = doc.TotalAmount", "doc.BalanceDue = doc.TotalAmount;"],
         why=_F2_WHY + "ยอดจ่ายแล้ว/ค้างตอนสร้าง = ยอดจ่ายผู้รับเงิน · ใบสำคัญจ่ายที่ปิดหนี้ใบต้นทางเจ้าของ ภ.พ.36 สืบทอดธง (ทุกทางเข้า)"),
    dict(file=DOC, method="UpdateDocumentAsync",
         must=["ForeignServiceVat.PayeeAmount(doc, updSrcOwns)", "request.IsForeignService.HasValue", "ForeignServiceVat.LinkedVoucherFlag(",
               "throw new BusinessRuleException(updLinkedErr", "updFlagChangedBySystem"],
         forbid=["doc.BalanceDue = doc.TotalAmount - doc.PaidAmount", "doc.PaidAmount = doc.TotalAmount"],
         why=_F2_WHY + "แก้บรรทัด/ปัดเศษ/ติ๊กธงเฉย ๆ ยอดค้างต้องตามยอดจ่ายผู้รับเงิน (ห้าม silent no-op)"),
    dict(file=DOC, method="CreatePaymentAsync",
         must=["DocumentSettlementState.WouldOverpay(", "ForeignServiceVat.PayeeAmount(doc)"],
         call_args=[("DocumentSettlementState.Apply(", "PayeeAmount")],
         forbid=["/ doc.TotalAmount"],
         why=_F2_WHY + "สถานะ/ยอดค้างหลังจ่าย + ตัวหารสัดส่วน WHT = ยอดจ่ายผู้รับเงิน (จ่าย 5,908 ต้องปิดใบ ไม่ค้าง 413.56)"),
    dict(file=DOC, method="ApplySourceDocumentAdjustmentsAsync",
         must=["ForeignServiceVat.OwnsPp36(source)", "ForeignServiceVat.PayeeAmount(doc, srcOwnsPp36)", "source.BalanceDue = sourcePayee - source.PaidAmount"],
         forbid=["source.PaidAmount += doc.TotalAmount", "source.BalanceDue = source.TotalAmount - source.PaidAmount"],
         why=_F2_WHY + "ใบสำคัญจ่าย/ใบลดหนี้ที่ปิดหนี้ใบต้นทางเจ้าของ ภ.พ.36 ปิดเฉพาะยอดจ่ายผู้รับเงิน"),
    dict(file=DOC, method="RevertSourceDocumentAdjustmentsAsync",
         must=["ForeignServiceVat.PayeeAmount(doc, ForeignServiceVat.OwnsPp36(source))", "ForeignServiceVat.PayeeAmount(source)"],
         forbid=["source.PaidAmount - doc.TotalAmount"],
         why=_F2_WHY + "ยกเลิกใบลูกคืนเท่ายอดที่ตอน apply บวกไว้ (คู่สมมาตร)"),
    dict(file=DOC, method="AutoPostToJournalAsync",
         must=["ForeignServiceVat.PayeeAmount(doc, pvSrcOwnsPp36)", "var pvCashThb = Conv(pvPayee)", "ForeignServiceVat.OwnsPp36(source)",
               "ForeignServiceVat.SplitCredit(cnSrcOwnsPp36", "Pp36SettledReasonAsync("],
         forbid=["var pvCashThb = Conv(doc.TotalAmount)", "apThb = PvSrcThb(doc.TotalAmount"],
         why=_F2_WHY + "PV ที่ปิดหนี้ใบต้นทางเจ้าของ ภ.พ.36 ตัดเจ้าหนี้/จ่ายตามยอดจ่ายผู้รับเงิน (ไม่ตั้ง 21912 ซ้ำ) · ใบลดหนี้กลับ 21912 ตามสัดส่วน + ปฏิเสธหลังนำส่ง (ข้อ 134)"),
    dict(file=DOC, method="ConvertCoreAsync",
         must=["IsForeignService: source.IsForeignService"],
         why=_F2_WHY + "เอกสารลูกสืบทอดธงบริการต่างประเทศ (E-1b)"),
    dict(file=DOC, method="ReclassifyForeignServiceAsync",
         must=["doc.RelatedDocumentId.HasValue", "ForeignServiceVat.PayeeAmount(doc)"],
         before=[("doc.RelatedDocumentId.HasValue", "BeginTransactionAsync(")],
         why=_F2_WHY + "ใบสำคัญจ่ายที่ปิดหนี้ใบต้นทางห้ามติ๊ก (ปฏิเสธพร้อมทางไปต่อ) · ติ๊ก/ปลดแล้วยอดจ่าย/ค้างตาม"),
    dict(file=DOC, method="CollectApprovalWarningsAsync",
         must=["settledSourceOwnsPp36: fsSourceOwns", "ForeignWhtPayeeCheck.UnclassifiedNoWithholdingWarning("],
         why=_F2_WHY + "RD-83/6-UNFLAGGED ปิดเมื่อใบต้นทางเจ้าของ ภ.พ.36 แล้ว · เตือน ม.70 ผู้รับต่างประเทศไม่หัก/ไม่จำแนก (ข้อ 130) · §65 ตรี ผู้รับต่างประเทศ (E-12)"),
    dict(file=DOC, method="EvaluateSection65TerAsync",
         must=["PayeeIsForeign: Accounting.Helpers.WhtPayeeKind.IsForeignPayee("],
         why=_F2_WHY + "E-12 §65 ตรี (11)(18) รู้ว่าผู้รับเป็นต่างประเทศ (สองสัญญาณเดียวกับทะเบียน 50 ทวิ)"),
    dict(file=DOC, method="MapDocumentToResponse",
         must=["PayeeAmount: ForeignServiceVat.PayeeAmount(d)"],
         why=_F2_WHY + "หน้าเว็บอ่านยอดจ่ายผู้รับเงินที่เซิร์ฟเวอร์คำนวณ (ตัวหารสัดส่วน WHT)"),
    dict(file=PDF_HTML, method="BuildDocumentHtml",
         must=["ResolvePrintTotals(doc, L)", "ThaiBahtText.Format(printTotals.Grand)"],
         must_lit=["{printTotals.VatLabel}", "{printTotals.NetLabel}"],
         forbid=["ThaiBahtText.Format(doc.TotalAmount)", "ConvertToThaiWords("],
         forbid_lit=["<span>{L.TotalNet}</span><span>{doc.TotalAmount:N2}</span>"],
         why=_F2_WHY + "HTML renderer: แถว VAT ประเมินเอง + ยอดจ่ายผู้รับเงิน + ตัวอักษร จากตัวตัดสินเดียวกับ QuestPDF"),
    dict(file=PDF_QUEST, method="ComposeSummary",
         must=["ResolvePrintTotals(doc, L)", "printTotals.VatLabel", "printTotals.NetLabel", "ThaiBahtText.Format(wordsAmount)"],
         forbid=["ThaiBahtText.Format(doc.TotalAmount)", "ConvertToThaiWords(", "Row(L.TotalNet, doc.TotalAmount"],
         forbid_lit=["\"ภาษีมูลค่าเพิ่ม 7%\""],
         why=_F2_WHY + "QuestPDF renderer: ห้ามฝังข้อความไทยตรง ๆ (โหมดอังกฤษพิมพ์ไทย) · ตัวตัดสินแถวยอดเดียวกับ HTML"),
    dict(file=PDF_HTML, method="BuildProjectedGlAsync",
         must=["ForeignServiceVat.PayeeAmount(doc, pvSrcRow != null"],
         forbid=["doc.TotalAmount + pvWht"],
         why=_F2_WHY + "พรีวิว GL ของ PV ที่ปิดหนี้ = mirror ของ AutoPost (สอง renderer ของ JE เดียวกัน)"),
    dict(file="Services/Implementations/AgingReportService.cs", method="BuildAgingReportAsync",
         must=["ForeignServiceVat.PayeeAmount(doc)"],
         why=_F2_WHY + "ยอดหนี้ของใบในอายุเจ้าหนี้ = ยอดจ่ายผู้รับเงิน"),
    dict(file="Services/Implementations/BankService.MatchResolution.cs", method="ResolveCoreAsync",
         must=["ForeignServiceVat.PayeeAmount(d.DocumentType, d.IsForeignService, d.TotalAmount, d.VatAmount)"],
         forbid=["overrideAmount ?? d.TotalAmount"],
         why=_F2_WHY + "ยอดเอกสารที่แสดงคู่รายการธนาคาร = เงินที่ออกจริง"),
]


# ── ฝ่ายค้าน F2+F3 (merge 6a1baa8a) P1-1/P1-2/P2-5 — ธงใบลูกตามใบต้นทาง · ด่าน JE ใช้ค่าเดียวกับ JE · ใบเพิ่ม/ลดหนี้ ──
_F2X_WHY = "ฝ่ายค้าน F2+F3 (ทีม F2): "
RULES += [
    dict(file=DOC, method="AutoPostToJournalAsync",
         call_args=[("JournalPostingGuard.DocFacts(", "SourceOwnsPp36")],
         why=_F2X_WHY + "P1-1 ด่าน JE-NO-COUNTERPART ต้องรู้ว่าใบต้นทางเป็นเจ้าของ ภ.พ.36 (ค่าเดียวกับ PayeeAmount(doc, pvSrcOwnsPp36) ของ JE)"),
    dict(file="Services/Implementations/JournalAnomalyService.cs", method="ScanAsync",
         call_args=[("JournalPostingGuard.DocFacts(", "SourceOwnsPp36")],
         must=["ForeignServiceVat.OwnsPp36Query"],
         why=_F2X_WHY + "P1-1 ตัวสแกนกฎชุดเดียวกับด่านก่อนบันทึก (PV ที่ปิดหนี้ใบเจ้าของ ภ.พ.36)"),
    dict(file="Services/JournalPostingGuard.cs", method="Validate",
         must=["ForeignServiceVat.VatNotPaidToPayee(doc.DocumentType, doc.IsForeignService, doc.SourceOwnsPp36)"],
         forbid=["SplitCredit(doc.IsForeignService && isPurchase"],
         why=_F2X_WHY + "P1-1 ยอดผู้รับเงินที่คาดใช้ตัวตัดสินเดียวกับ JE (ไม่ใช่ธงของใบลูกอย่างเดียว)"),
    dict(file=DOC, method="ApproveDocumentAsync#2",
         must=["ForeignServiceVat.PayeeAmount(doc, (await LinkedSourcePp36Async("],
         why=_F2X_WHY + "P2-5 ยอดจ่ายแล้วของ PV ที่ปิดหนี้ตอนอนุมัติใช้ resolver เดียวกับ JE (ครอบใบเก่าที่ธงยังไม่ตาม)"),
]

# ── สแกนจริง f1690d11 (2026-10-09 · e-Tax ซีอาร์ซี ไทวัสดุ → โรงแรม): มัดจำปลอมจากชื่อแท็ก XML · หมวด/WHT จากบรรทัดส่วนน้อย ·
#    Dr/Cr กลับด้าน (Dr 21230 เจ้าหนี้กรรมการ / Cr 51530 ต้นทุนซ่อมบำรุง) — เทสต์ล็อกแค่ helper ⇒ ล็อกจุดเรียกที่นี่
_F1690_WHY = "สแกนจริง f1690d11: "
RULES += [
    dict(file=OCR, method="ScanAsync",
         must=["OcrDepositMarker.Decide(", "OcrEtaxXmlText.ContentOnly(extractedText)",
               "OcrAccountPlacement.AppliesToScan(", "OcrAccountPlacement.Check(", "PickCategorySeededDebitAsync(",
               "OcrMinorityWhtPart.Tag", "OcrMinorityWhtPart.WhtFieldConfidenceCap",
               "OcrAccountPlacement.HistoryConflict(", "OcrAccountPlacement.HistoryConflictConfidenceCap"],
         call_args=[("ExpenseCategoryResolver.Resolve(", "pricedLines"),
                    ("ExpenseCategoryResolver.Resolve(", "diagnostics"),
                    ("ExpenseCategoryResolver.Resolve(", "minorityWhtParts")],
         before=[("OcrAccountPlacement.Check(", "SuggestedAccountsJson = System.Text.Json.JsonSerializer.Serialize("),
                 ("OcrAccountPlacement.Check(", "PickCategorySeededDebitAsync(")],
         why=_F1690_WHY + "ตัวจัดหมวดต้องรู้ยอดรายบรรทัด (บรรทัดส่วนน้อยห้ามตัดสินหมวด/WHT ทั้งใบ) และไม่เห็นชื่อแท็ก XML · "
             "คู่ Dr/Cr ที่ระบบเสนอผ่านด่านวางฝั่งก่อนบันทึก"),
    dict(file="Services/Implementations/Ocr/ExpenseCategoryResolver.cs", method="Resolve",
         must=["OcrLineValueShare.Measure(", "MinorityLineReason(", "OcrMinorityWhtPart.NeedsReview(", "minorityWhtParts?.Add("],
         must_re=[r"if\s*\(\s*lineMinority\s*&&\s*!\s*anchored"],
         why=_F1690_WHY + "หมวดที่ชนะด้วยบรรทัดส่วนน้อยของมูลค่า (ค่าขนส่ง 37.38 จาก 2,991.59) ต้องถูกตัดพร้อมเหตุผล"),
    dict(file=OCR, method="ResolveScanAccountCodesAsync",
         must=["OcrAccountPlacement.AppliesTo(docType, result.OurRole)", "OcrAccountPlacement.ResolveStored(",
               "OcrAccountPlacement.AckJsonProperty"],
         forbid=["debitIsSystemSuggested: true", "isSalesSide"],
         why=_F1690_WHY + "แถวที่เก็บไว้ (อาจเป็นค่าที่ผู้ใช้เลือก) ผ่านด่านแบบผู้ใช้ — คู่สลับสลับกลับ · แหล่งเงินเป็นค่าใช้จ่ายตัดทิ้ง"),
    dict(file=OCR, method="ResolveScanDebitAccountIdAsync",
         must=["ResolveScanAccountCodesAsync("],
         why=_F1690_WHY + "ผังบรรทัดของพรีวิว/เติมบรรทัดใหม่ใช้คู่ Dr/Cr ที่ผ่านด่านตัวเดียวกับเส้นสร้างเอกสาร"),
    dict(file=OCR, method="CreateDocumentFromScanCoreAsync",
         must=["ResolveScanAccountCodesAsync("],
         forbid_lit=['TryGetProperty("CreditAccountCode"'],
         why=_F1690_WHY + "แหล่งเงินของใบที่สร้างมาจากคู่ที่ผ่านด่าน — ห้ามอ่าน CreditAccountCode จาก JSON ตรง ๆ อีก"),
    dict(file=OCR, method="SubmitCorrectionAsync",
         must=["OcrAccountPlacement.DecideCorrection(", "OcrAccountPlacement.AppliesToScan(",
               "debitAccountCode: learnDebitCode", "OcrCorrectedFieldList.AccountChanged(",
               "OcrAccountPlacement.IsSystemPlacedEcho(", "new Accounting.Helpers.OcrAccountsBaseline(",
               "ourRole: result.OurRole",
               # คำสั่งเจ้าของ 2026-10-13 (ไม่ล็อกตาย): ยืนยันแล้วเก็บตามผู้ใช้ + audit + ไม่สอนตัวเรียนรู้
               "acknowledged: correction.AcknowledgeAccountPlacement == true",
               "OcrAccountPlacement.OverrideRuleCode", "_db.AddChainedAuditLog(",
               "!placementOverridden ? correction.DebitAccountCode : null",
               "placementOverridden ? null : learnDebitCode"],
         must_re=[r"CorrectionAction\s*\.\s*RejectCredit\s*\)\s*throw\s+new\s+Accounting\s*\.\s*Helpers\s*\.\s*BusinessRuleException\(\s*corrDecision\.Reason!\s*,[^;]*,\s*409\s*\)",
                  r"swappedDebitCode\s*!=\s*null\s*\?\s*Accounting\s*\.\s*Models\s*\.\s*Enums\s*\.\s*UserChoiceSource\s*\.\s*Implicit"],
         forbid=["correction = correction with", "debitAccountCode: correction.DebitAccountCode",
                 "userSentDebit: !string.IsNullOrEmpty(", "OcrAccountPlacement.IsLearnableLineAccount("],
         before=[("OcrAccountPlacement.DecideCorrection(", "_categoryLearner.RecordAsync("),
                 ("new Accounting.Helpers.OcrAccountsBaseline(", "result.SuggestedAccountsJson = "),
                 ("OcrAccountPlacement.IsSystemPlacedEcho(", "_vendorIntel.TrainFromAdminAsync(")],
         why=_F1690_WHY + "ฝ่ายค้าน ข้อ 5: สลับเฉพาะเมื่อผู้ใช้ส่งทั้งคู่ · แหล่งเงินที่ผู้ใช้เลือกเป็นค่าใช้จ่าย = ปฏิเสธดัง ๆ · "
             "ฝั่งที่ไม่ได้ส่งไม่แตะ · คู่ที่ระบบสลับ = Implicit · รอบสาม: 'ส่งมา' = เปลี่ยนจากคู่ที่จอแสดง · ค่าที่จอวางให้แล้วผู้ใช้ไม่แตะ "
             "ไม่สอนเป็น Explicit · คำตอบของผู้ใช้ทุกประเภท (รวมหนี้สิน/ทุน) สอนได้ — ห้ามกลับไปกรองด้วย IsLearnableLineAccount"),
    # ── ฝ่ายค้านรอบ f1690d11 (ข้อ 1/2/4) ──
    dict(file=OCR, method="RegisterAssetFromScanAsync",
         must=["ResolveScanAccountCodesAsync(", "journalSkippedReason"],
         must_re=[r"creditAccountId\s*\.\s*HasValue\s*&&\s*!\s*placementCutCredit"],
         forbid_lit=['TryGetProperty("CreditAccountCode"'],
         why=_F1690_WHY + "JE ตั้งสินทรัพย์จากสแกนเครดิตแหล่งเงินที่ผ่านด่านตัวเดียวกับเส้นสร้างเอกสาร (เดิมอ่าน JSON ดิบ)"),
    dict(file=OCR, method="PreviewDocumentLinesAsync",
         forbid_lit=['TryGetProperty("CreditAccountCode"', 'TryGetProperty("DebitAccountCode"'],
         why=_F1690_WHY + "ผู้อ่านผังจากสแกนเพื่อลงบัญชี = ResolveScanAccountCodesAsync ตัวเดียว"),
    dict(file=OCR, method="RepopulateDocumentLinesFromScanAsync",
         must=["ResolveScanDebitAccountIdAsync("],
         forbid_lit=['TryGetProperty("CreditAccountCode"', 'TryGetProperty("DebitAccountCode"'],
         why=_F1690_WHY + "ผู้อ่านผังจากสแกนเพื่อลงบัญชี = ResolveScanAccountCodesAsync ตัวเดียว"),
    dict(file=OCR, method="AttachComplianceAsync",
         must=["OcrAccountPlacement.ResolveStored(", "OcrAccountPlacement.AppliesToScan("],
         why=_F1690_WHY + "ฝ่ายค้าน ข้อ 4: คู่ที่การ์ดแสดง = คู่ที่เส้นสร้างเอกสารลงจริง (ตัวตัดสินเดียวกัน)"),
    # ── ฝ่ายค้านรอบสาม: เอกสารที่อนุมัติ/บันทึกแล้ว = คำตอบของผู้ใช้ ⇒ สอนได้ทุกประเภท (ห้ามกรองหนี้สิน/ทุนทิ้งในประตูของผู้เช่าเอง) ──
    dict(file="Services/Implementations/Ocr/VendorIntelligenceService.cs", method="TrainFromDocumentAsync#1",   # ตัวที่ 2 = ตัวทำงานจริง (ตัวแรกเป็นตัวส่งต่อ)
         forbid=["OcrAccountPlacement.IsLearnableLineAccount("],
         why=_F1690_WHY + "รอบสาม: ผู้ขาย 'คืนเงินกรรมการ' ต้องเรียน 21230 ได้ — ด่านท้ายไปป์ไลน์กันตอนใช้ + HistoryConflict เตือน"),
    dict(file="Services/Implementations/Ocr/VendorIntelligenceService.cs", method="BackfillCoreAsync",
         forbid=["OcrAccountPlacement.IsLearnableLineAccount("],
         why=_F1690_WHY + "รอบสาม: เส้นย้อนหลังใช้กติกาเดียวกับการเรียนทีละใบ"),
    dict(file="Services/Implementations/DocumentService.cs", method="RecordLineAccountFeedbackAsync",
         must_re=[r"ChartOfAccounts\s*\.\s*AsNoTracking\s*\(\s*\)\s*\.\s*Where\s*\(\s*a\s*=>\s*a\s*\.\s*CompanyId\s*==\s*companyId",
                  r"AiSuggestionFeedbacks\s*\.\s*AsNoTracking\s*\(\s*\)\s*\.\s*Where\s*\(\s*f\s*=>\s*f\s*\.\s*CompanyId\s*==\s*companyId"],
         forbid=["OcrAccountPlacement.IsLearnableLineAccount("],
         why=_F1690_WHY + "รอบสาม: แถว feedback GL ต้องปิดด้วยคำตอบของผู้ใช้เสมอ (ไม่งั้นค้างตลอดกาล) · กฎ M CompanyId ทุก query"),
    dict(file="Services/Implementations/Ocr/CrossTenantKnowledgeAggregator.cs", method="AggregateVendorIntelligenceAsync",
         must=["OcrAccountPlacement.IsLearnableLineAccount(", "notSeedable.Contains("],
         why=_F1690_WHY + "รอบสาม (F2 #9): หนี้สิน/ทุนของบริษัทหนึ่งห้ามเป็นค่าตั้งต้นของผู้เช่ารายอื่น"),
    dict(file="Controllers/AdminController.cs", method="TrainOcrFromSystemAdmin",
         must=["OcrAccountPlacement.LearnedDebitLabel("],
         before=[("OcrAccountPlacement.LearnedDebitLabel(", "learner.RecordSystemAsync(")],
         why=_F1690_WHY + "รอบสาม: ความรู้ระบบกลาง (ทุกบริษัท) ไม่สอนหนี้สิน/ทุนเป็นผังของผู้ขาย"),
    dict(file="Controllers/OcrController.cs", method="TrainFromSample",
         must=["OcrAccountPlacement.LearnedDebitLabel("],
         why=_F1690_WHY + "รอบสาม: สอนจากตัวอย่าง = คำตอบ explicit (สอนได้) แต่ต้องบอกว่าหนี้สิน/ทุนจะไม่ถูกเติมเป็นผังค่าใช้จ่ายเอง"),
    dict(file="Controllers/OcrController.cs", method="GetVendorIntelligence",
         must=["OcrAccountPlacement.LearnedDebitLabel("],
         why=_F1690_WHY + "รอบสาม: ความรู้ผู้ขายที่เป็นหนี้สิน/ทุนต้องติดป้าย ไม่แสดงเหมือนผังค่าใช้จ่ายที่แนะนำ"),
]


# ── 2026-10-09 สแกน f1690d11 (CRC ไทวัสดุ e-Tax): บรรทัด e-Tax ราคารวม VAT + ส่วนลดรายบรรทัด ⇒ จำนวนถูกหารจากยอด (3 → 2.13) ──
# ตัวตัดสินเดียว Helpers/OcrEtaxLineNormalizer · ล็อกทุกจุดที่ "จำนวน × ราคา ≠ ยอด" ถูกตีความ (ตัวสกัด→ตัวกันจำนวนระเบิด→ด่าน→ตัวสร้างบรรทัด→แก้ในรีวิว)
_ETAX_LINE_WHY = "สแกน f1690d11 (2026-10-09): "
RULES += [
    dict(file=OCR, method="MapEtaxToOcrData",
         must=["OcrEtaxLineNormalizer.NormalizeInvoice(", "Quantity = etaxLine.Quantity", "UnitPrice = etaxLine.UnitPrice",
               "LineDiscountAmount = etaxLine.LineDiscount", "QuantityFromEtaxXml = etaxLine.QuantityFromDocument",
               "PriceIncludesVat = etaxLine.PriceIncludesVat", "VatStripResidual = etaxLine.VatStripResidual",
               "data.EtaxLineNotes.AddRange(etaxLines.Notes)",
               "VatRate = Accounting.Helpers.OcrEtaxLineNormalizer.LineVatRate(etaxLine, li.VatRatePercent, li.DeclaresVatExemption)"],
         call_args=[("OcrEtaxLineFacts(", "LineAllowance"), ("OcrEtaxLineFacts(", "NetIncludingVatAmount"),
                    ("OcrEtaxLineNormalizer.NormalizeInvoice(", "etax.GrandTotal")],
         forbid=["Quantity = li.Quantity", "UnitPrice = li.UnitPrice", "VatRate = etaxLine.PriceIncludesVat ? etaxLine.VatRate : null",
                 "LineVatRate(etaxLine, li.VatRatePercent, li.Description)"],
         why=_ETAX_LINE_WHY + "บรรทัด e-Tax ต้องผ่านตัวตัดสินราคารวม/ไม่รวม VAT + ส่วนลดบรรทัด — map ตรงจาก XML = ราคารวม VAT ในเอกสารราคาก่อน VAT"),
    dict(file="Helpers/OcrEtaxLineNormalizer.cs", method="LineVatRate",
         must=["xmlDeclaresExemption ? ThaiVatTypeRule.ExemptRate : 0m"],
         forbid=["LooksExempt"],
         why=_ETAX_LINE_WHY + "ทบทวนรอบ 8 ข้อ 3: อัตรา 0 ใน XML ที่ลงนาม = 0% · ยกเว้นเฉพาะเมื่อ XML ประกาศ ExemptionReason(Code) — ห้ามเดาจากชื่อ (F2 ข้อ 3)"),
    dict(file="Services/Implementations/Ocr/EtaxPdfXmlExtractor.cs", method="ParseEtaxXml",
         must=["item.ExemptionReasonCode = TextOf(lineTax,", "item.ExemptionReason = TextOf(lineTax,"],
         must_lit=['"ExemptionReasonCode"', '"ExemptionReason"'],
         why=_ETAX_LINE_WHY + "ทบทวนรอบ 8 ข้อ 3: ตัวสกัดต้องอ่านการประกาศยกเว้นของบรรทัดจาก XML เอง"),
    dict(file=OCR, method="SanitizeVatSplitArtifacts",
         must=["bool Signed(OcrExtractedLineItem it) => quantitiesFromSignedXml || it.QuantityFromEtaxXml;",
               "if (Signed(item)) continue;", "OcrLineQuantityGuard.Decide(",
               "return emptyDesc && isPhantom && !Signed(it);", "!phantoms.Contains(it) && !Signed(it)",
               "if (string.IsNullOrEmpty(desc) || Signed(item))"],
         must_re=[r"var\s+phantoms\s*=\s*data\.Items\.Where\(\s*it\s*=>\s*!\s*Signed\(it\)"],
         # 2026-10-16: สูตรหารจำนวน + ส่วนลดที่พิสูจน์แล้ว (ProvenLineDiscount) ย้ายไปอยู่ใน Helpers/OcrLineQuantityGuard.Decide (กติกาด้านล่าง)
         before=[("if (Signed(item)) continue;", "OcrLineQuantityGuard.Decide(")],
         forbid=["EtaxUndecided"],
         why=_ETAX_LINE_WHY + "ตัวกันจำนวนระเบิดห้ามหารจำนวนจากยอดบนบรรทัด e-Tax ทุกบรรทัด (รวมที่ตัดสินฐานราคาไม่ได้ — ทบทวน e8547899 ข้อ 2: 3 → 2.13) · ส่วนลดบรรทัดไม่ใช่จำนวนผิด"),
    dict(file=OCR, method="BuildScanLinesAsync",
         must=["OcrEtaxLineNormalizer.ProvenLineDiscount(", "+ lineOwnDisc[i]", "lineGross[idx] + lineOwnDisc[idx]"],
         before=[("OcrEtaxLineNormalizer.ProvenLineDiscount(", "OcrVatCodeLines.Decide(")],
         why=_ETAX_LINE_WHY + "ส่วนลดบรรทัดลง DocumentLine.DiscountAmount และไม่ถูกตีเป็นส่วนลดท้ายบิล/ผลต่างปัดเศษ"),
    dict(file=OCR, method="ScanAsync",
         call_args=[("OcrConfidenceGateway.LineItemForValidation(", "LineDiscountAmount")],
         why=_ETAX_LINE_WHY + "ด่าน [Gateway] ต้องรู้ส่วนลดบรรทัด (เดิมฟ้อง Qty × UnitPrice ≠ Amount 9 บรรทัดบนใบที่ถูกทุกบรรทัด)"),
    dict(file="Services/Implementations/Ocr/OcrConfidenceGateway.cs", method="Validate",
         must=["OcrEtaxLineNormalizer.ProvenLineDiscount("],
         why=_ETAX_LINE_WHY + "ด่านต่อบรรทัดเทียบ จำนวน × ราคา − ส่วนลดบรรทัด กับยอด"),
    dict(file=OCR, method="SetExtractedLineFieldsAsync",
         must=["OcrEtaxLineNormalizer.AmountAfterLineDiscount(", "discountDropped = dropped",
               "OcrEtaxLineNormalizer.ApplyReviewPriceEntry(", "line.UnitPrice = priceEdit.UnitPrice",
               "if (priceEdit.DiscountChanged)", "line.LineDiscountAmount = priceEdit.Discount", "line.VatStripResidual = null"],
         must_re=[r"return\s*\(\s*line\.Amount\s*\?\?\s*0m\s*,\s*line\.LineDiscountAmount\s*,\s*discountDropped\s*,\s*line\.UnitPrice\s*,\s*conversionNote\s*\)"],
         forbid=["line.Amount = System.Math.Round(qty * up, 2, MidpointRounding.AwayFromZero)"],
         why=_ETAX_LINE_WHY + "แก้บรรทัดในหน้ารีวิวต้องคงส่วนลดบรรทัด (ไม่งั้นยอดกลับเป็นยอดก่อนลดเงียบ ๆ)"),
]


# ── 2026-10-16 ใบจริง RE-20261000111 (พรามี่ ควอลิตี้ · ผู้ใช้รายงาน "ระบบปรับจำนวนรายการ แทนที่จะลงส่วนลดตามรายการ"):
#    ตัวกันจำนวนระเบิดหาร 1,037 ÷ 1,330 = 0.78 ทั้งที่กระดาษพิมพ์ส่วนลด 293.00 บนแถว · ตัวตัดสินเดียว Helpers/OcrLineQuantityGuard ·
#    ล็อก: ถามแถวบนกระดาษก่อนหาร · ส่วนลดที่พิมพ์มาก่อนเกณฑ์ ±2% · จำนวนที่พิมพ์ห้ามแต่ง · ทุกผู้เรียกส่งข้อความกระดาษ · แท็กไม่เงียบ
_LINE_QTY_WHY = "RE-20261000111 (2026-10-16): "
RULES += [
    dict(file=OCR, method="SanitizeVatSplitArtifacts",
         must=["OcrLineQuantityGuard.Read(paperText)", "item.LineDiscountAmount = printedDiscount",
               "data.ReasoningTrace.Add(qtyNote)", "OcrLineQuantityGuard.PaperDiscountPricesIncludeVat(",
               # ฝ่ายค้าน 658e3705 P1-1/P2-6: ยอดก่อนหักของแถวหัก ณ ที่จ่าย/มัดจำต้องถูกเขียน · บรรทัดที่ผู้ใช้แก้ไม่ผ่านตัวแก้ (แท็กเมื่อไม่ลงตัว)
               "item.Amount = grossAmount", "OcrLineQuantityGuard.UserEditedNote(", "data.ReasoningTrace.Add(userNote)"],
         call_args=[("OcrLineQuantityGuard.Decide(", "item.LineDiscountAmount"), ("OcrLineQuantityGuard.Decide(", "linePaper")],
         before=[("if (item.UserEdited)", "OcrLineQuantityGuard.Decide(")],
         forbid=["item.Quantity = fixedQty", "System.Math.Round(amt / up"],
         why=_LINE_QTY_WHY + "ตัวกันจำนวนระเบิดต้องถามแถวบนกระดาษ (ส่วนลดที่พิมพ์ ⇒ ส่วนลดบรรทัด · จำนวนที่พิมพ์ ⇒ ห้ามหาร) ผ่านตัวตัดสินเดียว — ห้ามประกอบสูตรหารจำนวนเองใน service"),
    dict(file="Helpers/OcrLineQuantityGuard.cs", method="DecideCore",
         must=["OcrEtaxLineNormalizer.ProvenLineDiscount(", "FindRow(paper, qty, up, amt)", "MidpointRounding.AwayFromZero",
               # ฝ่ายค้าน 658e3705 P2-3: แก้ที่พิสูจน์แล้ว = แจ้ง (มิเตอร์คู่หน่วย · จำนวนใหม่บนแถว) · แท็กบล็อกต้องพกค่าที่ระบบตั้ง (ตรวจสด P2-2)
               "MeterQuantityPrinted(paper, qty, amt", "FindRow(paper, newQty, up, amt)", "Fingerprint(newQty, up)", "Fingerprint(qty, up)"],
         must_lit=["ส่วนลดรายบรรทัด {printed:N2} จากกระดาษ", "ส่วนต่างที่กระดาษไม่บอก — ตรวจ"],
         # ทีม OCR 8: เกณฑ์ ±2% เปลี่ยนเป็นบล็อก (ด่านราคาเกิน 2 ตำแหน่งอยู่ข้างใน) — ลำดับเดิมคงไว้
         before=[("row.Discount is decimal printed", "if (Math.Abs(computed - amt) <= tol)"),
                 ("if (!qtyLooksLikeAmount && row.QuantityPrinted)", "var fixedQty")],
         forbid=["Math.Round(amt / up, 3)", "Math.Round(amt / qty, 4)"],
         why=_LINE_QTY_WHY + "ส่วนลดที่กระดาษพิมพ์ต้องถูกถามก่อนเกณฑ์ ±2% และจำนวนที่พิมพ์บนแถวต้องถูกคุ้มครองก่อนสูตรหารจำนวน (ลำดับ) · ปัดเศษ AwayFromZero"),
    # ── ทีม OCR 8 (2026-10-16 · บิลค่าไฟจริง กฟภ. · เจ้าของ "มี 2 หน่วยนะ 4หน่วยน่าจะเป็นค่า ft"): ห้ามแต่งราคาต่อหน่วยที่กระดาษไม่พิมพ์ ·
    #    ห้ามเก็บราคาเกิน 2 ตำแหน่ง (DocumentLine.UnitPrice numeric(18,2) ⇒ บันทึกซ้ำเลื่อน) ⇒ 1 × ยอด + จำนวนในคำอธิบาย (ตัวตัดสินเดียว Decide)
    dict(file="Helpers/OcrLineQuantityGuard.cs", method="DecideCore",
         must=["OcrLineQuantityAction.QuantityInDescription", "PrintedPriceTies(paper, qty, filled, amt)", "PrintedPriceTies(paper, qty, paperPrice, amt)",
               "!PriceFitsDocumentLine(up)", "Fingerprint(1m, amt)"],
         must_lit=["{NoteTag} {QuantityInDescriptionPhrase}"],
         before=[("if (qty == 1m) return new(OcrLineQuantityAction.FillUnitPrice", "PrintedPriceTies(paper, qty, filled, amt)"),
                 ("PrintedPriceTies(paper, qty, paperPrice, amt)", "MeterQuantityPrinted(paper, qty, amt")],
         forbid=["Math.Round(amt / qty, 4, MidpointRounding.AwayFromZero), null, null)", "null, price, null", "null, derived, null"],
         why="ทีม OCR 8 (2026-10-16 บิล PEA): ราคาว่าง/ราคาเท่ายอด/ราคาเกิน 2 ตำแหน่ง ⇒ 1 × ยอด + จำนวนในคำอธิบาย — ห้ามกลับไปเติม ยอด ÷ จำนวน 4 ตำแหน่ง "
             "(DocumentLine เก็บ 2 ตำแหน่ง ⇒ 4.54 × 4,272 = 19,394.88 ≠ 19,381.50) · ราคาที่พิมพ์บนแถวถูกถามก่อน"),
    dict(file=OCR, method="SanitizeVatSplitArtifacts",
         must=["item.Description = decidedDesc", "item.Unit = decidedUnit"],
         call_args=[("OcrLineQuantityGuard.Decide(", "item.Description"), ("OcrLineQuantityGuard.Decide(", "item.Unit")],
         why="ทีม OCR 8: คำอธิบาย \"(4,272 หน่วย)\" + หน่วย \"รายการ\" จากตัวกันต้องถูกเขียนลงบรรทัด — ไม่งั้น 1 × ยอด ทิ้งจำนวนหน่วยมิเตอร์เงียบ"),
    dict(file="Helpers/OcrLineSplitGuard.cs", method="Evaluate",
         must=["(qty == 1m ? amount.Value : null)"],
         forbid=["Math.Round(amount.Value / qty"],
         why="ทีม OCR 8: AI แตกบรรทัดไม่ส่งราคา ⇒ ปล่อยว่างให้ตัวกันจำนวนระเบิดถามกระดาษ — ห้ามเติม ยอด ÷ จำนวน 4 ตำแหน่งเอง"),
    dict(file=HARNESS, method="BuildLines",
         must=["Description = d.Description ?? l.Description", "UnitPrice = d.UnitPrice ?? l.UnitPrice"],
         call_args=[("OcrLineQuantityGuard.Decide(", "l.Description")],
         forbid=["d.Action == OcrLineQuantityAction.FillUnitPrice ? l.UnitPrice"],
         why="ทีม OCR 8: harness ต้องเล่นซ้ำตัวกันจำนวนระเบิดครบทุกการกระทำ (รวมเติมราคา/ย้ายจำนวนไปคำอธิบาย) ตรงกับ service — เดิมข้าม FillUnitPrice ⇒ golden ไม่เห็นราคา 4 ตำแหน่ง"),
    dict(file=OCR, method="ApplyExternalAmountOverrides",
         must=["qty == 1m) unit = amt"],
         forbid=["unit = Math.Round(amt.Value / qty"],
         why="ทีม OCR 8: ระบบภายนอกไม่ส่งราคา ⇒ ปล่อยว่างให้ตัวกันจำนวนระเบิดถามกระดาษ — ห้ามเติม round(ยอด ÷ จำนวน, 2) (จำนวน × ราคา ≠ ยอด ⇒ บันทึกซ้ำเลื่อน)"),
    dict(file=OCR, method="ScanAsync",
         call_args=[("SanitizeVatSplitArtifacts(", "extractedText"), ("OcrLineQuantityGuard.MergeNotes(", "extractedData.ReasoningTrace")],
         must=["OcrLineQuantityGuard.MergeNotes("],
         before=[("SanitizeVatSplitArtifacts(extractedData", "OcrLineQuantityGuard.MergeNotes(")],
         why=_LINE_QTY_WHY + "ตอนสแกนต้องส่งข้อความกระดาษให้ตัวกันจำนวนระเบิด และแท็ก [LINE-QTY] ต้องขึ้น ProcessingNotes (ห้ามอนุมัติเอง — เดิมหารจำนวนเงียบ)"),
] + [
    dict(file=OCR, method=m,
         call_args=[("SanitizeVatSplitArtifacts(", "RawTextContent"), ("OcrLineQuantityGuard.MergeNotes(", "tmpSan.ReasoningTrace")],
         must=["OcrLineQuantityGuard.MergeNotes("],
         before=[("SanitizeVatSplitArtifacts(tmpSan", "OcrLineQuantityGuard.MergeNotes(")],
         why=_LINE_QTY_WHY + "สร้างซ้ำ/พรีวิว/ดึงรายการซ้ำต้องส่งข้อความกระดาษ (RawTextContent) ไม่งั้นได้จำนวนคนละค่ากับตอนสแกน · "
             "ฝ่ายค้าน 658e3705 P2-6: หมายเหตุของตัวกันรอบนี้ต้องถึง ProcessingNotes (เดิมทิ้งไปกับ tmpSan.ReasoningTrace)")
    for m in ("CreateDocumentFromScanCoreAsync", "PreviewDocumentLinesAsync", "RepopulateDocumentLinesFromScanAsync")
] + [
    # ── ฝ่ายค้าน 658e3705 (ทีม OCR 6 · 2026-10-16) ──
    # P2-2: แท็ก [LINE-QTY] ตรวจสดบนบรรทัดเอกสารก่อนตัวตัดสินอนุมัติ ทั้งปุ่มอนุมัติ LINE และสร้าง+อนุมัติ (ตัวเดียว · ตรวจไม่ได้ ⇒ บล็อกต่อ)
    dict(file="Services/Implementations/LineBotService.cs", method="HandlePostbackAsync",
         must=["OcrLineQuantityGuard.LiveNotes("],
         call_args=[("OcrLineQuantityGuard.LiveNotes(", "liveLines")],
         must_re=[r"OcrPostingReadiness\s*\.\s*Evaluate\(\s*Accounting\s*\.\s*Helpers\s*\.\s*OcrLineQuantityGuard\s*\.\s*LiveNotes\("],
         why=_LINE_QTY_WHY + "ฝ่ายค้าน 658e3705 P2-2: แท็กของตอนสแกนต้องตรวจสดบนบรรทัดตอนนี้ — เดิมค้างหยุดแชทตลอดไป"),
    dict(file="Controllers/OcrController.cs", method="CreateDocument",
         must=["OcrLineQuantityGuard.LiveNotes("],
         before=[("OcrLineQuantityGuard.LiveNotes(", "skipReason")],
         why=_LINE_QTY_WHY + "ฝ่ายค้าน 658e3705 P2-2: สร้าง+อนุมัติตรวจสด [LINE-QTY] บนบรรทัดที่เพิ่งสร้าง (ตัวเดียวกับ LINE) — เดิมข้ามทุกครั้ง"),
    # P2-6: ค่าที่ผู้ใช้แก้ในหน้ารีวิวต้องติดธง ⇒ ตัวกันตอนสร้างเอกสารไม่แก้กลับเงียบ
    dict(file=OCR, method="SetExtractedLineFieldsAsync",
         must=["line.UserEdited = true"],
         why=_LINE_QTY_WHY + "ฝ่ายค้าน 658e3705 P2-6: บรรทัดที่ผู้ใช้แก้ต้องไม่ถูกตัวกันจำนวนระเบิดเขียนทับตอนสร้างเอกสาร (ค่าผู้ใช้ชนะ)"),
    # แท็กบล็อกต้องมีคำอธิบายบนการ์ด LINE (บทเรียน [WHT-CERT] — ปุ่มอนุมัติหายโดยไม่มีเหตุผล)
    dict(file="Services/Implementations/LineBotService.cs", method="OcrScanWarnings",
         must=["OcrLineQuantityGuard.Tag", "OcrLineQuantityGuard.NoteTag"],
         why=_LINE_QTY_WHY + "ฝ่ายค้าน 658e3705: [LINE-QTY]/[LINE-QTY-NOTE] ต้องขึ้นการ์ด LINE"),
]

# ฝ่ายค้าน 658e3705 — การถดถอยจริงที่ต้องถูกจับ (+ รูปถูกที่ต้องไม่ถูกฟ้อง)
REVIEWER_CASES += [
    # LINE ประเมินจากหมายเหตุดิบ (ไม่ตรวจสด) — แท็กค้างหยุดตลอดไป
    ("LQ1", "HandlePostbackAsync", "                    liveLines, docGate?.PricesIncludeVat ?? false,",
     "                    new List<Accounting.Helpers.OcrLiveLine>(), docGate?.PricesIncludeVat ?? false,", True),
    # บรรทัดที่ผู้ใช้แก้ไม่ข้ามตัวแก้แล้ว (ลบ continue ของแขนงผู้ใช้ — ตัวกันเขียนทับค่าผู้ใช้)
    ("LQ2", "SanitizeVatSplitArtifacts", "            if (item.UserEdited)\n", "            if (false && item.UserEdited)\n", True),
    # เส้นสร้างเอกสารทิ้งหมายเหตุของตัวกันอีก (กลับรูป 658e3705)
    ("LQ3", "CreateDocumentFromScanCoreAsync",
     "            result.ProcessingNotes = Accounting.Helpers.OcrLineQuantityGuard.MergeNotes(result.ProcessingNotes, tmpSan.ReasoningTrace);\n", "", True),
    # PrintedDiscount กลับไปไม่มีหมายเหตุ (Note = null)
    ("LQ4", "DecideCore", '$"{NoteTag} ส่วนลดรายบรรทัด {printed:N2} จากกระดาษ', '$"{NoteTag} ส่วนลด {printed:N2}', True),
    # รูปถูก: ขึ้นบรรทัดใหม่ก่อน .LiveNotes( ต้องไม่ถูกฟ้อง
    ("LQ5", "HandlePostbackAsync", "                Accounting.Helpers.OcrLineQuantityGuard.LiveNotes(",
     "                Accounting.Helpers.OcrLineQuantityGuard\n                    .LiveNotes(", False),
    # ── ทีม OCR 8 (บิลมิเตอร์): การถดถอยจริงที่ต้องถูกจับ + รูปถูกที่ต้องไม่ถูกฟ้อง ──
    # กลับไปเติม ยอด ÷ จำนวน 4 ตำแหน่ง (FillUnitPrice เดิม)
    ("MB1", "DecideCore", "            if (qty == 1m) return new(OcrLineQuantityAction.FillUnitPrice, null, amt, null, null);\n",
     "            return new(OcrLineQuantityAction.FillUnitPrice, null, Math.Round(amt / qty, 4, MidpointRounding.AwayFromZero), null, null);\n", True),
    # service ทิ้งคำอธิบายที่มีจำนวนหน่วย (1 × ยอด แล้วจำนวนมิเตอร์หายเงียบ)
    ("MB2", "SanitizeVatSplitArtifacts", "            if (qtyDecision.Description is string decidedDesc) item.Description = decidedDesc;\n", "", True),
    # ถอดด่านราคาเกิน 2 ตำแหน่ง (สแกนเก่าที่ persist 4.5369 กลับไป Keep ⇒ บันทึกซ้ำเลื่อน)
    ("MB3", "DecideCore", "if (proven == 0m && qty != 1m && !PriceFitsDocumentLine(up) && R2(qty * R2(up)) != amt)", "if (false)", True),
    # AI แตกบรรทัดกลับไปเติม 4 ตำแหน่ง
    ("MB4", "Evaluate", "?? (qty == 1m ? amount.Value : null);", "?? (qty > 0m ? Math.Round(amount.Value / qty, 4, MidpointRounding.AwayFromZero) : null);", True),
    # ไม่ส่งคำอธิบายให้ตัวกัน (ได้วงเล็บอย่างเดียว ไม่มีชื่อรายการ)
    ("MB5", "SanitizeVatSplitArtifacts", "linePaper, item.Description, item.Unit, utilitySeller);", "linePaper, null, item.Unit, utilitySeller);", True),
    # รูปถูก: ขึ้นบรรทัดใหม่ก่อนกำหนดค่าคำอธิบาย ต้องไม่ถูกฟ้อง
    ("MB6", "SanitizeVatSplitArtifacts", "            if (qtyDecision.Description is string decidedDesc) item.Description = decidedDesc;\n",
     "            if (qtyDecision.Description is string decidedDesc)\n                item.Description = decidedDesc;\n", False),
]


# ── ทีม OCR 9 (2026-10-16 · ฝ่ายค้าน 13a4d788): บรรทัดสินค้าห้ามกลายเป็น 1 × ยอดเงียบ ๆ (น้ำดื่ม 12 / 300 ⇒ 1 × 300 · สต็อกขาด 11) ·
#    1 × ยอด เฉพาะเมื่อมีหลักฐานมิเตอร์ (MeterEvidence ตัวเดียว) · ยอด ÷ จำนวน ลงตัว ⇒ คงจำนวน · ไม่งั้นคงจำนวน + ส่วนลดเศษ + แท็กบล็อก ·
#    ด่านราคาเกิน 2 ตำแหน่งถาม "ปัดแล้วลงตัวไหม" · ผู้ขายสาธารณูปโภคตามไปทุกเส้น · คีย์จับคู่/ตัวเรียนตัดท้าย "(4,272 หน่วย)" ผ่านตัวตัดเดียว
_GQ_WHY = "ทีม OCR 9 (ฝ่ายค้าน 13a4d788 P0/P1): "
_GQ_METER = r"if\s*\(\s*MeterEvidence\(\s*paper\s*,\s*qty\s*,\s*amt\s*,\s*description\s*,\s*unit\s*,\s*utilitySeller\s*\)\s*\)\s*return\s+"
RULES += [
    dict(file="Helpers/OcrLineQuantityGuard.cs", method="DecideCore",
         must=["R2(qty * R2(up)) != amt", "TiePrice(qty, amt)", "Rounded(qty, amt, null,", "Rounded(qty, amt, up,"],
         must_re=[
             # ราคาว่าง: ลงตัว ⇒ คงจำนวน → มิเตอร์ ⇒ 1 × ยอด → ไม่งั้น RoundedUnitPrice (ลำดับในแขนงเดียว — "Rounded(qty, amt, null" มีแขนงเดียว)
             r"TiePrice\(qty,\s*amt\)\s+is\s+decimal\s+filled[\s\S]*?" + _GQ_METER
             + r"InDescription\(OcrLineQuantityAction\.QuantityInDescription[\s\S]*?return\s+Rounded\(qty,\s*amt,\s*null",
             # เคส ก: ยอด ÷ จำนวน ลงตัว ⇒ คงจำนวน ก่อนถามมิเตอร์ · มิเตอร์ ⇒ 1 × ยอด · ไม่งั้น RoundedUnitPrice
             r"if\s*\(\s*TiePrice\(qty,\s*amt\)\s+is\s+decimal\s+tied\s*\)\s*return\s+qtyOnPaper[\s\S]*?" + _GQ_METER
             + r"qtyOnPaper\s*\?\s*InDescription\(",
             # ราคาเกิน 2 ตำแหน่งที่ไม่ลงตัว + ส่วนต่างลบของจำนวนที่พิมพ์: 1 × ยอด ต้องอยู่หลังหลักฐานมิเตอร์เสมอ
             _GQ_METER + r"InDescription\(OcrLineQuantityAction\.QuantityInDescription,\s*qty,\s*amt,\s*description,\s*unit,\s*OnPaper",
             _GQ_METER + r"InDescription\(OcrLineQuantityAction\.KeepPrintedGap",
         ],
         why=_GQ_WHY + "1 × ยอด + จำนวนในคำอธิบาย เฉพาะบรรทัดที่มีหลักฐานมิเตอร์ — สินค้าคงจำนวน (สต็อก/ทุน/PO) · ยอด ÷ จำนวน ลงตัว = เลขคณิต ไม่ใช่ราคาที่แต่ง"),
    dict(file="Helpers/OcrLineQuantityGuard.cs", method="MeterEvidence",
         must=["MeterLabelBefore(paper.Text, nums[k].Start)", "IsMeterUnit(word, description, ctx)", "GoodsUnitRe.IsMatch(word)",
               "if (goodsOnPaper) return false;", "LineUnit(description, unit)", "IsMeterUnit(lineUnit, description, ctx)",
               "new MeterContext(seller, OcrMeteredWater.IsMeteredWater(", "MeterChargeDescription(description, seller)",
               # 2026-10-19 บิล กปภ.: รอบบิลล้วน + ผู้ขายสาธารณูปโภค (ไม่ใช่ผู้ขายอย่างเดียว)
               "seller != OcrUtilityKind.None && BillingPeriodOnly(description)"],
         before=[("if (goodsOnPaper) return false;", "IsMeterUnit(lineUnit, description, ctx)"),
                 # ทีม OCR 14: หน่วยสินค้าบนบรรทัดชนะคำอธิบายค่ามิเตอร์
                 ("GoodsUnitRe.IsMatch(ThaiTextNormalizer.MapThaiPua(lineUnit))", "MeterChargeDescription(description, seller)")],
         # ★ ทีม OCR 11 (ฝ่ายค้าน OCR 9 P1-2): ผู้ขายอย่างเดียวห้ามเป็นหลักฐาน (AWN ขายเคส 3 ชิ้น ⇒ 1 × ยอด) — เป็นบริบทของหน่วยกำกวมเท่านั้น
         forbid=["return utilitySeller;", "return seller;", "return seller !=", "|| seller !=", "|| seller =="],
         why=_GQ_WHY + "หลักฐานมิเตอร์ = ป้าย/หน่วยมิเตอร์บนบรรทัด (ผู้ขายเป็นแค่บริบทของหน่วยกำกวม — ทีม OCR 11) · หน่วยสินค้า (ขวด/กก./ลิตร) บนแถวชนะ — เดิม \"คำหน่วยนับใดก็ได้\" = มิเตอร์"),
    dict(file="Helpers/OcrLineQuantityGuard.cs", method="Decide",
         must=["DecideCore(", "BillingPeriodOnly(description)", "UtilityChargeLabel(utilitySeller)", "FitDescription("],
         why="2026-10-19 บิล กปภ. WE1105: คำอธิบายรอบบิลล้วนได้ชื่อรายการตามชนิดผู้ขาย — หน่วยในวงเล็บตัดสินจาก DecideCore บนคำอธิบายเดิม (ห้ามอนุมาน ลบ.ม. จากคำที่ระบบเติม)"),
    dict(file="Helpers/OcrLineQuantityGuard.cs", method="DecideCore",
         must=["qty != 1m && !qtyLooksLikeAmount && MeterEvidence(paper, qty, amt, description, unit, utilitySeller)"],
         before=[("FindRow(paper, newQty, up, amt) is { Found: true, QuantityPrinted: true }",
                  "qty != 1m && !qtyLooksLikeAmount && MeterEvidence(paper, qty, amt, description, unit, utilitySeller)")],
         why="2026-10-19 (ฝ่ายค้าน P1): บรรทัดมิเตอร์ราคาอ่านผิดลง 1 × ยอดได้เฉพาะ<b>หลัง</b>จำนวนที่พิสูจน์บนแถว (ค่าน้ำ 25 ลบ.ม. × 10.50 ที่ engine อ่านจำนวน 1 ต้องได้ 25)"),
    dict(file="Helpers/OcrLineQuantityGuard.cs", method="IsMeterUnit",
         must=["OcrMeteredWater.IsMeteredWater(d)", "BulkMaterialRe.IsMatch(d)", "DataContextRe.IsMatch(d)", "ElectricityContextRe.IsMatch(d)"],
         why="ทีม OCR 11 (ฝ่ายค้าน OCR 9 P1-1): ลบ.ม./m3 เป็นมิเตอร์เฉพาะบริบทน้ำประปา (คอนกรีต/ทราย/หินขายเป็น ลบ.ม.) · GB/MB เฉพาะอินเทอร์เน็ต/ดาต้า"),
    dict(file="Helpers/OcrLineQuantityGuard.cs", method="RoundedPrices",
         must=["Math.Ceiling(amt / qty * 100m) / 100m"],
         why=_GQ_WHY + "ราคาปัดขึ้นเมื่อปัดปกติแล้วต่ำกว่ายอด (ส่วนลดติดลบเก็บไม่ได้)"),
    dict(file="Helpers/OcrLineQuantityGuard.cs", method="Rounded",
         must=["RoundedPrices(qty, amt)", "RoundingRemainderWithinCap(gap, amt)", "Fingerprint(qty, up)",
               "OcrLineQuantityAction.RoundedUnitPrice"],
         must_lit=["{Tag} {why}", "สตางค์", "{Tag} {RoundingOverCapCode}", "{RoundingRemainderPhrase}"],
         before=[("RoundingRemainderWithinCap(gap, amt)", "gap > 0m ? gap : null")],
         # ★ ทีม OCR 12 (ฝ่ายค้านรอบ 3 P2-1): เกินเพดานต้องเก็บบรรทัดสอดคล้อง (ราคาปัด + เศษในช่องส่วนลด) — ห้ามกลับไปคืน LineDiscount null
         must_re=[r"RoundingRemainderWithinCap\(gap,\s*amt\)\)\s*\{[\s\S]*?new\(OcrLineQuantityAction\.RoundedUnitPrice,\s*null,\s*up,\s*gap,"],
         why=_GQ_WHY + "คงจำนวน · แท็กบล็อกบอกส่วนต่างเป็นสตางค์ + ค่าที่ระบบตั้ง (ตรวจสด) · ทีม OCR 11 (P2-1/P2-2): เศษเกินเพดานห้ามแต่งเป็นส่วนลด · ใต้เพดานป้ายเศษปัด"),
    dict(file="Helpers/OcrLineQuantityGuard.cs", method="DescriptionForMatching",
         must=["LumpSumSuffix(description, quantity, unit)"],
         why="ทีม OCR 11 (ฝ่ายค้าน OCR 9 P2-3): ตัวตัดคีย์ตัดเฉพาะเครื่องหมายของตัวกัน (\"Flash Drive (64 GB)\" ห้ามถูกตัด)"),
    dict(file="Helpers/OcrLineQuantityGuard.cs", method="LumpSumSuffix",
         must=["IsMeterUnit(unitWord, baseText)", "specInDescription", "wholeWord"],
         why="ทีม OCR 11 (ฝ่ายค้าน OCR 9 P2-3): สมมาตรกับตัวเขียน (หน่วยมิเตอร์แบบบรรทัดอย่างเดียว) + ย้อนหลังรุ่น 6–8 เฉพาะคำหน่วยนับที่ไม่ใช่สเปกในคำอธิบาย"),
    dict(file="Services/Implementations/Ocr/UnitInferrer.cs", method="Infer",
         must=["IsWaterSupply(description)"],
         why="ทีม OCR 11 (ฝ่ายค้าน OCR 9 P1-3): substring \"ค่าน้ำ\" จับ ค่าน้ำดื่ม/ค่าน้ำแข็ง/ค่าน้ำมัน ⇒ ลบ.ม. — น้ำประปาผ่านตัวตัดสินเดียว IsWaterSupply"),
    dict(file="Controllers/OcrController.cs", method="ImportStock",
         must=["OcrLineQuantityGuard.DescriptionForMatching(ocrLine.Description, ocrLine.Quantity, ocrLine.Unit)",
               "RecordAliasAsync(companyId, productId, matchDesc!", "ProductMatcher.Normalize(matchDesc)", "UnitInferrer.Infer(matchDesc)"],
         forbid=["RecordAliasAsync(companyId, productId, ocrDesc", "ProductMatcher.Normalize(ocrDesc)",
                 ": !string.IsNullOrWhiteSpace(ocrDesc) ? ocrDesc!", "UnitInferrer.Infer(ocrDesc)"],
         why="ทีม OCR 11 (ฝ่ายค้าน OCR 9 P1-4): ตอนกดนำเข้าสต็อก alias (→ global pattern) · ชื่อสินค้า/สินทรัพย์ใหม่ · ตัวเรียนสินทรัพย์ ใช้ตัวตัดเดียวกับพรีวิว"),
    dict(file="Services/Implementations/Ocr/OcrLumpSumRepairService.cs", method="RepairAsync",
         must=["AdvisoryLockKey.For(companyId, AdvisoryLockKey.OcrLumpSumRepair", "OcrLumpSumRepair.AlreadyMarked(", "OcrLumpSumRepair.ScanNote(", "OcrLumpSumRepair.CleanKey(",
               "Audit(companyId, actorId, nameof(OcrScanResult)", "Audit(companyId, actorId, nameof(ProductAlias)",
               "Audit(companyId, actorId, nameof(OcrCategoryMapping)", "x.CompanyId == companyId"],
         forbid=["_db.Documents", "_db.StockMovements", "_db.Products"],
         why="ทีม OCR 11 (ฝ่ายค้าน OCR 9 P1-5 · CLAUDE F2 ข้อ 9): ซ่อมเฉพาะส่วนที่ปลอดภัย (สแกน/alias/คีย์ตัวเรียน) + audit · ห้ามแก้เอกสาร/สต็อก/สินค้าเอง"),
    # ── ทีม OCR 12 (ฝ่ายค้านรอบ 3) ──
    dict(file=DOC, method="ApproveDocumentAsync#2",
         must=["OcrLumpSumRepairService.ApprovalBlockAsync(_db, companyId, doc)", "OcrPostingReadiness.HumanApprovalRuleCode"],
         before=[("OcrLumpSumRepairService.ApprovalBlockAsync(_db, companyId, doc)", "EnsureCashSaleStockPolicyAllowsAsync(companyId, doc)"),
                 ("OcrLumpSumRepairService.ApprovalBlockAsync(_db, companyId, doc)", "CollectApprovalWarningsAsync(companyId, doc)")],
         why="ทีม OCR 12 (ฝ่ายค้านรอบ 3 P1-4): ทางไปต่อของการซ่อมสัญญาว่า \"ด่านอนุมัติบล็อก\" — ด่านต้องมีจริงในทางเข้าเดียวของการอนุมัติ ก่อนทุกขั้นที่บันทึก"),
    dict(file="Services/Implementations/Ocr/OcrLumpSumRepairService.cs", method="ApprovalBlockAsync",
         must=["r.CompanyId == companyId && r.CreatedDocumentId == doc.Id", "OcrPostingReadiness.EvaluateHumanApproval(", "Description = l.Description",
               # ทีม OCR 14 (ฝ่ายค้านรอบ 5 P2-a): เอกสารลูกที่ก๊อปค่าที่ระบบตั้ง (ไม่มีลิงก์สแกน) — บล็อกตามค่า · กรองบริษัท · บริบทผู้ขายของสแกน
               "OcrLineQuantityGuard.MayCarryHumanBlock(", "r.CompanyId == companyId && (r.CreatedDocumentId == null", "carried: true",
               "OcrUtilityVendor.KindOf(n.ExtractedVendorTaxId, n.ExtractedVendorName)"],
         why="ทีม OCR 12: ด่านอ่านหมายเหตุสแกนของบริษัทนี้เท่านั้น (กฎ M) และตัดสินด้วยตัวเดียว OcrPostingReadiness บนบรรทัดตอนนี้"),
    dict(file="Helpers/OcrPostingReadiness.cs", method="EvaluateHumanApproval",
         must=["HumanApprovalBlockingMarkers", "OcrLineQuantityGuard.SystemValueHeld(", "OcrLumpSumRepair.NoteDescription("],
         must_lit=["แกะใหม่"],
         why="ทีม OCR 12: ด่านคนปลดเมื่อบรรทัดเลิกถือค่าที่ระบบตั้ง (ร่องรอยตัวเดียวกับ LiveNotes) และมีทางไปต่อภาษาไทยเสมอ (คำตัดสินข้อ 150)"),
    dict(file="Services/Implementations/Ocr/OcrLumpSumRepairService.cs", method="PollutedLines",
         must=["OcrLineQuantityGuard.Read(scan.RawTextContent)", "OcrUtilityVendor.KindOf(scan.ExtractedVendorTaxId, scan.ExtractedVendorName)"],
         call_args=[("OcrLumpSumRepair.IsPollutedLine(", "paper"), ("OcrLumpSumRepair.IsPollutedLine(", "seller")],
         why="ทีม OCR 12 (ฝ่ายค้านรอบ 3 P2-2): ตัวตรวจซ่อมต้องใช้บริบทเดียวกับตัวตัดสินตอนเขียน — บิล PEA \"ค่าพลังงาน (จำนวน 4,272)\" ห้ามถูกฟ้องว่าจำนวนสินค้าหาย"),
    # ── ทีม OCR 14 (ฝ่ายค้านรอบ 5) ──
    dict(file="Services/Implementations/ApprovalService.cs", method="SubmitActionAsync",
         must=["OcrLumpSumRepairService.ApprovalBlockAsync(_db, companyId, ocrDoc)", "Accounting.Helpers.OcrPostingReadiness.HumanApprovalRuleCode, 409"],
         before=[("OcrLumpSumRepairService.ApprovalBlockAsync(_db, companyId, ocrDoc)", "action.Status = actionRequest.Status;")],
         why="ทีม OCR 14 (ฝ่ายค้านรอบ 5 P2-a): ด่านอนุมัติโดยคนต้องบอกตั้งแต่ขั้นแรกของ workflow (เดิมขั้นสุดท้ายเท่านั้น ⇒ ทุกคนลงนามแล้วใบค้าง) ก่อนบันทึกผลขั้น"),
    dict(file="Helpers/OcrLineQuantityGuard.cs", method="MeterChargeDescription",
         must=["OcrMeteredWater.IsMeteredWater(d, seller)", "if (seller == OcrUtilityKind.None) return false;"],
         why="ทีม OCR 14 (ฝ่ายค้านรอบ 5 P1-1): ค่ามิเตอร์ตัดสินจากคำอธิบาย · ค่าบริการนับเฉพาะบนบิลผู้ขายสาธารณูปโภค (ผู้ขายอย่างเดียวไม่ใช่หลักฐาน — OCR 11)"),
    dict(file="Services/Implementations/Ocr/UnitInferrer.cs", method="IsWaterSupply",
         must=["Accounting.Helpers.OcrMeteredWater.IsMeteredWater(text)"],
         why="ทีม OCR 14 (ฝ่ายค้านรอบ 5 P2-c · F2 ข้อ 4): น้ำมิเตอร์มีตัวตั้งเดียว Helpers/OcrMeteredWater (หน่วย + กฎหมวด)"),
    dict(file=OCR, method="SanitizeVatSplitArtifacts",
         must=["ownResidual ? null : item.LineDiscountAmount"],
         why="ทีม OCR 14 (ฝ่ายค้านรอบ 5 P1-1): เศษปัดของตัวกันเอง (ธง UnitPriceRoundingResidual) ไม่ใช่ส่วนลดที่พิสูจน์แล้ว — รันซ้ำต้องตัดสินรูปเดิมใหม่"),
    dict(file="Helpers/OcrLumpSumRepair.cs", method="IsPollutedLine",
         must=["OcrLineQuantityGuard.WasMeterLine(s, amount, paper, seller)"],
         why="ทีม OCR 12 (ฝ่ายค้านรอบ 3 P2-2): เล่นหลักฐานมิเตอร์ซ้ำด้วยบริบทของสแกน"),
    dict(file="Services/Implementations/Ocr/OcrLumpSumRepairService.cs", method="Audit",
         must=["_db.AddChainedAuditLog("],
         why="ทีม OCR 11: แถว audit ของการซ่อมเข้า hash chain"),
    dict(file="Helpers/OcrLineQuantityGuard.cs", method="QuantityDescription",
         must=["FitDescription(desc, suffix)", "IsMeterUnit(lineUnit, description)"],
         why=_GQ_WHY + "P2: ท้ายคำอธิบายต้องพอดีคอลัมน์ DocumentLine.DescriptionMaxLength (ตัวตัดเดียว) และมีรูปเดียวที่ตัวตัดกลับรู้จัก"),
    dict(file=OCR, method="SanitizeVatSplitArtifacts",
         must=["OcrUtilityVendor.KindOf(data.VendorTaxId, data.VendorName)",
               "if (qtyDecision.Action == Accounting.Helpers.OcrLineQuantityAction.RoundedUnitPrice) item.UnitPriceRoundingResidual = printedDiscount;"],
         call_args=[("OcrLineQuantityGuard.Decide(", "utilitySeller")],
         why=_GQ_WHY + "ชนิดผู้ขายสาธารณูปโภค (กุญแจ = เลขผู้เสียภาษี) เป็นบริบทของหน่วยมิเตอร์ — ต้องถึงตัวตัดสิน · ทีม OCR 11: เศษปัดราคาต่อหน่วยติดธง (ไม่ใช่ส่วนลดบนกระดาษ)"),
    dict(file=HARNESS, method="BuildLines",
         must=["OcrUtilityVendor.KindOf(p.VendorTaxId"],
         call_args=[("OcrLineQuantityGuard.Decide(", "utilitySeller")],
         why=_GQ_WHY + "harness เล่นซ้ำหลักฐานผู้ขายเหมือน service"),
    dict(file=OCR, method="ScanAsync",
         forbid=["OcrLearnerDescriptionKey.For(extractedData.Items.Select(i => i.Description))"],
         call_args=[("OcrLearnerDescriptionKey.PredictAsync(", "OcrLearnerDescriptionKey.Descriptions(")],
         # NaiveBayes + ตัวเรียน = สองจุด ต้องผ่านตัวตัดเดียวทั้งคู่ (ตัวเรียนล็อกด้วย call_args ของ PredictAsync)
         must=["OcrLearnerDescriptionKey.For(Accounting.Helpers.OcrLearnerDescriptionKey.Descriptions(extractedData.Items.Select(i => (i.Description, i.Quantity, i.Unit))))"],
         why=_GQ_WHY + "P1: คีย์ตัวเรียนฝั่งทำนายต้องตัดท้าย \"(4,272 หน่วย)\" (ตัวตัดเดียว) — ไม่งั้นคีย์แตกตามจำนวนหน่วยทุกเดือน"),
    dict(file=OCR, method="ScanLineDescriptionsForLearner",
         must=["(i.Description, i.Quantity, i.Unit)"],
         why=_GQ_WHY + "P1: ฝั่งเขียนของตัวเรียนส่งจำนวน/หน่วยให้ตัวตัดเดียวกับฝั่งทำนาย"),
    dict(file=OCR, method="LinkPurchaseOrderAsync",
         must=["OcrLineQuantityGuard.DescriptionForMatching("],
         before=[("OcrLineQuantityGuard.DescriptionForMatching(", "_productMatcher.RecordAliasAsync(")],
         why=_GQ_WHY + "P1: alias ที่เรียนตอนผูก PO ห้ามติดท้ายจำนวนหน่วย"),
    dict(file="Controllers/OcrController.cs", method="StockPreview",
         must=["OcrLineQuantityGuard.DescriptionForMatching(", "PrewarmGlobalPatternsAsync(matchDescriptions)", "var desc = matchDescriptions[i];",
               "var detectedQty = line.Quantity ??"],
         forbid=["var desc = line.Description"],
         why=_GQ_WHY + "P1: พรีวิวนำเข้าสต็อกจับคู่/ตั้งชื่อสินค้าด้วยคำอธิบายที่ตัดท้ายจำนวนหน่วย · จำนวนนำเข้า = จำนวนของบรรทัด (ตัวกันต้องไม่ทิ้งจำนวนสินค้า)"),
    dict(file="Services/Implementations/Ocr/ProductMatcher.cs", method="BackfillAliasesFromHistoryAsync",
         must=["OcrLineQuantityGuard.DescriptionForMatching(r.Description, r.Quantity, r.Unit)"],
         forbid=["RecordAliasAsync(companyId, r.ProductId, r.Description,"],
         why=_GQ_WHY + "P1: alias จากประวัติบรรทัดเอกสารห้ามติดท้ายจำนวนหน่วย"),
] + [
    dict(file=OCR, method=m,
         must=["VendorTaxId = result.ExtractedVendorTaxId", "VendorName = result.ExtractedVendorName"],
         why=_GQ_WHY + "สร้างซ้ำ/พรีวิว/ดึงรายการซ้ำต้องพาผู้ขายของสแกนให้ตัวกัน — ไม่งั้นบิลสาธารณูปโภคได้คำตอบคนละค่ากับตอนสแกน")
    for m in ("CreateDocumentFromScanCoreAsync", "PreviewDocumentLinesAsync", "RepopulateDocumentLinesFromScanAsync")
]
REVIEWER_CASES += [
    # P0 กลับมา: ราคาว่างแล้วลง 1 × ยอดโดยไม่ถามหลักฐานมิเตอร์
    ("GQ1", "DecideCore", "            if (MeterEvidence(paper, qty, amt, description, unit, utilitySeller))\n                return InDescription(OcrLineQuantityAction.QuantityInDescription, qty, amt, description, unit, null,\n                    $\"{NoteTag} {QuantityInDescriptionPhrase} — กระดาษพิมพ์จำนวน",
     "            if (true)\n                return InDescription(OcrLineQuantityAction.QuantityInDescription, qty, amt, description, unit, null,\n                    $\"{NoteTag} {QuantityInDescriptionPhrase} — กระดาษพิมพ์จำนวน", True),
    # P1 กลับมา: เคส ก ไม่ถามยอด ÷ จำนวน ลงตัวก่อน (น้ำดื่ม 12 ขวด 300 ⇒ 1 × 300)
    ("GQ2", "DecideCore", "            if (TiePrice(qty, amt) is decimal tied)\n                return qtyOnPaper",
     "            if (false && TiePrice(qty, amt) is decimal tied)\n                return qtyOnPaper", True),
    # P1 กลับมา: ด่านเกิน 2 ตำแหน่งไม่ถาม "ปัดแล้วลงตัวไหม" (1.235 × 80.0405 ⇒ 1 × 98.85)
    ("GQ3", "DecideCore", "!PriceFitsDocumentLine(up) && R2(qty * R2(up)) != amt)", "!PriceFitsDocumentLine(up))", True),
    # P1 กลับมา: หน่วยสินค้าบนแถวไม่ชนะผู้ขายสาธารณูปโภค
    ("GQ4", "MeterEvidence", "        if (goodsOnPaper) return false;\n", "", True),
    # service ไม่ส่งหลักฐานผู้ขาย
    ("GQ5", "SanitizeVatSplitArtifacts", "item.Unit, utilitySeller);", "item.Unit);", True),
    # เส้นสร้างเอกสารทิ้งผู้ขายของสแกน
    ("GQ6", "CreateDocumentFromScanCoreAsync",
     "new OcrExtractedData { VendorTaxId = result.ExtractedVendorTaxId, VendorName = result.ExtractedVendorName };", "new OcrExtractedData();", True),
    # พรีวิวสต็อกกลับไปจับคู่ด้วยคำอธิบายดิบ
    ("GQ7", "StockPreview", "            var desc = matchDescriptions[i];", "            var desc = line.Description ?? \"\";", True),
    # ตัวเรียนฝั่งทำนายกลับไปใช้คำอธิบายดิบ
    ("GQ8", "ScanAsync", "Accounting.Helpers.OcrLearnerDescriptionKey.Descriptions(extractedData.Items.Select(i => (i.Description, i.Quantity, i.Unit))),\n                extractedData.DocumentType",
     "extractedData.Items.Select(i => (string?)i.Description),\n                extractedData.DocumentType", True),
    # รูปถูก: ขึ้นบรรทัดใหม่ในการเรียกตัวตัดของพรีวิวสต็อก ต้องไม่ถูกฟ้อง
    ("GQ9", "StockPreview", "            .Select(l => Accounting.Helpers.OcrLineQuantityGuard.DescriptionForMatching(l.Description, l.Quantity, l.Unit))",
     "            .Select(l => Accounting.Helpers.OcrLineQuantityGuard\n                .DescriptionForMatching(l.Description, l.Quantity, l.Unit))", False),
    # ── ทีม OCR 11 (ฝ่ายค้าน OCR 9) ──
    # P1-2 กลับมา: ผู้ขายอย่างเดียวเป็นหลักฐานมิเตอร์ (AWN เคส 3 ชิ้น ⇒ 1 × ยอด)
    ("G11a", "MeterEvidence", "        return MeterChargeDescription(description, seller) || (seller != OcrUtilityKind.None && BillingPeriodOnly(description));\n",
     "        return seller != OcrUtilityKind.None;\n", True),
    # P1-1 กลับมา: ลบ.ม. ไม่ถามว่าเป็นวัสดุก่อสร้าง
    ("G11b", "IsMeterUnit", "|| (ctx.PaperWater && !BulkMaterialRe.IsMatch(d));", "|| ctx.PaperWater;", True),
    # P2-1 กลับมา: เศษไม่มีเพดาน (น็อต 100,000 ตัว ⇒ "ส่วนลด" 500)
    ("G11c", "Rounded", "        if (!RoundingRemainderWithinCap(gap, amt))\n", "        if (false)\n", True),
    # P1-4 กลับมา: ตอนนำเข้าเรียน alias ด้วยคำอธิบายดิบ
    ("G11d", "ImportStock", "RecordAliasAsync(companyId, productId, matchDesc!", "RecordAliasAsync(companyId, productId, ocrDesc!", True),
    # P1-3 กลับมา: "ค่าน้ำ" substring
    ("G11e", "Infer", "            if (r == 1 && IsWaterSupply(description)) return WaterUnit;\n", "", True),
    # ตัวซ่อมแก้เอกสารเอง
    ("G11f", "RepairAsync", "        await _db.SaveChangesAsync(ct);\n        if (tx != null)",
     "        foreach (var d in _db.Documents.Where(x => x.CompanyId == companyId)) d.Notes = \"x\";\n        await _db.SaveChangesAsync(ct);\n        if (tx != null)", True),
    # ── ทีม OCR 12 ──
    # P1-4 กลับมา: ถอดด่านอนุมัติของหมายเหตุซ่อม
    ("G12a", "ApproveDocumentAsync#2",
     "        if (await Accounting.Services.Implementations.Ocr.OcrLumpSumRepairService.ApprovalBlockAsync(_db, companyId, doc) is string ocrLineBlock)\n"
     "            throw new BusinessRuleException(ocrLineBlock, Accounting.Helpers.OcrPostingReadiness.HumanApprovalRuleCode);\n", "", True),
    # P2-1 กลับมา: เกินเพดานคืนบรรทัดไม่สอดคล้อง (ไม่มีส่วนลด)
    ("G12b", "Rounded", "            return new(OcrLineQuantityAction.RoundedUnitPrice, null, up, gap,\n",
     "            return new(OcrLineQuantityAction.RoundedUnitPrice, null, up, null,\n", True),
    # P2-2 กลับมา: ตัวตรวจซ่อมไม่ส่งบริบทของสแกน
    ("G12c", "PollutedLines", "it.Amount ?? 0m, paper, seller)", "it.Amount ?? 0m)", True),
    # ด่านอ่านหมายเหตุข้ามบริษัท
    ("G12d", "ApprovalBlockAsync", "r.CompanyId == companyId && ", "", True),
    # ── ทีม OCR 14 (ฝ่ายค้านรอบ 5) ──
    # P2-a กลับมา: ด่านไม่ดูเอกสารลูก (คัดลอก/แปลง/รายการเกิดซ้ำ)
    ("G14a", "ApprovalBlockAsync", "carried: true)", "carried: false)", True),
    # เส้นเอกสารลูกอ่านหมายเหตุข้ามบริษัท
    ("G14b", "ApprovalBlockAsync", "r.CompanyId == companyId && (r.CreatedDocumentId == null", "(r.CreatedDocumentId == null", True),
    # workflow กลับไปบล็อกที่ขั้นสุดท้ายเท่านั้น
    ("G14c", "SubmitActionAsync", "OcrLumpSumRepairService.ApprovalBlockAsync(_db, companyId, ocrDoc)", "Task.FromResult<string?>(null).Result", True),
    # P1-1 กลับมา: ผู้ขายสาธารณูปโภคอย่างเดียวพา "ค่าบริการ" ของใครก็ได้ไปเป็นมิเตอร์
    ("G14d", "MeterChargeDescription", "        if (seller == OcrUtilityKind.None) return false;\n", "", True),
    # P2-c กลับมา: UnitInferrer มีสำเนาน้ำประปาของตัวเอง
    ("G14e", "IsWaterSupply", "return Accounting.Helpers.OcrMeteredWater.IsMeteredWater(text);", "return text?.Contains(\"ค่าน้ำ\") == true;", True),
    # P1-1 กลับมา: รันซ้ำส่งเศษปัดของตัวกันเป็นส่วนลดที่พิสูจน์แล้ว (บรรทัดมิเตอร์รุ่นก่อนค้าง 4,272 × 4.54 − 13.38)
    ("G14f", "SanitizeVatSplitArtifacts", "ownResidual ? null : item.LineDiscountAmount", "item.LineDiscountAmount", True),
    # รูปถูก: ขึ้นบรรทัดใหม่ในการเรียกตัวตัดตอนนำเข้า ต้องไม่ถูกฟ้อง
    ("G11g", "ImportStock", "Accounting.Helpers.OcrLineQuantityGuard.DescriptionForMatching(ocrLine.Description, ocrLine.Quantity, ocrLine.Unit)",
     "Accounting.Helpers.OcrLineQuantityGuard.DescriptionForMatching(\n                        ocrLine.Description, ocrLine.Quantity, ocrLine.Unit)", False),
]


# ── ฝ่ายค้านรอบสอง f1690d11 (2026-10-09): ทุกเส้นที่อ่าน ExtractedItemsJson แล้วรันตัวกันจำนวนระเบิดต้องคุ้มครองสแกน e-Tax ·
#    เขียนกลับตอนอนุมัติต้องพาส่วนลด+ธงไปด้วย · ทุนนำเข้าสต็อก = หลังส่วนลด · หน้ารีวิวบอกเมื่อส่วนลดถูกล้าง
RULES += [
    dict(file=OCR, method=m,
         call_args=[("SanitizeVatSplitArtifacts(", "IsEtaxEngine")],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบสอง ข้อ 2: สแกน e-Tax ห้ามถูกหารจำนวนใหม่ตอนสร้างซ้ำ/พรีวิว/ดึงรายการซ้ำ")
    for m in ("ScanAsync", "CreateDocumentFromScanCoreAsync", "PreviewDocumentLinesAsync", "RepopulateDocumentLinesFromScanAsync")
] + [
    dict(file=DOC, method="BuildScanItemsJsonFromLines",
         must=["OcrEtaxLineNormalizer.WriteBackLine(", "Amount = wb.Amount", "LineDiscountAmount = wb.Discount",
               "QuantityFromEtaxXml = quantitiesFromSignedXml", "PriceIncludesVat = pricesIncludeVat",
               "if (wb.Lost) lost.Add((l.LineOrder, wb.Loss));"],
         forbid=["Amount = pricesIncludeVat ? l.Amount + l.VatAmount : l.Amount"],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบสอง ข้อ 2: เขียนบรรทัดที่อนุมัติกลับลงสแกนต้องคงส่วนลด+ธงจำนวนจากเอกสารที่ลงนาม"),
    dict(file=DOC, method="SyncScanToPostedDocumentAsync",
         call_args=[("BuildScanItemsJsonFromLines(", "IsEtaxEngine"),
                    ("BuildScanItemsJsonFromLines(", "ItemsJsonCarriesSignedQuantities"),
                    ("BuildScanItemsJsonFromLines(", "PricesIncludeVat"),
                    ("BuildScanItemsJsonFromLines(", "BillDiscountAmount"),
                    ("BuildScanItemsJsonFromLines(", "DepositBaseDeducted")],
         must=["if (linesChanged && writeBackLostLines.Count > 0",
               "OcrEtaxLineNormalizer.WriteBackLostNote(doc.DocumentNumber, writeBackLostLines)"],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบสอง ข้อ 2: สแกน e-Tax/สำเนาของมัน (engine Cached) ต้องได้ธงคืน"),
    dict(file="Controllers/OcrController.cs", method="StockPreview",
         must=["OcrEtaxLineNormalizer.EffectiveUnitCost(", "effectiveCosts[li]", "EffectiveUnitCost: effCost",
               "l.PriceIncludesVat, l.VatRate"],
         forbid=["lines.Select(l => (l.Description, l.Quantity, l.UnitPrice, l.Amount))", "line.UnitPrice.Value - best.CostPrice"],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบสอง ข้อ 3: ทุนนำเข้าสต็อก/ตัวตรวจสินทรัพย์ = ยอดหลังส่วนลด ÷ จำนวน ไม่ใช่ราคาก่อนลด"),
    dict(file="Controllers/OcrController.cs", method="SetLineFields",
         must=["req.LineDiscount", "discountDropped"],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบสอง ข้อ 4: ส่วนลดบรรทัดแก้ได้จากหน้ารีวิว · ถูกล้างต้องบอกผู้ใช้"),
]


# ── ฝ่ายค้านรอบสาม f1690d11 (2026-10-09): ใบ e-Tax ราคารวม VAT ทุกบรรทัด ⇒ เอกสาร PricesIncludeVat ด้วยค่าตามกระดาษ (ไม่แต่งส่วนลด) ·
#    ตัวพิสูจน์ (OcrEtaxLineNormalizer.TiesOutInclusive) เป็นสำเนาสูตรของ DocumentService ⇒ ล็อกสูตรต้นทางไว้ที่นี่: เปลี่ยนสูตรที่นั่น = checker แดง
#    = ต้องแก้ตัวพิสูจน์ด้วย (ไม่งั้นตัวพิสูจน์ "ผ่าน" ใบที่บันทึกซ้ำแล้วยอดหลุด)
RULES += [
    dict(file=OCR, method="ScanAsync",
         must=["foreach (var etaxNote in extractedData.EtaxLineNotes)"],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบสาม: หมายเหตุ [e-Tax] (เศษจากถอด VAT · เหตุที่ใช้ทางสำรอง) ต้องถึง ProcessingNotes"),
    dict(file=OCR, method="BuildScanLinesAsync",
         must=["OcrEtaxLineNormalizer.AllLinesPriceIncludeVat(", "OcrEtaxLineNormalizer.InclusiveLineVats("],
         before=[("OcrEtaxLineNormalizer.AllLinesPriceIncludeVat(", "OcrEtaxLineNormalizer.InclusiveLineVats(")],
         forbid=["items.All(x => x.PriceIncludesVat)"],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบสาม: บรรทัดราคารวม VAT ตามกระดาษ ⇒ เอกสาร PricesIncludeVat + VAT รายบรรทัดสูตรเดียวกับ ComputeLineAmounts"),
    dict(file=OCR, method="MapToResponse",
         must=["OcrEtaxLineNormalizer.DiscountLabel("],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบสาม: ป้ายช่องส่วนลด (รวม/ก่อน VAT · เศษจากถอด VAT) เซิร์ฟเวอร์เป็นคนเขียน"),
    dict(file=DOC, method="ComputeLineAmounts",
         must=["var gross = Math.Round(line.Quantity * line.UnitPrice, 2, R);",
               "? Math.Min(Math.Round(line.DiscountAmount.Value, 2, R), gross)",
               "var afterDiscount = gross - discountAmt;",
               "net = Math.Round(afterDiscount * 100m / (100m + line.VatRate), 2, R);",
               "vatAmt = afterDiscount - net;"],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบสาม: OcrEtaxLineNormalizer.TiesOutInclusive จำลองสูตรนี้ — เปลี่ยนสูตรต้องแก้ตัวพิสูจน์ด้วย"),
    dict(file=DOC, method="ReconcileTaxRounding",
         must=["var target = Math.Round(g.Sum(i => amts[i].NetAmount) * g.Key / 100m, 2, R);",
               "NetAmount = pricesIncludeVat ? amts[j].NetAmount - diff : amts[j].NetAmount",
               "&& lines[i].VatRate > 0 && amts[i].NetAmount != 0m)",
               "if (diff == 0m || Math.Abs(diff) > 1m) continue;",
               "var j = g.OrderByDescending(i => Math.Abs(amts[i].NetAmount)).First();",
               "VatAmount = amts[j].VatAmount + diff,"],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบสาม: TiesOutInclusive ต้องการ VAT รายบรรทัดรวม = เป้าของขั้นนี้ (ไม่งั้นบรรทัดใหญ่สุดถูกขยับ)"),
]


# ── ฝ่ายค้านรอบสี่ f1690d11 (2026-10-09): ต้นทุนสินทรัพย์จากบรรทัดราคารวม VAT · แถวที่เพิ่มในรีวิวสืบทอดฐานราคา ──
RULES += [
    dict(file=OCR, method="ScanAsync",
         must=["OcrEtaxLineNormalizer.ExVatAmount(i.Amount, i.PriceIncludesVat, i.VatRate, assetFallbackRate)",
               "amount = assetInput.ElementAtOrDefault(d.LineIndex).Item4"],
         forbid=[".Select(i => (i.Description, i.Quantity, i.UnitPrice, i.Amount))"],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบสี่ ข้อ 1: ตัวตรวจ/ผู้สมัครสินทรัพย์ถาวรใช้ต้นทุนก่อน VAT หลังส่วนลด (ไม่ใช่ยอดรวม VAT)"),
    dict(file=OCR, method="RegisterAssetFromScanAsync",
         must=["OcrEtaxLineNormalizer.ExVatAmountOfItem(scan.ExtractedItemsJson, req.LineIndex)"],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบสี่ ข้อ 1: ราคาซื้อสินทรัพย์ตั้งต้นจากบรรทัดราคารวม VAT = ยอดก่อน VAT"),
    dict(file=OCR, method="ModifyExtractedLineAsync",
         must=["OcrEtaxLineNormalizer.InheritForNewLine(", "PriceIncludesVat = inherit.PriceIncludesVat", "VatRate = inherit.VatRate"],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบสี่ ข้อ 2: แถวใหม่ในใบราคารวม VAT ต้องอยู่ฐานเดียวกัน (ไม่งั้นทั้งใบหลุด VAT 209.41 → 227.57)"),
]


# ── ฝ่ายค้านรอบห้า f1690d11 (2026-10-09): แถวราคารวม VAT ไม่รู้อัตรา · มัดจำ + ส่วนลดการค้า · [SCAN-WRITEBACK] ต้องดัง ──
RULES += [
    dict(file=OCR, method="ScanAsync",
         must=["OcrEtaxLineNormalizer.DominantVatRate(", "OcrEtaxLineNormalizer.AssetCostNote("],
         call_args=[("OcrEtaxLineNormalizer.ExVatAmount(", "assetFallbackRate"),
                    ("OcrEtaxLineNormalizer.EffectiveUnitCost(", "assetFallbackRate")],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบห้า ข้อ 2/4: ผู้สมัครสินทรัพย์ใช้อัตราหลักของใบเมื่อแถวไม่รู้อัตรา · ไม่เดาฐาน VAT ของกระดาษ ⇒ หมายเหตุ [ASSET-COST]"),
    dict(file="Controllers/OcrController.cs", method="StockPreview",
         call_args=[("OcrEtaxLineNormalizer.EffectiveUnitCost(", "stockFallbackRate")],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบห้า ข้อ 4: ทุนนำเข้าสต็อกของแถวราคารวม VAT ไม่รู้อัตรา ⇒ อัตราหลักของใบ · ไม่มี ⇒ ไม่เติมจากราคารวม VAT"),
    dict(file=DOC, method="SyncScanToPostedDocumentAsync",
         must=["billDiscount: doc.BillDiscountAmount, depositBase: doc.DepositBaseDeducted",
               "OcrEtaxLineNormalizer.WriteBackLostNote("],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบห้า ข้อ 1/5: แท็กห้ามอนุมัติตัวเดียว (OcrPostingReadiness อ่าน) · ส่งยอดส่วนลดการค้าและมัดจำแยกกัน"),
    dict(file=DOC, method="BuildScanItemsJsonFromLines",
         call_args=[("OcrEtaxLineNormalizer.WriteBackLine(", "depositBase"), ("OcrEtaxLineNormalizer.WriteBackLine(", "billDiscount")],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบห้า ข้อ 5: ส่วนลดการค้ายังไปกับส่วนลดบรรทัดแม้มีมัดจำ — เฉพาะส่วนมัดจำที่หาย"),
]


# ── ฝ่ายค้านรอบห้า ข้อ 3: แถวราคารวม VAT ที่ผู้ใช้กรอกราคาไม่รวม VAT ⇒ เซิร์ฟเวอร์แปลงเป็นฐานรวม VAT ของใบ (ไม่ปล่อยแถวปนฐาน) ──
RULES += [
    dict(file=OCR, method="SetExtractedLineFieldsAsync",
         must=["OcrEtaxLineNormalizer.ApplyReviewPriceEntry(", "OcrEtaxLineNormalizer.DominantVatRate("],
         call_args=[("OcrEtaxLineNormalizer.ApplyReviewPriceEntry(", "line.UnitPrice, line.LineDiscountAmount, line.PriceIncludesVat"),
                    ("OcrEtaxLineNormalizer.ApplyReviewPriceEntry(", "unitPrice, lineDiscount, priceEnteredExVat")],
         before=[("OcrEtaxLineNormalizer.ApplyReviewPriceEntry(", "line.UnitPrice = priceEdit.UnitPrice")],
         forbid=["ConvertExVatEntryToInclusive(", "if (unitPrice.HasValue) line.UnitPrice = unitPrice.Value;"],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบห้า ข้อ 3 + ทบทวน e8547899 ข้อ 1: แปลงราคาไม่รวม VAT ผ่านตัวตัดสินเดียว — ห้ามแปลงราคาที่เก็บไว้ (ติ๊กซ้ำ = VAT สองชั้น)"),
    dict(file="Helpers/OcrEtaxLineNormalizer.cs", method="WriteBackLostNote",
         must_lit=["{WriteBackLossText(g.Key)}", "{WriteBackLostTag} เอกสาร {documentNumber}:"],
         why=_ETAX_LINE_WHY + "ทบทวน e8547899 ข้อ 3 (LOW): แท็กห้ามอนุมัติตัวเดียว (OcrPostingReadiness อ่าน) + เหตุจริงรายบรรทัด ไม่ใช่ \"หักมัดจำ\" ทุกกรณี"),
    dict(file="Helpers/OcrEtaxLineNormalizer.cs", method="ApplyReviewPriceEntry",
         must=["if (sentUnitPrice is not decimal entered)", "if (sd != (storedDiscount ?? 0m))",
               "ConvertExVatEntryToInclusive(entered, discChanged ? disc : null, rate)"],
         before=[("if (sentUnitPrice is not decimal entered)", "ConvertExVatEntryToInclusive(entered")],
         forbid=["ConvertExVatEntryToInclusive(storedUnitPrice", "ConvertExVatEntryToInclusive(price"],
         why=_ETAX_LINE_WHY + "ทบทวน e8547899 ข้อ 1 (MEDIUM): แปลงเฉพาะราคาที่ส่งมาในคำขอนี้ · ส่วนลดเฉพาะที่เปลี่ยน · idempotent"),
    dict(file="Controllers/OcrController.cs", method="SetLineFields",
         must=["req.PriceEnteredExVat", "conversionNote"],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบห้า ข้อ 3: ส่งคำสั่งแปลง + คืนราคาใหม่และข้อความให้หน้าแสดง"),
    dict(file=OCR, method="MapToResponse",
         must=["OcrEtaxLineNormalizer.PriceBasisLabel("],
         why=_ETAX_LINE_WHY + "ฝ่ายค้านรอบห้า ข้อ 3: ป้ายช่องติ๊กฐานราคาเซิร์ฟเวอร์เป็นคนเขียน"),
    # ── คำขอเจ้าของ 2026-10-12: 50 ทวิ อัตโนมัติ — ติ๊กต่อใบ + ผู้ถูกหักแยกจากผู้รับเงิน (ประกันผ่านโบรกเกอร์) · ตัวตัดสินเดียว WhtCertIssuePlan ·
    #    ทุกทางเข้าอัตโนมัติ (อนุมัติ · บันทึกจ่าย · จ่ายหลายใบ · ซิงค์ · สร้างทั้งเดือน) เดินทางเดียว TryAutoIssueForDocumentAsync (ล้มดัง ไม่กลืน) ──
    dict(file=DOCSVC, method="ApproveDocumentAsync#2",
         must=["TryAutoIssueForDocumentAsync(", "WhtCertIssueTrigger.Approve", "paidAtTrigger: whtPaidOnApprove",
               # รอบ 204 (ข้อ 142): ใบลดหนี้ซื้อลดภาษีหักหลังออก 50 ทวิ — ยกเลิก+ออกใหม่/ติดป้ายต้องตรวจ (ไม่แก้เงียบ)
               "AdjustCertsForCreditNoteAsync("],
         forbid=["AutoGenerateFromDocumentAsync("],
         why="50 ทวิ ตอนอนุมัติผ่านตัวตัดสินเดียว (ติ๊กปิด = ไม่ออก · ผู้ถูกหักแยก) · ห้ามเรียกตัวออกใบตรงแล้วกลืนข้อผิดพลาด"),
    dict(file=DOCSVC, method="CreatePaymentAsync",
         must=["TryAutoIssueForDocumentAsync(", "WhtCertIssueTrigger.Payment", "sourcePaymentId: payment.Id", "WhtCertNotice: whtCertNotice"],
         forbid=["AutoGenerateFromDocumentAsync("],
         why="50 ทวิ รายงวดผ่านตัวตัดสินเดียว · ผลที่ผู้ใช้ต้องรู้ (ล้ม/ตั้งให้ออกเอง) กลับในคำตอบการจ่าย"),
    dict(file=DOCSVC, method="CreateMultiDocPaymentAsync",
         must=["TryAutoIssueForDocumentAsync(", "WhtCertIssueTrigger.Payment", "multiWhtNotices"],
         forbid=["AutoGenerateFromDocumentAsync("],
         why="โอนก้อนเดียวหลายใบ — 50 ทวิ ของแต่ละใบเดินทางเดียวกับใบเดี่ยว + ข้อความรวมในคำตอบ"),
    # รอบ 204 (ข้อ 143): ทุกขา WHT ฝั่งผู้จ่ายผ่านตัวแบ่งต่อผู้ถูกหักตัวเดียว (ใบรายเดียว = ผู้ถูกหักตัวเดิมผ่าน LoadWhtPayeeAsync — JE เดิมทุกประการ)
    dict(file=DOCSVC, method="AutoPostToJournalAsync",
         must=["ResolveWhtPayableSplitAsync(companyId, doc, doc.WithholdingTaxAmount)", "ResolveWhtPayableSplitAsync(companyId, doc, thisWht)"],
         forbid=["ResolveWhtPayableAccountAsync(companyId, doc.Contact",
                 "ResolveWhtPayableAccountAsync(companyId, await _db.LoadWhtPayeeAsync(companyId, doc)"],
         why="ผัง 21916/21917/21918 ตามผู้ถูกหัก (ตัวเดียวกับแบบบน 50 ทวิ) — ไม่งั้น GL ภ.ง.ด.3/53 ไม่ตรงทะเบียนเมื่อผู้ถูกหักต่างชนิดกับผู้รับเงิน · "
             "รอบ 204: ใบหลายผู้ถูกหัก ผังต่อผู้ถูกหัก"),
    dict(file=DOCSVC, method="ResolveWhtPayableSplitAsync",
         must=["WhtPayeePortionsWithLinesAsync(companyId, doc)", "LoadWhtPayeeAsync(companyId, doc)", "WhtPayeeSplit.Allocate(",
               "ResolveWhtPayableAccountAsync("],
         why="รอบ 204 (ข้อ 143): ตัวแบ่งผังหนี้ WHT ต่อผู้ถูกหัก — ใบรายเดียวใช้ผู้ถูกหักตัวเดิม · หลายรายเฉลี่ยด้วย Allocate (Σ = ยอดเป๊ะ)"),
    dict(file=DOCSVC, method="CreatePaymentJournalAsync",
         must=["ResolveWhtPayableSplitAsync(companyId, doc, thbWht, payment.PaymentDate)"],
         forbid=["ResolveWhtPayableAccountAsync(companyId, await _db.LoadWhtPayeeAsync(companyId, doc)"],
         why="รอบ 204 (ข้อ 143): WHT รายงวดแบ่งผังตามผู้ถูกหัก (ตัวเดียวกับตอนอนุมัติ)"),
    dict(file=DOCSVC, method="WhtPayeePortionsWithLinesAsync",
         must=["WhtPayeeSplit.Portions(", "WhtCertIssuePlan.AppliesTo(src.DocumentType)"],
         why="รอบ 204: ใบลดหนี้/เพิ่มหนี้ฝั่งซื้อกลับหนี้ WHT เข้าผังของผู้ถูกหักของใบต้นทาง (ใบลดหนี้ตั้งผู้ถูกหักเองไม่ได้)"),
    dict(file=DOCSVC, method="ChangeWhtPayeeAsync",
         must=["WhtPayeeChangePlan.BlockReason(", "WhtCertVoidGuard.CheckAsync(", "WhtPayeeChangePlan.PeriodFiledReasonAsync(",
               "WhtPayeeChangePlan.PayableDeltas(", "AddChainedAuditLog(", "BeginTransactionAsync(", "TryAutoIssueForDocumentAsync(",
               "WhtPayeeSplit.NormalizeLineOverride(", "ClosedPeriodReasonAsync("],
         before=[("BeginTransactionAsync(", "LockDocumentsForPaymentVoidAsync("),
                 ("LockDocumentsForPaymentVoidAsync(", "_db.Documents.Include(d => d.Lines)"),
                 ("WhtPayeeChangePlan.BlockReason(", "PostAsync("),
                 ("CommitAsync()", "TryAutoIssueForDocumentAsync(")],
         why="รอบ 204 (ข้อ 145): เปลี่ยนผู้ถูกหักหลังอนุมัติ = การกระทำ (ด่านยื่นแล้วรายใบ+รายงวดก่อนเขียน · JE ย้ายผัง · audit · ออกใบใหม่หลัง commit) · "
             "ฝ่ายค้าน P3-3: ล็อกแถวเอกสารในธุรกรรมก่อนอ่าน"),
    dict(file=DOCSVC, method="ReversePaymentInternalAsync",
         must=["FlagReviewAsync("],
         why="รอบ 204: ยกเลิกการจ่ายแต่ 50 ทวิ ออกแล้ว — ล้มดังที่ตัวเอกสาร + ลิสต์รอออกใบ (เดิมแค่ LogWarning)"),
    dict(file=DOCSVC, method="ReverseMultiDocPaymentInternalAsync",
         must=["FlagReviewAsync("],
         why="รอบ 204: ทางจ่ายหลายใบ — ล้มดังที่ตัวเอกสารเหมือนทางใบเดียว"),
    dict(file=DOCSVC, method="CreateDocumentAsync",
         must=["ResolveLineWhtPayeesAsync(", "WhtPayeeContactId = createLinePayees[lineIdx]"],
         why="รอบ 204 (ข้อ 143): ผู้ถูกหักรายบรรทัดตรวจ (บริษัทนี้ · ฝั่งผู้จ่าย) แล้วเก็บแบบ normalize"),
    dict(file=DOCSVC, method="UpdateDocumentAsync",
         must=["WhtPayeeSplit.RequestedOrKept(", "WhtPayeeContactId = updLinePayees[updLineIdx]"],
         before=[("prevLinePayees", "_db.DocumentLines.RemoveRange(doc.Lines)")],
         why="รอบ 204: แก้ไขแล้วบันทึก ผู้ถูกหักรายบรรทัดไม่หายเงียบ (ไม่ส่ง = คงเดิม · Guid.Empty = ล้าง)"),
    dict(file=DOCSVC, method="MapDocumentToResponse",
         must=["WhtPayeeContactId: l.WhtPayeeContactId", "WhtCertReviewNote.ReasonOf(", "WhtPayeeSplit.Portions("],
         why="รอบ 204: echo ผู้ถูกหักรายบรรทัด + ป้าย \"50 ทวิ ต้องตรวจ\" + จำนวนผู้ถูกหัก (เซิร์ฟเวอร์ตัดสิน)"),
    dict(file=INTEGRATION, method="TryAutoGenerateWhtAsync",
         must=["TryAutoIssueForDocumentAsync(", "WhtCertIssueTrigger.IntegrationSync", "issueAsIssued: paid"],
         forbid=["AutoGenerateFromDocumentAsync("],
         why="ซิงค์จากระบบภายนอกเดินทางเดียวกับอนุมัติ/จ่าย (ติ๊กปิด/ผู้ถูกหัก/ล้มดัง)"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="BulkGenerateAsync",
         must=["TryAutoIssueForDocumentAsync(", "WhtCertIssueTrigger.Bulk"],
         forbid=["AutoGenerateFromDocumentAsync("],
         why="สร้างทั้งเดือนต้องไม่กวาดใบที่ผู้ใช้ตั้งให้ออกเอง — ตัวตัดสินเดียวกับทางอัตโนมัติอื่น"),
    # รอบ 204 (ข้อ 143): ทางเข้าสาธารณะแยกใบหลายผู้ถูกหัก → ตัดสิน/ออกต่อผู้ถูกหักผ่านแกนเดียว (TryAutoIssueCoreAsync)
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="TryAutoIssueForDocumentAsync",
         must=["LoadPayeePortionsAsync(", "WhtPayeeSplit.IsMultiPayee(", "TryAutoIssueCoreAsync(", "TryAutoIssueMultiPayeeAsync("],
         why="ใบรายเดียวเดินแกนเดิม · ใบหลายผู้ถูกหักแยกต่อผู้ถูกหัก (idempotent ต่อ (เอกสาร, ผู้ถูกหัก))"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="TryAutoIssueMultiPayeeAsync",
         must=["WhtPayeeSplit.Allocate(", "TryAutoIssueCoreAsync(", "WhtCertAutoFailedNote.Append("],
         why="รอบ 204: ยอดงวดเฉลี่ยต่อผู้ถูกหัก · ล้มรายใด = ป้ายเดียวที่บอกทุกรายที่ล้ม (ไม่ทับกัน)"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="TryAutoIssueCoreAsync",
         must=["WhtCertIssuePlan.Decide(", "WhtCertAutoFailedNote.Append(", "RollbackToSavepointAsync(", "CreateSavepointAsync("],
         before=[("WhtCertIssuePlan.Decide(", "AutoGenerateFromDocumentAsync("),
                 ("SaveChangesAsync()", "CreateSavepointAsync(")],
         why="ตัดสินก่อนออก · ล้ม = ป้ายบนเอกสาร (ไม่กลืน) · savepoint กันการอนุมัติ/การจ่ายล้มตาม · บันทึกงานผู้เรียกก่อนตั้ง savepoint (ไม่ให้ถูกย้อนทิ้งเงียบ)"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="AutoGenerateFromDocumentAsync",
         must=["WhtPayeeSplit.EffectiveSinglePayee(", "WhtCertIssuePlan.PayeeTaxIdProblem(", "ResolveWhtFormType(payee", "PayeeContactId = payeeId",
               "WhtCertAutoFailedNote.Clear("],
         forbid=["PayeeContactId = doc.ContactId", "ResolveWhtFormType(doc.Contact"],
         why="50 ทวิ ในนามผู้ถูกหัก (ชื่อ/เลข/แบบ ภ.ง.ด. จากผู้ถูกหัก ไม่ใช่ผู้รับเงิน) · ไม่มีเลขผู้เสียภาษี = ล้มดัง · ออกได้แล้วล้างป้าย"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="AdjustCertsForCreditNoteAsync",
         must=["WhtCertReissuePlan.ForCreditNote(", "WhtCertVoidGuard.CheckAsync(", "FlagReviewAsync(", "ReissueCertWithSavepointAsync("],
         why="รอบ 204 (ข้อ 142): ใบลดหนี้ลดฐานหลังออก 50 ทวิ — ตัวตัดสินเดียว · ยื่นแล้ว/จับคู่ไม่ได้ = ป้ายต้องตรวจ (ไม่แก้เงียบ)"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="ReissueCertWithSavepointAsync",
         must=["CreateSavepointAsync(", "RollbackToSavepointAsync(", "AddChainedAuditLog(", "FlagReviewAsync(", "WithholdingTaxCertStatus.Voided"],
         why="รอบ 204: ยกเลิก+ออกใหม่ใต้ savepoint · audit หลังบันทึกสำเร็จ · ล้ม = ป้ายต้องตรวจ"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="AcknowledgeReviewAsync",
         must=["WhtCertReviewNote.Clear(", "AddChainedAuditLog("],
         why="รอบ 204: ล้างป้าย \"ต้องตรวจ\" ได้เฉพาะทางรับทราบที่มีเหตุผล + audit"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="GetPendingDocumentsAsync",
         must=["WhtCertIssuePlan.PendingLabel(", "LoadWhtPayeeContactsAsync(", "WhtCertAutoFailedNote.ReasonOf(",
               "WhtCertReviewNote.ReasonOf(", "WhtPayeeSplit.UncoveredPayees("],
         why="ลิสต์รอออกใบบอกว่าใบจะออกในนามใคร + ทำไมยังไม่มีใบ (ล้ม/ตั้งให้ออกเอง) — server computes"),
    dict(file="Services/Implementations/TaxService.cs", method="GenerateWhtReport",
         must=["LoadWhtPayeeContactsAsync(", "PayeeInScope(PayeeOf(d)"],
         forbid=["PayeeInScope(d.Contact"],
         why="แถวเตือนใบที่ยังไม่มี 50 ทวิ แบ่ง ภ.ง.ด.3/53/54 จากผู้ถูกหักตัวเดียวกับทะเบียน"),
]


# ── รอบ 203 (คำถามเจ้าของ "หักค่าเบี้ยประกัน 1% ลงถูกช่องไหม"): ประเภทเงินได้มีตารางเดียว ThaiWhtRateTable — ทุกผู้อ่าน
#    ค่าที่เก็บไว้ (ป้าย · รหัส Col7 · ตัวตรวจอัตรา · ข้อความ "ระบุ" 50 ทวิ) ต้องผ่านตาราง ห้ามมี switch/ตารางอัตราชุดที่สอง ──
_WHT203 = "รอบ 203 · WhtIncomeTypeCatalogTests: "
RULES += [
    dict(file="Helpers/PndIncomeTypeCode.cs", method="ForFile",
         must=["ThaiWhtRateTable.Resolve("], forbid=["switch"],
         why=_WHT203 + "รหัส Col7 ไฟล์ ภ.ง.ด. อ่านจากแถวในตารางกลาง (เดิม switch ชุดที่สอง ⇒ ประเภทใหม่ตก default เงียบ)"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="GetIncomeTypeName",
         must=["ThaiWhtRateTable.Resolve("], forbid=["switch"],
         why=_WHT203 + "ป้ายประเภทบนหน้า 50 ทวิ จากตารางกลาง (เดิมไม่รู้จัก 8ad/8tr)"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="IssueWarningsAsync",
         must=["ThaiWhtRateTable.CheckRateForType("],
         why=_WHT203 + "ออก/แก้ 50 ทวิ ในประเทศ เตือนอัตราไม่ตรงประเภท+ชนิดผู้รับ (ตัวตัดสินเดียวกับตอนอนุมัติเอกสาร)"),
    # (ลบ 2026-10-13 ทีม B · A10: กติกา CertificateRowSpecs — ช่องคำตอบที่ไม่มีหน้าใดอ่าน ถอดทิ้งแล้ว · ข้อความ "ระบุ" แถว 5/6
    #  พิมพ์ผ่าน WhtCertFormText → ThaiWhtRateTable.CertificateRowSpecification ตัวเดียวทั้ง HTML/QuestPDF)
    dict(file="Services/Implementations/WhtCreditService.cs", method="CheckRate",
         must=["ThaiWhtRateTable.CheckRateForType("], forbid=["switch", ".Contains("],
         why=_WHT203 + "ทะเบียนภาษีถูกหัก — ตัวตรวจอัตราอ่านตารางกลาง (เดิมจับคำเอง รหัสจาก dropdown ไม่ถูกตรวจเลย)"),
    dict(file=DOC, method="CollectApprovalWarningsAsync",
         must=["WhtLineRateWarning.ForDomesticPayee(", "whtPayeeForWarn?.TaxId"],
         before=[("LoadWhtPayeeAsync(companyId, doc)", "WhtLineRateWarning.ForDomesticPayee(")],
         forbid=["Detect(doc.Contact?.TaxId", "IsForeignPayee(doc.IsForeignService, doc.Contact?.CountryCode)"],
         why=_WHT203 + "อนุมัติเอกสารฝั่งจ่าย: อัตราเทียบประเภทเงินได้ของบรรทัด + ชนิด \"ผู้ถูกหัก\" (ฝ่ายค้าน M2 — ไม่ใช่ผู้รับเงินบน PV) · "
             "ข้อความผ่านตัวประกอบที่ติดป้าย [WHT-RATE] (M3)"),
    dict(file="Helpers/WhtLineRateWarning.cs", method="ForDomesticPayee",
         must=["ThaiWhtRateTable.CheckRateForType(", "WhtPayeeKind.Detect("],
         must_lit=["$\"{Prefix} อัตรา WHT ของ"],
         why=_WHT203 + "ฝ่ายค้าน M3: คำเตือนอัตรา-ประเภทขึ้นต้นป้ายเสมอ (ไม่งั้นทางเข้าไม่มีคนถูกหยุด) · ตัวตัดสินอัตราเดียวกับ 50 ทวิ/ทะเบียนเครดิต"),
    # ประเภทเงินได้รายบรรทัด (DocumentLineIncomeTypeTests): เก็บแล้วต้อง echo กลับ · แก้ไขไม่ส่ง = คงค่าเดิม
    dict(file=DOC, method="ValidateDocumentLinesAsync",
         must=["DocumentLineIncomeType.Decide("],
         why=_WHT203 + "ประเภทเงินได้ที่ชี้ขาดไม่ได้ถูกปฏิเสธพร้อมเหตุผล (ไม่ตกแถว 6 ของ 50 ทวิ เงียบ ๆ)"),
    dict(file=DOC, method="CreateDocumentAsync",
         must=["DocumentLineIncomeType.Decide(line.IncomeTypeCode"],
         why=_WHT203 + "ใบคีย์มือระบุประเภทเงินได้ได้ (เดิมไม่มีช่อง ⇒ 50 ทวิ ได้ \"8\" เสมอ)"),
    dict(file=DOC, method="UpdateDocumentAsync",
         must=["DocumentLineIncomeType.KeptFromPrevious(", "DocumentLineIncomeType.Decide(line.IncomeTypeCode"],
         before=[("prevIncomeTypes", "_db.DocumentLines.RemoveRange(doc.Lines)")],
         why=_WHT203 + "แก้ไขแล้วบันทึก ประเภทที่ OCR เติมไว้ต้องไม่หายเงียบ ๆ (เก็บค่าเดิมก่อนลบบรรทัด)"),
    dict(file="Services/Ai/Prompts/WorkflowPrompts.cs", method="CodeList",
         must=["ThaiWhtRateTable.All"],
         why=_WHT203 + "prompt ถามประเภทเงินได้สร้างรายการรหัส/อัตราจากตารางกลาง (เดิมพิมพ์ตารางผิดกฎหมายไว้ใน prompt)"),
    dict(file=DOC, method="MapDocumentToResponse",
         must=["IncomeTypeCode: l.IncomeTypeCode"],
         why=_WHT203 + "echo ประเภทเงินได้กลับให้ฟอร์มแก้ไข hydrate"),
    # 2026-10-12 (เจ้าของ: 50 ทวิ "ตัวหนังสือและรูปแบบทำให้ตรง"): สอง renderer ฝั่ง C# อ่านข้อความแบบจาก WhtCertFormText ตัวเดียว
    # (เดิม QuestPDF เพี้ยนจาก HTML: หมายเหตุข้อ 3 · (2.2) · (2.3)) · แถว 6 (ระบุ) ผ่าน Row6Spec ตัวเดียว · ช่องลำดับที่ห้ามพิมพ์เลขที่หนังสือรับรอง
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildWithholdingTaxCertHtml",
         must=["WhtCertFormText.Row6Spec(", "WhtCertFormText.Copies.Count", "WhtCertFormText.TaxId10Groups"],
         must_lit=["{WhtCertFormText.Title}", "{WhtCertFormText.Row5}", "{WhtCertFormText.SeqLabel} <span class='sq'>&nbsp;</span>",
                   "string.Join(\"<br>\", WhtCertFormText.FootnoteItems)"],
         forbid_lit=["<span class='sq'>{certNum}", "ฉบับที่", "หนังสือรับรองการหัก", "ตามมาตรา", "ภ.ง.ด.", "ประทับตรา", "หมายเหตุ", "คำเตือน"],
         why="50 ทวิ HTML: ข้อความแบบจาก WhtCertFormText · ลำดับที่ ไม่ใช่เลขที่หนังสือรับรอง · แถว 6 ระบุผ่าน Row6Spec"),
    dict(file="Services/Implementations/PdfGenerationService.WhtCert.cs", method="BuildWhtCertPdf",
         must=["WhtCertFormText.Copies.Count", "WhtCertFormText.PayerHeading", "WhtCertFormText.PayeeHeading"],
         forbid_lit=["ฉบับที่", "หนังสือรับรองการหัก", "ผู้มีหน้าที่หักภาษี", "ผู้ถูกหักภาษี"],
         why="50 ทวิ QuestPDF: ข้อความแบบจาก WhtCertFormText ตัวเดียวกับ HTML (กฎ #4 A สอง renderer ห้าม drift)"),
    dict(file="Services/Implementations/PdfGenerationService.WhtCert.cs", method="BuildIncomeTable",
         must=["WhtCertFormText.Row6Spec(", "WhtCertFormText.Row5", "WhtCertFormText.Div22b", "WhtCertFormText.Div23b"],
         forbid_lit=["ตามมาตรา", "ของกำไรสุทธิ", "อื่น ๆ (ระบุ)"],
         why="50 ทวิ QuestPDF: แถวเงินได้/4(ข) ใช้ข้อความและการตัดบรรทัดเดียวกับ HTML · แถว 6 ระบุผ่าน Row6Spec"),
    dict(file="Services/Implementations/PdfGenerationService.WhtCert.cs", method="BuildFootnote",
         must=["WhtCertFormText.FootnoteItems"], forbid_lit=["กรณีอื่น"],
         why="50 ทวิ QuestPDF: หมายเหตุครบ 3 ข้อจากตัวตั้งเดียว (เดิมขาด \"นอกเหนือจาก 1. และ 2.\")"),
    # รอบ 204 · คำตัดสินข้อ 148: ช่องที่ติ๊ก (แบบยื่น · เงื่อนไขผู้จ่ายเงิน) จากตัวตัดสินเดียว — ภ.ง.ด.2 → (3) · ออกให้ครั้งเดียว → (3)
    # เดิมแต่ละ renderer เทียบ enum เอง ⇒ ภ.ง.ด.2 ไม่มี renderer ไหนติ๊ก · หน้าพิมพ์ wht.html ได้ดัชนีผ่าน DTO (WhtCertFormTextTests ล็อกฝั่ง JS)
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildWithholdingTaxCertHtml",
         must=["WhtCertFormText.FormBoxIndex(cert.TaxFormType)", "WhtCertFormText.PayerConditionIndex(cert.CertificateType)"],
         forbid=["cert.TaxFormType == ", "cert.CertificateType == "],
         why="50 ทวิ HTML: ติ๊กช่องแบบยื่น/เงื่อนไขผู้จ่ายเงินจาก WhtCertFormText ตัวเดียว (รอบ 204 · ภ.ง.ด.2 · ออกให้ครั้งเดียว)"),
    dict(file="Services/Implementations/PdfGenerationService.WhtCert.cs", method="BuildFormTypeRow",
         must=["WhtCertFormText.FormBoxIndex(cert.TaxFormType)"], forbid=["cert.TaxFormType == "],
         why="50 ทวิ QuestPDF: ช่องแบบยื่นจากตัวตัดสินเดียวกับ HTML (รอบ 204 · ภ.ง.ด.2 → (3))"),
    dict(file="Services/Implementations/PdfGenerationService.WhtCert.cs", method="BuildConditionsRow",
         must=["WhtCertFormText.PayerConditionIndex(cert.CertificateType)"], forbid=["cert.CertificateType == "],
         why="50 ทวิ QuestPDF: เงื่อนไขผู้จ่ายเงินจากตัวตัดสินเดียวกับ HTML (รอบ 204 · ออกให้ครั้งเดียว → (3))"),
]


# ── ฝ่ายค้านงาน WHT/50 ทวิ (2026-10-12 · cbc0f705..56ff4998) — WhtAdversarialFixTests ล็อก helper · กติกาข้างล่างล็อกจุดเรียก ──
_WHTADV = "ฝ่ายค้าน WHT 2026-10-12 · WhtAdversarialFixTests: "
RULES += [
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildProjectedGlAsync",
         must=["LoadWhtPayeeAsync(companyId, doc)", "CodeChain(doc.IsForeignService, whtPayee?.CountryCode",
               # รอบ 204 (ข้อ 143): พรีวิวแบ่งผังต่อผู้ถูกหักเหมือน JE จริง
               "WhtPayableSplitAsync(pvWht)", "WhtPayableSplitAsync(doc.WithholdingTaxAmount)", "WhtPayeeSplit.Allocate("],
         forbid=["CodeChain(doc.IsForeignService, doc.Contact?.CountryCode", "PreferredLabel(doc.IsForeignService, doc.Contact?.CountryCode"],
         why=_WHTADV + "M1 พรีวิว GL ผัง 21916/21917/21918 ตามผู้ถูกหัก — ตัวเดียวกับ JE จริง (AutoPostToJournalAsync)"),
    dict(file="Helpers/ApprovalAcknowledgement.cs", method="PassesWithoutPerson",
         must=["WhtLineRateWarning.IsApprovalWarning(", "WhtCertIssuePlan.IsApprovalWarning("],
         why=_WHTADV + "M3 นโยบายเจ้าของ \"เตือน ไม่บล็อก\": คำเตือน [WHT-RATE]/[WHT-PAYEE] ไม่หยุดใบประจำ/LINE/OCR อนุมัติอัตโนมัติ/API"),
    dict(file=DOC, method="ConvertCoreAsync",
         must=["IncomeTypeCode: Accounting.Helpers.DocumentLineIncomeType.Inherit(s.Line.IncomeTypeCode,", "WhtCertIssuePlan.AppliesTo(targetType)"],
         why=_WHTADV + "M4 แปลงเอกสาร: ประเภทเงินได้รายบรรทัดตามไปกับอัตรา WHT (เดิมหาย ⇒ 50 ทวิ ตก \"8\")"),
    dict(file=CLONE, method="Clone",
         must=["DocumentLineIncomeType.Inherit(l.IncomeTypeCode,", "WhtCertIssuePlan.AppliesTo(options?.OverrideType ?? src.DocumentType)"],
         why=_WHTADV + "M4 คัดลอกเอกสาร: ตัวสืบทอดเดียวกับเส้นแปลง (ค่าเก่าที่ชี้ขาดไม่ได้ไม่ทำให้คัดลอกล้ม)"),
    dict(file="Services/Implementations/RecurringTransactionService.cs", method="CreateDocumentFromTemplateAsync",
         must=["DocumentLineIncomeType.Inherit(", "WhtCertIssuePlan.RecurringTemplateAutoIssue(", "ApprovalAckSource.Unattended"],
         forbid=["_whtService.CreateAsync(", "GenerateWhtCertFromTemplateAsync("], forbid_lit=["\"40(8)\""],
         why=_WHTADV + "M4/M5 ใบประจำ: ประเภทเงินได้จากเทมเพลต · 50 ทวิ ออกทางเดียวกับใบคีย์มือ (อนุมัติ/จ่าย → TryAutoIssueForDocumentAsync) — "
             "ห้ามกลับไปออกใบตรงด้วยรหัสฝัง/ผู้ถูกหักผิดคน/ข้ามติ๊ก"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="MapLineToResponse",
         must=["CertificateRow: Accounting.Helpers.ThaiWhtRateTable.CertificateRow(l.IncomeTypeCode)"],
         why=_WHTADV + "M6 แถว 50 ทวิ ของหน้าเว็บมาจากเซิร์ฟเวอร์ (ตัวตัดสินเดียวกับ PDF) — หน้าไม่จับคู่รหัสเอง"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="AutoGenerateFromDocumentAsync",
         before=[("WhtCertIssuePlan.PayeeTaxIdProblem(", "WithholdingTaxCertStatus.Voided;")],
         why=_WHTADV + "L1 ตรวจผู้ถูกหัก/เลขผู้เสียภาษีก่อนยกเลิกใบร่างระดับเอกสาร — ด่านล้มต้องไม่ทิ้งใบร่างที่ถูกยกเลิกไปแล้ว"),
    dict(file=DOC, method="CreateDocumentAsync",
         must=["DocumentLineIncomeType.PayerSideProblem("],
         why=_WHTADV + "L3 เอกสารฝั่งผู้จ่ายเลือก gov1/1 ไม่ได้ (ปฏิเสธพร้อมเหตุผล)"),
    dict(file=DOC, method="UpdateDocumentAsync",
         must=["DocumentLineIncomeType.PayerSideProblem("],
         call_args=[("DocumentLineIncomeType.PayerSideProblem(", "KeptForPayerSideCheck(prevIncomeTypes")],
         before=[("DocumentLineIncomeType.PayerSideProblem(", "_db.DocumentLines.RemoveRange(doc.Lines)")],
         why=_WHTADV + "L3 แก้ไข: ปฏิเสธเฉพาะการเลือกใหม่ (ค่าเดิมของบรรทัดเดียวกันบันทึกซ้ำได้) · ตรวจก่อนลบบรรทัด"),
    # ฝ่ายค้านรอบสอง (2026-10-12 · WhtDerivedIncomeTypeTests): L3 + M4 รวมกันทำให้เอกสารลูกล้ม (gov1/1 จากต้นทาง ⇒ PV ถูกปฏิเสธ ·
    # รายการประจำหยุดสร้างใบเงียบ ๆ) ⇒ ทุกเส้นสืบทอดส่ง "ชนิดปลายทางเป็นฝั่งผู้จ่ายไหม" · OCR ทิ้งประเภทต้องห้ามก่อนเขียนบรรทัดฝั่งผู้จ่าย
    dict(file="Services/Implementations/RecurringTransactionService.cs", method="CreateDocumentFromTemplateAsync",
         must=["WhtCertIssuePlan.AppliesTo(docType)"],
         why=_WHTADV + "รอบสอง: รายการประจำสืบทอดประเภทลงใบฝั่งผู้จ่ายโดยทิ้ง gov1/1 (ไม่ใช่ล้มแล้ววนลองทุกรอบ)"),
    dict(file=OCR, method="BuildScanLinesAsync",
         must=["DocumentLineIncomeType.ForSystemWrittenPayerLine(result.WhtIncomeTypeCode)"],
         forbid=["? result.WhtIncomeTypeCode : null"],
         why=_WHTADV + "รอบสอง: OCR ฝั่งซื้อไม่เขียน gov1/1 ลงบรรทัด (มิฉะนั้นแปลง/แก้ใบภายหลังล้มด่าน L3)"),
]

# รอบ 204 (คำตัดสินข้อ 148 (4)) — ผู้จ่ายออกภาษีแทน (ออกให้ตลอดไป/ครั้งเดียว): สูตรมีที่เดียว Helpers/WhtGrossUp · เทสต์ (WhtGrossUpTests) ล็อก helper
# ⇒ ล็อกจุดเรียกใน service: ยอดสุทธิ · ขา Dr ค่าใช้จ่ายของส่วนที่ออกแทน (3 สาย) · ประเภท/เงินได้บน 50 ทวิ · คอลัมน์เงื่อนไข Col11
_WHTBORNE = "รอบ 204 ผู้จ่ายออกภาษีแทน: "
RULES += [
    dict(file=DOC, method="CreateDocumentAsync",
         must=["ResolveWhtCertTypeAsync(", "ApplyWhtGrossUp(", "WhtGrossUp.PayeeTotal("],
         before=[("ApplyWhtGrossUp(", "WhtGrossUp.PayeeTotal(")],
         forbid=["doc.SubTotal + totalVat - totalWht"],
         why=_WHTBORNE + "สร้างเอกสาร: ภาษีรายบรรทัดผ่าน WhtGrossUp ก่อนรวมยอด · ยอดจ่ายผู้รับ = PayeeTotal (รวมส่วนที่ออกแทน)"),
    dict(file=DOC, method="UpdateDocumentAsync",
         must=["ResolveWhtCertTypeAsync(", "ApplyWhtGrossUp(", "WhtGrossUp.ForLines(", "updWhtCertTypeChanged"],
         forbid=["doc.SubTotal + totalVat - totalWht"],
         why=_WHTBORNE + "แก้เอกสาร: สูตรเดียวกับตอนสร้าง · เปลี่ยนเงื่อนไขโดยไม่ส่งบรรทัดต้องคิดใหม่ (ห้าม silent no-op)"),
    dict(file=DOC, method="AutoPostToJournalAsync",
         must=["WhtGrossUp.MismatchReason("],
         # สามจุดเรียกผูกกับโค้ดข้างเคียงของแต่ละสาย (pattern เส้นตรง — ห้าม [\s\S]* ซ้อน: ล้มแล้ว backtrack ทั้งเมธอดจนค้าง)
         must_re=[r"AddLine\(\s*cilResidualAccount[^;]*;\s*AddPayerBorneWhtExpense\(\)\s*;",
                  r"AddPayerBorneWhtExpense\(\)\s*;\s*var\s+claimableVatPi\b",
                  r"AddPayerBorneWhtExpense\(\)\s*;\s*var\s+claimableVatPv\b"],
         why=_WHTBORNE + "JE: Dr ส่วนที่ออกแทนทั้งสาม (ใบรับรองแทนใบเสร็จ · PI/ค่าใช้จ่าย · ใบสำคัญจ่ายเดี่ยว) — ไม่งั้นเจ้าหนี้/เงินออกรวมส่วนที่ออกแทนแต่ฝั่ง Dr ขาด (JE-BAL)"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="AutoGenerateFromDocumentAsync",
         must=["WhtGrossUp.Normalize(doc.WhtCertType)", "WhtGrossUp.ForLines(", "WhtGrossUp.MismatchReason("],
         must_re=[r"IncomeAmount\s*=\s*Prorate\(\s*line\.Amount\s*\+\s*borneByLine"],
         forbid=["CertificateType = WithholdingTaxCertType.Withhold"],
         why=_WHTBORNE + "50 ทวิ จากเอกสาร: เงื่อนไขตามเอกสาร · เงินได้ = ฐาน + ภาษีที่ออกแทน (ม.39)"),
    dict(file="Services/Implementations/TaxFilingExportService.cs", method="BuildPndRows",
         must=["WhtGrossUp.FilingCondition("], forbid=["is 2 ? 2 : 1"],
         why=_WHTBORNE + "ไฟล์ ภ.ง.ด. Col11 1/2/3 — เดิม \"ออกให้ครั้งเดียว\" ตกเป็น 1"),
    dict(file="Services/Implementations/TaxService.cs", method="GenerateWhtReport",
         must=["WhtGrossUp.FilingCondition("], forbid=["is 2 ? 2 : 1"],
         why=_WHTBORNE + "รายงาน ภ.ง.ด. (ต้นทางของปุ่ม e-Filing) เงื่อนไข 1/2/3"),
    dict(file="Services/Implementations/Tax/Section65TerValidator.cs", method="Evaluate",
         must=["PayerBorneFindings(doc, ctx.HasAttachment)"],
         why=_WHTBORNE + "§65 ตรี (3) บวกกลับส่วนที่ออกแทน (ไม่มีสัญญา = เสน่หา) — ผ่านตัวตัดสินเดียว PayerBorneFindings"),
    dict(file="Services/Implementations/Tax/Section65TerValidator.cs", method="PayerBorneFindings",
         must=["PayerBorneWhtRuleCode", "doc.WhtPayerBorneAmount", "doc.WhtBorneByContract", "PayerBorneContractRuleCode"],
         why=_WHTBORNE + "§65 ตรี (3): ไม่มีสัญญา ⇒ บวกกลับ · มีสัญญา (ทีม B) ⇒ ไม่บวกกลับ + ร่องรอย RD-65ter(3)-WHT-BORNE-CONTRACT"),
]

# ── รอบ 204 ทีม B (คำสั่งเจ้าของ 2026-10-13) — บริการต่างประเทศ + ผู้จ่ายออกภาษีแทน · ผู้จ่ายรับภาระตามสัญญา ──
# ForeignServiceVatTests/WhtGrossUpTests ล็อก helper · กติกาข้างล่างล็อกจุดเรียก (ถอดแล้ว VAT ภ.พ.36 กลับไปคิดจากฐานที่ไม่รวมภาษีออกแทน = ประเมินขาด)
_FSBORNE = "รอบ 204 ทีม B บริการต่างประเทศ+ออกแทน: "
RULES += [
    dict(file=DOC, method="CreateDocumentAsync",
         must=["ApplyForeignServiceBorneVat(", "WhtGrossUp.PayerSideFlagOnCreate("],
         before=[("ApplyWhtGrossUp(", "ApplyForeignServiceBorneVat("), ("ApplyForeignServiceBorneVat(", "WhtGrossUp.PayeeTotal(")],
         why=_FSBORNE + "สร้าง: VAT ภ.พ.36 บนฐานรวมภาษีที่ออกแทน (คำตัดสินข้อ 40) ก่อนรวมยอด · ธงสัญญา/e-Withholding ผ่านตัวตัดสินเดียว"),
    dict(file=DOC, method="UpdateDocumentAsync",
         must=["ApplyForeignServiceBorneVat(", "ForeignServiceVat.VatWithBorne(", "WhtGrossUp.PayerSideFlagOnUpdate("],
         before=[("ApplyWhtGrossUp(", "ApplyForeignServiceBorneVat(")],
         why=_FSBORNE + "แก้: สูตรเดียวกับตอนสร้าง · เปลี่ยนธง/เงื่อนไขโดยไม่ส่งบรรทัดคิด VAT ใหม่ด้วย (ห้าม silent no-op)"),
    dict(file=DOC, method="ApplyForeignServiceBorneVat",
         must=["WhtGrossUp.ForLines(", "ForeignServiceVat.VatWithBorne("],
         why=_FSBORNE + "VAT รายบรรทัดจากตัวตั้งเดียว (ห้ามประกอบสูตรเอง)"),
    dict(file=DOC, method="ReclassifyForeignServiceAsync",
         must=["ForeignServiceVat.ReclassifyPayerBorneRefusal("],
         why=_FSBORNE + "ติ๊ก/ปลดบริการต่างประเทศย้อนหลังบนใบออกแทน ⇒ ปฏิเสธพร้อมทางไปต่อ (ยกเลิกแล้วออกใหม่) — VAT คิดตอนบันทึก"),
    dict(file=DOC, method="SetWhtBorneByContractAsync",
         must=["Section65TerValidator.PayerBorneFindings(", "Section65TerValidator.ReplacePayerBorneFindings(", "AddChainedAuditLog("],
         why=_FSBORNE + "แก้ \"มีสัญญา\" หลังอนุมัติ: ประเมินบวกกลับใหม่ด้วยตัวตัดสินเดียว + audit"),
    dict(file=DOC, method="EvaluateSection65TerAsync",
         must=["HasAttachment: hasAttachment"],
         why=_FSBORNE + "มีสัญญาแต่ไม่มีไฟล์แนบ ⇒ ข้อสังเกตแนบสัญญา (ไม่บล็อก)"),
    dict(file="Services/Implementations/TaxService.cs", method="GeneratePp36Report",
         must=["ForeignServiceVat.Pp36ReportBorne(doc.WhtPayerBorneAmount"],
         why=_FSBORNE + "รายงาน ภ.พ.36: ใบที่คิดออกแทนบนเอกสารใช้ยอดของเอกสาร ไม่บวกจาก 50 ทวิ ซ้ำ"),
    dict(file="Controllers/WithholdingTaxCertController.cs", method="GrossUpPreview",
         must=["ForeignServiceVat.VatWithBorne("],
         why=_FSBORNE + "พรีวิวบนฟอร์ม: VAT ภ.พ.36 สูตรเดียวกับตอนบันทึก (หน้าเว็บไม่มีสูตร)"),
    # ฝ่ายค้านรอบ 204 M1 — ใบลด/เพิ่มหนี้ของใบเจ้าของ ภ.พ.36 ที่ออกแทน: VAT บนฐานรวม (ติ๊กธงไม่ได้ แต่ใบต้นทางเป็นเจ้าของ) · คำเตือน/รายงานครอบด้วย
    dict(file=DOC, method="CreateDocumentAsync",
         must=["ForeignServiceVat.BorneVatApplies(doc.IsForeignService, createLinkedSrc?.Owns == true, createWhtBorne)"],
         forbid=["doc.IsForeignService && createWhtBorne != 0m"],
         why=_FSBORNE + "M1: ใบลดหนี้ของใบออกแทนคิด VAT บนฐานรวม — เดิมเฉพาะ doc.IsForeignService ⇒ ลดเต็มใบแล้ว 21912/11640 ค้าง 79.41×7%"),
    dict(file=DOC, method="UpdateDocumentAsync",
         must=["ForeignServiceVat.BorneVatApplies(doc.IsForeignService, updSrcOwns, updWhtBorne)",
               "ForeignServiceVat.BorneVatApplies(doc.IsForeignService, updSrcOwns, keptBorne)"],
         forbid=["doc.IsForeignService && updWhtBorne != 0m", "doc.IsForeignService && keptBorne != 0m"],
         why=_FSBORNE + "M1: แก้ใบลดหนี้ใช้ตัวตัดสินเดียวกับตอนสร้าง"),
    dict(file=DOC, method="CollectApprovalWarningsAsync",
         must=["ForeignServiceVat.Pp36BaseApprovalWarning(doc, pp36NoteSrcOwns)"],
         why=_FSBORNE + "M1: [PP36-BASE] ครอบใบลด/เพิ่มหนี้ของใบเจ้าของ ภ.พ.36 (ใบร่างเก่าที่คิดบนฐานอย่างเดียว)"),
    dict(file="Services/Implementations/TaxService.cs", method="GeneratePp36Report",
         must=["ForeignServiceVat.Pp36NoteBaseDelta(n.DocumentType, n.SubTotal, n.WhtPayerBorneAmount)"],
         why=_FSBORNE + "M1: ฐานในรายงานหักส่วนที่ออกแทนของใบลดหนี้ด้วย — ลดเต็มใบแล้วฐานต้องเป็น 0"),
]


# ── ฝ่ายค้านรอบ 204 (L2–L7a) — ทุกจุดคงหลัก "ไม่ล็อกตาย" (DECISIONS ข้อ 150): บล็อกเฉพาะที่กฎหมาย/GL บังคับ พร้อมทางไปต่อ ──
_R204ADV = "ฝ่ายค้านรอบ 204: "
RULES += [
    dict(file="Services/Implementations/DocumentService.OcrLineFix.cs", method="FixOcrLineDiscountAsync",
         must=["OcrLineDiscountFixRefusalAsync(db, companyId, doc, ct)"],
         before=[("OcrLineDiscountFixRefusalAsync(db, companyId, doc, ct)", "db.AddChainedAuditLog(")],
         why=_R204ADV + "L2: ใบขายที่ออกแล้ว (§86/4) ⇒ ปฏิเสธพร้อมทางไปต่อ ก่อนแตะบรรทัด"),
    dict(file="Services/Implementations/DocumentService.OcrLineFix.cs", method="OcrLineDiscountFixRefusalAsync",
         must=["OcrLineDiscountFixScope.Refusal(", "OcrLineDiscountFixScope.IsOurSalesDocument(", "AdjustmentNoteAccount.ResolvePostedSide("],
         why=_R204ADV + "L2: กติกา/ฝั่งใบลดหนี้จากตัวตัดสินกลางเท่านั้น"),
    dict(file="Services/Implementations/DataIntegrityReportService.cs", method="LoadOcrLineDiscountSuspectsAsync",
         must=["DocumentService.OcrLineDiscountFixRefusalAsync(_db, companyId, d)", "fixRefusal == null ? OcrLineDiscountFixPath(d.Id) : null"],
         why=_R204ADV + "L2: แถวรายงานซ่อนปุ่ม \"แก้\" ด้วยตัวตัดสินเดียวกับตัวแก้ (server-computed)"),
    dict(file=DOC, method="ResolveWhtPayableSplitAsync",
         must=["EWithholdingScope.RequireAccount(eWhtVia,"],
         forbid=["&& await FindAccountAsync(companyId, Accounting.Helpers.WhtPayableAccount.EWithholdingCode) is { } eWht"],
         why=_R204ADV + "L3: ใบ e-Withholding ไม่มีผัง 21923 ⇒ ล้มดัง (เดิมตก 2191x เงียบ ขณะที่หน้านำส่ง/รายงานตัดใบนี้ออก)"),
    dict(file="Services/Implementations/IntegrationService.cs", method="BuildIntegrationJournalLinesAsync",
         must=["EWithholdingScope.RequireAccount(intEWhtVia,"],
         why=_R204ADV + "L3: เส้น integration ตัวตัดสินเดียวกับ DocumentService"),
    dict(file="Services/Implementations/IntegrationService.cs", method="CreatePaymentVoucherJournalAsync",
         must=["EWithholdingScope.RequireAccount(payEWhtVia,"],
         why=_R204ADV + "L3: ใบสำคัญจ่ายจาก integration ตัวตัดสินเดียวกับ DocumentService"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="IssueAsync#1",
         must=["WhtCertIssuePlan.PayeeTaxIdProblem(issuePayee?.Name, issuePayee?.TaxId,", "WhtCertPayeeTaxIdDraft.IssueNeedsAckRuleCode",
               "WhtCertPayeeTaxIdDraft.IssueAckRuleCode", "AddChainedAuditLog("],
         before=[("WhtCertIssuePlan.PayeeTaxIdProblem(", "cert.Status = WithholdingTaxCertStatus.Issued")],
         why=_R204ADV + "L5: ออกร่างที่ผู้ถูกหักไม่มีเลขผู้เสียภาษี ⇒ ตรวจซ้ำก่อนเปลี่ยนสถานะ · รับทราบแล้วออกได้ + audit (ไม่ล็อกตาย)"),
    dict(file="Controllers/WithholdingTaxCertController.cs", method="Issue",
         must=["_whtService.IssueAsync(companyId, certId, request, JwtHelper.GetUserIdFromClaims(User))"],
         why=_R204ADV + "L5: endpoint ส่งการรับทราบ + ผู้ทำรายการ (audit มี UserId)"),
    dict(file=DOC, method="SetWhtBorneByContractAsync",
         must=["UserId = Guid.TryParse(actor, out var borneActor) ? borneActor : null"],
         why=_R204ADV + "L7a: audit แก้ \"มีสัญญา\" ต้องมีผู้ทำรายการ (UserId) ไม่ใช่แค่ข้อความใน NewValues"),
]

# ── รอบ 204 — แบบ ภ.ง.ด.2 (คำตัดสินเจ้าของ DECISIONS ข้อ 148 · Pnd2FormTests ล็อก helper · กติกาข้างล่างล็อกจุดเรียก) ──
# ตัวตัดสินแบบ/ผังรับ "ประเภทเงินได้ของบรรทัดที่หัก" — ทางที่ไม่ส่ง = ดอกเบี้ย/ปันผลบุคคลตกกลับ ภ.ง.ด.3 เงียบ ๆ (GL 21915 ≠ ทะเบียน/นำส่ง)
_PND2 = "รอบ 204 ภ.ง.ด.2 (ข้อ 148): "
RULES += [
    dict(file="Services/Implementations/TaxService.cs", method="GenerateWhtReport",
         must=["TipShareAllocation.IsTipJournalReference(je.Reference)"],
         forbid=["isPnd2 && (je.Reference"],
         why=_PND2 + "ฝ่ายค้านข้อ 1: JE จ่ายทิปข้ามทุกแบบในสาขา JV ล้วน — 50 ทวิ ของทิปนับแล้ว (มิฉะนั้นภาษี ภ.ง.ด.3 เบิ้ล + แถวปลอมในไฟล์ยื่น)"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="ResolveWhtFormType",
         must=["WhtPayeeKind.Pnd2ChoiceAllowed(withheldIncomeTypeCodes)"],
         why=_PND2 + "ฝ่ายค้านข้อ 2: เคารพ ภ.ง.ด.2 ที่ผู้ใช้เลือกเฉพาะเมื่อประเภทเงินได้ไม่ขัด"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="IssueWarningsAsync",
         must=["TaxType.WithholdingTax2 or TaxType.WithholdingTax3 or TaxType.WithholdingTax53"],
         why=_PND2 + "ฝ่ายค้านข้อ 3: ใบ ภ.ง.ด.2 ตรวจอัตราเทียบประเภทเงินได้ด้วย (ม.54)"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildProjectedGlAsync",
         must=["LoadWithheldAsync(companyId, doc)", "whtPayee?.Name, withheldCodes, royaltyAsPnd2)", "c?.Name, payeeCodes, royaltyAsPnd2)",
               "WhtIncomeCodes.WithheldAt(orderedLines,"],
         why=_PND2 + "พรีวิว GL ก่อนอนุมัติใช้ชุดรหัสเงินได้เดียวกับ JE จริง (ดอกเบี้ยบุคคล ⇒ 21915 ไม่ใช่ 21916)"),
    # หลังรวมทีม T4 (ข้อ 143): ทุกขา WHT ฝั่งผู้จ่ายของ JE อนุมัติ/รายงวดผ่าน ResolveWhtPayableSplitAsync ⇒ รหัสเงินได้ส่งที่ตัวแบ่ง (ใบรายเดียว = LoadWithheldAsync
    # ทั้งใบ · ใบหลายราย = รหัสของบรรทัดของรายนั้น) · JE ห้ามเรียกตัวเลือกผังตรง (ทางลัดที่ข้ามทั้งการแบ่งผู้ถูกหักและรหัสเงินได้)
    dict(file=DOCSVC, method="AutoPostToJournalAsync",
         must=["ResolveWhtPayableSplitAsync(companyId, doc,"], forbid=["ResolveWhtPayableAccountAsync("],
         why=_PND2 + "JE ตอนอนุมัติ — ผัง 21915 ของดอกเบี้ย/ปันผลบุคคล ต้องได้จากชุดรหัสเดียวกับ 50 ทวิ (ผ่านตัวแบ่งต่อผู้ถูกหัก)"),
    dict(file=DOCSVC, method="CreatePaymentJournalAsync",
         must=["ResolveWhtPayableSplitAsync(companyId, doc, thbWht, payment.PaymentDate)"], forbid=["ResolveWhtPayableAccountAsync("],
         why=_PND2 + "JE รายงวด (เกณฑ์เงินสด) — ทางจ่ายไม่ Include บรรทัด ⇒ ตัวแบ่งอ่านบรรทัด/รหัสจากฐานเอง ไม่ใช่ doc.Lines ว่าง"),
    dict(file=DOCSVC, method="ResolveWhtPayableSplitAsync",
         must=["LoadWithheldAsync(companyId, doc)", "WhtIncomeCodes.WithheldAt(portionLines, p.LineIndexes)"],
         forbid=["doc.IsForeignService)"],   # เรียกตัวเลือกผังโดยไม่ส่งรหัสเงินได้ (อาร์กิวเมนต์ตัวสุดท้าย) = ภ.ง.ด.2 ตกกลับ 21916
         why=_PND2 + "ตัวแบ่งผังต่อผู้ถูกหักส่งรหัสเงินได้ทุกทาง: ใบรายเดียว = ทั้งใบ (เดิม) · ใบหลายราย = บรรทัดของรายนั้นเท่านั้น "
             "(ดอกเบี้ยของบุคคล ก ⇒ 21915 แม้ใบมีค่าบริการของ ข)"),
    dict(file=DOCSVC, method="ChangeWhtPayeeAsync",
         must=["WhtPayableAccount.DocumentPayableCodes", "WhtIncomeCodes.WithheldAt(lines, newPortion.LineIndexes)",
               "WhtIncomeCodes.WithheldAt(lines, portion.LineIndexes)"],
         forbid_lit=['"21916", "21917", "21918" }'],
         why=_PND2 + "เปลี่ยนผู้ถูกหัก: หนี้ที่ลงไว้ที่ 21915 ต้องถูกนับ/ย้ายด้วย · แบบ/ผังปลายทางตัดสินด้วยรหัสของบรรทัดของผู้ถูกหักรายนั้น"),
    dict(file=DOCSVC, method="CollectApprovalWarningsAsync",
         must=["WhtPayeeKind.Pnd2MixWarning("], must_lit=['ApprovalWarningPrefix + " " + pnd2Mix'],
         why=_PND2 + "ใบปนดอกเบี้ย/ปันผลกับเงินได้อื่นของบุคคล ⇒ เตือนให้แยกใบ ([WHT-PAYEE] เตือน ไม่บล็อก)"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="AutoGenerateFromDocumentAsync",
         must=["ResolveWhtFormType(payee, null, doc.IsForeignService,", "LoadWithheldAsync(companyId, doc)",
               "WhtIncomeCodes.WithheldForPayee(doc.Lines, doc.ContactId, doc.WhtPayeeContactId, formPayee)"],
         why=_PND2 + "50 ทวิ อัตโนมัติลงแบบตามประเภทเงินได้ (ตรงผังของ JE)"),
    dict(file="Services/Implementations/TaxService.cs", method="GenerateWhtReport",
         must=["WhtIncomeCodes.Withheld(d.Lines)", "WhtPayableAccount.CodeFor(report.TaxType)",
               "PayeeInScope(LinePayeeOf(d, l), d.IsForeignService, LinePayeeCodes(d, l), RoyaltyOf(d))",
               "PayeeInScope(LinePayeeOf(doc, l), doc.IsForeignService, LinePayeeCodes(doc, l), RoyaltyOf(doc))"],
         forbid_lit=['report.TaxType == TaxType.WithholdingTax3 ? "21916"'],
         why=_PND2 + "รายงานแบ่งแบบด้วยประเภทเงินได้ + ผังของแบบจากตัวตั้งเดียว (เดิมสูตร 3/53 พิมพ์รหัสเอง)"),
    dict(file="Services/Implementations/StatutoryRemittanceService.cs", method="GetDashboardAsync",
         must=["d.IncomeCodes, royalty.AppliesTo(date))", "WhtIncomeCodes.At(lineCodes, p.LineIndexes)", "pk.Name, pCodes, royalty.AppliesTo(date))"], must_lit=['"WhtPnd2"'],
         why=_PND2 + "หน้านำส่ง: ภ.ง.ด.2 มีแถวของตัวเอง + เอกสารที่ยังไม่มี 50 ทวิ แบ่งแบบด้วยประเภทเงินได้"),
    dict(file="Services/Implementations/StatutoryRemittanceService.cs", method="GetFilingCalendarAsync",
         must=["d.IncomeCodes, calRoyalty.AppliesTo(dt))", "WhtIncomeCodes.At(calLineCodes, p.LineIndexes)", "pk.Name, pCodes, calRoyalty.AppliesTo(dt))"], must_lit=['"WhtPnd2"'],
         why=_PND2 + "ปฏิทินยื่น: แบบเดียวกับหน้านำส่ง (สองจอของ service เดียวกันห้ามเล่าคนละเรื่อง)"),
    dict(file="Services/Implementations/Payroll/TipPayoutService.cs", method="DistributeAndPayoutAsync",
         must=["TipShareAllocation.PayableAccountCode"], must_lit=["TipShareAllocation.JournalReferencePrefix"],
         forbid_lit=['a.AccountCode == "21915"'],
         why=_PND2 + "ทิป = ม.40(2) ภ.ง.ด.3 ⇒ ผัง 21916 (เดิม 21915 ผัง ภ.ง.ด.2 — นำส่ง ภ.ง.ด.3 Dr บัญชีที่ไม่มียอด)"),
]

# รอบ 204 — แก้ผลฝ่ายค้านงานผู้จ่ายออกภาษีแทน (P1-1 ภ.พ.36 · P1-2 ใบลด/เพิ่มหนี้ · P2-1 กระดาษ · P2-2 พรีวิว GL · P3-1 รายงาน · P3-2 ซิงค์)
_WHTBORNE_ADV = "รอบ 204 ฝ่ายค้านผู้จ่ายออกภาษีแทน: "
RULES += [
    dict(file=DOC, method="ResolveWhtCertTypeAsync",
         must=["WhtGrossUp.TypeError(doc.DocumentType, type, doc.IsForeignService, sourceType.HasValue)",
               "WhtGrossUp.IsAdjustmentNote(doc.DocumentType)", "WhtGrossUp.SourceMismatchReason("],
         why=_WHTBORNE_ADV + "P1-1/P1-2: ตัวตัดสินเงื่อนไขตรวจค่าสุดท้ายกับธงบริการต่างประเทศ · ใบลด/เพิ่มหนี้สืบทอดจากใบต้นทางฝั่งผู้จ่าย"),
    dict(file=DOC, method="UpdateDocumentAsync",
         must=["keepIfUnspecified: doc.WhtCertType"],
         forbid=["if (request.WhtCertType.HasValue)"],
         why=_WHTBORNE_ADV + "P1-1: ตัดสินเงื่อนไขทุกครั้งที่แก้ (ติ๊กบริการต่างประเทศทีหลังบนใบออกแทนต้องล้มดัง · ใบลดหนี้ร่างเดิมได้เงื่อนไขของใบต้นทาง)"),
    dict(file=DOC, method="AutoPostToJournalAsync",
         must=["AddPayerBorneWhtExpense(reverse: isCreditNote)",
               "WhtGrossUp.TypeError(doc.DocumentType, doc.WhtCertType, doc.IsForeignService, hasPayerSideSource: true)"],
         must_re=[r"WhtGrossUp\.Normalize\(source\.WhtCertType\)\s*!=\s*WhtGrossUp\.Normalize\(doc\.WhtCertType\)"],
         why=_WHTBORNE_ADV + "P1-2: ใบลดหนี้ซื้อกลับขาส่วนที่ออกแทน (ใบเพิ่มหนี้ Dr) + เงื่อนไขไม่ตรงใบต้นทาง = ล้มดัง · P1-1: ใบต่างประเทศ+ออกแทน (ข้อมูลเก่า) ไม่ลง JE"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="AdjustCertsForCreditNoteAsync",
         must=["WhtGrossUp.ForLines(", "cnBorneByLine.GetValueOrDefault(l.Id)", "cn.SubTotal + cn.WhtPayerBorneAmount"],
         why=_WHTBORNE_ADV + "P1-2: ใบลดหนี้ลดเงินได้บน 50 ทวิ = ฐาน + ส่วนที่ออกแทน (ม.39) — ตรงกับเงินได้ของใบเดิม"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildDocumentHtml",
         must_lit=["{L.TotalWhtPayerBorne}", "doc.WhtPayerBorneAmount > 0"],
         why=_WHTBORNE_ADV + "P2-1: กระดาษบวกลงยอด — แถวผู้จ่ายออกแทน (+) (คู่กับ QuestPDF)"),
    dict(file="Services/Implementations/PdfGenerationService.DocumentRenderer.cs", method="ComposeSummary",
         must=["L.TotalWhtPayerBorne", "doc.WhtPayerBorneAmount > 0"],
         why=_WHTBORNE_ADV + "P2-1: สอง renderer ห้าม drift — แถวผู้จ่ายออกแทนใน QuestPDF ด้วย"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildProjectedGlAsync",
         must=["WhtGrossUp.ForLines(", "gross - doc.WithholdingTaxAmount + previewBorne"],
         why=_WHTBORNE_ADV + "P2-2: พรีวิว GL มีขา Dr ส่วนที่ออกแทน + เจ้าหนี้รวมส่วนนี้ (ตรง JE จริง)"),
    dict(file="Services/Implementations/TaxService.cs", method="GenerateWhtReport",
         must=["WhtGrossUp.FilingCondition(doc.WhtCertType)", "unissuedBorne.GetValueOrDefault(line.Id)", "WhtCondition = unissuedCondition"],
         why=_WHTBORNE_ADV + "P3-1: แถวเตือนใบยังไม่ออก 50 ทวิ — เงินได้รวมส่วนที่ออกแทน + เงื่อนไข Col11 ของเอกสาร"),
    dict(file="Services/Implementations/IntegrationService.cs", method="ResyncUpdateExpenseCoreAsync",
         must=["WhtGrossUp.PayeeTotal(", "existing.WhtPayerBorneAmount = 0m"],
         forbid=["existing.TotalAmount = subTotal + totalVat - totalWht"],
         why=_WHTBORNE_ADV + "P3-2: ซิงค์ทับยอดแบบหัก ณ ที่จ่าย ⇒ ตั้งเงื่อนไขกลับอย่างประกาศ (ไม่ค้าง WhtCertType/ส่วนที่ออกแทน)"),
]

# รอบ 204 — ฝ่ายค้านงานหลายผู้ถูกหัก/ผู้จ่ายออกภาษีแทน (หลัง 778e95f9 + c3f47b4d): P1 ผู้ถูกหักรายบรรทัดรายเดียว · P2a/b/c ใบลดหนี้ ·
# P3-1..7 (ใบแทนของใบร่าง · ตัวออกเลขเดียว · ล็อก · งวดยื่นแล้วทุกเดือน · เฉลี่ยไม่ติดลบ · สืบทอดผู้ติดต่อที่ถูกลบ · ผู้ถูกหักไม่ตรง)
_WHT204_ADV = "รอบ 204 ฝ่ายค้านหลายผู้ถูกหัก: "
_CERTSVC = "Services/Implementations/WithholdingTaxCertService.cs"
RULES += [
    dict(file=DOC, method="ResolveLineWhtPayeesAsync",
         must=["WhtPayeeSplit.LiftSinglePayee(", "doc.WhtPayeeContactId = liftedDocPayee"],
         why=_WHT204_ADV + "P1: ทุกบรรทัดที่หักเป็นของผู้ถูกหักรายบรรทัดรายเดียว ⇒ ยกขึ้นหัวเอกสารตอนบันทึก (ตัวเดียวกับเปลี่ยนผู้ถูกหัก)"),
    dict(file=DOC, method="UpdateDocumentAsync",
         must=["LiftStoredLineWhtPayees(doc, updHeaderChanged)"],
         why=_WHT204_ADV + "P1: แก้ไขโดยไม่ส่งบรรทัดก็ยกผู้ถูกหักรายบรรทัดรายเดียวขึ้นหัวเอกสาร"),
    dict(file=DOC, method="ChangeWhtPayeeAsync",
         must=["WhtPayeeSplit.LiftSinglePayee(", "WhtPayeeChangePlan.AdjustmentNotesReason(", "WhtPayeeChangePlan.AffectedPeriods(",
               "PeriodFiledReasonAsync(_db, companyId, touchedForms, periods)"],
         forbid=["taxDate.Year, taxDate.Month)"],
         why=_WHT204_ADV + "P1 ยกผู้ถูกหัก · P2a ใบลด/เพิ่มหนี้อนุมัติแล้ว = 409 · P3-4 ตรวจยื่นแล้วทุกเดือนที่จ่าย/มี 50 ทวิ"),
    dict(file=DOC, method="WhtPayeePortionsWithLinesAsync",
         must=["WhtPayeeSplit.UnmappedAdjustmentLineReason(", "SharedUnmappedPayableCodeAsync(", "FlagReviewAsync(",
               "WhtPayeeSplit.UnmappedSharedAccountReview("],
         # รอบ 204 ทีม D (#10 · คำสั่งเจ้าของ 2026-10-13 ไม่ล็อกตาย): ผังต่างกัน ⇒ ยังล้มดัง · ผังเดียวกัน ⇒ ลงบัญชีได้ + ป้ายต้องตรวจบนใบต้นทาง
         must_re=[r"is\s+not\s+string\s+sharedCode\s*\)\s*throw\s+new\s+BusinessRuleException\(\s*unmapped\b"],
         before=[("SharedUnmappedPayableCodeAsync(", "FlagReviewAsync(")],
         why=_WHT204_ADV + "P2c: ใบลด/เพิ่มหนี้ของใบหลายผู้ถูกหักที่ไม่อ้างบรรทัดต้นทาง = ล้มดังเมื่อผังต่างกัน (ไม่ตกผังหัวเอกสารเงียบ) · "
             "ทีม D #10: ผังเดียวกัน ⇒ ไม่บล็อกแต่ติดป้าย \"50 ทวิ ต้องตรวจ\" บนใบต้นทาง (ไม่ปรับ 50 ทวิ เงียบ)"),
    dict(file=DOC, method="ApproveDocumentAsync#2",
         must=["WhtPayeePortionsAsync(companyId, doc)"],
         before=[("WhtPayeePortionsAsync(companyId, doc)", "AutoPostToJournalAsync(companyId, doc, approvedBy)")],
         why=_WHT204_ADV + "P2c: ตรวจตอนอนุมัติก่อนลง JE (รวมโหมดลง gross ที่ไม่เรียกตัวแบ่งผัง)"),
    dict(file=DOC, method="VoidDocumentAsync",
         must=["WhtCertReissuePlan.CreditNoteVoidedReason(", "FlagReviewAsync(companyId, cnSrc"],
         why=_WHT204_ADV + "P2b: ยกเลิกใบลดหนี้ที่ลดภาษีหักแล้ว ⇒ ป้าย \"50 ทวิ ต้องตรวจ\" บนใบต้นทาง (ไม่เงียบ)"),
    dict(file=DOC, method="ConvertCoreAsync",
         must=["WhtPayeeSplit.InheritPayee(s.Line.WhtPayeeContactId", "WhtPayeeSplit.InheritPayee(source.WhtPayeeContactId",
               "ActiveContactIdsAsync(", "WhtPayeeSplit.MarkDroppedInheritedPayees(created.InternalNotes", "WhtPayeeSplit.DroppedInheritedWhtPayees("],
         forbid=["created.WhtPayeeContactId =", "AppendInternalNote(created, droppedNote)"],
         why=_WHT204_ADV + "P3-6: ผู้ถูกหักที่ผู้ติดต่อถูกลบสืบทอดเป็น null + ป้าย · P1: หัวเอกสารส่งตั้งแต่ create (ห้ามตั้งทับผลยกผู้ถูกหัก)"),
    dict(file=DOC, method="CollectApprovalWarningsAsync",
         must=["WhtPayeeSplit.EffectiveSinglePayee(doc)"],
         why=_WHT204_ADV + "P1: คำเตือนใบปน ภ.ง.ด.2 ของใบรายเดียวใช้ผู้ถูกหักที่มีผลจริง (ตัวเดียวกับ LoadWhtPayeeAsync)"),
    dict(file=CLONE, method="Preview",
         must=["WhtPayeeSplit.InheritPayee(", "ActiveContactIdsAsync("],
         why=_WHT204_ADV + "P3-6: พรีวิวคัดลอกไม่พรีฟิลผู้ถูกหักที่ถูกลบ (ตัวสืบทอดเดียวกับ POST)"),
    dict(file=CLONE, method="Clone",
         must=["WhtPayeeSplit.InheritPayee(l.WhtPayeeContactId", "WhtPayeeSplit.InheritPayee(src.WhtPayeeContactId",
               "ActiveContactIdsAsync(", "WhtPayeeSplit.DroppedInheritedPayeeNote(", "WhtPayeeSplit.MarkDroppedInheritedPayees(cloned.InternalNotes",
               "WhtPayeeSplit.DroppedInheritedWhtPayees("],
         why=_WHT204_ADV + "P3-6: คัดลอกไม่ล้มเพราะผู้ถูกหักถูกลบ — สืบทอดเป็น null + ป้ายบนใบใหม่"),
    dict(file="Services/Implementations/RecurringTransactionService.cs", method="CreateDocumentFromTemplateAsync",
         must=["WhtPayeeSplit.InheritPayee(TplPayee(line)", "WhtPayeeSplit.InheritPayee(TplPayee(root)", "ActiveContactIdsAsync(",
               "WhtPayeeSplit.MarkDroppedInheritedPayees(droppedDoc.InternalNotes", "WhtPayeeSplit.DroppedInheritedWhtPayees(",
               "WhtPayeeSplit.DroppedPayeeAutoApproveSkipReason(droppedPayees)"],
         must_re=[r"recurring\.AutoApprove\s*&&\s*result\s*!=\s*null\s*&&\s*droppedPayees\s*==\s*0"],
         why=_WHT204_ADV + "P3-6: รายการประจำไม่ล้มทุกงวด — ผู้ถูกหักที่ถูกลบ = null + ป้าย + ไม่อนุมัติอัตโนมัติใบนั้น"),
    dict(file="Helpers/ContactHydration.cs", method="WhtPayeeOf",
         must=["WhtPayeeSplit.EffectiveSinglePayee(doc)"],
         forbid=["WhtCertIssuePlan.PayeeOf(doc.ContactId, doc.WhtPayeeContactId)"],
         why=_WHT204_ADV + "P1: ตัวโหลดผู้ถูกหักของรายงาน/ลิสต์ = ผู้ถูกหักที่มีผลจริง (ตรงกับ JE)"),
    dict(file="Helpers/ContactHydration.cs", method="LoadWhtPayeeAsync",
         must=["WhtPayeeSplit.EffectiveSinglePayee(doc)"],
         forbid=["WhtCertIssuePlan.PayeeOf(doc.ContactId, doc.WhtPayeeContactId)"],
         why=_WHT204_ADV + "P1: พรีวิว GL/คำเตือน/JE รายเดียวโหลดผู้ถูกหักที่มีผลจริง"),
    dict(file="Services/Implementations/StatutoryRemittanceService.cs", method="GetDashboardAsync",
         must=["WhtPayeeSplit.EffectiveSinglePayee("],
         forbid=["d.WhtPayeeContactId is Guid gp"],
         why=_WHT204_ADV + "P1: ช่องโหว่หน้านำส่งของใบรายเดียวแบ่งแบบจากผู้ถูกหักที่มีผลจริง"),
    dict(file="Services/Implementations/StatutoryRemittanceService.cs", method="GetFilingCalendarAsync",
         must=["WhtPayeeSplit.EffectiveSinglePayee("],
         forbid=["d.WhtPayeeContactId is Guid cp"],
         why=_WHT204_ADV + "P1: ปฏิทินยื่นแบบตัวเดียวกับหน้านำส่ง"),
    dict(file="Services/Implementations/DataIntegrityReportService.cs", method="LoadPnd2SuspectsAsync",
         must=["WhtPayeeSplit.EffectiveSinglePayee("],
         forbid=["WhtCertIssuePlan.PayeeOf(d.ContactId, d.WhtPayeeContactId)"],
         why=_WHT204_ADV + "P1: หน้าตรวจข้อมูลใช้ผู้ถูกหักที่มีผลจริง"),
    dict(file=_CERTSVC, method="AutoGenerateFromDocumentAsync",
         must=["NextCertificateNumberAsync("],
         must_re=[r"askedPayee\s*!=\s*payeeId\s*\)\s*throw\b"],
         forbid=["?? WhtCertIssuePlan.PayeeOf(doc.ContactId, doc.WhtPayeeContactId)", "CertificateNumber.StartsWith(", ".Max() + 1"],
         why=_WHT204_ADV + "P1 ผู้ถูกหักที่มีผลจริง · P3-7 ระบุผู้ถูกหักไม่ตรงใบรายเดียว = 400 · P3-2 ตัวออกเลขเดียว"),
    dict(file=_CERTSVC, method="CreateAsync",
         must=["NextCertificateNumberAsync("],
         forbid=["CertificateNumber.StartsWith(", ".Max() + 1"],
         why=_WHT204_ADV + "P3-2: เลขที่ 50 ทวิ ผ่านตัวออกเลขเดียว (ห้ามสำเนา MAX+1)"),
    dict(file=_CERTSVC, method="ReissueCertWithSavepointAsync",
         must=["NextCertificateNumberAsync(", "WhtCertReissuePlan.ReplacementStatus(oldStatus)"],
         before=[("var oldStatus = oldCert.Status", "oldCert.Status = WithholdingTaxCertStatus.Voided")],
         forbid=["oldCert.Status == WithholdingTaxCertStatus.Draft"],
         why=_WHT204_ADV + "P3-1: ใบแทนของใบร่างเป็นร่าง (จำสถานะก่อนประทับยกเลิก)"),
    dict(file=_CERTSVC, method="AdjustCertsForCreditNoteAsync",
         must=["WhtPayeeSplit.EffectiveSinglePayee("],
         forbid=["WhtCertIssuePlan.PayeeOf(src.ContactId, src.WhtPayeeContactId)"],
         why=_WHT204_ADV + "P1: ใบลดหนี้ของใบรายเดียวจับคู่ผู้ถูกหักที่มีผลจริง"),
    dict(file=_CERTSVC, method="GetPendingDocumentsAsync",
         must=["WhtPayeeSplit.EffectiveSinglePayee(d)"],
         forbid=["PayeeContactId: WhtCertIssuePlan.PayeeOf("],
         why=_WHT204_ADV + "P1: ลิสต์รอออกใบบอกผู้ถูกหักที่มีผลจริง"),
]

# รอบ 204 — ฝ่ายค้านรอบสองของงานหลายผู้ถูกหัก (หลัง 0f428969): (1) ผู้ถูกหักที่สืบทอดถูกตัดทิ้ง = ป้าย "ต้องตรวจ" ที่ Decide/คำเตือนอนุมัติอ่าน ·
# (2) เปลี่ยนผู้ถูกหักแล้วใบแทนของใบร่างยังเป็นร่าง · (3) ทางไปต่อของใบลดหนี้ที่ไม่อ้างบรรทัดต้องทำได้จริง · (4) หัวเอกสารที่ระบุเองขัดกับรายบรรทัด = 400 ·
# (5) รายการประจำที่ไม่อนุมัติอัตโนมัติเดินเส้นรายงานเดียวกับการอนุมัติล้ม
_WHT204_ADV2 = "รอบ 204 ฝ่ายค้านรอบสองหลายผู้ถูกหัก: "
RULES += [
    dict(file="Controllers/DocumentCloneController.cs", method="Clone",
         must=["WhtPayeeSplit.InheritPendingDroppedReview(cloned.InternalNotes, src.InternalNotes"],
         why=_WHT204_ADV2 + "ฝ่ายค้านรอบสาม: ป้ายผู้ถูกหักถูกตัดทิ้งที่ยังไม่รับทราบตามไปใบที่คัดลอก (มิฉะนั้นเดือนถัดไป 50 ทวิ ออกในนามโบรกเกอร์อัตโนมัติ)"),
    dict(file=DOCSVC, method="ConvertCoreAsync",
         must=["WhtPayeeSplit.InheritPendingDroppedReview(created.InternalNotes, source.InternalNotes"],
         why=_WHT204_ADV2 + "ฝ่ายค้านรอบสาม: ป้ายผู้ถูกหักถูกตัดทิ้งตามไปใบลูกที่แปลง (PI ติดป้าย → PV)"),
    dict(file=DOCSVC, method="UpdateDocumentAsync",
         must=["WhtPayeeSplit.ChangedHeaderRequest(doc.ContactId, request.WhtPayeeContactId, doc.WhtPayeeContactId)", "LiftStoredLineWhtPayees(doc, updHeaderChanged)"],
         forbid=["LiftStoredLineWhtPayees(doc, request.WhtPayeeContactId)"],
         why=_WHT204_ADV2 + "ฝ่ายค้านรอบสาม: ฟอร์มส่งหัวเอกสารค่าเดิมกลับทุกครั้ง — ด่านขัดกับบรรทัดดูเฉพาะค่าที่เปลี่ยนจริง"),
    dict(file=DOCSVC, method="ChangeWhtPayeeAsync",
         must=["WhtPayeeSplit.ChangedHeaderRequest(doc.ContactId, request.WhtPayeeContactId, doc.WhtPayeeContactId)"],
         why=_WHT204_ADV2 + "ฝ่ายค้านรอบสาม: โมดัลเปลี่ยนผู้ถูกหักส่งหัวเอกสารเดิมกลับ — ไม่ถือว่าระบุใหม่"),
    dict(file=_CERTSVC, method="TryAutoIssueCoreAsync",
         must=["PayeeReviewPending: WhtPayeeSplit.PendingDroppedPayeeReview(doc.InternalNotes)"],
         why=_WHT204_ADV2 + "(1) ใบที่ผู้ถูกหักสืบทอดถูกตัดทิ้งไม่ออก 50 ทวิ อัตโนมัติจนกว่าจะรับทราบ (ตัวตัดสินเดียว Decide)"),
    dict(file=DOC, method="CollectApprovalWarningsAsync",
         must=["WhtPayeeSplit.DroppedPayeeApprovalWarning(doc.InternalNotes)"],
         must_re=[r"DroppedPayeeApprovalWarning\(doc\.InternalNotes\)\s+is\s+string\s+droppedPayeeWarn\s*\)\s*warnings\.Add\(droppedPayeeWarn\)"],
         why=_WHT204_ADV2 + "(1) คำเตือนก่อนอนุมัติ [WHT-PAYEE] ของใบที่ผู้ถูกหักสืบทอดถูกตัดทิ้ง"),
    # รอบ 204 ทีม D (#7): ตัวตัดสินเดิม (ExplicitHeaderConflict) ห่อเป็นข้อผิดพลาดรหัสแยก RD-50BIS-PAYEE-HEADER-CONFLICT + {headerPayeeId, linePayeeId}
    dict(file=DOC, method="ResolveLineWhtPayeesAsync",
         must=["WhtPayeeSplit.HeaderConflictException(explicitHeaderRequest"],
         must_re=[r"is\s+\{\s*\}\s+headerConflict\s*\)\s*throw\s+headerConflict\b"],
         before=[("WhtPayeeSplit.HeaderConflictException(", "WhtPayeeSplit.LiftSinglePayee(")],
         why=_WHT204_ADV2 + "(4) หัวเอกสารที่ระบุเองขัดกับผู้ถูกหักรายบรรทัดรายเดียว = 400 ก่อนยก (ไม่ทับเงียบ)"),
    dict(file=DOC, method="LiftStoredLineWhtPayees",
         must=["WhtPayeeSplit.HeaderConflictException(explicitHeaderRequest"],
         must_re=[r"is\s+\{\s*\}\s+headerConflict\s*\)\s*throw\s+headerConflict\b"],
         before=[("WhtPayeeSplit.HeaderConflictException(", "WhtPayeeSplit.LiftSinglePayee(")],
         why=_WHT204_ADV2 + "(4) แก้ไขไม่ส่งบรรทัด — หัวเอกสารที่ระบุขัดกับบรรทัดที่บันทึกไว้ = 400"),
    dict(file=DOC, method="CreateDocumentAsync",
         call_args=[("ResolveLineWhtPayeesAsync(", "request.WhtPayeeContactId")],
         why=_WHT204_ADV2 + "(4) ตอนสร้างส่งผู้ถูกหักหัวเอกสารที่ผู้เรียกระบุเข้าด่านขัดกับรายบรรทัด"),
    dict(file=DOC, method="UpdateDocumentAsync",
         call_args=[("ResolveLineWhtPayeesAsync(", "updHeaderChanged")],
         why=_WHT204_ADV2 + "(4) ตอนแก้ไขส่งผู้ถูกหักหัวเอกสารที่ผู้เรียกระบุเข้าด่านขัดกับรายบรรทัด"),
    dict(file=DOC, method="ChangeWhtPayeeAsync",
         must=["WhtPayeeSplit.HeaderConflictException(", "WhtCertReissuePlan.ReplacementIssuesAsIssued(",
               "issueAsIssued: r.IssueAsIssued"],
         must_re=[r"is\s+\{\s*\}\s+headerConflict\s*\)\s*throw\s+headerConflict\b"],
         before=[("WhtCertReissuePlan.ReplacementIssuesAsIssued(", "c.Status = WithholdingTaxCertStatus.Voided")],
         forbid_lit=['Status = "Issued"'],
         why=_WHT204_ADV2 + "(2) ใบแทนของใบร่างเป็นร่าง (สถานะก่อนประทับยกเลิก) · (4) หัวเอกสารขัดกับรายบรรทัด = 400"),
    dict(file=DOC, method="WhtPayeePortionsWithLinesAsync",
         must=["GetValidConversionTargets(src.DocumentType).Contains(doc.DocumentType)"],
         call_args=[("WhtPayeeSplit.UnmappedAdjustmentLineReason(", "srcConvertsToNote")],
         why=_WHT204_ADV2 + "(3) ทางไปต่อ \"แปลง\" แนะเฉพาะเมื่อใบต้นทางแปลงเป็นใบลด/เพิ่มหนี้ได้จริง"),
    dict(file="Services/Implementations/RecurringTransactionService.cs", method="CreateDocumentFromTemplateAsync",
         must=["RecordAutoApproveFailureAsync(recurring.CompanyId, result.Id"],
         must_re=[r"if\s*\(recurring\.AutoApprove\)\s*await\s+_documentService\.RecordAutoApproveFailureAsync\("],
         forbid_lit=["ผู้ถูกหักภาษีของเทมเพลต {Count} รายการถูกลบ"],
         why=_WHT204_ADV2 + "(5) ไม่อนุมัติอัตโนมัติเพราะผู้ถูกหักถูกตัดทิ้ง = เส้นรายงานเดียวกับการอนุมัติอัตโนมัติล้ม (ไม่ใช่ LogWarning)"),
]

# กระดานตัดสิน 2026-10-13 ทีม C — A9: 50 ทวิ รายปีของพนักงาน (ภ.ง.ด.1ก) พิมพ์ "เงินที่จ่ายเข้า" ประกันสังคม/สำรองเลี้ยงชีพ (เดิมว่างเสมอ)
# · ยอดจาก PayrollDetail ชุดเดียวกับยอดเงินได้ · ตัวตัดสิน/ตัวจัดรูปแบบอยู่ที่ WhtCertFormText ที่เดียว · สอง renderer รับพารามิเตอร์เดียวกัน
# (เทสต์ WhtCertFormTextTests ล็อก helper + HTML ทั้งสองทิศ — กติกาข้างล่างล็อกจุดเรียกที่เทสต์ helper มองไม่เห็น)
_TEAMC_1013 = "กระดาน 2026-10-13 ทีม C: "
RULES += [
    dict(file="Services/Implementations/PayrollService.cs", method="GenerateAnnualEmployeeWhtCertsAsync",
         must=["WhtCertFormText.EmployeeAnnualFunds(ssoEmployeeTotal, pvdEmployeeTotal)",
               "BuildEmployeeAnnualCertPdfAsync(companyId, cert, funds)"],
         must_re=[r"ssoEmployeeTotal\s*=\s*g\.Sum\(d\s*=>\s*d\.SocialSecurityEmployee\)",
                  r"pvdEmployeeTotal\s*=\s*g\.Sum\(d\s*=>\s*d\.ProvidentFundEmployee\)"],
         before=[("WhtCertFormText.EmployeeAnnualFunds(", "BuildEmployeeAnnualCertPdfAsync(")],
         why=_TEAMC_1013 + "A9 ใบรายปีของพนักงานส่งยอดกองทุนส่วนลูกจ้างทั้งปีให้ renderer (เดิมช่องว่างเสมอ) — ตัดสินผ่าน EmployeeAnnualFunds ที่เดียว"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildEmployeeAnnualCertPdfAsync",
         must=["BuildWhtCertPdf(inMemoryCert, company, sigBytes, sigName, funds, stamp)"],
         why=_TEAMC_1013 + "A9 ยอดกองทุนไหลถึง QuestPDF (ไม่ทิ้งพารามิเตอร์กลางทาง)"),
    dict(file="Services/Implementations/PdfGenerationService.WhtCert.cs", method="BuildWhtCertPdf",
         must=["BuildFundLine(form, funds)"],
         why=_TEAMC_1013 + "A9 QuestPDF ส่งยอดกองทุนถึงบรรทัด \"เงินที่จ่ายเข้า\""),
    dict(file="Services/Implementations/PdfGenerationService.WhtCert.cs", method="BuildFundLine",
         must=["WhtCertFormText.FundAmountText(", "funds?.Gov", "funds?.Sso", "funds?.Pvd"],
         forbid=["ToString("],
         why=_TEAMC_1013 + "A9 QuestPDF จัดรูปแบบยอดกองทุนผ่านตัวเดียวกับ HTML (สอง renderer ห้าม drift)"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildWithholdingTaxCertHtml",
         must=["WhtCertFormText.FundAmountText("],
         must_lit=["{FundCell(funds?.Gov)}", "{FundCell(funds?.Sso)}", "{FundCell(funds?.Pvd)}"],
         why=_TEAMC_1013 + "A9 HTML รับยอดกองทุนพารามิเตอร์เดียวกับ QuestPDF · จัดรูปแบบผ่านตัวเดียว"),
    # A12: ปฏิทินภาษีสร้าง ภ.ง.ด.2/2ก · 54 · ภ.พ.36 เฉพาะบริษัทที่มีกิจกรรม (เดิมทุกบริษัท ⇒ เลยกำหนด/เตือนปลอม) · ถอนแถวที่ไม่มีใครแตะตอนสร้างใหม่
    # (TaxCalendarSeedScopeTests ล็อก helper ทั้งสองทิศ — กติกาข้างล่างล็อกว่าทั้งสองลูปและการถอนเรียกตัวตัดสินจริง)
    dict(file="Services/Implementations/TaxCalendarService.cs", method="InitializeYearAsync",
         must=["LoadSeedActivityAsync(companyId, year)", "TaxCalendarSeedScope.ShouldRetract(row, activity)"],
         must_re=[r"(?s)foreach \(var \(code, name, remitKey\) in monthlyForms\)\s*\{\s*if \(!TaxCalendarSeedScope\.ShouldSeed\(code, activity\)\) continue;",
                  r"(?s)foreach \(var \(code, name, dueDate, eFilingExtra\) in annualForms\)\s*\{\s*if \(!TaxCalendarSeedScope\.ShouldSeed\(code, activity\)\) continue;",
                  r"retracted\s*>\s*0\)\s*await _db\.SaveChangesAsync\(\)"],
         before=[("LoadSeedActivityAsync(companyId, year)", "monthlyForms")],
         forbid=["_db.Set<TaxCalendarEvent>().Remove("],
         why=_TEAMC_1013 + "A12 แบบผูกกิจกรรมสร้างผ่าน TaxCalendarSeedScope ทั้งรายเดือน/รายปี · ถอนแบบ soft-delete เฉพาะแถวที่ไม่มีใครแตะ"),
    dict(file="Services/Implementations/TaxCalendarService.cs", method="LoadSeedActivityAsync",
         must=["TaxCalendarSeedScope.ActivityYears(year)", "WithholdingTaxCertStatus.Voided", "DocumentStatus.Voided", "TaxType.WithholdingTax2",
               "TaxType.WithholdingTax54", "d.IsForeignService"],
         must_re=[r"w\.CompanyId\s*==\s*companyId", r"d\.CompanyId\s*==\s*companyId(?:.|\n)*d\.CompanyId\s*==\s*companyId"],
         why=_TEAMC_1013 + "A12 กิจกรรมโหลดแบบ tenant-scoped ทุก query · ใบ/เอกสารที่ยกเลิกไม่นับ"),
    dict(file="Services/Implementations/TaxCalendarService.cs", method="LoadSeedActivityAsync",
         must=["WhtPayableAccount.Pnd2Code", "WhtPayableAccount.Pnd54Code", "glPayableCodes.Contains(WhtPayableAccount.Pnd2Code)",
               "glPayableCodes.Contains(WhtPayableAccount.Pnd54Code)", "JournalEntryStatus.Posted", "l.JournalEntry.CompanyId == companyId"],
         why=_TEAMC_1013 + "ฝ่ายค้าน: ภาระ ภ.ง.ด.2/54 ที่อยู่ใน GL (ยังไม่มี 50 ทวิ) ต้องนับเป็นกิจกรรม — มิฉะนั้นปฏิทินถอนแถวเตือนของภาระจริง"),
    # B4: ใบกำกับอย่างย่อใน ETDA = T05/T06 (T04 = ใบส่งของ/ใบกำกับภาษีเต็มรูป — EtaxPdfXmlExtractor.MapTypeCodeToInternal) · ข้อความถึงผู้ใช้ห้ามกลับไปเรียก T04
    dict(file="Services/Implementations/EtaxInvoiceService.cs", method="GenerateAsync",
         must_lit=["ใบกำกับภาษีอย่างย่อ T05/T06"], forbid_lit=["อย่างย่อ T04"],
         why=_TEAMC_1013 + "B4 ข้อความ \"ยังไม่รองรับ\" อ้างรหัส ETDA ของใบกำกับอย่างย่อให้ถูก (T05/T06 ไม่ใช่ T04)"),
]

# ══════ ทีม A กระดานตัดสิน 2026-10-13 (A1 ป้ายผู้ถูกหักถูกตัดล้างเอง · A4 ผู้ถูกหักรายบรรทัดต่างประเทศ · A11 รหัสเงินได้เก่าไม่ขังใบ) ══════
_TEAM_A_1013 = "ทีม A กระดานตัดสิน 2026-10-13: "
RULES += [
    dict(file=DOC, method="UpdateDocumentAsync",
         must=["WhtPayeeSplit.ClearDroppedPayeeReview(", "WhtPayeeSplit.DroppedPayeeResolvedRuleCode"],
         must_re=[r"is\s+string\s+resolvedDropReason\s*\)\s*\{\s*doc\.InternalNotes\s*=\s*updReviewNotes\s*;\s*_db\.AddChainedAuditLog\("],
         before=[("updPayeesBefore =", "LiftStoredLineWhtPayees("),
                 ("updPayeesBefore =", "RemoveRange(doc.Lines)"),
                 ("WhtPayeeSplit.ClearDroppedPayeeReview(", "SaveChangesAsync(")],
         forbid=["WhtCertReviewNote.Clear("],
         call_args=[("ValidateDocumentLinesAsync(", "storedIncomeCodes"),
                    ("DocumentLineIncomeType.Decide(", "storedIncomeCodes")],
         why=_TEAM_A_1013 + "(A1) ชุดผู้ถูกหักก่อนแก้อ่านก่อนแตะหัว/บรรทัด · ล้างเฉพาะป้ายผู้ถูกหักถูกตัด (ห้ามล้างทุกป้าย) + audit ก่อนบันทึก · "
             + "(A11) รหัสเงินได้ที่เก็บบนใบนี้ส่งกลับตรงตัวได้ทั้งด่านตรวจและตอนสร้างบรรทัด"),
    dict(file=DOC, method="ChangeWhtPayeeAsync",
         must=["WhtPayeeSplit.ClearDroppedPayeeReview(doc.InternalNotes, oldPortions, newPortions)",
               "WhtPayeeSplit.DroppedPayeeResolvedRuleCode"],
         must_re=[r"doc\.InternalNotes\s*=\s*WhtCertAutoFailedNote\.Clear\(\s*chgReviewNotes\s*\)",
                  r"is\s+string\s+chgResolvedReason\s*\)\s*_db\.AddChainedAuditLog\("],
         before=[("WhtPayeeSplit.ClearDroppedPayeeReview(", "tx.CommitAsync(")],
         forbid=["WhtCertReviewNote.Clear("],
         why=_TEAM_A_1013 + "(A1) เปลี่ยนผู้ถูกหักหลังอนุมัติ ⇒ ล้างเฉพาะป้ายผู้ถูกหักถูกตัดในธุรกรรมเดียวกัน + audit (ป้ายเรื่องอื่นต้องรับทราบเอง)"),
    dict(file=DOC, method="CollectApprovalWarningsAsync",
         must=["ForeignWhtPayeeCheck.ScopeOf(lp.TaxId, lp.ContactType, lp.Name)",
               "ForeignWhtRateResolver.ResolveForIncomeCode(lpLine.IncomeTypeCode, lp.CountryCode"],
         must_re=[r"is\s+string\s+lpForeignWarn\s*\)\s*warnings\.Add\(",
                  r"doc\.Lines\.Where\(\s*l\s*=>\s*l\.WhtPayeeContactId\s*==\s*null\s*&&"],
         why=_TEAM_A_1013 + "(A4) ผู้ถูกหักรายบรรทัดต่างประเทศตรวจ ม.70 กับรายนั้น · ด่าน ม.70 ของหัวเอกสารตรวจเฉพาะบรรทัดที่ตามหัวเอกสาร"),
    dict(file=DOC, method="ValidateDocumentLinesAsync",
         call_args=[("DocumentLineIncomeType.Decide(", "storedIncomeCodes")],
         why=_TEAM_A_1013 + "(A11) ด่านรหัสเงินได้รับรหัสที่เก็บบนใบนี้ (ตัวตัดสินเดียว DocumentLineIncomeType.Decide)"),
]

# บอร์ดตัดสิน 2026-10-13 ทีม B — A3: ใบลดหนี้ซื้อมาก่อน 50 ทวิ ป้ายต้องบอกตัวเลข · A5b: ฐาน ภ.พ.36 ครอบ 50 ทวิ "ออกให้ครั้งเดียว"
_BOARD1013_B = "บอร์ด 2026-10-13 ทีม B: "
RULES += [
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="AdjustCertsForCreditNoteAsync",
         must=["WhtCreditNoteRemainder.Compute(", "WhtCreditNoteRemainder.BeforeAnyCertReason(prefix, figures)",
               "WhtCreditNoteRemainder.PartlyPaidReason(prefix, figures)", "WhtCreditNoteRemainder.Notice(src.DocumentNumber, figures)",
               "n.RelatedDocumentId == src.Id", "n.CompanyId == companyId", "n.Id != cn.Id", "certs.Sum(c => c.TotalTaxAmount)"],
         before=[("WhtCreditNoteRemainder.Compute(", "WhtCreditNoteRemainder.BeforeAnyCertReason(")],
         forbid_lit=["ตอนออกใบให้ลดยอดตามใบลดหนี้ (ระบบยังไม่หักให้เอง)", "ตรวจยอดบน 50 ทวิ ของงวดที่จ่ายแล้วกับงวดที่เหลือเอง"],
         why=_BOARD1013_B + "A3 ป้าย \"50 ทวิ ต้องตรวจ\" ของใบลดหนี้ก่อนออกใบ/จ่ายไม่ครบ บอกยอดที่ต้องกรอก (ใบเดิม − ใบลดหนี้ทุกใบที่มีผล − ที่ออกใบแล้ว) "
             "จากตัวคิดเดียว — ห้ามกลับไปให้ผู้ใช้คิดเอง"),
    dict(file="Services/Implementations/TaxService.cs", method="GeneratePp36Report",
         must=["WithholdingTaxCertType.PayOnce", "WithholdingTaxCertType.PayAlways",
               "ForeignServiceVat.BorneTaxOutsideLines(b.Type, serviceValue, doc.WithholdingTaxAmount, b.Income, b.Tax)"],
         why=_BOARD1013_B + "A5b รายงาน ภ.พ.36 นับส่วนที่ผู้จ่ายออกแทนของ 50 ทวิ \"ครั้งเดียว\" ด้วย — คิดต่อเงื่อนไขผ่านตัวตัดสินเดียว"),
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="IssueWarningsAsync",
         must=["WithholdingTaxCertType.PayOnce", "ForeignServiceVat.BorneTaxOutsideLines(cert.CertificateType,"],
         forbid=["cert.CertificateType == WithholdingTaxCertType.PayAlways &&"],
         why=_BOARD1013_B + "A5b คำเตือน \"VAT ขาด\" ตอนออก/แก้ 50 ทวิ \"ครั้งเดียว\" ใช้ตัวตัดสินเดียวกับรายงาน"),
]

# ทีม A (2a · 2026-10-13): ภาษีหัก ณ ที่จ่ายตอนบันทึกจ่าย = ยอดแนะนำหลังหักใบลดหนี้ซื้อ (Helpers/WhtPaymentSuggestion ตัวเดียว) ·
# ผู้ใช้ระบุเกินยอดที่เหลือ = รับ + คำเตือน + audit · เกินเพดานใบเดิม = บล็อก (ใบเดี่ยว) — ล็อกจุดเรียกทุกทางเข้าการจ่าย + endpoint + คำเตือนก่อนอนุมัติ
_TEAM_A_2A = "ทีม A 2a 2026-10-13: "
RULES += [
    dict(file=DOC, method="CreatePaymentAsync",
         must=["SuggestPaymentWhtAsync(", "WhtAlreadyWithheldAsync(companyId, doc.Id)",
               "WhtPaymentSuggestion.AboveNetWarning(whtSuggestion, paymentWht,", "cashBasis: await IsWhtCashBasisAsync(companyId)", "WhtPaymentSuggestion.AboveNetRuleCode",
               "WhtPaymentSuggestion.LegalReference", "AddChainedAuditLog("],
         must_re=[r"paymentWht\s*=\s*whtSuggestion\s*\.\s*Suggested\s*;",
                  r"paymentWht\s*>\s*whtSuggestion\s*\.\s*GrossCap\s*\+\s*0\.01m\s*\)\s*throw\b",
                  r"Warnings\s*:\s*whtAboveNetWarning\b"],
         before=[("SuggestPaymentWhtAsync(", "_db.Payments.Add(payment)")],
         forbid=["(request.Amount + settleNet) * doc.WithholdingTaxAmount", "paymentWht = remainingCap"],
         why=_TEAM_A_2A + "ไม่ส่งยอด = ยอดแนะนำหลังใบลดหนี้ (ห้ามกลับไปสูตรใบเดิมเต็ม) · ระบุเกินยอดที่เหลือ = คำเตือนในคำตอบ + audit · เกินเพดานใบเดิม = บล็อก"),
    dict(file=DOC, method="CreateMultiDocPaymentAsync",
         must=["SuggestPaymentWhtAsync(companyId, d, alreadyWht", "WhtPaymentSuggestion.AboveNetWarning(allocWht, whtSlice, d.DocumentNumber,", "cashBasis: await IsWhtCashBasisAsync(companyId)",
               "WhtPaymentSuggestion.AboveNetRuleCode"],
         must_re=[r"whtSlice\s*=\s*allocWht\s*\.\s*Suggested\s*;", r"Warnings\s*:\s*multiWhtWarnings\b"],
         forbid=["alloc.AllocatedAmount * d.WithholdingTaxAmount"],
         why=_TEAM_A_2A + "ชำระหลายใบใช้ตัวแนะนำเดียวกับใบเดี่ยว (ใบลดหนี้ซื้อถูกหักออก) · คำเตือนต่อใบในคำตอบ"),
    dict(file=DOC, method="SuggestPaymentWhtAsync",
         must=["PurchaseCreditNoteWhtAsync(companyId, doc)", "WhtPaymentSuggestion.Compute(", "ForeignServiceVat.PayeeAmount(doc)"],
         why=_TEAM_A_2A + "ชุดใบลดหนี้ + ตัวคิด + ตัวหาร (ยอดจ่ายผู้รับ) ตัวเดียวของทุกผู้เรียก"),
    dict(file=DOC, method="PurchaseCreditNoteWhtAsync",
         must=["n.CompanyId == companyId", "n.RelatedDocumentId == doc.Id", "DocumentType.CreditNote", "n.CnDnPurchaseSideOverride != false",
               "DocumentStatus.Voided", "DocumentStatusRules.NotIssued", "WhtCertIssuePlan.AppliesTo(doc.DocumentType)", "ForeignServiceVat.PayeeAmount("],
         why=_TEAM_A_2A + "ชุดใบลดหนี้เดียวกับป้าย \"50 ทวิ ต้องตรวจ\" (ออกเลขแล้ว · ไม่ยกเลิก · ฝั่งซื้อ · บริษัทเดียวกัน)"),
    dict(file=DOC, method="WhtAlreadyWithheldAsync",
         must=["p.CompanyId == companyId", "r.CompanyId == companyId", "DocumentStatus.Draft", "DocumentStatus.Voided", "DocumentStatus.Rejected"],
         why=_TEAM_A_2A + "ที่หักไปแล้ว = Payments + ใบเสร็จ/ใบสำคัญที่อ้างใบ (กติกาเดิมของใบเดี่ยว) · กรองบริษัท"),
    dict(file=DOC, method="GetPaymentWhtSuggestionAsync",
         must=["SuggestPaymentWhtAsync(", "WhtAlreadyWithheldAsync(companyId, doc.Id)", "WhtPaymentSuggestion.AboveNetWarning(r, en, cashBasis: await IsWhtCashBasisAsync(companyId))"],
         must_re=[r"d\s*\.\s*CompanyId\s*==\s*companyId"],
         why=_TEAM_A_2A + "endpoint หน้าบันทึกจ่ายคิดด้วยตัวเดียวกับตอนบันทึก (server computes · page displays)"),
    dict(file=DOC, method="CollectApprovalWarningsAsync",
         must=["WhtPaymentSuggestion.ApprovalWarning(", "PurchaseCreditNoteWhtAsync(companyId, whtNetSrc)", "excludeDocumentId: doc.Id"],
         must_re=[r"x\s*\.\s*CompanyId\s*==\s*companyId"],
         why=_TEAM_A_2A + "[WHT-CN-NET] ใบสำคัญจ่ายที่อ้างใบซื้อหักเกินยอดที่เหลือหลังใบลดหนี้ — เตือน ไม่บล็อก"),
    dict(file=DOC, method="GetDocumentAsync",
         must=["PurchaseCreditNoteWhtAsync(companyId, doc)", "WhtNetOfAdjustments"],
         why=_TEAM_A_2A + "ค่าแสดงผลภาษีหักหลังใบลดหนี้มาจากชุดใบลดหนี้เดียวกับยอดแนะนำ"),
    dict(file="Controllers/DocumentController.cs", method="GetPaymentWhtSuggestion",
         must=["DenySensitiveAsync(", "GetPaymentWhtSuggestionAsync("],
         before=[("DenySensitiveAsync(", "GetPaymentWhtSuggestionAsync(")],
         why=_TEAM_A_2A + "endpoint แนะนำยอดต้องผ่านด่านสิทธิ์ดูเอกสารก่อน"),
]

# กระดานตัดสิน 2026-10-13 (ส่งต่อจากทีม A): ด่าน "ปิดยอดแต่ WHT ขาด" เกณฑ์ Cash หักยอดใบลดหนี้ซื้อ — ตัวนับเดียวกับยอดแนะนำตอนจ่าย
RULES += [
    dict(file=DOC, method="ApplySourceDocumentAdjustmentsAsync",
         must=["PurchaseCreditNoteWhtAsync(companyId, source)", "var missing = requiredWht - whtTotal"],
         forbid=["var missing = source.WithholdingTaxAmount - whtTotal"],
         why="กระดาน 2026-10-13: PV ที่หักภาษีสุทธิหลังใบลดหนี้ (ตามคำเตือน [WHT-CN-NET]) ต้องไม่ถูกปฏิเสธด้วยยอด WHT เต็มของใบต้นทาง"),
]

# ── รอบ 204 ทีม C (คำสั่งเจ้าของ 2026-10-13 · WhtRoyaltyEWithholdingTests ล็อก helper — กติกาข้างล่างล็อกจุดเรียก) ──
# c2: ค่าสิทธิ ม.40(3) บุคคลธรรมดา ⇒ ภ.ง.ด.2 ตามค่าตั้งบริษัท (WhtRoyaltyPolicy) — ทุกทางที่ตัดสินแบบ/ผังต้องส่ง royaltyAsPnd2 (ไม่งั้น GL ≠ 50 ทวิ ≠ หน้านำส่ง)
# c3: e-Withholding (EWithholdingScope ตัวตัดสินเดียว) — JE 21923 · ไม่ออก 50 ทวิ อัตโนมัติ · ไม่อยู่ในยอดนำส่ง/ไฟล์ยื่น · ปฏิทินเตือนยอดค้าง
_TEAMC = "รอบ 204 ทีม C (ค่าสิทธิ ภ.ง.ด.2 / e-Withholding): "
_TAXFILE = "Services/Implementations/TaxFilingExportService.cs"
_TAXCAL = "Services/Implementations/TaxCalendarService.cs"
_REMIT = "Services/Implementations/StatutoryRemittanceService.cs"
RULES += [
    dict(file=DOCSVC, method="ResolveWhtPayableSplitAsync",
         must=["EWithholdingScope.IsViaAsync(_db, companyId, doc)", "WhtPayableAccount.EWithholdingCode",
               "ResolveRoyaltyPolicyAsync(companyId,", "await _db.LoadWithheldAsync(companyId, doc), royaltyAsPnd2)",
               "codesOf.GetValueOrDefault(payeeId) ?? Array.Empty<string?>(), royaltyAsPnd2)"],
         before=[("EWithholdingScope.IsViaAsync(", "WhtPayeePortionsWithLinesAsync(")],
         why=_TEAMC + "หนี้ WHT ของใบ e-Withholding ลง 21923 ทั้งก้อน · ค่าสิทธิบุคคลตามค่าตั้ง ⇒ 21915 ทั้งใบรายเดียวและต่อผู้ถูกหัก"),
    dict(file=DOCSVC, method="ResolveWhtPayableAccountAsync",
         must=["withheldIncomeTypeCodes, royaltyAsPnd2)"],
         why=_TEAMC + "ตัวเลือกผังส่งค่าตั้งค่าสิทธิเข้าลำดับผัง (WhtPayableAccount.CodeChain)"),
    dict(file=DOCSVC, method="ResolveRoyaltyPolicyAsync",
         must=["WhtRoyaltyPolicy.LoadAsync(_db, companyId)", "setting.AppliesTo(taxDate)"],
         why=_TEAMC + "ตัวโหลดเดียวของ DocumentService — ค่าตั้ง + วันเริ่มผ่าน WhtRoyaltyPolicy เท่านั้น"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildProjectedGlAsync",
         must=["EWithholdingScope.IsViaAsync(_db, companyId, doc)", "WhtPayableAccount.EWithholdingCode",
               "WhtRoyaltyPolicy.LoadAsync(_db, companyId)"],
         why=_TEAMC + "พรีวิว GL พูดตรงกับ JE จริง (21923 / 21915 ของค่าสิทธิ) — สอง renderer ห้าม drift"),
    dict(file=_CERTSVC, method="TryAutoIssueCoreAsync",
         must=["ViaEWithholding: await EWithholdingScope.IsViaAsync(_db, companyId, doc)"],
         before=[("EWithholdingScope.IsViaAsync(", "AutoGenerateFromDocumentAsync(")],
         why=_TEAMC + "ใบ e-Withholding ไม่ออก 50 ทวิ อัตโนมัติ (ธนาคารนำส่ง/กรมสรรพากรออกหลักฐาน) — ตัวตัดสินเดียว WhtCertIssuePlan"),
    dict(file=_CERTSVC, method="GetPendingDocumentsAsync",
         must=["EWithholdingScope.LoadDocIdsAsync(_db, companyId)", "viaEWithholding: eWhtDocIds.Contains(d.Id)"],
         why=_TEAMC + "ลิสต์รอออกใบบอกว่าใบ e-Withholding ไม่ต้องออก (ไม่ใช่ \"ขาด\") — server computes"),
    dict(file=_CERTSVC, method="ResolveWhtFormType",
         must=["requested != TaxType.WithholdingTax3", "IndividualForm(withheldIncomeTypeCodes, royaltyAsPnd2: true)"],
         why=_TEAMC + "ใบคีย์มือเลือก 2/3 เองได้สำหรับค่าสิทธิ (ไม่ล็อกแข็ง) · บังคับ ภ.ง.ด.2 เฉพาะ ม.40(4) ทั้งใบ"),
    dict(file=_CERTSVC, method="AutoGenerateFromDocumentAsync",
         must=["royaltyAsPnd2: (await WhtRoyaltyPolicy.LoadAsync(_db, companyId)).AppliesTo(WhtRoyaltyPolicy.PolicyDate(paymentDate, doc.PaymentDate, doc.DocumentDate))"],
         why=_TEAMC + "50 ทวิ อัตโนมัติลงแบบเดียวกับ JE (ค่าตั้ง ณ วันจ่ายของงวด — ตัวตัดสินวันเดียว PolicyDate)"),
    dict(file=_CERTSVC, method="CreateAsync",
         must=["WhtRoyaltyPolicy.LoadAsync(_db, companyId)", "CertRequestTaxDate(request)"],
         why=_TEAMC + "ใบคีย์มือ: ค่าตั้งค่าสิทธิ ณ วันจ่ายของบรรทัด"),
    dict(file=_CERTSVC, method="UpdateAsync",
         must=["WhtRoyaltyPolicy.LoadAsync(_db, companyId)", "CertRequestTaxDate(request)"],
         why=_TEAMC + "แก้ใบคีย์มือ: กติกาเดียวกับตอนสร้าง"),
    dict(file=_REMIT, method="GetDashboardAsync",
         must=["EWithholdingScope.LoadDocIdsAsync(_db, companyId)", "!eWhtDocIds.Contains(d.Id)", "!eWhtDocIds.Contains(cd)",
               "WhtRoyaltyPolicy.LoadAsync(_db, companyId)"],
         why=_TEAMC + "หน้านำส่ง: ใบ e-Withholding + 50 ทวิ ที่ออกเองให้ใบนั้น ไม่ใช่ภาระนำส่งของเรา (กันนำส่งซ้ำกับธนาคาร)"),
    dict(file=_REMIT, method="GetFilingCalendarAsync",
         must=["EWithholdingScope.LoadDocIdsAsync(_db, companyId)", "calEWhtDocIds.Contains(d.Id)", "!calEWhtDocIds.Contains(cd)",
               "WhtRoyaltyPolicy.LoadAsync(_db, companyId)"],
         why=_TEAMC + "ปฏิทินยื่นเล่าเรื่องเดียวกับหน้านำส่ง"),
    dict(file="Services/Implementations/TaxService.cs", method="GenerateWhtReport",
         must=["EWithholdingScope.LoadDocIdsAsync(_db, companyId)", "!eWhtDocIds.Contains(d.Id)", "IsExcluded = eWhtCert",
               "ResolveWhtFormType(c, null, docIsForeignService, withheldIncomeCodes, royaltyAsPnd2)", "WhtRoyaltyPolicy.LoadAsync(_db, companyId)"],
         why=_TEAMC + "รายงาน ภ.ง.ด.: ใบ e-Withholding ไม่ขึ้นแถวเตือน \"ยังไม่ออกใบ\" · 50 ทวิ ที่ออกเองให้ใบนั้นเป็นแถวข้อมูล (ไม่นับยอด) · ค่าสิทธิตามค่าตั้ง"),
    dict(file=_TAXFILE, method="ExportPnd3Async", must=["ExcludeEWithholdingAsync(companyId, certs)", "+ eWhtNote"],
         why=_TEAMC + "ไฟล์ ภ.ง.ด.3 ไม่รวมใบ e-Withholding — บอกในสรุป (ไม่หายเงียบ)"),
    dict(file=_TAXFILE, method="ExportPnd53Async", must=["ExcludeEWithholdingAsync(companyId, certs)", "+ eWhtNote"],
         why=_TEAMC + "ไฟล์ ภ.ง.ด.53 ไม่รวมใบ e-Withholding — บอกในสรุป"),
    dict(file=_TAXFILE, method="ExportPnd2Async", must=["ExcludeEWithholdingAsync(companyId, certs)", "+ eWhtNote"],
         why=_TEAMC + "ไฟล์ ภ.ง.ด.2 ไม่รวมใบ e-Withholding — บอกในสรุป"),
    dict(file=_TAXFILE, method="ExportPnd54Async", must=["ExcludeEWithholdingAsync(companyId, certs)", "+ eWhtNote"],
         why=_TEAMC + "ไฟล์ ภ.ง.ด.54 ไม่รวมใบ e-Withholding — บอกในสรุป"),
    dict(file=_TAXFILE, method="ExcludeEWithholdingAsync", must=["EWithholdingScope.LoadDocIdsAsync(_db, companyId)"],
         why=_TEAMC + "ตัวตัดสินเดียวของชุดใบ e-Withholding"),
    dict(file=_TAXCAL, method="EWithholdingReminderAsync",
         must=["l.JournalEntry.CompanyId == companyId", "WhtPayableAccount.EWithholdingCode", "EWithholdingClearing.Oldest(",
               "EWithholdingClearing.NeedsReminder("],
         why=_TEAMC + "แถวเตือนยอด 21923 ค้างเกิน 30 วัน — ของบริษัทนี้เท่านั้น · ตัดยอด FIFO ตัวเดียว"),
    dict(file=_TAXCAL, method="GetOverdueAsync", must=["EWithholdingReminderAsync(companyId)"],
         why=_TEAMC + "ยอด e-Withholding ค้างโผล่ในรายการเลยกำหนด"),
    dict(file=_TAXCAL, method="GetEventsAsync", must=["EWithholdingReminderAsync(companyId)"],
         why=_TEAMC + "ยอด e-Withholding ค้างโผล่ในปฏิทินเดือนปัจจุบัน"),
    dict(file=_TAXCAL, method="UpdateEventAsync", must=["EWithholdingClearing.ReminderEventId", "EWithholdingClearing.ReminderNotEditable"],
         why=_TEAMC + "แก้สถานะแถวคำนวณ = บอกทางไปต่อ (ห้าม silent no-op)"),
    dict(file="Services/Implementations/CompanyService.cs", method="UpdateAsync",
         must=["ApplyRoyaltyPolicyAsync(company, userId, request)"],
         why=_TEAMC + "ค่าตั้งค่าสิทธิ + วันเริ่ม คำนวณที่ตัวตัดสินเดียว (เปิดครั้งแรก = วันที่ 1 ของเดือน)"),
]

# กระดานตัดสิน 2026-10-13 ผู้รวมงาน: ทางเข้าซิงค์ระบบภายนอก + ตัวกระทบยอด GL ใช้ตัวตัดสินค่าสิทธิ/e-Withholding ตัวเดียวกับ DocumentService
RULES += [
    dict(file="Services/Implementations/IntegrationService.cs", method="CreatePaymentVoucherJournalAsync",
         must=["EWithholdingScope.IsViaAsync(_db, companyId, document)",
               "payRoyalty.AppliesTo(Accounting.Helpers.WhtRoyaltyPolicy.PolicyDate(null, document.PaymentDate, document.DocumentDate))"],
         forbid=["WhtRoyaltyPolicy.AppliesTo(payRoyalty.Form"],
         why="ใบสำคัญจ่ายจากระบบภายนอก: ค่าสิทธิ ม.40(3) ตามค่าตั้ง (21915) + e-Withholding (21923) — มิฉะนั้นลง 21916 ผิดผัง"),
    dict(file="Services/Implementations/TaxGlReconciliationService.cs", method="BuildWhtLineAsync",
         must=["EWithholdingScope.LoadDocIdsAsync(_db, companyId)", "!eWhtDocIds.Contains(d.Id)"],
         why="ใบหักผ่าน e-Withholding ไม่ต้องออก 50 ทวิ — ตัวกระทบยอดต้องไม่ฟ้องว่า \"ยังไม่มีใบ\""),
]

# ── ฝ่ายค้านรอบ 204 (ค่าตั้งค่าสิทธิ ม.40(3) — M2 ประวัติ · L1 ผังร่วม · L6 คำเตือน · L7b รายงาน) ──
# ค่าตั้งมีประวัติ (WhtRoyaltyPolicy.AppliesTo อ่านช่วงที่ปิดแล้ว) ⇒ ทุกทางต้องผ่าน PolicyDate ตัวเดียว · บันทึกค่าตั้งที่แตะงวดยื่นแล้ว = รับทราบ + audit
_ROYH = "ฝ่ายค้านรอบ 204 ค่าตั้งค่าสิทธิ: "
RULES += [
    dict(file=DOCSVC, method="ResolveWhtPayableSplitAsync",
         must=["WhtRoyaltyPolicy.PolicyDate(instalmentPaymentDate, doc.PaymentDate, doc.DocumentDate)"],
         forbid=["ResolveRoyaltyPolicyAsync(companyId, doc.PaymentDate ?? doc.DocumentDate)"],
         why=_ROYH + "JE รายงวด (เกณฑ์เงินสด) ใช้วันที่จ่ายของงวด = วันเดียวกับ 50 ทวิ ของงวด"),
    dict(file=DOCSVC, method="CreatePaymentJournalAsync",
         must=["ResolveWhtPayableSplitAsync(companyId, doc, thbWht, payment.PaymentDate)"],
         why=_ROYH + "หนี้ WHT ตอนบันทึกจ่ายรายงวดตัดสินค่าตั้งค่าสิทธิ ณ วันจ่ายของงวด (เดิมใช้วันเอกสาร — GL ≠ 50 ทวิ ของงวด)"),
    dict(file=DOCSVC, method="SharedUnmappedPayableCodeAsync",
         must=["EWithholdingScope.IsViaAsync(_db, companyId, note)", "WhtPayableAccount.EWithholdingCode",
               "c?.ContactType ?? ContactType.Individual, c?.Name, codes, royaltyAsPnd2)",
               "WhtRoyaltyPolicy.PolicyDate(null, note.PaymentDate, note.DocumentDate)"],
         why=_ROYH + "L1 — \"ผังเดียวกัน\" ของใบลด/เพิ่มหนี้ตัดสินด้วยลำดับผังชุดเดียวกับ JE (e-Withholding 21923 + ค่าสิทธิตามค่าตั้ง)"),
    dict(file=DOCSVC, method="ChangeWhtPayeeAsync",
         must=["WhtRoyaltyPolicy.PostedFlag(", "WhtRoyaltyPolicy.PolicyDate(null, doc.PaymentDate, doc.DocumentDate)"],
         forbid=["ResolveRoyaltyPolicyAsync(companyId, doc.PaymentDate ?? doc.DocumentDate)"],
         why=_ROYH + "M2 — ย้ายหนี้ตามค่าตั้ง ณ วันที่ลงบัญชีไว้ (ประวัติ) ไม่ใช่ค่าตั้งวันนี้ ⇒ ผู้ถูกหักที่แบบต่างเพราะค่าตั้งอย่างเดียวไม่ถูกย้ายผัง"),
    dict(file=DOCSVC, method="CollectApprovalWarningsAsync",
         must=["WhtRoyaltyPolicy.ApprovalWarning( await Accounting.Helpers.WhtRoyaltyPolicy.LoadAsync(_db, companyId),"],
         why=_ROYH + "L6 — คำเตือน [WHT-FORM] อ่านค่าตั้งทั้งชุด (ผู้ใช้ตัดสินแล้ว = เงียบ) ไม่ใช่แค่แบบ"),
    dict(file="Services/Implementations/CompanyService.cs", method="ApplyRoyaltyPolicyAsync",
         must=["WhtRoyaltyPolicy.ApplyUpdate(", "WhtPayeeChangePlan.FiledPeriodsFromAsync(", "WhtRoyaltyPolicy.TouchedFiledPeriods(",
               "request.AcknowledgeWhtRoyaltyFiledPeriods != true", "WhtRoyaltyPolicy.FiledPeriodRuleCode, 409",
               "company.WhtRoyaltyPnd2History = after.Pnd2History", "company.WhtRoyaltyFormDecidedAt = after.DecidedAt",
               "_db.AddChainedAuditLog("],
         before=[("WhtRoyaltyPolicy.TouchedFiledPeriods(", "company.WhtRoyaltyIndividualForm = after.Form")],
         why=_ROYH + "M2 — บันทึกค่าตั้งที่แตะงวด ภ.ง.ด.2/3 ที่ยื่นแล้ว ⇒ 409 ให้รับทราบก่อนเขียน (ไม่เงียบ · ไม่ล็อก) + audit"),
    dict(file="Services/Implementations/DataIntegrityReportService.cs", method="LoadPnd2SuspectsAsync",
         must=["royaltySetting.AppliesTo(Accounting.Helpers.WhtRoyaltyPolicy.PolicyDate(null, d.PaymentDate, d.DocumentDate))"],
         forbid=["(await Accounting.Helpers.WhtRoyaltyPolicy.LoadAsync(_db, companyId)).AppliesTo(d.DocumentDate)"],
         before=[("WhtRoyaltyPolicy.LoadAsync(_db, companyId)", "foreach (var d in docs)")],
         why=_ROYH + "L7b — โหลดค่าตั้งครั้งเดียวนอกลูป · วันตัดสินเดียวกับ JE (PolicyDate ไม่ใช่ DocumentDate)"),
    dict(file="Services/Implementations/TaxService.cs", method="GenerateWhtReport",
         must=["royalty.AppliesTo(Accounting.Helpers.WhtRoyaltyPolicy.PolicyDate(null, d.PaymentDate, d.DocumentDate))"],
         why=_ROYH + "รายงาน ภ.ง.ด. จัดแบบค่าสิทธิด้วยวันตัดสินเดียวกับ JE"),
]

# ── รอบ 204 ทีม D (คำสั่งเจ้าของ 2026-10-13 "ระบบห้ามล็อกตาย · ผู้ใช้ตัดสิน"): ล็อกเปลี่ยนเป็นทางเลือก แต่ด่านยังถูกคำนวณก่อนแยกทางเสมอ ·
#    WhtCertUserDecisionTests + Db/WhtUserDecisionDbTests + WhtDividendSubRowTests ล็อก helper/พฤติกรรม — กติกาข้างล่างล็อกจุดเรียก ──
_TEAM_D = "รอบ 204 ทีม D (ผู้ใช้ตัดสิน): "
_WHTSVC = "Services/Implementations/WithholdingTaxCertService.cs"
RULES += [
    dict(file=_WHTSVC, method="VoidAsync",
         must=["WhtFiledPeriodAck.AckProblem(", "WhtFiledPeriodAck.WayForward", "WhtFiledPeriodAck.VoidAckRuleCode", "AddChainedAuditLog(",
               "FlagReviewAsync("],
         before=[("WhtCertVoidGuard.CheckAsync(", "WhtFiledPeriodAck.AckProblem("),
                 ("WhtFiledPeriodAck.AckProblem(", "cert.Status = WithholdingTaxCertStatus.Voided")],
         why=_TEAM_D + "#23 ยกเลิก 50 ทวิ ที่ยื่นแล้ว: ด่านยื่นแล้วคำนวณก่อน · ไม่รับทราบ = 409 พร้อมทางไปต่อ · รับทราบ (Tax.File + เหตุผล) = ยกเลิก + ป้ายยื่นเพิ่มเติม + audit"),
    dict(file=_WHTSVC, method="AutoGenerateFromDocumentAsync",
         must=["WhtCertPayeeTaxIdDraft.DraftInstead(payeeProblem, manualRequest, autoIssue)", "WhtCertPayeeTaxIdDraft.WarningFor("],
         must_re=[r"payeeProblem\s*!=\s*null\s*&&\s*!draftForMissingTaxId\s*\)\s*throw\b"],
         why=_TEAM_D + "#20 ไม่มีเลขผู้เสียภาษี: คนกด \"สร้างใบ\" ได้ร่าง + คำเตือน + audit · ทางอัตโนมัติ/ออกทันทียังล้มดัง (ห้ามออกใบเข้าแบบยื่นโดยไม่มีเลข)"),
    dict(file=_WHTSVC, method="CreateAsync",
         must=["WhtCertFormCorrection.Notice(", "FormCorrectionNotice = formNotice", "ValidateDividendSubRows(request)",
               "WhtCertFormText.NormalizeDividendSubRow("],
         why=_TEAM_D + "#21 แบบ ภ.ง.ด. ที่ระบบแก้ต้องบอกผู้ใช้ (ไม่แก้เงียบ) · 2f แถวย่อยปันผลตรวจก่อนเก็บ"),
    dict(file=_WHTSVC, method="UpdateAsync",
         must=["WhtCertFormCorrection.Notice(", "FormCorrectionNotice = updFormNotice", "ValidateDividendSubRows(request)",
               "WhtCertFormText.NormalizeDividendSubRow("],
         why=_TEAM_D + "#21/2f ทางแก้ใบเดินด่านเดียวกับทางสร้าง"),
    dict(file=DOC, method="ChangeWhtPayeeAsync",
         must=["WhtFiledPeriodAck.AckProblem(", "WhtFiledPeriodAck.WayForward", "WhtFiledPeriodAck.PayeeChangeNeedsAckRuleCode",
               "WhtFiledPeriodAck.PayeeChangeAckRuleCode", "WhtPayeeChangePlan.FiledPeriodsAsync(", "request.AcknowledgeFiledPeriod"],
         before=[("WhtCertVoidGuard.CheckAsync(", "WhtFiledPeriodAck.AckProblem("),
                 ("WhtFiledPeriodAck.AckProblem(", "PostAsync(")],
         why=_TEAM_D + "#8c เปลี่ยนผู้ถูกหักแตะงวดยื่นแล้ว: ด่านคำนวณก่อน · ไม่รับทราบ = 409 · รับทราบ = ไปต่อ + ป้าย \"ต้องยื่น ภ.ง.ด.X เพิ่มเติม\" + audit"),
    dict(file="Controllers/DocumentController.cs", method="ChangeWhtPayee",
         must=["PermissionKeys.TaxFile", "holdsTaxFile"],
         why=_TEAM_D + "#8c รับทราบงวดยื่นแล้วต้องถือ Tax.File (เซิร์ฟเวอร์ตรวจ ไม่ใช่หน้าเว็บ)"),
    dict(file="Controllers/WithholdingTaxCertController.cs", method="Void",
         must=["PermissionKeys.TaxFile", "holdsTaxFile"],
         why=_TEAM_D + "#23 รับทราบใบที่ยื่นแล้วต้องถือ Tax.File"),
    dict(file="Controllers/WithholdingTaxCertController.cs", method="AutoGenerate",
         must=["manualRequest: true"],
         why=_TEAM_D + "#20 ทางคนกด \"สร้างใบ\" เท่านั้นที่ได้ร่างแทนการปฏิเสธ"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildWithholdingTaxCertHtml",
         must=["WhtCertFormText.DividendBlock(lines)"],
         why=_TEAM_D + "2f แถวย่อยปันผล 4(ข) จากตัวตั้งเดียว (ตัวเดียวกับ QuestPDF + หน้าพิมพ์)"),
    dict(file="Services/Implementations/PdfGenerationService.WhtCert.cs", method="BuildIncomeTable",
         must=["WhtCertFormText.DividendBlock(lines)"],
         why=_TEAM_D + "2f แถวย่อยปันผล 4(ข) จากตัวตั้งเดียว (ตัวเดียวกับ HTML + หน้าพิมพ์)"),
]

RULES += [
    dict(file=DOC, method="ChangeWhtPayeeAsync",
         must=["WhtRoyaltyPolicy.PostedFlag(chgRoyaltySetting,",
               "WhtIncomeCodes.WithheldAt(lines, portion.LineIndexes), chgRoyaltyAsPnd2)",
               "WhtIncomeCodes.WithheldAt(lines, newPortion.LineIndexes), chgRoyaltyAsPnd2)"],
         why="ผู้รวมงานกระดาน 2026-10-13: เปลี่ยนผู้ถูกหักใช้ค่าตั้งค่าสิทธิ ม.40(3) เดียวกับ JE — มิฉะนั้นย้ายผังไป 21916 ทั้งที่ JE เดิมลง 21915"),
]

# ฝ่ายค้านรอบสามของคำสั่งไม่ล็อคตายตัว (L-a/L-b/L-c)
RULES += [
    dict(file="Services/Settlement/SettlementPostingService.cs", method="BuildGateAsync",
         must=["WhtCertIssuePlan.PayeeTaxIdProblem(counterparty.Name, counterparty.TaxId"],
         forbid=["!string.IsNullOrWhiteSpace(counterparty?.TaxId)"],
         why="ด่านแผนรอบโอนใช้ตัวตรวจเลขผู้เสียภาษีเดียวกับตอนออก 50 ทวิ — เลขผิดรูปต้องหยุดก่อนลงบัญชี ไม่ใช่ล้มกลางทาง"),
    dict(file=DOC, method="ChangeWhtPayeeAsync",
         must=["await IsWhtCashBasisAsync(companyId)", "a.WithholdingTaxAmount > 0m"],
         why="เปลี่ยนผู้ถูกหัก: วันลงหนี้ WHT จริงรวมการจ่ายหลายใบ + ตัวตัดสินเกณฑ์เดียว"),
]

# รอบ 205 ทีม C (IMPORT_ETL_REVIEW §1 แถว 6–9): ตัวเขียน statement ธนาคารตัวเดียว — ทุกทางเข้าต้องลงผ่าน
# BankService.ImportParsedStatementRowsAsync (เดิม 4 ตัวเขียน · ยอดคงเหลือ 2 ความหมาย · open-banking OFX ตอบ "ใหม่" โดยไม่บันทึก)
BANK_SVC = "Services/Implementations/BankService.cs"
BANK_WRITER = "Services/Implementations/BankService.StatementImport.cs"
RULES += [
    dict(file=BANK_SVC, method="ImportBankStatementAsync",
         must=["BankStatementFileReader.Read(", "ImportParsedStatementRowsAsync("],
         forbid=["BankTransaction>().Add(", "BankTransactions.Add(", "BankCsvParser.ParseStatement(", "CurrentBalance ="],
         why="หน้า \"ธนาคาร → นำเข้า statement\" แยกไฟล์ด้วยตัวเลือกตัวแยกเดียวแล้วลงผ่านตัวเขียนเดียว — ห้ามกลับไปเขียนแถว/ยอดเอง"),
    dict(file="Services/Implementations/OpenBankingService.cs", method="ImportFileAsync",
         must=["BankStatementFileReader.Read(", "_bank.ImportParsedStatementRowsAsync("],
         forbid=["BankTransactions.Add(", "CurrentBalance =", "newTransactions = totalTransactions"],
         why="open-banking file = ทางเดียวกับหน้า \"ธนาคาร\" · OFX ต้องบันทึกจริง (เดิมนับบรรทัดแล้วตอบว่าใหม่ = HTTP 200 โกหก)"),
    dict(file="Controllers/V1/BankV1Controller.cs", method="ImportStatement",
         must=["_bank.ImportParsedStatementRowsAsync(", "StatementWriteOptions()"],
         forbid=["AddRange(created)", "BankTransaction>().Add("],
         why="API v1 ลงผ่านตัวเขียนเดียวด้วยความหมาย v1 ที่เทสต์ล็อกไว้ (ExternalId อย่างเดียว · ไม่แตะยอด)"),
    dict(file=BANK_WRITER, method="ImportParsedStatementRowsAsync",
         must=["ApplyStatementSnapshot(", "AutoMatchAsync(", "KeepStatementSourceFileAsync("],
         forbid=["CurrentBalance +=", "CurrentBalance -="],
         why="ตัวเขียนเดียว: ยอดคงเหลือ = ยอดตามธนาคาร (ห้ามบวกยอดเดินเอง) · จับคู่อัตโนมัติหลังบันทึก · เก็บไฟล์ต้นทาง"),
    dict(file="Services/Import/Targets/BankStatementImportTarget.cs", method="CommitGroupAsync",
         must=["_bank.ImportParsedStatementRowsAsync("],
         forbid=["BankTransactions.Add(", "BankTransaction>().Add("],
         why="ศูนย์นำเข้า banktransactions ลงผ่านตัวเขียนเดียวกับทุกทางเข้า"),
]

# ทีมตรวจรายการประจำ 2026-10-14 (ผู้ใช้: เลือกบัญชีธนาคารแล้วบันทึกไม่ได้ "Template มี accountId ที่ไม่พบในผังบัญชี") —
# เทสต์ RecurringTemplateTests ล็อก helper · กติกาข้างล่างล็อกจุดเรียกที่เทสต์ helper มองไม่เห็น
_RECUR_1014 = "ทีมตรวจรายการประจำ 2026-10-14: "
RULES += [
    dict(file=RECURRING, method="ValidateTemplateAsync",
         must=["RecurringTemplate.Read(", "_db.BankAccounts", "RecurringTemplate.RefKind.BankAccount", "RecurringTemplate.RefKind.ChartOfAccount",
               "RecurringTemplate.RefKind.Project", "RecurringTemplate.RefKind.Branch"],
         forbid=["accountIds.Add(bankId"],
         why=_RECUR_1014 + "รหัสแต่ละชนิดค้นในตารางของมัน (บัญชีธนาคาร → ทะเบียนบัญชีธนาคาร ไม่ใช่ผังบัญชี) ผ่านตัวอ่านเดียวกับตอนสร้าง"),
    dict(file=RECURRING, method="ExecuteRecurringAsync",
         must=["ValidateTemplateAsync(recurring.CompanyId", "atSave: false", "RecurringSchedule.PlaceholderBaseDate("],
         why=_RECUR_1014 + "ตรวจซ้ำตอนถึงรอบ — ของที่ถูกลบ/ปิดหลังบันทึกต้องล้มดังพร้อมช่อง (ไม่ใช่ AutoPost ตกไปลงเงินสด 111 เงียบ ๆ)"),
    dict(file=RECURRING, method="RunNowAsync",
         must=["JobLock.RunExclusiveAsync(", "AdvisoryLockKey.RecurringRun", "RecordRunFailureAsync(", "RecurringSchedule.IsDue("],
         forbid=["BeginTransactionAsync(", "NextRunDate <= DateTime.UtcNow"], forbid_lit=["FOR UPDATE"],
         why=_RECUR_1014 + "CreateDocumentAsync/ApproveDocumentAsync เปิดธุรกรรมเอง — ห่อด้วยธุรกรรมอีกชั้น = 'connection is already in a transaction' ทุกครั้ง"),
    dict(file=RECURRING, method="ProcessDueRecurringTransactionsAsync",
         must=["JobLock.RunExclusiveAsync(", "AdvisoryLockKey.RecurringRun", "RecordRunFailureAsync(", "_db.ChangeTracker.Clear()",
               "RecurringSchedule.IsFinished(", "RecurringSchedule.DueCutoffUtc(", "RecurringSchedule.IsDue("],
         forbid=["BeginTransactionAsync(", "r.EndDate >= now", "NextRunDate <= now", "NextRunDate > now"], forbid_lit=["FOR UPDATE"],
         why=_RECUR_1014 + "cron: ล็อกต่อรายการแบบ session · ล้ม = เหตุผลบนแถว · วันสิ้นสุดนับรวมวันนั้น (เดิม EndDate >= now ตัดงวดสุดท้าย)"),
    dict(file=RECURRING, method="CreateDocumentFromTemplateAsync",
         must=["MarkRunAsync(recurring, consumesPeriod, result.Id)", "SetRunNoticeAsync(recurring, notices)",
               "RecurringTemplate.ReadGuid(root,", "RecurringTemplate.Number(line,"],
         forbid=["Guid.Parse(aid.GetString()", "up.GetDecimal()"],
         before=[("CreateDocumentAsync(recurring.CompanyId", "MarkRunAsync("), ("MarkRunAsync(", "ApproveDocumentAsync(")],
         why=_RECUR_1014 + "ประทับรอบทันทีหลังสร้าง (ขั้นอนุมัติ/อีเมลล้มไม่ทำให้ออกใบซ้ำ) · เรื่องต้องทำต่อขึ้นแถว · ตัวอ่านเดียวกับตัวตรวจ"),
]

# ── รอบ 2026-10-14 ทีม F4 (PAYMENT_CHANNEL_AUDIT ส่วนที่ 2 · ID#9/10/11/12/25): ผู้อนุมัติ · ผู้ลงนามข้ามบริษัท · อีเมล/รายงานตั้งเวลา ──
RULES += [
    dict(file=APPROVAL, method="SubmitActionAsync",
         must=["ApprovalApproverCheck.NotAssignedMessage(", "EnsureMayApproveDocumentAsync(", "a.ApproverUserId == userId"],
         before=[("EnsureMayApproveDocumentAsync(", "action.Status = actionRequest.Status")],
         why="ID#9: ผู้กดต้องเป็นผู้อนุมัติของขั้นปัจจุบัน (ทุกขั้น รวมปฏิเสธ) + ขั้นสุดท้ายของเอกสารต้องมีสิทธิ์อนุมัติเอกสาร — ตรวจก่อนบันทึก (เจ้าของเดียวของเว็บ+มือถือ)"),
    dict(file=APPROVAL, method="EnsureMayApproveDocumentAsync",
         must=["DocumentPermissionHelper.CanApproveAsync(", "d.CompanyId == companyId"],
         why="ID#9: ด่านสิทธิ์ขั้นสุดท้ายใช้ตัวตัดสินเดียวกับปุ่มอนุมัติบนหน้าเอกสาร"),
    dict(file=APPROVAL, method="SubmitForApprovalAsync",
         must=["CompanyMembership.ActiveMemberIdsAsync(", "ApprovalApproverCheck.DepartedStepsProblem("],
         before=[("ApprovalApproverCheck.DepartedStepsProblem(", "_db.ApprovalRequests.Add(")],
         why="ID#11: ห้ามส่งคำขอไปหาผู้อนุมัติที่ออกจากบริษัทแล้ว (ค้างตลอดกาล) — บอกให้แก้กฎก่อนสร้างคำขอ"),
    dict(file=APPROVAL, method="ReassignApproverAsync",
         must=["ApprovalApproverCheck.ReassignProblem(", "CompanyMembership.ActiveMemberIdsAsync(", "AddChainedAuditLog(",
               "r.CompanyId == companyId"],
         before=[("ApprovalApproverCheck.ReassignProblem(", "action.ApproverUserId = req.NewApproverUserId")],
         forbid=["_db.ApprovalActions.Add(", "AuditLogs.Add("],
         why="ID#11: โอนผู้อนุมัติแก้แถวที่รออยู่ (ไม่เพิ่มแถว) หลังผ่านตัวตัดสิน (คนเดิมออกแล้ว · คนใหม่เป็นสมาชิก · ไม่ใช่ผู้ขอ) + audit ใน chain"),
    dict(file=APPROVAL, method="MapWithMembershipAsync",
         must=["CompanyMembership.ActiveMemberIdsAsync("],
         why="ID#11: ป้าย 'ผู้อนุมัติออกแล้ว' บนคำขอมาจากตัวตัดสินสมาชิกตัวเดียว"),
    dict(file="Controllers/ApprovalController.cs", method="Reassign",
         must=["ReassignApproverAsync("],
         why="ID#11: ทางไปต่อของคำขอที่ค้าง (ด่านสิทธิ์อยู่ที่ attribute — tools/owner_action_wiring_check.py)"),
    dict(file=XTENANT, method="ApproveIncomingAsync",
         must=["CrossTenantSignerResolver.Resolve(", "CompanyMembership.ActiveMemberIdsAsync(", "s.UserId == signerUserId"],
         before=[("CrossTenantSignerResolver.Resolve(", "_db.DocumentApprovals.Add(")],
         forbid=["signatureUserId ?? config.DefaultApproverUserId ?? actingUserId"],
         why="ID#10: ผู้ลงนามต้องเป็นสมาชิกของบริษัทผู้อนุมัติ · ลายเซ็นต้องเป็นของผู้ลงนาม — ตรวจก่อนประทับลายเซ็น"),
    dict(file=XTENANT, method="TryAutoApproveAsync",
         must=["CompanyMembership.IsActiveMemberAsync(", "CrossTenantSignerResolver.DepartedDefaultApproverNotice"],
         before=[("CompanyMembership.IsActiveMemberAsync(", "ApproveIncomingAsync(")],
         why="ID#10: ผู้อนุมัติเริ่มต้นที่ออกจากบริษัทแล้วห้ามอนุมัติ/ลงนามอัตโนมัติ — และต้องทิ้งหมายเหตุให้เห็น (ไม่ค้างเงียบ)"),
    dict(file=XTENANT, method="ApplyDefaultSignerAsync",
         must=["CrossTenantSignerResolver.ConfigProblem(", "CompanyMembership.IsActiveMemberAsync("],
         before=[("CrossTenantSignerResolver.ConfigProblem(", "cfg.DefaultApproverUserId = effectiveApprover")],
         why="ID#10: ตั้งผู้อนุมัติ/ลายเซ็นเริ่มต้นได้เฉพาะสมาชิก + ลายเซ็นของคนนั้น"),
    dict(file="Controllers/CrossTenantWorkflowController.cs", method="UpdateConfig",
         must=["ApplyDefaultSignerAsync("],
         forbid=["c.DefaultApproverUserId = req.", "c.DefaultSignatureId = req."],
         why="ID#10: ห้ามเขียน Guid ผู้อนุมัติ/ลายเซ็นที่ส่งมาตรง ๆ"),
    dict(file="Controllers/CrossTenantWorkflowController.cs", method="GetConfig",
         must=["GetDefaultApproverNoticeAsync("],
         why="ID#10: หน้าตั้งค่าต้องเห็นว่าผู้อนุมัติเริ่มต้นออกไปแล้ว (ระบบหยุดอนุมัติอัตโนมัติ)"),
    dict(file="Services/Implementations/EmailScheduleService.cs", method="ProcessPendingQueueAsync",
         must=["RecheckDocumentAtSendAsync(", "ScheduledEmailSendCheck.AttachmentFailure(", "ScheduledEmailSendCheck.SkippedStatus"],
         before=[("RecheckDocumentAtSendAsync(", "sender.SendAsync("), ("BuildAttachmentAsync(", "sender.SendAsync(")],
         why="ID#12: ตรวจเอกสารซ้ำตอนส่ง (ยกเลิก/ชำระครบ ⇒ ข้ามพร้อมเหตุผล) · ไฟล์แนบล้ม ⇒ ไม่ส่งอีเมลเปล่าแล้วประทับ Sent"),
    dict(file="Services/Implementations/EmailScheduleService.cs", method="RecheckDocumentAtSendAsync",
         must=["ScheduledEmailSendCheck.SkipReason(", "d.CompanyId == item.CompanyId", "DocumentEmailContext("],
         why="ID#12: ตัวตัดสินเดียว + ค่าในอีเมลเป็นค่า ณ เวลาส่ง (ยอดค้างชำระปัจจุบัน)"),
    dict(file="Services/Implementations/EmailScheduleService.cs", method="BuildAttachmentAsync",
         forbid=["catch", "LogWarning("],
         why="ID#12: สร้างไฟล์แนบล้มต้องโยนให้ผู้เรียกเลื่อนไปลองใหม่ — ห้ามกลืนแล้วคืน null (ส่งอีเมลเปล่า + ประทับ Sent)"),
    dict(file="Services/Implementations/ScheduledReportDispatcher.cs", method="DispatchOneAsync",
         must=["ScheduledReportOutcome.Summarize(", "result.Success", "failures.Add("],
         forbid=["catch { }", "IEmailService"],
         why="ID#25: ผลรายผู้รับมาจากผลจริงของช่องส่ง — ล้มบางราย/ทุกรายต้องไม่เป็น OK"),
    dict(file="Services/Implementations/ScheduledReportDispatcher.cs", method="DispatchAndRecordAsync",
         must=["outcome.AnyDelivered", "s.LastResult = outcome.LastResult"],
         why="ID#25: ประทับ LastSentAt เฉพาะเมื่อมีผู้รับได้รับจริง"),
    dict(file="Controllers/ScheduledReportController.cs", method="SendNow",
         must=["ScheduledReportDispatcher.DispatchAndRecordAsync(", "JobLock.RunExclusiveAsync(", "!x.IsDeleted"],
         before=[("JobLock.RunExclusiveAsync(", "ScheduledReportDispatcher.DispatchAndRecordAsync(")],
         forbid=["s.LastSentAt = DateTime.UtcNow"],
         why="ID#25: ปุ่มส่งทันทีต้องส่งจริง (เดิมประทับ OK + LastSentAt โดยไม่ส่ง) · ฝ่ายค้าน F4: ล็อกเดียวกับรอบตามเวลา + ไม่ส่งรายงานที่ลบแล้ว"),
    # ── ฝ่ายค้าน F4 (ทางเข้าอื่นของข้อมูลชุดเดียวกัน) ──
    dict(file="Helpers/CompanyMembership.cs", method="ActiveMemberIdsAsync",
         must=["cu.CompanyId == companyId", "CanAct("],
         why="ID#10/11: นิยามสมาชิกตัวเดียว — กรองบริษัท (tenant) + ตัดบัญชีที่ปิด/ระงับ"),
    dict(file="Services/Implementations/SignatureApprovalService.cs", method="SetupApprovalAsync",
         must=["CompanyMembership.ActiveMemberIdsAsync("],
         before=[("CompanyMembership.ActiveMemberIdsAsync(", "_db.Set<DocumentApproval>().AddRange(")],
         why="ฝ่ายค้าน F4 (ID#11 ทางเข้าอื่น): ขั้นอนุมัติแบบลายเซ็นห้ามชี้ผู้ใช้ที่ไม่ใช่สมาชิก (ค้าง WaitingApproval ตลอดกาล)"),
    dict(file="Services/Implementations/MobileApiService.cs", method="GetQuickActionsAsync",
         must=["a.ApproverUserId == userId", "a.StepOrder == ar.CurrentStep"],
         why="ฝ่ายค้าน F4 (ID#9): ตัวนับของแอปใช้เกณฑ์เดียวกับด่าน (รอคุณที่ขั้นปัจจุบัน) — ไม่โชว์รายการที่กดแล้วได้ 403"),
    dict(file="Services/Implementations/EmailScheduleService.cs", method="RecheckWhtCertAtSendAsync",
         must=["ScheduledEmailSendCheck.WhtCertSkipReason(", "c.CompanyId == item.CompanyId"],
         why="ฝ่ายค้าน F4 (ID#12 ทางเข้าอื่น): 50 ทวิ ที่ยกเลิกหลังเข้าคิวต้องไม่ถูกส่ง"),
    dict(file="Services/Implementations/ApprovalService.cs", method="ReassignApproverAsync",
         must=["a.ApproverUserId == req.NewApproverUserId && (action == null || a.Id != action.Id)"],
         why="ฝ่ายค้าน F4 (SoD) + ฝ่ายค้านรอบ 2 P2(f): โอนให้คนที่เป็นผู้อนุมัติขั้นอื่นของคำขอเดียวกัน (อนุมัติแล้วหรือยังรอ) ไม่ได้"),
]

# ทีม F1 · 2026-10-14 (PAYMENT_CHANNEL_AUDIT ส่วนที่ 1 ข้อ 1/2/19 · ส่วนที่ 2 ข้อ 1/2/8/13): รหัสที่ผู้ใช้เลือกมาต้องผ่าน
# Helpers/PickedAccountGuard ก่อนเขียนลงบรรทัด JE/ใบสำคัญจ่าย — บั๊กต้นเรื่อง: BankAccount.Id ถูกใช้เป็นผังบัญชีตรง ๆ
PETTY = "Services/Implementations/PettyCash/PettyCashService.cs"
LOAN = "Services/Implementations/LoanService.cs"
RULES += [
    dict(file=PETTY, method="DisburseAsync",
         must=["PickedAccountGuard.RequirePostingAccountAsync(", "RequireFundAccountUsableAsync("],
         before=[("PickedAccountGuard.RequirePostingAccountAsync(", "_db.JournalEntries.Add(")],
         why="ผังค่าใช้จ่ายจาก request ต้องเป็นผังของบริษัทนี้ที่ลงรายการได้ก่อนเขียนลง JE (เดิมเขียนตรง — ข้ามบริษัท/ผังคุมได้)"),
    dict(file=PETTY, method="ReplenishAsync",
         must=["PickedAccountGuard.ResolveMoneyChannelAsync(", "RequireFundAccountUsableAsync("],
         before=[("PickedAccountGuard.ResolveMoneyChannelAsync(", "_db.JournalEntries.Add(")],
         why="บัญชีธนาคารต้นทางต้องแปลงผ่าน LinkedAccountId (เดิมใช้ BankAccount.Id เป็นผังบัญชี ⇒ FK ล้ม)"),
    dict(file=LOAN, method="CreateAsync",
         must=["PickedAccountGuard.ResolveMoneyChannelAsync(", "RequireOptionalAccountAsync(", "LoanPostingPlan.ForDisbursement("],
         before=[("LoanPostingPlan.ForDisbursement(", "_db.Loans.Add(")],
         why="ผังของสินเชื่อตรวจบริษัท/สถานะ/ชนิด · ช่องทางเงินแปลงจากบัญชีธนาคาร · มีผังครึ่งเดียว = ปฏิเสธ ไม่ข้าม JE เงียบ"),
    dict(file=LOAN, method="MakePaymentAsync",
         must=["PickedAccountGuard.ResolveMoneyChannelAsync(", "loan.MoneyAccountId", "LoanPostingPlan.NoMoneyAccountMessage"],
         forbid_lit=['a.AccountCode == "11122"'],
         why="ค่างวดจ่ายจากบัญชีที่เลือก/ของสินเชื่อ — เดิมไม่สนผังของสินเชื่อแล้วจ่ายจาก 11122/111x ตัวแรกเสมอ"),
    dict(file="Services/Implementations/ExpenseClaimService.cs", method="MarkAsPaidAsync",
         must=["PickedAccountGuard.ResolveMoneyChannelAsync(", "BankAccountId: payBank", "PaymentAccountId: payAccount"],
         before=[("PickedAccountGuard.ResolveMoneyChannelAsync(", "CreateDocumentAsync(")],
         why="จ่ายใบเบิก: บัญชีที่จ่ายตรวจก่อนสร้างใบสำคัญจ่าย และส่งต่อถึงใบ (เดิมไม่มีช่อง ⇒ ลงเงินสดเสมอ)"),
    dict(file="Services/Implementations/SalaryAdvanceService.cs", method="DisburseAsync",
         must=["PickedAccountGuard.ResolveMoneyChannelAsync(", "BankAccountId: payBank", "PaymentAccountId: payAccount"],
         before=[("PickedAccountGuard.ResolveMoneyChannelAsync(", "CreateDocumentAsync(")],
         why="จ่ายเงินทดรอง: บัญชีธนาคารที่ไม่ผูกผัง/ของบริษัทอื่นต้องถูกปฏิเสธก่อนสร้าง PV (ไม่ตกไปเงินสดเงียบ ๆ)"),
    dict(file="Services/Implementations/Cheque/ChequeService.cs", method="OpenChequeBookAsync",
         must=["PickedAccountGuard.RequireActiveBankAsync("],
         before=[("PickedAccountGuard.RequireActiveBankAsync(", "_db.ChequeBooks.Add(")],
         why="เล่มเช็คต้องผูกกับบัญชีธนาคารของบริษัทนี้ที่เปิดใช้งาน (เดิมไม่ตรวจ)"),
]

# ฝ่ายค้าน 2026-10-14 P0 (หลัง f696f418): งวดค้างหลายงวด ⇒ รอบอัตโนมัติ/สร้างทันทีออกงวดเดียว (ไม่ไล่ออกใบกำกับย้อนหลังทุก 15 นาที)
RULES += [
    dict(file=RECURRING, method="ProcessDueRecurringTransactionsAsync",
         must=["SkipOverduePeriodsAsync(recurring, now)"],
         before=[("SkipOverduePeriodsAsync(", "ExecuteRecurringAsync(")],
         why="งวดค้าง (เอกสารชนิดรายการประจำล้มทุกรอบตั้งแต่ 2026-09-03) ต้องเลื่อนไปงวดล่าสุดก่อนออกใบ — ไม่งั้น cron ออกใบกำกับภาษีย้อนหลังทีละงวด (เลขจริง ลงวันที่วันนี้ ส่งอีเมลลูกค้า)"),
    dict(file=RECURRING, method="RunNowAsync",
         must=["SkipOverduePeriodsAsync(recurring, DateTime.UtcNow)"],
         before=[("SkipOverduePeriodsAsync(", "ExecuteRecurringAsync(")],
         why="ปุ่มสร้างทันทีบนแถวที่ค้างหลายงวดออกงวดล่าสุด ไม่ใช่งวดเก่าสุด — ตัวตัดสินเดียวกับ cron"),
]

# รอบ 205 ทีม D (IMPORT_ETL_REVIEW §2.4/§2.5/§2.9) — ปลายทางบัญชีของศูนย์นำเข้า + migration wizard: ลงผ่าน domain service เดิมเท่านั้น ·
# งบทดลองยกมาตรวจด้วยตัวตัดสินเดียว (บล็อก/คำเตือน ข้อ 150) ทั้งสองทางเข้า · ด่านสิทธิ์ทุก action ของ wizard
_ETL_D = "รอบ 205 ทีม D: "
_MW = "Services/Implementations/MigrationWizardService.cs"
_MWC = "Controllers/MigrationWizardController.cs"
RULES += [
    dict(file=_MW, method="EvaluateAsync", must=["OpeningTrialBalanceRules.Evaluate("],
         why=_ETL_D + "หน้า wizard ตรวจด้วยตัวตัดสินเดียวกับศูนย์นำเข้า (รหัสคำเตือนชุดเดียว)"),
    dict(file=_MW, method="CommitAsync",
         must=["await EvaluateAsync(session)", "OpeningTrialBalanceRules.Unacknowledged(", "existing.OpeningDebit, existing.OpeningCredit"],
         before=[("await EvaluateAsync(session)", "BeginTransactionAsync("), ("OpeningTrialBalanceRules.Unacknowledged(", "BeginTransactionAsync(")],
         why=_ETL_D + "ลงยอดยกมาตรวจใหม่ก่อนเขียน (ไม่เชื่อสถานะเดิม) · คำเตือนต้องรับทราบ · เก็บยอดเดิมที่ถูกทับไว้ย้อน"),
    dict(file=_MW, method="RollbackAsync", must=["ob.OpeningDebit = o.PriorDebit"],
         why=_ETL_D + "ย้อนคืนยอดเดิมที่ชุดนี้ทับ (เดิมลบเฉพาะแถวที่สร้างใหม่)"),
    dict(file=_MW, method="ApplyMappingsAsync", must=["a.CompanyId == companyId", "_importAi.RecordPickChoiceAsync("],
         why=_ETL_D + "บัญชีปลายทางต้องอยู่ในผังของบริษัท (ข้อ 150 (3)) · ปิดลูป AI ImportAccountMap (กฎเหล็ก #1)"),
    dict(file="Services/Import/Targets/OpeningTrialBalanceImportTarget.cs", method="EvaluateAsync", must=["OpeningTrialBalanceRules.Evaluate("],
         why=_ETL_D + "ศูนย์นำเข้าตรวจงบทดลองด้วยตัวตัดสินเดียวกับหน้า wizard"),
    dict(file="Services/Import/Targets/OpeningTrialBalanceImportTarget.cs", method="CommitGroupAsync", must=["_wizard.CommitAsync("],
         forbid=["OpeningBalances.Add(", "OpeningBalances.Update("],
         why=_ETL_D + "ลงยอดยกมาผ่าน MigrationWizardService เท่านั้น"),
    dict(file="Services/Import/Targets/DocumentImportTarget.cs", method="CommitGroupAsync",
         must=["_builder.BuildAsync(", "_documents.CreateDocumentAsync("], forbid=["Documents.Add(", "DocumentNumber ="],
         why=_ETL_D + "เอกสารนำเข้าผ่าน DocumentService (ร่าง + เลข DRAFT · ด่านงวด) — ห้ามเขียนตาราง Documents ตรง (คำตัดสินข้อ 151)"),
    dict(file="Services/Import/Targets/DocumentImportBuilder.cs", method="BuildAsync", must=["Reference: original"],
         forbid=["SubTotal", "TotalAmount", "VatAmount ="],
         why=_ETL_D + "เลขเดิมไป Reference · ยอดรวม/VAT คำนวณโดย DocumentService ไม่ใช่ตัวประกอบ"),
    dict(file="Services/Import/Targets/PaymentImportTarget.cs", method="CommitGroupAsync", must=["_documents.CreatePaymentAsync("],
         forbid=["PaidAmount", "Payments.Add(", "BalanceDue ="],
         why=_ETL_D + "รับ/จ่ายชำระผ่าน CreatePaymentAsync ⇒ JE/ยอดค้าง/หัก ณ ที่จ่าย (เดิมแก้ PaidAmount ตรง GL ไม่ขยับ)"),
    dict(file="Services/Import/Targets/FixedAssetImportTarget.cs", method="CommitGroupAsync", must=["_assets.ImportAsync("],
         forbid=["FixedAssets.Add("], why=_ETL_D + "ทะเบียนสินทรัพย์ผ่าน FixedAssetService.ImportAsync (FixedAssetImportMethod — ที่ดินไม่คิดค่าเสื่อม)"),
    dict(file="Services/Import/Targets/FixedAssetImportTarget.cs", method="ValidateAsync", must=["FixedAssetImportMethod.Resolve("],
         why=_ETL_D + "หน้าตรวจใช้ตัวตัดสินวิธีคิดค่าเสื่อมเดียวกับตอนลง"),
    dict(file="Services/Import/ImportEntityResolver.Accounts.cs", method="AskAccountMapAiAsync",
         must=["_importAi.SuggestAccountMapAsync(", "allowed"],
         why=_ETL_D + "ชั้นโมเดลของ ImportAccountMap ผ่าน augmenter ของทีม A (orchestrator · ด่านรหัส ∈ ผังบริษัท) เท่านั้น"),
    dict(file="Services/Import/ImportEntityResolver.Accounts.cs", method="ResolveAsync",
         must=["ImportAccountCodeResolver.Resolve(", "answer.Confidence >= AiMinConfidence", "a.IsPostable"],
         before=[("ImportAccountCodeResolver.Resolve(", "AskAccountMapAiAsync(")],
         why=_ETL_D + "local ก่อนเสมอ · คำตอบโมเดลผ่านด่านผังบริษัท + ความมั่นใจ (กฎเหล็ก #1 anti-hallucination)"),
] + [
    dict(file=_MWC, method=m, must=["await DenyAsync(companyId)"], why=_ETL_D + "ด่านสิทธิ์ migration wizard (เดิมมีแค่ [Authorize])")
    for m in ("Create", "List", "Get", "Upload", "UploadText", "Suggest", "ApplyMappings", "Validate", "PostDifference", "Commit",
              "Rollback", "Reconciliation")
]

# รอบ 205 ทีม E — ศูนย์นำเข้าข้อมูล (IMPORT_ETL_REVIEW §2.7): service ทีม A/B ไม่ตรวจสิทธิ์โดยสัญญา ⇒ ทุก action ต้องเรียกด่านก่อน service และใช้ผล
_HUB = "Controllers/ImportHubController.cs"
_HUB_WHY = "รอบ 205 ทีม E: ด่านสิทธิ์นำเข้าของชนิดของ batch (ForImport) ต้องมาก่อน service และใช้ผล (ไม่ใช่เรียกแล้วทิ้ง) — batch บริษัทอื่น = 404"
_HUB_USE = r"if\s*\(\s*deny\s*!=\s*null\s*\)\s*return\s+deny\s*;"
RULES += [
    dict(file=_HUB, method=m, must=["DenyBatchAsync(companyId, batchId, ct)"], must_re=[_HUB_USE],
         before=[("DenyBatchAsync(", sink)], why=_HUB_WHY)
    for m, sink in [
        ("GetBatch", "_pipeline.GetAsync("), ("GetRows", "_pipeline.GetRowsAsync("), ("Analyze", "_pipeline.AnalyzeAsync("),
        ("Remap", "_pipeline.RemapAsync("), ("EditRow", "_pipeline.EditRowAsync("), ("Bulk", "_pipeline.BulkActionAsync("),
        ("Commit", "_pipeline.CommitAsync("), ("UndoPreview", "_pipeline.PreviewUndoAsync("), ("Undo", "_pipeline.UndoAsync("),
        ("Cancel", "_pipeline.CancelAsync("), ("ErrorsCsv", "_pipeline.ExportErrorRowsCsvAsync("), ("SourceFile", "_pipeline.GetSourceFileAsync("),
    ]
] + [
    dict(file=_HUB, method="DenyBatchAsync", must=["BatchEntityTypeAsync(companyId, batchId, ct)", "BatchNotFound()", "DenyAsync(companyId, entityType)"],
         why=_HUB_WHY),
    dict(file=_HUB, method="BatchEntityTypeAsync", must_lit=["s.CompanyId == companyId"], why="tenant isolation: batch ของบริษัทอื่น = ไม่พบ (404)"),
    dict(file=_HUB, method="DenyAsync", must=["ImportExportPermissionScope.ForImport(entityType)", "CanAsync(companyId, key, cache)",
                                              "ImportExportPermissionScope.DeniedMessage("],
         why="ตารางชนิด→คีย์ตัวเดียว (ห้ามคีย์คงที่ตัวเดียวคลุมทุกชนิด) · ปฏิเสธพร้อมทางไปต่อ"),
    dict(file=_HUB, method="Analyze", must=["DenyAsync(companyId, req.EntityType)"], must_re=[r"if\s*\(\s*denyNew\s*!=\s*null\s*\)\s*return\s+denyNew\s*;"],
         before=[("DenyAsync(companyId, req.EntityType)", "_pipeline.AnalyzeAsync(")],
         why="เปลี่ยนชนิดข้อมูลของ batch ต้องผ่านด่านของชนิดใหม่ด้วย (กันเปลี่ยนเป็นพนักงานเพื่อเลี่ยงสิทธิ์ HR)"),
    dict(file=_HUB, method="CreateBatch", must=["DenyAsync(companyId, chosen)", "CanImportAnythingAsync(companyId)",
                                                "ImportExportPermissionScope.ForImport(detected)", "_pipeline.CancelAsync("],
         before=[("DenyAsync(companyId, chosen)", "_pipeline.CreateAsync("), ("CanImportAnythingAsync(companyId)", "_pipeline.CreateAsync("),
                 ("_pipeline.CreateAsync(", "ImportExportPermissionScope.ForImport(detected)")],
         why="ระบุชนิด = ด่านก่อนอ่านไฟล์ · ให้ระบบเดา = ด่านของชนิดที่เดาได้ (ไม่ผ่าน ⇒ ยกเลิกชุด + 403)"),
    dict(file=_HUB, method="UpdateTemplate", must=["DenyAsync(companyId, existing.EntityType)"],
         before=[("DenyAsync(companyId, existing.EntityType)", "_templates.UpdateAsync(")], why=_HUB_WHY),
    dict(file=_HUB, method="DeleteTemplate", must=["DenyAsync(companyId, existing.EntityType)"],
         before=[("DenyAsync(companyId, existing.EntityType)", "_templates.DeleteAsync(")], why=_HUB_WHY),
    dict(file=_HUB, method="Entities", must=["ImportExportPermissionScope.ForImport(e.EntityType)", "UserCanImport = await CanAsync("],
         why="ปุ่มล็อก/เปิดบนหน้าเว็บใช้ด่านเดียวกับ action (ไม่เชื่อค่าที่ service ส่งมา) ⇒ ปุ่มเปิดแต่กดแล้ว 403 เกิดไม่ได้"),
    # ฝ่ายค้านรอบ ETL (P1-5 · PDPA ม.26): ค่าเต็มของทะเบียนพนักงานเฉพาะผู้มี Pii.View — ผลด่านต้องถูกส่งเข้า service (ไม่ใช่ถามแล้วทิ้ง)
    dict(file=_HUB, method="RevealPiiAsync", must=["ImportPiiFields.HasPii(entityType)", "PermissionKeys.PiiView", "LogPiiAccessAsync("],
         why="ฝ่ายค้านรอบ ETL P1-5: ค่าเต็มของข้อมูลอ่อนไหวเปิดได้เฉพาะ Pii.View + บันทึก PiiAccessLog (ม.37)"),
] + [
    dict(file=_HUB, method=m, must=["RevealPiiAsync("], call_args=[(sink, "reveal")], before=[("RevealPiiAsync(", sink)],
         why="ฝ่ายค้านรอบ ETL P1-5: ผลด่าน Pii.View ต้องถูกส่งให้ service ปิดบัง (ค่าเริ่มของ service = ปิดบัง)")
    for m, sink in [("GetRows", "_pipeline.GetRowsAsync("), ("EditRow", "_pipeline.EditRowAsync("), ("ErrorsCsv", "_pipeline.ExportErrorRowsCsvAsync(")]
] + [
    dict(file=_HUB, method="SourceFile", must=["RevealPiiAsync("], before=[("RevealPiiAsync(", "_pipeline.GetSourceFileAsync(")],
         why="ฝ่ายค้านรอบ ETL P1-5: ไฟล์ต้นทางปิดบังรายช่องไม่ได้ ⇒ ชนิดที่มี PII ต้องมี Pii.View ก่อนอ่านไฟล์"),
    dict(file="Services/Implementations/AttachmentAccessGate.cs", method="DenyAttachmentAsync", must=["ImportPiiFields.HasPii(batchType)"],
         why="ฝ่ายค้านรอบ ETL P1-5: ทางไฟล์แนบชนิด ImportBatch ต้องด่าน Pii.View เดียวกับ source-file (ไม่ใช่ทางอ้อม)"),
    # ฝ่ายค้านรอบ ETL — เป้าหมายของปลายทาง
    dict(file="Services/Import/Targets/MasterDataImportTargets.cs", method="AccountReferencesAsync",
         must=["ChartAccountReferences.FirstReferrerAsync(", "DefaultPaymentAccountId == entityId", "ParentAccountId == entityId"],
         why="ฝ่ายค้านรอบ ETL P1-3: ย้อนผังบัญชีต้องดูทุก FK ที่อ้างผัง (ยอดยกมา · บัญชีลูก · ธนาคาร · สินค้า · สินทรัพย์ · AccountMappings …)"),
    dict(file="Services/Implementations/ImportExportService.cs", method="ImportJournalEntryAsync",
         must=["_journalsCreatedHere.Contains(existing.Id)", "JournalAppendedLineKey", "_journalsCreatedHere.Add(entry.Id)"],
         why="ฝ่ายค้านรอบ ETL P1-1: ต่อบรรทัดเข้าใบที่มีก่อนการนำเข้า ⇒ จดบรรทัดที่เพิ่ม (ย้อนลบเฉพาะบรรทัด ไม่ลบใบของผู้ใช้)"),
    dict(file="Services/Import/Targets/DocumentImportTarget.cs", method="ValidateAsync", must=["_builder.PrepareAsync(", "ImportMatchAnswerSpace.Link"],
         why="ฝ่ายค้านรอบ ETL P1-2: ผู้ติดต่อของเอกสารตัดสินตั้งแต่ขั้นตรวจ (ไม่ใช่ 'พร้อมลง' แล้วล้มตอนลง) + ปิดลูป AI ด้วยผู้ติดต่อที่ใช้จริง"),
    dict(file="Services/Import/Targets/DocumentImportTarget.cs", method="CommitGroupAsync", must=["CreatedEarlierInBatchAsync("],
         before=[("CreatedEarlierInBatchAsync(", "_documents.CreateDocumentAsync(")],
         why="ฝ่ายค้านรอบ ETL P2: ทำต่อหลังเครื่องล่มต้องไม่สร้างเอกสารซ้ำ"),
    dict(file="Services/Import/Targets/PaymentImportTarget.cs", method="CommitGroupAsync", must=["CreatedEarlierInBatchAsync("],
         before=[("CreatedEarlierInBatchAsync(", "_documents.CreatePaymentAsync(")],
         why="ฝ่ายค้านรอบ ETL P2: ทำต่อหลังเครื่องล่มต้องไม่บันทึกการชำระซ้ำ"),
    dict(file="Services/Import/Targets/DocumentImportBuilder.cs", method="BuildAsync", forbid=["SuggestGlAccountAsync(", "AllowAi: true"],
         why="ฝ่ายค้านรอบ ETL P2: ตอนลงข้อมูล/worker ไม่ถาม AI — ใช้ผลที่ขั้นตรวจเก็บไว้ในแถว"),
    dict(file="Services/Import/Targets/DocumentImportBuilder.cs", method="ResolveContactAsync",
         must=["ContactTaxBranchKey.SoftScope(", "hits.Count == 1"],
         why="ฝ่ายค้านรอบ ETL P1-2: ชื่อตรงตัวผูกได้เฉพาะเมื่อไม่ซ้ำในชุด SoftScope · ชื่อคล้ายอย่างเดียวไม่เลือกให้ (ข้อ 151 (5))"),
    dict(file="Services/Import/Targets/OpeningTrialBalanceImportTarget.cs", method="BuildLinesAsync", must=["AllowAi(ctx)"],
         forbid=["AllowAi: true"],
         why="ฝ่ายค้านรอบ ETL P1-4: งบทดลองไม่ถาม AI ตอนลงยอด/ตรวจซ้ำ (pipeline เป็นผู้อนุญาต)"),
    dict(file="Services/Import/Targets/OpeningTrialBalanceImportTarget.cs", method="ValidateAsync",
         must=["MatchAiFeedbackId: m?.AiFeedbackId", "ImportMatchAnswerSpace.AccountCode"],
         why="ฝ่ายค้านรอบ ETL P1-4: feedback ของ ImportAccountMap ต้องไปถึงแถวพักให้ปิดลูปตอนลงยอด (กฎเหล็ก #1)"),
    dict(file="Services/Import/Targets/OpeningTrialBalanceImportTarget.cs", method="UndoAsync",
         must=["MigrationWizardService.RollbackSkippedAccountIds("],
         why="ฝ่ายค้านรอบ ETL: ยอดที่ผู้ใช้แก้มือ (wizard ไม่แตะ) ต้องไม่ถูกประทับ 'ย้อนแล้ว'"),
    dict(file="Services/Import/ImportBatchWorker.cs", method="PurgeAsync", must=["PurgeAbandonedAsync("],
         why="ฝ่ายค้านรอบ ETL: ชุดที่ถูกทิ้งไว้ต้องถูกล้างแถวพัก (PII) ไม่ค้างตลอดไป"),
    dict(file="Services/Ai/Prompts/ImportPrompts.cs", method="BuildColumnMatch", must=["ImportPiiFields.RedactForPrompt("],
         why="ฝ่ายค้านรอบ ETL P1-5: ตัวอย่างค่าที่ส่ง AI ไม่มีเลขบัตร/บัญชีจริง"),
    dict(file="Services/Import/ImportMappingTemplateStore.cs", method="UpdateAsync", must=["t.AutoCommitWhenClean = false"],
         forbid=["t.AutoCommitWhenClean = request.AutoCommitWhenClean"],
         why="ฝ่ายค้านรอบ ETL (F2 ข้อ 2): ค่าตั้งที่ไม่มีผู้อ่านห้ามรับ 'เปิด' แล้วเงียบ"),
]
_IC = "Controllers/ImportConflictController.cs"
RULES += [
    dict(file=_IC, method=m, must=["DenyAsync(companyId,"], must_re=[_HUB_USE], before=[("DenyAsync(companyId,", sink)],
         why="รอบ 205 ทีม E: แถว staged มีข้อมูลจากไฟล์ (PII ได้) · ผลตัดสินเขียนฐาน ⇒ ด่านนำเข้าของชนิดของแถวก่อนอ่าน/เขียน")
    for m, sink in [("GetBySession", "Ok("), ("GetBySessionRef", "Ok("), ("Resolve", "SaveChangesAsync("),
                    ("ResolveAll", "SaveChangesAsync("), ("Detect", "_detector.DetectBatchAsync(")]
]

# 2026-10-14 (ผู้ใช้มือถือ "กดบันทึก pdf ขึ้นแต่หน้าปริ้น"): มือถือได้ไฟล์ QuestPDF แทนหน้าพิมพ์ ⇒ บรรทัด "ชื่อ" ของ 50 ทวิ
# ต้องเล่าเหมือนกันทั้ง 3 renderer — ประกอบผ่าน WhtCertFormText.PartyName ตัวเดียว (เดิม QuestPDF ต่อคำนำหน้า+สาขา · HTML ต่อแต่สาขา · หน้าพิมพ์ไม่ต่อ)
# ฝั่ง DTO (MapToResponse เป็น expression-bodied — checker อ่านไม่ได้) ล็อกใน WhtCertFormTextTests.PartyName_IsTheOnlyNameComposer_InAllThreeRenderers
RULES += [
    dict(file="Services/Implementations/PdfGenerationService.WhtCert.cs", method="BuildWhtCertPdf",
         must=["WhtCertFormText.PartyName(null, company.Name,", "WhtCertFormText.PartyName(cert.PayeeContact.TitleTh,"],
         forbid=["CertBranchSuffix(", "ThaiTitleHelper.WithTitle("],
         why="50 ทวิ QuestPDF: ชื่อผู้หัก/ผู้ถูกหักจากตัวตัดสินเดียวกับ HTML + หน้าพิมพ์ (2026-10-14)"),
    # การเรียกอยู่ใน interpolated string ⇒ must_lit/forbid_lit (checker ตัด string ออกจาก must/forbid)
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildWithholdingTaxCertHtml",
         must_lit=["{WebUtility.HtmlEncode(WhtCertFormText.PartyName(null, company.Name,",
                   "{WebUtility.HtmlEncode(WhtCertFormText.PartyName(cert.PayeeContact.TitleTh,"],
         forbid_lit=["CertBranchSuffix(", "cert.PayeeContact.Name +"],
         why="50 ทวิ HTML: ชื่อผู้ถูกหักมีคำนำหน้าเหมือน QuestPDF (เดิมขาด) — ตัวตัดสินเดียว WhtCertFormText.PartyName (2026-10-14)"),
]

# 2026-10-14 (ทีม PDF): จำนวนเงินตัวอักษร ≥ 10 ล้านพิมพ์ผิด (units[pos % 7] ไม่วน "ล้าน") ในสูตรเขียนเอง 4 ชุด ⇒ ตัวตั้งเดียว
# Helpers/ThaiBahtText.Format · renderer ทั้ง 4 จุดต้องเรียกตัวตั้ง (เอกสาร HTML/QuestPDF ล็อกในกติกา BuildDocumentHtml/ComposeSummary ข้างบน)
# ฝั่ง DTO wht.html (MapToResponse expression-bodied) ล็อกใน ThaiBahtTextTests · สำเนา JS พรีวิวล็อกใน tools/thai_baht_text_sim.js
RULES += [
    # การเรียกอยู่ใน interpolated string ⇒ must_lit/forbid_lit
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildWithholdingTaxCertHtml",
         must_lit=["<b>{ThaiBahtText.Format(cert.TotalTaxAmount)}</b>"],
         forbid_lit=["ThaiNumberToText("],
         why="50 ทวิ HTML: จำนวนเงินตัวอักษรจากตัวตั้งเดียว ThaiBahtText (2026-10-14 · ≥ 10 ล้านเคยพิมพ์ผิด)"),
    dict(file="Services/Implementations/PdfGenerationService.WhtCert.cs", method="BuildTotalInWords",
         must=["ThaiBahtText.Format(totalTax)"],
         forbid=["ThaiNumberToText("],
         why="50 ทวิ QuestPDF: จำนวนเงินตัวอักษรจากตัวตั้งเดียวกับ HTML + หน้าพิมพ์ (2026-10-14)"),
]

# ── รอบ 206 (ทีมงานค้าง): (2) กติกา "ลงรายการได้" เจ้าของเดียว PostingAccountRule — DocumentMoneyChannel/BankService เดิมใช้ Level < 4 เอง
# (เจ้าของที่สาม/สี่ · ห้ามเขียนใหม่ทั้งเรพ = tools/posting_level_check.py) · (3) ทุกเส้นที่ปิดหนี้ใบเดิมอ่านผังลูกหนี้/เจ้าหนี้จาก JE ตอนออกใบ
# ผ่าน Helpers/OriginalArApAccount (เดิมอ่านผังที่ผู้ติดต่อปักตอนนี้ ⇒ ปิดใช้ = ปิดหนี้ไม่ได้ · เปลี่ยน = ตัดคนละผัง) ──
_R206 = "รอบ 206 ทีมงานค้าง (PAYMENT_CHANNEL_AUDIT ADV2-P1-1 ยังค้าง · ADV2-P2-b)"
_DMC = "Helpers/DocumentMoneyChannel.cs"
RULES += [
    dict(file=_DMC, method="Decide", must=["MoneyAccountDirectory.SourceProblem(", "MoneyAccountDirectory.BankLinkedGlProblem("],
         forbid=["MinPostingLevel", "Level <"],
         why=_R206 + ": แหล่งเงินตัดสินบัญชีคุมด้วยเจ้าของกติกา (มีบัญชีย่อย) — ผังตรงที่ธนาคารผูก/ผังที่ธนาคารผูกยอมเป็นบัญชีคุมเหมือนทางรับจ่ายชำระ"),
    dict(file=_DMC, method="ExpenseCategoryProblem", must=["PostingAccountRule.Check("], forbid=["Level <"],
         why=_R206 + ": หมวดค่าใช้จ่ายตัดสินด้วย PostingAccountRule"),
    dict(file=_DMC, method="LoadGlAsync", must=["LoadPostingFactsAsync("],
         why=_R206 + ": ข้อเท็จจริงของผังมาจากตัวดึงของเจ้าของกติกา (IsGroup = มีบัญชีย่อย) ไม่ใช่ระดับ"),
    dict(file=_DMC, method="ChannelValueForGlAsync", must=["MoneyAccountDirectory.SourceProblem("],
         why=_R206 + ": ค่าตั้งต้นของเทมเพลตตัดสินด้วยตัวเดียวกับตอนลงบัญชี"),
    dict(file="Services/Implementations/BankService.cs", method="ValidateLinkedAccountAsync",
         must=["LoadPostingFactsAsync(", "PostingAccountRule.Check(", "sameAsCurrentLink"], forbid=["Level <"],
         why=_R206 + ": ผูกผังของบัญชีธนาคารใหม่ตัดสินบัญชีคุมด้วยเจ้าของ · ส่งค่าเดิมกลับ (แก้ชื่อ) ไม่ถูกบล็อก"),
    dict(file="Services/Implementations/AccountingService.cs", method="GetAccountsAsync", must=["HasChildAccounts ="],
         why=_R206 + ": หน้าเว็บกรองผังลงรายการได้จาก hasChildAccounts ที่เซิร์ฟเวอร์ตัดสิน (ห้าม level >= 4 ในหน้า)"),
    dict(file=DOC, method="OriginalArApAccountAsync", must=["OriginalArApAccount.LoadAsync(", "fallback("],
         why=_R206 + ": ผังที่ใบตั้งไว้จริงก่อน · ไม่มี JE ⇒ ตัวหาเดิม (โหลดผู้ติดต่อให้ — เดิม Contact ไม่ถูกโหลด ⇒ ข้ามผังที่ปัก)"),
    dict(file=DOC, method="CreatePaymentJournalAsync", must=["OriginalArApAccountAsync(companyId, doc.Id, Accounting.Helpers.ArApSide.Receivable",
         "OriginalArApAccountAsync(companyId, doc.Id, Accounting.Helpers.ArApSide.Payable"],
         why=_R206 + ": รับ/จ่ายชำระตัดผังที่ใบตั้งไว้ (ผังที่ปักถูกปิด/เปลี่ยนทีหลังต้องไม่ทำให้รับชำระไม่ได้/ตัดคนละผัง)"),
    dict(file=DOC, method="WriteOffBadDebtAsync", must=["OriginalArApAccountAsync("],
         why=_R206 + ": ตัดหนี้สูญตัดผังลูกหนี้ที่ใบตั้งไว้"),
    dict(file=DOC, method="ApplyDepositToInvoiceCoreAsync", must=["OriginalArApAccountAsync("],
         why=_R206 + ": หักมัดจำตัดผังลูกหนี้ที่ใบตั้งไว้"),
    dict(file=DOC, method="ApplyJournalDepositToInvoiceAsync", must=["OriginalArApAccountAsync("],
         why=_R206 + ": หักมัดจำจาก JV ตัดผังลูกหนี้ที่ใบตั้งไว้"),
    dict(file=DOC, method="AutoPostToJournalAsync", must=[
             "OriginalArApAccountAsync(companyId, source.Id",                                             # ใบลด/เพิ่มหนี้
             "OriginalArApAccountAsync(companyId, doc.RelatedDocumentId, Accounting.Helpers.ArApSide.Receivable",   # ใบเสร็จปิดใบแจ้งหนี้
             "OriginalArApAccountAsync(companyId, doc.RelatedDocumentId, Accounting.Helpers.ArApSide.Payable"],     # ใบสำคัญจ่าย/ใบรับรองแทนใบเสร็จ
         why=_R206 + ": ใบที่ปิด/ปรับหนี้ของใบต้นทางตัดผังที่ใบต้นทางตั้งไว้"),
    dict(file="Services/Implementations/IntegrationService.cs", method="ResolvePaymentJournalAccountsAsync",
         must=["OriginalArApAccount.LoadAsync("],
         why=_R206 + ": รับ/จ่ายชำระผ่านระบบเชื่อมต่อตัดผังที่ใบตั้งไว้ (เดิมเจ้าหนี้ที่ปักถูกปิด ⇒ ตก 212 ตัวแรกเงียบ ๆ)"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildProjectedGlAsync",
         must=["OriginalArApAccount.LoadAsync("],
         why=_R206 + ": พรีวิว GL ของใบสำคัญจ่ายที่ปิดใบตั้งหนี้ = mirror ของ AutoPost (สอง renderer ห้าม drift)"),
]

# ── ฝ่ายค้านรอบส่งออก 2026-10-15 (erp-review/2026-10-14/EXPORT_AUDIT.md §"ฝ่ายค้านรอบส่งออก") — ล็อกจุดเรียกที่เทสต์ helper มองไม่เห็น ──
_ADVX = "ฝ่ายค้านรอบส่งออก 2026-10-15"
_ACC = "Services/Implementations/AccountingService.cs"
_YEC = "Services/Implementations/AccountingService.YearEndClose.cs"
RULES += [
    # P1-1 ยอดยกมาปิดปี: ตัวคำนวณตัวเดียว (ฐานล่าสุดก่อน 1 ม.ค. + รายการตั้งแต่วันเริ่มฐาน) · ห้ามกลับไปบวก "ยอดยกมา ม.ค. ปีก่อน" ทับรายการทั้งหมด
    dict(file=_YEC, method="RollIntoNextFiscalYearAsync", must=["ComputeRolledOpeningsAsync(", "OpeningRoll.IsImported(", "AddChainedAuditLog(",
                                                                  "FiscalYear.RangeFor("],
         forbid=["prevOpenings", "p.Year == year - 1", "new DateTime(year, 1, 1)"],
         why=_ADVX + " P1-1: ปิดปีครั้งที่สองนับปีเก่าซ้ำ · รอบ 2 P2-6/P2-7: ยกไปงวดแรกของรอบถัดไป + ไม่ทับยอดนำเข้า + audit"),
    dict(file=_YEC, method="RollOpeningBalancesAsync", must=["RollIntoNextFiscalYearAsync(", "OpeningRoll.LockKey(", "YearEndClosings"],
         must_lit=["pg_advisory_xact_lock"],
         why=_ADVX + " รอบ 2 P2-7: ยกยอดด้วยมือต้องปิดปีก่อน + ล็อกเดียวกับปิดปี/ซ่อม (เดิมข้ามด่านทั้งหมด)"),
    dict(file=_YEC, method="YearEndCloseAsync", must=["OpeningRoll.LockKey(", "LatestOpeningAnchorAsync(", "OpeningRoll.PnlCloseWindow(",
                                                       "RollIntoNextFiscalYearAsync("],
         must_lit=["pg_advisory_xact_lock"],
         before=[("OpeningRoll.LockKey(", "YearEndClosings")],
         forbid=["p.Year == fiscalYear"],
         why=_ADVX + " รอบ 2 P1-2/P2-6/P2-7: ใบปิดปีปิดกำไรขาดทุนของฐานเดียวกับงบทดลอง · ล็อกงวดตามรอบบัญชี · ล็อกก่อนตรวจ \"ปิดแล้ว\""),
    dict(file=_YEC, method="LoadRollTrialBalanceAsync", must=["OpeningRoll.Combine(", "LatestOpeningAnchorAsync("],
         must_re=[r"entry\.EntryDate\s*>=\s*movementStart\.Value"],
         forbid=["account.AccountType == AccountType.Asset"],
         why=_ADVX + " P1-1 + รอบ 2 P1-2: ยอดยกมา = งบทดลองสิ้นรอบ (ทุกประเภท) แล้วยกเฉพาะ A/L/E · กำไรขาดทุนค้างเข้ากำไรสะสม"),
    dict(file=_YEC, method="RepairOpeningRollAsync", must=["EvaluateOpeningRollChainAsync(", "AddChainedAuditLog(", "OpeningRoll.LockKey("],
         must_lit=["pg_advisory_xact_lock", "opening-roll-dup-zeroed"],
         why=_ADVX + " P1-1: ซ่อมยอดที่เก็บผิดคิดใหม่ ณ ตอนกด + audit ทุกบัญชี (รวมแถวซ้ำที่ตั้งเป็นศูนย์ — รอบ 2 P2-7) + ล็อกต่อบริษัท"),
    # P1-2 ผังตั้งต้น: ลำดับความชอบตัวเดียว · ผังนำเข้าผูกบัญชีแม่ทุกทางเข้า
    dict(file=DOC, method="FindAccountAsync", must=["DefaultPostingCandidates(_db)", "OrderByGeneralPurposeFirst()"],
         forbid=[".OrderBy(a => a.AccountCode)", "WhereGeneralPurpose"],
         why=_ADVX + " P1-2: ลงลูกหนี้/เจ้าหนี้อัตโนมัติต้องไม่หยิบหัวบัญชีที่นำเข้า (\"1130\" ก่อน \"11310\")"),
    dict(file=IMPORT, method="ImportAccountAsync", must=["ChartParentLinker.LinkImportedAsync("],
         why=_ADVX + " P1-2: ผังที่นำเข้าจากไฟล์ผูกบัญชีแม่ (เดิมไม่ผูก ⇒ หัวบัญชีดูไม่มีบัญชีย่อย)"),
    dict(file="Services/Implementations/Migration/AccountImportAdapter.cs", method="ImportAsync", must=["ChartParentLinker.LinkImportedAsync("],
         before=[("ChartParentLinker.LinkImportedAsync(", "SaveChangesAsync(")],
         why=_ADVX + " P1-2: ผังจากโปรแกรมเดิม (Express/PEAK/FlowAccount) ผูกบัญชีแม่ทั้งชุดก่อนบันทึก"),
    # P2-4 ไฟล์โอนเงินเดือน: แหล่งจ่ายต้องเป็นธนาคารถึง "พร้อมโอน"
    # (PayrollExportSheets.BankTransferRows เป็น expression-bodied — checker อ่านไม่ได้ · ล็อกใน ModuleExportTests
    #  ไฟล์โอน_เลขบัญชีตัวเลขล้วน… — ถอด l.PaymentSourceIsBank ออก ⇒ แถว "พร้อมโอน" กลายเป็น "ยังไม่ได้เลือกแหล่งจ่าย" ⇒ เทสต์แดง)
    dict(file=PAYROLL, method="GetBankTransferSheetAsync", must=["MoneyAccountRule.GroupOf(", "PaymentSourceIsBank:"],
         why=_ADVX + " P2-4: ตัดสินว่าแหล่งจ่ายเป็นธนาคารด้วยกติกาเดียวกับ dropdown แหล่งเงิน"),
    # P2-6 GL/TB ตัวกรองเดียว + ฐานยอดยกมาเดียว
    dict(file=_ACC, method="GetGeneralLedgerAsync", must=["ResolveLedgerFilterAsync(", "LoadLedgerBaseAsync(", "LoadLedgerSliceAsync(", "SumLedgerAsync(",
                                                           "LedgerFilterRule.PnlWindowStart("],
         forbid=["effectiveDimId", "ApplyLedgerLineFilters("],
         why=_ADVX + " P2-6: บัญชีแยกประเภทกับงบทดลองใช้ตัวกรอง/ฐานชุดเดียว · รอบ 2 P2-5: ส่วนที่ปัน + รายได้/ค่าใช้จ่ายเริ่มต้นรอบเมื่อกรอง"),
    dict(file=_ACC, method="GetTrialBalanceAsync", must=["ResolveLedgerFilterAsync(", "LoadLedgerBaseAsync(", "SumLedgerAsync(",
                                                          "LedgerFilterRule.PnlWindowStart(", "LedgerFilterRule.PriorPnlRowName"],
         forbid=["l.BranchId == branchId.Value", "postedLines", "ApplyLedgerLineFilters("],
         why=_ADVX + " P2-6 + รอบ 2 P2-5: งบทดลองห้ามเขียนเงื่อนไขสาขา/มิติเอง · รวมยอดในฐาน (เดิม ToList ทั้งสมุดเมื่อกรอง) · กรอง ⇒ ลงตัวข้ามปี"),
    dict(file=_ACC, method="GetProfitAndLossAsync", must=["ResolveLedgerFilterAsync(", "SumLedgerAsync("],
         forbid=["postedLines", "ApplyLedgerLineFilters("],
         why=_ADVX + " รอบ 2 P2-5: งบกำไรขาดทุนกรองมิติ = ส่วนที่ปัน (เท่ารายงานตามมิติ) + รวมยอดในฐาน"),
    dict(file=_ACC, method="GetCashFlowStatementAsync", must=["ResolveLedgerFilterAsync(", "LoadLedgerSliceAsync("],
         forbid=["ApplyLedgerLineFilters("],
         why=_ADVX + " รอบ 2 P2-5: งบกระแสเงินสดกรองมิติใช้ส่วนที่ปันชุดเดียวกัน"),
    dict(file=_ACC, method="LoadAllocationSharesAsync", must=["LedgerFilterRule.AllocationShare("],
         why=_ADVX + " รอบ 2 P2-5: สัดส่วนที่ปันคิดด้วยตัวคำนวณเดียวกับรายงานตามมิติ"),
    dict(file="Services/Implementations/DimensionalAccountingService.cs", method="GetDimensionPnLAsync", must=["LedgerFilterRule.AllocationShare("],
         forbid=["l.AllocatedAmount ??"],
         why=_ADVX + " รอบ 2 P2-5: รายงานตามมิติ = งบกรองมิติ (ปัน 30/70 เดิมได้ 100% ทั้งคู่)"),
    dict(file=_ACC, method="GetAccountBalanceAsOfAsync", must=["LoadLedgerBaseAsync("],
         why=_ADVX + " P2-6: ยอด ณ วันที่ (แดชบอร์ด/กระแสเงินสด) ใช้ฐานเดียวกับงบทดลอง"),
    # P2-7 ปิดบังใบที่ไม่มีสิทธิ์เห็น: หน้า + ไฟล์บัญชีแยกประเภทผ่านตัวเดียว
    dict(file="Controllers/AccountingController.cs", method="GetGeneralLedger", must=["GetGeneralLedgerForUserAsync("],
         why=_ADVX + " P2-7: หน้าบัญชีแยกประเภทปิดบังใบเงินเดือนแบบเดียวกับสมุดรายวัน"),
    dict(file="Services/Implementations/AccountingReportExportService.cs", method="GeneralLedgerAsync", must=["GetGeneralLedgerForUserAsync("],
         forbid=["_accounting.GetGeneralLedgerAsync("],
         why=_ADVX + " P2-7: ไฟล์บัญชีแยกประเภทปิดบังใบเงินเดือนแบบเดียวกับหน้า"),
    dict(file=_ACC, method="GetGeneralLedgerForUserAsync", must=["GetVisibleKindsAsync(", "LedgerRedaction.Redact("],
         why=_ADVX + " P2-7: ตัวตัดสินสิทธิ์เดียวกับสมุดรายวัน + กติกาปิดบังตัวเดียว"),
    # P2-8 บันทึกการใช้งาน: วันที่ล้วน = วันตามเวลาไทย
    dict(file="Services/Implementations/AuditTrailService.cs", method="GetLogsAsync", must=["ThaiDate.InstantFromUtc(", "ThaiDate.InstantToUtc("],
         forbid=["to.AddDays(1)"],
         why=_ADVX + " P2-8: \"ถึง 14 ต.ค.\" = ถึงเที่ยงคืนเวลาไทย (เดิมเที่ยงคืน UTC ⇒ รายการ 7 ชม. แรกของวันถัดไปหลุดเข้ามา)"),
    # P2-10 JE ตอนออกใบ: เลือกด้วยชนิดสมุด ไม่ใช่ "ตัวแรกตามเวลาสร้าง"
    dict(file="Helpers/OriginalArApAccount.cs", method="LoadAsync", must=["PickIssueEntry(", "ClearingAccountId", "ReserveAccountId"],
         forbid=[".OrderBy(j => j.CreatedAt)"],
         why=_ADVX + " P2-10: JE รับชำระที่ลงย้อนหลังถูกนับเป็น JE ตอนออกใบ · ผังพัก/ผังกันเงินของ gateway ไม่ใช่ลูกหนี้ของใบ"),
    # ── รอบ 2 ──
    dict(file="Helpers/ChartAccountReferences.cs", method="Referrers", must=["GetReferencingForeignKeys()"],
         why=_ADVX + " รอบ 2 P1-4: \"ผังถูกอ้างไหม\" ไล่ทุก FK ของโมเดล (ย้อนนำเข้า + ผูกบัญชีแม่ใช้ตัวเดียว)"),
    dict(file="Services/Implementations/MoneyAccounts/ChartParentLinker.cs", method="LoadInUseAccountIdsAsync",
         must=["ChartAccountReferences.ReferencedAccountIdsAsync(", "ChartParentLink.InUseCheckCandidates("],
         before=[("ChartParentLink.InUseCheckCandidates(", "ChartAccountReferences.ReferencedAccountIdsAsync(")],
         forbid=["db.JournalEntryLines"],
         why=_ADVX + " รอบ 2 P1-4: ผังที่มียอดยกมา/ถูกปักเป็นค่าตั้งไม่ถูกทำเป็นบัญชีคุม (เดิมดูแค่บรรทัด JE)"),
    dict(file="Services/Implementations/MoneyAccounts/ChartParentLinker.cs", method="LinkImportedAsync",
         must=["ChartParentLink.Relevel(", "AddChainedAuditLog("],
         why=_ADVX + " รอบ 2 P2-8: ย้ายแม่ตอนนำเข้ามี audit + ลูกหลานปรับระดับตาม"),
    dict(file="Services/Implementations/MoneyAccounts/ChartParentLinker.cs", method="RepairAsync",
         must=["ChartParentLink.Relevel(", "AddChainedAuditLog("],
         why=_ADVX + " รอบ 2 P2-8: ผูกหัวบัญชีแล้วลูกหลานปรับระดับตาม + audit"),
    # (PostingAccountGuard.DefaultFallbackCandidates / AccountingService.LatestOpeningAnchorAsync เป็น expression-bodied — checker อ่านไม่ได้ ·
    #  ล็อกด้วย Db/AdversarialExportRound2DbTests ผังของผู้ใช้_… และ ย้ายระบบกลางปี_…)
    dict(file="Controllers/ConsolidationController.cs", method="DenyUnlessReadableAsync", must=["CanReadFinancialsAsync(m.CompanyId)", "CanReadFinancialsAsync(companyId)"],
         why=_ADVX + " รอบ 2 P2-10: งบรวมต้องมีสิทธิ์งบการเงินทั้งบริษัทแม่และทุกสมาชิก (เดิมไม่มีด่านสิทธิ์)"),
    dict(file="Controllers/ConsolidationController.cs", method="AddMember", must=["CanReadFinancialsAsync(request.CompanyId)"],
         why=_ADVX + " รอบ 2 P2-10: เพิ่มสมาชิกต้องมีสิทธิ์งบการเงินในบริษัทนั้น"),
    # ── รอบ 3 (ฝ่ายค้านบน de823512) ──
    dict(file=_ACC, method="RebuildMissingLinesAsync", must=["DefaultFallbackCandidates(_db)"],
         why=_ADVX + " รอบ 3 P1/P2-7: เครื่องมือซ่อมเลือกผังด้วยลำดับผังตั้งต้นตัวเดียว (เดิม FirstOrDefault ไม่เรียง)"),
    dict(file="Helpers/AccountingReportSheets.cs", method="BuildTrialBalanceWorksheet", must=["LedgerFilterRule.IsPriorPnlRow("],
         why=_ADVX + " รอบ 3 P2-1: แถวกำไรขาดทุนก่อนรอบของงบทดลองกรอง = ยอดยกมา (เดิมผลต่างเท็จทุกไฟล์หลังปิดปีแรก)"),
    # รอบ 4: ตัวตรวจผังที่ลบย้ายไป LoadRollTrialBalanceAsync (ใบปิดปีเรียกก่อนสร้างใบ) · ComputeRolledOpeningsAsync ต้องผ่านตัวนั้น
    dict(file=_YEC, method="ComputeRolledOpeningsAsync", must=["LoadRollTrialBalanceAsync(", "OpeningRoll.CarryForward(", "tb.DeletedIds"],
         before=[("LoadRollTrialBalanceAsync(", "OpeningRoll.CarryForward(")],
         forbid=["join account in _db.ChartOfAccounts"],
         why=_ADVX + " รอบ 3 P2-6 + รอบ 4 P2-b: ยกยอดผ่านตัวตรวจผังที่ลบตัวเดียว · CarryForward รู้ชุดผังที่ลบ (กันผู้เรียกลืม)"),
    dict(file=_YEC, method="LoadRollTrialBalanceAsync",
         must=["OpeningRoll.UnknownAccountsWithBalance(", "OpeningRoll.DeletedWithBalanceMessage(", "DeletedAccountsWithBalanceException(", "IgnoreQueryFilters()"],
         forbid=["join account in _db.ChartOfAccounts"],
         why=_ADVX + " รอบ 3 P2-6 + รอบ 4 P2-b: ผังสินทรัพย์/หนี้สิน/ทุนที่ถูกลบแต่ยังมียอด = ล้มดังพร้อมชื่อผัง · ประเภทของผังที่ลบต้องรู้ (รายได้/ค่าใช้จ่ายรวมเข้ากำไรสะสม)"),
    dict(file=_YEC, method="EvaluateOpeningRollChainAsync",
         must=["FindLegacyCalendarRollPeriodsAsync(", "OpeningRoll.LegacyRepairYear(", "legacyPeriodIds", "DeletedAccountsWithBalanceException"],
         why=_ADVX + " รอบ 3 P2-4 + รอบ 4: ยอดที่ควรเป็นไม่ใช้งวดยกยอดรุ่นเก่าไป ม.ค. เป็นฐาน + รายงานอธิบาย · ตัวตรวจ/ปีที่ซ่อมตัวเดียวกับด่านปิดปี"),
    dict(file=_YEC, method="FindLegacyCalendarRollPeriodsAsync", must=["OpeningRoll.IsLegacyCalendarRollPeriod("],
         why=_ADVX + " รอบ 4 P2-a: ตัวตรวจงวดยกยอดรุ่นเก่าไป ม.ค. ตัวเดียว (รายงาน/ซ่อม + ด่านปิดปี/ยกยอดด้วยมือ)"),
    dict(file=_YEC, method="RepairOpeningRollAsync", must=["OpeningRoll.IsRolled(", "OpeningRoll.LegacyJanNeutralizedAction", "AddChainedAuditLog("],
         why=_ADVX + " รอบ 3 P2-4: ซ่อมยกเลิกแถวยกยอดรุ่นเก่าของงวด ม.ค. เฉพาะแถวที่การปิดปีเขียน + audit"),
    dict(file=_YEC, method="YearEndCloseAsync", must=["AnchorPnlScope.PriorYears", "NetClosingRows(", "priorPerAccount"],
         why=_ADVX + " รอบ 3 P2-3: P&L ของฐานตรงต้นรอบ = ปีก่อน — แยกบรรทัด ไม่นับในกำไรของปี"),
    dict(file="Services/Implementations/MoneyAccounts/ChartParentLinker.cs", method="RescueTemplateLeafChildrenAsync",
         must=["ChartParentLink.Relevel(", "AddChainedAuditLog(", "ChartParentLink.LeafRescueAction"], must_lit=["pg_advisory_xact_lock"],
         why=_ADVX + " รอบ 3 P2-7: ย้ายผังออกจากใต้ผังลงรายการของแม่แบบ — ลูกหลานปรับระดับ + audit + ล็อกเดียวกับผูกแม่"),
    # ── รอบ 4 (ฝ่ายค้านบน 774ea7bc) ──
    dict(file=_YEC, method="EnsureNoUnrepairedLegacyRollAsync",
         must=["FindLegacyCalendarRollPeriodsAsync(", "OpeningRoll.LegacyRepairYear(", "OpeningRoll.LegacyRollBlockMessage("],
         must_re=[r"throw new BusinessRuleException\(OpeningRoll\.LegacyRollBlockMessage\("],
         why=_ADVX + " รอบ 4 P2-a: งวดยกยอดรุ่นเก่าไป ม.ค. ที่ซ่อมได้แล้ว ⇒ ปิดปี/ยกยอดล้มดังพร้อมทางไปต่อ (เดิมคิดบนฐานนั้น ⇒ ยอดยกมาคลาด)"),
    dict(file=_YEC, method="YearEndCloseAsync",
         must=["EnsureNoUnrepairedLegacyRollAsync(", "LoadRollTrialBalanceAsync(", "CreateJournalEntryCoreAsync(", "legacyAnchorIds",
               "_db.ChartOfAccounts.IgnoreQueryFilters()"],
         before=[("EnsureNoUnrepairedLegacyRollAsync(", "LatestOpeningAnchorAsync("),
                 ("EnsureNoUnrepairedLegacyRollAsync(", "CreateJournalEntryCoreAsync("),
                 ("LoadRollTrialBalanceAsync(", "CreateJournalEntryCoreAsync(")],
         forbid=["_db.ChartOfAccounts on "],
         why=_ADVX + " รอบ 4 P2-a/P2-b: ด่านยกยอดรุ่นเก่า + ผังที่ลบยังมียอด ต้องล้มก่อนสร้างใบปิดปี (ข้อความบอกว่ายังไม่บันทึก) · "
             "ใบปิดปีล้างผังรายได้/ค่าใช้จ่ายที่ถูกลบ (join ผังแบบไม่กรอง soft-delete)"),
    dict(file=_YEC, method="RollOpeningBalancesAsync", must=["EnsureNoUnrepairedLegacyRollAsync(", "RollIntoNextFiscalYearAsync("],
         before=[("EnsureNoUnrepairedLegacyRollAsync(", "RollIntoNextFiscalYearAsync(")],
         why=_ADVX + " รอบ 4 P2-a: ยกยอดด้วยมือเดินด่านยกยอดรุ่นเก่าเดียวกับปิดปี"),
    dict(file=_YEC, method="RollIntoNextFiscalYearAsync", must=["ComputeRolledOpeningsAsync(companyId, next.Start, retainedEarningsAccountId, null, legacyAnchorIds)"],
         why=_ADVX + " รอบ 4 P2-a: ยกยอดไม่ใช้งวดรุ่นเก่าเป็นฐาน (เกณฑ์เดียวกับยอดที่ควรเป็นของรายงาน)"),
    dict(file=_YEC, method="RepairOpeningRollAsync", must=["ev.Report.HasImportedRows", "ev.LegacyRowIds.Count > 0"],
         why=_ADVX + " รอบ 4 P2-d: งวดแรกของรอบมียอดนำเข้า ⇒ ยังยกเลิกแถวยกยอดรุ่นเก่าของงวด ม.ค. ได้ (แถวนำเข้าไม่ถูกแตะ)"),
    dict(file=_ACC, method="ValidateJournalReferencesAsync", must=["allowDeletedAccountIds.Contains("],
         why=_ADVX + " รอบ 4 P2-b: ยอมผังที่ถูกลบเฉพาะชุดที่ใบปิดปีส่งมา (ทางอื่นยังล้ม NotFound)"),
    dict(file="Services/Implementations/AccountingService.ChartLinks.cs", method="RestoreAccountAsync",
         must=["AddChainedAuditLog(", "OpeningRoll.AccountRestoreAction", "IgnoreQueryFilters()"], must_lit=["pg_advisory_xact_lock"],
         why=_ADVX + " รอบ 4 P2-b: กู้คืนผังที่ถูกลบ = ทางไปต่อจริงของ \"ผังที่ลบยังมียอด\" · audit chain + ล็อกเดียวกับต้นไม้ผัง + ตรวจรหัสซ้ำ/ผังแม่ที่ลบ"),
    dict(file="Controllers/AccountingController.cs", method="RestoreAccount", must=["EnsureOwnerAccessAsync(", "RestoreAccountAsync("],
         before=[("EnsureOwnerAccessAsync(", "RestoreAccountAsync(")],
         why=_ADVX + " รอบ 4 P2-b: กู้คืนผัง = เจ้าของบริษัทเท่านั้น (สิทธิ์เดียวกับปิดปี)"),
    dict(file="Services/Implementations/DataIntegrityReportService.cs", method="LoadChartRepairSuspectsAsync",
         must=["AccountRestoreFixPath(", "y.DeletedAccounts", "y.LegacyRolledRows > 0"],
         why=_ADVX + " รอบ 4 P2-b/P2-d: รายงานมีปุ่มกู้คืนรายผัง + แถวยกยอดรุ่นเก่าของปีที่มียอดนำเข้า (เดิมข้ามทั้งปี)"),
    dict(file="Services/Implementations/MoneyAccounts/ChartParentLinker.cs", method="PlanLeafRescueAsync",
         must=["ImportLinkedAccountIdsAsync(", "underLeaf.Where(evidenced.Contains)", "ChartAccountReferences.ReferencedAccountIdsAsync("],
         before=[("ImportLinkedAccountIdsAsync(", "ChartAccountReferences.ReferencedAccountIdsAsync(")],
         why=_ADVX + " รอบ 4 P2-c: รายงาน/ปุ่มย้ายผังใต้ผังลงรายการของแม่แบบ เฉพาะผังที่มีหลักฐานว่าระบบผูก (ผู้ใช้สร้างเอง = ไม่แตะ)"),
    dict(file="Services/Implementations/MoneyAccounts/ChartParentLinker.cs", method="ImportLinkedAccountIdsAsync",
         must=["ChartParentLink.IsImportCreated(", "ChartParentLink.ParentLinkAuditMarker", "ImportRowAction.Create", "a.CompanyId == companyId",
               "r.CompanyId == companyId"],
         why=_ADVX + " รอบ 4 P2-c: หลักฐานสามทาง (สร้างจากการนำเข้า · แถวพักที่สร้างผัง · audit การผูก) — แถวพักแบบอัปเดตผังเดิมไม่ใช่หลักฐาน"),
    dict(file=IMPORT, method="ImportAccountAsync", must=["ChartParentLink.FileImportActor"],
         why=_ADVX + " รอบ 4 P2-c: ผังที่นำเข้าจากไฟล์ประทับเครื่องหมายผู้สร้าง (หลักฐานว่าแม่มาจากการผูกของระบบ)"),
    # ── รอบ 5 (ฝ่ายค้านบน eec6947d) ──
    # P1: บรรทัดของผังที่ถูกลบต้องไม่หายจากการแสดง/กลับรายการ/คัดลอก/ส่งออก/ปรับปรุงใบเดียว (ThenInclude ผัง = INNER JOIN ผ่านตัวกรอง soft-delete)
    dict(file=_ACC, method="GetJournalEntryAsync", must=["JournalLineAccounts.HydrateAsync("], forbid=["ThenInclude(l => l.Account)"],
         why=_ADVX + " รอบ 5 P1: เปิดดูใบสำคัญเห็นบรรทัดผังที่ลบ (ใบปิดปีเคยโชว์ Dr 8,000/Cr 7,000)"),
    dict(file=_ACC, method="GetJournalEntriesAsync", must=["JournalLineAccounts.HydrateAsync("], forbid=["ThenInclude(l => l.Account)"],
         why=_ADVX + " รอบ 5 P1: รายการสมุดรายวันเห็นบรรทัดผังที่ลบ"),
    dict(file=_ACC, method="PostJournalEntryAsync", must=["JournalLineAccounts.HydrateAsync("], forbid=["ThenInclude(l => l.Account)"],
         why=_ADVX + " รอบ 5 P1: คำตอบ post ไม่ตัดบรรทัดผังที่ลบ"),
    dict(file=_ACC, method="ReverseJournalEntryAsync",
         must=["ClosingEntryLock.EnsureNotClosingEntriesAsync(", "JournalLineAccounts.ReversalMismatch(", "_db.JournalEntryLines.AsNoTracking()"],
         must_re=[r"throw new Accounting\.Helpers\.BusinessRuleException\(reversalMismatch"],
         before=[("ClosingEntryLock.EnsureNotClosingEntriesAsync(", "BeginTransactionAsync("),
                 ("JournalLineAccounts.ReversalMismatch(", "GetNextEntryNumberAsync(")],
         forbid=["ThenInclude(l => l.Account)", "if (!systemTriggered) await Accounting.Helpers.ClosingEntryLock"],
         why=_ADVX + " รอบ 5 P1: ใบปิดปีกลับรายการไม่ได้ทุกทาง (รวมทางระบบ) · ตัวกลับ = บรรทัดที่บันทึกไว้ครบและลงตัว ก่อนเขียนอะไร"),
    dict(file=_ACC, method="CorrectJournalEntryAsync",
         must=["JournalLineAccounts.HydrateAsync(", "ClosingEntryLock.EnsureNotClosingEntriesAsync(", "l.Account.IsDeleted"],
         before=[("l.Account.IsDeleted", "ReverseJournalEntryAsync(")], forbid=["ThenInclude(l => l.Account)"],
         why=_ADVX + " รอบ 5 P1: ร่างที่คัดลอกลงผังที่ลบไม่ได้ ⇒ ล้มก่อนกลับรายการ (เดิมกลับแล้วค่อยล้ม)"),
    dict(file=_ACC, method="VoidJournalEntryAsync", must=["ClosingEntryLock.EnsureNotClosingEntriesAsync("],
         before=[("ClosingEntryLock.EnsureNotClosingEntriesAsync(", "ValidateFiscalPeriodOpenAsync(")],
         why=_ADVX + " รอบ 5 P1: ยกเลิกใบปิดปีไม่ได้ (บอกเหตุที่แท้จริงก่อนด่านงวด)"),
    dict(file=_ACC, method="DeleteJournalEntryAsync", must=["ClosingEntryLock.EnsureNotClosingEntriesAsync("],
         before=[("ClosingEntryLock.EnsureNotClosingEntriesAsync(", "IsDeleted = true")],
         why=_ADVX + " รอบ 5 P1: ลบใบปิดปีไม่ได้ (YearEndClosing ชี้ใบที่ถูกลบ)"),
    dict(file=_ACC, method="BatchVoidJournalEntriesAsync", must=["ClosingEntryLock.EnsureNotClosingEntriesAsync("],
         before=[("ClosingEntryLock.EnsureNotClosingEntriesAsync(", "ReverseJournalEntryAsync(")],
         why=_ADVX + " รอบ 5 P1: ยกเลิกหลายใบตรวจทั้งชุดก่อนเริ่ม (ไม่กลับครึ่งชุดแล้วค่อยล้มที่ใบปิด)"),
    dict(file=_ACC, method="BatchDeleteJournalEntriesAsync", must=["ClosingEntryLock.EnsureNotClosingEntriesAsync("],
         before=[("ClosingEntryLock.EnsureNotClosingEntriesAsync(", "IsDeleted = true")],
         why=_ADVX + " รอบ 5 P1: ลบหลายใบไม่ตรวจงวดเลย ⇒ เคยลบใบปิดปีทิ้งได้"),
    dict(file="Services/Implementations/IntegrationService.cs", method="ProcessJournalReverseAsync", must=["ClosingEntryLock.EnsureNotClosingEntriesAsync("],
         before=[("ClosingEntryLock.EnsureNotClosingEntriesAsync(", "_db.JournalEntries.Add(reversal)")],
         why=_ADVX + " รอบ 5 P1: ระบบภายนอกกลับใบปิดปีไม่ได้ (ด่านเดียวกับสมุดรายวัน)"),
    dict(file="Services/Implementations/DocumentService.cs", method="GetDocumentJournalEntriesAsync", must=["JournalLineAccounts.HydrateAsync("],
         forbid=["ThenInclude(l => l.Account)"], why=_ADVX + " รอบ 5 P1: แผงรายการบัญชีของเอกสารเห็นบรรทัดผังที่ลบ"),
    dict(file="Services/Implementations/DocumentService.cs", method="AdjustDocumentJournalEntryAsync", must=["JournalLineAccounts.HydrateAsync("],
         forbid=["ThenInclude(l => l.Account)"], why=_ADVX + " รอบ 5 P1: ผลต่างของใบปรับปรุงคิดจากบรรทัดครบ (เดิมบรรทัดผังที่ลบหาย ⇒ ใบปรับปรุงผิด)"),
    dict(file=IMPORT, method="ExportJournalEntriesAsync", must=["JournalLineAccounts.HydrateAsync("], forbid=["ThenInclude(l => l.Account)"],
         why=_ADVX + " รอบ 5 P1: ไฟล์ส่งออกสมุดรายวันมีบรรทัดผังที่ลบ (ใบไม่ขาด)"),
    dict(file="Helpers/JournalLineAccounts.cs", method="HydrateAsync", must=["IgnoreQueryFilters()", "a.CompanyId == companyId"],
         why=_ADVX + " รอบ 5 P1: เติมผังรวมผังที่ลบ — กรองบริษัทเสมอ (กฎ M)"),
    dict(file="Helpers/ClosingEntryLock.cs", method="EnsureNotClosingEntriesAsync",
         must=["IsClosingEntry", "ClosingJournalEntryId", "y.CompanyId == companyId", "j.CompanyId == companyId", "BlockMessage("],
         why=_ADVX + " รอบ 5 P1: ใบปิดบัญชี = ธง IsClosingEntry หรือถูกอ้างโดย YearEndClosing (บริษัทนี้)"),
    # P2-2: เดือนเริ่มรอบบัญชีล็อกหลังปิดปี + ตัวตรวจยกยอดรุ่นเก่าใช้หลักฐานเวลา/ประวัติ
    dict(file="Services/Implementations/CompanyService.cs", method="UpdateAsync", must=["ApplyFiscalYearStartMonthAsync("],
         forbid=["company.FiscalYearStartMonth = request.FiscalYearStartMonth"],
         why=_ADVX + " รอบ 5 P2-2: เปลี่ยนเดือนเริ่มรอบผ่านด่านเดียว (เดิมเขียนตรงไม่ตรวจอะไร)"),
    dict(file="Services/Implementations/CompanyService.cs", method="ApplyFiscalYearStartMonthAsync",
         must=["YearEndClosings", "OpeningRoll.FiscalYearStartMonthLockedMessage(", "AddChainedAuditLog(", "OpeningRoll.FiscalYearStartMonthChangeAction",
               "y.CompanyId == company.Id"],
         before=[("OpeningRoll.FiscalYearStartMonthLockedMessage(", "AddChainedAuditLog(")],
         why=_ADVX + " รอบ 5 P2-2: ปิดปีแล้วเปลี่ยนเดือนเริ่มรอบไม่ได้ (พ.ร.บ.การบัญชี ม.11) · ก่อนปิดปี = เปลี่ยนได้ + audit เป็นหลักฐานของตัวตรวจ"),
    dict(file=_YEC, method="FindLegacyCalendarRollPeriodsAsync",
         must=["OpeningRoll.IsLegacyCalendarRoll(", "LoadFiscalYearStartMonthChangesAsync(", "OpeningRoll.LegacyRepairYear(", "YearEndClosings"],
         why=_ADVX + " รอบ 5 P2-2: งวด ม.ค. ที่ยกตอนบริษัทยังเป็นปีปฏิทิน (ปิดหลังการแก้ / audit ค่าเดิม = 1) ไม่ใช่รุ่นเก่า — ไม่บล็อก/ไม่ซ่อมทับประวัติ"),
    dict(file=_YEC, method="LoadFiscalYearStartMonthChangesAsync",
         must=["a.CompanyId == companyId", "OpeningRoll.FiscalYearStartMonthChangeAction"],
         why=_ADVX + " รอบ 5 P2-2: ประวัติเปลี่ยนเดือนเริ่มรอบของบริษัทนี้เท่านั้น"),
    # P2-3: กู้คืนผังไม่ทำผังแม่ที่เป็นผังลงรายการให้กลายเป็นบัญชีคุม
    dict(file="Services/Implementations/AccountingService.ChartLinks.cs", method="RestoreAccountAsync",
         must=["PlanRestoreUnderAsync(", "RestoreBlockedMessage(", "AncestorIdsAsync(", "RelevelRestoredAsync("],
         before=[("PlanRestoreUnderAsync(", "account.IsDeleted = false")],
         why=_ADVX + " รอบ 5 P2-3: ผังแม่ที่กลายเป็นผังลงรายการ (ถูกใช้/ผังระบบ) รับลูกที่กู้คืนไม่ได้ ⇒ ล้มก่อนเขียน + เสนอหัวบัญชี · กู้คืนใต้หัวบัญชีอื่นได้"),
    dict(file="Services/Implementations/AccountingService.ChartLinks.cs", method="PlanRestoreUnderAsync",
         must=["ChartParentLink.RestoreParentBlockedReason(", "ChartParentLink.RestoreAlternativeParent(", "ChartAccountReferences.ReferencedAccountIdsAsync(",
               "a.CompanyId == companyId"],
         why=_ADVX + " รอบ 5 P2-3: ตัวตัดสินเดียวของการกู้คืน (บริการ + หน้าตรวจข้อมูล)"),
    dict(file="Controllers/AccountingController.cs", method="RestoreAccount",
         must=["RestoreAccountAsync(companyId, accountId, userId.ToString(), newParentId)"],
         why=_ADVX + " รอบ 5 P2-3: ปุ่มหน้าตรวจข้อมูลส่งหัวบัญชีที่เสนอ (newParentId) ถึงบริการจริง"),
    dict(file="Services/Implementations/DataIntegrityReportService.cs", method="LoadChartRepairSuspectsAsync",
         must=["PlanAccountRestoreAsync(", "plan.SuggestedParentId"],
         why=_ADVX + " รอบ 5 P2-3: ปุ่มกู้คืนใช้แผนเดียวกับบริการ (ผังแม่รับไม่ได้ ⇒ ปุ่มกู้คืนใต้หัวบัญชีที่เสนอ / ไม่มีปุ่ม + เหตุผล)"),
    # ── ยกเลิกการปิดปี (คำสั่งเจ้าของ 2026-10-16 · DECISIONS ข้อ 152 · Helpers/YearEndReopenRule) ──
    dict(file=_YEC, method="ReopenYearEndAsync",
         must=["OpeningRoll.LockKey(", "YearEndReopenRule.Evaluate(", "throw new YearEndReopenException(", "FindLegacyCalendarRollPeriodsAsync(",
               "YearEndReopenRule.IsFiledReturn(", "JournalLineAccounts.ReversalMismatch(", "CreateJournalEntryCoreAsync(", "OpeningRoll.IsRolled(",
               "AddChainedAuditLog(", "y.CompanyId == companyId", "f.CompanyId == companyId", "o.CompanyId == companyId", "p.CompanyId == companyId",
               "a.CompanyId == companyId", "reversal.IsClosingEntry = true", "original.Status = JournalEntryStatus.Reversed"],
         must_lit=['"SELECT pg_advisory_xact_lock({0})"'],
         must_re=[r"if\s*\(\s*!decision\.Allowed\s*\)\s*throw new YearEndReopenException\(decision\)"],
         before=[("OpeningRoll.LockKey(", "YearEndReopenRule.Evaluate("),
                 ("YearEndReopenRule.Evaluate(", "SaveChangesAsync("),
                 ("YearEndReopenRule.Evaluate(", "CreateJournalEntryCoreAsync("),
                 ("p.Status = FiscalPeriodStatus.Open", "CreateJournalEntryCoreAsync(")],
         forbid=["ReverseJournalEntryAsync(", "ThenInclude(l => l.Account)", "IgnoreQueryFilters().Where(o =>"],
         why="ยกเลิกการปิดปี (2026-10-16): ล็อกเดียวกับปิดปี/ยกยอด/ซ่อม → ตัวตัดสินเดียวก่อนเขียน → เปิดงวดก่อนสร้างใบกลับ · ใบกลับผ่านทางเฉพาะ"
             " (ไม่ผ่าน ReverseJournalEntryAsync ที่ ClosingEntryLock กันไว้) ติดธงใบปิด · ยกเลิกเฉพาะแถวยกยอด (แถวนำเข้าไม่แตะ) · audit ทุกแถว · กรองบริษัททุกคิวรี"),
    dict(file="Controllers/AccountingController.cs", method="ReopenYearEnd",
         must=["EnsureOwnerAccessAsync(companyId, userId)", "ReopenYearEndAsync(", "YearEndReopenOutcome.FiledNeedsAcknowledgement", "filedReturns"],
         before=[("EnsureOwnerAccessAsync(companyId, userId)", "ReopenYearEndAsync(")],
         why="ยกเลิกการปิดปี (2026-10-16 คำตอบเจ้าของข้อ 4): เจ้าของเท่านั้น — ด่านมาก่อน service · ยื่นแล้วไม่รับทราบ = 409 + filedReturns (คำตอบข้อ 1)"),
    dict(file=_YEC, method="YearEndCloseAsync",
         must=["YearEndCloseThroughSoftClosedAction", "AddChainedAuditLog("],
         before=[("closingDatePeriod.Status = FiscalPeriodStatus.Open", "CreateJournalEntryCoreAsync("),
                 ("CreateJournalEntryCoreAsync(", "p.Status = FiscalPeriodStatus.Locked")],
         why="ยกเลิกการปิดปี (2026-10-16) บั๊กข้างเคียง: งวดของวันที่ใบปิดปีที่ปิดชั่วคราว ⇒ เปิดใต้ล็อกเดียวกัน + audit ก่อนสร้างใบ แล้วล็อกในธุรกรรมเดียวกัน"),
    dict(file=_YEC, method="ReopenPeriodAsync", must=["YearEndReopenRule.LockedPeriodMessage("],
         why="ยกเลิกการปิดปี (2026-10-16): เปิดงวดเดี่ยวที่ล็อกจากการปิดปีไม่ได้ — ข้อความชี้ปุ่ม \"ยกเลิกการปิดปี\" (เดิมอ้างคำสั่งที่ไม่มีอยู่)"),
    dict(file=_YEC, method="GetYearEndClosingsAsync", must=["y.CompanyId == companyId", "YearEndReopenRule.Effects(", "YearEndReopenRule.LaterYearClosedMessage("],
         why="ยกเลิกการปิดปี (2026-10-16): หน้างวดบัญชีแสดงข้อความผลกระทบ/เหตุที่เปิดไม่ได้จากตัวตัดสินเดียว (หน้าเว็บไม่มีสำเนากติกา)"),
]

# ── 2026-10-16 (Makro หน้า 2/2 · ใบสำคัญจ่าย 11,846.50 ทั้งที่กระดาษยืนยัน 10,980): ยอดเอกสารห้ามเกินยอดรวมที่กระดาษยืนยัน ──────────
#    ตัวตัดสิน Helpers/OcrTotalAnchor.DocumentOverstatement (pure + OcrMakroPage2VatAddedTwiceTests) · ถอดการเรียกที่ตัวสร้างบรรทัด =
#    สแกนใหม่/เก่าที่หัวสแกนเกินกระดาษไม่มี [Σ-GAP] · ถอดที่ด่านอนุมัติ = เอกสารที่สร้างไปแล้ว/บรรทัดที่ผู้ใช้แยกเองผ่านเงียบ
RULES += [
    dict(file=OCR, method="BuildScanLinesAsync",
         must=["OcrTotalAnchor.DocumentOverstatement(", "OcrLineBuildNotes.GapTag"],
         must_re=[r"overstated\s*\.\s*Message"],
         before=[("OcrLineBuildNotes.StripRecomputed(", "OcrTotalAnchor.DocumentOverstatement(")],
         why="2026-10-16 Makro 2/2: ยอดบรรทัดที่สร้างเกินยอดรวมที่กระดาษยืนยัน ⇒ [Σ-GAP] (รับทราบตอนอนุมัติ · ไม่อนุมัติเอง) — ห้ามลงบัญชีเกินกระดาษเงียบ ๆ"),
    dict(file=DOCSVC, method="CollectApprovalWarningsAsync",
         must=["OcrTotalAnchor.DocumentOverstatement(", "OcrVatCodeLines.TableMismatch("],
         before=[("OcrTotalAnchor.DocumentOverstatement(", "OcrApprovalGapWarning.Build("),
                 ("OcrVatCodeLines.TableMismatch(", "OcrApprovalGapWarning.Build(")],
         # ฝ่ายค้าน afbb0956 P2-5: "ตรวจสดแล้ว" = ตัดสินได้จริง (OverstatedNow ไม่ใช่ null) — ไม่ใช่ true เสมอ · P2-4: หมายเหตุตารางรหัส ภ.พ. ผ่านผลสด
         call_args=[("OcrApprovalGapWarning.Build(", "OcrTotalAnchor.OverstatedNow"),
                    ("OcrApprovalGapWarning.Build(", "OcrVatCodeLines.LiveNotes")],
         why="2026-10-16 Makro 2/2: ตรวจสดบนบรรทัดเอกสารตอนอนุมัติ (ครอบเอกสารที่สร้างก่อนแก้ + บรรทัดที่ผู้ใช้แยกเอง) ทุกทางเข้าที่อนุมัติ"),
]

# ── ฝ่ายค้าน 6e56d33d P2-4: [Σ-GAP] "ยอดเอกสารเกินกระดาษ" ตอนสร้างค้างหยุดการอนุมัติจากแชทแม้ผู้ใช้แก้บรรทัดแล้ว ⇒ ปุ่มอนุมัติ LINE ตรวจสด ──
#    ถอดการตรวจสด = กลับไปหยุดตลอดกาล (เงียบ ไม่มีทางออก) · ไม่ส่ง overstatedNow = ตัวตัดสินอ่านหมายเหตุเก่าอย่างเดียว
RULES += [
    # ฝ่ายค้าน afbb0956 P2-5: ผลสามค่า OcrTotalAnchor.OverstatedNow (ตัดสินไม่ได้ ≠ ไม่เกิน) — ห้ามกลับไปแปลง DocumentOverstatement(...) is not null เป็น bool ·
    # P2-4: หมายเหตุ [Σ-GAP] ตารางรหัส ภ.พ. ตรวจสดผ่าน OcrVatCodeLines.LiveNotes
    dict(file=LINEBOT, method="HandlePostbackAsync",
         must=["OcrTotalAnchor.OverstatedNow(", "OcrPostingReadiness.Evaluate(", "OcrVatCodeLines.TableMismatch("],
         before=[("OcrTotalAnchor.OverstatedNow(", "OcrPostingReadiness.Evaluate("),
                 ("OcrVatCodeLines.TableMismatch(", "OcrPostingReadiness.Evaluate(")],
         call_args=[("OcrPostingReadiness.Evaluate(", "overstatedNow"), ("OcrPostingReadiness.Evaluate(", "OcrVatCodeLines.LiveNotes")],
         forbid=["OcrTotalAnchor.DocumentOverstatement("],
         why="ฝ่ายค้าน 6e56d33d P2-4 → afbb0956 P2-4/P2-5: อนุมัติจากแชทตัดสินข้อ \"เกินกระดาษ\" + ตารางรหัส ภ.พ. จากบรรทัดตอนนี้ (สามค่า) ไม่ใช่หมายเหตุตอนสร้าง"),
]

# ── 2026-10-16 รหัส ภ.พ. รายบรรทัด (คำสั่งเจ้าของ "อัตรา VAT ต่อบรรทัดยังเดาจากชื่อสินค้า … ควรต้องตามตาราง ภพ") ──────────────────────────
#    ตัวตัดสิน Helpers/OcrVatCodeLines (pure + OcrVatCodeLinesTests · golden makro-p1p2-*) · ถอด Plan = ใบผสมกลับไปเดาจากชื่อ ·
#    ถอดการใช้ AmountsAfterDiscount/DiscountPercents = ส่วนลดกลับไป % เดียวทั้งใบ (Σ กลุ่มยกเว้น ≠ ตาราง) · ถอด TableMismatch = ใบที่ไม่ลงตารางผ่านเงียบ ·
#    ลำดับ: หลังตัวกระทบยอด (ต้องรู้ราคารวม VAT/เป้าหลังส่วนลด) และก่อนตัวพิสูจน์ทั้งใบ/ตัวเดาจากชื่อ (ชั้นล่างกว่า)
RULES += [
    # 2026-10-16 คำตัดสินเจ้าของข้อ 1/3: ตัวกระทบยอด + แผนรหัส ภ.พ. ย้ายเข้าตัวตัดสินเดียว OcrVatCodeLines.Decide (รายการ / แถวส่วนลด→ส่วนลด /
    # ตารางชนะ) · ถอด Decide = ใบผสมกลับไปเดาจากชื่อ · ถอดการใช้ TableLines = รายการครึ่งใบ + ช่องว่างกลับมา · ถอดการใช้ Keep/BillDiscount =
    # แถวส่วนลดยอดบวกพองยอดรวม · ถอด lineSources ในพรีวิว = ป้าย VAT เลื่อนผิดแถว
    dict(file=OCR, method="BuildScanLinesAsync",
         must=["OcrVatCodeLines.Decide(", "OcrVatCodeLines.TableMismatch("],
         must_re=[r"vatCodePlan\s*\.\s*AmountsAfterDiscount", r"vatCodePlan\s*\.\s*DiscountPercents", r"vatCodePlan\s*\.\s*ReplaceEngineRate",
                  r"codeDecision\s*\.\s*TableLines", r"codeDecision\s*\.\s*Keep", r"codeDecision\s*\.\s*BillDiscount",
                  r"codeDecision\s*\.\s*Recon", r"tableLinesInsteadOfItems\s*\?\?", r"poLineMap\s*\.\s*TryGetValue\s*\(\s*srcIdx\s*\["],
         before=[("OcrVatCodeLines.Decide(", "OcrLineVatPlanner.PlanWholeInvoice("),
                 ("OcrVatCodeLines.Decide(", "ThaiVatTypeRule.Suggest(")],
         forbid=["OcrVatCodeLines.Plan(", "OcrLineReconciler.Classify("],
         why="2026-10-16 รหัส ภ.พ.: ตารางของกระดาษเป็นตัวตั้ง (รายการลงตาราง/แถวส่วนลดแปลงเป็นส่วนลด/ตารางชนะ) ก่อนการเดาจากชื่อ · ผลสุดท้ายไม่ลงตาราง = [Σ-GAP]"),
    dict(file=OCR, method="PreviewDocumentLinesAsync",
         must_re=[r"var\s+lineSources\s*=\s*await\s+BuildScanLinesAsync", r"lineSources\s*\["],
         why="2026-10-16: ดัชนีต้นทางของบรรทัดพรีวิวมาจากตัวสร้างบรรทัด (แถวส่วนลดที่แปลง/บรรทัดสรุปตามตาราง ไม่ใช่ 1 แถว = 1 บรรทัด)"),
]

# ── 2026-10-16 ทีม OCR 7 — Makro หน้า 2/2 สแกนซ้ำ ("ยอดรวมลงถูกแต่บอดราย รายการ … เห็นตารางสรุปรวม ก็ใช้ตารางนั้นได้เลย") + คำตัดสินเจ้าของภาคต่อ ──────
#    "ถ้าตารางสินค้าตรงกับยอดรวม ยึด รายการสินค้าทั้งหมด · แต่ถ้าไม่ตรงและตารางภพ ตรงกับยอดรวม ให้ยึดตาราง ภพ"
#    ถอด paperTotal = สแกนเก่าที่หัวสแกนผิด (11,846.50) ตารางแพ้ · ถอด MixedDimensionsNote = ผัง/โครงการรายการเดิมหายเงียบ · ถอด rawText ใน Plan = ยอดก่อน VAT ค้าง 11,208 ·
#    กลับไปเรียก KeepItemsReason = เลขหน้าครบ/ผังต่างกันกลับมาบล็อกตาราง (ภาพหน้าจอของผู้ใช้) · ถอด AllDiscountRows = หน้าที่มีแต่ส่วนลดได้หมายเหตุ "รายการสินค้าถูกแทน" ผิด
RULES += [
    dict(file=OCR, method="BuildScanLinesAsync",
         must=["OcrTotalAnchor.Find(", "OcrVatCodeLines.Decide("],
         must_re=[r"paperTotal\s*:\s*paperProven\s*\?\s*paperAnchor\s*\.\s*Total", r"paperVat\s*:\s*paperProven\s*\?\s*paperAnchor\s*\.\s*PrintedVat",
                  r"codeDecision\s*\.\s*MixedDimensionsNote\s+is\s+string", r"AccountLabel\s*:\s*ItemAccountLabel\(\s*x\s*\)"],
         before=[("OcrTotalAnchor.Find(", "OcrVatCodeLines.Decide(")],
         why="2026-10-16 คำตัดสินเจ้าของภาคต่อ: ด่าน \"ตารางลงยอด\" เทียบยอดที่กระดาษพิสูจน์ · ผัง/โครงการต่างกัน = หมายเหตุรายการเดิม (ไม่ทิ้งเงียบ)"),
    dict(file=OCR, method="ApplyTotalFirst",
         must=["OcrTotalAnchor.Plan("],
         call_args=[("OcrTotalAnchor.Plan(", "text")],
         why="2026-10-16 Makro 2/2: ยอดก่อน VAT ที่พิมพ์ว่ารวม VAT (11,208) ต้องถูกแทนด้วยฐานที่พิมพ์ — Plan ต้องได้ข้อความกระดาษ"),
    dict(file="Helpers/OcrVatCodeLines.cs", method="Decide",
         must=["AllDiscountRows(", "SummaryInsteadOfItems(", "MixedDimensionsNote(", "LargestShare("],
         call_args=[("SummaryInsteadOfItems(", "paperTotal")],
         forbid=["KeepItemsReason("],
         why="2026-10-16 คำตัดสินเจ้าของภาคต่อ: รายการลงยอด ⇒ รายการ · ไม่ลงแต่ตารางลงยอด ⇒ ตาราง (ไม่ดูเลขหน้า · ไม่บล็อกเพราะผัง/โครงการ) · ไม่ลงทั้งคู่ ⇒ รายการ + [Σ-GAP]"),
]

# ── 2026-10-16 DECISIONS ข้อ 153 ข้อ 2 — ความหมายรหัส ภ.พ. ที่กระดาษไม่บอก (กฎเหล็ก #1 ครบ 4 ขั้น) ──────────────────────────────────────────
#    ถอด Resolve ในตัวสร้างบรรทัด = รหัสไม่รู้ ⇒ ตารางใช้ไม่ได้ (กลับไปบรรทัดสรุป 7% + [Σ-GAP]) · ถอด `with { Groups = vatCodeTable }` = สาขาไม่มีรายการไม่ได้ผล ·
#    ถอดการถามตอนสแกน = ไม่มีวันถาม AI · ถอด Guard ใน augmenter = คำตอบ 7% บนแถว VAT 0 ผ่าน · ถอดการเก็บ JSON/UsedAi = ป้ายโกหก + ปิดลูปไม่ได้ ·
#    ถอดตัวปิดลูปตอนอนุมัติ = นักเรียนไม่เคยเรียน (จ่าย token ทุกใบตลอดกาล)
RULES += [
    dict(file=OCR, method="BuildScanLinesAsync",
         must=["OcrVatCodeMeaning.ResolvedTable("],
         must_re=[r"with\s*\{\s*Groups\s*=\s*vatCodeTable\s*\}", r"var\s+vatCodeTable\s*=\s*vatCodeResolution\s*\.\s*Table"],
         call_args=[("OcrVatCodeMeaning.ResolvedTable(", "result.VatCodeMeaningsJson")],
         before=[("OcrVatCodeMeaning.ResolvedTable(", "OcrVatCodeLines.Decide("), ("OcrVatCodeMeaning.ResolvedTable(", "OcrVatCodeLines.TableMismatch(")],
         # ฝ่ายค้าน f7c29c2c P1-1: ห้ามประกอบตารางเอง (ReadGroups ดิบ / Resolve ตรง) — ผู้อ่านทุกตัวต้องได้ตารางเดียวกัน
         forbid=["OcrLineVatMarks.ReadGroups(", "OcrVatCodeMeaning.Resolve("],
         why="DECISIONS 153 ข้อ 2: ตารางที่รหัสถูกตัดสินความหมายแล้ว (ตัวเลข → AI/นักเรียนที่ persist → ค่าตั้งต้น) เป็นตัวเดียวของทุกสาขาในตัวสร้างบรรทัด"),
    # ฝ่ายค้าน f7c29c2c P1-1: ด่านตรวจสดตอนอนุมัติ (เว็บ/มือถือ/API/workflow) + ปุ่มอนุมัติ LINE อ่านตารางที่ตัดสินแล้วตัวเดียวกับตัวสร้างบรรทัด ·
    #    ตารางดิบ ⇒ Makro 2/2 ที่รหัส 2 พิสูจน์ด้วยตัวเลขแล้ว ถูกฟ้อง "ไม่บอกความหมาย" ตอนอนุมัติ และ LINE ปฏิเสธ ทั้งที่สร้างสะอาด
    dict(file=DOCSVC, method="CollectApprovalWarningsAsync",
         must=["OcrVatCodeMeaning.ResolvedTable("],
         must_re=[r"OcrVatCodeMeaning\.ResolvedTable\(\s*gapScanNormalized\s*,\s*gapScan\.VatCodeMeaningsJson\s*\)"],
         call_args=[("OcrVatCodeLines.TableMismatch(", "gapScanCodeTable"), ("OcrLineVatPlanner.PaperExemptAmount(", "gapScanCodeTable")],
         forbid=["OcrLineVatMarks.ReadGroups(", "OcrVatCodeMeaning.Resolve("],
         why="ฝ่ายค้าน f7c29c2c P1-1: ด่านอนุมัติใช้ตารางที่ตัดสินแล้ว (OcrVatCodeMeaning.ResolvedTable) ตัวเดียวกับตอนสร้าง"),
    dict(file=LINEBOT, method="HandlePostbackAsync",
         must=["OcrVatCodeMeaning.ResolvedTable("],
         must_re=[r"OcrVatCodeMeaning\.ResolvedTable\(\s*gateNormalized\s*,\s*scanGate\.VatCodeMeaningsJson\s*\)"],
         call_args=[("OcrVatCodeLines.TableMismatch(", "OcrVatCodeMeaning.ResolvedTable")],
         forbid=["OcrLineVatMarks.ReadGroups(", "OcrVatCodeMeaning.Resolve("],
         why="ฝ่ายค้าน f7c29c2c P1-1: ปุ่มอนุมัติ LINE ใช้ตารางที่ตัดสินแล้วตัวเดียวกับตอนสร้าง/เว็บ"),
    # ฝ่ายค้าน f7c29c2c P1-2/P1-3 → คำตัดสินเจ้าของ 2026-10-16 ภาคต่อ: ผัง/โครงการของรายการเข้าตัวตัดสิน (ต่างกัน ⇒ ของที่ยอดมากสุด + หมายเหตุรายการเดิม ·
    # ไม่คงรายการ) · บรรทัดสรุปแบกผัง/โครงการที่ตัวตัดสินเลือก + โครงการหัวเอกสาร
    dict(file=OCR, method="BuildScanLinesAsync",
         must_re=[r"AccountId\s*:\s*ItemAccount\(\s*x\s*\)", r"ProjectId\s*:\s*x\s*\.\s*ProjectId",
                  r"codeDecision\s*!\s*\.\s*SummaryAccountId", r"codeDecision\s*!\s*\.\s*SummaryProjectId\s+is\s+Guid\s+summaryProject",
                  r"ProjectId\s*=\s*tableLinesInsteadOfItems\s+is\s+not\s+null\s*\?\s*codeDecision\s*!\s*\.\s*SummaryProjectId"],
         call_args=[("OcrLineAccountSource.Resolve(", "scanDebitAccountId")],
         why="ฝ่ายค้าน f7c29c2c P1-2/P1-3: ตารางชนะต้องไม่ทิ้งผังบัญชี/โครงการรายบรรทัด"),
    dict(file=OCR, method="ScanAsync",
         must=["ResolveVatCodeMeaningsAsync("],
         why="DECISIONS 153 ข้อ 2: ถามความหมายรหัสตอนสแกน (ครั้งเดียว · ผลถูก persist ให้ทุกทางเข้าที่สร้างบรรทัด)"),
    dict(file=OCR, method="ResolveVatCodeMeaningsAsync",
         # ฝ่ายค้าน f7c29c2c P2-3: หลักฐาน 0% บนกระดาษถึงทั้งด่านของ augmenter และ Choose
         must=["OcrVatCodeMeaning.Ambiguous(", "InferVatCodeMeaningAsync(", "OcrVatCodeMeaning.Choose(", "OcrVatCodeMeaning.ZeroRatedOnPaper("],
         call_args=[("OcrVatCodeMeaning.Choose(", "zeroRatedOnPaper"), ("InferVatCodeMeaningAsync(", "zeroRatedOnPaper")],
         must_re=[r"scanResult\s*\.\s*VatCodeMeaningsJson\s*=\s*Accounting\.Helpers\.OcrVatCodeMeaning\.ToJson\(", r"scanResult\s*\.\s*VatCodeUsedAi\s*="],
         before=[("OcrVatCodeMeaning.Ambiguous(", "InferVatCodeMeaningAsync(")],
         why="DECISIONS 153 ข้อ 2: ถามเฉพาะแถว VAT 0 ที่ไม่รู้ความหมาย (ตัวเลขก่อน AI) · ผ่านด่าน+เกณฑ์ · เก็บ FeedbackId/UsedAi"),
    dict(file="Services/Ai/OcrAiAugmenter.cs", method="InferVatCodeMeaningAsync",
         must=["_orchestrator.AskAsync(", "OcrVatCodeMeaning.Guard(", "OcrVatCodeMeaningPrompt.Build("],
         before=[("_orchestrator.AskAsync(", "OcrVatCodeMeaning.Guard(")],
         call_args=[("OcrVatCodeMeaning.Guard(", "zeroRatedOnPaper")],
         why="DECISIONS 153 ข้อ 2 / กฎเหล็ก #1: เรียกผ่าน orchestrator เท่านั้น + ด่านเลขคณิตของแถวก่อนคืนคำตอบ"),
    dict(file=DOC, method="ApproveDocumentAsync#2",
         must=["RecordVatCodeMeaningFeedbackAsync("],
         why="DECISIONS 153 ข้อ 2 / กฎเหล็ก #1 ขั้น CAPTURE: คำตอบจริงตอนอนุมัติ (ประเภท VAT ของบรรทัดที่ไม่มี VAT)"),
    dict(file=DOC, method="RecordVatCodeMeaningFeedbackAsync",
         # ฝ่ายค้าน f7c29c2c P2-6: คำตอบต่อรหัส (ปนยกเว้น+0% จับคู่ด้วยยอด) · P2-4: สอนเฉพาะแก้/คงคำตอบ AI (ค่าตั้งต้น/นักเรียนที่กดผ่านไม่สอน)
         must=["OcrVatCodeMeaning.ChosenKindsByCode(", "OcrVatCodeMeaning.ResolvedTable(", "_feedbackRecorder.RecordUserChoiceAsync("],
         must_re=[r"if\s*\(\s*!\s*Accounting\.Helpers\.OcrVatCodeMeaning\.Teaches\(\s*e\s*,\s*kind\s*\)\s*\)\s*continue"],
         before=[("OcrVatCodeMeaning.Teaches(", "_feedbackRecorder.RecordUserChoiceAsync(")],
         forbid=["ChosenNoVatKind(", "OcrLineVatMarks.ReadGroups("],
         why="DECISIONS 153 ข้อ 2: ปิดลูปด้วยชนิดที่ผู้ใช้อนุมัติต่อรหัส · ไม่บันทึกนักเรียน/ค่าตั้งต้นยืนยันตัวเอง (DECISION_DOCTRINE §3 · ฝ่ายค้าน f7c29c2c P2-4)"),
    dict(file="Services/Ai/Prompts/OcrVatCodeMeaningPrompt.cs", method="Build",
         must=["OcrVatCodeMeaning.SanitizeEvidence(legendText)", "OcrVatCodeMeaning.SanitizeEvidence(vendorName)"],
         why="ฝ่ายค้าน f7c29c2c P2-3: ข้อความ OCR ในพรอมต์ = ข้อมูล (ตัดบรรทัดหน้าตาคำสั่ง · เพดานความยาว)"),
]


# ── ทีม OCR 10 (2026-10-16) — ข้อความ PDF ตามพิกัด (บิล กฟภ. XH0712609004157 · ใบพรามี่ RE-20261000111) ──
#    page.Text ต่อ glyph ตามลำดับ content stream ⇒ ช่องติดกัน/ป้ายกับค่าสลับ/PUA · ตัวประกอบเดียว Helpers/PdfPositionalText ·
#    ตัวอ่านหัวใบที่แก้ตามมา: ผู้ออกบิลหน่วยงานรัฐบนหัวกระดาษ · เลข/สาขาผู้ซื้อเมื่อรู้เลขผู้ขาย · หัก ณ ที่จ่ายพิมพ์ 0.00 · แถวตารางคั่นสองช่องว่าง
_PDF_POS_WHY = "ทีม OCR 10 (2026-10-16 · ข้อความ PDF ตามพิกัด): "
PDFTEXT = "Services/Implementations/Ocr/PdfTextLayerExtractor.cs"
RULES += [
    dict(file=PDFTEXT, method="TryExtract",
         must=["PositionalPageLayout(page)", "layout.Text ?? page.Text", "if (layout.Text == null)", "if (layout.PuaDropped > 0)", "+ notes"],
         forbid=["var pageText = page.Text", "catch { continue; }"],
         why=_PDF_POS_WHY + "ข้อความทุกหน้าต้องผ่านตัวประกอบตามพิกัดก่อน · page.Text เป็นทางถอยเมื่อประกอบไม่ได้เท่านั้น · ทีม OCR 13: เหตุที่ถอย/ตัด PUA ต้องไปถึงเหตุผลของผล (F2 #7)"),
    dict(file=PDFTEXT, method="PositionalPageLayout",
         must=["Accounting.Helpers.PdfPositionalText.Layout(glyphs)", "l.TextOrientation == TextOrientation.Horizontal", "l.FontName"],
         forbid=["catch { return"],
         why=_PDF_POS_WHY + "ตัวแปลง PdfPig → glyph มีที่เดียว · glyph ที่หมุนแยกจากการจัดแถว · ทีม OCR 13: ส่งฟอนต์ (ตัดตัวซ้ำ/PUA ต่อฟอนต์) · ถอดพิกัดไม่ได้ต้องบอกเหตุ ไม่ใช่ catch เงียบ"),
    dict(file="Helpers/PdfPositionalText.cs", method="NormalizeGlyphText#1",
         must=["ThaiTextNormalizer.MapThaiPua(text)", "mapThaiPua ?", "char.IsControl(ch)"],
         why=_PDF_POS_WHY + "ตาราง PUA ไทยมีเจ้าของเดียว (ThaiTextNormalizer) — ห้ามมีตารางที่สอง"),
    dict(file="Helpers/PdfPositionalText.cs", method="Layout",
         must=["NormalizeGlyphText(g.Text, mapThai)", "thaiFonts.Contains(", "IsDuplicate(seen, text, g)",
               "ComposeSaraAm(lineSb.ToString())", "DigitBoxRuns(cells)"],
         before=[("NormalizeGlyphText(g.Text, mapThai)", "IsDuplicate(seen, text, g)")],
         why=_PDF_POS_WHY + "glyph ตัวหนาที่วาดซ้ำต้องถูกตัด (กฟภ. วาดยอดซ้ำ) · PUA แปลงก่อนเทียบซ้ำ · สระอำรวมทุกบรรทัด"),
    dict(file=OCR, method="ParseThaiDocument",
         must=["Accounting.Helpers.PaperWhtReader.PrintedZero(text)"],
         before=[("Accounting.Helpers.PaperWhtReader.PrintedZero(text)", "data.HasWht = true")],
         why=_PDF_POS_WHY + "ป้ายหัก ณ ที่จ่ายที่พิมพ์ยอด 0.00 ห้ามติ๊กมีหัก (ใบพรามี่ — ป้ายอ่านได้หลังแปลง PUA)"),
    dict(file=SMART, method="TryExtractVendorNameFromRawText",
         must=["Accounting.Helpers.OcrPartyLabels.FindSeller(text) < 0", "ExtractCompanyNames(text).All(n => n.Position > gov.Index)",
               "!GovNameIsBuyer(text, gov.Index)", "GovTaxIdLeadsPage(text)", "!GovNameIsBuyer(text, first.Index)",
               "Accounting.Helpers.ThaiCompanyName.FindAll(text)", "Accounting.Helpers.OcrPartyLabels.BuyerLabelBeforeOnLine(text, position)"],
         forbid_lit=["(บริษัท|ห้างหุ้นส่วน"],
         why=_PDF_POS_WHY + "ผู้ออกบิลหน่วยงานรัฐแทนชื่อที่อ่านได้เฉพาะเมื่อเป็นชื่อคู่สัญญาตัวแรกบนหน้าและไม่มีป้ายผู้ขาย"),
    dict(file=SMART, method="AssignVendorBuyerRoles",
         must=["BranchCodeExtractor.OnTaxIdLine(text, data.BuyerTaxId)",
               "labelled.Count >= 2 && remaining.Select(t => t.Id).Distinct().Count() == 1",
               "LabelBeforeOnSameLine(text, remaining[0].Position)",
               "newBuyerTaxId = BuyerTaxIdBesides(newVendorTaxId);"],
         why=_PDF_POS_WHY + "เลขผู้ซื้อไม่มีป้ายผู้ซื้อ = เฉพาะเลขที่มีป้ายเลขผู้เสียภาษีที่เหลือตัวเดียว · สาขาผู้ซื้อจากบรรทัดเลขของตัวเอง"),
    dict(file="Helpers/PaperWhtReader.cs", method="Read",
         must=["if (amt == 0m && mm.Index < labelLineEnd) break;"],
         why=_PDF_POS_WHY + "ยอด 0.00 บนบรรทัดป้ายหัก ณ ที่จ่าย = ไม่หัก — ห้ามหยิบตัวเลขบรรทัดถัดไป (ใบพรามี่ 1,037.00)"),
    dict(file="Helpers/RawTextLineSplitter.cs", method="Split#1",
         must=["var table = TableRows(rawText, out var skipped);", "Reconciles(table, subTotal, total)", "TrailingRows(rawText)", "skipped > 0"],
         before=[("var table = TableRows(rawText, out var skipped);", "TrailingRows(rawText)"),
                 ("if (Reconciles(table, subTotal, total)) return table;", "Reconciles(trailing, subTotal, total)")],
         why=_PDF_POS_WHY + "ข้อความที่มีหัวตารางอ่านตามคอลัมน์ก่อน · ทีม OCR 13 (P1-3): ตารางที่ Σ ไม่ตรง/ข้ามแถว ถอยไปกติกาท้ายบรรทัดบนข้อความเดียวกัน"),
    dict(file="Helpers/RawTextLineSplitter.cs", method="Reconciles",
         must=["OcrLineSplitGuard.Evaluate(ToJson(lines)"],
         why="ทีม OCR 13: การเลือกตาราง/กติกาท้ายบรรทัดใช้ด่าน Σ ตัวเดียวกับผู้เรียก (ห้ามเขียนผลรวม/ค่าเผื่อเอง)"),
    dict(file="Helpers/RawTextLineSplitter.cs", method="TableRow",
         must=["if (cells.Count != width) return null;", "RowFrom(cells,"],
         why=_PDF_POS_WHY + "แถวเต็มต้องมีจำนวนช่องเท่าหัวตาราง"),
    dict(file="Helpers/RawTextLineSplitter.cs", method="RowFrom",
         must=["OcrNonItemRow.IsSummaryOrTenderRow(desc)", "proven = Math.Abs(qty * p - amount) <= 0.05m"],
         forbid=["amount / qty"],
         why=_PDF_POS_WHY + "แถวสรุปไม่ใช่รายการ · ห้ามหาร ยอด ÷ จำนวน (ราคาที่กระดาษไม่พิมพ์ = ว่าง ให้ตัวกันจำนวนตัดสิน)"),
    dict(file="Helpers/RawTextLineSplitter.cs", method="GappedRow",
         must=["cols.UnitPrice < 0) return null", "if (found != null && found != row) return null;", "!proven"],
         why="ทีม OCR 13 (P1-3): แถวขาดช่องรับเฉพาะวิธีวางเดียวที่ จำนวน × ราคา = ยอด — ไม่มีคอลัมน์ราคา/วางได้สองแบบ = ไม่เดา"),
    dict(file="Helpers/RawTextLineSplitter.cs", method="TableRows#1",
         must=["OcrTableColumnMapper.Map(header)", "skipped += pendingSkipped"],
         why=_PDF_POS_WHY + "บทบาทหัวคอลัมน์มาจากตัวจับคู่ตัวเดียวกับตาราง Azure"),
]
REVIEWER_CASES += [
    # ถอยกลับไปใช้ page.Text ตรง ๆ (ลำดับ stream)
    ("PP1", "TryExtract", "var pageText = layout.Text ?? page.Text ?? \"\";", "var pageText = page.Text ?? \"\";", True),
    # ตาราง PUA ชุดที่สองในตัวประกอบ
    ("PP2", "NormalizeGlyphText#1", "var mapped = mapThaiPua ? ThaiTextNormalizer.MapThaiPua(text) : text;", "var mapped = text;", True),
    # ป้ายหัก 0.00 กลับไปติ๊กมีหัก
    ("PP3", "ParseThaiDocument", "if (whtMatch.Success && Accounting.Helpers.PaperWhtReader.PrintedZero(text))", "if (false)", True),
    # แถวตารางหารราคาเอง
    ("PP4", "RowFrom", "else if (cols.Quantity < 0) price = amount;", "else price = amount / qty;", True),
    # รูปถูก: ขึ้นบรรทัดใหม่ในการเรียกตัวประกอบ ต้องไม่ถูกฟ้อง
    ("PP5", "PositionalPageLayout", "return Accounting.Helpers.PdfPositionalText.Layout(glyphs);",
     "return Accounting.Helpers.PdfPositionalText\n            .Layout(glyphs);", False),
]

# ── ทีม OCR 13 (2026-10-16 · ฝ่ายค้าน OCR 10) — ชื่อนิติบุคคลตัวตั้งเดียว · ผู้ออกบิลรัฐ · ตาราง Σ · หัก 0.00 · สาขาตามเลข · ที่อยู่ในบล็อกผู้ซื้อ ──
_OCR13_WHY = "ทีม OCR 13 (ฝ่ายค้าน OCR 10): "
ZONE = "Services/Implementations/DocumentZoneAnalyzer.cs"
FPL = "Services/Implementations/Ocr/FieldPatternLibrary.cs"
_NAME_COPY = ["(บริษัท|ห้างหุ้นส่วน", "(บริษัท)", "(ห้างหุ้นส่วนจำกัด|หจก"]
RULES += [
    dict(file=SMART, method="ExtractCompanyNames", must=["Accounting.Helpers.ThaiCompanyName.FindAll(text)"], forbid_lit=_NAME_COPY,
         why=_OCR13_WHY + "regex ชื่อบริษัท + คำท้าย จำกัด มีเจ้าของเดียว Helpers/ThaiCompanyName — ห้ามสำเนา (เดิม 6 สำเนา · 4 ตัวยังต่อ \"จำกัด\" ซ้ำ)"),
    dict(file=OCR, method="ParseThaiDocument",
         must=["Accounting.Helpers.ThaiCompanyName.FindAll(text)", "Accounting.Helpers.OcrPartyLabels.BuyerLabelBeforeOnLine(text, c.Position)",
               "Accounting.Helpers.OcrVendorAddressReader.Read(text)"],
         forbid_lit=_NAME_COPY,
         why=_OCR13_WHY + "ชื่อผู้ขายผ่านเจ้าของเดียว · ชื่อหลังป้ายผู้ซื้อบนบรรทัดเดียวกันไม่ใช่ผู้ขาย · ที่อยู่ผู้ขายผ่านตัวอ่านเดียว"),
    dict(file="Helpers/OcrVendorAddressReader.cs", method="Read",
         must=["VendorAddressRegex.Match(text)", "OcrPartyLabels.InBuyerBlock(text, m.Index)", "l[..tab]"],
         why=_OCR13_WHY + "ป้ายที่อยู่ในบล็อกผู้ซื้อไม่ใช่ที่อยู่ผู้ขาย · ตัดคอลัมน์ขวา (tab) ออกจากที่อยู่"),
    dict(file=OCR, method="TrySplitLineItemsAsync",
         must=["RawTextLineSplitter.SplitToJson(rawText, data.SubTotal, data.TotalAmount)"],
         why=_OCR13_WHY + "ตัวแตกบรรทัดต้องรู้ยอดบนกระดาษเพื่อเลือกตาราง/กติกาท้ายบรรทัด (P1-3)"),
    dict(file=ZONE, method="FallbackCompanyExtraction", must=["Accounting.Helpers.ThaiCompanyName.FindAll(text)"], forbid_lit=_NAME_COPY,
         forbid=["ExtractCompanyNameFromMatch("],
         why=_OCR13_WHY + "zone fallback ที่ทำงานจริงเคยออก \"ห้างหุ้นส่วนจำกัด X จำกัด\" (หาคำว่าจำกัดตั้งแต่คำนำหน้า)"),
    dict(file=FPL, method="ExtractCompanyNameCandidates", must=["Accounting.Helpers.ThaiCompanyName.FindAll(text)"], forbid_lit=_NAME_COPY,
         why=_OCR13_WHY + "สำเนาที่ห้า: ต่อ \"จำกัด\" ตาม pattern ของคำนำหน้า"),
    dict(file="Helpers/ThaiCompanyName.cs", method="Compose",
         must=["Suffix(text, prefix, m.Groups[2].Index + m.Groups[2].Length, m.Index + m.Length)", "ProseStarts.Any("],
         why=_OCR13_WHY + "คำท้ายหาหลังชื่อ (ไม่ใช่ต้นคำนำหน้า) · ประโยคเงื่อนไขท้ายใบที่ขึ้นด้วยคำว่าบริษัทไม่ใช่ชื่อ"),
    dict(file="Helpers/PaperWhtReader.cs", method="PrintedZero",
         must=["if (Read(rawText).Amount is > 0m) return false;"],
         why=_OCR13_WHY + "ป้ายหักที่พิมพ์ 0.00 ห้ามซ่อนยอดหัก > 0 ที่ตัวอ่านยอดตัวเดียวพบ (P2)"),
    dict(file=SMART, method="Enrich",
         must=["BranchCodeExtractor.VendorBranchFromBuyerLine(rawText, data.VendorBranchCode, data.VendorTaxId, data.BuyerTaxId)",
               "BranchCodeExtractor.OnTaxIdLine(rawText, data.VendorTaxId)", "BranchCodeExtractor.OnTaxIdLine(rawText, data.BuyerTaxId)"],
         before=[("AssignVendorBuyerRoles(data, rawText, taxIds)", "BranchCodeExtractor.VendorBranchFromBuyerLine(")],
         why=_OCR13_WHY + "สาขาผู้ขายที่มาจากบรรทัดเลขผู้ซื้อต้องถูกล้าง (F2 #3) · สาขาบนบรรทัดเลขของฝั่งนั้นชนะการกวาดทั้งหน้า (กุญแจ = เลข)"),
    dict(file="Services/Ai/Distillation/LineSplitDistillationModel.cs", method="PredictAsync",
         must=["RawTextLineSplitter.SplitToJson(rawText, knownSub, knownTotal)"],
         why=_OCR13_WHY + "นักเรียนชั้นกติกาใช้ยอดบนกระดาษเลือกตาราง/กติกาท้ายบรรทัดเหมือนเส้น service"),
]
REVIEWER_CASES += [
    # ผู้ออกบิลรัฐกลับไปไม่ดูป้ายผู้ซื้อ (P1-2)
    ("Q1", "TryExtractVendorNameFromRawText", "&& !GovNameIsBuyer(text, gov.Index)", "", True),
    # สำเนา regex ชื่อบริษัทกลับเข้า FieldPatternLibrary (P1-5)
    ("Q2", "ExtractCompanyNameCandidates", "foreach (var (full, position) in Accounting.Helpers.ThaiCompanyName.FindAll(text))",
     "foreach (var (full, position) in Regex.Matches(text, @\"(บริษัท)\\s+(.+?)\\s*จำกัด\").Select(m => (m.Value, m.Index)))", True),
    # ตัวแตกบรรทัดกลับไปคืนตารางบางแถวโดยไม่เทียบ Σ (P1-3)
    ("Q3", "Split#1", "if (Reconciles(table, subTotal, total)) return table;", "return table;", True),
    # PrintedZero กลับไปไม่ยอมตัวอ่านยอดหัก (P2)
    ("Q4", "PrintedZero", "if (Read(rawText).Amount is > 0m) return false;", "", True),
]

# ── ทีม OCR 15 (2026-10-16 · ฝ่ายค้าน OCR 13) — คำท้ายไม่ข้ามคอลัมน์ · ชื่อผูกเลขในคอลัมน์เดียวกัน · เลขตีกรอบต้องมีช่องกล่อง · ลายนิ้วมือสมมาตร · ที่อยู่หยุดที่ป้ายผู้ซื้อ ──
_OCR15_WHY = "ทีม OCR 15 (ฝ่ายค้าน OCR 13): "
RULES += [
    dict(file="Helpers/ThaiCompanyName.cs", method="Suffix",
         must=["after.IndexOfAny(CellBreaks)", "IsPartnership(prefix)"],
         why=_OCR15_WHY + "คำว่าจำกัดในคอลัมน์ขวา/บรรทัดถัดไปเป็นของชื่ออื่น (P1-1 · BS2026100001 \"หจก. แอมแฮปปี้เนส จำกัด\") · ห้างหุ้นส่วนไม่ต่อคำท้าย"),
    dict(file=OCR, method="ParseThaiDocument",
         must=["Accounting.Helpers.OcrVendorNameByTaxId.Pick(text, companyNames,"],
         forbid=["vendorIdAt.Pos - c.Position <= 300"],
         why=_OCR15_WHY + "ชื่อผูกเลขผู้ขายต้องอยู่คอลัมน์เดียวกัน (P1-2 หัวสองคอลัมน์ชื่อสลับฝั่ง) — ตัวตัดสินเดียว Helpers/OcrVendorNameByTaxId ห้ามเขียนกติกาเองใน service"),
    dict(file="Helpers/OcrVendorNameByTaxId.cs", method="Pick",
         must=["SameColumn(text, c.Position, allNames, taxIdPos, allTaxIdPos)", "default: return null;"],
         why=_OCR15_WHY + "ตัดสินคอลัมน์ไม่ได้ ⇒ ไม่ตอบ (คงคำตอบใกล้ป้ายผู้ขาย) — ห้ามเดาเมื่อไม่มีข้อมูลคอลัมน์"),
    dict(file="Helpers/PdfPositionalText.cs", method="IsDigitBoxRun",
         must=["cells[k].Left - cells[k - 1].Right <= WordGap * Math.Max(cells[k].Size, cells[k - 1].Size)"],
         why=_OCR15_WHY + "เลขตีกรอบต้องมีช่องว่างจริงระหว่างกล่องทุกคู่ — เลขที่พิมพ์ติดกันไม่ถูกต่อ (\"โทร 0812345678 123\" ⇒ 13 หลักปลอม)"),
    dict(file="Services/Ai/Distillation/LineSplitDistillationModel.cs", method="ShapeFingerprint",
         must=["Accounting.Helpers.PdfPositionalText.ComposeSaraAm(Accounting.Helpers.PdfPositionalText.NormalizeGlyphText(rawText))"],
         why=_OCR15_WHY + "ลายนิ้วมือโครงกระดาษ normalize รูปอักขระด้วยตัวแปลงเดียวกับตัวประกอบ PDF ทั้งตอนเรียนและตอนอ่าน (ห้ามสำเนาตาราง)"),
    dict(file="Helpers/OcrVendorAddressReader.cs", method="Read",
         must=["OcrPartyLabels.FindBuyer(l) < 0"],
         why=_OCR15_WHY + "ที่อยู่ผู้ขายหยุดที่บรรทัดป้ายผู้ซื้อ (เดิม \"…10110 ผู้ซื้อ: บริษัทบีจำกัด\")"),
]
REVIEWER_CASES += [
    # คำท้ายกลับไปข้าม tab (P1-1)
    ("R1", "Suffix", "var stop = after.IndexOfAny(CellBreaks);", "var stop = -1;", True),
    # กลับไปเขียนกติกาชื่อผูกเลขเองใน service (P1-2)
    ("R2", "ParseThaiDocument", "if (Accounting.Helpers.OcrVendorNameByTaxId.Pick(text, companyNames,", "if (Pick(text, companyNames,", True),
    # ตัดสินคอลัมน์ไม่ได้แล้วเดาเอา
    ("R3", "Pick", "default: return null;", "default: return c.FullName;", True),
]

_RULE_KEYS ={"file", "method", "why", "must", "must_re", "must_lit", "before", "forbid", "forbid_lit", "call_args"}


# 2026-10-19 — ทะเบียนสินทรัพย์ที่ระบบตัดสินผิด (PV-20260903-0001 อุปกรณ์สำนักงาน 138.32 ลง 12210):
# ด่านลบต้องอ่าน GL ของทั้งใบผ่านตัวคำนวณเดียว · ย้ายผังออกจากสินทรัพย์ต้องเอาทะเบียนออกด้วย · ของราคาต่ำบนผังสินทรัพย์ต้องเตือนก่อนอนุมัติ
RULES += [
    dict(file="Services/Implementations/FixedAssetService.cs", method="DeleteAsync",
         must=["UnbackedAssetAmountForDocumentAsync(_db, companyId, sourceDoc.Id, new[] { asset.Id })"],
         forbid=["ที่ยังใช้งานอยู่ "],
         why="ด่านลบตัดสินจาก GL ทั้งใบ (Posted+Reversed · ทุกผังสินทรัพย์) ไม่ใช่สถานะใบ — เดิมของที่ไม่ใช่สินทรัพย์บน PV ที่จ่ายแล้วลบไม่ได้เลย"),
    dict(file=DOC, method="ReclassifyLineAccountAsync",
         must=["AssetRemovalGlCheck.MovesOffAssetAccount(oldAccount.AccountCode, newAccount.AccountCode)",
               "await RemoveLeavingAssetsAsync();", "await RecordReclassifyChoiceAsync();"],
         why="ย้ายบรรทัดออกจากผังสินทรัพย์ ⇒ เอาทะเบียนออกในธุรกรรมเดียวกัน (ทั้งทาง JE และทางลัดยอด 0) + บันทึกคำตอบผู้ใช้ให้นักเรียน"),
    dict(file=DOC, method="CollectApprovalWarningsAsync",
         must=["LowValueAssetLineCheck.Warnings("],
         why="ของราคาต่ำกว่าเกณฑ์ขึ้นทะเบียนบนผังสินทรัพย์ ⇒ เตือนก่อนอนุมัติ (จุดเดียวที่ทุกทางเข้าผ่าน)"),
]

# ทีม J 2026-10-22 — ตราประทับ/ลายเซ็น: ใบ 50 ทวิ ไม่มีตราทั้งดูบนเว็บ/ดาวน์โหลด/พิมพ์ (ทางออก 50 ทวิไม่เคยอ่าน StampPath) ·
# เงื่อนไข "ประทับเมื่อไร" เคยเขียน inline 3 ที่ ⇒ ตัวตัดสินเดียว Helpers/DocumentStampPolicy · ทุก renderer ต้องเรียกตัวเดียวกัน
# (DocumentStampPolicyTests + WhtCertStampTests ล็อกพฤติกรรม — กติกาข้างล่างล็อกว่าจุดเรียกยังต่อสายอยู่)
_STAMP = "ทีม J 2026-10-22 ตราประทับ: "
RULES += [
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildDocumentHtml",
         must=["StampDecisionFor(doc, settings)", "ResolveStampBytes(settings, template)", "DocumentStampBlockHtml(stampRule, stampSrc)"],
         forbid=["stampApproved", "DocumentStatus.WaitingApproval or DocumentStatus.Rejected"],
         why=_STAMP + "HTML เอกสารขาย/ซื้อ ตัดสินตราผ่าน DocumentStampPolicy ตัวเดียวกับ QuestPDF (เดิม inline สถานะเอง)"),
    dict(file="Services/Implementations/PdfGenerationService.DocumentRenderer.cs", method="RenderDocumentPdfNative",
         must=["StampDecisionFor(doc, settings)", "BuildBranding(template, settings, watermarkOverride, stampRule,"],
         forbid=["stampAllowed"],
         why=_STAMP + "QuestPDF เอกสารขาย/ซื้อ ตัดสินตราผ่านตัวเดียวกับ HTML"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildBranding",
         must=["ResolveStampBytes(settings, template)", "stampRule is { ShowStamp: true }", "stampRule?.OffsetXMm"],
         why=_STAMP + "QuestPDF รูปตรา/ขนาด/ระยะเลื่อนจากตัวตัดสิน (ไม่อ่าน CompanySettings.StampWidthMm เอง)"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="ResolveSignersAsync",
         must=["ApplySignerPolicyAsync(doc.CompanyId, StampDecisionFor(doc, stampSettings), signers, slotUsers, customerSlots)"],
         before=[("ApplySignerPolicyAsync(", "return signers;")],
         why=_STAMP + "ลายเซ็นเอกสารผ่านด่าน \"ขั้นที่พิมพ์ลายเซ็น/ผู้ลงนามที่มีสิทธิ์\" ก่อนคืนให้ทั้งสอง renderer"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="GenerateWithholdingTaxCertPdfAsync",
         must=["ResolveWhtCertStamp(cert, settings)", "ResolveWhtPayerSignatureAsync(companyId, cert.CreatedBy, stampDecision)",
               "BuildWithholdingTaxCertHtml(cert, company, sigB64, sigName, stamp: stamp)",
               "BuildWhtCertPdf(cert, company, sigBytes, sigName, stamp: stamp)"],
         why=_STAMP + "50 ทวิ ส่งตราที่ตัดสินแล้วถึงทั้ง HTML (Chromium) และ QuestPDF (บั๊กผู้ใช้: ไม่มีตรา)"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildEmployeeAnnualCertPdfAsync",
         must=["ResolveWhtCertStamp(inMemoryCert, settings)", "ResolveCertSignatoryAsync(companyId, inMemoryCert.CreatedBy, decision)"],
         why=_STAMP + "ใบ 50 ทวิ รายปีของพนักงานประทับตรา/ผ่านด่านลายเซ็นเดียวกัน"),
    dict(file="Services/Implementations/PdfGenerationService.cs", method="BuildWithholdingTaxCertHtml",
         must_lit=["{WhtStampAreaHtml(stamp)}</td>"],
         why=_STAMP + "HTML 50 ทวิ วาดช่องตราผ่านตัวเดียว (มีตรา = รูปกลางวง · ไม่มี = วงกลมเดิมทุกไบต์)"),
    dict(file="Services/Implementations/PdfGenerationService.WhtCert.cs", method="BuildBottomSplit",
         must=["DrawStampCircle(e, stamp)"],
         why=_STAMP + "QuestPDF 50 ทวิ ส่งตราถึงวงกลมของแบบ"),
    dict(file="Services/Implementations/PdfGenerationService.WhtCert.cs", method="BuildWhtCertPdf",
         must=["BuildBottomSplit(form, cert.IssuedDate, sigImg, signerName, stampImg)"],
         why=_STAMP + "QuestPDF 50 ทวิ ไม่ทิ้งพารามิเตอร์ตรากลางทาง"),
    dict(file="Services/Implementations/PdfGenerationService.Stamp.cs", method="GetWithholdingTaxCertPrintDecorationAsync",
         must=["w.CompanyId == companyId", "StampDecisionFor(cert, settings)", "ResolveWhtCertStamp(cert, settings)",
               "ResolveWhtPayerSignatureAsync(companyId, cert.CreatedBy, d)"],
         why=_STAMP + "หน้าพิมพ์ wht.html ได้ตรา+ลายเซ็นจากตัวตัดสินเดียวกับ PDF (เดิมใช้ลายเซ็นของผู้ที่กดพิมพ์)"),
]


# ── รอบ 205 ทีม E (docs/BOOKING_REVENUE_AUDIT.md): เงินออนไลน์ของ CMS ต้องลงเอกสารทุกก้อน · ใบมัดจำของเงินที่ยังไม่ได้รับห้ามอนุมัติ ·
#    โควตาเอกสาร: หักโบนัสด้วยตัวตัดสินคู่กับสูตรเพดาน ──
RULES += [
    dict(file="Services/Payments/Handlers/LodgingReservationPaymentHandler.cs", method="HandleSucceededAsync",
         must=["GatewayPaymentRecord.AlreadyRecordedOnReservation(", "GatewayPaymentRecord.ReservationReference("],
         before=[("GatewayPaymentRecord.AlreadyRecordedOnReservation(", "ConfirmAsync(")],
         forbid=["DepositPaid >= intent.Amount"],
         why="รอบ 205 L-1: กันซ้ำด้วยเลขอ้างอิงเท่านั้น — เดิม DepositPaid ≥ ยอด ⇒ ยอดคงเหลือที่เท่า/น้อยกว่ามัดจำถูกตีเป็นซ้ำ เงินเข้าแต่ไม่มีใบ"),
    dict(file=LODGING_LIFE, method="ConfirmCoreAsync", must=["GatewayPaymentRecord.AlreadyRecordedOnReservation("],
         why="รอบ 205 L-1: ตัวตัดสิน \"บันทึกเงินก้อนนี้แล้ว\" ตัวเดียวกับ handler"),
    dict(file="Services/Payments/Handlers/SiteOrderPaymentHandler.cs", method="HandleSucceededAsync",
         must=["GatewayPaymentRecord.Reference(", "SiteOrderPayments.Add(", "paymentId: pay.Id"],
         forbid=["paymentId: null"],
         why="รอบ 205 S-1: ต้องยืนยันแถวรับเงินของรายการชำระนี้ — paymentId: null หยิบแถวสลิปล่าสุด (ไม่มีสลิป = เงินเข้าแต่ออเดอร์ไม่ถูกยืนยัน)"),
    dict(file="Services/Implementations/CmsBookingService.cs", method="SyncBookingToErpAsync",
         must=["CmsBookingErpPolicy.AutoApprove(", "CmsBookingErpPolicy.AwaitingPaymentNote("],
         before=[("CmsBookingErpPolicy.AutoApprove(", "ApproveDocumentAsync(")],
         why="รอบ 205 B-1: ใบเสร็จมัดจำของการจองที่ยังไม่ได้รับเงินห้ามอนุมัติ (JE เงินสด + ภาษีขายของเงินที่ไม่เคยได้รับ)"),
    dict(file="Services/Implementations/CmsBookingService.cs", method="CreateBookingAsync",
         must=["CmsBookingErpPolicy.SyncFailedNote(", "ErpNotices"],
         why="รอบ 205 B-3: ขั้น ERP ล้มต้องไม่ทำให้แขกเห็นจองไม่สำเร็จ (การจองบันทึกแล้ว ⇒ จองซ้อน) — หมายเหตุ + คำตอบ"),
    dict(file="Services/Implementations/SubscriptionService.cs", method="IncrementUsageAsync",
         must=["DocumentQuotaPolicy.ConsumesBonus(", "DocumentQuotaPolicy.CrossedWarnLevel(", "DocumentQuotaPolicy.Basis(",
               "FlushPendingQuotaAlertsAsync("],
         before=[("DocumentQuotaPolicy.Basis(", "DocumentQuotaPolicy.ConsumesBonus(")],
         forbid=["sub.CurrentMonthDocuments > sub.MaxDocumentsPerMonth", "NotifyDocumentQuotaCrossedAsync("],
         why="รอบ 205 Q-1/Q-3 + ฝ่ายค้าน ข้อ 1/3: หักโบนัสด้วยฐานนับเดียวกับการตรวจ (กลุ่ม/บริษัท) · แจ้งเตือนเก็บไว้ส่งหลัง commit (ห้ามส่งในธุรกรรม)"),
    dict(file="Services/Implementations/SubscriptionService.cs", method="CheckUsageLimitAsync",
         must=["used: agg.Documents", "used: sub.CurrentMonthDocuments"],
         why="ฝ่ายค้านรอบ 205 ข้อ 1: ทั้งเส้นกลุ่มและบริษัทเดี่ยวส่งยอดที่ใช้ไป (ไม่งั้นโบนัสหายสองเท่า)"),
    dict(file=DOC, method="CreateDocumentAsync", must=["FlushPendingQuotaAlertsAsync("],
         why="ฝ่ายค้านรอบ 205 ข้อ 3: แจ้งเตือนโควตาหลัง commit"),
    dict(file="Services/Implementations/CmsCommerceService.cs", method="ConfirmPaymentAsync",
         must=["GatewayPaymentRecord.OverpaysOrder("],
         why="ฝ่ายค้านรอบ 205 ข้อ 2: ห้ามยืนยันแถวรับเงินที่ทำให้รับเกินยอดออเดอร์ (สลิป + ออนไลน์ = 2 เท่า)"),
    dict(file="Services/Payments/Handlers/SiteOrderPaymentHandler.cs", method="HandleSucceededAsync",
         must=["GatewayPaymentRecord.IsPendingSlipRow("],
         why="ฝ่ายค้านรอบ 205 ข้อ 2: เงินออนไลน์เข้า ⇒ ปิดสลิปรอตรวจของออเดอร์"),
    dict(file="Services/Implementations/CmsBookingService.cs", method="SyncBookingToErpAsync",
         must=["CmsBookingErpPolicy.NeedsErpDocument(", "b.CompanyId == companyId"],
         why="ฝ่ายค้านรอบ 205 ข้อ 6/7: ไม่ออกใบเสร็จมัดจำ 0.00 · tenant isolation (กฎ M)"),
    dict(file="Services/Implementations/CmsBookingService.cs", method="CreateBookingAsync",
         must=["CmsBookingErpPolicy.ErpTypeOf("],
         why="ฝ่ายค้านรอบ 205 ข้อ 5: ล้มหลังสร้างใบ ⇒ ผูกใบกลับ (กันใบกำพร้า/ออกซ้ำ)"),
    # คำตัดสินข้อ 155 (B-2): จังหวะออกใบกำกับของการจอง Guaranteed — ทุกทางเข้าเดินตัวตัดสินเดียว BookingInvoiceTimingPolicy
    dict(file="Services/Implementations/CmsBookingService.cs", method="SyncBookingToErpAsync",
         must=["BookingInvoiceTimingPolicy.Decide(", "InvoiceTimingAsync(", "BookingInvoiceEvent.Booked"],
         before=[("BookingInvoiceTimingPolicy.Decide(", "CreateDocumentAsync(")],
         forbid=["BookingType.Guaranteed => (DocumentType.TaxInvoice"],
         why="ข้อ 155: ชนิดเอกสารตอนจองตัดสินที่ BookingInvoiceTimingPolicy ตัวเดียว (ห้าม switch ชนิดเอกสารเองซ้ำ — ค่าตั้งไซต์จะไม่มีผล)"),
    dict(file="Services/Implementations/CmsBookingService.cs", method="UpdateBookingStatusAsync",
         must=["ApplyInvoiceTimingAsync(", "BookingInvoiceEvent.ServiceCompleted", "BookingInvoiceEvent.NoShow"],
         why="ข้อ 155: ให้บริการเสร็จ = tax point §78/1 ของ Guaranteed ที่ยังมีแค่ใบยืนยันการจอง — ถอดการเรียก = ใบกำกับไม่เคยออก"),
    dict(file="Services/Implementations/CmsBookingService.cs", method="IssueTaxInvoiceOnPaymentAsync",
         must=["ApplyInvoiceTimingAsync(", "BookingInvoiceEvent.PaymentReceived", "b.CompanyId == companyId",
               "BookingInvoiceTimingPolicy.PaymentDate(", "BookingInvoiceAction.Rejected"],
         before=[("BookingInvoiceTimingPolicy.PaymentDate(", "ApplyInvoiceTimingAsync(")],
         why="ข้อ 155: รับเงินแล้ว = tax point §78/1 — เดินตัวตัดสินเดียวกับเช็คเอาต์/ให้บริการ (ไม่ออกซ้ำ)"),
    dict(file="Services/Implementations/CmsBookingService.cs", method="ApplyInvoiceTimingAsync",
         must=["ApplyInvoiceTimingLockedAsync(", "AdvisoryLockKey.CmsBookingInvoice"],
         must_lit=["pg_try_advisory_lock", "pg_advisory_unlock"],
         before=[("AdvisoryLockKey.CmsBookingInvoice", "ApplyInvoiceTimingLockedAsync(")],
         why="ข้อ 155: ออกใบกำกับหลังจองใต้ล็อกต่อการจอง — กดรับเงินสองแท็บพร้อมกันต้องไม่แปลงสองใบ"),
    dict(file="Services/Implementations/CmsBookingService.cs", method="ApplyInvoiceTimingLockedAsync",
         must=["BookingInvoiceTimingPolicy.Decide(", "ConvertDocumentAsync(", "ConvertDocumentPartialAsync(", "ApproveBookingTaxDocumentAsync(",
               "SyncBookingToErpAsync(", "VoidDocumentAsync(", "BookingInvoiceAction.SettledByPosBill"],
         before=[("BookingInvoiceTimingPolicy.Decide(", "ConvertDocumentAsync(")],
         forbid=["CreateDocumentAsync(", "PostJournal"],
         why="ข้อ 155: ออกใบกำกับด้วยเส้นแปลง/สร้างเอกสารเดิม (ด่าน §90/2 · กันแปลงซ้ำ · สืบทอดผู้ซื้อ/สาขา) — ห้ามประกอบใบ/posting เอง"),
    dict(file="Services/Implementations/CmsBookingService.cs", method="LoadInvoiceFactsAsync",
         must=["BookingInvoiceTimingPolicy.IssuedTaxDocumentTypes", "x.CompanyId == companyId", "CompanyVatStatus.ProfileAsync(",
               "BookingInvoiceTimingPolicy.ShouldLookForIssuedChild(", "PosBookingSource.CmsBooking", "o.CompanyId == companyId"],
         before=[("BookingInvoiceTimingPolicy.ShouldLookForIssuedChild(", "BookingInvoiceTimingPolicy.IssuedTaxDocumentTypes")],
         why="ข้อ 155: ใบที่พนักงานแปลงเองแล้วต้องถูกนับ (ไม่ออกซ้ำ) · tenant isolation (กฎ M) · สถานะ VAT ตัวเดียว (§90/2)"),
    dict(file="Services/Implementations/PosService.Bookings.cs", method="LoadBookingSnapshotsAsync",
         must=["BookingInvoiceTimingPolicy.RevenueDocumentForPos(", "RelatedDocumentId", "d.CompanyId == companyId"],
         forbid=["d.Type != DocumentType.Quotation"],
         why="ฝ่ายค้านทีม N: POS นับใบลูกที่แปลงจากใบยืนยันการจอง (ใบกำกับ/ใบแจ้งหนี้) เป็นเอกสารรายได้ — ตัวตัดสินเดียว ห้ามเขียนเงื่อนไขซ้ำ (เปิดบิลซ้ำ = รายได้ซ้ำ)"),
    dict(file="Services/Implementations/CmsBookingService.cs", method="GetBookingAsync",
         must=["BookingInvoiceTimingPolicy.ShowPaymentButton("],
         why="ข้อ 155: ปุ่ม 'รับเงินแล้ว — ออกใบกำกับภาษี' ตัดสินด้วยตัวเดียวกับตอนกด (server computes · page displays)"),
]

# คำตัดสินข้อ 157 (ทีม P · 2026-10-23): จังหวะออกเอกสารภาษีของที่พัก — ทุกทางเข้า (จองหน้าเว็บ/พนักงานสร้าง/ยืนยัน/รับเงินออนไลน์/
# ยืนยันสลิป/รับชำระระหว่างพัก/เช็คเอาต์/ยกเลิก/no-show/เลื่อนวัน) เดินตัวตัดสินเดียว LodgingTaxInvoiceTimingPolicy · ตัดสินจากใบที่ผูกจริง
LODGING_TAX = "Services/Implementations/Lodging/LodgingService.TaxTiming.cs"
LODGING_OPS = "Services/Implementations/Lodging/LodgingService.Operations.cs"
RULES += [
    dict(file=LODGING_LIFE, method="ConfirmCoreAsync",
         must=["EnsureAdvanceTaxInvoiceAsync(", "LodgingTaxTrigger.MoneyReceived", "TryIssueAdvanceOnStaffConfirmAsync(", "LodgingTaxTrigger.StaffConfirm"],
         before=[("EnsureAdvanceTaxInvoiceAsync(", "CreateDepositReceiptAsync(")],
         must_re=[r"DepositCoveredByAdvance\(advance"],
         call_args=[("CreateDepositReceiptAsync(", "coveredBy")],
         why="ข้อ 157: เงินเข้าทุกช่องทาง (ออนไลน์/สลิป/ยืนยัน/รับชำระเพิ่ม) ตัดสินจังหวะก่อนออกใบรับเงิน — ใบกำกับล่วงหน้าที่มีผล ⇒ ใบรับเงินไม่มี VAT (ถอด = VAT ซ้อน)"),
    dict(file=LODGING_RES, method="CreateReservationAsync",
         must=["TryIssueAdvanceOnStaffConfirmAsync(", "LodgingTaxTrigger.StaffCreateConfirmed", "AdvisoryLockKey.LodgingConfirm"],
         why="ข้อ 157: พนักงานสร้างแบบยืนยันทันที = การยืนยันแบบรับประกัน — ออกใบกำกับล่วงหน้าใต้ล็อกเดียวกับเส้นรับเงิน"),
    dict(file=LODGING_LIFE, method="CheckOutCoreAsync",
         must=["LoadAdvanceAsync(", "CheckOutWithAdvanceAsync(", "NewCheckoutChargesAsync("],
         before=[("CheckOutWithAdvanceAsync(", "UpsertFinalDraftAsync(")],
         why="ข้อ 157: ใบกำกับล่วงหน้าที่มีผล ⇒ เช็คเอาต์ออกเฉพาะส่วนเพิ่ม (ถอด = ค่าห้องออกใบกำกับซ้ำ)"),
    dict(file=LODGING_LIFE, method="CancelCoreAsync",
         must=["LoadAdvanceAsync(", "SettleAdvanceOnCancelAsync("],
         before=[("_db.SaveChangesAsync(", "SettleAdvanceOnCancelAsync("), ("SettleAdvanceOnCancelAsync(", "RealizeDepositAsync(")],
         why="ข้อ 157: ยกเลิก/no-show หลังออกใบกำกับล่วงหน้า = ใบลดหนี้ + ตัดชำระด้วยมัดจำที่ริบ (ริบเป็นรายได้ = ใบกำกับซ้อน) · บันทึกสถานะก่อน (กดซ้ำไม่ลงซ้ำ)"),
    dict(file=LODGING_LIFE, method="ReissueFinalDocumentCoreAsync", must=["LoadAdvanceAsync("],
         why="ข้อ 157: ตัวสร้างใบเช็คเอาต์ใหม่แบบเต็มยอดห้ามใช้เมื่อค่าห้องอยู่ในใบกำกับล่วงหน้า"),
    dict(file=LODGING_LIFE, method="RescheduleAsync",
         must=["LoadAdvanceAsync(", "LodgingTaxInvoiceTimingPolicy.RescheduleNote("],
         why="ข้อ 157: เลื่อนวันหลังออกใบกำกับล่วงหน้า — บอกว่าส่วนต่างปรับตอนเช็คเอาต์/ยกเลิก"),
    dict(file=LODGING_LIFE, method="CreateDepositReceiptAsync",
         must=["coveredByAdvanceNumber", "DepositVatTreatment.FullDeposit", "covered ? null : kind.KindId", "covered || kind.KindId is null ? null : kind"],
         why="ข้อ 157: ภาษีของค่าห้องอยู่ในใบกำกับล่วงหน้า ⇒ ใบรับเงินรูปเต็มยอด (VAT 0) ไม่ตรึงประเภท — ตามประเภทจะได้ VAT ซ้อน"),
    dict(file=LODGING_LIFE, method="BuildFinalInvoiceLinesAsync", must=["includeStay ? r.Rooms", "includeStay ? r.Extras", "includeStay && r.ServiceChargeAmount"],
         why="ข้อ 157: ใบส่วนเพิ่มบนใบกำกับล่วงหน้า = รายการ folio อย่างเดียว (ตัวสร้างรายการตัวเดียว)"),
    dict(file=LODGING_TAX, method="EnsureAdvanceTaxInvoiceAsync",
         must=["LoadAdvanceAsync(", "AdvanceFactsAsync(", "LodgingTaxInvoiceTimingPolicy.Decide(", "LodgingAdvanceState.Live"],
         before=[("LodgingTaxInvoiceTimingPolicy.Decide(", "IssueAdvanceTaxInvoiceCoreAsync(")],
         why="ข้อ 157: ออก/ไม่ออก ตัดสินที่ตัวตัดสินเดียว · ใบที่มีผลแล้วไม่ออกซ้ำ (webhook/สลิป/กดซ้ำ)"),
    dict(file=LODGING_TAX, method="AdvanceFactsAsync", must=["LodgingDepositSettlement.RoomDeposits(", "prop.TaxInvoiceTiming", "fresh?.FinalDocumentId ?? r.FinalDocumentId"],
         why="ข้อ 157: ข้อเท็จจริงจากเอกสารที่ผูกจริง (มัดจำที่มี VAT · ใบเช็คเอาต์) — ไม่ใช่ค่าตั้งอย่างเดียว"),
    dict(file=LODGING_TAX, method="LoadAdvanceAsync",
         must=["x.CompanyId == companyId", "LodgingTaxInvoiceTimingPolicy.StateOf(", "DocumentType.CreditNote"],
         why="ข้อ 157: tenant isolation (กฎ M) · สถานะใบล่วงหน้าตัวเดียว · ยอดคงเหลือหักใบลดหนี้ (กันลดหนี้ซ้ำ)"),
    dict(file=LODGING_TAX, method="IssueAdvanceTaxInvoiceCoreAsync",
         must=["BuildFinalInvoiceLinesAsync(", "UpsertFinalDraftAsync(", "_docService.ApproveDocumentAsync(", "VatProfileAsync(", "AddChainedAuditLog(",
               "LodgingDepositSettlement.ApplyCandidates("],
         before=[("UpsertFinalDraftAsync(", "r.AdvanceTaxDocumentId = approved.Id")],
         forbid=["ServiceUsedDate:", "CreateDocumentAsync("],
         why="ข้อ 157: ใบกำกับล่วงหน้าออกด้วยตัวสร้างรายการ/ใบร่าง/อนุมัติเดิม (กันกดซ้ำ · ชนิดใบตาม §90/2) · ไม่ตั้งวันใช้บริการ (tax point = วันออกใบ)"),
    dict(file=LODGING_TAX, method="IssueAdvanceTaxInvoiceAsync",
         must=["WithReservationLockAsync(", "AdvisoryLockKey.LodgingConfirm", "LodgingTaxTrigger.ManualIssue"],
         why="ข้อ 157: ปุ่มออกใบล่วงหน้าใช้ล็อกเดียวกับเส้นรับเงิน — กดพร้อม webhook ได้ใบเดียว"),
    dict(file=LODGING_TAX, method="IssueAdvanceCreditNoteAsync",
         must=["LodgingTaxInvoiceTimingPolicy.AllocateCredit(", "RelatedDocumentId: original.Id", "CreditNoteReason.Adjustment", "PricesIncludeVat: true",
               "_docService.ApproveDocumentAsync("],
         why="ข้อ 157: ใบลดหนี้อ้างใบเดิม §86/10 · ลงกลับผัง/อัตราเดิม · ยอดรวม = ยอดที่ลดพอดี"),
    dict(file=LODGING_TAX, method="SettleAdvanceOnCancelAsync",
         must=["LodgingTaxInvoiceTimingPolicy.CancelCreditGross(", "IssueAdvanceCreditNoteAsync(", "ApplyDepositToInvoiceAsync(",
               "ForfeitAs: DepositForfeitAs.PriceOrFee"],
         must_lit=["LODGING-CANCEL-ADVANCE"],
         before=[("IssueAdvanceCreditNoteAsync(", "ApplyDepositToInvoiceAsync(")],
         forbid=["RefundDepositAsync(", "DepositForfeitAs.Compensation"],
         why="ข้อ 157: ยกเลิกหลังใบล่วงหน้า — ลดหนี้ก่อนตัดชำระ · คืนเงินผ่าน “ยืนยันคืนเงินแล้ว” เท่านั้น (F-03) · ล้มดังพร้อมทางไปต่อ"),
    dict(file=LODGING_TAX, method="CheckOutWithAdvanceAsync",
         must=["LodgingTaxInvoiceTimingPolicy.StayDelta(", "includeStay: false", "IssueAdvanceCreditNoteAsync(", "SettleCheckOutAsync(",
               "LodgingDepositSettlement.RoomDeposits(", "LodgingDepositSettlement.PlanCheckout(", "LodgingPaymentBank.Problem(",
               "NewCheckoutChargesAsync(", "ApplyFinalDepositPlanAsync("],
         before=[("LodgingPaymentBank.Problem(", "IssueAdvanceCreditNoteAsync("), ("IssueAdvanceCreditNoteAsync(", "ApplyFinalDepositPlanAsync("),
                 ("LodgingDepositSettlement.PlanCheckout(", "UpsertFinalDraftAsync(")],
         forbid=["BillDiscountAmount", "DepositAppliedDrivesJournal"],
         why="ข้อ 157: เช็คเอาต์บนใบล่วงหน้า — ด่านก่อนแตะข้อมูล · ยอดลด = ใบลดหนี้ก่อนตัดชำระ · วางแผนมัดจำก่อนออกเลข (§86/4) · ปิดการเข้าพักด้วยขั้นเดิม"),
    dict(file=LODGING_OPS, method="MapAsync", must=["LoadAdvanceAsync(", "LodgingTaxInvoiceTimingPolicy.CanOfferManualIssue("],
         why="ข้อ 157: ปุ่ม “ออกใบกำกับภาษีล่วงหน้า” ตัดสินด้วยตัวเดียวกับตอนกด (server computes · page displays)"),
    dict(file=LODGING, method="Apply", must=["LodgingTaxInvoiceTimingPolicy.TimingProblem("],
         why="ข้อ 157: ค่าตั้งจังหวะผ่านด่านเดียว (โหมดยืนยันต้องออกเอกสารเต็มรูป) · null = คงค่าเดิม"),
    dict(file=LODGING, method="ToDtoAsync",
         must=["LodgingTaxInvoiceTimingPolicy.Options(", "LodgingTaxInvoiceTimingPolicy.PaymentInvoiceNote(", "TaxInvoiceTiming = p.TaxInvoiceTiming"],
         why="ข้อ 157: echo ค่าตั้ง + ตัวเลือก/ผลภาษีจากเซิร์ฟเวอร์ (หน้าเว็บไม่มีสำเนาข้อความ)"),
]

# ฝ่ายค้านทีม P (ข้อ 157): ล็อกต่อการจองตัวเดียวทุกเส้นที่แตะใบกำกับล่วงหน้า · แขกยกเลิกไม่เห็นข้อความภายใน · เงินเข้าไม่ล้มเพราะใบล่วงหน้า
RULES += [
    dict(file=LODGING_LIFE, method="CancelAsync", must=["WithReservationLockAsync(", "AdvisoryLockKey.LodgingConfirm", "CancelLockedAsync("],
         before=[("AdvisoryLockKey.LodgingConfirm", "CancelLockedAsync(")],
         why="ฝ่ายค้าน P ข้อ 1: ยกเลิก/no-show ถือล็อกเดียวกับเส้นรับเงิน — เงินเข้าพร้อมยกเลิกต้องไม่ได้ใบล่วงหน้าค้างบนการจองที่ยกเลิก"),
    dict(file=LODGING_LIFE, method="CancelByTokenAsync", must=["WithReservationLockAsync(", "AdvisoryLockKey.LodgingConfirm", "CancelByTokenLockedAsync("],
         why="ฝ่ายค้าน P ข้อ 1: แขกยกเลิกเองถือล็อกเดียวกับเส้นรับเงิน"),
    dict(file=LODGING_LIFE, method="CancelByTokenLockedAsync", must=["CancelCoreAsync("], must_lit=["LODGING-CANCEL-ADVANCE", "LODGING-CANCEL-FORFEIT"],
         why="ฝ่ายค้าน P ข้อ 2: ยกเลิกสำเร็จแล้วแต่ปรับใบล่วงหน้าไม่ครบ = งานพนักงาน — แขกไม่ได้ 400/ข้อความภายใน"),
    dict(file=LODGING_LIFE, method="CheckOutAsync", must=["WithReservationLockAsync(", "AdvisoryLockKey.LodgingConfirm", "ExclusiveCheckoutAsync("],
         before=[("AdvisoryLockKey.LodgingConfirm", "ExclusiveCheckoutAsync(")],
         why="ฝ่ายค้าน P ข้อ 1: ลำดับล็อก LodgingConfirm → LodgingCheckout ทุกเส้น (กัน deadlock) · เงินเข้าพร้อมเช็คเอาต์ไม่ได้ใบล่วงหน้าซ้อน"),
    dict(file=LODGING_LIFE, method="ConfirmCoreAsync", must=["LodgingTaxInvoiceTimingPolicy.AdvanceFailedOnMoneyNote("],
         before=[("LodgingTaxInvoiceTimingPolicy.AdvanceFailedOnMoneyNote(", "CreateDepositReceiptAsync(")],
         why="ฝ่ายค้าน P ข้อ 3: ใบล่วงหน้าล้ม ⇒ ถอยไปใบมัดจำตามประเภทเดิม (tax point = วันรับเงิน) ไม่ใช่รับเงินไม่ได้ทั้งก้อน"),
    dict(file=LODGING_TAX, method="AdvanceFactsAsync", must=["x.CompanyId == companyId", "fresh?.Status"],
         why="ฝ่ายค้าน P ข้อ 1: สถานะ/ใบเช็คเอาต์อ่านใหม่หลังได้ล็อก (ยกเลิก/เช็คเอาต์/เช็คอินแล้ว ⇒ ไม่ออก) · tenant (กฎ M)"),
    dict(file=LODGING_TAX, method="EnsureAdvanceTaxInvoiceAsync", must=["LodgingTaxInvoiceTimingPolicy.TimingAllowsAdvance("],
         before=[("LodgingTaxInvoiceTimingPolicy.TimingAllowsAdvance(", "AdvanceFactsAsync(")],
         why="ฝ่ายค้าน P ข้อ 5: ค่าเริ่มต้นไม่อ่านฐานเพิ่ม (กติกาข้อแรกของตัวตัดสินเดียว)"),
    dict(file=LODGING_TAX, method="IssueAdvanceTaxInvoiceCoreAsync", must=["DeleteDocumentAsync(", "d.ServiceUsedDate != null"],
         before=[("DeleteDocumentAsync(", "UpsertFinalDraftAsync(")],
         why="ฝ่ายค้าน P ข้อ 6: ใบร่างเช็คเอาต์ค้าง (มีวันใช้บริการ) ห้ามถูกใช้ต่อเป็นใบล่วงหน้า"),
    dict(file=LODGING_RES, method="CreateReservationAsync", must=["LodgingTaxInvoiceTimingPolicy.TimingAllowsAdvance("],
         before=[("LodgingTaxInvoiceTimingPolicy.TimingAllowsAdvance(", "TryIssueAdvanceOnStaffConfirmAsync(")],
         why="ฝ่ายค้าน P ข้อ 5: ค่าเริ่มต้นไม่ขอล็อก/ไม่อ่านฐานเพิ่มตอนสร้างการจอง"),
]

# ทีม L ฝ่ายค้านรอบ 1 — บัตรคอร์ส/เครดิต: ยกเลิกใบขาย/ลดหนี้ต้องผ่านด่านบัตร · เปิดบัตรยึดยอด JE · กันจองซ้ำ/ครูซ้อน · ขายกันกดซ้ำ
CC_SVC = "Services/Implementations/CourseCredit/CourseCreditService.cs"
CC_HOOK = "Services/Implementations/CourseCredit/CreditPassDocumentHook.cs"
RULES += [
    dict(file=DOC, method="VoidDocumentAsync",
         must=["CreditPassDocumentHook.OnVoidAsync(_db, companyId, doc,"],
         before=[("CreditPassDocumentHook.OnVoidAsync(", "await transaction.CommitAsync()")],
         why="ยกเลิกใบขายบัตรคอร์สที่ใช้แล้ว = 21713 ติดลบ + รายได้ที่รับรู้ไม่ถูกกลับ ⇒ ด่านในธุรกรรมเดียวกับการกลับรายการ"),
    dict(file=DOC, method="ApplySourceDocumentAdjustmentsAsync",
         must=["CreditPassDocumentHook.OnCreditNoteApprovedAsync(_db, companyId, doc, source,"],
         why="ใบลดหนี้อ้างใบขายบัตรคอร์ส ⇒ ได้เฉพาะส่วนที่ยังไม่รับรู้ทั้งก้อน + ปิดบัตรพร้อมกัน"),
    dict(file=CC_HOOK, method="OnVoidAsync",
         must=["CreditPassDocumentRule.OnVoidSale(", "CreditPassDocumentRule.OnVoidCreditNote(", "LockLivePassAsync("],
         why="ตัวตัดสินเดียว + ล็อกแถวบัตรก่อนตัดสิน"),
    dict(file=CC_HOOK, method="OnCreditNoteApprovedAsync",
         must=["CreditPassDocumentRule.OnCreditNote(", "LockLivePassAsync("],
         why="ตัวตัดสินเดียว + ล็อกแถวบัตรก่อนตัดสิน"),
    dict(file=CC_HOOK, method="Apply",
         must_re=[r"DocAction\.Block\)\s*throw new BusinessRuleException"],
         why="ผลบล็อกต้องโยนจริง (ไม่ใช่ log แล้วเดินต่อ)"),
    dict(file=CC_SVC, method="OpenPassFromDocumentAsync",
         must=["JournalLiabilityCreditsAsync(companyId, doc.Id, ct)", "CreditPassDocumentRule.ActivateProblem("],
         forbid=["_db.DocumentLines"],
         why="ยอดสัญญาจาก JE ที่ Cr ผังรายได้รับล่วงหน้าจริง (ใบเสร็จตัดลูกหนี้ห้ามเปิดบัตร) + ห้ามเปิดซ้ำในสายเอกสาร"),
    dict(file=CC_SVC, method="HoldAsync",
         must=["AdvisoryLockKey.CreditHold"],
         before=[("AdvisoryLockKey.CreditHold", "LockPassAsync(")],
         must_re=[r"LockPassAsync\([\s\S]*b\.ExternalRef == extRef"],
         why="ref เดียวกันสองคำขอพร้อมกันต้องรอกัน + ตรวจซ้ำหลังล็อกบัตร"),
    dict(file=CC_SVC, method="NewOneOnOneSessionAsync",
         before=[("LockTrainerDaysAsync(", "TrainerBusyAsync("), ("LockTrainerDaysAsync(companyId, eligible", "PickTrainer(")],
         why="ตรวจครูว่าง→สร้างรอบ ต้องอยู่ใต้ล็อกครูต่อวัน"),
    dict(file=CC_SVC, method="CreateSessionAsync",
         before=[("LockTrainerDaysAsync(", "TrainerBusyAsync(")],
         why="ตรวจครูว่าง→สร้างรอบ ต้องอยู่ใต้ล็อกครูต่อวัน"),
    dict(file=CC_SVC, method="SellAsync",
         must=["JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.CreditSale"],
         why="ขายคอร์สกันกดซ้ำด้วยคีย์ของหน้าจอ"),
]


# ทีม F (หัก ณ ที่จ่ายตามรายการ · ฝ่ายค้านข้อ 4/5): ทุกผู้อ่านที่ส่งยอดหัก ณ ที่จ่ายออกเป็น "บาท" ต้องเดินตัวคำนวณเดียว Helpers/PerLineWht —
# เดิมหน้านำส่ง/ปฏิทิน/ตัวกระทบ GL รวม d.WithholdingTaxAmount สกุลเอกสาร ⇒ ใบ USD ต่ำ ~36 เท่า (docs/PER_LINE_WHT_DESIGN.md)
_PLWHT = "ทีม F หัก ณ ที่จ่ายเป็นบาท: "
RULES += [
    dict(file="Services/Implementations/WithholdingTaxCertService.cs", method="AutoGenerateFromDocumentAsync",
         must=["PerLineWht.ForDocument(doc)", "PerLineWht.DocumentWhtThb(", "PerLineWht.TieOut("],
         why=_PLWHT + "50 ทวิ ของใบต่างสกุลเป็นบาทต่อบรรทัด + ผูก Σ กับยอด GL"),
    dict(file="Services/Implementations/TaxService.cs", method="GenerateWhtReport",
         must=["PerLineWht.ForDocument(doc)", "PerLineWht.DocumentWhtThb("],
         why=_PLWHT + "แถวเตือน \"ยังไม่ออกใบ\" ของ ภ.ง.ด. เป็นบาทเหมือน 50 ทวิ ที่จะออก"),
    dict(file="Services/Implementations/StatutoryRemittanceService.cs", method="GetDashboardAsync",
         must_re=[r"PerLineWht\.DocumentWhtThb\(\s*d\.WithholdingTaxAmount\s*,\s*d\.ExchangeRate\)",
                  r"PerLineWht\.DocumentWhtThb\(\s*p\.WithholdingTaxAmount\s*,\s*d\.ExchangeRate\)"],
         why=_PLWHT + "หน้านำส่ง: แถวช่องโหว่ (เอกสารที่ยังไม่มี 50 ทวิ) เป็นบาท"),
    dict(file="Services/Implementations/StatutoryRemittanceService.cs", method="GetFilingCalendarAsync",
         must_re=[r"PerLineWht\.DocumentWhtThb\(\s*d\.WithholdingTaxAmount\s*,\s*d\.ExchangeRate\)",
                  r"PerLineWht\.DocumentWhtThb\(\s*p\.WithholdingTaxAmount\s*,\s*d\.ExchangeRate\)"],
         why=_PLWHT + "ปฏิทินยื่น: ช่องโหว่ 50 ทวิ เป็นบาท"),
    dict(file="Services/Implementations/TaxGlReconciliationService.cs", method="BuildWhtLineAsync",
         must=["PerLineWht.DocumentWhtThb(d.WithholdingTaxAmount, d.ExchangeRate)"],
         forbid=["noCert.Sum(d => d.WithholdingTaxAmount)"],
         why=_PLWHT + "ตัวกระทบ GL: สาเหตุ \"ยังไม่ออก 50 ทวิ\" เป็นบาท"),
    dict(file=DOC, method="CreatePaymentJournalAsync",
         must=["PerLineWht.DocumentWhtThb(payment.WithholdingTaxAmount, fx)"],
         why=_PLWHT + "JE ตอนจ่าย: ขา WHT ฐานเดียวกับ 50 ทวิ รายงวด (อัตราของเอกสาร)"),
    dict(file=DOC, method="CreateDocumentAsync",
         must=["ApplyDocumentFxDecisionAsync(companyId, doc, request)"],
         why=_PLWHT + "สร้างเอกสาร: อัตรา/ที่มา/ฐานบาทผ่าน DocumentFx.Decide ตัวเดียว"),
    dict(file="Services/Implementations/OcrService.cs", method="BuildScanLinesAsync",
         must=["OcrLineWhtPlan.Decide(", "PerLineWhtEnabledAsync(companyId)"],
         why=_PLWHT + "OCR แผนต่อบรรทัดเฉพาะเมื่อเปิดค่าตั้ง"),
    dict(file="Services/Implementations/OcrService.cs", method="CreateDocumentFromScanCoreAsync",
         must=["OcrThbEquivalent.Plan(", "scanBotRate)"],
         why=_PLWHT + "OCR ยอดบาทบนใบต่างสกุล: หลักฐานแหล่งเดียวต้องเทียบอัตรา ธปท."),
]

# ทีม O 2026-10-22 (คำสั่งเจ้าของ): "JE ไหนนับเข้ารายงาน" ผ่าน Helpers/ReportableJournal ตัวเดียว — เดิม ภ.ง.ด.50/51 · FP&A · งบรวม · ECL ·
# กระทบยอดสต็อก กรอง Posted อย่างเดียว ⇒ ใบเดิมที่ถูกกลับรายการ (Reversed) หาย แต่ใบกลับรายการ (Posted) ยังนับ ⇒ ยอดถูกหักซ้ำ (ฐานภาษีต่ำกว่าจริง)
_RJ_LINES = ".WherePeriodActivity("
_RJ_BAL = ".WhereInLedger("
_RJ_INLINE = "ReportableJournal.LedgerStatuses.Contains("
_RJ_WHY = "ทีม O 2026-10-22: รวมยอดรายงานต้องนับคู่กลับรายการ (Posted + Reversed) ผ่าน ReportableJournal ตัวเดียว — ห้ามกรอง Posted เอง"
_TAX = "Services/Implementations/TaxService.cs"
RULES += [
    dict(file=_TAX, method="GenerateCitReport", must=["CitPeriodLines("], forbid=["JournalEntryStatus.Posted", "IsClosingEntry"],
         why=_RJ_WHY + " · ภ.ง.ด.50 ใช้ฐานบรรทัดเดียวกับตัวเทียบความสด"),
    dict(file=_TAX, method="BuildCitFreshnessAsync", must=["CitFreshTotalsAsync("], forbid=["JournalEntryStatus.Posted", "IsClosingEntry"],
         why=_RJ_WHY + " · ตัวเทียบความสดต้องคิดฐานเดียวกับตอนสร้าง (ต่างกัน = ฟ้องทุกใบตลอดกาล)"),
    dict(file=_TAX, method="CitFreshTotalsAsync", must=["CitPeriodLines("], forbid=["JournalEntryStatus.Posted"], why=_RJ_WHY),
    dict(file=_TAX, method="CitPeriodLines", must=[_RJ_LINES, "l.JournalEntry.CompanyId == companyId"], forbid=["JournalEntryStatus.Posted"],
         why=_RJ_WHY + " · tenant isolation (กฎ M)"),
    dict(file=_TAX, method="GetCitRecomputeReviewAsync", must=["BuildCitFreshnessAsync(", "r.CompanyId == companyId", "TaxReportRecompute.SuggestedAction("],
         forbid=["SaveChangesAsync("], why="ทีม O: รายงานทบทวน ภ.ง.ด.50 ที่บันทึกไว้ = อ่านอย่างเดียว · ฐานเดียวกับตัวเทียบความสด"),
    dict(file=_TAX, method="RegenerateTaxReportAsync", must=["AddChainedAuditLog(", "TaxReportRecompute.AuditPayload("],
         why="ทีม O (หลักการ #9): คำนวณรายงานใหม่ต้องทิ้งร่องรอยยอดเดิม→ยอดใหม่ใน audit chain"),
    dict(file="Services/Implementations/TaxFilingExportService.cs", method="ExportPnd51Async", must=[_RJ_LINES],
         forbid=["JournalEntryStatus.Posted"], why=_RJ_WHY + " · ภ.ง.ด.51 ฐานเดียวกับ ภ.ง.ด.50"),
    dict(file="Services/Implementations/FinancialManagementService.Part2.cs", method="CalculateCITAsync", must=[_RJ_LINES],
         forbid=["JournalEntryStatus.Posted"], why=_RJ_WHY),
    dict(file="Services/Implementations/FinancialManagementService.Part2.cs", method="CreateProfitAppropriationAsync", must=[_RJ_BAL],
         forbid=["JournalEntryStatus.Posted"], why=_RJ_WHY),
    dict(file="Services/Implementations/FpaService.cs", method="CalculateRatiosAsync", must=[_RJ_BAL], forbid=["JournalEntryStatus.Posted"], why=_RJ_WHY),
    dict(file="Services/Implementations/FpaService.cs", method="CalculateBreakEvenAsync", must=[_RJ_LINES], forbid=["JournalEntryStatus.Posted"], why=_RJ_WHY),
    dict(file="Services/Implementations/FpaService.cs", method="GetMonthlyGlDataAsync", must=[_RJ_LINES], forbid=["JournalEntryStatus.Posted"], why=_RJ_WHY),
    dict(file="Services/Implementations/ConsolidationService.cs", method="GenerateConsolidatedPnLAsync", must=[_RJ_LINES],
         forbid=["JournalEntryStatus.Posted"], why=_RJ_WHY),
    dict(file="Services/Implementations/ConsolidationService.cs", method="GetAccountBalances", must=[_RJ_BAL], forbid=["JournalEntryStatus.Posted"], why=_RJ_WHY),
    dict(file="Services/Background/EclAllowanceJob.cs", method="ProcessCompanyAsync", must=[_RJ_INLINE],
         forbid=["l.JournalEntry.Status == JournalEntryStatus.Posted"], why=_RJ_WHY + " · ยอดค่าเผื่อผิด ⇒ JE ปรับปรุงผิด"),
    dict(file="Services/Implementations/ProductService.cs", method="CreateInventorySnapshotAsync", must=[_RJ_INLINE],
         forbid=["l.JournalEntry.Status == JournalEntryStatus.Posted"], why=_RJ_WHY + " · ยอด GL 115 ผิด ⇒ JE ปรับสต็อกผิด"),
    dict(file="Services/Implementations/ProductService.cs", method="GetInventoryGlTieOutAsync", must=[_RJ_INLINE],
         forbid=["l.JournalEntry.Status == JournalEntryStatus.Posted"], why=_RJ_WHY),
    dict(file="Controllers/CashForecastController.cs", method="Get", must=[_RJ_INLINE],
         forbid=["l.JournalEntry.Status == JournalEntryStatus.Posted"], why=_RJ_WHY),
    # ฝ่ายค้านรอบแรกของทีม O: "ตัดแค่ใบเดิม" (Posted && ReversedByEntryId == null) ⇒ ใบกลับรายการติดลบ
    dict(file="Services/Payments/GatewaySettlementService.cs", method="LoadFeeVatAgingAsync", must=[_RJ_INLINE],
         why=_RJ_WHY + " · ใบกลับรายการคัดลอก Tags ⇒ เดิม claimed = −X ⇒ VAT ค่าธรรมเนียมเคลมได้เกิน 2 เท่า"),
    dict(file=DOC, method="GetDepositsAsync", must=[_RJ_INLINE], why=_RJ_WHY + " · มัดจำที่ยกเลิกต้องไม่ติดลบ"),
    dict(file=DOC, method="GetDepositDiagnosticsAsync", must=[_RJ_INLINE], why=_RJ_WHY),
    dict(file=DOC, method="GetDepositCenterAsync", must=[_RJ_INLINE], why=_RJ_WHY),
    dict(file=DOC, method="CheckBudgetCommitmentAsync", must=["ReportableJournal.WherePeriodActivity("], why=_RJ_WHY + " · ไม่นับใบปิดบัญชี"),
    dict(file="Services/Background/EclAllowanceJob.cs", method="ProcessCompanyAsync", must=["EclAllowanceBasis.ChangeNote(", "notifications.SendAsync("],
         why="ฝ่ายค้านข้อ 3 (หลักการ #7): ฐานค่าเผื่อเปลี่ยนจากการแก้ ⇒ บอกใน JE + แจ้งเจ้าของ ไม่ลงเงียบ"),
    dict(file="Services/Implementations/AccountingService.cs", method="LoadSourceDocumentMapAsync", must=["d.CompanyId == companyId"],
         why="ทีม O 2026-10-22: กฎ M — แผนที่เอกสารต้นทางของ drill-down ต้องกรองบริษัท"),
]

# ── ทีม Q (คำตัดสินข้อ 156): มัดจำออเดอร์ร้านออนไลน์ — ทุกทางเข้า (webhook/สลิป/พนักงานยืนยัน · จัดส่ง/ส่งมอบ · ปุ่ม sync ERP · ยกเลิก)
#    เดินตัวตัดสินเดียว Helpers/SiteOrderDepositInvoicing · ใบสุดท้ายหักมัดจำผ่านตัววางแผนเดียวกับเช็คเอาต์ที่พัก (เทสต์ SiteOrderDepositInvoicingTests) ──
_SITE = "Services/Implementations/CmsCommerceService.cs"
_Q_WHY = "ทีม Q ข้อ 156: มัดจำออเดอร์ออนไลน์ — "
RULES += [
    dict(file=_SITE, method="ConfirmPaymentAsync",
         must=["SiteOrderDepositInvoicing.OnPayment(", "IssueOrderDepositReceiptAsync(", "SettleFinalInvoiceAsync(",
               "ApproveOrderDocumentIfPendingAsync(", "plan.DeductsStock", "plan.RecordsPaymentOnSalesDocument"],
         before=[("SiteOrderDepositInvoicing.OnPayment(", "IssueOrderDepositReceiptAsync("),
                 ("SiteOrderDepositInvoicing.OnPayment(", "DeductStockAsync(")],
         why=_Q_WHY + "ทุกเงินที่ยืนยัน (webhook · สลิป · พนักงาน) ต้องผ่านตัวตัดสินก่อนออกเอกสาร/ตัดสต็อก · ใบมัดจำห้ามบันทึกรับชำระซ้ำ/ตัดสต็อก"),
    dict(file=_SITE, method="UpdateOrderStatusAsync",
         must=["SiteOrderDepositInvoicing.SettlesOnHandover(", "SettleOnHandoverAsync(", "SiteOrderDepositInvoicing.CancelNotice("],
         before=[("SettleOnHandoverAsync(", "ApplyStatus(order)")],
         why=_Q_WHY + "จัดส่ง/ส่งมอบ ⇒ ใบส่วนที่เหลือ + ตัดสต็อกก่อนเปลี่ยนสถานะ · ยกเลิกที่มีมัดจำค้างต้องบอกทางไปต่อ"),
    dict(file=_SITE, method="SettleOnHandoverAsync",
         must=["SettleFinalInvoiceAsync(", "DeductStockAsync("],
         why=_Q_WHY + "ส่งมอบต้องออกใบขาย + ตัดสต็อก (ทำซ้ำได้)"),
    dict(file=_SITE, method="SettleFinalInvoiceAsync",
         must=["PlanFinalDeductionAsync(", "SyncOrderToErpCoreAsync(", "ApproveOrderDocumentIfPendingAsync(",
               "LodgingDepositSettlement.ApplyCandidates(", "ApplyDepositToInvoiceAsync("],
         before=[("PlanFinalDeductionAsync(", "SyncOrderToErpCoreAsync(")],
         why=_Q_WHY + "ใบส่วนที่เหลือวางแผนหักมัดจำก่อนออกเลข (§86/4) · มัดจำไม่มี VAT ตัดชำระ (ไม่ตัดซ้ำ)"),
    dict(file=_SITE, method="PlanFinalDeductionAsync",
         must=["LodgingDepositSettlement.PlanCheckout(", "DocumentService.PreviewTotals(", "BuildOrderErpLines("],
         why=_Q_WHY + "ตัววางแผน/ตัวคิดยอดตัวเดียวกับเช็คเอาต์ที่พัก — ห้ามสูตรหักมัดจำของตัวเอง"),
    dict(file=_SITE, method="SyncOrderToErpAsync",
         must=["PlanFinalDeductionAsync("],
         why=_Q_WHY + "ปุ่ม sync ERP มือต้องหักมัดจำที่ออกใบกำกับแล้ว (ไม่หัก = ภาษีขายซ้ำ)"),
    dict(file=_SITE, method="IssueOrderDepositReceiptAsync",
         must=["IsDeposit: true", "DepositOutputVatDeferred: false", "SiteOrderDepositInvoicing.DepositLineVatRate(",
               "pay.DepositDocumentId = created.Id", "ApproveOrderDocumentIfPendingAsync(", "o.CompanyId == companyId"],
         forbid=["CreatePaymentAsync(", "DeductStockAsync("],
         before=[("pay.DepositDocumentId = created.Id", "ApproveOrderDocumentIfPendingAsync(")],
         why=_Q_WHY + "ใบรับเงินมัดจำ = เส้นใบเสร็จมัดจำเดิม (VAT ทันที §78(1)(ข)) · ผูกก่อนอนุมัติ (ไม่ออกซ้ำ) · ไม่บันทึกรับชำระซ้ำ/ไม่ตัดสต็อก"),
    dict(file=_SITE, method="LoadOrderDepositSnapshotsAsync",
         must=["d.CompanyId == companyId", "d.IsDeposit"],
         why=_Q_WHY + "tenant isolation (กฎ M) · เฉพาะใบมัดจำ"),
    dict(file=_SITE, method="CreateOrderAsync",
         must=["order.DepositInvoicingMode = checkout.DepositInvoicing"],
         why=_Q_WHY + "ตรึงวิธีออกเอกสารลงออเดอร์ตอนสั่ง (เปลี่ยนค่าตั้งภายหลังไม่เปลี่ยนออเดอร์ที่ค้าง)"),
    # ── หลังฝ่ายค้านทีม Q (ข้อ 1–4, 6) ──
    dict(file=_SITE, method="SettleFinalInvoiceAsync", must=["EnsureOrderDepositsApprovedAsync("],
         before=[("EnsureOrderDepositsApprovedAsync(", "PlanFinalDeductionAsync(")],
         why=_Q_WHY + "ฝ่ายค้านข้อ 1: ใบมัดจำร่างต้องอนุมัติก่อนวางแผนหัก (ไม่งั้นใบขาย VAT เต็ม แล้วอนุมัติใบร่าง = VAT ซ้ำ)"),
    dict(file=_SITE, method="SyncOrderToErpAsync", must=["EnsureOrderDepositsApprovedAsync("],
         before=[("EnsureOrderDepositsApprovedAsync(", "PlanFinalDeductionAsync(")],
         why=_Q_WHY + "ฝ่ายค้านข้อ 1: ปุ่ม sync ERP มือเดินด่านใบมัดจำร่างเดียวกัน"),
    dict(file=_SITE, method="ConfirmPaymentAsync",
         must=["DiscardDraftDepositThenPayOnFinal", "_docService.DeleteDocumentAsync(", "pay.ErpPaymentId = erpPay.Id", "PendingPaymentsNote("],
         why=_Q_WHY + "ฝ่ายค้านข้อ 1/3: ใบร่าง + ใบขาย ⇒ ลบใบร่าง (ห้ามอนุมัติ) · ผูกรายการรับชำระ ERP · เงินงวดอื่นที่ค้างต้องดัง"),
    dict(file=_SITE, method="GetOrderAsync", must=["PendingPaymentActionsAsync(", "p.PendingAction ="],
         why=_Q_WHY + "ฝ่ายค้านข้อ 2: ปุ่มทำขั้นที่ค้างมาจากเซิร์ฟเวอร์ (ทุกหมายเหตุที่บอกให้กดต้องมีปุ่ม)"),
    dict(file=_SITE, method="PendingPaymentActionsAsync", must=["SiteOrderDepositInvoicing.PaymentPendingAction(", "d.CompanyId == companyId", "p.CompanyId == companyId"],
         why=_Q_WHY + "ฝ่ายค้านข้อ 2: ป้ายจากตัวตัดสินเดียว · tenant isolation"),
    dict(file=_SITE, method="DeductStockAsync", must=["SiteOrderDepositInvoicing.StockDeductProblem("],
         why=_Q_WHY + "ฝ่ายค้านข้อ 6: ตัดสต็อกมือก่อนมีใบขายในโหมดมัดจำ ⇒ ปฏิเสธ"),
    dict(file=_SITE, method="UpdateOrderStatusAsync", must=["handoverNotes"],
         why=_Q_WHY + "ฝ่ายค้านข้อ 4: ใบขายออกแล้วแต่ตัดสต็อกล้ม ⇒ ส่งมอบได้ + หมายเหตุ"),
    dict(file="Controllers/CmsCommerceConfigController.cs", method="UpdateConfig",
         must=["DepositInvoicingMode.Normalize("],
         why=_Q_WHY + "ค่าตั้งที่ไม่รู้จัก = 400 (ห้ามเก็บค่าที่ไม่มีผู้อ่าน)"),
]

# ── ทีม S 2026-10-26 (ผู้ใช้รายงาน · การจอง #149433): JE จาก API ตัดมัดจำที่ถูกกลับรายการแล้ว ⇒ ด่านสองทิศ + ธงภาษีขายไม่มีใบกำกับ ──
_S_WHY = "ทีม S 2026-10-26 (JV-INT ตัด 21510 ของมัดจำที่ถูกกลับแล้ว): "
RULES += [
    dict(file=INTEG, method="ProcessJournalAsync",
         must=["BookingDepositLedger.DrawdownProblemAsync(", "BookingDepositDrawdown.RuleCode", "BookingDepositDrawdown.OutputVatNoInvoiceNote(",
               "Warnings: journalWarnings", "Note = journalNote", "BookingDepositDrawdown.JournalApiEventType"],
         must_re=[r"is\s+string\s+overdraw\s*\)\s*throw\b"],
         before=[("RunAtomicCreateAsync(", "DrawdownProblemAsync("), ("DrawdownProblemAsync(", "_db.JournalEntries.Add(je)")],
         why=_S_WHY + "ตัดมัดจำเกินยอดคงเหลือของการจอง ⇒ ปฏิเสธก่อนบันทึก (422 + ruleCode) · ภาษีขายไม่มีใบกำกับ ⇒ ติดธงในคำตอบ+หมายเหตุ JE"),
    dict(file=INTEG, method="ProcessJournalReverseAsync",
         must=["BookingDepositLedger.RemovalProblemAsync(", "BookingDepositDrawdown.RemovalRuleCode"],
         before=[("RemovalProblemAsync(", "_db.JournalEntries.Add(reversal)")],
         why=_S_WHY + "ทิศกลับ: API กลับ JE มัดจำที่ถูกตัดแล้ว ⇒ 409"),
    dict(file=INTEG, method="HandleSyncError",
         must=["RuleCode: bre.RuleCode", "StatusCode: bre.StatusCode"],
         why=_S_WHY + "ระบบต้นทางต้องเห็น ruleCode + status ของการปฏิเสธตามกติกา"),
    dict(file=DOCSVC, method="VoidDocumentAsync",
         must=["BookingDepositLedger.RemovalProblemAsync(", "BookingDepositDrawdown.RemovalRuleCode"],
         before=[("RemovalProblemAsync(", "BeginTransactionAsync(")],
         why=_S_WHY + "ทิศกลับ: ยกเลิกใบมัดจำที่มี JE ตัดผ่านรายการอยู่ ⇒ 409 พร้อมทางไปต่อ (ก่อนแตะธุรกรรม)"),
    dict(file="Services/Implementations/AccountingService.cs", method="ReverseJournalEntryAsync",
         must=["BookingDepositLedger.RemovalProblemAsync(", "BookingDepositDrawdown.RemovalRuleCode"],
         must_re=[r"!\s*systemTriggered\s*&&\s*await\s+Accounting\.Helpers\.BookingDepositLedger\.RemovalProblemAsync\("],
         why=_S_WHY + "ทิศกลับ: ผู้ใช้กลับ JE มัดจำ (สมุดรายวัน) ที่ถูกตัดแล้ว ⇒ 409"),
    dict(file="Helpers/BookingDepositLedger.cs", method="LoadChunkAsync",
         must=["WhereInLedger()", "d.CompanyId == companyId", "j.CompanyId == companyId", "a.CompanyId == companyId",
               "l.JournalEntry.CompanyId == companyId", "BookingDepositDrawdown.IsDepositLiabilityAccount(", "Take(RowCap + 1)",
               "truncated = true", "BookingDepositDrawdown.ExtractKeys("],
         forbid=["JournalEntryStatus.Posted", "Take(500)", "Take(2000)"],
         why=_S_WHY + "ยอดคงเหลือนับคู่กลับรายการด้วย ReportableJournal (ห้ามกรอง Posted เอง) · tenant isolation ทุก query · เกินเพดาน ⇒ Truncated ไม่ตัดผลเงียบ · จับคู่ตรงตัว"),
    dict(file="Helpers/BookingDepositLedger.cs", method="DrawdownProblemAsync",
         must=["BookingDepositDrawdown.AssignDrawdown(", "AdvisoryLockKey.BookingDeposit", "snap.Truncated"],
         must_lit=["pg_advisory_xact_lock"],
         before=[("AdvisoryLockKey.BookingDeposit", "LoadManyAsync(")],
         why=_S_WHY + "ฝ่ายค้านข้อ 1: แยกยอดต่อการจองตามบรรทัด · ล็อกต่อการจองก่อนอ่านยอด (กันสองคำขอพร้อมกัน) · ข้อมูลไม่ครบ ⇒ ไม่ตัดสิน"),
    dict(file=INTEG, method="ProcessDailySummaryAsync",
         must=["BookingDepositLedger.DrawdownProblemAsync(", "BookingDepositDrawdown.RuleCode", "RunAtomicCreateAsync("],
         before=[("DrawdownProblemAsync(", "_db.JournalEntries.Add(journalEntry)")],
         why=_S_WHY + "ฝ่ายค้านข้อ 4: สรุปรายวันเดินด่านมัดจำตัวเดียวกัน"),
    dict(file="Services/Implementations/DataIntegrityReportService.cs", method="LoadBookingDepositOverdrawnAsync",
         must=["BookingDepositLedger.LoadManyAsync(", "BookingDepositDrawdown.OverdrawnInOrder(", "j.EntryDate >= since",
               "j.CompanyId == companyId", "a.CompanyId == companyId"],
         forbid=["ReverseJournalEntryAsync(", "SaveChangesAsync("],
         why=_S_WHY + "หลักการ #9: รายงานอ่านอย่างเดียว — ห้ามกลับรายการ/บันทึกเอง"),
    dict(file="Services/Implementations/DataIntegrityReportService.cs", method="LoadApiJournalOutputVatAsync",
         must=["BookingDepositDrawdown.JournalApiEventType", "s.CompanyId == companyId", "j.CompanyId == companyId"],
         forbid=["SaveChangesAsync("],
         why=_S_WHY + "ภาษีขายจาก JE API ที่ไม่มีใบกำกับ — อ่านอย่างเดียว"),
    dict(file="Services/Implementations/TaxGlReconciliationService.cs", method="ReconcileAsync",
         must=["BookingDepositDrawdown.JournalApiEventType"],
         must_lit=["BookingDepositDrawdown.OutputVatNoInvoiceCode"],
         why=_S_WHY + "ตัวกระทบภาษี-GL ระบุ JE จาก API ที่มีภาษีขายไม่มีใบกำกับ"),
]

def main() -> int:
    errs = []
    # 2026-10-08: กติกาที่สะกดคีย์ผิด (เช่น forbid_re) เคยถูกข้ามเงียบ = ด่านที่ไม่มีอยู่จริง — คีย์ที่ไม่รู้จักต้องฟ้อง
    for rule in RULES:
        bad = set(rule) - _RULE_KEYS
        if bad:
            errs.append(f"{rule.get('file')}:{rule.get('method')} มีคีย์กติกาที่ตัวตรวจไม่รู้จัก {sorted(bad)} — กติกานี้จะไม่ถูกตรวจ")
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
    marker_files = notes_marker_files()
    errs += notes_marker_errors(marker_files)
    st = (self_test() + folder_forbid_self_test(ocr_files) + settlement_folder_self_test(settle_files)
          + notes_marker_self_test(marker_files))
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
