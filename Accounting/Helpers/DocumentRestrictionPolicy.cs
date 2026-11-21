using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 276 — <b>ตัวตัดสินเดียว</b>ของผู้ใช้ "เห็นเฉพาะเอกสารที่ตัวเองสร้าง" / "สร้าง/แก้ได้เฉพาะร่าง" (pure · ไม่แตะ DB) ·
/// <list type="bullet">
/// <item><see cref="Effective"/> ค่าที่มีผล (ผู้ใช้ ∪ บทบาท · เจ้าของ/SystemAdmin ไม่ถูกจำกัด · เห็นเฉพาะของตัวเอง ⇒ เฉพาะร่าง — คำตัดสิน 276-2)</item>
/// <item><see cref="Decide"/> ทางเข้า (method + route template) ที่ผู้ถูกจำกัดเรียกได้ — รายการอนุญาต (ไม่อยู่ในรายการ = ปฏิเสธ · ปลอดภัยไว้ก่อน)</item>
/// <item>ข้อความถึงผู้ใช้ทุกข้อ (หน้าเว็บแสดงอย่างเดียว · server computes)</item>
/// </list>
/// ผู้เรียก: <c>Filters/DocumentRestrictionFilter</c> (ทุกคำขอ) · <c>Helpers/DocumentRestrictionGuard</c> (ชั้น service) · <c>RolePermissionService.GetMyPermissionsAsync</c> (เมนู/ป้าย)
/// </summary>
public static class DocumentRestrictionPolicy
{
    public const string OwnScopeRuleCode = "R276-OWN-SCOPE";
    public const string DraftOnlyRuleCode = "R276-DRAFT-ONLY";
    public const string NotFoundMessage = "ไม่พบเอกสาร";

    /// <summary>ค่าที่มีผลของผู้ใช้หนึ่งคนในบริษัทหนึ่ง</summary>
    public readonly record struct Restriction(bool OwnDocumentsOnly, bool DraftOnly)
    {
        public static Restriction None => default;
        public bool IsRestricted => OwnDocumentsOnly || DraftOnly;
    }

    /// <summary>ค่าที่มีผล = ค่าต่อผู้ใช้ ∪ ค่าของบทบาทกำหนดเอง · เจ้าของ/SystemAdmin ไม่ถูกจำกัดเสมอ (กันล็อกบริษัท — คนตั้งค่าต้องเห็นทุกอย่าง) ·
    /// "เห็นเฉพาะของตัวเอง" ⇒ "เฉพาะร่าง" เสมอ: การอนุมัติต้องเห็นทั้งบริษัท (เลข gap-free · ผลรวมใบลดหนี้ · ใบซ้ำ · สต็อก) — คำตัดสิน 276-2</summary>
    public static Restriction Effective(UserRole? role, bool userOwn, bool userDraft, bool roleOwn, bool roleDraft)
    {
        if (role is UserRole.Owner or UserRole.SystemAdmin) return Restriction.None;
        var own = userOwn || roleOwn;
        return new Restriction(own, own || userDraft || roleDraft);
    }

    /// <summary>ตั้งค่าจำกัดให้สมาชิกคนนี้ได้ไหม (null = ได้) — ตั้งบนเจ้าของ/ตัวเองไม่ได้</summary>
    public static string? SetProblem(UserRole targetRole, bool targetIsSelf)
    {
        if (targetIsSelf) return "ตั้งค่าจำกัดเอกสารให้ตัวเองไม่ได้ — ให้เจ้าของอีกคนเป็นผู้ตั้ง";
        if (targetRole is UserRole.Owner or UserRole.SystemAdmin)
            return "เจ้าของบริษัทถูกจำกัดเอกสารไม่ได้ (คนตั้งค่าต้องเห็นทุกเอกสาร) — เปลี่ยนบทบาทก่อนถ้าต้องการจำกัด";
        return null;
    }

