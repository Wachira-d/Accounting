using Accounting.Models.Entities;

namespace Accounting.Services.Interfaces;

public record ChatAskResult(
    Guid ConversationId,
    string SessionToken,
    Guid? MessageId,          // ข้อความตอบ (null = แค่ ack เช่น รอเจ้าหน้าที่)
    string Answer,
    bool UsedAi,              // ป้ายซื่อสัตย์ 🤖/⚙️
    string ConversationStatus,
    bool RateLimited = false);

public record ChatHistoryItem(Guid Id, string Role, string Content, bool UsedAi,
    DateTime CreatedAt, int? HelpfulVote);

/// <summary>บริการ chatbot ทั้ง 2 ช่อง — ดู CHATBOT_PLAN.md.
/// ทุกคำตอบวิ่งผ่าน IAiOrchestrator (กฎเหล็ก #1) โดยมี retrieval-only
/// fallback เป็น LocalPrimaryAnswer เสมอ — ปิด AI ทุกตัวแล้วยังตอบได้.</summary>
public interface IChatbotService
{
    /// <summary>public chat หน้าแรก — anonymous session + rate limit หลายชั้น.
    /// ipHash = SHA-256 ของ IP (คนเรียกทำ hash มาแล้ว — service ไม่เห็น IP ดิบ).</summary>
    Task<ChatAskResult> AskPublicAsync(string? sessionToken, string ipHash, string message,
        string? visitorName, string? visitorEmail, CancellationToken ct = default);

    /// <summary>tenant assistant — ผู้ใช้ล็อกอิน ถามในบริบทบริษัทตัวเอง
    /// (RAG ย่อย refresh อัตโนมัติเมื่อ stale).</summary>
    Task<ChatAskResult> AskTenantAsync(Guid companyId, Guid userId, string userEmail,
        string message, CancellationToken ct = default);

    /// <summary>ขอคุยกับเจ้าหน้าที่ — เปลี่ยนสถานะห้องเป็น WaitingAgent.</summary>
    Task<bool> RequestAgentAsync(Guid conversationId, string? sessionToken,
        string? visitorName, string? visitorEmail, CancellationToken ct = default);

    /// <summary>ดึงข้อความใหม่หลัง afterId (ใช้ polling ฝั่ง widget — จำเป็น
    /// ช่วงเจ้าหน้าที่ตอบ). sessionToken บังคับตรวจเสมอสำหรับ public.</summary>
    Task<List<ChatHistoryItem>> GetMessagesAsync(Guid conversationId, string? sessionToken,
        Guid? companyId, Guid? userId, DateTime? after, CancellationToken ct = default);

    /// <summary>👍/👎 บนคำตอบ — ปิดลูป distillation (RecordUserChoiceAsync).</summary>
    Task<bool> VoteAsync(Guid messageId, string? sessionToken, Guid? companyId,
        int vote, CancellationToken ct = default);

    // ── ฝั่ง admin console ──
    Task<List<ChatConversation>> ListConversationsAsync(string? status, string? channel,
        int page, int pageSize, CancellationToken ct = default);
    Task<List<ChatHistoryItem>> GetConversationMessagesAsync(Guid conversationId, CancellationToken ct = default);
    Task<bool> AgentReplyAsync(Guid conversationId, string agentName, string content, CancellationToken ct = default);
    Task<bool> CloseConversationAsync(Guid conversationId, string agentName, CancellationToken ct = default);
}
