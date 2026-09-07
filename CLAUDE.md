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
      (รหัสสาขา §86/4, เครดิตเทอม, ส่วนลด, หน่วยนับ, จำนวนเงินตัวอักษร)
   3. **แพตเทิร์นที่เรียนไว้** — `DocumentZoneAnalyzer.ApplyLearnedPatternsTo`
      อ่าน `OcrLearnedPatterns` ของผู้ขายรายนั้น (เติมเฉพาะช่องที่ยังว่าง)
   4. **วิเคราะห์โซน** — `DocumentZoneAnalyzer.Analyze` ทำงานเมื่อ pipeline
      หลักไม่ได้ทั้งชื่อผู้ขายและยอดรวม
   5. **Local distillation model** (`OcrFullReviewDistillationModel` +
      `GlAccountDistillationModel` ฯลฯ — ตามกฎเหล็ก #1)
   6. **Historical lookup** — ถ้า `SellerTaxId` เคยมีในระบบ → autofill
      `SellerName/Address/BranchCode` จาก `Contact` ล่าสุดของ vendor นั้น
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

### F. เครื่องมือบังคับก่อน commit (env นี้ไม่มี .NET SDK — คอมไพล์ไม่ได้)

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
python3 tools/identifier_space_check.py # CS1001/CS1003 ช่องว่างในชื่อ method/ชนิด
python3 tools/namespace_shadow_check.py # CS0234 `Helpers.X` ผูกไป namespace ผิดชั้น
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
python3 tools/flag_field_overwrite_check.py # เขียนทับช่องข้อความที่เป็นที่สะสม**และ**มีด่านอ่านธงจากมัน → ธงของด่านหายเงียบ
node --check                           # ทุก <script> ใน .html ที่แก้
awk brace-balance                      # ทุก .cs ที่แก้
```
- **error ในโปรเจกต์หลัก = ล้มทั้ง solution** — `Accounting` คอมไพล์ไม่ผ่าน ทำให้
  `Accounting.Tests` พังตามด้วย CS0006 "Metadata file Accounting.dll could not
  be found" ทุกครั้ง (ไม่ใช่บั๊กแยก — หายเองเมื่อแก้ต้นเหตุ) _(ที่มา: CS0246
  `AuditHashChain.cs` ใช้ `AuditAction` (Models.Enums) แต่ import แค่
  Models.Entities; CS1739 `MapDocumentToResponse` ส่ง `IssuedAsCashReceipt:`
  ที่ยังไม่มีใน `DocumentResponse`)_
- **เทสต์ xUnit ต้อง public — ชนิดช่วยที่รับเป็นพารามิเตอร์ก็ต้อง public ตาม**
  `[Theory]`/`[Fact]` ต้องเป็น public method; ถ้าพารามิเตอร์เป็น `private enum`/
  `private record` ที่เขียนไว้ช่วยในคลาสเดียวกัน → **CS0051** ล้มทั้ง solution
  (ชนิดที่ใช้แค่ในตัว body ไม่เป็นไร — เฉพาะที่อยู่ใน "ลายเซ็น" เท่านั้น)
  _(ที่มา: `ReclassifyLineAccountTests.Nature` — จับด้วย `tools/accessibility_check.py`)_
- **ส่ง `xxx.Id` เข้าพารามิเตอร์ที่รับตัว entity = CS1503 ล้มทั้ง solution**
  helper ที่รับ `(Guid companyId, Document doc)` ถูกเรียกด้วย `(companyId, doc.Id)`
  และ `(companyId, allocDocId)` — สายตาอ่านผ่านง่ายมากเพราะ "ก็ส่ง id ของเอกสาร
  ไปนั่นแหละ" แต่คอมไพเลอร์ไม่ยอม. checker เดิมไม่มีตัวไหนดูชนิดอาร์กิวเมนต์เลย
  (`nullable_arg_check` ดูแค่ nullable→non-nullable) → เพิ่ม `tools/arg_type_check.py`
  ฟ้องเมื่ออาร์กิวเมนต์เป็นค่า id (`x.Id` หรือตัวแปรลงท้าย `...Id`) แต่พารามิเตอร์
  เป็นชนิด entity ใน `Models/Entities`
  _(ที่มา: `SyncWhtCreditReceivedAsync` 2 จุด — ผ่าน checker 6 ตัวเดิมทั้งหมด
  แล้วไปตายตอน build ฝั่งผู้ใช้)_
- **ลิงก์ไปหน้าที่ไม่มีอยู่จริง — ไม่มี compiler ตัวไหนจับให้** ปุ่ม "เปิด →"
  ในหน้าสำนักงานบัญชีพาไป `/pages/dashboard.html` ซึ่ง **ไม่เคยมีไฟล์นั้นอยู่
  จริงเลยสักวันเดียว** (แดชบอร์ดจริงคือ `/simple.html` หรือ `/app.html` ตาม
  `uiMode`) — ผู้ใช้เจอ ERR_INVALID_RESPONSE. พาธเดียวกันมีอีก 3 จุด (ตอบรับ
  คำเชิญ · ปุ่ม "กลับระบบหลัก" ใน Connect portal · ออกจาก POS) และเจอ
  `/pages/document-template.html` (เอกพจน์ ไฟล์จริงพหูพจน์) ในเช็กลิสต์เริ่มต้น
  ใช้งานอีก 1 จุด. เคยแก้ไปแล้ว **1 จุด** (pos.html) แต่ที่เหลือถูกทิ้งไว้ —
  defect class "แก้ตัวเดียว เหลือที่เหลือ"
  _(ลิงก์ที่ชี้ผิดเป็น string ที่ถูกต้องทางไวยากรณ์ทุกประการ ⇒ `node --check`
  และ checker ฝั่ง C# ทั้งหมดมองไม่เห็น ต้องเช็คกับระบบไฟล์เท่านั้น → เพิ่ม
  `tools/dead_link_check.py`. ปลายทางที่มีหลายหน้าให้ผ่าน resolver กลาง
  `Layout.dashboardUrl()` และ **ต้องทิ้งหน้า redirect ไว้ที่ URL เดิม** เพราะ
  ลิงก์ในอีเมลคำเชิญที่ส่งออกไปแล้ว/บุ๊กมาร์กของผู้ใช้ แก้ย้อนหลังไม่ได้)_
- **method ชื่อซ้ำใน object literal เดียวกัน = ตัวหลังทับตัวแรกเงียบ ๆ** เจอจริง
  2 จุดในรอบเดียว: `documents.html` มี `toggleVatClaim(el)` (ไอคอนเคลม VAT บน
  บรรทัดฟอร์ม) กับ `async toggleVatClaim(id, claim, el)` (หลังอนุมัติ ในหน้า
  detail) ใน `Page` เดียวกัน — กดไอคอนบนฟอร์มไปเรียกตัวหลังด้วย claim=undefined
  ⇒ เด้ง confirm "เลิกเคลมภาษีซื้อใบนี้?" กลางฟอร์ม + ยิง API ด้วย DOM element
  แทน id (ผู้ใช้ที่ตั้งใจ**เปิด**เคลมโดนถามว่าจะ**เลิก**เคลม); `projects.html`
  มี `openEdit` ซ้ำ — ตัว rename ด้วย `prompt()` ทับฟอร์มแก้ไขเต็ม ⇒ ปุ่มแก้ไข
  โครงการแก้ได้แค่ชื่อ วันที่/งบ/สัญญาแก้ไม่ได้เลยตั้งแต่เขียนมา. duplicate key
  ใน object literal **ถูกกติกา JS** — `node --check` ไม่ฟ้อง → เพิ่ม
  `tools/js_dup_method_check.py` (นับ key ที่ brace depth 1 ต่อ object)
  _(บทเรียนซ้อนตอนเขียน checker — ผ่าน 3 รุ่นกว่าจะจับของจริงได้ครบ: (1) เทียบ
  indent เฉย ๆ ฟ้องผิดใน object ซ้อนที่ indent ชั้นในเท่าชั้นนอก (translations)
  (2) นับ brace ด้วย regex ต่อบรรทัด พังกับ template literal ข้ามบรรทัด —
  **พลาดของจริง** (3) tokenizer ต้องรู้จัก **regex literal** ด้วย: เจอ
  `replace(/'/g, ...)` — quote ในตัว regex เปิด string ค้างแล้วกลืนโค้ดที่เหลือ
  ทั้งไฟล์ ตัดสิน regex-vs-หาร จาก token ก่อนหน้าแบบ minifier. ทุกรุ่นต้องผ่าน
  negative test กับบั๊กจริงทั้งสองตัวก่อนเชื่อ)_
- **หน้าเว็บอ่านคีย์ localStorage ที่ไม่มีใครเขียน = ทั้งหน้าตายเงียบ** เจอ
  พร้อมกันรอบเดียว 3 จุด: `pages/etax.html` อ่าน `'companyId'` (คีย์ของ portal
  `/connect` เท่านั้น แอปหลักไม่เคยเขียน) → ได้ null ทุกครั้ง → `window.location
  .href = '/pages/settings.html'` ⇒ **เมนู e-Tax Invoice กดแล้วเด้งไปหน้าตั้งค่า
  ตลอด เข้าไม่ได้เลยสักครั้งตั้งแต่เขียนมา**; `mobile-expense.html` คีย์เดียวกัน
  ⇒ ขึ้น "ต้อง login + เลือกบริษัทก่อน" ตลอด; `pages/signatures-logic.js` อ่าน
  `'selectedCompanyId'` ซึ่ง**ไม่มีที่ไหนเขียนเลยทั้งเรพ** ⇒ แท็บรออนุมัติว่าง
  และปุ่มอนุมัติ/ปฏิเสธ `return` เงียบ ๆ กดแล้วไม่มีอะไรเกิดขึ้น. resolver กลาง
  ตัวเดียวคือ `Layout.getCompanyId()` (อ่าน `currentCompany`) — **ห้ามอ่านคีย์เอง**
  _(เป็น string ถูกไวยากรณ์ทุกประการ → `node --check` และ checker ฝั่ง C# มองไม่
  เห็น ต้องเทียบ "ฝั่งอ่าน" กับ "ฝั่งเขียน" ทั้งเรพ → เพิ่ม
  `tools/localstorage_key_check.py`. บทเรียนซ้อนตอนเขียน checker: (1) ต้องตัด
  คอมเมนต์ก่อนสแกน ไม่งั้นหมายเหตุที่อธิบายบั๊กเก่าถูกนับเป็นการอ่านจริง —
  checker ฟ้องตัวเอกสารของตัวเอง (2) ตอนตัดคอมเมนต์ต้องคงจำนวนบรรทัดไว้ ไม่งั้น
  เลขบรรทัดที่ฟ้องเพี้ยนทั้งไฟล์ (3) กติกาต้องมีทิศทาง — หน้าแอปหลักอ่านคีย์ที่มี
  แต่ `/connect` เขียน = ผิด แต่ทางกลับกันปกติ ไม่งั้น `token`/`uiMode` ถูกฟ้องผิด)_
- **raw string หลายบรรทัด: ห้ามเขียนเนื้อหาต่อท้ายตัวเปิด = CS8997 ระเบิดทั้งไฟล์**
  เพิ่ม SQL migration แบบหลายบรรทัดโดยเขียน `UPDATE ...` ต่อท้าย `"""` เลย —
  C# บังคับว่า raw string ที่เนื้อหา**ข้ามบรรทัด** ตัวเปิดต้องตามด้วยขึ้นบรรทัด
  ใหม่ทันที และตัวปิดต้องอยู่บรรทัดของตัวเอง ⇒ ได้ CS8997 "Unterminated raw
  string literal" + error ลามทั้งไฟล์ 300+ รายการเหมือนเคส `$@"` ก่อนหน้า.
  ทางที่ปลอดภัยสุดในลิสต์ migration: **เขียน SQL บรรทัดเดียวจบ** (ทุกตัวใน
  ไฟล์นั้นเป็นแบบนี้อยู่แล้ว — อย่าแหวกแนว)
  _(checker `verbatim_string_check.py` เดิม **ข้าม raw string ทั้งก้อน** เพราะ
  quote เดี่ยวถูกกฎในนั้น จึงมองไม่เห็นกฎหลายบรรทัดเลย → เพิ่มการตรวจแล้ว.
  บทเรียนซ้อน: ตอนเขียนหมายเหตุนี้ลง docstring ของ checker เอง ผมใส่ `"""`
  ลงไปตรง ๆ จน docstring **Python** ปิดก่อนเวลา — กฎเดียวกันข้ามภาษา ห้าม
  พิมพ์ตัวคั่นสตริงลงในสตริงชนิดเดียวกัน)_
- **`"` เดี่ยวในคอมเมนต์ CSS/HTML ที่อยู่ใน `$@"..."` = ระเบิดทั้งไฟล์** เขียน
  คอมเมนต์ CSS ว่า `/* ผู้ใช้ขอ: "ให้ย้ายไปทั้งส่วน" */` ใน `BuildCss` ซึ่งเป็น
  verbatim interpolated string ยาวหลายร้อยบรรทัด — `"` ตัวแรก **ปิด string ทันที**
  ⇒ CSS ที่เหลือถูกอ่านเป็นโค้ด C# ⇒ error 300+ รายการ (CS1010 Newline in
  constant · CS1056 Unexpected character `—` · CS1040 preprocessor เพราะ `#` ใน
  โค้ดสี · CS1002/CS1513 อีกเป็นร้อย) โดย error **ตัวแรก** เท่านั้นที่ชี้บรรทัดจริง
  ที่เหลือชี้มั่วทั้งไฟล์ — อ่านจากท้ายรายการจะหลงทาง. ในสตริงแบบนี้ `"` จริงต้อง
  เขียน `""` หรือเลี่ยงไปใช้อัญประกาศไทย `“ ”` ในคอมเมนต์
  _(checker เดิม 7 ตัวมองไม่เห็นเลย: awk นับปีกกา — คอมเมนต์นี้ไม่มีปีกกา; ตัวอื่น
  อ่านโครงสร้างโค้ด ไม่มีตัวไหน tokenize string literal → เพิ่ม
  `tools/verbatim_string_check.py` ซึ่งต้อง track interpolation hole `{...}` ด้วย
  เพราะ `$@"{(x ? $"<b>{y}</b>" : "")}"` ถูกต้องตามภาษา — รุ่นแรกที่ไม่ track
  ฟ้องผิด 5 จุดในเรพ)_
- **เปลี่ยนชื่อตัวแปรต้องไล่ให้ครบทั้งเมธอด = CS0103** เปลี่ยน `postedJournalIds`
  (list ของ Guid) เป็น `postedJournals` (anonymous type) เพราะต้องใช้ `EntryDate`
  ด้วย แล้วลืมจุดใช้งานที่อยู่ห่างออกไป ~120 บรรทัดในเมธอดเดียวกัน — เมธอดยาว
  หลายร้อยบรรทัดทำให้ "อ่านทั้งเมธอด" ไม่เกิดขึ้นจริง: หลังเปลี่ยนชื่อ ให้ grep
  ชื่อเดิมทั้งไฟล์ทุกครั้ง (`grep -n 'ชื่อเดิม' <file>`) ก่อน commit
- **เพิ่ม field ระดับเอกสาร = แตะ record ทั้ง 3 ตัวเสมอ** — `CreateDocumentRequest`
  + `UpdateDocumentRequest` + **`DocumentResponse`** (ข้อ B ข้างล่างระบุลำดับไว้แล้ว)
  ลืมตัวใดตัวหนึ่ง: ลืม Response → CS1739 ตอน build; ลืมทั้ง mapper และ Response
  → ไม่มี error แต่ค่าหายเงียบตอน runtime (defect class "เก็บแล้วต้อง echo กลับ")
- **ช่องว่างในชื่อ method = CS1001/CS1003 ล้มทั้ง solution** โปรเจกต์นี้ตั้งชื่อ
  เทสต์เป็นภาษาไทย (อ่านง่ายมาก) แต่พลาดง่ายเป็นพิเศษ เพราะภาษาไทยเขียนติดกัน
  ไม่มีช่องว่าง — ยกเว้นตอนแทรกศัพท์อังกฤษ: `public void แก้ field ใดภายหลัง_...()`
  ช่องว่างรอบคำว่า `field` **จบ identifier ตรงนั้น** คอมไพเลอร์อ่านเป็น
  "return type = แก้ · ชื่อ = field" แล้วเจอ token เกิน ⇒ CS1001 ลามทั้งไฟล์ และ
  ล้ม `Accounting.Tests` ตามด้วย CS0006. checker 11 ตัวเดิมมองไม่เห็นเลย —
  ทุกตัวอ่านโครงสร้างระดับสูงกว่า (DI graph, ชนิดอาร์กิวเมนต์, string literal)
  ไม่มีตัวไหนตรวจ "ชื่อ" ระดับ token → เพิ่ม `tools/identifier_space_check.py`
  _(บทเรียนซ้อนตอนเขียน checker: รุ่นแรกฟ้อง **661 จุด** ทั้งที่เรพไม่มีบั๊กเลย
  สองสาเหตุ — (1) `private static readonly Guid Acc = Guid.NewGuid();` วงเล็บใน
  **ค่าเริ่มต้น** ถูกนับเป็นพารามิเตอร์ ต้องตัดที่ `=`/`{` ตัวไหนมาก่อนก่อนเสมอ
  (2) `record struct Foo(...)` มีคำนำหน้า **สองคำ** ป๊อปคำเดียวไม่พอ. และต้อง
  ยกเว้น `operator ==` ที่ตัดตรง `=` ไม่ได้ · ยุบช่องว่างใน `<...>` ก่อน ไม่งั้น
  `Dictionary<string, int>` ถูกนับเป็นสอง token)_
- **`Helpers.X` ผูกไป namespace ผิดชั้น = CS0234 ล้มทั้ง solution** `AuthService.cs`
  อยู่ใน `Accounting.Services.Implementations` เขียน `Helpers.PdpaPolicy` โดยคิดว่า
  C# จะไล่ขึ้นไปเจอ `Accounting.Helpers` — แต่เรพนี้**มี `Accounting.Services.Helpers`
  อยู่ด้วย** (BankCsvParser/BankExcelParser) C# ใช้กติกา "ชั้นใกล้ชนะ" จึงหยุดที่นั่น
  แล้วฟ้องว่าไม่มี `PdpaPolicy` — **ทั้งที่ไฟล์มี `using Accounting.Helpers;` อยู่แล้ว**
  (using ไม่ช่วยเลยกับการอ้างที่มีจุดนำหน้า) เกิด 7 จุดในเมธอดเดียว. กฎ: ในเรพนี้
  **อย่าเขียนชื่อแบบมีจุดนำหน้าบางส่วน** — ใช้ชื่อเปล่าผ่าน `using` หรือชื่อเต็ม
  `Accounting.Helpers.X` เท่านั้น
  _(`using_check.py` จับไม่ได้เพราะ using ครบอยู่แล้ว — ปัญหาคือชื่อผูกไปผิดที่
  ไม่ใช่หาไม่เจอ → เพิ่ม `tools/namespace_shadow_check.py`. บทเรียนซ้อน: รุ่นแรก
  ฟ้อง "กำกวม" ทุกจุดที่มีผู้สมัคร > 1 → ฟ้อง `Helpers.BankCsvParser` ใน
  `OpenBankingService.cs` ซึ่ง**ถูกต้องอยู่แล้ว** (ชั้นใกล้มีชนิดนั้นจริง) ต้อง
  ทำแผนที่ namespace → ชื่อชนิด แล้วฟ้องเฉพาะตอน "ชั้นที่ชนะไม่มี แต่ชั้นนอกมี")_
- **`var(--ตัวแปรที่ไม่มี)` = ปุ่มล่องหนที่ยังคลิกโดนได้** ปุ่ม "🗑 ลบทั้งหมด"
  ในหน้ารายละเอียดเอกสารตั้ง `style="background:var(--red-700)"` แต่ `--red-700`
  **ไม่เคยถูกประกาศที่ไหนเลย** — สเปก CSS ระบุว่าการอ้างตัวแปรที่ไม่มี = "invalid
  at computed-value time" ⇒ property กลายเป็น **initial value** (`transparent`)
  **ไม่ใช่**ตกไปใช้ค่าจากคลาส ⇒ `.btn-danger` ที่ตั้ง `color:#fff` ไว้ทำให้ได้
  ตัวหนังสือขาวบนพื้นใส บนพื้น modal สีขาว = **ปุ่มลบถาวรที่มองไม่เห็นแต่กดโดนได้**
  (ผู้ใช้เห็นเป็นแค่ช่องว่างระหว่างปุ่ม) สแกนทั้ง wwwroot เจอ **85 จุด / 11 ชื่อ**
  รวม `wht.html` ที่มีปุ่มลบล่องหนแบบเดียวกันอีก 2 จุด. แก้ด้วยการประกาศโทเคน
  ที่ขาดใน `:root` **ที่เดียว** → หายพร้อมกันทั้ง 85 จุด
  _(เป็น CSS ที่ถูกไวยากรณ์ทุกประการ — `node --check` ดูแต่ JS, checker อื่นอ่าน
  โครงสร้าง C#/DOM ไม่มีตัวไหน resolve โทเคนข้ามไฟล์ → เพิ่ม `tools/css_var_check.py`.
  บทเรียนซ้อน: ต้องตัดคอมเมนต์ก่อนสแกน ไม่งั้นหมายเหตุที่อธิบายบั๊กนี้ถูกนับเป็น
  การอ้างจริง (checker ฟ้องเอกสารของตัวเอง — ซ้ำรอย `localstorage_key_check`) และ
  ต้องไม่ฟ้อง `var(--x, ค่าเผื่อ)` ที่มี fallback เพราะผู้เขียนกันไว้แล้ว)_
- **`.modal-footer` ไม่มี `flex-wrap` บนเดสก์ท็อป** (มีเฉพาะใน `@media` มือถือ)
  + `justify-content:flex-end` ⇒ modal ที่มีปุ่มเยอะ (หน้ารายละเอียดเอกสารมีได้ถึง
  10 ปุ่ม) ล้นออกนอกกรอบ และส่วนที่ล้นหลุดออกทาง**ซ้าย** = ปุ่มแรก ๆ ถูกตัดหาย
  กดไม่ได้เลย. แก้ที่ `.modal-footer` จุดเดียว = ทุก modal ทั้งระบบปลอดภัยพร้อมกัน
  · แถบปุ่มที่ยาวจริงให้จัดกลุ่มด้วย `data-g="primary|more|danger"` + เมนู
  "⋯ เพิ่มเติม" (`_layoutDetailActions`) — **ปุ่มที่ลืมติดป้ายต้องถือเป็น primary**
  (ปุ่มใหม่ต้อง "เห็น" ไม่ใช่ "หาย") และคำสั่งอันตรายอยู่ท้ายเมนูหลังเส้นคั่น
- **ส่งตัวแปรที่ "มีอยู่ในเมธอดอื่น" เป็นอาร์กิวเมนต์ = CS0103 ล้มทั้ง solution**
  ตอนเพิ่มพารามิเตอร์ `issuer` ให้ `ComposeFooter` ผมไปเติมอาร์กิวเมนต์ที่จุดเรียก
  ใน `RenderDocumentPdfNative` โดยเข้าใจว่าเมธอดนั้นมีตัวแปร `issuer` อยู่แล้ว —
  **แต่มันอยู่คนละเมธอด** (`ComposeHeaderAndTitle` คำนวณเองข้างใน) ⇒ CS0103 +
  `Accounting.Tests` พังตามด้วย CS0006. อันตรายเป็นพิเศษเพราะชื่อนั้น**มีอยู่จริง
  ในไฟล์** (grep เจอเต็มไปหมด) สายตาจึงอ่านผ่าน. เป็นญาติของกฎ "เปลี่ยนชื่อตัวแปร
  ต้องไล่ให้ครบทั้งเมธอด" แต่คนละทิศ: อันนั้นลืมแก้จุดใช้ อันนี้เติมจุดใช้ในเมธอดที่
  ไม่มีตัวแปร → เพิ่ม `tools/undeclared_local_check.py`
  _(checker 14 ตัวเดิมมองไม่เห็นเลย — ทุกตัวอ่านโครงสร้างระดับสูงกว่า ไม่มีตัวไหน
  ถามว่า "ชื่อนี้ประกาศไว้ที่ไหน"; `arg_type_check` ดูชนิดแต่ต้องมีตัวแปรจริงก่อน.
  บทเรียนซ้อนตอนเขียน checker — ผ่าน 4 รุ่นกว่าจะฟ้อง 0 จุดบนเรพที่ไม่มีบั๊ก:
  (1) regex จับหัวเมธอดพังกับ **return type ที่เป็น tuple** `(bool Ok, string R)`
  ซึ่งมีวงเล็บในตัวเอง → ล็อกผิดเมธอด ฟ้อง 43 จุด; เปลี่ยนเป็น scan หา `(` ที่ปิดแล้ว
  ตามด้วย `{` (2) **local function ต้องไม่ตรวจแยก** — มันเห็นตัวแปรของเมธอดแม่
  (closure) แยกเมื่อไรจะฟ้องทุกตัวที่ capture มา (3) ต้อง over-approximate ฝั่ง
  "ประกาศแล้ว" ให้กว้าง (อะไรที่ถูก `=` ที่ไหนก็ตามในเมธอด · `foreach (var (a,b) in …)`
  · property pattern `is { } x` · พารามิเตอร์ tuple ของ local function) — พลาดฝั่งนี้
  ไม่ทำให้พลาดบั๊กจริง เพราะตัวที่พลาดจะไม่ถูกกำหนดค่าที่ไหนเลยอยู่แล้ว (4) `var (a,b)=`
  · `typeof(T)` · `is not (A or B)` ไม่ใช่ "การเรียกเมธอด" ต้องตัดออกก่อน)_
- **"แอดมินของบริษัท" ≠ "แอดมินของแพลตฟอร์ม" — ใช้ธงเดียวกันไม่ได้** เมนู
  "รายงานการใช้งาน AI" และ "การใช้งานรายบริษัท" (แสดงข้อมูล **ข้ามบริษัท**) ติดธง
  `adminOnly: true` แต่ layout.js เช็คธงนั้นกับ `myPermissions.isOwnerOrAdmin`
  ซึ่งแปลว่า "เจ้าของ/แอดมิน **ของบริษัทนี้**" = **ลูกค้าทุกรายที่เปิดบริษัทเอง**
  ⇒ ลูกค้าเห็นเมนูของแพลตฟอร์มโผล่ในแถบซ้ายตัวเอง. ข้อมูลไม่รั่วเพราะ controller
  ใต้ `/api/admin/*` บังคับ `[Authorize(Roles = "SystemAdmin")]` (กดแล้วได้ 403)
  แต่เป็นการเปิดเผยหน้าจอภายใน และห่างจากการรั่วจริงแค่ "ลืม attribute หนึ่งบรรทัด"
  → แยกเป็นธง `platformAdmin` ที่ผูกกับ `IsSystemAdmin` (ค่าจากเซิร์ฟเวอร์เท่านั้น
  ห้ามอ่าน localStorage ที่ผู้ใช้แก้เองได้) + เพิ่ม `tools/admin_menu_gate_check.py`
  _(checker ต้องโยง **สามชั้น**: รายการเมนูใน layout.js → ไฟล์ HTML ปลายทาง →
  endpoint ที่หน้านั้นเรียก — ไม่มี checker ตัวไหนก่อนหน้านี้อ่านข้ามชั้นแบบนั้น.
  ตอนรันครั้งแรกมันเจอของแถมอีก 3 จุดที่เป็นบั๊กจริงคนละทิศ: `AdminAccountSubscription
  Controller` มีแต่ `[Authorize]` ระดับคลาส (พึ่งการเช็ครายเมธอด — ครบอยู่ แต่เพิ่ม
  endpoint ใหม่แล้วลืมบรรทัดเดียว = License ของลูกค้าทุกรายหลุด) และ **หน้าของลูกค้า
  สองหน้ายิงไป endpoint ของแอดมิน** ⇒ ลูกค้าอัปโหลดตราประทับไม่ได้ (403) และหน้า
  "License ของฉัน" โชว์รายการแพ็กเกจว่างเปล่าโดยไม่มีอะไรบอกว่าทำไม)_
- **อัปโหลดสำเร็จ แต่ static handler ตอบ 404 = "รูปไม่ขึ้น" ที่ไล่ต้นเหตุยากมาก**
  โลโก้ "ชื่อทางการค้า" ถูกเขียนลง `wwwroot/uploads/brand-logos/{companyId}/…`
  และ DB เก็บ URL ถูกทุกตัวอักษร — แต่ middleware ใน `Program.cs` เป็น **allow-list**
  (`/uploads/**` อะไรที่ไม่อยู่ในลิสต์ → 404 ทิ้ง เพื่อกันเอกสารแนบ/สแกน OCR/ไฟล์
  e-Tax หลุด) และ `brand-logos` ไม่เคยถูกเพิ่มเข้าลิสต์ ⇒ โลโก้แบรนด์ 404 ทุกไฟล์
  ตั้งแต่วันแรก. ตรวจรอบเดียวกันเจออีก 2 จุด: `/uploads/slips` (สลิปค่าบริการ) และ
  ไฟล์ที่ AdminController เขียนลง **ราก** `/uploads/` (โลโก้/ไอคอนของแพลตฟอร์ม)
  → แก้ที่ลิสต์ + เปิดเฉพาะ "ไฟล์ระดับรากของ /uploads" (ไม่ลามถึงโฟลเดอร์ย่อยที่เป็น
  ข้อมูลลูกค้า) + เพิ่ม `tools/upload_route_check.py`
  _(ทั้งฝั่งอัปโหลดและฝั่ง DB ถูกต้องหมด ผิดแค่ "เส้นทางที่ยอมให้เสิร์ฟ" ซึ่งอยู่คนละ
  ไฟล์กับโค้ดที่เขียนรูป — ไม่มี checker ตัวไหนก่อนหน้านี้โยงสองที่นี้เข้าหากัน.
  กติกาของ checker: web prefix ทุกตัวที่ส่งเข้า `ProcessAndSaveAsync` (ตัวประมวลผล
  รูปที่คืน URL ให้เบราว์เซอร์) ต้องอยู่ใน `publicUploadPrefixes` — เว้นโฟลเดอร์ที่
  ตั้งใจให้เป็นส่วนตัวและมี endpoint ตรวจสิทธิ์ของตัวเอง (`/uploads/attachments`))_
- **พรีวิวต้องเดินลำดับเดียวกับ renderer จริง** — พรีวิวหัวเอกสารในหน้าตั้งค่าแบรนด์
  อ่านแค่ `brand.logoUrl` แล้วโชว์กล่องว่างเมื่อแบรนด์ไม่มีโลโก้ ทั้งที่ PDF จริงตกไป
  ใช้ **โลโก้บริษัท** ตาม `Pick(b.LogoPath, companyLogoPath)` ⇒ ผู้ใช้เห็นพรีวิวไม่ตรง
  กับกระดาษ. พรีวิวคือ renderer ตัวที่สาม — กฎ "สอง renderer ห้าม drift" ครอบถึงมันด้วย
- **รายการที่ "คัดลอกมาด้วยมือ" จากรายการหลัก = drift แน่นอน แค่รอเวลา** หน้า
  จัดการ Role มี `MENU_SECTIONS` เป็นสำเนามือของ `Layout.navItems` — เมนูที่เพิ่ม
  ทีหลัง (ภ.พ.30 ย้อนหลัง · ภาษีซื้อยังไม่ถึงกำหนด · ภาษีถูกหัก · นำส่งภาษี ·
  อากรแสตมป์ · ทะเบียนสาขา ฯลฯ รวม **~50 เมนู**) ไม่มีช่องให้ติ๊กเลย ⇒ สมาชิก
  role กำหนดเอง (strict mode) **ไม่มีวันเห็นเมนูเหล่านั้น และแอดมินเปิดให้ไม่ได้**
  ผู้ใช้รายงานเป็น "ตั้งสิทธิ์แล้วแต่ยังไม่เห็นเมนู". กลไกที่แก้: **สร้างรายการจาก
  `Layout.navItems` ตอน runtime** (roles.html โหลด layout.js อยู่แล้ว) — เพิ่มเมนู
  ที่เดียว ช่องติ๊กตามทันที drift เป็นศูนย์โดยโครงสร้าง แรงกว่า checker
  _(ตรวจด้วย harness ที่ vm-load ทั้ง layout.js + IIFE จริงจาก roles.html:
  ก่อนแก้ editor มี 57 ช่อง เมนูจริง 104 — หลังแก้ 104/104)_
- **"มองไม่เห็นเมนู" ≠ "ไม่มีสิทธิ์" — มี 4 ชั้นที่ชนะ role เงียบ ๆ** ไล่ด้วย harness
  ที่รัน `Layout._refreshNavMenu()` จริงบน DOM ปลอม: (1) `ownerHiddenMenuIds`
  ซ่อนทั้งบริษัท (2) ผู้ใช้กดซ่อนเองใน localStorage (3) `vatOnly`/`etaxOnly` ตามสถานะ
  บริษัท (4) **หมวดเมนู default "พับ" ทุกหมวด** — ตัวสุดท้ายคือเหตุที่พบบ่อยสุดกับ
  ผู้ใช้ทั่วไป (สิทธิ์มาครบ เมนูอยู่ในจอ แต่ซ่อนใต้หัวหมวดที่ต้องกดก่อน) → สมาชิก
  custom-role ให้หมวด default เปิด (เมนูเขาคือ shortlist ที่คัดแล้ว) ส่วนผู้ใช้สิทธิ์
  เต็ม 60+ เมนูยังพับเหมือนเดิม และหน้า Role ติดป้ายเตือนบนช่องติ๊กที่เข้าข่าย
  ชั้น 1-3 พร้อมบอกที่ปลด — ห้ามให้แอดมินติ๊กแล้วต้องเดาเองว่าทำไมไม่ขึ้น
- **"checksum ผ่าน" ≠ "เป็นเลขนั้นจริง" — เลข 13 หลักบนกระดาษไม่ได้มีแค่เลขผู้เสียภาษี**
  ใบกำกับร้านวัสดุ (PI-20260820-0005) ถูกต้องครบทุกอย่างแต่ขึ้นเตือน 3 ข้อ รวม
  "อาจอัพโหลดผิดบริษัท" โดยอ้างเลขผู้ซื้อ `8885009199627` ที่**ไม่เคยมีบนกระดาษ** —
  มันคือบาร์โค้ดสินค้า EAN-13 ในตารางรายการที่ OCR อ่านเพี้ยน แล้วบังเอิญผ่าน mod-11
  ไทย (เลขสุ่มผ่าน ~1/10 ⇒ หน้าที่มีบาร์โค้ด 10 ตัว = แทบการันตีว่าจะมีตัวหนึ่งหลุด)
  และบาร์โค้ดจริงอีกตัว `8859991446166` ผ่าน**ทั้ง** mod-11 ไทยและ EAN-13 ⇒ ตัวเลข
  อย่างเดียวแยกไม่ออก **ต้องใช้บริบท** (ป้าย "เลขประจำตัวผู้เสียภาษี" นำหน้า / ตำแหน่ง)
  เป็นตัวตัดสินหลัก. เจอ defect ซ้อนอีก 4 ตัวในเส้นทางเดียวกัน: (1) regex ใช้ `[-\s]?`
  เป็นตัวคั่นซึ่ง `\s` **ครอบ `\n`** ⇒ เลขท้ายบรรทัดถูกต่อกับเลขต้นบรรทัดถัดไปเป็นเลข
  13 หลักที่ไม่มีใครพิมพ์ลงกระดาษ (2) `buyerPos = text.Length` เมื่อไม่เจอคำว่า "ผู้ซื้อ"
  = สมมติว่า "ผู้ซื้ออยู่ท้ายหน้า" ⇒ เลข 13 หลักตัวสุดท้ายของหน้าเป็นเลขผู้ซื้อทุกใบ —
  **ค่า default ที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้ อันตรายกว่าการไม่ตอบ** (3) Rule 7 ดูแค่
  ช่องที่ OCR เดามา ไม่เคยเปิดดู raw text ⇒ ขัดกับ Rule 3 ที่บอกว่า "พบเลขผู้ซื้อแล้ว"
  บนใบเดียวกัน — **กฎสองข้อที่มองข้อมูลคนละชุดจะเถียงกันเองต่อหน้าผู้ใช้เสมอ** (4)
  checksum เดียวกันถูกเขียนซ้ำ **5 ที่** (`SmartFieldExtractor` · `ThaiTaxIdValidator` ·
  `ImportReviewHeuristics` · `TaxInvoiceCompletenessChecker` · `RdComplianceValidator`
  ที่ผ่อนเป็น "13 หลักอะไรก็ได้") → ยุบเป็น `Helpers/ThaiTaxId.cs` ตัวเดียว
  _(ไม่มี checker ตัวไหนจับได้ — ทุกอย่างเป็นตัวเลขที่ถูกไวยากรณ์และผ่านคณิตศาสตร์
  ที่เขียนไว้ กลไกที่กันตัวถัดไปคือ **เทสต์ที่ใช้เลขจริงจากใบที่พัง** + simulation ที่มี
  negative test พิสูจน์ว่า reproduce คำเตือนเดิมได้ครบก่อน แล้วค่อยดูว่าหลังแก้เงียบ —_
  _บทเรียนซ้อน: simulation รุ่นแรก reproduce ไม่ได้เพราะ regex ข้ามบรรทัดกลืนเลขจน_
  _บาร์โค้ดหายไปจากผู้สมัครทั้งหมด ถ้าเชื่อผลรุ่นแรกจะสรุปว่า "ไม่มีบั๊ก" ทั้งที่ผู้ใช้_
  _เห็นอยู่กับตา — **negative test ที่ไม่ผ่าน = จำลองผิด ห้ามข้ามไปดูผลหลังแก้**)_
- **regex ที่ใช้ `\s` เป็นตัวคั่นระหว่างกลุ่มตัวเลข = กลืนขึ้นบรรทัดใหม่เงียบ ๆ**
  ต่อจากข้อบน — หลังแก้ `SmartFieldExtractor` เสร็จแล้ว grep ทั้งเรพพบว่า pattern
  ตัวเดียวกันถูก **คัดลอกไปวางอีก 4 ไฟล์** (`OcrService` · `FieldPatternLibrary` ·
  `DocumentZoneAnalyzer` 3 จุด) และรูปแบบเดียวกันยังใช้กับ **เบอร์โทร · เลขบัญชี ·
  เลขที่เอกสารในสเตทเมนต์ธนาคาร · ตัวกรอง PII ก่อนส่งเข้า AI** รวม 13 จุด/7 ไฟล์ —
  ถ้าหยุดที่ "แก้ตรงที่ผู้ใช้เจอ" ก็เหลือบั๊กเดียวกันอีก 12 จุดรอเวลา (defect class
  "แก้ตัวเดียว เหลือที่เหลือ" ซ้ำรอย `/pages/dashboard.html`). ที่ร้ายกว่า: จุดหนึ่ง
  คือ `AiPromptSanitizer` ซึ่งเป็น **control ด้าน PDPA** — match ข้ามบรรทัดที่นั่น
  แปลว่า mask ไปกินตัวเลขคนละชุดจนพรอมป์ต์ที่ส่งออกไปเพี้ยน. แก้: ยุบ pattern เลข
  ผู้เสียภาษีเข้า `ThaiTaxId.Pattern` ตัวเดียว + เปลี่ยนตัวคั่นทุกที่เป็น `[- \t]?` +
  **ล้างของที่ค้างในฐานข้อมูล** (`OcrLearnedPatterns.ExtractionRegex` เก็บ regex ลง
  DB ⇒ แก้โค้ดอย่างเดียวไม่พอ แถวเก่ายังถือ pattern เดิมตลอดไป)
  _(เพิ่ม `tools/regex_line_span_check.py` — ฟ้องเฉพาะ character class ที่มี `\s`_
  _และ**ติดกับ atom ตัวเลข** พร้อม quantifier แบบตัวคั่น (`?`/`*`/`{0,n}`) จึงไม่ไป_
  _ฟ้อง `\s*` ระหว่างคำอย่าง `Account\s*No\.?` ที่ตั้งใจให้ข้ามบรรทัดจริง ·_
  _negative test: ใส่ pattern เดิมกลับเข้าไปแล้วต้องจับได้ + ไฟล์สังเคราะห์ที่มี_
  _3 รูปแบบผิด (เลขภาษี/เบอร์โทร/กลุ่มซ้ำ `(?:[-\s]?[0-9]){12}`) และ 4 รูปแบบถูก_
  _ต้องแยกออกจากกันได้ครบ)_
  _บทเรียนซ้อนตอนเขียน migration: `LIKE '%[-\s]?%'` **ไม่ match อะไรเลย** เพราะ_
  _`\` เป็น escape char โดยปริยายของ LIKE ใน Postgres ⇒ ถูกอ่านเป็น `%[-s]?%` —_
  _migration ที่ "รันผ่าน" แต่ไม่แตะแถวไหนเลย มองไม่ออกจาก log ต้องเทียบเท่ากันเป๊ะ_
- **"ป้ายกำกับอยู่ก่อนค่าเสมอ" เป็นสมมติฐานที่ผิดกับแบบฟอร์มพิมพ์สำเร็จ** ต่อจาก
  สองข้อบน — ตอนแก้บาร์โค้ดผมเพิ่มกติกา "เลขที่มีป้าย *เลขประจำตัวผู้เสียภาษี*
  นำหน้าชนะเลขที่ไม่มีป้าย" แล้วมองย้อนหลังอย่างเดียว. ใบเสร็จ/ใบกำกับเล่มมีสำเนา
  (หจก.สหกลชลบุรี เลขที่ 0339) พิมพ์เส้นประให้เขียนแล้ววาง**ป้ายไว้ใต้เส้น** ⇒ เลข
  ผู้ซื้ออยู่บรรทัด**ก่อน**ป้าย ถูกตัดสินว่า "ไม่มีป้าย" แล้วแพ้เลขผู้ขาย ⇒ ช่อง
  ผู้ซื้อว่าง แล้วเตือนว่า "ควรระบุเลขผู้เสียภาษีของผู้ซื้อ" ทั้งที่กระดาษมีเลข —
  **ผมสร้างบั๊กใหม่ตอนแก้บั๊กเก่า** จับได้เพราะเขียน simulation ที่มี negative test
  กับกระดาษอีกแบบก่อนเชื่อว่าแก้จบ. บทเรียนซ้อน: พอเปิดให้มองไปข้างหน้าด้วย
  **บาร์โค้ดบรรทัดแรกของตารางสินค้าติดธง "มีป้าย" โดยบังเอิญ** (อยู่ห่างจากบล็อก
  เลขผู้ซื้อไม่กี่สิบตัวอักษร) แล้วรอดด่านคัดบาร์โค้ดที่เขียนไว้ว่า
  `c.Labelled || !LooksLikeProductBarcode(...)` ⇒ **ด่านที่อิง "ความใกล้" ห้ามใช้
  เป็นข้อยกเว้นของด่านที่อิงคุณสมบัติของตัวข้อมูลเอง** — เลขที่ผ่าน EAN-13 และมี
  GS1 prefix คือบาร์โค้ด ไม่ว่าข้อความรอบ ๆ จะเขียนว่าอะไร
- **กฎ compliance ที่คัดลอกไปเขียนใหม่ใน JS = เตือนผิดต่อหน้าผู้ใช้ตลอดไป**
  `document-scan.html` มีสำเนามือของกฎ `RdComplianceValidator` เขียนด้วย JS บน
  การ์ดผลสแกน — ตัดสินจาก `scan.buyerTaxId` ช่องเดียว ไม่เคยเปิดดู raw text และ
  ไม่แยกฝั่งซื้อ/ขาย ⇒ ทุกอย่างที่แก้ฝั่งเซิร์ฟเวอร์ไปไม่มีผลกับการ์ดเลย (ผู้ใช้
  ยังเห็นคำเตือนเดิมบนใบที่กระดาษครบ) และคำเตือน "อาจเป็นเอกสารของบริษัทอื่น" ก็
  ค้างเป็นรุ่นก่อนแก้. ย้ายมาคำนวณที่เซิร์ฟเวอร์ตัวเดียว
  (`OcrScanComplianceEvaluator`) ส่งเป็น `complianceIssues` ให้หน้าเว็บ **แสดง**
  อย่างเดียว — drift เป็นศูนย์โดยโครงสร้าง (กลไกเดียวกับที่ใช้กับ `MENU_SECTIONS`)
  _(เจอของแถม: JS เดิมต่อค่าที่ OCR อ่านมาเข้า HTML **โดยไม่ escape** — XSS จาก_
  _ค่าที่กระดาษ/OCR คุมได้ ผิดกฎเหล็ก #4 C · ฝั่งใหม่ผ่าน `Layout.esc` ทุกจุด)_
  _ข้อควรระวังของสัญญา: `undefined` (เซิร์ฟเวอร์เก่ายังไม่ส่งฟิลด์) ต้องแปลว่า_
  _"ยังไม่ได้ตรวจ — อย่าวาดอะไร" ไม่ใช่ "ไม่มีปัญหา" ซึ่งเป็นความหมายของ `[]`_
- **สำเนามือฝั่ง JS ที่ "ตามหลังอยู่ไม่กี่ธง" อันตรายกว่าสำเนาที่ผิดชัด ๆ** —
  `Layout.docHeaderLabel` เป็นสำเนาของ `PdfGenerationService.ComputeDocumentTitle`
  ที่รู้จัก **3 ธงจาก ~8 เงื่อนไข** จึงถูกในเคสที่พบบ่อยที่สุด (ใบกำกับธรรมดา)
  และเนียนมาตลอด — แต่เพี้ยน 6 เคส และสองเคสในนั้นไม่ใช่ "ป้ายผิด" เฉย ๆ:
  ผู้ซื้อ §86/4 ไม่ครบ → กระดาษพิมพ์ **"ใบกำกับภาษีอย่างย่อ"** (ผู้ซื้อเคลม
  ภาษีซื้อไม่ได้ §82/5(2)) แต่จอบอก "ใบกำกับภาษี" · ใบเสร็จที่มี VAT → กระดาษ
  พิมพ์ "ใบกำกับภาษี/ใบเสร็จรับเงิน" แต่จอบอกแค่ "ใบเสร็จรับเงิน" ⇒ **ผู้ใช้
  ตัดสินใจส่งเอกสารให้ลูกค้าจากชนิดเอกสารทางกฎหมายที่ผิด**. อีก 4 เคสคือชื่อหัว
  ที่ผู้ใช้ตั้งเองใน settings · `template.CustomTitle` · `(เงินมัดจำ)` ·
  `IssuedAsCashReceipt` ที่ JS **สลับลำดับคำ** ("ใบกำกับภาษี/ใบเสร็จรับเงิน"
  แทน "ใบเสร็จรับเงิน/ใบกำกับภาษี" ที่ต้องตรงกับ e-Tax T03)
  → แก้แบบเดียวกับ `complianceIssues`/`MENU_SECTIONS`: **เซิร์ฟเวอร์ส่งค่าที่
  คำนวณแล้วมา หน้าเว็บแสดงอย่างเดียว** (`DocumentResponse.DocumentTitle`)
  _(สองบทเรียนซ้อน — (1) **ทำ batch โดยไม่สร้างอัลกอริทึมสำเนาที่สอง**: เส้นทาง_
  _"หลายใบ" อยากได้ resolver ที่ไม่ยิง query ต่อแถว วิธีที่ผิดคือเขียนตัวเลือก_
  _เทมเพลตแบบง่าย ๆ ขึ้นมาใหม่ (= drift รอบหน้า) วิธีที่ถูกคือแยก **ตรรกะล้วน**_
  _(`PickTemplate`) ออกจาก **การโหลดข้อมูล** (`LoadTemplatePoolAsync`) แล้วให้ทั้ง_
  _เส้นเดี่ยวและเส้น batch เรียกตัวเดียวกัน — เส้นพิมพ์ PDF ได้ query น้อยลงด้วย_
  _(2) **ค่าที่เซิร์ฟเวอร์คำนวณมาเป็น "สถานะปัจจุบัน" ห้ามเอาไปตอบคำถามสมมติ**:_
  _ข้อความ "ถ้าจ่ายครบ หัวจะเปลี่ยนเป็น …" ส่ง `{servedAsReceipt:true}` เข้า_
  _resolver — ถ้าไม่ล้าง `documentTitle` ทิ้ง จะได้หัว**เดิม**กลับมาแล้วประโยค_
  _กลายเป็น "จะเปลี่ยนเป็น <ค่าที่ไม่เปลี่ยน>" ทุกเคสที่ short-circuit)_
- **"ค่าที่คงที่ต่อ X" กับ "ค่าที่เปลี่ยนทุกใบ" ห้ามอยู่ในถังเดียวกัน**
  `VendorKnownGoodCorrector` เก็บ "ค่าที่รู้ว่าถูกต่อผู้ขาย" ไว้ซ่อมผลอ่าน OCR ที่
  เพี้ยน — ถูกต้องสำหรับ **ชื่อ/ที่อยู่** แต่ในลิสต์เดียวกันมี **`DocumentNumber`**
  ปนอยู่ด้วย. เลขที่เอกสารเป็นค่า **"ต่อใบ"** ⇒ เลขรันของผู้ขายรายเดียวกันต่างกัน
  หลักเดียว ได้ similarity **0.83–0.92 เกินเกณฑ์ 0.80 เสมอ** ⇒ ใบใหม่ถูกเขียนทับ
  ด้วยเลขของ**ใบก่อนหน้า** (simulation เลขจริง 8 รูปแบบ: ทับ 6/8) ⇒ รายงานภาษีซื้อ
  §87 ยื่นเลขใบกำกับผิด + ด่านกันสแกนซ้ำตีว่าเป็นใบเดิม. **รหัสสาขา §86/4** ยิ่ง
  ชัด: 5 หลักต่างกัน 1 ตัว = ratio **0.80 พอดี** ⇒ ผ่านทุกคู่ ⇒ ใบของสาขาถูกเขียน
  เป็นสำนักงานใหญ่ทุกใบ
  → กติกา: fuzzy-correct ได้เฉพาะช่องที่ **คงที่ตลอดชีวิตของ key** และเป็น
  **ข้อความ** (มี "การสะกด" ให้ซ่อม) — **ตัวเลขล้วนไม่มีการสะกดผิด มันถูกหรือผิด
  เท่านั้น การเดาคือการแต่งข้อมูล** (ญาติของกฎ "ค่า default ที่แต่งขึ้น") ·
  ตัวตัดสินอยู่ที่เดียว `Helpers/VendorKnownGoodFields` ให้ทั้งฝั่งเขียนและฝั่งอ่านใช้
  _(สองบทเรียนซ้อน — (1) **threshold ที่ตั้งจากตัวอย่างชนิดเดียวจะพังกับชนิดอื่น**:_
  _0.80 ถูกจูนมาจากชื่อไทยยาว ๆ ที่ OCR อ่านเพี้ยน พอเอาไปใช้กับสตริงสั้น/ตัวเลข_
  _มันกลายเป็น "ต่างกัน 1 ตัวก็ยังผ่าน" โดยอัตโนมัติ — ก่อนใช้ threshold เดิมกับ_
  _ข้อมูลชนิดใหม่ ให้คำนวณว่า "ต่างกันกี่ตัวถึงจะตก" ที่ความยาวจริงของชนิดนั้น_
  _(2) **แก้โค้ดอย่างเดียวไม่พอเมื่อของเสียถูก persist ไว้แล้ว** — แถว_
  _`VendorKnownGoodValues` ที่สะสมไว้ (1 แถว/1 ใบ) ยังทับใบใหม่ได้ต่อไป ต้องมี_
  _migration ลบทิ้งด้วย ซ้ำรอยเคส `OcrLearnedPatterns.ExtractionRegex`)_
- **`HashCode.Combine` / `GetHashCode()` สุ่มต่อ process — ห้ามใช้เป็นคีย์ล็อก**
  `pg_advisory_xact_lock` กันการแย่งทรัพยากรได้ก็ต่อเมื่อทุก instance คำนวณคีย์ได้
  **ค่าเดียวกัน** แต่ .NET สุ่ม seed ของ Marvin hash ใหม่ทุก process ⇒ instance A
  กับ B ได้คีย์คนละค่า ⇒ **ล็อกไม่กันกันเลย** ทั้งที่โค้ดอ่านแล้วเหมือนป้องกันแล้ว
  — อาการโผล่เฉพาะตอนมีหลาย instance (เลขเอกสาร §86/4 ซ้ำ · เลข JE ซ้ำ · สต็อก
  หายตอนปรับพร้อมกัน · รายการธนาคารถูกจับคู่สองครั้ง) จึงไม่เจอตอนเทสต์เครื่องเดียว
  → คีย์ทุกตัวมาจาก `Helpers/AdvisoryLockKey.For(companyId, scope, part)` (FNV-1a)
  _(บทเรียนซ้อน 3 ข้อ — (1) **รอบก่อนแก้ไปแล้ว 1 จุด แล้วเหลืออีก 7**: การแก้ที่_
  _จุดที่ผู้ใช้รายงานอย่างเดียวคือ defect class "แก้ตัวเดียว เหลือที่เหลือ" ที่_
  _เรพนี้เจอซ้ำที่สุด — หลังแก้ต้อง `grep` รูปแบบเดิมทั้งเรพเสมอ_
  _(2) **เทสต์ "เรียกสองครั้งได้เท่ากัน" จับบั๊กนี้ไม่ได้** เพราะ `HashCode.Combine`_
  _ก็ผ่านภายใน process เดียวกัน — ต้อง **hard-code ค่าคงที่จริง** ถึงจะพิสูจน์ว่า_
  _ข้าม process ได้ (ดู `AdvisoryLockKeyTests`)_
  _(3) นี่เป็นบั๊กที่ checker จับได้จริงเพราะเป็น **รูปทรงของโค้ด** ไม่ใช่ taint —_
  _ต่างจากเคส `_db.Users` ที่จงใจไม่เขียน checker → `tools/advisory_lock_key_check.py`)_
- **entity ที่ "ไม่ใช่ tenant entity" คือจุดที่ global query filter ช่วยไม่ได้**
  กฎ M ("ทุก query ต้องมี `CompanyId`") ถูกบังคับด้วย global query filter สำหรับ
  entity ที่มี `CompanyId` — แต่ **`User` ผูกกับบริษัทผ่าน `CompanyUser`** จึงไม่มี
  filter ตัวไหนช่วยเลย. เส้น DSR ทั้งสาม (`GenerateAccessReportAsync` อ่าน ·
  `ApplyRectificationAsync` แก้ · `ApplyErasureAsync` **anonymize ถาวร**) เขียน
  `_db.Users.FirstOrDefaultAsync(u => u.Id == userId)` เปล่า ๆ ⇒ สมาชิกบริษัท A
  ใส่ GUID ของผู้ใช้บริษัทไหนก็ได้ในระบบ แล้วอ่าน/แก้/**ลบ**ได้จริง
  → ทุกครั้งที่ query entity ที่ไม่มี `CompanyId` ของตัวเอง ต้องมี **ด่านสมาชิก**
  (`CompanyUser`) คั่นเสมอ · รวมเป็น helper ตัวเดียวให้ทุกเส้นเรียก · ข้อความ
  ปฏิเสธต้อง **ไม่บอกว่า "มี id นี้อยู่จริงไหม"** (กัน enumeration ข้ามบริษัท) ·
  เส้นที่เขียนข้อมูล **throw** ไม่ใช่ข้ามเงียบ ๆ (ห้าม silent no-op)
  _(ทำไมไม่เขียน checker: ตัวแยกคือ "id มาจาก request ของผู้ใช้ หรือมาจากแถวที่_
  _scope แล้ว" = **taint ไม่ใช่รูปทรงของโค้ด** — `_db.Users` มี 85 จุดในเรพและ_
  _ส่วนใหญ่ถูกต้อง (resolve ชื่อผู้อนุมัติจาก id ที่เชื่อถือได้แล้ว) checker regex_
  _จะฟ้องผิดเป็นสิบจุด และ **checker ที่ฟ้องผิด = checker ที่พังแล้ว**)_
- **ด่านที่อ่อนกว่าแต่คืนข้อมูลมากกว่า คือช่องที่ใหญ่ที่สุด — ไล่เทียบกันเสมอ**
  `PayrollController` ต้องมี `perm:Pii.View` ถึงจะเห็นเลขบัตรแบบไม่ mask (ถูกแล้ว)
  แต่ `PdpaController` มีแค่ `[Authorize]` ระดับคลาส ⇒ **สมาชิกคนไหนของบริษัทก็ได้**
  เรียก `dsr/access` เพื่อดัมพ์โปรไฟล์ + เอกสาร + การชำระเงิน + **ประวัติการเข้าถึง
  1 ปี** ของใครก็ได้ · `dsr/erase` **anonymize ถาวร**. สองหน้านี้เปิดข้อมูลชุด
  เดียวกัน แต่ด่านคนละระดับ — เวลาเพิ่ม endpoint ที่แตะ PII ให้ถามว่า "มีหน้าอื่น
  ที่เปิดข้อมูลชนิดนี้อยู่แล้วไหม แล้วมันใช้ด่านอะไร" แล้วใช้ด่านอย่างน้อยเท่ากัน
- **`LogWarning` แล้วเดินต่อ = กลืน error — "ดังพอ" ต้องดังในที่ที่คนดู**
  กฎ "ห้าม `catch {}` ใน payment/stock/JE path" ถูกอ่านว่า "ใส่ log แล้วจบ" ซึ่ง
  ยังไม่พอ: `IntegrationService` มีทางออก `return null` ของการลงบัญชีถึง **7 ทาง**
  ทุกทางเขียน `LogWarning` แล้วเดินต่อ ⇒ คู่ค้าได้ `success: true "Invoice
  created"` · sync log ขึ้น `Success` · เอกสารเป็น **Approved** (ภ.พ.30 นับแล้ว)
  แต่ **ไม่มีรายการบัญชีเลย** ⇒ ภ.พ.30 ไม่ตรง GL ถาวร โดยร่องรอยเดียวอยู่ในไฟล์
  log ที่ไม่มีใครเปิด. **log ของเซิร์ฟเวอร์ไม่ใช่ช่องทางแจ้งผู้ใช้**
  → กติกา: เมื่อ "ทำงานหลักไม่สำเร็จแต่ยังเดินต่อ" ต้องดัง **3 ที่**: (ก) บน
  **ตัวข้อมูล** ที่ผู้ใช้เปิดดู (เช่นต่อเข้า `Document.Notes` พร้อมหัวข้อคงที่)
  (ข) บน **สถานะของงาน** (`PartialSuccess` ไม่ใช่ `Success`) (ค) ใน **คำตอบที่ส่ง
  กลับผู้เรียก**. และรวมไว้ที่ helper ตัวเดียวให้ทุกจุดเรียก — 6 จุดที่ต่างคน
  ต่างแจ้งจะ drift แน่นอน
  _(สองบทเรียนซ้อน — (1) **เหตุผลต้องเดินทางออกมาได้**: จุดที่ `return null` รู้_
  _เหตุผลดีที่สุด แต่ผู้เรียกเป็นคนแจ้ง → ส่งผ่าน `Action<string>? onSkip` แล้วใช้_
  _**สตริงเดียวกัน**กับที่ log (ห้ามเขียนข้อความสองชุด — drift รอบหน้า)_
  _(2) **อย่า throw ทิ้งข้อมูลของคนอื่น**: คู่ค้าส่วนใหญ่ไม่ retry — เก็บเอกสารไว้_
  _แล้วบอกให้ชัดว่ายังไม่ลงบัญชี กู้คืนได้ ส่วนข้อมูลที่หายไปแล้วกู้ไม่ได้)_
- **"ข้อมูลอ้างอิงที่เชื่อถือได้" เชื่อถือได้เฉพาะเมื่อ *กุญแจที่ใช้ค้น* ถูก**
  `EnrichFromDbdAsync` ค้นทะเบียนกรมพัฒนาธุรกิจการค้าด้วย `VendorTaxId` แล้วถือว่า
  "ทะเบียนราชการต้องถูกเสมอ" — แต่คีย์นั้นมาจาก **OCR** ซึ่งเป็นช่องที่อ่านผิดได้
  บ่อยที่สุดช่องหนึ่ง (บาร์โค้ด EAN-13 ที่ผ่าน mod-11 · เลขผู้ซื้อถูกหยิบมาเป็น
  ผู้ขาย · หลักเดียวเพี้ยน) เลขผิด ⇒ ได้ข้อมูล**บริษัทอื่น**ที่ถูกต้อง 100% ตาม
  ทะเบียน แล้วระบบเอาไป (ก) ทับชื่อผู้ขายที่อ่านมาถูกแล้ว (ข) บันทึกชื่อที่ถูกต้อง
  เป็น **negative example** = สอนตัวเรียนรู้ผิดถาวร (ค) ตั้ง confidence 0.95 จน
  ไฮไลต์เตือนไม่ขึ้น (ง) สร้าง Contact ผู้ขายของบริษัทที่ไม่เกี่ยวกับใบนี้เลย
  → กติกา: ก่อนให้ข้อมูลอ้างอิง "ชนะ" ต้องมี **ด่านตรวจว่าคีย์น่าเชื่อถือ** —
  ที่นี่ใช้ระดับความต่างของชื่อ: OCR ที่อ่านชื่อเดียวกันผิดได้สตริงที่*คล้าย*เสมอ
  (สมมติฐานทั้งหมดของ FuzzyMatcher) ส่วนคนละบริษัทได้เกือบศูนย์ — วัดจริงได้
  **0.772–0.941 vs 0.000–0.087** ⇒ เกณฑ์ 0.45 นั่งกลางช่องว่าง. ต่ำกว่านั้น =
  ไม่ทับ ไม่สอน ไม่สร้างอะไร + ลด confidence ให้ไฮไลต์ขึ้น + บอกว่าสงสัยคีย์ผิด
  _(สังเกตว่าเกณฑ์นี้ตั้งจากการ**วัดช่องว่างระหว่างสองกลุ่มจริง** ไม่ใช่หยิบเลข_
  _สวย ๆ มาใช้ — ตรงกับบทเรียน "threshold ที่ตั้งจากตัวอย่างชนิดเดียวจะพังกับ_
  _ชนิดอื่น" และเทสต์ต้องล็อก**ช่องว่าง**ไว้ ไม่ใช่ล็อกแค่เลขเกณฑ์)_
- **checker ที่ฟ้องผิด = checker ที่พังแล้ว ต้องแก้ทันที ห้ามเลี่ยงโค้ด**
  `undeclared_local_check` ฟ้อง `x is { Matched: true, Status: { } s }` ว่า `s`
  ไม่เคยประกาศ — มันรู้จักแค่ `is { } f` (มี `is` ติดหน้า) ไม่รู้จัก designator ที่
  อยู่ใน **subpattern ซ้อน**. ทางที่ผิดคือเขียนโค้ดให้อ้อม checker (โค้ดแย่ลงเพื่อ
  ให้เครื่องมือพอใจ) ทางที่ถูกคือขยายฝั่ง "ประกาศแล้ว" ให้ครอบ ตามหลักที่เขียนไว้
  ในตัว checker เองว่า **over-approximate ฝั่งนี้ได้ไม่เสียหาย** เพราะตัวที่พลาด
  จะไม่ถูกกำหนดค่าที่ไหนเลยอยู่แล้ว · แก้แล้วต้องรัน negative test ซ้ำเสมอ
  (ไฟล์สังเคราะห์ที่มีทั้งรูปแบบถูก 2 แบบและบั๊กจริง 1 จุด — ต้องฟ้องเฉพาะจุดหลัง)
- **แยกของออกเป็นหลายชิ้น = ต้องถามทุกฟิลด์ว่า "เป็นเปอร์เซ็นต์ หรือจำนวนเงิน"**
  แยกบิล POS สร้างใบลูกโดยไม่สืบทอด `DiscountPercent`/`ServiceChargePercent`
  แล้วคิดยอดใหม่จาก 0 ⇒ ร้านที่คิดค่าบริการ 10% **เสียรายได้ส่วนนั้นทุกครั้งที่
  แยกบิล** (งานประจำวันของร้านอาหาร ไม่ใช่ edge case) และบิลที่ให้ส่วนลดท้ายบิล
  **เก็บลูกค้าเกิน**. ทั้งสองเงียบสนิทเพราะยอดของแต่ละใบ "ดูสมเหตุสมผล" — ไม่มี
  ใครเอาผลรวมของใบลูกไปเทียบกับใบแม่
  → กติกา: ค่า **เปอร์เซ็นต์** สืบทอดตรง ๆ ได้ (เชิงเส้น ⇒ ผลรวมเท่าเดิมพอดี) ·
  ค่าที่เป็น **จำนวนเงินก้อน** (คูปอง/ทิป) แบ่งไม่ได้โดยไม่เดา → **บล็อกพร้อมบอก
  ทางแก้** ดีกว่าเฉลี่ยเอาเองหรือทิ้งไว้ที่ใบแม่เงียบ ๆ · และเขียนเทสต์ที่ยืนยัน
  **invariant ผลรวมของชิ้นย่อย = ของเดิม** เพราะนั่นคือสิ่งเดียวที่จับบั๊กคลาสนี้ได้
- **"ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" คือ defect class ที่ใหญ่ที่สุดในเรพนี้**
  ตั้งทีมผู้เชี่ยวชาญ 4 ด้านไล่ตรวจไปป์ไลน์ OCR ทั้งสาย (อนุมานชนิดเอกสาร ·
  วิเคราะห์ข้อมูลบนกระดาษ · UX · สถาปัตยกรรม AI) แล้วพบว่า**ปัญหาส่วนใหญ่ไม่ใช่
  ขาดความสามารถ** แต่คือสายที่ขาดระหว่างของที่มีอยู่แล้ว — รูปแบบซ้ำ ๆ 4 แบบ:
  1. **เงื่อนไขที่เป็นจริงไม่ได้เลย** — ตัวเรียนรู้ federated เขียน feedback สะสม
     มาตลอด แต่ฝั่งอ่านมีเงื่อนไข `string.IsNullOrEmpty(TargetDocumentType)` ซึ่ง
     ตัวอนุมานเซ็ตค่าให้เสมอทุกเส้นทาง ⇒ **write-only ตั้งแต่วันแรก**
  2. **มีทุกอย่างยกเว้น call site** — `AiFeatureKey.DocumentTypeClassification`
     มี enum + prompt + student register ครบ แต่ prompt ไม่มีใครเรียก ⇒ ตายในไฟล์
     และ student อดอาหารถาวร (ไม่มี feedback row จึงไม่มีวัน IsReady)
  3. **เรียกจากที่เดียวที่ไม่ใช่เส้นทางหลัก** — `BranchCodeExtractor` ถูกเรียกจาก
     `ParseThaiDocument` ซึ่งรันเฉพาะ Tesseract ⇒ เส้นทาง Azure (เส้นหลัก) ไม่เคย
     อ่านรหัสสาขาเลย แล้ว `?? "00000"` กลบเงียบ ๆ
  4. **ชื่อ key คนละชุดระหว่างฝั่งเขียนกับฝั่งอ่าน** — FieldConfidence ถูกเขียน
     ด้วยชื่อ 3 ชุด (Azure/SmartExtractor/UI) ⇒ ด่านลด confidence ไม่ทำงาน 3 ใน 4
     เส้นทาง · ไฮไลต์เหลืองตามกฎเหล็ก #3 แทบไม่เคยขึ้น
  _กลไกที่กัน: ก่อนเพิ่มความสามารถใหม่ ให้ grep หา **call site** ของสิ่งที่มีอยู่
  ก่อนเสมอ — "มีคลาสนี้อยู่" ≠ "โค้ดเส้นนี้ถูกเดินจริง". และเมื่อเขียนตัวเรียนรู้
  ตัวใหม่ ต้องเขียนเทสต์/simulation ที่พิสูจน์ว่า **ฝั่งอ่านถูกเรียกจริง** ไม่ใช่
  แค่ฝั่งเขียนทำงาน (ฝั่งเขียนที่ทำงานอย่างเดียวดูเหมือนระบบเรียนรู้อยู่ตลอด)_
- **"ค่า default ที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้" อันตรายกว่าการไม่ตอบ** — พบ
  รูปแบบเดียวกันซ้ำ 3 จุดในไปป์ไลน์เดียว: `buyerPos = text.Length` (สมมติว่าผู้ซื้อ
  อยู่ท้ายหน้า ⇒ บาร์โค้ดบรรทัดล่างสุดเป็นเลขผู้ซื้อทุกใบ) · `?? "00000"` (สมมติว่า
  ไม่มีสาขา = สำนักงานใหญ่ ⇒ ใบของสาขาที่ 3 ลงผิดทุกใบ) · "เจอ 50 ทวิ ⇒ เราเป็น
  ผู้ขาย" (สมมติทิศเดียว ⇒ ผิดครึ่งหนึ่งเสมอ). ทั้งสามทำให้ระบบ "ตอบได้เสมอ" ซึ่ง
  ดูดีกว่าในโค้ด แต่ผู้ใช้ไม่มีทางรู้ว่าคำตอบไหนคือข้อมูลจริงและคำตอบไหนคือค่าที่
  แต่งขึ้น — **ไม่รู้ = ต้องบอกว่าไม่รู้** แล้วให้ชั้นถัดไป (กฎ/คน/AI) ตัดสิน
- **"ด่านที่เขียนไว้ + doc-comment ที่บอกว่าด่านอยู่ตรงไหน" ≠ ด่านที่ถูกเรียก**
  `GlobalVendorIntelLearner.CanShareMoneyAggregates` (k-anonymity k=3) ถูกเขียน
  ครบ มีคอนสแตนต์ `MinTenantsForFullDisclosure = 3` มีหมายเหตุ 6 บรรทัดอธิบาย
  เหตุผล และ doc-comment ที่หัวคลาสเขียนชัดว่า *"those numbers are guarded by
  the k=3 read-time gate inside VendorIntelligenceService.PredictAsync"* — แต่
  **ไม่มีใครเรียกเมธอดนั้นเลยทั้งเรพ** ⇒ ผู้ขายที่มีผู้เช่ารายเดียวในระบบ
  ผู้เช่ารายอื่นเห็น ยอดเฉลี่ย/ต่ำสุด/สูงสุด/มัธยฐาน ของรายนั้น**ตรง ๆ**บน
  แบนเนอร์ "ยอด X นอกช่วง min–max" = ข้อมูลการค้าข้ามผู้เช่ารั่วมาตลอด.
  พี่น้องของมัน (`GlobalProductLearner` · `GlobalExpenseCategoryLearner` ·
  `GlobalDocWorkflowLearner`) บังคับ floor ของตัวเองครบ — ตัวนี้ตัวเดียวที่ลืม
  _(บทเรียน: **doc-comment ที่บอกว่า "ป้องกันแล้วที่ X" เป็นเจตนา ไม่ใช่หลักฐาน**_
  _— ก่อนเชื่อว่ามีด่าน ให้ `grep` หาชื่อเมธอดด่านนั้นว่ามี call site จริงไหม.
  กติกาที่ใช้ได้ทั่วไป: control ที่เป็น "เมธอดคืน bool" แล้วไม่มีใครเรียก =
  ไม่มี control — เหมือนกฎ "control ที่ไม่มีเทสต์ยืนยัน = ไม่มี control")_
- **ตารางความรู้เชิงกฎหมายที่ถูกคัดลอกไปเขียนใหม่ = เตือน/คิดผิดตลอดไป**
  อัตราหัก ณ ที่จ่ายมีอยู่ **2 ตาราง**: `WithholdingTaxCertController.GetIncomeTypes`
  (40(3) ค่าสิทธิ = **5%** ควรเป็น 3% · 40(1) เงินเดือน = **3% คงที่** ทั้งที่
  กฎหมายใช้อัตราขั้นบันได) และ dropdown ใน `wht-credit.html` (ยุบ "ดอกเบี้ย/
  เงินปันผล" เป็นตัวเลือกเดียวที่ **1%** ทั้งที่ดอกเบี้ยบุคคล 15% · ดอกเบี้ย
  นิติบุคคล 1% · ปันผล 10%). สองตารางไม่ตรงกันเอง **และไม่ตรงกฎหมายทั้งคู่** —
  dropdown คือคำแนะนำเดียวที่ผู้ใช้เห็นตอนกรอกอัตรา ⇒ ยอดหักผิดตั้งแต่ต้นทาง
  แล้วไหลไป ภ.ง.ด.3/53 + เครดิต CIT ทั้งสาย. เจอพร้อมกันอีก 2 เรื่องแบบเดียวกัน:
  checksum เลขผู้เสียภาษีฝั่ง JS มี 2 ชุด (`layout.js` **ไม่ตรวจหลักแรก 0-8**
  ⇒ เลขขึ้นต้น 9 ผ่านฝั่งผู้ใช้แล้วไปตายที่เซิร์ฟเวอร์) และการแปลง พ.ศ.→ค.ศ.
  มี **4 เกณฑ์ที่ต่างกัน** (`> 2500` · `>= 2400` · `> 2400` · `> currentYear+10`)
  ⇒ ปีย่อ "69" เส้นทางหนึ่งได้ **2069** อีกเส้นทางในไฟล์เดียวกันได้ 2026
  _(กลไกที่แก้: ตารางกฎหมายทุกตัวต้องมี **ที่เดียว** ใน `Helpers/` แล้วหน้าเว็บ_
  _**สร้าง UI จาก endpoint** ไม่ใช่พิมพ์ตัวเลขซ้ำ — กลไกเดียวกับที่ใช้กับ_
  _`MENU_SECTIONS` และ `complianceIssues`. ค่าที่กฎหมายไม่ได้กำหนดคงที่_
  _(เงินเดือน = ขั้นบันได) ต้องเก็บ **null** ห้ามใส่ตัวเลขปลอมให้ช่องไม่ว่าง)_
- **"ครึ่งเซิร์ฟเวอร์ของการแก้ ship ไปคนเดียว"** — `OcrCorrectionRequest` เพิ่ม
  `VendorBranchCode`/`BuyerTaxId`/`BuyerBranchCode` แล้ว, `SubmitCorrectionAsync`
  persist ครบแล้ว, มีหมายเหตุในโค้ดว่า *"ช่องที่เพิ่งเปิดให้ผู้ใช้แก้ได้ (เดิม
  ไม่มีทางแก้เลย)"* — แต่ `_buildReviewCorrection()` ซึ่งเป็น payload builder
  **ตัวเดียว**ของหน้า review ไม่เคยส่ง 3 ช่องนั้น และ modal ยังโชว์ผู้ซื้อเป็น
  ข้อความอ่านอย่างเดียว ⇒ ผู้ใช้ยัง "ไม่มีทางแก้" เหมือนเดิมทุกประการ
  _(กติกา: เพิ่มช่องใน request DTO = ต้องไล่ให้ครบ **ฟอร์ม → payload builder →_
  _DTO → persist** ในคอมมิตเดียว ตามเช็กลิสต์ข้อ B — การมีช่องใน DTO ไม่ได้แปลว่า_
  _ผู้ใช้กรอกได้)_
- **`Math.Round` ที่ลืม `AwayFromZero` มักมาเป็นคู่ — แก้ตัวหนึ่งแล้วเหลืออีกตัว**
  `PlatformBillingDocumentIssuer` คิด 7/107 ถูกต้องพร้อม `AwayFromZero` แต่
  `SaasBillingDocumentService` ที่คิด**สูตรเดียวกัน** ลืมทั้ง 4 จุด และ
  `SampleDataController` อีก 1 จุด
  _(ก่อน commit ที่แตะเงิน: `grep -n 'Math\.Round' <file>` แล้วดูว่าจุดที่เป็น_
  _จำนวนเงินมี `MidpointRounding` ครบทุกจุดไหม — ไม่ใช่แค่จุดที่กำลังแก้)_
- **แต่ "ลืม `AwayFromZero`" ≠ "ยอดผิด" เสมอไป — ต้องพิสูจน์ว่าสูตรนั้นตกจุด
  กึ่งกลางได้จริงก่อนเรียกว่าบั๊ก** ตอนไล่แก้ 5 จุดข้างบน ผมสรุปเหมาเข่งว่า
  "ใบเดียวกันคำนวณคนละที่ต่างกัน ฿0.01" แล้วเขียนลง DOCUMENT_FLOW/ACCOUNT_STRUCTURE
  ไปแล้ว — พอเขียน simulation ไล่ทุกยอด ฿0.01–฿20,000 (2 ล้านค่า) กลับพบว่า:
  `x × 7 / 107` และ `x / 1.07` **ไม่มีค่าใดตกจุดกึ่งกลางเลย** (ต้องมี
  14·c ≡ 107 mod 214 เมื่อ c = จำนวนสตางค์ ซึ่งเป็นไปไม่ได้ — ซ้ายคู่เสมอ ขวาคี่)
  ⇒ 3 ใน 5 จุดที่ "แก้" ไม่เคยผิดมาก่อน. ที่ผิดจริงคือ `x × 0.07` (VAT บวกเพิ่ม)
  ซึ่งตกกึ่งกลาง ~0.5% ของยอด (฿1.50 → 0.105 ⇒ 0.11 เทียบ 0.10)
  _(บทเรียนซ้อน: **negative test ที่ "ผ่านทั้งก่อนและหลังแก้" = ยังไม่ได้พิสูจน์
  ว่ามีบั๊ก** ไม่ใช่ "แก้สำเร็จ" — ต้องรัน**สูตรเดิม**กับช่วงค่าจริงแล้วเห็นมันพัง
  ก่อน ถ้าไม่พังแปลว่าที่รายงานมาผิด ให้แก้บันทึกให้ตรงความจริงทันที ไม่ใช่ปล่อย
  ให้ doc ถือ "บทเรียน" ที่ไม่เคยเกิด. เก็บการยุบสูตรซ้ำไว้ได้ แต่ต้องเรียกว่า
  **การป้องกัน** ไม่ใช่การแก้บั๊ก — ล็อกข้อเท็จจริงไว้ที่ `VatRoundingModeTests`)_
- **ผลตรวจจากทีม/agent ต้อง verify ก่อนเชื่อ — มันผิดได้ทั้งสองทาง** รอบเก็บงาน
  คงค้างพิสูจน์ทั้งสองแบบในรอบเดียว: รายงานบอกว่า `quick-sale.html` มีสูตร
  7/107 ซ้ำ — **ไม่มีจริง** (แก้ตามไปก็เสียเวลาเปล่า); รายงานบอกว่า
  `GetActivePatternsAsync` เป็น N+1 ซึ่งผมประเมินตอนแรกว่า "เกินจริง" —
  **จริง** (`MatchAsync` ถูกเรียกใน `for` ลูปของ `OcrController` แล้วยิง
  query ต่อบรรทัด). ทั้งสองครั้งคำตอบมาจาก `grep` จุดเรียกจริง ไม่ใช่การอ่าน
  รายงานแล้วเชื่อ/ไม่เชื่อตามความรู้สึก
  _(กติกา: ทุกข้อในผลตรวจต้องเปิดไฟล์ยืนยันเองก่อนลงมือ — และเมื่อพบว่าที่_
  _รายงานมาผิด ให้บันทึกไว้ด้วยว่าผิด ไม่ใช่แค่ข้ามไปเงียบ ๆ)_
- **"ลืม `AwayFromZero`" ≠ "ยอดผิด" — พิสูจน์ว่าสูตรตกจุดกึ่งกลางได้จริงก่อน**
  ดูรายละเอียดในบทเรียนเรื่อง `Math.Round` ข้างล่าง: `x × 7/107` และ `x / 1.07`
  **ไม่มีค่าใดตกจุดกึ่งกลางเลย** ส่วน `x × 0.07` ตกจริง ~0.5% ของยอด
- **ของที่ "ไม่มีใครเรียก" มี 2 ทางแก้ และต้องเลือกอย่างตั้งใจ: ต่อสาย หรือ ลบ**
  รอบนี้เจอ 4 ตัวและ**ต่อสายทั้งหมด** เพราะแต่ละตัวมีที่ทางชัดเจนอยู่แล้ว
  (`GetActivePatternsAsync` → prewarm ก่อนลูป · `RunMaintenanceForCompanyAsync`
  → endpoint แอดมิน · `DocumentWorkflowPredictor` → local prior ของ
  `DocumentConversionSuggestion` ซึ่ง `LocalModelVersion` ชื่อ "WorkflowMap-v1"
  รออยู่แล้ว · `DocumentZoneAnalyzer.Analyze` → fallback เมื่อ pipeline หลัก
  ไม่ได้อะไรเลย). **ห้ามปล่อยไว้เฉย ๆ** เพราะโค้ดที่ไม่มีใครเรียกจะถูกอ่านว่า
  "มี feature นี้แล้ว" ทั้งที่ไม่มี — และ doc-comment ของมันจะโกหกคนอ่านต่อไป
- **feature ที่ยังไม่มีจริง ห้ามเขียนใน CLAUDE.md ว่ามีแล้ว** กฎเหล็ก #3 ข้อ 2
  เคยระบุว่า fallback ขั้น 1 คือ "Vision/OCR primary (DeepSeek-VL)" แต่
  `DeepSeekProvider.CompleteAsync` **รับแต่ข้อความ ส่งรูปไม่ได้** และไม่มี engine
  ตัวไหนเรียกโมเดล vision เลย ⇒ ทุกคนที่อ่านกฎนี้ (รวม AI agent) เข้าใจผิดว่า
  ระบบมีชั้นนั้นแล้วและไปออกแบบต่อจากสมมติฐานที่ผิด
  _(กติกาเดิมของไฟล์นี้ใช้ได้ตรง ๆ: **โค้ดเป็น ground truth — doc ผิด แก้ doc**._
  _และเมื่อจดว่า "ยังไม่มี" ให้เขียนด้วยว่า**ต้องมีอะไรบ้างถึงจะเรียกว่าเสร็จ**_
  _ไม่งั้นรอบหน้าจะมีคน merge ครึ่ง ๆ กลาง ๆ เข้าเส้นทางที่ตัวเลขกลายเป็นบัญชีจริง)_
- **ตัวเลขคู่ที่ต้องสอดคล้องกัน ห้ามมาจากคนละแหล่ง — ต้องมี "ตัวตั้ง" ตัวเดียว**
  ไฟล์ สปส.1-10 ประกาศคู่ (ค่าจ้าง, เงินสมทบ) โดย exporter หยิบ **`GrossIncome`**
  มาใส่ช่องค่าจ้าง แต่หยิบ **`SocialSecurityEmployee` ที่เก็บไว้** มาใส่ช่องเงินสมทบ
  ⇒ ผู้ใช้แก้ยอดสมทบรายคนเมื่อไร ไฟล์ประกาศคู่ที่ 5% ไม่ลงตัวทันที (ค่าจ้าง
  14,094 คู่กับสมทบ 683 ทั้งที่ 5% = 705) — **สปส. e-Service คิดใหม่จากค่าจ้าง
  ที่กรอกแล้วตีกลับทั้งแถว** และเงียบสนิทจนกว่าจะไปเจอตอนอัปโหลด
  → กติกา: เมื่อสองค่าต้องสัมพันธ์กันด้วยสูตร ให้เลือก **ตัวตั้ง** (ที่นี่คือ
  *ฐานค่าจ้าง*) เก็บลง DB แล้วให้ที่เหลือเป็น *ผลลัพธ์* ทุกที่ — ห้ามให้สองช่อง
  เป็นอิสระต่อกันแล้วหวังว่าจะตรง · และเพิ่ม**ด่านตรวจคู่ก่อนส่งออก** เพราะข้อมูล
  เก่าที่ค้างมาก่อนมีตัวตั้งจะยังขัดกันอยู่
  _(สามบทเรียนซ้อน — (1) **ยอดสองฝั่งที่กฎหมายผูกกันไว้ ต้องผูกในโค้ดด้วย**:_
  _ม.33 ให้ลูกจ้าง/นายจ้างใช้ฐานเดียวกัน แต่ฟอร์มแก้ยอดให้แก้ฝั่งเดียวได้อิสระ ⇒_
  _ยอดนำส่งต่างกัน 22 บาทโดยไม่มีอะไรเตือน. และ **ฝั่งที่สองต้องคิดจากฝั่งแรก_
  _ไม่ใช่คำนวณจากฐานใหม่อีกรอบ** — ยอดที่ import มามักปัดเป็นบาทถ้วนแล้ว_
  _(12,953 × 5% = 647.65 แต่หักจริง 648) คิดใหม่จะต่างกัน 0.35 ทุกครั้ง_
  _(2) **ซ่อมเฉพาะแถวที่พังจริง**: การ "หารกลับ" หาฐานจากยอดสมทบ ถ้าใช้กับทุกแถว_
  _จะเปลี่ยนค่าจ้างที่ประกาศของแถวที่ถูกอยู่แล้ว (12,953 → 12,960) — ตรวจก่อนว่า_
  _คู่เดิมเข้ากันได้ไหม ถ้าได้ให้คงของเดิม_
  _(3) **ผู้ใช้ hack เพราะระบบไม่มีช่องให้** — ค่าจ้างตาม ม.5 ไม่รวมเบี้ยเลี้ยง/_
  _ค่าน้ำมันเหมาจ่าย แต่ระบบไม่มีช่อง "ฐานค่าจ้าง" ผู้ใช้จึงไปแก้ยอดสมทบแทนแล้ว_
  _ยัดส่วนต่างลง "หักอื่น" ⇒ อาการโผล่ที่ไฟล์ยื่น. เจอ workaround แปลก ๆ ให้ถามว่า_
  _"เขากำลังพยายามบอกอะไรที่ระบบไม่มีที่ให้ใส่")_
- **"เลขที่" กับ "ชื่อบนกระดาษ" เป็นคนละแกน — อย่าให้ผู้ใช้เดาความสัมพันธ์เอง**
  ผู้ใช้ถามว่าทำไม "เดี๋ยว TIV เดี๋ยว REC": เลขที่เอกสารผูกกับ `DocumentType`
  (ชนิดข้อมูล) แต่หัวกระดาษผูกกับ **บทบาททางกฎหมาย** ที่คำนวณจากธงคนละชุด
  (`IssuedAsCashReceipt` · `ServedAsReceipt` · `CombinedInvoiceTaxInvoice` ·
  ผู้ซื้อครบ §86/4 ไหม) ⇒ ใบที่หัวพิมพ์ "ใบกำกับภาษี/ใบเสร็จรับเงิน" **เหมือนกัน
  เป๊ะ** อยู่ได้ทั้งบน TIV- และ REC- แล้วแต่ทางที่กดเข้า · และมีเคสกลับด้าน
  (TaxInvoice ที่ VAT=0 พิมพ์หัว "ใบเสร็จรับเงิน" บนเลข TIV-)
  → กติกา: เมื่อสองแกนนี้ไม่ใช่ฟังก์ชันของกันและกัน **ห้ามแก้ด้วยการย้าย series**
  (ชนกับ gap-free §86/4 ย้อนหลัง) ให้เพิ่ม **นโยบายระดับบริษัท** ที่บังคับให้ทุก
  ทางเข้าเดินเส้นเดียว แล้วโชว์ "ชนิดตามกฎหมาย" ข้างเลขในรายงาน — ผู้ใช้จะได้ไม่
  ต้องจำว่าเลขไหนแปลว่าอะไร
  _(สี่บทเรียนซ้อน — (1) **default ของ UI ที่ "ฉลาด" อาจตรงข้ามกับนโยบายของ_
  _ลูกค้าพอดี**: smart default ปลดติ๊ก "ออกใบเสร็จ" ให้เองเมื่อใบกำกับจ่ายครบ_
  _งวดเดียว ซึ่งเป็น**เคสที่บริษัทกลุ่ม "แยกใบเสมอ" ต้องการแยกมากที่สุด** ⇒_
  _default ที่คิดมาดีสำหรับกลุ่มหนึ่ง = กับดักของอีกกลุ่ม ต้องให้ policy เป็นคน_
  _ตั้ง default ไม่ใช่ heuristic ในหน้าจอ_
  _(2) **ด่านสถานะที่เขียนว่า `== Approved` เป๊ะ ๆ มักผิด** เพราะสถานะเดินต่อได้_
  _เอง: e-Tax gate บังคับ Approved ⇒ พอส่งอีเมล/รับเงิน เอกสารไป Sent/Paid แล้ว_
  _**สร้าง e-Tax ไม่ได้อีกเลย** ทั้งที่เป็นใบที่อนุมัติแล้วแท้ ๆ — เขียนด่านเป็น_
  _"ห้ามสถานะไหน" (WaitingApproval/Rejected/Draft/Voided) ปลอดภัยกว่า "ต้องเป็น_
  _สถานะไหน" เมื่อสถานะเป็น lifecycle ที่ไหลไปข้างหน้า_
  _(3) **ค่า default ที่แปลว่า "ยังไม่ระบุ" ต้องแยกจากค่าที่มีความหมายจริง**:_
  _`RelatedDocumentId = null` บนใบเสร็จถูกตีความทั้งระบบว่า "ขายสด standalone"_
  _⇒ ใบเสร็จที่ผู้ใช้ลืมผูกใบต้นทางกลายเป็นการขายใหม่ทั้งใบ (VAT เข้า ภ.พ.30_
  _รอบสอง + รายได้เบิ้ล + AR ไม่ถูกล้าง) — เงียบสนิทเพราะยอดแต่ละใบดูสมเหตุสมผล_
  _(4) **ของที่ "ไม่มีใครเรียก" ต้องเลือกอย่างตั้งใจว่าจะต่อสายหรือลบ — และ_
  _ต่อสายเฉพาะส่วนที่ควรมี**: `NumberSeries` มี entity/service/controller ครบแต่_
  _ไม่มี UI + ไม่มี seed ⇒ ตารางว่างตลอด. ส่วนที่ผู้ใช้อยากได้จริงคือ "ตั้งตัวย่อ_
  _เอง" ส่วน `Format`/`CurrentNumber` ของมันสร้างเลข**คนละทรง**ที่ไม่มี advisory_
  _lock — ต่อสายทั้งก้อนคือปล่อยบั๊กเข้าระบบ จึงต่อเฉพาะ Prefix)_
  **ทางแก้ที่เลือก (รอบ 113)**: ให้ **บทบาททางกฎหมายเป็นตัวเลือก series** —
  "หัวมีคำว่าใบกำกับภาษี → เลขชุด TIV เสมอ" โดย **ไม่แตะ `DocumentType`**
  (แตะแล้วจะกระทบ JE / การนับ ภ.พ.30 / สายแปลงเอกสารทั้งสาย) เปลี่ยนแค่ *ตัวย่อ*
  ซึ่งทำได้เพราะตัวนับเลขนับจาก prefix ไม่ใช่ชนิดเอกสารอยู่แล้ว
  _(สองบทเรียนซ้อน — (ก) **ค่าที่ใช้ตัดสินเลข ต้องถูกตรึงพร้อมเลข**: §86/4 ห้าม_
  _แก้เลขย้อนหลัง ถ้าตัวตัดสินยังคำนวณสดอยู่ (เช่น `ServedAsReceipt` ที่เด้งกลับ_
  _ได้เมื่อใบเสร็จลูกถูก void) วันหนึ่งเลขกับหัวจะเล่าคนละเรื่อง → ตรึงลงเอกสาร_
  _เหมือนที่ทำกับ `IssuerBranchCode` แล้ว nullable = "ยังไม่เคยตรึง" ไม่ใช่ false_
  _(ข) **ของแบบนี้มีจุดออกเลขมากกว่าหนึ่งที่เสมอ** — นอกจาก `ApproveDocumentAsync`_
  _ยังมี `CreateSettlementReceiptAsync` ที่ออกเลขเอง ถ้าต่อสายจุดเดียวจะเหลือเคส_
  _ที่ผู้ใช้เจอบ่อยที่สุด (ใบกำกับ ณ วันรับเงิน §78/1) ไว้เหมือนเดิม —_
  _`grep` หา `DocumentNumberGenerator.NextAsync` ทุกจุดก่อนบอกว่าจบ)_
- **"สถานะปลายทาง" ที่ผู้ใช้ไปต่อไม่ได้ = ฟีเจอร์ที่ยังไม่จบ ไม่ใช่การป้องกัน**
  รอบเงินเดือนที่ `Paid` แล้ว แก้ยอดไม่ได้ (ถูกต้อง — JE ลงไปแล้ว) แต่ทางออก
  เดียวที่มีคือ `Voided` ซึ่งเป็น**สถานะปลายทาง** ⇒ เจอเลขผิดของพนักงานคนเดียว
  ต้องทิ้งทั้งรอบแล้วสร้างใหม่ · ที่แย่กว่าคือหน้าจอ **ซ่อนปุ่มเฉย ๆ**: ไม่มี
  ป้าย ไม่มีเหตุผล ไม่มีทางไปต่อ (defect class "ห้าม silent no-op" ในรูปแบบ
  ที่มองไม่เห็นที่สุด — ไม่มี error ให้ debug เพราะไม่มีอะไรเกิดขึ้นเลย)
  → กติกา: ทุกครั้งที่เขียนด่าน "สถานะนี้ทำไม่ได้" ต้องตอบให้ได้ว่า
  **"แล้วผู้ใช้ทำอะไรได้แทน"** ถ้าคำตอบคือ "ทิ้งแล้วทำใหม่ทั้งก้อน" แปลว่ายัง
  ขาดเส้นทางกลับ (ที่นี่คือ `ReopenPaidRunAsync`: Paid → Approved) · และด่านต้อง
  คืน **เหตุผลเป็นข้อความที่เอาไปโชว์ได้** ไม่ใช่ `bool` แล้วให้แต่ละหน้าจอ
  ไปแต่งคำเอง (= สำเนามือชุดที่ 3 รอ drift)
  _(สามบทเรียนซ้อน — (1) **วันที่ของรายการกลับ ไม่ใช่ "วันนี้" เสมอ**: Void =_
  _เหตุการณ์ถูกยกเลิกวันนี้จริง → กลับรายการวันนี้ถูก; แต่ Reopen จะ**โพสต์ใหม่_
  _เข้างวดเดิม**หลังแก้ ⇒ ต้องกลับรายการลงวันเดียวกับ PayDate ไม่งั้นงวดเดิม_
  _เหลือรายการค้างและงวดใหม่มีเกิน ทั้งที่ยอดรวมทั้งปี "ดูถูก"_
  _(2) **"idempotent by skip" กลายเป็นบั๊กทันทีที่ของทำซ้ำได้**: ตัวแนบไฟล์เขียน_
  _ไว้ว่า "ชื่อซ้ำ → ข้าม" ซึ่งถูกตอนจ่ายได้ครั้งเดียวตลอดกาล พอเปิดให้จ่ายซ้ำ_
  _ได้ ภ.ง.ด.1/สปส.1-10/สลิป จะค้างเป็นตัวเลขก่อนแก้**ตลอดไป** แล้ว HR ยื่นผิด_
  _ฉบับโดยไม่มีอะไรบอก — เปิดเส้นทางใหม่แล้วต้องไล่ดูว่า "ของที่เคยเกิดครั้งเดียว"_
  _มีอะไรบ้าง. และทางแก้คือ **เปลี่ยนชื่อฉบับเก่า ไม่ใช่ลบ** เพราะ_
  _`FileAttachmentService.DeleteAsync` ลบไฟล์จริงบนดิสก์ ส่วนฉบับเก่าอาจถูกยื่น_
  _ไปแล้วและต้องเก็บ 5 ปี (พ.ร.บ.การบัญชี ม.10)_
  _(3) `DateTime.ToString("dd/MM/yyyy")` **ไม่ใช่ ค.ศ. เสมอ** — ถ้า process ตั้ง_
  _culture th-TH ปฏิทินเริ่มต้นเป็นพุทธศักราช ปี 2026 กลายเป็น 2569 เงียบ ๆ_
  _ในข้อความที่ไม่ใช่แบบยื่นภาษี ต้องระบุ `CultureInfo.InvariantCulture`)_
- **`.HasValue` บนพร็อพเพอร์ตี้ที่ไม่ใช่ nullable = CS1061 ล้มทั้ง solution —
  และ checker จับไม่ได้จริง ๆ** เขียนด่านว่า `doc.ContactId.HasValue` โดยเข้าใจว่า
  เป็น `Guid?` แต่ `Document.ContactId` เป็น **`Guid`** ("ไม่มีคู่ค้า" แทนด้วย
  `Guid.Empty` ไม่ใช่ null) ⇒ CS1061 + `Accounting.Tests` พังตามด้วย CS0006
  · ชื่อเดียวกันนี้เป็น **`Guid?` ใน DTO** (`DocumentDtos.cs:199`) และเป็น `Guid`
  ในอีกสองที่ของไฟล์เดียวกัน — สายตาอ่านผ่านง่ายมากเพราะ "ก็ ContactId เหมือนกัน"
  → กติกา: ก่อนเขียน `.HasValue` / `.Value` บนพร็อพเพอร์ตี้ ให้เปิดดู**การประกาศ
  ของชนิดที่ถืออยู่จริง** (entity ≠ DTO) โดยเฉพาะชื่อลงท้าย `...Id` ที่มักมีทั้ง
  สองแบบ · "มีค่าไหม" ของ `Guid` เขียนว่า `!= Guid.Empty`
  _(**เขียน checker แล้วแต่ต้องทิ้ง** — บันทึกไว้กันคนถัดไปสร้างของเดิมซ้ำ:_
  _ตัวตรวจแบบดูชื่อพร็อพเพอร์ตี้ทำงานไม่ได้กับเคสนี้เลย เพราะต้องรู้ว่า **ตัวแปร_
  _ที่ถืออยู่เป็นชนิดอะไร** (`doc` = entity → non-nullable · `request` = DTO →_
  _nullable) ซึ่งต้อง resolve type จริง ไม่ใช่จับชื่อ. รุ่นที่จับชื่ออย่างเดียว_
  _ได้ผล "0 จุด" ทั้งก่อนและหลังใส่บั๊กกลับเข้าไป = **negative test ไม่ผ่าน**_
  _ตามกฎข้างล่างจึงลบทิ้ง ไม่ ship ของที่ดูเหมือนมีด่านแต่ไม่มีจริง_
  _(บทเรียนเดียวกับ "control ที่ไม่มีเทสต์ยืนยัน = ไม่มี control"). ถ้าจะทำจริง_
  _ต้องมี type resolution ระดับ Roslyn ซึ่งเกินขอบเขตของ tools/ ชุดนี้ —_
  _ทางที่คุ้มกว่าคือ `dotnet build` ฝั่งผู้ใช้ ซึ่งจับได้ทันทีอยู่แล้ว)_
- **CSP เป็น allow-list — สคริปต์ที่ไม่ได้ระบุถูกบล็อกเงียบ และข้อความ error ที่
  เขียนไว้จะ "โทษผิดตัว" ตลอดไป** หน้า login โหลด
  `https://accounts.google.com/gsi/client` แต่ `script-src` ใน `SecurityMiddleware`
  มีแค่ `'self'` + fonts.googleapis.com + cdn.jsdelivr.net ⇒ เบราว์เซอร์บล็อก
  ทุกครั้ง ⇒ `window.google` ไม่เคยมี ⇒ ปุ่มขึ้น **"โหลดบริการ Google ไม่สำเร็จ —
  ปิดตัวบล็อกโฆษณาแล้วลองใหม่"** ทั้งที่ผู้ใช้ไม่มีตัวบล็อกโฆษณาเลย (Facebook SDK
  โดนแบบเดียวกัน · LINE รอดเพราะเป็น redirect ล้วน ไม่โหลดสคริปต์ของใคร)
  → บทเรียนสองชั้น: (ก) **ข้อความ error ที่เดาสาเหตุแทนผู้ใช้ อันตรายกว่าไม่บอก
  อะไรเลย** — มันพาไล่ผิดทางเป็นเดือน เขียนให้บอก "ทางแก้ที่อยู่ในมือเรา" แทน
  (ข) **ของที่ต้องพึ่งสคริปต์ของบุคคลที่สามมีจุดพังมากกว่าที่คิด** (CSP ของเราเอง ·
  ตัวบล็อก · คุกกี้ข้ามเว็บ/FedCM · in-app browser) — เมื่อมี **redirect flow**
  ให้เลือก ให้ใช้ redirect เป็นเส้นหลักเสมอ. `GoogleClientSecret` มีช่องให้กรอกใน
  หน้าแอดมินมาตลอดแต่ **ไม่มีใครเรียกใช้เลย** (defect class "ของที่สร้างไว้แล้ว
  ไม่ได้ถูกเรียกใช้") — ต่อสายให้เป็นตัวแลก authorization code เหมือน LINE
  _(→ เพิ่ม `tools/csp_external_ref_check.py` โยง "หน้าเว็บโหลดอะไร" (wwwroot)_
  _เข้ากับ "เซิร์ฟเวอร์อนุญาตอะไร" (CSP ใน middleware) ซึ่งอยู่คนละไฟล์คนละภาษา —_
  _รูปแบบเดียวกับ `upload_route_check`. **บทเรียนซ้อน**: รุ่นแรกตัดคอมเมนต์ด้วย_
  _`//[^\n]*` ก่อนดึง string literal แล้วมันไป**กิน `//` ของ `https://`** ที่อยู่ใน_
  _สตริงเอง ⇒ ฟ้องผิด 300+ จุด — การตัดคอมเมนต์ต้องแยกสถานะในสตริง/นอกสตริงเสมอ_
  _(ซ้ำรอยบทเรียน tokenizer ของ `js_dup_method_check`))_
- **"ค่าที่ตรงกัน" ≠ "พิสูจน์ตัวตนแล้ว" — และ control ที่ไม่มีใครเรียกก็ยังเป็น
  ศูนย์เหมือนเดิม** `SsoLoginAsync` ผูกบัญชี Google/Facebook/LINE เข้ากับผู้ใช้เดิม
  ด้วยเกณฑ์เดียวคือ **"อีเมลตรงกัน"** แล้วล็อกอินให้ทันที — ไม่เคยอ่าน
  `email_verified` ที่ Google ส่งมาให้ในคำตอบเดียวกันเลย และ Facebook ไม่มี
  สัญญาณนั้นตั้งแต่ต้น ⇒ ใครสร้างบัญชีฝั่ง provider ให้อีเมลตรงกับผู้ใช้ของเรา
  ก็เข้าถึงข้อมูลทั้ง tenant ได้ (account pre-hijacking) · ในไฟล์เดียวกันยังพบว่า
  **ไม่มีทางเข้าไหนอ่าน `User.Status` เลยสักบรรทัด** ทั้งที่มีโค้ด 4 จุดตั้งเป็น
  `Inactive` — รวม `PayrollService` ที่ตั้งให้อัตโนมัติเมื่อพนักงานลาออก **พร้อม
  คอมเมนต์ว่า "so login + LIFF are revoked"** ⇒ พนักงานที่ลาออกแล้วยังล็อกอินได้
  ทั้งรหัสผ่านและ SSO (ญาติของ "ด่านที่เขียนไว้ + doc-comment ที่บอกว่าด่านอยู่
  ตรงไหน ≠ ด่านที่ถูกเรียก")
  → กติกา: (ก) ก่อนให้ข้อมูลจากภายนอก "ชนะ" ต้องถามว่า **ใครเป็นคนรับรอง**
  ค่านั้น — อีเมลที่ provider ไม่ได้ยืนยัน คือข้อความที่ผู้ใช้พิมพ์เอง ไม่ใช่
  ข้อพิสูจน์ (ข) ปฏิเสธแล้วต้องมีทางไปต่อ — ที่นี่คือลิงก์ยืนยันทางอีเมล ไม่ใช่
  ตันเฉย ๆ (ค) การเปลี่ยนแปลงที่กระทบ "ใครเข้าบัญชีได้" ห้ามเงียบ: audit +
  อีเมลแจ้งเจ้าของ + หน้าให้ผู้ใช้ดู/ถอดเอง (ง) ค่าที่เป็น **ช่องเดี่ยว**
  (`AuthProvider`/`AuthProviderId`) แต่ความจริงเป็น **หลายค่าได้** ต้องเป็นตาราง
  ตั้งแต่แรก — ไม่งั้นผูกอันที่สองแล้วอันแรกหายเงียบ ๆ และถอดไม่ได้เพราะระบบ
  ไม่รู้ว่ามีอะไรอยู่
  _(บทเรียนซ้อน: เจอ **500 แทน 401** ในเส้นเดียวกัน — บัญชีที่สร้างผ่าน SSO มี_
  _`PasswordHash=""` ส่งเข้า `BCrypt.Verify` แล้วโยน `SaltParseException` ซึ่ง_
  _middleware แปลงเป็น "เกิดข้อผิดพลาดภายในระบบ" ⇒ ผู้ใช้ไม่มีทางรู้ว่าต้องกดปุ่ม_
  _provider แทน. **ทางเข้าที่ผู้ใช้ใช้ผิดวิธีได้ ต้องตอบเป็นคำแนะนำ ไม่ใช่ 500**)_
- **บังคับให้มีข้อมูลที่ผู้ให้บริการ "อาจไม่ให้" = ปิดฟีเจอร์ทิ้งทั้งเส้น**
  `SsoLoginAsync` throw ทันทีเมื่อ provider ไม่คืนอีเมล — แต่ LINE คืนอีเมลเฉพาะ
  channel ที่ผ่านการอนุมัติสิทธิ์ email (ต้องยื่นเอกสาร) ซึ่งส่วนใหญ่ยังไม่ผ่าน
  ⇒ ปุ่ม LINE ใช้ไม่ได้เลย **แม้แต่กับผู้ใช้ที่ผูกบัญชีไว้เรียบร้อยแล้ว** ทั้งที่
  ตอนนั้นระบบรู้อยู่แล้วว่าเขาเป็นใครจาก `ProviderUserId` — ข้อมูลที่ *จำเป็นจริง*
  คือ uid ส่วนอีเมลจำเป็นเฉพาะตอน "จับคู่บัญชีเดิม/สร้างบัญชีใหม่" เท่านั้น
  → กติกา: แยกให้ออกระหว่าง **ข้อมูลที่ระบบต้องใช้จริงในขั้นนั้น** กับ **ข้อมูลที่
  สะดวกถ้ามี** แล้ววาง guard ไว้ที่ขั้นที่ต้องใช้จริง ไม่ใช่ที่ประตูหน้า ·
  เมื่อขาดจริง ๆ ต้อง **ไปต่อได้** — ที่นี่คือออก "ตั๋วที่เซ็นแล้ว" พาไปหน้าสมัคร
  กรอกเฉพาะสิ่งที่ขาด แล้วผูกให้อัตโนมัติ (ห้ามให้ผู้ใช้กด SSO ซ้ำ เพราะ
  authorization code ใช้ได้ครั้งเดียว) และ **ห้ามแต่งอีเมลปลอมให้เอง**
  _(บทเรียนซ้อน — (1) **token คนละวัตถุประสงค์ต้องใช้กุญแจคนละดอก**: ตั๋วสมัคร_
  _เซ็นด้วย `secret + "|purpose"` ⇒ access token ที่ใครก็ถือ เอามาสวมเป็นตั๋วไม่ได้_
  _(2) **provider ตั้ง callback URL ได้ชุดเดียว** ⇒ ปุ่ม "ผูกบัญชี" ในหน้าตั้งค่า_
  _ต้องเดินผ่าน `/login.html` แล้วให้หน้านั้นรู้เจตนาจาก `ssoMode` ใน sessionStorage_
  _— ห้ามลงทะเบียน callback ใหม่ต่อหน้าที่อยากผูก)_
- **ด่านตรวจที่เขียนไว้ "ครึ่งเดียว" อันตรายกว่าไม่มีด่าน — เพราะดูเหมือนมีแล้ว**
  เส้นนำเข้ารอบเงินเดือนจากระบบนอก (`ImportPayrollRunAsync`) มีด่านตรวจอย่างดีว่า
  `net = gross − หักฝั่งลูกจ้าง` ถึงขั้น reject ทั้งรอบเมื่อไม่ตรง และมีคอมเมนต์
  อธิบายว่า "ปกส./PVD ฝั่งนายจ้าง ห้ามนำมาหักจาก net" — **คือรู้ว่ามีตัวเลขนั้นอยู่
  แต่ไม่เคยถามว่ามันสมเหตุสมผลไหม** ⇒ ระบบต้นทางคิดฝั่งนายจ้างจากค่าจ้างเต็ม
  แต่คิดฝั่งลูกจ้างจากฐานที่หักจริง ⇒ รวมทั้งรอบต่างกัน 22 บาท ติดมากับข้อมูล
  ตั้งแต่วินาทีแรกและไปโผล่ตอนนำส่ง สปส. (ผู้ใช้เห็นแค่ "ยอดไม่ตรง" ไม่รู้ว่ามาจากไหน)
  → กติกา: เมื่อเขียนด่านให้ค่าหนึ่ง ให้ไล่ดู **ทุกค่าที่มาจากแหล่งเดียวกัน** ว่า
  ค่าไหนยังไม่มีใครตรวจ — ข้อมูลจากภายนอกที่ "ผ่านด่านมาแล้ว" จะถูกทุกคนอ่านว่า
  เชื่อถือได้ทั้งก้อน
  _(บทเรียนซ้อน 2 ข้อ — (1) **ตรรกะซ่อมที่มีอยู่แล้วอยู่ผิดที่**: โค้ดซ่อมคู่ ปกส._
  _เขียนไว้ใน `ReopenPaidRunAsync` ที่เดียว ⇒ ทำงานเฉพาะตอนผู้ใช้กด "กลับรายการ_
  _จ่าย" · รอบที่ import แล้วเดินตรงไป "จ่าย" ไม่เคยผ่านเลย — **ก่อนบอกว่า "มีตัว_
  _ซ่อมแล้ว" ต้องถามว่ามันอยู่บนเส้นทางที่ข้อมูลเดินจริงหรือเปล่า** (ญาติของ_
  _"ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้") → ยุบเป็น `SsoWageBase.Normalize`_
  _ตัวเดียว เรียกจาก 3 จุดที่เขียนยอดจริง_
  _(2) **จุดที่ซ่อมได้ต้องเป็นจุดที่ยังไม่ลง JE**: ซ่อมตอน Paid = ตัวเลขไม่ตรงกับ_
  _รายการบัญชีที่ลงไปแล้ว ⇒ ตาข่ายรับสุดท้ายต้องอยู่ **ก่อน**สร้าง JE ในเมธอดจ่าย_
  _ส่วนรอบที่จ่ายไปแล้วต้องกลับรายการก่อนเท่านั้น — ห้ามเขียน migration ไปไล่แก้_
  _แถวเงินย้อนหลังโดยไม่แตะ JE)_
- **"กดแล้วไม่เห็นอะไรเปลี่ยน" มักไม่ใช่ปุ่มพัง แต่คือระบบไม่ได้บอกว่าเปลี่ยนที่ไหน**
  ผู้ใช้กด "กลับรายการจ่าย" แล้วเปิด JE เดิม เห็นยอดประกันสังคมผิดเท่าเดิม จึง
  รายงานว่าปุ่มไม่ทำงาน — แต่การกลับรายการ **ไม่แก้ใบเดิม** (ห้ามแก้ใบที่ผ่าน
  รายการแล้ว) มันสร้าง**ใบตรงข้าม**ขึ้นมาหักล้าง แล้วเปลี่ยนสถานะใบเดิมเป็น
  `Reversed` ⇒ ใบเดิมจะโชว์ยอดเดิมตลอดไปโดยถูกต้อง. สิ่งที่ขาดคือ **ข้อความ
  ตอบกลับไม่เคยบอกว่ากลับใบไหน** ผู้ใช้จึงไม่มีอะไรให้ไปตรวจ
  → กติกา: การกระทำที่ผลลัพธ์ไปโผล่ที่ **เอกสารคนละใบ** ต้องบอก "เลขใบปลายทาง"
  กลับมาเสมอ + บอกด้วยว่าใบต้นทางจะไม่เปลี่ยนตัวเลข (ตัวชี้วัดง่าย ๆ: ถ้าผู้ใช้
  ต้องเดาเองว่าไปดูผลที่ไหน แปลว่ายังไม่จบ)
  _(เจอของแถมสองอย่างในเส้นเดียวกัน — (1) **ปุ่มสองปุ่มที่ชื่อคล้ายกันแต่ขอบเขต_
  _ต่างกัน**: "กลับรายการนำส่ง" กลับเฉพาะ JE นำส่ง (Dr 21815/Cr ธนาคาร) ไม่แตะ JE_
  _จ่ายเงินเดือนที่มีบรรทัด 54120 — ต้องเขียนขอบเขตลงในข้อความตอบกลับ ไม่ใช่ให้_
  _ผู้ใช้อนุมานจากชื่อปุ่ม (2) **`if (มี JE) กลับ;` เฉย ๆ = silent no-op**: รอบที่_
  _สถานะ Paid แต่ไม่มี `JournalEntryId` ผูกอยู่ จะข้ามการกลับไปเงียบ ๆ แล้วตอบว่า_
  _"กลับรายการจ่ายแล้ว" ทั้งที่เงินยังอยู่ในบัญชีครบ — เงื่อนไขที่ "ปกติเป็นจริง_
  _เสมอ" ต้องมี else ที่ fail loud ไม่ใช่ปล่อยผ่าน)_
- **"ทำให้สองฝั่งเท่ากัน" ต้องถามก่อนว่า *ฝั่งที่ยึดเป็นความจริงนั้นถูกไหม***
  ตัวซ่อมคู่ยอดประกันสังคมยึด "ฝั่งลูกจ้างเป็นความจริง" แล้วคำนวณฝั่งนายจ้างตาม
  — ถูกในเคสที่ตั้งใจ แต่พังสามทางที่ไม่ได้คิดถึง: (1) **ลูกจ้าง = 0 แต่นายจ้าง
  มียอด** (นายจ้างออกให้เป็นสวัสดิการ / ต้นทางส่งขาด) ⇒ สูตรคืน 0 ⇒ **ล้างยอด
  นายจ้างทิ้ง** ⇒ JE ไม่มีบรรทัดค่าใช้จ่าย หนี้สินขาด และไฟล์ สปส.1-10 ก็กรอง
  แถวนั้นออก ⇒ นำส่งขาดโดยไม่มีใครเห็นจนกระทบยอด GL เดือนถัดไป (2) ลูกจ้างถูก
  หัก**เกินเพดาน** ⇒ การทำให้เท่ากันเท่ากับรับรองการหักเกิน ซึ่งกฎหมายให้คืนเงิน
  (3) ลูกจ้างถูกหัก**ต่ำกว่าฐานขั้นต่ำ** ⇒ กลายเป็นการประกาศค่าจ้างต่ำกว่าความจริง
  → กติกา: ก่อนใช้ค่าใดเป็น "ความจริง" ต้องตรวจว่าค่านั้น**อยู่ในกรอบที่กฎหมาย
  ยอมรับ**ก่อน · คู่ที่ขัดกันโดยระบบตัดสินแทนไม่ได้ ให้ **คงของเดิมไว้ทั้งคู่ +
  รายงาน** แล้วให้ด่านตอนเงินจะออกจริงเป็นตัวบล็อก — "ค่าที่แต่งขึ้นอันตรายกว่า
  การไม่ตอบ" ใช้กับการ *ลบ* ค่าทิ้งด้วย ไม่ใช่แค่การ *เติม* ค่าที่ไม่มี
  _(บทเรียนซ้อน: เทสต์ 7 เคสที่เขียนไว้ครอบแต่ทิศที่ตั้งใจแก้ ไม่มีเคสไหนใส่ 0_
  _ลงฝั่งใดฝั่งหนึ่งเลย — **เทสต์ที่เขียนจากเคสที่เจอ ไม่ใช่จากโดเมนของ input**_
  _จะพลาดทิศที่ไม่เคยเจอเสมอ. และการแก้ตัวเลขเงินอัตโนมัติต้องเข้า AuditLog ที่มี_
  _hash chain ไม่ใช่แค่ `_logger` — ผู้สอบบัญชีถามว่า "ใครเปลี่ยน 4,403 → 4,381_
  _ด้วยอำนาจอะไร" ไฟล์ log ตอบไม่ได้)_
- **GET ที่เปลี่ยนสถานะ = ให้ตัวสแกนอีเมลกดแทนผู้ใช้** ลิงก์ "ยืนยันการผูกบัญชี"
  ถูกทำเป็น `GET /sso/confirm-link?token=` ที่ผูกบัญชีทันที — แต่ Microsoft Safe
  Links · Proofpoint URL Defense · antivirus ที่สแกนอีเมล · ตัว preview ของ
  Outlook/Slack **ยิง GET จริงทุกตัว** ⇒ ผู้โจมตีที่กดปุ่ม SSO ด้วยอีเมลของเหยื่อ
  ไม่ต้องรอให้เหยื่อกดอะไรเลย ก็ได้บัญชีไป (account takeover ที่ยิงได้จริง)
  → กติกา: **ลิงก์ในอีเมลพาไปได้แค่ "หน้า"** การเปลี่ยนสถานะต้องเป็น POST ที่มี
  การกดของมนุษย์เสมอ · token แข็งแรงแค่ไหนก็ไม่ช่วย เพราะช่องโหว่อยู่ที่ *วิธี
  บริโภค* token ไม่ใช่ตัว token
- **ด่านที่เพิ่งสร้างต้องไล่ให้ครบ "ทุกทางเข้า" ไม่ใช่ทางที่ผู้ใช้รายงาน**
  `UserLoginPolicy` ถูกต่อสายเข้า 3 ทางเข้าฝั่งเว็บครบ แต่ **LINE bot ทั้ง 3 จุด
  (ข้อความ · รูปใบเสร็จ · ปุ่มอนุมัติเอกสาร) ไม่เคยอ่านค่านั้น** ⇒ พนักงานที่
  ลาออกแล้วยังกดอนุมัติเอกสารผ่าน LINE ได้ ทั้งที่คอมมิตนั้นอ้างว่า "revoke แล้ว"
  → หลังเพิ่ม control ให้ `grep` หา **ทุกจุดที่ resolve ตัวตนผู้ใช้** (ไม่ใช่แค่
  จุดที่ทำ authentication) แล้วถามทีละจุดว่า "ด่านนี้ครอบไหม"
- **ช่องที่ "ผู้ใช้มองเห็น" กับ "เก็บไว้ภายใน" ห้ามใช้ช่องเดียวกัน** ตอนเพิ่ม
  ร่องรอย "ผู้ใช้กดอนุมัติทั้งที่มีคำเตือน" ผมเขียนลง `Document.Notes` ก่อน —
  ซึ่ง `PdfGenerationService` **พิมพ์ลงกระดาษจริง** ⇒ คำเตือนภายในว่า "ใบนี้
  ผิด §86 แต่ยืนยันแล้ว" จะไปโผล่บนใบที่ส่งให้ลูกค้า. ที่ถูกคือ
  `Document.InternalNotes` ซึ่ง**มีอยู่แล้วแต่ไม่เคยมีใครใช้** — และพอจะใช้ก็พบว่า
  **ไม่เคยมีบรรทัด `ADD COLUMN` ให้มันเลย** (มีบน entity เฉย ๆ ⇒ ฐานที่สร้างก่อน
  เพิ่มพร็อพเพอร์ตี้จะไม่มีคอลัมน์)
  _(กติกา: ก่อนเขียนอะไรลงช่องข้อความของเอกสาร ให้ `grep` ว่าช่องนั้นถูก renderer_
  _อ่านหรือเปล่า · และก่อนใช้พร็อพเพอร์ตี้ที่ "มีอยู่แล้ว" ให้เช็คว่ามีคอลัมน์จริง_
  _— entity กับ schema ในเรพนี้ไม่ได้ sync อัตโนมัติ (ห้ามใช้ EF Migrations))_
- **"ค่าที่กฎหมายกำหนดเป็นช่วงเวลา" ห้ามเก็บเป็นค่าเดียวต่อปี** อัตราสมทบ
  ประกันสังคมของไทยถูก**ลดชั่วคราวเป็นช่วงเดือน**เสมอ (1% พ.ค.–ก.ค. 2563 ·
  2.5% ม.ค.–ก.พ. 2565) แต่ `SsoYearConfig` เก็บได้ปีละค่าเดียว ⇒ ผู้ใช้ต้องแก้
  แถวเดิมกลางปี ซึ่ง **เปลี่ยนอัตราของเดือนที่ยื่น สปส. ไปแล้วย้อนหลังไปด้วย** ⇒
  สร้างไฟล์ สปส.1-10 ใหม่แล้วไม่ตรงกับที่ยื่นจริง โดยไม่มีอะไรเตือน (เงียบสนิท
  เพราะยอดแต่ละเดือน "ดูสมเหตุสมผล"). สังเกตว่า doc-comment ของ
  `SsoRateSchedule` **เขียนไว้เองว่า "ยกเว้นประกาศลดชั่วคราวเป็นรายงวด"** แล้ว
  ยังเก็บเป็นรายปี — เจตนาถูก โครงสร้างไม่รองรับ
  → กติกา: เมื่อหลายแถวครอบเวลาเดียวกัน ต้องมี **เกณฑ์ตัดสินที่ไม่ขึ้นกับลำดับแถว**
  (ที่นี่ = "ช่วงแคบกว่าชนะ" `SsoRateSchedule.SpanWidth` ⇒ ตั้ง "ทั้งปี 5% +
  ลด 1% เฉพาะ 5–7" ได้โดยไม่ต้องตัดปีเป็นสามท่อน) และปฏิเสธเฉพาะเคสที่**ตัดสิน
  ไม่ได้จริง ๆ** (ทับกันแบบกว้างเท่ากัน) — ห้ามห้ามทุกการทับ เพราะรูปที่กฎหมาย
  ออกจริงคือ "อัตราปกติ + ข้อยกเว้นซ้อนข้างใน" · และพารามิเตอร์ใหม่ที่ตัดสิน
  ตัวเลขเงิน (`month`) **ห้ามมีค่า default** — เส้นที่ "ไม่รู้เดือน" จะคิดผิดเงียบ ๆ
  _(บทเรียนซ้อน: id ของ input ในตารางที่คีย์ด้วย "ปี" อย่างเดียว กลายเป็น **id ซ้ำ**_
  _ทันทีที่หนึ่งปีมีหลายแถว ⇒ `getElementById` หยิบแถวแรกเสมอ = กดบันทึกแล้วทับ_
  _แถวผิด — เปลี่ยนมิติของข้อมูลเมื่อไร ต้องไล่ดูคีย์ของ DOM ด้วยทุกครั้ง)_
- **"ทางเข้าที่ยังเปิดอยู่ = การลบที่ยังไม่จบ"** `ApplyErasureAsync` (PDPA ม.33)
  anonymise ชื่อ/อีเมล/เบอร์ แล้วตั้ง `Status=Inactive` — แต่ **ไม่เคยถอด
  `UserExternalLogins`** ⇒ บัญชีที่ "ลบแล้ว" ยังกดปุ่ม Google/LINE เข้าได้ตามปกติ
  เพราะเส้น SSO ค้นด้วย `ProviderUserId` **ไม่เคยดูอีเมล**ที่เพิ่ง anonymise ไป
  (refresh token ที่ยังไม่หมดอายุก็เหมือนกัน)
  → กติกา: การ "ลบ/ปิด" บัญชีต้องไล่ถอด **ทุกกุญแจที่เข้าได้** พร้อมกัน —
  รหัสผ่าน · บัญชีภายนอกทุก provider · token ที่ออกไปแล้ว · ช่องเดี่ยวเดิม
  (`AuthProvider`/`AuthProviderId`) ที่ยังมีโค้ดเก่าอ่านอยู่. วิธีหา: `grep` ว่า
  **อะไรบ้างที่ resolve ตัวตนผู้ใช้ได้** แล้วถามทีละอย่างว่า "อันนี้ถูกตัดหรือยัง"
- **slug พิเศษของ storefront ห้ามแย่งหน้า CMS ที่ seed ไว้โดยไม่ probe ก่อน** — `/booking` และ `/rooms`
  เป็นหน้า `SitePage` ที่ `CmsSiteTemplateSeeder.HotelPlan` สร้างให้ทุกเว็บโรงแรม; โมดูลที่พักอยาก
  ยึด slug เดียวกัน (ให้ปุ่ม "จองห้องพัก" ที่ seed ไว้พาไประบบจองจริงโดยไม่ต้องแก้เทมเพลต) ⇒
  `tryRouteSpecialSlug` ต้อง `fetch /lodging/info` **ก่อน** แล้วยึดเฉพาะเมื่อมีที่พักผูกจริง —
  เว็บสปา/คลินิกที่ใช้บล็อก BookingCalendar แบบ slot ต้องไม่ถูกกระทบ (ทดสอบ LDG-S-02)
- **โมดูลใหม่ที่มี "เงิน" ห้ามออกเอกสาร/เลขเอง — เดินผ่าน `IDocumentService` เสมอ** (โมดูลที่พัก รอบ 124
  เป็นตัวอย่าง: มัดจำ = `Receipt IsDeposit` · เช็คเอาต์ = `TaxInvoice` + `DepositApplied*` ·
  ยกเลิก = `RefundDeposit`/`RealizeDeposit`) — TakeTime ที่นำมาวิเคราะห์ออกเลขด้วย
  `SELECT TOP 1 … +1` ไม่มี lock และเก็บ `Deposit` เป็น "ยอดจ่ายสะสม" (reuse field ผิดความหมาย)
  ทั้งสองอย่างเป็น defect class ที่ไฟล์นี้ห้ามอยู่แล้ว · ค่าตั้งค่าที่ยังไม่มีใครอ่าน
  (`EarlyCheckInFee/LateCheckOutFee`) ต้องถูกจดใน backlog (`LODGING_TAKETIME_ANALYSIS.md` §2)
  ไม่ปล่อยเงียบ — เป็น "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" ตั้งแต่วันแรกโดยรู้ตัว
- **`Layout.esc` เดิมไม่หนี `"` — 61 จุดที่เขียน `title="${esc(v)}"` เปิดช่อง XSS**
  `textContent → innerHTML` หนีแค่ `& < >` ตาม HTML serialization spec ⇒ ค่าที่มี `"`
  **แตกออกจาก attribute** แล้วผู้โจมตีเติม `onmouseover=` ต่อได้ · ค่าพวกนี้คือชื่อ
  ผู้ติดต่อ · ชื่อสินค้า · หมายเหตุ · ผลอ่าน OCR — ทั้งหมดคนนอกคุมได้ และ JWT อยู่ใน
  localStorage ⇒ XSS = ขโมย token · จุดที่แรงที่สุดคือ **หน้าแอดมิน** ที่แสดงชื่อบริษัท/
  ชื่อผู้ใช้ที่ *ผู้เช่าพิมพ์เอง* ให้ SystemAdmin ดู = ผู้เช่ายึดแพลตฟอร์ม
  → แก้ที่ helper ตัวเดียวให้หนีครบ 5 ตัว (61 จุดหายพร้อมกัน) + เพิ่ม `AdminLayout.esc`
  เพราะหน้าแอดมินไม่ได้โหลด `layout.js` (คนละชั้น จึงเป็นสำเนาที่ **ต้องมี** — ไม่ใช่ drift)
  _(บทเรียนซ้อน 3 ข้อจากการเขียน `tools/html_attr_escape_check.py`: (1) รุ่นแรกฟ้อง_
  _**669 จุด** เพราะจับทุกค่าที่ไม่ผ่านตัวหนี ซึ่งเกือบทั้งหมดเป็น `id`/`class` ที่โค้ด_
  _เราสร้างเอง — checker ที่ฟ้องผิดขนาดนั้นคือ checker ที่พังแล้ว ต้องแคบเหลือ **ชื่อ_
  _ฟิลด์ที่เก็บข้อความอิสระ** (taint ไม่ใช่รูปทรง) เหลือ 11 จุดซึ่งเป็นของจริงทั้งหมด_
  _(2) **negative test จับบั๊กใน checker เอง**: กติกา "ตัวแปรดัชนี `i`/`row` ปลอดภัย"_
  _ไม่ผูกท้าย ⇒ `row.name` ถูกมองว่าปลอดภัยเพราะขึ้นต้นด้วย `row` ทั้งที่เป็นเคสที่_
  _checker มีไว้จับพอดี · พอผูก `\s*$` แล้วเจอของจริงเพิ่มอีก 1 จุด (`i.name`) ที่_
  _กติกาหลวมเคยกลบไว้ (3) 3 จุดหนี `"` เองด้วย `.replace(/"/g,'&quot;')` = สำเนามือ_
  _ที่หนีไม่ครบ (ไม่หนี `&` `<` `'`) — ยุบมาใช้ตัวกลางแทน)_
- **`[Authorize]` ระดับคลาส = "ล็อกอินอยู่ไหม" ไม่ใช่ "มีสิทธิ์ทำสิ่งนี้ไหม"**
  `DocumentController` เช็คสิทธิ์ครบใน create/approve/void จนดูเหมือนคุมแล้ว —
  แต่ **PUT · DELETE · convert · batch-convert · payments · void-payment ·
  write-off · contacts (สร้าง/แก้/ลบ/รวม) รวม 20 endpoint ไม่เช็คอะไรเลย** ⇒
  สมาชิกที่ระบบตั้งใจไม่ให้ *สร้าง* เอกสาร กลับ **แก้ · ลบ · แปลง · บันทึกรับ-จ่าย
  เงิน · ตัดหนี้สูญ** ได้ทั้งหมด (ลง JE จริง) · `PayrollController` หนักกว่า:
  ด่านที่มีคือ `CheckPayrollAccessAsync` ซึ่งถามแค่ **"ดูข้อมูลเงินเดือนได้ไหม"**
  แล้วอีก 20 endpoint (สร้างรอบ · import · คำนวณ · **อนุมัติ** · **จ่าย** ·
  นำส่ง ปกส. · ตั้งค่าอัตราภาษี) ไม่มีด่านเลย ⇒ **สมาชิกคนไหนก็กดจ่ายเงินเดือน
  จริงได้** — และคีย์ `PayrollRun`/`PayrollApprove`/`PayrollPay` มีอยู่ใน
  `PermissionKeys` มาตลอดแต่**ไม่เคยมีใครเรียก** (defect class "ของที่สร้างไว้แล้ว
  ไม่ได้ถูกเรียกใช้")
  → กติกา: ด่านต้องรวมเป็น **เมธอดเดียวต่อคอนโทรลเลอร์** (`DenyDocAsync` /
  `RequirePayrollWriteAsync`) เพราะข้อความปฏิเสธต้องบอก **ชื่อคีย์ที่ต้องขอ**
  ให้ตรงกันทุกจุด — 40 จุดที่ต่างคนต่างแต่งข้อความจะ drift แน่นอน · และเลือก
  ระดับสิทธิ์จาก **ผลกระทบ** ไม่ใช่จาก HTTP verb: แตะแต่ร่าง = Create ·
  โพสต์ JE/ขยับเงิน = Approve/Pay · กลับรายการ = Void
  _(→ เพิ่ม `tools/write_permission_gate_check.py` — โยง "verb ของ route" เข้ากับ_
  _"ด่านที่เมธอดเรียก" ซึ่งคอมไพเลอร์ไม่มีทางเห็น. **เป็น allow-list ต่อคอนโทรลเลอร์_
  _ไม่ใช่กวาดทั้งเรพ** เพราะคอนโทรลเลอร์สาธารณะ (portal แขก · webhook) ตั้งใจไม่มี_
  _ด่านผู้ใช้ — กวาดหมดจะฟ้องผิดเป็นสิบจุด และ "checker ที่ฟ้องผิด = checker ที่พัง_
  _แล้ว" · POST ที่อ่านอย่างเดียวต้องใส่ชื่อใน `READ_ONLY_POSTS` ให้เป็นการตัดสินใจ_
  _ที่ตั้งใจ ไม่ใช่ยกเว้นอัตโนมัติ)_
- **ป้ายที่ติดผิดบนแถวข้อมูล ย้อนกลับมาบล็อกฟีเจอร์ของตัวเอง** — 27 endpoint
  ใน `AiSuggestionController` เป็น heuristic ล้วน (history-mode · stats · bigram ·
  keyword · lookup · rules · memory) ไม่เคยยิง provider เลย แต่บันทึกแถว feedback
  เป็น `Status=Success` + `ProviderUsed=DeepSeek` ⇒ `AiBudgetGuard` นับเป็น call
  ที่เสียเงิน ⇒ **วันที่ผู้ใช้กดปุ่มแนะนำ (ฟรี) เยอะ daily cap เต็ม แล้วบล็อก AI
  ของจริงทั้ง tenant** · และยิ่ง local model แม่นขึ้น (ยิง provider น้อยลง) cap
  ยิ่งเต็มเร็วขึ้น = **ตรงข้ามกับเจตนาของกฎเหล็ก #1 ทุกประการ** · ผลพลอยได้อีกทาง
  คือรายงานขึ้น "สำเร็จ (เรียก AI)" ให้ call ที่ไม่เคยเกิด ⇒ ตัวชี้วัด
  "`UsedAi` ลดลงเรื่อย ๆ" (กฎเหล็ก #1 ข้อ 6) อ่านไม่ได้เลย. คอมเมนต์เหนือตัวกรอง
  ใน `AiBudgetGuard` **เขียนเจตนาไว้ถูกตั้งแต่ต้น** ("นับเฉพาะ call ที่ยิง provider
  จริง") แต่ตัวกรองดูแค่ *สถานะ* — เจตนาที่ถูกไม่ได้แปลว่าโค้ดทำตาม
  → กติกา: เมื่อบันทึกแถวเพื่อ **หลายวัตถุประสงค์** (training · คิดเงิน · ตัวชี้วัด)
  ต้องมีช่องที่บอก "เกิดอะไรขึ้นจริง" แยกจาก "ผลลัพธ์ที่ผู้ใช้เห็น" —
  ที่นี่คือ `AiCallStatus.LocalServed` + `ProviderUsed=None` · และตัวตัดสิน
  "แถวไหนเสียเงิน" ต้องอยู่ที่เดียว (`Helpers/AiCallBilling.BillableRow` เป็น
  `Expression` เพื่อให้ **SQL กับเทสต์ใช้กติกาเดียวกัน**)
  _(สองบทเรียนซ้อน — (1) **แถวลูกสังเคราะห์ต้องไม่ถูกนับซ้ำ**:_
  _`SynthesiseChildFeedbackAsync` แตกคำตอบ bulk 1 ครั้งเป็นลูกหลายสิบแถวเพื่อให้_
  _distillation เรียนรายบรรทัด — ทุกแถวเป็น `Success` ⇒ provider call เดียวถูกนับ_
  _หลายสิบครั้ง (คัดออกด้วย `CacheHitOfFeedbackId != null`)_
  _(2) **แก้โค้ดอย่างเดียวไม่พอเมื่อของเสียถูก persist ไว้แล้ว** — แถวเก่ายังเป็น_
  _`Success+DeepSeek` ตลอดไป ต้องมี migration ล้าง และเกณฑ์คัดต้องแม่นทั้งสองทิศ:_
  _ปล่อยแถวจริงหลุด = cap ไม่กันของจริง · ตีแถวจริงเป็น local = นับต้นทุนขาด →_
  _ใช้ลายเซ็นของ "HTTP call ที่เกิดจริง" สามอย่างพร้อมกัน (มี latency · มี token ·_
  _มีต้นทุน) ซ้ำรอย `OcrLearnedPatterns.ExtractionRegex` / `VendorKnownGoodValues`)_
- **ตัวหนีที่หนี "เกือบครบ" อันตรายกว่าไม่มีตัวหนีเลย** ต่อจากบทเรียน `Layout.esc`
  รอบก่อน (แก้ที่ตัวกลางตัวเดียว 61 จุดหายพร้อมกัน) — พอ `grep` ทั้งเรพกลับพบ
  **สำเนามือของตัวหนีอีก 25 ตัว** ที่หนีแค่ 2-4 ตัว (`_esc` ใน pos · pdpa · risk ·
  tax · bank · consignment · production · vendor-portal · 5 หน้าแอดมิน ฯลฯ) ⇒ ค่าที่มี
  `"` ยัง**แตกออกจาก attribute**ได้เหมือนเดิม. อันตรายเป็นพิเศษเพราะคนอ่านโค้ดเห็น
  `esc(...)` ครอบอยู่แล้วเชื่อว่าจบ — เป็นญาติของ "ด่านที่เขียนไว้ครึ่งเดียว"
  → หน้าที่โหลด `layout.js`/`admin-layout.js` อยู่แล้วให้ **มอบต่อ**
  (`_esc(s){ return Layout.esc(s); }`) ห้ามคัดลอกสูตร · หน้าที่ไม่โหลด (widget ฝัง
  เว็บนอก · vendor portal) หนีให้ครบในที่ของมันเอง
  _(→ เพิ่ม `tools/escape_helper_check.py`. **บทเรียนซ้อน**: รุ่นแรกสแกน_
  _**ทีละบรรทัด** จึงไปฟ้อง `Layout.esc` · `jsArg` · `export.js` ซึ่งเขียนบรรทัดละ_
  _`.replace()` ตัวเดียวและ**ถูกต้องอยู่แล้ว** 7 จุด — ต้องมองโซ่ `.replace()` ที่_
  _ต่อกันข้ามบรรทัดเป็นหน่วยเดียว ("checker ที่ฟ้องผิด = checker ที่พังแล้ว")_
  _และ negative test ต้องมีทั้งรูปผิดข้ามบรรทัดและรูปถูกข้ามบรรทัด ไม่งั้นแยกไม่ออก)_
- **`esc()` กับ `jsArg()` สลับที่กันไม่ได้ และ "หนีสองชั้น" ก็ผิดพอกัน** ตอนไล่ห่อ
  `AdminLayout.esc` 147 จุด ผมห่อทับค่าที่อยู่ใน **JS string ของ `onclick`** ไปด้วย
  7 จุด — `onclick_js_string_check` จับได้ทันที (esc คืน `&#39;` ซึ่งเบราว์เซอร์
  decode กลับเป็น `'` **ก่อน** parser ของ JS อ่าน ⇒ ปิด string ได้อยู่ดี) · ทิศตรงข้าม
  ก็มีอยู่แล้วในเรพ 4 จุด: `Layout.jsArg(x).replace(/'/g,'&apos;')` — หนีระดับ JS
  แล้วเอา entity มาทับ `\'` ⇒ ชื่อกลายเป็น `&apos;` บนหน้าจอ
  → กติกา: **ตำแหน่งที่วางเป็นตัวเลือกฟังก์ชัน ไม่ใช่ชนิดของข้อมูล** — ใน JS string
  ของ `onclick` ใช้ `jsArg` เท่านั้น · ที่อื่นใช้ `esc` เท่านั้น · ห้ามต่อท้ายอีกชั้น
- **เรียงเลขรันแบบข้อความ = เลขวนกลับไปทับของเดิม เงียบสนิท** จุดที่ออกเลขเอง
  ด้วย `OrderByDescending(x => x.SomethingNumber).First() + 1` มี 9 จุดในเรพ
  ทุกจุดพลาด **3 อย่างพร้อมกัน**: (1) ไม่มี advisory lock (2) เรียงแบบ
  lexicographic ⇒ `"…9999"` ชนะ `"…10000"` ⇒ พอทะลุหลักพัน เลขวนกลับไปทับใบเก่า
  (3) ไม่นับแถวที่ยัง `Add` ค้างใน change tracker ⇒ งานที่สร้างสองแถวในธุรกรรม
  เดียว (กลับรายการ + ตั้งใหม่) ได้เลขซ้ำกันเอง · อาการเงียบเพราะเลขที่ได้
  **ดูปกติทุกประการ** — ที่กันไว้ชั้นสุดท้ายคือ unique index ซึ่งกลายเป็น
  "บันทึกไม่สำเร็จแบบสุ่มที่กดใหม่แล้วหาย" ส่วนตารางที่**ไม่มี** unique index
  ก็ซ้ำเงียบไปเลย · รหัสสินทรัพย์ยิ่งชัด: มีสำเนา `GenerateAssetCodeAsync`
  **เหมือนกันคำต่อคำสองชุด** (`FixedAssetService` + ตอนขึ้นทะเบียนจากใบซื้อ)
  → `Helpers/SequenceNumber` (ล็อก + integer-max + tracker) ตัวเดียว ·
  `Helpers/AssetCodeGenerator` · JE ทุกจุดเดินผ่าน `JournalEntryBuilder`
  _(→ `tools/sequence_lock_check.py`. **บทเรียนซ้อน 2 ชั้นตอนเขียน checker**:_
  _(1) ดู "บรรทัดใกล้ ๆ" ว่ามี `FirstOrDefaultAsync` ไหม → ไปฟ้องการเรียงเพื่อ_
  _**แสดงผล** (`…Skip().Take().ToListAsync()`) เพราะเมธอดถัดไปมีคำนั้น — ต้องดู_
  _**คำสั่งเดียวกัน** (จนถึง `;`) (2) `OrderByDescending(a => a.AccountCode.Length)`_
  _เป็นการ**ค้นหา**ผังบัญชีที่ prefix ยาวสุด ไม่ใช่การออกเลข — regex ต้องบังคับว่า_
  _จบที่ตัวคอลัมน์จริง ๆ `(?!\s*[.\w])`)_
- **งานเบื้องหลังที่ไม่มีล็อก ไม่ได้แค่ "ทำงานซ้ำ" — มันเขียนข้อมูลจริง** จาก 16 job
  มีล็อกแค่ 5 · ที่เหลือรันพร้อมกันได้ทุกเครื่อง: ค่าเสื่อมลง JE **สองเท่า**
  (ค่าใช้จ่ายเกิน · ค่าเสื่อมสะสมเกิน · กำไรในงบที่ยื่นต่ำกว่าจริง) · ค่าปรับล่าช้า
  คิดซ้ำ = เก็บลูกค้าเกิน · อีเมลทวงหนี้ถึงลูกค้า **N ฉบับตามจำนวนเครื่อง** ·
  งานลบข้อมูลตามอายุรันพร้อมกันสองเครื่อง
  → `Helpers/JobLock` ตัวเดียว ใช้ **try ไม่ใช่ wait** (งานตามตารางที่อีกเครื่อง
  ทำอยู่ การรอ = ทำงานเดิมซ้ำเปล่า ๆ ข้ามไปรอบหน้าถูกกว่า) · ต้องเป็น
  **session-level** (`pg_try_advisory_lock`) ไม่ใช่ xact lock เพราะ job ทั่วไป
  `SaveChanges` หลายครั้ง ไม่ได้อยู่ในธุรกรรมเดียว · ปลดล็อกใน `finally` **เสมอ**
  ไม่งั้นค้างจนกว่า connection จะถูกคืน pool แล้วทั้งคลัสเตอร์ข้ามงานนั้นไปเรื่อย ๆ
  _(หน่วงเริ่มงานเท่ากันทุกเครื่อง ⇒ ตื่นพร้อมกัน**เกือบเสมอ** ไม่ใช่ "นาน ๆ ที")_
- **state ที่ผูกกับเครื่อง จะพังตอนมีเครื่องที่สอง โดยไม่มี error ให้เห็น**
  key ring ของ DataProtection เก็บบนดิสก์ต่อ instance ⇒ PII ที่เครื่อง A เข้ารหัส
  เครื่อง B ถอดไม่ออก และ `TryDecrypt` คืน `null` **เงียบ ๆ** ⇒ ผู้ใช้เห็นเลขบัตร/
  เลขบัญชี/ลายเซ็นเป็นช่องว่าง **สลับไปมาตามเครื่องที่ load balancer ส่งไป** ·
  pod restart ที่ไม่ได้ mount volume = คีย์หายถาวร ข้อมูลกู้ไม่ได้เลย
  → ย้ายไปฐานข้อมูล (เขียน `IXmlRepository` เอง ~90 บรรทัด ไม่ต้องเพิ่ม NuGet —
  DataProtection ถูกสร้างก่อน DbContext พร้อม และอ่านคีย์แบบ **synchronous**
  จึงต้องใช้ ADO.NET ตรง ห้ามเรียก EF async จากตรงนั้น)
  · **แคชที่ป้อนจากเส้นไม่ต้องล็อกอิน ต้องมีเพดาน+TTL เสมอ** (แคชคำตอบแชท
  สาธารณะ · dict ของ rate limiter ที่คีย์ด้วย IP จากอินเทอร์เน็ต) ไม่งั้นคือ
  memory leak ที่ผู้โจมตีสั่งได้ฟรี
- **middleware ที่ตัดสินสิทธิ์ ต้องอยู่หลังจุดที่พิสูจน์ตัวตนแล้ว** rate limiter
  อยู่ก่อน `UseAuthentication()` แต่ตัดสิน tier จาก "มี header `Authorization`
  ไหม" ⇒ ใครส่ง `Authorization: x` มาก็เลื่อนตัวเองจาก 600 เป็น 3,000 ครั้ง/นาที
  ได้ฟรี **โดยไม่ต้องมีบัญชี** — เพดานที่ตั้งไว้กันยิงถล่มกลายเป็น 5 เท่าของที่ตั้งใจ
  → ย้ายมาหลัง `UseAuthentication()` แล้วถาม `context.User.Identity.IsAuthenticated`
  ที่ผ่านการตรวจลายเซ็นจริง · เส้นล็อกอิน/สมัครยังเป็น anonymous จึงยังโดน tier
  เข้มต่อ IP เหมือนเดิม
  · ญาติกัน: **BCrypt ถูกออกแบบให้ช้าโดยตั้งใจ** (~100ms) เพราะใช้กับรหัสผ่านที่
  คนพิมพ์ ไม่ใช่ header ที่คู่ค้ายิงมาทุก request ⇒ CPU เต็ม thread pool ตัน
  ทั้งเซิร์ฟเวอร์ช้า — แคชผลได้ แต่**แคชเฉพาะ "ลายเซ็นถูกไหม"** ส่วนสถานะ
  เพิกถอน/หมดอายุ/IP ต้องตรวจจาก DB ทุก request (ไม่งั้นเพิกถอนแล้วยังใช้ได้)
  และ**ห้ามแคชผลที่ไม่ผ่าน** ไม่งั้นเดาคีย์ผิดไปเรื่อย ๆ ก็ทำให้ dict โตได้ฟรี
- **"ด่านที่ครอบแค่ทางเดียว คือด่านที่ไม่มี"** `ProhibitedInputVatScreener` (§82/5)
  ถูกเรียกจากสาย OCR ที่เดียว ⇒ ใบที่คีย์มือ / นำเข้า CSV / ยิงผ่าน API เลือกผัง
  "ค่ารับรอง" หรือ "ค่าน้ำมัน" แล้ว **เคลมภาษีซื้อผ่านฉลุย** ⇒ ยื่น ภ.พ.30 เกินสิทธิ์
  · รูปเดียวกันคนละที่: `ResolveFiscalPeriodAsync` ไม่ดู `Status` ⇒ 9 เส้นยัด JE
  เข้างวดที่**ปิดและยื่นแบบไปแล้ว** ขณะที่เส้นลง JE ด้วยมือตรวจอยู่แล้วมาตั้งแต่ต้น
  = กติกาเดียวกัน**สองมาตรฐาน** · และ endpoint **อ่าน** 7 ตัวของ payroll ไม่มีด่าน
  เลย ทั้งที่ฝั่ง **เขียน** ใส่ครบ 30 ตัวไปแล้วรอบก่อน
  → หลังใส่ด่านที่ไหนก็ตาม ให้ `grep` หา **ทุกทางเข้าที่แตะข้อมูลชุดเดียวกัน**
  แล้วถามทีละทาง — "แก้ตัวเดียว เหลือที่เหลือ" มีหลายทรง: คนละไฟล์ · คนละ
  ทางเข้า · และ**ฝั่งอ่าน vs ฝั่งเขียน**
- **สองฝั่งเขียนถูกทั้งคู่ แต่สายขาดหนึ่งเส้น = ฟีเจอร์ตายทั้งก้อนตั้งแต่วันแรก**
  `PublicApiControllerBase` ปฏิเสธ API key ที่ `Scopes` ว่างทุกใบ (ถูกต้อง — สิทธิ์
  ต้องตั้งใจให้) แต่ **จุดสร้าง ApiKey ไม่เคยเซ็ตช่องนั้นเลยสักครั้ง** (`grep
  '.Scopes = '` = 0) ⇒ `/api/v1` ทั้งหมด (ocr · bank · contacts · documents)
  **เข้าไม่ได้เลยสักเส้น** ทั้งที่โค้ดทั้งสองฝั่งอ่านแล้วถูกต้องในตัวมันเอง —
  ไม่มีใครผิด มีแต่สายที่ขาด · หาไม่เจอด้วยการอ่านไฟล์เดียว ต้อง `grep` ว่า
  **ใครเขียนค่านี้บ้าง** ไม่ใช่แค่ "ใครอ่าน"
- **เรียกเมธอดที่ไม่มีนิยาม — JS ไม่ฟ้องจนกว่าจะถึงบรรทัดนั้น และบางที่ก็กลืนทิ้ง**
  `documents.html` เรียก `this.edit()` (ของจริงชื่อ `openEdit`) ใน `setTimeout`
  ที่ไม่มี `catch` ⇒ คัดลอกเอกสารสำเร็จแต่ใบใหม่ไม่เปิด ผู้ใช้เห็นแค่หน้าเดิม ·
  และเขียน `if (typeof this.viewDetail === 'function')` กับเมธอดที่ไม่เคยมี
  ⇒ **เท็จเสมอ** ⇒ หลังหักมัดจำหน้ารายละเอียดไม่รีเฟรช ยอดค้างชำระค้างค่าเดิม
  → `typeof … === 'function'` รอบเมธอดของ **ตัวเอง** เป็นกลิ่นเสมอ: ถ้ามีจริงก็
  ไม่ต้องถาม ถ้าไม่มีก็คือ dead branch ที่ปิดบั๊กไว้ · เช่นเดียวกับ id ของ DOM —
  `documents.html` อ่าน 4 id ที่**ไม่มีอยู่ในหน้าเลยสักตัว** ⇒ ได้ 0 เสมอ
- **ลืม `AwayFromZero` ≠ ยอดผิด — แต่ "ตกจุดกึ่งกลางได้" ก็ไม่ใช่ทุกสูตร**
  (ต่อจากบทเรียน `VatRoundingMode`) รอบนี้ไล่ค่าจริงก่อนแก้: `x / 2` ตกจุด
  กึ่งกลาง **ทุกครั้งที่สตางค์เป็นเลขคี่** · `x × 0.15` ตกที่สตางค์ ≡ 10 (mod 20)
  · **`x × 0.20` ไม่เคยตกเลย** (ไล่ 2 ล้านค่า) → สองตัวแรกเป็นบั๊กจริง ตัวที่สาม
  ใส่ให้เหมือนกันทั้งเมธอดในฐานะ **การป้องกัน** และ**จดไว้ว่าตัวไหนเป็นตัวไหน**
  ไม่งั้นรอบหน้าจะมีคนอ่าน commit แล้วเข้าใจว่าทั้งสามเคยผิด
- **ล้มดังพร้อมทางไปต่อ ดีกว่าคัดลอกอัลกอริทึมมาไว้ที่สอง** แก้โบนัส/OT ในโมดัล
  เงินเดือนแล้ว `WithholdingTax` ค้างค่าเดิม ⇒ หักขาด แล้วผู้จ่ายรับผิด §54 ·
  ทางที่ดู "ครบ" คือคำนวณภาษีใหม่ตรงนั้น — แต่สูตรต้องใช้บริบท**ทั้งปี** (รายได้
  สะสม · ลดหย่อนรายช่อง · ตารางขั้นของบริษัท · งวดที่เหลือ) ที่เมธอดนั้นไม่มี
  ⇒ คัดลอกมา = อัลกอริทึมภาษีชุดที่สองที่จะ drift แน่นอน (defect class ที่ไฟล์
  เดียวกันเพิ่งยุบทิ้งไปในรอบ `ThaiPitCalculator`)
  → ปฏิเสธพร้อมบอกสองทางเลือกที่ทำได้จริง (กรอกภาษีเอง / กดคำนวณใหม่ทั้งรอบ)
  — **"ไม่รู้ = บอกว่าไม่รู้" ใช้กับตัวเราเองด้วย ไม่ใช่แค่กับข้อมูลของผู้ใช้**
- **doc-comment ที่เขียนสูตรถูกไว้ ไม่ได้แปลว่าโค้ดทำตาม** `RetentionUntil` มี
  คอมเมนต์เขียนชัดว่า "นับจาก MAX(วันสิ้นรอบบัญชี, วันที่เอกสาร) + 5 ปี" แล้ว
  บรรทัดถัดมาเขียน `DocumentDate.AddYears(5)` พร้อมหมายเหตุว่า "ใช้ simple rule"
  ⇒ สั้นไปเกือบ 12 เดือน (ถึง ~14 ถ้ารอบไม่ตรงปีปฏิทิน) ⇒ เสี่ยงลบเอกสารที่
  สรรพากรยังเรียกดูได้ · ญาติของ "ด่านที่ doc บอกว่ามี แต่ไม่มีใครเรียก"
  → เวลาอ่านโค้ดเก่า ให้เทียบ**คอมเมนต์กับบรรทัดถัดไป** เสมอ — ที่ที่ผู้เขียน
  รู้ว่าอะไรถูกแต่ยังไม่ได้ทำ คือที่ที่บั๊กรออยู่ · และเมื่อแก้ ต้องมี migration
  ด้วย (`GREATEST` เพื่อ**ไม่ย่นของใคร** — retention เป็น MAX ของทุกกฎที่ครอบ)
- **`"` ASCII ในสตริง *ธรรมดา* ก็ระเบิดเหมือนกัน — และไม่มี checker ตัวไหนดูอยู่เลย**
  CLAUDE.md มีบทเรียนนี้อยู่แล้วสำหรับ `$@"..."` (เคส `BuildCss`) ผมจึงเชื่อว่า
  "ด่านมีแล้ว" — แต่ `verbatim_string_check.py` **ข้าม string ธรรมดาทั้งหมด**
  (มันถูกเขียนมาตรวจ verbatim/raw โดยเฉพาะ) ⇒ ตอนเขียนข้อความบอกทางแก้ของด่าน
  งวดปิด `$"ลง{what}เข้างวด "{period.Name}" ไม่ได้ …"` + `"หน้า "งวดบัญชี" …"`
  อัญประกาศ ASCII **ปิดสตริงกลางคำ** ⇒ CS1002 ลามทั้งไฟล์ + `Accounting.Tests`
  พังตามด้วย CS0006 — **หลุดผ่าน checker 28 ตัวและถูก push ไปแล้ว** เจอตอน
  ตรวจย้อนของตัวเองรอบถัดไป → เพิ่ม `tools/string_quote_close_check.py`
  _(บทเรียนซ้อน 4 ข้อจากการเขียน checker — ผ่าน 4 รุ่น: (1) slice `text[i:]` ในลูป_
  _= O(n²) กับไฟล์ 1 MB ⇒ **timeout 120 วิ รันไม่จบ** ต้อง `regex.match(text, i)`_
  _(2) ไม่ track **interpolation hole** ⇒ ฟ้องผิด **382 จุด** (`{x ?? ""}` ·_
  _`{id.ToString("N")}`) — ซ้ำรอยบทเรียนเดียวกันของ `verbatim_string_check`_
  _(3) ตอนแก้ลิสต์ "ตัวอักษรที่ปิดสตริงได้" ทำ `,` **หล่นหาย** ⇒ ฟ้องผิดพุ่งเป็น_
  _**16,791 จุด**: ตัวเลขที่พุ่งผิดปกติหลังแก้จุดจิ๋ว ๆ = สัญญาณว่า state machine_
  _desync **ไม่ใช่** "เจอบั๊กเพิ่ม" — ต้องกลับไปดูสิ่งที่เพิ่งแก้ ไม่ใช่ไล่อ่านผลฟ้อง_
  _(4) `$"…"[..14]` (range indexer) ถูกต้องตามภาษา ต้องอยู่ในลิสต์ตัวปิดด้วย)_
- **`Debug.WriteLine` ไม่ใช่การ "ดัง" — มันถูกคอมไพล์ทิ้งใน Release**
  `SequenceNumber.NextAsync` เตือนว่า "ออกเลขนอก transaction ⇒ advisory lock ถูก
  ปล่อยก่อน insert" ด้วย `Debug.WriteLine` **พร้อมคอมเมนต์ที่เขียนเองว่า "ต้องดัง
  พอให้เห็นตอนตรวจ log (กติกาห้ามเงียบ)"** — บนเครื่องจริงบรรทัดนั้นไม่มีอยู่เลย
  ⇒ คำเตือนที่ตั้งใจให้ดัง เงียบสนิทในที่เดียวที่มันสำคัญ (ญาติของบทเรียน
  "`LogWarning` แล้วเดินต่อ = ดังในที่ที่ไม่มีคนดู") → ยิงผ่าน `ILogger` จริง
  (static property ตั้งครั้งเดียวตอน boot) และ fallback `Console.Error` เมื่อยัง
  ไม่มีใครตั้ง — **ห้ามมีทางที่คำเตือนหายไปทั้งหมด**
- **doc drift เกิดกับตัวเองได้ ถ้าไม่ไล่เช็คว่า "คอมมิตนี้แตะข้อไหนในลิสต์บังคับ"**
  4 คอมมิตของรอบ multi-instance/compliance เปลี่ยน flow จริง 2 ข้อ (ด่านงวดปิดใน
  ขั้นอนุมัติ = ข้อ 3 · retention = ข้อ 13 ของลิสต์ "ต้องอัปเดตเมื่อ...") แต่
  **ไม่มีคอมมิตไหนแตะ `DOCUMENT_FLOW.md` เลย** — กฎเขียนไว้ชัดแล้วยังพลาด เพราะ
  ตอน commit คิดว่า "นี่งานแก้บั๊ก ไม่ใช่เปลี่ยน flow" → ตัวชี้วัดที่ใช้ได้จริงคือ
  **"เพิ่ม/ลด `throw` หรือเปลี่ยนสูตรของค่าที่ persist ลงเอกสาร = เปลี่ยน flow เสมอ"**
- **"อ้างสมาชิกที่ไม่มีอยู่บนชนิดที่ถืออยู่จริง" = CS1061/CS0117 ล้มทั้ง solution —
  และรอบเดียวหลุดไปพร้อมกัน 4 ตัว** ผู้ใช้ส่ง build error กลับมา 9 ข้อ ซึ่งยุบเป็น
  ต้นเหตุเดียวกันหมด: เขียนชื่อสมาชิกจาก **ชนิดที่คิดว่าถืออยู่** ไม่ใช่ชนิดจริง —
  (1) `CompanyUser.IsDeleted` — join entity ตัวนี้ **ไม่ได้สืบทอด `BaseEntity`**
  การถอนสมาชิกคือลบแถวจริง (2) `Contact.Notes` — `Document` มี `Notes` แต่ `Contact`
  ไม่มี (และช่องที่ต้องใช้จริงคือ **`InternalNotes`** ตามกฎ "ช่องที่ผู้ใช้มองเห็น
  กับเก็บไว้ภายใน ห้ามใช้ช่องเดียวกัน" — ซึ่งยังต้องเพิ่มทั้งพร็อพเพอร์ตี้ **และ**
  บรรทัด `ADD COLUMN IF NOT EXISTS`) (3) `Document.OurRole` — พารามิเตอร์ตัวที่สอง
  ของ `DocumentSide.IsPurchase` ไม่มีอยู่จริง (จุดเรียกที่ถูกต้องอยู่ใน
  `TaxService.cs` มาตลอด) (4) `UploadFileKind.Value` — `?? throw` **แกะ
  `Nullable<T>` ให้แล้ว** ตัวแปรจึงเป็น `T` ไม่ใช่ `T?`
  · กติกาที่ใช้ได้จริง: ก่อนเขียน `x.Member` บนตัวแปรที่ไม่ได้ประกาศในบรรทัดที่มอง
  เห็น ให้เปิด**คำประกาศของชนิดนั้น**ก่อน โดยเฉพาะ (ก) entity vs DTO ที่ชื่อเหมือนกัน
  (ข) join entity ที่ไม่สืบทอด `BaseEntity` (ค) ตัวแปรที่ผ่าน `?? throw`
  _(**เขียน `tools/nullable_unwrap_check.py` แล้วต้องทิ้ง — จดไว้กันคนถัดไปสร้างซ้ำ**:_
  _กติกา "`var x = … ?? throw …;` แล้วมี `x.Value`" ดูเหมือนเป็นรูปทรงของโค้ดล้วน ๆ_
  _(หลักฐานอยู่ในเมธอดเดียวกัน ไม่ต้อง resolve type) — รันจริงบนเรพแล้ว **ฟ้องผิด**_
  _ที่ `JwtHelper.GetUserIdFromClaims`: `Claim` เป็น **reference type ที่มี `.Value`_
  _จริง** ⇒ `?? throw` บนมันถูกต้องทุกประการ · เรพมี `?? throw` **672 จุด** ซึ่ง_
  _เกือบทั้งหมดเป็น reference type และคลาสของ false positive เปิดกว้าง (`Lazy<T>` ·_
  _`StrongBox<T>` · `IOptions<T>` ล้วนมี `.Value`) — แยกไม่ออกโดยไม่มี type_
  _resolution **เหมือนเคส `.HasValue` ที่ถูกทิ้งไปแล้วทุกประการ** ⇒ นี่คือกำแพงเดิม_
  _ตัวเดิม ไม่ใช่ปัญหาที่เขียน regex ให้ดีขึ้นแล้วจะผ่าน. **ห้าม ship checker ที่_
  _ฟ้องผิดแม้จุดเดียว** — ทางที่คุ้มกว่าคือ `dotnet build` ฝั่งผู้ใช้ ซึ่งจับได้ทันที_
  _· บทเรียนซ้อนตอนเขียน: regex `(?:[^;{}]|\n)*?` **รันไม่จบใน 120 วิ** เพราะ_
  _`[^;{}]` ครอบ `\n` อยู่แล้ว ⇒ สองทางเลือกที่แมตช์ตัวเดียวกัน = catastrophic_
  _backtracking — ซ้ำรอย `string_quote_close_check` รุ่นแรก: **ก่อนสงสัยว่าไฟล์ใหญ่_
  _เกินไป ให้ดูว่า character class กับ `|\n` ทับกันหรือเปล่าก่อน**)_
- **คัดลอกแถวด้วย "รายการช่องที่เขียนมือ" = ลืมแน่นอน — ต้องกลับด้านเป็น deny-list**
  เส้นทาง cache ของ OCR (ไฟล์ hash ตรง → คืนผลเดิมโดยไม่เรียก engine ซ้ำ) คัดลอก
  ค่าจากสแกนเก่าด้วยรายการที่พิมพ์เอง **10 ช่องจาก 49** ⇒ อีก **39 ช่องหายเงียบ**
  ทุกครั้งที่ผู้ใช้อัปโหลดไฟล์เดิมซ้ำ: `RawTextContent` · `ExtractedItemsJson`
  (**ไม่มีรายการสินค้าสักบรรทัด** ผิดกฎเหล็ก #3) · `FieldConfidenceJson`
  (ไฮไลต์เหลืองไม่ขึ้นแต่ยังโชว์ 95%) · ช่อง §86/4 ฝั่งผู้ซื้อ ·
  `TargetDocumentType` (สายสร้างเอกสารอ่านช่องนี้ ไม่ใช่ `DocumentType`) · หมวด
  ค่าใช้จ่าย · WHT · เครดิตเทอม — สังเกตว่าช่องที่หายคือ**ช่องที่ถูกเพิ่มทีหลัง**
  ทั้งหมด: ทุกครั้งที่มีคนเพิ่ม field ต้องมีคนจำได้ว่าต้องมาเติมที่ตัวคัดลอกด้วย
  ซึ่งไม่มีทางเกิดขึ้นครบ · และมันไม่ใช่แค่ "ช่องว่างบนจอ" — `RawTextContent`
  ป้อน 8 จุดที่ตัดสินตัวเลขจริง (สกุลเงิน · เหตุผลใบลดหนี้ §86/10 · ประเภทเงินได้
  50 ทวิ · เงินมัดจำ · คำเตือน RD) ⇒ **กระดาษใบเดียวกันได้เอกสารคนละหน้าตา**
  → กติกา: การคัดลอกแถวต้องอยู่ที่ helper ตัวเดียวที่ **คัดลอกทุกช่อง ยกเว้น
  รายการ "ตัวตนของแถว" ที่ระบุชื่อไว้ชัด** (`Helpers/OcrScanSnapshot`) ⇒ ช่องใหม่
  ตามมาเอง และทิศของความผิดพลาดกลายเป็น "คัดลอกเกิน" ซึ่งเห็นทันที แทน
  "คัดลอกขาด" ซึ่งเงียบสนิท · ตัวที่กันตัวถัดไปคือ **เทสต์ reflection ที่เติมค่า
  ให้ทุกช่องแล้วพิสูจน์ว่าไม่มีช่องไหนหลุด** (regex มองไม่เห็นว่าช่องไหนถูกคัดลอก)
  _(สี่บทเรียนซ้อน — (1) **ช่องที่ "ห้ามคัดลอก" ต้องคิดทีละตัว ไม่ใช่เหมา**:_
  _`CreatedDocumentId` คัดลอกมา = สำเนาอ้างว่าสร้างเอกสารของต้นฉบับไปแล้ว ⇒ ปุ่ม_
  _สร้างเอกสารหาย · `StockImportedAt` = นำสต็อกเข้าไม่ได้ตลอดกาล ·_
  _`ExternalMetadataJson` เป็นของ**การอัปโหลดครั้งนี้** ไม่ใช่ของกระดาษ · แต่ FK ของ_
  _AI feedback **ต้องคัดลอก** เพราะค่าที่โชว์มาจากคำตอบนั้นจริง ๆ ถ้าไม่พาไปด้วย_
  _`RecordUserChoiceAsync` จะไม่มีแถวให้เขียนกลับ = วงจร distillation ขาด (กฎเหล็ก #1)_
  _(2) **ปุ่มที่ผู้ใช้จะกดต่อจากอาการ ต้องตรวจด้วยเสมอ** — ปุ่ม "สแกนใหม่" ส่ง_
  _**scanId** ไป endpoint ที่รับ **fileAttachmentId** ⇒ 500 "File attachment not_
  _found." มาตลอด (defect class `arg_type_check`) และ retry ก็ไม่ได้ข้าม cache_
  _จึงคืนสำเนาเดิมแล้วตอบว่า "สำเร็จ" (silent no-op) พร้อมทิ้งแถวเดิมค้าง_
  _`Processing` ตลอดกาล — **บั๊กเดียวมักมีทางออกที่พังรออยู่ปลายทาง**_
  _(3) **ข้อความ error ที่เดาสาเหตุแทนผู้ใช้** (หน้าจอโทษ Docker ทั้งที่ช่อง Engine_
  _เขียนว่า `Cached`) เป็นบทเรียนเดิมของ CSP/Google SSO ซ้ำอีกรอบ — ข้อความว่าง_
  _ต้องแตกตามสถานะจริงของแถว ไม่ใช่พิมพ์คำวินิจฉัยสำเร็จรูป_
  _(4) **ที่รายงานเองแล้วผิด**: ตอนวิเคราะห์ครั้งแรกผมบอกว่าเส้น cached "เสียโควตา_
  _ให้งานที่ไม่ได้ทำ" — ไม่จริง `OcrController` คืนโควตาเมื่อ `result.IsDuplicate`_
  _อยู่แล้ว (จดไว้ตามกติกา "ผลตรวจผิดได้ทั้งสองทาง — ต้องบันทึกว่าผิด ไม่ใช่ข้ามเงียบ"))_
- **checker ที่เป็น allow-list ต้องถามทุกครั้งว่า "ไฟล์ที่เพิ่งแตะ อยู่ในลิสต์ไหม"**
  `write_permission_gate_check` เฝ้าแค่ `DocumentController` + `PayrollController`
  (ตั้งใจ — กวาดทั้งเรพจะฟ้องผิดที่ controller สาธารณะ) แต่ `MeteringController`
  **ไม่เคยอยู่ในลิสต์** ทั้งที่ทุก write ที่นั่น "ก่อค่าใช้จ่ายให้บริษัท"
  (เปิด add-on รายเดือน · ซื้อโควตา) ⇒ มีแค่ `[Authorize]` ระดับคลาสมาตลอด
  = สมาชิกคนไหนก็กดแทนบริษัทได้ · แล้วผมยัง**เพิ่ม endpoint เงินอีกสองตัว**เข้าไป
  โดย checker รายงานเขียวทุกครั้ง เพราะมันไม่ได้มองไฟล์นั้นตั้งแต่แรก
  _(กติกา: allow-list ให้ความมั่นใจเท่ากับ "ลิสต์ครบไหม" ไม่ใช่ "ผ่านไหม" —_
  _เพิ่ม write endpoint ที่ไฟล์ไหน ให้เช็คว่าไฟล์นั้นอยู่ในลิสต์ของ checker ที่_
  _เกี่ยวข้องหรือยัง ถ้าไม่อยู่ = checker ตัวนั้นไม่มีอยู่จริงสำหรับไฟล์นี้)_
- **"1 request ที่ไม่ต้องล็อกอิน = 1 outbound call" คือ amplification ที่เราจ่ายเอง**
  หน้าจ่ายเงินของแขก poll สถานะทุก 2-10 วินาที นานได้ถึง 15 นาที · endpoint นั้น
  เรียก `RefreshAsync` ซึ่งถาม provider จริงทุกครั้ง ⇒ ใครถือ token ที่ถูกต้องใบเดียว
  เปิดแท็บทิ้งไว้หลายสิบแท็บก็ยิง provider แทนเราได้ฟรี (โควตาและค่าบริการเป็นของเรา)
  → หน่วงด้วย **ฟิลด์ที่มีอยู่แล้ว** (`LastPolledAt` ที่ settlement job ใช้เว้นจังหวะ)
  ไม่ใช่สร้าง state ข้าม request ตัวใหม่
  _(บทเรียนซ้อน: อย่าหน่วงด้วย `UpdatedAt` — มันขยับเฉพาะตอนข้อมูลเปลี่ยน ⇒ intent_
  _ที่ลูกค้ายังไม่จ่าย (สถานะคงเดิม) จะ "เก่าตลอด" แล้วยิง provider ทุกครั้งเหมือนเดิม_
  _ตัวหน่วงต้องผูกกับ "ถามครั้งล่าสุดเมื่อไร" ไม่ใช่ "ข้อมูลเปลี่ยนล่าสุดเมื่อไร")_
- **UI ที่โชว์ปุ่มซึ่งจะ 403 แน่ ๆ = silent no-op อีกทรง** — ผู้ใช้กดแล้วเจอ error
  ที่เขาแก้เองไม่ได้และไม่รู้ต้องขออะไรจากใคร · หน้าเว็บต้องรู้ล่วงหน้าจาก
  **endpoint ที่เซิร์ฟเวอร์ตอบ** (`metering/my-access`) ห้ามให้ JS เดาจาก role เอง
  (= สำเนากติกาสิทธิ์ชุดที่สองที่ drift แน่นอน) · และเมื่อซ่อนปุ่ม **ต้องบอกเหตุผล
  + ทางไปต่อ** ("ขอสิทธิ์ … จากเจ้าของบริษัท") ไม่ใช่ซ่อนเงียบ
  _(บทเรียนซ้อน: โหลดธงสิทธิ์ต้องเสร็จ**ก่อน** render — เดิมผมโหลดขนานกับข้อมูลอื่น_
  _ใน `Promise.all` ⇒ กล่องโควตาวาดปุ่มไปแล้วก่อนธงมาถึง = "ใครมาก่อนชนะ")_
- checker ใหม่ทุกตัวต้องผ่าน **negative test** ก่อนเชื่อ: ใส่บั๊กที่ตั้งใจจับ
  กลับเข้าไปแล้วยืนยันว่า checker จับได้จริง (เคยมี checker ที่ regex ผิด
  จนไม่จับเคสหลักของตัวเอง)
  _(รอบล่าสุดพิสูจน์อีกครั้ง: `tab_hidelist_check` รุ่นแรกใช้ `[^)]{0,200}?`_
  _ระหว่าง `.forEach(` กับ `classList.add('hidden')` ⇒ **จับไม่ได้เลย** เพราะ_
  _ข้อความจริงมี `)` คั่นอยู่ (`getElementById(id)?.classList…`) ⇒ อาร์เรย์ว่าง ⇒_
  _ข้ามไฟล์ ⇒ "ผ่าน" ทั้งที่มีบั๊ก. ถ้าไม่รัน negative test จะได้ checker ที่_
  _รายงานเขียวตลอดกาล = ด่านที่ไม่มีอยู่จริง)_
- **คำเตือนที่ฟ้องใบถูกกฎหมาย "ทุกใบ" = การปิดด่านโดยไม่ได้ตั้งใจ — และมันปิดด่าน
  ข้าง ๆ ไปด้วย** `OcrConfidenceGateway` ข้อ 3 หัก −0.10 ทุกบิลที่มีสินค้ายกเว้น §81
  ปน (Makro: VAT 49 บนยอด 1,000 = 4.9%) และข้อ 7 หัก −0.15 ทุกใบที่ราคาต่อหน่วย
  **รวม VAT แล้ว** (IKEA) ⇒ ใบที่ตัวเลขถูกทุกช่องได้ 0.70 ตกเกณฑ์ auto-create 0.85
  · ที่ร้ายกว่าคะแนนคือ ผู้ใช้เห็นคำเตือนสองบรรทัดนี้บนใบที่ถูกต้องทุกวันจนเรียนรู้
  ที่จะเมินทั้งกล่อง แล้ว**คำเตือนจริงถูกเมินตามไปด้วย** — กติกา "checker ที่ฟ้องผิด
  = checker ที่พังแล้ว" ใช้กับคำเตือนถึงผู้ใช้เท่ากับที่ใช้กับ `tools/*.py`
  → ทางแก้ **ไม่ใช่**ลบเงื่อนไขทิ้ง (นั่นคือปิดด่านจริง ๆ) แต่คือแยกเป็น **สองช่อง**:
  `Warnings` = สิ่งที่น่าจะผิด มี penalty เสมอ · `Notes` = ข้อสังเกตที่ไม่ใช่ความผิด
  ไม่มี penalty ติดป้ายคนละแบบ · และเทสต์ต้องล็อก **ทั้งสองทิศ** (ใบถูกกฎหมายอยู่ใน
  Notes · ใบที่อ่านผิดจริงยังอยู่ใน Warnings พร้อมคะแนนที่ลดลง) ไม่ใช่ยืนยันแค่ว่า
  "เงียบลง" ซึ่งผ่านได้ทั้งตอนแก้ถูกและตอนปิดด่านทิ้ง
  _(บทเรียนซ้อน — **ด่านที่ไม่สมมาตรต้องเขียนให้ไม่สมมาตร**: อัตรา VAT สูงกว่า 7%_
  _ผิดเสมอ (กฎหมายไทยไม่มีอัตรานั้น) แต่ต่ำกว่า 7% ชอบธรรมได้จากส่วนผสมยกเว้น/0% —_
  _กรอบ "6.5–7.5%" ที่เขียนสองทิศเท่ากันจึงผิดตั้งแต่รูปทรง · ตัวแยกสองเคสที่ต่ำ_
  _กว่าเกณฑ์คือ `mathConsistent` ซึ่ง**คอมเมนต์ของด่านข้าง ๆ อธิบายไว้เองแล้ว**_
  _แต่ด่านนี้ไม่ได้ใช้ — "คอมเมนต์ที่เขียนเจตนาถูกไว้ ไม่ได้แปลว่าโค้ดบรรทัดถัดไปทำตาม")_
- **ด่านตรวจกับเส้นทำงานที่อนุมานเรื่องเดียวกัน ต้องเรียกตัวจำแนกตัวเดียวกัน**
  ด่าน 7 เทียบ Σ บรรทัดกับ `SubTotal` ตรง ๆ ขณะที่ `CreateDocumentFromScanAsync`
  รู้จักเคส "ราคารวม VAT" และ "ส่วนลดที่กระดาษพิมพ์ไว้" อยู่แล้วผ่าน
  `Helpers/OcrLineReconciler` ⇒ สองที่ตัดสินเรื่องเดียวกันคนละสูตร = สำเนามือที่
  drift แน่นอน และทิศของ drift คือ "ด่านฟ้องสิ่งที่เส้นทำงานยอมรับ" ซึ่งดูเหมือน
  ระบบขัดแย้งกับตัวเองต่อหน้าผู้ใช้ (ญาติของ `MENU_SECTIONS` / `complianceIssues` /
  `docHeaderLabel`) → ให้ด่านเรียก helper ตัวเดียวกัน แล้วแปลผลลัพธ์เป็นคำเตือน
  เฉพาะเคสที่ helper เองบอกว่าตัดสินไม่ได้ (`LinesShort`/`Ambiguous`)
- **"ค่าที่รับเข้ามาแล้วแต่ rule ไม่ได้ใช้" อันตรายกว่าค่าที่ไม่มี — เพราะลายเซ็น
  บอกว่าคิดครบแล้ว** `ExpenseCategoryResolver.Resolve` รับ `industry` มาตั้งแต่ต้น
  และมีตารางถ่วงน้ำหนักรายอุตสาหกรรมยาวเหยียด — แต่ rule "ซื้อสินค้า/วัตถุดิบ"
  ชนะด้วย**ชื่อผู้ขายเป็นแบรนด์ค้าส่ง** (+4 คะแนน) โดยไม่แตะตารางนั้นเลย ⇒ บริษัท
  ซอฟต์แวร์/คลินิกที่ซื้อกาแฟ·กระดาษ A4 ที่ Makro ได้ผัง "ต้นทุนสินค้า" ทุกใบ
  ⇒ กำไรขั้นต้นในงบเพี้ยน · คู่แฝดของมันคือ `SuggestedEntryMode` ที่ตัดสิน
  Stock/Expense จากสัญญาณเดียว ("ผู้ขายเคยนำเข้าสต๊อกไหม") ทั้งที่ตัวจับคู่
  Product master คำนวณหลักฐาน cold-start ไว้ให้แล้วแต่ผลถูกทิ้ง
  → กติกา: เมื่อเห็นพารามิเตอร์ที่ "รับมาแล้ว" ให้ `grep` ว่ามัน**ถูกอ่านในทุกสาขา
  ของการตัดสิน**หรือแค่บางสาขา — สาขาที่ข้ามไปคือสาขาที่บั๊กอยู่ · และคำถามที่
  สองที่ถามเหมือนกัน ("กิจการเราถือสต๊อกไหม") ต้องมีตัวตอบตัวเดียว
  (`Helpers/InventoryIndustry`) ไม่ใช่เขียนเงื่อนไขซ้ำสองที่
  _(บทเรียนซ้อน 3 ข้อ — (1) **แยก "ไม่รู้" ออกจาก "รู้ว่าไม่ใช่" เสมอ**: กิจการที่_
  _ยังไม่ได้ตั้ง `IndustryType` ต้องไม่ถูกลงโทษเหมือนกิจการที่รู้ว่าไม่ถือสต๊อก —_
  _และเมื่อปฏิเสธเพราะ "ไม่รู้" ต้องบอกทางไปต่อ ("ตั้งค่าประเภทกิจการ") ไม่ใช่ตันเงียบ_
  _(2) **ถ่วงน้ำหนักดีกว่าตัดทิ้ง**: คอมเมนต์ของ rule เขียนเจตนาไว้ถูกว่ามันมีไว้กัน_
  _"บิลที่อ่านรายการไม่ได้แล้วแพ้ keyword หลง ๆ ท้ายบิล" — ลบ rule ทิ้งคือคืนบั๊กเดิม_
  _กลับมา · 0.25 ทำให้มันแพ้ keyword จริงแต่ยังชนะเมื่อไม่มีอะไรอื่น และ confidence_
  _ตกจนหน้า review ไฮไลต์เหลืองเอง_
  _(3) **ผลตรวจของทีมผิดได้เรื่องชื่อ enum**: รายงานเสนอให้ดู `BusinessType ∈_
  _{Trading, Manufacturing}` แต่ `BusinessType` ในเรพนี้คือ**รูปแบบนิติบุคคล**_
  _(บุคคลธรรมดา/นิติบุคคล/หจก.) แกนที่ถูกคือ `IndustryType` — แก้ตามรายงานตรง ๆ_
  _จะคอมไพล์ไม่ผ่าน ยืนยันด้วยการเปิด `Models/Enums/AllEnums.cs` เอง)_
- **ด่านที่ต้องให้ "ทุกช่องตรงเป๊ะ" ถึงจะฟ้อง = ด่านที่ข้อมูลผิดนิดเดียวก็ผ่าน**
  ด่านกันสร้างเอกสารซ้ำต้อง **เลขที่ AND ยอดตรงถึงสตางค์** ⇒ OCR อ่าน 1,070 เป็น
  1,010 (หลักเดียวเพี้ยน — ความผิดพลาดที่พบบ่อยที่สุดของ OCR ซึ่งเป็น**ต้นทาง
  ของข้อมูลที่ป้อนด่านนี้เอง**) = ใบซ้ำหลุด ⇒ เคลมภาษีซื้อสองครั้งจากใบกำกับใบเดียว
  → กติกา: เมื่อเงื่อนไขของด่านใช้ค่าที่มาจากช่องทางที่**อ่านผิดได้** ห้ามต่อด้วย
  `AND` แล้วเงียบเมื่อไม่ครบ — ให้แยกระดับความมั่นใจแล้ว**บอกส่วนต่าง**ให้คนตัดสิน
  (ที่นี่ §86/4 บังคับเลขใบกำกับ unique ต่อผู้ขาย ⇒ "เลขตรง + ผู้ขายตรง" พอแล้ว
  ยอดต่างแปลว่าอ่านผิด ไม่ใช่คนละใบ) · และ **"ไม่ทราบ" ต้องไม่ถูกอ่านเป็น "ไม่ตรง"**
  — ปล่อยของซ้ำหลุดเพราะข้อมูลไม่ครบคือทิศที่แพงกว่าเสมอ
  _(บทเรียนซ้อน 2 ข้อ — (1) **ขยายด่านโดยไม่เปิดทางไปต่อ = สร้างทางตัน**:_
  _`allowDuplicate` ถูกรับที่ controller มาตั้งแต่ต้นแต่ `grep` ทั้งเรพพบว่า_
  _**ไม่มีใครส่งเลย** ทั้งที่ข้อความเตือนเขียนเองว่า "กดยืนยันสร้างซ้ำในกล่องเตือน"_
  _⇒ ผู้ใช้ที่โดนด่านตันสนิทมาตลอด — ต้องต่อสายปุ่มยืนยันในคอมมิตเดียวกับที่ขยายด่าน_
  _(2) **ด่านที่ครอบแค่ทางเดียว คือด่านที่ไม่มี** (ซ้ำอีกรอบ): เส้นสร้างเอกสาร_
  _เรียกด่านกันซ้ำ แต่ปุ่ม "บันทึก JE เท่านั้น" ลัดผ่านทั้งดุ้น — หลังใส่ด่านที่ไหน_
  _ให้ `grep` ทุกทางเข้าที่แตะข้อมูลชุดเดียวกันเสมอ ไม่ใช่ทางที่ผู้ใช้รายงาน)_
- **`=` แทน `+=` บนช่องที่หลายฝ่ายเขียนร่วมกัน = ลบด่านของคนอื่นทิ้งเงียบ ๆ**
  ตอนตั้งธง `IsDuplicate` โค้ดเขียน `scanResult.ProcessingNotes = "Possible
  duplicate: …"` ด้วย `=` — ช่องนั้นเป็นที่สะสมของ **ทุกขั้นในไปป์ไลน์**
  (`[Field Confidence]` · `[Buyer]` · `[Reasoning]` · `[Deposit-Buy]` และที่สำคัญ
  ที่สุด **`[VAT-CLAIM]`** ซึ่งเป็นธง §82/5 ที่ด่านบันทึก JE อ่านด้วย
  `Contains("[VAT-CLAIM]")`) ⇒ ใบภาษีซื้อต้องห้ามที่บังเอิญถูกตีว่าซ้ำ
  **ผ่านด่าน §82/5 ไปได้** โดยไม่มีอะไรบอก
  → กติกา: ช่องข้อความที่มีผู้เขียนมากกว่าหนึ่งจุด ต้องต่อท้ายเสมอ · และเมื่อ
  ช่องนั้นถูกใช้เป็น**ที่เก็บธงของด่าน** (ไม่ใช่แค่ข้อความให้คนอ่าน) ให้ `grep`
  หา `Contains("[` บนช่องนั้นก่อนแตะมันทุกครั้ง — ธงที่หายไม่มี error ให้เห็น
  _(บทเรียนซ้อน: การเก็บธงของด่านไว้ในช่องข้อความอิสระเป็นการออกแบบที่เปราะ_
  _ตั้งแต่ต้น — แต่การย้ายไปเป็นคอลัมน์ของตัวเองต้องแตะทุกจุดที่อ่าน/เขียน จึงจด_
  _ไว้เป็นงานแยก ไม่ใช่แอบเปลี่ยนกลางคอมมิตที่แก้เรื่องอื่น)_
  _(→ เพิ่ม `tools/flag_field_overwrite_check.py`. **กติกาต้องแคบมากถึงจะไม่ฟ้องผิด**:_
  _"ฟิลด์ชื่อ Notes ถูกเขียนทับ" เฉย ๆ ฟ้องผิด 6 จุดที่ถูกต้องทั้งหมด_
  _(`StockCountLine.Notes` · `PdpaProcessingActivity.Notes` · `TaxReport.Notes` เป็น_
  _ช่องที่ผู้ใช้กรอกเอง) และการแยกว่า `doc.Notes` กับ `line.Notes` คนละตารางกันต้อง_
  _resolve type จริง = **กำแพงเดิมที่ทำให้ checker สองตัวก่อนหน้าถูกทิ้ง** จึงเลี่ยง_
  _ด้วยการใช้ **คุณสมบัติของตัวฟิลด์เอง** แทนชนิด: เฝ้าเฉพาะฟิลด์ที่ (1) ถูกต่อท้าย_
  _ด้วยสำนวน `(x ?? "") +` ≥ 3 ครั้ง **และ** (2) ถูกอ่านเป็นธงด้วย `.Contains("[`_
  _— ทั้งเรพมีฟิลด์เดียวที่ผ่านสองข้อนี้ และเป็นตัวที่มีบั๊กจริงพอดี · ฟิลด์ใหม่ที่_
  _ถูกใช้แบบเดียวกันจะถูกเฝ้าเองอัตโนมัติ · negative test: ใส่บรรทัดเดิมกลับ → ฟ้อง 1 จุด)_
- **`DateTime.TryParse` เปล่า ๆ = พฤติกรรมขึ้นกับ culture ของเครื่องที่ deploy —
  และสองทิศของมันแก้พร้อมกันไม่ได้ด้วย culture เดียว** เส้น OCR มี 4 จุด และ
  `Program.cs` ไม่เคยตั้ง `DefaultThreadCurrentCulture` ⇒ บน `th-TH` ปฏิทินเริ่มต้น
  เป็น**พุทธ** ⇒ ISO `"2026-09-05"` = ค.ศ. 1483 → ถูกด่านล้างทิ้ง ⇒ **ทุกใบไม่มี
  วันที่** · บน `en-US` อ่าน `05/08/2569` เป็น MM/dd ⇒ 8 พ.ค. แทน 5 ส.ค. ซึ่ง
  **เงียบสนิท** เพราะวันที่ยังดูสมเหตุสมผล แล้วเอกสารลงผิดงวด ภ.พ.30
  → กติกา: อย่าแก้ด้วยการ "บังคับ InvariantCulture" (ทิศที่สองจะพังแทน) — ต้อง
  **แยกรูปแบบก่อนแล้วเลือกวิธีอ่านให้ตรงชนิด**: ISO (ขึ้นต้น `yyyy-MM-dd`) อ่าน
  ผ่าน `DateTimeOffset` แล้วเอา**เวลาตามที่เอกสารเขียน** (ห้ามแปลงเป็นโซนเครื่อง —
  `T00:30+07:00` บนเครื่องโซนลบจะถอยไปวันก่อนหน้า) · รูปแบบบนกระดาษ **แยกตัวเลข
  เองด้วย regex ไม่ผ่าน culture ใด ๆ** · ปีผ่าน `ThaiDate.NormalizeYear` ที่เดียว
  _(บทเรียนซ้อน 2 ข้อ — (1) **`TryParseExact` กับรูปแบบ `"yy"` ใช้แทนไม่ได้**:_
  _.NET เติมศตวรรษตาม `TwoDigitYearMax` ของปฏิทิน**ก่อน** ⇒ "69" กลายเป็น 1969_
  _แล้ว `NormalizeYear` มองไม่เห็นว่าเป็น พ.ศ. ย่อ (2026) — ต้องจับเลข 2 หลักดิบเอง_
  _(2) **เทสต์ต้องตั้ง culture จริงทั้งสองแบบ** ไม่ใช่เทสต์บนเครื่องเดียว: เทสต์ที่_
  _ผ่านบน en-US อย่างเดียวไม่ได้พิสูจน์อะไรเลยเกี่ยวกับบั๊กนี้)_
- **tier ที่ข้อมูลอ่อนที่สุด มักเป็น tier ที่เดามากที่สุด — ไล่ตรวจมันก่อนเสมอ**
  เส้น Tesseract (tier สุดท้าย) แต่งตัวเลขภาษีขึ้นเอง **3 ทางในเมธอดเดียว**:
  แยก VAT 7/107 จากยอดรวมเพราะมีคำว่า "ใบกำกับภาษี" ที่ไหนก็ได้บนหน้า (ใบเสร็จ
  ร้านที่พิมพ์ว่า "ขอใบกำกับภาษีได้ที่เคาน์เตอร์" ก็เข้า) · `WHT|W/?T` ที่ไม่มี
  word-boundary ติ๊ก "มีภาษีหัก ณ ที่จ่าย" ให้ใบภาษาอังกฤษทั่วไป · อัตราหักมาจาก
  "เลข%" ตัวไหนก็ได้ในหน้าต่าง 70 ตัวอักษร (ส่วนลด 3% กลายเป็นอัตราหัก) — ขณะที่
  เส้น Azure ไม่ทำสักอย่าง = **สองมาตรฐานระหว่าง tier ในระบบเดียวกัน**
  → กติกา: เมื่อเห็นโค้ดเติมค่าให้เอง ให้ถามว่า "tier อื่นทำแบบนี้ไหม" — ถ้าไม่
  แปลว่าเส้นนี้กำลังชดเชยความอ่อนของตัวเองด้วยการเดา · และค่าที่ **คำนวณขึ้น**
  ต้อง stamp `FieldConfidence` ต่ำกว่าเกณฑ์ไฮไลต์เสมอ ไม่ใช่ปล่อยให้ดูเหมือนค่าที่
  อ่านมาจากกระดาษ (กฎเหล็ก #3 ข้อ 3) — ป้ายที่ไม่ขึ้นเพราะ**ไม่มี key** เป็นการ
  ปิดด่านที่มองไม่เห็นจากโค้ดฝั่ง UI เลย
  _(บทเรียนซ้อน: **"ปิดการเดาทิ้งไปเลย" ไม่ใช่คำตอบเสมอ** — ใบกำกับไทยจำนวนมาก_
  _พิมพ์แต่ยอดรวม ปิดทิ้ง = ผู้ใช้กรอกเองทุกใบ (ขัดกฎเหล็ก #3) · ทางที่ถูกคือ_
  _แยกเป็น "มีสัญญาณค้าน → ไม่เดา" กับ "ไม่มีสัญญาณค้าน → เดาแต่ติดป้าย" แล้ว_
  _ยกระดับความมั่นใจเมื่อกระดาษยืนยันเอง — สามระดับ ไม่ใช่เปิด/ปิด)_
- **เส้น DSR ต้องไล่ "ทุกตารางที่เก็บข้อความจากกระดาษ" ไม่ใช่แค่ตารางที่ชื่อ
  เหมือนข้อมูลส่วนบุคคล** `PdpaService` ทำ ม.30/ม.33 ครบสำหรับ `Users` +
  `Contacts` — แต่ไม่เคยแตะ `OcrScanResult` ซึ่งเก็บ `RawTextContent` =
  **ข้อความทั้งหน้ากระดาษ** (ชื่อ · ที่อยู่ · เลขประจำตัวผู้เสียภาษี) ⇒ ผู้ที่
  ใช้สิทธิขอลบยังค้นเจอตัวเองได้เต็ม ๆ และคำตอบ ม.30 ไม่ครบตามที่กฎหมายบังคับ
  → กติกา: ตารางที่เก็บ **ผลอ่านจากเอกสาร** (OCR · อีเมลขาเข้า · ไฟล์แนบที่ถูก
  แปลงเป็นข้อความ) เป็นข้อมูลส่วนบุคคลเสมอ แม้ชื่อคอลัมน์จะดูเป็นเรื่องบัญชี ·
  และรายการ "ช่องไหนเป็น PII" ต้องเขียนเป็น **"ทุกช่องต้องถูกตัดสิน"** พร้อม
  เทสต์ reflection — deny-list เฉย ๆ จะปล่อยช่องที่เพิ่มทีหลังหลุดเงียบ
  (บทเรียนเดียวกับ `OcrScanSnapshot`)
  _(บทเรียนซ้อน 2 ข้อ — (1) **ขา "ขอสำเนา" ต้องไม่ยกข้อความดิบมาให้ผู้ขอ**:_
  _กระดาษใบเดียวกันมีข้อมูลของคู่ค้าอีกฝ่ายอยู่ด้วย ⇒ ส่งทั้งหน้า = เปิดเผย_
  _ข้อมูลบุคคลที่สามไปพร้อมกัน — รายงานเป็นสรุปรายแถวแล้วให้ขอไฟล์ต้นฉบับ_
  _ผ่านผู้ควบคุมข้อมูลแทน (2) **ขา "ขอให้ลบ" ต้องใช้ legal hold ชุดเดียวกับ_
  _ตารางที่เกี่ยวข้อง** ไม่ใช่คิดเกณฑ์ใหม่ — ที่นี่คือ 5 ปีของ พ.ร.บ.การบัญชี ม.10_
  _ซึ่ง `Contacts` ใช้อยู่แล้ว (กฎ M: `MAX(retention)` ชนะสิทธิขอลบ))_
- **"โหมดออฟไลน์" ที่ประทับสถานะปลายทางให้เอง = โกหกที่ล็อกเอกสารถาวร**
  `SubmitToRevenueAsync` เมื่อยังไม่ได้ตั้งค่า RD API ตั้ง `Status = Submitted` +
  `SubmissionId = "OFFLINE-{guid}"` พร้อมคอมเมนต์ว่า "offline mode" — แต่ **ไม่เคย
  ยิงไปที่ไหนเลย** ⇒ (ก) ผู้ใช้เห็นว่า "นำส่งกรมสรรพากรแล้ว" ทั้งที่ไม่เคยส่ง
  (ข) ส่งใหม่ไม่ได้เพราะด่านกันส่งซ้ำเห็นว่าส่งแล้ว (ค) **void ไม่ได้** เพราะ
  `DocumentService` block การ void เอกสารที่มี e-Tax สถานะ `Submitted` ⇒ ใบที่ผิด
  ค้างในระบบตลอดไป · อาการเงียบสนิทเพราะทุกอย่าง "สำเร็จ" ทุกจอ
  → กติกา: **สถานะที่แปลว่า "ระบบภายนอกรับไปแล้ว" ต้องตั้งได้เฉพาะเมื่อระบบภายนอก
  ตอบกลับจริงเท่านั้น** — ไม่มีการตั้งค่า = ยังไม่ถึงขั้นนั้น ให้คงสถานะเดิมแล้ว
  บอกทางไปต่อ (ญาติของ "ค่า default ที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้") · และเมื่อ
  ขั้นก่อนหน้า (ลงนาม) สำเร็จจริง ห้ามให้ขั้นหลังที่ล้มลากขั้นก่อนล้มตาม — จับ
  exception เฉพาะชนิดแล้วคืนผลของขั้นที่ทำได้ · **แก้โค้ดอย่างเดียวไม่พอ** แถวเก่าที่
  ประทับไว้แล้วยังโกหกต่อไป ต้องมี migration ล้าง (ซ้ำรอย `OcrLearnedPatterns` /
  `VendorKnownGoodValues` / `AiCallStatus`)
- แจ้งผู้ใช้เสมอว่า "ยังไม่ได้คอมไพล์ — รบกวน rebuild ฝั่งคุณ"

### G. Testing mandate (ช่องโหว่ใหญ่สุดของระบบ)

- Logic เงิน/ภาษีใหม่ → extract เป็น pure class (แบบ `TaxPointResolver`,
  `Section65TerValidator`) + เทสต์ในคอมมิตเดียวกัน
- แก้บั๊กที่ผู้ใช้รายงาน → reproduce เป็นเทสต์/simulation ให้เห็นตัวเลขตรง
  กับที่รายงานก่อน แล้วค่อยแก้ (ยืนยันว่าแก้ถูกตัว)
- **Control เชิง compliance (hash chain, retention, encryption) ต้องมี
  round-trip test — control ที่ไม่มีเทสต์ยืนยัน = ไม่มี control**
  _(ที่มา: hash chain พังเงียบ ๆ เพราะไม่มีเทสต์เดียวที่ write→verify)_

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
  (ข้อเท็จจริงสำคัญ: `Product.CurrentStock` กับ `WarehouseStock` เป็น**สองความจริงที่ไม่คุยกัน**
  — ห้ามเพิ่มฟีเจอร์สาขาก่อนยุบผ่าน `IStockLedger` ตัวเดียว · POS ยังไม่ผูก `Branch`/`Warehouse`
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

- **อย่าเรียก `dotnet build/test`** — env ไม่มี SDK เช็ค brace balance
  เองด้วย `awk` แล้วบอกผู้ใช้ rebuild ฝั่งเขา
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

### บทเรียนใหม่จากรอบนี้ (เพิ่มเข้าคลัง defect class)

- **"ค่าที่ลงตัวทางคณิตศาสตร์" ไม่ได้แปลว่า "ค่าที่ถูก" — ต้องถามว่ามันคือเลขอะไรบน
  กระดาษ** `AmountTripleExtractor` ค้นสามค่าที่ `sub + vat = total` และ `vat/sub ≈ 7%`
  แล้ว**ทับ**ค่าที่ engine อ่านถูกอยู่แล้ว: บิล Makro (1,000 / 49 / 1,049 — ผสม 7%/
  ยกเว้น จึงไม่ใช่ 7% พอดี) ถูกแทนด้วย (951 / 49 / 1,000) ซึ่งลงตัวเป๊ะเพราะ 1,000 =
  **เงินสดที่ลูกค้าจ่าย** และ 951 = **เงินทอน**; ใบส่งออก 0% (50,000/0/50,000) ถูกแต่ง
  VAT 3,271 ที่ไม่มีบนกระดาษ — ทั้งสองกรณีตั้ง confidence 0.95 ให้ค่าที่แต่งขึ้น
  → กติกา: ตัวสกัดที่ "ยืนยันตัวเองด้วยคณิต" ต้องถูกผูกกับ **ค่าที่อ่านมาได้จริง**
  อย่างน้อยหนึ่งจุด (ที่นี่: ยอดรวมต้องตรงกับที่ engine อ่าน) ก่อนได้สิทธิ์ทับ ·
  และ "สอดคล้อง" ต้องนับใบ 0%/ยกเว้น (VAT=0, sub=total) ด้วย ไม่งั้นทุกใบที่ไม่ใช่ 7%
  จะถูกลากเข้าสูตร 7/107 ตลอดไป
- **นักเรียนตอบแล้วถูกทิ้ง = "เรียนแล้วโง่ลง" (ตรงข้ามกับเจตนาของกฎเหล็ก #1)**
  orchestrator short-circuit คืน `UsedAi=false` พร้อมคำตอบของ local model (ถูกต้องตาม
  สัญญา) แต่ call site บนเส้น OCR **ทั้ง 5 จุด** เขียนด่านแรกว่า `if (result.UsedAi && …)`
  ⇒ คำตอบนักเรียนถูกทิ้งทุกครั้ง ⇒ ยิ่ง local แม่นขึ้น (short-circuit บ่อยขึ้น) ผู้ใช้
  ยิ่งได้ heuristic เปล่า ๆ แทนคำตอบที่เคยได้จาก AI — และตัวชี้วัด `UsedAi` ที่ลดลง
  (กฎเหล็ก #1 ข้อ 6) **อ่านไม่ได้เลย** เพราะมันลดลงพร้อมคุณภาพที่แย่ลง
  → กติกา: `UsedAi` ตอบคำถาม **"จ่ายเงินให้ provider ไหม"** เท่านั้น — ห้ามใช้เป็นด่าน
  ว่า "มีคำตอบให้ใช้ไหม" ใช้ `HasModelAnswer` (ครูหรือนักเรียนก็ได้) แล้วให้
  anti-hallucination guard เดิมเป็นตัวคัด · ป้าย UI แยก "🤖 AI" กับ "⚙️ ระบบเรียนรู้แล้ว"
  · และ **วัด `UsedAi` คู่กับ first-pass accept rate เสมอ** ไม่งั้น "ประหยัดโดยโง่ลง"
  จะดูเหมือนความสำเร็จ
- **ตัวเลขที่ "เทียบผิดฝั่งของ VAT" ทำให้ระบบแต่งบรรทัดที่ไม่มีบนกระดาษ**
  ตรรกะกระทบยอดเดิมเทียบ **Σ บรรทัด (ก่อน VAT)** กับ **ยอดรวม (หลัง VAT)** ⇒ ใบที่มี
  ส่วนลดท้ายบิลตกเข้าเงื่อนไข "ราคารวม VAT" พอดี แล้วส่วนต่างถูกสร้างเป็นบรรทัด
  "ค่าขนส่ง/บริการอื่น (ตรวจสอบใบจริง)" ที่ไม่มีอยู่จริง (ค่าใช้จ่ายผี → JE → ภาษีซื้อ)
  · อีกทิศหนึ่งส่วนลดถูกคิดเทียบยอดรวมแล้วบวก VAT ซ้ำ ⇒ เอกสาร ≠ กระดาษ โดยยอด
  "ดูสมเหตุสมผล" ทั้งสองใบ จึงเงียบสนิท
  → กติกา: การกระทบยอดต้องเทียบ **ฝั่งเดียวกันของ VAT เสมอ** · ส่วนลดต้องมี**หลักฐาน
  บนกระดาษ** (ช่องส่วนลด) ไม่ใช่อนุมานจากส่วนต่าง · ตัดสินไม่ได้ = รายงานช่องว่างให้คน
  ตัดสิน (`[Σ-GAP]`) — **"ไม่รู้ = บอกว่าไม่รู้" ใช้กับตัวเลขเงินเข้มกว่าที่อื่น**
- **"ธงที่บอกว่าใครตอบ" ต้องมาจากคนที่รู้ ไม่ใช่จากการเดารูปแบบสตริงที่ปลายทาง**
  ตอนเปิดทางให้ "นักเรียน" (local model) ตอบแทน AI ได้ ผมเขียนตัวตรวจที่ปลายทางว่า
  `Status == Skipped && ProviderModel เริ่มด้วย "local:"` — ซึ่งเป็น**ลายเซ็นของเส้น
  short-circuit เส้นเดียว**. orchestrator มีอีก **9 จุด**ที่คืนคำตอบนักเรียนผ่าน
  `FallbackToLocal` (ปิด provider ทุกตัว `NoProvider` · เกินงบ `BudgetExceeded` ·
  provider ล่ม `Failed` · ตอบไม่เข้า schema `InvalidResponse` · admin ปิด feature)
  ซึ่งตั้ง `ProviderModel = null` ⇒ คำตอบนักเรียนถูกทิ้ง **ในเคสที่กฎเหล็ก #1 ข้อ 5
  (kill-switch) เขียนขึ้นมาเพื่อรองรับพอดี** — ปิด AI แล้วผู้ใช้ได้ heuristic เปล่า ๆ
  แทนสิ่งที่ระบบเรียนไว้ ทั้งที่คำตอบนั้น**ติดมากับ response อยู่แล้ว**
  → กติกา: ค่าที่เป็น "ใครเป็นคนตอบ / เกิดอะไรขึ้นจริง" ต้องเป็น **ช่องที่ผู้ผลิตค่าเป็น
  คนตั้ง** (`AiResponse.FromLocalModel`) ไม่ใช่ให้ผู้บริโภคเดาจากผลข้างเคียง —
  ญาติของบทเรียน `AiCallStatus.LocalServed` (แถวที่บันทึกเพื่อหลายวัตถุประสงค์ต้องมีช่อง
  บอก "เกิดอะไรขึ้นจริง" แยกจาก "ผลลัพธ์ที่ผู้ใช้เห็น") · และเมื่อเพิ่มธงแบบนี้ ให้ไล่
  **ทุก return path ของฟังก์ชันที่ผลิตค่านั้น** ไม่ใช่เฉพาะเส้นที่กำลังแก้
  _(เทสต์ที่จับได้คือเทสต์ที่ล็อก **ทุกสถานะ** ไม่ใช่สถานะที่ตั้งใจแก้ —_
  _`StudentAnswerSourceTests` มี 6 InlineData ซึ่ง 5 ตัวเคยตกทั้งหมด)_
- **ตัวแปลงที่ "คืน null เมื่อแปลงไม่ได้" ข้างหน้าเส้นที่ "ทำงานเสมอ" = ด่านที่ไม่มีอยู่จริง**
  ด่านสิทธิ์ "สร้างเอกสารจากสแกน" แปลง `TargetDocumentType` เป็น enum เอง แล้ว
  `return Enum.TryParse(...) ? dt : null` — ผู้เรียกเขียนว่า `if (target is DocumentType t
  && !มีสิทธิ์) return 403;` ⇒ **null = ผ่านด่าน**. แต่ฝั่ง service ไม่เคยคืน null:
  มันมี fallback ตามชนิดกระดาษ (`Invoice`→PurchaseInvoice · `Receipt`→PaymentVoucher ·
  อื่น ๆ →Expense) และค่า pseudo `"Deposit"` (แปลงไม่ได้) กลายเป็น **Receipt = ฝั่งขาย**
  ซึ่งเป็นคีย์สิทธิ์คนละตัว ⇒ ผู้ใช้ที่ไม่มีสิทธิ์ฝั่งขายสร้างใบฝั่งขายได้ และเคสที่พบบ่อย
  ที่สุด (สแกนใหม่ที่ยังไม่มี `TargetDocumentType`) ไม่เคยผ่านด่านเลยสักครั้ง
  → กติกา: เมื่อด่านกับเส้นทำงานต้องตัดสินค่าเดียวกัน ให้ยุบเป็น **ฟังก์ชัน pure ตัวเดียว**
  ที่ทั้งสองเรียก (`Helpers/OcrTargetDocumentType`) และถ้าเส้นทำงาน "ตอบเสมอ" ฟังก์ชันนั้น
  **ต้องไม่มีทางคืน "ไม่รู้"** — ไม่งั้น "ไม่รู้" จะถูกอ่านเป็น "ไม่ต้องตรวจ" เสมอ
- **แก้ฝั่งเดียวของคู่ที่สมมาตร = เหลืออีกฝั่งไว้ และมักเป็นฝั่งที่พบบ่อยกว่า**
  รอบก่อนแก้ "ฝั่งขายไม่มีบรรทัดภาษีถูกหัก ณ ที่จ่าย" (11910) แล้วจบ — ฝั่ง**ซื้อ**
  ซึ่งเป็นเคสที่เกิดทุกวัน (เราเป็นผู้หัก) ยังปล่อยให้บรรทัดหายเงียบเมื่อผังไม่มี
  21916/21917 ⇒ เครดิตเต็มจำนวน = "จ่ายผู้ขายครบ" ทั้งที่ผู้ใช้ติ๊กว่าหัก ⇒ ไม่มีหนี้สิน
  ให้นำส่ง ภ.ง.ด.3/53 + เจ้าหนี้เกินจริง + 50 ทวิ ที่ออกไปแล้วไม่มีคู่ในบัญชี
  → หลังแก้สาขา `if (isSeller) … else …` ให้ **อ่านอีกสาขาทันทีในรอบเดียวกัน**
  แล้วถามว่า "อาการเดียวกันเกิดที่นี่ได้ไหม" — โค้ดที่แตกเป็นสองฝั่งคือที่ที่ defect class
  "แก้ตัวเดียว เหลือที่เหลือ" ซ่อนตัวได้ดีที่สุด เพราะสองฝั่งอยู่ห่างกันไม่กี่บรรทัดจนดู
  เหมือนอ่านครบแล้ว
- **ตัวชี้วัดที่นับจาก "แถวถูกอัปเดต" วัดพฤติกรรมของโค้ดเรา ไม่ใช่ของผู้ใช้**
  ตัวชี้วัดความแม่นยำ OCR นับ "ใบที่ผู้ใช้แก้" จาก `OcrScanResult.UpdatedAt != null`
  — แต่ค่านั้นขยับทุกครั้งที่ **ระบบเอง** บันทึกแถว (จบการสแกน · ผูกเอกสารที่สร้าง ·
  sync ตอนอนุมัติ) ⇒ อัตราการแก้ ≈ 100% ทุก tenant ตลอดกาล = ตัวเลขที่ **อ่านไม่ได้**
  แต่ดูเหมือนมีตัวชี้วัดแล้ว (อันตรายกว่าไม่มี เพราะไม่มีใครไปหาตัวใหม่)
  → กติกา: ตัวชี้วัดที่ตั้งใจวัด **การกระทำของคน** ต้องมีช่องที่ถูกตั้งจาก
  **จุดที่คนทำจริง** เท่านั้น (`UserCorrectedAt`) · และเมื่อเพิ่มช่องแบบนี้ ให้ไล่
  **ทุกทางที่คนแก้ค่าได้** ไม่ใช่ทางเดียว (ที่นี่: หน้า review **และ** การแก้ Draft
  ก่อนอนุมัติ — ไม่งั้นตัวเลข "ผ่านรอบแรก" จะสวยเกินจริง) · เก็บ**ชื่อช่อง**ที่ถูกแก้
  ด้วย เพราะ "แก้ตรงไหนบ่อย" บอกว่าควรไปปรับปรุงอะไรก่อน ส่วน "ถูกแก้กี่ใบ" บอกแค่ว่ามีปัญหา
- **ตัวเลขที่ลดลงได้จากสองสาเหตุตรงข้ามกัน ห้ามอ่านตัวเดียว — ต้องมีคู่เสมอ**
  กฎเหล็ก #1 ข้อ 6 ให้ใช้ `<Feature>UsedAi` ที่ลดลงเป็นตัวชี้วัดความสำเร็จ แต่มันลดลง
  ได้ทั้งจาก "นักเรียนเก่งขึ้นจนไม่ต้องถามครู" (สำเร็จ) และ "โค้ดหยุดใช้คำตอบของโมเดล"
  (ถอยหลัง — ผู้ใช้ได้ heuristic เปล่า ๆ แล้วต้องแก้เองทุกใบ) ทั้งสองกรณีกราฟหน้าตา
  **เหมือนกันเป๊ะ** และเคสหลังเกิดขึ้นจริงมาแล้วในเรพนี้ (ด่าน `if (UsedAi && …)`
  ทิ้งคำตอบนักเรียน) โดยกราฟต้นทุนดู "ดีขึ้น" ตลอด
  → กติกา: จับคู่กับตัวชี้วัด**คุณภาพ**ที่ขยับสวนทางเมื่อของพัง (`FirstPassAcceptRate`
  = สแกนที่กลายเป็นเอกสารโดยผู้ใช้ไม่ต้องแก้อะไรเลย) แล้วให้ระบบ**ตัดสินให้เอง**ว่า
  คู่นี้แปลว่าอะไร (`Helpers/OcrQualityKpi`) — ห้ามวางตัวเลขดิบสองตัวไว้เฉย ๆ ให้คนอ่าน
  ตีความ · ฐานของตัวชี้วัดคุณภาพต้องเป็น **สแกนทั้งหมด** ไม่ใช่ "เฉพาะใบที่กลายเป็น
  เอกสาร" ไม่งั้นใบที่เติมมาผิดจนผู้ใช้ทิ้งไปเลยจะหายออกจากตัวหาร = ตัวเลขสวยขึ้นเมื่อ
  คุณภาพแย่ลง · และต้องมี **จำนวนตัวอย่างขั้นต่ำ** ก่อนตัดสิน ไม่งั้น tenant ที่เพิ่ง
  สแกน 3 ใบจะถูกติดป้าย "ถอยหลัง" ทุกวัน จนคนเลิกดูตัวชี้วัดไปเลย
- **ตารางลำดับความสำคัญที่ประกาศไว้ ต้องเป็นตัวที่โค้ด "ใช้จริง" ไม่ใช่แค่เอกสารข้างโค้ด**
  ตอนเขียน `OcrFieldArbiter` ผมประกาศ `Precedence[]` ไว้เป็นตารางความน่าเชื่อ แล้วเรียง
  ด้วย `OrderByDescending((int)source)` เพราะบังเอิญกำหนดค่า enum ให้เรียงตรงกันอยู่แล้ว
  — ได้ผลถูกวันนี้ แต่วันที่มีคนเพิ่มแหล่งใหม่ด้วยเลขที่ไม่ตรงลำดับ ตารางจะกลายเป็น
  **"ของที่ประกาศไว้แต่ไม่มีใครใช้"** ทันที และไม่มีอะไรฟ้อง (ญาติของ
  `GlobalVendorIntelLearner.CanShareMoneyAggregates` ที่มีด่านครบแต่ไม่มี call site)
  → กติกา: ค่าที่ประกาศเป็น "ตารางกติกา" ต้องถูก**อ่าน**โดยตัวตัดสินเสมอ (`Rank()`
  ไล่หาใน `Precedence`) และมีเทสต์ที่ยืนยันว่า **ลำดับในตาราง = ลำดับที่ตัดสินจริง**
  ไม่ใช่แค่เทสต์ว่า "เคสนี้ได้คำตอบนี้"
- **"state ต่อ process" กับ "ข้อมูลที่ใช้ร่วมกัน" ห้ามอยู่ใต้ล็อกเดียวกัน**
  งานเทรน AI ห่อทั้งก้อนด้วย `JobLock` แบบ try (ถูกต้องสำหรับส่วนที่**เขียนข้อมูล**)
  — แต่ข้างในนั้นมีขั้น "โหลดนักเรียนเข้าหน่วยความจำ" ซึ่ง `ILocalDistillationModel`
  เป็น **singleton ต่อ process** ⇒ instance ที่แพ้ล็อก **ไม่เคยมีนักเรียนเลยตลอดอายุ
  process** (try-lock ไม่รอ และเครื่องเดิมมักชนะซ้ำ ๆ) ⇒ คำขอที่ load balancer ส่งไป
  เครื่องนั้นได้ heuristic เปล่า ๆ หรือถูกส่งไปเรียก provider ทุกครั้ง = กฎเหล็ก #1
  ใช้ไม่ได้จริงบน N−1 เครื่อง · อาการมองไม่เห็นจาก log เพราะแต่ละเครื่อง "ทำงานถูกต้อง"
  ในสายตาตัวเอง และเครื่องที่ชนะก็รายงานว่าโหลดสำเร็จทุกรอบ
  → กติกา: ก่อนห่อโค้ดด้วยล็อกกันรันซ้ำ ให้แยกให้ออกว่าขั้นไหน **เขียนของกลาง**
  (ต้องกัน) กับขั้นไหนเป็น **การเตรียม state ของเครื่องนี้เอง** (ต้องทำทุกเครื่อง) ·
  และ state แบบหลังต้องถูกเตรียม **ตอน boot** ด้วย ไม่ใช่รอรอบงานตามตารางรอบแรก
  (ที่นี่หน่วง 7 นาที = หลัง deploy ทุกครั้งระบบไม่มีนักเรียนอยู่ 7 นาที)
- **ขอบเขตของ "กองข้อความที่เอาไปค้น" สำคัญพอ ๆ กับตัวคำที่ค้น**
  ด่านภาษีซื้อต้องห้าม §82/5 เอาคำที่บ่งชี้ **ผู้ขาย** (ชื่อปั๊มน้ำมัน: "ปตท" ·
  "shell" · "pure") ไปค้นกับ **ข้อความทั้งหน้า** ⇒ โฆษณาท้ายใบ · ชื่อถนน ("ติดปั๊ม
  ปตท.") · แม้แต่ **ชื่อสินค้า** ("PURE LIFE" น้ำดื่ม) ทำให้ทั้งใบถูก**ปิดเคลม
  ภาษีซื้อ** ซึ่งเป็นการเสียสิทธิ์จริง ไม่ใช่แค่คำเตือนที่กดข้ามได้
  → กติกา: คำที่ตอบคำถาม "**ใคร**ขาย" ต้องค้นในช่องชื่อผู้ขาย · คำที่ตอบ "ซื้อ**อะไร**"
  ค้นได้ทั้งหน้า — เขียนไว้ในชื่อตัวแปรให้ชัด (`vendorHay` vs `hay`) ไม่งั้นรอบหน้า
  จะมีคนเติมคำใหม่ลงลิสต์ผิดใบ
- **`Contains` กับคำละตินสั้น ๆ = ระเบิดเวลา (ภาษาไทยไม่มีปัญหานี้ จึงมองข้ามง่าย)**
  ลิสต์เดียวกันมีทั้งคำไทยยาว ๆ ("ค่าน้ำมัน") และรหัสละตินสั้น ("b7"/"e20") — ผู้เขียน
  ใช้ `Contains` ตัวเดียวกับทั้งลิสต์เพราะภาษาไทยเขียนติดกันจึงไม่มี "ขอบคำ" ให้ใช้
  ⇒ `"SIZE20"` มี `"e20"` · `"purity"` มี `"pure"` · `"advance"` มี `"van"`
  → กติกา: ในลิสต์ผสม ให้แยกวิธีเทียบตาม **ชนิดของคำ** (ละติน = ขอบคำ · ไทย = substring)
  ที่ตัวฟังก์ชันเทียบเอง ไม่ใช่ให้คนเขียนลิสต์จำว่าคำไหนปลอดภัย
- **ด่านที่ถูก "ป้อนผลของสูตรที่ตัวเองกำลังจะตรวจ" = ด่านที่ผ่านทุกครั้งตลอดกาล**
  `OcrConfidenceGateway` มีข้อ 8 ตรวจว่า "ยอดหัก ณ ที่จ่าย ≈ SubTotal × Rate/100"
  — แต่ผู้เรียก **คำนวณ** `whtAmt = SubTotal × Rate/100` ขึ้นมาเองแล้วส่งเข้าไปเป็น
  อินพุตของด่านนั้น (พร้อมคอมเมนต์ว่า "for sanity validation") ⇒ ด่านเทียบสูตรกับ
  ผลของสูตรตัวเอง ⇒ ไม่มีทางไม่ผ่าน · อ่านโค้ดผ่าน ๆ จะเห็นเป็น "มีด่านคณิตศาสตร์แล้ว"
  ทั้งที่ยอดที่ควรตรวจ (ยอดที่ **พิมพ์บนกระดาษ**) ไม่เคยถูกอ่านมาเลย
  → กติกา: ก่อนเชื่อว่าด่านทำงาน ให้ถามว่า **อินพุตของมันมาจากไหน** — ถ้ามาจาก
  ฝั่งเดียวกับสิ่งที่กำลังตรวจ ด่านนั้นเป็นแค่การยืนยันตัวเอง · ตัวชี้วัดง่าย ๆ:
  เขียนเทสต์ที่ทำให้ด่าน **ล้ม** ได้ไหม ถ้าคิดไม่ออกว่าอินพุตแบบไหนจะทำให้ล้ม
  แปลว่ามันล้มไม่ได้ (ญาติของ "control ที่ไม่มีเทสต์ยืนยัน = ไม่มี control")
- **ฟอร์มพิมพ์สำเร็จ "พิมพ์ทุกตัวเลือกไว้ให้ติ๊ก" — การนับว่าเจอคำไหนจึงตอบอะไรไม่ได้เลย**
  ตัวอ่านประเภทเงินได้ ม.40 จากหนังสือรับรอง 50 ทวิ สแกนข้อความทั้งใบแล้วยอมตอบเฉพาะ
  เมื่อ "เจอกลุ่มเดียว" — แต่แบบ 50 ทวิ พิมพ์ **ทุกประเภท** เป็นรายการให้ติ๊ก
  (1. เงินเดือน 40(1) · 2. ค่านายหน้า 40(2) · … · 8. อื่น ๆ 40(8)) ⇒ เจอครบทุกกลุ่ม
  **เสมอ** ⇒ เงื่อนไข `hits.Count == 1` เป็นจริงไม่ได้เลย ⇒ **คืน null ทุกใบ** ⇒
  ด่านตรวจอัตราตาม ท.ป.4/2528 ที่คอมเมนต์บอกว่า "ป้อนให้มันตรวจ" ไม่เคยมีข้อมูลให้ตรวจ
  → กติกา: บนฟอร์มติ๊ก สิ่งที่ตอบคำถามคือ **แถวที่มีค่ากรอกอยู่** (ตัวเลข/เครื่องหมาย)
  ไม่ใช่คำที่ปรากฏบนหน้า · ชี้ขาดไม่ได้ = **คืนว่าไม่รู้** ไม่ใช่หยิบตัวแรก ·
  และก่อนเขียนตัวอ่านที่นับ "คำที่เจอ" ให้ถามก่อนว่า **กระดาษจริงพิมพ์ตัวเลือกทั้งหมด
  ไว้อยู่แล้วหรือเปล่า** (แบบภาษีไทยเกือบทุกใบเป็นแบบนั้น)
- **ผู้มาทีหลังที่ "ทับโดยไม่เทียบ" + ไม่อัปเดตป้ายความมั่นใจ = ผิดสองชั้น**
  ตัวเรียนรู้หมวดรายจ่ายทับ `DebitAccountCode` ที่ความมั่นใจ 0.55 โดยไม่ดูว่าใครใส่ไว้
  ⇒ ทับคำตอบของ NaiveBayes (0.92) และของ **สินค้า master (0.95)** ซึ่ง doc-comment
  เหนือบล็อกนั้นเขียนเองว่า "แข็งแรงที่สุดเพราะคนตั้งใจตั้งไว้" — และเพราะมัน
  **ไม่เขียน `FieldConfidence` ของตัวเอง** ป้ายจึงค้างที่ 0.95 ของเจ้าเดิม ⇒
  ไฮไลต์เหลือง "ตรวจสอบอีกครั้ง" (กฎเหล็ก #3 ข้อ 3) ไม่ขึ้น ทั้งที่ค่าที่ผู้ใช้เห็น
  มาจากแหล่งที่อ่อนกว่ามาก — **ค่าผิดพร้อมป้ายบอกว่ามั่นใจ อันตรายกว่าค่าผิดเฉย ๆ**
  → กติกา: ทุกจุดที่เขียนค่าลงช่องที่มีป้ายความมั่นใจ ต้องเขียน**ความมั่นใจของตัวเอง**
  ลงไปในคำสั่งเดียวกันเสมอ (แยกกันเมื่อไรจะ drift) · และ "ทับ" ต้องมีเงื่อนไขว่า
  **มั่นใจกว่าจริง** ไม่ใช่แค่ "มาทีหลัง" · ไม่ทับก็ต้องบันทึกว่ามีความเห็นต่าง
  ไม่ใช่เงียบ (ไม่งั้นไล่ย้อนไม่ได้ว่าทำไมไม่ใช้คำตอบนั้น)
- **แหล่งที่น่าเชื่อที่สุด กลับเป็นแหล่งที่เสียข้อมูลมากที่สุด — เพราะ "ตัว map" ถูกเขียนทีเดียวแล้วไม่มีใครกลับมาดู**
  `EtaxPdfXmlExtractor` อ่านค่าจาก XML ที่มีลายเซ็นดิจิทัลได้ครบ (หน่วยรายบรรทัด ·
  ส่วนลด · สกุลเงิน) แต่ `MapEtaxToOcrData` **ไม่คัดลอกทั้งสามอย่าง** ⇒ ใบ e-Tax
  ได้หน่วย "ชิ้น" ทุกบรรทัด · ส่วนลดหาย · สกุลเงินถูก**เดาจากข้อความ**ทั้งที่ XML
  ประกาศไว้ชัด ⇒ tier ที่ควรแม่นที่สุดกลับให้ข้อมูลน้อยกว่าเส้นอ่านกระดาษ
  → กติกา: ทุกครั้งที่เขียน "ตัว map จากโครงสร้าง A → B" ให้ไล่ดู**ทุกช่องของ A**
  ว่าถูกใช้หรือถูกทิ้ง แล้วเขียนเหตุผลไว้ตรงจุดที่ตั้งใจทิ้ง — ตัว map แบบ
  allow-list ที่พิมพ์ชื่อช่องเองคือที่เดียวกับที่ defect class "คัดลอกด้วยรายการ
  ที่เขียนมือ = ลืมแน่นอน" อาศัยอยู่ · และ **ค่าที่เอกสารประกาศไว้ต้องชนะการเดาเสมอ**
  ⇒ ต้องมีที่เก็บของมันเอง ไม่ใช่ให้ปลายทางสองที่ต่างคนต่าง `Infer…()` ซ้ำ
- **deny-list ที่ทำงานอัตโนมัติ ต้องถูกทบทวนทุกครั้งที่เพิ่มช่องใหม่**
  `OcrScanSnapshot` ถูกเปลี่ยนเป็น deny-list เพื่อให้ช่องใหม่ถูกคัดลอกตามเอง
  (แก้ปัญหา "ลืมเติมชื่อช่อง") — แต่นั่นแปลว่าช่องใหม่ที่ **ไม่ควร**คัดลอกจะถูก
  คัดลอกเงียบ ๆ ด้วย: `UserCorrectedAt`/`UserCorrectedFields` เป็นร่องรอยของ
  **การอัปโหลดครั้งนั้น** ไม่ใช่ของกระดาษ ⇒ สำเนาถูกนับว่า "ผู้ใช้แก้แล้ว" ทั้งที่
  ยังไม่มีใครแตะ ⇒ ตัวชี้วัดคู่ที่เพิ่งสร้างเสียทันที
  → กติกา: เพิ่มช่องใหม่บน entity ที่มีตัวคัดลอกแบบ deny-list ให้ถามเสมอว่า
  "ช่องนี้เป็นของ **กระดาษ** หรือของ **การอัปโหลดครั้งนี้**" — ของอย่างหลังต้อง
  เข้า deny-list ในคอมมิตเดียวกัน (ทิศของความผิดพลาดกลับด้านจาก allow-list:
  เดิมลืมแล้ว "ขาด" เห็นยาก · ตอนนี้ลืมแล้ว "เกิน" ซึ่งเห็นง่ายกว่าแต่ยังต้องคิด)