    /// <summary>ป้ายบอกผู้ใช้ว่าทำไมเห็น/ทำได้เท่านี้ — null = ไม่ถูกจำกัด</summary>
    public static string? Notice(Restriction r)
    {
        if (!r.IsRestricted) return null;
        return r.OwnDocumentsOnly
            ? "บัญชีของคุณถูกตั้งให้เห็นเฉพาะเอกสารที่คุณสร้าง และสร้าง/แก้ได้เฉพาะฉบับร่าง — เมื่อพร้อมให้กด \"ส่งขออนุมัติ\" ผู้ดูแล/บัญชีจะได้รับแจ้งและเป็นผู้อนุมัติ (ตั้งโดยเจ้าของบริษัท)"
            : "บัญชีของคุณสร้าง/แก้ได้เฉพาะเอกสารฉบับร่าง (อนุมัติ · ออกเลข · ยกเลิก · ลงบัญชี · รับ/จ่ายชำระ ไม่ได้) — เมื่อพร้อมให้กด \"ส่งขออนุมัติ\" (ตั้งโดยเจ้าของบริษัท)";
    }

    public static string OwnScopeDeniedMessage =>
        "บัญชีของคุณถูกตั้งให้เห็นเฉพาะเอกสารที่คุณสร้าง — หน้านี้แสดงข้อมูลรวมของบริษัท จึงเปิดไม่ได้ (ติดต่อเจ้าของบริษัทถ้าต้องใช้งาน)";

    public static string DraftOnlyDeniedMessage(string? verb = null) =>
        $"บัญชีของคุณสร้าง/แก้ได้เฉพาะเอกสารฉบับร่าง — {(string.IsNullOrWhiteSpace(verb) ? "คำสั่งนี้" : verb)}ทำไม่ได้ · กด \"ส่งขออนุมัติ\" บนใบเพื่อให้ผู้ดูแล/บัญชีอนุมัติ";

    public static string DraftStatusDeniedMessage =>
        "บัญชีของคุณแก้/ลบได้เฉพาะใบร่างหรือใบที่ถูกตีกลับ — ใบที่รออนุมัติให้กด \"ถอนคำขอ\" ก่อน · ใบที่อนุมัติแล้วให้ผู้ดูแล/บัญชีจัดการ";

    /// <summary>ทางเข้าเขียน = POST/PUT/PATCH/DELETE</summary>
    public static bool IsWrite(string? method) =>
        method is not null && !HttpMethodIs(method, "GET") && !HttpMethodIs(method, "HEAD") && !HttpMethodIs(method, "OPTIONS");

    private static bool HttpMethodIs(string m, string x) => string.Equals(m, x, StringComparison.OrdinalIgnoreCase);

    /// <summary>route template → รูปเทียบ (ตัวเล็ก · ตัด "/" และ "api/" นำหน้า)</summary>
    internal static string NormalizeTemplate(string? template)
    {
        var t = (template ?? "").Trim().TrimStart('~').TrimStart('/').ToLowerInvariant();
        return t.StartsWith("api/") ? t[4..] : t;
    }

    /// <summary>ด่านเพิ่มของทางเข้า (ตัวกรองตรวจกับฐาน)</summary>
    [Flags]
    public enum RouteCheck
    {
        None = 0,
        /// <summary>เขียนได้เฉพาะใบ Draft/Rejected (id จาก route)</summary>
        DraftStatus = 1,
        /// <summary>entityType/entityId ใน route หรือ query ต้องเป็นเอกสาร (ไม่ใช่ entity อื่น เช่นพนักงาน/สมุดรายวัน)</summary>
        DocumentEntityOnly = 2,
        /// <summary>attachmentId ใน route ต้องเป็นไฟล์ของเอกสาร</summary>
        AttachmentOfDocument = 4,
        /// <summary>scanId/fileAttachmentId ใน route ต้องเป็นสแกน/ไฟล์ที่ผู้ใช้คนนี้อัปโหลดเอง (ฝ่ายค้าน LOW-12: OCR สร้างร่างของตัวเอง)</summary>
        OwnScan = 8,
    }

