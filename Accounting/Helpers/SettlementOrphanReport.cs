namespace Accounting.Helpers;

/// <summary>ของกำพร้า 1 รายการในรายงานระดับช่องทาง (รอบ 201 ทีม ST · A-ST4)</summary>
/// <param name="Item">รายการจากตัวแยกของกำพร้าตัวเดียวกับด่านลงบัญชี (กอง · เหตุ · ผู้/เวลา/เหตุผลที่รับรู้)</param>
/// <param name="Amount">ยอดของเอกสาร/การรับชำระ · null = ซ่อนตามสิทธิ์ (D-P5) หรือหาไม่เจอ</param>
/// <param name="ClearingEffect">ผลต่อผังพักของช่องทาง (บวก = Dr ค้างเพิ่ม — ใบขายสรุป/การรับชำระ · ลบ = Cr — ใบค่าธรรมเนียมที่จ่ายจากผังพัก) · null = ซ่อน/ไม่รู้</param>
/// <param name="ClearingEffectLabel">คำอธิบายผลต่อผังพัก (server computes · page displays)</param>
public sealed record SettlementChannelOrphanRow(SettlementOrphanItem Item, decimal? Amount, decimal? ClearingEffect, string ClearingEffectLabel);

/// <summary>รายงานของกำพร้าระดับช่องทาง (A-ST4 · team-SF V2-P2 · team-V2 คำถามค้าง 5)</summary>
/// <param name="AcknowledgedClearingTotal">Σ ผลต่อผังพักของรายการที่รับรู้แล้ว (การรับรู้มีผล) — ยอดที่ค้างในผังพักโดยไม่มี JE รอบโอนล้าง · null = ซ่อน</param>
/// <param name="BlockingCount">จำนวนรายการที่ยังบล็อกการลงบัญชีของช่องทาง (ทุกกองที่การรับรู้ไม่มีผล)</param>
/// <param name="AmountsHiddenReason">เหตุที่ซ่อนยอด (ผู้มีแค่สิทธิ์ดู · D-P5) · null = แสดงยอด</param>
public sealed record SettlementChannelOrphanReport(Guid ChannelId, string ChannelName, IReadOnlyList<SettlementChannelOrphanRow> Rows,
    decimal? AcknowledgedClearingTotal, int BlockingCount, string? AmountsHiddenReason);

/// <summary>สถานะการรับรู้ของกำพร้าของเอกสาร 1 ใบ (รอบ 201 ฝ่ายค้าน ST-X4) — ตัวตัดสินเดียวกับพรีวิว (<see cref="SettlementOrphanItem.AckEffective"/> ·
/// <see cref="SettlementOrphanItem.AckStatusLabel"/>) · หน้าเอกสารแสดงอย่างเดียว</summary>
/// <param name="Effective">การรับรู้ยังมีผล (ไม่บล็อกการลงบัญชีของช่องทาง)</param>
/// <param name="Label">ป้ายสถานะ (server computes · page displays)</param>
/// <param name="NextStep">ทางไปต่อเมื่อการรับรู้ไม่มีผล · null = ไม่มี</param>
public sealed record SettlementOrphanAckStatus(bool Effective, string Label, string? NextStep);

/// <summary>
/// **รายงานของกำพร้าระดับช่องทาง — เห็นยอดค้างผังพักของใบกำพร้าที่รับรู้แล้ว** (รอบ 201 ทีม ST · A-ST4) · เดิมเห็นได้เฉพาะในพรีวิวของรอบที่กำลังลงบัญชี ⇒
/// หลังรับรู้แล้วไม่มีที่ไหนบอกว่ายอดใดค้างในผังพักโดยไม่มี JE รอบโอนล้าง (รอบเจ้าของถูกยกเลิก) · ตัวแยกกองคือ <see cref="SettlementOrphanTriage"/> ตัวเดียว ·
/// ทิศของผลต่อผังพักตามที่ผู้ลงบัญชีสร้างชิ้นนั้น: ใบขายสรุปรายวัน = เงินเข้าผังพัก (Dr) · การรับชำระ = เงินเข้าผังพัก (Dr) · ใบค่าธรรมเนียม = จ่ายจากผังพัก (Cr) ·
/// ชิ้นที่ไม่รู้ชนิด ⇒ ไม่เดาทิศ (null + บอกว่าไม่รู้) · pure
/// </summary>
public static class SettlementOrphanReport
{
    /// <param name="documents">id เอกสาร → (ยอดรวม · ชิ้นของแผนจาก CreatedBy เช่น <c>sum-…</c>/<c>fee-…</c>)</param>
    /// <param name="payments">id การรับชำระ → ยอด</param>
    /// <param name="amountsHiddenReason">ไม่ null = ซ่อนยอดทุกช่อง (<see cref="SettlementPermissionScope.CandidatesHiddenReason"/>)</param>
    public static SettlementChannelOrphanReport Build(Guid channelId, string channelName, IReadOnlyList<SettlementOrphanItem> items,
        IReadOnlyDictionary<Guid, (decimal Total, string? Component)> documents, IReadOnlyDictionary<Guid, decimal> payments,
        string? amountsHiddenReason)
    {
        var rows = new List<SettlementChannelOrphanRow>();
        foreach (var i in items)
        {
            decimal? amount = null;
            decimal? effect = null;
            string label;
            if (i.IsPayment)
            {
                label = "การรับชำระเข้าผังพัก (Dr ผังพัก)";
                if (payments.TryGetValue(i.Id, out var a)) { amount = a; effect = a; }
            }
            else if (documents.TryGetValue(i.Id, out var d))
            {
                amount = d.Total;
                (effect, label) = EffectOf(d.Component, d.Total);
            }
            else label = "ไม่พบเอกสารในบริษัทนี้ (ถูกลบ?) — ไม่รู้ผลต่อผังพัก";
            if (amountsHiddenReason != null) { amount = null; effect = null; }
            rows.Add(new SettlementChannelOrphanRow(i, amount, effect, label));
        }
        decimal? total = amountsHiddenReason != null ? null
            : Math.Round(rows.Where(r => r.Item.AckEffective && r.ClearingEffect is not null).Sum(r => r.ClearingEffect!.Value), 2,
                MidpointRounding.AwayFromZero);
        return new SettlementChannelOrphanReport(channelId, channelName, rows, total, rows.Count(r => !r.Item.AckEffective), amountsHiddenReason);
    }

