using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>รายการ 1 ฟีเจอร์ในแคตตาล็อก — ชื่อ enum · หมวด · ป้ายไทย/อังกฤษ · คำอธิบายสั้น</summary>
public sealed record FeatureCatalogEntry(string Name, string Category, string LabelTh, string LabelEn, string DescriptionTh);

/// <summary>หมวดของฟีเจอร์ (ลำดับที่แสดง = ลำดับใน <see cref="FeatureCatalog.Categories"/>)</summary>
public sealed record FeatureCategoryInfo(string Key, string LabelTh, string LabelEn);

/// <summary>
/// **แคตตาล็อกฟีเจอร์ของแพ็กเกจ — ที่เดียวของทั้งระบบ**
///
/// <para>ที่มา (2026-09-10): ตารางเปรียบเทียบแพ็กเกจบนหน้าแรก (`index.html`) · การ์ดเลือก
/// แพ็กเกจตอนสมัคร (`register.html`) · หน้าตั้งค่าฟีเจอร์ของเจ้าของ (`settings-features.html`)
/// · ปุ่ม preset ในหน้าแพ็กเกจของแอดมิน (`admin/plans.html`) · ป้ายชื่อฟีเจอร์ใน
/// <c>FeatureFlagsHelper</c> — **ห้าที่ต่างคนต่างพิมพ์ชื่อฟีเจอร์/ป้าย/รายการเอง** ⇒ แอดมินติ๊ก
/// ฟีเจอร์ในแพ็กเกจแล้วหน้าแรกยังโชว์ตารางเดิมที่พิมพ์ไว้ตายตัว (ผู้ใช้รายงาน) · ธง CMS 3 ตัว
/// ไม่มีป้ายไทยในบางสำเนา · preset ของแอดมินตกฟีเจอร์ที่เพิ่มทีหลัง (`EtaxByEmail`)</para>
///
/// <para>กติกา: หน้าเว็บทุกหน้า**แสดง**จาก endpoint `GET /api/subscription/feature-catalog`
/// (ตัวนี้) + `GET /api/subscription/plans` (ค่าที่แอดมินตั้ง) เท่านั้น — ห้ามพิมพ์ชื่อฟีเจอร์ ·
/// ป้าย · รายการ preset ซ้ำใน JS/HTML · เพิ่มธงใหม่ใน <see cref="FeatureFlags"/> แล้วต้องเพิ่ม
/// metadata ที่นี่ในคอมมิตเดียว (เทสต์ <c>FeatureCatalogTests</c> ล็อกว่าทุกบิตมี metadata)</para>
/// </summary>
public static class FeatureCatalog
{
    /// <summary>ชื่อสมาชิก enum ที่เป็น "ชุดสำเร็จรูป" (หลายบิต) — ไม่ใช่ฟีเจอร์เดี่ยว</summary>
    public static readonly string[] PresetNames = { "None", "TrialFeatures", "BasicFeatures", "ProFeatures", "EnterpriseFeatures" };

    public static readonly IReadOnlyList<FeatureCategoryInfo> Categories = new[]
    {
        new FeatureCategoryInfo("core", "ฟีเจอร์หลัก", "Core"),
        new FeatureCategoryInfo("reporting", "รายงาน", "Reporting"),
        new FeatureCategoryInfo("operations", "การดำเนินงาน", "Operations"),
        new FeatureCategoryInfo("multi", "หลายบริษัท / หลายสกุลเงิน", "Multi-company / Multi-currency"),
        new FeatureCategoryInfo("advanced", "ขั้นสูง / Workflow / e-Tax", "Advanced / Workflow / e-Tax"),
        new FeatureCategoryInfo("integration", "เชื่อมต่อ / นำเข้า", "Integration / Import"),
        new FeatureCategoryInfo("ai", "AI / OCR", "AI / OCR"),
        new FeatureCategoryInfo("cms", "CMS & E-Commerce", "CMS & E-Commerce"),
        new FeatureCategoryInfo("other", "อื่น ๆ", "Other"),
    };

