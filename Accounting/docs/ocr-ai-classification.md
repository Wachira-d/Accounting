# OCR → DeepSeek → Local Model (การจัดหมวดบัญชีค่าใช้จ่าย)

> เอกสารนี้อธิบายว่า "ตอนสแกนเอกสาร ระบบจัดบัญชีเดบิต (หมวดค่าใช้จ่าย) ให้
> อย่างไร" และเหตุผลที่เคยจัดผิด (เช่น *มัลติฟังก์ชั่น Epson L6370* → *ค่าขนส่ง*)

## TL;DR

ตอน OCR สแกนเสร็จ ระบบจะ **โยนรายการไปถาม DeepSeek** (AI provider ที่
เชื่อมต่อไว้) เพื่อเลือกผังบัญชี แล้ว **เอาคำตอบมาสอน local model**
(distillation). ครั้งต่อ ๆ ไปถ้า local model มั่นใจพอ จะตอบเองโดย
**ไม่เสีย token** — DeepSeek ถูกเรียกเฉพาะตอนที่ยังไม่มั่นใจ

```
สแกน → [local resolvers: keyword + สถิติ] → [DeepSeek teacher]
                                                  │
                                       AiSuggestionFeedback (เก็บคำตอบ)
                                                  │
                            nightly AiFeedbackTrainingJob (distill)
                                                  │
                                   GlAccountDistillationModel (student)
```

## ทำไมเคยจัดผิด

ก่อนหน้านี้ pipeline การจัดบัญชี **ไม่เคยเรียก AI เลย** — ใช้แค่ 4 ชั้นที่
เป็น rule/สถิติล้วน:

| ชั้น | ไฟล์ | กลไก |
| --- | --- | --- |
| Keyword + brand | `Ocr/ExpenseCategoryResolver.cs` | match คำ/ยี่ห้อในข้อความ |
| Association rule | `Ocr/AssociationRuleMiner.cs` | Apriori basket mining ข้ามผู้เช่า |
| Naive Bayes + TF-IDF | `Ocr/TfIdfNaiveBayesClassifier.cs` | per-tenant probabilistic |
| Vendor memory | `Ocr/ExpenseCategoryLearner.cs` | จำว่าผู้ขายรายนี้เคยลงบัญชีไหน |

คำว่า "มัลติฟังก์ชั่น / เครื่องพิมพ์ / Epson" **ไม่มีใน keyword list** ของ
resolver → คะแนนต่ำ → ชั้นสถิติที่มี training บาง ๆ override ไปเป็น *ค่าขนส่ง*
แบบมั่ว ป้าย "🤖 AI แนะนำ" บน UI จึง **ไม่จริง** (ไม่มี AI อยู่ใน path)

## ของใหม่: DeepSeek เป็น teacher, local model เป็น student

โครงสร้าง AI orchestration มีอยู่แล้วในโปรเจกต์ แค่ OCR scan ไม่เคยเรียก
ตอนนี้ wire เข้าแล้วที่ `OcrService.cs` (หลังชั้น VendorIntel):

```csharp
var glResult = await _aiAugmenter.SuggestGlAccountAsync(
    companyId, scanResult.Id,
    vendorName, vendorTaxId, industry,
    lineDescription, amount, "THB",
    localBestAccountCode, localConfidence, ct);
```

`SuggestGlAccountAsync` → `AiOrchestrator.AskAsync(AiFeatureKey.GlAccountSuggestion)`
ซึ่งจัด routing เอง:

1. **Student ก่อน** — `GlAccountDistillationModel.PredictAsync`. ถ้ามั่นใจ
   ≥ threshold → ตอบเลย **ไม่เรียก DeepSeek** (ประหยัด quota)
2. **Teacher fallback** — ถ้า student ไม่มั่นใจ → เรียก DeepSeek จริง
3. **บันทึก feedback** — เก็บคำตอบลง `AiSuggestionFeedback` (คืน `FeedbackId`)
4. **Drift sampling** — สุ่มเทียบ teacher vs student เพื่อวัด accuracy

