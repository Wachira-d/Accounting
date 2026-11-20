using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace Accounting.Services.Background;

/// <summary>
/// ฝ่ายค้านรอบ 219 B2 — <b>เข้ารหัสความลับใหม่ด้วยคีย์ปัจจุบัน</b> หลังเปลี่ยนแหล่งคีย์ (<see cref="EncryptionKeyResolver"/>)
///
/// <para>ค่าที่เข้ารหัสด้วยคีย์เดิม (config เดิม/dev key) ยังถอดได้ผ่านคีย์เดิมของ <see cref="SecretProtector"/> (ไม่มีอะไรพังทันที) ·
/// งานนี้ย้ายค่าเหล่านั้นไปคีย์ปัจจุบัน<b>ครั้งเดียวต่อการบูต</b> (ล็อก <see cref="JobLock"/> — หลาย instance ไม่ทำซ้ำ) แล้วบันทึกจำนวน +
/// audit chain ต่อแถว (ชื่อช่องเท่านั้น ไม่มีค่า) · ค่าที่ถอดไม่ออกทุกคีย์ <b>ไม่ถูกแตะ</b> (คงอยู่ · <c>IsUsable=false</c> ⇒ ผู้ใช้กรอกใหม่) ·
/// รอบ 231: ย้ายค่าที่<b>ไม่มีคำนำหน้า <c>enc1:</c></b> ด้วย — ciphertext รุ่นก่อน (ถอดได้ด้วยคีย์ปัจจุบัน/คีย์เดิม) และ plaintext เก่า (รวม token ที่รุ่นก่อนเก็บดิบเพราะเดารูปผิด)
/// ⇒ ทุกค่าในทะเบียนไปอยู่รูปแบบมีคำนำหน้า แล้วการ "เดารูป" (<c>EncryptionHelper.LooksLikeLegacyCiphertext</c>) ไม่ต้องถูกใช้กับแถวนั้นอีก ·
/// รอบ 2: อ่านทีละชุด + เขียนแบบมีเงื่อนไขค่าเดิม (ไม่ทับค่าที่ผู้ใช้เพิ่งหมุน)</para>
///
/// <para><b>ทะเบียนช่องความลับ (ตัวเดียว):</b> ทุกช่องที่เขียนผ่าน <c>ISecretProtector.Protect</c> — เพิ่มช่องใหม่ต้องเพิ่มที่นี่
/// (ล็อกด้วย <c>tools/secret_registry_check.py</c> — เทียบทุกจุดที่เขียน <c>.Protect(</c>/<c>EncryptionHelper.Encrypt(</c> กับทะเบียนนี้)</para>
/// </summary>
public static class SecretReprotectPass
{
    /// <summary>ฝ่ายค้านรอบ 219 รอบ 2: อ่านทีละชุด (keyset ตาม Id) — ไม่โหลดทั้งตารางเข้าหน่วยความจำ</summary>
    public const int BatchSize = 200;

    /// <param name="SkippedConcurrent">ค่าที่ถูกผู้ใช้เปลี่ยนระหว่างอ่าน→เขียน (เงื่อนไข "ค่าเดิม" ไม่ตรง) ⇒ ไม่เขียนทับ</param>
    /// <param name="PendingWrap">ฝ่ายค้านรอบ 231 B1: ค่าไม่มีคำนำหน้า (LegacyFormat/Plaintext/AmbiguousBase64) ที่ยังไม่ถูกห่อเพราะ <c>Security:MigrateCipherPrefix</c> ปิด — อ่านได้ครบ ไม่รีบ</param>
    /// <param name="WrappedAmbiguous">S1: ค่ากำกวม (base64 ถอดไม่ออก) ที่ถูกห่อเป็น enc1 (ประตูเปิด) — ถ้าจริง ๆ เป็น ciphertext คีย์ที่หายไป การห่อทำให้เห็นชัดว่าต้องกรอกใหม่ · นับแยก + LogWarning</param>
    /// <param name="Unusable">รอบ 275 แพ็ก A: ค่าที่ถอดไม่ออกทุกคีย์ (WrongKey) หรือเสียหาย (Malformed) — ไม่แตะ · ผู้ใช้ต้องกรอกใหม่ (หน้า "สุขภาพข้อมูลเข้ารหัส")</param>
    /// <param name="LegacyKeyRemaining">รอบ 275 แพ็ก A (T4 M-4 watermark): ค่าที่ยังอยู่บนคีย์เดิมหลังรอบนี้ — 0 (และฝั่ง PII ก็ 0) ⇒ ถอด ENCRYPTION_KEY_PREVIOUS ออกได้</param>
    /// <param name="States">จำนวนค่าต่อสถานะต่อช่อง (<c>ตาราง.คอลัมน์</c>) — หน้ารายงานอ่านจากตัวนี้</param>
    /// <param name="Findings">แถวที่ต้องให้คนดู (ใช้ไม่ได้ · กำกวม) — สูงสุด <see cref="MaxFindings"/> แถว</param>
    public sealed record Result(int Scanned, int Reprotected, IReadOnlyDictionary<string, int> PerColumn, int SkippedConcurrent = 0,
        int PendingWrap = 0, int WrappedAmbiguous = 0, int Unusable = 0, int LegacyKeyRemaining = 0,
        IReadOnlyDictionary<string, IReadOnlyDictionary<SecretState, int>>? States = null, IReadOnlyList<Finding>? Findings = null);

