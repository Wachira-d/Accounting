namespace Accounting.Helpers;

/// <summary>บรรทัดของเอกสาร ณ จุดแบ่งผู้ถูกหัก — ผู้ถูกหักรายบรรทัด (null = ตามหัวเอกสาร) + ภาษีหักของบรรทัด</summary>
public readonly record struct WhtPayeeLine(Guid? LinePayeeContactId, decimal WithholdingTaxAmount);

/// <summary>ส่วนของผู้ถูกหักหนึ่งราย — ภาษีหักรวมของบรรทัดที่เป็นของผู้ถูกหักนี้ + ลำดับบรรทัด (index ในรายการที่ส่งเข้า)</summary>
public sealed record WhtPayeePortion(Guid PayeeContactId, decimal WithholdingTaxAmount, IReadOnlyList<int> LineIndexes);

/// <summary>
/// **ตัวแบ่ง "ใบเดียวหลายผู้ถูกหัก" ตัวเดียว** (คำตัดสินเจ้าของข้อ 148 ⇒ ทำข้อเสนอ 143 · รอบ 204)
///
/// <para>ที่มา: โบรกเกอร์ส่งเบี้ยของบริษัทประกัน 2 แห่งในใบสำคัญจ่ายใบเดียว — ผู้มีเงินได้คนละราย ⇒ 50 ทวิ ต้องแยกใบต่อผู้ถูกหัก
/// (หนึ่ง 50 ทวิ = ผู้ถูกหักหนึ่งราย) · แบบ ภ.ง.ด.3/53/54 และผังหนี้ 21916/21917/21918 ตัดสินต่อผู้ถูกหัก ไม่ใช่ต่อใบ</para>
///
/// <para>กติกา: ผู้ถูกหักของบรรทัด = ค่าบนบรรทัด (<c>DocumentLine.WhtPayeeContactId</c>) ถ้ามี ไม่งั้นผู้ถูกหักของหัวเอกสาร
/// (<see cref="WhtCertIssuePlan.PayeeOf"/>) · แบ่งเฉพาะบรรทัดที่มีภาษีหัก · ใบที่ไม่มีบรรทัดหัก (หักระดับหัวเอกสาร) = ส่วนเดียวของผู้ถูกหักหัวเอกสาร ·
/// <b>ใบผู้ถูกหักรายเดียว ⇒ ส่วนเดียวเสมอ — ทุกเส้น (50 ทวิ · JE · รายงาน) เดินทางเดิมทุกประการ</b></para>
/// </summary>
public static class WhtPayeeSplit
{
    public const string RuleCode = "RD-50BIS-MULTI-PAYEE";

    /// <summary>ผู้ถูกหักของบรรทัด — ค่าบนบรรทัดชนะ · ว่าง = ผู้ถูกหักของหัวเอกสาร</summary>
    public static Guid LinePayeeOf(Guid docContactId, Guid? docPayeeOverride, Guid? linePayee)
        => linePayee is Guid p && p != Guid.Empty ? p : WhtCertIssuePlan.PayeeOf(docContactId, docPayeeOverride);

    /// <summary>ค่าที่ควรเก็บลง <c>DocumentLine.WhtPayeeContactId</c> — ใบที่เราไม่ใช่ผู้หัก/บรรทัดไม่มีอัตราหัก/ว่าง/ตรงกับผู้ถูกหักหัวเอกสาร ⇒ null
    /// (ไม่เก็บสำเนาที่ไม่มีความหมาย · null = ตามหัวเอกสาร)</summary>
    public static Guid? NormalizeLineOverride(Guid docContactId, Guid? docPayeeOverride, Guid? requested,
        bool payerSide, decimal lineWithholdingTaxRate)
    {
        if (!payerSide || lineWithholdingTaxRate <= 0m) return null;
        if (requested is not Guid p || p == Guid.Empty) return null;
        return p == WhtCertIssuePlan.PayeeOf(docContactId, docPayeeOverride) ? null : p;
    }

    /// <summary>เอกสารลูก (แปลง · คัดลอก · รายการประจำ) สืบทอดผู้ถูกหัก — <b>ทั้งหัวเอกสารและรายบรรทัด ตัวเดียว</b> ·
    /// ปลายทางเป็นฝั่งผู้หัก = ค่าเดิม (null เป็น null) · ปลายทางฝั่งอื่น = null (ชนิดอื่นถูกปฏิเสธ — ห้ามพ่วงแล้วทำให้แปลงล้ม) ·
    /// ฝ่ายค้าน P3-6 (รอบ 204): ผู้ติดต่อที่ถูกลบไปแล้ว (ไม่อยู่ใน <paramref name="activeContacts"/> — <c>ContactHydration.ActiveContactIdsAsync</c>) ⇒ null
    /// (บรรทัด = ตามหัวเอกสาร · หัวเอกสาร = ผู้ติดต่อของเอกสาร) — เดิมพ่วงไปแล้วตัวตรวจตอนสร้างปฏิเสธ ⇒ แปลง/คัดลอก/รายการประจำล้มทุกครั้ง
    /// จนกว่าจะกู้ผู้ติดต่อ · ผู้เรียกต้องติดหมายเหตุ <see cref="DroppedInheritedPayeeNote"/> บนใบใหม่ (ไม่เปลี่ยนผู้ถูกหักเงียบ ๆ)</summary>
    public static Guid? InheritPayee(Guid? stored, bool targetIsPayerSide, IReadOnlySet<Guid> activeContacts)
        => targetIsPayerSide && stored is Guid p && p != Guid.Empty && activeContacts.Contains(p) ? p : null;

