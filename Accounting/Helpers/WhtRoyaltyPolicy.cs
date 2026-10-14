using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>ค่าตั้งบริษัท "ค่าสิทธิ ม.40(3) ที่จ่ายบุคคลธรรมดา ยื่นแบบไหน" (<c>Company.WhtRoyaltyIndividualForm</c> + <c>WhtRoyaltyPnd2From</c>) —
/// ค่าตั้งต้น (<c>default</c>) = ภ.ง.ด.3 ไม่มีวันเริ่ม = พฤติกรรมเดิม</summary>
public readonly record struct WhtRoyaltySetting(WhtRoyaltyIndividualForm Form, DateTime? Pnd2From)
{
    /// <summary>ใช้ ภ.ง.ด.2 กับค่าสิทธิบุคคลธรรมดาที่จ่าย ณ <paramref name="taxDate"/> ไหม (<see cref="WhtRoyaltyPolicy.AppliesTo"/>)</summary>
    public bool AppliesTo(DateTime taxDate) => WhtRoyaltyPolicy.AppliesTo(Form, Pnd2From, taxDate);
}

/// <summary>
/// **ค่าสิทธิ ม.40(3) จ่ายบุคคลธรรมดา — ยื่น ภ.ง.ด.2 หรือ ภ.ง.ด.3: ตัวตัดสินตัวเดียว** (รอบ 204 · c2 · คำสั่งเจ้าของ 2026-10-13
/// "ระบบห้ามล็อกแข็ง — ผู้ใช้ตัดสิน · ระบบแนะนำ/อำนวยความสะดวก")
///
/// <para>ข้อเท็จจริง: แบบ ภ.ง.ด.2 ของกรมสรรพากรใช้กับเงินได้ ม.40(3)(4) ที่จ่ายบุคคลธรรมดา (ม.50(2) · ประกาศอธิบดีฯ ฉบับที่ 58) แต่ระบบเดิม
/// (และผู้ใช้จำนวนมาก) ยื่นค่าสิทธิใน ภ.ง.ด.3 ⇒ <b>ค่าตั้งบริษัท</b>: ภ.ง.ด.3 (เดิม · ค่าตั้งต้น) / ภ.ง.ด.2 (แนะนำ) + วันที่เริ่มใช้
/// (เปิดครั้งแรก = วันที่ 1 ของเดือนปัจจุบัน · แก้ได้) — ใบที่จ่ายก่อนวันเริ่มยังเป็น ภ.ง.ด.3 (งวดที่ยื่นไปแล้วไม่ถูกย้ายแบบ)</para>
///
/// <para>ผลของ <see cref="AppliesTo"/> ส่งเข้า <c>royaltyAsPnd2</c> ของ <see cref="WhtPayeeKind.ResolveForm"/> /
/// <see cref="ThaiWhtRateTable.ClassifySection40_4"/> / <see cref="WhtPayableAccount.CodeChain"/> <b>ทุกทาง</b> (JE 21915 · 50 ทวิ ·
/// หน้านำส่ง · ปฏิทินยื่น · รายงาน) — ทางที่ไม่ส่ง = GL กับทะเบียนเล่าคนละแบบ · 50 ทวิ ที่ผู้ใช้ออกเอง: เลือก 2 หรือ 3 ได้ทั้งสองค่าตั้ง
/// (บังคับ ภ.ง.ด.2 เฉพาะ ม.40(4) ทั้งใบ — กติกาเดิม)</para>
/// </summary>
public static class WhtRoyaltyPolicy
{
    /// <summary>ป้ายคำเตือนก่อนอนุมัติ "แนะนำ ภ.ง.ด.2" (เตือน ไม่บล็อก — ผู้ใช้ตัดสิน) · ผู้เรียก: <c>CollectApprovalWarningsAsync</c> (ทีม A)</summary>
    public const string ApprovalWarningPrefix = "[WHT-FORM]";
    public const string RuleCode = "RD-50(2)-ROYALTY";
    public const string LegalReference = "ป.รัษฎากร ม.50(2) · ประกาศอธิบดีกรมสรรพากรเกี่ยวกับภาษีเงินได้ (ฉบับที่ 58)";

    public static bool IsApprovalWarning(string warning)
        => warning.StartsWith(ApprovalWarningPrefix, StringComparison.Ordinal);

    /// <summary>ค่าตั้ง ภ.ง.ด.2 มีผลกับการจ่าย ณ <paramref name="taxDate"/> ไหม — ภ.ง.ด.3 ⇒ false เสมอ (เดิม) ·
    /// ภ.ง.ด.2 ไม่มีวันเริ่ม ⇒ true · มีวันเริ่ม ⇒ วันที่จ่าย (ตามปฏิทินไทย) ≥ วันเริ่ม</summary>
    public static bool AppliesTo(WhtRoyaltyIndividualForm setting, DateTime? from, DateTime taxDate)
        => setting == WhtRoyaltyIndividualForm.Pnd2
           && (from is not DateTime f || ThaiDate.CalendarDateUtc(taxDate) >= ThaiDate.CalendarDateUtc(f));

