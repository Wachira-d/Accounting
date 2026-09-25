using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>ตัวเลือก "ลักษณะเงิน" 1 ข้อสำหรับหน้าเว็บ (ชื่อ enum + ป้าย + คำอธิบายสั้น + มาตรา) — หน้าเว็บสร้าง select จากลิสต์นี้
/// (ห้ามพิมพ์ซ้ำใน JS — F2 ข้อ 5)</summary>
public sealed record DepositNatureOption(string Value, string Label, string Description, string LegalReference);

/// <summary>ผลตัดสินของคู่ (ลักษณะ × โหมดที่ตั้งบนประเภท) — หน้าตั้งค่าใช้แสดงคำเตือน/ช่องเหตุผล "ก่อนกดบันทึก" โดยไม่คิดกติกาเอง</summary>
/// <param name="Nature">ชื่อ <see cref="DepositNature"/></param>
/// <param name="Treatment">ชื่อ <see cref="DepositVatTreatment"/> ที่ตั้งบนประเภท · null = "ตามค่าตั้งบริษัท"</param>
/// <param name="EffectiveTreatment">โหมดที่ใช้จริงหลังตกชั้น (ตัวตัดสิน <c>DepositPolicyResolver.ResolveKind</c>)</param>
/// <param name="EffectiveLabel">ป้ายของโหมดที่ใช้จริง</param>
/// <param name="ReasonRequired">บันทึกประเภทนี้ต้องมีเหตุผล (<c>DepositPolicyResolver.KindProblem</c> ปฏิเสธเมื่อเว้นว่าง)</param>
/// <param name="Warning">คำเตือนตามลักษณะ × โหมด (<c>DepositPolicyResolver.KindWarning</c>)</param>
/// <param name="RuleCode">รหัสกฎของคำเตือน</param>
public sealed record DepositKindMatrixCell(
    string Nature, string? Treatment, string EffectiveTreatment, string EffectiveLabel,
    bool ReasonRequired, string? Warning, string? RuleCode);

/// <summary>ข้อมูลระดับบริษัทที่ตัวตัดสินประเภทเงินมัดจำต้องใช้ (ชั้น ④ ⑤ ⑥ ของ <c>ResolveKind</c>) — โหลดครั้งเดียวต่อคำขอ</summary>
/// <param name="Supply">ลักษณะสิ่งที่ขายตามประเภทธุรกิจ (<c>DepositPolicyResolver.NatureOf</c>)</param>
/// <param name="CompanySetting">⑤ <c>CompanySettings.DepositVatTreatment</c> (null = ยังไม่เคยตั้ง)</param>
/// <param name="DefaultKind">④ ประเภทเริ่มต้นที่เปิดใช้อยู่ (null = ไม่มี)</param>
/// <param name="ChartHas21530">ผังมีบัญชีเงินประกันความเสียหาย 21530 (ผังโรงแรม) — เลือกบัญชีของเงินประกัน</param>
public sealed record DepositKindCompanyContext(
    Guid CompanyId, DepositSupplyNature Supply, DepositVatTreatment? CompanySetting, DepositKind? DefaultKind, bool ChartHas21530)
{
    /// <summary>บริษัท "ตั้งค่ามัดจำเองแล้ว" หรือยัง — ดู <see cref="DepositKindCatalog.CompanyConfigured"/></summary>
    public bool CompanyConfigured => DepositKindCatalog.CompanyConfigured(CompanySetting, DefaultKind);
}

/// <summary>
/// <b>แคตตาล็อกประเภทเงินมัดจำ</b> (รอบ 194 ทีม C · spec S2/S4/S5) — สิ่งที่หน้าตั้งค่า · API <c>/deposit-kinds</c> · integration ·
/// CMS booking ใช้ร่วมกัน: ป้ายลักษณะเงิน · รูปรหัส · โหลดบริบทบริษัท · ตัดสินผ่าน <see cref="DepositPolicyResolver.ResolveKind"/> ตัวเดียว
///
/// <para>ไม่มีกติกา VAT ของตัวเองในไฟล์นี้ — คำเตือน/เหตุผล/โหมดที่ใช้จริงมาจาก <see cref="DepositPolicyResolver"/> ทั้งหมด
/// (owner file ของเรื่องมัดจำ) · ไฟล์นี้แค่ประกอบอินพุตและจัดรูปให้หน้าเว็บ</para>
/// </summary>
public static partial class DepositKindCatalog
{
    /// <summary>ความยาวสูงสุดของรหัส (ตรงกับ <c>varchar(32)</c> ของตาราง DepositKinds)</summary>
    public const int CodeMaxLength = 32;
    /// <summary>ความยาวสูงสุดของชื่อ (ตรงกับ <c>varchar(256)</c>)</summary>
    public const int NameMaxLength = 256;

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeShape();

