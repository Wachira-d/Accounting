using System.Text.RegularExpressions;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสิน "แถวผู้ติดต่อที่จับได้ด้วยอีเมล/เบอร์ ใช้เป็นผู้ซื้อของแขกคนนี้ได้ไหม" — โมดูลที่พัก (รอบ 193 · ฝ่ายค้าน C-7)
///
/// <para>═══ ที่มา ═══ แขกนิติบุคคลที่ส่งเลขภาษี<b>ใหม่</b> (ยังไม่มีในทะเบียน) ⇒ <c>ContactTaxBranchKey.SoftMatchScope</c> คืน
/// <c>RowsWithoutTaxId</c> ⇒ เดิมหยิบแถวแรกที่อีเมล/เบอร์ตรงและไม่มีเลขภาษี — ซึ่งมักเป็น<b>แถวบุคคลธรรมดา</b> ของผู้ติดต่อคนนั้น
/// (หรือคนอื่นที่ใช้อีเมลบริษัทร่วมกัน) แล้วไม่เติมเลขภาษี/ชื่อบริษัท ⇒ ใบกำกับออกในชื่อบุคคลโดยไม่มีเลขผู้ซื้อ (§86/4)</para>
///
/// <para>═══ กติกา ═══ แขกที่ <b>ไม่ได้</b> ส่งเลขภาษี/ชื่อบริษัท → ใช้แถวที่จับได้ตามเดิม ·
/// แขกที่ส่งมา → ใช้ได้เฉพาะแถวที่<b>ไม่ใช่บุคคลธรรมดา</b> และ<b>ชื่อตรงกับชื่อบริษัทที่แขกกรอก</b> (หลังตัดช่องว่าง/ตัวพิมพ์ —
/// ไม่ fuzzy: ชื่อคล้ายกันของคนละนิติบุคคลต้องไม่ถูกรวม) · ไม่ผ่าน = สร้างแถวใหม่ · ขอบเขตแถวที่ค้นยังตัดสินโดย
/// <c>ContactTaxBranchKey.SoftMatchScope</c> ตัวเดิม (ตัวนี้กรองซ้อนเฉพาะฝั่งผู้เรียกที่พัก)</para>
/// </summary>
public static class LodgingGuestContact
{
    /// <summary>แขกระบุตัวตนทางภาษี (เลขภาษีหรือชื่อบริษัท) หรือไม่</summary>
    private static bool HasTaxIdentity(string? guestTaxId, string? guestCompanyName)
        => !string.IsNullOrWhiteSpace(guestTaxId) || !string.IsNullOrWhiteSpace(guestCompanyName);

    /// <summary>แถวที่จับได้ด้วยอีเมล/เบอร์ ใช้เป็นผู้ติดต่อของแขกคนนี้ได้ไหม</summary>
    public static bool SoftCandidateAcceptable(
        string? guestTaxId, string? guestCompanyName, string? candidateName, ContactType candidateType)
    {
        if (!HasTaxIdentity(guestTaxId, guestCompanyName)) return true;           // แขกบุคคลทั่วไป — พฤติกรรมเดิม
        if (candidateType == ContactType.Individual) return false;               // ห้ามใช้แถวบุคคลธรรมดาเป็นผู้ซื้อนิติบุคคล
        if (string.IsNullOrWhiteSpace(guestCompanyName)) return false;           // มีเลขแต่ไม่มีชื่อบริษัท — ยืนยันตัวไม่ได้ สร้างใหม่
        return SameName(candidateName, guestCompanyName);
    }

    /// <summary>ชื่อเดียวกัน (ตัดช่องว่างซ้ำ/หัวท้าย · ไม่สนตัวพิมพ์) — ไม่ fuzzy</summary>
    private static bool SameName(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
           && string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    private static string Norm(string s) => Regex.Replace(s.Trim(), @"\s+", " ");
}
