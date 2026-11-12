namespace Accounting.Helpers;

/// <summary>บริษัทผู้เช่าที่ถือเลขผู้เสียภาษีเดียวกัน (ข้อมูลเท่าที่ตัวจับคู่ต้องใช้) — ผู้เรียกกรองเลขภาษีมาก่อน</summary>
public sealed record PartnerCompanyCandidate(Guid Id, string? BranchCode);

/// <summary>ผลการจับคู่บริษัทคู่ค้า</summary>
public enum PartnerMatchKind
{
    /// <summary>ไม่มีบริษัทที่ตรงเลขภาษี + สาขา</summary>
    None,
    /// <summary>ตรงหนึ่งบริษัทแบบไม่กำกวม</summary>
    Matched,
    /// <summary>ตรงมากกว่าหนึ่งบริษัท — <b>ห้ามเดา</b> (เดิม <c>FirstOrDefault</c> = สุ่มตามลำดับจากฐาน)</summary>
    Ambiguous,
}

public readonly record struct PartnerCompanyPick(PartnerMatchKind Kind, Guid? CompanyId);

/// <summary>
/// **ตัวจับคู่ "เลขผู้เสียภาษี + สาขา → บริษัทผู้เช่าคู่ค้า" ตัวเดียวของ workflow ข้ามบริษัท** (รอบ 250 WP-02b · AUTOPILOT_BILLING_DESIGN §6.1 P-1/P-2)
///
/// <para>═══ ที่มา ═══ <c>CrossTenantWorkflowService.InvitePartnerAsync</c> และ <c>OnDocumentSentAsync</c> ใช้
/// <c>FirstOrDefaultAsync(c =&gt; c.TaxId == …)</c> ไม่ดู <c>BranchCode</c> ⇒ สองบริษัทเลขเดียวกันต่างสาขา (นิติบุคคลเดียวที่แยกผู้เช่าต่อสาขา)
/// ได้บริษัทไหนก็ได้ตามลำดับจากฐาน — เอกสารขายของสาขา 00001 ไปโผล่ในกล่องของสำนักงานใหญ่ (P-2)</para>
///
/// <para>═══ กติกา ═══ (สาขาผ่าน <see cref="TaxBranchCode.Normalize"/> ตัวเดียว · ว่าง/ศูนย์ล้วน = สำนักงานใหญ่ 00000 ตามประกาศอธิบดีฯ ฉบับที่ 199)
/// <list type="number">
/// <item>ระบุสาขา ⇒ ต้องตรงสาขาแบบเป๊ะ · ตรงหนึ่งเดียว = จับ · หลายบริษัท = กำกวม · ไม่มี = ไม่พบ (ไม่ถอยไปสำนักงานใหญ่ — สาขาผิด = ส่งผิดสมุด)</item>
/// <item>ไม่ระบุสาขา ⇒ มีบริษัทเดียวของเลขนี้ = จับ (พฤติกรรมเดิมของคู่ที่ไม่เคยกรอกสาขา) · หลายบริษัท ⇒ จับเฉพาะเมื่อมีสำนักงานใหญ่หนึ่งเดียว · อื่น ๆ = กำกวม</item>
/// </list>
/// ต่างจาก <see cref="ContactTaxBranchKey.PickBranch"/> (ฝั่งผู้ติดต่อในบริษัทเดียว — ถอยไปสาขาต่ำสุดเมื่อกำกวม) เพราะที่นี่ปลายทางคือ<b>สมุดบัญชีของบริษัทอื่น</b>:
/// กำกวม ⇒ ไม่ส่ง ไม่เดา (F2 ข้อ 3)</para>
/// </summary>
public static class PartnerCompanyMatch
{
    public const string RuleCode = "XT-PARTNER-MATCH";

