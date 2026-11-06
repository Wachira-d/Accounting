using Accounting.Data;
using Accounting.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Storage;

/// <summary>ผลรอบย้ายไฟล์ — ตัวเลขทุกช่องนับจริง (ไม่มีค่าแต่ง)
/// <para><c>Remaining</c> = ยังต้องคัดลอก (รู้แน่) · <c>Unchecked</c> = ยังไม่ได้ตรวจในรอบนี้เพราะครบ limit แล้ว (อาจมีบางไฟล์ย้ายไปแล้ว) ⇒
/// งานที่เหลือ ≤ Remaining + Unchecked · เรียกซ้ำจนทั้งสองเป็น 0 · <c>RemoteNewer</c> = ปลายทางมี object ที่ระบบเขียนเอง (มี hash ใน metadata)
/// แต่ไบต์ต่างจากดิสก์ = ฉบับใหม่กว่าที่เขียนหลังเปลี่ยนเป็น S3 — ไม่ใช่ความล้มเหลว ไม่เขียนทับ</para></summary>
public sealed record FileStorageMigrationReport(
    string Target,
    bool DryRun,
    IReadOnlyList<string> Directories,
    int Scanned,
    int AlreadyPresent,
    int Copied,
    int Remaining,
    int Unchecked,
    int RemoteNewer,
    int Skipped,
    int Failed,
    int Unmappable,
    long CopiedBytes,
    IReadOnlyList<string> Failures,
    int DbRowsChecked,
    int DbRowsUnmappable,
    IReadOnlyList<string> DbUnmappableSamples,
    string Note)
{
    /// <summary>ครบและสะอาด = ไม่มีงานค้าง ไม่มีล้ม ไม่มีพาธ (ไฟล์/แถวในฐาน) ที่แปลงเป็น key ไม่ได้ — เงื่อนไขก่อนปิด LocalReadFallback/ถอด volume</summary>
    public bool Complete => !DryRun && Remaining == 0 && Unchecked == 0 && Failed == 0 && Unmappable == 0 && DbRowsUnmappable == 0;
    public bool Ok => Failed == 0 && Unmappable == 0 && DbRowsUnmappable == 0;
}

/// <summary>
/// **เครื่องมือย้ายไฟล์ครั้งเดียว: ดิสก์ของเครื่องนี้ → object storage** (แอดมินกดเอง · <c>POST api/admin/file-storage/migrate</c>)
///
/// <para>ทีละไฟล์: key จาก <see cref="FileStorageKey"/> ตัวเดียวกับฝั่งเขียน/อ่าน → HEAD ปลายทาง →
/// ไม่มี ⇒ อ่านไฟล์ + SHA-256 → PUT → <b>GET กลับมาเทียบ SHA-256</b> (นับว่าย้ายสำเร็จเฉพาะเมื่อไบต์ตรง) ·
/// มีแล้ว ⇒ เทียบ<b>ขนาด</b>ก่อน (ต่างขนาด = ตัดสินได้โดยไม่ต้องอ่านไฟล์) แล้วค่อย hash เมื่อขนาดเท่ากัน ·
/// ปลายทางที่ระบบเขียนเอง (มี hash ใน metadata) แต่ไบต์ต่าง ⇒ <c>RemoteNewer</c> (ฉบับใหม่หลังสลับเป็น S3 — ไม่เขียนทับ ไม่นับล้ม) ·
/// ปลายทางที่ไม่ได้เขียนผ่านระบบ + ไบต์ต่าง ⇒ ล้ม (ไม่เดาว่าฝั่งไหนถูก)</para>
///
/// <para><b>ไม่ลบไฟล์บนดิสก์เลย</b> — เจ้าของถอด volume เองหลังตรวจรายงาน · ไฟล์หลักฐานบัญชีอยู่ใต้ระยะเก็บ 5 ปี (พ.ร.บ.การบัญชี ม.14 · ป.รัษฎากร §87/3) ·
/// ไม่ย้ายไฟล์ความลับ (ใบรับรอง .pfx/.p12/.pem/.key) ที่บังเอิญอยู่ใต้รากเดียวกัน</para>
///
/// <para>รันซ้ำได้ · ครบ <c>limit</c> ไฟล์แล้วหยุดตรวจ (ไม่อ่าน/hash ไฟล์ที่เหลือ — นับเป็น <c>Unchecked</c>) · ไฟล์ใดอ่าน/ส่งไม่ได้ ⇒ บันทึกเป็นความล้มเหลวแล้วไปต่อ ·
/// กันสองเครื่องรันพร้อมกันด้วย <see cref="JobLock"/> · ตรวจทุกคอลัมน์พาธในฐาน (ไฟล์แนบ · โลโก้/ตราประทับ · โลโก้แบรนด์ · e-Tax · สื่อ CMS รุ่นเก่า)</para>
/// </summary>
public sealed class FileStorageMigrationService
{
    private const int MaxListed = 50;
    /// <summary>นามสกุลความลับ — ไม่ส่งขึ้น object storage แม้อยู่ใต้รากเดียวกัน (ฝ่ายค้าน N7)</summary>
    private static readonly HashSet<string> SecretExtensions = new(StringComparer.OrdinalIgnoreCase) { ".pfx", ".p12", ".pem", ".key" };

