using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 274 — <b>รายงานอ่านอย่างเดียว</b> "เลขประจำตัวที่ต้องแก้" (หลังเหตุ ENCRYPTION_KEY ถูกเปลี่ยน · F2 ข้อ 9: ของเสียที่ persist แล้วต้องมีทางเห็น)
///
/// <para>สองแหล่ง:
/// <list type="number">
/// <item><b>ทะเบียนพนักงาน</b> (<c>CitizenId</c>/<c>TaxId</c>) — อ่านค่าดิบข้าม converter แล้วตัดสินด้วย <see cref="EncryptedColumnConverter.Inspect"/> ตัวเดียวกับฝั่งอ่าน:
///   ถอดไม่ออกทุกคีย์ (กู้ไม่ได้ ⇒ ตั้ง <c>ENCRYPTION_KEY_PREVIOUS</c> หรือกรอกใหม่) · เข้ารหัสซ้อนที่แกะไม่ได้ (รอบ 274-10: ซ้อนที่แกะได้ = รอย้ายคีย์) · อยู่บนคีย์เดิม (อ่านได้แล้ว รองานย้ายคีย์) ·
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
        /// <summary>ถอดแล้วได้ ciphertext อีกชั้น (เคยถูกห่อซ้อน) และ<b>แกะไม่ได้</b> — ชั้นในถอดไม่ออก/ไม่ใช่ค่าที่ใช้ได้ (เลข 13 หลักผิด)/
        /// เป็น ciphertext ที่ผู้ใช้ส่งมาเอง (ไม่แกะ — ปิด oracle รอบ 231) ⇒ กรอกใหม่ · รอบ 274-10: ซ้อนที่แกะได้ = <see cref="PendingRekey"/></summary>
        NestedCipher,
        /// <summary>ถอดได้ด้วยคีย์เดิม หรือเข้ารหัสซ้อนที่แกะได้ (รอบ 274-10) — อ่านถูกแล้ว · งานย้ายคีย์จะเข้ารหัสใหม่เป็นชั้นเดียว (ไม่ต้องทำอะไร)</summary>
        PendingRekey,
        /// <summary>ค่าที่ถอดแล้ว/ผู้รับใน 50 ทวิ ไม่ใช่เลข 13 หลักที่ถูกต้อง</summary>
        Invalid,
    }

    public sealed record Row(string Source, string Ref, string? Name, string Field, Issue Issue, string Masked, string Action, bool BlocksFiling);

    /// <param name="RekeyJob">ผลรันล่าสุดของ <c>PiiReprotectJob</c> จาก JobRunLog (ฝ่ายค้าน MED-2: ข้อความ "ระบบกำลังเข้ารหัสใหม่" ต้องมาจากผลจริง) ·
    /// null = ยังไม่เคยรัน</param>
    public sealed record Report(IReadOnlyList<Row> Rows, RekeyJobStatus? RekeyJob = null)
    {
        public int Blocking => Rows.Count(r => r.BlocksFiling);
    }

    /// <summary>ผลรันล่าสุดของงานย้ายคีย์ (เวลา UTC)</summary>
    public sealed record RekeyJobStatus(DateTime FinishedAt, bool Success, string? Message);

    /// <summary>ป้ายช่อง (คอลัมน์ของทะเบียนพนักงาน) — คอลัมน์อื่นใช้ "ตาราง.ช่อง"</summary>
    private static readonly Dictionary<string, string> EmployeeFieldLabels = new(StringComparer.Ordinal)
    {
        [nameof(Employee.CitizenId)] = "เลขบัตรประชาชน",
        [nameof(Employee.TaxId)] = "เลขประจำตัวผู้เสียภาษี",
        [nameof(Employee.BankAccountNumber)] = "เลขบัญชีธนาคาร",
        [nameof(Employee.SocialSecurityNumber)] = "เลขประกันสังคม",
        [nameof(Employee.PassportNumber)] = "เลขหนังสือเดินทาง",
    };

    /// <summary>คอลัมน์ที่เป็นเลขประจำตัวผู้เสียภาษี (ตรวจ 13 หลัก + mod-11 และกันการยื่นแบบ)</summary>
    private static bool IsTaxIdColumn(string property) => property is nameof(Employee.CitizenId) or nameof(Employee.TaxId);

    public static async Task<Report> BuildAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default,
        string? currentKey = null, IReadOnlyList<string>? previousKeys = null)
    {
        var current = currentKey ?? EncryptedColumnConverter.Keys.Current;
        var previous = previousKeys ?? EncryptedColumnConverter.Keys.Previous;
        var rows = new List<Row>();

        var job = await db.Set<JobRunLog>().AsNoTracking()
            .Where(j => j.JobName == Accounting.Services.Background.PiiReprotectJob.JobName)
            .OrderByDescending(j => j.FinishedAt)
            .Select(j => new RekeyJobStatus(j.FinishedAt ?? j.StartedAt, j.Success, j.Message))
            .FirstOrDefaultAsync(ct);

        // ── 1. ทุกคอลัมน์ PII ที่ใช้ converter (ทะเบียนเดียวกับงานย้ายคีย์ — ฝ่ายค้าน MED-1) — ค่าดิบข้าม converter ──
        var empTable = db.Model.FindEntityType(typeof(Employee))!;
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere) await conn.OpenAsync(ct);
        try
        {
            foreach (var col in Accounting.Services.Background.PiiReprotectPass.Columns(db).Where(c => c.HasCompanyId))
            {
                var isEmp = col.Table == empTable.GetTableName();
                string Col(string prop) => empTable.FindProperty(prop)!.GetColumnName();
                await using var cmd = conn.CreateCommand();
                var refCols = isEmp
                    ? $"\"{Col(nameof(Employee.EmployeeCode))}\", \"{Col(nameof(Employee.FirstNameTh))}\", \"{Col(nameof(Employee.LastNameTh))}\""
                    : "\"Id\"::text, NULL, NULL";
                // เลขประจำตัวผู้เสียภาษี: ตรวจทุกแถว (ค่าที่ถอดได้แต่ผิดรูปด้วย) · คอลัมน์อื่น: เฉพาะแถวที่อาจเป็น ciphertext
                var filter = IsTaxIdColumn(col.Property) && isEmp
                    ? $"\"{col.Column}\" IS NOT NULL" : Accounting.Services.Background.PiiReprotectPass.CandidateSql(col.Column);
                cmd.CommandText = $"SELECT {refCols}, \"{col.Column}\" FROM \"{col.Table}\" WHERE \"CompanyId\" = @cid AND {filter}"
                    + (isEmp ? " AND \"IsDeleted\" = false" : "") + " ORDER BY 1 LIMIT 2000";
                var p = cmd.CreateParameter(); p.ParameterName = "cid"; p.Value = companyId; cmd.Parameters.Add(p);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    var code = rd.IsDBNull(0) ? "" : rd.GetString(0);
                    var name = $"{(rd.IsDBNull(1) ? "" : rd.GetString(1))} {(rd.IsDBNull(2) ? "" : rd.GetString(2))}".Trim();
                    var raw = rd.IsDBNull(3) ? null : rd.GetString(3);
                    var taxCol = isEmp && IsTaxIdColumn(col.Property);
                    var field = isEmp && EmployeeFieldLabels.TryGetValue(col.Property, out var lbl) ? lbl : $"{col.Table}.{col.Property}";
                    if (Classify(raw, current, previous, taxCol, col.Kind) is (Issue issue, string masked))
                        rows.Add(new Row(isEmp ? "พนักงาน" : col.Table, code, isEmp ? name : null, field, issue, masked,
                            ActionFor(issue, job), taxCol && issue != Issue.PendingRekey));
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
        return new Report(rows, job);
    }

    /// <summary>pure — ตัดสินค่าดิบหนึ่งค่า (null = ไม่มีปัญหา/ว่าง)</summary>
    /// <param name="taxIdColumn">false = คอลัมน์ที่ไม่ใช่เลขผู้เสียภาษี (เลขบัญชี/ประกันสังคม/พาสปอร์ต/อื่น) — ไม่ตรวจ 13 หลัก · ไม่แสดงค่า</param>
    /// <param name="kind">ชนิดค่าของคอลัมน์ (จาก converter ของโมเดล) · null = ตาม <paramref name="taxIdColumn"/> — ใช้ตัดสินค่าที่แกะจากชั้นซ้อน</param>
    public static (Issue Issue, string Masked)? Classify(string? raw, string currentKey, IReadOnlyList<string> previousKeys, bool taxIdColumn = true,
        PiiValueKind? kind = null)
    {
        var r = EncryptedColumnConverter.Inspect(raw, currentKey, previousKeys,
            kind ?? (taxIdColumn ? PiiValueKind.ThaiTaxId : PiiValueKind.Text));
        switch (r.State)
        {
            case PiiCipherState.Empty: return null;
            case PiiCipherState.Undecryptable: return (Issue.Undecryptable, "(ถอดรหัสไม่ได้)");
        }
        // รอบ 274-10: ซ้อนที่แกะไม่ได้เท่านั้นที่ต้องกรอกใหม่ (ตัวตัดสินเดียว = Inspect) · ซ้อนที่แกะได้ไหลต่อเป็น "รอย้ายคีย์"
        if (r.NestedUnresolved || EncryptedColumnConverter.IsUnreadable(r.Plain)) return (Issue.NestedCipher, "(เข้ารหัสซ้อน)");
        var pending = r.State is PiiCipherState.Previous or PiiCipherState.Nested;
        if (!taxIdColumn)
            return pending ? (Issue.PendingRekey, r.State == PiiCipherState.Nested ? "(เข้ารหัสซ้อน — อ่านได้แล้ว)" : "(อ่านได้ด้วยคีย์เดิม)") : null;
        var id = EmployeeTaxIdentity.InspectValue(r.Plain);
        if (id.Problem == TaxIdProblem.Invalid) return (Issue.Invalid, id.Masked ?? "");
        if (pending) return (Issue.PendingRekey, id.Masked ?? "");
        return null;
    }

    private static string ActionFor(Issue issue, RekeyJobStatus? job) => issue switch
    {
        Issue.Undecryptable => "แจ้งผู้ดูแลระบบให้ตั้งคีย์เดิมที่ใช้ตอนบันทึก (ข้อมูลกลับมาเองหลังรีสตาร์ต) — ถ้าไม่มีคีย์เดิมแล้ว ให้กรอกค่าใหม่ที่ทะเบียน",   // รอบ 275 แพ็ก A (M-1)
        Issue.NestedCipher => "กรอกค่าใหม่ที่ทะเบียน",
        Issue.PendingRekey => RekeyAction(job),
        _ => "แก้เลขที่ทะเบียนพนักงานให้เป็นเลข 13 หลักที่ถูกต้อง",
    };

    /// <summary>ฝ่ายค้าน MED-2: ทางไปต่อของแถว "อยู่บนคีย์เดิม" ตามผลรันจริงของงานย้ายคีย์ — ไม่ประทับ "ไม่ต้องทำอะไร" เอง</summary>
    public static string RekeyAction(RekeyJobStatus? job)
    {
        var interval = Accounting.Services.Background.PiiReprotectJob.Interval.TotalHours;
        if (job == null)
            return $"อ่านได้ด้วยคีย์เดิม — งานย้ายคีย์ยังไม่เคยรัน (รันหลังบูต 3 นาที แล้วทุก {interval:0} ชั่วโมง) · ถ้าผ่านไปนานแล้วยังขึ้นแถวนี้ แจ้งผู้ดูแลระบบ";
        var at = job.FinishedAt.AddHours(7).ToString("dd/MM/yyyy HH:mm");
        return job.Success
            ? $"อ่านได้ด้วยคีย์เดิม — งานย้ายคีย์รันล่าสุด {at} สำเร็จ · แถวนี้จะถูกเข้ารหัสใหม่ในรอบถัดไป (ทุก {interval:0} ชั่วโมง)"
            : $"⛔ อ่านได้ด้วยคีย์เดิม แต่งานย้ายคีย์ล้มล่าสุด {at}: {job.Message} — แจ้งผู้ดูแลระบบ (ห้ามลบคีย์เดิมจนกว่างานจะสำเร็จ)";
    }
}
