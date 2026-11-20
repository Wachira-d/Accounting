using System.Text.Json;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// <b>ตัวตนผู้ถูกหัก ณ วันออก 50 ทวิ — ตัวเดียว</b> (รอบ 275 แพ็ก B · T2 M4 ครึ่งหลัง · คำตัดสิน 275-B-6 (ข))
///
/// <para>ที่มา: 50 ทวิ ไม่มีสำเนาชื่อ/เลข/สาขา/ที่อยู่ผู้ถูกหัก — อ่านผู้ติดต่อสดทุกครั้ง ⇒ แก้/รวมผู้ติดต่อภายหลังเปลี่ยนหน้าใบที่ออก/ยื่นไปแล้ว
/// (ทั้งกระดาษ HTML + QuestPDF · หน้าพิมพ์ · รายงาน/ไฟล์ ภ.ง.ด.) ทั้งที่ใบอยู่ในมือผู้ถูกหักแล้ว</para>
/// <para>กติกา: ประทับครั้งเดียวเมื่อใบเข้าสถานะ "ออกแล้ว" (<see cref="WhtCertFilingScope.Filed"/>) ที่ <c>AccountingDbContext.SaveChanges</c>
/// (ทุกทางสร้าง/ออกใบผ่านจุดนี้ — มือ · อัตโนมัติ · เงินเดือน · ทิป · คู่ค้า) · เปลี่ยนผู้ถูกหักของใบ ⇒ ประทับใหม่ · ใบร่าง = ผู้ติดต่อสด ·
/// ใบเก่าเติมจากผู้ติดต่อตอน migration (<see cref="BackfillSql"/>) · ผู้อ่านทุกตัวเรียก <see cref="Of"/> — ห้ามอ่าน <c>cert.PayeeContact.Name/TaxId/…</c> เอง</para>
/// </summary>
public static class WhtPayeeSnapshot
{
    public sealed record Snapshot(string? Title, string? Name, string? TaxId, string? BranchCode, string? BranchName,
        string? Address, string? BuildingNumber, string? BuildingName, string? Moo, string? StreetName,
        string? SubDistrict, string? District, string? Province, string? PostalCode);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string Serialize(Contact c) => JsonSerializer.Serialize(new Snapshot(c.TitleTh, c.Name, c.TaxId, c.BranchCode, c.BranchName,
        c.Address, c.BuildingNumber, c.BuildingName, c.Moo, c.StreetName, c.SubDistrict, c.District, c.Province, c.PostalCode));

    /// <summary>ใบนี้ควรมีสำเนาไหม (ออกแล้ว/พิมพ์แล้ว)</summary>
    public static bool ShouldStamp(WithholdingTaxCertStatus status) => WhtCertFilingScope.Filed.Contains(status);

    /// <summary>ผู้ถูกหักที่ใช้แสดง/ยื่น — สำเนา ณ วันออก ถ้ามี · ไม่มี (ร่าง · ใบเก่าที่ backfill ไม่ได้) ⇒ ผู้ติดต่อสด ·
    /// คืน <see cref="Contact"/> ใหม่ที่ไม่ผูกกับ DbContext (ห้ามนำไปตั้งเป็น navigation) · ช่องที่ไม่อยู่ในสำเนา (ชนิด/ประเทศ) มาจากผู้ติดต่อสด</summary>
    public static Contact? Of(WithholdingTaxCert cert)
    {
        var live = cert.PayeeContact;
        if (string.IsNullOrWhiteSpace(cert.PayeeSnapshotJson)) return live;
        Snapshot? s;
        try { s = JsonSerializer.Deserialize<Snapshot>(cert.PayeeSnapshotJson!, Json); }
        catch (JsonException) { return live; }   // สำเนาเสีย = ใช้ผู้ติดต่อสด (ข้อมูลเดิม) — ไม่แต่งค่า
        if (s == null) return live;
        return new Contact
        {
            Id = cert.PayeeContactId, CompanyId = cert.CompanyId,
            TitleTh = s.Title, Name = s.Name ?? live?.Name ?? "", TaxId = s.TaxId, BranchCode = s.BranchCode, BranchName = s.BranchName,
            Address = s.Address, BuildingNumber = s.BuildingNumber, BuildingName = s.BuildingName, Moo = s.Moo, StreetName = s.StreetName,
            SubDistrict = s.SubDistrict, District = s.District, Province = s.Province, PostalCode = s.PostalCode,
            ContactType = live?.ContactType ?? default, CountryCode = live?.CountryCode ?? "TH",
        };
    }

    /// <summary>เติมสำเนาให้ 50 ทวิ ที่ไม่ใช่ร่างซึ่งยังไม่มีสำเนา จากผู้ติดต่อปัจจุบัน (ตอน migration — ใบเก่าออกก่อนมีคอลัมน์) · idempotent</summary>
    public const string BackfillSql = """
        UPDATE "WithholdingTaxCerts" w SET "PayeeSnapshotJson" = json_build_object(
            'Title', c."TitleTh", 'Name', c."Name", 'TaxId', c."TaxId", 'BranchCode', c."BranchCode", 'BranchName', c."BranchName",
            'Address', c."Address", 'BuildingNumber', c."BuildingNumber", 'BuildingName', c."BuildingName", 'Moo', c."Moo",
            'StreetName', c."StreetName", 'SubDistrict', c."SubDistrict", 'District', c."District", 'Province', c."Province",
            'PostalCode', c."PostalCode")::text
        FROM "Contacts" c
        WHERE c."Id" = w."PayeeContactId" AND c."CompanyId" = w."CompanyId"
          AND w."PayeeSnapshotJson" IS NULL AND w."Status" <> 0;
        """;
}
