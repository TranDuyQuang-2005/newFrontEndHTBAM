using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;
using HTBAM.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Application.Services;

public sealed class AiServiceUnavailableException(string message)
    : Exception(message);

public sealed class SessionService(IAppDbContext db, IAiClient ai, ISummaryService summary, IObjectStorage storage) : ISessionService
{
    public async Task<Session> CreateAsync(CreateSessionRequest r, CancellationToken ct)
    {
        var classSection = await db.ClassSections.FirstOrDefaultAsync(x => x.Id == r.ClassSectionId && x.IsActive, ct)
            ?? throw new InvalidOperationException("Lớp học phần không tồn tại hoặc đã ngừng hoạt động.");

        if ((r.CameraId is null) == (r.VideoId is null))
            throw new InvalidOperationException("Session phải chọn đúng một nguồn: camera hoặc video.");

        if (r.CameraId is not null)
        {
            var camera = await db.Cameras.FirstOrDefaultAsync(x => x.Id == r.CameraId.Value && x.IsActive, ct)
                ?? throw new InvalidOperationException("Camera không tồn tại hoặc đã tắt.");
            if (r.RoomId is not null && camera.RoomId != r.RoomId.Value)
                throw new InvalidOperationException("Camera không thuộc phòng đã chọn.");
        }
        if (r.VideoId is not null && !await db.Videos.AnyAsync(x => x.Id == r.VideoId.Value && x.Status == "READY" && x.VideoType == "INPUT_UPLOAD", ct))
            throw new InvalidOperationException("Video không tồn tại hoặc chưa READY.");

        var classRoster = await db.Enrollments.Where(x => x.ClassSectionId == classSection.Id).Select(x => x.StudentId).ToArrayAsync(ct);
        if (classRoster.Length == 0) throw new InvalidOperationException("Lớp học phần chưa có roster.");
        var requested = r.StudentIds?.Distinct().ToArray() ?? Array.Empty<long>();
        var roster = requested.Length == 0 ? classRoster : requested;
        var invalid = roster.Except(classRoster).ToArray();
        if (invalid.Length > 0) throw new InvalidOperationException("Roster Session chứa sinh viên không thuộc lớp học phần.");

        if (!await db.AttendancePolicies.AnyAsync(x => x.Id == r.AttendancePolicyId && x.IsActive, ct)) throw new InvalidOperationException("Chính sách chuyên cần không tồn tại/không hoạt động.");
        if (r.ScheduledEnd is not null && r.ScheduledEnd <= r.ScheduledStart) throw new InvalidOperationException("ScheduledEnd phải sau ScheduledStart.");
        if (r.CameraId is not null)
        {
            var conflict = await db.Sessions.AnyAsync(x => x.CameraId == r.CameraId && x.Status != "CANCELLED" && x.Status != "COMPLETED" && x.ScheduledStart < (r.ScheduledEnd ?? r.ScheduledStart.AddHours(2)) && (x.ScheduledEnd ?? x.ScheduledStart.AddHours(2)) > r.ScheduledStart, ct);
            if (conflict) throw new InvalidOperationException("Camera đã được gán cho Session khác trong khoảng thời gian này.");
        }

        var s = new Session
        {
            ClassSectionId = r.ClassSectionId,
            OriginalTeacherId = classSection.TeacherId,
            RoomId = r.RoomId,
            CameraId = r.CameraId,
            VideoId = r.VideoId,
            ScheduledStart = r.ScheduledStart,
            Status = "READY",
            AlertProfile = string.IsNullOrWhiteSpace(r.AlertProfile) ? "DEFAULT" : r.AlertProfile.Trim(),
            AttendancePolicyId = r.AttendancePolicyId,
            ScheduledEnd = r.ScheduledEnd
        };
        await db.AddAsync(s, ct);
        await db.SaveChangesAsync(ct);
        foreach (var sid in roster) await db.AddAsync(new SessionStudent { SessionId = s.Id, StudentId = sid }, ct);
        await db.SaveChangesAsync(ct);
        return s;
    }

