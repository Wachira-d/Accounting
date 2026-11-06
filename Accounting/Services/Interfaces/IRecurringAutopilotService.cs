using Accounting.Models.DTOs.Recurring;

namespace Accounting.Services.Interfaces;

/// <summary>
/// รอบ 248 — รายการประจำแบบ Autopilot (AUTOPILOT_BILLING_DESIGN §4.3–§4.5 · §5.1): ตรวจความพร้อม · ทดลองรัน (ไม่เขียนอะไรเลย) ·
/// นโยบายอนุมัติข้ามคำเตือน (ต่อรายการ/ต่อผู้ติดต่อ) · ช่องทางรับเงินต่อผู้ติดต่อ · ตัวตัดสินอยู่ใน <c>Helpers/AutopilotReadiness</c> ·
/// <c>Helpers/AutoApprovePolicy</c> · <c>Helpers/AutopilotSettingsResolver</c>
/// </summary>
public interface IRecurringAutopilotService
{
    /// <summary>ตัวเลือกที่หน้าแสดง (โหมดรับเงิน · ช่องทางที่บริษัทเปิด · คำเตือนที่ปล่อยได้/ไม่ได้)</summary>
    Task<AutopilotOptionsResponse> OptionsAsync(Guid companyId, CancellationToken ct = default);

    /// <summary>ความพร้อมของรายการที่บันทึกแล้ว — อ่านอย่างเดียว</summary>
    Task<AutopilotReadinessResponse> ReadinessAsync(Guid companyId, Guid recurringId, CancellationToken ct = default);

    /// <summary>ความพร้อมของร่างในฟอร์ม (ยังไม่บันทึก) — อ่านอย่างเดียว · <paramref name="recurringId"/> = รายการที่กำลังแก้ (ใช้นโยบายที่ตั้งไว้) หรือ null</summary>
    Task<AutopilotReadinessResponse> ReadinessForDraftAsync(Guid companyId, CreateRecurringTransactionRequest draft, Guid? recurringId, CancellationToken ct = default);

    /// <summary>ทดลองรันรอบถัดไป — <b>ไม่เขียนอะไรเลย</b> (ไม่ออกเลข · ไม่สร้างลิงก์ · ไม่ส่ง · ไม่จองโควตา)</summary>
    Task<RecurringDryRunResponse> DryRunAsync(Guid companyId, Guid recurringId, CancellationToken ct = default);

    /// <summary>ทดลองรันร่างในฟอร์ม — ไม่เขียนอะไรเลย</summary>
    Task<RecurringDryRunResponse> DryRunDraftAsync(Guid companyId, CreateRecurringTransactionRequest draft, Guid? recurringId, CancellationToken ct = default);

    /// <summary>รอบ 251 (WP-07 ข้อ 1): ระดับ Autopilot ของหลายแถว (ตารางรายการประจำ) — อ่านอย่างเดียว · ตัวตัดสินเดียวกับแผงความพร้อม ·
    /// ข้ามแถวที่เสร็จสิ้น · แถวที่ตรวจไม่สำเร็จคืน <c>Error</c> (ไม่ทำให้ทั้งตารางล้ม)</summary>
    Task<IReadOnlyList<RecurringAutopilotLevelRow>> LevelsAsync(Guid companyId, IReadOnlyList<Guid> recurringIds, CancellationToken ct = default);

    /// <summary>รอบ 251 (WP-07 ข้อ 4): ส่งอีเมลทดสอบของรอบถัดไปถึง<b>ผู้ใช้ที่กดเท่านั้น</b> (ผู้ส่งจากตัวตัดสินเดียว <c>OnBehalfEmailSender</c>) ·
    /// ไม่นับเพดานอีเมลเอกสาร · ไม่บันทึก <c>DocumentEmailLog</c> · audit ว่าใครกด · ผู้ใช้ไม่มีอีเมล ⇒ 409</summary>
    Task<RecurringSendTestResponse> SendTestAsync(Guid companyId, Guid recurringId, Guid userId, CancellationToken ct = default);

    /// <summary>ตั้ง/ล้างนโยบายของรายการประจำ — ผู้เรียก (controller) ตรวจสิทธิ์อนุมัติเอกสารชนิดนั้นแล้ว · รหัสที่ห้ามปล่อย ⇒ 400 พร้อมเหตุ · audit</summary>
    Task<AutoApprovePolicyView?> SetRecurringPolicyAsync(Guid companyId, Guid recurringId, SetAutoApprovePolicyRequest request, Guid userId, CancellationToken ct = default);

    /// <summary>ค่าตั้ง Autopilot ของผู้ติดต่อ (echo กลับทุกช่อง)</summary>
    Task<ContactAutopilotSettingsResponse> GetContactSettingsAsync(Guid companyId, Guid contactId, CancellationToken ct = default);

    /// <summary>แก้ช่องทางรับเงินของผู้ติดต่อ · audit</summary>
    Task<ContactAutopilotSettingsResponse> UpdateContactPaymentAsync(Guid companyId, Guid contactId, UpdateContactAutopilotRequest request, Guid userId, CancellationToken ct = default);

    /// <summary>ตั้ง/ล้างนโยบายของผู้ติดต่อ — ผู้เรียกตรวจสิทธิ์อนุมัติเอกสารขายแล้ว · audit</summary>
    Task<ContactAutopilotSettingsResponse> SetContactPolicyAsync(Guid companyId, Guid contactId, SetAutoApprovePolicyRequest request, Guid userId, CancellationToken ct = default);
}