    /// <summary>หนึ่งทางเข้าที่ผู้ถูกจำกัดเรียกได้ · <paramref name="OwnScope"/> = ผู้ "เห็นเฉพาะของตัวเอง" เรียกได้ · <paramref name="DraftWrite"/> = ทางเขียนที่ผู้ "เฉพาะร่าง" เรียกได้</summary>
    public sealed record RouteRule(string Method, string Template, bool OwnScope, bool DraftWrite, RouteCheck Checks = RouteCheck.None);

    private const string C = "companies/{companyid:guid}/";
    private const string D = C + "document/";
    private const string DId = D + "{documentid:guid}";

    /// <summary>รายการอนุญาตทั้งหมด (ทางอ่านทั้งหมดของผู้ "เฉพาะร่าง" ไม่ต้องอยู่ที่นี่ — อ่านได้ตามสิทธิ์เดิม) ·
    /// เทสต์ <c>DocumentRestrictionRouteInventoryTests</c> ยืนยันว่าทุกแถวตรงกับ action จริง (กัน drift เมื่อ route เปลี่ยน)</summary>
    public static readonly IReadOnlyList<RouteRule> Routes = new RouteRule[]
    {
        // ── เอกสาร: อ่าน (ตัวกรองแถว EF ตัดใบของคนอื่น · id ใน route ตรวจซ้ำที่ตัวกรอง ⇒ 404) ──
        new("GET", C + "document", OwnScope: true, DraftWrite: false),
        new("GET", D + "periods", true, false),
        new("GET", D + "conversion-filter-options", true, false),
        new("GET", D + "duplicate-check", true, false),
        new("GET", D + "export", true, false),
        new("GET", DId, true, false),
        new("GET", DId + "/linked-scan", true, false),
        new("GET", DId + "/chain", true, false),
        new("GET", DId + "/revisions", true, false),
        new("GET", DId + "/revisions/{revisionnumber:int}", true, false),
        new("GET", DId + "/conversion-targets", true, false),
        new("GET", DId + "/fulfillment", true, false),
        new("GET", DId + "/approval-request", true, false),
        new("GET", DId + "/receipt-line-default", true, false),   // รอบ 284: ค่าตั้งต้นบรรทัดใบเสร็จ (ร่างใบเสร็จของตัวเอง · ตัวกรองแถวตัดใบของคนอื่น)
        new("GET", C + "documents/{sourceid:guid}/clone/preview", true, false),
        new("POST", C + "document-templates/generate-pdf", true, DraftWrite: true),
        new("POST", C + "document-templates/generate-html", true, true),
        // ── เอกสาร: เขียนร่าง ──
        new("POST", C + "document", true, true),
        new("PUT", DId, true, true, RouteCheck.DraftStatus),
        new("DELETE", DId, true, true, RouteCheck.DraftStatus),
        new("POST", DId + "/convert/{targettype}", true, true),
        new("POST", DId + "/convert-partial/{targettype}", true, true),
        new("POST", C + "documents/{sourceid:guid}/clone", true, true),
        new("POST", D + "ai-suggest-pv-accounting", true, true),
        new("POST", DId + "/approval-request", true, true),
        new("POST", DId + "/approval-request/withdraw", true, true),
        new("POST", C + "approval/submit", true, true, RouteCheck.DocumentEntityOnly),
        new("GET", C + "approval/for-entity", true, false, RouteCheck.DocumentEntityOnly),
        // ── ไฟล์แนบ/ความเห็นของเอกสาร ──
        new("GET", C + "attachments/{entitytype}/{entityid:guid}", true, false, RouteCheck.DocumentEntityOnly),
        new("POST", C + "attachments/{entitytype}/{entityid:guid}", true, true, RouteCheck.DocumentEntityOnly | RouteCheck.DraftStatus),
        new("DELETE", C + "attachments/{attachmentid:guid}", true, true, RouteCheck.AttachmentOfDocument | RouteCheck.DraftStatus),
        new("GET", C + "attachments/{attachmentid:guid}/download", true, false, RouteCheck.AttachmentOfDocument),
        new("GET", C + "comments/entity/{entitytype}/{entityid:guid}", true, false, RouteCheck.DocumentEntityOnly),
        new("GET", C + "accountant/timeline/{entitytype}/{entityid}", true, false, RouteCheck.DocumentEntityOnly),   // ปุ่ม "📜 ประวัติ" ของใบ
        new("GET", C + "document-auto-send/documents/{documentid:guid}", true, false),
        // ── ข้อมูลหลักที่ฟอร์มเอกสารต้องใช้ (ไม่ใช่เอกสารของคนอื่น) ──
        new("GET", D + "contacts", true, false),
        new("GET", D + "contacts/{contactid:guid}", true, false),
        new("GET", D + "contacts/{contactid:guid}/smart-defaults", true, false),
        new("POST", D + "contacts", true, true),
        // ฝ่ายค้าน MED-6: ผู้ถูกจำกัด "สร้าง" ผู้ติดต่อใหม่ได้ (จำเป็นต่อการทำร่าง) แต่ **แก้ของเดิมไม่ได้** (บัญชีธนาคาร · เลขภาษี · วงเงิน) — PUT ไม่อยู่ในรายการ ⇒ 403 + เหตุ
        new("POST", D + "contacts/parse-address", true, true),
        new("GET", C + "product", true, false),
        new("GET", C + "product/{productid:guid}", true, false),
        new("GET", C + "accounting/accounts", true, false),
        new("GET", C + "accounting/accounts/payment-channels", true, false),
        new("GET", C + "settings", true, false),
        new("GET", C + "projects/active", true, false),
        new("GET", C + "dimensions/branches", true, false),
        new("GET", C + "deposit-kinds", true, false),
        new("GET", C + "document-templates", true, false),
        new("GET", C + "document-templates/default/{documenttype}", true, false),
        new("GET", C + "document-brands", true, false),
        new("GET", C + "document-brands/policy", true, false),
        new("GET", C + "quota/summary", true, false),
        new("GET", C + "metering/quota", true, false),
        new("GET", C + "wht-certs/filter-options", true, false),
        new("GET", D + "send-types", true, false),
        new("GET", C + "ocr/engines", true, false),
        new("GET", C + "smart/check-period-status", true, false),
        new("GET", C + "smart/validate-tax-id", true, false),
        new("GET", C + "ai/gl-account/suggest", true, false),
        new("POST", C + "ai/payment-voucher/suggest-account", true, true),
        new("POST", C + "ai/credit-note/classify-reason", true, true),
        new("POST", C + "ai-feedback/record", true, true),
        new("POST", C + "withholding-tax-certs/gross-up-preview", true, true),
        // ── OCR สร้างร่างของตัวเอง (ฝ่ายค้าน LOW-12 · กฎเหล็ก #3) — สแกน/ไฟล์ต้องเป็นของผู้ใช้คนนี้ · นำเข้าสต็อก/ลงทะเบียนสินทรัพย์/ลง JE/คิวตรวจ/ตั้งค่า ไม่อยู่ ──
        new("POST", C + "ocr/upload", true, true),
        new("POST", C + "ocr/scan/{fileattachmentid:guid}", true, true, RouteCheck.OwnScan),
        new("POST", C + "ocr/{scanid:guid}/retry", true, true, RouteCheck.OwnScan),
        new("GET", C + "ocr/{scanid:guid}", true, false, RouteCheck.OwnScan),
        new("GET", C + "ocr/{scanid:guid}/image", true, false, RouteCheck.OwnScan),
        new("GET", C + "ocr/{scanid:guid}/line-preview", true, false, RouteCheck.OwnScan),
        new("POST", C + "ocr/{scanid:guid}/create-document", true, true, RouteCheck.OwnScan),
        new("POST", C + "ocr/{scanid:guid}/link-document/{documentid:guid}", true, true, RouteCheck.OwnScan | RouteCheck.DraftStatus),
        new("POST", C + "ocr/{scanid:guid}/line-fields", true, true, RouteCheck.OwnScan),
        new("POST", C + "ocr/{scanid:guid}/modify-line", true, true, RouteCheck.OwnScan),
        new("POST", C + "ocr/{scanid:guid}/line-project", true, true, RouteCheck.OwnScan),
        new("POST", C + "ocr/{scanid:guid}/lines-project", true, true, RouteCheck.OwnScan),
        new("POST", C + "ocr/{scanid:guid}/match-contact/{contactid:guid}", true, true, RouteCheck.OwnScan),
        new("POST", C + "ocr/{scanid:guid}/match-walk-in", true, true, RouteCheck.OwnScan),
        new("POST", C + "ocr/{scanid:guid}/correct", true, true, RouteCheck.OwnScan),
        new("DELETE", C + "ocr/{scanid:guid}", true, true, RouteCheck.OwnScan),
        new("GET", C + "ocr/quota", true, false),
        // ── บัญชีผู้ใช้/บริษัท (หัวหน้าจอ · เมนู) ──
        new("GET", "company/{companyid:guid}", true, false),
        new("GET", "company/{companyid:guid}/roles/my-permissions", true, false),

        // ── ทางเข้าที่ไม่มี {companyId} ใน route (ฝ่ายค้าน — ด่านระดับ "ผู้ใช้ + บริษัทที่ทำงานอยู่") ──
        // ข้อมูลส่วนตัว/ข้อมูลอ้างอิงสาธารณะ ไม่ใช่ข้อมูลของบริษัท · ภาพรวมข้ามบริษัท (accountant-workspace) · บิลค่าบริการ · ส่งแจ้งเตือน ไม่อยู่ ⇒ 403
        new("GET", "me/permissions", true, false),
        new("GET", "company", true, false),
        new("GET", "notification", true, false),          // รายการกรองแถวที่ NotificationService (ฝ่ายค้าน MED-3)
        new("GET", "notification/count", true, false),
        new("POST", "notification/mark-read", true, true),
        new("POST", "notification/mark-all-read", true, true),
        new("POST", "mobile/devices", true, true),
        new("DELETE", "mobile/devices/{devicetoken}", true, true),
        new("GET", "invitations/by-token/{token}", true, false),
        new("POST", "invitations/accept", true, true),
        new("GET", "subscription/plans", true, false),
        new("GET", "subscription/feature-catalog", true, false),
        new("GET", "integration/link-patterns", true, false),
        new("GET", "auth/*", true, false),
        new("POST", "auth/*", true, true),
        new("PUT", "auth/*", true, true),
        new("GET", "gov/*", true, false),
        new("GET", "dbd/*", true, false),
        new("GET", "reference/*", true, false),
        new("GET", "signatures", true, false),           // ลายเซ็นของตัวเอง (my-signature)
        new("GET", "signatures/default", true, false),
        new("POST", "signatures", true, true),
        new("POST", "signatures/{signatureid:guid}/set-default", true, true),
        new("DELETE", "signatures/{signatureid:guid}", true, true),
    };

