using System.Globalization;
using System.Text.Json;

namespace Accounting.Helpers;

/// <summary>
/// <b>ตัวอ่าน TemplateData ของรายการประจำ — ตัวเดียว</b> ที่ทั้งฝั่ง "ตรวจตอนบันทึก" (<c>ValidateTemplateAsync</c>) ·
/// ฝั่ง "สร้างเอกสาร/สมุดรายวันตอนถึงรอบ" (<c>CreateDocumentFromTemplateAsync</c> / <c>CreateJournalFromTemplateAsync</c>) ·
/// และยอดบนหน้ารายการ (<c>ExtractAmount</c>) ใช้ร่วมกัน
///
/// <para><b>ที่มา (ผู้ใช้รายงาน 2026-10-14 + ทีมตรวจรายการประจำ):</b> ฝั่งบันทึกกับฝั่งสร้างอ่าน JSON คนละแบบ —
/// (1) ตัวตรวจเอา <c>bankAccountId</c> (รหัสทะเบียนบัญชีธนาคาร) ไปค้นในผังบัญชี ⇒ เลือกบัญชีธนาคารแล้วบันทึกไม่ได้ทุกใบ ·
/// (2) ตัวตรวจข้ามรหัสบัญชีที่อ่านไม่ออก (<c>TryParse</c>) แต่ตัวสร้างใช้ <c>Guid.Parse</c> ⇒ บันทึกผ่าน แล้วไปล้มตอนเที่ยงคืน ·
/// (3) ตัวสร้างเรียก <c>GetDecimal()</c> ตรง ๆ ⇒ ตัวเลขที่เป็นสตริง ("1,200") หน้าจอโชว์ยอดถูก แต่ถึงรอบแล้วล้มด้วยข้อความเทคนิค ·
/// (4) ตัวตรวจไม่ดูโครงการ/สาขา/ผู้ถูกหักเลย ⇒ cron ล้มแทน</para>
///
/// <para>กติกา: ช่องตัวเลขรับ Number · สตริงตัวเลข (ตัดจุลภาค) · ไม่มี/null = ค่าตั้งต้น — อย่างอื่น "อ่านไม่ได้" ·
/// ช่องรหัสรับ GUID · ไม่มี/null/""/Guid.Empty = ไม่ระบุ — สตริงอื่น "อ่านไม่ได้" · ตัวตรวจรายงานทุกช่องที่อ่านไม่ได้พร้อมชื่อช่องภาษาไทย</para>
/// </summary>
public static class RecurringTemplate
{
    public const string RuleCode = "RECURRING-TEMPLATE";

    /// <summary>ชนิดตารางที่รหัสในเทมเพลตชี้ไป — ตัวตรวจค้นแต่ละชนิดใน "ตารางที่ถูก" (ห้ามรวมถังกัน · บั๊ก 2026-10-14)</summary>
    public enum RefKind { ChartOfAccount, BankAccount, Project, Branch, WhtPayeeContact }

    /// <summary>รหัสหนึ่งตัวในเทมเพลต + ช่องที่มันมา (ใช้ในข้อความถึงผู้ใช้ — ห้ามโชว์ GUID)</summary>
    public sealed record Ref(RefKind Kind, Guid Id, string Field);

    /// <summary>ผลอ่านเทมเพลตทั้งก้อน: รหัสทุกตัวแยกตามตาราง + ปัญหารูปแบบ (ข้อความไทยพร้อมบรรทัด/ช่อง)</summary>
    public sealed record Scan(IReadOnlyList<Ref> Refs, IReadOnlyList<string> Problems, int LineCount);

