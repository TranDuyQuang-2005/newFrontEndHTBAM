import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, OnInit, ViewChild, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';
import { catchError, firstValueFrom, forkJoin, of } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { BackButtonComponent, ModalComponent, PageTitleComponent, behaviorText, errorText, fmtDate, statusText } from '../shared/ui';

@Component({
  standalone: true,
  imports: [BackButtonComponent, PageTitleComponent, ModalComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-back-button to="/sessions" label="Quay lại danh sách buổi học" />
    <app-page-title [title]="session()?.classSection ? session().classSection : 'Chi tiết buổi học'" [subtitle]="sessionSubtitle()">
      <span class="badge" [class.blue]="realtimeState() === 'ONLINE'" [class.warning]="realtimeState() === 'CONNECTING'" [class.gray]="realtimeState() === 'OFFLINE'">{{ realtimeLabel() }}</span>
      <button type="button" class="secondary" (click)="load()">Cập nhật</button>
      @if (session()?.canOperate && (session()?.status === 'READY' || session()?.status === 'DRAFT')) { <button type="button" [disabled]="acting() || pendingAction() !== null" (click)="requestAction('start')">Bắt đầu</button> }
      @if (session()?.canOperate && session()?.status === 'RUNNING') { <button type="button" class="danger" [disabled]="acting() || pendingAction() !== null" (click)="requestAction('stop')">Kết thúc</button> }
      @if (session()?.canOperate && session()?.status === 'FINALIZE_FAILED') { <button type="button" class="danger" [disabled]="acting() || pendingAction() !== null" (click)="requestAction('retry-finalize')">Thử hoàn tất lại</button> }
      @if (session()?.status === 'COMPLETED' && hasMissingOutputs()) { <button type="button" class="secondary" [disabled]="acting() || pendingAction() !== null" (click)="requestAction('retry-finalize')">Kiểm tra/khôi phục đầu ra</button> }
    </app-page-title>

    @if (error()) { <div class="error-box">{{ error() }}</div> }
    @if (sessionError()) { <div class="error-box">Buổi học: {{ sessionError() }}</div> }
    @if (dashboardError()) { <div class="error-box">Bảng theo dõi: {{ dashboardError() }}</div> }
    @if (message()) { <div class="success-box">{{ message() }}</div> }
    @if (loading()) { <div class="notice">Đang tải dữ liệu buổi học…</div> }
    @if (session()?.status === 'FINALIZING') { <div class="notice">Hệ thống đang tổng hợp dữ liệu cuối buổi học. Bạn có thể rời trang; quá trình này vẫn tiếp tục.</div> }
    @if (session()?.status === 'FINALIZE_FAILED') { <div class="error-box"><strong>Chưa thể hoàn tất buổi học.</strong><br />Dữ liệu đã nhận vẫn được giữ. Bấm “Thử hoàn tất lại” để tiếp tục.</div> }

    @if (dashboard()) {
      <div class="stats stats-four">
        <section class="stat-card"><div class="stat-card-head"><div class="stat-icon">◎</div></div><span>Nhận diện hoạt động</span><strong>{{ dashboard().activeIdentities }}</strong><small>Định danh ổn định</small></section>
        <section class="stat-card"><div class="stat-card-head"><div class="stat-icon green">✓</div></div><span>Đã nhận diện</span><strong>{{ dashboard().identifiedStudents }}</strong><small>Sinh viên</small></section>
        <section class="stat-card"><div class="stat-card-head"><div class="stat-icon amber">?</div></div><span>Chưa xác định</span><strong>{{ dashboard().unknownIdentities }}</strong><small>Định danh chưa liên kết</small></section>
        <section class="stat-card"><div class="stat-card-head"><div class="stat-icon red">!</div></div><span>Cảnh báo</span><strong>{{ dashboard().totalAlerts ?? dashboard().recentAlerts?.length ?? 0 }}</strong><small>Toàn bộ buổi học</small></section>
      </div>

      @if (dashboard().outputs) {
        <section class="card"><div class="card-header" style="margin:-20px -20px 18px"><div><h3>Đầu ra đã lưu</h3><p>Trạng thái dữ liệu phát sinh từ buổi học</p></div></div>
          <div class="service-grid">@for(item of outputEntries();track item.key){<div class="service-card"><span class="status-dot" [style.background]="item.status === 'AVAILABLE' ? '#16a34a' : '#94a3b8'"></span><div><strong>{{item.label}} · {{item.count}}</strong><small>{{item.message}}</small></div></div>}</div>
        </section>
      }

      <div class="dashboard-grid">
        <section class="card" style="min-height:360px">
          <div class="card-header" style="margin:-20px -20px 18px"><div><h3>Nguồn phân tích</h3><p>{{ isBrowserCamera() ? 'Camera trên thiết bị' : session()?.cameraId ? 'Camera phòng học' : 'Video tải lên' }}</p></div><span class="badge" [class.warning]="isBrowserCamera() ? browserCameraState() !== 'STREAMING' : dashboard().cameraHealth !== 'ONLINE'">{{ isBrowserCamera() ? browserCameraStateText() : statusText(dashboard().cameraHealth) }}</span></div>
          @if (isBrowserCamera()) {
            <div style="display:grid;gap:12px">
              <!-- Lớp hiển thị kết quả theo thời gian thực -->
        <div style="position:relative;width:100%;line-height:0">
          <video #browserPreview autoplay muted playsinline style="width:100%;max-height:430px;background:#0f172a;border-radius:12px;object-fit:contain" [style.display]="cameraViewMode() === 'live' || !aiPreviewUrl() ? 'block' : 'none'"></video>
          <canvas #realtimeOverlay [style.display]="cameraViewMode() === 'live' ? 'block' : 'none'"
                  style="position:absolute;inset:0;width:100%;height:100%;pointer-events:none;border-radius:12px"></canvas>
          @if (cameraViewMode() === 'live') {
            <div style="position:absolute;left:12px;bottom:12px;display:flex;gap:8px;align-items:center;padding:6px 10px;border-radius:999px;background:rgba(15,23,42,.78);color:white;font-size:12px;line-height:1.2;pointer-events:none">
              <span style="width:7px;height:7px;border-radius:50%;background:#22c55e"></span>
              Trực tiếp · {{ fastOverlayPeople() }} người đang được theo dõi
            </div>
          }
        </div>
        <!-- Khung hình đã xử lý -->
        @if (cameraViewMode() === 'ai' && aiPreviewUrl()) {
          <img [src]="aiPreviewUrl()!" alt="Khung hình đã phân tích"
               style="display:block;width:100%;aspect-ratio:16/9;object-fit:contain;background:#0f172a;border-radius:12px" />
        }
        <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin-top:10px">
          <button type="button" class="secondary" (click)="cameraViewMode.set('live')">Xem trực tiếp</button>
          <button type="button" class="secondary" (click)="cameraViewMode.set('ai')">Xem khung hình đã xử lý</button>
          @if (cameraViewMode() === 'ai') {
            <small style="color:#64748b">{{ aiPreviewUrl() ? 'Khung hình hiển thị kết quả phân tích gần nhất.' : 'Đang chờ khung hình đầu tiên…' }}</small>
          }
        </div>

              @if (browserCameraState() === 'IDLE') { <div class="notice">Bấm <b>Bắt đầu</b> để cấp quyền và mở camera laptop.</div> }
              @if (browserCameraState() === 'REQUESTING') { <div class="notice">Đang yêu cầu quyền truy cập camera…</div> }
              @if (browserCameraState() === 'ERROR') { <div class="error-box">{{ browserCameraError() }}</div> }
            </div>
          } @else {
            <div class="empty-state"><div><div class="upload-icon">▶</div><strong>Hình ảnh đang được phân tích</strong><span>Nguồn camera hoặc video được hệ thống xử lý tự động.</span></div></div>
          }
        </section>

        <section class="card">
          <div class="card-header" style="margin:-20px -20px 18px"><div><h3>Phân tích lớp học</h3><p>Cập nhật {{ date(dashboard().generatedAt) }}</p></div><span class="badge purple">{{ statusText(dashboard().aiHealth) }}</span></div>
          @for (metric of behaviorMetrics(); track metric.label) {
            <div class="metric-row"><span>{{ metric.label }}</span><div class="progress-track"><div class="progress-value" [style.width.%]="metric.value" [style.background]="metric.color"></div></div><strong>{{ metric.value }}%</strong></div>
          }
          <div class="service-grid service-grid-two" style="margin-top:22px">
            <div class="service-card"><span class="status-dot" [style.background]="dashboard().modelReady ? '#16a34a' : '#dc2626'"></span><div><strong>Nhận biết hành vi</strong><small>{{ dashboard().modelReady ? 'Sẵn sàng' : 'Chưa sẵn sàng' }}</small></div></div>
            <div class="service-card"><span class="status-dot" [style.background]="(dashboard().loadedModels?.length || 0) > 1 ? '#16a34a' : '#dc2626'"></span><div><strong>Nhận diện sinh viên</strong><small>{{ (dashboard().loadedModels?.length || 0) > 1 ? 'Sẵn sàng' : 'Chưa sẵn sàng' }}</small></div></div>
            <div class="service-card"><span class="status-dot" [style.background]="dashboard().modelReady ? '#16a34a' : '#dc2626'"></span><div><strong>Phân tích buổi học</strong><small>{{ dashboard().modelReady ? 'Sẵn sàng' : 'Chưa sẵn sàng' }}</small></div></div>
            <div class="service-card"><span class="status-dot" [style.background]="dashboard().aiHealth === 'ONLINE' ? '#16a34a' : '#dc2626'"></span><div><strong>Dịch vụ phân tích</strong><small>{{ statusText(dashboard().aiHealth) }}</small></div></div>
          </div>
        </section>
      </div>

      <section class="card table-card">
        <div class="card-header"><div><h3>Hành vi sinh viên theo thời gian thực</h3><p>Dữ liệu nhận diện và hành vi gần nhất</p></div></div>
        <div class="table-wrap">
          <table>
            <thead><tr><th>Đối tượng</th><th>Trạng thái nhận diện</th><th>Hành vi hiện tại</th><th>Cập nhật lần cuối</th></tr></thead>
            <tbody>
              @for (row of dashboard().identities; track row.id; let index = $index) {
                <tr>
                  <td class="cell-title">Đối tượng {{ index + 1 }}</td><td>{{ row.studentId ? 'Đã xác định sinh viên' : 'Chưa xác định' }}</td>
                  <td><span class="badge blue">{{ behaviorLabel(row.currentBehavior) }}</span></td><td>{{ date(row.lastSeenAt) }}</td>
                </tr>
              } @empty { <tr><td colspan="4" class="empty">{{ session()?.status === 'COMPLETED' ? 'Không có dữ liệu nhận diện trong buổi học này.' : 'Chưa có đối tượng đang được theo dõi.' }}</td></tr> }
            </tbody>
          </table>
        </div>
      </section>
    }

    @if (pendingAction()) {
      <app-modal [title]="actionTitle()" size="sm" [busy]="acting()" (close)="pendingAction.set(null)">
        <p>{{ actionMessage() }}</p>
        <div class="modal-footer" style="margin:20px -20px -20px">
          <button type="button" class="secondary" [disabled]="acting()" (click)="pendingAction.set(null)">Hủy</button>
          <button type="button" [class.danger]="pendingAction() === 'stop' || pendingAction() === 'cancel'" [disabled]="acting()" (click)="confirmAction()">Xác nhận</button>
        </div>
      </app-modal>
    }
  `,
})
export class SessionDetailComponent implements OnInit, OnDestroy {
  @ViewChild('browserPreview') browserPreview?: ElementRef<HTMLVideoElement>;
  @ViewChild('realtimeOverlay') private realtimeOverlay?: ElementRef<HTMLCanvasElement>;
  private readonly route = inject(ActivatedRoute);
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private connection: HubConnection | null = null;
  private refreshTimer: ReturnType<typeof setTimeout> | null = null;
  private browserStream: MediaStream | null = null;
  private browserRecorder: MediaRecorder | null = null;
  private browserRecorderTimer: ReturnType<typeof setTimeout> | null = null;
  private browserCaptureActive = false;
  private browserChunkIndex = 0;
  private browserUploadChain: Promise<void> = Promise.resolve();
  readonly sessionId = Number(this.route.snapshot.paramMap.get('id'));
  readonly session = signal<any>(null);
  readonly dashboard = signal<any>(null);
  readonly error = signal('');
  readonly sessionError = signal('');
  readonly dashboardError = signal('');
  readonly message = signal('');
  readonly loading = signal(false);
  readonly acting = signal(false);
  readonly realtimeState = signal<'CONNECTING' | 'ONLINE' | 'OFFLINE'>('CONNECTING');
  readonly cameraViewMode = signal<'live' | 'ai'>('live');
  readonly aiPreviewUrl = signal<string | null>(null);
  readonly fastOverlayLatencyMs = signal<number | null>(null);
  readonly fastOverlayPeople = signal(0);
  private fastOverlayTimer: ReturnType<typeof setInterval> | null = null;
  private fastOverlayBusy = false;
  private readonly fastOverlayCapture = document.createElement('canvas');
  readonly browserCameraState = signal<'IDLE' | 'REQUESTING' | 'STREAMING' | 'ERROR'>('IDLE');
  readonly browserCameraError = signal('');
  readonly pendingAction = signal<'start' | 'stop' | 'cancel' | 'retry-finalize' | null>(null);
  readonly date = fmtDate;
  readonly statusText = statusText;

  ngOnInit(): void {
    this.startFastOverlayLoop();
    this.load();
    void this.connectRealtime();
  }

  ngOnDestroy(): void {
    if (this.fastOverlayTimer) clearInterval(this.fastOverlayTimer);
    const currentPreview = this.aiPreviewUrl();
    if (currentPreview) URL.revokeObjectURL(currentPreview);
    if (this.refreshTimer) clearTimeout(this.refreshTimer);
    void this.stopBrowserCapture(false);
    if (this.connection) {
      void this.connection.invoke('LeaveSession', this.sessionId).catch(() => undefined);
      void this.connection.stop();
    }
  }
  load(): void {
    this.loading.set(true);
    this.error.set('');
    this.sessionError.set('');
    this.dashboardError.set('');
    forkJoin({
      session: this.api.session(this.sessionId).pipe(catchError((error) => { this.sessionError.set(errorText(error)); return of(null); })),
      dashboard: this.api.sessionDashboard(this.sessionId).pipe(catchError((error) => { this.dashboardError.set(errorText(error)); return of(null); })),
    }).subscribe({
      next: (result) => {
        this.session.set(result.session); this.dashboard.set(result.dashboard); this.loading.set(false);
        if (result.session?.status === 'FINALIZING') this.scheduleFinalizeRefresh();
      },
    });
  }
  requestAction(action: 'start' | 'stop' | 'cancel' | 'retry-finalize'): void {
    if (this.acting() || this.pendingAction() !== null) return;
    this.pendingAction.set(action);
  }
  async confirmAction(): Promise<void> {
    const action = this.pendingAction();
    if (!action || this.acting()) return;
    const recoveringCompleted = action === 'retry-finalize' && this.session()?.status === 'COMPLETED';

    this.acting.set(true);
    this.error.set('');
    try {
      if (action === 'start' && this.isBrowserCamera()) {
        await this.ensureBrowserCamera();
      }

      if (action === 'stop' && this.isBrowserCamera()) {
        await this.stopBrowserCapture(true);
      }

      await firstValueFrom(this.api.sessionAction(this.sessionId, action));

      if (action === 'start' && this.isBrowserCamera()) {
        this.browserCaptureActive = true;
        this.startBrowserRecorderCycle();
      }

      this.pendingAction.set(null);
      this.message.set(
        recoveringCompleted
          ? 'Đã kiểm tra lại đầu ra còn tồn tại và tạo lại báo cáo từ dữ liệu đã lưu.'
          : action === 'start' && this.isBrowserCamera()
          ? 'Buổi học đã bắt đầu. Camera đang truyền hình ảnh để phân tích.'
          : action === 'stop' || action === 'retry-finalize'
            ? 'Đã ghi nhận yêu cầu kết thúc. Hệ thống đang hoàn tất dữ liệu.'
            : 'Đã cập nhật trạng thái buổi học.'
      );
      this.load();
    } catch (error) {
      if (action === 'start' && this.isBrowserCamera()) {
        await this.stopBrowserCapture(false);
      }
      this.error.set(errorText(error));
      this.load();
    } finally {
      this.acting.set(false);
    }
  }
  sessionSubtitle(): string { const item = this.session(); return item ? `${item.course || ''} · ${item.teacher || ''} · ${fmtDate(item.scheduledStart)}` : 'Bảng theo dõi buổi học và kết quả phân tích'; }
  behaviorMetrics(): Array<{ label: string; value: number; color: string }> {
    const behavior = this.dashboard()?.behavior ?? {};
    return [
      { label: 'Tập trung', value: this.ratio(behavior.focused), color: '#16a34a' },
      { label: 'Mất tập trung', value: this.ratio(behavior.distracted), color: '#f59e0b' },
      { label: 'Buồn ngủ', value: this.ratio(behavior.sleepy), color: '#dc2626' },
      { label: 'Hoạt động', value: this.ratio(behavior.active), color: '#2563eb' },
    ];
  }
  ratio(value: unknown): number { return Math.round(Number(value ?? 0) * 100); }
  percent(value: unknown): string { return value == null ? '—' : `${this.ratio(value)}%`; }
  behaviorLabel(value: string | null | undefined): string { return behaviorText(value); }
  isBrowserCamera(): boolean { return this.session()?.cameraMode === 'BROWSER'; }
  browserCameraStateText(): string {
    return ({ IDLE: 'Chưa mở', REQUESTING: 'Đang xin quyền', STREAMING: 'Đang truyền', ERROR: 'Lỗi camera' } as const)[this.browserCameraState()];
  }

  private async ensureBrowserCamera(): Promise<void> {
    if (this.browserStream?.active) return;
    if (!navigator.mediaDevices?.getUserMedia) {
      throw new Error('Trình duyệt không hỗ trợ truy cập camera.');
    }

    this.browserCameraState.set('REQUESTING');
    this.browserCameraError.set('');
    try {
      this.browserStream = await navigator.mediaDevices.getUserMedia({
        video: {
          width: { ideal: 1280 },
          height: { ideal: 720 },
          frameRate: { ideal: 30, max: 30 },
          facingMode: 'user',
        },
        audio: false,
      });
      await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
      if (this.browserPreview?.nativeElement) {
        this.browserPreview.nativeElement.srcObject = this.browserStream;
        await this.browserPreview.nativeElement.play().catch(() => undefined);
      }
      this.browserCameraState.set('STREAMING');
    } catch (error: any) {
      this.browserCameraState.set('ERROR');
      const message = error?.name === 'NotAllowedError'
        ? 'Bạn chưa cấp quyền sử dụng camera cho trình duyệt.'
        : error?.name === 'NotFoundError'
          ? 'Không tìm thấy camera laptop.'
          : `Không mở được camera: ${error?.message ?? error}`;
      this.browserCameraError.set(message);
      throw new Error(message);
    }
  }

  private recorderMimeType(): string {
    const options = ['video/webm;codecs=vp8', 'video/webm;codecs=vp9', 'video/webm'];
    return options.find((x) => typeof MediaRecorder !== 'undefined' && MediaRecorder.isTypeSupported(x)) ?? '';
  }

  private startBrowserRecorderCycle(): void {
    if (!this.browserCaptureActive || !this.browserStream?.active) return;
    if (this.browserRecorder?.state === 'recording') return;

    const chunks: BlobPart[] = [];
    const mimeType = this.recorderMimeType();
    const recorder = mimeType
      ? new MediaRecorder(this.browserStream, { mimeType, videoBitsPerSecond: 2_500_000 })
      : new MediaRecorder(this.browserStream, { videoBitsPerSecond: 2_500_000 });
    this.browserRecorder = recorder;

    recorder.ondataavailable = (event) => {
      if (event.data && event.data.size > 0) chunks.push(event.data);
    };

    recorder.onerror = (event: any) => {
      this.browserCameraState.set('ERROR');
      this.browserCameraError.set(`Lỗi ghi camera: ${event?.error?.message ?? 'không xác định'}`);
    };

    recorder.onstop = () => {
      if (this.browserRecorderTimer) {
        clearTimeout(this.browserRecorderTimer);
        this.browserRecorderTimer = null;
      }
      const blob = new Blob(chunks, { type: recorder.mimeType || 'video/webm' });
      if (blob.size > 0) this.enqueueBrowserChunk(blob);
      this.browserRecorder = null;
      if (this.browserCaptureActive && this.browserStream?.active) {
        this.startBrowserRecorderCycle();
      }
    };

    recorder.start();
    this.browserCameraState.set('STREAMING');
    this.browserRecorderTimer = setTimeout(() => {
      if (recorder.state === 'recording') recorder.stop();
    }, 12_000);
  }

  private enqueueBrowserChunk(blob: Blob): void {
    const index = this.browserChunkIndex++;
    this.browserUploadChain = this.browserUploadChain
      .then(() => this.uploadBrowserChunkWithRetry(blob, index));
    // Retain the rejected promise for stopBrowserCapture(), but attach a handler
    // immediately so the browser does not report an unhandled rejection.
    void this.browserUploadChain.catch(() => undefined);
  }

  private async uploadBrowserChunkWithRetry(blob: Blob, index: number): Promise<void> {
    let lastError: unknown;
    for (let attempt = 0; attempt < 5; attempt++) {
      try {
        await firstValueFrom(this.api.browserCameraChunk(this.sessionId, blob, index));
        return;
      } catch (error) {
        lastError = error;
        if (attempt < 4) await new Promise((resolve) => setTimeout(resolve, 500 * (attempt + 1)));
      }
    }
    this.browserCaptureActive = false;
    this.browserCameraState.set('ERROR');
    this.browserCameraError.set(`Không thể gửi dữ liệu camera để phân tích. ${errorText(lastError)}`);
    throw lastError;
  }

  private async stopBrowserCapture(flush: boolean): Promise<void> {
    this.browserCaptureActive = false;
    if (this.browserRecorderTimer) {
      clearTimeout(this.browserRecorderTimer);
      this.browserRecorderTimer = null;
    }

    if (this.browserRecorder && this.browserRecorder.state !== 'inactive') {
      await new Promise<void>((resolve) => {
        const recorder = this.browserRecorder!;
        const previous = recorder.onstop;
        recorder.onstop = (event) => {
          if (previous) previous.call(recorder, event);
          resolve();
        };
        recorder.stop();
      });
    }

    let uploadFailure: unknown;
    if (flush) {
      try { await this.browserUploadChain; }
      catch (error) { uploadFailure = error; }
    }

    this.browserStream?.getTracks().forEach((track) => track.stop());
    this.browserStream = null;
    if (this.browserPreview?.nativeElement) this.browserPreview.nativeElement.srcObject = null;
    this.browserCameraState.set('IDLE');
    if (uploadFailure) throw uploadFailure;
  }

  outputEntries(): Array<{key:string;label:string;status:string;count:number;message:string}> { const source=this.dashboard()?.outputs??{}; const labels:Record<string,string>={video:'Video',behaviorEvents:'Sự kiện hành vi',alerts:'Cảnh báo',identities:'Dữ liệu nhận diện',attendance:'Chuyên cần',summaries:'Báo cáo tổng hợp'}; return Object.entries(source).map(([key,value]:[string,any])=>{const status=value?.status??'EMPTY';const count=Number(value?.count??0);return{key,label:labels[key]??'Dữ liệu buổi học',status,count,message:status==='AVAILABLE'?`${count} bản ghi đã sẵn sàng`:'Chưa có dữ liệu'}}); }
  hasMissingOutputs(): boolean { const outputs=this.dashboard()?.outputs; return !!outputs && (outputs.video?.status === 'NOT_CREATED' || outputs.summaries?.status !== 'AVAILABLE'); }
  realtimeLabel(): string { return ({ CONNECTING: 'Đang kết nối', ONLINE: 'Đang cập nhật trực tiếp', OFFLINE: 'Cập nhật bị gián đoạn' } as const)[this.realtimeState()]; }
  actionTitle(): string { return this.pendingAction() === 'start' ? 'Bắt đầu buổi học' : this.pendingAction() === 'retry-finalize' ? 'Thử hoàn tất lại' : 'Kết thúc buổi học'; }
  actionMessage(): string { return this.pendingAction() === 'start' ? 'Bắt đầu phân tích cho buổi học này?' : this.pendingAction() === 'retry-finalize' ? (this.session()?.status === 'COMPLETED' ? 'Tạo lại báo cáo từ dữ liệu đã lưu?' : 'Gửi lại yêu cầu hoàn tất buổi học?') : 'Kết thúc nhận dữ liệu mới và tổng hợp kết quả buổi học?'; }

  private scheduleFinalizeRefresh(): void {
    if (this.refreshTimer) return;
    this.refreshTimer = setTimeout(() => {
      this.refreshTimer = null;
      this.load();
    }, 2_000);
  }

  private startFastOverlayLoop(): void {
    if (this.fastOverlayTimer) return;
    this.fastOverlayTimer = setInterval(() => this.captureFastOverlay(), 500);
  }

  private captureFastOverlay(): void {
    if (this.fastOverlayBusy || this.cameraViewMode() !== 'live') return;
    if (this.session()?.status !== 'RUNNING') { this.clearRealtimeOverlay(); return; }
    const video = this.browserPreview?.nativeElement;
    if (!video || video.readyState < HTMLMediaElement.HAVE_CURRENT_DATA || !video.videoWidth || !video.videoHeight) return;
    const scale = Math.min(1, 960 / video.videoWidth);
    const width = Math.max(2, Math.round(video.videoWidth * scale));
    const height = Math.max(2, Math.round(video.videoHeight * scale));
    const capture = this.fastOverlayCapture;
    capture.width = width; capture.height = height;
    const ctx = capture.getContext('2d', { alpha: false });
    if (!ctx) return;
    ctx.drawImage(video, 0, 0, width, height);
    this.fastOverlayBusy = true;
    capture.toBlob((blob) => {
      if (!blob) { this.fastOverlayBusy = false; return; }
      this.api.sessionFastOverlay(this.sessionId, blob).subscribe({
        next: (result) => {
          this.fastOverlayBusy = false;
          if (!result || result.busy) return;
          this.fastOverlayLatencyMs.set(Math.round(Number(result.processingMs ?? 0)));
          this.fastOverlayPeople.set(Array.isArray(result.detections) ? result.detections.length : 0);
          this.drawRealtimeOverlay(result);
        },
        error: () => { this.fastOverlayBusy = false; },
      });
    }, 'image/jpeg', 0.72);
  }

  private drawRealtimeOverlay(result: any): void {
    const video = this.browserPreview?.nativeElement;
    const canvas = this.realtimeOverlay?.nativeElement;
    if (!video || !canvas) return;
    const rect = video.getBoundingClientRect();
    const cw = Math.max(1, rect.width), ch = Math.max(1, rect.height);
    const dpr = Math.max(1, window.devicePixelRatio || 1);
    canvas.width = Math.round(cw * dpr); canvas.height = Math.round(ch * dpr);
    const ctx = canvas.getContext('2d'); if (!ctx) return;
    ctx.setTransform(dpr,0,0,dpr,0,0); ctx.clearRect(0,0,cw,ch);
    const sw = Math.max(1, Number(result.frameWidth || video.videoWidth || 1));
    const sh = Math.max(1, Number(result.frameHeight || video.videoHeight || 1));
    const fit = Math.min(cw/sw, ch/sh), iw = sw*fit, ih = sh*fit;
    const ox = (cw-iw)/2, oy = (ch-ih)/2;
    const colors:Record<string,string>={FOCUSED:'#16a34a',DISTRACTED:'#f59e0b',SLEEPY:'#dc2626',ACTIVE:'#2563eb',OUT_OF_VIEW:'#64748b'};
    for (const d of result.detections ?? []) {
      const b=d.bbox; if(!Array.isArray(b)||b.length!==4) continue;
      const x=ox+Number(b[0])*iw,y=oy+Number(b[1])*ih,w=(Number(b[2])-Number(b[0]))*iw,h=(Number(b[3])-Number(b[1]))*ih;
      if(w<=1||h<=1) continue;
      const stable=d.stableId as string|null;
      const row=stable?(this.dashboard()?.identities??[]).find((r:any)=>r.stableId===stable):null;
      const behavior=String(d.behavior??row?.currentBehavior??'').toUpperCase();
      const color=colors[behavior]??'#22c55e';
      const identity=d.studentId?'Sinh viên':'Đối tượng';
      const name=behavior?this.behaviorLabel(behavior):'Đang theo dõi';
      const label=`${identity} · ${name}`;
      ctx.strokeStyle=color;ctx.lineWidth=2.5;ctx.strokeRect(x,y,w,h);
      ctx.font='600 13px Inter, Arial, sans-serif';const tw=ctx.measureText(label).width,lh=25,ly=Math.max(0,y-lh);
      ctx.fillStyle=color;ctx.fillRect(x,ly,Math.min(cw-x,tw+16),lh);ctx.fillStyle='#fff';ctx.textBaseline='middle';ctx.fillText(label,x+8,ly+lh/2);
    }
  }

  private clearRealtimeOverlay(): void {
    const canvas=this.realtimeOverlay?.nativeElement;if(!canvas)return;
    const ctx=canvas.getContext('2d');if(ctx)ctx.clearRect(0,0,canvas.width,canvas.height);
    this.fastOverlayLatencyMs.set(null);this.fastOverlayPeople.set(0);
  }

  private refreshAiPreview(): void {
    this.api.sessionAiPreview(this.sessionId).subscribe({
      next: (blob) => {
        if (!blob || blob.size === 0) return;
        const nextUrl = URL.createObjectURL(blob);
        const previous = this.aiPreviewUrl();
        this.aiPreviewUrl.set(nextUrl);
        if (previous) URL.revokeObjectURL(previous);
      },
      error: () => undefined,
    });
  }

  private async connectRealtime(): Promise<void> {
    this.connection = new HubConnectionBuilder()
      .withUrl('/hubs/session', { accessTokenFactory: () => this.auth.token() ?? '' })
      .withAutomaticReconnect()
      .build();

    this.connection.on('aiEvent', () => this.scheduleRealtimeRefresh());
    this.connection.on('sessionStatus', () => this.scheduleRealtimeRefresh());
    this.connection.onreconnecting(() => this.realtimeState.set('CONNECTING'));
    this.connection.onreconnected(async () => {
      this.realtimeState.set('ONLINE');
      await this.connection?.invoke('JoinSession', this.sessionId);
      this.scheduleRealtimeRefresh();
    });
    this.connection.onclose(() => this.realtimeState.set('OFFLINE'));

    try {
      await this.connection.start();
      await this.connection.invoke('JoinSession', this.sessionId);
      this.realtimeState.set('ONLINE');
    } catch {
      this.realtimeState.set('OFFLINE');
    }
  }

  private scheduleRealtimeRefresh(): void {
    if (this.refreshTimer) return;
    this.refreshTimer = setTimeout(() => {
      this.refreshTimer = null;
      this.load();
      this.refreshAiPreview();
    }, 500);
  }
}
