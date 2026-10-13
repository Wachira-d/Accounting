namespace Accounting.Helpers;

/// <summary>บรรทัดของเอกสาร ณ จุดแบ่งผู้ถูกหัก — ผู้ถูกหักรายบรรทัด (null = ตามหัวเอกสาร) + ภาษีหักของบรรทัด</summary>
public readonly record struct WhtPayeeLine(Guid? LinePayeeContactId, decimal WithholdingTaxAmount);

/// <summary>ส่วนของผู้ถูกหักหนึ่งราย — ภาษีหักรวมของบรรทัดที่เป็นของผู้ถูกหักนี้ + ลำดับบรรทัด (index ในรายการที่ส่งเข้า)</summary>
public sealed record WhtPayeePortion(Guid PayeeContactId, decimal WithholdingTaxAmount, IReadOnlyList<int> LineIndexes);

/// <summary>
/// **ตัวแบ่ง "ใบเดียวหลายผู้ถูกหัก" ตัวเดียว** (คำตัดสินเจ้าของข้อ 148 ⇒ ทำข้อเสนอ 143 · รอบ 204)
///
/// <para>ที่มา: โบรกเกอร์ส่งเบี้ยของบริษัทประกัน 2 แห่งในใบสำคัญจ่ายใบเดียว — ผู้มีเงินได้คนละราย ⇒ 50 ทวิ ต้องแยกใบต่อผู้ถูกหัก
/// (หนึ่ง 50 ทวิ = ผู้ถูกหักหนึ่งราย) · แบบ ภ.ง.ด.3/53/54 และผังหนี้ 21916/21917/21918 ตัดสินต่อผู้ถูกหัก ไม่ใช่ต่อใบ</para>
///
/// <para>กติกา: ผู้ถูกหักของบรรทัด = ค่าบนบรรทัด (<c>DocumentLine.WhtPayeeContactId</c>) ถ้ามี ไม่งั้นผู้ถูกหักของหัวเอกสาร
/// (<see cref="WhtCertIssuePlan.PayeeOf"/>) · แบ่งเฉพาะบรรทัดที่มีภาษีหัก · ใบที่ไม่มีบรรทัดหัก (หักระดับหัวเอกสาร) = ส่วนเดียวของผู้ถูกหักหัวเอกสาร ·
/// <b>ใบผู้ถูกหักรายเดียว ⇒ ส่วนเดียวเสมอ — ทุกเส้น (50 ทวิ · JE · รายงาน) เดินทางเดิมทุกประการ</b></para>
/// </summary>
public static class WhtPayeeSplit
{
    public const string RuleCode = "RD-50BIS-MULTI-PAYEE";

    /// <summary>ผู้ถูกหักของบรรทัด — ค่าบนบรรทัดชนะ · ว่าง = ผู้ถูกหักของหัวเอกสาร</summary>
    public static Guid LinePayeeOf(Guid docContactId, Guid? docPayeeOverride, Guid? linePayee)
        => linePayee is Guid p && p != Guid.Empty ? p : WhtCertIssuePlan.PayeeOf(docContactId, docPayeeOverride);

    /// <summary>ค่าที่ควรเก็บลง <c>DocumentLine.WhtPayeeContactId</c> — ใบที่เราไม่ใช่ผู้หัก/บรรทัดไม่มีอัตราหัก/ว่าง/ตรงกับผู้ถูกหักหัวเอกสาร ⇒ null
    /// (ไม่เก็บสำเนาที่ไม่มีความหมาย · null = ตามหัวเอกสาร)</summary>
    public static Guid? NormalizeLineOverride(Guid docContactId, Guid? docPayeeOverride, Guid? requested,
        bool payerSide, decimal lineWithholdingTaxRate)
    {
        if (!payerSide || lineWithholdingTaxRate <= 0m) return null;
        if (requested is not Guid p || p == Guid.Empty) return null;
        return p == WhtCertIssuePlan.PayeeOf(docContactId, docPayeeOverride) ? null : p;
    }

    /// <summary>เอกสารลูก (แปลง · คัดลอก · รายการประจำ) สืบทอดผู้ถูกหักรายบรรทัด — ปลายทางเป็นฝั่งผู้หัก = ค่าเดิม (null เป็น null) ·
    /// ปลายทางฝั่งอื่น = null (ชนิดอื่นถูกปฏิเสธ — ห้ามพ่วงแล้วทำให้แปลงล้ม)</summary>
    public static Guid? InheritLinePayee(Guid? stored, bool targetIsPayerSide) => targetIsPayerSide ? stored : null;

    /// <summary>ค่าที่ผู้เรียก "ส่งมา" ตอนแก้ไขเอกสาร: null = ไม่ส่ง (คงค่าเดิมของบรรทัดลำดับ+คำอธิบายเดียวกัน — ฟอร์ม/API รุ่นเก่าต้องไม่ล้าง) ·
    /// Guid.Empty = ล้าง (กลับไปตามหัวเอกสาร) · id = ค่าใหม่</summary>
    public static Guid? RequestedOrKept(Guid? requested, IReadOnlyList<(string? Description, Guid? Payee)> previous,
        int index, string? description)
    {
        if (requested.HasValue) return requested.Value == Guid.Empty ? null : requested;
        if (index < 0 || index >= previous.Count) return null;
        var prev = previous[index];
        return string.Equals((prev.Description ?? "").Trim(), (description ?? "").Trim(), StringComparison.Ordinal)
            ? prev.Payee : null;
    }

