# BankOs · Panel de Administración de Banco

> ⚠️ **Cambio de arquitectura (importante):** esta aplicación **ya NO consume la API de Laravel**.
> Ahora se conecta **directamente a la base de datos PostgreSQL** de BankOS (en el VPS) y ejecuta
> toda la lógica contra la base de datos, replicando el comportamiento del backend.
> **Configura la conexión y verifícala siguiendo [`SETUP_BASE_DE_DATOS.md`](SETUP_BASE_DE_DATOS.md).**
> La sección «Arquitectura → Solo API» de abajo quedó obsoleta y se conserva solo como referencia.


Aplicación web en **C# / .NET 10 (ASP.NET Core MVC)** para los **administradores de un banco** dentro de la plataforma multi-tenant **BankOs**. Consume exclusivamente la API REST de BankOs (Laravel, proyecto `192-bankroot`) correspondiente al rol `administrador`.

Es la aplicación complementaria al panel **SuperAdmin** (gestión de bancos/tenants). Mientras el SuperAdmin administra *los bancos*, esta app administra *el interior de un banco*: sus clientes, cuentas, transacciones, soporte y configuración.

---

## ✨ Funcionalidades

| Módulo | Descripción |
|---|---|
| **Landing pública** | Página de presentación de la administración del banco, con acceso a login y a solicitud de certificados. |
| **Login de administrador** | Selección de banco + credenciales. Autenticación JWT contra la API; sólo entran usuarios con rol `administrador`. |
| **Resumen (dashboard)** | KPIs del banco: usuarios, cuentas activas, transacciones y saldo total; movimientos y cuentas recientes. |
| **Clientes / usuarios** | Crear, editar, activar/desactivar usuarios (clientes o administradores). **Cada cambio notifica al cliente por correo.** |
| **Cuentas** | Crear, editar (moneda, saldo, estado) y activar/desactivar cuentas. **Operaciones sensibles confirmadas en modal** y con aviso especial cuando la cuenta tiene saldo. Notificación al titular en cada acción. |
| **Transacciones** | Listado con filtros (depósito / retiro / transferencia), detalle, y ejecución de **depósitos** y **transferencias** con confirmación en modal. |
| **PQRS** | Listado de peticiones, quejas, reclamos y sugerencias; responder (el cliente recibe la respuesta por correo) y marcar en revisión. |
| **Auditoría** | Registro de las acciones administrativas del banco. |
| **Asistente con IA** | Chatbot que analiza clientes, cuentas y transacciones del banco (OpenAI). Incluye sugerencias de preguntas. |
| **Certificados PDF** | Generación del certificado bancario **desde el MVC** (no satura la API). El administrador puede emitirlo para cualquier cliente/cuenta. |
| **Solicitar certificado (autoservicio)** | Un cliente verifica su identidad con sus propias credenciales y descarga su certificado, que **también le llega por correo**. |
| **Política de privacidad** | Página dedicada en español. |

### Notificaciones por correo (enviadas desde el MVC)

La API **no** envía correos al crear/editar usuarios o cuentas, por lo que esta aplicación los envía para que **el cliente siempre sepa qué se hace con sus datos y su dinero**:

- Usuario: creado (con credenciales), actualizado (con detalle de cambios), desactivado, reactivado.
- Cuenta: creada, modificada (con detalle de cambios y aviso si hay saldo), desactivada (aviso de saldo), reactivada.

> Las respuestas a PQRS y el OTP de retiro los envía la propia API; no se duplican aquí.

---

## 🧱 Arquitectura

- **Solo API**: la aplicación no accede a ninguna base de datos. Toda la información proviene de la API de BankOs.
- **Autenticación**: `POST /auth/login` devuelve un JWT que se guarda en sesión. Cada llamada incluye `Authorization: Bearer <token>`, el encabezado `X-Tenant-ID` del banco y un `X-Correlation-ID`. Los depósitos y transferencias añaden `Idempotency-Key`.
- **Correos y PDF en el MVC**: tanto las notificaciones como los certificados se generan en esta aplicación (con `SmtpClient` y `QuestPDF`), evitando carga adicional sobre la API.
- **Autoservicio seguro**: la solicitud de certificado por parte de un cliente exige sus credenciales reales y envía el documento únicamente al correo registrado del titular.

### Endpoints consumidos

```
POST   /api/v1/auth/login
PATCH  /api/v1/auth/me/password
GET    /api/v1/banks                      (público)
GET    /api/v1/users  · POST · GET/{id} · PUT/{id} · PATCH/{id}/status
GET    /api/v1/accounts · POST · GET/{id} · PUT/{id} · PATCH/{id}/status
GET    /api/v1/transactions · GET/{id}
POST   /api/v1/transactions/deposit       (Idempotency-Key)
POST   /api/v1/transactions/transfer      (Idempotency-Key)
GET    /api/v1/pqrs · PATCH/{id}/respond · PATCH/{id}/status
GET    /api/v1/audit/logs
GET    /api/v1/config · PATCH /api/v1/tenants/{slug}/config
```

---

## ⚙️ Requisitos

- **.NET 10 SDK**
- Acceso a la API de BankOs en ejecución.
- (Opcional) Cuenta SMTP para correos y clave de OpenAI para el asistente.

---

## 🚀 Puesta en marcha

```bash
cd BankAdmin
dotnet restore
dotnet run
```

La aplicación queda disponible en `http://localhost:5001` (puerto distinto al del SuperAdmin para poder ejecutar ambos a la vez).

### Configuración (`appsettings.json`)

```jsonc
{
  "BankOS": {
    "ApiBaseUrl": "http://bank-os.duckdns.org:8080"   // URL base de la API
  },
  "Email": {
    "Enabled": true,                  // pon false para desactivar el envío real
    "Host": "smtp.gmail.com",
    "Port": "587",
    "Username": "tu-correo@gmail.com",
    "Password": "tu-app-password-de-gmail",
    "From": "tu-correo@gmail.com",
    "FromName": "BankOs"
  },
  "OpenAI": {
    "ApiKey": "sk-...",               // necesaria para el asistente
    "Model": "gpt-4o-mini"
  },
  "Branding": {
    "SupportEmail": "soporte@bankos.com",
    "PortalUrl": "http://bank-os.duckdns.org:8080"
  }
}
```

> En desarrollo, `appsettings.Development.json` apunta la API a `http://localhost:8080`.

### Notas

- **Correos**: con Gmail necesitas una *App Password* (no la contraseña normal) y verificación en dos pasos activa. Si `Email:Enabled` es `false`, las notificaciones se registran en el log pero no se envían (útil para pruebas).
- **Asistente**: requiere una clave válida de OpenAI. Sin ella, el chat lo indica y no falla.
- **Certificados**: se generan con QuestPDF (licencia Community) e incluyen el logo y la paleta de BankOs.

---

## 🎨 Identidad visual

Se reutiliza el sistema de diseño de BankOs: logotipos, la paleta espectral (azul marino → púrpura → azul → cian → verde) y la tipografía (Sora / Inter / JetBrains Mono). Los formularios sensibles usan modales de confirmación, con énfasis especial en las operaciones que involucran dinero.

---

© BankOs — Infraestructura financiera multi-tenant.
