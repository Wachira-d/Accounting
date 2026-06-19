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
