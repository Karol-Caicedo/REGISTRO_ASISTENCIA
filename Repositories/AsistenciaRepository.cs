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
        var g = new Grupo { DocenteId = d, Carrera = x.Carrera.Trim(), Jornada = x.Jornada.Trim(), Materia = (x.Materia ?? "").Trim(), Horarios = x.Horarios.Select(H).ToList() };
        if (x.Id is Guid id) g.Id = id;
        db.Grupos.Add(g); await db.SaveChangesAsync(); return g;
    }

    public async Task<bool> ActualizarGrupo(Guid d, Guid id, GrupoDto x)
    {
        var g = await Grupo(d, id); if (g is null) return false;
        g.Carrera = x.Carrera.Trim(); g.Jornada = x.Jornada.Trim(); g.Materia = (x.Materia ?? "").Trim(); g.UpdatedAt = DateTime.UtcNow;
        db.Horarios.RemoveRange(g.Horarios); g.Horarios = x.Horarios.Select(H).ToList();
        await db.SaveChangesAsync(); return true;
    }

    public async Task<bool> EliminarGrupo(Guid d, Guid id)
    {
        var g = await db.Grupos.FirstOrDefaultAsync(g => g.Id == id && g.DocenteId == d);
        if (g is null) return false;
        db.Grupos.Remove(g);
        await SafeSaveChanges();
        return true;
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
        var e = await db.Estudiantes.Include(x => x.Grupo).FirstOrDefaultAsync(e => e.Id == eid && (e.GrupoId == gid || (e.Grupo != null && e.Grupo.DocenteId == d)));
        if (e is null) return false;
        db.Estudiantes.Remove(e);
        await SafeSaveChanges();
        return true;
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
        var gs = await db.Grupos.Include(g => g.Horarios).Where(g => g.DocenteId == d && !g.Deleted).ToListAsync();
        var gids = gs.Select(g => g.Id).ToList();
        var es = await db.Estudiantes.Where(e => gids.Contains(e.GrupoId) && !e.Deleted).ToListAsync();
        var eids = es.Select(e => e.Id).ToHashSet();
        var rs = await db.Registros.Where(r => eids.Contains(r.EstudianteId) && !r.Deleted).ToListAsync();
        return new SyncPayload(
            gs.Select(g => new SyncGrupo(g.Id, g.Carrera, g.Jornada, g.Horarios.Select(HD).ToList(), g.UpdatedAt, false, g.Materia)).ToList(),
            es.Select(e => new SyncEstudiante(e.Id, e.GrupoId, e.Codigo, e.NombreCompleto, e.UpdatedAt, false)).ToList(),
            rs.Select(r => new SyncRegistro(r.Id, r.EstudianteId, r.Fecha.ToString("yyyy-MM-dd"), r.Presente, r.UpdatedAt, false)).ToList(),
            DateTime.UtcNow);
    }

    private async Task SafeSaveChanges()
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            foreach (var entry in ex.Entries)
            {
                entry.State = EntityState.Detached;
            }
            try
            {
                await db.SaveChangesAsync();
            }
            catch { }
        }
    }

    public async Task Push(Guid d, SyncPayload p)
    {
        var now = DateTime.UtcNow;
        var mine = await db.Grupos.Include(g => g.Horarios).Where(g => g.DocenteId == d).ToDictionaryAsync(g => g.Id);
        foreach (var s in p.Grupos)
        {
            var gid = s.Id ?? Guid.NewGuid();
            var t = Utc(s.UpdatedAt);
            var effectiveTime = t > now ? t : now;

            if (s.Deleted)
            {
                if (mine.TryGetValue(gid, out var gDel))
                {
                    db.Grupos.Remove(gDel);
                    mine.Remove(gid);
                }
                else
                {
                    var exG = await db.Grupos.FirstOrDefaultAsync(x => x.DocenteId == d && x.Carrera == s.Carrera && x.Jornada == s.Jornada && x.Materia == (s.Materia ?? ""));
                    if (exG is not null)
                    {
                        db.Grupos.Remove(exG);
                    }
                }
                continue;
            }

            if (mine.TryGetValue(gid, out var g))
            {
                g.Carrera = s.Carrera; g.Jornada = s.Jornada; g.Materia = s.Materia ?? ""; g.Deleted = false; g.UpdatedAt = effectiveTime;
                if (g.Horarios.Count > 0) db.Horarios.RemoveRange(g.Horarios);
                foreach (var h in s.Horarios)
                {
                    db.Horarios.Add(new Horario { Id = Guid.NewGuid(), GrupoId = g.Id, Dia = h.Dia, Inicio = h.Inicio, Fin = h.Fin });
                }
            }
            else
            {
                var existingGroup = await db.Grupos.Include(x => x.Horarios).FirstOrDefaultAsync(x => x.DocenteId == d && x.Carrera == s.Carrera && x.Jornada == s.Jornada && x.Materia == (s.Materia ?? ""));
                if (existingGroup is not null)
                {
                    existingGroup.Deleted = false;
                    existingGroup.UpdatedAt = effectiveTime;
                    if (existingGroup.Horarios.Count > 0) db.Horarios.RemoveRange(existingGroup.Horarios);
                    foreach (var h in s.Horarios)
                    {
                        db.Horarios.Add(new Horario { Id = Guid.NewGuid(), GrupoId = existingGroup.Id, Dia = h.Dia, Inicio = h.Inicio, Fin = h.Fin });
                    }
                    mine[gid] = existingGroup;
                }
                else if (!await db.Grupos.AnyAsync(x => x.Id == gid))
                {
                    var n = new Grupo { Id = gid, DocenteId = d, Carrera = s.Carrera, Jornada = s.Jornada, Materia = s.Materia ?? "", Deleted = false, UpdatedAt = effectiveTime };
                    db.Grupos.Add(n); mine[n.Id] = n; mine[gid] = n;
                    foreach (var h in s.Horarios)
                    {
                        db.Horarios.Add(new Horario { Id = Guid.NewGuid(), GrupoId = gid, Dia = h.Dia, Inicio = h.Inicio, Fin = h.Fin });
                    }
                }
            }
        }
        await SafeSaveChanges();

        var gids = mine.Values.Select(g => g.Id).Concat(mine.Keys).Distinct().ToList();
        var est = await db.Estudiantes.Where(e => gids.Contains(e.GrupoId)).ToListAsync();
        foreach (var s in p.Estudiantes)
        {
            var eid = s.Id ?? Guid.NewGuid();
            var t = Utc(s.UpdatedAt);
            var effectiveTime = t > now ? t : now;
            var targetGrupoId = mine.TryGetValue(s.GrupoId, out var mg) ? mg.Id : s.GrupoId;
            var sCod = s.Codigo?.Trim() ?? "";

            if (s.Deleted)
            {
                var toDelete = est.Where(e =>
                    (s.Id.HasValue && e.Id == s.Id.Value) ||
                    (!string.IsNullOrEmpty(sCod) && (e.GrupoId == targetGrupoId || mine.ContainsKey(e.GrupoId)) &&
                     string.Equals(e.Codigo?.Trim(), sCod, StringComparison.OrdinalIgnoreCase))
                ).ToList();

                if (toDelete.Count > 0)
                {
                    db.Estudiantes.RemoveRange(toDelete);
                    foreach (var td in toDelete) est.Remove(td);
                }
                else if (s.Id.HasValue || !string.IsNullOrEmpty(sCod))
                {
                    var inDbList = await db.Estudiantes
                        .Include(e => e.Grupo)
                        .Where(e => e.Grupo != null && e.Grupo.DocenteId == d &&
                            ((s.Id.HasValue && e.Id == s.Id.Value) || (!string.IsNullOrEmpty(sCod) && e.Codigo == sCod)))
                        .ToListAsync();
                    if (inDbList.Count > 0)
                    {
                        db.Estudiantes.RemoveRange(inDbList);
                    }
                }
                continue;
            }

            var existingEst = est.FirstOrDefault(e =>
                (s.Id.HasValue && e.Id == s.Id.Value) ||
                (e.GrupoId == targetGrupoId && string.Equals(e.Codigo?.Trim(), sCod, StringComparison.OrdinalIgnoreCase)));

            if (existingEst is not null)
            {
                if (existingEst.Deleted && !s.Deleted && t <= existingEst.UpdatedAt)
                {
                    continue; // Rechazar resurrección con caché vieja de otro dispositivo
                }
                existingEst.Codigo = sCod;
                existingEst.NombreCompleto = s.NombreCompleto?.Trim() ?? "";
                existingEst.Deleted = false;
                existingEst.UpdatedAt = effectiveTime;
            }
            else if (mine.ContainsKey(s.GrupoId) || mine.Values.Any(g => g.Id == targetGrupoId))
            {
                var inDb = s.Id.HasValue ? await db.Estudiantes.FirstOrDefaultAsync(x => x.Id == s.Id.Value) : null;
                if (inDb is not null)
                {
                    if (inDb.Deleted && !s.Deleted && t <= inDb.UpdatedAt)
                    {
                        continue; // Rechazar resurrección con caché vieja de otro dispositivo
                    }
                    inDb.GrupoId = targetGrupoId;
                    inDb.Codigo = sCod;
                    inDb.NombreCompleto = s.NombreCompleto?.Trim() ?? "";
                    inDb.Deleted = false;
                    inDb.UpdatedAt = effectiveTime;
                    est.Add(inDb);
                }
                else
                {
                    var deletedInDb = await db.Estudiantes.Include(x => x.Grupo).FirstOrDefaultAsync(x =>
                        x.Grupo != null && x.Grupo.DocenteId == d && x.Deleted && x.Codigo == sCod);
                    if (deletedInDb is not null && t <= deletedInDb.UpdatedAt)
                    {
                        continue; // Rechazar resurrección con caché vieja de otro dispositivo
                    }

                    var n = new Estudiante
                    {
                        Id = eid,
                        GrupoId = targetGrupoId,
                        Codigo = sCod,
                        NombreCompleto = s.NombreCompleto?.Trim() ?? "",
                        Deleted = false,
                        UpdatedAt = effectiveTime
                    };
                    db.Estudiantes.Add(n);
                    est.Add(n);
                }
            }
        }
        await SafeSaveChanges();

        var eids = est.Select(e => e.Id).ToList();
        var reg = (await db.Registros.Where(r => eids.Contains(r.EstudianteId)).ToListAsync())
            .GroupBy(r => (r.EstudianteId, r.Fecha))
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var s in p.Registros)
        {
            var targetEst = est.FirstOrDefault(e => e.Id == s.EstudianteId);
            if (targetEst is null) continue;

            DateOnly fecha;
            if (!DateOnly.TryParse(s.Fecha, out fecha))
            {
                if (DateTime.TryParse(s.Fecha, out var dt)) fecha = DateOnly.FromDateTime(dt);
                else continue;
            }

            var rid = s.Id ?? Guid.NewGuid();
            var t = Utc(s.UpdatedAt);
            var effectiveTime = t > now ? t : now;
            if (s.Deleted)
            {
                if (reg.TryGetValue((targetEst.Id, fecha), out var rDel))
                {
                    db.Registros.Remove(rDel);
                    reg.Remove((targetEst.Id, fecha));
                }
                else
                {
                    var inDbDel = await db.Registros.FirstOrDefaultAsync(x => x.Id == rid || (x.EstudianteId == targetEst.Id && x.Fecha == fecha));
                    if (inDbDel is not null) db.Registros.Remove(inDbDel);
                }
                continue;
            }

            if (reg.TryGetValue((targetEst.Id, fecha), out var r2))
            {
                r2.Presente = s.Presente;
                r2.Deleted = false;
                r2.UpdatedAt = effectiveTime;
            }
            else
            {
                var inDb = await db.Registros.FirstOrDefaultAsync(x => x.Id == rid);
                if (inDb is not null)
                {
                    inDb.EstudianteId = targetEst.Id;
                    inDb.Fecha = fecha;
                    inDb.Presente = s.Presente;
                    inDb.Deleted = false;
                    inDb.UpdatedAt = effectiveTime;
                    reg[(targetEst.Id, fecha)] = inDb;
                }
                else
                {
                    var n = new Registro
                    {
                        Id = rid,
                        EstudianteId = targetEst.Id,
                        Fecha = fecha,
                        Presente = s.Presente,
                        Deleted = false,
                        UpdatedAt = effectiveTime
                    };
                    db.Registros.Add(n);
                    reg[(targetEst.Id, fecha)] = n;
                }
            }
        }
        await SafeSaveChanges();
    }

    public async Task<Docente?> GetDocenteByEmail(string email)
    {
        var em = email.Trim().ToLowerInvariant();
        return await db.Docentes.FirstOrDefaultAsync(x => x.Email == em);
    }

    public async Task<Docente> CreateDocente(string email, string passwordHash)
    {
        var u = new Docente { Email = email.Trim().ToLowerInvariant(), PasswordHash = passwordHash, CreatedAt = DateTime.UtcNow };
        db.Docentes.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    public async Task<bool> UpdatePassword(string email, string passwordHash)
    {
        var em = email.Trim().ToLowerInvariant();
        var u = await db.Docentes.FirstOrDefaultAsync(x => x.Email == em);
        if (u is null) return false;
        u.PasswordHash = passwordHash;
        await db.SaveChangesAsync();
        return true;
    }
}
