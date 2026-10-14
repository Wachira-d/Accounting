using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ผังบัญชี "ภาษีหัก ณ ที่จ่ายค้างจ่าย" ที่ต้องใช้ — ตัวตัดสินตัวเดียวของระบบ** (pure ไม่มี I/O)
///
/// ═══ ที่มา (ผลตรวจทีมสายข้อมูล รอบ 180) ═══
/// การเลือก 21916 / 21917 / 21918 ถูกตัดสินจาก <b>4 ที่</b> ด้วยกติกาคนละชุด:
/// <list type="bullet">
/// <item><c>DocumentService.ResolveWhtPayableAccountAsync</c> — ผ่าน <see cref="WhtPayeeKind"/> ✅</item>
/// <item><c>PdfGenerationService</c> (พรีวิว GL ก่อนอนุมัติ) — <c>ContactType</c> ดิบ ⇒
/// <b>พรีวิวโชว์บัญชีหนึ่ง แต่ JE จริงลงอีกบัญชี</b> (defect class "สอง renderer ห้าม drift")</item>
/// <item><c>IntegrationService</c> ตั้งหนี้ค่าใช้จ่าย — <c>ContactType</c> ดิบ</item>
/// <item><c>IntegrationService</c> จ่ายเงิน — <c>TaxId.StartsWith("0")</c> ดิบ</item>
/// </list>
/// และ <b>สามที่หลังไม่รู้จัก 21918 (ภ.ง.ด.54) เลย</b> ⇒ WHT ของการจ่ายต่างประเทศ
/// ตกไปกอง ภ.ง.ด.3/53 ⇒ ตอนกดนำส่งจะ Dr บัญชีที่ไม่มียอด ⇒ ยอดค้างที่ล้างไม่ได้ตลอดกาล
///
/// <para>⚠️ คืน <b>ลำดับรหัสที่ต้องลองตามลำดับ</b> ไม่ใช่รหัสเดียว — ผังบัญชีที่ตั้งไม่ครบ
/// ต้องยังลงบัญชีได้ (ตกไปตัวที่มีอยู่) แทนที่จะ throw แล้วทำให้เอกสารลง GL ไม่ได้เลย</para>
/// </summary>
public static class WhtPayableAccount
{
    /// <summary>ภ.ง.ด.2 — ดอกเบี้ย/เงินปันผล ม.40(4) จ่ายบุคคลธรรมดา (ม.50(2) · ม.59) · รอบ 204 คำตัดสินข้อ 148 ·
    /// แถวนี้อยู่ในผังมาตรฐาน (<c>ChartOfAccountTemplates</c>) ตั้งแต่แรก แต่ไม่เคยมีผู้ลงบัญชี (นอกจากทิปที่ลงผิดผัง — แก้แล้ว
    /// <c>TipPayoutService</c>) · บริษัทเดิมที่ไม่มีแถวนี้ได้จาก <see cref="MigrationSeedSql"/></summary>
    public const string Pnd2Code = "21915";
    /// <summary>ภ.ง.ด.3 — ผู้รับเป็นบุคคลธรรมดา</summary>
    public const string Pnd3Code = "21916";
    /// <summary>ภ.ง.ด.53 — ผู้รับเป็นนิติบุคคลไทย</summary>
    public const string Pnd53Code = "21917";
    /// <summary>ภ.ง.ด.54 — จ่ายออกต่างประเทศ (ม.70) · คนละแบบ คนละกำหนดยื่น</summary>
    public const string Pnd54Code = "21918";

    /// <summary>ผังค้างจ่ายภาษีหัก ณ ที่จ่ายของ "ผู้จ่าย" ทุกแบบ (ภ.ง.ด.2/3/53/54) — ชุดเดียวให้ด่าน JE/เครื่องมือย้ายผัง/กระทบยอดอ่าน
    /// (ภ.ง.ด.1 21914 ไม่อยู่ในชุดนี้ — มาจากรอบเงินเดือน ไม่ใช่เอกสารซื้อ)</summary>
    public static readonly IReadOnlyList<string> DocumentPayableCodes = new[] { Pnd2Code, Pnd3Code, Pnd53Code, Pnd54Code };

    /// <summary>รหัสผังค้างจ่ายของแบบ ภ.ง.ด. หนึ่งแบบ (null = ไม่ใช่แบบที่เอกสารซื้อตั้งหนี้)</summary>
    public static string? CodeFor(TaxType form) => form switch
    {
        TaxType.WithholdingTax2 => Pnd2Code,
        TaxType.WithholdingTax3 => Pnd3Code,
        TaxType.WithholdingTax53 => Pnd53Code,
        TaxType.WithholdingTax54 => Pnd54Code,
        _ => null,
    };

