using Accounting.Helpers;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Validates OCR results against business rules before accepting them.
/// Catches obvious errors that high model confidence can hide
/// (e.g. bad TaxId checksum, SubTotal+VAT != Total, future date).
/// </summary>
public static class OcrConfidenceGateway
{
    /// <param name="Warnings">สิ่งที่<b>น่าจะผิด</b> — มี penalty เสมอ ผู้ใช้ต้องดู</param>
    /// <param name="Notes">ข้อสังเกตที่<b>ไม่ใช่ความผิด</b> (เช่น ใบผสมอัตรา VAT, ราคารวม VAT)
    /// — ไม่มี penalty. แยกออกจาก Warnings เพราะ "คำเตือนที่ฟ้องใบถูกกฎหมายทุกใบ
    /// = คำเตือนที่ผู้ใช้เรียนรู้ที่จะเมิน" แล้วคำเตือนจริงจะถูกเมินตามไปด้วย</param>
    public record GatewayResult(decimal AdjustedConfidence, List<string> Warnings, bool MathConsistent,
        List<string> Notes);

    /// <summary>Default config — used when caller doesn't pass overrides.
    /// Production code should pass config from SiteSettings via Validate(...config).</summary>
    public sealed class GatewayConfig
    {
        public decimal MaxPenalty { get; init; } = 0.60m;
        public decimal MathTolerance { get; init; } = 2.0m;
        public decimal TaxIdPenalty { get; init; } = 0.15m;
        public decimal MathPenalty { get; init; } = 0.20m;
        public decimal DatePenalty { get; init; } = 0.15m;
        public decimal VatRatePenalty { get; init; } = 0.10m;
        public decimal LowConfidencePenalty { get; init; } = 0.05m;
        public static readonly GatewayConfig Default = new();
    }

    /// <summary>Line-item shape used by validation. Mirrors OcrExtractedLineItem
    /// — kept simple so callers can pass either internal or DTO objects.</summary>
    public sealed record LineItemForValidation(decimal? Quantity, decimal? UnitPrice, decimal? Amount);

