# CHANGELOG — ประวัติการเปลี่ยนแปลงราย "รอบ" (ย้ายจาก DOCUMENT_FLOW.md 2026-09-18)

> ไฟล์นี้คือ **log** — append อย่างเดียว ไม่แก้ย้อนหลัง · `DOCUMENT_FLOW.md` คือ **สถานะปัจจุบัน** ·
> ทุกคอมมิตที่เปลี่ยน flow: แก้ DOCUMENT_FLOW §ที่เกี่ยวข้อง + เติมบล็อก `_Last verified against codebase: <วันที่> (รอบ NNN — … — commit <sha>)_`
> ท้ายไฟล์นี้ **และ** แทนบล็อกล่าสุดใน DOCUMENT_FLOW §10 ในคอมมิตเดียวกัน · sha เติมในคอมมิตตามหลัง ห้าม amend
> (`tools/doc_commit_sha_check.py` ตรวจไฟล์นี้ด้วย)
>
> ที่มา: REGRESSION_ROOT_CAUSE_2026-09-18 §7.2 RC-3 / §7.4 — DOCUMENT_FLOW โตเป็น 0.8 MB จาก 179 บล็อกประวัติ
> จนสถานะปัจจุบันผิด (เช่น "สองความจริงของสต็อก") แล้วไม่มีใครเห็น

---

_รอบ 17: Payroll import endpoint (TakeTime) — POST /payroll/runs/import รับ
ยอดสำเร็จรูปต่อพนักงาน → run สถานะ Calculated (ไม่ recalc) → approve/pay/
exports เดิมออก GL+ภงด.1+สปส.1-10+50ทวิ+payslip จากยอดที่ส่งมา. idempotent
(ExternalRunRef + unique index), validate net=gross−หักลูกจ้าง, account override
(salary/payment code) ลง JE. + integration outbound document attachments[]._

_รอบ 18: OCR contact address ครบ + กทม. แสดง แขวง/เขต ถูกต้อง. ปัญหา: OCR ผ่าน
API → เอกสารที่อยู่ กทม. ขึ้น "ตำบล/อำเภอ" (ผิด ต้องเป็น "แขวง/เขต") + ผู้ติดต่อ
ไม่มีที่อยู่จนกด "ดึงข้อมูล" เอง. แก้ 4 ชั้น:
(1) `OcrService.EnrichContactAddress` — contact ที่ match จากของเดิม เติมทั้ง
free-text + structured (เดิมเติมแค่ free-text) จาก DbdAddress ก่อน VendorAddress;
ทับเฉพาะเมื่อ DBD ยืนยัน + contact OCR-managed.
(2) `PdfGenerationService.FormatThaiAddress` — เมื่อ structured locality ว่าง
parse free-text ผ่าน `ThaiAddressParser` ตอน render → กทม. ได้ แขวง/เขต ครบทุก
เอกสารโดยไม่ต้อง migrate; แก้ token-strip เป็น word-aware (เดิม substring replace
ทำ "บางนาตราด"→"ตราด"); 50ทวิ payee/company address route ผ่าน FormatThaiAddress.
(3) `ThaiAddressParser` — StreetRegex/BuildingNameRegex หยุดที่ marker เขตปกครอง
(ไม่กลืนชื่อตำบล) + `ExtractStreetHead` รักษาส่วนหัวเต็ม (ห้อง/ชั้น/อาคาร/ซอย/ถนน).
(4) `DocumentService.GetContactAsync` — lazy backfill structured จาก free-text
ตอนเปิดฟอร์ม (self-heal contact เก่า ไม่ต้องกด "ดึงข้อมูล")._

_รอบ 19: OCR header subtotal ผูกกับ grand total — แก้ "รายงานยอด 530 แต่ใบพิมพ์/
JE = 630". เคส: OCR แกะ subtotal (630) ไม่ตรง grand total (530) โดยไม่มี VAT/ส่วนลด
อธิบาย → `CreateDocumentFromScanAsync` fallback line (items==0) ใช้ headerSubTotal
(630) แต่ document.TotalAmount = headerTotal (530) → report (อ่าน TotalAmount) ≠
print/JE (อ่าน line). แก้: headerSubTotal ใช้ ExtractedSubTotal เฉพาะตอน tie กับ
grand total (subtotal+VAT−discount=total); ไม่งั้น derive จาก grand total (ตัวเลข
ที่พาร์ทเนอร์/ใบส่งมา = ตัวตั้งต้นเชื่อถือได้สุด) → line/subtotal/total แตกกันไม่ได้.
หมายเหตุ: แก้เฉพาะ doc ที่สร้างใหม่ — เอกสารเดิมที่ผิดต้องลบแล้ว re-OCR._

_รอบ 20: OCR เชื่อค่าเงินจากระบบภายนอก (override OCR vision). `ScanAsync` →
`ApplyExternalAmountOverrides(extractedData, metadata)` หลัง EnrichFromRawText:
อ่านยอดที่พาร์ทเนอร์กรอกมาใน metadata (top-level หรือ nested "amounts") —
total/totalAmount/grandTotal/amount, subTotal, vat/vatAmount, wht/whtAmount|whtRate,
และ "lineItems":[{description,quantity,unitPrice,amount}] — เขียนทับค่าที่ OCR แกะ
จากรูป (กันอ่านเลขผิด 530↔630) + ตั้ง FieldConfidence=1.0 + re-sync เข้า scanResult
ก่อน dup-check/serialize/auto-create. เพิ่ม body metadata ให้ endpoint POST
/ocr/scan/{fileId} (flow 2 ขั้น) ด้วย (`OcrScanMetadataRequest{Metadata,Engine}`).
fallback chain กฎเหล็ก #3: partner-provided > OCR vision. Fail-safe: metadata
เพี้ยน → คงค่า OCR._

_รอบ 21: ภ.พ.30 ภาษีซื้อ = 0 ทั้งที่มียอด — `GenerateVatReport`
(`TaxService.cs`) loop จัด input VAT จาก PurchaseInvoice/Expense/CertificateInLieu
เท่านั้น **ไม่มี branch ของ PaymentVoucher** → ใบสำคัญจ่ายที่ติ๊ก "ใช้งานใบกำกับ
ภาษี" (HasTaxInvoiceReference=true) ภาษีซื้อตกหล่นทั้งหมด (JE ของ PV มี
SourceDocumentId → JE-only fallback ก็ข้าม). แก้: เพิ่มเงื่อนไข
`|| (DocumentType==PaymentVoucher && HasTaxInvoiceReference)` เข้า branch ภาษีซื้อ
(ใช้ §82/5 prohibited + §82/3 window เดิม). PV ที่ไม่ติ๊ก = ไม่เคลม (§82/5(1)).
มีผลทั้งจอ + CSV ยื่น (ComputeVatReportAsync → GenerateVatReport ตัวเดียวกัน)._

_รอบ 22: หน้านำส่งภาษี/ประกันสังคมรวม (StatutoryRemittance) — สปส.1-10 + ภงด.1/3/53
+ ภพ.30 ในที่เดียว (pattern QuickBooks Pay Liabilities). `StatutoryRemittanceService`
.GetDashboardAsync รวมยอดค้าง (SSO=PayrollRun Paid ที่ยังไม่ settle; ภงด.1=Total
WithholdingTax; ภงด.3/53=เอกสาร WHT แยกชนิดผู้ติดต่อ; ภพ.30=ComputeVatReportAsync
.NetVat) + กำหนดยื่น/overdue/เงินเพิ่ม §49. .RemitAsync post JE ล้างหนี้ค้างจ่าย/Cr
ธนาคาร (VAT: Dr 21911/Cr 11610/Cr ธนาคาร net), บันทึก remittance (unique/งวด กัน
จ่ายซ้ำ), stamp PayrollRun.SsoSettledAt, แนบใบเสร็จ (FileAttachment "StatutoryRemittance").
รองรับทั้งบริษัทรันเงินเดือนในระบบ (ตั้งค้างจ่าย 21815 อัตโนมัติ) + ทำข้างนอก.
หน้า /pages/tax-remittance.html. Endpoints GET/POST /companies/{id}/remittances._

_รอบ 23 (ชุดแก้ + ปรับปรุง): (a) รายงานภาษีซื้อ/ขายบนจอ + พิมพ์ → ฟอร์มราชการ §87
(ฉบับที่ 104): GetTaxReportAsync เติม InvoiceNumber/BranchCode/CompanyName-TaxId ต่อ
บรรทัด; tax.html ตารางคอลัมน์ราชการ + ปุ่ม "พิมพ์ฟอร์มราชการ". (b) WHT cert 50ทวิ:
GenerateWithholdingTaxCertPdfAsync ลอง render HTML (BuildWithholdingTaxCertHtml +
ลายเซ็น) ผ่าน IHtmlPdfRenderer/Puppeteer ก่อน → fallback QuestPDF (font Sarabun:
ThaiFontCandidatePaths เพิ่ม Windows/macOS/Fonts bundle); auto-attach เข้า PV ปิด
default ผ่าน CompanySettings.AutoAttachWhtCertPdf. (c) StatutoryRemittance ภพ.30
เปลี่ยนเป็นอ่าน TaxReports ที่ generate แล้ว (เลิกคำนวณสด — กันหน้าค้าง) + per-section
try/catch. (d) documents list: server-side types[] filter + DocumentPermissionHelper
Other=rev&&pur (กัน paging หายสำหรับ owner) + count bar. (e) OCR amount: external
metadata override (เชื่อยอดที่ partner ส่ง) + headerSubTotal ผูก grand total._

_รอบ 24 (UX หน้านำส่งภาษี/ประกันสังคม): (a) ถอด Floating Action Button "＋ Quick"
ออกทั้งระบบ (layout.js — ปุ่มลอยมุมขวาล่างบังเนื้อหา; ทางลัดยังอยู่ใน sidebar +
mobile bottom-nav). (b) "แหล่งเงิน (บัญชีจ่าย)" ในโมดัลนำส่ง: เพิ่ม payment channels
ครบ — bank accounts (optgroup, value `bank:<id>` → LinkedAccountId) + GL เงินสด/
ช่องจ่ายอื่น (getPaymentChannels, value `account:<id>` → ใช้เป็นผัง Cr ตรง ๆ).
RemitRequest เพิ่ม `BankGlAccountId`; ResolveBankGlAsync validate GL เป็นผังบริษัทนี้
+ active + level≥4 ก่อนใช้. (c) แนบเอกสารที่จ่าย/ใบเสร็จได้ในโมดัลนำส่งเลย (input
`rmDoc`) → หลัง RemitAsync สำเร็จ auto-upload เข้า FileAttachment "StatutoryRemittance"
ใน flow เดียว (ไม่เลือกไฟล์ → แสดง step แนบภายหลังเหมือนเดิม)._

_รอบ 25 (payroll import loop — เห็นรายคน): POST /payroll/runs/import (ระบบนอก เช่น
TakeTime ส่งยอดสำเร็จรูป recalculate=false → run สถานะ Calculated) เดิมหน้า "ดู" รอบ
เงินเดือนตารางรายคนว่าง เพราะ GetPayrollRunAsync (PayrollRunResponse) ไม่คืน detail
lines. แก้: PayrollRunResponse เพิ่ม `Details` (List<PayrollRunLineDto>) — เติมเฉพาะตอน
ดึง run เดี่ยว (list ปล่อย null) + `ExternalSystem`/`ExternalRunRef`. payroll.html
viewRun แสดงรายคน + ปุ่มดูสลิป/50ทวิ ได้ครบทั้ง run ที่สร้างในระบบและ import; runs
list ติด badge "↧ <ระบบนอก>". ลูปต่อ (approve → pay → settle-sso → payslip → ภงด.1 →
50ทวิรายปี) ครบเหมือน run ปกติ — import ไม่ auto-post ต้องกด approve/pay ในระบบเอง._

_รอบ 26 (แหล่งจ่ายเงินรายคน): เดิม ProcessPaymentAsync ลง Cr เงินสด/ธนาคารบรรทัด
เดียวรวมทั้ง run (run.NetPaymentAccountCode → default 11122/111x) — จ่ายทุกคนจากบัญชี
เดียว. แก้ให้แยกรายคน: PayrollDetail เพิ่ม `NetPaymentAccountCode` (migration). import
เก็บ PaymentAccountCode รายคนลง detail (เลิกยุบเป็นค่าเดียว). Pay → group ยอดสุทธิ
(NetPay − AdvanceRecovered) ตามบัญชีจ่ายของแต่ละคน (fallback detail → run → default)
→ ลง Cr หลายบรรทัดตามบัญชี. ผู้ใช้แก้แหล่งจ่ายรายคนได้ก่อนจ่าย (Calculated/Approved)
ผ่าน PUT /payroll/runs/{id}/employees/{empId}/payment-account (validate 111x/1133/2123)
→ หน้า run detail dropdown ราย row. PayrollRunLineDto เพิ่ม NetPaymentAccountCode._

_รอบ 27 (แก้ 2 จุดหน้า run detail): (a) dropdown แหล่งจ่ายรายคน "ไม่มีบัญชีธนาคาร" —
GetPaymentChannelAccountsAsync ตัด 1112x (ธนาคาร) ออกโดยตั้งใจ (ธนาคารมาจาก
getBankAccounts แยก). payroll.html viewRun โหลด getBankAccounts ด้วย → optgroup
"บัญชีธนาคาร" (value = LinkedAccountCode) + "เงินสด/ช่องทางอื่น" (payment channels).
(b) ดูสลิปไม่ขึ้น — iframe payslip ส่ง ?token= แต่ JWT รับ query-token เฉพาะ /hubs +
OCR image และอ่านคีย์ access_token เท่านั้น. Program.cs OnMessageReceived: รับทั้ง
access_token+token และ allow path ที่ลงท้าย /payslip._

_รอบ 28 (สลิปเงินเดือน — แสดง inline + ดีไซน์ใหม่): (a) เดิม GetPayslip ส่ง
File(bytes,ct,fileName) → Content-Disposition: attachment → เบราว์เซอร์ดาวน์โหลด
แทนที่จะ render. แก้: download=false (default) → set inline + File ไม่มีชื่อไฟล์ →
iframe โชว์; download=true → attachment ชื่อไฟล์มีชื่อพนักงาน (สลิปเงินเดือน_<ชื่อ>_
MM-YYYY.pdf). หน้า payslip modal เพิ่มปุ่ม "⬇️ ดาวน์โหลด". (b) GeneratePayslipAsync
สร้าง HTML ดีไซน์ใหม่ (หัวแถบสีธีม PrimaryColor + โลโก้ data-URI, การ์ดข้อมูล,
ตารางรายได้/หัก, กล่อง Net Pay เด่น). **render ด้วย QuestPDF โดยตรง**
(PdfGenerationService.Payslip.cs → GeneratePayslipPdfAsync) ไม่ผ่าน HTML→Chromium
จึงสวยคงที่ทุก server แม้ไม่เปิด Puppeteer; สีธีมดึงจากเทมเพลตใบกำกับ
(AccentColor/TableHeaderColor) + โลโก้จาก CompanySettings.LogoPath._

_รอบ 29 (ดู JE ของรอบเงินเดือน): การจ่ายเงินเดือนลงเป็น JournalEntry 1 ใบ/รอบ
(ProcessPaymentAsync, ref "HR-PR-{year}-{month}", sensitivity=Payroll) ไม่ออกเอกสาร
ใบสำคัญจ่ายแยก. เพิ่ม JournalEntryId ใน PayrollRunResponse + MapToPayrollRunResponse
→ payroll.html run detail ปุ่ม "🧾 ดูรายการบัญชี (JE)" deep-link
journals.html?entryId={id} (เปิด JE detail ตรง). สลิป = หลักฐานพนักงาน (HR), JE =
บันทึกบัญชีการจ่าย — แยกหน้าที่กัน._

_รอบ 30 (แก้ยอดรายคนก่อนจ่าย): เดิมไม่มีทางแก้ยอดรายคน (Calculate ทำเฉพาะ Draft +
ลบ detail คำนวณใหม่; import เป็น Calculated). เพิ่ม UpdatePayrollDetailAsync +
PUT /payroll/runs/{id}/employees/{empId}/detail (UpdatePayrollDetailRequest, field
nullable แก้เฉพาะที่ส่ง) — อนุญาตเฉพาะ Calculated/Approved, รวม Gross/หัก/สุทธิ +
run totals ใหม่, กันสุทธิติดลบ, ปัดค่าติดลบเป็น 0. PayrollRunLineDto ขยายเป็น raw
fields ครบ (commission/otherIncome/PVD/loan/SSO นายจ้าง) เพื่อ pre-fill ตัวแก้.
payroll.html run detail: ปุ่ม "✏️ แก้ยอด" ราย row → โมดัลแก้ทีละช่อง + รวมสุทธิ live._

_รอบ 31 (กดจ่ายแล้ว "เชื่อมต่อเซิร์ฟเวอร์ไม่ได้"): ProcessPaymentAsync commit JE แล้ว
แต่ยัง await งานหนักใน request — IssueMonthlyPnd1Certs + AutoGenerateFilings (สร้าง
PDF ภงด.1/สปส. + สลิป QuestPDF ทุกคน + upload) + email enqueue → ใช้เวลานาน proxy
reset connection (client เห็น "Failed to fetch" ทั้งที่จ่ายสำเร็จแล้ว). แก้: ย้ายงาน
สร้างเอกสารไป background DI scope ใหม่ (IServiceScopeFactory) ผ่าน
DispatchPostPaymentArtifactsAsync → GeneratePostPaymentArtifactsAsync (IPayrollService);
response กลับทันทีหลัง commit + notifications. ไม่มี scopeFactory (test) → inline เดิม.
เอกสาร best-effort + สร้าง on-demand ได้._

_รอบ 32 (กดจ่ายแล้ว error จริง — nested transaction): log ชี้ "The connection is
already in a transaction and cannot participate in another transaction". ต้นเหตุ:
ProcessPaymentAsync/SettleSocialSecurityAsync เปิด tx เอง แล้วเรียก
AccountingService.CreateJournalEntryAsync ที่ก็เปิด tx ใหม่แบบ unconditional →
Npgsql ห้าม nested tx. แก้: CreateJournalEntryAsync ใช้ ambient-tx pattern เดียวกับ
ReverseJournalEntryAsync (เช็ค _db.Database.CurrentTransaction — เปิด/commit เฉพาะ
ตอนไม่มี ambient tx) → JE creation เข้าร่วม tx ของ caller. PayrollController.Pay
ครอบ try/catch คืน 400 + ข้อความจริง (เดิม propagate ดิบ). แก้ทั้ง payroll pay +
settle SSO + ทุก caller ที่ครอบ JE ด้วย tx._

_รอบ 33 (post JE ซ้ำ — "post ได้เฉพาะ Draft"): หลังแก้ nested-tx (รอบ 32) โผล่บั๊ก
ถัดมา — CreateJournalEntryAsync สร้าง JE เป็น Posted ตั้งแต่แรก (ไม่มีขั้น Draft) แต่
ผู้เรียก 4 ที่ (payroll pay / settle SSO / severance / RemitAsync นำส่งภาษี) เรียก
PostJournalEntryAsync ตามหลัง Create → post ใบที่ Posted แล้ว → throw. แก้:
PostJournalEntryAsync เป็น idempotent — entry Posted อยู่แล้ว → no-op สำเร็จ (คืน
response เดิม); Draft → post ปกติ; สถานะอื่น (Voided/Reversed) → ยัง throw._

_รอบ 34 (แหล่งจ่ายโมดัลนำส่ง สปส. ไม่ครบ): settleSsoBank โหลดแค่ getBankAccounts —
เพิ่ม getPaymentChannels (เงินสด/เงินทดรองกรรมการ 1133/ช่องจ่าย 2123) แบบ optgroup
(value bank:<id> / account:<id>). SettleSsoRequest + SettleSocialSecurityAsync เพิ่ม
BankGlAccountId (validate ผังบริษัท+active+level≥4 ใช้เป็น Cr ตรง ๆ). pattern เดียวกับ
รอบ 24 (หน้านำส่งภาษี) + payment-source รายคน._

_รอบ 35 (reclassify ผังบัญชี "กดแล้วไม่เปลี่ยน"): ReclassifyLineAccountAsync ทำงาน
ถูกต้อง (update line.AccountId + post JE คู่ Dr ใหม่/Cr เก่า ผ่าน JournalEntryBuilder
status=Posted, ไม่มี nested-tx). บั๊กอยู่ที่ frontend: submitReclassifyLine สำเร็จแล้ว
เรียก this.openDetail?.() ที่ "ไม่มี method นี้จริง" (ชื่อจริง detail()) → optional-chaining
no-op เงียบ → detail ไม่ refresh → ดูเหมือนข้อมูลไม่เปลี่ยน. แก้: เรียก
await this.detail(ctx.docId). (retry ไม่สร้าง JE ซ้ำ — backend guard line.AccountId==new → no-op)._

_รอบ 36 (PDF footer "การบันทึกบัญชี" สะท้อน reclassify): เดิม LoadGlPostingAsync หยิบ
JE ต้นทางใบเดียว (FirstOrDefault) → footer ยังโชว์ผังเดิม (516) แม้ reclassify แล้ว.
แก้: รวม JE forward ทั้งหมดของเอกสาร (SourceDocumentId เดียวกัน + OriginalEntryId==null
+ Posted + ReversedByEntryId==null = JE ต้นทาง + คู่แก้ไข reclassify) → NetGlLinesByAccount
net Dr−Cr ต่อผัง (ผังที่ reclassify หักล้างเป็น 0 หายไป เหลือผังใหม่) → footer แสดงยอด
สุทธิ Dr ผังใหม่ / Cr เงินสด ตรงกับที่แก้. label เพิ่ม "(สุทธิรวมแก้ไข N)" เมื่อมี >1 JE.
footer นี้ opt-in ผ่าน CompanySettings.ShowGlEntryOnDocument (default ปิด); เอกสารปกติ
ไม่แสดงผังบัญชีบนหน้า (ไม่ใช่ field §86/4)._

_รอบ 37 (แก้ "แหล่งเงิน" ไม่ได้ — แต่แก้ผังบัญชีได้): backend ReclassifyPaymentSourceAsync
ทำงานถูก (JournalEntryBuilder Dr เก่า/Cr ใหม่, guards ผ่าน). บั๊กที่ frontend:
openReclassifyPaymentSource หา doc จาก this.docs (list projection ที่อาจไม่มี doc
นี้/ไม่มี bankAccountId·paidAmount) แล้ว hard-return "ไม่พบเอกสาร" — ต่างจาก
openReclassifyLine ที่รับ args inline จึงไม่กระทบ. แก้: ใช้ this._currentDoc (เอกสารที่
detail() เพิ่ง fetch มี field ครบ) ก่อน fallback this.docs._

_รอบ 38 (ตั้งค่าต่อลูกค้า "ออกใบกำกับภาษีเสมอ"): Contact เพิ่ม
DefaultIssueTaxInvoice (bool, migration) + Create/Update/ContactResponse DTO +
MapContactToResponse. contacts.html เพิ่ม checkbox ในส่วนตั้งค่าบันทึกบัญชี (save/load/
reset). documents.html onContactChange → maybePreselectTaxInvoice: ลูกค้าที่ flag=true
+ ชนิดปัจจุบัน Invoice → เปลี่ยนเป็น TaxInvoice อัตโนมัติ + เตือน §86/4 (TaxId/สาขา/
ที่อยู่) ไม่ครบ. ไม่บังคับ — ยังเลือกชนิดเองได้/convert ได้เหมือนเดิม._

_รอบ 39 (หมายเหตุขึ้น PDF + รายละเอียดหลายบรรทัด): (a) doc.Notes (หมายเหตุที่กรอกตอน
สร้าง) เดิมไม่ถูก render บน PDF (โชว์แต่ CustomFooterNotes) — เพิ่ม render ทั้ง 2 path:
RenderDocumentPdfNative (QuestPDF Text รองรับ \n) + BuildDocumentHtml (white-space:
pre-line). (b) รายละเอียดรายการรองรับหลายบรรทัด: line desc input เปลี่ยนจาก <input>
เป็น <textarea rows=1 auto-grow> (Enter=เว้นบรรทัด; ProductLookup ยัง select ด้วย Enter
เมื่อ arrow-highlight เท่านั้น idx≥0 จึงไม่ชน); PDF cell + on-screen td ใช้ pre-line/Td
.Text() render \n ครบ._

_รอบ 40 (ชุด invoice/tax-invoice ครบวงจร): (a) เครดิตเทอมต่อลูกค้า —
Contact.PaymentDueDays/PaymentTerms → เติมวันครบกำหนดอัตโนมัติตอนสร้างเอกสารขาย.
(b) §86/4 บังคับตอนอนุมัติ — enforce864 default true; TaxInvoice บังคับ field ผู้ซื้อ
(เลขภาษี13/ที่อยู่) → ใบไม่ครบ block. **ยกเว้น**: (i) `BuyerDeclinedTaxInvoice`/walk-in
→ ข้าม gate ทั้งชุด; (ii) branch code (สาขา5) บังคับเฉพาะผู้ซื้อนิติบุคคล
(ประกาศ 199) บุคคลธรรมดาไม่บังคับ (ดู §2.3 ผู้ซื้อไม่ประสงค์รับใบกำกับ). (c) ป้าย "ต้นฉบับ" บน PDF
— ใบกำกับ/ใบเสร็จภาษี/CN/DN เติม "(ต้นฉบับ)" (สำเนา=WatermarkOverride) ทั้ง
QuestPDF+HTML. (d) หัว PDF ต่อชนิด GetDocumentTitle ถูกต้องอยู่แล้ว. (e) auto-receipt:
ชำระครบบน Invoice/TaxInvoice → prompt "ออกใบเสร็จรับเงิน" → convertDocument→Receipt
(VAT รับรู้ที่ใบเดิม ไม่คิดซ้ำ). (f) e-Tax email 1-คลิก: ปุ่ม sendEtaxEmailOneClick =
/etax/generate → sendEtaxByEmail (PDF/A-3+XML+CC สรรพากร)._

_รอบ 41 (ดาวน์โหลดสำเนา): pdfModal เพิ่ม dropdown "ต้นฉบับ/สำเนา" (pdfCopyMode) →
_refreshPdfPreview re-render + printPdf/downloadServerPdf/generate-html ส่ง
watermarkOverride="สำเนา (COPY)". server พิมพ์ลายน้ำ "สำเนา" (HTML div.watermark +
QuestPDF background) + isCopyPrint ตัดป้าย "(ต้นฉบับ)" ออก. ต้นฉบับ=ให้ลูกค้า,
สำเนา=ผู้ขายเก็บ (retention 5 ปี §87/3). จำเป็นเฉพาะเอกสารภาษี (ใบกำกับ/ใบเสร็จ
VAT/CN/DN); เอกสารทั่วไป (ใบแจ้งหนี้/เสนอราคา/ส่งของ) ไม่บังคับ._

_รอบ 42 (audit เชิงลึก convert/void/CN — verify แล้วแก้ 5 จุด): (a) ConvertCoreAsync
คงสกุลเงิน+เรตต้นทาง (เดิม default THB). (b) ValidateConversionAsync กันแปลงซ้ำเป็น
Invoice/TaxInvoice (1 ต้นทาง=1 ใบรับรู้รายได้ กัน double VAT/ภพ.30). (c) VoidDocumentAsync
block เมื่อมีเอกสารลูก active อ้างอยู่ (กัน orphan + ครอบเคสลูกมี e-Tax ยื่น RD).
(d) Void เพิ่ม FOR UPDATE lock + re-read สถานะ (กัน double-void race → reverse JE ซ้ำ).
(e) §86/10 CN cumulative cap: SUM(CN)≤source.TotalAmount (โหมดคืนเงินสดเดิมไม่ cap).
หมายเหตุ: ภพ.30 สร้างแบบ on-demand จาก documents (อ่านสด ตาม TaxPointDate) —
สะท้อน CN/DN/void ถูกต้องอยู่แล้ว ไม่ต้องมี TaxReportLine incremental._

_รอบ 43 (supersede + หัวเอกสารรวม): (a) แปลง Invoice→TaxInvoice: เมื่ออนุมัติ
TaxInvoice ที่แปลงจากใบแจ้งหนี้ (approved/ยังไม่ชำระ/ไม่มีลูกอื่น) →
SupersedeSourceInvoiceAsync ล้างใบแจ้งหนี้เดิม (reverse JE + stock -1 + project -1
+ Voided) กัน GL/รายได้/สต๊อกซ้ำ (ภพ.30 นับ TaxInvoice ใบเดียวอยู่แล้ว). (b) หัว
PDF ต่อชนิด GetDocumentTitle ถูกต้อง (Invoice→ใบแจ้งหนี้, TaxInvoice→ใบกำกับภาษี+
ต้นฉบับ, Receipt+VAT→ใบกำกับภาษี/ใบเสร็จรับเงิน) — แต่ไม่มี "ใบแจ้งหนี้/ใบกำกับภาษี"
รวม. เปิดช่อง CustomTitle/CustomTitleEn ในหน้า document-templates (เดิมมี field
แต่ UI ไม่โชว์) → ตั้งหัวเอกสารเองต่อเทมเพลตได้ (เช่น "ใบแจ้งหนี้/ใบกำกับภาษี").
เมื่อตั้ง CustomTitle → "(ต้นฉบับ)" auto ไม่ต่อท้าย (ใส่เองในหัวได้)._

_รอบ 44 (ใบแจ้งหนี้/ใบกำกับภาษี ใบเดียว — checkbox บนฟอร์ม): เพิ่ม flag ระดับ
เอกสาร `Document.CombinedInvoiceTaxInvoice` (bool, migration ALTER ADD COLUMN
IF NOT EXISTS). หน้า create-doc (documents.html) มี checkbox `fCombinedTaxInvoice`
โผล่เฉพาะฝั่งขาย Invoice/TaxInvoice (คุมโดย onDocTypeChange). ติ๊กแล้ว save →
frontend บังคับ `documentType='TaxInvoice'` + `combinedInvoiceTaxInvoice=true`
(ผ่าน `_effectiveDocType`). เอกสารทำงานเป็นใบกำกับภาษีเต็มรูป (post VAT 21911→
ภพ.30, บังคับ §86/4 ตอน approve, ออก e-Tax T03/T01 ได้ตามปกติ) แต่หัวกระดาษ PDF
พิมพ์ "ใบแจ้งหนี้/ใบกำกับภาษี" (Invoice / Tax Invoice) แทน "ใบกำกับภาษี" — override
ทั้ง QuestPDF (PdfGenerationService.DocumentRenderer.cs) + HTML path
(PdfGenerationService.cs) เมื่อ `type==TaxInvoice && CombinedInvoiceTaxInvoice
&& CustomTitle==null`. ยังคงต่อท้าย "(ต้นฉบับ)"/สำเนา ตามเดิม. เครดิตเทอมดึงจาก
contact.paymentDueDays → fDueDate + fPaymentTerms อัตโนมัติ (maybePreselectTaxInvoice
เดิม). Service กันเฉพาะ type=TaxInvoice จริงเท่านั้นถึงรับ flag (กันหัวเพี้ยน).
DTO: CreateDocumentRequest + DocumentResponse echo flag; hydrate checkbox ตอน edit._

_รอบ 45 (ส่งสลิปเงินเดือนทาง LINE): พนักงานผูก LINE เองผ่าน LINE OA บริษัท —
HR สร้างรหัส 6 หลัก (`EmployeeLineBindCode`, หมดอายุ 24 ชม.), พนักงานเพิ่มเพื่อน
OA แล้วส่ง "สลิป {รหัส}" → LineBotService.TryBindFromLineAsync เขียน Employee.LineId
(= push userId เดียวกับ NotificationEngine). ส่งสลิป: PayslipLineDeliveryService
สร้าง `PayslipShareToken` (สุ่ม 32 bytes base64url, หมดอายุ 7 วัน, เพิกถอน token
เก่าของงวด+คนเดียวกัน) แล้ว push flex card **ซ่อนยอดเงิน** (โชว์แค่ชื่อ/งวด + ปุ่ม)
ผ่าน ILineNotifyService.PushFlexToUserAsync (channel ต่อบริษัท). ปุ่มลิงก์ไป
`GET /api/public/payslip/{token}` ([AllowAnonymous]) → validate token → reuse
GeneratePayslipAsync → stream PDF inline + log **PdpaPiiAccessLog** (ม.37(4),
Operation=Read, SubjectType=Employee) + increment AccessCount. UI payroll.html:
ปุ่ม "📤 LINE" รายคน + "ส่งสลิปทั้งงวดทาง LINE" + modal รหัสผูก (NotBound →
เสนอสร้างรหัส). Settings: LINE OA Basic ID (`CompanySettings.LineOaBasicId`)
ทำลิงก์เพิ่มเพื่อน. Endpoints (HR-authed): POST runs/{r}/employees/{e}/payslip/
send-line · POST runs/{r}/payslip/send-line-all · POST employees/{e}/line-bind-code
· GET employees/{e}/line-status._

_รอบ 46 (กันส่งอีเมลเอกสาร Draft): เดิมกด "ส่งอีเมลหลังบันทึก" ตอนสร้าง →
ส่ง PDF เลข DRAFT-xxx ให้ลูกค้าทันทีโดยไม่อนุมัติ (ผิด §86/4 — เลขจริงออกตอน
Approve). แก้ 2 ชั้น: (a) backend DocumentEmailService.SendDocumentEmailAsync
บล็อกเอกสาร Draft/WaitingApproval/Rejected/Voided (throw) — กันทุกทาง
(create-flow, ปุ่มส่งซ้ำ, integration). e-Tax path บล็อก Draft อยู่แล้ว
(EtaxInvoiceService). (b) frontend create-flow: ติ๊กส่งอีเมล → อนุมัติให้ก่อน
(ออกเลขจริง) แล้วค่อยส่ง (1-click); อนุมัติไม่ผ่าน (§86/4 ไม่ครบ) → ไม่ส่ง +
แจ้งเหตุ. แชร์ savedStatus กับ paid-on-issue chain กัน approve ซ้ำ (ApproveDocument
throw ถ้าไม่ใช่ Draft/WaitingApproval). เพิ่มปุ่ม "📧 ส่งอีเมล" (PDF ปกติ) บน
เอกสารฝั่งขายที่อนุมัติแล้ว นอกเหนือจาก "ส่ง e-Tax อีเมล" (CC สรรพากร+XML) เดิม.
e-Tax by Email checkbox แสดงกับ TaxInvoice (รวม combined) อยู่แล้ว.

_รอบ 47 (รายงานภาษี — สะท้อน GL + PDF + ภ.พ.30): (a) รายงานภาษีซื้อไม่ดึง
เอกสารที่ไม่ได้เคลม VAT — เดิม §82/5/ไม่เคลม (IsVatClaimable=false, VAT กลบ
ค่าใช้จ่ายไม่ลง 11610) ถูกใส่เป็น audit line IsExcluded → เลิก emit; รายงานมี
เฉพาะภาษีซื้อที่เคลมจริง (claimableVat>0) สะท้อน GL. ต้นเหตุ: OCR ตั้ง
HasTaxInvoiceReference=true อัตโนมัติเมื่อมี VAT+เลขใบ แต่ผังบัญชีบังคับ
ไม่เคลมตอน approve. (b) วันที่ export วว/ดด/ปปปป (พ.ศ.) ตรงหัวคอลัมน์ (เดิม
สลับ ปปปป-ดด-วว). (c) เพิ่ม PDF: GET tax/{id}/export-pdf?kind=purchase|sales|
pp30 → PdfGenerationService.GenerateVatReportPdfAsync (QuestPDF) — รายงาน
ภาษีซื้อ/ขาย ตาราง §87 ประกาศ 104 + แบบสรุป ภ.พ.30 (ช่อง 1-9); ปุ่มในหน้า tax._

(c) UX: maker ที่ไม่มีสิทธิ์อนุมัติ (เช็คจาก my-permissions allowedMenuIds:
perm:Document.Approve / .Revenue.Approve / .Purchase.Approve) → กล่องส่งอีเมล
ขึ้นหมายเหตุล่วงหน้าว่าเอกสารจะเป็นร่างรออนุมัติ + ตอนบันทึกไม่ยิง approve
(กัน 403) แจ้งแบบเป็นมิตร. Owner/Admin หรือ role ที่มี perm → ส่งได้ปกติ._

_รอบ 97: แก้ภาษีซื้อที่ "ถึงกำหนดทีหลัง" (§83/6 ภ.พ.36 รับรู้ / §86/4 เติมใบกำกับ)
_หายจากรายงาน — GenerateVatReport เดิมโหลดเอกสารด้วย (TaxPointDate ?? DocumentDate)
_ในงวดเท่านั้น → ใบเดือน พ.ค. ที่ BecameClaimableAt=ก.ค. ไม่ถูกโหลดในรายงาน ก.ค.
_= "กดเคลมแล้วหาไม่เจอ". แก้ docs query: OR (InputVatBecameClaimableAt ในงวด) +
_ในลูป input เพิ่ม guard "เคลมเฉพาะเดือนที่ถึงกำหนด" (skip ถ้า BecameClaimableAt
_นอกงวด) → นับเฉพาะงวดรับรู้ กันเคลมผิดเดือน/เบิ้ล 2 งวด. + recognize ภ.พ.36 ให้
_ผู้ใช้กรอก "วันที่ใบเสร็จ RD" (เดิม hardcode วันนี้) → JE + BecameClaimableAt +
_เดือนที่เข้า ภ.พ.30 = วันที่นั้น (§77/2)._
_รอบ 96: แก้ §83/6 โชว์ผิด flow "รอใบกำกับ §86/4" — เอกสารบริการต่างประเทศ post
_11640 (InputVatPostedAsUndue) เหมือนกัน แต่เคลมผ่าน ภ.พ.36 (นำส่ง+รับรู้) ไม่ใช่
_§86/4 completeness. ผู้ขาย ตปท. ไม่มีเลขภาษีไทย → CompleteSupplierTaxInvoice
_(completeness) fail เงียบ ๆ แต่ toast บอก "สำเร็จ ย้ายเข้า ภ.พ.30" ทั้งที่ไม่ย้าย.
_แก้: (1) ReclassifyUndueInputVatAsync guard IsForeignService → return false (ชัด).
_(2) banner detail + badge list แยก §83/6 → โชว์ "🌐 ภ.พ.36 รอรับรู้" + ลิงก์หน้า
_นำส่งภาษี (ไม่โชว์ฟอร์ม §86/4). (3) toast completeTaxInvoice ซื่อสัตย์ — เช็ค
_inputVatBecameClaimableAt จริง: ย้ายแล้ว/ยังเคลมไม่ได้ (ชี้ ภ.พ.36 ถ้า ตปท.)._
_รอบ 95: หน้ารายงานภาษี (tax.html) — (1) ติ๊ก "ใช้" ไม่มี onchange → ยอดไม่ recalc
_+ ต้องกดปุ่มบันทึกเอง. เพิ่ม onVatLineToggle: recalc footer ตารางนั้นทันที
_(client) + auto-save debounce 700ms (indicator ● กำลังบันทึก → ✓ บันทึกแล้ว) +
_อัปเดต KPI จาก server response — ไม่ต้องกดปุ่มบันทึกอีก. (2) §82/3 carry-forward:
_GenerateVatReport ดึงบรรทัด INPUT ที่ IsExcluded ในรายงานเดือนก่อน (ภายใน 6 เดือน,
_ยังไม่ถูกใช้/ยังไม่ undue/ไม่ voided) มาเป็นบรรทัด "ยกมา §82/3" (default ติ๊กออก) →
_ผู้ใช้ติ๊กใช้เดือนไหนก็เคลมเดือนนั้น (RecalcVatTotals นับตอนบันทึก). กันเครดิต
_ภาษีซื้อที่เลื่อนไว้หายถาวร._
_รอบ 96 (เอกสารมาช้าหลังปิดงวด): carry-forward รอบ 95 ครอบเฉพาะใบที่ "เคยมี
_บรรทัดแล้วถูกติ๊กออก" — ใบกำกับ มิ.ย. ที่เพิ่งบันทึกตอน ก.ค. (งวด มิ.ย. Filed
_แล้ว regenerate ไม่ได้) ไม่เคยอยู่ในรายงานไหนเลย → tax point = มิ.ย. หลุดทั้ง
_query งวด ก.ค. และ carry-forward = ภาษีซื้อหายเงียบ. GenerateVatReport เพิ่ม
_late-arrival sweep: ใบ tax point ในงวดก่อน (≤6 เดือน) ที่ไม่มีบรรทัดในรายงาน
_VAT ใดเลย + (งวดนั้น Filed **หรือ** CreatedAt หลังเดือน tax point จบ) →
_ฝั่งซื้อใส่บรรทัด opt-in "[ใบกำกับซื้อมาช้า]" (IsExcluded, ยอด = เฉพาะส่วน
_เคลมได้หลังหัก §82/5) / ฝั่งขายใส่บรรทัดเตือน "ต้องยื่น ภ.พ.30 เพิ่มเติมงวดนั้น"
_(ภาษีขายเลื่อนงวดไม่ได้). + PullableVatTypes เดิมขาด **PaymentVoucher** (loop
_หลักนับเป็นภาษีซื้อเมื่อ HasTaxInvoiceReference) + ใบขายที่ไม่ใช่ TaxInvoice →
_ปุ่ม "ดึงเอกสาร" ดึง PV ไม่ได้เลย; เพิ่มแล้ว + guard PV ที่ไม่ติ๊กใช้ใบกำกับ +
_hard block §82/3 เกิน 6 เดือนตามงวดปลายทาง (เดิมเช็คจากวันนี้ = เตือนอย่างเดียว)._
_รอบ 94 (ภ.พ.36 ครบวงจร §83/6): (A) AutoPost แก้ JE บริการต่างประเทศ — เดิม Cr
_เจ้าหนี้/เงินสด "รวม VAT" (จ่ายผู้ขาย ตปท. เกิน 7% + งบไม่มีหนี้ ภ.พ.36). ใหม่:
_Cr ผู้ขาย/เงินสด = ฐาน + Cr 21912 เจ้าหนี้ ภ.พ.36 = VAT ประเมินเอง + Dr 11640
_บังคับเสมอ (§77/2 เคลมได้หลังนำส่ง) — ทั้ง branch Expense/PI accrual และ PV
_standalone (cash + legacy credit). (B) StatutoryRemittance เพิ่ม VatPp36:
_dashboard pending จากเอกสาร IsForeignService, RemitAsync generic → Dr 21912/
_Cr ธนาคาร, due 7/15. (C) RecognizePp36InputVatAsync — หลังได้ใบเสร็จ RD:
_Dr 11610/Cr 11640 + stamp InputVatBecameClaimableAt → เข้า ภ.พ.30 เดือนรับรู้;
_idempotent (JE Ref ภ.พ.36R-YYYYMM) + ต้องนำส่งก่อน. UI: หน้า tax-remittance
_แถว ภ.พ.36 ขึ้น pending อัตโนมัติ + ปุ่ม "รับรู้ภาษีซื้อ" บนประวัติ._
_รอบ 93: เปิดฟอร์มออก "ใบกำกับภาษี/ใบเสร็จรับเงิน ใบเดียว" (ขายเงินสด) — เดิม
_IssuedAsCashReceipt set ได้เฉพาะ integration (TakeTime IsCashSale); ฟอร์มสร้างเอง
_มีแค่ "จ่ายแล้ว (2 ใบ + REC แยก)". เพิ่ม CreateDocumentRequest.IssuedAsCashReceipt
_(guard TaxInvoice), approve ปิด Paid, checkbox ในฟอร์ม (mutually exclusive กับ
_paidOnIssue). มัดจำที่เลือก → ส่ง drives (DepositAppliedDrivesJournal) reuse เส้น
_AutoPost cash-sale ที่ verified (JE เดียว Dr เงินสดสุทธิ+กลับมัดจำ/Cr รายได้+VAT,
_ไม่ตั้งลูกหนี้, ไม่ออก REC แยก, e-Tax T03).
_รอบ 92: หักมัดจำหลายใบโชว์ครบบน PDF — apply สะสมทุกเลขใน DepositAppliedRef
_(MergeDepositRef comma-sep+dedup, เดิมเก็บใบแรก → label โชว์เลขเดียว) + PDF
_แตกบรรทัดต่อใบ (LoadDepositApplyBreakdownAsync อ่าน gross ต่อใบจาก apply JE:
_Cr 113; เลข = Document มัดจำ/JV จาก description) ผ่าน param depositApplies._
_รอบ 91: void/purge คืน "JV มัดจำ raw (non-drives)" — เดิม void 2b/purge 0c วน_
_เฉพาะ Document deposits (d.IsDeposit) → JV apply (raw JE, SourceDocumentId=null,_
_Reference=docNo) ไม่ถูก reverse/delete + JV mark ไม่ถูกล้าง → ลบใบแล้ว 3 JV apply_
_(Dr 21510/Cr 113) ค้าง orphan + JV นำไป apply ใบใหม่ไม่ได้. เพิ่ม helper_
_ReverseOrphanJvDepositAppliesAsync (void→reverse / purge→delete ทุก apply JE +_
_un-mark JV ต้นทางจาก description "JV XXX"). ปรับ PaidAmount ตาม gross ที่คืน._
_รอบ 90: JV apply strictly one-shot — เดิม guard บล็อกเฉพาะ apply ไปใบอื่น_
_(DepositAppliedToDocumentId != invoiceId) แต่ "ยอมหักซ้ำใบเดิม" (== invoiceId)._
_JV เป็น one-shot เต็มจำนวน ไม่มี remaining tracking แบบมัดจำเอกสาร → หักซ้ำ =_
_โพสต์ Dr 21510/Cr ลูกหนี้ ซ้ำเต็มจำนวน. เคสจริง JV 3,000 ถูกหัก 3 ครั้ง →_
_Dr 21510 9,467.29 (467.29+3×3,000) + Cr ลูกหนี้ค้าง 6,000. แก้: == invoiceId_
_→ throw idempotent (แก้ยอด = void/ลบใบแล้วสร้างใหม่). มัดจำเอกสารปลอดภัยอยู่_
_แล้ว (availableBase guard). ใบที่พังไปแล้วต้อง void+recreate บน build ใหม่._
_รอบ 89: ปิด field-driven 21712 ที่เหลือ — Realize + Refund มัดจำ. เพิ่ม helper_
_ResolveDepositBaseAccountAsync (หาผัง 215/217 ยอด Cr สูงสุดจาก JE จริงของใบมัดจำ)_
_→ Dr ผังจริง (เช่น 21510) แทนเดา 21712 (ไม่งั้น 21510 ค้าง Cr + 21712 ติดลบ)._
_ครบทุกเส้นแล้ว: apply(doc/JV), realize, refund, drives(cash-sale), void/purge_
_= GL-driven อ่านขาจริงหมด. 21510 = ผังมัดจำจริงของ tenant (ที่ผู้ใช้ส่งมา)._
_รอบ 88: ApplyDepositToInvoiceAsync เปลี่ยนเป็น GL-driven (แบบเดียวกับ JV apply)_
_— เดิม field-driven (DepositDeferredAccountCode ?? 21712 + flag เดา VAT) → ใบ_
_มัดจำที่ JE จริงลง Cr ผังอื่น (integration ลง 21510/21610) ถูก Dr 21712 ผิดผัง:_
_ผังเดิมค้าง Cr ถาวร + 21712 ติดลบ. ใหม่: family-net (Cr−Dr ต่อผัง) จากทุก JE_
_forward ของใบมัดจำ (ต้นทาง+apply ก่อนหน้า, ตัดผัง 1xxxx) → Dr ตามขาจริงตาม_
_สัดส่วน, ฐาน/VAT แยกตามผังจริง (21913/21911), guard เกิน grossRemaining._
_field-driven เหลือเป็น fallback เมื่อไม่มี JE forward เท่านั้น._
_รอบ 87: footer "การบันทึกบัญชี" รวม JE ตัดมัดจำ — เดิมดึงเฉพาะ SourceDocumentId_
_== ใบนี้ แต่ JE ตัดมัดจำผูกกับ "ใบมัดจำ" (doc apply) / null (JV apply) → net view_
_โชว์ Dr ลูกหนี้ "ค้าง" เท่ายอดมัดจำทั้งที่ GL จริงล้างครบ (ผู้ใช้เข้าใจผิดว่าลงผิด)._
_เพิ่มเงื่อนไข OR (IsAutoGenerated && Reference == เลขใบ) — apply ทั้ง 2 path_
_stamp Reference = เลขใบปลายทาง; JE ของใบอื่น Reference = เลขตัวเอง ไม่ปน._
_รอบ 86 (audit ซ้ำ): (1) chain "จ่ายแล้ว" รันเฉพาะปุ่ม "บันทึกและอนุมัติ" — เดิม_
_กด "บันทึกร่าง" ก็ approve+บันทึกชำระเลย (ร่างขยับเงินจริง) (2) วันที่ JE ตัด_
_ชำระมัดจำ = วันที่เอกสาร (เดิม UtcNow → backdate ข้ามเดือน VAT recognition_
_หลุดเดือน ภ.พ.30): ApplyJournalDepositRequest + ApplyJournalDepositToInvoiceAsync_
_รับ ApplyDate, frontend ส่ง fDate ทุกจุด (pending applies + pickJvDeposit)._
_ตรวจแล้วถูกอยู่: PDF โชว์ "หักเงินมัดจำ→ยอดชำระสุทธิ" (ทั้ง 2 apply path stamp_
_DepositAppliedAmount/Ref), JV apply แบบ GL-driven รองรับทั้ง JV มี/ไม่มี VAT leg._
_รอบ 85: แก้ "จ่ายเงินแล้ว (cash sale) + หักมัดจำ" ชนกัน — เดิม paidNow branch_
_approve แล้วจ่าย "เต็ม balanceDue" ทันที แล้ว skip บล็อกหักมัดจำ (อยู่ใน_
_approveAfter ที่ข้ามเพราะ Approved แล้ว) → เงินเข้าธนาคารเต็มใบทั้งที่รับจริงแค่_
_ส่วนต่าง + มัดจำค้างไม่ถูกหักเงียบ ๆ. แก้: แยก _applyPendingDeposits (consume_
_list กันหักซ้ำ) เรียกทั้ง 2 branch — ลำดับใหม่: approve → หักมัดจำ (ลด_
_BalanceDue) → จ่ายเฉพาะยอดคงเหลือจริง (re-fetch หลัง apply)._
_รอบ 84: หักมัดจำในฟอร์ม — (1) totals box เพิ่มแถว "หักมัดจำที่เลือก / คงเหลือรับ_
_ชำระ" (updateDepositSummary — ยอดสุทธิใบไม่เปลี่ยนตาม §86/4, มัดจำลดยอดค้างหลัง_
_อนุมัติ) (2) แก้หน่วยยอด: DepositSummary.outstandingAmount เป็น "ฐานไม่รวม VAT"_
_แต่ ApplyDeposit.amount เป็น gross → เดิม UI ส่งฐานเป็น gross = มัดจำมี VAT หัก_
_ขาด (เศษ VAT ค้าง AR). เพิ่ม _depGrossOut แปลงฐาน→gross ใช้ทุกจุด (banner/_
_checkbox rows/quick-apply/modal) + label "(รวม VAT)" (3) แก้ modal picker ใช้_
_d.id (เดิม d.depositDocumentId ที่ไม่มีจริง → option value undefined หักไม่ได้)._
_รอบ 83: purge ครอบผลข้างเคียงนอก GL ครบ (mirror void) — เดิม PurgeDocumentAsync_
_ลบ JE/Payment/WHT/e-Tax แต่ "ไม่กลับ" สต๊อก, ยอดใบต้นทางที่ถูกตัดชำระ, project_
_billed/cost, FixedAsset auto-register, bank match (MatchedPaymentId + สถานะ_
_Matched ค้าง), ReconciliationGroup, OcrScanResult.CreatedDocumentId → resync =_
_ตัดสต๊อกซ้ำ/ยอดเบิ้ล/กระทบยอดค้างผี. เพิ่ม step 0d (Revert+Billing−1+Stock−1+_
_PCE reverse — ข้าม Draft/Voided กันคืนเกิน), ลบแถว StockMovement ของใบ, 0e_
_asset cascade (NeedsReview+ไม่มี dep → ลบ; อื่น ๆ ตัด link), reset bank txn เป็น_
_Unmatched ทั้งขา JE และ Payment, 7b ตัด link OCR scan, unwind groups หลัง_
_commit. + JE คู่กลับรายการ: ลบ REV → คืนใบเดิม Posted; ลบใบเดิม → ลาก REV ตาม_
_(AccountingService). batch-delete แจ้งรายการที่ข้าม (ผูกเอกสาร) แทนเงียบ._
_รอบ 82: หักมัดจำ "หลายใบ" ในฟอร์มสร้างเอกสาร — เดิม dropdown เดียว = หักได้ใบเดียว/_
_บันทึก. เปลี่ยนเป็น checkbox rows (แต่ละใบมียอดของตัวเอง, booking-match pre-tick),_
_`_pendingDepositApply` → array `_pendingDepositApplies` (ปนมัดจำเอกสาร+JV ได้),_
_save() วน applyDeposit/applyJournalDeposit ทีละใบหลัง approve, fail-soft ต่อใบ +_
_สรุปผลรวม. ติ๊กใบมัดจำ → autofill เลขจอง (fBookingNumber) + อ้างอิง (fRef) จากใบ_
_มัดจำ (เฉพาะตอนช่องว่าง ไม่ทับที่ผู้ใช้พิมพ์; ไม่แตะบรรทัดสินค้า) ผูกใบเข้า booking_
_เดียวกัน. frontend เท่านั้น (backend applyDeposit เรียกซ้ำสะสมได้อยู่แล้ว)._
_รอบ 81: WHT cert ประเภทแบบ guard (ภ.ง.ด.3↔53 ตามผู้ถูกหัก) — ResolveWhtFormType_
_+ DetectJuristic บังคับทุก create path, override ค่าที่ integration ส่งผิด._
_รอบ 80: OCR review inline line editing (Description/Quantity/UnitPrice แก้ในตาราง_
_→ SetExtractedLineFieldsAsync recompute Amount + persist, คู่กับ qty-guard); ปิดลูป_
_project-match feedback (SetExtractedLineProject → RecordUserChoiceAsync)._
_รอบ 81 (2026-07-24): Invoice undue output VAT — ใบแจ้งหนี้บริการล้วน Cr 21913_
_(ไม่เข้า ภ.พ.30 จนรับเงิน §78/1) → reclass 21913→21911 + OutputVatDueAt เมื่อ_
_รับชำระ (Payment/ใบเสร็จ settlement); ใบมีสินค้า TrackStock = ส่งมอบ (§78) ลง_
_21911 ทันทีเหมือนเดิม; report ตัดสิน GL-driven (ใบเก่า net 21913=0 → พฤติกรรมเดิม)._
_รอบ 82 (2026-07-24): กันใบกำกับซ้อน/ยอดหาย (เคส INV 97,500 + TIV 75,000 ไม่ผูกกัน):_
_(1) Supersede guard — อนุมัติ TIV ที่แปลงจาก INV ยอดต้องเท่าใบต้นทาง (±0.01)_
_ไม่งั้น block (กัน partial/ราคาหลุด 0 ทำส่วนต่างหายจาก GL เงียบ);_
_(2) integration invoice.created: ถ้ามี INV active อ้างอิง (WO) เดียวกัน — ยอดตรง_
_→ ออกใบกำกับผ่าน ConvertDocumentAsync+Approve (copy บรรทัดจากใบจริง + supersede_
_อัตโนมัติ), ยอดไม่ตรง/ชำระแล้ว/มีมัดจำ → Failed ดัง ๆ ไม่ mint TIV แยกใบ;_
_(3) payment.received lookup ข้ามใบ Voided/Rejected + เลือก TIV ก่อน INV._
_รอบ 83 (2026-07-24): settlement receipt v2 — (A) ใบรวม (Combined) รับครบงวดเดียว_
_ผ่าน modal/paidOnIssue → ไม่ออกใบเสร็จแยก ตัวใบรวม ServedAsReceipt → หัว 3-in-1_
_"ใบแจ้งหนี้/ใบกำกับภาษี/ใบเสร็จรับเงิน" (ผ่อนหลายงวด = ใบเสร็จแยกต่องวด + หัวคง_
_2 หน้าที่เดิม); (B) ใบแจ้งหนี้ VAT รับครบงวดเดียว → settlement receipt ถือ VAT/_
_บรรทัดจากใบแจ้งหนี้ (carryVatFromSource) = ใบกำกับภาษี ณ วันรับเงิน §78/1 หัว_
_"ใบกำกับภาษี/ใบเสร็จรับเงิน" — ไม่ post JE/ไม่เข้า ภ.พ.30 ที่ใบนี้ (VAT รายงานที่_
_INV ผ่าน OutputVatDueAt, settlement ถูก exclude เดิม); งวดแรกบางส่วน/TIV source_
_= ใบเสร็จเปล่า VAT=0 เหมือนเดิม._
_รอบ 84 (2026-07-24): invariant "หัวมีคำใบกำกับภาษี ⇔ อยู่ใน ภ.พ.30" — (1) ใบเสร็จ_
_ถือ VAT ที่ settle ใบแจ้งหนี้ undue เป็น "เจ้าของแถว ภ.พ.30" แทน INV (เลข/วันที่ตรง_
_กระดาษใบกำกับจริง; INV skip กันซ้ำ; ไม่มีใบเสร็จถือ VAT → fallback INV ตาม_
_OutputVatDueAt เดิม — VAT ไม่หลุดรายงาน; legacy invoice (OutputVatDueAt null)_
_ใบเสร็จ convert ไม่ผ่านเงื่อนไข → ไม่ซ้ำ); (2) มัดจำ deferred ที่ recognize แล้ว_
_(เข้า ภ.พ.30) หัว upgrade เป็น "ใบกำกับภาษี/ใบเสร็จรับเงิน (เงินมัดจำ)" — ยกเว้น_
_ถูก apply เข้าใบปลายทาง (ใบปลายทางคือใบกำกับ กันกระดาษซ้ำ). ข้อยกเว้น invariant_
_ที่ตั้งใจ: ใบแจ้งหนี้สินค้า/legacy + ผู้ซื้อปฏิเสธใบกำกับ (ขายปลีก) = อยู่ในรายงาน_
_โดยกระดาษไม่มีหัวใบกำกับ (นำส่งครบตามกฎหมาย มี approval warning ชี้ทางแล้ว)._
_รอบ 16 (multi-team audit 8 โดเมน — ดู DEVELOPMENT_PHASES.md): void ใบที่ชำระ_
_ด้วย multi-doc payment คืนยอดธนาคารครบ (ReverseMultiDocPaymentInternalAsync;_
_ยกเลิกใบเดียวในกลุ่ม → block ให้ยกเลิกใบชำระก่อน); RealizeDeposit หัก_
_DepositRefundedAmount; ApplyDepositToInvoice ห่อ transaction + FOR UPDATE;_
_UpdateDocument บล็อกการแก้ใบที่ restore แล้วถือเลขจริง (§86/4); approve_
_idempotency guard ไม่นับ reversal (void→restore→approve ต้อง post JE ใหม่);_
_FX reval เฉพาะ monetary types; ค่าเสื่อม/ตีราคาเช็คงวดปิด + FiscalPeriodId;_
_ภ.ง.ด.3/53 แยกผู้ถูกหักด้วย DetectJuristic + กรองเฉพาะเอกสารฝั่งซื้อ + ภ.ง.ด.1_
_ไม่ดึงจากเอกสาร; BuildPnd กรอง SUMMARY/IsExcluded; void ติ๊กบรรทัดรายงานทุกแบบ_
_(RecalcWhtTotals); 50 ทวิ ออกตามงวดจ่าย (SourcePaymentId + pro-rate);_
_claimedElsewhere ยกเว้นงวดเดียวกัน; Sale.csv กรอง IsExcluded + ปี พ.ศ. ต่อแถว;_
_InputVat ไม่รวมเครดิตยกมา; ฐานรายงาน = VatableBase หักบรรทัดยกเว้น; e-Tax_
_BasisAmount/PDF ไม่หักส่วนลดซ้ำ + TaxId ผู้ซื้อบังคับ 13 หลัก + ISO8601 +07:00 +_
_ชื่อเอกสาร PDF=XML; AI: fingerprint ตรงกันเมื่อ sanitize PII, ImportDataReview_
_มี local heuristics, DailyCallCap นับเฉพาะ provider call, OCR เก็บสาขา/ที่อยู่;_
_tenant: CMS cart scope, POS ProductId, payroll includeSalary; XSS 4 หน้า;_
_import: พ.ศ.→ค.ศ. ทุกจุด + JE/bank dedup. **ใหม่: ภาษาเอกสาร th/en**)_

_Last updated: 2026-08-04 — Chatbot Phase 1-5.1 (public FAQ + tenant assistant_
_+ admin console + คลังความรู้/metrics + PDPA purge + rate limit ข้าม instance_
_+ ถามผ่าน LINE — รายละเอียด/งานที่เหลืออยู่ CHATBOT_PLAN.md); ก่อนหน้า:_
_LINE bot รับรูปใบเสร็จ → OCR → เอกสารทันที (§2.2b:_
_รวม Flex ปุ่มอนุมัติในแชท + postback guard + แจ้งกลับผู้ส่งเมื่ออนุมัติ)_
_+ routing บิลไม่เป็นทางการ → ใบรับรองแทนใบเสร็จ (§2.2c); ก่อนหน้า: ปฏิทินนำส่ง_
_ภาษี/ประกันสังคมบน dashboard (§5.3b) + แนบสลิปนำส่ง สปส. เข้ารอบเงินเดือน_

_รอบ 107 — **แอดมิน: กรอง/เรียงผู้ใช้ · คัดลอก secret · SSO ตั้งจากหน้าเว็บ**:
(1) **จัดการผู้ใช้งาน** เดิมมีแค่ค้นหา+แบ่งหน้า เรียงตายตัวตามวันสมัคร ⇒ หา
"ใครยังไม่ยืนยันอีเมล" / "ใครไม่ได้เข้านานแล้ว" ไม่ได้ → เพิ่มตัวกรอง สถานะ/
แอดมิน/ยืนยันอีเมล/ถือ License + เรียง 7 แบบ + จำนวนต่อหน้า (20/50/100) +
ตัวนับผลรวม + ล้างตัวกรอง (`GET /api/admin/users` รับ status/isAdmin/
emailVerified/hasLicense/sort · NULL ของ "เข้าใช้ล่าสุด" ไปท้ายเสมอทั้ง
asc/desc — คนไม่เคยเข้าไม่ควรลอยขึ้นหัวตอนเรียง "ล่าสุด")
(2) **คัดลอก secret** — ช่องรหัส SMTP/MS secret เพิ่มปุ่ม 👁 แสดง + 📋 คัดลอก
**ของค่าที่พิมพ์อยู่** (ค่าที่บันทึกแล้วยังไม่ส่งกลับมาแสดงตามเดิม — API คืนแค่
`hasPassword:true`; เขียนกำกับบน UI ว่าคัดลอกได้เฉพาะค่าที่เพิ่งพิมพ์)
(3) **SSO/OAuth ตั้งจากหน้าแอดมินได้แล้ว** — เดิมมีแต่ `appsettings.json`
(แก้ทีต้อง deploy+restart และแอดมินมองไม่เห็นว่าตั้งไว้ไหม). ย้ายมาเก็บใน
`SiteSettings` (+migration 9 คอลัมน์, secret เข้ารหัสด้วย SecretProtector) +
หน้า `/admin/sso-config.html` มีขั้นตอนตั้งค่าทีละข้อ + Callback URL ที่ต้องใส่
ใน console ของแต่ละเจ้า (คัดลอกได้) · `GET/PUT /api/admin/sso-config` ·
**เปิดใช้ไม่ได้ถ้ายังไม่มี Client ID** (กันปุ่มหลอก) · appsettings ยังเป็น
fallback ให้ deployment เดิม
(4) **หน้า login ซ่อนปุ่มที่ยังใช้ไม่ได้** — `/api/auth/sso-config` คืนเฉพาะ
provider ที่ "เปิดสวิตช์ + มีคีย์" (ตัวตัดสินเดียวกับ `SsoLoginAsync` ที่ปฏิเสธ
provider ที่ปิดอยู่) ⇒ ไม่มีปุ่มที่กดแล้วเจอ "ยังไม่ได้ตั้งค่า OAuth" อีก ·
ไม่มี provider ไหนเปิดเลย = ซ่อนทั้งบล็อก "หรือเข้าสู่ระบบด้วย"
(5) **LINE Login** (ไทยใช้เยอะสุด) — web OAuth2 authorization-code:
หน้า login ส่งไป `access.line.me/oauth2/v2.1/authorize` (state กัน CSRF, ล้าง
query หลังกลับกันยิง code ซ้ำ) → `AuthService.ValidateLineTokenAsync` แลก code
เป็น id_token ด้วย channel secret (ไม่ออกจาก server) แล้ว verify ที่
`api.line.me/oauth2/v2.1/verify` + เช็ค audience · รองรับ id_token ตรงด้วย
(นับจุดใน JWT แยกสองกรณี) · ต้องขอ scope `openid email` — ไม่มีอีเมล = เข้าไม่ได้
เพราะระบบผูกบัญชีด้วยอีเมล (แจ้งไว้ในขั้นตอนบนหน้าแอดมิน);_

_รอบ 106 — **แอดมิน: การใช้งานรายบริษัทต่อเดือน (เอกสาร/OCR/AI/อีเมล/e-Tax)**:
เดิมมีแต่หน้า "รายงานการใช้งาน AI" (เจาะ AI อย่างเดียว) — ตอบไม่ได้ว่าบริษัท
ไหนออกเอกสารอะไรไปกี่ใบในเดือนนั้น. เพิ่ม `GET /api/admin/company-usage?year=&month=`
(`AdminCompanyUsageController`, `[Authorize(Roles="SystemAdmin")]`) รวม 5 แหล่ง
เป็นแถวต่อบริษัท: **เอกสาร** group ที่ DB ตาม (CompanyId, DocumentType) — นับ
ทั้งหมด/อนุมัติแล้ว/มูลค่า (ตัดร่าง+รอดำเนินการ+ปฏิเสธ ออกจากยอดเงิน และตัด
Voided ออกอีกชั้นเฉพาะยอดเงิน — ใบยกเลิกเคยออกเลขจริงจึงยังนับเป็น "อนุมัติแล้ว")
· **OCR** `OcrScanResults` (ทั้งหมด/สำเร็จ) · **AI** `AiUsageDailyTenants`
rollup ชุดเดียวกับหน้า AI (ตัด sandbox — ยอดต้องตรงกัน) แยก "เรียกทั้งหมด" กับ
"จ่ายจริง" ตามกฎเหล็ก #1 · **อีเมล** `EmailQueues` ที่ Status=Sent ·
**e-Tax** `EtaxInvoices`. นับตาม **CreatedAt** (มิเตอร์การใช้งาน) ไม่ใช่เดือน
ภาษีของเอกสาร — คนละมุมกับรายงานบัญชี ระบุไว้บนหน้าจอชัด. หน้า
`pages/admin-company-usage.html` (adminOnly): KPI 4 ตัว + ตารางเรียงตามจำนวน
เอกสาร + คลิกแถวกางชิปแยกชนิดเอกสาร + ค้นหา + CSV (BOM ให้ Excel ไทยอ่านออก);_

_รอบ 105 — **"กดอนุมัติแล้วเด้งถามเลิกเคลม?" — method ชื่อซ้ำทับกันเงียบ**:
เคสจริงจาก screenshot: ใบไทวัสดุเป็น**ใบกำกับเต็มรูป** (ผู้ขายยกเลิกใบอย่างย่อ
ออกใบเต็มรูปแทนเพื่อให้เคลมได้) ผู้ใช้ติ๊ก "มีใบกำกับภาษีซื้อ — ขอเครดิต
ภ.พ.30" ครบ แต่บรรทัดค้าง 🚫 จาก OCR แล้วกดสลับไม่ได้ — ต้นเหตุ:
`toggleVatClaim` มี **2 ตัวชื่อซ้ำใน `Page` เดียวกัน** (ตัวฟอร์มรับ `(el)` /
ตัวหน้า detail รับ `(id, claim, el)`) JS เอาตัวหลังทับตัวแรกเงียบ ๆ ⇒ กดไอคอน
บนฟอร์มไปเรียกตัวหลังด้วย claim=undefined → เด้ง confirm "เลิกเคลมภาษีซื้อ
ใบนี้?" กลางฟอร์ม + ยิง API ด้วย DOM element แทน id. แก้ 3 ชั้น:
(1) เปลี่ยนชื่อตัวฟอร์มเป็น `toggleLineVatClaim` — ไอคอน ✓/🚫 กลับมาทำงาน
(2) ติ๊ก "มีใบกำกับภาษีซื้อ" → `_reclaimLinesForFullTaxInvoice` ปลด 🚫 ของ
บรรทัดที่เหตุผล "ใบเต็มรูปรักษาได้" (§82/5(1) ใบไม่สมบูรณ์/อย่างย่อ/บิลเงินสด)
อัตโนมัติ + toast — เหตุถาวร (§82/5(3)(4)(6) ค่ารับรอง/น้ำมันรถนั่ง) ไม่แตะ ·
ผู้ใช้ override เองไม่แตะ (3) ด่านก่อน save: header บอกเคลมแต่ทุกบรรทัด 🚫 →
confirm ภาษาคนให้เลือก เคลม (เปิด ✓ ให้) / คงไม่เคลม — ไม่ปล่อยกระดาษกับบัญชี
ขัดกันเงียบ. **checker ตัวที่ 11** `js_dup_method_check.py` จับ class นี้ทั้งเรพ
→ เจอตัวที่สองทันที: `projects.html openEdit` ซ้ำ (rename ด้วย prompt ทับฟอร์ม
แก้ไขเต็ม — ปุ่มแก้ไขโครงการแก้ได้แค่ชื่อมาตลอด) ลบตัวทับ ฟอร์มเต็มกลับมา;_

_รอบ 104 — **ตัวเลือกกระดาษ: ทีมนักบัญชี + ทีม UX ตรวจแล้วปรับตาม**:
เคสจริงจากผู้ใช้ — กลุ่ม "รับเงินแล้ว" ขึ้นหัวข้อแต่**ว่างเปล่า** และ
"ใบแจ้งหนี้/ใบกำกับภาษี" หายทั้งชุด เพราะ facade ซ่อนหัวใบกำกับตามธง
`vatRegistered === false` ซึ่งอาจผิด/ยังไม่ได้ติ๊กทั้งที่บริษัทจดจริง. แก้:
(1) **เลิกซ่อนตามธง** — แสดงครบเสมอ เลือกแล้วขึ้นกล่องเตือนแดง §90/2 พร้อม
ลิงก์ `settings.html?tab=tax` (ด่านจริงคือ approve ฝั่ง server) (2) หัวกลุ่มที่
option ถูกซ่อนหมดต้องหายทั้งกลุ่ม (`_hideEmptyPaperGroups`) (3) resolve
กระดาษจาก **flag จริง** ผ่านตัวตัดสินกลาง `_resolveModeFromFlags` ใช้ร่วมกับ
`_syncIssueModeUi` — เลิกผูกกับธง VAT ตอน hydrate.
**ผลตรวจ 2 ทีม (subagent)**: ทีมนักบัญชีไล่ 16 เคสธุรกิจ — mapping/JE ถูกเกือบ
หมด แต่พบ (ก) hint ใบสำคัญรับ **drift จาก JE จริง** (เขียน "Cr รายได้รับ
ล่วงหน้า" แต่ JE จริง = Cr รายได้+VAT) + เงินรับที่ไม่ใช่รายได้ควรไป JV —
แก้ title แล้ว (ข) hint มัดจำ/ใบส่งของไม่เตือน tax point §78 — เติมแล้ว
(ค) เคสรับเงินบางส่วน ณ วันออก — เติมคำแนะนำใน hint 3-in-1 แล้ว.
ทีม UX เดิน 4 persona — แม่ค้ารับโอนเลือกผิดเพราะ "ขายสด"/"ตั้งลูกหนี้"
แอดมินร้านวัสดุจบที่ใบวางบิลเพราะหาคำว่า "บิล": **เรียงกลุ่มใหม่** (รับเงินแล้ว
ขึ้นก่อน — งานที่ทำบ่อยสุด) ชื่อกลุ่ม = คำตอบ "รับเงินหรือยัง?" · ย้ายมัดจำ/
ใบเสร็จเข้ากลุ่มรับเงินแล้ว · เลิกคำ "ขายสด→ลูกค้าจ่ายแล้ว (เงินสด/โอน)",
"3-in-1→ใบเดียวครบ 3 อย่าง", ตัด Dr/Cr/e-Tax T03/IsDeposit/ชื่อ flag ออกจาก
ทุก hint (ภาษาคนก่อน มาตราตามหลัง) · "ใบกำกับภาษี" เดี่ยวติดป้าย "เหมือนข้อบน
ต่างแค่หัวกระดาษ" กันเลือกผิด · ตัดคำ "มัดจำ" ออกจากใบสำคัญรับ (ทับกับใบมัดจำ).
งานที่จดไว้ทำต่อ (ยังไม่ทำ): กระดาษ "ใบส่งของ/ใบกำกับภาษี" (e-Tax T04) ·
คำถามนำ 2 ข้อเหนือ dropdown · gate ใบกำกับอย่างย่อด้วย IsRetailApproved;_

_รอบ 103 — **ใบวางบิลรวมใบแจ้งหนี้หลายใบ — จาก doc ที่โกหกให้เป็นของจริง**:
tooltip กับ doc เขียนว่า "ใบรวมยอด invoice หลายใบไปวางบิลครั้งเดียว" มานานแต่
โค้ดไม่มีทางทำ (ConvertDocumentAsync รับใบเดียว · ไม่มี Invoice→BillingNote ใน
convert map) — ผู้ใช้ต้องพิมพ์บรรทัดเอง. เพิ่มเส้นทาง compose จริง:
`GET document/billing-note/outstanding?contactId=` (ใบแจ้งหนี้/ใบกำกับ/ใบเพิ่มหนี้
ที่ Approved/Sent/PartiallyPaid/Overdue + BalanceDue>0 ของลูกค้า พร้อมบอกใบที่
ถูกวางบิลแล้วอยู่ใบไหน) + `POST document/billing-note/from-invoices` →
`CreateBillingNoteFromInvoicesAsync` (ไฟล์ใหม่ `DocumentService.BillingNote.cs`,
class เปลี่ยนเป็น partial): ตรวจ ชนิด/สถานะ/ยอดค้าง/ลูกค้าเดียวกันทั้งชุด/
ห้ามซ้ำใบวางบิล active → สร้าง BN ร่างผ่าน `CreateDocumentAsync` ปกติ (เลขจริง
ออกตอนอนุมัติ · **ไม่ลง JE** — ตัวหนี้อยู่ที่ใบต้นทาง) 1 บรรทัด = 1 ใบ ยอด =
BalanceDue (รวม VAT ของใบต้นทางแล้ว → VatRate 0) เรียงตามวันที่ · ลิงก์ต้นทาง
ต่อบรรทัดเก็บใน **`DocumentLine.SourceDocumentId` (คอลัมน์ใหม่ + migration)**
ใช้กันรวมใบเดิมซ้ำ (ทวงลูกค้าซ้ำสองทาง = เสียเครดิต). UI: ฟอร์มใบวางบิล +
เลือกลูกค้า → กล่องฟ้าแสดงใบค้างให้ติ๊ก (ใบที่วางบิลแล้ว disable + โชว์เลข BN)
+ ยอดรวมสด → ปุ่มสร้าง → ปิดฟอร์ม เปิดใบที่สร้าง. ตรวจด้วย simulation
validation matrix 116 เคส (ชนิด×สถานะครบ + dedup/คนละลูกค้า/ใบลบ/เรียงลำดับ)
— ผ่านหมด. **audit ครบทุก DocumentType ในรอบเดียวกัน**: convert map ครบถ้วนดี
(PR→PO→GRN→PI→PV · Expense→PV/CIL · Receipt/PV→CN/DN · CN terminal) · พบ+แก้
อีกจุด: ใบมัดจำเป็น pseudo-type (DB = Receipt+IsDeposit) เปิดแก้แล้ว facade
เดิมชี้ "ใบเสร็จ" — ตอนนี้ชี้ "ใบมัดจำ" ถูกต้อง;_

_รอบ 102 — **ฟอร์มสร้างเอกสาร: "ประเภทเอกสาร" ชั้นเดียว = กระดาษที่จะออก**:
ผู้ใช้ยังงง "ใบแจ้งหนี้/ใบกำกับภาษี" กับ "ใบแจ้งหนี้/ใบกำกับภาษี/ใบเสร็จ" สร้าง
ต่างกันยังไง ต้องติ๊กจ่ายไหม — เพราะการเลือกเป็น 2 ชั้น (ชนิดดิบ → dropdown
"หัวกระดาษ" ที่โผล่ทีหลัง) + ติ๊กจ่าย. รวมเป็น **ตัวเลือกเดียว `fPaper`**:
รายการคือกระดาษปลายทางตรง ๆ จัดกลุ่มตาม "เงิน" (ก่อนขาย/เรียกเก็บ · ขายเครดิต
ยังไม่รับเงิน · รับเงินแล้วจบในใบเดียว — ระบบบันทึกรับเงินให้ · รับเงินอื่น ๆ ·
ปรับปรุงหนี้ · ฝั่งรายจ่าย) เลือกแล้วระบบตั้ง `fDocType` (ยังเป็น source of
truth เดิม — payload/แปลง/JE ไม่แตะ) + issue mode + flag ทุกตัวให้เอง.
กลไก: `_PAPERS` map กระดาษ→(type, mode) · `onPaperChange` → ตั้ง type (เรียก
`onDocTypeChange` เฉพาะตอนชนิดดิบเปลี่ยนจริง) แล้วตั้งโหมดหลังรอบ deferred ของ
`_syncIssueModeUi` (คิว FIFO — กันโดน resolve จาก flag เก่าทับ) ·
`_syncPaperFromState` (เรียกท้าย `_syncIssueModeUi` ทั้งสองทางออก) ซิงก์ย้อน
ตอน hydrate ใบเดิม/ใบแปลง/โค้ดตั้งชนิดเอง + copy `disabled` (แก้ไขใบเดิม
เปลี่ยนชนิดไม่ได้เหมือนเดิม) · `_syncPaperOptionVisibility` ใช้กติกาชุดเดียว
กับของเดิม: ฝั่งรายรับ/จ่าย (CN/DN อยู่ทั้งสองฝั่ง) · ไม่จด VAT ซ่อนหัวใบกำกับ
ทั้งชุด · ใบแปลงจากขายเครดิตซ่อน ขายสด/หัวรวม/3-in-1 และเปิด `tax_paid` แทน ·
โหมดที่ใบเดิมใช้แต่บริบทซ่อน — เปิด option ให้เลือกเห็น ไม่เด้งไปค่าอื่นเงียบ.
select เดิม (`fDocType`, `fIssueMode`+label) ซ่อนใน DOM — hint ฟ้า (ชนิด)/เขียว
(โหมด) ยังแสดง โดยกระดาษที่มีโหมดโชว์เฉพาะ hint เขียวกันข้อความตีกัน ·
"ตัวเลือกขั้นสูง" (checkbox จริง) ยังอยู่เป็นทางหนีไฟ. ยืนยันด้วย harness รัน
Page จริงทั้ง object กับ DOM จำลอง (select สร้างจาก markup จริง) 40+ assertion:
เลือกกระดาษ→flag ครบ 13 แบบ · hydrate ย้อน 8 แบบ · ใบแปลง/ไม่จด VAT/ฝั่งจ่าย/
ล็อกตอนแก้ไข — ผ่านหมด · negative test (ตัดบรรทัดตั้งโหมด) ฟ้อง 9 เคส;_

_รอบ 101 — **แท็บ ภ.พ.36 ในหน้ารายงานภาษี: งวดที่นำส่งแล้วหายไปทั้งงวด**:
แถวเทา "ยังไม่สร้าง" (รอบ 36) ดึงจาก `dashboard.pending` อย่างเดียว — พอกด
นำส่ง งวดนั้น**หลุดจาก pending ไปอยู่ recentHistory** แถวเทาจึงหายตาม และถ้า
ไม่เคยกดสร้างรายงาน งวดนั้นก็ไม่โผล่ที่ไหนเลยทั้งที่จ่ายเงินไปจริง ⇒ ผู้ใช้
เข้าใจว่า "ไม่มียอด". แก้ 2 ชั้น: (1) **server** `RemitAsync` ของ VatPp36 เรียก
`TryEnsurePp36ReportAsync` สร้างรายงานงวดนั้นให้อัตโนมัติ — idempotent, อยู่
**นอก** transaction ของการนำส่ง และห้าม throw (นำส่ง commit ไปแล้ว ห้ามล้มย้อน
หลังเพราะสร้างรายงานพลาด) (2) **UI** รวมงวดจาก `recentHistory` เข้าแถวเทาด้วย
ป้าย "นำส่งแล้ว · ยังไม่มีรายงาน" (พื้นเหลือง) — ครอบข้อมูลเก่าที่นำส่งไปก่อน
มี auto-generate. **ความสอดคล้องของปุ่ม/สถานะ**: เดิมเช็ค
`taxType.toLowerCase().includes('vat')` ซึ่ง `'VatPp36'` ก็ผ่าน ⇒ แถว ภ.พ.36 มี
ปุ่ม **ภ.ซื้อ · ภ.ขาย · ภ.พ.30** ทั้งที่ §87 รายงานซื้อ-ขาย และแบบ ภ.พ.30 เป็น
ของ VAT ปกติ ภ.พ.36 (§83/6) ไม่มีของตัวเอง — จำกัดเป็น `VAT` จริงเท่านั้น แล้ว
ใส่ปุ่ม "💸 หน้านำส่ง/ใบเสร็จ" แทน; ฝั่ง Excel export ก็เลิกแตกชีต
"รายงานภาษีขาย/ซื้อ" ให้ ภ.พ.36 (ใช้ชีต "รายการ" + สรุป) ให้ตรงกัน;
เพิ่มป้ายใต้สถานะรายงานบอก **สถานะการนำส่งเงิน** (นำส่งแล้ววันไหน / รับรู้ภาษี
ซื้อเข้า ภ.พ.30 แล้วหรือยัง) เพราะ "ร่าง" ของรายงานคนละเรื่องกับการจ่ายเงิน
ผู้ใช้เห็นแล้วเข้าใจว่ายังไม่ได้นำส่ง · ตัวเทียบชนิดภาษีรวมเป็น `_isType()`
ตัวเดียว รองรับทั้งชื่อ enum และตัวเลข (serializer ส่งได้ทั้ง 2 แบบ);_

_รอบ 100 — **"เมนู e-Tax กดแล้วขึ้นหน้าตั้งค่า" — ที่แท้คือหน้าเข้าไม่ได้เลย**:
ไม่ใช่ดีไซน์ แต่เป็นบั๊ก: `pages/etax.html` อ่าน `localStorage['companyId']`
ซึ่ง**มีแต่ portal `/connect` เท่านั้นที่เขียน** แอปหลักไม่เคยเขียนคีย์นี้เลย
⇒ ได้ null ทุกครั้ง → `window.location.href='/pages/settings.html'` ทันที
= หน้า e-Tax Invoice เข้าไม่ได้สักครั้งตั้งแต่เขียนมา. แก้ให้ใช้ resolver กลาง
`Layout.getCompanyId()`. **defect class เดียวกันอีก 2 จุด**: `mobile-expense.html`
(ขึ้น "ต้อง login + เลือกบริษัทก่อน" ตลอด ส่งเบิกไม่ได้) และ
`pages/signatures-logic.js` อ่าน `'selectedCompanyId'` ที่ไม่มีใครเขียนเลยทั้งเรพ
⇒ แท็บรออนุมัติว่าง + ปุ่มอนุมัติ/ปฏิเสธ `return` เงียบ ๆ (กดแล้วไม่มีอะไร
เกิดขึ้น ไม่มี error) — แก้ทั้งหมด + เปลี่ยน guard ให้ดังแทนที่จะเงียบ.
**คำถาม "เมนูไหนไม่ได้ใช้ก็เอาออก"**: e-Tax เป็นฟีเจอร์จริงตามกฎเหล็ก #2 F
(ETDA ขมธอ.3-2560) ไม่ควรลบทิ้ง แต่เป็น opt-in ⇒ เพิ่มธง `etaxOnly` บน nav item
คู่กับ `_etaxEnabled` ที่อ่านจาก `/settings` **ครั้งเดียวกับที่ดึง vatRegistered
อยู่แล้ว (ไม่มี request เพิ่ม)** → บริษัทที่ยังไม่เปิดใช้ e-Tax ไม่เห็นเมนูนี้
เลย; ถ้าเปิดหน้ามาแล้วยังไม่ได้เปิดใช้ แบนเนอร์อธิบายว่าเมนูนี้ทำอะไร + ปุ่ม
"ซ่อนเมนูนี้" (ผ่านกลไกซ่อนเมนูกลาง เปิดกลับได้ที่ ตั้งค่า > ทั่วไป).
ตรวจ nav ทั้ง 107 รายการ — ปลายทางมีไฟล์จริงครบทุกอัน ไม่มีเมนูตายอื่น;_

_รอบ 99 — **หน้านำส่งภาษี/ประกันสังคม: กรองตามประเภทแบบได้ + ลด noise**:
หน้าจอจริงมี 13 รายการค้างจาก 5 แบบ × 5 งวด เรียงปนกัน ไม่มีตัวกรองเลยสักตัว
และทุกแถวขึ้น "เลยกำหนด" แดง + ปุ่ม primary น้ำเงิน ⇒ ทุกอย่างเด่นเท่ากัน =
ไม่มีอะไรเด่น (alarm fatigue) ผู้ใช้ที่จะยื่น "ภ.พ.30 เม.ย." ต้องไล่สายตาเอง.
เพิ่ม: **ชิปกรองตามแบบ** (เลือกได้หลายประเภท พร้อมจำนวน+ยอดในชิป) · ตัวกรอง
สถานะ/งวด/ค้นหา · **3 มุมมอง** (ตามงวด/ตามประเภท/รายการ) พับกลุ่มได้ · KPI
กดเป็นตัวกรองลัด · ป้ายบอก **"เลยกำหนด N วัน"** แทนคำลอย ๆ + แถบสีความด่วน
หน้าแถว (ปุ่มลดเป็น outline เท่ากันหมด ให้สีสื่อความด่วนแทน) · ประวัติกรอง
ประเภท/ค้นหา + สรุปยอด · มือถือแปลงตารางเป็นการ์ด · จำตัวกรองต่อบริษัทใน
localStorage · แถบ ภ.พ.36 "รอรับรู้ภาษีซื้อ" ย้ายออกนอกการ์ดที่ถูกกรอง (ตัว
กรองต้องไม่ซ่อนงานที่ค้างอยู่). ตัวตัดสินความด่วน `_urgency` เป็นตัวเดียว ใช้
ร่วมทั้ง badge/แถบสี/ตัวกรอง. **แถมแก้บั๊กร่วมทั้งระบบ**: `Layout.toast` ไม่เคย
รับพารามิเตอร์ที่ 3 (ระยะเวลา) แต่มีคนเรียกส่งมาแล้ว **40 จุด** → ข้อความสอน
ขั้นตอนยาว ๆ หายใน 3.5 วิ; และ `.toast-warning` ไม่มีสีพื้นเลย (`.toast` ตั้ง
`color:#fff`) = ตัวอักษรขาวบนขาว มองไม่เห็น — แก้ทั้งคู่;_

_รอบ 98 — **รายการประจำ: ออกเอกสารอนุมัติ + ส่งอีเมลได้จบในฟอร์มเดียว**:
เดิมมี hook `OnRecurringDocumentCreatedAsync` อยู่แล้ว แต่มันส่งเฉพาะเมื่อ
tenant ไป**สร้างกฎเองที่หน้า "ตารางส่งอีเมล"** (trigger `RecurringInvoiceCreated`)
⇒ ผู้ใช้ที่ตั้ง SMTP บริษัทไว้แล้วยังไม่มีอะไรถึงลูกค้าเลยและไม่มีที่ไหนบอก
(silent no-op เต็มรูป). เพิ่มธง `RecurringTransaction.AutoSendEmail` +
ช่องติ๊กในฟอร์ม: ไม่มีกฎ → ระบบ enqueue เองด้วย **rule เสมือน** (`Id=Guid.Empty`
→ `EmailQueue.RuleId=null`) ส่ง**ทันที** ไม่รอ 09:00; มีกฎอยู่แล้ว → กฎชนะ
ไม่ส่งซ้ำ. invariant `AutoSendEmail ⇒ AutoApprove` บังคับทั้ง service
(`CreateAsync`/`UpdateAsync`) และ UI (ล็อกช่อง + บอกเหตุผล §86/4 ใบร่างเป็น
`DRAFT-{guid}` ส่งไม่ได้) — ไม่ใช่ปล่อยติ๊กแล้วเงียบ. ฟอร์มดึงสถานะอีเมลจริง
จาก `GET /email-config` มาแสดง (พร้อม/ยังไม่ทดสอบ—ครอบทั้ง SMTP/MS Graph/Gmail
ไม่ใช่ดูแค่ `smtp.host`/ยังไม่ตั้ง→ใช้อีเมลกลาง) + เช็คว่าผู้ติดต่อมีอีเมลไหม
ตั้งแต่ตอนบันทึก + เตือนเมื่อชนิดเอกสารเป็นฝั่งซื้อ (จะส่งไปหาผู้ขาย) +
`settings.html` รับ deep link `?tab=email` ได้แล้ว (เดิมลิงก์ไปตกแท็บแรก).
ธงนี้ไม่มีผลกับ template สมุดรายวัน — ปัดทิ้งที่ service (ตรวจด้วย simulation
102 เคส ผ่าน invariant `AutoSendEmail ⇒ AutoApprove` + `⇒ ไม่ใช่ journal`);_

_รอบ 93 — **single source of truth: เดือนเคลม = อยู่ในรายงานจริง**: ผู้ใช้
ไม่ยอมกด "สร้างใหม่" (ล้างการติ๊ก/แก้ยอดของบรรทัดอื่นทั้งงวด — ถูกต้อง) →
เพิ่ม `TaxService.TryPullIntoDraftReportAsync(companyId, docId)`: หา**รายงาน
ร่างของงวดเคลม**แล้วดึงใบเดียวเข้าโดยใช้ `PullDocumentIntoReportAsync` เดิม
(INSERT บรรทัดเดียว + recalc — บรรทัดอื่นไม่ถูกแตะ) best-effort คืนข้อความ
ไม่ throw. ผู้เรียก: (1) `RecognizePp36InputVatAsync` หลัง commit — ทุกใบที่
รับรู้ (2) `POST document/{id}/vat-claim-period` หลังตั้งงวด — toast โชว์ผล
จริงจาก backend. + backfill เลข/วันที่ใบเสร็จ RD ใบเก่าจาก FilingNumber ของ
การนำส่งงวดเดียวกัน (idempotent) + ตาราง modal scroll แนวนอน;_
_รอบ 92 — **ภ.พ.36 ไม่โผล่ใน "ดึงเอกสาร"/ภ.พ.30 — ต้นเหตุจริง**: ทั้ง
`GenerateVatReport` และ `GetPullableDocumentsAsync` รับ PV เข้าฝั่งภาษีซื้อ
**เฉพาะที่ `HasTaxInvoiceReference=true`** (นิยามเดิม = อ้างใบกำกับซื้อเพื่อขอ
เครดิต) — ใบ ภ.พ.36 ไม่มีใบกำกับไทยจึงไม่เคยติ๊ก และ `RecognizePp36InputVatAsync`
ก็ไม่เคยตั้งให้ (ต่างจากเส้น §86/4 `ReclassifyUndueInputVatAsync` ที่ตั้งอยู่แล้ว)
⇒ GL มี Dr 11610 แต่รายงานไม่มีแถว + ปุ่มดึงเอกสารไม่เห็นใบเลยทุกกรณี. แก้:
recognition ตั้งธงให้ PV (ใบเสร็จ RD = ใบกำกับ §86/14 สิทธิ์เครดิตสมบูรณ์) +
**migration backfill** ใบที่รับรู้ไปแล้วก่อนหน้า (idempotent) + `ClaimBasisDate`
ของใบ ภ.พ.36 ใช้ `Pp36RdReceiptDate` เป็นฐาน §82/3 แทนวันจ่าย (เดิมหน้าต่าง
สั้นกว่าสิทธิ์จริง ~1 เดือน และงวดที่ดึงได้เพี้ยน);_
_รอบ 91 — **ภ.พ.36 หลังรับรู้ (เข้า ภ.พ.30 แล้ว...แต่หาไม่เจอ)**: สาเหตุจริง
2 ชั้น — ป้าย ✓ ไม่บอกงวดเคลม และ**รายงาน ภ.พ.30 เป็น snapshot**: สร้างไว้ก่อน
กดรับรู้ = บรรทัดใบ ภ.พ.36 ยังไม่อยู่จนกด "สร้างใหม่". แก้: ป้ายกลายเป็นปุ่ม
เปิด modal รายใบ (`GET remittances/pp36/recognized`) โชว์เดือนเคลม + สถานะใน
ภ.พ.30 ต่อใบ (ไม่มีรายงาน/สร้างก่อนรับรู้—บอกให้กดสร้างใหม่/อยู่ในรายงาน/
ยื่นแล้ว) + **แก้เดือนเคลมต่อใบจาก modal ได้เลย** ผ่าน endpoint ใหม่
`POST document/{id}/vat-claim-period` → `SetInputVatClaimPeriodAsync` ซึ่งห่อ
ตัวตรวจกลาง `ApplyInputVatClaimPeriodAsync` เดิมทั้งชุด (งวดร่างย้ายบรรทัด
อัตโนมัติ · §82/3 · งวดยื่นแล้ว block) — ไม่มีกติกาใหม่;_
_รอบ 90 — **ภ.พ.36 รอบสาม (VAT/WHT บนใบเดียวกัน)**: (0) **ฐานภาษี = ยอดจ่าย
จริงเสมอ ห้ามโหมดราคารวมภาษี** — ผู้ขาย ตปท. ไม่เก็บ VAT ไทย ยอดจ่ายไม่มี VAT
ปน การติ๊ก "ราคารวมภาษี" จะถอด 7/107 ออกจากยอดจ่าย ⇒ ฐานหด นำส่งขาด (จ่าย
11,009.25 → นำส่ง 720.23 แทน 770.65) + เจ้าหนี้ตั้งขาดเท่า VAT: ฟอร์มปลด+ล็อก
ช่องเมื่อติ๊ก 🌐 (ปลดล็อกเมื่อเลิกติ๊ก/เปลี่ยนชนิด) + guard ตอนอนุมัติกันทาง
API (JE ฝั่ง Cr ถูกอยู่แล้ว: เจ้าหนี้ = ฐาน · 21912 = VAT แยก); (1) guard อนุมัติ —
IsForeignService + VAT=0 → block (self-assess คือหัวใจ ภ.พ.36; ฟอร์มตั้ง 7%
ให้แล้วแต่ API ยิงข้ามได้ ปล่อยผ่าน = ใบหายทั้งวงจรเงียบ ๆ) (2) WHT ม.70:
`ResolveWhtPayableAccountAsync` รับ isForeignService → **21918** (เดิมตกไป
21916/17 ทุกใบ) · dashboard นำส่งเพิ่ม lane **WhtPnd54** (ภงด.54 · 21918 ·
กำหนด 7/15) + ตัดใบต่างประเทศออกจากการนับ ภงด.3/53 (3) รายงาน ภงด.3/53/54:
`PayeeInScope` เพิ่มสัญญาณ `doc.IsForeignService` (เดิมดูแค่ CountryCode ที่
มักไม่ได้กรอก → ใบเข้า ภงด.53 ผิดแบบ) (4) banner ฟอร์ม: อัตรา ม.70 15%/10%/
DTA + 40(8) ไม่เข้า ม.70 · ยอดเคลม ภ.พ.30 = ยอดนำส่ง (ยกเว้นส่วนต้องห้าม
§82/5 ที่นำส่งเต็มแต่เคลมไม่ได้ — ถูกต้องตามกฎหมาย);_
_รอบ 89 — **ภ.พ.36 รอบสอง (4 คำถามผู้ใช้)**: (1) แท็บ ภ.พ.36 เติมแถว "ยังไม่
สร้าง" จากงวดที่มียอดจริง (remittance dashboard — แหล่งเดียวกับหน้านำส่ง) +
ปุ่มสร้างคลิกเดียว (2) 📎 ประวัติกดดูไฟล์ได้ (fetch+blob JWT) (3) หน้า 11640
แยกใบ ภ.พ.36 เป็น section ต่างหาก — ใบพวกนี้ไม่มีวันเติมใบกำกับไทยได้
(CompleteSupplierTaxInvoiceAsync ก็ throw อยู่แล้ว) CTA ชี้ไปหน้านำส่งภาษี
(4) **§86/14**: `Document.Pp36RdReceiptNumber/Date` stamp ตอนรับรู้ (จาก
FilingNumber ของ remittance หรือ prompt ใหม่) → เลขที่ใบกำกับในรายงานภาษีซื้อ
ภ.พ.30 + CSV ยื่น = เลขใบเสร็จ RD ไม่ใช่ invoice ผู้ขาย ตปท. + **ตัด JE
ภ.พ.36R- ออกจาก scan JE ของ ภ.พ.30** (เดิมขึ้นบรรทัด JV ซ้อนกับใบจริงที่เข้า
ทาง BecameClaimableAt — ที่ผู้ใช้เห็น "ขึ้นเป็น JV" + เสี่ยงนับซ้ำ);_
_รอบ 88 — **วงจร ภ.พ.36 ครบถึง ภ.พ.30**: ผู้ใช้นำส่งแล้วหาใบใน ภ.พ.30 ไม่เจอ
— เพราะขั้นที่ 2 "รับรู้ภาษีซื้อ" (RecognizePp36InputVatAsync: 11640→11610 +
stamp BecameClaimableAt เมื่อได้ใบเสร็จ RD §77/2) ซ่อนอยู่ในแท็บประวัติโดยไม่มี
สถานะ. ปิดช่องว่าง: (1) dashboard เพิ่ม `Pp36AwaitingRecognition` (งวดที่นำส่ง
แล้วแต่ยังมีใบพัก 11640 — ตรวจจาก JE Reference ภ.พ.36R-YYYYMM ชุดเดียวกับ
กติกา idempotent เดิม) → แถบฟ้าค้างบนสุดหน้านำส่งภาษีพร้อมปุ่มรับรู้ (2)
history ต่อแถวมี `Pp36Recognized` → ✓ เข้า ภ.พ.30 แล้ว / ปุ่มรับรู้ (3) หน้า
success หลังนำส่งบอกขั้นถัดไป (4) แถวประวัติไม่มีไฟล์ → ปุ่ม ＋แนบ ย้อนหลัง
(5) ฟอร์ม PV: ติ๊ก 🌐 → banner วงจร 4 ขั้น + ตั้ง VAT 7% self-assess ให้
บรรทัดที่เป็น 0 (ปล่อย 0 = ใบไม่เข้า dashboard นำส่งเลย) + hint เมื่อคู่ค้า
ไม่มีเลขภาษีไทย (ชี้ ไม่ auto-ติ๊ก);_
_รอบ 87 — **ใบกำกับภาษีอย่างย่อ §86/6**: ขายมี VAT + ผู้ซื้อไม่รับใบกำกับ/
walk-in/ข้อมูล §86/4 ไม่ครบ (บุคคลธรรมดาที่ระบบ auto-downgrade ตอนอนุมัติ) →
หัวเปลี่ยนจาก "ใบเสร็จรับเงิน" เปล่า (ซึ่งผิดหลัก §86 — ผู้จด VAT ต้องออก
ใบกำกับบางรูปแบบทุกการขาย) เป็น **"ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ"**
(ใบกำกับขายเชื่อ → "ใบกำกับภาษีอย่างย่อ" เดี่ยว — ห้ามคำใบเสร็จ ม.105) +
บรรทัด "ยอดรวมทั้งสิ้นได้รวมภาษีมูลค่าเพิ่มแล้ว" (§86/6(6)) ใต้หัวทั้ง 2
renderer · ตัวตัดสินกลาง `IsAbbreviatedTaxInvoiceDoc` ใช้ร่วม title+โน้ต ·
ยกเว้น: ใบเสร็จ settlement ของใบกำกับ (ใบเสร็จเปล่า — กันใบกำกับซ้ำ) และมัดจำ
VAT พักรอ · ผู้ซื้อเคลมภาษีซื้อไม่ได้ §82/5(2) · VAT ขายเข้า ภ.พ.30 ครบ ·
เทสต์ `AbbreviatedTaxInvoiceTitleTests` เรียก resolver ตัวจริง;_
_รอบ 86 — **เปิดแก้ JE ของเอกสารทั้งใบ**: แผงปรับปรุง JE
(`AdjustDocumentJournalEntryAsync`) เลิกบล็อกบัญชีคุม — แก้ได้ทุกบรรทัดรวม
ลูกหนี้/เจ้าหนี้/ภาษี/มัดจำ/WHT และยอดต่อบรรทัด ตามคำขอผู้ใช้ hard rule
เหลือ 2 ข้อ: Dr = Cr และ **ยอดรวมแต่ละฝั่งต้องเท่าเอกสาร** (Σ Dr ปลายทาง =
Σ Dr ใบเดิม) · การขยับบัญชีคุมทุกตัวถูกจดลง audit เป็น `controlAccountsMoved`
(ก่อน→หลัง) + UI เตือน 2 ชั้น (ป้าย ⚠️ บนแถว + confirm ตอนบันทึกเมื่อยอดคุม
เปลี่ยนจริง) · gate ระดับเอกสารเดิมยังครบ (แบบยื่นแล้ว/e-Tax/งวดปิด/ใบมัดจำ/
ปรับซ้อน) — ป้าย 🔒 + "บัญชีคุมห้ามขยับ" ใน log รอบเก่าคือพฤติกรรมก่อนรอบนี้;_
_รอบ 85 — **OCR ส่วนลดซ้อน + คอลัมน์ยอด JE**: ใบ Scommerce ส่วนลด 2 ชั้น —
map บรรทัดเก็บส่วนลดรายบรรทัดจากกระดาษ (qty×unitPrice − amount) + reconcile
เขียนใหม่ยึด**ยอดรวมทั้งสิ้น**เป็นหลัก: บรรทัด ex-VAT เทียบกับฐานภาษี (ยอดรวม
− VAT) เท่านั้น ส่วนเกิน → `billDiscountAmount` ลงช่อง "ส่วนลดท้ายบิล"
ที่เดียว ห้ามกระจายใส่บรรทัด (เดิมเทียบข้ามฐาน incl/excl VAT แล้วกดบรรทัดลง
จนฐานภาษี = ยอดรวมทั้งบิล → VAT ถูกบวกซ้ำ) · ยอด Dr ใน "การบันทึกบัญชี"
ท้ายเอกสารเยื้องซ้ายจากยอด Cr 16px แบบบัญชีแยกประเภท (ทั้ง 2 renderer);_
_Last verified against codebase: 2026-09-01 (รอบ 117 — **LINE Login: Callback URL_
_มาจากสองแหล่งที่ไม่ผูกกัน**:_
_ผู้ใช้หา Callback URL ใน LINE Developers ไม่เจอ — มันอยู่แท็บ **"LINE Login"**_
_ไม่ใช่ "Basic settings" (คำแนะนำเดิมเขียนว่า "แท็บ LINE Login" แล้วแต่สั้นเกินจน_
_มองข้าม) → ขยายคำอธิบายให้ระบุว่าเป็นแท็บที่ 2 + ปุ่มคัดลอก URL_
_· **ของจริงที่เจอระหว่างตรวจ**: `redirect_uri` ถูกคำนวณ **2 ที่ที่ไม่ผูกกัน** —_
_หน้า `login.html`/`register.html` ใช้ `location.origin + '/login.html'` ส่วน_
_เซิร์ฟเวอร์ตอนแลก code ใช้ `SiteSettings.AppBaseUrl + '/login.html'`. OAuth บังคับ_
_ว่าสอง step ต้องส่ง URL **ตรงกันเป๊ะ** ⇒ ผู้ใช้เปิดด้วย `www.` แต่ AppBaseUrl_
_ไม่มี `www` (หรือกลับกัน / โดเมนสำรอง / http-https) = LINE ตอบ invalid_grant_
_แล้วผู้ใช้เห็นแค่ "แลก code ไม่สำเร็จ" ทั้งที่ตั้งค่าใน console ถูกแล้ว_
_→ ยุบเหลือแหล่งเดียว: `SsoSettings.LoginCallbackUrl` (สร้างจาก AppBaseUrl ผ่าน_
_`BuildLoginCallbackUrl`) ส่งออกทาง `/api/auth/sso-config` แล้วหน้า login ใช้ค่านั้น_
_(fallback เป็น origin เดิมเฉพาะตอนเซิร์ฟเวอร์รุ่นเก่าไม่ส่งมา + console.warn)_
_· **AppBaseUrl ว่าง = fail loud**: เดิม `GetLineRedirectUriAsync` คืน `"/login.html"`_
_แบบ relative ซึ่ง LINE ปฏิเสธและ error ไม่บอกอะไร → throw พร้อมบอกว่าต้องไปตั้ง_
_"URL ของระบบ" ที่ไหน + หน้า SSO config ขึ้นแบนเนอร์แดงเมื่อยังไม่ได้ตั้ง_
_(เดิมโชว์ `https://<โดเมนของคุณ>/login.html` เป็นตัวอย่างเฉย ๆ ไม่มีอะไรกัน);_
_Last verified against codebase: 2026-09-01 (รอบ 116 — **นำส่ง สปส. ยอดไม่ตรงกับ_
_ที่จ่ายจริง + ไม่มีทางกลับรายการ**:_
_ผู้ใช้กดนำส่งซ้ำก่อนตรวจ ⇒ สลิป K BIZ จ่ายจริง **8,762** แต่ JE ลง **8,784**_
_(JV-202609-0001: Dr 21815 / Cr Bank 8,784) — ต่าง 22 = ฝั่งนายจ้างที่ค้างค่าเดิม_
_ตัวเดียวกับรอบ 115 · ผลคือ **เงินฝากในบัญชีแยกประเภทหายเกินจริง 22 บาท** และจะ_
_ค้างเป็นผลต่างกระทบยอดธนาคารไปเรื่อย ๆ จนกว่าจะมีคนสังเกต_
_· **ด่านก่อนลง JE**: `SettleSocialSecurityAsync` ตรวจว่ายอดสองฝั่งสอดคล้องกันตาม_
_อัตราของปีนั้นไหม (ผ่าน `SsoWageBase.EmployerFrom`) ไม่ตรง = **block พร้อมบอกยอด_
_ที่ควรเป็นและทางแก้** — เดิมลง JE ไปก่อนแล้วค่อยหวังว่าจะตรง_
_· **`ReverseSsoSettlementAsync` (ใหม่)** — เดิม `SettleSocialSecurityAsync` เป็น_
_**ทางเดียว ไปแล้วกลับไม่ได้**: นำส่งผิดยอด/ผิดวัน/ผิดบัญชี = ตัน. ที่แย่กว่าคือ_
_ข้อความใน `PayrollRunEditPolicy.CanReopen` บอกให้ "กลับรายการนำส่ง สปส. ก่อน"_
_ทั้งที่ระบบไม่เคยมีปุ่มนั้น — **ด่านที่ชี้ไปยังทางที่ไม่มีอยู่** (defect class_
_"สถานะปลายทางที่ผู้ใช้ไปต่อไม่ได้"). ตัวใหม่กลับ JE **ลงวันเดียวกับวันที่นำส่ง_
_เดิม** (ไม่ใช่วันนี้ — งวดที่บันทึกไว้ต้องกลับเป็นศูนย์สุทธิ) + ล้าง SsoSettledAt/_
_JE id/เลขรับ/เงินเพิ่ม §49 (ทั้งชุดคำนวณใหม่ตอนนำส่งรอบหน้า) + row lock + AuditLog_
_+ เหตุผลบังคับ + บล็อกเมื่องวดบัญชีปิด_
_· UI: ปุ่ม "↩️ กลับรายการนำส่ง" ในรายการรอบ (โผล่เมื่อ `ssoSettledAt` มีค่า) +_
_แบนเนอร์แดงในโมดัลนำส่งเมื่อสองฝั่งไม่สอดคล้อง บอกยอดที่ควรเป็นก่อนกด_
_· **ลำดับซ่อมข้อมูลที่ลงผิดไปแล้ว**: กลับรายการนำส่ง สปส. → **กลับรายการจ่าย_
_เงินเดือน (ระบบซ่อมยอด ปกส. ให้เอง)** → จ่ายใหม่ → นำส่งใหม่ (ยอดเท่าสลิป)_
_· **`ReopenPaidRunAsync` ซ่อมยอด ปกส. ให้ระหว่างกลับรายการ** — ผู้ใช้รายงานว่า_
_"กดกลับรายการแล้ว นำส่งใหม่ยอดยังไม่ตรง" เพราะการกลับรายการ**นำส่ง**ไม่ได้แตะ_
_ยอดบนรอบเงินเดือนเลย (4,403 ยังอยู่) ⇒ ต้องไปไล่แก้รายคนเอง ซึ่งไม่มีใครเดาถูก._
_reopen คือจังหวะเดียวที่ปลอดภัยจะซ่อม (JE ถูกกลับแล้วและกำลังจะโพสต์ใหม่ตอนกด_
_"จ่าย") → normalize ทุกแถว: ตั้งฐานที่ยอดสมทบคิดมาจริง + ฝั่งนายจ้างตามฝั่ง_
_ลูกจ้างตามอัตรา ม.33 + คิด totals ใหม่ · **ไม่แก้เงียบ ๆ**: คืนจำนวนคนที่ถูกปรับ_
_ผ่าน `LastReopenSsoAdjustedCount` แล้ว controller ต่อเข้าข้อความตอบกลับ_
_("ปรับยอดประกันสังคมฝั่งนายจ้างให้ตรงกับฝั่งลูกจ้าง N คน — ตรวจยอดก่อนกดจ่าย");_
_Last verified against codebase: 2026-09-01 (รอบ 115 — **ไฟล์ สปส.1-10 ดึงยอด_
_ไม่ถูก: ค่าจ้างกับเงินสมทบมาคนละแหล่ง**:_
_อาการที่ผู้ใช้เจอ (รอบ ส.ค. 2569 หลังกลับรายการจ่ายแล้วแก้ยอด 1 ครั้ง) —_
_ไฟล์แสดง NAN THAN THAN MAW **ค่าจ้าง 14,094 คู่กับเงินสมทบ 683** ทั้งที่ 5% ของ_
_14,094 = 705 · และโมดัลนำส่งขึ้น **ลูกจ้าง 4,381 vs นายจ้าง 4,403** (ต่าง 22)_
_· **สาเหตุไม่ใช่การกลับรายการจ่าย** (ตัวนั้นไม่แตะ totals และ_
_`UpdatePayrollDetailAsync` คิด totals ใหม่ทุกครั้งที่แก้อยู่แล้ว) — ต่าง 22 คือ_
_705 − 683 พอดี = **ยอดฝั่งนายจ้างของพนักงานคนนั้นค้างค่าเดิม** เพราะโมดัลแก้ยอด_
_ให้แก้ฝั่งลูกจ้างได้อิสระโดยฝั่งนายจ้างไม่ตาม ทั้งที่ ม.33 ใช้ฐานเดียวกันทั้งคู่_
_· **สาเหตุที่สอง (เก่ากว่า)**: `ExportSso110Async` / `ExportSso110ExcelAsync` เอา_
_`GrossIncome` ไปใส่ช่อง "ค่าจ้าง" แต่เอา `SocialSecurityEmployee` ที่เก็บไว้ไปใส่_
_ช่อง "เงินสมทบ" — **สองตัวเลขคนละแหล่ง ไม่มีใครตรวจว่าตรงกัน** ⇒ แก้ยอดเมื่อไร_
_ไฟล์เพี้ยนทันทีโดยเงียบ. และเป็นปัญหาเชิงกฎหมายด้วย: **ค่าจ้างตาม ม.5 ≠ รายได้รวม**_
_(เบี้ยเลี้ยง/ค่าน้ำมันเหมาจ่ายที่ไม่ใช่ค่าตอบแทนการทำงานไม่นับเป็นฐาน) แต่ระบบ_
_ไม่เคยมีช่องให้ระบุฐาน ผู้ใช้จึงต้องไปแก้ยอดสมทบแทน = คู่ตัวเลขพังทันที_
_· **ทางแก้: ฐานเป็นตัวตั้ง เงินสมทบเป็นผลลัพธ์** —_
_`PayrollDetail.SocialSecurityBase` (ใหม่ + migration) เก็บค่าจ้างที่ยอดสมทบคิดมา_
_จริง · `Helpers/SsoWageBase` เป็นตัวกลางเดียว (`Clamp` / `Contribution` /_
_`Resolve` / `EmployerFrom` / `IsConsistent`) · ต่อสายครบ: calculate (เก็บฐานที่ใช้)_
_· import (ระบบนอกส่งฐานมาก็ใช้ ไม่ส่งก็อนุมานจากยอดสมทบ **ไม่ใช่จาก gross**) ·_
_แก้ยอดรายคน (แก้ฐาน → คิดสองฝั่งใหม่; แก้ยอดลูกจ้าง → ย้อนหาฐาน + ให้ฝั่งนายจ้าง_
_ตาม) · exporter ทั้ง .txt และ Excel ใช้ฐานนั้นเป็นช่อง "ค่าจ้าง"_
_· **ฝั่งนายจ้างคิดจากยอดลูกจ้าง ไม่ใช่คำนวณจากฐานใหม่อีกรอบ** (`EmployerFrom`) —_
_ยอดที่ระบบนอกส่งมามักปัดเป็นบาทถ้วนแล้ว (ฐาน 12,953 → 5% = 647.65 แต่หักจริง 648)_
_ถ้าคิดใหม่จะได้ 647.65 ⇒ สองฝั่งต่างกัน 0.35 ทั้งที่ควรเท่ากันเป๊ะ_
_· **ซ่อมเฉพาะแถวที่พังจริง**: `Resolve` คืน `GrossIncome` เมื่อคู่เดิมเข้ากันได้อยู่_
_แล้ว (สมดี 12,953/648 คงเดิม) หารกลับเฉพาะแถวที่ขัดกัน (NAN → 13,660)_
_· **ด่านก่อนยื่น**: Excel export ตรวจทุกแถวว่า 5% ลงตัวไหม ไม่ลงตัว = ใส่ไว้ใน_
_รายการเตือนของไฟล์ (สปส. e-Service คิดใหม่จากค่าจ้างที่กรอกแล้วตีกลับทั้งแถว)_
_· UI: โมดัลแก้ยอดเพิ่มช่อง "ฐานค่าจ้างประกันสังคม (ม.33)" ที่คิดสองฝั่งให้สด ๆ_
_(อัตรา/เพดานมาจาก API — `SsoRatePercent`/`SsoWageCeiling` ห้ามหน้าเว็บฝังเลขเอง)_
_+ แบนเนอร์แดงเมื่อเปิดแถวที่สองฝั่งไม่สอดคล้องกัน_
_· simulation เคสจริง 6 คน: negative test reproduce ได้ครบ (4,381 vs 4,403 และ_
_คู่ 14,094/683) → หลังแก้ทั้งสองฝั่งเท่ากันและทุกแถว 5% ลงตัว;_
_Last verified against codebase: 2026-09-01 (รอบ 114 — **เปิดกติกาเลขชุดใบกำกับ_
_ให้เองโดยไม่ต้องไปหาสวิตช์ + ครอบเส้น integration**:_
_· `CompanySettings.UnifyTaxInvoiceNumberSeries` เปลี่ยนเป็น **nullable** โดย_
_ตั้งใจ — **NULL = ยังไม่เคยตั้ง → ใช้ค่าแนะนำ = เปิด** ผ่าน_
_`TaxInvoiceSeriesPolicy.IsUnifiedSeriesEnabled(bool?)` (ทุกจุดที่อ่านค่าต้อง_
_เรียกตัวนี้ ห้ามเขียน `?? false` เอง — มี 3 จุด: approve · settlement receipt ·_
_integration)_
_· **จงใจไม่เขียน migration ไล่ `UPDATE ... SET = true`** เพราะรายการ migration_
_รันทุกครั้งที่สตาร์ท ⇒ ผู้ใช้ที่ปลดติ๊กเองจะถูกทับกลับเป็นเปิดทุกรอบ (defect_
_class "ห้ามทับค่าที่ผู้ใช้ตั้งเอง"). ปลดติ๊ก → เก็บ `false` ซึ่งไม่ถูกแตะอีก_
_· ⚠️ ผู้ที่รัน build ระหว่างรอบ 113 จะมีคอลัมน์เป็น `NOT NULL DEFAULT false`_
_อยู่แล้ว → migration เพิ่ม `DROP NOT NULL` + `DROP DEFAULT` ให้ แต่**ค่าที่เป็น_
_false อยู่แล้วไม่ถูกแตะ** (แยกไม่ออกจากเจตนาผู้ใช้) — ต้องไปติ๊กเองครั้งเดียว;_
_Last verified against codebase: 2026-09-01 (รอบ 113 — **กติกาเลขชุดใบกำกับ:_
_"หัวมีคำว่าใบกำกับภาษี → เลขชุด TIV เสมอ"**:_
_ต่อจากรอบ 112 — ผู้ใช้ถามว่าจะรวมใบกำกับให้เป็น TIV ชุดเดียวดีไหม หรือแยกให้ขาด_
_(TIV=ใบกำกับล้วน / รับเงิน=REC). คำตอบ: **สองข้อนี้คนละแกน** — ข้อแรกคือกติกา_
_"เลขชุดไหน" ข้อหลังคือกติกา "ออกกี่ใบ" (= `ReceiptIssueMode` ที่ทำในรอบ 112)_
_ใช้ร่วมกันได้และควรใช้ทั้งคู่. เลือกกติกาแรกเป็นแกนเลข เพราะรายงานภาษีขาย §87_
_คือรายการใบกำกับล้วน ๆ ⇒ ต้องอยู่ชุดเดียวเรียงไม่ขาดช่วง สรรพากรหยิบเล่มเดียวจบ_
_· **`Helpers/TaxInvoiceSeriesPolicy`** — `CarriesTaxInvoiceRole(doc, resolvedTitle)`_
_ตัดสินจาก**หัวที่ resolver ตัวเดียวกับกระดาษคำนวณให้** (ไม่ใช่กติกาสำเนาที่สอง)_
_+ พื้นบังคับ "TaxInvoice ที่มี VAT เป็นใบกำกับเสมอ" กันผู้ใช้ตั้ง `CustomTitle`_
_แล้วเผลอลบคำนั้นจนใบกำกับตัวจริงหลุดเล่มหลัก; `SeriesTypeOverride` คืนชนิดที่ใช้_
_**เลือกตัวย่อ** เท่านั้น_
_· **ไม่แตะ `DocumentType`** ซึ่งคุม JE / การนับ ภ.พ.30 / สายแปลงเอกสาร —_
_เปลี่ยนแค่ตัวย่อของเลข. `DocumentNumberGenerator` นับจาก prefix ไม่ใช่ชนิดอยู่แล้ว_
_⇒ สองชนิดใช้ TIV ร่วมกันได้และยังเรียงไม่ขาดช่วงต่อ prefix ตาม §86/4_
_· **`Document.IsTaxInvoiceByLaw` (nullable) ตรึงตอนอนุมัติ**พร้อมเลขที่ (แบบเดียว_
_กับ `IssuerBranchCode`) — เลขออกไปแล้วเปลี่ยนย้อนหลังไม่ได้ ค่าที่ใช้ตัดสินจึงต้อง_
_หยุดนิ่งเท่ากัน + เป็นคำตอบสำเร็จรูปให้ผู้ตรวจว่า "ใบไหนเป็นใบกำกับ".  null =_
_ใบที่อนุมัติก่อนมีฟีเจอร์นี้ ห้ามตีความว่า false_
_· ต่อสาย **2 จุดที่ออกเลข**: `ApproveDocumentAsync` และ `CreateSettlementReceiptAsync`_
_(เส้นหลังออกเลขเองไม่ผ่าน approve — ถ้าลืมจุดนี้ ใบเสร็จที่เป็น "ใบกำกับ ณ วันรับ_
_เงิน §78/1" จะยังตกอยู่เล่ม REC ซึ่งเป็นเคสที่ผู้ใช้เจอบ่อยที่สุด)_
_· สวิตช์ `CompanySettings.UnifyTaxInvoiceNumberSeries` (**default false** ⇒ เลขของ_
_tenant เดิมไม่ขยับแม้แต่ใบเดียว) + ช่องติ๊กในหน้าตั้งค่าพร้อมคำเตือนว่ามีผลกับ_
_ใบใหม่เท่านั้น_
_· **ผลที่วัดได้** (simulation 11 เคสจริง + negative test ที่พิสูจน์ว่ากติกาเดิม_
_ปนจริงก่อน): ใบกำกับกระจาย **2 ชุด → 1 ชุด** · ใบที่ไม่ใช่ใบกำกับปนในชุด TIV_
_**1 → 0** (เคสกลับด้าน TaxInvoice VAT=0 ย้ายไป REC) · ใบสำคัญรับ (RV) ไม่ถูกยุบ_
_เข้า REC (เป็นกระดาษคนละอย่าง)_
_· **เส้น integration ครอบแล้ว (รอบ 114)**: ย้ายการออกเลขใน_
_`SyncTaxInvoiceAsync` ไป**หลังคำนวณยอด** (เดิมออกก่อนรู้ VAT) แล้วเรียก_
_`TaxInvoiceSeriesPolicy` ตัวเดียวกับเส้นในระบบผ่าน "roleProbe" (Document ชั่วคราว_
_ที่ถือชนิด+VAT) — ห้ามเขียนเงื่อนไข `VatAmount > 0` เองซ้ำ จะกลายเป็นสำเนาที่_
_drift · ย้ายได้ปลอดภัยเพราะตัวออกเลขนับจากเอกสารที่มีอยู่จริง เลขที่ขอไว้แล้ว_
_ไม่ได้ใช้ (เส้นทาง fail) ไม่เคยทำให้เกิดช่องว่างอยู่แล้ว · ประทับ_
_`IsTaxInvoiceByLaw` ลงเอกสารด้วยเหมือนเส้น approve;_
_Last verified against codebase: 2026-09-05 (รอบ 136 — **3 ข้อที่เจ้าของโปรเจกต์ตัดสิน**: ใบเสร็จ standalone_
_ที่ขายสินค้าคงคลัง → `CashSaleStockPolicy` ตั้งค่าได้ 3 ทาง (default ตัดสต๊อก+COGS = **เปลี่ยนพฤติกรรม**) ·_
_ใบวางบิลออกจากชุดลูกหนี้ทุกจุดผ่าน `Helpers/ArApScope` · บัญชีทิป POS ตั้งค่าได้ `PosTipPayableAccountCode` +_
_`TipAccountResolver` (default 21814→21819 ห้าม 216xx) ใช้ทั้ง POS/TipPayout)_
_ก่อนหน้า: 2026-09-05 (รอบ 135 — **ผลตรวจทีม ERP review A–F** (`ERP_REVIEW_2026-09-05.md`):_
_§3.2 ทางเข้า approve ที่ 4 (มือถือ) เข้า `ApproveDocumentAsync` · §2.4 ด่าน PO→PI เมื่อมี GRN ·_
_undue VAT reclass รวม CN/DN ลูก + stamp/unstamp `OutputVatDueAt` · §65ตรี(4) YTD ใช้_
_`DocumentStatusRules` · portal ลูกค้าเห็นเฉพาะเอกสารที่ออกแล้ว (`PortalService.PortalDocuments`) ·_
_รายงานสต๊อก Σ|OUT| + `StockMovementSign` (ADJUST คงเครื่องหมาย) · `StockTransferController`_
_มอบต่อ `IWarehouseService` · แดชบอร์ดตัด `IsClosingEntry` · aging กรอง CN/DN ตามฝั่ง)_
_ก่อนหน้า: 2026-09-04 (รอบ 134 — **สแกนซ้ำคัดลอกข้อมูลไม่ครบ**:_
_§2.2 เพิ่มเส้น `Cached` เต็มรูป (เกณฑ์เลือกต้นฉบับ · deny-list ของ `OcrScanSnapshot` ·_
_forceRescan) + ด่านตัวเลขสามช่องของ `OcrConfidenceGateway` — **เปลี่ยนพฤติกรรม**:_
_สแกนสำเนาได้ข้อมูลครบเท่าต้นฉบับ 49 ช่อง (เดิม 10) และ retry อ่านไฟล์ใหม่จริง)_
_ก่อนหน้า: 2026-09-04 (รอบ 126c — **LDG-P2-06 ปิดครบ**: สลิปออกจาก_
_static path สาธารณะ (PII) มาอยู่หลังด่าน + LINE แจ้งเจ้าของเมื่อมีจอง/สลิปใหม่ ·_
_แก้บันทึกที่ผิด: อีเมลแจ้งแขก/เจ้าของ **มีอยู่แล้วตั้งแต่รอบ 124** ที่ขาดคือ LINE เท่านั้น)_
_ก่อนหน้า: 2026-09-04 (รอบ 126b — **ปลายทางของโมดูลที่พัก**:_
_§6.5 เพิ่มตารางสิ่งที่ต่อสายรอบนี้ — ทางจ่ายออนไลน์ของลูกค้าปลายทาง (`PublicPaymentController`_
_ซึ่งเป็นทางเข้าเดียวที่ไม่ต้องล็อกอินและสร้าง `PaymentIntent` ได้) · ปฏิเสธสลิป ·_
_voucher หลักฐานการจอง (**ไม่ใช่เอกสารภาษี**) · ค่าเช็คอินก่อนเวลา/เช็คเอาต์ช้า ·_
_สูตร BalanceDue ยุบมาที่ `Helpers/LodgingAmounts`)_
_ก่อนหน้า: 2026-09-04 (รอบ 126 — **เก็บ doc ที่ค้างจาก Sprint 4/5**:_
_ขั้นอนุมัติเพิ่ม **ขั้น 0 "ด่านงวดปิด"** (`RequireOpenFiscalPeriodAsync` · C-T04 ·_
_9 จุดที่ลง JE จริง — **เปลี่ยนพฤติกรรม**: ลงย้อนเข้างวดที่ปิดแล้วจะได้ 400 พร้อม_
_ข้อความบอกทางแก้ 2 ทาง) · **ขั้น 5 retention** เปลี่ยนฐานจาก `DocumentDate + 5y`_
_เป็น **วันสิ้นรอบบัญชี + 5y** (C-T11 — สูตรเดิมสั้นไปเกือบ 12 เดือน) ·_
_ทั้งสองข้อถูก ship ใน `802f149` โดย **ไม่ได้อัปเดตไฟล์นี้** ซึ่งผิด hard requirement_
_ของ CLAUDE.md — บันทึกไว้เป็นบทเรียนใน §F แล้ว)_
_ก่อนหน้า: 2026-09-04 (รอบ 131 — **สกุลเงินของเอกสาร (A-D1)**:_
_ฟอร์ม `documents.html` ส่ง `currency`/`exchangeRate` ใน payload แล้ว (เดิมไม่เคยส่ง_
_⇒ ใบสกุลต่างประเทศจากหน้าจอลงบัญชีเป็นบาท rate 1 ทุกใบ) · ตอนแก้ไขแสดงค่าจริง +_
_ล็อกพร้อมเหตุผลผ่าน `_hydrateCurrencyReadonly` เพราะ `UpdateDocumentRequest`_
_ไม่รับสองช่องนี้ · ตรวจแล้ว `ToGlAmount` และ renderer ทั้งสองตัวถูกต้องอยู่ก่อนแล้ว)_
_ก่อนหน้า: 2026-09-03 (รอบ 130 — **ใบกำกับภาษีเต็มรูป
"แทน" ใบเสร็จ/ใบกำกับอย่างย่อ**: §2.4b ใหม่ — `IssueFullTaxInvoiceForReceiptAsync` +_
_`Helpers/FullTaxInvoiceReplacement` (pure) · ใบแทนใช้วันที่ใบเดิม ไม่ post JE ใหม่ ·_
_ประทับ `ReplacedByDocumentId` ตอน approve · `GenerateVatReport` +_
_`PullDocumentIntoReportAsync` กันใบที่ถูกแทนออก ⇒ ภาษีขายไม่ถูกนับสองครั้ง ·_
_void ใบแทน → ปลดตราประทับ คืนใบเดิมเข้ารายงาน)_
_ก่อนหน้า: 2026-09-03 (รอบ 127 — **หนึ่งความจริงของสต็อก**:_
_ขั้น 8 ของ Approve ไม่เขียนสต็อกเองอีกแล้ว — เดินผ่าน `IStockLedger.MoveAsync`_
_ซึ่งเขียน `WarehouseStock` (ความจริง) + `StockMovement` ที่มี `WarehouseId` เสมอ +_
_ปรับ `Product.CurrentStock` ให้เท่าผลรวมทุกคลัง · คลังของเอกสารแปลงจาก `doc.BranchId`_
_ผ่าน `ResolveWarehouseIdAsync` · ขา void group ต่อ (สินค้า, คลัง) เพื่อคืนของเข้าคลังเดิม ·_
_ผู้เขียนสต็อกเดิมทั้ง 9 ไฟล์ถูกย้ายในคอมมิตเดียว บังคับด้วย `tools/stock_writer_check.py`)_
_ก่อนหน้า: 2026-09-03 (รอบ 126 — **ปิดช่องเลี่ยงโควตา**:_
_`OriginModule` ย้ายออกจาก `CreateDocumentRequest` ไปเป็นพารามิเตอร์ของ_
_`IDocumentService.CreateDocumentAsync` ⇒ model binding เอื้อมไม่ถึงโดยโครงสร้าง ·_
_`LodgingService` ส่งค่าเป็นอาร์กิวเมนต์แทน (พฤติกรรมเดิมทุกประการ) ·_
_ล็อก §82/3 ผูก companyId แล้ว เลิกบล็อกข้ามบริษัทตอนกดปุ่มล้างภาษีซื้อหมดสิทธิ์)_
_ก่อนหน้า: 2026-09-03 (รอบ 125 — **โควตาเอกสาร + ส่วนเสริม**:_
_§7 เพิ่ม gate "โควตาเอกสารของแพ็กเกจ" — `DocumentQuotaPolicy` แยก "นับไหม" ออกจาก_
_"บล็อกได้ไหม" ⇒ ใบกำกับ/ใบเสร็จ/ใบลดหนี้ **ออกได้เสมอแม้โควตาเต็ม** (คิดเป็น_
_`documents.overage` แทน) ส่วนใบเสนอราคา/ใบสั่งซื้อบล็อกได้ · เอกสารจากโมดูลที่พัก_
_(`Document.OriginModule="Lodging"`) ไม่นับซ้ำเพราะมีมิเตอร์ `lodging.stay` แล้ว ·_
_รายละเอียดชั้น license/บิลอยู่ที่ ACCOUNT_STRUCTURE.md §6.1a-§6.1b)_
_ก่อนหน้า: 2026-09-03 (รอบ 124 — **โมดูลที่พัก**: §6.5 ใหม่ทั้งหมด —_
_เว็บไซต์ IndustryType.Hotel seed ที่พัก+ห้อง+ราคา+นโยบายให้จองได้ทันที · เงินทุกใบผ่าน_
_IDocumentService (มัดจำ §78/1 · เช็คเอาต์ DepositApplied* · ยกเลิก Refund/Realize) ·_
_ดู `LODGING_TAKETIME_ANALYSIS.md` สำหรับสิ่งที่ลอก/ไม่ลอก/ยังไม่ทำ)_
_ก่อนหน้า: 2026-09-02 (รอบ 123 — **เก็บงานค้างทั้งชุด**:_
_(1) **คำเตือนที่กด "รับทราบ" แล้วต้องเหลือร่องรอย** — `acknowledgeWarnings=true`_
_เขียน `Document.InternalNotes` (ไม่พิมพ์ลงกระดาษ — **ห้ามใช้ `Notes` เพราะ_
_`SanitizeNotesForPrint` พิมพ์ลงใบที่ส่งลูกค้า**) + `AuditLog`_
_`APPROVE-ACK-WARNINGS` และ echo กลับผ่าน `DocumentResponse.InternalNotes`_
_· พบว่า `Documents."InternalNotes"` **มีบน entity แต่ไม่เคยมี ADD COLUMN**_
_(ฐานที่สร้างก่อนเพิ่มพร็อพเพอร์ตี้จะไม่มีคอลัมน์) → เพิ่มใน migration_
_(2) **migration เลข placeholder เก่าบนเอกสาร Draft** — `INV-yyyyMMdd-XXXXXX`/_
_`TINV-…` จากสองทางที่เคย bypass เครื่องออกเลข → `DRAFT-{uuid}` **เฉพาะ_
_`Status = 0`** (ใบที่อนุมัติแล้วห้ามเปลี่ยนเลขย้อนหลัง §86/4) และต้องมี A-F_
_ในหกหลักท้าย (เลขที่ generator ออกเป็นตัวเลขล้วน — กันบริษัทที่ตั้งรูปแบบเลข_
_คล้ายกันไม่ให้โดนแตะ)_
_(3) **อัตรา/เพดานประกันสังคมเป็น "ช่วงเดือน" ไม่ใช่ทั้งปี** — ประกาศลดอัตราของ_
_ไทยออกเป็นช่วงเดือนเสมอ (1% พ.ค.–ก.ค. 2563 · 2.5% ม.ค.–ก.พ. 2565) แต่_
_`SsoYearConfig` เก็บได้ปีละค่าเดียว ⇒ ผู้ใช้ต้องแก้แถวเดิมกลางปี ซึ่ง**เปลี่ยน_
_อัตราของเดือนที่ยื่น สปส. ไปแล้วย้อนหลังด้วย** → เพิ่ม_
_`EffectiveFromMonth`/`EffectiveToMonth` + กติกา **"ช่วงแคบกว่าชนะ"**_
_(`SsoRateSchedule.SpanWidth`) ⇒ ตั้ง "ทั้งปี 5% + ลด 1% เฉพาะ 5–7" ได้โดยไม่ต้อง_
_ตัดปีเป็นสามท่อน และผลไม่ขึ้นกับลำดับแถว · `GetSsoParamsAsync(companyId, year,_
_**month**)` ไม่มี default ให้เดือน (เส้นที่ "ไม่รู้เดือน" จะคิดอัตราผิดเงียบ ๆ)_
_· ปฏิเสธเฉพาะช่วงที่ทับกันแบบ**กว้างเท่ากัน** (`RangesAmbiguous`)_
_(4) **PDPA**: `ApplyErasureAsync` ถอด `UserExternalLogins` + `AuthProvider`/_
_`AuthProviderId` + refresh token (บัญชีที่ anonymise แล้วแต่ยังผูก Google/LINE_
_กด SSO ก็เข้าได้ตามปกติ — เส้น SSO ค้นด้วย `ProviderUserId` ไม่ได้ดูอีเมล =_
_"ทางเข้าที่ยังเปิดอยู่ = การลบที่ยังไม่จบ") · `GenerateAccessReportAsync`_
_คืนบัญชีภายนอกที่ผูกไว้ (ม.30)_
_(5) `ExternalLoginResponse.LinkedAt` เคยแมป `ConfirmedAt` (ยืมช่องผิดความหมาย —_
_การผูกที่ยังไม่ยืนยัน = "ไม่มีวันที่ผูก") → แยกเป็น `LinkedAt`(CreatedAt) +_
_`ConfirmedAt` + `LinkedFromIp` (คอลัมน์ที่เก็บมาตลอดแต่ไม่มีใครอ่าน)_
_(6) `SsoLoginRequest.InvitationToken` ถูกใช้บนเส้น **บัญชีเดิม** ด้วย (เดิม_
_ใช้เฉพาะตอนสมัครใหม่ ⇒ คนที่มีบัญชีแล้วถูกเชิญ กดลิงก์แล้วเลือก Google =_
_คำเชิญค้าง Pending ตลอดไปโดยไม่มีอะไรบอก) + กันเพิ่ม `CompanyUser` ซ้ำ_
_(7) `js/sso.js`: state ใช้ `crypto.getRandomValues` (ไม่ใช่ `Math.random`) ·_
_เลิกเดา provider เป็น 'Line' เมื่ออ่าน sessionStorage ไม่ได้ (ข้อความจะโทษ_
_ผู้ให้บริการผิดตัว) · `sessionStorage` เขียนไม่ได้ → **หยุดพร้อมบอกทางแก้**_
_แทนพาเดินครบรอบไปเจอ "สถานะไม่ตรงกัน" · `Sso.displayName` เป็นตัวตัดสินชื่อ_
_ที่โชว์ ให้ตรงกับ `SsoIdentityPolicy.DisplayName` ฝั่งเซิร์ฟเวอร์_
_(8) ข้อความ Facebook ที่โทษ "ตัวบล็อกโฆษณา" อีก 2 จุด (สาเหตุจริงคือ CSP ของ_
_ระบบเอง ซึ่งผู้ใช้แก้ไม่ได้) · `SsoWageBase.IsConsistent` ใช้ `PairTolerance`_
_ตัวเดียวกับ `Normalize` (เกณฑ์ต่างกัน = "ผ่านตอนเขียน ตกตอนยื่น") ·_
_`ReadSsoSignupTicket` ตรึง `ValidAlgorithms` · ด่าน "provider เปิดใช้หรือยัง"_
_ยุบเป็น `EnsureProviderEnabled` ตัวเดียว (สองสำเนาเดิมมี `_ => LineEnabled`_
_ที่จะปล่อย provider ตัวที่สี่ผ่านเงียบ ๆ));_
_ก่อนหน้า 2026-09-01 (รอบ 122 — **"ใบแจ้งหนี้ที่เป็น_
_ใบกำกับภาษีในตัว ใช้เลข INV หรือ TIV?"**: ยืนยันกติกาเดิมถูกแล้ว — ใบรวมคือ_
_`TaxInvoice + CombinedInvoiceTaxInvoice` (เลข TIV · หัว "ใบแจ้งหนี้/ใบกำกับ_
_ภาษี" · e-Tax T02) ส่วน `DocumentType.Invoice` = INV เสมอ (เล่ม TIV มีเฉพาะ_
_ใบที่เป็นใบกำกับ ณ วินาทีที่ออกเลข — Invoice บริการยังไม่ถึง tax point §78/1_
_ให้ TIV จะเกิดเล่มขาดช่วงเทียม). ปิดช่อง 4 เรื่อง: (1) warning §86 ครอบ_
_Invoice ขายสินค้า+VAT ที่เข้า ภ.พ.30 ทันทีแต่กระดาษไม่ใช่ใบกำกับ (ตัวตัดสิน_
_"มีสินค้าไหม" ยุบเป็น `InvoiceHasTrackedGoodsAsync` ใช้ร่วมกับ AutoPost_
_21911/21913) (2) บล็อกอนุมัติ Invoice ที่หัวถูก override เป็นใบกำกับ_
_(`TaxInvoiceSeriesPolicy.IsTaxTitleOnPlainInvoice` — จงใจไม่ครอบ CN/DN ซึ่ง_
_§86/9-10 ถือเป็นใบกำกับอยู่แล้ว) (3) API: Invoice+combined → ยกชนิดเป็น_
_TaxInvoice ให้ (ตรง semantics ของ UI) ชนิดอื่น → 400 · update → error แทน_
_ดรอปธงเงียบ (4) เลิก hardcode เลข "INV-{GUID}"/"TINV-{GUID}" ใน_
_CrossTenantWorkflowService/TimeBillingService → DRAFT- ให้ generator ออกเลข_
_จริงตอนอนุมัติ + ยุบแผนที่ TypeCode↔ชื่อ e-Tax 4 สำเนาเป็น_
_`Helpers/EtaxDocumentTypeMap` ตัวเดียว);_
_ก่อนหน้า 2026-09-01 (รอบ 121 — **ยอดประกันสังคมฝั่ง_
_นายจ้างจากเส้นนำเข้า (TakeTime) ไม่เคยถูกตรวจ**: import ตรวจแค่ฝั่งลูกจ้าง_
_(net = gross − หัก) ส่วนนายจ้างคัดมาดิบ ๆ ⇒ 4,403 แทน 4,381 ติดมาตั้งแต่_
_วินาทีแรก · ยุบตรรกะซ่อมเป็น `SsoWageBase.Normalize` ตัวเดียว เรียกจาก 3 จุด_
_(import / จ่าย / กลับรายการจ่าย) + warning และข้อความตอบกลับทุกครั้งที่ปรับ);_
_ก่อนหน้า 2026-09-01 (รอบ 112 — **"เดี๋ยว TIV เดี๋ยว REC":_
_นโยบายการออกใบกำกับ/ใบเสร็จระดับบริษัท + ปิดกับดัก VAT ซ้ำ + ยุบเครื่องออกเลข**_
_(ผู้ใช้ถามว่าใบกำกับควรรวมเป็น TIV อย่างเดียวไหม และบริษัทที่อยากออกใบกำกับกับ_
_ใบเสร็จแยกกันต้องตั้งอะไร — ตั้งทีมสำรวจ 3 ทีม แล้วยืนยันทุกข้อกับโค้ดเองก่อนแก้):_
_· **สาเหตุราก** — เลขที่เอกสารผูกกับ `DocumentType` แต่หัวกระดาษผูกกับบทบาททาง_
_กฎหมายที่คำนวณจากธงคนละชุด (`ComputeDocumentTitle`) ⇒ ใบที่หัวพิมพ์ "ใบกำกับภาษี/_
_ใบเสร็จรับเงิน" เหมือนกันเป๊ะ เกิดได้ทั้งจาก TaxInvoice (TIV-, ทาง_
_`IssuedAsCashReceipt`/`ServedAsReceipt`) และ Receipt (REC-, ทาง standalone มี VAT /_
_ใบเสร็จ carryVat §78/1) · และมีเคสกลับด้าน: TaxInvoice ที่ VAT=0 พิมพ์หัว_
_"ใบเสร็จรับเงิน" บนเลข TIV- · จุดที่เห็นปนชัดสุดคือรายงานภาษีขายที่ปล่อยเลขดิบ_
_ลงคอลัมน์เดียว. **VAT ไม่เคยนับซ้ำ** (ภ.พ.30 dedup 4 ชั้น) — ที่ขาดคือนโยบาย_
_· **`CompanySettings.ReceiptIssueMode`** (0=ใบเดียวจบ 1=แยกเสมอ 2=ค้าปลีก;_
_default 0 = พฤติกรรมเดิมทุกข้อ มีเทสต์ล็อก) + `Helpers/ReceiptIssuePolicy` เป็น_
_กติกาที่เดียวให้ 5 จุดเรียก. โหมดแยกใบ: ปิด `ServedAsReceipt` ทั้ง 2 mirror ·_
_บังคับออกใบเสร็จทุกครั้งที่รับเงิน (ปิด `combinedSelfReceipt` ด้วย) · ซ่อนกระดาษ_
_`tax_receipt`/`invoice_tax_paid` พร้อมบอกเหตุผล · ล็อกช่อง "ออกใบเสร็จ" ในโมดัล_
_รับชำระ (เดิม smart default ปลดติ๊กให้เองพอดีในเคสที่บริษัทกลุ่มนี้ต้องการแยกที่สุด)_
_· **🔴 กับดัก VAT ซ้ำ** — ใบเสร็จมี VAT ที่ไม่ผูกใบต้นทาง ให้ลูกค้าที่มีใบค้างชำระ_
_⇒ `!RelatedDocumentId.HasValue` ทำให้ ภ.พ.30 นับรอบสอง + JE เดินเส้น standalone_
_(รายได้เบิ้ล, AR ไม่ถูกล้าง) เดิม**ไม่มี guard ฝั่ง server เลย** → block ตอน approve_
_พร้อมเลขใบที่ควรผูกและทางแก้_
_· **🔴 e-Tax gate ตัน** — เดิมบังคับ `Status == Approved` เป๊ะ ซึ่งเป็นสถานะ_
_ชั่วคราวมาก (ส่งอีเมล/รับเงินแล้วเดินไป Sent/PartiallyPaid/Paid) ⇒ ใบกำกับที่รับ_
_เงินแล้วสร้าง e-Tax ไม่ได้เลย และใบเสร็จ settlement (เกิดมาเป็น Paid) ไม่มีวันผ่าน_
_→ กันเฉพาะ WaitingApproval/Rejected + เพิ่มด่าน: ใบเสร็จที่รับชำระใบกำกับ ห้าม_
_export เป็น T03 (จะเป็นการแจ้งใบกำกับซ้ำใบที่สองจากการขายครั้งเดียว)_
_· **ยุบเครื่องออกเลขเหลือตัวเดียว** — `SettingsService.GetNextNumberAsync` เป็น_
_เครื่องที่สองที่เดินคู่ขนาน (เส้น integration 6 จุดใช้ตัวนี้) และพลาด 4 อย่าง:_
_ไม่มี advisory lock · ไม่ `IgnoreQueryFilters()` (ใบ soft-delete มองไม่เห็น →_
_ออกเลขทับ) · lexicographic max · ตาราง prefix ของตัวเองขาด `GoodsReceiptNote`_
_(ตก "DOC-") — และเมื่อมีแถว NumberSeries มันสร้างเลข**คนละทรง** (รายเดือน นับเอง)_
_→ delegate ไป `DocumentNumberGenerator` ทั้งหมด_
_· **NumberSeries: เลือก "ต่อสาย" ไม่ใช่ลบ** — entity/service/controller มีครบแต่_
_ไม่มี UI สร้าง/แก้และไม่มี seed ⇒ ตารางว่างตลอด. คงไว้เฉพาะส่วนที่ผู้ใช้ต้องการ_
_จริงคือ **ตัวย่อ** (`ResolvePrefixAsync` อ่าน `NumberSeries.Prefix`; รูปแบบเลขยังเป็น_
_`{PREFIX}-{yyyyMMdd}-{NNNN}` เหมือนกันทั้งระบบ) + แท็บ "ลำดับเลขที่" แก้ตัวย่อได้จริง_
_+ ด่าน `^[A-Z0-9]{1,8}$` ฝั่ง server_
_· `ImportExportService` อ่านเลขรัน JE จากแถว NumberSeries ที่ **ไม่มีใครสร้างเลย**_
_⇒ ทุก JE ที่ import ได้ `JV-000001` ซ้ำกันหมดตั้งแต่วันแรก → ใช้_
_`JournalEntryBuilder.NextJournalNumberAsync` ตัวเดียวของระบบ_
_· รายงานภาษีขายเพิ่ม `DocumentKindLabel` ต่อบรรทัด (คำนวณตอนอ่านจาก resolver หัว_
_เอกสารตัวเดียวกับกระดาษ — **ไม่แตะ `Description` ที่ไฟล์ยื่นใช้** และไม่ต้อง migration)_
_· จุดเล็ก: settings เพิ่มช่อง title override 3 คีย์ที่ server รองรับมาตลอด ·_
_`layout.js docHeaderLabel` fallback สลับลำดับคำของ `issuedAsCashReceipt`_
_(ต้องเป็น "ใบเสร็จรับเงิน/ใบกำกับภาษี" ตาม pairing e-Tax T03)_
_· **backlog ที่จดไว้ไม่แก้ครึ่ง ๆ**: (ก) `IntegrationService` ทั้ง 6 จุดยังไม่เปิด_
_transaction ⇒ `pg_advisory_xact_lock` ถูกปล่อยทันที ตัวกันชั้นสุดท้ายคือ unique_
_index `(CompanyId, DocumentNumber)` ที่ทำให้ชนแล้ว**error ดัง** ไม่ใช่เลขซ้ำเงียบ ๆ_
_(ข) e-Tax **T01** (ใบเสร็จ standalone ไม่มี VAT) ยังไม่ถูก expose — ต้องมี root_
_schema `Receipt_CII` ของตัวเอง จึงไม่ merge ครึ่ง ๆ (ค) เลขเอกสารยังไม่แยกชุดตาม_
_สาขา (ง) `EnforceFullTaxInvoiceFields` ไม่มี UI ตั้ง);_
_Last verified against codebase: 2026-08-31 (รอบ 111 — **แก้ยอดรอบเงินเดือนที่_
_จ่ายไปแล้ว: "กลับรายการจ่าย" (Paid → Approved)**:_
_อาการที่ผู้ใช้เจอ — เปิดรอบ "เงินเดือน สิงหาคม 2569" ที่จ่ายแล้ว ตารางรายคน_
_ไม่มีปุ่ม "✏️ แก้ยอด" ช่องแหล่งจ่ายกลายเป็นข้อความ และ**ไม่มีอะไรบอกว่าทำไม_
_หรือต้องทำอะไรต่อ** (defect class "ห้าม silent no-op") ทางเดียวที่มีคือ_
_"ยกเลิกรอบ" ซึ่ง `Voided` เป็นสถานะปลายทาง ⇒ ต้องสร้างรอบใหม่ทั้งรอบ_
_· **กติกาย้ายมาที่เดียว** `Helpers/PayrollRunEditPolicy` (`CanEditAmounts` /_
_`CanReopen`) — เดิมเงื่อนไข `Status != Calculated && != Approved` ถูกเขียนซ้ำ_
_3 ชุด (UpdatePayrollDetailAsync · SetEmployeePaymentAccountAsync · `payEditable`_
_ใน payroll.html). ทุกกรณีที่ตอบ "ไม่ได้" ต้องคืน**เหตุผลพร้อมทางแก้** เสมอ_
_· `PayrollRunResponse` เพิ่ม `CanEditAmounts` / `EditLockReason` / `CanReopen` /_
_`ReopenBlockReason` / `ReopenedAt|By|Reason` — เซิร์ฟเวอร์ตัดสิน หน้าเว็บแสดง_
_อย่างเดียว (กลไกเดียวกับ `complianceIssues` และ `DocumentTitle`); ฝั่ง JS ถือ_
_`undefined` = "เซิร์ฟเวอร์รุ่นเก่ายังไม่ส่ง" → ตกกลับกติกาเดิม ไม่ใช่ล็อกหน้าจอ_
_· **`ReopenPaidRunAsync`** (POST `/payroll/runs/{id}/reopen`, เหตุผลบังคับ ≥5 ตัว):_
_ล็อกแถว `FOR UPDATE` → กลับ JE ที่ลงตอนจ่าย **ลงวันเดียวกับ PayDate** (ไม่ใช่_
_วันนี้ — เพราะจะโพสต์ใหม่เข้างวดเดิมหลังแก้ ถ้ากลับรายการไปโผล่งวดอื่น งวดเดิม_
_เหลือรายการค้างและงวดใหม่มีเกิน; ต่างจาก Void ที่เหตุการณ์ถูกยกเลิก "วันนี้"_
_จริง ๆ) → คืนเงินทดรอง + ล้าง `AdvanceRecovered` → ตัด `JournalEntryId` →_
_`Status = Approved` + `AuditLog(Update, PayrollRun)` เก็บ JE เดิม/เหตุผล_
_· **ด่านก่อนกลับรายการ**: นำส่ง สปส. แล้ว (`SsoSettledAt`) = บล็อก (JE ก้อนที่_
_สองออกไปแล้ว + มีเลขรับบนกระดาษ) · งวดบัญชีของ PayDate ปิด = บล็อกพร้อมชี้ที่_
_เปิดงวด · สถานะอื่นบอกเหตุผลของตัวเอง (ยังไม่จ่าย = แก้ได้เลย / Voided = สร้าง_
_รอบใหม่)_
_· **คืนเงินทดรองใช้ตัวเดียวกับ Void** — แยกเป็น `RestoreSalaryAdvancesAsync`_
_(FIFO ตามวันขอเบิก) แทนการคัดลอกไปวางชุดที่สอง; simulation 4 เคส (หักบางส่วน/_
_หักหมด/ข้ามสองก้อน/ขอเกินยอดค้าง) ยืนยัน pay→reopen→pay คืนสถานะเป๊ะและจ่าย_
_รอบสองได้ยอดเท่าเดิม (การคืนถูก bound ด้วย `cleared` โดยโครงสร้าง จึงคืนเกิน_
_ไม่ได้ — เป็น**คุณสมบัติ** ไม่ใช่บั๊กที่เคยเกิด)_
_· **เอกสารที่แนบไว้ไม่ถูกทิ้ง**: `AutoGenerateFilingsAsync` เดิม "ชื่อซ้ำ → ข้าม"_
_ซึ่งถูกตอนจ่ายได้ครั้งเดียว แต่พอจ่ายซ้ำได้ ⇒ ภ.ง.ด.1/สปส.1-10/สลิป จะค้างเป็น_
_ตัวเลข**ก่อนแก้ตลอดไป**. เปลี่ยนเป็น **เปลี่ยนชื่อฉบับเก่า** เป็น_
_"… (ฉบับก่อนแก้ไข yyyyMMdd-HHmm)" แล้วให้ฉบับใหม่ใช้ชื่อเดิม — **ห้ามลบ**_
_เพราะ `FileAttachmentService.DeleteAsync` ลบไฟล์จริงบนดิสก์ และฉบับเก่าอาจถูก_
_ยื่น/ส่งพนักงานไปแล้ว ต้องเก็บ 5 ปี (พ.ร.บ.การบัญชี ม.10) · ระหว่างที่ยังไม่กด_
_จ่ายใหม่ หน้ารอบขึ้นแบนเนอร์ว่าไฟล์ที่แนบยังเป็นฉบับก่อนแก้ (กติกา "ต้องดัง_
_บนตัวข้อมูลที่ผู้ใช้เปิดดู" — log เซิร์ฟเวอร์ไม่ใช่ช่องทางแจ้งผู้ใช้)_
_· **ด่านสิทธิ์ที่ขาด**: `POST runs/{id}/void` ไม่มี `CheckPayrollAccessAsync` เลย_
_ทั้งที่ `GetRun`/`UpdateDetail` มี ⇒ ใครก็ได้ในบริษัทกลับ JE + คืนเงินทดรองได้_
_(บทเรียน "ด่านที่อ่อนกว่าแต่ทำได้มากกว่า") — ใส่ให้ทั้ง void และ reopen แล้ว._
_**ยังเหลือ**: `calculate` / `approve` / `pay` / `settle-sso` ยังไม่มีด่าน — จงใจ_
_ไม่แตะในรอบนี้เพราะเส้น integration (`runs/import` → approve → pay) ใช้ key ของ_
_เครื่องซึ่งอาจไม่ผ่าน sensitivity rule แล้วพังของที่ใช้งานอยู่ ต้องยืนยันกับ_
_เจ้าของระบบก่อน;_
_รอบ 110 — **รอบเสถียรภาพ: 4 ทีม_
_ล่า defect class ที่สรุปจากปัญหาที่ผู้ใช้รายงาน + ผมยืนยันเองทุกข้อก่อนแก้**_
_(5 commit: c1b104a · 50dd94d · 52098cb · f415340 · f067132):_
_(1) **สินเชื่อ: จ่ายหนี้แล้วยอดไม่ลดเลยตั้งแต่เขียนมา** — payload หน้า loans_
_กับ MakeLoanPaymentRequest ชื่อไม่ตรงกันสักตัว ⇒ binder ได้ 0 ทุกช่อง. สัญญา_
_ใหม่ตรงกับฟอร์มจริง + แตกต้น/ดอกจากตารางผ่อนเมื่อไม่ระบุ + JE ผ่าน_
_JournalEntryBuilder (ปิดผู้ออกเลขเถื่อน LP-/LD- อีก 2 ตัว) + LateFee มีขา Dr_
_แล้ว + ผังบัญชีไม่ครบ = throw ไม่ใช่โพสต์ JE ขาเดียวเงียบ ๆ_
_(2) **เลข 0 ถูก `|| null` กลืน 20 จุด/13 ไฟล์** (ตระกูลเดียวกับเครดิต 0 วัน) —_
_เพิ่ม `Layout.numOrNull/intOrNull` เป็น resolver กลาง; ตัวแรง: approval_
_threshold 0 (control bypass เงียบ) · vatRate 0% ถูกทับเป็น 7 ทั้ง save และ_
_โชว์ · VAT 0 ในหน้า review OCR · เพดานยกวันลา 0 กลายเป็นไม่จำกัด (พร้อมยุบ_
_ความหมาย 0 ของ PayrollService ให้ตรง LeaveController)_
_(3) **ล้างค่ากลับเป็นว่างไม่ได้ทั้งระบบ** — วาง sentinel ครบตระกูล: string=""_
_· Guid=Guid.Empty · int=-1 · DateTime=MinValue; เอกสาร (วันครบกำหนด/เครดิต/_
_โครงการ/แหล่งเงิน/หมวดค่าใช้จ่าย) · โครงการ (StartDate เดิมไม่อยู่ในสัญญา_
_เลย = silent no-op ทั้งที่ฟอร์มติด *) · รายการประจำ (StartDate + recompute_
_นัดรัน + เลิก hardcode dueDays:30) · สินทรัพย์ถาวร · พนักงาน (ปลดหัวหน้า) —_
_ฝั่งฟอร์มส่ง sentinel เฉพาะโหมดแก้ไข + ช่องแสดงอยู่จริง (กันปลดค่าของช่องที่_
_ถูกซ่อนตามชนิดเอกสาร)_
_(4) **field คู่ derive ขัดกันเอง 5 คู่** — WHT cert ฐาน×อัตรา (touched guard_
_ตายถาวรใน openEdit) · วันที่เอกสาร↔ครบกำหนด · เครดิต↔ข้อความเงื่อนไขบน_
_กระดาษ · มูลค่าสัญญา TFRS15↔allocation ของ PO (แก้สัญญาแล้วเงินหายจากรายงาน_
_deferred) · ดอกเบี้ยสินเชื่อ↔ตารางผ่อน_
_(5) **keyword ไทยล้วน/over-match 12 จุด** (ตระกูล "Diesel") — simplified tax_
_invoice · vat inclusive · CUSTOMER COPY ไม่ใช่ป้ายผู้ซื้อ · DEPOSIT exclusion_
_(เงินประกัน≠มัดจำ) + เงินจอง/PREPAYMENT · ตราจ่ายแล้ว EN · สลิปธนาคาร EN ·_
_ใบส่งสินค้า/delivery order · PROFORMA≠invoice · copyright≠สำเนา · 50 ทวิ EN ·_
_\bwht\b · สถานะ DBD EN — ทุกลิสต์มี simulation ดึงจากไฟล์จริง 17 เคสรวม_
_negative (UNPAID/Amount Paid: 0.00/security deposit ต้องไม่โดน)_
_· control ที่ตรวจแล้ว**ผ่าน**: audit hash chain (มี write→verify) · AiBudgetGuard_
_(ต่อสาย + fallback local) · ChatRateLimiter (DB-upsert ข้าม instance) — จดไว้:_
_ForceProviderCall ข้ามด่านงบจาก 3 จุด bulk โดยเจตนา_
_· backlog ที่จดไว้ไม่แก้ครึ่ง ๆ: normalize ไม่เท่ากันระหว่าง validator/inferrer_
_(SquashForKeywordMatch ฝั่งเดียว) · marker ชนิดเอกสารกระจาย 3 ที่ ควรรวมเป็น_
_bilingual table เดียว · ContainsAll("ณ ที่จ่าย","รับรอง") ควรเป็น window ·_
_SupplierTaxInvoiceDate เปลี่ยนแล้วไม่ revalidate งวดเคลมที่ตรึงไว้ (§82/3));_
_รอบ 109 — **เตือน DSR หลังผิดกฎหมาย_
_ไปแล้ว**: `ListOverdueAsync` คืนเฉพาะคำขอที่ `DueBy < now` — กว่าจะขึ้นหน้าจอ_
_บริษัทก็เลยกรอบ 30 วันตาม ม.32 ไปเรียบร้อยแล้ว ⇒ เตือนไว้เพื่อ "รู้ว่าผิด"_
_ไม่ใช่ "กันไม่ให้ผิด". เส้นแจ้งเหตุข้อมูลรั่วในไฟล์เดียวกัน_
_(`ListBreachAlertsAsync`) เตือนล่วงหน้า 24 ชม. ก่อนครบ 72 ชม. อยู่แล้ว —_
_control สองตัวในระบบเดียวกันแต่คนละพฤติกรรม_
_→ เพิ่มหน้าต่างเตือนล่วงหน้า `DsrWarnDaysAhead = 7` วัน · ข้อความสรุปแยก_
_"เลยกำหนด N" กับ "ใกล้ครบกำหนด M" · หน้าเว็บแยกสี ⛔ แดง vs ⏳ ส้ม เพื่อไม่ให้_
_ใบที่ยังทันดูเหมือนผิดกฎหมายไปแล้ว);_
_รอบ 108 — **ล็อกที่กันข้ามเครื่อง_
_ไม่ได้จริง + control เข้ารหัสที่ไม่มีเทสต์**:_
_(1) `pg_advisory_xact_lock` กันการแย่งทรัพยากรได้ก็ต่อเมื่อทุก instance คำนวณ_
_คีย์ได้ค่าเดียวกัน — แต่ **7 จุด** ใช้ `HashCode.Combine(...)` ซึ่ง .NET สุ่ม_
_seed ใหม่ทุก process ⇒ สอง instance ล็อกคนละคีย์ = ไม่กันกันเลย. รอบก่อนแก้ไป_
_แล้ว 1 จุด (`JournalEntryBuilder`) แต่เหลือ: **เลขเอกสาร** (`DocumentNumber_
_Generator` — §86/4 บังคับไม่ซ้ำ ไม่ขาดช่วง) · **ตัวออกเลข JE ตัวที่ 5 ใน_
_`DocumentService`** ที่ยังไม่ถูกยุบทิ้งทั้งที่เขียนลง number space เดียวกัน_
_(และเรียงด้วย string ⇒ พังที่เลข 5 หลัก) · เลขอ้างอิงการเงิน · จับคู่รายการ_
_ธนาคาร 2 จุด · ปรับสต็อกรายสินค้า_
_→ `Helpers/AdvisoryLockKey.For(companyId, scope, part)` (FNV-1a 64-bit) เป็น_
_เจ้าของสูตรที่เดียว + scope const ประกาศรวมไว้กันพิมพ์ผิดจนกลายเป็นคนละล็อก ·_
_`GetNextJournalEntryNumberAsync` ยุบเหลือ delegate ไป `JournalEntryBuilder`_
_· checker ตัวที่ 19 `tools/advisory_lock_key_check.py` (ผ่าน negative test:_
_ใส่บั๊กกลับ → จับได้ 1 จุด · ถอดออก → 0 จุด)_
_· `AdvisoryLockKeyTests` **hard-code ค่าคงที่จริง** เพราะเทสต์แบบ "เรียกสองครั้ง_
_ได้เท่ากัน" จับบั๊กนี้ไม่ได้ (HashCode.Combine ก็ผ่าน)_
_(2) ชั้น encrypt-at-rest ของ PII (ม.26) ไม่มีเทสต์เลยสักตัว ทั้งที่กฎเหล็ก #4 G_
_ระบุว่า "control ที่ไม่มีเทสต์ยืนยัน = ไม่มี control" → `PiiEncryptionRoundTrip_
_Tests` ล็อก: round-trip · **idempotent** (EF save ซ้ำห้ามเข้ารหัสซ้อน) ·_
_ciphertext ต่างกันทุกครั้ง (nonce สุ่ม) · คีย์ผิด/ข้อมูลถูกแก้ต้องถอดไม่ได้_
_(GCM tag) · plaintext เก่าต้องไม่ถูกเข้าใจผิดว่าเป็น ciphertext · **ความยาว_
_ต้องพอดี `HasMaxLength(200)`** — ข้อสุดท้ายมีระยะเผื่อ **ศูนย์** ที่ปลายล่าง:_
_ค่า 1 ไบต์ให้ ciphertext ยาว 40 ตัวอักษรพอดีเท่าเกณฑ์ `IsEncrypted` (≥40));_
_รอบ 107 — **รอบตรวจ PDPA/สิทธิ์:_
_เส้น DSR เปิดโล่งสามชั้น**:_
_(1) **ข้ามบริษัทได้** — `PdpaService` query `_db.Users.FirstOrDefaultAsync(u =>_
_u.Id == userId)` **เปล่า ๆ** ทั้งสามเส้น (`GenerateAccessReportAsync` อ่าน ·_
_`ApplyRectificationAsync` แก้ · `ApplyErasureAsync` **anonymize ถาวร**)._
_`Users` ไม่ใช่ tenant entity (ผูกผ่าน `CompanyUser`) จึงไม่มี global query filter_
_มาช่วย ⇒ สมาชิกบริษัท A ใส่ GUID ของผู้ใช้บริษัทไหนก็ได้ในระบบ แล้วอ่าน/แก้/ลบ_
_ได้จริง (ผิดทั้งกฎ M "ทุก query ต้องมี CompanyId" และ PDPA ม.37)_
_→ helper กลาง `IsCompanyMemberAsync` + ข้อความปฏิเสธชุดเดียว `NotInCompanyMessage`_
_ที่ไม่บอกว่า "มี user นี้อยู่จริงไหม" (กัน enumeration ข้ามบริษัท) · เส้นที่แก้/ลบ_
_**throw** ไม่ใช่ข้ามเงียบ ๆ (ห้าม silent no-op)_
_(2) **ไม่มีด่านสิทธิ์เลย** — `PdpaController` มีแค่ `[Authorize]` ระดับคลาส ⇒_
_สมาชิกคนไหนของบริษัทก็เรียก `dsr/access` ดัมพ์โปรไฟล์ + เอกสาร + การชำระเงิน +_
_**ประวัติการเข้าถึง 1 ปี** ของใครก็ได้ · `dsr/portability` โหลดเป็นไฟล์ ·_
_`dsr/erase` **anonymize ถาวร** — ทั้งที่หน้าเงินเดือนในเรพเดียวกันยังต้องมี_
_`Pii.View` ถึงจะเห็นเลขบัตรแบบไม่ mask. **ด่านที่อ่อนกว่าแต่คืนข้อมูลมากกว่า_
_คือช่องที่ใหญ่ที่สุด** → `RequireDpoAsync` (perm:Pii.View, Owner/SystemAdmin ผ่าน)_
_บน 12 endpoint: assign/complete/overdue · erasure-impact · dsr ทั้ง 4 ·_
_ropa upsert · consent withdraw · breach update/alerts. เปิดไว้ตามเดิมเฉพาะ_
_"ยื่นคำขอ" · "ให้ความยินยอม" · "แจ้งเหตุข้อมูลรั่ว" · อ่าน RoPA (ไม่มี PII)_
_(3) **`LogPiiAccessAsync` ไม่มี call site เลยทั้งเรพ** — ทั้งที่ doc-comment ของ_
_`PermissionKeys.PiiView` เขียนไว้ว่า "ทุกครั้งที่ field ถูกอ่านแบบ raw ต้อง log_
_ลง PiiAccessLog (ม.37(4))" ⇒ ตารางว่างเปล่าตลอด = **ไม่มี control จริง**_
_(ซ้ำรอย `CanShareMoneyAggregates`) → ต่อสายที่ DSR ทั้ง 4 เส้น + ที่_
_`PayrollController.CanViewPiiAsync` (log เฉพาะตอน "ได้ดูจริง" — คนที่ถูก mask_
_ไม่ได้เข้าถึง PII จึงไม่บันทึก)_
_**ทำไมรอบนี้ไม่เพิ่ม checker**: ตัวแยกบั๊กนี้คือ "id มาจาก request ของผู้ใช้ หรือ_
_มาจากแถวที่ scope ด้วย company แล้ว" = taint ไม่ใช่รูปทรงของโค้ด — `_db.Users`_
_มี 85 จุดในเรพและส่วนใหญ่ถูกต้อง checker แบบ regex จะฟ้องผิดเป็นสิบ ๆ จุด_
_(กฎของเรพเอง: checker ที่ฟ้องผิด = checker ที่พังแล้ว) กลไกที่ใช้แทนคือ helper_
_กลางตัวเดียวให้ทั้งสามเส้นเรียก + บันทึกเป็น defect class ใน CLAUDE.md);_
_รอบ 106 — **"สร้างเอกสารได้ แต่ลง_
_บัญชีไม่ได้" ถูกรายงานว่า Success**: เอกสารจาก integration ถูกสร้างเป็น_
_`Status = Approved` เสมอ ⇒ `TaxService` นับเข้า ภ.พ.30 ทันที แต่การลงบัญชีมีทาง_
_ออก null ถึง **7 ทาง** (ไม่พบผังลูกหนี้/รายได้ · ไม่พบผังเจ้าหนี้/ค่าใช้จ่าย ·_
_ไม่พบบัญชีภาษีซื้อ · ชนิดเอกสารไม่รู้จัก · สร้างบรรทัดไม่ได้เลย · Dr≠Cr · ด่าน_
_โครงสร้างไม่ผ่าน) และ**ทุกทางเขียนแค่ `LogWarning` แล้วเดินต่อ** ⇒ คู่ค้าได้_
_`success: true "Invoice created"` · sync log ขึ้น `Success` · เอกสารอยู่ในระบบ_
_แต่ **ไม่มีรายการบัญชีเลย** ⇒ **ภ.พ.30 ไม่ตรง GL ถาวร** โดยร่องรอยเดียวคือ log_
_ที่ไม่มีใครเปิดอ่าน (รอบ 102 แก้ *สาเหตุ* หนึ่งตัวคือ `IncludeVat` ไปแล้ว — แต่_
_*โหมดล้มเหลวเงียบ* ยังอยู่ครบสำหรับอีก 6 สาเหตุ)_
_→ แปลงความเงียบเป็นเสียง **ที่เดียว** (`PostMappingJournalAsync`) ให้ 6 จุดเรียก_
_ใช้ร่วมกัน ไม่ให้ drift: (1) ต่อเหตุผลเข้า `Document.Notes` ด้วยหัวข้อ_
_`[ยังไม่ลงบัญชี]` — ผู้ใช้เห็นตอนเปิดใบ ไม่ใช่แค่ใน log (2) sync log เป็น_
_**`PartialSuccess`** ไม่ใช่ `Success` + เหตุผลลง `ErrorMessage` (3) คำตอบที่ส่ง_
_กลับคู่ค้าต่อท้าย `⚠ ยังไม่ลงบัญชี: <เหตุผล>` · เหตุผลถูกส่งออกจากจุดที่ `return_
_null` ผ่าน `Action<string>? onSkip` (ตัวเดียวกับข้อความที่ log — ไม่มีสตริงสอง_
_ชุด) · จุดที่ตั้ง `log.Status` ทีหลังทุกจุดกันทับด้วย `if (log.Status !=_
_"PartialSuccess")`_
_**ทำไมไม่ throw ทิ้งทั้งก้อน**: เอกสารของคู่ค้าจะหายไปเลยและคู่ค้าส่วนใหญ่ไม่_
_retry — เก็บเอกสารไว้แล้วบอกให้ชัดว่ายังไม่ลงบัญชี ผู้ใช้แก้ผังบัญชีแล้วสั่ง_
_ลงบัญชีใหม่ได้ ซึ่งกู้คืนได้จริง (ต่างจากข้อมูลที่หายไปแล้ว)_
_· ของแถมในหมวดเดียวกัน: `PayrollService` มี `catch {}` **เปล่า** สองจุด —_
_จุดหนึ่งครอบงานที่ออก **50 ทวิ / ภ.ง.ด.1 / สปส.1-10** (มีกำหนดตามกฎหมาย)_
_ล้มทุกงวดก็ไม่มีใครรู้จนเลยกำหนดยื่น → ใส่ `LogError`/`LogWarning` ที่บอกว่า_
_เอกสารใดยังไม่ถูกสร้าง);_
_รอบ 105 — **DBD ชนะได้ก็ต่อเมื่อ_
_"กุญแจ" ถูก**: `EnrichFromDbdAsync` ค้นทะเบียนกรมพัฒนาธุรกิจการค้าโดยใช้_
_`VendorTaxId` เป็นคีย์ ซึ่งเป็นช่องที่ OCR อ่านผิดได้บ่อยที่สุดช่องหนึ่ง (บาร์โค้ด_
_EAN-13 ที่ผ่าน mod-11 · เลขผู้ซื้อถูกหยิบมาเป็นผู้ขาย · หลักเดียวเพี้ยน) เมื่อเลข_
_ผิด DBD คืน **คนละบริษัท** แล้วโค้ดเดิมถือว่า "ทะเบียนราชการต้องถูกเสมอ" จึง_
_(ก) **ทับชื่อผู้ขายที่อ่านมาถูกแล้ว**ด้วยชื่อบริษัทอื่น (ข) บันทึกชื่อที่ถูกต้อง_
_เป็น **negative example** = สอนตัวเรียนรู้ผิดถาวร (ค) ตั้ง confidence 0.95 ⇒_
_ไม่ขึ้นไฮไลต์เตือนตามกฎเหล็ก #3 ข้อ 3 (ง) **สร้าง Contact ผู้ขายรายใหม่ของบริษัท_
_ที่ไม่เกี่ยวกับใบนี้เลย**_
_→ ตัวแยกคือ **ระดับความต่าง**: OCR ที่อ่านชื่อเดียวกันผิด ได้สตริงที่*คล้าย*เสมอ_
_(สมมติฐานทั้งหมดของ FuzzyMatcher) — คนละบริษัทได้เกือบศูนย์. วัดกับตัวอย่างจริง:_
_อ่านเพี้ยน **0.772–0.941** · คนละบริษัท **0.000–0.087** ⇒ เกณฑ์ 0.45 อยู่กลาง_
_ช่องว่างกว้าง ๆ. ต่ำกว่าเกณฑ์ = **ไม่ทับ ไม่สอน ไม่สร้าง Contact** + ลด_
_confidence ของ `SellerTaxId` เหลือ 0.30 ให้ไฮไลต์เหลืองขึ้น + บอกตรง ๆ ว่า_
_"น่าจะอ่านเลขผู้เสียภาษีผิด" (หลัก "ไม่รู้ = ต้องบอกว่าไม่รู้")_
_· ของแถม: สถานะนิติบุคคลจาก DBD เคยโผล่แค่ใน ProcessingNotes (log ยาวที่ไม่มีใคร_
_อ่าน) → ขึ้นการ์ดผลสแกนเมื่อสถานะบ่งชี้ว่าเลิก/ร้าง/ชำระบัญชี (§65 ตรี(9)_
_หลักฐานไม่น่าเชื่อถือ · §82/5(5) ผู้ออกอาจไม่มีสิทธิออกใบกำกับแล้ว) — คำที่ไม่_
_รู้จัก **ไม่เตือน** ไม่ใช่ "ถือว่าผิด"_
_· **ผลตรวจที่ verify แล้วพบว่าไม่ใช่บั๊ก** (บันทึกไว้ไม่ให้ไล่ซ้ำ): `TargetDocument_
_Type` มีผู้เขียน 9 จุด — ทุกจุดมี guard `DocumentSide.MatchesRole` + เช็คค่าเดิม_
_ก่อนทับ (`is null or Expense or PaymentVoucher`) เป็น cascade ที่ deterministic_
_ไม่ใช่ "ใครมาก่อนชนะ" · ตระกูล extractor (`SmartFieldExtractor` /_
_`DocumentZoneAnalyzer` / learned patterns) ทุกตัวเป็น **fill-only-empty** จริง_
_· กฎ compliance ที่ "เถียงกันเอง" ถูกยุบไปที่ `OcrScanComplianceEvaluator` ตั้งแต่_
_รอบก่อนแล้ว);_
_รอบ 104 — **สามจุดที่ "ค่าของใบหนึ่ง_
_ไปโผล่อีกใบ / หายไปเฉย ๆ"**:_
_(1) **OCR เอาเลขที่ของใบก่อนหน้ามาทับใบใหม่** — `VendorKnownGoodCorrector` เก็บ_
_"ค่าที่รู้ว่าถูกต่อผู้ขาย" ไว้แก้ผลอ่านที่เพี้ยน ซึ่งถูกต้องสำหรับ**ชื่อ/ที่อยู่**_
_แต่ในลิสต์มี **`DocumentNumber`** ปนอยู่ด้วย — เลขที่เอกสารเป็นค่า "ต่อใบ" ไม่ใช่_
_"ต่อผู้ขาย" ⇒ เลขรันติดกันต่างกันหลักเดียวได้ similarity **0.83–0.92** เกินเกณฑ์_
_0.80 เสมอ (simulation เลขจริง 8 รูปแบบ: ถูกทับ 6/8) ⇒ ใบใหม่ถือเลขของใบเก่า ⇒_
_รายงานภาษีซื้อ §87 ยื่นเลขใบกำกับผิด + ด่านกันสแกนซ้ำตีว่าเป็นใบเดิม. **รหัสสาขา_
_§86/4** แย่กว่า: 5 หลักต่างกัน 1 ตัว = ratio **0.80 พอดี** ⇒ ผ่านทุกคู่ (00001 →_
_00000) ⇒ ใบของสาขาถูกเขียนเป็นสำนักงานใหญ่_
_→ ตัวตัดสินกลาง `Helpers/VendorKnownGoodFields`: แก้ได้เฉพาะช่องที่ **คงที่ต่อ_
_ผู้ขาย + เป็นข้อความ** (SellerName, VendorAddress) · ตัวเลขล้วนไม่มี "การสะกดผิด"_
_ให้ซ่อม จึงห้ามเดา · ฝั่งเขียนหยุดเก็บ `DocumentNumber` ตั้งแต่ต้นทาง +_
_**migration ลบแถวที่ค้างอยู่แล้ว** (แก้โค้ดอย่างเดียวไม่พอ — แถวเก่ายังอยู่)_
_(2) **แยกบิล POS ทำส่วนลด/ค่าบริการหายทั้งก้อน** — ใบลูกถูกสร้างโดยไม่สืบทอด_
_`DiscountPercent`/`ServiceChargePercent` แล้ว `RecalculateOrder(child)` คิดจาก 0_
_⇒ ร้านที่คิดค่าบริการ 10% **เสียรายได้ส่วนนั้นทุกครั้งที่แยกบิล** (฿1,000 แยกสอง_
_ใบ = หาย ฿100) ส่วนบิลที่ให้ส่วนลดท้ายบิล **เก็บลูกค้าเกิน ฿100**. เป็นเปอร์เซ็นต์_
_จึงยกมาตรง ๆ ได้ (ผลรวมเท่าเดิมพอดี) · ค่าที่เป็น**จำนวนเงินก้อน** (คูปอง/ทิป)_
_แบ่งไม่ได้โดยไม่เดา → **บล็อกพร้อมบอกทางแก้** แบบเดียวกับด่าน "ชำระแล้วห้ามแยก"_
_· คัดลอกแถว modifier + VAT รายบรรทัดมาด้วย (เดิมตั้ง 0 = ใบที่แยกออกมาเก็บเงิน_
_ค่าตัวเลือกโดยไม่มีบรรทัดอธิบาย)_
_(3) **หมายเหตุที่ผู้เบิกพิมพ์บนมือถือหายเงียบ** — `mobile-expense.html` อ่านค่า_
_ช่องหมายเหตุใส่ตัวแปรแล้ว**ไม่ส่งไปไหน**. §65 ตรี(3)/(14) รายจ่ายที่พิสูจน์ไม่ได้_
_ว่าเกี่ยวกับกิจการ = รายจ่ายต้องห้าม — ข้อความนี้คือหลักฐานชิ้นแรก → ต่อสายครบ_
_เส้น (ฟอร์ม → payload → `OcrCorrectionRequest.Notes` → `OcrScanResult.UserNotes`_
_+ migration → `Document.Notes` ของใบที่สร้าง)_
_(4) ป้ายชื่อชนิดเอกสารฝั่ง JS มี **5 สำเนา** และเพี้ยนจากกระดาษ 2 ชนิด_
_(`Receipt` = "ใบเสร็จ" · `CertificateInLieu` = "ใบรับรองแทนใบเสร็จ") → หน้าที่_
_โหลด layout.js สร้างรายการจาก `Layout.docTypeLabel` ตัวเดียว (คงลำดับของแต่ละหน้า_
_ไว้) · `portal.html` ไม่โหลด layout.js จึงแก้สตริงให้ตรงกระดาษ + ทิ้งหมายเหตุไว้);_
_รอบ 103 — **หัวเอกสาร: จอกับกระดาษ_
_ไม่ตรงกัน 6 เคส**: `Layout.docHeaderLabel` เป็น**สำเนามือ**ของ `ComputeDocumentTitle`_
_ที่รู้จักแค่ 3 ธง จึงมองไม่เห็น (ก) ชื่อหัวที่ผู้ใช้ตั้งเองใน_
_`CompanySettings.DocumentTitleOverridesJson` (ข) `template.CustomTitle`_
_(ค) ผู้ซื้อ §86/4 ไม่ครบ/walk-in → กระดาษพิมพ์ **"ใบกำกับภาษีอย่างย่อ"** แต่จอบอก_
_"ใบกำกับภาษี" (ง) ใบเสร็จ/ใบสำคัญรับที่มี VAT → กระดาษพิมพ์ **"ใบกำกับภาษี/_
_ใบเสร็จรับเงิน"** (§78/1) แต่จอบอกแค่ "ใบเสร็จรับเงิน" (จ) `IsDeposit` →_
_กระดาษต่อท้าย "(เงินมัดจำ)" (ฉ) `IssuedAsCashReceipt` — JS สลับลำดับเป็น_
_"ใบกำกับภาษี/ใบเสร็จรับเงิน" ทั้งที่กระดาษพิมพ์ "ใบเสร็จรับเงิน/ใบกำกับภาษี"_
_(ตรงกับ e-Tax T03). สองเคสกลางไม่ใช่แค่ป้ายผิด — มันคือ**ชนิดเอกสารทางกฎหมาย_
_คนละตัว** (§86/6 อย่างย่อ เคลมภาษีซื้อไม่ได้ตาม §82/5(2)) ⇒ ผู้ใช้ตัดสินใจส่ง_
_ให้ลูกค้าจากข้อมูลที่ผิด_
_→ แก้ด้วยกลไก ไม่ใช่แก้สำเนา: เซิร์ฟเวอร์คำนวณแล้วส่ง `DocumentResponse._
_DocumentTitle` มา (`ResolveDocumentTitleAsync` เดี่ยว + `ResolveDocumentTitlesAsync`_
_แบบ batch สำหรับหน้ารายการ) — JS **แสดงอย่างเดียว** กฎเดิมเหลือเป็น fallback_
_ของ endpoint ที่ยังไม่ส่งค่ามา (กลไกเดียวกับ `MENU_SECTIONS`/`complianceIssues`)_
_· ระหว่างทางยุบตัวเลือกเทมเพลตเป็นชุดเดียว (`LoadTemplatePoolAsync` +_
_`PickTemplate` pure) เพื่อไม่ให้เส้นทาง batch กลายเป็นอัลกอริทึมสำเนาที่สอง —_
_หน้ารายการจึงเป็น query คงที่ 3 ครั้ง/หน้า ไม่ใช่ N+1_
_· negative test: simulation รัน `docHeaderLabel` **จริง** จาก layout.js ยืนยัน_
_กฎเดิมเพี้ยน 5/5 เคสก่อน แล้วหลังแก้ตรง 5/5 + fallback ตรงกฎเดิม 8/8_
_· `DocumentTitleServerOwnedTests` ล็อกฝั่ง C# ทั้ง 6 เคส);_
_รอบ 102 — **ผลตรวจ "ฟีเจอร์ทำงานซ้อนกัน" 4 ด้าน — แก้ชุดแรก**:_
_(1) **`IncludeVat` ถูก implement 3 แบบในไฟล์เดียว → JE หายทั้งใบ**:_
_`IntegrationService` ฝั่งขาย+มี `line.VatAmount` หัก VAT ออกจาก net แต่_
_`totalAmount = subTotal` ⇒ ยอดรวมขาด VAT · ฝั่งขาย+ไม่มี `VatAmount` คำนวณ VAT_
_แต่**ไม่หัก** net ⇒ ยอดรวมถูกแต่ `SubTotal` เกินจริง (ฐานภาษีขายใน ภ.พ.30_
_และ e-Tax เกินเท่ายอด VAT) · **ฝั่งซื้อ `BuildDocumentLinesAsync` ไม่รู้จัก_
_ธงนี้เลย** คิด exclusive เสมอ แล้วผู้เรียกใช้ `totalAmount = subTotal − wht`_
_⇒ VAT ไม่เคยถูกบวก ⇒ JE ที่ประกอบขึ้นมี Dr เกิน Cr **เท่ายอด VAT พอดี** ⇒_
_ด่าน `Dr == Cr` ตีตกแล้ว `return null` **เงียบ ๆ** = เอกสารค่าใช้จ่าย/_
_ใบสำคัญจ่ายขึ้นสถานะ "อนุมัติแล้ว" **โดยไม่มีรายการบัญชีเลย** (ไม่มีค่าใช้จ่าย_
_ไม่มีภาษีซื้อ ไม่มีเจ้าหนี้) ขณะที่ `TaxService` ยัง mine ใบนี้เข้า ภ.พ.30 ⇒_
_**ภ.พ.30 ไม่ตรง GL ถาวร** · `IncludeVat = true` เป็น **ค่า default** ของ_
_request DTO ทั้ง 4 ตัว ⇒ เป็นเส้นทางปกติ ไม่ใช่ edge case_
_→ ยุบเป็นกติกาเดียว `DocumentLineVatConvention.SplitLine()` (ตัวถือ convention_
_"Amount = ก่อน VAT เสมอ" อยู่แล้ว ไม่สร้างสำเนาที่ 4) + `SubTotal` เป็นฐาน_
_ก่อน VAT เสมอ + `TotalAmount = SubTotal + Vat − WHT` ทั้ง 6 จุด ตรงกับ_
_`DocumentService` ที่เป็นเจ้าของกฎตัวจริง · CN/DN ไม่มีธงนี้จึงคงพฤติกรรมเดิม_
_· เทสต์ล็อก invariant `net + vat = ยอดที่ partner ส่งมา` (200,000 ยอด)_
_(2) **regression ของผมเองจากรอบ 99-100**: การ sync `extractedData → scanResult`_
_อยู่ **ก่อน** `EnrichFromRawText` / `ApplyLearnedPatterns` / `ZoneFallback`_
_และ re-sync ที่มีอยู่ครอบแค่ยอดเงิน ⇒ ช่องที่สามขั้นนั้นเติมให้ (ชื่อผู้ขาย/_
_เลขภาษี/เลขที่เอกสาร/วันที่) อยู่แต่ใน memory **ไม่เคยถูกบันทึก** — trace_
_เขียนว่า "เติมให้แล้ว" แต่หน้าจอว่าง และ `CreateDocumentFromScan` อ่านจาก_
_entity → ได้เอกสาร**ลงวันที่วันนี้**แทนวันที่บนกระดาษ (ผิดงวด ภ.พ.30)_
_→ เพิ่ม re-sync แบบ "เติมเฉพาะช่องที่ entity ยังว่าง" หลังสามขั้นนั้น_
_(3) **`quantity` ฝั่ง JS ใช้กติกาคนละแบบกับที่แสดงบนจอ**: ช่องจำนวนตั้ง_
_`min="0"` (ให้ปิดบรรทัดโดยไม่ลบได้) · `calcSum` ใช้ `|| 0` ⇒ จอโชว์ ฿0_
_ไม่เข้ายอดรวม · payload ใช้ `|| 1` (0 เป็น falsy) ⇒ **บันทึกเป็น 1 หน่วย**_
_เอกสารออกไปเกินจริงทั้งบรรทัดพร้อม JE/VAT ตามไปด้วย);_
_รอบ 101 — **ฟีเจอร์ซ้อน: endpoint ตราประทับสองตัวบน route เดียว**:_
_`SettingsController` มี `[HttpPost("stamp")]` **สองเมธอด** (ชื่อ `UploadStamp`_
_ทั้งคู่ — C# overload ได้จึงคอมไพล์ผ่าน) ตัวหนึ่งเป็นเวอร์ชันเข้มความปลอดภัย_
_ที่เขียนเพิ่มมาแก้เคส "หน้า document-templates โดน 403" (กัน SVG แต่**ไม่_
_persist** `StampPath` ⇒ PDF ไม่มีวันเห็นตรา) อีกตัวเป็นของเดิมของหน้า_
_settings (persist ครบ แต่**ยังรับ SVG** = stored XSS ที่กฎเหล็ก #4 C ห้าม)_
_⇒ route ชนกัน ASP.NET Core โยน `AmbiguousMatchException` = **อัปโหลด_
_ตราประทับได้ 500 ทุกครั้ง** ตั้งแต่คอมมิตที่เพิ่มตัวที่สองเข้ามา_
_→ ยุบเหลือตัวเดียว: persist ผ่าน `SettingsService.UploadStampAsync`_
_(ตัวถือกฎ validation ที่เดียว) + ตัด `image/svg+xml` ออกจาก allowedTypes_
_ของ**ทั้ง stamp และ logo** + ตัด svg ออกจาก `accept` ของ settings.html_
_+ response ตอบครบสัญญาของทั้งสองหน้า (`stampUrl` สำหรับ settings.html ·_
_`url`/`relativeUrl` สำหรับ document-templates.html) · สแกนซ้ำทั้งเรพแล้ว_
_ไม่มี route ซ้อนภายในคอนโทรลเลอร์เดียวกันเหลืออยู่ และไม่มี AiFeatureKey_
_ไหนมี distillation student ซ้อนสองตัว);_
_รอบ 100 — **เก็บงานคงค้างให้ครบ + แก้บันทึกที่เขียนเกินจริง**:_
_(1) **ตรวจซ้ำแล้วพบว่ารายงานรอบก่อนเขียนเกินจริง** — ที่สรุปว่า "VAT ปัดแบบ_
_banker's ทำให้ยอดต่างกัน ฿0.01 ทั้ง 5 จุด" จริงแค่ 2 จุด: สูตร `x × 7/107`_
_และ `x / 1.07` **ไม่มีค่าใดตกจุดกึ่งกลางเลย** (ต้องมี 14·c ≡ 107 mod 214_
_เมื่อ c = จำนวนสตางค์ ซึ่งเป็นไปไม่ได้ — ซ้ายคู่ ขวาคี่) พิสูจน์ด้วยการไล่_
_ยอด ฿0.01–฿20,000 ครบ 2 ล้านค่า. ที่ต่างจริงคือ `x × 0.07` (VAT บวกเพิ่ม)_
_ราว 0.5% ของยอด → แก้บันทึกใน DOCUMENT_FLOW/ACCOUNT_STRUCTURE/CLAUDE.md_
_ให้ตรงความจริง + ล็อกข้อเท็จจริงด้วย `VatRoundingModeTests`_
_(**negative test ที่ผ่านทั้งก่อนและหลังแก้ = ยังไม่ได้พิสูจน์ว่ามีบั๊ก**)_
_(2) **สูตร 7/107 ฝั่ง JS 3 จุด** (pos.html ×2 · documents.html ×1) ยุบเข้า_
_`Layout.vatFromInclusive` ที่คิดด้วย**จำนวนเต็มสตางค์** (ไม่มี float) —_
_ผลลัพธ์เท่าเดิมทุกค่า จึงเป็น**การป้องกัน drift ไม่ใช่การแก้บั๊ก**_
_(3) **escape ไม่ครบ 2 จุด** ใน `admin/ocr-config.html` (`.replace(/</g)`_
_ตัวเดียว ไม่ครอบ `"` ⇒ หลุดใน attribute context) → ใช้ `escapeHtml` ที่มีอยู่_
_(4) **`catch { }` กลืน error ส่งอีเมลคำเชิญ** — แอดมินเห็น "ต่ออายุคำเชิญแล้ว"_
_ซึ่งอ่านเหมือนสำเร็จ และไม่มีร่องรอยให้ไล่ SMTP เลย → log + คืน `emailError`_
_(ต้องเพิ่ม `ILogger` เข้า `AdminController` ซึ่ง**ไม่มีมาก่อน**)_
_(5) **ต่อสายโค้ดที่ไม่มีใครเรียกครบทั้ง 4 ตัว**:_
_· `GlobalProductLearner.GetActivePatternsAsync` (ตัวรวม) — `MatchAsync` ยิง_
_ตัวเดี่ยวทุกบรรทัดในลูป = N+1 ต่อการสแกน 1 ใบ → `PrewarmGlobalPatternsAsync`_
_ดึงครั้งเดียวก่อนเข้าลูป_
_· `OcrSelfCorrectionService.RunMaintenanceForCompanyAsync` → endpoint_
_`POST /admin/ocr-maintenance/run/{companyId}`_
_· `DocumentWorkflowPredictor.PredictNextAsync` → เป็น local prior ของ_
_`DocumentConversionSuggestion` (prompt ตั้งชื่อ `LocalModelVersion` ว่า_
_"WorkflowMap-v1" รออยู่แล้วแต่ไม่เคยมีใครเติม)_
_· `DocumentZoneAnalyzer.Analyze` → `ApplyZoneAnalysisFallbackAsync` ทำงาน_
_เมื่อ pipeline หลักไม่ได้ทั้งชื่อผู้ขายและยอดรวม (เติมเฉพาะช่องว่าง)_
_(6) **AI แตกบรรทัดเมื่อ engine ไม่คืนตาราง** — `AiFeatureKey.OcrLineItemSplit`_
_+ `OcrLineSplitPrompt` + `SplitLineItemsAsync` เรียกเฉพาะตอน `Items` ว่าง_
_และมียอดหัวกระดาษให้ตรวจ. **ด่านกันมั่ว `Helpers/OcrLineSplitGuard`**:_
_ผลรวมบรรทัดต้องลงตัวกับยอดก่อนภาษีหรือยอดรวม ±1 บาท ไม่ลงตัว = **ทิ้งทั้งชุด**_
_ไม่ใช่รับบางบรรทัด (บรรทัดที่แต่งขึ้นกลายเป็นรายการบัญชีจริง) · local path_
_คือบรรทัดสรุปใบเดียวที่มีอยู่แล้ว ⇒ ปิด AI ทั้งระบบยังสร้างเอกสารได้ครบ_
_(7) **GL prompt เดิม `.Take(3)`** ตัด 3 บรรทัดแรกของตะกร้า ซึ่งไม่ได้เป็น_
_ตัวแทนของยอด (บิลค้าปลีกเรียงตามลำดับสแกน) → เรียงตามยอดมากไปน้อย ส่งได้ 12_
_บรรทัด + บอกจำนวนที่เหลือ · ตัด `our_company` Phone/Email ที่ prompt ไม่เคยใช้_
_ออกจาก payload (จ่าย token ฟรี + ส่ง PII เกินจำเป็น) เพิ่มรหัสสาขาที่ §86/4 ต้องใช้_
_(8) **Vision-LLM tier: แก้เอกสาร ไม่ใช่เขียนโค้ด** — กฎเหล็ก #3 ข้อ 2 เขียนว่า_
_ขั้น 1 คือ "Vision/OCR primary (DeepSeek-VL)" แต่ `DeepSeekProvider` รับแต่_
_ข้อความ ส่งรูปไม่ได้ และไม่มี engine ตัวไหนเรียกโมเดล vision เลย ⇒ **doc ผิด**_
_แก้ CLAUDE.md ให้ตรงโค้ดจริง พร้อมระบุ 4 อย่างที่ต้องมีถ้าจะทำจริง และห้าม_
_merge ครึ่ง ๆ กลาง ๆ เพราะตัวเลขที่ได้กลายเป็นรายการบัญชี);_
_รอบ 99 — **ต่อสายฝั่งอ่านของตารางที่เรียนรู้**:_
_`OcrLearnedPatterns` ถูก**เขียน**ทุกครั้งที่ผู้ใช้แก้ผลสแกน (`OcrService` 2 จุด)_
_และทุกครั้งที่สแกนผ่าน Azure DI (`AzureDiPatternLearner` ที่ doc-comment ของ_
_ตัวเองเขียนว่า "DocumentZoneAnalyzer.ApplyLearnedPatterns picks them up") —_
_แต่ `DocumentZoneAnalyzer.Analyze` ซึ่งเป็นทางเข้าเดียวที่อ่านตารางนี้_
_**ไม่มี call site ทั้งเรพ** ⇒ write-only ตั้งแต่วันแรก: จ่ายค่าเขียนทุกการแก้_
_แต่ความแม่นไม่เคยดีขึ้นเลย และเรื่องที่โฆษณาไว้ว่า "ได้ความแม่นระดับ Azure_
_ฟรีบนสแกน Tesseract รอบถัดไป" ไม่เคยเกิดขึ้น (ผู้อ่านตารางนี้ตัวเดียวที่มีอยู่จริง_
_คือ `OcrSelfCorrectionService` ที่อ่านเพื่อ **ลบ** แถวเก่า)_
_→ เพิ่ม `DocumentZoneAnalyzer.ApplyLearnedPatternsTo(OcrExtractedData, …)` ที่_
_ทำงานบน DTO ของไปป์ไลน์เอง แล้วเรียกจาก `OcrService` หลัง `EnrichFromRawText`_
_(เส้นทางหลัก engine-agnostic) กติกา: **เติมเฉพาะช่องที่ยังว่าง ห้ามทับค่าที่_
_engine อ่านได้** · ข้าม negative example · ค่าที่เติมต้องผ่านด่านชนิดข้อมูล_
_(เลขภาษี 13 หลัก + checksum · ยอด > 0) · regex ที่เก็บไว้เสีย/ช้าไม่ทำให้ทั้ง_
_การสแกนล้ม · เขียนร่องรอยลง ReasoningTrace ว่าเติมช่องไหนบ้าง_
_พร้อมเทสต์ที่**พิสูจน์ว่าฝั่งอ่านถูกเรียกจริง** ตามที่ CLAUDE.md บังคับ_
_(ฝั่งเขียนที่ทำงานอย่างเดียวดูเหมือนระบบเรียนรู้อยู่ตลอด));_
_รอบ 98 — **ตารางกฎหมายที่ถูกคัดลอกไปเขียนใหม่**:_
_ต่อจากรอบ 97 — สาม "ตารางความรู้เชิงกฎหมาย" ที่มีสำเนามือมากกว่าหนึ่งชุด และ_
_ทุกชุดตอบไม่ตรงกัน (defect class "รายการที่คัดลอกมาด้วยมือ = drift แน่นอน"):_
_(1) **อัตราหัก ณ ที่จ่าย มี 2 ตาราง ไม่ตรงกันเอง และไม่ตรงกฎหมายทั้งคู่** —_
_`WithholdingTaxCertController.GetIncomeTypes` ใส่ 40(3) ค่าสิทธิ = **5%** (ท.ป.4/2528_
_คือ 3%) และ 40(1) เงินเดือน = **3% คงที่** ทั้งที่กฎหมายใช้อัตราขั้นบันได ·_
_`wht-credit.html` dropdown ยุบ "ดอกเบี้ย/เงินปันผล" เป็นตัวเลือกเดียวที่ **1%**_
_ทั้งที่ดอกเบี้ยบุคคล 15% · ดอกเบี้ยนิติบุคคล 1% · ปันผล 10%. dropdown คือ_
_คำแนะนำเดียวที่ผู้ใช้เห็นตอนกรอกอัตรา ⇒ ป้ายผิด = ยอดหักผิดตั้งแต่ต้นทาง_
_แล้วไหลไป ภ.ง.ด.3/53 และเครดิต CIT ทั้งสาย → ยุบเป็น `Helpers/ThaiWhtRateTable.cs`_
_(แยกอัตราบุคคล/นิติบุคคล · เงินเดือนเก็บ null แทนใส่ตัวเลขปลอม) แล้วให้_
_หน้าเว็บ**สร้าง dropdown จาก `/api/reference/income-types`** — drift เป็นศูนย์_
_โดยโครงสร้าง + `ShouldWithhold()` ที่บังคับด่าน ฿1,000 แบบสะสมตามข้อ 12_
_(2) **checksum เลขผู้เสียภาษีฝั่ง JS มี 2 ชุดที่ตอบไม่ตรงกันบนหน้าเดียวกัน** —_
_`layout.js validateTaxId` **ไม่มีกติกาหลักแรก 0-8** ⇒ เลขขึ้นต้น 9 ผ่านด่านฝั่ง_
_ผู้ใช้แล้วไปตายที่เซิร์ฟเวอร์โดยไม่บอกว่าผิดตรงไหน · `smart-hooks.js` มีสำเนา_
_ที่ตรวจหลักแรก → เหลือตัวเดียว `Layout.taxIdCheck` (ตรงกับ `Helpers/ThaiTaxId`)_
_และ smart-hooks เรียกตัวนั้น_
_(3) **แปลง พ.ศ.→ค.ศ. มี 4 เกณฑ์ที่ต่างกัน** (`> 2500` · `>= 2400` · `> 2400` ·_
_`> currentYear + 10`) ⇒ ปีย่อ "69" เส้นทาง `ParseThaiDocument` ได้ **2069**_
_ขณะที่ `EnrichFromRawText` ในไฟล์เดียวกันได้ 2026 — ใบเดียวกันลงคนละปีตาม_
_เส้นทาง OCR → `ThaiDate.NormalizeYear()` ตัวเดียว + เทสต์ที่ reproduce 2069);_
_รอบ 97 — **ไล่หา defect class ที่แก้แล้วแต่ยังเหลือที่อื่น**:_
_ตั้งทีมไล่ตรวจ 8 defect class ที่เคยแก้ไปรอบเดียวว่ายังเหลือตัวที่สองที่ไหนบ้าง_
_ผลคือเจอของจริงทุก class — ที่กระทบเงิน/ภาษี/ความเป็นส่วนตัวถูกแก้ในรอบนี้:_
_(1) **ข้อมูลการค้าข้ามผู้เช่ารั่ว** — `GlobalVendorIntelLearner.CanShareMoneyAggregates`_
_(ด่าน k-anonymity k=3) ถูกเขียนไว้ + มี doc-comment ระบุว่า "guarded by the k=3_
_read-time gate inside VendorIntelligenceService.PredictAsync" แต่ **ไม่มีใครเรียกเลย**_
_⇒ ถ้ามีผู้เช่ารายเดียวที่เคยทำธุรกรรมกับผู้ขายรายนั้น ผู้เช่ารายอื่นเห็น_
_ยอดเฉลี่ย/ต่ำสุด/สูงสุด/มัธยฐาน ของรายนั้นตรง ๆ บนแบนเนอร์ "ยอด X นอกช่วง min–max"_
_→ ใส่ด่านแล้ว: ต่ำกว่า k=3 ส่ง null ทุกช่องที่เป็นจำนวนเงิน (ช่องพฤติกรรมเปิดตามเดิม)_
_(2) **เครดิตภาษีถูกหักคิดจากยอดรวมทั้งใบ** — `EnsureWhtCreditFromCertAsync` เขียนว่า_
_`whtAmount = derived > 0 ? derived : (ExtractedTotalAmount ?? 0)` ⇒ ใบ 50 ทวิ ยอด 1,070_
_หัก 3% ถูกบันทึกเป็นเครดิต CIT **1,070 บาท** แทน 30 และ `IncomeAmount` เขียน 0 ทำให้_
_อัตราย้อนกลับเป็นอนันต์ ไม่มีด่านไหนจับได้ แถวนั้นเป็น `Received` ⇒ หักภาษีจริงใน_
_ภ.ง.ด.50 ทันที → แยกเป็น `Helpers/WhtCertAmountResolver.cs` (pure + เทสต์):_
_ฐาน×อัตรา → จำนวนเงินตัวอักษรบนกระดาษ (มีเพดาน "ภาษี < ยอดจ่าย") → **ไม่รู้**_
_(บันทึกเป็น Pending ที่ TaxService ไม่นับเป็นเครดิต ไม่ใช่เดายอด) · เพิ่ม_
_`MidpointRounding.AwayFromZero` · `PayerFormType` ตัดสินจาก `Company.BusinessType`_
_(ผู้ถูกหัก = เรา) แทน hardcode ภ.ง.ด.53_
_(3) **6 ช่องที่รับตอน Create/Update แต่ไม่มีใน `DocumentResponse`** —_
_`BuyerDeclinedTaxInvoice` (ธง §86/4 ที่คุมทั้งหัวกระดาษและหมายเหตุ e-Tax) ·_
_`PreparerName` · `PreparerSignatureBase64` · `DepositAppliedRef` ·_
_`DepositAppliedDrivesJournal` · `InputVatClaimPeriod` ⇒ ติ๊กแล้วเปิดแก้ใบ_
_ค่าเด้งกลับเงียบ ๆ ทุกครั้ง (บล็อกเดียวกับ `IssuedAsCashReceipt` ที่แก้ไปแล้ว —_
_ตกค้าง 6 ช่อง) · `InputVatClaimPeriod` ไม่มีคอลัมน์ของตัวเอง จึงคำนวณที่_
_`ResolveInputVatClaimPeriod` ตัวเดียวแล้วส่งเป็น "yyyy-MM" แทนให้หน้าเว็บประกอบเอง_
_(4) **สำเนา `salesTypes` ใน JS ที่ `DocumentSide.cs` ระบุชื่อไว้เองว่าเป็นตัวที่สาม**_
_ยังไม่เคยถูกแตะ ⇒ ใบลดหนี้**ขาย** (OurRole=Seller) เปิดฟอร์มรายจ่าย และใบส่งของ_
_จากผู้ขายเปิดฟอร์มรายได้ → เพิ่ม `DocumentSide.BuildSideMap()` ส่งมากับผลสแกน_
_(`OcrResultResponse.DocumentSideMap`) หน้าเว็บอ่านอย่างเดียว + เทสต์บังคับว่า_
_ทุก `DocumentType` ต้องถูกจัดฝั่ง (เพิ่มชนิดใหม่แล้วลืม = เทสต์แดง)_
_(5) **ครึ่งเซิร์ฟเวอร์ของการแก้ ship ไปคนเดียว** — `OcrCorrectionRequest` เปิดรับ_
_`VendorBranchCode`/`BuyerTaxId`/`BuyerBranchCode` และ persist แล้ว แต่ payload builder_
_ตัวเดียวของหน้า review ไม่เคยส่งมาเลย และ modal โชว์ผู้ซื้อเป็นข้อความอ่านอย่างเดียว_
_⇒ ผู้ใช้ยัง "ไม่มีทางแก้" และ `?? "00000"` ยังเป็นตัวเติมสาขาตัวจริง → เพิ่มช่องกรอก_
_3 ช่อง + ส่งใน correction + handoff อ่านค่าที่ผู้ใช้แก้ก่อน `'00000'`_
_(6) **XSS ค้างในโมดัลเดียวกับที่เพิ่งแก้** — ตาราง raw/debug ของ `document-scan.html`_
_ต่อค่าที่ OCR/กระดาษคุมได้เข้า `innerHTML` โดยไม่ผ่าน `Layout.esc` 6 จุด (ชื่อผู้ขาย/_
_เลขภาษี/เลขเอกสาร/วันที่/หมวด/engine) ทั้งที่ทุกจุดอื่นบนหน้าเดียวกัน escape ครบ ·_
_แก้พร้อมกับ `Layout.esc(...).substring()` ที่ตัดกลาง entity_
_(7) **VAT ปัดแบบ banker's rounding** ใน `SaasBillingDocumentService` 4 จุด +_
_`SampleDataController` ทั้งที่ `PlatformBillingDocumentIssuer` ที่คิดสูตรเดียวกัน_
_ใส่ `AwayFromZero` ไว้ถูกแล้ว — **แต่ตรวจย้ำในรอบ 100 แล้วพบว่าจริงแค่ครึ่งเดียว**:_
_สูตร `x × 7 / 107` และ `x / 1.07` **ไม่มีทางตกจุดกึ่งกลาง** (ต้องมี 14·c ≡ 107_
_mod 214 เมื่อ c เป็นจำนวนสตางค์ — เป็นไปไม่ได้เพราะซ้ายคู่ ขวาคี่) ⇒ ใส่โหมดปัด_
_หรือไม่ ผลเท่ากันทุกค่า. ที่ต่างจริงคือ `x × 0.07` (VAT บวกเพิ่ม) ซึ่งตกกึ่งกลาง_
_ราว 0.5% ของยอด (฿1.50 → 0.105 ⇒ 0.11 เทียบ 0.10) — คือ 2 ใน 5 จุดที่แก้ไป_
_ส่วนอีก 3 จุดเป็นการทำให้สม่ำเสมอ ไม่ใช่การแก้บั๊ก (ดู `VatRoundingModeTests`)_
_(8) **ทะเบียนสินค้าถูกเขียนด้วยค่าที่แต่งขึ้น** — `OcrController` นำเข้าสต็อกตั้ง_
_`Unit: "ชิ้น"` (ทั้งที่ `UnitInferrer` ถูกสร้างมาเพื่อเรื่องนี้และเส้นทางเอกสารแก้ไปแล้ว)_
_และ `VatRate: 7m` ⇒ สินค้ายกเว้น §81/อัตรา 0 ถูกตั้ง 7% ใน**ทะเบียน** แล้วใบขาย_
_ทุกใบในอนาคตคิด VAT ผิดตาม → ใช้ `UnitInferrer` + อิงจากใบว่ามี VAT จริงไหม_
_(9) **`catch {}` กลืน error 4 จุด** — `ExtractedItemsJson` เพี้ยน = ตารางรายการว่าง_
_โดยไม่มี error (ผู้ใช้อนุมัติเอกสารที่ไม่มีบรรทัด) · บัญชีเครดิตของ JE ตั้งสินทรัพย์_
_ตกไปใช้ "บัญชี 21xx ตัวแรก" เงียบ ๆ · ยอดรายการประจำโชว์ ฿0 เมื่อ JSON มีตัวเลข_
_เป็นสตริง → ใส่ log + parse ให้ทน_
_(10) **`VendorClusteringService` รายงานว่าสำเร็จทั้งที่ไม่ได้เก็บอะไรเลย** —_
_รัน K-means ทั้งระบบแล้วทิ้งผล (โค้ดเขียนหมายเหตุว่า "will return assignments to_
_caller" แต่ `ClusterResult` ไม่มีช่อง assignment) ขณะที่หน้าแอดมินขึ้น "จัดกลุ่ม_
_สำเร็จ (N vendors)" → คืนสรุปกลุ่มจริง + ธง `Persisted=false` + ข้อความบอกตามจริง);_
_รอบ 96 — **prompt ที่มีช่องแต่ไม่มีใครเติม**:_
_ผลตรวจ "AI โยนข้อมูลไปพอให้คิดครบไหม" รอบสอง — พบรูปแบบเดิมซ้ำอีก 3 จุด คือ_
_prompt เตรียมช่องบริบทไว้ครบแต่ **ผู้เรียกไม่เคยส่งค่าลงไปเลยตั้งแต่วันแรก**_
_(defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้"):_
_(1) **`WhtCategoryPrompt`** มีพารามิเตอร์ 4 ตัว (ประวัติหักภาษีของผู้ขาย ·_
_ยอดสะสมปีนี้ · ค่าเฉลี่ย 6 เดือน · ธุรกิจของผู้ขาย) แต่ `DocumentAiAugmenter`_
_เรียกด้วย 8 ตัวแรกเท่านั้น ⇒ AI เดารหัสเงินได้จากคำอธิบายบรรทัดล้วน ๆ ทั้งที่_
_ระบบมี 50 ทวิ ของผู้ขายรายเดิมอยู่ในมือ → เพิ่ม `LoadWhtVendorContextAsync`_
_(join `WithholdingTaxCertLines`→`WithholdingTaxCerts` ย้อน 24 เดือน ตัดใบ Voided,_
_ทุก query มี `CompanyId`) + `vendorDominantGlAccount` จาก `OcrCategoryMapping`._
_แถมแก้กติกาด่าน **฿1,000 ที่เดิมเขียนผิดกฎหมาย** — prompt สั่งว่า_
_"amount < 1000 → Skip" ซึ่งเป็นการดูยอด**ต่อบรรทัด** แต่ ท.ป.4/2528 ข้อ 12_
_เป็นเกณฑ์**สะสมต่อคู่สัญญา** ⇒ จ่ายงวดละ 800 สามงวด = ต้องหักตั้งแต่งวดแรก_
_ที่ยอดรวมถึง 1,000 (ระบบเดิมตอบ Skip ทุกงวด = ลูกค้าไม่ได้หักภาษีเลย)_
_(2) **`StockDecisionPrompt`** ขอ `suggested_account_code` มาตลอดแต่ payload_
_**ไม่เคยมีผังบัญชีของ tenant อยู่เลย** ⇒ AI ต้องเดารหัสจากผังมาตรฐานไทยซึ่ง_
_อาจไม่มีในผังลูกค้ารายนั้น → ส่ง `candidate_accounts` (GlCandidateBuilder 120 รหัส)_
_+ HARD CONSTRAINTS ว่าต้องคัดลอกรหัสจากลิสต์เท่านั้น + **anti-hallucination guard_
_ฝั่งรับ** (`ScrubStockDecisionJson`) ที่ล้างรหัสบัญชี/GUID สินค้าที่ไม่มีจริงทิ้ง_
_แล้วลด action เป็น `MatchUncertain` พร้อมติด compliance flag บอกผู้ใช้_
_(เดิมคำตอบดิบถูกส่งไปหน้าเว็บเป็นปุ่ม "ใช้ค่านี้" ให้กด → ลงบัญชีผิด)_
_(3) **ผังสินค้าเรียงตามรหัส `.Take(200)`** = ตัดตามตัวอักษร ⇒ ร้านที่มีสินค้า_
_เกิน 200 รายการ ตัวที่ตรงกับใบนี้แทบไม่เคยติดลิสต์ AI จึงตอบ CreateNew ทุกบรรทัด_
_→ สร้างสินค้าซ้ำ. เปลี่ยนเป็นจัดอันดับด้วย token overlap กับคำอธิบายบรรทัด_
_(`LoadRelevantProductsAsync`, pool 3,000 แถว, เสมอกันเรียงตามรหัสเพื่อ deterministic)_
_+ บอก AI ตรง ๆ ว่า `product_catalog` เป็น subset — "ไม่เจอ ≠ ไม่มี"_
_(4) **`company_industry` ค่า default `"general"` ชนะเสมอ** เพราะผู้เรียกไม่เคยส่ง_
_→ ส่ง `Company.BusinessType / IndustryType` จริง (ตัดสิน Inventory vs Supply)_
_(5) **GL prompt ส่งแต่ผังฝั่งรายจ่าย** ทั้งที่ `IntegrationService.ProcessInvoiceAsync`_
_เรียกเส้นเดียวกันเพื่อสร้าง**ใบกำกับภาษีขาย** ⇒ ผังรายได้ 4xxxx ไม่เคยอยู่ใน_
_candidate เลย → `expenseAssetOnly` ผูกกับ `lineContext.OurRole` + rule 6 ของ_
_`GlAccountPrompt` อ่าน `our_role` ก่อนแทนที่จะยืนยันว่า "นี่คือใบสำคัญจ่าย" เสมอ_
_(6) **`localConfidence: 0.50m` บน "Acknowledge"** = ตัวเลขที่แต่งขึ้นให้ดูน่าเชื่อ_
_บนคำตอบที่แปลว่า "ไม่รู้" → ลดเป็น 0.20 ให้ตรงความจริง (ไม่กระทบ routing_
_เพราะยังต่ำกว่า short-circuit 0.85 เหมือนเดิม);_
_รอบ 95 — **ผลตรวจทีม 4 ด้าน: ไล่ OCR ทั้งสาย**:_
_ตั้งทีมผู้เชี่ยวชาญ 4 คน (อนุมานชนิดเอกสาร · วิเคราะห์ข้อมูลบนกระดาษ · UX/UI ·_
_สถาปัตยกรรม AI) ไล่ตรวจตั้งแต่อัปโหลดถึงเอกสารเสร็จ ข้อสรุปใหญ่: **ปัญหาส่วนใหญ่_
_ไม่ใช่ขาดความสามารถ แต่คือของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้** —_
_(1) **ตัวอนุมาน**: 50 ทวิ กลับทิศทั้งสองทาง (ตัวอ่านทิศที่ถูกต้องมีอยู่แล้วแต่ถูก_
_เรียกหลังสร้างเอกสาร) · เอกสาร 4 ชนิดตกหลุม Expense (ใบเสนอราคา/ใบวางบิล/ใบส่งของ/_
_สลิปโอนเงินซึ่งไม่มี marker เลย) · ใบกำกับซื้อไม่มีเครดิตเทอม → PV = บันทึกจ่ายที่_
_ไม่เคยเกิด · ตัวเรียนรู้ federated เขียนมาตลอดแต่**เงื่อนไขอ่านเป็นจริงไม่ได้เลย** ·_
_prior ของ vendor ข้ามฝั่งซื้อ/ขายได้ · สแกนใบขายตัวเองซ้ำ = ออกเลขซ้ำ ·_
_ตัวจำแนกฝั่งซื้อ/ขาย 3 ชุดตอบไม่ตรงกัน → ยุบเป็น `Helpers/DocumentSide.cs`_
_(2) **สกัดข้อมูล**: รหัสสาขาถูกอ่านเฉพาะ Tesseract — เส้นทาง Azure (เส้นหลัก)_
_ไม่เคยอ่านเลยแล้ว default 00000 เงียบ ๆ · กฎที่ 4 เป็น stub hard-code "ผ่าน" ·_
_FieldConfidence ใช้ชื่อ key 3 ชุดไม่ตรงกัน (ด่านลด confidence ไม่ทำงาน 3/4 เส้นทาง ·_
_ไฮไลต์เหลืองแทบไม่เคยขึ้น · ไม่ persist) → `Helpers/OcrFieldKeys.cs` + คอลัมน์ใหม่ ·_
_**ไม่เคยเทียบจำนวนเงินตัวอักษร** ทั้งที่เป็น check ที่แรงที่สุดบนใบไทย →_
_`Helpers/ThaiAmountInWords.cs` (round-trip 2,000 ค่า) · เล่มที่/เลขที่ · อย่างย่อ_
_เชิงโครงสร้าง (§82/5(2)) · ต้นฉบับ/สำเนา_
_(3) **UX**: ปุ่มที่ระบบติดป้าย "แนะนำ" ทิ้งการแก้ไขทั้งหมด + ไม่เรียนรู้เลย ·_
_strict-products บล็อกการเซฟใบ OCR ทุกใบ · แท็บ "สร้างแล้ว" ว่างถาวร ·_
_ลำดับปุ่มกลับด้านกับกฎเหล็ก #3 ข้อ 4 · mobile-expense ไม่เคยเรียก OCR + POST ไป_
_route ที่ไม่มีอยู่จริง (404 ทุกครั้ง) · ผู้ใช้แก้ "บทบาทเรา" ไม่ได้เลย_
_(4) **AI**: DocumentTypeClassification มี enum+prompt+student ครบแต่**ไม่มี call_
_site** · OcrFullReview ไม่มี student = ปิด provider แล้วตายเงียบ (ผิดกฎเหล็ก #1) ·_
_feedbackId ถูกทิ้ง = จ่าย token ฟรี → ต่อสายครบทั้งสามจุด);_
_รอบ 94 — **บาร์โค้ดสินค้าถูกอ่านเป็นเลขผู้เสียภาษี**:_
_ใบ PI-20260820-0005 ถูกต้องครบแต่ขึ้นเตือน 3 ข้อ รวม "อาจอัพโหลดผิดบริษัท" —_
_เลข `8885009199627` ที่ระบบอ้างว่าเป็นเลขผู้ซื้อไม่เคยมีบนกระดาษ เป็นบาร์โค้ด_
_ในตารางสินค้าที่ผ่าน mod-11 ไทยโดยบังเอิญ · แก้ 5 ชั้น: (1) resolver กลาง_
_`Helpers/ThaiTaxId.cs` (checksum + EAN-13/GS1 + first-digit) แทน checksum ที่_
_กระจาย 5 ที่ · (2) `SmartFieldExtractor` คัดบาร์โค้ด + ให้เลขที่มีป้าย_
_"เลขประจำตัวผู้เสียภาษี" ชนะ + regex ตัวคั่นไม่รวม `\n` (เดิมต่อเลขข้ามบรรทัด_
_เป็นเลข 13 หลักที่ไม่มีบนกระดาษ) + **เลิกปลอม `buyerPos = text.Length`**_
_(เดิม = เลขท้ายหน้ากลายเป็นเลขผู้ซื้อทุกใบ) · (3) Rule 1 ย่อข้อความด้วย_
_`ThaiTextNormalizer.SquashForKeywordMatch` ก่อนค้น "ใบกำกับภาษี" · (4) Rule 7_
_ต้องดู raw text ก่อนฟันธง — เพิ่ม `BUYER_TAX_ID_MISREAD` (Warning) สำหรับ_
_"อ่านผิดช่อง" และจำกัด `TENANT_BUYER_MISMATCH` (Error) ไว้เฉพาะใบฝั่งซื้อที่_
_เลขเราไม่อยู่บนกระดาษเลย · (5) `OcrService` เติมเลขผู้ซื้อ = บริษัทเราเมื่อ_
_พิสูจน์ได้ว่าเราไม่ใช่ผู้ขายและเลขเราอยู่บนกระดาษ (กฎเหล็ก #3 ห้ามปล่อยช่องว่าง)) ·_
_**กวาดทั้งเรพต่อ**: pattern เดียวกันถูกคัดลอกไปวางอีก 4 ไฟล์ และรูปแบบ `[-\s]?`_
_เดียวกันยังใช้กับเบอร์โทร/เลขบัญชี/เลขที่เอกสารในสเตทเมนต์ธนาคาร/ตัวกรอง PII_
_ก่อนส่งเข้า AI รวม 13 จุด 7 ไฟล์ → ยุบเข้า `ThaiTaxId.Pattern` + เปลี่ยนตัวคั่น_
_ทุกที่เป็น `[- \t]?` + migration ล้าง `OcrLearnedPatterns.ExtractionRegex` ที่ค้าง_
_ในฐาน + checker ตัวที่ 18 `tools/regex_line_span_check.py`) ·_
_**รอบสอง (การ์ดผลสแกน)**: ใบเสร็จ/ใบกำกับ หจก.สหกลชลบุรี เลขที่ 0339 พิมพ์เลข_
_ผู้ซื้อไว้เต็ม ๆ แต่การ์ดยังเตือน "ควรระบุเลขผู้เสียภาษีของผู้ซื้อ" — สองต้นเหตุ:_
_(ก) แบบฟอร์มพิมพ์สำเร็จวาง**ป้ายไว้ใต้เส้นประ** ⇒ ค่าอยู่บรรทัดก่อนป้าย แต่ตัว_
_ตรวจป้ายมองย้อนหลังอย่างเดียว → มองสองทิศ (look-ahead 45) + ด่านคัดบาร์โค้ด_
_เลิกรับ "มีป้าย" เป็นข้อยกเว้น · (ข) `document-scan.html` มีสำเนามือของกฎ_
_`RdComplianceValidator` เขียนด้วย JS ที่ไม่เคยแก้ตาม → ย้ายมาคำนวณที่เซิร์ฟเวอร์_
_ตัวเดียว `Ocr/OcrScanComplianceEvaluator.cs` ส่งเป็น `OcrResultResponse_
_.ComplianceIssues` ให้หน้าเว็บแสดงอย่างเดียว (แถมปิด XSS จากค่าที่ OCR อ่านมา));_
_รอบ 93 — **ที่อยู่แบรนด์ผูกกับทะเบียนได้**:_
_`DocumentBrand.AddressSource` (Company/Branch/Custom) + resolver กลาง_
_`Helpers/BrandAddressSource.cs` · ย้ำด่าน "เอกสารภาษีใช้ที่อยู่สถานประกอบการเสมอ"_
_· พรีวิวเดินลำดับเดียวกับ renderer (ที่อยู่/โลโก้) — เดิมพรีวิวโชว์ที่อยู่หน้าร้าน_
_บนใบกำกับภาษีทั้งที่กระดาษจริงพิมพ์ที่อยู่จดทะเบียน);_
_รอบ 92 — **§6.2d สาขาผู้ออกเอกสาร**:_
_`Document.BranchId` + `IssuerBranchCode` (snapshot ตอนอนุมัติ) + resolver กลาง_
_`Helpers/DocumentIssuerBranch.cs` → รหัสสาขา/ที่อยู่บน renderer ทั้งสองตัว +_
_TXID/SellerTradeParty ของ e-Tax + สืบทอดเอกสารลูกครบ 5 ทาง + ตัวเลือกสาขาบนฟอร์ม_
_(โผล่เมื่อมี ≥ 2 สาขา · ค่าเริ่มต้น = ตามค่าบริษัทเสมอ) — **กิจการสาขาเดียวไม่กระทบ**);_
_รอบ 91 — **resolver กลางรหัสสาขา**:_
_`Helpers/TaxBranchCode.cs` แทนสูตรที่กระจาย 3 ที่ (PdfA3 ผู้ขาย/ผู้ซื้อพิมพ์เลขดิบ_
_"00003" · DocumentBrandController พิมพ์ "สาขาที่ 00003") → "สาขาที่ 3" ตาม_
_ประกาศอธิบดีฯ ฉบับที่ 199 ทุกจุด; ทะเบียนสาขาเฟส 0 ดู ACCOUNT_STRUCTURE.md §3.1a —_
_**ยังไม่แตะ flow เอกสาร** (Document.BranchId มาเฟส 1));_
_รอบ 90 — **§6.2c ผลตรวจ 3 ทีม**:_
_LegalLine ท้ายกระดาษใน QuestPDF · ที่อยู่จดทะเบียนบนเอกสารภาษี · บรรทัดรอง_
_แบรนด์ไม่ gate · IsActive ไม่ตัดตอน render · tenant guard · pinned template_
_ตรงชนิด · สืบทอดครบทุกเอกสารลูก · normalize marker);_
_รอบ 89 — **§6.2c ต่อของที่ค้าง**:_
_อัปโหลดโลโก้แบรนด์ + เลือกรูปแบบเอกสารรายใบ (Document.DocumentTemplateId) +_
_resolver กลาง ResolveDocumentTemplateAsync แทน if/else ที่ซ้ำ 2 ที่);_
_รอบ 88 — **§6.2c ปรับตามที่ผู้ใช้สั่ง**:_
_ไม่ตั้งแบรนด์ = เหมือนเดิมทุกอย่าง · ตั้งแล้วค่าเริ่มต้นยังเป็นชื่อบริษัท_
_(ตัด IsDefault ที่เลือกให้อัตโนมัติทิ้ง));_
_รอบ 87 — **§6.2c ชื่อทางการค้าบนหัว_
_เอกสาร**: DocumentBrand + Document.BrandId · ด่าน §86/4 + resolver กลาง_
_DocumentIssuerIdentity ที่ renderer ทั้งสองตัวใช้ร่วม · หน้าตั้งค่า document-brands);_
_รอบ 86 — **§6.2b convention ยอด_
_รายบรรทัดต้องเป็น net**: ซ่อมอัตโนมัติตอนอนุมัติ + backfill + ข้อความ error_
_ที่บอกทางแก้ (เคสจริง OCR ใบราคารวม VAT → Dr เกิน Cr เท่ายอด VAT));_
_รอบ 85 — **§6.2 ความยินยอมตอนสมัคร_
_(PDPA ม.19)**: ด่าน `RequireSignupConsent` + `RecordSignupConsentAsync` ทุกทางสมัคร ·_
_`PdpaConsentRecord` scope แพลตฟอร์ม + evidence hash canonical ตัวเดียว ·_
_หน้า terms.html/privacy.html จริง + `GET /api/legal/policy`);_
_รอบ 84 — **คุณภาพ OCR → สร้างเอกสาร**:_
_(1) `DocumentNumberSanitizer` กันเลข 2 ชุดถูกต่อกัน (บิล กฟภ.) โดยเทียบกับ_
_token บนกระดาษจริง; (2) reconcile บรรทัดแยกเคส "ราคาถือยอดรวม" (หารหาราคา_
_เก็บจำนวน) ออกจาก "จำนวนหลงคอลัมน์" (แก้จำนวน) — ค่าไฟ 59 ล้านหาย;_
_(3) `UnitInferrer` หน่วยตามชนิดรายการ + handoff เลิกทิ้ง field `unit`;_
_(4) `ProhibitedInputVatScreener` §82/5(4)(6) เลิก default เคลมให้ค่าน้ำมัน/_
_ค่ารับรอง + banner บอกรถประเภทที่เคลมได้/ไม่ได้; (5) `?approve=true` +_
_ปุ่ม "⚡ สร้าง + อนุมัติ" + ปิด modal/เด้งเปิดใบหลังสร้าง + แก้ deep-link_
_`?id=`→`?openDoc=`; เทสต์ `DocumentNumberSanitizerTests`,_
_`ProhibitedInputVatScreenerTests`, `OcrLineReconcileTests` (+3 เคส);_
_รอบ 83 — **การจัดหน้าเอกสารพิมพ์**:_
_(1) คอลัมน์ส่วนลดหายไปเองเมื่อทั้งใบไม่มีส่วนลด — resolver กลาง_
_`ShouldShowDiscountColumn` ที่ renderer ทั้งสองเรียก (ดู % ด้วย ไม่ใช่แค่ยอดบาท)_
_ตั้งค่า `HideEmptyDiscountColumn` ปิดได้; (2) ทุกกล่องข้อมูลย้ายทั้งก้อน_
_ไม่ผ่ากลาง (`break-inside: avoid` / `.ShowEntire()`); (3) บล็อกลายเซ็นเลิกใช้_
_flex → `<table><tr><td>` เพราะ Chromium ไม่เคารพ `break-inside` บน flex_
_(ลายเซ็นกับชื่อผู้เซ็นเคยแยกคนละหน้า); (4) หัวกระดาษ 3 ส่วนซ้ำทุกหน้าผ่าน_
_`.doc-frame thead` (HTML) + `page.Header()` (QuestPDF) ตั้งค่า_
_`RepeatHeaderEveryPage`; เทสต์ `DocumentPrintLayoutTests`;_
_รอบ 82 — **ตารางสะกดทางการระดับ_
_อำเภอ/เขต**: 50 เขต กทม. ยืนยันมือ + อำเภอเมือง 75 แห่งประกอบจากตารางจังหวัด_
_อัตโนมัติ (กันเคส เมืองจันทร์/เมืองปาน ที่ไม่ใช่ชื่อจังหวัด) — ครอบ 126/928 อำเภอ_
_+ แขวง กทม. 35/178 · อำเภอที่เหลือไม่ใส่มั่ว ใช้ AddressEn กรอกทับแทน;_
_รอบ 81 — **ที่อยู่คู่สัญญาบนเอกสาร_
_ภาษาอังกฤษแก้ทับได้แล้ว**: เพิ่ม `Contact.NameEn`/`AddressEn` คู่ขนานกับฝั่งบริษัท_
_(เดิมมีแต่ฝั่งเรา ที่อยู่ลูกค้าถูกถอดอักษรอัตโนมัติเสมอและแก้ไม่ได้เลย) +_
_รวมตรรกะที่เคยเขียนซ้ำ 4 จุดเป็น `ResolvePartyAddress`/`ResolvePartyName` ตัวเดียว_
_ที่ทั้ง HTML และ QuestPDF เรียก · เทสต์ `PartyEnglishTextTests`;_
_รอบ 80 — **ตรวจศัพท์อังกฤษบนเอกสาร_
_ทีละคำ**: แก้ 2 คำที่ผิดจริง ("bill discount" = ขายลดตั๋วเงินในภาษาการเงิน ·_
_"under-calculated" ไม่ใช่คำอังกฤษ) + 5 จุดที่ตกสาระ (เหตุผล CN/DN ย่อเกิน ·_
_Our document · Authorized · Expense) · เพิ่มตารางคำต้องห้ามใน DocumentLabelsTests_
_กันแปลตรงตัวกลับเข้ามา; รอบ 79 — **อนุมัติใบกำกับแปลงล้ม 500 +_
_แผงปรับปรุง JE ล้มเมื่อบัญชีซ้ำ**: ตัวขอเลข JE ทั้งสองตัวนับ change tracker ด้วย_
_(§6.0a-ter — AutoPost ค้าง SV ยังไม่ save แล้ว supersede ขอเลขตัวกลับ SV เดือน_
_เดียวกัน → เลขซ้ำ → unique ล้ม → อนุมัติใบแปลงไม่ได้ทั้งระบบ) · Adjust ใช้_
_GroupBy ก่อนทำ map บัญชี (JE ปกติมีบัญชีซ้ำหลายบรรทัดได้เสมอ) ·_
_เทสต์ `ConvertedTaxInvoiceApproveTests`; รอบ 78 — **ใบที่กู้คืนจากการยกเลิก_
_อนุมัติกลับเข้าบัญชีไม่ได้**: ด่าน §86/4 ใน UpdateDocumentAsync เช็ค "ส่งฟิลด์_
_มาไหม" แทน "แก้ค่าจริงไหม" — ฟอร์มส่ง Lines/ส่วนลดท้ายบิล/PricesIncludeVat_
_มาทุกครั้งอยู่แล้ว จึงบล็อกทุกการกดบันทึกแม้ไม่ได้แก้อะไร ⇒ "บันทึกและอนุมัติ"_
_ล้มที่ขั้น update ก่อนถึง approve. แก้เป็นเทียบค่าจริง + เพิ่ม PrintedLinesChanged_
_ที่ไม่นับผังบัญชี (ไม่ได้พิมพ์บนใบ) · เทสต์ `RestoredDocumentApproveTests`;_
_รอบ 77 — **จำลองเหตุการณ์ชุด 8:_
_ใบสำคัญปรับปรุงผังบัญชีตลอดวงจรชีวิต** (§6.0a-bis): ตัวกลับตกงวดของใบที่มัน_
_กลับ ไม่ใช่งวดเอกสารทั้งก้อน (ใบ ก.ค. ที่ปรับปรุงลง ส.ค. เคยผิดสองเดือน_
_พร้อมกันโดยยอดรวมทั้งปียังตรง) · กฎ scanner `DOC-ADJUST-LOST` เตือนเมื่อ_
_ยกเลิก→คืนชีพ→อนุมัติใหม่ ทำให้ผังที่แก้ไว้หายเงียบ ๆ ·_
_เทสต์ `SimulationRound8Tests`; รอบ 76 — **จำลองเหตุการณ์ชุด 7:_
_ใบขายที่ถูกหัก ณ ที่จ่ายแล้วปิดยอดด้วยมัดจำ**: ลูกหนี้ใน GL ตั้ง gross ตาม_
_เกณฑ์เงินสด แต่ BalanceDue สุทธิ ⇒ ปิดครบแล้วลูกหนี้ค้างเท่ายอด WHT ตลอดไป_
_และไม่ได้เครดิตภาษีตอนยื่น ภ.ง.ด.50 — เพิ่ม hook รับรู้ Dr 11910 / Cr ลูกหนี้_
_แบบ GL-first (ลงเฉพาะส่วนต่าง) ทั้งเส้นเอกสารและเส้น JV ·_
_เทสต์ `SimulationRound7Tests`; รอบ 75 — **จำลองเหตุการณ์ชุด 6:_
_ไฟล์ที่ยื่นจริงต้องเท่ากับรายงานบนจอ** (§6.0a): ใบ 50 ทวิ ที่ไม่มีบรรทัดย่อย_
_เคยหายทั้งใบจากไฟล์ ภ.ง.ด.3/53 ทั้งที่หัวสรุปยังนับภาษีของใบนั้น (นำส่งขาด) ·_
_สรุป ภ.ง.ด.50 แยก "ก่อนเครดิต / หักเครดิต / ต้องชำระเพิ่ม" (ป้ายเดิมเขียน_
_"หลังเครดิต" บนยอดก่อนเครดิต) · เทสต์ `SimulationRound6Tests`;_
_รอบ 74 — **จำลองเหตุการณ์ชุด 5:_
_มัดจำที่รับรู้บางส่วน · การจับคู่ธนาคารค้าง · ด่านงวดของสมุดรายวัน**:_
_family-net ของมัดจำแยก "JE ที่สร้างหนี้สินมัดจำ" ออกจาก JE รับรู้รายได้_
_(เดิมเอาส่วนที่เหลือไปตัดใบแจ้งหนี้แล้วลง Dr 41000 ล้างรายได้ที่รับรู้แล้ว) ·_
_ยกเลิกการชำระ/เปลี่ยนแหล่งเงินปลดการจับคู่ธนาคารที่ค้าง (§6.0c) ·_
_ด่านงวดยึดวันที่ ไม่ใช่แค่ FK + re-resolve เมื่อเปลี่ยนวัน + Post ปฏิเสธงวด_
_Locked (§6.0b) · เทสต์ `SimulationRound5Tests`; รอบ 73 — **จำลองเหตุการณ์ชุด 4:_
_เส้น integration + รายจ่ายที่เคลมภาษีซื้อไม่ได้**: ผังบัญชีที่ partner ส่งมาต้อง_
_อยู่ถูกฝั่งเอกสาร (ฝั่งจ่ายห้ามบัญชีรายได้ — JE แบบนี้สมดุลเป๊ะ guard จับไม่ได้_
_และกำไรสุทธิไม่ขยับ) · ย้ายผังบัญชีย้าย "ยอดที่ลงจริง" ผ่าน_
_`ResolveLinePostedGlAmountAsync` (VAT ต้องห้ามรวมเป็นต้นทุน / ใบอ้าง GRN /_
_PV settlement / ต่างสกุล) · CIL พับ VAT เคลมไม่ได้เข้าบรรทัดของตัวเองแทนกอง_
_รวม + §65 ตรี ครอบ CIL แล้ว · 50 ทวิ ยึดเกณฑ์เงินสด ท.ป.4/2528 (ตั้งหนี้ =_
_ฉบับร่าง, จ่ายจริง = ออกใบ; idempotency key = payment+document) ·_
_เทสต์ `SimulationRound4Tests`; รอบ 70-72 — **จำลองเหตุการณ์ชุด 1-3**:_
_guard เทียบยอดข้ามสกุล · ทุกทางปิดยอดยิง tax point §78/1 · 50 ทวิ ผูกงวดจ่าย ·_
_void ใบกำกับที่แทนที่ใบแจ้งหนี้แล้วคืนใบเดิมเป็นร่าง; รอบ 69 — **ปิดช่องว่าง OCR D1-D3**:_
_`BranchCodeExtractor` อ่านสาขาผู้ขาย/ผู้ซื้อแยกกัน (เดิมผู้ซื้อไม่เคยถูกอ่าน) ·_
_pseudo-target `Deposit` สแกนใบมัดจำได้ตรง (ทั้ง UI และ auto-create) ·_
_`duplicate-check` เตือนก่อนสร้างเมื่อใบเดิมถูกสแกนซ้ำ; รอบ 68 — **PaidOnIssue persist**:_
_เจตนา "รับเงินครบแล้ว" เก็บลง Document + Draft พิมพ์หัวรวมตามเจตนา / หลังอนุมัติ_
_ตามชำระจริง (mirror คู่ ComputeServedAsReceipt/ResolveServedAsReceiptAsync) ·_
_500 มีรหัสอ้างอิงผูก ErrorLogs; รอบ 67 — **JournalPostingGuard**:_
_ด่านตรวจโครงสร้าง JE ก่อนบันทึก (WHT ≤ 15% ของฐาน · ขาเจ้าหนี้/เงินครบยอด ·_
_VAT ไม่เกินเอกสาร) wire เข้า AutoPost + integration 3 จุด · scanner_
_`journal-anomalies` + การ์ด 🩺 หา JE เสียเก่าและเอกสารที่ไม่มี JE ·_
_เทสต์ `JournalPostingGuardTests` จากเคสจริง; รอบ 66 — **ตัวเลือกผังบัญชีบนมือถือ**:_
_เลิกใช้ `<datalist>` (Android Chrome ไม่เด้งรายการ) เปลี่ยนเป็น `<select>` +_
_`optgroup` ทั้งโมดัลแก้ JE และเปลี่ยนผัง · `_ensureAllAccounts()` โหลดครบ 5 หมวด_
_รวมส่วนของเจ้าของ · ถือ `accountId` ตรง ๆ แทนการ parse โค้ดจากข้อความ ·_
_แถวเป็นการ์ดต่อบรรทัดให้อ่านได้บนจอแคบ; รอบ 65 — **เลขที่/วันที่ใบลดหนี้จาก_
_ผู้ขาย**: เลิกยกเลขใบกำกับที่อ้างถึงมาเติมให้ CN/DN (ผู้ใช้สับสน + รายงานภาษีซื้อ_
_โชว์เลขผิด) และเพิ่ม `bookSupplierCreditNote` ให้ OCR ที่สแกนใบลดหนี้ของผู้ขาย_
_เติมเลขที่/วันที่จริงให้ โดยไม่ผูกกับเงื่อนไข VAT · เทสต์_
_`SupplierCreditNoteRefTests`; รอบ 64 — **แผง "📒 รายการบัญชี" บน_
_หน้าเอกสาร**: กางบรรทัด Dr/Cr จริงจาก GL + ปุ่ม "✏️ แก้ผังบัญชี" ต่อใบ →_
_`AdjustDocumentJournalEntryAsync` รับสถานะปลายทาง (แก้ผัง/เพิ่ม/ลดบรรทัด/เลือก_
_วันที่) แล้วลงใบปรับปรุงใหม่ตามผลต่าง · ค่าคงที่: ยอดรวมห้ามเปลี่ยน + บัญชีคุม_
_ห้ามขยับ · เทสต์ `AdjustDocumentJournalTests`; รอบ 63 — **แปลงเอกสารแล้วยอดขยับ_
_1 สตางค์**: `ConvertCoreAsync` ยก `VatAmountOverride` จากบรรทัดต้นทางเมื่อยกทั้ง_
_บรรทัด ⇒ ใบกำกับยอดตรงใบแจ้งหนี้เป๊ะ · ฟอร์มเอกสาร round-trip VAT ที่บันทึกไว้_
_ผ่าน `_vatBasisKey`/`_keptVat` (ไม่แตะฐาน = ไม่คิดใหม่) + ป้ายบอกส่วนต่าง ·_
_เทสต์ `ConvertTaxRoundingTests`; รอบ 62 — **แก้วันที่กลับบัญชีรายใบ**:_
_ทุกแถวในตารางมีช่องวันที่ของตัวเอง + ปุ่มตั้งทั้งชุด (วันที่ใบแรก / วันที่เอกสาร /_
_ตามใบต้นฉบับ / วันที่ที่เลือก) · API รับ `RedateVoidReversalRequest{Entries[]}` ·_
_กล่องอธิบาย "ทำไมใบเดียวมีหลายรายการ" (ลงบัญชีตามเหตุการณ์ ไม่ใช่ตามใบ);_
_รอบ 61 — **เปลี่ยนผังบัญชีรายบรรทัด_
_ของใบฝั่งขาย**: `ReclassifyLineAccountAsync` เปิดให้ Invoice/TaxInvoice/Receipt/_
_CN/DN · ทิศ JE อ่านขาที่ลงจริงใน GL แล้วกลับตามนั้น (รายได้ = Dr เก่า/Cr ใหม่) ·_
_เลิกบล็อกด้วยการมีรับ-จ่ายชำระ · ใบเสร็จหลักฐานไม่นับเป็นเอกสารปลายทาง ·_
_`ReclassifyProtectedCodes` เทียบตรงรหัสกันบัญชีคุม (ภาษี/ลูกหนี้-เจ้าหนี้/มัดจำ) ·_
_เทสต์ `ReclassifyLineAccountTests`; รอบ 60 — **แก้วันที่กลับบัญชีกับ_
_เอกสารที่มีตัวกลับหลายใบ**: ค่าเริ่มต้นต่อใบ = วันที่ใบต้นฉบับที่ตัวเองกลับ_
_(`ResolveRedateTarget`) ไม่ใช่ยัดรวมวันเดียว · endpoint preview_
_`GET /document/{id}/redate-void-reversal/preview` + โมดัลโชว์ตารางว่าใบไหนย้าย_
_จากวันไหนไปวันไหน/ย้ายไม่ได้เพราะอะไร ก่อนกดยืนยัน; รอบ 59 — **แก้ผังบัญชีของ JE ที่ระบบ_
_ลงจากเอกสาร**: หน้าสมุดรายวันโชว์เฉพาะปุ่มที่ server ยอมให้ทำ (ใบผูกเอกสาร =_
_"ดู" + "📄 เปิดเอกสารต้นทาง") · banner ชี้ทางแก้จริง (Expense/PI/PV → ปุ่ม_
_"✏️ เปลี่ยนผัง" ท้ายบรรทัด `ReclassifyLineAccountAsync`; ชนิดอื่น → ยกเลิกแล้ว_
_ออกใหม่) · `documents.html` อ่าน `?search=` (ลิงก์ตาย 4 หน้า) และ `?openDoc=`_
_(เปิดหน้ารายละเอียด ไม่ใช่ฟอร์มแก้ไข) · `CorrectJournalEntryAsync` guard_
_`SourceDocumentId` พร้อมข้อความชี้ทาง · แผงช่วย "❓ ปุ่มไหนใช้ตอนไหน" ·_
_เทสต์ `JournalEntryActionGuardTests` ตรึงตารางสิทธิ์; รอบ 40 — **วันที่ JE กลับรายการตอน_
_ยกเลิกเอกสาร**: `VoidDocumentAsync(companyId, documentId, reversalDate = null)` —_
_ค่าเริ่มต้นเปลี่ยนจาก `DateTime.UtcNow.Date` เป็น **วันที่ของเอกสารเอง** (ยกเลิก_
_ใบเดือนก่อนแล้วรายการกลับตกเดือนปัจจุบัน = ผิดสองเดือนพร้อมกัน: เดือนเก่าค้างยอด_
_ที่ไม่มีอยู่จริง เดือนใหม่มียอดติดลบไม่มีที่มา) · ส่งวันที่เดียวกันต่อไปทุกขา_
_(JE ของเอกสาร, JV หักมัดจำ, การกลับรับชำระเดี่ยว/หลายใบ, JV มัดจำ orphan) ·_
_`ResolveReversalDateAsync` ตกกลับเป็นวันนี้เมื่องวดปลายทางปิด **พร้อม log เหตุผล**_
_(ไม่บล็อกการยกเลิก ไม่เงียบ) · Supersede (INV→TIV) ใช้วันที่ของใบกำกับที่มาแทน ·_
_UI ถามวันที่ก่อนยืนยัน (default = วันที่เอกสาร) · **เครื่องมือแก้ย้อนหลัง**_
_`RedateVoidReversalAsync` + ปุ่ม "📅 แก้วันที่กลับบัญชี" บนใบที่ยกเลิกแล้ว —_
_ย้ายเฉพาะ JE ตัวกลับ (OriginalEntryId != null) ไม่แตะต้นฉบับ ⇒ ยอดสุทธิเท่าเดิม_
_เปลี่ยนแค่งวด; งวดต้นทาง+ปลายทางต้องเปิดทั้งคู่; รอบ 39 — Task Force P5-P7 (N+1):_
_ตรวจรหัสบัญชีของบรรทัดเอกสาร (`ValidateLines`) เดิมยิง AnyAsync **ต่อบรรทัด** —_
_ใบ 50 บรรทัด = 50 query ทุกครั้งที่สร้าง/แก้ · import เงินเดือนเช็คผัง 2 query/_
_พนักงาน (200 คน = 400 query) · โพสต์เงินเดือน resolve แหล่งจ่าย 1 query/โค้ด —_
_ทั้งหมดเปลี่ยนเป็นดึงชุดเดียวก่อนลูปแล้วเทียบใน RAM (query คงที่ 0-1 ครั้ง); รอบ 38 — Task Force P4/Q3: (P4)_
_`GetGeneralLedgerAsync` รวมยอดยกมาใน SQL (GroupBy+Sum) แทนดึง JournalEntryLine_
_ทั้งหมดตั้งแต่เปิดบริษัทเข้า RAM เมื่อไม่ได้เลือกบัญชีเจาะจง · (Q3) AuditMiddleware:_
_ข้าม path ที่เขียนถี่แต่ไม่มีคุณค่าเชิงตรวจสอบ (chat/telemetry/autosave/preview),_
_`Guid.TryParse` แทน `Parse` (subject ที่ไม่ใช่ GUID เคย throw **หลังงานสำเร็จ**_
_⇒ ผู้ใช้เห็น 500 แล้วกดซ้ำ = เอกสารซ้ำ), ตัด UserAgent ที่ 512 ตัว, และ audit ที่_
_บันทึกไม่สำเร็จ log เป็น Error แทนทำให้ request 500; รอบ 37 — เครื่องมือนักบัญชีใหม่:_
_**กระทบยอด GL ↔ รายงานภาษี** (`TaxGlReconciliationService` +_
_`GET /accountant/tax-gl-recon` + การ์ดในหน้า accountant.html): เทียบยอดเคลื่อนไหว_
_ใน GL ของงวด (21911 ภาษีขาย / 11610 ภาษีซื้อ / 21916-21917-21918 WHT ค้างจ่าย /_
_21912 ภ.พ.36) กับยอดในแบบที่จะยื่น — **พร้อมไล่หาสาเหตุจริงเมื่อไม่ตรง**:_
_บรรทัดที่ติ๊กออกจากรายงาน · ภาษีซื้อพัก 11640 (§86/4 ไม่ครบ) · เอกสารที่บันทึก_
_หลังสร้างรายงาน (snapshot ไม่อัปเดตเอง) · เอกสารที่ถูกยกเลิกแต่ยังอยู่ในแบบ ·_
_50 ทวิ ที่ยังเป็นร่าง/ยังไม่ออก · cert ที่ออกคนละเดือนกับเอกสาร (จ่ายข้ามเดือน) —_
_แต่ละสาเหตุมียอดที่อธิบายได้ + รายการเอกสาร + สิ่งที่ต้องทำ; รอบ 36 — S8: `/uploads/**` เปลี่ยนเป็น_
_**allow-list** และย้าย guard ไปอยู่**ก่อน static handler ทุกตัว** — ของเดิมบล็อกแค่_
_`/uploads/attachments` ซึ่งไม่ตรงที่ไฟล์เก็บจริง (เอกสารแนบอยู่_
_`/uploads/{companyId}/{entityType}/…`, สแกน OCR อยู่ `wwwroot/uploads/ocr/…` ที่_
_static handler ตัวแรกเสิร์ฟก่อน guard เดิมเสียอีก, e-Tax XML/PDF อยู่ uploads/etax)_
_⇒ ไฟล์การเงิน/PII โหลดได้ทาง URL ตรงโดยไม่ต้อง login. เปิดเฉพาะ logos/banners/_
_products/stamps/cms/signatures/order-slips/portal-slips; รอบ 35 — Task Force P3 บางส่วน: (Q5)_
_`CmsCommerceService.ConfirmPaymentAsync` เลิกกลืน error ใน money/stock path —_
_สะสมความล้มเหลว (อนุมัติเอกสาร/บันทึกชำระ/ตัดสต๊อก), log เป็น Error, และปักหมุด_
_ลง `Order.InternalNotes` ให้แอดมินเห็นว่า "เงินเข้าแล้วแต่บัญชี/สต๊อกยังไม่ครบ"_
_(throw ไม่ได้เพราะเป็น webhook — gateway จะ retry วนไม่จบ) · (S9)_
_`UseForwardedHeaders` เป็น middleware ตัวแรกสุด: rate limit เคยนับทุกคนเป็น IP_
_เดียว (IP ของ proxy) และ audit/PiiAccessLog/ลายเซ็นอนุมัติบันทึก IP ผิดคน —_
_ปิดได้ด้วย `Security:TrustProxyHeaders=false`; รอบ 34 — backlog ภาษีครบทั้ง 14 ข้อ:_
_(B6) ภ.ง.ด.51 ใช้ฐานเดียวกับ 50 — `ComputeSection65TerAddBackAsync` เป็น helper_
_ร่วม (เดิม 51 คำนวณจาก JE ล้วนไม่มีบวกกลับ ⇒ ประมาณการต่ำ เสี่ยงเงินเพิ่ม 20%_
_§67 ตรี) · (B7) **ผลขาดทุนสุทธิยกมา 5 รอบบัญชี §65 ตรี(12)** หักจากฐานกำไรก่อน_
_คิด CIT (FIFO + หมดอายุปี Y+5) — เดิมโค้ด "เครดิตภาษีปีก่อน" เป็น dead code_
_(เช็ค NetVat<0 = ปีขาดทุน ซึ่ง CitAmount = 0 เสมอ) ผลขาดทุนจึงไม่เคยถูกหักเลย ·_
_(B8) ค่าเสื่อม CIT ตามรอบบัญชี (Year+Month vs ช่วง fiscal) — เดิมกรองปีปฏิทิน ⇒_
_บริษัทรอบไม่ตรงปีได้ค่าเสื่อมผิดรอบ · (B9) ภ.ง.ด.50/91 unique ต่อ **ปี** (เดิม_
_ปี+เดือน ⇒ สร้างได้ 12 ใบ/ปี แล้ว e-Filing หยิบใบไหนก็ได้) · (B10) ไฟล์ ภ.ง.ด.91_
_ใช้ประชากร payroll Approved/Paid ตรงกับจอ (เดิม "ไม่ใช่ Draft/Voided"); รอบ 33 — backlog ภาษี B1-B4/B11-B14:_
_(B1) หนังสือรับรองที่คีย์มือผูก `DocumentId` ได้ (DTO+guard tenant+กันออกซ้ำ) ⇒ เลิก_
_นับซ้ำกับแถวเตือน · (B2) ออกใบรายงวดทับใบเต็มจำนวนไม่ได้แล้ว (idempotency สองสาขา_
_เคยเช็คคนละ key จึงลอดกัน) · (B3) **เดือนนำส่ง ภ.ง.ด. = เดือนที่จ่ายเสมอ** ไม่ขึ้นกับ_
_WhtRecognitionBasis (เกณฑ์บัญชีคนละเรื่องกับเดือนยื่น — Accrual เคยทำให้เงินก้อนเดียว_
_โผล่ 2 เดือน) · (B4) regenerate: snapshot ติ๊กของทุกชนิดรายงาน (เดิมเฉพาะ VAT) +_
_คืนฟิลด์ audit/RD/การยื่น (EFilingExportedAt, RdAck*, Rejection*, ReversalJournalEntryId)_
_— เส้น ปลดล็อก→แก้→สร้างใหม่ เคยลบร่องรอยการยื่นทิ้ง · (B11) RecalcVatTotals เป็น_
_allow-list สองฝั่ง (เดิม default-to-output: code ใหม่/สะกดผิดไหลเข้าภาษีขายเงียบ) ·_
_(B12) ฐาน §82/3 ใช้ `ClaimBasisDate` (วันที่ใบกำกับผู้ขาย) ทั้ง generate และ pull —_
_เดิมคนละฐาน ใบเดียวกันตอบต่างกันแล้วแต่ทางเข้า · (B13) ใบต้นทางที่ soft-delete แล้ว_
_resolve ด้วย IgnoreQueryFilters (output VAT ของใบเสร็จเคยหายเงียบ) · (B14) UI ล็อก_
_ติ๊กบรรทัด 🚫/⚠️/[รอใบกำกับ ที่ server ปฏิเสธอยู่แล้ว + error ระบุลำดับบรรทัด_
_(auto-save ส่งทั้งหน้า ติ๊กผิด 1 บรรทัดเคย rollback ทั้งชุดโดยไม่บอกว่าบรรทัดไหน); รอบ 32 — TODO_OPUS A3/A6: ใบลดหนี้/_
_ใบเพิ่มหนี้ **ฝั่งซื้อ** มีช่อง "เลขที่/วันที่ใบจากผู้ขาย" (reuse SupplierInvoice_
_Number — enrichment รายงานภาษีซื้อหยิบให้อยู่แล้วเพราะบรรทัด CN ฝั่งซื้อ =_
_IncomeTypeCode "INPUT") + soft warning §86/10 ตอนอนุมัติเมื่อมี VAT แต่เลขว่าง;_
_`_syncSupplierInvoiceFields` แยกจาก onDocTypeChange เพื่อให้สลับฝั่ง CN เรียกได้_
_โดยไม่วนซ้ำ · ไฟล์แนบในหน้ารายละเอียดพับไว้เป็นค่าเริ่มต้น + โหลดรูปย่อเฉพาะตอน_
_กางครั้งแรก (เดิม auto-โหลดทุกไฟล์ ≤8MB ทุกครั้งที่เปิดใบ); รอบ 31 — TODO_OPUS A4/A5 "ระบบพูดภาษา_
_หัวกระดาษ": `DocumentResponse.ServedAsReceipt` (read-only, batch query 1 ครั้ง/หน้า_
_ไม่ใช่ N+1; mirror `ResolveServedAsReceiptAsync` ผ่าน `ComputeServedAsReceipt`) →_
_`Layout.docHeaderLabel/docHeaderBadge` ตั้งป้ายตามหัวจริงบน list + หัว detail modal_
_(ใบ combined เห็นได้ทันทีโดยไม่ต้องเปิดแก้/พิมพ์) · A5 "สองประตู ห้องเดียว":_
_เอกสารตั้งหนี้แล้ว (INV/TIV/DN) เลือก "ใบเสร็จ/ใบสำคัญรับ" ในหน้าแปลง → เปิด modal_
_บันทึกชำระเงิน prefilled แทนการเรียก Convert API (ผลบัญชี+เอกสารเหมือนกัน 100% และ_
_ทำได้มากกว่า: จ่ายบางส่วน/WHT/มัดจำ/FX); QT/BN ยัง convert จริง (ขายสด ไม่มี AR) ·_
_ข้อความติ๊กใบเสร็จเขียนใหม่ให้ตอบ "จะมีเอกสารใหม่ไหม" + ใช้หัวจริงของใบนั้น_
_(ใบ combined จ่ายครบ = 3-in-1 — เดิมเขียนตายตัวผิด); รอบ 30 — TODO_OPUS A1/A2/B5: ไฟล์ยื่น_
_ภ.ง.ด.3/53/54 (.txt) เปลี่ยนเป็น **detail rows ล้วน ไม่มี H|/T|** ตามหน้า import_
_ของสรรพากร (Helpers/PndTextFileFormat.cs = format กลางตัวเดียว ใช้ทั้งเมนูส่งออก_
_และปุ่ม e-Filing ในหน้ารายงาน — กันสองปุ่มได้ไฟล์คนละหน้าตา): 11 คอลัมน์ Col1 ลำดับ /_
_Col2 เลขภาษี 13 / Col3 สาขา 5 / Col4 ชื่อ / Col5 ชื่อสกุล (นิติ=ว่าง) / Col6_
_ddMMyyyy ค.ศ. / Col7 ประเภทเงินได้ / Col8 เงินได้ / Col9 อัตรา / Col10 ภาษี /_
_Col11 เงื่อนไขการหัก / **Col12 คำนำหน้าชื่อ (ข้อความไทย — รอบ 162)** · ภ.ง.ด.54 ย้ายเป็น cert-primary (เลิก mine เอกสาร — เดิม_
_ต่างจากจอ 13 จุด + ซ้ำกับ 3/53) · แถว "⚠️ ยังไม่ออกหนังสือรับรอง" เกิดมาแบบ_
_IsExcluded (ยอดจอ = ยอดไฟล์ยื่น = ทะเบียน 50 ทวิ เสมอ; ติ๊กกลับเองได้); รอบ 29 — audit 3 ทีมรายงานภาษีทุกชนิด,_
_แก้ 20 จุด: **ภ.พ.30** ApplyVatDeferrals ใช้ RecalcVatTotals จริง (เดิม scalar ทิ้ง_
_เครดิตยกมา = ยอดยื่นเกิน + เปลี่ยนเองหลังติ๊ก) · JE-fallback matcher แคบ (ตัด 21912/_
_21913/21914/21915/21918, prefix 114/115 เงินกู้-สต๊อก, 11620/11630, ผัง 5xxxx ชื่อ_
_ภาษีซื้อ) · ทั้งสอง fallback ข้าม JE ที่ถูกกลับรายการ · ExportPp30 ใช้รายงาน persisted_
_ที่ผู้ใช้ติ๊กแล้ว (เดิม recompute ทิ้งติ๊กทั้งหมด) · endStampExclusive กัน timestamp_
_วันสุดท้ายหลุดทุกงวด (5 จุด) · PV settlement/CIL ห้ามเคลม-ห้ามดึง + pull เคารพ_
_§82/5 · PDF ภ.พ.30 JE_INPUT จัดฝั่งถูก · **ภงด.** fallback ฐาน=ΣDr (เดิมลบ WHT_
_ซ้ำ อัตราเพี้ยนทุกแถว) + ExtractTaxpayer + 50 ทวิ payee ตปท. → ภงด.54 (เดิมได้แค่_
_3/53 = นำส่งซ้ำสองแบบ) + cert query เลิก Include INNER-JOIN (payee ถูกลบแล้วจอขาด)_
_· **CIT** กด "บันทึก" ไม่ทับหัวรายงานแล้ว + detail/Excel โชว์ รายได้/กำไร/CIT จริง ·_
_**ภ.พ.36** ฐาน header-only ใช้ SubTotal (เดิม dead-fallback = 0) + update ยอด Output/_
_Net ตามติ๊ก + BuildPp36 กรอง IsExcluded + recompute header · **ภงด.1/สปส.** block_
_การสร้างรายงานว่าง (ชี้ไปหน้า e-Filing payroll) · **ภงด.91** โชว์ยอดถูก (typeMap/_
_list/detail) · AutoRefresh ปลด VatDeferral ก่อนลบ · tax.html หัวคอลัมน์ per-tab +_
_kpi ids (หัวรายงานอัปเดตหลังติ๊ก) + คำเตือน 🚫/⚠️ ไม่ถูกเลขใบกำกับทับ._
_**Backlog (ยังไม่แก้ — บันทึกไว้):** manual cert ไม่มี DocumentId นับซ้ำกับแถวเตือน;_
_cert เต็มใบ+รายงวด ซ้ำ (idempotency ข้ามสาขา); Accrual PI/cert ข้ามเดือนซ้ำ;_
_Regenerate ภงด. ไม่ snapshot ติ๊ก + ล้าง audit fields + ไม่ atomic; ExportPnd54_
_ยัง doc-mined (13 ความต่าง); ภงด.51 คนละฐานกับ 50; ขาดทุนยกมา 5 ปีไม่หักใน CIT;_
_ค่าเสื่อม CIT ปีปฏิทิน vs รอบบัญชี; CIT unique รายเดือน (สร้างได้ 12 ใบ/ปี); ภงด.91_
_สร้างจาก UI ไม่ได้ + ประชากร payroll จอ≠ไฟล์; RecalcVatTotals default-to-output;_
_§82/3 ฐานวันที่ generate≠pull; Receipt ที่ต้นทาง soft-deleted VAT หาย; auto-save_
_ภงด. all-or-nothing; รอบ 28d: fallback "สแกน JE ไร้เอกสาร"_
_ของรายงานภาษี เลิกจับด้วย prefix หลวม — ภงด.3/53: เดิม prefix "2191" กวาดบัญชี VAT_
_21911/21912/21913 (JV auto-reconcile มัดจำโผล่เป็นแถว "REC..." VAT 7% ใน ภงด.53)_
_+ "11910 ถูกหัก" คือเครดิตเราไม่ใช่ยอดนำส่ง + ไม่แยกแบบ (แถวเดียวเข้าทั้ง 3 และ 53)_
_→ จับเฉพาะบัญชีของแบบ: 21916=ภงด.3 / 21917=ภงด.53 (+ชื่อที่ระบุแบบ, ไม่ใช่ "ถูกหัก");_
_ภ.พ.30 (กระจกเงา): IsOutputVat ตัด 21916/21917 ออก (JV WHT manual เคยโผล่เป็นภาษีขาย)_
_+ IsInputVat ตัด 11640 undue (เคลมก่อนถึงกำหนดไม่ได้); รอบ 28c: ไล่ตรวจ pipeline รายงานภาษี_
_ทั้งเส้น (generator → MapToResponse → list → detail → e-Filing → Excel) — จุดที่แก้:_
_(1) สูตรยอดหัวตอน generate ภงด. กรอง SUMMARY+IsExcluded ให้ตรง RecalcPndTotals_
_(เดิมคนละสูตร); (2) cert "ร่าง" ของงวดขึ้นเป็นบรรทัด IsExcluded "⚠️ ยังเป็นร่าง —_
_ออกใบก่อนยื่น" (เห็นแต่ไม่นับ) + บรรทัด [สรุป] ต่อผู้ขายไม่รวมแถว excluded;_
_(3) e-Filing ภงด.3/53 export เฉพาะ cert Issued/Printed (เดิม != Voided ⇒ ใบร่าง_
_หลุดเข้าไฟล์ยื่น + ไม่ตรงจอ); (4) e-Filing ภ.พ.36 เปลี่ยนเป็น single source_
_ComputePp36ReportAsync (pattern เดียวกับ ภ.พ.30) — เดิม mine เอกสารเองคนละเงื่อนไข;_
_ที่ตรวจแล้วถูก: MapToResponse ส่ง totalIncome/totalTaxWithheld/citAmount/lines ครบ,_
_ภ.พ.30 export single-source อยู่แล้ว, Excel อ่าน report lines, ตาราง detail มีติ๊ก_
_ใช้/ไม่ใช้; ภงด.54 export ยัง doc-mined (ติด ⚠️ ไว้ — เคสน้อย); รอบ 28b: ภงด.3/53/54 ใช้ทะเบียน_
_หนังสือรับรอง 50 ทวิ (Issued/Printed งวดนั้น) เป็นแหล่งหลักของรายงาน — เดิม mine_
_จากบรรทัดเอกสารเท่านั้น: ใบที่ WHT อยู่ระดับเอกสาร/งวดจ่าย (บรรทัดไม่มียอดราย_
_บรรทัด) ผ่าน filter นอกแต่ inner loop ว่าง ⇒ รายงาน 0 ทั้งที่ cert ออกครบ; เอกสาร_
_ที่ cert ครอบแล้วไม่ mine ซ้ำ + เอกสารยังไม่ออก cert ขึ้นเป็นแถว "⚠️ ยังไม่ออก_
_หนังสือรับรอง" (รวมเคส WHT ระดับเอกสาร — เดิมหายทั้งใบ) · หน้า tax.html: แถว ภงด._
_โชว์ totalIncome/totalTaxWithheld (เดิม bind outputVat/netVat ของ VAT ⇒ 0.00_
_เสมอ) + แท็บ/ตัวเลือกสร้าง "ภ.พ.36" (เดิมไม่มีที่ดูเลย) + detail modal แยก layout_
_ตามชนิดแบบ; รอบ 28: ภาษีซื้อไม่เคลม "ตามก้อนเงิน" —_
_UnclaimInputVatAsync + expiry job §82/3 เลิก fallback "บัญชี 53xx ตัวแรก" (เคยได้_
_53120 ค่าโฆษณา ทั้งที่ก้อนเงินคือ 54123 สวัสดิการ) → ResolveNonClaimableVatExpense_
_AccountAsync: บัญชีของบรรทัดที่ VAT เกาะ (VAT มากสุดชนะ; Expense/Asset — capitalize_
_เข้าต้นทุนได้) > ผังชื่อ "ภาษีซื้อ/ขอคืนไม่ได้" > (job) ผังค่าใช้จ่ายใดก็ได้ ·_
_ภ.พ.36 มี generator เฉพาะ (GeneratePp36Report): เอกสาร IsForeignService ฝั่งซื้อ_
_ตามงวด tax point, ยอดนำส่ง = VAT ประเมินเอง, dedup เฉพาะกับ ภ.พ.36 งวดอื่น —_
_เดิม reuse ตัว ภ.พ.30 แล้วโดน cross-report dedup จนว่าง/0 ตลอด · ภงด.3/53 กันนับ_
_ซ้ำสายตั้งหนี้→PV ตาม WhtRecognitionBasis (Cash: PV คือแถวจริง ใบตั้งหนี้ที่มี PV_
_active ข้าม; Accrual: กลับกัน) — เดิมเข้าทั้งสองใบ = นำส่ง 2 เท่า; รอบ 27: พรีวิว GL "ประมาณการ — ก่อน_
_อนุมัติ" (PdfGenerationService.BuildProjectedGlAsync) เลิก drift จาก JE จริง —_
_(1) WHT ฝั่งซื้อเคย hardcode 21510 ("เงินมัดจำรับล่วงหน้าค่าห้องพัก" ในผังมาตรฐาน!)_
_→ mirror ResolveWhtPayableAccountAsync: 21917 (นิติ ภ.ง.ด.53) / 21916 (บุคคล ภ.ง.ด.3);_
_(2) PV ที่ผูกใบตั้งหนี้ (แปลง/ดึงใบค้าง) เคยพรีวิวแบบ standalone (Dr ค่าใช้จ่ายซ้ำ)_
_→ mirror settlement branch: Dr เจ้าหนี้ gross (ตามชนิดใบต้นทาง + pinned AP) / Cr_
_เงิน / Cr WHT (Cash basis) — ไม่มีขาค่าใช้จ่าย/VAT; (3) Expense credit contra →_
_21220 เจ้าหนี้อื่น (เดิมโชว์ 21210 ทุกชนิด); (4) fallback เงินสด 11111 (11110 ไม่มี_
_ในผัง). ป้องกัน 3 ชั้น: mirror comment สองฝั่ง + tools/gl_code_check.py (จับ code/_
_ชื่อไม่ตรงผังมาตรฐาน — negative test จับ 21510 ที่บรรทัดจริง) +_
_GlAccountTemplateSanityTests (pin ความหมาย code ที่ JE engine พึ่ง); รอบ 26d: ไล่ปิด "ทางเข้าที่หลุด policy_
_หัวกระดาษ" ครบทุกทาง — (1) Clone (`DocumentCloneController`): สืบทอด Combined/_
_Declined/IssuedAsCashReceipt + CustomAppendix/Footer/Terms (เดิมสืบทอดแค่ภาษา ⇒_
_clone ใบหัวรวมได้ใบหัวเดี่ยวเงียบ ๆ) — ส่ง flag เฉพาะเมื่อไม่ override ชนิด; (2)_
_Recurring: template UI มีตัวเลือก "ใบแจ้งหนี้/ใบกำกับภาษี (ใบเดียว หัวรวม)"_
_(pseudo → TaxInvoice + templateData.combinedInvoiceTaxInvoice) + backend อ่าน_
_combined/declined จาก template (ไม่รองรับ cash โดยเจตนา — auto-gen รายเดือน_
_โดยไม่มีเงินเข้าจริง = เงินสดปลอม) + hydrate/detail แสดงหัวรวมถูก; (3) convert_
_เปิดฟอร์มร่างหลังแปลงครอบ QT→INV/TIV ด้วย (เดิมเฉพาะ INV/BN→TIV) ให้เลือก_
_หัวกระดาษต่อทันทีทุกเส้น; รอบ 26c: convert modal พูดภาษา "หัวกระดาษ"_
_— เพิ่ม pseudo-target `TaxInvoicePaid` "ใบกำกับภาษี/ใบเสร็จรับเงิน — รับเงินครบแล้ว"_
_ใน dropdown แปลงเอกสาร (เฉพาะแหล่ง INV/BN ที่แปลงเป็น TIV ได้; backend ไม่รู้จัก —_
_frontend แปลงเป็น TaxInvoice ทั้งฉบับ + เปิดฟอร์มพร้อม preset โหมด tax_paid ให้เลย);_
_pseudo ไม่มี axis โดยตั้งใจ = ห้ามเลือกบางบรรทัด (supersede ต้องยอดเท่าต้นทาง);_
_hint ของ target "ใบกำกับภาษี" ชี้ทางไปตัวเลือกหัวรวม; รอบ 26b: dropdown เป็นตัวควบคุมเดี่ยว —_
_ซ่อนกล่องติ๊กซ้ำ "💰 ลูกค้าจ่ายเงินแล้ว" + "🧾 ขายเงินสด" เมื่อ dropdown แสดง (โหมด_
_ตั้ง flag ให้เอง; บริษัทไม่จด VAT ที่ไม่มี dropdown ยังใช้กล่องเขียวเดิม; reset ปลดติ๊ก_
_เฉพาะ "ชนิดเอกสารใช้ไม่ได้จริง" ไม่อิง display — กันล้าง flag ที่โหมดเพิ่งตั้ง) ·_
_`receipt_only` เป็นโหมดรับเงินในตัว (paid:true — ใบเสร็จ = หลักฐานรับเงิน ม.105;_
_เดิมเลือกหัวใบเสร็จได้โดยไม่บันทึกรับเงิน = กระดาษ/บัญชีขัดกัน) + hydrate ใบ declined_
_เดิม sync จะ arm paid ให้ตรง hint (กัน silent no-op) · hint โชว์บรรทัด "💰 เงินเข้า:_
_<ช่องทาง>" สำหรับโหมด paid/cash และ refresh เมื่อเปลี่ยนแหล่งเงิน; รอบ 26: โหมด `tax_paid` "ใบกำกับภาษี/_
_ใบเสร็จรับเงิน — รับเงินครบแล้ว" สำหรับใบที่แปลงจากขายเครดิต (INV/BN → TIV เท่านั้น;_
_standalone ใช้ tax_receipt/3-in-1 เดิม): ตั้ง fPaidOnIssue → chain อนุมัติ (Supersede_
_กลับ JE ใบต้นทาง) + บันทึกชำระเต็มยอด (backend หัก WHT งวดปิดยอดอัตโนมัติ =_
_remainingCap; ไม่ออกใบเสร็จแยก → ServedAsReceipt จัดหัวรวม) · ปลดล็อก paid-chain_
_ให้ทำงานตอน "แก้ไขร่าง/ใบถูกปฏิเสธ" ด้วย (เดิม !editingId เท่านั้น ⇒ ใบแปลงติ๊กจ่าย_
_แล้วกดอนุมัติ = silent no-op ไม่บันทึกชำระ; revision ใบอนุมัติแล้ว/สถานะไม่รู้ ไม่ยิง_
_fail-safe) · สลับออกจากโหมด paid → ปลดติ๊กที่โหมดตั้งให้ (ติ๊กมือผู้ใช้ไม่แตะ) ·_
_doConvert INV/BN→TIV เปิดฟอร์มร่างทันที + hint ใน convert modal บอกทางเลือก 2 แบบ ·_
_ยืนยันพฤติกรรมลบร่างใบแปลง: DeleteDocument (Draft, no JE/payment/e-Tax) = hard delete_
_→ ใบต้นทางไม่ถูกแตะ (supersede เกิดตอน approve เท่านั้น) + guard กันแปลงซ้ำ/consumption_
_มองไม่เห็นแถวที่ลบ → แปลงใหม่ได้ทันที; รอบ 25: dropdown "เอกสารที่จะออกให้ลูกค้า"_
_6 ตัวเลือกแทน 4 checkbox (รวม 3-in-1 ใบแจ้งหนี้/ใบกำกับภาษี/ใบเสร็จรับเงิน — ตั้ง_
_combined+paid ให้ chain อนุมัติ+ชำระรันเอง) · sync `_validConversions` frontend ให้ตรง_
_backend ValidConversions (เดิม drift หลายรุ่น: PO→Expense เลิกแล้ว, ขาด GRN/PV/Receipt/_
_CertInLieu ทั้งชุด ⇒ ปุ่มแปลงหาย/ตัวเลือกผี) + comment ชี้ mirror สองทิศ · openConvert_
_ใช้ API getConversionTargets เป็นหลัก map เป็น offline fallback · รอบ 24: UX audit ปุ่ม/ป้าย/ฟอร์มทุกชนิด×สถานะ —_
_ปุ่มรับเงิน/ตัดหนี้สูญ เพิ่ม type gate (เดิมโผล่บน QT/DN/PO/PR/CN) · แปลงเอกสารกรองชนิด_
_ที่มีปลายทางจริง + เปิดตอน PartiallyPaid/Paid/Overdue · Rejected แก้ไขได้ (backend ปลด_
_พร้อมกัน — ใบตีกลับยังไม่ posted) · e-Tax/อีเมล รวม Overdue + ส่ง PO ให้ vendor ได้ ·_
_AgingDays นับจาก DueDate ไม่ใช่ DocumentDate (ป้าย "ค้างชำระ" เดิมโกหกใบเครดิตยาว) ·_
_banner ค้างชำระมีปุ่มส่งทวง · confirm รับเงิน INV มี VAT เป็น 2 ขั้น (dismiss = ยกเลิกจริง_
_ไม่ใช่เลือกไม่ออกใบกำกับ) · detail: escape description (XSS), แถวส่วนลดท้ายบิล/หักมัดจำ,_
_ซ่อน WHT -0.00, ส่วนลดบาทไม่โชว์ 0% · badge เคลม ภ.พ.30 ไม่ขึ้นคู่ "รอใบกำกับ" ·_
_stale เงียบเมื่อ aging โชว์ · decline-TIV เช็คจด VAT · PR/PO ปิดคอลัมน์เคลม VAT ·_
_dialog void ไม่พูดถึง Reversal JE เมื่อใบยังไม่ posted) ·_
_รอบ 23: ลงมือตาม roadmap task force —_
_P0: audit hash canonical เดียว + เทสต์ round-trip · WHT GL นับ reversal ถูก · lockout key ·_
_prod config (ลบ placeholder JWT, PG VerifyFull). P1: CMS XSS allowlist sanitizer +_
_ถอด unsafe-eval · idempotency ย้ายขึ้น PostgreSQL + กัน race ด้วย InFlight ·_
_BusinessRuleException หยุด framework error รั่วถึง client. P2: §65 ตรี 8→16 กลุ่ม_
_อนุมาตรา + เลิก over-add-back (cap=0, VAT เคลมได้) · tax point §78/2 นำเข้า ·_
_แยก CitAmount ออกจาก TotalTaxWithheld · เปิด job §82/3 ที่เขียนไว้แต่ไม่มีใครเรียก) ·_
_รอบ 22: audit 3 ทีม (คำแปล/adversarial/_
_process) — เนื้ออีเมล+LINE ตามภาษาใบ · แนบ PDF อีเมล manual ที่หายไป · ลิงก์ LINE_
_จริงแทน example.com · e-Tax by Email on-demand ใช้ renderer รวม · RTGS fuzz 29 เคส_
_+ แก้ เเ/ไทย/ฤๅ · รอบ 21: ออกเอกสารเป็นอังกฤษ "เฉพาะใบเดียว" —_
_เติมทางเข้า UI 2 ชั้นที่ resolver รองรับอยู่แล้วแต่กดไม่ได้ (รายใบ `#fDocumentLanguage` +_
_ครั้งเดียว `#pdfLangMode`) · แก้ `DocumentResponse` ไม่คืน `DocumentLanguage` (เปิดแก้แล้ว_
_ภาษาหาย) · แก้ metadata Title ของ PDF/A-3 คำนวณภาษาเองข้าม resolver กลาง ·_
_รอบตาม: สืบทอดภาษาใบต้นทาง 5 ทาง — convert/clone/settlement receipt/CN คืนมัดจำ/_
_recurring generator · รอบตาม 2: ภาษาเริ่มต้นรายผู้ติดต่อ `Contact.DocumentLanguage` —_
_ประทับตอนสร้างใบ ไม่ resolve ตอนพิมพ์ · ฟอร์ม+server fallback คู่ขนานแบบเครดิตเทอม ·_
_รอบตาม 3: ชื่อ/ที่อยู่บริษัทภาษาอังกฤษ — NameEn เป็นชื่อหลักโหมด en, AddressEn ใหม่_
_+ ThaiRomanizer ถอด RTGS อัตโนมัติเมื่อไม่ได้กรอก, ที่อยู่ลูกค้าถอดอัตโนมัติ,_
_ปุ่มแปลงในตั้งค่า) ·_
_รอบ 20: เอกสารภาษาอังกฤษ — HTML renderer_
_ฝังคำไทยตาย 20 จุดทั้งที่ label มีครบและ QuestPDF ใช้อยู่ (เอกสารปนไทย + หน้าตาต่างกัน_
_ตาม renderer) แก้ครบ + เทสต์กันซ้ำ · เปิด ValidateOnBuild ทุก environment + CI ตรวจ_
_วงกลม DI) · รอบ 19: เหตุผลใบเพิ่มหนี้ §86/9 ครบวงจร_
_(enum+บังคับก่อนอนุมัติ+พิมพ์บนกระดาษ+สต๊อกเฉพาะ "ส่งสินค้าเกิน") · แก้วงกลม DI ที่_
_ทำให้ start ไม่ขึ้น + เพิ่ม tools/di_cycle_check.py) · รอบ 18: เครดิต NextAcc ท้ายเอกสารเฉพาะ_
_แพ็กเกจที่ราคารายเดือน = 0 (เกณฑ์เดียว ครอบทดลองใช้+ฟรีตลอดชีพ) — ไม่ดูธง_
_IsPermanentFree/ประวัติการจ่ายเงินเลย · ต่อสายครบทุก renderer · ไม่แปะแบบฟอร์มราชการ)_
_· รอบ 17: OCR ใบราคารวม VAT เก็บ Line.Amount_
_เป็นยอดรวม VAT ⇒ JE เดบิตเกินเครดิตเท่ายอด VAT ("บันทึกบัญชีไม่สมดุล") · คำเตือน_
_หัก ณ ที่จ่ายเลิกยิงใส่ใบซื้อสินค้า) · รอบ 16: ใบเสร็จตัดลูกหนี้ต้องตัดยอดก่อนหักภาษี_
_(เกณฑ์ Cash) — เดิมตัดยอดสุทธิ เหลือลูกหนี้ค้าง = WHT และเครดิต 11910 หายไป;_
_+ guard ตอนอนุมัติ + ตัวตรวจย้อนหลัง + UI เลือกเกณฑ์ที่ ตั้งค่า → ภาษี)_
_· รอบ 15: ใบต้นทางที่ผูกไว้แสดงตอนเปิดแก้ไข —_
_ปักหมุดก่อนตัวโหลด async, เติม option กลับเมื่อใบต้นทางชำระครบจนหลุดลิสต์, ล็อก+บอกเหตุ)_
_· รอบ 14: §90/2 ปิดทุกทางที่ไปถึงใบกำกับภาษี_
_เมื่อบริษัทไม่จด VAT — แปลงเอกสาร/แปลงทั้งหมด/ติ๊กใบเดียว เดิมเปิดโล่งจนไปตายตอนอนุมัติ)_
_· รอบ 13: "เทมเพลตเริ่มต้นของชนิดนี้"_
_ให้ server ตัดสินที่เดียว — หน้าเทมเพลตเคยเดาเอง (ไม่ดู isActive + ตกมาที่ใบแรก)_
_จึงแก้คนละใบกับที่ปุ่มตั้งค่าเขียนลง + ส่ง ""/0 เพื่อให้ล้างค่าได้จริง)_
_· รอบ 12: ลำดับที่มาเทอมชำระเงิน 4 ชั้น —_
_ประทับ autoSrc + เทียบลำดับจริง (เดิมใครมาถึงก่อนชนะ: AI แซงค่าของลูกค้าได้),_
_ข้อความบนกระดาษปรับตามวันเครดิตที่ใช้จริง, ครอบฝั่งซื้อด้วย, + fallback ฝั่ง server)_
_· รอบ 11: `RecheckRdComplianceAsync` +_
_`POST /ocr/documents/{id}/recheck-compliance` — ผลตรวจ RD เคย persist ตอนสแกน_
_ครั้งเดียว คำเตือนจาก validator รุ่นเก่าจึงค้างถาวรแม้กระดาษครบ (เคส: เลขผู้ซื้อ_
_อยู่ใน raw text แต่ OCR แยกช่องไม่ได้) → เปิด detail ของใบ OCR ที่ยังติดเตือน_
_ระบบตรวจซ้ำเบื้องหลังหนึ่งครั้งแล้วรีเฟรชเมื่อผลเปลี่ยน)_
_· รอบ 10: audit ช่องกรอกครั้งที่ 2 — แหล่งเงิน_
_เฉพาะใบที่เงินเคลื่อน · CertInLieu ตัดทุกอย่างที่อิงใบกำกับ (ไม่มีใบกำกับโดยนิยาม) ·_
_บริการตปท. §83/6 เฉพาะฝั่งซื้อ · เหตุผล CN + ข้อมูลใบรับรองย้ายขึ้นโซนบน)_
_· รอบ 9: ลิสต์ใบต้นทาง CN/DN โหลดทั้ง_
_สองฝั่งเป็น optgroup — ใบที่เลือกกำหนดฝั่ง ไม่ใช่ radio กรองลิสต์; กล่องใบอ้างอิง_
_(CN/DN + ใบเสร็จตัดใบค้าง) ย้ายขึ้นต่อจากชนิด+ผู้ติดต่อตามลำดับงานจริง)_
_· รอบ 8: ช่องกรอกต่อชนิดเอกสาร —_
_`_docFieldProfile` คุมวันเครดิต/เงื่อนไข/วันครบกำหนด/เลขจอง ตามชนิด + ป้ายวันที่_
_ตามบริบท (ยืนราคาถึง/กำหนดส่งมอบ) ตรงกัน ฟอร์ม-detail-PDF + server normalize)_
_· รอบ 7: WHT credit ครบ 5 เฟส (แถวในตาราง_
_อ้างอิงเคยค้างป้าย 📋 ทั้งที่สร้างแล้ว) + OCR 50 ทวิ อ่านประเภทเงินได้ป้อน `CheckRate`_
_+ อัตราพิเศษ โฆษณา/ขนส่ง/ปันผล ต้องตรวจก่อน 40(8) · ซ่อมตารางที่ถูกยุบเป็นบรรทัดเดียว_
_ด้วย literal `\\n`) · รอบ 6: ย้ายฝั่ง CN/DN ด้วยการกลับ JE,_
_กฎ RD ตามชนิดเอกสาร, CSP เปิด blob: ให้ iframe ดู PDF แนบ) · รอบ 5: audit เทมเพลตครบทุกชั้น —_
_ขนาด/แนวกระดาษเข้า QuestPDF, ข้อความชุด EN ถูกใช้จริง, สรุป schema ที่ยังไม่มีใครใช้)_
_· รอบ 4: ติ๊กเทมเพลต → กระดาษ —_
_payment terms ขึ้น preview, ปลุก 4 ติ๊กที่ตายทั้งสอง renderer, §86/4 สาขาบังคับ)_
_· รอบ 3: ThaiAddressFormatter —_
_ตัวประกอบที่อยู่ตัวเดียวของระบบ ที่อยู่บนเอกสารราชการมี ต./อ./จ. ครบทุกเส้นทาง)_
_· รอบ 2: เทมเพลตเอกสาร response_
_ครบทุก field — ติ๊กในหน้าปรับแต่งตรงกับ PDF จริง; เทอมชำระเงินหลายข้อ +_
_ลำดับ default ผู้ใช้>ลูกค้า>เทมเพลต) · รอบ 1: per-line account side guard:_
_`EnsureLineAccountMatchesDocSide` create/update + `RevenueLegAccountId` JE_
_safety net + datalist แยกฝั่งใน documents.html — แก้ "ทำใบเสนอราคาแล้วเจอ_
_ผังค่าใช้จ่ายตอนเพิ่ม item / JE Cr รายได้เข้า 5xxxx") ก่อนหน้า: 2026-07-31_
_(audit ทีมคิดเคส/ทีมทดสอบ 65 เคส →_
_แก้ 43 บั๊ก 3 ชุด: CN/DN text-ref resolve+undue VAT accounts+GRN block+qty cap+_
_FX rate+refund txn; ภ.พ.30 regen snapshot ticks+double-tick guard+warn-line_
_guard+CF นอกลูป+pastWindow ตามงวด+void→exclude+credit CF on file+deferral เป็น_
_บรรทัด+override exclude; e-Tax purpose code ตาม reason+original หัก CN ก่อนหน้า;_
_rounding: exempt -1 passthrough, r2 midpoint, billdisc clamp, partial-convert_
_ratio ex-VAT; subscription: trial expiry scheduler, soft-delete restore,_
_aggregate stale counters; OCR: checksum guard, negation suffix, abbrev→ปิด_
_IsVatClaimable, สาขาผู้ขายจาก contact; branch label เฉพาะนิติบุคคล) —_
_รอบ 13-14: OCR API=web UI,_
_DRAFT- placeholder, แหล่งเงิน 3-layer + Reclassify, ประกันสังคมครบวงจร,_
_floor 1,650, กท.20ก, สปส.1-03/6-09._
_รอบ 15: §82/3 block+reclassify, §82/5(6) car/fuel, §81/1 VAT-reg warning,_
_PII encrypt+ (Bank/SSN), audit-log DB trigger, §86/4 hard-block opt-in,_
_ภ.ง.ด.51 SME bracket, PDPA Wave 3 (RoPA/Consent/PiiAccessLog/Breach)._
_รอบ 42: ผู้ซื้อไม่ประสงค์รับใบกำกับภาษี — per-doc checkbox_
_`BuyerDeclinedTaxInvoice` (นอกจาก walk-in contact) ยกเว้น §86/4 gate +_
_downgrade หัว TaxInvoice → "ใบเสร็จรับเงิน"; branch code (§86/4) บังคับเฉพาะ_
_ผู้ซื้อนิติบุคคล บุคคลธรรมดาไม่บังคับ (ประกาศอธิบดีฯ 199). VAT → ภ.พ.30 ครบ._
_รอบ 43: §86/4 smart-lock — ไม่ block ทางตัน: บุคคลธรรมดา/ไม่มีเลขภาษีที่ข้อมูล_
_ไม่ครบ → auto ตั้ง BuyerDeclinedTaxInvoice (ออกเป็นใบเสร็จ, VAT ครบ); นิติบุคคล_
_→ block + ชี้ทางออก (เติม/ติ๊กไม่รับใบกำกับ). + ตราประทับบริษัท (company seal):_
_อัปโหลด `/settings/stamp` → `CompanySettings.StampPath` + ขนาด/ตำแหน่ง_
_(StampWidthMm/HeightMm/Align); ประทับในโซนลายเซ็น **เฉพาะเอกสารที่อนุมัติแล้ว**_
_(เงื่อนไขเดียวกับช่องผู้อนุมัติ) ทั้ง PDF native + HTML preview._
_รอบ 77: integration Expense/PV รองรับ `AutoApprove` (default true = เดิม). false =_
_สร้าง Draft: ไม่ลง GL + ไม่ออก 50 ทวิ ตอน create (เดิม hardcode Approved + JE +_
_50 ทวิ เสมอ). อนุมัติภายหลังผ่าน `ApproveDocumentAsync` → post JE + ออก 50 ทวิ._
_เพิ่มเงื่อนไข 50 ทวิ ตอน approve ให้ครอบ "จ่ายเต็มแล้ว" (BalanceDue<=0+PaidAmount>0)_
_ไม่ใช่แค่ Status Paid — กันใบ PV Draft ที่จ่ายแล้วมาอนุมัติทีหลังไม่ออก 50 ทวิ._
_backward-compat: ผู้เรียกเดิมไม่ส่ง AutoApprove = true เหมือนเดิม._
_รอบ 76: gate ภาษีซื้อ (§82/5) — ใบที่ "ขอเครดิตภาษีซื้อ" (HasTaxInvoiceReference)_
_ต้องให้ Contact ผู้ขายมีเลขภาษี 13 หลัก มิฉะนั้น **block อนุมัติ (hard, ไม่มี_
_acknowledge bypass)** ใน `ApproveDocumentAsync` — เคลมภาษีซื้อโดยผู้ขายไม่มีเลข_
_ภาษี/ไม่ใช่ใบกำกับเต็มรูป = §82/5(1)(5) โดนประเมินคืน. + frontend guard: เลขผู้เสีย_
_ภาษีผู้ขายต้อง 13 หลัก + ตรงกับ contact ที่เลือกก่อนบันทึก (เตือน+บล็อก). หมายเหตุ:_
_backend เคลมด้วย Contact.TaxId จริง (field ผู้ขายบนฟอร์มเป็น display อ่านจาก contact)._
_รอบ 75: FormatBranch ทนทานขึ้น — (1) รหัสสาขาจริง + ชื่อสาขา default "สำนักงานใหญ่"_
_ไม่ต่อท้าย "(สำนักงานใหญ่)" ที่ขัดกัน; (2) รหัสสาขา=00000 แต่ "ชื่อสาขา" เป็นเลขล้วน_
_(เช่น 00001 — กรอกผิดช่อง) → แสดง "สาขาที่ 00001" (regex เลขล้วนกัน false-positive_
_เช่น "สำนักงานใหญ่ ชั้น 5"). หมายเหตุ: ถ้า รหัสสาขา=00000 จริง (ชื่อ="สำนักงานใหญ่")_
_เอกสารโชว์ "สำนักงานใหญ่" ถูกต้องตามกฎหมาย — ต้องตั้ง รหัสสาขา=00001 ที่ entity._
_รอบ 74: เลขที่ 50 ทวิ เพิ่มเดือน — `WHT-{ปี}-{run}` → `WHT-{ปี}{เดือน2หลัก}-{run}`._
_เดิม manual (`CreateAsync`) รันต่อปี (WHT-2026-0006) แต่ auto (`AutoGenerate`)_
_รันต่อเดือน (WHT-202607-0006) — ไม่สอดคล้อง. แก้ manual ให้ใส่เดือน (TaxMonth ที่_
_ผู้ใช้ระบุ = เดือนภาษี) รันต่อเดือน; auto เปลี่ยนฐานเดือนจาก UtcNow → เดือนของ_
_paymentDate (tax month) ให้ตรง TaxMonth ในใบ. เลขเก่าไม่ชนกัน (prefix ต่างกัน)._
_รอบ 73: 50 ทวิ — แก้ที่ **client-side renderer** ด้วย (wht.html สร้าง HTML print_
_เองใน JS ไม่ผ่าน C# BuildWithholdingTaxCertHtml). รอบ 72 แก้แค่ 2 renderer ฝั่ง_
_C# (HTML+native) → ปุ่ม "พิมพ์/บันทึก PDF" ที่ผู้ใช้เห็นยังขึ้น 2 ฉบับ+ป้ายเดิม._
_แก้ buildCopy 1,2 → 1,2,3 + copy-label บรรทัดที่ 3 + ป้ายลงชื่อ → "ผู้มีหน้าที่_
_หักภาษี ณ ที่จ่าย" ใน wht.html. **บทเรียน: 50 ทวิ มี 3 renderer** (C# HTML,_
_C# native, JS ใน wht.html) — แก้ต้องครบทั้งสาม._
_รอบ 72: 50 ทวิ — (1) ป้ายลงชื่อเปลี่ยน "ผู้จ่ายเงิน" → "ผู้มีหน้าที่หักภาษี ณ ที่จ่าย"_
_(ตรงช่องลงชื่อ ทั้ง HTML + native; ป้าย "ผู้จ่ายเงิน" ในแถวเงื่อนไข (1)หัก/(2)ออกให้_
_คงเดิม—เป็นฟิลด์ทางการ). (2) เพิ่ม "ฉบับที่ 3 (สำหรับผู้หักภาษี ณ ที่จ่าย เก็บไว้เป็น_
_หลักฐาน)" — จากเดิม 2 ฉบับ (ผู้ถูกหัก ใช้แนบ/เก็บ) เป็น 3 ฉบับ; loop native 1→3,_
_BuildCopy(3) ฝั่ง HTML, copy-header เพิ่มบรรทัดที่ 3 ทั้งสอง renderer._
_รอบ 71: 50 ทวิ — เพิ่มสาขาต่อท้ายชื่อผู้จ่าย/ผู้รับ (inline, ไม่กระทบ layout ฟอร์ม)._
_เฉพาะนิติบุคคล (เลขภาษี 13 หลักขึ้นต้น 0) ผ่าน `CertBranchSuffix` — บุคคลธรรมดา_
_(ภ.ง.ด.3) ไม่มีสาขา คืนค่าว่าง. 50 ทวิ ไม่บังคับช่องสาขาตามกฎหมาย (คนละกรณี §86/4)_
_เพิ่มเพื่อความครบถ้วนในการระบุตัว (ช่วย ภ.ง.ด.53). ครบทั้ง HTML + native cert renderer._
_รอบ 70: แสดง "สาขา/สำนักงานใหญ่" บนเอกสาร (§86/4 + ประกาศฯ 199). เดิมหัวเอกสาร_
_ไม่แสดงรหัสสาขาเลย. เพิ่ม `FormatBranch(code, name, lang)`: 00000/ว่าง = "สำนักงานใหญ่",_
_อื่น = "สาขาที่ {code}" (+ชื่อสาขา). แสดงต่อท้ายเลขผู้เสียภาษีทั้งบริษัท (ผู้ออก) +_
_คู่ค้า ทั้ง HTML + native renderer. ตอบคำถามผู้ใช้: 00000 ต้องเป็น "สำนักงานใหญ่"_
_(ถูกต้องตามกฎหมาย) ไม่ใช่ "สาขา 00000"._
_รอบ 79 (UX สร้างมัดจำ — discoverability + กันพลาด): (1) เพิ่ม pseudo-type
"💰 ใบมัดจำ / รับเงินล่วงหน้า" ใน dropdown ประเภทเอกสาร (ฝั่งขาย) → save map เป็น
`Receipt` + `isDeposit=true` อัตโนมัติ (pattern เดียวกับ CombinedInvoiceTaxInvoice
ที่ map → TaxInvoice); onDocTypeChange ติ๊ก IsDeposit + โชว์ depositOptions ให้เลย
(เดิมต้องรู้เองว่า "เลือกใบเสร็จ → ติ๊ก checkbox"). (2) guard ตอน save: บรรทัดใด
เลือกผัง 215xx/217xx (ขายรอรับรู้/รับล่วงหน้า) แต่ไม่ได้ตั้งเป็นเอกสารมัดจำ → เตือน
(GL เข้า 217xx แต่ subledger มัดจำไม่รู้จัก = "มัดจำไร้เอกสาร" ที่ Realize/หัก/drives
ไม่เจอ) แนะนำเลือกประเภทใบมัดจำ. flag `IsDeposit` (ไม่ใช่ผังบัญชี) คือตัวคุมทุกกลไก
มัดจำ. frontend เท่านั้น._
_รอบ 78 (หัก "JV มัดจำที่ไม่มีเอกสาร" ได้): มัดจำที่ integration post ตรงผ่าน
`/integration/journals` (ไม่มี SourceDocumentId) เดิมหักเข้าใบแจ้งหนี้ไม่ได้ผ่าน UI
(ApplyDepositToInvoiceAsync ต้องมีใบมัดจำ Document). เพิ่ม:
(1) `SearchJournalDepositsAsync` — ค้น JE Posted, ไม่มี source doc, ยังไม่ apply,
มีขา Cr 215/217, filter ด้วย query (EntryNumber/Reference/Description contains) →
`GET /document/journal-deposits?q=`. (2) `ApplyJournalDepositToInvoiceAsync` —
อ่านขา Cr จริงของ JV (215/217 + 21913/21911) → post JV ตัดชำระ: Dr บัญชีเดิม +
Dr VAT / Cr ลูกหนี้ (gross) + ลด BalanceDue + mark `JV.DepositAppliedToDocumentId`
(one-shot, หักเต็ม JV; gross>ยอดใบ → block) → `POST /document/{id}/apply-journal-deposit`.
(3) frontend: กล่องค้น JV ในฟอร์ม (booking auto-fill) → editing หักทันที / creating
หักหลัง approve. GL-critical v1 — verify Windows._
_รอบ 77 (หักมัดจำได้ในฟอร์มสร้างเอกสารเลย): เดิมตอนสร้างใหม่ banner มัดจำคงค้าง
บอกแค่ "บันทึกใบก่อน แล้วเปิดแก้เพื่อหักมัดจำ" (2 ขั้น). เพิ่ม selector ในฟอร์ม
(checkbox + เลือกใบมัดจำ + ยอด, auto-select ใบ booking ตรงกัน) → เก็บ
`_pendingDepositApply` → `save()` หลัง approve สำเร็จเรียก `applyDeposit` อัตโนมัติ
(ApplyDepositToInvoiceAsync ที่ verified) = create+approve+หักมัดจำ ใน action เดียว.
cap ยอดไม่เกินคงเหลือ (UI guard). ต้อง "บันทึกและอนุมัติ" (บันทึกร่าง → หักไม่ได้
เพราะ ApplyDeposit ต้องเอกสาร approved). fail-soft: หักไม่ผ่าน → ใบยังอยู่ หักเอง
ได้. + แก้บั๊กเดิม: banner ใช้ `deposit.id` (DepositSummary) แทน `.depositDocumentId`
ที่ไม่มีจริง (quick-apply เคย pass undefined). frontend เท่านั้น ไม่แตะ GL logic._
_รอบ 76 (display "อ้างอิง" = เลขจอง ไม่ใช่ dedup key): integration เก็บ externalRef
(REC260718006 = dedup key ภายใน) ลง `Document.Reference` → PDF/หน้าเอกสารโชว์เป็น
"อ้างอิง" ทำให้ลูกค้า/บัญชีเห็นเลขใบเสร็จ TakeTime แทนรหัสจอง. เพิ่ม
`Document.DisplayReference` (NotMapped: `BookingNumber ?? Reference`) → PDF (5 จุด
DocumentRenderer + HTML) + frontend detail (2 จุด, ใช้ `bookingNumber || reference`)
render จากนี้. RES-id (BookingNumber) มีความหมายกับคน → โชว์แทน; company doc ไม่มี
BookingNumber → คืน Reference เดิม. **display เท่านั้น — Reference field ยังเก็บ
externalRef สำหรับ dedup/idempotency + resolve มัดจำ (รอบ 75) ไม่กระทบ**._
_รอบ 75 (depositAppliedRef รับ external ref — root cause ที่ทำ degrade เสมอ):
หลักฐานจากใบทดสอบจริง (TIV-20260718-0001): TakeTime ส่ง `depositAppliedRef` เป็น
**เลขใบเสร็จของเขาเอง** (REC260713008) แต่ resolver จับคู่เฉพาะ `DocumentNumber`
ของ NextAcc (REC-20260713-xxxx) → หาไม่เจอ → PostCashSale throw → **degrade เป็น
ตั้งหนี้เสมอ** แม้ deploy แล้ว → TakeTime fallback settle (มัดจำเป็น payment ใหม่
= เงินสด/มัดจำเบิ้ล + REC settlement งอก). แก้ 3 จุด (single lock-lookup / multi
loop / UnrealizeDrivesDeposit): จับคู่ `DocumentNumber` ก่อน → ไม่เจอ → จับคู่
`Reference` (= externalRef ที่ integration stamp ตอนสร้างใบมัดจำ). เลขจอง
(BookingNumber) ไม่เกี่ยว — `อ้างอิง` บนใบ = externalRef ตาม contract idempotency._
_รอบ 74 (purge สมมาตร void — กัน 21510 สะสมติดลบจาก recreate ทับ): TakeTime เจอ
21510 ติดลบ −934.58 จากการ resync (ลบ+สร้างใหม่) ซ้ำหลายรอบ. VoidDocumentAsync
มี step 2b (กลับ JV ตัดชำระด้วยมัดจำ + คืน subledger ใบมัดจำ) แต่ **PurgeDocumentAsync
ไม่มี** → hard-delete ใบกำกับแล้ว JV ApplyDeposit (SourceDocumentId=ใบมัดจำ, หลุด
step 1 ที่ลบเฉพาะ JE sourced จากใบนี้) ค้าง → มัดจำถูกตัด 217xx/21913 ถาวร +
subledger ไม่คืน → recreate ทับ → Dr 21510 สะสม. เพิ่ม purge step 0c (mirror 2b
แบบ delete): ลบ JV ApplyDeposit ที่ Reference=เลขใบนี้ (คัด Cr 113) + คืน subledger
มัดจำ (RealizedAmount/RecognizedAt/AppliedTo). **หมายเหตุ: root cause ที่ TakeTime
เจอคือ env ยังไม่ deploy branch นี้ — ทั้ง isCashSale + step 2b/0c ยังไม่ทำงานที่นั่น
→ ทุก resync สะสม. deploy = หยุด churn + delete สมมาตร**._
_รอบ 73 (มัดจำหลายใบ/ใบกำกับ — blocker โรงแรม): `driveDeposit` เดิม resolve
`depositAppliedRef` เป็นเลขเดียว (exact match) → comma-separated หาไม่เจอ →
degrade เป็น AR. เพิ่ม: split `depositAppliedRef` ด้วยจุลภาค — ถ้า >1 เลข →
loop **reverse ทุกใบเต็มยอดคงเหลือ** (GL-driven ต่อใบ: Dr 215xx/217xx + 21913/
21911 ที่แต่ละใบ Cr ไว้จริง, mark ใบมัดจำ realized เต็ม + one-shot guard ต่อใบ +
row-lock). ผลรวม Dr = `depositAppliedAmount` (ยอดรวมที่ส่งมา) → cashAmt (Total −
รวม) สมดุลพอดี; ไม่ตรง → AutoPost balance check throw → degrade (ปลอดภัย).
**เลขเดียว → else = logic เดิมไม่แตะ (zero regression)**. contract TakeTime: คง
comma-separated `depositAppliedRef` + `depositAppliedAmount`=ผลรวม, แต่ละใบถูก
consume เต็ม (semantic checkout โรงแรม). ⚠️ GL-critical — Windows GL test เคส
2+ ใบก่อนเปิด._
_รอบ 72 (ลบ+resync ให้สะอาด — ใบเสร็จ REC ลอยค้าง): ผู้ใช้ลบใบกำกับเก่าที่มี
ปัญหาเพื่อ resync ใหม่ แต่ **PurgeDocumentAsync เดิม step 7 แค่ NULL
RelatedDocumentId ไม่ได้ลบใบเสร็จ settlement (REC)** → REC ลอยค้างใน list. แก้:
(1) purge เพิ่ม step 6c — cascade ลบ settlement receipt (IsSettlementReceipt +
RelatedDocumentId==ใบนี้) พร้อม e-Tax/line ก่อน NULL ref; (2)
`BulkCleanupController`: `GET /cleanup/orphaned-settlement-receipts` (diagnostic,
API key อ่านได้) + `POST .../purge` (Owner soft-delete) — ล้าง REC ที่ orphan
อยู่แล้วจากการลบก่อนหน้า (RelatedDocumentId NULL หรือต้นทาง Voided). settlement
receipt ไม่มี JE ของตัวเอง (payment ถือ JE, ถูกลบไปกับใบกำกับ) → ลบปลอดภัย
ไม่กระทบ GL. วิธี resync สะอาด: ลบทั้ง group (มัดจำ+ใบกำกับ+REC) → resync มัดจำ
fresh + ใบกำกับ isCashSale อ้าง depositAppliedRef ใหม่._
_รอบ 71 (กันยอดเบิ้ลจากชำระซ้ำ — root cause ที่ผู้ใช้เจอ): `ProcessPaymentAsync`
(integration payment endpoint) เดิมมีแค่ idempotency-by-reference — **ไม่มี**
status guard/over-pay cap → ยิง payment ส่วนมัดจำแยก = Dr เงินสด/Cr ลูกหนี้ ซ้ำ
กับที่มัดจำ+ใบกำกับลงไปแล้ว → เงินสด/มัดจำนับซ้ำ + สร้าง REC settlement เยอะ.
เพิ่ม guard: เอกสาร `IssuedAsCashReceipt` (ขายเงินสด settle ในตัวแล้ว) หรือ
`Status=Paid`/`BalanceDue≤0` → skip ไม่รับชำระภายนอก; over-pay (Amount>คงค้าง)
→ throw พร้อมชี้ให้ใช้ `depositAppliedRef` (drives) แทนการยิง payment แยก.
(`DocumentService.CreatePaymentAsync` มี guard นี้อยู่แล้ว — เติมให้ครบฝั่ง
integration). วิธีถูก: ออกใบกำกับ isCashSale + depositAppliedRef → driveDeposit
**ดึงใบมัดจำเดิม** (กลับ 21510/21913) ใบเดียวจบ ไม่สร้าง receipt ใหม่/ไม่นับซ้ำ._
_รอบ 70 (TakeTime cash-sale — GL สะอาด ไม่มีลูกหนี้): แก้ตามที่ผู้ใช้ทัก — ขายเงินสด
B2B ต้องไม่มีลูกหนี้การค้าในการลงบัญชี. เดิม integration `isCashSale` ลงผ่าน
mapping-JE (Dr ลูกหนี้) + ApplyDeposit + settle → **AR-transit** (สุทธิ 0 แต่ footer
JE โชว์ Dr ลูกหนี้). เปลี่ยนเป็น: **(1)** ขยาย branch cash-receipt ใน
`AutoPostToJournalAsync` ให้รับ `TaxInvoice && IssuedAsCashReceipt` (sales branch
เพิ่ม `&& !IssuedAsCashReceipt` เพื่อ exclude; บังคับ `receiptSettlesAr=false`) →
reuse เส้น `driveDeposit` ที่ verified. **(2)** `IDocumentService.PostCashSaleJournalAsync`
(public wrapper: AutoPost + SaveChanges + detach-on-error + คืน JE id). **(3)**
integration isCashSale ตั้ง `IssuedAsCashReceipt=true` + `PaymentAccountId` + stamp
`DepositAppliedAmount` ทุกกรณี → post ผ่าน PostCashSale (ไม่ใช่ mapping+settle).
ผล GL: `Dr เงินสด(+217xx+21913 ถ้ามัดจำ) / Cr รายได้+21911` **ไม่มี 113 เลย** ใบเดียว.
fail-soft → degrade เป็นตั้งหนี้ (mapping JE) ให้ TakeTime fallback. ลบ
`TrySettleCashSaleAsync`/ApplyDeposit-transit path ทิ้ง. void สมมาตรผ่าน 7c เดิม.
⚠️ GL-critical + env นี้ test ไม่ได้ → **ต้อง verify GL บน Windows ก่อนเปิด**._
_รอบ 69 (ทุกเอกสาร): OWNER fallback ไม่ทับผู้อนุมัติตัวจริงอีกต่อไป. เดิม fallback_
_ทำงานทุกครั้งที่ slot 1 ไม่มี "รูปลายเซ็น" → ผู้อนุมัติจริงที่ยังไม่อัปโหลดลายเซ็น_
_ถูกแทนด้วยลายเซ็น+ชื่อ "เจ้าของ" ทุกประเภทเอกสาร (โชว์ผิดคน + แก้ชื่อผู้อนุมัติ_
_ไม่เปลี่ยนตาม). แก้: fallback ทำงานเฉพาะเมื่อ "ไม่มีผู้อนุมัติระบุเลย" (slot 1 ไม่มี_
_ชื่อ) — ถ้ามีผู้อนุมัติจริง (ชื่อ resolve สดจาก Users) คงชื่อไว้ เว้นบรรทัดลายเซ็น_
_ให้เซ็นมือ. custom signatory (opt-in) คงเดิม (ตั้งใจ fix ชื่อ — แก้ที่ Settings)._
_รอบ 68: ใบเสร็จ settlement เก่า — โชว์ชื่อผู้กดที่ถูกต้องด้วย. ชื่อ/ลายเซ็นถูก_
_resolve สดตอน render (ไม่ snapshot — ไม่มี field เก็บ HTML/PDF/ชื่อบนใบ) → ใบเดิม_
_แสดงชื่อถูกอัตโนมัติหลัง deploy. เสริม robustness: ใบ settlement ที่ UpdatedBy ว่าง_
_(ใบเก่า/บาง path) → approver ตกไปใช้ CreatedBy (= ผู้กดคนเดียวกัน) กันเว้นว่าง/เด้ง_
_ไปเจ้าของ. ไม่ต้อง migrate DB._
_รอบ 67: ใบเสร็จ settlement — ยกเว้น custom authorized signatory ด้วย (ต่อ รอบ 64)._
_รอบ 64 ข้าม owner fallback ให้ใบ settlement แล้ว แต่ **ยังไม่ข้าม custom signatory**_
_(opt-in `UseCustomAuthorizedSignatory`) ที่ override slot 1 ก่อนหน้า. `AuthorizedSignatoryName`_
_เป็นค่า "เก็บไว้" ใน CompanySettings → แก้ชื่อ user แล้วไม่เปลี่ยนตาม = อาการ "ชื่อเก่า_
_ไม่อัปเดต" + ลายเซ็นเจ้าของที่ผู้ใช้รายงานซ้ำ. แก้: เพิ่ม `!doc.IsSettlementReceipt` ที่_
_เงื่อนไข custom signatory ด้วย → ใบเสร็จ settlement ใช้ผู้กดบันทึก (อ่านชื่อสดจาก Users)_
_เสมอ ทั้ง custom + owner ข้ามหมด. ยืนยันไม่มี name snapshot ตอนสร้าง (ไม่เซ็ต PreparerName)._
_รอบ 66 (backlog F11): `RecalcVatTotals` นับ JE_INPUT เป็นภาษีซื้อ. เดิมภาษีซื้อ_
_จาก JE ล้วน (tag "JE_INPUT") ถูกเช็ค `!= "INPUT"` → หลุดไปรวมใน OutputVat +_
_หายจาก InputVat = ภาษีขายเกิน + ภาษีซื้อขาด → NetVat ผิด (นำส่งเกิน) ตอนแก้ไข/_
_finalize รายงานที่ recompute. แก้: `IsInputLine` = "INPUT" or "JE_INPUT"_
_(ตรงกับ LineSide ที่ generate ครั้งแรกถูกอยู่แล้ว — ปิด drift ระหว่าง 2 เส้นทาง)._
_รอบ 65 (backlog F5 — ปิดช่องนำส่งภาษีขายขาด): ใบแจ้งหนี้ (Invoice) ที่มี VAT_
_เข้า ภ.พ.30. เหตุ: `AutoPostToJournalAsync` ลง Cr 21911 ให้ทั้ง Invoice และ_
_TaxInvoice เท่ากัน แต่ `TaxService.GenerateVatReport` รายงานเฉพาะ TaxInvoice →_
_ใบแจ้งหนี้ที่มี VAT มีภาระภาษีขายใน GL แต่ไม่เคยถูกนำส่ง = ภ.พ.30 < GL (โดนปรับ)._
_แก้: branch output VAT รับ Invoice (VatAmount>0) ด้วย ยกเว้นใบที่ถูกแปลงเป็น_
_ใบกำกับภาษี (`supersededInvoiceIds` = Invoice ที่มี TaxInvoice child non-void_
_อ้างถึง) กันนับซ้ำ. Invoice→Receipt = settlement (Receipt child ถูก exclude ที่_
_branch เดิมอยู่แล้ว) ไม่กระทบ. **ค้าง (design)**: แปลง Invoice→TaxInvoice ที่_
_ทั้งคู่มี VAT → GL 21911 เบิ้ล (ต้อง reverse JE ใบต้นทางตอน convert) แยกแก้._
_รอบ 64: ใบเสร็จ settlement — ช่องผู้อนุมัติ = ลายเซ็นผู้กดบันทึก ไม่ใช่เจ้าของ._
_ปัญหา: กดรับเงินจากใบกำกับ/ใบแจ้งหนี้ → ใบเสร็จโชว์ลายเซ็น+ชื่อ "เจ้าของ" (Owner)_
_ไม่ใช่ผู้กด และผู้กดแก้ชื่อตัวเองแล้วไม่เปลี่ยนตาม. เหตุ: `ResolveSignersAsync`_
_มี Owner-signature fallback เมื่อ approver ยังไม่มีรูปลายเซ็น → ผู้กดที่ยังไม่ตั้ง_
_ลายเซ็นเด้งไปลายเซ็น+ชื่อเจ้าของ. แก้: fallback นี้ **ข้ามใบ IsSettlementReceipt**_
_→ ช่องผู้อนุมัติ = ผู้กด (UpdatedBy) เท่านั้น (ผ่านเช็คสิทธิ์อนุมัติแล้ว; ไม่มีสิทธิ์_
_= ใบเป็น Draft). ชื่ออ่านสดจาก Users ทุกครั้ง แก้ชื่อแล้วเปลี่ยนตามทันที._
_รอบ 63: ชื่ออาคาร (BuildingName) ขึ้นบนที่อยู่เอกสารครบ —_
_`PdfGenerationService.FormatThaiAddress` รับพารามิเตอร์ buildingName เพิ่ม_
_(เดิม structured street ประกอบจาก เลขที่+หมู่+ถนน เท่านั้น → contact ที่บันทึก_
_ชื่ออาคารไว้หายจากเอกสารพิมพ์ทุกใบ); อัปเดต call site ทั้ง 6 จุด (HTML + native_
_renderer, company + contact + 50ทวิ) + กันซ้ำเมื่อ street head จาก free-text_
_มีชื่ออาคารอยู่แล้ว + คงพฤติกรรมเดิมเมื่อมีแค่ชื่ออาคารโดด ๆ (ตกไปใช้ free-text)._
_`WithholdingTaxCertService.ComposeFullAddress` (API response) เติม buildingName_
_ใน structured fallback ด้วย. e-Tax XML มี BuildingName element อยู่แล้วทั้ง 2 ฝั่ง._
_รอบ 62: **Deposit Center** — redesign หน้าเงินมัดจำทั้งหน้า (`/pages/_
_deposit-center.html` + endpoint ใหม่ `GET document/deposit-center`): payload_
_เดียวจบ (rows + KPI + **GL tie-out** + sources + GeneratedAtUtc + build marker)_
_→ หน้ากับ GL ไม่ตรง = ฟ้องบน banner ทันที ไม่มีวันโชว์ 0 เงียบ. URL ใหม่ทั้ง_
_หน้า+API = ทะลุ cache เก่าทุกชั้น (SW/browser/CDN) ที่ทำ "แก้แล้วยังขึ้น 0"._
_เมนูชี้หน้าใหม่, หน้าเก่า redirect. mobile-first cards / desktop table, tabs+_
_ค้นหา+เรียง, VAT chip (พักรอ 21913/รายงานแล้ว), booking chip, progress bar_
_รับรู้/คืน, refresh + เวลาข้อมูลจากเซิร์ฟเวอร์, modal รับรู้/คืนเงิน (payload_
_เดิม), accordion ที่มาของตัวเลขรายบัญชี._
_รอบ 61 (ปิด backlog สูงจาก audit รอบ 60): กัน**รายได้ซ้ำ** QT/BN → Invoice_
_และ → Receipt (นับ Receipt/RV เป็น revenue-child ของ QT/BN ใน conversion guard_
_F3); **ภ.พ.30 นับ Receipt ที่แปลงจาก QT/BN** (ขายเงินสด — VAT ลง GL แต่เดิม_
_ถูก exclude เพราะมี RelatedDocumentId = นำส่งขาด F4-sales; settlement ของ_
_Invoice/TaxInvoice ยัง exclude ตามเดิม); PI อ้าง GRN + VAT ต้องห้าม → Dr VAT_
_เข้าเป็นต้นทุนตามบรรทัด (เดิม JE ไม่สมดุล F1-purchase); 50 ทวิ ลง**เดือนที่จ่าย_
_จริง** (paymentDate param F13) + auto-สร้างตอน approve เอกสารจ่ายที่จบทันที_
_(PV เงินสด/settle — เดิมไม่มี cert เลย F12); recurring ส่งต่อส่วนลดบาท/_
_IsVatClaimable/ProductCode/BillDiscount/PricesIncludeVat (D1/D2) + RunNow lock_
_+ เลื่อน NextRunDate กันออกใบซ้ำกับ cron (D3)._
_ยัง backlog (ต้อง design/เสี่ยงสูง): F5 Invoice ไม่เข้า ภ.พ.30 (VAT ใน GL ตั้งแต่_
_approve — ต้องเลือก post ตอน settle หรือรายงาน Invoice), F15 ภ.พ.36 ไม่มี JE,_
_F10 Receipt ขายสดไม่ตัด COGS/สต๊อก (เสี่ยงชน POS ที่เขียน movement เอง), A8_
_multi-warehouse, A4/A10 FIFO relayer ตอน void, A7 negative-stock enforcement,_
_F16 หัวใบเสร็จ settlement ขึ้น "ใบกำกับภาษี", F6 คอลัมน์ exempt/0%, §65ตรี ครบ_
_ทุกวงเล็บ, ใบกำกับอย่างย่อ (§86/6), F11 RecalcVatTotals JE_INPUT, F8 §82/3_
_anchor ตามงวดรายงาน, D5 convert race._
_รอบ 60 (audit ทุกประเภทเอกสาร — แก้ criticals ชุดแรก 15 จุด): void ใบเสร็จ_
_settlement ถูก block (ให้ยกเลิก payment แทน — กัน AR ติดลบ/เก็บซ้ำ B3/F8);_
_FindAccountAsync exact-match ข้าม header Level<4 (CN ซื้อเคย Cr "116"/"212"_
_header F2); CN/DN เคารพ WHT basis Cash (gross AR/AP ไม่แตะ 11910 F2-sales);_
_ห้าม CN/DN อ้างใบ Voided (§86/9-10 F9); void CN ซื้อคืน PaidAmount (Dr 212 probe_
_F3); ภ.พ.30 ไม่เคลม VAT undue 11640 (excluded line F4) + CIL ออกจาก input_
_whitelist (§82/4 F7); Overdue เฉพาะใบอนุมัติแล้ว + จ่ายใบ Overdue ได้ (F7-sales);_
_convert ส่งต่อ BillDiscount/PricesIncludeVat/ส่วนลดบาท/IsVatClaimable (F1, เฉลี่ย_
_ตาม partial); FIFO sign-agnostic + marginal-slice costing (A1/A2/A6); WAC rebuild_
_หลัง void (A5); void payment จัดการ 50 ทวิ Draft (B5); apply มัดจำ stamp_
_DepositAppliedAmount ลงใบ (E2); sensitivity ไม่โผล่ search/CSV (PDPA E3); HTML_
_scale-back ยกเว้นมัดจำ defer (F14). backlog ที่เหลือดูรายงาน audit._
_รอบ 60 (TakeTime cash-sale spec — B2B ขายเงินสด ใบเดียว จบ = e-Tax T03):
เพิ่ม `Document.IssuedAsCashReceipt` (bool, persist, migration ALTER ADD COLUMN).
IsCashSale settle สำเร็จ (BalanceDue→0) → ตั้ง flag → e-Tax **T03**
"ใบเสร็จรับเงิน/ใบกำกับภาษี" (EtaxInvoiceService docTypeCode/Name switch เพิ่ม
`TaxInvoice when IssuedAsCashReceipt`) + หัว PDF "ใบเสร็จรับเงิน/ใบกำกับภาษี"
(PdfGenerationService.ComputeDocumentTitle). ต่าง ServedAsReceipt (NotMapped,
คิดตอน render) ตรงที่ persist → คุม e-Tax type ได้ (ServedAsReceipt คุมแค่หัว).
+ InboundInvoiceRequest รับ deposit fields (DepositAppliedAmount/Ref/
OutputVatDeferred/DrivesJournal) → persist ลง Document ตอนสร้าง (รองรับ resync
+ deposit/checkout). **เคสมีมัดจำ (DrivesJournal=true):** ก่อน settle เรียก
`ApplyDepositToInvoiceAsync` (เส้น verified — Dr 217xx + Dr [21913|21911] /
Cr ลูกหนี้, กลับ deferred ของใบมัดจำ REC-xxx ที่อ้าง, ไม่รับรู้รายได้ซ้ำ) →
BalanceDue เหลือสุทธิ → settle รับแค่ส่วนต่าง → GL: Dr เงินสด(สุทธิ) +
Dr 217xx/VAT-reversal / Cr รายได้+VAT+ล้าง AR. drives ต้องมี "ใบมัดจำจริง"
(IsDeposit) — resolve จาก depositAppliedRef; ไม่พบ → fail-soft (ใบกำกับค้างชำระ
ไม่ล้ม sync). display-only mode (DrivesJournal=false): stamp DepositAppliedAmount
ที่ create เพื่อ render "หักมัดจำ/รับสุทธิ" เท่านั้น ไม่แตะ GL, settle จ่ายเต็ม.
⚠ ยัง gate ด้วย toggle ฝั่ง TakeTime (`Nexaacc_CashSale_Deposit`) จนกว่า
test GL บน Windows ผ่าน. **สมมาตร void (step 2b ใหม่ใน VoidDocumentAsync):**
JV ตัดชำระด้วยมัดจำ (ApplyDepositToInvoiceAsync) มี SourceDocumentId=ใบมัดจำ
จึงหลุด step 2 (กลับเฉพาะ JE ของใบที่ void) → เพิ่ม 2b: หา deposit ที่
DepositAppliedToDocumentId ชี้มาใบนี้ → reverse JV (คัดเฉพาะ JE ที่มีขา Cr 113
กัน realize-JE) + คืน subledger (RealizedAmount/RecognizedAt เฉพาะผู้ stamp
Dr 21913/AppliedToDocumentId) — ปิดช่อง AR ติดลบ + มัดจำถูกกลืนถาวร (ครอบ
ApplyDeposit ฝั่ง UI ที่มีช่องเดิมนี้ด้วย); เคส drives (ขา reversal ฝังใน JE
ใบเช็คเอาท์ ไม่มี JV) ข้าม 2b โดยธรรมชาติ → 7c ทำงานตามเดิม. **Self-heal
(TrySettleCashSaleAsync ใช้ร่วม 3 จุด: create / retry "Already synced" /
resyncUpdate):** create รอบแรก fail-soft → partner ยิงซ้ำหรือ resync → settle
ต่อจากขั้นที่ค้าง (มัดจำ apply แล้วข้าม — ดูจาก DepositAppliedAmount ที่ drives
ไม่ pre-stamp, ยอดปิดแล้ว → heal flag T03 อย่างเดียว); resync re-stamp deposit
fields จาก request (source of truth — guard PaidAmount==0 ผ่านแล้วจึงปลอดภัย)._
_รอบ 59 (audit จำลอง scenario — ชุดใหญ่ 15 แก้): **สมมาตร apply↔void สมบูรณ์** —_
_void/purge un-realize คิดจาก "บรรทัด JE จริงของใบเช็คเอาท์" (helper Unrealize_
_DrivesDepositAsync: depBase = ΣDr 215/217, เคลียร์ RecognizedAt เฉพาะเมื่อใบมี_
_Dr 21913 จริง) แทน field-ratio — ปิด F1 (gross drift +VAT/รอบ), F2 (void แล้ว_
_VAT ผี ค้าง ภ.พ.30), F3 (ล้าง stamp ของ RealizeDeposit ผิดใบ → 21913 ติดลบ/_
_21911 เบิ้ล). เคส A เพิ่ม guard ครบ (F4): one-shot+self-heal เหมือนเคส B +_
_over-apply เทียบ GL net/subledger + row-lock FOR UPDATE กัน concurrent (F9B,_
_ทั้งใบมัดจำและ JV+reload). purge un-realize subledger ก่อนลบ JE (F7). Apply_
_classic: over-apply guard + one-shot + คุม status PartiallyPaid + stamp_
_RecognizedAt เมื่อครบเท่านั้น (กัน 21913 ghost จาก partial). Refund: guard_
_เทียบคงเหลือจริง (หัก realized) + **ออกใบลดหนี้จริง** (§86/10) เมื่อ VAT เคย_
_ถูกรายงาน → ภ.พ.30 ลดยอดถูกต้อง. หน้า deposits: หัก refunded, clamp ≥0,_
_fallback GL net เมื่อ SubTotal=0, DTO เพิ่ม RefundedAmount. create block_
_IsDeposit+RelatedDocumentId (มัดจำต้อง standalone)._
_รอบ 69: **ปิดช่องว่าง OCR D1-D3** — (D1) **สาขาผู้ซื้อไม่เคยถูกอ่าน**: regex
"สาขาที่ …" เดิมยิงทับทั้งหน้าแล้วยัดผลเป็นสาขา **ผู้ขาย** ตัวเดียว ⇒ ลูกค้าที่
เป็นสาขาตกเป็นสำนักงานใหญ่เสมอ (รายงานภาษีขายผิดสาขา ประกาศฯ 199/§86/4) และถ้า
บล็อกผู้ซื้ออยู่บนสุดก็สลับกันอ่าน. แยกกฎออกเป็น `BranchCodeExtractor` (pure):
ตัดข้อความที่ "จุดเริ่มบล็อกผู้ซื้อ" → ก่อนหน้า = ผู้ขาย, หลัง = ผู้ซื้อ (จำกัด
หน้าต่าง 500 อักษร + ตัดที่หัวตารางรายการ กันเลขในตารางปน) · กฎอ่านค่าใช้
ฟังก์ชันเดียวทั้งสองฝั่ง · ไม่พบ = คืน null ไม่เดา 00000 · เติม `Contact.BranchCode`
ฝั่งลูกค้าทั้งตอนสร้างใหม่และ enrich ของเดิมที่ยังว่าง ·
(D2) **ใบมัดจำสแกนตรงได้แล้ว**: เพิ่ม pseudo-target `Deposit` ในหน้าสแกน (ฟอร์ม
แปลงเป็น Receipt + IsDeposit ให้เอง) + `DepositKeywordRegex` เดาให้เมื่อเราเป็น
ผู้ขายและกระดาษระบุ "มัดจำ/รับล่วงหน้า" + เส้น auto-create ฝั่ง backend ตัดสิน
`wantDeposit` ก่อน `Enum.TryParse` (ไม่งั้นตกไป fallback เป็น Expense) แล้วตั้ง
`IsDeposit=true` — เดิมต้องเลือก "ใบเสร็จ" แล้วไปติ๊กเอง ลืมติ๊ก = รับรู้รายได้
แทนหนี้สินมัดจำ 217xx ·
(D3) **กันสแกนใบเดิมซ้ำ**: `CheckDuplicateAsync` + `GET /document/duplicate-check`
สองระดับ — เลขใบกำกับผู้ขายตรงกัน+คู่ค้าเดียวกัน = **แน่นอน** (เลขนี้ไม่ซ้ำใน
ระบบผู้ขาย) · คู่ค้า+ยอด±0.5%+ช่วง ±60 วัน = น่าสงสัย · ตัด Voided/Rejected/
ลบแล้ว/ตัวเอง · หน้าสแกนถามยืนยันก่อนพาไปฟอร์ม (เตือนอย่างเดียว **ห้ามบล็อก** —
ผู้ขายขายของชุดเดิมซ้ำได้จริง false positive ที่บล็อกแรงกว่าปัญหาที่กัน).
D4 (แยกหลายใบในไฟล์เดียว) + D5 (AI second-opinion ผัง JE) ยังอยู่ใน TODO §D_
_รอบ 68: **เจตนา "รับเงินครบแล้ว" (tax_paid) ต้อง persist + 500 ต้องตามรอยได้**_
_เคสจริง: แปลง INV → เลือก "ใบกำกับภาษี/ใบเสร็จรับเงิน — รับเงินครบแล้ว" แต่_
_(ก) ใบร่างพิมพ์หัว "ใบกำกับภาษี" เฉย ๆ (ข) อนุมัติล้ม "เกิดข้อผิดพลาดภายในระบบ"._
_วิเคราะห์: โหมด tax_paid เดิมอยู่แค่ในฟอร์ม + chain ฝั่ง client (approve→_
_createPayment) **ไม่เคยบันทึกลงเอกสาร** ⇒ ปิดฟอร์ม/chain ล้ม = เจตนาหายเงียบ_
_(defect class "เก็บแล้วต้อง echo กลับ") และ resolver หัวรวมตัดสินจาก "ชำระจริง"_
_เท่านั้น จึงไม่มีทางรวมบนใบร่าง. แก้: (1) field ใหม่ `Document.PaidOnIssue`_
_(ครบ checklist B: entity + migration + Create/Update/Response + payload +_
_hydrate openEdit + reset + mapper) — เก็บเฉพาะ TaxInvoice/Invoice ·_
_(2) `ComputeServedAsReceipt` + `ResolveServedAsReceiptAsync` (mirror คู่):_
_**Draft → ใช้เจตนา** (เลข DRAFT ไม่ใช่เอกสารตามกฎหมาย — หลัก Draft PDF =_
_Approved PDF) / **หลังอนุมัติ → ใช้การชำระจริงเท่านั้น** (อนุมัติแล้ว chain_
_จ่ายล้ม ห้ามพิมพ์ "ใบเสร็จรับเงิน" = หลักฐานรับเงินเท็จ) · (3) การอนุมัติที่ล้ม:_
_toast แบบไม่มีคำนำหน้า = 500 ชนิด exception ไม่คาดคิด (middleware ปิดบังข้อความ)_
_— เพิ่ม **รหัสอ้างอิง 8 หลัก** ใน response 500 + prefix `[REF:xxxx]` ในแถว_
_ErrorLogs → เกิดซ้ำครั้งหน้าแจ้งรหัสแล้วเปิดดู exception จริงได้ทันที (แก้ blind_
_ไม่ได้เพราะ log อยู่ฝั่ง production)._
_รอบ 67: **ด่านตรวจโครงสร้าง JE ก่อนบันทึก (JournalPostingGuard) + สแกนย้อนหลัง**_
_ที่มา (เคสจริง UV-202607-0037 จาก EXP integration): Dr ค่าใช้จ่าย 17,890 +_
_Dr ภาษีซื้อ 1,252.30 / **Cr 21917 ทั้งใบ 19,142.30** — ไม่มีขาเจ้าหนี้เลย และ_
_WHT = 107% ของฐาน. JE สมดุลเป๊ะจึงผ่าน guard "Dr=Cr" เดิมทุกตัว ⇒ สมดุลไม่พอ_
_ต้องตรวจ **โครงสร้าง**. เพิ่ม `JournalPostingGuard` (pure class, ไม่มี DB/AI_
_dependency — kill-switch safe ตามกฎเหล็ก #1): JE-BAL · **JE-WHT-RATIO** (WHT_
_Cr > 15%+ε ของฐานค่าใช้จ่าย = เครดิตผิดบัญชีแน่ จับได้แม้ไม่รู้เอกสาร) ·_
_JE-WHT-DOC (≠ ยอดบนเอกสาร) · JE-VAT-OVER (VAT ใน GL เกินเอกสาร — น้อยกว่าได้_
_เพราะไม่เคลม/พักรอใบกำกับ) · **JE-NO-COUNTERPART** (ฝั่งซื้อ: Cr ที่ไม่ใช่บัญชี_
_ภาษีต้องรองรับ TotalAmount — ไม่ fix รหัสบัญชีเพื่อไม่ block แหล่งเงินถูกกฎหมาย_
_อื่น เช่น เจ้าหนี้กรรมการ/หักมัดจำจ่าย 11810) · JE-WHT-MISSING (warning). ตัวกลับ_
_ตรวจแบบ doc=null (ขาสลับโดยเจตนา) มัดจำข้าม doc-rules. **Wire 4 จุด**: (1)_
_`AutoPostToJournalAsync` ก่อน save — Error = throw (approve ล้มดังๆ) · (2)(3)(4)_
_integration `ValidateAndAutofixJournalAsync` (PV path เดิม + เพิ่มใน_
_`CreateJournalFromMappingsAsync` และ `UpdateJournalInPlaceAsync` ที่เดิม**ไม่_
_ผ่านการตรวจเลย** — ต้นทางของ JE เสียใบนี้). **Scanner**: `JournalAnomalyService`_
_รันกฎชุดเดียวกัน (canonical เดียว) กวาด JE posted ทั้งงวด + หา **DOC-NO-JE**_
_(เอกสารอนุมัติแล้วแต่ไม่มี JE — เส้น integration ที่ refuse แล้วเงียบ) →_
_`GET /accountant/journal-anomalies` + การ์ด "🩺 ตรวจโครงสร้างรายการบัญชี" ใน_
_accountant.html พร้อมทางแก้ (เปิดเอกสาร → 📒 แก้ผังบัญชี / ยกเลิกออกใหม่)._
_AI: ตำแหน่งที่ออกแบบไว้ = second-opinion ความสมเหตุสมผลของผังผ่าน_
_`IAiOrchestrator` + distillation (ยังไม่เปิด — กฎ rule-based จับ defect class_
_ที่เกิดจริงได้ 100% โดยไม่พึ่ง AI)._
_รอบ 66: **ตัวเลือกผังบัญชีบนมือถือไม่เด้งอะไรเลย** — โมดัล "แก้ผังบัญชี" และ_
_"เปลี่ยนผัง" ใช้ `<input list=...>` + `<datalist>` ซึ่งบนเดสก์ท็อปทำงานปกติ แต่บน_
_Android Chrome มัก **ไม่แสดงรายการเลย** (และเมื่อช่องมีค่าเต็มอยู่แล้ว เบราว์เซอร์_
_ยังกรองจนเหลือรายการเดียว) ⇒ ผู้ใช้กด/พิมพ์แล้ว "ไม่มีอะไรให้เลือก". เปลี่ยนเป็น_
_`<select>` + `<optgroup>` ตามหมวดบัญชี ทำงานเหมือนกันทุกเบราว์เซอร์ ·_
_`_ensureAllAccounts()` โหลดผังครบ **5 หมวดรวมส่วนของเจ้าของ** (coaByCode เดิม_
_มีแค่ Expense/Liability/Asset/Revenue → JE แตะหมวดทุนไม่ได้) แล้ว cache +_
_merge เข้า `coaByCode` · เลิกใช้การ parse โค้ดจากข้อความ (`"53120 ค่าโฆษณา"` →_
_split) เปลี่ยนไปถือ `accountId` ตรง ๆ จาก option value — ตัดชั้นที่พังเงียบทิ้ง ·_
_แถวใน "แก้ผังบัญชี" เปลี่ยนจากตาราง 4 คอลัมน์เป็น **การ์ดต่อบรรทัด** (บนมือถือ_
_ตารางต้องเลื่อนแนวนอนจนช่องเดบิต/เครดิตหลุดจอ) · เพิ่ม guard "ต้องมีอย่างน้อย_
_2 บรรทัด" และ "แต่ละบรรทัดใส่ได้ด้านเดียว" ในหน้าจอให้ตรงกับ server._
_รอบ 65: **"เลขที่/วันที่ใบลดหนี้จากผู้ขาย" ต้องเป็นของใบลดหนี้ ไม่ใช่ใบกำกับที่อ้าง**_
_ช่องคู่นี้ใช้ field เดียวกับใบกำกับซื้อ (`SupplierInvoiceNumber` /_
_`SupplierTaxInvoiceDate`) แต่ความหมายเปลี่ยนตามชนิดเอกสาร: PI/Expense/PV =_
_ใบกำกับของผู้ขาย · CN/DN ฝั่งซื้อ = **ใบลดหนี้/เพิ่มหนี้ที่ผู้ขายออกให้**._
_ปัญหา: (ก) ตอนเลือกใบต้นทางในฟอร์ม `_applyCnSourceDefaults` ยกเลขที่+วันที่ของ_
_**ใบกำกับที่กำลังลดหนี้ให้** มาเติม → ผู้ใช้เห็นเลขที่ไม่ใช่ของใบนี้ และถ้าบันทึกต่อ_
_รายงานภาษีซื้อจะโชว์เลขใบกำกับแทนเลขใบลดหนี้จริง (คำเตือน §86/10 ก่อนอนุมัติ_
_ก็ถูก "ทำให้ผ่าน" ด้วยเลขที่ผิด) · (ข) ตรงกันข้าม เวลา OCR สแกนใบลดหนี้ของผู้ขาย_
_มาจริง ช่องนี้กลับ **ว่างเสมอ** เพราะ `bookSupplierInvoice` จำกัดไว้ที่_
_PV/PI/Expense เท่านั้น. แก้: (1) เลิกยกเลขที่/วันที่จากใบต้นทางสำหรับ CN/DN_
_(ยกเฉพาะรหัสสาขาผู้ขายที่เป็นค่าเดียวกันเสมอ) — ใบที่อ้างถึงยังผูกอยู่ที่_
_`RelatedDocumentId` ไม่หายไปไหน · (2) เพิ่ม `bookSupplierCreditNote` ใน_
_`OcrService` (+ `hasSupplierCreditNote` ใน `document-scan.html` handoff):_
_CN/DN ฝั่งซื้อที่อ่านเลขได้ → เติม `SupplierInvoiceNumber`/`SupplierTaxInvoiceDate`/_
_`SupplierBranchCode` จากกระดาษ **โดยไม่ผูกกับเงื่อนไข VAT/claimable** (เลขที่และ_
_วันที่ใบลดหนี้เป็นข้อมูลอ้างอิงตาม §86/10 ไม่ใช่เงื่อนไขการเคลม) ·_
_`HasTaxInvoiceReference` ยังเป็นของ PV เท่านั้นเหมือนเดิม._
_รอบ 64: **แผง "📒 รายการบัญชี" บนหน้าเอกสาร — ตรวจสอบ + แก้ผังบัญชีของ JE ตรงนั้น**_
_ผู้ใช้ยังหาปุ่มแก้ผังไม่เจอ เพราะแผงเดิมโชว์แค่ยอดรวมต่อใบ (ไม่เห็นด้วยซ้ำว่าลง_
_ผังอะไรไป) และปุ่ม "เปลี่ยนผัง" ซ่อนอยู่ท้ายบรรทัดสินค้าคนละที่. เพิ่ม:_
_(1) `GET /document/{id}/journal-entries` คืน JE ทุกใบของเอกสารพร้อม **บรรทัด_
_Dr/Cr จริงจาก GL** + `CanAdjust`/`BlockReason` ต่อใบ · แผงกางบรรทัดให้เห็นทั้งหมด_
_พร้อมป้าย 🔒 บนบัญชีคุม · (2) `POST /document/{id}/journal-entries/{jeId}/adjust`_
_รับ **"สถานะปลายทาง"** ของใบสำคัญ (แก้ผัง / เพิ่ม / ลดบรรทัด + เลือกวันที่) แล้ว_
_`AdjustDocumentJournalEntryAsync` คำนวณผลต่างต่อบัญชีแล้วลง **ใบปรับปรุงใหม่**_
_— ใบเดิมไม่ถูกแตะ (audit trail ครบ อ่านคู่กันได้) · (3) ค่าคงที่ 2 ข้อที่ทำให้_
_เปิดให้แก้อิสระได้อย่างปลอดภัย: **ยอดรวมห้ามเปลี่ยน** (ยอดผิด = แก้ที่เอกสาร_
_ไม่ใช่แอบแก้ผ่าน GL) และ **บัญชีคุมห้ามขยับ** (`ReclassifyProtectedCodes` —_
_ภาษีซื้อ-ขาย/ลูกหนี้-เจ้าหนี้/มัดจำ/WHT เพราะ ภ.พ.30 · ภ.ง.ด. · อายุหนี้ อ่านอยู่)_
_UI ล็อกช่องของแถวบัญชีคุมไว้เลย · (4) gate ระดับเอกสารร่วมชุดเดียวกับ reclassify_
_(`ResolveJournalAdjustBlockAsync`: ยกเลิกแล้ว / อยู่ในแบบที่ยื่นแล้ว / ส่ง e-Tax แล้ว)_
_+ งวดของวันที่ใบปรับปรุงต้องเปิด · สิทธิ์ = สิทธิ์ Approve · เขียน AuditLog ผลต่าง_
_ทุกบัญชี · (5) แบนเนอร์/แผงช่วยในหน้าสมุดรายวันชี้มาที่แผงนี้ (ใช้ได้ทุกชนิดเอกสาร_
_ต่างจากปุ่ม "เปลี่ยนผัง" รายบรรทัดที่จำกัดชนิด)._
_รอบ 63: **แปลงเอกสารแล้วยอดขยับ 1 สตางค์** — ใบแจ้งหนี้ VAT 44,942.29 /_
_สุทธิ 667,713.95 แปลงเป็นใบกำกับได้ 44,942.28 / 667,713.94. สาเหตุ: ใบต้นทาง_
_เก็บ VAT ที่ปัด **รายบรรทัด** (Σ round(net×7%)) แต่ `ConvertCoreAsync` สร้าง_
_`DocumentLineRequest` ใหม่จาก qty/ราคา/อัตราแล้วให้ `CreateDocumentAsync` คิดใหม่_
_ผ่าน `ReconcileTaxRounding` ที่กระทบยอด **รายกลุ่มอัตรา** (round(Σฐาน×7%)) —_
_ต่างกันได้ ±สตางค์เสมอเมื่อหลายบรรทัดปัดขึ้นพร้อมกัน. ลูกค้าถือใบแจ้งหนี้อยู่แล้ว_
_ใบกำกับยอดไม่ตรง = เอกสารสองใบของรายการเดียวกันขัดกันเอง (กระทบยอดกับลูกค้า/_
_ตรวจสอบภาษี). แก้: (1) `ConvertCoreAsync` ยก `VatAmountOverride = line.VatAmount`_
_เมื่อยก **ทั้งบรรทัด** (`ComputeLineAmounts` honor ตรง ๆ + `ReconcileTaxRounding`_
_ข้ามบรรทัดที่มี override) ⇒ ใบลูก = ใบแม่เป๊ะ · ยกบางส่วนยังคิดใหม่ (การเฉลี่ย VAT_
_ตามสัดส่วนสร้างเศษของตัวเอง) · ยกเว้นโหมด "ราคารวม VAT + ส่วนลดท้ายบิล" ที่การ_
_back-out ทำให้ net ไม่ตรงอยู่ดี · (2) **ฟอร์มก็ drift แบบเดียวกัน** — เดิมไม่เคย_
_ส่ง/อ่าน `vatAmountOverride` เลย ⇒ เปิดใบเก่าแล้วกดบันทึกเฉย ๆ ยอดขยับเงียบ ๆ_
_(defect class "เก็บแล้วต้อง echo กลับ"). เพิ่ม `_vatBasisKey` (qty|ราคา|ส่วนลด|_
_โหมดส่วนลด|อัตรา|ราคารวมVAT) + `_keptVat`: ผู้ใช้ไม่แตะฐาน → ใช้ VAT ที่บันทึกไว้_
_ทั้งตอนแสดงผลและตอนส่งบันทึก, แตะเมื่อไรกลับไปคิดใหม่ + กระทบยอดตามปกติ ·_
_`calcSum` ข้ามบรรทัด kept ทั้งใน reconcile และการคิด VAT ใหม่หลังส่วนลดท้ายบิล_
_(mirror backend) · (3) ป้ายใต้ยอด VAT บอกตรง ๆ ว่า "ใช้ค่าที่บันทึกไว้เดิม ต่างจาก_
_การคิดจากฐานรวม X บาท" + วิธีให้คิดใหม่ — ไม่แก้ตัวเลขเงียบ ๆ และไม่ปล่อยให้ผู้ใช้_
_ดีดเครื่องคิดเลขแล้วงง._
_รอบ 62: **แก้วันที่กลับบัญชีรายใบจากตารางโดยตรง** — ต่อจากรอบ 60. คำถามผู้ใช้:_
_"1 ใบ ยกเลิกทีเดียวทำไมมี 4 รายการ" → ระบบลงบัญชีตาม **เหตุการณ์** ไม่ใช่ตามใบ_
_(อนุมัติเอกสาร 1 รายการ + รับ/จ่ายเงินอีก 1 รายการ) พอยกเลิกจึงได้ตัวกลับครบทุกตัว_
_= ต้นฉบับ 2 + ตัวกลับ 2. เพิ่มกล่องอธิบายเรื่องนี้ในโมดัล. ฟังก์ชัน: (1) ทุกแถวใน_
_ตารางมี `<input type="date">` ของตัวเอง แก้ทีละใบได้ทันที ไม่ต้องเลือกโหมดก่อน_
_(เลิกใช้ radio perEntry/fixed) + hint ต่อแถวว่า "จะย้าย / วันที่ตรงอยู่แล้ว /_
_ติดงวดปิด" · (2) ปุ่มตั้งทั้งชุด 4 แบบ: **📌 วันที่ใบแรก** (ใบสำคัญต้นฉบับที่_
_เก่าที่สุด ตกกลับวันที่เอกสาร) · วันที่เอกสาร · ตามใบต้นฉบับของแต่ละใบ · วันที่_
_ที่เลือกเอง · (3) API รับ body `RedateVoidReversalRequest{ Entries[] }` —_
_`RedateVoidReversalAsync(..., IReadOnlyList<RedateEntryDate>? entryDates)`_
_ลำดับตัดสิน: ระบุรายใบ → ระบุวันเดียวทั้งชุด → วันที่ใบต้นฉบับ → วันที่เอกสาร ·_
_id ที่ไม่ใช่ตัวกลับของเอกสารนี้ถูกปฏิเสธ (กันใช้เป็นช่องแก้วันที่ JE ใบไหนก็ได้) ·_
_(4) ปุ่มยืนยันบอกจำนวนจริง "ย้ายวันที่ N ใบ" และปิดตัวเองเมื่อไม่มีใบต้องย้าย._
_รอบ 61: **เปิดเปลี่ยนผังบัญชีรายบรรทัดให้ฝั่งขาย** — เดิม `ReclassifyLineAccountAsync`_
_รับเฉพาะ Expense/PurchaseInvoice/PaymentVoucher โดยอ้าง §86/4 ว่าใบกำกับแก้ไม่ได้_
_ซึ่งอ้างผิดมาตรา: §86/4 บังคับ **สิ่งที่พิมพ์บนใบกำกับ** และรหัสผังบัญชีไม่ได้อยู่_
_บนใบกำกับเลย — ย้ายรายได้ 41100 → 41200 ไม่แตะยอดบนใบ ไม่แตะ VAT ไม่แตะ ภ.พ.30_
_ผลของการห้ามคือใบขายที่ลงผังผิด "ไม่มีทางแก้" ต้องยกเลิกใบกำกับทั้งใบ ซึ่งเสี่ยงกว่า._
_แก้: (1) allowedTypes เพิ่ม Invoice/TaxInvoice/Receipt/CreditNote/DebitNote ·_
_(2) **ทิศ JE ต้องกลับด้านที่ลงจริง** — อ่านขา Dr/Cr ของผังเก่าจาก JE ของเอกสาร_
_(GL-first เหมือน drives รอบ 57) แล้วสร้างคู่ตรงข้าม: บรรทัดค่าใช้จ่าย = Dr ใหม่/_
_Cr เก่า · บรรทัดรายได้ = Dr เก่า/Cr ใหม่ (ถ้าใช้สูตรเดียวกันทั้งคู่ รายได้เดิมจะ_
_เพิ่มเป็นสองเท่าและผังใหม่ติดลบ) fallback = ธรรมชาติของ AccountType เมื่อหา JE ไม่เจอ ·_
_(3) เลิกบล็อกด้วย "มีการรับ/จ่ายชำระแล้ว" — JE ชำระเงินแตะ เงินสด ↔ ลูกหนี้/เจ้าหนี้_
_ไม่ใช่ผังของบรรทัดสินค้า (การเปลี่ยนแหล่งเงินยังอยู่ที่ `ReclassifyPaymentSourceAsync`) ·_
_(4) ใบเสร็จหลักฐาน `IsSettlementReceipt` ไม่นับเป็น "เอกสารปลายทาง" (evidence-only_
_ไม่ลง JE) ไม่งั้นใบขายที่เก็บเงินแล้วถูกล็อกทุกใบ · (5) เพิ่ม `ReclassifyProtectedCodes`_
_ห้ามย้ายเข้า/ออกบัญชีคุม (11310 ลูกหนี้, 21210 เจ้าหนี้, 11610/11640 ภาษีซื้อ,_
_21911/21912/21913 ภาษีขาย, 21916-18 WHT, 21510/21520/21610/21711-13 มัดจำ) —_
_**เทียบตรงรหัส ไม่ใช่ prefix** (prefix "215"/"217" จะเผลอล็อก 21511 ค่าไฟฟ้าค้างจ่าย_
_และ 21714 ดอกเบี้ยค้างจ่าย) · (6) ใบมัดจำ (`IsDeposit`) ยังห้าม (มีวงจรของตัวเอง) ·_
_gate เดิมคงอยู่ครบ: งวดปิด / อยู่ในรายงานภาษีที่ยื่นแล้ว / ส่ง e-Tax แล้ว / เอกสาร_
_ปลายทางจริง. UI: `canReclassify` + datalist mirror รายการเดียวกัน._
_รอบ 60: **"แก้วันที่กลับบัญชี" กับเอกสารที่มีตัวกลับหลายใบ** — เอกสารใบเดียว_
_มักมีตัวกลับหลายใบและคนละวัน (ใบซื้อ 1 ก.ค. + ใบจ่ายชำระ 17 ก.ค. → ยกเลิกทีเดียว_
_ได้ตัวกลับ 2 ใบ). เดิม `RedateVoidReversalAsync` ยัดทุกใบไป **วันเดียวกัน**_
_(วันที่เอกสาร) และ UI เป็น `prompt()` ที่ไม่บอกเลยว่าจะไปแตะใบไหนบ้าง._
_แก้: (1) ค่าเริ่มต้นเปลี่ยนเป็น **วันที่ของใบต้นฉบับที่ตัวเองกลับ** ต่อใบ_
_(`ResolveRedateTarget` + `LoadReversalOriginalsAsync` — resolver กลางใช้ร่วมกับ_
_preview, ตกกลับวันที่เอกสารเมื่อหาต้นฉบับไม่เจอ) · ระบุวันที่มาเอง = บังคับทุกใบ_
_ไปวันนั้น · (2) `PreviewVoidReversalRedateAsync` +_
_`GET /document/{id}/redate-void-reversal/preview` คืนตาราง เลขที่ตัวกลับ /_
_ประเภท / ยอด / วันที่ตอนนี้ → วันที่ใหม่ / ใบต้นฉบับที่อ้าง / เหตุผลที่ย้ายไม่ได้_
_(งวดต้นทางหรือปลายทางปิด, วันที่ตรงอยู่แล้ว) — ตรวจงวดครั้งเดียวจากลิสต์_
_FiscalPeriod ไม่ยิงรายบรรทัด · (3) UI เปลี่ยนจาก `prompt()` เป็นโมดัลที่โชว์_
_ตารางก่อนยืนยัน + เลือกโหมด "ตามใบต้นฉบับของแต่ละใบ (แนะนำ)" หรือ "วันเดียวกัน_
_ทุกใบ" · เลือกเฉพาะ `OriginalEntryId != null && Status == Posted` เหมือนเดิม_
_(ใบต้นฉบับไม่ถูกแตะ ⇒ ยอดรวมเท่าเดิม เปลี่ยนแค่งวด)._
_รอบ 59: **ทางแก้ผังบัญชีของ JE ที่ระบบลงจากเอกสาร** + ปุ่มหน้าสมุดรายวันตรงกับ_
_สิทธิ์จริง. ปัญหา: ใบสำคัญที่ผูก `SourceDocumentId` ถูก server บล็อกทั้ง Update /_
_Reverse / Correct / Void / Delete แต่ UI โชว์ปุ่มครบ ⇒ กด 4 ใน 5 ปุ่มแล้ว error_
_และไม่มีปุ่มไหนบอกว่าจริง ๆ ต้องไปแก้ที่ไหน. แก้: (1) แถว/โมดัลของใบที่ผูกเอกสาร_
_เหลือ "ดู" + "📄 เปิดเอกสารต้นทาง" เท่านั้น + ป้าย `จากเอกสาร` + checkbox batch_
_ถูก disable (BatchDelete ฝั่ง server ปฏิเสธทั้งชุด) · (2) banner ในโมดัลบอกทางแก้_
_ตามชนิดเอกสาร: Expense/PI/PV → ปุ่ม **"✏️ เปลี่ยนผัง"** ท้ายบรรทัด_
_(`ReclassifyLineAccountAsync` — ลง JE ย้ายบัญชีในงวดเดิม เอกสารต้นฉบับไม่ถูกแก้)_
_ชนิดอื่น → "ยกเลิกเอกสารแล้วออกใหม่" · (3) `CorrectJournalEntryAsync` เพิ่ม guard_
_`SourceDocumentId` พร้อมข้อความชี้ทางแก้ (เดิมตกไปโดน guard ของ Reverse ที่ตอบ_
_คนละคำถาม) · (4) ลิงก์ "เอกสารต้นทาง" เดิมชี้ `documents.html?search=<เลขที่>`_
_แต่หน้านั้น **ไม่เคยอ่าน `?search=`** ⇒ ตกมาที่ลิสต์เปล่า — เพิ่มการอ่าน `?search=`_
_(ซ่อมลิงก์ที่ตายพร้อมกัน 4 หน้า: journals / general-ledger / bank / wht) และเพิ่ม_
_`?openDoc=<id>` ที่เปิด **หน้ารายละเอียด** เอกสารตรง ๆ (ไม่ใช่ `?editDoc=` ที่เปิด_
_ฟอร์มแก้ไขของใบอนุมัติแล้ว = แก้อะไรไม่ได้) · (5) ปุ่ม reclassify เดิมเป็น "✏️"_
_เปล่าขนาด 10px ไม่มีใครเห็น → ใส่ข้อความ "เปลี่ยนผัง" + สีน้ำเงิน · (6) แผงช่วย_
_"❓ ปุ่มไหนใช้ตอนไหน" บนหน้าสมุดรายวัน (เปิดค้างครั้งแรก) อธิบายความต่าง_
_แก้ไข / กลับ-แก้ / กลับรายการ / ลบ เป็นภาษาคนอ่าน._
_รอบ 58 (audit จำลอง scenario): แก้ **ภ.พ.30 นับ VAT มัดจำซ้ำ** — มัดจำ defer ที่_
_ถูกหักผ่าน drives/apply (DepositAppliedToDocumentId ตั้ง) เคยถูกดึงเข้า ภ.พ.30_
_งวด RecognizedAt ทั้งที่ใบเช็คเอาท์/ใบกำกับปลายทางรายงาน VAT เต็มใบแล้ว → ยอด_
_ขาย/ภาษีขายเกินจริง. แก้: exclude applied deposits จาก deferredRecognized query_
_+ Receipt branch (standalone RealizeDeposit ยังรายงานปกติ). + deferred ตัดสิน_
_**GL-first** (flag หรือขา Cr 21913 จริงใน JE ใบมัดจำ) เหมือน drives d7ee4d3 —_
_มัดจำ integration ที่ flag ไม่ตั้งเคยถูกรายงานเดือนรับเงินทั้งที่ GL พัก 21913._
_รอบ 57: drives — **หลักเดียวทุกเคส** (TakeTime §5): resolve เจอแล้ว → อ่าน "ขา Cr_
_จริง" ของใบมัดจำ/JV แล้วกลับตามนั้น ไม่ assume โหมดจาก field/flag/setting._
_เคส A ยกเครื่องเหมือนเคส B: อ่าน JE ของใบมัดจำ (SourceDocumentId, Posted||Reversed)_
_→ ratio ฐาน/VAT จากขา Cr จริง + Dr กลับ "บัญชีเดิมที่ถูกเครดิต" (ไม่เดาผัง):_
_gross (ไม่มีขา VAT) → Dr 217xx เต็ม / net+21913 → Dr ทั้งคู่ + Cr 21911 เต็ม /_
_net+21911 → Dr 21911 (net). fallback field+flag เฉพาะเมื่อ JE ไม่ผูกใบมัดจำ._
_รอบ 56: drives เคส A (document REC-) + deferred VAT — ขา VAT อ่านจาก **GL จริง**_
_ของใบมัดจำ (GL-first, flag-fallback) เหมือนเคส B: เดิมพึ่ง flag DepositOutputVat_
_Deferred อย่างเดียว → มัดจำที่ Cr 21913 จริงแต่ flag ไม่ตั้ง ถูกเลือก 21911 →_
_Dr net กับ Cr 21911 ของใบเช็คเอาท์ = 21913 ค้างถาวร + ภาษีขายงวดขาด + JE ≠_
_ยอดเอกสาร (เคส REC-20260707-0002). ใหม่: sum(Cr−Dr) บน 21913 ของ JE ที่_
_SourceDocumentId=ใบมัดจำ (Posted||Reversed) ≥ depVat → Dr 21913 + Cr 21911 เต็ม._
_รอบ 55: drives-resolve (เคส B journal) เปลี่ยนจาก link-based เป็น **net-balance**_
_ครอบคลุมทุกกลไก un-reverse — เดิมกรอง `ReversedByEntryId == null` (partner_
_reverse→un-reverse → link ค้างที่ NextAcc → หาไม่เจอ). เปลี่ยนเป็นคำนวณ **net GL_
_จริง**: Σ(Cr−Dr) บนบัญชี deferred ของ **ทั้ง reverse-family** (transitive closure_
_ตาม OriginalEntryId ทุกชั้น) นับ **Status = Posted||Reversed** (ตรงกับ GetGeneral_
_Ledger/TrialBalance — Reversed ยังอยู่ใน ledger; Voided/Draft/ลบ หลุด) →_
_telescope เป็น net เสมอ: reversal-of-reversal(+Cr)/void/delete reversal → net live;_
_reversal ยัง active → net 0 (ตัด). สำคัญ: reverse ตั้ง original.Status=Reversed_
_ถ้ากรองแค่ Posted จะหา original ไม่เจอหลัง reverse. verify: TakeTime reverse ผ่าน_
_integration ProcessJournalReverse → ตั้ง OriginalEntryId + Status=Reversed →_
_closure เห็นครบ. ไม่พึ่ง flag ReversedByEntryId → un-reverse วิธีใดก็ได้._
_รอบ 54: กวาดบั๊ก Include(Contact) INNER JOIN ทั้งระบบ (~40 จุด) + integration_
_invoice รับ `bookingNumber` — helper กลาง `ContactHydration` (Hydrate*ContactsAsync_
_ผูก Contact ที่ soft-delete กลับเข้า nav ด้วย IgnoreQueryFilters). ครอบคลุม ภาษี_
_(ภ.พ.30/36/54, ภ.ง.ด.3, aging, bad-debt, 50 ทวิ), รายงาน (executive/dashboard/_
_cashforecast/reportbuilder), bank reconciliation, PDF/email/etax, portal,_
_revenue-recognition. `InboundInvoiceRequest.BookingNumber` (JSON `bookingNumber`,_
_string) → `Document.BookingNumber` (company endpoint มีอยู่แล้ว)._
_รอบ 53: **ต้นเหตุจริง** หน้าเงินมัดจำโชว์ 0 (ไม่ใช่ cache/deploy) —_
_`GetDepositsAsync` ทำ `.Include(d => d.Contact)` แต่ `Document.Contact` เป็น_
_required (ContactId non-nullable) + `Contact` มี `HasQueryFilter(!IsDeleted)` →_
_EF Core แปลงเป็น **INNER JOIN + filter** → เอกสารมัดจำที่ contact ถูกลบ/ปิด_
_(IsDeleted=true เช่น vendor โรงแรมที่ deactivate) ถูก "ตัดทิ้งเงียบทั้งใบ" →_
_native 16 ใบหายหมด. Diagnostic ไม่มี Include เลยนับครบ (= 2 ตัวเลขขัดกัน). แก้:_
_เลิก .Include, โหลดชื่อ/เลขภาษี contact แยกด้วย IgnoreQueryFilters ลง dictionary_
_แล้ว map ตอน build DepositSummary (contact ที่ถูกลบยังโชว์ชื่อ ไม่ทำใบหาย)._
_รอบ 52: deposit-applied drives-journal รับ journal ref (TakeTime point 2) —_
_เดิม `DepositAppliedRef` resolve ได้แค่ใบมัดจำ (Document) ตาม DocumentNumber →_
_มัดจำที่เป็นสมุดรายวันภายนอก (JV-INT) หาไม่เจอ → throw → integration ต้อง_
_fallback ส่ง JV reverse แยก. เพิ่มเคส B: ไม่พบ Document → resolve เป็น_
_JournalEntry.EntryNumber → กลับ deferred (217xx/21913) จากบรรทัด Cr จริงของ_
_journal → net JE ใบเดียว. guard double-reverse ด้วยคอลัมน์ใหม่_
_JournalEntry.DepositAppliedToDocumentId (void → un-mark). เคส A ไม่แตะ._
_รอบ 51: ที่อยู่ต่างประเทศของ Contact — ฟอร์มผู้ติดต่อเดิมเป็นโครงไทยล้วน_
_(จังหวัด/รหัสไปรษณีย์ required) → vendor/ลูกค้าต่างชาติ (เช่น Booking.com B.V.)_
_กรอกไม่ได้. เพิ่ม checkbox "🌐 ที่อยู่ต่างประเทศ" → สลับเป็น dropdown ประเทศ_
_(ISO alpha-2) + textarea ที่อยู่เต็ม; save เซ็ต `CountryCode`≠TH + `Address`_
_free-text + null โครงไทย. e-Tax `BuildBuyerParty`: guard `isThai` — CountryID≠TH_
_บังคับไปทาง unstructured (LineOne + CountryID ต่างชาติ) ไม่ยัด TISI geo-code_
_ไทยให้ที่อยู่ต่างชาติ. (backend DTO/entity/WHT ม.70 รองรับ CountryCode อยู่แล้ว)_
_รอบ 50: dashboard เงินมัดจำ (`GetDepositsAsync`) — ยังโชว์ 0. ขยายการตรวจจับ_
_เป็น 3 ชั้น: (1) native IsDeposit, (2) GL-detected **ทุก doc type** (เลิกจำกัด_
_แค่ Receipt/RV) ใช้ยอด **Cr สุทธิใน GL** (ΣCr−ΣDr) เป็นฐาน/คงค้าง แทน SubTotal_
_ที่ integration doc อาจไม่ตั้ง (เดิม outstanding=0), (3) doc-less: มัดจำที่เป็น_
_JE ล้วน `SourceDocumentId=null` → รวมเป็น 1 แถวสรุป (Id=Guid.Empty ไม่มีปุ่ม)_
_เพื่อ KPI ไม่ขึ้น 0 ทั้งที่งบดุลมีหนี้สินมัดจำ._
_รอบ 49: dashboard เงินมัดจำ (`GetDepositsAsync`) — เดิมกรอง `IsDeposit=true`_
_อย่างเดียว → พลาดมัดจำที่สร้างผ่าน integration (ลง JE เอง Cr 215xx/217xx ผ่าน_
_mapping DEPOSIT_RECEIVED โดยไม่ set IsDeposit) → หน้าเงินมัดจำโชว์ 0. เพิ่มการ_
_ตรวจจาก GL จริง (เอกสาร Receipt/RV ที่มี JE posted Cr 215xx/217xx ไม่ reverse)_
_union กับ native → สะท้อนความจริงทางบัญชี ไม่พึ่งแค่ธง._
_รอบ 48: e-Tax PDF/A-3 — เปลี่ยนวิธีฝัง XML จาก hand-rolled injector (2 xref,_
_XMP ซ้อน → strict parser/สรรพากรหา XML ไม่เจอ = "XML หาย") → **QuestPDF native_
_`DocumentOperation.AddAttachment()` + `ExtendMetadata()`** (qpdf single-pass,_
_xref เดียว, XMP เดียว, /AF ถูก — เหมือน iTextSharp ที่ TakeTime ใช้)._
_`AttachEtaxXmlNative` (temp file + fallback injector ถ้า native ล้ม),_
_`BuildEtdaXmpExtension` (rsm schema สำหรับ ExtendMetadata). ใช้ทั้ง_
_BuildEtaxPdfA3WithEmbeddedXml + GenerateDocumentPdfAsync._
_รอบ 47: e-Tax PDF/A-3 — แก้บั๊ก /Size ผิด (trailer /Size = maxObj+1 แต่ add_
_object เลข maxObj+1..+4 → embedded XML objects นอกช่วง → สรรพากร "ประมวลผล_
_เอกสารแนบไม่ได้"). แก้เป็น newOffsets.Keys.Max()+1. นี่คือสาเหตุหลักที่ RD reject._
_รอบ 46: e-Tax PDF/A-3 — แก้ compliance ให้ผ่าน validator: (1) trailer เพิ่ม /ID_
_(incremental update คง file id เดิม — PDF/A บังคับ), (2) XMP เพิ่ม field มาตรฐาน_
_ครบ (dc:title/creator/description, pdf:Producer/Keywords, xmp:CreatorTool/Create_
_Date/ModifyDate) ตรงกับ Info dict + วันที่ capture ครั้งเดียว, (3) FindMaxObj_
_fallback สแกน object header กัน XML ไม่ถูกฝังเงียบ. + DocumentEmailService:_
_ส่ง e-Tax by Email ถ้าสร้าง PDF/A-3 ไม่ได้ → **fail loud** (เดิม swallow ส่ง_
_อีเมลเปล่าไม่มีเอกสารตามกฎหมาย). หมายเหตุ: วิธี robust สุดคือใช้ PDF/A library_
_(ETDA reference ใช้ iTextSharp) — ปัจจุบัน QuestPDF(A-2b)+injector ยังเปราะ._
_รอบ 45: บันทึกชำระเงิน → ออก "ใบเสร็จรับเงิน" หลักฐานอัตโนมัติ (default เปิด_
_ฝั่งขาย Invoice/TaxInvoice/DebitNote). `Document.IsSettlementReceipt=true` +_
_`SettlementPaymentId`, `Payment.ReceiptDocumentId`. ใบนี้ **evidence-only**:_
_Payment ลง Dr เงินสด/Cr ลูกหนี้ + ตัด AR แล้ว → ใบเสร็จ **ไม่ลง JE ซ้ำ ไม่ตัด_
_หนี้ซ้ำ ไม่คิด VAT ซ้ำ** (VAT อยู่ที่ใบกำกับ, VatAmount=0) สร้างตรงเป็น Status=_
_Paid ไม่ผ่าน ApproveDocumentAsync. void payment → void ใบเสร็จตาม. เลิกใช้_
_convert Invoice→Receipt เป็นทางตัดหนี้ (กันเบิ้ล). `CreateSettlementReceiptAsync`._
_รอบ 44: หัวเอกสาร downgrade ตาม `Buyer864Incomplete` จริง (ไม่ใช่แค่ flag) —_
_ข้อมูล §86/4 ผู้ซื้อไม่ครบ = ห้ามขึ้น "ใบกำกับภาษี". + ส่วนลดท้ายบิล (จากยอด_
_รวม): `Document.BillDiscountPercent/Amount` — `ComputeLineAmounts(extraDiscount)`_
_เฉลี่ย pro-rata (ex-VAT) ลงบรรทัด → VAT/WHT รายบรรทัดถูกต้องแม้ mixed-rate._
_SubTotal = หลังหักท้ายบิล (คง invariant Σ line.Amount); PDF แสดง "ยอดรวมก่อน_
_VAT" = SubTotal+BillDiscount + บรรทัด "ส่วนลดท้ายบิล". `AllocateBillDiscount`._
_รอบ 16 (audit ยอดเบิ้ล/double-count + concurrency): supersede block,_
_deposit-apply settlement JE, settlement receipt กันนับซ้ำในรายงานรายได้,_
_POS tip fix, POS refund discountFactor, Integration idempotency (expense/PV_
_+ ExternalId fallback), payment/JE FOR UPDATE ใน tx (create+void), payroll_
_void row-lock, recurring FOR UPDATE SKIP LOCKED, Employee.LineId migration._
_รอบ 16: DBD XBRL annual export (TFRS-NPAEs taxonomy) + ผู้ทำบัญชี CPD gate_
_(พ.ร.บ.การบัญชี ม.7), PDPA Wave 3 UI tabs (DSR/RoPA/Consent/Breach with 72h timer)._

## รอบ 171 — เว็บไซต์ CMS: ลบแล้วสร้างชื่อเดิมไม่ได้ (500 อ่านไม่ออก)

**อาการที่ผู้ใช้เจอ** สร้างเว็บชื่อ `b1` → 500 "เกิดข้อผิดพลาดภายในระบบ (F37BE341)" ·
Error Logs: `23505 duplicate key ... IX_Sites_CompanyId_Slug`

**ต้นเหตุ 2 ตัวซ้อนกัน**
1. คีย์ไม่ซ้ำของ CMS ทั้ง 4 ตัวไม่กรอง `IsDeleted` แต่ `Site` มี query filter `!IsDeleted`
   ⇒ เว็บที่ลบแล้วยังจอง slug/subdomain/domain ไว้โดยไม่มี query ไหนมองเห็น
   (`DeleteSiteAsync` ตั้ง `IsDeleted=true` อย่างเดียว ไม่ปลดคีย์)
2. `CreateSiteAsync` ตรวจซ้ำ **เฉพาะ Subdomain ไม่เคยตรวจ Slug** ⇒ ตั้งชื่อเว็บซ้ำกับเว็บที่ยัง
   ใช้อยู่ก็ 500 เหมือนกัน แม้ไม่เคยลบอะไรเลย

**แก้**
- `DeleteSiteAsync` ปลดคีย์ก่อนซ่อนแถว (Slug · Subdomain · CustomDomain · ทุกแถว `SiteDomains`)
  ผ่าน `Helpers/CmsRetiredSlug` ตัวเดิมที่หน้า CMS ใช้อยู่ — เพิ่มพารามิเตอร์ความยาวคอลัมน์
- `Helpers/CmsFieldLengths` เป็นตัวตั้งความยาวตัวเดียว `AccountingDbContext` อ่านตัวเดียวกัน (5 จุด)
- `CreateSiteAsync` อ่านคีย์ที่จองไว้ด้วย `IgnoreQueryFilters()` แล้วแยกทางตามที่มาของค่า:
  Subdomain (ผู้ใช้พิมพ์) → `BusinessRuleException` บอกให้เปลี่ยน ·
  Slug (ระบบสร้าง ไม่มีช่องให้แก้) → `Helpers/CmsSlugUniquifier` เติม `-2` ให้เอง
- เพิ่มด่านชน `IX_SiteDomains_Domain` ซึ่งไม่ซ้ำ **ข้ามบริษัท** (ข้อความไม่บอกว่าใครถือไว้)
- `ExceptionMiddleware` แปลง Postgres 23505 เป็น **409 พร้อมข้อความไทย** แทน 500 ที่อ่านไม่ออก
  — ครอบทุกตารางในระบบ ไม่ใช่แค่ CMS · ข้อความบอกอาการ ไม่วินิจฉัยสาเหตุ
- migration 4 คำสั่งปลดคีย์ของเว็บที่ลบไปก่อนหน้า (ต่อท้ายด้วย `Id` ของแถว ⇒ ไม่ซ้ำ รันซ้ำได้)

**ยังไม่ได้แก้ — ต้องให้เจ้าของตัดสิน** กวาดทั้ง `AccountingDbContext` พบ **62 entity** ที่มีทั้ง
unique index และ query filter `!IsDeleted` ในนั้น **9 ตัวคีย์เป็นรหัสที่ผู้ใช้พิมพ์เอง** จึงมีอาการ
เดียวกันรออยู่: `Product.Code` · `ProductCategory.Code` · `Warehouse.Code` · `Branch.Code` ·
`Department.Code` · `Position.Code` · `Project.Code` · `ChartOfAccount.AccountCode` ·
`SiteCustomer.Email` — การปลดรหัสตอนลบเปลี่ยนสิ่งที่รายงานย้อนหลังแสดง (ERP core) จึงไม่รวมใน
คอมมิตของบั๊ก CMS · ระหว่างนี้ทั้ง 9 ตัวได้ข้อความ 409 ที่อ่านออกแล้วจาก ExceptionMiddleware

**เทสต์** `CmsSlugUniquifierTests` (9 เคส รวม invariant "ผลลัพธ์ต้องไม่ชนของที่จองไว้" 50 รอบ) ·
`CmsRetiredSlugTests` เพิ่มเคสความยาวคอลัมน์ 63/128/256


## รายการที่ผ่านมาเรียงตามรอบ

| รอบ | Theme | Key items |
| --- | --- | --- |
| 6 | e-Tax XML ครบสุด | line ChargeAmount ถอด VAT, TaxBasis แก้, PDF format |
| 7 | Multi-currency มัดจำ + audit | FX guard, hash chain weekly verifier, recurring template validate, §65 ตรี(4) YTD, §82/5(6) override |
| 8 | Option-1 reclassify + CMS sync | line GL reclassify-JE, PV Cash auto-approve all channels, CMS ConfirmPaymentAsync 6-step, PrePayment booking IsDeposit 217xx |
| 9 | CMS gap close + perf | payment-gateway webhook, POS Z/X-Report, Stock unify, OverdueDunningJob, Dashboard alerts, e-Tax retry, §82/3 LateReason, dup-doc detect, 11 indexes |
| 10 | Notification consolidate | NotificationContext.RecipientUserId, ApprovalService migrate, PiiMask helper, FX bank scope note |
| 11 | PDPA + DSR + builder ครบสุด | EncryptedColumnConverter (AES-256-GCM Employee CitizenId/TaxId/Passport), PiiMask + permission Pii.View ใน PayrollController, SubscriptionService migrate 4/5 → NotificationEngine, DSR endpoints /access /portability /rectify /erase (legal_hold), Multi-warehouse StockAdjustmentRequest WarehouseId/LotNumber, ProductLot verified, JournalEntryBuilder fluent abstraction |
| 12 | JE migrate + business gaps ปิด | JE Builder phase 2 (ReclassifyLine + FxRevaluation refactor), UnifiedPaymentQueryService cross-domain (AR+AP+POS+CMS), POS deposit IsDeposit+DepositRealizedAt, TipPayoutService §50 ทวิ (3% WHT >1000), RecurringLateFeeAccrualJob (rate/grace/cap config), DocumentLineDeliveryService LINE flex, Budget scenarios best/base/worst |
| 138 | ปิด backlog P1 ของไปป์ไลน์ OCR (4 ชุด) | ล็อกต่อสแกนกันสร้างเอกสารซ้ำ (`JobLock` try) · ห้ามลบสแกนที่ลง JE แล้ว · `OcrStuckScanSweepJob` กวาดแถวค้าง + คืนโควตา · เส้น OCR ผ่านธง `InputVatClaimable` และดึงอัตรา ธปท. เหมือนเส้นคีย์มือ (`Helpers/InputVatAccountPolicy`) · `Helpers/OcrPostingReadiness` เป็นตัวตัดสิน auto-approve ตัวเดียวของทุกช่องทาง · แยกอัตรา WHT ตามกฎหมายออกจากยอดบนกระดาษ + ประเภทเงินได้ ม.40 ไหลครบสายถึง `Document.IncomeTypeCode` · เตือน ภ.พ.36/ภ.ง.ด.54 ของใบต่างประเทศ · อ่าน `Items[].Unit/TaxRate/Tax` + `DueDate` ที่ Azure คืนมาแล้ว · ชนิดกระดาษตัดสินจากคำบนใบ ไม่ใช่ชื่อไฟล์ · `Helpers/OcrReviewGuard` กรองคำตอบปุ่ม AI ก่อนแตะฟอร์ม + `userTouched` · การ์ด LINE แสดง §82/5/Σ-GAP และซ่อนปุ่มอนุมัติเมื่อยังไม่พร้อม · ต่อเมนู review-queue · ช่องเหตุผลทางธุรกิจ §65 ตรี · คอลัมน์ VAT/หน่วยรายบรรทัด + handoff เลิกคิด vatRate ใหม่เอง |
| 137 | ไปป์ไลน์ OCR → เอกสาร (ทีมตรวจ 5 ทีม) | ด่านสิทธิ์ที่เส้น OCR (`CanCreateAsync`/`CanApproveAsync`/`JournalManage`) = ทางอนุมัติทางที่ 4 ที่รอบก่อนยังไม่ปิด · 50 ทวิ ที่เราถูกหัก เลิกสร้างใบสำคัญรับ standalone (รายได้เบิ้ล) → ลงทะเบียนเครดิตภาษีแล้ว throw `OCR-WHTCERT-NO-SALE` · `Helpers/OcrLineReconciler` (pure+เทสต์) แทนตรรกะ 4 เคสที่เทียบ Σ บรรทัดกับ**ยอดหลัง VAT** ⇒ เลิกแต่งบรรทัด "ค่าขนส่ง/บริการอื่น" และเลิกคิดส่วนลดผิดฐาน · JE จากสแกนบล็อก §82/5 + ฐาน WHT = total−VAT (เท่าเส้นเอกสาร) + ผังภาษี 11610/21916/21917 (เดิม 11511/21701 ไม่มีในผังมาตรฐาน ⇒ ตกไปหยิบบัญชีคุม "116") · เลขใบแจ้งหนี้เลิกลง `SupplierInvoiceNumber` (§82/5(1)) → `[TAX-INV-PENDING]` · `SupplierBranchCode` ไม่แต่ง "00000" · วันที่อ่านไม่ได้ = ห้าม auto-approve · คำตอบ "นักเรียน" ถูกใช้จริง (`HasModelAnswer`) ทั้ง 5 จุด · `AzureDiPatternLearner` สอนเฉพาะ conf ≥0.85 · CAPTURE ฝั่งยอมรับบนเส้น 1-click · `Failed` ไม่ถูกทับเป็น `Completed` · `AmountTripleExtractor` ไม่ทับยอดที่ engine อ่านมาแล้ว · "เลขที่" ของที่อยู่ไม่ชนะเลขที่เอกสาร |
| 134 | สแกนซ้ำคัดลอกข้อมูลไม่ครบ (ผู้ใช้รายงาน) | เส้น `Cached` เคยคัดลอก **10 ช่องจาก 49** ⇒ ข้อความดิบ · รายการสินค้า · ช่อง §86/4 ฝั่งผู้ซื้อ · `TargetDocumentType` · หมวดค่าใช้จ่าย · WHT · ค่าความมั่นใจรายช่อง **หายเงียบ 39 ช่อง** ทุกครั้งที่อัปโหลดไฟล์เดิมซ้ำ → `Helpers/OcrScanSnapshot` เป็น **deny-list** (คัดลอกทุกช่อง ยกเว้น 15 ช่องที่เป็นตัวตนของแถว) + `OcrScanSnapshotTests` เติมค่าทุกช่องพิสูจน์ว่าไม่มีช่องไหนหลุด · ด่านไฟล์ซ้ำกรอง `!IsDuplicate` + `OrderByDescending(CreatedAt)` (เดิมได้สำเนาของสำเนา แบบไม่ deterministic) · `ScanAsync(forceRescan)` + retry เลิกทิ้งแถวค้าง `Processing` และบอกเลขสแกนปลายทาง · ปุ่ม "สแกนใหม่" เลิกส่ง scanId ไป endpoint ที่รับ fileAttachmentId (500 มาตลอด) · กล่อง Raw Text เลิกโทษ Docker เมื่อ engine เป็น `Cached`/`EtaxXml` · `OcrConfidenceGateway` ข้อ 2b จับ "ตัวเลขสามช่องขัดกันเอง" · migration COALESCE ซ่อมแถวสำเนาที่พังไปแล้ว |
| 133 | สัญญาณความมั่นใจของ OCR + ด่าน PDPA ก่อนส่ง prompt | `fc()` ในหน้า review เลิก fallback ไปคะแนน**ทั้งใบ** (เดิม "00000 · 95%" บนค่าที่ระบบเดาให้) → "—" · stamp `SellerBranchCode/BuyerBranchCode = 0.30` เมื่ออ่านไม่ได้ · `Layout.applyOcrConfidenceHints` ตัวกลางตัวเดียว ใช้ทั้งฟอร์มเอกสารและ**หน้า review ที่ผู้ใช้ตัดสินใจจริง** · เปิดแก้ VendorAddress/BuyerName/BuyerAddress/PaymentTermsDays ครบทั้ง ฟอร์ม→payload→DTO→persist · `AiPromptSanitizer`: regex อีเมล + เลขบัญชีที่มีป้าย + **สตริงใน array ที่ไม่เคยถูกปิดบังเลย** + `AllowTaxIdInPrompt` คงเฉพาะเลขนิติบุคคล (เลขบัตรประชาชนปิดบังเสมอ §26) · `pg_try_advisory_lock` บนงานเทรน · `AiFeatureKey` 4 ค่าที่ตายแล้ว → `[Obsolete(error)]` |
| 128 | POS เฟส 3 — ขายแล้วกินสูตร | `ProductType.RawMaterial` · `Product.ConsumesBomOnSale` · `Helpers/BomConsumption` (บริสุทธิ์ · เรียงผลลัพธ์คงที่กัน deadlock) · ตัด/คืนวัตถุดิบครบ 4 เส้นผ่านตัวเดียว · ท็อปปิ้งผูกวัตถุดิบ (`ProductModifierOption.ComponentProductId`) · สูตรเป็น **เวอร์ชัน** ไม่เขียนทับ (`mfg/products/{id}/recipe`) |
| 127 | POS เฟส 1-2 — สาขา + ภ.พ.06 | `PosTerminal.BranchId/WarehouseId/Cash/BankAccountId` · snapshot ลง `PosOrder` · `Company.IsRetailApproved` + `PhoR06ApprovedDate` · `Helpers/PosSlipHeader` ตัดสินหัวสลิปที่เซิร์ฟเวอร์ (renderer 2 ตัวรับค่ามาแสดง) · เลขใบกำกับอย่างย่อ gap-free ต่อ (สาขา, เดือน) · JE ของ POS ติดมิติสาขา |
| 125 | POS เฟส 0-1 — หนึ่งความจริงของสต็อก | `IStockLedger` + `StockLedger` เป็นผู้เขียนสต็อกตัวเดียว · ย้ายผู้เขียนเดิม **ทุกไฟล์ในรอบเดียว** (POS 4 · เอกสาร 2 · สินค้า 3 · CMS 3 · นำเข้า 2 · ผลิต 2 · นับสต็อก · ฝากขาย · **ใบโอนคลัง 3**) · migration สร้างคลังหลัก + ย้ายยอดเดิม + backfill `StockMovements.WarehouseId` · `PosTerminal.BranchId/WarehouseId` + snapshot ลง `PosOrder` · `ProductionOrder.WarehouseId` · `Helpers/WeightedAverageCost` (ยุบสูตร WAC 3 ชุด) · `tools/stock_writer_check.py` |
| 124 | โมดูลที่พัก (Lodging) | วิเคราะห์ TakeTime → `Lodging*` 14 ตาราง · `LodgingPricingEngine` pure + 19 เทสต์ · storefront `/booking` + `/reservation/{token}` · front desk + ตั้งค่า 2 หน้า · มัดจำ Receipt(IsDeposit) → เช็คเอาต์ TaxInvoice DepositApplied* → ยกเลิก Refund/Realize ตามนโยบาย snapshot · seed เมื่อสร้างเว็บโรงแรม |
_Files referenced are accurate; if behavior diverges, this doc is wrong —_
_update it in the same PR (CLAUDE.md §"DOCUMENT_FLOW.md" hard requirement)._

_Last verified against codebase: 2026-09-06 (รอบ 137 — **ไปป์ไลน์ OCR → เอกสาร**:_
_ตั้งทีมตรวจ 5 ด้าน (สมองนักบัญชี · วิศวกรรมการสกัด · สถาปัตยกรรมการเรียนรู้/AI ·_
_UX 1-click · คุณภาพ/ตัวชี้วัด) แล้วแก้ 29 ข้อที่ยืนยันด้วยการเปิดไฟล์เอง —_
_รายละเอียดครบใน `OCR_PIPELINE_REVIEW_2026-09-06.md` §2/§2b · ที่เหลือเป็น backlog §3._
_จุดที่ flow เปลี่ยนจริง: ด่านก่อนสร้าง/อนุมัติ 5 ข้อใน §2.2 · fallback chain_
_(นักเรียนตอบแทนครูได้ + ไม่แต่งบรรทัดให้ยอดตรง) · เส้น `create-journal-entry`_
_บล็อก §82/5 และใช้ผังภาษีชุดเดียวกับเส้นเอกสาร)_

_Last verified against codebase: 2026-09-06 (รอบ 138 — **ปิด backlog P1 ของไปป์ไลน์ OCR**:_
_4 ชุดงานตามความเสี่ยง (ความทนทาน · สมองนักบัญชี · การสกัดข้อมูล · UX) —_
_รายละเอียดครบใน `OCR_PIPELINE_REVIEW_2026-09-06.md` §2c._
_จุดที่ flow เปลี่ยนจริง: สร้างเอกสารจากสแกนถูกล็อกต่อใบ · ลบสแกนที่ลง JE ไม่ได้ ·_
_เส้น OCR บังคับธง `InputVatClaimable` + อัตราแลกเปลี่ยนเหมือนเส้นคีย์มือ ·_
_auto-approve ตัดสินด้วย `Helpers/OcrPostingReadiness` ตัวเดียวทุกช่องทาง ·_
_`Document.IncomeTypeCode` ถูกเซ็ตจากสแกนเมื่อมีการหัก ณ ที่จ่าย)_

---

_Last verified against codebase: 2026-09-06 (รอบ 139 — **ตรวจย้อนงานรอบ 137**:
จุดที่ flow เปลี่ยนจริง: ด่านสิทธิ์ "สร้างเอกสารจากสแกน" ใช้
`Helpers/OcrTargetDocumentType` ตัวเดียวกับเส้นที่สร้างเอกสาร จึงไม่ถูกข้ามอีก
เมื่อสแกนยังไม่มี `TargetDocumentType` หรือเป็นใบมัดจำ · เส้น "บันทึก JE ตรง"
ฝั่งซื้อโยน `OCR-JE-NO-WHT-LIABILITY` เมื่อผังไม่มี 21916/21917 แทนที่จะทิ้ง
บรรทัดหักภาษีเงียบ ๆ · คำตอบของ local model ถูกใช้ในทุกเส้นที่ AI ไม่พร้อม
ผ่านธง `AiResponse.FromLocalModel` (kill-switch กฎเหล็ก #1 ข้อ 5))_


---

_Last verified against codebase: 2026-09-06 (รอบ 140 — **ปิดลูปการสอนที่เอกสารที่ลงจริง**:
`ApproveDocumentAsync` เรียก `SyncScanToPostedDocumentAsync` ต่อจาก
`RecordLineAccountFeedbackAsync` — sync แถว `OcrScanResult` ต้นทาง (ค้นด้วย
`CreatedDocumentId`) ให้ตรงกับเอกสารที่อนุมัติ รวม `ExtractedItemsJson` จากบรรทัดที่ลงจริง
⇒ นักเรียนที่ mine จาก "สแกนที่สร้างเอกสารสำเร็จ" ได้ความจริงหลังผู้ใช้แก้ Draft.
ตัวตัดสินช่องที่เปลี่ยนอยู่ใน `Helpers/OcrPostedTruth` (ห้ามลบค่าเดิมด้วยช่องว่าง/ศูนย์ ·
อนุมัติซ้ำไม่เปลี่ยนอะไร) · best-effort: ล้มแล้วการอนุมัติต้องไม่ล้ม)_

---

_Last verified against codebase: 2026-09-06 (รอบ 142 — **สมุดที่มาของค่ารายช่อง (D1 เฟส 1)**:
ทุกแหล่งที่เสนอค่าเรียก `OcrExtractedData.Note(...)` แล้ว `Helpers/OcrFieldArbiter`
ตัดสิน/บันทึกลง `OcrScanResult.FieldDecisionsJson` → `OcrResultResponse` → แผง
"ค่านี้มาจากไหน" ในหน้า review. **ค่าที่ใช้จริงยังมาจากลำดับเดิมทุกช่อง** — เฟสนี้
เปลี่ยนแค่ "ตรวจสอบที่มาได้" ยังไม่ย้ายตัวตัดสิน)_


---

_Last verified against codebase: 2026-09-07 (รอบ 147 — **e-Tax: หยุดโกหกว่าส่งแล้ว +
ด่านสิทธิ์ + เลขรัน**: `SubmitToRevenueAsync` ไม่ประทับ `Submitted` เมื่อยังไม่ได้ตั้งค่า
RD API (เดิมประทับ + ล็อกเอกสารถาวร) · `EtaxController` 10 write endpoint ผ่านด่าน
`RequireEtaxAsync` + เพิ่มไฟล์เข้า `WATCHED` ของ checker (เดิมรายงานเขียวตลอดเพราะไม่เคยมอง
ไฟล์นี้) · `EtaxRefNumber` ย้ายไป `SequenceNumber`. ผลตรวจ 7 ทีมรอบนี้อยู่ใน
`SYSTEM_AUDIT_2026-09-07.md`)_

_Last verified against codebase: 2026-09-10 (รอบ 150 — **สินทรัพย์ ↔ เอกสาร สองทาง + ผังบัญชี
ที่แก้ต้องมีผล**: ตัวขึ้นทะเบียนจากเอกสารย้ายไป `FixedAssetService.RegisterFromDocumentAsync`
ตัวเดียว (ขั้นอนุมัติมอบต่อ · ปุ่มขึ้นทะเบียนย้อนหลังรายบรรทัดในหน้าเอกสาร) · หน้าเอกสาร/
หน้าทะเบียนลิงก์หากันจาก endpoint เดียว · `AccountCode` ที่ resolve ไม่ได้ล้มดังแทนตกผัง
default เงียบ · หมวดค่าใช้จ่ายหัวเอกสาร→บรรทัดตาม `userTouched`)_

_Last verified against codebase: 2026-09-10 (รอบ 151 — **รายงานภาษีขายไม่กินแถวสรุปฝั่งซื้อ +
ใบกำกับยกหัวเป็นใบเสร็จได้เฉพาะรับเงินวันเดียวกับวันที่ใบ**)_

**(ก) รายงานภาษี §87 / ภ.พ.30 — ตัวจำแนกบรรทัดตัวเดียว `Helpers/VatReportLineKind`**
_ผู้ใช้รายงาน: รายงานภาษีขาย 08/2569 มีแถวแรกเป็น "ยอดซื้อที่ได้รับยกเว้นภาษี (§81)"
ลงวันที่ **01/01/0544** ยอด 7,151 และถูกบวกเข้ายอดรวม 3,234,571.84 ⇒ ยอดขายที่ยื่นเกินจริง._
_ต้นเหตุ: กติกา "บรรทัดนี้ฝั่งไหน" ถูกเขียนไว้ **5 ที่** — `tax.html` · `TaxService.Export`_
_· `NormalizeReportLineOrder` ใช้ allow-list 3 ทาง (ถูก) แต่ `PdfGenerationService.TaxReport`_
_ใช้ **deny-list** ("ไม่ใช่ INPUT/JE_INPUT = ขาย") ⇒ แถวสรุป `EXEMPT` (ยอด**ซื้อ**ยกเว้น) และ_
_`VAT_CREDIT_CF` ตกเข้ารายงาน**ขาย** พร้อม `default(DateTime)` ที่ render เป็น พ.ศ. 0544._
_บั๊กที่สองในไฟล์เดียวกัน: `ComposePp30` ยังอ่าน `"EXEMPT"` เป็น "ยอด**ขาย**ยกเว้น" ทั้งที่รหัส_
_ถูกแยกเป็น `EXEMPT_SALES`/`ZERO_RATED_SALES` ไปแล้ว ⇒ **ช่อง 7 กับ 8 สลับกัน** และช่อง 7 ถูก_
_นับซ้ำ (แถวเอกสารเก็บฐานที่ไม่ยกเว้นซึ่งรวมฐาน 0% อยู่)._
_แก้: ยุบเป็น `VatReportLineKind` ตัวเดียว (`SideOf` · `IsDocumentRow` · `BelongsToDetailReport`_
_· `IsNoteFor` · `SalesBoxOf` · `StandardBase`) — ช่อง 5 คิด**แบบลบ** (`Σ แถวเอกสาร − ยอด 0%`)_
_⇒ ช่อง 5+7+8 = ยอดขายทั้งหมดพอดี · ยอดที่ตัดออกจากตาราง §87 ไปโผล่เป็น **บล็อกหมายเหตุ**_
_ท้ายรายงานฝั่งที่ถูก (ห้ามหายเงียบ) · หน้าเว็บ **แสดง** `TaxReportLineResponse.Side` ที่เซิร์ฟเวอร์_
_คำนวณมา ไม่คำนวณเอง (สำเนามือ 2 ชุดใน `tax.html` ถูกลบ) · เทสต์ `VatReportLineKindTests`_

**(ข) หัว "ใบเสร็จรับเงิน" ต้องผูกกับวันที่รับเงินจริง (ม.105)**
_ผู้ใช้รายงาน: กด "บันทึกชำระเงิน" บนใบ `TIV-20260805-0005` (5 ส.ค.) โดยเงินเข้าคนละวัน แล้ว_
_**ใบเดิม**เปลี่ยนเป็น "ใบแจ้งหนี้/ใบกำกับภาษี/ใบเสร็จรับเงิน" — ถามว่าไม่ควรออกใบเสร็จ**แยก**_
_เหมือนการแปลงเอกสารหรือ. ถูกต้อง: กระดาษที่ลงวันที่ 5 ส.ค. ประกาศว่ารับเงิน 5 ส.ค. = **ใบรับที่_
_ลงวันที่เท็จ** (ป.รัษฎากร ม.105 ใบรับต้องออกทันทีที่รับเงินและลงวันที่ที่รับจริง) และไม่ตรงกับ_
_50 ทวิ ที่ผู้จ่ายออกตามวันจ่ายจริง. โค้ดเดิม**ไม่เคยเทียบวันที่เลย** ทั้งที่ doc-comment ของทั้ง_
_สอง mirror เขียนไว้เองว่า "ชำระครบ **ณ วันออก** (same-day settlement)" และ hint ในฟอร์มก็เขียนว่า_
_"รับเงินภายหลังระบบออกใบเสร็จแยกให้" — เจตนาถูก โค้ดไม่ทำตาม (กฎเหล็ก #4)._
_แก้: `ReceiptIssuePolicy.SettledSameDay` / `WhyNotCombined` (เหตุผลเป็นข้อความไทยที่เอาไปโชว์ได้_
_· `InvariantCulture` เพราะ th-TH render พ.ศ.) + `DocumentService.LoadSettlementDatesAsync`_
_(MAX วันที่รับชำระที่ยังไม่ถูกยกเลิก รวม `PaymentAllocation`; batch ต่อหน้า ไม่ใช่ N+1) ต่อสาย **4 จุด**:_
_`ComputeServedAsReceipt` (พารามิเตอร์ `settledOn` **บังคับ** — ให้ทุก call site ตัดสินใจ) ·_
_`PdfGenerationService.ResolveServedAsReceiptAsync` (เจ้าของกฎตอน render) · `TaxService` (ป้ายในรายงาน) ·_
_`CreatePaymentAsync` — `combinedSelfReceipt` ต้องผ่าน `WhyNotCombined` ⇒ คนละวัน = เดินเส้นเดิม_
_`CreateSettlementReceiptAsync` ซึ่งออก REC ลงวันที่ `payment.PaymentDate` อยู่แล้ว._
_ฝั่งหน้าเว็บ: โมดัลรับชำระเคยมี**สำเนามือ**ของกติกานี้ (`tivFull`) ที่ไม่ดูวันที่ ⇒ ปลดติ๊ก "ออกใบเสร็จ"_
_ให้เองแล้วลูกค้าจะไม่ได้ใบเสร็จเลยสักใบ — เพิ่มเงื่อนไขวันเดียวกัน + คำนวณใหม่เมื่อผู้ใช้เปลี่ยนวันที่._
_ไม่รู้วันรับเงิน (ปิดยอดด้วยการหักมัดจำ/ข้อมูลก่อนย้ายระบบ) = **ไม่ยกหัว** ("ไม่รู้ = บอกว่าไม่รู้")._
_เอกสารเก่าที่เคยได้หัว 3-in-1 แบบผิดจะกลับเป็น "ใบกำกับภาษี" เอง (คิดตอน render ไม่ได้ persist) —_
_ทางออกให้ลูกค้าคือ **แปลงเอกสาร → ใบเสร็จรับเงิน** (`TaxInvoice → Receipt` มีใน `ValidConversions` แล้ว)._
_เทสต์ `CombinedReceiptSameDayTests` (ล็อกสองทิศ) + `PaidOnIssueHeaderTests` mirror ตามไปด้วย_

_Last verified against codebase: 2026-09-11 (รอบ 152 — **ทางไปต่อของการรับชำระที่ไม่มีเอกสารคู่**:
`POST document/payments/{id}/receipt` → `IssueReceiptForPaymentAsync`)_

_ผู้ใช้รายงานต่อจากรอบ 151: ใบ `TIV-20260805-0005` กลับเป็น "ใบกำกับภาษี" ถูกต้องแล้ว **แต่**
การรับชำระที่บันทึกไว้ก่อนการแก้ (`PAY-202609-0010` · JE `RV-202609-0006` Dr ธนาคาร 111,800 +
Dr ภาษีถูกหัก 3,225 / Cr ลูกหนี้ 115,025) ยังเหลือ **JE รับเงินที่ไม่มีเอกสารใบรับให้ลูกค้า** —
เพราะเส้นเดิม `combinedSelfReceipt` กด REC ทิ้งไปตั้งแต่ตอนนั้น. **แก้โค้ดอย่างเดียวไม่พอเมื่อของ
เสียถูก persist ไว้แล้ว** (defect class เดิมของเรพนี้ — `OcrLearnedPatterns` · `VendorKnownGoodValues`
· `AiCallStatus` · e-Tax `OFFLINE-`) แต่ที่นี่ **ห้ามเขียน migration ไล่สร้างเอกสารย้อนหลัง**
เพราะเลข §86/4 ต้อง gap-free และต้องตรวจสอบได้ว่า "ใครเป็นคนออก" → เปิดเป็นปุ่มให้คนกดทีละใบ_

| ส่วน | รายละเอียด |
| --- | --- |
| ทางเข้าใหม่ | `POST /api/companies/{companyId}/document/payments/{paymentId}/receipt` — ด่านสิทธิ์ `DenyDocAsync(Receipt, Create)` (อยู่ในลิสต์ `write_permission_gate_check` แล้ว) |
| ผลลัพธ์ | ใบเดียวกับที่เส้นบันทึกรับชำระออกให้ (`CreateSettlementReceiptAsync`) — `DocumentDate = payment.PaymentDate` (ม.105) · `IsSettlementReceipt=true` · `SettlementPaymentId` · **ไม่ลง JE ซ้ำ** · **ไม่คิด VAT ซ้ำ** |
| idempotent | มีใบอยู่แล้ว → คืนใบเดิม + `AlreadyExisted=true` (ไม่สร้างซ้ำ) · ค้นสองทาง (`Payment.ReceiptDocumentId` **และ** `Document.SettlementPaymentId`) แล้วซ่อม FK ที่ขาดให้ด้วย |
| กันกดสองครั้ง | `FOR UPDATE` บนแถว `Payments` **ภายใน** transaction (บทเรียนเดิมของ `VoidPaymentAsync` — lock นอก tx ถูกปล่อยทันที) |
| สิทธิ์ผู้กด | มีสิทธิ์อนุมัติ `Receipt` → ใบสมบูรณ์ เลขจริงทันที · ไม่มี → `Draft` (`DRAFT-…`) รอผู้มีสิทธิ์อนุมัติ |
| ที่ปฏิเสธ | `Helpers/SettlementReceiptPolicy.WhyCannotIssue` — การชำระถูกยกเลิก · เงินก้อนเดียวกระจายหลายใบ (ให้ไปใช้ "แปลงเอกสาร → ใบเสร็จรับเงิน") · ต้นทางไม่ใช่ฝั่งรับเงิน · ต้นทาง Voided/Rejected/Draft |
| หน้าเว็บ | ตาราง "ประวัติการชำระเงิน" ในหน้ารายละเอียดเอกสาร เพิ่มคอลัมน์ **ใบเสร็จ** — มีใบ → ลิงก์ไปใบนั้น · ไม่มี → ปุ่ม "🧾 ออกใบเสร็จ" (ห้ามปล่อยช่องว่างเงียบ) |

**helper ใหม่ `Helpers/SettlementReceiptPolicy`** — เกณฑ์ "ใบเสร็จนี้ถือ VAT (= ใบกำกับภาษี
ณ วันรับเงิน §78/1) หรือเป็นใบรับเปล่า" เดิมเขียนอยู่ที่เส้นบันทึกรับชำระที่เดียว
(`Invoice && VatAmount > 0 && singleShotFull`) — พอเปิดเส้นที่สอง ถ้าคัดลอกไปวางอีกชุดจะได้
**ใบเดียวกันถือ VAT หรือไม่ถือ ขึ้นกับว่าผู้ใช้กดปุ่มไหน** ⇒ ยุบเป็นตัวเดียว
(`CarriesTaxInvoiceRole` · `IsReceivableSource` · `WhyCannotIssue`) + เทสต์
`SettlementReceiptPolicyTests` (ล็อกทั้งเคสที่ต้องออกได้และทุกเหตุผลที่ปฏิเสธ)

---

_Last verified against codebase: 2026-09-11 (รอบ 153 — **ปุ่ม "ออกใบเสร็จ" ต้องไม่โผล่บนใบที่
เป็นใบเสร็จในตัวอยู่แล้ว**)_

_ผู้ใช้รายงานต่อจากรอบ 152: ใบ `TIV-20260805-0007` (ลงวันที่ 5 ส.ค. 2569) รับเงินด้วย
`PAY-202608-0007` **วันเดียวกัน** ⇒ `ServedAsReceipt` เป็นจริง ⇒ หัวกระดาษพิมพ์
"ใบกำกับภาษี/ใบเสร็จรับเงิน" อยู่แล้ว — แต่คอลัมน์ "ใบเสร็จ" ที่เพิ่งเพิ่มรอบ 152 ตัดสินจาก
`receiptDocumentNumber` **ช่องเดียว** จึงยังเสนอปุ่ม "🧾 ออกใบเสร็จ" ⇒ กดแล้วได้กระดาษใบรับ
**ใบที่สอง**ของการรับเงินก้อนเดิม ซึ่งเป็นสิ่งที่ `ReceiptIssuePolicy` ทั้งไฟล์เขียนขึ้นมาเพื่อกัน_

| ส่วน | รายละเอียด |
| --- | --- |
| ตัวตัดสิน | `PaymentResponse.SourceServesAsReceipt` — **เซิร์ฟเวอร์คำนวณ** ด้วย `ComputeServedAsReceipt` ตัวเดียวกับที่ตัดสินหัวกระดาษ แล้วกรองต่อด้วย `ReceiptIssuePolicy.CoversPayment(served, docDate, paymentDate)` |
| ทำไมต้องเทียบวันที่ของ**รายการ**ด้วย | ใบผ่อนหลายงวดที่งวดสุดท้ายบังเอิญตรงวันที่บนใบ จะได้ธงระดับเอกสารเป็นจริง — แต่งวดก่อนหน้าที่รับเงินคนละวัน **ยังไม่มีกระดาษใบรับ** ถ้าปิดปุ่มทั้งแถวผู้ใช้จะออกใบให้งวดนั้นไม่ได้เลย |
| ด่านฝั่งเซิร์ฟเวอร์ | `SettlementReceiptPolicy.WhyCannotIssue(..., sourceServesAsReceipt)` — พารามิเตอร์ **ไม่มีค่าเริ่มต้น** (ผู้เรียกทุกรายต้องคำนวณมา) · `IssueReceiptForPaymentAsync` ปฏิเสธพร้อมเหตุผลไทย ⇒ ยิง API ตรงก็ไม่ผ่าน ("ด่านที่ครอบแค่ทางเดียว คือด่านที่ไม่มี") |
| หน้าเว็บ | แสดง **"ใบนี้เป็นใบเสร็จในตัว"** แทนปุ่ม พร้อม tooltip บอกเหตุผล + ทางไปต่อ (ตั้งค่าบริษัทเป็น "แยกใบกำกับภาษี–ใบเสร็จรับเงิน") — ห้ามซ่อนเงียบ |
| กันสำเนามือ | เงื่อนไข "ใบนี้มีใบเสร็จแยกอ้างอยู่ไหม" เคยถูกคัดลอก 3 ที่ → ยุบเป็น `DocumentService.LoadDocIdsWithSeparateReceiptAsync` (batch, ไม่ใช่ N+1) |

**บทเรียน**: ธงที่ตัดสิน "ควรเสนอปุ่มไหม" ต้องมาจาก**ตัวตัดสินเดียวกับที่ตัดสินผลลัพธ์จริง**
(ที่นี่คือหัวกระดาษ) — การดูช่องเดียวที่ "ใกล้เคียง" (`ReceiptDocumentNumber` = มี REC แยกไหม)
ตอบคำถามคนละข้อกับ "การรับเงินนี้มีหลักฐานใบรับแล้วหรือยัง" เพราะใบต้นทางเองก็เป็นหลักฐานได้


---

_Last verified against codebase: 2026-09-16 (รอบ 162 — **ไฟล์ยื่น ภ.ง.ด.3/53/54 ได้คอลัมน์
คำนำหน้าชื่อ (Col12) และปุ่ม e-Filing เลิก drift จากเมนูส่งออก 4 ช่อง**: คำนำหน้าไม่เคยมีที่เก็บ
เลยทั้งระบบ (`Contact` มีแต่ `Name` — ต่างจาก `Employee.TitleTh`) และ `SplitName` **ตัดทิ้ง**
ตาม doc-comment ที่เขียนสารภาพไว้เอง ⇒ เพิ่ม `Contact.TitleTh` ครบสาย (ฟอร์ม → payload →
DTO ×3 → entity → `ADD COLUMN` → mapper → hydrate/reset) · ตารางคำนำหน้าที่เคยมี **3 ชุด
ไม่ตรงกัน** (`PndTextFileFormat.ThaiTitles` ไม่รู้จัก เด็กชาย/เด็กหญิง · `TaxFilingExportService.TitleCode`
ไม่รู้จัก ดร. · `ThaiTitleHelper.SsoValidTitles`) ยุบเข้า **`Helpers/ThaiTitleHelper`** ตัวเดียว
+ เปิด `/api/reference/titles` ให้หน้าเว็บสร้าง dropdown (กลไกเดียวกับ `ThaiWhtRateTable` +
`/api/reference/income-types`) · ตัวแยกมี **ด่านกันตัดชื่อกิจการ** ("นายช่างการไฟฟ้า" ·
"นางเลิ้งพาณิชย์" ไม่ถูกแตะ) · **Col1–Col11 ไม่ขยับแม้แต่ช่องเดียว** (ยืนยันกับผู้ใช้ว่า Col3
ของ ภ.ง.ด.3 คือ *เลขที่สาขา* เหมือน 53 — สมมติฐานเดิมใน `TODO_OPUS.md` ที่เดาว่าอาจเป็น
คำนำหน้า **ไม่จริง**) ⇒ column-mapping ที่ผู้ใช้บันทึกไว้บนเว็บ RD ยังใช้ได้ · ฝั่งปุ่ม e-Filing
เพิ่ม `TaxReportLine.TaxPayerTitle/TaxPayerBranchCode/WhtCondition` แล้วเติมจาก cert ⇒ Col3/
Col7/Col11/Col12 ตรงกับเมนูส่งออก (เดิม: สาขา 00000 ทุกแถว · รหัสประเภทเงินได้ดิบ `"40(8)"` ·
เงื่อนไข 1 เสมอ) · `MapIncomeTypeCode` ย้ายเป็น **`Helpers/PndIncomeTypeCode`** ให้ทั้งสองทางใช้
— ดู `PndTextFileFormatTests` RDF-06..12 · รอบ 161 — **"หา JE ไม่เจอ" ถูกแปลว่า "ยังไม่อนุมัติ"
มาตลอด**: `LoadGlPostingAsync` ติดป้าย "(ประมาณการ — ก่อนอนุมัติ)" ให้ทุกกรณีที่ไม่มี JE โดยไม่เคย
ตรวจสถานะเอกสารเลย ⇒ ใบที่ **อนุมัติแล้วแต่ JE ถูกลบ/ถูกกลับรายการ** (ภ.พ.30 นับแล้ว แต่ GL ว่าง —
อาการหนักที่สุดที่ระบบมี) อ่านออกมาเป็น "ยังไม่อนุมัติ" ⇒ ผู้ใช้ไล่ผิดทาง · แยกตัวตัดสิน "ใบนี้ควรมี
JE ไหม" ออกจาก local `autoPostTypes` ใน `ApproveDocumentAsync` เป็น **`Helpers/DocumentJournalExpectation`**
ให้ทั้งเส้นอนุมัติและเส้นแสดงผลถามตัวเดียวกัน + ป้ายอ่านสาเหตุจริงจากฐาน (ลบ/กลับรายการ/ค้าง Draft)
— เทสต์ล็อกสองทิศใน `DocumentJournalExpectationTests` (ใบที่ควรมีต้องจับได้ · ใบเสนอราคา/ใบวางบิล/
ใบยกเลิก/ใบเสร็จหลักฐาน/ใบออกแทน ต้องไม่ถูกเตือน) · รอบ 160 — **พรีวิว GL "ประมาณการ — ก่อนอนุมัติ"
ไม่เคยรู้จัก §83/6 ภ.พ.36 เลยสักวัน**: `BuildProjectedGlAsync` ไม่มีบรรทัด `Cr 21912` และเครดิต
เจ้าหนี้/ธนาคารด้วย **ยอดรวม VAT** ขณะที่ `AutoPostToJournalAsync` (รอบ 94) แยกถูกมาตลอด ⇒ ผู้ใช้
เทียบใบเก่าที่อนุมัติแล้วกับใบใหม่ที่ยังไม่อนุมัติ แล้วเห็นว่า "ระบบบันทึกเปลี่ยนไปเป็นผิด" ทั้งที่
ตัวลงบัญชีไม่ได้เปลี่ยน · ตัวแยกขาเครดิตยุบเป็น **`Helpers/ForeignServiceVat.SplitCredit`** ที่
ทั้ง 3 เส้นเรียก (PI/Expense · PV · พรีวิว) + ผัง 21912/11640 เป็นคอนสแตนต์ร่วม · พรีวิวบังคับ
11640 เมื่อเป็น ภ.พ.36 (ไม่รอธง `InputVatPostedAsUndue` ที่เซ็ตตอนอนุมัติเท่านั้น) · และแก้
"Cr ชื่อบัญชีธนาคาร ไม่มีเลขผัง" → resolve `BankAccount.LinkedAccount` แบบเดียวกับ JE จริง
(prefix-search 111 เมื่อยังไม่ผูกผัง) — ดู `ForeignServiceVatTests` · รอบ 157 — §2.2c ใบรับรองแทนใบเสร็จตัดสินจาก **หลักฐานผู้รับเงิน** (`OcrPayeeEvidence`) ไม่ใช่ "ไม่มีเลขภาษี" · รอบ 156 — **ขั้นตัดสินกลาง "ชุดข้อมูลไหนคือใคร"**
`Helpers/OcrPartyResolver` + ต่อสาย `AiFeatureKey.DocumentRoleInference` ที่มี enum มานานแต่ไม่มี
prompt/call site/student)_

_ทางเข้า OCR (ข้อ 10): ลำดับใน `OcrService.ScanAsync` ตอนนี้คือ engine → `SmartFieldExtractor.Enrich`
(ขยายชื่อ · ตัดเศษชื่อจากที่อยู่ · ผ่าที่อยู่ที่ถูกต่อกัน · ป้ายจาก `OcrPartyLabels`) →
`DocumentNumberSanitizer` → แพตเทิร์นที่เรียนไว้ (ผ่าน sanitizer ซ้ำ) → **`OcrPartyResolver`**
(ตัวตนจาก DB > ป้ายบนกระดาษ > ช่องที่ engine ใส่ · ล้างคู่ค้าที่เป็นเราเอง · ย้ายค่าเฉพาะที่ปลายทางว่าง)
→ `OcrDocumentRoleInferrer.Infer` → **AI บทบาท** เฉพาะ `ShouldAskAi`/conf < 0.7 (ด่าน: ∈ {Buyer,Seller}
· ≥ 0.70 · ห้ามขัดเลขภาษีที่ตรงกับเรา) → Infer ซ้ำด้วย `roleOverride` → sync ลงแถวสแกน**ครบทุกช่อง
ที่จุดเดียว** (รวม `ExtractedVendorName/TaxId` ที่เดิมถูกเขียนก่อนขั้นตัดสินแล้วไม่มีใคร sync) →
`AzureDiPatternLearner` (ย้ายมาหลังทุกขั้นแก้ไข + ปฏิเสธเมื่อผู้ขาย = เรา) → จับคู่ Contact
(ด่านใหม่: ห้ามจับคู่เป็นบริษัทเราเอง ทั้งทางเลขภาษี/substring/fuzzy/สร้างใหม่)_

_Last verified against codebase: 2026-09-16 (รอบ 163 — **สองหน้าต้องเล่าเรื่องเดียวกัน**:
ผู้ใช้พบ ภ.ง.ด.53 งวด ก.ค. หน้า "รายงานภาษี" ถูก แต่หน้า "นำส่งภาษี" คนละยอด. ต้นเหตุ
สามชั้นซ้อนกัน — (1) หน้านำส่ง + ปฏิทินยื่น query เอกสารที่ `WithholdingTaxAmount > 0`
**โดยไม่กรองชนิดเอกสาร** ⇒ ใบขายที่ลูกค้าหักเราไว้ (เครดิตภาษี**ของเรา** Dr 11910)
ถูกนับเป็นหนี้นำส่ง (2) ใบตั้งหนี้ + ใบสำคัญจ่ายก้อนเดียวกันนับสองครั้ง (3) แบ่ง 3/53
จาก `ContactType` ดิบ ๆ ขณะที่ทะเบียน 50 ทวิ/รายงาน/ไฟล์ยื่นใช้ตัวตัดสิน 3 สัญญาณ
⇒ ยอดรวมเท่ากันแต่ช่องแบ่งไม่ตรง. ยุบเป็น `Helpers/WhtRemitScope` + `Helpers/WhtPayeeKind`
ตัวเดียวที่ทุกเส้นเรียก (รวม `DetectJuristic`/`ResolveWhtFormType` ที่ delegate มา) ·
เพิ่ม **ภ.ง.ด.54** ที่เดิมตกหล่นจาก `order`/`FilingRule`/`ReportTypeOf` ทั้งสามที่ ·
และหน้านำส่งอ่าน `TaxReport.Status/FiledDate` แล้ว ⇒ งวดที่ยื่นแบบไปแล้วเลิกขึ้น
"เลยกำหนด N วัน" สีแดง เปลี่ยนเป็น "ยื่นแบบแล้ว · รอบันทึกการนำส่งเงิน"
— commit ff635b0)_

_Last verified against codebase: 2026-09-16 (รอบ 164 — **ตั้งทีมกฎหมาย + ทีมฝ่ายค้าน
ไล่ตรวจแล้วพบว่าการแก้ของรอบก่อนผิดเองสองข้อ**: (1) การ์ด VAT บนหน้าแรกเปลี่ยนไปใช้
`Σ NetVat` ซึ่ง **หักเครดิตยกมาซ้ำสองรอบ**เมื่อช่วงครอบหลายเดือน (เครดิตยกมาของเดือน
หลัง = NetVat ติดลบของเดือนก่อนซึ่งอยู่ในผลรวมอยู่แล้ว) ⇒ จำกัดให้ใช้เฉพาะช่วงเดือนเดียว
+ เปลี่ยนป้ายหลายเดือนเป็น "สะสมในช่วง" (2) `DocumentVatFallback.ClaimableVat` ที่เพิ่ม
ไปเป็น **no-op บนเส้นทางจริง** (`InputVatPostedAsUndue` ถูกตั้งข้างใน
`if (claimableVatPi > 0)` ซึ่งรวมจาก `doc.Lines` เหมือนกัน ⇒ ใบไม่มีบรรทัดไม่เคยโผล่
หน้านั้น) และสาขา header-only ของมัน **ข้ามธง §82/5** ⇒ ถอดทิ้ง · บั๊กจริงของบรรทัดนั้น
คือ **ไม่แปลงสกุลเงิน** (ใบ USD FX 36 โชว์ 70 ทั้งที่ GL ถือ 2,520) แก้ด้วย `ToGlAmount`
เหมือนเส้นย้าย 11640→11610 · พร้อมกันนี้: ยุบตาราง**กำหนดยื่น 3 ชุด**เหลือ
`Helpers/TaxFilingDeadline` ตัวเดียว (แก้ ภ.พ.36 ที่เคยได้วันที่ 23 · เพิ่ม ภ.ง.ด.54
ที่หายจากปฏิทิน · เลื่อนวันหยุดตาม ป.พ.พ. §193/8 · ภ.ง.ด.50 นับ 150 วันจากสิ้นรอบ
ตาม §69 แทน 31 พ.ค. ตายตัว) · เงินเพิ่ม ปกส. §49 เลิกนับเดือนด้วย `ceil(วัน/30)`
ซึ่งคิดเกิน 1 งวดทุกรอยต่อเดือน 31 วัน · และไฟล์ยื่น ภ.ง.ด.1/สปส.1-10/ภ.ง.ด.1ก
รับเฉพาะรอบ **Approved/Paid** (เดิมรวม `Calculated` ที่ยังไม่มีใครอนุมัติ)
— commit 0393d2f)_

_Last verified against codebase: 2026-09-16 (รอบ 165 — **นิยาม "ต่างประเทศ" และ "3 vs 53"
เคยมี 4 ชุด ⇒ เงินก้อนเดียวกันขึ้นคนละแบบยื่นแล้วแต่ว่าใครถาม**: `DocumentService`
`ResolveWhtPayableAccountAsync` (ผังค้างจ่าย 21916/21917/21918) · `WithholdingTaxCert`
`DetectJuristic`+`ResolveWhtFormType` (ทะเบียน 50 ทวิ) · `TaxService` (รายงาน/ไฟล์ยื่น) ·
หน้านำส่ง — ทั้งสี่ยุบมาที่ `Helpers/WhtPayeeKind.ResolveForm(isForeignService, countryCode,
taxId, contactType, name)` ตัวเดียว ⇒ ใบ Booking.com/AWS ที่เคยลง 21918 ใน GL แต่**หาย
จากไฟล์ ภ.ง.ด.54** (เพราะทะเบียนดูแต่ `CountryCode` ส่วน GL ดูแต่ `IsForeignService`)
กลับมาตรงกันทั้งสาย · พร้อมกันนี้ **11 จุดใน `TaxService.cs`** เลิกเขียนชุดสถานะเอง
แล้วใช้ `DocumentStatusRules.NotIssued` ⇒ เอกสาร `WaitingApproval` ที่ยังถือเลข
`DRAFT-{guid}` และยังไม่มี JE เลิกไหลเข้ารายงานภาษี/แบบยื่นทุกแบบ (เดิมไฟล์นี้ไม่มีคำว่า
`WaitingApproval` อยู่เลยสักบรรทัด) — commit 1bd9ecd)_

_Last verified against codebase: 2026-09-16 (รอบ 166 — **ด่านที่ doc-comment อ้างมาตลอด
ว่ามี กลายเป็นมีจริง**: `Helpers/SsoWageBase` เขียนไว้ว่า "ด่านตอนนำส่งจะบล็อกให้เอง
เงินไม่ออกไปผิด" แต่ `grep` ทั้งเรพพบว่า **ไม่มี call site ใน `RemitAsync` เลย** ⇒
รอบที่มีพนักงานฝั่งลูกจ้าง = 0 แต่ฝั่งนายจ้าง > 0 (ม.46 บังคับให้สองฝั่งใช้ฐานเดียวกัน
⇒ คู่แบบนี้คือข้อมูลเสียเสมอ) จะ **นำส่งเงินตามยอดที่หน้าจอนับ แต่ไฟล์ สปส.1-10 ไม่
ประกาศแถวนั้น** ⇒ เงินที่โอนไป ≠ ยอดที่ประกาศ แล้ว สปส. ตีกลับทั้งไฟล์. เพิ่มด่านจริง
ก่อนเปิด transaction ใน `StatutoryRemittanceService.RemitAsync` (RuleCode
`SSO-PAIR-CONFLICT`) คืน **รายชื่อ + รหัสพนักงาน + ยอดที่ไฟล์จะไม่ประกาศ + ทางไปต่อ
2 ทาง** และแก้ doc-comment ให้ชี้จุดที่ด่านอยู่จริง — commit d4a7c77)_

_Last verified against codebase: 2026-09-16 (รอบ 168 — **ตรวจงานของรอบ 162–167 เอง
ด้วยทีมผลกระทบ + ทีมฝ่ายค้าน + ทีมเก็บงานคงค้าง แล้วพบว่ารอบก่อนทำพังเองอีก 4 ข้อ**:
(1) 🔴 ด่าน `SSO-PAIR-CONFLICT` เขียน `d.IsSubjectToSocialSecurity` บน `PayrollDetail`
ซึ่งธงนั้นอยู่บน **`Employee`** ⇒ **CS1061 ล้มทั้ง solution** (checker 9 ตัวมองไม่เห็น —
กำแพง type resolution เดิม) · แก้ชนิดแล้วยังขยายด่านให้ครอบ "แถวที่มีเงินแต่ไฟล์ไม่
ประกาศ" ทั้งสองทรงผ่าน `Helpers/SsoFilingScope` ที่ exporter ใช้ตัวเดียวกัน
(2) ไฟล์ยื่น ภ.ง.ด.1/1ก/สปส.1-10 ที่บีบเหลือรอบ `{Approved,Paid}` ได้ผลเป็น **ไฟล์ว่าง
ตอบ HTTP 200** สำหรับรอบที่ค้าง `Calculated` (สถานะปกติของ flow) ⇒ หน้าเว็บขึ้น
"ดาวน์โหลดสำเร็จ" แล้วผู้ใช้อัปโหลดไฟล์ว่างเข้าเว็บราชการ — เพิ่ม
`EnsureFilableRunsAsync` ที่ล้มพร้อมบอกสถานะที่ค้างและปุ่มที่ต้องกด · และหน้าจอ
รายงาน ภ.ง.ด.1/ปกส. (ซึ่งกว้างกว่าไฟล์โดยตั้งใจ) ส่งธงรายแถว `InFilingFile` +
`ExcludedNote` มาแสดง ⇒ จอกับไฟล์เล่าเรื่องเดียวกัน
(3) `settledByPv` ของปฏิทินยื่นประกอบจาก PV **ทุกใบ** รวมใบที่ WHT = 0 (ใบ ภ.พ.36)
⇒ ใบตั้งหนี้ที่ PV แบบนั้นอ้างถึงถูกตัดทิ้ง **ยอด ภ.ง.ด.53 หายทั้งก้อน** ขณะที่หน้า
นำส่งได้ยอดเต็ม — ยุบเป็น `WhtRemitScope.SettledSourceIds` ที่บังคับ `wht != 0`
ตรงกับ `TaxService.GenerateWhtReport`
(4) ด่านกัน "นายช่างการไฟฟ้า" ทำให้ `"นายสมชาย"` (ไม่มีนามสกุล) ได้ Col4 ที่มีคำนำหน้า
ค้าง + Col12 ว่าง — แก้ด้วยการให้ **`Contact.TitleTh` ที่ผู้ใช้ยืนยันแล้ว** ตัดแบบ
deterministic (ไม่ใช่ให้ตัวเดาเดาหนักขึ้น ซึ่งจะพาเคสชื่อร้านพังกลับ) และแถวที่ยัง
แยกไม่ได้ **รายงานใน Summary** แทนการเดา
พร้อมกันนี้: ปฏิทินภาษีเปลี่ยนจาก "สร้างครั้งเดียวแล้ว throw" เป็น **upsert** (แถว
`Pending` อัปเดตวันให้ตรง `TaxFilingDeadline` · แถวที่ยื่นแล้วห้ามแตะ) · บล็อก annual
เลิกพิมพ์ `+8` เอง 6 จุดและเลื่อนวันหยุดทั้งขากระดาษและ e-Filing · `SetupApprovalAsync`
ห้ามพลิกเอกสารที่ **ออกเลข §86/4 + ลง JE แล้ว** กลับเป็น `WaitingApproval` (ซึ่งจะทำให้
ใบหายจากรายงานภาษี 13 จุดพร้อมกันเงียบ ๆ) · `today` ของหน้านำส่งใช้ UTC+7 ให้ตรงกับ
ปฏิทินในไฟล์เดียวกัน · เส้น dedup ผู้ติดต่อผ่านด่านคำนำหน้าเหมือนอีกสองเส้น
— commit 3470198)_

_Last verified against codebase: 2026-09-18 (รอบ 169 — **root cause ของการถดถอย + logic ซ้อน + สายข้อมูล**
รายงานเต็ม `REGRESSION_ROOT_CAUSE_2026-09-18.md`. สิ่งที่เปลี่ยน flow: (1) **ตารางกำหนดยื่นชุดที่ 4 ถูกยุบ** —
`ComplianceService.InitializeFilingCalendarAsync` (ปฏิทิน compliance · `POST compliance/initialize/{year}`) เลิกพิมพ์
ภ.พ.30/ภ.ง.ด.1/3/53/สปส.1-10 เอง 4 ลูป ⇒ ทุกแบบรายเดือนได้ `DueDate` จาก `Helpers/TaxFilingDeadline.For` ตัวเดียวกับ
หน้านำส่ง/ปฏิทินยื่น (เลื่อนวันหยุด ป.พ.พ. §193/8 ด้วย — เดิมไม่เลื่อน) · สปส.6-09 ใน `PayrollService.TerminateEmployeeAsync`
ผ่าน `TaxFilingDeadline.For("SsoSps609")` (วันที่ 15 · ไม่มี e-Filing +8 เหมือน `Sso*` ทุกตัว) · checker
`filing_deadline_single_source_check` จับที่ body shape แทนชื่อเมธอด (รุ่นเดิมรายงาน 0 ทั้งที่มีสำเนา)
(2) **ใบลดหนี้ผ่าน Integration API** (`IntegrationService` ~1205) เลิกลด `BalanceDue` เดี่ยว ๆ — ขยับคู่
`PaidAmount += min(CN, max(0, BalanceDue))` · `BalanceDue = max(0, Total − Paid)` แล้วสถานะตาม `BalanceDue` เหมือน
`DocumentService` (เดิม `Total − Paid ≠ Balance` ⇒ รับชำระบางส่วนครั้งถัดไปคำนวณทับ ยอด CN เด้งกลับเป็นยอดค้าง)
(3) **50 ทวิ PDF** พิมพ์คำนำหน้าบุคคลธรรมดาจาก `Contact.TitleTh` ผ่าน `ThaiTitleHelper.WithTitle` (ไม่ต่อซ้ำเมื่อชื่อมีอยู่แล้ว ·
ไม่ต่อให้นิติบุคคล) ให้ตรงกับที่ไฟล์ ภ.ง.ด.3 ประกาศใน Col12 (4) **PreClose checklist** ข้อ WHT นับเฉพาะ
`WithholdingTax3/53` + ลิงก์ `/pages/tax.html` (เดิมนับ `WithholdingTax1` ซึ่ง `TaxService` throw ไม่ให้สร้าง และลิงก์ไป
ทะเบียน 50 ทวิ) · **ไม่ได้เปลี่ยน**: ยอด WHT ค้างนำส่งยังนับจาก `Documents` ขณะที่ไฟล์ยื่นนับจาก `WithholdingTaxCerts`
(§4 #2 ของรายงาน — รอการตัดสินใจ "50 ทวิ auto-issue") · สถานะ "ยื่นแล้ว" ยังเก็บ 4 ที่ไม่ sync (§4 #6)
— commit ac91b71)_

_Last verified against codebase: 2026-09-18 (รอบ 170 — **คำตัดสินเจ้าของ 4 ข้อจาก REGRESSION_ROOT_CAUSE §7.3/§10**:
(1) **50 ทวิ ออกอัตโนมัติเป็น Issued ตอนจ่าย** (ท.ป.4/2528 ให้ออกในวันจ่าย) — hook ทั้ง 3 ใน
`DocumentService` (approve ที่จ่ายจบ · `CreatePaymentAsync` · ชำระหลายใบ) เปลี่ยน `autoIssue: false → true`;
เส้น integration ยังเป็น `autoIssue: paid` (PI ที่ยังไม่จ่าย = Draft รอจ่ายจริง) · void เอกสารยัง cascade void cert (6d)
(2) **หน้านำส่ง/ปฏิทินยื่น ภ.ง.ด.3/53/54 อ่านจาก `WithholdingTaxCerts` Issued/Printed** (ตัวตั้งเดียวกับ
`TaxService.GenerateWhtReport` · `TaxFilingExportService` · การ์ดแดชบอร์ด) แทน `Documents.WithholdingTaxAmount`
— สถานะ "นับเข้าแบบยื่น" ตัดสินที่ `Helpers/WhtCertFilingScope.Filed` ตัวเดียว (ยุบสำเนา 5 ที่) · เอกสารฝั่งซื้อที่หัก
WHT แต่ไม่มี cert ที่ออกจริง = **ช่องโหว่** ⇒ `PendingRemittanceItem.UnissuedWhtCount/Amount` + แถวเตือนบนหน้านำส่ง
(งวดที่ certs = 0 แต่มีช่องโหว่ **ยังขึ้นแถว** ไม่ `continue` ทิ้ง) · ช่องปฏิทิน NotRequired/Filed ที่มีช่องโหว่ → Unknown
+ ลิงก์ออกใบ · **`RemitAsync` บล็อก** ด้วย `BusinessRuleException("WHT-CERT-UNISSUED")` จนกว่าจะออกครบ —
ไม่นำส่งน้อยกว่าที่หักจริงเงียบ ๆ · ลิสต์ "รอออกใบ" ใช้ชุดชนิด `WhtRemitScope.PayerSideTypes` (เพิ่ม CertificateInLieu)
(3) ลบ `PayrollService.GeneratePnd3Async` + `GET payroll/pnd3` — สูตรที่ 3 ของ ภ.ง.ด.3 ที่ไม่มี UI เรียก (ต่อสายไม่ได้
เพราะกติกาผิดตั้งแต่ต้น: ไม่กรองฝั่งซื้อ · ไม่แยก 3/53 · ไม่ตัด PV ซ้ำ) (4) **CI เปิดบน `claude/**` อีกครั้ง**
(`.github/workflows/ci.yml`) แบบแก้ "เสียง" ไม่ปิด "ด่าน": paths-ignore `**.md` · concurrency cancel · job
`static-checks` (= `tools/check_all.sh --all --no-dotnet`) + `build` ทุก push · `test` เฉพาะ PR/main/dispatch —
agent อ่านผลผ่าน MCP หลัง push · **ยังค้าง**: สถานะ "ยื่นแล้ว" 4 ที่ไม่ sync (§4 #6) · dead helper 58 ตัวรอตัดสิน
— commit db5112d)_

_Last verified against codebase: 2026-09-18 (รอบ 170b — **โครงสร้างเอกสาร (คำตัดสินเจ้าของข้อ 4)**: CLAUDE.md กฎเหล็ก #4 F
ยุบจากบทเรียน 142 bullet (2,912 บรรทัด/448 KB) เหลือ **หลักการ 10 ข้อ + checklist ก่อน push 12 ข้อ + ข้อห้าม 7 ข้อ** (F2–F5 · 944 บรรทัด)
· บทเรียนดิบย้ายไป `docs/lessons/<หมวด>.md` 8 ไฟล์ 153 ข้อ **ไม่ลบเนื้อหา** (ดัชนี `docs/lessons/README.md`) · ประวัติราย "รอบ"
ของ DOCUMENT_FLOW (179 บล็อก · 2,900 บรรทัด) ย้ายมา `CHANGELOG.md` — DOCUMENT_FLOW เหลือสถานะปัจจุบัน §1–§9 + §10 บล็อกล่าสุด
· ไม่มีการเปลี่ยน flow ของโค้ดในคอมมิตนี้ — commit 38629a7)_

_Last verified against codebase: 2026-09-19 (รอบ 183 — **ปิด P0 ทั้ง 21 ข้อของ `DECISION_AUDIT_2026-09-18` พร้อมกัน**
เพราะหลายข้อมีรากเดียวกัน (R1–R7) แก้ทีละข้อจะเกิด "ด่านใหม่ทับด่านเก่า" ซ้ำอีก:
**R1 สถานะปลายทางประทับเอง** — ยื่นภาษี `Filed` + ล็อกงวดต้องมีเลขรับจากกรมสรรพากร (`Helpers/TaxFilingLockPolicy` ·
ไม่มีเลขรับ = `Submitted` "ประกาศไว้ ยังไม่ล็อก") · จับคู่ธนาคาร `Matched` ต้องมี id ของคู่เสมอ (`Helpers/BankMatchArbiter`) ·
เลิกประทับ `BuyerDeclinedTaxInvoice` จากการที่ผู้ใช้ไม่กรอกข้อมูลผู้ซื้อ · **R2 "ไม่รู้"→ค่าแต่ง** — คำเตือนก่อนอนุมัติ
ไม่มีใครตอบ = ไม่มีป้าย ไม่มี FeedbackId (`Helpers/AiHintAnswer`) · **R3/R4 สำเนา+ด่านซ้อน** — สูตรจับคู่ธนาคาร 5 สำเนา →
`Helpers/BankMatchScorer` ตัวเดียว · ลบด่าน WHT เก่า (§50) 62 บรรทัด + 2 เมธอดที่ยังรันต่อจากด่านใหม่ ·
**R5 ทางเข้าอื่น** — สถานะจ่าย 7 สำเนา 3 เกณฑ์ปัดเศษ → `Helpers/DocumentSettlementState` + ด่าน "จ่ายเกิน" ครบ 3 ทางเข้า ·
**R6 ลูปสอนตัวเอง** — "กระดาษพูดเรื่องหัก ณ ที่จ่ายไหม" อ่านจาก `Helpers/PaperWhtReader` ตัวเดียว (เดิมอ่าน `scan.HasWht`
ที่ `VendorIntelligence` เขียนเองจากประวัติ = ประวัติยืนยันประวัติตัวเอง) · **R7 ไม่มีเทสต์ที่ด่านเงิน** — ทุกข้อ extract เป็น
pure helper + เทสต์สองครึ่ง (`PosTaxInvoiceLines` · `PosRefundMath` · `PayrollIncomeNatureRules` · `RevenueCodeSurcharge`) ·
**ภ.พ.06**: บิล POS ของสาขาที่ยังไม่กรอกรหัสสาขา 5 หลัก ไม่ออกเลขใบกำกับอย่างย่อ (เดิมตกไป `00000` = "สำนักงานใหญ่"
⇒ กระดาษประกาศเท็จ + เลขรันสาขาไปกินเล่มสำนักงานใหญ่ ไม่ gap-free §86/4) · migration 4 ชุด — commit 30c2e02)_

_Last verified against codebase: 2026-09-19 (รอบ 184 — **7 ทีมผู้เชี่ยวชาญถกเถียงสองฝั่งแล้วลงมือ** (POS · ภาษี · เงินเดือน ·
ธนาคาร/เงินสด · OCR/AI · ข้อมูล/ทางเข้าภายนอก · สถาปัตยกรรมเอกสาร) — main agent เปิดไฟล์ยืนยันทุกข้อ P0 ก่อนรับ:
**คำสั่งเจ้าของ "คืนทั้งหมด"** — `Helpers/PosRefundMath` คืน**ค่าบริการ**ไปกับของ (บิล 1,000 ลด 10% ค่าบริการ 10%
ลูกค้าจ่าย 990 เดิมคืนได้แค่ 900 ⇒ JE เหลือรายได้ 90 + ภาษีขาย 5.89 ค้างถาวร) · invariant ที่ล็อกเป็นเทสต์:
คืนเต็มใบ ⇒ `Gross == order.TotalAmount` และ `Vat == order.VatAmount` เป๊ะ · ทิป/ค่าปัดเศษไม่คืน (เหตุผลใน doc-comment) ·
**ด่านสิทธิ์ POS 36 endpoint** + `ImportExportController` 8 (เดิมมีแค่ `[Authorize]` = "ล็อกอินไหม") ·
**บรรทัดเอกสารห้ามติดลบ** (`Helpers/DocumentLineKind`) — เดิม `Pp30SalesClassifier:68-70` พาบรรทัดส่วนลด `VatRate==0`
เข้า ภ.พ.30 ช่อง 7 (ยอดส่งออก) และ JE ได้เครดิตติดลบ ⇒ **P0: ออเดอร์หน้าร้านที่มีส่วนลดทุกใบ จ่ายเงินแล้วแต่ไม่เคยมี
เอกสาร/JE/ลูกหนี้/ภาษีขาย** (validator throw → ถูกกลืน) ⇒ ส่วนลดเฉลี่ยลง `DiscountAmount` รายบรรทัดแทน ·
**FX**: ทุกเส้นจับคู่ธนาคารเคยเทียบ `Payment.Amount` (สกุลเอกสาร) กับยอดบนบรรทัดธนาคารตรง ๆ ⇒ 30,780 USD กับ
30,780 บาท ได้ "ยอดตรงเป๊ะ" (`Helpers/BankMatchCurrency`) · **เช็คเด้ง**กลับรายการชำระทั้งชุด · **เงินสดย่อย**เติมเงิน
ต้องมี JE · **ตาราง WHT รู้จักเวลา** (1.5% = ท.ป.310/2563 ช่วงโควิด ไม่ใช่ e-withholding — หักล้างรายงานเดิม) ·
**เบี้ยเลี้ยง**เลิกถูกฉาย × งวดที่เหลือ · **ประวัติผู้ขาย**เลิกเสนออัตรา WHT จากใบเดียว (`MinHistoryDocuments = 3`) ·
`ContactType.Unknown = 0` + `Helpers/ContactTypeResolver` (สองเรนเดอเรอร์เคยพิมพ์ "สำนักงานใหญ่" ต่อท้ายเลขบัตร
ประชาชน = ข้อความเท็จบนเอกสารภาษี) · ถอด AI ออกจากสรุปจุดสั่งซื้อ (kill-switch ไม่ผ่าน · `Helpers/ReorderNarrative`
เขียนเองได้ทุกครั้ง) · migration 5 ชุด — commit 555cf16)_

_Last verified against codebase: 2026-09-19 (รอบ 184b — **ซ่อม build ที่ CI จับได้ 2 รอบ** (ไม่มี .NET SDK ในเครื่องพัฒนา
⇒ CI คือคอมไพเลอร์ตัวแรก): (1) `OcrDtos.cs` วาง `using Accounting.Helpers;` **ใต้** `namespace Accounting.Models.DTOs.Ocr;`
⇒ ใช้กติกา "ชั้นใกล้ชนะ" และเรพมี namespace `Accounting.Models.DTOs.Accounting` อยู่จริง ⇒ CS0234 ·
`tools/namespace_shadow_check.py` มีไว้กัน defect class นี้โดยตรงแต่ข้ามบรรทัด `using` ทุกบรรทัด ⇒ **ปิดรูของด่านเอง**
(ตอนนี้ `using` เหนือ namespace ยังข้าม · ใต้ namespace เดินกติกาเดียวกับชื่อที่มีจุดนำหน้า + `resolves_ns()` ·
negative test 2 ทิศรันแล้ว) (2) `BankService` เติม `u.CompanyId == companyId` ลงคิวรี `_db.Users` แต่ **`User` ไม่มีช่องนั้น**
— ความเป็นสมาชิกบริษัทอยู่ที่ `CompanyUsers` (ตารางเดียวกับ `TenantGuard`) ⇒ ยุบเป็น `CompanyMemberNamesAsync` ตัวเดียว ·
เจตนาเดิมถูก: คิวรีทั้งสองเคยค้นชื่อจาก `_db.Users` **โดยไม่มีเงื่อนไขบริษัทเลย** ⇒ ชื่อผู้ใช้บริษัทอื่นโผล่บนหน้ากระทบยอด
· **CI เขียวทั้ง static-checks และ dotnet build (Release) ที่ `9167816`** ⇒ ไฟล์เทสต์ใหม่ 44 ไฟล์ของรอบ 183/184
คอมไพล์ผ่านเป็นครั้งแรก · `dotnet test` **ยังไม่เคยรัน** (job รันเฉพาะ PR/main/dispatch · `workflow_dispatch` = 403)
— commit 9167816)_

_Last verified against codebase: 2026-09-21 (รอบ 185 — **ผังบัญชีบนใบลดหนี้/ใบเพิ่มหนี้ฝั่งซื้อ** · ผู้ใช้รายงาน:
"หน้าลดหนี้ ผังบัญชีต้องเลือกฝั่งค่าใช้จ่ายได้ด้วย เพราะลดหนี้ฝั่งซื้อ ถ้าลงบัญชีเป็นวัสดุสิ้นเปลือง ก็ต้องไปลดวัสดุสิ้นเปลือง"
· ทีมตรวจ 3 ชุด (บัญชี/ภาษี · UX/UI · ฝ่ายค้าน-ทางเข้าอื่น) ถกเถียงแล้ว main agent เปิดไฟล์ยืนยันทุกข้อหลัก:
**ราก 4 ชั้น** (1) `onCnSourceSelect` สร้างแถว**ก่อน**ตั้งฝั่ง ⇒ ใบลดหนี้ที่อ้างใบ**ซื้อ** ได้แถวที่เสนอแต่ผังรายได้
4xxxx ทุกแถว แล้ว `_refreshCnSideEditability()` ล็อก radio ทันที ⇒ ผู้ใช้แก้เองไม่ได้เลย (เส้นที่ §86/10 อยากให้ใช้
ที่สุด ผิด 100%) (2) ลูปเติมบรรทัดจากใบต้นทางคัดลอกแค่ 6 ช่อง **ทิ้ง `accountCode`/`productCode`/`isVatClaimable`/
ส่วนลด** ทั้งที่ `DocumentLineResponse` ส่งมาครบ ⇒ ทิ้ง productCode = JE ตกไป 51110 แทนบัญชีคุมสต็อก · ทิ้ง
isVatClaimable = Cr 11610 ทั้งที่ตอนตั้งหนี้ไม่เคย Dr (ภาษีซื้อติดลบ) (3) `EnsureLineAccountMatchesDocSide`
**ยกเว้น CN/DN ทั้งด่าน** ⇒ ฝั่งขายมีตาข่าย `RevenueLegAccountId` แต่ฝั่งซื้อไม่มีเลย ⇒ `43060 ส่วนลดรับ` (หมวด
Revenue) **ถูก Cr เข้า 4xxxx เงียบสนิท** แทนที่จะลดวัสดุสิ้นเปลือง (4) ด่านบทบาทคู่ค้าที่ `CreateDocumentAsync`
อ่านแต่ `CnDnPurchaseSideOverride` ⇒ การแปลง **ใบแจ้งหนี้ซื้อ → ใบลดหนี้** ซึ่งเป็นเส้น**เดียว**ที่คัดลอกผังบัญชี
รายบรรทัดมาให้ครบ (`ConvertCoreAsync` `s.Line.AccountId`) ถูกปฏิเสธทุกครั้งด้วยข้อความ "ไม่ได้ตั้งค่าเป็นลูกค้า"
⇒ ผู้ใช้ถูกบีบไปคีย์มือแล้วไปเจอผังที่ค้างฝั่งขาย · **แก้**: `Helpers/AdjustmentNoteAccount` เป็น OWNER file
(`ResolveSide` ใบต้นทางชนะ override · `SideViolation` ฝั่งซื้อห้ามหมวดรายได้/ฝั่งขายห้ามหมวดค่าใช้จ่าย/สินทรัพย์-หนี้สิน
ผ่านทั้งคู่/**ฝั่ง null = ปล่อยผ่าน** กันการล้มใบเก่าและ API ที่ไม่เคยส่งฝั่ง · `StockValuationWarning` เป็น soft
warning ตอนอนุมัติ ไม่บล็อก) · ฟอร์มตั้งฝั่งก่อนสร้างแถว + `_hydrateLineRow` ตัวเดียวกับตอนแก้ไขเอกสาร +
`_syncLineAccountPickers()` (datalist/ป้าย/placeholder/ชิป) เรียกจาก `onCnSideChange` ด้วย + ชิป "⚠ ผังคนละฝั่ง" ·
ของแถม: counter account ของ CN/DN เคารพ `DefaultApAccountId`/`DefaultArAccountId` ของคู่ค้าแล้ว (เดิมไม่ส่ง
`doc.Contact` ต่างจากทุกเส้นอื่น) · `OcrService.FallbackLineDescription` เลิกเขียนชื่อ enum ดิบ ("TaxInvoice")
ลงช่องรายละเอียดบรรทัด ซึ่งถูกพิมพ์ลงกระดาษ §86/4 และลงคำบรรยาย JE ("ใบลดหนี้ - TaxInvoice") — commit e48ea3c)_

_Last verified against codebase: 2026-09-21 (รอบ 185b — **"ดำเนินการพัฒนาสิ่งที่ควรจะเป็น" (คำสั่งเจ้าของ)** — ลงมือ 4 ข้อ
ที่รอบ 185 ยกขึ้นมาเป็นเรื่องรอชี้ขาด โดยยึดหลัก "เพิ่มได้ · ย้ายยอดไม่ได้":
(1) **ผังที่ขาด** — `51150/51160` (contra-purchase) ย้ายจากเทมเพลตซื้อมาขายไปเข้า**ผังกลาง** ⇒ ทุกประเภทธุรกิจมีผังให้
ใบลดหนี้ฝั่งซื้อลง · `43060` เปลี่ยน**ชื่อ**เป็น "ส่วนลดรับ (ส่วนลดเงินสด)" ให้ชัดว่าไม่ใช่ส่วนลดการค้า · **`AccountType`
ไม่เปลี่ยน** (ย้ายหมวด = re-sign งบที่ปิด/ยื่นไปแล้ว) (2) **`11520 วัสดุสิ้นเปลืองคงเหลือ`** — `DefaultAccountPrefix(Supplies)`
เดิมคืน `"118"` แต่ผังมาตรฐานไทย `118 = เงินมัดจำจ่ายล่วงหน้า` และไม่มีผังวัสดุสิ้นเปลืองหมวดสินทรัพย์เลย ⇒ ค้นเจอ
**11810 เงินมัดจำ** ⇒ ซื้อวัสดุสิ้นเปลืองที่ตัดสต็อก **Dr เข้าบัญชีเงินมัดจำ** และยอดวัสดุคงเหลือกองอยู่ในงบดุลผิดที่
(`tools/gl_code_check.py` จับไม่ได้เพราะจับเฉพาะ string literal ไม่ใช่ค่าจาก method call) · ทั้งขาซื้อและขาเบิกใช้ถาม
`InventoryControlAccount` ตัวเดียวกัน จึงยังหักล้างกันได้ไม่ว่าจะตกทางไหน (3) **ภ.พ.30 ช่อง 7/8** — ฝั่งของ CN/DN เคย
ตัดสินด้วย `DocumentSide.IsPurchase(type)` ที่**ไม่ส่ง `ourRole`** ⇒ คืน "ซื้อ" เสมอ ⇒ ใบลดหนี้ฝั่งขายถูกนับเข้าช่อง
ฝั่งซื้อ · และ **ใบลดหนี้เคยถูกบวกเข้าช่องแทนที่จะลบ** ⇒ ช่องสูงกว่าความจริงสองเท่าของยอดที่ลด · ยุบเป็น
`AdjustmentNoteAccount.ResolvePostedSide` ตัวเดียวกับรายงานภาษีซื้อ/ขาย (สำเนาที่สองใน `TaxService` ถูกถอด) ·
ฝั่งที่ยัง**ไม่รู้ ไม่ถูกนับเข้าช่องใดเลย** (G3) (4) **แถบเตือนใบเก่า** — `openEdit` ขึ้นแถบแดงเมื่อใบที่เปิดมามีบรรทัด
ผังผิดฝั่ง · **ไม่แก้ให้เอง ไม่ backfill** เพราะใบที่ post แล้วต้องผ่าน reclassify ที่ลง JE ปรับปรุงในงวดเดิม —
การแก้คอลัมน์เฉย ๆ จะทำให้เอกสารกับ GL แยกทางถาวร · migration เป็น INSERT + rename เท่านั้น **ไม่มี UPDATE ยอดใด ๆ**
— commit 4bfe9bf)_

_Last verified against codebase: 2026-09-21 (รอบ 186 — **ใบซื้อบริการต่างประเทศที่ "ลืมติ๊ก ภ.พ.36" แก้ย้อนหลังได้แล้ว**
· ผู้ใช้ส่งภาพใบสำคัญจ่ายค่าคอมมิชชั่น Booking.com B.V. สองใบของคู่ค้าเดียวกัน: ใบ ก.ค. ติ๊กแล้ว (Dr 52150 + Dr 11640
· Cr 21912 VAT · Cr ธนาคาร **ฐาน**) ใบ ส.ค. ไม่ติ๊ก (**ไม่มี Cr 21912** · Cr ผู้รับเงิน **ฐาน+VAT**) · ผู้ขายต่างประเทศ
ไม่เก็บ VAT ไทย เราประเมิน 7% เองแล้วนำส่งแทน ⇒ ใบที่ลืมติ๊กพัง 3 ทางและ**เงียบทั้งสามทาง**: ไม่มีหนี้ ภ.พ.36 ⇒
ไม่โผล่หน้านำส่ง ⇒ ไม่เคยนำส่ง (ม.27) · เครดิตผู้รับเงินเกินเท่ากับ VAT ⇒ กระทบยอดธนาคารไม่ลง · ภาษีซื้อค้าง 11640
**โดยไม่มีทางเคลม** (เส้นปลดล็อก ภ.พ.36 อ่าน `IsForeignService` · เส้น §86/4 ปิดเพราะผู้ขายไม่มีเลขภาษีไทย) จนงาน
§82/3 โยนเป็นค่าใช้จ่ายเมื่อครบ 6 เดือน · **วันนี้ระบบไม่มีทางแก้เลย** — `UpdateDocumentAsync` แก้ได้เฉพาะก่อนอนุมัติ
⇒ เพิ่ม `ReclassifyForeignServiceAsync` (+ endpoint + ปุ่มบนหน้าเอกสาร) ที่กลับ JE เดิมแล้วลงใหม่ผ่านตัวลงบัญชีตัวเดิม
ด้วย gate ชุดเดียวกับย้ายฝั่ง CN/DN · และ `Helpers/ForeignServiceEvidence` เตือนตั้งแต่ก่อนอนุมัติ (คำเตือน ไม่บล็อก —
สาขาบริษัทต่างชาติที่จด VAT ไทยออกใบกำกับไทยได้ ไม่ใช่ §83/6) — commit abc5f92 · ซ่อม build (ลืมประกาศใน `IDocumentService`) 3fedb41)_
_Last verified against codebase: 2026-09-21 (รอบ 187 — **"ไม่มีบอกว่า Require อันไหน หรือ ขาดอะไร อันไหน"** (คำร้องผู้ใช้)
· กด "💾 บันทึกการตั้งค่า" ที่หน้าตั้งค่าที่พักแล้วได้ toast แดง `One or more validation errors occurred. — Code: The Code
field is required.` — **อังกฤษล้วน + ชื่อ property C# ที่ไม่ตรงกับป้ายใด ๆ บนจอ** (ป้ายจริงคือ "รหัส (ใช้ในเลขจอง RES-XXXX-…)")
⇒ ผู้ใช้หาไม่เจอว่าต้องแก้ช่องไหน · ต้นเหตุสองชั้นที่ขัดกันเอง: (ก) โปรเจกต์เปิด `<Nullable>enable</Nullable>` ⇒ ASP.NET ใส่
`[Required]` **โดยปริยาย** ให้ property reference ที่ไม่มี `?` ทุกตัว (71 ตัวใน `Models/DTOs/`) แล้วตอบด้วยข้อความมาตรฐาน
ของ framework **ก่อนถึง service** (ข) `LodgingService` เขียนไว้ชัดว่ารหัสเว้นว่างได้ — `if (IsNullOrWhiteSpace(p.Code))
p.Code = DeriveCode(p.Name);` และ `Apply` ก็เขียน `d.Code?.Trim() ?? ""` ไว้แล้ว ⇒ ฟอร์มไม่มีดอกจัน service รองรับค่าว่าง
แต่ DTO ประกาศว่าบังคับ · ผลข้างเคียงที่เจ็บกว่า: `BusinessRuleException` ไทยที่เขียนไว้ดี ("กรุณาระบุหมายเลขห้อง")
**ไม่เคยถูกเรียก**ในเคส null ("มี ≠ ถูกเรียก" F2 ข้อ 2)

แก้ 4 ชั้น — **ไม่ปิด implicit required ทั้งระบบ** (ปิดแล้ว null จะไหลเข้า `d.Name.Trim()` ⇒ NRE 500 ซึ่งเงียบกว่าและ
แย่กว่า — G5 "ทิศปลอดภัย = ทิศที่ความเสียหายมองเห็นและแก้ทัน") แต่ทำให้ด่านนั้น**พูดไทยและชี้ช่องได้**:
1. `Helpers/ValidationErrorText` (OWNER ตัวเดียว · pure · 10 เทสต์) — แปล ModelState เป็นไทย · ยุบคีย์ซ้ำ ·
   ทำคีย์ทุกทรง (`Code` · `$.code` · `$.lines[0].unitPrice`) ให้ตรงกับ `name="..."` บนฟอร์มด้วยอัลกอริทึมเดียวกับ
   `JsonNamingPolicy.CamelCase` ที่ `Program.cs` ตั้งไว้ · **ห้ามแต่งป้ายไทยเอง** (เซิร์ฟเวอร์ไม่รู้ป้าย — F2 ข้อ 5)
   · ข้อความอังกฤษที่แปลไม่ได้ **ส่งต่อตามเดิม** พร้อมป้าย "ระบบแจ้งว่า:" ไม่กลืนแล้วแต่งใหม่
2. `Program.cs` `InvalidModelStateResponseFactory` → ซองเดียวกับ `ExceptionMiddleware` (`ApiResponse<ValidationErrorData>`)
   ⇒ ฝั่ง JS อ่านทางเดียวเสมอ · `data.fields` = ชื่อช่อง camelCase
3. `wwwroot/js/api.js` `describeFieldErrors` — เอาชื่อช่องไปหา `[name=...]` **บน DOM ของหน้านั้นเอง** แล้วอ่านป้ายไทยจริง
   + ชื่อส่วนจาก `.card > h4` · ไฮไลต์ `.has-error` (คลาสที่มีใน `style.css` มาตลอดแต่**ไม่มีใครเรียก**) + `aria-invalid`
   + เลื่อนจอ/โฟกัส · ล้างไฮไลต์รอบก่อนทุกครั้ง (ไม่งั้นช่องที่แก้แล้วแดงค้าง ผู้ใช้ไล่ผิดช่อง) ·
   ช่องที่หาไม่เจอบนหน้าต้องบอกตรง ๆ ว่า **"ไม่มีช่องนี้บนหน้านี้ — ฟอร์มส่งค่ามาเอง"** (G3 ไม่รู้ต้องบอกว่าไม่รู้
   ห้ามสั่งให้ผู้ใช้ไปกรอกของที่มองไม่เห็น) · แท็บที่ซ่อนอยู่เปิดผ่าน hook `Page.revealField` ของหน้านั้น —
   `api.js` **ห้ามปลด `.hidden` เอง** (panel ที่ปลดมั่วจะค้างทับแท็บอื่น — ต้นเรื่องของ `tools/tab_hidelist_check.py`)
4. `LodgingDtos` — `Code` ×3 (ที่พัก/ประเภทห้อง/แผนราคา) → `string?` ให้ตรงกับที่ service ทำอยู่จริง ·
   `LodgingUnitDto.Number` → `string?` เพื่อให้ด่านไทย "กรุณาระบุหมายเลขห้อง" ได้ทำงานจริง · ฟอร์มเพิ่ม hint
   "ปล่อยว่างได้ — ระบบตั้งให้จากชื่อที่พัก"

กันกลับมาเกิด: `tools/dto_nullable_contract_check.py` (self-test 5 ทิศ) ฟ้องเฉพาะตอนสองชั้น**ขัดกัน** — ชั้นที่ `throw`/
`return BadRequest` เองถือว่าตรงกัน ไม่ฟ้อง ⇒ เขียวที่ 0 จุดโดยไม่ต้องมี baseline · ใส่บั๊กกลับ (Code เป็น non-nullable)
แล้วจับได้ 3 จุดทันที · `tools/validation_field_label_sim.js` ล็อกข้อความที่ผู้ใช้เห็นด้วย **โค้ดจริงจาก api.js** 4 ทิศ ·
และพบว่า CLAUDE.md §F เขียนตั้งแต่รอบ 169 ว่า `check_all.sh` รัน `node tools/vat_line_source_sim.js` ด้วย แต่
**ไม่เคยรัน** — เพิ่มหมวด 1b ที่กวาด `tools/*_sim.js` ทั้งหมด (ด่านที่ไม่มีใครเรียก = ไม่มีด่าน) — commit 81968d5)_
_Last verified against codebase: 2026-09-21 (รอบ 188 — **ช่องตัวเลขที่เว้นว่าง ทำให้บันทึกไม่ได้ทั้งใบ** ·
ผู้ใช้ส่งภาพตอนสร้างประเภทห้อง "Nordic Tent": เว้นช่อง "เตียงเสริมสูงสุด" แล้วได้
`dto: The dto field is required.; $.maxExtraBeds: The JSON value could not be converted to System.Int32.`
⇒ หน้าเว็บส่ง `maxExtraBeds: null` · `LodgingRoomTypeDto.MaxExtraBeds` เป็น `int` (ไม่ใช่ `int?`) ⇒
System.Text.Json แปลงไม่ได้ ⇒ **โยน body ทิ้งทั้งก้อน** ⇒ พารามิเตอร์ของ action เป็น null ⇒ MVC เติม error
ตัวที่สอง "dto is required" ซึ่ง**ไม่มีช่องชื่อ dto บนหน้าจอให้แก้** · และ**ไม่มีอะไรถูกบันทึกเลย**ทั้งที่ช่องนั้นไม่บังคับ

ต้นเหตุเชิงโครงสร้าง: `readForm` (ใช้ร่วมกัน **6 modal** — ประเภทห้อง/ห้อง/แผนราคา/ฤดูกาล/นโยบาย/บริการเสริม)
แปลง "ช่องตัวเลขว่าง" เป็น `null` ทุกช่องโดยไม่รู้ว่า DTO ปลายทาง nullable หรือไม่ · ส่วน `readProp` ของฟอร์มที่พัก
กันไว้ด้วย **ลิสต์ชื่อฟิลด์ฮาร์ดโค้ด 2 ชุด** (12 ชื่อ "ว่าง = ตัดทิ้ง" + 8 ชื่อ "ว่าง = 0") ซึ่งคือ**สำเนาที่สองของ
กติกาใน DTO** ที่ไม่มีใครอัปเดตตอนเพิ่มฟิลด์ (F2 ข้อ 4) — และ modal ทั้ง 6 ไม่มีลิสต์นั้นเลย

แก้:
1. `_readNumberInto` ตัวเดียวที่ทั้ง `readProp` และ `readForm` เรียก — ว่าง = **ตัดคีย์ทิ้ง** (ปล่อยให้ค่า default
   ที่ DTO ประกาศไว้ทำงาน: `MaxExtraBeds = 0` · `StandardOccupancy = 2`) · ช่องที่ "ว่าง = ศูนย์" ประกาศที่
   **ตัวช่องเอง**ด้วย `data-blank="0"` (8 ช่องเงิน/%) ⇒ ลิสต์ฮาร์ดโค้ดทั้ง 2 ชุดถูกถอด พฤติกรรมเดิมไม่เปลี่ยน ·
   ต้อง `delete` ไม่ใช่แค่ไม่เซ็ต เพราะ `readProp` seed payload มาจากใบเดิม
2. `Helpers/ValidationErrorText` — เมื่อมี error ระดับ JSON path (`$.x`) ให้**ตัด "required" ระดับพารามิเตอร์
   ที่เป็นผลพวงทิ้ง** (ปลอดภัยเพราะ deserialize ล้ม ⇒ MVC ข้าม validation ราย property ทั้งหมด) ⇒ ผู้ใช้เห็น
   เฉพาะช่องที่ผิดจริง ไม่เห็น "dto" ที่หาไม่เจอบนหน้าจอ · ข้อความชนิดผิดพูดถึง "ช่องตัวเลขที่เว้นว่าง" ด้วย

กวาดทั้งเรพ: จุดที่ส่ง null ให้ค่าตัวเลขมี 6 จุด — **2 จุดเป็นบั๊ก** (ตัวอ่านฟอร์มแบบวนลูปที่ไม่รู้ชนิดปลายทาง
= ที่แก้รอบนี้) · อีก 4 จุดเปิดไฟล์ยืนยันแล้ว**ไม่ใช่บั๊ก** เพราะปลายทาง nullable ทุกตัว
(`LodgingRateOverrideBulkRequest.StopSell` เป็น `bool?` · `LodgingAddChargeRequest.VatRate` เป็น `decimal?` ·
`payroll fixedAmount` เป็น `decimal?` · `admin/plans.html` ยิงเข้า Update DTO ที่เป็น `int?`/`decimal?` ทั้งชุด)
— ด้วยเหตุนี้ checker จึงจับ**เฉพาะตัวอ่านฟอร์มแบบวนลูป** ไม่กวาด `parseInt(...)` รายช่อง (จะฟ้องผิด 15 จุด
ใน `admin/plans.html` ที่ไม่มีบั๊ก — checker ที่ฟ้องผิด = checker ที่พัง)

กันกลับมาเกิด: `tools/blank_number_null_check.py` (self-test 3 ทิศ · ถอดการแก้ออกแล้วจับได้ 2 จุดทันที) ·
`tools/blank_number_form_sim.js` ล็อก 4 ทิศด้วย **โค้ดจริงที่ดึงออกมาจากหน้าเว็บ** (ว่าง = ตัดคีย์ · data-blank=0 =
ศูนย์ · **0 ที่ผู้ใช้พิมพ์เองต้องไม่หาย** · หน้าไม่เหลือรูปแบบเดิม) — commit ca299b7)_

_Last verified against codebase: 2026-09-24 (รอบ 190 — **11 ข้อจากเจ้าของ · 5 ทีม (U · L · P · C · M) + main agent** ·
หลักการที่เจ้าของสั่ง: "ใช้วิธีทดลองและพัฒนาวิธีคิดให้ระบบดีขึ้นเรื่อย ๆ อย่ารื้อทำใหม่ทั้งหมด" ⇒ ทุกทีม**เพิ่มขั้น**เป็น pure helper
ใน `Helpers/Ocr*.cs` ต่อสายจุดเดียว + เทสต์สองครึ่งด้วยข้อความจากกระดาษจริง 2 ใบ (`erp-review/2026-09-24/BRIEF.md`) · รายงานทีม
`erp-review/2026-09-24/team-*.md`

- **ข้อ 10 โควตา OCR ไม่ reset** (main · e8631de) — จุดขึ้นเดือนใหม่ 4 จุดล้างตัวนับคนละชุดแล้วแย่งกันเลื่อนวันรีเซ็ต ⇒ ยุบเป็น
  `Helpers/SubscriptionUsageRollover` · เส้นอ่านนับตัวนับค้างเดือนก่อนเป็น 0 · migration `LEAST(ตัวนับ, สแกนเดือนนี้)` คืนส่วนที่กินเกิน
- **ข้อ 1/4/7 UX** (ทีม U · 3c2f2c3) — คิวรอตรวจได้เมนูข้าง + เหตุผลไทย + สรุปเหตุคิวว่าง + ตัดสแกนที่บันทึกเป็น JE ออก ·
  ตัวแสดง "กำลังทำงาน" กลางใน `api.js` (`ApiBusy` — ทุกหน้าที่ใช้ `API.*` ได้เอง · กันกดซ้ำ) · แนบไฟล์บนใบที่อนุมัติแล้ว: ราก =
  `innerHTML +=` ลบ listener ของกล่องแนบไฟล์ · ด่านสิทธิ์สร้าง/อนุมัติชนิดเอกสาร + ตัดสินชนิดไฟล์จากไบต์
- **ข้อ 11 local OCR / วันที่** (ทีม L · 514d83f) — `OcrDateReader.CrossCheck` ทุก engine · `OcrBuyerAddressReader` + invariant ล้าง
  ที่อยู่เราออกจากช่องผู้ขาย · คลัง known-good เติมที่อยู่ผู้ขายที่ว่างได้ (`OcrKnownGoodAddressFill`) · ตารางสถานะ "Azure สอน local" รายช่อง
- **ข้อ 2/5 (OCR) ชื่อ/ที่อยู่/สาขา/เล่มที่** (ทีม P · 5753b2b) — ที่อยู่ขาด `12` (ตัวตัดเศษชื่อตัดเลขบ้าน + migration known-good) ·
  `FillOurName` ชื่อผู้ซื้อ = บริษัทเรา · `StripBranchSuffix` · `OcrIssuerBranch` (สาขาที่ 8 + ที่อยู่สาขาไทย) · `OcrBookSerial` (`066/3267`)
- **ข้อ 3/5/6 ผู้ติดต่อ/สาขา** (ทีม C · a49e030) — ด่าน §86/4 ของใบกำกับซื้อบนฟอร์มให้เซิร์ฟเวอร์ตัดสินด้วย `TaxInvoiceCompletenessChecker`
  ตัวเดียวกับตัวลงบัญชี (endpoint `supplier-tax-invoice-check`) · `InputVatParkingNotice` เตือนก่อนอนุมัติเมื่อจะพัก 11640 ·
  `OcrVendorBranchContact` · `RdVatBranchRecords` + `GetBranchAsync` · `ContactResponse.BranchLabel` บนตัวเลือกผู้ติดต่อ
- **ข้อ 8** แก้แล้วตั้งแต่ ca299b7/fb42474 (รอบ 188) — ผู้ใช้ต้อง pull + rebuild
- **ข้อ 9 ส่วนลดท้ายบิล / VAT ผสม** (ทีม M · 2463e76) — ตัวอ่านส่วนลดหยิบ "ส่วนลด" ตัวแรกของหน้า (ได้ยอดหลังลดเป็นส่วนลด ⇒
  ใบร้านวัสดุได้ 1,487.77 แทน 1,418.02 ผ่านเงียบ) · ด่านคณิตไม่รู้ส่วนลด · `SubTotal` เป็นยอดก่อนลด · อัตรา VAT รายบรรทัดเดาจากชื่อ
  แล้วเฉลี่ย VAT หัวใบ ⇒ Σ VAT ตรงเสมอ "โดยการสร้าง" ⇒ แก้ด้วย `OcrBillDiscount` · `NetSubTotal` · เคส E · `OcrLineVatMarks` ·
  `OcrAmountIntegrity` (`[Σ-GAP]`) · LINE postback ตรวจซ้ำ · แบนเนอร์แดงบนหน้ารีวิว
- **ฝ่ายค้าน (F3 ข้อ 11) หลัง merge ทั้ง 5 ทีม — แก้ในคอมมิตรวม**: (1) วันที่ที่ระบบเดา/ทับ < 0.85 ⇒ `[DATE-UNSURE]` บล็อกการอนุมัติ
  อัตโนมัติ (`OcrDateReader.NeedsHumanConfirm`) (2) คำเตือนพักภาษีซื้อทำให้ OCR/LINE อนุมัติไม่ผ่านโดยบอกแค่ "(1 รายการ)" ⇒
  `DescribeForUser` ส่งตัวคำเตือนไปถึงผู้ใช้ (3) ลบไฟล์แนบของใบที่ออกไปแล้วลบไฟล์จริง ⇒ `AttachmentRetention` เก็บไฟล์ไว้ (ม.10/§87/3)
  (4) ชื่อผู้ซื้อที่เติมจากทะเบียนเมื่อกระดาษมีแต่รหัส ได้ 0.80 (ไฮไลต์ + เหตุผล §86/4) ไม่ใช่ 0.95 · ที่ยังไม่แก้ (backlog):
  PurchaseInvoice ยังพัก 11640 เงียบได้ (ด่านบนฟอร์ม/คำเตือนผูกกับติ๊กของ PV) · ช่วงเปลี่ยนผ่าน `066/3267` เทียบใบซ้ำกับใบเก่า "3267"
  ไม่เจอ · แถวผู้ติดต่อที่ไม่มีรหัสสาขาถูกตีความสองแบบใน `OcrVendorBranchContact`
- เครื่องมือ: `tools/dead_helper_check.py` ตัดคอมเมนต์แบบสแกนครั้งเดียว — เดิม `/*` ในคอมเมนต์ `//` เปิดบล็อกกลืนโค้ดจริง ⇒ ฟ้องผิด
  (ทีม M เจอ) · checker ที่ฟ้องผิด = checker ที่พัง (F2 ข้อ 6)
- คำถามเจ้าของที่ค้าง: U 1–5 · L Q1–Q4 · P Q-P1..P5 · C 1–5 · M 1–5 (อยู่ท้ายรายงานแต่ละทีม — ห้ามเดาแทน)
— commit deda7c9)_

_Last verified against codebase: 2026-09-24 (รอบ 191 — **"ทำไมเอกสารกลับมาเป็นใบเสร็จรับเงินอย่างเดียวอีกแล้ว ทั้งที่ควรขึ้นเป็น
ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ"** (ผู้ใช้ส่งใบเสร็จค่าห้องพัก 3 ใบ: TIV-20260912-0005 = อย่างย่อ · REC-20260919-0006 = ใบเสร็จเปล่า)
ไล่แล้ว: ด่าน ภ.พ.06 ของรอบ 182–183 (`AbbreviatedTaxInvoiceRule` · D1-B4 · 3b81ca1/30c2e02 ลง 19 ก.ย.) ลดหัว "อย่างย่อ" เป็น
"ใบเสร็จรับเงิน" เมื่อบริษัทยังไม่ได้ติ๊ก+กรอกวันที่อนุมัติ ภ.พ.06 ในหน้าตั้งค่าบริษัท — **เงียบ** และเพราะหัวไม่มีคำว่า "ใบกำกับภาษี"
เลขจึงไปชุด REC แทน TIV (`TaxInvoiceSeriesPolicy`) · ใบ 12 ก.ย. ออกก่อนด่านนี้จึงยังเป็นอย่างย่อ · คำอธิบายช่อง ภ.พ.06 ในหน้าตั้งค่าบอก
ว่ากระทบแค่ "สลิป POS" (ผิด — กระทบทุกเอกสารขายที่มี VAT และผู้ซื้อไม่มีข้อมูลครบ)
- แก้: `PdfGenerationService.AbbreviatedDowngradeNotice` → `DocumentResponse.TaxInvoiceTitleNotice` → การ์ดแดงในหน้ารายละเอียดเอกสาร
  (บอกเหตุ + ทางไปต่อ: กรอก ภ.พ.06 · หรือออกใบกำกับเต็มรูป) · แก้คำอธิบายช่อง ภ.พ.06 · ด่านเองไม่แตะ (ถูกกฎหมาย §86/6)
- จงใจไม่ทำเป็นคำเตือนก่อนอนุมัติ: `DocumentsV1Controller` อนุมัติโดยไม่ยืนยันคำเตือน ⇒ ระบบภายนอกที่ส่งใบเสร็จมาจะล้มทั้งเส้น
- ใบที่ออกเลข REC ไปแล้วเปลี่ยนเลขย้อนหลังไม่ได้ (§86/4) — ออกใบกำกับเต็มรูปแทนได้ด้วยปุ่มเดิมเมื่อลูกค้าขอ
— commit 947f0ac)_

_Last verified against codebase: 2026-09-24 (รอบ 192 — **"หายอด total ให้ถูกก่อน แล้วค่อยให้ค่าอื่นรวมกันให้ตรงยอดนี้"** (เจ้าของ ·
ใบจริง 3 ใบ: Makro ผสมยกเว้น/มี VAT + ส่วนลดรวม VAT · Shopee ส่วนลดหลังยอดใบกำกับ · Lazada ส่วนลดก่อน VAT) · ทีมวิเคราะห์ 3 ด้าน
(`erp-review/2026-09-24/total-A/B/C.md`) → ทีมลงมือ (`total-T.md`) → ฝ่ายค้าน 1 รอบ (5 CONFIRMED + 5 PLAUSIBLE แก้ครบ)
- ต้นเหตุ: ไม่มีขั้น "เลือก" ยอดรวม — Azure `InvoiceTotal ?? AmountDue` · ข้อความ = regex แรกที่แมตช์ ("TOTAL 24,110" ของ Makro =
  ยอดก่อนส่วนลด) · ตัวอักษรไม่ตรงแค่ลดความมั่นใจ · back-calc 7/107 แต่ง VAT 1,577.29 ทับ 1,148.28 ที่พิมพ์ · มี 13 ผู้บริโภคยอดรวม
  ที่เชื่อค่านี้ทั้งหมด ⇒ ยอดผิดตัวเดียวทุกด่านเห็นว่า "ลงตัว"
- ของใหม่ (เพิ่ม ไม่รื้อ): `OcrTotalAnchor` (ยึดยอดด้วยชั้นหลักฐานอิสระ · เขียนทับเฉพาะพิสูจน์ได้ · `Plan` เป็นตัวตัดสินการเขียน) ·
  `OcrTotalDecomposer` (สมมติฐานส่วนลดก่อน/รวม/หลัง VAT · `ForScan` ใช้ร่วมสร้าง/พรีวิว/repopulate) · `OcrPaperAmounts` · `OcrPageSet` ·
  `OcrLineVatMarks.ReadGroups` · `VatBackCalcGuard.PrintedVatContradicts` · แท็ก `[TOTAL-CONFLICT]`/`[PAY≠TOTAL]` (บล็อก) ·
  `[TOTAL]`/`[TOTAL-UNSURE]`/`[PAGES-PARTIAL]` (ข้อสังเกต)
- ผลใบจริง: Makro 23,812.25 (ยกเว้น 6,260.00 · ฐาน 16,403.97 · VAT 1,148.28) · Shopee 536.00 / VAT 35.07 + `[PAY≠TOTAL]` (จ่าย 438) ·
  Lazada 5,024.00 (ส่วนลด 216.82 ก่อน VAT · บรรทัด 4,912.15 ตามที่พิมพ์) · ใบเดิม 21 สถานการณ์ผลเท่าเดิม (replay golden)
- คำถามเจ้าของ: วิธีลงส่วนต่าง 98 ของใบ Shopee · `[PAGES-PARTIAL]` ควรบล็อกไหม · บรรทัดสรุปต่อกลุ่มภาษี · เก็บยอดที่จ่ายเป็นคอลัมน์ไหม
- สแกนเก่าที่เก็บยอด 24,110 ไว้ไม่มี migration — ตอนสร้างเอกสาร `[Σ-GAP]` ฟ้อง · สแกนใหม่ได้ผลถูก
— commit 85bfb98)_

_Last verified against codebase: 2026-09-24 (รอบ 193 — **คำตัดสินเจ้าของ 37 ข้อ (`erp-review/2026-09-24/DECISIONS.md`) + ผลตรวจ "การตั้งค่าถูกเรียกใช้
ครบทุกส่วนงานไหม" (`audit-settings.md` · P0 1 · P1 7 · P2 12) + ผลตรวจวงจรเงินมัดจำ (`audit-deposit.md` · P0 3)** · 13 ทีม (E S F U2 P2 O2 → L2 O1 V W
S2 C3 M2) · ฝ่ายค้าน 3 รอบ (`review193-*.md` · `review193-r2-*.md` · `review193-r3.md`) · รายงานต่อทีม `erp-review/2026-09-24/r193-*.md`
- **E (สต็อก/POS · 725fe03 · a967b9e)** — E-01 P0: POS ขายเมนูสูตรลง COGS 0 และคืนเงินกลับ COGS เต็ม ⇒ ตัดสต็อกก่อน JE (`DeductSaleStockAsync`) + ต้นทุนตรึงบรรทัด
  (`PosCogsBooking` · `PosOrderItem.CostOfGoodsSold`) · คืนปัดสะสม Σ = ยอดขาย · E193-1 ปุ่มสถานะปิดบิลเองไม่ได้ (`PosOrderStatusTransition`) · E193-2 void บิลที่คืน
  บางส่วน = กลับเฉพาะส่วนค้าง (`PosVoidPlan` · กลับ JE ขาย + JE คืนเงินทุกใบ) · E193-3 กลับ JE ในธุรกรรมเดียว ล้ม = ยกเลิกไม่สำเร็จ · E-02 `CostingMethod` ไม่มีผู้เขียน = คำถามเจ้าของ
- **S (security · d334f1d)** — #37/G2-01 คีย์ `int_` ออก/ผูกผู้ใช้เฉพาะเจ้าของ · สิทธิ์ `CanRead/CanWrite/CanDelete` (ใหม่ = อ่านอย่างเดียว) · legacy 90 วัน ·
  `X-Acting-User` คีย์ใหม่ = mapping เท่านั้น · B-03 precheck กรองบริษัท (`TaxReportTenantScope`) · B-04 `TaxController` 12 endpoint เขียนมีด่าน
- **F (สัญญาฟอร์ม↔API · 52d82b3)** — A01 A02 A03 A04 A06 A08 A09 A10 (+A16/A17 บางส่วน) ปิด "กดสร้างแล้วไม่มีทางสำเร็จ" 6 หน้า · อัตรา ≤ 0 ถูกปฏิเสธ + ตัวอ่าน
  กรอง `MidRate > 0` · `enum_number_compare_check` กติกา 3
- **U2 (ไฟล์แนบ/คิว · f04c145)** — #29 ด่านไฟล์แนบทุกชนิดตามสิทธิ์โมดูลเจ้าของ (`AttachmentPermissionScope`) · #30 เพดานพื้นที่ = เตือน (`Subscription.CurrentStorageUsed` ไม่มี
  ผู้อ่านแล้ว — คิดจากของจริง) · #31 คิวรวมร่างจากสแกน · #32 คิวครอบงวดที่ยังไม่ปิด (`ClosedPeriodRanges`)
- **P2 (เงินเดือน · 34e62a3)** — #35/D-02 ฐาน ปกส. หักลาไม่รับค่าจ้าง (ไม่ได้จ่ายอะไรเลย = 0) + คำนวณใหม่รอบที่ยังไม่จ่าย · D-01 50 ทวิ ภ.ง.ด.1 ใช้ `EmployeeTaxIdentity`
  (เดิม `Employee.TaxId` ไม่มีผู้เขียน ⇒ ไม่เคยออก) · A05/D-07 แก้พนักงานไม่หายเงียบ (`EmployeeRecordEdit` · ค่าปิดบังไม่ทับของจริง)
- **O2 (ชื่อ/ที่อยู่/สาขา/วันที่ · abd33a7)** — #18 ทะเบียน RD แยกสาขาทั้งแถว · #19 รายงานผู้ติดต่อข้อมูลเสีย · #20 คีย์เลขภาษี+สาขา (`ContactTaxBranchKey`) ·
  #21 "สาขาที่ 00008" · #22 ใบอังกฤษ+สกุลต่างประเทศเชื่อ engine · #26 ที่อยู่ผู้ซื้อจากบริษัทเรา 0.70 · #27 python date reader · #15 ตรงแล้ว
- **L2 (มัดจำ/ที่พัก · 5721ebd)** — #34 มัดจำ 3 โหมด `DepositPolicyResolver` (ที่พัก → บริษัท → ประเภทธุรกิจ · ค่าเริ่มต้น VAT ทันที = ไม่เปลี่ยนใครเงียบ) ·
  P0-1 เช็คเอาต์ที่พักใช้มัดจำจริง (VAT ทันที = หักฐานก่อน VAT) · P0-2/F-03 ยกเลิกไม่ลงคืนเงินจนพนักงานยืนยัน `refund-paid` · P0-3 ห้ามหักมัดจำออกใบกำกับเต็มจำนวน
  ทุกงวด · #36/F-01 บริการเสริมราคา 0 · S-06 `AutoConfirmOnDeposit` ต่อสาย · S-10 VAT ที่พักตามบริษัท
- **O1 (OCR ยอดเงิน · 8561d5e)** — #1/#3/#4 ยอดชำระจริง + บรรทัดปรับ 51120/51150 ที่ขั้นชำระ · #5 `[NO-ITEMS]` · #8 ผลต่างปัดเศษ 54960 (`DocumentRounding`) ·
  #9 §82/5(1) ผู้ซื้อบนกระดาษ · #10 e-Tax XML ใน PDF แก้ 7 ช่องที่ตกไป OCR เงียบ · #12 `[Σ-GAP]` ต้องรับทราบ (API ไม่ขัดจังหวะ) · #14 รายงานข้อมูลเก่า
- **V (ค่าตั้งถึงทุกส่วนงาน · 8bcc733)** — S-01 ธง VAT สองตัว stopgap (`CompanySettingsFactory` · `CompanyVatStatus` · รายงาน `vat-flag-consistency`) · S-02 e-Tax
  หลังออกเอกสารจุดเดียว `IssuedDocumentHooks` (POS/Integration/CMS/ใบเสร็จ settlement) · S-05 `IsVehicleDealer` ถึง §82/5(6) (`InputVatVehicleRule` ชุดเดียว) ·
  S-10 VAT CMS · ของแถม: บันทึกภายใน lead ไม่หลุดถึงลูกค้า
- **W (ค่าตั้งมีผลจริง · 11e79b2 · 10ed065)** — ฝ่ายค้าน C1: งานเจ้าของต้องทำโดยคนที่ล็อกอิน (`OwnerActionGuard`) · S-04 สวิตช์ API Access คุมคีย์ที่ออกแล้ว ·
  S-08 ปิดรับสมัครกันที่ server (`RegistrationPolicy` · คำเชิญยังได้) · S-12 อีเมล/LINE/อีเมลตั้งเวลาใช้หัว+ภาษาเดียวกับ PDF (+ HtmlEncode) · S-13 "" = ล้าง ·
  S-20 number-series ปฏิเสธช่องที่ไม่มีผล · S-06 ช่องที่ไม่มีผลถูกล็อก+ป้าย · checker `settings_reader_check`
- **S2 (ทางเข้าอื่นของไฟล์แนบ · fd880c2)** — ด่านเป็น service `IAttachmentAccessGate` ตัวเดียว: ใบเสร็จนำส่ง · รูป/ผลอ่าน/รายการ/คิวของสแกน (ด่านของเจ้าของไฟล์) ·
  ใบเบิกหลังอนุมัติ · ถัง 50 ทวิ ก่อนบันทึก · นำเข้าจากโปรแกรมอื่น · checker `attachment_gate_check`
- **C3 (คีย์ผู้ติดต่อต่อ · 4fcd06b)** — API v1 `contactBranchCode` · แถวสาขาว่างเป็นของ สนญ. เท่านั้น · 19 → 7 จุดที่จับด้วยเลขอย่างเดียว · checker `contact_taxid_only_match_check`
- **M2 (เงินรอบฝ่ายค้าน · fc503b5)** — #35 รอบ Approved ที่ยื่น/นำส่ง/ปันต้นทุนแล้วห้ามคำนวณใหม่ · POS COGS สูตรเฉพาะวัตถุดิบ TrackStock · คืนวัตถุดิบต้นทุนวันขาย ·
  void เมื่อ JE ขายถูกกลับด้วยมือ (`PosVoidSaleJournal`) · pos-packages ประเภทคอมมิชชัน (option 0/1 เก็บกลับด้าน) · อัตราแลกเปลี่ยน 0 · checker `required_call_site_check`
- **ฝ่ายค้านรอบ 1 → แก้** (`review193-security/money/L2/O1/V-C3/S2/M2/W.md`): L2 198fb5c (C1–C10: integration ไม่ถอยไปตั้งหนี้เงียบ · มัดจำเกินยอด = ค้างคืน ·
  ยกเลิกซ้ำ · void กลับรับรู้มัดจำ · สูตรปัดตัวเดียว) · O1 f7bad8d (`ApprovalAcknowledgement` 4 แหล่ง · convert/clone สืบทอดผลต่างปัดเศษ · e-Tax Allowance/Charge ·
  `[PAY-AT-PAYMENT]` · T05/T06 §82/5(2)) · V 7601891 (e-Tax ข้ามใบไม่ใช่ใบกำกับเต็มรูป · ใบเสร็จ §78/1 ได้ hook · ลิสต์รถ JS → เซิร์ฟเวอร์ · `VatStatusConfirmedAt`) ·
  C3 6b2e7fb (`SoftScope` ทุกทางเข้า · "-" ไม่ใช่เลขภาษี) · S2 f4aa7d4 (สแกนใช้ด่านเจ้าของไฟล์ทุก action · ห้ามย้าย/ลบไฟล์ของรายการอื่น · ใบเบิก SoD) ·
  M2 4cbb715 (หลักฐานยื่น = ปฏิทินภาษี · นำส่งผูกรอบ · ✏️ ล็อกด้วยหลักฐานเดียวกัน · checker 7 ชนิด) · W c5df11c (คีย์ห้ามทำลายหลักฐาน/ตั้งนโยบาย ·
  subscription เจ้าของเท่านั้น · email-template จาก server · **hash chain v2** round-trip ได้)
- **ฝ่ายค้านรอบ 2 → แก้** (`review193-r2-money/sec-tax/W.md`): L2 979eefe (N1 มัดจำออกใบกำกับแล้วหักแบบ "ฐานก่อน VAT" **ทุกเส้น** — ยกเลิกทาง "ลงได้+ธง" ของรอบ 1 ·
  integration ปฏิเสธก่อนออกเลข · N4 VAT คืนคิดจากยอดสะสม) · O1 0eb8492 (N5 `FitsDocument` · N6 ลายเซ็นถามคำเตือนก่อนบันทึก · ปิดยอดด้วยบรรทัดปรับไม่หัก WHT) ·
  V c641f6c (ข้ามเงียบเฉพาะเจตนา · ใบ 0% เป็นใบกำกับ · ยืนยัน VAT เมื่อแตะจริง · `ManualInputVatLineRule` · checker hook หลัง commit) · C3 69ccf37 (`AdoptTaxId` ·
  กุญแจ tenant ก่อน) · S2 0a82911 (ใบเบิกด่านสิทธิ์ใน service + SoD · ลบร่างแล้วสแกนกลับมาใช้ได้ · คีย์โมดูล register-asset/import-stock · ระยะเก็บไฟล์สแกน 30 วัน) ·
  W b371d4c (`Analyze` แยก ถูกแก้/ขาดตอน/แตกกิ่ง · `[RequireOwner]` gateway/เอกสารลับ/กฎอนุมัติ · `GrantConsent` ต้อง Pii.View) · main 208f44d (API v1 OCR confirm กรองบริษัท)
- **ฝ่ายค้านรอบ 3 → แก้** (`review193-r3.md`): O1 132c2b5 (R3-3 ลายเซ็นลูกค้าใช้ซ้ำได้เฉพาะเนื้อหาเดิม `DocumentSignedContent.Hash` + ล็อกแถว B9) · C3 85dfda8 (R3-2
  เติมเลขภาษีเฉพาะชื่อตรงตัว · fuzzy + เลขจริง = แถวใหม่ · mod-11 · walk-in ไม่รับเลข B8) · V 41bb2c8 (R3-4 ค่ารับรองที่หลุดกลับมาปิดเคลม — baseline จาก git · B6 อัตรา −1
  ไม่ได้ VAT ติดลบ + สัญญา `INTEGRATION_RESYNC.md` §11) · S2 3da2760 (B7 งานกวาดไฟล์สแกนไม่ค้างหัวคิว)
- **ฝ่ายค้านรอบ 3 → แก้ (ต่อ)**: L2 04ce362 — **R3-1** ฐานมัดจำช่องแยก `Document.DepositBaseDeducted` (`BillDiscountAmount` = ส่วนลดการค้าอย่างเดียว ·
  `AllocateBillDeductions` ตัวเดียว · `SplitBillDeduction` เกินยอดขาย = ล้มดัง · `TaxedDepositDeductionProblem` · renderer ×2 สองแถว · echo + revision ·
  migration ย้ายค่าเดิม · convert สืบทอดตามสัดส่วนและนับการรับรู้ของใบแม่ · clone พ่วงส่วนลดแต่ไม่พ่วงมัดจำ) · **R3-5** `DrivesGuardMessage` ทางเดียว ·
  **R3-6** idempotency Integration กรอง Voided ในคิวรี + ใหม่สุดก่อน ครบ 6 เมธอด · **B1** ตาข่าย void ยกเลิกก่อนประทับ · ล้ม = `ChangeTracker.Clear` +
  หมายเหตุจริง + ยิงซ้ำล้มดัง (`TaxedDrivesVoidFailedMarker`) · **B2** รับรู้มัดจำตอนอนุมัติ `FOR UPDATE` · **B3** "ปนกัน" ตัดสินจากเลขที่ชี้มัดจำจริง · **B4** ด่านสถานะ
  ก่อนตัวแปลง + "หักมูลค่ามัดจำ" ห้ามแก้ย้อนหลัง · **B5** ปิดตามโดยผลข้างเคียง (แถวมัดจำดูช่องใหม่) · ที่พักใช้ `AdoptTaxId(..., ContactMatchKind)` + ลบรูป
  `[Obsolete]` · main 1aa8ef3 แก้ CS0029 หลัง merge O1 (CI run 36047169474) · เทสต์ `TaxedDepositDeductionTests` (6) · `DepositPolicyResolverTests` (+4)
- **ฝ่ายค้านรอบ 4 → แก้** (`review193-r4.md` · d0ca176e): O1 de5dc4cd (**R4-1** ตัวตัดสินเดียว `DocumentSignedContent.IsSignatureCurrent` ทุกเส้นที่อ่านลายเซ็นลูกค้า —
  ด่านเซ็นครบใน `ApproveDocumentAsync` 422 `SIGN-CUSTOMER-STALE` · แทนที่ลายเซ็นที่ไม่ตรงเนื้อหาตอนอนุมัติ · PDF ×2 ผ่าน `ResolveSignersAsync` · API `SignatureStaleReason` ·
  ข้ามบริษัทบันทึก hash · **R4-2** hash v2 ครอบวันที่/เงื่อนไข/ภาคผนวก/บัญชีธนาคาร/ภาษา/แบรนด์/สาขา/อ้างอิง/มัดจำ/ประเภทชำระ) · L2 c3820dce (**R4-3**
  `DepositBaseSplitMigrationSql` ครั้งเดียวจริง: advisory lock → `information_schema` → มีคอลัมน์แล้ว = ไม่ย้าย · **P4-1** ป้าย `[DEPOSIT-BASE-SPLIT]` · **P4-3**
  ข้อความทางไปต่อที่ทำได้จริง · **P4-4** `TaxedDepositDeductionAllowed` ฝั่งขายเท่านั้น · ที่พัก `StampTaxIdWarning`) · C3 cb552889 (**R4-4** `NameMatchKind` = ชื่อแกน +
  คลาสรูปนิติบุคคล `EntityFormOf` · **P4-5** `TaxIdChecksumWarning`/`StampTaxIdWarning` ป้าย `[TAXID-CHECKSUM]` · integration `Warnings` / API v1 `contact.taxIdWarning`) ·
  W 960e98cd (**P4-6** ข้อความขาดตอนไม่ฟันธงว่าถูกลบ) · main 8a8372c0/cb3d62aa (CLAUDE.md F2 ข้อ 2 · helper กลาง · กฎ M hash chain v2)
- **ยังเปิดหลังฝ่ายค้านรอบ 4**: P4-2 (ใบอนุมัติช่วง d788c2a→198fb5c ที่รับรู้จากหน้าเงินมัดจำไม่ถูกย้าย — อยู่บน branch ที่ยังไม่ deploy) · P4-7 (`generate-pdf`/`generate-html`
  ของเอกสารลับยังไม่เดินด่านชั้นความลับ — backlog ทีม W) · แถวผู้ติดต่อเก่าที่เก็บเลข checksum ผิดไม่ติดป้ายย้อนหลัง (เจ้าของตัดสิน)
- **ความเสี่ยงที่รู้ (ไม่ใช่บั๊กค้าง)**: ร่างที่มีฐานมัดจำแล้วเลือกมัดจำ VAT พักแบบขับ JE ⇒ ด่าน "ห้ามหักสองชั้น" ล้ม (ยังไม่มีปุ่มล้างฐานมัดจำ — สร้างใบใหม่) ·
  ป้ายแถวมัดจำพิมพ์ `DepositAppliedRef` ทั้งสตริง · `required_call_site_check` ช้าลง ~87 → ~124 วินาที (ควรแคชผล mask ต่อไฟล์)
- **เครื่องมือใหม่ (ทั้งหมดอยู่ใน `check_all.sh` + CLAUDE.md หมวด F)**: `required_call_site_check` · `settings_reader_check` · `approved_status_writer_check` ·
  `company_settings_factory_check` · `contact_taxid_only_match_check` · `attachment_gate_check` · `owner_action_wiring_check` · `tools/employee_form_contract_sim.js` ·
  `write_permission_gate_check` WATCHED +13 ไฟล์
- **ข้อมูลเก่า — ไม่แก้หลังบ้าน (คำตัดสิน #14) · มีรายงาน/SQL ให้คนตัดสิน**: `GET /api/ocr/amount-audit` · `GET …/contact-hygiene` · `GET …/vat-flag-consistency(/zero-rated-tax-invoices)` ·
  `GET …/pos/packages/commission-review` · `GET /lodging/extras/needs-reselect` · SQL ใน `r193-L2.md` §3/§9 · `r193-E.md` §4b (JE ขายเมนูสูตรที่ลง COGS 0)
- **คำถามเจ้าของที่ค้าง (ห้ามเดาแทน — รายละเอียดท้ายรายงานแต่ละทีม)**: Q1 ธง VAT ไหนเป็นต้นทาง · Q2 วงเงิน/SoD/Budget ของ POS/Integration · Q3 ค่าตั้งที่ไม่มีผล
  "ต่อสาย หรือ ลบ" (baseline 82 แถว) · Q4 `EnforceFullTaxInvoiceFields` · Q5 opt-out ข้อมูลเรียนรู้ข้ามบริษัท · Q6 LINE OA ต่อบริษัท · มัดจำ: VAT ของมัดจำที่ริบ ·
  ผังพักมัดจำที่พัก 21510/21713 · WHT จากมัดจำ · คืนเงินผ่าน gateway อัตโนมัติ · TakeTime มัดจำ VAT ทันที (Q8) · ปัด ±0.01 (Q9) · สิทธิ์ "คืนเงินแล้ว" (Q10) ·
  ใบเช็คเอาต์ที่ void (Q11) · เงินเดือน: ฐาน ปกส. นับเบี้ยเลี้ยงที่ไม่ใช่ค่าจ้าง · "ยื่นแล้ว" ต้องมีเลขรับไหม · ยกเลิกการปันต้นทุน · `RevenueRecognitionMethod`
  ต่อสายหรือถอด · คอมมิชชันเก่าคิดกลับด้าน (0 = % · 1 = บาท) · `StaffCommissionSummaries` ไม่มีผู้เขียน · สิทธิ์: Accountant ได้ `CompanySettings.Edit`
  โดยปริยายไหม · 401 → 403 · serialize การประทับ audit (sealer job vs advisory lock) · เทสต์ hash chain บน PostgreSQL จริง · ใครอ่าน audit log ได้ ·
  ไฟล์แนบ: static `/uploads/` · คีย์ "ดู" ของโมดูล · SoD บริษัทคนเดียว · ระยะผ่อน 30 วันของไฟล์สแกน · ผู้ติดต่อ: migrate "-" → NULL · data-fix แถว สนญ.
  ที่ถูกทับ · สต็อก: E-02 `CostingMethod` ต่อสายหรือแก้ศูนย์ช่วยเหลือ · JE ขายเมนูสูตรเก่าที่ COGS 0
- เอกสาร: DOCUMENT_FLOW §2.2 · §2.3 · §2.5 · §2.6 · §2.8 · §3.2 · §3.3 · §3.4 · §3.7 · **§3.8 ใหม่** · §5.2 · §6.1 · §6.2 · §6.2h · **§6.2i/§6.2j ใหม่** · §6.3 · §6.5 · §7 · §8 ·
  ACCOUNT_STRUCTURE §3.1 · **§3.1c ใหม่** · §4 · §5 · TEST_PLAN (ตารางรอบ 193 · BOM-04/12/13 · ADM-U08) · lessons +12 · ติ๊ก DECISIONS/audit-settings/audit-deposit/
  report-A/B/D/E/F/G (2026-09-21) · SYSTEM_REVIEW W1 · OCR_PIPELINE T4-14 · review193-r3/r4 (ติ๊ก)
— commit 7a16f097)_

_Last verified against codebase: 2026-09-25 (รอบ 194 — ทีม R แก้ผลฝ่ายค้านถดถอย/ความปลอดภัย `erp-review/2026-09-25/review194-regsec.md`:
- **C1** เงินประกันที่ต้องคืนกลายเป็นมัดจำค่าห้อง/ค่าเริ่มต้นบริษัทได้ ⇒ `ResolveKind(..., priceChannel:)` + ชั้น ④ กรองลักษณะเสมอ (`AcceptableAsPriceDeposit` ·
  ข้ามแล้วเตือน `DEP-KIND-NATURE`) · `SetDefaultAsync` ปฏิเสธเงินประกัน (`DefaultKindProblem`) · `UpdateAsync` ด่านเปลี่ยนลักษณะ (`NatureChangeProblem`:
  มีใบอ้าง/ผูกที่พักขัดช่อง/เป็นค่าเริ่มต้น) · `SecurityDeposit` ไม่ fallback ใบอื่น · CMS `PrePaymentKindId` · migration `DepositKindSecurityDefaultFixSql`
- **C3** ใบรับเงินประกันถูกยกเลิก ⇒ `SecurityLinkState` (DocumentGone = ไม่ค้าง รับใหม่ได้) + DTO `SecurityDepositOpen`/`SecurityDepositNote`
- **C4** ข้อความ "พิมพ์เหตุผลเป็นหมายเหตุบนใบ" ไม่จริง ⇒ แก้ข้อความ (บันทึกภายใน + คำเตือนตอนอนุมัติ)
- **P4** `documents.html` hydrate ประเภทรอรายการ (promise เดียว + ลำดับ) · **P6** schema รอบ 194 ย้ายเข้า `GetAlterStatements` + ชุดหลัง log แทน `catch {}`
- เทสต์ `DepositKindNatureGuardTests` · checker `required_call_site_check` +6 กติกา · ค้าง: P5 (seed ตามประเภทธุรกิจตอนเปลี่ยนภายหลัง) · C2/C5/P1–P3 (ทีม M)
— commit <pending>)_

_Last verified against codebase: 2026-09-25 (รอบ 195 — ทีม I แก้ใบ Scommerce TXE05202609T004679 ตาม `erp-review/2026-09-25/ocr-scommerce/report-X.md` P1/P2/P4 + report-Y:
- **P1** `Helpers/OcrLineVatPlanner.PlanWholeInvoice` — ชั้น "ตัวเลขหัวใบพิสูจน์อัตราทั้งใบ" ใน `BuildScanLinesAsync` **ก่อน** `ThaiVatTypeRule.Suggest` (เติมเฉพาะบรรทัดว่าง ·
  VAT หัวใบ = 7% ฐาน / 7/107 ยอดรวม · Σ บรรทัด = ฐาน · ยอดยกเว้นบนกระดาษ 0/ไม่มี · ชั้นบนขัด = Unknown) ⇒ ใบ Scommerce [7,7] · VAT 328.67 · 4,695.34 · ผลต่างปัด −0.01 · 5,024.00 · ไม่มี [Σ-GAP]
- **P2** `ThaiVatTypeRule.NotExemptDespiteKeyword` + นมผง/milk powder/formula/นมถั่วเหลือง/นม UHT/นมยูเอชที (คำ "นม" ยังอยู่ — ขอบเขตนมสด §81 รอเจ้าของ)
- **P4** `OcrLineVatPlanner.RateAdvice` → `OcrApprovalGapWarning.Build(..., rateAdvice)` รวมข้อยอดรวม/VAT/อัตราเป็นข้อเดียวพร้อมทางแก้เป็นตัวเลข · ท่อน "ตอนนี้…" ข้อแรกข้อเดียว ·
  `OcrAmountIntegrity`: บรรทัดยอด 0 ไม่นับเป็นบรรทัดมี VAT · ส่วนต่างยอดรวมที่มาจาก VAT ทั้งก้อนบอกสาเหตุ "อัตรา VAT" · `KindOf` (ค่าคงที่คำขึ้นต้นตัวเดียว)
- report-Y: บรรทัดยอดก่อนลด 0 ไม่ได้ % ส่วนลด (`OcrLineReconciler.LineDiscountPercent`) · "ยกเลิก…ใบกำกับภาษีอย่างย่อ…แทน" = คำปฏิเสธ (`OcrDocumentRoleInferrer.ContainsAnyNotNegated`)
- เทสต์ `OcrLineVatPlannerTests` (สองครึ่ง) + เพิ่มใน ThaiVatExemptKeyword/OcrAmountIntegrity/OcrApprovalGapWarning/OcrDocumentRoleInferrer/OcrLineReconciler/OcrReplayGolden (ช่อง `LineVatPlan`) ·
  checker `required_call_site_check` +2 กติกา · DOCUMENT_FLOW §1 OCR · lessons/ocr-pipeline +2 · CLAUDE.md กฎเหล็ก #3 ข้อ 2

_Last verified against codebase: 2026-09-25 (รอบ 194 — ทีม M2 แก้ผลฝ่ายค้านรอบสอง `erp-review/2026-09-25/review194-r2.md` (ยืนยันเองทุกข้อก่อนแก้):
- **R2-1** tax point ของการริบใช้ "ไม่มีแถวยื่นในระบบ" เป็นหลักฐานว่ายังไม่ยื่น ⇒ `ForfeitTaxPointDecision(..., depositPeriodLocked, today)`: วันรับเงินเฉพาะเมื่อ
  เดือนเดียวกัน หรือวันนี้ยังไม่เลยกำหนดยื่น (`TaxFilingDeadline` แบบกระดาษ) และไม่มีแถวยื่น/ล็อก/ปิดงวดบัญชี (`DepositReceiptPeriodLockedAsync`) และไม่ข้ามปีภาษี ·
  นอกนั้นงวดปัจจุบัน + ธง LATE-VAT ข้อความตรงความจริง · เส้นย้าย VAT พักแยก JE ลงวัน tax point ⇒ `DepositOutputVatRecognizedAt` ตรงเดือนที่ Cr 21911 จริง
- **R2-2** ใบเดิม (NULL · VAT 0 · ธง false) = กำกวม (`ForfeitZeroVatDeferred` สามสถานะ) ⇒ ทิศปลอดภัยคิด VAT · ตัวเลือกใหม่ `DepositForfeitAs.OriginallyNoVat`
  (=3) จากเซิร์ฟเวอร์ · อัตราช่องทางหาจากใบ (`DepositChannelVatRatesAsync` — ที่พัก `ChargeVat`) · เทสต์ M2 เดิมที่ล็อกใบ NULL เป็น "VAT 0 โดยชอบ" ถูกแก้
- **R2-3** เงินประกัน "ส่งมอบแล้ว" ⇒ ปฏิเสธ `DEP-SEC-PLAIN-REALIZE` + ทางไปต่อ 3 ทาง · `DepositSummary.PlainRealizeOffered` ⇒ หน้าต่างรับรู้ซ่อนตัวเลือก
- **R2-4** ยกเลิกใบมัดจำที่ตัดชำระหลายใบ ⇒ คืนยอดจ่ายรายใบจาก JV ที่จับภาพก่อนกลับรายการ (`DepositApplyJournals.GrossByTarget`/`AfterRestore`) · ขั้น 2
  ไม่กลับ JE ที่ถูกกลับแล้ว (เดิมโยนแล้วยกเลิกไม่ได้)
- **R2-5** `DepositsAppliedToAsync`/void 2b/purge 0c ตัวกรองเดียวที่ไม่นับคู่ที่ถูกกลับ ⇒ purge ใบที่เคย void ไม่ลบ JV ต้นฉบับ/ไม่หักรับรู้ซ้ำ
- **R2-6** เส้นหักมัดจำหลายใบ/แบบขับ JE ผ่อน one-shot แบบเดียวกับตัดชำระ + ข้อความ `AppliedElsewhereMessage` มีทางไปต่อ (ทุกเส้น)
- **P-a** คีย์ล็อกยอดใบมัดจำตัวเดียว `AdvisoryLockKey.DepositRealizeKey` — ปุ่ม/ที่พัก/CMS (session) · อนุมัติ/ตัดชำระ/คืน (`JobLock.TryXactLockAsync` ก่อนล็อกแถว) ·
  `JobLock` ปลดล็อกล้มหลังงานล้มไม่ทับ error เดิม
- **P1 ค้าง** ที่พักรับเงินประกันส่ง `depositChannelVatRate` · `UpdateDocumentAsync` จัดรูปซ้ำด้วยอัตราช่องทางของใบ
- **RevertTrackedChangesSinceAsync** → `Helpers/TrackedChangeRevert` (DetectChanges ก่อน · ตัด reference ฝั่ง principal · ตรวจซ้ำ) + เทสต์ด้วย DbContext ออฟไลน์
- เทสต์ `DepositRound194R2Tests` · แก้ `DepositForfeitRound194MTests`/`DepositKindTests` ตามสัญญาใหม่ · checker `required_call_site_check` +14 กติกา (ถอดกฎที่ล็อก
  `ForfeitZeroVatDeferred(ธงบนใบ…)`/`VatPeriodDeclaredOrFiledAsync` ในเส้นริบ — ล็อกพฤติกรรมผิดของ R2-1/R2-2)
— commit <pending>)_
