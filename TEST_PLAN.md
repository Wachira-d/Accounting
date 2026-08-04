# TEST_PLAN.md — แคตตาล็อกเคสทดสอบทั้งระบบ (Unit / Integration / System)

> **จุดประสงค์**: เป็น source of truth ของ "เคสที่ต้องทดสอบ" ทุกโมดูล ตั้งแต่ต้นจนจบ
> ใช้คู่กับ `DEVELOPMENT_PHASES.md` (แผนงานส่งต่อ) และ `DOCUMENT_FLOW.md` (behavior จริง)
> รหัสเคส (เช่น `DOC-U-01`) ใช้อ้างในแผนเฟส/commit — **ห้ามเปลี่ยนรหัสย้อนหลัง**
>
> รูปแบบรหัส: `<โมดูล>-<ชั้น>-<เลข>` โดยชั้น: `U` = unit, `I` = integration (DB จริง),
> `S` = system/E2E (ผ่าน API/UI)

---

## 0. สถานะปัจจุบัน

| รายการ | สถานะ |
| --- | --- |
| โปรเจกต์เทสต์ | `Accounting.Tests` (xUnit, net8.0) — **มีอยู่แล้ว** |
| เทสต์ที่มี | **92 เคส / 11 ไฟล์** — pure-logic ทั้งหมด (ไม่มี DB) |
| ครอบคลุมแล้ว | DepositReversalMath, DocumentConversion matrix, ExpenseCategoryResolver, OcrLineReconcile, Section65TerValidator, TaxPointResolver, WhtFormTypeGuard, **DocumentLabels (ภาษาเอกสาร)**, **ImportReviewHeuristics (local path ของ ImportDataReview)**, **ThaiAddressParser**, **VatClaimPeriod (§82/3 + กันดึงย้อนงวด)** |
| Integration tests | ❌ ยังไม่มี (ต้องใช้ Testcontainers PostgreSQL — ระบบใช้ raw SQL + `information_schema` จึง **ห้ามใช้** EF InMemory/SQLite แทน) |
| System/E2E tests | ❌ ยังไม่มี (แนวทาง: `WebApplicationFactory` + Playwright — Chromium มีใน env นี้แล้ว) |
| CI | ❌ ยังไม่มี (ดู ROADMAP Phase 0.2) |

**ข้อจำกัด env ปัจจุบันของ agent**: ไม่มี dotnet SDK → agent เขียนเทสต์ได้แต่รันไม่ได้
ผู้ใช้/CI ต้องเป็นคนรัน `dotnet test` — ทุก PR ที่เพิ่มเทสต์ต้องระบุในคำอธิบายว่า "ยังไม่ได้รัน"

### หลักการเลือกชั้นทดสอบ

1. **Unit** — logic ที่แยกจาก DbContext ได้ (ตัวคำนวณ, resolver, validator, parser)
   ถ้า logic ฝังอยู่ใน service method ใหญ่ ให้ **extract เป็น static/pure class ก่อน**
   (แบบเดียวกับ `TaxPointResolver`, `Section65TerValidator` ที่ทำไว้แล้ว)
2. **Integration** — พฤติกรรมที่ผูกกับ DB: tenant filter, transaction, concurrency,
   `information_schema` repoint, gap-free numbering, hash chain
3. **System** — เดินทั้ง flow ผ่าน API ตามผู้ใช้จริง: OCR→สร้างเอกสาร→Approve→
   รายงานภาษี→e-Tax; ยืนยันทั้ง status code, side effect และตัวเลขบนรายงาน

---

## 1. DOC — วงจรเอกสาร (DocumentService)

### Unit
| รหัส | เคส | คาดหวัง |
| --- | --- | --- |
| DOC-U-01 | ✅ มีแล้ว: conversion matrix ทุก DocumentType | ตาม `DocumentConversionTests` |
| DOC-U-02 | `ComputeLineAmounts` ราคา inclusive VAT 7% หลายบรรทัด เศษสตางค์ | Σ(base)+Σ(vat)=grand เสมอ; ผลต่างปัดเศษลงบรรทัดสุดท้าย |
| DOC-U-03 | mixed VAT rate ในใบเดียว (7/0/exempt) | VatAmount แยกตามบรรทัด; exempt ไม่ขึ้นในฐาน VAT |
| DOC-U-04 | ส่วนลดท้ายบิล + ส่วนลดรายบรรทัดพร้อมกัน | ฐาน VAT = หลังหักส่วนลดทั้งสองชั้น |
| DOC-U-05 | CN ยอดเกินใบเดิม (รวม CN ก่อนหน้า) | ถูก block (§86/10) |
| DOC-U-06 | DN/CN อ้างใบเดิมที่ Voided | ถูก block |
| DOC-U-07 | CN ข้ามเดือนภาษี > 1 เดือนโดยไม่มี LateReason | ถูก block / require reason |

