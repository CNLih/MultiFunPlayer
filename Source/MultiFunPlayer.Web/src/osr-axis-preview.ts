export const modeledAxisNames = ['L0', 'L1', 'L2', 'R0', 'R1', 'R2'] as const;
export type ModeledAxisName = typeof modeledAxisNames[number];

type Emulator = import('osr-emu').OSREmulator;

let emulator: Emulator | null = null;
let previewHost: HTMLElement | null = null;
let mountRevision = 0;
let pendingValues: Record<ModeledAxisName, number> = neutralAxisValues();

function neutralAxisValues(): Record<ModeledAxisName, number> {
  return Object.fromEntries(modeledAxisNames.map((axis) => [axis, 0.5])) as Record<ModeledAxisName, number>;
}

function normalize(value: number): number {
  return Number.isFinite(value) ? Math.max(0, Math.min(1, value)) : 0.5;
}

function tcode(values: Record<ModeledAxisName, number>): string {
  return `${modeledAxisNames.map((axis) => {
    const magnitude = Math.round(normalize(values[axis]) * 9999).toString().padStart(4, '0');
    return `${axis}${magnitude}I50`;
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
    const instance = new OSREmulator(host, { model: 'SR6' });
    if (revision !== mountRevision || previewHost !== host || !host.isConnected) {
      instance.destroy();
      return;
    }
    emulator = instance;
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
  emulator?.write(tcode(pendingValues));
}

export function destroyOsrAxisPreview(): void {
  mountRevision += 1;
  try { emulator?.destroy(); } catch { }
  emulator = null;
  previewHost = null;
}