    /// <summary>ตัวเลขของช่อง <paramref name="name"/> — null = มีค่าแต่อ่านไม่ได้</summary>
    public static decimal? TryNumber(JsonElement obj, string name, decimal fallback)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el)) return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDecimal(out var d) ? d : null,
            JsonValueKind.Null or JsonValueKind.Undefined => fallback,
            JsonValueKind.String => string.IsNullOrWhiteSpace(el.GetString())
                ? fallback
                : decimal.TryParse((el.GetString() ?? "").Replace(",", "").Trim(), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var s) ? s : null,
            _ => null,
        };
    }

    /// <summary>ตัวเลขของช่องสำหรับ "ตอนสร้างเอกสาร" — อ่านไม่ได้ ⇒ โยน <see cref="BusinessRuleException"/> ระบุบรรทัด/ช่อง
    /// (ไม่ใช่ InvalidOperationException ของ System.Text.Json ที่ผู้ใช้อ่านไม่รู้เรื่อง)</summary>
    public static decimal Number(JsonElement obj, string name, decimal fallback, int lineNo)
        => TryNumber(obj, name, fallback) ?? throw new BusinessRuleException(
            $"รายการประจำ: บรรทัดที่ {lineNo} ช่อง \"{FieldLabel(name)}\" ไม่ใช่ตัวเลข — เปิดแก้ไขรายการประจำแล้วกรอกใหม่", RuleCode);

    /// <summary>รหัส GUID ของช่อง — false = มีค่าแต่อ่านไม่ได้ · ไม่มี/null/""/Guid.Empty ⇒ true + null</summary>
    public static bool TryGuid(JsonElement obj, string name, out Guid? value)
    {
        value = null;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el)) return true;
        if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return true;
        if (el.ValueKind != JsonValueKind.String) return false;
        var s = el.GetString();
        if (string.IsNullOrWhiteSpace(s)) return true;
        if (!Guid.TryParse(s, out var g)) return false;
        value = g == Guid.Empty ? null : g;
        return true;
    }

    /// <summary>รหัสของช่องสำหรับ "ตอนสร้างเอกสาร" — อ่านไม่ได้ ⇒ โยนพร้อมชื่อช่อง (เดิม <c>Guid.Parse</c> ⇒ FormatException ตอนเที่ยงคืน)</summary>
    public static Guid? ReadGuid(JsonElement obj, string name, int lineNo)
        => TryGuid(obj, name, out var v) ? v : throw new BusinessRuleException(
            $"รายการประจำ: {(lineNo > 0 ? $"บรรทัดที่ {lineNo} " : "")}ช่อง \"{FieldLabel(name)}\" อ่านไม่ได้ — เปิดแก้ไขรายการประจำแล้วเลือกใหม่", RuleCode);

    /// <summary>ข้อความของช่อง — สตริง = ค่านั้น · ตัวเลข = ข้อความของตัวเลข · อย่างอื่น = null (เดิม GetString() บนตัวเลขโยน)</summary>
    public static string? Text(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null,
        };
    }

    /// <summary>อ่านทั้งเทมเพลต: รหัสทุกตัวแยกตามตารางที่ถูก + ปัญหารูปแบบทุกข้อ (ตัวตรวจตอนบันทึก/ตอนถึงรอบเรียกตัวนี้ตัวเดียว)</summary>
    public static Scan Read(JsonElement root, bool isJournal)
    {
        var refs = new List<Ref>();
        var problems = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            problems.Add("รูปแบบรายละเอียดรายการประจำไม่ถูกต้อง (ต้องเป็นออบเจ็กต์ JSON)");
            return new Scan(refs, problems, 0);
        }

        void AddRef(JsonElement obj, string name, RefKind kind, int lineNo)
        {
            var where = lineNo > 0 ? $"บรรทัดที่ {lineNo} ช่อง \"{FieldLabel(name)}\"" : $"ช่อง \"{FieldLabel(name)}\"";
            if (!TryGuid(obj, name, out var id)) { problems.Add($"{where} อ่านไม่ได้"); return; }
            if (id.HasValue) refs.Add(new Ref(kind, id.Value, where));
        }

        var lineCount = 0;
        if (root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in linesEl.EnumerateArray())
            {
                lineCount++;
                if (line.ValueKind != JsonValueKind.Object)
                {
                    problems.Add($"บรรทัดที่ {lineCount} รูปแบบไม่ถูกต้อง");
                    continue;
                }
                AddRef(line, "accountId", RefKind.ChartOfAccount, lineCount);
                var numbers = isJournal ? JournalNumberFields : DocumentNumberFields;
                foreach (var f in numbers)
                    if (TryNumber(line, f, 0m) == null) problems.Add($"บรรทัดที่ {lineCount} ช่อง \"{FieldLabel(f)}\" ไม่ใช่ตัวเลข");
                if (isJournal)
                {
                    if (TryGuid(line, "accountId", out var jid) && jid == null)
                        problems.Add($"บรรทัดที่ {lineCount} ยังไม่ได้เลือกบัญชี");
                }
                else
                {
                    AddRef(line, "projectId", RefKind.Project, lineCount);
                    AddRef(line, "whtPayeeContactId", RefKind.WhtPayeeContact, lineCount);
                }
            }
        }
        else if (root.TryGetProperty("lines", out var badLines) && badLines.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            problems.Add("ช่องบรรทัดรายการต้องเป็นรายการ (array)");
        }

        if (!isJournal)
        {
            AddRef(root, "bankAccountId", RefKind.BankAccount, 0);
            AddRef(root, "paymentAccountId", RefKind.ChartOfAccount, 0);
            AddRef(root, "expenseCategoryId", RefKind.ChartOfAccount, 0);
            AddRef(root, "projectId", RefKind.Project, 0);
            AddRef(root, "branchId", RefKind.Branch, 0);
            AddRef(root, "whtPayeeContactId", RefKind.WhtPayeeContact, 0);
        }
        return new Scan(refs, problems, lineCount);
    }

    private static readonly string[] DocumentNumberFields =
        { "quantity", "unitPrice", "discountPercent", "discountAmount", "vatRate", "withholdingTaxRate" };
    private static readonly string[] JournalNumberFields = { "debitAmount", "creditAmount" };

    /// <summary>ชื่อช่องภาษาไทย (ข้อความถึงผู้ใช้ห้ามมีชื่อ key/GUID)</summary>
    private static string FieldLabel(string name) => name switch
    {
        "accountId" => "บัญชี",
        "quantity" => "จำนวน",
        "unitPrice" => "ราคาต่อหน่วย",
        "discountPercent" => "ส่วนลด (%)",
        "discountAmount" => "ส่วนลด (บาท)",
        "vatRate" => "VAT (%)",
        "withholdingTaxRate" => "หัก ณ ที่จ่าย (%)",
        "debitAmount" => "เดบิต",
        "creditAmount" => "เครดิต",
        "bankAccountId" => "ช่องทางรับ/จ่ายเงิน (บัญชีธนาคาร)",
        "paymentAccountId" => "ช่องทางรับ/จ่ายเงิน",
        "expenseCategoryId" => "หมวดหมู่ค่าใช้จ่าย",
        "projectId" => "โครงการ",
        "branchId" => "สาขา",
        "whtPayeeContactId" => "ผู้ถูกหักภาษี",
        _ => name,
    };
}
