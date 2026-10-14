using Accounting.Data;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Tax;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Import;

/// <summary>
/// **กระทบยอดหลังย้ายระบบ** (ขั้นสุดท้ายของลำดับ §2.10) — เจ้าของ <b>ทีม D</b> (รอบ 205) · สัญญาที่ <see cref="IOpeningBalanceReconciliation"/>
///
/// <para>อ่านอย่างเดียว · ไม่ลงตัว = บรรทัดพร้อม Hint ทางไปต่อ (ไม่บล็อก — ผู้ใช้ตัดสิน · คำตัดสินข้อ 150):</para>
/// <list type="bullet">
/// <item><b>ลูกหนี้/เจ้าหนี้</b> = ผลของ <see cref="SubLedgerReconciliationService.ReconcileAsync"/> ตัวเดิม (ห้ามคำนวณซ้ำ) — ยกเฉพาะแถว AR/AP มา</item>
/// <item><b>สินค้าคงเหลือ</b> = Σ(สต็อก × ต้นทุน) ของสินค้าที่ติดตามสต็อก เทียบยอดบัญชีสินค้าคงเหลือ (115xx หรือชื่อ "สินค้าคงเหลือ" ไม่รวมค่าเผื่อ)</item>
/// <item><b>สินทรัพย์ — ราคาทุน</b> = Σ ราคาทุนในทะเบียน เทียบบัญชีสินทรัพย์ที่ <see cref="FixedAssetAccountClassifier"/> รู้จัก ·
/// <b>ค่าเสื่อมราคาสะสม</b> = Σ ค่าเสื่อมสะสมในทะเบียน เทียบบัญชีค่าเสื่อมราคาสะสม (183xx หรือชื่อ "ค่าเสื่อมราคาสะสม" — ยอดเครดิตกลับเครื่องหมาย)</item>
/// </list>
/// <para>ยอดบัญชีมาจาก <see cref="IAccountingService.GetAccountBalanceAsOfAsync"/> (ยอดยกมา + รายการเคลื่อนไหว ฐานเดียวกับงบทดลอง) · tenant: ทุก query กรอง CompanyId</para>
/// </summary>
public class OpeningBalanceReconciliation : IOpeningBalanceReconciliation
{
    public const decimal Tolerance = 0.01m;

    private readonly AccountingDbContext _db;
    private readonly SubLedgerReconciliationService _subLedger;
    private readonly IAccountingService _accounting;

    public OpeningBalanceReconciliation(AccountingDbContext db, SubLedgerReconciliationService subLedger, IAccountingService accounting)
    {
        _db = db;
        _subLedger = subLedger;
        _accounting = accounting;
    }

    public async Task<ImportReconciliationReportDto> ReconcileAsync(Guid companyId, DateTime asOf, CancellationToken ct = default)
    {
        var lines = new List<ImportReconciliationLineDto>();

        // ── ลูกหนี้/เจ้าหนี้: ตัวกระทบยอดเดิม ──
        var sub = await _subLedger.ReconcileAsync(companyId, asOf, Tolerance);
        foreach (var l in sub.Lines)
        {
            var area = l.Category.StartsWith("ลูกหนี้", StringComparison.Ordinal) ? "AR"
                : l.Category.StartsWith("เจ้าหนี้", StringComparison.Ordinal) ? "AP" : null;
            if (area == null) continue;
            lines.Add(Line(area, $"{(area == "AR" ? "ลูกหนี้คงค้าง" : "เจ้าหนี้คงค้าง")} vs บัญชี {l.AccountCode} {l.AccountName}",
                l.SubLedgerBalance, l.GlBalance,
                area == "AR"
                    ? $"นำเข้าลูกหนี้คงค้างยกมา (เอกสารค้างรับ) ให้ครบ หรือแก้ยอดยกมาของบัญชี {l.AccountCode} ในงบทดลองยกมา"
                    : $"นำเข้าเจ้าหนี้คงค้างยกมา (เอกสารค้างจ่าย) ให้ครบ หรือแก้ยอดยกมาของบัญชี {l.AccountCode} ในงบทดลองยกมา"));
        }

        var balances = await _accounting.GetAccountBalanceAsOfAsync(companyId, asOf);
        var chart = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.AccountType == AccountType.Asset)
            .Select(a => new { a.Id, a.AccountCode, a.AccountName })
            .ToListAsync(ct);
        decimal Bal(IEnumerable<Guid> ids) => ids.Sum(id => balances.TryGetValue(id, out var b) ? b : 0m);

