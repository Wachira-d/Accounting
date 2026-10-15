using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **"ผังนี้ถูกอ้างถึงอยู่ไหม" — ตัวไล่ตัวเดียว** (ฝ่ายค้านรอบ ETL P1-3 → ยกเป็นเจ้าของกลางในฝ่ายค้านรอบส่งออก 2026-10-15 รอบ 2 P1-4)
///
/// <para>ไล่ <b>ทุก foreign key ที่ชี้ผังบัญชีในโมเดล</b> (บรรทัด JE · ยอดยกมา · สินค้า · ผู้ติดต่อ · บัญชีธนาคาร · ทะเบียนสินทรัพย์ · ฯลฯ — ตารางใหม่ที่อ้างผัง
/// ถูกนับอัตโนมัติ ไม่ใช่รายการที่เขียนมือ) + ค่าตั้งที่ไม่มี FK (<c>CompanySettings.DefaultPaymentAccountId</c>) · ไม่นับบัญชีลูก (ผู้เรียกดูเอง) ·
/// กรอง CompanyId เมื่อตารางมีคอลัมน์นั้น และจำกัดเฉพาะผังของบริษัทนี้เสมอ (กฎ M) · แถวที่ลบแบบ soft ไม่นับ</para>
/// <para>ผู้ใช้: ย้อนการนำเข้าผัง (<c>ChartOfAccountsImportTarget</c> — ห้ามลบผังที่ถูกอ้าง) · ผูกบัญชีแม่ (<see cref="ChartParentLink"/> —
/// ห้ามทำผังที่ถูกอ้างให้กลายเป็นบัญชีคุม)</para>
/// </summary>
public static class ChartAccountReferences
{
    /// <summary>หนึ่งคอลัมน์ที่อ้างผัง (ชื่อตาราง/คอลัมน์มาจาก metadata ของโมเดล — ไม่ใช่ค่าจากผู้ใช้)</summary>
    private sealed record Referrer(string From, string Column, string? CompanyColumn, string? DeletedColumn, string ClrName);

    private static List<Referrer>? Referrers(AccountingDbContext db)
    {
        var coa = db.Model.FindEntityType(typeof(ChartOfAccount));
        if (coa == null) return null;
        var list = new List<Referrer>();
        foreach (var fk in coa.GetReferencingForeignKeys())
        {
            var dep = fk.DeclaringEntityType;
            if (dep.ClrType == typeof(ChartOfAccount) || fk.Properties.Count != 1) continue;   // บัญชีลูก — ผู้เรียกดูเอง
            var table = dep.GetTableName();
            if (table == null) continue;
            var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, dep.GetSchema());
            var col = fk.Properties[0].GetColumnName(store);
            if (col == null) continue;
            var from = dep.GetSchema() is { Length: > 0 } schema ? $"\"{schema}\".\"{table}\"" : $"\"{table}\"";
            list.Add(new Referrer(from, col, dep.FindProperty("CompanyId")?.GetColumnName(store),
                dep.FindProperty("IsDeleted")?.GetColumnName(store), dep.ClrType.Name));
        }
        return list;
    }

    /// <summary>ผู้อ้างรายแรกของผังหนึ่ง (ชื่อคลาสของตารางที่อ้าง) · <c>ModelMissing</c> = ตรวจไม่ได้ (ผู้เรียกต้องล้มฝั่งปลอดภัย)</summary>
    public static async Task<(bool ModelMissing, string? ReferrerClrName)> FirstReferrerAsync(
        AccountingDbContext db, Guid companyId, Guid accountId, CancellationToken ct = default)
    {
        var referrers = Referrers(db);
        if (referrers == null) return (true, null);
        foreach (var r in referrers)
        {
            var where = $"\"{r.Column}\" = {{0}}";
            var args = new List<object> { accountId };
            if (r.CompanyColumn != null)
            {
                where += $" AND \"{r.CompanyColumn}\" = {{1}}";
                args.Add(companyId);
            }
            if (r.DeletedColumn != null) where += $" AND NOT \"{r.DeletedColumn}\"";
            // ค่าเป็นพารามิเตอร์ {0}/{1} · ชื่อตาราง/คอลัมน์มาจาก metadata
            var sql = "SELECT EXISTS (SELECT 1 FROM " + r.From + " WHERE " + where + ") AS \"Value\"";
            if (await db.Database.SqlQueryRaw<bool>(sql, args.ToArray()).SingleAsync(ct))
                return (false, r.ClrName);
        }
        return (false, null);
    }

    /// <summary>ผังของบริษัทที่ถูกอ้างถึงจากข้อมูลใดก็ได้ (ทุก FK + แหล่งเงินตั้งต้นของบริษัท) · <paramref name="onlyIds"/> = ตรวจเฉพาะผังเหล่านี้
    /// (ว่าง ⇒ ไม่ยิงคิวรีเลย) · โมเดลหาไม่เจอ ⇒ ถือว่าทุกผังถูกอ้าง (ฝั่งปลอดภัย)</summary>
    public static async Task<HashSet<Guid>> ReferencedAccountIdsAsync(AccountingDbContext db, Guid companyId,
        IReadOnlyCollection<Guid>? onlyIds = null, CancellationToken ct = default)
    {
        if (onlyIds != null && onlyIds.Count == 0) return new HashSet<Guid>();
        var only = onlyIds?.Distinct().ToArray();
        var referrers = Referrers(db);
        if (referrers == null)
            return (await db.ChartOfAccounts.AsNoTracking().Where(a => a.CompanyId == companyId).Select(a => a.Id).ToListAsync(ct))
                .Where(id => only == null || only.Contains(id)).ToHashSet();
        var coa = db.Model.FindEntityType(typeof(ChartOfAccount))!;
        var coaTable = coa.GetSchema() is { Length: > 0 } coaSchema ? $"\"{coaSchema}\".\"{coa.GetTableName()}\"" : $"\"{coa.GetTableName()}\"";
        var result = new HashSet<Guid>();
        foreach (var r in referrers)
        {
            var where = $"t.\"{r.Column}\" IN (SELECT c.\"Id\" FROM {coaTable} c WHERE c.\"CompanyId\" = {{0}})";
            if (r.CompanyColumn != null) where += $" AND t.\"{r.CompanyColumn}\" = {{0}}";
            if (r.DeletedColumn != null) where += $" AND NOT t.\"{r.DeletedColumn}\"";
            var args = new List<object> { companyId };
            if (only != null)
            {
                where += $" AND t.\"{r.Column}\" = ANY({{1}})";
                args.Add(only);
            }
            var sql = "SELECT DISTINCT t.\"" + r.Column + "\" AS \"Value\" FROM " + r.From + " t WHERE " + where;
            foreach (var id in await db.Database.SqlQueryRaw<Guid>(sql, args.ToArray()).ToListAsync(ct))
                result.Add(id);
        }
        foreach (var id in await db.CompanySettings.AsNoTracking()
                     .Where(s => s.CompanyId == companyId && s.DefaultPaymentAccountId != null)
                     .Select(s => s.DefaultPaymentAccountId!.Value)
                     .ToListAsync(ct))
            if (only == null || only.Contains(id)) result.Add(id);
        return result;
    }
}
