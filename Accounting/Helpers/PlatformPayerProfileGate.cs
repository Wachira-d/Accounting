using System.Text.RegularExpressions;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 234 (คำตัดสิน 234-4/234-5 · เจ้าของ: "กรณีลูกค้าสมัครใหม่หรือยังไม่ได้ตั้งค่าบริษัท มีอะไรเตือนลูกค้าและให้ลูกค้าติ๊กหัก กรอกข้อมูลบริษัทก่อน") —
/// <b>ตัวตัดสินเดียว</b>ว่า "ข้อมูลผู้ซื้อ (บริษัทผู้เช่า) พอสำหรับใบกำกับภาษีเต็มรูป §86/4 ไหม" ก่อนชำระค่าบริการ NextAcc · pure
/// <list type="bullet">
/// <item>ชนิดผู้ซื้อ = จากเลขผู้เสียภาษี (<see cref="ContactTypeResolver.FromTaxId"/> — ตัวเดียวกับ <c>SubscriptionService.PayerIsJuristicAsync</c> และตัวออกใบ
/// <c>PlatformBillingDocumentIssuer.EnsureContactAsync</c>) — ไม่มีคอลัมน์ "ประเภทผู้ซื้อ" ที่สอง (เลขขึ้นต้น 0 = นิติบุคคล/หน่วยงาน)</item>
/// <item>ครบ = ชื่อ · ที่อยู่ (ข้อความ หรือ อำเภอ+จังหวัด) · เลขผู้เสียภาษี 13 หลัก + checksum (<see cref="ThaiTaxId.IsValid"/>) · สาขา 5 หลัก
/// (ว่าง = <c>00000</c> สำนักงานใหญ่ — ค่าเดียวกับที่ตัวออกใบใช้)</item>
/// <item>ผลต่อการชำระ: <b>ไม่บล็อก</b>การจ่ายของบุคคลธรรมดา/ข้อมูลไม่ครบ (ใบเสร็จ/ใบกำกับออกตามข้อมูลที่มี — กติกาเดิม) ·
/// <b>นิติบุคคลที่จะหัก ณ ที่จ่าย ต้องครบก่อน</b> (<see cref="WithholdingRefusal"/> — 50 ทวิ และใบกำกับต้องตรงตัวผู้หัก)</item>
/// </list>
/// ผู้เรียก: <c>PlatformWhtSuggestion.Decide</c> (ทุกเส้นชำระ) · ศูนย์บิล/checkout แสดงการ์ด "ข้อมูลบริษัทสำหรับใบกำกับภาษี"
/// </summary>
public static class PlatformPayerProfileGate
{
    public const string HeadOfficeBranch = "00000";
    public const string RuleCode = "PLATFORM-PAYER-PROFILE";

    /// <summary>ข้อความเตือนเมื่อยังไม่มีเลขผู้เสียภาษี (เจ้าของกำหนดถ้อยคำ)</summary>
    public const string NoTaxIdWarning = "ยังไม่ได้กรอกเลขผู้เสียภาษี — ใบกำกับภาษีจะออกในนามบุคคล/ไม่สามารถหัก ณ ที่จ่ายได้";

    /// <summary>ข้อเท็จจริงจาก <c>Company</c> (ผู้เรียกอ่านมา)</summary>
    public sealed record Facts(string? Name, string? TaxId, string? BranchCode, string? Address, string? District, string? Province);

    /// <summary>ผล — หน้าเว็บแสดงอย่างเดียว (ป้าย · สิ่งที่ขาด · คำเตือน)</summary>
    public sealed record Result(
        string BuyerKind, string BuyerKindLabel, bool IsJuristic, bool TaxIdValid, bool Complete,
        IReadOnlyList<string> Missing, string? Warning, string? WithholdingProblem,
        string? Name, string? TaxId, string BranchCode, string? Address);

    private static readonly Regex Branch5 = new(@"^\d{5}$", RegexOptions.Compiled);

    /// <summary>รหัสสาขาที่ใช้ — ว่าง = สำนักงานใหญ่ (ค่าเดียวกับ <c>EnsureContactAsync</c>)</summary>
    public static string BranchOf(string? branchCode) => string.IsNullOrWhiteSpace(branchCode) ? HeadOfficeBranch : branchCode.Trim();

    private static bool HasAddress(Facts f) =>
        !string.IsNullOrWhiteSpace(f.Address) || (!string.IsNullOrWhiteSpace(f.District) && !string.IsNullOrWhiteSpace(f.Province));

    public static Result Evaluate(Facts f)
    {
        var kind = ContactTypeResolver.FromTaxId(f.TaxId);
        var juristic = kind is ContactType.JuristicPerson or ContactType.GovernmentAgency;
        var hasDigits = ContactTaxBranchKey.HasTaxId(f.TaxId);
        var valid = ThaiTaxId.IsValid(f.TaxId);
        var branch = BranchOf(f.BranchCode);
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(f.Name)) missing.Add("ชื่อบริษัท/ผู้ซื้อ");
        if (!HasAddress(f)) missing.Add("ที่อยู่ตามทะเบียน");
        if (!valid) missing.Add(hasDigits ? "เลขผู้เสียภาษี (13 หลัก · checksum ไม่ผ่าน)" : "เลขผู้เสียภาษี 13 หลัก");
        if (juristic && !Branch5.IsMatch(branch)) missing.Add("รหัสสาขา 5 หลัก (สำนักงานใหญ่ = 00000)");