    /// <summary>จำนวนผู้ถูกหักที่ถูกตัดทิ้งตอนสืบทอดเพราะผู้ติดต่อถูกลบ (คู่กับ <see cref="InheritPayee"/>) — <b>นับเฉพาะที่มีผลกับภาษีหัก</b>
    /// (ฝ่ายค้านรอบ 204 หลัง 0f428969): ผู้ถูกหักรายบรรทัดนับเฉพาะบรรทัดที่มีภาษีหัก (<c>CarriesWht</c>) · ผู้ถูกหักหัวเอกสารนับเฉพาะเมื่อมีบรรทัดที่หัก
    /// "ตามหัวเอกสาร" (ไม่มีผู้ถูกหักรายบรรทัดที่สืบทอดได้) — บรรทัดที่ไม่หักภาษี/หัวเอกสารที่ไม่มีบรรทัดใดใช้ ถูกตัดทิ้งก็ไม่ทำให้ 50 ทวิ ผิดคน</summary>
    public static int DroppedInheritedWhtPayees(Guid? headerPayee, bool headerInherits,
        IReadOnlyList<(Guid? Payee, bool CarriesWht)> lines, bool linesInherit, IReadOnlySet<Guid> activeContacts)
    {
        bool Dropped(Guid? stored) => stored is Guid p && p != Guid.Empty && !activeContacts.Contains(p);
        var lineDropped = linesInherit ? lines.Count(l => l.CarriesWht && Dropped(l.Payee)) : 0;
        var headerUsed = lines.Any(l => l.CarriesWht && InheritPayee(l.Payee, linesInherit, activeContacts) == null);
        return lineDropped + (headerInherits && headerUsed && Dropped(headerPayee) ? 1 : 0);
    }

    /// <summary>ป้ายของ "ผู้ถูกหักที่สืบทอดถูกตัดทิ้ง" ภายในป้าย <see cref="WhtCertReviewNote"/> — ตัวตรวจ <see cref="PendingDroppedPayeeReview"/> อ่านจากนี้</summary>
    public const string DroppedPayeeTag = "[RD-50BIS-PAYEE-DROPPED]";

    /// <summary>เหตุผลของป้ายเมื่อผู้ถูกหักของต้นทางถูกลบ — ตรวจก่อนอนุมัติ (ล้มดังที่ตัวเอกสาร ไม่ใช่เปลี่ยนเงียบ) · 0 = null</summary>
    public static string? DroppedInheritedPayeeNote(string? sourceNumber, int dropped)
        => dropped <= 0 ? null
            : $"{DroppedPayeeTag} ผู้ถูกหักภาษีของเอกสารต้นทาง {sourceNumber} {dropped} รายการ (หัวเอกสาร/รายบรรทัดที่หักภาษี) ถูกลบออกจากผู้ติดต่อแล้ว — "
              + "ใบนี้ใช้ผู้ถูกหักของหัวเอกสาร/ผู้ติดต่อของเอกสารแทน ตรวจ/เลือกผู้ถูกหักใหม่ก่อนอนุมัติ (50 ทวิ ออกในนามผู้ถูกหัก)";

    /// <summary>
    /// ฝ่ายค้านรอบ 204 (หลัง 0f428969): ติดป้าย <b>"50 ทวิ ต้องตรวจ"</b> (<see cref="WhtCertReviewNote"/>) บนใบใหม่เมื่อผู้ถูกหักที่สืบทอดถูกตัดทิ้ง —
    /// เดิมเป็นหมายเหตุธรรมดาที่การอนุมัติ/การออกใบอัตโนมัติไม่อ่าน ⇒ 50 ทวิ ออกในนามผู้ติดต่อของเอกสาร (เช่นโบรกเกอร์แทนบริษัทประกัน) เงียบ ๆ ·
    /// ป้ายนี้: ลิสต์รอออกใบโชว์ "ต้องตรวจ" · <see cref="WhtCertIssuePlan.Decide"/> ไม่ออกอัตโนมัติ · คำเตือนก่อนอนุมัติ — ล้างด้วย "รับทราบ" ·
    /// ไม่มีอะไรถูกตัด ⇒ คืนค่าเดิมทุกตัวอักษร (ใบปกติไม่ถูกแตะ)
    /// </summary>
    public static string? MarkDroppedInheritedPayees(string? notes, string? sourceNumber, int dropped)
        => DroppedInheritedPayeeNote(sourceNumber, dropped) is string reason ? WhtCertReviewNote.Append(notes, reason) : notes;

