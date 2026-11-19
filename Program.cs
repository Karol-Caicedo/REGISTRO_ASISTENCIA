using System.Security.Claims;
using System.Threading.RateLimiting;
using System.Text;
using System.Text.RegularExpressions;
using Asistencia.Api.Data;
using Asistencia.Api.Domain;
using Asistencia.Api.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

var b = WebApplication.CreateBuilder(args);
var cfg = b.Configuration;
var jk = cfg["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jk) || jk.Length < 32 || jk.StartsWith("CAMBIAR"))
{
    if (!b.Environment.IsDevelopment()) throw new InvalidOperationException("Configura Jwt:Key (32+ caracteres) con variable de entorno Jwt__Key o user-secrets.");
    Console.WriteLine("ADVERTENCIA: usando Jwt:Key de ejemplo (solo desarrollo).");
}

var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
b.WebHost.UseUrls($"http://0.0.0.0:{port}");

static string ResolvePostgresConnection(IConfiguration cfg)
{
    var conn = Environment.GetEnvironmentVariable("DATABASE_URL")
        ?? Environment.GetEnvironmentVariable("DATABASE_PRIVATE_URL")
        ?? Environment.GetEnvironmentVariable("POSTGRES_URL")
        ?? cfg.GetConnectionString("Default")
        ?? "";

    var pgHost = Environment.GetEnvironmentVariable("PGHOST");
    if (!string.IsNullOrWhiteSpace(pgHost) && (string.IsNullOrWhiteSpace(conn) || conn.Contains("localhost")))
    {
        var p = Environment.GetEnvironmentVariable("PGPORT") ?? "5432";
        var db = Environment.GetEnvironmentVariable("PGDATABASE") ?? "railway";
        var user = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("PGPASSWORD") ?? "";
        return $"Host={pgHost};Port={p};Database={db};Username={user};Password={pass};SSL Mode=Prefer;Trust Server Certificate=true;Timeout=3;Command Timeout=5;";
    }

    if (conn.StartsWith("postgres://") || conn.StartsWith("postgresql://"))
    {
        try
        {
            var uri = new Uri(conn);
            var userInfo = uri.UserInfo.Split(':');
            var user = userInfo[0];
            var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
            var host = uri.Host;
            var p = uri.Port > 0 ? uri.Port : 5432;
            var db = uri.AbsolutePath.TrimStart('/');
            return $"Host={host};Port={p};Database={db};Username={user};Password={pass};SSL Mode=Prefer;Trust Server Certificate=true;Timeout=3;Command Timeout=5;";
        }
        catch { }
    }
    return conn;
}

var connStr = ResolvePostgresConnection(cfg);
b.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connStr));
var dbState = new DbProviderState();
b.Services.AddSingleton(dbState);
b.Services.AddSingleton<JsonFileAsistenciaRepository>();
b.Services.AddScoped<IAsistenciaRepository>(sp =>
{
    var state = sp.GetRequiredService<DbProviderState>();
    if (state.UsePostgres)
    {
        return new AsistenciaRepository(sp.GetRequiredService<AppDbContext>());
    }
    return sp.GetRequiredService<JsonFileAsistenciaRepository>();
});

b.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
b.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new()
    {
        ValidIssuer = cfg["Jwt:Issuer"], ValidAudience = cfg["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!)),
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true
    };
});
b.Services.AddAuthorization();
b.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = b.Build();