    private static readonly Dictionary<(string, string), RouteRule> ByKey =
        Routes.Where(r => !r.Template.EndsWith("/*")).ToDictionary(r => (r.Method.ToUpperInvariant(), NormalizeTemplate(r.Template)));

    /// <summary>แถวแบบ "คำนำหน้า/*" — ทั้งกลุ่มที่เป็นข้อมูลส่วนตัว/อ้างอิง (auth · ข้อมูลราชการ)</summary>
    private static readonly List<(string Method, string Prefix, RouteRule Rule)> ByPrefix =
        Routes.Where(r => r.Template.EndsWith("/*"))
            .Select(r => (r.Method.ToUpperInvariant(), NormalizeTemplate(r.Template[..^1]), r)).ToList();

    internal static RouteRule? Find(string method, string? template)
    {
        var m = method.ToUpperInvariant();
        var t = NormalizeTemplate(template);
        if (ByKey.TryGetValue((m, t), out var rule)) return rule;
        return ByPrefix.FirstOrDefault(p => p.Method == m && t.StartsWith(p.Prefix, StringComparison.Ordinal)).Rule;
    }

    public enum Verdict { Allow, DenyOwnScope, DenyDraftOnly }

    public readonly record struct Decision(Verdict Verdict, string? Message, string? RuleCode, RouteRule? Rule)
    {
        public bool Allowed => Verdict == Verdict.Allow;
    }

