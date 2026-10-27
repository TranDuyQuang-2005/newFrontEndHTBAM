using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;
using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HTBAM.Tests;

public sealed class SessionFinalizationTests
{
    [Fact]
    public async Task Slow_ai_finalize_is_acknowledged_idempotently_and_completed_only_after_poll()
    {
        await using var db = NewDb();
        await SeedRunning(db);
        var ai = new FinalizeAi();
        ai.StopResults.Enqueue(new("job-30025", "FINALIZING"));
        ai.PollResults.Enqueue(new("job-30025", "FINALIZING"));
        ai.PollResults.Enqueue(new("job-30025", "COMPLETED"));
        var summary = new CountingSummary();
        var service = new SessionService(db, ai, summary, new NoopStorage());

        await service.StopAsync(30025, CancellationToken.None);
        Assert.Equal("FINALIZING", (await db.SessionsSet.FindAsync(30025L))!.Status);
        Assert.Null((await db.SessionsSet.FindAsync(30025L))!.EndedAt);
        Assert.Equal(1, ai.StopCalls);

        await service.StopAsync(30025, CancellationToken.None);
        Assert.Equal(1, ai.StopCalls);
        Assert.Empty(await service.ProcessPendingFinalizationsAsync(CancellationToken.None));
        Assert.Equal("FINALIZING", (await db.SessionsSet.FindAsync(30025L))!.Status);
        Assert.Equal(0, summary.Calls);

        Assert.Equal([30025L], await service.ProcessPendingFinalizationsAsync(CancellationToken.None));
        Assert.Equal("COMPLETED", (await db.SessionsSet.FindAsync(30025L))!.Status);
        Assert.Equal("COMPLETED", (await db.AnalysisJobsSet.FindAsync(20005L))!.Status);
        Assert.NotNull((await db.SessionsSet.FindAsync(30025L))!.EndedAt);
        Assert.Equal(1, summary.Calls);
    }

    [Fact]
    public async Task Stop_timeout_remains_recoverable_and_retry_reuses_the_same_ai_job()
    {
        await using var db = NewDb();
        await SeedRunning(db);
        var ai = new FinalizeAi { StopFailure = new TaskCanceledException("simulated 30 second timeout") };
        var service = new SessionService(db, ai, new CountingSummary(), new NoopStorage());

        await Assert.ThrowsAsync<TaskCanceledException>(() => service.StopAsync(30025, CancellationToken.None));
        Assert.Equal("FINALIZE_FAILED", (await db.SessionsSet.FindAsync(30025L))!.Status);
        Assert.Equal("FINALIZE_FAILED", (await db.AnalysisJobsSet.FindAsync(20005L))!.Status);
        Assert.Null((await db.SessionsSet.FindAsync(30025L))!.EndedAt);

        ai.StopFailure = null;
        ai.PollResults.Enqueue(new("job-30025", "COMPLETED"));
        await service.RetryFinalizeAsync(30025, CancellationToken.None);
        Assert.Equal("FINALIZING", (await db.SessionsSet.FindAsync(30025L))!.Status);
        Assert.Equal([30025L], await service.ProcessPendingFinalizationsAsync(CancellationToken.None));
        Assert.Equal("COMPLETED", (await db.SessionsSet.FindAsync(30025L))!.Status);
        Assert.Single(await db.AnalysisJobsSet.ToListAsync());
        Assert.Equal(1, ai.StopCalls);
        Assert.All(ai.RequestedJobIds, id => Assert.Equal("job-30025", id));
    }

