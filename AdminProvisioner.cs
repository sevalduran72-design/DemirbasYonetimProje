using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace DemirbasYonetimProje;

public static class AdminProvisioner
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 600_000;

    public static async Task EnsureDefaultAdminAsync(
        string connectionString,
        string kullaniciAdi,
        string parola,
        bool resetPasswordIfExists = false)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection yapılandırılmamış.");
        }

        var kullaniciAdiTemiz = kullaniciAdi?.Trim();
        if (string.IsNullOrWhiteSpace(kullaniciAdiTemiz) ||
            kullaniciAdiTemiz.Length < 3 ||
            kullaniciAdiTemiz.Length > 100)
        {
            throw new InvalidOperationException(
                "Varsayılan admin kullanıcı adı 3 ile 100 karakter arasında olmalıdır.");
        }

        if (string.IsNullOrWhiteSpace(parola) || parola.Length < 12)
        {
            throw new InvalidOperationException(
                "Varsayılan admin parolası en az 12 karakter olmalıdır.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        const string tableSql = @"
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
                    Rol nvarchar(20) NOT NULL
                        CONSTRAINT CK_UygulamaKullanicilari_Rol
                        CHECK (Rol IN (N'Admin', N'Kullanici'))
                );
            END;";

        await using (var tableCommand = new SqlCommand(tableSql, connection))
        {
            await tableCommand.ExecuteNonQueryAsync();
        }

        var adminCountSql = @"SELECT COUNT(*) FROM dbo.UygulamaKullanicilari WHERE KullaniciAdi = @KullaniciAdi;";
        var kullaniciVarmi = false;
        await using (var adminCountCommand = new SqlCommand(adminCountSql, connection))
        {
            adminCountCommand.Parameters.AddWithValue("@KullaniciAdi", kullaniciAdiTemiz);
            kullaniciVarmi = Convert.ToInt32(await adminCountCommand.ExecuteScalarAsync()) > 0;
        }

        if (kullaniciVarmi && !resetPasswordIfExists)
        {
            const string updateSql = @"
                UPDATE dbo.UygulamaKullanicilari
                SET Rol = N'Admin'
                WHERE KullaniciAdi = @KullaniciAdi;";

            await using (var updateCommand = new SqlCommand(updateSql, connection))
            {
                updateCommand.Parameters.AddWithValue("@KullaniciAdi", kullaniciAdiTemiz);
                await updateCommand.ExecuteNonQueryAsync();
            }

            return;
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            parola,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        if (kullaniciVarmi)
        {
            const string updateSql = @"
                UPDATE dbo.UygulamaKullanicilari
                SET ParolaHash = @ParolaHash,
                    ParolaSalt = @ParolaSalt,
                    Rol = N'Admin'
                WHERE KullaniciAdi = @KullaniciAdi;";

            await using var updateCommand = new SqlCommand(updateSql, connection);
            updateCommand.Parameters.AddWithValue("@KullaniciAdi", kullaniciAdiTemiz);
            updateCommand.Parameters.Add("@ParolaHash", System.Data.SqlDbType.VarBinary, HashSize)
                .Value = hash;
            updateCommand.Parameters.Add("@ParolaSalt", System.Data.SqlDbType.VarBinary, SaltSize)
                .Value = salt;
            await updateCommand.ExecuteNonQueryAsync();
            return;
        }

        const string insertSql = @"
            INSERT INTO dbo.UygulamaKullanicilari
                (KullaniciAdi, ParolaHash, ParolaSalt, Rol)
            VALUES
                (@KullaniciAdi, @ParolaHash, @ParolaSalt, N'Admin');";

        await using (var insertCommand = new SqlCommand(insertSql, connection))
        {
            insertCommand.Parameters.AddWithValue("@KullaniciAdi", kullaniciAdiTemiz);
            insertCommand.Parameters.Add("@ParolaHash", System.Data.SqlDbType.VarBinary, HashSize)
                .Value = hash;
            insertCommand.Parameters.Add("@ParolaSalt", System.Data.SqlDbType.VarBinary, SaltSize)
                .Value = salt;

            try
            {
                await insertCommand.ExecuteNonQueryAsync();
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                // Aynı kullanıcı adı başka bir kullanıcıya aitse, mevcut kullanıcıyı güvenli şekilde güncelleme adımına dönülür.
            }
        }
    }

    public static async Task CreateAsync(IConfiguration configuration)
    {
        var connectionString =
            configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection yapılandırılmamış.");
        }

        Console.Write("Yeni admin kullanıcı adı: ");
        var kullaniciAdi = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(kullaniciAdi) ||
            kullaniciAdi.Length < 3 ||
            kullaniciAdi.Length > 100)
        {
            throw new InvalidOperationException(
                "Kullanıcı adı 3 ile 100 karakter arasında olmalıdır.");
        }

        var parola = GizliParolaOku("Yeni admin parolası (en az 12 karakter): ");

        if (parola.Length < 12)
        {
            throw new InvalidOperationException(
                "Parola en az 12 karakter olmalıdır.");
        }

        await EnsureDefaultAdminAsync(
            connectionString,
            kullaniciAdi,
            parola,
            resetPasswordIfExists: true);
        Console.WriteLine($"Admin hesabı oluşturuldu: {kullaniciAdi}");
    }

    private static string GizliParolaOku(string prompt)
    {
        Console.Write(prompt);
        var parola = new System.Text.StringBuilder();

        while (true)
        {
            var tus = Console.ReadKey(intercept: true);

            if (tus.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return parola.ToString();
            }

            if (tus.Key == ConsoleKey.Backspace)
            {
                if (parola.Length > 0)
                {
                    parola.Length--;
                }

                continue;
            }

            if (!char.IsControl(tus.KeyChar))
            {
                parola.Append(tus.KeyChar);
            }
        }
    }
}
