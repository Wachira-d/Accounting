# CLAUDE.md — กฎสำหรับ AI agent ที่พัฒนาโปรเจกต์นี้

เอกสารนี้ Claude Code (และ AI agent อื่น) อ่านอัตโนมัติทุก session
**ต้องทำตามทุกข้อ ไม่มีข้อยกเว้น** — เป็น policy ของเจ้าของโปรเจกต์

---

## 🚨 กฎเหล็ก #1 — Distillation Mandate

> **ห้ามเรียก AI ภายนอกแบบ "ยิงทิ้ง" เด็ดขาด**

ทุกครั้งที่เขียนโค้ดที่เรียก LLM / AI provider (DeepSeek, Claude, OpenAI,
Gemini, local Llama, ฯลฯ) **ต้องมี distillation loop ครบ 4 ขั้น** เสมอ

```
┌─────────────────────────────────────────────────────────────┐
│  1. STUDENT FIRST   → ถาม local model ก่อน                  │
│  2. TEACHER FALLBACK → เรียก AI เฉพาะตอน student ไม่มั่นใจ │
│  3. CAPTURE         → บันทึกคำตอบ AI เป็น feedback row     │
│  4. DISTILL         → งานกลางคืน retrain local model       │
└─────────────────────────────────────────────────────────────┘
```

### ทำไม

- **ต้นทุน** — token DeepSeek/Claude คิดเงินทุก call บริษัทไม่อยากจ่าย
  ทุกเอกสารทุกเดือนตลอดไป
- **Sovereignty** — local model เป็น *ทรัพย์สิน* ของบริษัท ฉลาดขึ้นเรื่อย ๆ
  ตามการใช้งาน ไม่ผูกกับ vendor ภายนอก
- **Latency** — เรียก AI ภายนอก 1-5 วินาที, local model หลัก ms
- **Privacy** — ข้อมูลลูกค้าไม่ควรไหลออกไป provider ทุกครั้ง

### 🛡️ Local-First Sovereignty — local model ต้องทดแทน AI ได้ 100%

> **หลักการสูงสุด:** AI ภายนอกเป็น "ครูพิเศษ" ที่ช่วยสอน — ไม่ใช่ "อวัยวะ
> สำคัญ" ที่ระบบขาดไม่ได้ ทุก feature ที่โยนข้อมูลไปให้ AI คิด **ต้องนำคำตอบ
> กลับมาสอน local model จนถึงจุดที่ local ทำงานแทนได้เต็ม 100%** เมื่อ AI
> ล่ม / ถูกปิด / เกินงบ / โดน rate-limit / เครือข่ายขาด ระบบต้องทำงานต่อ
> **ได้ครบทุกฟังก์ชัน** เหมือน AI ยังอยู่ — ผู้ใช้ไม่ควรรู้สึกถึงความต่าง

นี่คือเหตุผลที่ขั้น CAPTURE + DISTILL **ห้ามข้าม**: ทุก call ที่ไปหา AI คือ
โอกาสเก็บ training data ถ้าไม่เก็บ = จ่าย token ฟรีโดยไม่ได้อะไรกลับมา และ
local model จะไม่มีวันโตพอจะยืนด้วยตัวเอง

**ข้อบังคับเชิงสถาปัตยกรรม (ทุกข้อเป็น hard requirement):**

1. **ไม่มี hard dependency บน AI** — โค้ดทุกเส้นทางต้องมี local path ที่ให้
   คำตอบใช้งานได้จริง (ไม่ใช่ throw / return null / ปล่อยฟอร์มว่าง) เมื่อ
   provider ไม่ตอบสนอง การปิด AI ทั้งระบบต้อง **ไม่ทำให้ feature ใดพัง**
2. **Feature parity** — ทุก `AiFeatureKey` ที่เรียก AI ต้องมี
   `ILocalDistillationModel` register แล้ว 1 ตัวเสมอ ห้ามมี feature ที่เรียก
   AI ได้แต่ไม่มี student รองรับ (= สร้าง dependency ที่ถอดไม่ได้) ถ้า output
   เป็น **single answer** (classification/suggestion) ไม่ต้องเขียน model ใหม่
   — register `GenericFeedbackDistillationModel(featureKey, …)` ใน `Program.cs`
   ก็พอ (เรียนรู้ exact-input + company majority fallback ให้อัตโนมัติ) เขียน
   bespoke model เฉพาะตอน output เป็น structured/bulk/free-form essay
   (เช่น OcrFullReview, ImportColumnMatch, BulkBankStatementMatch)
3. **Cold-start ต้องไม่ว่างเปล่า** — tenant ใหม่ที่ยังไม่มี feedback ของ
   ตัวเอง local model ต้องตอบได้จาก seed/baseline (`IDistillationCorpusSeeder`,
   `SystemOcr*` mappings, rule resolver) — ไม่ใช่รอจน AI สอนถึงจะทำงาน
4. **Graceful degradation เงียบ** — provider ดับ/เกินงบ/timeout → สลับมา
   local ทันที ห้าม error ขึ้นหา user, ห้าม log spam, ห้าม block UI
5. **Kill-switch ต้องผ่าน** — โค้ดต้องทำงานถูกต้องเมื่อ **ปิด provider ทุก
   ตัว** (`AiProviderConfig.IsActive = false` ทั้งหมด) ก่อน commit ให้คิด
   เสมอว่า "ถ้า AI หายไปวันนี้ feature นี้ยังทำงานครบไหม" ถ้าตอบไม่ได้ = ยัง
   ไม่ผ่าน
6. **วัดความพร้อม** — ใช้ `<Feature>UsedAi` ที่ลดลงเรื่อย ๆ เป็นตัวชี้วัด:
   ยิ่ง local โตเท่าไร อัตราการเรียก AI ยิ่งต่ำ เป้าหมายคือ short-circuit
   ที่ local ≥ 0.85 confidence จนแทบไม่ต้องแตะ AI ในงานประจำ

**Litmus test ก่อน commit ทุก feature ที่แตะ AI:**
> "ถ้าตอนนี้ตัด DeepSeek/Claude ออกจากระบบถาวร — feature นี้ยังให้ผลลัพธ์ที่
> ใช้งานได้ครบ 100% หรือไม่?" ถ้า **ไม่** แปลว่าโค้ดยังพึ่ง AI เป็นไม้ค้ำ
> ไม่ใช่ครู — ต้องแก้ให้ local ยืนเองได้ก่อน

### Checklist — ต้องผ่านทุกข้อก่อน commit

- [ ] ใช้ `IAiOrchestrator.AskAsync(AiRequest)` (ไม่เรียก `IAiProvider` หรือ
      HTTP client ตรง) — orchestrator จัด student-first routing ให้
- [ ] สร้างหรือใช้ `ILocalDistillationModel` ที่ตรงกับ `AiFeatureKey` —
      ต้องมี `PredictAsync` (student) + `LoadFromFeedbackAsync` (retrain)
- [ ] register distillation model ใน `Program.cs` (`AddSingleton<ILocalDistillationModel, ...>`)
- [ ] เพิ่ม enum value ใน `AiFeatureKey` (`Models/Enums/AllEnums.cs`)
- [ ] เก็บ `FeedbackId` ที่ orchestrator คืน → ใส่ใน entity เป็น
      `<Feature>AiFeedbackId` ให้ค้นเจอ row ตอนผู้ใช้แก้
- [ ] ตอนผู้ใช้ **ยืนยัน/แก้** คำตอบ → เรียก
      `IAiFeedbackRecorder.RecordUserChoiceAsync(feedbackId, chosenAnswer,
      acceptedAi)` — มิฉะนั้น `AiFeedbackTrainingJob` จะ mine ไม่ได้
- [ ] เพิ่ม **anti-hallucination guard** — validate คำตอบ AI กับข้อมูล
      จริง (CoA, contact list, candidate set) ก่อน apply
- [ ] เพิ่ม `bool <Feature>UsedAi` ใน DTO → UI ติดป้ายซื่อสัตย์
      ("🤖 AI แนะนำ" เฉพาะตอนเรียกจริง, ไม่งั้น "⚙️ ระบบแนะนำ")
- [ ] fallback graceful: provider ดับ/เกินงบ/timeout → ใช้ local
      ต่อไปได้เงียบ ๆ ห้าม throw ขึ้นมาหา user