    /// <summary>
    /// ผู้ถูกหักหัวเอกสารที่ "ผู้ใช้เปลี่ยนในคำขอนี้จริง" สำหรับด่าน <see cref="ExplicitHeaderConflictReason"/> — ฝ่ายค้านรอบ 204 (หลัง 7b88c053):
    /// ฟอร์มแก้ไข/โมดัลเปลี่ยนผู้ถูกหักส่งค่าหัวเอกสารที่โหลดมาเดิมกลับทุกครั้ง ⇒ เดิมลบบรรทัด/ตั้งทุกบรรทัดเป็นผู้ถูกหักเดียวกันแล้วโดน 400 ทั้งที่ไม่ได้แตะหัวเอกสาร ·
    /// ค่าเท่าค่าที่เก็บไว้ (หลัง normalize) ⇒ null (ไม่ถือว่าระบุมา → ยกผู้ถูกหักรายบรรทัดขึ้นหัวเอกสารตามปกติ) · ค่าใหม่ ⇒ คืนค่าที่ขอ
    /// </summary>
    public static Guid? ChangedHeaderRequest(Guid contactId, Guid? requested, Guid? stored)
        => WhtCertIssuePlan.NormalizeOverride(contactId, requested) is Guid p && p != WhtCertIssuePlan.NormalizeOverride(contactId, stored)
            ? p : null;

    /// <summary>
    /// ฝ่ายค้านรอบ 204 (หลัง 7b88c053): ป้าย "ผู้ถูกหักถูกตัดทิ้ง" ที่ยังไม่รับทราบบน<b>ต้นทาง</b>ต้องตามไปเอกสารลูกฝั่งผู้จ่าย (คัดลอก/แปลง) —
    /// ต้นทางเก็บผู้ถูกหักสำรองไว้แล้ว (เช่นโบรกเกอร์) ⇒ ลูกไม่มีอะไร "ถูกตัด" อีก ⇒ เดิมลูกไม่ติดป้ายแล้ว 50 ทวิ ออกผิดคนอัตโนมัติ (กฎ #4 A เอกสารลูกต้องสืบทอด) ·
    /// ลูกมีป้ายนี้อยู่แล้ว / ต้นทางไม่มี / ลูกไม่ใช่ฝั่งผู้จ่าย ⇒ คืนค่าเดิมทุกตัวอักษร
    /// </summary>
    public static string? InheritPendingDroppedReview(string? childNotes, string? sourceNotes, bool childIsPayerSide)
        => childIsPayerSide
           && PendingDroppedPayeeReview(childNotes) is null
           && PendingDroppedPayeeReview(sourceNotes) is string r
            ? WhtCertReviewNote.Append(childNotes, r)
            : childNotes;

    /// <summary>เหตุผลของป้าย "ผู้ถูกหักที่สืบทอดถูกตัดทิ้ง" ที่ยังไม่รับทราบ (null = ไม่มี) — ป้ายต้องตรวจเรื่องอื่น (ใบลดหนี้ ฯลฯ) ไม่นับ</summary>
    public static string? PendingDroppedPayeeReview(string? notes)
        => WhtCertReviewNote.Reasons(notes).FirstOrDefault(IsDroppedPayeeReason);

    /// <summary>คำเตือนก่อนอนุมัติของใบที่ยังมีป้ายผู้ถูกหักถูกตัดทิ้ง (ชุด <see cref="WhtCertIssuePlan.ApprovalWarningPrefix"/> — เว็บถามยืนยัน ·
    /// ทางเข้าไม่มีคนผ่านตามนโยบายเดิม แต่ 50 ทวิ ไม่ออกอัตโนมัติจนกว่าจะรับทราบ) · ไม่มีป้าย = null
    /// <para>ทีม A (กระดานตัดสิน 2026-10-13): ทางไปต่อชี้ที่ปุ่ม "รับทราบ" บนป้ายของหน้าเอกสารนี้เอง (ใช้ได้ทั้งใบร่าง) หรือเลือกผู้ถูกหักใหม่ในฟอร์มแก้ไข
    /// (ป้ายล้างเอง — <see cref="ClearDroppedPayeeReview"/>) · หน้า "เอกสารรอออกใบ" ยังรับทราบได้เหมือนเดิม</para></summary>
    public static string? DroppedPayeeApprovalWarning(string? notes)
        => PendingDroppedPayeeReview(notes) is string r
            ? $"{WhtCertIssuePlan.ApprovalWarningPrefix} {r} · 50 ทวิ จะไม่ออกอัตโนมัติจนกว่าจะตรวจผู้ถูกหัก — "
              + "เลือกผู้ถูกหักใหม่ในฟอร์มแก้ไขเอกสาร (ป้ายล้างให้เอง) หรือถ้าผู้ถูกหักที่ใช้อยู่ถูกแล้ว กด \"รับทราบ\" ที่ป้าย \"50 ทวิ ต้องตรวจ\" "
              + "บนหน้ารายละเอียดเอกสารนี้ (หรือหน้า \"หนังสือรับรองหัก ณ ที่จ่าย\" → \"เอกสารรอออกใบ\")"
            : null;