    public static GatewayResult Validate(
        decimal modelConfidence,
        DateTime? documentDate,
        decimal? subTotal,
        decimal? vatAmount,
        decimal? total,
        string? vendorTaxId,
        string? buyerTaxId,
        Dictionary<string, decimal>? fieldConfidences = null,
        GatewayConfig? config = null,
        IReadOnlyList<LineItemForValidation>? lineItems = null,
        decimal? whtAmount = null,
        decimal? whtRatePercent = null,
        string? documentNumber = null,
        string? vendorName = null,
        decimal? documentDiscount = null)
    {
        config ??= GatewayConfig.Default;
        var warnings = new List<string>();
        var notes = new List<string>();
        var penalty = 0m;
        var mathConsistent = true;

        // 0. ยอดก่อน VAT บนกระดาษเป็นยอด "ก่อนหักส่วนลด" (รอบ 190 ข้อ 9)
        //
        // กระดาษไทยส่วนใหญ่พิมพ์ "รวมเงิน 1,395.00 · ส่วนลด 69.75 · VAT 92.77 · รวมทั้งสิ้น 1,418.02"
        // และ engine หยิบ "รวมเงิน" (ก่อนลด) เป็น SubTotal ⇒ ด่าน 2/2b/3 เดิมฟ้อง "คณิตศาสตร์ไม่ตรง"
        // + "สามช่องขัดกันเอง" กับ**ทุกใบที่มีส่วนลดท้ายบิล** (ใบถูกต้อง) ⇒ ติด [MATH] ห้ามอนุมัติเองทุกใบ
        // = คำเตือนที่ฟ้องใบถูก (ผู้ใช้เรียนรู้ที่จะเมิน แล้วคำเตือนจริงถูกเมินตาม)
        // ตัดสินว่าเป็นยอดก่อนลดเมื่อ **ส่วนลดที่อ่านจากกระดาษ** อธิบายส่วนต่างได้พอดีเท่านั้น
        // (ค่าที่อ่านมาจริงทั้งสี่ช่อง ไม่ใช่การแต่ง) — ไม่งั้นใช้ SubTotal ตามเดิมแล้วให้ด่านเดิมฟ้อง
        var effSub = subTotal;
        if (documentDiscount is > 0m && subTotal is > 0m && vatAmount.HasValue && total is > 0m
            && Math.Abs(subTotal.Value + vatAmount.Value - total.Value) > OcrLineReconciler.Tolerance
            && Math.Abs(subTotal.Value - documentDiscount.Value + vatAmount.Value - total.Value) <= OcrLineReconciler.Tolerance)
        {
            effSub = subTotal.Value - documentDiscount.Value;
            notes.Add($"ยอดก่อน VAT บนกระดาษ {subTotal:N2} เป็นยอดก่อนหักส่วนลด {documentDiscount:N2} "
                + $"— ฐานภาษีหลังส่วนลด = {effSub:N2}");
        }

        // 1. Date sanity: not in future, not too old
        if (documentDate.HasValue)
        {
            var now = DateTime.UtcNow;
            if (documentDate.Value > now.AddDays(2))
            {
                warnings.Add($"วันที่เอกสาร {documentDate.Value:yyyy-MM-dd} อยู่ในอนาคต");
                penalty += config.DatePenalty;
            }
            else if (documentDate.Value < now.AddYears(-7))
            {
                warnings.Add($"วันที่เอกสาร {documentDate.Value:yyyy-MM-dd} เก่ากว่า 7 ปี");
                penalty += config.DatePenalty * 0.67m; // older-date is less severe than future-date
            }
        }

        // 2. Math consistency: SubTotal + VAT ≈ Total
        if (effSub.HasValue && vatAmount.HasValue && total.HasValue)
        {
            var expected = effSub.Value + vatAmount.Value;
            var diff = Math.Abs(expected - total.Value);
            if (diff > config.MathTolerance)
            {
                warnings.Add($"คณิตศาสตร์ไม่ตรง: {effSub:N2} + {vatAmount:N2} = {expected:N2} ≠ {total:N2} (ห่าง {diff:N2})");
                penalty += config.MathPenalty;
                mathConsistent = false;
            }
        }

        // 2b. ── "ตัวเลขสามช่องขัดกันเอง" — ด่านที่ไม่ขึ้นกับ MathTolerance ──
        //
        // ที่มา (ใบจริงที่ผู้ใช้ส่งมา): SubTotal 922.44 · VAT 68.00 · Total 990.00
        //   • 922.44 + 68.00 = 990.44 ≠ 990.00 → ห่าง 0.44 ซึ่ง **น้อยกว่า** tolerance
        //     ค่าเริ่มต้น ฿2.00 ⇒ ข้อ 2 ไม่ฟ้อง
        //   • VAT 7% ของ 922.44 = 64.57 ≠ 68.00 → อัตราที่คำนวณได้ 7.37% ซึ่งยัง
        //     **อยู่ในกรอบ** 6.5–7.5% ของข้อ 3 ⇒ ข้อ 3 ก็ไม่ฟ้อง
        //   ⇒ ใบที่อย่างน้อยสองช่องอ่านผิด ผ่านทั้งสองด่านเงียบ ๆ ด้วย confidence 95%
        //
        // ทำไมต้องให้ **ทั้งสองเงื่อนไขจริงพร้อมกัน**: ใบที่มีหลายอัตรา (7% + 0% +
        // ยกเว้น) จะมี VAT ≠ 7% ของยอดรวมโดยชอบธรรม — แต่ใบพวกนั้น
        // `SubTotal + VAT = Total` **เป๊ะ** เพราะเป็นตัวเลขที่ผู้ขายพิมพ์ให้สอดคล้องกัน
        // ⇒ เงื่อนไขแรกกรองมันออกไปหมด ด่านนี้จึงฟ้องเฉพาะกระดาษที่ขัดกันเองจริง ๆ
        // ("checker ที่ฟ้องผิด = checker ที่พังแล้ว" ใช้กับคำเตือนถึงผู้ใช้ด้วย)
        //
        // ค่าคลาดเคลื่อนที่ยอมรับ: การปัดเศษให้ผลต่างได้ไม่เกิน 1 สตางค์ต่อการปัด
        // 1 ครั้ง — ผลรวมจึงยอม ฿0.02 และ VAT ยอม 1 สตางค์ต่อบรรทัด (ขั้นต่ำ ฿0.02)
        if (mathConsistent && effSub.HasValue && vatAmount.HasValue && total.HasValue
            && effSub.Value > 0m && vatAmount.Value > 0m)
        {
            var sumGap = Math.Abs(effSub.Value + vatAmount.Value - total.Value);
            var expectedVat = Math.Round(effSub.Value * 0.07m, 2, MidpointRounding.AwayFromZero);
            var vatGap = Math.Abs(vatAmount.Value - expectedVat);
            var vatSlack = Math.Max(0.02m, 0.01m * (lineItems?.Count ?? 0));
            if (sumGap > 0.02m && vatGap > vatSlack)
            {
                warnings.Add(
                    $"ตัวเลขสามช่องขัดกันเอง: {effSub:N2} + {vatAmount:N2} = {(effSub + vatAmount):N2} ≠ {total:N2} " +
                    $"และ VAT 7% ของ {effSub:N2} ควรเป็น {expectedVat:N2} ไม่ใช่ {vatAmount:N2} — อย่างน้อยหนึ่งช่องอ่านผิด");
                penalty += config.MathPenalty;
                mathConsistent = false;
            }
        }

        // 3. อัตรา VAT เทียบยอดรวม — **ไม่สมมาตรสองทิศ**
        //
        // ⚠️ เดิมฟ้องทุกครั้งที่อัตรานอกกรอบ 6.5–7.5% ทั้งสองทิศ ⇒ บิลผสม
        // 7% + ยกเว้น §81 (Makro/BigC: สินค้ามี VAT 700 + ยกเว้น 300 → VAT 49
        // → อัตรารวม 4.9%) โดน −0.10 **ทุกใบ** ทั้งที่กระดาษถูกต้องตามกฎหมาย
        // — ซึ่งขัดกับคอมเมนต์ของข้อ 2b ข้างบนที่อธิบายเคสนี้ไว้เองแล้ว
        // (ผลตรวจ 2026-09-06 · T2-03)
        //
        // ทิศที่ยังผิดจริงเสมอ: **สูงกว่า 7%** — ใบเดียวมีอัตราสูงสุดได้ 7%
        // ตามกฎหมายไทย จะเกินได้ก็ต่อเมื่ออ่านตัวเลขผิด (เช่น VAT 150 บนยอด
        // 1,000 = 15%) ⇒ ฟ้อง + หักคะแนน
        //
        // ทิศที่ **ต่ำกว่า** 6.5%: เป็นไปได้โดยชอบธรรมจากส่วนผสมยกเว้น §81 /
        // ศูนย์ §80/1 — แต่ก็เกิดได้จาก "OCR อ่าน VAT ขาดหลัก" เช่นกัน. สอง
        // เคสนี้แยกกันด้วย <c>mathConsistent</c>: ใบผสมที่ผู้ขายพิมพ์เอง
        // Sub + VAT = Total **เป๊ะ** ส่วนใบที่อ่านขาดหลักจะไม่ลงตัว (ข้อ 2/2b
        // จับไปแล้ว) ⇒ ลงตัว = ข้อสังเกต (ไม่หักคะแนน) · ไม่ลงตัว = คำเตือน
        if (effSub.HasValue && vatAmount.HasValue && effSub.Value > 0 && vatAmount.Value > 0)
        {
            var rate = vatAmount.Value / effSub.Value * 100m;
            if (rate > 7.5m)
            {
                warnings.Add($"อัตรา VAT ผิดปกติ: {rate:N1}% — สูงกว่าอัตราสูงสุดตามกฎหมาย (7%)");
                penalty += config.VatRatePenalty;
            }
            else if (rate < 6.5m)
            {
                if (mathConsistent)
                    notes.Add($"อัตรา VAT รวมทั้งใบ {rate:N1}% (ต่ำกว่า 7%) — น่าจะมีสินค้ายกเว้น §81 "
                        + "หรืออัตรา 0% §80/1 ปนอยู่ ตรวจอัตรารายบรรทัดอีกครั้ง");
                else
                {
                    warnings.Add($"อัตรา VAT ผิดปกติ: {rate:N1}% (ปกติ 7%) และตัวเลขหัวใบไม่ลงตัว "
                        + "— อาจอ่าน VAT ขาดหลัก");
                    penalty += config.VatRatePenalty;
                }
            }
        }

        // 4. Tax ID checksums — only penalize when extracted TaxId is 13 digits
        if (!string.IsNullOrEmpty(vendorTaxId) && new string(vendorTaxId.Where(char.IsDigit).ToArray()).Length == 13
            && !ValidateThaiTaxId(vendorTaxId))
        {
            warnings.Add($"เลขผู้เสียภาษีผู้ขาย {vendorTaxId} checksum ไม่ผ่าน");
            penalty += config.TaxIdPenalty;
        }
        if (!string.IsNullOrEmpty(buyerTaxId) && new string(buyerTaxId.Where(char.IsDigit).ToArray()).Length == 13
            && !ValidateThaiTaxId(buyerTaxId))
        {
            warnings.Add($"เลขผู้เสียภาษีผู้ซื้อ {buyerTaxId} checksum ไม่ผ่าน");
            penalty += config.TaxIdPenalty * 0.67m; // buyer mismatch less severe than vendor
        }

        // 5. Per-field confidence penalty
        //
        // ⚠️ เดิมมองหาชื่อชุดของ Azure ("InvoiceTotal"/"InvoiceId"/"VendorName"/
        // "VendorTaxId") ซึ่งมีเฉพาะบนเส้นทาง Azure DI ⇒ ด่านนี้ **ไม่เคยทำงาน
        // บนเส้นทาง Tesseract/Python เลย** ทั้งที่นั่นคือเส้นทางที่ความมั่นใจ
        // ต่ำที่สุดและควรถูกด่านนี้จับมากที่สุด — แปลงชื่อผ่านคำศัพท์กลาง
        // (Helpers/OcrFieldKeys.cs) ก่อนเทียบ ทุกเส้นทางจึงถูกตรวจเท่ากัน
        if (fieldConfidences != null)
        {
            var canon = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in fieldConfidences)
            {
                var ck = Accounting.Helpers.OcrFieldKeys.Canonical(k);
                if (!canon.TryGetValue(ck, out var prev) || v > prev) canon[ck] = v;
            }
            foreach (var critical in Accounting.Helpers.OcrFieldKeys.CriticalForTaxInvoice)
            {
                if (canon.TryGetValue(critical, out var c) && c < 0.5m)
                {
                    warnings.Add($"{critical} ความมั่นใจต่ำ ({c:P0})");
                    penalty += config.LowConfidencePenalty;
                }
            }
        }

