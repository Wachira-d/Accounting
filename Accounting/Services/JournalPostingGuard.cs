using Accounting.Models.Enums;

namespace Accounting.Services;

/// <summary>
/// ด่านตรวจ JE "1 ขั้นก่อนบันทึก" — กฎ pure ไม่มี DB/AI dependency
/// (กฎเหล็ก #1: เส้นทาง local ต้องทำงานได้ 100% แม้ปิด AI ทุกตัว)
///
/// ที่มา (บั๊กจริงจากผู้ใช้ — UV-202607-0037 จาก EXP-20260706-0007):
///   Dr ค่าวัสดุสิ้นเปลือง 17,890 + Dr ภาษีซื้อ 1,252.30
///   Cr 21917 ภาษีหัก ณ ที่จ่าย **19,142.30**   ← เครดิตทั้งใบลง WHT
///   • ไม่มีขาเจ้าหนี้/เงินสดเลย  • ยอด WHT = 107% ของฐาน (ควร ≤ 15%)
/// JE สมดุล (Dr = Cr) จึงผ่าน guard เดิมทุกตัว — สมดุลอย่างเดียวไม่พอ
/// ต้องตรวจ "โครงสร้าง" ด้วย: เงินไปลงถูกฝั่ง/ถูกกลุ่มบัญชีหรือไม่
///
/// ใช้ 2 ที่ด้วยกฎชุดเดียวกัน (canonical function เดียว — กฎเหล็ก #4 C):
///   1. posting gate — AutoPostToJournalAsync / integration ก่อน save (Error = block)
///   2. scanner — JournalAnomalyService กวาด JE ที่ post ไปแล้วหาใบที่หลุดมาก่อน
/// </summary>
public static class JournalPostingGuard
{
    public sealed record LineFacts(
        string AccountCode, AccountType AccountType,
        decimal Debit, decimal Credit);

    /// <summary>ข้อเท็จจริงจากเอกสารต้นทาง — null ได้ (JE ที่ไม่ผูกเอกสาร
    /// จะตรวจเฉพาะกฎโครงสร้างที่ไม่ต้องรู้บริบท)</summary>
    /// <param name="ExchangeRate">เรทแปลงเป็นหน่วยเดียวกับ GL — ยอดบนเอกสาร
    /// สกุลต่างประเทศเก็บเป็นสกุลเอกสาร แต่ AutoPost ลง GL เป็นบาท (Conv) ⇒
    /// ต้องคูณก่อนเทียบ ไม่งั้นใบ USD@35 โดน JE-VAT-OVER/JE-WHT-DOC block ทุกใบ
    /// (เคสจริงจากทีมจำลอง P-1). เส้นที่ JE ลงหน่วยเดียวกับเอกสารอยู่แล้ว
    /// (integration) ใช้ 1</param>
    public sealed record DocFacts(
        DocumentType DocumentType,
        decimal SubTotal,
        decimal VatAmount,
        decimal WithholdingTaxAmount,
        decimal TotalAmount,
        bool IsDeposit = false,
        decimal ExchangeRate = 1m);

    public sealed record Finding(string RuleCode, bool IsError, string Message);

    // ── กลุ่มบัญชีที่กฎอ้างถึง (ตรงรหัส/prefix ตามผังมาตรฐาน) ──────────────
    private static readonly string[] WhtPayableCodes = { "21916", "21917", "21918" };
    private static readonly string[] InputVatCodes = { "11610", "11640" };
    private static readonly string[] OutputVatCodes = { "21911", "21912", "21913" };

    private static bool IsWhtPayable(string c) => WhtPayableCodes.Contains(c);
    private static bool IsInputVat(string c) => InputVatCodes.Contains(c);
    private static bool IsOutputVat(string c) => OutputVatCodes.Contains(c);
    private static bool IsAnyVatOrWht(string c) => IsWhtPayable(c) || IsInputVat(c) || IsOutputVat(c);

    private static bool IsPurchaseFamily(DocumentType t) =>
        t is DocumentType.Expense or DocumentType.PurchaseInvoice or DocumentType.PaymentVoucher;

