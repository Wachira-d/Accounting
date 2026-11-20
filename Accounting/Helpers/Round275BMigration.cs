namespace Accounting.Helpers;

/// <summary>
/// รอบ 275 แพ็ก B (T2 H2 · M4 ครึ่งหลัง · ฝ่ายค้าน LOW · คำตัดสิน 275-B-4/275-B-6 (ข)) — ทุกคำสั่ง idempotent:
/// <list type="bullet">
/// <item><c>TaxReportLines.WhtCertId</c> + เติมย้อนหลัง<b>เฉพาะบรรทัดที่จับคู่ได้แน่นอน</b> (<see cref="BackfillLineCertSql"/>) — จับคู่ไม่ได้ = ปล่อย null
/// (ลายเซ็นใช้คีย์เอกสาร · ตัวคืนติ๊ก <see cref="RegenerateUntick"/> รองรับ null)</item>
/// <item><c>WithholdingTaxCerts.PayeeSnapshotJson</c> + เติมจากผู้ติดต่อให้ใบที่ไม่ใช่ร่าง (<see cref="WhtPayeeSnapshot.BackfillSql"/>)</item>
/// </list>
/// </summary>
public static class Round275BMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "TaxReportLines" ADD COLUMN IF NOT EXISTS "WhtCertId" uuid NULL;""",
        """ALTER TABLE "WithholdingTaxCerts" ADD COLUMN IF NOT EXISTS "PayeeSnapshotJson" text NULL;""",
        BackfillLineCertSql,
        WhtPayeeSnapshot.BackfillSql,
    };

    private static string Q(string s) => s.Replace("'", "''");

    /// <summary>บรรทัดรายงาน ภ.ง.ด.2/3/53/54 ที่ <c>GenerateWhtReport</c> สร้างจากใบ — คำอธิบายรูปแบบเดียวของตัวสร้าง
    /// (<c>เลขใบ</c> · <c>เลขใบ — รายการ</c> · มีคำนำหน้า e-Withholding · แถวใบร่าง · คำนำหน้าเอกสารยกเลิก) + บริษัท/แบบ/งวด/เอกสารตรงกับใบ ·
    /// เลขใบไม่ซ้ำต่อบริษัท ⇒ จับคู่ได้ไม่เกินหนึ่งใบ</summary>
    public static readonly string BackfillLineCertSql = $"""
        WITH l AS (
            SELECT tl."Id", tl."DocumentId", r."CompanyId", r."TaxType", r."Year", r."Month",
                   CASE WHEN left(tl."Description", {WhtReportLineRule.VoidedDocumentPrefix.Length}) = '{Q(WhtReportLineRule.VoidedDocumentPrefix)}'
                        THEN substr(tl."Description", {WhtReportLineRule.VoidedDocumentPrefix.Length + 1}) ELSE tl."Description" END AS d
            FROM "TaxReportLines" tl JOIN "TaxReports" r ON r."Id" = tl."TaxReportId"
            WHERE tl."WhtCertId" IS NULL AND tl."Description" IS NOT NULL AND r."TaxType" IN (2, 3, 8, 11)
        ), l2 AS (
            SELECT l.*, CASE WHEN left(l.d, {EWithholdingScope.ReportMarker.Length + 3}) = '{Q(EWithholdingScope.ReportMarker)} — '
                             THEN substr(l.d, {EWithholdingScope.ReportMarker.Length + 4}) ELSE l.d END AS d2
            FROM l
        )
        UPDATE "TaxReportLines" t SET "WhtCertId" = c."Id"
        FROM l2 JOIN "WithholdingTaxCerts" c
          ON c."CompanyId" = l2."CompanyId" AND c."TaxFormType" = l2."TaxType" AND c."TaxYear" = l2."Year" AND c."TaxMonth" = l2."Month"
         AND c."DocumentId" IS NOT DISTINCT FROM l2."DocumentId"
         AND (l2.d2 = c."CertificateNumber"
              OR left(l2.d2, length(c."CertificateNumber") + 3) = c."CertificateNumber" || ' — '
              OR l2.d = '{Q(WhtUnissuedCertGate.DraftCertMarker)} — ' || c."CertificateNumber" || ' (ออกใบก่อนยื่น)')
        WHERE t."Id" = l2."Id";
        """;
}
