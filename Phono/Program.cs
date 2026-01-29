using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;
using Phono.Data;
using Phono.Models;
using Phono.Services;

var builder = WebApplication.CreateBuilder(args);
var migrateOnly = args.Contains("--migrate-only", StringComparer.OrdinalIgnoreCase);
var createAdmin = args.Contains("--create-admin", StringComparer.OrdinalIgnoreCase);

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = long.MaxValue;
});

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = long.MaxValue;
});

// Configure SQLite Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    var dbPath = Path.Combine(builder.Environment.ContentRootPath, "Data", "app.db");
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    connectionString = $"Data Source={dbPath}";
}
else
{
    var sqliteBuilder = new SqliteConnectionStringBuilder(connectionString);
    var dataSource = sqliteBuilder.DataSource;
    if (!string.IsNullOrWhiteSpace(dataSource) && !Path.IsPathRooted(dataSource))
    {
        dataSource = Path.Combine(builder.Environment.ContentRootPath, dataSource);
        sqliteBuilder.DataSource = dataSource;
        connectionString = sqliteBuilder.ToString();
    }

    var dataDirectory = Path.GetDirectoryName(sqliteBuilder.DataSource);
    if (!string.IsNullOrWhiteSpace(dataDirectory))
    {
        Directory.CreateDirectory(dataDirectory);
    }
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));

var authSection = builder.Configuration.GetSection("Authentication");
var passwordSection = authSection.GetSection("PasswordRequirements");
var cookieExpirationDays = authSection.GetValue("CookieExpirationDays", 30);
var requireConfirmedEmail = authSection.GetValue("RequireConfirmedEmail", false);

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequiredLength = passwordSection.GetValue("MinLength", 8);
    options.Password.RequireDigit = passwordSection.GetValue("RequireDigit", true);
    options.Password.RequireLowercase = passwordSection.GetValue("RequireLowercase", true);
    options.Password.RequireUppercase = passwordSection.GetValue("RequireUppercase", true);
    options.Password.RequireNonAlphanumeric = passwordSection.GetValue("RequireNonAlphanumeric", false);

    options.User.RequireUniqueEmail = true;
    options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";

    options.SignIn.RequireConfirmedAccount = requireConfirmedEmail;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromDays(cookieExpirationDays);
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

// Register services
builder.Services.AddSingleton<FileService>();
builder.Services.AddScoped<TrackService>();
builder.Services.AddScoped<AlbumService>();
builder.Services.AddScoped<ArtistService>();
builder.Services.AddScoped<MetadataSyncService>();

// Configure compression options
builder.Services.Configure<CompressionOptions>(builder.Configuration.GetSection("Compression"));
builder.Services.AddScoped<AudioCompressionService>();

// Configure MagnetAPI service
builder.Services.Configure<MagnetApiOptions>(builder.Configuration.GetSection("MagnetApi"));
builder.Services.AddHttpClient<MagnetApiService>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<MagnetApiOptions>>();
    client.BaseAddress = new Uri(options.Value.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<MagnetApiService>();

// Configure qBittorrent service
builder.Services.Configure<TorrentSettings>(builder.Configuration.GetSection("Torrent"));
builder.Services.AddHttpClient<QBitClientService>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<TorrentSettings>>();
    client.BaseAddress = new Uri(options.Value.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler { CookieContainer = new CookieContainer() });
builder.Services.AddHostedService<TorrentMonitorService>();

var app = builder.Build();

// Apply migrations and import existing files on startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    dbContext.Database.Migrate();

    if (!migrateOnly)
    {
        // Import any existing files not in database
        var syncService = scope.ServiceProvider.GetRequiredService<MetadataSyncService>();
        var (imported, removed) = await syncService.FullSyncAsync();
        if (imported > 0 || removed > 0)
        {
            Console.WriteLine($"Metadata sync: {imported} files imported, {removed} orphaned records removed");
        }
    }
}

if (migrateOnly)
{
    return;
}

await CreateDefaultAdminUser(app.Services, createAdmin);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
    ;


app.Run();

static async Task CreateDefaultAdminUser(IServiceProvider services, bool forceCreate)
{
    var adminEmail = Environment.GetEnvironmentVariable("PHONO_ADMIN_EMAIL");
    var adminPassword = Environment.GetEnvironmentVariable("PHONO_ADMIN_PASSWORD");
    if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
    {
        return;
    }

    using var scope = services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    if (!forceCreate && await userManager.Users.AnyAsync())
    {
        return;
    }

    var existingUser = await userManager.FindByEmailAsync(adminEmail);
    if (existingUser != null)
    {
        return;
    }

    var user = new ApplicationUser
    {
        UserName = adminEmail,
        Email = adminEmail,
        EmailConfirmed = true,
        CreatedAt = DateTime.UtcNow
    };

    var result = await userManager.CreateAsync(user, adminPassword);
    if (!result.Succeeded)
    {
        var errors = string.Join(", ", result.Errors.Select(error => error.Description));
        Console.WriteLine($"Admin user creation failed: {errors}");
    }
}