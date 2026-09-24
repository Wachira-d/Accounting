using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ข้อมูลช่องหนึ่ง "พิมพ์อยู่บนกระดาษไหม" — ไม่รู้ต้องเป็นค่าใน enum (DECISION_DOCTRINE §1 G3)</summary>
public enum OcrPaperPresence
{
    /// <summary>ตัดสินไม่ได้ (ไม่มีข้อความ · ไม่รู้ชื่อบริษัทเราเลยและกระดาษไม่มีป้ายผู้ซื้อ)</summary>
    Unknown = 0,
    Printed = 1,
    Missing = 2,
}

/// <summary>หลักฐานจากกระดาษว่ามีชื่อ/เลขผู้ซื้อไหม</summary>
public readonly record struct OcrBuyerPaperEvidence(OcrPaperPresence Name, OcrPaperPresence TaxId, string Why);

/// <summary>คำตัดสินเรื่องสิทธิ์เคลมภาษีซื้อจากข้อมูลผู้ซื้อบนกระดาษ</summary>
/// <param name="BlockClaim">true = ภาษีซื้อต้องห้าม §82/5(1)</param>
/// <param name="RuleCode">รหัสกฎ (ลง audit/หมายเหตุ)</param>
/// <param name="LegalReference">มาตราที่อ้าง</param>
/// <param name="Message">ข้อความไทยพร้อมทางไปต่อ (null เมื่อไม่บล็อก)</param>
public readonly record struct OcrBuyerClaimVerdict(bool BlockClaim, string? RuleCode, string? LegalReference, string? Message);

/// <summary>
/// **ใบหัว "ใบกำกับภาษี" (เต็มรูป) ที่กระดาษไม่มีชื่อ/เลขผู้ซื้อ ⇒ ภาษีซื้อต้องห้าม §82/5(1)** — pure · ไม่ throw
///
/// ═══ ที่มา (คำตัดสินเจ้าของ รอบ 193 ข้อ 9) ═══
/// <para>§86/4(3) บังคับใบกำกับเต็มรูปแสดง "ชื่อ ที่อยู่ ของผู้ซื้อ" และเลขประจำตัวผู้เสียภาษีของผู้ซื้อที่จด VAT
/// (ประกาศอธิบดีฯ ฉบับที่ 199) · ขาดรายการ ⇒ §82/5(1) นำภาษีซื้อมาเครดิตไม่ได้ · ตัวอนุมานบทบาทเดิม
/// (<c>OcrDocumentRoleInferrer</c>) จับเฉพาะใบที่ "ราคารวม VAT + ไม่มีป้าย/เลขผู้ซื้อ" ว่าเป็นใบอย่างย่อ §82/5(2) —
/// ใบที่หัวพิมพ์ "ใบกำกับภาษี" แต่ไม่มีผู้ซื้อและราคาแยก VAT (หรือมีป้าย "ลูกค้า" แต่ไม่มีเลข) ยังถูกตีว่าเคลมได้ ⇒ ตัวนี้<b>เติม</b>
/// ไม่ใช่สำเนาของตัวนั้น (ใช้เฉพาะเมื่อตัวนั้นตอบว่าเป็นใบเต็มรูปแล้ว)</para>
///
/// <para><b>หลักฐานต้องมาจากกระดาษ</b> ไม่ใช่ค่าที่ระบบเติมเอง: ชื่อบริษัทเราที่ <c>OcrPartyResolver.FillOurName</c> ใส่ให้ ·
/// ที่อยู่เราที่เติมจากทะเบียน · เลขภาษีที่ย้ายมาจากตัวตนของเรา — ทุกตัวถูกตรวจด้วยการหา<b>ในข้อความดิบ</b>อีกครั้งเสมอ
/// (ค่าที่เราเติมเองไม่มีวันเป็นหลักฐานว่ากระดาษมีรายการนั้น — F2 ข้อ 3)</para>
///
/// <para>ทิศปลอดภัย (G5): บล็อก = คำเตือนเห็นบนหน้าจอ + บรรทัดติด "เคลมไม่ได้" ที่ผู้ใช้เปิดคืนได้ถ้ากระดาษมีจริง ·
/// ไม่บล็อกผิดใบ = ยื่น ภ.พ.30 เกินสิทธิ์เงียบจนสรรพากรตรวจ ⇒ ตัดสินได้ = บล็อก · ตัดสินไม่ได้ (Unknown) = ไม่แตะ (พฤติกรรมเดิม)</para>
/// </summary>
public static class OcrBuyerOnPaper
{
    public const string RuleCode = "RD-82/5(1)-BUYER";
    public const string LegalReference = "ป.รัษฎากร ม.82/5(1) ประกอบ ม.86/4(3) · ประกาศอธิบดีฯ (ฉบับที่ 199)";