- [ ] **Kill-switch test** — ปิด provider ทุกตัว (`IsActive=false`) แล้ว
      feature ยังทำงานครบ 100% ผ่าน local path (ดู "🛡️ Local-First
      Sovereignty") ถ้ายังพึ่ง AI เป็นไม้ค้ำ = ยังไม่ผ่าน
- [ ] cold-start: tenant ใหม่ (ยังไม่มี feedback) local model ตอบได้จาก
      seed/baseline ไม่ใช่รอ AI สอนก่อน

### Template ที่ลอกได้

| ส่วน | ตัวอย่างที่มีในโปรเจกต์ | ไฟล์ |
| --- | --- | --- |
| Augmenter pattern | `SuggestGlAccountAsync` | `Services/Ai/OcrAiAugmenter.cs` |
| Distillation model (bespoke) | `GlAccountDistillationModel` | `Services/Ai/Distillation/GlAccountDistillationModel.cs` |
| Distillation model (generic) | `GenericFeedbackDistillationModel` — ใช้กับ feature single-answer ที่ไม่ต้องเขียน model เอง register 1 instance/feature ใน `Program.cs` | `Services/Ai/Distillation/GenericFeedbackDistillationModel.cs` |
| Wiring เข้า pipeline | OCR GL-account classification block | `Services/Implementations/OcrService.cs` |
| ปิด loop จาก user edit | `SubmitCorrectionAsync` → `RecordUserChoiceAsync` | `Services/Implementations/OcrService.cs` |
| Routing config | Hybrid default 0.85 short-circuit | `Services/Ai/AiFeatureRoutingResolver.cs` |
| Budget guard | DailyCallCap + MonthlyBudgetUsd | `Services/Ai/AiBudgetGuard.cs` |

### Anti-pattern — ห้ามทำ

```csharp
// ❌ ห้าม: ยิง AI ตรง ๆ ไม่มี distillation
var http = _factory.CreateClient();
var resp = await http.PostAsync("https://api.deepseek.com/...", body);
return resp;

// ❌ ห้าม: เรียก provider ข้าม orchestrator
var raw = await _deepseek.CompleteAsync(req, cfg, ct);

// ❌ ห้าม: ใช้คำตอบ AI โดยไม่ validate
extractedData.AccountCode = aiResponse.Answer;  // hallucination = ลงบัญชีผิด

// ❌ ห้าม: ไม่บันทึก feedback
var resp = await _orch.AskAsync(req);
return resp.Answer;  // user แก้แล้วระบบไม่รู้ → ไม่เคยฉลาดขึ้น
```

```csharp
// ✅ ถูก
var resp = await _orchestrator.AskAsync(new AiRequest(
    CompanyId: cid, FeatureKey: AiFeatureKey.MyFeature,
    SystemPrompt: sys, UserPromptJson: payload, ...), ct);

entity.MyFeatureAiFeedbackId = resp.FeedbackId;
entity.MyFeatureUsedAi = resp.UsedAi;

if (resp.UsedAi && (resp.Confidence ?? 0) >= 0.70m
    && candidateSet.Contains(resp.PrimaryAnswer))   // ⬅ guard
{
    entity.Field = resp.PrimaryAnswer;
}

// ตอน user ยืนยัน/แก้:
await _recorder.RecordUserChoiceAsync(
    entity.MyFeatureAiFeedbackId.Value, userChoice,
    acceptedAi: userChoice == aiOriginalAnswer);
```

---

---

## 🚨 กฎเหล็ก #2 — Thai Legal & Regulatory Compliance (Hard Requirements)

> **ทุก feature ที่แตะ "เอกสารบัญชี/ภาษี/พนักงาน/ข้อมูลส่วนบุคคล" ต้องผ่าน
> checklist นี้ก่อน commit** — ไม่ใช่ best-effort เป็น **กฎหมาย** ผิด = ลูกค้า
> โดนสรรพากร/DBD/PDPC ปรับ → บริษัทรับผิดร่วมในฐานะผู้ให้บริการ

### A. ใบกำกับภาษี (ป.รัษฎากร §86/4, §86/6, §86/9, §86/10)

**§86/4 ใบกำกับภาษีเต็มรูป — บังคับ 8 รายการ:**
- [ ] คำว่า **"ใบกำกับภาษี"** บน header (literal, bold) — block save ถ้าไม่มี
- [ ] `SellerName`, `SellerAddress`, `SellerTaxId` required;
  TaxId regex `^[0-9]{13}$` + **mod-11 checksum**
- [ ] `BuyerName`, `BuyerAddress` required; `BuyerTaxId` required เมื่อ buyer
  เป็น VAT registrant (regex + checksum เหมือนกัน)
- [ ] `SellerBranchCode`, `BuyerBranchCode` required, regex `^[0-9]{5}$` —
  `00000` render "สำนักงานใหญ่", อื่น ๆ render "สาขาที่ {code}"
  (ประกาศอธิบดีฯ ฉบับที่ 199 ลว. 26 ธ.ค. 2556)
- [ ] `InvoiceNo` unique per `CompanyId+BranchCode+TaxYear`, **gap-free running**
- [ ] ทุก line: `Description` ≥ 1 ตัวอักษร, `Quantity > 0`, `UnitPrice ≥ 0`,
  `LineAmount = round(Qty × UnitPrice, 2)`; block save ถ้า `items.Count == 0`
- [ ] PDF/HTML ต้อง render `Subtotal`, `VatAmount`, `GrandTotal` **คนละบรรทัด**;
  per-line VAT rate รองรับ mixed
- [ ] `IssueDate` required, store ค.ศ. (UTC+7), render **พ.ศ.** บนพิมพ์;
  ห้าม backdate/post-date เกินกฎ
- [ ] **ห้ามแก้ไขย้อนหลัง** — เลขที่ออกแล้วต้องออก "ใบยกเลิก" + ใบใหม่
  (`CancelledByDocumentId`, `ReplacedByDocumentId`)

**§86/6 ใบกำกับภาษีอย่างย่อ:**
- [ ] เปิดใช้เฉพาะ `Company.IsRetailApproved=true` + `PhoR06ApprovedDate != null`
  (อนุมัติ ภ.พ.06 แล้ว); ตัวแทนห้ามออก
- [ ] header "ใบกำกับภาษีอย่างย่อ" + `PriceInclusiveVat=true`,
  `VatAmount = round(GrandTotal × 7/107, 2)`
- [ ] **ห้ามใช้เป็นภาษีซื้อ** (§82/5(2)) — block insert ลง `PurchaseVatReport`,
  flag `InputVatBlocked=true`

**§86/9 ใบเพิ่มหนี้ / §86/10 ใบลดหนี้:**
- [ ] `Reason` enum required (closed list); `OriginalTaxInvoiceId` FK required +
  same tenant + same counterparty
- [ ] header "ใบเพิ่มหนี้" / "ใบลดหนี้"; ต้อง ref **เลขที่+วันที่ใบเดิม + ผลต่าง + VAT ผลต่าง**
- [ ] **`SUM(creditNotes WHERE OriginalInvoiceId=X) ≤ OriginalInvoice.Amount`** — block save
- [ ] ห้ามออกถ้าใบเดิม `status = Voided`
- [ ] `IssueDate` ต้องอยู่ในเดือนภาษีเดียวกับ event date หรือเดือนถัดไป
  (เกิน → require `LateReason`)

### B. ภาษีซื้อต้องห้าม (§82/5) + 6-month window (§82/3)

**Auto set `InputVatClaimable=false` + log `RuleCode` + `LegalReference`:**
- [ ] **§82/5(1)** ใบกำกับขาดรายการ §86/4 → `Claimable=false`, reason=`NoInvoice`
- [ ] **§82/5(2)** ใบกำกับอย่างย่อ → block claim (buyer)
- [ ] **§82/5(3)** ไม่เกี่ยวกับกิจการ — `BusinessPurpose` required, flag `personal_use` → block
- [ ] **§82/5(4)** ค่ารับรอง / ของขวัญ → auto `Claimable=false` (รวม §65 ตรี(4))
- [ ] **§82/5(5)** ใบกำกับออกโดยผู้ไม่มีสิทธิ (ผู้ไม่จด VAT/ถูกเพิกถอน) → block
- [ ] **§82/5(6)** รถยนต์นั่ง ≤ 10 ที่นั่ง + ค่าน้ำมัน/ซ่อม/เช่าซื้อ → block
  (ยกเว้น `IsVehicleDealer=true`) — ประกาศอธิบดีฯ ฉบับที่ 42
- [ ] **§82/3** 6-month window — `(FilingMonth − InvoiceMonth) > 6` → block;
  1–6 เดือนช้า → require `LateReason`; expired → auto reclassify เป็น expense

### C. รายงานภาษีซื้อ/ขาย (§87) + Tax Point (§78, §78/1, §78/2)

- [ ] รายงานภาษีขาย: ลงภายใน **3 วันทำการ** นับจากวันที่ในใบกำกับ; ภาษีซื้อ:
  3 วันทำการนับจากวันที่ได้รับ
- [ ] ลำดับ **chronological** ห้ามสลับ ห้ามเว้นบรรทัด ห้ามแก้โดยไม่ขีดฆ่า
- [ ] รายงานสินค้าและวัตถุดิบ §87(3) เปิดเฉพาะ `BusinessType ∈ {Trading, Manufacturing}`
- [ ] **Retention 5 ปี** (§87/3) นับจากวันยื่นแบบ — `retainUntil = max(filingDate, reportDate) + 5y`,
  ห้ามลบจริงในช่วงนี้
- [ ] **Tax Point** — ขายสินค้า §78: `taxPointDate = MIN(deliveryDate, ownershipTransferDate, paymentDate, invoiceDate)`;
  บริการ §78/1: `MIN(paymentReceivedDate, invoiceDate, serviceUsedDate)`;
  นำเข้า §78/2: `customsDutyPaidDate`
- [ ] VAT period = month ของ `taxPointDate` (ไม่ใช่ `invoiceDate`) → ใช้ map เข้า ภ.พ.30

### D. VAT — อัตราและสถานะผู้ประกอบการ

- [ ] **§81/1 Threshold 1.8 ล.บาท/ปี** — `if annualRevenue > 1,800,000 AND !vatRegistered`
  → blocking prompt "ต้องจด VAT ภายใน 30 วัน (§85/1)"
- [ ] enum `VatStatus = {Standard7, ZeroRated, Exempt}`:
  - **Standard7** (7%) — ออกใบกำกับ, claim input ได้
  - **§80/1 ZeroRated** (0%) — ส่งออก/บริการใช้ต่างประเทศ — ออกใบกำกับ rate=0, claim input ได้
  - **§81 Exempt** — สินค้าเกษตรไม่แปรรูป/การแพทย์/การศึกษา — **ห้ามออกใบกำกับ**, claim input ไม่ได้ (ลง cost)
- [ ] รายงานภาษีขาย: **แยก column** 7% / 0% / ยกเว้น

### E. หัก ณ ที่จ่าย (ท.ป.4/2528)

**อัตรา (รวม §3 เตรส):**
| ประเภทเงินได้ | บุคคล | นิติบุคคลไทย | แบบยื่น |
| --- | --- | --- | --- |
| เงินเดือน ม.40(1) | ขั้นบันได | — | **ภงด.1** |
| ค่าบริการ/รับเหมา ม.40(2)(7)(8) | 3% | 3% | **ภงด.3 / 53** |
| ค่าโฆษณา | 2% | 2% | ภงด.3 / 53 |
| ค่าขนส่ง (ไม่ใช่สาธารณะ) | 1% | 1% | ภงด.3 / 53 |
| ค่าเช่าอสังหา ม.40(5)(ก) | 5% | 5% | ภงด.3 / 53 |
| วิชาชีพอิสระ ม.40(6) | 3% | 3% | ภงด.3 / 53 |
| ดอกเบี้ย ม.40(4)(ก) | 15% | 1% | ภงด.2 / 53 |
| เงินปันผล | 10% | 10% | ภงด.2 / 53 |
| ค่าสิทธิ ม.40(3) | 3% | 3% | ภงด.3 / 53 |
| จ่าย ตปท. ม.70 | — | 15% (ทั่วไป) / 10% (ปันผล) | **ภงด.54** |
| VAT แทน ตปท. ม.83/6 | — | 7% | **ภ.พ.36** |

**ระบบต้องบังคับ:**
- [ ] **Threshold 1,000 บาท** — ไม่หักถ้ายอดสัญญา < 1,000; แต่ถ้ารวมทั้งสัญญา ≥ 1,000 ต้องหักทุกงวด
- [ ] **หนังสือรับรอง 50 ทวิ** — auto-issue 2 ฉบับ ("สำหรับยื่นแบบ" + "เก็บไว้") ในวันจ่าย
- [ ] **กำหนดยื่น**: กระดาษ = วันที่ **7** ของเดือนถัดไป; e-Filing = วันที่ **15** (ขยายถึง 31 ม.ค. 2570);
  ถ้าตรงเสาร์/อาทิตย์/วันหยุด → next business day
- [ ] **DTA override** — เก็บตาราง bilateral treaty rate, ใช้แทนอัตรา default เมื่อ
  payee country มี DTA และเอกสาร TH8/CoR ครบ

### F. e-Tax Invoice / e-Receipt (ETDA ขมธอ.3-2560)

- [ ] **XML schema UBL 2.1 + ETDA profile** field: `DocumentTypeCode`
  (T01–T04), `ID`, `IssueDateTime` (ISO 8601 +07:00), `Seller.TaxID`, `Seller.BranchID`,
  `Buyer.TaxID`, `Buyer.BranchID`, `LineItem[]`, `VATAmount`, `TotalAmount`,
  `CurrencyCode=THB`, `ReferenceID` (กรณีอ้างใบเดิม)
- [ ] **Digital signature** XAdES-BES, RSA-SHA256, key ≥ 2048-bit, CA ที่ ETDA รับรอง
  → field `CertificateSerialNumber`, `SignatureValue`, `SignedAt`
- [ ] **Submission cron**: ส่งทุกฉบับของเดือนก่อน → RD portal **ภายในวันที่ 15 ของเดือนถัดไป**;
  field `SubmittedToRdAt`, retry queue
- [ ] **e-Tax Invoice by Email (SME ≤ 30 ล.บาท/ปี)**: PDF/A-3 + embedded XML →
  cc `csemail@etax.teda.th` → เก็บ `EtdaTimestampToken`

### G. TFRS for NPAEs (ฉบับปรับปรุง 2565, มีผล 1 ม.ค. 2566)

- [ ] **บทที่ 6 Revenue** — ขาย: `RevenueRecognizedAt` แยกจาก `InvoiceDate`;
  บริการ: percentage-of-completion → `PercentComplete` + milestone trigger
- [ ] **บทที่ 8 Inventory** — enum `InventoryCostMethod ∈ {SpecificIdentification, FIFO, WeightedAverage}`;
  **ห้าม LIFO** (validation reject); วัด LCNRV → ตั้งค่าเผื่อ `InventoryWriteDownAllowance`
- [ ] **บทที่ 9 AR** — table `ArAgingBucket` (0-30 / 31-60 / 61-90 / 91-180 / 180+) +
  `LossRatePercent` ต่อ tenant; nightly job คำนวณ `AllowanceForDoubtfulAccounts`
- [ ] **บทที่ 10 PPE** — `UsefulLifeYears` + `DepreciationMethod ∈ {StraightLine, DecliningBalance, UnitsOfProduction}`;
  ที่ดิน → `DepreciationMethod = None` (validation); ทบทวนสิ้นปี → `UsefulLifeReviewedAt`
- [ ] **บทที่ 19 FX** — spot rate วันทำรายการ, สิ้นงวด revalue monetary items ด้วย closing rate →
  field `JournalLine.ForeignCurrency`, `OriginalRate`, `ClosingRate`, `FxGainLossAccountId`

### H. พ.ร.บ.การบัญชี พ.ศ. 2543 + DBD

- [ ] **ม.7** `BookkeeperCpdNumber` + `BookkeeperName` required ก่อน finalize งบ
- [ ] **ม.10** เก็บเอกสาร **5 ปี** (ขยายได้ถึง 7 ปี) — `RetentionUntil = AccountingPeriodEndDate + 5y`
- [ ] **ม.11** รอบบัญชี enum `FiscalPeriodType ∈ {Normal, FirstYear, FinalYear, ChangeApproved}`;
  Normal ต้อง = 12 เดือนพอดี
- [ ] **DBD submission** — บริษัท: ภายใน 1 เดือนจาก AGM approve **และ** ≤ 5 เดือนจากสิ้นรอบ;
  alert เมื่อ `(ApprovedAt + 30d)` หรือ `(FiscalYearEnd + 150d)` ใกล้ถึง
- [ ] **XBRL export** ตาม taxonomy DBD — map ทุก GL account → XBRL concept

### I. ภงด.50 / ภงด.51 (ภาษีนิติบุคคล)

- [ ] **ภงด.50** ภายใน **150 วัน** จากสิ้นรอบ; e-Filing +8 วัน → flag `EFilingExtension`
- [ ] **ภงด.51** ภายใน 2 เดือนหลังครบ 6 เดือน; รอบ < 12 เดือน → `IsExemptFromPnd51=true`
- [ ] **Penalty calculator** — ประมาณการต่ำเกิน 25% → เงินเพิ่ม 20% ของส่วนต่าง
- [ ] **SME rate table** (ทุน ≤ 5 ล. + รายได้ ≤ 30 ล.): 0% (≤300k), 15% (300k–3M), 20% (>3M);
  ทั่วไป 20% → `CitRateBracket(fiscalYear, taxpayerType, min, max, rate)`

### J. PDPA (พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล 2562)

- [ ] **ม.26 Sensitive data** — column ต้อง **AES-256 encrypt-at-rest** แยก key (KMS);
  เลขบัตรประชาชน 13 หลัก display mask `1-XXXX-XXXXX-XX-3`, full value เฉพาะ role `pii:view`
- [ ] **DSR endpoints บังคับ**: `GET /dsr/access`, `POST /dsr/rectify`,
  `DELETE /dsr/erase` (cascade ยกเว้นที่ พ.ร.บ.บัญชี/สรรพากร hold), `GET /dsr/portability`;
  SLA **30 วัน** → `DsrRequest.DueAt = CreatedAt + 30d`
- [ ] **ม.37** TLS 1.2+, RBAC, `PiiAccessLog(userId, dataSubjectId, fieldName, op, purpose, at)` ≥ 1 ปี
- [ ] **ม.39 RoPA** — `ProcessingActivity(purpose, legalBasis, dataCategories, retentionPeriod, recipients)`
- [ ] **Retention by purpose** — nightly purge/anonymize เมื่อหมดอายุ;
  ถ้า `legal_hold=true` (พ.ร.บ.บัญชี/สรรพากร) → คงไว้ (**MAX(retention)** rule)
- [ ] **Consent** — `ConsentRecord(subjectId, purposeId, grantedAt, withdrawnAt, version, evidenceHash)`
- [ ] **ม.37(4) Breach** — แจ้ง PDPC ภายใน **72 ชม.** → incident workflow + auto-draft notification

### K. ประกันสังคม (พ.ร.บ.ประกันสังคม 2533)

- [ ] **ม.33** สมทบ 5% ลูกจ้าง + 5% นายจ้าง; floor 1,650, **cap 15,000/เดือน** →
  `min(max(salary, 1650), 15000) * 0.05` (เพดาน 750/ฝ่าย/เดือน)
- [ ] **กองทุนเงินทดแทน** — นายจ้างฝ่ายเดียว 0.2%–1.0% (cap 240,000/คน/ปี) ตามประเภทกิจการ;
  cron มี.ค. ออก กท.20ก
- [ ] **สปส.1-10** ยื่น + จ่ายภายในวันที่ **15** ของเดือนถัดไป; late fee 2%/เดือน auto-calc
- [ ] **สปส.1-03** ขึ้นทะเบียนลูกจ้างใหม่ภายใน 30 วัน (trigger ตอน insert Employee)
- [ ] **สปส.6-09** แจ้งออกภายในวันที่ 15 ของเดือนถัดจากเดือนที่ลาออก

### L. §65 ตรี — รายจ่ายต้องห้าม (Non-Deductible Expenses)

> ทุก expense save → ระบบเรียก `Section65TerValidator.Evaluate(expense)` คืน
> `{nonDeductible, addBackAmount, ruleCode}` → ผูกกับ ภ.ง.ด.50 worksheet
> **"บวกกลับ"** อัตโนมัติ ห้ามให้ user ลืม

- [ ] **(1) เงินสำรอง** — block ยกเว้น `provident_fund_pvd`, `life_insurance_reserve` (≤65% เบี้ย), `bad_debt_reserve_bank_insurance`
- [ ] **(2) เงินกองทุน** — block ยกเว้น PVD
- [ ] **(3) รายจ่ายส่วนตัว/เสน่หา/การกุศลทั่วไป** — default `nonDeductible=true` + warn
- [ ] **(4) ค่ารับรอง** — `cap = MAX(0.003×revenue, 0.003×paidUpCapital)` แต่ไม่เกิน
  **10,000,000 บาท/รอบ** (กฎกระทรวง 143); ส่วนเกิน mark `nonDeductible`;
  require ใบเสร็จ + ผู้อนุมัติระดับกรรมการ
- [ ] **(5) Capex (อายุใช้งาน > 1 ปี หรือ amount ≥ threshold)** — block save as expense,
  force capitalize + คิด depreciation (พ.ร.ฎ.145); รวมต่อเติม/ขยาย/ทำให้ดีขึ้น
- [ ] **(6) เบี้ยปรับ/เงินเพิ่ม/ค่าปรับอาญา** — auto `nonDeductible=true`, **no override**
- [ ] **(6 ทวิ) ภาษีเงินได้นิติบุคคล** — `glAccount LIKE 'CIT%'` → auto `nonDeductible=true`
- [ ] **(7) เงินบริจาค** — ทั่วไป cap 2% ของกำไรสุทธิ; การศึกษา/กีฬา +2% เพิ่ม;
  ส่วนเกิน mark `nonDeductible`; คำนวณตอนปิดรอบ
- [ ] **(8) รายจ่ายไม่ได้จ่ายจริง** — `paymentEvidence is null` && `!accrualReversed` → block
- [ ] **(9) ไม่มี source document** — `sourceDocument is null` && `amount > 0` → block save
- [ ] **(10) รายจ่ายรอบปีก่อน** — `expenseDate.fiscalYear < currentFiscalYear` → warn + require justification
- [ ] **(11)(18) ไม่ระบุผู้รับเงิน** — `payeeName empty OR payeeTaxId empty` && `amount > 0` → **hard block**
- [ ] **(12) ดอกเบี้ยของทุน/สำรองตนเอง** — auto `nonDeductible=true`
- [ ] **(13) เช่าทรัพย์สินของบริษัทเอง** — block
- [ ] **(14) ไม่เกี่ยวกิจการ** — checkbox `relatedToBusiness=false` → auto `nonDeductible=true`
- [ ] **(15) Transfer pricing** — `unitPrice > marketPrice × 1.2` (related party) → warn
- [ ] **(19) นอกประเทศไม่เชื่อมไทย** — `expenseLocation='offshore'` && `!linkedToThaiOperation` → `nonDeductible=true`

### M. Cross-cutting invariants (ทุก feature ต้องเป็นจริง)

- [ ] **Tenant isolation** `CompanyId == @companyId` ในทุก query
- [ ] **Retention superset** — ถ้า field อยู่ใต้หลายกฎหมาย ใช้ `MAX(retention)` + mark `legal_hold`
- [ ] **Audit log append-only** `AuditLog(actorId, entityType, entityId, before, after, at, ip, reason)`
  — ห้าม UPDATE/DELETE; ใช้ hash chain (PrevHash + RowHash SHA-256) เป็น tamper-evident
- [ ] **Time zone** เก็บ `timestamptz` UTC, แสดง Asia/Bangkok (+07:00); พ.ศ. เฉพาะแบบยื่นภาษี/รายงานทางการ
- [ ] **เลขเอกสาร** ออกตอน Approve เท่านั้น (Draft `DRAFT-{guid}`), gap-free ตาม §86/4
- [ ] **Legal reference logging** — ทุก validation rule log `RuleCode` + `LegalReference`
  (เช่น `RD-82/5(6)`) ลง audit; UI tooltip อ้างมาตรา

### Litmus test ก่อน commit (compliance)

> "ถ้าสรรพากร/DBD/PDPC ตรวจวันนี้ — feature ใหม่ของฉันจะทำให้ลูกค้าโดนปรับไหม?"
> ถ้าตอบไม่ได้ = ยังไม่ผ่าน ต้องเปิด research/ถามนักบัญชีก่อน

---

## 🚨 กฎเหล็ก #3 — OCR Auto-Fill (User Confirms Only)

> **ถ้า OCR เข้ามา ระบบต้องกรอกข้อมูลให้ครบทุก field ที่บังคับ
> ผู้ใช้แค่ "ยืนยัน" — ห้ามให้ผู้ใช้กรอกซ้ำ**

OCR ไม่ใช่ "ตัวช่วยพิมพ์" แต่เป็น "ผู้กรอกอัตโนมัติเต็มรูปแบบ" target UX =
**1-click approve** ไม่ใช่ **fill-in-the-blank**

### ข้อบังคับ

1. **ครบทุก field §86/4** — เมื่อ OCR เสร็จ DTO ที่ส่งไป UI ต้องมี:
   `SellerName`, `SellerTaxId`, `SellerBranchCode`, `SellerAddress`,
   `BuyerName`, `BuyerTaxId`, `BuyerBranchCode`, `BuyerAddress`,
   `InvoiceNo`, `InvoiceDate`, ทุก `Line.Description/Qty/UnitPrice/VatRate`,
   `Subtotal`, `VatAmount`, `GrandTotal`, `WhtRate` (ถ้ามี), `GlAccountCode` (รายบรรทัด),
   `PaymentTerms` — **ห้ามมี null/empty** ใน field ที่เอกสารต้องมีตามกฎหมาย

2. **Fallback chain เติมให้เต็ม** — ลำดับการเติมเมื่อ OCR confidence ต่ำ
   (**ตรงกับโค้ดจริงใน `OcrService.ProcessScanAsync`** — ปรับให้ตรงเมื่อ
   2026-08-28 หลังพบว่าข้อ 1 เดิมเขียนถึงสิ่งที่ยังไม่มีในระบบ):
   1. **OCR engine ตามลำดับ**: Azure Document Intelligence → local python
      service → `EmbeddedTesseractOcrService` (ตัวสุดท้ายคืน**ข้อความล้วน**
      ไม่มีโครงตาราง)
   2. **สกัดจากข้อความ** — `SmartFieldExtractor.Enrich` + `EnrichFromRawText`
      (รหัสสาขา §86/4, เครดิตเทอม, ส่วนลด, หน่วยนับ, จำนวนเงินตัวอักษร ·
      รอบ 190: ป้ายสาขาท้ายชื่อ `OcrPartyName.StripBranchSuffix` · ประโยคประกาศสาขาผู้ออกใบ
      `OcrIssuerBranch` · เล่มที่/เลขที่ `OcrBookSerial` · ที่อยู่ผู้ซื้อ `OcrBuyerAddressReader`
      + invariant "ที่อยู่ผู้ขายที่เป็นบล็อกผู้ซื้อถูกล้าง") · แล้ว**ตรวจวันที่กับป้ายบนกระดาษ**
      (`OcrDateReader.CrossCheck`) ทุก engine ก่อนด่านคณิต · ชื่อฝั่งเราที่เป็นรหัส/ว่าง →
      ชื่อบริษัทจากทะเบียน (`OcrPartyResolver.FillOurName`) · รอบ 192 **ยึดยอดรวมทั้งสิ้นก่อน**
      (`OcrTotalAnchor` — นับชั้นหลักฐานอิสระ ไม่เชื่อป้าย "TOTAL" ตามตัว) แล้ว**แยกองค์ประกอบให้รวมได้ยอดนั้น**
      (`OcrTotalDecomposer` · ตารางรหัส ภ.พ. `OcrLineVatMarks.ReadGroups`)
   3. **แพตเทิร์นที่เรียนไว้** — `DocumentZoneAnalyzer.ApplyLearnedPatternsTo`
      อ่าน `OcrLearnedPatterns` ของผู้ขายรายนั้น (เติมเฉพาะช่องที่ยังว่าง)
   4. **วิเคราะห์โซน** — `DocumentZoneAnalyzer.Analyze` ทำงานเมื่อ pipeline
      หลักไม่ได้ทั้งชื่อผู้ขายและยอดรวม
   5. **Local distillation model** (`OcrFullReviewDistillationModel` +
      `GlAccountDistillationModel` ฯลฯ — ตามกฎเหล็ก #1)
   6. **ทะเบียนราชการ + ประวัติผู้ขาย** (แก้ doc 2026-09-18 — ข้อเดิมเขียนว่า
      "autofill SellerName/Address/BranchCode จาก `Contact` ล่าสุด" ซึ่ง**ไม่มีใน
      โค้ด**: บล็อก `[Enrich]` ใน `OcrService` ไหล**ทางเดียว** คือเอาค่าจากสแกน
      ไปเติมช่องที่ว่างของ `Contact` ไม่เคยอ่านชื่อ Contact กลับมาทับ `VendorName`)
      ของจริงมีสองชั้น:
      - `VendorKnownGoodCorrector.ApplyAsync` — ค่าที่เคยยืนยันแล้วของผู้ขายราย
        นั้น (คีย์ = เลขผู้เสียภาษี) โดย `Source = "UserCorrection"` ชนะ `"AzureDI"`
        · รันทุก tier รวม **Azure** (เดิมเรียกเฉพาะ tier 2/3) · รอบ 190: **เติม**ที่อยู่ผู้ขาย
        ที่ว่างจากคลังได้ด้วย (ด่าน `OcrKnownGoodAddressFill`) — เดิมซ่อมได้แค่ค่าที่อ่านมาเพี้ยน
      - `EnrichFromDbdAsync` — เอาเลขผู้เสียภาษีไปค้นทะเบียน (RD VAT → DBD)
        แล้ว **`Helpers/DbdIdentityGuard` ตัวเดียว** ตัดสินว่าทะเบียนชนะไหม
        โดยถามว่า "**กุญแจ**ถูกไหม" (`Helpers/OcrVendorKeyEvidence`: ป้ายกำกับบน
        กระดาษ + ไม่ใช่เลขผู้ซื้อ/เลขเรา + ไม่ได้อยู่ในบล็อกผู้ซื้อ) **ไม่ใช่**
        "ชื่อสองชื่อคล้ายกันไหม" — ชื่อแบรนด์ละตินบนโลโก้ ("DECATHLON") ได้คะแนน
        ความคล้ายกับชื่อนิติบุคคลไทย = 0.000 เท่ากับ "คนละบริษัท" ⇒ ถ้าตัดสิน
        ด้วยชื่ออย่างเดียว ชื่อโลโก้จะกลายเป็นชื่อคู่ค้าถาวร (บั๊กจริง 2026-09-18)
   7. **Rule-based defaults** — VAT 7%, BranchCode `00000`, GL account จาก
      `VendorDefaultGlAccount`, payment terms = company default
   8. **เดาแบบมีเหตุผล** ใช้ `IAiOrchestrator.AskAsync` เป็น last resort
      (per กฎเหล็ก #1: ต้อง CAPTURE + DISTILL) — รวม
      `AiFeatureKey.OcrLineItemSplit` ที่แตกบรรทัดจากข้อความเมื่อ engine
      ไม่คืนตารางมา (ผ่านด่าน `Helpers/OcrLineSplitGuard` เสมอ)

   > ⚠️ **Vision-LLM tier ยังไม่มีในระบบ** — เดิมข้อ 1 เขียนว่า
   > "Vision/OCR primary (DeepSeek-VL)" แต่ `DeepSeekProvider.CompleteAsync`
   > รับแต่ข้อความ ไม่มีทางส่งรูปเข้าไป และไม่มี engine ตัวไหนเรียกโมเดล
   > vision เลย. **โค้ดเป็น ground truth — doc ผิด จึงแก้ doc**
   > ถ้าจะทำจริงต้องมี: (ก) ให้ provider ส่ง image content ได้
   > (ข) engine tier ใหม่ที่ส่งไฟล์สแกน (ค) toggle + โควตา + budget guard
   > (ง) ด่านตรวจผลลัพธ์เทียบยอดบนกระดาษแบบเดียวกับ `OcrLineSplitGuard`
   > — **ห้าม merge ครึ่ง ๆ กลาง ๆ** เพราะตัวเลขที่ได้กลายเป็นรายการบัญชีจริง

3. **Confidence + ป้ายเตือน** — field ที่ confidence < 0.85 → highlight สีเหลือง
   พร้อม tooltip "ตรวจสอบอีกครั้ง" แต่ **ยังต้องมีค่าเติมไว้แล้ว** ไม่ใช่ blank

4. **UI = ปุ่ม "ยืนยัน" เด่นเป็นหลัก** — ไม่ใช่ form ว่าง รอกรอก
   - **Default action** = approve (Enter / ปุ่มเขียวใหญ่)
   - **Secondary** = edit (เปิดเฉพาะ field ที่ user แตะ)
   - ห้ามมี required-field validation error ตอนกด approve (ทุก field ต้อง pre-filled แล้ว)

5. **Round-trip learning** — ทุก field ที่ user แก้ → call
   `IAiFeedbackRecorder.RecordUserChoiceAsync(feedbackId, userValue, acceptedAi=false)`
   เพื่อ retrain ทำให้ครั้งหน้า OCR เติมถูกตั้งแต่แรก (per กฎเหล็ก #1)

6. **Multi-page / multi-line table** — ตาราง line items ต้อง parse ครบทุกแถว
   ห้ามให้ user เพิ่มแถวเอง; เพิ่ม "Add line" เฉพาะกรณี OCR หลุดแล้ว user รายงาน

### Anti-pattern — ห้ามทำ

```csharp
// ❌ ห้าม: OCR เสร็จ → return เฉพาะ raw text ให้ user แปะลงฟอร์ม
return new { rawText = ocrResult };

// ❌ ห้าม: เติมแค่บาง field ปล่อย field อื่นว่าง
dto.SellerName = ocr.SellerName;  // เติม
dto.BuyerTaxId = null;            // ปล่อยให้ user หา — ห้าม

// ❌ ห้าม: required validation error ตอน approve เพราะ field ว่าง
if (string.IsNullOrEmpty(dto.GlAccountCode))
    return BadRequest("กรุณาเลือกบัญชี");  // ผิด — ต้อง pre-fill ไว้แล้ว
```

```csharp
// ✅ ถูก
var dto = await _ocrService.FullExtractAsync(file, companyId, ct);
// dto ทุก field ที่ §86/4 บังคับ ต้องมีค่า (จาก OCR → distill → history → rule → AI)
// confidence ต่ำ = highlight แต่ห้าม null
return Ok(dto);  // UI โชว์ → user กด "ยืนยัน" จบ
```

---

## 🚨 กฎเหล็ก #4 — Engineering Discipline (บทเรียนจากบั๊กจริง)

> ทุกข้อมีบั๊กที่เคยหลุดถึงผู้ใช้เป็นที่มา — ไม่ใช่ best practice ลอย ๆ
> ผิดคลาสเดิมซ้ำ = ยังไม่ผ่าน review

### A. Defect classes ที่ห้ามเกิดซ้ำ — checklist ก่อน commit

- [ ] **สอง renderer ห้าม drift** — แก้เอกสารที่พิมพ์ = แก้ทั้ง HTML
  (`PdfGenerationService`) + QuestPDF (`.DocumentRenderer`) ในคอมมิตเดียว;
  ข้อความบนเอกสารผ่าน `DocumentLabels` เท่านั้น ห้าม string literal
  _(ที่มา: โหมด en เอกสารปนไทย 20 จุดเพราะ HTML renderer ฝัง literal)_
- [ ] **เก็บแล้วต้อง echo กลับ** — field ที่รับใน Create/Update ต้องอยู่ใน
  Response DTO + hydrate ฟอร์มตอนแก้ไข มิฉะนั้น "เปิดแก้แล้วบันทึก ค่าหาย
  เงียบ ๆ" _(ที่มา: `DocumentLanguage` ทั้งบน Document และ Contact)_
- [ ] **Resolver กลาง ห้ามคำนวณเอง** — ค่าที่มีลำดับชั้น (ภาษา/ที่อยู่/
  หัวเอกสาร/เครดิตเทอม) ต้องผ่านฟังก์ชันกลางตัวเดียว ห้าม inline `??` เอง
  _(ที่มา: PDF/A-3 Title คำนวณภาษาเองแล้วไม่ตรงเนื้อเอกสารในไฟล์เดียว)_
- [ ] **ห้าม "ใครมาก่อนชนะ"** — ตัวเติมฟอร์ม async ทุกตัวใช้
  `dataset.autoSrc` + `userTouched`: ค่าผู้ใช้ชนะเสมอ, specific ชนะ generic,
  ผลลัพธ์ห้ามขึ้นกับลำดับ response _(ที่มา: เทอมชำระเงิน 4 ตัวเติมแข่งกัน)_
- [ ] **เอกสารลูกต้องสืบทอด** — field ระดับเอกสารที่ลูกค้าเห็น ต้องไหลตาม:
  convert → clone → settlement receipt → CN อัตโนมัติ → recurring
  (สืบทอด null เป็น null — ห้ามแปลงเป็นค่า default ตอนสืบทอด)
  _(ที่มา: ภาษาเอกสารหลุด 5 ทางเข้าอนุพันธ์)_
- [ ] **ห้าม silent no-op** — ถ้า server ไม่รับ field ตอน update → UI ต้อง
  ล็อกช่อง + บอกเหตุผล ห้ามให้กดบันทึกแล้วไม่มีผลเงียบ ๆ
  _(ที่มา: เปลี่ยนใบต้นทางตอนแก้ไขแล้วไม่มีผลอะไรเลย)_

### B. Checklist "เพิ่ม field ใหม่ระดับเอกสาร" (ไล่ตามลำดับ ครบทุกข้อ)

```
ฟอร์ม (markup+id) → payload (สร้าง: ว่าง=null · แก้ไข: ""=ล้างค่า)
→ CreateDocumentRequest + UpdateDocumentRequest → entity
→ DatabaseMigrationHelper (ADD COLUMN IF NOT EXISTS) → DocumentResponse
→ hydrate ตอน openEdit → reset ตอนฟอร์มใหม่ (ห้ามค้างค่าใบก่อน)
→ renderer ×2 (ถ้าพิมพ์) → เอกสารลูกสืบทอด (ข้อ A)
→ DOCUMENT_FLOW.md + TEST_PLAN.md ในคอมมิตเดียวกัน
```

### C. Security (จากผล security audit — บั๊กจริงทั้งหมด)

- [ ] **HtmlEncode ทุก field ที่ผู้ใช้/OCR/partner API คุมได้** ก่อนต่อเข้า
  HTML — renderer คืน text/html ที่ browser + Chromium รัน, JWT อยู่ใน
  localStorage ⇒ XSS = token theft _(ที่มา: XSS ใน line.Description/ชื่อบริษัท)_
- [ ] **Secret/key ใหม่ทุกตัว fail-fast ใน production** — throw เมื่อว่าง
  หรือเป็น placeholder (แบบ `JWT_SECRET`/`ENCRYPTION_KEY` ใน `Program.cs`)
  ห้าม fallback dev key เงียบ ๆ _(ที่มา: PII เข้ารหัสด้วย dev key ใน source)_
- [ ] **Hash/signature มี canonical function เดียว** ใช้ร่วมทั้งฝั่งเขียน
  และฝั่ง verify — ห้ามเขียน format string สองที่
  _(ที่มา: audit hash-chain verify ไม่มีวันผ่านเพราะ format ต่างกัน)_
- [ ] query ใหม่ทุกอันมี `CompanyId == companyId` (ย้ำจากกฎ M — raw SQL
  ไม่ผ่าน global query filter ต้องใส่เองเสมอ)

### D. Multi-instance readiness (ก่อน scale จะสายเกินแก้)

- ห้ามสร้าง state ข้าม request เป็น `static` dict / `IMemoryCache` โดยไม่มี
  แผน multi-node — ใช้ pattern DB-upsert แบบ `ChatRateLimiter` ที่มีอยู่แล้ว
- Background job ใหม่ต้องกันรันซ้ำข้าม instance (`pg_advisory_lock` ต่อ
  job run — pattern มีใน `FinancialManagementService`) + งานสแกนทั้งฐาน
  ต้องมี watermark ต่อบริษัท
- ไฟล์ห้ามเขียน local disk ตรง — ผ่าน abstraction กลาง

### E. เงิน/ภาษี — วินัยเชิงตัวเลข

- `Math.Round` ระบุ `MidpointRounding.AwayFromZero` เสมอ (default =
  banker's rounding — OCR เคยหลุด) · เงินเป็น `decimal` เท่านั้น
- **ห้าม reuse field ผิดความหมาย** — สร้าง field ใหม่ ไม่ยืม field เดิมเก็บ
  ค่าคนละเรื่อง _(ที่มา: `TotalTaxWithheld` เก็บ CIT ทำ aggregate เพี้ยนทุกจุด)_
- Business error โยน exception ชนิดเฉพาะ (ข้อความไทยถึงผู้ใช้ได้) — อย่าใช้
  `InvalidOperationException` ปนกับ framework แล้วปล่อย middleware echo
- **ห้าม `catch {}` กลืน error ใน payment/stock/JE path** — fail loud
  เท่านั้น _(ที่มา: ConfirmPayment กลืน error แล้ว order "สำเร็จ" ทั้งที่
  stock/เงินไม่ลง)_

### F. เครื่องมือบังคับก่อน commit (env นี้ยังไม่มี .NET SDK จนกว่าเจ้าของจะเปิด host .NET ใน proxy — **CI บน `claude/**` คือ compiler ตัวแรกหลัง push**)

```
python3 tools/di_cycle_check.py        # วงกลม DI (dotnet build จับไม่ได้)
python3 tools/nullable_arg_check.py    # CS1503 nullable→non-nullable
python3 tools/using_check.py           # CS0246 ลืม using ของ type ในเรพ
python3 tools/record_arg_check.py      # CS1739 named arg ที่ record ไม่มี
python3 tools/accessibility_check.py   # CS0051/CS0050 ชนิด private ในลายเซ็น public
python3 tools/arg_type_check.py        # CS1503 ส่ง id เข้าพารามิเตอร์ที่รับ entity
python3 tools/gl_code_check.py         # เลขผังบัญชี hardcode ชนความหมายผังมาตรฐาน
python3 tools/verbatim_string_check.py # CS1010/CS1056 `"` เดี่ยวปิด verbatim string
python3 tools/dead_link_check.py      # ลิงก์ /pages/*.html ที่ไม่มีไฟล์ปลายทาง
python3 tools/localstorage_key_check.py # คีย์ localStorage ที่อ่านแต่ไม่มีใครเขียน
python3 tools/js_dup_method_check.py   # method ชื่อซ้ำใน object เดียวกัน (ตัวหลังทับเงียบ)
python3 tools/identifier_space_check.py # CS1001/CS1003 ช่องว่างในชื่อ method/ชนิด · CS1056 ตัวอักษรต้องห้าม (§) ในชื่อ
python3 tools/namespace_shadow_check.py # CS0234 `Helpers.X` ผูกไป namespace ผิดชั้น
python3 tools/service_interface_check.py # CS1061 controller เรียกเมธอดที่ลืมประกาศใน interface ของ service (impl+endpoint ครบ แต่ interface ขาด) → ลาก CS0006 ให้เทสต์ล้มตาม
python3 tools/dto_nullable_contract_check.py # DTO ประกาศ `string` (ไม่ nullable) ทั้งที่ service เติมค่าให้เมื่อว่าง → ASP.NET ใส่ [Required] โดยปริยาย แล้วตีกลับเป็นอังกฤษชื่อ property C# ก่อนถึงโค้ดเรา (ฟ้องเฉพาะตอนสองชั้น**ขัดกัน** — ชั้นที่ throw/BadRequest เองถือว่าตรงกัน ไม่ฟ้อง)
python3 tools/css_var_check.py       # var(--x) ที่ไม่เคยประกาศ → ปุ่มล่องหน/สีหาย
python3 tools/undeclared_local_check.py # CS0103 ส่งตัวแปรที่ไม่มีในเมธอดนั้นเป็นอาร์กิวเมนต์
python3 tools/admin_menu_gate_check.py # เมนู/endpoint ของแพลตฟอร์มที่ลูกค้ามองเห็น
python3 tools/upload_route_check.py  # โฟลเดอร์อัปโหลดที่เขียนได้แต่ static handler ตอบ 404
python3 tools/regex_line_span_check.py # \s เป็นตัวคั่นระหว่างตัวเลข → กลืนขึ้นบรรทัดใหม่
python3 tools/advisory_lock_key_check.py # คีย์ advisory lock ที่สุ่มต่อ process → ล็อกข้ามเครื่องไม่ได้
python3 tools/csp_external_ref_check.py # สคริปต์/สไตล์ภายนอกที่ CSP ของเราเองไม่อนุญาต → เบราว์เซอร์บล็อกเงียบ
python3 tools/tab_hidelist_check.py  # panel ของแท็บที่ไม่อยู่ในลิสต์ซ่อน → เปิดแล้วค้างทับแท็บอื่น
python3 tools/stock_writer_check.py  # เขียนสต็อกนอก IStockLedger → สองความจริงที่ไม่มีวันตรงกัน
python3 tools/payment_provider_boundary_check.py # โดเมน/คีย์ของ gateway หลุดนอก adapter · ข้อมูลบัตรบนเซิร์ฟเวอร์
python3 tools/write_permission_gate_check.py # endpoint ที่เขียนข้อมูลแต่ไม่มีด่านสิทธิ์ ([Authorize] ตอบแค่ "ล็อกอินไหม")
python3 tools/html_attr_escape_check.py # ข้อความอิสระเข้า attribute ไม่ผ่านตัวหนี → แตก attribute ยิงสคริปต์ได้
python3 tools/escape_helper_check.py # ตัวหนี HTML ที่เขียนเองหนีไม่ครบ 5 ตัว → ปลอดภัยแค่ครึ่งเดียวแต่ดูเหมือนปลอดภัยแล้ว
python3 tools/string_quote_close_check.py # CS1002 `"` ASCII ในสตริง**ธรรมดา** ปิดสตริงกลางคำ (verbatim_string_check ไม่ครอบ)
python3 tools/onclick_js_string_check.py # esc() ใน JS string ของ onclick → เบราว์เซอร์ decode entity ก่อน JS อ่าน ⇒ ปิด string ได้อยู่ดี ทั้งหน้าตาย
python3 tools/sequence_lock_check.py # ออกเลขรันเองด้วยการเรียงแบบข้อความ → ไม่มีล็อก และ "9999" ชนะ "10000" เลขวนกลับทับของเดิม
python3 tools/deep_link_param_check.py # ลิงก์ส่ง query param ชื่อที่หน้าปลายทางไม่เคยอ่าน → กดแล้วตกที่ลิสต์เปล่า (dead_link_check ดูแค่ว่าไฟล์มีอยู่)
python3 tools/flag_field_overwrite_check.py # เขียนทับช่องข้อความที่เป็นที่สะสม**และ**มีด่านอ่านธงจากมัน → ธงของด่านหายเงียบ
python3 tools/ocr_helper_test_check.py # ตัวตัดสิน OCR (Helpers/Ocr*.cs) ที่ไม่มีเทสต์อ้างถึง → แก้แล้วใบที่เคยถูกกลับมาผิดโดยไม่มีอะไรฟ้อง
python3 tools/tuple_name_merge_check.py # ternary ที่สองสาขาเป็น tuple ชื่อไม่ตรงกัน → C# ทิ้งชื่อ แล้ว CS1061 ไปโผล่ไกลจากจุดที่ผิด
python3 tools/line_vat_source_check.py # เขียนอัตรา VAT ของบรรทัดตรง ๆ ไม่ผ่าน Layout.setLineVat → ตัวแนะนำทับค่าที่อ่านจากกระดาษ ยอดเพี้ยนเงียบ
node tools/vat_line_source_sim.js   # ล็อกพฤติกรรมลำดับที่มาของอัตรา VAT ด้วยโค้ดจริง (สองทิศ)
node tools/validation_field_label_sim.js # ข้อความ validation ต้องชี้ "ป้ายไทยที่ผู้ใช้เห็น" ไม่ใช่ชื่อ property C# · ช่องที่ไม่มีบนหน้าต้องบอกว่าไม่มี (รันโค้ดจริงจาก api.js)
python3 tools/blank_number_null_check.py # ช่องตัวเลขที่เว้นว่างถูกส่งเป็น `null` → System.Text.Json แปลงเข้า int/decimal ไม่ได้ ⇒ โยน body ทิ้งทั้งก้อน ⇒ ไม่มีอะไรถูกบันทึกและ error ชี้ไปที่ "dto" ที่ไม่มีบนหน้าจอ
node tools/blank_number_form_sim.js # ล็อก "ว่าง = ตัดคีย์ทิ้ง · data-blank=\"0\" = ศูนย์ · 0 ที่พิมพ์เองต้องไม่หาย" ด้วยโค้ดจริงจากหน้าเว็บ
# ↑ `tools/*_sim.js` ทุกตัวถูก check_all.sh กวาดรันเอง (แก้ 2026-09-21 — เดิมเขียนไว้ว่ารันแต่ **ไม่เคยรัน**)
python3 tools/enum_number_compare_check.py # UI ตัดสิน enum ด้วยตัวเลข ทั้งที่ API ส่งเป็น "ชื่อ" → เงื่อนไขเท็จเสมอ ปุ่มไม่ขึ้น ป้ายเป็น "-" · select option ตัวเลขที่ hydrate จาก enum (กติกา 3 · รอบ 193)
python3 tools/filing_deadline_single_source_check.py # ตารางกำหนดยื่นแบบภาษีที่เขียนซ้ำ → ภ.พ.36 เคยได้วันที่ 23 แทน 15 = เตือนช้ากว่ากฎหมาย 8 วัน
python3 tools/terminal_status_writer_check.py # สถานะปลายทาง (Filed/Matched/Approved/NoShow/StockDeducted) ประทับนอกเจ้าของกติกา — ratchet baseline (ราก R1)
python3 tools/ai_feedback_source_check.py # หน้าเว็บบันทึก "คำตอบที่ผู้ใช้เลือก" โดยไม่ส่ง `source` → นับเป็น Implicit ⇒ คลังเรียนรู้ทันทีตายเงียบ
python3 tools/required_call_site_check.py # ด่านเงิน/ภาษี/สต็อก/สิทธิ์ที่มีแต่ service ไม่เรียก — ล็อกจุดเรียกรายเมธอด 7 ชนิด (must · must_re · must_lit · call_args · before · forbid · `ชื่อ#n` overload) · negative test ในตัวรันทุกครั้ง (ลบ/คอมเมนต์/สลับลำดับ/ใส่สูตรต้องห้าม แล้วต้องฟ้อง) · "เทสต์เรียกแค่ helper" เขียวแม้ถอดการแก้ — ตัวนี้ล็อกว่า service เรียกจริง (รอบ 193)
python3 tools/settings_reader_check.py # ค่าตั้งที่เก็บ+echo ครบแต่ไม่มีผู้อ่าน ("มีช่อง ≠ มีผล") — ratchet กับ settings_reader_baseline.txt · `--self-test` ถอดผู้อ่านจริงแล้วต้องฟ้อง (รอบ 193)
python3 tools/approved_status_writer_check.py # เอกสารเกิดมา/ถูกตั้ง `Approved` นอกเส้นที่เรียก `IIssuedDocumentHooks.RunAsync` (e-Tax หลังออกเอกสาร) · hook ต้องอยู่หลัง `CommitAsync` บนเส้นเดียวกัน — ratchet baseline (รอบ 193)
python3 tools/company_settings_factory_check.py # `new CompanySettings` นอก `Helpers/CompanySettingsFactory` → แถวค่าตั้งเกิดด้วยค่า default ที่ขัดกับธงบริษัท (VAT สองธง · รอบ 193)
python3 tools/contact_taxid_only_match_check.py # query ผู้ติดต่อด้วยเลขภาษีอย่างเดียวไม่ดูสาขา (กติกา 1) · จับชื่อ/อีเมล/เบอร์หลัง `ContactTaxBranchKey.FindAsync` นอก `SoftScope` (กติกา 2 `#soft`) — ratchet baseline (รอบ 193)
python3 tools/attachment_gate_check.py # ทางเข้าที่แตะไฟล์แนบ/สแกนแต่ไม่เรียก `IAttachmentAccessGate` (หรือเรียกแล้วทิ้งผล/เรียกหลังแตะไฟล์) · ทุก action ของ OcrController ที่รับ scanId/fileAttachmentId/documentId · negative test ถอดด่านจากไฟล์จริงในตัว (รอบ 193)
python3 tools/owner_action_wiring_check.py # ด่านเจ้าของ/ปฏิเสธ API key (`[RejectApiKey]` · `[RequireOwner]` · `OwnerActionGuard` · `ApiAccessPolicy` · `RegistrationPolicy`) ต้องอยู่ที่จุดเรียกจริงและ "ใช้ผล" — `--self-test` ถอดทีละแถวจากไฟล์จริง (รอบ 193)
node tools/api_busy_indicator_sim.js # ตัวแสดง "กำลังทำงาน" กลาง (`ApiBusy` ใน api.js) — นานแสดง · สั้นไม่กระพริบ · ปุ่มกันกดซ้ำ (โค้ดจริง + negative test ในตัว)
node tools/employee_form_contract_sim.js # ฟอร์มพนักงาน ↔ API: hydrate↔payload สองทิศ · คีย์ ⊆ DTO · ทุกช่องในโมดัลถูก hydrate (ซอร์สจริง · baseline จากคอมมิตก่อนแก้ · รอบ 193)
python3 tools/doc_commit_sha_check.py # sha ที่ doc อ้างแต่ไม่อยู่บน branch (amend แล้ว sha ที่จดไว้ก่อน commit ตายทันที)
python3 tools/dead_helper_check.py    # public static ใน Helpers ที่ไม่มีผู้เรียกนอกไฟล์ (นอกคอมเมนต์ · เทสต์ไม่นับ) — ratchet กับ tools/dead_helper_baseline.txt: ล้มเฉพาะตัวใหม่ · "มี ≠ ถูกเรียก" มีตัววัดแล้ว
python3 tools/test_inventory.py --check # TEST_PLAN §0 ต้องตรงกับ [Fact]/[Theory] จริง (เคยค้าง "~150 เคส/19 ไฟล์" จนผิด 10 เท่า) — วางผล --row ทับ
node --check                           # ทุก <script> ใน .html ที่แก้
awk brace-balance                      # ทุก .cs ที่แก้
```
> **ทางลัด (รอบ 169): `bash tools/check_all.sh`** รันทุกบรรทัดข้างบนด้วยคำสั่งเดียว (checker ทุกตัว + `node --check`
> ทุก `<script>` ในไฟล์ที่แก้ + awk brace + U+FFFD + `test_inventory --check` + `dotnet build/test` ถ้ามี SDK) — exit code
> เดียว · และ **ก่อนแก้สัญลักษณ์ใด** ให้ `python3 tools/callers.py <Symbol>` (นิยาม · ผู้เรียกจริง · เทสต์ · คอมเมนต์ แยกกัน)
> แทนการประกอบ grep เอง — เหตุผลอยู่ใน `REGRESSION_ROOT_CAUSE_2026-09-18.md` §2.3 (ต้นเหตุอันดับ 2 ของการถดถอย 33 กรณี
> คือ "แก้เส้นเดียวจาก N โดยไม่ grep call site")
- แจ้งผู้ใช้เสมอว่า "ยังไม่ได้คอมไพล์ — รบกวน rebuild ฝั่งคุณ"

### F2. หลักการ 10 ข้อ (กลั่นจากบทเรียน 142 ข้อ — ตัวเต็มอยู่ใน `docs/lessons/`)

1. **แก้ที่หนึ่ง grep ทั้งเรพ** — `python3 tools/callers.py <Symbol>` ก่อนแตะ · ตอบเป็นตัวเลขในคอมมิต ("รูปแบบเดิมเหลือ 0 จุด") ·
   คู่สมมาตร (`if (isSeller) … else …` · renderer HTML/QuestPDF/พรีวิว · ฝั่งอ่าน/ฝั่งเขียน · ทุกทางเข้าที่แตะข้อมูลชุดเดียวกัน) ต้องอ่านอีกฝั่งทันที
2. **มี ≠ ถูกเรียก** — helper/ด่าน/doc-comment ที่ไม่มี call site = ไม่มี (`tools/dead_helper_check.py` ratchet · "ของที่ไม่มีใครเรียก" ต้องเลือกอย่างตั้งใจ: ต่อสาย หรือ ลบ)
   · **ค่าตั้งที่เก็บ+echo กลับครบ ≠ มีผล** (รอบ 193: ~83 ค่าตั้งมีช่องบนจอแต่ไม่มีผู้อ่าน) — round-trip ต้องตามถึง "ผู้อ่านเชิงธุรกิจ" ทุกทางเข้า
   (`tools/settings_reader_check.py` ratchet) · **เทสต์ที่เรียกแค่ helper ≠ ด่านถูกต่อสาย** — ถอดการเรียกใน service แล้วเทสต์ยังเขียว ⇒ ล็อกจุดเรียกด้วย
   `tools/required_call_site_check.py` (มี/ลำดับ/ใช้ผล/ห้ามประกอบเอง) ทุกครั้งที่เพิ่มด่านเงิน/ภาษี/สิทธิ์
3. **ค่าที่แต่งขึ้น / สถานะปลายทางที่ระบบประทับเอง อันตรายกว่าการไม่ตอบ** — ไม่รู้ = บอกว่าไม่รู้ แล้วให้ชั้นถัดไป (กฎ/คน/AI) ตัดสิน ·
   สถานะ "ระบบภายนอกรับแล้ว" ตั้งได้เฉพาะเมื่อภายนอกตอบกลับจริง · ตัวเลขล้วนไม่มี "การสะกดผิด" ห้าม fuzzy
4. **ตัวตั้งตัวเดียว** — กติกา/ตาราง/สูตร/ชุดสถานะ/ชุดชนิดเอกสาร อยู่ใน `Helpers/` OWNER file เดียว · helper ต้อง "เรียกได้ในประโยคเดียว" ·
   ตารางกฎหมาย (อัตรา · กำหนดยื่น · checksum · พ.ศ.) ห้ามมีสำเนาที่สอง · เอกสารลูกสืบทอดจากแม่ผ่าน resolver กลาง
5. **Server computes · page displays** — JS ห้ามมีสำเนากติกา/ป้าย/ตาราง/เงื่อนไข compliance · enum ออกเป็น "ชื่อ" เสมอ · `undefined` = "ยังไม่ได้ตรวจ" ≠ `[]`/`0`
6. **ด่านต้องมี negative test มิฉะนั้นไม่มีด่าน** — checker/เทสต์/ด่าน runtime ทุกตัว ใส่บั๊กกลับแล้วต้องจับได้ · **checker ที่ฟ้องผิด = checker ที่พัง** ·
   baseline ของ simulation ต้องมาจาก `git show <sha>:<file>` ไม่ใช่เขียนจากความจำ · ด่านที่ป้อนผลของสูตรที่ตัวเองตรวจ = ผ่านตลอดกาล
7. **ล้มดัง 3 ที่** (ตัวข้อมูลที่ผู้ใช้เปิดดู · สถานะงาน · คำตอบผู้เรียก) — `LogWarning`/`Debug.WriteLine`/`catch {}` ไม่ใช่การดัง ·
   HTTP 200 กับของว่างคือการโกหก · ข้อความที่ระบุ "สาเหตุ" ต้องตรวจสาเหตุนั้นจริง
8. **เข้มขึ้นต้องมีเทสต์ทิศตรงข้าม + ทางไปต่อของผู้ใช้** — บีบตัวกรอง/เพิ่ม throw แล้วต้องถามว่า "เคสที่ถูกตัดออกเห็นอะไร · ผู้ใช้ทำอะไรได้แทน" ·
   เทสต์ต้องล็อกทั้ง "ใบที่พังกลับมาถูก" และ "ใบที่ถูกอยู่แล้วไม่ถูกแตะ" · คำเตือนที่ฟ้องใบถูกทุกใบ = ปิดด่านโดยไม่ตั้งใจ
9. **Persist แล้วต้องมี migration** — แก้โค้ดอย่างเดียวไม่พอเมื่อของเสียถูกเก็บไว้แล้ว (regex ใน DB · ค่าที่เรียนไว้ · สถานะที่ประทับผิด) ·
   ค่าที่ "คงที่ต่อ key" กับ "เปลี่ยนทุกใบ" ห้ามอยู่ถังเดียวกัน
10. **Doc ตามโค้ดในคอมมิตเดียว · sha เติมในคอมมิตตามหลัง ห้าม amend** — โค้ดเป็น ground truth; doc ผิด = แก้ doc ทันที ·
    `grep` ที่ตอบว่า "ไม่มี" ไม่ใช่หลักฐาน ต้องเปิดไฟล์ตรงจุด · ผลตรวจของทีม/agent ต้อง verify ก่อนเชื่อและจดไว้เมื่อผิด

### F3. Checklist ก่อน push (12 ข้อ — ครึ่งแรกเครื่องทำ)

```
ก. เครื่องทำ — bash tools/check_all.sh (~1 นาที)
 1. checker ทุกตัวผ่าน (ล้มตัวไหน = แก้ ไม่ใช่เลี่ยง)
 2. node --check ทุก <script> ใน .html ที่แก้ · awk brace ทุก .cs ที่แก้ · U+FFFD
 3. ไม่มี dead helper ใหม่ (dead_helper_check) · TEST_PLAN §0 ตรงเทสต์จริง (test_inventory --check)
 4. doc sha อยู่บน branch (doc_commit_sha_check) — เติม sha ในคอมมิตตามหลัง ห้าม amend
 5. ถ้ามี dotnet: build + test ผ่าน
 6. หลัง push: อ่านผล Actions ผ่าน MCP actions_list/get_job_logs → แดง = แก้ก่อนรายงานผู้ใช้

ข. คนทำ — ตอบเป็นข้อความในคำอธิบายคอมมิต (ตอบไม่ได้ = ยังไม่ push)
 7. "แก้ที่นี่ แล้ว callers.py/grep รูปแบบเดิมทั้งเรพได้กี่จุด" → ตัวเลข (0 ก็เขียนว่า 0)
 8. "ทางเข้าอื่นที่แตะข้อมูลชุดเดียวกัน (มือ/CSV/API/LINE/job/มือถือ) เดินด่านเดียวกันไหม"
 9. "ถ้าเพิ่ม throw/เข้มขึ้น — ผู้ใช้ที่ถูกกันทำอะไรได้แทน + เทสต์ทิศตรงข้ามชื่ออะไร"
10. "ค่าที่ persist ไว้ก่อนแก้ ยังผิดอยู่ไหม → มี migration ไหม"
11. "แตะเงิน/ภาษี/สิทธิ์ → ส่ง diff ให้ subagent ฝ่ายค้าน 1 รอบแล้ว" (3 คำถาม: ทางเข้าอื่น? ทิศตรงข้าม? สถานะปลายทางประทับเองไหม?)
12. "DOCUMENT_FLOW/ACCOUNT_STRUCTURE/TEST_PLAN ต้องขยับไหม" — เพิ่ม/ลด throw หรือเปลี่ยนสูตรค่าที่ persist = ใช่ · ประวัติรอบเติมใน CHANGELOG.md
```

### F4. ข้อห้าม 7 ข้อ (มีหลักฐานว่าเสียเวลาแล้ว)

1. เขียน checker ที่ต้อง type resolution อีก (`.HasValue` · `nullable_unwrap` ถูกทิ้งแล้ว · `d.IsSubjectToSocialSecurity` คลาสเดียวกัน) — คำตอบคือ compiler (CI/SDK)
2. จดบทเรียน CSxxxx เพิ่ม — 9 ครั้งไม่ลดการเกิด
3. checker ที่กวาดทั้งเรพด้วยกติกาที่ต้องรู้ taint (`_db.Users` 85 จุด) — ใช้ scope แคบ/OWNER file เท่านั้น
4. แก้ "เสียงรบกวน" ด้วยการปิด trigger CI — ใช้ paths-ignore/concurrency/notification settings
5. ปล่อย doc เป็น append-only log โดยไม่มี "สถานะปัจจุบัน" ที่สั้นพอจะผิดแล้วเห็น (ประวัติรอบอยู่ `CHANGELOG.md`)
6. `BackgroundService` ใหม่โดยไม่ผ่าน `Helpers/JobLock`
7. เพิ่มฟีเจอร์ทับ `DocumentService.cs` ต่อจนกว่าจะแตก posting layer (ERP_REVIEW §6)

### F5. คลังบทเรียน — `docs/lessons/` (ย้ายจากหมวดนี้ 2026-09-18)

> บทเรียนดิบ 142 ข้อที่เคยอยู่ตรงนี้ (ทำให้ไฟล์โต 448 KB และคนอ่าน — รวม agent — อ่านไม่จบ) ย้ายไป
> `docs/lessons/<หมวด>.md` **โดยไม่ลบเนื้อหา** · ดัชนีอยู่ที่ `docs/lessons/README.md` · ก่อนแก้เรื่องใด
> `grep -rl "<คำสำคัญ>" docs/lessons/` · defect class ใหม่ → append ท้ายไฟล์หมวดที่ตรง ไม่ใช่ที่นี่ ·
> แตะ CLAUDE.md เฉพาะเมื่อบทเรียนเปลี่ยน **หลักการ** ใน F2

### G. Testing mandate (ช่องโหว่ใหญ่สุดของระบบ)

- Logic เงิน/ภาษีใหม่ → extract เป็น pure class (แบบ `TaxPointResolver`,
  `Section65TerValidator`) + เทสต์ในคอมมิตเดียวกัน
- แก้บั๊กที่ผู้ใช้รายงาน → reproduce เป็นเทสต์/simulation ให้เห็นตัวเลขตรง
  กับที่รายงานก่อน แล้วค่อยแก้ (ยืนยันว่าแก้ถูกตัว)
- **Control เชิง compliance (hash chain, retention, encryption) ต้องมี
  round-trip test — control ที่ไม่มีเทสต์ยืนยัน = ไม่มี control**
  _(ที่มา: hash chain พังเงียบ ๆ เพราะไม่มีเทสต์เดียวที่ write→verify)_

### H. กันถดถอย — "อะไรที่ทำได้ดีแล้ว ห้ามทำให้แย่ลง" (บทเรียน 2026-09-10)

> ผู้ใช้: "ก่อนหน้านี้เคยทำงานได้ถูกต้องมากกว่านี้ ทำไมแย่ลง" — ไล่ `git log` แล้วพบว่า
> การแก้ 4 ครั้งในสัปดาห์เดียว **ถูกทุกครั้งในตัวมันเอง** แต่ทำให้ใบที่เคยถูกกลับมาผิด
> (รายละเอียดใน `OCR_PIPELINE_REVIEW_2026-09-06.md` §2f RG-01..04)

- **ด่านที่ห้ามตัวเติมค่า "ทับ" อาจกำลังถอดตัวซ่อมของบั๊กอีกตัวที่ไม่มีใครรู้ว่ามี** —
  `4fd8dd6` ห้าม AmountTriple ทับค่าที่ engine อ่าน (ถูก — Makro 951/49/1,000) แต่ก่อนนั้นการ
  ทับนั่นเองที่ซ่อมป้าย SubTotal/Total สลับของใบลักกี้เวย์อยู่โดยบังเอิญ ⇒ พอด่านมา ป้ายสลับ
  โผล่เป็นครั้งแรก. **ก่อนใส่ด่าน/ถอด heuristic ตัวไหน ต้องรันชุดกระดาษจริงในเทสต์ก่อนและหลัง**
  แล้วอธิบายทุกใบที่คำตอบเปลี่ยน — ใบที่เปลี่ยนโดยอธิบายไม่ได้ = มีบั๊กอีกตัวที่ heuristic นั้น
  เคยกลบไว้ ต้องแก้ตัวนั้น**ในคอมมิตเดียวกัน** (ที่นี่คือ `OcrHeaderAmounts`)
- **defect class ที่เพิ่งแก้ในตัวสแกนคำตัวหนึ่ง ต้อง `grep` ตัวสแกนคำ/regex ทุกตัวในไฟล์เดียวกัน
  วันนั้นเลย** — "แถวยอด 0 ไม่ใช่หลักฐาน" แก้ให้ตัวอ่านประเภทเงินได้ (`1f5cc39`) แต่ตัวสแกนคำ
  "มัดจำ" ที่เพิ่มไว้**หนึ่งวันก่อน** (`416f095`) ยังอ่านทั้งหน้า ⇒ แถวฟอร์ม "หักเงินมัดจำ 0.00"
  ทำให้ใบซื้อธรรมดาติด `[DEPOSIT-BUY]` — ญาติของ "แก้ตัวเดียว เหลือที่เหลือ" ในทรงที่เจ็บที่สุด
  เพราะเป็นบั๊กที่**ตัวเองเพิ่งสร้าง**
- **ตัวเติมแบบ "เติมเฉพาะเมื่อว่าง" ไม่มีวันซ่อมค่าที่ถูกตัด** — ชื่อผู้ซื้อ "แอม แฮปปี้" (ตัด
  จาก "หจก. แอม แฮปปี้เนส") ผ่านทุกชั้นเพราะทุกชั้นเห็นว่า "มีค่าแล้ว". ตัวเติมต้องรู้จักเคส
  "ค่าปัจจุบันเป็นส่วนหนึ่งของบรรทัดกระดาษที่ยาวกว่า" (`OcrPartyName.ExpandTruncated`) และ
  ห้ามขยายเป็นชื่อคนละบริษัท (ต้องเป็น superstring ไม่ใช่ fuzzy)
- **ผู้ใช้รายงาน "เคยดีกว่านี้" = triage ด้วย `git log -- <ไฟล์>` ตั้งแต่จุดที่รู้ว่าดี แล้วเทียบ
  พฤติกรรมของกระดาษใบนั้นทีละคอมมิต** — ไม่ใช่อ่านโค้ดปัจจุบันแล้วเดา; และผลต้องเขียนเป็นตาราง
  "คอมมิต · สิ่งที่ตั้งใจแก้ · ผลข้างเคียง · แก้ที่" (§2f) เพื่อให้รอบหน้ารู้ว่าเรื่องนี้เคยเกิด
- **ตัวตัดสินตัวเลข/ธงบนสแกนทุกตัวต้องเป็น pure helper ใน `Helpers/Ocr*.cs` + มีเทสต์ที่ใช้
  เลข/ข้อความจากกระดาษจริง** — `tools/ocr_helper_test_check.py` ฟ้องคลาสที่ไม่มีเทสต์อ้างถึง.
  ตรรกะที่ยังฝังใน `OcrService.cs`/`SmartFieldExtractor.cs` คือที่ที่ถดถอยเกิดโดยไม่มีอะไรฟ้อง —
  แตะเมื่อไรให้ย้ายออกมาเป็น helper พร้อมเทสต์ในคอมมิตเดียวกัน (ทิศเดียวกับ `OcrHeaderAmounts` ·
  `OcrDepositMarker` · `OcrPartyName` · `OcrPredecessorMatcher`)
- **เทสต์ของการแก้ต้องล็อก "ใบที่ต้องยังถูก" ด้วย ไม่ใช่แค่ใบที่พัง** — ทุกไฟล์เทสต์ OCR ที่เพิ่ม
  รอบนี้มีสองครึ่ง: ครึ่งที่พิสูจน์ว่าใบที่พังกลับมาถูก และครึ่งที่พิสูจน์ว่าใบ Makro/ใบส่งออก/ใบที่ป้าย
  ถูกอยู่แล้ว **ไม่ถูกแตะ** — เทสต์ที่มีแต่ครึ่งแรกผ่านได้ทั้งตอนแก้ถูกและตอน "ปิดด่านทิ้ง"
- **สามเส้นที่ผลิตของชิ้นเดียวกัน (สร้าง · repopulate · แก้ในฟอร์มก่อน) ต้องเรียกตัวสร้างตัวเดียว**
  — บรรทัดเอกสารจากสแกนเคยมีตัวสร้าง 3 ชุด (2 ฝั่งเซิร์ฟเวอร์ + 1 สำเนา JS) ⇒ ใบเดียวได้บรรทัด
  สามแบบ; ยุบเป็น `BuildScanLinesAsync` + `GET line-preview`. ปุ่มที่ "อยากได้ผลเร็วโดยไม่บันทึก"
  ให้เซิร์ฟเวอร์คำนวณแล้วส่งมา ไม่ใช่ให้ JS คำนวณเอง
- **หน้าเว็บที่อ่านฟิลด์ซึ่งเซิร์ฟเวอร์ไม่เคยส่ง = 0 ตลอดกาลโดยไม่มี error** — Usage Limits ใน
  พอร์ทัลแอดมินอ่าน `subscription.current` ที่ endpoint แอดมินไม่เคยส่ง (JS คัดลอกจากหน้าลูกค้า)
  และ counter `CurrentStorageUsed`/สมุดรายวัน **ไม่มีใครเขียนทั้งเรพ** ⇒ 0/50 ทุกบริษัท. ก่อนเชื่อ
  ตัวเลขจาก counter ให้ `grep` ว่า**ใครเขียน**มัน; ก่อนอ่านฟิลด์ใน JS ให้เปิด DTO ของ endpoint
  **ตัวนั้น** ไม่ใช่ endpoint ที่หน้าอื่นใช้ · `undefined` ต้องแสดง "ไม่มีข้อมูล" ไม่ใช่ 0
- **method group ที่มีพารามิเตอร์ optional ส่งเข้า `Select` = CS0411** (`items.Select(MapToResponse)`
  เมื่อ `MapToResponse(x, string? y = null)`) — คอมไพเลอร์อนุมานชนิดไม่ได้ ต้องเขียน lambda หรือใช้
  batch mapper; checker ฝั่ง Python มองไม่เห็น (ต้อง resolve overload) → พึ่ง `dotnet build` ฝั่งผู้ใช้

- **บทเรียนที่จดแล้วยังเกิดซ้ำ 20/33 กรณี = การจดไม่ใช่ด่าน** (รอบ 169 — `REGRESSION_ROOT_CAUSE_2026-09-18.md`)
  ทีมโบราณคดีไล่ 145 คอมมิตพบการถดถอยที่พิสูจน์ sha คู่ได้ 33 กรณี: เทสต์จับได้ **0** · checker 1 · ผู้ใช้ 39% ·
  ทีมตรวจรอบถัดไป 30% · และ **20 กรณี defect class ถูกจดในไฟล์นี้ไว้แล้วก่อนเกิด** (`.HasValue` บนชนิดที่คิดว่าถืออยู่
  ซ้ำ 3 · "แก้ตัวเดียว เหลือที่เหลือ" ซ้ำ 8 · "สอง renderer ห้าม drift" — กฎข้อแรกของ A — ซ้ำใน 3 คอมมิตติด).
  กลุ่มที่ "จดแล้วไม่กัน" คือกลุ่มที่ต้องรู้**ชนิดจริง** (= compiler ซึ่ง env นี้ไม่มีเพราะ **proxy policy** ไม่ใช่กฎธรรมชาติ
  — ดู §7.3 O-1/O-2 ที่รอเจ้าของตัดสิน) และกลุ่มที่ต้อง **grep ให้ครบ** (= วินัย → ทำเป็นคำสั่ง `tools/callers.py` +
  ratchet `tools/dead_helper_check.py`). 79% ของ 33 กรณีอยู่นอกขอบเขต static checker — **หยุดเขียน checker ที่ต้อง type
  resolution และหยุดจดบทเรียน CSxxxx เพิ่ม**; ก่อนเริ่มงานอ่าน "หลักการ 10 ข้อ + checklist 12 ข้อ" ใน §8 ของรายงานนั้น
  แทนการไล่ bullet ทั้งหมวด F
  _(ของแถมที่ยืนยันแล้วรอบเดียวกัน: ข้อ M เคยเขียนว่า tenant isolation "บังคับด้วย global query filter" — **ไม่จริง**_
  _(203 ตัวกรองแค่ `IsDeleted`) · `SsoRateSchedule.RangesOverlap` มี doc-comment บน `Payroll.cs:493` ว่า "เป็นตัวตรวจ"_
  _แต่ไม่มีใครเรียก · `PayrollRunFilingScope.CanRemit/CountsTowardFiling` ไม่มีทั้งผู้เรียกและเทสต์ — ทั้งหมดอยู่ใน_
  _`tools/dead_helper_baseline.txt` 58 แถวที่ต้องถูกตัดสินทีละตัว "ต่อสาย หรือ ลบ" ห้ามเดาแทนเจ้าของ)_

### Litmus test ก่อน commit (engineering)

> "บั๊กนี้/โค้ดนี้อยู่ใน defect class ที่เคยเกิดแล้วหรือไม่ — ถ้าใช่
> อะไรคือกลไก (checker/test/pattern) ที่กันไม่ให้เกิดตัวที่สาม?"
> ตอบไม่ได้ = แก้เสร็จแต่ยังไม่จบ

---

## กฎอื่นในโปรเจกต์

- **ฐานข้อมูล** PostgreSQL, schema migrate ด้วย `DatabaseMigrationHelper.ApplyMissingColumns`
  (`ALTER TABLE ... ADD COLUMN IF NOT EXISTS`) — ห้ามใช้ EF Migrations
- **Tenant isolation** — query ทุกอันต้องมี `CompanyId == companyId`
- **Excel** ใช้ `MiniExcel` เท่านั้น (zero transitive deps) ห้าม ClosedXML/EPPlus
- **Frontend** vanilla HTML + Tailwind CDN, ไม่ใช้ build step
- **ภาษา** comment / log / UI string ใช้ไทย (mix อังกฤษได้ถ้าศัพท์เทคนิค)
- **ปี** ในรายงานสรรพากร = พ.ศ. (ค.ศ. + 543); ภายในระบบเก็บ ค.ศ.
- **VAT** = 7% (กฎหมายไทย); per-line `VatRate` รองรับ mixed rate / 0% / exempt
- **เลขเอกสาร** ออกตอน Approve เท่านั้น (Draft ใช้ `DRAFT-{guid}` placeholder)
  เพื่อกัน gap จากการลบ Draft (compliance §86/4)
- **POS หลายสาขา + วัตถุดิบ** — ผลวิเคราะห์และแผน 7 เฟสอยู่ใน `POS_MULTI_BRANCH_ANALYSIS.md`
  (ข้อเท็จจริง **ณ 2026-09-18 — แก้ doc**: `Product.CurrentStock` กับ `WarehouseStock`
  **ยุบผ่าน `IStockLedger` ตัวเดียวแล้ว** (`StockLedger.MoveAsync` เขียนทั้งคู่ในคำสั่งเดียว ·
  `tools/stock_writer_check.py` = 0 จุดนอก ledger) — ประโยคเดิม "สองความจริงที่ไม่คุยกัน"
  ล้าสมัย · ที่ยังค้าง: `ReconcileProductTotalsAsync` มีแต่**ไม่มีใครเรียก** (ตาข่ายซ่อมข้อมูล
  เก่าก่อนเฟส 0 ยังไม่ต่อสาย) · POS ยังไม่ผูก `Branch`/`Warehouse`
  · สลิปพิมพ์ "ใบกำกับภาษีอย่างย่อ" โดยไม่ตรวจ ภ.พ.06)
- **Payment gateway (Omise ก่อน · เปลี่ยนเจ้าได้)** — ออกแบบใน `PAYMENT_GATEWAY_DESIGN.md`
  (วันนี้**ไม่มี**การเชื่อม gateway ใดเลย มีแค่ enum + คีย์ที่เข้ารหัสไว้แล้วไม่มีใครอ่าน ·
  ทุกทางเข้าต้องเดินผ่าน `PaymentIntent` + `IPaymentProvider` ตัวเดียว · webhook ยืนยันแบบของ
  เจ้านั้น (Omise = re-fetch event) ไม่ใช่ HMAC ของเรา · ห้ามสลับ live ก่อนทดสอบผ่าน)
- **โมดูลที่พัก (Lodging)** — flow/เส้นเงินอยู่ใน `DOCUMENT_FLOW.md` §6.5 · โครงสร้างใน
  `ACCOUNT_STRUCTURE.md` §3.1b · สิ่งที่ลอก/ไม่ลอกจาก TakeTime + backlog ใน `LODGING_TAKETIME_ANALYSIS.md`
  · **ผลตรวจฟีเจอร์ฝั่งผู้ใช้ (แขก/เจ้าของ) + งานที่ต้องทำต่อ อยู่ใน
  `LODGING_BOOKING_AUDIT.md`** — หลังบ้านครบ แต่ปลายทางขาด 6 ชิ้น และ 3 ชิ้นเป็น
  defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" (ตัวหนักสุด:
  `PaymentGatewayController` เป็น `[Authorize]` ⇒ **ลูกค้าปลายทางจ่ายออนไลน์ไม่ได้
  เลยทั้งระบบ** ทั้งร้านค้าและที่พัก ⇒ `LodgingReservationPaymentHandler` /
  `SiteOrderPaymentHandler` ไม่มีวันถูกเรียก)

## วิธีทำงาน

- **`bash tools/check_all.sh` ก่อน commit ทุกครั้ง** — ถ้าเครื่องมี `dotnet` มันจะ build/test ให้เอง ถ้าไม่มี
  (env นี้ยังไม่มี SDK จนกว่าเจ้าของจะเปิด host .NET ใน proxy) ให้ push แล้ว **อ่านผล CI บน `claude/**` ผ่าน MCP**
  (`actions_list` → `get_job_logs`) แล้วแก้ก่อนรายงานผู้ใช้ — และยังต้องบอกผู้ใช้ว่า "ยังไม่ได้คอมไพล์ในเครื่องนี้"
- **`/publish`** (`.claude/commands/publish.md`) = คำสั่งปิดรอบ ห่อ F3 ทั้งชุดไว้: ตรวจ branch → `check_all.sh` →
  เอกสารขยับพร้อมโค้ด → ตอบ 6 คำถามในข้อความคอมมิต → push → **อ่านผล CI ผ่าน MCP จนเขียว** → ค่อยรายงาน
  (คอมมิตที่แตะแต่ `**.md` ไม่รัน CI ตาม `paths-ignore` — ต้องบอกผู้ใช้ว่าไม่มีรอบให้รอ ห้ามรายงานว่าเขียว)
- **commit message** เขียนเป็นไทยได้ อธิบาย *ทำไม* มากกว่า *ทำอะไร*
- **ห้าม push** main/master โดยไม่มี explicit approval
- งานพัฒนาทั้งหมดอยู่บน branch ที่ระบุใน prompt ต้น session

## 📘 DOCUMENT_FLOW.md — เอกสารอ้างอิง flow ล่าสุด

`DOCUMENT_FLOW.md` (root ของ repo) คือ **single source of truth** ของ flow
เอกสารทุกประเภทในระบบ — ตั้งแต่ทางเข้า (สร้าง/OCR/integration/convert/recurring)
→ lifecycle (Draft → Approved → Sent → Paid/Voided) → ทางออก (PDF, e-Tax XML,
รายงานภาษี). ใช้เป็น reference เวลาแก้/เพิ่ม feature ที่เกี่ยวกับเอกสาร

### กฎการดูแล (hard requirement)

> **ทุก PR/commit ที่เปลี่ยน flow ต้องอัปเดต `DOCUMENT_FLOW.md` ในคอมมิตเดียวกัน**
> — ห้ามแยก commit, ห้ามขึ้น TODO ไว้ทำทีหลัง. ถ้าไฟล์นี้ drift จากโค้ดจริง
> = ทุกคน (รวม AI agent) จะตัดสินใจผิดจาก doc ที่ไม่ตรงความจริง

**ต้องอัปเดตเมื่อแก้สิ่งต่อไปนี้** (ไม่ครบก็ใส่เพิ่มได้):
1. เพิ่ม/ลด `DocumentType` enum value
2. เปลี่ยน `DocumentStatus` หรือ transition (เพิ่ม state ใหม่, เปลี่ยน guard)
3. แก้ `ApproveDocumentAsync` (ลำดับขั้น, เพิ่ม/ลด validation, JE/stock/asset)
4. แก้ JE posting per type (`AutoPostToJournalAsync`)
5. แก้ `ApplyStockMovementsAsync` (เปลี่ยน DocumentType ที่กระทบ stock)
6. แก้ tax point logic (`TaxPointResolver`)
7. แก้ §86/4 / §82/3 / §82/5 / §65 ตรี gate
8. แก้ undue VAT reclassification (11640 ↔ 11610)
9. แก้ deposit lifecycle (Realize/Refund/Apply)
10. เพิ่ม/แก้ entry point ใหม่ (OCR, integration, convert pair, recurring)
11. เพิ่ม/แก้ออก channel (PDF template, e-Tax type, รายงานภาษีใหม่)
12. เพิ่ม/แก้ AI feature (`AiFeatureKey`) — ต้องเพิ่มในตาราง distillation
13. แก้ retention period / PDPA gate

### Workflow ที่ AI agent ต้องทำ

ก่อน commit ที่กระทบ flow:
- [ ] อ่าน `DOCUMENT_FLOW.md` ก่อน — ให้รู้ behavior ปัจจุบัน
- [ ] แก้โค้ด + อัปเดต section ที่เกี่ยวข้องใน `DOCUMENT_FLOW.md`
  (แก้ file:line, แก้ตาราง, แก้ลำดับขั้นถ้าจำเป็น)
- [ ] อัปเดตบรรทัดท้ายไฟล์: `Last verified against codebase: YYYY-MM-DD —
  commit <new-sha>` (รอใส่ sha จริงหลัง commit ก็ได้)
- [ ] ใส่ทั้ง 2 ไฟล์ใน commit เดียวกัน
- [ ] **ตรวจว่า sha ที่จดไว้อยู่บน branch จริง** — `git merge-base --is-ancestor <sha> HEAD`
  ก่อน push ทุกครั้ง. sha ที่เขียนลง doc *ก่อน* commit จะกลายเป็น **dangling ทันทีที่
  amend/rebase** (แก้ commit message · เพิ่มไฟล์ที่ลืม · ซ่อม build) ⇒ doc ชี้ไปยัง object
  ที่ `git show` ยังเปิดได้วันนี้แต่ **ไม่อยู่ในประวัติของ branch** และจะหายจริงหลัง `gc`
  ⇒ คนที่ตามรอยว่า "พฤติกรรมนี้เปลี่ยนที่คอมมิตไหน" จะหาไม่เจอ
  _(ที่มา: รอบ 163/164 จด `dbaa778`/`0c80a7b` ไว้ ซึ่งเป็น sha ก่อน amend — ของจริงคือ
  `ff635b0`/`0393d2f`; `git cat-file -t` ตอบว่า "commit" ทั้งคู่จึงดูเหมือนถูก
  — **`cat-file` พิสูจน์ว่า object มีอยู่ ไม่ได้พิสูจน์ว่าอยู่บน branch**)_
  _(**และวิธีเติม sha ก็สำคัญ**: `git commit --amend` หลังเติม sha ลง doc **เปลี่ยน sha
  ที่เพิ่งเขียนไปเสมอ** ⇒ วนไม่รู้จบ. ให้เติม sha ใน **คอมมิตตามหลังอีกใบ** (หรือปล่อย
  `<pending>` ไว้แล้วตามเก็บ) — จับได้เพราะ `doc_commit_sha_check` ฟ้องทันทีหลัง amend
  ในรอบที่เพิ่งเขียน checker ตัวนี้เอง)_

### Anti-pattern — ห้ามทำ

```
❌ "เดี๋ยวค่อยอัปเดต doc ทีหลัง" → doc drift → คนถัดมา (รวม AI) อ่าน doc
   แล้วทำผิดเพราะ doc ไม่ตรงโค้ด
❌ commit แยกระหว่างโค้ดกับ doc → ระหว่าง 2 commit นี้ branch อยู่ใน
   inconsistent state
❌ อัปเดตแค่ตาราง ไม่อัปเดต file:line → ลิงก์ใน "Quick reference" จะตาย
   หลัง refactor
```

### ถ้าพบ doc กับโค้ดไม่ตรง

แปลว่า **doc ผิด** (โค้ดเป็น ground truth). ให้แก้ doc ทันทีในคอมมิต
เดียวกับงานที่กำลังทำ — ห้ามรอ

## 📗 ACCOUNT_STRUCTURE.md — โครงสร้างลูกค้า/กลุ่มบริษัท/สาขา/บิลลิ่ง/API

`ACCOUNT_STRUCTURE.md` (root) คือ single source of truth ของชั้น
**BillingAccount → Company → Branch**, ผลิตภัณฑ์ Connected (`/api/v1`),
`UsageEvent`/pricing, portal `/connect` — ใช้กฎการดูแล**ชุดเดียวกับ
DOCUMENT_FLOW.md ทุกข้อ**: แตะ entity/พฤติกรรมที่ไฟล์นั้นครอบ (Company,
Branch, AccountSubscription, Subscription, ExternalIntegration/ApiClient,
billing, quota resolution) → อัปเดตไฟล์ + ป้ายสถานะ (✅/🔨/📋) + บรรทัด
`Last verified` ในคอมมิตเดียวกัน. ไฟล์นี้แยกส่วน "มีจริง" กับ "ออกแบบไว้"
ชัดเจน — ห้ามปล่อยให้ 📋 ที่สร้างเสร็จแล้วยังติดป้ายเดิม


## 📙 ERP_REVIEW_2026-09-05.md — ผลตรวจรอบ "ทีม ERP" (A–F) + แผนสู่ ERP

`ERP_REVIEW_2026-09-05.md` (root) คือผลตรวจรอบที่สอง โจทย์จากเจ้าของโปรเจกต์: "ทีมที่ครอบ
ทุกมุม — ความต่อเนื่อง/ถูกต้อง/ครบถ้วนของข้อมูล · ประเภทเอกสาร จุดแสดงผล จุดให้เลือก ตรงกัน
ไหม · ระบบเดิมต้องถูกก่อนค่อยขยายเป็น ERP". รายงานเต็มของแต่ละทีมอยู่ใน
`erp-review/2026-09-05/report-*.md` + `VERIFY-main.md` (สิ่งที่ main agent เปิดไฟล์ยืนยันเอง)
- **กติกาเดิมทุกข้อของ SYSTEM_REVIEW ใช้กับไฟล์นี้** (verify ก่อนเชื่อ · ติ๊ก `✅ <sha>` ไม่ลบแถว ·
  §"ตรวจแล้วไม่ใช่บั๊ก" ห้ามรายงานซ้ำ)
- ครบ 9 ทีม (F บางส่วน — ควรรันซ้ำ) · แก้แล้ว P0 6 + P1 15 ในคอมมิตชุดรอบ 135 · ที่เหลือเป็น backlog
  เรียงลำดับใน §1/§9 ของไฟล์นั้น · brief สำหรับรอบถัดไป: `erp-review/2026-09-05/BRIEF.md`
- ราก 9 ข้อที่ต้องซ่อมก่อนขยายเป็น ERP อยู่ใน §6 — **ห้ามเพิ่มโมดูลใหม่ทับรากที่ยังไม่ซ่อม**
  (โดยเฉพาะ: ชั้น posting เดียว · DocumentTypeRegistry/DocumentStatusRules · สิทธิ์ที่ server ·
  enum→UI จากแหล่งเดียว · Sales Order)
- helper ใหม่ที่ทุกเส้นต้องใช้แทนสำเนามือ: `Helpers/DocumentStatusRules` (แทน `== Approved`) ·
  `Helpers/StockMovementSign` (เครื่องหมาย StockMovement) · `PayrollRunEditPolicy.CanVoid` ·
  `Helpers/CashSaleStockRules` (ใบเสร็จ standalone ↔ สต๊อก/COGS ตาม `CashSaleStockPolicy`) ·
  `Helpers/ArApScope` (ชุดชนิดลูกหนี้/เจ้าหนี้ — ใบวางบิล**ไม่ใช่**ลูกหนี้) ·
  `Helpers/TipAccountResolver` (บัญชีทิป POS/TipPayout — ห้าม 216xx) ·
  ทุกทางเข้าอนุมัติเอกสาร (เว็บ/กฎ/ลายเซ็น/มือถือ/LINE) ต้องผ่าน `DocumentPermissionHelper.CanApproveAsync`
- helper กลางจากรอบ 193 (ทุกเส้นต้องใช้ ห้ามเขียนสำเนา): `Helpers/DepositPolicyResolver` (โหมดมัดจำ 3 แบบ) · `Helpers/ContactTaxBranchKey`
  (คีย์ผู้ติดต่อ = เลขภาษี+สาขา · `SoftScope` · `AdoptTaxId(..., ContactMatchKind)`) · `IIssuedDocumentHooks.RunAsync` (e-Tax หลังออกเอกสาร
  ทุกทางเข้า — หลัง commit) · `Helpers/CompanyVatStatus` + `CompanySettingsFactory` (ธง VAT · stopgap รอเจ้าของตัดสินต้นทาง) ·
  `Helpers/InputVatVehicleRule` (§82/5(6)) · `Helpers/OwnerActionGuard` + `[RejectApiKey]`/`[RequireOwner]` (งานระดับเจ้าของห้ามคีย์ API) ·
  `IAttachmentAccessGate` (ด่านไฟล์แนบ/สแกนตัวเดียว) · `Helpers/DocumentSignedContent` (ลายเซ็นลูกค้าผูก hash เนื้อหา) ·
  `AuditHashChain.Seal/Analyze` (hash chain canonical ตัวเดียว)

## 📕 SYSTEM_REVIEW_2026-09.md — ลิสต์งานจากการตรวจทั้งระบบ (8 ทีม)

`SYSTEM_REVIEW_2026-09.md` (root) คือผลตรวจทั้งระบบโดยทีมผู้เชี่ยวชาญ 8 ด้าน
(เอกสาร · onboarding/nav · บัญชี/ภาษี · payroll · OCR/AI · security/arch ·
frontend · โมดูลรอง) **180 ข้อ (P0 25 · P1 61 · P2 65 · P3 29)** พร้อม file:line
ทุกข้อ และ P0/P1 ผ่านการ verify ซ้ำโดย main agent — ใช้เป็น backlog หลัก

**สถานะ ณ 2026-09-04: ติ๊กแล้ว 84 แถว · P0 เหลือ 0 · P1 31 · P2 43 · P3 20**
สิ่งที่เหลือ**ไม่ใช่บั๊กที่แก้ได้ในที่เดียว**อีกแล้ว แบ่งเป็น 3 กอง — ต้องเลือก
อย่างตั้งใจ ไม่ใช่ไล่ทำตามลำดับ ID:
1. **ฟีเจอร์ใหม่** (ภ.ง.ด.50 export · ค่าเผื่อหนี้ TFRS บทที่ 9 · ไฟล์โอนเงินเดือน
   เข้าธนาคาร · หน้าเก็บ ปกส./PVD รายคน · ปฏิทินวันหยุดราชการ) — งานหลายวัน/ชิ้น
2. **re-design ที่ผลตรวจระบุเองว่าให้ประเมินแยก** (ยุบสองแดชบอร์ด · ชั้นสต็อก
   ทางเข้าเดียว · component layer · migration lock + `CONCURRENTLY`)
3. **ของที่ต้องตัดสินใจว่า "ต่อสาย หรือ ลบ"** (E-commerce 460 บรรทัดที่ไม่มี UI ·
   Open Banking ที่คอมเมนต์เขียนว่า "simulate" · `api.js` 101 เมธอดที่หน้าไม่เรียก)
   — **ห้ามเดาแทนเจ้าของโปรเจกต์** สองทางเลือกให้ผลต่างกันคนละเรื่อง

### กติกาการใช้

- **อ่าน §1 (20 ข้อแรก) + §2 (ต้นเหตุร่วม) + §3 (ลำดับ sprint) ก่อนลงมือ** —
  หลายข้อมีรากเดียวกัน แก้ที่รากหนึ่งครั้งปิดได้หลายข้อ
- **§9 คือรายการที่ตรวจแล้วไม่ใช่บั๊ก** — ห้ามรายงานซ้ำ ห้ามแก้
- ทุกข้อที่แก้ต้องปฏิบัติตามกฎเหล็ก #4 ครบ (reproduce → pure class + เทสต์ →
  checker negative test → sync DOCUMENT_FLOW/ACCOUNT_STRUCTURE/TEST_PLAN ในคอมมิตเดียว)
- เมื่อแก้ข้อใดเสร็จ ให้**ติ๊กในไฟล์นั้น** (เติม `✅ <sha>` หน้า ID) ไม่ลบแถว —
  เพื่อให้รอบถัดไปรู้ว่าอะไรปิดแล้ว ปิดที่คอมมิตไหน
- §10 คือส่วนที่ยังไม่ได้ตรวจ เรียงตามความเสี่ยง — ทีมตรวจรอบถัดไปเริ่มจากตรงนั้น

## 📓 DECISION_DOCTRINE.md — ตรวจอะไรก่อน · เมื่อไรถาม AI · เอาคำตอบกลับมาเรียนยังไง

`DECISION_DOCTRINE.md` (root) คือ **กติกากลางของ "ขั้นตอนการตัดสินใจ"** ทั้งระบบ — กลั่นจากโค้ดจริง
โดยทีม 3 ด้าน (ลำดับชั้นหลักฐาน · เกณฑ์โยนให้ AI · วงจรเรียนรู้) รอบ 177 · **อ่านก่อนเขียนตัวตัดสิน
(`Helpers/*Evidence.cs` · `*Guard.cs` · `*Policy.cs`) หรือก่อนเพิ่มจุดที่เรียก AI ใหม่ทุกครั้ง**
- **§1 ลำดับชั้นหลักฐาน (G1–G7)** — เรียงด้วย "ระยะห่างจากของจริง" ไม่ใช่ confidence ที่ผู้เสนอแต่งเอง ·
  "ไม่รู้" ต้องเป็นค่าใน enum · **เงื่อนไขที่เป็นเท็จเพราะไม่มีข้อมูล ห้ามตกเป็น "ผ่าน"** ·
  ทิศปลอดภัย = ทิศที่ความเสียหาย**มองเห็นและแก้ทัน** ไม่ใช่ทิศที่เงียบ
- **§2 ถาม AI ได้เมื่อครบ 4 ข้อพร้อมกัน** + ชั้นการรับคำตอบ 3 ชั้น + **เกณฑ์ต่างกันตามทิศของ
  ความเสียหาย** (silence-gate ≥0.70 + ต้องทิ้งร่องรอย · warn-gate ไม่มีขั้นต่ำ · write-gate ≥0.70
  + candidate set) · **ห้ามใช้ `HasModelAnswer` เดี่ยว ๆ** (tier-2 majority ไม่ดูอินพุตเลย)
- **§3 วงจรเรียนรู้** — คำตอบจริงอยู่ที่ "อนุมัติ" · เก็บสองทิศ · **กันคลังเอียง 5 ข้อ** ·
  ตัวชี้วัดที่แยก "local โตจริง" ออกจาก "ระบบเงียบลง"
- **§4 backlog ที่ยืนยันแล้ว** (V1 `OcrFieldArbiter` ไม่ได้ตัดสินแต่หน้าจอบอกว่าตัดสิน · GAP-1 สูตร
  จับคู่ธนาคารสองชุดที่ให้อันดับต่างกัน · kill-switch ที่ไม่ผ่าน ฯลฯ) · **§5 = ที่ตรวจแล้วไม่จริง
  ห้ามรายงานซ้ำ** · **§6 = 4 ข้อที่ต้องให้เจ้าของตัดสิน**

## 📗 DECISION_AUDIT_2026-09-18.md — ตรวจ "กระบวนการตัดสินใจ" ทั้งระบบ (8 ทีม) + แผนรอบพัฒนาถัดไป

`DECISION_AUDIT_2026-09-18.md` (root) คือผลตรวจรอบ 181 โดยทีม 8 ด้าน (เอกสาร/ด่านอนุมัติ · ยื่นภาษี · OCR · ธนาคาร/
จ่ายเงิน · สต็อก/สินทรัพย์ · เงินเดือน · สถาปัตยกรรม AI · POS/CMS/ที่พัก/ทางเข้าภายนอก) ตามโจทย์เจ้าของ "ตัดสินถูก
หลักการไหม · ลำดับขั้นถูกไหม · ครอบคลุมไหม · เรียก AI + เทรนให้ดีขึ้นเองครบไหม" — **main agent เปิดไฟล์ยืนยันทุกข้อ
P0/P1 ก่อนเขียน** (✅ ในตาราง) · กติกาเดียวกับ SYSTEM_REVIEW/ERP_REVIEW/DOCTRINE ทุกข้อ
- **§1 คำตอบ 5 ข้อ** · **§2 ต้นเหตุร่วม 7 แบบ (R1–R7)** — สถานะปลายทางประทับเอง · "ไม่รู้"→ค่าแต่ง · ตารางกฎหมาย
  สำเนาที่สอง · สองด่านในเมธอดเดียว · ทางเข้าอื่นไม่เดินด่าน · ลูปเรียนรู้ไม่ปิด/สอนตัวเอง · ไม่มีเทสต์ที่ด่านเงิน
- **§3 ผลตรวจรายทีม** (P0 ที่ยืนยันแล้ว: ด่าน WHT เก่ายังรันหลังด่านใหม่ · CIT 2 ตาราง ภ.ง.ด.50 ใช้ขั้น SME ทุกบริษัท ·
  Filed จากปุ่ม · OCR serialize บรรทัดก่อน Product master · VendorIntel สวมรอยกระดาษเรื่อง WHT · BankFeed Matched ไม่มีคู่ ·
  FIFO เศษ `Max(taken,1)` · ด่านสต็อกติดลบถูกข้ามด้วย override · POS ใบกำกับไม่หักส่วนลด · V1 เลข 13 หลัก = นิติบุคคล ·
  Integration VAT 7 ไม่ดู `IsVatRegistered` · sentinel `__USER_KEPT_EXISTING__` ไหลเข้าตัวแนะนำ GL · กุญแจคลังเขียน≠อ่าน
  เมื่อนักเรียนตอบ · JS 6 จุดไม่ส่ง `source`) · **§5 = ที่ตรวจแล้วไม่จริง/บรรทัดคลาด ห้ามรายงานซ้ำ** · **§7 = ไม่ใช่ปัญหา ห้ามแก้**
- **§6 แผนรอบถัดไป**: §6.0 เทสต์ก่อนแตะ (`ApprovalWarningGolden` · `BankMatchGolden` · FIFO/CIT/POS/Payroll · checker
  `terminal_status_writer`) → §6.1 P0 22 ข้อทำได้เลย → §6.2 P1 รายโดเมน → §6.3 P2 โครงสร้าง → **§6.4 = 9 ข้อที่เจ้าของ
  ต้องตัดสินก่อน ห้ามเดาแทน** (ฐาน ปกส. รวมเบี้ยเลี้ยง · `BuyerDeclinedTaxInvoice` ประทับเอง · ผลข้างเคียงถอดด่าน WHT เก่า ·
  VendorIntel auto-fill WHT · "ประกาศว่ายื่น" ต้องมีเลขรับไหม · ตาราง DTA · ขอบเขตฝากขาย/POC/LCNRV · วันเริ่มค่าเสื่อม ·
  ค้างจากรอบ 180)
- **ห้ามเพิ่มโมดูล/ฟีเจอร์ทับรากใน §2 ที่ยังไม่ซ่อม** — โดยเฉพาะ R1 (สถานะปลายทาง) และ R5 (ทางเข้าอื่น) เพราะทุกทางเข้า
  ใหม่จะสืบทอดช่องโหว่เดิม

## 📔 REGRESSION_ROOT_CAUSE_2026-09-18.md — ทำไมของที่เคยดีกลับแย่ลง + กลไกให้ระบบดีขึ้นเรื่อย ๆ

`REGRESSION_ROOT_CAUSE_2026-09-18.md` (root) คือผลตรวจรอบ 169 โดยทีม 4 ด้าน (โบราณคดีการถดถอย 33 กรณี ·
logic ซ้อน 10 หมวด · สายข้อมูล 8 ค่า · สถาปนิกกระบวนการ) ที่ main agent เปิดไฟล์ยืนยันทุกข้อ — **อ่าน §1 (คำตอบ 5 ข้อ)
+ §8 (หลักการ 10 ข้อ + checklist ก่อน push 12 ข้อ + ข้อห้าม 7 ข้อ — สำเนาอยู่ใน กฎเหล็ก #4 F2–F4) ก่อนเริ่มงานทุกรอบ** · บทเรียนดิบอยู่ `docs/lessons/`
- §6 = รายการที่ทีมรายงานมาแล้ว**ไม่จริง** — ห้ามรายงานซ้ำ · §7.3 = 2 เรื่องที่ต้องให้เจ้าของตัดสิน (เปิด host .NET ใน
  proxy policy · เปิด `claude/**` ใน workflow แบบแก้ "เสียง" ไม่ใช่ปิด "ด่าน") · §10 = backlog พร้อมป้ายว่าใครต้องตัดสิน
- เครื่องมือที่เกิดจากรอบนี้: `tools/check_all.sh` · `tools/callers.py` · `tools/dead_helper_check.py` (+ baseline) ·
  `tools/test_inventory.py` — กติกา ratchet: baseline **ห้ามเพิ่มแถว** เพื่อให้ checker เขียว มีแต่ตัดออกเมื่อต่อสาย/ลบแล้ว
- **คำตัดสินเจ้าของ (รอบ 170)**: (ก) **CI เปิดบน `claude/**` แล้ว** — หลัง push ต้องอ่านผล Actions ผ่าน MCP
  (`actions_list` → `get_job_logs`) แล้วแก้ก่อนรายงานผู้ใช้; job `test` รันเฉพาะ PR/main/dispatch (ข) เจ้าของจะเปิด host
  .NET ใน proxy policy — เมื่อ `command -v dotnet` เจอ `check_all.sh` จะ build/test ให้เอง (ค) **50 ทวิ ออกอัตโนมัติเป็น
  Issued ตอนจ่าย** ทุกทางเข้า — ยอดนำส่ง/ปฏิทิน/รายงาน/ไฟล์ยื่น/แดชบอร์ด อ่านจาก certs ผ่าน `Helpers/WhtCertFilingScope.Filed`
  ตัวเดียว · เอกสารหัก WHT ที่ไม่มี cert ออกจริง = ช่องโหว่ที่ต้องเตือน+บล็อกนำส่ง ห้ามนับเงียบ (ง) ยุบหมวด F เป็นหลักการ
  10 ข้อ + ย้ายบทเรียนดิบไป `docs/lessons/` (คอมมิตถัดไป)

## 📒 OCR_PIPELINE_REVIEW_2026-09-06.md — ไปป์ไลน์ OCR → เอกสาร (ทีมตรวจ 5 ด้าน)

`OCR_PIPELINE_REVIEW_2026-09-06.md` (root) คือผลตรวจ **เส้นทางตั้งแต่อัปโหลดจนได้
เอกสาร + JE** โดยทีม 5 ด้าน (สมองนักบัญชี · วิศวกรรมการสกัดข้อมูล · สถาปัตยกรรม
การเรียนรู้/AI · UX 1-click · คุณภาพ/ตัวชี้วัด) — โจทย์: "แค่อัพเอกสารไป ก็เหมือนมี
นักบัญชีที่เก่งที่สุดในโลกมาทำให้". รายงานดิบของแต่ละทีม:
`erp-review/2026-09-05/ocr-report-T1..T5.md` · โจทย์ที่ให้ทีม: `.../OCR-BRIEF.md`
- ใช้กติกาเดียวกับ SYSTEM_REVIEW/ERP_REVIEW ทุกข้อ (verify ก่อนเชื่อ · ติ๊ก `✅ <sha>`
  ไม่ลบแถว · §"ตรวจแล้วไม่ใช่บั๊ก" ห้ามรายงานซ้ำ)
- §2 = 16 ข้อที่แก้แล้ว · §3 = backlog เรียง P1/P2/P3 · §4 = สถาปัตยกรรมเป้าหมาย
  (Decision Record + Arbiter · student ต้องตอบได้ · ปิด loop ที่เอกสารที่อนุมัติ ·
  eval harness + KPI คู่) · §5 = แผน 5 เฟส · §6 = 3 คำถามที่ต้องให้เจ้าของตัดสิน
- helper ใหม่ที่ทุกเส้นต้องใช้: **`Helpers/OcrLineReconciler`** (กระทบยอด Σ บรรทัด ↔
  หัวใบ — ห้ามเขียนตรรกะ 4 เคสเองอีก) · `OcrAiAugmentationResult.HasModelAnswer`
  (ใช้แทน `UsedAi` ทุกจุดที่จะ **นำคำตอบไป apply**) ·
  **`Helpers/OcrPostingReadiness`** (ตัวตัดสิน "อนุมัติอัตโนมัติได้ไหม" ตัวเดียวของ
  ทุกช่องทาง — เว็บ/LINE/มือถือ ห้ามเขียนเกณฑ์เอง) · **`Helpers/OcrReviewGuard`**
  (กรองคำตอบ AI ก่อนแตะฟอร์ม — ยอดเงินรับเป็นชุดและต้องลงตัว) ·
  **`Helpers/OcrVendorKeyEvidence`** (หลักฐานว่า "เลขที่ใช้ค้นทะเบียนเป็นของผู้ขายจริง" —
  ป้ายกำกับ + ไม่ใช่เลขผู้ซื้อ/เลขเรา + ไม่ได้อยู่ในบล็อกผู้ซื้อ · ส่งผลให้
  `DbdIdentityGuard.Judge(..., keyProven:)` ซึ่งเป็น**ตัวตัดสินตัวเดียว**ของทั้งเส้น OCR
  และเส้น integration — สำเนา inline ใน `OcrService` ถูกถอดแล้ว) ·
  **`Helpers/InputVatAccountPolicy`** (ธงผังบัญชีปิดการเคลม §82/5 — ใช้ทั้งเส้นคีย์มือ
  และเส้น OCR) · **`Helpers/RawTextLineSplitter`** (แตกบรรทัดจากข้อความเมื่อไม่มีโมเดล
  — ต้องผ่าน `OcrLineSplitGuard` เสมอ) · **`Helpers/OcrTargetDocumentType`** (ชนิดเอกสาร
  ที่สแกนจะกลายเป็น — ด่านสิทธิ์กับเส้นสร้างเอกสารต้องใช้ตัวเดียวกัน **ห้ามคืน "ไม่รู้"**) ·
  **`Helpers/OcrPostedTruth`** (ช่องไหนของสแกนควร sync ให้ตรงเอกสารที่อนุมัติแล้ว —
  ห้ามลบค่าเดิมด้วยช่องว่าง/ศูนย์) · `AiResponse.FromLocalModel` (**ธงเดียวที่บอกว่า
  "นักเรียนตอบ"** — ห้ามเดาจาก `ProviderModel`/`Status` อีก) · ตารางอัตรา/ประเภทเงินได้ ม.40 อ่านจาก
  **`Helpers/ThaiWhtRateTable`** ตัวเดียว และหน้าเว็บสร้าง dropdown จาก
  `/api/reference/income-types` (ห้ามพิมพ์อัตราซ้ำใน JS)
- §2c = รอบ "เริ่มดำเนินการทั้งหมด" — ปิด backlog P1 อีก 13 ข้อใน 4 ชุด (ความทนทาน ·
  สมองนักบัญชี · การสกัดข้อมูล · UX) ดูตารางในไฟล์นั้น

### บทเรียนจากรอบ OCR — ย้ายไป `docs/lessons/ocr-pipeline.md`

บทเรียน defect class ของไปป์ไลน์ OCR ทั้งหมด (รวมที่เคยอยู่ท้ายไฟล์นี้) อยู่ที่ `docs/lessons/ocr-pipeline.md` —
กติกาเดียวกับ F5: append ที่นั่น · แตะ CLAUDE.md เฉพาะเมื่อเปลี่ยนหลักการ
