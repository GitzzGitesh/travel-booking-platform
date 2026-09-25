/**
 * Unit tests only: jsdom has no modal <dialog> or matchMedia. This adds the minimum the dialog helpers use,
 * so specs exercise the real open and close flow. Browsers (and the Playwright journeys) use the native API.
 */
export function installDialogShim(): void {
  const proto = HTMLDialogElement.prototype;
  if (typeof proto.showModal !== 'function') {
    proto.showModal = function (this: HTMLDialogElement) {
      this.setAttribute('open', '');
    };
  }
  if (typeof proto.close !== 'function' || !('__shim' in proto)) {
    Object.assign(proto, {
      __shim: true,
      close(this: HTMLDialogElement) {
        if (this.hasAttribute('open')) {
          this.removeAttribute('open');
          this.dispatchEvent(new Event('close'));
        }
      },
    });
  }
  if (typeof window.matchMedia !== 'function') {
    window.matchMedia = (query: string) => ({ matches: false, media: query }) as MediaQueryList;
  }
}
