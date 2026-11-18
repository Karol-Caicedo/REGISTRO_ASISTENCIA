namespace Asistencia.Api.Domain;

public abstract class Syncable
{
    public Guid Id { get; set; } = Guid.NewGuid();          // el cliente (web/app) puede generar el Id
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool Deleted { get; set; }                       // borrado lógico para poder sincronizar
}

public class Docente
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Grupo : Syncable
{
    public Guid DocenteId { get; set; }
    public string Carrera { get; set; } = "";
    public string Jornada { get; set; } = "";
    public List<Horario> Horarios { get; set; } = new();
    public List<Estudiante> Estudiantes { get; set; } = new();
}

public class Horario
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GrupoId { get; set; }
    public int Dia { get; set; }                            // 0 = domingo ... 6 = sábado
    public string Inicio { get; set; } = "18:00";
    public string Fin { get; set; } = "20:00";
}

public class Estudiante : Syncable
{
    public Guid GrupoId { get; set; }
    public Grupo? Grupo { get; set; }
    public string Codigo { get; set; } = "";                // Identificador
    public string NombreCompleto { get; set; } = "";
}

public class Registro : Syncable
{
    public Guid EstudianteId { get; set; }
    public Estudiante? Estudiante { get; set; }
    public DateOnly Fecha { get; set; }
    public bool Presente { get; set; }                      // false = falta
}
