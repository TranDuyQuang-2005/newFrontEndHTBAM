using HTBAM.Api.Support;
using HTBAM.Application.Interfaces;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Controllers;

[ApiController, Authorize(Roles = "ADMIN,LECTURER,TECH_AI"), Route("api/alerts")]
public sealed class AlertsController(AppDbContext db, IAuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult> List(
        [FromQuery] long? sessionId, [FromQuery] long? studentId, [FromQuery] string? status,
        [FromQuery] string? type, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var query = db.AlertsSet.AsNoTracking().AsQueryable();
        if (sessionId is not null)
        {
            if (!await AccessScope.CanAccessSessionAsync(db, User, sessionId.Value, ct)) return Forbid();
            query = query.Where(x => x.SessionId == sessionId);
        }
        else
        {
            var teacherId = await AccessScope.TeacherIdAsync(db, User, ct);
            var allowedSessionIds = AccessScope.Sessions(db, User, teacherId).Select(s => s.Id);
            query = query.Where(x => allowedSessionIds.Contains(x.SessionId));
        }

        if (studentId is not null) query = query.Where(x => x.StudentId == studentId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(x => x.Type == type.Trim().ToUpperInvariant());
        if (from is not null) query = query.Where(x => x.StartedAt >= from);
        if (to is not null) query = query.Where(x => x.StartedAt < to);
        var rows = await query.OrderByDescending(x => x.CreatedAt).Take(2000).ToListAsync(ct);
        var possibleBehaviorIds = rows.Select(x => ParentEventId(x.ExternalEventId)).Where(x => x is not null).Cast<string>().Distinct().ToArray();
        var behaviorIds = possibleBehaviorIds.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await db.BehaviorEventsSet.AsNoTracking().Where(x => possibleBehaviorIds.Contains(x.ExternalEventId)).Select(x => x.ExternalEventId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var stableIds = rows.Select(x => x.StableIdentityId).Distinct().ToArray();
        var trackIds = await db.StableIdentitiesSet.AsNoTracking().Where(x => stableIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.CurrentTrackId, ct);
        return Ok(rows.Select(x =>
        {
            var parent = ParentEventId(x.ExternalEventId);
            var behaviorEventId = parent is not null && behaviorIds.Contains(parent) ? parent : null;
            return new
            {
                x.Id, x.ExternalEventId, x.SessionId, x.StableIdentityId, x.StudentId, x.Type,
                Severity = Severity(x.Type, x.Confidence),
                Source = behaviorEventId is null ? "AI_DIRECT" : "RULE_ENGINE",
                BehaviorEventId = behaviorEventId,
                TrackId = trackIds.GetValueOrDefault(x.StableIdentityId),
                x.StartedAt, x.EndedAt, x.DurationSeconds, x.Confidence, x.ObservationQuality,
                x.Status, x.LecturerNote, x.EvidenceRef, x.CreatedAt, x.AcknowledgedAt, x.ClosedAt
            };
        }));
    }

    private static string? ParentEventId(string externalEventId)
    {
        var split = externalEventId.LastIndexOf(':');
        return split <= 0 ? null : externalEventId[..split];
    }

    private static string Severity(string type, decimal confidence) =>
        type.Contains("SLEEP", StringComparison.OrdinalIgnoreCase) || confidence >= .9m ? "CRITICAL" :
        confidence >= .65m ? "WARNING" : "INFO";

    public record NoteReq(string? Note);

    [HttpPost("{id:long}/ack"), Authorize(Roles = "LECTURER")]
    public Task<ActionResult> Ack(long id, NoteReq request, CancellationToken ct) => Transition(id, "ACK", request.Note, ct);

    [HttpPost("{id:long}/close"), Authorize(Roles = "LECTURER")]
    public Task<ActionResult> Close(long id, NoteReq request, CancellationToken ct) => Transition(id, "CLOSED", request.Note, ct);

    [HttpPost("{id:long}/reopen"), Authorize(Roles = "LECTURER")]
    public Task<ActionResult> Reopen(long id, NoteReq request, CancellationToken ct) => Transition(id, "OPEN", request.Note, ct);

    private async Task<ActionResult> Transition(long id, string target, string? note, CancellationToken ct)
    {
        var alert = await db.AlertsSet.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (alert is null) return NotFound();
        if (!await AccessScope.CanOperateSessionAsync(db, User, alert.SessionId, ct)) return Forbid();
        alert.Status = target;
        alert.LecturerNote = string.IsNullOrWhiteSpace(note) ? alert.LecturerNote : note.Trim();
        if (target == "ACK") alert.AcknowledgedAt = DateTime.UtcNow;
        if (target == "CLOSED") alert.ClosedAt = DateTime.UtcNow;
        if (target == "OPEN") { alert.ClosedAt = null; alert.AcknowledgedAt = null; }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), $"ALERT_{target}", "Alert", id.ToString(), new { alert.SessionId, alert.StudentId }, ct);
        return NoContent();
    }

    [HttpGet("rules")]
    public async Task<ActionResult> Rules(CancellationToken ct) =>
        Ok(await db.AlertRulesSet.AsNoTracking().OrderBy(x => x.Code).ToListAsync(ct));

    public record RuleReq(
        string? Code, string? Name, string? BehaviorLabel, int? MinDurationSeconds,
        decimal? MinConfidence, decimal? MinObservationQuality, bool? Enabled, string? Version);

    [HttpPost("rules"), Authorize(Roles = "ADMIN")]
    public async Task<ActionResult> AddRule(RuleReq request, CancellationToken ct)
    {
        var errors = RuleErrors(request);
        if (errors.Count > 0) return ValidationError(errors);
        var code = request.Code!.Trim().ToUpperInvariant();
        if (await db.AlertRulesSet.AnyAsync(x => x.Code.ToUpper() == code, ct))
            return Conflict(ApiValidation.Duplicate("ALERT_RULE_CODE_DUPLICATE", "code", $"Mã luật cảnh báo {code} đã tồn tại."));

        var rule = MapRule(new AlertRule(), request);
        db.AlertRulesSet.Add(rule);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), "ALERT_RULE_CREATE", "AlertRule", rule.Id.ToString(), new { rule.Code }, ct);
        return Ok(rule);
    }

    [HttpPut("rules/{id:long}"), Authorize(Roles = "ADMIN")]
    public async Task<ActionResult> UpdateRule(long id, RuleReq request, CancellationToken ct)
    {
        var rule = await db.AlertRulesSet.FindAsync([id], ct);
        if (rule is null) return NotFound(new { message = "Luật cảnh báo không tồn tại." });
        var errors = RuleErrors(request);
        if (errors.Count > 0) return ValidationError(errors);
        var code = request.Code!.Trim().ToUpperInvariant();
        if (await db.AlertRulesSet.AnyAsync(x => x.Id != id && x.Code.ToUpper() == code, ct))
            return Conflict(ApiValidation.Duplicate("ALERT_RULE_CODE_DUPLICATE", "code", $"Mã luật cảnh báo {code} đã tồn tại."));

        MapRule(rule, request);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), "ALERT_RULE_UPDATE", "AlertRule", id.ToString(), new { rule.Code }, ct);
        return NoContent();
    }

    private BadRequestObjectResult ValidationError(Dictionary<string, string[]> errors) =>
        BadRequest(new { message = "Dữ liệu luật cảnh báo chưa hợp lệ.", errors });

    private static AlertRule MapRule(AlertRule rule, RuleReq request)
    {
        rule.Code = request.Code!.Trim().ToUpperInvariant();
        rule.Name = request.Name!.Trim();
        rule.BehaviorLabel = request.BehaviorLabel!.Trim().ToUpperInvariant();
        rule.MinDurationSeconds = request.MinDurationSeconds!.Value;
        rule.MinConfidence = request.MinConfidence!.Value;
        rule.MinObservationQuality = request.MinObservationQuality!.Value;
        rule.Enabled = request.Enabled!.Value;
        rule.Version = request.Version!.Trim();
        return rule;
    }

    private static Dictionary<string, string[]> RuleErrors(RuleReq request)
    {
        var errors = new Dictionary<string, string[]>();
        var code = request.Code?.Trim() ?? "";
        var name = request.Name?.Trim() ?? "";
        var behavior = request.BehaviorLabel?.Trim().ToUpperInvariant() ?? "";
        var version = request.Version?.Trim() ?? "";
        var labels = new HashSet<string>(StringComparer.Ordinal) { "DISTRACTED", "SLEEPY", "PHONE_USE", "OUT_OF_VIEW" };

        if (code.Length == 0) errors["code"] = ["Mã luật cảnh báo là bắt buộc."];
        else if (code.Length > 64) errors["code"] = ["Mã luật cảnh báo không được vượt quá 64 ký tự."];
        if (name.Length == 0) errors["name"] = ["Tên luật cảnh báo là bắt buộc."];
        else if (name.Length > 256) errors["name"] = ["Tên luật cảnh báo không được vượt quá 256 ký tự."];
        if (behavior.Length == 0) errors["behaviorLabel"] = ["Loại hành vi là bắt buộc."];
        else if (!labels.Contains(behavior)) errors["behaviorLabel"] = ["Loại hành vi không được hỗ trợ."];
        if (request.MinDurationSeconds is null) errors["minDurationSeconds"] = ["Thời lượng là bắt buộc."];
        else if (request.MinDurationSeconds <= 0) errors["minDurationSeconds"] = ["Thời lượng phải lớn hơn 0."];
        if (request.MinConfidence is null) errors["minConfidence"] = ["Độ tin cậy là bắt buộc."];
        else if (request.MinConfidence < 0 || request.MinConfidence > 1) errors["minConfidence"] = ["Độ tin cậy phải nằm trong khoảng 0 đến 1."];
        if (request.MinObservationQuality is null) errors["minObservationQuality"] = ["Chất lượng quan sát là bắt buộc."];
        else if (request.MinObservationQuality < 0 || request.MinObservationQuality > 1) errors["minObservationQuality"] = ["Chất lượng quan sát phải nằm trong khoảng 0 đến 1."];
        if (request.Enabled is null) errors["enabled"] = ["Trạng thái kích hoạt là bắt buộc."];
        if (version.Length == 0) errors["version"] = ["Phiên bản là bắt buộc."];
        else if (version.Length > 32) errors["version"] = ["Phiên bản không được vượt quá 32 ký tự."];
        return errors;
    }
}