### Integration
| รหัส | เคส | คาดหวัง |
| --- | --- | --- |
| DOC-I-01 | Approve พร้อมกัน 2 threads บนเอกสารเดียว (race) | ออกเลขครั้งเดียว, JE ชุดเดียว, อีก thread ได้ error ชัดเจน |
| DOC-I-02 | Approve 100 ใบพร้อมกันคนละเอกสาร | เลขเอกสาร gap-free ไม่ซ้ำ (unique per Company+Branch+TaxYear) |
| DOC-I-03 | ลบ Draft → เลขไม่ขยับ (Draft ไม่กินเลข) | ใบถัดไปที่ Approve ได้เลขต่อเนื่อง |
| DOC-I-04 | Void → JE reverse + stock reverse ครบ | GL/สต๊อกกลับเท่าก่อน Approve เป๊ะ |
| DOC-I-05 | Restore ใบ Voided ที่ยังไม่ยื่นภาษี | กลับเป็น Draft **คงเลขเดิม**; re-approve ไม่ออกเลขใหม่ |
| DOC-I-06 | Restore ใบที่มี e-Tax accepted / อยู่ในรายงาน Filed | ถูก block |
| DOC-I-07 | เอกสารข้าม tenant: user A เรียกดู/แก้/Approve ใบของ company B | 404/403 ทุก endpoint (ห้ามรั่วแม้ Id ถูก) |
| DOC-I-08 | Convert Quotation→Invoice→TaxInvoice→Receipt ยอดตรงกันทุกชั้น | header+lines+VAT ตรงต้นทาง; แก้ต้นทางหลัง convert ไม่กระทบปลายทาง |
| DOC-I-09 | มัดจำ: รับมัดจำ→Apply บางส่วน→Refund ส่วนเหลือ | 11640/21310 เดินครบ; Apply เกินยอดถูก block |
| DOC-I-10 | ใบที่ PartiallyPaid → Void | block (ต้องยกเลิกรับเงินก่อน) |

### System
| รหัส | เคส | คาดหวัง |
| --- | --- | --- |
| DOC-S-01 | flow ขายเต็มสาย: Quotation→อนุมัติ→Invoice→TaxInvoice→รับเงิน→ภ.พ.30 | ยอดใน ภ.พ.30 = VAT ของ TaxInvoice ใบนั้น เดือน tax point |
| DOC-S-02 | flow ซื้อเต็มสาย: PO→GRN→PurchaseInvoice (3-way match)→PV จ่าย | GRNI เกลี้ยง, AP ปิด, input VAT เข้ารายงานซื้อเดือนที่รับใบกำกับ |
| DOC-S-03 | สร้างใบผ่าน UI แล้วกด Approve ตอน draft มี validation ผิด | ข้อความ error ไทยชัดเจน ไม่ swallow |

---

## 2. TAX — ภาษีมูลค่าเพิ่ม + รายงาน (TaxService, TaxPointResolver)

### Unit
| รหัส | เคส | คาดหวัง |
| --- | --- | --- |
| TAX-U-01 | ✅ มีแล้ว: tax point ขายของ/บริการ/ซื้อ | ตาม `TaxPointResolverTests` |
| TAX-U-02 | 7/107 ปัดเศษ: gross 100.00, 107.00, 1.00, 0.01 | `round(gross×7/107, 2)` ตรงทุกเคส; base+vat=gross |
| TAX-U-03 | §82/3: invoice เดือน M, ยื่นเดือน M+6 พอดี | ยัง claim ได้ (=6 ไม่เกิน); M+7 → block |
| TAX-U-04 | §82/5(6): หมวดรถยนต์นั่ง + IsVehicleDealer=true | dealer claim ได้; ปกติ block พร้อม RuleCode `RD-82/5(6)` |
| TAX-U-05 | ใบกำกับอย่างย่อฝั่งซื้อ | `InputVatBlocked=true` ไม่เข้า PurchaseVatReport |

### Integration
| รหัส | เคส | คาดหวัง |
| --- | --- | --- |
| TAX-I-01 | generate รายงานขายเดือน M: ใบ tax point M แต่ invoice date M+1 | อยู่ในรายงาน M (ตาม tax point ไม่ใช่ invoice date) |
| TAX-I-02 | regenerate รายงานที่ Filed แล้ว | block หรือสร้าง revision ใหม่ — ห้ามเขียนทับของที่ยื่นแล้ว |
| TAX-I-03 | CN เดือน M อ้างใบเดือน M-1 | CN อยู่ในรายงานเดือน M เป็นยอดติดลบ; ภ.พ.30 ช่อง 4 ถูกต้อง |
| TAX-I-04 | ภ.พ.30 reconcile: Σ(รายงานขาย) − Σ(รายงานซื้อ claimable) = ยอดชำระ/เครดิต | ตรงทุกเดือน รวมเดือนที่มี CN/DN/มัดจำ/undue VAT |
| TAX-I-05 | undue VAT: Invoice (11640) → รับเงิน/ออกใบกำกับ (11610) | reclass ครบเมื่อ realize; ยอดค้าง 11640 = ใบที่ยังไม่ถึง tax point เท่านั้น |
| TAX-I-06 | สองสาขา (BranchCode ต่างกัน) เดือนเดียวกัน | รายงานแยกสาขา; เลขใบกำกับ running แยกสาขา |
| TAX-I-07 | snapshot: แก้ชื่อ contact หลังรายงาน generate แล้ว | แถวรายงานเดิมไม่เปลี่ยน (snapshot คงเดิม); regenerate เท่านั้นที่เห็นชื่อใหม่ + มี ContactIdentity audit |

