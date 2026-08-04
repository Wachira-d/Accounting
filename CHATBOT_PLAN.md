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

### ✅ Phase 2 — ความทนทาน production (เสร็จแล้ว)
1. **Purge job (PDPA)** — `ChatRetentionPurgeJob` (24 ชม.): ห้อง Public ที่เลย
   `PurgeAfter` → **ลบข้อความทิ้งจริง** (hard delete ไม่ใช่ soft) + ล้าง
   `VisitorName/VisitorEmail/IpHash/SessionToken/PendingChallenge` แล้ว mark
   ห้องเป็น deleted; คงสถิติที่ระบุตัวบุคคลไม่ได้ไว้วัดคุณภาพ. ห้อง Tenant
   ไม่แตะ (เป็นบันทึกการทำงานของกิจการ ลบผ่าน DSR ของบริษัท) + ล้าง
   `ChatRateBuckets` > 2 วันในรอบเดียวกัน
2. **Rate limit ข้าม instance** — `IChatRateLimiter` / `ChatRateLimiter` บน
   ตาราง `ChatRateBuckets` ใช้ `INSERT … ON CONFLICT DO UPDATE … RETURNING`
   (atomic คำสั่งเดียว) — fixed window (นาที/วัน/ชม.) **fail-open** เมื่อ DB
   มีปัญหา เพราะตารางนับพังไม่ใช่เหตุผลที่จะปิดบริการ (ด่านอื่นยังทำงาน)
3. **Training** — `AiFeedbackTrainingJob` loop `ILocalDistillationModel` เดิม
   ครอบ 2 instance ใหม่อยู่แล้ว; เพิ่ม **lazy first-load ใน `PredictAsync`**
   (ไม่งั้นถ้ายังไม่มีใครกด 👍 เลย ความจำจะไม่เคยโหลดทั้งที่มี teacher answer
   ให้เรียนแล้ว) + กันโหลดซ้ำภายใน 5 นาที (job เรียกทีละบริษัท N รอบ)
4. **แจ้งเตือน admin เมื่อ WaitingAgent** — `NotifyAgentNeededAsync` push LINE
   ทันทีทั้งจาก intent ในข้อความและปุ่ม request-agent (best-effort)
5. **Challenge เมื่อยิงรัวซ้ำ** — ชนเพดาน ≥ 3 ครั้ง/ชม. → โจทย์บวกเลขฝั่ง
   server เก็บใน `ChatConversation.PendingChallenge`; ตอบถูกจึงถามต่อได้
   (ไม่พึ่ง CAPTCHA ภายนอก — จะกลายเป็น dependency ที่ล่มแล้วแชทตายทั้งระบบ)

### ✅ Phase 3 — คุณภาพคำตอบ tenant (เสร็จแล้ว)
1. **Context เอกสารเฉพาะใบ** — `AskTenantAsync(…, documentId)` →
   `BuildDocumentContextAsync` สรุปใบนั้น (ประเภท/เลข/วันที่/คู่ค้า+มีเลข
   ผู้เสียภาษีไหม/ยอด/บรรทัด+ผังปัจจุบัน) แนบเป็น context อันแรก;
   ปุ่ม **"💬 ถามผู้ช่วย"** ในหน้ารายละเอียดเอกสาร → `assistant.html?documentId=`
   ซึ่งขึ้นแถบบริบท + เปลี่ยนคำถามลัดให้ตรงใบ (ลงหมวดไหน / หัก ณ ที่จ่าย /
   เคลม VAT / ค่าใช้จ่ายหรือสินทรัพย์)
2. **RAG ย่อยเพิ่มชั้น** — snapshot เพิ่ม: สถานะ ภ.พ.30 ย้อนหลัง 6 งวด
   (ยื่นแล้ว/ยังไม่ยื่น + ยอดสุทธิ) และการตั้งค่ากิจการ/งวดบัญชีล่าสุด
3. **ยังไม่ทำ** — action deep-links ในคำตอบ (ให้บอทแนบลิงก์ที่กดทำงานต่อได้)
   และ "หลายห้องต่อ user" (สคีมารองรับแล้ว เหลืองาน UI + endpoint list)

### ✅ Phase 4 — Admin KB management + วัดผล (เสร็จแล้ว)
1. **หน้า `/admin/chat-kb.html`** — 4 แท็บ: บทความในคลัง (กรอง audience/ค้น,
   เปิด-ปิดใช้งาน, แก้ไข), ❓ คำถามที่ตอบไม่ได้, 🔍 ทดลองค้น (เห็นว่าคำถาม
   หนึ่งดึงบทความไหน + คะแนน), ✏️ เขียนบทความ.
   **ชิ้นที่มาจากไฟล์ .md แก้ที่นี่ไม่ได้** (จะถูกเขียนทับตอน refresh) —
   `UpsertManualChunkAsync` คืน null แล้ว UI บอกให้ไปแก้ไฟล์ต้นทาง
2. **Metrics** — `GET /api/admin/chats/metrics?days=30` → จำนวนห้อง (แยก
   public/tenant), ห้องรอเจ้าหน้าที่, **อัตราตอบด้วย AI จริง** (เป้าหมาย:
   ลดลงเรื่อย ๆ = student จำได้แล้ว), 👍/👎, คะแนนเฉลี่ย, จำนวนบทความ,
   คำถามที่ตอบไม่ได้ (จาก `ChatMessage.NoContextFound`), กราฟรายวัน
3. **Survey** — `RateConversationAsync` 1-5 + ปุ่ม "⭐ ให้คะแนน" ใน widget

### ✅/⏳ Phase 5 — ขยายช่องทาง
1. **✅ LINE bot ต่อ tenant assistant** — คำสั่ง `ถาม {คำถาม}` / `ask …` →
   `AskTenantAsync` ของบริษัทที่ active อยู่ ตอบพร้อมป้าย 🤖/⚙️ (help + ข้อความ
   ตอนผูกบัญชีอัปเดตแล้ว)
2. **⏳ Streaming คำตอบ (SSE)** — ยังไม่ทำ: ต้องมี streaming path ใน
   `IAiProvider`/orchestrator ก่อน (งานกลาง-ใหญ่ กระทบ contract ที่ feature
   อื่นใช้ร่วม — ประเมินผลกระทบก่อนลงมือ)
3. **⏳ อัปเกรด embedding** — สลับ `HashingEmbeddingService` →
   `OnnxSentenceEmbeddingService` ใน DI จุดเดียว; ติดที่ต้อง ship ไฟล์ .onnx
   (~100MB) + วัด RAM/latency ก่อน — retrieval แม่นขึ้นโดยไม่แก้ call site

### งานที่เหลือ (สรุปสำหรับ agent ถัดไป)
- Action deep-links ในคำตอบของบอท (Phase 3.2)
- หลายห้องสนทนาต่อ user + รายการห้องเก่า (Phase 3.4)
- SSE streaming (Phase 5.2) · ONNX embedding (Phase 5.3)
- ทดสอบจริง: kill-switch, audience wall, purge job, challenge, LINE `ถาม`

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
_Last updated: 2026-08-04 — Phase 1-4 + Phase 5.1 implemented; เหลือ deep-links,_
_หลายห้องต่อ user, SSE streaming, ONNX embedding_
