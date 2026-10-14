using System.Security.Cryptography;
using System.Text;
using Accounting.Services.Settlement.Adapters;

namespace Accounting.Helpers;

/// <summary>
/// **ลายมือชื่อของชุดหัวตาราง + ตัว normalize หัวคอลัมน์ตัวเดียว** (รอบ 205 ทีม A · IMPORT_ETL_REVIEW §2.3)
///
/// <para>ทุกที่ที่เทียบ "หัวคอลัมน์ในไฟล์" กับคำพ้อง/แม่แบบ/คู่ที่เรียนไว้ (<see cref="ImportColumnMapper"/> · <c>ImportMappingTemplateStore</c> ·
/// <c>ImportColumnMatchDistillationModel</c> · <see cref="ImportSourceSeed"/>) normalize ผ่าน <see cref="Normalize"/> ตัวเดียว ⇒
/// "ชื่อ ลูกค้า" · "ชื่อลูกค้า*" · "ชื่อ_ลูกค้า" · "ชื่อลูกค้า (Name)" เป็นคีย์เดียวกัน · ห้ามเขียน ToLower/Replace ของตัวเองอีกที่
/// (เดิมมีสามชุด: <c>ImportExportService.NormalizeHeader</c> · <c>AiSuggestionController</c> · adapter โปรแกรมอื่น)</para>
///
/// <para><see cref="Compute"/> = SHA-256 ของชุดหัวที่ normalize แล้ว <b>เรียงแล้ว + ไม่ซ้ำ + ไม่ว่าง</b> ⇒ ไม่ขึ้นกับลำดับคอลัมน์ (ผู้ใช้สลับคอลัมน์
/// ในไฟล์รอบหน้า แม่แบบเดิมยังตรง) · คีย์ของ <c>ImportMappingTemplates (CompanyId, EntityType, HeaderSignature)</c></para>
///
/// <para>G6: pure · ไม่ throw · ไม่ขึ้นกับ culture</para>
/// </summary>
public static class ImportHeaderSignature
{
    /// <summary>อักขระที่ตัดทิ้ง — ช่องว่าง/ขีด/จุด/วงเล็บ/เครื่องหมายบังคับ (*) /ทับ/โคลอน · ZWSP/BOM/NBSP</summary>
    private static readonly HashSet<char> Drop = new()
    {
        ' ', '\t', '\r', '\n', '_', '-', '.', '(', ')', '[', ']', '{', '}', '*', ':', '/', '\\', '#', '"', '\'', ',', ';', '|',
        ' ', '​', '‌', '‍', '﻿', '–', '—',
    };

    /// <summary>
    /// หัวคอลัมน์ → คีย์เทียบ: ตัวเล็ก (invariant) · เลขไทยเป็นอารบิก · ตัดช่องว่าง/ขีด/จุด/วงเล็บ/ * /ทับ/อักขระล่องหน ·
    /// null/ว่าง ⇒ "" (ผู้เรียกข้ามคอลัมน์ไม่มีหัว)
    /// </summary>
    public static string Normalize(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return "";
        var ascii = SettlementValueParser.ThaiDigitsToAscii(header);
        var sb = new StringBuilder(ascii.Length);
        foreach (var ch in ascii)
        {
            if (Drop.Contains(ch) || char.IsControl(ch)) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>SHA-256 (hex ตัวเล็ก 64 ตัว) ของชุดหัวตาราง — ไม่ขึ้นกับลำดับ · ตัดหัวว่าง/ซ้ำ · ชุดว่าง ⇒ hash ของสตริงว่าง (คงที่)</summary>
    public static string Compute(IEnumerable<string?> headers)
    {
        var keys = headers.Select(Normalize).Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", keys)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
