using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using System.Net;
using HTBAM.Api.Support;
using HTBAM.Application.Interfaces;
using HTBAM.Application.Services;
using HTBAM.Infrastructure.Data;
using HTBAM.Infrastructure.Services;
using HTBAM.Infrastructure.Services.Llm;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuestPDF.Infrastructure;

var migrationMode = args.Contains("--migrate");
var seedMode = args.Contains("--seed");
var builder = WebApplication.CreateBuilder(args.Where(x => x is not ("--migrate" or "--seed")).ToArray());

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();

builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "web",
        policy =>
            policy
                .WithOrigins(
                    builder.Configuration
                        .GetSection("Cors:Origins")
                        .Get<string[]>()
                    ?? ["http://localhost:5173"])
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials());
});

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("SqlServer"),
        sql =>
            sql.EnableRetryOnFailure(
                5,
                TimeSpan.FromSeconds(5),
                null));
});

builder.Services.AddScoped<IAppDbContext>(
    serviceProvider =>
        serviceProvider.GetRequiredService<AppDbContext>());

builder.Services.AddSingleton<IObjectStorage, MinioObjectStorage>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IAlertEngine, AlertEngine>();
builder.Services.AddScoped<IFaceEnrollmentService, FaceEnrollmentService>();
builder.Services.AddScoped<ISummaryService, SummaryService>();
builder.Services.AddScoped<ISessionService, SessionService>();
builder.Services.AddHostedService<SessionFinalizationWorker>();
builder.Services.AddScoped<IAiEventService, AiEventService>();
builder.Services.AddScoped<IAutoCommentService, AutoCommentService>();
builder.Services.AddSingleton<GeminiLlmProvider>();
builder.Services.AddSingleton<UnavailableLlmProvider>();
builder.Services.AddSingleton<ILlmProviderResolver, LlmProviderResolver>();

builder.Services.AddHttpClient<IAiClient, AiClient>(client =>
{
    client.BaseAddress =
        new Uri(
            builder.Configuration["AiService:BaseUrl"]
            ?? "http://localhost:8001/");

    client.Timeout = TimeSpan.FromSeconds(30);
});

var authRateLimit =
    builder.Configuration
        .GetSection("AuthRateLimit")
        .Get<AuthRateLimitSettings>()
    ?? new AuthRateLimitSettings();

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy(
        "auth",
        context =>
            AuthRateLimitPolicy.Partition(
                context,
                authRateLimit));

    options.RejectionStatusCode =
        StatusCodes.Status429TooManyRequests;
    options.OnRejected =
        (context, cancellationToken) =>
            AuthRateLimitPolicy.WriteRejectedAsync(
                context.HttpContext,
                context.Lease,
                authRateLimit.WindowSeconds,
                cancellationToken);
});

var key = Encoding.UTF8.GetBytes(
    builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret missing"));

if (key.Length < 32)
{
    throw new InvalidOperationException(
        "Jwt:Secret phải tối thiểu 32 bytes.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters =
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer =
                    builder.Configuration["Jwt:Issuer"],
                ValidAudience =
                    builder.Configuration["Jwt:Audience"],
                IssuerSigningKey =
                    new SymmetricSecurityKey(key),
                ClockSkew = TimeSpan.FromMinutes(1),
            };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token =
                    context.Request.Query["access_token"];

                if (
                    !string.IsNullOrEmpty(token) &&
                    context.HttpContext.Request.Path
                        .StartsWithSegments("/hubs"))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },

            OnTokenValidated = async context =>
            {
                var idText =
                    context.Principal?.FindFirstValue(
                        ClaimTypes.NameIdentifier);

                var versionText =
                    context.Principal?.FindFirstValue(
                        "token_version");

                if (
                    !long.TryParse(idText, out var id) ||
                    !int.TryParse(versionText, out var version))
                {
                    context.Fail("Invalid token claims");
                    return;
                }

                var db =
                    context.HttpContext.RequestServices
                        .GetRequiredService<AppDbContext>();

                var user =
                    await db.UsersSet
                        .AsNoTracking()
                        .Where(item => item.Id == id)
                        .Select(item => new
                        {
                            item.Status,
                            item.TokenVersion,
                        })
                        .FirstOrDefaultAsync();

                if (
                    user is null ||
                    user.Status != "ACTIVE" ||
                    user.TokenVersion != version)
                {
                    context.Fail("Token revoked");
                }
            },
        };
    });

builder.Services.AddAuthorization();

QuestPDF.Settings.License =
    LicenseType.Community;

var app = builder.Build();

