using Asistencia.Api.Data;
using Asistencia.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Asistencia.Api.Repositories;

public class AsistenciaRepository(AppDbContext db) : IAsistenciaRepository
{
    static Horario H(HorarioDto h) => new() { Dia = h.Dia, Inicio = h.Inicio, Fin = h.Fin };
    static HorarioDto HD(Horario h) => new(h.Dia, h.Inicio, h.Fin);
    static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    public Task<List<Grupo>> Grupos(Guid d) =>
        db.Grupos.Include(g => g.Horarios).Where(g => g.DocenteId == d && !g.Deleted)
          .OrderBy(g => g.Carrera).ThenBy(g => g.Jornada).ToListAsync();

    public Task<Grupo?> Grupo(Guid d, Guid id) =>
        db.Grupos.Include(g => g.Horarios).Include(g => g.Estudiantes.Where(e => !e.Deleted))
          .FirstOrDefaultAsync(g => g.Id == id && g.DocenteId == d && !g.Deleted);

    public async Task<Grupo> CrearGrupo(Guid d, GrupoDto x)
    {
        var g = new Grupo { DocenteId = d, Carrera = x.Carrera.Trim(), Jornada = x.Jornada.Trim(), Horarios = x.Horarios.Select(H).ToList() };
        if (x.Id is Guid id) g.Id = id;
        db.Grupos.Add(g); await db.SaveChangesAsync(); return g;
    }

    public async Task<bool> ActualizarGrupo(Guid d, Guid id, GrupoDto x)
    {
        var g = await Grupo(d, id); if (g is null) return false;
        g.Carrera = x.Carrera.Trim(); g.Jornada = x.Jornada.Trim(); g.UpdatedAt = DateTime.UtcNow;
        db.Horarios.RemoveRange(g.Horarios); g.Horarios = x.Horarios.Select(H).ToList();
        await db.SaveChangesAsync(); return true;
    }

    public async Task<bool> EliminarGrupo(Guid d, Guid id)
    {
        var g = await db.Grupos.FirstOrDefaultAsync(g => g.Id == id && g.DocenteId == d && !g.Deleted);
        if (g is null) return false;
        g.Deleted = true; g.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(); return true;
    }

    public async Task<int> AgregarEstudiantes(Guid d, Guid gid, IEnumerable<EstudianteDto> items)
    {
        var g = await Grupo(d, gid); if (g is null) return -1;
        var have = g.Estudiantes.Select(e => e.Codigo).ToHashSet();
        var nuevos = items
            .Where(i => !string.IsNullOrWhiteSpace(i.Codigo) && !string.IsNullOrWhiteSpace(i.NombreCompleto) && have.Add(i.Codigo.Trim()))
            .Select(i => new Estudiante { GrupoId = gid, Codigo = i.Codigo.Trim(), NombreCompleto = i.NombreCompleto.Trim() }).ToList();
        db.Estudiantes.AddRange(nuevos); await db.SaveChangesAsync(); return nuevos.Count;
    }

    public async Task<bool> EliminarEstudiante(Guid d, Guid gid, Guid eid)
    {
        var e = await db.Estudiantes.FirstOrDefaultAsync(e => e.Id == eid && e.GrupoId == gid && e.Grupo!.DocenteId == d && !e.Deleted);
        if (e is null) return false;
        e.Deleted = true; e.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(); return true;
    }

    public async Task<bool> GuardarAsistencia(Guid d, Guid gid, AsistenciaReq req)
    {
        var g = await Grupo(d, gid); if (g is null) return false;
        var ids = g.Estudiantes.Select(e => e.Id).ToHashSet();
        var ex = await db.Registros.Where(r => r.Fecha == req.Fecha && ids.Contains(r.EstudianteId)).ToListAsync();
        foreach (var m in req.Marcas.Where(m => ids.Contains(m.EstudianteId)))
        {
            var r = ex.FirstOrDefault(x => x.EstudianteId == m.EstudianteId);
            if (r is null) db.Registros.Add(new Registro { EstudianteId = m.EstudianteId, Fecha = req.Fecha, Presente = m.Presente });
            else { r.Presente = m.Presente; r.Deleted = false; r.UpdatedAt = DateTime.UtcNow; }
        }
        await db.SaveChangesAsync(); return true;
    }

