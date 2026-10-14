namespace Accounting.Helpers;

/// <summary>ผลคิด "ภาษีหัก ณ ที่จ่ายที่ระบบแนะนำสำหรับการจ่ายงวดนี้" (ดู <see cref="WhtPaymentSuggestion"/>)</summary>
/// <param name="Figures">ตัวเลขใบเดิม/ใบลดหนี้/ที่หักไปแล้ว — ตัวคิดเดียวกับป้าย "50 ทวิ ต้องตรวจ" (<see cref="WhtCreditNoteRemainder.Compute"/>)</param>
/// <param name="AlreadyWithheld">ภาษีที่หักไปแล้วของใบนี้ (งวดก่อน ๆ)</param>
/// <param name="NetWht">ภาษีหักทั้งใบหลังใบลดหนี้ = ใบเดิม − Σ ใบลดหนี้ (ไม่ติดลบ) · ไม่มีใบลดหนี้ = ภาษีหักของใบเดิม</param>
/// <param name="GrossCap">เพดานตามกฎหมายของใบเดิม = max(0, ใบเดิม − ที่หักไปแล้ว) — เกินนี้ = บล็อก (ค่าเดิมของระบบ)</param>
/// <param name="NetCap">ภาษีหักที่ยังเหลือให้หักหลังใบลดหนี้ = max(0, NetWht − ที่หักไปแล้ว) — เกินนี้ = รับได้แต่เตือน + audit</param>
/// <param name="Suggested">ยอดที่ระบบเติมให้ (ผู้ใช้แก้ได้) — งวดสุดท้าย = NetCap · งวดกลาง = สัดส่วนยอดจ่าย (ไม่เกิน NetCap)</param>
/// <param name="IsFinal">งวดนี้ปิดยอดค้างของใบ</param>
/// <param name="ReasonText">ข้อความอธิบายที่มาของยอดแนะนำ (หน้าเว็บแสดงตรง ๆ — ไม่คิดเอง)</param>
public sealed record WhtPaymentSuggestionResult(
    WhtCreditNoteRemainderFigures Figures, decimal AlreadyWithheld, decimal NetWht, decimal GrossCap, decimal NetCap,
    decimal Suggested, bool IsFinal, string ReasonText)
{
    /// <summary>มีใบลดหนี้ที่ลดภาษีหักของใบนี้อยู่</summary>
    public bool HasCreditNotes => Figures.CreditNoteCount > 0;
}

/// <summary>
/// **ภาษีหัก ณ ที่จ่ายตอนบันทึกจ่าย — ยอดแนะนำหลังหักใบลดหนี้ซื้อที่อนุมัติแล้ว** (ทีม A · 2a · คำสั่งเจ้าของ 2026-10-13
/// "ระบบจะต้องไม่ล็อคตายตัว ผู้ใช้สามารถตัดสินใจได้ ระบบแค่อำนวยความสะดวกและแนะนำให้ได้มากที่สุด")
///
/// <para>═══ ที่มา ═══ ใบแจ้งหนี้ซื้อหัก 3% = 300 · ใบลดหนี้ 1,000 หัก 30 (อนุมัติแล้ว ⇒ ยอดค้างลดแล้ว) · บันทึกจ่ายงวดสุดท้าย —
/// เดิม <c>CreatePaymentAsync</c> หยิบ "ที่เหลือทั้งก้อน" ของใบเดิม 300 (ไม่รู้จักใบลดหนี้) ⇒ 50 ทวิ ออก 300 ทั้งที่หักจริงควรเป็น 270 ·
/// ป้าย "50 ทวิ ต้องตรวจ" บอกยอดที่ถูกแล้วแต่ "ระบบยังไม่หักให้เอง" ⇒ ผู้ใช้ต้องจำไปแก้เอง</para>
/// <para>═══ กติกา ═══ NetWht = ใบเดิม − Σ ใบลดหนี้ (ตัวคิดเดียว <see cref="WhtCreditNoteRemainder.Compute"/>) · NetCap = NetWht − ที่หักไปแล้ว ·
/// แนะนำ = งวดสุดท้าย NetCap / งวดกลาง round((จ่าย + ยอดปิดด้วยบรรทัดปรับ) × NetWht ÷ ยอดจ่ายผู้รับสุทธิหลังใบลดหนี้) ไม่เกิน NetCap ·
/// ไม่มีใบลดหนี้ ⇒ ตัวเลขเดิมของระบบทุกสตางค์ (เทสต์ล็อก) · ผู้ใช้ระบุเอง: เกินเพดานใบเดิม = บล็อก (กฎหมาย/GL) · อยู่ระหว่าง NetCap กับเพดานใบเดิม
/// = รับ + เตือน + audit (<see cref="AboveNetRuleCode"/>) · ต่ำกว่า = รับ</para>
/// <para>═══ ใบเพิ่มหนี้ไม่นับ ═══ ใบเพิ่มหนี้ตั้งหนี้แยกของตัวเอง (ไม่แตะยอดค้างของใบต้นทาง · จ่ายผ่าน <c>PayableDocumentTypes</c> ใบของมันเอง
/// พร้อมหักภาษีของมันเอง) ⇒ บวกภาษีของใบเพิ่มหนี้เข้าใบต้นทาง = หักซ้ำสองที่</para>
/// </summary>
public static class WhtPaymentSuggestion
{
    /// <summary>audit เมื่อผู้ใช้บันทึกภาษีหักสูงกว่ายอดที่เหลือหลังใบลดหนี้ (รับตามที่ผู้ใช้ระบุ)</summary>
    public const string AboveNetRuleCode = "RD-50BIS-PAY-WHT-ABOVE-NET";

