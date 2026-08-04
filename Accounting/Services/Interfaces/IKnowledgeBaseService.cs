using Accounting.Models.Entities;

namespace Accounting.Services.Interfaces;

/// <summary>คลังความรู้ของ chatbot (RAG) — ดู CHATBOT_PLAN.md.
/// Global: ingest ไฟล์ .md ของ repo + seed FAQ สาธารณะ (upsert ด้วย hash —
/// ไฟล์เปลี่ยนแล้ว refresh เฉพาะชิ้นที่เปลี่ยน = "อัพเดทตลอด").
/// Tenant: สร้าง snapshot จากข้อมูลจริงของบริษัท (ผังบัญชี/ผู้ขาย/ตั้งค่า)
/// แบบ lazy — เรียกก่อนตอบทุกครั้ง ถ้าเก่ากว่า TTL ค่อย rebuild.</summary>
public interface IKnowledgeBaseService
{
    /// <summary>ingest ไฟล์ .md + seed FAQ → KnowledgeChunks (global).
    /// เรียกตอน startup (background) + ปุ่ม refresh ของ admin.</summary>
    Task<int> RefreshGlobalAsync(CancellationToken ct = default);

    /// <summary>rebuild RAG ย่อยของบริษัท ถ้า snapshot เก่ากว่า
    /// <paramref name="maxAge"/> (default 6 ชม.) — คืน true เมื่อ rebuild จริง.</summary>
    Task<bool> RefreshTenantIfStaleAsync(Guid companyId, TimeSpan? maxAge = null, CancellationToken ct = default);

    /// <summary>ค้น top-K ชิ้นที่ใกล้คำถามที่สุด — audience บังคับขอบเขต:
    /// "Public" เห็นเฉพาะ Public; "Tenant" เห็น Public+Tenant (global) +
    /// ชิ้นของบริษัทตัวเอง. cosine (IEmbeddingService) + keyword bonus.</summary>
    Task<List<(KnowledgeChunk Chunk, double Score)>> SearchAsync(
        string query, string audience, Guid? companyId, int topK = 4, CancellationToken ct = default);
}
