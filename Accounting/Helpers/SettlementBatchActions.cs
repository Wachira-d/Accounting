using Accounting.Models.Constants;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ปุ่มไหนของรอบโอนกดได้ตอนนี้ — หน้าเว็บ<b>แสดง</b>ตามนี้ (ไม่ตัดสินสถานะ/สิทธิ์เอง · CLAUDE.md F2 ข้อ 5)</summary>
/// <param name="CanEditLines">จัดประเภท · จับคู่ใบขาย · จับคู่ใหม่ทั้งรอบ · เปลี่ยนบัญชีธนาคารที่รับเงิน</param>
/// <param name="CanVoid">ยกเลิกรอบ (soft-delete · นำเข้าไฟล์เดิมใหม่ได้)</param>
/// <param name="CanPost">เปิดพรีวิวเพื่อลงบัญชี/ลงบัญชีได้ — <b>ตัวตัดสินจริงคือ <c>SettlementPostingPlan.CanPost</c></b> ของพรีวิว (ปุ่มลงบัญชีรอผลพรีวิวเสมอ) ·
/// รอบที่ลงค้างครึ่งทางยังกดได้ (ลงต่อจากที่ค้าง)</param>
/// <param name="CanUnpost">ยกเลิกการลงบัญชี (ด่านภาษี/e-Tax/50 ทวิ ก่อน → ยกเลิกเอกสาร → รับชำระ → ถอนจับคู่ธนาคาร → กลับรายการ JE)</param>
/// <param name="CanBankMatch">จับคู่ JE รอบโอนกับรายการเดินบัญชีจริง</param>
/// <param name="ResolvableChargebackLineIds">บรรทัด chargeback ที่ปิดผลได้ (แพ้/ชนะ) — ลงบัญชีแล้ว · ยังไม่ปิด · ผู้ใช้มีสิทธิ์</param>
/// <param name="LockedReason">ทำไมแก้บรรทัด/ยกเลิกรอบไม่ได้ (ห้าม silent no-op — ปุ่มที่ถูกล็อกต้องบอกเหตุผล)</param>
/// <param name="UnpostBlockedReason">ลงบัญชีแล้วแต่ยกเลิกการลงบัญชีไม่ได้เพราะอะไร (ด่าน <see cref="SettlementUnpostGate"/> ตัวเดียวกับ service) — ปุ่มถูกซ่อนต้องบอกเหตุผล</param>
/// <param name="CanPreview">ดูตัวอย่างการลงบัญชี/เอกสารที่ลงไว้ได้ (อ่านอย่างเดียว · สิทธิ์ดู) — แยกจาก <paramref name="CanPost"/> ที่ต้องมีสิทธิ์ลงบัญชี</param>
/// <param name="ClosedChargebacks">chargeback ที่ปิดผลแล้ว (เลข JE + คำอธิบายที่ service เขียนตอนปิด) — แสดงแทนปุ่มแพ้/ชนะ (review198-D D-04)</param>
/// <param name="PermissionNote">ปุ่มที่สถานะเปิดให้แต่ถูกซ่อนเพราะผู้ใช้ไม่มีสิทธิ์ + สิทธิ์ที่ต้องขอ (review198-D D-06 · ห้ามซ่อนเงียบ)</param>
/// <param name="CanRedecideLines">รอบที่ลงค้างครึ่งทาง: ตัดสินการจับคู่/จัดประเภทบรรทัด<b>ที่ยังไม่มีเอกสาร/การรับชำระ</b>ได้ (review198-S3 S3-1 ·
/// เซิร์ฟเวอร์ปฏิเสธการแก้ที่เปลี่ยนชิ้นที่ออกแล้ว — <see cref="SettlementPartialEdit"/>) · จับคู่ใหม่ทั้งรอบ/เปลี่ยนบัญชีธนาคาร/ยกเลิกรอบยังไม่ได้</param>
public sealed record SettlementBatchActionSet(
    bool CanEditLines,
    bool CanVoid,
    bool CanPost,
    bool CanUnpost,
    bool CanBankMatch,
    IReadOnlyList<Guid> ResolvableChargebackLineIds,
    string? LockedReason,
    string? UnpostBlockedReason = null,
    bool CanPreview = false,
    IReadOnlyList<SettlementClosedChargeback>? ClosedChargebacks = null,
    string? PermissionNote = null,
    bool CanRedecideLines = false);

