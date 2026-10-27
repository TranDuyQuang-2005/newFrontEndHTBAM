const focusableSelector = 'a[href], button:not(:disabled), input:not([type="hidden"]):not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])';

/** Shared by dialogs and the mobile drawer so nested overlays keep the scroll lock. */
export class OverlayFocus {
  private static readonly stacks = new WeakMap<Document, OverlayFocus[]>();
  private static readonly overflow = new WeakMap<Document, string>();
  private returnFocus: HTMLElement | null = null;
  private container?: HTMLElement;
  private focusTimer?: ReturnType<typeof setTimeout>;
  private onEscape?: () => void;

  constructor(private readonly document: Document) {}

  open(container: HTMLElement, onEscape: () => void, preferred?: HTMLElement | null): void {
    if (this.container) return;
    this.container = container;
    this.onEscape = onEscape;
    this.returnFocus = this.document.activeElement as HTMLElement | null;
    const stack = OverlayFocus.stacks.get(this.document) ?? [];
    if (!stack.length) {
      OverlayFocus.overflow.set(this.document, this.document.body.style.overflow);
      this.document.body.style.overflow = 'hidden';
    }
    stack.push(this);
    OverlayFocus.stacks.set(this.document, stack);
    this.document.addEventListener('keydown', this.onKeydown);
    this.focusTimer = setTimeout(() => {
      const items = this.focusable();
      const target = preferred && items.includes(preferred) ? preferred : items[0] ?? container;
      if (stack.at(-1) === this) target.focus();
    });
  }

  close(restoreFocus = true): void {
    if (!this.container) return;
    clearTimeout(this.focusTimer);
    this.document.removeEventListener('keydown', this.onKeydown);
    const stack = OverlayFocus.stacks.get(this.document) ?? [];
    const wasTop = stack.at(-1) === this;
    const index = stack.indexOf(this);
    if (index >= 0) stack.splice(index, 1);
    if (!stack.length) {
      this.document.body.style.overflow = OverlayFocus.overflow.get(this.document) ?? '';
      OverlayFocus.overflow.delete(this.document);
    }
    this.container = undefined;
    const target = this.returnFocus;
    if (restoreFocus && wasTop) {
      setTimeout(() => {
        if (target?.isConnected && !target.closest('[hidden], [inert]')) target.focus();
      });
    }
  }

  private readonly onKeydown = (event: KeyboardEvent): void => {
    if (OverlayFocus.stacks.get(this.document)?.at(-1) !== this) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopImmediatePropagation();
      this.onEscape?.();
      return;
    }
    if (event.key !== 'Tab' || !this.container) return;
    const items = this.focusable();
    const first = items[0];
    const last = items.at(-1);
    const active = this.document.activeElement;
    if (!first) {
      event.preventDefault();
      this.container.focus();
    } else if (event.shiftKey && (active === first || !items.includes(active as HTMLElement))) {
      event.preventDefault();
      last?.focus();
    } else if (!event.shiftKey && (active === last || !items.includes(active as HTMLElement))) {
      event.preventDefault();
      first.focus();
    }
  };

  private focusable(): HTMLElement[] {
    return Array.from(this.container?.querySelectorAll<HTMLElement>(focusableSelector) ?? [])
      .filter((element) => element.tabIndex >= 0 && element.getClientRects().length > 0 &&
        !element.closest('[hidden], [inert], [aria-hidden="true"]'));
  }
}