    /// <summary>มาตราอ้างอิงของ audit/คำเตือนชุดนี้</summary>
    public const string LegalReference = "ท.ป.4/2528";

    /// <summary>ป้ายคำเตือนก่อนอนุมัติ: ใบสำคัญจ่ายที่อ้างใบซื้อ หักภาษีรวมเกินยอดที่เหลือหลังใบลดหนี้ (ทางเข้าที่ไม่มีคนผ่านได้ — <see cref="ApprovalAcknowledgement"/>)</summary>
    public const string ApprovalWarningPrefix = "[WHT-CN-NET]";

    /// <summary>คำเตือนก่อนอนุมัติชุด <see cref="ApprovalWarningPrefix"/> ไหม</summary>
    public static bool IsApprovalWarning(string warning) => warning.StartsWith(ApprovalWarningPrefix, StringComparison.Ordinal);

    private const decimal Tolerance = 0.01m;

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>คิดยอดแนะนำ</summary>
    /// <param name="docWht">ภาษีหักของใบเดิม (<c>Document.WithholdingTaxAmount</c>)</param>
    /// <param name="cnWhts">ภาษีหักของใบลดหนี้ที่มีผลทุกใบ (อนุมัติแล้ว ไม่ถูกยกเลิก)</param>
    /// <param name="alreadyWithheld">ภาษีที่หักไปแล้วของใบนี้</param>
    /// <param name="payeeTotal">ยอดจ่ายผู้รับของใบเดิม (<c>ForeignServiceVat.PayeeAmount</c>) — ตัวหารของสัดส่วน</param>
    /// <param name="cnPayeeTotals">ยอดจ่ายผู้รับของใบลดหนี้แต่ละใบใน <paramref name="cnWhts"/> (ยอดที่ใบลดหนี้ลดหนี้ของใบเดิม)</param>
    /// <param name="amount">เงินที่จ่ายงวดนี้</param>
    /// <param name="settleNet">หนี้ที่ปิดด้วยบรรทัดปรับ (= ยอดที่ปิดหนี้ − เงินที่จ่าย · 0 = ไม่มี)</param>
    /// <param name="isFinal">งวดนี้ปิดยอดค้าง</param>
    public static WhtPaymentSuggestionResult Compute(decimal docWht, IReadOnlyCollection<decimal> cnWhts, decimal alreadyWithheld,
        decimal payeeTotal, IReadOnlyCollection<decimal> cnPayeeTotals, decimal amount, decimal settleNet, bool isFinal)
    {
        var figures = WhtCreditNoteRemainder.Compute(docWht, cnWhts, alreadyWithheld);
        var hasCn = figures.CreditNoteCount > 0;
        // ไม่มีใบลดหนี้ ⇒ สูตรเดิมของ CreatePaymentAsync ตรงตัว (ห้ามผ่านการปัด/ตัดของตัวคิดใบลดหนี้ — ต้องได้ค่าเดิมทุกสตางค์)
        var grossCap = Math.Max(0m, docWht - alreadyWithheld);
        var netWht = hasCn ? figures.Remaining : docWht;
        var netCap = hasCn ? figures.LeftToWithhold : grossCap;
        var netPayee = hasCn ? payeeTotal - cnPayeeTotals.Sum() : payeeTotal;

        decimal suggested;
        if (isFinal)
            suggested = netCap;
        else
        {
            var proportional = netPayee > 0m ? R((amount + settleNet) * netWht / netPayee) : 0m;
            suggested = Math.Min(proportional, netCap);
        }
        if (suggested < 0m) suggested = 0m;

        return new WhtPaymentSuggestionResult(figures, alreadyWithheld, netWht, grossCap, netCap, suggested, isFinal,
            ReasonOf(figures, docWht, alreadyWithheld, netCap, suggested, isFinal));
    }

