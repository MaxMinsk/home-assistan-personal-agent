using System.Security.Cryptography;
using System.Text;

namespace HaPersonalAgent.Web;

/// <summary>
/// Что: чистая логика авторизации входящего веб-запроса (без зависимости от HTTP-типов, чтобы тестировать без сервера).
/// Зачем: за HA Ingress аутентифицирует сам Home Assistant, а прямой (проброшенный) порт нужно закрывать api-token'ом.
/// Как: если токен не настроен — доступ открыт; запрос с заголовком HA Ingress считается доверенным; иначе требуется совпадение предъявленного токена с настроенным в постоянном времени.
/// </summary>
public static class WebRequestAuthorizer
{
    public static bool IsAuthorized(bool hasIngressHeader, string? providedToken, string? configuredToken)
    {
        // Токен не задан => веб-хост открыт (локальная разработка / доверенная сеть).
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return true;
        }

        // Запрос пришёл через HA Ingress — Home Assistant уже аутентифицировал пользователя.
        if (hasIngressHeader)
        {
            return true;
        }

        if (string.IsNullOrEmpty(providedToken))
        {
            return false;
        }

        // Постоянное по времени сравнение, чтобы не давать timing-подсказок при переборе токена.
        var provided = Encoding.UTF8.GetBytes(providedToken);
        var expected = Encoding.UTF8.GetBytes(configuredToken);
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }
}
