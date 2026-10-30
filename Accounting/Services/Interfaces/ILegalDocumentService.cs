namespace Accounting.Services.Interfaces;

/// <summary>ฉบับของเอกสารที่แสดง — <c>Source</c> = "published" (แถวที่แอดมินเผยแพร่) | "builtin" (ฉบับตั้งต้นในโค้ด)</summary>
public sealed record LegalDocumentView(
    string Kind, string Title, string Version, DateOnly EffectiveDate, string BodyMarkdown,
    string Source, DateTime? PublishedAt, string? ChangeSummary);

/// <summary>เวอร์ชันของสองเอกสาร + เวอร์ชันความยินยอมรวม (<c>LegalDocumentPolicy.ConsentVersion</c>)</summary>
public sealed record LegalConsentVersion(string PrivacyVersion, string TermsVersion, string ConsentVersion);

public sealed record LegalHistoryItem(
    string Version, DateOnly EffectiveDate, DateTime? PublishedAt, string? PublishedBy, string? ChangeSummary,
    bool IsCurrent, bool IsUpcoming, bool IsBuiltIn);

public sealed record LegalDraftInput(string? Version, DateOnly? EffectiveDate, string? BodyMarkdown, string? ChangeSummary);

public sealed record LegalDraftView(Guid Id, string Version, DateOnly EffectiveDate, string BodyMarkdown, string? ChangeSummary,
    DateTime CreatedAt, DateTime? UpdatedAt, string? UpdatedBy);

/// <summary>
/// รอบ 214 (คำตัดสินข้อ 173) — เอกสารทางกฎหมายของแพลตฟอร์ม (นโยบายความเป็นส่วนตัว · ข้อกำหนดการใช้งาน):
/// <b>resolver เวอร์ชันตัวเดียว</b> ของทั้งระบบ (หน้า privacy/terms · ความยินยอมตอนสมัคร · แถบแจ้งฉบับใหม่) + งานแอดมิน (ร่าง/เผยแพร่/ประวัติ)
/// · กติกาอยู่ที่ <c>Helpers/LegalDocumentPolicy</c> · เนื้อความตั้งต้น <c>Helpers/LegalDocumentDefaults</c> · แปลงเป็น HTML <c>Helpers/LegalMarkdown</c>
/// </summary>
public interface ILegalDocumentService
{
    /// <summary>ฉบับที่มีผลวันนี้ (ไม่มีฉบับเผยแพร่ ⇒ ฉบับตั้งต้น 1.0)</summary>
    Task<LegalDocumentView> GetCurrentAsync(string kind, CancellationToken ct = default);

    /// <summary>ฉบับที่ประกาศล่วงหน้า (เผยแพร่แล้ว วันมีผลยังไม่ถึง) — null = ไม่มี</summary>
    Task<LegalDocumentView?> GetUpcomingAsync(string kind, CancellationToken ct = default);

    /// <summary>ฉบับเฉพาะเลข (ดูย้อนหลัง) — "1.0" ที่ไม่มีแถว = ฉบับตั้งต้น · null = ไม่พบ/ยังเป็นร่าง</summary>
    Task<LegalDocumentView?> GetPublishedVersionAsync(string kind, string version, CancellationToken ct = default);

    /// <summary>เวอร์ชันความยินยอมปัจจุบัน — ผู้เรียก: AuthService (บันทึก PdpaConsentRecord) · LegalController (หน้าเว็บ + แถบแจ้ง)</summary>
    Task<LegalConsentVersion> GetConsentVersionAsync(CancellationToken ct = default);

    /// <summary>แปลง markdown + แทนโทเคนผู้ควบคุมข้อมูลจาก SiteSettings (ตัวตั้งเดียวกับ /api/legal/policy)</summary>
    Task<string> RenderPlatformAsync(string markdown, CancellationToken ct = default);

    Task<List<LegalHistoryItem>> GetHistoryAsync(string kind, CancellationToken ct = default);

    Task<LegalDraftView?> GetDraftAsync(string kind, CancellationToken ct = default);
    Task<LegalDraftView> SaveDraftAsync(string kind, LegalDraftInput input, string actor, CancellationToken ct = default);
    /// <summary>เผยแพร่ร่าง (ตรวจด้วย <c>LegalDocumentPolicy.ValidatePublish</c>) — หลังจากนี้แถวแก้ไม่ได้</summary>
    Task<LegalHistoryItem> PublishDraftAsync(string kind, string actor, string? ipAddress = null, CancellationToken ct = default);
    Task<bool> DiscardDraftAsync(string kind, string actor, CancellationToken ct = default);

    /// <summary>ผู้ใช้ที่ล็อกอินอยู่ยอมรับฉบับปัจจุบันแล้วหรือยัง (แถบแจ้ง "มีฉบับใหม่" — ไม่บล็อกการใช้งาน)</summary>
    Task<LegalAcceptanceStatus> GetAcceptanceStatusAsync(Guid userId, CancellationToken ct = default);

    /// <summary>บันทึก "รับทราบและยอมรับ" ฉบับปัจจุบันเป็น PdpaConsentRecord (hash หลักฐานตัวเดียวกับตอนสมัคร)</summary>
    Task<LegalAcceptanceStatus> RecordAcceptanceAsync(Guid userId, string? versionShown, string? ip, string? userAgent, CancellationToken ct = default);
}

/// <summary>สถานะการยอมรับของผู้ใช้ — <c>NeedsAcknowledge</c> ตัดสินที่ <c>LegalDocumentPolicy.NeedsAcknowledge</c></summary>
public sealed record LegalAcceptanceStatus(
    string CurrentConsentVersion, string? AcceptedVersion, DateTime? AcceptedAt, bool NeedsAcknowledge,
    string PrivacyVersion, string TermsVersion, string? PrivacyChangeSummary, string? TermsChangeSummary);
