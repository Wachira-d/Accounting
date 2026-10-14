namespace Accounting.Services.Import;

/// <summary>
/// **ตัวเขียนแถวเดียวของ writer เดิม** (รอบ 205 T0 · IMPORT_ETL_REVIEW §2.4) — สกัด switch ที่ซ้ำสองชุดใน <c>ImportExportService</c>
/// (<c>ImportAsync</c> :49-100 และ <c>ConfirmSmartImportAsync</c> :2290-2341) เหลือตัวเดียว · master-data target ของทีม B เรียกตัวนี้
///
/// <para>เจ้าของ implementation: <b>ทีม B</b> (<see cref="ImportRowWriter"/>) · ค่าใน <see cref="ImportTargetRow.Values"/> เป็นรูปมาตรฐานแล้ว
/// (วันที่ yyyy-MM-dd · ยอด invariant · flag true/false) ⇒ writer ห้าม parse ตาม culture อีก · ไม่ SaveChanges เว้นแต่
/// <see cref="ImportRowWriteOptions.SaveChanges"/> (SharedTransaction ให้ pipeline คุม)</para>
/// </summary>
public interface IImportRowWriter
{
    /// <summary>เขียนหนึ่งแถว — ล้ม ⇒ <c>Success = false</c> + ข้อความไทย (ไม่ throw กับ business error · ข้อผิดพลาดของระบบ throw ตามปกติ)</summary>
    Task<ImportRowCommitResult> WriteRowAsync(Guid companyId, string entityType, ImportTargetRow row, ImportRowWriteOptions options,
        string performedBy, CancellationToken ct = default);
}

/// <summary>ตัวเลือกการเขียน</summary>
/// <param name="ConflictAction">พฤติกรรมเดิมของ writer เมื่อชนของเดิม ("Skip" · "Overwrite" · "Merge") — null = ตาม <see cref="ImportTargetRow.Action"/></param>
/// <param name="SaveChanges">true = writer เรียก SaveChanges เอง (OwnTransactionPerGroup) · false = pipeline คุม (SharedTransaction)</param>
public sealed record ImportRowWriteOptions(string? ConflictAction = null, bool SaveChanges = false);
