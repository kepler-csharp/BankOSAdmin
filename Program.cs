using BankAdmin.Data;
using BankAdmin.Services;

// PostgreSQL has a mixed timestamp/timestamptz schema; legacy mode avoids DateTimeKind errors.
// Must run before any Npgsql connection is created.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// Session holds the session token + tenant + user after login (server-side, http-only cookie reference)
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(opts =>
{
    opts.IdleTimeout = TimeSpan.FromHours(4);
    opts.Cookie.Name = ".BankAdmin.Session";
    opts.Cookie.HttpOnly = true;
    opts.Cookie.IsEssential = true;
    opts.Cookie.SameSite = SameSiteMode.Lax;
});

// PostgreSQL connection settings (the app talks directly to the BankOS database, not the Laravel API)
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));

// Direct-to-database data access (replaces the old HTTP API client). Stateless → singleton.
builder.Services.AddSingleton<BankAdminApiService>();

// Generic HttpClientFactory (used by the AI assistant / ChatController to reach OpenAI)
builder.Services.AddHttpClient();

// Client notifications (user/account lifecycle + PQRS responses) — sent from this MVC app
builder.Services.AddScoped<EmailService>();

// PDF certificate generation (runs in the MVC app)
builder.Services.AddSingleton<PdfService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
