# Desplegar SupportAI en Azure (App Service + Azure SQL)

Guía para publicar la API y la web desde **VS Code** con la extensión *Azure App Service*.

## Qué se crea y por qué no una VM

| Recurso | Uso | Plan recomendado |
|---|---|---|
| Resource Group `rg-supportai` | Agrupa todo (borrarlo elimina todo) | — |
| App Service Plan Linux | Máquina administrada que comparten las dos apps | **B1** (permite *Always On*, necesario para los workers) |
| Web App `supportai-api-<sufijo>` | API (.NET 9) | En el plan B1 |
| Web App `supportai-web-<sufijo>` | Blazor Server (.NET 9) | En el mismo plan |
| Azure SQL Server + Database `SupportAI` | Base de datos | Oferta gratuita de Azure SQL (serverless) o *Basic* |

App Service en lugar de una VM: no administras sistema operativo, parches, IIS/nginx ni certificados; HTTPS, logs, reinicios y escalado vienen incluidos, y se despliega con un clic desde VS Code. Una VM solo tiene sentido si necesitas control total del servidor.

> **Costo orientativo**: el plan B1 Linux cuesta unos pocos dólares al mes y es el único costo fijo si usas la oferta gratuita de Azure SQL. Revisa la calculadora de precios de Azure para tu región. El plan Free (F1) no sirve: no tiene *Always On* y el procesador del outbox y el monitor de SLA se detendrían.

## 0. Requisitos

- Cuenta de Azure con una suscripción activa.
- VS Code con la carpeta `SupportAI` abierta (**File → Open Folder → SupportAI**) y las extensiones recomendadas (`.vscode/extensions.json`): *Azure App Service* y *Azure Resources*.
- En VS Code: icono de Azure en la barra lateral → **Sign in to Azure**.

## 1. Resource Group

Vista de Azure → sección **Resources** → botón **+** → **Create Resource Group** → nombre `rg-supportai` → región (por ejemplo, `East US 2` o la más cercana a ti). Usa la misma región para todo.

## 2. Azure SQL Database (en el portal)

La extensión no crea servidores SQL, así que este paso se hace en el portal de Azure:

1. **Create a resource → SQL Database**.
2. Resource group `rg-supportai`, nombre de la base `SupportAI`.
3. **Server → Create new**: nombre único (por ejemplo, `sql-supportai-<sufijo>`), misma región.
   - Authentication: **Use both SQL and Microsoft Entra authentication** (o *SQL authentication*), y define un usuario y contraseña de administrador. Guárdalos.
4. Si aparece **"Apply offer"** (oferta gratuita de Azure SQL), aplícala. Comportamiento al alcanzar el límite: *Auto-pause the database until next month*.
   Si no aparece, elige *Basic* en "Configure database".
5. **Networking**:
   - Connectivity: *Public endpoint*.
   - **Allow Azure services and resources to access this server: Yes** (para que App Service conecte).
   - **Add current client IP address: Yes** (para conectarte desde tu PC si lo necesitas).
6. **Review + create**.

Al terminar, en la base de datos → **Connection strings** → pestaña **ADO.NET**: copia la cadena y reemplaza la contraseña. Debe quedar así:

```text
Server=tcp:sql-supportai-xxx.database.windows.net,1433;Initial Catalog=SupportAI;User ID=<usuario>;Password=<contraseña>;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;
```

## 3. Las dos Web Apps (desde VS Code)

Paleta de comandos (`Ctrl+Shift+P`) → **Azure App Service: Create New Web App... (Advanced)**:

| Pregunta | Respuesta |
|---|---|
| Name | `supportai-api-<sufijo>` |
| Resource group | `rg-supportai` |
| Runtime stack | **.NET 9** |
| OS | **Linux** |
| Location | La misma que la base de datos |
| App Service plan | **Create new** → `plan-supportai` → **B1** |
| Application Insights | *Skip for now* (o créalo si quieres métricas) |

Repite para `supportai-web-<sufijo>` y elige el **mismo** plan `plan-supportai`.

> El dominio real de cada app aparece en el portal → la app → **Overview → Default domain** (puede incluir un sufijo aleatorio y la región, por ejemplo `supportai-api-xxxx.eastus2-01.azurewebsites.net`). Úsalo tal cual.

## 4. Configuración de cada app

### 4.1 Variables (App settings)

Portal → la app → **Settings → Environment variables → App settings → Advanced edit** → pega el contenido de:

- API: [`docs/azure/appsettings.api.json`](azure/appsettings.api.json)
- Web: [`docs/azure/appsettings.web.json`](azure/appsettings.web.json)

Reemplaza los marcadores `<...>`:

| Variable | Valor |
|---|---|
| `ConnectionStrings__SupportAI` | La cadena del paso 2 |
| `Jwt__SigningKey` | Clave aleatoria larga (ver comando abajo). Si la cambias, los usuarios deben volver a iniciar sesión |
| `Database__DemoPassword` | Contraseña de las cuentas demo. **No uses `Passw0rd!` en un sitio público** |
| `Llm__Provider` / `Llm__ApiKey` | `Fake` para la demo sin costo; `Claude` + tu API key para usar Claude real |
| `Api__BaseUrl` (web) | `https://<dominio de la API>/`, con la barra final |