/// <summary>chargeback 1 บรรทัดที่ปิดผลแล้ว — จาก JE ปิดรายการที่ยังมีผล (<c>ISettlementPostingService.ClosedChargebacksAsync</c>)</summary>
/// <param name="Description">คำอธิบายของ JE ที่ service เขียนตอนปิด ("ชนะ chargeback …" / "แพ้ chargeback …") — บอกว่าปิดด้วยผลไหน</param>
public sealed record SettlementClosedChargeback(Guid LineId, Guid JournalEntryId, string? EntryNumber, string? Description);

/// <summary>สิทธิ์ของผู้ใช้ที่ปุ่มต้องใช้ (review198-D D-06) — <c>null</c> ที่ <see cref="SettlementBatchActions.For"/> = "ไม่ได้ตรวจ" ⇒ ตัดสินจากสถานะอย่างเดียว</summary>
/// <param name="Import">นำเข้า/แก้รอบโอน (<see cref="SettlementPermissionScope.Import"/>)</param>
/// <param name="Post">ลงบัญชี/ยกเลิกการลงบัญชี/จับคู่เงินเข้า/ปิด chargeback (<see cref="SettlementPermissionScope.Post"/>)</param>
/// <param name="JournalManage">บันทึกสมุดรายวัน — service ปิด chargeback ตรวจสิทธิ์นี้ซ้อน (ฝ่ายค้าน C-8)</param>
public sealed record SettlementActionPermissions(bool Import, bool Post, bool JournalManage);

