
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.FileProviders;

if (args.Length == 1 && args[0] == "--create-admin")
{
    var provisionerBuilder =
        WebApplication.CreateBuilder(Array.Empty<string>());
    await DemirbasYonetimProje.AdminProvisioner.CreateAsync(
        provisionerBuilder.Configuration);
    return;
}

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection yapılandırılmamış.");
}

var defaultAdminKullaniciAdi =
    builder.Configuration["AppSettings:DefaultAdminUser"];
var defaultAdminParola =
    builder.Configuration["AppSettings:DefaultAdminPassword"];

if (string.IsNullOrWhiteSpace(defaultAdminKullaniciAdi) ||
    string.IsNullOrWhiteSpace(defaultAdminParola))
{
    throw new InvalidOperationException(
        "AppSettings:DefaultAdminUser ve AppSettings:DefaultAdminPassword ortam değişkenleri yapılandırılmalıdır.");
}

await DemirbasYonetimProje.AdminProvisioner.EnsureDefaultAdminAsync(
    connectionString,
    defaultAdminKullaniciAdi,
    defaultAdminParola);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "DemirbasYonetim.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddCors(options =>
{
    options.AddPolicy("FrontendIzin", policy =>
    {
        policy
            .WithOrigins(
                "http://127.0.0.1:5500",
                "http://localhost:5500"
            )
            .WithHeaders("Content-Type", "X-Requested-With")
            .WithMethods("GET", "POST", "PUT", "DELETE")
            .AllowCredentials();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("FrontendIzin");
var frontendPath = Path.Combine(
    app.Environment.ContentRootPath,
    "frontend");
if (Directory.Exists(frontendPath))
{
    var frontendProvider = new PhysicalFileProvider(frontendPath);
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = frontendProvider
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = frontendProvider
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    var isApiWrite =
        context.Request.Path.StartsWithSegments("/api") &&
        (HttpMethods.IsPost(context.Request.Method) ||
         HttpMethods.IsPut(context.Request.Method) ||
         HttpMethods.IsDelete(context.Request.Method));

    var isLoginRequest =
        HttpMethods.IsPost(context.Request.Method) &&
        (context.Request.Path.Equals(
             "/api/auth/giris",
             StringComparison.OrdinalIgnoreCase) ||
         context.Request.Path.Equals(
             "/api/auth/yonetici-giris",
             StringComparison.OrdinalIgnoreCase));

    if (isApiWrite &&
        !isLoginRequest &&
        context.Request.Headers["X-Requested-With"] != "XMLHttpRequest")
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new
        {
            mesaj = "İstek doğrulanamadı."
        });
        return;
    }

    await next();
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/api/veritabani-test", async () =>
{
    try
    {
        await using var connection =
            new SqlConnection(connectionString);

        await connection.OpenAsync();

        return Results.Ok(new
        {
            Basarili = true,
            Mesaj = "SQL Server bağlantısı başarılı!"
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Veritabanı bağlantısı başarısız",
            detail: ex.Message
        );
    }
});

app.Run();
