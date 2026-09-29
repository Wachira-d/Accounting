using Accounting.Models.DTOs.Settlement;
using Accounting.Models.Enums;

namespace Accounting.Services.Settlement;

/// <summary>
/// **นำเข้า · จัดประเภท · จับคู่ รอบโอน settlement** (รอบ 198 เฟส 1 ทีม B) — <b>ไม่ลงบัญชี</b> (ทีม C: ผู้ลงบัญชีอ่าน batch ที่ตัวนี้สร้าง)
///
/// <para>สัญญากับผู้เรียก (ทีม D · controller): ทุกเมธอดกรอง <c>CompanyId</c> เอง · <b>ด่านสิทธิ์เป็นของผู้เรียก</b> ·
/// ข้อผิดพลาดของผู้ใช้ = <c>BusinessRuleException</c> (ข้อความไทย + ทางไปต่อ · ไม่มีอะไรถูกบันทึกครึ่งทาง)</para>
/// </summary>
public interface ISettlementImportService
{
    /// <summary>ตรวจไฟล์ก่อนนำเข้า — หัวคอลัมน์ · แถวตัวอย่าง (ตัด PII) · การจับคู่ที่จำไว้ใช้ได้ไหม · ข้อเสนอการจับคู่ (ไม่บันทึกอะไร)</summary>
    /// <remarks>ข้อเสนอการจับคู่มาจากชั้น local เท่านั้น (หัวคอลัมน์ที่รู้จักใน adapter) — <b>ไม่ส่งตัวอย่างข้อมูลไป AI</b>:
    /// แถวตัวอย่างมีข้อมูลผู้ซื้อ และ augmenter ของ <c>ImportColumnMatch</c> บอกไม่ได้ว่าคำตอบมาจากครูหรือนักเรียน (ป้าย UsedAi ซื่อสัตย์ไม่ได้)</remarks>
    Task<SettlementFileInspection> InspectFileAsync(Guid companyId, Guid channelId, string fileName, Stream content,
        CancellationToken ct = default);

    /// <summary>นำเข้าไฟล์เป็นรอบโอน (สถานะ Imported/Classified/Matched) — idempotent ต่อ <c>ExternalTxnId</c> ต่อช่องทาง ·
    /// <c>PayoutRef</c> เดิมที่ยังไม่ลงบัญชี ⇒ เติมเฉพาะบรรทัดใหม่ · ไฟล์ต้นฉบับเก็บผ่าน attachment abstraction (ชนิด "SettlementBatch")</summary>
    /// <param name="memoryBlockedReason">เหตุที่การจับคู่ครั้งนี้<b>จำไม่ได้</b> (ไม่มีสิทธิ์ตั้งค่าช่องทาง · คีย์ API — ผู้เรียกตัดสินด่านสิทธิ์) — ข้อความถาม
    /// รูปแบบวันที่/เขตเวลาบอกผู้ใช้ตามจริง (ฝ่ายค้าน I-2) · null = จำได้ หรือผู้ใช้ไม่ได้ติ๊กจำเอง</param>
    Task<SettlementImportResult> ImportFileAsync(Guid companyId, Guid userId, SettlementFileImportRequest request,
        string fileName, Stream content, string? memoryBlockedReason, CancellationToken ct = default);

    /// <summary>ประกอบรอบโอนจาก PaymentIntent ของ gateway ที่ผูกกับช่องทาง (ยังไม่อยู่ในรอบใด + คืนเงินภายหลังของรอบก่อน)</summary>
    Task<SettlementImportResult> ImportFromPaymentIntentsAsync(Guid companyId, Guid userId, SettlementIntentBatchRequest request,
        CancellationToken ct = default);

    /// <summary>รอบโอน 1 รอบพร้อมบรรทัด + ผู้สมัครจับคู่ของบรรทัดที่รอคนตัดสิน</summary>
    Task<SettlementBatchView> GetBatchAsync(Guid companyId, Guid batchId, CancellationToken ct = default);

    /// <summary>รายการรอบโอน (ไม่มีบรรทัด) — ใหม่สุดก่อน</summary>
    Task<IReadOnlyList<SettlementBatchView>> ListBatchesAsync(Guid companyId, Guid? channelId, SettlementBatchStatus? status,
        int skip = 0, int take = 50, CancellationToken ct = default);

    /// <summary>ผู้ใช้เลือก/แก้ประเภทบรรทัด — บันทึกคำตอบลงคลังเรียนรู้ (<c>RecordUserChoiceAsync</c> · Explicit) ในธุรกรรมเดียวกัน</summary>
    Task<SettlementLineView> ReclassifyLineAsync(Guid companyId, Guid userId, Guid lineId, SettlementReclassifyRequest request,
        CancellationToken ct = default);

    /// <summary>ผู้ใช้ตัดสินการจับคู่ของบรรทัดขาย/คืนเงิน (เลือกเอกสาร หรือยืนยันใบขายสรุปรายวัน)</summary>
    Task<SettlementLineView> AssignLineMatchAsync(Guid companyId, Guid lineId, SettlementAssignMatchRequest request,
        CancellationToken ct = default);

    /// <summary>จับคู่ใหม่ทั้งรอบโอน (หลังสร้างเอกสารขายที่ขาด) — ไม่แตะบรรทัดที่ผู้ใช้ตัดสินเองแล้ว</summary>
    Task<SettlementBatchView> RematchBatchAsync(Guid companyId, Guid batchId, CancellationToken ct = default);

    /// <summary>ยกเลิกรอบโอนที่ยังไม่ลงบัญชี — soft-delete รอบ + บรรทัด (นำเข้าไฟล์เดิมใหม่ได้) · ปลด PaymentIntent ออกจากรอบ · audit</summary>
    Task VoidBatchAsync(Guid companyId, Guid userId, Guid batchId, string reason, CancellationToken ct = default);

    /// <summary>เปลี่ยนบัญชีธนาคารที่รับเงินของรอบโอนที่ยังแก้ได้ (ด่านเดียวกับแก้บรรทัด · ล็อกต่อช่องทาง · audit) — review198-D D-03</summary>
    Task<SettlementBatchView> SetBankAccountAsync(Guid companyId, Guid userId, Guid batchId, Guid? bankAccountId,
        CancellationToken ct = default);
}

/// <summary>
/// **ตั้งค่าช่องทางรับเงินผ่าน wallet** (รอบ 198 เฟส 1 ทีม B) — ตรวจ <c>FeeAccountMapJson</c> ด้วยตัวอ่านตัวเดียว · ผู้ติดต่อด้วย
/// <c>ContactTaxBranchKey</c> · ผูก/สร้างผังพักด้วย <c>SettlementChannelAccounts.EnsureClearingAccountAsync</c> ตอนบันทึก ·
/// tenant กรองที่นี่ · ด่านสิทธิ์เป็นของผู้เรียก
/// </summary>
public interface ISettlementChannelService
{
    Task<IReadOnlyList<SettlementChannelView>> ListAsync(Guid companyId, bool includeInactive, CancellationToken ct = default);
    Task<SettlementChannelView> GetAsync(Guid companyId, Guid channelId, CancellationToken ct = default);
    Task<SettlementChannelView> CreateAsync(Guid companyId, Guid userId, SettlementChannelUpsertRequest request, CancellationToken ct = default);
    Task<SettlementChannelView> UpdateAsync(Guid companyId, Guid userId, Guid channelId, SettlementChannelUpsertRequest request,
        CancellationToken ct = default);
}
