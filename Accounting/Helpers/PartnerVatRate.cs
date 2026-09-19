namespace Accounting.Helpers;

/// <summary>
/// **อัตรา VAT ของเอกสารที่ระบบคู่ค้ายิงเข้ามา (`/api/integration/*`) — ตัวตัดสินตัวเดียว**
///
/// ═══ ที่มา (บั๊กจริง · DECISION_AUDIT_2026-09-18 §3 D8-4) ═══
/// <para><c>IntegrationService</c> เคยให้ <c>request.VatRate</c> ถอยไปหาเลข 7 ตายตัว กระจาย
/// <b>6 จุด</b> และ <c>BuildDocumentLinesAsync(defaultVatRate = 7)</c> อีก 2 จุด
/// โดย<b>ไม่เคยอ่านสถานะจดทะเบียน VAT ของบริษัทเลย</b> ⇒ คู่ค้าที่ไม่ส่ง
/// <c>vatRate</c> มาทำให้ tenant ที่ <b>ไม่ได้จด VAT</b> ออกเอกสารที่มี VAT 7%
/// = เรียกเก็บภาษีโดยไม่มีสิทธิ์ (§90/2 · โทษอาญา + ต้องนำส่ง VAT ที่เก็บมา)
/// และยอด ภ.พ.30 เพี้ยน</para>
///
/// ═══ สองฝั่งไม่ใช่กติกาเดียวกัน — อย่ายุบรวม ═══
/// <list type="bullet">
/// <item><b>ฝั่งที่ "เราออกเอกสาร"</b> (ใบกำกับ/ใบเพิ่มหนี้/ใบลดหนี้) = <b>ภาษีขาย</b>
///   — สิทธิ์เรียกเก็บเป็นของบริษัทเรา ⇒ ผูกกับ <see cref="OutputVatRate.ForCompany"/>
///   ตรง ๆ และ<b>ปฏิเสธ</b>เมื่อคู่ค้าสั่งให้เก็บ VAT ทั้งที่เรายังไม่จดทะเบียน</item>
/// <item><b>ฝั่งที่ "เรารับเอกสาร"</b> (ค่าใช้จ่าย/ใบสำคัญจ่าย/ใบแทนฯ) = <b>ภาษีซื้อ</b>
///   — VAT บนกระดาษเป็นของ<b>ผู้ขาย</b> ไม่ใช่ของเรา. บริษัทที่ไม่จด VAT ก็ยัง
///   <b>จ่าย</b> VAT ให้ผู้ขายตามปกติ ⇒ <b>ห้ามบังคับเป็น 0</b> (ยอดที่ต้องจ่าย
///   ผู้ขายจะหายไป 7% เงียบ ๆ). สิ่งที่ต้องบังคับคือ <b>เคลมไม่ได้</b> —
///   VAT รวมเป็นต้นทุน/ค่าใช้จ่าย ไม่เข้า 11610/11640 ซึ่งเป็นกติกาเดียวกับ
///   เส้นคีย์มือใน <c>DocumentService.CreateDocumentAsync</c> เป๊ะ ๆ
///   (ดู <see cref="InputVatClaimable"/>)</item>
/// </list>
///
/// <para>⚠️ <b>ทิศของการล้ม</b> (DECISION_DOCTRINE §1 G5): ฝั่งขายเลือก "ปฏิเสธพร้อม
/// บอกวิธีแก้" ไม่ใช่ "บังคับเป็น 0 เงียบ ๆ" เพราะถ้าเงียบ ระบบต้นทางจะยังเชื่อว่า
/// เก็บ VAT จากลูกค้าได้ 7% ⇒ เงินที่เก็บจริงกับเอกสารไม่ตรงกันโดยไม่มีใครเห็น
/// ส่วนการปฏิเสธเห็นทันทีใน sync log และคู่ค้าแก้เองได้ใน 1 นาที</para>
/// </summary>
public static class PartnerVatRate
{
    /// <summary>อัตราภาษีมูลค่าเพิ่มตามกฎหมายไทยปัจจุบัน — ใช้ได้ **ที่เดียว** คือเป็น
    /// ค่าถอยหลังสุดท้ายเมื่อบริษัท "ยังไม่เคยตั้งค่า" อัตราของตัวเอง (แถว
    /// <c>CompanySettings</c> ยังไม่มี/เป็น 0) เพื่อไม่ให้บริษัทเดิมพฤติกรรมเปลี่ยน
    /// ⇒ ห้ามพิมพ์ <c>7</c> ลอย ๆ ในโค้ดเส้น integration อีก</summary>
    public const decimal StatutoryRate = 7m;

    /// <summary>เหตุผลมาตรฐานเมื่อบริษัทไม่ได้จด VAT จึงเคลมภาษีซื้อไม่ได้
    ///
    /// <para>⚠️ ข้อความเดียวกันนี้ยัง<b>ฝังเป็น literal</b> อยู่ใน
    /// <c>DocumentService.CreateDocumentAsync</c>/<c>UpdateDocumentAsync</c>
    /// (เส้นคีย์มือ) — ควรย้ายมาอ้างที่นี่ในคอมมิตที่แตะไฟล์นั้น</para></summary>
    public const string NotVatRegisteredReason =
        "บริษัทไม่ได้จดทะเบียน VAT — เคลมภาษีซื้อไม่ได้ (รวมเป็นต้นทุน)";

