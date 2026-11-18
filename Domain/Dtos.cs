namespace Asistencia.Api.Domain;

public record AuthReq(string Email, string Password);
public record AuthRes(string Token, string Email);
public record HorarioDto(int Dia, string Inicio, string Fin);
public record GrupoDto(Guid? Id, string Carrera, string Jornada, List<HorarioDto> Horarios);
public record EstudianteDto(string Codigo, string NombreCompleto);
public record MarcaDto(Guid EstudianteId, bool Presente);
public record AsistenciaReq(DateOnly Fecha, List<MarcaDto> Marcas);

// Sincronización (web <-> app local)
public record SyncGrupo(Guid Id, string Carrera, string Jornada, List<HorarioDto> Horarios, DateTime UpdatedAt, bool Deleted);
public record SyncEstudiante(Guid Id, Guid GrupoId, string Codigo, string NombreCompleto, DateTime UpdatedAt, bool Deleted);
public record SyncRegistro(Guid Id, Guid EstudianteId, DateOnly Fecha, bool Presente, DateTime UpdatedAt, bool Deleted);
public record SyncPayload(List<SyncGrupo> Grupos, List<SyncEstudiante> Estudiantes, List<SyncRegistro> Registros, DateTime? ServerTime = null);
