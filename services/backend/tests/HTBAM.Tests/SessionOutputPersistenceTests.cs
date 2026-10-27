using HTBAM.Application.DTOs;
using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HTBAM.Tests;

public sealed class SessionOutputPersistenceTests
{
    [Fact]
    public async Task Ai_event_retry_does_not_duplicate_behavior_alert_or_identity()
    {
        await using var db = NewDb();
        db.SessionsSet.Add(new Session { Id = 30023, ClassSectionId = 1, OriginalTeacherId = 1, AttendancePolicyId = 1, ScheduledStart = DateTime.UtcNow.AddHours(-1), Status = "RUNNING" });
        db.SessionStudentsSet.Add(new SessionStudent { SessionId = 30023, StudentId = 7 });
        db.AlertRulesSet.Add(new AlertRule { Code = "DISTRACTED", Name = "Distracted", BehaviorLabel = "DISTRACTED", MinDurationSeconds = 1, MinConfidence = .5m, MinObservationQuality = .5m, Enabled = true });
        await db.SaveChangesAsync();
        var service = new AiEventService(db, new AlertEngine(db));
        var started = DateTime.UtcNow.AddSeconds(-10);
        var envelope = new AiEventEnvelope("event-30023-1", 30023, "BEHAVIOR_SEGMENT", started, "stable-1", "track-1", 7, .9m, .2m, "DISTRACTED", .9m, .8m, null, null, "model", "threshold", started, started.AddSeconds(10));

        Assert.True(await service.ProcessAsync(envelope, CancellationToken.None));
        Assert.False(await service.ProcessAsync(envelope, CancellationToken.None));

        Assert.Single(await db.BehaviorEventsSet.Where(x => x.SessionId == 30023).ToListAsync());
        Assert.Single(await db.AlertsSet.Where(x => x.SessionId == 30023).ToListAsync());
        Assert.Single(await db.StableIdentitiesSet.Where(x => x.SessionId == 30023).ToListAsync());
        Assert.Single(await db.AiEventReceiptsSet.Where(x => x.SessionId == 30023).ToListAsync());
    }

    [Fact]
    public async Task Summary_is_persisted_with_zero_metrics_when_session_has_no_events()
    {
        await using var db = NewDb();
        var started = DateTime.UtcNow.AddMinutes(-30);
        db.AttendancePoliciesSet.Add(new AttendancePolicy { Id = 1, Code = "P", Name = "Policy", PresentThreshold = .8m, PartialThreshold = .5m });
        db.SessionsSet.Add(new Session { Id = 30023, ClassSectionId = 1, OriginalTeacherId = 1, AttendancePolicyId = 1, ScheduledStart = started, StartedAt = started, Status = "FINALIZING" });
        db.SessionStudentsSet.AddRange(new SessionStudent { SessionId = 30023, StudentId = 7 }, new SessionStudent { SessionId = 30023, StudentId = 8 });
        await db.SaveChangesAsync();

        await new SummaryService(db).RebuildSessionAsync(30023, started.AddMinutes(30), CancellationToken.None);

        var students = await db.StudentSessionSummariesSet.Where(x => x.SessionId == 30023).ToListAsync();
        Assert.Equal(2, students.Count);
        Assert.All(students, x => { Assert.Equal(0, x.FocusedRatio); Assert.Equal(0, x.AlertCount); });
        Assert.Equal(2, await db.AttendanceSet.CountAsync(x => x.SessionId == 30023));
        var cls = await db.ClassSessionSummariesSet.SingleAsync(x => x.SessionId == 30023);
        Assert.Equal(0, cls.ObservedStudentCount);
        Assert.Equal(0, cls.AlertCount);
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