    /// <summary>ผู้ใช้ค่าจำกัด <paramref name="r"/> เรียก <paramref name="method"/> <paramref name="template"/> ได้ไหม ·
    /// ไม่ถูกจำกัด = ผ่านเสมอ (พฤติกรรมเดิม) · เห็นเฉพาะของตัวเอง = ต้องอยู่ในรายการ (ทุก method) · เฉพาะร่าง = ทางเขียนต้องอยู่ในรายการเขียน</summary>
    public static Decision Decide(Restriction r, string method, string? template)
    {
        if (!r.IsRestricted) return new Decision(Verdict.Allow, null, null, null);
        var rule = Find(method, template);
        if (r.OwnDocumentsOnly && rule is not { OwnScope: true })
            return new Decision(Verdict.DenyOwnScope, OwnScopeDeniedMessage, OwnScopeRuleCode, rule);
        if (r.DraftOnly && IsWrite(method) && rule is not { DraftWrite: true })
            return new Decision(Verdict.DenyDraftOnly, DraftOnlyDeniedMessage(), DraftOnlyRuleCode, rule);
        return new Decision(Verdict.Allow, null, null, rule);
    }

    /// <summary>คำสั่ง LINE (บอทของบริษัท) — ชนิดเทียบกับทางเข้าเว็บที่ทำงานเดียวกัน</summary>
    public enum LineCommand
    {
        /// <summary>"ดูยอด" = ยอดเงินเข้า/ออกทั้งบริษัท (รายงาน — เว็บ: อ่านได้ตามสิทธิ์เดิม · เห็นเฉพาะของตัวเอง = ไม่ได้)</summary>
        CompanySummary,
        /// <summary>"ถาม" = ผู้ช่วย AI ที่ค้นข้อมูลทั้งบริษัท (เว็บ: แชตไม่อยู่ในรายการทั้งสองแบบ)</summary>
        AskAssistant,
        /// <summary>"บันทึก"/ส่งรูป = สร้างร่าง (ได้ทั้งสองแบบ — อนุมัติอัตโนมัติไม่ทำ)</summary>
        CreateDraft,
        /// <summary>ปุ่ม "อนุมัติเลย" (เว็บ: อนุมัติไม่อยู่ในรายการเขียน)</summary>
        Approve,
    }