    /// <summary>รอบ 275 แพ็ก A: แถวความลับหนึ่งค่าที่ต้องให้คนดู (ไม่มีค่าความลับ — แค่ที่อยู่ + สถานะ)</summary>
    public sealed record Finding(string Table, string Column, string Label, Guid RowId, Guid? CompanyId, SecretState State);

    public const int MaxFindings = 500;

    /// <summary>รอบ 275 แพ็ก A (M-1): เหตุของแถว (ตรวจจริงจากสถานะ — F2 ข้อ 7) · null = ไม่มีปัญหา</summary>
    public static string? ReasonFor(SecretState state) => SecretWarnings.ReasonFor(state) ?? SecretWarnings.SoftHintFor(state);

    /// <summary>รอบ 275 แพ็ก A (M-1): ทางไปต่อของผู้ใช้บริษัทต่อช่อง (ไม่มีชื่อ env/URL ดิบ) — server computes, page displays</summary>
    public static string ActionFor(string table, string column, SecretState state) => (table, column) switch
    {
        _ when state == SecretState.AmbiguousBase64 => "ใช้งานได้ตามเดิม — ถ้าการเชื่อมต่อนี้ใช้ไม่ติด ให้กรอกค่าใหม่ในหน้าตั้งค่า",
        ("Documents", _) => "ไม่ต้องทำอะไร — ระบบออกลิงก์ชำระเงินใหม่ให้เองเมื่อส่งเอกสารครั้งถัดไป",
        ("ContactNotifyEndpoints", _) => "ให้ผู้รับผูก LINE ใหม่ (ถ้าผู้ดูแลระบบยืนยันว่าคีย์เข้ารหัสถูกต้องแล้ว)",
        ("LineGroupBindCodes", _) => "ไม่ต้องทำอะไร — รหัสผูกกลุ่มหมดอายุเอง",
        ("SiteSettings", _) => "ผู้ดูแลแพลตฟอร์มกรอกค่าใหม่ในหน้าผู้ดูแลระบบ",
        _ => "กรอกค่าใหม่ในหน้าตั้งค่า หรือแจ้งผู้ดูแลระบบให้ตรวจคีย์เข้ารหัส",
    };

    /// <summary>รอบ 275 แพ็ก A (M-1): ชื่อช่องที่ผู้ใช้เข้าใจ — ตัวเดียวของหน้า "สุขภาพข้อมูลเข้ารหัส" (server computes) · ไม่มีในตาราง = ชื่อเทคนิค</summary>
    public static string Label(string table, string column) => Labels.TryGetValue($"{table}.{column}", out var l) ? l : $"{table}.{column}";

