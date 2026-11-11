using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Recurring;

namespace Accounting.Services.Interfaces;

public interface IRecurringTransactionService
{
    Task<RecurringTransactionResponse> CreateAsync(Guid companyId, CreateRecurringTransactionRequest request, string createdBy);
    Task<RecurringTransactionResponse> GetByIdAsync(Guid companyId, Guid id);
    Task<PagedResponse<RecurringTransactionResponse>> GetAllAsync(Guid companyId, PagedRequest request, string? status = null);
    /// <param name="editor">รอบ 259 (B-M3): ผู้แก้ + มีสิทธิ์อนุมัติไหม — มีสิทธิ์ ⇒ ค่าเริ่มต้น/ใบรับรองนโยบายอนุมัติอัตโนมัติประทับใหม่ในชื่อผู้แก้ ·
    /// null/ไม่มีสิทธิ์ ⇒ หยุดมีผล + คำเตือนหลังบันทึกบอกว่าใครเปิดคืนได้ (<c>Helpers/RecurringPolicyRestamp</c>)</param>
    Task<RecurringTransactionResponse> UpdateAsync(Guid companyId, Guid id, UpdateRecurringTransactionRequest request,
        Accounting.Helpers.RecurringEditor? editor = null);
    Task DeleteAsync(Guid companyId, Guid id);
    Task<RecurringTransactionResponse> PauseAsync(Guid companyId, Guid id);
    Task<RecurringTransactionResponse> ResumeAsync(Guid companyId, Guid id);
    /// <param name="confirmExtra">รอบ 259 (B-L3): ผู้ใช้ยืนยันออกใบพิเศษก่อนกำหนดเพิ่ม (ใบล่วงหน้าของงวดนี้มีแล้ว — ไม่ยืนยัน = 409 <c>RECURRING-RUN-DUP</c>)</param>
    Task<RecurringTransactionResponse> RunNowAsync(Guid companyId, Guid id, string performedBy, bool confirmExtra = false);
    /// <summary>รอบ 247: ผู้ติดต่อรับอีเมลเอกสารได้ไหม (ฟอร์มรายการประจำ ปิดช่อง "ส่งอีเมล" พร้อมเหตุผล)</summary>
    Task<RecurringEmailReadinessResponse> GetEmailReadinessAsync(Guid companyId, Guid contactId);
    Task ProcessDueRecurringTransactionsAsync(); // Background job
}
