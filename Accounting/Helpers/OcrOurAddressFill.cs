namespace Accounting.Helpers;

/// <summary>ผลการเติมที่อยู่ฝั่งเรา — ค่า · ความมั่นใจ (&lt; 0.85 เสมอ = ไฮไลต์เหลือง) · เหตุผลภาษาไทย</summary>
public sealed record OcrOurAddressFillResult(string Address, decimal Confidence, string Reason);

/// <summary>
/// **เติม "ที่อยู่ผู้ซื้อ" จากข้อมูลบริษัทเรา เมื่อกระดาษไม่มีที่อยู่ผู้ซื้อ** (รอบ 193 · คำตัดสินเจ้าของข้อ 26 · team-L Q5)
///
/// <para>═══ ทำไม ═══ §86/4 บังคับที่อยู่ผู้ซื้อบนใบกำกับเต็มรูป · กฎเหล็ก #3 ห้ามปล่อยช่องบังคับว่างให้ผู้ใช้กรอกซ้ำ ·
/// ใบจำนวนมากพิมพ์แค่ชื่อ/เลขผู้ซื้อ ไม่มีป้าย "ที่อยู่" ⇒ ตัวอ่าน <see cref="OcrBuyerAddressReader"/> (ต้องมีป้าย) ไม่ได้ค่า.
/// เมื่อตัวตัดสินคู่สัญญา (<see cref="OcrPartyResolver"/>) มั่นใจ ≥ 0.85 ว่าผู้ซื้อคือเรา ค่าที่ถูกที่สุดคือที่อยู่
/// <b>จากทะเบียนบริษัทเราเอง</b> (ไม่ใช่การแต่ง — เหตุผลเดียวกับ <c>FillOurIdentity</c>/<c>FillOurName</c>)</para>
///
/// <para>═══ กติกา ═══
/// (ก) เติม<b>เฉพาะช่องว่าง</b> — ที่อยู่ที่อ่านได้จากกระดาษ (ตัวอ่านป้าย/engine) ชนะเสมอ ห้ามทับ ·
/// (ข) ต้องเป็นฝั่งผู้ซื้อ + ความมั่นใจของฝั่ง ≥ 0.85 · (ค) สาขาผู้ซื้อบนกระดาษเป็น<b>สาขาอื่นของเรา</b> (ไม่ใช่ สนญ.
/// และไม่ตรงรหัสสาขาของบริษัท) ⇒ ไม่เติม (ที่อยู่ทะเบียนบริษัทอาจไม่ใช่ที่อยู่ของสาขานั้น — "ไม่รู้" ดีกว่าแต่ง) ·
/// (ง) ความมั่นใจ <see cref="FilledConfidence"/> &lt; 0.85 ⇒ ช่องขึ้นเหลืองพร้อมเหตุผล ให้ผู้ใช้เห็นว่าค่ามาจากฐานเรา
/// ไม่ใช่จากกระดาษ (DECISION_DOCTRINE G4)</para>
/// <para>ไม่ใช้กับ e-Tax XML (ผู้เรียกข้ามเอง) · pure ไม่มี I/O</para>
/// </summary>
public static class OcrOurAddressFill
{
    /// <summary>ต่ำกว่า 0.85 ตั้งใจ — ค่าจากฐานเรา ไม่ใช่จากกระดาษ ⇒ ไฮไลต์เหลือง</summary>
    public const decimal FilledConfidence = 0.70m;

    public static OcrOurAddressFillResult? ForBuyer(
        OcrSelfSide ourSide, decimal sideConfidence,
        string? currentBuyerAddress, string? paperBuyerBranch, OcrOurIdentity? us)
    {
        if (ourSide != OcrSelfSide.Buyer || sideConfidence < 0.85m) return null;
        if (!string.IsNullOrWhiteSpace(currentBuyerAddress)) return null;          // (ก) ห้ามทับที่อยู่ที่พิมพ์
        var ours = us?.Address?.Trim();
        if (string.IsNullOrEmpty(ours)) return null;

        if (!string.IsNullOrWhiteSpace(paperBuyerBranch)
            && !TaxBranchCode.IsHeadOffice(paperBuyerBranch)
            && TaxBranchCode.Normalize(paperBuyerBranch) != TaxBranchCode.Normalize(us!.BranchCode))
            return null;                                                          // (ค) สาขาอื่นของเรา

        return new OcrOurAddressFillResult(ours, FilledConfidence,
            "กระดาษไม่มีที่อยู่ผู้ซื้อ — เติมที่อยู่บริษัทเราจากข้อมูลบริษัท (ผู้ซื้อคือเรา มั่นใจ "
            + $"{sideConfidence:P0}) · ตรวจว่าใบกำกับระบุที่อยู่ผู้ซื้อครบตาม §86/4");
    }
}
