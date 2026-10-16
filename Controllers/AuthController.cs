using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace DemirbasYonetimProje.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 600_000;
    private readonly IConfiguration _configuration;

    public AuthController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [AllowAnonymous]
    [HttpGet("durum")]
    public async Task<IActionResult> Durum()
    {
        await using var connection = await BaglantiAcAsync();
        await KullaniciTablosunuHazirlaAsync(connection);

        await using var command = new SqlCommand(
            @"SELECT COUNT(*)
              FROM dbo.UygulamaKullanicilari
              WHERE Rol = N'Admin'",
            connection);

        var adminSayisi = Convert.ToInt32(
            await command.ExecuteScalarAsync());

        return Ok(new { kurulumGerekli = adminSayisi == 0 });
    }

    [AllowAnonymous]
    [HttpPost("ilk-yonetici")]
    public async Task<IActionResult> IlkYoneticiOlustur(
        [FromBody] KullaniciKimlikBilgisi istek)
    {
        if (!KimlikBilgileriGecerli(istek, out var hata))
        {
            return BadRequest(new { mesaj = hata });
        }

        await using var connection = await BaglantiAcAsync();
        await KullaniciTablosunuHazirlaAsync(connection);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var parolaHash = ParolaHashle(istek.Parola, salt);

        await using var transaction =
            await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable);

        await using var countCommand = new SqlCommand(
            @"SELECT COUNT(*)
              FROM dbo.UygulamaKullanicilari WITH (UPDLOCK, HOLDLOCK)
              WHERE Rol = N'Admin'",
            connection,
            (SqlTransaction)transaction);

        var adminSayisi = Convert.ToInt32(
            await countCommand.ExecuteScalarAsync());

        if (adminSayisi > 0)
        {
            return Conflict(new
            {
                mesaj = "Yönetici hesabı zaten mevcut."
            });
        }

        try
        {
            await KullaniciEkleAsync(
                connection,
                (SqlTransaction)transaction,
                istek.KullaniciAdi.Trim(),
                parolaHash,
                salt,
                "Admin");
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return Conflict(new
            {
                mesaj = "Bu kullanıcı adı zaten kullanılıyor. Yönetici için farklı bir ad seçin."
            });
        }

        await transaction.CommitAsync();
        await GirisYapAsync(istek.KullaniciAdi.Trim(), "Admin");

        return Ok(new
        {
            kullaniciAdi = istek.KullaniciAdi.Trim(),
            rol = "Admin"
        });
    }

    [AllowAnonymous]
    [HttpPost("giris")]
    public async Task<IActionResult> Giris(
        [FromBody] KullaniciKimlikBilgisi istek,
        CancellationToken cancellationToken)
    {
        return await KimlikDogrulaAsync(
            istek,
            "Kullanici",
            cancellationToken);
    }

    [AllowAnonymous]
    [HttpPost("yonetici-giris")]
    public async Task<IActionResult> YoneticiGirisi(
        [FromBody] KullaniciKimlikBilgisi istek,
        CancellationToken cancellationToken)
    {
        return await KimlikDogrulaAsync(
            istek,
            "Admin",
            cancellationToken);
    }

    [AllowAnonymous]
    [HttpPost("sifre-sifirlama-istegi")]
    public async Task<IActionResult> SifreSifirlamaIstegi(
        [FromBody] SifreSifirlamaIstegi istek,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(istek.KullaniciAdi) ||
            istek.KullaniciAdi.Trim().Length > 100)
        {
            return BadRequest(new
            {
                mesaj = "Geçerli bir kullanıcı adı girin."
            });
        }

        await using var connection =
            await BaglantiAcAsync(cancellationToken);
        await KullaniciTablosunuHazirlaAsync(
            connection,
            cancellationToken);

        const string sql = @"
            INSERT INTO dbo.SifreSifirlamaIstekleri (KullaniciId)
            SELECT KullaniciId
            FROM dbo.UygulamaKullanicilari AS k
            WHERE k.KullaniciAdi = @KullaniciAdi
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM dbo.SifreSifirlamaIstekleri
                  WHERE KullaniciId = k.KullaniciId
                    AND Durum = N'Beklemede'
              );";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(
            "@KullaniciAdi",
            System.Data.SqlDbType.NVarChar,
            100).Value = istek.KullaniciAdi.Trim();
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // A pending request already exists for this account.
        }

        return Ok(new
        {
            mesaj = "Kullanıcı adınız sistemde kayıtlıysa, yöneticiye sıfırlama isteği iletildi."
        });
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("sifre-sifirlama-istekleri")]
    public async Task<IActionResult> SifreSifirlamaIstekleriniGetir(
        CancellationToken cancellationToken)
    {
        await using var connection =
            await BaglantiAcAsync(cancellationToken);
        await KullaniciTablosunuHazirlaAsync(
            connection,
            cancellationToken);

        const string sql = @"
            SELECT i.IstekId, k.KullaniciAdi, i.OlusturulmaTarihi
            FROM dbo.SifreSifirlamaIstekleri AS i
            INNER JOIN dbo.UygulamaKullanicilari AS k
                ON k.KullaniciId = i.KullaniciId
            WHERE i.Durum = N'Beklemede'
            ORDER BY i.OlusturulmaTarihi;";

        var istekler = new List<object>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            istekler.Add(new
            {
                istekId = reader.GetInt32(0),
                kullaniciAdi = reader.GetString(1),
                olusturulmaTarihi = DateTime.SpecifyKind(
                    reader.GetDateTime(2),
                    DateTimeKind.Utc)
            });
        }

        return Ok(istekler);
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("sifre-sifirlama-istekleri/{istekId:int}/tamamla")]
    public async Task<IActionResult> SifreSifirlamaIstekiniTamamla(
        int istekId,
        [FromBody] YeniParolaIstegi istek,
        CancellationToken cancellationToken)
    {
        if (istekId <= 0)
        {
            return BadRequest(new { mesaj = "Geçersiz istek." });
        }

        if (string.IsNullOrEmpty(istek.Parola) || istek.Parola.Length < 12)
        {
            return BadRequest(new
            {
                mesaj = "Yeni parola en az 12 karakter olmalıdır."
            });
        }

        await using var connection =
            await BaglantiAcAsync(cancellationToken);
        await KullaniciTablosunuHazirlaAsync(
            connection,
            cancellationToken);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var parolaHash = ParolaHashle(istek.Parola, salt);

        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        const string sql = @"
            DECLARE @KullaniciId int;

            SELECT @KullaniciId = KullaniciId
            FROM dbo.SifreSifirlamaIstekleri WITH (UPDLOCK, HOLDLOCK)
            WHERE IstekId = @IstekId
              AND Durum = N'Beklemede';

            IF @KullaniciId IS NULL
            BEGIN
                SELECT CAST(0 AS bit);
                RETURN;
            END;

            UPDATE dbo.UygulamaKullanicilari
            SET ParolaHash = @ParolaHash,
                ParolaSalt = @ParolaSalt
            WHERE KullaniciId = @KullaniciId;

            UPDATE dbo.SifreSifirlamaIstekleri
            SET Durum = N'Tamamlandi'
            WHERE IstekId = @IstekId
              AND Durum = N'Beklemede';

            SELECT CAST(1 AS bit);";

        await using var command =
            new SqlCommand(sql, connection, (SqlTransaction)transaction);
        command.Parameters.Add(
            "@IstekId",
            System.Data.SqlDbType.Int).Value = istekId;
        command.Parameters.Add(
            "@ParolaHash",
            System.Data.SqlDbType.VarBinary,
            HashSize).Value = parolaHash;
        command.Parameters.Add(
            "@ParolaSalt",
            System.Data.SqlDbType.VarBinary,
            SaltSize).Value = salt;

        var tamamlandi = Convert.ToBoolean(
            await command.ExecuteScalarAsync(cancellationToken));

        if (!tamamlandi)
        {
            await transaction.RollbackAsync(cancellationToken);
            return NotFound(new
            {
                mesaj = "Bekleyen parola sıfırlama isteği bulunamadı."
            });
        }

        await transaction.CommitAsync(cancellationToken);
        return Ok(new { mesaj = "Parola sıfırlandı." });
    }

    private async Task<IActionResult> KimlikDogrulaAsync(
        KullaniciKimlikBilgisi istek,
        string beklenenRol,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(istek.KullaniciAdi) ||
            string.IsNullOrEmpty(istek.Parola))
        {
            return BadRequest(new
            {
                mesaj = "Kullanıcı adı ve parola zorunludur."
            });
        }

        await using var connection =
            await BaglantiAcAsync(cancellationToken);
        await KullaniciTablosunuHazirlaAsync(
            connection,
            cancellationToken);

        byte[]? saklananHash = null;
        byte[]? salt = null;
        string? rol = null;
        var kullaniciAdi = istek.KullaniciAdi.Trim();

        const string sql = @"
            SELECT ParolaHash, ParolaSalt, Rol
            FROM dbo.UygulamaKullanicilari
            WHERE KullaniciAdi = @KullaniciAdi";

        await using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("@KullaniciAdi", kullaniciAdi);

            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);

            if (await reader.ReadAsync(cancellationToken))
            {
                saklananHash = (byte[])reader["ParolaHash"];
                salt = (byte[])reader["ParolaSalt"];
                rol = reader.GetString(reader.GetOrdinal("Rol"));
            }
        }

        if (saklananHash == null || salt == null || rol == null)
        {
            return Unauthorized(new { mesaj = "Kullanıcı adı veya parola hatalı." });
        }

        var girilenHash = ParolaHashle(istek.Parola, salt);

        if (!CryptographicOperations.FixedTimeEquals(
            girilenHash, saklananHash))
        {
            return Unauthorized(new { mesaj = "Kullanıcı adı veya parola hatalı." });
        }

        if (!string.Equals(rol, beklenenRol, StringComparison.Ordinal))
        {
            return Unauthorized(new { mesaj = "Kullanıcı adı veya parola hatalı." });
        }

        await GirisYapAsync(kullaniciAdi, rol);

        return Ok(new { kullaniciAdi, rol });
    }

    [AllowAnonymous]
    [HttpPost("kayit")]
    public async Task<IActionResult> KayitOl(
        [FromBody] KullaniciKimlikBilgisi istek)
    {
        if (!KimlikBilgileriGecerli(istek, out var hata))
        {
            return BadRequest(new { mesaj = hata });
        }

        await using var connection = await BaglantiAcAsync();
        await KullaniciTablosunuHazirlaAsync(connection);

        await using (var adminKontrol = new SqlCommand(
            @"SELECT COUNT(*)
              FROM dbo.UygulamaKullanicilari
              WHERE Rol = N'Admin'",
            connection))
        {
            var adminSayisi = Convert.ToInt32(
                await adminKontrol.ExecuteScalarAsync());

            if (adminSayisi == 0)
            {
                return Conflict(new
                {
                    mesaj = "Kayıttan önce ilk yönetici hesabı oluşturulmalıdır."
                });
            }
        }

        var kullaniciAdi = istek.KullaniciAdi.Trim();
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var parolaHash = ParolaHashle(istek.Parola, salt);

        const string sql = @"
            INSERT INTO dbo.UygulamaKullanicilari
                (KullaniciAdi, ParolaHash, ParolaSalt, Rol)
            VALUES
                (@KullaniciAdi, @ParolaHash, @ParolaSalt, 'Kullanici')";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@KullaniciAdi", kullaniciAdi);
        command.Parameters.Add("@ParolaHash", System.Data.SqlDbType.VarBinary, HashSize)
            .Value = parolaHash;
        command.Parameters.Add("@ParolaSalt", System.Data.SqlDbType.VarBinary, SaltSize)
            .Value = salt;

        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return Conflict(new { mesaj = "Bu kullanıcı adı zaten kullanılıyor." });
        }

        await GirisYapAsync(kullaniciAdi, "Kullanici");

        return Ok(new
        {
            kullaniciAdi,
            rol = "Kullanici"
        });
    }

    [Authorize]
    [HttpGet("ben")]
    public IActionResult Ben()
    {
        return Ok(new
        {
            kullaniciAdi = User.Identity?.Name,
            rol = User.FindFirstValue(ClaimTypes.Role)
        });
    }

    [Authorize]
    [HttpPost("cikis")]
    public async Task<IActionResult> Cikis()
    {
        await HttpContext.SignOutAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);

        return Ok(new { mesaj = "Oturum kapatıldı." });
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("kullanicilar")]
    public async Task<IActionResult> KullanicilariGetir(
        CancellationToken cancellationToken)
    {
        await using var connection = await BaglantiAcAsync(cancellationToken);
        await KullaniciTablosunuHazirlaAsync(connection, cancellationToken);

        const string sql = @"
            SELECT KullaniciId, KullaniciAdi, Rol, PersonelId
            FROM dbo.UygulamaKullanicilari
            ORDER BY Rol DESC, KullaniciAdi;";

        var kullanicilar = new List<object>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            kullanicilar.Add(new
            {
                kullaniciId = reader.GetInt32(0),
                kullaniciAdi = reader.GetString(1),
                rol = reader.GetString(2),
                personelId = reader.IsDBNull(3) ? null : (int?)reader.GetInt32(3)
            });
        }

        return Ok(kullanicilar);
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("kullanicilar")]
    public async Task<IActionResult> KullaniciOlustur(
        [FromBody] KullaniciKimlikBilgisi istek,
        CancellationToken cancellationToken)
    {
        if (!KimlikBilgileriGecerli(istek, out var hata))
        {
            return BadRequest(new { mesaj = hata });
        }

        await using var connection = await BaglantiAcAsync();
        await KullaniciTablosunuHazirlaAsync(connection, cancellationToken);

        var personelHatasi = await PersonelEslemesiniDogrulaAsync(
            connection,
            istek.PersonelId,
            null,
            cancellationToken);
        if (personelHatasi is not null)
        {
            return BadRequest(new { mesaj = personelHatasi });
        }

        var kullaniciAdi = istek.KullaniciAdi.Trim();
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var parolaHash = ParolaHashle(istek.Parola, salt);

        const string sql = @"
            INSERT INTO dbo.UygulamaKullanicilari
                (KullaniciAdi, ParolaHash, ParolaSalt, Rol, PersonelId)
            VALUES
                (@KullaniciAdi, @ParolaHash, @ParolaSalt, 'Kullanici', @PersonelId)";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@KullaniciAdi", kullaniciAdi);
        command.Parameters.Add("@ParolaHash", System.Data.SqlDbType.VarBinary, HashSize)
            .Value = parolaHash;
        command.Parameters.Add("@ParolaSalt", System.Data.SqlDbType.VarBinary, SaltSize)
            .Value = salt;
        command.Parameters.Add("@PersonelId", System.Data.SqlDbType.Int)
            .Value = istek.PersonelId ?? (object)DBNull.Value;

        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return Conflict(new
            {
                mesaj = "Kullanıcı adı veya personel eşlemesi zaten kullanılıyor."
            });
        }

        return Ok(new
        {
            mesaj = "Normal kullanıcı hesabı oluşturuldu.",
            kullaniciAdi,
            rol = "Kullanici"
        });
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("kullanicilar/{kullaniciId:int}")]
    public async Task<IActionResult> KullaniciGuncelle(
        int kullaniciId,
        [FromBody] KullaniciGuncellemeIsteği istek,
        CancellationToken cancellationToken)
    {
        if (kullaniciId <= 0)
        {
            return BadRequest(new { mesaj = "Geçersiz kullanıcı seçimi." });
        }

        var mevcutKullaniciAdi = string.IsNullOrWhiteSpace(istek.KullaniciAdi)
            ? null
            : istek.KullaniciAdi.Trim();
        var yeniParola = string.IsNullOrWhiteSpace(istek.Parola)
            ? null
            : istek.Parola;
        var yeniRol = string.IsNullOrWhiteSpace(istek.Rol)
            ? null
            : istek.Rol.Trim();

        if (mevcutKullaniciAdi is null &&
            yeniParola is null &&
            yeniRol is null &&
            !istek.PersonelIdGuncellensin)
        {
            return BadRequest(new { mesaj = "Güncellenecek bilgi bulunamadı." });
        }

        await using var connection = await BaglantiAcAsync(cancellationToken);
        await KullaniciTablosunuHazirlaAsync(connection, cancellationToken);

        if (istek.PersonelIdGuncellensin)
        {
            var personelHatasi = await PersonelEslemesiniDogrulaAsync(
                connection,
                istek.PersonelId,
                kullaniciId,
                cancellationToken);
            if (personelHatasi is not null)
            {
                return BadRequest(new { mesaj = personelHatasi });
            }
        }

        const string mevcutSql = @"
            SELECT KullaniciAdi, Rol
            FROM dbo.UygulamaKullanicilari
            WHERE KullaniciId = @KullaniciId;";

        string mevcutAdi;
        string mevcutRol;
        await using (var mevcutCommand = new SqlCommand(mevcutSql, connection))
        {
            mevcutCommand.Parameters.AddWithValue("@KullaniciId", kullaniciId);
            await using var mevcutReader = await mevcutCommand.ExecuteReaderAsync(cancellationToken);
            if (!await mevcutReader.ReadAsync(cancellationToken))
            {
                return NotFound(new { mesaj = "Kullanıcı bulunamadı." });
            }

            mevcutAdi = mevcutReader.GetString(0);
            mevcutRol = mevcutReader.GetString(1);
        }

        if (yeniRol is not null &&
            !string.Equals(yeniRol, "Admin", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(yeniRol, "Kullanici", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { mesaj = "Geçersiz rol değeri." });
        }

        if (mevcutRol == "Admin" &&
            yeniRol is not null &&
            string.Equals(yeniRol, "Kullanici", StringComparison.OrdinalIgnoreCase))
        {
            const string adminSayisiSql = @"SELECT COUNT(*) FROM dbo.UygulamaKullanicilari WHERE Rol = N'Admin';";
            await using var adminSayisiCommand = new SqlCommand(adminSayisiSql, connection);
            var adminSayisi = Convert.ToInt32(
                await adminSayisiCommand.ExecuteScalarAsync(cancellationToken));
            if (adminSayisi <= 1)
            {
                return BadRequest(new { mesaj = "Son yönetici rolü kaldırılamaz." });
            }
        }

        if (mevcutKullaniciAdi is not null)
        {
            if (mevcutKullaniciAdi.Length < 3 || mevcutKullaniciAdi.Length > 100)
            {
                return BadRequest(new { mesaj = "Kullanıcı adı 3 ile 100 karakter arasında olmalıdır." });
            }

            const string isimKontrolSql = @"
                SELECT COUNT(*)
                FROM dbo.UygulamaKullanicilari
                WHERE KullaniciAdi = @KullaniciAdi
                  AND KullaniciId <> @KullaniciId;";

            await using var isimKontrol = new SqlCommand(isimKontrolSql, connection);
            isimKontrol.Parameters.AddWithValue("@KullaniciAdi", mevcutKullaniciAdi);
            isimKontrol.Parameters.AddWithValue("@KullaniciId", kullaniciId);
            var isimKullaniliyor = Convert.ToInt32(
                await isimKontrol.ExecuteScalarAsync(cancellationToken));
            if (isimKullaniliyor > 0)
            {
                return Conflict(new { mesaj = "Bu kullanıcı adı başka bir hesaba ait." });
            }
        }

        var setParcalari = new List<string>();
        var parameters = new List<SqlParameter>();

        if (mevcutKullaniciAdi is not null)
        {
            setParcalari.Add("KullaniciAdi = @KullaniciAdi");
            parameters.Add(new SqlParameter("@KullaniciAdi", mevcutKullaniciAdi));
        }

        if (yeniRol is not null)
        {
            setParcalari.Add("Rol = @Rol");
            parameters.Add(new SqlParameter("@Rol", yeniRol));
        }

        if (istek.PersonelIdGuncellensin)
        {
            setParcalari.Add("PersonelId = @PersonelId");
            parameters.Add(new SqlParameter("@PersonelId", SqlDbType.Int)
            {
                Value = istek.PersonelId ?? (object)DBNull.Value
            });
        }

        if (yeniParola is not null)
        {
            if (yeniParola.Length < 12)
            {
                return BadRequest(new { mesaj = "Parola en az 12 karakter olmalıdır." });
            }

            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var hash = ParolaHashle(yeniParola, salt);
            setParcalari.Add("ParolaHash = @ParolaHash");
            setParcalari.Add("ParolaSalt = @ParolaSalt");
            parameters.Add(new SqlParameter("@ParolaHash", SqlDbType.VarBinary, HashSize) { Value = hash });
            parameters.Add(new SqlParameter("@ParolaSalt", SqlDbType.VarBinary, SaltSize) { Value = salt });
        }

        if (setParcalari.Count == 0)
        {
            return BadRequest(new { mesaj = "Güncellenecek alan yok." });
        }

        var updateSql = $@"
            UPDATE dbo.UygulamaKullanicilari
            SET {string.Join(", ", setParcalari)}
            WHERE KullaniciId = @KullaniciId;";

        await using var updateCommand = new SqlCommand(updateSql, connection);
        updateCommand.Parameters.AddWithValue("@KullaniciId", kullaniciId);
        foreach (var parameter in parameters)
        {
            updateCommand.Parameters.Add(parameter);
        }

        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        return Ok(new { mesaj = "Kullanıcı bilgileri güncellendi." });
    }

    private static async Task<string?> PersonelEslemesiniDogrulaAsync(
        SqlConnection connection,
        int? personelId,
        int? currentUserId,
        CancellationToken cancellationToken)
    {
        if (personelId is null)
        {
            return null;
        }

        if (personelId <= 0)
        {
            return "Geçerli bir personel seçin.";
        }

        await using (var tableCommand = new SqlCommand(
            "SELECT OBJECT_ID(N'dbo.Personeller', N'U')",
            connection))
        {
            if (await tableCommand.ExecuteScalarAsync(cancellationToken) is DBNull or null)
            {
                return "Personel kayıtları bulunamadı. Önce personel listesini yükleyin.";
            }
        }

        const string personelSql = @"
            SELECT COUNT(*)
            FROM dbo.Personeller
            WHERE PersonelId = @PersonelId;";
        await using (var personelCommand = new SqlCommand(personelSql, connection))
        {
            personelCommand.Parameters.AddWithValue("@PersonelId", personelId.Value);
            if (Convert.ToInt32(
                    await personelCommand.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                return "Seçilen personel kaydı bulunamadı.";
            }
        }

        const string duplicateSql = @"
            SELECT COUNT(*)
            FROM dbo.UygulamaKullanicilari
            WHERE PersonelId = @PersonelId
              AND (@CurrentUserId IS NULL OR KullaniciId <> @CurrentUserId);";
        await using var duplicateCommand = new SqlCommand(duplicateSql, connection);
        duplicateCommand.Parameters.AddWithValue("@PersonelId", personelId.Value);
        duplicateCommand.Parameters.Add("@CurrentUserId", SqlDbType.Int)
            .Value = currentUserId ?? (object)DBNull.Value;
        return Convert.ToInt32(
                   await duplicateCommand.ExecuteScalarAsync(cancellationToken)) > 0
            ? "Bu personel başka bir kullanıcı hesabına bağlı."
            : null;
    }

    [Authorize(Roles = "Admin")]
    [HttpDelete("kullanicilar/{kullaniciId:int}")]
    public async Task<IActionResult> KullaniciSil(
        int kullaniciId,
        CancellationToken cancellationToken)
    {
        if (kullaniciId <= 0)
        {
            return BadRequest(new { mesaj = "Geçersiz kullanıcı seçimi." });
        }

        await using var connection = await BaglantiAcAsync(cancellationToken);
        await KullaniciTablosunuHazirlaAsync(connection, cancellationToken);

        const string secSql = @"
            SELECT KullaniciAdi, Rol
            FROM dbo.UygulamaKullanicilari
            WHERE KullaniciId = @KullaniciId;";

        await using (var secCommand = new SqlCommand(secSql, connection))
        {
            secCommand.Parameters.AddWithValue("@KullaniciId", kullaniciId);
            await using var secReader = await secCommand.ExecuteReaderAsync(cancellationToken);
            if (!await secReader.ReadAsync(cancellationToken))
            {
                return NotFound(new { mesaj = "Kullanıcı bulunamadı." });
            }

            var kullaniciAdi = secReader.GetString(0);
            var rol = secReader.GetString(1);

            if (string.Equals(rol, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                const string adminSayisiSql = @"SELECT COUNT(*) FROM dbo.UygulamaKullanicilari WHERE Rol = N'Admin';";
                await using var adminSayisiCommand = new SqlCommand(adminSayisiSql, connection);
                var adminSayisi = Convert.ToInt32(await adminSayisiCommand.ExecuteScalarAsync());
                if (adminSayisi <= 1)
                {
                    return BadRequest(new { mesaj = "Son yönetici silinemez." });
                }
            }

            if (string.Equals(User.Identity?.Name, kullaniciAdi, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { mesaj = "Kendi hesabınızı silemezsiniz." });
            }
        }

        const string deleteSql = @"DELETE FROM dbo.UygulamaKullanicilari WHERE KullaniciId = @KullaniciId;";
        await using var deleteCommand = new SqlCommand(deleteSql, connection);
        deleteCommand.Parameters.AddWithValue("@KullaniciId", kullaniciId);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

        return Ok(new { mesaj = "Kullanıcı silindi." });
    }

    private async Task<SqlConnection> BaglantiAcAsync(
        CancellationToken cancellationToken = default)
    {
        var connectionString =
            _configuration.GetConnectionString("DefaultConnection");

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task KullaniciTablosunuHazirlaAsync(
        SqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            IF OBJECT_ID(N'dbo.UygulamaKullanicilari', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.UygulamaKullanicilari
                (
                    KullaniciId int IDENTITY(1,1) NOT NULL
                        CONSTRAINT PK_UygulamaKullanicilari PRIMARY KEY,
                    KullaniciAdi nvarchar(100) NOT NULL
                        CONSTRAINT UQ_UygulamaKullanicilari_KullaniciAdi UNIQUE,
                    ParolaHash varbinary(32) NOT NULL,
                    ParolaSalt varbinary(16) NOT NULL,
                    PersonelId int NULL,
                    Rol nvarchar(20) NOT NULL
                        CONSTRAINT CK_UygulamaKullanicilari_Rol
                        CHECK (Rol IN (N'Admin', N'Kullanici'))
                );
            END";

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        const string personelColumnSql = @"
            IF COL_LENGTH(N'dbo.UygulamaKullanicilari', N'PersonelId') IS NULL
                ALTER TABLE dbo.UygulamaKullanicilari ADD PersonelId int NULL;";
        await using var personelColumnCommand =
            new SqlCommand(personelColumnSql, connection);
        await personelColumnCommand.ExecuteNonQueryAsync(cancellationToken);

        const string istekTablosuSql = @"
            IF OBJECT_ID(N'dbo.SifreSifirlamaIstekleri', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.SifreSifirlamaIstekleri
                (
                        IstekId int IDENTITY(1,1) NOT NULL
                            CONSTRAINT PK_SifreSifirlamaIstekleri PRIMARY KEY,
                        KullaniciId int NOT NULL
                            CONSTRAINT FK_SifreSifirlamaIstekleri_Kullanicilari
                            REFERENCES dbo.UygulamaKullanicilari(KullaniciId),
                        OlusturulmaTarihi datetime2 NOT NULL
                            CONSTRAINT DF_SifreSifirlamaIstekleri_OlusturulmaTarihi
                            DEFAULT SYSUTCDATETIME(),
                        Durum nvarchar(20) NOT NULL
                            CONSTRAINT DF_SifreSifirlamaIstekleri_Durum
                            DEFAULT N'Beklemede',
                        CONSTRAINT CK_SifreSifirlamaIstekleri_Durum
                            CHECK (Durum IN (N'Beklemede', N'Tamamlandi'))
                );
            END;

            IF NOT EXISTS
            (
                SELECT 1
                FROM sys.indexes
                WHERE name = N'UX_SifreSifirlamaIstekleri_Bekleyen'
                  AND object_id = OBJECT_ID(N'dbo.SifreSifirlamaIstekleri')
            )
            BEGIN
                CREATE UNIQUE INDEX UX_SifreSifirlamaIstekleri_Bekleyen
                ON dbo.SifreSifirlamaIstekleri (KullaniciId)
                WHERE Durum = N'Beklemede';
            END";

        await using var istekTablosuCommand =
            new SqlCommand(istekTablosuSql, connection);
        await istekTablosuCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task KullaniciEkleAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string kullaniciAdi,
        byte[] parolaHash,
        byte[] salt,
        string rol)
    {
        const string sql = @"
            INSERT INTO dbo.UygulamaKullanicilari
                (KullaniciAdi, ParolaHash, ParolaSalt, Rol)
            VALUES
                (@KullaniciAdi, @ParolaHash, @ParolaSalt, @Rol)";

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@KullaniciAdi", kullaniciAdi);
        command.Parameters.Add("@ParolaHash", System.Data.SqlDbType.VarBinary, HashSize)
            .Value = parolaHash;
        command.Parameters.Add("@ParolaSalt", System.Data.SqlDbType.VarBinary, SaltSize)
            .Value = salt;
        command.Parameters.AddWithValue("@Rol", rol);
        await command.ExecuteNonQueryAsync();
    }

    private async Task GirisYapAsync(string kullaniciAdi, string rol)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, kullaniciAdi),
            new Claim(ClaimTypes.Role, rol)
        };
        var identity = new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });
    }

    private static byte[] ParolaHashle(string parola, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            parola,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);
    }

    private static bool KimlikBilgileriGecerli(
        KullaniciKimlikBilgisi istek,
        out string hata)
    {
        if (string.IsNullOrWhiteSpace(istek.KullaniciAdi) ||
            istek.KullaniciAdi.Trim().Length < 3 ||
            istek.KullaniciAdi.Trim().Length > 100)
        {
            hata = "Kullanıcı adı 3 ile 100 karakter arasında olmalıdır.";
            return false;
        }

        if (string.IsNullOrEmpty(istek.Parola) || istek.Parola.Length < 12)
        {
            hata = "Parola en az 12 karakter olmalıdır.";
            return false;
        }

        hata = "";
        return true;
    }
}

public sealed class KullaniciKimlikBilgisi
{
    public string KullaniciAdi { get; set; } = "";
    public string Parola { get; set; } = "";
    public int? PersonelId { get; set; }
}

public sealed class SifreSifirlamaIstegi
{
    public string KullaniciAdi { get; set; } = "";
}

public sealed class YeniParolaIstegi
{
    public string Parola { get; set; } = "";
}

public sealed class KullaniciGuncellemeIsteği
{
    public string? KullaniciAdi { get; set; }
    public string? Parola { get; set; }
    public string? Rol { get; set; }
    public int? PersonelId { get; set; }
    public bool PersonelIdGuncellensin { get; set; }
}