    /// <summary>ข้อความที่มาของยอดแนะนำ — มีใบลดหนี้ใช้ถ้อยคำเดียวกับป้าย "50 ทวิ ต้องตรวจ" (<see cref="WhtCreditNoteRemainder.FiguresText"/>)</summary>
    private static string ReasonOf(WhtCreditNoteRemainderFigures f, decimal docWht, decimal already, decimal netCap, decimal suggested, bool isFinal)
    {
        var basis = f.CreditNoteCount > 0
            ? WhtCreditNoteRemainder.FiguresText(f)
            : $"ภาษีหักทั้งใบ {docWht:N2}";
        var done = already > 0m ? $" · หักไปแล้ว {already:N2} ⇒ เหลือหักได้ {netCap:N2}" : "";
        var how = isFinal ? "งวดนี้ปิดยอดค้าง = ยอดที่เหลือทั้งหมด" : "ตามสัดส่วนยอดที่จ่ายงวดนี้";
        return $"{basis}{done} — ระบบแนะนำงวดนี้ {suggested:N2} ({how}) · แก้ได้ถ้าหนังสือรับรองระบุยอดอื่น";
    }

    /// <summary>ข้อความเตือนเมื่อผู้ใช้ระบุภาษีหักงวดนี้สูงกว่ายอดที่เหลือ (NetCap) — null = ไม่ต้องเตือน (≤ NetCap) ·
    /// ผู้เรียกที่มีเพดานใบเดิม (<c>CreatePaymentAsync</c>) ตรวจ "เกินเพดาน = บล็อก" ก่อนเรียกตัวนี้</summary>
    /// <param name="r">ผลจาก <see cref="Compute"/></param>
    /// <param name="entered">ยอดที่ผู้ใช้ระบุ (ปัด 2 ตำแหน่งแล้ว)</param>
    /// <param name="documentNumber">เลขเอกสาร (ข้อความของการชำระหลายใบ) — null = ใบเดียว</param>
    /// <param name="cashBasis">ฝ่ายค้านรอบ 204 L4: บริษัทรับรู้ภาษีหักตอนจ่าย (<c>WhtRecognitionBasis.Cash</c>) — JE งวดจ่าย Dr เจ้าหนี้ = เงิน + ภาษีหัก ⇒
    /// ส่วนที่เกินยอดที่เหลือหลังใบลดหนี้ตัดเจ้าหนี้เกิน (ใบลดหนี้ลดเจ้าหนี้แบบรวมภาษีไปแล้ว) ⇒ บอกยอดเดบิตค้างในคำเตือน (ยังรับตามผู้ใช้ — คำสั่งเจ้าของ ข้อ 150)</param>
    public static string? AboveNetWarning(WhtPaymentSuggestionResult r, decimal entered, string? documentNumber = null, bool cashBasis = false)
    {
        if (entered <= r.NetCap + Tolerance) return null;
        var basis = r.HasCreditNotes
            ? $"ใบเดิม {r.Figures.OriginalTax:N2} − ใบลดหนี้ {r.Figures.CreditNoteTax:N2} = {r.NetWht:N2}"
            : $"ภาษีหักทั้งใบ {r.NetWht:N2}";
        if (r.AlreadyWithheld > 0m) basis += $" · หักไปแล้ว {r.AlreadyWithheld:N2} ⇒ เหลือหักได้ {r.NetCap:N2}";
        var who = documentNumber is null ? "" : documentNumber + ": ";
        return $"{who}ภาษีหัก ณ ที่จ่ายงวดนี้สูงกว่ายอดที่เหลือ — ระบบแนะนำ {r.Suggested:N2} ({basis}) — บันทึก {entered:N2} ตามที่ผู้ใช้ระบุ"
            + " · ตรวจกับหนังสือรับรอง 50 ทวิ ที่จะออก (ยอดบนใบ = ยอดที่หักจริง)"
            + (cashBasis ? CashBasisResidueNote(R(entered - r.NetCap)) : "");
    }

