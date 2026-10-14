namespace Accounting.Helpers;

/// <summary>โปรไฟล์ไฟล์ส่งออกของโปรแกรมต้นทางหนึ่งแบบ (หนึ่งชนิดข้อมูล)</summary>
/// <param name="SourceSystem">คีย์โปรแกรมต้นทาง ("express" · "peak" · "flowaccount" · "bank:generic-th") — เก็บใน <c>SmartImportSession.SourceSystem</c></param>
/// <param name="EntityType">ชนิดข้อมูลปลายทาง (คีย์แบบแสดงของ <see cref="ImportSchemas"/>)</param>
/// <param name="Headers">หัวคอลัมน์ตามไฟล์ (สะกดตามที่โปรแกรมส่งออก)</param>
/// <param name="ColumnMap">หัวคอลัมน์ → field (null = คอลัมน์ที่ไม่ใช้ เช่น รหัสลูกค้าของระบบเดิม)</param>
/// <param name="Confirmed">true = ยืนยันกับไฟล์ส่งออกจริงแล้ว · false = <b>เดา</b> (จาก adapter เดิม/ความรู้ทั่วไป — รอไฟล์จริงตาม IMPORT_ETL_REVIEW §4)</param>
/// <param name="Evidence">ที่มาของโปรไฟล์ (ข้อความไทย — แสดงใน tooltip/รายงาน)</param>
public sealed record ImportSourceProfile(
    string SourceSystem,
    string EntityType,
    IReadOnlyList<string> Headers,
    IReadOnlyDictionary<string, string?> ColumnMap,
    bool Confirmed,
    string Evidence);

/// <summary>ผลเดาโปรแกรมต้นทาง — <see cref="Score"/> = สัดส่วนหัวของโปรไฟล์ที่พบในไฟล์ (0–1)</summary>
public sealed record ImportSourceMatch(ImportSourceProfile Profile, decimal Score, int MatchedHeaders, string Label);

/// <summary>
/// **โปรไฟล์ cold-start ของไฟล์จากโปรแกรมอื่น — อยู่ในโค้ด ไม่ใช่แถวข้ามบริษัท** (รอบ 205 ทีม A · IMPORT_ETL_REVIEW §2.2/§2.3)
///
/// <para>บริษัทใหม่ที่ยังไม่มีแม่แบบของตัวเองต้องได้การจับคู่ที่ดีตั้งแต่ไฟล์แรก (กฎเหล็ก #1 ข้อ 3 cold-start ไม่ว่าง) ⇒ <see cref="ImportColumnMapper"/>
/// ใช้โปรไฟล์ที่ตรงเป็นชั้นคำพ้อง (ป้าย "📚 คำพ้อง (รูปแบบ X)") · <see cref="Detect"/> เดาโปรแกรมต้นทาง/ชนิดข้อมูลให้ chip บนหน้า</para>
///
/// <para>⚠️ <b>ทุกโปรไฟล์ในตอนนี้ <see cref="ImportSourceProfile.Confirmed"/> = false</b> — ประกอบจากหัวที่ adapter เดิมอ่าน
/// (<c>Migration/CompetitorImportFramework</c> · <c>AccountImportAdapter</c> · <c>ProductImportAdapter</c>) และรูปแบบของ <c>BankCsvParser</c>
/// <b>ยังไม่เคยเทียบกับไฟล์ส่งออกจริง</b> (รอข้อมูลภายนอก §4 ข้อ 1–2) · ได้ไฟล์จริงแล้ว: แก้ Headers/ColumnMap ให้ตรง + ตั้ง Confirmed = true +
/// เพิ่มเทสต์จากไฟล์นั้น · ห้ามติดป้ายผู้ใช้ว่า "ยืนยันแล้ว" จากโปรไฟล์ที่เดา</para>
/// </summary>
public static class ImportSourceSeed
{
    private const string FromAdapter = "เดาจากหัวคอลัมน์ที่ adapter ย้ายระบบเดิมอ่าน — ยังไม่ได้เทียบกับไฟล์ส่งออกจริง (รอไฟล์ตัวอย่าง)";
    private const string FromBankParser = "เดาจากรูปแบบหัวคอลัมน์ของตัวอ่าน statement เดิม (BankCsvParser) — ยังไม่ได้เทียบกับไฟล์จริงของแต่ละธนาคาร";

