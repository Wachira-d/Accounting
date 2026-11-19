using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 273 — <b>รายงานอ่านอย่างเดียว</b> "เลขประจำตัวที่ต้องแก้" (หลังเหตุ ENCRYPTION_KEY ถูกเปลี่ยน · F2 ข้อ 9: ของเสียที่ persist แล้วต้องมีทางเห็น)
///
/// <para>สองแหล่ง:
/// <list type="number">
/// <item><b>ทะเบียนพนักงาน</b> (<c>CitizenId</c>/<c>TaxId</c>) — อ่านค่าดิบข้าม converter แล้วตัดสินด้วย <see cref="EncryptedColumnConverter.Inspect"/> ตัวเดียวกับฝั่งอ่าน:
///   ถอดไม่ออกทุกคีย์ (กู้ไม่ได้ ⇒ ตั้ง <c>ENCRYPTION_KEY_PREVIOUS</c> หรือกรอกใหม่) · เข้ารหัสซ้อน (ถอดแล้วได้ enc1:… อีกชั้น) · อยู่บนคีย์เดิม (อ่านได้แล้ว รองานย้ายคีย์) ·
///   ถอดได้แต่ไม่ใช่เลข 13 หลักที่ถูกต้อง</item>
/// <item><b>ผู้รับใน 50 ทวิ ที่ยังไม่ยกเลิก</b> ที่เลขใน <c>Contact.TaxId</c> ใช้ไม่ได้ — ส่วนใหญ่คือ Contact ที่ระบบเคยสร้างจากเลขที่ตัดจาก ciphertext
///   ตอนออก 50 ทวิ รายเดือน (ทางแก้: "สร้างเอกสารใหม่" ที่รอบเงินเดือน — ยกเลิกใบเดิมและออกใหม่ด้วยเลขที่ถูก)</item>
/// </list>
/// <b>ไม่แก้อะไรและไม่แต่งค่า</b> · เลขปิดบังเสมอ (จำนวนหลัก/หลักแรก-ท้าย) · ทุก query กรอง <c>CompanyId</c> (กฎ M)</para>
/// </summary>
public static class EmployeeIdentityHealth
{
    public enum Issue
    {
        /// <summary>enc1: ที่ถอดไม่ออกทุกคีย์ — ข้อมูลกู้ไม่ได้จนกว่าจะได้คีย์เดิมคืน</summary>
        Undecryptable,
        /// <summary>ถอดแล้วได้ enc1: อีกชั้น (เคยถูกห่อซ้อน) — ระบบไม่ถอดชั้นในเอง (ปิด oracle รอบ 231) ⇒ กรอกใหม่</summary>
        NestedCipher,
        /// <summary>ถอดได้ด้วยคีย์เดิม — อ่านถูกแล้ว · งานย้ายคีย์จะเข้ารหัสใหม่ (ไม่ต้องทำอะไร)</summary>
        PendingRekey,
        /// <summary>ค่าที่ถอดแล้ว/ผู้รับใน 50 ทวิ ไม่ใช่เลข 13 หลักที่ถูกต้อง</summary>
        Invalid,
    }

    public sealed record Row(string Source, string Ref, string? Name, string Field, Issue Issue, string Masked, string Action, bool BlocksFiling);

    public sealed record Report(IReadOnlyList<Row> Rows)
    {
        public int Blocking => Rows.Count(r => r.BlocksFiling);
    }