Genera la clave JWT en PowerShell:

```powershell
$b = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
```

> Alternativa desde VS Code: en la vista de Azure, despliega la app → **Application Settings** → clic derecho → **Add New Setting...**, una por una.
> Más adelante, mueve los secretos a **Azure Key Vault** con referencias `@Microsoft.KeyVault(...)` y usa *Managed Identity* para SQL.

### 4.2 Configuración general

Portal → la app → **Settings → Configuration → General settings**:

| Opción | API | Web | Por qué |
|---|---|---|---|
| Startup Command | `dotnet SupportAI.Api.dll` | `dotnet SupportAI.Web.dll` | Indica qué ensamblado ejecutar |
| Web sockets | **On** | **On** | SignalR (API) y Blazor Server (Web) |
| Always on | **On** | **On** | Mantiene vivos el outbox y el monitor de SLA |
| Session affinity (ARR) | Off | **On** | Blazor Server mantiene un circuito por usuario |
| HTTPS Only | On | On | — |
| Minimum TLS | 1.2 | 1.2 | — |

API → **Monitoring → Health check** → path `/health/ready`.

Guarda (la app se reinicia).

## 5. Publicar y desplegar

1. En VS Code: **Terminal → Run Task → `publish: api + web`**. Genera `artifacts/publish/api` y `artifacts/publish/web` (carpeta ignorada por Git).
2. En el explorador de archivos: clic derecho en **`artifacts/publish/api`** → **Deploy to Web App...** → elige `supportai-api-<sufijo>` → **Deploy**.
   - Si pregunta si debe recordar ese destino para siempre, responde **"Skip for now"**: el repositorio tiene dos apps.
   - Si pregunta por compilar en el servidor, responde **No** (ya está publicado).
3. Clic derecho en **`artifacts/publish/web`** → **Deploy to Web App...** → `supportai-web-<sufijo>`.

En el primer arranque, la API aplica las migraciones en Azure SQL, publica los prompts y carga los datos demo (si `Database__SeedDemoData=true`). Con la base serverless pausada puede tardar alrededor de un minuto.

## 6. Verificar

1. Logs en vivo: vista de Azure → clic derecho en la app → **Start Streaming Logs**.
2. `https://<dominio-api>/health/ready` → `{"status":"Healthy",...}`.
3. `https://<dominio-api>/scalar/v1` → documentación interactiva (si `OpenApi__Enabled=true`).
4. `https://<dominio-web>/` → login con `admin@acme.test` y la contraseña que pusiste en `Database__DemoPassword`.
5. Crea un ticket como cliente y comprueba que se clasifica solo en unos segundos.

## 7. Problemas frecuentes

| Síntoma | Causa probable | Solución |
|---|---|---|
| "Application Error" o 500.30 al abrir la API | Falta una variable obligatoria (por ejemplo, `Jwt__SigningKey` de menos de 32 caracteres) o no conecta a la BD | Revisa el Log stream: el error de arranque aparece completo |
| `Login failed for user` / `Cannot open server` | Credenciales o firewall de SQL | Revisa la cadena y que "Allow Azure services" esté en *Yes* |
| La web muestra "Reconnecting..." | Web sockets desactivados o sin afinidad ARR | Activa *Web sockets* y *Session affinity* en la app web |
| La web no conecta con la API | `Api__BaseUrl` incorrecto | Usa el dominio exacto de la API con `https://` y barra final |
| Los tickets no se clasifican | *Always on* apagado, `Workers__Enabled=false` o prompts sin activar | Activa *Always on*; revisa `/admin/prompts` |
| 401 en todo tras reconfigurar | Cambió `Jwt__SigningKey` | Cierra sesión y vuelve a entrar |
| El primer request tarda o falla | Base serverless reanudándose | Reintenta; la API reintenta automáticamente hasta 6 veces |

## 8. Volver a desplegar

1. `Run Task → publish: api + web`.
2. Clic derecho en cada carpeta → **Deploy to Web App...**.

Las migraciones nuevas se aplican solas al arrancar mientras `Database__ApplyMigrationsOnStartup=true`. En un entorno real, desactívalo y aplica el script idempotente que genera el pipeline (`artifacts/migrations.sql`).

## 9. Siguiente paso: despliegue automático desde GitLab

El job `deploy-staging` de `.gitlab-ci.yml` es una plantilla. Para desplegar en Azure desde CI:

1. Crea un *service principal* (o federación OIDC) con permiso *Website Contributor* sobre `rg-supportai`.
2. Guarda sus credenciales como variables *masked* en GitLab.
3. En el job: `az login --service-principal ...`, luego `az webapp deploy --resource-group rg-supportai --name supportai-api-<sufijo> --src-path api.zip --type zip`.
