import { DOCUMENT } from '@angular/common';
import { HttpClient, HttpParams } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, ElementRef, HostListener, Input, OnChanges, OnDestroy, ViewChild, forwardRef, inject, signal } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { Subscription } from 'rxjs';
import { displayBusinessCode, errorText } from './ui';

export interface LookupOption { id: number; code: string; name: string; isActive: boolean; }

@Component({
  selector: 'app-search-select', standalone: true, changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => SearchSelectComponent), multi: true }],
  template: `
    <div class="search-select-control">
      <input #input type="text" role="combobox" autocomplete="off" [attr.aria-label]="label"
        aria-autocomplete="list" aria-haspopup="listbox" [attr.aria-expanded]="open()"
        [attr.aria-controls]="panelId" [attr.aria-activedescendant]="open() && active() >= 0 ? panelId + '-' + active() : null"
        [placeholder]="placeholder" [disabled]="disabled" [value]="open() ? query() : display()"
        (focus)="show()" (click)="show()" (input)="search($event)" (keydown)="keydown($event)" (blur)="touched()" />
      @if (value !== null && !disabled) {
        <button type="button" class="search-select-clear" aria-label="Bỏ lựa chọn" (click)="clear($event)">×</button>
      }
    </div>
    <span #panelHost>
      <div #panel class="search-select-panel" [id]="panelId" [hidden]="!open()" (mousedown)="$event.preventDefault()">
        <div role="listbox" [attr.aria-label]="label" [attr.aria-busy]="loading()">
          @for (option of options(); track option.id; let i = $index) {
            <div role="option" class="search-select-option" [id]="panelId + '-' + i"
              [class.highlighted]="active() === i" [attr.aria-selected]="option.id === value"
              [attr.aria-disabled]="!option.isActive" (mouseenter)="active.set(i)" (click)="choose(option)">
              @if(resource !== 'courses'){<strong>{{ codeLabel(option.code) }}</strong>}<span>{{markLegacyDepartments ? '— ' : ''}}{{ option.name }}</span>
              @if (markLegacyDepartments && legacyDepartment(option.code)) { <small class="badge">Mã cũ</small> }
              @if (!option.isActive) { <small>Ngừng sử dụng</small> }
            </div>
          }
        </div>
        @if (loading()) { <p class="search-select-message" role="status">Đang tải…</p> }
        @if (error()) { <p class="search-select-message field-error" role="alert">{{ error() }}</p><button type="button" class="secondary small" (click)="load(page)">Thử lại</button> }
        @if (!loading() && !error() && !options().length) { <p class="search-select-message">Không có kết quả phù hợp.</p> }
        @if (hasMore() && !loading()) { <button type="button" class="search-select-more secondary small" (click)="load(page + 1)">Tải thêm</button> }
      </div>
    </span>
  `,
})
export class SearchSelectComponent implements ControlValueAccessor, OnChanges, OnDestroy {
  private static nextId = 0;
  private readonly http = inject(HttpClient);
  private readonly document = inject(DOCUMENT);
  private readonly host = inject(ElementRef<HTMLElement>);
  @Input({ required: true }) resource = '';
  @Input() label = 'Chọn bản ghi';
  @Input() placeholder = 'Tìm mã hoặc tên…';
  @Input() filters: Record<string, string | number | boolean | null | undefined> = {};
  @Input() selectedLabel = '';
  @Input() boundToModal = false;
  @Input() constrainWidth = false;
  @Input() markLegacyDepartments = false;
  readonly codeLabel=displayBusinessCode;
  optionLabel(option:LookupOption):string{return this.resource==='courses'?option.name:`${displayBusinessCode(option.code)} · ${option.name}`;}
  legacyDepartment(code:string):boolean{return !/^BM-[A-Z0-9]+(?:-[0-9]{2})?$/.test(code)||/^BM-[A-F0-9]{32}(?:-[0-9]{2})?$/.test(code)||code.length>36;}
  @ViewChild('input', { static: true }) private input?: ElementRef<HTMLInputElement>;
  @ViewChild('panel', { static: true }) private panel?: ElementRef<HTMLElement>;
  @ViewChild('panelHost', { static: true }) private panelHost?: ElementRef<HTMLElement>;
  readonly panelId = `search-select-${++SearchSelectComponent.nextId}`;
  readonly open = signal(false);
  readonly display = signal('');
  readonly query = signal('');
  readonly options = signal<LookupOption[]>([]);
  readonly loading = signal(false);
  readonly hasMore = signal(false);
  readonly error = signal('');
  readonly active = signal(-1);
  value: number | null = null;
  disabled = false;
  page = 1;
  touched = () => {};
  private changed: (value: number | null) => void = () => {};
  private request?: Subscription;
  private labelRequest?: Subscription;
  private debounce?: ReturnType<typeof setTimeout>;
  private positionTimer?: ReturnType<typeof setTimeout>;
  private readonly scrollListener = (event: Event) => {
    if (!this.panel?.nativeElement.contains(event.target as Node)) { if (this.boundToModal) this.position(); else this.close(); }
  };
  private readonly lockScroll = (event: Event) => { if (!this.panel?.nativeElement.contains(event.target as Node)) event.preventDefault(); };

