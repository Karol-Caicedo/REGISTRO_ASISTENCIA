using Asistencia.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Asistencia.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> o) : DbContext(o)
{
    public DbSet<Docente> Docentes => Set<Docente>();
    public DbSet<Grupo> Grupos => Set<Grupo>();
    public DbSet<Horario> Horarios => Set<Horario>();
    public DbSet<Estudiante> Estudiantes => Set<Estudiante>();
    public DbSet<Registro> Registros => Set<Registro>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Docente>().HasIndex(x => x.Email).IsUnique();
        b.Entity<Docente>().HasMany<Grupo>().WithOne().HasForeignKey(g => g.DocenteId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Grupo>().HasIndex(x => x.DocenteId);
        b.Entity<Grupo>().HasMany(g => g.Horarios).WithOne().HasForeignKey(h => h.GrupoId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Grupo>().HasMany(g => g.Estudiantes).WithOne(e => e.Grupo).HasForeignKey(e => e.GrupoId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Estudiante>().HasIndex(e => new { e.GrupoId, e.Codigo }).IsUnique().HasFilter("\"Deleted\" = false");
        b.Entity<Registro>().HasOne(r => r.Estudiante).WithMany().HasForeignKey(r => r.EstudianteId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Registro>().HasIndex(r => new { r.EstudianteId, r.Fecha }).IsUnique();
        b.Entity<Registro>().HasIndex(r => r.UpdatedAt);
    }
}
