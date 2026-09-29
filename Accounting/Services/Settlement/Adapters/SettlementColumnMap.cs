using System.Text.Json;
using System.Text.Json.Serialization;
using Accounting.Helpers;
using Accounting.Models.Enums;

namespace Accounting.Services.Settlement.Adapters;

/// <summary>รูปแบบตารางของไฟล์</summary>
public enum SettlementFileLayout
{
    /// <summary>1 แถว = 1 รายการ (มีคอลัมน์ประเภท + คอลัมน์ยอด)</summary>
    Long = 1,
    /// <summary>1 แถว = 1 ออเดอร์ · ยอดแต่ละประเภทอยู่คนละคอลัมน์ (ยอดขาย · ค่าคอม · ค่าธรรมเนียม ...)</summary>
    Wide = 2,
}

/// <summary>คอลัมน์ยอดเงิน 1 คอลัมน์ของไฟล์แบบกว้าง</summary>
public sealed class SettlementAmountColumn
{
    /// <summary>หัวคอลัมน์ในไฟล์ (เทียบแบบไม่สนตัวพิมพ์/ช่องว่างซ้ำ)</summary>
    public string Header { get; set; } = "";
    /// <summary>ประเภทที่ผู้ใช้กำหนดให้คอลัมน์นี้ (ชื่อ enum) — null = ให้ตัวจัดประเภทตัดสินจากหัวคอลัมน์</summary>
    public string? Type { get; set; }
    /// <summary>กลับเครื่องหมาย (ไฟล์แสดงยอดที่ถูกหักเป็นบวก)</summary>
    public bool Negate { get; set; }
    /// <summary>ยอดในคอลัมน์นี้ยังไม่รวม VAT — ระบบคิด VAT 7% ทับผ่าน <c>SettlementFeeTax.FromExclusive</c></summary>
    public bool VatExclusive { get; set; }
}

/// <summary>
/// **การจับคู่คอลัมน์ของไฟล์ settlement (เก็บใน <c>SettlementChannel.ColumnMapJson</c> · ระบบจำต่อช่องทาง)**
///
/// <para>อ้างคอลัมน์ด้วย "หัวคอลัมน์" ไม่ใช่ลำดับ — แพลตฟอร์มสลับ/เพิ่มคอลัมน์บ่อย · หัวที่จับคู่ไว้หายจากไฟล์ = รูปแบบเปลี่ยน ⇒
/// ล้มดังให้จับคู่ใหม่ (ไม่อ่านคอลัมน์ผิดตัวเงียบ) · ไฟล์แบบกว้างมีคอลัมน์ใหม่ที่ไม่อยู่ทั้งในการจับคู่และรายการ "ไม่ใช้" ⇒
/// ล้มดังเช่นกัน (คอลัมน์ใหม่มักเป็นค่าธรรมเนียมใหม่ — ข้ามเงียบ = ยอดหาย)</para>
/// </summary>
public sealed class SettlementColumnMap
{
    public int Version { get; set; } = 1;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SettlementFileLayout Layout { get; set; } = SettlementFileLayout.Long;

