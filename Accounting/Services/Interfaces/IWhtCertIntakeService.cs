using Accounting.Models.DTOs;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

/// <summary>
/// รอบ 252 (WP-09 · AUTO_SEND_WHT_DESIGN §3.0) — <b>ประตูเดียว</b>ของใบ 50 ทวิ ขาเข้า: ทุกช่องทาง (สแกน · อัปโหลดเอง · ลิงก์ · พอร์ทัล · NextAcc)
/// เข้าทาง <see cref="SubmitAsync"/> · การตั้งเครดิตจากใบ (<c>WhtCreditReceived.ReceivedAmount</c>/สถานะ Received) ผ่านไฟล์นี้เท่านั้น
/// (required_call_site_check R252)
/// </summary>
public interface IWhtCertIntakeService
{
    Task<WhtCertSubmitResult> SubmitAsync(Guid companyId, WhtCertSubmission submission, CancellationToken ct = default);

    /// <summary>ไฟล์นี้ (SHA-256) เคยเข้ากล่องรับของบริษัทนี้แล้วไหม — คืนผลแบบ Duplicate · null = ยังไม่เคย (ผู้เรียกสแกนต่อได้)</summary>
    Task<WhtCertSubmitResult?> FindDuplicateFileAsync(Guid companyId, Guid fileAttachmentId, CancellationToken ct = default);

    /// <summary>รอบ 253 (WP-10): ไฟล์ที่ยังไม่ได้เก็บ (ลิงก์อัปโหลดสาธารณะ) — SHA-256 hex เคยเข้ากล่องรับของบริษัทนี้แล้วไหม (ตัวกันซ้ำเดียวกับ <see cref="SubmitAsync"/>) ·
    /// null = ยังไม่เคย · ใช้ก่อนเก็บไฟล์/สแกน (ไม่เสียโควตา OCR และไม่เก็บไฟล์ซ้ำของลูกค้า)</summary>
    Task<WhtCertSubmitResult?> FindDuplicateShaAsync(Guid companyId, string sha256, CancellationToken ct = default);

    /// <summary>อ่านผลสแกน (OCR pipeline เดิม — มี distillation แล้ว) แล้วเข้า <see cref="SubmitAsync"/> ·
    /// รอบ 253 (WP-10 · N4): <paramref name="evidenceKind"/> = EWithholding เมื่อผู้ใช้บอกว่าเป็นหลักฐาน e-WHT (แนบเป็นใบ 50 ทวิ ธรรมดา — ด่าน/การจับคู่เดียวกันทุกข้อ ·
    /// ไม่เสนออัตรา 1%) · <paramref name="evidenceReference"/> = เลขอ้างอิง e-WHT (ข้อความอิสระ)</summary>
    Task<WhtCertSubmitResult> SubmitFromScanAsync(Guid companyId, Guid scanResultId, WhtCertIntakeChannel channel,
        IReadOnlyList<Guid>? documentHints, string submittedBy, Guid? submittedByUserId, CancellationToken ct = default,
        WhtEvidenceKind evidenceKind = WhtEvidenceKind.PaperCertificate, string? evidenceReference = null, string? fileSha256 = null,
        Guid? expectedPayerContactId = null);

    /// <param name="status">ชื่อ <see cref="WhtCertIntakeRecordStatus"/> · ว่าง = รอยืนยันก่อน แล้วตามด้วยอื่น ๆ</param>
    Task<List<WhtCertIntakeDto>> ListInboxAsync(Guid companyId, string? status, CancellationToken ct = default);

    Task<List<WhtCertCandidateDto>> CandidatesAsync(Guid companyId, Guid intakeId, CancellationToken ct = default);

    /// <param name="journalActor">รอบ 257 ชุด 2 (B-H1): ผู้ถือ Journal.Manage — ใบที่ครอบแถวตัดสูญส่วนที่เหลือแล้ว (ยืนยัน = กลับรายการตัดสูญ) · null ⇒ 403</param>
    Task<WhtCertIntakeDto> ConfirmAsync(Guid companyId, Guid intakeId, WhtCertConfirmRequest request, Guid userId, string userName,
        bool holdsTaxFile, CancellationToken ct = default, Accounting.Services.Implementations.WhtCreditService.WriteOffActor? journalActor = null);

    /// <summary>รอบ 257 ชุด 2 (B-H2): ถอนการยืนยันใบที่ลงเครดิตแล้ว — กลับการจัดสรร · ลดยอดที่ได้ใบ · สถานะใหม่ · audit · แถวที่ใช้เครดิตในแบบที่ยื่นแล้ว (Claimed) ⇒ 409</summary>
    Task<WhtCertIntakeDto> UnconfirmAsync(Guid companyId, Guid intakeId, WhtCertUnconfirmRequest request, Guid userId, string userName,
        CancellationToken ct = default, Accounting.Services.Implementations.WhtCreditService.WriteOffActor? journalActor = null);

    /// <summary>รอบ 257 ชุด 2 (B-L3): OCR ล้ม ⇒ เข้าประตูเดียวแบบรอคนตรวจจากไฟล์ (ไม่มีค่าที่อ่านได้ · ไม่ยืนยันอัตโนมัติ) — ไม่ตอบ error ดิบ ไม่ทิ้งไฟล์</summary>
    Task<WhtCertSubmitResult> SubmitUnreadableAsync(Guid companyId, WhtCertIntakeChannel channel, Guid fileAttachmentId, string? fileSha256,
        IReadOnlyList<Guid>? documentHints, string submittedBy, Guid? submittedByUserId, CancellationToken ct = default,
        Guid? expectedPayerContactId = null, WhtEvidenceKind evidenceKind = WhtEvidenceKind.PaperCertificate, string? evidenceReference = null);

    Task<WhtCertIntakeDto> RejectAsync(Guid companyId, Guid intakeId, WhtCertRejectRequest request, Guid userId, string userName,
        CancellationToken ct = default);

    /// <summary>การ์ด "50 ทวิ ที่ต้องตาม" — ทั้งบริษัท (แดชบอร์ด) หรือรายผู้ติดต่อ</summary>
    /// <param name="taxYear">จำกัดปีภาษี (null = ทุกปี) — โหลดเฉพาะแถวที่ยังต้องตาม</param>
    Task<WhtCertTrackingDto> TrackingAsync(Guid companyId, Guid? contactId, int? taxYear = null, CancellationToken ct = default);

    /// <summary>รายงานปลายปี "50 ทวิ ที่ยังขาด" (AUTO_SEND §4.5) + กระทบ GL 11910</summary>
    Task<WhtCertMissingReportDto> MissingReportAsync(Guid companyId, int taxYear, CancellationToken ct = default);
}