### System
| รหัส | เคส | คาดหวัง |
| --- | --- | --- |
| TAX-S-01 | เดือนจริง 30 ใบผสม (ขาย/ซื้อ/CN/อย่างย่อ/ต้องห้าม/ZeroRated/Exempt) | รายงาน 3 คอลัมน์ (7%/0%/ยกเว้น) + ภ.พ.30 ตรง manual คำนวณมือ |
| TAX-S-02 | export e-Filing แล้ว diff กับตัวเลขบนหน้าจอ | ไฟล์ยื่น = หน้าจอ = DB ทุกช่อง |

### ปฏิทินนำส่ง (dashboard) — `StatutoryRemittanceService.GetFilingCalendarAsync`
| รหัส | เคส | คาดหวัง |
| --- | --- | --- |
| CAL-U-01 | ✅ มีแล้ว: กำหนดยื่นต่อแบบ (ปกส. 15/15, ภ.พ.30 15/23, ภ.ง.ด. 7/15) | `FilingCalendarRulesTests` |
| CAL-U-02 | ✅ มีแล้ว: ภ.พ.30/สปส./ภ.ง.ด.1 = ต้องยื่นแม้ยอด 0 | `FilingCalendarRulesTests` |
| CAL-I-01 | เดือนที่ VAT สุทธิ = 0 และยังไม่ยื่น | ช่อง `Pending` + hint "ยื่นแบบเปล่า" — **ห้ามหายไปจากตาราง** |
| CAL-I-02 | เดือนที่ยังไม่ได้กด "สร้างรายงาน ภ.พ.30" | ช่อง `Unknown` (ไม่ใช่ `NotRequired`) + ลิงก์ไปหน้ารายงาน |
| CAL-I-03 | เดือนที่ VAT สุทธิ < 0 (ขอคืน/ยกไป) | ยังต้องยื่น — สถานะไม่ใช่ `NotRequired` |
| CAL-I-04 | ปิดงวด สปส. จากหน้า payroll (stamp `SsoSettledAt` ไม่มีแถว remittance) | ช่อง `Filed` ไม่ใช่ `Partial` |
| CAL-I-05 | นำส่ง ภ.ง.ด.1 บางส่วน แล้วเพิ่มรอบเงินเดือนใหม่ในเดือนเดียวกัน | ช่องกลับเป็น `Partial` พร้อมยอดค้างที่ถูกต้อง |
| CAL-I-06 | บริษัทเปิดในระบบเมื่อ 3 เดือนก่อน ขอปฏิทิน 12 เดือน | 9 เดือนแรก = `NotRequired` (ไม่ขึ้นแดง) |
| CAL-I-07 | บริษัทไม่ได้จด VAT / ไม่ได้ขึ้นทะเบียนนายจ้าง | แถวนั้นทุกช่อง `NotRequired` + เหตุผลชัดเจน |
| CAL-I-08 | ภ.ง.ด.3/53 เดือนที่ไม่มีการหักภาษี | `NotRequired` (ไม่ใช่ `Pending` — ไม่ต้องยื่นแบบเปล่า) |
| CAL-S-01 | คลิกช่องบนปฏิทิน → deep-link `tax-remittance.html?type=&year=&month=` | เปิดฟอร์มนำส่งงวดนั้นทันที; งวดที่จ่ายแล้ว/ยอด 0 ขึ้น toast อธิบายแทนที่จะเงียบ |

---

## 3. WHT — หัก ณ ที่จ่าย

### Unit
| รหัส | เคส | คาดหวัง |
| --- | --- | --- |
| WHT-U-01 | ✅ มีแล้ว: ภงด.3/53 form guard | ตาม `WhtFormTypeGuardTests` |
| WHT-U-02 | อัตราตามประเภทเงินได้ ครบตาราง ท.ป.4/2528 (บริการ 3, โฆษณา 2, ขนส่ง 1, เช่า 5, ดอกเบี้ยบุคคล 15/นิติ 1, ปันผล 10) | ตรงตาราง; ประเภทที่ไม่รู้จัก → ไม่หักเงียบ ๆ ต้อง flag |
| WHT-U-03 | ฐานคำนวณ = ก่อน VAT | ค่าบริการ 1,000 + VAT 70 → หัก 30 ไม่ใช่ 32.10 |
| WHT-U-04 | threshold: งวดละ 800 แต่สัญญารวม 2,400 | หักทุกงวด (นับต่อสัญญา) |
| WHT-U-05 | ม.70 จ่าย ตปท. + DTA override | ใช้ rate ตาม treaty เมื่อเอกสารครบ; ไม่ครบ → default 15% |

### Integration
| รหัส | เคส | คาดหวัง |
| --- | --- | --- |
| WHT-I-01 | PV มี WHT → Approve | 50 ทวิ ออกอัตโนมัติ 2 ฉบับ + JE เจ้าหนี้กรมสรรพากรถูกบัญชี |
| WHT-I-02 | Void PV ที่ออก 50 ทวิ แล้ว | 50 ทวิ ถูกยกเลิกตาม; ไม่โผล่ในแบบยื่นเดือนนั้น |
| WHT-I-03 | ยอดรวมแบบ ภงด.3/53 เดือน M | = Σ(50 ทวิ ที่ไม่ยกเลิก เดือน M) ทุกช่อง |

---

## 4. ETAX — e-Tax Invoice (ETDA)

