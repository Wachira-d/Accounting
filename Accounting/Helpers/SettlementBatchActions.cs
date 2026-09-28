using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ปุ่มไหนของรอบโอนกดได้ตอนนี้ — หน้าเว็บ<b>แสดง</b>ตามนี้ (ไม่ตัดสินสถานะเอง · CLAUDE.md F2 ข้อ 5)</summary>
/// <param name="CanEditLines">จัดประเภท · จับคู่ใบขาย · จับคู่ใหม่ทั้งรอบ</param>
/// <param name="CanVoid">ยกเลิกรอบ (soft-delete · นำเข้าไฟล์เดิมใหม่ได้)</param>
/// <param name="CanPost">เปิดพรีวิว/ลงบัญชีได้ — <b>ตัวตัดสินจริงคือ <c>SettlementPostingPlan.CanPost</c></b> ของพรีวิว (ปุ่มลงบัญชีรอผลพรีวิวเสมอ) ·
/// รอบที่ลงค้างครึ่งทางยังกดได้ (ลงต่อจากที่ค้าง)</param>
/// <param name="CanUnpost">ยกเลิกการลงบัญชี (ด่านภาษี/e-Tax/50 ทวิ ก่อน → ยกเลิกเอกสาร → รับชำระ → ถอนจับคู่ธนาคาร → กลับรายการ JE)</param>
/// <param name="CanBankMatch">จับคู่ JE รอบโอนกับรายการเดินบัญชีจริง</param>
/// <param name="ResolvableChargebackLineIds">บรรทัด chargeback ที่ปิดผลได้ (แพ้/ชนะ) — ลงบัญชีแล้วเท่านั้น</param>
/// <param name="LockedReason">ทำไมแก้บรรทัด/ยกเลิกรอบไม่ได้ (ห้าม silent no-op — ปุ่มที่ถูกล็อกต้องบอกเหตุผล)</param>
/// <param name="UnpostBlockedReason">ลงบัญชีแล้วแต่ยกเลิกการลงบัญชีไม่ได้เพราะอะไร (ด่าน <see cref="SettlementUnpostGate"/> ตัวเดียวกับ service) — ปุ่มถูกซ่อนต้องบอกเหตุผล</param>
public sealed record SettlementBatchActionSet(
    bool CanEditLines,
    bool CanVoid,
    bool CanPost,
    bool CanUnpost,
    bool CanBankMatch,
    IReadOnlyList<Guid> ResolvableChargebackLineIds,
    string? LockedReason,
    string? UnpostBlockedReason = null);

/// <summary>
/// **ปุ่มของรอบโอนตามสถานะ — ตัวตัดสินตัวเดียว** (รอบ 198 เฟส 1 ทีม D · ต่อสายกับด่านของทีม S3)
///
/// <para>ใช้ตัวตัดสิน<b>ตัวเดียวกับ service</b> (ห้ามคิดเกณฑ์ใหม่ · ห้าม drift): แก้บรรทัด/ยกเลิก = <see cref="SettlementSaleMatch.IsEditable(SettlementBatchStatus, int)"/>
/// (ตัวเดียวกับ <c>LoadEditableBatchAsync</c> — รอบที่ลงค้างครึ่งทางแก้/ยกเลิกไม่ได้ · C-1) · ลงบัญชี = <see cref="SettlementSaleMatch.IsEditable(SettlementBatchStatus)"/>
/// (ลงต่อจากที่ค้างได้) · ยกเลิกการลงบัญชี = <c>Posted</c>/<c>BankMatched</c> <b>และ</b> ผลของ <see cref="SettlementUnpostGate"/> ว่าง
/// (ข้อเท็จจริงจาก <c>ISettlementPostingService.UnpostBlockersAsync</c> — ตัวโหลดเดียวกับ <c>UnpostAsync</c> · C-2) · ปิด chargeback = ลงบัญชีแล้ว ·
/// จับคู่ธนาคาร = <c>Posted</c> (ตรงกับ <see cref="SettlementBankMatch.Check"/>) — ปุ่มที่หน้าเว็บซ่อนตามนี้ยังถูกด่านของ service ตรวจซ้ำเสมอ
/// (ปุ่มคือความสะดวก ไม่ใช่ด่าน)</para>
/// <para><paramref name="postingArtifacts"/>/<paramref name="unpostRefusals"/> = <c>null</c> ⇒ "ไม่ได้ตรวจ" (ไม่ใช่ 0/ว่าง) ⇒ ตัดสินจากสถานะอย่างเดียว
/// แล้วให้ service ตรวจตอนกด (F2 ข้อ 5)</para>
///
/// <para>G6: pure</para>
/// </summary>
public static class SettlementBatchActions
{
    public static SettlementBatchActionSet For(SettlementBatchStatus status,
        IEnumerable<(Guid LineId, SettlementLineType LineType)> lines,
        int? postingArtifacts = null,
        IReadOnlyList<SettlementUnpostRefusal>? unpostRefusals = null)
    {
        var editable = postingArtifacts is int n ? SettlementSaleMatch.IsEditable(status, n) : SettlementSaleMatch.IsEditable(status);
        var halfPosted = SettlementSaleMatch.IsEditable(status) && !editable;
        var posted = status is SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched;
        var unpostBlocked = posted && unpostRefusals is { Count: > 0 };
        var chargebacks = posted
            ? lines.Where(l => l.LineType == SettlementLineType.Chargeback).Select(l => l.LineId).ToList()
            : new List<Guid>();
        string? locked = status switch
        {
            SettlementBatchStatus.Voided => "รอบโอนนี้ยกเลิกแล้ว — นำเข้าไฟล์ใหม่ถ้าต้องการบันทึกรอบนี้อีกครั้ง",
            SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched =>
                "รอบโอนนี้ลงบัญชีแล้ว — แก้บรรทัด/ยกเลิกรอบไม่ได้ · กด \"ยกเลิกการลงบัญชี\" ก่อน แล้วแก้แล้วลงใหม่",
            _ when halfPosted =>
                $"รอบโอนนี้ลงบัญชีค้างครึ่งทาง (มีเอกสาร/การรับชำระที่การลงบัญชีสร้างแล้ว {postingArtifacts} รายการ) — แก้บรรทัด/ยกเลิกรอบไม่ได้ · "
                + "กด \"ดูตัวอย่างการลงบัญชี\" แล้วลงบัญชีต่อให้ครบ หรือยกเลิกเอกสาร/การรับชำระเหล่านั้นที่หน้าเอกสารก่อน",
            _ => null,
        };
        return new SettlementBatchActionSet(
            CanEditLines: editable,
            CanVoid: editable,
            CanPost: SettlementSaleMatch.IsEditable(status),
            CanUnpost: posted && !unpostBlocked,
            CanBankMatch: status == SettlementBatchStatus.Posted,
            ResolvableChargebackLineIds: chargebacks,
            LockedReason: locked,
            UnpostBlockedReason: unpostBlocked
                ? "ยกเลิกการลงบัญชีไม่ได้: " + string.Join(" · ", unpostRefusals!.Select(r => $"{r.Subject}: {r.Reason}"))
                  + " — " + unpostRefusals![0].NextStep
                : null);
    }
}
