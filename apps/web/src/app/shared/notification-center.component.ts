import { DatePipe } from '@angular/common';
import { Component, ElementRef, HostListener, Input, OnChanges, ViewChild, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';

const DEMO_KEY = 'system-demo-data-v1';
interface Notice { key: string; title: string; content: string; createdAt: string; read: boolean; }

@Component({
  selector: 'app-notification-center', standalone: true, imports: [DatePipe, RouterLink],
  styles: [`:host{position:relative;display:block}.bell{position:relative}.unread-count{position:absolute;right:-3px;top:-5px;min-width:17px;height:17px;border-radius:10px;background:#dc2626;color:white;font-size:11px;display:grid;place-items:center}.notification-panel{position:absolute;right:0;top:calc(100% + 12px);width:370px;max-width:calc(100vw - 24px);background:white;border:1px solid #e2e8f0;border-radius:14px;box-shadow:0 16px 45px #0f172a26;z-index:2000;overflow:hidden;color:#334155}.notification-heading{display:flex;justify-content:space-between;gap:8px;align-items:center;padding:16px;border-bottom:1px solid #e2e8f0}.notification-heading h2{font-size:17px;margin:0}.notification-panel button{font-size:12px}.notification-list{max-height:min(400px,50dvh);overflow-y:auto}.notification-item{padding:16px;border-bottom:1px solid #e2e8f0}.notification-item.unread{background:#eff6ff}.notification-item h3{font-size:14px;margin:8px 0}.notification-item p{font-size:13px;line-height:1.6;margin:8px 0;white-space:normal}.notification-item time{display:block;font-size:11px;color:#64748b}.notification-footer{display:flex;gap:12px;align-items:center;padding:12px 16px;font-size:13px}.notification-empty{padding:24px 16px;font-size:13px}.notification-actions{display:flex;gap:8px;margin-top:12px}@media(max-width:600px){.notification-panel{position:fixed;right:12px;top:76px;width:calc(100vw - 24px)}}`],
  template: `<button #bell type="button" class="icon-button bell" aria-label="Thông báo hệ thống và dữ liệu DEMO" aria-controls="notification-panel" [attr.aria-expanded]="open()" (click)="toggle()">
    <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><path d="M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9M13.7 21a2 2 0 0 1-3.4 0"/></svg>
    @if(unreadCount()){<span class="unread-count" [attr.aria-label]="unreadCount() + ' thông báo chưa đọc'">{{unreadCount()}}</span>}</button>
    @if(open()){<section id="notification-panel" class="notification-panel" aria-label="Trung tâm thông báo" (keydown.escape)="close(true);$event.stopPropagation()">
      <div class="notification-heading"><h2>Thông báo hệ thống</h2><button type="button" class="secondary" [disabled]="!unreadCount()" (click)="markAll()">Đánh dấu tất cả đã đọc</button></div>
      <div class="notification-list">@for(notice of notices();track notice.key){<article class="notification-item" [class.unread]="!notice.read">
        <span class="badge warning">Dữ liệu DEMO · {{notice.read ? 'Đã đọc' : 'Chưa đọc'}}</span><h3>{{notice.title}}</h3><p>{{notice.content}}</p><time [attr.datetime]="notice.createdAt">{{notice.createdAt | date:'dd/MM/yyyy HH:mm'}}</time>
        @if(!notice.read){<div class="notification-actions"><button type="button" class="secondary" (click)="markRead(notice.key)">Đánh dấu đã đọc</button></div>}
      </article>}@empty{<p class="notification-empty">Không có thông báo mới.</p>}</div>
      <div class="notification-footer">@if(demo && auth.hasPermission('CLASS_SECTION_VIEW')){<a routerLink="/management" [queryParams]="{tab:'class-sections'}" (click)="close(false)">Xem dữ liệu DEMO</a>}<button type="button" class="secondary" (click)="close(true)">Đóng</button></div>
    </section>}`,
})
export class NotificationCenterComponent implements OnChanges {
  @Input() demo = false;
  readonly auth = inject(AuthService);
  @ViewChild('bell') bell?: ElementRef<HTMLButtonElement>;
  readonly open = signal(false);
  readonly notices = signal<Notice[]>([]);
  readonly unreadCount = computed(() => this.notices().filter(x => !x.read).length);
  private storageKey = '';

  constructor(private host: ElementRef<HTMLElement>) {}

  ngOnChanges(): void {
    this.storageKey = `htbam:notifications:${this.auth.user().id ?? this.auth.user().userName}`;
    if(!this.demo){this.notices.set([]);return;}
    let state: { read?: boolean; createdAt?: string } = {};
    try { state = JSON.parse(localStorage.getItem(this.storageKey) ?? '{}')?.[DEMO_KEY] ?? {}; } catch { /* Storage can be unavailable. */ }
    this.notices.set([{key:DEMO_KEY,title:'Dữ liệu mẫu phục vụ kiểm thử',content:'Lịch học, mốc tuần và một số kết quả tổng hợp hiện là dữ liệu DEMO, chưa phải dữ liệu chính thức của nhà trường.',createdAt:state.createdAt && !isNaN(Date.parse(state.createdAt)) ? state.createdAt : new Date().toISOString(),read:state.read === true}]);
    this.persist();
  }
  toggle(): void { this.open.update(value=>!value);if(this.open())setTimeout(()=>this.host.nativeElement.querySelector<HTMLButtonElement>('.notification-panel button:not(:disabled)')?.focus()); }
  close(focus: boolean): void { this.open.set(false);if(focus)this.bell?.nativeElement.focus(); }
  markRead(key: string): void { this.notices.update(items=>items.map(item=>item.key===key?{...item,read:true}:item));this.persist(); }
  markAll(): void { this.notices.update(items=>items.map(item=>({...item,read:true})));this.persist(); }
  private persist(): void { try {localStorage.setItem(this.storageKey,JSON.stringify(Object.fromEntries(this.notices().map(x=>[x.key,{read:x.read,createdAt:x.createdAt}]))));} catch { /* Keep in-memory read state. */ } }
  @HostListener('document:pointerdown',['$event']) outside(event: Event): void { if(this.open()&&!this.host.nativeElement.contains(event.target as Node))this.close(false); }
  @HostListener('document:focusin',['$event']) outsideFocus(event: Event): void { if(this.open()&&!this.host.nativeElement.contains(event.target as Node))this.close(false); }
  @HostListener('document:keydown.escape',['$event']) escape(event: KeyboardEvent): void { if(this.open()){event.preventDefault();event.stopPropagation();this.close(true);} }
}