| รหัส | ชั้น | เคส | คาดหวัง |
| --- | --- | --- | --- |
| ETX-U-01 | U | XML ทุก field บังคับตาม ขมธอ.3-2560 (TypeCode T01-T04, TaxID, BranchID 5 หลัก, ISO8601+07:00, THB) | schema-valid; ไทย/อักขระพิเศษ escape ถูก |
| ETX-U-02 | U | CN/DN: `DifferenceInformationAmount` + ReferenceID ใบเดิม | ตรง §86/9-10 |
| ETX-U-03 | U | ยอดใน XML vs DB vs PDF | ตรงกันทุกช่อง (สตางค์เดียวก็ห้ามต่าง) |
| ETX-I-01 | I | สร้าง e-Tax จากใบ Voided / Draft | block |
| ETX-I-02 | I | submission cron: ใบเดือนก่อนค้างส่ง | เข้า retry queue; `SubmittedToRdAt` set เมื่อสำเร็จเท่านั้น |
| ETX-S-01 | S | PDF/A-3 + embedded XML → เปิดอ่าน XML กลับ | XML ใน PDF = XML ที่ส่ง RD |

---

## 5. OCR — pipeline + auto-fill (กฎเหล็ก #3)

| รหัส | ชั้น | เคส | คาดหวัง |
| --- | --- | --- | --- |
| OCR-U-01 | U | ✅ มีแล้ว: line reconcile (qty/amount สลับ) | ตาม `OcrLineReconcileTests` |
| OCR-U-02 | U | ✅ มีแล้ว: expense category resolver | ตาม `ExpenseCategoryResolverTests` |
| OCR-U-03 | U | contact match: ชื่อสั้น <6 ตัวอักษร | ไม่ match ด้วยชื่อ (กันจับมั่ว) |
| OCR-U-04 | U | contact match: TaxId ตรงหลายราย ต่างสาขา | เลือกตามสาขา; ไม่มีสาขาตรง → 00000 ก่อน, deterministic |
| OCR-U-05 | U | enrich TaxId ลง contact: name-sim < 0.90 | ไม่เขียน + มี ProcessingNote เตือน |
| OCR-I-01 | I | สแกนใบเดิมซ้ำ 2 ครั้ง | ไม่สร้าง contact ซ้ำ, ไม่สร้างเอกสารซ้ำ (duplicate detection) |
| OCR-I-02 | I | DTO ที่ส่งไป review modal | ทุก field §86/4 ไม่ null/empty (fallback chain เติมครบ) |
| OCR-I-03 | I | user แก้ field ใน review → ยืนยัน | `RecordUserChoiceAsync` ถูกเรียกด้วย feedbackId ถูกตัว |
| OCR-S-01 | S | อัปโหลดรูปจริง → ยืนยัน 1 คลิก → Approve | เอกสารสมบูรณ์โดยผู้ใช้ไม่พิมพ์อะไรเลย |
| OCR-S-02 | S | kill-switch: ปิด AI provider ทุกตัวแล้วสแกน | flow เดินจบด้วย local path — ไม่มี error ถึงผู้ใช้ |

### LINE bot รับรูปใบเสร็จ (§2.2b) + ใบรับรองแทนใบเสร็จ (§2.2c)
| รหัส | ชั้น | เคส | คาดหวัง |
| --- | --- | --- | --- |
| LNE-I-01 | I | ส่งรูปโดยยังไม่ผูกบัญชี | ตอบวิธีผูก — ไม่เงียบ, ไม่ crash |
| LNE-I-02 | I | ผู้ใช้หลายบริษัท ยังไม่เลือก active → ส่งรูป | ตอบเมนูเลือกบริษัท; รูปไม่ถูกประมวลผล, ไม่เสียโควต้า |
| LNE-I-03 | I | รูปชัด ใบกำกับสมบูรณ์ confidence ≥ 0.85 | สร้างเอกสารร่าง + reply ประเภท/ผู้ขาย/ยอด/ลิงก์ reviewScan |
| LNE-I-04 | I | รูปเบลอ/ไฟล์เล็กเกิน (preflight fail) | ตอบคำแนะนำถ่ายใหม่ — **ไม่เสียโควต้า** |
| LNE-I-05 | I | ส่งรูปเดิมซ้ำใน 10 นาที (LINE double-send) | ตอบ "ส่งไปแล้ว + เลขเอกสารเดิม" — ไม่สแกนซ้ำ ไม่เสียโควต้า |
| LNE-I-06 | I | โควต้า OCR หมด | ตอบยอดใช้/เพดาน + ทางเลือกบันทึกด้วยข้อความ |
| LNE-I-07 | I | scan fail / duplicate / e-Tax XML | โควต้าถูก refund (กติกาเดียวกับหน้าเว็บ) |
| CIL-U-01 | U | บิลฝั่งซื้อ ไม่มี TaxId + ไม่มี VAT + target Expense/PV | target เปลี่ยนเป็น CertificateInLieu + ReasoningTrace อ้าง §65 ตรี |
| CIL-U-02 | U | ใบกำกับอย่างย่อ (มี VAT, ไม่มี TaxId ผู้ซื้อ) | **ไม่**เข้ากติกา CertInLieu — คงเส้นทาง §82/5(2) |
| CIL-U-03 | U | target CertificateInLieu ไม่มีเลขที่บนกระดาษ | ผ่าน critical-fields gate (เลขที่ไม่บังคับ); ยังบังคับวันที่+ยอด |
| CIL-I-01 | I | auto-create CertInLieu จาก LINE | เอกสารมี CertificateReason + CertifierName + รูปบิล relink เป็นไฟล์แนบ |
| CIL-S-01 | S | ถ่ายบิลแม่ค้าตลาดจริงส่งเข้า LINE | ได้ใบรับรองแทนใบเสร็จร่าง พิมพ์ PDF ได้ ฟอร์มครบช่องผู้รับรอง/พยาน |
| LNE-I-08 | I | สร้างเอกสารสำเร็จ → Flex card | มีปุ่มอนุมัติ (postback) + ปุ่มตรวจ (uri); flex push fail → fallback ข้อความธรรมดา |
| LNE-I-09 | I | postback approve จาก role Staff/Viewer/Auditor | ปฏิเสธพร้อมคำอธิบาย — เอกสารยังเป็นร่าง |
| LNE-I-10 | I | postback approve ด้วย docId ของบริษัทอื่น (data ปลอม) | ปฏิเสธ (tenant guard) — ไม่แตะเอกสาร |
| LNE-I-11 | I | postback approve ซ้ำ / เอกสาร Approved-Paid-Voided แล้ว | ตอบสถานะปัจจุบัน ไม่ approve ซ้ำ |
| LNE-I-12 | I | postback approve สำเร็จ | เลขเอกสารจริงออก + reply เลขที่; JE/ภาษีลงครบเหมือน approve หน้าเว็บ |
| LNE-I-13 | I | นักบัญชีอนุมัติบนเว็บ เอกสารที่มาจาก LINE | ผู้ส่งบิลได้ LINE แจ้ง (เลขที่/ยอด); ผู้อนุมัติ=ผู้ส่ง → ไม่แจ้งซ้ำ |
| LNE-I-14 | I | ส่งอัลบั้มหลายรูปรวดเดียว | ทุกใบถูกประมวลผลแยกกัน — 1 รูป = 1 scan = 1 การ์ด |