    [Fact]
    public async Task Session_is_not_completed_until_summary_persistence_finishes()
    {
        await using var db = NewDb();
        await SeedRunning(db);
        var ai = new FinalizeAi();
        ai.StopResults.Enqueue(new("job-30025", "FINALIZING"));
        ai.PollResults.Enqueue(new("job-30025", "COMPLETED"));
        var summary = new BlockingSummary();
        var service = new SessionService(db, ai, summary, new NoopStorage());
        await service.StopAsync(30025, CancellationToken.None);

        var processing = service.ProcessPendingFinalizationsAsync(CancellationToken.None);
        await summary.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("FINALIZING", (await db.SessionsSet.FindAsync(30025L))!.Status);
        Assert.Equal("FINALIZING", (await db.AnalysisJobsSet.FindAsync(20005L))!.Status);

        summary.Release.SetResult();
        Assert.Equal([30025L], await processing);
        Assert.Equal("COMPLETED", (await db.SessionsSet.FindAsync(30025L))!.Status);
    }

    [Fact]
    public async Task Camera_session_without_video_artifact_remains_recoverable()
    {
        await using var db = NewDb();
        await SeedRunning(db);
        var session = (await db.SessionsSet.FindAsync(30025L))!;
        session.CameraId = 9;
        await db.SaveChangesAsync();
        var ai = new FinalizeAi();
        ai.StopResults.Enqueue(new("job-30025", "COMPLETED"));
        var summary = new CountingSummary();
        var service = new SessionService(db, ai, summary, new NoopStorage());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StopAsync(30025, CancellationToken.None));