> มีตัวกั้นงบ (`AiBudgetGuard`: DailyCallCap / MonthlyBudgetUsd) — เกินงบ
> จะ fallback มาใช้ local อัตโนมัติ ไม่ throw

### การ apply คำตอบ

จะใช้คำตอบ AI ก็ต่อเมื่อ **(1) AI รันจริง (2) confidence ≥ 0.70 (3) เป็น
รหัสบัญชีที่มีอยู่จริงใน CoA ของบริษัทนั้น** (กัน hallucination). มิฉะนั้น
คงผลจากชั้น local ไว้

## การปิด loop "เอาคำตอบมาสอน local model"

มี 2 กลไกที่ทำให้ local model ฉลาดขึ้นเรื่อย ๆ:

1. **`ExpenseCategoryLearner.RecordAsync`** — ตอนผู้ใช้กด *บันทึก & สอนระบบ*
   หรือสร้างเอกสาร ระบบเขียน `OcrCategoryMappings(vendor, keyword → account)`
   ซึ่งชั้น local อ่านกลับทันทีในการสแกนครั้งถัดไป
2. **Distillation feedback** — `OcrService.SubmitCorrectionAsync` เรียก
   `IAiFeedbackRecorder.RecordUserChoiceAsync(GlAccountAiFeedbackId, chosenCode,
   acceptedAi)` → set `UserChosenAt` บน feedback row. งานกลางคืน
   `AiFeedbackTrainingJob` mine เฉพาะ row ที่ `UserChosenAt != null`
   มา retrain `GlAccountDistillationModel` (Wilson-score + embedding index)

`acceptedAi = true` เมื่อผู้ใช้คงรหัสที่ AI แนะนำไว้ไม่เปลี่ยน — ใช้วัด
teacher accuracy

## ป้ายบน UI (ซื่อสัตย์)

`OcrResultResponse.GlAccountUsedAi` บอกว่า DeepSeek จัดบัญชีจริงบนสแกนนี้
หรือไม่ → `document-scan.html` แสดง:

- `🤖 AI (DeepSeek) แนะนำ` — เมื่อ `glAccountUsedAi == true`
- `⚙️ ระบบแนะนำ (rule-based)` — เมื่อมาจาก keyword/สถิติ (ยังไม่เรียก AI
  หรือ local model มั่นใจแล้วเลย short-circuit)

## ไฟล์ที่เกี่ยวข้อง

| งาน | ไฟล์ |
| --- | --- |
| เรียก DeepSeek ตอนสแกน | `Services/Implementations/OcrService.cs` (บล็อก *DeepSeek GL-account classification*) |
| ปิด loop feedback | `Services/Implementations/OcrService.cs` → `SubmitCorrectionAsync` |
| AI augmenter | `Services/Ai/OcrAiAugmenter.cs` → `SuggestGlAccountAsync` |
| Orchestration / routing | `Services/Ai/AiOrchestrator.cs`, `AiFeatureRoutingResolver.cs` |
| Provider | `Services/Ai/Providers/DeepSeekProvider.cs` |
| Student model | `Services/Ai/Distillation/GlAccountDistillationModel.cs` |
| งาน retrain กลางคืน | `AiFeedbackTrainingJob` |
| Local learner | `Services/Implementations/Ocr/ExpenseCategoryLearner.cs` |
| Entity fields | `Models/Entities/Intelligence.cs` (`GlAccountUsedAi`, `GlAccountAiFeedbackId`) |
| คอลัมน์ DB | `Data/DatabaseMigrationHelper.cs` |

## วิธีเปิด/ปิด DeepSeek

ตั้งค่าผ่าน `AiIntegration` / `AiProviderConfig` (admin): `ProviderType =
DeepSeek`, `IsActive = true`, ใส่ `ApiKey` + `Endpoint` + `Model`. ถ้าไม่มี
provider ที่ active ระบบจะ fallback มาใช้ชั้น local เดิมแบบ graceful — ป้าย
จะขึ้น *ระบบแนะนำ (rule-based)* ตามจริง
