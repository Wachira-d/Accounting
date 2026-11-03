using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ฝ่ายค้านรอบ 227 S3 — "เงินค่าบริการเข้าแล้วแต่ยังไม่มีใบเสร็จ/ใบกำกับ" ของรายการออนไลน์ของ NextAcc · pure · ตัวเดียวของตารางรายการบนหน้าแอดมิน
/// และปุ่ม "ออกใบเสร็จอีกครั้ง" (<c>AdminPlatformGatewayController.ReissueReceipt</c>)
/// <para>ที่มา: ตัวออกเอกสารไม่โยนเมื่อล้ม (เงินเข้าแล้วต้องให้สิทธิ์ได้เสมอ) ⇒ ใบที่ขาดต้องมองเห็นและกดออกซ้ำได้ ไม่ใช่รอผู้ทำบัญชีไปเจอเอง</para>
/// </summary>
public static class PlatformReceiptGap
{
    /// <summary>null = ไม่ขาด/ไม่เกี่ยว · ข้อความ = เหตุที่ต้องออกใบ</summary>
    public static string? Reason(PaymentSourceKind kind, PaymentIntentStatus status, bool isTestMode, bool hasReceipt)
    {
        if (!PlatformRevenueSource.IsPlatformRevenue(kind) || isTestMode || hasReceipt) return null;
        if (status is not (PaymentIntentStatus.Succeeded or PaymentIntentStatus.PartiallyRefunded)) return null;
        return "เงินเข้าแล้วแต่ยังไม่มีใบเสร็จ/ใบกำกับภาษีในบริษัทผู้ให้บริการ (§86/4) — กด \"ออกใบเสร็จอีกครั้ง\" หลังแก้สาเหตุ (ผังบัญชี/บริษัทผู้ให้บริการ/ผู้ติดต่อ)";
    }

    public const string AuditAction = "platform-receipt-reissue";

    /// <summary>รอบ 228 ฝ่ายค้าน S2: ใบลดหนี้ของการคืนเงินค่าบริการออกอัตโนมัติไม่สำเร็จ (เรื่องค้างบนรายการ) — null = ไม่มี · ทางไปต่อ = ปุ่ม "ออกใบลดหนี้อีกครั้ง"</summary>
    public static string? CreditNoteReason(string? problem, decimal? pendingAmount)
        => string.IsNullOrWhiteSpace(problem) ? null
            : $"คืนเงิน {(pendingAmount ?? 0m):N2} แล้วแต่ยังไม่มีใบลดหนี้ (§86/10): {problem} — กด \"ออกใบลดหนี้อีกครั้ง\"";
}