    /// <summary>คำตอบของการเชิญคู่ค้า — <b>เหมือนกันทุกกรณี</b> (มี/ไม่มีบริษัทของเลขนี้ในระบบ · เชิญซ้ำ · เชิญตัวเอง) กันการไล่เลขภาษีว่าใครใช้ NextAcc (P-1) ·
    /// รอบ 264 (คำตัดสิน 264-1-1): คำขอถึงบริษัทที่ตรงเลขภาษี + สาขาเสมอ (ไม่ต้องเปิด "ให้คู่ค้าค้นเจอ" ก่อน — เดิมคำขอหายเงียบเมื่อปลายทางไม่เปิด) ·
    /// ผู้เชิญรู้ผลเมื่อปลายทางตอบรับเท่านั้น (ตาราง "คำขอที่เราส่ง")</summary>
    public const string InviteAckMessage = "ส่งคำขอแล้ว ถ้าผู้รับใช้ NextAcc เจ้าของ/ผู้ดูแลของเขาจะได้รับแจ้ง (กระดิ่ง · อีเมล · LINE ตามที่เขาตั้ง) พร้อมลิงก์ให้กดยอมรับ — "
        + "ติดตามได้ที่ \"คำขอที่เราส่ง\" (ระบบไม่บอกว่าเลขนี้ใช้ NextAcc หรือไม่จนกว่าเขาจะตอบรับ · คำขอหมดอายุใน 30 วัน)";

    /// <summary>ข้อความเมื่อเลขภาษีที่กรอกผิดรูป — ตัดสินจากรูปแบบอย่างเดียว (ไม่ดูฐาน) ⇒ ไม่รั่วสมาชิกภาพ</summary>
    public const string InvalidTaxIdMessage = "เลขประจำตัวผู้เสียภาษีของคู่ค้าต้องเป็นตัวเลข 13 หลัก";

    /// <summary>ข้อความเมื่อพบหลายบริษัทที่ตรงเลขภาษี + สาขาของผู้ติดต่อ (ส่งเอกสารไม่ได้ — ต้องระบุสาขาของผู้ติดต่อให้ตรง)</summary>
    public static string AmbiguousRouteMessage(string taxId)
        => $"พบคู่ค้าที่ใช้ NextAcc มากกว่าหนึ่งบริษัทของเลขผู้เสียภาษี {taxId} — ระบุรหัสสาขาของผู้ติดต่อ (5 หลัก) ให้ตรงสาขาของคู่ค้า แล้วกดส่งใหม่";

    /// <summary>ฝ่ายค้าน M5: คู่ค้าที่เชื่อมแล้วมีเลขภาษีตรง แต่ไม่มีสาขาที่ผู้ติดต่อระบุ — เดิม (ไม่ดูสาขา) ส่งได้ · ตอนนี้ไม่ส่ง แต่ต้องดัง</summary>
    public static string BranchMismatchRouteMessage(string taxId, string? contactBranch)
        => $"คู่ค้าที่เชื่อมระบบไว้ (เลขผู้เสียภาษี {taxId}) ไม่มีสาขา {TaxBranchCode.Normalize(contactBranch)} ตามที่ผู้ติดต่อระบุ — "
           + "แก้รหัสสาขาของผู้ติดต่อให้ตรงสาขาของคู่ค้า (หรือให้สาขานั้นเชื่อมระบบ) แล้วกดส่งใหม่";

    /// <summary>เลขภาษีรูปแบบมาตรฐาน (ตัวเลข ASCII 13 หลัก) · ไม่ใช่ 13 หลัก = <c>null</c> (ไม่จับคู่ — ค่า "-" ของบริษัทที่ยังไม่กรอกเลขต้องไม่ชนกัน)</summary>
    public static string? NormalizeTaxId(string? raw)
    {
        var d = ThaiTaxId.Normalize(raw);
        return d.Length == 13 ? d : null;
    }

    /// <summary>สาขาที่ต้องการ — ว่าง = ไม่ระบุ (<c>null</c>) · อื่น ๆ = รหัส 5 หลัก</summary>
    public static string? WantedBranch(string? branchCode)
        => string.IsNullOrWhiteSpace(branchCode) ? null : TaxBranchCode.Normalize(branchCode);