if (migrationMode)
{
    try
    {
        await SqlMigrationRunner.RunAsync(builder.Configuration.GetConnectionString("SqlServer")!,
            SqlMigrationRunner.FindDirectory(builder.Configuration["Database:MigrationDirectory"]), Console.WriteLine,
            readinessTimeout: TimeSpan.FromSeconds(Math.Clamp(builder.Configuration.GetValue("Database:ReadinessTimeoutSeconds", 120), 1, 600)));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Migration thất bại: {ex.Message} Nguyên nhân: {ex.GetBaseException().Message}");
        Environment.ExitCode = ex is TimeoutException ? 2 : 1;
    }
    return;
}

if (seedMode)
{
    try
    {
    using var seedScope = app.Services.CreateScope();
    var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
    await SqlMigrationRunner.VerifyAsync(seedDb, SqlMigrationRunner.FindDirectory(builder.Configuration["Database:MigrationDirectory"]));
    await BusinessTransaction.Run(seedDb, "HTBAM:system-seed", async () =>
    {
        await SystemSeed.SeedAsync(seedDb);
        await AcademicSeed.SeedAsync(seedDb);
        return true;
    }, CancellationToken.None);
    seedDb.DevelopmentDataEnabled = app.Environment.IsDevelopment() && builder.Configuration.GetValue("Seed:Development", false);
    if (seedDb.DevelopmentDataEnabled)
    {
        await DemoDataSeeder.SeedAsync(seedDb, builder.Configuration["SEED_DEFAULT_PASSWORD"]);
        Console.WriteLine("Development seed complete. Accounts (passwords and hashes omitted):");
        foreach (var name in DemoDataSeeder.Accounts) Console.WriteLine($"demo.{name}: {(name == "admin" ? "ADMIN" : "LECTURER")} / {name}");
    }
    else Console.WriteLine("Development seed disabled. No demo accounts or records created.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Seed thất bại: {ex.Message}");
        Environment.ExitCode = 1;
    }
    return;
}

app.UseMiddleware<ApiExceptionMiddleware>();

app.UseForwardedHeaders(
    new ForwardedHeadersOptions
    {
        ForwardedHeaders =
            ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1,
        KnownNetworks =
        {
            new Microsoft.AspNetCore.HttpOverrides.IPNetwork(
                IPAddress.Parse("172.16.0.0"),
                12),
        },
    });

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("web");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet(
        "/health",
        () =>
            Results.Ok(
                new
                {
                    status = "ok",
                }))
    .AllowAnonymous();

app.MapControllers();

app.MapHub<HTBAM.Api.Hubs.SessionHub>(
    "/hubs/session");

using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("HTBAM.Startup");

    var db =
        scope.ServiceProvider
            .GetRequiredService<AppDbContext>();

    await RetryAsync(
        async () =>
        {
            if (!await db.Database.CanConnectAsync())
            {
                throw new InvalidOperationException(
                    "SQL Server chưa sẵn sàng.");
            }
        },
        logger,
        "SQL Server");

    // Schema mistakes are permanent until migrated: do not retry seed queries for a minute.
    await SqlMigrationRunner.VerifyAsync(db, SqlMigrationRunner.FindDirectory(builder.Configuration["Database:MigrationDirectory"]));

    if (
        builder.Configuration.GetValue(
            "ObjectStorage:EnsureBucketOnStartup",
            true))
    {
        var storage =
            scope.ServiceProvider
                .GetRequiredService<IObjectStorage>();

        await RetryAsync(
            () => storage.EnsureReadyAsync(),
            logger,
            "MinIO");
    }
}

app.Use(async (context, next) =>
{
    var db = context.RequestServices.GetRequiredService<AppDbContext>();
    db.DevelopmentDataEnabled = app.Environment.IsDevelopment() && builder.Configuration.GetValue("Seed:Development", false);
    await next();
});

app.Run();

static async Task RetryAsync(
    Func<Task> operation,
    ILogger logger,
    string operationName,
    int maxAttempts = 20,
    int delaySeconds = 3)
{
    Exception? lastError = null;

    for (
        var attempt = 1;
        attempt <= maxAttempts;
        attempt++)
    {
        try
        {
            await operation();

            logger.LogInformation(
                "{Operation} ready on attempt {Attempt}.",
                operationName,
                attempt);

            return;
        }
        catch (Exception error)
        {
            lastError = error;

            logger.LogWarning(
                error,
                "{Operation} chưa sẵn sàng. Lần thử {Attempt}/{MaxAttempts}.",
                operationName,
                attempt,
                maxAttempts);

            if (attempt < maxAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(delaySeconds));
            }
        }
    }

    throw new InvalidOperationException(
        $"{operationName} không sẵn sàng sau {maxAttempts} lần thử.",
        lastError);
}
