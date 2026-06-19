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
2. **Feature parity** — ทุก `AiFeatureKey` ต้องมี `ILocalDistillationModel`
   ที่ register แล้ว 1 ตัวเสมอ ห้ามมี feature ที่เรียก AI ได้แต่ไม่มี
   student รองรับ (= สร้าง dependency ที่ถอดไม่ได้)
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
| Distillation model | `GlAccountDistillationModel` | `Services/Ai/Distillation/GlAccountDistillationModel.cs` |
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
