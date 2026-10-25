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
            throw new InvalidOperationException("AI Face Enrollment chưa sẵn sàng. Enrollment vẫn được giữ PENDING_AI.");
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<StartFaceEnrollmentResponse>(cancellationToken: ct) ?? throw new InvalidOperationException("AI face enrollment response empty");
    }

    public async Task UploadBrowserChunkAsync(
        string externalJobId,
        Stream stream,
        string fileName,
        string contentType,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        using var content = new StreamContent(stream);
        var safeContentType = string.IsNullOrWhiteSpace(contentType)
            ? "video/webm"
            : contentType.Split(';', 2)[0].Trim();

        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(safeContentType)
                    ? "video/webm"
                    : safeContentType);
        form.Add(content, "file", string.IsNullOrWhiteSpace(fileName) ? "webcam.webm" : fileName);
        using var res = await http.PostAsync(
            $"jobs/{Uri.EscapeDataString(externalJobId)}/browser-chunks",
            form,
            ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        try { return (await http.GetAsync("health", ct)).IsSuccessStatusCode; }
        catch { return false; }
    }

    public async Task<(byte[] Data,string ContentType)?> GetOverlayPreviewAsync(string externalJobId, CancellationToken ct = default)
    {
        using var res = await http.GetAsync($"jobs/{Uri.EscapeDataString(externalJobId)}/overlay-preview", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        var data = await res.Content.ReadAsByteArrayAsync(ct);
        var contentType = res.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        return (data, contentType);
    }


    public async Task<string?> FastOverlayAsync(string externalJobId, Stream stream, string fileName, string contentType, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType);
        form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "realtime.jpg" : fileName);
        using var res = await http.PostAsync($"jobs/{Uri.EscapeDataString(externalJobId)}/fast-overlay", form, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

}

