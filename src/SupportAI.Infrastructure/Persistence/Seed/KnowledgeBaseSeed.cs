using SupportAI.Domain.Knowledge;

namespace SupportAI.Infrastructure.Persistence.Seed;

internal static class KnowledgeBaseSeed
{
    public static IEnumerable<KnowledgeArticle> Articles(Guid tenantId, Guid authorId)
    {
        yield return KnowledgeArticle.Create(tenantId, authorId,
            "Restablecer contraseña y desbloquear la cuenta",
            """
            Si olvidaste tu contraseña:
            1. En la pantalla de inicio de sesión pulsa **¿Olvidaste tu contraseña?**.
            2. Recibirás un enlace válido durante 30 minutos.
            3. La nueva contraseña debe tener al menos 8 caracteres, una mayúscula, un dígito y un símbolo.

            Tras 5 intentos fallidos la cuenta se bloquea 15 minutos. Pasado ese tiempo se desbloquea sola;
            un administrador del tenant también puede desbloquearla desde *Ajustes → Usuarios*.
            """,
            "acceso", ["contraseña", "bloqueo", "login"], publish: true);

        yield return KnowledgeArticle.Create(tenantId, authorId,
            "Problemas con la verificación en dos pasos (2FA)",
            """
            Los códigos TOTP dependen de la hora del dispositivo. Si el código nunca es válido:
            - Activa la **hora automática** en el móvil (es la causa del 80 % de los casos).
            - Usa uno de los **códigos de recuperación** que se generaron al activar 2FA.
            - Si los perdiste, el soporte puede desactivar 2FA tras verificar tu identidad (requiere el último pago o el ID de la organización).

            Nunca compartas el código de 6 dígitos con nadie, ni siquiera con el soporte.
            """,
            "acceso", ["2fa", "totp", "login", "seguridad"], publish: true);

        yield return KnowledgeArticle.Create(tenantId, authorId,
            "Cobros duplicados y política de reembolsos",
            """
            Un cobro duplicado suele ser una **preautorización** que el banco libera en 3 a 5 días hábiles.
            Si pasados 5 días los dos cargos siguen confirmados:
            - Facturación verifica los cargos en la pasarela y reembolsa el duplicado en un plazo de 48 h.
            - El reembolso vuelve al mismo medio de pago; el banco puede tardar hasta 10 días en reflejarlo.

            Los reembolsos de suscripciones anuales se calculan de forma prorrateada.
            """,
            "facturacion", ["reembolso", "cobro-duplicado", "tarjeta"], publish: true);

        yield return KnowledgeArticle.Create(tenantId, authorId,
            "Exportar facturas y comprobantes",
            """
            En *Facturación → Historial* puedes:
            - Descargar cada factura en PDF.
            - Exportar el listado completo en **CSV** o **XLSX** con el botón *Exportar* (filtra por rango de fechas).
            - Programar el envío mensual automático a la dirección de tu contador.
            """,
            "facturacion", ["facturas", "csv", "exportar"], publish: true);

        yield return KnowledgeArticle.Create(tenantId, authorId,
            "Diagnóstico de lentitud en el dashboard",
            """
            Pasos para diagnosticar lentitud:
            1. Prueba en una ventana de incógnito (descarta extensiones).
            2. Reduce el rango de fechas de los widgets: el widget de *métricas mensuales* recalcula 12 meses.
            3. Los tenants con más de 500 proyectos deben activar la *agregación nocturna* en Ajustes → Rendimiento.

            Si persiste, adjunta un HAR del navegador y la hora aproximada para que ingeniería revise las trazas.
            """,
            "rendimiento", ["dashboard", "lentitud", "performance"], publish: true);

        yield return KnowledgeArticle.Create(tenantId, authorId,
            "Webhooks: firmas, reintentos y errores 401",
            """
            Cada webhook incluye la cabecera `X-Signature` (HMAC-SHA256 del cuerpo con tu *signing secret*).
            - Tras **rotar el secreto**, el anterior sigue siendo válido 24 h. Pasado ese plazo, las firmas calculadas con el secreto viejo fallan con 401.
            - Calcula la firma sobre el **cuerpo crudo**, sin re-serializar el JSON.
            - Reintentamos con backoff exponencial durante 24 h (8 intentos).
            """,
            "integraciones", ["webhooks", "401", "firma", "hmac"], publish: true);

        yield return KnowledgeArticle.Create(tenantId, authorId,
            "Límites de la API (rate limiting)",
            """
            Límite por defecto: 600 solicitudes por minuto por token. Al superarlo se responde **429** con la cabecera `Retry-After`.
            Recomendaciones: reintentos con backoff exponencial y jitter, y uso de los endpoints *bulk* para operaciones masivas.
            """,
            "integraciones", ["api", "rate-limit", "429"], publish: true);
    }
}
