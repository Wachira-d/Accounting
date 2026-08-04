using Accounting.Models.Entities;

namespace Accounting.Services.Implementations.Tax;

/// <summary>
/// ตรวจว่าใบกำกับภาษีซื้อบนเอกสาร (PurchaseInvoice/Expense/PaymentVoucher
/// ที่ HasTaxInvoiceReference=true) ครบขั้นต่ำตาม ป.รัษฎากร §86/4 พอที่จะ
/// claim ภาษีซื้อได้หรือไม่ (§82/3). ไม่ครบ → AutoPostToJournalAsync จะ
/// post VAT ไป 11640 "ภาษีซื้อยังไม่ถึงกำหนด" แทน 11610 "ภาษีซื้อ ภ.พ.30".
///
/// เกณฑ์ขั้นต่ำ (ตามที่เจ้าของระบบเลือก — minimum legal):
///   - Supplier name ไม่ว่าง (Contact.Name)
///   - Supplier TaxId 13 หลัก + mod-11 checksum (Contact.TaxId)
///   - Supplier address ไม่ว่าง (Contact.Address)
///   - Supplier branch code ระบุ ("00000" = สำนักงานใหญ่ ก็นับ)
///   - Supplier invoice number ไม่ว่าง (เลขที่ใบจากผู้ขาย)
///   - Supplier tax invoice date ระบุ
///
/// Pure function — ไม่แตะ DB. ใช้ทั้ง approve flow และ adjusting JE flow.
/// </summary>
public static class TaxInvoiceCompletenessChecker
{
    public sealed record Result(bool IsClaimable, IReadOnlyList<string> MissingFields)
    {
        public string MissingSummary => string.Join(", ", MissingFields);
    }

