export interface LoginResponse {
  accessToken: string;
  expiresAt: string;
  userId: number;
  fullName: string;
  roles: string[];
  tokenVersion: number;
}

export interface UserInfo {
  positionLabel?: string;
  id: number;
  userName: string;
  email: string;
  fullName: string;
  status: string;
  teacherId?: number | null;
  roles: string[];
  managementAssignments?: ManagementAssignment[];
  facultyIds?: number[];
  departmentIds?: number[];
  permissions?: string[];
}
export interface Student { id:number; studentCode:string; fullName:string; email:string; studentClassId?:number|null; studentClass?:string|null; anonymousCode:string; sourceAnonymousCode?:string|null; isActive:boolean; createdAt?:string; canManage?:boolean; }
export interface CatalogBase { id:number; code:string; name:string; note?:string|null; isActive:boolean; }
export interface Faculty extends CatalogBase {}
export interface Department extends CatalogBase { facultyId:number; faculty?:string; }
export interface StudentClass extends CatalogBase { facultyId:number; faculty?:string; startYear?:number|null; }
export interface Course extends CatalogBase { startAcademicYear?:string|null; startSemester?:string|null; startYear?:number|null; endYear?:number|null; departmentId?:number|null; credits:number; }
export interface Teacher { id:number; teacherCode:string; fullName:string; email:string; userId?:number|null; departmentId?:number|null; note?:string|null; isActive:boolean; }
export interface ClassSection extends CatalogBase { sourceClassSectionCode?:string|null; courseId:number; course?:string; teacherId:number; teacher?:string; semester:string; academicYear:string; }
export interface Room extends CatalogBase { location?:string; capacity:number; roomType?:string; }
export interface Camera extends CatalogBase { roomId:number; room?:string; status:string; lastHealthCheckAt?:string|null; lastHealthMessage?:string|null; rtspUrl?:string; sourceMode?:'RTSP'|'BROWSER'; }
export interface Video { id:number; fileName:string; contentType:string; sizeBytes:number; status:string; videoType:'INPUT_UPLOAD'|'ANNOTATED_OUTPUT'; sessionId?:number|null; sessionCode?:string|null; courseName?:string|null; sourceType:'UPLOAD'|'VIDEO'|'CAMERA'; parentVideoId?:number|null; parentVideoName?:string|null; isSystemGenerated:boolean; uploadedAt:string; uploadedByUserId?:number|null; }
export interface ManagementAssignment { id:number; teacherId?:number; positionType:string; facultyId?:number|null; departmentId?:number|null; effectiveFrom?:string; effectiveTo?:string|null; note?:string|null; isActive?:boolean; }
export interface SessionSubstitution { id:number; sessionId:number; originalTeacherId:number; originalTeacherName?:string; substituteTeacherId:number; substituteTeacherName?:string; assignedByTeacherId?:number|null; reason:string; status:string; assignedAt:string; cancelledAt?:string|null; completedAt?:string|null; note?:string|null; }
export interface Session { id:number; classSectionId:number; classSection?:string; course?:string; classScheduleId?:number|null; occurrenceDate?:string|null; startPeriod?:number|null; endPeriod?:number|null; roomId?:number|null; room?:string|null; scheduledStart:string; scheduledEnd?:string|null; startedAt?:string|null; endedAt?:string|null; status:string; cameraId?:number|null; videoId?:number|null; sourceConfigured?:boolean; isScheduled?:boolean; attendancePolicyId:number; originalTeacherId?:number; originalTeacherName?:string|null; actualTeacherId?:number; actualTeacherName?:string|null; substitution?:SessionSubstitution|null; activeSubstitute?:SessionSubstitution|null; activeSubstitution?:SessionSubstitution|null; hasActiveSubstitution?:boolean; isSubstituteTeaching?:boolean; canOperate?:boolean; canManage?:boolean; }
export interface AttendancePolicy { id:number; code:string; name:string; presentThreshold:number; partialThreshold:number; presentScore:number; partialScore:number; absentScore:number; version:string; isActive:boolean; }
export interface Alert { id:number; externalEventId?:string; sessionId:number; stableIdentityId?:number|null; studentId?:number|null; trackId?:string|null; type:string; severity?:'CRITICAL'|'WARNING'|'INFO'; source?:'RULE_ENGINE'|'AI_DIRECT'; behaviorEventId?:string|null; evidenceRef?:string|null; status:string; confidence:number; observationQuality:number; startedAt:string; endedAt?:string|null; durationSeconds?:number; createdAt:string; lecturerNote?:string|null; }
export interface AdminUser { id:number; userName:string; email:string; fullName:string; status:string; tokenVersion:number; lastLoginAt?:string|null; roles:string[]; }
export interface FaceEnrollment { id:number; studentId:number; status:string; requiredPoses:string; minAcceptedImages:number; acceptedImageCount:number; uploadedImageCount:number; startedAt:string; }
export interface Capabilities { developmentDataWarning?:string|null; web:boolean; backend:boolean; database:boolean; objectStorage:boolean; aiHealth:boolean; sessionInference:boolean; faceEnrollment:boolean; annotatedVideoOutput:boolean; loadedModels:string[]; llmProvider:string; modelIntegrationPending:boolean; }
export interface ApiErrorItem { line?:number; field?:string|null; message:string; }
export type StudentStatusFilter = 'all'|'active'|'inactive';

export interface AcademicTerm { isDevelopment?:boolean; status?:string; id:number; academicYear:string; semester:number; startsOn:string|null; endsOn:string|null; datesConfirmed:boolean; divisionPolicy:string; phasesConfirmed:boolean; weekOneMonday:string|null; weekMappingConfirmed:boolean; evidence:string|null; }
export interface TermPhase { id?:number; academicTermId:number; credits:number; number:number; startsOn:string; endsOn:string; }
export interface BellVersion { id:number; code:string; group:string; effectiveFrom:string; effectiveTo:string|null; confirmed:boolean; evidence:string; }
export interface BellPeriod { id?:number; bellTimeVersionId:number; number:number; shift:number; startsAt:string; endsAt:string; }
export interface AcademicConfiguration { terms:AcademicTerm[]; phases:TermPhase[]; bellVersions:BellVersion[]; periods:BellPeriod[]; exceptions:any[]; weekCalendars:any[]; }
export interface ScheduleLine { lineKey:string; isoDayOfWeek:number; startPeriod:number; endPeriod:number; bellGroup:string|null; roomId:number|null; activityType:string; startsOn:string; endsOn:string; selectedWeeks:number[]; room?:string; roomCode?:string; }
export interface ClassPermissions { isManager:boolean; canAddDetails:boolean; canEditDetails:boolean; canDeleteDetails:boolean; canManageStudents:boolean; canDeactivate:boolean; }
export interface Paged<T> { items:T[]; total:number; page:number; pageSize:number; }
