using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Application.Services;

public sealed class AutoCommentService(
    IAppDbContext db,
    ILlmProviderResolver providerResolver,
    IAuditService audit) : IAutoCommentService
{
    public async Task<LlmGenerationResult?> GenerateSessionAsync(long sessionId, long? userId, CancellationToken ct = default)
    {
        var summary = await db.ClassSessionSummaries.FirstOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (summary is null) return null;

        var session = await db.Sessions.Where(x => x.Id == sessionId)
            .Select(x => new { x.StartedAt, x.EndedAt, x.ScheduledStart, x.ScheduledEnd, x.AttendancePolicyId }).FirstAsync(ct);
        var behaviorEvents = await db.BehaviorEvents.Where(x => x.SessionId == sessionId).ToListAsync(ct);
        var rangeStart = session.StartedAt ?? session.ScheduledStart;
        var rangeEnd = session.EndedAt ?? session.ScheduledEnd ?? (behaviorEvents.Count > 0 ? behaviorEvents.Max(x => x.EndedAt) : rangeStart);
        var behavior = BehaviorSummaryCalculator.Calculate(behaviorEvents, rangeStart, rangeEnd);
        var attendanceRows = await db.Attendance.Where(x => x.SessionId == sessionId).ToListAsync(ct);
        var policy = await db.AttendancePolicies.FirstOrDefaultAsync(x => x.Id == session.AttendancePolicyId, ct);
        var attendanceScore = policy is null ? null : AverageScorePercent(attendanceRows.Select(x => x.Score), policy.PresentScore, policy.PartialScore, policy.AbsentScore);
        var hadComment = !string.IsNullOrWhiteSpace(summary.AutoComment);
        var result = await GenerateWithFallbackAsync(new LlmCommentRequest(
            "SESSION", "STORED_SUMMARY", "Buổi học",
            behavior.ObservedSeconds > 0 ? behavior.FocusedRatio : summary.FocusedRatio,
            behavior.ObservedSeconds > 0 ? behavior.DistractedRatio : summary.DistractedRatio,
            behavior.ObservedSeconds > 0 ? behavior.SleepyRatio : summary.SleepyRatio,
            behavior.ObservedSeconds > 0 ? behavior.ActiveRatio : summary.ActiveRatio,
            summary.AlertCount, behavior.ObservedSeconds > 0 ? behavior.ObservationQuality : summary.CameraAiQuality,
            attendanceScore, attendanceRows.Sum(x => x.PresentSeconds), attendanceRows.Sum(x => x.ObservedSeconds),
            $"ObservedIdentityCount={(behavior.ObservedSeconds > 0 ? behavior.ObservedIdentityCount : summary.ObservedStudentCount)}"), ct);

        summary.AutoComment = result.Text;
        summary.AutoCommentProvider = ProviderStorageValue(result);
        summary.AutoCommentGeneratedAt = result.GeneratedAt;
        db.Update(summary);
        await db.SaveChangesAsync(ct);
        await WriteAuditAsync(userId, hadComment, "ClassSessionSummary", summary.Id, result, "SESSION", sessionId, ct);
        return result;
    }

    public async Task<LlmGenerationResult?> GenerateStudentAsync(long sessionId, long studentId, long? userId, CancellationToken ct = default)
    {
        var summary = await db.StudentSessionSummaries.FirstOrDefaultAsync(x => x.SessionId == sessionId && x.StudentId == studentId, ct);
        if (summary is null) return null;

        var trustedIdentity = await db.StableIdentities.AnyAsync(x => x.SessionId == sessionId && x.StudentId == studentId && x.IdentityStatus == "IDENTIFIED", ct);
        if (!trustedIdentity) return null;
        var attendance = await db.Attendance.FirstOrDefaultAsync(x => x.SessionId == sessionId && x.StudentId == studentId, ct);
        if (attendance is null || attendance.ObservedSeconds <= 0) return null;
        var studentEvents = await db.BehaviorEvents.Where(x => x.SessionId == sessionId && x.StudentId == studentId).ToListAsync(ct);
        if (!studentEvents.Any(BehaviorSummaryCalculator.IsValidState)) return null;
        var attendancePolicyId = await db.Sessions.Where(x => x.Id == sessionId).Select(x => x.AttendancePolicyId).FirstAsync(ct);
        var policy = await db.AttendancePolicies.FirstOrDefaultAsync(x => x.Id == attendancePolicyId, ct);
        decimal? attendanceScore = policy is null ? null : ScorePercent(attendance.Score, policy.PresentScore, policy.PartialScore, policy.AbsentScore);
        var hadComment = !string.IsNullOrWhiteSpace(summary.AutoComment);
        var result = await GenerateWithFallbackAsync(new LlmCommentRequest(
            "STUDENT", "STORED_SUMMARY", "Sinh viên trong buổi học",
            summary.FocusedRatio, summary.DistractedRatio, summary.SleepyRatio, summary.ActiveRatio,
            summary.AlertCount, summary.ObservationQuality, attendanceScore, attendance.PresentSeconds, attendance.ObservedSeconds,
            null), ct);

        summary.AutoComment = result.Text;
        summary.AutoCommentProvider = ProviderStorageValue(result);
        summary.AutoCommentGeneratedAt = result.GeneratedAt;
        db.Update(summary);
        await db.SaveChangesAsync(ct);
        await WriteAuditAsync(userId, hadComment, "StudentSessionSummary", summary.Id, result, "STUDENT", studentId, ct);
        return result;
    }

    public async Task<LlmGenerationResult?> GenerateClassSectionAsync(long classSectionId, long? userId, CancellationToken ct = default)
    {
        var classSection = await db.ClassSections.FirstOrDefaultAsync(x => x.Id == classSectionId, ct);
        if (classSection is null) return null;

        var sessions = await db.Sessions.Where(x => x.ClassSectionId == classSectionId && x.Status == "COMPLETED")
            .Select(x => new { x.Id, x.StartedAt, x.EndedAt, x.ScheduledStart, x.ScheduledEnd }).ToListAsync(ct);
        var sessionIds = sessions.Select(x => x.Id).ToArray();
        var aggregate = await db.ClassSessionSummaries.Where(x => sessionIds.Contains(x.SessionId))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Sessions = g.Count(),
                Focused = g.Average(x => x.FocusedRatio),
                Distracted = g.Average(x => x.DistractedRatio),
                Sleepy = g.Average(x => x.SleepyRatio),
                Active = g.Average(x => x.ActiveRatio),
                Alerts = g.Sum(x => x.AlertCount),
                Quality = g.Average(x => x.CameraAiQuality)
            }).FirstOrDefaultAsync(ct);
        if (aggregate is null) return null;

        var behaviorEvents = await db.BehaviorEvents.Where(x => sessionIds.Contains(x.SessionId)).ToListAsync(ct);
        var eventSummaries = sessions.Select(session =>
        {
            var events = behaviorEvents.Where(x => x.SessionId == session.Id).ToList();
            var start = session.StartedAt ?? session.ScheduledStart;
            var end = session.EndedAt ?? session.ScheduledEnd ?? (events.Count > 0 ? events.Max(x => x.EndedAt) : start);
            return BehaviorSummaryCalculator.Calculate(events, start, end);
        }).Where(x => x.ObservedSeconds > 0).ToList();
        var eventSeconds = eventSummaries.Sum(x => x.ObservedSeconds);
        decimal Effective(Func<StoredBehaviorSummary, decimal> selector, decimal persisted) =>
            eventSeconds <= 0 ? persisted : eventSummaries.Sum(x => selector(x) * x.ObservedSeconds) / eventSeconds;
        var alertCount = await db.Alerts.CountAsync(x => sessionIds.Contains(x.SessionId), ct);

        var attendance = await db.Attendance.Where(x => sessionIds.Contains(x.SessionId))
            .Join(db.Sessions, row => row.SessionId, session => session.Id, (row, session) => new { Row = row, session.AttendancePolicyId })
            .Join(db.AttendancePolicies, row => row.AttendancePolicyId, policy => policy.Id,
                (row, policy) => new { row.Row.Score, row.Row.PresentSeconds, row.Row.ObservedSeconds, policy.PresentScore, policy.PartialScore, policy.AbsentScore })
            .ToListAsync(ct);
        var attendanceScore = attendance.Count == 0 ? null : (decimal?)attendance.Average(x => ScorePercent(x.Score, x.PresentScore, x.PartialScore, x.AbsentScore));
        var hadComment = !string.IsNullOrWhiteSpace(classSection.AutoComment);
        var result = await GenerateWithFallbackAsync(new LlmCommentRequest(
            "CLASS_SECTION", "STORED_SUMMARY", "Lớp học phần",
            Effective(x => x.FocusedRatio, aggregate.Focused), Effective(x => x.DistractedRatio, aggregate.Distracted),
            Effective(x => x.SleepyRatio, aggregate.Sleepy), Effective(x => x.ActiveRatio, aggregate.Active),
            alertCount, Effective(x => x.ObservationQuality, aggregate.Quality), attendanceScore, attendance.Sum(x => x.PresentSeconds), attendance.Sum(x => x.ObservedSeconds),
            $"SessionCount={aggregate.Sessions}"), ct);

        classSection.AutoComment = result.Text;
        classSection.AutoCommentProvider = ProviderStorageValue(result);
        classSection.AutoCommentGeneratedAt = result.GeneratedAt;
        db.Update(classSection);
        await db.SaveChangesAsync(ct);
        await WriteAuditAsync(userId, hadComment, "ClassSection", classSection.Id, result, "CLASS_SECTION", classSectionId, ct);
        return result;
    }

    private async Task<LlmGenerationResult> GenerateWithFallbackAsync(LlmCommentRequest request, CancellationToken ct)
    {
        var provider = providerResolver.Resolve();
        if (provider.IsConfigured)
        {
            try
            {
                var generated = await provider.GenerateCommentAsync(request, ct);
                if (generated is not null && string.IsNullOrWhiteSpace(generated.ErrorCode) && !string.IsNullOrWhiteSpace(generated.Text))
                    return generated;
                return RuleBased(request, generated?.ErrorCode ?? "PROVIDER_ERROR");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return RuleBased(request, "PROVIDER_EXCEPTION");
            }
        }

        return RuleBased(request, "PROVIDER_NOT_CONFIGURED");
    }

    private static LlmGenerationResult RuleBased(LlmCommentRequest request, string errorCode)
    {
        var subject = request.SubjectType switch
        {
            "STUDENT" => "Sinh viên",
            "CLASS_SECTION" => "Lớp học",
            _ => "Buổi học"
        };
        var core = request.FocusedRatio >= .70m
            ? $"{subject} duy trì tỷ lệ tập trung ở mức tốt trong dữ liệu quan sát."
            : request.DistractedRatio >= .30m
                ? $"{subject} có tỷ lệ mất tập trung đáng chú ý trong dữ liệu quan sát."
                : request.SleepyRatio >= .15m
                    ? $"{subject} có tỷ lệ trạng thái buồn ngủ cần được tiếp tục theo dõi."
                    : $"{subject} có phân bố hành vi tương đối cân bằng trong dữ liệu quan sát.";
        var metrics = $"Tỷ lệ tập trung {request.FocusedRatio:P1}, mất tập trung {request.DistractedRatio:P1}, buồn ngủ {request.SleepyRatio:P1}, hoạt động {request.ActiveRatio:P1}; hệ thống ghi nhận {request.AlertCount} cảnh báo.";
        var quality = request.ObservationQuality < .50m
            ? "Chất lượng quan sát thấp nên nhận xét này có độ tin cậy hạn chế."
            : "Có thể tiếp tục theo dõi xu hướng này ở các buổi học tiếp theo.";
        return new LlmGenerationResult($"{core} {metrics} {quality}", "RULE_BASED_FALLBACK", "RULE_BASED", DateTime.UtcNow, true, errorCode);
    }

    private async Task WriteAuditAsync(long? userId, bool regenerated, string entityType, long entityId, LlmGenerationResult result, string subjectType, long subjectId, CancellationToken ct)
    {
        await audit.WriteAsync(userId, regenerated ? "AUTO_COMMENT_REGENERATE" : "AUTO_COMMENT_GENERATE", entityType, entityId.ToString(), new
        {
            provider = result.Provider,
            model = result.Model,
            subjectType,
            subjectId,
            fallback = result.IsFallback,
            timestamp = result.GeneratedAt,
            errorCode = result.ErrorCode
        }, ct);
        if (result.IsFallback)
            await audit.WriteAsync(userId, "AUTO_COMMENT_FALLBACK", entityType, entityId.ToString(), new { provider = result.Provider, model = result.Model, subjectType, subjectId, fallback = true, timestamp = result.GeneratedAt, errorCode = result.ErrorCode }, ct);
    }

    private static string ProviderStorageValue(LlmGenerationResult result) =>
        result.IsFallback ? "RULE_BASED_FALLBACK" : $"{result.Provider}:{result.Model}";

    private static decimal? AverageScorePercent(IEnumerable<decimal> scores, decimal present, decimal partial, decimal absent)
    {
        var values = scores.ToList();
        return values.Count == 0 ? null : values.Average(x => ScorePercent(x, present, partial, absent));
    }

    private static decimal ScorePercent(decimal score, decimal present, decimal partial, decimal absent)
    {
        var maximum = Math.Max(present, Math.Max(partial, absent));
        return maximum <= 0 ? 0 : Math.Clamp(score / maximum * 100m, 0m, 100m);
    }
}