/// <summary>
/// **ปุ่มของรอบโอนตามสถานะ + สิทธิ์ของผู้ใช้ — ตัวตัดสินตัวเดียว** (รอบ 198 เฟส 1 ทีม D · ต่อสายกับด่านของทีม S3 · review198-D D-04/D-06)
///
/// <para>ใช้ตัวตัดสิน<b>ตัวเดียวกับ service</b> (ห้ามคิดเกณฑ์ใหม่ · ห้าม drift): แก้บรรทัด/ยกเลิก = <see cref="SettlementSaleMatch.IsEditable(SettlementBatchStatus, int)"/>
/// (ตัวเดียวกับ <c>LoadEditableBatchAsync</c> — รอบที่ลงค้างครึ่งทางแก้/ยกเลิกไม่ได้ · C-1) · ลงบัญชี = <see cref="SettlementSaleMatch.IsEditable(SettlementBatchStatus)"/>
/// (ลงต่อจากที่ค้างได้) · ยกเลิกการลงบัญชี = <c>Posted</c>/<c>BankMatched</c> <b>และ</b> ผลของ <see cref="SettlementUnpostGate"/> ว่าง
/// (ข้อเท็จจริงจาก <c>ISettlementPostingService.UnpostBlockersAsync</c> — ตัวโหลดเดียวกับ <c>UnpostAsync</c> · C-2) · ปิด chargeback = ลงบัญชีแล้ว
/// <b>และยังไม่ปิด</b> (ข้อเท็จจริงจาก <c>ClosedChargebacksAsync</c> — นิยาม "ปิดไว้แล้ว" ตัวเดียวกับด่านกันลงซ้ำของ service · D-04) ·
/// จับคู่ธนาคาร = <c>Posted</c> (ตรงกับ <see cref="SettlementBankMatch.Check"/>) — แล้ว<b>ตัดด้วยสิทธิ์</b>ของผู้ใช้ (คีย์เดียวกับ <c>[RequirePermission]</c>
/// ของ endpoint · D-06) · ปุ่มที่หน้าเว็บซ่อนตามนี้ยังถูกด่านของ service/controller ตรวจซ้ำเสมอ (ปุ่มคือความสะดวก ไม่ใช่ด่าน)</para>
/// <para><paramref name="postingArtifacts"/>/<paramref name="unpostRefusals"/>/<paramref name="closedChargebacks"/>/<paramref name="permissions"/> = <c>null</c> ⇒
/// "ไม่ได้ตรวจ" (ไม่ใช่ 0/ว่าง/ไม่มีสิทธิ์) ⇒ ตัดสินจากสถานะอย่างเดียว แล้วให้ service ตรวจตอนกด (F2 ข้อ 5)</para>
/// <para>รอบที่ยกเลิกแล้ว (Voided) ถูก soft-delete — รายการ/รายละเอียดไม่คืนรอบนั้นเลย (ตัวกรองสถานะของหน้าเว็บไม่มี "ยกเลิกแล้ว" · D-05) ·
/// กิ่ง Voided ที่นี่คงไว้เป็นทางป้องกัน (ไม่มีปุ่มใด) ไม่ใช่เส้นที่ผู้ใช้เห็น</para>
///
/// <para>G6: pure</para>
/// </summary>
public static class SettlementBatchActions
{
    public static SettlementBatchActionSet For(SettlementBatchStatus status,
        IEnumerable<(Guid LineId, SettlementLineType LineType)> lines,
        int? postingArtifacts = null,
        IReadOnlyList<SettlementUnpostRefusal>? unpostRefusals = null,
        IReadOnlyList<SettlementClosedChargeback>? closedChargebacks = null,
        SettlementActionPermissions? permissions = null)
    {
        var editable = postingArtifacts is int n ? SettlementSaleMatch.IsEditable(status, n) : SettlementSaleMatch.IsEditable(status);
        var halfPosted = SettlementSaleMatch.IsEditable(status) && !editable;
        var posted = status is SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched;
        var unpostBlocked = posted && unpostRefusals is { Count: > 0 };
        var closed = (closedChargebacks ?? Array.Empty<SettlementClosedChargeback>()).Select(c => c.LineId).ToHashSet();
        var openChargebacks = posted
            ? lines.Where(l => l.LineType == SettlementLineType.Chargeback && !closed.Contains(l.LineId)).Select(l => l.LineId).ToList()
            : new List<Guid>();

        // ── สถานะอนุญาตอะไร (ก่อนดูสิทธิ์) ──
        var stEdit = editable;
        var stPost = SettlementSaleMatch.IsEditable(status);
        var stUnpost = posted && !unpostBlocked;
        var stBank = status == SettlementBatchStatus.Posted;
        var stChargeback = openChargebacks.Count > 0;
        // S3-1: ค้างครึ่งทาง ⇒ แก้ได้เฉพาะบรรทัดที่ยังไม่มีชิ้นที่ออกแล้ว (ตัวตัดสินจริง = SettlementPartialEdit ในธุรกรรมของ service)
        var stRedecide = halfPosted;

        // ── ตัดด้วยสิทธิ์ (D-06) — null = ไม่ได้ตรวจ ⇒ ไม่ตัด ──
        var p = permissions ?? new SettlementActionPermissions(true, true, true);
        var canChargeback = p.Post && p.JournalManage;
        var hidden = new List<string>();
        if (stRedecide && !p.Import)
            hidden.Add($"ตัดสินการจับคู่/จัดประเภทบรรทัดที่ยังไม่รับชำระ — ต้องมีสิทธิ์ “{PermissionKeys.LabelOf(SettlementPermissionScope.Import)}”");
        if (stEdit && !p.Import)
            hidden.Add($"แก้บรรทัด/จับคู่/เปลี่ยนบัญชีธนาคาร/ยกเลิกรอบ — ต้องมีสิทธิ์ “{PermissionKeys.LabelOf(SettlementPermissionScope.Import)}”");
        if ((stPost || stUnpost || stBank) && !p.Post)
            hidden.Add($"ลงบัญชี/ยกเลิกการลงบัญชี/จับคู่เงินเข้าธนาคาร — ต้องมีสิทธิ์ “{PermissionKeys.LabelOf(SettlementPermissionScope.Post)}”");
        if (stChargeback && !canChargeback)
            hidden.Add($"ปิดรายการ chargeback — ต้องมีสิทธิ์ “{PermissionKeys.LabelOf(SettlementPermissionScope.Post)}” "
                + $"และ “{PermissionKeys.LabelOf(PermissionKeys.JournalManage)}”");

        string? locked = status switch
        {
            SettlementBatchStatus.Voided => "รอบโอนนี้ยกเลิกแล้ว — นำเข้าไฟล์ใหม่ถ้าต้องการบันทึกรอบนี้อีกครั้ง",
            SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched =>
                "รอบโอนนี้ลงบัญชีแล้ว — แก้บรรทัด/ยกเลิกรอบไม่ได้ · กด \"ยกเลิกการลงบัญชี\" ก่อน แล้วแก้แล้วลงใหม่",
            _ when halfPosted =>
                $"รอบโอนนี้ลงบัญชีค้างครึ่งทาง (มีเอกสาร/การรับชำระที่การลงบัญชีสร้างแล้ว {postingArtifacts} รายการ) — ยกเลิกรอบ/จับคู่ใหม่ทั้งรอบ/"
                + "เปลี่ยนบัญชีธนาคารไม่ได้ · กด \"ดูตัวอย่างการลงบัญชี\" แล้วลงบัญชีต่อให้ครบ · ถ้าด่านบอกให้เลือกใบขายใหม่ (ใบที่จับคู่ไว้ถูกรับชำระ/ยกเลิกไประหว่างนั้น) "
                + "ตัดสินการจับคู่หรือจัดประเภทใหม่ได้เฉพาะบรรทัดที่ยังไม่มีเอกสาร/การรับชำระ (ระบบปฏิเสธถ้าการแก้เปลี่ยนชิ้นที่ออกแล้ว) · "
                + "หรือยกเลิกเอกสาร/การรับชำระเหล่านั้นที่หน้าเอกสารก่อน",
            _ => null,
        };
        return new SettlementBatchActionSet(
            CanEditLines: stEdit && p.Import,
            CanVoid: stEdit && p.Import,
            CanPost: stPost && p.Post,
            CanUnpost: stUnpost && p.Post,
            CanBankMatch: stBank && p.Post,
            ResolvableChargebackLineIds: canChargeback ? openChargebacks : new List<Guid>(),
            LockedReason: locked,
            UnpostBlockedReason: unpostBlocked
                ? "ยกเลิกการลงบัญชีไม่ได้: " + string.Join(" · ", unpostRefusals!.Select(r => $"{r.Subject}: {r.Reason}"))
                  + " — " + unpostRefusals![0].NextStep
                : null,
            CanPreview: status != SettlementBatchStatus.Voided,
            ClosedChargebacks: (closedChargebacks ?? Array.Empty<SettlementClosedChargeback>())
                .Where(c => posted && lines.Any(l => l.LineId == c.LineId)).ToList(),
            CanRedecideLines: stRedecide && p.Import,
            PermissionNote: hidden.Count == 0 ? null
                : "ปุ่มบางปุ่มถูกซ่อนเพราะบัญชีของคุณยังไม่มีสิทธิ์: " + string.Join(" · ", hidden)
                  + " — ขอให้เจ้าของบริษัทเปิดสิทธิ์ที่หน้า “บทบาทและสิทธิ์” (/pages/roles.html)");
    }
}