    private static readonly Regex ThirteenDigits = new(
        @"(?<!\d)\d(?:[- \t]?\d){12}(?!\d)", RegexOptions.CultureInvariant);

    /// <summary>อ่านหลักฐานจากข้อความดิบของกระดาษ</summary>
    /// <param name="rawText">ข้อความทั้งใบ (normalize แล้ว)</param>
    /// <param name="vendorTaxId">เลขผู้ขาย (ตัดออกจากการนับ)</param>
    /// <param name="buyerTaxId">เลขผู้ซื้อที่ engine/ตัวตัดสินใส่ไว้ (ต้องเจอในข้อความจึงนับ)</param>
    /// <param name="buyerName">ชื่อผู้ซื้อที่ถืออยู่ (อาจเป็นค่าที่ระบบเติม — ต้องเจอในข้อความจึงนับ)</param>
    /// <param name="ourTaxId">เลขภาษีบริษัทเรา</param>
    /// <param name="ourName">ชื่อบริษัทเรา</param>
    public static OcrBuyerPaperEvidence Read(
        string? rawText, string? vendorTaxId, string? buyerTaxId, string? buyerName,
        string? ourTaxId, string? ourName)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new(OcrPaperPresence.Unknown, OcrPaperPresence.Unknown, "ไม่มีข้อความจากกระดาษ");

        // ── เลขผู้ซื้อ ──
        var vendor = ThaiTaxId.Normalize(vendorTaxId);
        var buyer = ThaiTaxId.Normalize(buyerTaxId);
        var ours = ThaiTaxId.Normalize(ourTaxId);
        var taxPrinted = false;
        string taxWhy = "ไม่พบเลข 13 หลักของผู้ซื้อบนกระดาษ";
        foreach (Match m in ThirteenDigits.Matches(rawText!))
        {
            var d = ThaiTaxId.Normalize(m.Value);
            if (d.Length != 13 || d == vendor) continue;
            if (d == ours || (buyer.Length == 13 && d == buyer))
            { taxPrinted = true; taxWhy = "พบเลขผู้ซื้อบนกระดาษ"; break; }
            if (ThaiTaxId.IsPlausibleFromScan(d))
            { taxPrinted = true; taxWhy = "พบเลขผู้เสียภาษี 13 หลักอีกเลขที่ไม่ใช่ของผู้ขาย"; break; }
        }
        // เลขเราที่ OCR อ่านเพี้ยนหลักเดียว (checksum ตก) = กระดาษพิมพ์ไว้จริง
        if (!taxPrinted && ours.Length == 13 && OcrPartyResolver.OurTaxIdOnPaper(rawText, ours).NearMiss is not null)
        { taxPrinted = true; taxWhy = "พบเลขภาษีบริษัทเราบนกระดาษ (อ่านเพี้ยนหนึ่งหลัก)"; }

        // ── ชื่อผู้ซื้อ ──
        var lines = rawText!.Split('\n');
        OcrPaperPresence name;
        string nameWhy;
        if (!string.IsNullOrWhiteSpace(ourName) && lines.Any(l => OcrSelfPartyGuard.NameOverlaps(l, ourName)))
        { name = OcrPaperPresence.Printed; nameWhy = "พบชื่อบริษัทเราบนกระดาษ"; }
        else if (!string.IsNullOrWhiteSpace(buyerName) && lines.Any(l => OcrSelfPartyGuard.NameOverlaps(l, buyerName)))
        { name = OcrPaperPresence.Printed; nameWhy = "พบชื่อผู้ซื้อบนกระดาษ"; }
        else if (HasLabelledBuyerName(rawText!))
        { name = OcrPaperPresence.Printed; nameWhy = "มีป้ายผู้ซื้อพร้อมชื่อบนกระดาษ"; }
        else if (string.IsNullOrWhiteSpace(ourName) && string.IsNullOrWhiteSpace(buyerName))
        { name = OcrPaperPresence.Unknown; nameWhy = "ไม่รู้ชื่อบริษัทเรา และกระดาษไม่มีป้ายผู้ซื้อ — ตัดสินไม่ได้"; }
        else
        { name = OcrPaperPresence.Missing; nameWhy = "ไม่พบชื่อผู้ซื้อบนกระดาษ"; }