    /// <summary>ผลการตัดสินอัตราภาษี<b>ขาย</b>ของเอกสารที่เราเป็นผู้ออก</summary>
    /// <param name="Rate">อัตราที่ใช้ได้จริง (ใช้ต่อเมื่อ <see cref="Rejected"/> = false)</param>
    /// <param name="Error">ข้อความปฏิเสธเป็นภาษาไทยที่คู่ค้าอ่านแล้วแก้ได้ทันที — null = ผ่าน</param>
    public readonly record struct IssuedDecision(decimal Rate, string? Error)
    {
        public bool Rejected => Error != null;
    }

    /// <summary>
    /// อัตราภาษี<b>ขาย</b>ของเอกสารที่เราออกให้ลูกค้า (ใบกำกับ · ใบเพิ่มหนี้ · ใบลดหนี้)
    ///
    /// <list type="number">
    /// <item>คู่ค้าไม่ส่งอัตรามา → อัตราของบริษัทตาม <see cref="OutputVatRate.ForCompany"/>
    ///   (ไม่จดทะเบียน = 0 · ไม่ใช่ 7 ที่ hardcode ไว้)</item>
    /// <item>คู่ค้าส่ง 0 หรือติดลบมาเอง → <b>เคารพเสมอ</b> — สินค้ายกเว้น §81 และ
    ///   ส่งออกอัตรา 0 (§80/1) เป็นเคสจริงที่ห้ามถูกตัวแนะนำทับ
    ///   (<c>VatRate = -1</c> เป็นรหัส "ยกเว้น" ของเรพนี้ · ดู <c>DocumentVatFallback</c>)</item>
    /// <item>คู่ค้าส่งอัตรา &gt; 0 แต่บริษัท<b>ยังไม่จดทะเบียน</b> → <b>ปฏิเสธ</b> (§90/2)</item>
    /// <item>คู่ค้าส่งอัตรา &gt; 0 และบริษัทจดทะเบียนแล้ว → ใช้ตามที่ส่งมา
    ///   (อัตราผสมในใบเดียว/อัตราย้อนหลังเป็นเคสจริง ห้ามบังคับเป็นอัตราบริษัท)</item>
    /// </list>
    /// </summary>
    public static IssuedDecision ForIssuedDocument(
        decimal? requestedRate, bool vatRegistered, decimal companyVatRate)
    {
        if (requestedRate is null)
            return new IssuedDecision(OutputVatRate.ForCompany(vatRegistered, companyVatRate), null);

        var rate = requestedRate.Value;
        if (rate <= 0m) return new IssuedDecision(rate, null);
        if (!vatRegistered) return new IssuedDecision(0m, BlockedMessage(rate));
        return new IssuedDecision(rate, null);
    }

    /// <summary>ข้อความปฏิเสธ — บอก "ผิดเพราะอะไร" + "ทำอะไรต่อได้ 2 ทาง"
    /// (กฎเหล็ก #4 F2 ข้อ 8: เข้มขึ้นต้องมีทางไปต่อของผู้ใช้)</summary>
    public static string BlockedMessage(decimal rate) =>
        $"บริษัทยังไม่ได้จดทะเบียนภาษีมูลค่าเพิ่ม แต่ระบบต้นทางส่งอัตรา VAT {rate:0.##}% มา — " +
        "ผู้ไม่จดทะเบียนเรียกเก็บ VAT ไม่ได้ (§90/2) และต้องนำส่ง VAT ที่เรียกเก็บทั้งจำนวน. " +
        "แก้ได้ 2 ทาง: (1) ส่ง vatRate = 0 มาแทน หรือ " +
        "(2) ถ้าจดทะเบียนแล้ว เปิด \"จดทะเบียนภาษีมูลค่าเพิ่ม\" ในหน้าตั้งค่าระบบบัญชีของ NextAcc";

    /// <summary>
    /// อัตราภาษี<b>ซื้อ</b>ของเอกสารที่ผู้ขายออกให้เรา — คู่ค้าส่งมาเท่าไรใช้เท่านั้น
    /// (รวม 0) · ไม่ส่งมา = อัตราตั้งต้นของบริษัท (<c>CompanySettings.DefaultVatRate</c>)
    ///
    /// <para><b>ห้าม</b>เอาสถานะจดทะเบียนของ<b>เรา</b>มาตัดอัตรานี้ — VAT บนใบเป็นของ
    /// ผู้ขาย ตัดแล้วยอดที่ต้องจ่ายผู้ขายจะหายไปเงียบ ๆ. สิ่งที่สถานะของเราตัดสินคือ
    /// <b>เคลมได้หรือไม่</b> → <see cref="InputVatClaimable"/></para>
    /// </summary>
    public static decimal ForReceivedDocument(decimal? requestedRate, decimal companyDefaultVatRate)
        => requestedRate ?? companyDefaultVatRate;

    /// <summary>บรรทัดฝั่งซื้อนี้เคลมภาษีซื้อได้ไหม — บริษัทไม่จด VAT = ไม่ได้ทุกบรรทัด ·
    /// บรรทัดที่ไม่มี VAT ก็ไม่มีอะไรให้เคลม (ห้ามเก็บสถานะที่เป็นไปไม่ได้ลงฐาน —
    /// กติกาเดียวกับ <c>DocumentService</c>)</summary>
    public static bool InputVatClaimable(bool vatRegistered, decimal lineVatAmount)
        => vatRegistered && lineVatAmount > 0m;
}
