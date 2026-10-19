using Accounting.Services.Implementations.Ocr;
using Accounting.Services.Implementations.Tax;

namespace Accounting.Helpers;

/// <summary>
/// คำเตือนก่อนอนุมัติ: บรรทัดที่ลงผังสินทรัพย์ (12xxx) แต่ต้นทุนต่อหน่วยต่ำกว่าเกณฑ์ขึ้นทะเบียน — ตัวตัดสินเดียว
///
/// <para><b>ที่มา (ผู้ใช้รายงาน 2026-10-19):</b> PV ซื้อ "อุปกรณ์สำนักงาน" 138.32 บาทลงผัง 12210 ⇒ อนุมัติแล้วระบบขึ้นทะเบียน
/// สินทรัพย์ให้อัตโนมัติ (TFRS บทที่ 10) คิดค่าเสื่อม 5 ปีของที่ไม่ใช่สินทรัพย์. ผังมาได้หลายทาง (คนเลือก · OCR · ประวัติผู้ขาย ·
/// AI · นำเข้า) — ด่านที่จับได้ทุกทางคือจุดเดียวที่ทุกใบต้องผ่าน = ก่อนอนุมัติ. <b>เตือน ไม่บล็อก</b>: บางกิจการมีนโยบาย
/// ขึ้นทะเบียนของใช้ทนราคาต่ำจริง (ตัดสินใจเป็นของผู้ทำบัญชี)</para>
///
/// <para><b>ต้นทุนที่เทียบ = ต้นทุนที่ทะเบียนจะได้จริง</b> จาก <see cref="AssetRegistrationPlanner.Plan"/> ตัวเดียวกับตอนขึ้นทะเบียน
/// (ฝ่ายค้าน P1-1: เทียบรายบรรทัดเคยเตือนค่าขนส่ง 1,500 ของเครื่องจักร 80,000 ให้ไปลงค่าใช้จ่าย = ขัด TFRS บทที่ 10 ที่ต้องรวมเข้าต้นทุน) ·
/// ผังที่ไม่คิดค่าเสื่อม (ที่ดิน · งานระหว่างก่อสร้าง) ไม่เตือน (P1-2: CIP ประกอบจากวัสดุ/ค่าแรงก้อนเล็กทุกใบ)</para>
///
/// <para>เกณฑ์ = <see cref="FixedAssetDetector.DefaultCapitalizationThreshold"/> (ตัวเดียวกับที่สแกนใช้เตือน "อาจเป็นสินทรัพย์")
/// — ห้ามมีตัวเลขเกณฑ์ชุดที่สอง</para>
/// </summary>
public static class LowValueAssetLineCheck
{
    public const string Tag = "[LOW-VALUE-ASSET]";

    /// <summary>มาตราอ้างอิงของร่องรอย "อนุมัติทั้งที่มีคำเตือนนี้" (กฎ M) — การรับรู้รายการเป็นสินทรัพย์ + ค่าเสื่อม</summary>
    public const string LegalReference = "TFRS-NPAEs-10";

    /// <summary>คำเตือนชุดนี้ — ทางเข้าที่ไม่มีคนเห็น (API · ระบบ) ผ่านได้ (<see cref="ApprovalAcknowledgement"/>) · เว็บ/มือถือแสดงให้รับทราบ</summary>
    public static bool IsApprovalWarning(string? warning)
        => warning is not null && warning.StartsWith(Tag, StringComparison.Ordinal);

    public static decimal Threshold => FixedAssetDetector.DefaultCapitalizationThreshold;

    /// <summary>ทางเข้าไม่มีหน้าจอรับทราบ (OCR สร้าง+อนุมัติ · LINE · รายการประจำ) — คำเตือนที่ผ่านไปต้องกลับถึงคน (ฝ่ายค้าน P2-2 ·
    /// แบบเดียวกับ <see cref="Section65TerApprovalWarnings.PassedNotice"/>) · null = ไม่มีอะไรต้องบอก</summary>
    public static string? PassedNotice(IEnumerable<string>? passedWarnings)
    {
        var notes = passedWarnings?.Where(IsApprovalWarning).Select(w => w[Tag.Length..].Trim().Replace('\n', ' ')).ToList() ?? new List<string>();
        if (notes.Count == 0) return null;
        return $"อนุมัติแล้ว — ขึ้นทะเบียนสินทรัพย์ที่ต้นทุนต่ำกว่าเกณฑ์ {notes.Count} รายการ (ถ้าไม่ใช่สินทรัพย์ กด \"ไม่ใช่สินทรัพย์\" "
               + "ที่หน้าทะเบียนสินทรัพย์ ระบบจะย้ายบรรทัดไปผังค่าใช้จ่ายและกลับรายการให้): " + string.Join(" · ", notes);
    }

    /// <summary>บรรทัดของเอกสาร (ยอดเป็นบาทแล้ว — ผู้เรียกคูณอัตราแลกเปลี่ยน)</summary>
    public sealed record Line(Guid Id, string? AccountCode, string? AccountName, string? Description, decimal Quantity, decimal AmountThb);

    /// <summary>ต้นทุนต่อหน่วย (จำนวน ≤ 0 ⇒ ถือทั้งก้อนเป็นชิ้นเดียว)</summary>
    public static decimal UnitCost(decimal quantity, decimal amount)
        => Math.Round(quantity > 0m ? amount / quantity : amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>คำเตือนของทั้งเอกสาร — วางแผนต่อกลุ่มผังด้วยตัววางแผนเดียวกับทะเบียน แล้วเทียบต้นทุนต่อหน่วยของสินทรัพย์แต่ละตัว</summary>
    public static List<string> Warnings(IReadOnlyList<Line> lines)
    {
        var result = new List<string>();
        foreach (var grp in lines.Where(l => l.AmountThb > 0m && FixedAssetAccountClassifier.Resolve(l.AccountCode) is { Depreciable: true })
                                 .GroupBy(l => l.AccountCode))
        {
            var byId = grp.ToDictionary(l => l.Id);
            foreach (var plan in AssetRegistrationPlanner.Plan(
                         grp.Select(l => new AssetRegistrationPlanner.Line(l.Id, l.Description, l.AmountThb)).ToList()))
            {
                var src = byId[plan.SourceLineId];
                var unit = UnitCost(src.Quantity, plan.Cost);
                if (unit >= Threshold) continue;
                var what = string.IsNullOrWhiteSpace(src.Description) ? "(ไม่มีชื่อรายการ)" : src.Description.Trim();
                if (what.Length > 60) what = what[..60] + "…";
                result.Add($"{Tag} บรรทัด “{what}” ลงผังสินทรัพย์ {src.AccountCode} {src.AccountName} แต่ต้นทุนต่อหน่วย {unit:N2} บาท "
                    + $"ต่ำกว่าเกณฑ์ขึ้นทะเบียน {Threshold:N0} บาท — อนุมัติแล้วระบบจะขึ้นทะเบียนสินทรัพย์และคิดค่าเสื่อมให้. "
                    + "ถ้าเป็นของใช้สิ้นเปลือง ให้เปลี่ยนผังบรรทัดนี้เป็นค่าใช้จ่ายก่อนอนุมัติ (ถ้ากิจการขึ้นทะเบียนของราคานี้จริง อนุมัติต่อได้)");
            }
        }
        return result;
    }
}