    /// <summary>ลักษณะเงินทั้งหมด (เรียงตามเลข enum) — ป้าย/คำอธิบาย/มาตรา ตามสเปก S1</summary>
    public static IReadOnlyList<DepositNatureOption> NatureOptions { get; } = new[]
    {
        new DepositNatureOption(nameof(DepositNature.PartOfPrice), "ส่วนหนึ่งของราคา (มัดจำ/เงินจอง/ชำระล่วงหน้า)",
            "เงินที่จะนำไปหักเป็นค่าสินค้า/ค่าบริการ — ภาษีขายถึงกำหนดตอนได้รับเงิน · ค่าแนะนำ: ออกใบกำกับภาษี รับรู้ VAT ทันที",
            "ป.รัษฎากร มาตรา 78(1)(ข) สินค้า · มาตรา 78/1(1) บริการ · คำสั่งกรมสรรพากรที่ ป.73/2541"),
        new DepositNatureOption(nameof(DepositNature.RefundableSecurity), "เงินประกันที่ต้องคืน",
            "เงินประกันความเสียหาย/กุญแจ/ภาชนะ/การปฏิบัติตามสัญญา ที่ต้องคืนเมื่อจบ — ยังไม่ใช่ค่าตอบแทนจึงยังไม่เกิดภาษีขาย · "
            + "ห้ามนำไปหักราคา ตัดชำระหนี้ได้เท่านั้น · ค่าแนะนำ: รับเป็นเงินมัดจำเต็มยอด ไม่แยก VAT",
            "คำสั่งกรมสรรพากรที่ ป.73/2541 (เงินประกันที่ไม่ใช่ค่าตอบแทน) · มาตรา 79 (ฐานภาษี)"),
        new DepositNatureOption(nameof(DepositNature.NonVatSupply), "นอกระบบ VAT (สิ่งที่ขายได้รับยกเว้น)",
            "มัดจำของสิ่งที่ขายซึ่งไม่อยู่ในระบบภาษีมูลค่าเพิ่ม เช่น ค่าเช่าอสังหาริมทรัพย์ · ขายอสังหาฯ ที่เสียภาษีธุรกิจเฉพาะ — ระบบบันทึก VAT 0 เสมอ",
            "ป.รัษฎากร มาตรา 81(1)(ต) ค่าเช่าอสังหาฯ · มาตรา 81(1)(น) · มาตรา 81"),
    };

    /// <summary>ป้ายไทยของลักษณะเงิน (ค่าที่ไม่นิยาม = "ไม่ทราบ")</summary>
    public static string NatureLabelOf(DepositNature? nature)
        => nature is { } n ? NatureOptions.FirstOrDefault(o => o.Value == n.ToString())?.Label ?? "ไม่ทราบ" : "ไม่ทราบ";

    /// <summary>รหัสที่ผู้ใช้/คู่ค้าส่งมา → รูปมาตรฐาน (ตัดช่องว่างหัวท้าย · ตัวพิมพ์ใหญ่) · null/ว่าง = ""</summary>
    public static string NormalizeCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>ด่านรูปรหัส — null = ผ่าน · ข้อความไทย = เหตุ (ผู้เรียกโยน BusinessRuleException/ตอบ 400)</summary>
    public static string? CodeProblem(string? code)
    {
        var c = NormalizeCode(code);
        if (c.Length == 0) return "กรุณาระบุรหัสประเภทเงินมัดจำ (เช่น ADVANCE) — ระบบภายนอกใช้รหัสนี้อ้างถึงประเภท";
        if (c.Length > CodeMaxLength) return $"รหัสประเภทเงินมัดจำยาวเกิน {CodeMaxLength} ตัวอักษร";
        return CodeShape().IsMatch(c) ? null
            : "รหัสประเภทเงินมัดจำใช้ได้เฉพาะ A–Z · 0–9 · ขีด (-) · ขีดล่าง (_) และต้องขึ้นต้นด้วยตัวอักษรหรือตัวเลข";
    }