    /// <summary>รหัสกติกาของ audit "ป้ายผู้ถูกหักถูกตัดทิ้งล้างเองเพราะผู้ใช้เปลี่ยนผู้ถูกหักแล้ว"</summary>
    public const string DroppedPayeeResolvedRuleCode = "RD-50BIS-PAYEE-DROPPED-RESOLVED";

    /// <summary>
    /// ผู้ใช้เปลี่ยน<b>ชุดผู้ถูกหักที่มีผลจริง</b>ของใบไหม (ทีม A กระดานตัดสิน 2026-10-13) — เทียบชุด id ของส่วนจาก <see cref="Portions"/> ก่อน/หลัง ·
    /// ไม่สนลำดับ/ยอด/บรรทัด (เพิ่มบรรทัดที่หักของผู้ถูกหักเดิม ≠ เปลี่ยนผู้ถูกหัก) ·
    /// <b>ฝั่งใดว่าง ⇒ false</b>: ก่อนว่าง (ยังไม่หัก) แล้วมีส่วนของผู้ติดต่อเอกสาร = ระบบเติมให้ ไม่ใช่ผู้ใช้เลือก · หลังว่าง (ลบภาษีหักทิ้ง)
    /// = ไม่ได้เลือกผู้ถูกหักใหม่ — ป้ายต้องอยู่ (เพิ่มภาษีหักกลับมาทีหลังจะได้ผู้ติดต่อของเอกสารเดิมที่ยังไม่มีใครตรวจ)
    /// </summary>
    internal static bool PayeeSetChanged(IReadOnlyList<WhtPayeePortion> before, IReadOnlyList<WhtPayeePortion> after)
        => before.Count > 0 && after.Count > 0
           && !before.Select(p => p.PayeeContactId).ToHashSet().SetEquals(after.Select(p => p.PayeeContactId));

    /// <summary>
    /// ล้างป้าย <see cref="DroppedPayeeTag"/> <b>เฉพาะย่อหน้านั้น</b> เมื่อผู้ใช้เปลี่ยนชุดผู้ถูกหักจริง (<see cref="PayeeSetChanged"/>) —
    /// ป้ายเรื่องอื่น (ใบลดหนี้ลดฐาน · ยกเลิกการจ่าย) ไม่ถูกแตะด้วยการแก้เอกสาร (ต้อง "รับทราบ" พร้อมเหตุผลเท่านั้น) ·
    /// บันทึกซ้ำด้วยค่าเดิม/ไม่มีป้าย ⇒ (หมายเหตุเดิมทุกตัวอักษร, null) · ล้างแล้ว ⇒ Resolved = เหตุผลของป้ายที่ถูกล้าง (ผู้เรียกเขียน audit
    /// <see cref="DroppedPayeeResolvedRuleCode"/>)
    /// </summary>
    public static (string? Notes, string? Resolved) ClearDroppedPayeeReview(string? notes,
        IReadOnlyList<WhtPayeePortion> before, IReadOnlyList<WhtPayeePortion> after)
    {
        var pending = WhtCertReviewNote.Reasons(notes).Where(IsDroppedPayeeReason).ToList();
        if (pending.Count == 0 || !PayeeSetChanged(before, after)) return (notes, null);
        return (WhtCertReviewNote.ClearWhere(notes, IsDroppedPayeeReason), string.Join(" · ", pending));
    }

    private static bool IsDroppedPayeeReason(string reason) => reason.StartsWith(DroppedPayeeTag, StringComparison.Ordinal);

    /// <summary>เหตุผลที่รายการประจำ "ไม่อนุมัติอัตโนมัติ" ใบที่ผู้ถูกหักของเทมเพลตถูกตัดทิ้ง (ส่งเข้าเส้นรายงานการอนุมัติอัตโนมัติไม่สำเร็จเส้นเดียวกับเหตุอื่น)</summary>
    public static string DroppedPayeeAutoApproveSkipReason(int dropped)
        => $"ผู้ถูกหักภาษีของเทมเพลต {dropped} รายการ (ที่หักภาษี) ถูกลบออกจากผู้ติดต่อแล้ว — ใบนี้ใช้ผู้ถูกหักของหัวเอกสาร/ผู้ติดต่อแทน "
           + "จึงไม่อนุมัติอัตโนมัติ (50 ทวิ ออกในนามผู้ถูกหัก) · ตรวจ/เลือกผู้ถูกหัก แล้วอนุมัติเอง และกด \"รับทราบ\" ป้าย 50 ทวิ ต้องตรวจ";

