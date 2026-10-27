import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { Video } from '../core/models';
import { ModalComponent, PageTitleComponent, RowActionMenuComponent, errorText, fmtDate, statusText } from '../shared/ui';

@Component({
  standalone: true,
  imports: [FormsModule, PageTitleComponent, ModalComponent, RowActionMenuComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-title title="Dữ liệu video" subtitle="Quản lý, xem trước và theo dõi trạng thái xử lý video.">
      <button type="button" (click)="openUpload()">+ Tải video lên</button>
    </app-page-title>

    @if (error()) { <div class="error-box">{{ error() }}</div> }
    @if (message()) { <div class="success-box">{{ message() }}</div> }
    @if (loading()) { <div class="notice">Đang tải danh sách video…</div> }

    <div class="filter-bar">
      <label class="grow">Tìm video<input [(ngModel)]="search" placeholder="Tên file video…" /></label>
      <label>Trạng thái
        <select [(ngModel)]="statusFilter">
          <option value="">Tất cả</option>
          <option value="READY">Hoàn tất</option>
          <option value="PROCESSING">Đang xử lý</option>
          <option value="UPLOADED">Chờ xử lý</option>
          <option value="FAILED">Lỗi</option>
        </select>
      </label>
      <label>Loại video
        <select [(ngModel)]="typeFilter">
          <option value="">Tất cả</option>
          <option value="INPUT_UPLOAD">Video tải lên</option>
          <option value="ANNOTATED_OUTPUT">Video đầu ra buổi học</option>
        </select>
      </label>
      <button type="button" class="secondary" (click)="clearFilters()">Xóa lọc</button>
    </div>

    <section class="card table-card">
      <div class="table-wrap">
        <table>
          <thead><tr><th>Tên file</th><th>Loại video</th><th>Buổi học</th><th>Nguồn</th><th>Ngày tạo</th><th>Trạng thái</th><th>Thao tác</th></tr></thead>
          <tbody>
            @for (video of filteredItems(); track video.id) {
              <tr>
                <td><span class="cell-title">{{ video.fileName }}</span><small>{{ video.contentType }}</small></td>
                <td><span class="badge" [class.purple]="video.videoType === 'ANNOTATED_OUTPUT'">{{ typeLabel(video.videoType) }}</span><small>{{ fileSize(video.sizeBytes) }}</small></td>
                <td>@if(video.sessionId){<span class="cell-title">{{ video.sessionCode || 'Buổi học liên quan' }}</span><small>{{ video.courseName }}</small>}@else{—}</td>
                <td>{{ sourceLabel(video) }}</td>
                <td>{{ date(video.uploadedAt) }}</td>
                <td><span class="badge" [class.blue]="video.status === 'PROCESSING' || video.status === 'UPLOADED'" [class.danger]="video.status === 'FAILED'">{{ statusLabel(video.status) }}</span></td>
                <td class="menu-cell">
                  <app-row-action-menu>
                    <button type="button" class="secondary small" [disabled]="video.status !== 'READY'" (click)="preview(video)">Xem trước</button>
                    <button type="button" class="danger small" (click)="pendingDelete.set(video)">Xóa</button>
                  </app-row-action-menu>
                </td>
              </tr>
            } @empty {
              <tr><td colspan="7" class="empty">Không có video phù hợp. Video từ camera chỉ xuất hiện sau khi hệ thống xử lý và lưu tệp thành công.</td></tr>
            }
          </tbody>
        </table>
      </div>
    </section>

    @if (uploadOpen()) {
      <app-modal title="Tải video lên" [busy]="uploading()" size="md" (close)="closeUpload()">
        <div class="stack">
          <label class="drop-zone">
            <div>
              <div class="upload-icon">▶</div>
              <strong>{{ uploadFile()?.name || 'Kéo thả file video vào đây' }}</strong>
              <small>{{ uploadFile() ? fileSize(uploadFile()!.size) : 'MP4, AVI, MKV — chọn file từ máy tính' }}</small>
            </div>
            <input type="file" accept="video/*,.mkv" [disabled]="uploading()" (change)="chooseFile($event)" />
          </label>
          <div class="modal-footer" style="margin:0 -20px -20px">
            <button type="button" class="secondary" [disabled]="uploading()" (click)="closeUpload()">Hủy</button>
            <button type="button" [disabled]="!uploadFile() || uploading()" (click)="upload()">{{ uploading() ? 'Đang tải lên…' : 'Tải lên' }}</button>
          </div>
        </div>
      </app-modal>
    }

    @if (previewVideo()) {
      <app-modal title="Xem trước video" size="lg" (close)="closePreview()">
        <p><strong>{{ previewVideo()!.fileName }}</strong></p>
        @if (previewLoading()) { <div class="notice">Đang xác minh tệp trong kho lưu trữ…</div> }
        @if (previewError()) { <div class="error-box">{{ previewError() }}</div> }
        @if (previewUrl()) {
          <video class="video-preview" controls autoplay tabindex="0" [src]="previewUrl()" (loadedmetadata)="onMetadata($event)" (error)="onVideoError()"></video>
          @if (previewDuration() > 0) { <small>Thời lượng: {{ durationLabel() }}</small> }
        }
      </app-modal>
    }

    @if (pendingDelete()) {
      <app-modal title="Xóa video" size="sm" (close)="pendingDelete.set(null)">
        <p>Bạn có chắc muốn xóa <b>{{ pendingDelete()!.fileName }}</b>? Thao tác này không thể hoàn tác.</p>
        <div class="modal-footer" style="margin:20px -20px -20px">
          <button type="button" class="secondary" (click)="pendingDelete.set(null)">Hủy</button>
          <button type="button" class="danger" (click)="remove()">Xóa video</button>
        </div>
      </app-modal>
    }
  `,
})
export class VideosComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly items = signal<Video[]>([]);
  readonly error = signal('');
  readonly message = signal('');
  readonly loading = signal(false);
  readonly uploadOpen = signal(false);
  readonly uploadFile = signal<File | null>(null);
  readonly uploading = signal(false);
  readonly previewUrl = signal('');
  readonly previewVideo = signal<Video | null>(null);
  readonly previewLoading = signal(false);
  readonly previewError = signal('');
  readonly previewDuration = signal(0);
  readonly pendingDelete = signal<Video | null>(null);
  readonly date = fmtDate;
  search = '';
  statusFilter = '';
  typeFilter = '';

  filteredItems(): Video[] {
    const query = this.search.trim().toLowerCase();
    return this.items().filter((item) =>
      (!query || item.fileName.toLowerCase().includes(query)) &&
      (!this.statusFilter || item.status === this.statusFilter) &&
      (!this.typeFilter || item.videoType === this.typeFilter),
    );
  }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading.set(true);
    this.api.videos().subscribe({
      next: (items) => { this.items.set(items); this.loading.set(false); },
      error: (error) => { this.error.set(errorText(error)); this.loading.set(false); },
    });
  }

  openUpload(): void { this.uploadFile.set(null); this.uploadOpen.set(true); }
  closeUpload(): void { if (!this.uploading()) this.uploadOpen.set(false); }
  chooseFile(event: Event): void { this.uploadFile.set((event.target as HTMLInputElement).files?.[0] ?? null); }

  upload(): void {
    const file = this.uploadFile();
    if (!file || this.uploading()) return;
    this.uploading.set(true);
    this.error.set('');
    this.api.uploadVideo(file).subscribe({
      next: () => {
        this.uploading.set(false);
        this.uploadOpen.set(false);
        this.message.set('Tải video lên thành công.');
        this.load();
      },
      error: (error) => { this.error.set(errorText(error)); this.uploading.set(false); },
    });
  }

  preview(video: Video): void {
    this.previewVideo.set(video);
    this.previewUrl.set('');
    this.previewError.set('');
    this.previewDuration.set(0);
    this.previewLoading.set(true);
    this.api.previewVideo(video.id).subscribe({
      next: (result) => { this.previewUrl.set(result.url); this.previewLoading.set(false); },
      error: (error) => { this.previewError.set(errorText(error)); this.previewLoading.set(false); },
    });
  }

  closePreview(): void { this.previewUrl.set(''); this.previewVideo.set(null); this.previewError.set(''); this.previewDuration.set(0); }
  onMetadata(event: Event): void { this.previewDuration.set(Number((event.target as HTMLVideoElement).duration) || 0); }
  onVideoError(): void { this.previewError.set('Không thể phát video. Tệp có thể không tồn tại, liên kết đã hết hạn hoặc định dạng không được trình duyệt hỗ trợ.'); }
  durationLabel(): string { const seconds = Math.round(this.previewDuration()); return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`; }

  remove(): void {
    const video = this.pendingDelete();
    if (!video) return;
    this.api.deleteVideo(video.id).subscribe({
      next: () => { this.pendingDelete.set(null); this.message.set('Đã xóa video.'); this.load(); },
      error: (error) => this.error.set(errorText(error)),
    });
  }

  clearFilters(): void { this.search = ''; this.statusFilter = ''; this.typeFilter = ''; }
  fileSize(bytes: number): string { return bytes < 1024 * 1024 ? `${Math.max(1, Math.round(bytes / 1024))} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`; }
  typeLabel(type: Video['videoType']): string { return type === 'ANNOTATED_OUTPUT' ? 'Video đầu ra buổi học' : 'Video tải lên'; }
  sourceLabel(video: Video): string { return video.sourceType === 'CAMERA' ? 'Camera' : video.sourceType === 'VIDEO' ? (video.parentVideoName || 'Video nguồn') : 'Tải lên'; }
  statusLabel(status: string): string { return statusText(status); }
}