---

### Chatbot 2 ช่อง (CHATBOT_PLAN.md)
| รหัส | ชั้น | เคส | คาดหวัง |
| --- | --- | --- | --- |
| CHT-U-01 | U | `ChunkMarkdown` หั่นไฟล์ตาม ##/### + ก้อน >3500 | ทุกชิ้น ≤3500 ตัวอักษร, key ไม่ซ้ำ |
| CHT-U-02 | U | `WantsHuman` จับ intent "ติดต่อเจ้าหน้าที่"/"คุยกับคน" | true; ประโยคทั่วไป → false |
| CHT-U-03 | U | `ScrubInternalRefs` ลบ path/บรรทัดโค้ดจากคำตอบ | "Foo.cs:123" หายจากข้อความ |
| CHT-U-04 | U | `StripJsonWrapper` แกะ {"answer":"..."} / "..." | ได้ plain text; ไม่ใช่ JSON → คงเดิม |
| CHT-I-01 | I | public ถามเกิน 6/นาที ต่อ IP | 429 + ข้อความสุภาพ — ไม่เรียก AI |
| CHT-I-02 | I | คำถามเดิมซ้ำใน 10 นาที | คำตอบเดิม UsedAi=false — ไม่จ่าย AI ซ้ำ |
| CHT-I-03 | I | audience wall: public ถามเรื่องที่อยู่เฉพาะชิ้น Tenant/Internal | คำตอบไม่มีเนื้อหาชิ้นนั้น (retrieval ไม่หยิบ) |
| CHT-I-04 | I | kill-switch: ปิด provider ทุกตัว | บอทตอบ retrieval-only (⚙️ ระบบตอบ) ไม่ error |
| CHT-I-05 | I | "ติดต่อเจ้าหน้าที่" → admin ตอบ → widget poll | สถานะ WaitingAgent→AgentHandling; ข้อความ Agent ถึง widget; บอทหยุดตอบห้องนั้น |
| CHT-I-06 | I | 👍 บนคำตอบ → ถามคำถามเดิมอีกครั้ง (หลัง retrain) | student ตอบ local (short-circuit) — UsedAi=false |
| CHT-I-07 | I | tenant ถาม "ค่าน้ำมันลงหมวดไหน" | คำตอบอ้างเลขผังจากผังบัญชีของบริษัทตัวเองเท่านั้น |
| CHT-I-08 | I | tenant ของบริษัท A ถามข้อมูลบริษัท B | ไม่มีข้อมูล B ในคำตอบ (KB scope ต่อ companyId) |
| CHT-I-09 | I | user ที่ไม่ใช่สมาชิกบริษัทเรียก /assistant/ask | 403 |
| CHT-S-01 | S | flow เต็ม: ถามหน้าแรก → บอทตอบ → ขอเจ้าหน้าที่ → admin ตอบ → ปิดห้อง | ทุกขั้นทำงาน + ประวัติครบใน admin console |
| CHT-I-10 | I | rate limit: 2 instance ยิงพร้อมกัน (DB-backed) | เพดานรวมไม่คูณจำนวน instance |
| CHT-I-11 | I | ตาราง ChatRateBuckets ใช้ไม่ได้ (DB error) | fail-open — ยังตอบได้ ไม่ล็อกทั้งระบบ; ด่านห้อง/วัน + budget ยังคุม |
| CHT-I-12 | I | ชนเพดาน ≥3 ครั้ง/ชม. | ได้โจทย์บวกเลข; ตอบผิด = ถามต่อไม่ได้, ตอบถูก = ปลดล็อก |
| CHT-I-13 | I | purge job กับห้อง Public ที่เลย PurgeAfter | ข้อความหายจริงจาก DB + ชื่อ/อีเมล/IpHash เป็น null; ห้อง Tenant ไม่ถูกแตะ |
| CHT-I-14 | I | ขอคุยเจ้าหน้าที่ (intent + ปุ่ม) | LINE แจ้งทีมงานทั้งสองทาง; แจ้งล้มเหลวไม่ทำให้คำขอล้ม |
| CHT-I-15 | I | ยังไม่มีใครโหวตเลย แล้วถามคำถามซ้ำ | student โหลด lazy จาก teacher answer — ไม่ต้องรอ 👍 ก่อน |
| CHT-I-16 | I | ถามพร้อม documentId ของบริษัทอื่น | ไม่มี context ใบนั้น (tenant guard ใน BuildDocumentContextAsync) |
| CHT-I-17 | I | ถามพร้อม documentId ของบริษัทตัวเอง | คำตอบอ้างคู่ค้า/ยอด/ผังของใบจริง |
| CHT-I-18 | I | admin แก้บทความที่มาจากไฟล์ .md | ถูกปฏิเสธพร้อมบอกให้แก้ไฟล์ต้นทาง (กันแก้แล้วหายตอน refresh) |
| CHT-I-19 | I | admin เพิ่มบทความใหม่ แล้วทดลองค้นทันที | บทความถูกหยิบโดยไม่ต้อง restart (cache invalidate) |
| CHT-I-20 | I | LINE: "ถาม ค่าน้ำมันลงหมวดไหน" | ตอบจากผู้ช่วยของบริษัทที่ active พร้อมป้าย 🤖/⚙️ |
| CHT-I-21 | I | metrics: อัตรา UsedAi หลัง student จำคำตอบได้ | ค่าลดลงเทียบกับก่อนหน้า (ตัวชี้วัดความพร้อม) |

