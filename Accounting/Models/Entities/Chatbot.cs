namespace Accounting.Models.Entities;

// ══════════════════════════════════════════════════════════════════════════
//  Chatbot 2 ส่วน (ดู CHATBOT_PLAN.md ประกอบ)
//    • Public  — หน้าแรก ให้ผู้สนใจถามฟีเจอร์/ข้อสงสัย (anonymous session)
//    • Tenant  — ผู้ใช้ในระบบถามวิธีลงบัญชี/เลือกหมวด (มี RAG ย่อยต่อบริษัท)
//  ทั้งสองช่องวิ่งผ่าน IAiOrchestrator (กฎเหล็ก #1) — student-first, capture
//  feedback, kill-switch แล้วยังตอบได้จาก retrieval ล้วน
// ══════════════════════════════════════════════════════════════════════════

/// <summary>ห้องสนทนา 1 ห้อง — public ระบุด้วย SessionToken (anonymous),
/// tenant ระบุด้วย CompanyId+UserId. ไม่สืบทอด TenantEntity เพราะ public
/// chat ไม่มีบริษัท (CompanyId nullable).</summary>
public class ChatConversation : BaseEntity
{
    /// <summary>Public | Tenant</summary>
    public string Channel { get; set; } = "Public";

    public Guid? CompanyId { get; set; }
    public Guid? UserId { get; set; }

    /// <summary>anonymous session ของ public chat (GUID ฝั่ง client เก็บใน
    /// localStorage) — unique ต่อห้อง</summary>
    public string? SessionToken { get; set; }

    /// <summary>ข้อมูลติดต่อกลับ (ผู้เยี่ยมชมกรอกเองตอนขอคุยกับเจ้าหน้าที่)
    /// — PDPA: เก็บตาม PurgeAfter แล้วลบ/anonymize</summary>
    public string? VisitorName { get; set; }
    public string? VisitorEmail { get; set; }

    /// <summary>SHA-256 ของ IP — ใช้ rate-limit/ตรวจ abuse โดยไม่เก็บ IP ดิบ
    /// (PDPA data minimization)</summary>
    public string? IpHash { get; set; }

    /// <summary>AiHandling | WaitingAgent | AgentHandling | Closed</summary>
    public string Status { get; set; } = "AiHandling";

    /// <summary>คำถามแรก (ตัดสั้น) — โชว์ในลิสต์ admin</summary>
    public string? Title { get; set; }

    public int MessageCount { get; set; }
    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;

    public DateTime? AgentJoinedAt { get; set; }
    public string? AgentName { get; set; }

    /// <summary>คะแนนจากผู้ใช้หลังปิดห้อง (1-5) — วัดคุณภาพบอท/เจ้าหน้าที่</summary>
    public int? SatisfactionScore { get; set; }

    /// <summary>PDPA retention — nightly job ลบห้อง public ที่เลยกำหนด
    /// (default สร้าง+90 วัน)</summary>
    public DateTime? PurgeAfter { get; set; }

    /// <summary>คำตอบของโจทย์ challenge ที่ค้างอยู่ (เช่น "12") — ตั้งเมื่อ
    /// session ชนเพดานซ้ำหลายครั้ง; ต้องตอบให้ถูกก่อนถามต่อ. null = ไม่มีโจทย์</summary>
    public string? PendingChallenge { get; set; }
}

/// <summary>ข้อความ 1 บรรทัดในห้อง — role แยกคนตอบชัดเจนเพื่อป้ายซื่อสัตย์
/// (🤖 AI / ⚙️ ระบบ / 👤 เจ้าหน้าที่)</summary>
public class ChatMessage : BaseEntity
{
    public Guid ConversationId { get; set; }
    public ChatConversation Conversation { get; set; } = null!;

    /// <summary>User | Assistant | Agent | System</summary>
    public string Role { get; set; } = "User";

    public string Content { get; set; } = "";

    /// <summary>true = คำตอบมาจาก AI provider จริง; false = local
    /// (retrieval/distilled) — UI ติดป้ายตามจริง (กฎเหล็ก #1)</summary>
    public bool UsedAi { get; set; }
    public decimal? AiConfidence { get; set; }

    /// <summary>AiSuggestionFeedback.Id ที่ orchestrator คืน — ปุ่ม 👍/👎
    /// เรียก RecordUserChoiceAsync ปิดลูป distillation</summary>
    public Guid? AiFeedbackId { get; set; }

    /// <summary>chunk ids + คะแนนที่ retrieval หยิบมาใช้ (JSON) — debug/audit
    /// ว่าคำตอบอ้างจากความรู้ชิ้นไหน</summary>
    public string? RetrievedChunksJson { get; set; }

    /// <summary>ผู้ดูแลปักธงข้อความที่ตอบผิด/ต้องตรวจ</summary>
    public bool FlaggedForReview { get; set; }

    /// <summary>โหวตจากผู้ถาม: 1 = 👍, -1 = 👎, null = ยังไม่โหวต</summary>
    public int? HelpfulVote { get; set; }

    /// <summary>retrieval ไม่เจอชิ้นความรู้ที่เกี่ยวเลย — สัญญาณตรงว่าคลัง
    /// ความรู้ยังขาดเรื่องนี้ (หน้า admin เอาไปทำรายการ "คำถามที่ตอบไม่ได้"
    /// เพื่อเขียนบทความเพิ่ม)</summary>
    public bool NoContextFound { get; set; }
}

/// <summary>ชิ้นความรู้ 1 ก้อนใน RAG — global (CompanyId null) จากไฟล์ .md /
/// บทความที่ admin เขียน, หรือ tenant snapshot (CompanyId set) ที่สร้างจาก
/// ข้อมูลจริงของบริษัทนั้น (ผังบัญชี, ผู้ขายประจำ, การตั้งค่า).</summary>
public class KnowledgeChunk : BaseEntity
{
    /// <summary>null = ความรู้กลางของระบบ; มีค่า = RAG ย่อยของบริษัทนั้น</summary>
    public Guid? CompanyId { get; set; }

    /// <summary>File | Manual | TenantSnapshot</summary>
    public string SourceType { get; set; } = "File";

    /// <summary>คีย์คงที่ต่อชิ้นไว้ upsert ตอน refresh — เช่น
    /// "DOCUMENT_FLOW.md#2.2b" / "seed:faq-vat" / "tenant:coa:1"</summary>
    public string SourceKey { get; set; } = "";

    public string Title { get; set; } = "";
    public string Content { get; set; } = "";

    /// <summary>Public = บอทหน้าแรกใช้ได้ | Tenant = ผู้ใช้ล็อกอิน |
    /// Internal = ห้ามส่งออกนอกทีมพัฒนา (เช่น CLAUDE.md).
    /// Public bot ดึงเฉพาะ Public เท่านั้น — กันเอกสารภายในรั่วผ่านคำตอบ</summary>
    public string Audience { get; set; } = "Internal";

    /// <summary>เวกเตอร์จาก IEmbeddingService (float[] json) — คำนวณตอน
    /// ingest ครั้งเดียว query มาแค่ cosine</summary>
    public string? EmbeddingJson { get; set; }

    /// <summary>SHA-256 ของ Content — refresh แล้ว re-embed เฉพาะชิ้นที่เปลี่ยน</summary>
    public string ContentHash { get; set; } = "";

    public bool IsActive { get; set; } = true;
}
