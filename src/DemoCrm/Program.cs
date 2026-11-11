using DemoCrm.Configuration;
using DemoCrm.Customers;
using DemoCrm.Demo;
using DemoCrm.Security;
using DemoCrm.Simulation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DemoCrmOptions>(builder.Configuration.GetSection(DemoCrmOptions.SectionName));
builder.Services.AddSingleton<ICustomerStore, InMemoryCustomerStore>();
builder.Services.AddSingleton<FailureSimulator>();
builder.Services.AddSingleton<DemoUserAuthenticator>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddAuthentication("DemoCrmCookie")
    .AddCookie("DemoCrmCookie", options =>
    {
        options.Cookie.Name = "DemoCrm.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(2);
    });
builder.Services.AddAuthorization();

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "DemoCrm.Antiforgery";
    options.Cookie.SameSite = SameSiteMode.Strict;
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Error");
})
// Validation messages come from CustomerInputValidator only, not from MVC's implicit [Required] on non-nullable strings.
.AddMvcOptions(options => options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

// Forms are tiny; reject anything larger.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 64 * 1024);

var app = builder.Build();

app.UseExceptionHandler("/Error");
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapDemoControlEndpoints();

app.Run();

/// <summary>Exposed so tests can use WebApplicationFactory.</summary>
public partial class Program;
