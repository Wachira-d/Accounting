using System.Reflection;
using Accounting.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 276 (ฝ่ายค้าน LOW-11) — ไล่ <b>ทุก route ของแอป</b> (reflection ของ controller ทุกตัว) เทียบรายการอนุญาตของผู้ถูกจำกัดเอกสาร
/// (<see cref="DocumentRestrictionPolicy.Routes"/>): (1) ทุกแถวในรายการตรงกับ action จริง (กัน drift) · (2) route ที่แสดงข้อมูลรวมของบริษัท
/// (รายงาน · GL · ธนาคาร · ภาษี · เงินเดือน · แดชบอร์ด · ค้นหา · แชต) ไม่มีตัวไหนหลุดเข้ารายการ · (3) ทุก route ในรายการที่มี id ของเอกสาร/สแกน/ไฟล์แนบ
/// ใช้ชื่อพารามิเตอร์ที่ <c>DocumentRestrictionFilter</c> ตรวจได้จริง (ชื่ออื่น = ตัวกรองไม่ตรวจ ⇒ ห้าม)
/// </summary>
public class DocumentRestrictionRouteInventoryTests
{
    private static readonly DocumentRestrictionPolicy.Restriction Own = new(true, true);

    private sealed record AppRoute(string Method, string Template, bool Anonymous);

    private static List<AppRoute> AllRoutes()
    {
        var list = new List<AppRoute>();
        foreach (var ctl in typeof(Accounting.Controllers.DocumentController).Assembly.GetTypes()
                     .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var name = ctl.Name.EndsWith("Controller") ? ctl.Name[..^"Controller".Length] : ctl.Name;
            var bases = ctl.GetCustomAttributes<RouteAttribute>(true).Select(r => r.Template.Replace("[controller]", name)).DefaultIfEmpty("").ToList();
            var ctlAnon = ctl.GetCustomAttributes<AllowAnonymousAttribute>(true).Any();
            foreach (var m in ctl.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                var anon = ctlAnon || m.GetCustomAttributes<AllowAnonymousAttribute>(true).Any();
                foreach (var h in m.GetCustomAttributes<HttpMethodAttribute>(true))
                    foreach (var verb in h.HttpMethods)
                        foreach (var b in bases)
                        {
                            var t = h.Template ?? "";
                            var full = t.StartsWith("~/") ? t[2..] : t.StartsWith("api/") || t.StartsWith("/") || b == "" ? t : string.IsNullOrEmpty(t) ? b : b + "/" + t;
                            list.Add(new AppRoute(verb.ToUpperInvariant(), DocumentRestrictionPolicy.NormalizeTemplate(full), anon));
                        }
            }
        }
        return list;
    }

    [Fact]
    public void รายการอนุญาตทุกแถวตรงกับ_action_จริง()
    {
        var actual = AllRoutes();
        var exact = actual.Select(r => (r.Method, r.Template)).ToHashSet();
        var missing = new List<string>();
        foreach (var r in DocumentRestrictionPolicy.Routes)
        {
            var t = DocumentRestrictionPolicy.NormalizeTemplate(r.Template);
            var ok = t.EndsWith("/*")
                ? actual.Any(a => a.Method == r.Method && a.Template.StartsWith(t[..^1], StringComparison.Ordinal))
                : exact.Contains((r.Method.ToUpperInvariant(), t));
            if (!ok) missing.Add(r.Method + " " + r.Template);
        }
        Assert.True(missing.Count == 0, "แถวที่ไม่มี action จริง: " + string.Join(" | ", missing));
    }

    private static readonly string[] CompanyWideMarkers =
    {
        "report", "journal", "ledger", "trial-balance", "/bank", "tax-report", "pnd", "pp30", "payroll", "dashboard", "search",
        "assistant", "chat", "accountant-workspace", "aging", "cash-flow", "balance-sheet", "profit-loss", "budget", "executive",
        "payments", "customer-credits", "deposits", "employees", "audit",
    };

    [Fact]
    public void route_ที่แสดงข้อมูลรวมของบริษัท_ไม่หลุดเข้ารายการอนุญาตของผู้เห็นเฉพาะของตัวเอง()
    {
        var leaked = AllRoutes()
            // ทะเบียนราชการสาธารณะ (DBD/ข้อมูลกลาง) ไม่ใช่ข้อมูลของบริษัท — ค้นได้
            .Where(r => !r.Anonymous && !r.Template.StartsWith("dbd/") && !r.Template.StartsWith("gov/")
                && CompanyWideMarkers.Any(k => r.Template.Contains(k, StringComparison.Ordinal)))
            .Where(r => DocumentRestrictionPolicy.Decide(Own, r.Method, r.Template).Allowed)
            .Select(r => r.Method + " " + r.Template).Distinct().ToList();
        Assert.True(leaked.Count == 0, "หลุดเข้ารายการ: " + string.Join(" | ", leaked));
    }

    /// <summary>ชื่อพารามิเตอร์ที่ตัวกรองรู้จัก (ตรวจของตัวเอง/สถานะ) + ชื่อที่ไม่ใช่ข้อมูลของผู้ใช้อื่น (ข้อมูลหลัก/ค่าคงที่)</summary>
    private static readonly HashSet<string> KnownParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "companyid", "documentid", "sourceid", "entitytype", "entityid", "attachmentid", "scanid", "fileattachmentid",
        "contactid", "productid", "revisionnumber", "targettype", "documenttype", "devicetoken", "token", "signatureid",
    };

    [Fact]
    public void ทุก_route_ในรายการ_ใช้ชื่อพารามิเตอร์ที่ตัวกรองตรวจได้()
    {
        var bad = new List<string>();
        foreach (var r in DocumentRestrictionPolicy.Routes)
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(r.Template, @"\{([a-zA-Z]+)"))
                if (!KnownParams.Contains(m.Groups[1].Value)) bad.Add(r.Template + " → " + m.Groups[1].Value);
        }
        Assert.True(bad.Count == 0, "พารามิเตอร์ที่ตัวกรองไม่รู้จัก: " + string.Join(" | ", bad));
    }

    [Fact]
    public void ผู้เห็นเฉพาะของตัวเอง_เรียกได้เฉพาะส่วนน้อยของแอป_ที่เหลือปฏิเสธ()
    {
        var all = AllRoutes().Where(r => !r.Anonymous).ToList();
        var allowed = all.Count(r => DocumentRestrictionPolicy.Decide(Own, r.Method, r.Template).Allowed);
        Assert.True(allowed > 50 && allowed < all.Count / 10, $"อนุญาต {allowed} จาก {all.Count} route");
    }
}