    /// <summary>รหัสที่ต้องลองตามลำดับ (ตัวแรก = ที่ถูกต้อง · ที่เหลือ = ตาข่ายกันผังไม่ครบ)</summary>
    /// <param name="withheldIncomeTypeCodes">รหัสเงินได้ของบรรทัดที่หัก (<see cref="WhtIncomeCodes"/>) — ต้องส่งทุกทางที่มีเอกสาร
    /// ไม่งั้นใบดอกเบี้ย/ปันผลของบุคคลธรรมดาลง 21916 แทน 21915 (รอบ 204)</param>
    /// <param name="royaltyAsPnd2">ค่าสิทธิ ม.40(3) บุคคลธรรมดา ⇒ ภ.ง.ด.2/21915 (<see cref="WhtRoyaltyPolicy.AppliesTo"/> · รอบ 204 c2) · false = เดิม</param>
    public static IReadOnlyList<string> CodeChain(
        bool isForeignService, string? countryCode, string? taxId, ContactType contactType, string? name,
        IEnumerable<string?>? withheldIncomeTypeCodes = null, bool royaltyAsPnd2 = false)
        => Form(isForeignService, countryCode, taxId, contactType, name, withheldIncomeTypeCodes, royaltyAsPnd2) switch
        {
            TaxType.WithholdingTax54 => new[] { Pnd54Code, Pnd53Code, Pnd3Code },
            TaxType.WithholdingTax53 => new[] { Pnd53Code, Pnd3Code },
            // ภ.ง.ด.2: ผังไม่ครบ ⇒ ตกไป 21916 (บุคคลธรรมดาเหมือนกัน) — migration ใส่ 21915 ให้บริษัทเดิมที่มีกลุ่ม 219 แล้ว
            TaxType.WithholdingTax2 => new[] { Pnd2Code, Pnd3Code, Pnd53Code },
            _ => new[] { Pnd3Code, Pnd53Code },
        };

    /// <summary>ชื่อบัญชีที่ควรแสดงคู่กับรหัสตัวแรกของ <see cref="CodeChain"/>
    /// — ให้พรีวิวกับ JE จริงพูดตรงกันคำต่อคำ</summary>
    public static string PreferredLabel(
        bool isForeignService, string? countryCode, string? taxId, ContactType contactType, string? name,
        IEnumerable<string?>? withheldIncomeTypeCodes = null, bool royaltyAsPnd2 = false)
        => Form(isForeignService, countryCode, taxId, contactType, name, withheldIncomeTypeCodes, royaltyAsPnd2) switch
        {
            TaxType.WithholdingTax54 => "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 54",
            TaxType.WithholdingTax53 => "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 53",
            TaxType.WithholdingTax2 => "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 2",
            _ => "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 3",
        };

    /// <summary>แบบ ภ.ง.ด. ที่ผู้รับรายนี้อยู่ — ผ่าน <see cref="WhtPayeeKind"/> ตัวเดียว
    /// ห้ามเขียนกติกา "เลขขึ้นต้น 0" หรือ "ContactType ดิบ" ซ้ำที่ไหนอีก</summary>
    private static TaxType Form(
        bool isForeignService, string? countryCode, string? taxId, ContactType contactType, string? name,
        IEnumerable<string?>? withheldIncomeTypeCodes, bool royaltyAsPnd2)
        => WhtPayeeKind.ResolveForm(isForeignService, countryCode, taxId, contactType, name, withheldIncomeTypeCodes, royaltyAsPnd2);

    // ═══ รอบ 204 (c3) — e-Withholding: ธนาคารหัก/นำส่งแทน ═══

    /// <summary>ผัง "ภาษีหัก ณ ที่จ่ายรอธนาคารนำส่ง (e-Withholding)" — หนี้ WHT ของเอกสารที่ติ๊ก <c>Document.WhtViaEWithholding</c> ลงที่นี่แทน 2191x
    /// (ธนาคารหักจากบัญชีผู้จ่ายแล้วนำส่งกรมสรรพากรเอง ⇒ ไม่อยู่ในยอดนำส่ง ภ.ง.ด. ของเรา) · ล้างด้วยรายการบัญชีเมื่อเห็นยอดตัดจากธนาคาร ·
    /// ค้างเกิน <see cref="EWithholdingClearing.ReminderAgeDays"/> วัน ⇒ ปฏิทินภาษีเตือน ·
    /// ⚠️ 21920 ในผังมาตรฐานคือ "ภาษีเงินได้นิติบุคคลค้างจ่าย" — ห้ามใช้ · <b>ไม่อยู่ใน</b> <see cref="DocumentPayableCodes"/> (ไม่ใช่หนี้ที่เรานำส่งเอง)</summary>
    public const string EWithholdingCode = "21923";
    public const string EWithholdingLabel = "ภาษีหัก ณ ที่จ่ายรอธนาคารนำส่ง (e-Withholding)";

