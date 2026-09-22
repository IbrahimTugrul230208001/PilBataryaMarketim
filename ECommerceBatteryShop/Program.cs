using ECommerceBatteryShop.DataAccess;
using ECommerceBatteryShop.DataAccess.Abstract;
using ECommerceBatteryShop.DataAccess.Concrete;
using ECommerceBatteryShop.Options;
using ECommerceBatteryShop.Services;
using ECommerceBatteryShop.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Serilog;
using Serilog.Events;

DotNetEnv.Env.Load();
var builder = WebApplication.CreateBuilder(args);

// Debug: Print Google credentials
Console.WriteLine($"DEBUG - Google ClientId: {builder.Configuration["Authentication:Google:ClientId"]}");
Console.WriteLine($"DEBUG - Google ClientSecret: {builder.Configuration["Authentication:Google:ClientSecret"]}");

// Configure Serilog to log only Error+ to a dedicated file
var logsDir = Path.Combine(builder.Environment.ContentRootPath, "logs");
Directory.CreateDirectory(logsDir);

builder.Host.UseSerilog((ctx, services, lc) => lc
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    // Errors file (daily rolling)
    .WriteTo.File(
        path: Path.Combine(logsDir, "errors-.log"),
        restrictedToMinimumLevel: LogEventLevel.Error,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true,
        outputTemplate:
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
);

// MVC + EF
builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<BatteryShopContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<IAccountRepository, AccountRepository>();
builder.Services.AddScoped<ICartRepository, CartRepository>();
builder.Services.AddScoped<ICategoryRepository, CategoryRepository>();
builder.Services.AddScoped<IAddressRepository, AddressRepository>();
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<IFavoritesRepository, FavoritesRepository>();
builder.Services.AddScoped<IInventoryRepository, InventoryRepository>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<ICartService, CartService>();
builder.Services.AddScoped<IFavoritesService, FavoritesService>();
builder.Services.AddScoped<IIyzicoPaymentService, IyzicoPaymentService>();
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<IPricingService, PricingService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<ISavedCardRepository, SavedCardRepository>();
builder.Services.AddScoped<IThreeDSStore, ThreeDSStore>();
builder.Services.AddMemoryCache();

// Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Global fixed window limiter: 100 requests per minute per IP
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 200,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // Strict policy for auth endpoints (login, register, password reset)
    options.AddPolicy("auth", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // Strict policy for payment endpoints
    options.AddPolicy("payment", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                retryAfter.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        }
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { success = false, message = "Çok fazla istek gönderdiniz. Lütfen biraz bekleyin." },
            cancellationToken);
    };
});

// Options
builder.Services.AddOptions<CurrencyOptions>()
    .Bind(builder.Configuration.GetSection("Currency"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.BaseUrl) && !string.IsNullOrWhiteSpace(o.ApiKey),
        "Currency:BaseUrl and Currency:ApiKey are required")
    .ValidateOnStart();

builder.Services.AddOptions<IyzicoOptions>()
    .Bind(builder.Configuration.GetSection("Iyzico"))
    .Validate(o =>
            !string.IsNullOrWhiteSpace(o.ApiKey) &&
            !string.IsNullOrWhiteSpace(o.SecretKey) &&
            !string.IsNullOrWhiteSpace(o.BaseUrl),
        "Iyzico configuration (ApiKey, SecretKey, BaseUrl) is required")
    .ValidateOnStart();

// Typed HttpClient for currency service
builder.Services.AddHttpClient<ICurrencyService, CurrencyService>();

