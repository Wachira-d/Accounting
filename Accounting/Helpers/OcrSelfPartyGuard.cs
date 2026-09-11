namespace Accounting.Helpers;

/// <summary>ฝั่งที่<b>กระดาษ</b>วางบริษัทของเราไว้</summary>
public enum OcrSelfSide
{
    /// <summary>กระดาษไม่ได้บอก — ห้ามเดา</summary>
    Unknown = 0,
    /// <summary>ชื่อเราอยู่ใต้ป้ายฝั่งผู้ซื้อ (นาม/ลูกค้า/Bill To) ⇒ เราเป็นผู้จ่าย</summary>
    Buyer = 1,
    /// <summary>ชื่อเราอยู่ใต้ป้ายฝั่งผู้ขาย ⇒ เราเป็นผู้รับเงิน</summary>
    Seller = 2,
}

/// <param name="Side">ฝั่งที่กระดาษบอก</param>
/// <param name="Reason">เหตุผลภาษาไทยที่เอาไปโชว์/ลง ReasoningTrace ได้</param>
/// <param name="NameLinePos">ตำแหน่งบรรทัดที่พบชื่อเรา (-1 = ไม่พบ)</param>
public sealed record OcrSelfPartyVerdict(OcrSelfSide Side, string Reason, int NameLinePos);

/// <summary>
/// **“ชื่อเราโผล่ในช่องคู่ค้า” ไม่ได้แปลว่าเราเป็นคู่ค้าฝั่งนั้น**
///
/// <para>═══ ที่มา (สแกนจริง 2026-09-11 · บิลเงินสดเขียนมือ 3,500 บาท) ═══
/// กระดาษเป็นบิลเงินสดที่<b>ร้านออกให้เรา</b> (เราจ่ายเงิน) — กรอบบนคือร้าน
/// ช่อง “นาม/NAME” คือเรา. Azure DI หยิบชื่อในช่อง “นาม” (= <b>ลูกค้า</b>) ไปใส่
/// <c>VendorName</c> ⇒ <c>OcrDocumentRoleInferrer</c> เห็นว่า “ชื่อผู้ขายใกล้เคียง
/// บริษัทเรา” จึงสรุปว่า <b>เราเป็นผู้ขาย</b> (conf 0.75) แล้วเสนอให้สร้าง
/// <b>ใบแจ้งหนี้ขาย</b> ⇒ เงินที่<b>จ่ายออก</b> 3,500 กำลังจะถูกบันทึกเป็น<b>รายได้</b>
/// · ซ้ำร้าย ระบบจับคู่ Contact ได้เป็น<b>ตัวบริษัทเราเอง</b></para>
///
/// <para>═══ ข้อผิดพลาดเชิงตรรกะที่แก้ ═══ เมื่อชื่อเราโผล่ในช่องคู่ค้า มีสอง
/// สมมติฐานเสมอ: (ก) เราเป็นคู่ค้าฝั่งนั้นจริง (ข) <b>engine ใส่ผิดช่อง</b> —
/// โค้ดเดิมพิจารณาแค่ (ก). ตัวตัดสินที่ถูกคือ<b>ป้ายบนกระดาษ</b> ไม่ใช่ “ช่องที่
/// engine เลือกใส่” เพราะช่องนั้นคือสิ่งที่กำลังถูกสงสัยอยู่
/// (กติกาเดิมของเรพ: “ป้ายกำกับเป็นตัวตัดสินหลัก” — เคสบาร์โค้ด EAN-13)</para>
///
/// <para>═══ ต้องมองสองทาง ═══ แบบฟอร์มพิมพ์สำเร็จวาง<b>ป้ายไว้ใต้/หลังค่า</b>ได้
/// (ลำดับที่ Azure คืนมาคือ “บจก. แอมแฮปปี้เนส …” แล้วค่อย “นาม”) — บทเรียนเดิม
/// ของเรพ (“ป้ายกำกับอยู่ก่อนค่าเสมอ เป็นสมมติฐานที่ผิด”) ⇒ วัดระยะ<b>สองทิศ</b>
/// แต่ต้อง<b>ใกล้จริง</b> ({<see cref="MaxLabelDistance"/>} ตัวอักษร) ไม่งั้นป้าย
/// ที่อยู่คนละบล็อกจะติดธงโดยบังเอิญ</para>
/// </summary>
public static class OcrSelfPartyGuard
{
    /// <summary>ระยะสูงสุดระหว่างบรรทัดชื่อกับป้าย ที่ยังถือว่า “ป้ายนี้กำกับชื่อนี้”
    /// — บล็อกข้อมูลคู่ค้าบนกระดาษไทยกว้างไม่เกินนี้ (ชื่อ+ที่อยู่+เลขภาษี)</summary>
    public const int MaxLabelDistance = 90;

