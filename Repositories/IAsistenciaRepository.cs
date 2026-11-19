using Asistencia.Api.Domain;

namespace Asistencia.Api.Repositories;

public interface IAsistenciaRepository
{
    Task<List<Grupo>> Grupos(Guid d);
    Task<Grupo?> Grupo(Guid d, Guid id);
    Task<Grupo> CrearGrupo(Guid d, GrupoDto x);
    Task<bool> ActualizarGrupo(Guid d, Guid id, GrupoDto x);
    Task<bool> EliminarGrupo(Guid d, Guid id);
    Task<int> AgregarEstudiantes(Guid d, Guid gid, IEnumerable<EstudianteDto> items);
    Task<bool> EliminarEstudiante(Guid d, Guid gid, Guid eid);
    Task<bool> GuardarAsistencia(Guid d, Guid gid, AsistenciaReq r);
    Task<object?> Historico(Guid d, Guid gid, DateOnly mes, DateOnly? fecha, string? estado);
    Task<object?> Resumen(Guid d, Guid gid, string? q);
    Task<SyncPayload> Pull(Guid d, DateTime since);
    Task Push(Guid d, SyncPayload p);
    Task<Docente?> GetDocenteByEmail(string email);
    Task<Docente> CreateDocente(string email, string passwordHash);
    Task<bool> UpdatePassword(string email, string passwordHash);
    Task<bool> EliminarCuenta(Guid d);
}
