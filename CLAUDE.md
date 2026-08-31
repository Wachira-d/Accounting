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
- checker ใหม่ทุกตัวต้องผ่าน **negative test** ก่อนเชื่อ: ใส่บั๊กที่ตั้งใจจับ
  กลับเข้าไปแล้วยืนยันว่า checker จับได้จริง (เคยมี checker ที่ regex ผิด
  จนไม่จับเคสหลักของตัวเอง)
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

