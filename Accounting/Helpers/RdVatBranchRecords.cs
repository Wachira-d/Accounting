using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>หนึ่งสถานประกอบการ (สำนักงานใหญ่หรือสาขา) จากบริการตรวจผู้ประกอบการ VAT ของกรมสรรพากร</summary>
/// <param name="BranchNumber">เลขสาขาตามที่ RD คืน (0 = สำนักงานใหญ่) · null = RD ไม่ได้ส่งเลขสาขามา</param>
public sealed record RdVatBranchRecord(
    int? BranchNumber,
    string Name,
    string? BranchTitle,
    string? BranchName,
    string Address)
{
    /// <summary>รหัส 5 หลักตามประกาศอธิบดีฯ 199 (null เมื่อ RD ไม่ส่งเลขสาขา)</summary>
    public string? BranchCode => BranchNumber is int n && n >= 0 ? n.ToString("D5") : null;
}

/// <summary>
/// อ่านผลของ <c>rdws.rd.go.th/serviceRD3/vatserviceRD3</c> (SOAP) เป็น <b>รายสถานประกอบการ</b>
///
/// <para><b>ทำไม</b> (รอบ 190 ทีม C ข้อ 5 — "ดึง DBD แยกสาขาได้ไหม"): บริการนี้คืนแต่ละช่อง
/// (<c>vName</c> · <c>vHouseNumber</c> · <c>vBranchNumber</c> …) เป็น <b>ลิสต์ <c>anyType</c> ที่เรียงตรงกัน
/// ตามดัชนี</b> — ดัชนีที่ i ของทุกช่องคือสถานประกอบการเดียวกัน. ตัวอ่านเดิมใน
/// <c>DbdLookupService.LookupRdVatAsync</c> หยิบ "ค่าแรกที่ไม่ว่าง" <b>ทีละช่องแยกกัน</b> ⇒ ใช้ได้เมื่อมี
/// สถานประกอบการเดียว แต่ถ้า RD คืนหลายแถว ที่อยู่ที่ได้อาจเป็นเลขบ้านของแถวหนึ่ง + ตำบลของอีกแถว.
/// ตัวนี้ประกอบ <b>ทั้งแถวจากดัชนีเดียวกัน</b> แล้วให้ผู้เรียกเลือกแถวตามเลขสาขา</para>
///
/// <para><b>"ไม่รู้" ต้องเป็น "ไม่รู้"</b>: ถ้าลิสต์แต่ละช่องยาวไม่เท่ากัน (จัดแถวไม่ได้) → คืนลิสต์ว่าง
/// ไม่เดาจับคู่ · <see cref="PickBranch"/> คืนแถวเฉพาะเมื่อ <c>vBranchNumber</c> ตรงจริง — ไม่มีเลขสาขา
/// = ไม่ถือว่าเป็นสาขาที่ถาม (กัน "ที่อยู่สำนักงานใหญ่ถูกติดป้ายว่าเป็นของสาขาที่ 8")</para>
///
/// <para>Pure — ไม่ยิงเครือข่าย (ผู้เรียกส่ง XML เข้ามา) เทสต์ได้ด้วยข้อความตัวอย่าง</para>
/// </summary>
public static class RdVatBranchRecords
{
    // ช่องที่ประกอบเป็นที่อยู่ — ลำดับเดียวกับตัวประกอบเดิมใน DbdLookupService.LookupRdVatAsync
    private static readonly string[] AddressTags =
    {
        "vHouseNumber", "vRoomNumber", "vFloorNumber", "vBuildingName", "vVillageName",
        "vMooNumber", "vSoiName", "vStreetName", "vThambol", "vAmphur", "vProvince", "vPostCode",
    };

    public static IReadOnlyList<RdVatBranchRecord> Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return Array.Empty<RdVatBranchRecord>();

        var title = Values(xml, "vtitleName");
        var name = Values(xml, "vName");
        var surname = Values(xml, "vSurname");
        var branchTitle = Values(xml, "vBranchTitleName");
        var branchName = Values(xml, "vBranchName");
        var branchNumber = Values(xml, "vBranchNumber");
        var addr = AddressTags.ToDictionary(t => t, t => Values(xml, t));