---

## 6. AI — Distillation loop (กฎเหล็ก #1)

| รหัส | ชั้น | เคส | คาดหวัง |
| --- | --- | --- | --- |
| AI-U-01 | U | ทุก `AiFeatureKey` มี `ILocalDistillationModel` register | test สะท้อน enum ↔ DI registration — feature ใหม่ที่ลืม register = เทสต์แดง |
| AI-U-02 | U | routing: local confidence ≥ 0.85 | ไม่เรียก provider (short-circuit) |
| AI-U-03 | U | budget guard: เกิน DailyCallCap/MonthlyBudgetUsd | ตกมา local เงียบ ๆ ไม่ throw |
| AI-I-01 | I | provider timeout/500 | คำตอบ local ใช้งานได้จริง ไม่ null/ฟอร์มว่าง |
| AI-I-02 | I | `AiFeedbackTrainingJob` รันกับ feedback ที่ user แก้ | local model ตอบตามคำแก้ในครั้งถัดไป (exact-input) |
| AI-I-03 | I | cold-start tenant ใหม่ | ตอบจาก seed/baseline ได้ ไม่รอ AI |

---

## 7. JE/STOCK — บัญชีแยกประเภท + สต๊อก + สินทรัพย์

| รหัส | ชั้น | เคส | คาดหวัง |
| --- | --- | --- | --- |
| JE-U-01 | U | JE ทุก template ประเภทเอกสาร | Σdebit = Σcredit เสมอ (รวมเคสเศษสตางค์หลายบรรทัด) |
| JE-U-02 | U | FIFO / WeightedAverage ต้นทุนขาย | ตรง worked example; LIFO ถูก reject |
| JE-U-03 | U | ค่าเสื่อม StraightLine/DecliningBalance ปีแรก/ปีสุดท้าย (pro-rata) | ตรงสูตร; ที่ดิน depreciation = 0 |
| JE-I-01 | I | ขายของที่ stock ไม่พอ | ตาม policy (block หรือ backorder) — ห้ามต้นทุนติดลบเงียบ ๆ |
| JE-I-02 | I | ปิดงวดแล้ว post JE ย้อนเข้า period ปิด | block |
| JE-I-03 | I | FX: invoice USD → รับเงินเรทต่าง + revalue สิ้นเดือน | FX gain/loss ลงบัญชีที่กำหนด; ยอดกลับ balance |
| JE-S-01 | S | Trial balance หลังเดินเอกสารครบทุกประเภท 1 เดือน | เดบิต=เครดิต; งบทดลอง = Σ JE |

---

## 8. SEC — Tenant isolation + สิทธิ์ + PDPA + Audit