    /// <summary>ฟีเจอร์เดี่ยวทุกตัว (single-bit) เรียงตามค่าบิต — ไม่รวม preset</summary>
    public static readonly IReadOnlyList<FeatureCatalogEntry> All = BuildAll();

    private static readonly Dictionary<string, FeatureCatalogEntry> ByName =
        All.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>ชุดสำเร็จรูป → รายชื่อฟีเจอร์ (คำนวณจาก enum จริง — เพิ่มบิตใน `ProFeatures` แล้ว
    /// ปุ่ม preset ของแอดมินตามทันที ไม่ต้องแก้ JS)</summary>
    public static IReadOnlyDictionary<string, List<string>> Presets => new Dictionary<string, List<string>>
    {
        ["trial"] = NamesOf(FeatureFlags.TrialFeatures),
        ["basic"] = NamesOf(FeatureFlags.BasicFeatures),
        ["pro"] = NamesOf(FeatureFlags.ProFeatures),
        ["enterprise"] = NamesOf(FeatureFlags.EnterpriseFeatures),
    };

    public static bool IsSingleBit(FeatureFlags v)
    {
        var val = (long)v;
        return val > 0 && (val & (val - 1)) == 0;
    }

    /// <summary>bitmask → รายชื่อฟีเจอร์เดี่ยวที่เปิด (เรียงตามบิต)</summary>
    public static List<string> NamesOf(FeatureFlags flags)
        => All.Where(e => flags.HasFlag(Enum.Parse<FeatureFlags>(e.Name))).Select(e => e.Name).ToList();

    /// <summary>รายชื่อ → bitmask (ชื่อที่ไม่รู้จัก/preset ถูกข้าม)</summary>
    public static FeatureFlags FlagsOf(IEnumerable<string>? names)
    {
        var result = FeatureFlags.None;
        if (names == null) return result;
        foreach (var n in names)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            if (Enum.TryParse<FeatureFlags>(n.Trim(), true, out var f) && IsSingleBit(f))
                result |= f;
        }
        return result;
    }

    public static FeatureCatalogEntry? Find(string name)
        => ByName.TryGetValue(name, out var e) ? e : null;

    public static string LabelTh(string name) => Find(name)?.LabelTh ?? name;

    private static List<FeatureCatalogEntry> BuildAll()
    {
        var list = new List<FeatureCatalogEntry>();
        foreach (FeatureFlags v in Enum.GetValues(typeof(FeatureFlags)))
        {
            var name = v.ToString();
            if (PresetNames.Contains(name) || !IsSingleBit(v)) continue;
            list.Add(Meta(name));
        }
        return list;
    }