        // ── สินค้าคงเหลือ ──
        var invAccounts = chart.Where(a => (a.AccountCode.StartsWith("115", StringComparison.Ordinal) || a.AccountName.Contains("สินค้าคงเหลือ"))
                && !a.AccountName.Contains("ค่าเผื่อ")).ToList();
        var stock = await _db.Products.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.TrackStock)
            .Select(p => new { p.CurrentStock, p.CostPrice })
            .ToListAsync(ct);
        var stockValue = Math.Round(stock.Sum(p => p.CurrentStock * p.CostPrice), 2, MidpointRounding.AwayFromZero);
        if (invAccounts.Count > 0 || stockValue != 0m)
            lines.Add(Line("Inventory", "มูลค่าสต็อก (จำนวน × ต้นทุน) vs บัญชีสินค้าคงเหลือ", stockValue, Bal(invAccounts.Select(a => a.Id)),
                invAccounts.Count == 0
                    ? "ไม่พบบัญชีสินค้าคงเหลือในผังบัญชี — เพิ่มบัญชี 115xx แล้วลงยอดยกมา"
                    : "นำเข้าสต็อกยกมา (จำนวน + ต้นทุน) ให้ตรงยอดบัญชีสินค้าคงเหลือ หรือแก้ยอดยกมาในงบทดลอง"));

        // ── สินทรัพย์: ราคาทุน + ค่าเสื่อมสะสม ──
        var assets = await _db.FixedAssets.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.Status != AssetStatus.Disposed && a.Status != AssetStatus.WrittenOff)
            .Select(a => new { a.PurchaseCost, a.AccumulatedDepreciation })
            .ToListAsync(ct);
        var costAccounts = chart.Where(a => FixedAssetAccountClassifier.IsRegisterable(a.AccountCode)).ToList();
        var accumAccounts = chart.Where(a => a.AccountCode.StartsWith("183", StringComparison.Ordinal) || a.AccountName.Contains("ค่าเสื่อมราคาสะสม")).ToList();
        if (assets.Count > 0 || costAccounts.Count > 0)
        {
            lines.Add(Line("FixedAssetCost", "ราคาทุนตามทะเบียนสินทรัพย์ vs บัญชีที่ดิน อาคารและอุปกรณ์",
                assets.Sum(a => a.PurchaseCost), Bal(costAccounts.Select(a => a.Id)),
                "นำเข้าทะเบียนสินทรัพย์ให้ครบ หรือแก้ยอดยกมาบัญชี 122xx/123xx ในงบทดลอง"));
            // ค่าเสื่อมสะสมเป็นบัญชีปรับมูลค่า (ชนิดสินทรัพย์ ยอดเครดิต) ⇒ กลับเครื่องหมายให้เป็นยอดบวกเทียบทะเบียน
            lines.Add(Line("AccumulatedDepreciation", "ค่าเสื่อมราคาสะสมตามทะเบียน vs บัญชีค่าเสื่อมราคาสะสม",
                assets.Sum(a => a.AccumulatedDepreciation), -Bal(accumAccounts.Select(a => a.Id)),
                "ทะเบียนยังไม่รองรับค่าเสื่อมสะสมยกมารายสินทรัพย์ — ถ้าส่วนต่างเท่ากับค่าเสื่อมสะสมยกมาในงบทดลอง ถือว่าถูกต้อง (บันทึกเหตุผลไว้)"));
        }

        return new ImportReconciliationReportDto(asOf.Date, lines.All(l => l.Balanced), lines);
    }

    private static ImportReconciliationLineDto Line(string area, string label, decimal subledger, decimal ledger, string hintWhenOff)
    {
        var s = Math.Round(subledger, 2, MidpointRounding.AwayFromZero);
        var g = Math.Round(ledger, 2, MidpointRounding.AwayFromZero);
        var diff = Math.Round(g - s, 2, MidpointRounding.AwayFromZero);
        var ok = Math.Abs(diff) <= Tolerance;
        return new ImportReconciliationLineDto(area, label, s, g, diff, ok,
            ok ? null : $"ต่างกัน {diff:N2} — {hintWhenOff}");
    }
}