        return new(name, taxPrinted ? OcrPaperPresence.Printed : OcrPaperPresence.Missing, nameWhy + " · " + taxWhy);
    }

    /// <summary>หลักฐานจาก e-Tax XML ที่ลงนามแล้ว (ช่อง BuyerTradeParty) — ไม่มีข้อความให้ค้น แต่ XML คือตัวใบกำกับเอง</summary>
    public static OcrBuyerPaperEvidence FromStructured(string? buyerName, string? buyerTaxId)
        => new(string.IsNullOrWhiteSpace(buyerName) ? OcrPaperPresence.Missing : OcrPaperPresence.Printed,
            ThaiTaxId.IsWellFormed(buyerTaxId) ? OcrPaperPresence.Printed : OcrPaperPresence.Missing,
            "e-Tax XML (BuyerTradeParty)");

    /// <summary>คำตัดสิน — ใช้เฉพาะเมื่อ <paramref name="isFullTaxInvoice"/> (ตัวอนุมานบทบาทตอบว่าใบเต็มรูปและเราเป็นผู้ซื้อ)
    /// และมี VAT · ขาดชื่อ<b>หรือ</b>ขาดเลข (ที่ตัดสินได้) ⇒ บล็อก</summary>
    public static OcrBuyerClaimVerdict JudgeClaim(bool isFullTaxInvoice, decimal vatAmount, OcrBuyerPaperEvidence ev)
    {
        if (!isFullTaxInvoice || vatAmount <= 0m) return new(false, null, null, null);
        var missName = ev.Name == OcrPaperPresence.Missing;
        var missTax = ev.TaxId == OcrPaperPresence.Missing;
        if (!missName && !missTax) return new(false, null, null, null);

        var what = missName && missTax ? "ชื่อผู้ซื้อและเลขประจำตัวผู้เสียภาษีของผู้ซื้อ"
            : missName ? "ชื่อผู้ซื้อ" : "เลขประจำตัวผู้เสียภาษีของผู้ซื้อ";
        return new(true, RuleCode, LegalReference,
            $"({RuleCode}) หัวกระดาษเป็น \"ใบกำกับภาษี\" แต่ไม่มี{what}บนกระดาษ — ใบกำกับขาดรายการตาม ม.86/4(3) "
            + "จึงนำภาษีซื้อมาเคลม ภ.พ.30 ไม่ได้ (ม.82/5(1)) ระบบปิดการเคลมให้และรวม VAT เป็นต้นทุน · "
            + "ขอใบกำกับภาษีเต็มรูปที่ระบุชื่อ-ที่อยู่-เลขผู้เสียภาษีของบริษัทจากผู้ขาย แล้วเปิดการเคลมคืนในบรรทัด"
            + " · ถ้ากระดาษมีข้อมูลนี้จริง (อ่านไม่ออก) ให้เปิดการเคลมคืนเอง");
    }

    /// <summary>ป้ายผู้ซื้อ (ลูกค้า/ผู้ซื้อ/Bill To…) ที่มีข้อความชื่อ ≥ 4 ตัวอักษรต่อท้ายในบรรทัดเดียวกันหรือบรรทัดถัดไป</summary>
    private static bool HasLabelledBuyerName(string text)
    {
        var positions = OcrPartyLabels.FindAll(text).BuyerPos;
        foreach (var pos in positions)
        {
            var lineEnd = text.IndexOf('\n', pos);
            var rest = lineEnd < 0 ? text[pos..] : text[pos..lineEnd];
            // ตัดตัวป้ายเอง (คำแรก) แล้วดูว่ายังเหลือ "ชื่อ" ไหม
            var afterLabel = Regex.Replace(rest, @"^[^\s:：]+[\s:：]*", "");
            if (LetterCount(afterLabel) >= 4) return true;
            if (lineEnd >= 0)
            {
                var nextEnd = text.IndexOf('\n', lineEnd + 1);
                var next = nextEnd < 0 ? text[(lineEnd + 1)..] : text[(lineEnd + 1)..nextEnd];
                if (LetterCount(next) >= 4 && !ThirteenDigits.IsMatch(next)) return true;
            }
        }
        return false;
    }

    private static int LetterCount(string s) => s.Count(char.IsLetter);
}