    /// <summary>ชื่อคู่ค้าที่อ่านได้ = บริษัทของเราเองหรือเปล่า (fuzzy — ตัดคำนำหน้า
    /// นิติบุคคลออกก่อนเทียบ) · ตัวเดียวของระบบ ใช้ทั้ง SmartFieldExtractor และ
    /// OcrDocumentRoleInferrer</summary>
    public static bool IsSelf(string? partyName, string? companyName)
    {
        if (!NameOverlaps(partyName, companyName)) return false;
        // ชื่อบนกระดาษ **ยาวกว่า** ชื่อเรามาก = บริษัทในเครือ/ชื่อที่มีคำเราเป็นส่วนหนึ่ง
        // ("แอม แฮปปี้เนส เทรดดิ้ง" · "สยามแม็คโคร" กับ tenant "สยาม") ไม่ใช่เรา —
        // ทิศตรงข้าม (กระดาษสั้นกว่า = engine ตัดชื่อเรา "แอม แฮปปี้") ยังยอมเหมือนเดิม
        var paper = Normalize(partyName);
        var ours = Normalize(companyName);
        return paper.Length - ours.Length <= MaxSurplus;
    }

    /// <summary>ตัวอักษร (หลัง normalize) ที่ชื่อบนกระดาษยาวเกินชื่อเราได้ โดยยังถือว่าเป็นเรา
    /// — พอสำหรับเศษอย่าง "(สนญ.)"/"สาขา 1" ไม่พอสำหรับคำต่อท้ายที่เป็นชื่อบริษัทอื่น</summary>
    public const int MaxSurplus = 6;

