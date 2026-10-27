import { SearchSelectComponent } from '../shared/search-select.component';
import {
  HttpErrorResponse,
} from '@angular/common/http';
import {
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import {
  FormBuilder,
  FormControl,
  FormsModule,
  ReactiveFormsModule,
  Validators,
} from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import {
  Student,
  StudentClass,
  StudentStatusFilter,
} from '../core/models';
import {
  ModalComponent,
  PageTitleComponent,
  RowActionMenuComponent,
  apiFieldErrors,
  errorText,
} from '../shared/ui';

@Component({
  standalone: true,
  imports: [SearchSelectComponent,
    FormsModule,
    ReactiveFormsModule,
    RouterLink,
    ModalComponent,
    PageTitleComponent,
    RowActionMenuComponent,
  ],
  template: `
    <app-page-title
      title="Hồ sơ sinh viên"
      subtitle="Quản lý thông tin, lớp sinh hoạt và dữ liệu nhận diện sinh viên."
    >
      @if (auth.canCreateStudents()) {
        <button
          type="button"
          (click)="openCreateStudent()"
        >
          + Thêm sinh viên
        </button>
      }
    </app-page-title>

    @if (error()) {
      <div class="error-box">
        {{ error() }}
      </div>
    }

    @if (message()) {
      <div class="success-box">
        {{ message() }}
      </div>
    }

    @if (loading()) {
      <div class="notice">Đang tải danh sách sinh viên…</div>
    }

    @if (importErrors().length) {
      <div class="error-box">
        <b>File chưa được import:</b>

        <ul>
          @for (
            item of importErrors();
            track $index
          ) {
            <li>
              {{
                item.line
                  ? 'Dòng ' + item.line + ': '
                  : ''
              }}
              {{ item.message }}
            </li>
          }
        </ul>
      </div>
    }

    <section class="card">
      <div class="student-search-grid">
        <label class="student-search-query">
          Tìm kiếm
          <input
            [(ngModel)]="query"
            placeholder="MSSV, họ tên, email…"
          />
        </label>

        <label>
          Trạng thái
          <select [(ngModel)]="status">
            <option value="all">
              Tất cả
            </option>
            <option value="active">
              Hoạt động
            </option>
            <option value="inactive">
              Ngừng hoạt động
            </option>
          </select>
        </label>

        <div class="student-search-actions">
          <button
            type="button"
            (click)="applyFilter()"
          >
            Tìm kiếm
          </button>

          <button
            type="button"
            class="secondary"
            (click)="clearFilter()"
          >
            Xóa lọc
          </button>
        </div>

        @if (auth.canCreateStudents()) {
          <div class="student-import-actions">
            <button type="button" class="secondary" (click)="openImportModal()">Import CSV</button>

            <button
              type="button"
              class="secondary"
              (click)="downloadSample()"
            >
              Tải CSV mẫu
            </button>
          </div>
        }
      </div>
    </section>

    <section class="card table-card">
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>MSSV</th>
              <th>Họ tên</th>
              <th>Email</th>
              <th>Lớp</th>
              <th>Mã ẩn danh</th>
              <th>Trạng thái</th>
              <th>Thao tác</th>
            </tr>
          </thead>

          <tbody>
            @for (
              student of items();
              track student.id
            ) {
              <tr>
                <td>
                  {{ student.studentCode }}
                </td>
                <td>
                  {{ student.fullName }}
                </td>
                <td>
                  {{ student.email || '—' }}
                </td>
                <td>
                  {{
                    student.studentClass ||
                      '—'
                  }}
                </td>
                <td><code>{{ student.anonymousCode }}</code>@if(student.sourceAnonymousCode){<small>Mã nguồn: {{student.sourceAnonymousCode}}</small>}</td>
                <td>
                  <span
                    class="badge"
                    [class.inactive]="
                      !student.isActive
                    "
                  >
                    {{
                      student.isActive
                        ? 'Hoạt động'
                        : 'Ngừng hoạt động'
                    }}
                  </span>
                </td>
                <td class="menu-cell">
                  <app-row-action-menu>
                    @if (auth.canEnrollFaces() && (student.canManage || auth.user().roles?.includes('TECH_AI'))) {
                      <a
                        class="button-link"
                        [routerLink]="[
                          '/students',
                          student.id,
                          'face-enrollment'
                        ]"
                      >
                        Khuôn mặt
                      </a>
                    }

                    @if (auth.canEditStudents() && student.canManage) {
                      <button
                        type="button"
                        class="secondary small"
                        (click)="
                          openEditStudent(
                            student
                          )
                        "
                      >
                        Sửa
                      </button>

                      @if (student.isActive) {
                        <button
                          type="button"
                          class="danger small"
                          (click)="
                            askDeactivate(
                              student
                            )
                          "
                        >
                          Ngừng hoạt động
                        </button>
                      } @else {
                        <button
                          type="button"
                          class="small"
                          (click)="
                            reactivate(
                              student
                            )
                          "
                        >
                          Kích hoạt lại
                        </button>
                      }
                    }
                  </app-row-action-menu>
                </td>
              </tr>
            } @empty {
              <tr>
                <td
                  colspan="7"
                  class="empty"
                >
                  Không có sinh viên phù hợp.
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    </section>

    @if (isStudentModalOpen()) {
      <app-modal
        [title]="
          editingStudentId()
            ? 'Sửa sinh viên'
            : 'Thêm sinh viên'
        "
        [busy]="saving()"
        size="md"
        (close)="closeStudentModal()"
      >
        <form
          class="form-grid"
          [formGroup]="form"
          (ngSubmit)="saveStudent()"
        >
          @if (studentFormError()) {
            <div class="error-box span2">{{ studentFormError() }}</div>
          }
          <label>
            MSSV *
            <input
              formControlName="studentCode"
              (input)="clearStudentFieldError('studentCode')"
            />

            @if (
              form.controls.studentCode.touched &&
              form.controls.studentCode.hasError(
                'required'
              )
            ) {
              <small class="field-error">
                Mã sinh viên là bắt buộc.
              </small>
            }
            @if (studentFieldErrors()['studentCode']) {
              <small class="field-error">{{ studentFieldErrors()['studentCode'] }}</small>
            }
          </label>

          <label>
            Họ tên *
            <input
              formControlName="fullName"
            />

            @if (
              form.controls.fullName.touched &&
              form.controls.fullName.hasError(
                'required'
              )
            ) {
              <small class="field-error">
                Họ tên là bắt buộc.
              </small>
            }
          </label>

          <label>
            Email
            <input
              type="email"
              formControlName="email"
              (input)="clearStudentFieldError('email')"
            />
            @if (studentFieldErrors()['email']) {
              <small class="field-error">{{ studentFieldErrors()['email'] }}</small>
            }
          </label>

          <label>
            Lớp sinh hoạt
            <app-search-select resource="student-classes" label="Lớp sinh hoạt" formControlName="studentClassId" />
          </label>

          <label>
            Mã ẩn danh nội bộ
            <input
              formControlName="anonymousCode"
              readonly placeholder="Tự động tạo khi lưu"
              (input)="clearStudentFieldError('anonymousCode')"
            />

            @if (
              form.controls.anonymousCode.touched &&
              form.controls.anonymousCode.hasError(
                'required'
              )
            ) {
              <small class="field-error">
                Mã ẩn danh là bắt buộc.
              </small>
            }
            @if (studentFieldErrors()['anonymousCode']) {
              <small class="field-error">{{ studentFieldErrors()['anonymousCode'] }}</small>
            }
          </label>

          <label class="check">
            <input
              type="checkbox"
              formControlName="isActive"
            />
            Hoạt động
          </label>

          <div class="modal-footer span2" style="margin:0 -20px -20px">
            <button
              type="submit"
              [disabled]="
                saving() ||
                form.invalid
              "
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
              (click)="
                closeStudentModal()
              "
            >
              Hủy
            </button>
          </div>
        </form>
      </app-modal>
    }

    @if (importModalOpen()) {
      <app-modal title="Import danh sách sinh viên" [busy]="importing()" size="md" (close)="closeImportModal()">
        <div class="stack">
          <label class="drop-zone">
            <div>
              <div class="upload-icon">⇧</div>
              <strong>{{ selectedImportFile()?.name || 'Kéo thả file CSV vào đây' }}</strong>
              <small>{{ selectedImportFile() ? formatFileSize(selectedImportFile()!.size) : 'hoặc chọn file từ máy tính' }}</small>
            </div>
            <input type="file" accept=".csv,text/csv" [disabled]="importing()" (change)="chooseImportFile($event)" />
          </label>

          <button type="button" class="secondary" (click)="downloadSample()">Tải file CSV mẫu</button>

          @if (importErrors().length) {
            <div class="error-box">
              <b>File chưa thể import:</b>
              <ul>
                @for (item of importErrors(); track $index) {
                  <li>{{ item.line ? 'Dòng ' + item.line + ': ' : '' }}{{ item.message }}</li>
                }
              </ul>
            </div>
          }

          <div class="modal-footer" style="margin:0 -20px -20px">
            <button type="button" class="secondary" [disabled]="importing()" (click)="closeImportModal()">Hủy</button>
            <button type="button" [disabled]="!selectedImportFile() || importing()" (click)="confirmImport()">{{ importing() ? 'Đang import…' : 'Import sinh viên' }}</button>
          </div>
        </div>
      </app-modal>
    }

    @if (pendingDeactivate()) {
      <app-modal
        title="Xác nhận ngừng hoạt động"
        [busy]="saving()"
        (close)="cancelDeactivate()"
      >
        <p>
          Bạn có chắc muốn ngừng hoạt động
          <b>
            {{
              pendingDeactivate()
                ?.studentCode
            }}
            -
            {{
              pendingDeactivate()
                ?.fullName
            }}
          </b>
          không?
        </p>

        <div class="modal-footer" style="margin:20px -20px -20px">
          <button
            type="button"
            class="danger"
            [disabled]="saving()"
            (click)="confirmDeactivate()"
          >
            Xác nhận
          </button>

          <button
            type="button"
            class="secondary"
            [disabled]="saving()"
            (click)="cancelDeactivate()"
          >
            Hủy
          </button>
        </div>
      </app-modal>
    }
  `,
})
export class StudentsComponent
  implements OnInit
{
  private readonly api = inject(ApiService);

  readonly auth = inject(AuthService);

  private readonly fb = inject(FormBuilder);

  readonly items = signal<Student[]>([]);

  readonly classes =
    signal<StudentClass[]>([]);

  readonly isStudentModalOpen =
    signal(false);

  readonly editingStudentId =
    signal<number | null>(null);

  readonly pendingDeactivate =
    signal<Student | null>(null);

  readonly saving = signal(false);
  readonly importing = signal(false);
  readonly loading = signal(false);
  readonly importModalOpen = signal(false);
  readonly selectedImportFile = signal<File | null>(null);
  readonly error = signal('');
  readonly studentFormError = signal('');
  readonly studentFieldErrors = signal<Record<string, string>>({});
  readonly message = signal('');

  readonly importErrors =
    signal<any[]>([]);

  query = '';

  status: StudentStatusFilter = 'all';

  readonly activeClasses = computed(
    () =>
      this.classes().filter(
        (studentClass) =>
          studentClass.isActive,
      ),
  );

  readonly form = this.fb.group({
    studentCode:
      this.fb.nonNullable.control(
        '',
        Validators.required,
      ),

    fullName:
      this.fb.nonNullable.control(
        '',
        Validators.required,
      ),

    email:
      this.fb.nonNullable.control(''),

    studentClassId:
      new FormControl<number | null>(
        null,
      ),

    anonymousCode:
      this.fb.nonNullable.control(''),

    isActive:
      this.fb.nonNullable.control(true),
  });

  ngOnInit(): void {
    this.loadStudents();

    this.api.studentClasses().subscribe({
      next: (classes) =>
        this.classes.set(classes),

      error: (error) =>
        this.error.set(
          errorText(error),
        ),
    });
  }

  applyFilter(): void {
    this.loadStudents();
  }

  clearFilter(): void {
    this.query = '';
    this.status = 'all';
    this.loadStudents();
  }

  loadStudents(): void {
    this.error.set('');
    this.loading.set(true);

    this.api
      .students(
        this.query.trim(),
        this.status,
      )
      .subscribe({
        next: (students) => {
          this.items.set(students);
          this.loading.set(false);
        },

        error: (error) => {
          this.error.set(
            errorText(error),
          );
          this.loading.set(false);
        },
      });
  }

  openCreateStudent(): void {
    this.studentFormError.set('');
    this.studentFieldErrors.set({});
    this.form.controls.studentClassId.enable({ emitEvent: false });
    this.editingStudentId.set(null);

    this.form.reset({
      studentCode: '',
      fullName: '',
      email: '',
      studentClassId: null,
      anonymousCode: '',
      isActive: true,
    });

    this.isStudentModalOpen.set(
      true,
    );
  }

  openEditStudent(
    student: Student,
  ): void {
    this.studentFormError.set('');
    this.studentFieldErrors.set({});
    if (this.auth.canCreateStudents()) {
      this.form.controls.studentClassId.enable({ emitEvent: false });
    } else {
      this.form.controls.studentClassId.disable({ emitEvent: false });
    }
    this.editingStudentId.set(
      student.id,
    );

    this.form.reset({
      studentCode:
        student.studentCode,

      fullName:
        student.fullName,

      email:
        student.email ?? '',

      studentClassId:
        student.studentClassId ??
        null,

      anonymousCode:
        student.anonymousCode,

      isActive:
        student.isActive,
    });

    this.isStudentModalOpen.set(
      true,
    );
  }

  closeStudentModal(): void {
    if (!this.saving()) {
      this.isStudentModalOpen.set(
        false,
      );
    }
  }

  clearStudentFieldError(field: string): void {
    this.studentFieldErrors.update((errors) => {
      if (!errors[field]) return errors;
      const next = { ...errors };
      delete next[field];
      return next;
    });
  }

  saveStudent(): void {
    if (
      this.form.invalid ||
      this.saving()
    ) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    this.studentFormError.set('');
    this.studentFieldErrors.set({});
    this.message.set('');

    const raw =
      this.form.getRawValue();

    const payload: Partial<Student> = {
      studentCode:
        raw.studentCode.trim(),

      fullName:
        raw.fullName.trim(),

      email:
        raw.email.trim(),

      studentClassId:
        raw.studentClassId,

      anonymousCode:
        raw.anonymousCode.trim(),

      isActive:
        raw.isActive,
    };

    this.api
      .saveStudent(
        this.editingStudentId(),
        payload,
      )
      .subscribe({
        next: () => {
          this.message.set(
            this.editingStudentId()
              ? 'Đã cập nhật sinh viên.'
              : 'Đã thêm sinh viên.',
          );

          this.isStudentModalOpen.set(
            false,
          );

          this.saving.set(false);
          this.loadStudents();
        },

        error: (error) => {
          this.studentFieldErrors.set(apiFieldErrors(error));
          this.studentFormError.set(
            errorText(error),
          );

          this.saving.set(false);
        },
      });
  }

  askDeactivate(
    student: Student,
  ): void {
    this.pendingDeactivate.set(
      student,
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
    const student =
      this.pendingDeactivate();

    if (!student) {
      return;
    }

    this.saving.set(true);

    this.api
      .deactivateStudent(
        student.id,
      )
      .subscribe({
        next: () => {
          this.pendingDeactivate.set(
            null,
          );

          this.saving.set(false);

          this.message.set(
            'Đã ngừng hoạt động sinh viên.',
          );

          this.loadStudents();
        },

        error: (error) => {
          this.error.set(
            errorText(error),
          );

          this.saving.set(false);
        },
      });
  }

  reactivate(
    student: Student,
  ): void {
    this.api
      .reactivateStudent(student.id)
      .subscribe({
        next: () => {
          this.message.set(
            'Đã kích hoạt lại sinh viên.',
          );

          this.loadStudents();
        },

        error: (error) =>
          this.error.set(
            errorText(error),
          ),
      });
  }

  openImportModal(): void {
    this.importErrors.set([]);
    this.selectedImportFile.set(null);
    this.importModalOpen.set(true);
  }

  closeImportModal(): void {
    if (!this.importing()) {
      this.importModalOpen.set(false);
      this.selectedImportFile.set(null);
    }
  }

  chooseImportFile(event: Event): void {
    const input =
      event.target as HTMLInputElement;

    const file =
      input.files?.[0];

    this.selectedImportFile.set(file ?? null);
    this.importErrors.set([]);
  }

  confirmImport(): void {
    const file = this.selectedImportFile();
    if (!file || this.importing()) return;

    this.importing.set(true);
    this.error.set('');
    this.message.set('');
    this.importErrors.set([]);

    this.api.importStudents(file)
      .subscribe({
        next: (result) => {
          this.message.set(
            `Đã import ${result.inserted} sinh viên.`,
          );

          this.importing.set(false);
          this.importModalOpen.set(false);
          this.selectedImportFile.set(null);
          this.loadStudents();
        },

        error: (
          error: HttpErrorResponse,
        ) => {
          this.error.set(
            errorText(error),
          );

          this.importErrors.set(
            Array.isArray(
              error.error?.errors,
            )
              ? error.error.errors
              : [],
          );

          this.importing.set(false);
        },
      });
  }

  formatFileSize(bytes: number): string {
    return bytes < 1024 * 1024
      ? `${Math.max(1, Math.round(bytes / 1024))} KB`
      : `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  }

  downloadSample(): void {
    const text =
      '\uFEFFStudentCode,FullName,Email,StudentClassCode,AnonymousCode\r\n' +
      'SV1001,Nguyễn Minh Anh,sv1001@example.edu.vn,12DHTH01,ANON-SV1001\r\n';

    const url =
      URL.createObjectURL(
        new Blob(
          [text],
          {
            type:
              'text/csv;charset=utf-8',
          },
        ),
      );

    const anchor =
      document.createElement('a');

    anchor.href = url;

    anchor.download =
      'mau-import-sinh-vien.csv';

    anchor.click();

    URL.revokeObjectURL(url);
  }
}