    /// <summary>แบ่งภาษีหักของใบตามผู้ถูกหัก — ลำดับ: ผู้ถูกหักหัวเอกสารก่อน (ถ้ามีบรรทัดของเขา) แล้วตามลำดับที่พบบนใบ ·
    /// เศษ (ยอดหัวเอกสาร − Σ บรรทัด) ไปที่ส่วนของผู้ถูกหักหัวเอกสาร (ไม่มี = ส่วนแรก) ⇒ Σ ส่วน = ยอดหัวเอกสารเสมอ</summary>
    public static IReadOnlyList<WhtPayeePortion> Portions(Guid docContactId, Guid? docPayeeOverride,
        IReadOnlyList<WhtPayeeLine> lines, decimal docWithholdingTaxAmount)
    {
        var docPayee = WhtCertIssuePlan.PayeeOf(docContactId, docPayeeOverride);
        var order = new List<Guid>();
        var amounts = new Dictionary<Guid, decimal>();
        var idx = new Dictionary<Guid, List<int>>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].WithholdingTaxAmount <= 0m) continue;
            var p = LinePayeeOf(docContactId, docPayeeOverride, lines[i].LinePayeeContactId);
            if (!amounts.ContainsKey(p)) { order.Add(p); amounts[p] = 0m; idx[p] = new List<int>(); }
            amounts[p] += lines[i].WithholdingTaxAmount;
            idx[p].Add(i);
        }
        if (order.Count == 0)
            return docWithholdingTaxAmount > 0m
                ? new[] { new WhtPayeePortion(docPayee, docWithholdingTaxAmount, Array.Empty<int>()) }
                : Array.Empty<WhtPayeePortion>();
        if (order.Remove(docPayee)) order.Insert(0, docPayee);
        var residual = docWithholdingTaxAmount - amounts.Values.Sum();
        if (residual != 0m) amounts[order[0]] += residual;
        return order.Select(p => new WhtPayeePortion(p, amounts[p], idx[p])).ToList();
    }

    /// <summary>ใบนี้มีผู้ถูกหักมากกว่าหนึ่งรายไหม</summary>
    public static bool IsMultiPayee(IReadOnlyList<WhtPayeePortion> portions) => portions.Count > 1;

    /// <summary>เฉลี่ยยอด (เช่น ภาษีหักของ "งวดจ่ายนี้" หรือยอด WHT ที่ JE ลง) ไปตามสัดส่วนของแต่ละผู้ถูกหัก ·
    /// ยอดเท่ายอดรวมของส่วน = คืนยอดของแต่ละส่วนตรง ๆ · อื่น ๆ ปัดสองตำแหน่ง (AwayFromZero) แล้วส่วนสุดท้ายรับเศษ ⇒ Σ = ยอดที่ขอเป๊ะ</summary>
    public static IReadOnlyList<(Guid PayeeContactId, decimal Amount)> Allocate(IReadOnlyList<WhtPayeePortion> portions, decimal amount)
    {
        if (portions.Count == 0) return Array.Empty<(Guid, decimal)>();
        if (portions.Count == 1) return new[] { (portions[0].PayeeContactId, amount) };
        var total = portions.Sum(p => p.WithholdingTaxAmount);
        if (total == amount) return portions.Select(p => (p.PayeeContactId, p.WithholdingTaxAmount)).ToList();
        var result = new List<(Guid, decimal)>(portions.Count);
        var allocated = 0m;
        for (var i = 0; i < portions.Count; i++)
        {
            decimal share;
            if (i == portions.Count - 1) share = amount - allocated;
            else
            {
                share = total == 0m ? 0m
                    : Math.Round(amount * portions[i].WithholdingTaxAmount / total, 2, MidpointRounding.AwayFromZero);
                allocated += share;
            }
            result.Add((portions[i].PayeeContactId, share));
        }
        return result;
    }

    /// <summary>ผู้ถูกหักที่ยังไม่มี 50 ทวิ (ใบที่ยังไม่ยกเลิก) — ใบรายเดียว: มีใบใดก็ตาม = ครบ (พฤติกรรมเดิม · ใบเก่าที่ออกในนามอื่นด้วยมือ
    /// ไม่ถูกนับว่าขาด) · ใบหลายราย: ตรวจต่อผู้ถูกหัก</summary>
    public static IReadOnlyList<Guid> UncoveredPayees(IReadOnlyList<WhtPayeePortion> portions, IReadOnlyCollection<Guid> activeCertPayees)
    {
        if (portions.Count <= 1)
            return activeCertPayees.Count > 0 ? Array.Empty<Guid>() : portions.Select(p => p.PayeeContactId).ToList();
        var have = activeCertPayees.ToHashSet();
        return portions.Where(p => !have.Contains(p.PayeeContactId)).Select(p => p.PayeeContactId).ToList();
    }
}
