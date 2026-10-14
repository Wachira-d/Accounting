using Accounting.Helpers;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.Enums;

namespace Accounting.Services.Helpers;

/// <summary>
/// **อ่านไฟล์ statement ธนาคาร — ตัวเลือกตัวแยกไฟล์ตัวเดียว** (รอบ 205 ทีม C) ของหน้า "ธนาคาร → นำเข้า statement" และ open-banking file
///
/// <para>ตัดสินชนิดจาก<b>เนื้อไฟล์</b> (<see cref="ImportTabularReader.Sniff"/>) ไม่ใช่ชนิดที่ผู้ใช้เลือก/นามสกุล — ธนาคารไทยหลายแห่งส่งออก
/// ".xls" ที่จริงเป็นข้อความคั่นแท็บ · ผู้ใช้เลือก "CSV" แต่แนบ .xlsx: .xlsx → <see cref="BankExcelParser"/> (เลือกชีตได้) ·
/// CSV/TSV → <see cref="BankCsvParser"/> (UTF-8/TIS-620) · OFX → <see cref="BankOfxParser"/> · QIF → ล้มดัง <see cref="QifMessage"/>
/// (คำตัดสินข้อ 151) · .xls (BIFF8)/HTML/PDF/XML/JSON → ข้อความไทยพร้อมทางไปต่อของตัวอ่านกลาง</para>
/// </summary>
public static class BankStatementFileReader
{
    /// <summary>ข้อความของ QIF (คำตัดสินข้อ 151 (4)) — ตัวเดียวที่ validator/ตัวอ่านใช้</summary>
    public const string QifMessage = "ยังไม่รองรับไฟล์ QIF — ส่งออก statement จากธนาคาร/โปรแกรมบัญชีเป็น CSV หรือ OFX แล้วนำเข้าใหม่";

    /// <summary>ผลอ่านไฟล์ statement</summary>
    /// <param name="Notes">ข้อสังเกตถึงผู้ใช้ (ชีตที่อ่าน · encoding · แถวที่ข้าม · TRNTYPE ขัดเครื่องหมาย) — ส่งต่อเป็นคำเตือนของการนำเข้า</param>
    public sealed record Parsed(
        List<BankCsvParser.Row> Rows,
        List<string> Skipped,
        ImportFileKind Kind,
        string? Encoding,
        string? SheetName,
        IReadOnlyList<string> SheetNames,
        decimal? ClosingBalance,
        DateTime? ClosingBalanceDate,
        string? SourceAccountNumber,
        string? Currency,
        List<string> Notes);

    public static Parsed Read(string? declaredFormat, string? fileName, byte[] bytes, string? sheetName = null)
    {
        if (string.Equals(declaredFormat?.Trim(), "QIF", StringComparison.OrdinalIgnoreCase))
            throw new ImportFormatException("qif-unsupported", QifMessage);
        if (bytes.Length == 0)
            throw new ImportFormatException("empty-file", "ไฟล์ว่างเปล่า — ส่งออก statement จากธนาคารใหม่แล้วอัปโหลดอีกครั้ง");
        if (bytes.LongLength > ImportTabularReader.MaxFileBytes)
            throw new ImportFormatException("file-too-large",
                $"ไฟล์ใหญ่เกิน {ImportTabularReader.MaxFileBytes / (1024 * 1024)} MB — ส่งออก statement ช่วงวันที่สั้นลงแล้วนำเข้าทีละไฟล์");

        var kind = ImportTabularReader.Sniff(fileName, bytes);
        var notes = new List<string>();
        switch (kind)
        {
            case ImportFileKind.Xlsx:
            {
                var wb = BankExcelParser.ParseWorkbook(bytes, sheetName);
                if (wb.SheetNames.Count > 1 && wb.SheetName != null)
                    notes.Add($"ℹ️ อ่านจากชีต \"{wb.SheetName}\" (ไฟล์มี {wb.SheetNames.Count} ชีต: {string.Join(", ", wb.SheetNames)})");
                AddSkippedNote(wb.Skipped, notes);
                return new Parsed(wb.Rows, wb.Skipped, kind, null, wb.SheetName, wb.SheetNames, null, null, null, null, notes);
            }
            case ImportFileKind.Csv:
            case ImportFileKind.Tsv:
            {
                var encoding = ImportTabularReader.Open(fileName, bytes).Encoding;
                var (rows, skipped) = BankCsvParser.ParseStatement(bytes);
                if (encoding == "windows-874") notes.Add("ℹ️ อ่านไฟล์เป็นภาษาไทยรหัส TIS-620 (Windows-874)");
                AddSkippedNote(skipped, notes);
                return new Parsed(rows, skipped, kind, encoding, null, Array.Empty<string>(), null, null, null, null, notes);
            }
            case ImportFileKind.Ofx:
            {
                var ofx = BankOfxParser.Parse(bytes);
                notes.AddRange(ofx.Notes);
                AddSkippedNote(ofx.Skipped, notes);
                return new Parsed(ofx.Rows, ofx.Skipped, kind, null, null, Array.Empty<string>(), ofx.LedgerBalance,
                    ofx.LedgerBalanceDate, ofx.AccountId, ofx.Currency, notes);
            }
            case ImportFileKind.Qif:
                throw new ImportFormatException("qif-unsupported", QifMessage);
            default:
                // .xls BIFF8 · HTML ปลอมเป็น xls · PDF · XML · JSON · ไบนารีไม่รู้จัก — ข้อความไทยพร้อมทางไปต่อของตัวอ่านกลาง (ตัวเดียว)
                ImportTabularReader.Open(fileName, bytes);
                throw new ImportFormatException("unsupported-type", "ไม่รู้จักชนิดไฟล์นี้ — รองรับ CSV · Excel (.xlsx) · OFX");
        }
    }

