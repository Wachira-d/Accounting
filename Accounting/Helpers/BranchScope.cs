namespace Accounting.Helpers;

/// <summary>
/// ขอบเขตสาขาของผู้ใช้ — **ตัวตัดสินตัวเดียวของระบบ**
///
/// ═══ ที่มา (POS_MULTI_BRANCH_ANALYSIS.md เฟส 6) ═══
/// แคชเชียร์ของสาขา B เปิดกะบนเครื่องของสาขา A ได้ ⇒ ยอดขายลงผิดสาขา · ตัดสต็อก
/// ผิดคลัง · และเห็นยอดขายของสาขาที่ตัวเองไม่ได้ดูแล · <c>Employee.BranchId</c> มีอยู่แล้ว
/// แต่ <c>CompanyUser</c> (ตัวที่ตัดสินสิทธิ์จริง) ไม่มี
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item><b>null / ว่าง = ทุกสาขา</b> — พฤติกรรมเดิมทุกประการ · บริษัทสาขาเดียวและ
///   เจ้าของต้องไม่รู้สึกว่ามีอะไรเปลี่ยน (ห้ามให้ "ยังไม่ตั้งค่า" แปลว่า "ห้ามทุกอย่าง"
///   ไม่งั้นทุก tenant ที่อัปเกรดมาจะล็อกตัวเองออกจากระบบทันที)</item>
/// <item>ผู้ใช้ที่ถูกจำกัดแล้ว แต่**เครื่องยังไม่ผูกสาขา** (<c>branchId = null</c>) →
///   <b>ห้าม</b> — เพราะเราไม่รู้ว่าเครื่องนั้นอยู่สาขาไหน จึงตอบไม่ได้ว่าเขามีสิทธิ์ไหม
///   ("ไม่รู้ = ต้องบอกว่าไม่รู้" ไม่ใช่ปล่อยผ่าน)</item>
/// <item>ข้อความปฏิเสธต้องบอก**ทางไปต่อ** ไม่ใช่ตันเฉย ๆ</item>
/// </list>
/// </summary>
public static class BranchScope
{
    /// <summary>แปลง CSV เป็นชุด Guid — ค่าเสียหายถูกข้ามเงียบ ๆ (ไม่ทำให้ทั้งชุดพัง)</summary>
    public static IReadOnlySet<Guid> Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new HashSet<Guid>();
        var set = new HashSet<Guid>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Guid.TryParse(part, out var id)) set.Add(id);
        return set;
    }

    public static string? ToCsv(IEnumerable<Guid>? ids)
    {
        var list = (ids ?? Enumerable.Empty<Guid>()).Distinct().ToList();
        return list.Count == 0 ? null : string.Join(',', list.Select(x => x.ToString()));
    }

    /// <summary>ผู้ใช้คนนี้ถูกจำกัดสาขาไหม (ว่าง = ไม่ถูกจำกัด)</summary>
    public static bool IsRestricted(string? csv) => Parse(csv).Count > 0;

    /// <summary>เข้าถึงสาขานี้ได้ไหม
    ///
    /// <para><paramref name="branchId"/> = null แปลว่า "ยังไม่รู้ว่าสาขาไหน" — ผู้ใช้ที่
    /// ไม่ถูกจำกัดผ่านได้ (เหมือนเดิม) ส่วนผู้ใช้ที่ถูกจำกัดต้องถูกปฏิเสธ</para></summary>
    public static bool CanAccess(string? allowedCsv, Guid? branchId)
    {
        var allowed = Parse(allowedCsv);
        if (allowed.Count == 0) return true;              // ไม่ถูกจำกัด = ทุกสาขา
        return branchId is Guid id && allowed.Contains(id);
    }

    /// <summary>เหตุผลที่ปฏิเสธ พร้อมทางไปต่อ — null เมื่อผ่าน</summary>
    public static string? DenyReason(string? allowedCsv, Guid? branchId, string? branchName)
    {
        if (CanAccess(allowedCsv, branchId)) return null;
        return branchId is null
            ? "เครื่องขายนี้ยังไม่ได้ผูกสาขา — ผู้ใช้ที่ถูกจำกัดสาขาเปิดกะไม่ได้ "
              + "(ให้ผู้ดูแลตั้งสาขาของเครื่องที่ปุ่ม \"⚙️ ตั้งค่าเครื่อง\" ในหน้า POS ก่อน)"
            : $"คุณไม่มีสิทธิ์ทำงานที่สาขา \"{branchName ?? "นี้"}\" — "
              + "ให้ผู้ดูแลเพิ่มสาขานี้ในสิทธิ์ของคุณที่หน้าจัดการผู้ใช้";
    }
}