// Hosted service
builder.Services.AddHostedService<FxThreeTimesDailyRefresher>();
// ⬇️ AUTH: Cookie + Google
builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
    })
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.LogoutPath = "/logout";
    })
    .AddGoogle(o =>
    {
        o.ClientId = builder.Configuration["Authentication:Google:ClientId"]!;
        o.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"]!;
        o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.SaveTokens = true;
        o.Scope.Add("email");
        o.Scope.Add("profile");
        o.ClaimActions.MapJsonKey("urn:google:picture", "picture", "url");
        o.Events.OnCreatingTicket = async ctx =>
        {
            var services = ctx.HttpContext.RequestServices;
            var db = services.GetRequiredService<BatteryShopContext>();
            var cartService = services.GetRequiredService<ICartService>();
            var logger = services.GetRequiredService<ILogger<Program>>();
            var cancellationToken = ctx.HttpContext.RequestAborted;

            var email = ctx.Identity?.FindFirst(ClaimTypes.Email)?.Value;
            if (string.IsNullOrWhiteSpace(email))
            {
                return;
            }

            var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, cancellationToken);
            var displayName = ctx.Identity?.FindFirst(ClaimTypes.Name)?.Value;

            if (user is null)
            {
                user = new User
                {
                    Email = email,
                    UserName = string.IsNullOrWhiteSpace(displayName) ? email : displayName,
                    PasswordHash = string.Empty,
                    CreatedAt = DateTime.UtcNow
                };

                db.Users.Add(user);
                await db.SaveChangesAsync(cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(user.UserName))
            {
                user.UserName = displayName;
                await db.SaveChangesAsync(cancellationToken);
            }

            if (ctx.Identity is ClaimsIdentity identity)
            {
                foreach (var existing in identity.FindAll("sub").ToList())
                {
                    identity.RemoveClaim(existing);
                }

                identity.AddClaim(new Claim("sub", user.Id.ToString(CultureInfo.InvariantCulture)));
            }

            // ✅ CART MERGE: Transfer guest cart to user during Google authentication
            var anonId = ctx.HttpContext.Request.Cookies["ANON_ID"];
            if (!string.IsNullOrWhiteSpace(anonId) && user is not null)
            {
                try
                {
                    await cartService.MergeGuestIntoUserAsync(anonId, user.Id, cancellationToken);
                    ctx.HttpContext.Response.Cookies.Delete("ANON_ID");
                    logger.LogInformation("Cart merged during Google auth for user {UserId} from anonymous ID {AnonId}",
                        user.Id, anonId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to merge cart during Google auth for user {UserId} from anonymous ID {AnonId}", user.Id,
                        anonId);
                    // Don't fail the authentication, just log the error
                }
            }
        };
        o.Backchannel = new HttpClient(new HttpLogHandler(new HttpClientHandler()));
    });

var app = builder.Build();

// Behind the droplet's TLS-terminating reverse proxy Kestrel only ever sees plain http://:8080,
// so Request.Scheme/Host must come from the proxy's forwarded headers. Without this the Google
// OAuth handler builds redirect_uri as http://... and Google rejects it (redirect_uri_mismatch).
// Must run before any middleware that reads the scheme, host or client IP.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
};
// The proxy reaches us over the Docker bridge, not loopback; the defaults only trust loopback
// and would otherwise silently discard its headers. Clear() is required here - an empty
// collection initializer (KnownNetworks = { }) compiles but adds nothing and clears nothing.
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Ev/Error");
    app.UseHsts();
}

// fetch() calls (checkout, cart...) can't parse the HTML error page, so answer them with JSON
// carrying a trace id that can be matched against the logs. Page requests still fall through
// to /Home/Error above.
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex) when (!ctx.Response.HasStarted &&
                               (ctx.Request.Headers.ContainsKey("RequestVerificationToken") ||
                                ctx.Request.Headers.Accept.ToString().Contains("application/json")))
    {
        var traceId = ctx.TraceIdentifier;
        ctx.RequestServices.GetRequiredService<ILogger<Program>>().LogError(ex,
            "Unhandled exception on {Method} {Path}. TraceId={TraceId}",
            ctx.Request.Method, ctx.Request.Path, traceId);

        ctx.Response.Clear();
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(new
        {
            success = false,
            message = $"Sunucu hatası oluştu. Lütfen tekrar deneyin. (Hata kodu: {traceId})"
        });
    }
});

// Ensure schema exists (needed after volume reset or first deploy)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BatteryShopContext>();
    try
    {
        db.Database.EnsureCreated();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Failed to ensure database is created");
    }
}

app.UseHttpsRedirection();
app.UseStaticFiles();
// One line per request (status + duration); 5xx are logged as Error so they reach the file sink too.
app.UseSerilogRequestLogging();
app.UseRouting();
app.UseRateLimiter();

// ⬇️ AUTH MIDDLEWARE ORDER
app.UseAuthentication();
app.UseAuthorization();
// Program.cs (middleware)
app.Use(async (ctx, next) =>
{
    const string Cookie = "ANON_ID";
    if (!(ctx.User?.Identity?.IsAuthenticated ?? false) &&
        !ctx.Request.Cookies.ContainsKey(Cookie))
    {
        ctx.Response.Cookies.Append(
            Cookie, Guid.NewGuid().ToString(),
            new CookieOptions { HttpOnly = true, IsEssential = true, Expires = DateTimeOffset.UtcNow.AddMonths(3) });
    }

    await next();
});

// Debug endpoint you had
app.MapPost("/debug/currency/refresh", async (ICurrencyService svc, CancellationToken ct) =>
{
    var r = await svc.RefreshNowAsync(ct);
    return Results.Ok(new { rate = r });
});


// ⬇️ Minimal login/logout routes (optional; use your own controller if preferred)
app.MapGet("/login", (HttpContext ctx) =>
{
    var props = new AuthenticationProperties { RedirectUri = "/" };
    return Results.Challenge(props, new[] { GoogleDefaults.AuthenticationScheme });
});
app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

// Example home
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Ev}/{action=Index}");

app.Run();
