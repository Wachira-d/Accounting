namespace Accounting.Services.Interfaces;

/// <summary>ลิงก์ที่เพิ่งออก — <paramref name="Url"/> มี token ดิบ (แสดง/ส่งครั้งเดียว · ฐานเก็บเฉพาะ hash) · null = ยังไม่ได้ตั้งที่อยู่เว็บของระบบ</summary>
public sealed record WhtCertUploadLinkIssued(Guid Id, string? Url, DateTime ExpiresAt, string? Problem);

/// <summary>แถวลิงก์บนหน้าทะเบียน (ไม่มี token — คัดลอกซ้ำไม่ได้ ต้องออกใหม่)</summary>
public sealed record WhtCertUploadLinkDto(Guid Id, Guid ContactId, Guid? DocumentId, string Source, DateTime CreatedAt, DateTime ExpiresAt,
    DateTime? RevokedAt, DateTime? LastUsedAt, int UploadCount, bool Usable);

/// <summary>หนึ่งรายการที่รอใบของลูกค้าบนหน้าอัปโหลด — เฉพาะของลูกค้าที่ถือลิงก์ (ไม่มีข้อมูลรายอื่น)</summary>
public sealed record WhtUploadItemDto(Guid DocumentId, string? DocumentNumber, DateTime? WithheldSince, decimal Missing, string Label, bool Submitted, bool Preselected);

/// <summary>หน้าอัปโหลดสาธารณะ — <paramref name="Usable"/> false ⇒ เฉพาะ <paramref name="Message"/> (ไม่เปิดเผยอะไรของลูกค้า/เอกสาร) ·
/// ข้อมูลบริษัทเรา = ข้อมูลที่ต้องพิมพ์บนใบอยู่แล้ว (ให้ลูกค้าออกใบถูกนาม)</summary>
public sealed record WhtUploadPublicView(bool Usable, string? Message, string? CompanyName, string? CompanyTaxId, string? CompanyBranchLabel,
    string? CompanyAddress, string? ContactName, IReadOnlyList<WhtUploadItemDto> Items, bool AllReceived);

/// <summary>ผลการส่งไฟล์ (ข้อความคงที่จากสถานะ — ไม่สะท้อนผลจับคู่/ชื่อรายอื่น) · <paramref name="Duplicate"/> = ไฟล์นี้เคยส่งแล้ว</summary>
public sealed record WhtUploadPublicResult(bool Received, bool Duplicate, string Status, string Message);

/// <summary>
/// รอบ 253 (WP-10 · AUTO_SEND_WHT_DESIGN §3.2 ช่อง ① · §6.5) — ลิงก์อัปโหลดหนังสือรับรอง 50 ทวิ แบบไม่ต้องล็อกอิน · กติกาที่ <c>Helpers/WhtCertUploadLinkPolicy</c> ·
/// ไฟล์เข้าประตูเดียว <see cref="IWhtCertIntakeService"/> (Channel = UploadLink) · ทุก query ผูก CompanyId
/// </summary>
public interface IWhtCertUploadLinkService
{
    /// <summary>ออกลิงก์ใหม่ (ปุ่ม "คัดลอกลิงก์อัปโหลด") — <paramref name="documentId"/> มีค่า ⇒ ผู้ติดต่อของเอกสารนั้น (ต้องตรงกับ <paramref name="contactId"/> ถ้าส่งมา)</summary>
    Task<WhtCertUploadLinkIssued> IssueAsync(Guid companyId, Guid? contactId, Guid? documentId, Guid? userId, string actor, CancellationToken ct = default);

    Task<List<WhtCertUploadLinkDto>> ListAsync(Guid companyId, Guid contactId, CancellationToken ct = default);

    Task RevokeAsync(Guid companyId, Guid linkId, string actor, CancellationToken ct = default);

    /// <summary>null = ไม่พบ token ของบริษัทนี้ (ข้อความเดียวกับ token ผิด)</summary>
    Task<WhtUploadPublicView?> PublicViewAsync(Guid companyId, string token, CancellationToken ct = default);

    /// <summary>ลูกค้าส่งไฟล์ — null = ไม่พบ token · ลิงก์ใช้ไม่ได้/ไฟล์ไม่ผ่าน ⇒ <c>BusinessRuleException</c> (ข้อความไทย) ·
    /// <paramref name="documentIds"/> = เอกสารที่ลูกค้าติ๊ก (กรองให้อยู่ในชุดของลิงก์)</summary>
    Task<WhtUploadPublicResult?> PublicUploadAsync(Guid companyId, string token, byte[] content, IReadOnlyList<Guid>? documentIds, string? senderName,
        CancellationToken ct = default);

    /// <summary>รอบ 267 P4 (267-1-48): ลูกค้าส่ง 50 ทวิ จาก<b>หน้าเอกสาร</b> (สิทธิ์ = token ของใบ — ผู้เรียกพิสูจน์แล้ว) — แกนเดียวกับ
    /// <see cref="PublicUploadAsync"/> (ประตูเดียว · ช่องทาง UploadLink) · ใบที่ไม่ได้รอใบ/ส่งแล้วรอตรวจ ⇒ <c>BusinessRuleException</c> 409</summary>
    Task<WhtUploadPublicResult> UploadForDocumentAsync(Guid companyId, Guid contactId, Guid documentId, byte[] content, string? senderName,
        CancellationToken ct = default);
}