        // 6. Total must be positive
        if (total.HasValue && total.Value < 0)
        {
            warnings.Add($"ยอดรวมติดลบ ({total:N2})");
            penalty += config.MathPenalty;
        }

        // 7. Σ บรรทัด ↔ หัวใบ — **ใช้ตัวจำแนกตัวเดียวกับตอนสร้างเอกสาร**
        //
        // ⚠️ เดิมเทียบ Σ บรรทัดกับ `SubTotal` ตรง ๆ ⇒ ใบที่ราคาต่อหน่วย
        // **รวม VAT แล้ว** (IKEA/ค้าปลีก/ร้านอาหาร: บรรทัด 1,396 · sub 1,304.67)
        // ห่าง 91.33 → ฟ้อง + −0.15 + `mathConsistent=false` บนใบที่ตัวเลข
        // ถูกทุกช่อง ⇒ 0.95 − 0.15 = 0.80 ตกเกณฑ์ auto-create 0.85
        // (ผลตรวจ 2026-09-06 · T2-16)
        //
        // ที่แย่กว่าคือ `CreateDocumentFromScanAsync` **รู้จักเคสนี้อยู่แล้ว**
        // ผ่าน Helpers/OcrLineReconciler — ด่านกับเส้นทำงานอนุมานเรื่องเดียวกัน
        // คนละสูตร = สำเนามือที่ drift แน่นอน. ตอนนี้เรียกตัวเดียวกัน:
        // เคส B (ตรง SubTotal) / A (ราคารวม VAT) / C (ส่วนลดที่กระดาษพิมพ์ไว้)
        // = ลงตัว ไม่ฟ้อง · LinesShort / Ambiguous = ฟ้องจริง
        if (lineItems != null && lineItems.Count > 0 && effSub.HasValue)
        {
            var withAmounts = lineItems.Where(i => i.Amount.HasValue).ToList();
            if (withAmounts.Count > 0)
            {
                var lineSum = withAmounts.Sum(i => i.Amount!.Value);
                var recon = Accounting.Helpers.OcrLineReconciler.Classify(
                    lineSum, effSub.Value, vatAmount ?? 0m, total ?? 0m, documentDiscount ?? 0m);
                switch (recon.Case)
                {
                    case Accounting.Helpers.OcrLineReconcileCase.PricesIncludeVat:
                    case Accounting.Helpers.OcrLineReconcileCase.DiscountOnSubTotal:
                    case Accounting.Helpers.OcrLineReconcileCase.DiscountOnTotal:
                    // (E) ราคารวม VAT + ส่วนลดท้ายบิล — ลงตัวกับหัวใบแล้ว (รอบ 190) · ถ้าไม่ใส่ในลิสต์นี้
                    // เคสจะตกไปไม่มีทั้ง note และ warning = ผู้ใช้ไม่รู้ว่าระบบหักส่วนลดให้
                    case Accounting.Helpers.OcrLineReconcileCase.DiscountOnTotalInclVat:
                        notes.Add(recon.Note);
                        break;
                    case Accounting.Helpers.OcrLineReconcileCase.LinesShort:
                    case Accounting.Helpers.OcrLineReconcileCase.Ambiguous:
                        warnings.Add(recon.Note);
                        penalty += config.MathPenalty * 0.75m;
                        mathConsistent = false;
                        break;
                }
            }

            // Per-line: Quantity × UnitPrice should ≈ Amount
            var brokenLines = 0;
            foreach (var item in lineItems)
            {
                if (item.Quantity.HasValue && item.UnitPrice.HasValue && item.Amount.HasValue)
                {
                    var expected = item.Quantity.Value * item.UnitPrice.Value;
                    if (Math.Abs(expected - item.Amount.Value) > config.MathTolerance)
                        brokenLines++;
                }
            }
            if (brokenLines > 0)
            {
                warnings.Add($"พบรายการที่ Qty × UnitPrice ≠ Amount จำนวน {brokenLines} บรรทัด");
                penalty += config.MathPenalty * 0.5m;
            }
        }