    // Días de clase del mes según el horario del grupo + fechas con registros
    static List<DateOnly> FechasClase(Grupo g, DateOnly ini, DateOnly fin, IEnumerable<DateOnly> reg)
    {
        var dias = g.Horarios.Select(h => h.Dia).ToHashSet();
        var s = new SortedSet<DateOnly>(reg);
        for (var f = ini; f < fin; f = f.AddDays(1)) if (dias.Contains((int)f.DayOfWeek)) s.Add(f);
        return s.ToList();
    }

    public async Task<object?> Historico(Guid d, Guid gid, DateOnly mes, DateOnly? fecha, string? estado)
    {
        var g = await Grupo(d, gid); if (g is null) return null;
        var ini = new DateOnly(mes.Year, mes.Month, 1); var fin = ini.AddMonths(1);
        var regs = await db.Registros.Where(r => !r.Deleted && r.Fecha >= ini && r.Fecha < fin && r.Estudiante!.GrupoId == gid).ToListAsync();
        var fechas = FechasClase(g, ini, fin, regs.Select(r => r.Fecha));
        var filas = g.Estudiantes.OrderBy(e => e.Codigo).Select(e =>
        {
            var mine = regs.Where(r => r.EstudianteId == e.Id && (fecha == null || r.Fecha == fecha)).OrderBy(r => r.Fecha).ToList();
            return new { e.Id, e.Codigo, e.NombreCompleto,
                Registros = mine.Select(r => new { r.Fecha, r.Presente }).ToList(),
                Asistencias = mine.Count(r => r.Presente), Faltas = mine.Count(r => !r.Presente) };
        }).Where(f => estado == "asistieron" ? f.Asistencias > 0 : estado == "faltaron" ? f.Faltas > 0 : true).ToList();
        return new { mes = $"{ini.Year}-{ini.Month:00}", fechas, filas };
    }