    /// <summary>INSERT ผัง 21915 (ภ.ง.ด.2) + 21923 (e-Withholding) ให้<b>บริษัทเดิม</b>ที่มีกลุ่ม 219 แต่ไม่มีแถวนี้ — ตัวสร้าง SQL เดียวกับผังมาตรฐานของ Settlement
    /// (<see cref="SettlementChartSeed.SeedSqlFor"/> · <c>ON CONFLICT DO NOTHING</c> ⇒ รันทุกบูตได้ · ไม่ทับชื่อที่ลูกค้าตั้งเอง · ไม่ย้ายยอด)</summary>
    public static string MigrationSeedSql() => SettlementChartSeed.SeedSqlFor(new[] { Pnd2Code, EWithholdingCode });
}

/// <summary>
/// **ยอดค้างผัง e-Withholding (21923) ที่เก่าที่สุด** — pure (ผู้เรียกโหลดรายการ GL ของบริษัทเดียว) · ตัดยอดแบบ FIFO: Cr = ภาษีที่หักรอธนาคารนำส่ง ·
/// Dr = ล้างยอด (ตัดรายการเก่าสุดก่อน) · คืนวันที่ของรายการ Cr ที่ยังค้างเก่าที่สุด (null = ไม่มียอดค้าง)
/// <para>ผู้อ่าน: <c>TaxCalendarService</c> (แถวเตือนเมื่อค้างเกิน <see cref="ReminderAgeDays"/> วัน) — ธนาคารควรนำส่งภายในวันทำการไม่กี่วัน
/// ยอดที่ค้างนาน = ยังไม่ได้ลงรายการล้าง หรือธนาคารไม่ได้นำส่งจริง (ผู้ใช้ต้องตรวจ — ระบบไม่ประทับว่า "นำส่งแล้ว" เอง · หลักการ #3)</para>
/// </summary>
public static class EWithholdingClearing
{
    public const int ReminderAgeDays = 30;
    public const string ReminderFormCode = "e-Withholding";
    /// <summary>id คงที่ของแถวเตือนบนปฏิทินภาษี (แถวคำนวณสด ไม่ได้เก็บในตาราง — แก้สถานะที่แถวนี้ไม่ได้ · ล้างยอด 21923 แล้วแถวหายเอง)</summary>
    public static readonly Guid ReminderEventId = new("6e5f0c1a-2192-4a00-9e00-000000021923");

    /// <summary>ชื่อแถวเตือน (server computes · page displays)</summary>
    public static string ReminderTitle(DateTime oldestOpenDate)
        => $"ภาษีหัก ณ ที่จ่ายรอธนาคารนำส่ง (e-Withholding · ผัง {WhtPayableAccount.EWithholdingCode}) ค้างตั้งแต่ "
           + ThaiDate.CalendarDateUtc(oldestOpenDate).ToString("dd/MM/", System.Globalization.CultureInfo.InvariantCulture)
           + (ThaiDate.CalendarDateUtc(oldestOpenDate).Year + 543)
           + $" เกิน {ReminderAgeDays} วัน — ตรวจว่าธนาคารนำส่งแล้ว แล้วบันทึกรายการล้างยอด (Dr {WhtPayableAccount.EWithholdingCode})";

    /// <summary>ข้อความเมื่อผู้ใช้พยายามแก้สถานะแถวเตือน — แถวนี้คำนวณจากยอดผัง ไม่ใช่รายการที่เก็บไว้</summary>
    public const string ReminderNotEditable =
        "แถวนี้คำนวณจากยอดคงค้างผัง 21923 (e-Withholding) — แก้สถานะไม่ได้ · เมื่อธนาคารนำส่งแล้ว บันทึกรายการบัญชี Dr 21923 / Cr เงินฝากธนาคาร แถวนี้จะหายเอง";

    /// <summary>(วันที่ของ Cr ที่ค้างเก่าสุด, ยอดค้างรวม) · ยอดคงเหลือ ≤ 0 ⇒ (null, ยอด)</summary>
    public static (DateTime? OldestOpenDate, decimal Outstanding) Oldest(IEnumerable<(DateTime Date, decimal Debit, decimal Credit)> lines)
    {
        var ordered = lines.OrderBy(l => l.Date).ToList();
        var debits = ordered.Sum(l => l.Debit);
        var outstanding = ordered.Sum(l => l.Credit) - debits;
        if (outstanding <= 0.005m) return (null, outstanding);
        var toClear = debits;
        foreach (var l in ordered.Where(l => l.Credit > 0m))
        {
            if (toClear >= l.Credit) { toClear -= l.Credit; continue; }
            return (l.Date, outstanding);
        }
        return (null, outstanding);
    }

