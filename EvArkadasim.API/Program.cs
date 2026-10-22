// Program.cs
using MediatR;
using FluentValidation;
using Application.Common.Behaviors;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Core.Exceptions;
using Persistence;
using Core.Security.JWT;
using Application.Features.Auths.Commands.SendVerificationCode;
using Application.Services.PlannedExpenses;
using Microsoft.EntityFrameworkCore;
using Persistence.Contexts;
using Microsoft.OpenApi.Models;
using Application.Services.Repositories;
using Persistence.Repositories;
using System.Text.Json.Serialization;
using EvArkadasim.API.Services.Receipts;
using Microsoft.AspNetCore.HttpOverrides;
using Application.Services.Accounts;
using Persistence.Services;
using System.Security.Claims;
using EvArkadasim.API.Services.Notifications;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
}

builder.Configuration.AddEnvironmentVariables();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// 1) Persistence (DbContext, Repos, MailService, JwtHelper, vs.)
builder.Services.AddPersistenceServices(builder.Configuration);

// --- Repositories ---
builder.Services.AddScoped<IExpenseRepository, EfExpenseRepository>();
builder.Services.AddScoped<IPersonalExpenseRepository, EfPersonalExpenseRepository>();
builder.Services.AddScoped<IShareRepository, EfShareRepository>();
builder.Services.AddScoped<IPaymentRepository, EfPaymentRepository>();
builder.Services.AddScoped<IHouseMemberRepository, EfHouseMemberRepository>();
builder.Services.AddScoped<IHouseNoteRepository, EfHouseNoteRepository>();
builder.Services.AddScoped<IInvitationRepository, EfInvitationRepository>();
builder.Services.AddScoped<ILedgerLineRepository, EfLedgerLineRepository>();
builder.Services.AddScoped<IRecurringChargeRepository, EfRecurringChargeRepository>();
builder.Services.AddScoped<IChargeCycleRepository, EfChargeCycleRepository>();
builder.Services.AddScoped<IPlannedExpenseLedgerSyncService, PlannedExpenseLedgerSyncService>();
builder.Services.AddScoped<IAccountDeletionService, AccountDeletionService>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<IReceiptOcrService, ReceiptOcrService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
});
builder.Services.AddHttpClient<IPushNotificationService, ExpoPushNotificationService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

// 2) MediatR — Application assembly’sini tara (V11 uyumlu kayıt)
builder.Services.AddMediatR(typeof(Application.Features.Auths.Commands.SendVerificationCode.SendVerificationCodeCommand).Assembly);

// 3) FluentValidation
builder.Services.AddValidatorsFromAssembly(typeof(SendVerificationCodeCommand).Assembly);
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

// 4) JWT
var tokenOptions = builder.Configuration.GetSection("TokenOptions").Get<TokenOptions>()
    ?? throw new InvalidOperationException("TokenOptions configuration is missing.");
if (IsMissingSecret(tokenOptions.SecurityKey) ||
    IsMissingSecret(tokenOptions.Issuer) ||
    IsMissingSecret(tokenOptions.Audience) ||
    tokenOptions.AccessTokenExpiration <= 0)
{
    throw new InvalidOperationException(
        "TokenOptions must contain a secure key, issuer, audience and a positive expiration value.");
}
if (Encoding.UTF8.GetByteCount(tokenOptions.SecurityKey) < 64)
{
    throw new InvalidOperationException(
        "TokenOptions:SecurityKey must be at least 64 bytes for HS512.");
}
if (!builder.Environment.IsDevelopment() &&
    (IsMissingSecret(builder.Configuration["SmtpSettings:SenderEmail"]) ||
     IsMissingSecret(builder.Configuration["SmtpSettings:Password"])))
{
    throw new InvalidOperationException(
        "Production requires SmtpSettings:SenderEmail and SmtpSettings:Password.");
}
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidIssuer = tokenOptions.Issuer,
            ValidAudience = tokenOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(tokenOptions.SecurityKey))
        };
        opt.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var userIdValue = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!int.TryParse(userIdValue, out var userId) || userId <= 0)
                {
                    context.Fail("Invalid user identity.");
                    return;
                }

                var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var isActive = await db.Users
                    .AsNoTracking()
                    .AnyAsync(user => user.Id == userId && user.IsActive, context.HttpContext.RequestAborted);
                if (!isActive)
                    context.Fail("User account is inactive.");
            }
        };
    });

// 5) MVC + JSON + Swagger + CORS
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Roomora API", Version = "v1" });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT token'ınızı 'Bearer {token}' şeklinde girin."
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                },
                Scheme = "Bearer",
                Name = "Bearer",
                In = ParameterLocation.Header
            },
            Array.Empty<string>()
        }
    });

    c.CustomSchemaIds(t => t.FullName);
    c.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
});