    private readonly AccountingDbContext _db;
    private readonly LocalDiskFileStorage _local;
    private readonly IRemoteObjectStore? _remote;
    private readonly ILogger<FileStorageMigrationService> _logger;

    public FileStorageMigrationService(AccountingDbContext db, LocalDiskFileStorage local, ILogger<FileStorageMigrationService> logger,
        IRemoteObjectStore? remote = null)
    {
        _db = db;
        _local = local;
        _remote = remote;
        _logger = logger;
    }

    public bool HasRemoteTarget => _remote != null;
    public string? TargetDescription => _remote?.Describe;

    /// <summary>ตรวจว่าคีย์เข้าถึง bucket ได้ (HEAD object ที่ไม่มี ⇒ ควรได้ 404) · คืนข้อความผล (ไม่โยน) — ใช้บนหน้า status</summary>
    public async Task<string?> ProbeAsync(CancellationToken ct)
    {
        if (_remote == null) return null;
        try
        {
            await _remote.HeadAsync("app/.storage-probe", ct);
            return "ok — เข้าถึง bucket ได้";
        }
        catch (FileStorageException ex) { return "ล้ม — " + ex.Message; }
    }

    /// <summary>null = อีกเครื่องกำลังรันอยู่ · โยน <see cref="InvalidOperationException"/> เมื่อไม่ได้ตั้ง Provider=S3</summary>
    public async Task<FileStorageMigrationReport?> RunAsync(bool dryRun, int limit, string actor, CancellationToken ct)
    {
        var remote = _remote ?? throw new InvalidOperationException(
            "ยังไม่ได้ตั้งที่เก็บปลายทาง — ตั้ง FileStorage:Provider=S3 และค่า FileStorage:S3:* แล้ว deploy ก่อน จึงย้ายไฟล์ได้");
        FileStorageMigrationReport? report = null;
        var ran = await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.FileStorageMigrate, "all", async () =>
        {
            var files = await MigrateAsync(_local, remote, dryRun, limit, ct);
            var (checkedRows, unmappable, samples) = await CheckDbPathsAsync(ct);
            report = WithDb(files, checkedRows, unmappable, samples);
        }, _logger, ct: ct);
        if (!ran) return null;
        _logger.LogInformation(
            "ย้ายไฟล์ไป {Target} โดย {Actor}: dryRun={DryRun} scanned={Scanned} present={Present} copied={Copied} remaining={Remaining} unchecked={Unchecked} remoteNewer={RemoteNewer} failed={Failed} unmappable={Unmappable} dbUnmappable={DbUnmappable}",
            report!.Target, actor, dryRun, report.Scanned, report.AlreadyPresent, report.Copied, report.Remaining, report.Unchecked,
            report.RemoteNewer, report.Failed, report.Unmappable, report.DbRowsUnmappable);
        return report;
    }

    /// <summary>ใส่ผลตรวจฐาน + ประกอบข้อความสรุปจากตัวเลขครบทุกช่อง ("ครบแล้ว" เฉพาะเมื่อ <see cref="FileStorageMigrationReport.Complete"/>)</summary>
    public static FileStorageMigrationReport WithDb(FileStorageMigrationReport files, int dbChecked, int dbUnmappable, IReadOnlyList<string> samples)
    {
        var r = files with { DbRowsChecked = dbChecked, DbRowsUnmappable = dbUnmappable, DbUnmappableSamples = samples };
        return r with { Note = Summarize(r) };
    }

    internal static string Summarize(FileStorageMigrationReport r)
    {
        if (r.DryRun) return $"dry-run — ยังไม่ได้คัดลอก · จะคัดลอก {r.Remaining} ไฟล์";
        if (r.Complete)
            return "ครบแล้ว — ไฟล์บนดิสก์ยังอยู่ (เครื่องมือนี้ไม่ลบ) · ตรวจรายงานแล้วค่อยปิด LocalReadFallback/ถอด volume เอง";
        var parts = new List<string> { $"คัดลอกแล้ว {r.Copied} ไฟล์ในรอบนี้" };
        if (r.Remaining + r.Unchecked > 0) parts.Add($"เหลืออย่างมาก {r.Remaining + r.Unchecked} ไฟล์ — เรียกซ้ำจนเหลือ 0");
        if (r.Failed > 0) parts.Add($"ล้มเหลว {r.Failed} ไฟล์ (ดู failures)");
        if (r.Unmappable > 0) parts.Add($"แปลงเป็น key ไม่ได้ {r.Unmappable} ไฟล์");
        if (r.DbRowsUnmappable > 0) parts.Add($"แถวในฐานที่พาธอยู่นอกราก {r.DbRowsUnmappable} แถว (โหมด S3 จะหาไม่เจอ)");
        parts.Add("ยังไม่ครบ — ห้ามปิด LocalReadFallback/ถอด volume");
        return string.Join(" · ", parts);
    }

    /// <summary>แกนการย้าย (ไม่แตะ DB — เทสต์ได้ด้วยโฟลเดอร์ชั่วคราว + ปลายทางในหน่วยความจำ)</summary>
    public static async Task<FileStorageMigrationReport> MigrateAsync(LocalDiskFileStorage source, IRemoteObjectStore target,
        bool dryRun, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 5000);
        var dirs = source.Locator.MigrationDirectories();
        int scanned = 0, present = 0, copied = 0, remaining = 0, unchecked_ = 0, remoteNewer = 0, skipped = 0, failed = 0, unmappable = 0;
        long copiedBytes = 0;
        var failures = new List<string>();
        void Note(string msg) { if (failures.Count < MaxListed) failures.Add(msg); }
        void Fail(string msg) { failed++; Note(msg); }

        foreach (var dir in dirs)
        {
            foreach (var file in source.EnumerateFiles(dir))
            {
                ct.ThrowIfCancellationRequested();
                scanned++;
                // ครบ limit แล้ว (ย้ายจริง) ⇒ ไม่อ่าน/hash/HEAD ไฟล์ที่เหลือ — แค่นับ (ฝ่ายค้าน S1)
                if (!dryRun && copied >= limit) { unchecked_++; continue; }
                if (SecretExtensions.Contains(Path.GetExtension(file))) { skipped++; continue; }
                var key = source.Locator.ToKey(file);
                if (key == null)
                {
                    unmappable++;
                    Note($"แปลงเป็น key ไม่ได้ (ชื่อไฟล์/พาธไม่ปลอดภัย): {file}");
                    continue;
                }
                try
                {
                    var head = await target.HeadAsync(key, ct);
                    if (head == null)
                    {
                        if (dryRun) { remaining++; continue; }
                        var bytes = await source.ReadAllBytesAsync(file, ct);
                        if (bytes == null) { Fail($"อ่านไฟล์บนดิสก์ไม่ได้ (หายระหว่างรอบ): {key}"); continue; }
                        var sha = S3SigV4.Sha256Hex(bytes);
                        await target.PutAsync(key, bytes, null, ct);
                        var back = await target.GetAsync(key, ct);
                        if (back == null) { Fail($"PUT {key} สำเร็จแต่อ่านกลับไม่พบ"); continue; }
                        if (S3SigV4.Sha256Hex(back) != sha) { Fail($"PUT {key} แล้วอ่านกลับได้ไบต์ไม่ตรง (SHA-256 ต่าง)"); continue; }
                        copied++;
                        copiedBytes += bytes.LongLength;
                        continue;
                    }

                    // ปลายทางมีแล้ว — ขนาดต่าง ⇒ ตัดสินได้เลยไม่ต้องอ่านไฟล์
                    var size = source.Length(file);
                    if (size == null) { Fail($"อ่านไฟล์บนดิสก์ไม่ได้ (หายระหว่างรอบ): {key}"); continue; }
                    if (head.Size != size.Value)
                    {
                        if (head.Sha256 != null) { remoteNewer++; Note($"ปลายทางมีฉบับที่ระบบเขียนหลังสลับเป็น S3 (ขนาดต่าง): {key} — ไม่เขียนทับ"); }
                        else Fail($"ปลายทางมี {key} อยู่แล้วแต่ขนาดไม่ตรงกับดิสก์ — ไม่เขียนทับ (ตรวจด้วยมือ)");
                        continue;
                    }
                    var local = await source.ReadAllBytesAsync(file, ct);
                    if (local == null) { Fail($"อ่านไฟล์บนดิสก์ไม่ได้ (หายระหว่างรอบ): {key}"); continue; }
                    var localSha = S3SigV4.Sha256Hex(local);
                    if (head.Sha256 != null)
                    {
                        if (string.Equals(head.Sha256, localSha, StringComparison.OrdinalIgnoreCase)) present++;
                        else { remoteNewer++; Note($"ปลายทางมีฉบับที่ระบบเขียนหลังสลับเป็น S3: {key} — ไม่เขียนทับ"); }
                        continue;
                    }
                    // object ที่ไม่มี hash ใน metadata (เขียนนอกระบบ) — ดาวน์โหลดเทียบไบต์ก่อนตัดสิน
                    if (await target.GetAsync(key, ct) is { } existing && S3SigV4.Sha256Hex(existing) == localSha) { present++; continue; }
                    Fail($"ปลายทางมี {key} อยู่แล้วแต่เนื้อไฟล์ไม่ตรงกับดิสก์ — ไม่เขียนทับ (ตรวจด้วยมือ)");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // FileStorageException (ปลายทาง) · IOException/สิทธิ์ (ดิสก์) — บันทึกแล้วไปไฟล์ถัดไป (ฝ่ายค้าน S1)
                    Fail($"{key}: {ex.Message}");
                }
            }
        }

        var r = new FileStorageMigrationReport(target.Describe, dryRun, dirs, scanned, present, copied, remaining, unchecked_, remoteNewer,
            skipped, failed, unmappable, copiedBytes, failures, 0, 0, Array.Empty<string>(), "");
        return r with { Note = Summarize(r) };
    }

    /// <summary>ทุกคอลัมน์ที่เก็บพาธไฟล์บนดิสก์ (ทุกบริษัท · รวม soft-delete) — แถวที่พาธแปลงเป็น key ไม่ได้ = แถวที่โหมด S3 จะหาไฟล์ไม่เจอตลอดไป
    /// (ฝ่ายค้าน S3) · URL สัมพัทธ์ (<c>/uploads/…</c> ของ LogoUrl) ไม่ใช่พาธดิสก์ — ไม่ตรวจ</summary>
    private async Task<(int Checked, int Unmappable, List<string> Samples)> CheckDbPathsAsync(CancellationToken ct)
    {
        // งานของแอดมินแพลตฟอร์ม (ไม่ใช่คำขอของผู้เช่า) — อ่านแค่พาธ ไม่อ่านเนื้อไฟล์ · IgnoreQueryFilters รวมแถวที่ถอดจากรายการแต่ต้องเก็บไฟล์ (พ.ร.บ.การบัญชี ม.14)
        var rows = new List<(string Source, Guid CompanyId, Guid Id, string? Path)>();
        rows.AddRange((await _db.FileAttachments.AsNoTracking().IgnoreQueryFilters()
            .Select(f => new { f.CompanyId, f.Id, f.StoragePath }).ToListAsync(ct))
            .Select(x => ("FileAttachments.StoragePath", x.CompanyId, x.Id, (string?)x.StoragePath)));
        foreach (var x in await _db.CompanySettings.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.LogoPath != null || s.StampPath != null)
            .Select(s => new { s.CompanyId, s.Id, s.LogoPath, s.StampPath }).ToListAsync(ct))
        {
            rows.Add(("CompanySettings.LogoPath", x.CompanyId, x.Id, x.LogoPath));
            rows.Add(("CompanySettings.StampPath", x.CompanyId, x.Id, x.StampPath));
        }
        rows.AddRange((await _db.DocumentBrands.AsNoTracking().IgnoreQueryFilters()
            .Where(b => b.LogoPath != null).Select(b => new { b.CompanyId, b.Id, b.LogoPath }).ToListAsync(ct))
            .Select(x => ("DocumentBrands.LogoPath", x.CompanyId, x.Id, x.LogoPath)));
        foreach (var x in await _db.EtaxInvoices.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.PdfFilePath != null || e.XmlFilePath != null)
            .Select(e => new { e.CompanyId, e.Id, e.PdfFilePath, e.XmlFilePath }).ToListAsync(ct))
        {
            rows.Add(("EtaxInvoices.PdfFilePath", x.CompanyId, x.Id, x.PdfFilePath));
            rows.Add(("EtaxInvoices.XmlFilePath", x.CompanyId, x.Id, x.XmlFilePath));
        }
        foreach (var x in await _db.SiteMediaItems.AsNoTracking().IgnoreQueryFilters()
            .Where(m => m.AttachmentId == null)
            .Select(m => new { m.CompanyId, m.Id, m.StoragePath, m.WebPPath, m.ThumbnailPath }).ToListAsync(ct))
        {
            rows.Add(("SiteMedia.StoragePath", x.CompanyId, x.Id, x.StoragePath));
            rows.Add(("SiteMedia.WebPPath", x.CompanyId, x.Id, x.WebPPath));
            rows.Add(("SiteMedia.ThumbnailPath", x.CompanyId, x.Id, x.ThumbnailPath));
        }
        var withPath = rows.Where(r => !string.IsNullOrWhiteSpace(r.Path)).ToList();
        var bad = withPath.Where(r => _local.Locator.ToKey(r.Path) == null).ToList();
        return (withPath.Count, bad.Count,
            bad.Take(MaxListed).Select(r => $"{r.Source} company={r.CompanyId} id={r.Id} path={r.Path}").ToList());
    }
}
