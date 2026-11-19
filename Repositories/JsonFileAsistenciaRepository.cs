using System.Text.Json;
using System.Text.RegularExpressions;
using Asistencia.Api.Domain;

namespace Asistencia.Api.Repositories;

public class JsonFileAsistenciaRepository : IAsistenciaRepository
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public class DatabaseModel
    {
        public List<Docente> Docentes { get; set; } = new();
        public List<Grupo> Grupos { get; set; } = new();
        public List<Estudiante> Estudiantes { get; set; } = new();
        public List<RegistroModel> Registros { get; set; } = new();
    }

    public class RegistroModel
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid EstudianteId { get; set; }
        public string Fecha { get; set; } = "";
        public bool Presente { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public bool Deleted { get; set; }
    }

    private DatabaseModel _data;

    public JsonFileAsistenciaRepository(IWebHostEnvironment env)
    {
        var dataDir = Path.Combine(env.ContentRootPath, "data");
        if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "asistencia_db.json");
        _data = Load();
    }

    private DatabaseModel Load()
    {
        lock (_lock)
        {
            if (File.Exists(_filePath))
            {
                try
                {
                    var json = File.ReadAllText(_filePath);
                    return JsonSerializer.Deserialize<DatabaseModel>(json) ?? new DatabaseModel();
                }
                catch { }
            }
            return new DatabaseModel();
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch { }
        }
    }

    private static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);
    private static Horario H(HorarioDto h) => new() { Dia = h.Dia, Inicio = h.Inicio, Fin = h.Fin };
    private static HorarioDto HD(Horario h) => new(h.Dia, h.Inicio, h.Fin);

    public Task<Docente?> GetDocenteByEmail(string email)
    {
        lock (_lock)
        {
            var em = email.Trim().ToLowerInvariant();
            var d = _data.Docentes.FirstOrDefault(x => x.Email.ToLowerInvariant() == em);
            return Task.FromResult(d);
        }
    }

    public Task<Docente> CreateDocente(string email, string passwordHash)
    {
        lock (_lock)
        {
            var u = new Docente { Email = email.Trim().ToLowerInvariant(), PasswordHash = passwordHash, CreatedAt = DateTime.UtcNow };
            _data.Docentes.Add(u);
            Save();
            return Task.FromResult(u);
        }
    }

    public Task<bool> UpdatePassword(string email, string passwordHash)
    {
        lock (_lock)
        {
            var em = email.Trim().ToLowerInvariant();
            var u = _data.Docentes.FirstOrDefault(x => x.Email.ToLowerInvariant() == em);
            if (u is null) return Task.FromResult(false);
            u.PasswordHash = passwordHash;
            Save();
            return Task.FromResult(true);
        }
    }

    public Task<List<Grupo>> Grupos(Guid d)
    {
        lock (_lock)
        {
            var list = _data.Grupos
                .Where(g => g.DocenteId == d && !g.Deleted)
                .OrderBy(g => g.Carrera).ThenBy(g => g.Jornada)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<Grupo?> Grupo(Guid d, Guid id)
    {
        lock (_lock)
        {
            var g = _data.Grupos.FirstOrDefault(g => g.Id == id && g.DocenteId == d && !g.Deleted);
            if (g is not null)
            {
                g.Estudiantes = _data.Estudiantes.Where(e => e.GrupoId == g.Id && !e.Deleted).ToList();
            }
            return Task.FromResult(g);
        }
    }

    public Task<Grupo> CrearGrupo(Guid d, GrupoDto x)
    {
        lock (_lock)
        {
            var g = new Grupo
            {
                Id = x.Id ?? Guid.NewGuid(),
                DocenteId = d,
                Carrera = x.Carrera.Trim(),
                Materia = (x.Materia ?? "").Trim(),
                Jornada = x.Jornada.Trim(),
                UpdatedAt = DateTime.UtcNow,
                Horarios = x.Horarios.Select(H).ToList()
            };
            _data.Grupos.Add(g);
            Save();
            return Task.FromResult(g);
        }
    }

    public Task<bool> ActualizarGrupo(Guid d, Guid id, GrupoDto x)
    {
        lock (_lock)
        {
            var g = _data.Grupos.FirstOrDefault(g => g.Id == id && g.DocenteId == d && !g.Deleted);
            if (g is null) return Task.FromResult(false);
            g.Carrera = x.Carrera.Trim();
            g.Materia = (x.Materia ?? "").Trim();
            g.Jornada = x.Jornada.Trim();
            g.UpdatedAt = DateTime.UtcNow;
            g.Horarios = x.Horarios.Select(H).ToList();
            Save();
            return Task.FromResult(true);
        }
    }

    public Task<bool> EliminarGrupo(Guid d, Guid id)
    {
        lock (_lock)
        {
            var g = _data.Grupos.FirstOrDefault(g => g.Id == id && g.DocenteId == d && !g.Deleted);
            if (g is null) return Task.FromResult(false);
            var now = DateTime.UtcNow;
            g.Deleted = true;
            g.UpdatedAt = now;
            foreach (var e in _data.Estudiantes.Where(x => x.GrupoId == g.Id))
            {
                e.Deleted = true;
                e.UpdatedAt = now;
            }
            Save();
            return Task.FromResult(true);
        }
    }

    public Task<int> AgregarEstudiantes(Guid d, Guid gid, IEnumerable<EstudianteDto> items)
    {
        lock (_lock)
        {
            var g = _data.Grupos.FirstOrDefault(g => g.Id == gid && g.DocenteId == d && !g.Deleted);
            if (g is null) return Task.FromResult(-1);

            var existingCodes = _data.Estudiantes.Where(e => e.GrupoId == gid && !e.Deleted).Select(e => e.Codigo).ToHashSet();
            int added = 0;
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Codigo) || string.IsNullOrWhiteSpace(item.NombreCompleto)) continue;
                var code = item.Codigo.Trim();
                if (existingCodes.Add(code))
                {
                    _data.Estudiantes.Add(new Estudiante
                    {
                        Id = Guid.NewGuid(),
                        GrupoId = gid,
                        Codigo = code,
                        NombreCompleto = item.NombreCompleto.Trim(),
                        UpdatedAt = DateTime.UtcNow,
                        Deleted = false
                    });
                    added++;
                }
            }
            if (added > 0) Save();
            return Task.FromResult(added);
        }
    }

    public Task<bool> EliminarEstudiante(Guid d, Guid gid, Guid eid)
    {
        lock (_lock)
        {
            var e = _data.Estudiantes.FirstOrDefault(x => x.Id == eid && x.GrupoId == gid && !x.Deleted);
            if (e is null) return Task.FromResult(false);
            e.Deleted = true;
            e.UpdatedAt = DateTime.UtcNow;
            Save();
            return Task.FromResult(true);
        }
    }

    public Task<bool> GuardarAsistencia(Guid d, Guid gid, AsistenciaReq r)
    {
        lock (_lock)
        {
            var fechaStr = r.Fecha.ToString("yyyy-MM-dd");
            foreach (var m in r.Marcas)
            {
                var reg = _data.Registros.FirstOrDefault(x => x.EstudianteId == m.EstudianteId && x.Fecha == fechaStr);
                if (reg is not null)
                {
                    reg.Presente = m.Presente;
                    reg.Deleted = false;
                    reg.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    _data.Registros.Add(new RegistroModel
                    {
                        Id = Guid.NewGuid(),
                        EstudianteId = m.EstudianteId,
                        Fecha = fechaStr,
                        Presente = m.Presente,
                        UpdatedAt = DateTime.UtcNow,
                        Deleted = false
                    });
                }
            }
            Save();
            return Task.FromResult(true);
        }
    }

    public Task<object?> Historico(Guid d, Guid gid, DateOnly mes, DateOnly? fecha, string? estado)
    {
        lock (_lock)
        {
            var g = _data.Grupos.FirstOrDefault(x => x.Id == gid && x.DocenteId == d && !x.Deleted);
            if (g is null) return Task.FromResult<object?>(null);

            var ests = _data.Estudiantes.Where(e => e.GrupoId == gid && !e.Deleted).OrderBy(e => e.NombreCompleto).ToList();
            var eids = ests.Select(e => e.Id).ToHashSet();
            var prefix = mes.ToString("yyyy-MM");
            var regs = _data.Registros.Where(r => eids.Contains(r.EstudianteId) && !r.Deleted && r.Fecha.StartsWith(prefix)).ToList();

            var dias = regs.Select(r => r.Fecha).Distinct().OrderBy(x => x).ToList();
            var regMap = regs.ToDictionary(r => (r.EstudianteId, r.Fecha), r => r.Presente);

            var filas = ests.Select(e => new
            {
                estudianteId = e.Id,
                codigo = e.Codigo,
                nombreCompleto = e.NombreCompleto,
                asistencias = dias.Select(d => regMap.TryGetValue((e.Id, d), out var p) ? (p ? "P" : "A") : "-").ToList()
            }).ToList();

            return Task.FromResult<object?>(new { grupoId = gid, mes = prefix, dias, filas });
        }
    }

    public Task<object?> Resumen(Guid d, Guid gid, string? q)
    {
        lock (_lock)
        {
            var g = _data.Grupos.FirstOrDefault(x => x.Id == gid && x.DocenteId == d && !x.Deleted);
            if (g is null) return Task.FromResult<object?>(null);

            var ests = _data.Estudiantes.Where(e => e.GrupoId == gid && !e.Deleted).ToList();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLowerInvariant();
                ests = ests.Where(e => e.Codigo.ToLowerInvariant().Contains(term) || e.NombreCompleto.ToLowerInvariant().Contains(term)).ToList();
            }

            var eids = ests.Select(e => e.Id).ToHashSet();
            var regs = _data.Registros.Where(r => eids.Contains(r.EstudianteId) && !r.Deleted).ToList();

            int totalP = regs.Count(r => r.Presente);
            int totalA = regs.Count(r => !r.Presente);
            double pct = regs.Count == 0 ? 0 : Math.Round(100.0 * totalP / regs.Count);

            return Task.FromResult<object?>(new
            {
                grupoId = gid,
                totalClases = regs.Select(r => r.Fecha).Distinct().Count(),
                totalEstudiantes = ests.Count,
                porcentajeAsistencia = pct
            });
        }
    }

    public Task<SyncPayload> Pull(Guid d, DateTime since)
    {
        lock (_lock)
        {
            since = Utc(since);
            var isAll = since.Year < 2000;
            var tombstoneCutoff = DateTime.UtcNow.AddDays(-30);
            var gs = _data.Grupos.Where(g => g.DocenteId == d && (isAll ? (!g.Deleted || g.UpdatedAt > tombstoneCutoff) : g.UpdatedAt > since)).ToList();
            var gids = _data.Grupos.Where(g => g.DocenteId == d).Select(g => g.Id).ToHashSet();
            var es = _data.Estudiantes.Where(e => gids.Contains(e.GrupoId) && (isAll ? (!e.Deleted || e.UpdatedAt > tombstoneCutoff) : e.UpdatedAt > since)).ToList();
            var eids = _data.Estudiantes.Where(e => gids.Contains(e.GrupoId)).Select(e => e.Id).ToHashSet();
            var rs = _data.Registros.Where(r => eids.Contains(r.EstudianteId) && (isAll ? (!r.Deleted || r.UpdatedAt > tombstoneCutoff) : r.UpdatedAt > since)).ToList();

            var payload = new SyncPayload(
                gs.Select(g => new SyncGrupo(g.Id, g.Carrera, g.Jornada, g.Horarios.Select(HD).ToList(), g.UpdatedAt, g.Deleted, g.Materia)).ToList(),
                es.Select(e => new SyncEstudiante(e.Id, e.GrupoId, e.Codigo, e.NombreCompleto, e.UpdatedAt, e.Deleted)).ToList(),
                rs.Select(r => new SyncRegistro(r.Id, r.EstudianteId, r.Fecha, r.Presente, r.UpdatedAt, r.Deleted)).ToList(),
                DateTime.UtcNow
            );
            return Task.FromResult(payload);
        }
    }

    public Task Push(Guid d, SyncPayload p)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var groupMap = new Dictionary<Guid, Guid>();
            foreach (var sg in p.Grupos)
            {
                var gid = sg.Id ?? Guid.NewGuid();
                var t = Utc(sg.UpdatedAt);
                var effectiveTime = t > now ? t : now;
                var existing = _data.Grupos.FirstOrDefault(g => g.Id == gid || (g.DocenteId == d && g.Carrera == sg.Carrera && g.Jornada == sg.Jornada && g.Materia == (sg.Materia ?? "")));
                if (existing is not null)
                {
                    if (existing.Deleted && !sg.Deleted && t <= existing.UpdatedAt)
                    {
                        groupMap[gid] = existing.Id;
                        groupMap[existing.Id] = existing.Id;
                        continue;
                    }
                    existing.Carrera = sg.Carrera;
                    existing.Jornada = sg.Jornada;
                    existing.Materia = sg.Materia ?? "";
                    existing.Deleted = sg.Deleted;
                    existing.UpdatedAt = effectiveTime;
                    existing.Horarios = sg.Horarios.Select(H).ToList();
                    groupMap[gid] = existing.Id;
                    groupMap[existing.Id] = existing.Id;
                }
                else
                {
                    var n = new Grupo
                    {
                        Id = gid,
                        DocenteId = d,
                        Carrera = sg.Carrera,
                        Jornada = sg.Jornada,
                        Materia = sg.Materia ?? "",
                        Deleted = sg.Deleted,
                        UpdatedAt = effectiveTime,
                        Horarios = sg.Horarios.Select(H).ToList()
                    };
                    _data.Grupos.Add(n);
                    groupMap[gid] = n.Id;
                    groupMap[n.Id] = n.Id;
                }
            }

            foreach (var se in p.Estudiantes)
            {
                var eid = se.Id ?? Guid.NewGuid();
                var t = Utc(se.UpdatedAt);
                var effectiveTime = t > now ? t : now;
                var targetGrupoId = groupMap.TryGetValue(se.GrupoId, out var mappedGid) ? mappedGid : se.GrupoId;
                var sCod = se.Codigo?.Trim() ?? "";

                if (se.Deleted)
                {
                    var toDelete = _data.Estudiantes.Where(e =>
                        (se.Id.HasValue && e.Id == se.Id.Value) ||
                        (!string.IsNullOrEmpty(sCod) && (e.GrupoId == targetGrupoId || groupMap.ContainsKey(e.GrupoId)) &&
                         string.Equals(e.Codigo?.Trim(), sCod, StringComparison.OrdinalIgnoreCase))
                    ).ToList();
                    foreach (var td in toDelete)
                    {
                        td.Deleted = true;
                        td.UpdatedAt = effectiveTime;
                    }
                    continue;
                }

                var existing = _data.Estudiantes.FirstOrDefault(e =>
                    (se.Id.HasValue && e.Id == se.Id.Value) ||
                    (e.GrupoId == targetGrupoId && string.Equals(e.Codigo?.Trim(), sCod, StringComparison.OrdinalIgnoreCase)));
                if (existing is not null)
                {
                    if (existing.Deleted && !se.Deleted && t <= existing.UpdatedAt)
                    {
                        continue;
                    }
                    existing.GrupoId = targetGrupoId;
                    existing.Codigo = sCod;
                    existing.NombreCompleto = se.NombreCompleto?.Trim() ?? "";
                    existing.Deleted = false;
                    existing.UpdatedAt = effectiveTime;
                }
                else
                {
                    var deletedExisting = _data.Estudiantes.FirstOrDefault(e =>
                        (e.GrupoId == targetGrupoId || groupMap.ContainsKey(e.GrupoId)) &&
                        e.Deleted && string.Equals(e.Codigo?.Trim(), sCod, StringComparison.OrdinalIgnoreCase));
                    if (deletedExisting is not null && t <= deletedExisting.UpdatedAt)
                    {
                        continue;
                    }

                    _data.Estudiantes.Add(new Estudiante
                    {
                        Id = eid,
                        GrupoId = targetGrupoId,
                        Codigo = sCod,
                        NombreCompleto = se.NombreCompleto?.Trim() ?? "",
                        Deleted = false,
                        UpdatedAt = effectiveTime
                    });
                }
            }

            foreach (var sr in p.Registros)
            {
                var rid = sr.Id ?? Guid.NewGuid();
                var t = Utc(sr.UpdatedAt);
                var effectiveTime = t > now ? t : now;

                string fechaStr;
                if (DateOnly.TryParse(sr.Fecha, out var parsedDate))
                {
                    fechaStr = parsedDate.ToString("yyyy-MM-dd");
                }
                else if (DateTime.TryParse(sr.Fecha, out var parsedDt))
                {
                    fechaStr = parsedDt.ToString("yyyy-MM-dd");
                }
                else
                {
                    fechaStr = sr.Fecha.Trim();
                }

                var existing = _data.Registros.FirstOrDefault(r => r.EstudianteId == sr.EstudianteId && r.Fecha == fechaStr);
                if (existing is not null)
                {
                    if (t <= existing.UpdatedAt) continue;
                    existing.Presente = sr.Presente;
                    existing.Deleted = sr.Deleted;
                    existing.UpdatedAt = effectiveTime;
                }
                else
                {
                    _data.Registros.Add(new RegistroModel
                    {
                        Id = rid,
                        EstudianteId = sr.EstudianteId,
                        Fecha = fechaStr,
                        Presente = sr.Presente,
                        Deleted = sr.Deleted,
                        UpdatedAt = effectiveTime
                    });
                }
            }

            Save();
            return Task.CompletedTask;
        }
    }
}