        Assert.Equal("FINALIZE_FAILED", session.Status);
        Assert.Null(session.EndedAt);
        Assert.Equal(0, summary.Calls);
    }

    [Fact]
    public async Task Completed_dashboard_uses_closed_identities_and_persisted_summary()
    {
        await using var db = NewDb();
        await SeedRunning(db);
        var session = (await db.SessionsSet.FindAsync(30025L))!;
        session.Status = "COMPLETED";
        session.EndedAt = DateTime.UtcNow;
        db.StableIdentitiesSet.Add(new StableIdentity { SessionId = 30025, StableId = "stable-1", StudentId = 7, IdentityStatus = "IDENTIFIED", FirstSeenAt = DateTime.UtcNow.AddMinutes(-10), LastSeenAt = DateTime.UtcNow.AddMinutes(-1), State = "CLOSED" });
        db.ClassSessionSummariesSet.Add(new ClassSessionSummary { SessionId = 30025, ObservedStudentCount = 1, FocusedRatio = .75m, DistractedRatio = .25m });
        await db.SaveChangesAsync();
        var service = new SessionService(db, new FinalizeAi(), new CountingSummary(), new NoopStorage());

        var dashboard = await service.DashboardAsync(30025, CancellationToken.None);

        Assert.Single(dashboard.Identities);
        Assert.Equal(1, dashboard.IdentifiedStudents);
        Assert.Equal(.75m, dashboard.Behavior.Focused);
        Assert.Equal("EMPTY", dashboard.Outputs.BehaviorEvents.Status);
        Assert.Contains("số liệu 0", dashboard.Outputs.BehaviorEvents.Message);
    }

    [Fact]
    public async Task Completed_session_recovery_rebuilds_summary_and_registers_only_existing_artifact()
    {
        await using var db = NewDb();
        await SeedRunning(db);
        var session = (await db.SessionsSet.FindAsync(30025L))!;
        session.Status = "COMPLETED";
        session.EndedAt = DateTime.UtcNow;
        var job = (await db.AnalysisJobsSet.FindAsync(20005L))!;
        job.Status = "COMPLETED";
        await db.SaveChangesAsync();
        var artifact = new AiArtifactMetadata("annotations/sessions/30025/corr.mp4", "recording.mp4", "video/mp4", 12, new string('a', 64));
        var ai = new FinalizeAi();
        ai.PollResults.Enqueue(new("job-30025", "COMPLETED", artifact));
        ai.PollResults.Enqueue(new("job-30025", "COMPLETED", artifact));
        var summary = new CountingSummary();
        var service = new SessionService(db, ai, summary, new NoopStorage());

        await service.RetryFinalizeAsync(30025, CancellationToken.None);
        await service.RetryFinalizeAsync(30025, CancellationToken.None);

        Assert.Equal(2, summary.Calls);
        Assert.Single(await db.VideosSet.Where(x => x.SessionId == 30025 && x.VideoType == "ANNOTATED_OUTPUT").ToListAsync());
        Assert.Equal("COMPLETED", session.Status);
        Assert.NotNull(session.EndedAt);
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task SeedRunning(AppDbContext db)
    {
        db.SessionsSet.Add(new Session
        {
            Id = 30025, ClassSectionId = 1, OriginalTeacherId = 10, AttendancePolicyId = 1,
            ScheduledStart = DateTime.UtcNow.AddHours(-1), StartedAt = DateTime.UtcNow.AddMinutes(-30), Status = "RUNNING"
        });
        db.AnalysisJobsSet.Add(new AnalysisJob
        {
            Id = 20005, SessionId = 30025, CorrelationId = "corr", ExternalJobId = "job-30025",
            Status = "RUNNING", StartedAt = DateTime.UtcNow.AddMinutes(-30)
        });
        await db.SaveChangesAsync();
    }

    private sealed class FinalizeAi : IAiClient
    {
        public Queue<StopAiJobResponse> StopResults { get; } = new();
        public Queue<StopAiJobResponse> PollResults { get; } = new();
        public Exception? StopFailure { get; set; }
        public int StopCalls { get; private set; }
        public List<string> RequestedJobIds { get; } = [];
        public Task<StopAiJobResponse> StopAsync(string externalJobId, CancellationToken ct = default)
        {
            StopCalls++; RequestedJobIds.Add(externalJobId);
            if (StopFailure is not null) throw StopFailure;
            return Task.FromResult(StopResults.Dequeue());
        }
        public Task<StopAiJobResponse> GetJobAsync(string externalJobId, CancellationToken ct = default)
        {
            RequestedJobIds.Add(externalJobId);
            return Task.FromResult(PollResults.Dequeue());
        }
        public Task<StartAiJobResponse> StartAsync(StartAiJobRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AiCapabilitiesResponse> CapabilitiesAsync(CancellationToken ct = default) => Task.FromResult(new AiCapabilitiesResponse(true, true, true, ["test"]));
        public Task<StartFaceEnrollmentResponse> StartFaceEnrollmentAsync(FaceEnrollmentManifest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> HealthAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task UploadBrowserChunkAsync(string externalJobId, Stream stream, string fileName, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(byte[] Data, string ContentType)?> GetOverlayPreviewAsync(string externalJobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> FastOverlayAsync(string externalJobId, Stream stream, string fileName, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CountingSummary : ISummaryService
    {
        public int Calls { get; private set; }
        public Task RebuildSessionAsync(long sessionId, DateTime endedAt, CancellationToken ct) { Calls++; return Task.CompletedTask; }
    }

    private sealed class BlockingSummary : ISummaryService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RebuildSessionAsync(long sessionId, DateTime endedAt, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }

    private sealed class NoopStorage : IObjectStorage
    {
        public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PutAsync(string objectKey, Stream stream, long sizeBytes, string contentType, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string objectKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetReadUrlAsync(string objectKey, int expirySeconds = 900, CancellationToken ct = default) => Task.FromResult(objectKey);
        public Task<string> GetInternalReadUrlAsync(string objectKey, int expirySeconds = 900, CancellationToken ct = default) => Task.FromResult(objectKey);
        public Task<string> GetInternalWriteUrlAsync(string objectKey, int expirySeconds = 900, CancellationToken ct = default) => Task.FromResult(objectKey);
        public Task<ObjectStorageMetadata?> StatAsync(string objectKey, CancellationToken ct = default) => Task.FromResult<ObjectStorageMetadata?>(new(1, "video/mp4"));
        public Task CopyRangeToAsync(string objectKey, long offset, long length, Stream destination, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string objectKey, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> HealthAsync(CancellationToken ct = default) => Task.FromResult(true);
    }
}
