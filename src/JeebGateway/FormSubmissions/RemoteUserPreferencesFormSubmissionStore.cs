using JeebGateway.Services.Generated.ServiceRemoteUserPreferences;
using JeebGateway.Users;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace JeebGateway.FormSubmissions;

public sealed class RemoteUserPreferencesFormSubmissionStore(IServiceScopeFactory scopeFactory) : IFormSubmissionStore
{
    public async Task<FormSubmissionRecord?> GetAsync(string userId, string templateName, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMilliseconds(1500));
        using var scope = scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ServiceRemoteUserPreferencesClient>();
        var key = Key(templateName);
        try
        {
            var nested = await client.Data_GetNestedPreferenceAsync(userId, key, budget.Token);
            return FormSubmissionSnapshot.Decode(nested?.Value);
        }
        catch (ApiException ex) when (ex.StatusCode == 404) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Preferences read exceeded its budget.");
        }
        catch (TimeoutRejectedException) { throw new TimeoutException("Preferences read timed out."); }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or BrokenCircuitException)
        {
            throw new UserPreferencesUnavailableException("Preferences read failed.");
        }
    }

    public async Task SetAsync(string userId, string templateName, FormSubmissionRecord record, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMilliseconds(2000));
        using var scope = scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ServiceRemoteUserPreferencesClient>();
        var key = Key(templateName);
        try
        {
            // RUP replaces this complete nested dictionary in one SQL upsert.
            // Timestamp, answers and coverage therefore have the same commit boundary.
            await client.Data_SetNestedPreferenceAsync(userId, key,
                new NestedPreferenceInput { Value = FormSubmissionSnapshot.Encode(record) }, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Preferences write exceeded its budget.");
        }
        catch (TimeoutRejectedException) { throw new TimeoutException("Preferences write timed out."); }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or BrokenCircuitException)
        {
            throw new UserPreferencesUnavailableException("Preferences write failed.");
        }
    }

    private static string Key(string templateName)
    {
        if (!FormTemplateRegistry.IsKnown(templateName)) throw new ArgumentException("Unknown form template.");
        return "jeeb.form." + templateName;
    }
}
