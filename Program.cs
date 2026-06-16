using BankAdmin.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// Session holds the JWT + tenant + user after login (server-side, http-only cookie reference)
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(opts =>
{
    opts.IdleTimeout = TimeSpan.FromHours(4);
    opts.Cookie.Name = ".BankAdmin.Session";
    opts.Cookie.HttpOnly = true;
    opts.Cookie.IsEssential = true;
    opts.Cookie.SameSite = SameSiteMode.Lax;
});

// Typed HttpClient against the BankOS (Laravel) tenant API (administrador scope)
builder.Services.AddHttpClient<BankAdminApiService>();

// Client notifications (user/account lifecycle) — sent from this MVC app, never the API
builder.Services.AddScoped<EmailService>();

// PDF certificate generation (runs in the MVC app, not the API)
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
