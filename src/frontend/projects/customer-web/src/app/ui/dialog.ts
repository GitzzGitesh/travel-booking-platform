/**
 * Native modal <dialog> helpers: the browser provides the focus trap, Escape to close, and an inert page behind.
 * On wide screens a dialog opened from an anchor is placed next to it like a popover; on phones the
 * `dialog.modal-sheet` styles turn every dialog into a bottom sheet. Browser-only: call from event handlers.
 */
const wideScreen = '(min-width: 640px)';
const margin = 16;
const gap = 8;

export function openSheet(dialog: HTMLDialogElement, anchor?: HTMLElement | null): void {
  if (dialog.open) {
    return;
  }
  dialog.showModal();
  const anchored = !!anchor && window.matchMedia(wideScreen).matches;
  dialog.classList.toggle('anchored', anchored);
  if (!anchored) {
    dialog.style.removeProperty('top');
    dialog.style.removeProperty('left');
    return;
  }

  const target = anchor.getBoundingClientRect();
  const width = dialog.offsetWidth;
  const height = dialog.offsetHeight;
  const rtl = getComputedStyle(anchor).direction === 'rtl';

  let top = target.bottom + gap;
  if (top + height > window.innerHeight - margin) {
    const above = target.top - gap - height;
    top = above >= margin ? above : Math.max(margin, window.innerHeight - margin - height);
  }
  const preferred = rtl ? target.right - width : target.left;
  const left = Math.min(Math.max(margin, preferred), window.innerWidth - margin - width);

  dialog.style.top = `${Math.round(top)}px`;
  dialog.style.left = `${Math.round(Math.max(margin, left))}px`;
}

/** Light dismiss: a click on the backdrop targets the dialog element itself, never its content. */
export function closeOnBackdropClick(event: MouseEvent): void {
  const dialog = event.currentTarget as HTMLDialogElement;
  if (event.target === dialog) {
    dialog.close();
  }
}
