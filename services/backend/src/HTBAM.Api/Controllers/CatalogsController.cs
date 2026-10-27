using System.Net.Sockets;
using System.Text;
using HTBAM.Api.Support;
using HTBAM.Application.Interfaces;
using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace HTBAM.Api.Controllers;

[ApiController, Authorize, Route("api/catalogs")]
public sealed class CatalogsController(AppDbContext db, IAuditService audit) : ControllerBase
{
    public record FacultyReq(string? Code, string Name, string? Note, bool IsActive = true, string? InitialDepartmentName = null, long? InitialHeadTeacherId = null); public record DepartmentReq(string? Code, string Name, long FacultyId, string? Note, bool IsActive = true); public record StudentClassReq(string? Code, string Name, long FacultyId, int? StartYear, string? Note, bool IsActive = true, long? DepartmentId = null); public record CourseReq(string? Code, string Name, long? DepartmentId, int Credits, string? Note, bool IsActive = true, string? StartAcademicYear = null, string? StartSemester = null); public record TeacherReq(string? TeacherCode, string FullName, string Email, long? UserId, long? DepartmentId, string? Note, bool IsActive = true); public record ClassReq(string? Code, string Name, long CourseId, long TeacherId, string Semester, string AcademicYear, string? Note, bool IsActive = true); public record RoomReq(string? Code, string Name, string? Location, int Capacity, string? Note, bool IsActive = true, string RoomType = "UNKNOWN", string? Campus = null, string? Building = null, string? RoomNumber = null); public record CameraReq(string? Code, string Name, long RoomId, string RtspUrl, string? Note, bool IsActive = true);