| รหัส | ชั้น | เคส | คาดหวัง |
| --- | --- | --- | --- |
| SEC-I-01 | I | sweep ทุก endpoint (841): ยิงด้วย resource id ของ tenant อื่น | 403/404 ทั้งหมด — เขียนเป็น parametrized test iterate controller |
| SEC-I-02 | I | role ต่ำ (viewer) เรียก endpoint เขียน/อนุมัติ/ลบ | 403 |
| SEC-I-03 | I | AuditLog: พยายาม UPDATE/DELETE + ตรวจ hash chain | append-only; RowHash/PrevHash ตรวจ tamper ได้ |
| SEC-I-04 | I | PII: เลขบัตร ปชช. ใน response ที่ไม่มี role `pii:view` | mask `1-XXXX-XXXXX-XX-3`; PiiAccessLog ถูกเขียน |
| SEC-I-05 | I | DSR erase ข้อมูลที่ติด legal hold (พ.ร.บ.บัญชี 5 ปี) | ไม่ลบ + ตอบเหตุผล; ข้อมูลนอก hold ถูก erase จริง |
| SEC-U-01 | U | `ExecuteSqlRaw` ทุกจุดในโค้ด | ไม่มี string interpolation จาก user input (ตรวจแบบ static/grep test) |

---

## 9. SUB — Subscription / บริษัท / ผู้ใช้

| รหัส | ชั้น | เคส | คาดหวัง |
| --- | --- | --- | --- |
| SUB-U-01 | U | แพ็กเกจฟรีถาวร limit จากคอลัมน์หลัก + floor > 0 | ตามที่แก้ไปแล้ว — กัน regression |
| SUB-I-01 | I | subscription เดิม limit=0 → เรียก CheckUsageLimit | self-heal แล้วใช้งานได้ |
| SUB-I-02 | I | ใช้เกิน limit (เอกสาร/ผู้ใช้/OCR) | block พร้อมข้อความชัดเจน ไม่ crash |
| SUB-I-03 | I | สมัคร→trial หมดอายุ→ต่ออายุ | สถานะเดินถูก; ข้อมูลไม่หาย |

---

## 10. BNK/IMP — ธนาคาร + นำเข้า/ส่งออก

| รหัส | ชั้น | เคส | คาดหวัง |
| --- | --- | --- | --- |
| BNK-U-01 | U | statement matching: ยอดตรงหลายใบ, วันที่เหลื่อม | จับคู่ตาม tolerance ที่กำหนด; ambiguous → เสนอไม่ auto-match |
| BNK-I-01 | I | reconcile แล้ว unreconcile | สถานะกลับครบ ไม่ทิ้ง orphan |
| BNK-U-02 | U | `autoFillBankToMatch`: JV 77,678 + โอน 5 รายการวันเดียวกันที่รวมพอดี | เลือกครบทั้ง 5 ผลต่าง = 0 |
| BNK-U-03 | U | pool มีเงินเข้าปนอยู่ | ไม่หยิบเงินเข้ามาหักล้างให้ยอดพอดี (ทิศเดียวกันเท่านั้น) |
| BNK-U-04 | U | รวมได้ไม่พอดี (ขาด/ไม่มีชุดที่ลงตัว) | เติมเท่าที่ได้ + แจ้งยอดที่ยังขาด ไม่ยืนยันเงียบ |
| BNK-I-02 | I | หลายรายการโอน → JE เดียว ผ่านกลุ่มกระทบยอด M:N | ทุกรายการโอนเป็น Matched, JE ถูกใช้ครั้งเดียว, ยอดกลุ่มสมดุล |
| BNK-I-03 | I | ปุ่ม "หารายการโอนที่เหลือ" จากหน้าต่างจับคู่ 1:N | เปิดโหมดกลุ่มพร้อม preselect รายการเดิม + เอกสารเดิม |
| BNK-I-04 | I | unreconcile กลุ่ม M:N | รายการโอนทุกใบกลับเป็น Unmatched, JE ปลดล็อกใช้ใหม่ได้ |
| BNK-I-05 | I | JV เงินเดือนหลายขา (Dr เงินเดือน 77,678 / Cr ปกส.+ภงด.1 / Cr ธนาคาร 70,110) ในลิสต์กระทบยอด | แสดง **70,110** (ขาธนาคาร) ไม่ใช่ 77,678 (footing) + หมายเหตุยอด JE ทั้งใบ |
| BNK-I-06 | I | เลือกโอนออก 5 รายการ + JE ฝั่งขวา | ฝั่งรายการเป็นค่าลบ → ผลต่าง = ผลหักกันจริง ไม่ใช่ผลบวก (เดิม −147,788) |
| BNK-I-07 | I | จัดสรร JE เกินยอดขาธนาคาร (เช่นใส่ 77,678 ทั้งที่ขาธนาคาร 70,110) | backend ปฏิเสธ — เพดานคือขาธนาคาร ไม่ใช่ footing |
| BNK-I-08 | I | JE ที่ไม่มีบรรทัดลงผังบัญชีของธนาคารนี้ | ยังเลือกได้ (ไม่บล็อก) แต่ขึ้นป้าย "⚠️ ไม่มีขาที่แตะบัญชีนี้" |
| BNK-I-09 | I | ผู้ใช้พิมพ์ยอดจัดสรรเอง แล้วติ๊กรายการธนาคารเพิ่ม | ระบบไม่เซ็นทับค่าที่พิมพ์เอง (_manual) |
| IMP-U-01 | U | parse ตัวเลขไทย "1,234.50", วันที่ พ.ศ./ค.ศ. ปนกัน | แปลงถูก; พ.ศ. detect จากปี > 2400 |
| IMP-I-01 | I | import ไฟล์เดิมซ้ำ (opening balance) | idempotent — ไม่ duplicate (refresh in place) |
| IMP-I-02 | I | ไฟล์ header สลับคอลัมน์/มีแถวขยะ | ColumnMatch (AI+local) จับได้ หรือ reject พร้อมบอกแถว |