    public async Task<object?> Resumen(Guid d, Guid gid, string? q)
    {
        var g = await Grupo(d, gid); if (g is null) return null;
        var regs = await db.Registros.Where(r => !r.Deleted && r.Estudiante!.GrupoId == gid).ToListAsync();
        var por = g.Estudiantes
            .Where(e => string.IsNullOrWhiteSpace(q) || e.Codigo.Contains(q, StringComparison.OrdinalIgnoreCase) || e.NombreCompleto.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(e => new { e.Id, e.Codigo, e.NombreCompleto,
                Asistencias = regs.Count(r => r.EstudianteId == e.Id && r.Presente),
                Faltas = regs.Count(r => r.EstudianteId == e.Id && !r.Presente) }).ToList();
        var p = regs.Count(r => r.Presente);
        return new { totalEstudiantes = g.Estudiantes.Count, clases = regs.Select(r => r.Fecha).Distinct().Count(),
                     porcentajeAsistencia = regs.Count == 0 ? 0 : Math.Round(100.0 * p / regs.Count), estudiantes = por };
    }

    // ===== Sincronización: last-write-wins por UpdatedAt =====
    public async Task<SyncPayload> Pull(Guid d, DateTime since)
    {
        since = Utc(since);
        var gs = await db.Grupos.Include(g => g.Horarios).Where(g => g.DocenteId == d && g.UpdatedAt > since).ToListAsync();
        var gids = await db.Grupos.Where(g => g.DocenteId == d).Select(g => g.Id).ToListAsync();
        var es = await db.Estudiantes.Where(e => gids.Contains(e.GrupoId) && e.UpdatedAt > since).ToListAsync();
        var rs = await db.Registros.Where(r => gids.Contains(r.Estudiante!.GrupoId) && r.UpdatedAt > since).ToListAsync();
        return new SyncPayload(
            gs.Select(g => new SyncGrupo(g.Id, g.Carrera, g.Jornada, g.Horarios.Select(HD).ToList(), g.UpdatedAt, g.Deleted)).ToList(),
            es.Select(e => new SyncEstudiante(e.Id, e.GrupoId, e.Codigo, e.NombreCompleto, e.UpdatedAt, e.Deleted)).ToList(),
            rs.Select(r => new SyncRegistro(r.Id, r.EstudianteId, r.Fecha, r.Presente, r.UpdatedAt, r.Deleted)).ToList(),
            DateTime.UtcNow);
    }

    public async Task Push(Guid d, SyncPayload p)
    {
        var mine = await db.Grupos.Include(g => g.Horarios).Where(g => g.DocenteId == d).ToDictionaryAsync(g => g.Id);
        foreach (var s in p.Grupos)
        {
            var t = Utc(s.UpdatedAt);
            if (mine.TryGetValue(s.Id, out var g))
            {
                if (t <= g.UpdatedAt) continue;
                g.Carrera = s.Carrera; g.Jornada = s.Jornada; g.Deleted = s.Deleted; g.UpdatedAt = t;
                db.Horarios.RemoveRange(g.Horarios); g.Horarios = s.Horarios.Select(H).ToList();
            }
            else if (!await db.Grupos.AnyAsync(x => x.Id == s.Id))
            {
                var n = new Grupo { Id = s.Id, DocenteId = d, Carrera = s.Carrera, Jornada = s.Jornada, Deleted = s.Deleted, UpdatedAt = t, Horarios = s.Horarios.Select(H).ToList() };
                db.Grupos.Add(n); mine[n.Id] = n;
            }
        }
        await db.SaveChangesAsync();

        var gids = mine.Keys.ToList();
        var est = await db.Estudiantes.Where(e => gids.Contains(e.GrupoId)).ToDictionaryAsync(e => e.Id);
        foreach (var s in p.Estudiantes)
        {
            var t = Utc(s.UpdatedAt);
            if (est.TryGetValue(s.Id, out var e))
            {
                if (t <= e.UpdatedAt) continue;
                e.Codigo = s.Codigo; e.NombreCompleto = s.NombreCompleto; e.Deleted = s.Deleted; e.UpdatedAt = t;
            }
            else if (mine.ContainsKey(s.GrupoId) && !await db.Estudiantes.AnyAsync(x => x.Id == s.Id))
            {
                var n = new Estudiante { Id = s.Id, GrupoId = s.GrupoId, Codigo = s.Codigo, NombreCompleto = s.NombreCompleto, Deleted = s.Deleted, UpdatedAt = t };
                db.Estudiantes.Add(n); est[n.Id] = n;
            }
        }
        await db.SaveChangesAsync();

        var eids = est.Keys.ToList();
        var reg = (await db.Registros.Where(r => eids.Contains(r.EstudianteId)).ToListAsync()).ToDictionary(r => (r.EstudianteId, r.Fecha));
        foreach (var s in p.Registros)
        {
            if (!est.ContainsKey(s.EstudianteId)) continue;
            var t = Utc(s.UpdatedAt);
            if (reg.TryGetValue((s.EstudianteId, s.Fecha), out var r))
            {
                if (t <= r.UpdatedAt) continue;
                r.Presente = s.Presente; r.Deleted = s.Deleted; r.UpdatedAt = t;
            }
            else
            {
                var n = new Registro { Id = s.Id, EstudianteId = s.EstudianteId, Fecha = s.Fecha, Presente = s.Presente, Deleted = s.Deleted, UpdatedAt = t };
                db.Registros.Add(n); reg[(n.EstudianteId, n.Fecha)] = n;
            }
        }
        await db.SaveChangesAsync();
    }
}
