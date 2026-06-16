# BankOS Admin — Conexión directa a la base de datos (sin API Laravel)

Esta aplicación **ya no usa la API de Laravel**. Ahora se conecta **directamente a la misma
base de datos PostgreSQL** que usa BankOS (en tu VPS). Toda la lógica (login, clientes,
cuentas, depósitos, transferencias, PQRS, auditoría, configuración) se ejecuta contra la base
de datos, replicando el comportamiento del backend Laravel.

No cambió nada de la interfaz: los controladores, vistas, correos y certificados PDF siguen
igual. Solo se reemplazó la capa de datos.

---

## 1) Configuración (`appsettings.json`)

La sección `Database` ya viene con tus datos:

```json
"Database": {
  "Host": "87.99.154.103",
  "Port": "5433",
  "User": "bankos",
  "Password": "secret",
  "CentralDb": "bankos_central",
  "TenantDbPrefix": "tenant_",
  "DomainSuffix": ".bank.os"
}
```

> Las claves del JSON **deben llamarse exactamente así** (`Host`, `Port`, `User`, `Password`,
> `CentralDb`, `TenantDbPrefix`, `DomainSuffix`). Si no coinciden con las propiedades internas,
> .NET las ignora y la app intenta conectarse a valores por defecto.

Opcionalmente puedes añadir, dentro de `Database`:
- `"SslMode": "Prefer"` (Disable | Allow | Prefer | Require | VerifyCA | VerifyFull) — por defecto `Prefer`.
- `"TrustServerCertificate": true` — por defecto `true` (útil con certificados autofirmados del VPS).

### Cómo se eligen las bases de datos (multi-tenant, `stancl/tenancy`)

- **Base central / super admin** = `CentralDb` → **`bankos_central`**. Contiene `tenants`,
  `domains` y `tenant_configs`.
- **Una base por banco**: su nombre es **`TenantDbPrefix` + `<id del banco>`**.
  Ejemplos reales: `tenant_test-bank`, `tenant_fintech-co`.
- `DomainSuffix` (`.bank.os`) es el **dominio público** de cada banco (p. ej. `test-bank.bank.os`).
  **No** forma parte del nombre de la base; el nombre de la base es solo `tenant_<idtenant>`.

El flujo de login es:
1. La app lee la lista de bancos desde `bankos_central.tenants` (estado `active`).
2. En el login **eliges el banco (tenant)**; el valor enviado es el `id` del banco.
3. Con ese id se arma el nombre de la base: `tenant_<id>` (p. ej. `tenant_test-bank`).
4. La app se conecta a esa base, valida credenciales (bcrypt) y exige rol `administrador`.
5. A partir de ahí, todo (clientes, cuentas, transacciones, PQRS, auditoría) se lee/escribe en
   la base de ese banco; la configuración vive en `bankos_central.tenant_configs`.

### Acceso de red al VPS
PostgreSQL escucha en el puerto **5433**. La máquina donde corras la app debe poder llegar a
`87.99.154.103:5433`:
- `postgresql.conf` → `listen_addresses = '*'`
- `pg_hba.conf` → permitir tu IP/usuario (p. ej. `host all bankos 0.0.0.0/0 scram-sha-256`)
- Firewall del VPS abriendo el **5433**
- Reinicia PostgreSQL tras los cambios.

Si PostgreSQL **no** está expuesto a internet (recomendado), abre un túnel SSH desde la máquina
de la app y usa `"Host": "127.0.0.1"` y `"Port": "5433"`:
```bash
ssh -N -L 5433:localhost:5433 usuario@87.99.154.103
```

---

## 2) Verificar la conexión (¡hazlo primero!)

Arranca la app y abre:

```
http://localhost:5001/Home/DbCheck
```

Si todo está bien verás algo así:

```json
{
  "host": "87.99.154.103",
  "centralDatabase": "bankos_central",
  "centralOk": true,
  "tenants": [
    { "id": "test-bank",  "name": "Test Bank",  "database": "tenant_test-bank",  "ok": true, "users": 3 },
    { "id": "fintech-co", "name": "Fintech Co", "database": "tenant_fintech-co", "ok": true, "users": 3 }
  ]
}
```

- `centralOk: false` → revisa Host/Port/User/Password o el nombre `bankos_central`.
- Un banco con `ok: false` y un `error` → su base `tenant_<id>` no existe o el prefijo difiere;
  el mensaje del JSON indica el motivo exacto.

---

## 3) Ejecutar

```bash
cd BankAdmin
dotnet restore
dotnet run
```

La app queda en `http://localhost:5001`.

### Credenciales de demostración (del seeder de Laravel)
Por cada banco (contraseña: `password123`):

- **Admin:**   `admin@<id-del-banco>.internal`   → p. ej. `admin@test-bank.internal`
- Cliente:     `john@<id-del-banco>.internal`
- Cliente:     `jane@<id-del-banco>.internal`

En el login eliges el banco, el correo y la contraseña. Solo entran usuarios con rol
`administrador`.

---

## 4) Qué se respetó del backend

- **Contraseñas bcrypt** compatibles con Laravel (`Hash::make` / `password_verify`).
- **Depósito / transferencia** dentro de transacción con bloqueo de fila: límite por
  transacción, comisión (porcentaje/fija), verificación de fondos (monto + comisión),
  conversión con `exchange_rates` si difieren las monedas, `balance_after` y
  `meta.destination_balance_after`. Las transferencias bloquean ambas cuentas en orden
  determinista para evitar interbloqueos.
- **Auditoría** en `audit_logs` con el `performed_by_user_id` del admin de la sesión.
- **PQRS**: responder marca `resuelto`, guarda `admin_response` y envía el correo al cliente
  desde esta app (requiere configurar `Email`).
- **Configuración** se lee/actualiza en `bankos_central.tenant_configs`.

### Correos (opcional)
Completa la sección `Email` y pon `"Enabled": true`. Con `false` solo se registran en el log.

### Asistente IA (opcional)
Agrega `OpenAI:ApiKey`. Sin clave, el chat avisa y no falla.

---

## 5) Notas
- **Idempotencia:** cada operación genera su clave; evita el doble clic (los formularios usan
  confirmación en modal).
- Si cambiaras el prefijo de bases por tenant, ajústalo en `TenantDbPrefix`.