    public async Task<AnalysisJob> StartAsync(long sessionId, string callbackUrl, string callbackApiKey, CancellationToken ct)
    {
        var s = await db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId, ct) ?? throw new KeyNotFoundException("Session không tồn tại.");
        if (s.Status != "READY") throw new InvalidOperationException($"Chỉ start Session READY. Hiện tại: {s.Status}");
        var roster = await db.SessionStudents.Where(x => x.SessionId == sessionId).Select(x => x.StudentId).ToArrayAsync(ct);
        if (roster.Length == 0) throw new InvalidOperationException("Roster rỗng.");
        if (!await ai.HealthAsync(ct)) throw new AiServiceUnavailableException("AI Service không sẵn sàng.");
        var capabilities = await ai.CapabilitiesAsync(ct);
        if (!capabilities.SessionInference) throw new AiServiceUnavailableException("AI Service đang chạy nhưng model inference Session chưa được tích hợp.");

        s.Status = "STARTING";
        db.Update(s);
        await db.SaveChangesAsync(ct); // RowVersion ngăn double-start đồng thời.

        var job = new AnalysisJob { SessionId = sessionId, Status = "STARTING", CorrelationId = Guid.NewGuid().ToString("N") };
        await db.AddAsync(job, ct);
        await db.SaveChangesAsync(ct);

        try
        {
            string sourceType;
            string source;
            if (s.CameraId is not null)
            {
                source = await db.Cameras
                    .Where(x => x.Id == s.CameraId.Value && x.IsActive)
                    .Select(x => x.RtspUrl)
                    .FirstOrDefaultAsync(ct)
                    ?? throw new InvalidOperationException("Camera không tồn tại/không hoạt động.");
                sourceType = source.StartsWith("browser://", StringComparison.OrdinalIgnoreCase)
                    ? "BROWSER"
                    : "CAMERA";
            }
            else
            {
                sourceType = "VIDEO";
                source = await GetVideoReadUrlAsync(s.VideoId!.Value, ct);
            }

            AiArtifactUploadTarget? annotatedVideoUpload = null;
            if (capabilities.AnnotatedVideoOutput)
            {
                var objectKey = AnnotatedObjectKey(sessionId, job.CorrelationId);
                annotatedVideoUpload = new AiArtifactUploadTarget(
                    objectKey,
                    await storage.GetInternalWriteUrlAsync(objectKey, 21600, ct),
                    $"buoi-hoc-{sessionId}-annotations.mp4",
                    "video/mp4");
            }

            var activeTemplates = await db.StudentFaceTemplates
                .Where(x => roster.Contains(x.StudentId) && x.IsActive && x.QualityStatus == "PASS")
                .ToListAsync(ct);

            var rosterCandidates = new List<AiRosterCandidate>();
            foreach (var template in activeTemplates)
            {
                rosterCandidates.Add(new AiRosterCandidate(
                    template.StudentId,
                    await storage.GetInternalReadUrlAsync(template.TemplateRef, 21600, ct),
                    template.TemplateVersion,
                    template.ModelName,
                    template.EmbeddingDimension));
            }

            var res = await ai.StartAsync(new StartAiJobRequest(
                sessionId,
                sourceType,
                source,
                job.CorrelationId,
                roster,
                s.AlertProfile,
                callbackUrl,
                callbackApiKey,
                annotatedVideoUpload,
                rosterCandidates.ToArray()), ct);
            if (!res.Status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"AI không xác nhận RUNNING: {res.Status}");

            var now = DateTime.UtcNow;
            job.ExternalJobId = res.JobId;
            job.Status = "RUNNING";
            job.ModelVersion = res.ModelVersion;
            job.StartedAt = now;
            s.Status = "RUNNING";
            s.StartedAt = now;
            db.Update(job); db.Update(s);
            await db.SaveChangesAsync(ct);
            return job;
        }
        catch (Exception ex)
        {
            job.Status = "FAILED";
            job.ErrorMessage = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            s.Status = "READY"; // Cho phép retry start; job FAILED vẫn giữ để audit/troubleshoot.
            db.Update(job); db.Update(s);
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    private async Task<string> GetVideoReadUrlAsync(long videoId, CancellationToken ct)
    {
        var storageRef = await db.Videos.Where(x => x.Id == videoId && x.Status == "READY" && x.VideoType == "INPUT_UPLOAD").Select(x => x.StorageRef).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Video không tồn tại/chưa READY.");
        return await storage.GetInternalReadUrlAsync(storageRef, 21600, ct);
    }

    public async Task StopAsync(long sessionId, CancellationToken ct)
    {
        var s = await db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId, ct) ?? throw new KeyNotFoundException("Session không tồn tại.");
        if (s.Status != "RUNNING") throw new InvalidOperationException("Chỉ stop Session RUNNING.");
        var job = await db.AnalysisJobs.Where(x => x.SessionId == sessionId && x.Status == "RUNNING").OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Không tìm thấy AnalysisJob RUNNING.");

        s.Status = "FINALIZING";
        job.Status = "FINALIZING";
        db.Update(s); db.Update(job);
        await db.SaveChangesAsync(ct);
        await FinalizeAsync(s, job, ct);
    }