    public string? TxnId { get; set; }
    public string? OrderId { get; set; }
    public string? Date { get; set; }
    /// <summary>คอลัมน์ป้ายประเภท (แบบยาว — บังคับ)</summary>
    public string? Type { get; set; }
    public string? Description { get; set; }
    /// <summary>ยอดมีเครื่องหมาย (แบบยาว) — หรือใช้คู่ <see cref="AmountIn"/>/<see cref="AmountOut"/></summary>
    public string? Amount { get; set; }
    /// <summary>ยอดเข้า wallet (บวก)</summary>
    public string? AmountIn { get; set; }
    /// <summary>ยอดออกจาก wallet (ค่าบวกในไฟล์ ระบบกลับเป็นลบ)</summary>
    public string? AmountOut { get; set; }
    public string? Vat { get; set; }
    public string? Wht { get; set; }
    public string? PayoutRef { get; set; }
    /// <summary>แบบยาว: กลับเครื่องหมายของ <see cref="Amount"/></summary>
    public bool Negate { get; set; }
    /// <summary>แบบยาว: ยอดยังไม่รวม VAT (คิดทับ 7% ผ่าน <c>SettlementFeeTax.FromExclusive</c>)</summary>
    public bool VatExclusive { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SettlementDateOrder DateOrder { get; set; } = SettlementDateOrder.Auto;
    /// <summary>เขตเวลาของวันที่+เวลาที่ไม่มี offset ในไฟล์ (review198-B R-B8 · ทีม I รอบ 200) — <c>Auto</c> = ดูจากหัวคอลัมน์/ข้อมูล
    /// (ตัดสินไม่ได้และมีผลต่อวันที่ ⇒ ล้มดังให้เลือก) · ระบบจำค่าที่ไฟล์พิสูจน์ได้ให้ช่องทาง (<see cref="Learn"/>)</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SettlementFileTimeZone TimeZone { get; set; } = SettlementFileTimeZone.Auto;

    /// <summary>แบบกว้าง: คอลัมน์ยอดเงิน</summary>
    public List<SettlementAmountColumn> AmountColumns { get; set; } = new();
    /// <summary>หัวคอลัมน์ที่ผู้ใช้เลือก "ไม่ใช้" (ชื่อผู้ซื้อ · ที่อยู่ · เบอร์ ฯลฯ) — แบบกว้างใช้ตรวจคอลัมน์ใหม่</summary>
    public List<string> Ignore { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>อ่าน JSON — ว่าง ⇒ null (ยังไม่เคยจับคู่) · JSON พัง ⇒ ล้มดัง (ห้ามอ่านไฟล์ด้วยการจับคู่ครึ่งเดียว)</summary>
    public static SettlementColumnMap? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<SettlementColumnMap>(json, Options)
                   ?? throw new SettlementFormatException("column-map-invalid", "การจับคู่คอลัมน์ว่าง — จับคู่คอลัมน์ใหม่");
        }
        catch (JsonException ex)
        {
            throw new SettlementFormatException("column-map-invalid",
                $"การจับคู่คอลัมน์ที่บันทึกไว้อ่านไม่ได้ ({ex.Message}) — จับคู่คอลัมน์ใหม่แล้วนำเข้าอีกครั้ง");
        }
    }

    /// <summary>ปัญหาของการจับคู่ (ว่าง = ใช้ได้) — ข้อความไทยพร้อมทางไปต่อ</summary>
    public IReadOnlyList<string> Validate()
    {
        var p = new List<string>();
        if (Version != 1) p.Add($"การจับคู่คอลัมน์รุ่น {Version} ไม่รองรับ — จับคู่คอลัมน์ใหม่");
        if (Layout == SettlementFileLayout.Long)
        {
            if (string.IsNullOrWhiteSpace(Type))
                p.Add("ยังไม่ได้เลือกคอลัมน์ \"ประเภทรายการ\" — ระบบต้องรู้ว่าแต่ละแถวเป็นยอดขาย/ค่าธรรมเนียม/คืนเงิน");
            var hasSigned = !string.IsNullOrWhiteSpace(Amount);
            var hasPair = !string.IsNullOrWhiteSpace(AmountIn) || !string.IsNullOrWhiteSpace(AmountOut);
            if (!hasSigned && !hasPair) p.Add("ยังไม่ได้เลือกคอลัมน์ยอดเงิน (ยอดมีเครื่องหมาย หรือ ยอดเข้า/ยอดออก)");
            if (hasSigned && hasPair) p.Add("เลือกคอลัมน์ยอดเงินได้แบบเดียว — ยอดมีเครื่องหมาย หรือ ยอดเข้า/ยอดออก");
            if (AmountColumns.Count > 0) p.Add("ไฟล์แบบยาวไม่ใช้รายการคอลัมน์ยอดเงินหลายคอลัมน์ — เปลี่ยนเป็นแบบกว้าง หรือล้างรายการ");
        }
        else
        {
            if (AmountColumns.Count == 0) p.Add("ไฟล์แบบกว้างต้องเลือกคอลัมน์ยอดเงินอย่างน้อย 1 คอลัมน์");
            if (string.IsNullOrWhiteSpace(OrderId) && string.IsNullOrWhiteSpace(TxnId))
                p.Add("ไฟล์แบบกว้างต้องมีคอลัมน์เลขออเดอร์หรือเลขรายการ (ใช้กันนำเข้าซ้ำ)");
            foreach (var c in AmountColumns)
            {
                if (string.IsNullOrWhiteSpace(c.Header)) p.Add("คอลัมน์ยอดเงินรายการหนึ่งไม่มีชื่อหัวคอลัมน์");
                if (c.Type != null && ParseType(c.Type) == null)
                    p.Add($"คอลัมน์ \"{c.Header}\" กำหนดประเภท \"{c.Type}\" ที่ลงบัญชีไม่ได้ — เลือกประเภทใหม่ หรือเว้นว่างให้ระบบจัด");
            }
            var dup = AmountColumns.GroupBy(c => NormalizeHeader(c.Header)).FirstOrDefault(g => g.Key.Length > 0 && g.Count() > 1);
            if (dup != null) p.Add($"คอลัมน์ \"{dup.First().Header}\" ถูกเลือกเป็นยอดเงินซ้ำ");
        }
        return p;
    }

