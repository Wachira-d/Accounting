using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>แผนริบมัดจำของออเดอร์หน้าเว็บที่ถูกยกเลิก (ต่อใบมัดจำ) — ตัวเลขทุกตัวเป็นฐาน/gross ตามใบมัดจำนั้น</summary>
/// <param name="ForfeitBase">ฐานก่อน VAT ที่ส่งเข้า <c>RealizeDepositAsync(ForfeitAs: PriceOrFee)</c></param>
/// <param name="ForfeitGross">ส่วนที่ริบรวม VAT (ลูกค้าเห็น)</param>
/// <param name="RefundGross">ส่วนที่เหลือรอคืน — ระบบ<b>ไม่</b>คืนเอง (R1)</param>
public sealed record SiteOrderDepositForfeitLine(Guid Id, string Number, decimal ForfeitBase, decimal ForfeitGross, decimal RefundGross);

/// <summary>
/// <b>ตัวตัดสินเดียว</b> "ยกเลิกออเดอร์ MTO หลังจ่ายมัดจำ ริบเท่าไร" (รอบ 206 · ทะเบียน R-31 · POS_MTO_DESIGN §3.6 ข้อ 7)
/// <para><b>ที่มา</b>: คำตัดสินข้อ 156 (R1) — ระบบไม่คืน/ไม่ริบเองเพราะต้องมีหลักฐาน/เงื่อนไขจากคน · เจ้าของสั่ง "ตัดสินใจพัฒนาได้เลย" ⇒
/// <b>คำตัดสินข้อ 165</b>: เงื่อนไขริบ = ค่าตั้งต่อร้าน <c>SiteCommerceConfig.DepositForfeitOnCancelPercent</c> (% ของมัดจำคงเหลือ · null/0 = ไม่ริบเอง — เดิม) ·
/// เมื่อตั้งแล้ว การยกเลิกออเดอร์ริบอัตโนมัติผ่าน <c>RealizeDepositAsync(ForfeitAs: PriceOrFee)</c> เดิม (ไม่มี posting ใหม่) · ส่วนที่เหลือ "รอคืน" ยังเป็นงานคน (R1 คงอยู่สำหรับการคืน)</para>
/// <para>กติกา: เงินประกัน (<see cref="DepositNature.RefundableSecurity"/>) ไม่ริบ · ใบมัดจำเต็มยอดที่ VAT ยังรอ (<c>VatPending</c>) ไม่ริบเอง (ต้องออกใบกำกับก่อน — เส้นเดิม) ·
/// ริบจากฐานคงเหลือ (<see cref="LodgingDepositSettlement.Remaining"/>) ไม่ใช่ยอดใบ</para>
/// </summary>
public static class SiteOrderDepositForfeit
{
    public const string RuleCode = "CMS-ORDER-DEPOSIT-FORFEIT";
    public const string LegalReference = "DECISIONS-156 · DECISIONS-165";

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static string? ValidatePercent(decimal? percent)
        => percent is null ? null
            : percent < 0m || percent > 100m ? "เปอร์เซ็นต์ริบมัดจำเมื่อยกเลิกต้องอยู่ระหว่าง 0–100"
            : decimal.Round(percent.Value, 2) != percent.Value ? "เปอร์เซ็นต์ริบมัดจำต้องเป็นทศนิยมไม่เกิน 2 ตำแหน่ง"
            : null;

    /// <summary>ริบอัตโนมัติไหม — null/0 = ไม่ (พฤติกรรมเดิมทุกประการ)</summary>
    public static bool Enabled(decimal? percent) => percent is > 0m;

    public static IReadOnlyList<SiteOrderDepositForfeitLine> Plan(decimal? percent, IReadOnlyList<LodgingDepositSnapshot> deposits)
    {
        if (!Enabled(percent)) return Array.Empty<SiteOrderDepositForfeitLine>();
        var pct = Math.Clamp(percent!.Value, 0m, 100m);
        var lines = new List<SiteOrderDepositForfeitLine>();
        foreach (var d in deposits)
        {
            if (d.Nature == DepositNature.RefundableSecurity || d.VatPending) continue;
            var rem = LodgingDepositSettlement.Remaining(d);
            if (rem.Base <= 0.005m) continue;
            var forfeitBase = Math.Min(rem.Base, R2(rem.Base * pct / 100m));
            if (forfeitBase <= 0.005m) continue;
            var forfeitGross = Math.Min(rem.Gross, R2(rem.Gross * pct / 100m));
            lines.Add(new SiteOrderDepositForfeitLine(d.Id, d.Number, forfeitBase, forfeitGross, R2(rem.Gross - forfeitGross)));
        }
        return lines;
    }

    public static string Note(IReadOnlyList<SiteOrderDepositForfeitLine> done, decimal percent, DateTime atUtc)
        => $"[{RuleCode} {atUtc:yyyy-MM-dd HH:mm}Z] ริบมัดจำ {percent:0.##}% ตามเงื่อนไขร้าน (คำตัดสินข้อ 165): "
           + string.Join(" · ", done.Select(l => $"{l.Number} ริบ {l.ForfeitGross:N2} (ฐาน {l.ForfeitBase:N2})" + (l.RefundGross > 0.005m ? $" รอคืน {l.RefundGross:N2}" : "")))
           + " — ส่วนที่รอคืน ระบบไม่คืนเอง: คืนเงินแล้วบันทึกที่ \"เงินมัดจำ\" (R1)";

    public static string FailureNote(string number, string reason, DateTime atUtc)
        => $"[{RuleCode}-FAILED {atUtc:yyyy-MM-dd HH:mm}Z] ริบมัดจำ {number} ตามเงื่อนไขร้านไม่สำเร็จ: {reason} — กด \"รับรู้/ริบมัดจำ\" ที่ใบมัดจำเอง";
}