    private static readonly Dictionary<string, string> Labels = new()
    {
        ["CompanySettings.EmailSmtpPassword"] = "รหัสผ่าน SMTP (อีเมลของบริษัท)",
        ["CompanySettings.EmailMsClientSecret"] = "Client Secret ของ Microsoft 365 (อีเมลของบริษัท)",
        ["CompanySettings.EmailGmailClientSecret"] = "Client Secret ของ Gmail (อีเมลของบริษัท)",
        ["CompanySettings.EmailGmailRefreshToken"] = "Refresh Token ของ Gmail (อีเมลของบริษัท)",
        ["CompanySettings.LineChannelAccessToken"] = "Channel Access Token ของ LINE OA บริษัท",
        ["CompanySettings.LineChannelSecret"] = "Channel Secret ของ LINE OA บริษัท",
        ["CompanySettings.EtaxCertificatePassword"] = "รหัสผ่านใบรับรอง e-Tax",
        ["CompanySettings.EtaxRdApiSecret"] = "API Secret ของกรมสรรพากร (e-Tax)",
        ["CompanySettings.SignCallbackSecret"] = "รหัสลับลงนาม webhook ลิงก์ให้ลูกค้าเซ็น",
        ["SiteSettings.SystemSmtpPassword"] = "รหัสผ่าน SMTP ของระบบ",
        ["SiteSettings.SystemMsClientSecret"] = "Client Secret ของ Microsoft 365 (อีเมลระบบ)",
        ["SiteSettings.SystemGmailClientSecret"] = "Client Secret ของ Gmail (อีเมลระบบ)",
        ["SiteSettings.SystemGmailRefreshToken"] = "Refresh Token ของ Gmail (อีเมลระบบ)",
        ["SiteSettings.LineMessagingChannelAccessToken"] = "Channel Access Token ของ LINE OA กลาง",
        ["SiteSettings.LineMessagingChannelSecret"] = "Channel Secret ของ LINE OA กลาง",
        ["SiteSettings.GoogleClientSecret"] = "Google Client Secret (เข้าสู่ระบบ)",
        ["SiteSettings.FacebookAppSecret"] = "Facebook App Secret (เข้าสู่ระบบ)",
        ["SiteSettings.LineLoginChannelSecret"] = "LINE Login Channel Secret (เข้าสู่ระบบ)",
        ["PaymentProviderConfigs.TestSecretKeyProtected"] = "คีย์ลับโหมดทดสอบของช่องทางรับชำระ",
        ["PaymentProviderConfigs.LiveSecretKeyProtected"] = "คีย์ลับโหมดใช้งานจริงของช่องทางรับชำระ",
        ["BankConnections.EncryptedCredentials"] = "ข้อมูลเข้าสู่ระบบธนาคาร (bank feed)",
        ["BankConnections.AccessToken"] = "Access Token ของธนาคาร (bank feed)",
        ["WebhookRegistrations.Secret"] = "รหัสลับลงนาม webhook ขาออก",
        ["ExternalIntegrations.SecretKey"] = "Secret Key ของการเชื่อมต่อภายนอก",
        ["SitePaymentGateways.ApiKeyEncrypted"] = "API Key ของช่องทางชำระบนเว็บไซต์",
        ["SitePaymentGateways.SecretKeyEncrypted"] = "Secret Key ของช่องทางชำระบนเว็บไซต์",
        ["SitePaymentGateways.WebhookSecret"] = "Webhook Secret ของช่องทางชำระบนเว็บไซต์",
        ["Documents.PayLinkTokenProtected"] = "ลิงก์ชำระเงินของเอกสาร (ระบบออกใหม่ให้อัตโนมัติ)",
        ["ContactNotifyEndpoints.LineUserId"] = "LINE ของผู้ติดต่อ (ผู้รับต้องผูกใหม่)",
        ["ContactNotifyEndpoints.LineGroupId"] = "กลุ่ม LINE ของผู้ติดต่อ (ต้องผูกกลุ่มใหม่)",
        ["LineGroupBindCodes.GroupIdProtected"] = "รหัสผูกกลุ่ม LINE (หมดอายุเอง)",
    };

    /// <summary>ช่องความลับหนึ่งช่อง — ชื่อคอลัมน์มาจากนิพจน์ (<c>r =&gt; r.Col</c>) ตัวเดียวกับที่ใช้อ่าน ⇒ ชื่อพิมพ์ผิดไม่ได้</summary>
    public sealed record Column<T>(string Name, Func<T, string?> Get);

    public static Column<T> Col<T>(Expression<Func<T, string?>> e)
        => new(((MemberExpression)e.Body).Member.Name, e.Compile());

