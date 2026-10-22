using System.Net;
using System.Net.Http.Json;
using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;

namespace HTBAM.Infrastructure.Services;

public sealed class AiClient(HttpClient http) : IAiClient
{
    public async Task<StartAiJobResponse> StartAsync(StartAiJobRequest request, CancellationToken ct = default)
    {
        using var res = await http.PostAsJsonAsync("jobs/start", request, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<StartAiJobResponse>(cancellationToken: ct) ?? throw new InvalidOperationException("AI response empty");
    }

    public async Task<StopAiJobResponse> StopAsync(string externalJobId, CancellationToken ct = default)
    {
        using var res = await http.PostAsync($"jobs/{Uri.EscapeDataString(externalJobId)}/stop", null, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<StopAiJobResponse>(cancellationToken: ct) ?? throw new InvalidOperationException("AI response empty");
    }

    public async Task<AiCapabilitiesResponse> CapabilitiesAsync(CancellationToken ct = default)
    {
        using var res = await http.GetAsync("capabilities", ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<AiCapabilitiesResponse>(cancellationToken: ct) ?? new AiCapabilitiesResponse(false, false, false, Array.Empty<string>());
    }

    public async Task<StartFaceEnrollmentResponse> StartFaceEnrollmentAsync(FaceEnrollmentManifest request, CancellationToken ct = default)
    {
        using var res = await http.PostAsJsonAsync("face-enrollments/start", request, ct);
        if (res.StatusCode == HttpStatusCode.NotImplemented || res.StatusCode == HttpStatusCode.ServiceUnavailable)
            throw new InvalidOperationException("AI Face Enrollment chÆ°a sáºµn sÃ ng. Enrollment váº«n Ä‘Æ°á»£c giá»¯ PENDING_AI.");
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<StartFaceEnrollmentResponse>(cancellationToken: ct) ?? throw new InvalidOperationException("AI face enrollment response empty");
    }

    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        try { return (await http.GetAsync("health", ct)).IsSuccessStatusCode; }
        catch { return false; }
    }
}

