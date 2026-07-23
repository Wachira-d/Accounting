using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

/// <summary>หน้านำส่งภาษี/ประกันสังคมรวม — สปส.1-10 + ภงด.1/3/53 + ภพ.30.
/// แสดงยอดรอนำส่งทุกประเภทในที่เดียว พร้อมกำหนดชำระ/เลยกำหนด + ทำจ่าย
/// (post JE ล้างหนี้ค้างจ่าย / Cr ธนาคาร + แนบใบเสร็จ).</summary>
public interface IStatutoryRemittanceService
{
    /// <summary>คำนวณรายการรอนำส่งทุกประเภท ย้อนหลัง N เดือน (default 12) +
    /// ประวัติที่นำส่งล่าสุด. ยอดค้าง = หนี้ที่ตั้งไว้ − ที่นำส่งแล้ว.</summary>
    Task<RemittanceDashboardResponse> GetDashboardAsync(Guid companyId, int monthsBack = 12);

    /// <summary>นำส่ง 1 งวด — post JE (Dr หนี้ค้างจ่าย [+ เงินเพิ่ม] / Cr ธนาคาร),
    /// บันทึก StatutoryRemittance, อัปเดต PayrollRun.SsoSettledAt ถ้าเป็น SSO
    /// ที่มีรอบผูก. idempotent ต่อ (Type, งวด) — นำส่งซ้ำงวดเดิม block.</summary>
    Task<RemitResult> RemitAsync(Guid companyId, RemitRequest request, string performedBy);

    /// <summary>ประมาณการยอด + เงินเพิ่มของงวดหนึ่งก่อนกดจ่าย (ให้ UI preview).</summary>
    Task<PendingRemittanceItem?> PreviewAsync(Guid companyId, string remittanceType,
        int periodYear, int periodMonth, DateTime payDate);

    /// <summary>ผูกไฟล์ใบเสร็จ/หลักฐาน (FileAttachment ที่อัปโหลดแล้ว) กับการนำส่ง.</summary>
    Task AttachReceiptAsync(Guid companyId, Guid remittanceId, Guid attachmentId);

    /// <summary>รับรู้ภาษีซื้อ ภ.พ.36 หลังได้ใบเสร็จ RD (Dr 11610 / Cr 11640 +
    /// stamp เอกสาร → เข้า ภ.พ.30 เดือนที่รับรู้) — ต้องนำส่งงวดนั้นก่อน</summary>
    Task<RemitResult> RecognizePp36InputVatAsync(Guid companyId, int periodYear,
        int periodMonth, DateTime recognizeDate, string performedBy);
}