  ngOnChanges(): void {
    this.close();
    this.resolveLabel();
  }

  writeValue(value: number | null): void { this.value = value == null ? null : Number(value); this.resolveLabel(); }
  registerOnChange(fn: (value: number | null) => void): void { this.changed = fn; }
  registerOnTouched(fn: () => void): void { this.touched = fn; }
  setDisabledState(disabled: boolean): void { this.disabled = disabled; if (disabled) this.close(); }

  show(): void {
    if (this.disabled || this.open() || !this.panel) return;
    this.open.set(true); this.query.set(''); this.active.set(-1);
    const modal = this.boundToModal ? this.host.nativeElement.closest('.modal-dialog') : null;
    (modal ?? this.document.body).appendChild(this.panel.nativeElement);
    this.document.addEventListener('scroll', this.scrollListener, true);
    if (this.boundToModal) {
      this.document.addEventListener('wheel', this.lockScroll, {capture:true,passive:false});
      this.document.addEventListener('touchmove', this.lockScroll, {capture:true,passive:false});
    }
    this.load(1);
    this.position();
  }

  search(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
    // Cancel now, before debounce, so an old response cannot replace the new query.
    this.request?.unsubscribe();
    clearTimeout(this.debounce);
    this.options.set([]); this.hasMore.set(false);
    this.loading.set(true); this.active.set(-1);
    this.debounce = setTimeout(() => this.load(1), 250);
  }

  load(page: number): void {
    this.request?.unsubscribe(); this.error.set(''); this.loading.set(true); this.page = page;
    if (page === 1) this.options.set([]);
    this.request = this.http.get<{ items: LookupOption[]; hasMore: boolean }>(`/api/lookups/${this.resource}`, { params: this.params().set('q', this.query()).set('page', page).set('pageSize', 10) }).subscribe({
      next: (result) => { this.options.update(old => page === 1 ? result.items : [...old, ...result.items]); this.hasMore.set(result.hasMore); this.loading.set(false); this.position(); },
      error: (error) => { this.error.set(errorText(error)); this.loading.set(false); this.position(); },
    });
  }

  choose(option: LookupOption): void {
    if (!option.isActive) return;
    this.value = option.id; this.display.set(this.optionLabel(option)); this.changed(option.id); this.touched();
    this.close(); this.input?.nativeElement.focus();
  }

  clear(event: MouseEvent): void {
    event.stopPropagation(); this.value = null; this.display.set(''); this.changed(null); this.close(); this.touched();
  }

