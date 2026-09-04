using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace Accounting.Services.Implementations.Security;

/// <summary>
/// **ที่เก็บ key ring ของ DataProtection — ในฐานข้อมูล ไม่ใช่ดิสก์ของแต่ละเครื่อง**
///
/// ═══ ที่มา (ผลตรวจ F-06) ═══
/// เดิม <c>PersistKeysToFileSystem("./.dpkeys")</c> ⇒ <b>key ring แยกต่อ instance</b>
/// ผลเมื่อขึ้นเครื่องที่สอง:
/// <list type="bullet">
///   <item>PII ที่เครื่อง A เข้ารหัส (เลขบัตรประชาชน · เลขบัญชี · ลายเซ็น)
///     เครื่อง B <b>ถอดไม่ออก</b> — และ <c>PiiProtector.TryDecrypt</c> คืน
///     <c>null</c> <b>เงียบ ๆ</b> ⇒ ผู้ใช้เห็นช่องว่าง สลับไปมาตามเครื่องที่
///     load balancer ส่งไป โดยไม่มี error ให้ตามรอย</item>
///   <item>pod restart บนคอนเทนเนอร์ที่ไม่ได้ mount volume = <b>คีย์หายถาวร</b>
///     ⇒ ข้อมูลที่เข้ารหัสไว้กู้ไม่ได้เลย (ไม่ใช่แค่ "อ่านไม่ได้ชั่วคราว")</item>
/// </list>
///
/// <para>เก็บลงตารางกลางทำให้ทุกเครื่องเห็นคีย์ชุดเดียวกันโดยไม่ต้องพึ่ง volume
/// ที่แชร์กันได้หรือ KMS ภายนอก — และรอด pod restart โดยอัตโนมัติ</para>
///
/// <para>⚠️ ตารางนี้เก็บ<b>กุญแจถอดรหัส PII</b> — สิทธิ์อ่านต้องเท่ากับสิทธิ์
/// อ่านฐานข้อมูลทั้งก้อนอยู่แล้ว (ถ้าผู้โจมตีอ่านตารางนี้ได้ แปลว่าเขาอ่าน
/// ตารางที่ถูกเข้ารหัสได้อยู่แล้วเช่นกัน) · ขั้นถัดไปถ้าต้องการแยกชั้นจริง ๆ
/// คือ <c>ProtectKeysWithCertificate</c>/KMS ซึ่งวางทับตัวนี้ได้โดยไม่ต้องย้ายที่เก็บ</para>
///
/// <para>ใช้ ADO.NET ตรง ๆ ไม่ผ่าน EF เพราะ DataProtection ถูกสร้างตอน
/// <c>builder.Build()</c> ซึ่งเร็วกว่าที่ DbContext จะพร้อม และมันอ่านคีย์แบบ
/// <b>synchronous</b> — เรียก EF async จากตรงนั้นเสี่ยง deadlock</para>
/// </summary>
public sealed class DbXmlRepository : IXmlRepository
{
    private readonly string _connectionString;
    private readonly ILogger<DbXmlRepository>? _logger;

    public const string TableName = "DataProtectionKeys";

    public DbXmlRepository(string connectionString, ILogger<DbXmlRepository>? logger = null)
    {
        _connectionString = connectionString;
        _logger = logger;
        EnsureTable();
    }

    private void EnsureTable()
    {
        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"CREATE TABLE IF NOT EXISTS ""{TableName}"" (
                ""Id"" bigserial PRIMARY KEY,
                ""FriendlyName"" text NULL,
                ""Xml"" text NOT NULL,
                ""CreatedAt"" timestamptz NOT NULL DEFAULT now())";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // ล้มตรงนี้ = อ่าน/เขียนคีย์ไม่ได้ ⇒ PII ทั้งระบบใช้ไม่ได้
            // ต้องดัง ไม่ใช่ปล่อยให้ค้นพบตอนผู้ใช้เปิดหน้าพนักงานแล้วเห็นช่องว่าง
            _logger?.LogError(ex, "สร้างตารางเก็บคีย์ DataProtection ไม่สำเร็จ — PII จะเข้ารหัส/ถอดไม่ได้");
            throw;
        }
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var result = new List<XElement>();
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"SELECT ""Xml"" FROM ""{TableName}"" ORDER BY ""Id""";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var xml = reader.GetString(0);
            try { result.Add(XElement.Parse(xml)); }
            catch (Exception ex)
            {
                // แถวเดียวเสียต้องไม่ทำให้คีย์ที่เหลืออ่านไม่ได้ทั้งชุด
                _logger?.LogWarning(ex, "ข้ามคีย์ DataProtection ที่ parse ไม่ได้ 1 แถว");
            }
        }
        return result;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"INSERT INTO ""{TableName}"" (""FriendlyName"", ""Xml"") VALUES (@n, @x)";
        cmd.Parameters.AddWithValue("n", (object?)friendlyName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("x", element.ToString(SaveOptions.DisableFormatting));
        cmd.ExecuteNonQuery();
    }
}