    /// <summary>metadata รายฟีเจอร์ — ธงที่ไม่มีบรรทัดที่นี่จะตกเป็น "other" พร้อมชื่อ enum ล้วน
    /// ซึ่ง <c>FeatureCatalogTests</c> ถือว่า**ยังไม่ผ่าน** (ป้ายที่ผู้ใช้เห็นห้ามเป็นชื่อตัวแปร)</summary>
    private static FeatureCatalogEntry Meta(string name) => name switch
    {
        // ─── core ───
        "BasicAccounting" => new(name, "core", "ระบบบัญชี + สมุดรายวัน", "Accounting + journal", "GL · สมุดรายวัน · งบทดลอง · ผังบัญชี"),
        "DocumentEngine" => new(name, "core", "ออกเอกสาร (ใบแจ้งหนี้/ใบเสร็จ/ใบกำกับ)", "Documents (invoice/receipt/tax invoice)", "ใบเสนอราคา → ใบแจ้งหนี้ → ใบกำกับภาษี → ใบเสร็จ"),
        "TaxManagement" => new(name, "core", "ภาษีมูลค่าเพิ่ม + หัก ณ ที่จ่าย", "VAT + withholding tax", "ภ.พ.30 · ภ.ง.ด.1/3/53 · 50 ทวิ"),
        "Dashboard" => new(name, "core", "แดชบอร์ดภาพรวม", "Overview dashboard", "KPI · กราฟ · สรุปผู้บริหาร"),
        "CustomChartOfAccounts" => new(name, "core", "ผังบัญชีแบบกำหนดเอง", "Custom chart of accounts", "เพิ่ม/แก้ผังบัญชีเอง"),
        "AutoPosting" => new(name, "core", "ลงบัญชีอัตโนมัติ", "Automatic posting", "ลงสมุดรายวันอัตโนมัติจากเอกสาร"),
        // ─── reporting ───
        "AdvancedReporting" => new(name, "reporting", "รายงานขั้นสูง + กระแสเงินสด", "Advanced reports + cash flow", "งบดุล · งบกำไรขาดทุน · กระแสเงินสด"),
        "AgingReport" => new(name, "reporting", "รายงานอายุลูกหนี้/เจ้าหนี้", "AR/AP aging reports", "อายุลูกหนี้-เจ้าหนี้ · แจ้งเตือน"),
        "BudgetManagement" => new(name, "reporting", "งบประมาณ", "Budget", "งบประมาณเทียบจริง · ผลต่าง"),
        "ReportBuilder" => new(name, "reporting", "สร้างรายงานเอง", "Report builder", "ออกแบบรายงานเอง"),
        "FPA" => new(name, "reporting", "วางแผน/วิเคราะห์การเงิน (FP&A)", "FP&A", "พยากรณ์ · วิเคราะห์ผลต่าง"),
        // ─── operations ───
        "Inventory" => new(name, "operations", "สต็อกสินค้า", "Inventory", "ความเคลื่อนไหวสต็อก · บาร์โค้ด · ต้นทุน"),
        "WarehouseManagement" => new(name, "operations", "คลังสินค้าหลายแห่ง", "Multi-warehouse", "สต็อกแยกคลัง"),
        "FixedAssets" => new(name, "operations", "สินทรัพย์ถาวร + ค่าเสื่อมราคา", "Fixed assets + depreciation", "ทะเบียน · ค่าเสื่อม · จำหน่าย"),
        "BankReconciliation" => new(name, "operations", "กระทบยอดธนาคาร", "Bank reconciliation", "จับคู่รายการธนาคารอัตโนมัติ"),
        "ExpenseManagement" => new(name, "operations", "เบิกค่าใช้จ่าย", "Expense claims", "เบิกจ่าย · §65 ตรี · อนุมัติ"),
        "PurchaseOrders" => new(name, "operations", "ใบสั่งซื้อ", "Purchase orders", "PO → รับสินค้า → ใบซื้อ"),
        "RecurringTransactions" => new(name, "operations", "รายการซ้ำอัตโนมัติ", "Recurring transactions", "สร้างเอกสารประจำอัตโนมัติ"),
        "CostCenter" => new(name, "operations", "ศูนย์ต้นทุน", "Cost centers", "รายงานตามมิติ/ศูนย์ต้นทุน"),
        "ProjectAccounting" => new(name, "operations", "บัญชีโครงการ", "Project accounting", "กำไรขาดทุนรายโครงการ"),
        "TimeBilling" => new(name, "operations", "Time Billing", "Time billing", "บันทึกชั่วโมง → วางบิลลูกค้า"),
        "Payroll" => new(name, "operations", "ระบบเงินเดือน + ประกันสังคม", "Payroll + social security", "รอบเงินเดือน · ลา · ภ.ง.ด.1 · สปส."),
        "LoanManagement" => new(name, "operations", "สินเชื่อ/เงินกู้", "Loans", "ตารางผ่อน · ดอกเบี้ย"),
        "Commission" => new(name, "operations", "คอมมิชชั่น", "Commission", "คำนวณค่าคอมมิชชั่นตามกฎ"),
        "RevenueRecognition" => new(name, "operations", "รับรู้รายได้", "Revenue recognition", "TFRS 15 / NPAEs บทที่ 6"),
        // ─── multi ───
        "MultiCompany" => new(name, "multi", "หลายบริษัท", "Multi-company", "จัดการหลายบริษัทในบัญชีเดียว"),
        "MultiUser" => new(name, "multi", "ผู้ใช้หลายคน", "Multi-user", "เชิญผู้ใช้ · สิทธิ์ตาม role"),
        "MultiCurrency" => new(name, "multi", "หลายสกุลเงิน", "Multi-currency", "อัตราแลกเปลี่ยน · กำไรขาดทุนจากอัตรา"),
        "Consolidation" => new(name, "multi", "Consolidation + Intercompany", "Consolidation + intercompany", "รวมงบบริษัทในกลุ่ม"),
        // ─── advanced ───
        "WorkflowEngine" => new(name, "advanced", "Workflow Engine", "Workflow engine", "กำหนดขั้นอนุมัติหลายชั้น"),
        "ApprovalWorkflow" => new(name, "advanced", "ระบบอนุมัติ", "Approval workflow", "สายอนุมัติเอกสาร"),
        "AuditLog" => new(name, "advanced", "Audit Log", "Audit log", "บันทึกการเข้าถึง/แก้ไข (hash chain)"),
        "EtaxInvoice" => new(name, "advanced", "e-Tax Invoice + ลายเซ็นดิจิทัล", "e-Tax Invoice + digital signature", "PDF/A-3 + XML ETDA"),
        "EtaxByEmail" => new(name, "advanced", "e-Tax Invoice by Email", "e-Tax Invoice by Email", "PDF/A-3 ส่งลูกค้า + RD ประทับเวลา"),
        "EtaxDirect" => new(name, "advanced", "e-Tax Direct (ส่ง XML เข้า RD)", "e-Tax Direct (RD API)", "ส่ง XML ตรงเข้า RD API"),
        "OpenBanking" => new(name, "advanced", "Open Banking", "Open Banking", "ดึงรายการธนาคารอัตโนมัติ"),
        "Webhook" => new(name, "advanced", "Webhook", "Webhook", "ส่งเหตุการณ์ออกไประบบนอก"),
        // ─── integration ───
        "APIAccess" => new(name, "integration", "API Access", "API access", "เชื่อมระบบนอกผ่าน REST API"),
        "BulkImport" => new(name, "integration", "นำเข้าจำนวนมาก", "Bulk import", "นำเข้า CSV/Excel"),
        "EmailNotification" => new(name, "integration", "แจ้งเตือนทางอีเมล", "Email notifications", "ส่งเอกสาร · แจ้งครบกำหนด"),
        "FileAttachments" => new(name, "integration", "ไฟล์แนบ", "File attachments", "แนบรูป/PDF กับเอกสาร"),
        "CustomerPortal" => new(name, "integration", "พอร์ทัลลูกค้า", "Customer portal", "ลูกค้าเข้าดูเอกสารของตัวเอง"),
        // ─── ai ───
        "AI_Features" => new(name, "ai", "AI อัจฉริยะ", "Smart AI", "AI แนะนำ + โมเดลเรียนรู้ในระบบ"),
        "DocumentOCR" => new(name, "ai", "OCR เอกสาร", "Document OCR", "ถ่ายรูปบิล → อ่านอัตโนมัติ → สร้างเอกสาร"),
        // ─── cms ───
        "CmsWebsiteBuilder" => new(name, "cms", "สร้างเว็บไซต์ (Multi-site CMS)", "Website builder (multi-site CMS)", "เว็บบริษัท · หน้า Landing · ฟอร์มติดต่อ"),
        "CmsEcommerce" => new(name, "cms", "ร้านค้าออนไลน์ + ตะกร้า + ชำระเงิน", "Online store + cart + payment", "ตะกร้า · คูปอง · จัดส่ง · ตัวแปรสินค้า"),
        "CmsBooking" => new(name, "cms", "ระบบจองบริการ + นัดหมาย", "Service booking + appointments", "ปฏิทินจอง · ที่พัก · นัดหมาย"),
        _ => new(name, "other", name, name, ""),
    };
}