    /// <summary>เทียบชื่อสองตัวแบบหลวม: ตัดคำนำหน้า/ต่อท้ายนิติบุคคลและช่องว่างออก
    /// แล้วดูว่าตัวหนึ่ง<b>ครอบ</b>อีกตัวไหม (≥ 4 ตัวอักษรจึงจะนับเป็นหลักฐาน)</summary>
    public static bool NameOverlaps(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length < 4 || nb.Length < 4) return false;
        return na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal);
    }

    /// <summary>ตัดคำนำหน้า/ต่อท้ายนิติบุคคล + ช่องว่าง + เครื่องหมายวรรคตอน
    /// ("บจก." เคยตกหล่นจนชื่อเดียวกันสองรูปเทียบไม่ติด)</summary>
    public static string Normalize(string? s)
    {
        var lowered = (s ?? string.Empty).ToLowerInvariant();
        foreach (var w in new[]
        {
            "ห้างหุ้นส่วนจำกัด", "ห้างหุ้นส่วนสามัญ", "บริษัทมหาชนจำกัด", "บริษัท",
            "หจก.", "หจก", "บจก.", "บจก", "บมจ.", "บมจ", "จำกัด", "(มหาชน)", "มหาชน",
            "สำนักงานใหญ่", "head office",
            "co., ltd.", "co.,ltd.", "co. ltd.", "ltd.", "ltd", "company", "part.",
        })
            lowered = lowered.Replace(w, "", StringComparison.Ordinal);
        return new string(lowered
            .Where(c => !char.IsWhiteSpace(c) && c != '.' && c != ',' && c != '(' && c != ')')
            .ToArray()).Trim();
    }

    /// <summary>กระดาษวางชื่อบริษัทเราไว้ฝั่งไหน — ตัดสินจาก<b>ป้ายที่ใกล้ที่สุด</b>
    /// รอบบรรทัดที่มีชื่อเรา (มองทั้งก่อนและหลัง)
    ///
    /// <para>คืน <see cref="OcrSelfSide.Unknown"/> เมื่อหาชื่อเราไม่เจอ / ไม่มีป้าย
    /// ใกล้พอ / ป้ายสองฝั่งใกล้เท่ากัน — “ไม่รู้ = บอกว่าไม่รู้” ห้ามเดา เพราะ
    /// เดาผิดทิศเดียว = รายจ่ายกลายเป็นรายได้</para></summary>
    public static OcrSelfPartyVerdict FromPaperLabels(string? rawText, string? companyName, string? ourTaxId = null)
    {
        if (string.IsNullOrWhiteSpace(rawText) || string.IsNullOrWhiteSpace(companyName))
            return new(OcrSelfSide.Unknown, "", -1);

        var pos = FindOurNameLine(rawText!, companyName!);
        // อ่านชื่อเราไม่ออกเลย แต่กระดาษมีเลขภาษีเราตรง/เพี้ยนหลักเดียว → ใช้ตำแหน่งเลขเป็นจุดยึด
        // (บล็อกคู่สัญญาบนกระดาษไทยพิมพ์ชื่อกับเลขภาษีติดกันเสมอ)
        if (pos < 0) pos = FindOurTaxIdPosition(rawText!, ourTaxId);
        if (pos < 0) return new(OcrSelfSide.Unknown, "", -1);

        var (buyerAll, sellerAll) = OcrPartyLabels.FindAll(rawText);
        // ป้ายที่ **ใกล้ที่สุด** ของแต่ละฝั่ง — ไม่ใช่ตัวแรกของหน้า
        var buyerPos = Nearest(buyerAll, pos);
        var sellerPos = Nearest(sellerAll, pos);
        var dBuyer = buyerPos >= 0 ? Math.Abs(buyerPos - pos) : int.MaxValue;
        var dSeller = sellerPos >= 0 ? Math.Abs(sellerPos - pos) : int.MaxValue;
        if (dBuyer > MaxLabelDistance && dSeller > MaxLabelDistance)
            return new(OcrSelfSide.Unknown, "", pos);
        if (dBuyer == dSeller) return new(OcrSelfSide.Unknown, "", pos);
        // "ผู้ซื้อ … ผู้ขาย" บน**บรรทัดเดียวกัน** = หัวตารางสองคอลัมน์ — ตำแหน่งตัวอักษรใน
        // ข้อความเรียงบรรทัดบอกไม่ได้ว่าชื่อเราอยู่คอลัมน์ไหน ⇒ ไม่ตัดสิน (ทีมตรวจ 2026-09-11)
        if (buyerPos >= 0 && sellerPos >= 0 && SameLine(rawText!, buyerPos, sellerPos))
            return new(OcrSelfSide.Unknown, "ป้ายผู้ซื้อ/ผู้ขายอยู่บรรทัดเดียวกัน (หัวตาราง) — ตำแหน่งบอกคอลัมน์ไม่ได้", pos);

        return dBuyer < dSeller
            ? new(OcrSelfSide.Buyer,
                "ชื่อบริษัทเราอยู่ติดป้ายฝั่งผู้ซื้อบนกระดาษ (นาม/ลูกค้า/Bill To) → เราเป็นผู้ซื้อ",
                pos)
            : new(OcrSelfSide.Seller,
                "ชื่อบริษัทเราอยู่ติดป้ายฝั่งผู้ขายบนกระดาษ (ผู้ขาย/ผู้ออกใบ) → เราเป็นผู้ขาย",
                pos);
    }

    /// <summary>ตำแหน่งบรรทัดแรกของ rawText ที่ชื่อบริษัทเราปรากฏ
    ///
    /// <para>ต้องค้นจาก <b>rawText</b> ไม่ใช่จากค่าที่ engine คืนมา — ค่าที่ engine
    /// คืนอาจถูก <c>VendorKnownGoodCorrector</c> แทนด้วยรูปมาตรฐานของเราไปแล้ว
    /// (“บจก. แอมแฮปปี้เนส (สำนักงานใหญ่)” บนกระดาษ → “หจก. แอม แฮปปี้เนส สำนักงานใหญ่”)
    /// ⇒ <c>IndexOf</c> ด้วยสตริงนั้นจะหาไม่เจอทั้งที่ชื่อเราอยู่บนกระดาษชัด ๆ</para></summary>
    private static int Nearest(IReadOnlyList<int> positions, int anchor)
    {
        var best = -1; var bestD = int.MaxValue;
        foreach (var p in positions)
        {
            var d = Math.Abs(p - anchor);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    private static bool SameLine(string text, int a, int b)
    {
        var lo = Math.Min(a, b); var hi = Math.Max(a, b);
        return text.IndexOf('\n', lo, hi - lo) < 0;
    }

    /// <summary>ตำแหน่งของเลขภาษีเรา (ตรง หรือต่างหลักเดียว) บนกระดาษ — -1 เมื่อไม่พบ</summary>
    private static int FindOurTaxIdPosition(string rawText, string? ourTaxId)
    {
        var ours = ThaiTaxId.Normalize(ourTaxId);
        if (ours.Length != 13) return -1;
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(rawText, @"(?<!\d)\d(?:[- \t]?\d){12}(?!\d)"))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            var diff = 0;
            for (var i = 0; i < 13 && diff <= 1; i++) if (digits[i] != ours[i]) diff++;
            if (diff <= 1) return m.Index;
        }
        return -1;
    }

    private static int FindOurNameLine(string rawText, string companyName)
    {
        var offset = 0;
        foreach (var line in rawText.Split('\n'))
        {
            if (NameOverlaps(line, companyName)) return offset;
            offset += line.Length + 1;
        }
        return -1;
    }
}