    public static List<Finding> Validate(IReadOnlyList<LineFacts> lines, DocFacts? doc)
    {
        const MidpointRounding R = MidpointRounding.AwayFromZero;
        var findings = new List<Finding>();
        if (lines.Count == 0) return findings;

        decimal Rnd(decimal v) => Math.Round(v, 2, R);

        // ── กฎโครงสร้าง (ไม่ต้องรู้เอกสาร) ─────────────────────────────────
        var totalDr = Rnd(lines.Sum(l => l.Debit));
        var totalCr = Rnd(lines.Sum(l => l.Credit));
        if (Math.Abs(totalDr - totalCr) > 0.01m)
            findings.Add(new Finding("JE-BAL", true,
                $"เดบิต ({totalDr:N2}) ไม่เท่ากับเครดิต ({totalCr:N2})"));

        // WHT ค้างจ่ายฝั่ง Cr ต้องเป็น "เศษส่วนเล็ก" ของฐานค่าใช้จ่าย — อัตรา
        // ตามกฎหมายสูงสุด 15% (ท.ป.4/2528) ถ้าเกินมาก = เครดิตผิดบัญชีแน่นอน
        // (เคสจริง: ทั้งใบ 19,142.30 ลง 21917 = 107% ของฐาน)
        var whtCr = Rnd(lines.Where(l => IsWhtPayable(l.AccountCode)).Sum(l => l.Credit));
        // ฐานเทียบ = **ทุกขา Dr ที่ไม่ใช่บัญชีภาษี** ไม่ใช่เฉพาะหมวดค่าใช้จ่าย/
        // สินทรัพย์ — เพราะ JE ที่มี WHT มีได้หลายทรง:
        //   ตั้งหนี้  Dr ค่าใช้จ่าย  / Cr เจ้าหนี้ + Cr WHT
        //   จ่ายชำระ Dr เจ้าหนี้(หนี้สิน) + Dr ผลต่างอัตราแลกเปลี่ยน / Cr เงิน + Cr WHT
        // เดิมนับเฉพาะ Expense/Asset ⇒ JE จ่ายชำระเหลือฐานแค่ขา FX เล็ก ๆ แล้ว
        // ฟ้อง Error เท็จทุกใบ (X-8ข) · และถ้าบรรทัดถูกลงผังหมวดรายได้ผิดฝั่ง
        // ฐานจะเป็น 0 จนกฎถูกข้ามทั้งข้อ — ยิ่งผิดยิ่งเงียบ (P-8)
        var whtBase = Rnd(lines
            .Where(l => !IsAnyVatOrWht(l.AccountCode))
            .Sum(l => l.Debit));
        if (whtCr > 0 && whtBase > 0 && whtCr > Rnd(whtBase * 0.155m) + 1m)
            findings.Add(new Finding("JE-WHT-RATIO", true,
                $"ยอดภาษีหัก ณ ที่จ่าย ({whtCr:N2}) สูงเกินอัตราสูงสุด 15% ของฐาน " +
                $"({whtBase:N2}) — น่าจะเครดิตผิดบัญชี (ขาเจ้าหนี้/เงินไปลงบัญชี WHT)"));

        if (doc == null || doc.IsDeposit) return findings;

        // ── กฎที่เทียบกับเอกสารต้นทาง ─────────────────────────────────────
        // ยอดเอกสารแปลงเป็นหน่วยเดียวกับ GL ก่อนเทียบเสมอ (P-1) — เผื่อค่า
        // ความคลาดเคลื่อนจากการปัดรายบรรทัด (Conv ปัดต่อบรรทัด + fx squeeze)
        var rate = doc.ExchangeRate <= 0m ? 1m : doc.ExchangeRate;
        var docVat = Rnd(doc.VatAmount * rate);
        var docWht = Rnd(doc.WithholdingTaxAmount * rate);
        var docTotal = Rnd(doc.TotalAmount * rate);
        var tol = rate == 1m ? 1m : 1m + Rnd(0.02m * rate);

        // 1) ยอด WHT ใน GL ต้องตรงกับยอดบนเอกสาร
        if (whtCr > 0 && Math.Abs(whtCr - docWht) > tol)
            findings.Add(new Finding("JE-WHT-DOC", true,
                $"ยอดภาษีหัก ณ ที่จ่ายใน JE ({whtCr:N2}) ไม่ตรงกับเอกสาร " +
                $"({docWht:N2})"));
        if (whtCr == 0 && docWht > 1m && IsPurchaseFamily(doc.DocumentType))
            findings.Add(new Finding("JE-WHT-MISSING", false,
                $"เอกสารมีภาษีหัก ณ ที่จ่าย {docWht:N2} " +
                "แต่ JE ไม่มีบรรทัดบัญชี WHT ค้างจ่าย (21916/21917/21918)"));

        // 2) VAT ใน GL ต้องไม่ "เกิน" เอกสาร (น้อยกว่าได้ — เคสไม่เคลม/พักรอใบกำกับ)
        var inputVatDr = Rnd(lines.Where(l => IsInputVat(l.AccountCode)).Sum(l => l.Debit - l.Credit));
        var outputVatCr = Rnd(lines.Where(l => IsOutputVat(l.AccountCode)).Sum(l => l.Credit - l.Debit));
        if (inputVatDr > docVat + tol)
            findings.Add(new Finding("JE-VAT-OVER", true,
                $"ภาษีซื้อใน JE ({inputVatDr:N2}) มากกว่า VAT บนเอกสาร ({docVat:N2})"));
        if (outputVatCr > docVat + tol)
            findings.Add(new Finding("JE-VAT-OVER", true,
                $"ภาษีขายใน JE ({outputVatCr:N2}) มากกว่า VAT บนเอกสาร ({docVat:N2})"));

        // 3) ฝั่งซื้อ: "เงินของใบนี้ต้องไปอยู่ที่ไหนสักแห่งที่ไม่ใช่บัญชีภาษี" —
        //    ขา Cr ที่ไม่ใช่ VAT/WHT ต้องรองรับยอด TotalAmount (เจ้าหนี้/เงินสด/
        //    ธนาคาร/เจ้าหนี้กรรมการ/แหล่งเงินใดก็ได้ที่ผู้ใช้เลือก — ไม่ fix รหัส
        //    เพื่อไม่ block แหล่งเงินที่ถูกกฎหมายอื่น ๆ) ถ้าเงินทั้งใบไปกองใน
        //    บัญชีภาษี = ผิดแน่ (เคสจริง: Cr ที่ไม่ใช่ภาษี = 0)
        if (IsPurchaseFamily(doc.DocumentType) && docTotal > 1m)
        {
            var counterpartCr = Rnd(lines
                .Where(l => !IsAnyVatOrWht(l.AccountCode))
                .Sum(l => l.Credit));
            if (counterpartCr < docTotal - tol)
                findings.Add(new Finding("JE-NO-COUNTERPART", true,
                    $"ขาเครดิตเจ้าหนี้/เงินสด/ธนาคาร ({counterpartCr:N2}) ไม่ครบยอดเอกสาร " +
                    $"({docTotal:N2}) — เงินของใบนี้ไปกองอยู่ในบัญชีภาษีแทน"));
        }

        return findings;
    }

    /// <summary>สรุป error เป็นข้อความเดียวสำหรับ throw — คืน null ถ้าไม่มี error</summary>
    public static string? ErrorSummary(List<Finding> findings, string docNumber)
    {
        var errors = findings.Where(f => f.IsError).ToList();
        if (errors.Count == 0) return null;
        return $"JE ของ {docNumber} ไม่ผ่านการตรวจโครงสร้างบัญชี — "
            + string.Join(" · ", errors.Select(e => $"[{e.RuleCode}] {e.Message}"))
            + " (ระบบไม่บันทึกเพื่อกันข้อมูลผิดเข้าแยกประเภท)";
    }
}