using (var s = app.Services.CreateScope())
{
    var state = s.ServiceProvider.GetRequiredService<DbProviderState>();
    try
    {
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.Database.CanConnect())
        {
            if (cfg.GetValue("Db:UsarMigraciones", false)) db.Database.Migrate(); 
            else db.Database.EnsureCreated();
            try
            {
                db.Database.ExecuteSqlRaw(@"ALTER TABLE ""Grupos"" ADD COLUMN IF NOT EXISTS ""Materia"" text NOT NULL DEFAULT '';");
                db.Database.ExecuteSqlRaw(@"
                    DO $$
                    BEGIN
                        IF NOT EXISTS (
                            SELECT 1 FROM pg_constraint WHERE conname = 'fk_grupos_docentes_cascade'
                        ) THEN
                            DELETE FROM ""Grupos"" WHERE ""DocenteId"" NOT IN (SELECT ""Id"" FROM ""Docentes"");
                            ALTER TABLE ""Grupos""
                            ADD CONSTRAINT fk_grupos_docentes_cascade
                            FOREIGN KEY (""DocenteId"") REFERENCES ""Docentes""(""Id"")
                            ON DELETE CASCADE;
                        END IF;
                    END $$;
                ");
                db.Database.ExecuteSqlRaw(@"
                    DELETE FROM ""Registros"" WHERE ""Deleted"" = true;
                    DELETE FROM ""Estudiantes"" WHERE ""Deleted"" = true;
                    DELETE FROM ""Grupos"" WHERE ""Deleted"" = true;
                ");
            }
            catch { }
            state.UsePostgres = true;
            Console.WriteLine("[DB] Conectado exitosamente a PostgreSQL.");
        }
        else
        {
            state.UsePostgres = false;
            Console.WriteLine("[DB] PostgreSQL no disponible. Usando base de datos persistente en data/asistencia_db.json.");
        }
    }
    catch
    {
        state.UsePostgres = false;
        Console.WriteLine("[DB] PostgreSQL no disponible. Usando base de datos persistente en data/asistencia_db.json.");
    }
}

if (cfg.GetValue("Https:Forzar", !app.Environment.IsDevelopment())) { app.UseHsts(); app.UseHttpsRedirection(); }
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
            ctx.Context.Response.Headers.Append("Pragma", "no-cache");
            ctx.Context.Response.Headers.Append("Expires", "0");
        }
    }
});
app.UseCors(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();

string Token(Docente u) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
{
    Subject = new ClaimsIdentity([new Claim("sub", u.Id.ToString()), new Claim("email", u.Email)]),
    Issuer = cfg["Jwt:Issuer"], Audience = cfg["Jwt:Audience"],
    Expires = DateTime.UtcNow.AddHours(cfg.GetValue("Jwt:Horas", 12)),
    SigningCredentials = new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!)), SecurityAlgorithms.HmacSha256)
});
static Guid Uid(ClaimsPrincipal u) => Guid.Parse(u.FindFirstValue("sub")!);
static bool Valido(GrupoDto g) => !string.IsNullOrWhiteSpace(g.Carrera) && !string.IsNullOrWhiteSpace(g.Jornada)
    && g.Horarios.All(h => h.Dia is >= 0 and <= 6 && Regex.IsMatch(h.Inicio, @"^\d{2}:\d{2}$") && Regex.IsMatch(h.Fin, @"^\d{2}:\d{2}$"));
static GrupoDto Map(Grupo g) => new(g.Id, g.Carrera, g.Jornada, g.Horarios.Select(h => new HorarioDto(h.Dia, h.Inicio, h.Fin)).ToList(), g.Materia);

