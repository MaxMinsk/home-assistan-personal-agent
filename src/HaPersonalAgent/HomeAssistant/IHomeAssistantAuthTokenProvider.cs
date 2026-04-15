namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: контракт выбора bearer token для Home Assistant endpoint.
/// Зачем: discovery, agent tools и confirmation executor должны одинаково решать, использовать Supervisor token или long-lived token.
/// Как: Resolve получает уже собранный endpoint и возвращает безопасный result с token source или причиной отсутствия токена.
/// </summary>
public interface IHomeAssistantAuthTokenProvider
{
    HomeAssistantAuthToken Resolve(Uri endpoint);
}