    /// <summary>ส่วนต่อท้ายคำเตือนเมื่อบริษัทรับรู้ภาษีหักตอนจ่าย — เจ้าหนี้ค้างยอดเดบิตเท่าส่วนที่เกิน (ฝ่ายค้านรอบ 204 L4)</summary>
    public static string CashBasisResidueNote(decimal excess)
        => $" · บริษัทรับรู้ภาษีหัก ณ ที่จ่ายตอนจ่าย (เกณฑ์เงินสด): ส่วนที่เกิน {excess:N2} ตัดเจ้าหนี้เกินยอดที่เหลือ ⇒ เจ้าหนี้ของใบนี้จะค้างยอดเดบิต {excess:N2} "
           + "หลังปิดยอด — ถ้ายอดนี้ถูกจริง ให้ปรับปรุงเจ้าหนี้ด้วยใบสำคัญทั่วไป · ถ้าไม่ตั้งใจ ให้แก้ยอดภาษีงวดนี้เป็นยอดที่ระบบแนะนำ";

    /// <summary>
    /// คำเตือนก่อนอนุมัติ <see cref="ApprovalWarningPrefix"/> — ใบสำคัญจ่าย/ใบรับรองแทนใบเสร็จที่อ้างใบซื้อ (แปลงมา = คัดลอกภาษีหักของใบเดิมเต็ม
    /// ไม่รู้จักใบลดหนี้) หักภาษีรวมกับที่หักไปแล้วเกินยอดที่เหลือหลังใบลดหนี้ · null = ไม่เตือน (ไม่มีใบลดหนี้ · ไม่เกิน)
    /// </summary>
    /// <param name="sourceNumber">เลขใบต้นทาง</param>
    /// <param name="sourceWht">ภาษีหักของใบต้นทาง</param>
    /// <param name="cnWhts">ภาษีหักของใบลดหนี้ที่มีผลของใบต้นทาง</param>
    /// <param name="alreadyWithheld">ภาษีที่หักไปแล้วของใบต้นทาง (ไม่รวมใบนี้)</param>
    /// <param name="thisDocWht">ภาษีหักของใบนี้</param>
    public static string? ApprovalWarning(string sourceNumber, decimal sourceWht, IReadOnlyCollection<decimal> cnWhts,
        decimal alreadyWithheld, decimal thisDocWht)
    {
        var f = WhtCreditNoteRemainder.Compute(sourceWht, cnWhts, alreadyWithheld);
        if (f.CreditNoteCount == 0 || thisDocWht <= 0m) return null;
        var total = R(thisDocWht + Math.Max(0m, alreadyWithheld));
        if (total <= f.Remaining + Tolerance) return null;
        var done = alreadyWithheld > 0m ? $" + ที่หักไปแล้ว {alreadyWithheld:N2} = {total:N2}" : "";
        return $"{ApprovalWarningPrefix} ใบนี้หักภาษี ณ ที่จ่าย {thisDocWht:N2}{done} เกินยอดที่เหลือของ {sourceNumber} หลังใบลดหนี้ — "
            + $"{WhtCreditNoteRemainder.FiguresText(f)} ⇒ ระบบแนะนำให้ใบนี้หัก {f.LeftToWithhold:N2} (แก้ช่องหัก ณ ที่จ่ายในบรรทัด) · "
            + "อนุมัติต่อได้ถ้าหนังสือรับรอง 50 ทวิ ระบุยอดนี้จริง";
    }
}
