export const modeledAxisNames = ['L0', 'L1', 'L2', 'R0', 'R1', 'R2'] as const;
export type ModeledAxisName = typeof modeledAxisNames[number];

type Emulator = import('osr-emu').OSREmulator;

let emulator: Emulator | null = null;
let previewHost: HTMLElement | null = null;
let mountRevision = 0;
let pendingValues: Record<ModeledAxisName, number> = neutralAxisValues();
let frame: number | undefined;
let resizeObserver: ResizeObserver | null = null;
let visibilityObserver: IntersectionObserver | null = null;
let visible = false;
let lastCommand = '';
let rendering = false;

function cancelFrame(): void {
  if (frame !== undefined) cancelAnimationFrame(frame);
  frame = undefined;
}

function requestRender(): void {
  if (rendering || frame !== undefined || !emulator || !visible || document.hidden) return;
  frame = requestAnimationFrame(() => {
    frame = undefined;
    if (!emulator || !visible || document.hidden || !previewHost?.isConnected) return;
    rendering = true;
    try { emulator.render(); } finally { rendering = false; }
  });
}

function onVisibilityChange(): void {
  if (document.hidden) cancelFrame();
  else requestRender();
}

function neutralAxisValues(): Record<ModeledAxisName, number> {
  return Object.fromEntries(modeledAxisNames.map((axis) => [axis, 0.5])) as Record<ModeledAxisName, number>;
}

function normalize(value: number): number {
  return Number.isFinite(value) ? Math.max(0, Math.min(1, value)) : 0.5;
}

function tcode(values: Record<ModeledAxisName, number>): string {
  return `${modeledAxisNames.map((axis) => {
    const magnitude = Math.round(normalize(values[axis]) * 9999).toString().padStart(4, '0');
    // The UI samples an explicit pose. No hidden interpolation loop is needed.
    return `${axis}${magnitude}`;
  }).join(' ')}\n`;
}

export async function mountOsrAxisPreview(host: HTMLElement): Promise<void> {
  if (previewHost === host && emulator) return;
  destroyOsrAxisPreview();
  const revision = ++mountRevision;
  previewHost = host;
  host.dataset.modelState = 'loading';
  host.replaceChildren(Object.assign(document.createElement('span'), {
    className: 'axis-model-status',
    textContent: 'Loading SR6 model...',
  }));

  try {
    const { OSREmulator } = await import('osr-emu');
    if (revision !== mountRevision || previewHost !== host || !host.isConnected) return;
    const instance = new OSREmulator(host, { model: 'SR6', renderOnDemand: true });
    if (revision !== mountRevision || previewHost !== host || !host.isConnected) {
      instance.destroy();
      return;
    }
    emulator = instance;
    lastCommand = '';
    visible = true;
    instance.controls.addEventListener('change', requestRender);
    resizeObserver = new ResizeObserver(requestRender);
    resizeObserver.observe(host);
    visibilityObserver = new IntersectionObserver(([entry]) => {
      visible = entry.isIntersecting;
      if (visible) requestRender(); else cancelFrame();
    });
    visibilityObserver.observe(host);
    document.addEventListener('visibilitychange', onVisibilityChange);
    instance.renderer.domElement.classList.add('axis-model-canvas');
    host.dataset.modelState = 'ready';
    updateOsrAxisPreview(pendingValues);
  } catch (error) {
    if (revision !== mountRevision || previewHost !== host) return;
    host.dataset.modelState = 'error';
    host.replaceChildren(Object.assign(document.createElement('span'), {
      className: 'axis-model-status axis-model-error',
      textContent: error instanceof Error ? `3D preview unavailable: ${error.message}` : '3D preview unavailable',
    }));
  }
}

export function updateOsrAxisPreview(values: Partial<Record<ModeledAxisName, number>>): void {
  pendingValues = Object.fromEntries(modeledAxisNames.map((axis) => [axis, normalize(values[axis] ?? 0.5)])) as Record<ModeledAxisName, number>;
  if (previewHost) {
    modeledAxisNames.forEach((axis) => {
      previewHost?.style.setProperty(`--axis-${axis.toLowerCase()}`, String(pendingValues[axis]));
      if (previewHost) previewHost.dataset[`axis${axis}`] = pendingValues[axis].toFixed(4);
    });
  }
  const command = tcode(pendingValues);
  if (emulator && command !== lastCommand) {
    emulator.write(command);
    lastCommand = command;
    requestRender();
  }
}

export function destroyOsrAxisPreview(): void {
  mountRevision += 1;
  cancelFrame();
  resizeObserver?.disconnect();
  visibilityObserver?.disconnect();
  resizeObserver = visibilityObserver = null;
  document.removeEventListener('visibilitychange', onVisibilityChange);
  if (emulator) {
    emulator.controls.removeEventListener('change', requestRender);
    emulator.controls.dispose();
    // renderer.dispose() does not release scene geometry, materials or shadows.
    const resources = new Set<{ dispose(): void }>();
    emulator.scene.traverse((object) => {
      if (object.geometry) resources.add(object.geometry);
      for (const material of [object.material].flat()) if (material) resources.add(material);
      if (object.shadow?.map) resources.add(object.shadow.map);
      if (object.shadow?.mapPass) resources.add(object.shadow.mapPass);
    });
    resources.forEach((resource) => resource.dispose());
  }
  try { emulator?.destroy(); } catch { }
  emulator = null;
  previewHost = null;
  visible = false;
}
