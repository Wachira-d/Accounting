using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ระดับความ "พิสูจน์ผู้รับเงินได้" ของกระดาษใบหนึ่ง (§65 ตรี(18))</summary>
public enum OcrPayeeProof
{
    /// <summary>กระดาษระบุตัวผู้รับเงินได้ — ใช้เป็นหลักฐานรายจ่ายได้ตามปกติ</summary>
    Identified,
    /// <summary>มีชื่อผู้รับเงิน แต่ไม่มีที่อยู่/เบอร์โทร/เลขภาษี — ก้ำกึ่ง ให้คนตัดสิน</summary>
    Weak,
    /// <summary>กระดาษไม่ได้ระบุว่าใครรับเงิน — ต้องทำใบรับรองแทนใบเสร็จประกอบ</summary>
    Unidentified,
}

/// <param name="Level">ผลตัดสิน</param>
/// <param name="Reason">เหตุผลเป็นข้อความไทยที่เอาไปโชว์ผู้ใช้ได้ตรง ๆ</param>
public readonly record struct OcrPayeeEvidenceResult(OcrPayeeProof Level, string Reason);

/// <summary>
/// **"กระดาษใบนี้พิสูจน์ได้ไหมว่าใครเป็นผู้รับเงิน" — ฟังก์ชันบริสุทธิ์ตัวเดียว**
///
/// ═══ ที่มา (ผู้ใช้รายงาน 2026-09-11) ═══
/// บิลเงินสดเขียนมือของร้านบริการ (ชื่อร้าน + ที่อยู่ + ยอด 3,500 ครบบนกระดาษ)
/// ถูกระบบเสนอให้สร้างเป็น **"ใบรับรองแทนใบเสร็จ"** แทน **"ใบสำคัญจ่าย"**
/// เพราะกติกาเดิมใน <c>OcrService</c> ตัดสินจากสัญญาณเดียว —
/// <c>string.IsNullOrWhiteSpace(VendorTaxId)</c> — แล้วสรุปว่า "ระบุตัวผู้รับเงินไม่ได้"
///
/// <para>ข้อสรุปนั้น**ไม่ตรงกฎหมาย**: §65 ตรี(18) ห้ามรายจ่ายที่ "ผู้จ่ายพิสูจน์ไม่ได้ว่า
/// ใครเป็นผู้รับ" — สิ่งที่พิสูจน์คือ **ชื่อ + ที่อยู่** ของผู้รับเงิน ไม่ใช่เลขประจำตัว
/// ผู้เสียภาษี (ร้านค้า/บุคคลธรรมดาที่ไม่จด VAT ไม่มีเลขนั้นพิมพ์บนบิลอยู่แล้ว) ·
/// "ใบรับรองแทนใบเสร็จรับเงิน" มีไว้สำหรับกรณีที่ผู้รับเงิน<b>ออกใบเสร็จให้ไม่ได้</b>
/// (แม่ค้าตลาด · วินมอเตอร์ไซค์ · แท็กซี่) — ใบที่ผู้ขาย<b>ออกบิลให้แล้ว</b>
/// ไม่ต้องออกเอกสารนี้ ให้ทำใบสำคัญจ่ายแนบบิลตามปกติ</para>
///
/// <para>ผลของการเดาผิด: ผู้ใช้ได้เอกสารผิดชนิด · ต้องกรอกเหตุผล/ผู้รับรอง/พยาน
/// ที่ไม่จำเป็น · และบรรทัดถูกตั้งเป็น "ยกเว้น VAT" ตามชนิดเอกสารโดยอัตโนมัติ</para>
///
/// กติกา: เลข 13 หลักคือ<b>ทางลัด</b>ที่พิสูจน์ได้ทันที ไม่ใช่<b>เงื่อนไขเดียว</b> —
/// ไม่มีเลขแต่มีชื่อ+ที่อยู่/เบอร์ ก็พิสูจน์ได้ · มีแต่ชื่อลอย ๆ = ก้ำกึ่ง (บอกผู้ใช้
/// ไม่ใช่เปลี่ยนชนิดเอกสารให้เงียบ ๆ) · ไม่มีอะไรเลยถึงจะเป็น "ใบรับรองแทนใบเสร็จ"
/// </summary>
public static class OcrPayeeEvidence
{
    /// <summary>ชื่อผู้รับเงินสั้นกว่านี้ถือว่าอ่านไม่ออก (เศษ OCR อย่าง "-" หรือ "ก")</summary>
    public const int MinNameLength = 3;

    /// <summary>ที่อยู่สั้นกว่านี้ไม่พอชี้ตัว (เช่น "ชลบุรี" เฉย ๆ ยังไม่ใช่ที่อยู่)</summary>
    public const int MinAddressLength = 8;

    private static readonly Regex NonDigit = new(@"[^0-9]", RegexOptions.Compiled);

    /// <param name="vendorTaxId">เลขผู้เสียภาษีผู้รับเงินที่อ่านได้ (อาจว่าง)</param>
    /// <param name="vendorName">ชื่อผู้รับเงินบนกระดาษ</param>
    /// <param name="vendorAddress">ที่อยู่ผู้รับเงินบนกระดาษ</param>
    /// <param name="vendorPhone">เบอร์โทรผู้รับเงินบนกระดาษ</param>
    public static OcrPayeeEvidenceResult Evaluate(
        string? vendorTaxId, string? vendorName, string? vendorAddress, string? vendorPhone)
    {
        // เลขผู้เสียภาษีที่ผ่าน checksum = พิสูจน์ได้ทันที (แต่ห้ามเป็นบาร์โค้ดสินค้า
        // ที่ OCR หยิบมา — ด่านเดียวกับที่ ThaiTaxId ใช้ทั้งเรพ)
        if (ThaiTaxId.IsPlausibleFromScan(vendorTaxId))
            return new(OcrPayeeProof.Identified, "มีเลขประจำตัวผู้เสียภาษี 13 หลักของผู้รับเงิน");

        var name = (vendorName ?? "").Trim();
        if (name.Length < MinNameLength)
            return new(OcrPayeeProof.Unidentified, "กระดาษไม่มีชื่อผู้รับเงินที่อ่านได้");

        var hasAddress = (vendorAddress ?? "").Trim().Length >= MinAddressLength;
        // เบอร์โทรไทย 9–10 หลัก (02-xxx-xxxx / 08x-xxx-xxxx)
        var hasPhone = NonDigit.Replace(vendorPhone ?? "", "").Length >= 9;

        if (hasAddress)
            return new(OcrPayeeProof.Identified, "กระดาษมีชื่อ + ที่อยู่ผู้รับเงิน");
        if (hasPhone)
            return new(OcrPayeeProof.Identified, "กระดาษมีชื่อ + เบอร์โทรผู้รับเงิน");

        return new(OcrPayeeProof.Weak, "มีชื่อผู้รับเงินแต่ไม่มีที่อยู่/เบอร์โทร/เลขผู้เสียภาษี");
    }
}
