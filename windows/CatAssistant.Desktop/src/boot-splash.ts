const bootElement = () => document.getElementById('boot');

let hidden = false;

function setStatus(text: string, tone: 'default' | 'danger'): void {
  const boot = bootElement();
  if (!boot || hidden) return;
  const status = boot.querySelector<HTMLElement>('#boot-status');
  const label = boot.querySelector<HTMLElement>('#boot-text');
  const progress = boot.querySelector<HTMLElement>('#boot-progress');
  if (!status || !label || !progress) return;
  label.textContent = text;
  status.classList.toggle('danger', tone === 'danger');
  progress.style.display = tone === 'danger' ? 'none' : '';
}

export function showSplashError(text: string): void { setStatus(text, 'danger'); }

export function hideSplash(): void {
  const boot = bootElement();
  if (!boot || hidden) return;
  hidden = true;
  boot.classList.add('is-hidden');
  // With the white startup screen gone, switch the caption-button area to the app's
  // grey title-bar colour so it keeps matching whatever is behind it.
  window.catAssistant?.markChromeReady?.();
  setTimeout(() => { if (boot.isConnected) boot.remove(); }, 400);
}
