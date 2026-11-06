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

    /// <summary>อ่านผลสแกน (OCR pipeline เดิม — มี distillation แล้ว) แล้วเข้า <see cref="SubmitAsync"/></summary>
    Task<WhtCertSubmitResult> SubmitFromScanAsync(Guid companyId, Guid scanResultId, WhtCertIntakeChannel channel,
        IReadOnlyList<Guid>? documentHints, string submittedBy, Guid? submittedByUserId, CancellationToken ct = default);

    /// <param name="status">ชื่อ <see cref="WhtCertIntakeRecordStatus"/> · ว่าง = รอยืนยันก่อน แล้วตามด้วยอื่น ๆ</param>
    Task<List<WhtCertIntakeDto>> ListInboxAsync(Guid companyId, string? status, CancellationToken ct = default);

    Task<List<WhtCertCandidateDto>> CandidatesAsync(Guid companyId, Guid intakeId, CancellationToken ct = default);

    Task<WhtCertIntakeDto> ConfirmAsync(Guid companyId, Guid intakeId, WhtCertConfirmRequest request, Guid userId, string userName,
        bool holdsTaxFile, CancellationToken ct = default);

    Task<WhtCertIntakeDto> RejectAsync(Guid companyId, Guid intakeId, WhtCertRejectRequest request, Guid userId, string userName,
        CancellationToken ct = default);

    /// <summary>การ์ด "50 ทวิ ที่ต้องตาม" — ทั้งบริษัท (แดชบอร์ด) หรือรายผู้ติดต่อ</summary>
    /// <param name="taxYear">จำกัดปีภาษี (null = ทุกปี) — โหลดเฉพาะแถวที่ยังต้องตาม</param>
    Task<WhtCertTrackingDto> TrackingAsync(Guid companyId, Guid? contactId, int? taxYear = null, CancellationToken ct = default);

    /// <summary>รายงานปลายปี "50 ทวิ ที่ยังขาด" (AUTO_SEND §4.5) + กระทบ GL 11910</summary>
    Task<WhtCertMissingReportDto> MissingReportAsync(Guid companyId, int taxYear, CancellationToken ct = default);
}
