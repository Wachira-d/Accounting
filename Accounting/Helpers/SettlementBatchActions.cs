using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ปุ่มไหนของรอบโอนกดได้ตอนนี้ — หน้าเว็บ<b>แสดง</b>ตามนี้ (ไม่ตัดสินสถานะเอง · CLAUDE.md F2 ข้อ 5)</summary>
/// <param name="CanEditLines">จัดประเภท · จับคู่ใบขาย · จับคู่ใหม่ทั้งรอบ</param>
/// <param name="CanVoid">ยกเลิกรอบ (soft-delete · นำเข้าไฟล์เดิมใหม่ได้)</param>
/// <param name="CanPost">เปิดพรีวิว/ลงบัญชีได้ — <b>ตัวตัดสินจริงคือ <c>SettlementPostingPlan.CanPost</c></b> ของพรีวิว (ปุ่มลงบัญชีรอผลพรีวิวเสมอ)</param>
/// <param name="CanUnpost">ยกเลิกการลงบัญชี (ถอนจับคู่ธนาคาร → ยกเลิกเอกสาร → กลับรายการ JE)</param>
/// <param name="CanBankMatch">จับคู่ JE รอบโอนกับรายการเดินบัญชีจริง</param>
/// <param name="ResolvableChargebackLineIds">บรรทัด chargeback ที่ปิดผลได้ (แพ้/ชนะ) — ลงบัญชีแล้วเท่านั้น</param>
/// <param name="LockedReason">ทำไมแก้บรรทัด/ยกเลิกรอบไม่ได้ (ห้าม silent no-op — ปุ่มที่ถูกล็อกต้องบอกเหตุผล)</param>
public sealed record SettlementBatchActionSet(
    bool CanEditLines,
    bool CanVoid,
    bool CanPost,
    bool CanUnpost,
    bool CanBankMatch,
    IReadOnlyList<Guid> ResolvableChargebackLineIds,
    string? LockedReason);

/// <summary>
/// **ปุ่มของรอบโอนตามสถานะ — ตัวตัดสินตัวเดียว** (รอบ 198 เฟส 1 ทีม D)
///
/// <para>สอดคล้องกับด่านของ service ทุกตัว (ห้ามคิดเกณฑ์ใหม่): แก้บรรทัด/ยกเลิก = <see cref="SettlementSaleMatch.IsEditable"/> (ตัวเดียวกับ
/// <c>LoadEditableBatchAsync</c>) · ยกเลิกการลงบัญชี/ปิด chargeback = <c>Posted</c> หรือ <c>BankMatched</c> (ตรงกับ <c>UnpostCoreAsync</c> ·
/// <c>ResolveChargebackAsync</c>) · จับคู่ธนาคาร = <c>Posted</c> (ตรงกับ <see cref="SettlementBankMatch.Check"/>) — ปุ่มที่หน้าเว็บซ่อนตามนี้
/// ยังถูกด่านของ service ตรวจซ้ำเสมอ (ปุ่มคือความสะดวก ไม่ใช่ด่าน)</para>
///
/// <para>G6: pure</para>
/// </summary>
public static class SettlementBatchActions
{
    public static SettlementBatchActionSet For(SettlementBatchStatus status,
        IEnumerable<(Guid LineId, SettlementLineType LineType)> lines)
    {
        var editable = SettlementSaleMatch.IsEditable(status);
        var posted = status is SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched;
        var chargebacks = posted
            ? lines.Where(l => l.LineType == SettlementLineType.Chargeback).Select(l => l.LineId).ToList()
            : new List<Guid>();
        string? locked = status switch
        {
            SettlementBatchStatus.Voided => "รอบโอนนี้ยกเลิกแล้ว — นำเข้าไฟล์ใหม่ถ้าต้องการบันทึกรอบนี้อีกครั้ง",
            SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched =>
                "รอบโอนนี้ลงบัญชีแล้ว — แก้บรรทัด/ยกเลิกรอบไม่ได้ · กด \"ยกเลิกการลงบัญชี\" ก่อน แล้วแก้แล้วลงใหม่",
            _ => null,
        };
        return new SettlementBatchActionSet(
            CanEditLines: editable,
            CanVoid: editable,
            CanPost: editable,
            CanUnpost: posted,
            CanBankMatch: status == SettlementBatchStatus.Posted,
            ResolvableChargebackLineIds: chargebacks,
            LockedReason: locked);
    }
}
