namespace Accounting.Helpers;

/// <summary>
/// **วางแผนว่าใบซื้อ 1 ใบต้องขึ้นทะเบียนสินทรัพย์กี่ตัว และตัวละเท่าไร** (pure, ไม่มี I/O)
///
/// ═══ ที่มา (ผู้ใช้รายงาน 2026-09-08) ═══
/// เดิม <c>AutoRegisterFixedAssetsAsync</c> จัดกลุ่มบรรทัดด้วย <b>AccountId</b>
/// แล้วสร้างสินทรัพย์ <b>ตัวเดียวต่อผัง</b> โดยรวมยอดทุกบรรทัดเข้าด้วยกัน —
/// ถูกต้องเฉพาะเจตนาเดิม (ค่าขนส่ง/ติดตั้ง capitalize เข้าตัวเดียวกันตาม TFRS
/// for NPAEs บทที่ 10) แต่ <b>ผิดทันทีที่ใบเดียวซื้อของหลายชิ้นในผังเดียวกัน</b>:
/// แอร์ 3 เครื่องบน 12210 ⇒ ทะเบียนได้ <b>1 แถว</b> ราคารวม ⇒ จำหน่ายทีละเครื่อง
/// ไม่ได้ · คิดค่าเสื่อมแยกตัวไม่ได้ · นับจำนวนทรัพย์สินผิด
///
/// <para><b>กติกา</b> — ในกลุ่มผังเดียวกัน: บรรทัดที่<b>ไม่ใช่</b>ค่าใช้จ่ายประกอบ
/// = สินทรัพย์ 1 ตัว/บรรทัด · ค่าใช้จ่ายประกอบ (ขนส่ง/ติดตั้ง/ฝึกอบรม) <b>เฉลี่ย
/// ตามสัดส่วนราคา</b> เข้าทุกตัวในกลุ่มนั้น (บรรทัดสุดท้ายรับเศษ) ⇒ Σ ต้นทุนที่
/// ขึ้นทะเบียน = Σ ยอดบรรทัดในกลุ่ม <b>เป๊ะเสมอ</b> ไม่มีบาทไหนหายหรือเกิน —
/// นี่คือ invariant เดียวที่จับบั๊กคลาสนี้ได้ (ยอดรายตัว "ดูสมเหตุสมผล" ทุกตัว)</para>
/// </summary>
public static class AssetRegistrationPlanner
{
    /// <summary>บรรทัดที่ป้อนเข้ามา — ใช้ชนิดของตัวเองเพื่อให้เทสต์ไม่ต้องพึ่ง EF</summary>
    public sealed record Line(Guid Id, string? Description, decimal Amount);

    /// <summary>สินทรัพย์ 1 ตัวที่ต้องสร้าง</summary>
    /// <param name="SourceLineId">บรรทัดต้นทาง — เป็นคีย์กันสร้างซ้ำตอน re-approve</param>
    /// <param name="Cost">ราคาบรรทัด + ส่วนแบ่งค่าใช้จ่ายประกอบ</param>
    /// <param name="AllocatedAuxiliary">ส่วนแบ่งค่าใช้จ่ายประกอบที่บวกเข้าไป (0 = ไม่มี)</param>
    public sealed record PlannedAsset(
        Guid SourceLineId, string? Name, decimal Cost, decimal AllocatedAuxiliary);

    /// <summary>คำที่บอกว่าบรรทัดนี้เป็น "ค่าใช้จ่ายที่ทำให้สินทรัพย์พร้อมใช้"
    /// ไม่ใช่ตัวสินทรัพย์เอง — <b>ตัวตัดสินอยู่ที่นี่ที่เดียว</b> (เดิมเป็น local
    /// function ใน DocumentService ⇒ เขียนเทสต์ตรง ๆ ไม่ได้)</summary>
    public static bool IsAuxiliary(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;
        var d = description.ToLowerInvariant();
        return d.Contains("ขนส่ง") || d.Contains("จัดส่ง") || d.Contains("ติดตั้ง")
            || d.Contains("ฝึกอบรม") || d.Contains("ค่าธรรมเนียม") || d.Contains("ค่าบริการ")
            || d.Contains("shipping") || d.Contains("delivery") || d.Contains("freight")
            || d.Contains("install") || d.Contains("training") || d.Contains("setup")
            || d.Contains("ค่าประกัน");
    }

    /// <summary>วางแผนสินทรัพย์ของ <b>หนึ่งกลุ่มผัง</b> (บรรทัดทั้งหมดลงผัง PPE ตัวเดียวกัน)</summary>
    public static List<PlannedAsset> Plan(IReadOnlyList<Line> groupLines)
    {
        var lines = groupLines.Where(l => l.Amount > 0m).ToList();
        if (lines.Count == 0) return new List<PlannedAsset>();

        var real = lines.Where(l => !IsAuxiliary(l.Description)).ToList();
        // ทุกบรรทัดเป็นค่าใช้จ่ายประกอบ (เช่นใบค่าติดตั้งใบเดียวที่ผังลง PPE) —
        // ยังต้องขึ้นทะเบียน 1 ตัว ไม่งั้นยอด Dr 12xxx บนงบไม่มีคู่ในทะเบียน
        if (real.Count == 0)
            return new List<PlannedAsset> {
                new(lines[0].Id, lines[0].Description, lines.Sum(l => l.Amount), 0m) };

        var aux = lines.Sum(l => l.Amount) - real.Sum(l => l.Amount);
        var realTotal = real.Sum(l => l.Amount);
        var result = new List<PlannedAsset>(real.Count);
        var allocated = 0m;
        for (var i = 0; i < real.Count; i++)
        {
            // บรรทัดสุดท้ายรับเศษที่เหลือ — กันไม่ให้ Σ ต้นทุนเพี้ยนจากการปัดเศษ
            // (วินัยเดียวกับงวดสุดท้ายของตารางค่าเสื่อม)
            var share = i == real.Count - 1
                ? aux - allocated
                : (realTotal > 0m
                    ? Math.Round(aux * real[i].Amount / realTotal, 2, MidpointRounding.AwayFromZero)
                    : 0m);
            allocated += share;
            result.Add(new PlannedAsset(real[i].Id, real[i].Description, real[i].Amount + share, share));
        }
        return result;
    }
}