  keydown(event: KeyboardEvent): void {
    if (event.key === 'Escape' && this.open()) { event.preventDefault(); event.stopPropagation(); this.close(); return; }
    if (event.key === 'Tab') { this.close(); return; }
    if (this.boundToModal && this.open() && ['PageDown','PageUp'].includes(event.key)) { event.preventDefault(); return; }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault(); this.show();
      const count = this.options().length;
      if (count) {
        this.active.set(this.active() < 0 ? (event.key === 'ArrowDown' ? 0 : count - 1) : (this.active() + (event.key === 'ArrowDown' ? 1 : count - 1) + count) % count);
        const option = this.document.getElementById(`${this.panelId}-${this.active()}`);
        option?.scrollIntoView({ block: 'nearest' });
      }
    }
    if (event.key === 'Enter' && this.open()) {
      event.preventDefault(); const option = this.options()[this.active()]; if (option) this.choose(option);
    }
  }

  @HostListener('document:click', ['$event'])
  outside(event: MouseEvent): void {
    if (!this.host.nativeElement.contains(event.target as Node) && !this.panel?.nativeElement.contains(event.target as Node)) this.close();
  }
  @HostListener('window:resize') closeOnResize(): void { this.close(); }

  close(): void {
    this.open.set(false); clearTimeout(this.debounce); clearTimeout(this.positionTimer); this.request?.unsubscribe();
    this.document.removeEventListener('scroll', this.scrollListener, true);
    this.document.removeEventListener('wheel', this.lockScroll, {capture:true});
    this.document.removeEventListener('touchmove', this.lockScroll, {capture:true});
    if (this.panel && this.panelHost && this.panel.nativeElement.parentElement !== this.panelHost.nativeElement) this.panelHost.nativeElement.appendChild(this.panel.nativeElement);
  }
  ngOnDestroy(): void { this.close(); this.labelRequest?.unsubscribe(); }

  private params(): HttpParams {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(this.filters)) if (value != null) params = params.set(key, String(value));
    return params;
  }
  private resolveLabel(): void {
    this.labelRequest?.unsubscribe();
    if (this.value === null) { this.display.set(''); return; }
    if (this.selectedLabel) this.display.set(this.selectedLabel);
    if (!this.resource) return;
    this.labelRequest = this.http.get<{ items: LookupOption[] }>(`/api/lookups/${this.resource}`, { params: this.params().set('id', this.value) }).subscribe({
      next: (result) => { const item = result.items[0]; if (item) this.display.set(this.optionLabel(item)+(item.isActive ? '' : ' (ngừng sử dụng)')); },
      error: () => this.display.set(this.selectedLabel || `#${this.value}`),
    });
  }
  private position(): void {
    clearTimeout(this.positionTimer);
    this.positionTimer = setTimeout(() => {
      const panel = this.panel?.nativeElement; const input = this.input?.nativeElement; const view = this.document.defaultView;
      if (!panel || !input || !view || !this.open()) return;
      const rect = input.getBoundingClientRect(); const height = view.visualViewport?.height ?? view.innerHeight;
      const modal = this.boundToModal ? this.host.nativeElement.closest('.modal-dialog') : null;
      const top = Math.max(8, modal?.querySelector('header')?.getBoundingClientRect().bottom ?? 8);
      const bottom = Math.min(height - 8, modal?.querySelector('.import-footer, .catalog-footer')?.getBoundingClientRect().top ?? height - 8);
      const below = this.boundToModal ? Math.max(0,bottom - rect.bottom - 4) : height - rect.bottom - 12;
      const above = this.boundToModal ? Math.max(0,rect.top - top - 4) : rect.top - 12;
      const limit = this.boundToModal ? 320 : 390;
      const maxHeight = Math.min(limit, Math.max(below, above, this.boundToModal ? 0 : 80));
      if (maxHeight < 48) { this.close(); return; }
      panel.style.maxHeight = `${maxHeight}px`; panel.style.width = `${Math.min(this.constrainWidth ? rect.width : Math.max(rect.width, 260), view.innerWidth - 16)}px`;
      panel.style.left = `${Math.max(8, Math.min(rect.left, view.innerWidth - panel.offsetWidth - 8))}px`;
      panel.style.top = `${Math.max(top, below >= Math.min(panel.scrollHeight, limit) || below >= above ? rect.bottom + 4 : rect.top - Math.min(panel.scrollHeight, maxHeight) - 4)}px`;
    });
  }
}
