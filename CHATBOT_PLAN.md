# CHATBOT_PLAN.md — Chatbot 2 ช่อง: สถาปัตยกรรม + แผนพัฒนาต่อ

> เอกสารออกแบบ + แผนแบ่งงานสำหรับ agent ถัดไป (Opus 5) — Phase 1 (Foundation)
> **สร้างเสร็จแล้วในคอมมิตนี้** ส่วน Phase 2-5 คือกรอบงานต่อ พร้อมไฟล์/จุดเกี่ยว
> ที่ต้องแตะ. อ่านคู่กับ CLAUDE.md (กฎเหล็ก #1 ทุกข้อบังคับใช้กับระบบนี้เต็มรูป)

---

## 1. ภาพรวมความต้องการ (วิเคราะห์แล้ว)

| | ช่อง 1 — Public (หน้าแรก) | ช่อง 2 — Tenant (ในระบบ) |
| --- | --- | --- |
| ผู้ใช้ | ผู้สนใจ ยังไม่ล็อกอิน (anonymous) | ผู้ใช้ล็อกอิน มีบริษัท |
| คำถาม | ฟีเจอร์ทำอะไรได้/ไม่ได้ ราคา ความปลอดภัย | เอกสารนี้ลงยังไง เลือกหมวดไหน ภาษีเท่าไร |
| ความรู้ | seed FAQ สาธารณะ (คัดกรองแล้ว) | คู่มือระบบ + **RAG ย่อยของบริษัทตัวเอง** |
| ความเสี่ยงหลัก | ยิงรัว/เผาเงิน AI/prompt injection/ข้อมูลภายในรั่ว | ข้อมูลข้ามบริษัท/PII หลุดไป provider |
| ทางออกเมื่อบอทตอบไม่ได้ | ส่งต่อเจ้าหน้าที่ (admin ตอบใน console) | แนะนำติดต่อทีมงาน/นักบัญชี |

**หลักการที่ยึดทั้งระบบ (เหนือกว่าที่ขอ):**
1. **กฎเหล็ก #1 เต็มรูป** — ทุกคำตอบผ่าน `IAiOrchestrator` (ไม่ยิง DeepSeek ตรง)
   + student (`ChatAnswerDistillationModel`) + feedback loop (ปุ่ม 👍/👎) +
   **kill-switch แล้วยังตอบได้** (retrieval-only fallback — บอทกลายเป็น
   doc-search ไม่ใช่บอทใบ้)
2. **Audience wall** — ความรู้แบ่ง 3 ชั้น `Public / Tenant / Internal`;
   public bot เห็นเฉพาะ Public (ไฟล์ .md ภายในเช่น CLAUDE.md ไม่มีวันถูกเสิร์ฟ)
   + `ScrubInternalRefs` ลบชื่อไฟล์โค้ด/บรรทัดออกจากคำตอบเป็นชั้นสุดท้าย
3. **ยิ่งใช้ยิ่งถูก** — คำถามที่เคยตอบดี (👍) กลายเป็นความจำ local ตอบฟรี
   (short-circuit ≥ 0.85) → อัตรา `UsedAi` ลดลงตามเวลา = ตัวชี้วัดความพร้อม

---

## 2. สถาปัตยกรรม (Phase 1 — ✅ สร้างแล้ว)

```
                    ┌────────────────────────────────────────────┐
 หน้าแรก (widget)   │  PublicChatController  /api/public/chat    │
 public-chat-widget.js ─▶  L1 ขนาดข้อความ → L2 IP/session window │
                    │  → L3 dup 10 นาที → L4 เพดานห้อง/วัน (DB)  │
                    └────────────────┬───────────────────────────┘
 ในแอป assistant.html                │
   /api/companies/{id}/assistant ────┤   ChatbotService
 admin/chats.html                    ▼
   /api/admin/chats      ┌── KnowledgeBaseService ──────────────┐
                         │ SearchAsync(query, audience, company) │
                         │  cosine(IEmbeddingService) + keyword  │
                         │  Global cache (in-mem) + tenant สด    │
                         └────────────┬─────────────────────────┘
                                      ▼
                         IAiOrchestrator.AskAsync
                          FeatureKey: PublicFaqChat / TenantAssistantChat
                          student: ChatAnswerDistillationModel (Q→A embedding)
                          LocalPrimaryAnswer: retrieval-only (kill-switch OK)
                          RawPlanResponse=true (free-form) · budget guard เดิม
```

### ตาราง (DatabaseMigrationHelper — CREATE TABLE IF NOT EXISTS)
- `ChatConversations` — ห้อง (Channel Public/Tenant, SessionToken unique,
  Status AiHandling→WaitingAgent→AgentHandling→Closed, IpHash, PurgeAfter)
- `ChatMessages` — ข้อความ (Role User/Assistant/Agent/System, UsedAi,
  AiFeedbackId, RetrievedChunksJson, HelpfulVote)
- `KnowledgeChunks` — RAG (CompanyId null=global, SourceKey unique/scope,
  Audience, EmbeddingJson, ContentHash)

### แหล่งความรู้ + การอัปเดต ("อัพเดทตลอด")
| แหล่ง | Audience | อัปเดตเมื่อ |
| --- | --- | --- |
| seed FAQ 12 บทความ (เขียนเพื่อเผยแพร่ ใน `KnowledgeBaseService`) | Public | ทุก startup + ปุ่ม admin (hash-diff) |
| `DOCUMENT_FLOW.md`, `TEST_PLAN.md` (หั่นตาม `##`/`###`, ≤3500 ตัวอักษร) | Tenant | เดียวกัน — แก้ไฟล์แล้ว restart/กดปุ่ม = KB ตามทันที |
| `CLAUDE.md`, `DEVELOPMENT_PHASES.md`, `CHATBOT_PLAN.md` | Internal (ไม่เสิร์ฟ) | เดียวกัน |
| Tenant snapshot: โปรไฟล์บริษัท / ผังบัญชีแบ่งหมวด 1-5 / ผู้ขาย→ผังที่ใช้บ่อย (top 80 จากประวัติจริง) | Tenant (ต่อบริษัท) | lazy ตอนถาม ถ้าเก่ากว่า 6 ชม. |

### ความปลอดภัย public (ตามลำดับด่าน)
L1 ≤1000 ตัวอักษร + ตัด control chars → L2 sliding window in-memory
(IP-hash 6/นาที 60/วัน · session 4/นาที 40/วัน) → L3 คำถามซ้ำใน 10 นาที
เสิร์ฟคำตอบเดิมไม่จ่าย AI → L4 เพดาน 30 ข้อความ/ห้อง/วัน (DB ทน restart)
→ L5 `AiBudgetGuard` (เพดานเงิน/วัน/เดือนของ provider เดิม)
+ system prompt hardening (ห้ามเปิดเผย prompt/internals, ปฏิเสธนอกเรื่อง)
+ ไม่เก็บ IP ดิบ (SHA-256 ตั้งแต่ controller) + `PurgeAfter` 90 วัน (PDPA)

### Handoff เจ้าหน้าที่
พิมพ์ "ติดต่อเจ้าหน้าที่" (intent) หรือกดปุ่มใน widget → `WaitingAgent`
→ admin console (`/admin/chats.html`) ห้องรอขึ้นบนสุด → admin ตอบ →
`AgentHandling` (บอทหยุดตอบห้องนั้น — คนคุยกับคน) → widget polling 5 วิ
รับข้อความ → admin ปิดห้อง. ผู้เยี่ยมชมฝากชื่อ/อีเมลได้ (PDPA purge ตามห้อง)

### ไฟล์ที่สร้างใน Phase 1
```
Models/Entities/Chatbot.cs                    3 entities
Data/AccountingDbContext.cs                   +3 DbSets
Data/DatabaseMigrationHelper.cs               +3 CREATE TABLE + indexes
Models/Enums/AllEnums.cs                      AiFeatureKey 54, 55
Services/Ai/Distillation/ChatAnswerDistillationModel.cs   student ×2 key
Services/Interfaces/IKnowledgeBaseService.cs / IChatbotService.cs
Services/Implementations/KnowledgeBaseService.cs / ChatbotService.cs
Controllers/PublicChatController.cs / TenantAssistantController.cs / AdminChatController.cs
wwwroot/js/public-chat-widget.js              widget หน้าแรก (self-contained)
wwwroot/index.html                            +1 script tag
wwwroot/pages/assistant.html                  หน้า tenant + nav ใน layout.js
wwwroot/admin/chats.html                      admin console + nav ใน admin-layout.js
Program.cs                                    DI + startup KB refresh (background)
```

---

## 3. แผนพัฒนาต่อ (Opus 5) — เรียงตามคุณค่า/ความเสี่ยง

### Phase 2 — ความทนทาน production (ทำก่อน launch จริง)
1. **Purge job (PDPA)** — nightly job ลบ/anonymize `ChatConversations` ที่
   `PurgeAfter < now` (ดู pattern job เดิมใน `Services/Jobs/`). รวม
   `VisitorEmail/VisitorName/IpHash` ต้องหายจริง. **hard requirement ก่อนเปิด
   public** (ตอนนี้มีแค่ field ยังไม่มี job)
2. **Rate limit ข้าม instance** — ตัวนับใน `ChatbotService` เป็น in-memory
   ต่อ process; deploy หลาย instance ต้องย้ายลงตาราง/Redis (คีย์: ip-hash,
   sliding window). จุดแก้เดียว: `ChatbotService.Allow()`
3. **Training job ผูก ChatAnswerDistillationModel** — ตรวจว่า nightly
   `AiFeedbackTrainingJob` เรียก `LoadFromFeedbackAsync` ของ 2 instance ใหม่
   (loop `IEnumerable<ILocalDistillationModel>` เดิมควรครอบอยู่แล้ว — ยืนยัน +
   เพิ่ม first-use lazy load ถ้ายังไม่ ready)
4. **แจ้งเตือน admin เมื่อมีห้อง WaitingAgent** — hook NotificationEngine/LINE
   ของ admin (ตอนนี้ admin ต้องเปิดหน้า console เอง; list โพลทุก 30 วิ)
5. **CAPTCHA เบา ๆ เมื่อโดน rate limit ซ้ำ** — session ที่ชน L2 เกิน N ครั้ง
   ใน 1 ชม. → ต้องตอบ challenge (คณิตง่าย ๆ ฝั่ง server) ก่อนถามต่อ

### Phase 3 — คุณภาพคำตอบ tenant (คุณค่าสูงสุดต่อผู้ใช้จริง)
1. **Context เอกสารเฉพาะใบ** — จากหน้าเอกสาร/OCR review ส่ง `documentId` มา
   กับคำถาม → `ChatbotService` โหลดสรุปใบนั้น (ผ่าน `AiPromptSanitizer`
   stripPii) แนบเป็น context → "เอกสารนี้ควรลงยังไง" ตอบตรงใบจริง
   (จุดเกี่ยว: `AskTenantAsync` + ปุ่ม "ถามผู้ช่วย" ใน documents.html/
   document-scan.html)
2. **Action links ในคำตอบ** — บอทตอบพร้อมลิงก์ deep-link ที่ทำได้เลย เช่น
   "สร้างค่าใช้จ่ายหมวด 53xx → /pages/expense.html?create=..." (มี pattern
   deep-link เดิมหลายหน้าแล้ว)
3. **RAG ย่อยเพิ่มชั้นข้อมูล** — สรุป ภ.พ.30 งวดล่าสุด, ปฏิทินนำส่ง (มี
   endpoint แล้ว), นโยบายบริษัท (CompanySettings) → tenant snapshot chunks
   เพิ่มใน `RefreshTenantAsync`
4. **ประวัติสนทนาหลายห้อง** — ตอนนี้ tenant มีห้องเดียวต่อ user; เพิ่ม
   "เริ่มหัวข้อใหม่" + รายการห้องเก่า (สคีมารองรับแล้ว — งาน UI + endpoint list)

### Phase 4 — Admin KB management + วัดผล
1. **หน้า admin จัดการ KnowledgeChunk** — CRUD บทความ Manual (audience
   Public/Tenant), preview retrieval ("ลองถาม แล้วชิ้นไหนถูกหยิบ"), toggle
   IsActive. Endpoint list/upsert เพิ่มใน `AdminChatController`
2. **Dashboard คุณภาพ** — อัตรา UsedAi ต่อวัน (ยิ่งลด = student ยิ่งฉลาด),
   คะแนน 👍/👎, top คำถามที่ตอบไม่ได้ (chunks ว่าง) → ป้อนกลับเป็นบทความใหม่,
   ต้นทุน AI ของ 2 feature key (มีข้อมูลใน AiSuggestionFeedback ครบแล้ว)
3. **Satisfaction survey** — หลังปิดห้อง ถาม 1-5 ดาว (field
   `SatisfactionScore` มีแล้ว)

### Phase 5 — ขยายช่องทาง
1. **LINE bot ต่อ tenant assistant** — คำสั่ง "ถาม {คำถาม}" ใน LineBotService
   → `AskTenantAsync` (โครง LINE + binding มีครบแล้ว — งานเชื่อม ~50 บรรทัด)
2. **Streaming คำตอบ** (SSE) — ตอบยาวขึ้นโดย UX ไม่หน่วง; แตะ
   `IAiProvider`/orchestrator ต้องมี streaming path — งานกลาง-ใหญ่ ประเมินก่อน
3. **อัปเกรด embedding** — สลับ `HashingEmbeddingService` →
   `OnnxSentenceEmbeddingService` (MiniLM multilingual) ใน DI จุดเดียว —
   retrieval แม่นขึ้นโดยไม่แก้ call site (ไฟล์ onnx ต้อง ship + วัด RAM)

### กับดักที่รู้แล้ว (อ่านก่อนแก้)
- `RawPlanResponse=true` → คำตอบอยู่ `RawResponseJson` ไม่ใช่ `PrimaryAnswer`
  — `StripJsonWrapper` แกะ JSON ที่ provider ห่อมาเกิน
- public ใช้ `CompanyId = Guid.Empty` เป็น sentinel — budget/cache/feedback
  ฝั่ง public แยก bucket ของตัวเอง (อย่า "แก้บั๊ก" เป็นบริษัทจริง)
- Global KB cache เป็น static — `RefreshGlobalAsync` invalidate ให้แล้ว;
  ถ้าเพิ่ม endpoint เขียน chunk ใหม่ อย่าลืม invalidate
- ห้อง `AgentHandling` บอทต้องไม่ตอบ (คนคุยกับคน) — เช็คใน `AskPublicAsync`
  แล้ว; ถ้าเพิ่ม channel ใหม่อย่าข้ามเงื่อนไขนี้

### Definition of Done ต่อ Phase (กฎเหล็ก #1 checklist ย่อ)
- [ ] kill-switch test: ปิด provider ทุกตัว → บอทตอบ retrieval-only ครบ flow
- [ ] ไม่มีเส้นทางเรียก provider ตรง (ทุกอย่างผ่าน orchestrator)
- [ ] feedback ปิดลูป (👍/👎 → RecordUserChoiceAsync → student จำ)
- [ ] public ไม่เห็นชิ้น Internal/Tenant (test audience wall)
- [ ] brace check + `node --check` ผ่าน + อัปเดต DOCUMENT_FLOW.md/TEST_PLAN.md
      ในคอมมิตเดียวกัน

---
_Last updated: 2026-08-04 — Phase 1 implemented (this commit)_