    /// <summary>แถวที่อ่านไม่ได้ต้องถึงผู้ใช้ (เดิมเห็นเฉพาะเมื่อทั้งไฟล์อ่านไม่ได้เลย) — ตัวอย่าง 3 แถว</summary>
    private static void AddSkippedNote(List<string> skipped, List<string> notes)
    {
        if (skipped.Count == 0) return;
        notes.Add($"⚠ ข้าม {skipped.Count} แถวที่อ่านไม่ได้ (ตัวอย่าง: {string.Join("; ", skipped.Take(3))})");
    }

    /// <summary>บรรทัดของตัวเขียน (ลำดับเดียวกับไฟล์)</summary>
    public static List<BankStatementLineInput> ToLines(Parsed parsed)
        => parsed.Rows.Select(r => new BankStatementLineInput(r.Date, r.Description, r.Withdrawal, r.Deposit, r.Balance,
            r.Reference, r.Channel, r.ExternalId, r.Payee, r.RowNumber)).ToList();

    /// <summary>ตัวเลือกของตัวเขียนจากสิ่งที่ไฟล์บอก (ยอดปิด OFX · เลขบัญชี/สกุลตามไฟล์ · ข้อสังเกตของตัวอ่าน)</summary>
    public static BankStatementWriteOptions WriteOptions(Parsed parsed, BankStatementConflictPolicy onConflict, BankStatementSourceFile? sourceFile)
        => new(OnConflict: onConflict, SourceFile: sourceFile,
            ClosingBalance: parsed.ClosingBalance, ClosingBalanceDate: parsed.ClosingBalanceDate,
            SourceAccountNumber: parsed.SourceAccountNumber, SourceCurrency: parsed.Currency, ParserNotes: parsed.Notes);

    /// <summary>ไฟล์ต้นทางที่จะเก็บ — ชื่อ/ชนิดที่ระบบไฟล์แนบรับ (OFX/TSV เก็บเป็น .txt · ไฟล์ "xls" ที่จริงเป็นข้อความ = .txt)</summary>
    public static BankStatementSourceFile SourceFile(Parsed parsed, string? fileName, byte[] bytes)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "statement" : Path.GetFileName(fileName.Trim());
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (parsed.Kind == ImportFileKind.Xlsx)
            return new(ext == ".xlsx" ? name : name + ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", bytes);
        if ((parsed.Kind is ImportFileKind.Csv or ImportFileKind.Tsv) && (ext is ".csv" or ".txt"))
            return new(name, "text/csv", bytes);
        return new(name + ".txt", "text/plain", bytes);
    }
}
