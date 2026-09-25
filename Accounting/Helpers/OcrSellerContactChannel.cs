using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **อีเมล/เบอร์โทรบนกระดาษ — ของผู้ขาย หรือของผู้ซื้อ** (รอบ 197 ทีม K · ใบ Makro 3/3)
///
/// <para>═══ ที่มา (ผู้ใช้รายงาน 2026-09-25) ═══ ผู้ติดต่อ "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)" มีอีเมล
/// <c>taketime.bangphra@gmail.com</c> — อีเมลนั้นพิมพ์อยู่ในบล็อก "สถานที่ส่งสินค้า / ชื่อผู้รับสินค้า / อีเมล์" ของ<b>ผู้ซื้อ</b> (เรา)
/// เดิมตัวอ่านใน <c>OcrService</c> ยิง regex อีเมลทั้งหน้าแล้วหยิบ<b>ตัวแรก</b>เป็น <c>VendorEmail</c> ⇒ สร้าง/เติมผู้ติดต่อผู้ขาย
/// ด้วยอีเมลผู้ซื้อ + ตัวเรียนรู้ Azure จำเป็นค่าที่ถูกของผู้ขายรายนั้น (ถาวร)</para>
///
/// <para>═══ กติกา ═══ ช่องติดต่อที่<b>ป้ายฝั่งผู้ซื้อ/ผู้รับ</b>อยู่เหนือใกล้กว่าป้ายฝั่งผู้ขาย = ของผู้ซื้อ — ห้ามเป็นของผู้ขาย
/// (ตำแหน่งแบบเดียวกับ <see cref="OcrVendorKeyEvidence"/>) และห่างป้ายนั้นไม่เกิน <see cref="MaxBlockLines"/> บรรทัด
/// (บล็อกผู้ซื้อ/ที่อยู่จัดส่งเป็นกล่องสั้น — อีเมลผู้ขายที่ท้ายกระดาษใต้ตารางสินค้าไม่ถูกตัดทิ้ง) · กระดาษที่<b>ไม่มีป้ายฝั่งใดเลย</b> = ไม่มีหลักฐานเชิงตำแหน่ง ⇒
/// พฤติกรรมเดิม (ตัวแรกของหน้า — ใบเสร็จร้านเล็กที่มีแต่หัวร้าน) · อีเมล/เบอร์ของบริษัทเรา (ถ้ารู้) ไม่ใช่ของผู้ขาย</para>
/// </summary>
public static class OcrSellerContactChannel
{
    /// <summary>ช่องติดต่อที่อยู่ใต้ป้ายผู้ซื้อ/ผู้รับเกินกี่บรรทัดถือว่าพ้นบล็อกนั้นแล้ว (เผื่อข้อความสองคอลัมน์ที่ engine สลับบรรทัดกัน)</summary>
    public const int MaxBlockLines = 10;

    /// <summary>รูปอีเมล — ตัวเดียวของเส้น OCR (เดิมเป็น <c>VendorEmailRegex</c> ใน OcrService)</summary>
    public static readonly Regex EmailRx = new(@"[\w\.\-]+@[\w\.\-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled);

    /// <summary>อีเมลของผู้ขาย: ตัวแรกที่ไม่อยู่ในบล็อกผู้ซื้อ/ผู้รับ และไม่ใช่อีเมลเรา — null = ไม่มี</summary>
    public static string? SellerEmail(string? text, string? ourEmail = null)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var (buyer, seller) = Positions(text);
        foreach (Match m in EmailRx.Matches(text))
        {
            var email = Clean(m.Value);
            if (email.Length == 0) continue;
            if (!string.IsNullOrWhiteSpace(ourEmail) && string.Equals(email, ourEmail.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (InBuyerSide(text, m.Index, buyer, seller)) continue;
            return email;
        }
        return null;
    }

    /// <summary>อีเมลทุกตัวที่พิมพ์อยู่<b>ฝั่งผู้ซื้อ/ผู้รับ</b> — ใช้เตือนเมื่ออีเมลที่เก็บไว้ในผู้ติดต่อผู้ขายตรงกับตัวใดตัวหนึ่ง
    /// (ข้อมูลที่ปนไปแล้วก่อนแก้ — แยกไม่ได้ว่าอันไหนผู้ใช้กรอกเอง จึง "เตือน" ไม่ "ลบ")</summary>
    public static IReadOnlyList<string> BuyerSideEmails(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var (buyer, seller) = Positions(text);
        return EmailRx.Matches(text).Cast<Match>()
            .Where(m => InBuyerSide(text, m.Index, buyer, seller))
            .Select(m => Clean(m.Value))
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>ตำแหน่งนี้อยู่ฝั่งผู้ซื้อ/ผู้รับไหม (ใช้กับเบอร์โทร — ผู้เรียกวนทุก match ของ regex เบอร์แล้วข้ามตัวที่เป็นจริง)</summary>
    public static bool IsBuyerSide(string? text, int position)
    {
        if (string.IsNullOrEmpty(text) || position < 0) return false;
        var (buyer, seller) = Positions(text);
        return InBuyerSide(text, position, buyer, seller);
    }

    private static (List<int> Buyer, IReadOnlyList<int> Seller) Positions(string text)
    {
        var all = OcrPartyLabels.FindAll(text);
        var buyer = all.BuyerPos.Concat(OcrPartyLabels.FindRecipientAll(text)).ToList();
        return (buyer, all.SellerPos);
    }

    private static bool InBuyerSide(string text, int position, IReadOnlyList<int> buyer, IReadOnlyList<int> seller)
    {
        var b = NearestAbove(position, buyer);
        if (b < 0 || b <= NearestAbove(position, seller)) return false;
        var lines = 0;
        for (var i = b; i < position && i < text.Length; i++)
            if (text[i] == '\n' && ++lines > MaxBlockLines) return false;
        return true;
    }

    private static int NearestAbove(int position, IReadOnlyList<int> positions)
    {
        var best = -1;
        foreach (var p in positions)
            if (p < position && p > best) best = p;
        return best;
    }

    private static string Clean(string raw) => raw.Trim().TrimEnd('.', ',', ';', ':');
}