    /// <param name="onlyCompanyId">จำกัดเฉพาะแถวของบริษัทนี้ (ซ่อมรายบริษัท/เทสต์บนฐานร่วม) · null = ทุกแถว รวมค่าตั้งแพลตฟอร์ม</param>
    /// <param name="beforeWrite">จุดสอดของเทสต์ — เรียกหลังอ่านค่า ก่อนเขียนแบบมีเงื่อนไข (จำลองผู้ใช้หมุนคีย์พร้อมกัน) · production = null</param>
    /// <param name="logger">LogWarning ต่อค่ากำกวมที่ถูกห่อ (S1) · null = ไม่ log</param>
    /// <param name="inspectOnly">รอบ 275 แพ็ก A: นับสถานะอย่างเดียว ไม่เขียน (หน้า "สุขภาพข้อมูลเข้ารหัส" — ทะเบียนเดียวกับงานย้าย ไม่มีสำเนาที่สอง)</param>
    public static async Task<Result> RunAsync(AccountingDbContext db, ISecretProtector protector, CancellationToken ct = default,
        Guid? onlyCompanyId = null, Func<Guid, Task>? beforeWrite = null, ILogger? logger = null, bool inspectOnly = false)
    {
        var per = new Dictionary<string, int>();
        var scanned = 0;
        var skipped = 0;
        var pendingWrap = 0;
        var wrappedAmbiguous = 0;
        var unusable = 0;
        var legacyRemaining = 0;
        var states = new Dictionary<string, Dictionary<SecretState, int>>();
        var findings = new List<Finding>();

        IQueryable<T> Scope<T>(IQueryable<T> q) where T : TenantEntity
            => onlyCompanyId is Guid c ? q.Where(r => r.CompanyId == c) : q;

        // ฝ่ายค้านรอบ 219 รอบ 2 (ข้อ 1 · lost update): เดิมโหลดทุกแถวแล้ว SaveChanges ครั้งเดียวท้ายสุด ไม่มี concurrency token ⇒
        // ผู้ใช้หมุนคีย์ (บันทึกค่าใหม่) ระหว่างงานวิ่ง ⇒ งานเขียนค่าเก่าที่เข้ารหัสใหม่ทับ = ความลับใหม่หาย ·
        // ตอนนี้: อ่านแบบ AsNoTracking ทีละชุด · เขียนทีละค่าด้วย UPDATE ... WHERE Id = @id AND Col = @ciphertextเดิม ⇒ ค่าเปลี่ยนไปแล้ว = 0 แถว = ไม่แตะ
        async Task Pass<T>(IQueryable<T> src, string table, Func<T, Guid?> company, params Column<T>[] cols) where T : BaseEntity
        {
            Guid? last = null;
            while (true)
            {
                var page = src.AsNoTracking();
                if (last is Guid l) page = page.Where(r => r.Id.CompareTo(l) > 0);
                var batch = await page.OrderBy(r => r.Id).Take(BatchSize).ToListAsync(ct);
                if (batch.Count == 0) break;
                last = batch[^1].Id;

                var audits = 0;
                foreach (var row in batch)
                {
                    var changed = new List<string>();
                    foreach (var c in cols)
                    {
                        var v = c.Get(row);
                        if (string.IsNullOrEmpty(v)) continue;
                        scanned++;
                        var state = protector.Inspect(v);
                        var key = $"{table}.{c.Name}";
                        if (!states.TryGetValue(key, out var byState)) states[key] = byState = new Dictionary<SecretState, int>();
                        byState[state] = byState.GetValueOrDefault(state) + 1;
                        if (state is SecretState.WrongKey or SecretState.Malformed or SecretState.AmbiguousBase64 && findings.Count < MaxFindings)
                            findings.Add(new Finding(table, c.Name, Label(table, c.Name), row.Id, company(row), state));
                        if (state is SecretState.WrongKey or SecretState.Malformed) unusable++;
                        if (inspectOnly)
                        {
                            if (state == SecretState.LegacyKey) legacyRemaining++;
                            continue;
                        }
                        if (protector.Reprotect(v) is not string nv)
                        {
                            if (state == SecretState.LegacyKey) legacyRemaining++;
                            // B1: ไม่มีคำนำหน้าแต่ประตูปิด ⇒ รอห่อ (สรุปใน JobRun ให้ผู้ดูแลเห็นว่ามีกี่ค่า)
                            if (state is SecretState.LegacyFormat or SecretState.Plaintext or SecretState.AmbiguousBase64) pendingWrap++;
                            continue;
                        }
                        if (beforeWrite != null) await beforeWrite(row.Id);
                        var id = row.Id;
                        var name = c.Name;
                        var n = await src
                            .Where(r => r.Id == id && EF.Property<string?>(r, name) == v)
                            .ExecuteUpdateAsync(s => s.SetProperty(r => EF.Property<string?>(r, name), nv), ct);
                        if (n == 1)
                        {
                            changed.Add(name);
                            per[$"{table}.{name}"] = per.GetValueOrDefault($"{table}.{name}") + 1;
                            if (state == SecretState.AmbiguousBase64)
                            {
                                wrappedAmbiguous++;
                                logger?.LogWarning("SecretReprotect: ห่อค่ากำกวม {Table}.{Column} (Id {Id}) — base64 ที่ถอดไม่ออก ใช้เป็นค่าดิบ · ถ้าเคยเปลี่ยนคีย์ ค่านี้ต้องกรอกใหม่",
                                    table, name, id);
                            }
                        }
                        else
                        {
                            skipped++;   // ค่าเปลี่ยนไปแล้ว (ผู้ใช้บันทึกใหม่/ลบ) ⇒ ของผู้ใช้ชนะ
                            if (state == SecretState.LegacyKey) legacyRemaining++;
                        }
                    }
                    if (changed.Count > 0)
                    {
                        audits++;
                        db.AddChainedAuditLog(new AuditLog
                        {
                            CompanyId = company(row),
                            Action = AuditAction.Update,
                            EntityType = table,
                            EntityId = row.Id.ToString(),
                            NewValues = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                action = "SecretReprotected", fields = changed, ruleCode = "SEC-KEY-REPROTECT",
                            }),
                            Timestamp = DateTime.UtcNow,
                        });
                    }
                }
                if (audits > 0) await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                if (batch.Count < BatchSize) break;
            }
        }

        await Pass(Scope(db.CompanySettings.IgnoreQueryFilters()), "CompanySettings", r => r.CompanyId,
            Col<CompanySettings>(r => r.EmailSmtpPassword),
            Col<CompanySettings>(r => r.EmailMsClientSecret),
            Col<CompanySettings>(r => r.EmailGmailClientSecret),
            Col<CompanySettings>(r => r.EmailGmailRefreshToken),
            Col<CompanySettings>(r => r.LineChannelAccessToken),
            Col<CompanySettings>(r => r.LineChannelSecret),
            Col<CompanySettings>(r => r.EtaxCertificatePassword),
            Col<CompanySettings>(r => r.EtaxRdApiSecret),
            Col<CompanySettings>(r => r.SignCallbackSecret));   // รอบ 240 — รหัสลับลงนาม webhook ต่อเอกสาร
        if (onlyCompanyId == null)   // ค่าตั้งแพลตฟอร์ม ไม่มีบริษัท
            await Pass(db.SiteSettings.IgnoreQueryFilters(), "SiteSettings", _ => (Guid?)null,
                Col<SiteSettings>(r => r.SystemSmtpPassword),
                Col<SiteSettings>(r => r.SystemMsClientSecret),
                Col<SiteSettings>(r => r.SystemGmailClientSecret),
                Col<SiteSettings>(r => r.SystemGmailRefreshToken),
                Col<SiteSettings>(r => r.LineMessagingChannelAccessToken),
                Col<SiteSettings>(r => r.LineMessagingChannelSecret),
                Col<SiteSettings>(r => r.GoogleClientSecret),
                Col<SiteSettings>(r => r.FacebookAppSecret),
                Col<SiteSettings>(r => r.LineLoginChannelSecret));
        await Pass(Scope(db.PaymentProviderConfigs.IgnoreQueryFilters()), "PaymentProviderConfigs", r => r.CompanyId,
            Col<PaymentProviderConfig>(r => r.TestSecretKeyProtected),
            Col<PaymentProviderConfig>(r => r.LiveSecretKeyProtected));
        await Pass(Scope(db.BankConnections.IgnoreQueryFilters()), "BankConnections", r => r.CompanyId,
            Col<BankConnection>(r => r.EncryptedCredentials),
            Col<BankConnection>(r => r.AccessToken));
        await Pass(Scope(db.WebhookRegistrations.IgnoreQueryFilters()), "WebhookRegistrations", r => r.CompanyId,
            Col<WebhookRegistration>(r => r.Secret));
        await Pass(Scope(db.ExternalIntegrations.IgnoreQueryFilters()), "ExternalIntegrations", r => r.CompanyId,
            Col<ExternalIntegration>(r => r.SecretKey));
        // ฝ่ายค้านรอบ 219 รอบ 2: คีย์ gateway ของเว็บ (CmsCommerceService เขียนด้วย EncryptionHelper + คีย์เดียวกับ SecretProtector) +
        // webhook secret (CmsWebhookController ถอดด้วย SecretProtector) — เดิมไม่อยู่ในทะเบียน ⇒ ค้างบน dev key/config เดิม
        await Pass(Scope(db.SitePaymentGateways.IgnoreQueryFilters()), "SitePaymentGateways", r => r.CompanyId,
            Col<SitePaymentGateway>(r => r.ApiKeyEncrypted),
            Col<SitePaymentGateway>(r => r.SecretKeyEncrypted),
            Col<SitePaymentGateway>(r => r.WebhookSecret));
        // รอบ 241 (241-10): token ลิงก์ชำระเงินของเอกสารฉบับเข้ารหัส — กรองเฉพาะใบที่มีลิงก์ (ไม่สแกนเอกสารทั้งตาราง)
        await Pass(Scope(db.Documents.IgnoreQueryFilters().Where(d => d.PayLinkTokenProtected != null)), "Documents", r => r.CompanyId,
            Col<Document>(r => r.PayLinkTokenProtected));
        // รอบ 245 L1: LINE userId ของปลายทางผู้ติดต่อ (เข้ารหัส · ย้ายจาก Contact.LineUserId) — กรองเฉพาะแถวที่มีค่า
        // รอบ 254 ฝ่ายค้าน L4: + LineGroupId (ผูกกลุ่ม L3 เขียนผ่าน Protect) — ทั้งสองช่องในรอบเดียว
        await Pass(Scope(db.ContactNotifyEndpoints.IgnoreQueryFilters().Where(e => e.LineUserId != null || e.LineGroupId != null)), "ContactNotifyEndpoints", r => r.CompanyId,
            Col<ContactNotifyEndpoint>(r => r.LineUserId), Col<ContactNotifyEndpoint>(r => r.LineGroupId));
        // รอบ 254 WP-11 L3: groupId ในรหัสผูกกลุ่ม (ระดับแพลตฟอร์ม · อายุ 30 นาที แต่ค้างได้ถึงงานลบรายคืน) ·
        // รอบ 275 แพ็ก A: ระดับแพลตฟอร์ม ⇒ เฉพาะรอบทั้งระบบ (เดิมรอบรายบริษัทก็แตะตารางนี้ทั้งตาราง)
        if (onlyCompanyId == null)
            await Pass(db.LineGroupBindCodes.IgnoreQueryFilters().Where(r => r.GroupIdProtected != ""), "LineGroupBindCodes", _ => (Guid?)null,
                Col<LineGroupBindCode>(r => r.GroupIdProtected));

        return new Result(scanned, per.Values.Sum(), per, skipped, pendingWrap, wrappedAmbiguous, unusable, legacyRemaining,
            states.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<SecretState, int>)kv.Value), findings);
    }
}