// ---- CORS: Mobil/uzak test icin gelistirme ortaminda esnek tutuyoruz ----
const string RemoteTestCorsPolicy = "RemoteTestCorsPolicy";
var configuredCorsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(p =>
{
    p.AddPolicy(RemoteTestCorsPolicy, x =>
    {
        if (builder.Environment.IsDevelopment())
        {
            x
                .SetIsOriginAllowed(_ => true)
                .AllowAnyHeader()
                .AllowAnyMethod();
            return;
        }

        if (configuredCorsOrigins.Length > 0)
        {
            x
                .WithOrigins(configuredCorsOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
            return;
        }

        x
            .WithOrigins("https://evarkadasim.co", "https://www.evarkadasim.co")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
// ------------------------------------------------------------

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// 7) Kestrel: TLS is terminated by the production reverse proxy.
builder.WebHost.ConfigureKestrel(options =>
{
    // The API is not published directly in production, but it must listen on
    // every container interface so the reverse proxy can reach it.
    var configuredPort = Environment.GetEnvironmentVariable("ROOMORA_HTTP_PORT");
    var httpPort = int.TryParse(configuredPort, out var parsedPort) && parsedPort > 0
        ? parsedPort
        : 5118;
    options.ListenAnyIP(httpPort);

    var runningInContainer = string.Equals(
        Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
        "true",
        StringComparison.OrdinalIgnoreCase);

    if (builder.Environment.IsDevelopment() && !runningInContainer)
    {
        options.ListenAnyIP(7118, listen => listen.UseHttps()); // Local development HTTPS
    }
});

var app = builder.Build();

// 8) Schema changes are explicit in production so blue-green instances can
// start without racing each other. Run the image with --migrate-only first.
var migrateOnly = args.Contains("--migrate-only", StringComparer.OrdinalIgnoreCase);
var shouldMigrate = app.Environment.IsDevelopment() ||
    migrateOnly ||
    builder.Configuration.GetValue<bool>("Database:RunMigrations");

if (shouldMigrate)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    EnsureProfileColumns(db);
    EnsureReceiptTables(db);
}

if (migrateOnly)
{
    return;
}

// 9) Pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;
        context.Response.ContentType = "application/json";

        if (exception is ValidationException validationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Gonderilen veri dogrulamadan gecemedi.",
                errors = validationException.Errors.Select(error => new
                {
                    field = error.PropertyName,
                    message = error.ErrorMessage
                })
            });
            return;
        }

        if (exception is DbUpdateConcurrencyException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Bu kayıt başka bir işlem tarafından güncellendi. Verileri yenileyip tekrar deneyin."
            });
            return;
        }

        if (exception is InvalidOperationException invalidOperationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { message = invalidOperationException.Message });
            return;
        }

        if (exception is ForbiddenAccessException forbiddenAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = forbiddenAccessException.Message });
            return;
        }

        if (exception is UnauthorizedAccessException unauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { message = unauthorizedAccessException.Message });
            return;
        }

        if (exception is KeyNotFoundException keyNotFoundException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { message = keyNotFoundException.Message });
            return;
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { message = "Beklenmeyen bir sunucu hatasi olustu." });
    });
});

app.UseForwardedHeaders();

// Dikkat: HTTPS yönlendirmesi SADECE prod'da Nginx tarafından yapıldığı için 
// uygulama içinde devre dışı bırakıyoruz. Aksi takdirde CORS preflight (OPTIONS)
// istekleri yönlendirme nedeniyle başarısız oluyor.
// if (!app.Environment.IsDevelopment())
// {
//     app.UseHttpsRedirection();
// }

app.UseStaticFiles();

