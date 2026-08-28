namespace Accounting.Helpers;

/// <summary>
/// "ยอดภาษีที่ถูกหัก" จากหนังสือรับรอง 50 ทวิ ที่สแกนเข้ามา — ตัวตัดสินกลาง
///
/// ═══ ที่มา (บั๊กจริง) ═══
/// <c>OcrScanResult</c> ไม่มีช่อง "ยอดภาษีที่ถูกหัก" ตรง ๆ มีแค่ <c>HasWht</c>
/// กับ <c>WhtRate</c> โค้ดเดิมจึงเขียนว่า
/// <code>whtAmount = derived > 0 ? derived : (ExtractedTotalAmount ?? 0)</code>
/// = **เอายอดรวมทั้งใบมาเป็นยอดภาษี** เมื่ออ่านฐาน/อัตราไม่ครบ ⇒ ใบ 50 ทวิ
/// ยอด 1,070 หัก 3% ถูกบันทึกเป็นเครดิต CIT <b>1,070 บาท</b> แทนที่จะเป็น 30
/// และ <c>IncomeAmount</c> ถูกเขียน 0 ⇒ อัตราที่คำนวณกลับได้เป็นอนันต์
/// ไม่มีด่านไหนจับได้ แถวนั้นสถานะ <c>Received</c> ⇒ หักภาษีจริงใน ภ.ง.ด.50 ทันที
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item>ฐาน × อัตรา ครบทั้งคู่ → คำนวณตรง ๆ (แม่นที่สุด)</item>
/// <item>ไม่ครบ → อ่าน "รวมเงินภาษีที่หักนำส่ง (ตัวอักษร)" จากกระดาษ
///   (<see cref="ThaiAmountInWords"/>) พร้อมด่านกัน false positive:
///   ยอดภาษีต้อง <b>น้อยกว่า</b> ยอดจ่าย เสมอ</item>
/// <item>อ่านไม่ได้ทั้งสองทาง → <see cref="WhtCertAmount.IsUnknown"/> = true
///   ผู้เรียกต้องบันทึกเป็น Pending (ยังไม่ถูกนับเป็นเครดิต) ห้ามเดายอด</item>
/// </list>
/// </summary>
public static class WhtCertAmountResolver
{
    /// <summary>ที่มาของยอดที่ได้ — ใช้เขียนหมายเหตุให้ผู้ใช้รู้ว่าเชื่อได้แค่ไหน</summary>
    public enum AmountSource
    {
        /// <summary>คำนวณจาก ฐาน × อัตรา ที่อ่านได้ครบ</summary>
        Computed,
        /// <summary>อ่านจากจำนวนเงินตัวอักษรบนกระดาษ</summary>
        AmountInWords,
        /// <summary>อ่านไม่ได้ — ไม่รู้</summary>
        Unknown,
    }

    public readonly record struct WhtCertAmount(
        decimal Amount,
        decimal Rate,
        AmountSource Source)
    {
        /// <summary>ยอดภาษีอ่านไม่ได้เลย — ห้ามตั้งเป็นเครดิตที่ใช้ได้</summary>
        public bool IsUnknown => Source == AmountSource.Unknown;

        /// <summary>ได้ยอดมาแต่ไม่ได้มาจากการคำนวณ — ผู้ใช้ควรตรวจก่อนใช้</summary>
        public bool NeedsReview => Source != AmountSource.Computed;
    }

    /// <param name="subTotal">ฐานภาษี (จำนวนเงินที่จ่าย) ที่ OCR อ่านได้</param>
    /// <param name="rate">อัตราที่หัก (%) ที่ OCR อ่านได้</param>
    /// <param name="totalAmount">ยอดรวมทั้งใบ — ใช้เป็น**เพดาน**เท่านั้น ห้ามใช้เป็นยอดภาษี</param>
    /// <param name="rawText">ข้อความทั้งหน้าจาก OCR</param>
    public static WhtCertAmount Resolve(
        decimal? subTotal, decimal? rate, decimal? totalAmount, string? rawText)
    {
        var baseAmount = subTotal ?? 0m;
        var rateValue = rate ?? 0m;

        if (baseAmount > 0m && rateValue > 0m)
        {
            var computed = Math.Round(baseAmount * rateValue / 100m, 2, MidpointRounding.AwayFromZero);
            if (computed > 0m)
                return new WhtCertAmount(computed, rateValue, AmountSource.Computed);
        }

        // ยอดจ่ายที่ใช้เป็นเพดาน — ยอดรวมทั้งใบ ถ้าไม่มีใช้ฐานภาษี
        var gross = totalAmount ?? subTotal ?? 0m;
        var fromWords = ThaiAmountInWords.FindInText(rawText);
        if (fromWords is > 0m && (gross <= 0m || fromWords < gross))
        {
            var amount = Math.Round(fromWords.Value, 2, MidpointRounding.AwayFromZero);
            var impliedRate = baseAmount > 0m
                ? Math.Round(amount / baseAmount * 100m, 2, MidpointRounding.AwayFromZero)
                : 0m;
            return new WhtCertAmount(amount, rateValue > 0m ? rateValue : impliedRate,
                AmountSource.AmountInWords);
        }

        return new WhtCertAmount(0m, rateValue, AmountSource.Unknown);
    }
}