        // 8. WHT formula sanity: ยอดที่**พิมพ์บนกระดาษ** ≈ SubTotal × Rate / 100
        //
        // ⚠️ ด่านนี้มีความหมายก็ต่อเมื่อ <paramref name="whtAmount"/> เป็นยอดที่
        // **อ่านมาจากกระดาษ** — ผู้เรียกเคยส่งผลของสูตรเดียวกันนี้เข้ามา ทำให้
        // เทียบสูตรกับตัวเอง ⇒ ผ่านทุกครั้งตลอดกาล (ผลตรวจ 2026-09-06 · T2-02)
        // ดู Helpers/PaperWhtReader ที่ฝั่งผู้เรียก
        //
        // เคสที่ด่านนี้จับได้จริง: ผู้ขายคิด 3% จากยอด**รวม VAT** (ต้องคิดจากยอด
        // ก่อน VAT) · OCR อ่านอัตราเป็น 5% ทั้งที่กระดาษเขียน 3% · ยอดหักถูก
        // คีย์มือผิดหลัก
        if (whtAmount.HasValue && whtRatePercent.HasValue && effSub.HasValue
            && effSub.Value > 0 && whtRatePercent.Value > 0)
        {
            var expectedWht = Math.Round(effSub.Value * whtRatePercent.Value / 100m, 2,
                MidpointRounding.AwayFromZero);
            var diff = Math.Abs(expectedWht - whtAmount.Value);
            if (diff > config.MathTolerance)
            {
                warnings.Add($"ภาษีหัก ณ ที่จ่ายบนกระดาษ {whtAmount:N2} ไม่ตรงสูตร "
                    + $"({whtRatePercent}% × {effSub:N2} = {expectedWht:N2}) — "
                    + "ตรวจว่าอัตราถูกอ่านถูกไหม และผู้ขายคิดจากยอดก่อน VAT หรือหลัง VAT");
                penalty += config.MathPenalty * 0.5m;
            }
        }

