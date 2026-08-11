using Microsoft.AspNetCore.Authentication.Cookies;
using SiteQueryDefectTracking.Web.Components;
using SiteQueryDefectTracking.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var apiBaseUrl = builder.Configuration["Api:BaseUrl"] ?? "http://localhost:5170";
if (!apiBaseUrl.Contains("://"))
{
    apiBaseUrl = $"https://{apiBaseUrl}";
}

builder.Services.AddHttpClient("api", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ApiClient>();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await ApiDevLauncher.EnsureRunningAsync(builder.Configuration, app.Logger);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

// Misrouted API calls (a JSON POST that reached this app instead of the API) must fail
// with a clean 404. Without this guard they match the catch-all Razor component endpoint
// and the component invoker rejects the JSON Content-Type with the cryptic
// HTTP 400 "The request has an incorrect Content-type.". This app only ever receives
// JSON POSTs from its own ApiClient when Api:BaseUrl points at it, so JSON is safe to
// short-circuit here; SSR forms are urlencoded/multipart and SignalR uses /_blazor.
app.Use(async (context, next) =>
{
    if (context.Request.Method != HttpMethods.Get
        && context.Request.Method != HttpMethods.Head
        && context.Request.ContentType is { } contentType
        && contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
        && !context.Request.Path.StartsWithSegments("/_blazor"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("Not Found.");
        return;
    }
    await next();
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();