    /// <summary>
    /// **จำสิ่งที่ไฟล์พิสูจน์แล้ว** (R-B9: "บันทึกลำดับวัน/เดือนเมื่อไฟล์พิสูจน์ได้") — เติมเฉพาะช่องที่ยังเป็น <c>Auto</c> (ค่าที่ผู้ใช้เลือกเองชนะเสมอ) ·
    /// คืนข้อความแจ้งผู้ใช้ต่อค่าที่จำ (ไม่จำเงียบ — ผิดแล้วแก้ได้ที่หน้าจับคู่คอลัมน์)
    /// </summary>
    public IReadOnlyList<string> Learn(SettlementDateOrder? provenOrder, SettlementFileTimeZone? provenZone)
    {
        var notes = new List<string>();
        if (DateOrder == SettlementDateOrder.Auto && provenOrder is SettlementDateOrder o && o != SettlementDateOrder.Auto)
        {
            DateOrder = o;
            notes.Add($"ระบบจำรูปแบบวันที่ \"{(o == SettlementDateOrder.MonthDayYear ? "เดือน/วัน/ปี" : "วัน/เดือน/ปี")}\" ไว้กับช่องทางนี้แล้ว "
                + "(พิสูจน์จากวันที่ในไฟล์หรือช่วงวันที่ของรอบโอน) — ถ้าไม่ถูก แก้ได้ที่ขั้นจับคู่คอลัมน์");
        }
        if (TimeZone == SettlementFileTimeZone.Auto && provenZone is SettlementFileTimeZone z && z != SettlementFileTimeZone.Auto)
        {
            TimeZone = z;
            notes.Add($"ระบบจำเขตเวลาของไฟล์ \"{(z == SettlementFileTimeZone.Utc ? "UTC" : "เวลาไทย")}\" ไว้กับช่องทางนี้แล้ว "
                + "(ดูจากหัวคอลัมน์วันที่) — ถ้าไม่ถูก แก้ได้ที่ขั้นจับคู่คอลัมน์");
        }
        return notes;
    }

    /// <summary>ประเภทที่ผู้ใช้กำหนดต่อคอลัมน์ — รับเฉพาะชื่อที่ลงบัญชีได้ (ด่านเดียวกับคำตอบตัวจัดประเภท)</summary>
    internal static SettlementLineType? ParseType(string? name) => SettlementLineTypeRules.ParseClassifierAnswer(name);

    /// <summary>หัวคอลัมน์ที่การจับคู่นี้อ้างถึง (ต้องมีในไฟล์)</summary>
    public IEnumerable<string> ReferencedHeaders()
    {
        foreach (var h in new[] { TxnId, OrderId, Date, Type, Description, Amount, AmountIn, AmountOut, Vat, Wht, PayoutRef })
            if (!string.IsNullOrWhiteSpace(h)) yield return h!;
        foreach (var c in AmountColumns)
            if (!string.IsNullOrWhiteSpace(c.Header)) yield return c.Header;
    }

    /// <summary>หัวคอลัมน์แบบเทียบได้: ตัดช่องว่างหัวท้าย · ยุบช่องว่างซ้ำ · ตัวพิมพ์เล็ก · ตัด BOM/เครื่องหมายคำพูด</summary>
    public static string NormalizeHeader(string? h)
    {
        if (string.IsNullOrWhiteSpace(h)) return "";
        var s = h.Replace("﻿", "", StringComparison.Ordinal).Trim().Trim('"').Trim();
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }
}
