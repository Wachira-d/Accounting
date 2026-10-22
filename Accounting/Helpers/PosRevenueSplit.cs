namespace Accounting.Helpers;

/// <summary>บรรทัดของบิลที่ใช้แบ่งยอดรายได้ — <paramref name="AccountId"/> null = บัญชีรายได้ขายมาตรฐาน (41000)</summary>
/// <param name="Weight">ฐานการแบ่ง (ยอดบรรทัดรวม VAT ก่อนส่วนลดท้ายบิล — ฐานเดียวกับที่ส่วนลด/ค่าบริการถูกเฉลี่ย)</param>
public readonly record struct PosRevenueWeight(Guid? AccountId, decimal Weight);

/// <summary>ยอดเครดิตรายได้ต่อบัญชี — <paramref name="AccountId"/> null = บัญชีรายได้ขายมาตรฐาน</summary>
public readonly record struct PosRevenuePart(Guid? AccountId, decimal Amount);

/// <summary>
/// **แบ่งเครดิตรายได้ของบิล POS ลงหลายบัญชี** — ตัวเดียวที่ทั้ง JE ขาย (<c>CreateSalesJournalEntryAsync</c>)
/// และ JE คืนเงิน (<c>CreateRefundJournalEntryAsync</c>) ใช้ ⇒ คืนเงินกลับรายการ<b>เข้าบัญชีเดียวกับที่ขายลงไว้</b>
///
/// <para>═══ ที่มา (ทีม K · 2026-10-22 · "Quick Add / Custom Item") ═══ แคชเชียร์ขายบริการพิเศษที่ไม่มีในระบบ
/// (ค่าเดินทางนอกสถานที่ · ค่าแก้งาน) และเลือกบัญชีรายได้ของรายการนั้นได้ — เดิม JE ขายเครดิต 41000 ก้อนเดียวเสมอ ·
/// ถ้าแบ่งที่ฝั่งขายแต่ฝั่งคืนยังลง 41000 ⇒ บัญชีรายได้พิเศษค้างยอด 41000 ติดลบ (defect class "ฝั่งอ่าน/ฝั่งเขียนไม่สมมาตร")</para>
///
/// <para><b>กติกา</b>:
/// <list type="bullet">
/// <item>Σ ผลลัพธ์ = <paramref name="amount"/> <b>เป๊ะ</b> — แบ่งตามสัดส่วนน้ำหนัก ปัด 2 ตำแหน่ง <c>AwayFromZero</c>
///   แล้วโยนเศษสตางค์ให้กองที่น้ำหนักมากที่สุด (JE ต้องสมดุล — ปัดอิสระแล้วบวกกันคลาดได้ 0.01)</item>
/// <item>ทุกบรรทัดไม่ระบุบัญชี ⇒ ได้กองเดียว <c>(null, amount)</c> = <b>JE หน้าตาเดิมทุกสตางค์</b> (บิลที่ถูกอยู่แล้วไม่ถูกแตะ)</item>
/// <item>น้ำหนักรวม ≤ 0 (บิลยอด 0 / มีแต่บรรทัดติดลบ) ⇒ ทั้งก้อนเข้าบัญชีมาตรฐาน — ไม่มีฐานให้แบ่ง ห้ามหารศูนย์</item>
/// <item>บรรทัดน้ำหนักติดลบไม่ได้ส่วนแบ่ง (เป็นยอดหัก ไม่ใช่รายได้ของบัญชีนั้น)</item>
/// <item>ผลเรียงคงที่: บัญชีมาตรฐาน (null) ก่อน แล้วตาม AccountId</item>
/// </list></para>
/// </summary>
public static class PosRevenueSplit
{
    private const MidpointRounding R = MidpointRounding.AwayFromZero;

    public static IReadOnlyList<PosRevenuePart> Split(IEnumerable<PosRevenueWeight> lines, decimal amount)
    {
        var groups = new Dictionary<Guid, decimal>();
        decimal defaultWeight = 0m;
        foreach (var l in lines)
        {
            if (l.Weight <= 0m) continue;
            if (l.AccountId is Guid a && a != Guid.Empty)
                groups[a] = (groups.TryGetValue(a, out var w) ? w : 0m) + l.Weight;
            else
                defaultWeight += l.Weight;
        }

        var total = defaultWeight + groups.Values.Sum();
        if (groups.Count == 0 || total <= 0m)
            return new[] { new PosRevenuePart(null, amount) };

        var keys = new List<Guid?>();
        var weights = new List<decimal>();
        if (defaultWeight > 0m) { keys.Add(null); weights.Add(defaultWeight); }
        foreach (var kv in groups.OrderBy(k => k.Key)) { keys.Add(kv.Key); weights.Add(kv.Value); }

        var parts = new decimal[keys.Count];
        decimal running = 0m;
        var biggest = 0;
        for (var i = 0; i < keys.Count; i++)
        {
            parts[i] = Math.Round(amount * weights[i] / total, 2, R);
            running += parts[i];
            if (weights[i] > weights[biggest]) biggest = i;
        }
        parts[biggest] += amount - running;

        var result = new List<PosRevenuePart>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
            if (parts[i] != 0m) result.Add(new PosRevenuePart(keys[i], parts[i]));
        if (result.Count == 0) result.Add(new PosRevenuePart(null, amount));
        return result;
    }
}