    /// <summary>บริษัท "ตั้งค่ามัดจำเองแล้ว" — ประเภทเริ่มต้นมีโหมดของตัวเอง หรือ ค่าตั้งต้นบริษัท (ตั้งค่า → ภาษี) ถูกตั้งไว้ ·
    /// ทางเข้าที่เดิม "ไม่อ่านค่าตั้งบริษัท" (CMS booking) ใช้ธงนี้ตัดสินว่าจะส่งประเภทเข้าเส้นเอกสารไหม — ไม่ได้ตั้ง = คงพฤติกรรมเดิม
    /// (seed ADVANCE มีโหมด null ⇒ บริษัทที่ไม่เคยแตะอะไรเลยได้ false)</summary>
    public static bool CompanyConfigured(DepositVatTreatment? companySetting, DepositKind? defaultKind)
        => DepositPolicyResolver.IsDefined(companySetting)
           || (defaultKind is { IsActive: true, IsDeleted: false } && DepositPolicyResolver.IsDefined(defaultKind.VatTreatment));

    /// <summary>ตัดสินประเภทของใบใหม่ในบริษัทนี้ (① ประเภทที่ระบุ → ④ ประเภทเริ่มต้น → ⑤ ค่าตั้งบริษัท → ⑥ ประเภทธุรกิจ) —
    /// ทางเข้าที่ไม่มีช่องทางของตัวเอง (integration · CMS) ใช้ตัวนี้ · ที่พักใช้ <c>ResolveKind</c> ตรงพร้อมชั้น ② ③</summary>
    public static DepositKindDecision Decide(DepositKindCompanyContext ctx, DepositKind? documentKind)
        => DepositPolicyResolver.ResolveKind(ctx.CompanyId, ctx.Supply, documentKind, channelKind: null, channelTreatment: null,
            companyDefaultKind: ctx.DefaultKind, companySetting: ctx.CompanySetting, chartHas21530: ctx.ChartHas21530);

    /// <summary>ผลตัดสิน "ถ้าเลือกประเภทนี้บนใบ" สำหรับแสดงในหน้าตั้งค่า — ประเภทที่ปิดใช้ก็ต้องแสดงค่าของตัวเอง (ไม่ใช่ค่าที่ตกไปชั้นถัดไป)
    /// จึงประเมินจากสำเนาที่เปิดใช้ · ไม่แตะแถวจริง</summary>
    public static DepositKindDecision Preview(DepositKindCompanyContext ctx, DepositKind kind)
        => Decide(ctx, new DepositKind
        {
            Id = kind.Id, CompanyId = ctx.CompanyId, Code = kind.Code, Name = kind.Name, Nature = kind.Nature,
            VatTreatment = kind.VatTreatment, LiabilityAccountCode = kind.LiabilityAccountCode,
            ForfeitAccountCode = kind.ForfeitAccountCode, PolicyReason = kind.PolicyReason, IsActive = true,
        });

    /// <summary>ตาราง (ลักษณะ 3 × โหมด 4 รวม "ตามค่าตั้งบริษัท") ของบริษัทนี้ — หน้าตั้งค่าเลือกแถวตามที่ผู้ใช้กำลังเลือก
    /// ⇒ คำเตือน/ช่องเหตุผลขึ้นก่อนกดบันทึก และตรงกับที่เซิร์ฟเวอร์จะตัดสินตอนบันทึกจริง (ตัวตัดสินชุดเดียว)</summary>
    public static IReadOnlyList<DepositKindMatrixCell> Matrix(DepositKindCompanyContext ctx)
    {
        var treatments = new DepositVatTreatment?[]
            { null, DepositVatTreatment.FullDeposit, DepositVatTreatment.VatPendingUndue, DepositVatTreatment.VatImmediate };
        var cells = new List<DepositKindMatrixCell>();
        foreach (var n in new[] { DepositNature.PartOfPrice, DepositNature.RefundableSecurity, DepositNature.NonVatSupply })
            foreach (var t in treatments)
            {
                var d = Preview(ctx, new DepositKind { Nature = n, VatTreatment = t });
                cells.Add(new DepositKindMatrixCell(n.ToString(), t?.ToString(), d.Treatment.ToString(),
                    DepositPolicyResolver.LabelOf(d.Treatment),
                    ReasonRequired: DepositPolicyResolver.KindProblem(n, t, null) != null,
                    d.Warning, d.RuleCode));
            }
        return cells;
    }

