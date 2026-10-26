namespace Accounting.Helpers;

/// <summary>
/// <b>แปลงยอดสกุลเอกสาร → บาท ตัวเดียวกับที่ JE ใช้</b> (รอบ 203 ทีม F3 · PP36_REVIEW E-4)
/// <para>JE ของเอกสาร (<c>AutoPostToJournalAsync</c> · <c>Conv</c>) แปลงทุกยอดด้วย <c>Document.ExchangeRate</c> ปัด 2 ตำแหน่ง AwayFromZero ·
/// เส้นภาษีที่อ่านยอดจากเอกสารตรง ๆ (ยอดค้าง/รายงาน/รับรู้ ภ.พ.36) เคยใช้ยอดสกุลเอกสารดิบ ⇒ ใบ USD ที่อัตรา 36 นำส่ง "70" แทน 2,520 บาท ·
/// <c>DocumentService.ToGlAmount</c> เรียกตัวนี้ (สูตรเดียว ห้ามเขียนซ้ำ)</para>
/// </summary>
public static class DocumentFx
{
    /// <summary>ยอดเป็นบาท · อัตรา ≤ 0 (ข้อมูลเก่า) ถือเป็น 1</summary>
    public static decimal ToBaht(decimal docAmount, decimal exchangeRate)
    {
        var fx = exchangeRate <= 0m ? 1m : exchangeRate;
        return fx == 1m ? docAmount : Math.Round(docAmount * fx, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>ทศนิยมของอัตราที่เก็บ (<c>Documents."ExchangeRate" numeric(18,6)</c>)</summary>
    public const int RateDecimals = 6;

    /// <summary>ที่มาของอัตราแลกเปลี่ยนบนเอกสาร (<c>Document.ExchangeRateSource</c>) — null = ข้อมูลเก่า/ไม่ทราบ</summary>
    public const string SourceBot = "BOT";
    /// <summary>ผู้ใช้กรอก/ยืนยันอัตราเอง</summary>
    public const string SourceUser = "User";
    /// <summary>คำนวณจากยอดบาทที่เอกสารพิมพ์ไว้เอง (<c>Document.DocumentThbBase</c>)</summary>
    public const string SourceDocument = "Document";

    /// <summary>ป้ายไทยของที่มาอัตรา (เซิร์ฟเวอร์เป็นเจ้าของข้อความ — หน้าเว็บแสดงอย่างเดียว) · null/ไม่รู้จัก ⇒ null</summary>
    public static string? SourceLabel(string? source) => NormalizeSource(source) switch
    {
        SourceBot => "อัตรา ธปท. ตามวันที่เอกสาร",
        SourceUser => "ผู้ใช้กรอก/ยืนยัน",
        SourceDocument => "คำนวณจากยอดเงินบาทที่พิมพ์บนเอกสาร",
        _ => null,
    };

    /// <summary>ชุดค่าที่รับได้ของ <c>ExchangeRateSource</c> (ค่าอื่น/ว่าง ⇒ null)</summary>
    private static string? NormalizeSource(string? source) => source?.Trim() switch
    {
        "BOT" or "bot" => SourceBot,
        "User" or "user" => SourceUser,
        "Document" or "document" => SourceDocument,
        _ => null,
    };

    /// <summary>
    /// <b>อัตราจากยอดบาทที่เอกสารพิมพ์ไว้</b> (ทีม F · per-line WHT) — ใบแจ้งหนี้ต่างสกุลที่มี "ยอดเทียบเท่าเงินบาท" บนกระดาษ ⇒ ยึดยอดบาทนั้น
    /// (ฐาน VAT/WHT) และอัตราที่ทุกเส้น (JE · ภ.พ.30 · ภ.พ.36) ใช้ต้องเป็นอัตราที่ทำให้ฐานสกุลเอกสารแปลงแล้วได้ยอดบาทนั้น ·
    /// ปัด <see cref="RateDecimals"/> ตำแหน่ง AwayFromZero · ฐานสกุลเอกสาร/ยอดบาท ≤ 0 ⇒ null (ไม่รู้ = ไม่แต่ง)
    /// </summary>
    public static decimal? RateFromPrintedBaht(decimal foreignAmount, decimal printedBaht)
    {
        if (foreignAmount <= 0m || printedBaht <= 0m) return null;
        return Math.Round(printedBaht / foreignAmount, RateDecimals, MidpointRounding.AwayFromZero);
    }

    /// <summary>ผลต่างสูงสุดที่ยอมให้ระหว่าง "ฐานสกุลเอกสาร × อัตรา 6 ตำแหน่ง" กับยอดบาทที่พิมพ์ (บาท) — ระดับเศษสตางค์เดียว</summary>
    public const decimal PrintedBahtRoundTripTolerance = 0.01m;

    /// <summary>อัตราที่เก็บ (6 ตำแหน่ง) แปลงฐานสกุลเอกสารกลับได้ยอดบาทที่พิมพ์ไหม (± <see cref="PrintedBahtRoundTripTolerance"/>) — ไม่ได้ ⇒ JE/ภ.พ.30 กับ
    /// 50 ทวิ จะเล่าคนละตัวเลข (ฝ่ายค้านทีม F ข้อ 1: VND 98,765,432 ↔ 135,432.10 บาท ⇒ อัตรา 0.001371 ⇒ 135,407.41)</summary>
    public static bool PrintedBahtRoundTrips(decimal foreignAmount, decimal exchangeRate, decimal printedBaht)
        => Math.Abs(ToBaht(foreignAmount, exchangeRate) - printedBaht) <= PrintedBahtRoundTripTolerance;

    /// <summary>ข้อความปฏิเสธเมื่ออัตรา 6 ตำแหน่งแปลงกลับไม่ได้ยอดบาทที่พิมพ์</summary>
    public static string RoundTripRefusal(decimal foreignAmount, decimal rate, decimal printedBaht)
        => $"ยอดบาทตามเอกสาร {printedBaht:N2} บาท แปลงเป็นอัตราแลกเปลี่ยน (เก็บ {RateDecimals} ตำแหน่ง = {rate:0.######}) แล้วคูณกลับได้ "
           + $"{ToBaht(foreignAmount, rate):N2} บาท (ต่าง {ToBaht(foreignAmount, rate) - printedBaht:N2}) — สกุลเงินหน่วยเล็กมากใช้ยอดบาทนี้เป็นฐานไม่ได้ "
           + "ให้เว้นช่อง \"ยอดบาทตามเอกสาร\" แล้วกรอกอัตราแลกเปลี่ยนเอง (ระบบจะคิดฐานบาทจากอัตรา)";

    /// <summary>ผลตัดสินอัตรา/ที่มา/ฐานบาทของเอกสารตอนสร้าง · <see cref="Error"/> ≠ null ⇒ ผู้เรียกล้มดัง (400 ข้อความไทย)</summary>
    // ───────────────────────── คำตัดสินเจ้าของข้อ 162 (Q2 · 2026-10-26) — ตั้งค่าได้ต่อบริษัท · ค่าตั้งต้น = เดิมทุกสตางค์ ─────────────────────────

    /// <summary>อัตราที่ใช้กับ<b>ฐานหัก ณ ที่จ่าย</b>ของงวดจ่าย (50 ทวิ รายงวด + ขา 2191x ใน <c>CreatePaymentJournalAsync</c>) — <b>ตัวตัดสินเดียว</b> ·
    /// <see cref="Models.Enums.WhtFxRateBasis.DocumentDate"/> (ค่าตั้งต้น) ⇒ อัตราเอกสารเสมอ (เดิม) ·
    /// <see cref="Models.Enums.WhtFxRateBasis.PaymentDate"/> ⇒ อัตราวันจ่ายเมื่อมี (&gt; 0) ไม่งั้นอัตราเอกสาร (ไม่แต่งอัตรา) · ใบบาท (อัตรา 1) ⇒ 1 เสมอ</summary>
    public static decimal WhtRateFor(Models.Enums.WhtFxRateBasis basis, decimal documentRate, decimal? paymentRate)
    {
        var docFx = documentRate <= 0m ? 1m : documentRate;
        if (docFx == 1m) return 1m;
        return basis == Models.Enums.WhtFxRateBasis.PaymentDate && paymentRate is decimal p && p > 0m ? p : docFx;
    }

    /// <summary>ป้ายที่มาของอัตราฐานภาษีบน 50 ทวิ (เก็บลง <c>WithholdingTaxCert.WhtFxRateBasis</c> — ทุกใบต้องบอกที่มา)</summary>
    public static string WhtRateBasisLabel(Models.Enums.WhtFxRateBasis basis, decimal documentRate, decimal? paymentRate)
        => WhtRateFor(basis, documentRate, paymentRate) == (documentRate <= 0m ? 1m : documentRate)
            ? "อัตราของเอกสาร (วันที่ใบ)"
            : "อัตรา ณ วันจ่าย (ป.132/2548)";

    /// <summary>อัตรา ธปท. ตัวไหนที่ใช้เมื่อไม่มีอัตราจากผู้ใช้ — <b>ตัวเลือกเดียว</b>ของทุกผู้เรียก ธปท. (เอกสาร · OCR) ·
    /// ค่าตั้งต้น <see cref="Models.Enums.BotRateType.Mid"/> = อัตรากลาง (เดิม) · ตัวที่เลือกไม่มีค่า (≤ 0) ⇒ ตกไปอัตรากลาง (ไม่คืนศูนย์ให้ GL)</summary>
    public static decimal PickBotRate(Models.Enums.BotRateType type, decimal mid, decimal buyingTransfer, decimal selling)
    {
        var picked = type switch
        {
            Models.Enums.BotRateType.AverageBuying => buyingTransfer,
            Models.Enums.BotRateType.AverageSelling => selling,
            _ => mid,
        };
        return picked > 0m ? picked : mid;
    }

    public readonly record struct Decision(decimal Rate, string? Source, decimal? ThbBase, string? Error);

    /// <summary>
    /// <b>อัตรา + ที่มา + ฐานบาทของเอกสารตอนสร้าง — ตัวตัดสินเดียว</b> (ทีม F) · เรียกหลังคิดยอดบรรทัดแล้ว (ต้องรู้ฐานสกุลเอกสาร)
    /// <list type="bullet">
    /// <item>ใบบาท ⇒ (1, null, null) · ส่งยอดบาทตามเอกสารมา = ผิด (ช่องนี้มีความหมายเฉพาะใบต่างสกุล)</item>
    /// <item>ส่งยอดบาทตามเอกสาร ⇒ ต้อง &gt; 0 และฐานสกุลเอกสาร &gt; 0 · อัตรา = <see cref="RateFromPrintedBaht"/> · ที่มา Document ·
    ///   ใบลด/เพิ่มหนี้ที่ใช้อัตราของใบเดิม (<paramref name="rateLockedToSource"/>) ⇒ คงอัตราใบเดิม (ตัดหนี้ต้องเท่ายอดที่ตั้ง) เก็บยอดบาทไว้เป็นฐาน WHT</item>
    /// <item>ไม่ส่ง + ใบต้นทางมียอดบาทตามเอกสารและแปลงมาเต็มจำนวน (<paramref name="inheritedThbBase"/>) ⇒ สืบทอด (ที่มา/ฐานเดียวกับใบต้นทาง)</item>
    /// <item>ไม่ส่ง ⇒ ที่มา = ที่ส่งมา (BOT/User) · ส่งอัตราแต่ไม่บอกที่มา = User · ไม่ส่งอัตรา (ระบบดึง ธปท.) = BOT · สืบทอดอัตราใบเดิม = ที่มาของใบเดิม</item>
    /// </list>
    /// </summary>
    public static Decision Decide(string? currency, decimal resolvedRate, bool rateFromRequest, string? requestedSource,
        decimal? requestedThbBase, decimal docSubTotal, bool rateLockedToSource, string? sourceRateSource,
        decimal? inheritedThbBase)
    {
        var isThb = string.IsNullOrWhiteSpace(currency) || string.Equals(currency.Trim(), "THB", StringComparison.OrdinalIgnoreCase);
        if (isThb)
            return requestedThbBase is decimal tb && tb != 0m
                ? new(1m, null, null, "เอกสารสกุลบาทไม่ต้องระบุ \"ยอดบาทตามเอกสาร\" — ช่องนี้ใช้กับใบสกุลต่างประเทศที่พิมพ์ยอดเทียบเท่าเงินบาทไว้เท่านั้น")
                : new(1m, null, null, null);
        if (requestedThbBase is decimal printed)
        {
            if (printed <= 0m)
                return new(resolvedRate, null, null, "ยอดบาทตามเอกสาร (ก่อน VAT) ต้องมากกว่า 0 — เว้นว่างถ้าเอกสารไม่ได้พิมพ์ยอดเงินบาทไว้");
            if (rateLockedToSource)
                return new(resolvedRate, sourceRateSource, printed, null);
            if (RateFromPrintedBaht(docSubTotal, printed) is not decimal derived)
                return new(resolvedRate, null, null, "คำนวณอัตราจากยอดบาทตามเอกสารไม่ได้ — ยอดก่อน VAT ของรายการต้องมากกว่า 0");
            if (!PrintedBahtRoundTrips(docSubTotal, derived, printed))
                return new(resolvedRate, null, null, RoundTripRefusal(docSubTotal, derived, printed));
            return new(derived, SourceDocument, printed, null);
        }
        if (inheritedThbBase is decimal inherited && inherited > 0m)
            return new(resolvedRate, sourceRateSource ?? SourceDocument, inherited, null);
        var source = rateLockedToSource
            ? sourceRateSource
            : rateFromRequest ? (NormalizeSource(requestedSource) is string s && s != SourceDocument ? s : SourceUser) : SourceBot;
        return new(resolvedRate, source, null, null);
    }

}