    /// <summary>
    /// สถานะการรับรู้ของเอกสารใบหนึ่งจากผลตัวแยก (ST-X4) — ไม่อยู่ในรายการของกำพร้าแล้ว (ถูกยกเลิก/รอบเจ้าของยังไม่ถูกยกเลิก) = การรับรู้ไม่มีผล ·
    /// อยู่ในรายการ ⇒ ป้าย/ผลของรายการนั้นตรงตัว (ตัวเดียวกับพรีวิว) · pure
    /// </summary>
    public static SettlementOrphanAckStatus AckStatusOf(Guid documentId, IReadOnlyList<SettlementOrphanItem>? items)
    {
        var item = (items ?? Array.Empty<SettlementOrphanItem>()).FirstOrDefault(i => !i.IsPayment && i.Id == documentId);
        if (item == null)
            return new SettlementOrphanAckStatus(false, "การรับรู้เดิมไม่มีผล — เอกสารนี้ไม่ใช่ของกำพร้าแล้ว (ถูกยกเลิก หรือรอบโอนเจ้าของไม่ได้ถูกยกเลิก)", null);
        if (item.AckEffective) return new SettlementOrphanAckStatus(true, item.AckStatusLabel ?? "✅ รับรู้แล้ว", null);
        // กองยกเลิกไม่ได้จริงที่การรับรู้ไม่ครอบ ⇒ ตัวแยกคืนรายการแบบ "ยังไม่รับรู้" (Ack = null) + เหตุที่บอกว่าการรับรู้เดิมไม่ครอบ (เหตุเปลี่ยน · รับรู้ก่อนระบบเก็บเหตุ ·
        // รอบใหม่เลขเดิม) — ผู้เรียกถามเฉพาะเอกสารที่มีการรับรู้บนแถว ⇒ Ack = null ที่นี่ = การรับรู้เดิมไม่มีผล
        var label = item.AckStatusLabel ?? (item.Pile == SettlementOrphanPile.Unvoidable
            ? "⚠️ การรับรู้เดิมไม่ครอบเหตุ/รอบปัจจุบัน — ต้องตรวจแล้วรับรู้ใหม่"
            : "⚠️ การรับรู้เดิมไม่มีผล");
        return new SettlementOrphanAckStatus(false, label, item.Why + " · ทางไปต่อ: " + item.NextStep);
    }

    /// <summary>ทิศต่อผังพักของเอกสารจากชิ้นของแผน (<see cref="SettlementPostingKeys.SummaryComponent"/> · <see cref="SettlementPostingKeys.FeeComponent"/>) — ไม่รู้ชิ้น = null</summary>
    internal static (decimal? Effect, string Label) EffectOf(string? component, decimal total)
    {
        if (component != null && component.StartsWith("sum-", StringComparison.Ordinal))
            return (total, "ใบขายสรุปรายวัน — เงินเข้าผังพัก (Dr ผังพัก)");
        if (component != null && component.StartsWith("fee-", StringComparison.Ordinal))
            return (-total, "ใบค่าธรรมเนียม — จ่ายจากผังพัก (Cr ผังพัก)");
        return (null, "ไม่รู้ชนิดชิ้นของรอบโอน — ไม่เดาผลต่อผังพัก");
    }
}