/// <summary>รันหลังบูต (รอ migration 3 นาที) แล้วทุก <see cref="Interval"/> — ล็อก <see cref="JobLock"/> ทั้งระบบ · บันทึกผลลง <see cref="IJobRunRecorder"/> ·
/// รอบ 275 แพ็ก A: ตรวจลายนิ้วมือคีย์ก่อนย้าย (ไม่ตรง ⇒ ไม่ย้าย) · ล้มดัง 3 ที่ (LogError · JobRunLog ล้ม · หน้า "สุขภาพข้อมูลเข้ารหัส") · ไม่ล้มแอป</summary>
public class SecretReprotectJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SecretReprotectJob> _logger;

    public SecretReprotectJob(IServiceScopeFactory scopeFactory, ILogger<SecretReprotectJob> logger)
    { _scopeFactory = scopeFactory; _logger = logger; }

    /// <summary>ชื่องานใน JobRunLog — หน้า "สุขภาพข้อมูลเข้ารหัส" อ่านผลรันล่าสุดจากชื่อนี้</summary>
    public const string JobName = "SecretReprotect";
    /// <summary>รอบ 275 แพ็ก A: รันเป็นระยะเหมือนฝั่ง PII — ตั้งคีย์เดิมแล้วความลับถูกย้ายโดยไม่ต้องรีสตาร์ตอีกรอบ</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
        }
    }

    /// <summary>ข้อความสรุปของรอบ (JobRunLog) — แยกเป็นฟังก์ชันให้เทสต์ล็อกได้</summary>
    public static string Summary(SecretReprotectPass.Result r)
        => $"ตรวจความลับ {r.Scanned} ค่า · เข้ารหัสใหม่ด้วยคีย์ปัจจุบัน {r.Reprotected} ค่า"
           + (r.PerColumn.Count == 0 ? "" : " (" + string.Join(", ", r.PerColumn.Select(kv => $"{kv.Key}={kv.Value}")) + ")")
           + (r.PendingWrap == 0 ? "" : $" · รอห่อ enc1 {r.PendingWrap} ค่า (เปิด {SecretProtector.MigrateCipherPrefixConfigKey} เมื่อพร้อม — forward-only)")
           + (r.WrappedAmbiguous == 0 ? "" : $" · ห่อค่ากำกวม (base64 ถอดไม่ออก) {r.WrappedAmbiguous} ค่า — ดู log")
           + (r.Unusable == 0 ? "" : $" · ⛔ ถอดไม่ออกทุกคีย์/เสียหาย {r.Unusable} ค่า — {EncryptionHealthText.AdminKeyAdvice} หรือให้ผู้ใช้กรอกใหม่")
           + (r.SkippedConcurrent == 0 ? "" : $" · ข้าม {r.SkippedConcurrent} ค่าที่ผู้ใช้เพิ่งแก้")
           + (r.LegacyKeyRemaining == 0 ? " · ✓ ไม่มีความลับบนคีย์เดิมเหลือ" : $" · ยังอยู่บนคีย์เดิม {r.LegacyKeyRemaining} ค่า (รอบถัดไปย้ายต่อ)");

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        var startedAt = DateTime.UtcNow;
        IJobRunRecorder? recorder = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();
            // T4 M-4: เครื่องนี้ใช้คีย์ที่ระบบใช้อยู่จริงไหม — ไม่ตรง ⇒ ไม่ย้าย (ย้าย = เขียนด้วยคีย์ที่เครื่องอื่นอ่านไม่ได้)
            var key = await Accounting.Services.Implementations.Security.EncryptionKeyFingerprintService.CheckAsync(db, protector, persist: true, _logger, stoppingToken);
            if (!key.AllowsRekey)
            {
                if (recorder != null) await recorder.RecordAsync(JobName, false, "ไม่ย้ายคีย์: " + key.AdminMessage, 0, startedAt);
                return;
            }
            await JobLock.RunExclusiveAsync(db, AdvisoryLockKey.BackgroundJob, nameof(SecretReprotectJob), async () =>
            {
                var r = await SecretReprotectPass.RunAsync(db, protector, stoppingToken, logger: _logger);
                var summary = Summary(r);
                if (r.Reprotected > 0 || r.Unusable > 0) _logger.LogWarning("SecretReprotect: {Summary}", summary);
                // สำเร็จ = งานรันจบ (ค่าที่ถอดไม่ออกเป็นงานของผู้ใช้ — อยู่ในข้อความ + หน้ารายงานรายแถว)
                if (recorder != null) await recorder.RecordAsync(JobName, true, summary, r.Reprotected, startedAt);
            }, _logger, ct: stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // ล้มดัง 3 ที่: LogError · JobRunLog (สถานะงาน) · หน้า "สุขภาพข้อมูลเข้ารหัส" แสดง "งานย้ายคีย์ล้ม"
            _logger.LogError(ex, "SecretReprotectJob ล้ม — ความลับที่เข้ารหัสด้วยคีย์เดิมยังถอดได้ผ่านคีย์เดิม จะลองใหม่ในรอบถัดไป");
            if (recorder != null)
            {
                try { await recorder.RecordAsync(JobName, false, "ล้ม: " + ex.Message, 0, startedAt); }
                catch (Exception rex) { _logger.LogError(rex, "SecretReprotectJob: บันทึกผลรันล้มไม่สำเร็จ"); }
            }
        }
    }
}