    private static ImportSourceProfile P(string system, string entity, string evidence, params (string Header, string? Field)[] cols)
        => new(system, entity, cols.Select(c => c.Header).ToList(),
            cols.ToDictionary(c => c.Header, c => c.Field, StringComparer.OrdinalIgnoreCase), Confirmed: false, evidence);

    /// <summary>ทุกโปรไฟล์ (ลำดับคงที่ — ใช้ตัดสินเสมอเมื่อคะแนนเท่ากัน)</summary>
    public static IReadOnlyList<ImportSourceProfile> All { get; } = new[]
    {
        // ── Express (ExpressContactsAdapter: รหัสลูกค้า/ชื่อลูกค้า/เลขประจำตัวผู้เสียภาษี/โทรศัพท์/อีเมล/ที่อยู่) ──
        P("express", "contacts", FromAdapter,
            ("รหัสลูกค้า", null), ("ชื่อลูกค้า", "Name"), ("ที่อยู่", "Address"), ("เลขประจำตัวผู้เสียภาษี", "TaxId"),
            ("สาขา", "BranchCode"), ("โทรศัพท์", "Phone"), ("อีเมล", "Email"), ("ผู้ติดต่อ", "ContactPerson")),
        P("express", "contacts", FromAdapter,
            ("รหัสผู้จัดจำหน่าย", null), ("ชื่อผู้จัดจำหน่าย", "Name"), ("ที่อยู่", "Address"), ("เลขประจำตัวผู้เสียภาษี", "TaxId"),
            ("สาขา", "BranchCode"), ("โทรศัพท์", "Phone"), ("อีเมล", "Email"), ("ผู้ติดต่อ", "ContactPerson")),
        P("express", "products", FromAdapter,
            ("รหัสสินค้า", "Code"), ("ชื่อสินค้า", "Name"), ("หน่วย", "Unit"), ("ราคาขาย", "SellingPrice"), ("ราคาทุน", "CostPrice"),
            ("VAT", "VatRate"), ("หมวด", "Category"), ("บาร์โค้ด", null)),
        P("express", "chart-of-accounts", FromAdapter,
            ("รหัสบัญชี", "AccountCode"), ("ชื่อบัญชี", "AccountName"), ("ชื่ออังกฤษ", "AccountNameEn"), ("ประเภทบัญชี", "AccountType"),
            ("หมายเหตุ", "Description")),

        // ── PEAK (PeakContactsAdapter: contactCode/contactName/taxNumber · products: productCode/productName · CoA: accountCode/accountName) ──
        P("peak", "contacts", FromAdapter,
            ("contactCode", null), ("contactName", "Name"), ("taxNumber", "TaxId"), ("branchCode", "BranchCode"), ("phone", "Phone"),
            ("email", "Email"), ("address", "Address")),
        P("peak", "products", FromAdapter,
            ("productCode", "Code"), ("productName", "Name"), ("unit", "Unit"), ("sellingPrice", "SellingPrice"), ("costPrice", "CostPrice"),
            ("vatRate", "VatRate"), ("category", "Category"), ("barcode", null)),
        P("peak", "chart-of-accounts", FromAdapter,
            ("accountCode", "AccountCode"), ("accountName", "AccountName"), ("accountNameEn", "AccountNameEn"), ("accountType", "AccountType"),
            ("description", "Description")),

        // ── FlowAccount (FlowAccountContactsAdapter: name (TH)/tax id · products: product code/selling price · CoA: account code/account name) ──
        P("flowaccount", "contacts", FromAdapter,
            ("name (TH)", "Name"), ("name (EN)", null), ("tax id", "TaxId"), ("branch", "BranchCode"), ("phone", "Phone"), ("email", "Email"),
            ("address", "Address")),
        P("flowaccount", "products", FromAdapter,
            ("product code", "Code"), ("product name", "Name"), ("unit", "Unit"), ("selling price", "SellingPrice"), ("cost price", "CostPrice"),
            ("vat rate", "VatRate"), ("category", "Category"), ("barcode", null)),
        P("flowaccount", "chart-of-accounts", FromAdapter,
            ("account code", "AccountCode"), ("account name", "AccountName"), ("account name en", "AccountNameEn"), ("account type", "AccountType"),
            ("description", "Description")),

        // ── statement ธนาคารไทยทั่วไป (ไทย/อังกฤษ) → ImportSchemas.BankStatementLines (ทีม C) ──
        P("bank:generic-th", "banktransactions", FromBankParser,
            ("วันที่", "TransactionDate"), ("เวลา", "TransactionTime"), ("รายการ", "Description"), ("ถอนเงิน", "Withdrawal"), ("ฝากเงิน", "Deposit"),
            ("ยอดคงเหลือ", "Balance"), ("ช่องทาง", "Channel"), ("รายละเอียด", "Details")),
        P("bank:generic-th", "banktransactions", FromBankParser,
            ("Date", "TransactionDate"), ("Time", "TransactionTime"), ("Description", "Description"), ("Withdrawal", "Withdrawal"),
            ("Deposit", "Deposit"), ("Balance", "Balance"), ("Channel", "Channel"), ("Details", "Details")),
    };