    public static Result Evaluate(Document doc, Contact? supplier)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(supplier?.Name))
            missing.Add("ชื่อผู้ขาย");

        if (!IsValidThaiTaxId(supplier?.TaxId))
            missing.Add("เลขผู้เสียภาษีผู้ขาย (13 หลัก + mod-11)");

        if (string.IsNullOrWhiteSpace(supplier?.Address))
            missing.Add("ที่อยู่ผู้ขาย");

        // branch code: header snapshot ก่อน, fallback Contact (เพราะเอกสารเก่า
        // อาจยังไม่ snapshot). "00000" = สำนักงานใหญ่ นับเป็นครบ.
        var branch = doc.SupplierBranchCode ?? supplier?.BranchCode;
        if (string.IsNullOrWhiteSpace(branch))
            missing.Add("รหัสสาขาผู้ขาย");

        if (string.IsNullOrWhiteSpace(doc.SupplierInvoiceNumber))
            missing.Add("เลขที่ใบกำกับภาษีจากผู้ขาย");

        if (!doc.SupplierTaxInvoiceDate.HasValue)
            missing.Add("วันที่ใบกำกับภาษีจากผู้ขาย");

        return new Result(missing.Count == 0, missing);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ฝั่งขาย — "ข้อมูลผู้ซื้อครบพอจะออกใบกำกับภาษีเต็มรูปไหม" (§86/4(3))
    //
    //  สิ่งที่กฎหมายบังคับจริง (อ่านให้ตรงตัว — เดิมระบบเข้มเกินกฎหมาย):
    //   • §86/4(3) บังคับแค่ **"ชื่อ ที่อยู่ ของผู้ซื้อ"** ไม่มีคำว่าเลข
    //     ประจำตัวผู้เสียภาษีของผู้ซื้อ
    //   • ประกาศอธิบดีฯ (VAT) ฉบับที่ 194/2556 เพิ่ม "เลขประจำตัวผู้เสียภาษี
    //     ของผู้ซื้อ" **เฉพาะกรณีผู้ซื้อเป็นผู้ประกอบการจดทะเบียน VAT**
    //   • ประกาศอธิบดีฯ ฉบับที่ 199/2556 เครื่องหมายสาขา — เช่นกัน เฉพาะผู้ซื้อ
    //     ที่จดทะเบียน (บุคคลธรรมดาไม่มีสาขา)
    //
    //  ผลของการเข้มเกิน: ขายให้บุคคลธรรมดาที่ให้ชื่อ+ที่อยู่ครบ ระบบเคย
    //  ตัดสินว่า "ไม่ครบ" → auto ติ๊กไม่ประสงค์รับใบกำกับ → หัวกระดาษกลาย
    //  เป็น "ใบเสร็จรับเงิน" ทั้งที่ออกใบกำกับเต็มรูปได้ตามกฎหมาย → ลูกค้า
    //  ที่ขอใบกำกับไม่ได้ใบ (§86 ผู้ประกอบการจดทะเบียนต้องออกให้ทันทีที่
    //  tax point เกิด) และเกิดสภาพ "ใบเสร็จโผล่ในรายงานภาษีขาย" ที่อ่านแล้ว
    //  งงว่าทำไมใบไม่ใช่ใบกำกับแต่มี VAT อยู่ในรายงาน
    // ══════════════════════════════════════════════════════════════════

    /// <summary>ผู้ซื้อรายนี้เป็น "ผู้ประกอบการจดทะเบียน" หรือไม่ — ตัดสินจาก
    /// ประเภทผู้ติดต่อ หรือเลขภาษี 13 หลักที่ขึ้นต้นด้วย 0 (เลขทะเบียน
    /// นิติบุคคล; บัตรประชาชนบุคคลธรรมดาขึ้นต้น 1-8).</summary>
    public static bool IsJuristicBuyer(Contact? buyer)
    {
        if (buyer == null) return false;
        if (buyer.ContactType == Models.Enums.ContactType.JuristicPerson) return true;
        var tid = Digits(buyer.TaxId);
        return tid.Length == 13 && tid.StartsWith("0");
    }

    /// <summary>field ผู้ซื้อที่ยังขาดสำหรับใบกำกับภาษีเต็มรูป — ว่าง = ครบ.
    /// ใช้ร่วมกันทั้ง gate ตอนอนุมัติ (DocumentService) และการตัดสินหัวเอกสาร
    /// (PdfGenerationService) เพื่อไม่ให้สองที่ drift กัน (เคยเป็นโค้ดคนละชุด).</summary>
    public static IReadOnlyList<string> MissingBuyerFields(Contact? buyer)
    {
        var missing = new List<string>();
        if (buyer == null) { missing.Add("ข้อมูลผู้ซื้อ"); return missing; }

        // ชื่อ + ที่อยู่ = ขั้นต่ำตามกฎหมายสำหรับผู้ซื้อทุกประเภท
        if (string.IsNullOrWhiteSpace(buyer.Name)) missing.Add("ชื่อผู้ซื้อ");
        if (string.IsNullOrWhiteSpace(buyer.Address)) missing.Add("ที่อยู่ผู้ซื้อ");

        // เลขภาษี + สาขา บังคับเฉพาะผู้ซื้อที่จดทะเบียน (นิติบุคคล)
        if (IsJuristicBuyer(buyer))
        {
            if (Digits(buyer.TaxId).Length != 13) missing.Add("เลขผู้เสียภาษีผู้ซื้อ 13 หลัก");
            if (Digits(buyer.BranchCode).Length != 5) missing.Add("รหัสสาขาผู้ซื้อ 5 หลัก (00000=สนญ.)");
        }
        return missing;
    }

    private static string Digits(string? raw) =>
        new((raw ?? "").Where(char.IsDigit).ToArray());

    /// <summary>Thai TaxId 13-digit + mod-11 checksum ตาม spec กรมสรรพากร
    /// (ใช้ใน e-Tax XML schema validation ด้วย).
    /// สูตร: sum = Σ(digit[i] × (13-i)) for i in 0..11;
    ///        check = (11 - sum%11) % 10 = digit[12]</summary>
    public static bool IsValidThaiTaxId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        // strip whitespace + dashes (RD เอกสารเขียน 1-2345-67890-12-3 ได้)
        Span<char> digits = stackalloc char[13];
        int n = 0;
        foreach (var c in raw)
        {
            if (c is ' ' or '-' or '\t') continue;
            if (!char.IsDigit(c)) return false;
            if (n >= 13) return false;
            digits[n++] = c;
        }
        if (n != 13) return false;

        int sum = 0;
        for (int i = 0; i < 12; i++)
            sum += (digits[i] - '0') * (13 - i);
        int check = (11 - (sum % 11)) % 10;
        return check == (digits[12] - '0');
    }
}