// ===== Autenticación =====
app.MapGet("/api/health", () => Results.Ok("ok"));
app.MapPost("/api/auth/register", async (AuthReq r, IAsistenciaRepository repo) =>
{
    var em = r.Email.Trim().ToLowerInvariant();
    if (!Regex.IsMatch(em, @"^[a-z0-9._+-]+@gmail\.com$")) return Results.BadRequest(new { error = "Usa un correo Gmail válido." });
    if (!Regex.IsMatch(r.Password, @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).{8,}$")) return Results.BadRequest(new { error = "La contraseña debe tener mínimo 8 caracteres con mayúsculas, minúsculas y números." });
    if (await repo.GetDocenteByEmail(em) is not null) return Results.Conflict(new { error = "Ese correo ya está registrado." });
    var u = await repo.CreateDocente(em, BCrypt.Net.BCrypt.HashPassword(r.Password));
    return Results.Ok(new AuthRes(Token(u), em));
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", async (AuthReq r, IAsistenciaRepository repo) =>
{
    var em = r.Email.Trim().ToLowerInvariant();
    var u = await repo.GetDocenteByEmail(em);
    return u is null || !BCrypt.Net.BCrypt.Verify(r.Password, u.PasswordHash)
        ? Results.Json(new { error = "Correo o contraseña incorrectos." }, statusCode: 401)
        : Results.Ok(new AuthRes(Token(u), em));
}).RequireRateLimiting("auth");
var resetTokens = new System.Collections.Concurrent.ConcurrentDictionary<string, (string Email, DateTime Exp)>();

app.MapPost("/api/auth/forgot-password", async (ForgotReq r, IAsistenciaRepository repo, HttpContext ctx) =>
{
    var em = r.Email?.Trim().ToLowerInvariant() ?? "";
    if (!Regex.IsMatch(em, @"^[a-z0-9._+-]+@gmail\.com$")) return Results.BadRequest(new { error = "Usa un correo Gmail válido." });
    var u = await repo.GetDocenteByEmail(em);
    if (u is null) return Results.NotFound(new { error = "No se encontró una cuenta con ese correo." });
    var tok = Guid.NewGuid().ToString("N");
    resetTokens[tok] = (em, DateTime.UtcNow.AddHours(1));
    var link = $"{ctx.Request.Scheme}://{ctx.Request.Host}/?token={tok}&email={Uri.EscapeDataString(em)}";
    return Results.Ok(new { message = "Enlace de recuperación generado.", link });
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/reset-with-token", async (ResetWithTokenReq r, IAsistenciaRepository repo) =>
{
    var em = r.Email?.Trim().ToLowerInvariant() ?? "";
    if (!resetTokens.TryGetValue(r.Token, out var entry) || entry.Email != em || entry.Exp < DateTime.UtcNow)
        return Results.BadRequest(new { error = "El enlace de recuperación es inválido o ha expirado." });
    if (!Regex.IsMatch(r.NewPassword, @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).{8,}$"))
        return Results.BadRequest(new { error = "La nueva contraseña debe tener mínimo 8 caracteres con mayúsculas, minúsculas y números." });
    if (!await repo.UpdatePassword(em, BCrypt.Net.BCrypt.HashPassword(r.NewPassword)))
        return Results.NotFound(new { error = "Docente no encontrado." });
    resetTokens.TryRemove(r.Token, out _);
    return Results.Ok(new { message = "Contraseña actualizada correctamente." });
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/reset-password", async (AuthReq r, IAsistenciaRepository repo) =>
{
    var em = r.Email.Trim().ToLowerInvariant();
    if (!Regex.IsMatch(em, @"^[a-z0-9._+-]+@gmail\.com$")) return Results.BadRequest(new { error = "Usa un correo Gmail válido." });
    if (!Regex.IsMatch(r.Password, @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).{8,}$")) return Results.BadRequest(new { error = "La nueva contraseña debe tener mínimo 8 caracteres con mayúsculas, minúsculas y números." });
    if (!await repo.UpdatePassword(em, BCrypt.Net.BCrypt.HashPassword(r.Password)))
        return Results.NotFound(new { error = "No se encontró una cuenta con ese correo." });
    return Results.Ok(new { message = "Contraseña actualizada correctamente." });
}).RequireRateLimiting("auth");

// ===== API protegida (cada docente solo ve lo suyo) =====
var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/grupos", async (ClaimsPrincipal u, IAsistenciaRepository r) => (await r.Grupos(Uid(u))).Select(Map));
api.MapPost("/grupos", async (ClaimsPrincipal u, GrupoDto g, IAsistenciaRepository r) =>
    !Valido(g) ? Results.BadRequest(new { error = "Grupo u horario inválido." }) : Results.Ok(Map(await r.CrearGrupo(Uid(u), g))));
api.MapPut("/grupos/{id:guid}", async (ClaimsPrincipal u, Guid id, GrupoDto g, IAsistenciaRepository r) =>
    !Valido(g) ? Results.BadRequest(new { error = "Grupo u horario inválido." }) : await r.ActualizarGrupo(Uid(u), id, g) ? Results.NoContent() : Results.NotFound());
api.MapDelete("/grupos/{id:guid}", async (ClaimsPrincipal u, Guid id, IAsistenciaRepository r) =>
    await r.EliminarGrupo(Uid(u), id) ? Results.NoContent() : Results.NotFound());

api.MapGet("/grupos/{id:guid}/estudiantes", async (ClaimsPrincipal u, Guid id, IAsistenciaRepository r) =>
    await r.Grupo(Uid(u), id) is { } g ? Results.Ok(g.Estudiantes.OrderBy(e => e.Codigo).Select(e => new { e.Id, e.Codigo, e.NombreCompleto })) : Results.NotFound());
api.MapPost("/grupos/{id:guid}/estudiantes", async (ClaimsPrincipal u, Guid id, List<EstudianteDto> items, IAsistenciaRepository r) =>
    await r.AgregarEstudiantes(Uid(u), id, items) is var n && n >= 0 ? Results.Ok(new { agregados = n }) : Results.NotFound());
api.MapDelete("/grupos/{id:guid}/estudiantes/{eid:guid}", async (ClaimsPrincipal u, Guid id, Guid eid, IAsistenciaRepository r) =>
    await r.EliminarEstudiante(Uid(u), id, eid) ? Results.NoContent() : Results.NotFound());

api.MapPut("/grupos/{id:guid}/asistencia", async (ClaimsPrincipal u, Guid id, AsistenciaReq req, IAsistenciaRepository r) =>
    await r.GuardarAsistencia(Uid(u), id, req) ? Results.NoContent() : Results.NotFound());
// GET /api/grupos/{id}/historico?mes=2026-11&fecha=2026-11-17&estado=asistieron|faltaron
api.MapGet("/grupos/{id:guid}/historico", async (ClaimsPrincipal u, Guid id, string mes, DateOnly? fecha, string? estado, IAsistenciaRepository r) =>
    !DateOnly.TryParseExact(mes + "-01", "yyyy-MM-dd", out var m) ? Results.BadRequest(new { error = "mes debe ser AAAA-MM" })
    : await r.Historico(Uid(u), id, m, fecha, estado) is { } h ? Results.Ok(h) : Results.NotFound());
api.MapGet("/grupos/{id:guid}/resumen", async (ClaimsPrincipal u, Guid id, string? q, IAsistenciaRepository r) =>
    await r.Resumen(Uid(u), id, q) is { } x ? Results.Ok(x) : Results.NotFound());

// ===== Sincronización =====
api.MapGet("/sync", async (ClaimsPrincipal u, DateTime? since, IAsistenciaRepository r) =>
{
    try
    {
        var uid = Uid(u);
        var res = await r.Pull(uid, since ?? DateTime.UnixEpoch);
        Console.WriteLine($"[SYNC-PULL] Docente {uid}: retornando {res.Grupos.Count} grupos, {res.Estudiantes.Count} estudiantes.");
        return Results.Ok(res);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[SYNC-PULL ERROR] {ex.Message}");
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});

api.MapPost("/sync", async (ClaimsPrincipal u, SyncPayload p, IAsistenciaRepository r) =>
{
    try
    {
        var uid = Uid(u);
        Console.WriteLine($"[SYNC-PUSH] Docente {uid}: guardando {p.Grupos.Count} grupos, {p.Estudiantes.Count} estudiantes, {p.Registros.Count} marcas.");
        await r.Push(uid, p);
        return Results.NoContent();
    }
    catch (DbUpdateConcurrencyException ex)
    {
        // En sincronización distribuida, si una fila a eliminar/modificar ya no existe en la BD,
        // significa que el estado deseado (eliminada) ya se cumplió.
        Console.WriteLine($"[SYNC-CONCURRENCY-HANDLED] {ex.Message}");
        return Results.NoContent();
    }
    catch (Exception ex)
    {
        // El motivo real de un fallo de SaveChanges suele estar en la excepción interna.
        var real = ex.InnerException?.Message ?? ex.Message;
        Console.WriteLine($"[SYNC-PUSH ERROR] {ex}");
        return Results.Problem(detail: real, statusCode: 500);
    }
});

app.Run();

public class DbProviderState
{
    public bool UsePostgres { get; set; }
}