        // 9. Missing critical fields — penalize each one heavily so a scan
        // that can't extract date/number/total can't sail past the auto-
        // create threshold on TYPE-detection confidence alone. Previously
        // these were flag-only; auto-create then fired on a document with
        // every field null (just because "ใบกำกับภาษี" appeared on the
        // page and ParseThaiDocument set Confidence = 0.95 on that marker).
        const decimal MissingCriticalPenalty = 0.20m;
        if (string.IsNullOrWhiteSpace(documentNumber))
        {
            warnings.Add("ไม่พบเลขที่เอกสาร — กรุณาตรวจสอบ");
            penalty += MissingCriticalPenalty;
        }
        if (!documentDate.HasValue)
        {
            warnings.Add("ไม่พบวันที่เอกสาร — กรุณาตรวจสอบ");
            penalty += MissingCriticalPenalty;
        }
        if (string.IsNullOrWhiteSpace(vendorName))
        {
            warnings.Add("ไม่พบชื่อผู้ขาย — กรุณาตรวจสอบ");
            penalty += MissingCriticalPenalty;
        }
        if (!total.HasValue || total.Value == 0)
        {
            warnings.Add("ไม่พบยอดรวม — กรุณาตรวจสอบ");
            penalty += MissingCriticalPenalty;
        }

        // Cap penalty
        if (penalty > config.MaxPenalty) penalty = config.MaxPenalty;

        var adjusted = Math.Max(0m, Math.Min(1m, modelConfidence - penalty));
        return new GatewayResult(adjusted, warnings, mathConsistent, notes);
    }

    private static bool ValidateThaiTaxId(string taxId)
    {
        if (string.IsNullOrEmpty(taxId)) return false;
        var digits = new string(taxId.Where(char.IsDigit).ToArray());
        if (digits.Length != 13) return false;

        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (digits[i] - '0') * (13 - i);

        var checkDigit = (11 - (sum % 11)) % 10;
        return checkDigit == (digits[12] - '0');
    }
}