app.UseCors(RemoteTestCorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", async (AppDbContext db, CancellationToken cancellationToken) =>
{
    var databaseAvailable = await db.Database.CanConnectAsync(cancellationToken);
    return databaseAvailable
        ? Results.Ok(new
        {
            status = "healthy",
            service = "roomora-api",
            database = "available",
            timestamp = DateTimeOffset.UtcNow
        })
        : Results.Json(
            new
            {
                status = "unhealthy",
                service = "roomora-api",
                database = "unavailable",
                timestamp = DateTimeOffset.UtcNow
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

app.MapControllers();

app.Run();

static bool IsMissingSecret(string? value)
{
    return string.IsNullOrWhiteSpace(value) ||
        value.Trim().StartsWith("CHANGE_ME_", StringComparison.OrdinalIgnoreCase);
}

static void EnsureProfileColumns(AppDbContext db)
{
    const string sql = """
IF COL_LENGTH('Users', 'PhoneNumber') IS NULL
    ALTER TABLE [Users] ADD [PhoneNumber] NVARCHAR(16) NULL;
IF COL_LENGTH('Users', 'Iban') IS NULL
    ALTER TABLE [Users] ADD [Iban] NVARCHAR(26) NULL;
IF COL_LENGTH('Users', 'ProfileImageUrl') IS NULL
    ALTER TABLE [Users] ADD [ProfileImageUrl] NVARCHAR(1024) NULL;
""";
    db.Database.ExecuteSqlRaw(sql);
}

static void EnsureReceiptTables(AppDbContext db)
{
    const string sql = """
IF OBJECT_ID(N'[Receipts]', N'U') IS NULL
BEGIN
    CREATE TABLE [Receipts] (
        [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [HouseId] INT NOT NULL,
        [UploadedByUserId] INT NOT NULL,
        [ImageUrl] NVARCHAR(1024) NOT NULL,
        [RawOcrText] NVARCHAR(MAX) NULL,
        [StoreName] NVARCHAR(256) NULL,
        [ReceiptDate] DATETIME2 NULL,
        [DetectedTotalAmount] DECIMAL(18,2) NULL,
        [ImageWidth] INT NULL,
        [ImageHeight] INT NULL,
        [Status] INT NOT NULL DEFAULT(0),
        [ConvertedExpenseId] INT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NULL,
        CONSTRAINT [FK_Receipts_Houses_HouseId] FOREIGN KEY ([HouseId]) REFERENCES [Houses]([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_Receipts_Users_UploadedByUserId] FOREIGN KEY ([UploadedByUserId]) REFERENCES [Users]([Id])
    );

    CREATE INDEX [IX_Receipts_HouseId] ON [Receipts]([HouseId]);
    CREATE INDEX [IX_Receipts_UploadedByUserId] ON [Receipts]([UploadedByUserId]);
    CREATE INDEX [IX_Receipts_CreatedAt] ON [Receipts]([CreatedAt]);
    CREATE INDEX [IX_Receipts_HouseId_UploadedByUserId_CreatedAt] ON [Receipts]([HouseId], [UploadedByUserId], [CreatedAt]);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Receipts_CreatedAt' AND object_id = OBJECT_ID(N'[Receipts]'))
    CREATE INDEX [IX_Receipts_CreatedAt] ON [Receipts]([CreatedAt]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Receipts_HouseId_UploadedByUserId_CreatedAt' AND object_id = OBJECT_ID(N'[Receipts]'))
    CREATE INDEX [IX_Receipts_HouseId_UploadedByUserId_CreatedAt] ON [Receipts]([HouseId], [UploadedByUserId], [CreatedAt]);

IF OBJECT_ID(N'[ReceiptItems]', N'U') IS NULL
BEGIN
    CREATE TABLE [ReceiptItems] (
        [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [ReceiptId] INT NOT NULL,
        [Name] NVARCHAR(256) NOT NULL,
        [Price] DECIMAL(18,2) NOT NULL,
        [Quantity] DECIMAL(18,2) NOT NULL,
        [LineTotal] DECIMAL(18,2) NOT NULL,
        [BoxLeft] INT NULL,
        [BoxTop] INT NULL,
        [BoxWidth] INT NULL,
        [BoxHeight] INT NULL,
        [IsAssigned] BIT NOT NULL DEFAULT(0),
        [IsShared] BIT NOT NULL DEFAULT(1),
        [PersonalUserId] INT NULL,
        [ParticipantUserIdsJson] NVARCHAR(2000) NULL,
        [SortOrder] INT NOT NULL DEFAULT(0),
        CONSTRAINT [FK_ReceiptItems_Receipts_ReceiptId] FOREIGN KEY ([ReceiptId]) REFERENCES [Receipts]([Id]) ON DELETE CASCADE
    );

    CREATE INDEX [IX_ReceiptItems_ReceiptId] ON [ReceiptItems]([ReceiptId]);
END;

IF COL_LENGTH('ReceiptItems', 'BoxLeft') IS NULL
    ALTER TABLE [ReceiptItems] ADD [BoxLeft] INT NULL;
IF COL_LENGTH('ReceiptItems', 'BoxTop') IS NULL
    ALTER TABLE [ReceiptItems] ADD [BoxTop] INT NULL;
IF COL_LENGTH('ReceiptItems', 'BoxWidth') IS NULL
    ALTER TABLE [ReceiptItems] ADD [BoxWidth] INT NULL;
IF COL_LENGTH('ReceiptItems', 'BoxHeight') IS NULL
    ALTER TABLE [ReceiptItems] ADD [BoxHeight] INT NULL;
IF COL_LENGTH('ReceiptItems', 'IsAssigned') IS NULL
    ALTER TABLE [ReceiptItems] ADD [IsAssigned] BIT NOT NULL CONSTRAINT [DF_ReceiptItems_IsAssigned] DEFAULT(0);
IF COL_LENGTH('Receipts', 'ImageWidth') IS NULL
    ALTER TABLE [Receipts] ADD [ImageWidth] INT NULL;
IF COL_LENGTH('Receipts', 'ImageHeight') IS NULL
    ALTER TABLE [Receipts] ADD [ImageHeight] INT NULL;
IF COL_LENGTH('ReceiptItems', 'ParticipantUserIdsJson') IS NULL
    ALTER TABLE [ReceiptItems] ADD [ParticipantUserIdsJson] NVARCHAR(2000) NULL;
""";

    db.Database.ExecuteSqlRaw(sql);
}