    /// <summary>วันเริ่มใช้ตั้งต้นเมื่อผู้ใช้เปิด ภ.ง.ด.2 — วันที่ 1 ของเดือนปัจจุบัน (ปฏิทินไทย) · ผู้ใช้แก้ได้</summary>
    private static DateTime DefaultFromDate(DateTime utcNow)
    {
        var today = ThaiDate.CalendarDateUtc(utcNow);
        return new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>ค่าที่ต้องเก็บลงบริษัทหลังผู้ใช้บันทึกค่าตั้ง (server computes) — ไม่ส่งแบบ = ไม่แตะ · เปิด ภ.ง.ด.2 โดยไม่ระบุวัน ⇒ คงวันเดิม
    /// (ถ้าเคยเปิดอยู่แล้ว) ไม่งั้นวันที่ 1 ของเดือนนี้ · ระบุวัน ⇒ ใช้วันนั้น (ปฏิทินไทย) · ภ.ง.ด.3 ⇒ คงวันเดิมไว้เป็นประวัติ (ไม่มีผลเพราะ <see cref="AppliesTo"/> = false)</summary>
    public static WhtRoyaltySetting ApplyUpdate(WhtRoyaltySetting current, WhtRoyaltyIndividualForm? requestedForm,
        DateTime? requestedFrom, DateTime utcNow)
    {
        var form = requestedForm ?? current.Form;
        var from = requestedFrom is DateTime rf ? ThaiDate.CalendarDateUtc(rf) : current.Pnd2From;
        if (form == WhtRoyaltyIndividualForm.Pnd2 && requestedFrom == null
            && (current.Form != WhtRoyaltyIndividualForm.Pnd2 || from == null))
            from = DefaultFromDate(utcNow);
        return new WhtRoyaltySetting(form, from);
    }

    /// <summary>
    /// คำแนะนำก่อนอนุมัติ (ขึ้นต้น <see cref="ApprovalWarningPrefix"/> — คืนข้อความพร้อมป้ายแล้ว · null = ไม่ต้องแนะนำ) — ค่าตั้งยังเป็น ภ.ง.ด.3
    /// และผู้ถูกหักเป็นบุคคลธรรมดาในประเทศ (ตัวตัดสินเดียวกับแบบที่จะลง — <see cref="WhtPayeeKind.ResolveForm"/>) และ<b>ทุกบรรทัดที่หัก</b>เป็นค่าสิทธิ ม.40(3)
    /// <para>เตือน ไม่บล็อก — ค่าตั้งเป็นของผู้ใช้ (คำสั่งเจ้าของ 2026-10-13) · ค่าตั้ง ภ.ง.ด.2 ⇒ เงียบ (ผู้ใช้เลือกแล้ว — รวมใบที่จ่ายก่อนวันเริ่ม)</para>
    /// </summary>
    /// <param name="setting">ค่าตั้งของบริษัท (<c>Company.WhtRoyaltyIndividualForm</c>)</param>
    public static string? ApprovalWarning(WhtRoyaltyIndividualForm setting, bool documentIsForeignService, string? countryCode,
        string? taxId, ContactType contactType, string? name, IEnumerable<string?>? withheldIncomeTypeCodes)
    {
        if (setting != WhtRoyaltyIndividualForm.Pnd3) return null;
        var codes = withheldIncomeTypeCodes?.ToList();
        if (codes == null || codes.Count == 0 || !codes.All(ThaiWhtRateTable.IsSection40_3)) return null;
        if (WhtPayeeKind.ResolveForm(documentIsForeignService, countryCode, taxId, contactType, name, codes) != TaxType.WithholdingTax3)
            return null;
        return ApprovalWarningPrefix + " ระบบแนะนำยื่น ภ.ง.ด.2 (ประกาศอธิบดีฯ ฉบับที่ 58) — เปลี่ยนได้ที่ ตั้งค่า > ภาษี";
    }

    /// <summary>โหลดค่าตั้งของบริษัท (ไม่พบ = ค่าตั้งต้น ภ.ง.ด.3 = พฤติกรรมเดิม) · กรอง <c>Id == companyId</c></summary>
    public static async Task<WhtRoyaltySetting> LoadAsync(AccountingDbContext db, Guid companyId)
    {
        var row = await db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.WhtRoyaltyIndividualForm, c.WhtRoyaltyPnd2From })
            .FirstOrDefaultAsync();
        return row == null ? default : new WhtRoyaltySetting(row.WhtRoyaltyIndividualForm, row.WhtRoyaltyPnd2From);
    }
}
