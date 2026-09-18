namespace Accounting.Helpers;

/// <summary>หลักฐานว่า "เลขที่เราเอาไปค้นทะเบียน คือเลขของ<b>ผู้ขาย</b>บนกระดาษใบนี้จริง"</summary>
public enum VendorKeyEvidence
{
    /// <summary>มีแค่รูปแบบที่ถูก — ยังพิสูจน์ไม่ได้ว่าเลขนี้เป็นของผู้ขาย
    /// ⇒ ทะเบียนชนะได้เฉพาะตอนชื่อ<b>คล้าย</b>ของเดิม (ด่าน 0.45 เดิม)</summary>
    Unproven = 0,

    /// <summary>พิสูจน์แล้วว่าเลขนี้คือเลขผู้เสียภาษีของผู้ขายบนกระดาษ
    /// ⇒ ทะเบียนชนะได้<b>แม้ชื่อไม่คล้ายเลย</b> (ชื่อบนกระดาษเป็นแบรนด์/โลโก้)</summary>
    ProvenSellerKey = 1,
}

/// <summary>
/// **กุญแจถูกไหม — ตัวตัดสินตัวเดียวของเส้น OCR** (ฟังก์ชันบริสุทธิ์ ทดสอบได้)
///
/// <para>═══ ที่มา (ผู้ใช้รายงาน 2026-09-18) ═══ สแกนใบกำกับของดีแคทลอน แล้ว
/// ระบบตั้งชื่อคู่ค้าเป็น <c>"DECATHLON"</c> — คำบน<b>โลโก้</b> ไม่ใช่ชื่อนิติบุคคล
/// ที่ใช้ออกใบกำกับได้ตาม §86/4. ระบบ<b>ค้นทะเบียนถูกแล้ว</b>และได้ชื่อทางการมา
/// แต่โยนทิ้ง เพราะด่านเดิมถามว่า <i>"ชื่อสองชื่อคล้ายกันไหม"</i> — ซึ่งเป็น
/// <b>คำถามผิด</b>: "DECATHLON" กับ "บริษัท ดีแคทลอน (ประเทศไทย) จำกัด" อยู่คนละ
/// ตัวอักษร (ละติน/ไทย) คะแนนความคล้าย = <b>0.000</b> เท่ากับ "คนละบริษัท"
/// ⇒ ตกด่าน ⇒ ชื่อโลโก้รอดไปเป็นชื่อคู่ค้าถาวร</para>
///
/// <para>═══ คำถามที่ถูก ═══ ทะเบียนราชการเชื่อถือได้เสมอ<b>ถ้ากุญแจที่ใช้ค้นถูก</b>
/// ⇒ ให้ไปพิสูจน์ <b>กุญแจ</b> แทนที่จะเอาชื่อสองชื่อมาเทียบกัน:
/// <list type="number">
/// <item>เลขผ่าน checksum และ<b>ไม่ใช่บาร์โค้ดสินค้า</b> (<see cref="ThaiTaxId.IsPlausibleFromScan"/>)</item>
/// <item>บนกระดาษมี<b>ป้าย "เลขประจำตัวผู้เสียภาษี"</b> กำกับเลขก้อนนี้จริง</item>
/// <item><b>ไม่ใช่เลขของผู้ซื้อ</b>ในใบเดียวกัน และ<b>ไม่ใช่เลขบริษัทเรา</b>
///   — สองเคสนี้คือทางที่เลข "ถูกต้องแต่ผิดฝั่ง" เล็ดลอดเข้ามาเป็นเลขผู้ขาย</item>
/// <item>ตำแหน่งของเลขไม่ได้อยู่ใน<b>บล็อกผู้ซื้อ</b> — วัดจาก "ป้ายฝั่งไหนอยู่
///   เหนือเลขนี้ใกล้ที่สุด" (<see cref="OcrPartyLabels.FindAll"/>)</item>
/// </list></para>
///
/// <para>⚠️ ข้อ 3–4 ถอดไม่ได้ ไม่ใช่ของแถม: ถ้าเหลือแค่ "มีป้าย" เลขของ<b>ผู้ซื้อ</b>
/// ก็มีป้ายเหมือนกันทุกใบ ⇒ ทะเบียนจะคืน "บริษัทผู้ซื้อ" แล้วเราจะเอาไปทับชื่อ
/// ผู้ขายด้วยความมั่นใจเต็มร้อย ซึ่งเลวร้ายกว่าเดิม (นี่คือเคสที่ด่าน 0.45 เดิม
/// ถูกสร้างมากัน — ห้ามเปิดรูแทนที่จะปิด)</para>
///
/// <para>ที่นี่<b>ไม่</b>คำนวณ "มีป้ายไหม"/"ป้ายอยู่ที่ไหน" เอง — ผู้เรียกส่งผลจาก
/// <c>SmartFieldExtractor.ExtractTaxIdCandidates</c> และ <see cref="OcrPartyLabels"/>
/// เข้ามา เพื่อให้ตัวนี้เป็นฟังก์ชันบริสุทธิ์ และเพื่อไม่ให้รายการป้ายมีสำเนาที่สอง</para>
/// </summary>
public static class OcrVendorKeyEvidence
{
    /// <summary>ตัดสินว่ากุญแจ (เลขผู้เสียภาษีผู้ขาย) พิสูจน์ได้ไหม</summary>
    /// <param name="vendorTaxId">เลขที่จะเอาไปค้นทะเบียน</param>
    /// <param name="buyerTaxId">เลขผู้ซื้อในใบเดียวกัน (ถ้าอ่านได้)</param>
    /// <param name="ourTaxId">เลขบริษัทเจ้าของ tenant</param>
    /// <param name="labelledAsTaxIdOnPaper">กระดาษมีป้าย "เลขประจำตัวผู้เสียภาษี" กำกับเลขก้อนนี้</param>
    /// <param name="taxIdPosition">ตำแหน่งเลขก้อนนี้ในข้อความทั้งหน้า (-1 = ไม่พบ/ไม่รู้)</param>
    /// <param name="buyerLabelPositions">ตำแหน่งป้ายฝั่งผู้ซื้อทุกตัว</param>
    /// <param name="sellerLabelPositions">ตำแหน่งป้ายฝั่งผู้ขายทุกตัว</param>
    /// <param name="textLength">ความยาวข้อความทั้งหน้า — ใช้เฉพาะตอน<b>ไม่มีป้ายฝั่งใดเลย</b>
    /// (0 = ไม่รู้)</param>
    public static VendorKeyEvidence Judge(
        string? vendorTaxId,
        string? buyerTaxId,
        string? ourTaxId,
        bool labelledAsTaxIdOnPaper,
        int taxIdPosition,
        IReadOnlyList<int>? buyerLabelPositions,
        IReadOnlyList<int>? sellerLabelPositions,
        int textLength = 0)
    {
        // 1) รูปแบบ + checksum + ไม่ใช่บาร์โค้ดสินค้า
        if (!ThaiTaxId.IsPlausibleFromScan(vendorTaxId)) return VendorKeyEvidence.Unproven;

        // 2) ต้องมีป้ายกำกับบนกระดาษ — "เลข 13 หลักลอย ๆ" ไม่ใช่หลักฐาน
        if (!labelledAsTaxIdOnPaper) return VendorKeyEvidence.Unproven;

        // 3) เลขที่ถูกต้องแต่ผิดฝั่ง: เลขผู้ซื้อ / เลขบริษัทเราเอง
        if (ThaiTaxId.Same(vendorTaxId, buyerTaxId)) return VendorKeyEvidence.Unproven;
        if (ThaiTaxId.Same(vendorTaxId, ourTaxId)) return VendorKeyEvidence.Unproven;

        // 4) ตำแหน่ง: ป้ายฝั่งไหน "อยู่เหนือเลขนี้ใกล้ที่สุด"
        if (InBuyerBlock(taxIdPosition, buyerLabelPositions, sellerLabelPositions))
            return VendorKeyEvidence.Unproven;

        // 5) กระดาษที่<b>ไม่มีป้ายฝั่งใดเลยทั้งหน้า</b> = ไม่มีหลักฐานเชิงตำแหน่ง
        //
        // เดิมเคสนี้ตกลงมาเป็น "พิสูจน์แล้ว" เพราะ InBuyerBlock คืน false เมื่อไม่มี
        // ป้ายอะไรเลย (ฝ่ายค้านรอบ 174 จับได้) — ซึ่งแปลง "ไม่รู้" เป็น "ใช่" ตรง ๆ
        // ขัดหลักการข้อ 3 ของเรพ. ป้าย "เลขประจำตัวผู้เสียภาษี" ในเงื่อนไข 2
        // **ไม่แยกฝั่ง** (ผู้ซื้อก็มีป้ายนี้) ⇒ ถ้าไม่มีป้ายฝั่งเลย ด่านที่เหลือจริง
        // มีแค่ "ไม่ใช่เลขผู้ซื้อ/เลขเรา" ซึ่งหายไปเองเมื่อ tenant ยังไม่กรอกเลขบริษัท
        //
        // ใช้แบบแผนหน้ากระดาษแทน: บล็อกผู้ขายของใบไทยอยู่**หัวใบ**เสมอ (หมายเหตุใน
        // OcrPartyLabels) ⇒ ยอมรับเฉพาะเลขที่อยู่ในครึ่งบน. ไม่รู้ความยาวหน้า
        // (textLength = 0) ⇒ ไม่มีหลักฐาน ⇒ Unproven
        var noPartyLabels = (buyerLabelPositions?.Count ?? 0) == 0
                         && (sellerLabelPositions?.Count ?? 0) == 0;
        if (noPartyLabels && !InTopHalf(taxIdPosition, textLength))
            return VendorKeyEvidence.Unproven;

        return VendorKeyEvidence.ProvenSellerKey;
    }