    /// <summary>ต้องเตือนไหม — ยอดค้างที่เก่าที่สุดเกิน <see cref="ReminderAgeDays"/> วัน (นับวันตามปฏิทินไทย)</summary>
    public static bool NeedsReminder(DateTime? oldestOpenDate, DateTime utcNow)
        => oldestOpenDate is DateTime d
           && (ThaiDate.CalendarDateUtc(utcNow) - ThaiDate.CalendarDateUtc(d)).TotalDays > ReminderAgeDays;
}

/// <summary>
/// **เอกสารนี้หักภาษีผ่าน e-Withholding ไหม — ตัวตัดสินเดียว** (รอบ 204 · c3) · ธงอยู่ที่เอกสารฝั่งผู้จ่าย (<c>Document.WhtViaEWithholding</c>) ·
/// เอกสารลูกที่อ้างใบฝั่งผู้จ่ายที่ติ๊ก (ใบสำคัญจ่ายที่จ่ายใบตั้งหนี้ · ใบลด/เพิ่มหนี้) = e-Withholding ตามใบต้นทาง (ธนาคารหักไปกับการจ่ายเดียวกัน ·
/// กลับรายการเข้าผังเดียวกับที่ใบต้นทางตั้งไว้)
/// <para>ผู้อ่านทุกตัวต้องผ่านที่นี่ (JE 21923 · พรีวิว GL · ตัดสินออก 50 ทวิ · ลิสต์รอออกใบ · หน้านำส่ง/ปฏิทิน · รายงาน/ไฟล์ ภ.ง.ด.) —
/// สองทางที่ตัดสินคนละกติกา = GL 2191x มียอดที่ไม่มีภาระนำส่ง (หรือกลับกัน)</para>
/// </summary>
public static class EWithholdingScope
{
    /// <summary>คำนำหน้าแถวข้อมูลบนรายงาน ภ.ง.ด. ของ 50 ทวิ ที่ออกเองให้ใบ e-Withholding (ติ๊กออก · ไม่นับยอด) — ไม่ใช่ป้าย "ยังไม่ออกใบ"
    /// (<c>WhtUnissuedCertGate</c> ไม่บล็อกไฟล์ยื่นด้วยแถวนี้)</summary>
    public const string ReportMarker = "ℹ️ หักผ่าน e-Withholding (ธนาคารนำส่งแล้ว — ไม่ยื่นซ้ำ)";

    /// <summary>กติกา pure — ธงของใบเอง หรือใบต้นทางฝั่งผู้จ่ายที่ติ๊ก</summary>
    public static bool IsVia(bool ownFlag, DocumentType? sourceType, bool sourceFlag)
        => ownFlag || (sourceType is DocumentType st && WhtCertIssuePlan.AppliesTo(st) && sourceFlag);

    /// <summary>เอกสารเดียว (โหลดใบต้นทางเมื่อจำเป็น · กรอง <c>CompanyId</c>)</summary>
    public static async Task<bool> IsViaAsync(AccountingDbContext db, Guid companyId, Document doc)
    {
        if (doc.WhtViaEWithholding) return true;
        if (doc.RelatedDocumentId is not Guid srcId) return false;
        var src = await db.Documents.AsNoTracking()
            .Where(d => d.Id == srcId && d.CompanyId == companyId)
            .Select(d => new { d.DocumentType, d.WhtViaEWithholding })
            .FirstOrDefaultAsync();
        return src != null && IsVia(false, src.DocumentType, src.WhtViaEWithholding);
    }

    /// <summary>ชุด id ของเอกสาร e-Withholding ทั้งบริษัท (ใบที่ติ๊ก + ใบลูกที่อ้างใบฝั่งผู้จ่ายที่ติ๊ก) — ให้ผู้อ่านแบบชุด (หน้านำส่ง · รายงาน · ไฟล์ยื่น)
    /// ตัด 50 ทวิ/เอกสารออกจากภาระนำส่ง · ไม่มีใบใดติ๊ก ⇒ ชุดว่าง (query เดียว · พฤติกรรมเดิม)</summary>
    public static async Task<HashSet<Guid>> LoadDocIdsAsync(AccountingDbContext db, Guid companyId)
    {
        var payerSide = WhtRemitScope.PayerSideTypes;
        var flagged = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.WhtViaEWithholding && payerSide.Contains(d.DocumentType))
            .Select(d => d.Id)
            .ToListAsync();
        var set = flagged.ToHashSet();
        if (flagged.Count == 0) return set;
        var children = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.RelatedDocumentId != null && flagged.Contains(d.RelatedDocumentId.Value))
            .Select(d => d.Id)
            .ToListAsync();
        set.UnionWith(children);
        return set;
    }
}
