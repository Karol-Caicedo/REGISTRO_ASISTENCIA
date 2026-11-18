# Asistencia.Api (ASP.NET Core + EF Core + PostgreSQL)

## Arrancar
    docker compose up -d          # PostgreSQL
    dotnet run                    # crea las tablas al iniciar (EnsureCreated)
Abre http://localhost:5000: la web (wwwroot/index.html) se sirve desde el mismo backend.

## Configuración segura
- Clave JWT (obligatoria fuera de Development): `dotnet user-secrets set "Jwt:Key" "<32+ caracteres>"` o variable `Jwt__Key`.
- Migraciones EF (recomendado):
      dotnet tool install -g dotnet-ef
      dotnet ef migrations add Inicial
      # y activar: Db__UsarMigraciones=true
- Login/registro limitados a 10 intentos por minuto por IP; HTTPS/HSTS automáticos fuera de Development (`Https:Forzar`).
- Si usas proxy inverso, configura ForwardedHeaders para que el límite use la IP real.

## Endpoints (JWT en `Authorization: Bearer ...`)
- POST /api/auth/register | /api/auth/login  → { token, email }
- GET/POST /api/grupos · PUT/DELETE /api/grupos/{id}   (carrera, jornada, horarios [{dia 0-6, inicio, fin}])
- GET/POST /api/grupos/{id}/estudiantes · DELETE .../estudiantes/{eid}   (codigo, nombreCompleto)
- PUT /api/grupos/{id}/asistencia  { fecha, marcas:[{estudianteId, presente}] }
- GET /api/grupos/{id}/historico?mes=2026-11&fecha=&estado=asistieron|faltaron
- GET /api/grupos/{id}/resumen?q=   (dashboard: totales y asistencias/faltas por estudiante)
- GET /api/sync?since=ISO · POST /api/sync   (sincronización cliente ↔ servidor)

## Sincronización web
El cliente guarda todo localmente con Id (Guid), `updatedAt` (UTC) y `deleted`.
1. Push: POST /api/sync con lo modificado desde la última sincronización.
2. Pull: GET /api/sync?since={serverTime anterior}; guarda el `serverTime` devuelto.
Conflictos: gana el cambio con `updatedAt` más reciente. Borrados = `deleted: true` (no se eliminan filas).

## Pendiente
Refresh tokens (hoy el token dura 12 h y la web pide iniciar sesión de nuevo), pruebas automáticas, política de contraseñas olvidadas.
# REGISTRO_ASISTENCIA
