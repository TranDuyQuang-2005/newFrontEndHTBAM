using HTBAM.Api.Support;
using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Controllers;

[ApiController, Authorize(Roles = "LECTURER,TECH_AI"), Route("api/students/{studentId:long}/face-enrollments")]
public sealed class FaceEnrollmentController(
    AppDbContext db,
    IFaceEnrollmentService enrollments,
    IObjectStorage storage,
    IAiClient ai,
    IAuditService audit,
    IConfiguration cfg) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult> List(long studentId, CancellationToken ct)
    {
        if (!await CanAccessStudent(studentId, ct)) return Forbid();
        if (!await db.StudentsSet.AnyAsync(x => x.Id == studentId, ct)) return NotFound();
        var list = await db.FaceEnrollmentsSet.AsNoTracking().Where(x => x.StudentId == studentId).OrderByDescending(x => x.StartedAt)
            .Select(x => new
            {
                x.Id,
                x.StudentId,
                x.Status,
                x.RequiredPoses,
                x.MinAcceptedImages,
                x.AcceptedImageCount,
                UploadedImageCount = db.StudentFaceImagesSet.Count(i => i.FaceEnrollmentId == x.Id && i.IsActive),
                x.StartedAt,
                x.SubmittedAt,
                x.CompletedAt,
                x.FailureReason
            }).ToListAsync(ct);
        return Ok(list);
    }

    [HttpGet("{enrollmentId:long}")]
    public async Task<ActionResult> Detail(long studentId, long enrollmentId, CancellationToken ct)
    {
        if (!await CanAccessStudent(studentId, ct)) return Forbid();
        var enrollment = await db.FaceEnrollmentsSet.AsNoTracking().FirstOrDefaultAsync(x => x.Id == enrollmentId && x.StudentId == studentId, ct);
        if (enrollment is null) return NotFound();
        var images = await db.StudentFaceImagesSet.AsNoTracking().Where(x => x.FaceEnrollmentId == enrollmentId && x.IsActive)
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.OriginalFileName, x.CapturePose, x.SourceType, x.SizeBytes, x.QualityStatus, x.QualityScore, x.QualityReason, x.CreatedAt }).ToListAsync(ct);
        var template = await db.StudentFaceTemplatesSet.AsNoTracking().Where(x => x.FaceEnrollmentId == enrollmentId && x.IsActive)
            .Select(x => new { x.Id, x.ModelName, x.TemplateVersion, x.EmbeddingDimension, x.QualityScore, x.PoseCoverage, x.SourceImageCount, x.CreatedAt }).FirstOrDefaultAsync(ct);
        return Ok(new { enrollment, images, template });
    }

    [HttpGet("status")]
    public async Task<ActionResult> Status(long studentId, CancellationToken ct)
    {
        if (!await CanAccessStudent(studentId, ct)) return Forbid();
        if (!await db.StudentsSet.AnyAsync(x => x.Id == studentId, ct)) return NotFound();
        var latest = await db.FaceEnrollmentsSet.AsNoTracking().Where(x => x.StudentId == studentId).OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(ct);
        var ready = await db.StudentFaceTemplatesSet.AsNoTracking().AnyAsync(x => x.StudentId == studentId && x.IsActive && x.QualityStatus == "PASS", ct);
        return Ok(new { studentId, enrollmentReady = ready, latestStatus = latest?.Status ?? "NOT_ENROLLED", latestEnrollmentId = latest?.Id });
    }

    [HttpPost]
    public async Task<ActionResult> Start(long studentId, CancellationToken ct)
    {
        if (!await CanAccessStudent(studentId, ct)) return Forbid();
        var x = await enrollments.StartAsync(studentId, ct);
        await audit.WriteAsync(UserContext.Id(User), "FACE_ENROLLMENT_START", "FaceEnrollment", x.Id.ToString(), new { studentId }, ct);
        return CreatedAtAction(nameof(Detail), new { studentId, enrollmentId = x.Id }, new { x.Id, x.StudentId, x.Status, x.RequiredPoses, x.MinAcceptedImages });
    }

    [HttpPost("{enrollmentId:long}/images")]
    [RequestFormLimits(MultipartBodyLengthLimit = 8 * 1024 * 1024)]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<ActionResult> UploadImage(long studentId, long enrollmentId, IFormFile file, [FromForm] string capturePose, [FromForm] string sourceType = "UPLOAD", CancellationToken ct = default)
    {
        if (!await CanAccessStudent(studentId, ct)) return Forbid();
        if (file is null || file.Length == 0) return BadRequest("Thiếu file ảnh.");
        await using var stream = file.OpenReadStream();
        var image = await enrollments.UploadImageAsync(studentId, enrollmentId, file.FileName, file.ContentType, file.Length, capturePose, sourceType, stream, ct);
        await audit.WriteAsync(UserContext.Id(User), "FACE_IMAGE_UPLOAD", "StudentFaceImage", image.Id.ToString(), new { studentId, enrollmentId, image.CapturePose, image.SizeBytes }, ct);
        return Ok(new { image.Id, image.FaceEnrollmentId, image.OriginalFileName, image.CapturePose, image.SourceType, image.SizeBytes, image.QualityStatus, image.CreatedAt });
    }

    [HttpGet("{enrollmentId:long}/images/{imageId:long}/preview-url")]
    public async Task<ActionResult> Preview(long studentId, long enrollmentId, long imageId, CancellationToken ct)
    {
        if (!await CanAccessStudent(studentId, ct)) return Forbid();
        var image = await db.StudentFaceImagesSet.AsNoTracking().FirstOrDefaultAsync(x => x.Id == imageId && x.FaceEnrollmentId == enrollmentId && x.StudentId == studentId && x.IsActive, ct);
        if (image is null) return NotFound();
        var url = await storage.GetReadUrlAsync(image.ObjectKey, 300, ct);
        return Ok(new { url, expiresInSeconds = 300 });
    }

    [HttpDelete("{enrollmentId:long}/images/{imageId:long}")]
    public async Task<ActionResult> DeleteImage(long studentId, long enrollmentId, long imageId, CancellationToken ct)
    {
        if (!await CanAccessStudent(studentId, ct)) return Forbid();
        await enrollments.DeleteImageAsync(studentId, enrollmentId, imageId, ct);
        await audit.WriteAsync(UserContext.Id(User), "FACE_IMAGE_DELETE", "StudentFaceImage", imageId.ToString(), new { studentId, enrollmentId }, ct);
        return NoContent();
    }

    [HttpPost("{enrollmentId:long}/submit")]
    public async Task<ActionResult> Submit(long studentId, long enrollmentId, CancellationToken ct)
    {
        if (!await CanAccessStudent(studentId, ct)) return Forbid();
        var x = await enrollments.SubmitAsync(studentId, enrollmentId, ct);
        await audit.WriteAsync(UserContext.Id(User), "FACE_ENROLLMENT_SUBMIT", "FaceEnrollment", x.Id.ToString(), new { studentId, x.Status }, ct);
        return Ok(new { x.Id, x.Status, message = "Ảnh đã lưu an toàn. Chờ AI quality/ArcFace; không tạo embedding giả." });
    }

    [HttpPost("{enrollmentId:long}/process")]
    public async Task<ActionResult> Process(long studentId, long enrollmentId, CancellationToken ct)
    {
        if (!await CanAccessStudent(studentId, ct)) return Forbid();

        var capabilities = await ai.CapabilitiesAsync(ct);
        if (!capabilities.FaceEnrollment)
            return Conflict(new
            {
                code = "FACE_MODEL_NOT_READY",
                message = "Dịch vụ AI đăng ký khuôn mặt chưa sẵn sàng; dữ liệu vẫn được giữ ở trạng thái chờ xử lý.",
                loadedModels = capabilities.LoadedModels
            });

        var baseUrl = cfg["PublicBaseUrl"] ?? $"{Request.Scheme}://{Request.Host}";
        var key = cfg["AiService:CallbackApiKey"] ?? "";

        var manifest = await enrollments.BuildManifestAsync(
            studentId,
            enrollmentId,
            $"{baseUrl.TrimEnd('/')}/api/ai/face-enrollments/{enrollmentId}/result",
            key,
            ct);

        var entity = await db.FaceEnrollmentsSet.FirstAsync(
            x => x.Id == enrollmentId && x.StudentId == studentId, ct);
        entity.Status = "PROCESSING";
        entity.FailureReason = null;
        db.Update(entity);
        await db.SaveChangesAsync(ct);

        try
        {
            var response = await ai.StartFaceEnrollmentAsync(manifest, ct);
            await audit.WriteAsync(
                UserContext.Id(User),
                "FACE_ENROLLMENT_PROCESS",
                "FaceEnrollment",
                enrollmentId.ToString(),
                new { response.JobId, response.Status },
                ct);
            return Ok(response);
        }
        catch
        {
            var current = await db.FaceEnrollmentsSet.FirstAsync(
                x => x.Id == enrollmentId && x.StudentId == studentId, ct);
            if (current.Status == "PROCESSING")
            {
                current.Status = "PENDING_AI";
                current.FailureReason = "Không khởi động được AI Face Enrollment; có thể thử lại.";
                db.Update(current);
                await db.SaveChangesAsync(ct);
            }
            throw;
        }
    }

    [HttpPost("{enrollmentId:long}/cancel")]
    public async Task<ActionResult> Cancel(long studentId, long enrollmentId, CancellationToken ct)
    {
        if (!await CanAccessStudent(studentId, ct)) return Forbid();
        await enrollments.CancelAsync(studentId, enrollmentId, ct);
        await audit.WriteAsync(UserContext.Id(User), "FACE_ENROLLMENT_CANCEL", "FaceEnrollment", enrollmentId.ToString(), new { studentId }, ct);
        return NoContent();
    }

    private Task<bool> CanAccessStudent(long studentId, CancellationToken ct) =>
        User.IsInRole("TECH_AI") ? Task.FromResult(true) : AccessScope.CanManageStudentAsync(db, User, studentId, ct);
}