    /// <param name="currentKey">null = คีย์ของ converter</param>
    /// <param name="previousKeys">null = ชุดคีย์เดิมของ converter</param>
    public static async Task<Report> BuildAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default,
        string? currentKey = null, IReadOnlyList<string>? previousKeys = null)
    {
        var current = currentKey ?? EncryptedColumnConverter.Keys.Current;
        var previous = previousKeys ?? EncryptedColumnConverter.Keys.Previous;
        var rows = new List<Row>();

        // ── 1. ทะเบียนพนักงาน — ค่าดิบ (ข้าม converter) ──
        var emp = db.Model.FindEntityType(typeof(Employee))!;
        string Col(string prop) => emp.FindProperty(prop)!.GetColumnName();
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT \"{Col(nameof(Employee.EmployeeCode))}\", \"{Col(nameof(Employee.FirstNameTh))}\", \"{Col(nameof(Employee.LastNameTh))}\", "
                + $"\"{Col(nameof(Employee.CitizenId))}\", \"{Col(nameof(Employee.TaxId))}\" FROM \"{emp.GetTableName()}\" "
                + "WHERE \"CompanyId\" = @cid AND \"IsDeleted\" = false ORDER BY 1";
            var p = cmd.CreateParameter(); p.ParameterName = "cid"; p.Value = companyId; cmd.Parameters.Add(p);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var code = rd.IsDBNull(0) ? "" : rd.GetString(0);
                var name = $"{(rd.IsDBNull(1) ? "" : rd.GetString(1))} {(rd.IsDBNull(2) ? "" : rd.GetString(2))}".Trim();
                for (var i = 3; i <= 4; i++)
                {
                    var raw = rd.IsDBNull(i) ? null : rd.GetString(i);
                    var field = i == 3 ? "เลขบัตรประชาชน" : "เลขประจำตัวผู้เสียภาษี";
                    if (Classify(raw, current, previous) is (Issue issue, string masked))
                        rows.Add(new Row("พนักงาน", code, name, field, issue, masked, ActionFor(issue), issue != Issue.PendingRekey));
                }
            }
        }
        finally
        {
            if (openedHere) await conn.CloseAsync();
        }

        // ── 2. ผู้รับใน 50 ทวิ ที่ยังไม่ยกเลิก ──
        var certs = await db.WithholdingTaxCerts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.Status != WithholdingTaxCertStatus.Voided)
            .Select(c => new { c.CertificateNumber, c.SourcePayrollRunId, c.PayeeContactId })
            .ToListAsync(ct);
        var contactIds = certs.Select(c => c.PayeeContactId).Distinct().ToList();
        var contacts = await db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.TaxId })
            .ToDictionaryAsync(c => c.Id, ct);
        foreach (var c in certs.OrderBy(c => c.CertificateNumber, StringComparer.Ordinal))
        {
            if (!contacts.TryGetValue(c.PayeeContactId, out var k)) continue;
            var id = EmployeeTaxIdentity.InspectValue(k.TaxId);
            if (id.Problem is not (TaxIdProblem.Undecryptable or TaxIdProblem.Invalid)) continue;
            rows.Add(new Row("หนังสือรับรอง 50 ทวิ", c.CertificateNumber, k.Name, "เลขผู้ถูกหักภาษี", Issue.Invalid, id.Masked ?? "",
                c.SourcePayrollRunId != null
                    ? "แก้เลขที่ทะเบียนพนักงาน (หรือตั้งคีย์เดิม) แล้วกด “สร้างเอกสารใหม่” ที่รอบเงินเดือน — ระบบยกเลิกใบเดิมและออกใหม่"
                    : "แก้เลขที่ผู้ติดต่อ แล้วพิมพ์ใบใหม่",
                true));
        }
        return new Report(rows);
    }

    /// <summary>pure — ตัดสินค่าดิบหนึ่งค่า (null = ไม่มีปัญหา/ว่าง)</summary>
    public static (Issue Issue, string Masked)? Classify(string? raw, string currentKey, IReadOnlyList<string> previousKeys)
    {
        var r = EncryptedColumnConverter.Inspect(raw, currentKey, previousKeys);
        switch (r.State)
        {
            case PiiCipherState.Empty: return null;
            case PiiCipherState.Undecryptable: return (Issue.Undecryptable, "(ถอดรหัสไม่ได้)");
        }
        if (EncryptionHelper.IsEncrypted(r.Plain)) return (Issue.NestedCipher, "(เข้ารหัสซ้อน)");
        var id = EmployeeTaxIdentity.InspectValue(r.Plain);
        if (id.Problem == TaxIdProblem.Invalid) return (Issue.Invalid, id.Masked ?? "");
        if (r.State == PiiCipherState.Previous) return (Issue.PendingRekey, id.Masked ?? "");
        return null;
    }

    private static string ActionFor(Issue issue) => issue switch
    {
        Issue.Undecryptable => $"ตั้ง {EncryptionKeyResolver.PreviousKeyEnv} เป็นคีย์ที่ใช้ตอนบันทึกแล้วรีสตาร์ต — ถ้าไม่มีคีย์เดิมแล้ว ให้กรอกเลขใหม่ที่ทะเบียนพนักงาน",
        Issue.NestedCipher => "กรอกเลขใหม่ที่ทะเบียนพนักงาน",
        Issue.PendingRekey => "อ่านได้แล้ว — ระบบกำลังเข้ารหัสใหม่ด้วยคีย์ปัจจุบัน (ไม่ต้องทำอะไร)",
        _ => "แก้เลขที่ทะเบียนพนักงานให้เป็นเลข 13 หลักที่ถูกต้อง",
    };
}