    /// <summary>
    /// ฝ่ายค้านรอบ 204 (หลัง 0f428969): ผู้ถูกหักหัวเอกสาร<b>ที่ผู้เรียกระบุมาเอง</b> (<paramref name="explicitHeaderRequest"/> — ไม่ใช่ว่าง/Guid.Empty)
    /// ขัดกับผู้ถูกหักรายบรรทัด: ทุกบรรทัดที่มีภาษีหักระบุผู้ถูกหักรายเดียวกัน Y ที่ไม่ใช่หัวเอกสาร ⇒ หัวเอกสารไม่มีผลกับบรรทัดใดเลย และตัวยก
    /// (<see cref="LiftSinglePayee"/>) จะทับเป็น Y เงียบ ๆ ⇒ ปฏิเสธพร้อมทางไปต่อ · <paramref name="normalizedLines"/> = ผู้ถูกหักรายบรรทัดหลัง
    /// <see cref="NormalizeLineOverride"/> (ตรงกับหัวเอกสาร = null) · ไม่ระบุหัวเอกสาร/มีบรรทัดตามหัวเอกสาร/หลายราย ⇒ null (เส้นเดิม)
    /// </summary>
    public static string? ExplicitHeaderConflictReason(Guid? explicitHeaderRequest, IReadOnlyList<WhtPayeeLine> normalizedLines)
    {
        if (explicitHeaderRequest is not Guid h || h == Guid.Empty) return null;
        Guid? only = null;
        foreach (var l in normalizedLines)
        {
            if (l.WithholdingTaxAmount <= 0m) continue;
            if (l.LinePayeeContactId is not Guid p || p == Guid.Empty) return null;   // บรรทัดนี้ตามหัวเอกสาร ⇒ หัวเอกสารมีผล
            if (only is Guid o && o != p) return null;                                 // หลายราย ⇒ ไม่ยก
            only = p;
        }
        return only == null ? null
            : "ผู้ถูกหักภาษีหัวเอกสารที่เลือกไม่ตรงกับผู้ถูกหักรายบรรทัด — ทุกรายการที่หักภาษีระบุผู้ถูกหักรายอื่น (รายเดียวกันทุกบรรทัด) "
              + "ผู้ถูกหักหัวเอกสารจึงไม่มีผลกับรายการใดเลย · ทางไปต่อ: ตั้งผู้ถูกหักหัวเอกสารเป็นรายเดียวกับรายบรรทัด (แล้วล้างช่องรายบรรทัด) "
              + "หรือล้างผู้ถูกหักรายบรรทัดให้รายการตามหัวเอกสาร";
    }

    /// <summary>ค่าที่ผู้เรียก "ส่งมา" ตอนแก้ไขเอกสาร: null = ไม่ส่ง (คงค่าเดิมของบรรทัดลำดับ+คำอธิบายเดียวกัน — ฟอร์ม/API รุ่นเก่าต้องไม่ล้าง) ·
    /// Guid.Empty = ล้าง (กลับไปตามหัวเอกสาร) · id = ค่าใหม่</summary>
    public static Guid? RequestedOrKept(Guid? requested, IReadOnlyList<(string? Description, Guid? Payee)> previous,
        int index, string? description)
    {
        if (requested.HasValue) return requested.Value == Guid.Empty ? null : requested;
        if (index < 0 || index >= previous.Count) return null;
        var prev = previous[index];
        return string.Equals((prev.Description ?? "").Trim(), (description ?? "").Trim(), StringComparison.Ordinal)
            ? prev.Payee : null;
    }