---

## 11. FE — Frontend (vanilla JS + Tailwind)

| รหัส | ชั้น | เคส | คาดหวัง |
| --- | --- | --- | --- |
| FE-S-01 | S | ทุกหน้าใน wwwroot/pages เปิดด้วย Playwright | ไม่มี console error / uncaught exception ตอนโหลด |
| FE-S-02 | S | ชื่อผู้ติดต่อ/สินค้า มี `<script>`/quote | ถูก escape ทุกจุดที่ render (กัน XSS) |
| FE-S-03 | S | ยอดเงินคำนวณฝั่ง client (ฟอร์มเอกสาร) vs server | ตรงกันทุกเคสปัดเศษ — server เป็น authority |
| FE-S-04 | S | ปุ่มยืนยัน OCR review กับข้อมูล confidence ต่ำ | field เหลืองแต่ approve ผ่านได้ (ไม่มี required error) |

---

## 12. ลำดับความสำคัญ (ใช้จัดเฟสใน DEVELOPMENT_PHASES.md)

| Priority | กลุ่ม | เหตุผล |
| --- | --- | --- |
| **P0 — เงิน/กฎหมายผิดโดยตรง** | TAX-I-04, TAX-I-05, JE-U-01, WHT-U-02/03, DOC-I-01/02, TAX-U-02 | ตัวเลขยื่นสรรพากรผิด = ลูกค้าโดนปรับ |
| **P0 — ความปลอดภัยข้อมูล** | SEC-I-01, SEC-U-01, SEC-I-03 | tenant รั่ว = จบธุรกิจ |
| **P1 — ความถูกต้อง flow** | DOC-I-04..10, TAX-I-01..03/06/07, WHT-I-01..03, ETX-* | ผิดแล้วแก้ย้อนหลังแพง |
| **P1 — mandate โปรเจกต์** | AI-U-01..03, OCR-I-02/03, OCR-S-02 | กฎเหล็ก #1/#3 ต้องมีเทสต์กัน regression |
| **P2 — ประสบการณ์ใช้งาน** | FE-*, BNK-*, IMP-*, SUB-*, OCR-S-01 | สำคัญแต่ผิดแล้วเห็น/แก้ง่ายกว่า |

### Definition of Done ต่อเคส
1. เทสต์รันผ่านบนเครื่อง dev/CI (`dotnet test`)
2. เคส integration ใช้ Testcontainers PostgreSQL — **ห้าม mock DbContext ทั้งก้อน**
3. เทสต์ที่สะท้อนกฎหมายต้อง comment อ้างมาตรา (เช่น `// §82/3 6-month window`)
4. เทสต์ fail ต้องอ่าน assertion message แล้วรู้ทันทีว่าธุรกิจผิดตรงไหน

---
### เคสที่เพิ่มจากรอบแก้บั๊ก multi-team audit (ยังไม่มีเทสต์ — ต้องใช้ DB)

| รหัส | เคส | หมายเหตุ |
| --- | --- | --- |
| DOC-I-11 | void ใบที่ 2 ของ multi-doc payment | ยอดธนาคาร/PaidAmount/JE กลับครบ; ยกเลิกใบเดียวในกลุ่ม → ต้อง block |
| DOC-I-12 | restore ใบ voided แล้วพยายามแก้ยอด/วันที่ | block ตาม §86/4 (แก้ได้เฉพาะหมายเหตุ) |
| DOC-I-13 | void → restore → approve ใหม่ | ต้อง post JE ชุดใหม่ (guard ไม่นับ reversal) และ stock ไม่ตัดซ้ำ |
| DOC-I-14 | ApplyDepositToInvoice 2 requests พร้อมกัน | สำเร็จ 1 ล้มเหลว 1, เลข JV ไม่ซ้ำ |
| WHT-I-04 | จ่าย 40%/60% สองเดือน | 50 ทวิ 2 ใบ ยอดตามงวดจริง; Σ = ยอดหักทั้งเอกสาร |
| WHT-I-05 | void PV ที่หักภาษี | บรรทัด ภ.ง.ด. ถูกติ๊กออก + ยอดหัวรายงาน recalc |
| TAX-I-08 | export ภ.พ.30 งวดที่มีรายงานบันทึกไว้แล้ว | CSV ต้องมีรายการครบ ไม่ว่าง |
| TAX-I-09 | ใบกำกับซื้อยกมาข้ามปี (§82/3) | คอลัมน์วันที่ในไฟล์ยื่นเป็นปี พ.ศ. ของใบจริง |
| BNK-I-02 | ส่ง AllocatedAmount เกินยอดจริง | 400 พร้อมข้อความชัดเจน |
| JE-I-04 | ค่าเสื่อม/ตีราคาในงวดที่ปิดแล้ว | block + JE มี FiscalPeriodId |
| FE-S-05 | ตั้งภาษาเอกสาร = อังกฤษ แล้วออกใบกำกับ | หัวเป็นสองภาษา, ฟอร์มราชการยังเป็นไทย |

---
Last updated: 2026-07-31 — สร้างจาก audit session; อัปเดตหลังแก้เฟส 1-3 + ฟีเจอร์ภาษาเอกสาร
