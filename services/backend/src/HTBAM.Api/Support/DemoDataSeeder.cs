using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HTBAM.Application.DTOs;
using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Support;

/// <summary>Deterministic, additive development fixture. Existing records are never reset.</summary>
public static class DemoDataSeeder
{
    public const string Warning = "Dữ liệu lịch mẫu, chưa phải lịch chính thức";
    public const string LocalPassword = "HTBAM-Local-Demo!2026#";
    public static readonly string[] Accounts = ["admin", "dean", "head1", "head2", "head3", "lecturer1", "lecturer2", "lecturer3", "lecturer4", "lecturer5", "lecturer6", "editor", "viewer"];
    public static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("HTBAM:DEMO:v1:" + value)))[..32].ToLowerInvariant();

    public static Task<int> SeedAsync(AppDbContext db, string? password = null, CancellationToken ct = default) =>
        BusinessTransaction.Run(db, "HTBAM:academic-scheduling", async () =>
        {
            if (!db.DevelopmentDataEnabled) throw new InvalidOperationException("Development seed must be explicitly enabled in Development.");
            password ??= Environment.GetEnvironmentVariable("SEED_DEFAULT_PASSWORD") ?? LocalPassword;
            if (password.Length < 12) throw new InvalidOperationException("SEED_DEFAULT_PASSWORD must have at least 12 characters.");
            var facultyMarker = await db.CatalogSeedIdentities.SingleOrDefaultAsync(x => x.Key == "demo.faculty", ct);
            var faculty = facultyMarker?.FacultyId is long facultyId ? await db.FacultiesSet.SingleAsync(x => x.Id == facultyId, ct)
                : await db.FacultiesSet.SingleOrDefaultAsync(x => x.Code == "DEMO-CNTT", ct);
            if (faculty == null) { faculty = new() { Code = await BusinessCodes.Next(db,"KHOA-CNTT-",2,db.FacultiesSet.Select(x=>x.Code),ct,true), Name = "Khoa Công nghệ Thông tin (DEMO)", Note = Warning }; db.FacultiesSet.Add(faculty); await db.SaveChangesAsync(ct); }
            if (facultyMarker is null) { db.CatalogSeedIdentities.Add(new() { Key="demo.faculty", FacultyId=faculty.Id }); await db.SaveChangesAsync(ct); }
            var departments = new Department[4];
            string[] deptCodes = ["HTTT", "CNPM", "KHMT", "EMPTY"];
            string[] deptNames = ["Hệ thống thông tin", "Công nghệ phần mềm", "Khoa học máy tính", "Bộ môn chờ bổ nhiệm"];
            for (var i = 0; i < 4; i++)
            {
                var code = "DEMO-" + deptCodes[i];
                var seedKey="demo.department."+deptCodes[i];
                var identity=await db.CatalogSeedIdentities.SingleOrDefaultAsync(x=>x.Key==seedKey,ct);
                var d = identity?.DepartmentId is long existingId ? await db.DepartmentsSet.SingleAsync(x=>x.Id==existingId,ct) : await db.DepartmentsSet.SingleOrDefaultAsync(x => x.Code == code || x.FacultyId==faculty.Id&&x.Name==deptNames[i]+" (DEMO)", ct);
                if (d == null) { d = new() { Code = await CatalogNumbering.Department(db,deptNames[i],ct), Name = deptNames[i] + " (DEMO)", FacultyId = faculty.Id, Note = Warning }; db.DepartmentsSet.Add(d); await db.SaveChangesAsync(ct); }
                if(identity is null){db.CatalogSeedIdentities.Add(new(){Key=seedKey,DepartmentId=d.Id});await db.SaveChangesAsync(ct);}
                departments[i] = d;
            }
            var teachers = new Dictionary<string, Teacher>();
            var users = new Dictionary<string, User>();
            foreach (var name in Accounts)
            {
                var userName = "demo." + name;
                var user = await db.UsersSet.SingleOrDefaultAsync(x => x.UserName == userName, ct);
                if (user == null)
                {
                    user = new() { UserName = userName, Email = userName + "@example.invalid", FullName = "DEMO " + name };
                    user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
                    db.UsersSet.Add(user); await db.SaveChangesAsync(ct);
                    var role = await db.RolesSet.SingleAsync(x => x.Name == (name == "admin" ? "ADMIN" : "LECTURER"), ct);
                    db.UserRoles.Add(new() { UserId = user.Id, RoleId = role.Id });
                }
                users[name] = user;
                var teacherCode = "DEMO-GV-" + name.ToUpperInvariant();
                var teacher = await db.Teachers.SingleOrDefaultAsync(x => x.UserId == user.Id, ct)
                    ?? await db.Teachers.SingleOrDefaultAsync(x => x.TeacherCode == teacherCode, ct);
                if (teacher == null)
                {
                    var deptIndex = name.StartsWith("head") ? int.Parse(name[4..]) - 1 : name.StartsWith("lecturer") ? (int.Parse(name[8..]) - 1) % 3 : name == "viewer" ? 1 : 0;
                    teacher = new() { TeacherCode = await LecturerCodeAllocator.Next(db,departments[deptIndex],true,ct), UserId = user.Id, Email = user.Email, FullName = user.FullName, DepartmentId = departments[deptIndex].Id, Note = Warning };
                    db.Teachers.Add(teacher); await db.SaveChangesAsync(ct);
                }
                teachers[name] = teacher;
            }
            await Assignment(db, teachers["dean"].Id, faculty.Id, null, users["admin"].Id, true, ct);
            for (var i = 0; i < 3; i++) await Assignment(db, teachers["head" + (i + 1)].Id, null, departments[i].Id, users["dean"].Id, true, ct);
            // An intentionally ended appointment gives the empty department a revocation/reappointment scenario.
            await Assignment(db, teachers["lecturer6"].Id, null, departments[3].Id, users["dean"].Id, false, ct);

            var studentClasses = new StudentClass[3];
            for (var i = 0; i < 3; i++)
            {
                var code = $"DEMO-SHC-{i + 1:00}";
                var key = $"demo.student-class.{i+1:00}";
                var marker = await db.CatalogSeedIdentities.SingleOrDefaultAsync(x=>x.Key==key,ct);
                var item = marker?.StudentClassId is long classId ? await db.StudentClassesSet.SingleAsync(x=>x.Id==classId,ct) : await db.StudentClassesSet.SingleOrDefaultAsync(x => x.Code == code, ct);
                if (item == null) { item = new() { Code = await BusinessCodes.Next(db,$"LSH-2026-{departments[i].Code[3..]}-",2,db.StudentClassesSet.Select(x=>x.Code),ct), Name = $"Lớp sinh hoạt DEMO {i + 1}", FacultyId = faculty.Id, StartYear = 2026, Note = Warning }; db.StudentClassesSet.Add(item); await db.SaveChangesAsync(ct); }
                if(marker is null){db.CatalogSeedIdentities.Add(new(){Key=key,StudentClassId=item.Id});await db.SaveChangesAsync(ct);}
                studentClasses[i] = item;
            }
            string[] surnames = ["Nguyễn", "Trần", "Lê", "Phạm", "Hoàng", "Huỳnh", "Phan", "Vũ", "Đặng", "Bùi"];
            string[] givenNames = ["Văn An", "Thị Bình", "Minh Châu", "Ngọc Dung", "Quốc Huy", "Thanh Hà"];
            for (var i = 0; i < 60; i++)
            {
                var code = $"DEMO-SV-{i + 1:03}";
                var key=$"demo.student.{i+1:03}";
                var marker=await db.CatalogSeedIdentities.SingleOrDefaultAsync(x=>x.Key==key,ct);
                var student=marker?.StudentId is long studentId ? await db.StudentsSet.SingleAsync(x=>x.Id==studentId,ct) : await db.StudentsSet.SingleOrDefaultAsync(x=>x.StudentCode==code,ct);
                if(student is null){student=new(){
                    StudentCode = await BusinessCodes.Next(db,"SV-DEMO-",5,db.StudentsSet.Select(x=>x.StudentCode),ct), AnonymousCode = await BusinessCodes.Next(db,"SV-ANON-",6,db.StudentsSet.Select(x=>x.AnonymousCode),ct), FullName = surnames[i % 10] + " " + givenNames[i / 10],
                    Email = $"demo.sv{i + 1:03}@example.invalid", StudentClassId = studentClasses[i / 20].Id, IsActive = i % 20 != 19 };db.StudentsSet.Add(student);await db.SaveChangesAsync(ct);}
                if(marker is null){db.CatalogSeedIdentities.Add(new(){Key=key,StudentId=student.Id});await db.SaveChangesAsync(ct);}
            }
            await db.SaveChangesAsync(ct);
            var students = await db.StudentsSet.Where(x => db.CatalogSeedIdentities.Any(m=>m.StudentId==x.Id) && x.IsActive).OrderBy(x => x.StudentCode).ToArrayAsync(ct);
            var rooms = new List<Room>();
            foreach (var (type, count) in new[] { ("THEORY", 5), ("COMPUTER", 4), ("LAB", 2), ("ZOOM", 3), ("INACTIVE", 1) })
                for (var i = 1; i <= count; i++)
                {
                    var code = $"DEMO-{type}-{i:00}";
                    var key=$"demo.room.{type}.{i:00}";
                    var marker=await db.CatalogSeedIdentities.SingleOrDefaultAsync(x=>x.Key==key,ct);
                    var room = marker?.RoomId is long roomId ? await db.Rooms.SingleAsync(x=>x.Id==roomId,ct) : await db.Rooms.SingleOrDefaultAsync(x => x.Code == code, ct);
                    // The DEMO campus/building below are explicit fixture metadata, never inferred from real rooms.
                    if (room == null) { room = new() { Code = BusinessCodes.RoomPrefix("DEMO",type,type=="ZOOM"?$"ZOOM{i:00}":$"{i:000}",type), Name = $"Phòng {type} DEMO {i}", RoomType = type == "INACTIVE" ? "THEORY" : type,
                        Capacity = type == "ZOOM" ? 100 : type == "THEORY" ? 60 : 30, IsActive = type != "INACTIVE", Location = "Cơ sở DEMO", Note = Warning }; db.Rooms.Add(room); await db.SaveChangesAsync(ct); }
                    if(marker is null){db.CatalogSeedIdentities.Add(new(){Key=key,RoomId=room.Id});await db.SaveChangesAsync(ct);}
                    rooms.Add(room);
                }
            var terms = new AcademicTerm[2];
            for (var i = 0; i < 2; i++)
            {
                var semester = i + 1;
                var term = await db.AcademicTerms.SingleOrDefaultAsync(x => x.AcademicYear == "2026-2027" && x.Semester == semester, ct);
                if (term == null)
                {
                    term = new() { AcademicYear = "2026-2027", Semester = semester, StartsOn = i == 0 ? new(2026, 8, 17) : new(2027, 1, 11),
                        EndsOn = i == 0 ? new(2026, 12, 20) : new(2027, 5, 16), IsDevelopment = true, Status = i == 0 ? "ACTIVE" : "DRAFT",
                        DivisionPolicy = "EXPLICIT", WeekOneMonday = i == 0 ? new(2026, 8, 17) : new(2027, 1, 11), Evidence = "DEVELOPMENT/DEMO: " + Warning + ". Ngày, tuần và ranh giới đều là cấu hình kiểm thử." };
                    db.AcademicTerms.Add(term); await db.SaveChangesAsync(ct);
                    foreach (var credits in new[] { 1, 2 })
                        foreach (var (range, n) in AcademicRules.EqualDays(term.StartsOn.Value, term.EndsOn.Value, credits == 1 ? 3 : 2).Select((x, n) => (x, n)))
                            db.TermPhases.Add(new() { AcademicTermId = term.Id, Credits = credits, Number = n + 1, StartsOn = range.StartsOn, EndsOn = range.EndsOn });
                    await db.SaveChangesAsync(ct);
                }
                if (!term.IsDevelopment) throw new InvalidOperationException("2026-2027 term already exists as real data. Demo seed will not overwrite it; select separate demo years in source.");
                terms[i] = term;
            }
            string[] courseNames = ["Cơ sở dữ liệu", "Phân tích hệ thống", "Kỹ năng học tập", "Lập trình web", "Kiểm thử phần mềm", "Thực hành lập trình", "Trí tuệ nhân tạo", "Học máy", "Thực hành Python", "Mạng máy tính", "An toàn thông tin", "Kỹ năng nghiên cứu", "Môn đã ngừng"];
            var policy = await db.AttendancePoliciesSet.SingleAsync(x => x.Code == "DEFAULT", ct);
            // Leave groups 01/02 available for the documented manual demonstration.
            if (!await db.CatalogSeedIdentities.AnyAsync(x=>x.Key=="demo.course.HTBAM",ct))
            {
                var demoCourse=await db.Courses.SingleOrDefaultAsync(x=>x.Code=="HTBAM"||x.DepartmentId==departments[0].Id&&x.Name=="Hệ thống theo dõi hành vi (DEMO)",ct);
                if(demoCourse is null){demoCourse=new Course{StartYear=2026,EndYear=2027,StartSemester="HK1",Code=await CatalogNumbering.Course(db,departments[0],true,ct),Name="Hệ thống theo dõi hành vi (DEMO)",DepartmentId=departments[0].Id,Credits=3,Note=Warning};db.Courses.Add(demoCourse);await db.SaveChangesAsync(ct);}
                db.CatalogSeedIdentities.Add(new(){Key="demo.course.HTBAM",CourseId=demoCourse.Id});
                await db.SaveChangesAsync(ct);
            }
            for (var i = 0; i < 13; i++)
            {
                var code = $"DEMO-MH-{i + 1:00}";
                var departmentIndex = i / 3 % 3;
                var seedKey=$"demo.course.{i+1:00}";
                var identity=await db.CatalogSeedIdentities.SingleOrDefaultAsync(x=>x.Key==seedKey,ct);
                var course = identity?.CourseId is long existingId ? await db.Courses.SingleAsync(x=>x.Id==existingId,ct) : await db.Courses.SingleOrDefaultAsync(x => x.Code == code || x.DepartmentId==departments[departmentIndex].Id&&x.Name==courseNames[i]+" (DEMO)", ct);
                if (course == null) { course = new() { StartYear=2026,EndYear=2027,StartSemester="HK1", Code = await CatalogNumbering.Course(db,departments[departmentIndex],true,ct), Name = courseNames[i] + " (DEMO)", DepartmentId = departments[departmentIndex].Id,
                    Credits = 3 - i % 3, IsActive = i != 12, Note = Warning }; db.Courses.Add(course); await db.SaveChangesAsync(ct); }
                if(identity is null){db.CatalogSeedIdentities.Add(new(){Key=seedKey,CourseId=course.Id});await db.SaveChangesAsync(ct);}
                if (i == 12) continue;
                var classCode = $"DEMO-LHP-{i + 1:00}";
                // An existing class, including any user edits or cancellation, is never regenerated.
                var creationKey = Key(classCode);
                if (await db.ClassSections.AnyAsync(x => x.Code == classCode || x.CreationKey == creationKey, ct)) continue;
                var owner = i == 0 ? teachers["editor"] : i == 3 ? teachers["viewer"] : teachers["lecturer" + (departmentIndex + 1)];
                var term = terms[i == 10 ? 1 : 0];
                int? phase = course.Credits == 3 ? null : course.Credits == 2 ? i % 2 + 1 : i / 3 % 3 + 1;
                var phases = await db.TermPhases.Where(x => x.AcademicTermId == term.Id).ToArrayAsync(ct);
                var range = AcademicRules.Range(term, phases, course.Credits, phase);
                var room = rooms.Where(x => x.IsActive).ElementAt(i);
                var group = AcademicRules.BellGroup(room.RoomType, "LT", null);
                var lines = new List<ScheduleLineRequest>();
                var day = i % 6 + 1;
                var firstPeriod = i / 6 * 6 + 1;
                int[] weeks = Enumerable.Range(1, 53).Where(w => w <= (range.EndsOn.DayNumber - term.StartsOn!.Value.DayNumber) / 7 + 1 && w >= (range.StartsOn.DayNumber - term.StartsOn.Value.DayNumber) / 7 + 1 && w % 2 == 1).ToArray();
                lines.Add(new(Key(classCode + ":1"), day, firstPeriod, firstPeriod + 2, group, room.Id, "LT", range.StartsOn, range.EndsOn, weeks));
                if (i == 0) // Two weekdays and LT/TH.
                    lines.Add(new(Key(classCode + ":2"), 7, 7, 9, "B", rooms.First(x => x.RoomType == "COMPUTER").Id, "TH", range.StartsOn, range.EndsOn, weeks));
                if (i == 1) // Same weekday and periods; a disjoint date range changes rooms.
                {
                    var split = range.StartsOn.AddDays((range.EndsOn.DayNumber - range.StartsOn.DayNumber) / 2);
                    lines[0] = lines[0] with { EndsOn = split };
                    lines.Add(lines[0] with { LineKey = Key(classCode + ":2"), RoomId = rooms[2].Id, StartsOn = split.AddDays(1), EndsOn = range.EndsOn });
                }
                lines = lines.Select(line => line with { SelectedWeeks = line.SelectedWeeks.Where(w => term.WeekOneMonday!.Value.AddDays((w - 1) * 7) <= line.EndsOn && term.WeekOneMonday.Value.AddDays((w - 1) * 7 + 6) >= line.StartsOn).ToArray() }).ToList();
                var request = new ClassSectionRequest($"{classCode} · {courseNames[i]} (DEMO)", course.Id, owner.Id, term.Id, phase, policy.Id, Warning, classCode, lines.ToArray(), CreationKey: creationKey);
                var planner = new ClassSchedulePlanner(db);
                var plan = await planner.Prepare(request, null, ct);
                var section = new ClassSection { Code = await ClassSectionNumbering.Next(db, term, course.Id, ct), Name = request.Name, CourseId = course.Id, TeacherId = owner.Id, CreationKey = request.CreationKey, CreationRequestHash = ClassSchedulePlanner.Hash(request) };
                db.ClassSections.Add(section); await db.SaveChangesAsync(ct);
                foreach (var student in students.Skip(i % 3 * 19).Take(19)) db.Enrollments.Add(new() { ClassSectionId = section.Id, StudentId = student.Id, ClassNote = "DEMO" });
                await db.SaveChangesAsync(ct);
                await planner.Apply(section, request, plan, ct);
                if (i == 11) { section.IsActive = false; await db.SaveChangesAsync(ct); }
                if (i is 0 or 3) { db.ClassSectionGrants.Add(new() { ClassSectionId = section.Id, TeacherId = owner.Id, GrantedByUserId = users["dean"].Id, CanEditDetails = i == 0, CanAddDetails = i == 0, CanDeleteDetails = i == 0, CanManageStudents = i == 0 }); await db.SaveChangesAsync(ct); }
            }
            await Reports(db, users["admin"].Id, ct);
            return Accounts.Length;
        }, ct);

    private static async Task Assignment(AppDbContext db, long teacherId, long? facultyId, long? departmentId, long userId, bool active, CancellationToken ct)
    {
        // A revoked or transferred position stays revoked/transferred on future seed runs.
        if (await db.ManagementAssignmentsSet.AnyAsync(x => x.FacultyId == facultyId && x.DepartmentId == departmentId, ct)) return;
        db.ManagementAssignmentsSet.Add(new() { TeacherId = teacherId, FacultyId = facultyId, DepartmentId = departmentId, PositionType = facultyId != null ? "FACULTY_HEAD" : "DEPARTMENT_HEAD",
            EffectiveFrom = new(2026, 1, 1), EffectiveTo = active ? null : new DateTime(2026, 2, 1), IsActive = active, AssignedByUserId = userId, Note = "DEMO: " + Warning });
        await db.SaveChangesAsync(ct);
    }

    private static async Task Reports(AppDbContext db, long userId, CancellationToken ct)
    {
        if (await db.AuditLogsSet.AnyAsync(x => x.Action == "DEMO_SEED_V1", ct)) return;
        var sessions = await db.SessionsSet.Include(x => x.ClassSection).Where(x => (x.ClassSection.Code.StartsWith("DEMO-LHP-") || (x.ClassSection.SourceClassSectionCode != null && x.ClassSection.SourceClassSectionCode.StartsWith("DEMO-LHP-"))) && x.ScheduledEnd < DateTime.UtcNow)
            .OrderBy(x => x.ClassSectionId).ThenBy(x => x.ScheduledStart).Take(3).ToArrayAsync(ct);
        if (sessions.Length == 0) return;
        foreach (var session in sessions)
        {
            if (session.Status != "DRAFT" || session.StartedAt != null || await db.AttendanceSet.AnyAsync(x => x.SessionId == session.Id, ct)) continue;
            // No camera/video is invented. These are labeled synthetic reporting fixtures, not inference results.
            session.IsDevelopment = true; session.Status = "COMPLETED"; session.StartedAt = session.ScheduledStart; session.EndedAt = session.ScheduledEnd;
            session.LecturerComment = "DEMO: dữ liệu báo cáo tổng hợp giả lập để kiểm thử, không phải kết quả AI thực tế.";
            var roster = await db.SessionStudentsSet.Where(x => x.SessionId == session.Id).OrderBy(x => x.StudentId).ToArrayAsync(ct);
            for (var i = 0; i < roster.Length; i++)
            {
                var studentId = roster[i].StudentId;
                if (i % 5 == 0) continue; // No tracking => absent in the actual summary service.
                var start = session.ScheduledStart.AddMinutes(i % 5 == 1 ? 35 : 0);
                var stable = new StableIdentity { SessionId = session.Id, StudentId = studentId, StableId = $"DEMO-{session.Id}-{studentId}", FirstSeenAt = start, LastSeenAt = session.ScheduledEnd!.Value,
                    IdentityStatus = "IDENTIFIED", IdentityConfidence = .99m, ModelVersion = "DEMO-SYNTHETIC-NOT-INFERENCE", State = "CLOSED" };
                db.StableIdentitiesSet.Add(stable); await db.SaveChangesAsync(ct);
                db.TrackSegmentsSet.Add(new() { StableIdentityId = stable.Id, TrackId = stable.StableId, StartedAt = start, EndedAt = session.ScheduledEnd.Value, AvgQuality = .9m });
                db.IdentityLinksSet.Add(new() { StableIdentityId = stable.Id, StudentId = studentId, EvidenceType = "DEMO_FIXTURE", Score = .99m, Decision = "DEMO", ModelVersion = stable.ModelVersion, CreatedAt = start });
                var label = new[] { "FOCUSED", "DISTRACTED", "SLEEPY", "ACTIVE", "PHONE_USE", "OUT_OF_VIEW" }[i % 6];
                var evt = new BehaviorEvent { ExternalEventId = $"DEMO-BE-{session.Id}-{studentId}", SessionId = session.Id, StudentId = studentId, StableIdentityId = stable.Id, Label = label,
                    StartedAt = start, EndedAt = session.ScheduledEnd.Value, Probability = .95m, ObservationQuality = .9m, ModelVersion = stable.ModelVersion, ThresholdVersion = "DEMO-v1" };
                db.BehaviorEventsSet.Add(evt);
                await new AlertEngine(db).EvaluateAsync(evt, ct);
            }
            await db.SaveChangesAsync(ct);
            await new SummaryService(db).RebuildSessionAsync(session.Id, session.EndedAt!.Value, ct);
            var summary = await db.ClassSessionSummariesSet.SingleAsync(x => x.SessionId == session.Id, ct);
            summary.AutoComment = session.LecturerComment; summary.AutoCommentProvider = "DEMO_FIXTURE";
            await db.SaveChangesAsync(ct);
        }
        db.AuditLogsSet.Add(new() { UserId = userId, Action = "DEMO_SEED_V1", EntityType = "DevelopmentFixture", EntityId = "v1", MetadataJson = JsonSerializer.Serialize(new { Warning, Synthetic = true, Accounts = Accounts.Select(x => "demo." + x) }) });
        await db.SaveChangesAsync(ct);
    }
}