    /// <summary>โหลดบริบทบริษัท (ประเภทธุรกิจ · ค่าตั้งมัดจำ · ประเภทเริ่มต้นที่เปิดใช้ · ผังมี 21530 ไหม) — ทุก query กรอง CompanyId</summary>
    public static async Task<DepositKindCompanyContext> LoadContextAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var industry = await db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => (IndustryType?)c.IndustryType).FirstOrDefaultAsync(ct) ?? IndustryType.General;
        var setting = await db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => s.DepositVatTreatment).FirstOrDefaultAsync(ct);
        var defaultKind = await db.DepositKinds.AsNoTracking()
            .Where(k => k.CompanyId == companyId && k.IsDefault && k.IsActive)
            .OrderBy(k => k.SortOrder).FirstOrDefaultAsync(ct);
        var has21530 = await ChartHasSecurityAccountAsync(db, companyId, ct);
        return new DepositKindCompanyContext(companyId, DepositPolicyResolver.NatureOf(industry), setting, defaultKind, has21530);
    }

    /// <summary>บัญชีเงินประกันความเสียหาย 21530 (ผังโรงแรม) ที่ใช้ได้จริง — ตัวกรองของบัญชี (ดู <see cref="UsableSecurityAccount"/>)</summary>
    public const string SecurityAccountCode = "21530";

    /// <summary>รอบ 194 P-f — บัญชี 21530 "ใช้ได้" = ของบริษัทนี้ · เปิดใช้ · ไม่ถูกลบ (เดิมสองเส้นตรวจคนละแบบ: ที่นี่ <c>IsActive</c> ·
    /// DocumentService <c>!IsDeleted</c> ⇒ บัญชีที่ปิดใช้ถูกเลือกเป็นบัญชีเงินประกันในเส้นหนึ่ง) · รูป expression ให้ EF แปลได้ · เทสต์ compile แล้วรัน</summary>
    internal static System.Linq.Expressions.Expression<Func<ChartOfAccount, bool>> UsableSecurityAccount(Guid companyId)
        => a => a.CompanyId == companyId && a.AccountCode == SecurityAccountCode && a.IsActive && !a.IsDeleted;

    /// <summary>ผังของบริษัทมีบัญชีเงินประกัน 21530 ที่ใช้ได้ไหม — <b>ตัวเดียว</b>ของทุกเส้นที่ตัดสินประเภทเงินมัดจำ
    /// (ตัวเลือกบัญชีจริงอยู่ที่ DepositPolicyResolver · spec S7 ไม่เพิ่มเลขใหม่)</summary>
    public static Task<bool> ChartHasSecurityAccountAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
        => db.ChartOfAccounts.AsNoTracking().AnyAsync(UsableSecurityAccount(companyId), ct);

    /// <summary>หาประเภทจากรหัส (ไม่สนตัวพิมพ์เล็ก/ใหญ่ · แถวที่ลบแล้วไม่นับ) ของบริษัทนี้ — null = ไม่รู้จัก ·
    /// ผู้เรียกตัดสินเองว่าประเภทที่ปิดใช้รับได้ไหม (integration: ไม่รับ — ตอบ 400)</summary>
    public static async Task<DepositKind?> FindByCodeAsync(AccountingDbContext db, Guid companyId, string? code, CancellationToken ct = default)
    {
        var c = NormalizeCode(code);
        if (c.Length == 0) return null;
        return await db.DepositKinds.AsNoTracking()
            .FirstOrDefaultAsync(k => k.CompanyId == companyId && k.Code == c && !k.IsDeleted, ct);
    }

    /// <summary>ข้อความเมื่อคู่ค้า/ผู้ใช้ส่งรหัสประเภทที่ใช้ไม่ได้ — null = ใช้ได้ · ใช้ร่วมทุกทางเข้าที่รับ <c>DepositKindCode</c></summary>
    public static string? UnusableCodeMessage(string? code, DepositKind? found)
    {
        var c = NormalizeCode(code);
        if (found is null)
            return $"ไม่รู้จักประเภทเงินมัดจำรหัส “{c}” — ตั้งค่าประเภทได้ที่ ตั้งค่า → ภาษี → ประเภทเงินมัดจำ "
                   + "(ไม่ส่ง depositKindCode = ระบบบันทึกแบบเดิมตามค่าตั้งบริษัท)";
        if (!found.IsActive)
            return $"ประเภทเงินมัดจำ “{c}” ({found.Name}) ถูกปิดใช้แล้ว — เปิดใช้อีกครั้งที่หน้าตั้งค่า หรือส่งรหัสประเภทอื่น";
        return null;
    }
}
