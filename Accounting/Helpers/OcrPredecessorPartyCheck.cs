namespace Accounting.Helpers;

/// <summary>ผลของ <see cref="OcrPredecessorPartyCheck.Judge"/> — <c>Block</c> = ห้ามผูก (ข้อความไทยบอกทางไปต่อ) · <c>Note</c> = ผูกได้แต่ต้องเตือน · ทั้งคู่ null = ผ่าน</summary>
public sealed record OcrPredecessorPartyVerdict(string? Block, string? Note);

/// <summary>
/// **ผู้ใช้ผูกใบต้นทางให้สแกนที่ระบบยังไม่รู้คู่ค้า — ใบนั้นเป็นของคู่ค้าบนกระดาษไหม** (รอบ 201 ทีม OC · ฝ่ายค้าน OCX-10) — pure
///
/// <para>เส้นผูกใบต้นทางด้วยมือ (<c>LinkPredecessorAsync</c>) ตรวจ "คู่ค้าเดียวกัน" ได้เฉพาะเมื่อรู้ผู้ติดต่อของสแกนแล้ว · ยังไม่รู้ (ผู้ขายยังไม่จับคู่ /
/// ผู้ซื้อยังไม่มีในระบบ) = เดิมผ่านเงียบ ⇒ ใบรับ/จ่ายเงินที่สร้างตามมาสืบทอดผู้ติดต่อของใบต้นทาง (C-20) ของนิติบุคคลอื่นได้</para>
///
/// <para>กติกา (หลักฐานจากแรงไปอ่อน): เลขผู้เสียภาษี 13 หลักทั้งสองฝั่งและ<b>ต่างกัน</b> ⇒ บล็อก (คนละนิติบุคคล) · เลขตรงกัน ⇒ ผ่าน ·
/// ไม่มีเลขให้เทียบ ⇒ ดูชื่อแกน (<see cref="ContactTaxBranchKey.NameCore"/>): เท่ากัน/ฝั่งหนึ่งครอบอีกฝั่ง (≥ 4 ตัวอักษร — ชื่อที่ถูกตัด) ⇒ ผ่าน ·
/// ชื่อไม่ตรง/ไม่มีชื่อ ⇒ ผูกได้ + โน้ตเตือน (ผู้ใช้เห็นทั้งกระดาษและใบต้นทาง — ไม่บล็อกด้วยหลักฐานอ่อน)</para>
/// </summary>
public static class OcrPredecessorPartyCheck
{
    private const int MinCoreForContainment = 4;

    public static OcrPredecessorPartyVerdict Judge(string? scanPartyName, string? scanPartyTaxId,
        string? predecessorContactName, string? predecessorContactTaxId, string? predecessorNumber)
    {
        if (ThaiTaxId.IsWellFormed(scanPartyTaxId) && ThaiTaxId.IsWellFormed(predecessorContactTaxId))
        {
            if (ThaiTaxId.Normalize(scanPartyTaxId) == ThaiTaxId.Normalize(predecessorContactTaxId)) return new(null, null);
            return new($"ใบต้นทาง {predecessorNumber} เป็นของผู้ติดต่อเลขผู้เสียภาษี {ThaiTaxId.Normalize(predecessorContactTaxId)} "
                + $"แต่กระดาษพิมพ์เลข {ThaiTaxId.Normalize(scanPartyTaxId)} — คนละนิติบุคคล ผูกไม่ได้ · "
                + "ถ้าเลขบนกระดาษอ่านผิด ให้แก้เลขในหน้าตรวจสแกนก่อน แล้วผูกใหม่", null);
        }
        var a = ContactTaxBranchKey.NameCore(scanPartyName);
        var b = ContactTaxBranchKey.NameCore(predecessorContactName);
        if (a.Length > 0 && b.Length > 0
            && (a == b || (Math.Min(a.Length, b.Length) >= MinCoreForContainment && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)))))
            return new(null, null);
        if (a.Length == 0)
            return new(null, $"[LINK] ⚠ ยังไม่รู้คู่ค้าของสแกนนี้ และกระดาษไม่มีชื่อ/เลขให้เทียบ — ผูก {predecessorNumber} ของ “{predecessorContactName}” "
                + "ตามที่ผู้ใช้เลือก (ตรวจว่าเป็นคู่ค้ารายเดียวกันก่อนสร้างเอกสาร — ใบรับ/จ่ายเงินจะใช้ผู้ติดต่อของใบต้นทาง)");
        return new(null, $"[LINK] ⚠ ชื่อบนกระดาษ “{scanPartyName}” ไม่ตรงกับผู้ติดต่อของใบต้นทาง {predecessorNumber} “{predecessorContactName}” "
            + "— ผูกตามที่ผู้ใช้เลือก (ตรวจก่อนสร้างเอกสาร — ใบรับ/จ่ายเงินจะใช้ผู้ติดต่อของใบต้นทาง)");
    }
}