        var n = name.Count;
        if (n == 0) return Array.Empty<RdVatBranchRecord>();
        // ทุกช่องที่มีค่าต้องยาวเท่ากัน — ไม่เท่า = จัดแถวไม่ได้ ⇒ ไม่ตอบ (ห้ามเดาจับคู่ข้ามแถว)
        var lists = new List<List<string>> { title, surname, branchTitle, branchName, branchNumber };
        lists.AddRange(addr.Values);
        if (lists.Any(l => l.Count != 0 && l.Count != n)) return Array.Empty<RdVatBranchRecord>();

        string At(List<string> l, int i) => l.Count == n ? l[i] : "";
        var result = new List<RdVatBranchRecord>(n);
        for (var i = 0; i < n; i++)
        {
            var fullName = string.Join(" ", new[] { At(title, i), At(name, i), At(surname, i) }
                .Where(s => s.Length > 0));
            if (fullName.Length == 0) continue;
            int? bn = int.TryParse(At(branchNumber, i), out var parsed) ? parsed : null;
            result.Add(new RdVatBranchRecord(
                bn, fullName,
                NullIfEmpty(At(branchTitle, i)),
                NullIfEmpty(At(branchName, i)),
                ComposeAddress(k => At(addr[k], i))));
        }
        return result;
    }

    /// <summary>แถวของสาขาที่ถาม — เทียบด้วย "เลขสาขา" ที่ RD ส่งมาเท่านั้น (00008 ≡ 8)</summary>
    public static RdVatBranchRecord? PickBranch(IReadOnlyList<RdVatBranchRecord> records, string? branchCode)
    {
        if (records == null || records.Count == 0) return null;
        if (!TaxBranchCode.TryNormalize(branchCode, out var code, out _) || code == null) return null;
        var want = int.Parse(code);
        return records.FirstOrDefault(r => r.BranchNumber == want);
    }

    /// <summary>ที่อยู่รูปเดียวกับตัวประกอบเดิม (บ้านเลขที่ ห้อง ชั้น อาคาร หมู่บ้าน หมู่ ซอย ถนน ตำบล อำเภอ จังหวัด ไปรษณีย์)</summary>
    private static string ComposeAddress(Func<string, string> get)
    {
        var parts = new List<string>();
        void Add(string tag, string prefix = "")
        {
            var v = get(tag);
            if (v.Length > 0) parts.Add(prefix + v);
        }
        Add("vHouseNumber");
        Add("vRoomNumber", "ห้อง ");
        Add("vFloorNumber", "ชั้น ");
        Add("vBuildingName", "อาคาร ");
        Add("vVillageName", "หมู่บ้าน ");
        Add("vMooNumber", "หมู่ ");
        Add("vSoiName", "ซอย ");
        Add("vStreetName", "ถนน ");
        Add("vThambol", "ตำบล ");
        Add("vAmphur", "อำเภอ ");
        Add("vProvince", "จังหวัด ");
        Add("vPostCode");
        return string.Join(" ", parts);
    }

    /// <summary>ค่าทุกตัวใน <c>&lt;tag&gt;&lt;anyType&gt;…&lt;/anyType&gt;…&lt;/tag&gt;</c> ตามลำดับ ·
    /// "-" (ตัวแทนค่าว่างของ RD) = ว่าง แต่ <b>คงตำแหน่งไว้</b> เพื่อให้ดัชนีตรงกันทุกช่อง</summary>
    private static List<string> Values(string xml, string tag)
    {
        var m = Regex.Match(xml, $"<{tag}>(.*?)</{tag}>", RegexOptions.Singleline);
        if (!m.Success) return new List<string>();
        var inner = m.Groups[1].Value;
        var items = Regex.Matches(inner, @"<anyType[^>]*?(?:/>|>([^<]*)</anyType>)");
        if (items.Count == 0)
        {
            var single = System.Net.WebUtility.HtmlDecode(inner.Trim());
            return new List<string> { single == "-" ? "" : single };
        }
        return items.Select(x =>
        {
            var v = System.Net.WebUtility.HtmlDecode(x.Groups[1].Value).Trim();
            return v == "-" ? "" : v;
        }).ToList();
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
