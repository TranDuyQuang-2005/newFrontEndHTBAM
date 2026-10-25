import {
  Component,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  Observable,
  catchError,
  forkJoin,
  of,
} from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import {
  ModalComponent,
  PageTitleComponent,
  RowActionMenuComponent,
  apiFieldErrors,
  errorText,
  statusText,
} from '../shared/ui';

type Tab =
  | 'faculties'
  | 'departments'
  | 'student-classes'
  | 'courses'
  | 'teachers'
  | 'class-sections'
  | 'rooms'
  | 'cameras'
  | 'roster';

@Component({
  standalone: true,
  imports: [
    FormsModule,
    ModalComponent,
    PageTitleComponent,
    RowActionMenuComponent,
  ],
  template: `
    <app-page-title
      title="Danh mục & lớp học"
      subtitle="Quản lý dữ liệu nền, lớp học phần và roster sinh viên."
    >
      @if (
        canCreateTab() &&
        tab() !== 'roster'
      ) {
        <button
          type="button"
          (click)="openEditor()"
        >
          + Thêm {{ label() }}
        </button>
      }
    </app-page-title>

    @if (error()) {
      <div class="error-box">
        {{ error() }}
      </div>
    }

    @for (refError of refErrorList(); track refError.key) {
      <div class="error-box">
        {{ refError.label }}: {{ refError.message }}
      </div>
    }

    @if (message()) {
      <div class="success-box">
        {{ message() }}
      </div>
    }

    <div class="tabs">
      @for (
        currentTab of tabs;
        track currentTab.key
      ) {
        <button
          type="button"
          [class.active]="
            tab() === currentTab.key
          "
          (click)="
            selectTab(
              currentTab.key
            )
          "
        >
          {{ currentTab.label }}
        </button>
      }
    </div>

    @if (tab() === 'roster') {
      <section class="card">
        <h3>Danh sách sinh viên lớp học phần</h3>

        <div class="toolbar">
          <label>
            Lớp học phần

            <select
              [(ngModel)]="
                rosterSectionId
              "
              (ngModelChange)="
                loadRoster()
              "
            >
              <option [ngValue]="null">
                -- Chọn --
              </option>

              @for (
                section of refs.sections;
                track section.id
              ) {
                <option
                  [ngValue]="
                    section.id
                  "
                >
                  {{ section.code }}
                  -
                  {{ section.name }}
                </option>
              }
            </select>
          </label>
        </div>

        @if (rosterSectionId) {
          <div class="filter-bar" style="padding:0;border:0;margin-top:16px">
            <label class="grow">Tìm sinh viên<input [(ngModel)]="rosterQuery" placeholder="MSSV hoặc họ tên…" /></label>
            @if (auth.hasPermission('ROSTER_MANAGE_SCOPE')) {
              <button type="button" class="secondary" (click)="selectAllRoster()">Chọn tất cả</button>
              <button type="button" class="secondary" (click)="clearRoster()">Bỏ chọn tất cả</button>
            }
          </div>
          <div class="roster-grid">
            <div class="roster-column">
              <h3>Sinh viên trong hệ thống ({{ availableRosterStudents().length }})</h3>
              @for (student of availableRosterStudents(); track student.id) {
                <label class="check"><input type="checkbox" [checked]="false" [disabled]="!auth.hasPermission('ROSTER_MANAGE_SCOPE')" (change)="toggleRoster(student.id, $event)" />{{ student.studentCode }} - {{ student.fullName }}</label>
              } @empty { <p class="empty">Không còn sinh viên phù hợp.</p> }
            </div>
            <div class="roster-column">
              <h3>Sinh viên thuộc lớp ({{ selectedRosterStudents().length }})</h3>
              @for (student of selectedRosterStudents(); track student.id) {
                <label class="check"><input type="checkbox" [checked]="true" [disabled]="!auth.hasPermission('ROSTER_MANAGE_SCOPE')" (change)="toggleRoster(student.id, $event)" />{{ student.studentCode }} - {{ student.fullName }}</label>
              } @empty { <p class="empty">Chưa chọn sinh viên.</p> }
            </div>
          </div>

          @if (auth.hasPermission('ROSTER_MANAGE_SCOPE')) {
            <button
              type="button"
              [disabled]="saving()"
              (click)="saveRoster()"
            >
              {{ saving() ? 'Đang lưu…' : 'Lưu roster' }}
            </button>
          }
        }
      </section>
    } @else {
      <div class="filter-bar">
        <label class="grow">Tìm kiếm<input [(ngModel)]="managementSearch" [placeholder]="'Tìm trong ' + label().toLowerCase() + '…'" /></label>
      </div>
      <section class="card table-card">
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Mã</th>
                <th>Tên / Họ tên</th>
                <th>Thông tin</th>
                <th>Ghi chú</th>
                <th>Trạng thái</th>
                <th>Thao tác</th>
              </tr>
            </thead>

            <tbody>
              @for (
                item of visibleData();
                track item.id
              ) {
                <tr>
                  <td>
                    {{
                      item.code ||
                        item.teacherCode
                    }}
                  </td>

                  <td>
                    {{
                      item.name ||
                        item.fullName
                    }}
                  </td>

                  <td>
                    {{ info(item) }}
                  </td>

                  <td>
                    <span
                      class="clamp"
                      [title]="
                        item.note || ''
                      "
                    >
                      {{
                        item.note || '—'
                      }}
                    </span>
                  </td>

                  <td>
                    <span
                      class="badge"
                      [class.inactive]="
                        item.isActive ===
                        false
                      "
                    >
                      {{
                        item.isActive ===
                        false
                          ? 'Ngừng hoạt động'
                          : statusText(item.status || 'ACTIVE')
                      }}
                    </span>
                  </td>

                  <td class="menu-cell">
                    @if (hasRowAction(item)) {
                      <app-row-action-menu>
                        @if (canEditTab()) {
                        <button
                          type="button"
                          (click)="
                            openEditor(item)
                          "
                        >
                          Sửa
                        </button>
                        }

                        @if (
                          tab() ===
                          'cameras' &&
                          canTestCamera()
                        ) {
                          <button
                            type="button"
                            (click)="
                              testCamera(
                                item
                              )
                            "
                          >
                            Test RTSP
                          </button>
                        }

                        @if (
                          item.isActive !==
                          false &&
                          canDeactivateTab()
                        ) {
                          <button
                            type="button"
                            class="danger"
                            (click)="
                              askDeactivate(
                                item
                              )
                            "
                          >
                            Ngừng
                          </button>
                        }

                        @if (
                          item.isActive === false &&
                          canDeactivateTab()
                        ) {
                          <button
                            type="button"
                            (click)="reactivate(item)"
                          >
                            Kích hoạt lại
                          </button>
                        }
                      </app-row-action-menu>
                    }
                  </td>
                </tr>
              } @empty {
                <tr>
                  <td
                    colspan="6"
                    class="empty"
                  >
                    Chưa có dữ liệu.
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </section>
    }

    @if (editorOpen()) {
      <app-modal
        [title]="
          (editingId()
            ? 'Sửa '
            : 'Thêm ') +
          label()
        "
        [busy]="saving()"
        [size]="tab() === 'class-sections' || tab() === 'cameras' ? 'lg' : 'md'"
        (close)="closeEditor()"
      >
        <form
          class="form-grid"
          (ngSubmit)="saveCatalog()"
        >
          @if (editorError()) {
            <div class="error-box span2">{{ editorError() }}</div>
          }
          <label>
            {{
              tab() === 'teachers'
                ? 'Mã GV'
                : 'Mã'
            }}
            *

            <input
              [(ngModel)]="
                form[
                  tab() === 'teachers'
                    ? 'teacherCode'
                    : 'code'
                ]
              "
              [ngModelOptions]="{
                standalone: true
              }"
              required
              (input)="clearEditorFieldError(tab() === 'teachers' ? 'teacherCode' : 'code')"
            />
            @if (editorFieldErrors()[tab() === 'teachers' ? 'teacherCode' : 'code']) {
              <small class="field-error">{{ editorFieldErrors()[tab() === 'teachers' ? 'teacherCode' : 'code'] }}</small>
            }
          </label>

          <label>
            {{
              tab() === 'teachers'
                ? 'Họ tên'
                : 'Tên'
            }}
            *

            <input
              [(ngModel)]="
                form[
                  tab() === 'teachers'
                    ? 'fullName'
                    : 'name'
                ]
              "
              [ngModelOptions]="{
                standalone: true
              }"
              required
            />
          </label>

          @if (
            tab() === 'departments' ||
            tab() === 'student-classes'
          ) {
            <label>
              Khoa *

              <select
                [(ngModel)]="
                  form.facultyId
                "
                [ngModelOptions]="{
                  standalone: true
                }"
              >
                <option
                  [ngValue]="null"
                >
                  -- Chọn --
                </option>

                @for (
                  faculty of
                    refs.faculties;
                  track faculty.id
                ) {
                  <option
                    [ngValue]="
                      faculty.id
                    "
                  >
                    {{ faculty.code }}
                    -
                    {{ faculty.name }}
                  </option>
                }
              </select>
            </label>
          }

          @if (
            tab() ===
            'student-classes'
          ) {
            <label>
              Năm bắt đầu

              <input
                type="number"
                [(ngModel)]="
                  form.startYear
                "
                [ngModelOptions]="{
                  standalone: true
                }"
              />
            </label>
          }

          @if (
            tab() === 'courses' ||
            tab() === 'teachers'
          ) {
            <label>
              Bộ môn

              <select
                [(ngModel)]="
                  form.departmentId
                "
                [ngModelOptions]="{
                  standalone: true
                }"
              >
                <option
                  [ngValue]="null"
                >
                  -- Chọn --
                </option>

                @for (
                  department of
                    refs.departments;
                  track department.id
                ) {
                  <option
                    [ngValue]="
                      department.id
                    "
                  >
                    {{
                      department.code
                    }}
                    -
                    {{
                      department.name
                    }}
                  </option>
                }
              </select>
            </label>
          }

          @if (
            tab() === 'courses'
          ) {
            <label>
              Tín chỉ

              <input
                type="number"
                min="1"
                [(ngModel)]="
                  form.credits
                "
                [ngModelOptions]="{
                  standalone: true
                }"
              />
            </label>
          }

          @if (
            tab() === 'teachers'
          ) {
            <label>
              Email

              <input
                type="email"
                [(ngModel)]="
                  form.email
                "
                [ngModelOptions]="{
                  standalone: true
                }"
              />
            </label>

            <label>
              Mã tài khoản liên kết

              <input
                type="number"
                [(ngModel)]="
                  form.userId
                "
                [ngModelOptions]="{
                  standalone: true
                }"
                (input)="clearEditorFieldError('userId')"
              />
              @if (editorFieldErrors()['userId']) {
                <small class="field-error">{{ editorFieldErrors()['userId'] }}</small>
              }
            </label>
          }

          @if (
            tab() ===
            'class-sections'
          ) {
            <label>
              Môn học *

              <select
                [(ngModel)]="
                  form.courseId
                "
                [ngModelOptions]="{
                  standalone: true
                }"
              >
                <option
                  [ngValue]="null"
                >
                  -- Chọn --
                </option>

                @for (
                  course of refs.courses;
                  track course.id
                ) {
                  <option
                    [ngValue]="
                      course.id
                    "
                  >
                    {{ course.code }}
                    -
                    {{ course.name }}
                  </option>
                }
              </select>
            </label>

            <label>
              Giảng viên *

              <select
                [(ngModel)]="
                  form.teacherId
                "
                [ngModelOptions]="{
                  standalone: true
                }"
              >
                <option
                  [ngValue]="null"
                >
                  -- Chọn --
                </option>

                @for (
                  teacher of
                    refs.teachers;
                  track teacher.id
                ) {
                  <option
                    [ngValue]="
                      teacher.id
                    "
                  >
                    {{
                      teacher.teacherCode
                    }}
                    -
                    {{
                      teacher.fullName
                    }}
                  </option>
                }
              </select>
            </label>

            <label>
              Học kỳ *

              <input
                [(ngModel)]="
                  form.semester
                "
                [ngModelOptions]="{
                  standalone: true
                }"
              />
            </label>

            <label>
              Năm học *

              <input
                [(ngModel)]="
                  form.academicYear
                "
                [ngModelOptions]="{
                  standalone: true
                }"
              />
            </label>
          }

          @if (tab() === 'rooms') {
            <label>
              Vị trí

              <input
                [(ngModel)]="
                  form.location
                "
                [ngModelOptions]="{
                  standalone: true
                }"
              />
            </label>

            <label>
              Sức chứa

              <input
                type="number"
                min="0"
                [(ngModel)]="
                  form.capacity
                "
                [ngModelOptions]="{
                  standalone: true
                }"
              />
            </label>
          }

          @if (
            tab() === 'cameras'
          ) {
            <label>
              Phòng *

              <select
                [(ngModel)]="
                  form.roomId
                "
                [ngModelOptions]="{
                  standalone: true
                }"
              >
                <option
                  [ngValue]="null"
                >
                  -- Chọn --
                </option>

                @for (
                  room of refs.rooms;
                  track room.id
                ) {
                  <option
                    [ngValue]="
                      room.id
                    "
                  >
                    {{ room.code }}
                    -
                    {{ room.name }}
                  </option>
                }
              </select>
            </label>

            <label class="span2">
              Nguồn camera

              <input
                [(ngModel)]="
                  form.rtspUrl
                "
                [ngModelOptions]="{
                  standalone: true
                }"
                placeholder="rtsp://... hoặc browser://laptop"
              />
            </label>
          }

          <label class="span2">
            Ghi chú

            <textarea
              rows="4"
              [(ngModel)]="form.note"
              [ngModelOptions]="{
                standalone: true
              }"
            ></textarea>
          </label>

          <label class="check">
            <input
              type="checkbox"
              [(ngModel)]="
                form.isActive
              "
              [ngModelOptions]="{
                standalone: true
              }"
            />
            Hoạt động
          </label>

          <div class="form-actions">
            <button
              type="submit"
              [disabled]="saving()"
            >
              {{
                saving()
                  ? 'Đang lưu…'
                  : 'Lưu'
              }}
            </button>

            <button
              type="button"
              class="secondary"
              [disabled]="saving()"
              (click)="closeEditor()"
            >
              Hủy
            </button>
          </div>
        </form>
      </app-modal>
    }

    @if (pendingDeactivate()) {
      <app-modal
        title="Xác nhận ngừng hoạt động"
        [busy]="saving()"
        (close)="
          cancelDeactivate()
        "
      >
        <p>
          Bạn có chắc muốn ngừng hoạt động
          <b>
            {{
              pendingDeactivate()
                ?.code ||
              pendingDeactivate()
                ?.teacherCode
            }}
          </b>
          không?
        </p>

        <div class="form-actions">
          <button
            type="button"
            class="danger"
            [disabled]="saving()"
            (click)="
              confirmDeactivate()
            "
          >
            Xác nhận
          </button>

          <button
            type="button"
            class="secondary"
            [disabled]="saving()"
            (click)="
              cancelDeactivate()
            "
          >
            Hủy
          </button>
        </div>
      </app-modal>
    }
  `,
})
export class ManagementComponent
  implements OnInit
{
  private readonly api =
    inject(ApiService);

  readonly auth =
    inject(AuthService);

  readonly statusText = statusText;

  readonly tabs: {
    key: Tab;
    label: string;
  }[] = [
    {
      key: 'faculties',
      label: 'Khoa',
    },
    {
      key: 'departments',
      label: 'Bộ môn',
    },
    {
      key: 'student-classes',
      label: 'Lớp sinh hoạt',
    },
    {
      key: 'courses',
      label: 'Môn học',
    },
    {
      key: 'teachers',
      label: 'Giảng viên',
    },
    {
      key: 'class-sections',
      label: 'Lớp học phần',
    },
    {
      key: 'rooms',
      label: 'Phòng học',
    },
    {
      key: 'cameras',
      label: 'Camera',
    },
    {
      key: 'roster',
      label: 'Danh sách lớp học phần',
    },
  ];

  readonly tab =
    signal<Tab>('faculties');

  readonly data =
    signal<any[]>([]);

  readonly error = signal('');
  readonly message = signal('');
  readonly editorError = signal('');
  readonly editorFieldErrors = signal<Record<string, string>>({});
  readonly refErrors =
    signal<Record<string, string>>({});

  readonly editorOpen =
    signal(false);

  readonly editingId =
    signal<number | null>(null);

  readonly saving =
    signal(false);

  readonly pendingDeactivate =
    signal<any | null>(null);

  form: any = {
    isActive: true,
    note: '',
  };

  refs: any = {
    faculties: [],
    departments: [],
    classes: [],
    courses: [],
    teachers: [],
    sections: [],
    rooms: [],
    cameras: [],
    students: [],
  };

  rosterSectionId:
    | number
    | null = null;

  managementSearch = '';
  rosterQuery = '';

  readonly rosterIds =
    signal(new Set<number>());

  visibleData(): any[] {
    const key = this.managementSearch.trim().toLowerCase();
    if (!key) return this.data();
    return this.data().filter((item) =>
      [item.code, item.teacherCode, item.name, item.fullName, item.email, item.note]
        .filter(Boolean)
        .some((value) => String(value).toLowerCase().includes(key)),
    );
  }

  availableRosterStudents(): any[] {
    const key = this.rosterQuery.trim().toLowerCase();
    return this.refs.students.filter((student: any) =>
      !this.rosterIds().has(student.id) &&
      (!key || `${student.studentCode} ${student.fullName}`.toLowerCase().includes(key)),
    );
  }

  selectedRosterStudents(): any[] {
    const key = this.rosterQuery.trim().toLowerCase();
    return this.refs.students.filter((student: any) =>
      this.rosterIds().has(student.id) &&
      (!key || `${student.studentCode} ${student.fullName}`.toLowerCase().includes(key)),
    );
  }

  selectAllRoster(): void {
    if (
      !this.auth.hasPermission(
        'ROSTER_MANAGE_SCOPE',
      )
    ) {
      return;
    }

    this.rosterIds.set(new Set(this.refs.students.map((student: any) => student.id)));
  }

  clearRoster(): void {
    if (
      !this.auth.hasPermission(
        'ROSTER_MANAGE_SCOPE',
      )
    ) {
      return;
    }

    this.rosterIds.set(new Set<number>());
  }

  ngOnInit(): void {
    this.loadAll();
  }

  label(): string {
    return (
      this.tabs.find(
        (item) =>
          item.key === this.tab(),
      )?.label ?? ''
    );
  }

  refErrorList(): {
    key: string;
    label: string;
    message: string;
  }[] {
    const labels: Record<string, string> = {
      faculties: 'Khoa',
      departments: 'Bộ môn',
      classes: 'Lớp sinh hoạt',
      courses: 'Môn học',
      teachers: 'Giảng viên',
      sections: 'Lớp học phần',
      rooms: 'Phòng học',
      cameras: 'Camera',
      students: 'Sinh viên',
    };

    return Object.entries(
      this.refErrors(),
    ).map(([key, message]) => ({
      key,
      label: labels[key] ?? key,
      message,
    }));
  }

  canCreateTab(): boolean {
    const permission: Partial<
      Record<Tab, string>
    > = {
      departments:
        'DEPARTMENT_MANAGE',
      'student-classes':
        'STUDENT_CLASS_MANAGE',
      courses:
        'DEPARTMENT_DATA_MANAGE',
      teachers:
        'DEPARTMENT_DATA_MANAGE',
      'class-sections':
        'CLASS_SECTION_CREATE',
      rooms: 'ROOM_MANAGE',
      cameras: 'CAMERA_MANAGE',
    };

    const required =
      permission[this.tab()];
    return !!required &&
      this.auth.hasPermission(
        required,
      );
  }

  canEditTab(): boolean {
    const permission: Partial<
      Record<Tab, string>
    > = {
      faculties: 'FACULTY_UPDATE',
      departments:
        'DEPARTMENT_MANAGE',
      'student-classes':
        'STUDENT_CLASS_MANAGE',
      courses:
        'DEPARTMENT_DATA_MANAGE',
      teachers:
        'DEPARTMENT_DATA_MANAGE',
      'class-sections':
        'CLASS_SECTION_UPDATE_SCOPE',
      rooms: 'ROOM_MANAGE',
      cameras: 'CAMERA_MANAGE',
    };

    const required =
      permission[this.tab()];
    return !!required &&
      this.auth.hasPermission(
        required,
      );
  }

  canDeactivateTab(): boolean {
    if (
      this.tab() ===
      'class-sections'
    ) {
      return this.auth.hasPermission(
        'CLASS_SECTION_DEACTIVATE_SCOPE',
      );
    }

    if (
      this.tab() === 'rooms' ||
      this.tab() === 'cameras'
    ) {
      return this.auth.hasPermission(
        'TECH_CATALOG_DEACTIVATE',
      );
    }

    return this.canEditTab();
  }

  canTestCamera(): boolean {
    return this.auth.hasPermission(
      'CAMERA_TEST',
    );
  }

  hasRowAction(item: any): boolean {
    return (
      this.canEditTab() ||
      this.canDeactivateTab() ||
      (this.tab() === 'cameras' &&
        this.canTestCamera())
    );
  }

  selectTab(tab: Tab): void {
    this.tab.set(tab);
    this.editorOpen.set(false);
    this.pendingDeactivate.set(null);
    this.refreshData();

    if (
      tab === 'roster' &&
      !this.refs.students.length
    ) {
      this.api
        .students('', 'active')
        .subscribe({
          next: (students) => {
            this.refs.students =
              students;
          },
          error: (error) => {
            this.refErrors.update(
              (errors) => ({
                ...errors,
                students:
                  errorText(error),
              }),
            );
          },
        });
    }
  }

  loadAll(): void {
    this.error.set('');
    this.refErrors.set({});

    forkJoin({
      faculties:
        this.withFallback(
          'faculties',
          [],
          this.api.faculties(),
        ),

      departments:
        this.withFallback(
          'departments',
          [],
          this.api.departments(),
        ),

      classes:
        this.withFallback(
          'classes',
          [],
          this.api.studentClasses(),
        ),

      courses:
        this.withFallback(
          'courses',
          [],
          this.api.courses(),
        ),

      teachers:
        this.withFallback(
          'teachers',
          [],
          this.api.teachers(),
        ),

      sections:
        this.withFallback(
          'sections',
          [],
          this.api.classSections(),
        ),

      rooms:
        this.withFallback(
          'rooms',
          [],
          this.api.rooms(),
        ),

      cameras:
        this.withFallback(
          'cameras',
          [],
          this.api.cameras(),
        ),

      students:
        this.withFallback(
          'students',
          [],
          this.api.students(
            '',
            'active',
          ),
        ),
    }).subscribe({
      next: (result) => {
        this.refs = {
          faculties:
            result.faculties,

          departments:
            result.departments,

          classes:
            result.classes,

          courses:
            result.courses,

          teachers:
            result.teachers,

          sections:
            result.sections,

          rooms:
            result.rooms,

          cameras:
            result.cameras,

          students:
            result.students,
        };

        this.refreshData();
      },

      error: (error) =>
        this.error.set(
          errorText(error),
        ),
    });
  }

  refreshData(): void {
    const map: Record<
      string,
      any[]
    > = {
      faculties:
        this.refs.faculties,

      departments:
        this.refs.departments,

      'student-classes':
        this.refs.classes,

      courses:
        this.refs.courses,

      teachers:
        this.refs.teachers,

      'class-sections':
        this.refs.sections,

      rooms:
        this.refs.rooms,

      cameras:
        this.refs.cameras,
    };

    this.data.set(
      map[this.tab()] ?? [],
    );
  }

  openEditor(item?: any): void {
    if (
      item
        ? !this.canEditTab()
        : !this.canCreateTab()
    ) {
      return;
    }

    this.error.set('');
    this.message.set('');
    this.editorError.set('');
    this.editorFieldErrors.set({});

    this.editingId.set(
      item?.id ?? null,
    );

    this.form = item
      ? {
          ...item,
        }
      : this.emptyForm();

    this.editorOpen.set(true);
  }

  closeEditor(): void {
    if (!this.saving()) {
      this.editorOpen.set(false);
    }
  }

  emptyForm(): any {
    return {
      isActive: true,
      note: '',
      credits: 3,
      capacity: 0,
      facultyId: null,
      departmentId: null,
      courseId: null,
      teacherId: null,
      roomId: null,
      semester: '',
      academicYear: '',
    };
  }

  saveCatalog(): void {
    if (
      this.editingId() !== null
        ? !this.canEditTab()
        : !this.canCreateTab()
    ) {
      return;
    }

    const validationError =
      this.validateCatalog();

    if (validationError) {
      this.editorError.set(
        validationError,
      );
      return;
    }

    this.saving.set(true);
    this.editorError.set('');
    this.editorFieldErrors.set({});
    this.message.set('');

    this.api
      .saveCatalog(
        this.tab(),
        this.editingId(),
        this.form,
      )
      .subscribe({
        next: () => {
          this.message.set(
            `Đã lưu ${this.label()}.`,
          );

          this.editorOpen.set(
            false,
          );

          this.saving.set(false);
          this.loadAll();
        },

        error: (error) => {
          this.editorFieldErrors.set(apiFieldErrors(error));
          this.editorError.set(
            errorText(error),
          );

          this.saving.set(false);
        },
      });
  }

  validateCatalog(): string | null {
    const isTeacher =
      this.tab() === 'teachers';

    const code = String(
      isTeacher
        ? this.form.teacherCode ?? ''
        : this.form.code ?? '',
    ).trim();

    const name = String(
      isTeacher
        ? this.form.fullName ?? ''
        : this.form.name ?? '',
    ).trim();

    if (!code) {
      return 'Mã là bắt buộc.';
    }

    if (!name) {
      return 'Tên/Họ tên là bắt buộc.';
    }

    if (
      (
        this.tab() ===
          'departments' ||
        this.tab() ===
          'student-classes'
      ) &&
      !this.form.facultyId
    ) {
      return 'Vui lòng chọn Khoa.';
    }

    if (
      (
        this.tab() ===
          'courses' ||
        this.tab() ===
          'teachers'
      ) &&
      !this.form.departmentId
    ) {
      return 'Vui lòng chọn Bộ môn.';
    }

    if (
      this.tab() === 'teachers' &&
      !String(
        this.form.email ?? '',
      ).trim()
    ) {
      return 'Email là bắt buộc.';
    }

    if (
      this.tab() ===
        'class-sections'
    ) {
      if (!this.form.courseId) {
        return 'Vui lòng chọn Môn học.';
      }

      if (!this.form.teacherId) {
        return 'Vui lòng chọn Giảng viên.';
      }

      if (
        !String(
          this.form.semester ??
            '',
        ).trim()
      ) {
        return 'Học kỳ là bắt buộc.';
      }

      if (
        !String(
          this.form.academicYear ??
            '',
        ).trim()
      ) {
        return 'Năm học là bắt buộc.';
      }
    }

    if (
      this.tab() === 'rooms' &&
      Number(this.form.capacity) < 0
    ) {
      return 'Sức chứa phải lớn hơn hoặc bằng 0.';
    }

    if (
      this.tab() === 'cameras' &&
      !this.form.roomId
    ) {
      return 'Vui lòng chọn Phòng.';
    }

    if (
      this.tab() === 'cameras' &&
      !String(
        this.form.rtspUrl ?? '',
      ).trim()
    ) {
      return 'Nguồn camera là bắt buộc (RTSP hoặc browser://laptop).';
    }

    return null;
  }

  askDeactivate(item: any): void {
    if (!this.canDeactivateTab()) {
      return;
    }

    this.pendingDeactivate.set(
      item,
    );
  }

  cancelDeactivate(): void {
    if (!this.saving()) {
      this.pendingDeactivate.set(
        null,
      );
    }
  }

  confirmDeactivate(): void {
    const item =
      this.pendingDeactivate();

    if (
      !item ||
      !this.canDeactivateTab()
    ) {
      return;
    }

    this.saving.set(true);

    this.api
      .deactivateCatalog(
        this.tab(),
        item.id,
      )
      .subscribe({
        next: () => {
          this.pendingDeactivate.set(
            null,
          );

          this.saving.set(false);

          this.message.set(
            `Đã ngừng hoạt động ${this.label()}.`,
          );

          this.loadAll();
        },

        error: (error) => {
          this.error.set(
            errorText(error),
          );

          this.saving.set(false);
        },
      });
  }

  reactivate(item: any): void {
    if (!this.canDeactivateTab()) return;
    this.saving.set(true);
    this.error.set('');
    this.api.reactivateCatalog(this.tab(), item.id).subscribe({
      next: () => {
        this.saving.set(false);
        this.message.set(`Đã kích hoạt lại ${this.label()}.`);
        this.loadAll();
      },
      error: (error) => {
        this.saving.set(false);
        this.error.set(errorText(error));
      },
    });
  }

  testCamera(item: any): void {
    if (!this.canTestCamera()) {
      return;
    }

    this.api
      .testCamera(item.id)
      .subscribe({
        next: (result) => {
          this.message.set(
            `Camera ${item.code}: ${statusText(result.status)} — ${result.lastHealthMessage ?? ''}`,
          );

          this.loadAll();
        },

        error: (error) =>
          this.error.set(
            errorText(error),
          ),
      });
  }

  info(item: any): string {
    switch (this.tab()) {
      case 'departments':
        return item.faculty ?? '—';

      case 'student-classes':
        return `${item.faculty ?? ''} ${
          item.startYear
            ? '• ' + item.startYear
            : ''
        }`;

      case 'courses':
        return `${item.credits ?? 0} tín chỉ`;

      case 'teachers':
        return item.email ?? '—';

      case 'class-sections':
        return `${item.course ?? ''} • ${item.teacher ?? ''} • ${item.semester ?? ''} ${item.academicYear ?? ''}`;

      case 'rooms':
        return `${item.location ?? ''} • ${item.capacity ?? 0} chỗ`;

      case 'cameras':
        return `${item.room ?? ''} • ${item.lastHealthMessage ?? 'Chưa test'}`;

      default:
        return '—';
    }
  }

  loadRoster(): void {
    if (!this.rosterSectionId) {
      this.rosterIds.set(
        new Set<number>(),
      );
      return;
    }

    this.api
      .roster(
        this.rosterSectionId,
      )
      .subscribe({
        next: (rows) => {
          this.rosterIds.set(
            new Set(
              rows.map(
                (row) =>
                  row.studentId,
              ),
            ),
          );
        },
        error: (error) => {
          this.error.set(
            errorText(error),
          );
        },
      });
  }

  toggleRoster(
    studentId: number,
    event: Event,
  ): void {
    if (
      !this.auth.hasPermission(
        'ROSTER_MANAGE_SCOPE',
      )
    ) {
      return;
    }

    const selected =
      new Set(this.rosterIds());

    const checked =
      (
        event.target as HTMLInputElement
      ).checked;

    if (checked) {
      selected.add(studentId);
    } else {
      selected.delete(studentId);
    }

    this.rosterIds.set(selected);
  }

  saveRoster(): void {
    if (
      !this.rosterSectionId ||
      !this.auth.hasPermission(
        'ROSTER_MANAGE_SCOPE',
      )
    ) {
      return;
    }

    this.saving.set(true);
    this.api
      .setRoster(
        this.rosterSectionId,
        [...this.rosterIds()],
      )
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.message.set(
            'Đã lưu danh sách sinh viên lớp học phần.',
          );
        },

        error: (error) => {
          this.saving.set(false);
          this.error.set(
            errorText(error),
          );
        },
      });
  }

  clearEditorFieldError(field: string): void {
    this.editorFieldErrors.update((errors) => {
      if (!errors[field]) return errors;
      const next = { ...errors };
      delete next[field];
      return next;
    });
  }

  private withFallback<T>(
    key: string,
    fallback: T,
    source: Observable<T>,
  ): Observable<T> {
    return source.pipe(
      catchError((error) => {
        this.refErrors.update(
          (errors) => ({
            ...errors,
            [key]:
              errorText(error),
          }),
        );
        return of(fallback);
      }),
    );
  }
}
