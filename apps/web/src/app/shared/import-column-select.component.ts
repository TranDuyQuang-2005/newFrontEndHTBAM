import { DOCUMENT } from '@angular/common';
import { Component, ElementRef, EventEmitter, HostListener, Input, OnDestroy, Output, ViewChild, inject, signal } from '@angular/core';

export interface ImportColumnChoice { index: number; label: string; usedBy?: string; }

export function columnPanelPlacement(anchor: { top: number; bottom: number; left: number; width: number }, top: number, bottom: number, viewportWidth: number) {
  const below = Math.max(0, bottom - anchor.bottom - 4), above = Math.max(0, anchor.top - top - 4);
  const up = below < 280 && above > below;
  const height = Math.min(320, up ? above : below);
  const width = Math.min(Math.max(anchor.width, 260), viewportWidth - 16);
  return { height, width, left: Math.max(8, Math.min(anchor.left, viewportWidth - width - 8)), top: up ? anchor.top - height - 4 : anchor.bottom + 4, up };
}

@Component({
  selector: 'app-import-column-select', standalone: true,
  template: `
    <input #input type="text" role="combobox" autocomplete="off" aria-autocomplete="list" aria-haspopup="listbox"
      [attr.aria-label]="'Tìm cột cho ' + label" [attr.aria-controls]="panelId" [attr.aria-expanded]="open()"
      [attr.aria-activedescendant]="open() && active() >= 0 ? panelId + '-' + active() : null"
      [value]="open() ? query() : selectedLabel()" [disabled]="disabled" placeholder="Tìm tên, cột Excel hoặc dữ liệu mẫu…"
      (click)="show()" (input)="search($event)" (keydown)="keydown($event)" />
    <span #panelHost><div #panel class="import-column-panel" [id]="panelId" [hidden]="!open()" role="listbox" [attr.aria-label]="label" (mousedown)="$event.preventDefault()">
      @for(option of choices(); track option.index; let i = $index){
        <div role="option" class="search-select-option" [id]="panelId + '-' + i" [class.highlighted]="active() === i"
          [attr.aria-selected]="option.index === value" [attr.aria-disabled]="!!option.usedBy"
          (mouseenter)="active.set(i)" (click)="choose(option)">
          {{option.label}} @if(option.usedBy){<small>Đã dùng cho {{option.usedBy}}</small>}
        </div>
      } @empty {<p class="search-select-message">Không có cột phù hợp.</p>}
    </div></span>`,
})
export class ImportColumnSelectComponent implements OnDestroy {
  private static nextId = 0;
  private readonly document = inject(DOCUMENT);
  private readonly host = inject(ElementRef<HTMLElement>);
  @Input() columns: ImportColumnChoice[] = [];
  @Input() value: number | null = null;
  @Input() label = '';
  @Input() disabled = false;
  @Output() valueChange = new EventEmitter<number | null>();
  @ViewChild('input', { static: true }) private input?: ElementRef<HTMLInputElement>;
  @ViewChild('panel', { static: true }) private panel?: ElementRef<HTMLElement>;
  @ViewChild('panelHost', { static: true }) private panelHost?: ElementRef<HTMLElement>;
  readonly panelId = `import-column-${++ImportColumnSelectComponent.nextId}`;
  readonly open = signal(false); readonly query = signal(''); readonly active = signal(-1);
  private positionTimer?: ReturnType<typeof setTimeout>;
  private readonly lockScroll = (event: Event) => { if (!this.panel?.nativeElement.contains(event.target as Node)) event.preventDefault(); };
  // Focusing a lower field can finish a native scroll after its click. Follow the anchor;
  // closing here would dismiss a newly opened list before the user can choose anything.
  private readonly followScroll = (event: Event) => { if (!this.panel?.nativeElement.contains(event.target as Node)) this.position(); };
  private readonly closeOnViewportChange = () => this.close();
  private normalize(value: string): string { return value.toLocaleLowerCase('vi').normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/đ/g, 'd'); }
  selectedLabel(): string { return this.columns.find(x => x.index === this.value)?.label ?? 'Không ánh xạ'; }
  choices(): ImportColumnChoice[] {
    const q = this.normalize(this.query());
    return [{index: -1, label: 'Không ánh xạ'}, ...this.columns].filter(x => this.normalize(x.label + ' ' + ('usedBy' in x ? x.usedBy ?? '' : '')).includes(q));
  }
  show(): void {
    if (this.disabled || this.open() || !this.panel) return;
    this.open.set(true); this.query.set(''); this.active.set(-1);
    // Keep the list inside the modal's focus trap, outside its scrolling body.
    (this.host.nativeElement.closest('.modal-dialog') ?? this.document.body).appendChild(this.panel.nativeElement);
    this.document.addEventListener('wheel', this.lockScroll, { capture: true, passive: false });
    this.document.addEventListener('touchmove', this.lockScroll, { capture: true, passive: false });
    this.document.addEventListener('scroll', this.followScroll, true);
    this.document.defaultView?.visualViewport?.addEventListener('resize', this.closeOnViewportChange);
    this.document.defaultView?.visualViewport?.addEventListener('scroll', this.closeOnViewportChange);
    this.positionTimer = setTimeout(() => this.position());
  }
  search(event: Event): void { this.show(); this.query.set((event.target as HTMLInputElement).value); this.active.set(-1); }
  choose(option: ImportColumnChoice): void {
    if (option.usedBy) return;
    this.value = option.index < 0 ? null : option.index; this.valueChange.emit(this.value); this.close(); this.input?.nativeElement.focus();
  }
  keydown(event: KeyboardEvent): void {
    if (event.key === 'Escape' && this.open()) { event.preventDefault(); event.stopPropagation(); this.close(); return; }
    if (event.key === 'Tab') { this.close(); return; }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault(); this.show(); const options = this.choices();
      const direction = event.key === 'ArrowDown' ? 1 : -1; let next = this.active() < 0 && direction < 0 ? options.length : this.active();
      for (let i = 0; i < options.length; i++) { next = (next + direction + options.length) % options.length; if (!options[next].usedBy) { this.active.set(next); break; } }
      this.document.getElementById(`${this.panelId}-${this.active()}`)?.scrollIntoView({block: 'nearest'});
    }
    if (this.open() && ['PageDown','PageUp','Home','End'].includes(event.key)) {
      event.preventDefault(); const options = this.choices(); const enabled = options.map((option,i)=>option.usedBy?-1:i).filter(i=>i>=0);
      this.active.set((event.key === 'End' || event.key === 'PageDown' ? enabled.at(-1) : enabled[0]) ?? -1);
      this.document.getElementById(`${this.panelId}-${this.active()}`)?.scrollIntoView({block:'nearest'});
    }
    if (event.key === 'Enter' && this.open()) { event.preventDefault(); const option = this.choices()[this.active()]; if (option) this.choose(option); }
  }
  @HostListener('document:pointerdown', ['$event']) outside(event: Event): void { if (!this.host.nativeElement.contains(event.target as Node) && !this.panel?.nativeElement.contains(event.target as Node)) this.close(); }
  @HostListener('document:focusin', ['$event']) focusOutside(event: Event): void { this.outside(event); }
  @HostListener('window:resize') resize(): void { this.close(); }
  private position(): void {
    const panel = this.panel?.nativeElement, input = this.input?.nativeElement, view = this.document.defaultView;
    if (!panel || !input || !view || !this.open()) return;
    const dialog = this.host.nativeElement.closest('.modal-dialog');
    const footer = dialog?.querySelector('.import-footer')?.getBoundingClientRect();
    const header = dialog?.querySelector('header')?.getBoundingClientRect();
    const viewport = view.visualViewport;
    const placement = columnPanelPlacement(input.getBoundingClientRect(), Math.max((viewport?.offsetTop ?? 0) + 8, header?.bottom ?? 8),
      Math.min((viewport?.offsetTop ?? 0) + (viewport?.height ?? view.innerHeight) - 8, footer?.top ?? view.innerHeight - 8), view.innerWidth);
    if (placement.height < 48) { this.close(); return; }
    Object.assign(panel.style, {height: `${placement.height}px`, maxHeight: '320px', width: `${placement.width}px`, left: `${placement.left}px`, top: `${placement.top}px`});
  }
  close(): void {
    this.open.set(false); clearTimeout(this.positionTimer);
    this.document.removeEventListener('wheel', this.lockScroll, {capture:true}); this.document.removeEventListener('touchmove', this.lockScroll, {capture:true}); this.document.removeEventListener('scroll', this.followScroll, true);
    this.document.defaultView?.visualViewport?.removeEventListener('resize', this.closeOnViewportChange);
    this.document.defaultView?.visualViewport?.removeEventListener('scroll', this.closeOnViewportChange);
    if (this.panel && this.panelHost && this.panel.nativeElement.parentElement !== this.panelHost.nativeElement) this.panelHost.nativeElement.appendChild(this.panel.nativeElement);
  }
  ngOnDestroy(): void { this.close(); }
}