    /// <summary>แบ่งภาษีหักของใบตามผู้ถูกหัก — ลำดับ: ผู้ถูกหักหัวเอกสารก่อน (ถ้ามีบรรทัดของเขา) แล้วตามลำดับที่พบบนใบ ·
    /// เศษ (ยอดหัวเอกสาร − Σ บรรทัด) ไปที่ส่วนของผู้ถูกหักหัวเอกสาร (ไม่มี = ส่วนแรก) ⇒ Σ ส่วน = ยอดหัวเอกสารเสมอ</summary>
    public static IReadOnlyList<WhtPayeePortion> Portions(Guid docContactId, Guid? docPayeeOverride,
        IReadOnlyList<WhtPayeeLine> lines, decimal docWithholdingTaxAmount)
    {
        var docPayee = WhtCertIssuePlan.PayeeOf(docContactId, docPayeeOverride);
        var order = new List<Guid>();
        var amounts = new Dictionary<Guid, decimal>();
        var idx = new Dictionary<Guid, List<int>>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].WithholdingTaxAmount <= 0m) continue;
            var p = LinePayeeOf(docContactId, docPayeeOverride, lines[i].LinePayeeContactId);
            if (!amounts.ContainsKey(p)) { order.Add(p); amounts[p] = 0m; idx[p] = new List<int>(); }
            amounts[p] += lines[i].WithholdingTaxAmount;
            idx[p].Add(i);
        }
        if (order.Count == 0)
            return docWithholdingTaxAmount > 0m
                ? new[] { new WhtPayeePortion(docPayee, docWithholdingTaxAmount, Array.Empty<int>()) }
                : Array.Empty<WhtPayeePortion>();
        if (order.Remove(docPayee)) order.Insert(0, docPayee);
        var residual = docWithholdingTaxAmount - amounts.Values.Sum();
        if (residual != 0m) amounts[order[0]] += residual;
        return order.Select(p => new WhtPayeePortion(p, amounts[p], idx[p])).ToList();
    }

    /// <summary>ใบนี้มีผู้ถูกหักมากกว่าหนึ่งรายไหม</summary>
    public static bool IsMultiPayee(IReadOnlyList<WhtPayeePortion> portions) => portions.Count > 1;

    /// <summary>เฉลี่ยยอด (เช่น ภาษีหักของ "งวดจ่ายนี้" หรือยอด WHT ที่ JE ลง) ไปตามสัดส่วนของแต่ละผู้ถูกหัก ·
    /// ยอดเท่ายอดรวมของส่วน = คืนยอดของแต่ละส่วนตรง ๆ · อื่น ๆ แบ่งเป็นสตางค์ด้วย <b>เศษเหลือมากสุด</b> (largest remainder) ⇒
    /// Σ = ยอดที่ขอเป๊ะ และ<b>ทุกส่วนมีเครื่องหมายเดียวกับยอด</b> (ฝ่ายค้าน P3-5 รอบ 204: เดิม "ส่วนสุดท้ายรับเศษ" ⇒ 4 ส่วนเท่ากัน ยอด 0.02
    /// ได้ 0.01/0.01/0.01/−0.01 = หนี้ WHT ติดลบของผู้ถูกหักรายหนึ่ง) · ใบรายเดียว = ยอดทั้งก้อน (identity)</summary>
    public static IReadOnlyList<(Guid PayeeContactId, decimal Amount)> Allocate(IReadOnlyList<WhtPayeePortion> portions, decimal amount)
    {
        if (portions.Count == 0) return Array.Empty<(Guid, decimal)>();
        if (portions.Count == 1) return new[] { (portions[0].PayeeContactId, amount) };
        var total = portions.Sum(p => p.WithholdingTaxAmount);
        if (total == amount) return portions.Select(p => (p.PayeeContactId, p.WithholdingTaxAmount)).ToList();
        // น้ำหนัก = ภาษีหักของแต่ละส่วน (ส่วนที่ติดลบ/ศูนย์ไม่รับส่วนแบ่ง) · ไม่มีน้ำหนักเลย ⇒ ทั้งก้อนไปส่วนแรก (ผู้ถูกหักหัวเอกสาร — กติกาเศษเดียวกับ Portions)
        var weights = portions.Select(p => Math.Max(p.WithholdingTaxAmount, 0m)).ToList();
        var weightTotal = weights.Sum();
        var result = portions.Select(p => (p.PayeeContactId, Amount: 0m)).ToList();
        if (weightTotal == 0m)
        {
            result[0] = (result[0].PayeeContactId, amount);
            return result;
        }
        var sign = amount < 0m ? -1m : 1m;
        var abs = Math.Abs(amount);
        var satang = Math.Round(abs * 100m, 0, MidpointRounding.AwayFromZero);
        var floors = new decimal[portions.Count];
        var remainders = new decimal[portions.Count];
        for (var i = 0; i < portions.Count; i++)
        {
            var exact = satang * weights[i] / weightTotal;
            floors[i] = Math.Floor(exact);
            remainders[i] = exact - floors[i];
        }
        var left = satang - floors.Sum();
        var byRemainder = Enumerable.Range(0, portions.Count)
            .OrderByDescending(k => remainders[k]).ThenBy(k => k).ToList();
        foreach (var idx in byRemainder)
        {
            if (left <= 0m) break;
            floors[idx] += 1m;
            left -= 1m;
        }
        for (var i = 0; i < portions.Count; i++)
            result[i] = (portions[i].PayeeContactId, sign * floors[i] / 100m);
        // ยอดที่มีทศนิยมเกินสองตำแหน่ง (ไม่ควรเกิดกับเงิน) — เศษใต้สตางค์ไปส่วนที่มากที่สุด ⇒ Σ เป๊ะเสมอ
        var subSatang = amount - result.Sum(r => r.Amount);
        if (subSatang != 0m)
        {
            var biggest = Enumerable.Range(0, result.Count).OrderByDescending(k => Math.Abs(result[k].Amount)).First();
            result[biggest] = (result[biggest].PayeeContactId, result[biggest].Amount + subSatang);
        }
        return result;
    }

    /// <summary>
    /// <b>ผู้ถูกหักหัวเอกสาร "ที่มีผลจริง"</b> (ฝ่ายค้าน P1 รอบ 204) — ทุกบรรทัดที่มีภาษีหักเป็นของผู้ถูกหักรายเดียว Y (ตั้งรายบรรทัดทุกบรรทัด) ⇒
    /// ใบนี้คือ "ใบรายเดียวของ Y" ไม่ใช่ของผู้ถูกหักหัวเอกสาร X · ค่าที่ควรเก็บลง <c>Document.WhtPayeeContactId</c> (normalize แล้ว) ·
    /// ใบหลายผู้ถูกหัก/ไม่มีบรรทัดหัก ⇒ ค่าหัวเอกสารเดิม
    /// <para>ที่มา: <see cref="Portions"/> คืนส่วนเดียวของ Y (JE ลงผังของ Y) แต่ 50 ทวิ/พรีวิว GL/ช่องโหว่หน้านำส่ง อ่านหัวเอกสาร X ⇒ คนละคน</para>
    /// </summary>
    private static Guid? EffectivePayeeOverride(Guid docContactId, Guid? docPayeeOverride, IReadOnlyList<WhtPayeeLine> lines)
    {
        Guid? only = null;
        foreach (var l in lines)
        {
            if (l.WithholdingTaxAmount <= 0m) continue;
            var p = LinePayeeOf(docContactId, docPayeeOverride, l.LinePayeeContactId);
            if (only is Guid o && o != p) return WhtCertIssuePlan.NormalizeOverride(docContactId, docPayeeOverride);
            only = p;
        }
        return WhtCertIssuePlan.NormalizeOverride(docContactId, only ?? docPayeeOverride);
    }

    /// <summary>ผู้ถูกหักของ "ใบรายเดียว" — ตัวอ่านเดียวของทุกเส้นที่ตัดสินผู้ถูกหักทั้งใบ (50 ทวิ · พรีวิว GL · ช่องโหว่นำส่ง · ลิสต์รอออกใบ ·
    /// ตัวโหลดผู้ถูกหัก <c>ContactHydration</c>) — ตรงกับส่วนเดียวที่ <see cref="Portions"/> คืน · ใบหลายผู้ถูกหัก = ผู้ถูกหักหัวเอกสาร (ผู้เรียกแยกต่อรายเอง)</summary>
    public static Guid EffectiveSinglePayee(Guid docContactId, Guid? docPayeeOverride, IReadOnlyList<WhtPayeeLine> lines)
        => WhtCertIssuePlan.PayeeOf(docContactId, EffectivePayeeOverride(docContactId, docPayeeOverride, lines));

    /// <summary>บรรทัดของเอกสาร (ไม่ลบ · ตามลำดับ) ในรูปที่ตัวแบ่งรับ</summary>
    public static IReadOnlyList<WhtPayeeLine> LinesOf(Accounting.Models.Entities.Document doc)
        => doc.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.LineOrder)
            .Select(l => new WhtPayeeLine(l.WhtPayeeContactId, l.WithholdingTaxAmount)).ToList();

    /// <summary><see cref="EffectiveSinglePayee(Guid, Guid?, IReadOnlyList{WhtPayeeLine})"/> ของเอกสาร (ต้องโหลดบรรทัดมาแล้ว — ไม่มีบรรทัด = หัวเอกสาร)</summary>
    public static Guid EffectiveSinglePayee(Accounting.Models.Entities.Document doc)
        => EffectiveSinglePayee(doc.ContactId, doc.WhtPayeeContactId, LinesOf(doc));

    /// <summary>
    /// <b>ยกผู้ถูกหักรายบรรทัดขึ้นหัวเอกสารตอนบันทึก</b> (ฝ่ายค้าน P1 รอบ 204 · ตัวเดียวของ สร้าง/แก้ไข/เปลี่ยนผู้ถูกหักหลังอนุมัติ) —
    /// ทุกบรรทัดที่หักเป็นของ Y ≠ หัวเอกสาร ⇒ หัวเอกสาร = Y และบรรทัด = null (ข้อมูลที่เก็บไม่มีรูป "หัว X แต่ทุกบรรทัด Y" อีก) ·
    /// ใบอื่น ⇒ ค่าเดิม (บรรทัดที่ตรงกับหัวเอกสาร = null เหมือน <see cref="NormalizeLineOverride"/>)
    /// </summary>
    public static (Guid? DocPayeeOverride, IReadOnlyList<Guid?> LineOverrides) LiftSinglePayee(
        Guid docContactId, Guid? docPayeeOverride, IReadOnlyList<WhtPayeeLine> lines)
    {
        var lifted = EffectivePayeeOverride(docContactId, docPayeeOverride, lines);
        var docPayee = WhtCertIssuePlan.PayeeOf(docContactId, lifted);
        return (lifted, lines.Select(l => l.LinePayeeContactId is Guid p && p != Guid.Empty && p != docPayee ? (Guid?)p : null).ToList());
    }

    /// <summary>
    /// ฝ่ายค้าน P2c (รอบ 204): ใบลด/เพิ่มหนี้ที่อ้างใบต้นทาง<b>หลายผู้ถูกหัก</b> — ทุกบรรทัดที่มีภาษีหักต้องอ้างบรรทัดของใบต้นทาง (SourceLineId)
    /// ไม่งั้นระบบไม่รู้ว่าลด/เพิ่มหนี้ภาษีของผู้ถูกหักรายใด (เดิมตกไปผังของผู้ถูกหักหัวเอกสารเงียบ ๆ) ⇒ ข้อความปฏิเสธพร้อมทางไปต่อ ·
    /// ใบต้นทางรายเดียว/ทุกบรรทัดอ้างครบ ⇒ null (เส้นเดิม)
    /// <para>ฝ่ายค้านรอบ 204 (หลัง 0f428969): ทางไปต่อต้องทำได้จริง — เดิมแนะ "แยกใบลดหนี้ต่อผู้ถูกหัก" ซึ่งยังไม่อ้างบรรทัด (ด่านเดิมฟ้องซ้ำ) และแนะ "แปลง"
    /// แม้ชนิดใบต้นทางไม่มีเส้นแปลงเป็นใบลด/เพิ่มหนี้ (ใบรับรองแทนใบเสร็จ) ⇒ <paramref name="sourceConvertsToNote"/> = มีเส้นแปลง (ตาราง ValidConversions)
    /// · ไม่มี ⇒ ทางไปต่อ = อ้างเอกสารตั้งหนี้ที่ใบนั้นจ่ายชำระ (แปลงจากเอกสารนั้น) หรือยกเลิกใบต้นทางแล้วออกใหม่ด้วยยอดที่ถูกต้อง</para>
    /// </summary>
    public static string? UnmappedAdjustmentLineReason(string? noteNumber, string? sourceNumber, bool sourceIsMultiPayee,
        IReadOnlyList<(string? Description, decimal WithholdingTaxAmount, bool MappedToSourceLine)> lines, bool sourceConvertsToNote)
    {
        if (!sourceIsMultiPayee) return null;
        var bad = lines.Where(l => l.WithholdingTaxAmount > 0m && !l.MappedToSourceLine).Select(l => l.Description).ToList();
        if (bad.Count == 0) return null;
        var names = string.Join(" · ", bad);
        var head = $"ใบลด/เพิ่มหนี้ {noteNumber}: รายการ \"{names}\" มีภาษีหัก ณ ที่จ่ายแต่ไม่ได้อ้างบรรทัดของใบ {sourceNumber} "
                   + "ซึ่งมีผู้ถูกหักภาษีหลายราย — ระบบไม่รู้ว่าปรับหนี้ภาษีของผู้ถูกหักรายใด · ";
        return sourceConvertsToNote
            ? head + $"ทางไปต่อ: ลบใบร่างนี้แล้วสร้างใบลด/เพิ่มหนี้ด้วยการ \"แปลง\" จากใบ {sourceNumber} (เลือกรายการ/จำนวนที่ปรับ — ระบบผูกบรรทัดให้ "
                   + "ใบเดียวปรับได้หลายผู้ถูกหัก) แทนการพิมพ์รายการเอง"
            : head + $"ใบ {sourceNumber} แปลงเป็นใบลด/เพิ่มหนี้ไม่ได้ · ทางไปต่อ: ถ้าใบนี้จ่ายชำระเอกสารตั้งหนี้ (ค่าใช้จ่าย/ใบแจ้งหนี้ซื้อ) ให้ \"แปลง\" "
                   + "ใบลด/เพิ่มหนี้จากเอกสารตั้งหนี้นั้น · ถ้าไม่ใช่ ให้ยกเลิกใบ " + sourceNumber + " แล้วออกใหม่ด้วยยอดที่ถูกต้อง "
                   + "(ตรวจ/ยกเลิก 50 ทวิ ของใบเดิมด้วย)";
    }

    /// <summary>ผู้ถูกหักที่ยังไม่มี 50 ทวิ (ใบที่ยังไม่ยกเลิก) — ใบรายเดียว: มีใบใดก็ตาม = ครบ (พฤติกรรมเดิม · ใบเก่าที่ออกในนามอื่นด้วยมือ
    /// ไม่ถูกนับว่าขาด) · ใบหลายราย: ตรวจต่อผู้ถูกหัก</summary>
    public static IReadOnlyList<Guid> UncoveredPayees(IReadOnlyList<WhtPayeePortion> portions, IReadOnlyCollection<Guid> activeCertPayees)
    {
        if (portions.Count <= 1)
            return activeCertPayees.Count > 0 ? Array.Empty<Guid>() : portions.Select(p => p.PayeeContactId).ToList();
        var have = activeCertPayees.ToHashSet();
        return portions.Where(p => !have.Contains(p.PayeeContactId)).Select(p => p.PayeeContactId).ToList();
    }
}