        string? warning = !hasDigits ? NoTaxIdWarning
            : !valid ? "เลขผู้เสียภาษีไม่ถูกต้อง (ตรวจ checksum ไม่ผ่าน) — ใบกำกับภาษีจะไม่มีเลขผู้ซื้อ/ไม่สามารถหัก ณ ที่จ่ายได้ · แก้เลขก่อนชำระ"
            : missing.Count > 0 ? $"ข้อมูลบริษัทสำหรับใบกำกับภาษียังไม่ครบ: {string.Join(" · ", missing)}"
            : null;
        string? whtProblem = juristic && missing.Count > 0
            ? $"กรอกข้อมูลบริษัทสำหรับใบกำกับภาษีให้ครบก่อนหักภาษี ณ ที่จ่าย (ขาด: {string.Join(" · ", missing)}) — หนังสือรับรอง 50 ทวิ และใบกำกับภาษีต้องตรงตัวผู้หัก"
            : null;
        var (k, label) = kind switch
        {
            ContactType.JuristicPerson or ContactType.GovernmentAgency => ("Juristic", "นิติบุคคล"),
            ContactType.Individual => ("Individual", "บุคคลธรรมดา"),
            _ => ("Unknown", "ยังไม่ระบุ (ไม่มีเลขผู้เสียภาษี)"),
        };
        return new Result(k, label, juristic, valid, missing.Count == 0, missing, warning, whtProblem,
            string.IsNullOrWhiteSpace(f.Name) ? null : f.Name.Trim(), hasDigits ? f.TaxId!.Trim() : null, branch,
            string.IsNullOrWhiteSpace(f.Address) ? null : f.Address.Trim());
    }

    /// <summary>ฝ่ายค้าน 234 N2: ข้อมูลที่ผู้เช่ากรอกในการ์ด/checkout ตาม "ประเภทผู้ซื้อ" ที่เลือก — ผลที่จะบันทึกลงบริษัท (เส้นแก้ข้อมูลบริษัทเดิม) หรือเหตุที่ปฏิเสธ ·
    /// นิติบุคคล: เลข 13 หลัก checksum ผ่าน <b>และ</b>เป็นเลขนิติบุคคล (ขึ้นต้น 0) · สาขา 5 หลัก (ว่าง = 00000) ·
    /// บุคคลธรรมดา: เลขไม่บังคับ — ว่าง = <b>ล้างเลขเดิม</b> ("" ตามกฎ #4 B) · กรอก = ต้องเป็นเลขบุคคลที่ checksum ผ่าน · สาขาไม่ใช้ (00000)</summary>
    public sealed record Input(string? BuyerType, string? Name, string? Address, string? TaxId, string? BranchCode);
    public sealed record Normalized(string Name, string? Address, string TaxId, string BranchCode);

    public static (Normalized? Value, string? Problem) Normalize(Input i)
    {
        var juristic = string.Equals(i.BuyerType?.Trim(), "Juristic", StringComparison.OrdinalIgnoreCase);
        var individual = string.Equals(i.BuyerType?.Trim(), "Individual", StringComparison.OrdinalIgnoreCase);
        if (!juristic && !individual) return (null, "เลือกประเภทผู้ซื้อ: นิติบุคคล หรือ บุคคลธรรมดา");
        var name = (i.Name ?? "").Trim();
        if (name.Length == 0) return (null, "กรอกชื่อผู้ซื้อตามทะเบียน");
        if (name.Length > 100) return (null, "ชื่อผู้ซื้อยาวเกิน 100 ตัวอักษร");
        var address = string.IsNullOrWhiteSpace(i.Address) ? null : i.Address.Trim();
        var digits = new string((i.TaxId ?? "").Where(char.IsAsciiDigit).ToArray());
        if (juristic)
        {
            if (address == null) return (null, "นิติบุคคลต้องกรอกที่อยู่ตามทะเบียน (พิมพ์บนใบกำกับภาษี §86/4)");
            if (!ThaiTaxId.IsValid(digits)) return (null, "เลขประจำตัวผู้เสียภาษีของนิติบุคคลต้องเป็นตัวเลข 13 หลักที่ถูกต้อง (ตรวจ checksum ไม่ผ่าน/ไม่ครบ)");
            if (!ThaiTaxId.IsJuristic(digits))
                return (null, "เลขนี้เป็นเลขประจำตัวผู้เสียภาษีของบุคคลธรรมดา (ไม่ขึ้นต้นด้วย 0) — ถ้าเป็นบริษัทให้กรอกเลขนิติบุคคล หรือเลือกประเภท \"บุคคลธรรมดา\"");
            var branch = BranchOf(i.BranchCode);
            if (!Branch5.IsMatch(branch)) return (null, "รหัสสาขาต้องเป็นตัวเลข 5 หลัก (สำนักงานใหญ่ = 00000)");
            return (new Normalized(name, address, digits, branch), null);
        }
        if (digits.Length == 0) return (new Normalized(name, address, "", HeadOfficeBranch), null);   // ล้างเลขเดิม (กฎ #4 B "" = ล้าง)
        if (!ThaiTaxId.IsValid(digits)) return (null, "เลขประจำตัวผู้เสียภาษีต้องเป็นตัวเลข 13 หลักที่ถูกต้อง — หรือเว้นว่าง (บุคคลธรรมดาไม่บังคับ)");
        if (ThaiTaxId.IsJuristic(digits))
            return (null, "เลขนี้เป็นเลขนิติบุคคล (ขึ้นต้นด้วย 0) — เลือกประเภท \"นิติบุคคล\" แล้วกรอกที่อยู่/สาขา");
        return (new Normalized(name, address, digits, HeadOfficeBranch), null);
    }

    /// <summary>ผู้เช่าขอหัก ณ ที่จ่าย — ข้อความปฏิเสธ (นิติบุคคลที่ข้อมูลไม่ครบ) · null = หักได้ด้านข้อมูลผู้ซื้อ</summary>
    public static string? WithholdingRefusal(Result r) => r.WithholdingProblem;
}