    [HttpGet("faculties")] public async Task<ActionResult> Faculties(CancellationToken ct) { var ids = await VisibleFacultyIds(ct); return Ok(await db.FacultiesSet.AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Code).ToListAsync(ct)); }
    [HttpPost("faculties"), Authorize(Roles = "ADMIN")]
    public Task<ActionResult> AddFaculty(FacultyReq r, CancellationToken ct) =>
        BusinessTransaction.Run<ActionResult>(db, "HTBAM:academic-scheduling", async () =>
        {
            if (!User.IsInRole("ADMIN")) return Forbid();
            if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 256) return BadRequest("Tên khoa cần 1–256 ký tự.");
            if (string.IsNullOrWhiteSpace(r.InitialDepartmentName) || r.InitialDepartmentName.Trim().Length > 256 || r.InitialHeadTeacherId is not > 0)
                return BadRequest("Cần tên bộ môn đầu tiên và giảng viên hiện có làm Trưởng khoa để khởi tạo khoa.");
            var head = await FacultyInitialization.EligibleTeachers(db).FirstOrDefaultAsync(t => t.Id == r.InitialHeadTeacherId, ct);
            if (head is null) return BadRequest("Trưởng khoa đầu tiên phải là giảng viên hoạt động, có tài khoản giảng viên, chưa có phân công giảng dạy hoặc chức vụ còn hiệu lực.");
            var previousDepartmentId = head.DepartmentId;
            var faculty = new Faculty { Code = await BusinessCodes.Next(db, "KHOA-" + CatalogCodes.DepartmentBase(System.Text.RegularExpressions.Regex.Replace(r.Name.Trim(), "^Khoa\\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase))[3..] + "-", 2, db.FacultiesSet.Select(x => x.Code), ct, true), Name = r.Name.Trim(), Note = CleanNote(r.Note), IsActive = r.IsActive };
            var department = new Department { Code = await CatalogNumbering.Department(db, r.InitialDepartmentName, ct), Name = r.InitialDepartmentName.Trim(), Faculty = faculty };
            db.DepartmentsSet.Add(department); await db.SaveChangesAsync(ct);
            head.DepartmentId = department.Id;
            var assignment = new ManagementAssignment { TeacherId = head.Id, PositionType = "FACULTY_HEAD", FacultyId = faculty.Id,
                EffectiveFrom = DateTime.UtcNow, AssignedByUserId = UserContext.Id(User), Note = "Bổ nhiệm Trưởng khoa đầu tiên khi khởi tạo khoa." };
            db.ManagementAssignmentsSet.Add(assignment); await db.SaveChangesAsync(ct);
            await audit.WriteAsync(UserContext.Id(User), "FACULTY_INITIALIZE", "Catalog", faculty.Id.ToString(),
                new { faculty.Code, DepartmentId = department.Id, HeadTeacherId = head.Id, PreviousDepartmentId = previousDepartmentId, AssignmentId = assignment.Id }, ct);
            return Ok(faculty);
        }, ct);
    [HttpPut("faculties/{id:long}"), Authorize(Roles = "LECTURER")] public async Task<ActionResult> UpdateFaculty(long id, FacultyReq r, CancellationToken ct) { if (!await AccessScope.CanManageFacultyAsync(db, User, id, ct)) return Forbid(); var x = await db.FacultiesSet.FindAsync([id], ct); if (x is null) return NotFound(); var code = x.Code; if (await db.FacultiesSet.AnyAsync(y => y.Id != id && y.Code.ToUpper() == ApiValidation.Key(code), ct)) return Duplicate("FACULTY_CODE_DUPLICATE", "code", $"Mã khoa {code} đã tồn tại."); x.Name = r.Name.Trim(); x.Note = CleanNote(r.Note); x.IsActive = r.IsActive; await db.SaveChangesAsync(ct); await Audit("FACULTY_UPDATE", id, x.Code, ct); return NoContent(); }

    [HttpGet("departments")] public async Task<ActionResult> Departments(CancellationToken ct) { var ids = await VisibleDepartmentIds(ct); return Ok(await db.DepartmentsSet.AsNoTracking().Include(x => x.Faculty).Where(x => ids.Contains(x.Id)).OrderBy(x => x.Code).Select(x => new { x.Id, x.Code, x.Name, x.FacultyId, Faculty = x.Faculty.Name, x.Note, x.IsActive }).ToListAsync(ct)); }
    [HttpPost("departments"), Authorize(Roles = "LECTURER")]
    public Task<ActionResult> AddDepartment(DepartmentReq r, CancellationToken ct) => BusinessTransaction.Run<ActionResult>(db, CatalogNumbering.Resource, async () =>
    {
        if (!await AccessScope.CanManageFacultyAsync(db, User, r.FacultyId, ct)) return Forbid();
        if (!await db.FacultiesSet.AnyAsync(x=>x.Id==r.FacultyId&&x.IsActive,ct)) return BadRequest("Khoa không hợp lệ.");
        var code=await CatalogNumbering.Department(db,r.Name,ct);
        var item=new Department{Code=code,Name=r.Name.Trim(),FacultyId=r.FacultyId,Note=CleanNote(r.Note),IsActive=r.IsActive};
        db.Add(item);await db.SaveChangesAsync(ct);await Audit("DEPARTMENT_CREATE",item.Id,item.Code,ct);return Ok(item);
    },ct);
    [HttpPut("departments/{id:long}"), Authorize(Roles = "LECTURER")] public async Task<ActionResult> UpdateDepartment(long id, DepartmentReq r, CancellationToken ct) { if (!await AccessScope.CanManageDepartmentAsync(db, User, id, ct) || !await AccessScope.CanManageFacultyAsync(db, User, r.FacultyId, ct)) return Forbid(); var x = await db.DepartmentsSet.FindAsync([id], ct); if (x is null) return NotFound(); if (!await db.FacultiesSet.AnyAsync(y => y.Id == r.FacultyId && y.IsActive, ct)) return BadRequest("Khoa không hợp lệ."); var code = x.Code; if (await db.DepartmentsSet.AnyAsync(y => y.Id != id && y.Code.ToUpper() == ApiValidation.Key(code), ct)) return Duplicate("DEPARTMENT_CODE_DUPLICATE", "code", $"Mã bộ môn {code} đã tồn tại."); x.Name = r.Name.Trim(); x.FacultyId = r.FacultyId; x.Note = CleanNote(r.Note); x.IsActive = r.IsActive; await db.SaveChangesAsync(ct); await Audit("DEPARTMENT_UPDATE", id, x.Code, ct); return NoContent(); }

    [HttpGet("student-classes")] public async Task<ActionResult> StudentClasses(CancellationToken ct) { var facultyIds = await VisibleFacultyIds(ct); return Ok(await db.StudentClassesSet.AsNoTracking().Include(x => x.Faculty).Where(x => facultyIds.Contains(x.FacultyId)).OrderBy(x => x.Code).Select(x => new { x.Id, x.Code, x.Name, x.FacultyId, Faculty = x.Faculty.Name, x.StartYear, x.Note, x.IsActive }).ToListAsync(ct)); }
    [HttpPost("student-classes"), Authorize(Roles = "LECTURER")] public Task<ActionResult> AddStudentClass(StudentClassReq r, CancellationToken ct) => BusinessTransaction.Run<ActionResult>(db, BusinessCodes.Resource, async () => { if (!await AccessScope.CanManageFacultyAsync(db, User, r.FacultyId, ct)) return Forbid(); if (!await db.FacultiesSet.AnyAsync(x => x.Id == r.FacultyId && x.IsActive, ct)) return BadRequest("Khoa không hợp lệ."); if (r.StartYear is null or < 1000 or > 9999) return BadRequest("Vui lòng nhập khóa gồm bốn chữ số trước khi tạo lớp sinh hoạt.");
        var department = await db.DepartmentsSet.SingleOrDefaultAsync(x => x.Id == r.DepartmentId && x.FacultyId == r.FacultyId && x.IsActive, ct);
        if (department is null || !CatalogCodes.ValidDepartment(department.Code)) return BadRequest("Vui lòng chọn Bộ môn có mã hợp lệ thuộc Khoa để xác định chuyên ngành lớp sinh hoạt.");
        var code = await BusinessCodes.Next(db, $"LSH-{r.StartYear}-{department.Code[3..]}-", 2, db.StudentClassesSet.Select(x => x.Code), ct); if (await db.StudentClassesSet.AnyAsync(x => x.Code.ToUpper() == ApiValidation.Key(code), ct)) return Duplicate("STUDENT_CLASS_CODE_DUPLICATE", "code", $"Mã lớp sinh hoạt {code} đã tồn tại."); var x = new StudentClass { Code = code, Name = r.Name.Trim(), FacultyId = r.FacultyId, StartYear = r.StartYear, Note = CleanNote(r.Note), IsActive = r.IsActive }; db.Add(x); await db.SaveChangesAsync(ct); await Audit("STUDENT_CLASS_CREATE", x.Id, x.Code, ct); return Ok(x); }, ct);
    [HttpPut("student-classes/{id:long}"), Authorize(Roles = "LECTURER")] public async Task<ActionResult> UpdateStudentClass(long id, StudentClassReq r, CancellationToken ct) { if (!await AccessScope.CanManageStudentClassAsync(db, User, id, ct) || !await AccessScope.CanManageFacultyAsync(db, User, r.FacultyId, ct)) return Forbid(); var x = await db.StudentClassesSet.FindAsync([id], ct); if (x is null) return NotFound(); var code = x.Code; if (await db.StudentClassesSet.AnyAsync(y => y.Id != id && y.Code.ToUpper() == ApiValidation.Key(code), ct)) return Duplicate("STUDENT_CLASS_CODE_DUPLICATE", "code", $"Mã lớp sinh hoạt {code} đã tồn tại."); x.Name = r.Name.Trim(); x.FacultyId = r.FacultyId; x.StartYear = r.StartYear; x.Note = CleanNote(r.Note); x.IsActive = r.IsActive; await db.SaveChangesAsync(ct); await Audit("STUDENT_CLASS_UPDATE", id, x.Code, ct); return NoContent(); }

    [HttpGet("courses")] public async Task<ActionResult> Courses(CancellationToken ct) { var departments = await VisibleDepartmentIds(ct); return Ok(await db.Courses.AsNoTracking().Where(x => x.DepartmentId != null && departments.Contains(x.DepartmentId.Value)).OrderBy(x => x.Code).ToListAsync(ct)); }
    [HttpGet("courses/next-code"), Authorize(Roles = "LECTURER")]
    public Task<ActionResult> NextCourseCode([FromQuery] long departmentId,CancellationToken ct) => BusinessTransaction.Run<ActionResult>(db,CatalogNumbering.Resource,async()=>
    {
        if(departmentId<=0)return BadRequest("Vui lòng chọn Bộ môn.");
        if(!await AccessScope.CanManageDepartmentDataAsync(db,User,departmentId,ct))return Forbid();
        var department=await db.DepartmentsSet.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==departmentId&&x.IsActive&&x.Faculty.IsActive,ct);
        if(department is null)return BadRequest("Bộ môn không hợp lệ.");
        return Ok(new{code=await CatalogNumbering.Course(db,department,false,ct),departmentId});
    },ct);
    [HttpPost("courses"), Authorize(Roles = "LECTURER")]
    public Task<ActionResult> AddCourse(CourseReq r,CancellationToken ct) => BusinessTransaction.Run<ActionResult>(db,CatalogNumbering.Resource,async()=>
    {
        if(r.DepartmentId is not >0)return BadRequest("Vui lòng chọn Bộ môn.");
        if(!await AccessScope.CanManageDepartmentDataAsync(db,User,r.DepartmentId.Value,ct))return Forbid();
        var department=await db.DepartmentsSet.SingleOrDefaultAsync(x=>x.Id==r.DepartmentId&&x.IsActive&&x.Faculty.IsActive,ct);
        if(department is null)return BadRequest("Bộ môn không hợp lệ.");
        if(string.IsNullOrWhiteSpace(r.Name)||r.Name.Trim().Length>256)return BadRequest("Tên môn học cần 1–256 ký tự.");
        if(r.Credits is <1 or >20)return BadRequest("Số tín chỉ không hợp lệ.");
        var period=CourseStartPeriod.Parse(r.StartAcademicYear,r.StartSemester);
        var code=await CatalogNumbering.Course(db,department,true,ct);
        var item=new Course{StartYear=period.StartYear,EndYear=period.EndYear,StartSemester=period.Semester,Code=code,Name=r.Name.Trim(),DepartmentId=department.Id,Credits=r.Credits,Note=CleanNote(r.Note),IsActive=r.IsActive};
        db.Add(item);await db.SaveChangesAsync(ct);await Audit("COURSE_CREATE",item.Id,item.Code,ct);return Ok(item);
    },ct);
    [HttpPut("courses/{id:long}"), Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> UpdateCourse(long id,CourseReq r,CancellationToken ct)
    {
        var item=await db.Courses.FindAsync([id],ct);if(item is null)return NotFound();
        if(item.DepartmentId is null||!await AccessScope.CanManageDepartmentDataAsync(db,User,item.DepartmentId.Value,ct))return Forbid();
        if(r.DepartmentId!=item.DepartmentId)return Conflict("Bộ môn được giữ nguyên khi sửa môn học. Cần quy trình chuyển riêng để bảo toàn mã và ngữ cảnh lịch sử.");
        if(string.IsNullOrWhiteSpace(r.Name)||r.Name.Trim().Length>256)return BadRequest("Tên môn học cần 1–256 ký tự.");
        if(r.Credits is <1 or >20)return BadRequest("Số tín chỉ không hợp lệ.");
        if(r.Credits!=item.Credits&&await db.ClassSections.AnyAsync(c=>c.CourseId==id,ct))return Conflict("Môn đã có lớp học phần: không đổi tín chỉ làm thay đổi ngữ cảnh lịch sử.");
        var period=CourseStartPeriod.Parse(r.StartAcademicYear,r.StartSemester);
        item.StartYear=period.StartYear;item.EndYear=period.EndYear;item.StartSemester=period.Semester;
        item.Name=r.Name.Trim();item.Credits=r.Credits;item.Note=CleanNote(r.Note);item.IsActive=r.IsActive;
        await db.SaveChangesAsync(ct);await Audit("COURSE_UPDATE",id,item.Code,ct);return NoContent();
    }

    [HttpGet("teachers")] public async Task<ActionResult> Teachers(CancellationToken ct) { var tid = await AccessScope.TeacherIdAsync(db, User, ct); var departments = await VisibleDepartmentIds(ct); return Ok(await db.Teachers.AsNoTracking().Where(x => x.Id == tid || (x.DepartmentId != null && departments.Contains(x.DepartmentId.Value))).OrderBy(x => x.TeacherCode).ToListAsync(ct)); }
    [HttpGet("teachers/next-code"), Authorize(Roles="LECTURER")]
    public Task<ActionResult> NextTeacherCode([FromQuery] long departmentId,CancellationToken ct)=>BusinessTransaction.Run<ActionResult>(db,CatalogNumbering.Resource,async()=>
    {
        if(User.IsInRole("ADMIN")||!await AccessScope.CanCreateTeacher(db,User,departmentId,ct))return Forbid();
        var department=await db.DepartmentsSet.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==departmentId&&x.IsActive&&x.Faculty.IsActive,ct);
        if(department is null)return BadRequest("Bộ môn không hợp lệ.");
        return Ok(new{code=await LecturerCodeAllocator.Next(db,department,false,ct),departmentId});
    },ct);
    [HttpPost("teachers"), Authorize(Roles="LECTURER")]
    public Task<ActionResult> AddTeacher(TeacherReq r,CancellationToken ct)=>BusinessTransaction.Run<ActionResult>(db,CatalogNumbering.Resource,async()=>
    {
        if(User.IsInRole("ADMIN"))return Forbid();
        if(r.DepartmentId is not >0)return BadRequest("Vui lòng chọn Bộ môn trước khi thêm giảng viên.");
        if(!await AccessScope.CanCreateTeacher(db,User,r.DepartmentId.Value,ct))return Forbid();
        var err=await TeacherValid(r,null,ct);if(err!=null)return err;
        var department=await db.DepartmentsSet.SingleAsync(x=>x.Id==r.DepartmentId,ct);
        var item=new Teacher{TeacherCode=await LecturerCodeAllocator.Next(db,department,true,ct),FullName=r.FullName.Trim(),Email=r.Email.Trim(),UserId=r.UserId,DepartmentId=department.Id,Note=CleanNote(r.Note),IsActive=r.IsActive};
        db.Add(item);await db.SaveChangesAsync(ct);await Audit("TEACHER_CREATE",item.Id,item.TeacherCode,ct);return Ok(item);
    },ct);
    [HttpPut("teachers/{id:long}"), Authorize(Roles="LECTURER")]
    public Task<ActionResult> UpdateTeacher(long id,TeacherReq r,CancellationToken ct)=>BusinessTransaction.Run<ActionResult>(db,CatalogNumbering.Resource,async()=>
    {
        var item=await db.Teachers.FindAsync([id],ct);if(item is null)return NotFound();
        if(item.DepartmentId is null||!await AccessScope.CanManageDepartmentDataAsync(db,User,item.DepartmentId.Value,ct)||r.DepartmentId is null||!await AccessScope.CanManageDepartmentDataAsync(db,User,r.DepartmentId.Value,ct))return Forbid();
        if(r.DepartmentId!=item.DepartmentId&&(await db.ClassSections.AnyAsync(c=>c.TeacherId==id,ct)||await AccessScope.ActiveAssignments(db).AnyAsync(a=>a.TeacherId==id,ct)))return Conflict("Giảng viên đang có lớp hoặc chức vụ; cần xử lý phân công trước khi chuyển bộ môn.");
        var err=await TeacherValid(r,id,ct);if(err!=null)return err;
        item.FullName=r.FullName.Trim();item.Email=r.Email.Trim();item.UserId=r.UserId;item.DepartmentId=r.DepartmentId;item.Note=CleanNote(r.Note);item.IsActive=r.IsActive;
        await db.SaveChangesAsync(ct);await Audit("TEACHER_UPDATE",id,item.TeacherCode,ct);return NoContent();
    },ct);

    [HttpGet("class-sections")] public async Task<ActionResult> Classes(CancellationToken ct) { var tid = await AccessScope.TeacherIdAsync(db, User, ct); var q = AccessScope.ClassSections(db, tid).AsNoTracking().Include(x => x.Course).Include(x => x.Teacher); return Ok(await q.OrderBy(x => x.Code).Select(x => new { x.Id, x.Code, x.Name, x.CourseId, Course = x.Course.Name, x.TeacherId, Teacher = x.Teacher.FullName, x.Semester, x.AcademicYear, x.Note, x.IsActive }).ToListAsync(ct)); }
    [HttpPost("class-sections"), Authorize(Roles = "LECTURER")] public Task<ActionResult> AddClass(ClassReq r, CancellationToken ct) => Task.FromResult<ActionResult>(Conflict(new { code = "SCHEDULE_REQUIRED", message = "Dùng màn hình Lớp học phần và API /api/class-sections để lưu lịch và tự sinh buổi học." }));
    [HttpPut("class-sections/{id:long}"), Authorize(Roles = "LECTURER")] public Task<ActionResult> UpdateClass(long id, ClassReq r, CancellationToken ct) => Task.FromResult<ActionResult>(Conflict(new { code = "SCHEDULE_PREVIEW_REQUIRED", message = "Dùng API /api/class-sections cùng bước xem ảnh hưởng để sửa lớp học phần." }));

    [HttpGet("rooms")] public async Task<ActionResult> Rooms(CancellationToken ct) => Ok(await db.Rooms.AsNoTracking().OrderBy(x => x.Code).ToListAsync(ct));
    [HttpPost("rooms"), Authorize(Roles = "ADMIN")] public Task<ActionResult> AddRoom(RoomReq r, CancellationToken ct) => BusinessTransaction.Run<ActionResult>(db, BusinessCodes.Resource, async () => { if (!ValidRoomType(r.RoomType)) return BadRequest("Loại phòng không hợp lệ."); var code = BusinessCodes.RoomPrefix(r.Campus,r.Building,r.RoomNumber,r.RoomType); if (await db.Rooms.AnyAsync(x => x.Code.ToUpper() == ApiValidation.Key(code), ct)) return Duplicate("ROOM_CODE_DUPLICATE", "code", $"Mã phòng {code} đã tồn tại."); var x = new Room { Code = code, Name = r.Name.Trim(), Location = r.Location?.Trim(), Capacity = Math.Max(0, r.Capacity), IsActive = r.IsActive, RoomType = r.RoomType }; db.Add(x); await db.SaveChangesAsync(ct); await Audit("ROOM_CREATE", x.Id, x.Code, ct); return Ok(x); }, ct);
    [HttpPut("rooms/{id:long}"), Authorize(Roles = "ADMIN")] public async Task<ActionResult> UpdateRoom(long id, RoomReq r, CancellationToken ct) { var x = await db.Rooms.FindAsync([id], ct); if (x is null) return NotFound(); if (!ValidRoomType(r.RoomType)) return BadRequest("Loại phòng không hợp lệ."); var code = x.Code; if (await db.Rooms.AnyAsync(y => y.Id != id && y.Code.ToUpper() == ApiValidation.Key(code), ct)) return Duplicate("ROOM_CODE_DUPLICATE", "code", $"Mã phòng {code} đã tồn tại."); x.Name = r.Name.Trim(); x.Location = r.Location?.Trim(); x.Capacity = Math.Max(0, r.Capacity); x.IsActive = r.IsActive; x.RoomType = r.RoomType; await db.SaveChangesAsync(ct); await Audit("ROOM_UPDATE", id, x.Code, ct); return NoContent(); }

    [HttpGet("cameras")] public async Task<ActionResult> Cameras(CancellationToken ct) => Ok(await db.CamerasSet.AsNoTracking().Include(x => x.Room).OrderBy(x => x.Code).Select(x => new { x.Id, x.Code, x.Name, x.RoomId, Room = x.Room.Code, x.RtspUrl, SourceMode = x.RtspUrl.StartsWith("browser://") ? "BROWSER" : "RTSP", x.Status, x.LastHealthCheckAt, x.LastHealthMessage, x.Note, x.IsActive }).ToListAsync(ct));
    [HttpGet("cameras/{id:long}"), Authorize(Roles = "ADMIN,TECH_AI")] public async Task<ActionResult> CameraDetail(long id, CancellationToken ct) { var x = await db.CamerasSet.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost("cameras"), Authorize(Roles = "ADMIN")] public Task<ActionResult> AddCamera(CameraReq r, CancellationToken ct) => BusinessTransaction.Run<ActionResult>(db, BusinessCodes.Resource, async () => { var err = await CameraValid(r, null, ct); if (err != null) return err; var room = await db.Rooms.SingleAsync(x => x.Id == r.RoomId, ct);
        if (!System.Text.RegularExpressions.Regex.IsMatch(room.Code, "^PH-[A-Z0-9]+(?:-[A-Z0-9]+)*$") || System.Text.RegularExpressions.Regex.IsMatch(room.Code, "[A-F0-9]{32}")) return BadRequest("Phòng học chưa có mã hợp lệ để cấp mã Camera. Vui lòng chọn phòng có đủ thông tin cơ sở, tòa và số phòng.");
        var x = new Camera { Code = await BusinessCodes.Next(db, "CAM-" + room.Code[3..] + "-", 2, db.CamerasSet.Select(x => x.Code), ct), Name = r.Name.Trim(), RoomId = r.RoomId, RtspUrl = r.RtspUrl.Trim(), Status = "UNKNOWN", Note = CleanNote(r.Note), IsActive = r.IsActive }; db.Add(x); await db.SaveChangesAsync(ct); await Audit("CAMERA_CREATE", x.Id, x.Code, ct); return Ok(new { x.Id, x.Code, x.Name, x.RoomId, x.Status, x.Note, x.IsActive }); }, ct);
    [HttpPut("cameras/{id:long}"), Authorize(Roles = "ADMIN")] public async Task<ActionResult> UpdateCamera(long id, CameraReq r, CancellationToken ct) { var x = await db.CamerasSet.FindAsync([id], ct); if (x is null) return NotFound(); var err = await CameraValid(r, id, ct); if (err != null) return err; x.Name = r.Name.Trim(); x.RoomId = r.RoomId; x.RtspUrl = r.RtspUrl.Trim(); x.Note = CleanNote(r.Note); x.IsActive = r.IsActive; await db.SaveChangesAsync(ct); await Audit("CAMERA_UPDATE", id, x.Code, ct); return NoContent(); }
    [HttpPost("cameras/{id:long}/test"), Authorize(Roles = "ADMIN,TECH_AI")] public async Task<ActionResult> TestCamera(long id, CancellationToken ct) { var x = await db.CamerasSet.FindAsync([id], ct); if (x is null) return NotFound(); string message; if (x.RtspUrl.StartsWith("browser://", StringComparison.OrdinalIgnoreCase)) { x.Status = "READY"; message = "Camera laptop sẽ được kiểm tra khi trình duyệt cấp quyền lúc bắt đầu buổi học."; } else { var uri = new Uri(x.RtspUrl); var port = uri.Port > 0 ? uri.Port : 554; try { using var tcp = new TcpClient(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3)); await tcp.ConnectAsync(uri.Host, port, timeout.Token); x.Status = "ONLINE"; message = $"TCP {uri.Host}:{port} reachable"; } catch (Exception ex) { x.Status = "OFFLINE"; message = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message; } } x.LastHealthCheckAt = DateTime.UtcNow; x.LastHealthMessage = message; await db.SaveChangesAsync(ct); await Audit("CAMERA_TEST", id, x.Code, ct); return Ok(new { x.Id, x.Status, x.LastHealthCheckAt, x.LastHealthMessage }); }

    [HttpGet("class-sections/{id:long}/roster")] public async Task<ActionResult> Roster(long id, CancellationToken ct) { if (!await AccessScope.CanAccessClassAsync(db, User, id, ct)) return Forbid(); return Ok(await db.Enrollments.AsNoTracking().Where(x => x.ClassSectionId == id).Include(x => x.Student).Select(x => new { x.StudentId, x.Student.StudentCode, x.Student.FullName, x.Student.Email }).OrderBy(x => x.StudentCode).ToListAsync(ct)); }
    public record RosterReq(long[] StudentIds);
    [HttpPut("class-sections/{id:long}/roster"), Authorize(Roles = "LECTURER")] public Task<ActionResult> SetRoster(long id, RosterReq r, CancellationToken ct) => new ClassSectionsController(db, audit) { ControllerContext = ControllerContext }.SetStudents(id, new(r.StudentIds), ct);
    [HttpPost("class-sections/{id:long}/roster/import-csv"), Authorize(Roles = "LECTURER")] public async Task<ActionResult> ImportRoster(long id, IFormFile file, CancellationToken ct) { if (!await AccessScope.CanManageRosterAsync(db, User, id, ct)) return Forbid(); if (file is null || file.Length == 0) return BadRequest("Thiếu CSV."); using var sr = new StreamReader(file.OpenReadStream(), Encoding.UTF8, true); var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase); while (await sr.ReadLineAsync(ct) is string line) { var code = line.Split(',', ';')[0].Trim().Trim('"'); if (code.Contains("MSSV", StringComparison.OrdinalIgnoreCase) || code.Contains("StudentCode", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(code)) continue; codes.Add(code); } var students = await db.StudentsSet.Where(x => codes.Contains(x.StudentCode) && x.IsActive).ToListAsync(ct); var missing = codes.Except(students.Select(x => x.StudentCode), StringComparer.OrdinalIgnoreCase).ToArray(); if (missing.Length > 0) return BadRequest(new { message = "Có MSSV không tồn tại", missing }); return await SetRoster(id, new RosterReq(students.Select(x => x.Id).ToArray()), ct); }

    private async Task<ActionResult?> TeacherValid(TeacherReq r, long? id, CancellationToken ct) {
        if (r.UserId != null) {
            if (!await db.UsersSet.AnyAsync(x => x.Id == r.UserId && x.Status == "ACTIVE", ct)) return BadRequest("Tài khoản người dùng không hoạt động.");
            var previousUser = id == null ? null : await db.Teachers.Where(x => x.Id == id).Select(x => x.UserId).FirstOrDefaultAsync(ct);
            if (previousUser != r.UserId && !await db.UsersSet.AnyAsync(x => x.Id == r.UserId && x.UserRoles.Any(ur => ur.Role.Name == "LECTURER") && !x.UserRoles.Any(ur => ur.Role.Name == "ADMIN"), ct)) return BadRequest("Chỉ liên kết tài khoản giảng viên hiện có.");
            if (await db.Teachers.AnyAsync(x => x.Id != id && x.UserId == r.UserId, ct)) return Duplicate("TEACHER_USER_DUPLICATE", "userId", "Tài khoản này đã liên kết với giảng viên khác.");
        }
        if (r.DepartmentId != null && !await db.DepartmentsSet.AnyAsync(x => x.Id == r.DepartmentId && x.IsActive && x.Faculty.IsActive, ct)) return BadRequest("Bộ môn không hợp lệ.");
        return null;
    }
    private async Task<ActionResult?> CameraValid(CameraReq r, long? id, CancellationToken ct) { if (!await db.Rooms.AnyAsync(x => x.Id == r.RoomId && x.IsActive, ct)) return BadRequest("Phòng không hợp lệ."); if (!Uri.TryCreate(r.RtspUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("rtsp" or "rtsps" or "browser")) return BadRequest("Nguồn camera phải là RTSP/RTSPS hoặc browser://laptop."); return null; }
    [HttpDelete("class-sections/{id:long}/roster/{studentId:long}"), Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> RemoveRosterStudent(long id, long studentId, CancellationToken ct) {
        if (!await AccessScope.CanManageRosterAsync(db, User, id, ct)) return Forbid();
        if (!await db.Enrollments.AnyAsync(x => x.ClassSectionId == id && x.StudentId == studentId, ct)) return NotFound();
        var ids = await db.Enrollments.Where(x => x.ClassSectionId == id && x.StudentId != studentId).Select(x => x.StudentId).ToArrayAsync(ct);
        return await new ClassSectionsController(db, audit) { ControllerContext = ControllerContext }.SetStudents(id, new(ids), ct);
    }

    [HttpDelete("{resource}/{id:long}"), Authorize(Roles = "ADMIN,LECTURER,TECH_AI")]
    public async Task<ActionResult> DeactivateCatalog(string resource, long id, CancellationToken ct)
    {
        resource = resource.Trim().ToLowerInvariant();
        if (!await CanChangeActive(resource, id, ct)) return Forbid();
        string code;
        switch (resource)
        {
            case "faculties":
                { var x = await db.FacultiesSet.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Khoa không tồn tại." }); if (await db.DepartmentsSet.AnyAsync(y => y.FacultyId == id, ct) || await db.StudentClassesSet.AnyAsync(y => y.FacultyId == id, ct)) return Conflict(new { message = $"Không thể ngừng khoa {x.Code} vì đang có bộ môn hoặc lớp sinh hoạt tham chiếu." }); x.IsActive = false; code = x.Code; break; }
            case "departments":
                { var x = await db.DepartmentsSet.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Bộ môn không tồn tại." }); if (await db.Teachers.AnyAsync(y => y.DepartmentId == id, ct) || await db.Courses.AnyAsync(y => y.DepartmentId == id, ct)) return Conflict(new { message = $"Không thể ngừng bộ môn {x.Code} vì đang có giảng viên hoặc môn học tham chiếu." }); x.IsActive = false; code = x.Code; break; }
            case "student-classes":
                { var x = await db.StudentClassesSet.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Lớp sinh hoạt không tồn tại." }); if (await db.StudentsSet.AnyAsync(y => y.StudentClassId == id, ct)) return Conflict(new { message = $"Không thể ngừng lớp {x.Code} vì đang có sinh viên tham chiếu." }); x.IsActive = false; code = x.Code; break; }
            case "courses":
                { var x = await db.Courses.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Môn học không tồn tại." }); if (await db.ClassSections.AnyAsync(y => y.CourseId == id, ct)) return Conflict(new { message = $"Không thể ngừng môn {x.Code} vì đang có lớp học phần tham chiếu." }); x.IsActive = false; code = x.Code; break; }
            case "teachers":
                { var x = await db.Teachers.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Giảng viên không tồn tại." }); if (await db.ClassSections.AnyAsync(y => y.TeacherId == id, ct)) return Conflict(new { message = $"Không thể ngừng giảng viên {x.TeacherCode} vì đang phụ trách lớp học phần." }); x.IsActive = false; code = x.TeacherCode; break; }
            case "class-sections":
                { var x = await db.ClassSections.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Lớp học phần không tồn tại." }); if (await db.SessionsSet.AnyAsync(y => y.ClassSectionId == id, ct) || await db.Enrollments.AnyAsync(y => y.ClassSectionId == id, ct)) return Conflict(new { message = $"Không thể ngừng lớp học phần {x.Code} vì đã có buổi học hoặc danh sách sinh viên tham chiếu." }); x.IsActive = false; code = x.Code; break; }
            case "rooms":
                { var x = await db.Rooms.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Phòng không tồn tại." }); if (await db.CamerasSet.AnyAsync(y => y.RoomId == id, ct) || await db.SessionsSet.AnyAsync(y => y.RoomId == id, ct)) return Conflict(new { message = $"Không thể ngừng phòng {x.Code} vì đang có camera hoặc buổi học tham chiếu." }); x.IsActive = false; code = x.Code; break; }
            case "cameras":
                { var x = await db.CamerasSet.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Camera không tồn tại." }); if (await db.SessionsSet.AnyAsync(y => y.CameraId == id, ct)) return Conflict(new { message = $"Không thể ngừng camera {x.Code} vì đã được gắn với buổi học." }); x.IsActive = false; code = x.Code; break; }
            default: return NotFound(new { message = "Danh mục không được hỗ trợ." });
        }
        await db.SaveChangesAsync(ct); await Audit("CATALOG_DEACTIVATE", id, code, ct); return NoContent();
    }

    [HttpPost("{resource}/{id:long}/reactivate"), Authorize(Roles = "ADMIN,LECTURER,TECH_AI")]
    public async Task<ActionResult> ReactivateCatalog(string resource, long id, CancellationToken ct)
    {
        resource = resource.Trim().ToLowerInvariant();
        if (!await CanChangeActive(resource, id, ct)) return Forbid();
        string code;
        switch (resource)
        {
            case "faculties":
                { var x = await db.FacultiesSet.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Khoa không tồn tại." }); x.IsActive = true; code = x.Code; break; }
            case "departments":
                { var x = await db.DepartmentsSet.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Bộ môn không tồn tại." }); if (!await db.FacultiesSet.AnyAsync(y => y.Id == x.FacultyId && y.IsActive, ct)) return Conflict(new { code = "PARENT_INACTIVE", message = "Không thể kích hoạt bộ môn khi khoa đang ngừng hoạt động." }); x.IsActive = true; code = x.Code; break; }
            case "student-classes":
                { var x = await db.StudentClassesSet.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Lớp sinh hoạt không tồn tại." }); if (!await db.FacultiesSet.AnyAsync(y => y.Id == x.FacultyId && y.IsActive, ct)) return Conflict(new { code = "PARENT_INACTIVE", message = "Không thể kích hoạt lớp sinh hoạt khi khoa đang ngừng hoạt động." }); x.IsActive = true; code = x.Code; break; }
            case "courses":
                { var x = await db.Courses.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Môn học không tồn tại." }); if (x.DepartmentId is null || !await db.DepartmentsSet.AnyAsync(y => y.Id == x.DepartmentId && y.IsActive, ct)) return Conflict(new { code = "PARENT_INACTIVE", message = "Không thể kích hoạt môn học khi bộ môn đang ngừng hoạt động." }); x.IsActive = true; code = x.Code; break; }
            case "teachers":
                { var x = await db.Teachers.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Giảng viên không tồn tại." }); if (x.DepartmentId is null || !await db.DepartmentsSet.AnyAsync(y => y.Id == x.DepartmentId && y.IsActive, ct)) return Conflict(new { code = "PARENT_INACTIVE", message = "Không thể kích hoạt giảng viên khi bộ môn đang ngừng hoạt động." }); x.IsActive = true; code = x.TeacherCode; break; }
            case "class-sections":
                { var x = await db.ClassSections.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Lớp học phần không tồn tại." }); if (!await db.Courses.AnyAsync(y => y.Id == x.CourseId && y.IsActive, ct) || !await db.Teachers.AnyAsync(y => y.Id == x.TeacherId && y.IsActive, ct)) return Conflict(new { code = "PARENT_INACTIVE", message = "Không thể kích hoạt lớp học phần khi môn học hoặc giảng viên đang ngừng hoạt động." }); x.IsActive = true; code = x.Code; break; }
            case "rooms":
                { var x = await db.Rooms.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Phòng không tồn tại." }); x.IsActive = true; code = x.Code; break; }
            case "cameras":
                { var x = await db.CamerasSet.FindAsync([id], ct); if (x is null) return NotFound(new { message = "Camera không tồn tại." }); if (!await db.Rooms.AnyAsync(y => y.Id == x.RoomId && y.IsActive, ct)) return Conflict(new { code = "PARENT_INACTIVE", message = "Không thể kích hoạt camera khi phòng đang ngừng hoạt động." }); x.IsActive = true; code = x.Code; break; }
            default: return NotFound(new { message = "Danh mục không được hỗ trợ." });
        }
        await db.SaveChangesAsync(ct);
        await Audit("CATALOG_REACTIVATE", id, code, ct);
        return NoContent();
    }

    private static bool ValidRoomType(string type) => new[] { "UNKNOWN", "THEORY", "ZOOM", "COMPUTER", "LAB", "PRACTICE", "PE", "DEFENSE" }.Contains(type);
    private static string? CleanNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    private async Task<bool> CanChangeActive(string resource, long id, CancellationToken ct)
    {
        if (resource is "rooms" or "cameras") return User.IsInRole("ADMIN") || User.IsInRole("TECH_AI");
        if (!User.IsInRole("LECTURER")) return false;
        return resource switch
        {
            "faculties" => await AccessScope.CanManageFacultyAsync(db, User, id, ct),
            "departments" => await AccessScope.CanManageDepartmentAsync(db, User, id, ct),
            "student-classes" => await AccessScope.CanManageStudentClassAsync(db, User, id, ct),
            "courses" => await CanManageCourse(id, ct),
            "teachers" => await CanManageTeacher(id, ct),
            "class-sections" => (await AccessScope.ClassPermissions(db, User, id, ct)).CanDeactivate,
            _ => false
        };
    }
    private async Task<bool> CanManageCourse(long id, CancellationToken ct) { var departmentId = await db.Courses.Where(x => x.Id == id).Select(x => x.DepartmentId).FirstOrDefaultAsync(ct); return departmentId != null && await AccessScope.CanManageDepartmentDataAsync(db, User, departmentId.Value, ct); }
    private async Task<bool> CanManageTeacher(long id, CancellationToken ct) { var departmentId = await db.Teachers.Where(x => x.Id == id).Select(x => x.DepartmentId).FirstOrDefaultAsync(ct); return departmentId != null && await AccessScope.CanManageDepartmentDataAsync(db, User, departmentId.Value, ct); }
    private ConflictObjectResult Duplicate(string code, string field, string message) => Conflict(ApiValidation.Duplicate(code, field, message));
    private async Task<long[]> VisibleDepartmentIds(CancellationToken ct)
    {
        var tid = await AccessScope.TeacherIdAsync(db, User, ct);
        if (tid is not > 0) return [];
        var ownDepartmentId = await db.Teachers.AsNoTracking().Where(t => t.Id == tid && t.DepartmentId != null).Select(t => t.DepartmentId).FirstOrDefaultAsync(ct);
        var assignments = await AccessScope.ActiveAssignments(db).AsNoTracking().Where(a => a.TeacherId == tid && a.IsActive).Select(a => new { a.DepartmentId, a.FacultyId }).ToListAsync(ct);
        var departmentIds = assignments.Where(x => x.DepartmentId.HasValue).Select(x => x.DepartmentId!.Value).ToList();
        if (ownDepartmentId.HasValue) departmentIds.Add(ownDepartmentId.Value);
        var facultyIds = assignments.Where(x => x.FacultyId.HasValue).Select(x => x.FacultyId!.Value).Distinct().ToArray();
        var facultyDepartmentIds = await db.DepartmentsSet.AsNoTracking().Where(x => facultyIds.Contains(x.FacultyId)).Select(x => x.Id).ToListAsync(ct);
        return departmentIds.Concat(facultyDepartmentIds).Distinct().ToArray();
    }
    private async Task<long[]> VisibleFacultyIds(CancellationToken ct)
    {
        if (User.IsInRole("ADMIN")) return await db.FacultiesSet.Select(x => x.Id).ToArrayAsync(ct);
        var tid = await AccessScope.TeacherIdAsync(db, User, ct);
        if (tid is not > 0) return [];
        var assignments = await AccessScope.ActiveAssignments(db).AsNoTracking().Where(a => a.TeacherId == tid && a.IsActive).Select(a => new { a.DepartmentId, a.FacultyId }).ToListAsync(ct);
        var explicitFacultyIds = assignments.Where(x => x.FacultyId.HasValue).Select(x => x.FacultyId!.Value).ToList();
        var departmentIds = assignments.Where(x => x.DepartmentId.HasValue).Select(x => x.DepartmentId!.Value).ToList();
        var ownDepartmentId = await db.Teachers.AsNoTracking().Where(t => t.Id == tid && t.DepartmentId != null).Select(t => t.DepartmentId).FirstOrDefaultAsync(ct);
        if (ownDepartmentId.HasValue) departmentIds.Add(ownDepartmentId.Value);
        var departmentFacultyIds = await db.DepartmentsSet.AsNoTracking().Where(d => departmentIds.Contains(d.Id)).Select(d => d.FacultyId).ToListAsync(ct);
        return explicitFacultyIds.Concat(departmentFacultyIds).Distinct().ToArray();
    }
    private Task Audit(string action, long id, string code, CancellationToken ct) => audit.WriteAsync(UserContext.Id(User), action, "Catalog", id.ToString(), new { code }, ct);
}