    /// <summary>ตัดสินจากบริษัทที่ถือเลขภาษีเดียวกัน (ลำดับที่ส่งมาไม่มีผล · Id ซ้ำนับครั้งเดียว)</summary>
    public static PartnerCompanyPick Pick(IEnumerable<PartnerCompanyCandidate>? sameTaxId, string? branchCode)
    {
        var list = (sameTaxId ?? Enumerable.Empty<PartnerCompanyCandidate>())
            .GroupBy(c => c.Id).Select(g => g.First()).ToList();
        if (list.Count == 0) return new PartnerCompanyPick(PartnerMatchKind.None, null);

        var wanted = WantedBranch(branchCode);
        if (wanted != null)
            return Single(list.Where(c => TaxBranchCode.Normalize(c.BranchCode) == wanted).ToList());

        if (list.Count == 1) return new PartnerCompanyPick(PartnerMatchKind.Matched, list[0].Id);
        var hq = list.Where(c => TaxBranchCode.Normalize(c.BranchCode) == TaxBranchCode.HeadOffice).ToList();
        return hq.Count == 1
            ? new PartnerCompanyPick(PartnerMatchKind.Matched, hq[0].Id)
            : new PartnerCompanyPick(PartnerMatchKind.Ambiguous, null);
    }

    /// <summary>รอบ 264 (คำตัดสิน 264-1-3): ผู้รับ<b>คำขอเชื่อม</b> — ตรงหนึ่งเดียว ⇒ บริษัทนั้น · กำกวม ⇒ <b>ทุกบริษัทที่ตรง</b>
    /// (ระบุสาขา = ทุกบริษัทสาขานั้น · ไม่ระบุ = สำนักงานใหญ่ทุกราย ถ้ามี ≥ 2 · ไม่มีสำนักงานใหญ่ = ทุกบริษัทของเลขนี้) · ไม่มี ⇒ ว่าง ·
    /// ต่างจาก <see cref="Pick"/> (ส่ง<b>เอกสาร</b> — กำกวม ⇒ ไม่ส่ง) เพราะคำขอไม่ย้ายข้อมูลใด: ผู้รับต้องกดยอมรับเอง · ทิ้งเมื่อกำกวม = บริษัทที่ตั้งเลขซ้ำวัดได้ว่ามีอีกบริษัทถือเลขนั้น</summary>
    public static IReadOnlyList<Guid> InviteTargets(IEnumerable<PartnerCompanyCandidate>? sameTaxId, string? branchCode)
    {
        var list = (sameTaxId ?? Enumerable.Empty<PartnerCompanyCandidate>())
            .GroupBy(c => c.Id).Select(g => g.First()).ToList();
        var pick = Pick(list, branchCode);
        if (pick.Kind == PartnerMatchKind.Matched && pick.CompanyId is Guid one) return new[] { one };
        if (pick.Kind == PartnerMatchKind.None) return Array.Empty<Guid>();
        var wanted = WantedBranch(branchCode);
        if (wanted != null) return list.Where(c => TaxBranchCode.Normalize(c.BranchCode) == wanted).Select(c => c.Id).OrderBy(x => x).ToList();
        var hq = list.Where(c => TaxBranchCode.Normalize(c.BranchCode) == TaxBranchCode.HeadOffice).ToList();
        return (hq.Count >= 2 ? hq : list).Select(c => c.Id).OrderBy(x => x).ToList();
    }

    private static PartnerCompanyPick Single(List<PartnerCompanyCandidate> hits) => hits.Count switch
    {
        0 => new PartnerCompanyPick(PartnerMatchKind.None, null),
        1 => new PartnerCompanyPick(PartnerMatchKind.Matched, hits[0].Id),
        _ => new PartnerCompanyPick(PartnerMatchKind.Ambiguous, null),
    };
}