    public async Task RetryFinalizeAsync(long sessionId, CancellationToken ct)
    {
        var s = await db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId, ct) ?? throw new KeyNotFoundException("Session không tồn tại.");
        if (s.Status != "FINALIZE_FAILED") throw new InvalidOperationException("Session không cần retry finalize.");
        var job = await db.AnalysisJobs.Where(x => x.SessionId == sessionId && x.Status == "FINALIZE_FAILED").OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Không tìm thấy AnalysisJob FINALIZE_FAILED.");
        s.Status = "FINALIZING"; job.Status = "FINALIZING"; job.ErrorMessage = null;
        db.Update(s); db.Update(job); await db.SaveChangesAsync(ct);
        await FinalizeAsync(s, job, ct);
    }

    private async Task FinalizeAsync(Session s, AnalysisJob job, CancellationToken ct)
    {
        try
        {
            var stop = await ai.StopAsync(job.ExternalJobId, ct); // AI phải finalize/flush các BehaviorEvent đang mở trước khi trả COMPLETED.
            if (!stop.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"AI không xác nhận COMPLETED: {stop.Status}");
            if (stop.Artifact is not null)
                await PersistAnnotatedVideoAsync(s, job, stop.Artifact, ct);

            s.EndedAt = DateTime.UtcNow;
            await summary.RebuildSessionAsync(s.Id, ct);

            var identities = await db.StableIdentities.Where(x => x.SessionId == s.Id && x.State != "CLOSED").ToListAsync(ct);
            foreach (var identity in identities) { identity.State = "CLOSED"; db.Update(identity); }

            var substitutions = await db.SessionSubstitutions.Where(x => x.SessionId == s.Id && x.Status == "ACTIVE").ToListAsync(ct);
            foreach (var substitution in substitutions)
            {
                substitution.Status = "COMPLETED";
                substitution.CompletedAt = s.EndedAt;
                db.Update(substitution);
            }

            job.Status = "COMPLETED";
            job.EndedAt = s.EndedAt;
            job.ErrorMessage = null;
            s.Status = "COMPLETED";
            db.Update(job); db.Update(s);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            job.Status = "FINALIZE_FAILED";
            job.ErrorMessage = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            s.Status = "FINALIZE_FAILED";
            db.Update(job); db.Update(s);
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    private async Task PersistAnnotatedVideoAsync(Session session, AnalysisJob job, AiArtifactMetadata artifact, CancellationToken ct)
    {
        if (await db.Videos.AnyAsync(v => v.SessionId == session.Id && v.VideoType == "ANNOTATED_OUTPUT" && v.Status != "DELETED", ct))
            return;

        var expectedObjectKey = AnnotatedObjectKey(session.Id, job.CorrelationId);
        if (!string.Equals(artifact.ObjectKey, expectedObjectKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Artifact annotation không đúng object key đã được backend cấp.");
        if (artifact.SizeBytes <= 0 || string.IsNullOrWhiteSpace(artifact.FileName) ||
            string.IsNullOrWhiteSpace(artifact.ContentType) || !artifact.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
            artifact.Sha256.Length != 64 || artifact.Sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Metadata artifact annotation không hợp lệ.");
        if (!await storage.ExistsAsync(artifact.ObjectKey, ct))
            throw new InvalidOperationException("AI đã khai báo artifact annotation nhưng tệp không tồn tại trong kho lưu trữ.");

        await db.AddAsync(new Video
        {
            FileName = Path.GetFileName(artifact.FileName),
            StorageRef = artifact.ObjectKey,
            ContentType = artifact.ContentType,
            SizeBytes = artifact.SizeBytes,
            Sha256 = artifact.Sha256.ToLowerInvariant(),
            Status = "READY",
            VideoType = "ANNOTATED_OUTPUT",
            SessionId = session.Id,
            ParentVideoId = session.VideoId,
            IsSystemGenerated = true
        }, ct);
        await db.SaveChangesAsync(ct);
    }

    private static string AnnotatedObjectKey(long sessionId, string correlationId) =>
        $"annotations/sessions/{sessionId}/{correlationId}.mp4";

    public async Task<DashboardSnapshot> DashboardAsync(long sessionId, CancellationToken ct)
    {
        var s = await db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId, ct) ?? throw new KeyNotFoundException("Session không tồn tại.");
        var ids = await db.StableIdentities.Where(x => x.SessionId == sessionId && (x.State == "ACTIVE" || x.State == "LOST"))
            .Select(x => new { x.StableId, x.StudentId, x.IdentityStatus, x.IdentityConfidence, x.IdentityMargin, x.State, x.CurrentTrackId, x.LastSeenAt }).ToListAsync(ct);
        // Browser-camera inference is asynchronous: media timestamps (StartedAt/EndedAt)
        // can legitimately lag wall-clock time while a chunk is being processed.
        // For the LIVE dashboard, freshness must therefore be based on when the backend
        // actually RECEIVED the BEHAVIOR_SEGMENT callback, not the media EndedAt.
        var since = DateTime.UtcNow.AddSeconds(-60);
        var recentBehaviorEventIds = db.AiEventReceipts
            .Where(x => x.SessionId == sessionId
                && x.EventType == "BEHAVIOR_SEGMENT"
                && x.ReceivedAt >= since)
            .Select(x => x.ExternalEventId);

        var latestBehaviors = await db.BehaviorEvents
            .Where(x => x.SessionId == sessionId
                && recentBehaviorEventIds.Contains(x.ExternalEventId))
            .OrderByDescending(x => x.EndedAt)
            .ToListAsync(ct);
        var currentByStable = latestBehaviors.GroupBy(x => x.StableIdentityId).ToDictionary(g => g.Key, g => g.First());
        var stableEntities = await db.StableIdentities.Where(x => x.SessionId == sessionId && (x.State == "ACTIVE" || x.State == "LOST")).ToListAsync(ct);
        var identityRows = stableEntities.Select(x => new { x.Id, x.StableId, x.StudentId, x.IdentityStatus, x.IdentityConfidence, x.IdentityMargin, x.State, x.CurrentTrackId, x.LastSeenAt, CurrentBehavior = currentByStable.GetValueOrDefault(x.Id)?.Label, BehaviorProbability = currentByStable.GetValueOrDefault(x.Id)?.Probability, ObservationQuality = currentByStable.GetValueOrDefault(x.Id)?.ObservationQuality }).Cast<object>().ToList();
        var stateEvents = latestBehaviors.Where(x => x.Label is "FOCUSED" or "DISTRACTED" or "SLEEPY" or "ACTIVE").ToList();
        decimal ratio(string label) => stateEvents.Count == 0 ? 0m : (decimal)stateEvents.Count(x => x.Label == label) / stateEvents.Count;
        var alerts = await db.Alerts.Where(x => x.SessionId == sessionId).OrderByDescending(x => x.CreatedAt).Take(20).Select(x => new { x.Id, x.Type, x.StudentId, x.Status, x.Confidence, x.ObservationQuality, x.StartedAt, x.EndedAt, x.CreatedAt }).ToListAsync(ct);
        var cameraHealth = "N/A";
        if (s.CameraId is not null) cameraHealth = await db.Cameras.Where(x => x.Id == s.CameraId).Select(x => x.Status).FirstOrDefaultAsync(ct) ?? "UNKNOWN";
        var aiHealthy = await ai.HealthAsync(ct); var capabilities = aiHealthy ? await ai.CapabilitiesAsync(ct) : new AiCapabilitiesResponse(false,false,false,Array.Empty<string>());
        return new DashboardSnapshot(sessionId,s.Status,cameraHealth,aiHealthy?"ONLINE":"OFFLINE",capabilities.SessionInference,capabilities.LoadedModels,stableEntities.Count(x=>x.State=="ACTIVE"),stableEntities.Count(x=>x.StudentId!=null),stableEntities.Count(x=>x.StudentId==null),new BehaviorRatios(ratio("FOCUSED"),ratio("DISTRACTED"),ratio("SLEEPY"),ratio("ACTIVE")),stateEvents.Count(x=>x.Label=="SLEEPY"),stateEvents.Count(x=>x.Label=="ACTIVE"),identityRows,alerts.Cast<object>().ToList(),DateTime.UtcNow);
    }
}

public sealed class SummaryService(IAppDbContext db) : ISummaryService
{
        public async Task RebuildSessionAsync(long sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId, ct) ?? throw new KeyNotFoundException("Session không tồn tại.");
        if (session.StartedAt is null || session.EndedAt is null || session.EndedAt <= session.StartedAt)
            throw new InvalidOperationException("Session chưa có khoảng thời gian hoàn chỉnh để tổng hợp.");
        var sessionSeconds = Math.Max(1, (int)Math.Round((session.EndedAt.Value - session.StartedAt.Value).TotalSeconds));
        var policy = await db.AttendancePolicies.FirstOrDefaultAsync(x => x.Id == session.AttendancePolicyId, ct) ?? throw new InvalidOperationException("Session thiếu AttendancePolicy hợp lệ.");
        var students = await db.SessionStudents.Where(x => x.SessionId == sessionId).Select(x => x.StudentId).ToListAsync(ct);

        foreach (var studentId in students)
        {
            var identities = await db.StableIdentities.Where(x => x.SessionId == sessionId && x.StudentId == studentId).ToListAsync(ct);
            var identityIds = identities.Select(x => x.Id).ToArray();
            var tracks = identityIds.Length == 0 ? new List<TrackSegment>() : await db.TrackSegments.Where(x => identityIds.Contains(x.StableIdentityId)).ToListAsync(ct);
            var events = await db.BehaviorEvents.Where(x => x.SessionId == sessionId && x.StudentId == studentId).ToListAsync(ct);
            var alerts = await db.Alerts.Where(x => x.SessionId == sessionId && x.StudentId == studentId).ToListAsync(ct);

            var presentIntervals = tracks.Count > 0
                ? tracks.Select(x => (x.StartedAt, x.EndedAt)).ToList()
                : identities.Select(x => (x.FirstSeenAt, x.LastSeenAt)).ToList();
            var presentSeconds = Math.Min(sessionSeconds, UnionSeconds(presentIntervals, session.StartedAt.Value, session.EndedAt.Value));

            // PHONE_USE là tín hiệu/rule phụ có thể chồng lên HTBAM state; không dùng nó để cộng observed time.
            // OUT_OF_VIEW cũng không phải thời gian quan sát hợp lệ.
            var validEvents = events.Where(x => (x.Label == "FOCUSED" || x.Label == "DISTRACTED" || x.Label == "SLEEPY" || x.Label == "ACTIVE") && x.EndedAt > x.StartedAt).ToList();
            var observedSeconds = Math.Min(presentSeconds, UnionSeconds(validEvents.Select(x => (x.StartedAt, x.EndedAt)), session.StartedAt.Value, session.EndedAt.Value));
            var observationQuality = WeightedQuality(validEvents);
            var presenceRatio = Math.Clamp((decimal)presentSeconds / sessionSeconds, 0m, 1m);

            var att = await db.Attendance.FirstOrDefaultAsync(x => x.SessionId == sessionId && x.StudentId == studentId, ct)
                ?? new Attendance { SessionId = sessionId, StudentId = studentId };
            att.PresentSeconds = presentSeconds;
            att.ObservedSeconds = observedSeconds;
            att.PresenceRatio = presenceRatio;
            att.ObservationQuality = observationQuality;
            att.Status = presenceRatio >= policy.PresentThreshold ? "PRESENT" : presenceRatio >= policy.PartialThreshold ? "PARTIAL" : "ABSENT";
            att.Score = att.Status == "PRESENT" ? policy.PresentScore : att.Status == "PARTIAL" ? policy.PartialScore : policy.AbsentScore;
            att.PolicyVersion = policy.Version;
            if (att.Id == 0) await db.AddAsync(att, ct); else db.Update(att);

            var labelSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in new[] { "FOCUSED", "DISTRACTED", "SLEEPY", "ACTIVE" })
                labelSeconds[label] = UnionSeconds(validEvents.Where(x => x.Label == label).Select(x => (x.StartedAt, x.EndedAt)), session.StartedAt.Value, session.EndedAt.Value);
            // Tỷ lệ hành vi tính trên valid observed duration theo blueprint, không trên session duration.
            // Exclusive state segments được kiểm tra ở AI event ingestion nên tổng không bị double-count theo stable_id.
            var behaviorDenominator = Math.Max(1, observedSeconds);

            var ss = await db.StudentSessionSummaries.FirstOrDefaultAsync(x => x.SessionId == sessionId && x.StudentId == studentId, ct)
                ?? new StudentSessionSummary { SessionId = sessionId, StudentId = studentId };
            ss.FocusedRatio = (decimal)labelSeconds["FOCUSED"] / behaviorDenominator;
            ss.DistractedRatio = (decimal)labelSeconds["DISTRACTED"] / behaviorDenominator;
            ss.SleepyRatio = (decimal)labelSeconds["SLEEPY"] / behaviorDenominator;
            ss.ActiveRatio = (decimal)labelSeconds["ACTIVE"] / behaviorDenominator;
            ss.AlertCount = alerts.Count;
            ss.AlertDurationSeconds = alerts.Sum(a => a.DurationSeconds);
            ss.ObservationQuality = observationQuality;
            if (ss.Id == 0) await db.AddAsync(ss, ct); else db.Update(ss);
        }
        await db.SaveChangesAsync(ct);

        var summaries = await db.StudentSessionSummaries.Where(x => x.SessionId == sessionId).ToListAsync(ct);
        var attendance = await db.Attendance.Where(x => x.SessionId == sessionId).ToListAsync(ct);
        var weightByStudent = attendance.ToDictionary(x => x.StudentId, x => x.ObservedSeconds);
        decimal Weighted(Func<StudentSessionSummary, decimal> selector)
        {
            var weighted = summaries.Sum(x => selector(x) * weightByStudent.GetValueOrDefault(x.StudentId));
            var totalWeight = summaries.Sum(x => weightByStudent.GetValueOrDefault(x.StudentId));
            return totalWeight <= 0 ? 0 : weighted / totalWeight;
        }

        var classSummary = await db.ClassSessionSummaries.FirstOrDefaultAsync(x => x.SessionId == sessionId, ct) ?? new ClassSessionSummary { SessionId = sessionId };
        classSummary.ObservedStudentCount = attendance.Count(x => x.ObservedSeconds > 0);
        classSummary.FocusedRatio = Weighted(x => x.FocusedRatio);
        classSummary.DistractedRatio = Weighted(x => x.DistractedRatio);
        classSummary.SleepyRatio = Weighted(x => x.SleepyRatio);
        classSummary.ActiveRatio = Weighted(x => x.ActiveRatio);
        classSummary.AlertCount = summaries.Sum(x => x.AlertCount);
        var totalObserved = attendance.Sum(x => x.ObservedSeconds);
        classSummary.CameraAiQuality = totalObserved <= 0 ? 0 : attendance.Sum(x => x.ObservationQuality * x.ObservedSeconds) / totalObserved;
        if (classSummary.Id == 0) await db.AddAsync(classSummary, ct); else db.Update(classSummary);
        await db.SaveChangesAsync(ct);
    }

    private static decimal WeightedQuality(IEnumerable<BehaviorEvent> events)
    {
        decimal numerator = 0; decimal denominator = 0;
        foreach (var e in events)
        {
            var seconds = (decimal)Math.Max(0, (e.EndedAt - e.StartedAt).TotalSeconds);
            numerator += e.ObservationQuality * seconds;
            denominator += seconds;
        }
        return denominator <= 0 ? 0 : Math.Clamp(numerator / denominator, 0m, 1m);
    }

    private static int UnionSeconds(IEnumerable<(DateTime Start, DateTime End)> source, DateTime clampStart, DateTime clampEnd)
    {
        var intervals = source.Select(x => (Start: x.Start < clampStart ? clampStart : x.Start, End: x.End > clampEnd ? clampEnd : x.End))
            .Where(x => x.End > x.Start).OrderBy(x => x.Start).ToList();
        if (intervals.Count == 0) return 0;
        var total = TimeSpan.Zero; var start = intervals[0].Start; var end = intervals[0].End;
        foreach (var x in intervals.Skip(1))
        {
            if (x.Start <= end) { if (x.End > end) end = x.End; }
            else { total += end - start; start = x.Start; end = x.End; }
        }
        total += end - start;
        return Math.Max(0, (int)Math.Round(total.TotalSeconds));
    }
}
