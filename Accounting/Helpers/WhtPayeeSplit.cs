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

    /// <summary>เอกสารลูก (แปลง · คัดลอก · รายการประจำ) สืบทอดผู้ถูกหัก — <b>ทั้งหัวเอกสารและรายบรรทัด ตัวเดียว</b> ·
    /// ปลายทางเป็นฝั่งผู้หัก = ค่าเดิม (null เป็น null) · ปลายทางฝั่งอื่น = null (ชนิดอื่นถูกปฏิเสธ — ห้ามพ่วงแล้วทำให้แปลงล้ม) ·
    /// ฝ่ายค้าน P3-6 (รอบ 204): ผู้ติดต่อที่ถูกลบไปแล้ว (ไม่อยู่ใน <paramref name="activeContacts"/> — <c>ContactHydration.ActiveContactIdsAsync</c>) ⇒ null
    /// (บรรทัด = ตามหัวเอกสาร · หัวเอกสาร = ผู้ติดต่อของเอกสาร) — เดิมพ่วงไปแล้วตัวตรวจตอนสร้างปฏิเสธ ⇒ แปลง/คัดลอก/รายการประจำล้มทุกครั้ง
    /// จนกว่าจะกู้ผู้ติดต่อ · ผู้เรียกต้องติดหมายเหตุ <see cref="DroppedInheritedPayeeNote"/> บนใบใหม่ (ไม่เปลี่ยนผู้ถูกหักเงียบ ๆ)</summary>
    public static Guid? InheritPayee(Guid? stored, bool targetIsPayerSide, IReadOnlySet<Guid> activeContacts)
        => targetIsPayerSide && stored is Guid p && p != Guid.Empty && activeContacts.Contains(p) ? p : null;

    /// <summary>จำนวนผู้ถูกหัก (หัวเอกสาร + รายบรรทัด) ที่ถูกตัดทิ้งตอนสืบทอดเพราะผู้ติดต่อถูกลบ (คู่กับ <see cref="InheritPayee"/>)</summary>
    public static int DroppedInheritedPayees(IEnumerable<Guid?> stored, bool targetIsPayerSide, IReadOnlySet<Guid> activeContacts)
        => !targetIsPayerSide ? 0 : stored.Count(s => s is Guid p && p != Guid.Empty && !activeContacts.Contains(p));

    /// <summary>หมายเหตุภายในของใบใหม่เมื่อผู้ถูกหักของต้นทางถูกลบ — ตรวจก่อนอนุมัติ (ล้มดังที่ตัวเอกสาร ไม่ใช่เปลี่ยนเงียบ) · 0 = null</summary>
    public static string? DroppedInheritedPayeeNote(string? sourceNumber, int dropped)
        => dropped <= 0 ? null
            : $"[{RuleCode}] ผู้ถูกหักภาษีของเอกสารต้นทาง {sourceNumber} {dropped} รายการ (หัวเอกสาร/รายบรรทัด) ถูกลบออกจากผู้ติดต่อแล้ว — "
              + "ใบนี้ใช้ผู้ถูกหักของหัวเอกสาร/ผู้ติดต่อของเอกสารแทน ตรวจ/เลือกผู้ถูกหักใหม่ก่อนอนุมัติ (50 ทวิ ออกในนามผู้ถูกหัก)";

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
    /// ยอดเท่ายอดรวมของส่วน = คืนยอดของแต่ละส่วนตรง ๆ · อื่น ๆ แบ่งเป็นสตางค์ด้วย <b>เศษเหลือมากสุด</b> (largest remainder) ⇒
    /// Σ = ยอดที่ขอเป๊ะ และ<b>ทุกส่วนมีเครื่องหมายเดียวกับยอด</b> (ฝ่ายค้าน P3-5 รอบ 204: เดิม "ส่วนสุดท้ายรับเศษ" ⇒ 4 ส่วนเท่ากัน ยอด 0.02
    /// ได้ 0.01/0.01/0.01/−0.01 = หนี้ WHT ติดลบของผู้ถูกหักรายหนึ่ง) · ใบรายเดียว = ยอดทั้งก้อน (identity)</summary>
    public static IReadOnlyList<(Guid PayeeContactId, decimal Amount)> Allocate(IReadOnlyList<WhtPayeePortion> portions, decimal amount)
    {
        if (portions.Count == 0) return Array.Empty<(Guid, decimal)>();
        if (portions.Count == 1) return new[] { (portions[0].PayeeContactId, amount) };
        var total = portions.Sum(p => p.WithholdingTaxAmount);
        if (total == amount) return portions.Select(p => (p.PayeeContactId, p.WithholdingTaxAmount)).ToList();
        // น้ำหนัก = ภาษีหักของแต่ละส่วน (ส่วนที่ติดลบ/ศูนย์ไม่รับส่วนแบ่ง) · ไม่มีน้ำหนักเลย ⇒ ทั้งก้อนไปส่วนแรก (ผู้ถูกหักหัวเอกสาร — กติกาเศษเดียวกับ Portions)
        var weights = portions.Select(p => Math.Max(p.WithholdingTaxAmount, 0m)).ToList();
        var weightTotal = weights.Sum();
        var result = portions.Select(p => (p.PayeeContactId, Amount: 0m)).ToList();
        if (weightTotal == 0m)
        {
            result[0] = (result[0].PayeeContactId, amount);
            return result;
        }
        var sign = amount < 0m ? -1m : 1m;
        var abs = Math.Abs(amount);
        var satang = Math.Round(abs * 100m, 0, MidpointRounding.AwayFromZero);
        var floors = new decimal[portions.Count];
        var remainders = new decimal[portions.Count];
        for (var i = 0; i < portions.Count; i++)
        {
            var exact = satang * weights[i] / weightTotal;
            floors[i] = Math.Floor(exact);
            remainders[i] = exact - floors[i];
        }
        var left = satang - floors.Sum();
        var byRemainder = Enumerable.Range(0, portions.Count)
            .OrderByDescending(k => remainders[k]).ThenBy(k => k).ToList();
        foreach (var idx in byRemainder)
        {
            if (left <= 0m) break;
            floors[idx] += 1m;
            left -= 1m;
        }
        for (var i = 0; i < portions.Count; i++)
            result[i] = (portions[i].PayeeContactId, sign * floors[i] / 100m);
        // ยอดที่มีทศนิยมเกินสองตำแหน่ง (ไม่ควรเกิดกับเงิน) — เศษใต้สตางค์ไปส่วนที่มากที่สุด ⇒ Σ เป๊ะเสมอ
        var subSatang = amount - result.Sum(r => r.Amount);
        if (subSatang != 0m)
        {
            var biggest = Enumerable.Range(0, result.Count).OrderByDescending(k => Math.Abs(result[k].Amount)).First();
            result[biggest] = (result[biggest].PayeeContactId, result[biggest].Amount + subSatang);
        }
        return result;
    }

    /// <summary>
    /// <b>ผู้ถูกหักหัวเอกสาร "ที่มีผลจริง"</b> (ฝ่ายค้าน P1 รอบ 204) — ทุกบรรทัดที่มีภาษีหักเป็นของผู้ถูกหักรายเดียว Y (ตั้งรายบรรทัดทุกบรรทัด) ⇒
    /// ใบนี้คือ "ใบรายเดียวของ Y" ไม่ใช่ของผู้ถูกหักหัวเอกสาร X · ค่าที่ควรเก็บลง <c>Document.WhtPayeeContactId</c> (normalize แล้ว) ·
    /// ใบหลายผู้ถูกหัก/ไม่มีบรรทัดหัก ⇒ ค่าหัวเอกสารเดิม
    /// <para>ที่มา: <see cref="Portions"/> คืนส่วนเดียวของ Y (JE ลงผังของ Y) แต่ 50 ทวิ/พรีวิว GL/ช่องโหว่หน้านำส่ง อ่านหัวเอกสาร X ⇒ คนละคน</para>
    /// </summary>
    private static Guid? EffectivePayeeOverride(Guid docContactId, Guid? docPayeeOverride, IReadOnlyList<WhtPayeeLine> lines)
    {
        Guid? only = null;
        foreach (var l in lines)
        {
            if (l.WithholdingTaxAmount <= 0m) continue;
            var p = LinePayeeOf(docContactId, docPayeeOverride, l.LinePayeeContactId);
            if (only is Guid o && o != p) return WhtCertIssuePlan.NormalizeOverride(docContactId, docPayeeOverride);
            only = p;
        }
        return WhtCertIssuePlan.NormalizeOverride(docContactId, only ?? docPayeeOverride);
    }

    /// <summary>ผู้ถูกหักของ "ใบรายเดียว" — ตัวอ่านเดียวของทุกเส้นที่ตัดสินผู้ถูกหักทั้งใบ (50 ทวิ · พรีวิว GL · ช่องโหว่นำส่ง · ลิสต์รอออกใบ ·
    /// ตัวโหลดผู้ถูกหัก <c>ContactHydration</c>) — ตรงกับส่วนเดียวที่ <see cref="Portions"/> คืน · ใบหลายผู้ถูกหัก = ผู้ถูกหักหัวเอกสาร (ผู้เรียกแยกต่อรายเอง)</summary>
    public static Guid EffectiveSinglePayee(Guid docContactId, Guid? docPayeeOverride, IReadOnlyList<WhtPayeeLine> lines)
        => WhtCertIssuePlan.PayeeOf(docContactId, EffectivePayeeOverride(docContactId, docPayeeOverride, lines));

    /// <summary>บรรทัดของเอกสาร (ไม่ลบ · ตามลำดับ) ในรูปที่ตัวแบ่งรับ</summary>
    public static IReadOnlyList<WhtPayeeLine> LinesOf(Accounting.Models.Entities.Document doc)
        => doc.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.LineOrder)
            .Select(l => new WhtPayeeLine(l.WhtPayeeContactId, l.WithholdingTaxAmount)).ToList();

    /// <summary><see cref="EffectiveSinglePayee(Guid, Guid?, IReadOnlyList{WhtPayeeLine})"/> ของเอกสาร (ต้องโหลดบรรทัดมาแล้ว — ไม่มีบรรทัด = หัวเอกสาร)</summary>
    public static Guid EffectiveSinglePayee(Accounting.Models.Entities.Document doc)
        => EffectiveSinglePayee(doc.ContactId, doc.WhtPayeeContactId, LinesOf(doc));

    /// <summary>
    /// <b>ยกผู้ถูกหักรายบรรทัดขึ้นหัวเอกสารตอนบันทึก</b> (ฝ่ายค้าน P1 รอบ 204 · ตัวเดียวของ สร้าง/แก้ไข/เปลี่ยนผู้ถูกหักหลังอนุมัติ) —
    /// ทุกบรรทัดที่หักเป็นของ Y ≠ หัวเอกสาร ⇒ หัวเอกสาร = Y และบรรทัด = null (ข้อมูลที่เก็บไม่มีรูป "หัว X แต่ทุกบรรทัด Y" อีก) ·
    /// ใบอื่น ⇒ ค่าเดิม (บรรทัดที่ตรงกับหัวเอกสาร = null เหมือน <see cref="NormalizeLineOverride"/>)
    /// </summary>
    public static (Guid? DocPayeeOverride, IReadOnlyList<Guid?> LineOverrides) LiftSinglePayee(
        Guid docContactId, Guid? docPayeeOverride, IReadOnlyList<WhtPayeeLine> lines)
    {
        var lifted = EffectivePayeeOverride(docContactId, docPayeeOverride, lines);
        var docPayee = WhtCertIssuePlan.PayeeOf(docContactId, lifted);
        return (lifted, lines.Select(l => l.LinePayeeContactId is Guid p && p != Guid.Empty && p != docPayee ? (Guid?)p : null).ToList());
    }

    /// <summary>
    /// ฝ่ายค้าน P2c (รอบ 204): ใบลด/เพิ่มหนี้ที่อ้างใบต้นทาง<b>หลายผู้ถูกหัก</b> — ทุกบรรทัดที่มีภาษีหักต้องอ้างบรรทัดของใบต้นทาง (SourceLineId)
    /// ไม่งั้นระบบไม่รู้ว่าลด/เพิ่มหนี้ภาษีของผู้ถูกหักรายใด (เดิมตกไปผังของผู้ถูกหักหัวเอกสารเงียบ ๆ) ⇒ ข้อความปฏิเสธพร้อมทางไปต่อ ·
    /// ใบต้นทางรายเดียว/ทุกบรรทัดอ้างครบ ⇒ null (เส้นเดิม)
    /// </summary>
    public static string? UnmappedAdjustmentLineReason(string? noteNumber, string? sourceNumber, bool sourceIsMultiPayee,
        IReadOnlyList<(string? Description, decimal WithholdingTaxAmount, bool MappedToSourceLine)> lines)
    {
        if (!sourceIsMultiPayee) return null;
        var bad = lines.Where(l => l.WithholdingTaxAmount > 0m && !l.MappedToSourceLine).Select(l => l.Description).ToList();
        if (bad.Count == 0) return null;
        var names = string.Join(" · ", bad);
        return $"ใบลด/เพิ่มหนี้ {noteNumber}: รายการ \"{names}\" มีภาษีหัก ณ ที่จ่ายแต่ไม่ได้อ้างบรรทัดของใบ {sourceNumber} "
               + "ซึ่งมีผู้ถูกหักภาษีหลายราย — ระบบไม่รู้ว่าปรับหนี้ภาษีของผู้ถูกหักรายใด · ทางไปต่อ: สร้างใบลด/เพิ่มหนี้ด้วยการ \"แปลง\" จากใบ "
               + $"{sourceNumber} (เลือกรายการที่ปรับ — ระบบผูกบรรทัดให้) แทนการพิมพ์รายการเอง หรือแยกใบลดหนี้ต่อผู้ถูกหัก";
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
