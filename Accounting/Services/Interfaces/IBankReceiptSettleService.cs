using Accounting.Models.DTOs.Autopilot;

namespace Accounting.Services.Interfaces;

/// <summary>รอบ 252 WP-12A — เงินโอนเข้า ⇒ ปิดหนี้ใบค้าง (AUTOPILOT §5) · ผู้ทำ: <c>BankReceiptSettleService</c></summary>
public interface IBankReceiptSettleService
{
    /// <summary>หนึ่งรอบของงานเบื้องหลัง (ทุกบริษัท) — คืนจำนวนบรรทัดที่ประเมิน</summary>
    Task<int> EvaluatePendingAsync(DateTime nowUtc, CancellationToken ct = default);
    /// <summary>ประเมิน + ลงมือหนึ่งบรรทัด (ผู้เรียก: งานเบื้องหลัง · เทสต์)</summary>
    Task EvaluateLineAsync(Guid companyId, Guid lineId, bool companyEnabled, DateTime nowUtc, CancellationToken ct = default);
    /// <summary>คิว "เงินเข้าที่รอยืนยัน" (ข้อเสนอ + ที่ล้ม)</summary>
    Task<IReadOnlyList<BankAutoSettleQueueRowDto>> QueueAsync(Guid companyId, CancellationToken ct = default);
    /// <summary>คนยืนยันข้อเสนอ ⇒ บันทึกรับชำระ (ทางเดียวกับระบบ) · คืนข้อความสำเร็จ</summary>
    Task<string> ConfirmAsync(Guid companyId, Guid lineId, Guid documentId, Guid userId, CancellationToken ct = default);
    /// <summary>คนปฏิเสธข้อเสนอ</summary>
    Task RejectAsync(Guid companyId, Guid lineId, Guid userId, CancellationToken ct = default);
    /// <summary>ชนิดของเอกสาร (บริษัทนี้) — ผู้เรียกตรวจสิทธิ์อนุมัติก่อนยืนยัน (ฝ่ายค้าน M4)</summary>
    Task<Accounting.Models.Enums.DocumentType?> DocumentTypeAsync(Guid companyId, Guid documentId, CancellationToken ct = default);
    /// <summary>ผลจำลองย้อนหลัง (อ่านอย่างเดียว)</summary>
    Task<BankAutoSettleSimulationDto> SimulateAsync(Guid companyId, int days, CancellationToken ct = default);
}
