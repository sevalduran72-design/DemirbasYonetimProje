
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using QRCoder;

namespace DemirbasYonetimProje.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class DemirbasController : ControllerBase
    {
        private static readonly string[] IzinliDurumlar =
        {
            "Kullanilabilir",
            "BostaBekleyen",
            "Zimmetli",
            "Arizali",
            "Bakimda"
        };

        private readonly IConfiguration _configuration;

        public DemirbasController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        private async Task<SqlConnection> BaglantiAcAsync()
        {
            var connectionString =
                _configuration.GetConnectionString("DefaultConnection");

            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await EnsureDemirbasSchemaAsync(connection);
            await NormalizeLegacyAssignmentColumnsAsync(connection);
            await NormalizeLegacyQrCodesAsync(connection);
            return connection;
        }

        private async Task EnsureDemirbasSchemaAsync(SqlConnection connection)
        {
            await EnsureTableExistsAsync(connection);
            await EnsureColumnExistsAsync(connection, "SeriNumarasi", "NVARCHAR(100)");
            await EnsureColumnExistsAsync(connection, "SatinAlinmaTarihi", "DATE");
            await EnsureColumnExistsAsync(connection, "GarantiBitisTarihi", "DATE");
            await EnsureColumnExistsAsync(connection, "QrKodu", "NVARCHAR(200)");
            await EnsureColumnExistsAsync(connection, "BarkodKodu", "NVARCHAR(200)");
            await EnsureZimmetSchemaAsync(connection);
        }

        private async Task EnsureZimmetSchemaAsync(SqlConnection connection)
        {
            await EnsureDepartmanlarTableAsync(connection);
            await EnsurePersonellerTableAsync(connection);
            await EnsureZimmetlerTableAsync(connection);
            await EnsureDemirbasHareketleriTableAsync(connection);
            await EnsureVarsayilanDepartmanAsync(connection);
            await EnsureVarsayilanPersonelAsync(connection);
        }

        private async Task EnsureDepartmanlarTableAsync(SqlConnection connection)
        {
            const string createSql = @"
                IF OBJECT_ID(N'dbo.Departmanlar', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.Departmanlar (
                        DepartmanId INT IDENTITY(1,1) PRIMARY KEY,
                        Ad NVARCHAR(150) NOT NULL UNIQUE,
                        OlusturulmaTarihi DATETIME2 NOT NULL DEFAULT SYSDATETIME()
                    );
                END;";

            await using (var command = new SqlCommand(createSql, connection))
            {
                await command.ExecuteNonQueryAsync();
            }

            if (!await ColumnExistsAsync(connection, "dbo.Departmanlar", "Ad"))
            {
                if (await ColumnExistsAsync(connection, "dbo.Departmanlar", "DepartmanAdi"))
                {
                    await using var rename = new SqlCommand(
                        "EXEC sp_rename N'dbo.Departmanlar.DepartmanAdi', N'Ad', N'COLUMN';",
                        connection);
                    await rename.ExecuteNonQueryAsync();
                }
                else
                {
                    await using var add = new SqlCommand(
                        "ALTER TABLE dbo.Departmanlar ADD Ad NVARCHAR(150) NULL;",
                        connection);
                    await add.ExecuteNonQueryAsync();
                }
            }

            if (await ColumnExistsAsync(connection, "dbo.Departmanlar", "DepartmanAdi"))
            {
                await using var normalize = new SqlCommand(
                    @"UPDATE dbo.Departmanlar
                      SET Ad = COALESCE(Ad, DepartmanAdi)
                      WHERE Ad IS NULL AND DepartmanAdi IS NOT NULL;",
                    connection);
                await normalize.ExecuteNonQueryAsync();
            }
        }

        private async Task EnsurePersonellerTableAsync(SqlConnection connection)
        {
            const string sql = @"
                IF OBJECT_ID(N'dbo.Personeller', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.Personeller (
                        PersonelId INT IDENTITY(1,1) PRIMARY KEY,
                        AdSoyad NVARCHAR(200) NOT NULL,
                        DepartmanId INT NULL,
                        Eposta NVARCHAR(200) NULL,
                        Telefon NVARCHAR(50) NULL,
                        OlusturulmaTarihi DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        CONSTRAINT FK_Personeller_Departmanlar FOREIGN KEY (DepartmanId)
                            REFERENCES dbo.Departmanlar(DepartmanId)
                    );
                END;";

            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();

            const string userPersonelLinkSql = @"
                IF OBJECT_ID(N'dbo.UygulamaKullanicilari', N'U') IS NOT NULL
                   AND COL_LENGTH(N'dbo.UygulamaKullanicilari', N'PersonelId') IS NULL
                    ALTER TABLE dbo.UygulamaKullanicilari ADD PersonelId INT NULL;

                IF OBJECT_ID(N'dbo.UygulamaKullanicilari', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'UX_UygulamaKullanicilari_PersonelId'
                         AND object_id = OBJECT_ID(N'dbo.UygulamaKullanicilari'))
                    CREATE UNIQUE INDEX UX_UygulamaKullanicilari_PersonelId
                    ON dbo.UygulamaKullanicilari(PersonelId)
                    WHERE PersonelId IS NOT NULL;

                IF OBJECT_ID(N'dbo.UygulamaKullanicilari', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.foreign_keys
                       WHERE name = N'FK_UygulamaKullanicilari_Personeller')
                    ALTER TABLE dbo.UygulamaKullanicilari
                    ADD CONSTRAINT FK_UygulamaKullanicilari_Personeller
                    FOREIGN KEY (PersonelId)
                    REFERENCES dbo.Personeller(PersonelId);";

            await using var userPersonelLinkCommand =
                new SqlCommand(userPersonelLinkSql, connection);
            await userPersonelLinkCommand.ExecuteNonQueryAsync();
        }

        private async Task EnsureZimmetlerTableAsync(SqlConnection connection)
        {
            const string sql = @"
                IF OBJECT_ID(N'dbo.Zimmetler', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.Zimmetler (
                        ZimmetId INT IDENTITY(1,1) PRIMARY KEY,
                        DemirbasId INT NOT NULL,
                        PersonelId INT NOT NULL,
                        DepartmanId INT NULL,
                        ZimmetTarihi DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        IadeTarihi DATETIME2 NULL,
                        Aciklama NVARCHAR(500) NULL,
                        Durum NVARCHAR(30) NOT NULL DEFAULT N'Aktif',
                        CONSTRAINT FK_Zimmetler_Demirbaslar FOREIGN KEY (DemirbasId)
                            REFERENCES dbo.Demirbaslar(DemirbasId),
                        CONSTRAINT FK_Zimmetler_Personeller FOREIGN KEY (PersonelId)
                            REFERENCES dbo.Personeller(PersonelId),
                        CONSTRAINT FK_Zimmetler_Departmanlar FOREIGN KEY (DepartmanId)
                            REFERENCES dbo.Departmanlar(DepartmanId)
                    );
                END;";

            await using (var command = new SqlCommand(sql, connection))
            {
                await command.ExecuteNonQueryAsync();
            }

            var hasLegacyKullaniciId = await ColumnExistsAsync(connection, "dbo.Zimmetler", "KullaniciId");
            if (!await ColumnExistsAsync(connection, "dbo.Zimmetler", "PersonelId"))
            {
                if (hasLegacyKullaniciId)
                {
                    await using var addPersonelId = new SqlCommand(
                        "ALTER TABLE dbo.Zimmetler ADD PersonelId INT NULL;",
                        connection);
                    await addPersonelId.ExecuteNonQueryAsync();

                    await using var migratePersonel = new SqlCommand(
                        "UPDATE dbo.Zimmetler SET PersonelId = KullaniciId WHERE PersonelId IS NULL AND KullaniciId IS NOT NULL;",
                        connection);
                    await migratePersonel.ExecuteNonQueryAsync();
                }
                else
                {
                    await using var addFallback = new SqlCommand(
                        "ALTER TABLE dbo.Zimmetler ADD PersonelId INT NOT NULL DEFAULT 1;",
                        connection);
                    await addFallback.ExecuteNonQueryAsync();
                }
            }

            if (hasLegacyKullaniciId)
            {
                await using var normalizeLegacyPersonel = new SqlCommand(
                    @"UPDATE dbo.Zimmetler
                      SET KullaniciId = COALESCE(PersonelId, KullaniciId)
                      WHERE PersonelId IS NOT NULL AND KullaniciId IS NULL;",
                    connection);
                await normalizeLegacyPersonel.ExecuteNonQueryAsync();

                await using var allowNullLegacyPersonel = new SqlCommand(
                    "ALTER TABLE dbo.Zimmetler ALTER COLUMN KullaniciId INT NULL;",
                    connection);
                await allowNullLegacyPersonel.ExecuteNonQueryAsync();
            }

            await EnsureColumnExistsAsync(connection, "dbo.Zimmetler", "DepartmanId", "INT");
            await EnsureColumnExistsAsync(connection, "dbo.Zimmetler", "ZimmetTarihi", "DATETIME2");
            await EnsureColumnExistsAsync(connection, "dbo.Zimmetler", "IadeTarihi", "DATETIME2");
            await EnsureColumnExistsAsync(connection, "dbo.Zimmetler", "Aciklama", "NVARCHAR(500)");
            await EnsureColumnExistsAsync(connection, "dbo.Zimmetler", "Durum", "NVARCHAR(30)");
        }

        private async Task EnsureDemirbasHareketleriTableAsync(SqlConnection connection)
        {
            const string sql = @"
                IF OBJECT_ID(N'dbo.DemirbasHareketleri', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.DemirbasHareketleri (
                        HareketId INT IDENTITY(1,1) PRIMARY KEY,
                        DemirbasId INT NOT NULL,
                        PersonelId INT NULL,
                        IslemTuru NVARCHAR(50) NOT NULL,
                        Aciklama NVARCHAR(500) NULL,
                        IslemTarihi DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        Konum NVARCHAR(200) NULL,
                        CONSTRAINT FK_DemirbasHareketleri_Demirbaslar FOREIGN KEY (DemirbasId)
                            REFERENCES dbo.Demirbaslar(DemirbasId),
                        CONSTRAINT FK_DemirbasHareketleri_Personeller FOREIGN KEY (PersonelId)
                            REFERENCES dbo.Personeller(PersonelId)
                    );
                END;";

            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();

            var hasLegacyKullaniciId = await ColumnExistsAsync(connection, "dbo.DemirbasHareketleri", "KullaniciId");
            if (!await ColumnExistsAsync(connection, "dbo.DemirbasHareketleri", "PersonelId"))
            {
                if (hasLegacyKullaniciId)
                {
                    await using var addPersonelId = new SqlCommand(
                        "ALTER TABLE dbo.DemirbasHareketleri ADD PersonelId INT NULL;",
                        connection);
                    await addPersonelId.ExecuteNonQueryAsync();

                    await using var migratePersonel = new SqlCommand(
                        "UPDATE dbo.DemirbasHareketleri SET PersonelId = KullaniciId WHERE PersonelId IS NULL AND KullaniciId IS NOT NULL;",
                        connection);
                    await migratePersonel.ExecuteNonQueryAsync();
                }
                else
                {
                    await using var addFallback = new SqlCommand(
                        "ALTER TABLE dbo.DemirbasHareketleri ADD PersonelId INT NULL;",
                        connection);
                    await addFallback.ExecuteNonQueryAsync();
                }
            }

            if (!await ColumnExistsAsync(connection, "dbo.DemirbasHareketleri", "Aciklama"))
            {
                if (await ColumnExistsAsync(connection, "dbo.DemirbasHareketleri", "IslemAciklamasi"))
                {
                    await using var addAciklama = new SqlCommand(
                        "ALTER TABLE dbo.DemirbasHareketleri ADD Aciklama NVARCHAR(500) NULL;",
                        connection);
                    await addAciklama.ExecuteNonQueryAsync();

                    await using var migrateAciklama = new SqlCommand(
                        "UPDATE dbo.DemirbasHareketleri SET Aciklama = IslemAciklamasi WHERE Aciklama IS NULL AND IslemAciklamasi IS NOT NULL;",
                        connection);
                    await migrateAciklama.ExecuteNonQueryAsync();
                }
                else
                {
                    await using var addFallback = new SqlCommand(
                        "ALTER TABLE dbo.DemirbasHareketleri ADD Aciklama NVARCHAR(500) NULL;",
                        connection);
                    await addFallback.ExecuteNonQueryAsync();
                }
            }

            if (!await ColumnExistsAsync(connection, "dbo.DemirbasHareketleri", "Konum"))
            {
                await using var addKonum = new SqlCommand(
                    "ALTER TABLE dbo.DemirbasHareketleri ADD Konum NVARCHAR(200) NULL;",
                    connection);
                await addKonum.ExecuteNonQueryAsync();
            }

            if (hasLegacyKullaniciId)
            {
                await using var normalizeLegacyPersonel = new SqlCommand(
                    @"UPDATE dbo.DemirbasHareketleri
                      SET KullaniciId = COALESCE(PersonelId, KullaniciId)
                      WHERE PersonelId IS NOT NULL AND KullaniciId IS NULL;",
                    connection);
                await normalizeLegacyPersonel.ExecuteNonQueryAsync();

                await using var allowNullLegacyPersonel = new SqlCommand(
                    "ALTER TABLE dbo.DemirbasHareketleri ALTER COLUMN KullaniciId INT NULL;",
                    connection);
                await allowNullLegacyPersonel.ExecuteNonQueryAsync();
            }
        }

        private async Task EnsureVarsayilanDepartmanAsync(SqlConnection connection)
        {
            const string sql = @"
                IF NOT EXISTS (SELECT 1 FROM dbo.Departmanlar)
                BEGIN
                    INSERT INTO dbo.Departmanlar (Ad)
                    VALUES (N'Genel');
                END;";

            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        private async Task EnsureVarsayilanPersonelAsync(SqlConnection connection)
        {
            const string sql = @"
                IF NOT EXISTS (SELECT 1 FROM dbo.Personeller)
                BEGIN
                    DECLARE @DepartmanId INT;
                    SELECT @DepartmanId = DepartmanId FROM dbo.Departmanlar WHERE Ad = N'Genel';
                    INSERT INTO dbo.Personeller (AdSoyad, DepartmanId)
                    VALUES (N'Atanmamış', @DepartmanId);
                END;";

            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        private async Task NormalizeLegacyAssignmentColumnsAsync(SqlConnection connection)
        {
            if (await ColumnExistsAsync(connection, "dbo.Zimmetler", "KullaniciId"))
            {
                await using var relaxZimmetler = new SqlCommand(
                    "ALTER TABLE dbo.Zimmetler ALTER COLUMN KullaniciId INT NULL;",
                    connection);
                await relaxZimmetler.ExecuteNonQueryAsync();

                await using var syncZimmetler = new SqlCommand(
                    "UPDATE dbo.Zimmetler SET KullaniciId = PersonelId WHERE KullaniciId IS NULL AND PersonelId IS NOT NULL;",
                    connection);
                await syncZimmetler.ExecuteNonQueryAsync();
            }

            if (await ColumnExistsAsync(connection, "dbo.DemirbasHareketleri", "KullaniciId"))
            {
                await using var relaxHareketler = new SqlCommand(
                    "ALTER TABLE dbo.DemirbasHareketleri ALTER COLUMN KullaniciId INT NULL;",
                    connection);
                await relaxHareketler.ExecuteNonQueryAsync();

                await using var syncHareketler = new SqlCommand(
                    "UPDATE dbo.DemirbasHareketleri SET KullaniciId = PersonelId WHERE KullaniciId IS NULL AND PersonelId IS NOT NULL;",
                    connection);
                await syncHareketler.ExecuteNonQueryAsync();
            }
        }

        private async Task NormalizeLegacyQrCodesAsync(SqlConnection connection)
        {
            const string selectSql = @"
                SELECT DemirbasId, QrKodu, BarkodKodu
                FROM dbo.Demirbaslar
                WHERE QrKodu IS NULL
                   OR LTRIM(RTRIM(QrKodu)) = N''
                   OR QrKodu = N'DMB-000000'
                   OR QrKodu LIKE N'DMB-%-000000';";

            await using var selectCommand = new SqlCommand(selectSql, connection);
            await using var reader = await selectCommand.ExecuteReaderAsync();

            var updates = new List<(int Id, string QrKodu, string? BarkodKodu)>();
            while (await reader.ReadAsync())
            {
                var id = reader.GetInt32(0);
                var mevcutQr = reader.IsDBNull(1) ? null : reader.GetString(1);
                var mevcutBarkod = reader.IsDBNull(2) ? null : reader.GetString(2);

                var qrKodu = string.IsNullOrWhiteSpace(mevcutQr) || mevcutQr == "DMB-000000" || mevcutQr.EndsWith("-000000", StringComparison.OrdinalIgnoreCase)
                    ? GenerateAssetCode(id)
                    : mevcutQr.Trim();

                var barkodKodu = string.IsNullOrWhiteSpace(mevcutBarkod)
                    ? qrKodu
                    : mevcutBarkod.Trim();

                updates.Add((id, qrKodu, barkodKodu));
            }

            await reader.CloseAsync();

            foreach (var item in updates)
            {
                const string updateSql = @"
                    UPDATE dbo.Demirbaslar
                    SET QrKodu = @QrKodu,
                        BarkodKodu = @BarkodKodu
                    WHERE DemirbasId = @Id;";

                await using var updateCommand = new SqlCommand(updateSql, connection);
                updateCommand.Parameters.AddWithValue("@Id", item.Id);
                updateCommand.Parameters.AddWithValue("@QrKodu", item.QrKodu);
                updateCommand.Parameters.AddWithValue("@BarkodKodu", item.BarkodKodu ?? (object)DBNull.Value);
                await updateCommand.ExecuteNonQueryAsync();
            }
        }

        private async Task EnsureTableExistsAsync(SqlConnection connection)
        {
            const string sql = @"
                IF OBJECT_ID(N'dbo.Demirbaslar', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.Demirbaslar (
                        DemirbasId INT IDENTITY(1,1) PRIMARY KEY,
                        DemirbasAdi NVARCHAR(150) NOT NULL,
                        Kategori NVARCHAR(100) NOT NULL,
                        Marka NVARCHAR(100) NULL,
                        Model NVARCHAR(100) NULL,
                        Durum NVARCHAR(30) NOT NULL DEFAULT 'Kullanilabilir',
                        SeriNumarasi NVARCHAR(100) NULL,
                        SatinAlinmaTarihi DATE NULL,
                        QrKodu NVARCHAR(200) NULL,
                        BarkodKodu NVARCHAR(200) NULL,
                        OlusturulmaTarihi DATETIME2 NOT NULL DEFAULT SYSDATETIME()
                    );
                END;";

            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        private async Task<bool> ColumnExistsAsync(
            SqlConnection connection,
            string tableName,
            string columnName)
        {
            var normalizedTableName = tableName.Trim();
            var schemaName = "dbo";
            var baseTableName = normalizedTableName;

            if (normalizedTableName.Contains('.'))
            {
                var parts = normalizedTableName.Split('.', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    schemaName = parts[0].Replace("[", string.Empty).Replace("]", string.Empty);
                    baseTableName = parts[1].Replace("[", string.Empty).Replace("]", string.Empty);
                }
            }
            else
            {
                baseTableName = normalizedTableName.Replace("[", string.Empty).Replace("]", string.Empty);
            }

            const string sql = @"
                SELECT COUNT(*)
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = @TableSchema
                  AND TABLE_NAME = @TableName
                  AND COLUMN_NAME = @ColumnName;";

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@TableSchema", schemaName);
            command.Parameters.AddWithValue("@TableName", baseTableName);
            command.Parameters.AddWithValue("@ColumnName", columnName);

            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        }

        private async Task EnsureColumnExistsAsync(
            SqlConnection connection,
            string tableName,
            string columnName,
            string columnType)
        {
            if (await ColumnExistsAsync(connection, tableName, columnName))
            {
                return;
            }

            var addSql = $"ALTER TABLE {tableName} ADD {columnName} {columnType};";
            await using var alter = new SqlCommand(addSql, connection);
            await alter.ExecuteNonQueryAsync();
        }

        private async Task EnsureColumnExistsAsync(
            SqlConnection connection,
            string columnName,
            string columnType)
        {
            await EnsureColumnExistsAsync(connection, "dbo.Demirbaslar", columnName, columnType);
        }

        private static string? ReadNullableString(SqlDataReader reader, int index)
        {
            if (reader.IsDBNull(index))
            {
                return null;
            }

            var value = reader.GetValue(index);
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }

        private static string GenerateAssetCode(int demirbasId)
        {
            return $"DMB-{DateTime.UtcNow:yyyyMMdd}-{demirbasId:000000}";
        }

        private static bool TryParseDate(string? value, out DateTime parsed)
        {
            parsed = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
                out parsed);
        }

        // Demirbaşları listele
        [HttpGet]
        public async Task<IActionResult> Getir()
        {
            var liste = new List<object>();

            await using var connection = await BaglantiAcAsync();

            var sql = @"
                SELECT d.DemirbasId,
                       d.DemirbasAdi,
                       d.Kategori,
                       d.Marka,
                       d.Model,
                       d.Durum,
                       d.SeriNumarasi,
                       d.SatinAlinmaTarihi,
                       d.QrKodu,
                       d.BarkodKodu,
                       z.PersonelId,
                       p.AdSoyad AS PersonelAdi,
                       z.DepartmanId,
                       dep.Ad AS DepartmanAdi,
                       z.ZimmetId,
                       z.ZimmetTarihi,
                       z.Aciklama AS ZimmetAciklama,
                       d.GarantiBitisTarihi
                FROM dbo.Demirbaslar AS d
                LEFT JOIN dbo.Zimmetler AS z
                    ON z.DemirbasId = d.DemirbasId
                   AND z.Durum = N'Aktif'
                LEFT JOIN dbo.Personeller AS p
                    ON p.PersonelId = z.PersonelId
                LEFT JOIN dbo.Departmanlar AS dep
                    ON dep.DepartmanId = z.DepartmanId
                ORDER BY d.DemirbasId DESC;";

            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                liste.Add(new
                {
                    DemirbasId = reader.GetInt32(0),
                    DemirbasAdi = reader.GetString(1),
                    Kategori = reader.GetString(2),
                    Marka = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Model = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Durum = reader.GetString(5),
                    SeriNumarasi = reader.IsDBNull(6) ? null : reader.GetString(6),
                    SatinAlinmaTarihi = reader.IsDBNull(7) ? null : (DateTime?)reader.GetDateTime(7),
                    QrKodu = reader.IsDBNull(8) ? null : reader.GetString(8),
                    BarkodKodu = reader.IsDBNull(9) ? null : reader.GetString(9),
                    PersonelId = reader.IsDBNull(10) ? null : (int?)reader.GetInt32(10),
                    PersonelAdi = reader.IsDBNull(11) ? null : reader.GetString(11),
                    DepartmanId = reader.IsDBNull(12) ? null : (int?)reader.GetInt32(12),
                    DepartmanAdi = reader.IsDBNull(13) ? null : reader.GetString(13),
                    ZimmetId = reader.IsDBNull(14) ? null : (int?)reader.GetInt32(14),
                    ZimmetTarihi = reader.IsDBNull(15) ? null : (DateTime?)reader.GetDateTime(15),
                    ZimmetAciklama = reader.IsDBNull(16) ? null : reader.GetString(16),
                    GarantiBitisTarihi = reader.IsDBNull(17) ? null : (DateTime?)reader.GetDateTime(17)
                });
            }

            return Ok(liste);
        }

        [HttpGet("qr/{kod}")]
        public async Task<IActionResult> QrIleGetir(string kod)
        {
            if (string.IsNullOrWhiteSpace(kod))
            {
                return BadRequest(new { mesaj = "QR/barkod değeri boş olamaz." });
            }

            await using var connection = await BaglantiAcAsync();
            const string sql = @"
                SELECT TOP 1 d.DemirbasId,
                       d.DemirbasAdi,
                       d.Kategori,
                       d.Marka,
                       d.Model,
                       d.Durum,
                       d.SeriNumarasi,
                       d.SatinAlinmaTarihi,
                       d.GarantiBitisTarihi,
                       d.QrKodu,
                       d.BarkodKodu,
                       p.AdSoyad AS PersonelAdi,
                       dep.Ad AS DepartmanAdi
                FROM dbo.Demirbaslar AS d
                LEFT JOIN dbo.Zimmetler AS z
                    ON z.DemirbasId = d.DemirbasId
                   AND z.Durum = N'Aktif'
                LEFT JOIN dbo.Personeller AS p
                    ON p.PersonelId = z.PersonelId
                LEFT JOIN dbo.Departmanlar AS dep
                    ON dep.DepartmanId = z.DepartmanId
                WHERE d.QrKodu = @Kod OR d.BarkodKodu = @Kod;";

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Kod", System.Data.SqlDbType.NVarChar, 200)
                .Value = kod.Trim();
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return NotFound(new { mesaj = "Bu QR/barkod ile eşleşen demirbaş bulunamadı." });
            }

            return Ok(new
            {
                DemirbasId = reader.GetInt32(0),
                DemirbasAdi = reader.GetString(1),
                Kategori = reader.GetString(2),
                Marka = ReadNullableString(reader, 3),
                Model = ReadNullableString(reader, 4),
                Durum = reader.GetString(5),
                SeriNumarasi = ReadNullableString(reader, 6),
                SatinAlinmaTarihi = reader.IsDBNull(7) ? null : (DateTime?)reader.GetDateTime(7),
                GarantiBitisTarihi = reader.IsDBNull(8) ? null : (DateTime?)reader.GetDateTime(8),
                QrKodu = ReadNullableString(reader, 9),
                BarkodKodu = ReadNullableString(reader, 10),
                PersonelAdi = ReadNullableString(reader, 11),
                DepartmanAdi = ReadNullableString(reader, 12)
            });
        }

        [HttpGet("qr-gorsel/{kod}")]
        public IActionResult QrGorseliniGetir(string kod)
        {
            if (string.IsNullOrWhiteSpace(kod) || kod.Length > 200)
            {
                return BadRequest(new { mesaj = "Geçerli bir QR kodu girin." });
            }

            Response.Headers.CacheControl = "private, no-store";
            return Content(QrSvgOlustur(kod), "image/svg+xml", System.Text.Encoding.UTF8);
        }

        [HttpGet("qr-indir/{kod}")]
        public IActionResult QrDosyasiniIndir(string kod)
        {
            if (string.IsNullOrWhiteSpace(kod) || kod.Length > 200)
            {
                return BadRequest(new { mesaj = "Geçerli bir QR kodu girin." });
            }

            var dosyaAdi = new string(kod
                .Where(karakter => char.IsLetterOrDigit(karakter) || karakter is '-' or '_')
                .ToArray());
            if (string.IsNullOrEmpty(dosyaAdi))
            {
                dosyaAdi = "demirbas-qr";
            }

            Response.Headers.CacheControl = "private, no-store";
            return File(
                System.Text.Encoding.UTF8.GetBytes(QrSvgOlustur(kod)),
                "image/svg+xml",
                $"{dosyaAdi}.svg");
        }

        private static string QrSvgOlustur(string kod)
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(
                kod,
                QRCodeGenerator.ECCLevel.Q);
            return new SvgQRCode(qrCodeData).GetGraphic(8);
        }

        [HttpGet("benim-zimmetlerim")]
        public async Task<IActionResult> BenimZimmetlerim(
            CancellationToken cancellationToken)
        {
            var kullaniciAdi = User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(kullaniciAdi))
            {
                return Unauthorized(new { mesaj = "Oturum bilgisi bulunamadı." });
            }

            await using var connection = await BaglantiAcAsync();
            const string sql = @"
                SELECT PersonelId
                FROM dbo.UygulamaKullanicilari
                WHERE KullaniciAdi = @KullaniciAdi;";
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@KullaniciAdi", System.Data.SqlDbType.NVarChar, 100)
                .Value = kullaniciAdi;
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is null || result is DBNull)
            {
                return Conflict(new
                {
                    mesaj = "Hesabınız henüz bir personel kaydına bağlanmamış. Yöneticinizden eşleştirme isteyin."
                });
            }

            var personelId = Convert.ToInt32(result);
            return await Zimmetler(null, null, personelId, "Aktif");
        }

        [HttpPost("{demirbasId:int}/ariza-bildir")]
        public async Task<IActionResult> ArizaBildir(
            int demirbasId,
            [FromBody] ArizaBildirimIstek istek,
            CancellationToken cancellationToken)
        {
            if (demirbasId <= 0 ||
                string.IsNullOrWhiteSpace(istek.Aciklama) ||
                istek.Aciklama.Trim().Length > 500)
            {
                return BadRequest(new
                {
                    mesaj = "Geçerli bir cihaz ve en fazla 500 karakterlik arıza açıklaması girin."
                });
            }

            await using var connection = await BaglantiAcAsync();
            var hasLegacyKullaniciId = await ColumnExistsAsync(
                connection,
                "dbo.DemirbasHareketleri",
                "KullaniciId");
            var kullaniciAdi = User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(kullaniciAdi))
            {
                return Unauthorized(new { mesaj = "Oturum bilgisi bulunamadı." });
            }

            const string userSql = @"
                SELECT KullaniciId, PersonelId
                FROM dbo.UygulamaKullanicilari
                WHERE KullaniciAdi = @KullaniciAdi;";
            int kullaniciId;
            int? personelId;
            await using (var userCommand = new SqlCommand(userSql, connection))
            {
                userCommand.Parameters.Add("@KullaniciAdi", System.Data.SqlDbType.NVarChar, 100)
                    .Value = kullaniciAdi;
                await using var userReader = await userCommand.ExecuteReaderAsync(cancellationToken);
                if (!await userReader.ReadAsync(cancellationToken))
                {
                    return Unauthorized(new { mesaj = "Kullanıcı hesabı bulunamadı." });
                }

                kullaniciId = userReader.GetInt32(0);
                personelId = userReader.IsDBNull(1) ? null : userReader.GetInt32(1);
            }

            var insertSql = hasLegacyKullaniciId
                ? @"
                    INSERT INTO dbo.DemirbasHareketleri
                        (DemirbasId, PersonelId, KullaniciId, IslemTuru, Aciklama, Konum)
                    VALUES
                        (@DemirbasId, @PersonelId, @KullaniciId, N'ArizaBildirildi', @Aciklama, N'Saha');"
                : @"
                    INSERT INTO dbo.DemirbasHareketleri
                        (DemirbasId, PersonelId, IslemTuru, Aciklama, Konum)
                    VALUES
                        (@DemirbasId, @PersonelId, N'ArizaBildirildi', @Aciklama, N'Saha');";

            await using var transaction =
                await connection.BeginTransactionAsync(cancellationToken);
            await using (var updateCommand = new SqlCommand(
                @"UPDATE dbo.Demirbaslar
                  SET Durum = N'Arizali'
                  WHERE DemirbasId = @DemirbasId;",
                connection,
                (SqlTransaction)transaction))
            {
                updateCommand.Parameters.AddWithValue("@DemirbasId", demirbasId);
                if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return NotFound(new { mesaj = "Demirbaş bulunamadı." });
                }
            }

            await using (var movementCommand = new SqlCommand(
                insertSql,
                connection,
                (SqlTransaction)transaction))
            {
                movementCommand.Parameters.AddWithValue("@DemirbasId", demirbasId);
                movementCommand.Parameters.Add("@PersonelId", System.Data.SqlDbType.Int)
                    .Value = personelId ?? (object)DBNull.Value;
                if (hasLegacyKullaniciId)
                {
                    movementCommand.Parameters.AddWithValue("@KullaniciId", kullaniciId);
                }

                movementCommand.Parameters.Add("@Aciklama", System.Data.SqlDbType.NVarChar, 500)
                    .Value = istek.Aciklama.Trim();
                await movementCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return Ok(new { basarili = true, mesaj = "Arıza bildirimi kaydedildi." });
        }

        [HttpGet("filtreler")]
        public async Task<IActionResult> Filtreler()
        {
            await using var connection = await BaglantiAcAsync();

            var departmanlar = new List<object>();
            const string departmanSql = @"
                SELECT DepartmanId, Ad
                FROM dbo.Departmanlar
                ORDER BY Ad;";

            await using (var depCommand = new SqlCommand(departmanSql, connection))
            await using (var depReader = await depCommand.ExecuteReaderAsync())
            {
                while (await depReader.ReadAsync())
                {
                    departmanlar.Add(new
                    {
                        departmanId = depReader.GetInt32(0),
                        ad = depReader.GetString(1)
                    });
                }
            }

            var personeller = new List<object>();
            const string personelSql = @"
                SELECT PersonelId, AdSoyad, DepartmanId
                FROM dbo.Personeller
                ORDER BY AdSoyad;";

            await using (var pCommand = new SqlCommand(personelSql, connection))
            await using (var pReader = await pCommand.ExecuteReaderAsync())
            {
                while (await pReader.ReadAsync())
                {
                    personeller.Add(new
                    {
                        personelId = pReader.GetInt32(0),
                        adSoyad = pReader.GetString(1),
                        departmanId = pReader.IsDBNull(2) ? null : (int?)pReader.GetInt32(2)
                    });
                }
            }

            return Ok(new
            {
                durumlar = new[]
                {
                    "Tumu",
                    "Kullanilabilir",
                    "BostaBekleyen",
                    "Zimmetli",
                    "Arizali",
                    "Bakimda"
                },
                departmanlar,
                personeller
            });
        }

        [HttpGet("zimmetler")]
        public async Task<IActionResult> Zimmetler(
            [FromQuery] int? demirbasId,
            [FromQuery] int? departmanId,
            [FromQuery] int? personelId,
            [FromQuery] string? durum)
        {
            await using var connection = await BaglantiAcAsync();

            var hasLegacyKullaniciId = await ColumnExistsAsync(connection, "dbo.Zimmetler", "KullaniciId");
            var personelKeyExpression = hasLegacyKullaniciId ? "COALESCE(z.PersonelId, z.KullaniciId)" : "z.PersonelId";
            var personelJoin = hasLegacyKullaniciId
                ? "LEFT JOIN dbo.Personeller AS p ON p.PersonelId = COALESCE(z.PersonelId, z.KullaniciId)"
                : "LEFT JOIN dbo.Personeller AS p ON p.PersonelId = z.PersonelId";

            var sql = $@"
                SELECT z.ZimmetId,
                       z.DemirbasId,
                       d.DemirbasAdi,
                       {personelKeyExpression} AS PersonelId,
                       p.AdSoyad AS PersonelAdi,
                       dep.DepartmanId,
                       dep.Ad AS DepartmanAdi,
                       z.ZimmetTarihi,
                       z.IadeTarihi,
                       z.Durum,
                       z.Aciklama
                FROM dbo.Zimmetler AS z
                LEFT JOIN dbo.Demirbaslar AS d ON d.DemirbasId = z.DemirbasId
                {personelJoin}
                LEFT JOIN dbo.Departmanlar AS dep ON dep.DepartmanId = z.DepartmanId
                WHERE (@DemirbasId IS NULL OR z.DemirbasId = @DemirbasId)
                  AND (@PersonelId IS NULL OR {personelKeyExpression} = @PersonelId)
                  AND (@DepartmanId IS NULL OR z.DepartmanId = @DepartmanId)
                  AND (@Durum IS NULL OR @Durum = N'' OR z.Durum = @Durum)
                ORDER BY z.ZimmetTarihi DESC;";

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@DemirbasId", demirbasId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@PersonelId", personelId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@DepartmanId", departmanId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@Durum", string.IsNullOrWhiteSpace(durum) ? DBNull.Value : durum.Trim());

            var liste = new List<object>();
            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                liste.Add(new
                {
                    zimmetId = reader.GetInt32(0),
                    demirbasId = reader.GetInt32(1),
                    demirbasAdi = ReadNullableString(reader, 2),
                    personelId = reader.IsDBNull(3) ? null : (int?)reader.GetInt32(3),
                    personelAdi = ReadNullableString(reader, 4),
                    departmanId = reader.IsDBNull(5) ? null : (int?)reader.GetInt32(5),
                    departmanAdi = ReadNullableString(reader, 6),
                    zimmetTarihi = reader.IsDBNull(7) ? null : (DateTime?)reader.GetDateTime(7),
                    iadeTarihi = reader.IsDBNull(8) ? null : (DateTime?)reader.GetDateTime(8),
                    durum = ReadNullableString(reader, 9),
                    aciklama = ReadNullableString(reader, 10)
                });
            }

            return Ok(liste);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost("zimmetle")]
        public async Task<IActionResult> Zimmetle([FromBody] ZimmetIstek istek)
        {
            if (istek == null || istek.DemirbasId <= 0)
            {
                return BadRequest(new { mesaj = "Geçerli bir demirbaş seçin." });
            }

            if (istek.PersonelId <= 0 && string.IsNullOrWhiteSpace(istek.PersonelAdi))
            {
                return BadRequest(new { mesaj = "Personel seçimi zorunludur." });
            }

            await using var connection = await BaglantiAcAsync();

            var personelId = istek.PersonelId;
            if (personelId <= 0 && !string.IsNullOrWhiteSpace(istek.PersonelAdi))
            {
                const string personelBulSql = @"
                    SELECT PersonelId
                    FROM dbo.Personeller
                    WHERE AdSoyad = @AdSoyad;";

                await using var personelBul = new SqlCommand(personelBulSql, connection);
                personelBul.Parameters.AddWithValue("@AdSoyad", istek.PersonelAdi.Trim());
                var bulunanPersonel = await personelBul.ExecuteScalarAsync();

                if (bulunanPersonel == null || bulunanPersonel == DBNull.Value)
                {
                    var departmanId = await GetOrCreateDepartmanAsync(connection, istek.DepartmanAdi);
                    var yeniPersonelSql = @"
                        INSERT INTO dbo.Personeller (AdSoyad, DepartmanId, Eposta, Telefon)
                        VALUES (@AdSoyad, @DepartmanId, @Eposta, @Telefon);
                        SELECT CAST(SCOPE_IDENTITY() AS INT);";

                    await using var yeniPersonel = new SqlCommand(yeniPersonelSql, connection);
                    yeniPersonel.Parameters.AddWithValue("@AdSoyad", istek.PersonelAdi.Trim());
                    yeniPersonel.Parameters.AddWithValue("@DepartmanId", departmanId);
                    yeniPersonel.Parameters.AddWithValue("@Eposta", string.IsNullOrWhiteSpace(istek.Eposta) ? DBNull.Value : (object)istek.Eposta.Trim());
                    yeniPersonel.Parameters.AddWithValue("@Telefon", string.IsNullOrWhiteSpace(istek.Telefon) ? DBNull.Value : (object)istek.Telefon.Trim());
                    personelId = Convert.ToInt32(await yeniPersonel.ExecuteScalarAsync());
                }
                else
                {
                    personelId = Convert.ToInt32(bulunanPersonel);
                }
            }

            var departmanIdAktif = istek.DepartmanId;
            if (departmanIdAktif <= 0 && !string.IsNullOrWhiteSpace(istek.DepartmanAdi))
            {
                departmanIdAktif = await GetOrCreateDepartmanAsync(connection, istek.DepartmanAdi);
            }

            if (departmanIdAktif <= 0)
            {
                departmanIdAktif = await GetOrCreateDepartmanAsync(connection, "Genel");
            }

            var mevcutAktifZimmetSql = @"
                SELECT TOP 1 ZimmetId
                FROM dbo.Zimmetler
                WHERE DemirbasId = @DemirbasId
                  AND Durum = N'Aktif';";

            await using var mevcutAktif = new SqlCommand(mevcutAktifZimmetSql, connection);
            mevcutAktif.Parameters.AddWithValue("@DemirbasId", istek.DemirbasId);
            var mevcutAktifId = await mevcutAktif.ExecuteScalarAsync();

            if (mevcutAktifId != null && mevcutAktifId != DBNull.Value)
            {
                var eskiZimmetSql = @"
                    UPDATE dbo.Zimmetler
                    SET Durum = N'Eski',
                        IadeTarihi = SYSDATETIME()
                    WHERE ZimmetId = @ZimmetId;";

                await using var eskiZimmet = new SqlCommand(eskiZimmetSql, connection);
                eskiZimmet.Parameters.AddWithValue("@ZimmetId", Convert.ToInt32(mevcutAktifId));
                await eskiZimmet.ExecuteNonQueryAsync();
            }

            var hasLegacyKullaniciId = await ColumnExistsAsync(connection, "dbo.Zimmetler", "KullaniciId");
            var yeniZimmetSql = hasLegacyKullaniciId
                ? @"
                    INSERT INTO dbo.Zimmetler (DemirbasId, PersonelId, KullaniciId, DepartmanId, Aciklama, Durum)
                    VALUES (@DemirbasId, @PersonelId, @KullaniciId, @DepartmanId, @Aciklama, N'Aktif');
                    SELECT CAST(SCOPE_IDENTITY() AS INT);"
                : @"
                    INSERT INTO dbo.Zimmetler (DemirbasId, PersonelId, DepartmanId, Aciklama, Durum)
                    VALUES (@DemirbasId, @PersonelId, @DepartmanId, @Aciklama, N'Aktif');
                    SELECT CAST(SCOPE_IDENTITY() AS INT);";

            await using var yeniZimmet = new SqlCommand(yeniZimmetSql, connection);
            yeniZimmet.Parameters.AddWithValue("@DemirbasId", istek.DemirbasId);
            yeniZimmet.Parameters.AddWithValue("@PersonelId", personelId);
            if (hasLegacyKullaniciId)
            {
                yeniZimmet.Parameters.AddWithValue("@KullaniciId", personelId);
            }
            yeniZimmet.Parameters.AddWithValue("@DepartmanId", departmanIdAktif);
            yeniZimmet.Parameters.AddWithValue("@Aciklama", string.IsNullOrWhiteSpace(istek.Aciklama) ? (object)DBNull.Value : istek.Aciklama.Trim());

            var zimmetId = Convert.ToInt32(await yeniZimmet.ExecuteScalarAsync());

            var demirbasGuncelleSql = @"
                UPDATE dbo.Demirbaslar
                SET Durum = N'Zimmetli'
                WHERE DemirbasId = @DemirbasId;";

            await using var demirbasGuncelle = new SqlCommand(demirbasGuncelleSql, connection);
            demirbasGuncelle.Parameters.AddWithValue("@DemirbasId", istek.DemirbasId);
            await demirbasGuncelle.ExecuteNonQueryAsync();

            var hasLegacyHareketKullaniciId = await ColumnExistsAsync(connection, "dbo.DemirbasHareketleri", "KullaniciId");
            var hasLegacyHareketAciklama = await ColumnExistsAsync(connection, "dbo.DemirbasHareketleri", "IslemAciklamasi") && !await ColumnExistsAsync(connection, "dbo.DemirbasHareketleri", "Aciklama");
            var hareketSql = hasLegacyHareketKullaniciId
                ? hasLegacyHareketAciklama
                    ? @"
                        INSERT INTO dbo.DemirbasHareketleri (DemirbasId, PersonelId, KullaniciId, IslemTuru, IslemAciklamasi)
                        VALUES (@DemirbasId, @PersonelId, @KullaniciId, N'Zimmetlendi', @Aciklama);"
                    : @"
                        INSERT INTO dbo.DemirbasHareketleri (DemirbasId, PersonelId, KullaniciId, IslemTuru, Aciklama, Konum)
                        VALUES (@DemirbasId, @PersonelId, @KullaniciId, N'Zimmetlendi', @Aciklama, N'Zimmet');"
                : hasLegacyHareketAciklama
                    ? @"
                        INSERT INTO dbo.DemirbasHareketleri (DemirbasId, PersonelId, IslemTuru, IslemAciklamasi)
                        VALUES (@DemirbasId, @PersonelId, N'Zimmetlendi', @Aciklama);"
                    : @"
                        INSERT INTO dbo.DemirbasHareketleri (DemirbasId, PersonelId, IslemTuru, Aciklama, Konum)
                        VALUES (@DemirbasId, @PersonelId, N'Zimmetlendi', @Aciklama, N'Zimmet');";

            await using var hareket = new SqlCommand(hareketSql, connection);
            hareket.Parameters.AddWithValue("@DemirbasId", istek.DemirbasId);
            hareket.Parameters.AddWithValue("@PersonelId", personelId);
            if (hasLegacyHareketKullaniciId)
            {
                hareket.Parameters.AddWithValue("@KullaniciId", personelId);
            }
            hareket.Parameters.AddWithValue("@Aciklama", string.IsNullOrWhiteSpace(istek.Aciklama) ? (object)DBNull.Value : istek.Aciklama.Trim());
            await hareket.ExecuteNonQueryAsync();

            return Ok(new
            {
                basarili = true,
                mesaj = "Demirbaş başarıyla zimmetlendi.",
                zimmetId,
                personelId,
                departmanId = departmanIdAktif
            });
        }

        [Authorize(Roles = "Admin")]
        [HttpPost("zimmetten-dusun/{demirbasId:int}")]
        public async Task<IActionResult> ZimmettenDus(int demirbasId)
        {
            if (demirbasId <= 0)
            {
                return BadRequest(new { mesaj = "Geçersiz demirbaş numarası." });
            }

            await using var connection = await BaglantiAcAsync();

            var aktifZimmetSql = @"
                SELECT TOP 1 ZimmetId
                FROM dbo.Zimmetler
                WHERE DemirbasId = @DemirbasId
                  AND Durum = N'Aktif';";

            await using var aktifZimmetCommand = new SqlCommand(aktifZimmetSql, connection);
            aktifZimmetCommand.Parameters.AddWithValue("@DemirbasId", demirbasId);
            var aktifZimmetId = await aktifZimmetCommand.ExecuteScalarAsync();

            if (aktifZimmetId == null || aktifZimmetId == DBNull.Value)
            {
                return BadRequest(new { mesaj = "Bu cihazın aktif zimmeti bulunmuyor." });
            }

            var iadeSql = @"
                UPDATE dbo.Zimmetler
                SET Durum = N'İade Edildi',
                    IadeTarihi = SYSDATETIME()
                WHERE ZimmetId = @ZimmetId;";

            await using var iadeCommand = new SqlCommand(iadeSql, connection);
            iadeCommand.Parameters.AddWithValue("@ZimmetId", Convert.ToInt32(aktifZimmetId));
            await iadeCommand.ExecuteNonQueryAsync();

            var demirbasSql = @"
                UPDATE dbo.Demirbaslar
                SET Durum = N'BostaBekleyen'
                WHERE DemirbasId = @DemirbasId;";

            await using var demirbasCommand = new SqlCommand(demirbasSql, connection);
            demirbasCommand.Parameters.AddWithValue("@DemirbasId", demirbasId);
            await demirbasCommand.ExecuteNonQueryAsync();

            var hasLegacyHareketKullaniciId = await ColumnExistsAsync(connection, "dbo.DemirbasHareketleri", "KullaniciId");
            var hasLegacyHareketAciklama = await ColumnExistsAsync(connection, "dbo.DemirbasHareketleri", "IslemAciklamasi") && !await ColumnExistsAsync(connection, "dbo.DemirbasHareketleri", "Aciklama");
            var hareketSql = hasLegacyHareketKullaniciId
                ? hasLegacyHareketAciklama
                    ? @"
                        INSERT INTO dbo.DemirbasHareketleri (DemirbasId, KullaniciId, IslemTuru, IslemAciklamasi)
                        VALUES (@DemirbasId, @KullaniciId, N'ZimmettenDusuldu', N'Zimmetten düşme işlemi tamamlandı.');"
                    : @"
                        INSERT INTO dbo.DemirbasHareketleri (DemirbasId, KullaniciId, IslemTuru, Aciklama, Konum)
                        VALUES (@DemirbasId, @KullaniciId, N'ZimmettenDusuldu', N'Zimmetten düşme işlemi tamamlandı.', N'Depo');"
                : hasLegacyHareketAciklama
                    ? @"
                        INSERT INTO dbo.DemirbasHareketleri (DemirbasId, IslemTuru, IslemAciklamasi)
                        VALUES (@DemirbasId, N'ZimmettenDusuldu', N'Zimmetten düşme işlemi tamamlandı.');"
                    : @"
                        INSERT INTO dbo.DemirbasHareketleri (DemirbasId, IslemTuru, Aciklama, Konum)
                        VALUES (@DemirbasId, N'ZimmettenDusuldu', N'Zimmetten düşme işlemi tamamlandı.', N'Depo');";

            await using var hareketCommand = new SqlCommand(hareketSql, connection);
            hareketCommand.Parameters.AddWithValue("@DemirbasId", demirbasId);
            if (hasLegacyHareketKullaniciId)
            {
                hareketCommand.Parameters.AddWithValue("@KullaniciId", DBNull.Value);
            }
            await hareketCommand.ExecuteNonQueryAsync();

            return Ok(new { basarili = true, mesaj = "Demirbaş zimmetten düşüldü." });
        }

        private async Task<int> GetOrCreateDepartmanAsync(SqlConnection connection, string? ad)
        {
            if (string.IsNullOrWhiteSpace(ad))
            {
                return await GetOrCreateDepartmanAsync(connection, "Genel");
            }

            const string bulSql = @"
                SELECT DepartmanId
                FROM dbo.Departmanlar
                WHERE Ad = @Ad;";

            await using var bulCommand = new SqlCommand(bulSql, connection);
            bulCommand.Parameters.AddWithValue("@Ad", ad.Trim());
            var sonuc = await bulCommand.ExecuteScalarAsync();
            if (sonuc != null && sonuc != DBNull.Value)
            {
                return Convert.ToInt32(sonuc);
            }

            const string ekleSql = @"
                INSERT INTO dbo.Departmanlar (Ad)
                VALUES (@Ad);
                SELECT CAST(SCOPE_IDENTITY() AS INT);";

            await using var ekleCommand = new SqlCommand(ekleSql, connection);
            ekleCommand.Parameters.AddWithValue("@Ad", ad.Trim());
            return Convert.ToInt32(await ekleCommand.ExecuteScalarAsync());
        }

        [HttpGet("istatistikler")]
        public async Task<IActionResult> Istatistikler()
        {
            await using var connection = await BaglantiAcAsync();

            const string sql = @"
                SELECT
                    COUNT(*) AS Toplam,
                    SUM(CASE WHEN LOWER(ISNULL(Durum, N'')) IN (N'kullanilabilir', N'bostabekleyen', N'boşta bekleyen') THEN 1 ELSE 0 END) AS BostaBekleyen,
                    SUM(CASE WHEN LOWER(ISNULL(Durum, N'')) LIKE N'%ariz%' THEN 1 ELSE 0 END) AS Arizali
                FROM dbo.Demirbaslar;";

            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                return Ok(new { toplam = 0, bostaBekleyen = 0, arizali = 0 });
            }

            return Ok(new
            {
                toplam = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                bostaBekleyen = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                arizali = reader.IsDBNull(2) ? 0 : reader.GetInt32(2)
            });
        }

        // Yeni demirbaş ekle
        [Authorize(Roles = "Admin")]
        [HttpPost]
        public async Task<IActionResult> Ekle([FromBody] YeniDemirbas yeni)
        {
            if (yeni == null ||
                string.IsNullOrWhiteSpace(yeni.DemirbasAdi) ||
                string.IsNullOrWhiteSpace(yeni.Kategori))
            {
                return BadRequest(new
                {
                    mesaj = "Demirbaş adı ve kategori zorunludur."
                });
            }

            var durum = string.IsNullOrWhiteSpace(yeni.Durum)
                ? "Kullanilabilir"
                : yeni.Durum.Trim();

            if (!IzinliDurumlar.Contains(durum, StringComparer.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    mesaj = "Geçersiz durum seçildi."
                });
            }

            if (!string.IsNullOrWhiteSpace(yeni.SeriNumarasi) &&
                yeni.SeriNumarasi.Trim().Length > 100)
            {
                return BadRequest(new { mesaj = "Seri numarası en fazla 100 karakter olabilir." });
            }

            DateTime? satinAlinmaTarihi = null;
            if (!string.IsNullOrWhiteSpace(yeni.SatinAlinmaTarihi))
            {
                if (!TryParseDate(yeni.SatinAlinmaTarihi, out var tarih))
                {
                    return BadRequest(new { mesaj = "Alım tarihi geçersiz." });
                }

                satinAlinmaTarihi = tarih;
            }

            DateTime? garantiBitisTarihi = null;
            if (!string.IsNullOrWhiteSpace(yeni.GarantiBitisTarihi))
            {
                if (!TryParseDate(yeni.GarantiBitisTarihi, out var tarih))
                {
                    return BadRequest(new { mesaj = "Garanti bitiş tarihi geçersiz." });
                }

                garantiBitisTarihi = tarih;
            }

            await using var connection = await BaglantiAcAsync();

            var sql = @"
                INSERT INTO Demirbaslar
                    (DemirbasAdi, Kategori, Marka, Model, Durum,
                     SeriNumarasi, SatinAlinmaTarihi, GarantiBitisTarihi, QrKodu, BarkodKodu)
                OUTPUT INSERTED.DemirbasId
                VALUES
                    (@DemirbasAdi, @Kategori, @Marka, @Model, @Durum,
                     @SeriNumarasi, @SatinAlinmaTarihi, @GarantiBitisTarihi, @QrKodu, @BarkodKodu)";

            await using var command = new SqlCommand(sql, connection);

            command.Parameters.AddWithValue("@DemirbasAdi", yeni.DemirbasAdi.Trim());
            command.Parameters.AddWithValue("@Kategori", yeni.Kategori.Trim());
            command.Parameters.Add("@Marka", System.Data.SqlDbType.NVarChar, 100).Value =
                string.IsNullOrWhiteSpace(yeni.Marka) ? DBNull.Value : yeni.Marka.Trim();
            command.Parameters.Add("@Model", System.Data.SqlDbType.NVarChar, 100).Value =
                string.IsNullOrWhiteSpace(yeni.Model) ? DBNull.Value : yeni.Model.Trim();
            command.Parameters.AddWithValue("@Durum", durum);
            command.Parameters.Add("@SeriNumarasi", System.Data.SqlDbType.NVarChar, 100).Value =
                string.IsNullOrWhiteSpace(yeni.SeriNumarasi) ? DBNull.Value : yeni.SeriNumarasi.Trim();
            command.Parameters.Add("@SatinAlinmaTarihi", System.Data.SqlDbType.Date).Value =
                satinAlinmaTarihi ?? (object)DBNull.Value;
            command.Parameters.Add("@GarantiBitisTarihi", System.Data.SqlDbType.Date).Value =
                garantiBitisTarihi ?? (object)DBNull.Value;
            command.Parameters.Add("@QrKodu", System.Data.SqlDbType.NVarChar, 200).Value = DBNull.Value;
            command.Parameters.Add("@BarkodKodu", System.Data.SqlDbType.NVarChar, 200).Value = DBNull.Value;
            var yeniIdObj = await command.ExecuteScalarAsync();
            if (yeniIdObj is null || yeniIdObj is DBNull)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new { mesaj = "Demirbaş oluşturulamadı." });
            }

            var yeniId = Convert.ToInt32(yeniIdObj);

            var qrKodu = string.IsNullOrWhiteSpace(yeni.QrKodu)
                ? GenerateAssetCode(yeniId)
                : yeni.QrKodu.Trim();
            var barkodKodu = string.IsNullOrWhiteSpace(yeni.BarkodKodu)
                ? qrKodu
                : yeni.BarkodKodu.Trim();

            await using var kodCommand = new SqlCommand(
                @"UPDATE Demirbaslar SET QrKodu = @QrKodu, BarkodKodu = @BarkodKodu WHERE DemirbasId = @Id",
                connection);
            kodCommand.Parameters.AddWithValue("@QrKodu", qrKodu);
            kodCommand.Parameters.AddWithValue("@BarkodKodu", barkodKodu);
            kodCommand.Parameters.AddWithValue("@Id", yeniId);
            await kodCommand.ExecuteNonQueryAsync();

            return Ok(new
            {
                basarili = true,
                mesaj = "Demirbaş başarıyla eklendi.",
                demirbasId = yeniId,
                qrKodu,
                barkodKodu
            });
        }

        // Demirbaş bilgilerini güncelle
        [Authorize(Roles = "Admin")]
        [HttpPut("{id:int}")]
        public async Task<IActionResult> Guncelle(
            int id,
            [FromBody] YeniDemirbas yeni)
        {
            if (id <= 0 ||
                yeni == null ||
                string.IsNullOrWhiteSpace(yeni.DemirbasAdi) ||
                string.IsNullOrWhiteSpace(yeni.Kategori))
            {
                return BadRequest(new
                {
                    mesaj = "Demirbaş numarası, adı ve kategorisi zorunludur."
                });
            }

            var durum = string.IsNullOrWhiteSpace(yeni.Durum)
                ? "Kullanilabilir"
                : yeni.Durum.Trim();

            if (!IzinliDurumlar.Contains(durum, StringComparer.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    mesaj = "Geçersiz durum seçildi."
                });
            }

            if (!string.IsNullOrWhiteSpace(yeni.SeriNumarasi) &&
                yeni.SeriNumarasi.Trim().Length > 100)
            {
                return BadRequest(new { mesaj = "Seri numarası en fazla 100 karakter olabilir." });
            }

            DateTime? satinAlinmaTarihi = null;
            if (!string.IsNullOrWhiteSpace(yeni.SatinAlinmaTarihi))
            {
                if (!TryParseDate(yeni.SatinAlinmaTarihi, out var tarih))
                {
                    return BadRequest(new { mesaj = "Alım tarihi geçersiz." });
                }

                satinAlinmaTarihi = tarih;
            }

            DateTime? garantiBitisTarihi = null;
            if (!string.IsNullOrWhiteSpace(yeni.GarantiBitisTarihi))
            {
                if (!TryParseDate(yeni.GarantiBitisTarihi, out var tarih))
                {
                    return BadRequest(new { mesaj = "Garanti bitiş tarihi geçersiz." });
                }

                garantiBitisTarihi = tarih;
            }

            await using var connection = await BaglantiAcAsync();

            var kontrolSql = @"
                SELECT COUNT(*)
                FROM Demirbaslar
                WHERE DemirbasId = @Id";

            await using var kontrolCommand =
                new SqlCommand(kontrolSql, connection);

            kontrolCommand.Parameters.AddWithValue("@Id", id);

            var mevcut = Convert.ToInt32(await kontrolCommand.ExecuteScalarAsync());

            if (mevcut == 0)
            {
                return NotFound(new
                {
                    mesaj = "Güncellenecek demirbaş bulunamadı."
                });
            }

            var mevcutDurumSql = @"
                SELECT Durum
                FROM Demirbaslar
                WHERE DemirbasId = @Id";

            await using var mevcutDurumCommand =
                new SqlCommand(mevcutDurumSql, connection);
            mevcutDurumCommand.Parameters.AddWithValue("@Id", id);
            var mevcutDurum = Convert.ToString(await mevcutDurumCommand.ExecuteScalarAsync());

            if (string.Equals(mevcutDurum, "Zimmetli", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(durum, "Zimmetli", StringComparison.OrdinalIgnoreCase))
            {
                return Conflict(new
                {
                    mesaj = "Zimmetli cihazın durumu, zimmet işlemi üzerinden değiştirilmelidir."
                });
            }

            var sql = @"
                UPDATE Demirbaslar
                SET DemirbasAdi = @DemirbasAdi,
                    Kategori = @Kategori,
                    Marka = @Marka,
                    Model = @Model,
                    Durum = @Durum,
                    SeriNumarasi = @SeriNumarasi,
                    SatinAlinmaTarihi = @SatinAlinmaTarihi,
                    GarantiBitisTarihi = ISNULL(@GarantiBitisTarihi, GarantiBitisTarihi),
                    QrKodu = ISNULL(@QrKodu, QrKodu),
                    BarkodKodu = ISNULL(@BarkodKodu, BarkodKodu)
                WHERE DemirbasId = @Id";

            await using var command = new SqlCommand(sql, connection);

            command.Parameters.AddWithValue("@Id", id);
            command.Parameters.AddWithValue("@DemirbasAdi", yeni.DemirbasAdi.Trim());
            command.Parameters.AddWithValue("@Kategori", yeni.Kategori.Trim());
            command.Parameters.Add("@Marka", System.Data.SqlDbType.NVarChar, 100).Value =
                string.IsNullOrWhiteSpace(yeni.Marka) ? DBNull.Value : yeni.Marka.Trim();
            command.Parameters.Add("@Model", System.Data.SqlDbType.NVarChar, 100).Value =
                string.IsNullOrWhiteSpace(yeni.Model) ? DBNull.Value : yeni.Model.Trim();
            command.Parameters.AddWithValue("@Durum", durum);
            command.Parameters.Add("@SeriNumarasi", System.Data.SqlDbType.NVarChar, 100).Value =
                string.IsNullOrWhiteSpace(yeni.SeriNumarasi) ? DBNull.Value : yeni.SeriNumarasi.Trim();
            command.Parameters.Add("@SatinAlinmaTarihi", System.Data.SqlDbType.Date).Value =
                satinAlinmaTarihi ?? (object)DBNull.Value;
            command.Parameters.Add("@GarantiBitisTarihi", System.Data.SqlDbType.Date).Value =
                garantiBitisTarihi ?? (object)DBNull.Value;

            var mevcutQr = await GetQrKoduAsync(connection, id);
            var qrKodu = string.IsNullOrWhiteSpace(yeni.QrKodu)
                ? mevcutQr ?? GenerateAssetCode(id)
                : yeni.QrKodu.Trim();
            var barkodKodu = string.IsNullOrWhiteSpace(yeni.BarkodKodu)
                ? mevcutQr ?? qrKodu
                : yeni.BarkodKodu.Trim();

            command.Parameters.AddWithValue("@QrKodu", qrKodu);
            command.Parameters.AddWithValue("@BarkodKodu", barkodKodu);

            await command.ExecuteNonQueryAsync();

            return Ok(new
            {
                basarili = true,
                mesaj = "Demirbaş başarıyla güncellendi.",
                qrKodu,
                barkodKodu
            });
        }

        // Demirbaş sil
        [Authorize(Roles = "Admin")]
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Sil(int id)
        {
            if (id <= 0)
            {
                return BadRequest(new
                {
                    mesaj = "Geçersiz demirbaş numarası."
                });
            }

            var connectionString =
                _configuration.GetConnectionString("DefaultConnection");

            await using var connection =
                new SqlConnection(connectionString);

            await connection.OpenAsync();

            // Cihaz var mı, durumu ne?
            var kontrolSql = @"
                SELECT Durum
                FROM Demirbaslar
                WHERE DemirbasId = @Id";

            await using var kontrolCommand =
                new SqlCommand(kontrolSql, connection);

            kontrolCommand.Parameters.AddWithValue("@Id", id);

            var durumSonucu = await kontrolCommand.ExecuteScalarAsync();

            if (durumSonucu == null || durumSonucu == DBNull.Value)
            {
                return NotFound(new
                {
                    mesaj = "Demirbaş bulunamadı."
                });
            }

            var durum = Convert.ToString(durumSonucu);

            if (string.Equals(
                durum, "Zimmetli",
                StringComparison.OrdinalIgnoreCase))
            {
                return Conflict(new
                {
                    mesaj = "Bu cihaz zimmetli olduğu için silinemez."
                });
            }

            // Zimmet geçmişini koru.
            var zimmetKontrolSql = @"
                SELECT COUNT(*)
                FROM Zimmetler
                WHERE DemirbasId = @Id";

            await using var zimmetCommand =
                new SqlCommand(zimmetKontrolSql, connection);

            zimmetCommand.Parameters.AddWithValue("@Id", id);

            var zimmetSayisi =
                Convert.ToInt32(await zimmetCommand.ExecuteScalarAsync());

            if (zimmetSayisi > 0)
            {
                return Conflict(new
                {
                    mesaj = "Bu cihazın zimmet geçmişi var. Kayıt geçmişini korumak için silinmedi."
                });
            }

            // Hareket geçmişini koru.
            var hareketKontrolSql = @"
                SELECT COUNT(*)
                FROM DemirbasHareketleri
                WHERE DemirbasId = @Id";

            await using var hareketCommand =
                new SqlCommand(hareketKontrolSql, connection);

            hareketCommand.Parameters.AddWithValue("@Id", id);

            var hareketSayisi =
                Convert.ToInt32(await hareketCommand.ExecuteScalarAsync());

            if (hareketSayisi > 0)
            {
                return Conflict(new
                {
                    mesaj = "Bu cihazın hareket geçmişi var. Kayıt geçmişini korumak için silinmedi."
                });
            }

            // Geçmiş kaydı olmayan cihazı sil.
            var silSql = @"
                DELETE FROM Demirbaslar
                WHERE DemirbasId = @Id";

            await using var silCommand =
                new SqlCommand(silSql, connection);

            silCommand.Parameters.AddWithValue("@Id", id);

            var silinenKayit = await silCommand.ExecuteNonQueryAsync();

            if (silinenKayit == 0)
            {
                return NotFound(new
                {
                    mesaj = "Demirbaş bulunamadı."
                });
            }

            return Ok(new
            {
                basarili = true,
                mesaj = "Demirbaş başarıyla silindi."
            });
        }

        private async Task<string?> GetQrKoduAsync(SqlConnection connection, int demirbasId)
        {
            const string sql = @"SELECT QrKodu FROM Demirbaslar WHERE DemirbasId = @Id";
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Id", demirbasId);
            var result = await command.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? null : Convert.ToString(result);
        }

        public class YeniDemirbas
        {
            public string DemirbasAdi { get; set; } = "";
            public string Kategori { get; set; } = "";
            public string? Marka { get; set; }
            public string? Model { get; set; }
            public string? SeriNumarasi { get; set; }
            public string? SatinAlinmaTarihi { get; set; }
            public string? GarantiBitisTarihi { get; set; }
            public string? Durum { get; set; }
            public string? QrKodu { get; set; }
            public string? BarkodKodu { get; set; }
        }

        public class ZimmetIstek
        {
            public int DemirbasId { get; set; }
            public int PersonelId { get; set; }
            public string? PersonelAdi { get; set; }
            public int DepartmanId { get; set; }
            public string? DepartmanAdi { get; set; }
            public string? Eposta { get; set; }
            public string? Telefon { get; set; }
            public string? Aciklama { get; set; }
        }

        public class ArizaBildirimIstek
        {
            public string? Aciklama { get; set; }
        }
    }
}