    /// <summary>เลขอยู่ในครึ่งบนของข้อความทั้งหน้าไหม (ไม่รู้ตำแหน่ง/ความยาว = ไม่ใช่)</summary>
    private static bool InTopHalf(int position, int textLength)
        => position >= 0 && textLength > 0 && position * 2 <= textLength;

    /// <summary>เลขก้อนนี้อยู่ใต้ป้ายฝั่ง<b>ผู้ซื้อ</b>หรือไม่
    ///
    /// <para>ตั้งใจ<b>ไม่</b>บังคับว่า "ต้องมีป้ายผู้ขาย" — ใบไทยส่วนใหญ่วางบล็อก
    /// ผู้ขายไว้หัวกระดาษโดย<b>ไม่มีป้ายอะไรเลย</b> (ดูหมายเหตุใน
    /// <see cref="OcrPartyLabels"/>) ถ้าบังคับ เงื่อนไขนี้จะเป็นเท็จเกือบทุกใบ
    /// = ปิดด่านโดยไม่ตั้งใจในทิศตรงข้าม</para></summary>
    private static bool InBuyerBlock(int taxIdPosition,
        IReadOnlyList<int>? buyerLabelPositions, IReadOnlyList<int>? sellerLabelPositions)
    {
        if (taxIdPosition < 0) return false;   // ไม่รู้ตำแหน่ง = ไม่มีหลักฐานว่าอยู่ฝั่งผู้ซื้อ
        var buyer = NearestAbove(taxIdPosition, buyerLabelPositions);
        var seller = NearestAbove(taxIdPosition, sellerLabelPositions);
        return buyer > seller;
    }

    /// <summary>ตำแหน่งป้ายที่อยู่<b>ก่อน</b>จุดนี้และใกล้ที่สุด (-1 = ไม่มี)</summary>
    private static int NearestAbove(int position, IReadOnlyList<int>? positions)
    {
        var best = -1;
        if (positions == null) return best;
        foreach (var p in positions)
            if (p < position && p > best) best = p;
        return best;
    }
}
