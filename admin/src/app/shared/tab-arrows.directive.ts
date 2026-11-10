import { AfterViewInit, DestroyRef, Directive, ElementRef, NgZone, inject } from '@angular/core';

/** How much of the visible row one press moves. */
const step = 0.7;

/**
 * Arrows beside a row of tabs that does not fit (T-423): on a computer the row
 * moves with them, on a phone it is still swiped (T-365) - each uses what suits
 * it. Shown only for a mouse or trackpad (`hover: hover` and `pointer: fine`),
 * only when the row overflows, and each gone at its end.
 *
 * Added to every `mat-tab-group` and `mat-tab-nav-bar`; the buttons are plain
 * elements beside Material's header, which this does not change.
 */
@Directive({
  // Material's own elements on purpose: every row of tabs gets the arrows once
  // the directive is imported, without an attribute to forget.
  // eslint-disable-next-line @angular-eslint/directive-selector
  selector: 'mat-tab-group, nav[mat-tab-nav-bar]',
})
export class TabArrowsDirective implements AfterViewInit {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly zone = inject(NgZone);
  private readonly destroyRef = inject(DestroyRef);

  ngAfterViewInit(): void {
    if (typeof window === 'undefined' || !window.matchMedia?.('(hover: hover) and (pointer: fine)').matches) {
      return;
    }

    const host = this.host.nativeElement;
    const header = host.classList.contains('mat-mdc-tab-header')
      ? host
      : host.querySelector<HTMLElement>(':scope > .mat-mdc-tab-header');
    const anchor = header?.parentElement;

    if (!header || !anchor) {
      return;
    }

    this.zone.runOutsideAngular(() => {
      anchor.classList.add('neo-tab-arrows');
      const back = this.arrow('chevron_left', 'neo-tab-arrow--start', () => this.move(header, -1));
      const forward = this.arrow('chevron_right', 'neo-tab-arrow--end', () => this.move(header, 1));
      anchor.append(back, forward);

      const update = (): void => {
        // At an end its arrow goes, so it covers no label there.
        back.hidden = header.scrollLeft <= 0;
        forward.hidden = header.scrollLeft + header.clientWidth >= header.scrollWidth - 1;
        back.style.top = forward.style.top = `${header.offsetTop}px`;
        back.style.height = forward.style.height = `${header.offsetHeight}px`;
      };

      const resize = new ResizeObserver(update);
      resize.observe(header);
      header.addEventListener('scroll', update, { passive: true });
      update();

      this.destroyRef.onDestroy(() => {
        resize.disconnect();
        header.removeEventListener('scroll', update);
        back.remove();
        forward.remove();
      });
    });
  }

  private move(header: HTMLElement, direction: 1 | -1): void {
    header.scrollBy({ left: direction * header.clientWidth * step, behavior: 'smooth' });
  }

  private arrow(icon: string, side: string, press: () => void): HTMLButtonElement {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = `neo-tab-arrow ${side}`;
    // The row's labels are read on their own; the arrows only move them.
    button.setAttribute('aria-hidden', 'true');
    button.tabIndex = -1;
    const glyph = document.createElement('span');
    glyph.className = 'material-symbols-rounded';
    glyph.textContent = icon;
    button.append(glyph);
    button.addEventListener('click', press);
    return button;
  }
}