    /// <summary>
    /// เดาโปรแกรมต้นทางจากชุดหัวตาราง — โปรไฟล์ที่หัวของมันพบในไฟล์ ≥ 60% และอย่างน้อย 3 หัว (เทียบหลัง <see cref="ImportHeaderSignature.Normalize"/>) ·
    /// <paramref name="entityType"/> ≠ null ⇒ เฉพาะโปรไฟล์ของชนิดนั้น · ไม่มีที่ผ่านเกณฑ์ ⇒ null (ไม่เดาลอย ๆ — F2 ข้อ 3)
    /// </summary>
    public static ImportSourceMatch? Detect(string? entityType, IEnumerable<string?> headers)
    {
        var have = new HashSet<string>(headers.Select(ImportHeaderSignature.Normalize).Where(h => h.Length > 0), StringComparer.Ordinal);
        if (have.Count == 0) return null;
        var entityNorm = entityType == null ? null : ImportExportPermissionScope.NormalizeEntityType(entityType);
        ImportSourceMatch? best = null;
        foreach (var p in All)
        {
            if (entityNorm != null && ImportExportPermissionScope.NormalizeEntityType(p.EntityType) != entityNorm) continue;
            var keys = p.Headers.Select(ImportHeaderSignature.Normalize).Distinct(StringComparer.Ordinal).ToList();
            var hit = keys.Count(have.Contains);
            if (hit < 3 || keys.Count == 0) continue;
            var score = Math.Round((decimal)hit / keys.Count, 4, MidpointRounding.AwayFromZero);
            if (score < 0.6m) continue;
            if (best == null || score > best.Score || (score == best.Score && hit > best.MatchedHeaders))
                best = new ImportSourceMatch(p, score, hit, Label(p.SourceSystem));
        }
        return best;
    }

    /// <summary>ชื่อไทย/ยี่ห้อที่แสดงของโปรแกรมต้นทาง · null/ว่าง ⇒ null · ไม่รู้จัก ⇒ คืนคีย์เดิม (ผู้ใช้เลือกเองจากช่องอิสระ)</summary>
    public static string? Label(string? sourceSystem)
    {
        if (string.IsNullOrWhiteSpace(sourceSystem)) return null;
        return sourceSystem.Trim().ToLowerInvariant() switch
        {
            "express" => "Express",
            "peak" => "PEAK",
            "flowaccount" => "FlowAccount",
            "bank:generic-th" => "ไฟล์ธนาคาร (รูปแบบทั่วไป)",
            var s when s.StartsWith("bank:", StringComparison.Ordinal) => "ไฟล์ธนาคาร",
            _ => sourceSystem.Trim(),
        };
    }
}
