import {
  Component,
  OnInit,
  OnDestroy,
  DestroyRef,
  inject,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ClassSectionsComponent } from './class-sections.component';
import { CatalogTab as Tab, CATALOG_TABS, resolveCatalogTab, visibleCatalogTabs } from '../shared/catalog-navigation';
import { SearchSelectComponent } from '../shared/search-select.component';
import { CatalogSectionHeaderComponent } from '../shared/catalog-section-header.component';
import {
  Observable,
  catchError,
  forkJoin,
  of,
  Subscription,
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

@Component({
  standalone: true,
  imports: [
    FormsModule, SearchSelectComponent, ClassSectionsComponent, CatalogSectionHeaderComponent,
    ModalComponent,
    PageTitleComponent,
    RowActionMenuComponent,
  ],
  template: `
    <app-page-title
      title="Danh mục dữ liệu"
      subtitle="Quản lý dữ liệu nền trong phạm vi được giao."
    />

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

    <div class="tabs catalog-tabs" role="tablist" aria-label="Danh mục dữ liệu">
      @for (
        currentTab of visibleTabs();
        track currentTab.key
      ) {
        <button
          type="button"
          role="tab"
          [id]="'catalog-tab-' + currentTab.key"
          [attr.aria-selected]="tab() === currentTab.key"
          [attr.aria-controls]="'catalog-panel-' + currentTab.key"
          [attr.tabindex]="tab() === currentTab.key ? 0 : -1"
          (keydown)="tabKey($event, currentTab.key)"
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

    <div role="tabpanel" [id]="'catalog-panel-' + tab()" [attr.aria-labelledby]="'catalog-tab-' + tab()">
    @if(tab() === 'class-sections'){
      <app-class-sections />
    } @else {
      <app-catalog-section-header [title]="label()" [description]="sectionDescription()">
        @if(canCreateTab() && tab() !== 'roster'){<button type="button" (click)="openEditor()">+ Thêm {{label()}}</button>}
      </app-catalog-section-header>
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
                    @if(tab() === 'teachers' && legacyTeacherCode(item.teacherCode)){<small class="badge">Mã cũ</small>}
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
    </div>

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
          [class.course-catalog-form]="tab() === 'courses'"
          [class.catalog-editor-form]="tab() === 'courses' || tab() === 'departments' || tab() === 'teachers'"
          (ngSubmit)="saveCatalog()"
        >
          @if (editorError()) {
            <div class="error-box span2">{{ editorError() }}</div>
          }
          @if(tab() === 'courses'){
            <label class="span2">Tên môn học *<input type="text" name="courseName" [(ngModel)]="form.name" required maxlength="256" /></label>
            <label class="course-department">Bộ môn *<app-search-select name="courseDepartment" resource="departments" label="Bộ môn" [boundToModal]="true" [constrainWidth]="true" [markLegacyDepartments]="true" [ngModel]="form.departmentId" (ngModelChange)="courseDepartmentChanged($event)" [disabled]="saving() || !!editingId()" /></label>
            <label>Tín chỉ *<input name="courseCredits" type="number" min="1" max="20" [(ngModel)]="form.credits" required /></label>
            <section class="catalog-code-info span2" aria-label="Mã môn học"><strong>Mã môn học</strong><p>{{editingId() ? form.code : 'Tự động tạo sau khi chọn Bộ môn và lưu'}}</p>
              @if(editingId()){<small>Bộ môn và mã đã cấp được giữ nguyên khi sửa.</small>}
              @if(codePreview()){<p role="status">Mã dự kiến: {{codePreview()}} <small>Chỉ tham khảo; backend cấp mã chính thức khi lưu.</small></p>}
              @if(codePreviewLoading()){<p role="status">Đang lấy mã dự kiến…</p>}
              @if(departmentCodeError()){<p class="field-error" role="alert">{{departmentCodeError()}}</p>}
            </section>
          } @else {
          @if(tab() === 'teachers'){<section class="catalog-code-info span2" aria-label="Mã giảng viên"><strong>Mã giảng viên</strong><p>{{editingId()?form.teacherCode:'Tự động tạo khi lưu'}}</p>
            @if(codePreview()){<p role="status">Mã dự kiến: {{codePreview()}} <small>Chỉ tham khảo; backend cấp mã chính thức khi lưu.</small></p>}
            @if(codePreviewLoading()){<p role="status">Đang lấy mã dự kiến…</p>}
            @if(departmentCodeError()){<p class="field-error" role="alert">{{departmentCodeError()}}</p>}
            @if(editingId() && form.departmentId !== originalTeacherDepartmentId){<p class="notice">Mã giảng viên đã cấp sẽ được giữ nguyên để bảo toàn dữ liệu lịch sử.</p>}
          </section>}
          @else if(tab() === 'departments' && !editingId()){<div class="catalog-code-info span2"><strong>Mã bộ môn</strong><p>Tự động tạo khi lưu</p></div>}
          @else {<label>Mã nội bộ<input [value]="(tab() === 'teachers' ? form.teacherCode : form.code) || 'Tự động tạo khi lưu'" readonly /></label>}

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
          }

          @if (tab() === 'faculties' && !editingId()) {
            <label>Bộ môn đầu tiên *<input name="initialDepartmentName" [(ngModel)]="form.initialDepartmentName" maxlength="256" required /></label>
            <label>Trưởng khoa đầu tiên *<app-search-select name="initialHeadTeacherId" resource="faculty-initial-heads" label="Trưởng khoa đầu tiên" [(ngModel)]="form.initialHeadTeacherId" /></label>
            <p class="notice span2">Giảng viên được chọn sẽ chuyển về bộ môn mới và được bổ nhiệm Trưởng khoa. Chỉ chọn hồ sơ có sẵn chưa có phân công giảng dạy hoặc chức vụ còn hiệu lực; không tạo thêm hồ sơ giảng viên.</p>
          }

          @if (
            tab() === 'departments' ||
            tab() === 'student-classes'
          ) {
            <label>
              Khoa *

              <app-search-select resource="faculties" label="Khoa" [(ngModel)]="form.facultyId" [ngModelOptions]="{standalone:true}" />
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
            tab() === 'teachers'
          ) {
            <label>
              Bộ môn

              <app-search-select resource="departments" label="Bộ môn" [boundToModal]="true" [constrainWidth]="true" [markLegacyDepartments]="true" [ngModel]="form.departmentId" (ngModelChange)="teacherDepartmentChanged($event)" [ngModelOptions]="{standalone:true}" />
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
              Tài khoản giảng viên liên kết

              <app-search-select resource="users" label="Tài khoản giảng viên liên kết" [boundToModal]="true" [constrainWidth]="true" [(ngModel)]="form.userId" [ngModelOptions]="{standalone:true}" [filters]="{departmentId:form.departmentId}" [disabled]="!!editingId() || !form.departmentId || !!departmentCodeError()" />
              @if (editorFieldErrors()['userId']) {
                <small class="field-error">{{ editorFieldErrors()['userId'] }}</small>
              }
            </label>
          }

          @if (tab() === 'rooms') {
            <label>Loại phòng<select [(ngModel)]="form.roomType" [ngModelOptions]="{standalone:true}"><option value="UNKNOWN">Chưa xác định</option><option value="THEORY">Phòng lý thuyết</option><option value="ZOOM">Phòng Zoom</option><option value="COMPUTER">Phòng máy tính</option><option value="LAB">Phòng thí nghiệm</option><option value="PRACTICE">Phòng thực hành</option><option value="PE">Giáo dục thể chất</option><option value="DEFENSE">Quốc phòng – an ninh</option></select></label>
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

              <app-search-select resource="rooms" label="Phòng học" [(ngModel)]="form.roomId" [ngModelOptions]="{standalone:true}" />
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
              [rows]="tab() === 'courses' ? 2 : 4"
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

          <div class="form-actions" [class.catalog-footer]="tab() === 'courses' || tab() === 'departments' || tab() === 'teachers'">
            <button
              type="submit"
              [disabled]="saving() || ((tab() === 'courses' || tab() === 'teachers') && !editingId() && (!form.departmentId || !!departmentCodeError() || codePreviewLoading()))"
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
  implements OnInit, OnDestroy
{
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  private catalogRequest?: Subscription;
  private readonly api =
    inject(ApiService);

  readonly auth =
    inject(AuthService);

  readonly statusText = statusText;

  readonly tabs = CATALOG_TABS;

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
  readonly codePreview=signal('');readonly codePreviewLoading=signal(false);readonly departmentCodeError=signal('');
  private codePreviewRequest?:Subscription;
  originalTeacherDepartmentId:number|null=null;
  sectionDescription():string{return CATALOG_TABS.find(x=>x.key===this.tab())?.description??'';}
  legacyTeacherCode(code:string):boolean{return !/^GV-[A-Z0-9]+(?:-[0-9]{2})?-[0-9]{4}$/.test(code??'');}
  teacherDepartmentChanged(id:number|null):void{
    this.codePreviewRequest?.unsubscribe();this.form.departmentId=id;this.codePreview.set('');this.departmentCodeError.set('');this.codePreviewLoading.set(false);
    if(this.editingId())return;
    this.form.userId=null;
    if(!id)return;
    const department=this.refs.departments.find((x:any)=>x.id===id);
    if(!department||!/^BM-[A-Z0-9]+(?:-[0-9]{2})?$/.test(department.code)||/^BM-[A-F0-9]{32}(?:-[0-9]{2})?$/.test(department.code)||department.code.length>36){this.departmentCodeError.set('Bộ môn chưa có mã hợp lệ. Vui lòng chuẩn hóa mã bộ môn trước khi thêm giảng viên.');return;}
    this.codePreviewLoading.set(true);
    this.codePreviewRequest=this.api.nextTeacherCode(id).subscribe({next:r=>{if(this.form.departmentId===id){this.codePreview.set(r.code);this.codePreviewLoading.set(false);}},error:e=>{if(this.form.departmentId===id){this.departmentCodeError.set(errorText(e));this.codePreviewLoading.set(false);}}});
  }
  courseDepartmentChanged(id:number|null):void{
    this.codePreviewRequest?.unsubscribe();this.form.departmentId=id;this.codePreview.set('');this.departmentCodeError.set('');this.codePreviewLoading.set(false);
    if(!id||this.editingId())return;
    const department=this.refs.departments.find((x:any)=>x.id===id);
    if(!department||!/^BM-[A-Z0-9]+(?:-[0-9]{2})?$/.test(department.code)||/^BM-[A-F0-9]{32}(?:-[0-9]{2})?$/.test(department.code)||department.code.length>36){
      this.departmentCodeError.set('Bộ môn chưa có mã hợp lệ. Vui lòng chuẩn hóa mã bộ môn trước khi tạo môn học.');return;
    }
    this.codePreviewLoading.set(true);
    this.codePreviewRequest=this.api.nextCourseCode(id).subscribe({next:r=>{if(this.form.departmentId===id){this.codePreview.set(r.code);this.codePreviewLoading.set(false);}},error:e=>{if(this.form.departmentId===id){this.departmentCodeError.set(errorText(e));this.codePreviewLoading.set(false);}}});
  }
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
    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(params => {
      this.catalogRequest?.unsubscribe();
      const selected = resolveCatalogTab(this.visibleTabs(), params.get('tab'));
      this.tab.set(selected);
      this.refreshData();
      this.editorOpen.set(false); this.pendingDeactivate.set(null); this.error.set(''); this.refErrors.set({});
      if (selected !== 'class-sections') this.loadAll();
    });
  }

  ngOnDestroy(): void { this.catalogRequest?.unsubscribe(); this.codePreviewRequest?.unsubscribe(); }

  visibleTabs(): {key: Tab; label: string}[] {
    return visibleCatalogTabs(this.auth);
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
    if(this.tab()==='teachers' && this.auth.isAdmin())return false;
    const permission: Partial<
      Record<Tab, string>
    > = {
      faculties: 'FACULTY_CREATE',
      departments:
        'DEPARTMENT_MANAGE',
      'student-classes':
        'STUDENT_CLASS_MANAGE',
      courses:
        'DEPARTMENT_DATA_MANAGE',
      teachers:
        'TEACHER_CREATE',
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
    if (this.tab() === 'teachers') return this.auth.hasPermission('DEPARTMENT_DATA_MANAGE');
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
    if (!this.visibleTabs().some(item => item.key === tab)) return;
    void this.router.navigate(['/management'], { queryParams: { tab }, queryParamsHandling: 'merge' });

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

  tabKey(event: KeyboardEvent, current: Tab): void {
    const tabs = this.visibleTabs(); const index = tabs.findIndex(tab => tab.key === current);
    const target = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : event.key === 'ArrowRight' ? (index + 1) % tabs.length : event.key === 'ArrowLeft' ? (index + tabs.length - 1) % tabs.length : -1;
    if (target < 0) return;
    event.preventDefault(); this.selectTab(tabs[target].key);
    const button = (event.currentTarget as HTMLElement).parentElement?.querySelector<HTMLButtonElement>('#catalog-tab-' + tabs[target].key);
    button?.focus(); button?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }

  loadAll(): void {
    this.catalogRequest?.unsubscribe();
    this.error.set('');
    this.refErrors.set({});

    this.catalogRequest = forkJoin({
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

      sections: of([]),

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
        this.auth.hasPermission('STUDENT_GLOBAL_VIEW') ? this.withFallback(
          'students',
          [],
          this.api.students(
            '',
            'active',
          ),
        ) : of([]),
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
    this.codePreviewRequest?.unsubscribe();this.codePreview.set('');this.departmentCodeError.set('');this.codePreviewLoading.set(false);

    this.editingId.set(
      item?.id ?? null,
    );

    this.form = item
      ? {
          ...item,
        }
      : this.emptyForm();
    this.originalTeacherDepartmentId=item?.departmentId??null;

    this.editorOpen.set(true);
  }

  closeEditor(): void {
    if (!this.saving()) {
      this.codePreviewRequest?.unsubscribe();
      this.editorOpen.set(false);
    }
  }

  emptyForm(): any {
    return {
      isActive: true,
      note: '',
      credits: 3,
      capacity: 0,
      code: '', teacherCode: '', roomType: 'UNKNOWN',
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
    if(this.saving())return;
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

    const payload={...this.form};
    if(this.tab()==='courses'||this.tab()==='departments'||this.tab()==='teachers'){delete payload.code;delete payload.teacherCode;}
    this.api
      .saveCatalog(
        this.tab(),
        this.editingId(),
        payload,
      )
      .subscribe({
        next: (saved:any) => {
          this.managementSearch='';
          const key=this.tab()==='courses'?'courses':this.tab()==='departments'?'departments':this.tab()==='teachers'?'teachers':null;
          if(key&&saved?.id){this.refs[key]=[saved,...this.refs[key].filter((x:any)=>x.id!==saved.id)];this.refreshData();}
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
    if (this.tab() === 'faculties' && !this.editingId() && (!String(this.form.initialDepartmentName ?? '').trim() || !this.form.initialHeadTeacherId))
      return 'Cần tên bộ môn đầu tiên và giảng viên hiện có làm Trưởng khoa.';
    if(this.tab()==='courses'&&!this.editingId()&&(this.departmentCodeError()||this.codePreviewLoading()))return this.departmentCodeError()||'Đang lấy mã dự kiến. Vui lòng chờ.';
    if(this.tab()==='teachers'&&!this.editingId()&&(this.departmentCodeError()||this.codePreviewLoading()))return this.departmentCodeError()||'Đang lấy mã dự kiến. Vui lòng chờ.';
    if(this.tab()==='courses'&&(!Number.isInteger(Number(this.form.credits))||Number(this.form.credits)<1||Number(this.form.credits)>20))return 'Số tín chỉ cần từ 1 đến 20.';
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