    /// <summary>ผู้ถูกจำกัดใช้คำสั่ง LINE นี้ได้ไหม (null = ได้ · ข้อความ = เหตุที่ตอบในแชต) — ตัวตัดสินเดียวกับเว็บ (คำสั่งที่เทียบเท่า)</summary>
    public static string? LineCommandDenied(Restriction r, LineCommand cmd)
    {
        if (!r.IsRestricted) return null;
        return cmd switch
        {
            LineCommand.CompanySummary when r.OwnDocumentsOnly => "⛔ " + OwnScopeDeniedMessage,
            LineCommand.AskAssistant => "⛔ ผู้ช่วย AI ค้นข้อมูลทั้งบริษัท — บัญชีของคุณถูกจำกัดเอกสารจึงใช้คำสั่งนี้ไม่ได้ (ส่งรูปใบเสร็จ/\"บันทึก\" เพื่อสร้างร่างได้ตามปกติ)",
            LineCommand.Approve => "⛔ " + DraftOnlyDeniedMessage("อนุมัติ"),
            _ => null,
        };
    }

    /// <summary>สถานะที่ผู้ "เฉพาะร่าง" แก้/ลบ/แนบไฟล์ได้</summary>
    public static bool IsEditableDraftStatus(DocumentStatus s) => s is DocumentStatus.Draft or DocumentStatus.Rejected;

    /// <summary>เมนูที่ผู้ "เห็นเฉพาะของตัวเอง" เห็น (หน้าอื่นแสดงข้อมูลรวมของบริษัท — API ปฏิเสธอยู่แล้ว · ตรงนี้กันเมนูโผล่แล้วกดได้ 403)</summary>
    public static readonly IReadOnlyList<string> OwnScopeMenuIds = new[]
    {
        "documents", "expense-docs", "contacts", "notifications", "my-signature", "help", "getting-started",
    };
}
